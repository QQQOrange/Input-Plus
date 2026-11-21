using System.Windows.Controls;
using System.Windows.Media;
using InputPlus.Core.Gestures;

namespace InputPlus.UI;

/// <summary>
/// 调色小工具：手势设置窗口与「提示条」共用一套，两处的色块看起来才是同一个东西。
///
/// 两个约定（都是从 WGestures 2 那边学来的、并在 v1.4 就定下来的）：
/// <list type="bullet">
/// <item>色块按原色的 alpha 画，但**垫一个下限 0x66** —— 出厂未识别色是 0x30 的 alpha，
/// 原样画几乎看不见；纯不透明又会把「淡」这个信息丢掉。原始 #RRGGBBAA 放 ToolTip。</item>
/// <item><c>ColorDialog</c> 没有 alpha 通道：先按不透明色挑，挑完把原 alpha 贴回去，
/// 于是「同一个紫、两种透明度」这类配置能保持形状。</item>
/// </list>
/// </summary>
internal static class ColorSwatch
{
    /// <summary>把 #RRGGBBAA 画到小色块上（alpha 不低于 0x66）。</summary>
    public static void Apply(Border swatch, string hex)
    {
        var c = Wg2Color.Parse(hex, Colors.Magenta);
        byte a = Math.Max(c.A, (byte)0x66);
        swatch.Background = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        swatch.ToolTip = hex.ToUpperInvariant();
    }

    /// <summary>挑一个颜色并保留原色的 alpha，返回 #RRGGBBAA；用户取消时返回 null。</summary>
    public static string? PickRgba(string current)
    {
        var before = Wg2Color.Parse(current, Colors.Magenta);
        var picked = Pick($"#{before.A:X2}{before.R:X2}{before.G:X2}{before.B:X2}");
        if (picked == null) return null;
        return Wg2Color.Format(Color.FromArgb(before.A, picked.Value.R, picked.Value.G, picked.Value.B));
    }

    /// <summary>原生取色对话框（WinForms 的，WPF 没有）。</summary>
    public static System.Drawing.Color? Pick(string initial)
    {
        System.Drawing.Color start;
        try { start = System.Drawing.ColorTranslator.FromHtml(initial); }
        catch { start = System.Drawing.Color.FromArgb(0x3B, 0x82, 0xF6); }

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = start,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.Color : null;
    }
}
