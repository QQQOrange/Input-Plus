using System;
using InputPlus.Core.HoldDrag;
using InputPlus.Core.Settings;
using InputPlus.Modules.HoldDrag;

namespace InputPlus;

/// <summary>
/// 自检：并进来的「按住拖拽」。
///
/// 引擎本体（钩子、拖窗）在这里**不启动** —— 自检跑在用户桌面上，
/// 装一个全局鼠标钩子会把用户的鼠标拖歪。所以只验到"配置这一层"：
/// 默认关、JSON 往返、越界值被收敛、关着的时候绝不装钩子。
/// 之所以强调默认关：触发键默认是 Enter，装上就等于抢走回车，
/// 用户只会觉得键盘坏了。
/// </summary>
public static partial class SelfTest
{
    private static void TestHoldDrag()
    {
        var fresh = new AppSettings();
        Check("按住拖拽：默认是关的", !fresh.HoldDragEnabled);
        CheckEqual("按住拖拽：默认没存过配置", fresh.HoldDragJson, "");

        // ---- 空串要能解析成一份能用的默认配置 ----
        var cfg = ConfigStore.Parse("");
        Check("按住拖拽：空配置解析得出默认值", cfg != null);
        if (cfg is null) cfg = new AppConfig();
        CheckEqual("按住拖拽：默认触发键是 Enter", cfg.TriggerKeyId, "K:0x0D");
        Check("按住拖拽：默认长按时长是 300ms", cfg.HoldMs == 300, cfg.HoldMs.ToString());
        CheckEqual("按住拖拽：左键默认拖", cfg.ActionLeft, Actions.Move);
        CheckEqual("按住拖拽：右键默认缩放", cfg.ActionRight, Actions.Resize);
        CheckEqual("按住拖拽：中键默认最大化", cfg.ActionMiddle, Actions.Maximize);

        // ---- JSON 往返：存下来的东西能原样读回来 ----
        cfg.TriggerKeyId = "K:0x20";
        cfg.TriggerMode = "Toggle";
        cfg.HoldMs = 450;
        cfg.FrameStyleId = "Aurora";
        cfg.FrameColorId = "Accent";
        cfg.SnapThreshold = 12;
        cfg.Blacklist = "game.exe";
        cfg.AutoRemaximizeRestore = true;
        string json = ConfigStore.Serialize(cfg);
        var back = ConfigStore.Parse(json);
        CheckEqual("按住拖拽：往返保住触发键", back.TriggerKeyId, "K:0x20");
        CheckEqual("按住拖拽：往返保住触发方式", back.TriggerMode, "Toggle");
        Check("按住拖拽：往返保住长按时长", back.HoldMs == 450, back.HoldMs.ToString());
        CheckEqual("按住拖拽：往返保住外框样式", back.FrameStyleId, "Aurora");
        CheckEqual("按住拖拽：往返保住外框颜色", back.FrameColorId, "Accent");
        Check("按住拖拽：往返保住吸附阈值", back.SnapThreshold == 12, back.SnapThreshold.ToString());
        CheckEqual("按住拖拽：往返保住黑名单", back.Blacklist, "game.exe");
        Check("按住拖拽：往返保住「重新最大化后一秒还原」", back.AutoRemaximizeRestore);

        // ---- 坏值一律收敛，不许把引擎带崩 ----
        var wild = ConfigStore.Parse("");
        wild.HoldMs = 99999;
        wild.FrameThickness = 0;
        wild.FrameOpacity = 500;
        wild.SnapThreshold = 400;
        wild.TriggerKeyId = "K:0x00";
        wild.FrameStyleId = "不存在的样式";
        wild.ActionLeft = "不存在的动作";
        var fixedUp = ConfigStore.Parse(ConfigStore.Serialize(wild));
        Check("按住拖拽：长按时长被夹回 3000", fixedUp.HoldMs == 3000, fixedUp.HoldMs.ToString());
        Check("按住拖拽：粗细被夹到最小 1", fixedUp.FrameThickness == 1, fixedUp.FrameThickness.ToString());
        Check("按住拖拽：不透明度被夹回 100", fixedUp.FrameOpacity == 100, fixedUp.FrameOpacity.ToString());
        Check("按住拖拽：吸附阈值被夹回 40", fixedUp.SnapThreshold == 40, fixedUp.SnapThreshold.ToString());
        CheckEqual("按住拖拽：无效触发键回落 Enter", fixedUp.TriggerKeyId, "K:0x0D");
        Check("按住拖拽：无效样式回落默认样式",
            string.Equals(fixedUp.FrameStyleId, FrameStyles.DefaultId, StringComparison.Ordinal), fixedUp.FrameStyleId);
        CheckEqual("按住拖拽：无效动作回落拖动", fixedUp.ActionLeft, Actions.Move);

        // ---- 外框样式只留三套：细线环、虚线环、流光边 ----
        Check("按住拖拽：外框样式只剩三套", FrameStyles.All.Length == 3, FrameStyles.All.Length.ToString());
        CheckEqual("按住拖拽：三套样式是 虚线环/细线环/流光边",
            string.Join("|", FrameStyles.All), "Dashed|Hairline|Aurora");
        Check("按住拖拽：细线环还在", FrameStyles.IsValid("Hairline"));
        Check("按住拖拽：流光边还在", FrameStyles.IsValid("Aurora"));
        Check("按住拖拽：旧样式 柔光 已经删掉", !FrameStyles.IsValid("Glow"));
        Check("按住拖拽：旧样式 霓虹 已经删掉", !FrameStyles.IsValid("Neon"));
        Check("按住拖拽：旧样式 底部条 已经删掉", !FrameStyles.IsValid("Underline"));
        var legacyStyle = ConfigStore.Parse("{\"FrameStyleId\":\"Glow\"}");
        CheckEqual("按住拖拽：老配置里的旧样式自动回落虚线环", legacyStyle.FrameStyleId, FrameStyles.DefaultId);

        // ---- 关着的时候：装配一次也不许装钩子 ----
        var settings = new AppSettings();
        using (var service = new HoldDragService(() => settings))
        {
            service.ApplySettings();
            Check("按住拖拽：关着时不会装钩子", !service.Installed);
            Check("按住拖拽：关着时也不报错", string.IsNullOrEmpty(service.LastError), service.LastError);

            var editable = service.EditableConfig();
            Check("按住拖拽：没配置时也能给出可编辑的默认值", editable != null && editable.HoldMs == 300);
            if (editable is null) editable = new AppConfig();

            editable.HoldMs = 520;
            editable.TriggerKeyId = "K:0x20";
            int persisted = 0;
            var withSave = new HoldDragService(() => settings, _ => persisted++);
            Check("按住拖拽：改参数能写回设置", withSave.ApplyEditedConfig(editable));
            withSave.Dispose();
            Check("按住拖拽：写回时通知了宿主保存", persisted == 1, persisted.ToString());

            var saved = ConfigStore.Parse(settings.HoldDragJson);
            Check("按住拖拽：写回的参数读得出来", saved.HoldMs == 520, saved.HoldMs.ToString());
            CheckEqual("按住拖拽：写回的触发键读得出来", saved.TriggerKeyId, "K:0x20");
            Check("按住拖拽：改参数不会顺手把功能打开", !settings.HoldDragEnabled);
            Check("按住拖拽：写回之后钩子仍然没装", !withSave.Installed);
        }

        // ---- 触发键不能被配成一个普通字母键（否则打字都变成拖窗） ----
        Check("按住拖拽：候选触发键都解析得出虚拟键码",
            KeysUtil.ToVirtualKey("K:0x0D") != 0 && KeysUtil.ToVirtualKey("K:0x20") != 0
            && KeysUtil.ToVirtualKey("K:LShift") != 0);
        Check("按住拖拽：Enter 显示成可读名字而不是裸 id",
            KeysUtil.DisplayName("K:0x0D").Length > 0 && KeysUtil.DisplayName("K:0x0D") != "K:0x0D");


        // ---- 触发键候选表覆盖住了要显示的名字（下拉框不能出现裸 id） ----
        Check("按住拖拽：候选触发键都有可读名字",
            KeysUtil.DisplayName("M:1").Length > 0 && KeysUtil.DisplayName("K:LShift").Length > 0);

        // ---- 黑名单/规则：两个挑程序的入口与手打共用同一个追加逻辑 ----
        CheckEqual("黑名单追加：空清单直接放进去",
            UI.Views.HoldDragView.AppendToken("", "obs64.exe"), "obs64.exe");
        CheckEqual("黑名单追加：换行分隔、保持原顺序",
            UI.Views.HoldDragView.AppendToken("obs64.exe\ngame*.exe", "code.exe"),
            "obs64.exe\ngame*.exe\ncode.exe");
        CheckEqual("黑名单追加：同一个程序不会进两次（忽略大小写）",
            UI.Views.HoldDragView.AppendToken("OBS64.exe", "obs64.exe"), "OBS64.exe");
        CheckEqual("黑名单追加：空白行会被抹掉",
            UI.Views.HoldDragView.AppendToken("  obs64.exe  \n\n", "code.exe"), "obs64.exe\ncode.exe");

        CheckEqual("按程序规则：新程序写成 程序名=行为",
            UI.Views.HoldDragView.UpsertRule("", "wps.exe", "Ignore"), "wps.exe=Ignore");
        CheckEqual("按程序规则：同名换行为，不新增一行",
            UI.Views.HoldDragView.UpsertRule("wps.exe=Ignore", "wps.exe", "MoveOnly"), "wps.exe=MoveOnly");
        CheckEqual("按程序规则：不同程序各占一行",
            UI.Views.HoldDragView.UpsertRule("wps.exe=Ignore", "code.exe", "NoFrame"),
            "wps.exe=Ignore\ncode.exe=NoFrame");
        CheckEqual("按程序规则：行为留空按完全不接管算",
            UI.Views.HoldDragView.UpsertRule("", "game.exe", ""), "game.exe=Ignore");
        // 手打用分号（引擎自己的写法）也得拆开，不能把 "; code.exe=MoveOnly" 当成行为名
        CheckEqual("按程序规则：分号分隔的清单也拆得开",
            UI.Views.HoldDragView.UpsertRule("wps.exe=Ignore; code.exe=MoveOnly", "code.exe", "NoFrame"),
            "wps.exe=Ignore\ncode.exe=NoFrame");

        // ---- 清单是配置的视图：删掉一条就等于把它从配置里拿掉 ----
        CheckEqual("程序例外：删一条不影响别的", UI.Views.HoldDragView.RemoveToken("a.exe\nb.exe", "b.exe"), "a.exe");
        CheckEqual("程序例外：删不存在的条目什么都不变",
            UI.Views.HoldDragView.RemoveToken("a.exe", "zz.exe"), "a.exe");
        CheckEqual("按程序规则：删规则按程序名匹配",
            UI.Views.HoldDragView.RemoveRule("wps.exe=Ignore; code.exe=MoveOnly", "wps.exe"), "code.exe=MoveOnly");

        // ---- 拖动不再按屏幕刷新率节流（AltDrag 那套：每个鼠标移动事件立刻落位） ----
        // 这两个配置字段和节流 API 已经删掉，留着的话就会有人"顺手"再把它接回移动路径。
        Check("按住拖拽：配置里已经没有「跟随刷新率」",
            typeof(AppConfig).GetProperty("DragRefreshMode") is null && typeof(AppConfig).GetProperty("DragRefreshHz") is null);
        Check("按住拖拽：拖动线程没有帧间隔节流",
            typeof(DragWorker).GetProperty("FrameIntervalMs") is null
            && typeof(AppConfig).GetMethod("FrameIntervalMs") is null);

        // ---- 拖拽核完全照 AltDrag：Input-Plus 自己加的两条语义（位移门槛、阈值按 DPI 缩放）已经拿掉 ----
        // 留着的话，改了 ini 里的 SnapThreshold 在 150% 屏上也不是同一个手感，跟源码对不上。
        Check("按住拖拽：配置里已经没有「拖动阈值 / 出界容差」",
            typeof(AppConfig).GetProperty("DragThreshold") is null
            && typeof(AppConfig).GetProperty("DragOutThresholdPx") is null);
        Check("按住拖拽：吸附阈值不再按 DPI 缩放",
            typeof(DragEngine).GetMethod("ScaledThreshold", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic) is null);
        Check("按住拖拽：抽帧时那个 100ms 补帧定时器还在（hooks.c 的 MOVE_TIMER）",
            typeof(DragEngine).GetMethod("StartMovePollTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) != null);
        var dflt = ConfigStore.Parse("");
        Check("按住拖拽：抽帧倍数跟 AltDrag.ini 一样（移动 2 / 缩放 5）",
            dflt.MoveRate == 2 && dflt.ResizeRate == 5, dflt.MoveRate + "/" + dflt.ResizeRate);
        Check("按住拖拽：AutoRemaximize 默认按 ini 的 1（一秒还原关着）",
            dflt.AutoRemaximize && !dflt.AutoRemaximizeRestore);

        // ---- 拖动跟手：「手停了窗口还在跑」的病根 ----
        TestHoldDragTailMath();
        TestHoldDragLiveCursor();

        TestHoldDragResizeAxes();
    }

    // ---------------- 收尾对账的算术 ----------------
    // 松手时最后那一帧写下去之后要跟窗口的真实矩形对一次，对不上就补写。
    // 这段全是整数减法，单独钉住，免得哪天"顺手优化"把符号或宽高算反。
    private static void TestHoldDragTailMath()
    {
        var want = new Native.RECT(100, 100, 400, 300);

        Check("收尾对账：严丝合缝不算偏",
            !DragEngine.TailMismatch(want, new Native.RECT(100, 100, 400, 300), out _, out _, out _, out _));

        bool off = DragEngine.TailMismatch(want, new Native.RECT(103, 96, 403, 296), out int dx, out int dy, out int dw, out int dh);
        Check("收尾对账：偏 3,-4 能带符号算出来",
            off && dx == 3 && dy == -4 && dw == 0 && dh == 0, dx + "," + dy + "," + dw + "," + dh);

        bool sized = DragEngine.TailMismatch(want, new Native.RECT(100, 100, 420, 280), out _, out _, out int dw2, out int dh2);
        Check("收尾对账：目标自己改了尺寸也算偏",
            sized && dw2 == 20 && dh2 == -20, dw2 + "," + dh2);
    }

    // ---------------- 拖动只认「此刻的光标」 ----------------
    // 用户的原话是"我手停了、鼠标都不动了，窗口还会跑一段时间，最后跑到不是我要的位置"。
    // 病根：钩子 → 工作线程 → 目标程序这条链会排队，事件里带的坐标是"过去的坐标"，
    // 把它写下去，窗口就是在追历史。AltDrag 的做法是每个事件到了现取 GetCursorPos。
    //
    // 这里用**真窗口 + 假光标**把这条规则钉死：事件里的坐标再怎么离谱，窗口也只认光标。
    // 假光标省得为了跑一次自检去挪用户的鼠标；真窗口则保证走的是真正的 SetWindowPos 链路。
    private static void TestHoldDragLiveCursor()
    {
        nint hwnd = IntPtr.Zero;
        uint tid = 0u;
        var ready = new System.Threading.ManualResetEventSlim(initialState: false);
        var pump = new System.Threading.Thread(() =>
        {
            // 窗口必须建在一台会转消息的线程上：工作线程 SetWindowPos 时要跨线程发消息，
            // 那边不抽消息就会把写窗口的那一帧卡死（测试反而会超时）。
            hwnd = Native.CreateWindowExW(0u, "STATIC", "inputplus-drag-selftest", 0u, 100, 100, 300, 200, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
            tid = Native.GetCurrentThreadId();
            ready.Set();
            Native.MSG msg;
            while (Native.GetMessageW(out msg, IntPtr.Zero, 0u, 0u) > 0)
            {
                Native.DispatchMessageW(ref msg);
            }
        });
        pump.IsBackground = true;
        pump.Start();
        ready.Wait(3000);

        Func<Native.POINT?> previous = DragEngine.CursorProbeForTest;
        DragEngine? engine = null;
        try
        {
            if (hwnd == IntPtr.Zero)
            {
                Check("按住拖拽跟手：自检窗口建不出来（跳过拖动行为断言）", true, "CreateWindowEx 返回 0");
                return;
            }
            Native.SetWindowPos(hwnd, IntPtr.Zero, 100, 100, 300, 200, 4u);

            var cfg = ConfigStore.Parse("");
            cfg.SnapToEdges = false;
            cfg.ShowDragHud = false;
            cfg.ShowActivationFrame = false;
            cfg.BringToFront = false;
            cfg.ShakeMinimizeOthers = false;
            cfg.MoveRate = 1;
            DragDiag.Init(InputPlus.Core.AppPaths.DataDir);
            engine = new DragEngine(() => cfg);

            int fx = 1000;
            int fy = 800;
            DragEngine.CursorProbeForTest = () => new Native.POINT(fx, fy);
            bool began = engine.Begin(hwnd, move: true, button: 0, pt: new Native.POINT(1000, 800));
            Check("按住拖拽跟手：能开始拖动自检窗口", began);

            // ① 事件里的坐标离光标 600px：旧实现会把窗口拽过去，现在它必须纹丝不动
            for (int i = 0; i < 8; i++)
            {
                engine.DeliverCursor(new Native.POINT(1600, 1200));
            }
            System.Threading.Thread.Sleep(180);
            Native.GetWindowRect(hwnd, out Native.RECT r1);
            Check("按住拖拽跟手：事件里的过期坐标不会把窗口带跑",
                Math.Abs(r1.Left - 100) <= 1 && Math.Abs(r1.Top - 100) <= 1, r1.Left + "," + r1.Top);

            // ② 光标真的走了 40,50 → 窗口必须正好跟 40,50（哪怕事件里给的是 0,0）
            fx = 1040;
            fy = 850;
            engine.DeliverCursor(new Native.POINT(0, 0));
            System.Threading.Thread.Sleep(180);
            Native.GetWindowRect(hwnd, out Native.RECT r2);
            Check("按住拖拽跟手：光标走 40,50，窗口跟着走 40,50",
                Math.Abs(r2.Left - 140) <= 1 && Math.Abs(r2.Top - 150) <= 1, r2.Left + "," + r2.Top);

            // ④ 一口气打 60 个移动事件：队列里始终只能有一条在途消息，
            //    最后落点必须是"最后一次光标位置"，不多不少（旧实现会积压成串，松手后再补跑一段）
            DragWorker.ResetStats();
            fx = 500;
            fy = 500;
            for (int i = 0; i < 60; i++)
            {
                fx += 7;
                fy += 5;
                engine.DeliverCursor(new Native.POINT(fx, fy));
            }
            System.Threading.Thread.Sleep(250);
            Native.GetWindowRect(hwnd, out Native.RECT r4);
            Check("按住拖拽：连打 60 个移动事件后停在最后一个光标位置，不会再多跑一段",
                Math.Abs(r4.Left - (fx - 900)) <= 1 && Math.Abs(r4.Top - (fy - 700)) <= 1, r4.Left + "," + r4.Top);
            Check("按住拖拽：连打 60 个移动事件，线程消息队列里始终只有一条在途", DragWorker.MaxDrained == 0, DragWorker.MaxDrained.ToString());
            Check("按住拖拽：从排进队列到开始写窗口不超过一帧", DragWorker.MaxLagMs <= 40, DragWorker.MaxLagMs + "ms");

            // ③ 松手：收尾那一帧之后窗口必须还停在光标对应的位置
            engine.End();
            System.Threading.Thread.Sleep(300);
            Native.GetWindowRect(hwnd, out Native.RECT r3);
            Check("按住拖拽收尾：松手后停在光标对应的位置，不再自己往前跑",
                Math.Abs(r3.Left - r4.Left) <= 1 && Math.Abs(r3.Top - r4.Top) <= 1, r3.Left + "," + r3.Top);

            bool wrote = false;
            string diagPath = DragDiag.Path;
            try
            {
                wrote = diagPath.Length > 0 && System.IO.File.Exists(diagPath)
                    && System.IO.File.ReadAllText(diagPath).Contains("收尾补写");
            }
            catch
            {
            }
            Check("按住拖拽：拖动结束会往 drag-diag.txt 记一笔明文账", wrote, diagPath);

            // ⑤ AltDrag 的 MOVE_TIMER（hooks.c:1849-1854、1999-2008）：抽帧开着的时候，
            //    如果低层鼠标钩子被系统掐掉（光标还在动、钩子不再来事件），源码靠一个 100ms 定时器补一帧。
            //    这里把 MoveRate 调成 2（每两个事件才写一次），然后只挪假光标、不喂事件：
            //    定时器不干活的话，窗口就会停在原地等下一个事件。
            engine.End();
            System.Threading.Thread.Sleep(120);
            Native.SetWindowPos(hwnd, IntPtr.Zero, 100, 100, 300, 200, 4u);
            cfg.MoveRate = 2;
            fx = 1000;
            fy = 800;
            Check("按住拖拽补帧：抽帧 2 时还能开始拖动",
                engine.Begin(hwnd, move: true, button: 0, pt: new Native.POINT(1000, 800)));
            System.Threading.Thread.Sleep(60);
            fx = 1060;
            fy = 840;
            System.Threading.Thread.Sleep(320);
            Native.GetWindowRect(hwnd, out Native.RECT r5);
            Check("按住拖拽补帧：钩子不再来事件时，100ms 定时器把窗口补到光标位置",
                Math.Abs(r5.Left - 160) <= 1 && Math.Abs(r5.Top - 140) <= 1, r5.Left + "," + r5.Top);
            engine.End();
            System.Threading.Thread.Sleep(120);

            // ⑥ AutoRemaximize=2 的一秒还原（hooks.c:1974-1997）：先解锁，再按光标在新显示器里的
            //    相对位置重算 offset，然后 SW_RESTORE。这里单独钉住"解锁 + 比例 + 真的退出了最大化"。
            Native.SetWindowPos(hwnd, IntPtr.Zero, 100, 100, 300, 200, 4u);
            Native.WINDOWPLACEMENT wp = new Native.WINDOWPLACEMENT
            {
                length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
            };
            Native.GetWindowPlacement(hwnd, ref wp);
            wp.showCmd = Native.SW_MAXIMIZE;
            Native.SetWindowPlacement(hwnd, ref wp);
            var alt = new AltState
            {
                OriginWidth = 800,
                OriginHeight = 600,
                Locked = true
            };
            Native.POINT probe = new Native.POINT(200, 150);
            Native.MONITORINFO mi = Native.GetMonitorInfoAt(probe);
            int wantOffsetX = (int)Math.Round((double)(probe.X - mi.rcMonitor.Left) / (double)mi.rcMonitor.Width * 800.0);
            bool restored = AltDragCore.RestoreAfterRemaximize(alt, hwnd, probe);
            Check("按住拖拽重最大化后还原：解锁并退出最大化", restored && !alt.Locked && !Native.IsZoomed(hwnd));
            Check("按住拖拽重最大化后还原：offset 按光标在新显示器里的比例重算",
                alt.OffsetX == wantOffsetX, alt.OffsetX + " vs " + wantOffsetX);
        }
        finally
        {
            DragEngine.CursorProbeForTest = previous;
            try
            {
                engine?.Dispose();
            }
            catch
            {
            }
            if (hwnd != IntPtr.Zero)
            {
                Native.DestroyWindow(hwnd);
            }
            if (tid != 0u)
            {
                Native.PostThreadMessageW(tid, 18u, IntPtr.Zero, IntPtr.Zero);
            }
            try
            {
                pump.Join(1000);
            }
            catch
            {
            }
            ready.Dispose();
        }
    }
    // ---------------- 临时探针：缩放的两个轴到底有没有动 ----------------
    // ---------------- 缩放：两轴都得能动 ----------------
    // AltDrag 的九宫格（hooks.c:1700-1710）在"只有一条轴落在中间带"时会把那一轴锁死：
    // 抓左边框中段上下拖，只有宽度会变。用户手上的预期是"按住触发键随便拖，两个方向都能调"，
    // 所以移植里把锁死的那一轴换成了"离得近的那条边"（上半→上边、下半→下边、左半→左边、右半→右边），
    // 两轴都落在中间带时仍然走 AltDrag 的四向对称扩张。这一段真起一个窗口拖给它看。
    private static void TestHoldDragResizeAxes()
    {
        nint hwnd = IntPtr.Zero;
        var ready = new System.Threading.ManualResetEventSlim(initialState: false);
        var pump = new System.Threading.Thread(() =>
        {
            hwnd = Native.CreateWindowExW(0u, "STATIC", "inputplus-resize-axes", 0x00CF0000u, 100, 100, 400, 300, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
            ready.Set();
            Native.MSG msg;
            while (Native.GetMessageW(out msg, IntPtr.Zero, 0u, 0u) > 0)
            {
                Native.DispatchMessageW(ref msg);
            }
        });
        pump.IsBackground = true;
        pump.Start();
        ready.Wait(3000);
        if (hwnd == IntPtr.Zero)
        {
            Check("缩放：窗口建不出来", false);
            return;
        }

        var previous = DragEngine.CursorProbeForTest;
        DragEngine engine = null!;
        try
        {
            var cfg = ConfigStore.Parse("");
            cfg.SnapToEdges = false;
            cfg.ShowDragHud = false;
            cfg.ShowActivationFrame = false;
            cfg.BringToFront = false;
            cfg.ShakeMinimizeOthers = false;
            cfg.MoveRate = 1;
            cfg.ResizeRate = 1;
            engine = new DragEngine(() => cfg);

            int fx = 0;
            int fy = 0;
            DragEngine.CursorProbeForTest = () => new Native.POINT(fx, fy);

            Native.RECT Run(int gx, int gy, int dx, int dy)
            {
                Native.SetWindowPos(hwnd, IntPtr.Zero, 100, 100, 400, 300, 4u);
                System.Threading.Thread.Sleep(60);
                fx = gx;
                fy = gy;
                engine.Begin(hwnd, move: false, button: 1, pt: new Native.POINT(gx, gy));
                for (int i = 1; i <= 6; i++)
                {
                    fx = gx + dx * i / 6;
                    fy = gy + dy * i / 6;
                    engine.DeliverCursor(new Native.POINT(fx, fy));
                    System.Threading.Thread.Sleep(20);
                }
                System.Threading.Thread.Sleep(150);
                Native.GetWindowRect(hwnd, out Native.RECT r1);
                engine.End();
                System.Threading.Thread.Sleep(120);
                return r1;
            }

            static bool Near(int a, int b) => Math.Abs(a - b) <= 2;
            static string Fmt(Native.RECT r) => r.Left + "," + r.Top + "," + r.Width + "x" + r.Height;

            // 底边中段往下拖 60：只动高，宽度和上边不许动。
            Native.RECT bottom = Run(300, 395, 0, 60);
            Check("缩放：抓底边往下拖只改高", Near(bottom.Top, 100) && Near(bottom.Height, 360) && Near(bottom.Width, 400), Fmt(bottom));

            // 左边框中段（上下拖本来是被 AltDrag 锁死的那种）：宽和高都得跟着动。
            Native.RECT leftMid = Run(105, 250, -40, 60);
            Check("缩放：抓左边框中段也能改高", Near(leftMid.Width, 440) && Near(leftMid.Height, 360), Fmt(leftMid));
            Check("缩放：抓左边框中段改高时不挪上边", Near(leftMid.Top, 100) && Near(leftMid.Left, 60), Fmt(leftMid));

            // 右边框中段往左拖：只改宽，高不许被带跑。
            Native.RECT rightMid = Run(495, 250, -60, 0);
            Check("缩放：抓右边框中段横拖只改宽", Near(rightMid.Width, 340) && Near(rightMid.Height, 300), Fmt(rightMid));

            // 上边中段往下拖 60：上边跟着走、高变小，宽度也跟着动（另一轴不再锁死）。
            Native.RECT topMid = Run(300, 105, 40, 60);
            Check("缩放：抓上边中段下拖，上边跟着走", Near(topMid.Top, 160) && Near(topMid.Height, 240), Fmt(topMid));
            Check("缩放：抓上边中段也能改宽", Near(topMid.Width, 440), Fmt(topMid));

            // 抓窗口正中：AltDrag 的四向对称扩张，原样保留。
            Native.RECT center = Run(300, 250, 40, 60);
            Check("缩放：抓正中仍是四向对称扩张",
                Near(center.Left, 60) && Near(center.Top, 40) && Near(center.Width, 480) && Near(center.Height, 420), Fmt(center));
        }
        finally
        {
            DragEngine.CursorProbeForTest = previous;
            try
            {
                engine?.Dispose();
            }
            catch
            {
            }
            try
            {
                Native.DestroyWindow(hwnd);
            }
            catch
            {
            }
        }
    }

}
