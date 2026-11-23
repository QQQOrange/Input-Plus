using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Icons;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;
using InputPlus.Core.Suggestions;
using InputPlus.Core.Windows;
using InputPlus.UI.Theme;

namespace InputPlus.UI.Windows;

public partial class QbarWindow : Window
{
    /// <summary>
    /// 字号测量的字形样本：全角汉字 + 带上下伸部的拉丁字母，
    /// 量出来的行高对中英文都够用。
    /// **刻意不做成可翻译文本** —— 加了译文行高就会跟着变，布局对不上。
    /// </summary>
    private const string GlyphSample = "汉字Agqy";

    private readonly AppHost _host;
    private List<QbarItem> _items = new();
    private QbarChip? _chip;
    private bool _closing;
    private bool _suppressTextChanged;
    private bool _imeApplied;
    private string _imeKey = "";
    private DateTime _imeAt = DateTime.MinValue;
    private int _imeRetry;
    private string _imeLoggedMode = "";
    private DispatcherTimer? _imeTimer;
    private DispatcherTimer? _imeVerify;

    /* ---- 搜索联想 ----
     * 联想是"打字过程中的辅助"，所以这里的每个字段都在解决"过期"问题：
     *   _suggestTimer   250ms 防抖；每敲一个键都重置，避免每个字符发一次请求
     *   _suggestCts     当前在途请求；下一次输入直接取消，省带宽也避免旧词后到
     *   _suggestSeq     请求序号；取消有竞态（响应可能已在路上），回来时对不上号就丢
     *   _suggestVisible 列表是否真的在显示；↑↓/Enter/Esc 都先问它，避免误伤结果列表
     */
    private const int SuggestDebounceMs = 250;
    private readonly DispatcherTimer _suggestTimer;
    private CancellationTokenSource? _suggestCts;
    private int _suggestSeq;
    private bool _suggestVisible;

    /* ---- 文件菜单模式 ----
     * 在文件结果上右键时，列表**原地**换成一串针对这个文件的操作项，
     * 而不是弹一个系统菜单。这几个字段就是这次"换脸"的状态：
     *   _menuFile         正在给它开菜单的那条结果（非空 = 菜单模式）
     *   _menuReturnItems  换脸前的搜索结果，Esc/右键/打字时原样换回来
     *   _menuReturnIndex  换脸前选中的行，回来时还原
     *   _menuActions      菜单行 id → 动作。行本身只是个 QbarResult，
     *                     不能用委托直接挂（列表绑定的是数据），所以按 id 查表。
     */
    /* ---- 拖动面板（物理像素，跨 DPI 不用换算） ----
     *   _dragging      正在拖的手势里
     *   _dragMoved     这次手势真的移动过（只是点一下空白处不该写盘）
     *   _dragCursor    按下那一刻的光标位置
     *   _dragWindowRect按下那一刻的窗口矩形
     */
    private bool _dragging;
    private bool _dragMoved;
    private Core.Interop.NativeMethods.POINT _dragCursor;
    private Core.Interop.NativeMethods.RECT _dragWindowRect;
    private DispatcherTimer? _dragPoll;

    private const int VK_LBUTTON = 0x01;
    private const int VK_ESCAPE = 0x1B;

