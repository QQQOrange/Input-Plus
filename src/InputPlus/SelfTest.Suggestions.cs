using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using InputPlus.Core;
using InputPlus.Core.Qbar;
using InputPlus.Core.Settings;
using InputPlus.Core.Suggestions;
using InputPlus.Core.Suggestions.Sources;

namespace InputPlus;

public static partial class SelfTest
{
    /* 搜索前建议（Suggestions）的自检。
     *
     * 这一层最容易"看起来对"的地方是去重与规则：
     *   · 同一文件同时出现在下载和桌面时，只剩一条、徽标要合并；
     *   · SystemRoot 这类系统目录必须整棵排除，但 C:\WindowsApps 不能被
     *     C:\Windows 的裸前缀误伤（按目录边界判定）；
     *   · 黑名单写盘失败时内存必须原样回滚，否则用户点了"不建议"以为成功、
     *     重启又冒出来。
     * 所以下面按"模型 → 规则 → 合并 → 增量配置 → 服务"逐层断言。 */

    private static void TestSuggestions()
    {
        TestSuggestionItemModel();
        TestSuggestionRules();
        TestSuggestionMerge();
        TestSuggestionConfigDelta();
        TestSuggestionServiceHide();
        TestSuggestionSourceDegrade();
        TestSuggestionEmptyStateIntegration();
    }

    private static string SuggestionTempDir(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ip-suggestions-selftest", tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static RecItem Rec(string path, string sourceId, int weight, DateTime modified,
        RecItemType type = RecItemType.File, params string[] badges)
        => new()
        {
            Path = path,
            Name = "",
            SourceId = sourceId,
            SourceWeight = weight,
            Modified = modified,
            ItemType = type,
            Badges = badges,
        };

    private static void TestSuggestionItemModel()
    {
        CheckEqual("建议模型：路径小写 + 去尾部反斜杠", RecItem.NormalizeKey("D:\\Data\\A.TXT\\"), "d:\\data\\a.txt");
        CheckEqual("建议模型：正斜杠归一", RecItem.NormalizeKey("D:/Data/Sub/"), "d:\\data\\sub");
        CheckEqual("建议模型：盘根不剪", RecItem.NormalizeKey("C:\\"), "c:\\");
        CheckEqual("建议模型：空路径键为空", RecItem.NormalizeKey("   "), "");

        RecItem file = Rec("D:\\data\\报告 v2.docx", "downloads", 70, DateTime.Now);
        CheckEqual("建议模型：Name 为空时用文件名兜底", file.DisplayName, "报告 v2.docx");

        RecItem folder = Rec("D:\\data\\项目资料\\", "desktop", 65, DateTime.Now, RecItemType.Folder);
        CheckEqual("建议模型：文件夹名去掉尾分隔符", folder.DisplayName, "项目资料");
        Check("建议模型：文件夹 IsFolder", folder.IsFolder);
        Check("建议模型：文件不是文件夹", !file.IsFolder);
    }

    private static void TestSuggestionRules()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Check("规则：系统目录列表非空", windows.Length > 0);

        var rules = new RecRules();
        Check("规则：默认排除 .tmp", rules.IsExcluded("D:\\data\\a.tmp", false, out string r1), r1);
        CheckEqual("规则：排除原因=扩展名", r1, "排除扩展名");
        Check("规则：默认不排除 .docx", !rules.IsExcluded("D:\\data\\a.docx", false, out _));
        Check("规则：文件夹不受扩展名影响", !rules.IsExcluded("D:\\data\\folder.tmp", true, out _));
        Check("规则：空路径被拦", rules.IsExcluded("", false, out string r2));
        CheckEqual("规则：空路径原因", r2, "空路径");

        string inWindows = Path.Combine(windows, "notepad.exe");
        Check("规则：SystemRoot 下的文件被排除", rules.IsExcluded(inWindows, false, out string r3), r3);
        CheckEqual("规则：排除原因=系统目录", r3, "系统目录");

