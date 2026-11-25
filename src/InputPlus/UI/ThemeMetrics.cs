using System.Windows;

namespace InputPlus.UI;

/// <summary>
/// 主题里那些"别处也要用"的尺寸。
///
/// 圆角只有一个真值：<see cref="Radius"/>。所有 C# 侧画圆角的地方都走它，
/// XAML 侧对应资源是 <c>Theme/Controls.xaml</c> 里的 <c>Radius.All</c>（同值）。
/// 主窗口外框也读这个值：8 走 DWM 原生圆角（带抗锯齿），其它值走窗口区域裁剪
/// （见 Theme/WindowChrome.EnableRounded），所以外框和内层卡片永远同半径。
///
/// 目前只有滚动条是独立尺寸：<c>Theme/Controls.xaml</c> 把宽度收窄到 7 之后，
/// 任何"给滚动条让一条缝"的布局（Qbar 设置页顶部说明卡、按键映射的最小窗口宽度）
/// 都必须读同一个值。以前这些地方用 <see cref="SystemParameters.VerticalScrollBarWidth"/>
/// （系统默认 17），于是卡片右边沿比下面的窄 10 DIP —— 用户截图里那条空带就是这么来的。
/// </summary>
internal static class ThemeMetrics
{
    /// <summary>全站唯一圆角值（DIP）。改这里，卡片、控件、菜单、主窗口外框一起变。</summary>
    internal const double Radius = 12;

    /// <summary>DWM 原生圆角（Win11）只有这一个半径，命中时走原生抗锯齿路径。</summary>
    internal const double NativeRadius = 8;   // DWM 原生圆角只有 8/4/0 三档

    /// <summary>卡片 / 弹层 / 面板的圆角（= <see cref="Radius"/>）。</summary>
    internal const double RadiusCard = Radius;

    /// <summary>按钮 / 输入框 / 列表行 / 键帽的圆角（= <see cref="Radius"/>）。</summary>
    internal const double RadiusControl = Radius;

    /// <summary>小标签 / 徽章的圆角（= <see cref="Radius"/>）。</summary>
    internal const double RadiusSmall = Radius;

    /// <summary>滚动条 / 细条 / 16px 小色块的圆角（= <see cref="Radius"/>）。</summary>
    internal const double RadiusTiny = Radius;

    /// <summary>圆形（CornerRadius 会自动夹成胶囊）。</summary>
    internal const double RadiusPill = 999;

    /// <summary>四角同值。</summary>
    internal static CornerRadius CornerAll => new(Radius);

    /// <summary>上两角（贴着下沿的浮层用）。</summary>
    internal static CornerRadius CornerTop => new(Radius, Radius, 0, 0);

    /// <summary>下两角（贴着上沿的浮层用）。</summary>
    internal static CornerRadius CornerBottom => new(0, 0, Radius, Radius);

    /// <summary>左侧两角（横向贴在窗口左沿的卡片用）。</summary>
    internal static CornerRadius CornerLeft => new(Radius, 0, 0, Radius);

    /// <summary>滚动条实际粗细（DIP）。资源没加载时退回 7，和主题里的默认值一致。</summary>
    internal static double ScrollBarWidth
    {
        get
        {
            try
            {
                if (Application.Current?.TryFindResource("ScrollBarWidth") is double value && value > 0)
                    return value;
            }
            catch
            {
                /* 资源字典还没加载（极早期 / 自检环境）：走下面的兜底 */
            }
            return 7;
        }
    }
}
