using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using InputPlus.Core.Settings;
using InputPlus.Modules.HoldDrag;
using HdNative = InputPlus.Modules.HoldDrag.Native;

namespace InputPlus.Core;

/// <summary>
/// `--drag-e2e [结果文件] [--live]`：端到端验证「按住拖拽」。
///
/// 为什么需要它：这条功能的失败点是「按住触发键点鼠标没反应」，而那正是自检覆盖不到的地方
/// —— 现有自检用 TriggerProbeForTest 假装触发键按着，从没跑过「真钩子 + 真按键 + 真鼠标 +
/// 别的程序的窗口」这一整条链，于是出现「自检 6092 项全过，用户一按什么都不动」。
///
/// 这个模式做的事：
///   ① 起一个别的程序的窗口（记事本 / 画图）当靶子，摆到固定位置；
///   ② 按配置装真钩子（HookEngine + AltDragSink）；
///   ③ SendInput 按下触发键 → 等过长按阈值 → 在标题栏按下左键 → 拖 80x48 → 抬起 → 松开触发键；
///   ④ 比较窗口矩形，把每一步的判定写进结果文件。
///
/// --live 时用解密后的真实设置（HoldDragEnabled / HoldDragJson）跑一遍，于是
/// 「是不是根本没开」「触发键被改成别的」「设备范围把这块键盘挡掉了」三种静默失败都能一眼看出来。
/// </summary>
internal static partial class DragE2E
{
    private const int VkReturn = 0x0D;
    private const uint SwpShowWindow = 0x0040u;

