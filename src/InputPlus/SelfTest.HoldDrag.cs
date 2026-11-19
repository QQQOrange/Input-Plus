using System;
using System.Threading;
using InputPlus.Core.HoldDrag;
using InputPlus.Core.Settings;
using InputPlus.Modules.HoldDrag;

namespace InputPlus;

/// <summary>
/// 自检：并进来的「按住拖拽」（AltDrag 的 hooks.c）。
///
/// 分两层：
///   · 配置 / 黑名单 / 动作表这些纯函数，直接算；
///   · 拖拽本身用**真窗口 + 假光标**走一遍完整链路（按住触发键 → 按下 → 移动 → 松开），
///     假光标是为了不挪用户的鼠标，真窗口是为了走真正的 MoveWindow。
///
/// 最要紧的一条断言是「同步」：MouseMove 事件一返回，窗口就必须已经在新的位置 ——
/// 旧实现把事件塞进工作线程队列，于是松手之后窗口还要追几十毫秒前的坐标跑一段。
/// 这里不 sleep、直接 GetWindowRect，就是钉住这条。
/// </summary>
public static partial class SelfTest
{
    private static void TestHoldDrag()
    {
        var fresh = new AppSettings();
        Check("按住拖拽：默认是关的", !fresh.HoldDragEnabled);
        CheckEqual("按住拖拽：默认没存过配置", fresh.HoldDragJson, "");

        TestHoldDragConfig();
        TestHoldDragRules();
        TestHoldDragLive();
    }

    // ---------------- 配置：默认值 / 往返 / 收敛 ----------------

