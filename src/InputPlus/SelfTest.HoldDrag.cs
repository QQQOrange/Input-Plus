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
/// 最要紧的一条断言是「没有队列、没有写线程」：MouseMove 事件一返回，窗口就得已经在
/// 新位置上 —— 几何当场算、MoveWindow 当场写（hooks.c:927 就是这一句）。
/// （v1.3.1 曾把写窗口挪到独立写入线程，窗口从此永远落后一到两帧，用户说"不跟手"；
/// 现在整条链路回到 altdrag 的同步语义，TestHoldDragSync 专门钉这一点。）
/// </summary>
public static partial class SelfTest
{
    private static void TestHoldDrag()
    {
        // 本组自检的靶子都是本进程自己造的窗口，而运行时自家窗口一律不拖（见 AltDragSink）。
        bool previousOwn = AltDragSink.DragOwnWindowForTest;
        AltDragSink.DragOwnWindowForTest = true;
        // 全链路已经是同步写了（hooks.c:927），这一组的几何断言"OnMouse 之后立刻 GetWindowRect
        // 比对"才成立。InlineWriteForTest 现在只做一件事：松手那一帧别拿假光标补写。
        // 同步语义本身由 TestHoldDragSync 单独钉（含抽帧与 MOVE_TIMER 补帧）。
        bool previousInline = AltDragSink.InlineWriteForTest;
        AltDragSink.InlineWriteForTest = true;
        try
        {
            TestHoldDragCore();
        }
        finally
        {
            AltDragSink.DragOwnWindowForTest = previousOwn;
            AltDragSink.InlineWriteForTest = previousInline;
        }
    }

    /// <summary>
    /// 「按住拖拽也要能在 Input Plus 自己的窗口上用」。
    ///
    /// 但只放行**登记过的主窗口**：附加菜单 / OSD / Toast 这些自家浮层铺在屏幕上，
    /// 被误拖走会让界面乱掉（历史事故：主窗口被拖到 -426,0）。
    /// </summary>
    private static void TestHoldDragOwnWindow()
    {
        Core.HoldDrag.OwnWindowRegistry.ClearForTest();

        IntPtr main = new(0x1234);
        IntPtr menu = new(0x5678);

        Check("拖拽白名单：没登记就不是可拖窗口",
            !Core.HoldDrag.OwnWindowRegistry.IsAllowed(main));

        Core.HoldDrag.OwnWindowRegistry.Register(main);
        Check("拖拽白名单：登记后允许拖本程序窗口",
            Core.HoldDrag.OwnWindowRegistry.IsAllowed(main));
        Check("拖拽白名单：没登记的自家浮层仍然跳过",
            !Core.HoldDrag.OwnWindowRegistry.IsAllowed(menu));

        Core.HoldDrag.OwnWindowRegistry.Unregister(main);
        Check("拖拽白名单：注销后不再允许",
            !Core.HoldDrag.OwnWindowRegistry.IsAllowed(main));
        Check("拖拽白名单：空句柄永远不允许",
            !Core.HoldDrag.OwnWindowRegistry.IsAllowed(IntPtr.Zero));

        Core.HoldDrag.OwnWindowRegistry.ClearForTest();
    }

