using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using InputPlus.Core;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.UI.Theme;

/// <summary>给无边框窗口加上 Win11 的云母 / 圆角 / 深色标题栏效果。</summary>
public static class WindowChrome
{

    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR_ATTR = 34;
    private const int DWMWA_VISIBLE_FRAME_BORDER_THICKNESS = 37;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    /// <summary>DWMWA_COLOR_NONE：不要系统边框。</summary>
    private const int ColorNone = unchecked((int)0xFFFFFFFE);

    /* Win11 起可以单独指定标题栏 / 边框 / 标题文字的颜色。
     * 不指定的话，深色模式是**纯黑**、浅色模式是**纯白** —— 跟应用里
     * #1A1E27 / #F5F8FC 这样的皮肤颜色放在一起就是"一条突兀的横杠"。 */
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    public enum BackdropType
    {
        Auto = 0,
        None = 1,
        Mica = 2,
        Acrylic = 3,
        Tabbed = 4,
    }

    public static void Apply(Window window, bool dark, BackdropType backdrop = BackdropType.Mica, bool zeroFrame = true,
        string captionBrushKey = "Brush.SurfaceAlt")
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int darkValue = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));

            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            /* 云母背景要求非客户区由系统绘制；主窗口以前在 OnSourceInitialized 里
             * 把它关掉了，两处互相打架，边框就按系统默认（浅色）画。 */
            int nc = 0; // DWMNCRP_ENABLED
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref nc, sizeof(int));

            int type = (int)backdrop;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));

            /* 标题栏颜色跟皮肤走：默认用「比正文稍亮/稍暗一档」的 SurfaceAlt，
             * 既不纯黑也不纯白，和对话框正文是同一条色阶。
             * 页面底色是 Background 的窗口（手势设置窗）要传 "Brush.Background"：
             * 那种窗口正文是从头铺到底的，标题栏比正文亮一档就成了"顶上贴了一条"。 */
            if (ThemeColor(captionBrushKey) is int caption)
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            /* 边框：整圈外壳都是自绘的，系统那 1px 不要。
             *
             * 以前这里是把边框染成 Brush.Border（深色 #2A3040）。问题是
             * ① 它在深色界面外面就是一条**浅灰的线**，看着像个白框；
             * ② 系统在最大化/还原、DPI 变化、从托盘唤回之后会把边框色重新按
             *    默认值画一遍 —— 深色模式下默认就是浅色，于是"白框"时有时无。
             * 现在直接设成"无"，并且跟着窗口状态重新设一次（见 AttachBorderless）。 */
            int none = ColorNone;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR_ATTR, ref none, sizeof(int));
            /* 只有主窗口（外壳全自绘）才把系统那圈 1px 的可见边框压成 0。
             *
             * 普通对话框必须留着它：Win11 的圆角就画在这圈边框上，压成 0 之后
             * 窗口是**直角**的 —— 手势设置窗看起来跟系统里其它窗口对不上，就是
             * 这么来的（v1.6.0 起 AttachTo 传 zeroFrame: false 修掉）。 */
            if (zeroFrame)
            {
                int frame = 0;
                DwmSetWindowAttribute(hwnd, DWMWA_VISIBLE_FRAME_BORDER_THICKNESS, ref frame, sizeof(int));
            }
            if (ThemeColor("Brush.TextPrimary") is int text)
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
        catch (Exception ex)
        {
            CoreLog.Warn("设置窗口外观失败：" + ex.Message);
        }
    }

    /// <summary>当前是不是深色主题（跟系统走时按系统实际明暗算）。</summary>
    public static bool IsDarkTheme()
        => ThemeManager.Current == ThemeManager.Mode.Dark
           || (ThemeManager.Current == ThemeManager.Mode.System && ThemeManager.IsDarkSystem);

    /// <summary>
    /// 让一个普通窗口（对话框）的标题栏跟随主题。
    ///
    /// 只做两件事：HWND 建好后调一次 <see cref="Apply"/>；主题切换时再调一次。
    /// 用 AttachTo 而不是让每个对话框自己写 —— 以前只有 AppDialog 做了，
    /// 其它弹窗在深色界面里顶着一根白条，非常突兀。
    /// </summary>
    /// <param name="captionBrushKey">
    /// 标题栏取哪个主题色。默认 <c>Brush.SurfaceAlt</c>（比正文亮/暗一档）；
    /// 正文本身就铺成 <c>Brush.Background</c> 的窗口要传 <c>Brush.Background</c>，
    /// 否则顶上那条和窗口底色对不上。
    /// </param>
    public static void AttachTo(Window window, string captionBrushKey = "Brush.SurfaceAlt")
    {
        try
        {
            window.SourceInitialized += (_, _)
                => Apply(window, IsDarkTheme(), BackdropType.None, zeroFrame: false, captionBrushKey);

            void OnThemeChanged(bool dark) => Apply(window, dark, BackdropType.None, zeroFrame: false, captionBrushKey);
            ThemeManager.ThemeChanged += OnThemeChanged;
            window.Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "对话框标题栏跟随主题");
        }
    }

    /// <summary>
    /// 主窗口专用：外壳全自绘的窗口不要系统那圈边框。
    ///
    /// 只在加载时设一次是不够的 —— 最大化 / 还原、DPI 变化、从最小化回来、
    /// 换主题，系统都会把这几个 DWM 属性重画回默认值，深色主题下默认边框是浅色的，
    /// 于是窗口外面就会出现一圈"白框"。所以这几个时机都重新设一遍。
    /// </summary>
    public static void AttachBorderless(Window window)
    {
        try
        {
            void Refresh() => ApplyShell(window, IsDarkTheme());

            window.SourceInitialized += (_, _) => Refresh();
            window.Activated += (_, _) => Refresh();
            window.StateChanged += (_, _) => Refresh();
            window.DpiChanged += (_, _) => Refresh();

            void OnThemeChanged(bool dark) => ApplyShell(window, dark);
            ThemeManager.ThemeChanged += OnThemeChanged;
            window.Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "主窗口去掉系统边框");
        }
    }

    /// <summary>从主题里取一个颜色，转成 DWM 要的 COLORREF（0x00BBGGRR）。</summary>
    private static int? ThemeColor(string brushKey)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(brushKey)
                is System.Windows.Media.SolidColorBrush brush)
            {
                var c = brush.Color;
                return c.R | (c.G << 8) | (c.B << 16);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取主题颜色");
        }

        return null;
    }

    /// <summary>
    /// 自绘外壳的主窗口专用的实际调用：云母背景 + 圆角，但**不**把可见边框压成 0。
    ///
    /// v1.3.0 修正：以前主窗口走 <c>Apply(..., zeroFrame: true)</c>，把系统那圈 1px 的
    /// 可见边框压成 0 —— 而 Win11 的圆角正是画在这圈边框上的（见 Apply 里的注释），
    /// 于是所有对话框是圆的、主窗口偏偏是直角。用户原话「设置窗口没有圆角」就是这个。
    ///
    /// 现在只是不再压边框厚度；边框颜色仍然是 <see cref="ColorNone"/>，
    /// 所以补不出那一圈白线，只是把圆角让出来。
    /// </summary>
    public static void ApplyShell(Window window, bool dark)
        => Apply(window, dark, BackdropType.Mica, zeroFrame: false);

    /// <summary>
    /// 主窗口外框圆角，半径读 <see cref="ThemeMetrics.Radius"/>。
    ///
    /// 两种情况二选一：
    /// ① 半径正好等于 Win11 的原生值（8）—— 交给 DWM，边缘有抗锯齿，最漂亮；
    /// ② 半径是别的值（例如统一成 12）—— DWM 只能给 8/4/0，只能自绘：
    ///    先关掉 DWM 圆角，再用窗口区域（窗口形状）裁一个同半径的圆角矩形。
    ///    代价是区域边缘不做抗锯齿，拐角会有一点锯齿；好处是外框和内层卡片
    ///    永远是同一个半径，用户要的"所有圆角一致"才成立。
    ///
    /// 最大化时把区域整个撤掉，否则屏幕角上会露出一圈桌面色。
    /// </summary>
    public static void EnableRounded(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            /* 一律交给 DWM，**不再自绘窗口区域**。
             *
             * 以前（工作区里那版）在半径 != DWM 原生值时用 SetWindowRgn 裁一个圆角矩形，
             * 代价是：① 边缘不抗锯齿；② 对 AllowsTransparency 的窗口（手势覆盖层、
             * 中键菜单、Toast）设区域会直接破坏分层渲染 —— 表现就是"窗口在，但一个像素都看不见"。
             * 用户要的"圆角跟图上一样大"是内层卡片/按钮的事（ThemeMetrics.Radius），
             * 窗口外框保持系统原生的 8，视觉上更干净，也不会再出现整块不显示。
             * 最大化时 DWM 自己会去掉圆角，不需要额外处理。 */
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    private static void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Window window) ApplyWindowRegion(window);
    }

    private static void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (sender is Window window) ApplyWindowRegion(window);
    }

    /// <summary>把窗口形状裁成圆角矩形；最大化时撤掉区域。</summary>
    private static void ApplyWindowRegion(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (window.WindowState == WindowState.Maximized)
            {
                SetWindowRgn(hwnd, IntPtr.Zero, true);
                return;
            }

            var source = PresentationSource.FromVisual(window);
            double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int w = (int)Math.Round(window.ActualWidth * scale);
            int h = (int)Math.Round(window.ActualHeight * scale);
            if (w <= 0 || h <= 0) return;

            int diameter = (int)Math.Round(ThemeMetrics.Radius * scale) * 2;
            IntPtr region = CreateRoundRectRgn(0, 0, w + 1, h + 1, diameter, diameter);
            if (region == IntPtr.Zero) return;
            /* SetWindowRgn 接管这块 region 的所有权，成功后不要 DeleteObject。 */
            if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "窗口圆角区域");
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
}
