using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using InputPlus.Core;
using InputPlus.Core.Input;
using KeyModifiers = InputPlus.Core.Input.KeyModifiers;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.UI.Windows;

/// <summary>
/// 自定义按键捕获窗口（支持组合键）。
///
/// 打开后用户直接按目标组合键，例如 Ctrl+Shift+K、Alt+D。
/// 用全局低级键盘钩子捕获：修饰键按下时实时高亮，
/// 松开非修饰键的那一刻定格组合并等待确认。
///
/// 为什么用钩子而不是 PreviewKeyDown：WPF 键盘事件拿不到
/// 浏览器键、媒体键等扩展键的原始 VK，而这些恰是需要自定义的场景。
/// </summary>
public partial class KeyCaptureWindow : Window
{
    private const int WH_KEYBOARD_LL = 13;

    private IntPtr _hook = IntPtr.Zero;
    private HookProc? _proc;

    /* ── 鼠标键只能点选，不再用钩子录 ──
     *
     * 从 5.9 到 5.10 反复出现「录完组合键点『确定绑定』，绑上去的变成 mouseLeft」：
     * 根子是"用鼠标钩子录鼠标键"这件事本身 —— 要判断"这一下点击算不算用户想绑的键"，
     * 而窗口句柄没建好、钩子坐标滞后、点在窗口圆角外，任何一种失手都会把
     * 「确定绑定」那一下左键录成 mouseLeft，把刚按好的组合键顶掉。
     * 判据加了四轮（按下排队 + 抬起确认 + 三处几何判据 + 活动窗口闸门）仍然只是概率。
     * 现在改成**结构上不可能**：捕获窗不装鼠标钩子，鼠标键只能点下面那排按钮选。 */

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }


    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

    /// <summary>某个虚拟键当前是否处于按下状态。</summary>
    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // 修饰键的虚拟键码
    private const int VK_CTRL_A = 0xA2, VK_CTRL_B = 0xA3;   // 左/右 Ctrl
    private const int VK_SHIFT_A = 0xA0, VK_SHIFT_B = 0xA1;
    private const int VK_ALT_A = 0xA4, VK_ALT_B = 0xA5;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_ESC = 0x1B;

    /// <summary>捕获到的组合键（KeyName 形式，如 "ctrl+shift+k"）；取消时为空。</summary>
    public string? CapturedCombo { get; private set; }

    /// <summary>
    /// 触发键（层前缀键）的虚拟键码集合。
    ///
    /// 为什么需要它：用户日常用的键**全都是"触发键 + 某键"**（默认 CapsLock + J）。
    /// 但 CapsLock 本身不是修饰键，捕获窗口会把它当成"目标键"立刻定格成
    /// capslock —— 于是按 Caps+J 录到的永远是 CapsLock，怎么都绑不到
    /// 那个已有的 J。这正是"自定义按键绑不了已有按键"的根本原因。
    ///
    /// 现在：按到前缀键 ⇒ 只当作"开始录组合"，继续等真正的目标键；
    /// 单独按一下前缀键就松手 ⇒ 仍允许把它自己当目标（绑短按用）。
    /// </summary>
    public HashSet<int> PrefixKeys { get; init; } = new();

    /// <summary>当前按住的触发键（0 = 没有）。</summary>
    private int _prefixVk;

    /// <summary>按住触发键期间是否又按了别的键。</summary>
    private bool _prefixUsed;

    /// <summary>可当前缀用的触发键（已排除修饰键）。</summary>
    private HashSet<int> _prefixEligible = new();

    /// <summary>捕获到的主键名（不含修饰键）。</summary>
    public string? CapturedKey => CapturedCombo?.Contains('+') == true
        ? CapturedCombo[(CapturedCombo.LastIndexOf('+') + 1)..]
        : CapturedCombo;

    /// <summary>捕获到的修饰键。</summary>
    public KeyModifiers CapturedMods { get; private set; }

    /// <summary>是否捕获到了组合键（含修饰键才算组合）。</summary>
    public bool IsCombo => CapturedMods != KeyModifiers.None;

    /// <summary>捕获结果（非模态回调用）。</summary>
    public readonly record struct CaptureResult(string? Combo, string? Key, Core.Input.KeyModifiers Mods);

    /// <summary>
    /// 统一的"录一个键"入口：**非模态**打开，结果通过回调返回。
    ///
    /// 为什么不建议直接 new + ShowDialog：ShowDialog 会阻塞界面线程，
    /// 万一窗口没显示出来（透明窗口 + Owner 不可见时出现过），用户看到的就是
    /// "整个程序卡死、识别按键的页面也不出现"，而且没有任何日志。
    /// </summary>
    public static void Capture(Action<CaptureResult> onCaptured, IEnumerable<int>? prefixKeys = null,
                              string? title = null)
    {
        /* 上一次的窗口如果还活着（可能用户没看见它），先关掉。
         * 不能直接 return —— 那会让"再点一次"永远没反应，正是用户报的现象。 */
        if (_open != null)
        {
            CoreLog.Warn("上一次的捕获窗还在（可能没显示出来），先关掉再开新的");
            try { _open.Close(); } catch { }
            _open = null;
        }

        bool plain = Environment.GetEnvironmentVariable("CLP_CAPTURE_PLAIN") == "1";
        KeyCaptureWindow dlg;
        try
        {
            dlg = Create(plain);
        }
        catch (Exception ex)
        {
            CoreLog.Error($"创建捕获窗失败：{ex.Message}");
            AppDialog.Error(null, Loc.F("打开录键窗口失败：{0}", ex.Message), Loc.T("自定义按键"));
            return;
        }

        KeyCaptureWindow Create(bool usePlain)
        {
            var w = new KeyCaptureWindow(usePlain)
            {
                PrefixKeys = prefixKeys == null ? new HashSet<int>() : new HashSet<int>(prefixKeys),
            };
            if (!string.IsNullOrWhiteSpace(title)) w.Title = title!;
            return w;
        }

        void Wire(KeyCaptureWindow w)
        {
            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(_open, w)) _open = null;
                /* 空结果也记一条：用户报"点确定绑定，值变成 X"时，
                 * 日志里必须能看出这一次到底录到了什么、还是根本没录到。 */
                if (string.IsNullOrWhiteSpace(w.CapturedCombo))
                    CoreLog.Info("捕获窗关闭：没有录到任何键，本次不改动");
                else
                    CoreLog.Info($"捕获窗关闭：结果 [{w.CapturedCombo}]");
                try { onCaptured(new CaptureResult(w.CapturedCombo, w.CapturedKey, w.CapturedMods)); }
                catch (Exception ex) { CoreLog.Swallow(ex, "捕获键回调"); }
            };
        }

        try
        {
            Wire(dlg);
            CoreLog.Info($"打开键位捕获窗（非模态，plain={plain}）");
            dlg.Show();
            dlg.Activate();
            _open = dlg;
        }
        catch (Exception ex)
        {
            CoreLog.Error($"显示捕获窗失败：{ex.Message}");
            AppDialog.Error(null, Loc.F("打开录键窗口失败：{0}", ex.Message), Loc.T("自定义按键"));
            return;
        }

        /* 400ms 后确认它真的显示出来了。没显示就换朴素窗口重开一次 ——
         * 分层窗口在个别机器上"窗口在、内容不画"，用户看到的就是"没反应"。 */
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        check.Tick += (_, _) =>
        {
            check.Stop();

            /* 只看 IsVisible —— 尺寸在 SizeToContent 下可能还没算好（曾经因为尺寸为 0
             * 误判"没显示出来"，把刚打开的窗口关掉重开，用户会觉得窗口一闪就没了）。 */
            bool ok = dlg.IsVisible;
            CoreLog.Info($"捕获窗属性：SizeToContent={dlg.SizeToContent} Width={dlg.Width} Height={dlg.Height} " +
                         $"WindowStyle={dlg.WindowStyle} 透明={dlg.AllowsTransparency}");
            CoreLog.Info($"捕获窗状态：IsVisible={dlg.IsVisible} " +
                         $"尺寸={dlg.ActualWidth:F0}x{dlg.ActualHeight:F0} 位置=({dlg.Left:F0},{dlg.Top:F0}) " +
                         $"焦点={dlg.IsKeyboardFocusWithinForTest()}");
            if (ok) return;

            CoreLog.Warn("捕获窗没显示出来，改用朴素窗口重开");
            try { dlg.Close(); } catch { }
            try
            {
                var plainWin = Create(true);
                Wire(plainWin);
                plainWin.Show();
                plainWin.Activate();
                _open = plainWin;
            }
            catch (Exception ex)
            {
                CoreLog.Error($"朴素捕获窗也失败：{ex.Message}");
                AppDialog.Error(null,
                    Loc.T("录键窗口打不开。请把日志里「捕获窗状态」那几行发给我们。"),
                    Loc.T("自定义按键"));
            }
        };
        check.Start();
    }

    /// <summary>诊断用：窗口有没有拿到键盘焦点。</summary>
    internal bool IsKeyboardFocusWithinForTest() => IsKeyboardFocusWithin;

    /// <summary>当前打开的捕获窗（用于处理"上一次没关掉"的情况）。</summary>
    private static KeyCaptureWindow? _open;

    /// <summary>
    /// <paramref name="plain"/>=true 时用不透明窗口。
    ///
    /// 为什么需要：这个窗口是 `AllowsTransparency=True` 的分层窗口 —— 观感好，
    /// 但在个别显卡/驱动组合上会"窗口在、内容不画"，用户看到的就是"点了没反应、
    /// 录制页面不出现"。所以自动兜底：透明窗没显示出来就换成朴素窗口重开一次。
    /// </summary>
    public KeyCaptureWindow(bool plain = false)
    {
        InitializeComponent();

        if (plain)
        {
            AllowsTransparency = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.NoResize;
            Background = (Brush)FindResource("Brush.Surface");
            CoreLog.Info("捕获窗改用朴素窗口（不透明）");
        }
        _proc = HookCallback;

        /* 第三条来源：WPF 自己的预览事件。
         *
         * 窗口是模态 + Topmost + 打开即激活，只要它还拿着键盘焦点，
         * 每个按键都会先走 PreviewKeyDown —— 这条路**不依赖任何全局钩子**，
         * 所以钩子被系统摘掉时它照样能用。 */
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;

        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Keyboard.Focus(this); };

        Loaded += (_, _) =>
        {
            /* PrefixKeys 是 init 属性，对象初始化器在构造函数**之后**才赋值，
             * 所以不能写在构造函数里 —— 到这里才读得到。
             * 修饰键要排除：右 Alt 之类被设成触发键时仍按修饰键语义处理。 */
            _prefixEligible = PrefixKeys.Where(v => v != 0 && !IsModifier(v)).ToHashSet();

            /* 鼠标键的按钮要等窗口资源就绪才能建（Button.Ghost 样式从资源里取）。 */
            BuildMouseKeyButtons();

            /* 第二条来源：Raw Input（不受钩子超时影响）。
             * 订阅之后即使钩子死了，按键也照样进来。 */
            KeyCaptureSink.Current = (vk, down) =>
            {
                RawHits++;
                if (Environment.GetEnvironmentVariable("CLP_CAPTURE_LOG") == "1")
                    CoreLog.Info($"[捕获] raw vk=0x{vk:X2} down={down}");
                Dispatcher.BeginInvoke(() => HandleKey(vk, down));
            };

            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

            if (_hook == IntPtr.Zero)
            {
                /* 不再把这当成"功能不可用"：钩子只是三路来源之一，
                 * 另外两路（Raw Input / WPF 预览）足够覆盖普通按键。
                 * 提示要如实说清"现在还能用"，否则用户会以为坏了。 */
                PromptText.Text = Loc.T("等待按键…");
                DetailText.Text = Loc.T("全局钩子没装上（可能被安全软件拦截），已改用窗口内捕获——功能正常。");
                CoreLog.Warn($"自定义按键捕获：钩子安装失败（错误码 {Marshal.GetLastWin32Error()}），改用 Raw Input + 窗口内捕获");
            }
            Activate();
            Focus();
            Keyboard.Focus(this);

            // 用户可能是"已经按着 Ctrl 才点开对话框"，那几个 down 事件我们没收到，
            // 先播种一次初始状态，之后完全靠事件维护。
            SeedModifiers();
            UpdateModifierBadges();

            /* 3 秒还没收到任何按键：把"三路来源各自收到几次"写进日志，并给用户一句人话。
             * 这条日志是下次排查的抓手 —— 哪一路是 0，问题就在哪一路。 */
            _noInputTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _noInputTimer.Tick += (_, _) =>
            {
                _noInputTimer!.Stop();
                if (HookHits + RawHits + WpfHits > 0) return;
                CoreLog.Warn($"录键 3 秒没收到任何按键：钩子={HookHits} RawInput={RawHits} 窗口事件={WpfHits} " +
                             $"（管理员={Core.ElevationHelper.IsElevated}）");
                DetailText.Text = Loc.T("还没收到按键。可以再按一次；如果一直没反应，把日志发给我们（日志里记了三路来源的计数）。");
            };
            _noInputTimer.Start();
        };
        Closed += (_, _) =>
        {
            _noInputTimer?.Stop();
            KeyCaptureSink.Current = null;
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        };
    }

    /* ---------------- 三路输入汇合到这里 ---------------- */

    /// <summary>上一次处理的按键，用来给多路来源去重（同一个物理按键会被钩子 + Raw Input + WPF 各送一次）。</summary>
    /// <summary>
    /// 非模态打开时（Recommend：不用 ShowDialog），<c>DialogResult</c> 一设就抛
    /// InvalidOperationException。这里统一兜住 —— 调用方看 CapturedCombo 就够了。
    /// </summary>
    private void TrySetResult(bool value)
    {
        try { DialogResult = value; } catch (InvalidOperationException) { /* 非模态 */ }
    }

    private DispatcherTimer? _noInputTimer;

    private int _lastVk;
    private bool _lastDown;
    private long _lastTick;
    private bool _closed;

    /// <summary>
    /// 一帧内同一个 (vk, 方向) 的重复事件直接丢掉。
    ///
    /// 阈值取 40ms：真实自动重复最快约 30ms 一次，而三路来源之间相差不到 5ms，
    /// 所以既能吃掉重复，又不会把用户的第二次按键当成重复。
    /// </summary>
    private bool Duplicate(int vk, bool down)
    {
        long now = Environment.TickCount64;
        if (vk == _lastVk && down == _lastDown && now - _lastTick < 40) return true;
        _lastVk = vk;
        _lastDown = down;
        _lastTick = now;
        return false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        WpfHits++;
        if (Environment.GetEnvironmentVariable("CLP_CAPTURE_LOG") == "1")
            CoreLog.Info($"[捕获] wpf key={e.Key}");
        e.Handled = true;
        // Alt 在 WPF 里走 Key.System，真正的键在 SystemKey 上
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        if (e.IsRepeat) return;
        HandleKey(vk, true);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        WpfHits++;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        HandleKey(vk, false);
    }

    /// <summary>
    /// 修饰键跟踪器。
    ///
    /// 不用 <see cref="GetAsyncKeyState"/> 判断修饰键 —— 低级钩子回调里
    /// 异步按键状态还没更新，查到的永远是上一个键的状态，
    /// 表现就是"能识别按键但录不到组合键"。
    /// 这里完全靠钩子的 down/up 事件维护。
    /// </summary>
    private readonly ModifierTracker _tracker = new();

    /// <summary>当前按住的修饰键（从钩子事件推导）。</summary>
    private KeyModifiers ReadModifiers() => _tracker.Current;

    private void TrackModifier(int vk, bool down) => _tracker.Track(vk, down);

    private static bool IsModifier(int vk) => ModifierTracker.IsModifier(vk);

    /// <summary>
    /// 窗口刚打开时补一次初始状态：用户可能**已经按着** Ctrl 才点开对话框，
    /// 那几个键的 down 事件我们没收到。这里用 GetAsyncKeyState 做一次性播种，
    /// 之后就完全靠钩子事件维护（那时它反而不可靠）。
    /// </summary>
    private void SeedModifiers() => _tracker.Seed(IsKeyDown);

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            HookHits++;
            if (Environment.GetEnvironmentVariable("CLP_CAPTURE_LOG") == "1")
                CoreLog.Info("[捕获] hook 事件");
        }
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool down = msg is 0x0100 or 0x0104;      // WM_KEYDOWN / WM_SYSKEYDOWN
        bool up = msg is 0x0101 or 0x0105;        // WM_KEYUP / WM_SYSKEYUP
        if (!down && !up) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        // 吞掉按键：捕获期间这些键不该漏给前台（Alt+Tab / Win 之类会当场切走窗口）
        return HandleKey((int)data.vkCode, down)
            ? (IntPtr)1
            : CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /* ---- 三路来源计数器（--capture-test 诊断用） ---- */

    /// <summary>低级钩子收到的事件数。</summary>
    internal int HookHits { get; set; }

    /* ── 鼠标键：只能点选，绝不由钩子录 ──
     *
     * 以前这里是一条 WH_MOUSE_LL 钩子 + 四层判据，试图分辨"用户想绑鼠标键"
     * 和"用户点了确定绑定"，反复失败（失败的样子就是组合键被顶成 mouseLeft）。
     * 现在这条路整个删掉：鼠标键是五个按钮，点一下就等于按了一下那颗键。
     * 键本身仍然能绑（发送鼠标键、绑鼠标键都能用），只是不再从钩子里猜。 */

    /// <summary>
    /// 这个 vk 是不是鼠标按键（0x01~0x06 = 左/右/中/侧 1/侧 2）。
    ///
    /// 键盘永远发不出这几个 vk，所以"收到就是鼠标"。录键窗用它把
    /// 管道里混进来的鼠标事件挡掉（原因见 <see cref="HandleKey"/> 里的注释）。
    /// </summary>
    internal static bool IsMouseVk(int vk) => vk is >= 0x01 and <= 0x06;

    /// <summary>捕获窗里可以直接点选的鼠标键（顺序即按钮顺序）。</summary>
    internal static readonly string[] MouseKeyChoices =
    {
        "mouseLeft", "mouseRight", "mouseMiddle", "mouseX1", "mouseX2",
    };

    private void OnPickMouseKey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name) PickMouseKey(name);
    }

    /// <summary>
    /// 把鼠标键写成捕获结果（按钮和自检共用这一条路）。
    ///
    /// 鼠标键不能和 Ctrl/Shift 组合 —— 系统本身也不会产生"Ctrl+左键"这种键，
    /// 所以这里把修饰键清零，免得界面上留着高亮的 Ctrl 却和结果不符。
    /// </summary>
    internal void PickMouseKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (Array.IndexOf(MouseKeyChoices, name) < 0) return;
        CapturedMods = KeyModifiers.None;
        CapturedCombo = name;
        PromptText.Text = KeyName.Display(name);
        DetailText.Text = Loc.T("鼠标键单独使用，不能和 Ctrl / Shift 组合　·　按「确定绑定」生效");
        ConfirmButton.IsEnabled = true;
        UpdateModifierBadges();
        CoreLog.Info($"[捕获] 已选鼠标键 {name}");
    }

    /// <summary>按 <see cref="MouseKeyChoices"/> 生成那排按钮（样式要窗口资源，所以放在 Loaded 里）。</summary>
    private void BuildMouseKeyButtons()
    {
        MouseKeyRow.Children.Clear();
        foreach (string name in MouseKeyChoices)
        {
            var b = new Button
            {
                Content = KeyName.Display(name),
                Style = (Style)FindResource("Button.Ghost"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Tag = name,
                ToolTip = Loc.T("点这里直接把鼠标键当成要发送的键（不用去按鼠标）"),
            };
            b.Click += OnPickMouseKey;
            MouseKeyRow.Children.Add(b);
        }
    }
    /// <summary>Raw Input 送进来的事件数。</summary>
    internal int RawHits { get; private set; }

    /// <summary>窗口内 PreviewKey 事件数。</summary>
    internal int WpfHits { get; private set; }

    /// <summary>
    /// 三路输入（低级钩子 / Raw Input / WPF 预览）汇合后的唯一处理点。
    /// 返回值 = 是否吞掉这个按键（只有钩子那一路用得到）。
    /// </summary>
    private bool HandleKey(int vk, bool down)
    {
        if (_closed || vk == 0) return false;

        /* ── 鼠标键不当成"要绑的键" ──
         *
         * 0x01~0x06 是鼠标左/右/中/侧 1/侧 2。它们会**主动送进来**：
         * 录键期间钩子宿主进捕获模式，引擎的 KeySeen 对所有 vk 都上报
         * （含 0x01~0x06），主程序再把 KEY 消息转进这里。
         * 于是用户按「确定绑定」的那一下左键，就在确认之前 100ms 作为
         * vk=0x01 到达，把刚录好的 Ctrl+Alt+Z 顶成 mouseLeft ——
         * 这就是"录完组合键点确定，绑上去变成鼠标左键"的真正来源。
         *
         * 鼠标键只能点下面那排按钮选（PickMouseKey），这里一律不认。 */
        if (IsMouseVk(vk))
        {
            CoreLog.Info($"[捕获] 忽略鼠标键事件 vk=0x{vk:X2}（要绑鼠标键请点下面的按钮）");
            return false;
        }

        if (Duplicate(vk, down)) return true;

        // Esc 取消
        if (down && vk == VK_ESC)
        {
            _closed = true;
            Dispatcher.BeginInvoke(() =>
            {
                CapturedCombo = null;
                TrySetResult(false);
                Close();
            });
            return true;
        }

        if (IsModifier(vk))
        {
            // 修饰键：先把按住状态记下来（这是唯一可靠的来源），再刷新高亮。
            // **抬起**时如果还没定案，就把这个修饰键本身作为目标
            // （方便单独绑定 Ctrl/Shift/Alt/Win）。
            TrackModifier(vk, down);

            if (down)
            {
                Dispatcher.BeginInvoke(UpdateModifierBadges);
            }
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateModifierBadges();
                    if (string.IsNullOrEmpty(CapturedCombo))
                    {
                        // 只按了一个修饰键就松手：允许把它当目标键
                        string? modName = KeyName.NameOf(vk);
                        if (modName != null)
                        {
                            CapturedMods = KeyModifiers.None;
                            CapturedCombo = modName;
                            PromptText.Text = KeyName.Display(modName);
                            DetailText.Text = Loc.T("单独绑定这个修饰键　·　按「确定绑定」生效");
                            ConfirmButton.IsEnabled = true;
                            CoreLog.Info($"[捕获] 已录入 {modName}（来源=键盘·单个修饰键）");
                        }
                    }
                });
            }
            return true;
        }

        /* 触发键（层前缀）必须在这里拦下来。
         *
         * 默认触发键是 CapsLock —— 它**不是修饰键**，如果不拦，
         * 流程会直接落到下面"非修饰键按下 = 定格组合"，把用户按下的
         * 前缀键录成目标键本身。于是「Caps + J」永远录成 capslock，
         * 用户想改的那个已有按键（J）怎么都选不中 —— 这就是
         * Loc.T("自定义按键绑不了已有按键")的真正原因。
         *
         * 只对**非修饰键**做前缀处理：右 Alt 这类既可能被设成触发键、
         * 本身又是修饰键的键，仍走原来的修饰键语义，
         * 否则"按住右 Alt 再按 K"会被误判成普通按键。
         */
        if (_prefixEligible.Contains(vk))
        {
            if (down)
            {
                _prefixVk = vk;
                _prefixUsed = false;
                string? pName = KeyName.NameOf(vk);
                Dispatcher.BeginInvoke(() =>
                {
                    PromptText.Text = $"{KeyName.Display(pName ?? "")} + ?";
                    DetailText.Text = Loc.T("触发键只作组合前缀，不会占用它　·　再按一个键");
                });
            }
            else
            {
                if (!_prefixUsed && _prefixVk == vk)
                {
                    // 单独按了一下触发键就松手：允许把它自己当目标（改短按行为用）
                    string? modName = KeyName.NameOf(vk);
                    if (modName != null)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            CapturedMods = KeyModifiers.None;
                            CapturedCombo = modName;
                            PromptText.Text = KeyName.Display(modName);
                            DetailText.Text = Loc.T("单独绑定这个触发键　·　按「确定绑定」生效");
                            ConfirmButton.IsEnabled = true;
                            CoreLog.Info($"[捕获] 已录入 {modName}（来源=键盘·单个触发键）");
                        });
                    }
                }
                _prefixVk = 0;
                _prefixUsed = false;
            }
            return true;
        }

        if (down)
        {
            // 有前缀键按着 → 这个键才是真正的目标键
            if (_prefixVk != 0) _prefixUsed = true;

            /* 非修饰键按下 = 定格组合。
             *
             * 未知键不再拒绝：给它一个 `vk0xXX` 的兜底名字。
             * 键名表是白名单，输入法键 / 厂商自定义键根本不在表里，
             * 以前用户按下去只看到"暂时不能绑定"，等于功能缺失；
             * 而 KeyName.VkOf 早就支持这个格式（宏录制一直在用）。 */
            string? known = KeyName.NameOf(vk);
            string name = known ?? KeyName.GeneratedName(vk);
            var mods = ReadModifiers();
            string combo = BuildCombo(mods, name);

            Dispatcher.BeginInvoke(() =>
            {
                CapturedMods = mods;
                CapturedCombo = combo;

                PromptText.Text = combo;
                DetailText.Text = known != null
                    ? (mods == KeyModifiers.None
                        ? Loc.F("单键 {0}　·　按「确定绑定」生效", name)
                        : Loc.F("组合键 {0}　·　按「确定绑定」生效", combo))
                    : Loc.F("未收录的键（VK 0x{0:X2}），按 {1} 记录　·　按「确定绑定」生效", vk, name);
                ConfirmButton.IsEnabled = true;
                /* 常开日志：录到什么都不用猜了。
                 * 用户报"点确定绑定就变成 mouseLeft"时，靠这几条对账。 */
                CoreLog.Info($"[捕获] 已录入 {combo}（来源=键盘 vk=0x{vk:X2}）");
            });

            return true;
        }

        // 非修饰键抬起：不吞（否则某些程序收不到 up，会"粘键"）
        return false;
    }

    private static string BuildCombo(KeyModifiers mods, string? key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("alt");
        if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");
        parts.Add(key ?? "?");
        return string.Join("+", parts);
    }

    private void UpdateModifierBadges()
    {
        SetBadge(CtrlBadge, ReadModifiers().HasFlag(KeyModifiers.Ctrl));
        SetBadge(ShiftBadge, ReadModifiers().HasFlag(KeyModifiers.Shift));
        SetBadge(AltBadge, ReadModifiers().HasFlag(KeyModifiers.Alt));
        SetBadge(WinBadge, ReadModifiers().HasFlag(KeyModifiers.Win));
    }

    private static void SetBadge(Border badge, bool on)
    {
        if (on)
        {
            badge.Background = (Brush)Application.Current.FindResource("Brush.AccentSoft");
            badge.BorderBrush = (Brush)Application.Current.FindResource("Brush.Accent");
            if (badge.Child is TextBlock tb)
                tb.Foreground = (Brush)Application.Current.FindResource("Brush.Accent");
        }
        else
        {
            badge.Background = (Brush)Application.Current.FindResource("Brush.Surface");
            badge.BorderBrush = (Brush)Application.Current.FindResource("Brush.Border");
            if (badge.Child is TextBlock tb)
                tb.Foreground = (Brush)Application.Current.FindResource("Brush.TextTertiary");
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CapturedCombo))
        {
            PromptText.Text = Loc.T("先按一个组合键");
            return;
        }
        /* 确认那一刻的值就是最终值 —— 用户报"点了确定绑定，绑上去的变成别的"，
         * 这条日志是唯一能对账的记录（谁在确认之后再改，就一目了然）。 */
        CoreLog.Info($"[捕获] 确定绑定：[{CapturedCombo}]");
        TrySetResult(true);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CapturedCombo = null;
        TrySetResult(false);
        Close();
    }
}
