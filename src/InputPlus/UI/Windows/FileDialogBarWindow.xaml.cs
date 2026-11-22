using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.FileDialog;
using InputPlus.Core.Input;

namespace InputPlus.UI.Windows;

/// <summary>浮层里的一行。Bar 只认识这个结构 + <see cref="DialogSelection"/>，不认识 Win32。</summary>
public sealed class BarRow : System.ComponentModel.INotifyPropertyChanged
{
    public DialogSelection Item { get; init; } = new("", false, "");
    public string Glyph { get; init; } = "\uE7C3";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";

    /// <summary>
    /// 地址列显示的文本：太长时**保留头尾、中间省略**。
    ///
    /// 为什么不用 <c>TextTrimming="CharacterEllipsis"</c>：末尾省略正好把"最后两级目录"削掉，
    /// 而那恰恰是路径里最有用的部分。地址列的宽度由 1 : 1.7 的星号列决定，
    /// 12px 字号下大约放得下 40 个西文字符 —— 这里按 12 + 24 的字数预算切，
    /// 剩下的宽度差交给 CharacterEllipsis 兜底（浮层被挤到很窄时）。
    /// 完整路径挂在 ToolTip 上，脚注里也有面包屑。
    /// </summary>
    public string SubtitleDisplay => MiddleTrim(Subtitle, AddressHeadChars, AddressTailChars);

    private const int AddressHeadChars = 12;
    private const int AddressTailChars = 24;

    /// <summary>保留头 head 个字符 + 尾 tail 个字符，中间换成省略号。</summary>
    internal static string MiddleTrim(string text, int head, int tail)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= head + tail + 2) return text;
        return text[..head] + "…" + text[^tail..];
    }
    public string Badge { get; init; } = "";
    public Visibility BadgeVisibility => string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>真实 shell 图标（与资源管理器同一套 SHGetFileInfo 结果）。</summary>
    private System.Windows.Media.ImageSource? _icon;
    public System.Windows.Media.ImageSource? Icon => _icon;
    public Visibility IconVisibility => _icon == null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => _icon == null ? Visibility.Visible : Visibility.Collapsed;

    public void SetIcon(System.Windows.Media.ImageSource? icon)
    {
        _icon = icon;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IconVisibility)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(GlyphVisibility)));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 文件对话框下方的搜索浮层（全程序只有这一套实现）。
///
/// 只做四件事：贴住对话框、拿键盘、显示结果、把回车/点击翻译成
/// <see cref="DialogSelection"/>。回填、判型、记录都不在这里 —— 见 FILE-DIALOG-BAR.md 的三层约束。
/// </summary>
public partial class FileDialogBarWindow : Window
{
    private readonly ObservableCollection<BarRow> _rows = new();
    private readonly DispatcherTimer _queryTimer;
    private nint _dialog;
    private double _scale = 1;
    private int _barHeightDip = 44;
    private BarPlacement _lastPlacement = BarPlacement.Below;
    private readonly DialogBarAnchor _anchor = new();
    private bool _imeComposing;
    private string _lastQuery = "";
    private TemporaryKeyboardRedirect? _redirect;

    /// <summary>输入变化（已去抖、已跳过输入法组合期）。</summary>
    public event Action<string, DialogScope>? QueryChanged;

    /// <summary>用户回车或点击了某一项。</summary>
    public event Action<DialogSelection>? CommitRequested;

    /// <summary>用户按 Esc 关掉了浮层。</summary>
    public event Action? Dismissed;

    internal event Action? FavoritesRequested;
    internal event Action? HistoryRequested;
    internal event Action? MenuRequested;
    internal event Action? FolderChipRequested;
    internal event Action<string>? FolderPicked;
    internal event Action<QuickSwitchAction>? ActionRequested;

    private readonly ObservableCollection<QuickSwitchRow> _overlayRows = new();
    private bool _overlayOpen;

    private enum OverlayKind { None, Favorites, History, Menu, HideMenu }
    private OverlayKind _overlayKind;

    /// <summary>用户要求"在资源管理器里定位这一项"（Alt+Enter，或不回填时的退路）。</summary>
    public event Action<DialogSelection>? RevealRequested;

    public DialogScope Scope { get; private set; } = DialogScope.All;
    public nint DialogHandle => _dialog;
    public bool IsOpen => IsVisible && _dialog != 0;

    public FileDialogBarWindow()
    {
        InitializeComponent();
        ResultList.ItemsSource = _rows;

        _queryTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(45) };
        _queryTimer.Tick += (_, _) => FlushQuery();

        AddHandler(TextCompositionManager.TextInputStartEvent,
            new TextCompositionEventHandler((_, _) => _imeComposing = true), true);
        AddHandler(TextCompositionManager.TextInputUpdateEvent,
            new TextCompositionEventHandler((_, _) => _imeComposing = true), true);
        AddHandler(TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler((_, _) => { _imeComposing = false; ScheduleQuery(); }), true);

