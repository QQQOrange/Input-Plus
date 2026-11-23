using System.Windows;
using System.Windows.Media;
using InputPlus.Core;

namespace InputPlus.UI.Theme;

/// <summary>
/// 界面字体：用户从**系统已安装的字体**里挑一个，整个软件的界面文字跟着它走。
///
/// 为什么只需要换两个资源键就够：界面从一开始就把字体收在 <c>Theme/Controls.xaml</c>
/// 的 <c>Font.Ui</c> / <c>Font.Mono</c> 里 —— 每个控件模板的 Setter、每个页面/窗口的
/// 根元素引用的都是它。所以"全局改字体"不需要逐个窗口去设属性，只要：
///
///   1. 引用方写成 <c>{DynamicResource Font.Ui}</c>（这一点是前提：<c>StaticResource</c>
///      在加载那一刻就把值**复制**进元素，之后换掉资源，已经建好的界面不会刷新）；
///   2. 这里把两个条目换成新的 <see cref="FontFamily"/>。
///
/// 两者都成立时，WPF 会通知所有动态引用重新取值 —— 已打开的窗口、浮层、托盘菜单一起变，
/// 不用重启，也不用重建窗口。
///
/// 有一类字体**故意不跟着变**：
///   · 图标字体（Segoe Fluent Icons / MDL2）—— 换了就是一堆豆腐块，控件模板里写死；
///   · 音量/亮度横条的数字（<c>OsdPlacement.ValueFontFamilyName</c>）—— 那条横条复刻的是
///     Windows 自己的横条，字体是按系统逐像素量出来的，换成本软件的字体就不再像系统了。
/// </summary>
public static class FontManager
{
    /// <summary>
    /// 出厂字体：中文走微软雅黑，西文走 Win11 的 Segoe UI Variable，再兜一层经典 Segoe UI。
    /// 必须与 <c>Theme/Controls.xaml</c> 里 <c>Font.Ui</c> 的初始值一致 ——
    /// 不一致的话"恢复默认"和首次启动会显示成两种字。
    /// </summary>
    public const string BuiltInUi = "Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI";

    /// <summary>出厂等宽字体（脚本编辑器、公式输入、录制计时用）。</summary>
    public const string BuiltInMono = "Cascadia Mono, Consolas, Microsoft YaHei UI";

    /// <summary>出厂等宽字体里第一个"一定装得上"的族名，给 WinForms（托盘菜单）用。</summary>
    private const string WinFormsFallback = "Microsoft Sans Serif";

    private static IReadOnlyList<string>? _installed;

    private static IReadOnlyList<FontFamily>? _families;

    /// <summary>用户选的字体族名；空 = 出厂字体。</summary>
    public static string Family { get; private set; } = "";

    /// <summary>等宽区域是否也跟随界面字体（见 <c>GlobalOptions.UiFontMono</c>）。</summary>
    public static bool FollowMono { get; private set; } = true;

    /// <summary>界面正文字体。选中的字体排在前面，后面挂上出厂链做**缺字兜底**。</summary>
    public static FontFamily UiFamily => Build(Family, BuiltInUi);

    /// <summary>等宽区域字体。跟随开关打开时与界面字体一致，否则回到出厂等宽链。</summary>
    public static FontFamily MonoFamily => FollowMono ? Build(Family, BuiltInUi) : Build("", BuiltInMono);

    /// <summary>字体真的换了之后触发（托盘菜单这种不认 WPF 资源的地方要靠它自己重刷）。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 应用字体：写进应用级资源，界面立刻跟着变。应用设置时调用。
    /// </summary>
    /// <param name="family">系统已安装字体的族名；空/空白 = 出厂字体。</param>
    /// <param name="followMono">等宽区域是否也换。</param>
    public static void Apply(string? family, bool followMono = true)
    {
        Family = Normalize(family);
        FollowMono = followMono;

        var app = Application.Current;
        if (app != null)
        {
            /* 直接写在 Application.Resources 顶层：它会**盖住**合并字典里的同键条目，
             * 于是切换主题（ThemeManager 换掉整个 Theme.*.xaml）也不会把字体弄丢。
             * 换主题之后仍然会调一次 Reapply —— 那条路也有人在合并字典里直接改键，
             * 多写一次不会有害。 */
            try
            {
                app.Resources["Font.Ui"] = UiFamily;
                app.Resources["Font.Mono"] = MonoFamily;
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[字体] 写入界面字体失败：" + ex.Message);
            }
        }

        try { Changed?.Invoke(); }
        catch (Exception ex) { CoreLog.Swallow(ex, "通知界面字体变化"); }
    }

