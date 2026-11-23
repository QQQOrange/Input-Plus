using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InputPlus.Core;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;

namespace InputPlus;

public static partial class SelfTest
{
    /// <summary>
    /// Qbar 语义层（翻译 / 优先级 / 结构化检索 / 命令 / 历史 / 拼音）的底线用例。
    ///
    /// 这一层最容易出的错不是崩溃，而是"看起来在工作"：doc: 少展开两个扩展名、
    /// 否定词被当成普通词、规则把不该藏的文件藏了、占位符多替换了一层。所以每条
    /// 断言都直接打在解析结果上，而不是"跑完没抛异常"。
    /// </summary>
    private static void TestQbarSemantics()
    {
        TestTranslator();
        TestPriorityRules();
        TestSpecSearchAndEngine();
        TestCommandStore();
        TestDictionary();
        TestHistoryLog();
        TestPinyinFallback();

    }

    private static QbarTranslateResult Translate(string text, QbarFilterCatalog catalog)
        => QbarQueryTranslator.Translate(text, catalog, new DateTime(2026, 7, 15, 10, 0, 0));

    /// <summary>
    /// 在"当前发布的夹具索引"上跑一次结构化查询（谓词全部下推 + 本地复核）。
    /// 原来这一层是 <c>NativeSearchIndex.SearchSpecInForTest</c>；索引换了内核，
    /// 但断言仍然打在"谓词有没有生效"上，不是"哪个类有没有被调用"。
    /// </summary>
    private static IReadOnlyList<FileSearchHit> SearchSpecFixture(QbarSearchSpec spec, int limit)
    {
        QbarTantivyBackend.TryRun(spec, limit, out List<FileSearchHit> hits, out _, out _);
        return hits;
    }