        OverlayList.ItemsSource = _overlayRows;
        OverlayList.PreviewKeyDown += OnOverlayKeyDown;
        QueryBox.TextChanged += (_, _) => ScheduleQuery();
        PreviewKeyDown += OnPreviewKeyDown;
        ResultList.PreviewMouseLeftButtonUp += OnListClick;
        /* 自动显示模式下浮层带 WS_EX_NOACTIVATE（不抢对话框的输入）。
         * 用户点输入框/列表时才把键盘拿过来 —— 这是"点一下就能打字"的关键：
         * NOACTIVATE 的窗口鼠标点上去不会激活，不显式抢前台的话
         * 输入全都会落到下面的对话框里。 */
        PreviewMouseLeftButtonDown += OnBarMouseDown;
        /* 尺寸只由 ComputeGeometry 决定。这里**绝不能**再排一次几何：
         * SetWindowPos 落地后会异步回一个 SizeChanged，再算一次几何 → 又改尺寸 → 再回一条，
         * 就是无限乒乓（自检里能跑到 100% CPU 卡死）。只留计数供自检断言。 */
        SizeChanged += (_, _) =>
        {
            if (_inApply) _sizeChangedInApply++;
        };
        Closed += (_, _) => DisposeRedirect();
    }

    private bool _keyboardTaken;

    private static bool IsInteractiveClick(DependencyObject? source)
    {
        for (DependencyObject? d = source; d != null; d = ParentOf(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            if (d is ListBoxItem) return true;
            if (d is System.Windows.Controls.Primitives.TextBoxBase) return true;
            if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
            if (d is System.Windows.Controls.Primitives.Thumb) return true;
            if (d is ListBox) return true;
        }
        return false;
    }

    /// <summary>用户点了浮层：第一次点击时把键盘抢过来（点结果行不改变选中项之外的行为）。</summary>
    private static DependencyObject? ParentOf(DependencyObject d)
    {
        try
        {
            if (d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(d);
        }
        catch (InvalidOperationException) { }
        if (d is FrameworkContentElement fce) return fce.Parent;
        if (d is FrameworkElement fe) return fe.Parent;
        return null;
    }

    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsVisible || _dialog == 0) return;

        /* 按钮/列表行自己会处理点击：不能在 MouseDown 里把焦点抢到输入框，
         * 那会让 Button 丢失捕获，表现就是"点了没反应"（1.3.9 的实测 bug）。 */
        if (IsInteractiveClick(e.OriginalSource as DependencyObject)) return;
        if (!_keyboardTaken) EnsureKeyboard();

        /* 这一下鼠标按下发生在窗口还是 NOACTIVATE 的时候，WPF 自己不会把焦点
         * 交给 TextBox；等这轮鼠标消息走完再显式聚焦一次，用户才"点一下就能打字"。 */
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsVisible) return;
            Activate();
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
            FocusManager.SetFocusedElement(this, QueryBox);
        }), DispatcherPriority.Input);
    }

    /// <summary>
    /// 从"只显示"切到"可以打字"：去掉 NOACTIVATE，再走同一套焦点阶梯。
    /// 失败时恢复 NOACTIVATE，并把原因写到状态栏（不弹框）。
    /// </summary>
    public void EnsureKeyboard()
    {
        if (_keyboardTaken) return;
        SetNoActivate(false);
        nint hwnd = new WindowInteropHelper(this).Handle;
        var result = DialogFocusLadder.TryTakeKeyboard(hwnd, out string detail);
        switch (result)
        {
            case DialogFocusLadder.Result.Foreground:
            case DialogFocusLadder.Result.AttachThreadInput:
            case DialogFocusLadder.Result.AltTap:
                Activate();
                QueryBox.Focus();
                Keyboard.Focus(QueryBox);
                _keyboardTaken = true;
                SetStatus("");
                break;

            case DialogFocusLadder.Result.NeedsKeyboardHook:
                if (InstallRedirect())
                {
                    _keyboardTaken = true;
                    SetNoActivate(false);
                    break;
                }
                SetNoActivate(true);
                SetStatus("取不到键盘：点对话框输入");
                CoreLog.Warn("[文件对话框] 点击浮层后仍取不到键盘：" + detail);
                break;

            default:
                SetNoActivate(true);
                SetStatus("取不到键盘：点对话框输入");
                CoreLog.Warn("[文件对话框] 点击浮层后取不到键盘：" + detail);
                break;
        }
    }

    /// <summary>切换 WS_EX_NOACTIVATE。显示时默认加上（不打扰对话框），要输入时去掉。</summary>
    private void SetNoActivate(bool on)
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        long ex = (long)DialogNative.GetWindowLongPtr(hwnd, DialogNative.GWL_EXSTYLE);
        long next = on ? (ex | DialogNative.WS_EX_NOACTIVATE) : (ex & ~DialogNative.WS_EX_NOACTIVATE);
        DialogNative.SetWindowLongPtr(hwnd, DialogNative.GWL_EXSTYLE, (nint)next);
    }

    // ---------------- 显示 / 定位 ----------------

    /// <summary>绑定到对话框并显示。takeKeyboard=false 时只显示、不抢焦点。</summary>
    public bool ShowFor(DialogWindowInfo info, DialogRect bounds, bool takeKeyboard, out string detail)
    {
        detail = "";
        /* 顺序：建句柄 → 绑对话框 → 算几何 → **先摆到目标位置** → 最后才 Show。
         * 以前是先 Show 再摆，第一帧必然出现在"上次的位置/默认位置"，然后跳过去；
         * 现在窗口显示时矩形已经是目标矩形，第一帧就是对的。 */
        AttachTo(info, bounds);
        _bounds = bounds;
        ComputeGeometry("显示");
        ApplyPosition(resize: true);

        if (!IsVisible) Show();

        /* 第二帧校正：对话框出现后的头一帧经常还在"初始尺寸→最终布局"之间，
         * 只按 SHOW 那一刻的矩形摆位会差几十像素（看着就是"没贴住"）。
         * 渲染优先级补一次，再在 120ms / 320ms 各补一次 —— 文件对话框的最终布局
         * 通常在 100~300ms 内落定，而它不一定会为此发 LOCATIONCHANGE。 */
        ScheduleFirstFrameFix();
        _ = FixPositionLater(120);
        _ = FixPositionLater(320);

        _keyboardTaken = false;
        if (!takeKeyboard)
        {
            SetNoActivate(true);
            detail = "只显示不抢焦点（点输入框即可打字）";
            return true;
        }

        SetNoActivate(false);
        nint hwnd = new WindowInteropHelper(this).Handle;
        var result = DialogFocusLadder.TryTakeKeyboard(hwnd, out detail);
        switch (result)
        {
            case DialogFocusLadder.Result.Foreground:
            case DialogFocusLadder.Result.AttachThreadInput:
            case DialogFocusLadder.Result.AltTap:
                Activate();
                QueryBox.Focus();
                Keyboard.Focus(QueryBox);
                _keyboardTaken = true;
                return true;

            case DialogFocusLadder.Result.NeedsKeyboardHook:
                if (InstallRedirect()) { _keyboardTaken = true; return true; }
                detail += "；键盘钩子也未装上";
                HideBar();
                return false;

            default:
                HideBar();
                return false;
        }
    }

    public void AttachTo(DialogWindowInfo info, DialogRect bounds)
    {
        _dialog = info.Hwnd;
        /* 先把句柄建出来（不显示）：之后 ComputeGeometry/ApplyPosition 才有窗口可摆。
         * 句柄建得早也意味着 Show() 不再承担"建窗 + 首次布局"的成本。 */
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (hwnd == 0) return;
        WindowDpiScale();
        if (!bounds.IsEmpty) _bounds = bounds;

        long ex = (long)DialogNative.GetWindowLongPtr(hwnd, DialogNative.GWL_EXSTYLE);
        DialogNative.SetWindowLongPtr(hwnd, DialogNative.GWL_EXSTYLE,
            (nint)(ex | DialogNative.WS_EX_TOOLWINDOW | DialogNative.WS_EX_NOACTIVATE));
        DialogNative.SetWindowLongPtr(hwnd, DialogNative.GWLP_HWNDPARENT, info.Hwnd);
        DialogBarAnchor.ConfigureBarChrome(hwnd);
        _anchor.Attach(info.Hwnd);
        _anchor.SetBarHandle(hwnd);
    }

    // ───────────────── 跟随：事件驱动，不在跟随路径里做布局 ─────────────────

    /* 拖动判定窗口：200ms 内 ≥3 次 LOCATIONCHANGE 就算"正在拖"。 */
    private const long DragWindowMs = 200;
    private const int DragEventThreshold = 3;

    /* 最新目标矩形（物理像素，单字段替换 = 天然原子：引用赋值）。
     * 同一帧来多次事件只留最后一次，绝不做 FIFO 队列 —— 排队就是延迟。 */
    private DialogRect _bounds;

    private long _lastBoundsAt;
    private int _boundsInWindow;
    private bool _dragging;

    /* 当前几何（物理像素）。只在 ComputeGeometry 里改；拖动中一个字节都不动。 */
    private int _barWidthPx;
    private int _barHeightPx;

    /* 重入闸：我们自己 SetWindowPos 引起的 SizeChanged 不许再排跟随。 */
    private bool _inApply;
    private int _sizeChangedInApply;       // 自检断言用：必须恒为 0
    private int _followScheduledInApply;   // 自检断言用：_inApply 期间又排几何的次数，必须 0

    /* 静置后做一次完整几何重算（宽/列表高/翻转/圆角方向）。 */
    private DispatcherTimer? _settleTimer;

    /* 度量：事件 → SetWindowPos 返回的耗时（微秒）与像素误差，默认关。 */
    private readonly List<double> _latencyUs = new(256);
    private readonly List<double> _pixelError = new(256);
    private long _metricsWindowStart;
    private int _metricsApplied;

    /// <summary>每秒汇总一次 p50/p99 与像素误差（默认关，见设置里的「跟随度量」）。</summary>
    public bool MetricsEnabled { get; set; }

    /// <summary>最近一次几何计算的原因（日志与自检用）。</summary>
    internal string LastGeometryReason { get; private set; } = "";

    /// <summary>正在拖动（200ms 内 ≥3 次位置事件）。拖动中只挪位、不改尺寸、不因前台变化收起。</summary>
    internal bool Dragging => _dragging;

    /// <summary>几何被重算过几次。</summary>
    internal int GeometryCount { get; private set; }

    /// <summary>自检用：跟随应用次数、应用期间收到的 SizeChanged 次数、最近 p99（微秒）。</summary>
    internal int AppliedCount { get; private set; }
    internal int SizeChangedInApply => _sizeChangedInApply;
    internal int FollowScheduledInApply => _followScheduledInApply;
    internal double LatencyP99Us { get { var c = new List<double>(_latencyUs); c.Sort(); return Percentile(c, 0.99); } }
    internal double LatencyP50Us { get { var c = new List<double>(_latencyUs); c.Sort(); return Percentile(c, 0.50); } }
    internal double PixelErrorMax { get; private set; }

    /// <summary>
    /// 跟随（**事件回调里同步调用**）。只做三件事：记下最新矩形、判拖动、摆位。
    /// 不做 Measure/UpdateLayout/改尺寸 —— 那些在 <see cref="ComputeGeometry"/> 里，
    /// 只在尺寸真的变了、或静置 200ms 后跑一次。
    /// </summary>
    public void UpdateBounds(DialogRect bounds)
    {
        if (bounds.IsEmpty || _dialog == 0) return;

        long now = Environment.TickCount64;
        bool rapid = now - _lastBoundsAt <= DragWindowMs;
        _boundsInWindow = rapid ? _boundsInWindow + 1 : 1;
        _lastBoundsAt = now;
        _bounds = bounds;

        bool wasDragging = _dragging;
        _dragging = _boundsInWindow >= DragEventThreshold;
        if (_dragging) RestartSettle();

        /* 尺寸变化（不是拖动）才允许重算几何；拖动中只挪、尺寸恒定。 */
        bool sizeChanged = bounds.Width != _appliedFrame.Width || bounds.Height != _appliedFrame.Height;
        if (sizeChanged && !_dragging)
            ComputeGeometry($"对话框尺寸变化 {_appliedFrame.Width}x{_appliedFrame.Height} → {bounds.Width}x{bounds.Height}");

        if (wasDragging != _dragging)
            CoreLog.Info($"[文件对话框] 跟随模式：{(_dragging ? "拖动（只挪位，不改尺寸）" : "静置")}");

        ApplyPosition(resize: _dragging == false);
    }

    /// <summary>
    /// 内容变了（结果列表 / 浮层开合 / 目录展开）：重算一次几何再摆位。
    /// 只在**内容变化**时调，不在跟随路径里调 —— 跟随路径只允许 <see cref="ApplyPosition"/>。
    /// </summary>
    private void GeometryChanged()
    {
        if (_dialog == 0) return;
        try
        {
            ComputeGeometry("内容变化");
            ApplyPosition(resize: true);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "内容变化后重算几何"); }
    }

    /// <summary>拖动停止 200ms 后做一次完整几何重算（宽度/列表高/翻转/圆角）。</summary>
    private void RestartSettle()
    {
        _settleTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(DragWindowMs) };
        _settleTimer.Stop();
        _settleTimer.Tick -= OnSettleTick;
        _settleTimer.Tick += OnSettleTick;
        _settleTimer.Start();
    }

    private void OnSettleTick(object? sender, EventArgs e)
    {
        _settleTimer?.Stop();
        _dragging = false;
        _boundsInWindow = 0;
        try
        {
            /* 停手时以"现在这一刻"的对话框矩形为准，别用拖动期间缓存的最后一帧。 */
            DialogRect live = DialogNative.FrameBounds(_dialog);
            if (!live.IsEmpty) _bounds = live;
            ComputeGeometry("拖动结束，静置重算");
            ApplyPosition(resize: true);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "拖动结束后重算几何"); }
    }

    /// <summary>
    /// 完整几何：宽度（对话框 3/5，装不下就按内容最小宽）、列表最大高度、吸附方向、圆角方向。
    /// **这是唯一允许 Measure/UpdateLayout/改 Width/Height 的地方。**
    /// </summary>
    internal void ComputeGeometry(string reason)
    {
        if (_inApply) { _followScheduledInApply++; return; }
        if (_dialog == 0) return;
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        if (!DialogNative.TryGetWorkArea(_dialog, out DialogNative.RECT wr)) return;

        var work = new DialogRect(wr.Left, wr.Top, wr.Right, wr.Bottom);
        /* DPI 必须取**我们自己窗口**的，不能取对话框进程的：
         * 对方常常是 DPI-unaware（GetDpiForWindow 返回 96），拿它算出来的
         * 宽高（DIP）乘出来的物理像素是实际的一半 —— 窗口被 SetWindowPos 压小、
         * WPF 又把尺寸改回去，每挪一次就互相打架，表现就是"拖不跟手 + 内容被裁"。 */
        double scale = WindowDpiScale();
        DialogRect frame = _bounds.IsEmpty ? DialogNative.FrameBounds(_dialog) : _bounds;
        if (frame.IsEmpty) return;

        _inApply = true;
        try
        {
            int widthPx = Math.Max(1, frame.Width * 3 / 5 + (int)Math.Round(16 * scale));
            double widthDip = widthPx / scale;
            if (Math.Abs(Width - widthDip) > 0.5) Width = widthDip;

            /* 内容有最小宽度（目录 chip + 输入框 + ★/🕘/≡）。对话框小的时候
             * 3/5 装不下，右边按钮会被裁到窗口外 —— 量一次最小宽度再撑开。
             *
             * ⚠ 只量**输入行**（InputGrid），绝不能再量 OuterGrid：
             *   结果列表一出来，行里那些长路径会让 OuterGrid.DesiredSize 一路涨，
             *   于是每搜一次这条浮层就左右撑开一次 —— 用户看到的就是"开始搜索之后宽度剧烈增加"。
             *   地址列的中间省略与列宽分配已经保证任何长度的路径都塞得进去，
             *   宽度只该由对话框决定。 */
            try
            {
                if (InputGrid != null)
                {
                    InputGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    /* + 22 InputGrid 左右外边距 + 2 面板描边 + 16 阴影留白 */
                    double needDip = InputGrid.DesiredSize.Width + 40;
                    double capDip = work.Width / scale - 16;
                    if (needDip > Width + 0.5 && capDip > Width)
                        Width = Math.Min(needDip, capDip);
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "浮条按内容加宽"); }
            widthPx = (int)Math.Round(Width * scale);

            DialogBarAnchor.GetTargetRect(frame, work, widthPx,
                Math.Max(1, _barHeightPx), out BarPlacement placement);
            ApplyOrientation(placement == BarPlacement.Below);

            /* 列表高度按"对话框到工作区的剩余空间"算，但**只在这里算一次**。
             * 拖动过程中这个值不变，所以窗口尺寸不变，分层窗口不用整窗重合成。 */
            int availablePx = placement == BarPlacement.Below
                ? Math.Max(0, work.Bottom - frame.Bottom)
                : Math.Max(0, frame.Top - work.Top);
            double inputDip = _barHeightDip;
            double listMaxDip = Math.Min(320, Math.Max(80, availablePx / scale - inputDip - 46));
            if (Math.Abs(ResultList.MaxHeight - listMaxDip) > 1) ResultList.MaxHeight = listMaxDip;
            if (Math.Abs(OverlayList.MaxHeight - listMaxDip) > 1) OverlayList.MaxHeight = listMaxDip;

            UpdateLayout();

            /* 高度：DesiredSize 已经含 OuterGrid 的阴影边距（接触侧 0、外侧 8），
             * 空态就是"输入行 + 8px 透明边距"，展开列表时才加列表高。 */
            double heightDip = inputDip + 8;
            try
            {
                if (OuterGrid != null)
                {
                    OuterGrid.Measure(new Size(Math.Max(1, Width), double.PositiveInfinity));
                    double desired = OuterGrid.DesiredSize.Height;
                    bool hasList = ResultList.Items.Count > 0 || OverlayOpen;
                    heightDip = hasList ? Math.Max(inputDip + 8, desired) : Math.Max(inputDip, desired);
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "浮条按内容量高度"); }

            double capHeightDip = work.Height / scale;
            if (heightDip > capHeightDip) heightDip = Math.Max(inputDip, capHeightDip);
            if (Math.Abs(Height - heightDip) > 0.5) Height = heightDip;

            UpdateLayout();
            _barWidthPx = Math.Max(1, (int)Math.Round(Width * scale));
            _barHeightPx = Math.Max(1, (int)Math.Round(Height * scale));

            /* 高度变了可能要翻面：用真实高度再算一次 placement。 */
            DialogBarAnchor.GetTargetRect(frame, work, _barWidthPx, _barHeightPx, out placement);
            if (placement != _lastPlacement)
            {
                ApplyOrientation(placement == BarPlacement.Below);
                UpdateLayout();
            }

            _lastPlacement = placement;
            _appliedFrame = frame;
            GeometryCount++;
            LastGeometryReason = reason;
            CoreLog.Info($"[文件对话框] 几何：{reason} → {_barWidthPx}x{_barHeightPx}px "
                         + $"placement={placement} 列表max={listMaxDip:0}");
        }
        finally { _inApply = false; }
    }

    private DialogRect _appliedFrame;

    /// <summary>
    /// 只摆位置：算目标矩形（纯算术）→ 一次 SetWindowPos。
    /// 拖动中 <paramref name="resize"/> = false，带 SWP_NOSIZE，窗口尺寸一个像素都不变；
    /// Z 序统一 SWP_NOZORDER（以前不带，等于每 tick 把窗口顶到最前）。
    /// </summary>
    internal void ApplyPosition(bool resize)
    {
        if (_dialog == 0 || _bounds.IsEmpty || _barWidthPx <= 0 || _barHeightPx <= 0) return;
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        if (!DialogNative.TryGetWorkArea(_dialog, out DialogNative.RECT wr)) return;

        /* 非拖动时以"现在这一刻"的对话框矩形为准：SHOW 和最终布局之间常常差几十像素，
         * 而对话框那一下 resize 不一定发 LOCATIONCHANGE —— 只按缓存矩形摆会一直差那几十像素。 */
        if (resize)
        {
            DialogRect live = DialogNative.FrameBounds(_dialog);
            if (!live.IsEmpty) _bounds = live;
        }

        var work = new DialogRect(wr.Left, wr.Top, wr.Right, wr.Bottom);
        DialogRect target = DialogBarAnchor.GetTargetRect(_bounds, work, _barWidthPx, _barHeightPx,
            out BarPlacement placement);
        if (target.IsEmpty) return;

        /* 接触边重叠 2px：DWM/WPF 在透明窗口边缘的抗锯齿会露 1px 亮缝。
         * 上方贴合时，面板底下还有 8px 阴影边距（OuterGrid.Margin 底 8），
         * 所以窗口要再往下压 8px，面板底边才真的贴住对话框顶边。 */
        const int contactOverlap = 2;
        const int shadowMargin = 8;
        int y = placement == BarPlacement.Below
            ? target.Top - contactOverlap
            : target.Top + contactOverlap + shadowMargin;

        uint flags = DialogNative.SWP_NOACTIVATE | DialogNative.SWP_NOZORDER;
        if (!resize) flags |= DialogNative.SWP_NOSIZE;

        long t0 = Stopwatch.GetTimestamp();
        _inApply = true;
        try
        {
            DialogNative.SetWindowPos(hwnd, IntPtr.Zero, target.Left, y,
                _barWidthPx, _barHeightPx, flags);
        }
        finally { _inApply = false; }

        if (placement != _lastPlacement)
        {
            _lastPlacement = placement;
            ApplyOrientation(placement == BarPlacement.Below);
        }

        AppliedCount++;
        double us = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;

        /* 像素误差：摆完之后浮条的实际顶边 vs 对话框底边（Below 时）。
         * 只在开了度量时读 —— 每次多一次 GetWindowRect + DWM 查询，生产路径不需要。 */
        double err = 0;
        if (MetricsEnabled)
        {
            try
            {
                if (DialogNative.GetWindowRect(hwnd, out DialogNative.RECT barRect) && barRect.Bottom > barRect.Top)
                {
                    DialogRect live = DialogNative.FrameBounds(_dialog);
                    if (!live.IsEmpty)
                    {
                        int expected = placement == BarPlacement.Below
                            ? live.Bottom - contactOverlap
                            : live.Top - _barHeightPx + contactOverlap + shadowMargin;
                        err = Math.Abs(barRect.Top - expected);
                    }
                }
            }
            catch { }
        }
        if (err > PixelErrorMax) PixelErrorMax = err;

        RecordMetrics(us, err);
    }

    private void ScheduleFirstFrameFix()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_dialog == 0) return;
                ApplyPosition(resize: true);
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "浮层首帧校正"); }
        }), DispatcherPriority.Render);
    }

    /// <summary>显示后 120/320ms 各补一次位置（对话框最终布局落定后不再发 LOCATIONCHANGE 的情况）。</summary>
    private async Task FixPositionLater(int delayMs)
    {
        try
        {
            await Task.Delay(delayMs).ConfigureAwait(true);
            if (_dialog == 0 || !IsVisible) return;
            ApplyPosition(resize: true);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "浮层延迟校正"); }
    }

    /// <summary>
    /// 浮层窗口自己的 DPI 比例（物理像素 / DIP）。
    ///
    /// 不能拿对话框进程的 DPI：对方可能是 DPI-unaware 的老程序，`GetDpiForWindow` 给它 96，
    /// 于是我们按 1.0 算出来的"物理像素"只有实际的一半，窗口被压小、WPF 再改回去 —— 来回打架。
    /// </summary>
    private double WindowDpiScale()
    {
        try
        {
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (s > 0.1) { _scale = s; return s; }
        }
        catch { }
        return _scale > 0.1 ? _scale : 1;
    }

    /// <summary>
    /// 预热：启动空闲时把窗口建出来、离屏显示一次再藏起来。
    ///
    /// 为什么要：第一次遇到对话框时 `new Window()` + XAML/模板/字体/首次布局的成本
    /// 全都会算在"对话框出现"那一瞬 —— 那正是冷启动"要等一会儿才冒出来"的主因。
    /// 预热放在 ApplicationIdle，失败只记日志，不影响启动。
    /// </summary>
    public void WarmUp()
    {
        try
        {
            nint hwnd = new WindowInteropHelper(this).EnsureHandle();
            if (hwnd == 0) return;
            WindowDpiScale();

            const uint SWP_NOACTIVATE = 0x0010, SWP_NOZORDER = 0x0004;
            DialogNative.SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 600, 60,
                SWP_NOACTIVATE | SWP_NOZORDER);

            if (!IsVisible) Show();
            UpdateLayout();          // 让模板/样式/字体全部实例化一次
            Hide();
            CoreLog.Info("[文件对话框] 浮层已预热：窗口、模板与首次布局就绪");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "预热文件对话框浮层"); }
    }

    /// <summary>
    /// 自检专用：挂到一个真 #32770 上、建出窗口句柄，但不 Show（不闪）。
    /// 之后自检可以直接灌 <see cref="UpdateBounds"/>，走的就是运行期同一条路径。
    /// </summary>
    internal void AttachForTest(nint dialog, DialogRect bounds)
    {
        _dialog = dialog;
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowDpiScale();
        if (hwnd != 0)
        {
            DialogBarAnchor.ConfigureBarChrome(hwnd);
            _anchor.Attach(dialog);
            _anchor.SetBarHandle(hwnd);
        }
        _bounds = bounds;
        ComputeGeometry("自检");
        ApplyPosition(resize: true);
    }

    /// <summary>自检专用：清零度量与计数器。</summary>
    internal void ResetFollowMetricsForTest()    {
        _latencyUs.Clear();
        _pixelError.Clear();
        PixelErrorMax = 0;
        AppliedCount = 0;
        _sizeChangedInApply = 0;
        _followScheduledInApply = 0;
        GeometryCount = 0;
    }

    /// <summary>
    /// 自检专用：量一次"旧路径每个 16ms tick 都要做的那套重排"（Measure + UpdateLayout + 改尺寸）。
    /// 只用于报告改前/改后对比，不参与运行期任何路径。
    /// </summary>
    internal void MeasureHeavyForTest()
    {
        _inApply = true;
        try
        {
            if (OuterGrid != null)
                OuterGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            UpdateLayout();
            double w = Width;
            Width = w + 1;
            UpdateLayout();
            Width = w;
            UpdateLayout();
        }
        catch { }
        finally { _inApply = false; }
    }

    private void RecordMetrics(double us, double err)
    {
        _latencyUs.Add(us);
        _pixelError.Add(err);
        _metricsApplied++;
        if (_latencyUs.Count > 512) _latencyUs.Clear();
        if (_pixelError.Count > 512) _pixelError.Clear();
        if (!MetricsEnabled) return;

        long now = Environment.TickCount64;
        if (_metricsWindowStart == 0) _metricsWindowStart = now;
        if (now - _metricsWindowStart < 1000) return;

        int n = _latencyUs.Count;
        if (n > 0)
        {
            _latencyUs.Sort();
            _pixelError.Sort();
            CoreLog.Info($"[文件对话框] 跟随度量/秒：n={n} "
                         + $"p50={Percentile(_latencyUs, 0.50):0}µs p99={Percentile(_latencyUs, 0.99):0}µs "
                         + $"max={_latencyUs[n - 1]:0}µs 误差p99={Percentile(_pixelError, 0.99):0}px "
                         + $"max={_pixelError[n - 1]:0}px 几何重算={GeometryCount}");
        }
        _latencyUs.Clear();
        _pixelError.Clear();
        _metricsWindowStart = now;
        _metricsApplied = 0;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int i = (int)Math.Round((sorted.Count - 1) * p);
        return sorted[Math.Clamp(i, 0, sorted.Count - 1)];
    }

    /// <summary>输入行高度（40–48 DIP），由设置里的“浮层高度”决定。</summary>
    public void SetBarHeight(int dip)
    {
        int old = _barHeightDip;
        _barHeightDip = Math.Clamp(dip, 40, 48);
        _anchor.BarHeight = _barHeightDip;
        if (InputRow != null) InputRow.Height = new GridLength(_barHeightDip);
        if (old != _barHeightDip) ComputeGeometry("设置里的浮层高度变了");
    }
    /// <summary>
    /// 下方吸附 = 输入框在顶、列表向下长，顶边直角；
    /// 上方吸附 = 输入框在底、列表向上长，底边直角。
    /// </summary>
    private void ApplyOrientation(bool below)
    {
        if (InputGrid == null || ListHost == null || OverlayHost == null || Footer == null) return;

        ListRow.Height = below ? new GridLength(_barHeightDip) : GridLength.Auto;
        InputRow.Height = below ? GridLength.Auto : new GridLength(_barHeightDip);
        FooterRow.Height = GridLength.Auto;

        Grid.SetRow(InputGrid, below ? 0 : 2);
        Grid.SetRow(ListHost, below ? 2 : 0);
        Grid.SetRow(OverlayHost, below ? 2 : 0);
        Grid.SetRow(Footer, 1);

        /* 贴住对话框那一条边是直角（外侧两角保持圆角）；接触侧不留阴影留白，
         * 否则那条 8DIP 的透明边距看起来就是"没贴紧"的缝。 */
        Panel.CornerRadius = below ? new CornerRadius(0, 0, 12, 12) : new CornerRadius(12, 12, 0, 0);
        if (OuterGrid != null)
            OuterGrid.Margin = below ? new Thickness(8, 0, 8, 8) : new Thickness(8, 8, 8, 0);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;

        // 空白区域的点击穿透到对话框：只让输入框/按钮/列表吃掉点击。
        HwndSource.FromHwnd(hwnd)?.AddHook(BarWndProc);

        long ex = (long)DialogNative.GetWindowLongPtr(hwnd, DialogNative.GWL_EXSTYLE);
        DialogNative.SetWindowLongPtr(hwnd, DialogNative.GWL_EXSTYLE,
            (nint)(ex | DialogNative.WS_EX_TOOLWINDOW | DialogNative.WS_EX_NOACTIVATE));
        if (_dialog != 0) DialogNative.SetWindowLongPtr(hwnd, DialogNative.GWLP_HWNDPARENT, _dialog);

        /* 圆角由 XAML 的 Panel.CornerRadius 画（分层窗口），不用 DWM 圆角属性：
         * 那条只对非分层窗口生效，留着反而让人以为圆角是系统给的。 */
        WindowDpiScale();
    }

    private nint BarWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        if (msg != WM_NCHITTEST) return 0;
        try
        {
            int sx = unchecked((short)(long)lParam);
            int sy = unchecked((short)((long)lParam >> 16));
            Point client = PointFromScreen(new Point(sx, sy));
            var hit = InputHitTest(client) as DependencyObject;
            if (hit != null && !IsInteractiveClick(hit))
            {
                handled = true;
                return new nint(-1); // HTTRANSPARENT
            }
        }
        catch { }
        return 0;
    }

    public void HideBar()
    {
        DisposeRedirect();
        _queryTimer.Stop();
        _rows.Clear();
        _keyboardTaken = false;
        SetNoActivate(true);
        _anchor.Detach();
        ListHost.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;

        if (IsVisible) Hide();

        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) DialogNative.SetWindowLongPtr(hwnd, DialogNative.GWLP_HWNDPARENT, 0);
        _dialog = 0;
        _lastQuery = "";
        QueryBox.Text = "";
    }

    // ---------------- 结果 ----------------

    /// <summary>数字键最多到 9：Ctrl+1..9 是按位置对应的，条数再多也没有键可按。</summary>
    internal const int MaxRowLimit = 9;

    private int _rowLimit = 6;

    /// <summary>
    /// 一次呈现几条（1..9，设置页可调，默认 6）。
    /// 行尾的 Ctrl+N 与 Ctrl+数字按键都以它为准 —— "看到几个号就能按几个号"。
    /// </summary>
    internal int RowLimit
    {
        get => _rowLimit;
        set => _rowLimit = Math.Clamp(value, 1, MaxRowLimit);
    }

    public void SetRows(IReadOnlyList<DialogSelection> items, string? emptyHint = null)
    {
        _rows.Clear();
        int index = 0;
        foreach (var item in items)
        {
            if (_rows.Count >= _rowLimit) break;
            index++;
            _rows.Add(ToRow(item, index));
        }

        /* 真实文件图标：最多 6 行，逐个排队即可（以前是 60 行，现在上限就是 6）。 */
        for (int i = 0; i < _rows.Count; i++) LoadIcon(_rows[i]);

        bool any = _rows.Count > 0;
        bool showList = any && !_overlayOpen;
        ListHost.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        if (any)
        {
            ResultList.SelectedIndex = 0;
            ResultList.ScrollIntoView(_rows[0]);
        }
        _status = any ? "" : (emptyHint ?? "");
        GeometryChanged();
    }

    private void LoadIcon(BarRow row)
    {
        try
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            InputPlus.Core.Icons.IconCache.FromFileAsync(row.Item.Path, 18, dpi,
                image => Dispatcher.BeginInvoke(new Action(() => row.SetIcon(image))));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "加载文件对话框结果图标"); }
    }

    /// <summary>
    /// 渲染 / 自检用：塞几行长度差别很大的示例，专门看
    /// "图标 | 文件名 | 地址 | 快捷键" 四列的对齐和地址的中间省略。
    /// </summary>
    internal void SetRowsForRender()
    {
        string veryLong = @"C:\Users\64381\AppData\Local\Packages\Microsoft.Windows.Explorer_8wekyb3d8bbwe"
                          + @"\LocalState\BreadcrumbBar\缓存目录\第二级\第三级\第四级\⑤号文件夹";
        SetRows(new[]
        {
            new DialogSelection(@"D:\software\图吧工具箱202601\data\文件夹说明.txt", false, "文件夹说明.txt", "Ctrl+1"),
            new DialogSelection(@"C:\Users\64381\AppData\Local\Temp\inputplus\缓存\文件夹说明.txt", false, "文件夹说明.txt", "Ctrl+2"),
            new DialogSelection(@"\\nas-01\共享\项目\2026\第三季度\投标资料\文件1741661830504(1)(1).xlsx", false, "文件1741661830504(1)(1).xlsx", "Ctrl+4"),
            new DialogSelection(veryLong + @"\超长路径用来验证中间省略.txt", false, "超长路径用来验证中间省略.txt", "Ctrl+3"),
            new DialogSelection(@"C:\Users\64381\OneDrive\桌面\简历文件", true, "简历文件", "Ctrl+5"),
            new DialogSelection(@"D:\something\WPS Cloud\WPSDrive\3629...\应用\微信文件", true, "微信文件", "Ctrl+6"),
        });
    }

    /// <summary>渲染用：手动摆出"鼠标停在第 N 行"的脚注，截图才看得到面包屑。</summary>
    internal void ShowBreadcrumbForRender(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;
        ShowPathBreadcrumb(_rows[rowIndex].Item.Path);
    }

    /* ---------------- 脚注：鼠标停在哪一条，就显示那一条的地址面包屑 ----------------
       列表里地址那一列是中间省略的（空间有限），脚注这一行给的是**完整路径**，
       最后一级（真正要看的目录 / 文件名）加粗 + 主文字色：一眼能看出"我要的东西在哪一层"。
       不悬停时恢复成快捷键提示，不占额外空间，也不改浮层高度（同一行里换内容）。 */

    private void OnResultHover(object sender, MouseEventArgs e)
    {
        BarRow? row = RowUnder(e.OriginalSource as DependencyObject);
        if (row == null) { RestoreFooterHint(); return; }
        ShowPathBreadcrumb(row.Item.Path);
    }

    private void OnResultLeave(object sender, MouseEventArgs e) => RestoreFooterHint();

    private BarRow? RowUnder(DependencyObject? source)
    {
        if (source == null || ResultList == null) return null;
        try
        {
            var container = ItemsControl.ContainerFromElement(ResultList, source) as ListBoxItem;
            return container?.DataContext as BarRow;
        }
        catch { return null; }
    }

    private void RestoreFooterHint()
    {
        if (FooterText == null || FooterPath == null) return;
        FooterPath.Inlines.Clear();
        FooterPath.Visibility = Visibility.Collapsed;
        FooterText.Visibility = Visibility.Visible;
    }

    private void ShowPathBreadcrumb(string? path)
    {
        if (FooterPath == null || FooterText == null) return;
        var segments = SplitPathSegments(path ?? "");
        if (segments.Count == 0) { RestoreFooterHint(); return; }

        /* 太长的路径只留"根 + 最后几级"：面包屑的重点永远在最后一级，
           整串硬塞进去会被 TextTrimming 从尾部裁掉 —— 恰好把加粗的那一段裁没。 */
        bool dropped = false;
        while (segments.Count > 4 && BreadcrumbChars(segments) > 78)
        {
            segments.RemoveAt(1);      // 只删中间层，根（盘符 / UNC）留着
            dropped = true;
        }

        FooterPath.Inlines.Clear();

        var icon = new Run("\uE8B7")
        {
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
        };
        icon.SetResourceReference(TextElement.ForegroundProperty, "Brush.OverlayTextTertiary");
        FooterPath.Inlines.Add(icon);

        var gap = new Run("  ");
        gap.SetResourceReference(TextElement.ForegroundProperty, "Brush.OverlayTextTertiary");
        FooterPath.Inlines.Add(gap);

        for (int i = 0; i < segments.Count; i++)
        {
            bool last = i == segments.Count - 1;
            if (i > 0 || (dropped && i == 1))
            {
                var sep = new Run("  >  ");
                sep.SetResourceReference(TextElement.ForegroundProperty, "Brush.OverlayTextTertiary");
                FooterPath.Inlines.Add(sep);
            }
            if (dropped && i == 1)
            {
                var skipped = new Run("…  >  ");
                skipped.SetResourceReference(TextElement.ForegroundProperty, "Brush.OverlayTextTertiary");
                FooterPath.Inlines.Add(skipped);
            }

            var run = new Run(segments[i]) { FontWeight = last ? FontWeights.SemiBold : FontWeights.Normal };
            run.SetResourceReference(TextElement.ForegroundProperty,
                last ? "Brush.OverlayText" : "Brush.OverlayTextTertiary");
            FooterPath.Inlines.Add(run);
        }

        FooterText.Visibility = Visibility.Collapsed;
        FooterPath.Visibility = Visibility.Visible;
    }

    /// <summary>面包屑整串大约占多少字符（分隔符按 3 个算）。</summary>
    private static int BreadcrumbChars(List<string> segments)
    {
        int total = Math.Max(0, segments.Count - 1) * 3;
        foreach (string s in segments) total += s.Length;
        return total;
    }

    /// <summary>
    /// 把路径拆成面包屑的层级：`D:\a\b` → D: / a / b；
    /// UNC 的 `\\host\share` 按"根 + 各级"拆，盘符那一级单独留下（它本身就是一层）。
    /// </summary>
    private static List<string> SplitPathSegments(string path)
    {
        var list = new List<string>();
        string full = (path ?? "").Trim();
        if (full.Length == 0) return list;

        try
        {
            string trimmed = full.TrimEnd('\\', '/');
            string root = Path.GetPathRoot(trimmed) ?? "";
            if (root.Length > 0)
            {
                list.Add(root.TrimEnd('\\', '/'));       // "D:" / "\\\\host\\share"
                string rest = trimmed.Length > root.Length ? trimmed[root.Length..] : "";
                list.AddRange(rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries));
            }
            else
            {
                list.AddRange(full.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }
        catch
        {
            list.Clear();
            list.Add(full);
        }

        /* UNC 根会拆出 \\host 和 share 两段，粘回去当一层，读起来才像资源管理器。 */
        if (list.Count >= 2 && list[0].StartsWith(@"\\", StringComparison.Ordinal))
        {
            string host = list[0];
            list.RemoveAt(0);
            if (list.Count > 0) list[0] = host + "\\" + list[0];
            else list.Add(host);
        }

        return list;
    }

    private BarRow ToRow(DialogSelection item, int shortcutIndex = 0)
    {
        string dir = "";
        try
        {
            dir = item.IsFolder
                ? System.IO.Path.GetDirectoryName(item.Path.TrimEnd('\\')) ?? ""
                : System.IO.Path.GetDirectoryName(item.Path) ?? "";
        }
        catch { }

        return new BarRow
        {
            Item = item,
            Glyph = item.IsFolder ? "\uE8B7" : "\uE7C3",
            Title = item.DisplayName,
            Subtitle = dir,
            /* 行尾的快捷键 = 这一行的位置：Ctrl+1 永远是第一条。
             * 不用 DialogSelection.Source —— 那是"这条从哪来的"，不是给用户按的键。 */
            Badge = shortcutIndex >= 1 && shortcutIndex <= _rowLimit ? "Ctrl+" + shortcutIndex : "",
        };
    }

    /// <summary>推荐文件夹 chip：null = 隐藏；否则显示目录名，tooltip 给全路径。</summary>
    internal void SetFolderChip(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            FolderChip.Visibility = Visibility.Collapsed;
            return;
        }

        string name;
        try { name = System.IO.Path.GetFileName(path.TrimEnd('\\')); }
        catch { name = path; }
        if (string.IsNullOrEmpty(name)) name = path;

        ChipText.Text = name;
        FolderChip.ToolTip = path + "（Ctrl+G）";
        FolderChip.Visibility = Visibility.Visible;
    }

    internal void ShowFavoritesOverlay(IReadOnlyList<QuickSwitchRow> rows, bool addEntry)
    {
        _overlayKind = OverlayKind.Favorites;
        var list = new List<QuickSwitchRow>(rows);
        if (addEntry)
        {
            list.Add(new QuickSwitchRow
            {
                Title = "在当前目录添加收藏",
                Subtitle = "把对话框当前所在的目录加进收藏",
                Glyph = "\uE710",
                Action = QuickSwitchAction.AddFavorite,
            });
        }
        OpenOverlay("Qbar 固定项", list);
    }

    internal void ShowHistoryOverlay(IReadOnlyList<QuickSwitchRow> rows)
    {
        _overlayKind = OverlayKind.History;
        OpenOverlay("搜索前建议", new List<QuickSwitchRow>(rows));
    }

    internal void ShowMenuOverlay()
    {
        _overlayKind = OverlayKind.Menu;
        OpenOverlay("更多", QuickSwitchCatalog.Menu());
    }

    internal void ShowHideOverlay()
    {
        _overlayKind = OverlayKind.HideMenu;
        OpenOverlay("隐藏此搜索栏", QuickSwitchCatalog.HideMenu());
    }

    private void OpenOverlay(string title, IReadOnlyList<QuickSwitchRow> rows)
    {
        /* 浮层要键盘（↑↓/Enter/Esc），但**不能**裸调 WPF 的 Activate()：
         * 我们这个进程不是前台进程，Activate() 失败时 Windows 会把前台交给
         * z 序里下一个程序（实测是 Edge），于是"点了更多，菜单闪一下、浮条整个消失"。
         * 改用和输入行同一条焦点阶梯：失败就装键盘转发钩子，绝不让前台跑掉。 */
        /* 先立"菜单开着"的旗，再去抢键盘：抢键盘有可能让前台短暂离开对话框，
         * 服务层看到 OverlayOpen=true 才不会被判成"用户离开了对话框"而整条收起。 */
        _overlayOpen = true;
        SetNoActivate(false);
        try
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            var taken = DialogFocusLadder.TryTakeKeyboard(hwnd, out string detail);
            CoreLog.Info($"[浮条] 菜单要键盘：{taken}（{detail}）");
            if (taken == DialogFocusLadder.Result.NeedsKeyboardHook) InstallRedirect();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "菜单抢键盘"); }

        _overlayRows.Clear();
        foreach (var row in rows) _overlayRows.Add(row);
        OverlayTitle.Text = title;
        OverlayList.SelectedIndex = _overlayRows.Count > 0 ? 0 : -1;

        ListHost.Visibility = Visibility.Collapsed;
        OverlayHost.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Collapsed;
        if (_overlayRows.Count > 0) OverlayList.ScrollIntoView(_overlayRows[0]);
        LoadOverlayIcons();
        OverlayList.Focus();
        GeometryChanged();
    }

    /// <summary>浮层每行的真实 shell 图标（收藏/建议/目录预览都用 Explorer 同一套）。</summary>
    private void LoadOverlayIcons()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var row in _overlayRows)
        {
            if (string.IsNullOrWhiteSpace(row.IconPath)) continue;
            try
            {
                InputPlus.Core.Icons.IconCache.FromFileAsync(row.IconPath, 18, dpi,
                    image => Dispatcher.BeginInvoke(new Action(() => row.SetIcon(image))));
            }
            catch { }
        }
    }

    internal void CloseOverlay(bool focusInput = true)
    {
        if (!_overlayOpen) return;
        _overlayOpen = false;
        _overlayKind = OverlayKind.None;
        _expandedRows.Clear();
        _expandedFolder = null;
        _overlayRows.Clear();
        OverlayHost.Visibility = Visibility.Collapsed;

        bool hasRows = _rows.Count > 0;
        ListHost.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        if (focusInput)
        {
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
        }
        GeometryChanged();
    }

    internal bool OverlayOpen => _overlayOpen;

    /// <summary>浮层里有几行（自检用）。</summary>
    internal int OverlayRowCount => _overlayRows.Count;

    /// <summary>
    /// 按序号激活浮层里的一行（自检用，不经过真实鼠标）。
    ///
    /// 为什么要有：菜单行是"点了就做一件事"，而这套动作链（行 → ActionRequested →
    /// FileDialogBarService → AppHost）以前没有任何断言盯着；用户报"点了一点反应都没有"
    /// 时只能靠手点。现在自检可以直接把第二行（设置）按下去，断言事件确实发出来了。
    /// </summary>
    internal bool ActivateOverlayRowForTest(int index)
    {
        if (!_overlayOpen || index < 0 || index >= _overlayRows.Count) return false;
        OverlayList.SelectedItem = _overlayRows[index];
        ActivateOverlay();
        return true;
    }

    /// <summary>浮层第 n 行的标题（自检用）。</summary>
    internal string? OverlayRowTitleForTest(int index)
        => index >= 0 && index < _overlayRows.Count ? _overlayRows[index].Title : null;

    private void ActivateOverlay()
    {
        if (!_overlayOpen || OverlayList.SelectedItem is not QuickSwitchRow row) return;
        if (!row.Enabled) return;
        CloseOverlay();
        if (!string.IsNullOrWhiteSpace(row.Path)) FolderPicked?.Invoke(row.Path!);
        else if (row.Action is { } action) ActionRequested?.Invoke(action);
    }

    private string? _expandedFolder;
    private readonly List<QuickSwitchRow> _expandedRows = new();

    /// <summary>鼠标停在固定项文件夹上：把它的内容以缩进列表展开（只看，不导航）。</summary>
    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        if (!_overlayOpen) return;
        if (ItemsControl.ContainerFromElement(OverlayList, e.OriginalSource as DependencyObject)
            is not ListBoxItem { DataContext: QuickSwitchRow row }) return;
        if (!row.Expandable || string.IsNullOrWhiteSpace(row.Path)) return;
        if (string.Equals(_expandedFolder, row.Path, StringComparison.OrdinalIgnoreCase)) return;

        CollapseExpanded();
        if (!System.IO.Directory.Exists(row.Path)) return;

        int index = _overlayRows.IndexOf(row);
        if (index < 0) return;

        try
        {
            var entries = new System.IO.DirectoryInfo(row.Path).EnumerateFileSystemInfos()
                .OrderByDescending(x => x is System.IO.DirectoryInfo)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();

            int insert = index + 1;
            foreach (var entry in entries)
            {
                bool dir = entry is System.IO.DirectoryInfo;
                var child = new QuickSwitchRow
                {
                    Title = "    " + entry.Name,
                    Subtitle = dir ? "文件夹" : "文件",
                    Glyph = dir ? "\uE8B7" : "\uE7C3",
                    IconPath = entry.FullName,
                    Enabled = false,
                };
                _overlayRows.Insert(insert++, child);
                _expandedRows.Add(child);
            }
            _expandedFolder = row.Path;
            LoadOverlayIcons();
            OverlayList.ScrollIntoView(row);
            GeometryChanged();
        }
        catch { CollapseExpanded(); }
    }

    private void CollapseExpanded()
    {
        foreach (var child in _expandedRows) _overlayRows.Remove(child);
        _expandedRows.Clear();
        _expandedFolder = null;
    }

    private void OnOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(OverlayList, e.OriginalSource as DependencyObject)
            is ListBoxItem { DataContext: QuickSwitchRow row })
        {
            e.Handled = true;
            OverlayList.SelectedItem = row;
            ActivateOverlay();
        }
    }

    private void OnOverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (!_overlayOpen) return;
        if (e.Key == Key.Escape) { e.Handled = true; CloseOverlay(); return; }
        if (e.Key == Key.Enter) { e.Handled = true; ActivateOverlay(); }
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_overlayOpen && _overlayKind == OverlayKind.Favorites) { CloseOverlay(); return; }
        FavoritesRequested?.Invoke();
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_overlayOpen && _overlayKind == OverlayKind.History) { CloseOverlay(); return; }
        HistoryRequested?.Invoke();
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        // 同一个按钮第二次点击 = 折叠；从别的浮层切过来则直接换内容。
        if (_overlayOpen && _overlayKind == OverlayKind.Menu) { CloseOverlay(); return; }
        MenuRequested?.Invoke();
    }
    private void OnFolderChipClick(object sender, RoutedEventArgs e) => FolderChipRequested?.Invoke();

    private string _status = "";

    public void SetStatus(string text)
    {
        _status = text ?? "";
        Panel.ToolTip = _status.Length == 0 ? null : _status;
    }

    /// <summary>外部（服务层）预填查询串，不触发一次无意义的重查。</summary>
    public void SetQuery(string? text)
    {
        QueryBox.Text = text ?? "";
        QueryBox.CaretIndex = QueryBox.Text.Length;
        _lastQuery = QueryBox.Text.Trim();
    }

    /// <summary>打开收藏/历史前清空输入框（规格要求），但不要把结果列表顶出来。</summary>
    internal void ClearQueryForOverlay()
    {
        if (QueryBox.Text.Length > 0) QueryBox.Text = "";
        _lastQuery = "";
    }

    // ---------------- 输入 ----------------

    private void ScheduleQuery()
    {
        if (_imeComposing) return;      // 组合期间不查，等上屏后那一次 TextInput
        _queryTimer.Stop();
        _queryTimer.Start();
    }

    private void FlushQuery()
    {
        _queryTimer.Stop();
        string text = QueryBox.Text.Trim();
        if (text == _lastQuery) return;
        _lastQuery = text;
        QueryChanged?.Invoke(text, Scope);
    }

    /// <summary>Ctrl+数字：主键盘 D1..D9 与小键盘 NumPad1..9 都认；其它键返回 0。</summary>
    private static int DigitSlot(Key key)
    {
        if (key >= Key.D1 && key <= Key.D9) return (int)key - (int)Key.D0;
        if (key >= Key.NumPad1 && key <= Key.NumPad9) return (int)key - (int)Key.NumPad0;
        return 0;
    }

    /* 自检用：行数与行尾编号是"看到什么就按什么"的契约，必须能直接断言。 */
    internal int ResultRowCountForTest => _rows.Count;
    internal IReadOnlyList<string> RowBadgesForTest => _rows.Select(r => r.Badge).ToList();
    internal static int DigitSlotForTest(Key key) => DigitSlot(key);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_overlayOpen)
        {
            if (e.Key == Key.Escape) { e.Handled = true; CloseOverlay(); return; }
            if (e.Key == Key.Enter) { e.Handled = true; ActivateOverlay(); return; }
            return; // 上下键交给 OverlayList
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.G)
        {
            e.Handled = true;
            FolderChipRequested?.Invoke();
            return;
        }

        /* Ctrl+1..6：直接提交第 N 条，和用鼠标点它一下完全等价。
           行尾那个 Ctrl+N 徽标就是这里的编号（见 SetRows / ToRow），
           所以"看到的号"和"按下去的号"永远是同一个。
           没有第 N 条时不吞键，把这次按键原样留给下面的对话框。 */
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            int slot = DigitSlot(e.Key);
            if (slot >= 1 && slot <= _rowLimit)
            {
                e.Handled = true;
                if (slot <= _rows.Count)
                {
                    ResultList.SelectedIndex = slot - 1;
                    CommitCurrent();
                }
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                HideBar();
                Dismissed?.Invoke();
                return;

            case Key.Enter:
                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) RevealCurrent();
                else CommitCurrent();
                return;

            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                return;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                return;

            case Key.PageDown:
                e.Handled = true;
                MoveSelection(8);
                return;

            case Key.PageUp:
                e.Handled = true;
                MoveSelection(-8);
                return;

            case Key.Tab:
                /* Tab 在 [输入框 → 收藏 → 历史 → 菜单] 间循环（规格要求）；
                 * 切换搜索范围改到 Ctrl+Tab，避免两个功能抢同一个键。 */
                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    Scope = DialogSearchQuery.NextScope(Scope);
                    ScopeText.Text = DialogSearchQuery.ScopeLabel(Scope);
                    _lastQuery = "";              // 范围变了必须重查
                    QueryChanged?.Invoke(QueryBox.Text.Trim(), Scope);
                }
                else
                {
                    MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
                return;
        }
    }

    private void MoveSelection(int delta)    {
        if (_rows.Count == 0) return;
        int index = Math.Clamp(ResultList.SelectedIndex + delta, 0, _rows.Count - 1);
        ResultList.SelectedIndex = index;
        ResultList.ScrollIntoView(_rows[index]);
    }

    /// <summary>Alt+Enter：不碰对话框，直接在资源管理器里选中这一项。</summary>
    private void RevealCurrent()
    {
        if (ResultList.SelectedItem is BarRow row) { RevealRequested?.Invoke(row.Item); return; }

        string typed = QueryBox.Text.Trim();
        if (typed.Length == 0) return;
        try
        {
            if (File.Exists(typed) || Directory.Exists(typed))
                RevealRequested?.Invoke(new DialogSelection(typed, Directory.Exists(typed),
                    System.IO.Path.GetFileName(typed.TrimEnd('\\'))));
        }
        catch { }
    }

    private void CommitCurrent()
    {
        if (ResultList.SelectedItem is BarRow row)
        {
            CommitRequested?.Invoke(row.Item);
            return;
        }

        /* 没结果时允许直接回车一个存在的路径：用户经常把整条路径粘进来。 */
        string typed = QueryBox.Text.Trim();
        if (typed.Length == 0) return;
        try
        {
            if (Directory.Exists(typed))
                CommitRequested?.Invoke(new DialogSelection(typed, true,
                    System.IO.Path.GetFileName(typed.TrimEnd('\\'))));
            else if (File.Exists(typed))
                CommitRequested?.Invoke(new DialogSelection(typed, false, System.IO.Path.GetFileName(typed)));
        }
        catch { }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ResultList, e.OriginalSource as DependencyObject)
            is ListBoxItem { DataContext: BarRow row })
        {
            e.Handled = true;
            CommitRequested?.Invoke(row.Item);
        }
    }

    // ---------------- 键盘重定向（第四级） ----------------

    private bool InstallRedirect()
    {
        var redirect = TemporaryKeyboardRedirect.Install();
        if (redirect == null) return false;

        _redirect = redirect;
        redirect.Char += c => Dispatcher.BeginInvoke(new Action(() => AppendText(c.ToString())));
        redirect.Backspace += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (QueryBox.Text.Length == 0) return;
            QueryBox.Text = QueryBox.Text[..^1];
            QueryBox.CaretIndex = QueryBox.Text.Length;
        }));
        redirect.Enter += () => Dispatcher.BeginInvoke(new Action(CommitCurrent));
        redirect.Escape += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            HideBar();
            Dismissed?.Invoke();
        }));
        redirect.OtherKey += vk => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (vk == VK.Up) MoveSelection(-1);
            else if (vk == VK.Down) MoveSelection(1);
        }));

        SetStatus("输入中");
        CoreLog.Info("浮层使用键盘重定向收字符（第四级兜底）");
        return true;
    }

    private void AppendText(string text)
    {
        QueryBox.Text += text;
        QueryBox.CaretIndex = QueryBox.Text.Length;
    }

    private void DisposeRedirect()
    {
        var redirect = _redirect;
        _redirect = null;
        redirect?.Dispose();
    }
}
