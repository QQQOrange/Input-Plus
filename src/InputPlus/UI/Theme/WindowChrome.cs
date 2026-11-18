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

    public static void Apply(Window window, bool dark, BackdropType backdrop = BackdropType.Mica)
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

            /* 标题栏颜色跟皮肤走：用「比正文稍亮/稍暗一档」的 SurfaceAlt，
             * 既不纯黑也不纯白，和对话框正文是同一条色阶。 */
            if (ThemeColor("Brush.SurfaceAlt") is int caption)
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
            int frame = 0;
            DwmSetWindowAttribute(hwnd, DWMWA_VISIBLE_FRAME_BORDER_THICKNESS, ref frame, sizeof(int));
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
    public static void AttachTo(Window window)
    {
        try
        {
            window.SourceInitialized += (_, _)
                => Apply(window, IsDarkTheme(), BackdropType.None);

            void OnThemeChanged(bool dark) => Apply(window, dark, BackdropType.None);
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
            void Refresh() => Apply(window, IsDarkTheme());

            window.SourceInitialized += (_, _) => Refresh();
            window.Activated += (_, _) => Refresh();
            window.StateChanged += (_, _) => Refresh();
            window.DpiChanged += (_, _) => Refresh();

            void OnThemeChanged(bool dark) => Apply(window, dark);
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

    public static void EnableRounded(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int round = 2;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }
}