    /// <summary>这个键此刻是否被按住（查物理状态，不受 UIPI / 钩子影响）。</summary>
    private static bool IsDown(int vk) => (Core.Interop.NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private QbarItem? _menuFile;
    private List<QbarItem> _menuReturnItems = new();
    private int _menuReturnIndex;
    private readonly Dictionary<string, Action> _menuActions = new(StringComparer.Ordinal);

    /* ---- 语义层查询调度（防抖 + 后台文件检索） ----
     * 为什么要这几个字段，逐条对应红线：
     *   _queryTimer  输入防抖 45ms：逐键发查询会让索引扫描跟着按键跑，白烧 CPU
     *   _querySeq    请求序号：后台查询会乱序回来，旧结果盖新结果是这类改造最经典的 bug
     *   _imeComposing / _pendingQueryAfterComposition
     *                输入法还在上屏时**不许**查：此时 TextBox 里的内容只是候选，
     *                按半截拼音去搜盘既慢又全是噪声。上屏后只补查一次。
     *   _appliedFilter  过滤器 chip（doc: / pic: …）。它不占输入框，但每次查询都要
     *                拼回翻译层的输入串，否则 chip 会变成"只显示不生效"。
     */
    private const int QueryDebounceMs = 45;
    private readonly DispatcherTimer _queryTimer;
    private int _querySeq;
    private bool _imeComposing;
    private bool _pendingQueryAfterComposition;
    private QbarFilter? _appliedFilter;
    private QbarSearchOutcome? _lastOutcome;
    private string _lastQueryText = "";

    /// <summary>底部提示行的默认文案（离开文件菜单时要还原）。</summary>
    private const string FooterDefaultText = "↑↓ 选择    Enter 执行    Tab 补全    Esc 关闭";

    public QbarWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Deactivated += OnDeactivated;
        Activated += (_, _) => FocusInputNow();
        PreviewKeyDown += OnWindowKeyDown;
        Results.SelectionChanged += (_, _) =>
        {
            UpdateHint();
            SyncSuggestionSelection();
        };

        /* 建议在后台线程采集完会发一次 Changed。只有"空输入的空态"需要重画：
         * 用户已经在打字时列表里是搜索结果，不能被迟到的建议覆盖（红线 6）。 */
        if (_host.Suggestions != null) _host.Suggestions.Changed += OnSuggestionsChanged;
        /* 拖动面板空白处改位置。
         *
         * 不用 Window.DragMove()：它把拖动交给系统模态移动循环（SendMessage WM_NCLBUTTONDOWN），
         * 那种循环只认"真鼠标"的移动消息，注入/重定向的鼠标事件在第一下之后就断了 ——
         * 自己跟踪 GetCursorPos + SetWindowPos，位置算得清、拖到屏幕边上也好夹。 */
        Panel.PreviewMouseLeftButtonDown += OnPanelMouseDown;
        Panel.PreviewMouseMove += OnPanelMouseMove;   // 只管光标形状，拖动由定时器驱动

        // 构造函数里建一次，之后只 Start/Stop：DispatcherTimer 反复 new 会挂在
        // Dispatcher 的计时器列表上，长期开着面板会慢慢积累。
        _suggestTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SuggestDebounceMs),
        };
        _suggestTimer.Tick += OnSuggestTimerTick;

        // 查询防抖计时器同样只建一次：DispatcherTimer 反复 new 会挂在 Dispatcher 上越积越多
        _queryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(QueryDebounceMs),
        };
        _queryTimer.Tick += OnQueryTimerTick;

        /* 输入法组合期判定：PreviewTextInputStart/Update 里标记，TextInput（上屏那一下）清掉。
         * 不能只看 TextChanged —— 多数中文 IME 在组合期间也会改 TextBox.Text，
         * 只看 TextChanged 会在候选阶段就把半截拼音送去搜盘。 */
        TextCompositionManager.AddPreviewTextInputStartHandler(Input, OnCompositionStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Input, OnCompositionUpdate);
        TextCompositionManager.AddPreviewTextInputHandler(Input, OnCompositionEnd);

        ApplyStyle();
    }

    // ---------------- 抢前台与输入焦点 ----------------

    private IntPtr _hwnd;
    private System.Windows.Threading.DispatcherTimer? _focusTimer;
    private int _focusTries;
    private DateTime _openedAt = DateTime.MinValue;

    /// <summary>
    /// 打开后的宽限期（毫秒）：这段时间里忽略 Deactivated。
    ///
    /// 面板刚弹出时正在跟系统抢前台，抢的过程中会短暂收到 Deactivated；
    /// 原来直接 HideQbar，用户看到的是"面板闪一下就没了，字也没打进去"。
    /// </summary>
    private const int OpenGraceMs = 320;

    /// <summary>
    /// 把输入框的焦点真正拿到手。
    ///
    /// ⚠ 这里不能只写 <c>Input.Focus()</c>：窗口本身没被激活时，
    /// WPF 的焦点只是"窗口内部的逻辑焦点"，键盘输入仍然去前台那个程序。
    /// 必须先把窗口激活（<see cref="WindowService.ForceForeground"/>），
    /// 再 Focus + Keyboard.Focus，才会真的把输入接管过来。
    /// </summary>
    private void FocusInputNow()
    {
        try
        {
            if (!IsActive) Activate();
            Focus();
            Keyboard.Focus(Input);
            Input.CaretIndex = Input.Text.Length;

            /* 输入法只在**焦点真正拿到之后**切：盲按 Shift 是"切换"，不是"确保"，
             * 现在已经是中文时再按一下反而变英文。这里用 ImeGuard 查布局后按需切。 */
            if (!_imeApplied && IsActive)
            {
                /* EnsureComposing 会同时检查“布局”和“输入法的中/英模式”。
                 * 只有真的成功才记账；失败时保持 false，等下一次激活/焦点
                 * 事件再试一次（中文输入法刚启动时 IMM 上下文可能还没就绪）。 */
                _imeApplied = ApplyImePreference();
                if (!_imeApplied) QueueImePreference(180);
            }
        }
        catch
        {
            // 焦点操作失败不该影响面板本身
        }
    }

    /// <summary>
    /// 按当前面板状态决定输入语言：
    ///   已有引擎标签 → 中文（用户接下来要打的是搜索内容）；
    ///   空面板       → 按“打开时切英文”选项，方便直接打 bd / gg 这类关键字。
    ///
    /// 只做“确保”，不做“切换”：已经是目标语言时什么都不发，避免把
    /// 用户手工切好的中/英状态又翻回去。
    /// </summary>
    private bool ApplyImePreference(bool force = false)
    {
        try
        {
            /* 空面板不碰输入法：用户系统当前是什么中/英状态，就保持什么状态。
             * 唯一要做的是把上一次“引擎标签”可能遗留的禁用状态恢复回来。 */
            if (_chip == null)
            {
                if (!InputMethod.GetIsInputMethodEnabled(Input))
                    InputMethod.SetIsInputMethodEnabled(Input, true);
                return true;
            }

            /* 只有“已经带出引擎标签”并且用户勾了“认出引擎后切回中文”
             * 时才切中文。 */ 
            string want = "zh:" + _chip.Key;
            if (!force && _imeKey == want && (DateTime.UtcNow - _imeAt).TotalMilliseconds < 800)
                return true;

            bool Done(bool ok)
            {
                if (ok)
                {
                    _imeKey = want;
                    _imeAt = DateTime.UtcNow;
                }
                return ok;
            }

            InputMethod.SetIsInputMethodEnabled(Input, true);
            if (!_host.Settings.Qbar.SwitchBackOnEngine)
            {
                // 用户关掉了“认出引擎后切回中文”：保持输入法现状。
                return Done(true);
            }

            return Done(ShiftImeTo(true));
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "切换输入法");
            return false;
        }
    }

    /// <summary>
    /// 用 Shift 把当前输入法拨到中文/英文模式，不切换键盘布局。
    /// 先读 WPF 的 ImeConversionMode，已经是目标状态就什么都不发；
    /// 读不到明确状态（DoNotCare）时也不盲按，交给下一轮重试。
    /// </summary>
    private bool ShiftImeTo(bool chinese)
    {
        InputMethod.SetPreferredImeState(Input, InputMethodState.On);
        var ime = InputMethod.Current;
        if (ime == null) return false;

        if (ime.ImeState != InputMethodState.On)
            ime.ImeState = InputMethodState.On;

        var mode = ime.ImeConversionMode;
        bool nowChinese = (mode & ImeConversionModeValues.Native) != 0;
        bool nowEnglish = (mode & ImeConversionModeValues.Alphanumeric) != 0;

        string actual = mode.ToString();
        if (_imeLoggedMode != actual)
        {
            _imeLoggedMode = actual;
            CoreLog.Info($"[Qbar] IME 当前={actual}，目标={(chinese ? "中文" : "英文")}");
        }

        if (chinese ? nowChinese : nowEnglish) return true;
        if (!nowChinese && !nowEnglish) return false;   // DoNotCare / 还没挂好

        Core.Input.InputSender.Tap(0x10);   // VK_SHIFT：只切中英模式，不换布局
        try
        {
            /* 搜狗/微软拼音对合成 Shift 的反应有延迟，而且 WPF 的
             * ImeConversionMode 有时要到下一次输入才刷新。这里再直接
             * 把 TSF 的转换模式拨到目标值作为兜底 —— 仍然不换键盘布局。 */
            var target = chinese ? ImeConversionModeValues.Native : ImeConversionModeValues.Alphanumeric;
            InputMethod.SetPreferredImeConversionMode(Input, target);
            ime.ImeConversionMode = target;
        }
        catch { }
        return true;
    }

    /// <summary>
    /// 焦点刚拿到时 TSF 有时还没挂好，立刻设 IME 会被随后到来的
    /// 焦点消息覆盖。延迟一拍再强制应用一次，只延后不阻塞面板显示。
    /// </summary>
    private void QueueImePreference(int delayMs = 140)
    {
        _imeRetry = 0;
        StartImeTimer(delayMs);
    }

    private void StartImeTimer(int delayMs)
    {
        _imeTimer?.Stop();
        _imeTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(30, delayMs)),
        };
        _imeTimer.Tick += (_, _) =>
        {
            _imeTimer?.Stop();
            if (!IsVisible) return;
            _imeApplied = ApplyImePreference(force: true);
            if (!_imeApplied && _imeRetry++ < 5)
                StartImeTimer(250);
            else if (_imeApplied)
                ScheduleImeVerify();
        };
        _imeTimer.Start();
    }

    /// <summary>
    /// TSF 有时在面板刚显示时先报 Alphanumeric，等焦点稳定/开始输入后又变回
    /// 中文（搜狗、微软拼音都见过）。这里在 700ms 后再核对一次：只读状态，
    /// 真的变回中文才补按一次 Shift；已经是目标状态就什么都不做。
    /// </summary>
    private void ScheduleImeVerify()
    {
        _imeVerify?.Stop();
        _imeVerify = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _imeVerify.Tick += (_, _) =>
        {
            _imeVerify?.Stop();
            if (IsVisible)
                _imeApplied = ApplyImePreference(force: true);
        };
        _imeVerify.Start();
    }

    /// <summary>
    /// 打开时抢前台：先试一次，没抢到就每 20ms 重试，最多约 400ms。
    ///
    /// 为什么要重试而不是"一次不成就算了"：
    /// 热键触发那一刻，系统还在处理上一个前台窗口的焦点切换，
    /// 第一次 SetForegroundWindow 常常被前台锁定拒掉，
    /// 隔几十毫秒再试就能成功 —— 这也解释了"有时候能打、有时候不能"。
    /// </summary>
    private void BeginForceFocus()
    {
        try { _hwnd = new WindowInteropHelper(this).Handle; }
        catch { _hwnd = IntPtr.Zero; }

        Topmost = true;
        FocusInputNow();

        if (_hwnd != IntPtr.Zero && WindowService.ForegroundIs(_hwnd)) return;

        _focusTries = 0;
        _focusTimer ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        _focusTimer.Tick -= OnFocusTick;
        _focusTimer.Tick += OnFocusTick;
        _focusTimer.Start();
    }

    private void OnFocusTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _closing) { StopFocusTimer(); return; }

        if (_hwnd == IntPtr.Zero)
        {
            try { _hwnd = new WindowInteropHelper(this).Handle; } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        }

        if (_hwnd != IntPtr.Zero && WindowService.ForegroundIs(_hwnd))
        {
            FocusInputNow();
            StopFocusTimer();
            return;
        }

        if (++_focusTries > 20)      // 约 400ms
        {
            StopFocusTimer();
            CoreLog.Warn("Qbar 抢前台失败（前台锁定或被权限更高的程序占着），已放弃");
            return;
        }

        /* 前几次只做"常规抢"（SetForegroundWindow + AttachThreadInput）；
         * 反复失败才动前台锁定超时 —— 那一步会广播 WM_SETTINGCHANGE，
         * 每次重试都做一遍会把界面拖慢。 */
        bool aggressive = _focusTries >= 6;
        if (_hwnd != IntPtr.Zero) WindowService.ForceForeground(_hwnd, aggressive);
        FocusInputNow();
    }

    private void StopFocusTimer()
    {
        try
        {
            _focusTimer?.Stop();
            if (_focusTimer != null) _focusTimer.Tick -= OnFocusTick;
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!ShouldHideOnDeactivate((int)(DateTime.Now - _openedAt).TotalMilliseconds)) return;
        HideQbar();
    }

    /// <summary>
    /// 收到 Deactivated 时该不该把面板收起来（纯函数，便于自检）。
    ///
    /// 打开后的头 <see cref="OpenGraceMs"/> 毫秒里一律不收：
    /// 那段时间正在跟系统抢前台，Deactivated 是抢的过程产生的，
    /// 收起来会让用户看到"面板闪一下就消失"。
    /// </summary>
    internal static bool ShouldHideOnDeactivate(int elapsedMs) => elapsedMs >= OpenGraceMs;

    // ---------------- 点击面板外面就收起 ----------------

    /// <summary>
    /// 打开期间盯着全局鼠标按键。
    ///
    /// 为什么除了 Deactivated 还要这个：任务栏这类"不换前台"的地方，
    /// 点了它前台窗口不变，Deactivated 根本不触发，面板会一直挂着。
    /// 用户要求"点任何地方都消失"，这个全局钩子就是兜底。
    ///
    /// 窗口**内部**（投影、透明边距）不归钩子管：那些点击会穿透到下
    /// 层窗口、由 Deactivated 收走，与加钩子之前的行为完全一致 ——
    /// 所以只做"矩形外就收"，不做花哨的命中测试。
    /// </summary>
    private void StartOutsideWatch()
    {
        // 先摘再挂：重复 Open() 不会叠出多个回调
        _host.Engine.MouseDownEvent -= OnGlobalMouseDown;
        _host.Engine.MouseDownEvent += OnGlobalMouseDown;
    }

    private void StopOutsideWatch() => _host.Engine.MouseDownEvent -= OnGlobalMouseDown;

    /// <summary>钩子线程回调：只做判断，收起动作丢回界面线程。</summary>
    private void OnGlobalMouseDown(int x, int y, int mouseMessage)
    {
        if (_hwnd == IntPtr.Zero) return;

        /* 先看这一击落在谁身上：本进程自己弹出的东西（输入框的右键
         * 菜单、提示气泡）虽然在面板矩形之外，也不算"外面" ——
         * 否则右键菜单里点「粘贴」会把面板一起收走。 */
        var hit = Core.Interop.NativeMethods.WindowFromPoint(new Core.Interop.NativeMethods.POINT { X = x, Y = y });
        if (hit != IntPtr.Zero)
        {
            Core.Interop.NativeMethods.GetWindowThreadProcessId(hit, out uint pid);
            if (pid == Core.Interop.NativeMethods.GetCurrentProcessId()) return;
        }

        if (!Core.Interop.NativeMethods.GetWindowRect(_hwnd, out var r)) return;
        if (!IsOutsidePanel(x, y, r.Left, r.Top, r.Right, r.Bottom)) return;
        // 与 Deactivated 共用同一套宽限期：刚打开时正在抢前台，别被误关
        if ((DateTime.Now - _openedAt).TotalMilliseconds < OpenGraceMs) return;
        Dispatcher.BeginInvoke(new Action(HideQbar));
    }

    /// <summary>
    /// 一次全局按键是否落在面板窗口之外（纯函数，便于自检）。
    /// 右/下边界取开区间，与 Windows 的命中测试一致：点在最右一列
    /// 或最下一行像素上，算窗外。
    /// </summary>
    internal static bool IsOutsidePanel(int x, int y, int left, int top, int right, int bottom)
        => x < left || x >= right || y < top || y >= bottom;

    private SolidColorBrush? _panelBrush;
    private string _styleStamp = "";

    // 面板主色 + 用户设的不透明度：TintFill 的画笔按这两项算，
    // 有模糊底图时还要再压一次（见 ApplyPanelTint）。
    private Color _panelColor;
    private double _panelOpacity;

    /// <summary>
    /// 判断面板底色是深是浅，并据此推出一整套前景色。
    ///
    /// 背景：XAML 里原先所有文字/分隔线/图标底色都是**硬编码的深色系**
    /// （#FFFFFF / #9FB4D8 / #1FFFFFFF…），一旦用户把面板底色改成浅色，
    /// 就变成"白字白底"，什么都看不见 —— 用户反馈的"只有深色文本框"就是这个。
    ///
    /// 现在改为按底色的**相对亮度**推导：
    ///   · 深底 → 白字 + 白色半透明分隔线（与原来一致）
    ///   · 浅底 → 深灰字 + 黑色半透明分隔线
    /// 这样无论用户把面板设成什么颜色，文字都可读。
    /// </summary>
    private static bool IsLightColor(Color c)
    {
        // sRGB 相对亮度（ITU-R BT.709 系数）
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        return lum > 0.55;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// sRGB 相对亮度（WCAG 定义：先线性化再按 0.2126/0.7152/0.0722 加权）。
    /// 和 <see cref="IsLightColor"/> 那个粗略估计不是一回事 —— 算对比度必须用这个。
    /// </summary>
    private static double RelativeLuminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG 对比度（1~21）。</summary>
    internal static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// 在给定底色上挑可读性更高的文字色：纯白 还是 近黑。
    ///
    /// 用在"首字色块"上 —— 那块底是各家的品牌色，深浅完全不由我们定
    /// （百度蓝、必应青、维基灰……）。跟着面板明暗走就会踩雷：
    /// 浅色面板给深灰字，压在深蓝牌子上等于消失。
    /// </summary>
    internal static Color BestTextOn(Color background)
    {
        var white = Colors.White;

        /* 浅色牌子上用**纯黑**而不是面板那个 #161A23：
         * 谷歌红（#EA4335）、中灰（#808080）这类"中间调"底色，
         * 用 #161A23 只有 4.4:1，差一点点够不着正文可读的 4.5。 */
        var dark = Colors.Black;

        return Contrast(white, background) >= Contrast(dark, background) ? white : dark;
    }

    /// <summary>把颜色按给定不透明度叠到底色上（用于算出胶囊的实际底色）。</summary>
    internal static Color Overlay(Color baseColor, Color veil, double alpha) => Mix(baseColor, veil, alpha);

    /// <summary>面板上"主文字"该用什么色。浅底黑、深底白，胶囊里的引擎名也用它。</summary>
    internal static Color PrimaryOn(Color panel)
        => IsLightColor(panel) ? Color.FromRgb(0x16, 0x1A, 0x23) : Colors.White;

    /// <summary>把整套配色应用到面板里的每个元素。</summary>
    private void ApplyForegroundPalette(Color panelColor)
    {
        bool light = IsLightColor(panelColor);

        // 前景色阶：主文字 → 次级 → 弱化
        Color primary = PrimaryOn(panelColor);
        Color secondary = light ? Color.FromRgb(0x5B, 0x64, 0x78) : Color.FromRgb(0xC9, 0xD6, 0xEC);
        Color muted = light ? Color.FromRgb(0x8A, 0x93, 0xA6) : Color.FromRgb(0x9F, 0xB0, 0xCC);
        Color faint = light ? Color.FromRgb(0xA8, 0xB0, 0xBF) : Color.FromRgb(0x7F, 0x8D, 0xA8);

        // 半透明叠色（图标底、标签底、分隔线）：浅底用黑，深底用白
        Color veil = light ? Colors.Black : Colors.White;

        var bPrimary = Frozen(primary);
        var bSecondary = Frozen(secondary);
        var bMuted = Frozen(muted);
        var bFaint = Frozen(faint);
        var bVeil06 = Frozen(Color.FromArgb(0x0F, veil.R, veil.G, veil.B));
        var bVeil10 = Frozen(Color.FromArgb(0x1A, veil.R, veil.G, veil.B));
        var bVeil13 = Frozen(Color.FromArgb(0x22, veil.R, veil.G, veil.B));
        var bVeil20 = Frozen(Color.FromArgb(0x33, veil.R, veil.G, veil.B));

        ModeIcon.Foreground = bSecondary;
        Input.Foreground = bPrimary;
        Input.CaretBrush = bPrimary;

        /* 胶囊里的字是"当前用的是哪个引擎"的唯一提示，用主文字色。
         * 以前给的是次级灰：深色下偏蓝灰、浅色下偏浅灰，压在胶囊那层
         * 13% 叠色上就发糊 —— 用户在白天模式里一眼看不出写的什么。 */
        ChipLabel.Foreground = bPrimary;
        ChipClose.Foreground = bMuted;
        ChipBox.Background = bVeil13;
        ChipBox.BorderBrush = bVeil20;

        ChipInitialBox.Background = bVeil13;

        /* 首字块的底色是**品牌色**（每家深浅都不同），不能跟着面板走。
         * 以前固定用 bPrimary：浅色面板下是深灰字压深蓝牌子（如百度蓝），
         * 几乎看不见；深色面板下白字反而正常。改成按牌子亮度自己挑黑白。 */
        ChipInitial.Foreground = Frozen(BestTextOn(_chip?.Accent ?? Colors.SteelBlue));
        HintText.Foreground = bMuted;
        SepLine.Background = bVeil10;

        // 「当前分类」角标——它的底色是强调色，文字始终用白
        if (ModeIcon.Parent is Border modeBorder) modeBorder.Background = bVeil13;

        /* 结果列表项在 DataTemplate 里，逐个取出来改色。
         * 用 ItemContainerGenerator 拿容器，ContainerFromItem 在虚拟化下
         * 可能为空，所以这里遍历可视树更可靠。
         *
         * 有 x:Name 的都是模板外的固定元素，它们各自有明确语义，
         * 不该被"按字号猜"这一轮刷掉（ChipLabel 最典型）。 */
        /* 模板里的文字走 DynamicResource，这里直接把**窗口级资源**换掉：
         * 标题/副标题/角标各用各的角色，不再靠"字号 >= 13.5 就是标题"猜 ——
         * 那样任何字号微调都会让颜色错位，而且用户改显示缩放时特别明显。 */
        Resources["Brush.OverlayText"] = bPrimary;
        Resources["Brush.OverlayTextSecondary"] = bSecondary;
        Resources["Brush.OverlayTextTertiary"] = bFaint;

        // 再按具体名字覆盖一次（上面那轮会把模板外的也刷成近似色）
        ModeIcon.Foreground = bSecondary;
        Input.Foreground = bPrimary;
        ChipLabel.Foreground = bPrimary;
        ChipClose.Foreground = bMuted;
        HintText.Foreground = bMuted;

        foreach (var bd in FindAll<Border>(this))
        {
            // 图标底、徽标底这类小圆角块
            if (bd.CornerRadius.TopLeft is > 4 and < 10 && bd.Width is > 0 and < 30)
                bd.Background = bVeil10;
        }

        // 列表里的图标瓷砖（模板内，宽高都是 30）
        foreach (var bd in FindAll<Border>(this))
        {
            if (Math.Abs(bd.Width - 30) < 1 && Math.Abs(bd.Height - 30) < 1)
                bd.Background = bVeil06;
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>解析颜色字符串；非法值退回深色主题的卡片底色。</summary>
    private static Color ParseColor(string s)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch { return (Color)ColorConverter.ConvertFromString("#1C1F27"); }
    }

    /// <summary>
    /// 当前主题面板应使用的底色。
    ///
    /// 从应用资源里取 SurfaceAlt（卡片底），取不到就按"深色主题"兜底。
    /// 这样 Qbar 与主窗口的整体观感一致，而不是永远一块深色板。
    /// </summary>
    private Color ThemePanelColor()
    {
        try
        {
            if (Application.Current?.TryFindResource("Brush.SurfaceAlt") is SolidColorBrush br)
                return br.Color;
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        return (Color)ColorConverter.ConvertFromString("#1C1F27");
    }

    /// <summary>主题标识，用于让样式缓存能在切换主题后失效。</summary>
    private static string ThemeStamp()
    {
        try
        {
            return Application.Current?.TryFindResource("Brush.SurfaceAlt") is SolidColorBrush br
                ? br.Color.ToString()
                : "?";
        }
        catch { return "?"; }
    }

    /// <summary>深度遍历可视树，收集指定类型的元素。</summary>
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in FindAll<T>(child)) yield return deeper;
        }
    }

    private void ApplyStyle()
    {
        var q = _host.Settings.Qbar;
        try
        {
            /* 面板底色跟随主题。
             *
             * 用户设置的 PanelColor 如果是"跟随主题"的保留值（空 / "auto"），
             * 就按当前主题的 SurfaceAlt 取色；否则用他指定的颜色。
             * 这样浅色主题下不会再出现"深色面板 + 白字看不见"。 */
            bool followTheme = string.IsNullOrWhiteSpace(q.PanelColor)
                               || q.PanelColor.Equals("auto", StringComparison.OrdinalIgnoreCase);
            Color effectivePanel = followTheme ? ThemePanelColor() : ParseColor(q.PanelColor);

            // 只有设置真的变了才重建画笔：Qbar 是高频开关的面板，
            // 每次 Open 都 new 一次 SolidColorBrush 属于无谓开销。
            string stamp = $"{q.AccentColor}|{effectivePanel}|{q.PanelOpacity}|{q.TextFontSize}|{q.LineHeight}|{q.ListCount}|{ThemeStamp()}";
            if (stamp == _styleStamp) return;
            _styleStamp = stamp;

            /* 描边不再用强调色。
             *
             * 之前这里拿 q.AccentColor 画 1.5px 的框，默认是 #2B6CF6 ——
             * 在深色面板上是"发光的蓝边"，在浅色面板上更扎眼，用户说"太突兀"。
             * 描边的作用只是把面板和桌面分开，用主题自带的中性细线就够：
             * 深色 #33FFFFFF / 浅色 #1F000000，一层很淡的白/黑，日夜都吃得住。 */
            Panel.BorderBrush = (Brush)FindResource("Brush.OverlayBorder");

            _panelColor = effectivePanel;
            _panelOpacity = double.TryParse(q.PanelOpacity, out double op) ? Math.Clamp(op, 0.3, 1) : 0.96;
            ApplyPanelTint();
            var panelColor = effectivePanel;

            // 整套前景色按底色亮度推导，保证任何底色下文字都可读
            ApplyForegroundPalette(panelColor);

            Input.FontSize = q.TextFontSize;
            // 字号变大时必须同步抬高输入框，否则上下缘会被裁掉。
            // 用真实字形高度来算（不是估系数），再留出安全余量。
            Input.MinHeight = Core.Text.TextMetrics.MinBoxHeight(q.TextFontSize, GlyphSample);
            Results.MaxHeight = q.LineHeight * q.ListCount + 12;
            SizeToContent = SizeToContent.Height;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("Qbar 样式应用失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把面板底色铺到 TintFill 上。
    ///
    /// 有模糊底图时必须把不透明度压低：用户设的是 0.96，那么厚的一层底色会把
    /// 模糊糊死，等于白抓一张图（真机实测：0.82 时面板在纯色壁纸上看起来就是一块
    /// 不透明的深色板，毛玻璃完全看不出来）。0.72 = 背后留 28% 的模糊透上来，
    /// 既有玻璃感，字又不会被认出来 —— 真正让人读不出背后文字的是模糊半径，不是这层纱。
    /// 没有底图时完全按用户设置来，否则"半透明面板"就变成了不透明面板。
    /// </summary>
    private void ApplyPanelTint()
    {
        if (_panelOpacity <= 0) return;
        bool blurred = BackdropFill.Background is ImageBrush;
        double op = blurred ? Math.Min(_panelOpacity, 0.72) : _panelOpacity;
        byte alpha = (byte)Math.Round(Math.Clamp(op, 0.3, 1) * 255);

        _panelBrush = new SolidColorBrush(Color.FromArgb(alpha, _panelColor.R, _panelColor.G, _panelColor.B));
        _panelBrush.Freeze();
        TintFill.Background = _panelBrush;
        Panel.Background = Brushes.Transparent;
    }

    /// <summary>
    /// 抓一张"面板背后的桌面"做毛玻璃底图。
    ///
    /// Qbar 是分层窗口（要有圆角和透明），拿不到 DWM 的亚克力，只能自己拍一张。
    /// 拍的时候必须把自己排除掉，否则拍到的"背后"就是面板自己 —— 先试
    /// WDA_EXCLUDEFROMCAPTURE（Win10 2004+ 支持分层窗口），系统不认这条就
    /// 把窗口藏 50ms 再拍（老系统上会闪一下，但只在打开/拖完的那一帧）。
    ///
    /// 只在面板真的显示在屏幕上时才有意义：--render 把窗口摆在 -32000，
    /// 自检可能根本没桌面，这些情况直接跳过。
    /// </summary>
    private void RefreshBackdrop()
    {
        try
        {
            if (_closing || !IsVisible) return;
            if (Left < -2000 || Top < -2000) return;                 // 离屏渲染
            if (ActualWidth < 40 || ActualHeight < 40) return;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            Point tl = PointToScreen(new Point(0, 0));
            Point br = PointToScreen(new Point(ActualWidth, ActualHeight));

            System.Windows.Media.Imaging.BitmapSource? shot;
            if (BackdropBlur.TryExcludeFromCapture(hwnd))
            {
                try
                {
                    shot = BackdropBlur.Capture(tl, br);
                }
                finally
                {
                    // 只在这几毫秒里把自己藏起来，拍完立刻恢复，否则面板在录屏里是空的
                    BackdropBlur.RestoreCapture(hwnd);
                }
            }
            else
            {
                double saved = Opacity;
                Opacity = 0;
                try
                {
                    // 让合成器把"透明的一帧"推上去再拍，否则拍到的仍是我们自己
                    System.Threading.Thread.Sleep(50);
                    shot = BackdropBlur.Capture(tl, br);
                }
                finally
                {
                    Opacity = saved;
                }
            }

            if (shot == null) return;

            var brush = new ImageBrush(shot)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            brush.Freeze();
            BackdropFill.Background = brush;
            ApplyPanelTint();
        }
        catch (Exception ex)
        {
            CoreLog.Warn("Qbar 背景模糊失败：" + ex.Message);
        }
    }

    /// <summary>设置页改了外观后调用，强制下次打开重建画笔。</summary>
    public void InvalidateStyle() => _styleStamp = "";

    /// <summary>
    /// 面板显示期间的 Esc 拦截：面板经常抢不到键盘焦点（前台锁定），
    /// 这时 Esc 会打进原来那个程序，面板纹丝不动 —— 用户看到的就是"Esc 关不掉"。
    /// 挂在引擎上就与焦点无关了。
    /// </summary>
    private Func<int, bool>? _escInterceptor;

    private void AttachEscInterceptor()
    {
        if (_host == null) return;

        _escInterceptor ??= vk =>
        {
            if (vk != 0x1B) return false;
            Dispatcher.BeginInvoke(new Action(() => HideQbar()));
            return true;
        };

        _host.Engine.KeyInterceptor = _escInterceptor;
    }

    private void DetachEscInterceptor()
    {
        if (_host == null || _escInterceptor == null) return;
        if (ReferenceEquals(_host.Engine.KeyInterceptor, _escInterceptor))
            _host.Engine.KeyInterceptor = null;
    }

    /* ---- 拖动面板改位置 ----
     * 用户诉求：面板能手拖，拖完下次唤出就在那儿，设置里还能一键回默认位置。
     * 位置存成**工作区比例**（QbarOptions.PositionX/PositionY），不是像素：
     * 换分辨率、改缩放、插拔副屏之后像素会跑到屏幕外，比例还认得。 */

    /// <summary>
    /// 面板上按下左键：**空白处**才开始拖，按在控件上照常交给控件。
    ///
    /// 用排除法而不是"只认某块拖拽区"：面板大部分面积是输入框和结果列表，
    /// 只认几块的话用户几乎找不到能按的地方；反过来无条件 DragMove 又会变成
    /// "想点结果却把面板拖走了"。
    /// </summary>
    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _closing) return;
        if (IsInteractiveDragSource(e.OriginalSource as DependencyObject)) return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Core.Interop.NativeMethods.GetCursorPos(out _dragCursor)) return;
        if (!Core.Interop.NativeMethods.GetWindowRect(hwnd, out _dragWindowRect)) return;

        _dragging = true;
        _dragMoved = false;
        Panel.Cursor = GrabClosedCursor;   // 按住的那一下立刻变握拳
        e.Handled = true;                  // 别让这次按下再被当成"点空白处"

        _dragPoll ??= CreateDragPoll();
        _dragPoll.Start();
    }

    /* 为什么自己轮询，而不是 DragMove() / MouseMove 事件：
     *
     *   · Window.DragMove() 把拖动交给系统模态移动循环，那个循环只认硬件送来的
     *     移动消息；指针一旦被重定向/注入，第一下之后窗口就卡在原地。
     *   · 靠 WPF 的 MouseMove 也不行：面板捕获鼠标期间，任何一次按键状态同步
     *     都会补发一个 MouseLeftButtonUp，拖动当场结束（实测 158ms 后就被打断）。
     *
     * 每 15ms 直接读物理光标位置和左键状态，窗口跟着走；松手、按 Esc
     * 都在这里判定，不依赖事件有没有送达。 */
    private DispatcherTimer CreateDragPoll()
    {
        // Input 优先级：界面在渲染时也别让拖动掉帧
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(15),
        };
        timer.Tick += (_, _) => PollDrag();
        return timer;
    }

    private void PollDrag()
    {
        if (!_dragging) { _dragPoll?.Stop(); return; }

        if (_closing || !IsVisible) { EndPanelDrag(cancel: true); return; }
        if (IsDown(VK_ESCAPE)) { EndPanelDrag(cancel: true); return; }
        if (!IsDown(VK_LBUTTON)) { EndPanelDrag(cancel: false); return; }
        if (!Core.Interop.NativeMethods.GetCursorPos(out var cur)) return;

        // 用光标位移推窗口位置，而不是用相对窗口的坐标：
        // 窗口一动，同一物理点在窗口坐标系里的值也变了，只有屏幕坐标差是不变量。
        int dx = cur.X - _dragCursor.X;
        int dy = cur.Y - _dragCursor.Y;
        if (!_dragMoved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;   // 手抖不算拖

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        _dragMoved = true;
        Core.Interop.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            _dragWindowRect.Left + dx, _dragWindowRect.Top + dy, 0, 0,
            Core.Interop.NativeMethods.SWP_NOSIZE | Core.Interop.NativeMethods.SWP_NOZORDER
            | Core.Interop.NativeMethods.SWP_NOACTIVATE);
    }

    /* ---- 拖动指针 ----
     * Windows 只带"指向用"的手形（Cursors.Hand，食指指着），没有抓取手势那对
     * 张开手 / 握拳。所以自带两张 48px 的 .cur，打包成 Resource 现读。
     * 读不出来就退回系统指针，功能不受影响。 */
    private static Cursor? _grabOpenCursor;
    private static Cursor? _grabClosedCursor;

    private static Cursor GrabOpenCursor =>
        _grabOpenCursor ??= LoadCursorAsset("Assets/cursors/grab-open.cur", Cursors.Hand);

    private static Cursor GrabClosedCursor =>
        _grabClosedCursor ??= LoadCursorAsset("Assets/cursors/grab-closed.cur", Cursors.SizeAll);

    private static Cursor LoadCursorAsset(string relative, Cursor fallback)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/" + relative));
            if (info != null) return new Cursor(info.Stream);
            CoreLog.Warn("鼠标指针没打进程序：" + relative);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("读鼠标指针失败（" + relative + "）：" + ex.Message);
        }
        return fallback;
    }

    /// <summary>
    /// 收尾：正常松手就记住位置；Esc / 面板被收走不算数，退回按下时的位置。
    /// </summary>
    private void EndPanelDrag(bool cancel)
    {
        if (!_dragging && !_dragMoved) return;
        _dragging = false;
        _dragPoll?.Stop();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (cancel)
        {
            if (_dragMoved && hwnd != IntPtr.Zero)
            {
                Core.Interop.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    _dragWindowRect.Left, _dragWindowRect.Top, 0, 0,
                    Core.Interop.NativeMethods.SWP_NOSIZE | Core.Interop.NativeMethods.SWP_NOZORDER
                    | Core.Interop.NativeMethods.SWP_NOACTIVATE);
            }
        }
        else if (_dragMoved)
        {
            RememberPosition();

            /* 手可以拖出屏幕：比例已经夹回 0..1，这里再把窗口摆回夹取后的位置，
             * 否则"松手在屏幕外"会留下一半看不见的面板。 */
            QbarOptions q = _host.Settings.Qbar;
            Core.Windows.ScreenPlacement.PlaceAt(this, q.PositionX, q.PositionY);
        }

        _dragMoved = false;
        Panel.Cursor = GrabOpenCursor;   // 松手还指着面板：回到"能抓"

        if (!cancel && IsVisible)
        {
            Input.Focus();           // 拖完继续打字：别把焦点留在面板上
            Keyboard.Focus(Input);

            /* 拖到别处之后，背后已经是另一片桌面了，不重拍就会把原来那块
             * 桌面的模糊图拖着一路走 —— 看着像面板下面贴着一张照片。 */
            RefreshBackdrop();
        }
    }

    /// <summary>空白处的光标形状：能抓 / 抓着 / 普通箭头（指针在控件上时）。</summary>
    private void OnPanelMouseMove(object sender, MouseEventArgs e)
    {
        if (_closing || _dragging) return;

        /* Cursor 会往子元素继承，所以指针移到输入框/结果行/按钮上时必须清掉，
         * 否则文本框里也显示手形 —— 看着像能拖，实际是选字。 */
        bool onBlank = !IsInteractiveDragSource(e.OriginalSource as DependencyObject);
        Panel.Cursor = onBlank ? GrabOpenCursor : null;
    }

    /// <summary>这个落点会不会自己处理鼠标事件（输入框选字、按钮、列表行、滚动条）。</summary>
    private static bool IsInteractiveDragSource(DependencyObject? source)
    {
        for (DependencyObject? d = source; d != null; d = ParentOf(d))
        {
            if (d is System.Windows.Controls.Primitives.TextBoxBase) return true;
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
            if (d is System.Windows.Controls.Primitives.Thumb) return true;
            if (d is ListBoxItem) return true;
            if (d is ListBox) return true;
            if (d is ScrollViewer) return true;
        }
        return false;
    }

    /// <summary>
    /// 安全地往上找父级。
    ///
    /// 不能直接用 VisualTreeHelper.GetParent：鼠标移过标题里的 Run（Inline 文本）时
    /// 传进来的 OriginalSource 是 ContentElement，不是 Visual，WPF 会抛
    /// InvalidOperationException（"Run 不是 Visual 或 Visual3D"），
    /// 而这是鼠标移动事件 —— 抛一次就足以让界面线程崩掉、整个程序重启。
    /// ContentElement 用 FrameworkContentElement.Parent 走逻辑树。
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject d)
    {
        try
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(d);
        }
        catch (InvalidOperationException)
        {
            // 极少数内容元素混在可视树里，退回逻辑树
        }

        if (d is FrameworkContentElement fce) return fce.Parent;
        if (d is FrameworkElement fe) return fe.Parent;
        return null;
    }

    /// <summary>把面板当前位置量成比例写回设置（落盘，下次打开直接用它）。</summary>
    private void RememberPosition()
    {
        if (_host == null) return;
        if (!Core.Windows.ScreenPlacement.TryGetFraction(this, out double fx, out double fy)) return;

        QbarOptions q = _host.Settings.Qbar;
        q.CustomPosition = true;
        q.PositionX = Math.Round(fx, 4);
        q.PositionY = Math.Round(fy, 4);
        _host.SaveSettings();
    }

    /// <summary>设置页点「恢复默认位置」之后调用：清掉自定义位置，下次唤出回到默认摆位。</summary>
    public void ResetPosition()
    {
        if (_host == null) return;
        QbarOptions q = _host.Settings.Qbar;
        q.CustomPosition = false;
        q.PositionX = 0.5;
        q.PositionY = 0.3;
        _host.SaveSettings();
    }

    public void Open(string? prefill = null)
    {
        if (_closing) ReopenFromFade();
        _imeApplied = false;
        _imeKey = "";
        _imeAt = DateTime.MinValue;
        ApplyStyle();
        ClearChip();

        /* 过滤器 chip 每次打开都要清掉。
         *
         * 用户报的 bug：输入「WD 空格」进了文档搜索，关掉面板再打开，里面还是文档搜索。
         * 原因是 chip 存在 _appliedFilter 字段里，而 Open() 只清了 _chip（引擎标签），
         * 输入框被置空之后 EffectiveInput("") 又拼出 "WD:" —— 看着就像"记住了上次的搜索"。
         * 面板是临时输入口，不该有上次查询的记忆。 */
        ResetFilterChip();

        /* 顺手把内置 Everything 拉起来（后台线程，不挡面板显示）。
         * 它要几百毫秒才响应，而用户第一次搜文件通常就在这之后 ——
         * 等真搜了才启动的话，第一屏必然是空的。 */
        EverythingBootstrap.WarmupAsync();

        /* 每次打开都重取窗口列表：搜索用的缓存只有几百毫秒，
         * 但用户可能刚切过窗口，这里显式作废一次，第一眼结果更贴近现场。 */
        Core.Windows.WindowService.InvalidateWindowSearchCache();

        if (!IsVisible)
        {
            /* 顺序很重要：**先建句柄 → 再定位 → 最后 Show**。
             *
             * 定位要用到 HWND（才知道该落在哪块屏、那块屏的 DPI 是多少），
             * 但若先 Show 再挪，第一次打开会看到面板从默认位置"跳"一下。
             * EnsureHandle 只创建窗口句柄、不显示，正好满足"先有句柄又不闪"。 */
            try { _ = new WindowInteropHelper(this).EnsureHandle(); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

            var fg = Core.Interop.NativeMethods.GetForegroundWindow();

            /* 拖动过的面板落回用户自己放的位置（比例存在设置里，换分辨率也认得）；
             * 没拖过就走默认的"前台窗口所在屏、上方 22% 居中"。 */
            QbarOptions pos = _host.Settings.Qbar;
            bool placed = pos.CustomPosition
                ? Core.Windows.ScreenPlacement.PlaceAt(this, pos.PositionX, pos.PositionY, target: fg)
                : Core.Windows.ScreenPlacement.Place(this, Core.Windows.ScreenPlacement.Anchor.UpperCenter,
                    target: fg);
            if (!placed)
            {
                // 拿不到显示器信息时退回旧算法（至少保证在主屏上可见）
                var wa = SystemParameters.WorkArea;
                Left = wa.Left + (wa.Width - Width) / 2;
                Top = wa.Top + wa.Height * 0.22;
            }

            Show();

            /* 排完版再按同一个比例放一次。
             *
             * 面板是 SizeToContent 的：Show 之前 ActualHeight 还是 0，上面那次摆位只能拿
             * XAML 里的 Width/Height 当尺寸 —— 实测 760x791.5 DIP，而真实高度是 397.5 DIP。
             * 比例本身跟高度无关，但"别把面板顶出屏幕底边"的夹取要用真实高度：
             * 高度用大了会把面板往上顶（实测贴底时偏 9 DIP）。
             * 这里 UpdateLayout 之后高度才是真的；没碰到下边界时算出来的坐标跟上面
             * 一模一样（不会有可见位移），只有贴着底边时才纠正。 */
            if (placed && pos.CustomPosition)
            {
                try { UpdateLayout(); } catch { }   /* 预期内：布局型，失败即视为尺寸没拿到 */
                Core.Windows.ScreenPlacement.PlaceAt(this, pos.PositionX, pos.PositionY, target: fg);
            }
        }

        /* 抢前台 + 抢输入焦点。
         *
         * 原实现是 Show() 之后直接 Activate()/Focus() —— 那两步在
         * Loc.T("我们不是前台进程")时会被系统拒掉（前台锁定），
         * 面板显示在最上面，但打字仍然打在原来的程序里。
         * 这就是用户说的"换出 Qbar 之后打的字不落在面板里"。 */
        _openedAt = DateTime.Now;
        AttachEscInterceptor();
        StartOutsideWatch();

        /* prefill 有两种形态：
         *   ① "bd "           —— 引擎关键字（由 qbar:<key> 动作传入）
         *   ② Loc.T("要搜索的内容")   —— 普通预填文本
         *
         * 形态 ① 必须**立刻变成标签**，否则它只是输入框里的一段文字：
         * 用户按 Caps+G 打开却看到输入框写着 "go "，还得自己再敲个空格
         * 才变标签 —— 等于"绑定没生效"。这里直接判定并转换。 */
        string pre = prefill ?? "";
        SetInputText(pre);
        bool chipped = pre.Length > 0 && TryMakeChip();
        if (chipped && _chip != null)
        {
            // 真的变成了引擎标签：清空输入，让用户直接打关键词。
            // 记下“面板是带着引擎标签打开的”，输入法必须回到中文，
            // 不能依赖用户另外勾了“选中引擎后切回中文”。
            SetInputText("");
            RunQuery();
        }
        else
        {
            /* 普通预填，或者网址预填：网址不再变标签，整条 URL 就留在输入框里，
             * 这里不能清 —— 清掉等于 qbar:bili 打开是个空面板。 */
            RunQuery();
        }

        /* 先把预填内容变成引擎标签，再抢焦点。
         * 反过来的话，FocusInputNow 可能在 _chip 还没建立时先按“空面板”
         * 应用一次输入法，并把 _imeApplied 置 true；等标签建立后
         * 就没有第二次机会了 —— 这正是“qbar:google 打开仍是英文”的竞态。 */
        BeginForceFocus();

        Input.Focus();
        Keyboard.Focus(Input);
        Input.CaretIndex = Input.Text.Length;

        /* 输入法不在打开瞬间盲按 Shift：此时面板还没拿到前台焦点，
         * Shift 会打给上一个窗口；而且“按一下”是切换而不是确保，
         * 当前已经是中文时反而被切成英文。统一交给 ImeGuard 按当前标签状态
         * 决定“中文还是英文”。 */
        _imeApplied = false;
        _imeKey = "";
        if (IsActive)
            _imeApplied = ApplyImePreference();
        QueueImePreference(120);

        // 摆位、布局、结果都定了，这时候拍的"背后"才对得上最终位置
        RefreshBackdrop();

        PlayShowAnimation();
    }

    /// <summary>
    /// 离屏渲染用：打开并把某个关键字直接变成标签（相当于用户输入 "bd " 之后的状态）。
    /// </summary>
    public void OpenForRender(string keyword, string? tail = null)
    {
        Open("");
        SetInputText(keyword);
        if (!TryMakeChip()) RunQuery();
        if (!string.IsNullOrEmpty(tail)) SetInputText(tail);
        RunQuery();
    }

    /// <summary>
    /// 仅供 --render：整句直接当查询词打进去，**不做 chip**。
    /// 用来验证"不点空格、直接打 ol report 也能查词"这类不带标签的路径 ——
    /// OpenForRender 会先 TryMakeChip，把 ol 变成标签，那条路就验不到。
    /// </summary>
    /// <summary>
    /// 仅供 --render：把过滤器挂成 chip（"doc + 空格"那条路的终点，不是点结果行）。
    /// 用来出图检查 chip 与筛完的结果排在一起会不会挤坏。
    /// </summary>
    public void OpenForRenderFilter(string keyword, string? tail = null)
    {
        Open("");
        SetInputText(keyword);
        if (!TryMakeFilterChip()) RunQuery();
        if (!string.IsNullOrEmpty(tail)) SetInputText(tail);
        RunQuery();
    }

    /// <summary>
    /// 动作触发：打开面板并**直接挂上某个过滤器**（"wd 空格"那条路的终点）。
    ///
    /// 给「Qbar 搜索」分类里的 qfilter:&lt;Id&gt; 动作用：绑一个键，按下去就是
    /// "打开面板 + 只看文档"，不用先打关键字再按空格。
    /// </summary>
    /// <param name="filterId">QbarFilter.Id</param>
    /// <param name="query">可选：顺手带进去的搜索词（屏幕取词那条路用）</param>
    public void OpenFileSearch(string? filterId, string? query = null)
    {
        Open("");

        if (!string.IsNullOrEmpty(filterId))
        {
            QbarFilter? filter = QbarFilterCatalog.Create(_host.Settings.Qbar.Filters).All
                .FirstOrDefault(f => string.Equals(f.Id, filterId, StringComparison.Ordinal));

            if (filter != null)
            {
                /* 先把这个过滤器的触发词写进输入框，再走 ApplyFilter：
                 * ApplyFilter 是靠 Input.Text 判断"要不要把触发词从查询里切掉"的，
                 * 空着直接调它，它会把用户上次留在框里的内容整段当成搜索词。 */
                SetInputText(filter.Keyword + ":");
                ApplyFilter(filter.Id);
            }
        }

        if (!string.IsNullOrEmpty(query))
        {
            SetInputText(query);
            RunQuery();
        }
    }

    /// <summary>
    /// 动作触发：打开面板并直接进入「查词」状态（查词关键字变成 chip，词已经填好）。
    ///
    /// 给「Qbar 搜索」分类的 qdict.lookup 用：按一个键等价于
    /// "打开 Qbar → 打 ol → 空格 → 打词"，一个键都不用敲。
    /// 关键字取命令表里那条查词命令自己的（用户把 ol 改成「词典」也跟着走）。
    /// </summary>
    public void OpenDictionary(string? word = null)
    {
        Open("");

        QbarCommand? dict = DictionaryLauncher.EffectiveCommand(_host.Settings.Qbar.Commands);
        string keyword = string.IsNullOrWhiteSpace(dict?.Keyword)
            ? QbarCommandStore.DictKeyword
            : dict!.Keyword.Trim();

        SetInputText(keyword);
        if (!TryMakeChip()) RunQuery();     // 关键字 → 命令 chip（后面打的才是词）

        if (!string.IsNullOrEmpty(word))
        {
            SetInputText(word);
            RunQuery();                     // 走 TryStartDictionary，释义会铺成结果行
        }
    }

    public void OpenForRenderQuery(string text)
    {
        Open("");
        SetInputText(text);
        RunQuery();
    }

    /// <summary>
    /// 仅供 --render：Open 会把面板摆到屏幕**上方居中**（那是它给用户看的正常位置），
    /// 出图时用户并不知道会弹东西，看到的就是"突然冒出个面板"。
    ///
    /// 只改 WPF 的 Left/Top 不够：这个窗口是 Topmost + ShowActivated，
    /// 打开流程里位置还会被重算一次。直接下 Win32 命令挪到虚拟屏外面，
    /// 绕过属性重算；尺寸不变，RenderTargetBitmap 照样截得到内容。
    /// </summary>
    public void ParkOffScreenForRender()
    {
        Left = -32000;
        Top = -32000;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Core.Interop.NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, -20000, -20000, 0, 0,
            Core.Interop.NativeMethods.SWP_NOSIZE
            | Core.Interop.NativeMethods.SWP_NOZORDER
            | Core.Interop.NativeMethods.SWP_NOACTIVATE);
    }

    private void PlayShowAnimation()
    {
        Panel.BeginAnimation(OpacityProperty, null);
        Panel.Opacity = 1;
        var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Slide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>
    /// 收起淡出进行中又被打开：当场作废这次收起。
    ///
    /// 没有这一步的话，旧淡出动画跑完时的 Completed 会把**刚打开**的
    /// 面板 Hide 掉 —— 现象是"点一下外面、紧接着再按热键，面板不出来"。
    /// </summary>
    private void ReopenFromFade()
    {
        Panel.BeginAnimation(OpacityProperty, null);
        Panel.Opacity = 1;
        _closing = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        /* Alt+F4 / 系统关闭不会走 HideQbar；这里必须把全局 Esc 拦截和鼠标监听
         * 一起摘掉，否则 Qbar 消失后整个系统的 Esc 会被永久吞掉。 */
        _closing = true;
        DetachEscInterceptor();
        StopFocusTimer();
        StopOutsideWatch();
        base.OnClosed(e);
    }

    public void HideQbar()
    {
        if (!IsVisible || _closing) return;
        _imeTimer?.Stop();
        _imeVerify?.Stop();
        DetachEscInterceptor();
        StopFocusTimer();
        StopOutsideWatch();
        _closing = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
        fade.Completed += (_, _) =>
        {
            // 收起途中又被 Open() 打开（ReopenFromFade 把 _closing 清了）：
            // 这次收起作废，不能把新打开的面板 Hide 掉。
            if (!_closing) return;
            Hide();
            Panel.BeginAnimation(OpacityProperty, null);
            Panel.Opacity = 1;
            _closing = false;
        };
        Panel.BeginAnimation(OpacityProperty, fade);
    }

    // ---------------- 标签（Chip） ----------------

    /// <summary>
    /// 输入框整段等于一条快捷命令时直接执行（空格触发）。
    /// 返回 false 表示不是快捷命令，交给普通“变标签 / 搜索”流程。
    /// </summary>
    private bool TryRunQuickCommand()
    {
        var q = QbarService.FindQuickCommand(Input.Text);
        if (q == null) return false;
        ExecuteResult(QbarService.ToResult(q));
        return true;
    }

    /// <summary>
    /// 把 "bd" 变成标签。触发时机：输入空格 / Tab 时，
    /// 且当前整段文本恰好是一个已定义的关键字。
    /// </summary>
    private bool TryMakeChip()
    {
        if (_chip != null) return false;
        string text = Input.Text.Trim();
        if (text.Length == 0) return false;
        var chip = _host.Qbar.ResolveChip(text);
        if (chip == null) return false;

        /* 网址关键字（bili / gh / bd…）不再变成标签，直接把完整地址写进输入框。
         *
         * 标签的毛病是：地址被藏进那个小胶囊里，输入框被清空且变成只读 ——
         * 用户看不到自己要去哪，也接不上"打开后继续输路径"。
         * 把 URL 摊开在输入框里，回车就走普通的"打开这个网址"那条路，
         * 行为和手打网址完全一致（还看得见、能改）。 */
        if (chip.Kind == ChipKind.Web && !string.IsNullOrWhiteSpace(chip.Value))
        {
            ClearChip();
            SetInputText(chip.Value);
            Input.Focus();
            Input.CaretIndex = Input.Text.Length;
            RunQuery();
            return true;
        }

        SetChip(chip);
        SetInputText("");
        // 关键字已经变成引擎标签，立刻按“空关键词”刷新一次：
        // 网址类（如 bili）会直接显示首页，搜索类会显示待输入的提示，
        // 不会把上一个关键字留下的结果继续挂在下面。
        RunQuery();
        return true;
    }

    private void SetChip(QbarChip chip)
    {
        _chip = chip;
        /* v1.4.3：胶囊上也**不出现关键字**（用户要求"关键字在任何情况下都不显示"）。
         * 有备注用备注；没备注就只留图标 —— 和结果行同一套规则。
         * 唯一的退路是"既没备注又没有图标"的老条目：那时才退回关键字，
         * 否则输入框里会挂着一个什么都认不出的空胶囊。 */
        string chipText = chip.Note.Length > 0
            ? chip.Note
            : (chip.HasIcon ? "" : chip.Label);
        ChipLabel.Text = chipText;
        ChipLabel.Visibility = chipText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        /* 网址标签没有“后面的搜索词”这回事：bili + 空格 之后
         * 输入框里再打字也不会参与网址拼接，只会让人误以为还能搜。
         * 这里直接锁住输入框，只保留 Enter（打开）/ Backspace（退回关键字）
         * 这类窗口级按键；搜索/程序标签仍然可以继续输入。 */
        Input.IsReadOnly = chip.Kind == ChipKind.Web;
        Input.IsReadOnlyCaretVisible = chip.Kind != ChipKind.Web;

        if (chip.HasIcon)
        {
            ChipIcon.Source = chip.Icon;
            ChipIconBox.Visibility = Visibility.Visible;
            ChipInitialBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            ChipIcon.Source = null;
            ChipIconBox.Visibility = Visibility.Collapsed;
            ChipInitialBox.Visibility = Visibility.Visible;
            ChipInitial.Text = chip.Initial;
            ChipInitialBox.Background = new SolidColorBrush(chip.Accent);

            /* 字色按品牌色自身亮度选：深牌子用白字、浅牌子用深字。
             * 这里必须跟着底色一起设 —— 否则换一个引擎后，
             * 牌子换了、字色还是上一家的，又会出现"看不见"。 */
            ChipInitial.Foreground = Frozen(BestTextOn(chip.Accent));

            /* 标签本身也要真实图标：启动程序从 exe/lnk 取，
             * 网站从 favicon 取；拿不到才保留首字。 */
            if (chip.Kind is ChipKind.Run or ChipKind.Command)
            {
                var runIcon = IconCache.FromExecutable(chip.Value);
                if (runIcon != null) SetChipIcon(runIcon);
            }
            else if (chip.Kind == ChipKind.Web)
            {
                IconCache.LoadWebIconAsync(chip.Value, image =>
                {
                    if (image == null) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(_chip, chip)) SetChipIcon(image);
                    });
                });
            }
        }

        ChipBox.Visibility = Visibility.Visible;
        ModeIcon.Text = chip.Kind switch
        {
            ChipKind.Search => "\uE721",
            ChipKind.Web => "\uE774",
            _ => "\uE756",
        };
        UpdateMode();

        /* 引擎认出来了，接下来要打的是搜索内容 —— 多半是中文。
         * 只在面板已经拿到焦点时切；否则留给 FocusInputNow 的首次应用，
         * 免得把键发到上一个窗口（这就是“带引擎打开却还是英文”的根因）。 */
        if (IsActive)
            _imeApplied = ApplyImePreference();
        QueueImePreference(100);
    }

    private void SetChipIcon(ImageSource image)
    {
        ChipIcon.Source = image;
        ChipIconBox.Visibility = Visibility.Visible;
        ChipInitialBox.Visibility = Visibility.Collapsed;
    }

    private void ClearChip()
    {
        // 标签没了就不再是"某个搜索引擎"的上下文，联想必须立刻消失；
        // 否则标签已清空、下面还挂着百度词，看起来像数据串台。
        HideSuggestions();
        _chip = null;
        Input.IsReadOnly = false;
        Input.IsReadOnlyCaretVisible = true;
        ChipBox.Visibility = Visibility.Collapsed;
        ChipIcon.Source = null;
        ModeIcon.Text = "\uE721";
    }

    /// <summary>
    /// 叉掉引擎标签：清空输入框，回到自由搜索。
    ///
    /// 以前会把关键字塞回输入框（"方便直接改"），但那是个死胡同 ——
    /// 输入框里留着 "bd"，用户接着打空格 + 关键词就变成 "bd chrome"，
    /// QueryCore 的「第一段是引擎关键字」分支立刻短路，只剩一条网页搜索，
    /// 文件 / 应用 / 动作全被吃掉。用户实测：叉掉标签之后搜什么都只出网页搜索。
    /// 叉掉标签的语义是"不用这个引擎了"，所以清空输入、立刻重搜。
    /// </summary>
    private void RemoveChip()
    {
        if (_chip == null) return;
        ClearChip();
        SetInputText("");
        RunQuery();
        Input.Focus();
        Input.CaretIndex = 0;
        if (IsActive)
        {
            _imeApplied = ApplyImePreference();
            QueueImePreference(100);
        }
    }

    private void OnChipClose(object sender, RoutedEventArgs e) => RemoveChip();

    /// <summary>
    /// 展开：把当前输入原样交给独立的大窗口，面板自己先收起来。
    /// 不把面板"变大"是因为这里是 AllowsTransparency + SizeToContent 的自绘窗，
    /// 塞进分面栏和四列表格会把这条路径上所有尺寸假设都推翻；开一个正常窗口最省事，
    /// 也顺带给了用户"可以拉大、可以一直留着"的期望行为。
    /// </summary>
    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new QbarExpandWindow(_host, Input.Text);
            HideQbar();
            win.Show();
            win.Activate();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "展开文件搜索窗口");
        }
    }

    private void SetInputText(string text)
    {
        _suppressTextChanged = true;
        Input.Text = text;
        Input.CaretIndex = text.Length;
        _suppressTextChanged = false;
    }

    // ---------------- 输入与检索 ----------------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        /* 文件操作菜单开着时，Esc 只退菜单、不关面板 —— 底部提示写的就是这个语义。
         * 焦点在结果列表上时 TextBox 那条 OnInputKeyDown 收不到键，所以窗口级也得拦一道。 */
        if (e.Key == Key.Escape)
        {
            /* 四层 Esc：过滤器 chip（改的是查询条件，最先该被撤销）→ 文件菜单 →
             * 联想 → 关面板。联想可见时直接关面板会让用户丢失刚打的词，所以多给一层。 */
            if (_appliedFilter != null) ClearFilter();
            else if (_menuFile != null) CloseFileMenu();
            else if (_suggestVisible) HideSuggestions();
            else HideQbar();
            e.Handled = true;
            return;
        }

        /* 联想可见时，↑↓/Enter 在窗口级就截住：焦点可能在结果列表上，
         * 只靠 TextBox 的 PreviewKeyDown 会漏键。文件菜单优先，不抢它的键。
         * 没有选中联想时 Enter 不拦，继续走“搜输入框原文”的既有逻辑。 */
        if (_menuFile == null && _suggestVisible)
        {
            if (e.Key is Key.Down or Key.Up)
            {
                MoveSuggestion(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && SuggestList.SelectedItem is string picked)
            {
                AcceptSuggestion(picked);
                e.Handled = true;
                return;
            }
        }

        /* 网址标签会把输入框设为只读；某些 WPF/输入法组合下只读 TextBox
         * 收不到 PreviewKeyDown，退格就“卡死”在标签状态。窗口级再兜一层：
         * 输入框为空时退格 → 拆掉标签、把关键字放回输入框。 */
        if (e.Key == Key.Back && _appliedFilter != null && Input.Text.Length == 0)
        {
            ClearFilter();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && _chip != null && Input.Text.Length == 0)
        {
            RemoveChip();
            e.Handled = true;
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            PinSelected();
            e.Handled = true;
            return;
        }

        /* Ctrl+D：就地开关查询调试面板。排障时不该为了看中间结果跑去设置界面翻。 */
        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        {
            QbarSearchOptions options = _host.Settings.Qbar.Search ?? new QbarSearchOptions();
            options.DebugPanel = !options.DebugPanel;
            _host.Settings.Qbar.Search = options;
            _host.SaveSettings();
            UpdateDebugPanel(_lastOutcome);
            e.Handled = true;
            return;
        }

        /* 文件菜单里 Esc / ← / Backspace 都是"收起菜单"，不是关面板、也不删字：
         * 这三下在菜单外的语义分别是关面板、移光标、删字符，用户按错会丢上下文。 */
        if (_menuFile != null && e.Key is Key.Escape or Key.Left or Key.Back)
        {
            CloseFileMenu();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                // 窗口级已经拦过一道；这里再兜一次，防止焦点/事件路由变化漏掉
                if (_suggestVisible)
                {
                    HideSuggestions();
                    e.Handled = true;
                    break;
                }
                HideQbar();
                e.Handled = true;
                break;
            case Key.Space:
                /* 快捷命令优先：cmd / psh / hosts 这类词打完后按空格就直接执行，
                 * 不再要求“先变成标签、再按一次回车”。 */
                if (TryRunQuickCommand()) { e.Handled = true; break; }
                // 空格：如果整段正好是关键字，就转成标签（之后空格用于分词）
                if (TryMakeChip()) { e.Handled = true; break; }
                // 过滤器关键字（doc / pic / 自定义）同样"打完按空格"就生效，
                // 不用再记冒号。顺序在引擎关键字之后：同名时引擎那边先赢，
                // 这是既有行为，不能因为加了过滤器就改掉。
                if (TryMakeFilterChip()) { e.Handled = true; break; }
                break;
            case Key.Tab:
                if (TryMakeChip()) { e.Handled = true; break; }
                if (TryMakeFilterChip()) { e.Handled = true; break; }
                CompleteSelected();
                e.Handled = true;
                break;
            case Key.Back:
                // 输入框已空时按退格 → 把标签还原成文字
                if (_chip != null && Input.Text.Length == 0)
                {
                    RemoveChip();
                    e.Handled = true;
                }
                break;
            case Key.Down:
                // 联想可见时 ↑↓ 归联想，结果列表保持原位；选完联想还能继续打字
                if (_suggestVisible)
                {
                    MoveSuggestion(1);
                    e.Handled = true;
                    break;
                }
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                if (_suggestVisible)
                {
                    MoveSuggestion(-1);
                    e.Handled = true;
                    break;
                }
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(5);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-5);
                e.Handled = true;
                break;
            case Key.Enter:
                /* 联想优先：只有"高亮在某条联想上"才拦截回车。
                 * 没有选中联想时仍然走下面的老逻辑（补全 / 执行结果），
                 * 这样不认识联想、只想搜原文的用户完全不受影响。 */
                if (_suggestVisible && SuggestList.SelectedItem is string suggestion)
                {
                    AcceptSuggestion(suggestion);
                    e.Handled = true;
                    break;
                }
                /* 搜索/网址/程序这几类结果，回车先"补全成引擎 + 空格"，
                 * 第二次回车才真正执行。
                 *
                 * 原因：用户敲 `bd` 时匹配到的是百度**搜索引擎**这一条，
                 * 它的载荷是 `https://www.baidu.com/s?wd={q}` 这种模板。
                 * 直接执行会把没替换的 {q} 原样打开 —— 浏览器里出现
                 * "搜索 {q}" 这种结果。补全是这一行本来的意图：
                 * 变成「bd 」标签，接着打关键词即可。 */
                if (Results.SelectedItem is QbarItem sel && ShouldCompleteOnEnter(sel.Result))
                {
                    CompleteSelected();
                    e.Handled = true;
                    break;
                }
                ExecuteSelected();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 这一条结果回车时是否该"补全"而不是"执行"。
    ///
    /// 判据：归类里带标签（说明它是个"引擎 / 程序 / 网址前缀"），
    /// 且载荷里还有没填的占位符 —— 那它就不是一个可以直接打开的地址。
    /// </summary>
    private static bool ShouldCompleteOnEnter(QbarResult r)
        => r.Kind is QbarKind.Search or QbarKind.Web or QbarKind.Run
           && r.Badge.Length > 0
           && r.Payload.Contains("{q}", StringComparison.Ordinal);

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        int idx = Math.Clamp(Results.SelectedIndex + delta, 0, _items.Count - 1);
        Results.SelectedIndex = idx;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void CompleteSelected()
    {
        // 补全会改输入框内容；旧联想立刻作废，否则列表还挂着上一个词的候选
        HideSuggestions();
        if (Results.SelectedItem is not QbarItem item) return;
        // 补全成“关键字 + 空格”，下一次按键即可变成标签
        string completion = item.Result.Kind switch
        {
            QbarKind.Run or QbarKind.Web or QbarKind.Search when item.Result.Badge.Length > 0
                => item.Result.Badge + " ",
            _ => item.Result.Title,
        };
        SetInputText(completion);
        TryMakeChip();      // 成了标签就等于换了引擎，没成就只是段文字 —— 两种情况都要重搜
        RunQuery();
    }

    // ---------------- 搜索联想 ----------------

    /// <summary>
    /// 当前输入是否具备"该出联想"的上下文。
    ///
    /// 三个条件缺一不可：
    ///   1. 设置里开着联想；
    ///   2. 已经选定搜索引擎（_chip 是 Search 类，且不是本地词典/程序那种伪搜索源）；
    ///   3. 有关键词（只有空格不算）。
    /// 普通输入（打文件名、算数、cmd）一条请求都不会发 —— 联想只服务于"网络搜索"。
    /// </summary>
    private bool TryGetSuggestionContext(out string keyword, out string engine)
    {
        keyword = "";
        engine = SuggestionEngines.Auto;

        // 面板已经收起/正在淡出时不再发请求：定时器可能在淡出之后才到点，
        // 此时 IsVisible 仍可能是 true，必须连 _closing 一起看。
        if (!IsVisible || _closing) return false;

        var q = _host.Settings.Qbar;
        if (!q.SuggestEnabled) return false;
        if (_chip is not { Kind: ChipKind.Search }) return false;
        if (QbarService.LooksLikeProgram(_chip.Value)) return false;

        string text = (Input.Text ?? "").Trim();
        if (text.Length == 0) return false;

        keyword = text;
        engine = SuggestionEngines.Resolve(q.SuggestEngine, _chip.Value);
        return true;
    }

    /// <summary>
    /// 只取消在途请求和防抖计时，不动界面。
    ///
    /// 和 HideSuggestions 分开的原因：ScheduleSuggestions 要先取消旧请求，
    /// 再判断上下文；而“旧候选怎么处理”是另一件事。现在一旦确认仍在搜索上下文，
    /// Schedule 会立刻 Hide 掉旧候选 —— 它们对应上一个关键词，留在屏幕上
    /// 就有被 ↑/回车误选的风险。取消和隐藏分开，逻辑才不会互相绑死。
    /// </summary>
    private void CancelSuggestionRequest()
    {
        _suggestTimer.Stop();

        CancellationTokenSource? cts = _suggestCts;
        _suggestCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); }
            catch { /* 已释放或已取消：没有需要补救的状态 */ }
            cts.Dispose();
        }

        // 序号 +1 让"已经拿到响应、正排在 UI 队列里"的旧请求也失效
        _suggestSeq++;
    }

    /// <summary>收起联想、清空列表并作废在途请求。上下文真的不成立时才调用。</summary>
    private void HideSuggestions()
    {
        CancelSuggestionRequest();
        _suggestVisible = false;
        SuggestList.ItemsSource = null;
        SuggestList.SelectedIndex = -1;
        SuggestBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 输入变化后重新计时。
    ///
    /// 先取消上一次请求（达成"每次输入只保留最后一次"），再判断上下文：
    /// 空输入/没有搜索标签时立刻隐藏；仍在搜索时也收起旧候选（它们属于上一个词），
    /// 250ms 后按新词重新请求并展开。
    /// </summary>
    private void ScheduleSuggestions()
    {
        CancelSuggestionRequest();
        if (!TryGetSuggestionContext(out _, out _))
        {
            HideSuggestions();
            return;
        }

        /* 旧候选必须立刻收起，不能只清高亮：它们对应的是**上一个关键词**，
           在 250ms 防抖窗口里按 ↓/回车会选中并执行一个与输入框无关的词。
           收起会让面板在连续打字时缩一下，但正确性优先于这点跳动；
           新结果回来后会按新词重新展开。 */
        if (_suggestVisible) HideSuggestions();
        _suggestTimer.Start();
    }

    /// <summary>
    /// 防抖到点：查缓存或发请求。
    ///
    /// 整个方法包在 try 里，且 provider 自身也保证不抛 —— 联想链路上的任何失败
    /// 都只能表现为"列表不出现"，不能冒泡成未处理异常（那会弹 WPF 崩溃框）。
    /// </summary>
    private async void OnSuggestTimerTick(object? sender, EventArgs e)
    {
        _suggestTimer.Stop();

        try
        {
            if (!TryGetSuggestionContext(out string keyword, out string engine))
            {
                HideSuggestions();
                return;
            }

            var q = _host.Settings.Qbar;
            int max = Math.Clamp(q.SuggestMax, 1, 10);
            int ttl = Math.Clamp(q.SuggestCacheMinutes, 0, 1440);
            int timeout = Math.Clamp(q.SuggestTimeoutMs, 200, 5000);

            // 缓存命中直接显示，不必再发请求；缓存 key 里带引擎，换引擎不会串词
            if (SuggestionCache.TryGet(engine, keyword, ttl, max, out IReadOnlyList<string> cached))
            {
                /* 缓存按"引擎 + 关键词"存，不含条数：用户把上限从 10 调到 3 后，
                   旧缓存仍是 10 条。显示前按当前上限截断，设置才算真的即时生效。 */
                ShowSuggestions(cached.Count > max ? cached.Take(max).ToList() : cached, engine, keyword);
                return;
            }

            int seq = ++_suggestSeq;
            var cts = new CancellationTokenSource();
            _suggestCts = cts;
            try
            {
                // 超时也走取消：HttpClient 的 Timeout 是全局的、改不了，
                // 而设置里的 200..5000ms 必须是每次请求独立的。
                cts.CancelAfter(timeout);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            IReadOnlyList<string> items;
            try
            {
                items = await SuggestionEngines.Create(engine, max).QueryAsync(keyword, cts.Token);
            }
            catch
            {
                // provider 已保证不抛；这是最后一道保险
                items = Array.Empty<string>();
            }
            finally
            {
                if (ReferenceEquals(_suggestCts, cts)) _suggestCts = null;
                cts.Dispose();
            }

            /* 序号对不上 = 用户又敲了字 / 切了标签 / 收起了面板。
             * 这次响应再准也已经过期，直接丢，绝不覆盖新状态。 */
            if (seq != _suggestSeq) return;

            // 空结果 / 超时（provider 把 OperationCanceledException 吞成空列表）/
            // 网络失败：一律静默收起，不弹任何错误 —— 联想失败不该打断搜索。
            if (items.Count == 0)
            {
                HideSuggestions();
                return;
            }

            SuggestionCache.Put(engine, keyword, ttl, max, items);
            ShowSuggestions(items, engine, keyword);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "搜索联想");
            HideSuggestions();
        }
    }

    private void ShowSuggestions(IReadOnlyList<string> items, string engine, string keyword)
    {
        // 异步期间上下文可能已经变了（标签被清、设置被改），显示前再核对一次，
        // 避免"请求发出时是百度、回来时已经切成谷歌"这种串台。
        if (items.Count == 0
            || !TryGetSuggestionContext(out string nowKeyword, out string nowEngine)
            || nowEngine != engine
            || !string.Equals(nowKeyword, keyword, StringComparison.Ordinal))
        {
            HideSuggestions();
            return;
        }

        SuggestList.ItemsSource = items;
        // 默认不高亮任何一条：这样直接按回车仍然是"搜索输入框原文"，
        // 只有按过 ↑↓ 才进入联想选择。少一次误触，行为也和用户预期一致。
        SuggestList.SelectedIndex = -1;
        _suggestVisible = true;
        SuggestBox.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 在联想项之间移动。
    ///
    /// 与结果列表不同，联想默认是 -1（没有选中），所以第一次按 ↑ 直接落到最后一条：
    /// 用户按 ↑ 的意图就是"我想用联想词"，不该先停在最后一条再按很多次。
    /// </summary>
    private void MoveSuggestion(int delta)
    {
        int count = SuggestList.Items.Count;
        if (count == 0) return;

        int current = SuggestList.SelectedIndex;
        int next = current < 0
            ? (delta > 0 ? 0 : count - 1)
            : Math.Clamp(current + delta, 0, count - 1);

        SuggestList.SelectedIndex = next;
        if (SuggestList.SelectedItem != null) SuggestList.ScrollIntoView(SuggestList.SelectedItem);
    }

    /// <summary>
    /// 采用一条联想词：填进输入框，然后用**当前标签**走既有查询并执行。
    ///
    /// 不在这里自己拼 URL —— QbarService 已经处理了 {q} 替换、程序型搜索源、
    /// 日志、最近使用和执行回调；复用那条路径才不会出现"联想打开的地址
    /// 和回车打开的地址不一样"的漂移。
    /// </summary>
    private void AcceptSuggestion(string suggestion)
    {
        if (_chip is not { Kind: ChipKind.Search }) return;
        if (string.IsNullOrWhiteSpace(suggestion)) return;

        HideSuggestions();
        SetInputText(suggestion);
        RunQuery();

        // 带标签查询的第一条就是该引擎的搜索；没有结果时保持输入框内容，不报错
        if (Results.SelectedItem is QbarItem item) ExecuteResult(item.Result);
    }

    /// <summary>鼠标点联想条目 = 选中并执行。键盘路径之外的唯一入口。</summary>
    private void OnSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if (!_suggestVisible) return;

        /* 用容器反查而不是拿 SelectedItem：PreviewMouseLeftButtonUp 时
         * 点击的项可能还没完成选中（点在文字边缘/内边距上尤其明显）。 */
        var container = ItemsControl.ContainerFromElement(SuggestList,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is not string suggestion) return;

        e.Handled = true;
        AcceptSuggestion(suggestion);
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        // 在文件菜单里直接打字 = 不想看菜单了，收起它、按新输入继续搜
        if (_menuFile != null) CloseFileMenu();

        /* 输入法组合期间不查：此刻输入框里的内容只是"还在挑的候选"，
         * 按它去搜盘既慢又全是噪声，还会随着每次候选更新刷屏。 */
        if (_imeComposing)
        {
            _pendingQueryAfterComposition = true;
            return;
        }

        ScheduleQuery();

        // 联想在结果查询之后单独调度：它不该拖慢结果列表的刷新，
        // 250ms 内继续打字只会重置计时器，不会发请求。
        ScheduleSuggestions();
    }

    private void OnCompositionStart(object sender, TextCompositionEventArgs e) => _imeComposing = true;

    private void OnCompositionUpdate(object sender, TextCompositionEventArgs e)
    {
        // 组合串还有内容 = 还没上屏；空串说明候选已经落成正文，交给 TextInput
        _imeComposing = e.TextComposition.CompositionText.Length > 0;
        if (_imeComposing) _pendingQueryAfterComposition = true;
    }

    private void OnCompositionEnd(object sender, TextCompositionEventArgs e)
    {
        _imeComposing = false;
        if (!_pendingQueryAfterComposition) return;
        _pendingQueryAfterComposition = false;

        // 上屏只补查一次（不是每个候选一次）
        ScheduleQuery();
        ScheduleSuggestions();
    }

    /// <summary>输入防抖：40–60ms 内的后续按键只重置计时器，不重发查询（红线 4）。</summary>
    private void ScheduleQuery()
    {
        /* 防抖时长取自设置（默认 45ms）：0 表示立刻查，给"我就是要即时反馈"的人留出口。 */
        int ms = Math.Clamp(_host.Settings.Qbar.Search.DebounceMs, 0, 200);
        if (ms == 0)
        {
            _queryTimer.Stop();
            RunQuery();
            return;
        }
        _queryTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _queryTimer.Stop();
        _queryTimer.Start();
    }

    private void OnQueryTimerTick(object? sender, EventArgs e)
    {
        _queryTimer.Stop();
        RunQuery();
    }

    /// <summary>真正生效的查询串：过滤器 chip 拼在最前面，翻译层才认得出这是过滤。</summary>
    private string EffectiveInput(string text)
    {
        QbarFilter? filter = _appliedFilter;
        if (filter == null) return text;
        return string.IsNullOrEmpty(text) ? filter.Keyword + ":" : filter.Keyword + ": " + text;
    }

    /// <summary>
    /// 两段式查询：
    ///   第一段 = 非文件结果（命令、过滤器、应用、网页、计算…），全在内存里算，立刻上屏；
    ///   第二段 = 文件结果在后台线程跑语义层，回来校验请求序号再合并。
    /// "输入到有反应"因此永远是即时的，索引扫描再慢也卡不住输入框（红线 2）。
    /// </summary>
    private void RunQuery()
    {
        _queryTimer.Stop();
        string text = EffectiveInput(Input.Text);
        int limit = Math.Max(6, _host.Settings.Qbar.ListCount + 2);

        /* 查词走自己的通道：结果不是文件，是一次网络查词。
           必须在下面那条"先出快结果、再补文件结果"的流水线之前截走，
           否则会先闪一堆文件名，再被释义盖掉。 */
        if (TryStartDictionary(text)) return;

        bool fresh = !string.Equals(text, _lastQueryText, StringComparison.Ordinal);
        _lastQueryText = text;
        int keep = fresh ? 0 : Math.Max(0, Results.SelectedIndex);

        var quick = _host.Qbar.QueryPrepared(text, _chip, limit, null, skipFiles: true);
        ApplyResults(quick, keep);
        UpdateDebugPanel(null);

        StartFileQuery(text, limit, keep);
    }

    /* ── 软件内查词 ────────────────────────────────────────────────
       ol 变成 chip 之后，后面输入的文字是"要查的词"，不是文件名。
       所以这里直接问词典 API（英→dictionaryapi.dev，中→萌典），
       把词义铺成结果行：回车复制这一条释义，Esc 关掉。
       为什么要自建这一层：把词丢给浏览器只是"换个地方查"，用户要的是
       在 Qbar 里看一眼就走 —— 这条路径不能跳窗口。 */

    /// <summary>
    /// 当前输入是不是「查词」。是就发起查词并返回 true（调用方别再跑文件搜索）。
    ///
    /// 两种写法都认：
    ///   · 关键字 + 空格 → 变成 chip，后面继续打词（chip.Kind == Command）
    ///   · 直接一行打完「ol hello」—— 不打空格也得能用，否则用户根本发现不了这个功能
    /// </summary>
    private bool TryStartDictionary(string text)
    {
        if (!TryResolveDictionary(text, out QbarCommand? dictCommand, out string word)) return false;
        if (dictCommand != null && !string.IsNullOrWhiteSpace(dictCommand.Path))
        {
            /* 用户在设置里给 ol 指定了程序（比如欧路词典 exe）→ 那是外部命令，
               交给命令执行路径，别抢它的活。 */
            return false;
        }

        int seq = ++_querySeq;
        _lastOutcome = null;

        if (word.Length == 0)
        {
            ApplyResults(new List<QbarResult>
            {
                new(QbarKind.Dictionary, Loc.T("查词"), Loc.T("在 ol 后面打一个词：ol hello / ol 银行"), Loc.T("词典"), "")
                {
                    Glyph = GlyphDictionary,
                },
            }, 0);
            return true;
        }

        ApplyResults(new List<QbarResult>
        {
            new(QbarKind.Dictionary, word, Loc.T("正在查…"), Loc.T("词典"), "") { Glyph = GlyphDictionary },
        }, 0);

        Task.Run(async () =>
        {
            QbarDictionaryEntry entry;
            try
            {
                entry = await QbarDictionary.LookupAsync(word, System.Threading.CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                entry = new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "",
                                                ex.Message);
            }

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (seq != _querySeq) return;                       // 又输入了新词，丢弃
                ApplyResults(BuildDictionaryResults(entry), 0);
                UpdateDebugPanel(null);
            }), DispatcherPriority.Background);
        });

        return true;
    }

    /// <summary>
    /// 拆出「查词关键字 + 词」。关键字从设置里取，所以用户把 ol 改成 dict 也照样生效。
    /// </summary>
    private bool TryResolveDictionary(string? text, out QbarCommand? command, out string word)
    {
        word = "";
        command = QbarCommandStore.Effective(_host.Settings.Qbar.Commands)
            .FirstOrDefault(c => string.Equals(c.Id, QbarCommandStore.BuiltinDictId, StringComparison.OrdinalIgnoreCase));
        if (command == null) return false;

        bool chipped = _chip is { Kind: ChipKind.Command } chip &&
                       string.Equals(chip.CommandId, QbarCommandStore.BuiltinDictId, StringComparison.OrdinalIgnoreCase);

        string raw = (text ?? "").Trim();
        if (!chipped)
        {
            string key = (command.Keyword ?? "").Trim();
            if (key.Length == 0) return false;
            if (!raw.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return false;
            /* old / olive 不该被当成查词：关键字后面必须是空白或到头。 */
            if (raw.Length > key.Length && !char.IsWhiteSpace(raw[key.Length])) return false;
            raw = raw.Substring(key.Length).Trim();
        }

        word = raw;
        return true;
    }

    private const string GlyphDictionary = "\uE82D";

    /// <summary>词条 → 结果行。第一条是词头（回车复制单词），后面一行一条释义（回车复制释义）。</summary>
    internal static List<QbarResult> BuildDictionaryResults(QbarDictionaryEntry entry)
    {
        var rows = new List<QbarResult>();
        if (entry == null) return rows;

        if (!entry.Ok)
        {
            rows.Add(new QbarResult(QbarKind.Dictionary, entry.Word,
                entry.Error ?? Loc.T("没查到这个词，换个拼写试试"), Loc.T("词典"), entry.Word)
            {
                Glyph = GlyphDictionary,
            });
            return rows;
        }

        string head = entry.Phonetic.Length > 0 ? entry.Phonetic : entry.Source;
        rows.Add(new QbarResult(QbarKind.Dictionary, entry.Word, head, Loc.T("词典"), entry.Word)
        {
            Glyph = GlyphDictionary,
            Score = -100,                       // 词头永远第一行
        });

        foreach (QbarDictionarySense sense in entry.Senses)
        {
            string title = string.IsNullOrWhiteSpace(sense.PartOfSpeech) ? Loc.T("释义") : sense.PartOfSpeech;
            rows.Add(new QbarResult(QbarKind.Dictionary, title, sense.Text, entry.Word, sense.Text)
            {
                Glyph = GlyphDictionary,
            });
        }
        return rows;
    }

    /// <summary>
    /// 后台文件检索。结果回来时必须同时满足：请求号没变、输入没变、没进文件菜单 ——
    /// 否则就是"旧词的结果盖在新词上"，这是这类改造最经典的 bug。
    /// </summary>
    private void StartFileQuery(string effective, int limit, int keep)
    {
        int seq = ++_querySeq;
        _lastOutcome = null;

        // 标签（搜索引擎）模式下不打文件结果；太短也不打（原生索引最少 2 字符）
        if (_chip != null || effective.Length < 2) return;

        // 索引是惰性启动的：先在本线程点一下，后台线程只做纯计算
        NativeSearchIndex.Start();

        QbarQueryContext context = QbarSearchEngine.FromSettings(_host.Settings);
        int fileLimit = Math.Min(6, limit);
        string expected = effective;

        Task.Run(() =>
        {
            QbarSearchOutcome? outcome = null;
            try
            {
                outcome = QbarSearchEngine.Run(context, expected, fileLimit);
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "Qbar 文件检索");
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (seq != _querySeq) return;                       // 旧请求，丢弃
                if (_menuFile != null) return;                       // 正在看文件菜单，别换列表
                if (!string.Equals(EffectiveInput(Input.Text), expected, StringComparison.Ordinal)) return;

                _lastOutcome = outcome;
                var full = _host.Qbar.QueryPrepared(expected, _chip, limit, outcome, false);
                ApplyResults(full, keep);
                UpdateDebugPanel(outcome);
            }), System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    private void ApplyResults(List<QbarResult> results, int keep)
    {
        /* 过滤器已经生效时，把"应用这个过滤器"那一行藏掉：
         * chip 就摆在输入框上，再给一条"点我应用 doc:"只会让人以为还没生效。 */
        QbarFilter? active = _appliedFilter;
        if (active != null)
        {
            string action = "qfilter:" + active.Id;
            results = results.Where(r => !string.Equals(r.Action, action, StringComparison.Ordinal)).ToList();
        }

        _items = results.Select(r => new QbarItem(r, _host.Settings.Qbar.ShowIcons)).ToList();
        Results.ItemsSource = _items;
        if (_items.Count > 0) Results.SelectedIndex = Math.Min(Math.Max(0, keep), _items.Count - 1);
        UpdateMode();
        UpdateHint();
        UpdateFacets();
        UpdateGroupHeader();
        SyncSuggestionSelection();

        /* 结果行的文字颜色必须在**列表项生成之后**才能刷。
         *
         * ApplyStyle 里遍历可视树时，DataTemplate 还没实例化，
         * 结果就是行内标题保持 XAML 里写死的浅色 —— 浅色面板上几乎看不见。
         * 这里等布局完成再补一遍。 */
        Dispatcher.BeginInvoke(new Action(() => ApplyRowPalette()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 空态分组标题（"最近文件"）。
    /// 建议项和普通结果共用同一个列表（红线 6），所以标题只在这一档建议真的
    /// 存在时才出现；用户一打字，QbarService 的 DefaultResults 不再被调用，
    /// EmptyStateGroup 自然变空，标题跟着消失。
    /// </summary>
    private void UpdateGroupHeader()
    {
        string group = _host.Qbar.EmptyStateGroup;
        bool show = group.Length > 0 && _items.Count > 0;
        if (show) GroupHeaderText.Text = group;
        GroupHeaderBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把"当前选中的是不是建议行"同步给隐藏热键。</summary>
    private void SyncSuggestionSelection()
    {
        int index = Results.SelectedIndex;
        if (index < 0 || index >= _items.Count)
        {
            SuggestionsSelection.Clear();
            return;
        }

        QbarResult result = _items[index].Result;
        if (result.Suggestion.Length == 0)
        {
            SuggestionsSelection.Clear();
            return;
        }

        /* 判断文件/文件夹用磁盘现状而不是扩展名：桌面上的快捷方式、
         * 没有后缀的文件夹都靠扩展名猜不出来，写错黑名单会导致
         * "隐藏了但还在"（文件夹进了文件黑名单就永远匹配不上）。 */
        bool isFolder = Directory.Exists(result.Payload);
        SuggestionsSelection.Set(result.Payload, isFolder, result.Suggestion);
    }

    private void OnSuggestionsChanged(RecSnapshot snapshot)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsVisible) return;
            if (Input.Text.Length != 0 || _chip != null) return;
            if (_menuFile != null) return;   // 正在文件菜单里，别抽掉他的列表
            RunQuery();
        }), DispatcherPriority.Background);
    }

    // ---------------- 语义层：过滤器 / 分面 / 调试面板 ----------------

    /// <summary>
    /// "doc + 空格" / "doc: + 空格" → 过滤器 chip。
    ///
    /// 以前只有"点结果里那条『过滤器：文档』"这一条路，用户得先打关键字、
    /// 再从列表里挑一行 —— 等于把一个应该由空格完成的动作藏进了鼠标操作。
    /// 现在按空格的瞬间就问一次过滤器表，命中就直接生效并立刻开始搜。
    /// </summary>
    private bool TryMakeFilterChip()
    {
        if (_appliedFilter != null) return false;
        QbarFilter? filter = QbarService.ResolveFilterTrigger(Input.Text, _host.Settings.Qbar);
        if (filter == null) return false;
        ApplyFilter(filter.Id);
        return true;
    }

    /// <summary>应用一个过滤器（把 doc: / doc + 空格 变成输入框左侧的 chip）。</summary>
    private void ApplyFilter(string id)
    {
        QbarFilter? filter = QbarFilterCatalog.Create(_host.Settings.Qbar.Filters).All
            .FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
        if (filter == null) return;

        /* 输入框里那个触发词（"doc" 或 "doc:"）是给这次点击/空格用的，
         * 既然变成了 chip 就得从文本里去掉：留着的话查询串里会出现两次 doc，
         * 翻译层会多展开一遍扩展名（结果一样，但调试面板里看到两个 ext: 列表，
         * 排障时会以为是 bug）。 */
        string text = Input.Text.TrimStart();
        int space = text.IndexOf(' ');
        string head = space < 0 ? text : text.Substring(0, space);
        bool typedTrigger = head.EndsWith(":", StringComparison.Ordinal)
                            || head.Equals(filter.Keyword, StringComparison.OrdinalIgnoreCase);
        if (typedTrigger)
            text = space < 0 ? "" : text.Substring(space + 1).TrimStart();

        _appliedFilter = filter;
        FilterChipLabel.Text = string.IsNullOrEmpty(filter.Name) ? filter.Keyword : filter.Name;
        FilterChipBox.Visibility = Visibility.Visible;
        SetInputText(text);
        Input.Focus();
        Input.CaretIndex = Input.Text.Length;
        RunQuery();
    }

    /// <summary>清除过滤器 chip：Esc / 叉掉 chip / 空格退格都走这里。</summary>
    private void ClearFilter()
    {
        if (_appliedFilter == null) return;
        ResetFilterChip();
        RunQuery();
    }

    /// <summary>只把 chip 状态抹掉，不重查 —— 打开面板时用，那次查询由 Open() 自己安排。</summary>
    private void ResetFilterChip()
    {
        _appliedFilter = null;
        FilterChipBox.Visibility = Visibility.Collapsed;
    }

    private void OnFilterChipClose(object sender, RoutedEventArgs e) => ClearFilter();

    /// <summary>分面摘要：只在有文件结果时显示，且明确写"本页"（不是全库统计）。</summary>
    private void UpdateFacets()
    {
        QbarSearchOutcome? outcome = _lastOutcome;
        bool on = _host.Settings.Qbar.Search?.FacetsEnabled != false;
        string text = on && outcome != null && outcome.Hits.Count > 0 ? outcome.FacetSummary() : "";
        FacetText.Text = text;
        FacetText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 查询调试面板：把翻译层每一层的中间结果、请求的参数、命中数、耗时全摊开。
    /// 这一条是排障的生命线 —— 没有它，翻译层出问题只能看最终结果猜原因。
    /// 开关在设置里（搜索选项 / 查询调试），也可以 Ctrl+D 就地切换。
    /// </summary>
    private void UpdateDebugPanel(QbarSearchOutcome? outcome)
    {
        if (_host.Settings.Qbar.Search?.DebugPanel != true || _chip != null)
        {
            DebugBox.Visibility = Visibility.Collapsed;
            return;
        }

        string text = EffectiveInput(Input.Text);
        QbarTranslateResult translation = outcome?.Translation
            ?? QbarQueryTranslator.Translate(text, QbarFilterCatalog.Create(_host.Settings.Qbar.Filters), DateTime.Now);

        var sb = new StringBuilder();
        sb.AppendLine("输入: " + (translation.Input.Length == 0 ? "(空)" : translation.Input));
        sb.AppendLine("归一化: " + translation.Normalized);
        sb.AppendLine("索引查询: " + translation.EverythingText);
        sb.AppendLine("后端: " + (outcome == null ? "（尚未查询）" : outcome.BackendText));
        sb.AppendLine("请求: " + translation.RequestDetail);
        foreach (QbarDiagEntry stage in translation.Stages)
            sb.AppendLine("  [" + stage.Stage + "] " + stage.Detail + (stage.Warn ? "   <-- 需要留意" : ""));

        if (outcome != null)
        {
            sb.AppendLine("候选: " + outcome.RawCount + " 条 → 展示 " + outcome.Hits.Count + " 条");
            if (outcome.HiddenCount > 0)
                sb.AppendLine("被规则隐藏: Ignored " + outcome.HiddenIgnored +
                              " / DoNotIndex " + outcome.HiddenDoNotIndex);
            sb.AppendLine(outcome.TimingText);
            if (outcome.FuzzyUsed) sb.AppendLine("模糊匹配: 已启用");
            if (outcome.PinyinUsed) sb.AppendLine("拼音索引: 命中");
            if (outcome.PinyinPending) sb.AppendLine("拼音索引: 正在后台构建");
            if (outcome.TimedOut) sb.AppendLine("检索超时: 已按当前候选返回");
            string facets = outcome.FacetSummary();
            if (facets.Length > 0) sb.AppendLine(facets);
        }

        DebugText.Text = sb.ToString().TrimEnd();
        DebugBox.Visibility = Visibility.Visible;
    }

    /// <summary>点"有 N 条结果被优先级规则隐藏"时：把数量和样本摊开，而不是静默丢掉。</summary>
    private void ShowHiddenReasons()
    {
        QbarSearchOutcome? outcome = _lastOutcome;
        if (outcome == null || outcome.HiddenCount == 0)
        {
            _host.Notify(Loc.T("这次查询没有被优先级规则隐藏的结果"));
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(Loc.F("被优先级规则隐藏 {0} 条（Ignored {1} / DoNotIndex {2}）",
            outcome.HiddenCount, outcome.HiddenIgnored, outcome.HiddenDoNotIndex));
        foreach (string sample in outcome.HiddenSamples) sb.AppendLine("  " + sample);
        _host.Notify(sb.ToString().TrimEnd());
    }

    // ---------------- 语义层：命令执行 ----------------

    /// <summary>动作行分派。返回 true = 这条结果已经被语义层处理掉，不要再当文件打开。</summary>
    private bool TryRunAction(QbarResult result)
    {
        string action = result.Action;
        if (action.StartsWith("qcmd:", StringComparison.Ordinal))
        {
            ExecuteQbarCommand(result);
            return true;
        }
        if (action.StartsWith("qfilter:", StringComparison.Ordinal))
        {
            ApplyFilter(action.Substring("qfilter:".Length));
            return true;
        }
        if (string.Equals(action, "qbar:hidden", StringComparison.Ordinal))
        {
            ShowHiddenReasons();
            return true;
        }
        if (string.Equals(action, "qbar:everything", StringComparison.Ordinal))
        {
            OpenEverythingForSearch();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 文件搜索结果为空且后端报告"Everything 不可用"时，那条提示行点下去就走这里。
    ///
    /// 现在 Everything 是**内置**的（客户端与 SDK 都打在程序集里），所以这里不再
    /// 依赖用户装过 Everything：先让内置客户端起来，等它有反应就自动把这次查询重跑
    /// 一遍 —— 用户点一下就能看到结果，而不是"打开了某个程序，请自己再搜一次"。
    /// 只有内置那份起不来（被安全软件拦掉之类）才回落到系统里已安装的 Everything。
    /// </summary>
    private void OpenEverythingForSearch()
    {
        _host.Notify(Loc.T("正在启动内置 Everything…"));
        Task.Run(() =>
        {
            bool ok = EverythingBootstrap.EnsureClient(3000);
            if (!ok)
            {
                try
                {
                    var info = new ProcessStartInfo("Everything.exe") { UseShellExecute = true };
                    Process.Start(info);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("启动 Everything 失败：" + ex.Message);
                }
            }

            /* 索引可能还在加载，给它几秒；期间不弹任何窗口，用户只是看到"稍等"。 */
            var watch = Stopwatch.StartNew();
            while (!EverythingBootstrap.Ready && watch.ElapsedMilliseconds < 6000)
                System.Threading.Thread.Sleep(200);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (EverythingBootstrap.Ready)
                {
                    _host.Notify(Loc.T("Everything 已就绪，重新搜索"));
                    RunQuery();
                }
                else
                {
                    _host.Notify(EverythingBootstrap.LastDetail.Length > 0
                        ? EverythingBootstrap.LastDetail
                        : Loc.T("Everything 还没准备好，稍等几秒再搜一次"));
                }
            }));
        });
    }

    private void ExecuteQbarCommand(QbarResult result)
    {
        string id = result.Action.Substring("qcmd:".Length);
        List<QbarCommand> table = QbarCommandStore.Effective(_host.Settings.Qbar.Commands);
        QbarCommand? command = table.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
        if (command == null)
        {
            _host.Notify(Loc.T("这条命令已经不存在，请到设置里重新配置"));
            return;
        }

        string? actionPath = SelectedPath();
        string currentFolder = CurrentFolder(actionPath);

        /* 内置动作默认走软件自己的实现；但设置里给它填了程序，就得按用户填的来，
           否则「程序」那一栏是个摆设（用户反馈过这一点）。ol 例外：它自己会把
           默认参数 -w "{query}" 补上，所以留在 RunInternalCommand 里处理。 */
        bool overridden = QbarCommandStore.IsInternalAction(command.Id)
                          && !string.Equals(command.Id, QbarCommandStore.BuiltinDictId, StringComparison.Ordinal)
                          && !string.IsNullOrWhiteSpace(command.Path);
        if (QbarCommandStore.IsInternalAction(command.Id) && !overridden)
        {
            RunInternalCommand(command, actionPath, currentFolder, result.Arguments);
            return;
        }

        HideQbar();
        CoreLog.Info($"[Qbar] 执行命令：{command.Title} / {command.Path}");

        /* 命令执行失败必须说清楚是哪一层失败：取不出文件名 / 找不到程序 / 提权被取消。
         * 三种都报"执行失败"的话，用户根本不知道该改 Path 还是该点掉 UAC。 */
        QbarCommandResult run = QbarCommandStore.Execute(command, result.Arguments, currentFolder, actionPath);
        if (!run.Ok)
        {
            _host.Notify(run.Message);
            return;
        }
        RecordHistory("command", command.Title, command.Path, Results.SelectedIndex);
        if (run.Message.Length > 0) _host.Notify(run.Message);
    }

    private void RunInternalCommand(QbarCommand command, string? actionPath, string currentFolder, string tail)
    {
        try
        {
            switch (command.Id)
            {
                case QbarCommandStore.BuiltinCopyPathId:
                    if (string.IsNullOrEmpty(actionPath))
                    {
                        _host.Notify(Loc.T("没有选中项，先选一个文件或文件夹"));
                        return;
                    }
                    System.Windows.Clipboard.SetText(actionPath);
                    _host.Notify(Loc.T("路径已复制"));
                    HideQbar();
                    return;

                case QbarCommandStore.BuiltinNewFolderId:
                {
                    string dir = UniquePath(currentFolder, Loc.T("新建文件夹"), "");
                    Directory.CreateDirectory(dir);
                    HideQbar();
                    RecentTargets.Record(dir);
                    RecordHistory("create", System.IO.Path.GetFileName(dir), dir, Results.SelectedIndex);
                    _host.Notify(Loc.T("已创建：") + dir);
                    return;
                }

                case QbarCommandStore.BuiltinNewFileId:
                {
                    string file = UniquePath(currentFolder, Loc.T("新建文件"), ".txt");
                    if (!File.Exists(file)) File.WriteAllText(file, "");
                    HideQbar();
                    RecordHistory("create", System.IO.Path.GetFileName(file), file, Results.SelectedIndex);
                    _host.Notify(Loc.T("已创建：") + file);
                    return;
                }

                case QbarCommandStore.BuiltinParentId:
                {
                    string parent = "";
                    if (!string.IsNullOrEmpty(actionPath))
                    {
                        parent = Directory.Exists(actionPath)
                            ? (System.IO.Path.GetDirectoryName(actionPath.TrimEnd('\\')) ?? "")
                            : (System.IO.Path.GetDirectoryName(actionPath) ?? "");
                    }
                    if (parent.Length == 0)
                    {
                        _host.Notify(Loc.T("没有可以返回的上一级目录"));
                        return;
                    }
                    SetInputText(parent);
                    Input.CaretIndex = Input.Text.Length;
                    RunQuery();
                    return;
                }

                case QbarCommandStore.BuiltinRefreshId:
                    RefreshIndex();
                    return;

                case QbarCommandStore.BuiltinDictId:
                    RunDictionaryCommand(command, tail);
                    return;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Error("内置命令执行失败", ex);
            _host.Notify(Loc.T("执行失败：") + ex.Message);
        }
    }

    /// <summary>
    /// 查词：装了欧路词典就直接把词送过去，没装就打开网页词典 —— 这条命令必须在
    /// "什么都没配"的机器上也能用，否则「ol 不生效」会被当成功能坏了。
    /// 词里带引号/空格的清洗交给 shell 参数那一层（这里只去掉双引号，避免把
    /// -w " 的参数截断）。
    /// </summary>
    private void RunDictionaryCommand(QbarCommand command, string tail)
    {
        string query = (tail ?? "").Trim();
        string keyword = string.IsNullOrWhiteSpace(command.Keyword) ? "ol" : command.Keyword.Trim();
        if (query.Length == 0)
        {
            _host.Notify(Loc.F("在 {0} 后面输入要查的词，例如 {0} hello", keyword));
            return;
        }

        /* 程序从哪来、参数怎么展开、要不要提权，全交给 DictionaryLauncher ——
           面板里按回车和「直接搜索」里的查词动作共用同一个出口，
           参数模板（出厂 -w "{query}"）不可能再出现"一边有、一边空"的走样。 */
        HideQbar();

        DictionaryLaunchResult run =
            DictionaryLauncher.Launch(query, command, CurrentFolder(SelectedPath()), SelectedPath());

        if (!run.Ok)
        {
            _host.Notify(run.Message);
            return;
        }

        RecordHistory("command", run.External ? command.Title : Loc.T("网页词典"), run.Target, Results.SelectedIndex);
        if (run.Message.Length > 0) _host.Notify(run.Message);
    }

    /* 欧路装在哪由 EudicLocator 负责（PATH → App Paths → 开始菜单快捷方式 → 常见目录），
       调用点在 DictionaryLauncher —— 面板按回车和「直接搜索」里的查词共用同一个出口，
       这里不再留一份"只有面板看得见"的实现。 */

    /// <summary>
    /// 刷新索引状态。刻意**不**做"删库重建"：那是用户按了设置里的重建才该发生的事，
    /// 一条随手打的命令就重扫全盘，会让人以为机器卡死了。
    /// </summary>
    private void RefreshIndex()
    {
        QbarPinyinIndex.Invalidate();
        if (!NativeSearchIndex.Ready && !NativeSearchIndex.Building) NativeSearchIndex.Start();

        string status = EverythingApi.IsAvailable && EverythingApi.IsDbLoaded
            ? Loc.F("Everything 索引已加载（{0}）", EverythingApi.VersionText)
            : NativeSearchIndex.Ready
                ? Loc.F("文件索引已就绪：{0} 个文件 / {1} 个目录", NativeSearchIndex.FileCount, NativeSearchIndex.DirCount)
                : NativeSearchIndex.Building ? Loc.T("文件索引正在建立，稍后可用") : Loc.T("文件索引不可用，请检查索引目录权限");
        _host.Notify(status);

        QbarPinyinIndex.Start();
        RunQuery();
    }

    private string? SelectedPath()
    {
        if (Results.SelectedItem is QbarItem selected &&
            selected.Result.Kind is QbarKind.File or QbarKind.Run &&
            selected.Result.Payload.Length > 0)
            return selected.Result.Payload;

        for (int i = 0; i < _items.Count; i++)
        {
            QbarResult r = _items[i].Result;
            if (r.Kind is QbarKind.File or QbarKind.Run && r.Payload.Length > 0) return r.Payload;
        }
        return null;
    }

    private static string CurrentFolder(string? actionPath)
    {
        if (!string.IsNullOrEmpty(actionPath))
        {
            try
            {
                if (Directory.Exists(actionPath)) return actionPath;
                string? parent = System.IO.Path.GetDirectoryName(actionPath);
                if (!string.IsNullOrEmpty(parent)) return parent;
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "推断命令工作目录");
            }
        }
        return Environment.CurrentDirectory;
    }

    private static string UniquePath(string folder, string baseName, string extension)
    {
        string candidate = System.IO.Path.Combine(folder, baseName + extension);
        int n = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = System.IO.Path.Combine(folder, baseName + " (" + n++ + ")" + extension);
        return candidate;
    }

    /// <summary>历史只喂建议排序，不参与文件结果重排（否则"常打开"会污染搜索相关性）。</summary>
    private void RecordHistory(string kind, string name, string path, int rank)
    {
        if (_host.Settings.Qbar.Search?.HistoryEnabled == false) return;
        if (string.IsNullOrEmpty(path)) return;
        QbarHistory.Record(kind, name, path, Input.Text, rank);
    }

    /// <summary>给结果行里的文字重新上色（模板生成后调用）。</summary>
    private void ApplyRowPalette()
    {
        var q = _host.Settings.Qbar;
        bool followTheme = string.IsNullOrWhiteSpace(q.PanelColor)
                           || q.PanelColor.Equals("auto", StringComparison.OrdinalIgnoreCase);
        bool light = IsLightColor(followTheme ? ThemePanelColor() : ParseColor(q.PanelColor));

        Color primary = light ? Color.FromRgb(0x16, 0x1A, 0x23) : Colors.White;
        Color secondary = light ? Color.FromRgb(0x5B, 0x64, 0x78) : Color.FromRgb(0xC9, 0xD6, 0xEC);
        Color faint = light ? Color.FromRgb(0x8A, 0x93, 0xA6) : Color.FromRgb(0x7F, 0x8D, 0xA8);

        var bPrimary = Frozen(primary);
        var bSecondary = Frozen(secondary);
        var bFaint = Frozen(faint);

        // 只处理列表区域，别动输入框和提示行（它们已由 ApplyStyle 刷过）
        foreach (var tb in FindAll<TextBlock>(Results))
        {
            tb.Foreground = tb.FontSize switch
            {
                >= 13.5 => bPrimary,      // 行标题
                >= 11 => bSecondary,      // 副标题
                _ => bFaint,              // 徽标、角标
            };
        }
    }

    private void UpdateMode()
    {
        if (_menuFile != null) return;
        if (_chip != null)
        {
            HintText.Text = _chip.Kind switch
            {
                ChipKind.Search => Loc.T("Enter 搜索"),
                ChipKind.Web => Loc.T("Enter 打开"),
                _ => Loc.T("Enter 启动"),
            };
            return;
        }
        string text = Input.Text;
        string hint = Loc.T("Enter 执行");
        if (text.StartsWith('=')) hint = "Enter 复制结果";
        else if (text.StartsWith('/')) hint = "内置命令";
        else if (text.Contains(' ') && _host.Settings.Search.Any(s =>
                     s.Key.Equals(text.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
            hint = Loc.T("搜索");
        else if (text.Length > 0 && QbarService.LooksLikeMath(text)) hint = Loc.T("计算");
        HintText.Text = hint;
    }

    private void UpdateHint()
    {
        if (_menuFile != null) return;
        if (Results.SelectedItem is not QbarItem item) return;
        HintText.Text = item.Result.Kind switch
        {
            QbarKind.Math or QbarKind.Dictionary => Loc.T("Enter 复制结果"),
            QbarKind.Clipboard => Loc.T("Enter 粘贴"),
            QbarKind.Window => Loc.T("Enter 切换窗口"),
            QbarKind.Command => Loc.T("Enter 执行"),
            QbarKind.Search => _chip != null ? Loc.T("Enter 搜索") : Loc.T("Enter 打开搜索"),
            QbarKind.Web => Loc.T("Enter 打开网址"),
            QbarKind.Run => Loc.T("Enter 启动"),
            _ => Loc.T("Enter 执行"),
        };
    }

    /// <summary>Ctrl+P：把当前选中的文件/文件夹/网址/程序固定到 Qbar 空面板。</summary>
    private void PinSelected()
    {
        if (Results.SelectedItem is not QbarItem item) return;
        var r = item.Result;
        if (r.Kind is not (QbarKind.File or QbarKind.Run or QbarKind.Web))
        {
            _host.Notify(Loc.T("这条结果不能固定：请选择文件、文件夹、网址或程序"));
            return;
        }
        if (string.IsNullOrWhiteSpace(r.Payload)) return;

        var pinned = _host.Settings.Qbar.Pinned;
        if (pinned.Any(x => x.Value.Equals(r.Payload, StringComparison.OrdinalIgnoreCase)))
        {
            _host.Notify(Loc.T("这个目标已经固定过了"));
            return;
        }

        string title = string.IsNullOrWhiteSpace(r.Title)
            ? (System.IO.Path.GetFileName(r.Payload.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : r.Payload)
            : r.Title;
        string key = title;
        int n = 2;
        while (pinned.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) key = $"{title} {n++}";

        pinned.Add(new QEntry
        {
            Key = key,
            Value = r.Payload,
            Comment = title,
            IconPath = r.IconPath ?? "",
            Arguments = r.Arguments,
            RunAsAdmin = r.RunAsAdmin,
        });
        _host.SaveSettings();
        /* 固定是"立刻要生效"的动作：不能等 400ms 防抖。
         * 用户按完 Ctrl+P 可能马上就关面板/退程序，没 flush 的话
         * 这一条只活在内存里，重启后固定项就凭空消失了。 */
        _host.FlushSettings();
        _host.Notify(Loc.F("已固定到 Qbar：{0}", title));
    }

    private void ExecuteSelected()
    {
        if (_closing) return;
        if (Results.SelectedItem is not QbarItem item) return;

        /* 文件菜单模式下，选中的是一条"针对文件的操作"，按 id 查表执行，
         * 不能走 ExecuteResult —— 那会把菜单行当成结果去打开。 */
        if (_menuFile != null)
        {
            if (_menuActions.TryGetValue(item.Result.Arguments, out Action? act) && act != null) act();
            return;
        }

        ExecuteResult(item.Result);
    }

    /// <summary>
    /// 执行一条结果。`ExecuteSelected` 和“空格直接触发快捷命令”共用，
    /// 保证两条路径的收尾（隐藏面板、日志、异常提示）完全一致。
    /// </summary>
    private void ExecuteResult(QbarResult result)
    {
        /* 面板一开始淡出就不再执行：双击手势的两次点击间隔很短
         * （第二次抬起常常落在 90ms 淡出还没结束的时间里），没有这道
         * 闸门的话同一个结果会被执行两遍 —— 启动程序开两个窗口、
         * 打开网址开两个标签页。淡出期间的 Enter 同理。 */
        if (_closing) return;

        /* 语义层产出的"动作行"不是普通结果：过滤器改的是查询、命令要就地执行、
         * 被隐藏结果的说明只是弹一条提示。这三类都不能交给 QbarService.Execute ——
         * 那会把它们当成文件/程序去打开。 */
        if (result.Action.Length > 0 && TryRunAction(result)) return;

        if (result.Kind == QbarKind.File && result.Payload.Length > 0)
            RecordHistory("select", result.Title, result.Payload, Results.SelectedIndex);

        /* 记一行日志：这是"到底执行了几次"的唯一可靠观测量。
         * e2e 曾经靠"新起几个记事本进程"判断双击是否只执行一次，
         * 但 Win11 的记事本是标签页复用的 —— 执行两次也只多一个标签、
         * 不多进程，于是测试把"执行了"误判成"没执行"。
         * 日志不受系统行为影响，也不会被单实例程序带偏。 */
        CoreLog.Info($"[Qbar] 执行结果：{result.Title}");

        HideQbar();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _host.Qbar.Execute(result, BuildCallbacks());
            }
            catch (Exception ex)
            {
                CoreLog.Error("Qbar 执行失败", ex);
                _host.Notify(Loc.T("执行失败：") + ex.Message);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 在文件结果上右键：**不弹系统菜单**，而是把整个结果列表就地换成针对这条文件的
    /// 操作项 —— 列表本身就是菜单，↑↓ 挑、Enter 执行，Esc / 右键 / 直接打字都回搜索结果。
    ///
    /// 为什么不用 Explorer 的原生 IContextMenu：Qbar 是无边框弹层（AllowsTransparency
    /// + 自己抢前台），原生菜单属于另一套窗口和消息循环，弹出来会把面板挤到后面，
    /// 选完还得处理"焦点回不来"；而用户要的形态本来就是"列表变成菜单"。
    /// </summary>
    private void OnResultsRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // 已经在菜单里再右键一次 = 收起菜单，回到刚才那批搜索结果
        if (_menuFile != null) { CloseFileMenu(); return; }

        if (Results.ContainerFromElement(e.OriginalSource as DependencyObject) is not ListBoxItem row ||
            row.DataContext is not QbarItem item)
            return;

        QbarResult r = item.Result;
        if (r.Kind != QbarKind.File || r.Payload.Length == 0) return;
        OpenFileMenu(item);
    }

    /// <summary>把结果列表换成针对某个文件的一串操作。</summary>
    private void OpenFileMenu(QbarItem owner)
    {
        string path = owner.Result.Payload;
        bool isDir = Directory.Exists(path);
        bool exists = isDir || File.Exists(path);
        bool isProgram = !isDir && IsRunnableFile(path);

        _menuFile = owner;
        _menuReturnItems = _items;
        _menuReturnIndex = Results.SelectedIndex;
        _menuActions.Clear();

        var rows = new List<QbarItem>();
        int seq = 0;

        /* detail 是行副标题：把完整路径带上，用户能一眼确认"操作的是哪一个"。
         * 菜单行都是 QbarKind.Command，图标靠 Glyph 一条一个区分。 */
        void Add(string title, string detail, string glyph, Action run)
        {
            string id = (++seq).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _menuActions[id] = run;
            var result = new QbarResult(QbarKind.Command, title, detail, "", "", Arguments: id)
            {
                Glyph = glyph,
            };
            rows.Add(new QbarItem(result, _host.Settings.Qbar.ShowIcons));
        }

        /* 复制、属性这类动作做完把菜单收起来、面板留着（能接着对下一个文件右键）；
         * 打开文件、开资源管理器这种会把面板收掉。 */
        void AddLocal(string title, string detail, string glyph, Action run)
            => Add(title, detail, glyph, () => { CloseFileMenu(); run(); });

        Add("打开", path, "\uE8E5", () =>
        {
            QbarItem file = owner;
            CloseFileMenu();
            ExecuteResult(file.Result);
        });

        if (isProgram)
        {
            Add("以管理员身份运行", path, "\uE7EF", () =>
            {
                CloseFileMenu();
                HideQbar();
                Dispatcher.BeginInvoke(new Action(() => RunAsAdmin(path)));
            });
        }

        if (exists)
        {
            AddLocal("打开所在文件夹", isDir ? Loc.T("上一级") : Loc.T("在资源管理器中选中"), "\uE8B7",
                () => OpenContainingFolder(path));
            AddLocal("复制", path, "\uE8C8", () => SetFileClipboard(path, cut: false));
            AddLocal("剪切", path, "\uE8C6", () => SetFileClipboard(path, cut: true));
        }

        AddLocal("复制路径", path, "\uE71B", () => CopyText(path));
        if (exists) AddLocal("属性", path, "\uE946", () => ShowProperties(path));

        /* 只有建议行才给"不建议"：普通搜索结果里的文件是用户主动搜出来的，
         * 把它藏掉等于让人下次搜不到，纯属帮倒忙。 */
        if (owner.Result.Suggestion.Length > 0)
        {
            bool hideFolder = isDir;
            AddLocal(hideFolder ? Loc.T("不建议此文件夹") : Loc.T("不建议此文件"), path, "\uE7BA", () =>
            {
                var service = _host.Suggestions;
                if (service == null) return;
                if (service.Hide(path, hideFolder, out string error))
                {
                    ToastWindow.Show(Loc.T("已隐藏，可在设置里还原"), "info");
                    if (Input.Text.Length == 0) RunQuery();
                }
                else
                {
                    ToastWindow.Show(Loc.T("隐藏失败：") + error, "error");
                }
            });
        }
        AddLocal("返回搜索结果", Loc.T("Esc"), "\uE72B", () => { });

        MenuHeaderText.Text = path;
        MenuHeaderBox.Visibility = Visibility.Visible;
        ModeIconBox.Visibility = Visibility.Visible;
        ModeIcon.Text = "\uE712";
        HintText.Text = Loc.T("Enter 执行");
        FooterHint.Text = Loc.T("↑↓ 选择    Enter 执行    Esc 或右键返回搜索结果");

        _items = rows;
        Results.ItemsSource = _items;
        if (_items.Count > 0) Results.SelectedIndex = 0;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    /// <summary>收起文件菜单，把刚才那批搜索结果原样换回来。</summary>
    private void CloseFileMenu()
    {
        if (_menuFile == null) return;

        _menuFile = null;
        _menuActions.Clear();
        MenuHeaderBox.Visibility = Visibility.Collapsed;
        ModeIconBox.Visibility = Visibility.Collapsed;
        FooterHint.Text = Loc.T(FooterDefaultText);

        _items = _menuReturnItems;
        Results.ItemsSource = _items;
        if (_items.Count > 0) Results.SelectedIndex = Math.Clamp(_menuReturnIndex, 0, _items.Count - 1);
        UpdateMode();
        UpdateHint();
    }

    /// <summary>这条路径是不是"能当程序跑"的类型（决定要不要给「以管理员身份运行」）。</summary>
    private static bool IsRunnableFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase);
    }

    private void RunAsAdmin(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            // UAC 被拒会抛异常走 catch，所以走到这里就是真的启动了
            RecentTargets.Record(path);
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("以管理员身份运行失败：") + ex.Message);
        }
    }

    /// <summary>
    /// 「打开所在文件夹」。v1.5.1 起统一交给
    /// <see cref="Core.Windows.WindowService.RevealInForeground"/>：
    /// 优先 <c>SHOpenFolderAndSelectItems</c>（复用已开窗口 + 选中）、
    /// 文件被删了逐级上溯、提权时借 shell 之手 —— 不再自己拼 explorer 命令行。
    ///
    /// 放后台：in-proc 那条最多等 3 秒，而这里是 Qbar 的右键菜单回调。
    /// </summary>
    private void OpenContainingFolder(string path)
    {
        _ = Task.Run(() =>
        {
            Core.Windows.WindowService.RevealOutcome outcome =
                Core.Windows.WindowService.RevealInForeground(path, openFolderOnly: false);

            if (outcome.Launched)
            {
                if (outcome.WentUp) _host.Notify(Loc.T("文件已不在了，已打开上级目录"));
                return;
            }

            _host.Notify(Loc.T(outcome.Reachable ? "打开文件夹失败：" : "所在位置无法访问")
                         + (outcome.Why.Length > 0 ? outcome.Why : ""));
        });
    }

    private void SetFileClipboard(string path, bool cut)
    {
        try
        {
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { path });
            // Explorer 用这个 DWORD 区分“复制”和“剪切”：1=复制，2=剪切。
            data.SetData("Preferred DropEffect", new MemoryStream(cut ? new byte[] { 2, 0, 0, 0 } : new byte[] { 1, 0, 0, 0 }));
            Clipboard.SetDataObject(data, copy: true);
            _host.Notify(cut ? Loc.T("已剪切到剪贴板") : Loc.T("已复制到剪贴板"));
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("写入剪贴板失败：") + ex.Message);
        }
    }

    private void CopyText(string text)
    {
        try { Clipboard.SetText(text); _host.Notify(Loc.T("路径已复制")); }
        catch (Exception ex) { _host.Notify(Loc.T("复制失败：") + ex.Message); }
    }

    /// <summary>
    /// 右键菜单里的「属性」。
    ///
    /// 旧写法是 <c>Process.Start(new ProcessStartInfo(path) { Verb = "properties" })</c>：
    /// 那条路走 COM 的 IShellDispatch，Shell 拒绝或目标在库目录 / 网络位置 / 后缀被别的
    /// 程序接管时直接抛异常，用户看到的就是「打开属性失败」（而且什么都不弹）。
    ///
    /// 现在走 ShellExecuteEx + "properties" + SEE_MASK_INVOKEIDLIST —— 和资源管理器右键
    /// 「属性」是同一条系统路径，文件、文件夹、快捷方式、驱动器都认。
    ///
    /// 另外属性对话框是 Shell 的模态窗，ShellExecuteEx 会一直堵到用户关掉它，
    /// 所以放一条 STA 后台线程里开，别把界面线程钉死（否则 Qbar 整个不响应）。
    /// </summary>
    private void ShowProperties(string path)
    {
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var info = new Core.Interop.NativeMethods.SHELLEXECUTEINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Core.Interop.NativeMethods.SHELLEXECUTEINFO>(),
                    fMask = Core.Interop.NativeMethods.SEE_MASK_INVOKEIDLIST,
                    hwnd = IntPtr.Zero,
                    lpVerb = "properties",
                    lpFile = path,
                    nShow = 1,
                };
                if (!Core.Interop.NativeMethods.ShellExecuteExW(ref info))
                {
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Dispatcher.BeginInvoke(new Action(() => _host.Notify(Loc.T("打开属性失败：") + msg)));
            }
        })
        {
            IsBackground = true,
            Name = "InputPlus.Properties"
        };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    private QbarService.QbarCallbacks BuildCallbacks() => new(
        Notify: _host.Notify,
        CopyToClipboard: text =>
        {
            try { System.Windows.Clipboard.SetText(text); } catch { }   /* 预期内：清理动作，失败无副作用 */
        },
        Paste: text => _host.Clipboard.PasteText(text),
        OpenTarget: OpenTarget,
        RunTarget: RunTarget,
        RunCommand: RunCommand,
        RunAction: action => _host.InvokeAction(action, ""));

    private void OpenTarget(string target)
    {
        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            _host.Context.OpenUrl(target);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            // 成功之后才记"最近打开"（网址走上面的 Context.OpenUrl，那里已经记过）
            RecentTargets.Record(target);
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("打开失败：") + ex.Message);
        }
    }

    private void RunTarget(string target, bool asAdmin, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Arguments = args,
            };
            if (asAdmin) psi.Verb = "runas";
            Process.Start(psi);
            RecentTargets.Record(target);
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("启动失败：") + ex.Message);
        }
    }

    private void RunCommand(string command)
    {
        switch (command)
        {
            case "openSettings": _host.ShowMainWindow("settings"); break;
            case "openHelp": _host.ShowMainWindow("help"); break;
            case "openClipboard": _host.ShowClipboardHistory(); break;
            case "openMath": _host.ShowMathBoard(); break;
            case "touch": CreateDesktopFile(); break;
            case "reload": _host.ReloadAll(); break;
            case "toggleEnabled": _host.ToggleEnabled(); break;
            case "exit": _host.ExitApp(); break;
        }
    }

    /// <summary>
    /// 快捷命令 touch：在桌面新建一个空文件。
    /// 只收文件名（去掉任何目录部分），避免把文件写到用户没预期的地方。
    /// </summary>
    private void CreateDesktopFile()
    {
        string? name = TextPromptWindow.Ask(this, Loc.T("创建文件"), Loc.T("文件名"), "新建文件.txt");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = System.IO.Path.GetFileName(name.Trim());
        if (name.Length == 0) return;

        string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        /* 桌面可以被用户重定向到 D:\Desktop 等位置，而 SpecialFolder 仍返回
         * C:\Users\...\Desktop。优先读 shell 的 User Shell Folders，保证
         * “桌面新建文件”真的出现在用户看到的桌面上。 */
        try
        {
            if (Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                    "Desktop", null) is string redirected && redirected.Length > 0)
            {
                string expanded = Environment.ExpandEnvironmentVariables(redirected);
                if (System.IO.Directory.Exists(expanded)) dir = expanded;
            }
        }
        catch { }
        string path = System.IO.Path.Combine(dir, name);
        if (System.IO.File.Exists(path))
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(name);
            string ext = System.IO.Path.GetExtension(name);
            for (int i = 1; i < 1000 && System.IO.File.Exists(path); i++)
                path = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
        }

        try
        {
            System.IO.File.WriteAllText(path, "");
            _host.Notify(Loc.T("已创建：") + path);
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("创建失败：") + ex.Message);
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) != null)
            ExecuteSelected();
    }

    /* 原来这里还有一个 OnResultDoubleClick（XAML MouseDoubleClick 也挂着）：
     * 单击已经会执行并收起面板，它纯属重复，而且不像单击那样判断点没点
     * 中条目 —— 双击列表空白处也会把当前选中项执行一遍。
     * 已删除；Loc.T("双击只执行一次")由 ExecuteSelected 里的 _closing 闸门保证。 */

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private sealed class QbarItem : INotifyPropertyChanged
    {
        private ImageSource? _iconSource;
        private bool _hasIcon;

        public QbarItem(QbarResult result, bool showIcons = true)
        {
            Result = result;
            var chip = result.Chip;

            /* 「显示图标」这个设置项之前**没人读** ——
             * 界面上能勾掉，但列表照样显示图标，属于典型表面实现。
             * 现在关掉时只显示文字，不再画图标。 */
            if (!showIcons)
            {
                // 什么都不画，靠文本区分
            }
            else if (chip is { HasIcon: true })
            {
                SetIcon(chip.Icon);
            }
            else if (chip != null)
            {
                Letter = chip.Initial;
                LetterBrush = new SolidColorBrush(chip.Accent);
                HasLetter = true;
                TryResolveIcon(result);
            }
            else
            {
                TryResolveIcon(result);
            }

            IconGlyph = result.Glyph ?? result.Kind switch
            {
                QbarKind.Search => "\uE721",
                QbarKind.Web => "\uE774",
                QbarKind.Run => "\uE756",
                QbarKind.Window => "\uE737",
                QbarKind.Math => "\uE8EF",
                QbarKind.Dictionary => GlyphDictionary,
                QbarKind.Clipboard => "\uE77F",
                QbarKind.File => "\uE8B7",
                _ => "\uE756",
            };
            /* 建议行的小徽标显示"来源"（下载 / 桌面 / 最近，多来源命中时是
             * "下载·桌面"），这是它和普通搜索结果唯一的视觉差别 ——
             * 用 KindLabel（"文件"）的话，用户看不出这一行是"最近用过"的。 */
            Badge = result.Suggestion.Length > 0 && result.Badge.Length > 0
                ? result.Badge
                : result.Kind == QbarKind.Command ? result.Payload : result.KindLabel;
            TitleSegments = BuildTitleSegments(result);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>标题按命中区间切好的段；XAML 用附加属性把它们铺成 Inlines。</summary>
        public IReadOnlyList<QbarTitleSegment> TitleSegments { get; }

        /// <summary>
        /// 高亮区间来自搜索引擎（名字内的起止偏移），这里只切段不拼字符串：
        /// 在数据层拼 &lt;b&gt; 标记的话，文件名里本来就有尖括号/&amp;&amp; 的会被当成标记解析掉。
        /// </summary>
        private static IReadOnlyList<QbarTitleSegment> BuildTitleSegments(QbarResult result)
            => QbarTitleHighlight.Segments(result.Title, result.Highlights);

        /* 图标格是 28 DIP（QbarWindow.xaml 的 IconTile Width/Height=28），
         * 折算像素档位时按当前 DPI 向上取档，保证不用小图放大。 */
        private const int IconDip = 28;

        /// <summary>
        /// 所有结果类型共用的一条图标路径：**只问 Windows Shell**
        /// （<see cref="ShellIcons"/>），不再按扩展名映射内置图标 ——
        /// PDF 拿到的是系统默认处理器的图标，快捷方式带叠加箭头，
        /// .exe/UWP 应用/未知类型与资源管理器同一份。
        ///
        /// 构造函数里同步只做缓存查询（纯字典，不碰磁盘/COM），
        /// 未命中就保留已有的字形/首字占位，解析完成后再靠 PropertyChanged 换图。
        /// </summary>
        private void TryResolveIcon(QbarResult result)
        {
            double dpi = UiDpiScale();
            int px = ShellIcons.PickPixels(IconDip, dpi);
            List<string> candidates = IconCandidates(result);

            foreach (string candidate in candidates)
            {
                ImageSource? cached = ShellIcons.TryGetCached(candidate, px, dpi);
                if (cached != null)
                {
                    SetIcon(cached);
                    return;
                }
            }

            if (candidates.Count == 0)
            {
                WebFaviconFallback(result);
                return;
            }
            ResolveIconAsync(candidates, 0, px, dpi, result);
        }

        /// <summary>按候选顺序串行解析：前一个失败才往下试，不会出现后面的结果覆盖前面的正确图标。</summary>
        private void ResolveIconAsync(List<string> candidates, int index, int px, double dpi, QbarResult result)
        {
            if (index >= candidates.Count)
            {
                WebFaviconFallback(result);
                return;
            }

            string path = candidates[index];
            ShellIcons.Request(path, px, dpi, image =>
            {
                if (image != null)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => SetIcon(image));
                    return;
                }
                ResolveIconAsync(candidates, index + 1, px, dpi, result);
            });
        }

        private void WebFaviconFallback(QbarResult result)
        {
            if (result.Kind != QbarKind.Web || string.IsNullOrWhiteSpace(result.Payload)) return;

            /* favicon 必须联网，不能卡住 Qbar 的查询/打开。
             * 先给字形，图标到了再通过 PropertyChanged 刷新这一行。 */
            string url = result.Payload;
            IconCache.LoadWebIconAsync(url, image =>
            {
                if (image == null) return;
                Application.Current?.Dispatcher.BeginInvoke(() => SetIcon(image));
            });
        }

        /// <summary>
        /// 一个结果可能有几个候选路径：IconPath（窗口/快捷方式给的 exe 或图标文件）
        /// 与 Payload（File=路径，Run=程序名或路径）。其它类型 Payload 是
        /// 句柄数字/查询词/网址/剪贴板文本，拿去解析只会白扫一遍 PATH。
        /// </summary>
        private static List<string> IconCandidates(QbarResult result)
        {
            var list = new List<string>();
            AddCandidate(list, result.IconPath);

            string payload = ShellIcons.NormalizePath(result.Payload);
            if (payload.Length > 0)
            {
                if (result.Kind == QbarKind.File)
                {
                    AddCandidate(list, payload);
                }
                else if (result.Kind == QbarKind.Run)
                {
                    /* 启动表里很多值只写了 notepad.exe：先在 PATH/当前目录里找实际文件，
                     * 找不到就把原值交出去，Shell 自己也可能解析出关联程序图标。 */
                    AddCandidate(list, ShellIcons.ResolveFilesystemPath(payload) ?? payload);
                }
            }
            return list;
        }

        private static void AddCandidate(List<string> list, string? path)
        {
            string norm = ShellIcons.NormalizePath(path);
            if (norm.Length == 0) return;
            foreach (string existing in list)
            {
                if (string.Equals(existing, norm, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(norm);
        }

        /// <summary>
        /// QbarItem 是纯数据行，拿不到自己的窗口；按当前可见的 Qbar 窗口取 DPI，
        /// 取不到就退回系统 DPI（多屏不同缩放时只是档位偏保守，不会出现放大发虚）。
        /// </summary>
        private static double UiDpiScale()
        {
            try
            {
                WindowCollection? windows = Application.Current?.Windows;
                if (windows != null)
                {
                    foreach (Window window in windows)
                    {
                        if (window is QbarWindow qbar && qbar.IsVisible)
                        {
                            return VisualTreeHelper.GetDpi(qbar).DpiScaleX;
                        }
                    }
                }
            }
            catch { }
            return ShellIcons.SystemDpiScale();
        }

        private void SetIcon(ImageSource? icon)
        {
            if (icon == null) return;
            _iconSource = icon;
            _hasIcon = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
        }

        public QbarResult Result { get; }
        public string IconGlyph { get; }
        public string Badge { get; }
        public ImageSource? IconSource => _iconSource;
        public bool HasIcon => _hasIcon;
        public bool HasLetter { get; }
        public string Letter { get; } = "";
        public Brush LetterBrush { get; } = Brushes.Transparent;
        public string Title => Result.Title;
        public string Subtitle => Result.Subtitle;
    }
}
