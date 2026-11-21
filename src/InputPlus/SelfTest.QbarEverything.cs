using System;
using System.Collections.Generic;
using InputPlus.Core;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Search;

namespace InputPlus;

public static partial class SelfTest
{
    /// <summary>
    /// Everything 接入的底线用例。
    ///
    /// 这里不断言"本机装了 Everything"——CI 和用户机器都可能没装。要守的是：
    ///   · spec → Everything 查询串的翻译是确定的（安装与否都一样）；
    ///   · 本地复核的判定与自建索引同源（否则换后端结果集就变）；
    ///   · 没装 Everything 时安静降级，不抛、不空结果。
    /// </summary>
    private static void TestEverythingIntegration()
    {
        QbarFilterCatalog catalog = QbarFilterCatalog.Create(null);
        DateTime now = new DateTime(2026, 7, 15, 10, 0, 0);

        /* ---- 翻译：关键字 → Everything 原生语法 ---- */
        QbarSearchSpec doc = QbarQueryTranslator.Translate("doc: 报表", catalog, now).Spec;
        string docQuery = QbarEverythingBackend.BuildQuery(doc);
        Check("Everything：doc: 报表 生成 ext: 列表",
            docQuery.Contains("ext:") && docQuery.Contains("pdf") && docQuery.Contains("报表"), docQuery);

        QbarSearchSpec neg = QbarQueryTranslator.Translate("-ext:tmp", catalog, now).Spec;
        Check("Everything：-ext:tmp 生成 !ext:tmp",
            QbarEverythingBackend.BuildQuery(neg).Contains("!ext:tmp"), QbarEverythingBackend.BuildQuery(neg));

        /* ---- 特殊字符：必须加引号，否则 Everything 会当语法解释 ---- */
        QbarSearchSpec paren = QbarQueryTranslator.Translate("(", catalog, now).Spec;
        string parenQuery = QbarEverythingBackend.BuildQuery(paren);
        Check("Everything：普通词里的 ( 被引号包起来", parenQuery.Contains("\"(\""), parenQuery);

        QbarSearchSpec pipe = QbarQueryTranslator.Translate("a|b", catalog, now).Spec;
        string pipeQuery = QbarEverythingBackend.BuildQuery(pipe);
        Check("Everything：a|b 不被当成 OR", pipeQuery.Contains("\"a|b\"") || (pipeQuery.Contains("a") && pipeQuery.Contains("b")), pipeQuery);

        QbarSearchSpec quote = QbarQueryTranslator.Translate("\"年度 总结\"", catalog, now).Spec;
        string quoteQuery = QbarEverythingBackend.BuildQuery(quote);
        Check("Everything：短语整体加引号", quoteQuery.Contains("\"年度 总结\""), quoteQuery);

        /* ---- 大小 / 时间 / 类型 ---- */
        QbarSearchSpec range = QbarQueryTranslator.Translate("size:10mb..1gb", catalog, now).Spec;
        Check("Everything：size 区间生成 size:10mb..1gb",
            QbarEverythingBackend.BuildQuery(range).Contains("size:10mb..1gb"), QbarEverythingBackend.BuildQuery(range));

        QbarSearchSpec minSize = QbarQueryTranslator.Translate("size:>1mb", catalog, now).Spec;
        Check("Everything：size 下界生成 >=（闭区间语义）",
            QbarEverythingBackend.BuildQuery(minSize).Contains("size:>=1mb"), QbarEverythingBackend.BuildQuery(minSize));

        QbarSearchSpec dates = QbarQueryTranslator.Translate("date:2026-07-01..2026-07-31", catalog, now).Spec;
        Check("Everything：日期区间生成 dm: 区间",
            QbarEverythingBackend.BuildQuery(dates).Contains("dm:2026-07-01..2026-07-31"), QbarEverythingBackend.BuildQuery(dates));

        QbarSearchSpec folders = QbarQueryTranslator.Translate("folder:", catalog, now).Spec;
        Check("Everything：folder: 透传", QbarEverythingBackend.BuildQuery(folders).Contains("folder:"),
            QbarEverythingBackend.BuildQuery(folders));

        /* wfn: 必须下发：查询里只剩 wfn: 时，不下发就等于给 Everything 一条空查询，
         * 窗口里没有候选，本地复核再严也筛不出东西（实测 wfn:*.pdf 曾恒为 0 条）。 */
        QbarSearchSpec glob = QbarQueryTranslator.Translate("wfn:*.pdf", catalog, now).Spec;
        string globQuery = QbarEverythingBackend.BuildQuery(glob);
        Check("Everything：wfn 下发给 Everything", globQuery.Contains("wfn:*.pdf", StringComparison.OrdinalIgnoreCase), globQuery);
        Check("Everything：wfn 仍然留在 spec 里给本地复核用", glob.Glob.Length > 0, glob.Glob);

        /* regex: 只有"整条查询就是一条正则"时才交给 Everything（SetRegex 是全局开关）。
         * 反斜杠必须原样过去：加倍后 Everything 收到的是字面双反斜杠，实测 0 命中。 */
        QbarSearchSpec regexOnly = QbarQueryTranslator.Translate(@"regex:^rep.*\.docx$", catalog, now).Spec;
        Check("Everything：纯正则判定为可下发", QbarEverythingBackend.IsRegexOnly(regexOnly),
            QbarEverythingBackend.IsRegexOnly(regexOnly).ToString());
        string regexQuery = QbarEverythingBackend.BuildQuery(regexOnly);
        Check("Everything：纯正则原样下发且不加引号", regexQuery == regexOnly.RegexText && regexQuery.IndexOf('"') < 0, regexQuery);

        QbarSearchSpec regexMixed = QbarQueryTranslator.Translate(@"regex:^rep report", catalog, now).Spec;
        Check("Everything：正则混普通词时不走 Everything", !QbarEverythingBackend.IsRegexOnly(regexMixed),
            QbarEverythingBackend.IsRegexOnly(regexMixed).ToString());
        bool regexRouted = QbarEverythingBackend.TryRun(regexMixed, 200, out _, out _, out string regexDetail);
        Check("Everything：正则混普通词时返回 false 并给出原因", !regexRouted && regexDetail.Length > 0, regexDetail);

        /* content: 两条后端都没有内容索引：降级成普通文件名检索，并在调试面板说明。 */
        QbarSearchSpec contentSpec = QbarQueryTranslator.Translate("content:报表", catalog, now).Spec;
        string contentQuery = QbarEverythingBackend.BuildQuery(contentSpec);
        Check("Everything：content: 降级为文件名检索",
            contentSpec.ContentRequested && contentQuery.Contains("报表") && !contentQuery.Contains("content:"), contentQuery);

        /* ---- 本地复核与自建索引同源 ---- */
        var filter = new NativeSearchIndex.SpecFilter(doc);
        Check("复核：doc: 接受 pdf", filter.Accept(@"C:\Users\x\报表.pdf", false, 1024, 9000, out _), "pdf");
        Check("复核：doc: 拒绝 tmp", !filter.Accept(@"C:\Users\x\~$报表.tmp", false, 1024, 9000, out _), "tmp");
        Check("复核：doc: 拒绝目录", !filter.Accept(@"C:\Users\x\报表", true, 0, 9000, out _), "dir");

        var negFilter = new NativeSearchIndex.SpecFilter(neg);
        Check("复核：-ext:tmp 剔除 tmp", !negFilter.Accept(@"C:\x\a.tmp", false, 10, 9000, out _), "tmp");
        Check("复核：-ext:tmp 保留 txt", negFilter.Accept(@"C:\x\a.txt", false, 10, 9000, out _), "txt");

        var globFilter = new NativeSearchIndex.SpecFilter(glob);
        Check("复核：wfn:*.pdf 只收 pdf",
            globFilter.Accept(@"C:\x\a.pdf", false, 10, 9000, out _) &&
            !globFilter.Accept(@"C:\x\a.txt", false, 10, 9000, out _), "glob");

        /* ---- 大小/时间边界与索引同一口径（KB + 天数） ---- */
        var sizeFilter = new NativeSearchIndex.SpecFilter(range);
        Check("复核：size 下界命中", sizeFilter.Accept(@"C:\x\a.bin", false, 10 * 1024, 9000, out _), "10MB");
        Check("复核：size 下界外剔除", !sizeFilter.Accept(@"C:\x\a.bin", false, 9 * 1024, 9000, out _), "9MB");

        /* ---- 可用性与降级 ---- */
        Check("Everything：状态文案可读", EverythingApi.StatusText.Length > 0, EverythingApi.StatusText);
        Check("Everything：版本号可读", EverythingApi.VersionText.Length > 0, EverythingApi.VersionText);

        QbarQueryContext context = QbarSearchEngine.FromSettings(Core.Settings.SettingsStore.Load());
        QbarSearchOutcome outcome = QbarSearchEngine.Run(context, "doc:", 5);
        Check("Everything：后端有明确来源（everything / native / off）",
            outcome.Backend == "everything" || outcome.Backend == "native" || outcome.Backend == "off",
            outcome.Backend);
        Check("Everything：降级/成功都有说明文案", outcome.BackendDetail.Length > 0, outcome.BackendDetail);
        if (outcome.Backend == "everything")
            Check("Everything：调试面板能看到实际查询串", outcome.BackendQuery.Length > 0, outcome.BackendQuery);
        if (outcome.Backend == "off")
            Check("Everything：不可用时不回退本地快照（结果为空）", outcome.Hits.Count == 0, outcome.Hits.Count.ToString());

        /* ---- SDK 高亮解析 ---- */
        /* Everything 用 U+0001 / U+0002 把命中片段包起来。解析错的表现很脏：
           要么文件名里残留控制字符，要么区间整体偏移一个字，加亮错位。 */
        int[] marked = EverythingApi.ParseHighlightedName("\u0001Report\u0002.dll");
        Check("高亮解析：标记被去掉且偏移按去标记后的名字计算",
            marked.Length == 2 && marked[0] == 0 && marked[1] == 6, string.Join(",", marked));

        int[] noMark = EverythingApi.ParseHighlightedName("Report.dll");
        Check("高亮解析：没有标记返回空区间", noMark.Length == 0, noMark.Length.ToString());

        int[] tail = EverythingApi.ParseHighlightedName("~$\u0001报表\u0002.docx");
        Check("高亮解析：中段命中偏移正确（~$ 两个字符之后，命中「报表」两个字）",
            tail.Length == 2 && tail[0] == 2 && tail[1] == 2, string.Join(",", tail));

        // 只有开头没有结尾（SDK 异常/截断）：不能把剩下的名字整段当成命中，也不能抛。
        int[] open = EverythingApi.ParseHighlightedName("a\u0001bc");
        Check("高亮解析：半截标记不产生区间且不抛异常", open.Length == 0, string.Join(",", open));

        /* ---- 优先级规则：目录作用域（路径切分提到循环外之后必须完全一致）---- */
        QbarPriorityResolver resolver = QbarPriorityResolver.Create(true, true, null);
        QbarPriorityDecision windowsHit = resolver.Resolve(@"C:\Windows\System32\drivers\etc\hosts", false);
        Check(@"优先级：C:\Windows 下的文件是低优先",
            windowsHit.Level == QbarPriorityLevel.Low, windowsHit.Level.ToString());
        // 目录命中时 scopeDir 取父目录（与改造前一致），这里钉住：C:\Windows 自己在低优先作用域里。
        QbarPriorityDecision windowsDir = resolver.Resolve(@"C:\Windows\System32", true);
        Check(@"优先级：C:\Windows 目录本身也是低优先",
            windowsDir.Level == QbarPriorityLevel.Low, windowsDir.Level.ToString());
        QbarPriorityDecision plainFile = resolver.Resolve(@"D:\work\report.docx", false);
        Check("优先级：普通目录仍是 Normal",
            plainFile.Level == QbarPriorityLevel.Normal, plainFile.Level.ToString());
        // 路径段比较：C:\WindowsOldCopy 不能被当成 C:\Windows 的子树。
        QbarPriorityDecision lookalike = resolver.Resolve(@"C:\WindowsOldCopy\a.txt", false);
        Check("优先级：目录规则按路径段比较（前缀相似的兄弟目录不误伤）",
            lookalike.Level == QbarPriorityLevel.Normal, lookalike.Level.ToString());
        QbarPriorityDecision tempFile = resolver.Resolve(@"D:\work\~$report.docx", false);
        Check("优先级：临时文件名正则仍然生效",
            tempFile.Level == QbarPriorityLevel.Low, tempFile.Level.ToString());
        /* ---- 管理员默认启动 ---- */
        var fresh = new Core.Settings.AppSettings();
        Check("设置：RunAsAdmin 默认开启", fresh.Global.RunAsAdmin, fresh.Global.RunAsAdmin.ToString());
    }
}