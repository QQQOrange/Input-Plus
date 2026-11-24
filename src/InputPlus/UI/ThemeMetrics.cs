using System.Windows;

namespace InputPlus.UI;

/// <summary>
/// 主题里那些"别处也要用"的尺寸。
///
/// 目前只有滚动条：<c>Theme/Controls.xaml</c> 把宽度收窄到 7 之后，
/// 任何"给滚动条让一条缝"的布局（Qbar 设置页顶部说明卡、按键映射的最小窗口宽度）
/// 都必须读同一个值。以前这些地方用 <see cref="SystemParameters.VerticalScrollBarWidth"/>
/// （系统默认 17），于是卡片右边缘比下面的窄 10 DIP —— 用户截图里那条空带就是这么来的。
/// </summary>
internal static class ThemeMetrics
{
    /// <summary>卡片 / 弹层 / 面板的圆角。</summary>
    internal const double RadiusCard = 12;

    /// <summary>按钮 / 输入框 / 列表行 / 键帽的圆角。</summary>
    internal const double RadiusControl = 12;

    /// <summary>小标签 / 徽章的圆角。</summary>
    internal const double RadiusSmall = 12;

    /// <summary>滚动条 / 细条 / 16px 小色块的圆角。</summary>
    internal const double RadiusTiny = 12;

    /// <summary>圆形（CornerRadius 会自动夹成胶囊）。</summary>
    internal const double RadiusPill = 999;

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