    private static void TestTranslator()
    {
        QbarFilterCatalog catalog = QbarFilterCatalog.Create(null);

        /* ---- 用例 4：doc: 展开成 49 类文档扩展名 ---- */
        QbarTranslateResult doc = Translate("doc:", catalog);
        Check("语义层：doc: 展开成 49 类文档扩展名", doc.Spec.Extensions.Count == 49,
            "ext=" + doc.Spec.Extensions.Count);
        Check("语义层：doc: 记录命中的过滤器 Id",
            doc.Spec.AppliedFilterIds.Contains(QbarFilterCatalog.IdDoc),
            string.Join(",", doc.Spec.AppliedFilterIds));

        /* ---- 用例 5：过滤器 + 普通词叠加 ---- */
        QbarTranslateResult docReport = Translate("doc: 报表", catalog);
        Check("语义层：doc: 报表 同时带扩展名与普通词",
            docReport.Spec.Extensions.Count == 49 && docReport.Spec.PlainQuery == "报表",
            docReport.Spec.Describe());

        /* ---- 用例 6：组合谓词 ---- */
        QbarTranslateResult combo = Translate("ext:pdf;docx size:>1mb", catalog);
        Check("语义层：ext:pdf;docx 解析成两个扩展名",
            combo.Spec.Extensions.Count == 2 && combo.Spec.Extensions.Contains("pdf") && combo.Spec.Extensions.Contains("docx"),
            combo.Spec.Describe());
        Check("语义层：size:>1mb 解析成 1MB 下界",
            combo.Spec.MinSize == 1024L * 1024L && combo.Spec.MaxSize < 0,
            combo.Spec.MinSize + ".." + combo.Spec.MaxSize);

        QbarTranslateResult range = Translate("size:10mb..1gb", catalog);
        Check("语义层：size:10mb..1gb 解析成上下界",
            range.Spec.MinSize == 10L * 1024 * 1024 && range.Spec.MaxSize == 1024L * 1024 * 1024,
            range.Spec.MinSize + ".." + range.Spec.MaxSize);

        /* ---- 用例 7：date:week ---- */
        QbarTranslateResult week = Translate("date:week", catalog);
        int today = QbarSyntaxValues.ToDay(new DateTime(2026, 7, 15));
        /* date:week 翻成 dm:thisweek，也就是"本周"（周一为周首）而不是滚动 7 天：
         * 2026-07-15 是周三，本周一是 07-13，比今天早 2 天。 */
        Check("语义层：date:week 覆盖本周（周一到周日）",
            week.Spec.MinDay == today - 2 && week.Spec.MaxDay == today + 4,
            week.Spec.MinDay + ".." + week.Spec.MaxDay + "（今天 " + today + "）");
        Check("语义层：date:week 翻成 dm:thisweek",
            QbarQueryTranslator.ToDisplayDate("week") == "thisweek",
            QbarQueryTranslator.ToDisplayDate("week"));

        /* ---- 用例 8：否定 ---- */
        QbarTranslateResult neg = Translate("-ext:tmp", catalog);
        Check("语义层：-ext:tmp 解析成否定扩展名",
            neg.Spec.NegExtensions.Count == 1 && neg.Spec.NegExtensions.Contains("tmp"),
            neg.Spec.Describe());

        /* ---- 用例 9：短语不被空格拆开 ---- */
        QbarTranslateResult phrase = Translate("\"年度 总结\"", catalog);
        Check("语义层：带空格短语整体保留",
            phrase.Spec.Phrases.Count == 1 && phrase.Spec.Phrases[0] == "年度 总结" && phrase.Spec.Terms.Count == 0,
            phrase.Spec.Describe());

        /* ---- 用例 10：特殊字符当普通词处理，不崩 ---- */
        bool specialOk = true;
        string specialDetail = "";
        foreach (string special in new[] { "(", "a|b", "!", "$x", "*" })
        {
            try
            {
                QbarTranslateResult r = Translate(special, catalog);
                if (r.Spec.Terms.Count == 0 && r.Spec.Phrases.Count == 0)
                {
                    specialOk = false;
                    specialDetail = special + " 没有留下任何词";
                    break;
                }
            }
            catch (Exception ex)
            {
                specialOk = false;
                specialDetail = special + " 抛了 " + ex.GetType().Name;
                break;
            }
        }
        Check("语义层：特殊字符当普通词处理不崩", specialOk, specialDetail);

        /* ---- 降级：不认识的谓词降级成普通词并在调试面板标黄 ---- */
        QbarTranslateResult degraded = Translate("foo:bar", catalog);
        bool warned = degraded.Stages.Any(s => s.Warn);
        Check("语义层：不认识的谓词降级并标记警告",
            warned && degraded.Spec.PlainQuery.Contains("foo:bar"),
            degraded.Spec.Describe());

        /* ---- 归一化：全角冒号 / 全角空格 ---- */
        string normalized = QbarQueryTranslator.Normalize("doc：　报表");
        Check("语义层：全角冒号与全角空格归一化",
            normalized.StartsWith("doc:", StringComparison.Ordinal) && normalized.IndexOf('　') < 0,
            normalized);

        /* ---- 内置过滤器表规模（用户需求 2.4 节钉死的数量） ---- */
        QbarFilterCatalog builtins = QbarFilterCatalog.Create(null);
        Check("语义层：内置 doc 过滤器 49 项",
            ExtCount(builtins, "doc") == 49, "doc=" + ExtCount(builtins, "doc"));
        Check("语义层：内置 pic 过滤器 16 项",
            ExtCount(builtins, "pic") == 16, "pic=" + ExtCount(builtins, "pic"));
        Check("语义层：folder: 只搜目录",
            Translate("folder:", catalog).Spec.FoldersOnly,
            Translate("folder:", catalog).Spec.Describe());

        /* ---- 「关键字 + 空格」直接触发过滤器 ----
         * 面板按空格那一瞬间问的就是 ResolveFilterTrigger：doc / doc: / DOC 都要认，
         * 但 "doc 报表"（已经带搜索词）和 "report"（普通词）必须返回 null ——
         * 否则用户打 report 按空格会莫名其妙只剩文档。 */
        var triggerOptions = new QbarOptions();
        Check("过滤器触发：doc 按空格就命中内置过滤器",
            QbarService.ResolveFilterTrigger("doc", triggerOptions)?.Id == QbarFilterCatalog.IdDoc);
        Check("过滤器触发：doc: 老写法仍然命中",
            QbarService.ResolveFilterTrigger("doc:", triggerOptions)?.Id == QbarFilterCatalog.IdDoc);
        Check("过滤器触发：大小写不敏感",
            QbarService.ResolveFilterTrigger("DOC", triggerOptions)?.Id == QbarFilterCatalog.IdDoc);
        Check("过滤器触发：带搜索词时不抢空格",
            QbarService.ResolveFilterTrigger("doc 报表", triggerOptions) == null);
        Check("过滤器触发：普通词不会被当成过滤器",
            QbarService.ResolveFilterTrigger("report", triggerOptions) == null);
        Check("过滤器触发：folder 仍然只搜目录",
            QbarService.ResolveFilterTrigger("folder", triggerOptions)?.SearchForFolders == true);

        /* ---- 自定义过滤器覆盖内置同名关键字，内置项不可删 ---- */
        var custom = new QbarFilter { Id = "u.doc2", Keyword = "doc", Name = "我的文档", Extensions = "md;txt" };
        QbarFilterCatalog withUser = QbarFilterCatalog.Create(new[] { custom });
        QbarFilter? found = withUser.TryFind("doc");
        /* 同名关键字合并成一条：Id 沿用内置的（"恢复默认"要知道覆盖的是谁），
         * 扩展名与名字取用户的；doc 这个关键字只出现一次，不会变成两个 chip。 */
        int docCount = 0;
        foreach (QbarFilter item in withUser.All)
            if (string.Equals(item.Keyword, "doc", StringComparison.OrdinalIgnoreCase)) docCount++;
        Check("语义层：用户过滤器覆盖同名内置关键字",
            found != null && found.Id == QbarFilterCatalog.IdDoc && found.Extensions == "md;txt"
            && found.Name == "我的文档" && docCount == 1,
            found == null ? "没有命中" : found.Id + "/" + found.Extensions + "/" + docCount + " 条");

        /* ---- 改内置过滤器的关键字：认 Id 不认关键字 ----
         * 用户报了两个 bug：① 把文档的关键字改成 WD，列表里还显示 doc；
         * ② 改完变成"新增一条"。根因都是合并时只按关键字找，
         * doc → WD 之后找不到内置那条。这里钉住"按 Id 认领"。 */
        var renamed = new QbarFilter
        {
            Id = QbarFilterCatalog.IdDoc,
            Keyword = "WD",
            Name = "文档",
            Extensions = QbarFilterCatalog.DocExtensions,
            SearchForFiles = true,
            Enabled = true,
        };
        QbarFilterCatalog renameCatalog = QbarFilterCatalog.Create(new[] { renamed });
        int wdCount = renameCatalog.All.Count(f => string.Equals(f.Keyword, "wd", StringComparison.OrdinalIgnoreCase));
        Check("语义层：改内置关键字后仍然只有 7 条内置过滤器（不会多出一条）",
            renameCatalog.All.Count == 7 && wdCount == 1,
            renameCatalog.All.Count + " 条 / wd " + wdCount + " 条");
        /* 旧关键字必须还能用：真实配置里有人把 doc 改成 wd，改完 doc: 就查不到东西了。
         * 别名只挂在内置 Id 上，改名后 doc / wd 指向同一条内置过滤器。 */
        Check("语义层：改内置关键字后，旧关键字 doc 仍指向同一条（别名）",
            renameCatalog.TryFind("doc")?.Id == QbarFilterCatalog.IdDoc
            && ReferenceEquals(renameCatalog.TryFind("doc"), renameCatalog.TryFind("wd")),
            renameCatalog.TryFind("doc")?.Id ?? "doc 已失效");
        QbarTranslateResult aliasDoc = Translate("doc:", renameCatalog);
        Check("语义层：rename 之后 doc: 仍然展开成 49 个扩展名",
            aliasDoc.Spec.Extensions.Count == 49 && !aliasDoc.Stages.Any(s => s.Stage == "degrade"),
            aliasDoc.Spec.Extensions.Count + " exts");
        var renameOptions = new QbarOptions();
        renameOptions.Filters.Add(renamed);
        Check("过滤器触发：改成 WD 之后输入 WD 按空格命中同一条过滤器",
            QbarService.ResolveFilterTrigger("WD", renameOptions)?.Id == QbarFilterCatalog.IdDoc);

        /* 「只看正文」：两种写法都要落到 ContentOnly 上，但词放的位置不同 ——
         *   content: 报表  → 过滤器形态（词在 Terms 里）
         *   content:报表   → 谓词形态（词在 ContentTerms 里）
         * 后端两种都认，所以这里把"词在哪"也钉住，免得以后只剩一种能用。 */
        QbarTranslateResult contentOnly = Translate("content: 报表", QbarFilterCatalog.Create(null));
        Check("语义层：content: 空值 = 只看正文（过滤器形态）",
            contentOnly.Spec.ContentOnly && contentOnly.Spec.Terms.Count == 1
            && contentOnly.Spec.Terms[0] == "报表" && contentOnly.Spec.ContentTerms.Count == 0,
            "only=" + contentOnly.Spec.ContentOnly + " terms=" + contentOnly.Spec.Terms.Count);

        QbarTranslateResult contentWord = Translate("content:报表", QbarFilterCatalog.Create(null));
        Check("语义层：content:词 = 这个词只查正文（谓词形态）",
            contentWord.Spec.ContentOnly && contentWord.Spec.ContentTerms.Count == 1
            && contentWord.Spec.Terms.Count == 0,
            "terms=" + contentWord.Spec.Terms.Count + " content=" + contentWord.Spec.ContentTerms.Count);

        /* ---- 停用内置过滤器：开关必须真的关掉它 ----
         * 合并时跳过停用项会让 Enabled 强制为 true，设置页那个开关就变成"点了没用"。 */
        var disabled = new QbarFilter
        {
            Id = QbarFilterCatalog.IdDoc,
            Keyword = "wd",
            Name = "文档",
            Extensions = QbarFilterCatalog.DocExtensions,
            SearchForFiles = true,
            Enabled = false,
        };
        QbarFilterCatalog disabledCatalog = QbarFilterCatalog.Create(new[] { disabled });
        QbarFilter? disabledDoc = disabledCatalog.TryFind("doc");
        Check("语义层：停用内置过滤器后它仍在列表里且 Enabled=false",
            disabledCatalog.All.Count == 7 && disabledDoc != null && !disabledDoc.Enabled,
            disabledCatalog.All.Count + " 条 / enabled=" + (disabledDoc?.Enabled.ToString() ?? "null"));
        QbarTranslateResult disabledDocResult = Translate("doc: 报表", disabledCatalog);
        Check("语义层：停用后 doc: 整条忽略（不展开扩展名、也不降级成普通词）",
            disabledDocResult.Spec.Extensions.Count == 0
            && !disabledDocResult.Stages.Any(s => s.Stage == "degrade")
            && disabledDocResult.Spec.Terms.Count == 1,
            disabledDocResult.IndexText);
    }

