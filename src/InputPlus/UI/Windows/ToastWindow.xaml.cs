using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace InputPlus.UI.Windows;

/// <summary>
/// 轻提示：屏幕右下角浮出一条消息，短暂停留后自动淡出。
///
/// 两种用途：
///   * 普通提示（"已复制到剪贴板"）
///   * 动作反馈（"音量 65%"）—— 用来确认组合键有没有被识别
///
/// ⚠ 这里的核心约束是「跟上按键速度」，所以有两条和普通窗口不一样的设计：
///
///   ① **单例复用**。早先是每条消息 new 一个 Window，再排队逐条显示 ——
///      创建窗口 + 解析 XAML + Show + UpdateLayout 大约 30~80ms，
///      连按十下就在界面线程堆十条，后面的提示要等前面的播完才轮到，
///      最后显示的其实是十几秒前的旧消息。现在只有一条窗口，
///      内容直接替换、计时器重置，不存在"排队等播完"。
///
///   ② **只保留最新一条**。调用方在钩子/动作线程上，消息统一甩到界面线程。
///      如果界面线程正忙，旧写法会让每条消息各排一次 BeginInvoke，
///      忙完一口气全执行（前面的又盖后面的，白干）。现在用一个槽位
///      存"最新待显示消息"，只排一次 dispatch，执行时取走槽里最新的那条 ——
///      中间过时的消息自动被丢弃，这正是"新的立刻覆盖旧的"。
/// </summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _instance;
    private static DispatcherTimer? _timer;

    /// <summary>待显示消息槽：永远只放最新的一条。</summary>
    private static readonly ToastQueue Slot = new();

    /// <summary>当前这条是不是「动作反馈」（字号小一号、停留更短）。</summary>
    private bool _isFeedback;

    public ToastWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 句柄一建好就设成真的点击穿透（XAML 里的 IsHitTestVisible 只管 WPF 内部）。
    ///
    /// 提示条是 Topmost 压在右下角的：不设 WS_EX_TRANSPARENT，它在的那一两秒里
    /// 那个位置就点不动 —— 用户点任务栏/状态栏没反应，只会以为程序卡了。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Core.Windows.OverlayWindowStyle.Apply(this, nameof(ToastWindow));
    }

    /// <summary>仅供自检：当前提示条窗口句柄（没显示时为 Zero）。</summary>
    internal static IntPtr HandleForTest =>
        _instance != null ? new System.Windows.Interop.WindowInteropHelper(_instance).Handle : IntPtr.Zero;

    public static void Show(string message) => Show(message, "info");

    public static void Show(string message, string kind)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (Application.Current?.Dispatcher is not { } dispatcher) return;

        // 统一调度到界面线程：调用方可能来自钩子线程或动作线程
        if (dispatcher.CheckAccess())
        {
            ShowCore(message, kind);
            return;
        }

        if (!Slot.Post(message, kind)) return;   // 已经排了一个，它会取走最新值

        /* Send 是最高优先级：不排在渲染/布局任务后面。
         * 连按时这一条会插到队首，把上一条还没显示的消息顶掉。 */
        dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Flush));
    }

    /// <summary>界面线程上取走槽里最新的消息并显示。</summary>
    private static void Flush()
    {
        if (Slot.Take() is { } it) ShowCore(it.Text, it.Kind);
    }

    private static void ShowCore(string message, string kind)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (!dispatcher.CheckAccess()) { Show(message, kind); return; }

        var w = _instance ??= new ToastWindow();
        Apply(w, message, kind);

        bool wasVisible = w.IsVisible;
        if (!wasVisible) w.Show();

        w.RefreshToast(wasVisible);
    }

    /// <summary>
    /// 套样式：默认回到 v1.6.0 之前、也是 v1.0~v1.2 用的旧观感
    /// （主题卡片 + 22px 图标 + 13px 文字）。
    ///
    /// 「设置 → 外观 → 提示条」里的可调项仍然生效，但只覆盖用户真调过的部分：
    ///   · Color 为空 → 不设底色，继续用 XAML 的 Brush.OverlayCardAlt；
    ///   · ShowBorder 关闭 → 不设边框，继续用主题自带的 1px 发丝边；
    ///   · ShowBorder 打开 → 才按 BorderWidth × 2 画那圈白边。
    /// 这样默认值不必在代码里再抄一遍颜色，深浅色主题也能跟着走。
    /// </summary>
    private static void Apply(ToastWindow w, string message, string kind)
    {
        w.Message.Text = message;
        w._isFeedback = kind == "feedback";

        var style = Core.Settings.ToastStyles.Current;

        /* 底色：空串 = 旧观感的「跟随主题」；非空才是用户在设置里挑的 RGBA。
         * 解析失败也退回当前主题卡色，避免写坏配置后提示条变成一块洋红。 */
        if (string.IsNullOrWhiteSpace(style.Color))
        {
            w.Card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
                "Brush.OverlayCardAlt");
        }
        else
        {
            var fallback = w.TryFindResource("Brush.OverlayCardAlt") is SolidColorBrush theme
                ? theme.Color
                : Color.FromArgb(0xF2, 0x16, 0x1A, 0x23);
            w.Card.Background = new SolidColorBrush(
                Core.Gestures.Wg2Color.Parse(style.Color, fallback));
        }

        // 圆角回到主题卡片的 12（Overlay.CardAlt），不再写死 v1.6.3 的 14。
        w.Card.ClearValue(System.Windows.Controls.Border.CornerRadiusProperty);

        /* 可调白边默认关，关着时把边框交还给样式（主题发丝边）；
         * 打开后才覆盖。粗细 ×2 的口径与手势名称框一致。 */
        if (style.ShowBorder)
        {
            double border = Math.Max(1.0, style.BorderWidth) * 2;
            w.Card.BorderThickness = new Thickness(border);
            w.Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            w.Card.ClearValue(System.Windows.Controls.Border.BorderThicknessProperty);
            w.Card.ClearValue(System.Windows.Controls.Border.BorderBrushProperty);
        }

        if (w._isFeedback)
        {
            // 反馈样式：小一号、更紧凑，并且不占图标的位置 —— 连按音量键时不挡视线
            w.Card.Padding = new Thickness(12, 8, 14, 8);
            w.Message.FontSize = 12.5;
            w.Message.Opacity = 0.92;
            w.IconBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 旧观感：22px 图标 + 13px 文字 + 16,11 内边距
            w.Card.Padding = new Thickness(16, 11, 16, 11);
            w.Message.FontSize = 13;
            w.Message.Opacity = 1;
            w.IconBox.Visibility = Visibility.Visible;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Position();

    /// <summary>
    /// 显示/更新一条提示：重新量尺寸、贴右下角、重放淡入、重置计时器。
    /// 复用时只有这一段在跑，省掉了建窗口与 XAML 解析。
    /// </summary>
    private void RefreshToast(bool alreadyVisible)
    {
        // 文本变长/变短都会改变窗口宽度，必须重新量一次再定位
        UpdateLayout();
        Position();

        // 清掉进行中的淡出动画，否则它会把刚显示的窗口又淡掉
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        /* 已经显示时从 0.55 快速拉回 1：给一个"这条是新的"的视觉脉冲，
         * 又不至于整块闪一下。首次显示从 0 淡入。 */
        var fadeIn = new DoubleAnimation(alreadyVisible ? 0.55 : 0, 1,
            TimeSpan.FromMilliseconds(alreadyVisible ? 80 : 120));
        BeginAnimation(OpacityProperty, fadeIn);

        StartHoldTimer();
    }

    private void StartHoldTimer()
    {
        // 反馈类提示停留短一些，普通提示长一些（别再靠内边距猜，见 _isFeedback）
        int holdMs = _isFeedback ? 900 : 1800;

        /* 复用一个计时器。
         *
         * 早先每次提示都 new 一个 DispatcherTimer 再挂 Tick ——
         * 连按音量键时每秒能弹好几条，等于每秒造几个计时器对象，
         * 每个都要注册进 Dispatcher 的计时器表再摘掉，纯属浪费。
         * 计时器只跟"当前这条提示"有关，重设 Interval 就够了。 */
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            _timer.Tick += (_, _) =>
            {
                _timer!.Stop();
                FadeOutAndHide();
            };
        }

        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(holdMs);
        _timer.Start();
    }

    private void FadeOutAndHide()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
        fadeOut.Completed += (_, _) =>
        {
            // 只隐藏不关闭：下次直接改内容复用，避免重建窗口的开销
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void Position()
    {
        // 内容测量完再定位，避免第一次显示时尺寸还是 0
        UpdateLayout();

        /* 旧观感固定贴工作区右下角 10px，落位跟随前台窗口所在显示器
         * （旧实现用 SystemParameters.WorkArea：只认主屏，且是系统 DPI 的 DIP，
         * 副屏/混缩放下会错位到屏幕外）。
         *
         * XAML 里 Card 的 Margin=14 是卡片自身的透明呼吸空间，也把窗口尺寸撑开；
         * 它和这里的屏幕边距是两回事，别再拿它当边距用。 */
        var fg = Core.Interop.NativeMethods.GetForegroundWindow();
        if (Core.Windows.ScreenPlacement.Place(this, Core.Windows.ScreenPlacement.Anchor.BottomRight,
                marginX: 10, marginY: 10, target: fg))
            return;

        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 10;
        Top = wa.Bottom - ActualHeight - 10;
    }
}