    public static int Run(string? outFile, bool live)
    {
        var sb = new StringBuilder();
        int code = 2;
        Process? target = null;
        HookEngine? hooks = null;
        AltDragSink? sink = null;
        string targetName = "";
        try
        {
            InputPlus.Modules.HoldDrag.Log.Init(AppPaths.DataDir);
            InputPlus.Modules.HoldDrag.Log.Verbose = true;

            AppConfig cfg;
            if (live)
            {
                var settings = SettingsStore.Load();
                sb.AppendLine("实时设置：HoldDragEnabled=" + settings.HoldDragEnabled);
                cfg = AppConfig.Parse(settings.HoldDragJson);
            }
            else
            {
                sb.AppendLine("实时设置：（未指定 --live，用出厂默认配置）");
                cfg = new AppConfig();
            }
            cfg.Normalize();
            sb.AppendLine("配置：" + Describe(cfg));
            sb.AppendLine();

            // 首选靶子：本程序自己的 --drag-target 窗口进程。它是非提权、有标题栏、可控置顶的
            // 普通窗口 —— 前面几轮踩过的坑（提权窗口被 UIPI 挡住、商店应用拿不到主窗口句柄）
            // 全绕开了。只有它起不来时才退回系统程序。
            nint hwnd = LaunchSelfTarget(out target, out targetName);
            if (target == null)
            {
                target = LaunchTarget(out targetName);
                if (target == null)
                {
                    sb.AppendLine("结果：失败 —— 没能起靶子进程（自建靶子窗口与系统程序都没起来）");
                    return Finish(sb, outFile, 2);
                }
                hwnd = WaitForWindow(target, 4000);
            }
            if (hwnd == IntPtr.Zero)
            {
                sb.AppendLine("结果：失败 —— " + targetName + " 起来了但没有主窗口句柄");
                return Finish(sb, outFile, 2);
            }
            // regedit 起来是最大化：不先还原，SetWindowPos 不生效（看到的还是全屏矩形）。
            HdNative.ShowWindow(hwnd, 9);
            HdNative.SetWindowPos(hwnd, IntPtr.Zero, 260, 180, 640, 420, SwpShowWindow);
            ForceForeground(hwnd);
            Pump(500);

            if (!HdNative.GetWindowRect(hwnd, out var before))
            {
                sb.AppendLine("结果：失败 —— GetWindowRect 取不到靶子窗口位置");
                return Finish(sb, outFile, 2);
            }
            sb.AppendLine("靶子：" + targetName + " hwnd=0x" + hwnd.ToString("X") + " 矩形=" + Rect(before.Left, before.Top, before.Right, before.Bottom) +
                          " 前台=" + (HdNative.GetForegroundWindow() == hwnd));

            // 鼠标钩子是按需装的（altdrag 同款）：必须把请求转给 HookEngine，
            // 否则 e2e 里的注入点击永远进不来，测出来的就是假失败。
            sink = new AltDragSink(() => cfg, wanted => hooks?.SetMouseHookWanted(wanted));
            hooks = new HookEngine(sink);
            bool installed = hooks.Install();
            sb.AppendLine("钩子：Install=" + installed + " 键盘=" + hooks.KeyboardInstalled + " 鼠标(按需)=" + hooks.MouseInstalled +
                          (hooks.LastError != 0 ? " 错误码=" + hooks.LastError : ""));
            if (!installed)
            {
                sb.AppendLine("结果：失败 —— 钩子没装上（安全软件拦下了？）");
                return Finish(sb, outFile, 2);
            }
            Pump(200);

            /* 关键：整个流程必须一边等一边抽消息。
               低级钩子的回调是系统"发"给装钩子那个线程的：线程一睡，回调进不来，
               按键就像没发生（真实程序里界面线程一直在抽消息，所以只有这里会假失败）。 */
            int hold = Math.Max(0, cfg.TriggerHoldMs);

            /* 试验台的环境保真度：这台机器上常有真实窗口（包括 InputPlus 自己的窗口）压在靶子上方，
               于是注入的点击落在别人身上 —— 靶子当然不动，看起来就像"按住拖拽失效"。
               所以先把靶子顶成 TOPMOST，再逐点确认"光标正下方就是这个窗口"，确认不了就直接判
               「试验台不可用（跳过）」，绝不拿别人的窗口当证据。 */
            HdNative.SetWindowPos(hwnd, HdNative.HWND_TOPMOST, 0, 0, 0, 0,
                HdNative.SWP_NOMOVE | HdNative.SWP_NOSIZE | HdNative.SWP_SHOWWINDOW);
            Pump(150);
            if (!HdNative.GetWindowRect(hwnd, out before))
            {
                sb.AppendLine("结果：失败 —— 顶到最前之后取不到靶子窗口位置");
                return Finish(sb, outFile, 2);
            }

            int clickX = before.Left + 180;
            int clickY = before.Top + 12;
            nint under = nint.Zero;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                HdNative.SetCursorPos(clickX, clickY);
                Pump(50);
                under = HdNative.WindowFromPoint(new HdNative.POINT { X = clickX, Y = clickY });
                if (under != nint.Zero)
                {
                    under = HdNative.GetAncestor(under, HdNative.GA_ROOT);
                }
                if (under == hwnd)
                {
                    break;
                }
                clickY += 14;
                clickX += 12;
            }
            sb.AppendLine("步骤：光标下窗口 = 0x" + under.ToString("X") + "（靶子 0x" + hwnd.ToString("X") + "）"
                          + " 类名=" + AppRules.ClassNameOf(under)
                          + " 标题=" + AppRules.TitleOf(under)
                          + " 顶层=" + (((HdNative.GetWindowLong(under, HdNative.GWL_EXSTYLE) & HdNative.WS_EX_TOPMOST) != 0) ? "是" : "否")
                          + " 点击点=" + clickX + "," + clickY);
            if (under != hwnd)
            {
                HdNative.SetWindowPos(hwnd, new nint(-2), 0, 0, 0, 0,
                    HdNative.SWP_NOMOVE | HdNative.SWP_NOSIZE | HdNative.SWP_SHOWWINDOW);
                sb.AppendLine("结果：跳过 —— 试验台环境不可用：光标下方不是靶子窗口（靶子被别的窗口盖住了），"
                              + "这种情况窗口不动不能算功能失效。请在干净的桌面上重跑。");
                return Finish(sb, outFile, 3);
            }
            Key(VkReturn, true);
            {
                var samples = new System.Text.StringBuilder();
                for (int i = 0; i < 12; i++)
                {
                    Pump(20);
                    samples.Append(KeysUtil.IsPhysicallyDown(cfg.TriggerKey) ? "1" : "0");
                }
                sb.AppendLine("步骤：触发键物理状态采样 240ms = " + samples);
            }
            sb.AppendLine("步骤：按下 " + KeysUtil.DisplayName(cfg.TriggerKey) + "，等待 " + (hold + 140) + " ms");
            Pump(hold + 140);
            sb.AppendLine("步骤：触发键按住的判定 = " + sink.TriggerHeld + "（期望 True）");

            // 真机上用户可能一直在动鼠标：按下之前再确认一次光标还在靶子上。
            HdNative.SetCursorPos(clickX, clickY);
            Pump(40);
            nint atPress = HdNative.WindowFromPoint(new HdNative.POINT { X = clickX, Y = clickY });
            if (atPress != nint.Zero)
            {
                atPress = HdNative.GetAncestor(atPress, HdNative.GA_ROOT);
            }
            sb.AppendLine("步骤：按下瞬间光标下窗口 = 0x" + atPress.ToString("X"));
            Mouse(HdNative.MOUSEEVENTF_LEFTDOWN);
            Pump(40);
            for (int i = 1; i <= 8; i++)
            {
                HdNative.SetCursorPos(clickX + i * 10, clickY + i * 6);
                Pump(16);
            }
            Pump(80);
            Mouse(HdNative.MOUSEEVENTF_LEFTUP);
            Pump(80);
            Key(VkReturn, false);
            Pump(250);

            if (!HdNative.GetWindowRect(hwnd, out var after))
            {
                sb.AppendLine("结果：失败 —— 拖动后取不到窗口位置");
                return Finish(sb, outFile, 2);
            }

            int dx = after.Left - before.Left;
            int dy = after.Top - before.Top;
            sb.AppendLine("拖动前=" + Rect(before.Left, before.Top, before.Right, before.Bottom) + " 拖动后=" + Rect(after.Left, after.Top, after.Right, after.Bottom) + " 位移=(" + dx + "," + dy + ") 期望约(80,48)");
            if (Injections.Length > 0)
            {
                sb.AppendLine("注入异常：" + Injections);
            }
            bool moved = Math.Abs(dx - 80) <= 12 && Math.Abs(dy - 48) <= 12;
            sb.AppendLine(moved ? "结果：通过 —— 按住触发键拖窗口，窗口跟着走了"
                                : "结果：失败 —— 窗口没动（按住拖拽没有接管这次拖动）");
            code = moved ? 0 : 1;

            /* 上面只覆盖了「左键拖拽移动」一条路径。右键拖拽调整大小、以及"短按不许误触发"
               这两类行为从来没被端到端跑过 —— 而它们正是 v1.3.1 返工的重灾区。
               额外场景放在 DragE2E.Scenarios.cs 里，每个场景自己写结论。 */
            int extra = RunExtraScenarios(sb, hwnd, cfg, sink, hold);
            if (extra == 1 || extra == 2)
            {
                code = code == 0 ? extra : code;
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("结果：异常 —— " + ex.Message);
            sb.AppendLine(ex.ToString());
            code = 2;
        }
        finally
        {
            try { hooks?.Uninstall(); } catch { }
            try { hooks?.Dispose(); } catch { }
            try { sink?.Dispose(); } catch { }
            try { if (target != null && !target.HasExited) target.Kill(); } catch { }
        }

        return Finish(sb, outFile, code);
    }