    private static int ExtCount(QbarFilterCatalog catalog, string keyword)
    {
        QbarFilter? filter = catalog.TryFind(keyword);
        if (filter == null) return -1;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        QbarFilterCatalog.FillExtensions(filter.Extensions, set);
        return set.Count;
    }

    private static void TestPriorityRules()
    {
        QbarPriorityResolver resolver = QbarPriorityResolver.Create(true, true, null);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        QbarPriorityDecision system = resolver.Resolve(Path.Combine(windows, "System32", "drivers", "etc", "hosts"), false);
        Check("优先级：系统目录默认降权", system.Level == QbarPriorityLevel.Low,
            system.Level + " / " + system.Reason);

        QbarPriorityDecision normal = resolver.Resolve(@"D:\work\report.docx", false);
        Check("优先级：普通目录保持 Normal", normal.Level == QbarPriorityLevel.Normal,
            normal.Level.ToString());

        var rules = new List<QbarPriorityRule>
        {
            new QbarPriorityRule { Id = "r1", FolderPath = @"D:\temp", Level = "dontindex", Enabled = true },
            new QbarPriorityRule { Id = "r2", NameRegex = "^~", Level = "high", Enabled = true },
            new QbarPriorityRule { Id = "r3", FolderPath = @"D:\keep", Level = "ignored", Enabled = true },
            new QbarPriorityRule { Id = "r4", FolderPath = @"D:\off", Level = "dontindex", Enabled = false },
        };
        QbarPriorityResolver custom = QbarPriorityResolver.Create(true, true, rules);

        Check("优先级：用户规则可设 DoNotIndex",
            custom.Resolve(@"D:\temp\a.dll", false).Level == QbarPriorityLevel.DoNotIndex,
            custom.Resolve(@"D:\temp\a.dll", false).Level.ToString());
        Check("优先级：用户 High 规则覆盖内置降权",
            custom.Resolve(@"D:\work\~$report.docx", false).Level == QbarPriorityLevel.High,
            custom.Resolve(@"D:\work\~$report.docx", false).Level.ToString());
        Check("优先级：Ignored 与 DoNotIndex 是两个不同档",
            custom.Resolve(@"D:\keep\a.txt", false).Level == QbarPriorityLevel.Ignored,
            custom.Resolve(@"D:\keep\a.txt", false).Level.ToString());
        Check("优先级：禁用的规则不参与求值",
            custom.Resolve(@"D:\off\a.txt", false).Level == QbarPriorityLevel.Normal,
            custom.Resolve(@"D:\off\a.txt", false).Level.ToString());
        Check("优先级：规则集身份串稳定（同一份规则两次哈希相同）",
            custom.Identity == QbarPriorityResolver.Create(true, true, rules).Identity,
            custom.Identity);

        QbarPriorityResolver disabled = QbarPriorityResolver.Create(false, true, null);
        Check("优先级：总开关关闭后一律 Normal",
            disabled.Resolve(Path.Combine(windows, "notepad.exe"), false).Level == QbarPriorityLevel.Normal,
            disabled.Resolve(Path.Combine(windows, "notepad.exe"), false).Level.ToString());
    }

