using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace InputPlus.Core.Glass;

/// <summary>
/// Files（WinUI 3）那套「薄亚克力 / Thin Acrylic」在 WPF 里的等价实现。
///
/// 参照 <c>Files-main/src/Files.App/Helpers/UI/AppSystemBackdrop.cs</c> 的
/// <c>SetThinAcrylicBackdropProperties()</c>（Thin Acrylic recipe from the WinUI Figma toolkit）：
///   Light: TintColor #D3D3D3 / TintOpacity 0 / LuminosityOpacity 0.44 / Fallback #99D3D3D3
///   Dark : TintColor #545454 / TintOpacity 0 / LuminosityOpacity 0.64 / Fallback #FF202020
/// 也就是说：**模糊由系统给，颜色只有一层"亮压白 / 暗压黑"的亮度层**，没有别的染色。
///
/// 模糊这条路在本机实测过的取舍（见 TryEnable 的注释）：
///   1. accent 亚克力（SetWindowCompositionAttribute + ACCENT_ENABLE_ACRYLICBLURBEHIND）——
///      真·实时模糊，优先；
///   2. DWM 的 DWMWA_SYSTEMBACKDROP_TYPE = Acrylic —— Files 用的就是它，但本机
///      （Win11 26H1）只给一层固定 tint、背后不糊，所以只当老系统兜底。
/// 两条都不可用时，<see cref="MaterialBrush"/> 会给 Files 的 FallbackColor，面板仍然能看。
/// </summary>
internal static class ThinAcrylic
{
    /* ---------------- Files 的配方常量 ---------------- */

    /// <summary>亮色主题：TintColor #D3D3D3。</summary>
    public static readonly Color LightTint = Color.FromRgb(0xD3, 0xD3, 0xD3);

    /// <summary>暗色主题：TintColor #545454。</summary>
    public static readonly Color DarkTint = Color.FromRgb(0x54, 0x54, 0x54);

    /// <summary>亮色主题 LuminosityOpacity 0.44。</summary>
    public const double LightLuminosity = 0.44;

    /// <summary>暗色主题 LuminosityOpacity 0.64。</summary>
    public const double DarkLuminosity = 0.64;

    /// <summary>亮色主题 FallbackColor #99D3D3D3 —— 系统不给模糊时它就是面板本身。</summary>
    public static readonly Color LightFallback = Color.FromArgb(0x99, 0xD3, 0xD3, 0xD3);

    /// <summary>暗色主题 FallbackColor #FF202020。</summary>
    public static readonly Color DarkFallback = Color.FromArgb(0xFF, 0x20, 0x20, 0x20);

    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableAcrylicBlurBehind = 4;

    /// <summary>accent 渐变色的 alpha：面板色压到 25%（0x40）—— 再高就把背后的模糊盖住了。</summary>
    private const int AccentTintAlpha = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    /* ---------------- WPF 侧：颜色分层 ---------------- */

    /// <summary>当前主题的亮度层颜色：亮色压白、暗色压黑。</summary>
    public static Color LuminosityColor(bool dark) => dark ? Colors.Black : Colors.White;

    /// <summary>当前主题的 tint 颜色（accent 渐变用）。</summary>
    public static Color TintColor(bool dark) => dark ? DarkTint : LightTint;

    /// <summary>当前主题的兜底色。</summary>
    public static Color FallbackColor(bool dark) => dark ? DarkFallback : LightFallback;

    /// <summary>
    /// 面板上要铺的那层：模糊在跑就给亮度层（0.44 / 0.64 × 浓度），否则给 Files 的兜底色。
    ///
    /// <paramref name="panelTint"/> 是设置里的「主色调」：给了就在兜底路上当底色用，
    /// 模糊跑着时仍然只叠亮度层 —— Files 的 Thin Acrylic 本来就没有染色层（TintOpacity 0）。
    /// </summary>
    public static Brush MaterialBrush(bool dark, bool frosted, double density, Color? panelTint)
    {
        double d = Math.Clamp(double.IsFinite(density) ? density : 1.0, 0.3, 1.0);

        if (frosted)
        {
            Color lum = LuminosityColor(dark);
            double opacity = (dark ? DarkLuminosity : LightLuminosity) * d;
            var layer = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(Math.Round(opacity * 255.0), 0, 255), lum.R, lum.G, lum.B));
            layer.Freeze();
            return layer;
        }

        Color baseColor = panelTint ?? FallbackColor(dark);
        var solid = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp(Math.Round(baseColor.A * d), 0, 255), baseColor.R, baseColor.G, baseColor.B));
        solid.Freeze();
        return solid;
    }

    /* ---------------- 系统侧：开关模糊 ---------------- */

    /// <summary>
    /// 打开系统亚克力。返回 false 表示这台机器两条路都不吃，调用方用兜底色。
    ///
    /// 顺序：**先 accent，再 DWM** —— 本机实测（Win11 26H1）
    /// <c>DWMWA_SYSTEMBACKDROP_TYPE = Acrylic</c> 只给一层固定 tint、背后根本不糊；
    /// <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c> 才是真·实时模糊（压全黑背景 228、压全白 253）。
    /// </summary>
    public static bool TryEnable(Window window, bool dark, Color? panelTint)
    {
        if (window == null) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        if (TryEnableAccent(hwnd, dark, panelTint)) return true;

        try
        {
            /* 兜底：DWM 的系统背景（Files 用的就是这条）。不糊，但至少跟系统主题一致。 */
            if (UI.Theme.WindowChrome.TrySetBackdrop(window, UI.Theme.WindowChrome.BackdropType.Acrylic))
            {
                var all = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref all);
                return true;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "薄亚克力：DWM 背景不可用");
        }

        return false;
    }

    /// <summary>Win10 1809 起就有的那条路：accent 亚克力，真·实时模糊。</summary>
    private static bool TryEnableAccent(IntPtr hwnd, bool dark, Color? panelTint)
    {
        try
        {
            Color tint = panelTint ?? TintColor(dark);
            int gradient = (AccentTintAlpha << 24) | (tint.B << 16) | (tint.G << 8) | tint.R;

            var policy = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,                    // 往客户区画
                GradientColor = gradient,
                AnimationId = 0,
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, buffer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = buffer,
                    SizeOfData = size,
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "薄亚克力：accent 打开失败");
            return false;
        }
    }

    /// <summary>关掉系统亚克力（「背景模糊」拉到 0，或者窗口要收起来时）。</summary>
    public static void Disable(Window window)
    {
        if (window == null) return;

        try
        {
            UI.Theme.WindowChrome.TrySetBackdrop(window, UI.Theme.WindowChrome.BackdropType.None);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "薄亚克力：关闭 DWM 背景");
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var policy = new AccentPolicy { AccentState = AccentDisabled };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, buffer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = buffer,
                    SizeOfData = size,
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "薄亚克力：accent 关闭失败");
        }
    }
}