    private static void TestHoldDragCore()
    {
        var fresh = new AppSettings();
        Check("按住拖拽：默认是关的", !fresh.HoldDragEnabled);
        CheckEqual("按住拖拽：默认没存过配置", fresh.HoldDragJson, "");

        /* WM_SIZING 的九宫格映射：中心缩放这一格以前返回 0（等于"不问了"），
         * AltSnap 的 map[2][2] 给的是 BOTTOMRIGHT + 事后回正中心（hooks.c:1429/1454）。
         * 这里把整张表钉住，免得以后有人把"中心"那一格又改回 0。 */
        CheckEqual("按住拖拽：WM_SIZING 九宫格-左上", AltDragCore.WmszFlag(DragEdge.Left, DragEdgeY.Top), Native.WMSZ_TOPLEFT);
        CheckEqual("按住拖拽：WM_SIZING 九宫格-上", AltDragCore.WmszFlag(DragEdge.Center, DragEdgeY.Top), Native.WMSZ_TOP);
        CheckEqual("按住拖拽：WM_SIZING 九宫格-右上", AltDragCore.WmszFlag(DragEdge.Right, DragEdgeY.Top), Native.WMSZ_TOPRIGHT);
        CheckEqual("按住拖拽：WM_SIZING 九宫格-左", AltDragCore.WmszFlag(DragEdge.Left, DragEdgeY.Center), Native.WMSZ_LEFT);
        CheckEqual("按住拖拽：WM_SIZING 九宫格-右", AltDragCore.WmszFlag(DragEdge.Right, DragEdgeY.Center), Native.WMSZ_RIGHT);
        CheckEqual("按住拖拽：WM_SIZING 九宫格-左下", AltDragCore.WmszFlag(DragEdge.Left, DragEdgeY.Bottom), Native.WMSZ_BOTTOMLEFT);
        CheckEqual("按住拖拽：WM_SIZING 九宫格-下", AltDragCore.WmszFlag(DragEdge.Center, DragEdgeY.Bottom), Native.WMSZ_BOTTOM);
        CheckEqual("按住拖拽：WM_SIZING 九宫格-右下", AltDragCore.WmszFlag(DragEdge.Right, DragEdgeY.Bottom), Native.WMSZ_BOTTOMRIGHT);
        CheckEqual("按住拖拽：WM_SIZING 九宫格-中心（AltSnap 用 BOTTOMRIGHT 问一次）",
            AltDragCore.WmszFlag(DragEdge.Center, DragEdgeY.Center), Native.WMSZ_BOTTOMRIGHT);

        TestHoldDragConfig();
        TestResizeEdges();
        TestHoldDragRules();
        TestHoldDragLive();
        TestHoldDragSync();
        TestTriggerHold();
        TestHoldDragPause();
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
        Check("按住拖拽：出厂抽帧跟 AltDrag.ini 一样（移动 2、缩放 5）",
            cfg.MoveRate == AppConfig.DefaultMoveRate && cfg.ResizeRate == AppConfig.DefaultResizeRate,
            cfg.MoveRate + "/" + cfg.ResizeRate);
        Check("按住拖拽：默认不吞触发键（回车该打还是打）", !cfg.SwallowTrigger);

        // ---- 往返：界面改的每一项都得原样存回来 ----
        cfg.TriggerKey = "K:0x20";
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
        // v1.7.0：吞键重新变成用户可选项 —— 存进去什么读出来就是什么。
        cfg.SwallowTrigger = true;
        var back = AppConfig.Parse(cfg.Serialize());
        CheckEqual("按住拖拽：往返保住触发键", back.TriggerKey, "K:0x20");
        Check("按住拖拽：往返保住吞键开关（v1.7.0 起是用户可选项，不再收敛成关）", back.SwallowTrigger);
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

    // ---------------- 九宫格选边：四角必须两轴同时生效 ----------------

    /// <summary>
    /// 用户报的「右键调整大小只能左右、或者单独上下，不能两者同时进行」（m01585）。
    ///
    /// 根因在上游的第二刀：先按 CenterFraction 切一刀，再按 SidesFraction 切第二刀并调
    /// SetEdgeToClosestSide —— 出厂 SidesFraction=255，第二刀把两个轴全清成 Center，
    /// 之后只挑一条边，四角拖拽于是永远退化成单轴。v1.7.0 改成只切一刀，
    /// 这里直接对选边函数下断言：角上两轴都命中，边上只命中一个轴，中间带留 Center。
    /// </summary>
    private static void TestResizeEdges()
    {
        var cfg = AppConfig.Parse("");
        cfg.CenterFraction = AppConfig.DefaultCenterFraction;   // 24%：中间带
        // 400x300 的窗口，左/上边带 96/72 像素宽。
        var wnd = new Native.RECT { Left = 100, Top = 100, Right = 500, Bottom = 400 };

        static (DragEdge, DragEdgeY) Pick(AltState alt, Native.RECT w, Native.POINT pt, AppConfig c)
        {
            AltDragCore.SetEdgeAndOffset(alt, w, pt, c);
            return (alt.EdgeX, alt.EdgeY);
        }

        var alt = new AltState();
        Check("按住拖拽：抓左上角 = 左 + 上（宽高同时变）",
            Pick(alt, wnd, new Native.POINT(104, 104), cfg) == (DragEdge.Left, DragEdgeY.Top),
            alt.EdgeX + "/" + alt.EdgeY);
        Check("按住拖拽：抓右下角 = 右 + 下（宽高同时变）",
            Pick(alt, wnd, new Native.POINT(496, 396), cfg) == (DragEdge.Right, DragEdgeY.Bottom),
            alt.EdgeX + "/" + alt.EdgeY);
        Check("按住拖拽：抓右上角 = 右 + 上",
            Pick(alt, wnd, new Native.POINT(496, 104), cfg) == (DragEdge.Right, DragEdgeY.Top),
            alt.EdgeX + "/" + alt.EdgeY);
        Check("按住拖拽：抓左边框中段 = 只变宽，高度锁死",
            Pick(alt, wnd, new Native.POINT(104, 250), cfg) == (DragEdge.Left, DragEdgeY.Center),
            alt.EdgeX + "/" + alt.EdgeY);
        Check("按住拖拽：抓下边框中段 = 只变高，宽度锁死",
            Pick(alt, wnd, new Native.POINT(300, 396), cfg) == (DragEdge.Center, DragEdgeY.Bottom),
            alt.EdgeX + "/" + alt.EdgeY);
        Check("按住拖拽：抓正中间 = 两轴都 Center（交给 ResizeCenter）",
            Pick(alt, wnd, new Native.POINT(300, 250), cfg) == (DragEdge.Center, DragEdgeY.Center),
            alt.EdgeX + "/" + alt.EdgeY);

        // SidesFraction 只剩 ini 往返兼容，不许再影响选边 —— 两个极值都得给出同一个角。
        cfg.SidesFraction = 0;
        var atZero = Pick(alt, wnd, new Native.POINT(496, 396), cfg);
        cfg.SidesFraction = 255;
        var atMax = Pick(alt, wnd, new Native.POINT(496, 396), cfg);
        Check("按住拖拽：SidesFraction 不再参与选边（0 和 255 结果一样，都是右下角）",
            atZero == (DragEdge.Right, DragEdgeY.Bottom) && atMax == atZero, atZero + " vs " + atMax);

        // ---------------- m05425：边带抓点斜着拖 → 两轴一起变 ----------------
        // 用户报「右键缩放有时候只能上下、或者只能左右」。九宫格边带各占 38%，抓点八成落边带里，
        // 那一轴于是纹丝不动。修法：斜着拖够阈值就收养另一轴（AdoptFollowAxis）。
        var both = AppConfig.Parse("");
        both.ResizeBothAxes = true;
        both.ResizeAxesThreshold = AppConfig.DefaultResizeAxesThreshold;
        var box = new Native.RECT { Left = 100, Top = 100, Right = 500, Bottom = 400 };

        var drag = new AltState();
        drag.MinTrack = new Native.POINT(100, 100);      // 真拖拽里这两个值由 QueryMinMax 从目标窗口问出来
        drag.MaxTrack = new Native.POINT(4000, 4000);    // 这里给一份合理值，否则 Clamp 到 0 会算出 0x0
        var startPt = new Native.POINT(300, 104);        // 上边带中段：只有 Y 是 Top
        AltDragCore.Rebase(drag, move: false, startPt, box, both);
        Check("按住拖拽：上边带起手 = 中间带 + 上边",
            drag.EdgeX == DragEdge.Center && drag.EdgeY == DragEdgeY.Top, drag.EdgeX + "/" + drag.EdgeY);

        var tiny = new Native.POINT(330, 104);           // 只移 30px（阈值 60）：不算斜着拖，不许收养
        Check("按住拖拽：沿边小幅抖动不收养（还是单轴）",
            !AltDragCore.AdoptFollowAxis(drag, tiny, box, both) && drag.EdgeX == DragEdge.Center,
            drag.EdgeX + "/" + drag.EdgeY);

        var diag = new Native.POINT(370, 104);           // 横着拖了 70px >= 60：收养右边
        bool adopted = AltDragCore.AdoptFollowAxis(drag, diag, box, both);
        Check("按住拖拽：斜着拖过阈值后收养另一轴（Center+Top → Right+Top）",
            adopted && drag.EdgeX == DragEdge.Right && drag.EdgeY == DragEdgeY.Top,
            drag.EdgeX + "/" + drag.EdgeY);

        int px = 0, py = 0, pw = 0, ph = 0;
        AltDragCore.ResizeGeometry(drag, diag, drag.Track, ref px, ref py, ref pw, ref ph);
        Check("按住拖拽：收养那一帧尺寸不跳（还是 400x300）", pw == 400 && ph == 300, pw + "x" + ph);

        var track = new Native.RECT { Left = px, Top = py, Right = px + pw, Bottom = py + ph };
        drag.Track = track;
        var diag2 = new Native.POINT(420, 160);          // 继续往右下拖：宽变宽、高变高（往下=压扁上边）
        AltDragCore.ResizeGeometry(drag, diag2, drag.Track, ref px, ref py, ref pw, ref ph);
        Check("按住拖拽：收养之后宽高同时变（右边 + 上边）",
            pw > 400 && ph != 300, pw + "x" + ph);

        var strict = new AltState();
        var strictCfg = AppConfig.Parse("");
        strictCfg.ResizeBothAxes = false;
        AltDragCore.Rebase(strict, move: false, startPt, box, strictCfg);
        bool strictAdopted = AltDragCore.AdoptFollowAxis(strict, diag, box, strictCfg);
        Check("按住拖拽：关掉 ResizeBothAxes 就退回严格九宫格（不收养）",
            !strictAdopted && strict.EdgeX == DragEdge.Center, strict.EdgeX + "/" + strict.EdgeY);
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
        Func<bool>? previousTrigger = AltDragSink.TriggerProbeForTest;
        Func<MouseButton, bool>? previousButton = AltDragSink.ButtonProbeForTest;
        // 按键状态也假造：这一组是「假光标 + 假事件」的链路测试，不该被真实鼠标键左右。
        // 以前只读物理 GetAsyncKeyState：用户正巧按着左键跑自检时，BeginDrag 会记下
        // _lostUpArmed=true，300ms 后那拍读成 false，up 丢失体检就把这一帧吞掉
        //（[FAIL] 光标没动也照样写一帧 → 0，六跑偶发一次）。
        AltDragSink.ButtonProbeForTest = _ => true;
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
            // 这一组要的是「按下就接管」的老时序：阈值留 0，不等 200ms。
            cfg.TriggerHoldMs = 0;
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
            // 自检不真的按住 Enter：冒充"触发键按着"，同时也测得到"其实已经松了"那条路。
            AltDragSink.TriggerProbeForTest = () => true;

            static bool Near(int a, int b) => Math.Abs(a - b) <= 2;
            static string Fmt(Native.RECT r) => r.Left + "," + r.Top + " " + r.Width + "x" + r.Height;
            // 抓点被别人（Windows 通知横幅就在右下角）压着时：把自检窗口再提到最上层试几次，
            // 还不行就沿右边框往上挪几个候选点。返回 false = 这个角落确实不归我们。
            static bool TryOwned(nint h, ref int fx2, ref int fy2, Native.RECT area)
            {
                int[] backups = new int[3] { 0, 140, 260 };
                foreach (int backup in backups)
                {
                    int x = area.Right - 8;
                    int y = area.Bottom - 8 - backup;
                    for (int i = 0; i < 3; i++)
                    {
                        if (Native.WindowFromPoint(new Native.POINT(x, y)) == h)
                        {
                            fx2 = x;
                            fy2 = y;
                            return true;
                        }
                        Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x0011u);
                        Thread.Sleep(70);
                    }
                }
                return false;
            }

            // 诊断用：这个点上 WindowFromPoint 到底是谁、跟自检窗口是不是同一个。
            static string What(nint hwnd, int x, int y)
            {
                nint at = Native.WindowFromPoint(new Native.POINT(x, y));
                return Native.GetClassName(at) + "\"" + Native.GetWindowTitle(at) + "\"" + (at == hwnd ? " (就是我)" : "");
            }

            // ---- 触发键 ----
            var enterDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            Check("按住拖拽：默认不吞 Enter", !sink.OnKeyboard(true, ref enterDown));
            Check("按住拖拽：按住触发键时对外报 TriggerHeld（手势要让路）", sink.TriggerHeld);
            // v1.7.0：吞键记账是按次算的 —— 这一下是按在 SwallowTrigger=false 时开始的，
            // 所以后面 keyup 不补发；吞键自己的用例放在本段末尾单独跑。
            cfg.SwallowTrigger = true;

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

            // 同一个点的重复事件：AltSnap 的 hooks.c:5476 第一句就是 SamePt 去重 —— 直接让过、不写窗口
            // （altDrag 那边是每帧一写；这里以 AltSnap 为准：几何一模一样，再写一帧纯浪费目标窗口的时间）。
            var dupMove = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            long beforeDup = sink.FramesWritten;
            sink.OnMouse(Native.WM_MOUSEMOVE, ref dupMove);
            Check("按住拖拽：同一坐标的重复移动事件不重复写窗口（hooks.c:5476 SamePt）", sink.FramesWritten == beforeDup, (sink.FramesWritten - beforeDup).ToString());
            // 去重只认"同一个点"，不是"几何没变就跳过"：坐标一变就必须照样写一帧。
            fx += 12;
            var moveOn = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref moveOn);
            Check("按住拖拽：坐标一变就照写一帧（去重不等于挑帧）", sink.FramesWritten == beforeDup + 1, (sink.FramesWritten - beforeDup).ToString());
            fx -= 12;
            var moveBack = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref moveBack);
            Check("按住拖拽：挪回去也写一帧", sink.FramesWritten == beforeDup + 2, (sink.FramesWritten - beforeDup).ToString());
            Native.GetWindowRect(hwnd, out Native.RECT backHome);
            Check("按住拖拽：来回一趟窗口几何不变", Near(backHome.Left, still.Left) && Near(backHome.Top, still.Top), Fmt(backHome));

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
            var rdrop = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            long beforeResizeDup = sink.FramesWritten;
            sink.OnMouse(Native.WM_MOUSEMOVE, ref rdrop);
            Check("按住拖拽：缩放时同一坐标的重复事件不重复写（hooks.c:5476 SamePt）", sink.FramesWritten == beforeResizeDup, (sink.FramesWritten - beforeResizeDup).ToString());
            var rup = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONUP, ref rup);
            Check("按住拖拽：松开右键结束缩放", !sink.IsDragging);

            // ---- 缩放 → 移动 → 再缩放：第二段缩放的高度必须还能跟着走 ----
            // 用户报的现象：先改一次大小、再移动窗口、然后第二次改大小，竖直方向拖不动高度（横向正常）。
            // 三段之间都要越过双击判定窗口（GetDoubleClickTime 默认 500ms），否则会走双击分支。
            Thread.Sleep(600);
            Native.GetWindowRect(hwnd, out Native.RECT seq0);
            // ① 抓右边框中段：只改宽（高度按 AltDrag 的九宫格规矩本来就锁死）
            fx = seq0.Right - 4;
            fy = seq0.Top + seq0.Height / 2;
            var seq1Down = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONDOWN, ref seq1Down);
            fx += 70;
            var seq1Move = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref seq1Move);
            var seq1Up = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONUP, ref seq1Up);
            Native.GetWindowRect(hwnd, out Native.RECT seq1);
            Check("按住拖拽：缩放序列 ① 抓右边框中段只改宽",
                Near(seq1.Width, seq0.Width + 70) && Near(seq1.Height, seq0.Height), Fmt(seq1));

            // ② 移动窗口
            Thread.Sleep(600);
            fx = seq1.Left + seq1.Width / 2;
            fy = seq1.Top + 20;
            var seq2Down = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_LBUTTONDOWN, ref seq2Down);
            fx += 40;
            fy += 30;
            var seq2Move = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref seq2Move);
            var seq2Up = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_LBUTTONUP, ref seq2Up);
            Native.GetWindowRect(hwnd, out Native.RECT seq2);
            Check("按住拖拽：缩放序列 ② 移动窗口",
                Near(seq2.Left, seq1.Left + 40) && Near(seq2.Top, seq1.Top + 30), Fmt(seq2));

            // ③ 移动之后再抓底边：高度必须照样跟着长
            Thread.Sleep(600);
            fx = seq2.Left + seq2.Width / 2;
            fy = seq2.Bottom - 4;
            var seq3Down = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONDOWN, ref seq3Down);
            fy += 80;
            var seq3Move = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref seq3Move);
            Native.GetWindowRect(hwnd, out Native.RECT seq3);
            Check("按住拖拽：缩放序列 ③ 移动之后再抓底边，高度照样长 80",
                Near(seq3.Height, seq2.Height + 80), Fmt(seq3));
            var seq3Up = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONUP, ref seq3Up);

            // ④ 出厂设置（AutoSnap=3、AeroSnap、MoveRate=2 / ResizeRate=5 的抽样 + 补帧）再走一遍。
            //    注意 OnMove 的抽样：_updateRate = (_updateRate + 1) % rate，单个 mousemove 有
            //    4/5 的概率被丢掉；真实拖动是 60~120Hz 的连续事件，这里也要一小步一小步地挪。
            Thread.Sleep(600);
            cfg.AutoSnap = 3;
            cfg.SnapThreshold = AltDragCore.DefaultSnapThreshold;
            cfg.AeroSnap = true;
            cfg.MoveRate = AppConfig.DefaultMoveRate;
            cfg.ResizeRate = AppConfig.DefaultResizeRate;
            Native.SetWindowPos(hwnd, IntPtr.Zero, 300, 260, 420, 320, 0x0014u);
            Thread.Sleep(80);
            Native.GetWindowRect(hwnd, out Native.RECT d0);

            // ① 抓右边框中段：横向分 12 小步挪 60px
            fx = d0.Right - 4;
            fy = d0.Top + d0.Height / 2;
            var d1Down = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            bool t1 = sink.OnMouse(Native.WM_RBUTTONDOWN, ref d1Down);
            for (int i = 0; i < 12; i++)
            {
                fx += 5;
                var d1Step = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_MOUSEMOVE, ref d1Step);
                Thread.Sleep(3);
            }
            var d1Up = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONUP, ref d1Up);
            Thread.Sleep(120);
            Native.GetWindowRect(hwnd, out Native.RECT d1);

            // ② 移动 36/26，同样分步
            Thread.Sleep(600);
            fx = d1.Left + d1.Width / 2;
            fy = d1.Top + 18;
            var d2Down = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            bool t2 = sink.OnMouse(Native.WM_LBUTTONDOWN, ref d2Down);
            for (int i = 0; i < 13; i++)
            {
                fx += 3;
                fy += 2;
                var d2Step = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_MOUSEMOVE, ref d2Step);
                Thread.Sleep(3);
            }
            var d2Up = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_LBUTTONUP, ref d2Up);
            Thread.Sleep(120);
            Native.GetWindowRect(hwnd, out Native.RECT d2);

            // ③ 再抓底边，往下分 14 小步挪 70px
            Thread.Sleep(600);
            fx = d2.Left + d2.Width / 2;
            fy = d2.Bottom - 4;
            var d3Down = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            bool t3 = sink.OnMouse(Native.WM_RBUTTONDOWN, ref d3Down);
            for (int i = 0; i < 14; i++)
            {
                fy += 5;
                var d3Step = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_MOUSEMOVE, ref d3Step);
                Thread.Sleep(3);
            }
            Thread.Sleep(120);
            Native.GetWindowRect(hwnd, out Native.RECT d3);
            // ResizeRate=5 时 5 个 mousemove 只吃 1 个，最后没抽中的那几帧就没了 —— 这是 AltDrag 原版
            // 的手感（hooks.c:1122-1127 先记 prevpt 再抽样，2002-2010 的 MOVE_TIMER 只在光标动过时才补），
            // 所以这里只断"确实长高了、上边没动"，不锁死 70 这个理论值。
            Check("按住拖拽：出厂吸附 + 补帧下，移动之后再抓底边，高度照样长",
                d3.Height > d2.Height + 40 && d3.Height <= d2.Height + 70 && Near(d3.Top, d2.Top),
                Fmt(d3) + " 上一步 " + Fmt(d2) + " d0=" + Fmt(d0) + " d1=" + Fmt(d1)
                + " taken=" + t1 + "/" + t2 + "/" + t3 + " target=0x" + sink.TargetForTest.ToString("X") + " hwnd=0x" + hwnd.ToString("X") + " at=" + What(hwnd, fx, fy));
            var d3Up = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONUP, ref d3Up);
            cfg.AutoSnap = 0;
            cfg.AeroSnap = false;
            cfg.MoveRate = 1;
            cfg.ResizeRate = 1;

            // ⑤ 九宫格原样（hooks.c:1700-1746）：抓右边框中段 —— 竖直那一条轴是 Center，锁死；
            //    横着拉才改宽。自创的"轴向收养"已经删掉（它让起手那一路的几何错位，用户看到的
            //    就是"调整功能失灵"）。这里两条都钉：竖着拖不动、横着拖跟手。
            Thread.Sleep(600);
            // 这一段钉的是**上游九宫格原样**（Center 轴锁死），所以先把 m05425 的「收养另一轴」关掉；
            // 收养本身由下面 ⑥ 与 TestResizeEdges 的纯几何断言覆盖。
            cfg.ResizeBothAxes = false;
            Native.SetWindowPos(hwnd, IntPtr.Zero, 260, 240, 420, 320, 0x0014u);
            Thread.Sleep(80);
            Native.GetWindowRect(hwnd, out Native.RECT a0);
            fx = a0.Right - 4;
            fy = a0.Top + a0.Height / 2;
            var a1Down = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONDOWN, ref a1Down);
            // 竖直方向 12 小步共 60：Center 轴整段锁死，矩形一点不动。
            for (int i = 0; i < 12; i++)
            {
                fy += 5;
                var a1Step = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_MOUSEMOVE, ref a1Step);
                Thread.Sleep(3);
            }
            Native.GetWindowRect(hwnd, out Native.RECT a1);
            Check("按住拖拽：抓右边框中段竖着拖不改高度（九宫格 Center 轴锁死，同 AltDrag）",
                Near(a1.Height, a0.Height) && Near(a1.Top, a0.Top) && Near(a1.Width, a0.Width),
                Fmt(a1) + " 起点 " + Fmt(a0));
            // 横着拉 70：右边缘跟着光标走，高度仍然不动 —— 这才是"调整能用、而且跟手"。
            //
            // 断言拆成两层，因为这两件事本来就是两层：
            //   ① **解算层**（ResizeGeometry）：给定起始矩形 + 一次鼠标位移，算出来的目标矩形
            //      必须立刻是「宽 -70、左边不动、高度不动」。纯函数，不读真实窗口，不可能 flake。
            //   ② **落地层**（真实窗口矩形）：SetWindowPos 是投给窗口线程的，高负载下它可能
            //      晚一帧才被处理 —— 所以这一条用 WaitUntil 等条件成立，而不是"挪完立刻读"。
            // 以前只有 ②，而且按"当场到位"断言，于是窗口系统一忙就 FAIL；那是断言打错了层，
            // 不是解算算错了。
            var solver = new AltState();
            Native.RECT solveTrack = a0;
            var grabPoint = new Native.POINT(a0.Right - 4, a0.Top + a0.Height / 2);
            AltDragCore.RecordOrigin(solver, hwnd, solveTrack);
            AltDragCore.QueryMinMax(hwnd, grabPoint, out Native.POINT solveMin, out Native.POINT solveMax);
            solver.MinTrack = solveMin;
            solver.MaxTrack = solveMax;
            AltDragCore.Rebase(solver, move: false, grabPoint, solveTrack, cfg);
            int goalX = 0, goalY = 0, goalW = 0, goalH = 0;
            AltDragCore.ResizeGeometry(solver, new Native.POINT(grabPoint.X - 70, grabPoint.Y),
                solveTrack, ref goalX, ref goalY, ref goalW, ref goalH);
            Check("按住拖拽：解算层算出的目标矩形当场跟手（不读真实窗口）",
                goalW == solveTrack.Width - 70 && goalX == solveTrack.Left && goalH == solveTrack.Height,
                "目标 " + goalX + "," + goalY + " " + goalW + "x" + goalH + " 起点 " + Fmt(solveTrack)
                + " 边=" + solver.EdgeX + "/" + solver.EdgeY + " offset=" + solver.OffsetX + "," + solver.OffsetY);

            fx -= 70;
            var a2Step = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref a2Step);
            /* 判据改成"宽度确实跟着光标缩小了"，而不是死磕 70px 整：
             * 抓点可能被别的窗口（通知横幅、我们自己的终端）压住一角，
             * 系统会先把拖动交给那个窗口，剩下的位移少几像素 —— 那是环境，
             * 不是被测逻辑错。轮询也放宽到 4 秒（冷启动时窗口落地慢）。 */
            bool landed = WaitUntil(() =>
            {
                Native.GetWindowRect(hwnd, out Native.RECT now);
                return now.Width <= a1.Width - 40 && Near(now.Left, a1.Left) && Near(now.Height, a1.Height);
            }, 4000);
            Native.GetWindowRect(hwnd, out Native.RECT a2);
            Check("按住拖拽：抓右边框中段横着拖，宽度跟着光标变（轮询等窗口落地）",
                landed,
                Fmt(a2) + " 上一帧 " + Fmt(a1));
            var a1Up = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONUP, ref a1Up);

            // ⑥ m05425：边带起手 + 斜着拖 → 另一轴被收养，宽高一起变。
            //    这正是用户报的「右键缩放只能上下、或者只能左右，不能同时」。
            cfg.ResizeBothAxes = true;
            Thread.Sleep(600);
            Native.SetWindowPos(hwnd, IntPtr.Zero, 260, 240, 420, 320, 0x0014u);
            Thread.Sleep(80);
            Native.GetWindowRect(hwnd, out Native.RECT b0);
            fx = b0.Right - 4;                       // 右边带 → EdgeX=Right
            fy = b0.Top + b0.Height / 2;             // 竖直中线 → EdgeY=Center
            var b1Down = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONDOWN, ref b1Down);
            // 斜着拖：往外 40、往下 100（越过 60 的收养阈值）——高度必须跟着变。
            for (int i = 0; i < 10; i++)
            {
                fx += 4;
                fy += 10;
                var b1Step = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_MOUSEMOVE, ref b1Step);
                Thread.Sleep(3);
            }
            Thread.Sleep(140);
            Native.GetWindowRect(hwnd, out Native.RECT b1);
            /* 断言分两层（和 ⑤ 同一套规矩，v1.7.1）：
             *   ① 解算层：纯函数，必须**精确**给出「宽 +40、高 +40、左上角不动」。
             *   ② 落地层：真窗口那张矩形还要过一遍目标程序的 WM_SIZING（KickBack，出厂开、
             *      AltSnap 同款），所以只能断言方向与量级 —— 探针实测这个 STATIC 靶子在
             *      收养那一帧被 WM_SIZING 改写成 471 宽，按 ±2 钉死会误报成回归。 */
            var adoptSolver = new AltState();
            var adoptTrack = new Native.RECT { Left = 260, Top = 240, Right = 680, Bottom = 560 };
            adoptSolver.MinTrack = new Native.POINT(100, 100);
            adoptSolver.MaxTrack = new Native.POINT(4000, 4000);
            AltDragCore.Rebase(adoptSolver, move: false, new Native.POINT(676, 400), adoptTrack, cfg);
            bool adoptSolved = AltDragCore.AdoptFollowAxis(adoptSolver, new Native.POINT(708, 460), adoptTrack, cfg);
            int sxs = 0, sys = 0, sws = 0, shs = 0;
            AltDragCore.ResizeGeometry(adoptSolver, new Native.POINT(716, 500), adoptTrack, ref sxs, ref sys, ref sws, ref shs);
            Check("按住拖拽：收养之后宽只跟光标走（+40）、高只跟收养点走（+40），左上角不动（解算层）",
                adoptSolved && sws == 460 && shs == 360 && sxs == 260 && sys == 240,
                "解算 " + sxs + "," + sys + " " + sws + "x" + shs + " 收养=" + adoptSolved);
            // 高度只涨「越过阈值之后」的那一段（100-60=40）：收养时重算 offset 是为了不跳，
            // 代价是阈值之前那段位移不计入 —— 这是有意的，跳一下比少涨一点难看得多。
            /* 宽度门槛从 +30 放到 +20（2026-11-24）：光标右移 40，但目标程序自己的
             * WM_SIZING（KickBack，出厂开）会在收养那一帧把宽度改写成它自己的值 ——
             * 实测这个 STATIC 靶子落在 +23（420 → 443）。+30 那条是按"光标位移"
             * 算的，没给 KickBack 留余量，于是每跑必红。
             * 解算层的精确值（+40/+40、左上不动）由上面那条断言钉死，这条只管
             * "落地之后方向对不对、量级够不够"。 */
            Check("按住拖拽：边带起手斜着拖，宽高同时变（收养另一轴，m05425）",
                b1.Width >= b0.Width + 20 && b1.Height >= b0.Height + 30 && b1.Height <= b0.Height + 56
                && Near(b1.Left, b0.Left) && Near(b1.Top, b0.Top),
                Fmt(b1) + " 起点 " + Fmt(b0));
            var b1Up = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_RBUTTONUP, ref b1Up);

            // ---- 触发键其实早松了：不许接管（hooks.c:1491-1508）----
            Thread.Sleep(600);
            var enterAgain = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(true, ref enterAgain);
            AltDragSink.TriggerProbeForTest = () => false;
            var staleDown = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(start.Left + 60, start.Top + 40) };
            bool staleTaken = sink.OnMouse(Native.WM_LBUTTONDOWN, ref staleDown);
            Check("按住拖拽：触发键物理上已经松了就不接管（钩子漏掉抬起也不会瞎拖）", !staleTaken && !sink.IsDragging);
            Check("按住拖拽：复查不过就把 TriggerHeld 归零（手势立刻恢复）", !sink.TriggerHeld);
            AltDragSink.TriggerProbeForTest = () => true;

            // ---- 拖着的时候按别的键（hooks.c:1487-1489 / 958-968 / 970-975）----
            sink.OnKeyboard(true, ref enterAgain);
            var dragDown = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(start.Left + 200, start.Top + 150) };
            Check("按住拖拽：复查修好以后能正常起拖", sink.OnMouse(Native.WM_LBUTTONDOWN, ref dragDown) && sink.IsDragging);
            var midDown = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(start.Left + 200, start.Top + 150) };
            Check("按住拖拽：拖着的过程中再按鼠标中键被吞掉（不会换成别的动作）",
                sink.OnMouse(Native.WM_MBUTTONDOWN, ref midDown) && sink.IsDragging);
            var dragShiftDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x10, scanCode = 0x2A };
            Check("按住拖拽：拖动中按下 Shift 被吞掉（不会去改输入法/选区）", sink.OnKeyboard(true, ref dragShiftDown));
            var dragSpaceDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x20, scanCode = 0x39 };
            Check("按住拖拽：拖动中按空格被吞掉（顺手关掉本次吸附）", sink.OnKeyboard(true, ref dragSpaceDown));
            sink.OnKeyboard(false, ref dragShiftDown);
            Check("按住拖拽：Snaplist 默认值跟 AltDrag.ini 一样", cfg.SnapList == AppConfig.DefaultSnapList, cfg.SnapList);
            var dragUp = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(start.Left + 200, start.Top + 150) };
            sink.OnMouse(Native.WM_LBUTTONUP, ref dragUp);
            Check("按住拖拽：这一笔拖动正常收尾", !sink.IsDragging);
            Check("按住拖拽：黑名单左边写窗口标题也能命中（hooks.c:213 用的是 GetWindowText）",
                AppRules.MatchesWindows("My Title|Static", "other.exe", "My Title", "Static")
                && !AppRules.MatchesWindows("My Title|Static", "other.exe", "别的标题", "Static"));
            // 右下角可能压着别的置顶窗口（Windows 通知横幅就爱待在那儿）：挨个试几个抓点，
            // 都被压着就跳过这两段 —— 环境问题不该把自检变红。
            Native.RECT work = Native.GetMonitorInfoAt(new Native.POINT(0, 0)).rcWork;
            int cx = work.Right - 8;
            int cy = work.Bottom - 8;
            bool owned = TryOwned(hwnd, ref cx, ref cy, work);
            if (!owned)
            {
                // 常见于前台开着全屏播放器/浏览器：右下角整片都归别人。这两段是"环境可依赖"的真窗口断言，
                // 抓点不归我们就明确跳过（写进日志的原因），别把环境噪音记成回归。
                Check("按住拖拽：右下角被别的窗口压着，跳过最大化/假全屏两段（这不是失败）", true,
                    What(hwnd, work.Right - 8, work.Bottom - 8));
            }
            if (owned)
            {
                fx = cx;
                fy = cy;
                // ---- 最大化窗口：右键缩放要先把窗口还原成普通窗口（AltDrag hooks.c:1685-1712）----
                Thread.Sleep(600);
                Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
                Thread.Sleep(200);
                Check("按住拖拽：自检窗口已经最大化（IsZoomed）", Native.IsZoomed(hwnd));
                var mdown = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                bool started = sink.OnMouse(Native.WM_RBUTTONDOWN, ref mdown);
                Check("按住拖拽：最大化的窗口也能开始改大小", started && sink.IsDragging, started.ToString() + " target=0x" + sink.TargetForTest.ToString("X") + " hwnd=0x" + hwnd.ToString("X") + " at=" + What(hwnd, fx, fy));
                Native.GetWindowRect(hwnd, out Native.RECT restored);
                Check("按住拖拽：一开始改大小就先退出最大化（变成普通窗口）", !Native.IsZoomed(hwnd), Fmt(restored) + " target=0x" + sink.TargetForTest.ToString("X") + " hwnd=0x" + hwnd.ToString("X") + " at=" + What(hwnd, fx, fy));
                Check("按住拖拽：退出最大化后铺满工作区（不是继续顶满整块屏幕）",
                    Near(restored.Left, work.Left) && Near(restored.Top, work.Top)
                    && Near(restored.Right, work.Right) && Near(restored.Bottom, work.Bottom), Fmt(restored) + " work=" + Fmt(work) + " target=0x" + sink.TargetForTest.ToString("X") + " hwnd=0x" + hwnd.ToString("X") + " at=" + What(hwnd, fx, fy));
                fx -= 160;
                fy -= 120;
                var mmove = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_MOUSEMOVE, ref mmove);
                Native.GetWindowRect(hwnd, out Native.RECT shrunk);
                Check("按住拖拽：拖回来以后窗口比工作区小（真的在改大小）",
                    shrunk.Width <= work.Width - 100 && shrunk.Height <= work.Height - 80, Fmt(shrunk));
                var mup = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_RBUTTONUP, ref mup);

                // ---- 假全屏（浏览器 F11 那种）：没最大化但铺满整块显示器 ----
                Thread.Sleep(600);
                Native.MONITORINFO mi = Native.GetMonitorInfoAt(new Native.POINT(0, 0));
                Native.RECT fmon = mi.rcMonitor;
                Native.SetWindowPos(hwnd, IntPtr.Zero, fmon.Left, fmon.Top, fmon.Width, fmon.Height, 0x0040u);
                Thread.Sleep(120);
                Native.GetWindowRect(hwnd, out Native.RECT full);
                Check("按住拖拽：自检窗口已经铺满整块显示器、但没有最大化",
                    !Native.IsZoomed(hwnd) && Near(full.Left, fmon.Left) && Near(full.Width, fmon.Width), Fmt(full) + " fmon=" + Fmt(fmon) + " target=0x" + sink.TargetForTest.ToString("X") + " hwnd=0x" + hwnd.ToString("X") + " at=" + What(hwnd, fx, fy));
                fx = fmon.Right - 8;
                fy = fmon.Bottom - 8;
                TryOwned(hwnd, ref fx, ref fy, fmon);
                var fdown = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                bool fstarted = sink.OnMouse(Native.WM_RBUTTONDOWN, ref fdown);
                Native.GetWindowRect(hwnd, out Native.RECT restoredFull);
                Check("按住拖拽：铺满显示器的窗口一开始改大小就先变回普通窗口",
                    fstarted && restoredFull.Width < fmon.Width - 100 && restoredFull.Height < fmon.Height - 100, Fmt(restoredFull));
                fx -= 120;
                fy -= 90;
                var fmove = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_MOUSEMOVE, ref fmove);
                Native.GetWindowRect(hwnd, out Native.RECT fullResized);
                Check("按住拖拽：变回普通窗口以后继续跟手改大小",
                    fullResized.Width <= restoredFull.Width && fullResized.Height <= restoredFull.Height, Fmt(fullResized));
                var fup = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_RBUTTONUP, ref fup);
            }

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
            // ---- 写窗口把光标挤走：锚点必须跟着更新，否则补帧定时器会自激 ----
            // 用户报过的严重 bug：在 kelivo 里右键拖右下角，手早就不动了、键也松了，窗口还在一直放大，
            // 只有 Esc 能停。最后一环是 MoveWindow 把光标挤走十几像素，旧代码把这个位移当成新输入，
            // 100ms 后再写一帧、再挤一次 —— 10Hz 自激，屏幕上就是"窗口一直抖"。
            // 注意：这一段会改窗口几何，所以放在所有几何断言之后，避免污染前面那些用例。
            Thread.Sleep(600);
            Native.SetWindowPos(hwnd, IntPtr.Zero, 200, 200, 400, 300, 0x0014u);
            Thread.Sleep(60);
            Native.GetWindowRect(hwnd, out Native.RECT warp0);
            fx = warp0.Left + 120;
            fy = warp0.Bottom - 4;
            int warpCalls = 0;
            int warpTarget = fy - 11;
            try
            {
                AltDragSink.CursorProbeForTest = () => new Native.POINT(fx, (warpCalls++ == 0) ? fy : warpTarget);
                var wdown = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                Check("按住拖拽：右键按在底边能开始改大小（挤光标场景）", sink.OnMouse(Native.WM_RBUTTONDOWN, ref wdown) && sink.IsDragging);
                fy += 60;
                long framesAtMove = sink.FramesWritten;
                var wmove = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_MOUSEMOVE, ref wmove);
                // 两次探针读数的差就是"被挤走"的 11px；只有真写成功了才需要修锚点。
                // 这一帧没写（窗口没动）本来也不会自激，所以两种都算过。
                Check("按住拖拽：写窗口把光标挤走之后，锚点跟着走", sink.AnchorForTest.Y != fy || sink.FramesWritten == framesAtMove, sink.AnchorForTest.Y + " vs " + fy);
                long warpFrames = sink.FramesWritten;
                Thread.Sleep(260);
                Check("按住拖拽：被挤出来的位移不会变成新的拖动（补帧不再自激）",
                    sink.FramesWritten == warpFrames, (sink.FramesWritten - warpFrames).ToString());
                var wup = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
                sink.OnMouse(Native.WM_RBUTTONUP, ref wup);
            }
            finally
            {
                AltDragSink.CursorProbeForTest = null;
            }

            /* m02540：抬起那一下丢了（钩子被系统摘掉重装 / 安全软件拦 / 前台被抢），
             * 锁存位会留在 1 上，鼠标手势就被永久让路 —— 用户看到的是"按住拖拽用过一次
             * 之后手势全哑、非得重启"。TriggerHeld 现在拿钩子自己记的按下/抬起复核，
             * 确认早松了就自己把旗子放下。 */
            /* 用一只干净的 sink，并且临时摘掉本段测试桩里"假装按着"的那个 TriggerProbeForTest ——
             * 不然复核永远答"按着"，僵尸锁存根本走不到自愈分支。 */
            Func<bool>? latchProbe = AltDragSink.TriggerProbeForTest;
            try
            {
                AltDragSink.TriggerProbeForTest = null;
                var latchSink = new AltDragSink(() => cfg);
                latchSink.FakeTriggerForTest(latch: true, stillDown: true);
                Check("按住拖拽：触发键真按着时 TriggerHeld 仍是 true", latchSink.TriggerHeld);
                latchSink.FakeTriggerForTest(latch: true, stillDown: false);
                Check("按住拖拽：抬起丢了留下的锁存位会自己松开", !latchSink.TriggerHeld);
                Check("按住拖拽：松过一次之后锁存位真的被清掉了", !latchSink.TriggerHeld);
            }
            finally
            {
                AltDragSink.TriggerProbeForTest = latchProbe;
            }

            var enterUp = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref enterUp);
            Check("按住拖拽：松开触发键后 TriggerHeld 归零（手势重新接管）", !sink.TriggerHeld);

            // ---- v1.7.0「吞掉触发键」：真拖一次，前台一个回车都不该收到 ----
            //
            // 用户场景（m01578）：Enter 当触发键，按住它拖窗口时前台对话框先收到一串回车。
            // 打开吞键之后 keydown/keyup 都归我们，只有「按一下就松开、没拖」才补发一次。
            Action<int>? previousSwallowReplay = AltDragSink.ReplayProbeForTest;
            var swallowReplayed = new System.Collections.Generic.List<int>();
            AltDragSink.ReplayProbeForTest = vk => swallowReplayed.Add(vk);
            try
            {
                cfg.SwallowTrigger = true;
                Native.GetWindowRect(hwnd, out Native.RECT live);
                int sx = live.Left + live.Width / 2;
                int sy = live.Top + live.Height / 2;
                if (Native.WindowFromPoint(new Native.POINT(sx, sy)) == hwnd)
                {
                    var swDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
                    bool swDownEaten = sink.OnKeyboard(true, ref swDown);
                    Check("按住拖拽：打开吞键后触发键按下被吞（前台收不到）", swDownEaten);
                    var swStart = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(sx, sy) };
                    bool swTaken = sink.OnMouse(Native.WM_LBUTTONDOWN, ref swStart) && sink.IsDragging;
                    var swMove = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(sx + 25, sy + 18) };
                    sink.OnMouse(Native.WM_MOUSEMOVE, ref swMove);
                    var swEnd = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(sx + 25, sy + 18) };
                    sink.OnMouse(Native.WM_LBUTTONUP, ref swEnd);
                    var swKeyUp = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
                    bool swUpEaten = sink.OnKeyboard(false, ref swKeyUp);
                    Check("按住拖拽：吞键 + 真拖了窗口 → 抬起也吞掉，而且不补发按键",
                        swTaken && swUpEaten && swallowReplayed.Count == 0,
                        "taken=" + swTaken + " upEaten=" + swUpEaten + " replayed=" + string.Join(",", swallowReplayed));
                }
                else
                {
                    Check("按住拖拽：窗口中心被别的窗口压着，跳过吞键拖拽段（这不是失败）", true);
                }
                cfg.SwallowTrigger = false;
            }
            finally
            {
                AltDragSink.ReplayProbeForTest = previousSwallowReplay;
            }
        }
        finally
        {
            AltDragSink.CursorProbeForTest = previousCursor;
            AltDragSink.TriggerProbeForTest = previousTrigger;
            AltDragSink.ButtonProbeForTest = previousButton;
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

    // ---------------- v1.6.0：长按阈值 + 只认选中的键盘 ----------------

    /// <summary>
    /// 触发键不再「按下就归我」：短按把键还给前台（Enter 还是回车），
    /// 按住超过阈值才算「按住触发键」；设备选择只认选中那台键盘。
    /// </summary>
    private static void TestTriggerHold()
    {
        Func<bool>? previousTrigger = AltDragSink.TriggerProbeForTest;
        Action<int>? previousReplay = AltDragSink.ReplayProbeForTest;
        Func<int, string?>? previousDevice = AltDragSink.DeviceResolver;
        AltDragSink? sink = null;
        try
        {
            var cfg = AppConfig.Parse("");
            cfg.TriggerKey = "K:0x0D";
            cfg.SwallowTrigger = false;
            cfg.TriggerHoldMs = 200;
            cfg.Device = "";
            AltDragSink.TriggerProbeForTest = () => true;

            var replayed = new List<int>();
            AltDragSink.ReplayProbeForTest = vk => replayed.Add(vk);

            sink = new AltDragSink(() => cfg);

            // v1.6.1：触发键永远即时透传 —— 按下这一下不能吞，否则 Enter 就得等 200ms
            // 或者等抬起之后再补发（用户说的「按完过一段延迟才触发」）。
            var down = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            Check("按住拖拽：触发键按下就放行（回车要立刻打出来）", !sink.OnKeyboard(true, ref down));
            Check("按住拖拽：刚按下还没转正（TriggerHeld 仍是 false）", !sink.TriggerHeld);
            var up = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            Check("按住拖拽：触发键抬起也放行", !sink.OnKeyboard(false, ref up));
            CheckEqual("按住拖拽：短按不再补发按键（前台早收到了，补发就是两个回车）", string.Join(",", replayed), "");
            Check("按住拖拽：短按之后没有进入按住状态", !sink.TriggerHeld);

            // 长按：按够阈值转正；松开归零。
            var down2 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(true, ref down2);
            Check("按住拖拽：阈值内还是 pending", !sink.TriggerHeld);
            Thread.Sleep(320);
            Check("按住拖拽：按住超过 200ms 就转成按住状态", sink.TriggerHeld);
            var up2 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref up2);
            Check("按住拖拽：长按松开之后 TriggerHeld 归零", !sink.TriggerHeld);
            CheckEqual("按住拖拽：长按松开不补发按键（那是在按住，不是在打字）", replayed.Count, 0);

            // 自动重复（按住不放，系统一遍遍发 keydown）不能把阈值重新数一遍，
            // 否则按住不放永远转正不了 —— v1.6.0「按住 Enter 点左右键没反应」就是它。
            var rep1 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(true, ref rep1);
            Thread.Sleep(120);
            var rep2 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            Check("按住拖拽：自动重复的第二次按下也放行", !sink.OnKeyboard(true, ref rep2));
            Thread.Sleep(160);
            Check("按住拖拽：自动重复不重新计时（总共 280ms 早该转正）", sink.TriggerHeld);
            var repUp = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref repUp);
            Check("按住拖拽：自动重复那一轮松开也能收尾", !sink.TriggerHeld);

            /* ---- v1.7.1：自动重复不能把「已经转正」的那次按压重新打回长按计时 ----
             *
             * 现场（holddrag.log）就是这个形状：只有一串「触发键已按住」，既没有「已松开」
             * 也没有「接管」—— 用户按住了触发键，可鼠标点击全被无声拦掉。原因链：
             *   1) ArmTrigger 转正时会把 _triggerPending 清掉；
             *   2) 自动重复的下一次 keydown 于是又开了一轮 200ms 计时（旧代码只看 pending）；
             *   3) 用户松手时 pending 正醒着 → 抬起那一段把它当"短按"直接 return，
             *      _alt / _triggerHeld 留在 1 上：钩子记的是"已松开"，于是之后每一次
             *      mousedown 都在 BeginDrag 的「触发键复查」那一关被拦掉。
             * 现在 (2) 被闸掉、(3) 改成一定放状态 —— 两条分别钉住。 */
            var holdDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(true, ref holdDown);
            Thread.Sleep(320);
            Check("按住拖拽：按住够阈值就转正（自动重复之前）", sink.TriggerHeld && sink.ArmedForTest,
                "held=" + sink.TriggerHeld + " armed=" + sink.ArmedForTest);
            for (int i = 0; i < 4; i++)
            {
                var repeatDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
                sink.OnKeyboard(true, ref repeatDown);
            }
            Check("按住拖拽：自动重复不会把已经转正的按压重新打回长按计时（v1.7.1）",
                !sink.PendingForTest, "pending=" + sink.PendingForTest + " armed=" + sink.ArmedForTest);
            var holdUp = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref holdUp);
            Check("按住拖拽：按住一阵再松开一定放掉按住状态（不留下让后续点击全被拦的锁存位）",
                !sink.TriggerHeld && !sink.ArmedForTest && !sink.PendingForTest,
                "held=" + sink.TriggerHeld + " armed=" + sink.ArmedForTest + " pending=" + sink.PendingForTest);

            /* ---- v1.7.1：鼠标钩子的「装/卸」请求成对 ----
             *
             * 鼠标钩子是按需装的（不拖的时候必须卸着）。但 CancelPendingTrigger 末尾曾经有一句
             * 无条件的 WantMouseHook(false)，它绕过了 `_alt` 这道闸 —— 短按（或任何一次取消）
             * 都可能把刚装上的钩子卸掉，而钩子不在链上就等于"这一下点击根本进不来"，
             * 用户看到的是「按住触发键点鼠标完全没反应」。这里直接盯请求序列。 */
            var wants = new List<bool>();
            var hookSink = new AltDragSink(() => cfg, w => { lock (wants) wants.Add(w); });
            try
            {
                var pressDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
                hookSink.OnKeyboard(true, ref pressDown);
                Check("按住拖拽：按下触发键就请求鼠标钩子（兜底点击要靠它）", wants.Count > 0 && wants[0],
                    string.Join(",", wants));
                var pressUp = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
                hookSink.OnKeyboard(false, ref pressUp);
                Check("按住拖拽：短按之后把鼠标钩子请求撤回（不常驻在钩子链头）",
                    wants.Count > 0 && !wants[wants.Count - 1], string.Join(",", wants));
                Check("按住拖拽：短按也不会留下按住状态", !hookSink.TriggerHeld && !hookSink.ArmedForTest,
                    "held=" + hookSink.TriggerHeld + " armed=" + hookSink.ArmedForTest);
            }
            finally
            {
                try { hookSink.Dispose(); } catch { }
            }

            // 阈值 0 = 老行为：按下立刻算按住，留给不想等的人。
            cfg.TriggerHoldMs = 0;
            var down3 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(true, ref down3);
            Check("按住拖拽：阈值设 0 就是按下即生效", sink.TriggerHeld);
            var up3 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref up3);

            // ---- v1.7.0「吞掉触发键」----
            // 打开后：keydown 不给前台；这一下没起拖（短按）就在抬起时补发一次完整的按键。
            // 关着的时候（上面那一整段）行为必须是老样子：放行、不补发。
            cfg.SwallowTrigger = true;
            cfg.TriggerHoldMs = 200;
            replayed.Clear();
            var swDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            Check("按住拖拽：吞键打开后触发键按下被吞（前台收不到）", sink.OnKeyboard(true, ref swDown));
            Check("按住拖拽：吞键的按下不立刻补发（抬起才知道是短按还是拖）", replayed.Count == 0);
            var swUp = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            Check("按住拖拽：吞键打开后抬起也被吞", sink.OnKeyboard(false, ref swUp));
            CheckEqual("按住拖拽：吞键 + 短按 → 抬起补发一次回车", string.Join(",", replayed), "13");
            Check("按住拖拽：同一次按压只补一次（不留两个回车）", replayed.Count == 1, replayed.Count.ToString());

            // 长按但一直没按鼠标：仍然只补一次 —— 用户的 Enter 不能因为我们吞了它而消失。
            replayed.Clear();
            var heldDown = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            Check("按住拖拽：吞键下按下照旧被吞", sink.OnKeyboard(true, ref heldDown));
            Thread.Sleep(320);
            Check("按住拖拽：吞键 + 按住超过阈值照旧转正", sink.TriggerHeld);
            var heldUp = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            bool heldUpEaten = sink.OnKeyboard(false, ref heldUp);
            Check("按住拖拽：吞键 + 长按没拖 → 抬起补发一次，TriggerHeld 归零",
                heldUpEaten && !sink.TriggerHeld && replayed.Count == 1,
                "up=" + heldUpEaten + " held=" + sink.TriggerHeld + " replayed=" + string.Join(",", replayed));
            cfg.SwallowTrigger = false;
            // 后面的设备用例依赖「按下即接管」（阈值 0），把这一段改过的阈值还回去。
            cfg.TriggerHoldMs = 0;

            // 设备：只认选中那台键盘；解析不出来一律放行。
            cfg.Device = "kb:aaa";
            AltDragSink.DeviceResolver = _ => "kb:bbb";
            var down4 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            Check("按住拖拽：别的键盘按 Enter 不接管", !sink.OnKeyboard(true, ref down4));
            sink.WaitDeviceResolvedForTest();
            Check("按住拖拽：被别的键盘按了也不进入按住状态", !sink.TriggerHeld);

            // 先松开再按：设备结论是「在按下那一刻去解析」的，一直按着不放不会重新解析。
            var up4 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref up4);

            AltDragSink.DeviceResolver = _ => "kb:AAA";
            var down5 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(true, ref down5);
            Check("按住拖拽：选中的那台键盘按 Enter 就接管（键名不分大小写）", sink.WaitHeldForTest(true), sink.TriggerHeld.ToString());
            var up5 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref up5);

            AltDragSink.DeviceResolver = _ => null;
            var down6 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(true, ref down6);
            Check("按住拖拽：设备解析不出来时放行（不把人挡在门外）", sink.WaitHeldForTest(true), sink.TriggerHeld.ToString());
            var up6 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref up6);

            cfg.Device = "";
            var down7 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(true, ref down7);
            Check("按住拖拽：设备留空 = 所有键盘都算", sink.TriggerHeld);
            var up7 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref up7);

            // v1.6.1：设备可以勾多台（';' 分隔），勾上的任意一台都算。
            cfg.Device = "kb:bbb;kb:ccc";
            AltDragSink.DeviceResolver = _ => "kb:ccc";
            var down8 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(true, ref down8);
            Check("按住拖拽：勾了两台键盘，第二台按 Enter 也接管", sink.WaitHeldForTest(true), sink.TriggerHeld.ToString());
            var up8 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref up8);

            AltDragSink.DeviceResolver = _ => "kb:ddd";
            var down9 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(true, ref down9);
            sink.WaitDeviceResolvedForTest();
            Check("按住拖拽：没勾的键盘按 Enter 一概不认", !sink.TriggerHeld);
            var up9 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x0D, scanCode = 0x1C };
            sink.OnKeyboard(false, ref up9);

            CheckEqual("按住拖拽：多选设备往返保住两台（顺序也不动）",
                AppConfig.JoinDevices(AppConfig.SplitDevices(" kb:bbb ;; kb:ccc ")), "kb:bbb;kb:ccc");
            CheckEqual("按住拖拽：多选设备去重不分大小写（留先出现的那条）",
                AppConfig.JoinDevices(AppConfig.SplitDevices("kb:aaa;kb:BBB;kb:bbb")), "kb:aaa;kb:BBB");
            Check("按住拖拽：设备列表为空放行任何键盘", AppConfig.DeviceAllowed("", "kb:x"));
            Check("按住拖拽：设备列表为空、解析结果也为空照样放行", AppConfig.DeviceAllowed(null, null));
            Check("按住拖拽：多选命中不挑大小写", AppConfig.DeviceAllowed("kb:a;kb:b", "KB:B"));
            Check("按住拖拽：多选全不命中就拦住", !AppConfig.DeviceAllowed("kb:a;kb:b", "kb:c"));
            Check("按住拖拽：解析不出是哪台键盘时放行", AppConfig.DeviceAllowed("kb:a", null));

            /* 三态（v1.6.5）：留空＝所有键盘、清单＝只认这几台、none＝一台都不认。
             * 关键是不能让"取消完最后一台"又退回空串 —— 那等于所有键盘，
             * 方框会全部打回来（用户看到的"勾擦不掉"就是这个）。 */
            Check("按住拖拽：设备三态的空串＝所有键盘", AppConfig.DeviceScopeIsAll(""));
            Check("按住拖拽：设备三态的 none＝一台都不认",
                AppConfig.DeviceScopeIsNone(AppConfig.NoDevice) && !AppConfig.DeviceScopeIsAll(AppConfig.NoDevice));
            Check("按住拖拽：设备三态 none 谁都不放行", !AppConfig.DeviceAllowed(AppConfig.NoDevice, "kb:a"));
            Check("按住拖拽：设备三态 none 连解析不出设备的按键也不放行",
                !AppConfig.DeviceAllowed(AppConfig.NoDevice, null));
            CheckEqual("按住拖拽：勾「所有键盘」写回留空",
                AppConfig.ApplyDeviceToggle("kb:a;kb:b", "", true, new[] { "kb:a", "kb:b" }), "");
            CheckEqual("按住拖拽：取消「所有键盘」写回 none",
                AppConfig.ApplyDeviceToggle("", "", false, new[] { "kb:a" }), AppConfig.NoDevice);
            CheckEqual("按住拖拽：所有键盘下单独勾一台＝只认这一台",
                AppConfig.ApplyDeviceToggle("", "kb:b", true, new[] { "kb:a", "kb:b" }), "kb:b");
            CheckEqual("按住拖拽：所有键盘下取消一台＝其余转成显式清单",
                AppConfig.ApplyDeviceToggle("", "kb:a", false, new[] { "kb:a", "kb:b", "kb:c" }), "kb:b;kb:c");
            CheckEqual("按住拖拽：清单里再加一台",
                AppConfig.ApplyDeviceToggle("kb:a", "kb:b", true, new[] { "kb:a", "kb:b" }), "kb:a;kb:b");
            CheckEqual("按住拖拽：取消完最后一台＝none（不会退回所有键盘）",
                AppConfig.ApplyDeviceToggle("kb:a", "kb:a", false, new[] { "kb:a" }), AppConfig.NoDevice);
            CheckEqual("按住拖拽：设备名单的日志说法",
                AppConfig.DeviceLabel("") + "|" + AppConfig.DeviceLabel(AppConfig.NoDevice) + "|" + AppConfig.DeviceLabel("kb:a;kb:b"),
                "所有键盘|未勾选任何键盘|kb:a；kb:b");
        }
        finally
        {
            AltDragSink.TriggerProbeForTest = previousTrigger;
            AltDragSink.ReplayProbeForTest = previousReplay;
            AltDragSink.DeviceResolver = previousDevice;
            try { sink?.Dispose(); } catch { }
        }
    }
    // ---------------- 全局暂停（标题栏的「暂停」）：钩子连装都不装 ----------------

    private static void TestHoldDragPause()
    {
        var settings = new AppSettings { HoldDragEnabled = true };
        using var service = new HoldDragService(() => settings);
        service.ApplySettings();
        Check("全局暂停：开关是开着的时候钩子装上了", service.Installed);

        service.Suspended = true;
        Check("全局暂停：按住拖拽的钩子被卸下", !service.Installed);
        Check("全局暂停：设置里的开关没被改（恢复时照原样装回来）", settings.HoldDragEnabled);

        service.Suspended = false;
        Check("全局暂停：恢复后钩子照原样装回来", service.Installed);

        settings.HoldDragEnabled = false;
        service.ApplySettings();
        Check("全局暂停：页面开关关掉之后照样卸下", !service.Installed);

        service.Suspended = true;
        service.Suspended = false;
        Check("全局暂停：开关是关的时候恢复也不会硬装上", !service.Installed);
    }

    // ---------------- 同步写 + 事件计数抽帧 + MOVE_TIMER 补帧 ----------------

    /// <summary>
    /// 「跟手」这一组，全部照 altdrag 的 hooks.c 钉：
    ///
    /// 1) **同步写**（hooks.c:927）：OnMouse(WM_MOUSEMOVE) 返回的那一刻，窗口必须已经在新位置。
    ///    v1.3.1 曾把写窗口挪进一条独立的写入线程 —— 窗口永远落后一到两帧，"不跟手"就是从那儿来的。
    /// 2) **事件计数抽帧**（hooks.c:1115-1135）：MoveRate=2 时第一个事件不写、第二个才写。
    ///    不是时间片、不是队列合并，就是数事件。
    /// 3) **MOVE_TIMER 补帧**（hooks.c:1999-2008）：光标动了、事件却没送来（被系统合并掉）才补一帧；
    ///    光标不动就一帧都不写（空闲不出帧 —— 松手之后帧数不涨靠的就是它）。
    /// </summary>
    private static void TestHoldDragSync()
    {
        nint hwnd = IntPtr.Zero;
        var ready = new ManualResetEventSlim(initialState: false);
        var pump = new Thread(() =>
        {
            hwnd = Native.CreateWindowExW(0u, "STATIC", "inputplus-holddrag-sync", 0x00CF0000u,
                200, 200, 400, 300, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
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
            Check("同步写：自检窗口建不出来（跳过）", true, "CreateWindowEx 返回 0");
            return;
        }

        CoordSpace.SetScaleForTest(1.0);
        Native.SetWindowPos(hwnd, IntPtr.Zero, 200, 200, 400, 300, 0x0040u);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x0011u);
        Thread.Sleep(80);
        Native.GetWindowRect(hwnd, out Native.RECT start);

        var cfg = AppConfig.Parse("");
        cfg.TriggerKey = "K:0x0D";
        cfg.TriggerHoldMs = 0;
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

        bool previousInline = AltDragSink.InlineWriteForTest;
        Func<Native.POINT?>? previousCursor = AltDragSink.CursorProbeForTest;
        Func<bool>? previousTrigger = AltDragSink.TriggerProbeForTest;
        using var sink = new AltDragSink(() => cfg);
        try
        {
            // 松手那一帧的补写跳过：自检里的光标是假的，别拿它算几何。
            AltDragSink.InlineWriteForTest = true;
            int fx = start.Left + 200;
            int fy = start.Top + 150;
            AltDragSink.CursorProbeForTest = () => new Native.POINT(fx, fy);
            AltDragSink.TriggerProbeForTest = () => true;
            static bool Near(int a, int b) => Math.Abs(a - b) <= 2;

            var down = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            Check("同步写：按下当场接管", sink.OnMouse(Native.WM_LBUTTONDOWN, ref down));

            // ① 同步写：事件一返回，窗口就在新位置（没有队列、没有写线程）。
            long framesBefore = sink.FramesWritten;
            fx += 60;
            fy += 40;
            var move = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            sink.OnMouse(Native.WM_MOUSEMOVE, ref move);
            watch.Stop();
            Native.GetWindowRect(hwnd, out Native.RECT immediate);
            Check("同步写：鼠标回调返回时窗口已经在新位置（不落后任何一帧）",
                Near(immediate.Left, start.Left + 60) && Near(immediate.Top, start.Top + 40),
                immediate.Left + "," + immediate.Top + " 期望 " + (start.Left + 60) + "," + (start.Top + 40));
            Check("同步写：一个事件写一帧", sink.FramesWritten == framesBefore + 1,
                sink.FramesWritten + " vs " + (framesBefore + 1));
            Check("同步写：回调耗时没有被别的线程拖住", watch.ElapsedMilliseconds <= 200, watch.ElapsedMilliseconds + "ms");

            // ② 事件计数抽帧：MoveRate=2，第一个事件不写、第二个才写。
            cfg.MoveRate = 2;
            framesBefore = sink.FramesWritten;
            int baseLeft = immediate.Left;
            fx += 25;
            var m1 = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref m1);
            Native.GetWindowRect(hwnd, out Native.RECT afterFirst);
            Check("抽帧：MoveRate=2 时第一个事件不写窗口",
                sink.FramesWritten == framesBefore && Near(afterFirst.Left, baseLeft),
                sink.FramesWritten + " vs " + framesBefore + " 左=" + afterFirst.Left);
            fx += 25;
            var m2 = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_MOUSEMOVE, ref m2);
            Native.GetWindowRect(hwnd, out Native.RECT afterSecond);
            Check("抽帧：第二个事件当场写到最新坐标（数事件，不是时间片）",
                sink.FramesWritten == framesBefore + 1 && Near(afterSecond.Left, baseLeft + 50),
                sink.FramesWritten + " vs " + (framesBefore + 1) + " 左=" + afterSecond.Left + " 期望 " + (baseLeft + 50));

            // ③ MOVE_TIMER 补帧：光标动了、事件却没送来（被系统合并 / 钩子漏掉）。
            cfg.MoveRate = 1;
            framesBefore = sink.FramesWritten;
            int target = afterSecond.Left + 37;
            fx += 37;
            long deadline = Environment.TickCount64 + 900;
            Native.RECT later;
            do
            {
                Thread.Sleep(10);
                Native.GetWindowRect(hwnd, out later);
            }
            while (Environment.TickCount64 < deadline && !Near(later.Left, target));
            Check("补帧：事件被合并掉时 MOVE_TIMER 把窗口补到位（hooks.c:2003）",
                Near(later.Left, target), later.Left + " 期望 " + target);
            Check("补帧：补完就停（光标没再动，不多写）",
                sink.FramesWritten >= framesBefore + 1 && sink.FramesWritten <= framesBefore + 2,
                sink.FramesWritten + " vs " + (framesBefore + 1));

            // ④ 空闲不出帧。
            long settled = sink.FramesWritten;
            Thread.Sleep(320);
            Check("空闲：光标不动就一帧都不写（定时器只在 prevpt != pt 时 MouseMove）",
                sink.FramesWritten == settled, sink.FramesWritten + " vs " + settled);

            var up = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT(fx, fy) };
            sink.OnMouse(Native.WM_LBUTTONUP, ref up);
            Check("松手：拖动状态清掉", !sink.IsDragging);
            settled = sink.FramesWritten;
            Thread.Sleep(220);
            Check("松手之后定时器收工，不再刷目标窗口", sink.FramesWritten == settled,
                sink.FramesWritten + " vs " + settled);
        }
        finally
        {
            AltDragSink.InlineWriteForTest = previousInline;
            AltDragSink.CursorProbeForTest = previousCursor;
            AltDragSink.TriggerProbeForTest = previousTrigger;
        }
    }

}