    /// <summary>按当前状态再写一次资源（主题字典被换掉之后调用）。</summary>
    public static void Reapply() => Apply(Family, FollowMono);

    /// <summary>
    /// 归一化：去掉空白；写法与出厂字体完全相同时当作"没选"。
    ///
    /// 为什么要把出厂写法折成空：用户可能手动改过配置文件、或从旧版本备份导入了
    /// 一整串 "Microsoft YaHei UI, Segoe UI"，那串和出厂字体是同一个东西，
    /// 不该在下拉里表现成"一个名字很长的自定义项"。
    /// </summary>
    public static string Normalize(string? family)
    {
        string name = (family ?? "").Trim();
        if (name.Length == 0) return "";
        if (name.Equals(BuiltInUi, StringComparison.OrdinalIgnoreCase)) return "";
        if (name.Equals(BuiltInMono, StringComparison.OrdinalIgnoreCase)) return "";
        return name;
    }

    /// <summary>这个族名在本机装没装（用于判断配置里的字体换机之后还成不成立）。</summary>
    public static bool IsInstalled(string? family)
    {
        string name = Normalize(family);
        if (name.Length == 0) return true;      // 空 = 出厂字体，一直在
        foreach (string f in Installed())
        {
            if (f.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 这个族名在**本机**认不认得出 —— 比 <see cref="IsInstalled"/> 宽一层：
    /// 除了 <c>Source</c>（注册表里的西文名，如 <c>LXGW WenKai</c>），
    /// 再查一遍每个字体族的**本地化名**（如「霞鹜文楷」「楷体」）。
    ///
    /// 为什么要两条：<c>Fonts.SystemFontFamilies</c> 的 <c>Source</c> 不一定是用户看到的那个名字，
    /// 而 WPF 两个名字都认。设置页拿它来决定要不要在配置项后面标「（本机没有）」——
    /// 只查 Source 会把好好的中文字体标成"没装"，用户会以为设置坏了。
    /// </summary>
    public static bool IsKnownFamily(string? family)
    {
        string name = Normalize(family);
        if (name.Length == 0) return true;
        if (IsInstalled(name)) return true;

        foreach (FontFamily f in InstalledFamilies())
        {
            try
            {
                foreach (KeyValuePair<System.Windows.Markup.XmlLanguage, string> pair in f.FamilyNames)
                {
                    if (string.Equals(pair.Value, name, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "字体：本地化名匹配"); }
        }
        return false;
    }

    /// <summary>
    /// 系统已安装的字体族名（去重、按当前语言排序）。
    ///
    /// 只枚举一次并缓存：<c>Fonts.SystemFontFamilies</c> 是去注册表 + 解析字体文件的
    /// 重活（几百个条目、首次约几十到上百毫秒），而设置页每次保存都会 Refresh 一遍。
    /// 用户在软件运行期间新装了字体，用设置页那颗「重新扫描」调 <see cref="InvalidateInstalled"/>。
    /// </summary>
    public static IReadOnlyList<string> Installed()
    {
        if (_installed != null) return _installed;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (FontFamily f in InstalledFamilies())
        {
            string name = f.Source;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add(name)) list.Add(name);
        }
        try { list.Sort(StringComparer.CurrentCulture); }
        catch (Exception ex) { CoreLog.Swallow(ex, "排序字体列表"); }

        _installed = list;
        return list;
    }

    /// <summary>系统字体族对象本身（缓存一份，列表与"认不认得出"共用）。</summary>
    private static IReadOnlyList<FontFamily> InstalledFamilies()
    {
        if (_families != null) return _families;

        var list = new List<FontFamily>();
        try
        {
            foreach (FontFamily f in Fonts.SystemFontFamilies) list.Add(f);
        }
        catch (Exception ex)
        {
            /* 枚举失败不该让设置页打不开 —— 最差只是下拉里只剩"默认"一项。 */
            CoreLog.Warn("[字体] 枚举系统字体失败：" + ex.Message);
        }
        _families = list;
        return list;
    }

    /// <summary>
    /// 给下拉框用的显示名：优先用这个字体族的**本地化名**（中文系统上就是「楷体」「霞鹜文楷」），
    /// 没有本地化名时退回注册表里的西文名。
    ///
    /// 存进配置的永远是 <see cref="FontFamily.Source"/>（西文名），不是这个显示名 ——
    /// 显示名只影响下拉里那行字，换语言 / 换机器都不会让配置失效。
    /// </summary>
    public static string DisplayName(string? source)
    {
        string name = (source ?? "").Trim();
        if (name.Length == 0) return name;
        foreach (FontFamily f in InstalledFamilies())
        {
            if (!string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string localized = "";
                try
                {
                    var lang = System.Windows.Markup.XmlLanguage.GetLanguage(
                        System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);
                    if (f.FamilyNames.TryGetValue(lang, out string? n)) localized = n;
                }
                catch (Exception ex) { CoreLog.Swallow(ex, "字体：本地化语言标签"); }
                if (string.IsNullOrWhiteSpace(localized)) localized = f.FamilyNames.Values.FirstOrDefault() ?? "";
                if (!string.IsNullOrWhiteSpace(localized)) return localized;
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "字体：取显示名"); }
            return name;
        }
        return name;
    }

    /// <summary>清掉字体列表缓存（下次 <see cref="Installed"/> 重新枚举）。</summary>
    public static void InvalidateInstalled()
    {
        _installed = null;
        _families = null;
    }

    /// <summary>
    /// 下拉项预览用的字体：<paramref name="name"/> 打头，后面挂出厂链。
    ///
    /// 挂兜底不是可选项：西文/韩文字体大多没有汉字，只设它自己的话
    /// 「思源宋体」这类项目名会变成一行豆腐块，反而看不出字体的样子。
    /// </summary>
    public static FontFamily PreviewFamily(string? name)
    {
        string clean = Normalize(name);
        if (clean.Length == 0) return new FontFamily(BuiltInUi);
        return Build(clean, BuiltInUi);
    }

    /// <summary>
    /// 托盘菜单（WinForms <c>ContextMenuStrip</c>）能用的族名。
    ///
    /// WinForms 的 <c>Font</c> 只认**单个**族名，不认 WPF 那种逗号串的后备链；
    /// 传一个没装的族名会静默退到 Microsoft Sans Serif（中文变成方块）。
    /// 所以这里挑第一个真的装了的：用户选的 → 微软雅黑 → Segoe UI → GDI 兜底。
    /// </summary>
    public static string WinFormsFamilyName
    {
        get
        {
            foreach (string candidate in new[] { Family, "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" })
            {
                if (candidate.Length > 0 && IsInstalled(candidate)) return candidate;
            }
            return WinFormsFallback;
        }
    }

    /// <summary>
    /// 拼出 "选中字体, 出厂链" 这种复合字体族。
    ///
    /// WPF 的复合字体族是**按字形逐个回退**的：选中字体缺哪个字形就去后面那串里找，
    /// 所以选一个只有拉丁字母的字体也不会让中文界面变成方块。
    /// 字体名非法时（用户手改了配置文件）退回出厂字体，不让异常打断启动。
    /// </summary>
    private static FontFamily Build(string family, string fallback)
    {
        string text = family.Length == 0 ? fallback : family + ", " + fallback;
        try { return new FontFamily(text); }
        catch (Exception ex)
        {
            CoreLog.Warn("[字体] 字体名无效，退回出厂字体：" + text + "（" + ex.Message + "）");
            return new FontFamily(fallback);
        }
    }
}