    private static void TestHoldDragConfig()
    {
        var cfg = AppConfig.Parse("");
        CheckEqual("按住拖拽：默认触发键是 Enter", cfg.TriggerKey, "K:0x0D");
        Check("按住拖拽：回车显示成 Enter",
            KeysUtil.DisplayName("K:0x0D").Contains("Enter", StringComparison.OrdinalIgnoreCase),
            KeysUtil.DisplayName("K:0x0D"));
        Check("按住拖拽：触发键的虚拟键码认得出来", KeysUtil.ToVirtualKey("K:0x0D") == 13);
        CheckEqual("按住拖拽：默认左键拖动窗口", cfg.ActionLeft, Actions.Move);
        CheckEqual("按住拖拽：默认右键改大小", cfg.ActionRight, Actions.Resize);
        CheckEqual("按住拖拽：默认中键也改大小", cfg.ActionMiddle, Actions.Resize);
        CheckEqual("按住拖拽：默认侧键什么都不做", cfg.ActionX1 + "/" + cfg.ActionX2, Actions.Nothing + "/" + Actions.Nothing);
        Check("按住拖拽：默认贴到窗口内侧（AltDrag.ini 的 AutoSnap=3）", cfg.AutoSnap == 3);
        Check("按住拖拽：默认吸附距离 20（AltDrag 的 SnapThreshold）", cfg.SnapThreshold == 20);
        Check("按住拖拽：默认开 Aero 贴边", cfg.AeroSnap);
        Check("按住拖拽：默认拖到别的屏就最大化（AutoRemaximize=1）", cfg.AutoRemaximize == 1);
        Check("按住拖拽：默认不抢焦点", !cfg.AutoFocus);
        Check("按住拖拽：默认每个事件都跟手（抽帧 1/1）", cfg.MoveRate == 1 && cfg.ResizeRate == 1);
        Check("按住拖拽：默认不吞触发键（回车该打还是打）", !cfg.SwallowTrigger);

        // ---- 往返：界面改的每一项都得原样存回来 ----
        cfg.TriggerKey = "K:0x20";
        cfg.SwallowTrigger = true;
        cfg.ActionLeft = Actions.Resize;
        cfg.ActionMiddle = Actions.Center;
        cfg.ActionRight = Actions.Move;
        cfg.ActionX1 = Actions.AlwaysOnTop;
        cfg.ActionX2 = Actions.Close;
        cfg.AeroSnap = false;
        cfg.AutoSnap = 2;
        cfg.SnapThreshold = 33;
        cfg.AutoRemaximize = 2;
        cfg.AutoFocus = true;
        cfg.MoveRate = 3;
        cfg.ResizeRate = 4;
        cfg.ProcessBlacklist = "wps.exe\ncode.exe";
        cfg.WindowBlacklist = "*|FooWnd";
        var back = AppConfig.Parse(cfg.Serialize());
        CheckEqual("按住拖拽：往返保住触发键", back.TriggerKey, "K:0x20");
        Check("按住拖拽：往返保住吞键开关", back.SwallowTrigger);
        CheckEqual("按住拖拽：往返保住五个鼠标动作",
            back.ActionLeft + "/" + back.ActionMiddle + "/" + back.ActionRight + "/" + back.ActionX1 + "/" + back.ActionX2,
            "Resize/Center/Move/AlwaysOnTop/Close");
        Check("按住拖拽：往返保住贴边设置", !back.AeroSnap && back.AutoSnap == 2 && back.SnapThreshold == 33);
        Check("按住拖拽：往返保住多显示器与焦点设置", back.AutoRemaximize == 2 && back.AutoFocus);
        Check("按住拖拽：往返保住抽帧倍数", back.MoveRate == 3 && back.ResizeRate == 4);
        Check("按住拖拽：往返保住程序例外", back.ProcessBlacklist == "wps.exe\ncode.exe", back.ProcessBlacklist);
        CheckEqual("按住拖拽：往返保住窗口例外", back.WindowBlacklist, "*|FooWnd");

        // ---- 越界值收敛 ----
        var wild = AppConfig.Parse("{\"AutoSnap\":9,\"SnapThreshold\":-50,\"AutoRemaximize\":7,\"MoveRate\":0,\"ResizeRate\":99,\"ActionLeft\":\"炸\",\"TriggerKey\":\"\"}");
        Check("按住拖拽：AutoSnap 9 收到 3", wild.AutoSnap == 3, wild.AutoSnap.ToString());
        Check("按住拖拽：吸附距离负数收到 0", wild.SnapThreshold == 0, wild.SnapThreshold.ToString());
        Check("按住拖拽：AutoRemaximize 7 收到 2", wild.AutoRemaximize == 2, wild.AutoRemaximize.ToString());
        Check("按住拖拽：抽帧 0 收到 1", wild.MoveRate == 1 && wild.ResizeRate == 8, wild.MoveRate + "/" + wild.ResizeRate);
        CheckEqual("按住拖拽：认不出的动作落到 Nothing", wild.ActionLeft, Actions.Nothing);
        CheckEqual("按住拖拽：空触发键回到 Enter", wild.TriggerKey, "K:0x0D");
        Check("按住拖拽：坏 JSON 也回默认值", AppConfig.Parse("{ 不是 json").AutoSnap == 3);

        // ---- 动作表：AltDrag 的八个动作 ----
        Check("按住拖拽：动作表就是 AltDrag 那八个", Actions.All.Length == 8);
        Check("按住拖拽：动作编号从 1 开始（0 留给「没有动作」）",
            Actions.ToCode(Actions.Move) == 1 && Actions.ToCode(Actions.Nothing) == 8);
        Check("按住拖拽：动作名大小写不敏感", Actions.ToCode("move") == Actions.ToCode(Actions.Move));

        // ---- 旧引擎必须彻底不在：留着就等于队列还在 ----
        var asm = typeof(SelfTest).Assembly;
        Check("按住拖拽：旧的工作线程队列已经删掉",
            asm.GetType("InputPlus.Modules.HoldDrag.DragWorker") == null
            && asm.GetType("InputPlus.Modules.HoldDrag.DragEngine") == null);
        Check("按住拖拽：旧的协调器已经删掉", asm.GetType("InputPlus.Modules.HoldDrag.Coordinator") == null);
        Check("按住拖拽：引擎直接挂在钩子上（IInputSink）",
            typeof(IInputSink).IsAssignableFrom(typeof(AltDragSink)));
        Check("按住拖拽：抽帧补帧定时器还在（hooks.c 的 MOVE_TIMER）",
            typeof(AltDragSink).GetMethod("StartMoveTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) != null);
        Check("按住拖拽：单键捕获窗在（Enter 也能选）",
            typeof(UI.Windows.TriggerKeyWindow).GetMethod("OnPreviewKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly) != null);
    }

    // ---------------- 黑名单：AltDrag 的 [Blacklist] ----------------

    private static void TestHoldDragRules()
    {
        Check("按住拖拽：进程例外按程序名匹配", AppRules.MatchesProcesses("wps.exe", "wps.exe"));
        Check("按住拖拽：进程例外带 .exe 和不带都认", AppRules.MatchesProcesses("wps", "wps.exe"));
        Check("按住拖拽：进程例外支持通配", AppRules.MatchesProcesses("*team*", "teams.exe"));
        Check("按住拖拽：进程例外空清单不拦任何程序", !AppRules.MatchesProcesses("", "chrome.exe"));
        Check("按住拖拽：进程例外命中就不接管", AppRules.MatchesProcesses("chrome.exe", "Chrome.exe"));

        Check("按住拖拽：窗口例外只要类名对得上就拦",
            AppRules.MatchesWindows("|#32770", "explorer.exe", "#32770"));
        Check("按住拖拽：窗口例外只要进程对得上也拦",
            AppRules.MatchesWindows("explorer.exe|", "explorer.exe", "CabinetWClass"));
        Check("按住拖拽：窗口例外两边都要对上",
            !AppRules.MatchesWindows("explorer.exe|#32770", "explorer.exe", "CabinetWClass"));
        Check("按住拖拽：窗口例外支持 * 任意",
            AppRules.MatchesWindows("*|TaskSwitcherWnd", "anything.exe", "TaskSwitcherWnd"));

        var cfg = AppConfig.Parse("");
        Check("按住拖拽：默认窗口例外里带着 AltDrag 那几条",
            AppRules.MatchesWindows(cfg.WindowBlacklist, "x.exe", "MultitaskingViewFrame")
            && AppRules.MatchesWindows(cfg.WindowBlacklist, "x.exe", "#32770"),
            cfg.WindowBlacklist);
        CheckEqual("按住拖拽：例外清单换行/逗号/分号都当分隔符",
            string.Join("|", AppRules.Rows("a.exe\nb.exe, c.exe; d.exe")), "a.exe|b.exe|c.exe|d.exe");
    }

    // ---------------- 真窗口 + 假光标：整条链路 ----------------

    private static void TestHoldDragLive()
    {
        nint hwnd = IntPtr.Zero;
        uint tid = 0u;
        var ready = new ManualResetEventSlim(initialState: false);
        var pump = new Thread(() =>
        {
            // 窗口必须建在一台会转消息的线程上：钩子这边同步 MoveWindow 时要跨线程发消息，
            // 那边不抽消息，写窗口的那一帧就会卡住（测试反而超时）。
            // 0x00CF0000 = WS_OVERLAPPEDWINDOW：有标题栏、有可调边框，跟普通窗口一样。
            hwnd = Native.CreateWindowExW(0u, "STATIC", "inputplus-holddrag-selftest", 0x00CF0000u,
                200, 200, 400, 300, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
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

        Func<Native.POINT?>? previousCursor = AltDragSink.CursorProbeForTest;
        AltDragSink? sink = null;
        try
        {
            if (hwnd == IntPtr.Zero)
            {
                Check("按住拖拽：自检窗口建不出来（跳过拖动行为断言）", true, "CreateWindowEx 返回 0");
                return;
            }

            // 自检进程的 DPI 缩放会影响坐标换算：钉成 1，坐标就是物理像素。
            CoordSpace.SetScaleForTest(1.0);

            Native.SetWindowPos(hwnd, IntPtr.Zero, 200, 200, 400, 300, 0x0040u);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x0011u);
            Thread.Sleep(80);
            Native.RECT start;
            Native.GetWindowRect(hwnd, out start);

            var cfg = AppConfig.Parse("");
            cfg.TriggerKey = "K:0x0D";
            cfg.SwallowTrigger = false;
            cfg.ActionLeft = Actions.Move;
            cfg.ActionRight = Actions.Resize;
            cfg.ActionMiddle = Actions.Nothing;
            cfg.AutoSnap = 0;
            cfg.AeroSnap = false;
            cfg.AutoRemaximize = 0;
            cfg.AutoFocus = false;
            cfg.MoveRate = 1;
            cfg.ResizeRate = 1;
            sink = new AltDragSink(() => cfg);

            int fx = start.Left + 200;
            int fy = start.Top + 150;
            AltDragSink.CursorProbeForTest = () => new Native.POINT(fx, fy);

            static bool Near(int a, int b) => Math.Abs(a - b) <= 2;
            static string Fmt(Native.RECT r) => r.Left + "," + r.Top + " " + r.Width + "x" + r.Height;

            // ---- 触发键 ----
            var enterDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            Check("按住拖拽：默认不吞 Enter", !sink.OnKeyboard(true, ref enterDown));
            cfg.SwallowTrigger = true;
            var enterDown2 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            Check("按住拖拽：打开「吞掉触发键」后按住 Enter 被吞掉", sink.OnKeyboard(true, ref enterDown2));
            cfg.SwallowTrigger = false;

            // ---- 移动：按下 → 跟手 → 手停就停 ----
            var down = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            Check("按住拖拽：按住 Enter + 左键按下能接管窗口", sink.OnMouse(Native.WM_LBUTTONDOWN, ref down));
            Check("按住拖拽：接管的是鼠标下的那个窗口", sink.TargetForTest == hwnd, sink.TargetForTest.ToString());
            Check("按住拖拽：左键按下后就已经在拖动", sink.IsDragging);

            fx += 40;
            fy += 30;
            var move = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref move);
            Native.GetWindowRect(hwnd, out Native.RECT moved);
            // 关键：这里不 sleep。事件一返回窗口就得在新位置 —— 没有队列，就没有追帧。
            Check("按住拖拽：鼠标事件一返回，窗口就已经在新的位置",
                Near(moved.Left, start.Left + 40) && Near(moved.Top, start.Top + 30), Fmt(moved));

            long frames = sink.FramesWritten;
            Thread.Sleep(300);
            Native.GetWindowRect(hwnd, out Native.RECT still);
            Check("按住拖拽：手停下来，窗口也停下来（不会自己再跑一段）",
                Near(still.Left, moved.Left) && Near(still.Top, moved.Top), Fmt(still));
            Check("按住拖拽：没人在偷偷补帧", sink.FramesWritten == frames, (sink.FramesWritten - frames).ToString());

            // ---- 松手 ----
            var up = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_LBUTTONUP, ref up);
            Check("按住拖拽：松开鼠标就结束拖动", !sink.IsDragging);
            fx += 200;
            fy += 200;
            var afterUp = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref afterUp);
            Thread.Sleep(80);
            Native.GetWindowRect(hwnd, out Native.RECT afterUpRect);
            Check("按住拖拽：松手之后的鼠标移动不再动窗口", Near(afterUpRect.Left, still.Left), Fmt(afterUpRect));

            // ---- 缩放：抓底边往下拖，只长高 ----
            Thread.Sleep(600);      // 越过双击判定窗口（GetDoubleClickTime 默认 500ms）
            Native.SetWindowPos(hwnd, IntPtr.Zero, 200, 200, 400, 300, 0x0014u);
            Thread.Sleep(60);
            Native.GetWindowRect(hwnd, out start);
            fx = start.Left + 200;
            fy = start.Bottom - 4;
            var rdown = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            Check("按住拖拽：右键按在底边能开始改大小", sink.OnMouse(Native.WM_RBUTTONDOWN, ref rdown) && sink.IsDragging);
            fy += 60;
            var rmove = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref rmove);
            Native.GetWindowRect(hwnd, out Native.RECT resized);
            Check("按住拖拽：抓底边往下 60，高度跟着长 60",
                Near(resized.Height, start.Height + 60) && Near(resized.Top, start.Top) && Near(resized.Width, start.Width), Fmt(resized));
            var rup = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONUP, ref rup);
            Check("按住拖拽：松开右键结束缩放", !sink.IsDragging);

            // ---- Esc：立刻收手 ----
            Thread.Sleep(600);
            Native.SetWindowPos(hwnd, IntPtr.Zero, 200, 200, 400, 300, 0x0014u);
            Thread.Sleep(60);
            Native.GetWindowRect(hwnd, out start);
            fx = start.Left + 200;
            fy = start.Top + 150;
            var edown = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_LBUTTONDOWN, ref edown);
            fx += 40;
            fy += 30;
            var emove = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref emove);
            Native.GetWindowRect(hwnd, out Native.RECT beforeEsc);
            var esc = new Native.KBDLLHOOKSTRUCT { vkCode = 0x1B, scanCode = 0x01 };
            Check("按住拖拽：Esc 不吞（别的程序还能用它）", !sink.OnKeyboard(true, ref esc));
            Check("按住拖拽：Esc 立刻结束拖动", !sink.IsDragging);
            fx += 300;
            fy += 300;
            var emove2 = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref emove2);
            Native.GetWindowRect(hwnd, out Native.RECT afterEsc);
            Check("按住拖拽：Esc 之后窗口停在原地", Near(afterEsc.Left, beforeEsc.Left) && Near(afterEsc.Top, beforeEsc.Top), Fmt(afterEsc));
            var eup = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_LBUTTONUP, ref eup);

            // ---- Shift：临时把吸附拉到最高档 ----
            Thread.Sleep(600);
            cfg.AutoSnap = 1;
            Native.SetWindowPos(hwnd, IntPtr.Zero, 200, 200, 400, 300, 0x0014u);
            Thread.Sleep(60);
            Native.GetWindowRect(hwnd, out start);
            fx = start.Left + 200;
            fy = start.Top + 150;
            var sdown = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_LBUTTONDOWN, ref sdown);
            Check("按住拖拽：起拖时吸附档位取配置", sink.SnapForTest == 1, sink.SnapForTest.ToString());
            var shiftDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x10, scanCode = 0x2A };
            sink.OnKeyboard(true, ref shiftDown);
            Check("按住拖拽：拖的时候按 Shift 拉到最高档", sink.SnapForTest == 3, sink.SnapForTest.ToString());
            var shiftUp = new Native.KBDLLHOOKSTRUCT { vkCode = 0x10, scanCode = 0x2A };
            sink.OnKeyboard(false, ref shiftUp);
            Check("按住拖拽：松开 Shift 回到配置档位", sink.SnapForTest == 1, sink.SnapForTest.ToString());
            var sup = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_LBUTTONUP, ref sup);
            Check("按住拖拽：收尾干净（不在拖动）", !sink.IsDragging);
        }
        finally
        {
            AltDragSink.CursorProbeForTest = previousCursor;
            try { sink?.Dispose(); } catch { }
            try
            {
                if (hwnd != IntPtr.Zero)
                {
                    Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, 0x0011u);
                    Native.DestroyWindow(hwnd);
                }
            }
            catch { }
            try { CoordSpace.Init(); } catch { }
        }
    }
}