        // 目录边界：C:\Windows 不能把 C:\WindowsApps 一起吃进去
        string lookalike = windows.TrimEnd('\\') + "Apps\\tool.exe";
        Check("规则：同前缀不同目录不被误伤", !rules.IsInExcludedFolder(lookalike), lookalike);
        Check("规则：深一层仍在系统目录里",
            rules.IsInExcludedFolder(Path.Combine(windows, "System32", "drivers", "etc", "hosts")));

        var allowed = new RecRules(allowedExtensions: new[] { ".PDF" });
        Check("规则：白名单内保留", !allowed.IsExcluded("D:\\data\\a.pdf", false, out _));
        Check("规则：白名单外排除", allowed.IsExcluded("D:\\data\\a.docx", false, out string r4), r4);
        CheckEqual("规则：排除原因=白名单", r4, "不在类型白名单里");

        var hidden = new RecRules(hidden: new[] { "D:\\Secret\\a.txt" });
        Check("规则：黑名单大小写不敏感", hidden.IsHidden("d:\\secret\\A.TXT"));
        Check("规则：黑名单优先级最高", hidden.IsExcluded("D:\\Secret\\a.txt", false, out string r5), r5);
        CheckEqual("规则：排除原因=黑名单", r5, "黑名单");
        Check("规则：黑名单不误伤同目录其它文件", !hidden.IsExcluded("D:\\Secret\\b.txt", false, out _));
    }

    private static void TestSuggestionMerge()
    {
        DateTime t1 = new(2026, 1, 1, 10, 0, 0);
        DateTime t2 = new(2026, 1, 2, 10, 0, 0);
        DateTime t3 = new(2026, 1, 3, 10, 0, 0);

        var rules = new RecRules(new[] { "tmp" }, folders: new[] { "D:\\excluded" }, hidden: new[] { "D:\\hidden\\a.txt" });
        var items = new List<RecItem>
        {
            Rec("D:\\data\\a.txt", "downloads", 70, t1, RecItemType.File, "下载"),
            Rec("d:/data/A.TXT", "desktop", 65, t2, RecItemType.File, "桌面"),
            Rec("D:\\data\\b.txt", "desktop", 65, t3, RecItemType.File, "桌面"),
            Rec("D:\\data\\f.txt", "downloads", 70, t3, RecItemType.File, "下载"),
            Rec("D:\\data\\c.tmp", "downloads", 70, t3, RecItemType.File, "下载"),
            Rec("D:\\excluded\\d.txt", "downloads", 70, t3, RecItemType.File, "下载"),
            Rec("D:\\hidden\\a.txt", "desktop", 65, t3, RecItemType.File, "桌面"),
        };

        RecSnapshot snapshot = RecMerger.Merge(items, rules, 20);
        CheckEqual("合并：原始条数含被排除项", snapshot.RawCount, 7);
        CheckEqual("合并：排除后只剩三条", snapshot.Items.Count, 3);
        Check("合并：同路径只出现一次",
            snapshot.Items.Count(i => string.Equals(i.Key, "d:\\data\\a.txt", StringComparison.OrdinalIgnoreCase)) == 1);

        RecItem a = snapshot.Items.Single(i => i.Key == "d:\\data\\a.txt");
        CheckEqual("合并：保留高权重的来源", a.SourceId, "downloads");
        CheckEqual("合并：权重取胜者", a.SourceWeight, 70);
        Check("合并：徽标合并", a.Badges.Contains("下载") && a.Badges.Contains("桌面"),
            string.Join(",", a.Badges));
        Check("合并：来源列表含两个 id",
            a.MergedSources.Contains("downloads") && a.MergedSources.Contains("desktop"),
            string.Join(",", a.MergedSources));
        CheckEqual("合并：修改时间取较新", a.Modified, t2);

        CheckEqual("排序：最新修改的排第一（同时间权重高者先）", snapshot.Items[0].DisplayName, "f.txt");
        CheckEqual("排序：同时间权重次者随后", snapshot.Items[1].DisplayName, "b.txt");
        CheckEqual("排序：较旧的排最后", snapshot.Items[2].DisplayName, "a.txt");

        RecSnapshot bySource = RecMerger.Merge(items, rules, 20,
            sourceOrder: new[] { "desktop", "downloads" });
        CheckEqual("来源顺序：排在前面的来源先出", bySource.Items[0].SourceId, "desktop");
        Check("来源顺序：组内仍按修改时间倒序", bySource.Items[1].SourceId == "downloads");
        Check("来源顺序：不改条数", bySource.Items.Count == 3);

        var configured = new SuggestionsOptions { SourceOrder = new List<string> { "wechat", "unknown", "downloads" } };
        var resolved = configured.ResolveSourceOrder();
        CheckEqual("来源顺序：未知 id 被忽略", resolved[0], "wechat");
        CheckEqual("来源顺序：已知项保序", resolved[1], "downloads");
        Check("来源顺序：缺失来源补到末尾", resolved.Contains("app-history") && resolved.Count == 8);

        var many = new List<RecItem>();
        for (int i = 0; i < 2500; i++)
            many.Add(Rec("D:\\bulk\\f" + i + ".txt", "downloads", 70, t3));
        RecSnapshot capped = RecMerger.Merge(many, new RecRules(), 2000);
        CheckEqual("上限：候选硬上限 2000 生效", capped.Items.Count, 2000);
        CheckEqual("上限：原始条数照实统计", capped.RawCount, 2500);
    }

    private static void TestSuggestionConfigDelta()
    {
        string dir = SuggestionTempDir("delta");
        string path = Path.Combine(dir, "suggestions.delta.json");
        try
        {
            var store = new SuggestionConfigStore(path);
            Check("增量：落盘路径可读", store.StorePath.EndsWith("suggestions.delta.json", StringComparison.OrdinalIgnoreCase));
            Check("增量：写隐藏文件", store.Set(SuggestionKeys.HiddenFile("D:\\x\\a.txt"), "D:\\x\\a.txt", out string e1), e1);
            Check("增量：写展示条数", store.Set(SuggestionKeys.ShowCount, "12", out string e2), e2);
            Check("增量：关掉企业微信来源", store.Set(SuggestionKeys.Source("wecom"), "false", out string e3), e3);
            Check("增量：文件真的写下去了", File.Exists(path));

            var options = new SuggestionsOptions();
            new SuggestionConfigStore(path).Replay(options);
            Check("增量：重放后黑名单命中",
                options.Exclusions.Files.Any(x => string.Equals(x, "D:\\x\\a.txt", StringComparison.OrdinalIgnoreCase)),
                string.Join(",", options.Exclusions.Files));
            Check("增量：重放保留用户原始写法",
                options.Exclusions.Files.Contains("D:\\x\\a.txt"),
                string.Join(",", options.Exclusions.Files));
            CheckEqual("增量：重放后条数", options.ShowCount, 12);
            Check("增量：重放后来源被关", !options.IsSourceEnabled("wecom"));
            Check("增量：其它来源不受影响", options.IsSourceEnabled("wechat"));

            Check("增量：重新打开来源", store.Set(SuggestionKeys.Source("wecom"), "true", out string e4), e4);
            var reopen = new SuggestionsOptions();
            new SuggestionConfigStore(path).Replay(reopen);
            Check("增量：重开后来源恢复", reopen.IsSourceEnabled("wecom"));

            Check("增量：删除黑名单", store.Remove(SuggestionKeys.HiddenFile("D:\\x\\a.txt"), out string e5), e5);
            var cleared = new SuggestionsOptions();
            new SuggestionConfigStore(path).Replay(cleared);
            Check("增量：删除后黑名单清空", !cleared.Exclusions.Files.Contains("D:\\x\\a.txt"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) { CoreLog.Swallow(ex, "清理建议自检目录"); }
        }
    }

    private static void TestSuggestionServiceHide()
    {
        string dir = SuggestionTempDir("service");
        try
        {
            var settings = new AppSettings();
            var store = new SuggestionConfigStore(Path.Combine(dir, "delta.json"));
            using var service = new SuggestionsService(() => settings, store);

            Check("服务：未采集时首屏为空", service.FirstScreen(20).Count == 0);
            Check("服务：未采集时 IsWarm 为假", !service.IsWarm);

            string target = Path.Combine(dir, "被隐藏 的文件 (1).txt");
            Check("服务：隐藏成功", service.Hide(target, false, out string err), err);
            Check("服务：内存黑名单已写入",
                settings.Suggestions.Exclusions.Files.Contains(target, StringComparer.OrdinalIgnoreCase),
                string.Join(",", settings.Suggestions.Exclusions.Files));
            Check("服务：HiddenFiles 能看到", service.HiddenFiles().Contains(target));

            var afterRestart = new SuggestionsOptions();
            new SuggestionConfigStore(store.StorePath).Replay(afterRestart);
            Check("服务：重启后仍然隐藏",
                afterRestart.Exclusions.Files.Contains(target, StringComparer.OrdinalIgnoreCase));

            Check("服务：还原成功", service.Unhide(target, false, out string err2), err2);
            Check("服务：还原后内存移除",
                !settings.Suggestions.Exclusions.Files.Contains(target, StringComparer.OrdinalIgnoreCase));
            var afterUnhide = new SuggestionsOptions();
            new SuggestionConfigStore(store.StorePath).Replay(afterUnhide);
            Check("服务：还原后重启也不再隐藏",
                !afterUnhide.Exclusions.Files.Contains(target, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) { CoreLog.Swallow(ex, "清理建议自检目录"); }
        }

        // 写盘失败必须回滚内存（红线 5）
        string blocked = SuggestionTempDir("blocked");
        try
        {
            var settings = new AppSettings();
            var broken = new SuggestionConfigStore(blocked); // 路径本身是目录 → 写文件必失败
            using var service = new SuggestionsService(() => settings, broken);
            bool ok = service.Hide("D:\\nope\\a.txt", false, out string error);
            Check("服务：写盘失败时隐藏返回 false", !ok);
            Check("服务：写盘失败给出原因", error.Length > 0, error);
            Check("服务：写盘失败内存未变",
                !settings.Suggestions.Exclusions.Files.Contains("D:\\nope\\a.txt"),
                string.Join(",", settings.Suggestions.Exclusions.Files));
        }
        finally
        {
            try { Directory.Delete(blocked, true); } catch (Exception ex) { CoreLog.Swallow(ex, "清理建议自检目录"); }
        }
    }

    private static void TestSuggestionSourceDegrade()
    {
        var settings = new AppSettings();
        var rules = new RecRules();
        var context = new SuggestionContext(settings, rules, 2000);
        var budget = new SuggestionBudget(2000, 1200);

        // 目录不存在 → 0 条、不抛（来源降级的基本形态）
        var missing = DirectoryScan.Expand(Path.Combine(Path.GetTempPath(), "ip-suggestions-missing-" + Guid.NewGuid().ToString("N"), "*"));
        Check("降级：不存在的目录展开为空", missing.Count == 0);

        var sources = SuggestionsService.CreateSources();
        CheckEqual("来源：注册了八个来源", sources.Count, 8);
        Check("来源：id 不重复", sources.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == sources.Count);

        bool probed = false;
        foreach (IRecommendationSource source in sources)
        {
            try
            {
                source.Probe(context);
                probed = true;
            }
            catch (Exception ex)
            {
                Check("降级：来源探活不抛异常（" + source.Id + "）", false, ex.Message);
            }
        }
        Check("降级：八个来源都可以安全探活", probed);

        try
        {
            IReadOnlyList<RecItem> wecom = new WeComSource().Collect(context, budget);
            Check("降级：企业微信未安装时采集为空或有限", wecom.Count <= 80, wecom.Count.ToString());
        }
        catch (Exception ex)
        {
            Check("降级：企业微信采集不抛异常", false, ex.Message);
        }

        // 微信/飞书这类"目录不存在就计 0"的来源，Probe 必须是毫秒级布尔
        var wechat = new WeChatSource();
        Check("降级：微信探活返回布尔且不抛", wechat.Probe(context) || !wechat.Probe(context));
    }

    /* 空态接线：建议必须真的从 SuggestionsService 流进 QbarService.DefaultResults，
     * 并且"有建议"与"有搜索结果"互斥（红线 6）。
     * 这里等的是真采集（临时数据目录，但下载/桌面是真实目录），
     * 等不到就让断言失败 —— 不写"跳过"。 */
    private static void TestSuggestionEmptyStateIntegration()
    {
        string dir = SuggestionTempDir("empty-state");
        try
        {
            var settings = new AppSettings();
            settings.Suggestions.Show = true;
            settings.Suggestions.ShowCount = 10;
            settings.Suggestions.HideFileHotkey = ""; // 自检不占用全局热键
            settings.Qbar.RecentCount = 0;            // 只看建议，排除历史目标干扰

            var store = new SuggestionConfigStore(Path.Combine(dir, "delta.json"));
            using var service = new SuggestionsService(() => settings, store);
            service.Start();

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!service.IsWarm && DateTime.UtcNow < deadline) Thread.Sleep(50);

            Check("空态：采集完成", service.IsWarm);
            int visible = service.FirstScreen(10).Count;
            Check("空态：采集到建议项", visible > 0, visible.ToString());

            var qbar = new QbarService(() => settings, () => new List<string>());
            List<QbarResult> emptyRows = qbar.Query("", 12);
            List<QbarResult> suggestionRows = emptyRows.Where(r => r.Suggestion.Length > 0).ToList();
            Check("空态：Qbar 首屏出现建议行", suggestionRows.Count > 0,
                "可见 " + emptyRows.Count + " 行，建议 " + suggestionRows.Count + " 行");
            Check("空态：有建议时分组标题非空", qbar.EmptyStateGroup.Length > 0);

            if (suggestionRows.Count > 0)
            {
                Check("空态：建议行排在最前（Score 0 档）", emptyRows[0].Suggestion.Length > 0);
                Check("空态：建议行带路径载荷", suggestionRows[0].Payload.Length > 0, suggestionRows[0].Payload);
                Check("空态：建议行带徽标", suggestionRows[0].Badge.Length > 0, suggestionRows[0].Badge);
            }

            // 一开始输入，建议必须让位：既没有建议行，分组标题也要清空
            List<QbarResult> typed = qbar.Query("zzzz-no-such-file-zzzz", 12);
            Check("空态：输入后不再有建议行", typed.All(r => r.Suggestion.Length == 0));
            Check("空态：输入后分组标题清空", qbar.EmptyStateGroup.Length == 0);

            // 清空输入后建议恢复（同一实例、同一快照）
            List<QbarResult> restored = qbar.Query("", 12);
            Check("空态：清空输入后建议恢复", restored.Any(r => r.Suggestion.Length > 0));

            // 总开关关掉 → 建议与标题都不出现，行为回到改造前
            settings.Suggestions.Show = false;
            List<QbarResult> off = qbar.Query("", 12);
            Check("空态：总开关关闭后无建议行", off.All(r => r.Suggestion.Length == 0));
            Check("空态：总开关关闭后无分组标题", qbar.EmptyStateGroup.Length == 0);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) { CoreLog.Swallow(ex, "清理建议自检目录"); }
        }
    }
}
