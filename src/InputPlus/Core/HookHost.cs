using InputPlus.Core.Security;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using InputPlus.Core.Input;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;

namespace InputPlus.Core;

/// <summary>
/// 钩子宿主进程（<c>--hookhost &lt;管道名&gt;</c>）。
///
/// 为什么要独立进程：
///   实测同一台机器上，**最小进程**里的低级键盘钩子 6/6 正常、跑几十秒也不掉；
///   而完整程序（带云同步、自动备份、界面渲染）里，钩子会**间歇性收不到任何按键**，
///   句柄还在、线程还活着、状态全正常。
///   根因是低级钩子是**托管回调**：进程里任何一次 GC 停顿（启动期同步/渲染最容易触发）
///   都会让回调线程停住，超过系统限制（默认 300ms）后 Windows 直接摘钩并丢弃输入。
///   这个进程什么都不做，只装钩子，就不会被别的功能拖累。
///
/// 分工：
///   · 这里负责：装钩子、跑 <see cref="HotkeyEngine"/>（决定吞不吞键）、把动作名通过管道发出去；
///   · 主程序负责：执行动作（打开 Qbar、调音量……）、界面、同步、备份。
///
/// 管道协议（每行一条，UTF-8）：
///   <c>ACT\t动作名\t参数</c> —— 请主程序执行一个动作
///   <c>READY</c>              —— 宿主已装好钩子、开始工作
///   <c>BYE</c>                —— 宿主即将退出
/// 主程序 → 宿主：<c>RELOAD</c> 让宿主重新读配置。
/// </summary>
internal static class HookHost
{
    /// <param name="relayOnly">
    /// 中继模式：不装钩子，只执行主程序发来的 <c>INJECT</c> 命令（见
    /// <see cref="App.InjectionBridge"/>）。主程序是管理员时用它 —— 钩子留在
    /// 提升的主进程里（只有它看得见提升窗口的输入），注入交给普通权限的宿主。
    /// </param>
    public static int Run(string pipeName, int parentPid = 0, bool relayOnly = false)
    {
        var log = new StringBuilder();
        void Trace(string s) { log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }

        NamedPipeClientStream? pipe = null;
        StreamWriter? writer = null;

        try
        {
            // 1) 连上主程序的管道（主程序可能刚起，最多等 5 秒）
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(5000);
            writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            Trace("管道已连接");
        }
        catch (Exception ex)
        {
            // 连不上就不干活：主程序会退回"自己装钩子"
            Trace("连接主程序失败：" + ex.Message);
            TryWriteReport(log);
            return 2;
        }

        var settings = SettingsStore.Load();
        var queue = new ActionQueue("HookHost.Actions");
        HotkeyEngine? engine = null;

        /* 管道写入不仅来自 writerThread，READY/HOOKFAIL/BYE 也在主线程写。
         * StreamWriter 不是线程安全的，所有写入统一过这把锁，避免启动握手被拼坏。 */
        object writeGate = new();
        void SendLine(string line)
        {
            lock (writeGate)
            {
                try { writer?.WriteLine(line); } catch { }
            }
        }

        /* ---- 管道写入：绝不阻塞钩子回调 ----
         *
         * ⚠ 这是"Tab 一直往后跳、层卡住不回"的真正根因（用户日志里
         * "键盘钩子漏掉了 132 个物理按键……正在重装" 就是它）：
         *
         * 以前每次按键都在**低层钩子回调里**直接 WriteLine。命名管道缓冲区
         * 满了（主程序在忙：渲染页面、同步、GC……）时这次写会阻塞，
         * 钩子回调一旦超过系统的 LowLevelHooksTimeout(~300ms)，
         * Windows 就会**静默把钩子摘掉**且不通知我们：
         *   · 之后所有按键都不再被吞 —— 按住 Tab 就是系统自己连发 Tab（焦点一直跳）；
         *   · up 事件也收不到 —— 引擎以为触发键还按着，层就"卡住"了。
         *
         * 现在改成：回调里只把消息塞进队列（O(1)，永不阻塞），
         * 由单独线程慢慢写；队列满时**只丢显示用的 KEY/TRIG**，动作不丢。
         */
        /* 显示消息（KEY/TRIG/LAYER）走有界队列，满了就丢 —— 少几条流水无所谓；
         * 动作消息（ACT）必须**一条都不丢**：那是用户按下的热键，丢了就是"按了没反应"。
         *
         * 早先两者共用一个 512 的有界队列，注释写着"动作不丢"，代码却在
         * TryAdd 失败后直接 return（droppableWhenFull=false 时），
         * 主程序一卡顿（渲染页面/同步/GC）队列满，热键就被静默吞掉。 */
        var outQueue = new System.Collections.Concurrent.BlockingCollection<string>(
            new System.Collections.Concurrent.ConcurrentQueue<string>(), 512);

        var actQueue = new System.Collections.Concurrent.BlockingCollection<string>(
            new System.Collections.Concurrent.ConcurrentQueue<string>());

        void Enqueue(string line, bool droppableWhenFull)
        {
            if (droppableWhenFull)
            {
                /* 队列满一般是因为主程序卡住了。显示消息不排队、不等待，直接丢。 */
                outQueue.TryAdd(line);
                return;
            }

            /* 动作消息：无界队列，绝不丢。 */
            actQueue.Add(line);
        }

        void Forward(string action, string arg)
        {
            /* 参数里可能有换行/制表符（typeText 支持多行模板）。协议按行传输，
             * 直接拼接会把参数截断；统一 Base64 后主进程再解码。 */
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(arg ?? ""));
            Enqueue($"ACT\t{action}\t{payload}", droppableWhenFull: false);
        }

