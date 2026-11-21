using System.Windows;
using InputPlus.Core;

namespace InputPlus.UI.Windows;

/// <summary>
/// 手势设置窗：把原来摊在手势页上的「开关快捷键 / 手势外观 / 手势参数」收进来。
///
/// 为什么单独一个窗而不是继续塞在页面里：那两块折叠面板展开后比页面还高，
/// 只能靠 <c>OptionsScroll.MaxHeight</c> 硬算着滚，一行「手势列表」被压成一条缝。
/// 挪进窗口后页面按需滚动，设置也能占满整屏。
/// </summary>
public partial class GestureSettingsWindow : Window
{
    public GestureSettingsWindow(AppHost host)
    {
        InitializeComponent();
        Options.Initialize(host);

        /* 置顶是用户偏好（对齐 WGestures 2 的 PrefWindowTopmost）：
         * 对着别的程序调手势时，设置窗被目标窗口盖住就没法边看边调。 */
        Topmost = host.Settings.GestureOptions.PrefWindowTopmost;

        /* 标题栏跟随主题 —— 和其它对话框一样，不挂的话深色主题下顶着一根系统白条。
         * 但这个窗的正文是 Brush.Background 从顶铺到底，标题栏要是按默认的 SurfaceAlt 染，
         * 深色下就是"上面贴着一条明显更亮的横杠"（用户反馈的就是这条）。
         * 所以标题栏直接染成正文底色，整扇窗成一整块。 */
        Theme.WindowChrome.AttachTo(this, "Brush.Background");

        /* 点回主界面就把这个窗收起来（用户反馈：开着它去点软件别处，窗应该自己关）。
         *
         * 只认"主窗口重新拿到前台"这一种情况：
         *   · 点到本窗自己弹出的对话框（选动作 / 取色 / 提示框）上时，前台是那个
         *     对话框的句柄，不是主窗，所以不会误关；
         *   · 点到别的程序上也不关 —— 开着「置顶」正是为了边看边调别的程序，
         *     一碰别的窗口就消失反而没法用。 */
        Deactivated += OnDeactivated;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsVisible) return;
            if (Owner is not { } owner) return;
            IntPtr fg = Core.Interop.NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            if (fg == new System.Windows.Interop.WindowInteropHelper(owner).Handle) Close();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>渲染工具用：两块折叠面板都展开再截图。</summary>
    internal void ExpandAllForRender() => Options.ExpandAllForRender();

    /// <summary>渲染用：滚到设置卡片的最底下，拍参数卡尾部。</summary>
    internal void ScrollOptionsToEndForRender() => Options.ScrollToEndForRender();

    /// <summary>出图用：滚到「手势美化」卡。</summary>
    internal void ScrollOptionsToBeautyForRender() => Options.ScrollToBeautyForRender();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
