using System.Windows;
using System.Windows.Media;
using InputPlus.Core;
using InputPlus.Core.Settings;
using InputPlus.UI.Theme;

namespace InputPlus;

/// <summary>
/// 自检：界面字体（设置 → 外观 → 界面字体）。
///
/// 要钉住的是四件事，任何一件坏了用户都会立刻看到：
///   ① 归一化：空白 / 出厂写法都算"没选"，不会在配置里长出两种"默认"；
///   ② 真的写进了应用级资源 —— Font.Ui / Font.Mono 换掉之后，
///      所有 <c>{DynamicResource Font.Ui}</c> 的引用方（也就是整个界面）才跟着变；
///   ③ 切主题不会把字体弄丢（ThemeManager 换字典后要 Reapply）；
///   ④ 等宽区域的跟随开关生效，关掉时回到出厂等宽链。
/// </summary>
public static partial class SelfTest
{
    private static void TestFonts()
    {
        string savedFamily = FontManager.Family;
        bool savedMono = FontManager.FollowMono;
        var savedTheme = UI.Theme.ThemeManager.Current;

        try
        {
            // ── ① 归一化 ────────────────────────────────────────────────
            CheckEqual("字体：null 归一成默认", FontManager.Normalize(null), "");
            CheckEqual("字体：空白归一成默认", FontManager.Normalize("   "), "");
            CheckEqual("字体：出厂写法归一成默认", FontManager.Normalize(FontManager.BuiltInUi), "");
            CheckEqual("字体：出厂等宽写法也归一成默认", FontManager.Normalize(FontManager.BuiltInMono), "");
            CheckEqual("字体：前后空格被去掉", FontManager.Normalize("  Segoe UI  "), "Segoe UI");

            // ── ② 系统字体枚举 ──────────────────────────────────────────
            var installed = FontManager.Installed();
            Check("字体：枚举到系统字体（> 5 种）", installed.Count > 5, $"实际 {installed.Count} 种");
            Check("字体：列表里没有空白项", installed.All(n => n.Trim().Length > 0),
                string.Join(" / ", installed.Where(n => n.Trim().Length == 0).Take(3)));
            Check("字体：默认字体在列表里（微软雅黑或 Segoe UI 至少有一个）",
                installed.Any(n => n.Contains("YaHei", StringComparison.OrdinalIgnoreCase)) ||
                installed.Any(n => n.Contains("Segoe UI", StringComparison.OrdinalIgnoreCase)),
                string.Join(" / ", installed.Take(5)));

            // ── ③ 应用字体真的换掉了应用级资源 ──────────────────────────
            string probe = installed.FirstOrDefault(n => n.Contains("Segoe UI", StringComparison.OrdinalIgnoreCase))
                           ?? installed[0];

            FontManager.Apply(probe);
            CheckEqual("字体：当前字体就是选的那个", FontManager.Family, probe);
            Check("字体：Font.Ui 已换成选中的字体",
                UiFamilySource().StartsWith(probe, StringComparison.OrdinalIgnoreCase),
                UiFamilySource());
            Check("字体：选中的字体后面挂着出厂兜底（缺字不会变方块）",
                UiFamilySource().Contains("Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase),
                UiFamilySource());

            // ── ④ 等宽区域跟随 / 不跟随 ─────────────────────────────────
            FontManager.Apply(probe, followMono: true);
            Check("字体：等宽区域跟随界面字体",
                MonoFamilySource().StartsWith(probe, StringComparison.OrdinalIgnoreCase),
                MonoFamilySource());

            FontManager.Apply(probe, followMono: false);
            Check("字体：关掉跟随后等宽区域回到出厂等宽链",
                MonoFamilySource().StartsWith("Cascadia Mono", StringComparison.OrdinalIgnoreCase),
                MonoFamilySource());

            // ── ⑤ 切主题不能把字体弄丢（ThemeManager 换完字典要 Reapply）──
            FontManager.Apply(probe, followMono: true);
            UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Dark);
            Check("字体：切到深色主题后字体还在",
                UiFamilySource().StartsWith(probe, StringComparison.OrdinalIgnoreCase),
                UiFamilySource());
            UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Light);
            Check("字体：再切回浅色主题后字体还在",
                UiFamilySource().StartsWith(probe, StringComparison.OrdinalIgnoreCase),
                UiFamilySource());

            // ── ⑥ 恢复默认 ──────────────────────────────────────────────
            FontManager.Apply("", followMono: true);
            CheckEqual("字体：恢复默认后 Family 为空", FontManager.Family, "");
            Check("字体：恢复默认后 Font.Ui 回到出厂链",
                UiFamilySource().Contains("Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase),
                UiFamilySource());

            // ── ⑦ 配置里的脏数据不能让界面起不来 ────────────────────────
            bool threw = false;
            try { FontManager.Apply("这个字体根本不存在 ABC"); }
            catch (Exception ex) { threw = true; CoreLog.Warn("字体自检：预期内不该抛异常 —— " + ex.Message); }
            Check("字体：写了个不存在的字体也不抛异常", !threw);
            Check("字体：不存在的字体判为没装", !FontManager.IsInstalled("这个字体根本不存在 ABC"));
            Check("字体：默认字体永远算装了", FontManager.IsInstalled(""));

            // ── ⑧ 托盘菜单（WinForms）拿到的族名 ────────────────────────
            string winForms = FontManager.WinFormsFamilyName;
            Check("字体：托盘菜单拿到一个非空族名", winForms.Trim().Length > 0, winForms);
            Check("字体：托盘菜单的族名不含逗号（WinForms 不认复合字体）", !winForms.Contains(','), winForms);

            // ── ⑧.5 显示名与"认不认得出" ────────────────────────────────
            Check("字体：列表里的名字都认得出（IsKnownFamily）",
                installed.Count == 0 || installed.Take(5).All(FontManager.IsKnownFamily));
            CheckEqual("字体：未知名字的显示名原样返回",
                FontManager.DisplayName("这个字体根本不存在 ABC"), "这个字体根本不存在 ABC");
            /* 中文系统上「KaiTi」的显示名是「楷体」这类本地化名 —— 有就用，没有就退回西文名。
             * 这条是信息性断言：机器上没有任何带本地化名的字体时也算通过。 */
            string? localized = installed.FirstOrDefault(
                n => !string.Equals(FontManager.DisplayName(n), n, StringComparison.Ordinal));
            Check("字体：显示名优先用本地化名（本机有就用）", true,
                localized == null ? "这台机器上没有带本地化名的字体" : $"{localized} → {FontManager.DisplayName(localized)}");

            // ── ⑨ 设置项的出厂值 ────────────────────────────────────────
            var fresh = new GlobalOptions();
            CheckEqual("字体：出厂字体设置为空（跟随内置）", fresh.UiFont, "");
            Check("字体：出厂就允许等宽区域跟随", fresh.UiFontMono);
        }
        finally
        {
            FontManager.Apply(savedFamily, savedMono);
            UI.Theme.ThemeManager.Apply(savedTheme);
        }
    }

    /// <summary>应用级资源里 Font.Ui 的族名串（自检用；取不到给空串，断言会说出问题）。</summary>
    private static string UiFamilySource()
        => (Application.Current?.Resources["Font.Ui"] as FontFamily)?.Source ?? "(空)";

    /// <summary>应用级资源里 Font.Mono 的族名串。</summary>
    private static string MonoFamilySource()
        => (Application.Current?.Resources["Font.Mono"] as FontFamily)?.Source ?? "(空)";
}
