using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 读「当前网页」的地址（v1.5.0，收藏星标用）。
///
/// ── 为什么走 UI Automation，而不是模拟 Ctrl+L / Ctrl+C ──────────────
/// 后者会改用户的键盘状态与剪贴板，还会把焦点抢到地址栏 —— 为了填一个输入框
/// 去动用户的剪贴板，代价明显过大。浏览器的地址栏在 UIA 里本来就是一棵普通的树：
/// 一个 <c>Edit</c>，值就是当前标签页的网址，读它不产生任何副作用。
///
/// ── 三条实现约束 ────────────────────────────────────────────────
/// ① UIA 是 COM：**必须跑在 STA 线程**上，而且目标卡住时会一直阻塞，
///    所以调用方带硬超时，超时就放弃（返回 null，让用户手填）。
/// ② Chromium 系第一次被 UIA 问的时候要现场开启无障碍树，可能要好几百毫秒 ——
///    所以调用方把它放在后台线程上，不在界面线程等。
/// ③ 只认 <c>http/https</c>：<c>chrome://newtab</c>、<c>about:blank</c>、
///    <c>edge://settings</c> 都不是能收藏的网页，宁可空着。
/// </summary>
public static class BrowserPageProbe
{
    /// <summary>常见浏览器的进程名。类名兜底见 <see cref="IsBrowserClass"/>。</summary>
    private static readonly string[] BrowserExes =
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "vivaldi.exe",
        "opera.exe", "operagx.exe", "chromium.exe", "thorium.exe", "arc.exe",
        "360se.exe", "360chrome.exe", "qqbrowser.exe", "sogouexplorer.exe",
        "ucbrowser.exe", "liebao.exe", "2345explorer.exe", "maxthon.exe",
        "theworld.exe", "iexplore.exe", "supermium.exe", "centbrowser.exe",
        "waterfox.exe", "librewolf.exe", "zen.exe",
    };

    /// <summary>Chromium 系与 Firefox 的顶层窗口类名。</summary>
    private static readonly string[] BrowserClasses =
    {
        "Chrome_WidgetWin_1", "Chrome_WidgetWin_0", "MozillaWindowClass", "IEFrame",
    };

    /// <summary>自检用：拦截真实 UIA 查询。</summary>
    public static Func<IntPtr, string?>? InterceptForTest { get; set; }

    /// <summary>这个窗口类名是不是浏览器主窗口。</summary>
    public static bool IsBrowserClass(string? cls)
    {
        string c = (cls ?? "").Trim();
        if (c.Length == 0) return false;
        foreach (string known in BrowserClasses)
            if (string.Equals(known, c, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>这个进程名是不是浏览器。</summary>
    public static bool IsBrowserExe(string? exe)
    {
        string name = (exe ?? "").Trim();
        if (name.Length == 0) return false;
        foreach (string known in BrowserExes)
            if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 综合判定：进程名或窗口类名任一命中即算浏览器。
    /// 进程名优先（用户可能给 chrome 换了个外壳类名），类名兜底（便携版进程名不规范）。
    /// </summary>
    public static bool LooksLikeBrowser(string? exeName, string? className)
        => IsBrowserExe(exeName) || IsBrowserClass(className);

    /// <summary>
    /// 读浏览器窗口当前标签页的网址；读不到返回 null。
    ///
    /// 内部起一条 STA 线程并等 <paramref name="timeoutMs"/>：超时就返回 null，
    /// 线程自己跑完后退出（后台线程，不拖住进程）。
    /// </summary>
    public static string? ReadUrl(IntPtr hwnd, int timeoutMs = 1600)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (InterceptForTest != null) return InterceptForTest(hwnd);

        string? found = null;
        var thread = new System.Threading.Thread(() => { found = ReadUrlCore(hwnd); })
        {
            IsBackground = true,
            Name = "InputPlus.BrowserUrlProbe",
        };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        try
        {
            thread.Start();
            if (!thread.Join(Math.Max(200, timeoutMs))) return null;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "收藏：启动读地址栏线程");
            return null;
        }
        return found;
    }

    /// <summary>
    /// 在 STA 线程上跑：先找第一个 Edit（浏览器的地址栏在视觉树上排在网页正文之前），
    /// 找不到再找第一个 Document（文档的 ValuePattern 值就是文档 URL）。
    ///
    /// 为什么用 <c>FindFirst</c> 而不是 <c>FindAll(Descendants)</c>：
    /// 后者要求浏览器把**整棵**无障碍树materialize 出来（大页面上要好几秒），
    /// 而 FindFirst 一命中就返回 —— 地址栏本来就是最靠前的那个 Edit。
    /// 读不到就返回 null，让用户手填，绝不为一个输入框去卡住界面。
    /// </summary>
    private static string? ReadUrlCore(IntPtr hwnd)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) return null;

            string? url = Probe(root, ControlType.Edit);
            if (url != null) return url;

            return Probe(root, ControlType.Document);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "收藏：读地址栏");
            return null;
        }
    }

    /// <summary>找第一个指定类型的元素并把它当候选网址读一次。</summary>
    private static string? Probe(AutomationElement root, ControlType type)
    {
        try
        {
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
            AutomationElement? element = root.FindFirst(TreeScope.Descendants, condition);
            if (element == null) return null;

            try
            {
                string name = element.Current.Name ?? "";
                string id = element.Current.AutomationId ?? "";
                string value = ReadValue(element);

                string? url = NormalizeUrl(value);
                if (url == null) return null;

                /* 通过"名字/ID 像地址栏"或"值本身就是完整网址"至少一条才算数：
                 * 页内搜索框里恰好写着 example.com 这种极少数情况，靠这一条挡掉。 */
                bool strongUrl = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                 || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                int score = ScoreOf(element, name, id, value);
                return score >= 10 || strongUrl ? url : null;
            }
            catch (ElementNotAvailableException) { return null; }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "收藏：读 UIA 元素");
                return null;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "收藏：查找 UIA 元素");
            return null;
        }
    }

    /// <summary>
    /// 给一个候选元素打分。分数只用来"在两个都像网址的元素里挑更像地址栏的那个"，
    /// 阈值由 <see cref="NormalizeUrl"/> 保证：不像网址的直接不参与。
    /// </summary>
    internal static int ScoreOf(AutomationElement element, string name, string automationId, string value)
    {
        int score = 0;
        string n = name ?? "";
        string id = automationId ?? "";

        if (id.Contains("omnibox", StringComparison.OrdinalIgnoreCase)) score += 60;
        if (id.Contains("urlbar", StringComparison.OrdinalIgnoreCase)) score += 60;
        if (id.Contains("address", StringComparison.OrdinalIgnoreCase)) score += 50;

        if (n.Contains("地址", StringComparison.Ordinal)) score += 40;
        if (n.Contains("Address", StringComparison.OrdinalIgnoreCase)) score += 40;
        if (n.Contains("搜索", StringComparison.Ordinal)) score += 20;
        if (n.Contains("search", StringComparison.OrdinalIgnoreCase)) score += 20;
        if (n.Contains("enter address", StringComparison.OrdinalIgnoreCase)) score += 20;

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) score += 30;

        try
        {
            // 文档元素（网页正文）也是候选：它的值就是文档 URL，权重最低当兜底
            if (element.Current.ControlType == ControlType.Document) score += 10;
        }
        catch { /* 元素已经失效：不影响打分 */ }

        return score;
    }

    /// <summary>读 ValuePattern；没有就返回空串。</summary>
    private static string ReadValue(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern)
                && pattern is ValuePattern value)
                return value.Current.Value ?? "";
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：读 ValuePattern"); }
        return "";
    }

    private static readonly Regex DomainLike = new(
        @"^(?:[A-Za-z0-9_~%-]+\.)+[A-Za-z]{2,}(?::\d{1,5})?(?:[/?#].*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 把地址栏里的文字规范成可收藏的网址。
    ///
    /// 地址栏里经常只有 <c>example.com/a</c>（没有协议头），也可能夹着
    /// Chromium 放在值里的零宽标记；<c>chrome://</c> 这类则直接判"不是网页"。
    /// </summary>
    internal static string? NormalizeUrl(string? value)
    {
        string v = (value ?? "").Trim().Trim('\u200B', '\u200E', '\u200F', '\uFEFF');
        if (v.Length == 0) return null;

        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return v;

        // 其它协议（chrome:// / edge:// / about: / file:// / view-source:）都不是"网页"
        if (v.Contains("://", StringComparison.Ordinal)) return null;
        if (v.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return null;

        // 地址栏里是"正在输入的内容"（带空格）时不要当成网址
        if (v.Contains(' ')) return null;

        return DomainLike.IsMatch(v) ? "https://" + v : null;
    }
}