    private static void TestSpecSearchAndEngine()
    {
        string root = Path.Combine(Path.GetTempPath(), "inputplus-qbar-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            BuildIndexFixture(root);

            /* 引擎 / 拼音索引 / 模糊扫描走的都是"全局快照"，而 SearchSpecInForTest
             * 只查临时快照。这里显式把夹具树发布成全局快照，后面几组用例才有数据可查。 */
            int published = Core.Qbar.QbarIndex.PublishForTest(root);
            Check("结构化检索：夹具树可发布成全局快照", published > 0, "发布 " + published + " 个文件");

            /* ---- 结构化检索：扩展名白名单 ---- */
            var docxSpec = new QbarSearchSpec();
            docxSpec.Extensions.Add("docx");
            IReadOnlyList<FileSearchHit> docxHits = SearchSpecFixture(docxSpec, 10);
            Check("结构化检索：只返回 docx",
                docxHits.Count > 0 && docxHits.All(h => h.Path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)),
                DescribeHits(docxHits));

            /* ---- 否定扩展名 ---- */
            /* 否定扩展名必须配"纯谓词"来验：一旦给了 Terms，索引只按第一个词探名字，
             * 而夹具里带 filler 的全是 .txt —— 剔干净以后当然是 0 条。 */
            var noTxt = new QbarSearchSpec();
            noTxt.NegExtensions.Add("txt");
            IReadOnlyList<FileSearchHit> noTxtHits = SearchSpecFixture(noTxt, 20);
            Check("结构化检索：否定扩展名不返回 txt",
                noTxtHits.Count > 0 && noTxtHits.All(h => !h.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)),
                "命中 " + noTxtHits.Count + " 条");

            /* ---- 只搜目录 ---- */
            var dirsOnly = new QbarSearchSpec { FoldersOnly = true };
            IReadOnlyList<FileSearchHit> dirHits = SearchSpecFixture(dirsOnly, 20);
            Check("结构化检索：folder: 只返回目录",
                dirHits.Count > 0 && dirHits.All(h => h.IsDir), DescribeHits(dirHits));

            /* ---- 大小下界：夹具文件全是几十字节，1MB 下界必须一条不剩 ---- */
            var big = new QbarSearchSpec { MinSize = 1024L * 1024L };
            IReadOnlyList<FileSearchHit> bigHits = SearchSpecFixture(big, 10);
            Check("结构化检索：size:>1mb 滤掉夹具里的小文件", bigHits.Count == 0, "命中 " + bigHits.Count + " 条");

            /* ---- 时间下界：夹具刚建好，今天之后仍然命中 ---- */
            var recent = new QbarSearchSpec { MinDay = QbarSyntaxValues.ToDay(DateTime.Now) };
            IReadOnlyList<FileSearchHit> recentHits = SearchSpecFixture(recent, 20);
            Check("结构化检索：date:today 命中刚创建的文件", recentHits.Count > 0, "命中 " + recentHits.Count + " 条");

            /* ---- 引擎：优先级剔除 + 隐藏计数 + 高亮 ----
             * 上一步已经把夹具树发布成当前索引，引擎这里直接吃同一份。 */
            var context = new QbarQueryContext();
            context.Rules.Add(new QbarPriorityRule
            {
                Id = "test.dni",
                FolderPath = Path.Combine(root, "批量"),
                Level = "dontindex",
                Enabled = true,
            });

            QbarSearchOutcome outcome = QbarSearchEngine.Run(context, "filler", 30);
            /* 夹具里 "filler" 只出现在 批量 目录下，所以 DNI 生效后应当一条不剩。
             * 同时确认"没有规则时确实有命中"，否则这条用例可能只是查询打空了。 */
            var openBook = new QbarQueryContext();
            QbarSearchOutcome unfiltered = QbarSearchEngine.Run(openBook, "filler", 30);
            string offender = "";
            foreach (QbarSearchHit probe in outcome.Hits)
            {
                if (probe.Path.IndexOf("批量", StringComparison.Ordinal) < 0) continue;
                offender = probe.Path;
                break;
            }
            Check("引擎：DoNotIndex 目录的结果被彻底剔除",
                unfiltered.Hits.Count > 0 && outcome.Hits.Count == 0 && offender.Length == 0,
                "原始 " + unfiltered.Hits.Count + " 条 → 剔除后 " + outcome.Hits.Count +
                " 条，隐藏 " + outcome.HiddenDoNotIndex + " 条" +
                (offender.Length == 0 ? "" : "，漏网 " + offender));
            Check("引擎：剔除数量写进诊断", outcome.HiddenDoNotIndex > 0,
                "DoNotIndex=" + outcome.HiddenDoNotIndex);

