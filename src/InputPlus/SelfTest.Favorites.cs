using System.Collections.Generic;
using InputPlus.Core.Qbar;
using InputPlus.Core.Settings;

namespace InputPlus;

/// <summary>
/// 自检：附加菜单右上角的「收藏」（v1.5.0）。
///
/// 这一组全部是**纯对象**：关键字推导、网址规范化、备注清洗、草稿落成固定项。
/// 不点菜单、不读真的地址栏 —— 那些在 CI 机器上不存在，写进去只会变成
/// "本机绿、CI 红"的假用例。真机那部分（UIA 读网址、Shell COM 读目录）
/// 由手工验收覆盖，见 CHANGELOG。
/// </summary>
public static partial class SelfTest
{
    private static void TestFavorites()
    {
        // ── ① 网址关键字：取 www 与 com 中间那一段 ────────────────────
        CheckEqual("收藏关键字：www.example.com → example",
            FavoriteKeywords.FromUrl("https://www.example.com/a?b=1"), "example");
        CheckEqual("收藏关键字：GitHub → github",
            FavoriteKeywords.FromUrl("https://github.com/"), "github");
        CheckEqual("收藏关键字：www.bilibili.com → bilibili",
            FavoriteKeywords.FromUrl("https://www.bilibili.com/video/BV1"), "bilibili");
        CheckEqual("收藏关键字：mail.google.com → google（取可注册域名，不取子域）",
            FavoriteKeywords.FromHost("mail.google.com"), "google");
        CheckEqual("收藏关键字：bbs.szu.edu.cn → szu（两段式后缀）",
            FavoriteKeywords.FromHost("bbs.szu.edu.cn"), "szu");
        CheckEqual("收藏关键字：example.co.uk → example",
            FavoriteKeywords.FromHost("example.co.uk"), "example");
        CheckEqual("收藏关键字：没有协议头的网址也能推",
            FavoriteKeywords.FromUrl("example.com/path"), "example");
        CheckEqual("收藏关键字：IP 地址给 ip", FavoriteKeywords.FromHost("192.168.1.10:8080"), "ip");
        CheckEqual("收藏关键字：空网址给 web", FavoriteKeywords.FromUrl(""), "web");

        // ── ② 名字关键字：英文取首字母/前三位、中文取拼音首字母 ──────
        CheckEqual("收藏关键字：两个英文单词 → md", FavoriteKeywords.FromName("My Documents"), "md");
        CheckEqual("收藏关键字：单个英文单词 → 前 3 个字母",
            FavoriteKeywords.FromName("Projects"), "pro");
        CheckEqual("收藏关键字：英文超过 4 个词只取 4 个",
            FavoriteKeywords.FromName("a b c d e"), "abcd");
        CheckEqual("收藏关键字：中文取拼音首字母", FavoriteKeywords.FromName("工作资料"), "gzzl");
        CheckEqual("收藏关键字：中文超过 4 个字只取 4 个",
            FavoriteKeywords.FromName("微信图片收藏夹"), "wxtp");
        CheckEqual("收藏关键字：中文单个字", FavoriteKeywords.FromName("库"), "k");
        CheckEqual("收藏关键字：中英混排（数字词跳过）",
            FavoriteKeywords.FromName("2024 报告"), "bg");
        CheckEqual("收藏关键字：空名字给 fav", FavoriteKeywords.FromName(""), "fav");
        Check("收藏关键字：自动结果不超过 4 个字符",
            FavoriteKeywords.FromName("This Is A Very Long Folder Name").Length <= FavoriteKeywords.MaxLength);

        // ── ③ 手改之后的关键字清洗 ──────────────────────────────────
        CheckEqual("收藏关键字：去掉空格（空格是 Qbar 的分隔符）",
            FavoriteKeywords.Sanitize(" a b c "), "abc");
        CheckEqual("收藏关键字：只留字母数字与 - _",
            FavoriteKeywords.Sanitize("a/b\\c:d"), "abcd");
        CheckEqual("收藏关键字：大小写统一成小写",
            FavoriteKeywords.Sanitize("MyKey"), "mykey");
        CheckEqual("收藏关键字：空输入用兜底值",
            FavoriteKeywords.Sanitize("  ", "fav"), "fav");
        Check("收藏关键字：手改的长度有上限",
            FavoriteKeywords.Sanitize(new string('x', 100)).Length <= FavoriteKeywords.MaxTypedLength);

        // ── ④ 去重：撞了就挪一格 ────────────────────────────────────
        var taken = new List<string> { "gh", "GH2" };
        CheckEqual("收藏关键字：没撞就用原值", FavoriteKeywords.Unique("gh3", taken), "gh3");
        CheckEqual("收藏关键字：撞了挪到 gh3（大小写不敏感）",
            FavoriteKeywords.Unique("GH", taken), "gh3");

        // ── ⑤ 地址栏文字 → 可收藏的网址 ─────────────────────────────
        CheckEqual("网页地址：地址栏只有域名时补 https",
            BrowserPageProbe.NormalizeUrl("example.com/a"), "https://example.com/a");
        CheckEqual("网页地址：已经是 http(s) 就原样",
            BrowserPageProbe.NormalizeUrl("https://example.com/a"), "https://example.com/a");
        CheckEqual("网页地址：chrome:// 不是网页",
            BrowserPageProbe.NormalizeUrl("chrome://newtab"), null);
        CheckEqual("网页地址：about: 不是网页",
            BrowserPageProbe.NormalizeUrl("about:blank"), null);
        CheckEqual("网页地址：带空格的输入框内容不当作网址",
            BrowserPageProbe.NormalizeUrl("hello world"), null);
        CheckEqual("网页地址：空值", BrowserPageProbe.NormalizeUrl("   "), null);

        // ── ⑥ 浏览器识别 ────────────────────────────────────────────
        Check("浏览器识别：chrome.exe", BrowserPageProbe.IsBrowserExe("chrome.exe"));
        Check("浏览器识别：msedge.exe", BrowserPageProbe.IsBrowserExe("MSEDGE.EXE"));
        Check("浏览器识别：firefox.exe", BrowserPageProbe.IsBrowserExe("firefox.exe"));
        Check("浏览器识别：notepad.exe 不是浏览器", !BrowserPageProbe.IsBrowserExe("notepad.exe"));
        Check("浏览器识别：Chromium 的窗口类名", BrowserPageProbe.IsBrowserClass("Chrome_WidgetWin_1"));
        Check("浏览器识别：Firefox 的窗口类名", BrowserPageProbe.IsBrowserClass("MozillaWindowClass"));
        Check("浏览器识别：进程名 + 类名任一命中即算",
            BrowserPageProbe.LooksLikeBrowser("chrome.exe", "") );
        Check("浏览器识别：都不是就不算",
            !BrowserPageProbe.LooksLikeBrowser("notepad.exe", "Notepad"));

        // ── ⑦ 浏览器窗口标题 → 页面标题 ─────────────────────────────
        CheckEqual("网页标题：剥掉 Chrome 的尾巴",
            FavoriteResolver.CleanPageTitle("百度一下，你就知道 - Google Chrome", "chrome.exe"),
            "百度一下，你就知道");
        CheckEqual("网页标题：剥掉 Edge 的尾巴",
            FavoriteResolver.CleanPageTitle("示例站点 - Microsoft Edge", "msedge.exe"), "示例站点");
        CheckEqual("网页标题：不是浏览器尾巴就原样保留",
            FavoriteResolver.CleanPageTitle("A - B", "chrome.exe"), "A - B");
        CheckEqual("网页标题：空标题", FavoriteResolver.CleanPageTitle("", "chrome.exe"), "");

        // ── ⑧ 路径最后一段 ──────────────────────────────────────────
        CheckEqual("收藏路径：取最后一段", FavoriteResolver.LeafOf(@"D:\work\项目"), "项目");
        CheckEqual("收藏路径：末尾带分隔符也认", FavoriteResolver.LeafOf(@"D:\work\项目\"), "项目");
        CheckEqual("收藏路径：根目录给「D 盘」", FavoriteResolver.LeafOf(@"D:\"), "D" + Loc.T(" 盘"));

        // ── ⑨ 草稿 → Qbar 固定项 ────────────────────────────────────
        var draft = new FavoriteDraft
        {
            Source = FavoriteSource.Folder,
            Target = @"D:\work\项目",
            Comment = "项目",
            Keyword = "xm",
            IconPath = @"D:\work\项目",
            FallbackKeyword = "xm",
        };
        QEntry entry = draft.ToEntry(showInMiddleMenu: true);
        CheckEqual("收藏落库：关键字进 Key", entry.Key, "xm");
        CheckEqual("收藏落库：目标进 Value", entry.Value, @"D:\work\项目");
        CheckEqual("收藏落库：备注进 Comment", entry.Comment, "项目");
        CheckEqual("收藏落库：图标进 IconPath", entry.IconPath, @"D:\work\项目");
        Check("收藏落库：勾了就在附加菜单里显示", entry.ShowInMiddleMenu);
        Check("收藏落库：默认启用", entry.Enabled);

        // 关键字被清空时用兜底值，绝不落一条空关键字（那样在 Qbar 里搜不到）
        var blank = new FavoriteDraft { Target = @"D:\x", Keyword = "", FallbackKeyword = "fx" };
        CheckEqual("收藏落库：空关键字用兜底值", blank.ToEntry(false).Key, "fx");

        // ── ⑩ 上下文快照：没有目标时也要能显示 ───────────────────────
        var empty = FavoriteTarget.Empty;
        Check("收藏上下文：空快照不抛异常", empty.SourceLabel.Length > 0, empty.SourceLabel);
        CheckEqual("收藏上下文：空快照是 Unknown", empty.Source, FavoriteSource.Unknown);

        var browser = new FavoriteTarget
        {
            Source = FavoriteSource.Browser,
            ProcessName = "chrome.exe",
        };
        Check("收藏上下文：网页来源的标签里有浏览器名",
            browser.SourceLabel.Contains("Chrome", StringComparison.OrdinalIgnoreCase), browser.SourceLabel);
    }
}