    private static int Finish(StringBuilder sb, string? outFile, int code)
    {
        try
        {
            string path = InputPlus.Modules.HoldDrag.Log.Path;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                string[] lines = File.ReadAllLines(path);
                int from = Math.Max(0, lines.Length - 80);
                sb.AppendLine();
                sb.AppendLine("===== holddrag.log（最后 " + (lines.Length - from) + " 行）=====");
                for (int i = from; i < lines.Length; i++) sb.AppendLine(lines[i]);
            }
        }
        catch { }

        sb.AppendLine();
        sb.AppendLine("退出码：" + code);
        string text = sb.ToString();
        try
        {
            if (!string.IsNullOrEmpty(outFile))
            {
                string? dir = Path.GetDirectoryName(outFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outFile!, text, new UTF8Encoding(false));
            }
        }
        catch { }
        return code;
    }

    private static string Describe(AppConfig cfg)
    {
        return "触发键=" + cfg.TriggerKey + "（" + KeysUtil.DisplayName(cfg.TriggerKey) + "）" +
               " 长按=" + cfg.TriggerHoldMs + "ms" +
               " 设备=" + AppConfig.DeviceLabel(cfg.Device) +
               " 吞触发键=" + cfg.SwallowTrigger +
               " 左=" + cfg.ActionLeft + " 右=" + cfg.ActionRight + " 中=" + cfg.ActionMiddle +
               " X1=" + cfg.ActionX1 + " X2=" + cfg.ActionX2 +
               " 抽帧=" + cfg.MoveRate + "/" + cfg.ResizeRate;
    }

    private static string Rect(int left, int top, int right, int bottom)
        => "(" + left + "," + top + "," + right + "," + bottom + ")";

    /// <summary>
    /// 拉起自己的 --drag-target 子进程，等它把窗口句柄写进文件。拿不到就返回 0（调用方退回系统程序）。
    /// </summary>
    private static nint LaunchSelfTarget(out Process? proc, out string name)
    {
        proc = null;
        name = "InputPlus 靶子窗口";
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0)
        {
            return IntPtr.Zero;
        }
        string file = Path.Combine(Path.GetTempPath(), "holddrag-target-" + Environment.ProcessId + ".txt");
        try { File.Delete(file); } catch { }
        try { File.Delete(file + ".stop"); } catch { }
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--drag-target");
            psi.ArgumentList.Add(file);
            proc = Process.Start(psi);
        }
        catch
        {
            proc = null;
            return IntPtr.Zero;
        }
        if (proc == null)
        {
            return IntPtr.Zero;
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000)
        {
            try
            {
                if (File.Exists(file))
                {
                    string text = File.ReadAllText(file).Trim();
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        text = text.Substring(2);
                    }
                    if (long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out long value) && value != 0)
                    {
                        return new nint(value);
                    }
                }
            }
            catch { }
            Thread.Sleep(40);
        }
        try { proc.Kill(); } catch { }
        proc = null;
        return IntPtr.Zero;
    }

    private static Process? LaunchTarget(out string name)
    {
        // Win11 的记事本/画图已经变成商店应用：启动器进程会立刻退出、主窗口落在另一个
        // 进程上，Process.MainWindowHandle 永远是 0。靶子改用货真价实的 Win32 程序：
        // 注册表编辑器（一定有）、控制台窗口、最后才是记事本。
        // 桌面干净度考虑：regedit 要提权（UIPI 会把 SetWindowPos/SetForegroundWindow 静默挡掉，
        // 于是靶子永远被压在别人下面，试验台直接判不出来）。优先挑不提权、窗口不最大化的记事本。
        foreach (string candidate in new[] { "notepad.exe", "cmd.exe", "regedit.exe" })
        {
            name = candidate;
            try
            {
                Process? proc = Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = true });
                if (proc == null) continue;
                if (WaitForWindow(proc, 6000) != IntPtr.Zero) return proc;
                try { proc.Kill(); } catch { }
            }
            catch { }
        }
        name = "";
        return null;
    }

    private static nint WaitForWindow(Process proc, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                proc.Refresh();
                if (proc.MainWindowHandle != IntPtr.Zero) return proc.MainWindowHandle;
            }
            catch { }
            Thread.Sleep(60);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 一边等一边抽消息：低级钩子的回调是"发给"装钩子那个线程的，
    /// 线程 Thread.Sleep 时回调进不来 —— 按键、鼠标全都像没发生。
    /// </summary>
    private static void Pump(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 把靶子拉到前台。SetForegroundWindow 有"前台锁"：从后台进程直接调会被忽略，
    /// 于是按键全打进别的窗口、标题栏点击也落空。挂到当前前台线程上再调就绕过去了。
    /// </summary>
    private static void ForceForeground(nint hwnd)
    {
        nint fg = HdNative.GetForegroundWindow();
        uint fgPid;
        uint fgTid = fg == nint.Zero ? 0u : GetWindowThreadProcessId(fg, out fgPid);
        uint me = GetCurrentThreadId();
        bool attached = fgTid != 0 && fgTid != me && AttachThreadInput(me, fgTid, true);
        try
        {
            HdNative.BringWindowToTop(hwnd);
            HdNative.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(me, fgTid, false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>注入回执：SendInput 静默失败（UIPI 拦注入）时，光看"窗口没动"分不清是实现坏了还是没发出去。</summary>
    private static readonly StringBuilder Injections = new StringBuilder();

    private static void Key(int vk, bool down)
    {
        var input = new HdNative.INPUT { type = HdNative.INPUT_KEYBOARD };
        input.u.ki = new HdNative.KEYBDINPUT
        {
            wVk = (ushort)vk,
            dwFlags = down ? 0u : HdNative.KEYEVENTF_KEYUP,
        };
        uint sent = HdNative.SendInput(1u, new[] { input }, Marshal.SizeOf(typeof(HdNative.INPUT)));
        Record("key vk=0x" + vk.ToString("X") + (down ? " down" : " up"), sent);
    }

    private static void Mouse(uint flag)
    {
        var input = new HdNative.INPUT { type = HdNative.INPUT_MOUSE };
        input.u.mi = new HdNative.MOUSEINPUT { dwFlags = flag };
        uint sent = HdNative.SendInput(1u, new[] { input }, Marshal.SizeOf(typeof(HdNative.INPUT)));
        Record("mouse flag=0x" + flag.ToString("X"), sent);
    }

    private static void Record(string what, uint sent)
    {
        if (sent == 0)
        {
            Injections.Append(what).Append(" 注入失败 err=").Append(Marshal.GetLastWin32Error()).AppendLine();
        }
    }
}