            var clear = new QbarQueryContext();
            QbarSearchOutcome precise = QbarSearchEngine.Run(clear, "filler-001", 10);
            Check("引擎：命中区间返回给 UI 用于高亮",
                precise.Hits.Count > 0 && precise.Hits[0].Highlights.Count > 0,
                precise.Hits.Count == 0 ? "没有命中" : precise.Hits[0].Name + " / " + precise.Hits[0].Highlights.Count);

            Check("引擎：分面口径与来源一致（文案跟着 FacetsAreGlobal 走）",
                precise.Facets.Count == 8
                && precise.FacetSummary().StartsWith(precise.FacetsAreGlobal ? "全部：" : "本页：", StringComparison.Ordinal),
                precise.FacetSummary() + " · global=" + precise.FacetsAreGlobal);

            var folderRule = new QbarQueryContext();
            folderRule.Rules.Add(new QbarPriorityRule
            {
                Id = "test.high",
                FolderPath = Path.Combine(root, "批量", "00"),
                Level = "high",
                Enabled = true,
            });
            QbarSearchOutcome high = QbarSearchEngine.Run(folderRule, "filler", 30);
            Check("引擎：High 规则把目录前置",
                high.Hits.Count > 0 && high.Hits[0].Path.IndexOf(Path.Combine("批量", "00"), StringComparison.Ordinal) >= 0,
                high.Hits.Count == 0 ? "没有命中" : high.Hits[0].Path);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception ex) { CoreLog.Swallow(ex, "清理语义层夹具"); }
        }
    }

    private static void TestCommandStore()
    {
        var user = new List<QbarCommand>
        {
            new QbarCommand
            {
                Id = "u.ol", Keyword = "ol", Title = "打开测试程序",
                Path = "test.exe", Parameter = "-w \"{query}\"",
            },
        };
        List<QbarCommand> effective = QbarCommandStore.Effective(user);
        QbarCommand? matched = QbarCommandStore.Match(effective, "ol hello world", out string tail);
        Check("命令：首词命中命令并取出参数",
            matched != null && matched.Keyword == "ol" && tail == "hello world",
            matched == null ? "没有命中" : matched.Keyword + " / " + tail);

        Check("命令：{query} 一次替换不递归",
            QbarCommandStore.Expand("-w \"{query}\"", tail, @"C:\", @"C:\a\b.txt") == "-w \"hello world\"",
            QbarCommandStore.Expand("-w \"{query}\"", tail, @"C:\", @"C:\a\b.txt"));

        Check("命令：{action_path:parent} 取父目录",
            QbarCommandStore.Expand("{action_path:parent}", "", null, @"C:\a\b.txt") == @"C:\a",
            QbarCommandStore.Expand("{action_path:parent}", "", null, @"C:\a\b.txt"));

        Check("命令：{current_folder} 支持工作目录",
            QbarCommandStore.Expand("{current_folder}", "", @"D:\work", null) == @"D:\work",
            QbarCommandStore.Expand("{current_folder}", "", @"D:\work", null));

        Check("命令：模板里没有占位符时原样返回",
            QbarCommandStore.Expand("-n", "x", @"C:\", @"C:\a") == "-n",
            QbarCommandStore.Expand("-n", "x", @"C:\", @"C:\a"));

        var overrideCmd = new QbarCommand { Id = "u.cmd", Keyword = "cmd", Title = "我的 cmd", Path = "cmd.exe" };
        List<QbarCommand> overridden = QbarCommandStore.Effective(new[] { overrideCmd });
        Check("命令：用户同关键字覆盖内置命令",
            overridden.Count(c => string.Equals(c.Keyword, "cmd", StringComparison.OrdinalIgnoreCase)) == 1
            && overridden.First(c => string.Equals(c.Keyword, "cmd", StringComparison.OrdinalIgnoreCase)).Id == "u.cmd",
            string.Join(",", overridden.Select(c => c.Keyword + ":" + c.Id)));

        Check("命令：内置内部动作可识别",
            QbarCommandStore.IsInternalAction(QbarCommandStore.BuiltinNewFileId)
            && QbarCommandStore.IsInternalAction(QbarCommandStore.BuiltinCopyPathId)
            && !QbarCommandStore.IsInternalAction("builtin.cmd"),
            "newfile/copypath 应为内部动作");

        /* ol 必须是**开箱可用**的内置命令：本机没装欧路词典时也得有反应
         * （走网页词典），所以内置表的 Path 留空、由运行期解析。
         * 顺带守住「用户自建 ol 优先于内置 ol」这条覆盖规则。 */
        List<QbarCommand> bare = QbarCommandStore.Effective(null);
        QbarCommand? dict = bare.FirstOrDefault(c =>
            string.Equals(c.Keyword, "ol", StringComparison.OrdinalIgnoreCase));
        Check("命令：内置 ol 存在且是内置动作",
            dict != null && dict.Id == QbarCommandStore.BuiltinDictId
            && QbarCommandStore.IsInternalAction(dict!.Id),
            dict == null ? "没有内置 ol" : dict.Id);
        Check("命令：内置 ol 的 Path 留空交给运行期解析",
            dict != null && string.IsNullOrEmpty(dict.Path), dict?.Path);
        /* 参数必须写出来：欧路不带 -w 只会被打开、不会查词（用户反馈"参数是空的"）。 */
        Check("命令：内置 ol 的参数是 -w \"{query}\"",
            dict != null && dict.Parameter == QbarCommandStore.DefaultDictParameter,
            dict?.Parameter ?? "（空）");
        Check("命令：ol 的参数模板展开成 -w \"hello\"",
            QbarCommandStore.Expand(dict?.Parameter, "hello", null, null) == "-w \"hello\"",
            QbarCommandStore.Expand(dict?.Parameter, "hello", null, null));

        /* 老配置里 ol 的参数是空字符串（1.7.2 之前出厂没填）。Effective 要补回默认模板，
           否则升级上来的用户看到的还是空参数框、回车也不带词。 */
        var legacyOl = new QbarCommand
        {
            Id = QbarCommandStore.BuiltinDictId, Keyword = "ol", Title = "查词", Parameter = "",
        };
        QbarCommand? migratedOl = QbarCommandStore.Effective(new[] { legacyOl })
            .FirstOrDefault(c => string.Equals(c.Keyword, "ol", StringComparison.OrdinalIgnoreCase));
        Check("命令：老配置里空着的 ol 参数会补成默认模板",
            migratedOl != null && migratedOl.Parameter == QbarCommandStore.DefaultDictParameter,
            migratedOl?.Parameter ?? "（没找到）");

        /* 用户自建的 ol（Id 不是内置的）：老配置里参数也是空的。
           只按 Id 补默认值补不到它，所以 EnsureDefaults 还要认关键字。 */
        var userOl = new QbarCommand
        {
            Id = "user.ol", Keyword = "ol", Title = "我的查词", Path = "C:\\tools\\dict.exe", Parameter = "",
        };
        QbarCommand? effectiveUserOl = QbarCommandStore.Effective(new[] { userOl })
            .FirstOrDefault(c => string.Equals(c.Keyword, "ol", StringComparison.OrdinalIgnoreCase));
        Check("命令：用户自建的 ol 也会补上默认参数（按关键字认领）",
            effectiveUserOl != null
            && effectiveUserOl.Parameter == QbarCommandStore.DefaultDictParameter
            && effectiveUserOl.Path == "C:\\tools\\dict.exe",
            effectiveUserOl?.Parameter ?? "（没找到）");

        QbarCommand? dictMatched = QbarCommandStore.Match(bare, "ol hello", out string dictTail);
        Check("命令：ol 取到要查的词",
            dictMatched != null && dictMatched.Id == QbarCommandStore.BuiltinDictId && dictTail == "hello",
            dictMatched?.Id + " / " + dictTail);

        QbarCommandResult empty = QbarCommandStore.Execute(
            new QbarCommand { Id = "x", Keyword = "x", Title = "空路径" }, "", null, null);
        Check("命令：没有配置程序时给出明确文案而不是去 shell 找",
            !empty.Ok && empty.Message.Length > 0, empty.Message);

        /* 内置命令现在**可编辑**（m02881）：改关键字必须以 Id 为准替换，
         * 否则会同时留下「ol」和「dict」两条，用户按 ol 还能查到旧的。 */
        QbarCommand? seed = QbarCommandStore.Default(QbarCommandStore.BuiltinDictId);
        Check("命令：能按 Id 取到出厂定义", seed != null && seed!.Keyword == "ol", seed?.Keyword);
        Check("命令：默认值与自己相等（SameAsDefault）",
            seed != null && QbarCommandStore.SameAsDefault(seed!), "");

        if (seed != null)
        {
            var renamed = new QbarCommand
            {
                Id = seed.Id, Keyword = "dict", Title = seed.Title,
                Path = seed.Path, Parameter = seed.Parameter,
                WorkingDirectory = seed.WorkingDirectory,
                RunAsAdmin = seed.RunAsAdmin, Silent = seed.Silent,
            };
            Check("命令：改过关键字就不再等于默认值",
                !QbarCommandStore.SameAsDefault(renamed), renamed.Keyword);

            List<QbarCommand> afterRename = QbarCommandStore.Effective(new[] { renamed });
            Check("命令：内置改名后只留一条（按 Id 替换，不按关键字堆叠）",
                afterRename.Count(c => c.Id == seed.Id) == 1
                && afterRename.All(c => !string.Equals(c.Keyword, "ol", StringComparison.OrdinalIgnoreCase)),
                string.Join(",", afterRename.Select(c => c.Keyword + ":" + c.Id)));
            Check("命令：改名后按新关键字能命中",
                QbarCommandStore.Match(afterRename, "dict hello", out string renamedTail)?.Id == seed.Id
                && renamedTail == "hello", renamedTail);
            Check("命令：改名后旧关键字不再命中",
                QbarCommandStore.Match(afterRename, "ol hello", out _) == null, "ol 仍命中");
            Check("命令：内置 Id 判定",
                QbarCommandStore.IsBuiltin(seed.Id) && !QbarCommandStore.IsBuiltin("u.ol"), "");
        }
    }

    /// <summary>词典解析：纯离线断言，不联网（联网那条在手工验收里）。</summary>
    private static void TestDictionary()
    {
        /* 欧路词典定位：写死 Program Files 会漏掉装到 D:\software\eudic 的用户（真实事故），
           所以这里只断言"规则本身对"，不依赖本机装没装。 */
        Check("查词：快捷方式/文件名能认出欧路",
            EudicLocator.LooksLikeEudic("欧路词典 eudic.lnk")
            && EudicLocator.LooksLikeEudic("Eudic.exe")
            && !EudicLocator.LooksLikeEudic("notepad.exe"));
        Check("查词：非 exe 路径不算欧路", !EudicLocator.IsEudicExe(@"C:\Windows\notepad.exe"));
        string? eudic = EudicLocator.Find();
        Check("查词：找到的欧路路径一定真实存在", eudic == null || File.Exists(eudic), eudic ?? "（本机没装）");
        Check("查词：解析不存在的快捷方式返回 null 而不抛",
            ShortcutTarget.Resolve(@"C:\Windows\definitely-no-such-file.lnk") == null);

        const string en = "[{\"word\":\"report\",\"phonetic\":\"/rɪˈpɔːt/\","
            + "\"meanings\":[{\"partOfSpeech\":\"noun\",\"definitions\":"
            + "[{\"definition\":\"a spoken or written account of something\"},"
            + "{\"definition\":\"a sudden loud noise\"}]},"
            + "{\"partOfSpeech\":\"verb\",\"definitions\":"
            + "[{\"definition\":\"give a spoken or written account of\"}]}]}]";
        QbarDictionaryEntry? parsedEn = QbarDictionary.ParseEnglish("report", en);
        Check("查词：英文解析取到音标", parsedEn != null && parsedEn!.Phonetic.Contains("pɔːt"), parsedEn?.Phonetic);
        Check("查词：英文解析取到全部释义（跨词性）",
            parsedEn != null && parsedEn!.Senses.Count == 3,
            parsedEn?.Senses.Count.ToString());
        Check("查词：词性缩写成 n./v.", parsedEn != null
            && parsedEn!.Senses[0].PartOfSpeech == "n." && parsedEn.Senses[2].PartOfSpeech == "v.",
            parsedEn == null ? "" : string.Join("|", parsedEn.Senses.Select(x => x.PartOfSpeech)));

        const string zh = "{\"heteronyms\":[{\"bopomofo\":\"ㄧㄣˊ ㄏㄤˊ\",\"definitions\":"
            + "[{\"def\":\"<b>金融機構</b>的簡稱。&nbsp;\",\"type\":\"名詞\"}]}]}";
        QbarDictionaryEntry? parsedZh = QbarDictionary.ParseChinese("银行", zh);
        Check("查词：中文解析取到注音", parsedZh != null && parsedZh!.Phonetic.Contains("ㄧㄣˊ"), parsedZh?.Phonetic);
        Check("查词：中文释义去掉标签与实体",
            parsedZh != null && parsedZh!.Senses.Count == 1
            && parsedZh.Senses[0].Text == "金融機構的簡稱。" && parsedZh.Senses[0].PartOfSpeech == "名詞",
            parsedZh == null ? "" : parsedZh.Senses[0].Text);

        /* 中文词的主路是有道 jsonapi 的 newhh 段（《现代汉语规范词典》）：
           它同时认简体和繁体，带拼音 + 词性 + 中文释义，是"软件里真能查词"的关键。 */
        const string yd = "{\"newhh\":{\"source\":{\"name\":\"《现代汉语规范词典》\"},"
            + "\"dataList\":[{\"pinyin\":\"yínháng\",\"cat\":\"名词\",\"sense\":"
            + "[{\"def\":[\"经营存款、贷款等业务的金融机构。\"],\"cat\":\"名词\"}]}]},"
            + "\"simple\":{\"word\":[{\"phone\":\"yín háng\"}]},"
            + "\"baike\":{\"summarys\":[{\"summary\":\"银行是个多义词。\"}]}}";
        QbarDictionaryEntry? parsedZh2 = QbarDictionary.ParseYoudaoChinese("银行", yd);
        Check("查词：中文主路取到拼音与词性",
            parsedZh2 != null && parsedZh2!.Phonetic == "yínháng"
            && parsedZh2.Senses[0].PartOfSpeech == "名词", parsedZh2?.Phonetic);
        Check("查词：中文释义 + 百科摘要各占一条",
            parsedZh2 != null && parsedZh2!.Senses.Count == 2
            && parsedZh2.Senses[0].Text.Contains("金融机构")
            && parsedZh2.Senses[1].PartOfSpeech == "百科",
            parsedZh2 == null ? "" : string.Join("|", parsedZh2.Senses.Select(x => x.Text)));
        Check("查词：来源写清是哪本词典",
            parsedZh2 != null && parsedZh2!.Source.Contains("现代汉语"), parsedZh2?.Source);

        /* 简体中文词（银行）在萌典里是 404 —— 它只有繁体「銀行」。兜底走爱词霸的英译，
           这条断言守住"简体词也有东西可看"，不然用户会以为查词坏了。 */
        const string iciba = "{\"message\":[{\"key\":\"银行\",\"paraphrase\":\"bank\","
            + "\"means\":[{\"part\":\"\",\"means\":[\"bank\",\"banking house\"]}]}]}";
        QbarDictionaryEntry? parsedIciba = QbarDictionary.ParseIciba("银行", iciba);
        Check("查词：简体中文走爱词霸英译兜底",
            parsedIciba != null && parsedIciba!.Ok && parsedIciba.Senses.Count == 2,
            parsedIciba == null ? "" : parsedIciba.Senses.Count.ToString());
        Check("查词：英译兜底标明来源不是中文释义",
            parsedIciba != null && parsedIciba!.Senses[0].PartOfSpeech == "英译",
            parsedIciba?.Senses[0].PartOfSpeech);

        List<QbarResult> rows = UI.Windows.QbarWindow.BuildDictionaryResults(parsedEn!);
        Check("查词：列表首行是词条本身", rows.Count == 4 && rows[0].Title == "report", rows.Count.ToString());
        Check("查词：释义行回车可复制（Payload 是释义原文）",
            rows[1].Payload == "a spoken or written account of something", rows[1].Payload);
        Check("查词：行归类是词典而不是算式（徽标不会写成「计算」）",
            rows.All(r => r.Kind == QbarKind.Dictionary) && rows[0].KindLabel != Loc.T("计算"),
            rows[0].KindLabel);
        Check("查词：查询失败也给一行可读文案",
            UI.Windows.QbarWindow.BuildDictionaryResults(
                new QbarDictionaryEntry("zzz", "", Array.Empty<QbarDictionarySense>(), "", "网络不可用"))[0]
                .Subtitle.Contains("网络不可用"), "");
    }

    private static void TestHistoryLog()
    {
        string dir = Path.Combine(Path.GetTempPath(), "inputplus-qbar-history-" + Guid.NewGuid().ToString("N"));
        QbarHistory.OverrideDirectoryForTest(dir);
        try
        {
            QbarHistory.Record("select", "a.txt", @"C:\x\a.txt", "a", 0);
            QbarHistory.Record("select", "b.txt", @"C:\x\b.txt", "b", 1);
            QbarHistory.Record("select", "a.txt", @"C:\x\a.txt", "a", 0);

            Check("历史：按选中事件追加记录", QbarHistory.Recent(10).Count >= 3,
                "最近 " + QbarHistory.Recent(10).Count + " 条");
            List<(string Name, string Path, int Count)> frequent = QbarHistory.Frequent(10);
            Check("历史：常用聚合按次数排序",
                frequent.Count > 0 && frequent[0].Path == @"C:\x\a.txt" && frequent[0].Count >= 2,
                frequent.Count == 0 ? "没有聚合结果" : frequent[0].Path + " × " + frequent[0].Count);

            string month = DateTime.Now.ToString("yyyy-MM");
            Check("历史：按月一个 JSONL 文件",
                File.Exists(Path.Combine(dir, "events-" + month + ".jsonl")),
                Path.Combine(dir, "events-" + month + ".jsonl"));

            string[] lines = File.ReadAllLines(Path.Combine(dir, "events-" + month + ".jsonl"));
            Check("历史：每行一条 JSON 事件",
                lines.Length >= 3 && lines[0].StartsWith("{", StringComparison.Ordinal) && lines[0].Contains("\"kind\""),
                lines.Length == 0 ? "空文件" : lines[0]);
        }
        catch (Exception ex)
        {
            Check("历史：读写历史事件不抛异常", false, ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            QbarHistory.OverrideDirectoryForTest(null);
            try { Directory.Delete(dir, true); } catch (Exception ex) { CoreLog.Swallow(ex, "清理历史夹具"); }
        }
    }

    private static void TestPinyinFallback()
    {
        /* 未就绪时必须老实降级：查询为空，界面靠 PinyinPending 提示"正在建立"。 */
        QbarPinyinIndex.Invalidate();
        Check("拼音：索引未就绪时查询返回空而不是抛异常",
            !QbarPinyinIndex.Ready && QbarPinyinIndex.Query("sjg", 5).Count == 0,
            QbarPinyinIndex.Status);

        /* 此时快照还是 TestSpecSearchAndEngine 建好的夹具树（含 设计稿 / 报告-2026）。 */
        QbarPinyinIndex.Start();
        int waited = 0;
        while (QbarPinyinIndex.Building && waited < 10000)
        {
            System.Threading.Thread.Sleep(50);
            waited += 50;
        }

        Check("拼音：CJK 名字索引构建完成", QbarPinyinIndex.Ready,
            QbarPinyinIndex.Status + "（等 " + waited + "ms）" + QbarPinyinIndex.DebugSummary);

        List<QbarPinyinHit> hits = QbarPinyinIndex.Query("sjg", 5);
        Check("拼音：sjg 命中 设计稿 相关名字",
            hits.Count > 0 && hits.Any(h => h.Path.IndexOf("设计稿", StringComparison.Ordinal) >= 0),
            hits.Count == 0 ? "0 条" + QbarPinyinIndex.DebugSummary : hits[0].Path);

        List<QbarPinyinHit> full = QbarPinyinIndex.Query("shejigao", 5);
        Check("拼音：全拼 shejigao 也能命中",
            full.Count > 0 && full.Any(h => h.Path.IndexOf("设计稿", StringComparison.Ordinal) >= 0),
            full.Count == 0 ? "0 条" : full[0].Path);

        /* 子序列兜底：索引只认 2-gram 子串，chrme 这种缩写走模糊入口
         * （索引给出"每个字符都出现"的候选，子序列判定与打分只在 QbarNameScore 里算）。 */
        var fuzzyHits = new List<FileSearchHit>();
        foreach (IndexHit raw in QbarTantivyBackend.Fuzzy("chrme", 10, null))
        {
            int score = raw.Fuzzy == 2
                ? 120
                : QbarNameScore.Subsequence(Path.GetFileName(raw.Path), "chrme");
            if (score > 0) fuzzyHits.Add(new FileSearchHit(raw.Path, raw.IsDir == 1, score, 0, raw.Day, null));
        }
        Check("模糊：chrme 子序列命中 chrome 相关文件",
            fuzzyHits.Count > 0 && fuzzyHits.Any(h => Path.GetFileName(h.Path).IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0),
            DescribeHits(fuzzyHits));
    }
}
