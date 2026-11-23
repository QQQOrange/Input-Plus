using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Actions;
using InputPlus.Core.Gestures;
using InputPlus.Core.Input;
using InputPlus.Core.Interop;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;
using InputPlus.UI.Windows;

namespace InputPlus.UI;

/// <summary>
/// 鼠标手势服务：从钩子里拿"真实的"鼠标按键与滚轮，攒出一条轨迹，
/// 识别成动作后交给和键盘完全相同的动作管线执行。
///
/// 为什么这件事只能在我们自己的钩子里做：WGestures 那类工具靠低级钩子
/// 观察全局鼠标；而我们**就是**钩子的主人。手势起点是物理按键，
/// 不需要把合成事件再喂回系统（那正是以前"侧键→中键→手势"永远失灵的原因：
/// 合成鼠标事件带着 LLMHF_INJECTED，手势软件一律不理）。
///
/// 线程模型：钩子回调在钩子线程，绘制/执行在界面线程。
/// 共享状态用 <see cref="_gate"/> 保护，钩子回调里只做"记账 + BeginInvoke"。
/// </summary>
public sealed partial class GestureService : IDisposable
{
    private readonly AppHost _host;
    private readonly object _gate = new();

    private GestureOverlayWindow? _overlay;
    private DispatcherTimer? _poll;

    /// <summary>覆盖层看门狗（见 <see cref="OnOverlayGuard"/>）：100ms 兜一次底。</summary>
    private DispatcherTimer? _overlayGuard;
    private DispatcherTimer? _linger;
    /* 名称框淡出的第二段定时器：停留结束 → 开始淡出 → 淡完才清窗口。 */
    private DispatcherTimer? _fade;

    /* ── 覆盖层的「这一笔」代号 ───────────────────────────────────
     * 每次起手 +1。上一笔排的延迟收起（停留 / 淡出）回来时号对不上就作废。
     * 少了这一层，连着划两下时上一笔的淡出定时器会在第二笔中途 Clear()+Hide()，
     * 表现是"手势滑了，屏幕上一个像素都没有"，但动作其实执行了。 */
    private int _hideEpoch;

    /* 两个延迟收起定时器各自记着"我是哪一代排的队"。 */
    private int _lingerEpoch;
    private int _fadeEpoch;

    /* 手势进行中每隔一会儿把覆盖层重新顶到最前（有些程序会把自己置顶压上来）。 */
    private long _lastRaiseAt;
    private const int RaiseIntervalMs = 200;

    private readonly List<GestureMath.Pt> _points = new();
    private GestureMath.Pt _start;
    private GestureMath.Pt _last;
    private int _vk;
    private bool _active;
    private string _appPath = "";
    private MouseGesture? _cornerCandidate;
    private GesturePattern.Tracker? _tracker;
    private long _activeSince;
    private long _lastMoveAt;
    private bool _activeInjected;

    /* 起笔那一刻前台是不是开始菜单 / 搜索（见 ShellOverlay）。
     *
     * 为什么要有：Win11 的开始菜单是 XAML 岛窗口，跑在 Z 序带 6；我们的轨迹窗是普通
     * topmost WPF 窗口（带 0/1），于是"其实收到了指针、也在画"，但整条轨迹被开始菜单
     * 盖得一点看不见 —— 用户报的"在开始菜单里划不出手势"就是这个。
     * 指针一动就先收起浮层（_shellOverlayDismissed 保证只收一次），再重算作用目标，
     * 否则动作会发给 SearchHost.exe，而不是下面那个真正想操作的窗口。
     * 纯点击/按住不动不收：右键菜单还得在开始菜单里补发。 */
    private volatile bool _shellOverlayUp;
    private volatile bool _shellOverlayDismissed;
    private long _shellOverlayAt;

    /* 画的过程中实时显示“现在最像哪条手势”的名称，避免用户等到松手才知道。 */
    private string _previewTitle = "";

    /* 上一次跑判定时记到第几个点。形状比的是整笔轨迹，v5.13 起再没有“方向串变没变”
     * 这种便宜的判据了，于是按点数节流：每多记 3 个点（约 6px）才进一次引擎，
     * 跟手又不至于让 8ms 的轮询每一下都跑一遍 DP。 */
    private int _previewCount;

    /* ── 多步手势（v1.5.0） ────────────────────────────────────────
     * 按住触发键不放，一步接一步地做：画一笔 → 停一下 → 再画一笔 →
     * 按一组键 → 滚一下 → 甩到屏幕角。停 300ms 就算上一笔收尾。
     * 记下来的序列在松手时与 MouseGesture.Steps 逐项比对（见 PickSteps）。 */
    private readonly List<GestureStep> _stepsDrawn = new();
    private int _segStart;
    private bool _multiExpected;
    private const int StepPauseMs = 300;

    /* 边缘双击转角落（WGestures 的 A/K.cs:505）：贴边连点两下也当一次角落手势，
     * 不必把指针精确压进角里。这里记上一次"补发过的单击"的时间与位置。 */
    private long _lastTapAt;
    private GestureMath.Pt _lastTapPt;

    /* 防止“按住鼠标乱划”：滑动窗口里最多允许 20 次新手势，
     * 超过后短暂停用手势并提示，避免误触/脚本刷动作。 */
    private readonly Queue<long> _gestureBurst = new();
    private long _gestureBurstBlockedUntil;
    private const int GestureBurstLimit = 20;
    private const int GestureSegmentLimit = 20;
    private const long GestureBurstWindowMs = 10_000;
    private const long GestureBurstCooldownMs = 3_000;

    /* 立即执行后，物理鼠标键还按着：它的抬起必须由我们吞掉，
     * 否则系统会收到一个"没有按下"的抬起。
     * 0 = 没有待收尾的立即执行；否则是那个鼠标键的 vk。 */
    private int _swallowUpVk;

    /* ── 「按下被吞、却什么都没补发」的那几条路（v1.4.4 修） ──────────────
     *
     * 起始超时（按住不动 ≥ StartTimeoutMs）、分段过多、看门狗判定抬起丢了 ——
     * 这三条以前都是「吞掉按下 + 吞掉抬起 + 什么都不补发」。用户看到的就是：
     * **按住右键稍微久一点（默认 500ms）就再也弹不出右键菜单**，
     * 按得越快越正常，看起来像"右键时灵时不灵"。
     *
     * WGestures 2 在同一批路径上照样补发一次点击（B/d.cs 的回放分支不看超时标志），
     * 所以它的右键从来不会因为"按得慢"而失效。
     *
     * 0 = 没有待补发；否则是那个鼠标键的 vk。抬起事件到达时吞掉它并补发一次点击。 */
    private int _replayClickOnUpVk;

    /* 热角（悬停到屏幕角）监视器。WGestures 用鼠标路径跟踪器顺带判角，
     * 我们已经有独立的鼠标钩子，但"没按键、只是把指针停在角上"不会产生
     * 任何鼠标消息，所以必须自己轮询指针位置。 */
    private DispatcherTimer? _hotWatch;
    private bool _hotArmed = true;

    /* 鼠标手势总开关的全局热键。用 RegisterHotKey 而不是键盘钩子：
     * 钩子已经被Input Plus主引擎占用，手势服务自己再装一颗会重复收键；
     * RegisterHotKey 不抢输入、也不需要管理员权限。 */
    private HwndSource? _hotkeySource;
    private const int ToggleHotkeyId = 0x4B47;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private Action<MouseGesture>? _recordDone;

    /* 手势专用鼠标钩子。
     *
     * 不能用 Engine.Hook：宿主模式（--hookhost）下真正的钩子在最小进程里，
     * 主进程这颗是没装过的空壳 —— 订阅它等于一条手势也收不到。
     * 这里自己装一颗**只装鼠标**的钩子，和宿主/引擎互不影响。 */
    private KeyboardHook? _gestureHook;

    /* 手势动作执行后的抑制窗：动作里可能再发鼠标键（“点击中键”之类），
     * 那些注入不能被当成新手势的开头。 */
    private long _gestureSuppressUntil;

    /* 设置页弹「录一条新手势」时临时挂起：
     * 那个窗口要自己收鼠标按下/抬起，全局手势如果还在这儿拦一道，
     * 画出来的笔会被吞掉，窗口里什么都收不到。 */
    /// <summary>
    /// 设置页弹「录一条新手势」时临时挂起。
    ///
    /// 挂起时必须把正在画的那一笔收掉：录制窗口会自己截鼠标，抬起事件
    /// 不会再回到手势钩子；如果这里还留着 <c>_active=true</c>，下一次右键
    /// 会被当成"上一笔的抬起"吞掉 —— 用户看到的就是右键偶尔失灵。
    /// </summary>
    private volatile bool _suspended;
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            if (value)
            {
                /* 挂起时如果正画着一笔，抬起不会再回到钩子（录制窗自己截鼠标）——
                 * 这一下的按下已经吞了，必须补发，否则"打开录制窗那一下右键"就丢了。 */
                int tracking = 0;
                lock (_gate) if (_active) tracking = _vk;
                if (tracking != 0) AbortTrackingAndReplay(tracking);
                else ResetActive();
                _hotArmed = false;   // 关掉录制窗后先离开角/边，避免立刻触发
            }
            else
            {
                _hotArmed = false;
            }