        var writerThread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    /* 动作优先：先把 ACT 全部发出去，再发显示消息。
                     * 两者都空时阻塞 50ms，避免忙等。 */
                    if (actQueue.TryTake(out string? act))
                    {
                        SendLine(act);
                        continue;
                    }

                    if (outQueue.TryTake(out string? shown, 50))
                    {
                        SendLine(shown);
                    }
                }
            }
            catch { }
        })
        { IsBackground = true, Name = "HookHost.Writer" };
        writerThread.Start();

        engine = new HotkeyEngine(queue, () => settings, (action, arg) => { Forward(action, arg); return true; });

        /* 钩子在宿主里，但“按应用启用的层”和“只在某程序展开的片段”仍需要
         * 当前前台进程。主进程的 ForegroundWatcher 管不到宿主引擎，
         * 这里单独听一份；只取进程名，符合隐私约定。 */
        using var foreground = new ForegroundWatcher();
        void ApplyForeground(string process)
        {
            engine!.ForegroundProcess = process;
            engine.AppLayerId = AppLayerRules.Match(settings.Global.AppLayers, process);
        }
        foreground.Changed += ApplyForeground;
        if (foreground.Start()) ApplyForeground(foreground.Current);

        /* 诊断：`--trace` 只设了主进程的开关，而钩子在宿主里 ——
         * 不把开关传过来，用户开 --trace 也看不到任何"钩子收到"的日志。
         * 主程序通过环境变量传（子进程天然继承）。 */
        if (Environment.GetEnvironmentVariable("CLP_TRACE") == "1")
        {
            engine.EnableKeyTrace();
            CoreLog.Info("钩子宿主：已开启按键追踪（CLP_TRACE=1）");
        }

        /* 把按键 / 触发键状态回传给主程序：「测试」页靠它显示实时流水与当前层。
         * 这里只做显示，吞键决策全在宿主这边。 */
        engine.KeySeen += (vk, down, _)
            => Enqueue($"KEY\t{vk}\t{(down ? 1 : 0)}", droppableWhenFull: true);

        engine.TriggerHeldChanged += held
            => Enqueue($"TRIG\t{(held ? 1 : 0)}\t{engine.ActiveLayer}\t{engine.ActiveTriggerKey ?? ""}",
                       droppableWhenFull: true);

        /* 控制通道（主程序 → 宿主）：CAPTURE 1/0 —— 录键期间宿主必须停止吞键，
         * 否则用户想录 Caps/Tab 这类触发键会永远录不到。 */
        _ = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var cmd = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                    cmd.Connect(2000);
                    using var reader2 = new StreamReader(cmd, Encoding.UTF8);
                    string? cmdLine;
                    while ((cmdLine = reader2.ReadLine()) != null)
                    {
                        /* 中继模式只认 INJECT。
                         *
                         * 钩子不在这个进程里（它在提升的主进程那边，只有那个钩子
                         * 看得见提升窗口的输入），这里只负责把主程序算好的按键交给
                         * SendInput。CAPTURE / ENABLED / SUSPEND 的对象是本地 engine，
                         * 而 REHOOK 会真的去 SetWindowsHookEx —— 一旦装上，同一个按键
                         * 就会被两个钩子各处理一次（重复触发、双击）。直接忽略。 */
                        if (relayOnly
                            && !cmdLine.StartsWith(App.InjectionBridge.Prefix, StringComparison.Ordinal))
                        {
                            Trace("中继模式：忽略控制命令 " + cmdLine);
                            continue;
                        }

                        if (cmdLine.StartsWith("CAPTURE", StringComparison.Ordinal))
                        {
                            engine!.CaptureMode = cmdLine.EndsWith("1", StringComparison.Ordinal);
                        }
                        else if (cmdLine.StartsWith("SWALLOW", StringComparison.Ordinal))
                        {
                            /* 主程序判定"钩子已经吞不住键"：宿主立刻停止演层，
                             * 按键全部原样放行，并把残留的触发键会话清掉。
                             * 这样用户的键盘至少是正常的（Tab 就是 Tab）。 */
                            bool on = cmdLine.Trim().EndsWith("1", StringComparison.Ordinal);
                            engine!.SwallowUnavailable = !on;
                            if (!on) engine.ResetForRecovery("主程序判定钩子失效");
                            CoreLog.Info($"钩子宿主：吞键{(on ? "恢复" : "暂停")}（SWALLOW {(on ? 1 : 0)}）");
                        }
                        else if (cmdLine.StartsWith("REHOOK", StringComparison.Ordinal))
                        {
                            /* 主程序发现"物理按键漏了"时让宿主重装。
                             * 以前主程序自己 RestartHook() —— 可钩子根本不在它那儿，
                             * 于是日志里一直重装、用户那边一直没恢复。 */
                            try { engine!.RestartHook(); } catch { }
                            CoreLog.Info("钩子宿主：收到 REHOOK，已重装键盘钩子");
                        }
                        else if (cmdLine.StartsWith("ENABLED", StringComparison.Ordinal))
                        {
                            bool on = cmdLine.Trim().EndsWith("1", StringComparison.Ordinal);
                            engine!.Enabled = on;
                            CoreLog.Info($"钩子宿主：引擎{(on ? "启用" : "暂停")}（ENABLED {(on ? 1 : 0)}）");
                        }
                        else if (cmdLine.StartsWith("SUSPEND", StringComparison.Ordinal))
                        {
                            bool on = cmdLine.Trim().EndsWith("1", StringComparison.Ordinal);
                            engine!.Suspended = on;
                            CoreLog.Info($"钩子宿主：引擎{(on ? "挂起（录制中）" : "恢复")}（SUSPEND {(on ? 1 : 0)}）");
                        }
                        else if (cmdLine.StartsWith(App.InjectionBridge.Prefix, StringComparison.Ordinal))
                        {
                            /* 主程序是管理员：它算出来的按键由我们代发。
                             * 我们和被测程序同样在 medium IL，UIPI 不会拦。 */
                            int applied = App.InjectionBridge.ApplyFromWire(cmdLine);
                            if (applied == 0)
                                CoreLog.Warn("钩子宿主：INJECT 命令没有可执行的输入（格式无法识别）");
                        }
                    }
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "HookHost.Ctl" };

        /* 配置改动靠**文件修改时间**发现：主程序每次保存都会重写 settings.json，
         * 这里每秒看一眼有没有变，变了就重读。比再开一条控制管道简单也更稳。 */
        DateTime settingsStamp = SafeStamp();
        long lastReloadLog = 0;
        int lastLayer = -1;

        /* ⚠ 重读要节流。
         *
         * 主程序保存设置是 400ms 防抖 —— 用户在参数框里打字时，就是每 400ms
         * 重写一次 settings.json。而 SettingsStore.Load() 是**完整**的解密 +
         * JSON 反序列化 + Normalize + 内置引擎迁移，全量跑一遍要几十毫秒。
         * 以前这里是"一有变化就重读"，等于打字期间宿主每秒跑两三次全量加载，
         * 白烧 CPU 和磁盘读，还会和主进程抢 IO。
         *
         * 700ms 只会让"改了设置立刻生效"晚一点点（用户感知不到），
         * 但把连续保存合并成一次重读。 */
        long lastReloadAt = Environment.TickCount64;
        const int ReloadMinIntervalMs = 700;

        Timer? holdTimer = null;

        if (relayOnly)
        {
            /* 中继模式：钩子**不在这里**（它在提升的主进程里，只有那个钩子
             * 看得见提升窗口的输入），本进程只负责把主程序发来的注入事件
             * 交给 SendInput —— 我们跑在普通权限，所以这些键对浏览器、
             * 编辑器之类的普通程序完全合法。
             *
             * 握手先说清自己的身份：HELLO <是否提升> <pid>。主程序据此判断
             * 中继是否真的落在了 medium IL，以及要不要把 Kill 的目标接上。 */
            SendLine($"HELLO	{(ElevationHelper.IsElevated ? 1 : 0)}	{Environment.ProcessId}");
            SendLine("READY");
            Trace("中继模式：不装钩子，等待主程序的 INJECT 命令");
            CoreLog.Info($"钩子宿主：中继模式已就绪（PID={Environment.ProcessId}，"
                         + $"{(ElevationHelper.IsElevated ? "提升" : "普通权限")}）");
        }
        else
        {
            engine.Start();

            /* 宿主没有 WPF 消息循环，引擎里的 DispatcherTimer 不会跑 ——
             * 必须自己驱动长按判定，否则「按住触发键不放」永远进不了层。 */
            holdTimer = new Timer(_ =>
            {
                try { engine.TickHold(); } catch { }
            }, null, 20, 20);

            /* 只有钩子真的装上才发 READY。早先把 READY 固定发在主程序会据此
             * 认定宿主已接管，HOOKFAIL 随后到达也来不及回退，热键会整体静默失效。 */
            if (!engine.IsHookAlive)
            {
                SendLine("HOOKFAIL");
                Trace("钩子未生效，已通知主程序并退出");
                CoreLog.Warn("钩子宿主：SetWindowsHookEx 失败，主程序将改用自身钩子");
                engine.Dispose();
                queue.Dispose();
                TryWriteReport(log);
                return 3;
            }

            SendLine("READY");
            Trace("引擎已启动，钩子正常");
        }

        // 2) 主循环：配置文件变了就重读
        while (true)
        {
            Thread.Sleep(100);

            var stamp = SafeStamp();
            /* 中继模式既不装钩子也不吞键，配置对它没有意义 —— 每 700ms 跑一次
             * 完整解密+反序列化纯属白烧 IO，直接跳过。 */
            if (!relayOnly && stamp != settingsStamp && stamp != default
                && Environment.TickCount64 - lastReloadAt >= ReloadMinIntervalMs)
            {
                settingsStamp = stamp;
                lastReloadAt = Environment.TickCount64;
                try
                {
                    settings = SettingsStore.Load();
                    ApplyForeground(foreground.Current);
                    Trace("已重读配置");
                    if (Environment.TickCount64 - lastReloadLog > 5000)
                    {
                        lastReloadLog = Environment.TickCount64;
                        CoreLog.Info("钩子宿主：已重读配置（设置文件有更新）");
                    }
                }
                catch { /* 配置读不了就继续用旧的 */ }
            }

            // 层变化：每秒比对一次，变了就回传（界面靠它显示"当前层"）
            /* 中继模式必须跳过：它没有钩子，engine.ActiveLayer 恒为 0，
             * 回传一条 LAYER 0 会被主程序当成"当前层=0"，把用户刚切的层顶掉。 */
            if (!relayOnly)
            {
                int layerNow = engine.ActiveLayer;
                if (layerNow != lastLayer)
                {
                    lastLayer = layerNow;
                    Enqueue($"LAYER\t{layerNow}", droppableWhenFull: true);
                }
            }

            /* 主程序没了就自尽，避免留孤儿进程占着钩子。
             *
             * 不能只看 pipe.IsConnected —— NamedPipeClientStream 的这个属性
             * 在对方被杀掉之后仍会返回 true（实测），于是宿主会一直活着、
             * 和下一次启动的主程序抢同一个键盘钩子。改成直接看父进程是否还在。 */
            if (parentPid > 0)
            {
                try
                {
                    if (Process.GetProcessById(parentPid).HasExited) break;
                }
                catch { break; }        // 进程不存在 = 已退出
            }
        }

        SendLine("BYE");
        try { holdTimer?.Dispose(); } catch { }
        engine.Dispose();
        queue.Dispose();
        TryWriteReport(log);
        return 0;
    }

    /// <summary>设置文件的最后修改时间（读不到就返回 default）。</summary>
    private static DateTime SafeStamp()
    {
        try
        {
            string path = AppPaths.SettingsFile;
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        }
        catch { return default; }
    }

    /// <summary>把宿主的运行情况写一份到 logs\hookhost.log，便于事后排查。</summary>
    private static void TryWriteReport(StringBuilder log)
    {
        try
        {
            string path = Path.Combine(AppPaths.DataDir, "logs", "hookhost.log");
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            SecureFile.AppendLine(path, log.ToString());
        }
        catch { }
    }
}