            /* 暂停期间连"开关鼠标手势"这颗热键也要停：全局暂停就该一根钩子都不留，
             * 否则按一下快捷键又把手势打开了，看起来像暂停没生效。 */
            ApplyHotkey();
        }
    }

    /* 手势开始时鼠标下的那个顶层窗口。
     *
     * 手势是"在某个窗口上画"的，但画的时候左键/右键被我们吞掉了，
     * 那个窗口根本没被激活。WGestures 的做法是把动作发给画手势时
     * 鼠标下的窗口；这里记下句柄，执行前先把它切到前台，
     * 这样「↓→ 关闭」的 Ctrl+W 才会落在用户以为的那个窗口上。 */
    private IntPtr _targetWindow;

    /* 补发单击之后的一小段静默期：这段里同一个键再来（包括被别的鼠标
     * 工具复制出来的无标记副本）都直接放行，不再开新手势。 */
    private readonly Dictionary<int, long> _replayUntil = new();

    /* ── 补发到底有没有生效（v1.4.4） ────────────────────────────────
     *
     * 补发用的是 SendInput。SendInput 返回成功**不等于**事件真的进了系统：
     * 安全软件 / 输入过滤驱动 / UIPI（前台窗口权限更高）都会让它静默消失，
     * 而 SendInput 照样返回条数。这时我们"吞掉按下 + 补发"就等于把点击吃了，
     * 表现正是"右键在任何地方都没反应"，而手势本身看起来还是好的。
     *
     * 唯一可靠的自证：我们自己的钩子也能看到注入事件。补发之后等一下，
     * 钩子一次都没看到 → 这台机器上补发不可信 → 直接 fail-open（不再接管鼠标）。 */
    private long _lastInjectedMouseSeenAt;
    private int _replayUnobserved;

    /* ── 开机宽限（v1.5.1）──────────────────────────────────────────
     *
     * 开机后的头 20 秒里，前台/桌面还在抖动（UAC 安全桌面、启动静默期、
     * 各种开机自启程序抢前台），补发没回来不代表"这台机器注不了入"。
     * 以前这几秒的失败照样算连击，三次就把手势全局 fail-open 了 ——
     * 用户重启电脑后"手势全没了"，查到最后就是这里（2026-11-23 现场）。
     */
    private const int BootGraceMs = 20_000;
    private readonly long _hookStartedAt = Environment.TickCount64;

    /* 组合手势触发后，那颗"第二键"的抬起还要吞掉一次，
     * 否则系统会看到一次"无按下"的抬起。 */
    private int _chordSwallow;

    public GestureService(AppHost host)
    {
        _host = host;
        /* 附加菜单的键盘唤出：设置一变就重注册（用户改完组合键立刻生效），
         * 启动时先按当前设置注册一次。 */
        _host.SettingsChanged += ApplyMiddleHotkey;
        ApplyMiddleHotkey();
    }

    /// <summary>是否正在等用户画一笔（设置页的「录制」按钮）。</summary>
    public bool IsRecording { get; private set; }

    /// <summary>正在绘制中（自检和界面用它判断状态，不参与逻辑）。</summary>
    public bool IsTracking
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>
    /// 手势用的鼠标钩子：平滑滚动也挂在同一条上。
    /// WH_MOUSE_LL 是全局单点资源，装两条只会互相拖慢（每条都在同一条系统
    /// 钩子链上），所以后来的功能一律复用这一条。
    /// </summary>
    internal KeyboardHook? Hook => _gestureHook;

    /// <summary>
    /// 钩子自愈：鼠标钩子没装成功（被别的钩子挤掉、桌面临时不可用）时重装。
    /// 平滑滚动复用这条钩子，钩子没了它也就静默失效 —— 表现正是"开了没效果"。
    /// </summary>
    public void EnsureMouseHook()
    {
        try
        {
            if (_gestureHook is { MouseHookInstalled: false }) _gestureHook.Restart();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "重装手势鼠标钩子"); }
    }

    public void Attach()
    {
        try
        {
            _gestureHook = new KeyboardHook();
            _gestureHook.GestureButtonEvent += OnButton;
            _gestureHook.GestureWheelEvent += OnWheel;
            _gestureHook.KeySeen += OnKeySeen;
            _gestureHook.Start(mouseOnly: true);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "安装手势鼠标钩子"); }

        // 第一次启动时把 WGestures 的手势搬过来：用户升级上来就能直接用，
        // 不用先学会"导入"在哪。放到空闲优先级，等主窗口建好再改设置。
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(
                new Action(InitializeDefaults),
                DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "安排手势自动导入"); }

        ApplyHotkey();
        StartHotWatch();
    }

    /// <summary>
    /// 先导入 WGestures 配置，再补内置边角手势，最后把老动作换成正式动作（顺序不能反）。
    /// </summary>
    private void InitializeDefaults()
    {
        AutoImportOnce();
        try
        {
            if (GestureDefaults.Ensure(_host.Settings)) _host.SaveSettings();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "补齐内置边角手势"); }

        /* 老配置里的兼容动作（openUrl 这类）在这里一次性换成正式动作。
         * 不做的话，界面上打开那一条只能显示成"旧功能"，用户一保存就丢功能。
         * 换完参数不变、执行路径也不变，只是 id 变成界面上选得出来的那个。 */
        try
        {
            if (InputPlus.Core.Actions.LegacyActionMigration.Apply(_host.Settings) > 0) _host.SaveSettings();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "老动作换成正式动作"); }

        /* 覆盖层预热：第一笔手势不该为「建窗 + 首次上屏」付 30ms 的账。
         * 这里在空闲优先级先建好、显示一次再藏起来，之后每笔起手都是纯绘制。
         * 见 GestureOverlayWindow 与 GestureService.EnsureOverlay 的注释。 */
        try { Ui(() => EnsureOverlay().WarmUp()); }
        catch (Exception ex) { CoreLog.Swallow(ex, "预热手势覆盖层"); }

        /* 看门狗从起手那一刻就该在跑：它要能兜住"抬起丢了"和"收尾漏了一次"这两种情况。 */
        try { Ui(() => StartOverlayGuard()); }
        catch (Exception ex) { CoreLog.Swallow(ex, "启动覆盖层看门狗"); }
    }

    /// <summary>只在"没导过 + 自己还没手势 + 找得到 WGestures 配置"时导入一次。</summary>
    private void AutoImportOnce()
    {
        try
        {
            var settings = _host.Settings;
            var options = settings.GestureOptions;
            if (options == null || !string.IsNullOrEmpty(options.AutoImportSource)) return;
            if (settings.Gestures.Count > 0) return;

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WGestures2");
            if (!Directory.Exists(root)) return;

            string? dir = Directory.GetDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (dir == null) return;

            string file = Path.Combine(dir, "gestures.json");
            if (!File.Exists(file)) return;

            var (imported, skipped) = WGesturesImporter.ImportFile(file, settings);
            options.AutoImportSource = file;
            _host.SaveSettings();

            if (imported > 0)
                _host.Notify(Loc.F("已从 WGestures 导入 {0} 条手势（{1} 条需手动处理）", imported, skipped));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "自动导入手势"); }
    }

    public void Dispose()
    {
        try { _host.SettingsChanged -= ApplyMiddleHotkey; } catch { }
        ReleaseMiddleHotkey();
        try
        {
            if (_gestureHook != null)
            {
                _gestureHook.GestureButtonEvent -= OnButton;
                _gestureHook.GestureWheelEvent -= OnWheel;
                _gestureHook.KeySeen -= OnKeySeen;
                _gestureHook.Dispose();
                _gestureHook = null;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "卸载手势钩子"); }

        _poll?.Stop();
        _linger?.Stop();
        _fade?.Stop();
        _hotWatch?.Stop();
        try
        {
            if (_hotkeySource != null)
            {
                UnregisterHotKey(_hotkeySource.Handle, ToggleHotkeyId);
                _hotkeySource.RemoveHook(HotkeyWndProc);
                _hotkeySource.Dispose();
                _hotkeySource = null;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "注销手势热键"); }
        try { _overlay?.Close(); } catch (Exception ex) { CoreLog.Swallow(ex, "关闭手势覆盖层"); }
    }

    // ───────────────────────── 录制 ─────────────────────────

    /// <summary>
    /// 进入录制：下一笔（任意按键）不执行动作，而是把轨迹回传给界面。
    /// 设置页用它实现「按住画一个」。
    /// </summary>
    public void BeginRecord(Action<MouseGesture> done)
    {
        lock (_gate)
        {
            _recordDone = done;
            IsRecording = true;
            _active = false;
            _points.Clear();
            _tracker = null;
        }
    }

    public void CancelRecord()
    {
        lock (_gate)
        {
            _recordDone = null;
            IsRecording = false;
            _tracker = null;
        }
        HideOverlay(0);
    }

    // ───────────────────────── 钩子入口 ─────────────────────────

    /// <summary>
    /// 「按住拖拽」的触发键是不是正按着。按着就把鼠标让给它 —— 一个键一件事，
    /// 不能让两套功能抢同一颗鼠标键（AppHost 里 HoldDrag 比手势晚建，所以要判空）。
    /// </summary>
    private bool HoldDragHoldsTrigger()
    {
        try { return _host.HoldDrag?.IsTriggerHeld == true; }
        catch { return false; }
    }

    /// <summary>
    /// 开了「全屏程序里不接管」时，前台是不是全屏程序。挂在钩子热路径上，
    /// 所以只做几次很轻的 User32 查询，任何异常都当成"不是全屏"。
    /// </summary>
    private bool FullscreenBlocksGestures()
    {
        GestureOptions? o;
        try { o = _host.Settings.GestureOptions; }
        catch { return false; }
        if (o == null || !o.DisableInFullscreen) return false;
        try { return ForegroundIsFullscreen(); }
        catch { return false; }
    }

    /// <summary>判"真全屏"要用的窗口事实。拆成纯数据是为了自检能直接喂，不用真开个游戏。</summary>
    internal readonly record struct FullscreenFacts(string ClassName, bool Zoomed, bool Iconic, int Style);

    /// <summary>
    /// 这个前台窗口算不算"真全屏"（独占全屏 / 无边框全屏）。三条同时成立才算：
    ///
    /// 1) 矩形盖住**整块显示器** —— 只盖住工作区（被任务栏截掉一条）的不算；
    /// 2) **不是最大化** —— 用户要的就是"最大化的窗口里照常能画手势"，
    ///    最大化只铺满工作区，本来就该归普通窗口；
    /// 3) **没有标题栏、也不能拖边框** —— 真全屏和无边框全屏都把这俩去掉了；
    ///    一个带边框的普通窗口就算被手动拉到整屏，也还是普通窗口。
    ///
    /// 桌面（Progman/WorkerW）和任务栏不算 —— 它们铺满，但不是"程序"。
    /// </summary>
    internal static bool IsTrueFullscreen(FullscreenFacts facts, NativeMethods.RECT wr, NativeMethods.RECT monitor)
    {
        if (facts.ClassName is "Progman" or "WorkerW" or "Shell_TrayWnd" or "SHELLDLL_DefView") return false;
        if (facts.Iconic || facts.Zoomed) return false;
        if ((facts.Style & (NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME)) != 0) return false;

        // 允许 2px 误差：无边框全屏常常比屏幕小一两个像素
        return wr.Left <= monitor.Left + 2 && wr.Top <= monitor.Top + 2
            && wr.Right >= monitor.Right - 2 && wr.Bottom >= monitor.Bottom - 2;
    }

    private static bool ForegroundIsFullscreen()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var cls = new StringBuilder(64);
        string className = NativeMethods.GetClassName(fg, cls, cls.Capacity) > 0 ? cls.ToString() : "";

        if (!NativeMethods.GetWindowRect(fg, out NativeMethods.RECT wr)) return false;
        IntPtr mon = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;

        var facts = new FullscreenFacts(className,
            NativeMethods.IsZoomed(fg),
            NativeMethods.IsIconic(fg),
            NativeMethods.GetWindowLong(fg, NativeMethods.GWL_STYLE));
        return IsTrueFullscreen(facts, wr, mi.rcMonitor);
    }

    /// <summary>
    /// 10 秒窗口内的"真起笔"次数（窗口外的旧记录顺手扔掉）。
    /// 口径见 <see cref="OnButton"/>：只有 extent ≥ MinMove 的笔画才会被 <see cref="Finish"/> 记进来。
    /// </summary>
    private int BurstCount(long now)
    {
        lock (_gate)
        {
            while (_gestureBurst.Count > 0 && now - _gestureBurst.Peek() > GestureBurstWindowMs)
                _gestureBurst.Dequeue();
            return _gestureBurst.Count;
        }
    }

    /// <summary>
    /// 指针此刻是不是压在本程序自己的窗口上。
    ///
    /// 这类窗口不参与手势：界面本身要用右键菜单、文本选择、拖动选区，
    /// 被"可能是手势"吞掉再补发，用户看到的就是"软件里右键没反应"。
    /// </summary>
    private static bool CursorOverOwnWindow()
    {
        try
        {
            var (px, py) = CursorPos();
            IntPtr hwnd = NativeMethods.WindowFromPoint(
                new NativeMethods.POINT((int)Math.Round(px), (int)Math.Round(py)));
            return hwnd != IntPtr.Zero && WindowSummonAnimator.IsOwnProcess(hwnd);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "手势：判断指针是否在本程序窗口上");
            return false;
        }
    }

    /// <summary>
    /// 这一笔已经在跟踪中（按下被我们吞了），但抬起时不能再算手势了 ——
    /// 收干净，并**补发一次完整点击**。
    ///
    /// 为什么必须补发：按下被吞掉之后，如果抬起原样漏给系统，前台程序收到的就是一个
    /// "没有按下的 MouseUp" —— 右键菜单永远不会弹，浏览器也不会有任何反应。
    /// 用户看到的就是"鼠标右键失效"。补发一次点击，等效于"这一下从来没被接管过"。
    ///
    /// 返回 true 表示这一下（抬起）由我们处理，调用方直接 return。
    /// </summary>
    private bool AbortTrackingAndReplay(int vk)
    {
        bool mine;
        lock (_gate) mine = _active && _vk == vk;
        if (!mine) return false;

        ResetActive();
        AttachDiag($"  -> 中止手势并补发点击：vk=0x{vk:X2}");
        QueueReplayTap(vk, countAsTap: false);
        return true;
    }

    /// <summary>
    /// 补发一次被我们吞掉的点击 —— **唯一入口**（v1.4.4）。
    ///
    /// 所有"按下已经吞掉、最后没成手势"的分支都必须走这里：起始超时、分段过多、
    /// 抬手丢了、全屏/按住拖拽中止、认不出形状。少一条，那个条件下右键菜单就
    /// 永远打不开 —— 用户报的"任何地方点右键都没反应"正是这么来的。
    /// </summary>
    private void QueueReplayTap(int vk, bool countAsTap = true)
    {
        if (vk == 0) return;
        var (px, py) = CursorPos();
        lock (_gate)
        {
            /* 静默期里同一个键再来（包括被别的鼠标工具复制出来的无标记副本）都放行，
             * 不再当成新手势开头 —— 否则菜单刚弹出来又被第二下关掉。 */
            _replayUntil[vk] = Environment.TickCount64 + 300;
            if (countAsTap)
            {
                /* 记下"这一下其实只是点了一下"：贴边连点两下要能认出角落手势。
                 * 中止掉的真笔画（按住了很久 / 画了一半）不算点击，别去喂这条判定。 */
                _lastTapAt = Environment.TickCount64;
                _lastTapPt = new GestureMath.Pt(px, py);
            }
        }

        /* 补发不能在钩子回调里做：SendInput 会同步把事件送回同一个钩子线程，
         * 回调里再发就自锁（实测要等系统超时 1 秒），表现是"按一下鼠标整台电脑卡一下"。
         * 丢到线程池发。 */
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            long issuedAt = Environment.TickCount64;
            try { InputSender.TapMouseWithHold(vk); }
            catch (Exception ex) { CoreLog.Swallow(ex, "手势补发单击"); }
            VerifyReplayLanded(vk, issuedAt);
        });
    }

    /// <summary>
    /// 补发自检：等一小会儿，看我们自己的钩子有没有看到这对注入事件。
    ///
    /// 连续几次都看不到 → 这台机器上 SendInput 被环境吞掉了（安全软件 / 输入过滤 /
    /// UIPI）。继续"吞掉再补发"只会让鼠标键彻底失效，所以 fail-open：
    /// 置 <see cref="InputSender.InjectionBlocked"/>，之后 OnButton 整下都不接管。
    /// 宁可少一个手势，也不能让右键点不动。
    ///
    /// v1.5.1 起补了两条（2026-11-23 开机后手势全灭的根因）：
    ///   ① **看到就解除熔断** —— 只要有一次补发真的回到了钩子，说明这台机器能注入，
    ///      没理由继续 fail-open；以前只置不清，一次开机瞬间的失败能把整个会话废掉。
    ///   ② **开机 20 秒内不计失败** —— 那时候桌面/前台还没稳定（UAC、安全桌面、
    ///      启动静默期都在这一小段里），把这几秒的失败算进连击只会误判。
    /// </summary>
    private void VerifyReplayLanded(int vk, long issuedAt)
    {
        try
        {
            Thread.Sleep(160);
            if (Interlocked.Read(ref _lastInjectedMouseSeenAt) >= issuedAt)
            {
                Interlocked.Exchange(ref _replayUnobserved, 0);
                bool wasBlocked = InputSender.InjectionBlocked;
                InputSender.ClearInjectionBlocked("补发回到了钩子");
                if (wasBlocked) AttachDiag("  ok 补发回到钩子：解除熔断，手势恢复接管");
                return;
            }

            if (Environment.TickCount64 - _hookStartedAt < BootGraceMs)
            {
                AttachDiag($"  ~~ 开机 {BootGraceMs / 1000}s 内补发没回来（vk=0x{vk:X2}）：不计失败");
                return;
            }

            int misses = Interlocked.Increment(ref _replayUnobserved);
            AttachDiag($"  !! 补发没有回到钩子：vk=0x{vk:X2} 第 {misses}/3 次");
            CoreLog.Warn($"[手势] 补发没有回到钩子（vk=0x{vk:X2}，第 {misses} 次）—— 本次点击可能被系统吞掉");
            if (misses >= 3) InputSender.MarkInjectionBlocked("补发连续 3 次没有回到钩子");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "手势补发自检"); }
    }

    private bool OnButton(int vk, bool down, bool injected)
    {
        /* 自证：我们自己注入的鼠标事件回到钩子了 —— SendInput 在这台机器上是通的。
         * 补发之后靠这个时间戳判断"补发到底有没有生效"（见 VerifyReplayLanded）。 */
        if (injected) Interlocked.Exchange(ref _lastInjectedMouseSeenAt, Environment.TickCount64);

        /* 新的按下 = 上一次那笔无论如何都结束了：残留的"抬起要补发"必须作废。
         * 否则某次抬起丢了之后，下一次由**别的**分支吞掉的抬起会凭空多补一次点击
         * （用户看到的是动作被执行两遍）。 */
        if (down && !injected) Interlocked.Exchange(ref _replayClickOnUpVk, 0);

        /* 立即执行发生在鼠标键还按着的时候。那一下抬起不能放给系统，
         * 否则前台窗口会收到一个没有配对的 MouseUp。 */
        if (!down && !injected && Interlocked.CompareExchange(ref _swallowUpVk, 0, vk) == vk)
        {
            /* 中键菜单：路径 A 的收尾（按下被吞、抬起必须配对吞）。
             * 顺手判"用户其实在划还是只点了一下" —— 这里是唯一收得到这次抬起的地方，
             * 下面 689 行那段要求 _active，路径 A 压根没开过 _active。 */
            MiddleMenuOnSwallowedUp(vk);

            /* 这一下是「按下被吞了、抬起也被吞了」的配对收尾。
             * 如果它是被起始超时 / 分段过多 / 抬手丢了收掉的那一笔，必须补发一次点击 ——
             * 否则用户按得慢一点（≥ StartTimeoutMs，默认 500ms）右键菜单就永远弹不出来。 */
            if (Interlocked.CompareExchange(ref _replayClickOnUpVk, 0, vk) == vk)
            {
                AttachDiag($"  -> 超时/中止的那一笔：抬起配对补发 vk=0x{vk:X2}");
                QueueReplayTap(vk);
            }
            return true;
        }

        if (Suspended) return false;

        /* 中键菜单正开着：这一下中键（按下或抬起）只用来关菜单，绝不漏给底下程序。
         * 放在 HoldDrag / 全屏 / 任务栏之前 —— 菜单是我们自己的窗口，那几个判断对它没意义，
         * 早退会让"再按一下中键关掉"时灵时不灵。 */
        if (MiddleMenuSwallowWhileOpen(vk, down, injected)) return true;

        /* 按住拖拽的触发键正按着：鼠标该归它。用户用那个键已经声明了"这一下是
         * 拖窗口 / 改大小"，右键再被当成手势起笔就成了两套功能抢同一颗键 ——
         * 表现出来就是"按住 Enter 点右键，窗口没改大小，反而画出一道手势"。
         * 这里完全不接管：不吞、不画、不改系统事件，右键菜单照常。 */
        if (HoldDragHoldsTrigger())
        {
            if (!down && AbortTrackingAndReplay(vk)) return true;
            if (IsTracking) ResetActive();
            return false;
        }

        /* 全屏程序里不接管（出厂关）：看视频 / 玩游戏 / 浏览器 F11 时，
         * 画一笔就把桌面动作甩出来太容易误触。 */
        if (FullscreenBlocksGestures())
        {
            /* 按下已经被吞了：这一下的抬起不能只是"放行" —— 前台程序收到一个
             * 没有按下的 MouseUp 什么都不会做。补发整次点击，全屏里右键照常。 */
            if (!down && AbortTrackingAndReplay(vk)) return true;
            if (IsTracking) ResetActive();
            return false;
        }

        /* 任务栏/托盘是 Explorer 自己吃右键的地方：我们一旦把 down 吞掉，
         * 再注入一次右键，Explorer 往往不认（尤其是托盘图标的弹出菜单），
         * 表现为“任务栏闪一下、菜单没出来”。这里干脆不接管任务栏上的右键。 */
        if (down && !injected && IsShellTaskbarAtCursor()) { AttachDiag("  -> 任务栏"); return false; }

        /* 指针压在本程序自己的窗口上：绝不接管（v1.4.3）。
         * 我们的界面（设置页、Qbar、各种对话框）自己要用右键菜单、文本选择、拖动选区；
         * 一旦被当成"可能是手势"吞掉再补发，用户看到的就是"软件里右键没反应"。 */
        if (down && !injected && CursorOverOwnWindow())
        {
            AttachDiag("  -> 指针在本程序窗口上：放行");
            return false;
        }

        lock (_gate)
        {
            if (!down && _chordSwallow == vk) { _chordSwallow = 0; return true; }
        }

        if (injected)
        {
            /* 注入的鼠标键默认不算手势开头 —— 否则“点击中键”这类动作会自己
             * 触发一轮新手势，越点越深。唯一的例外：改键把侧键换成中键时，
             * 那颗中键是我们自己发的，但用户的手指确实按了侧键。这种“改键
             * 回声”由 InputSender 发一张一次性通行证，允许它当手势起点。 */
            if (Environment.TickCount64 < Interlocked.Read(ref _gestureSuppressUntil)) return false;

            /* 通行证只在"按下"时用一次；抬起要去收尾已经开始的这一笔，
             * 不能再要一张新票 —— 否则侧键→中键这种一次按压只发一张票的
             * 场景，按下开始了、抬起却进不来，手势永远收不了尾。 */
            if (down)
            {
                if (!InputSender.TakeGesturePass(vk)) return false;
            }
            else
            {
                bool tracking;
                lock (_gate) tracking = _active && _vk == vk;
                if (!tracking) return false;
            }
        }

        var settings = _host.Settings;
        var opts = settings.GestureOptions;

        bool recording;
        lock (_gate) recording = IsRecording;
        if (recording) return HandleRecordButton(vk, down);
        /* -- 中键（以及被配成菜单键的侧键）在哪儿归我们管（v1.5 修） ----------
         *
         * 「附加菜单的作用范围」只决定**菜单能在哪儿弹**（桌面空白 / 资源管理器 /
         * 文件对话框），不决定这颗键归不归我们。这里以前有一句
         *   `if (down && IsMenuButton(vk) && !MiddleMenuInScope()) return false;`
         * 把两者当成一回事：于是 WPS / Edge / Electron 程序里，**配了中键手势**的按下
         * 在进手势引擎之前就被原样放行了 —— 用户看到的就是"中键手势在这些软件里全没了"。
         *
         * 现在把判断交给下面两条既有出口，分工明确：
         *   · 这颗键在这里配了手势 → 走手势（Candidates 非空那条路，吞下按下开始跟踪）；
         *   · 没配手势 → MiddleMenuTryPlainDown 里问一次"这儿能弹菜单吗"，
         *     不能弹就原样放行（浏览器自动滚动、编辑器/终端的中键粘贴照旧）。
         * 两条路的"单击 vs 滑动"都由 MinMove 分开：
         *   滑动 = 成笔 → 执行手势；单击 = 没成笔 → 范围内弹菜单，范围外补发原生中键。 */

        if (opts is not { Enabled: true })
        {
            /* 中键菜单是**独立功能**，不看这个总开关（见 DECISIONS）。
             * 所以"手势关掉"这条 return 之前要先问一句：这一下是不是空白处的中键点击。
             * 是就吞下按下，抬起时再按"有没有划动"决定弹不弹（路径 A 的入口之一）。 */
            if (MiddleMenuTryPlainDown(vk, settings, hasMiddleGesture: false)) return true;
            if (IsTracking) ResetActive();
            return false;
        }
        if (GestureButtons.FromVk(vk).Length == 0) return false;   // 只处理 5 个鼠标键

        if (down)
        {
            bool active;
            int current;
            bool injectedStart;
            lock (_gate)
            {
                active = _active;
                current = _vk;
                injectedStart = _activeInjected;
            }

            /* 抬起事件丢了（UAC 安全桌面、锁屏、钩子被摘掉）会留下 _active。
             * 这时如果再来一次同一颗键的按下，抬起会被当成"上一笔的收尾"
             * 吞掉，系统只看到按下、看不到抬起 —— 右键菜单就不弹了。
             * 同一颗鼠标键在还没收笔时又按下，只可能是上一笔的抬起丢了：
             * 鼠标键没有自动重复，组合手势按的也是另一颗键。直接收干净重来。 */
            if (active && current == vk && !injectedStart)
            {
                ResetActive();
                lock (_gate)
                {
                    active = _active;
                    current = _vk;
                }
            }

            lock (_gate)
            {
                /* 刚补发出去的那一下会带着别人的标记回来（鼠标增强工具会把
                 * 事件复制一份重发），别再当成新手势的开头，原样交给系统。 */
                if (_replayUntil.TryGetValue(vk, out long until))
                {
                    /* 静默期里再来的一颗，多半是鼠标增强工具复制我们补发的那一下
                     * （标记被抹掉）。放过去的话系统会看到两次右键，
                     * 菜单刚弹出来又被第二下关掉。 */
                    if (Environment.TickCount64 <= until) return false;
                    _replayUntil.Remove(vk);
                }
            }

            if (active)
            {
                /* 画到一半又按了第二颗鼠标键：先当"组合手势"试一次
                 * （WGestures 的「按住右键再点左键」就是这种）。
                 * 对不上就把这颗键原样交给系统，别把右键菜单之类的弄丢。 */
                if (TryChord(settings, current, vk, opts)) return true;
                return false;
            }

            /* 「手势作用目标」决定这一笔算在哪个程序头上、执行前要不要切窗口：
             * 指针下方 = 画在哪就用哪；活动窗口 = 只看当前焦点，不切窗口。 */
            var (app, targetWindow) = ResolveTarget(opts);
            /* 用户在「作用目标」里给某个程序点了“禁用手势”：
             * 这个程序里连全局手势也不触发，右键/侧键原样交给系统。 */
            if (IsTargetDisabled(app)) { AttachDiag("  -> IsTargetDisabled " + app); return false; }

            var (px, py) = CursorPos();
            int cornerAt = CornerAt(px, py, opts.CornerSize);

            /* 「{作用目标} 允许的触发方式」—— 这就是 WGestures 2 目标页那一栏。
             * 某个按键在"当前这个作用目标"上被关掉，就等同于这个键不参与手势：
             * 完全不吞、原样交给系统。判定顺序见 GestureTriggerMap.Allows：
             * 特殊目标（任务栏→桌面）→ 应用程序 → 所属分组 → 全局。 */
            var specials = SpecialTargetsAt();
            var triggers = new GestureTriggerMap(opts);
            if (triggers.IsBlocked(app, specials.Desktop, specials.Taskbar)) { AttachDiag("  -> triggers.IsBlocked"); return false; }
            string triggerKey = TriggerKeyOf(vk, cornerAt);
            if (triggerKey.Length > 0 &&
                !triggers.Allows(app, specials.Desktop, specials.Taskbar, triggerKey))
                return false;

            /* 这里**不能**带 includeWheel：一条"右键 + 滚轮"的手势不应该让
             * 每次右键按下都被吞掉。那种手势在 OnWheel 里靠"物理键是否按下"
             * 识别，不需要开一笔轨迹；否则普通右键也要靠补发合成点击，
             * 在提权窗口/安全软件下就表现为"右键偶尔没反应"。
             *
             * 同理，只有"这一次按下去真有可能触发"的手势才值得吞：
             *   · 单击手势必须开了「单击也触发」；
             *   · 角落手势必须指针真的在对应角落；
             *   · 笔画手势任何位置都可能画，保留。
             * 否则一条用不到的单击/角落手势会让每次右键都走"吞掉 → 注入补发"，
             * 在管理员窗口里补发被 UIPI 丢掉，右键菜单就再也弹不出来。 */
            /* 边缘双击转角落：贴边连点两下也认那条角落手势。
             * 这时指针在边附近、不一定落在角里，所以先把角落手势放进候选。 */
            bool edgeDoubleTap = IsEdgeDoubleTap(px, py, opts);
            var candidates = Candidates(settings, vk, app, includeWheel: false)
                .Where(g => CanStart(g, opts, cornerAt) || (edgeDoubleTap && g.Kind == "corner"))
                .ToList();
            if (candidates.Count == 0)
            {
                /* 路径 A 的第二个入口：手势总开关开着，但用户**没给这颗键配任何手势**。
                 * 以前这里直接把中键放行（浏览器自动滚动就靠这条）。现在先问一句
                 * "空白处的中键要不要弹菜单"，不要才照旧放行。 */
                if (MiddleMenuTryPlainDown(vk, settings, hasMiddleGesture: false)) return true;
                return false;
            }

            /* 补发这条路根本走不通（SendInput 被环境吞掉 / 受限）：整下都不接管（fail-open）。
             * 绝不能吞了用户又还不出一次点击 —— 那正是"右键在任何地方都没反应"。
             * v1.4.4：判定提前到**开笔之前**，不再先画一次覆盖层再退回（省一次闪窗）。
             * v1.5.1：这是**限时**熔断（15s 起、逐次加长、成功即解除），不是永久失效 ——
             * 以前一次开机瞬间的失败会把整个会话的手势废掉，日志里全是这一句。 */
            if (InputSender.InjectionBlocked)
            {
                AttachDiag("  -> 补发熔断中：按下原样放行（到点会自己恢复）");
                return false;
            }

            /* 防止乱画：同一时间窗里连续**真起笔**太多次，就暂停识别一会儿。
             *
             * ⚠ 这里只「暂停识别手势」，**绝不吞这一下**（v1.4.3 修）。以前两个分支都是
             * `_swallowUpVk = vk; return true` —— 按下和抬起一起吞掉、还不补发，
             * 用户看到的就是"鼠标右键（左键也一样）整个没反应、菜单也不弹"，
             * 而且越点越没反应：每次点击都进这个分支，3 秒冷却里全被吃掉。
             *
             * 统计口径也一起改了：只有**真画了一笔**（extent ≥ MinMove，见 Finish）
             * 才计数。普通点击不再往窗口里堆 —— 否则连点二十下右键就把自己送进冷却。
             */
            long burstNow = Environment.TickCount64;
            if (burstNow < _gestureBurstBlockedUntil)
            {
                ShowToast("手势太频繁，已达到限制", px, py);
                return false;   // 放行：这一下交给系统，右键菜单照常弹
            }
            if (BurstCount(burstNow) >= GestureBurstLimit)
            {
                lock (_gate) _gestureBurst.Clear();
                _gestureBurstBlockedUntil = burstNow + GestureBurstCooldownMs;
                ShowToast("手势太频繁，已达到限制", px, py);
                return false;   // 同样放行
            }

            /* 路径 B：中键真要开始一笔手势了。趁按下这一刻把"空白命中"算好记下来 ——
             * 松手时才知道这一笔算不算"没成笔"，而那时不能再等跨进程查询（用户要求点了就出来）。
             * 放在 lock 外面：命中判定最长可能等 UIA 90ms，不能抱着 _gate 等。 */
            MiddleMenuArm(vk, settings, hasMiddleGesture: true);

            lock (_gate)
            {
                _active = true;
                _activeSince = Environment.TickCount64;
                _lastMoveAt = _activeSince;
                _activeInjected = injected;
                _previewTitle = "";
                _previewCount = 0;
                _vk = vk;
                _points.Clear();
                _start = new GestureMath.Pt(px, py);
                _last = _start;
                _points.Add(_start);
                _appPath = app;
                _targetWindow = targetWindow;
                /* 起笔在开始菜单/搜索上：先记下来，等指针真动起来再收（见 MaybeDismissShell）。
                 * 这里同时问一次前台 —— 有些版本上光标命中的是桌面 Progman，ResolveTarget
                 * 会退回前台窗口，而前台窗口正是 SearchHost。 */
                _shellOverlayUp = ShellOverlay.IsUp(targetWindow) || ShellOverlay.IsUp();
                _shellOverlayDismissed = false;
                _shellOverlayAt = 0;
                _cornerCandidate = CornerCandidate(candidates, px, py, opts.CornerSize)
                    ?? (edgeDoubleTap ? CornerCandidateFor(candidates, NearestCorner(px, py)) : null);
                /* 这一次按下有可能做成多步手势：OnPoll 就会按"停顿"切步骤。 */
                _stepsDrawn.Clear();
                _segStart = 0;
                _multiExpected = candidates.Any(g => g.HasSteps);
                _tracker = new GesturePattern.Tracker(opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance);
                _tracker.Start(px, py);
            }

            var o = opts;
            /* 上一笔排的「停留后收起 / 淡出后收起」到这里一律作废。
             * 不停的话：连着划两下时，上一笔 100~350ms 的淡出定时器正好在
             * 第二笔中途到点，把第二笔的覆盖层 Clear()+Hide() —— 轨迹和名字
             * 一个像素都不剩，动作却照常执行，就是用户说的「滑了但没显示出来」。 */
            BeginStrokeVisual();
            Ui(() =>
            {
                var overlay = EnsureOverlay();
                overlay.Begin(o, GestureButtons.FromVk(vk));
                overlay.ShowPassive();
                overlay.AddPoint(px, py);
                StartPoll(o);
            });

            /* 按下必须**吞掉**（WGestures 也是这么做的：按下先接管，松手再决定这一下算什么）。
             *
             * 关键在于两头必须**配对**：按下吞了，抬起也吞；确认只是点了一下，就补发一次
             * 完整的点击（down + up，见 Finish 里的 TapMouseWithHold）。这样系统的按键状态
             * 始终自洽 —— 而"只吞一头"是致命的：放进按下、吞掉抬起，
             *   · 系统一直以为右键还按着（`GetAsyncKeyState` 永远是 down）；
             *   · 目标程序卡在拖拽态；
             *   · 我们自己的轮询也以为这一笔没结束，于是每 8ms 往轨迹里加点、
             *     每 200ms 把覆盖层重新顶到最前 —— 用户看到的就是
             *     "滑完之后轨迹留在屏幕上，还一直跟着鼠标动，非再按一下右键不可"。
             * 这一版现场抓到的正是这种"吞了一头"的残局。
             *
             * 例外：补发如果根本发不出去（受限环境 / 安全软件拦 SendInput，
             * 见 InputSender.InjectionBlocked），那就整下都不接管 —— 绝不能吞了用户又还不出一次点击。 */
            /* 例外：补发如果根本发不出去（受限环境 / 安全软件拦 SendInput，
             * 见 InputSender.InjectionBlocked），那就整下都不接管 —— 绝不能吞了用户又还不出一次点击。
             * 那个判断已经提前到开笔之前（v1.4.4），这里只管吞。 */
            return true;
        }

        lock (_gate)
        {
            if (!_active || vk != _vk) return false;
        }

        Finish(execute: true);
        return true;
    }

    /// <summary>按住 <paramref name="primaryVk"/> 时又点了 <paramref name="chordVk"/>：按组合手势处理。</summary>
    private bool TryChord(AppSettings settings, int primaryVk, int chordVk, GestureOptions opts)
    {
        string chordName = GestureButtons.FromVk(chordVk);
        if (chordName.Length == 0) return false;

        string app;
        var drawn = new List<GestureMath.Pt>();
        lock (_gate)
        {
            app = _appPath;
            drawn.AddRange(_points);
        }

        /* 还没起手就按第二颗键，不能当成组合手势：那只是用户起手时手抖带了一下别的键。
         * "起手"看指针走了多远（起手距离），不再看方向串 —— v5.13 起没有方向串判定。 */
        if (GestureMath.Extent(drawn) < opts.ActivationDistance) return false;

        var sameKey = Candidates(settings, primaryVk, app, includeWheel: false, includeChords: true)
            .Where(g => string.Equals(g.Chord, chordName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sameKey.Count == 0) return false;

        /* 识别还是那一套：这一笔的形状和"同一颗第二键"的候选比，最像的那条就是它。
         * 通配（"*"）没有形状，跟 PickStroke 一样只当最后兜底。 */
        foreach (var g in sameKey) g.EnsurePattern();
        var shaped = sameKey.Where(g => !g.IsWildcard).ToList();
        MouseGesture? hit = shaped.Count > 0 ? GestureEngine.Match(shaped, drawn, opts).Hit : null;
        hit ??= sameKey.FirstOrDefault(g => g.IsWildcard);

        if (hit == null) return false;

        lock (_gate)
        {
            _active = false;
            _tracker = null;
            _chordSwallow = chordVk;
            /* 组合手势在第二颗键按下时就执行，此时起始键还按着。
             * 它的抬起也必须吞掉，否则系统会收到一个没有按下的 MouseUp。 */
            _swallowUpVk = primaryVk;
        }
        var (cx, cy) = CursorPos();
        Execute(hit, cx, cy);
        SettleOverlay(hit, cx, cy);
        CoreLog.Info($"组合手势命中：{hit.Title(ActionCatalogTitle(hit))}");
        return true;
    }

    private bool HandleRecordButton(int vk, bool down)
    {
        if (down)
        {
            var (px, py) = CursorPos();
            lock (_gate)
            {
                _active = true;
                _vk = vk;
                _points.Clear();
                _start = new GestureMath.Pt(px, py);
                _last = _start;
                _points.Add(_start);
                var recordOpts = _host.Settings.GestureOptions;
                _tracker = new GesturePattern.Tracker(recordOpts.ActivationDistance, recordOpts.TurnTolerance, recordOpts.MaxTurnDistance);
                _tracker.Start(px, py);
            }
            var opts = _host.Settings.GestureOptions;
            BeginStrokeVisual();
            Ui(() =>
            {
                var overlay = EnsureOverlay();
                overlay.Begin(opts, GestureButtons.FromVk(vk));
                overlay.ShowPassive();
                overlay.AddPoint(px, py);
                StartPoll(opts);
            });
            return true;
        }

        lock (_gate)
        {
            if (!_active || vk != _vk) return false;
        }
        Finish(execute: false);
        return true;
    }

    private bool OnWheel(int direction, bool injected, bool horizontal)
    {
        if (Suspended) return false;
        if (injected) return false;

        /* 触发键按着的时候滚轮也归按住拖拽（它拿滚轮调透明度之类），
         * 不在这里起"滚轮手势"。 */
        if (HoldDragHoldsTrigger())
        {
            if (IsTracking) ResetActive();
            return false;
        }

        /* 全屏程序里不接管（出厂关）：看视频 / 玩游戏 / 浏览器 F11 时，
         * 画一笔就把桌面动作甩出来太容易误触。 */
        if (FullscreenBlocksGestures())
        {
            if (IsTracking) ResetActive();
            return false;
        }

        var settings = _host.Settings;
        if (settings.GestureOptions is not { Enabled: true })
        {
            if (IsTracking) ResetActive();
            return false;
        }

        int wheel = horizontal ? (direction > 0 ? 2 : -2) : (direction > 0 ? -1 : 1);
        var (cx, cy) = CursorPos();
        var (app, wheelTarget) = ResolveTarget(settings.GestureOptions);
        if (IsTargetDisabled(app))
        {
            if (IsTracking) ResetActive();
            return false;
        }

        bool active;
        int vk;
        string activeApp;
        lock (_gate)
        {
            active = _active;
            vk = _vk;
            activeApp = _appPath;
        }

        if (active)
        {
            /* 多步手势里的「滚一下」：记一步就够，这一下滚轮不当作动作执行。 */
            if (_multiExpected)
            {
                lock (_gate)
                {
                    if (_stepsDrawn.Count < GestureStep.MaxSteps)
                        _stepsDrawn.Add(new GestureStep { Kind = "scroll", Wheel = wheel });
                    _segStart = _points.Count;
                }
                return true;
            }

            var hit = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "wheel" && g.Wheel == wheel &&
                GestureButtons.ToVk(g.Button) == vk && AppMatches(g, activeApp, settings.GestureOptions));

            if (hit == null) return false;

            Execute(hit, cx, cy);
            Finish(execute: false, keepOverlayFor: hit.HideLabel ? null : hit.Title(ActionCatalogTitle(hit)));
            return true;
        }

        /* "角落 + 滚轮"本来就不用按任何键：把手贴到屏幕角上拨一下滚轮。
           WGestures 里音量/亮度那几条就是这个形状，导入后 Button 留空，
           所以这里单独给"没按住键"的情况开一条路。 */
        int corner = CornerAt(cx, cy, settings.GestureOptions.CornerSize);
        if (corner != 0)
        {
            var cornerHit = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "wheel" && g.Wheel == wheel &&
                string.IsNullOrWhiteSpace(g.Button) &&
                (g.Corner == 0 || g.Corner == corner) &&
                AppMatches(g, app, settings.GestureOptions));

            if (cornerHit != null)
            {
                SetActionTarget(wheelTarget);
                Execute(cornerHit, cx, cy);
                ShowLabelOnly(cornerHit, cx, cy);
                return true;
            }
        }

        /* 贴边滚轮（WGestures 的 ScreenEdge 玩法）：指针在屏幕最上沿时
         * 滚轮调音量、最下沿调亮度。 */
        int edge = EdgeAt(cx, cy, settings.GestureOptions.EdgeSize);
        if (edge != 0)
        {
            var edgeHit = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "edgewheel" && g.Edge == edge && g.Wheel == wheel &&
                AppMatches(g, app, settings.GestureOptions));

            if (edgeHit != null)
            {
                SetActionTarget(wheelTarget);
                Execute(edgeHit, cx, cy);
                ShowLabelOnly(edgeHit, cx, cy);
                return true;
            }
        }

        /* 按住某颗鼠标键再滚：按下没有被吞（上面 OnButton 不再为纯滚轮手势
         * 开轨迹），所以这里直接看物理键状态，不依赖 _active。 */
        foreach (int buttonVk in MouseButtonVks)
        {
            if (!InputSender.IsPhysicallyDown(buttonVk)) continue;

            var held = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "wheel" && g.Wheel == wheel &&
                GestureButtons.ToVk(g.Button) == buttonVk && AppMatches(g, app, settings.GestureOptions));

            if (held == null) continue;

            SetActionTarget(wheelTarget);
            Execute(held, cx, cy);
            ShowLabelOnly(held, cx, cy);
            return true;
        }

        return false;
    }

    private static readonly int[] MouseButtonVks =
    {
        0x01, 0x02, 0x04, 0x05, 0x06
    };

    /// <summary>把这一笔的目标窗口记下来；<see cref="IntPtr.Zero"/> = 就用当前焦点窗口。</summary>
    private void SetActionTarget(IntPtr target)
    {
        lock (_gate) _targetWindow = target;
    }

    /// <summary>这一笔不切窗口，动作发给当前前台窗口。</summary>
    private void ClearActionTarget() => SetActionTarget(IntPtr.Zero);

    private void StartHotWatch()
    {
        try
        {
            _hotWatch ??= new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _hotWatch.Tick -= OnHotWatch;
            _hotWatch.Tick += OnHotWatch;
            _hotWatch.Start();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "启动热角检测"); }
    }

    /// <summary>
    /// 重新注册「鼠标手势总开关」热键。设置为空就只注销，不报错。
    /// 由设置页在录制完快捷键后调用，也在 Attach 时调用一次。
    /// </summary>
    public void ApplyHotkey()
    {
        try
        {
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(ApplyHotkey));
                return;
            }

            _hotkeySource ??= CreateHotkeySource();
            if (_hotkeySource == null) return;

            UnregisterHotKey(_hotkeySource.Handle, ToggleHotkeyId);

            /* 全局暂停时不占热键；恢复到 Enabled 时 ApplyHotkey 会再注册回来。 */
            if (_suspended) return;

            string combo = Settings().GestureOptions.ToggleHotkey ?? "";
            var parsed = ParseHotkey(combo);
            if (parsed == null) return;

            if (!RegisterHotKey(_hotkeySource.Handle, ToggleHotkeyId,
                    parsed.Value.Mods | MOD_NOREPEAT, (uint)parsed.Value.Vk))
                CoreLog.Warn($"注册鼠标手势开关热键失败：{combo}");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "注册鼠标手势开关热键"); }
    }

    private HwndSource? CreateHotkeySource()
    {
        try
        {
            var parameters = new HwndSourceParameters("InputPlusGestureHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            var source = new HwndSource(parameters);
            source.AddHook(HotkeyWndProc);
            return source;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "创建手势热键窗口");
            return null;
        }
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == ToggleHotkeyId)
        {
            ToggleGestures();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleGestures()
    {
        var options = Settings().GestureOptions;
        options.Enabled = !options.Enabled;
        if (!options.Enabled) ResetActive();
        _host.SaveSettings();
        _host.Notify(options.Enabled ? Loc.T("鼠标手势已开启") : Loc.T("鼠标手势已关闭"));
        CoreLog.Info($"鼠标手势总开关：{(options.Enabled ? "开" : "关")}");
    }

    private static (uint Mods, int Vk)? ParseHotkey(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        uint mods = 0;
        int vk = 0;
        foreach (string raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= MOD_CONTROL;
                    continue;
                case "shift":
                    mods |= MOD_SHIFT;
                    continue;
                case "alt":
                case "menu":
                    mods |= MOD_ALT;
                    continue;
                case "win":
                case "windows":
                case "lwin":
                    mods |= MOD_WIN;
                    continue;
            }
            int? parsed = KeyName.VkOf(part);
            if (parsed == null || parsed.Value == 0) return null;
            vk = parsed.Value;
        }
        return vk == 0 ? null : (mods, vk);
    }

    /// <summary>
    /// 悬停到屏幕角触发（WGestures 的 HotCorner）。
    ///
    /// 只在"没有按键、没有在画手势、指针真的停在角上"时触发；进入热角时
    /// 如果按着鼠标键（拖窗口贴角），先解除武装，必须离开热角重置区才允许
    /// 再触发 —— 否则把窗口拖到左上角松手就会误触。
    /// </summary>
    private void OnHotWatch(object? sender, EventArgs e)
    {
        try
        {
            var settings = _host.Settings;
            var opts = settings.GestureOptions;
            if (opts is not { Enabled: true } || Suspended || IsRecording || IsTracking) return;

            var (px, py) = CursorPos();
            var (app, hotTarget) = ResolveTarget(opts);
            if (IsTargetDisabled(app)) return;
            int corner = CornerAt(px, py, opts.HotCornerSize);

            if (corner != 0 && AnyMouseButtonDown())
            {
                _hotArmed = false;
                return;
            }

            if (!_hotArmed)
            {
                if (CornerAt(px, py, opts.HotCornerReset) == 0) _hotArmed = true;
                return;
            }

            if (corner == 0) return;

            var hit = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "hotcorner" &&
                (g.Corner == 0 || g.Corner == corner) &&
                AppMatches(g, app, settings.GestureOptions));

            if (hit == null) return;

            _hotArmed = false;
            SetActionTarget(hotTarget);
            Execute(hit, px, py);
            ShowLabelOnly(hit, px, py);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "热角触发"); }
    }

    private static bool AnyMouseButtonDown()
    {
        foreach (int vk in MouseButtonVks)
            if (InputSender.IsPhysicallyDown(vk)) return true;
        return false;
    }

    // ───────────────────────── 采样与收尾 ─────────────────────────

    /// <summary>丢掉当前这一笔（抬起丢了、选项被关掉、进入录制模式）。</summary>
    /// <summary>
    /// 在开始菜单 / 搜索上起笔时，把浮层收起来再继续画。
    ///
    /// 触发条件只有两个：起笔时前台确实是 shell 浮层（_shellOverlayUp），
    /// 且指针已经划过激活距离 —— 不划过就收，等于"点一下开始菜单它自己关了"。
    /// 收的动作交给 <see cref="ShellOverlay.Dismiss"/>，这里只负责只收一次、
    /// 并安排一次「重算作用目标」：收起后光标下面已经是别的窗口了。
    /// </summary>
    private void MaybeDismissShell(double extent)
    {
        if (!_shellOverlayUp || _shellOverlayDismissed) return;

        var opts = Settings().GestureOptions;
        if (extent < Math.Max(8, opts.ActivationDistance)) return;

        _shellOverlayDismissed = true;
        _shellOverlayAt = Environment.TickCount64;

        if (!ShellOverlay.Dismiss())
        {
            /* 前台已经自己换掉了：什么都不用做，轨迹本来就是可见的。 */
            _shellOverlayUp = false;
            return;
        }

        CoreLog.Info("[手势] 在开始菜单/搜索上起笔：先收起浮层，轨迹才看得见");
        _ = RetargetAfterShellDismiss();
    }

    /// <summary>
    /// 收起 shell 浮层之后重新认一次"这一笔要作用在哪个窗口"。
    ///
    /// 为什么不能沿用起笔时那个：起笔时光标在开始菜单上，ResolveTarget 认到的是
    /// SearchHost.exe；收起之后同一个位置是桌面或某个应用窗口，那才是用户想操作的东西。
    /// 用 120ms 的延迟等切换落定（开始菜单的收起动画约 100ms），再在后台线程问一次 ——
    /// 不能阻塞 8ms 的轮询，否则轨迹会一顿一顿。
    /// </summary>
    private async Task RetargetAfterShellDismiss()
    {
        try
        {
            await Task.Delay(120).ConfigureAwait(false);

            string app;
            lock (_gate)
            {
                if (!_active) return;      // 用户已经松手/取消了，别再改
            }

            (app, IntPtr targetWindow) = ResolveTarget(Settings().GestureOptions);
            lock (_gate)
            {
                if (!_active) return;
                _appPath = app;
                _targetWindow = targetWindow;
            }

            /* 前台切换会让别的窗口重新置顶一次，刚抬起来的轨迹窗可能又沉下去。 */
            Ui(() => _overlay?.ShowPassive());

            CoreLog.Info($"[手势] 收起浮层后改认目标：{app}");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "手势改认目标"); }
    }

    private void ResetActive()
    {
        Interlocked.Exchange(ref _swallowUpVk, 0);
        Interlocked.Exchange(ref _replayClickOnUpVk, 0);
        lock (_gate)
        {
            _active = false;
            _activeInjected = false;
            _tracker = null;
            _cornerCandidate = null;
            _previewTitle = "";
            _previewCount = 0;
            _stepsDrawn.Clear();
            _keysDown.Clear();
            _segStart = 0;
            _multiExpected = false;
            _points.Clear();
            _chordSwallow = 0;
            /* 中键那半截"点击"状态跟着一起清：抬起丢了（安全桌面/锁屏）之后
             * 残留的 pending 会让下一次中键抬起读到上一笔的命中结论。 */
            _middleTap = default;
            _targetWindow = IntPtr.Zero;
            _shellOverlayUp = false;
            _shellOverlayDismissed = false;
            _shellOverlayAt = 0;
        }
        _poll?.Stop();
        HideOverlay(0);
    }

    /// <summary>
    /// 覆盖层看门狗：**不在跟踪状态**却还有可见的轨迹/名称框 —— 直接收掉；
    /// 还在跟踪、但物理键早就松开了 —— 说明抬起丢了，把这一笔收干净。
    ///
    /// 为什么必须有它：收尾路径有四五条（命中淡出 / 未命中 / 起始超时 / 立即执行 / 抬起丢失），
    /// 每条都靠 `epoch + IsTracking` 那对条件判断该不该收。只要有一条漏了、或者某个
    /// 定时器被别处 Stop 掉，轨迹就会赖在屏幕上跟着鼠标走，直到下一次起手才被清掉
    /// （用户 2026-11-22 报的就是这个：滑完一直跟着鼠标，非再按一下右键不可）。
    /// 这条看门狗**不依赖任何状态机**，100ms 一次，代价可以忽略。
    ///
    /// 顺带：按下已经不再被我们吞掉（见 OnButton），所以这里可以直接问系统
    /// "这颗键还按着吗" —— 以前查不准正是因为它被吞了。
    /// </summary>
    private void OnOverlayGuard(object? sender, EventArgs e)
    {
        try
        {
            if (IsTracking)
            {
                /* 按下重新被吞掉之后，`GetAsyncKeyState` 查不出真实状态（系统队列里根本没有这次按下），
                 * 所以这里只按"多久没动"兜底：一笔手势停 8 秒还没松开，必然是一次丢失的抬起。 */
                long last;
                int vk;
                lock (_gate) { last = _lastMoveAt; vk = _vk; }
                if (vk != 0 && Environment.TickCount64 - last > 8000)
                {
                    AttachDiag($"guard: 抬手丢了，收掉这一笔 vk=0x{vk:X2}");
                    /* 按下早被我们吞了。抬起既然没回来，这一下就必须还回去 ——
                     * 以前这里只 ResetActive，右键/左键的这一次点击就彻底消失了。
                     * 物理键还按着就先记着，等它真抬起来再补（不要凭空多一次按下）。 */
                    bool stillDown = InputSender.IsPhysicallyDown(vk);
                    ResetActive();
                    if (stillDown) Interlocked.Exchange(ref _replayClickOnUpVk, vk);
                    else if (!IsMenuButton(vk)) QueueReplayTap(vk, countAsTap: false);
                }
                return;
            }

            var overlay = _overlay;
            if (overlay == null || !overlay.IsVisible) return;
            AttachDiag("guard: 收掉残留的轨迹/名称框");
            overlay.Clear();
            overlay.Hide();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "覆盖层看门狗"); }
    }

    private void StartOverlayGuard()
    {
        if (_overlayGuard != null) return;
        _overlayGuard = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _overlayGuard.Tick += OnOverlayGuard;
        _overlayGuard.Start();
    }

    private void StartPoll(GestureOptions opts)
    {
        _poll ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        _poll.Tick -= OnPoll;
        _poll.Tick += OnPoll;
        _poll.Start();
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        bool stale;
        lock (_gate)
        {
            /* 不能用 GetAsyncKeyState 判断“键还按着没有”：手势起点键的
             * WM_*BUTTONDOWN 已经被我们吞掉，系统队列里根本没有这次按下，
             * 查询结果会是 0。以前这里一进 OnPoll 就把刚开的一笔当成
             * “抬起丢了”清掉，表现就是右键画不出任何手势。
             * 现在只保留 8 秒超时兜底；抬起丢失主要由下一次同键按下复位，
             * 以及 Suspended/禁用时的 ResetActive 处理。 */
            stale = _active && Environment.TickCount64 - _activeSince > 8000;
        }
        if (stale)
        {
            /* 8 秒还没收笔 = 抬起丢了，或者用户真的按着不放。
             * 按下已经吞掉了：这一下必须还回去（物理键还按着就等它抬起来再补），
             * 否则这一次点击就这么凭空消失。 */
            int lostVk;
            lock (_gate) lostVk = _vk;
            bool stillDown = lostVk != 0 && InputSender.IsPhysicallyDown(lostVk);
            /* 中键的命中结论要在 ResetActive 之前取走 —— 它会把 _middleTap 一起清掉。 */
            bool menuTook = lostVk != 0 && !stillDown && MiddleMenuOnSilentCancel(lostVk, 0, 0);
            ResetActive();
            if (lostVk != 0)
            {
                if (stillDown) Interlocked.Exchange(ref _replayClickOnUpVk, lostVk);
                /* 抬起真的丢了（键已经松了）：中键不能只是"没反应" —— 菜单没接手就补发一次，
                 * 和右键走同一条兜底（v1.5）。 */
                else if (!menuTook) QueueReplayTap(lostVk, countAsTap: false);
            }
            return;
        }

        var (px, py) = CursorPos();
        bool add;
        bool tooManySegments = false;
        bool startTimedOut = false;
        string pattern;
        int vk;
        string app;
        int minMove = Settings().GestureOptions.MinMove;
        int timeoutMs = Math.Clamp(Settings().GestureOptions.StartTimeoutMs, 100, 3000);
        List<GestureMath.Pt>? snapshot = null;
        double extent = 0;
        lock (_gate)
        {
            if (!_active) return;
            extent = GestureMath.Extent(_points);
            double dx = px - _last.X, dy = py - _last.Y;
            add = dx * dx + dy * dy >= 4;   // 2px 一处，够画圆也够省内存
            long now = Environment.TickCount64;
            if (add)
            {
                _last = new GestureMath.Pt(px, py);
                _points.Add(_last);
                _lastMoveAt = now;
            }
            if (add) _ = _tracker?.Move(px, py);

            /* 多步手势：停 300ms 就算"这一步做完了"，把这一小段切出来存成一步。
             * 单笔手势不进来 —— 它的整条轨迹还要原样比形状。 */
            if (_multiExpected && !add && now - _lastMoveAt >= StepPauseMs)
                CloseStepLocked(px, py, minMove);

            pattern = _tracker?.Pattern ?? "";
            tooManySegments = pattern.Length >= GestureSegmentLimit;
            startTimedOut = !add
                && now - _lastMoveAt >= timeoutMs
                && GestureMath.Extent(_points) < minMove;
            vk = _vk;
            app = _appPath;
            /* 每多记 3 个点判一次：画一条直线时也不会只在起手那一下判过一次。
             * 长度门槛由 UpdatePreview / TryImmediate 自己按 MinMove 卡。 */
            if (_points.Count - _previewCount >= 3)
            {
                _previewCount = _points.Count;
                snapshot = new List<GestureMath.Pt>(_points);
            }
        }

        if (tooManySegments)
        {
            ResetActive();
            Interlocked.Exchange(ref _swallowUpVk, vk);
            /* 按下吞了、抬起也吞了 —— 这一下必须补发，否则"划太多"的代价是这颗键彻底没反应。
             * 唯一的例外：中键（菜单键）手里还握着一份"可弹"的记录，那松手时由
             * MiddleMenuOnSwallowedUp 弹菜单，不能再补一次原生中键（否则菜单 + 自动滚动一起出现）。 */
            if (!(IsMenuButton(vk) && MiddleMenuTapPending()))
                Interlocked.Exchange(ref _replayClickOnUpVk, vk);
            ShowToast("手势划动太多，已达到限制", px, py);
            return;
        }

        if (startTimedOut)
        {
            /* 中键菜单：按下后半天不动，这一笔同样"没成笔"。慢按一次中键也要算一次点击 ——
             * 少了这一句，慢按的观感是"菜单不弹、原生中键还被吞了"，最难解释。
             * 必须在 ResetActive 之前问：它会把 _middleTap 一起清掉。 */
            bool menuTook = MiddleMenuOnSilentCancel(vk, px, py);
            ResetActive();
            Interlocked.Exchange(ref _swallowUpVk, vk);
            /* ★ 右键失效的主因就在这里（v1.4.4 修）：
             * 起始超时把这一笔收掉了，但按下已经被我们吞掉。以前抬起照样吞掉、
             * **什么都不补发** —— 于是「按住右键超过 500ms 再松开」等于右键从来没按过，
             * 任何程序都不会有反应。现在标记成"抬起时补发一次点击"。
             * v1.5：中键也要补 —— 只有菜单真的接手了（menuTook）才不补，
             * 否则"在浏览器/WPS 里按住中键稍久一点"就又变成什么都没发生。 */
            if (!menuTook) Interlocked.Exchange(ref _replayClickOnUpVk, vk);
            ShowTimeoutDot(px, py);
            CoreLog.Info("手势起始超时");
            return;
        }

        if (!add) return;

        /* 开始菜单/搜索上起笔：指针一过激活距离就先收浮层，见 MaybeDismissShell。 */
        MaybeDismissShell(extent);

        /* 覆盖层每 200ms 重新顶一次最前：画的过程中别的程序可能把自己置顶
         * （开始菜单那种高带窗口、刚被激活的置顶窗体），一压下去整条轨迹就看不见了。
         * SetWindowPos 只改 Z 序、不改窗口状态，8ms 轮询里按 200ms 节流。 */
        long raiseNow = Environment.TickCount64;
        if (raiseNow - _lastRaiseAt >= RaiseIntervalMs)
        {
            _lastRaiseAt = raiseNow;
            _overlay?.ShowPassive();
        }

        if (Settings().GestureOptions.ShowPath)
            _overlay?.AddPoint(px, py);

        /* 画到能认出来就先显示名称，并且随形状变化实时换；
         * 真正命中/松手时再用最终名称覆盖（名字和轨迹一起留到点，见 ShowLabel）。 */
        if (snapshot is not null) UpdatePreview(vk, app, snapshot, px, py);

        /* 这一条开了「识别手势立即执行」：这一笔刚够像某条手势就直接执行，
         * 不再等鼠标抬起。覆盖层只显示最终手势名。 */
        if (snapshot is not null && TryImmediate(vk, app, snapshot, px, py)) return;
    }

    private void UpdatePreview(int vk, string app, List<GestureMath.Pt> points, double px, double py)
    {
        var opts = Settings().GestureOptions;
        if (GestureMath.Extent(points) < opts.MinMove) return;

        var hit = PickStroke(Settings(), vk, app, points, opts).Hit;
        if (hit == null)
        {
            if (_previewTitle.Length == 0) return;
            _previewTitle = "";
            Ui(() => _overlay?.HideLabelOnly());
            return;
        }

        string title = hit.Title(ActionCatalogTitle(hit));
        if (title == _previewTitle) return;
        _previewTitle = title;
        Ui(() =>
        {
            var overlay = EnsureOverlay();
            overlay.ShowPassive();
            /* 认出来的这一刻轨迹换成「已识别」色（WG2 是 A/T.cs:130 一次性切色）。 */
            overlay.MarkRecognized();
            overlay.ShowLabel(title, px, py, keepTrail: true);
        });
    }

    /// <summary>
    /// 画到已经能唯一确定一条「立即执行」手势时，不再等鼠标抬起。
    /// 只在 OnPoll 里调用（界面线程）。
    /// </summary>
    private bool TryImmediate(int vk, string app, List<GestureMath.Pt> points, double px, double py)
    {
        var opts = Settings().GestureOptions;
        if (GestureMath.Extent(points) < opts.MinMove) return false;

        lock (_gate)
        {
            if (!_active || _vk != vk) return false;
        }

        var strokes = Candidates(Settings(), vk, app, includeWheel: false)
            .Where(g => g.Kind == "stroke")
            .ToList();
        if (strokes.Count == 0) return false;
        foreach (var g in strokes) g.EnsurePattern();

        /* 画到 ↑ 时如果还配了 ↑→，现在执行就会把长手势永远截断。
         * 等这一笔不再像任何"更长候选的前一段"，才算能确定 —— 判据按形状比，
         * 见 GestureEngine.AwaitsLonger。 */
        if (GestureEngine.AwaitsLonger(strokes, points, opts)) return false;

        strokes = strokes.Where(g => g.Immediate).ToList();
        if (strokes.Count == 0) return false;

        var scoped = strokes.Where(g => !string.IsNullOrWhiteSpace(g.AppPath)).ToList();
        var global = strokes.Where(g => string.IsNullOrWhiteSpace(g.AppPath)).ToList();

        MouseGesture? hit = null;
        double score = 1;

        /* 立即执行要更保守：WGestures 引擎的阈值再收紧一档、对"两条都像"零容忍
         * —— 画到一半猜错就是误触发。 */
        var (immediateHit, immediateScore) = GestureEngine.Match(scoped, points, opts, immediate: true);
        if (immediateHit == null)
            (immediateHit, immediateScore) = GestureEngine.Match(global, points, opts, immediate: true);
        if (immediateHit != null) { hit = immediateHit; score = immediateScore; }

        /* 通配手势只认形状真正匹配的，不能像 PickStroke 那样兜底取第一条：
         * 画到一半就执行，猜错一次就是误触发。没存过轨迹的通配（纯 "*"）
         * 在这里不参与 —— 它没有形状可比，画到一半时无从判断"猜对没猜对"。 */
        if (hit == null)
        {
            foreach (var bucket in new[] { scoped, global })
            {
                var wild = bucket.Where(g => g.IsWildcard && GestureEngine.CanMatch(g)).ToList();
                if (wild.Count == 0) continue;
                var (candidate, matchScore) = GestureEngine.Match(wild, points, opts, immediate: true);
                if (candidate != null) { hit = candidate; score = matchScore; break; }
            }
        }

        if (hit == null) return false;

        /* 先记下“这次抬起要吞”，再复核状态：抬起可能刚好抢先跑完 Finish。 */
        Interlocked.Exchange(ref _swallowUpVk, vk);
        lock (_gate)
        {
            if (!_active || _vk != vk)
            {
                Interlocked.Exchange(ref _swallowUpVk, 0);
                return false;
            }
            _active = false;
            _activeInjected = false;
            _tracker = null;
            _cornerCandidate = null;
            _points.Clear();
        }
        _poll?.Stop();

        /* 立即执行模式：命令当场下去，覆盖层按 WG2 的「执行后淡出」规则收
         * （见 SettleOverlay / GestureOptions.FadeOutExecuted）。 */
        ImmediateHit(hit, px, py, score);
        return true;
    }

    /// <summary>
    /// 收尾：先判定，再决定是执行动作、还是把这一下还原成普通单击。
    ///
    /// 「还原成单击」是右键手势能用的前提 —— 我们吞掉了按下与抬起，
    /// 如果没画出手势又不补发一次右键，用户的右键菜单就永远打不开了。
    /// </summary>
    private void Finish(bool execute, string? keepOverlayFor = null)
    {
        bool recording;
        lock (_gate) recording = IsRecording;
        List<GestureMath.Pt> points;
        int vk;
        string app;
        MouseGesture? corner;
        string pattern;
        bool wasInjected;
        /* 中键那半截记录在这里一次性取走并清掉：这一笔无论成不成笔，结算之后都不该留尾巴。 */
        MiddleTap middleTap;
        lock (_gate)
        {
            _active = false;
            /* 这笔是不是从注入事件起手的（改键回声那张通行证）。
             * 中键菜单要用它：注入的中键一律不弹，不然"点中键"这条动作会一路套娃。 */
            wasInjected = _activeInjected;
            _activeInjected = false;
            middleTap = _middleTap;
            _middleTap = default;
            points = new List<GestureMath.Pt>(_points);
            _points.Clear();
            vk = _vk;
            app = _appPath;
            corner = _cornerCandidate;
            _previewTitle = "";
            _previewCount = 0;
            pattern = _tracker?.Pattern ?? "";
            _tracker = null;
        }
        _poll?.Stop();

        var opts = Settings().GestureOptions;
        double extent = GestureMath.Extent(points);
        var (px, py) = CursorPos();

        /* 只有真正画了一笔（不是点击）才计入"手势太频繁"的窗口 —— 口径见 OnButton 那段。 */
        if (extent >= opts.MinMove) lock (_gate) _gestureBurst.Enqueue(Environment.TickCount64);

        /* 笔画平滑（可在「鼠标手势 → 手势设置」关掉）：只改这一笔的「样子」，
         * 也就是接下来比对形状、存进手势、回放和缩略图用的那份点。
         * 放在 extent 之后是故意的 —— 最小识别距离、起始超时在记点时已经判过，
         * 平滑不能反过来影响「这算不算画了一笔」。关掉时这里整段跳过，逐点不变。 */
        if (opts.SmoothStrokes) points = StrokeSmoother.Smooth(points, opts);

        /* 不是录制、也不是松手结算：这是滚轮在跟踪中途按到了动作，
         * 只停止这一笔并保留一下名称。以前这里会误走录制分支。 */
        if (!execute && !recording)
        {
            /* 这一笔到此结束：轨迹按 WG2 立刻清空，名称该闪就闪，不拖着线一起留。 */
            Ui(() => _overlay?.ClearTrail());
            if (string.IsNullOrWhiteSpace(keepOverlayFor)) HideOverlay(0);
            else ShowLabel(keepOverlayFor!, px, py);
            return;
        }

        /* 多步手势：把最后一段也收进来，再跟 Steps 逐项比。 */
        var steps = new List<GestureStep>();
        if (_multiExpected)
        {
            lock (_gate)
            {
                CloseStepLocked(px, py, opts.MinMove);
                steps.AddRange(_stepsDrawn);
                _stepsDrawn.Clear();
                _multiExpected = false;
            }
        }

        MouseGesture? hit = null;
        double score = 1;
        if (execute)
        {
            if (steps.Count >= 2)
                (hit, score) = PickSteps(Settings(), vk, app, steps, opts);

            if (hit == null && extent >= opts.MinMove)
                (hit, score) = PickStroke(Settings(), vk, app, points, opts);

            /* 沿边移动：整条都贴着同一条边 → 那是「贴边手势」。 */
            if (hit == null && extent >= opts.MinMove)
                (hit, score) = PickEdge(Settings(), vk, app, points, opts);

            if (hit == null && extent < opts.MinMove && corner != null)
                hit = corner;

            if (hit == null && extent < opts.MinMove)
            {
                /* 中键菜单（路径 B）：中键手势没成笔 —— 先问菜单要不要接手。
                 * 接手了就**不要**补发原生中键：否则用户会同时得到一次自动滚动和一份菜单
                 * （这就是任务书里那句"不然用户会同时看到一次原生中键加一个菜单"）。 */
                if (MiddleMenuInterceptsTap(vk, px, py, wasInjected, middleTap)) return;

                AttachDiag($"  -> 没成笔：补发一次 vk=0x{vk:X2}（extent={extent:0.0}，MinMove={opts.MinMove}）");

                /* 没画出形状 → 用户只是想点一下。走统一入口补发（见 QueueReplayTap）：
                 * 静默期、贴边连点记账、线程池发送、补发自检都在那里面 ——
                 * 以前这里和各处各写一份，"少补发一条路"就是右键失效。 */
                QueueReplayTap(vk);
            }
            else if (hit == null)
            {
                /* 画了、但没认出任何手势，而且已经超过点击门槛 → 既不执行也不补发。
                 * 记一行：用户报"右键点了没反应"时，先看是不是走了这条（多半是手抖超过 MinMove）。 */
                AttachDiag($"  -> 笔画没认出来，未补发：vk=0x{vk:X2} extent={extent:0.0} MinMove={opts.MinMove}");
            }
        }
        else
        {
            // 录制模式：把轨迹交回界面，不执行
            Action<MouseGesture>? done;
            lock (_gate)
            {
                done = _recordDone;
                _recordDone = null;
                IsRecording = false;
            }
            /* 落库前过一遍轨迹校正：存下来的就是"校正后的那一笔"，以后回放、缩略图、
             * 以及识别都统一用它 —— 用户看到的就是校正后的样子。
             * 判定那一侧不在这里校正：GestureEngine.StrokeOf 里两侧已经统一过了一次，
             * 两个入口都做就成了双重校正。 */
            var canonical = StrokeCanonicalizer.Canonicalize(points);
            var recorded = new MouseGesture
            {
                Id = MouseGesture.NewId(),
                Button = GestureButtons.FromVk(vk),
                Kind = "stroke",
                Path = GestureMath.ToFlat(GestureMath.Normalize(canonical)),
                Pattern = GesturePattern.FromPoints(
                    canonical, opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance)
            };
            HideOverlay(0);
            try { done?.Invoke(recorded); }
            catch (Exception ex) { CoreLog.Swallow(ex, "手势录制回调"); }
            return;
        }

        if (hit != null)
        {
            /* 松手结算模式：画的过程中方框一直在（预览），用户松手 → 执行动作，
             * 名称换成「已执行」配色再淡出（见 FinishHit / SettleOverlay）。 */
            FinishHit(hit, px, py, score);
        }
        else
        {
            /* 未识别 = 屏幕上什么都不留。WG2 的手势结束处理只有两句
             * （A/T.cs:115-119）：A_f3.D() 把轨迹整条清空、e() 收红点并停计时。
             * 没有「未识别」字样，也没有"灰线多留一会儿" —— 那两样是我们自己加的。 */
            if (extent >= opts.MinMove) CoreLog.Info($"手势未命中（偏差 {score:0.000}）");
            HideOverlay(0);
        }
    }

    /// <summary>
    /// 松手命中：立刻执行动作，名称换「已执行」配色再收掉。
    /// </summary>
    private void FinishHit(MouseGesture hit, double px, double py, double score)
    {
        Execute(hit, px, py);
        SettleOverlay(hit, px, py);
        CoreLog.Info($"手势命中：{hit.Title(ActionCatalogTitle(hit))}（偏差 {score:0.000}）");
    }

    /// <summary>
    /// 立即执行（画到一半就命中）：动作当场下去，覆盖层按「执行后淡出」收。
    /// 调用点在 UI 线程（轮询回调），这里直接碰覆盖层。
    /// </summary>
    private void ImmediateHit(MouseGesture hit, double px, double py, double score)
    {
        Execute(hit, px, py);
        SettleOverlay(hit, px, py);
        CoreLog.Info($"手势立即执行：{hit.Title(ActionCatalogTitle(hit))}（偏差 {score:0.000}）");
    }
    private void Execute(MouseGesture g, double px, double py)
    {
        if (string.IsNullOrWhiteSpace(g.Action)) return;
        string action = g.Action;
        string arg = g.Arg;
        /* 老配置里可能残留上一个动作的参数（例如从 sendKeys 换成“直接搜索”）。
         * 不需要参数的动作必须把 Arg 清掉，否则 SearchSelection 会把残留串
         * 当成“显式关键词”，反而搜不到当前选中的文字。 */
        if (_host.Actions.Get(action) is { NeedsArg: false } actionInfo && actionInfo.Id != "customKey") arg = "";

        IntPtr target;
        lock (_gate)
        {
            target = _targetWindow;
            _targetWindow = IntPtr.Zero;
        }

        Interlocked.Exchange(ref _gestureSuppressUntil, Environment.TickCount64 + 700);
        Ui(() =>
        {
            void Run()
            {
                /* 手势名字松手时就浮在屏幕上了（「关闭」），动作再补一条
                 * 「已发送 Ctrl+W（未绑定应用，发给当前窗口）」等于同一件事说第二遍。
                 * 这一段里只放失败提示过去，成功的全部省掉。 */
                using (_host.QuietSuccess())
                {
                    try { _host.InvokeAction(action, arg); }
                    catch (Exception ex) { CoreLog.Swallow(ex, "执行手势动作"); }
                }
            }

            /* 先切窗口再发键：快捷键型动作（Ctrl+W / Ctrl+C …）落在焦点窗口上，
             * 而画手势时那颗按键被我们吞了，焦点还停在上一个窗口。切过去
             * 等一帧，让目标窗口真的拿到焦点，再发键。 */
            if (target != IntPtr.Zero)
            {
                try
                {
                    if (WindowService.Activate(target))
                    {
                        /* 切完窗口不要睡 70ms：把动作排到输入优先级队列的下一拍，
                         * 既让焦点切换落地，又满足"松手立刻生效"。 */
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null)
                            dispatcher.BeginInvoke(new Action(Run), DispatcherPriority.Input);
                        else
                            Run();
                        return;
                    }
                }
                catch (Exception ex) { CoreLog.Swallow(ex, "切到手势目标窗口"); }
            }

            Run();
        });
    }

    /// <summary>
    /// 指针是不是在任务栏/托盘上。Explorer 的托盘右键菜单不认注入的右键，
    /// 所以这里不接管；直接让 Explorer 自己处理。
    /// </summary>
    private static bool IsShellTaskbarAtCursor()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var pt)) return false;
            var hwnd = NativeMethods.WindowFromPoint(pt);
            for (int i = 0; i < 2 && hwnd != IntPtr.Zero; i++)
            {
                var sb = new StringBuilder(64);
                if (NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0)
                {
                    string cls = sb.ToString();
                    if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                        or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")
                        return true;
                }
                hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            }

            /* 任务栏自动隐藏时，指针可能落在已经缩下去的空白处，
             * WindowFromPoint 拿不到 Shell_TrayWnd；底部这条带子直接让给 Explorer。 */
            int screenH = NativeMethods.GetSystemMetrics(1); // SM_CYSCREEN
            if (screenH > 0 && pt.Y >= screenH - 80) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 指针现在是不是压在「特殊目标」上（桌面 / 任务栏）。
    /// WGestures 2 的目标树里这两项不按 exe 匹配，而是按壳窗口匹配
    /// （类名 Progman / WorkerW / Shell_TrayWnd…），所以单独判一次。
    /// </summary>
    private static (bool Desktop, bool Taskbar) SpecialTargetsAt()
    {
        bool taskbar = IsShellTaskbarAtCursor();
        bool desktop = false;
        try
        {
            if (NativeMethods.GetCursorPos(out var pt))
            {
                IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
                if (hwnd != IntPtr.Zero)
                {
                    string? cls = WindowService.ClassNameOf(hwnd);
                    if (cls == null || cls is "Progman" or "WorkerW" or "SHELLDLL_DefView")
                    {
                        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
                        string? rootCls = root == IntPtr.Zero ? null : WindowService.ClassNameOf(root);
                        desktop = rootCls is "Progman" or "WorkerW";
                    }
                }
                else
                {
                    // 窗口底下什么都没有：就是桌面。
                    desktop = true;
                }
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "判断特殊作用目标"); }
        return (desktop, taskbar);
    }

    /// <summary>
    /// 这一笔按下的「触发方式」键，格式和 WGestures 2 的 TriggerDef 一致：
    /// 普通按下是 <c>mouse:C</c>；在屏幕边角按下是 <c>edge:V:mouse:C</c>
    /// （V 是 RectEdge/RectCorner 的位掩码，见 <see cref="Wg2Triggers.MaskOfCorner"/>）。
    /// </summary>
    internal static string TriggerKeyOf(int vk, int cornerAt)
    {
        string mouse = Wg2Triggers.MouseKeyOfVk(vk);
        if (mouse.Length == 0) return "";
        return cornerAt == 0 ? mouse : "edge:" + Wg2Triggers.MaskOfCorner(cornerAt) + ":" + mouse;
    }

    /// <summary>鼠标位置下的顶层窗口；属于本进程（设置页/浮窗）时返回 0，不去切它。</summary>
    private static IntPtr RootWindowAtCursor()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var pt)) return IntPtr.Zero;
            var hwnd = NativeMethods.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;

            hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return IntPtr.Zero;
            return hwnd;
        }
        catch { return IntPtr.Zero; }
    }

    private static string ActionCatalogTitle(MouseGesture g)
        => ActionCatalog.TitleOf(g.Action) ?? "";

    // ───────────────────────── 查询 ─────────────────────────

    /// <summary>
    /// 这一次按下是否值得开一笔手势。只有真正可能触发的触发方式才拦：
    /// 角落手势看指针是否真在对应角落，笔画手势任何位置都可能画。
    /// 用不到的手势如果也拦，右键就会被迫走“吞掉 → 注入补发”，提权窗口里补发被
    /// UIPI 丢掉，表现就是右键菜单偶尔不弹。
    /// </summary>
    internal static bool CanStart(MouseGesture g, GestureOptions opts, int cornerAt) => g.Kind switch
    {
        "corner" => cornerAt != 0 && (g.Corner == 0 || g.Corner == cornerAt),
        "stroke" => true,
        /* 「沿边移动」（Kind=edge）也要开一笔才能识别：它和笔画一样是"按住键画一段"，
         * 区别只在最后按"整条是不是贴着同一条屏幕边"来判定（见 PickEdge）。
         * 以前这里把它漏掉了 —— 某颗键**只配了贴边手势**时，按下根本不开笔，
         * 那条手势永远轮不到 PickEdge，等于配了不生效。 */
        "edge" => true,
        _ => false
    };

    private static List<MouseGesture> Candidates(AppSettings settings, int vk, string app, bool includeWheel, bool includeChords = false)
    {
        var list = new List<MouseGesture>();
        foreach (var g in settings.Gestures)
        {
            if (!g.Enabled) continue;
            if (!includeChords && g.Chord.Length > 0) continue;   // 组合手势只在按住时点第二键触发
            if (GestureButtons.ToVk(g.Button) != vk) continue;
            if (!includeWheel && g.Kind == "wheel") continue;
            if (g.Kind == "stroke")
            {
                g.EnsurePattern();
                if (!g.HasPath && string.IsNullOrEmpty(g.Pattern)) continue;
            }
            if (!AppMatches(g, app, settings.GestureOptions)) continue;
            list.Add(g);
        }
        return list;
    }

    /// <summary>
    /// 笔画匹配：应用手势优先。给某个程序单独配过的手势，先只在"这个程序的"里找；
    /// 找不到再退回全局的 —— 否则全局的"↑ 上滚"会抢在浏览器的"↑ 新标签页"前面。
    ///
    /// 识别只有一套：<see cref="GestureEngine"/>（WGestures 2 的笔画引擎）。
    /// 记过轨迹的手势直接比形状；只有方向串的存量手势由 <see cref="GesturePattern.Sketch"/>
    /// 把方向串画成等长折线，进同一个引擎、同一个阈值 —— v5.13 起没有
    /// "形状没中就退回方向串"的第二层判定，也不再出现两套刻度谁更像。
    ///
    /// 顺序是硬的：应用专属 → 全局 → 应用专属通配 → 全局通配。
    /// 这样"浏览器里 ↑ 是新标签、桌面 ↑ 是上滚"能同时成立，而通配只是兜底，
    /// 永远不会抢走一条真配了笔画的手势。
    /// </summary>
    internal static (MouseGesture? Hit, double Score) PickStroke(
        AppSettings settings, int vk, string app,
        IReadOnlyList<GestureMath.Pt> points, GestureOptions opts)
    {
        var strokes = Candidates(settings, vk, app, includeWheel: false)
            .Where(g => g.Kind == "stroke")
            .ToList();
        foreach (var g in strokes) g.EnsurePattern();

        var scoped = strokes.Where(g => !string.IsNullOrWhiteSpace(g.AppPath)).ToList();
        var global = strokes.Where(g => string.IsNullOrWhiteSpace(g.AppPath)).ToList();

        /* ① 应用专属 → ② 全局：都交给 GestureEngine 打分（记过轨迹的比形状；
         * 只有方向串的先合成折线再比形状）。分数刻度 1.0 = 刚好卡在阈上。 */
        foreach (var bucket in new[] { scoped, global })
        {
            var (hit, score) = MatchWithScope(bucket, points, opts);
            if (hit != null) return (hit, score);
        }

        /* ③④ 通配 * 永远最后，不会抢走一条真配了笔画的手势。 */
        foreach (var bucket in new[] { scoped, global })
        {
            var wild = bucket.Where(g => g.IsWildcard).ToList();
            if (wild.Count == 0) continue;
            /* 通配就是"随便什么形状都算"：不看形状、不打分，直接命中第一条。
             * 它会走到这里，本身就意味着前面没有任何一条真配了形状的手势接住这一笔。 */
            return (wild[0], 0);
        }

        return (null, 1);
    }


    // ───────────────────────── 多步 / 贴边 / 双击角落（v1.5.0） ─────────────────────────

    /* 多步手势里的「按一组键」：钩子把每个按键旁观着报过来，这里攒成一步。
     * 只记修饰键之外的键 —— 单按 ctrl 不算一步。 */
    private readonly List<int> _keysDown = new();

    private static bool IsModifierVk(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
        or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>把按住的键拼成 ctrl+shift+k 这种写法（认不出的键返回空串）。</summary>
    private static string ComboText(IEnumerable<int> down)
    {
        bool ctrl = false, shift = false, alt = false, win = false;
        int main = 0;

        foreach (int vk in down)
        {
            switch (vk)
            {
                case 0x11: case 0xA2: case 0xA3: ctrl = true; break;
                case 0x10: case 0xA0: case 0xA1: shift = true; break;
                case 0x12: case 0xA4: case 0xA5: alt = true; break;
                case 0x5B: case 0x5C: win = true; break;
                default: main = vk; break;
            }
        }

        if (main == 0) return "";
        string name = KeyName.NameOf(main) ?? "";
        if (name.Length == 0) return "";

        var parts = new List<string>(5);
        if (ctrl) parts.Add("ctrl");
        if (shift) parts.Add("shift");
        if (alt) parts.Add("alt");
        if (win) parts.Add("win");
        parts.Add(name);

        CustomKeyRunner.TryNormalize(string.Join("+", parts), out string canonical, out _);
        return canonical.Length > 0 ? canonical : string.Join("+", parts);
    }

    /// <summary>
    /// 按键旁观：多步手势正在等「按一组键」时，把松手的那一组记成一步。
    ///
    /// 不吞键 —— 多步手势是"看着你操作"，不是"接管你的键盘"：
    /// 被按的那组键该发到哪里还是发到哪里（WG2 的多步手势也是这个行为）。
    /// </summary>
    private void OnKeySeen(int vk, bool down, bool injected)
    {
        if (injected) return;

        lock (_gate)
        {
            if (down)
            {
                if (!_keysDown.Contains(vk)) _keysDown.Add(vk);
                return;
            }

            bool was = _keysDown.Remove(vk);
            if (!_active || !_multiExpected) return;
            if (!was || IsModifierVk(vk)) return;

            /* 松手才算"按完了这一组"：先按住 ctrl 再按 a，松 a 时才成步。 */
            string combo = ComboText(_keysDown.Concat(new[] { vk }));
            if (combo.Length == 0) return;

            if (_stepsDrawn.Count < GestureStep.MaxSteps)
                _stepsDrawn.Add(new GestureStep { Kind = "key", Key = combo });
            _segStart = _points.Count;
        }
    }

    /// <summary>把当前待识别的一小段切成一步（锁内调用，只碰字段不算界面）。</summary>
    private void CloseStepLocked(double px, double py, int minMove)
    {
        if (_points.Count - _segStart < 2) return;

        var seg = new List<GestureMath.Pt>(_points.Count - _segStart);
        for (int i = _segStart; i < _points.Count; i++) seg.Add(_points[i]);
        _segStart = _points.Count;

        double extent = GestureMath.Extent(seg);
        if (extent < 1) return;

        var opts = Settings().GestureOptions;
        int corner = CornerAt(px, py, opts.CornerSize);
        int edge = EdgeAt(px, py, opts.EdgeSize);

        GestureStep step;
        if (extent < minMove && corner != 0)
        {
            step = new GestureStep { Kind = "corner", Corner = corner };
        }
        else if (extent >= minMove && edge != 0 && HugsEdge(seg, edge, opts.EdgeSize))
        {
            step = new GestureStep { Kind = "edge", Edge = edge };
        }
        else if (extent >= minMove)
        {
            step = new GestureStep
            {
                Kind = "stroke",
                /* 多步手势的每一小段也存校正后的形状（同一条入口规则）。 */
                Path = GestureMath.ToFlat(GestureMath.Normalize(StrokeCanonicalizer.Canonicalize(seg)))
            };
        }
        else
        {
            return;   // 太短、也不在角上：当手抖丢掉
        }

        if (_stepsDrawn.Count < GestureStep.MaxSteps) _stepsDrawn.Add(step);
        /* 这一步已经切走了：覆盖层上的轨迹从这儿重新画，免得看着像一笔。 */
        Ui(() => _overlay?.ClearTrail());
    }

    /// <summary>作用域层级：具体程序 2、分组 1、全局 0（WG2 目标树的三层）。</summary>
    internal static int ScopeRank(MouseGesture g)
    {
        if (string.IsNullOrWhiteSpace(g.AppPath)) return 0;
        return g.AppPath.StartsWith("group:", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    /// <summary>
    /// 同形手势按目标层级择优（对照 WG2 的 A/m.cs:478）：两条手势画得一样像时，
    /// 作用域更具体的那个赢 —— 具体程序 &gt; 分组 &gt; 全局。
    /// 分桶顺序（应用专属先于全局）不变，这里只处理"同一个桶里也有层级"的情况。
    /// </summary>
    internal static (MouseGesture? Hit, double Score) MatchWithScope(
        List<MouseGesture> bucket, IReadOnlyList<GestureMath.Pt> points, GestureOptions opts)
    {
        var (hit, score) = GestureEngine.MatchRelease(bucket, points, opts);
        if (hit == null) return (null, score);

        double threshold = Math.Max(GestureEngine.ThresholdOf(opts, false), 1e-6);
        MouseGesture best = hit;
        double bestDistance = GestureEngine.Distance(hit, points);

        foreach (var g in bucket)
        {
            if (ReferenceEquals(g, hit) || g.IsWildcard) continue;
            if (ScopeRank(g) <= ScopeRank(best)) continue;
            if (!GestureEngine.CanMatch(g)) continue;

            double d = GestureEngine.Distance(g, points);
            if (d <= threshold && d <= bestDistance + 0.02)
            {
                best = g;
                bestDistance = d;
            }
        }

        return (best, bestDistance / threshold);
    }

    /// <summary>
    /// 一步与模板一步的距离：类型必须一样；笔画比形状（同一套 WG 引擎、同一个阈值），
    /// 按键比组合，滚轮比方向，角落/边比编号。任何一项对不上就是无穷远。
    /// </summary>
    internal static double StepDistance(MouseGesture g, IReadOnlyList<GestureStep> drawn, GestureOptions opts)
    {
        if (g?.Steps == null || drawn == null || g.Steps.Count != drawn.Count) return double.MaxValue;

        double threshold = Math.Max(GestureEngine.ThresholdOf(opts, false), 1e-6);
        double worst = 0;

        for (int i = 0; i < drawn.Count; i++)
        {
            var t = g.Steps[i];
            var d = drawn[i];
            string tk = (t.Kind ?? "").Trim().ToLowerInvariant();
            string dk = (d.Kind ?? "").Trim().ToLowerInvariant();
            if (tk != dk) return double.MaxValue;

            switch (tk)
            {
                case "stroke":
                {
                    if (!t.HasPath || !d.HasPath) return double.MaxValue;
                    var tpl = new MouseGesture { Kind = "stroke", Path = new List<int>(t.Path) };
                    if (!GestureEngine.CanMatch(tpl)) return double.MaxValue;
                    double dist = GestureEngine.Distance(tpl, d.Points());
                    if (double.IsNaN(dist) || dist > threshold) return double.MaxValue;
                    worst = Math.Max(worst, dist / threshold);
                    break;
                }
                case "corner":
                    if (t.Corner != 0 && d.Corner != 0 && t.Corner != d.Corner) return double.MaxValue;
                    break;
                case "edge":
                    if (t.Edge != 0 && d.Edge != 0 && t.Edge != d.Edge) return double.MaxValue;
                    break;
                case "key":
                    if (!string.Equals(
                            CustomKeyRunner.Canonical(t.Key),
                            CustomKeyRunner.Canonical(d.Key),
                            StringComparison.OrdinalIgnoreCase))
                        return double.MaxValue;
                    break;
                case "scroll":
                    if (t.Wheel != 0 && d.Wheel != 0 && Math.Sign(t.Wheel) != Math.Sign(d.Wheel))
                        return double.MaxValue;
                    break;
                default:
                    return double.MaxValue;
            }
        }

        return worst;
    }

    /// <summary>
    /// 多步手势识别：步数一样、每一步都对得上，取最像的那一条。
    /// 只比"序列"，不做单笔手势那套闭环变体 —— 多步手势本来就没有闭环容错的余地。
    /// </summary>
    internal static (MouseGesture? Hit, double Score) PickSteps(
        AppSettings settings, int vk, string app, IReadOnlyList<GestureStep> steps, GestureOptions opts)
    {
        if (steps == null || steps.Count < 2) return (null, 1);

        MouseGesture? best = null;
        double bestScore = 1;
        foreach (var g in Candidates(settings, vk, app, includeWheel: false, includeChords: true))
        {
            if (!g.HasSteps || g.Steps.Count != steps.Count) continue;
            double score = StepDistance(g, steps, opts);
            if (score >= 1) continue;
            if (best == null || score < bestScore)
            {
                best = g;
                bestScore = score;
            }
        }

        return best == null ? (null, 1) : (best, bestScore);
    }

    /// <summary>「沿边移动」：整条轨迹都贴着同一条屏幕边，长度也够。</summary>
    internal static (MouseGesture? Hit, double Score) PickEdge(
        AppSettings settings, int vk, string app, IReadOnlyList<GestureMath.Pt> points, GestureOptions opts)
    {
        if (points == null || points.Count < 2) return (null, 1);

        /* 带宽：用户配的 EdgeSize（出厂 3）只该当"多敏感"，不该当"必须像素级贴边"。
         * 3px 的带子在真实鼠标下几乎不可能整笔待在里面，所以这里给一个 8px 的地板。 */
        int band = Math.Max(8, opts.EdgeSize);

        /* 从**终点**认边（用户最后停在哪条边就是哪条），认不到再看起点 —— 单向滑到边上的手势，
         * 起点还在屏幕中间，只看终点会漏；反过来只看起点会漏"从中间滑到边上"的那种。 */
        var first = points[0];
        var last = points[^1];
        int edge = EdgeAt(last.X, last.Y, band);
        if (edge == 0) edge = EdgeAt(first.X, first.Y, band);
        if (edge == 0 || !HugsEdge(points, edge, band)) return (null, 1);

        var hit = Candidates(settings, vk, app, includeWheel: false)
            .FirstOrDefault(g => g.Kind == "edge" && (g.Edge == 0 || g.Edge == edge));
        return hit == null ? (null, 1) : (hit, 0);
    }

    /// <summary>
    /// 整条轨迹是不是都在这条边的带宽里。
    ///
    /// 允许少量采样点出带（手画的线不可能一帧都不抖）：出带比例超过 15% 才判不贴边。
    /// 以前要求**全部**点都在带内，等于只有拿尺子画才认得出贴边手势。
    /// </summary>
    internal static bool HugsEdge(IReadOnlyList<GestureMath.Pt> points, int edge, int size)
    {
        if (points == null || points.Count == 0 || edge == 0) return false;

        int inside = 0;
        foreach (var p in points)
            if (EdgeAt(p.X, p.Y, size) == edge) inside++;

        /* 太短的轨迹（少于 3 个采样点）没有统计意义，按"全部在带内"处理。 */
        if (points.Count < 3) return inside == points.Count;
        return inside >= 3 && inside >= (int)Math.Ceiling(points.Count * 0.85);
    }

    /// <summary>贴边连点两下：WG2 用它把"点在边附近"当成角落手势（A/K.cs:505）。</summary>
    private bool IsEdgeDoubleTap(double px, double py, GestureOptions opts)
    {
        long last;
        GestureMath.Pt pt;
        lock (_gate)
        {
            last = _lastTapAt;
            pt = _lastTapPt;
        }

        if (last == 0 || Environment.TickCount64 - last > 420) return false;
        double dx = px - pt.X, dy = py - pt.Y;
        if (dx * dx + dy * dy > 900) return false;      // 30px 以内算同一下
        return EdgeAt(px, py, Math.Max(opts.EdgeSize, 24)) != 0;
    }

    /// <summary>离指针最近的那个角：1 左上、2 右上、3 左下、4 右下。</summary>
    internal static int NearestCorner(double px, double py)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);

        bool leftHalf = px - left <= right - px;
        bool topHalf = py - top <= bottom - py;
        return topHalf ? (leftHalf ? 1 : 2) : (leftHalf ? 3 : 4);
    }

    private static MouseGesture? CornerCandidateFor(List<MouseGesture> candidates, int corner) =>
        candidates.FirstOrDefault(g => g.Kind == "corner" && (g.Corner == 0 || g.Corner == corner));

    /// <summary>
    /// 这条手势在这个程序里算不算数。作用域为空 = 全局，任何程序都算。
    /// 分组作用域（group:xxx）按组里的程序列表匹配 —— 这是 WGestures 分组的本意：
    /// 一组程序共用一套手势，不是给组另存一份。
    /// </summary>
    internal static bool AppMatches(MouseGesture g, string foreground, GestureOptions options)
    {
        if (string.IsNullOrWhiteSpace(g.AppPath)) return true;
        if (g.AppPath.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
        {
            var group = options.TargetList?.FirstOrDefault(t =>
                string.Equals(t.Id, g.AppPath, StringComparison.OrdinalIgnoreCase));
            if (group?.Members == null || group.Members.Count == 0) return false;
            return group.Members.Any(m => SameApp(m, foreground));
        }
        return SameApp(g.AppPath, foreground);
    }

    /// <summary>这个程序是否被用户设成“所有手势都不触发”。</summary>
    private bool IsTargetDisabled(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return false;
        var disabled = Settings().GestureOptions.DisabledTargets;
        if (disabled == null || disabled.Count == 0) return false;
        foreach (string target in disabled)
            if (SameApp(target, app)) return true;
        return false;
    }

    private static bool SameApp(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return string.Equals(
                System.IO.Path.GetFileNameWithoutExtension(a),
                System.IO.Path.GetFileNameWithoutExtension(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static MouseGesture? CornerCandidate(List<MouseGesture> candidates, double px, double py, int size)
    {
        int corner = CornerAt(px, py, size);
        if (corner == 0) return null;
        return candidates.FirstOrDefault(g => g.Kind == "corner" && (g.Corner == 0 || g.Corner == corner));
    }

    /// <summary>指针在哪个角：1 左上、2 右上、3 左下、4 右下、0 不在角上。</summary>
    public static int CornerAt(double px, double py, int size)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);

        bool nearLeft = px <= left + size;
        bool nearRight = px >= right - size;
        bool nearTop = py <= top + size;
        bool nearBottom = py >= bottom - size;

        if (nearLeft && nearTop) return 1;
        if (nearRight && nearTop) return 2;
        if (nearLeft && nearBottom) return 3;
        if (nearRight && nearBottom) return 4;
        return 0;
    }

    /// <summary>指针贴着哪条屏幕边：1 上、2 右、3 下、4 左、0 不在边上。</summary>
    public static int EdgeAt(double px, double py, int size)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);

        bool nearLeft = px <= left + size;
        bool nearRight = px >= right - size;
        bool nearTop = py <= top + size;
        bool nearBottom = py >= bottom - size;

        if (nearTop) return 1;
        if (nearRight) return 2;
        if (nearBottom) return 3;
        if (nearLeft) return 4;
        return 0;
    }

    /// <summary>桌面、任务栏这类壳窗口不算"应用"，匹配手势时仍按活动窗口算。</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>
    /// 这一次手势该作用在哪个窗口上 —— 由设置里的「手势作用目标」决定。
    /// 返回 (程序路径, 执行动作前要切过去的窗口)：
    ///   · pointer（默认）= 指针下方的应用，同时把那个窗口记成目标窗口，执行前切过去；
    ///   · active = 当前活动窗口，目标窗口留空 —— 不切窗口，动作直接发给焦点窗口。
    ///
    /// 指针底下是桌面/任务栏、或者拿不到进程路径时退回活动窗口：
    /// 否则用户一旦在「作用目标」里禁用了 explorer.exe，桌面上就再也画不出手势。
    /// </summary>
    internal static (string App, IntPtr Window) ResolveTarget(GestureOptions opts)
    {
        if (opts is not { UsesPointerTarget: true }) return (ForegroundExe(), IntPtr.Zero);

        IntPtr hwnd = RootWindowAtCursor();
        if (hwnd == IntPtr.Zero) return (ForegroundExe(), IntPtr.Zero);

        string? cls = WindowService.ClassNameOf(hwnd);
        if (cls != null && ShellClasses.Contains(cls)) return (ForegroundExe(), IntPtr.Zero);

        string exe = ExeOfWindow(hwnd);
        return exe.Length == 0 ? (ForegroundExe(), IntPtr.Zero) : (exe, hwnd);
    }

    /// <summary>自检用：按指定作用目标解析一次，只读、不改任何运行状态。</summary>
    internal static (string App, IntPtr Window) ResolveTargetForTest(string mode) =>
        ResolveTarget(new GestureOptions { TargetMode = mode });

    private static string ExeOfWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return "";
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == 0 ? "" : WindowService.GetProcessPath(pid);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "取窗口程序路径");
            return "";
        }
    }

    private static string ForegroundExe() => ExeOfWindow(WindowService.Foreground);

    private AppSettings Settings() => _host.Settings;

    // ───────────────────────── 覆盖层 ─────────────────────────

    private void Ui(Action action)
    {
        var app = Application.Current;
        if (app == null) return;
        try
        {
            if (app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.BeginInvoke(action);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "手势界面调度"); }
    }

    private GestureOverlayWindow EnsureOverlay()
    {
        if (_overlay != null) return _overlay;

        /* 只能在界面线程上建：WPF 对象绑定在创建它的 Dispatcher 上。从钩子线程
         * new 出来的窗口，之后每次 Show() 都会抛线程亲和性异常，而那条异常会被
         * Ui() 吞掉 —— 表现就是"从此以后每笔手势都画得出来、但一个像素都不显示"。
         * 现在所有调用点都在 Ui() 里；这里留一道闸，越界就记日志并放弃这一次。 */
        if (Application.Current is { } app && !app.Dispatcher.CheckAccess())
        {
            CoreLog.Warn("[手势] 覆盖层被非界面线程创建，已忽略（轨迹这一笔不会显示）");
            throw new InvalidOperationException("GestureOverlayWindow 必须在界面线程创建");
        }

        _overlay = new GestureOverlayWindow();
        _overlay.Closed += (_, _) => _overlay = null;
        return _overlay;
    }

    /// <summary>没有轨迹的一次性触发（角落手势、角落 + 滚轮）：只闪一下名字。</summary>
    private void ShowLabelOnly(MouseGesture g, double px, double py)
    {
        if (g.HideLabel) { HideOverlay(0); return; }
        /* 建窗与显示都交给 ShowLabel 里的 Ui()：这里原来先 EnsureOverlay() 再 ShowLabel，
         * 而 OnWheel 跑在钩子线程上 —— 第一次走这条路会把覆盖层建在钩子线程上。 */
        ShowLabel(g.Title(ActionCatalogTitle(g)), px, py);
    }

    private void ShowTimeoutDot(double px, double py)
    {
        /* 「超时后显示此标志」关掉时，超时就只是安静地结束，屏幕什么都不画。 */
        if (!Settings().GestureOptions.ShowStartTimeoutIndicator)
        {
            HideOverlay(0);
            return;
        }
        Ui(() =>
        {
            var overlay = EnsureOverlay();
            overlay.ShowPassive();
            overlay.ShowTimeoutDot(px, py);
            /* 不设自动收：WG2 的红点是手势开始时显示（A/T.cs:51-52）、
             * 手势结束才藏（A/T.cs:383）。不画也不松手，它就一直亮着。 */
        });
    }

    private void ShowToast(string text, double px, double py)
    {
        Ui(() =>
        {
            var overlay = EnsureOverlay();
            overlay.ShowPassive();
            overlay.ShowLabel(text, px, py);
            HideOverlay(1600);
        });
        CoreLog.Info($"显示手势提示：{text}");
    }

    /// <param name="holdMs">
    /// 这次提示停留多久；null = 用配置里的 <see cref="LabelHoldMs"/>。
    /// 命中并执行了动作时**不走这里**（直接 <see cref="HideOverlay"/> 收掉，
    /// 见 <see cref="HitLabelLingerMs"/>），这里只剩预览 / 滚轮换层。
    /// </param>
    private void ShowLabel(string text, double px, double py, int? holdMs = null)
    {
        Ui(() =>
        {
            /* 兜底创建：立即执行/特殊路径可能还没建过覆盖层；
             * ShowLabel 以前只在 _overlay 非空时显示，导致日志有名字但屏幕没有。 */
            var overlay = EnsureOverlay();
            /* 无条件重新置顶：手势动作经常紧接着激活/打开一个窗口
             * （比如 Win+E 打开资源管理器），如果只在“首次显示”时置顶，
             * 新窗口会把胶囊压到下面，用户就只看到轨迹、看不到名字。 */
            overlay.ShowPassive();
            /* 只留名称框，轨迹一律不留 —— WG2 的轨迹归轨迹、名称归名称：
             * 轨迹在手势结束那一刻就整条清掉（A/T.cs:115-119），
             * 名称框按 FadeOutExecuted / 停留时间自己收。 */
            overlay.ShowLabel(text, px, py, keepTrail: false);

            /* 动作打开的窗口可能在这一帧之后才完成激活，160ms 后再顶一次，
             * 防止资源管理器/浏览器把胶囊盖住。只顶一次，不改变窗口状态。 */
            var raise = new DispatcherTimer(DispatcherPriority.Send)
            {
                Interval = TimeSpan.FromMilliseconds(160)
            };
            raise.Tick += (_, _) =>
            {
                raise.Stop();
                overlay.ShowPassive();
            };
            raise.Start();
        });
        CoreLog.Info($"显示手势名：{text}");
        /* 这段时长只决定名称框留多久：轨迹在收尾那一帧就已经清掉了
         * （见 overlay.ShowLabel 的 keepTrail:false），不会再出现
         * "手势画完了、线还在屏幕上挂着"的尾巴。 */
        HideOverlay(holdMs ?? LabelHoldMs(Settings().GestureOptions));
    }

    /// <summary>
    /// 没有执行动作的提示（预览、滚轮换层）名称框在屏幕上停留多久。
    ///
    /// **不包含轨迹**：轨迹在手势结束那一刻就已经清掉了（WG2 <c>A/T.cs:115-119</c>），
    /// 这条时长只决定名称框自己留多久。
    /// </summary>
    internal static int LabelHoldMs(GestureOptions opts)
        => Math.Max(1200, opts.LabelLingerMs);

    /// <summary>
    /// 命中并**执行了动作**之后，名称框（轨迹已在同一帧清掉）还留在屏幕上多久 —— <b>0，立刻消失</b>。
    ///
    /// 用户定的规则（两种模式都要"执行完就没"）：
    /// ① 手势勾了「识别手势立即执行」→ 方向串一对上就执行，执行那一刻方框立刻收；
    /// ② 没勾 → 画的过程中方框跟着预览（<see cref="UpdatePreview"/>），
    ///    等鼠标键松开、<see cref="Finish"/> 结算执行，**松手这一刻**立刻收。
    /// 两种情况都不留尾巴；只有"没执行动作"的提示（预览、滚轮换层）
    /// 才按 <see cref="LabelHoldMs"/> 继续留着。
    /// </summary>
    internal const int HitLabelLingerMs = 0;

    /// <summary>
    /// 命中并执行之后覆盖层怎么收 —— WGestures 2 的「执行后淡出」：
    /// 勾了 FadeOutExecuted（默认）→ 名称换「已执行」配色，停 100ms 后消失；
    /// 没勾 → 停 250ms，再用 250ms 缓入淡出。手势自己勾了「执行时不显示手势名称」
    /// 或者全局关了「显示手势名称」，就当场清掉，一个像素都不留。
    /// </summary>
    private void SettleOverlay(MouseGesture g, double px, double py)
    {
        var opts = Settings().GestureOptions;
        if (g.HideLabel || !opts.ShowGestureName)
        {
            HideOverlay(HitLabelLingerMs);
            return;
        }

        string title = g.Title(ActionCatalogTitle(g));
        Ui(() =>
        {
            var overlay = EnsureOverlay();
            overlay.ShowPassive();
            overlay.MarkRecognized();
            /* 手势结束 = 轨迹立刻消失（WG2 A/T.cs:115-119 的 A_f3.D()），
             * 屏幕上只留名称框按 FadeOutExecuted 淡出。以前这里是 keepTrail:true，
             * 轨迹陪着名称多挂 130~530ms —— 看起来就是"手势都做完了线还赖着"。 */
            overlay.ShowLabel(title, px, py, keepTrail: false, executed: true);
        });
        HideOverlayFading();
    }

    /// <summary>
    /// 新手势起手：先把上一笔排的「收起覆盖层」全部作废。
    ///
    /// 连着划两下时，上一笔的淡出定时器（默认 100~350ms）常常正好在第二笔
    /// 中途到点 —— 它会 Clear()+Hide()，第二笔的轨迹和名字就全没了，
    /// 而动作照常执行。所以起手这一下必须把两段定时器停掉并换一代号，
    /// 让已经排进消息队列的回调也一并失效（见 <see cref="_hideEpoch"/>）。
    ///
    /// 只在钩子线程/轮询线程上调，两个字段的 Stop 都是线程安全的：
    /// DispatcherTimer.Stop 可以从任意线程调用。
    /// </summary>
    private void BeginStrokeVisual()
    {
        Interlocked.Increment(ref _hideEpoch);
        try { _linger?.Stop(); } catch (Exception ex) { CoreLog.Swallow(ex, "停手势名称停留"); }
        try { _fade?.Stop(); } catch (Exception ex) { CoreLog.Swallow(ex, "停手势名称淡出"); }
        _lastRaiseAt = 0;
    }

    /// <summary>立刻开始淡出名称框，淡完再清窗口（WG2 的 T.cs:141-150）。</summary>
    private void HideOverlayFading()
    {
        _linger?.Stop();
        int epoch = Interlocked.Increment(ref _hideEpoch);
        Ui(() =>
        {
            if (epoch != Volatile.Read(ref _hideEpoch)) return;      // 新手势已经起手，这一笔的回报作废
            int fade = _overlay?.FadeOutLabel() ?? 0;
            if (fade <= 0)
            {
                _overlay?.Clear();
                _overlay?.Hide();
                return;
            }

            _fadeEpoch = epoch;
            _fade ??= new DispatcherTimer(DispatcherPriority.Background);
            _fade.Stop();
            _fade.Interval = TimeSpan.FromMilliseconds(fade);
            _fade.Tick -= OnFadeDone;
            _fade.Tick += OnFadeDone;
            _fade.Start();
        });
    }

    private void OnFadeDone(object? sender, EventArgs e)
    {
        _fade?.Stop();
        int epoch = _fadeEpoch;
        Ui(() =>
        {
            /* 号变了 = 新手势已经开始：这一下是上一笔的尾巴，不许碰现在的画面。 */
            if (epoch != Volatile.Read(ref _hideEpoch) || IsTracking) return;
            _overlay?.Clear();
            _overlay?.Hide();
        });
    }

    private void HideOverlay(int delayMs)
    {
        _linger?.Stop();
        _fade?.Stop();
        int epoch = Interlocked.Increment(ref _hideEpoch);
        if (delayMs <= 0)
        {
            Ui(() =>
            {
                if (epoch != Volatile.Read(ref _hideEpoch) || IsTracking) return;
                _overlay?.Clear();
                _overlay?.Hide();
            });
            return;
        }

        _lingerEpoch = epoch;
        _linger ??= new DispatcherTimer(DispatcherPriority.Background);
        _linger.Stop();
        _linger.Interval = TimeSpan.FromMilliseconds(delayMs);
        _linger.Tick -= OnLinger;
        _linger.Tick += OnLinger;
        _linger.Start();
    }

    private void OnLinger(object? sender, EventArgs e)
    {
        _linger?.Stop();
        _fade?.Stop();
        int epoch = _lingerEpoch;
        Ui(() =>
        {
            if (epoch != Volatile.Read(ref _hideEpoch) || IsTracking) return;
            _overlay?.Clear();
            _overlay?.Hide();
        });
    }

    // ───────────────────────── Win32 ─────────────────────────

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static (double X, double Y) CursorPos()
    {
        try
        {
            if (GetCursorPos(out var p)) return (p.X, p.Y);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "读取指针位置"); }
        return (0, 0);
    }
}
