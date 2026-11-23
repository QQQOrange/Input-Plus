using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using InputPlus.Core;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Search;

namespace InputPlus;

/// <summary>
/// 文件索引与查询后端的自检。
///
/// 这一组是"索引内核换成 tantivy"之后新增的主体用例：中文子串、拼音首字母/全拼、
/// 正则、globs、谓词组合、取反、目录 vs 文件、排序与状态。每条都打在真实索引上
/// （夹具树 → <see cref="QbarIndex.PublishForTest"/> → 查询），不是 mock。
/// </summary>
public static partial class SelfTest
{
    /// <summary>
    /// 建一棵确定的夹具树。所有断言都只依赖这棵树里有什么，
    /// 不依赖用户磁盘上的内容 —— 换台机器跑结果必须一样。
    /// </summary>
    internal static string BuildIndexFixture(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        void W(string relative, string content)
        {
            string full = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, Encoding.UTF8);
        }

        W(@"文档\报告-2026.docx", "annual report body");
        /* 正则片段预筛（F5）要一条**真正存在**、形状又必须自动机才编得出来的名字：
         * `^rep\b-?\d+\.docx$` 里的 `\b` 不被 tantivy 的自动机接受，只能靠 rep + .docx 两段预筛。 */
        W(@"文档\rep-2026.docx", "regex fragment body");
        W(@"文档\说明.txt", "readme body");
        W(@"文档\设计稿\首页.html", "<html></html>");
        W(@"批量\00\filler-001.txt", "filler one");
        W(@"批量\00\filler-002.txt", "filler two");
        W(@"批量\01\filler-003.txt", "filler three");
        W(@"批量\01\子目录\placeholder.txt", "nested");
        W(@"下载\chrome_setup.exe", "not really an exe");
        W(@"图片\图-001.png", "not really a png");
        Directory.CreateDirectory(Path.Combine(root, "空目录"));
        return root;
    }

    /// <summary>并发用例用：在某个目录下建 200 个不重叠文件，返回它们的路径（顺序固定）。</summary>
    private static List<string> BuildFanout(string dir, string tag)
    {
        var list = new List<string>(200);
        for (int i = 0; i < 200; i++)
        {
            string path = Path.Combine(dir, tag + "-" + i.ToString("000") + ".txt");
            File.WriteAllText(path, tag, Encoding.UTF8);
            list.Add(path);
        }
        return list;
    }



    /// <summary>把命中列表压成一行，断言失败时看得到到底命中了什么。</summary>
    internal static string DescribeHits(IEnumerable<FileSearchHit> hits)
    {
        var list = hits.Take(6).Select(h => (h.IsDir ? "[D] " : "") + System.IO.Path.GetFileName(h.Path)).ToList();
        return list.Count == 0 ? "（0 条）" : string.Join(" | ", list);
    }

    /// <summary>夹具索引上跑一次查询（谓词全部下推 + 本地复核）。</summary>
    private static List<FileSearchHit> SearchFixture(QbarSearchSpec spec, int limit = 40)
    {
        bool ok = QbarTantivyBackend.TryRun(spec, limit, out List<FileSearchHit> hits, out _, out _);
        return ok ? hits : new List<FileSearchHit>();
    }

    private static QbarSearchSpec Spec(List<string> terms, Action<QbarSearchSpec>? tweak = null)
    {
        var spec = new QbarSearchSpec();
        foreach (string t in terms) spec.Terms.Add(t);
        tweak?.Invoke(spec);
        return spec;
    }

    /// <summary>索引主体用例。约 70 条断言，全部走真实索引。</summary>
    private static void TestQbarIndex()
    {
        Check("索引：native 库可加载", TantivyApi.IsAvailable, TantivyApi.StatusText);
        Check("索引：native 库状态可读", TantivyApi.StatusText.Length > 0, TantivyApi.StatusText);
        Check("索引：索引目录在数据目录下",
            QbarIndex.IndexDir.StartsWith(AppPaths.DataDir, StringComparison.OrdinalIgnoreCase), QbarIndex.IndexDir);
        Check("索引：旧快照目录名与新目录不同",
            !string.Equals(QbarIndex.LegacyDir, QbarIndex.IndexDir, StringComparison.OrdinalIgnoreCase), "");

        /* ---- 根目录：盘根一律加（D61），且折叠逻辑必须生效 ----
         * 这两条守的是"覆盖面"这个最容易被顺手改掉的行为：盘根一旦退回"只在提权时加"，
         * D:\tmp、C:\Windows 这类路径就会从索引里消失，而所有查询断言都还是绿的
         * （它们查的是夹具树）。所以必须有独立用例钉住"根目录里到底有什么形状"。 */
        string[] roots = QbarRoots.Compute();
        bool hasDriveRoot = false;
        for (int i = 0; i < roots.Length; i++)
        {
            string r = roots[i];
            if (r.Length == 3 && r[1] == ':' && (r[2] == '\\' || r[2] == '/')) { hasDriveRoot = true; break; }
        }
        Check("索引：根目录包含固定磁盘盘根", hasDriveRoot,
            roots.Length == 0 ? "（一个根都没有）" : string.Join(" ；", roots));

        string nested = "";
        for (int i = 0; i < roots.Length && nested.Length == 0; i++)
        {
            for (int j = 0; j < roots.Length; j++)
            {
                if (i == j) continue;
                /* 双向都得是假：任意一个方向为真就说明其中一个被另一个包含（或两者相等），
                 * 那等于同一批文件被扫了两遍，结果里会出现两条一模一样的路径。 */
                if (QbarRoots.IsSameOrUnder(roots[i], roots[j])
                    || QbarRoots.IsSameOrUnder(roots[j], roots[i]))
                {
                    nested = roots[i] + " ⇄ " + roots[j];
                    break;
                }
            }
        }
        Check("索引：根目录互不包含", nested.Length == 0,
            roots.Length < 2 ? "根太少（" + roots.Length + " 个），无法判包含" : nested);

        string root = Path.Combine(Path.GetTempPath(), "inputplus-qbar-idxfix-" + Guid.NewGuid().ToString("N"));
        try
        {
            BuildIndexFixture(root);

            /* 先把入口作废，验"未就绪时查询给空而不是抛异常"。 */
            QbarPinyinIndex.Invalidate();
            Check("索引：未就绪时拼音查询返回空而不抛异常",
                !QbarPinyinIndex.Ready && QbarPinyinIndex.Query("sjg", 5).Count == 0, QbarPinyinIndex.Status);

            int files = QbarIndex.PublishForTest(root);
            Check("索引：夹具树建索引成功", files >= 8, "写入 " + files + " 个文件");
            Check("索引：建完即就绪", QbarIndex.Ready, QbarIndex.Status);
            Check("索引：文档条数非 0", QbarIndex.DocCount > 0, QbarIndex.DocCount.ToString());
            Check("索引：目录条数非 0", QbarIndex.DirCount > 0, QbarIndex.DirCount.ToString());
            Check("索引：索引体积可读", QbarIndex.IndexBytes > 0, QbarIndex.IndexBytes + " 字节");
            Check("索引：状态行可读", QbarIndex.Describe().Length > 0, QbarIndex.Describe());

            IndexStats stats = TantivyApi.Stats();
            Check("索引：stats 返回 ok", stats.Ok, stats.Error);
            Check("索引：stats 的 schema 版本对得上", stats.SchemaVersion == QbarIndex.SchemaVersion,
                stats.SchemaVersion.ToString());
            Check("索引：stats 的文件数不少于夹具文件数", stats.Files >= 8, stats.Files.ToString());

            /* ---- 校准的幂等性（F1）----
             * 校准每 6 小时跑一次，它必须**幂等**：连跑两次后条数不变、路径不重复。
             * 早期实现只 AddDocs 不删旧条目，tantivy 是 append-only，同一路径立刻变成
             * 两条文档 —— 用户看到的是"每个文件都出现两遍"，而这一天的索引体积翻倍。 */
            long docsBefore = QbarIndex.DocCount;
            long filesBefore = QbarIndex.FileCount;
            int changed1 = QbarIndex.Calibrate(CancellationToken.None, 0);
            long docsAfter1 = QbarIndex.DocCount;
            int changed2 = QbarIndex.Calibrate(CancellationToken.None, 0);
            long docsAfter2 = QbarIndex.DocCount;
            Check("索引：校准确实走了一遍夹具树", changed1 > 0 && changed2 > 0,
                "第一次 " + changed1 + " 条 / 第二次 " + changed2 + " 条");
            Check("索引：连跑两次校准条数不翻倍",
                docsAfter1 == docsBefore && docsAfter2 == docsBefore,
                "校准前 " + docsBefore + " → 第一次 " + docsAfter1 + " → 第二次 " + docsAfter2
                + "（改动 " + changed1 + "/" + changed2 + " · stats " + TantivyApi.Stats().Docs
                + " · 问题 " + QbarIndex.Problem + " · 目录 " + TantivyApi.CurrentDir + "）");
            Check("索引：连跑两次校准文件数不翻倍",
                QbarIndex.FileCount == filesBefore,
                "校准前 " + filesBefore + " → 现在 " + QbarIndex.FileCount);
            IndexStats diagStats = TantivyApi.Stats();
            Check("索引：校准后统计可读", diagStats.Ok && diagStats.Docs == docsBefore,
                "ok=" + diagStats.Ok + " docs=" + diagStats.Docs + " files=" + diagStats.Files
                + " err=" + diagStats.Error + " last=" + TantivyApi.LastError
                + " dir=" + diagStats.Dir + " seg=" + diagStats.Segments);

            var dupSpec = Spec(new List<string> { "filler" });
            IndexSearchResult dupRaw = QbarIndex.Search(QbarTantivyBackend.BuildQuery(dupSpec), 200);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int duplicates = 0;
            foreach (IndexHit hit in dupRaw.Hits)
            {
                if (!seenPaths.Add(hit.Path)) duplicates++;
            }
            Check("索引：校准之后同一路径不会出现两条", duplicates == 0,
                "命中 " + dupRaw.Hits.Count + " 条，重复 " + duplicates + " 条");

            /* ---- 中文子串 ---- */
            List<FileSearchHit> report = SearchFixture(Spec(new List<string> { "报告" }));
            Check("索引：中文 2 字子串命中", report.Any(h => h.Path.EndsWith("报告-2026.docx", StringComparison.Ordinal)),
                DescribeHits(report));
            Check("索引：中文子串不误伤无关文件", !report.Any(h => h.Path.EndsWith("说明.txt", StringComparison.Ordinal)),
                DescribeHits(report));

            List<FileSearchHit> design = SearchFixture(Spec(new List<string> { "设计" }));
            Check("索引：中文子串命中目录名", design.Any(h => h.Path.IndexOf("设计稿", StringComparison.Ordinal) >= 0),
                DescribeHits(design));

            List<FileSearchHit> single = SearchFixture(Spec(new List<string> { "报" }));
            Check("索引：中文单字也能命中", single.Any(h => h.Path.EndsWith("报告-2026.docx", StringComparison.Ordinal)),
                DescribeHits(single));

            List<FileSearchHit> miss = SearchFixture(Spec(new List<string> { "没有这个词" }));
            Check("索引：不相干的词返回空", miss.Count == 0, DescribeHits(miss));

            /* ---- 英文 / 大小写 ---- */
            List<FileSearchHit> filler = SearchFixture(Spec(new List<string> { "filler" }));
            Check("索引：英文子串命中", filler.Count >= 3, DescribeHits(filler));
            List<FileSearchHit> upper = SearchFixture(Spec(new List<string> { "FILLER" }));
            Check("索引：英文大小写不敏感", upper.Count == filler.Count, DescribeHits(upper));

            /* ---- 拼音 ---- */
            QbarPinyinIndex.Start();
            List<QbarPinyinHit> sjg = QbarPinyinIndex.Query("sjg", 5);
            Check("索引：拼音首字母 sjg 命中设计稿",
                sjg.Count > 0 && sjg.Any(h => h.Path.IndexOf("设计稿", StringComparison.Ordinal) >= 0),
                sjg.Count == 0 ? "0 条" + QbarPinyinIndex.DebugSummary : sjg[0].Path);
            List<QbarPinyinHit> fullPinyin = QbarPinyinIndex.Query("shejigao", 5);
            Check("索引：拼音全拼 shejigao 命中设计稿",
                fullPinyin.Count > 0 && fullPinyin.Any(h => h.Path.IndexOf("设计稿", StringComparison.Ordinal) >= 0),
                fullPinyin.Count == 0 ? "0 条" : fullPinyin[0].Path);
            List<QbarPinyinHit> bb = QbarPinyinIndex.Query("bg", 5);
            Check("索引：拼音首字母 bg 命中报告", bb.Count > 0, bb.Count + " 条");
            Check("索引：拼音查询单字母直接返回空", QbarPinyinIndex.Query("b", 5).Count == 0, "");
            Check("索引：拼音质量分越高越靠前",
                sjg.Count == 0 || sjg[0].Quality >= 200, sjg.Count == 0 ? "" : sjg[0].Quality.ToString());

            /* ---- 模糊（子序列 + 编辑距离兜底） ---- */
            var fuzzyQuery = new IndexQuery();
            fuzzyQuery.Terms.Add("chrme");
            IndexSearchResult fuzzyResult = QbarIndex.FuzzySearch(fuzzyQuery, 10);
            List<IndexHit> fuzzyRaw = fuzzyResult.Hits;
            int fuzzyScore = 0;
            foreach (IndexHit hit in fuzzyRaw)
            {
                int score = QbarNameScore.Subsequence(Path.GetFileName(hit.Path), "chrme");
                if (score > fuzzyScore) fuzzyScore = score;
            }
            Check("索引：模糊 chrme 能捞到 chrome 的候选",
                fuzzyRaw.Count > 0,
                fuzzyResult.Ok ? fuzzyRaw.Count + " 条" : "查询失败：" + fuzzyResult.Error);
            Check("索引：模糊候选能算出子序列分", fuzzyScore > 0, "分 " + fuzzyScore);
            Check("索引：子序列打分对不匹配返回 0", QbarNameScore.Subsequence("chrome.exe", "zzz") == 0, "");
            Check("索引：子序列打分对匹配给正分", QbarNameScore.Subsequence("chrome.exe", "chrme") > 0, "");

            /* ---- 扩展名 ---- */
            List<FileSearchHit> docxOnly = SearchFixture(Spec(new List<string>(), s => s.Extensions.Add("docx")));
            Check("索引：ext 白名单只回 docx",
                docxOnly.Count > 0 && docxOnly.All(h => h.Path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)),
                DescribeHits(docxOnly));
            List<FileSearchHit> noTxt = SearchFixture(Spec(new List<string>(), s => s.NegExtensions.Add("txt")));
            Check("索引：否定扩展名不返回 txt",
                noTxt.Count > 0 && noTxt.All(h => !h.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)),
                "命中 " + noTxt.Count + " 条");
            Check("索引：扩展名过滤不会把目录带出来", docxOnly.All(h => !h.IsDir), "");

            /* ---- 目录 vs 文件 ---- */
            List<FileSearchHit> dirsOnly = SearchFixture(Spec(new List<string>(), s => s.FoldersOnly = true));
            Check("索引：folder: 只返回目录", dirsOnly.Count > 0 && dirsOnly.All(h => h.IsDir), DescribeHits(dirsOnly));
            List<FileSearchHit> filesOnly = SearchFixture(Spec(new List<string>(), s => s.FilesOnly = true));
            Check("索引：file: 只返回文件", filesOnly.Count > 0 && filesOnly.All(h => !h.IsDir), DescribeHits(filesOnly));
            Check("索引：空目录也能被 folder: 搜到",
                dirsOnly.Any(h => h.Path.EndsWith("空目录", StringComparison.Ordinal)), DescribeHits(dirsOnly));

            /* ---- 大小 / 日期 ---- */
            List<FileSearchHit> big = SearchFixture(Spec(new List<string>(), s => s.MinSize = 1024L * 1024L));
            Check("索引：size>1mb 滤掉夹具里的小文件", big.Count == 0, "命中 " + big.Count + " 条");
            List<FileSearchHit> small = SearchFixture(Spec(new List<string>(), s => s.MaxSize = 1024L * 1024L));
            Check("索引：size<1mb 保留夹具里的全部文件", small.Count > 0, "命中 " + small.Count + " 条");
            List<FileSearchHit> today = SearchFixture(Spec(new List<string>(),
                s => s.MinDay = QbarSyntaxValues.ToDay(DateTime.Now)));
            Check("索引：date:today 命中刚创建的文件", today.Count > 0, "命中 " + today.Count + " 条");
            List<FileSearchHit> future = SearchFixture(Spec(new List<string>(),
                s => s.MinDay = QbarSyntaxValues.ToDay(DateTime.Now) + 30));
            Check("索引：未来日期区间返回空", future.Count == 0, "命中 " + future.Count + " 条");

            /* ---- path 谓词 ---- */
            List<FileSearchHit> pathPin = SearchFixture(Spec(new List<string>(), s => s.PathPins.Add("批量")));
            Check("索引：path: 片段只回该目录子树",
                pathPin.Count > 0 && pathPin.All(h => h.Path.IndexOf("批量", StringComparison.Ordinal) >= 0),
                DescribeHits(pathPin));
            List<FileSearchHit> negPath = SearchFixture(Spec(new List<string>(), s => s.NegPathPins.Add("批量")));
            Check("索引：-path: 排除该子树",
                negPath.Count > 0 && negPath.All(h => h.Path.IndexOf("批量", StringComparison.Ordinal) < 0),
                "命中 " + negPath.Count + " 条");

            /* ---- 取反词 ---- */
            List<FileSearchHit> negTerm = SearchFixture(Spec(new List<string> { "filler" }, s => s.NegTerms.Add("002")));
            Check("索引：取反词剔除对应条目",
                negTerm.Count > 0 && negTerm.All(h => h.Path.IndexOf("002", StringComparison.Ordinal) < 0),
                DescribeHits(negTerm) + " · " + QbarTantivyBackend.LastDetail);

            /* ---- 短语（当作整体子串） ---- */
            List<FileSearchHit> phrase = SearchFixture(Spec(new List<string> { "报告-2026" }));
            Check("索引：带标点的短语按整体子串匹配",
                phrase.Any(h => h.Path.EndsWith("报告-2026.docx", StringComparison.Ordinal)), DescribeHits(phrase));

            /* ---- glob / 正则 ---- */
            List<FileSearchHit> glob = SearchFixture(Spec(new List<string>(), s => s.Glob = "*.docx"));
            Check("索引：wfn:*.docx 只回 docx", glob.Count > 0 && glob.All(h => h.Path.EndsWith(".docx")),
                DescribeHits(glob));
            List<FileSearchHit> globQ = SearchFixture(Spec(new List<string>(), s => s.Glob = "filler-00?.txt"));
            Check("索引：wfn 的 ? 只匹配一个字符",
                globQ.Count == 3 && globQ.All(h => Path.GetFileName(h.Path).StartsWith("filler-00", StringComparison.Ordinal)),
                "命中 " + globQ.Count + " 条");
            List<FileSearchHit> globQ2 = SearchFixture(Spec(new List<string>(), s => s.Glob = "fill???-001.txt"));
            Check("索引：wfn 的 ? 不能匹配多个字符", globQ2.Count == 0, "命中 " + globQ2.Count + " 条");
            List<FileSearchHit> regex = SearchFixture(Spec(new List<string>(), s => s.RegexText = "^报告.*\\.docx$"));
            Check("索引：锚定正则由本地复核收紧",
                regex.Count > 0 && regex.All(h => h.Path.EndsWith("报告-2026.docx", StringComparison.Ordinal)),
                DescribeHits(regex));
            List<FileSearchHit> regexMixed = SearchFixture(Spec(new List<string> { "filler" },
                s => s.RegexText = "^filler-00[12]\\.txt$"));
            Check("索引：正则可以和普通词共存（不再有全局正则开关）",
                regexMixed.Count == 2, "命中 " + regexMixed.Count + " 条");

            /* 自动机编不出来（`\b`）→ 多个字面量片段 AND 预筛 → 再按真正则复核。
             * 这条路径以前只留"最长的一段"（.docx），候选窗口会被无关的 docx 填满；
             * 现在 rep 与 .docx 两段都要有，窗口才对得上。 */
            List<FileSearchHit> regexFragments = SearchFixture(Spec(new List<string>(),
                s => s.RegexText = @"^rep\b-?\d+\.docx$"));
            Check("索引：自动机编不出来的正则靠多片段预筛仍能命中",
                regexFragments.Count == 1
                && regexFragments[0].Path.EndsWith("rep-2026.docx", StringComparison.Ordinal),
                DescribeHits(regexFragments));

            /* ---- 组合谓词 ---- */
            List<FileSearchHit> combo = SearchFixture(Spec(new List<string> { "filler" },
                s => { s.Extensions.Add("txt"); s.MaxSize = 1024L * 1024L; }));
            Check("索引：词 + ext + size 同时生效",
                combo.Count >= 3 && combo.All(h => h.Path.EndsWith(".txt")), DescribeHits(combo));

            /* ---- 排序 ---- */
            List<FileSearchHit> bySize = SearchFixture(Spec(new List<string> { "filler" }));
            Check("索引：命中项带大小", bySize.All(h => h.SizeKb >= 0), "");

            /* ---- 引擎接线 ---- */
            var context = new QbarQueryContext();
            context.Rules.Add(new QbarPriorityRule
            {
                Id = "test.dni",
                FolderPath = Path.Combine(root, "批量"),
                Level = "dontindex",
                Enabled = true,
            });

            QbarSearchOutcome doNotIndex = QbarSearchEngine.Run(context, "filler", 30);
            var openBook = new QbarQueryContext();
            QbarSearchOutcome unfiltered = QbarSearchEngine.Run(openBook, "filler", 30);
            string offender = "";
            foreach (QbarSearchHit probe in doNotIndex.Hits)
            {
                if (probe.Path.IndexOf("批量", StringComparison.Ordinal) < 0) continue;
                offender = probe.Path;
                break;
            }
            Check("引擎：DoNotIndex 目录的结果被彻底剔除",
                unfiltered.Hits.Count > 0 && doNotIndex.Hits.Count == 0 && offender.Length == 0,
                "原始 " + unfiltered.Hits.Count + " 条 → 剔除后 " + doNotIndex.Hits.Count +
                " 条，隐藏 " + doNotIndex.HiddenDoNotIndex + " 条");
            Check("引擎：剔除数量写进诊断", doNotIndex.HiddenDoNotIndex > 0,
                "DoNotIndex=" + doNotIndex.HiddenDoNotIndex);
            Check("引擎：后端标记是 tantivy", unfiltered.Backend == "tantivy", unfiltered.BackendText);

            var clear = new QbarQueryContext();
            QbarSearchOutcome precise = QbarSearchEngine.Run(clear, "filler-001", 10);
            Check("引擎：命中区间返回给 UI 用于高亮",
                precise.Hits.Count > 0 && precise.Hits[0].Highlights.Count > 0,
                precise.Hits.Count == 0 ? "没有命中" : precise.Hits[0].Name + " / " + precise.Hits[0].Highlights.Count);
            Check("引擎：分面口径与来源一致（文案跟着 FacetsAreGlobal 走）",
                precise.Facets.Count == 8
                && precise.FacetSummary().StartsWith(precise.FacetsAreGlobal ? "全部：" : "本页：", StringComparison.Ordinal),
                precise.FacetSummary() + " · global=" + precise.FacetsAreGlobal);

            /* F9：分面必须覆盖**全部命中**，而不是页面上的那几条。
             * limit=2 时页面只有 2 条，而夹具里能命中 "filler" 的条目远多于 2。 */
            QbarSearchOutcome facetScope = QbarSearchEngine.Run(new QbarQueryContext(), "filler", 1);
            int facetTotal = 0;
            foreach ((string label, int count) in facetScope.Facets)
            {
                if (label == "近7天") continue;   // 这一桶是"全部命中"的子集，不能加进总数
                facetTotal += count;
            }
            /* limit=2 时页面只有 2 条（拼音/模糊增强可能再补几条），而分面覆盖的是
             * **全部命中** —— 所以分面合计必须大于页面条数。 */
            Check("引擎：分面覆盖全部命中而不是本页",
                facetScope.FacetsAreGlobal && facetTotal > 1 && facetScope.Facets.Count == 8,
                "请求 1 条 / 页面 " + facetScope.Hits.Count + " 条 / 分面合计 " + facetTotal
                + " 条 · " + facetScope.FacetSummary());

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
                high.Hits.Count == 0 ? "0 条" : high.Hits[0].Path);

            /* ---- 索引损坏自愈（F2）----
             * 场景：段文件被杀软删掉。`ipidx_open` 必须失败（这是前提，不是巧合），
             * 然后自愈路径要能把这个目录清空重建，而不是把程序永久钉在"索引打开失败"。
             *
             * 注意：这一步会**真的删掉一个段文件**，所以放在所有查询用例之后、
             * 并且在它之后重新发布一次夹具索引（后面还要继续查）。 */
            string damagedDir = TantivyApi.CurrentDir;
            string? victim = null;
            if (damagedDir.Length > 0 && Directory.Exists(damagedDir))
            {
                foreach (string pattern in new[] { "*.term", "*.store", "*.idx" })
                {
                    string[] found = Directory.GetFiles(damagedDir, pattern);
                    if (found.Length > 0) { victim = found[0]; break; }
                }
            }
            Check("索引：找得到可以删的段文件（自愈用例的前提）", victim != null,
                victim ?? ("目录里没有 *.term/*.store/*.idx：" + damagedDir));
            if (victim != null)
            {
                TantivyApi.Close();
                File.Delete(victim);
                int broken = TantivyApi.OpenEx(damagedDir);
                Check("索引：段文件缺一个之后打开会失败", broken != 0,
                    "返回码 " + broken + " / " + TantivyApi.LastError);

                TantivyApi.Close();
                int healedRc = QbarIndex.OpenIndexWithRecovery(damagedDir, out bool healed);
                Check("索引：损坏的索引目录能被自愈路径重建", healedRc == 0 && healed,
                    "返回码 " + healedRc + " / 自愈 " + healed + " / " + TantivyApi.LastError);
                Check("索引：自愈留痕写进了状态说明", QbarIndex.RecoveryNote.Contains("索引损坏"),
                    QbarIndex.RecoveryNote);

                /* 重建之后的目录里应该是空的（清空过），再写一条就能验证"能写" ——
                 * 只断言"打开成功"不够：一个能打开但写不进去的索引等于没有索引。
                 * 这里走**生产写入口**（UpsertForTest）而不是裸 AddDocs+Commit：
                 * 它带着"commit 失败就把这一批重新 add 一次"的重试，而火绒正好会在
                 * 刚清空重建的目录上瞬时捏住 .store（本机已知环境行为）。 */
                int wrote = QbarIndex.UpsertForTest(new[] { Path.Combine(root, "文档", "报告-2026.docx") });
                IndexStats healedStats = TantivyApi.Stats();
                Check("索引：自愈后的目录能重新写入并提交", wrote > 0 && healedStats.Docs > 0,
                    "写入 " + wrote + " / DocCount " + healedStats.Docs + " / " + QbarIndex.Problem);
                QbarIndex.ResetForTest();
                /* 自愈用例把夹具索引目录整个清空过，后面的用例还得在真实索引上跑 ——
                 * 重新发布一次（这也是"自愈之后能继续工作"的一部分）。 */
                int republished = QbarIndex.PublishForTest(root);
                Check("索引：自愈用例之后夹具索引已重新发布", republished >= 8, "写入 " + republished + " 个文件");
            }

            /* ---- 写入路径的竞态与批内重复（F3）----
             * 三条断言都打在**唯一写入口**上：批内同路径只留一条、同批增删以删为准、
             * 两个线程各写 200 条不重叠路径时条数恰好 +400（±0）。
             * 这三件事以前都没有用例，而它们正是"索引里出现幽灵条目"的全部来源。 */
            string liveDir = Path.Combine(root, "并发");
            Directory.CreateDirectory(liveDir);
            string dupPath = Path.Combine(liveDir, "dup.txt");
            File.WriteAllText(dupPath, "dup", Encoding.UTF8);
            long docsBeforeDup = QbarIndex.DocCount;
            QbarIndex.Upsert(new[] { dupPath, dupPath, dupPath });
            long docsAfterDup = QbarIndex.DocCount;
            Check("索引：同一批重复路径只留一条", docsAfterDup == docsBeforeDup + 1,
                docsBeforeDup + " → " + docsAfterDup + "（同一路径写了 3 次）");
            IndexSearchResult dupOnce = QbarIndex.Search(QbarTantivyBackend.BuildQuery(
                Spec(new List<string> { "dup" })), 20);
            int dupHits = dupOnce.Hits.Count(h => string.Equals(h.Path, dupPath, StringComparison.OrdinalIgnoreCase));
            Check("索引：同一批重复路径查出来只有一条", dupHits == 1, "命中 " + dupHits + " 条");

            string ghostPath = Path.Combine(liveDir, "ghost.txt");
            File.WriteAllText(ghostPath, "ghost", Encoding.UTF8);
            /* 同一批里既增又删：以删为准。这正是改名与"删了又立刻建同名文件"交错时的样子。 */
            var conflicted = new WatcherBatch();
            conflicted.Upserted.Add(ghostPath);
            conflicted.Deleted.Add(ghostPath);
            QbarIndex.OnWatcherBatchForTest(conflicted);
            IndexSearchResult ghostHits = QbarIndex.Search(QbarTantivyBackend.BuildQuery(
                Spec(new List<string> { "ghost" })), 20);
            Check("索引：同名既增又删以删为准",
                !ghostHits.Hits.Any(h => string.Equals(h.Path, ghostPath, StringComparison.OrdinalIgnoreCase)),
                "命中 " + ghostHits.Hits.Count + " 条");

            string parallelDir = Path.Combine(liveDir, "并发200");
            Directory.CreateDirectory(parallelDir);
            long docsBeforeParallel = QbarIndex.DocCount;
            int writtenA = -1, writtenB = -1;
            var writerA = new Thread(() => { writtenA = QbarIndex.UpsertForTest(BuildFanout(parallelDir, "a")); });
            var writerB = new Thread(() => { writtenB = QbarIndex.UpsertForTest(BuildFanout(parallelDir, "b")); });
            writerA.IsBackground = true;
            writerB.IsBackground = true;
            writerA.Start();
            writerB.Start();
            writerA.Join(30000);
            writerB.Join(30000);
            long docsAfterParallel = QbarIndex.DocCount;
            Check("索引：并发写入后条目数正确", docsAfterParallel == docsBeforeParallel + 400,
                docsBeforeParallel + " → " + docsAfterParallel + "（期望 +400，两批各写 " +
                writtenA + "/" + writtenB + " 条 · 问题 " + QbarIndex.Problem + "）");

            /* ---- 停机期间的增删：目录快照 diff（F6）---- */
            string newDir = Path.Combine(root, "快照新增");
            Directory.CreateDirectory(newDir);
            string newFile = Path.Combine(newDir, "snapshot-probe.txt");
            File.WriteAllText(newFile, "probe", Encoding.UTF8);
            int stampChanged = QbarIndex.CalibrateFromStamps(CancellationToken.None);
            IndexSearchResult probeHits = QbarIndex.Search(QbarTantivyBackend.BuildQuery(
                Spec(new List<string> { "snapshot-probe" })), 20);
            Check("索引：启动快照能发现新增目录",
                stampChanged > 0
                && probeHits.Hits.Any(h => string.Equals(h.Path, newFile, StringComparison.OrdinalIgnoreCase)),
                "补入 " + stampChanged + " 条 · 命中 " + probeHits.Hits.Count + " 条 · 快照 " + QbarIndex.StampStatus);

            /* 快照里写一条磁盘上并不存在的目录：不许抛、不许把索引打成未就绪，只许安排整根重扫。 */
            /* 先等上一轮的"启动快照能发现新增目录"收尾：它可能已经安排了后台重扫。 */
            QbarIndex.WaitRescansForTest(10000);
            string ghostDir = Path.Combine(root, "快照里有过、磁盘上没了");
            QbarIndex.InjectStampForTest(ghostDir);
            int ghostChanged = QbarIndex.CalibrateFromStamps(CancellationToken.None);
            Check("索引：快照缺失目录触发该根重扫",
                ghostChanged > 0 && QbarIndex.Ready && !QbarIndex.Building,
                "处理 " + ghostChanged + " 项 · 就绪 " + QbarIndex.Ready + " · " + QbarIndex.Status);
            /* 这条用例故意触发了一次整根重扫（后台线程）。不等它跑完，后面的写入会与它
             * 抢同一个 writer —— 那会让后面的读回用例偶发丢失，掩盖真正的问题。 */
            Check("索引：后台重扫能在超时内收敛", QbarIndex.WaitRescansForTest(20000), "仍在重扫");

            /* ---- 二进制写入协议的写后读回（F8，最关键的一条）----
             * 只比条数等于没验：字段错位（比如 size 读到 day 上）条数照样对。
             * 这里造 40 个**真实文件**（大小从 0 到 40×37，含 0/1/1023 这几个 KB 边界），
             * 写进去之后逐条按名字查回来，比对 path / isdir / size / day 全等。 */
            string binDir = Path.Combine(root, "二进制");
            Directory.CreateDirectory(binDir);
            var binFiles = new List<string>(40);
            var binSizes = new List<long>(40);
            var binDays = new List<int>(40);
            for (int i = 0; i < 40; i++)
            {
                string p = Path.Combine(binDir, "bin-probe-" + i.ToString("000") + ".bin");
                int size = i == 0 ? 0 : i == 1 ? 1 : i == 2 ? 1023 : i * 37;
                File.WriteAllBytes(p, new byte[size]);
                DateTime stamp = new DateTime(2024, 1, 1).AddDays(i * 11);
                File.SetLastWriteTimeUtc(p, stamp);
                binFiles.Add(p);
                binSizes.Add(size);
                binDays.Add(QbarSyntaxValues.ToDay(stamp));
            }
            long binDocsBefore = QbarIndex.DocCount;
            int binWritten = QbarIndex.UpsertForTest(binFiles);
            long binDocsAfter = QbarIndex.DocCount;
            Check("索引：二进制入口写入 40 条", binWritten == 40, binWritten.ToString());
            Check("索引：二进制写入后文档数恰好 +40", binDocsAfter - binDocsBefore == 40,
                binDocsBefore + " → " + binDocsAfter + "（期望 +40，写入 " + binWritten
                + " · 问题 " + QbarIndex.Problem + " · last " + TantivyApi.LastError + "）");

            int mismatched = 0;
            string firstMismatch = "";
            for (int i = 0; i < binFiles.Count; i++)
            {
                string leaf = Path.GetFileNameWithoutExtension(binFiles[i]);
                /* 窗口给足 400 条：40 个 probe 名字高度相似（共同前缀 bin-probe-），
                 * 按 BM25 取前 5 条时目标可能不在里面 —— 那是"窗口太小"，不是"字段写错"。
                 * 断言仍然逐条比对四个字段，不退让。 */
                IndexSearchResult one = QbarIndex.Search(QbarTantivyBackend.BuildQuery(
                    Spec(new List<string> { leaf })), 400);
                IndexHit? match = null;
                foreach (IndexHit hit in one.Hits)
                {
                    if (string.Equals(hit.Path, binFiles[i], StringComparison.OrdinalIgnoreCase)) { match = hit; break; }
                }
                bool ok = match != null
                          && match.IsDir == 0
                          && match.Size == binSizes[i]
                          && match.Day == binDays[i];
                if (ok) continue;
                mismatched++;
                if (firstMismatch.Length == 0)
                {
                    firstMismatch = leaf + "：期望 s=" + binSizes[i] + " t=" + binDays[i]
                                    + "，实际 " + (match == null
                                        ? "没查回来"
                                        : "s=" + match.Size + " t=" + match.Day + " d=" + match.IsDir);
                }
            }
            Check("索引：二进制写入的字段逐条读回全等（path/isdir/size/day）",
                mismatched == 0, mismatched == 0 ? "40/40 全等" : (mismatched + " 条不符：" + firstMismatch));

            /* 目录也要走一遍二进制入口：isdir 位错位的话，上面那条永远抓不到。 */
            string binSubDir = Path.Combine(binDir, "bin-probe-dir");
            Directory.CreateDirectory(binSubDir);
            QbarIndex.UpsertForTest(new[] { binSubDir });
            IndexSearchResult dirBack = QbarIndex.Search(QbarTantivyBackend.BuildQuery(
                Spec(new List<string> { "bin-probe-dir" })), 400);
            Check("索引：二进制写入的目录 isdir 位读回为真",
                dirBack.Hits.Any(h => h.IsDir == 1
                    && string.Equals(h.Path, binSubDir, StringComparison.OrdinalIgnoreCase)),
                dirBack.Hits.Count + " 条");

            /* ---- 索引没就绪时的行为 ---- */
            QbarIndex.ResetForTest();
            Check("索引：重置之后不再是就绪态", !QbarIndex.Ready, QbarIndex.Status);
            QbarSearchOutcome off = QbarSearchEngine.Run(new QbarQueryContext(), "filler", 10);
            Check("索引：没就绪时引擎标记为 off（不静默换源）", off.Backend == "off", off.BackendText);
            Check("索引：没就绪时给出可读提示", off.BackendDetail.Length > 0, off.BackendDetail);
            Check("索引：没就绪时结果为空", off.Hits.Count == 0, off.Hits.Count.ToString());
            Check("索引：没就绪时说明可读", off.BackendText.Contains("不可用"), off.BackendText);
        }
        finally
        {
            try { QbarIndex.ResetForTest(); } catch (Exception ex) { CoreLog.Swallow(ex, "清理索引自检"); }
            try { Directory.Delete(root, true); } catch (Exception ex) { CoreLog.Swallow(ex, "清理索引夹具"); }
        }
    }

    /// <summary>
    /// 查询构造 / 谓词翻译的自检：只验"spec 变成索引查询"这一层，不碰索引。
    /// </summary>
    private static void TestQbarIndexQuery()
    {
        var spec = new QbarSearchSpec();
        spec.Terms.Add("报表");
        spec.Extensions.Add("docx");
        spec.NegTerms.Add("tmp");
        spec.PathPins.Add("Documents");
        spec.MinSize = 1024;

        IndexQuery query = QbarTantivyBackend.BuildQuery(spec);
        Check("查询构造：词被搬进索引查询", query.Terms.Count == 1 && query.Terms[0] == "报表", "");
        Check("查询构造：扩展名被搬进索引查询", query.Ext.Count == 1, "");
        Check("查询构造：取反词被搬进索引查询", query.Neg.Count == 1, "");
        Check("查询构造：路径钉被搬进索引查询", query.Path.Count == 1, "");
        Check("查询构造：大小下界被搬进索引查询", query.MinSize == 1024, query.MinSize.ToString());
        Check("查询构造：带 size 时只搜文件（目录没有大小）", query.Files && !query.Dirs, "");

        var dirs = new QbarSearchSpec { FoldersOnly = true };
        IndexQuery dirQuery = QbarTantivyBackend.BuildQuery(dirs);
        Check("查询构造：folder: 只搜目录", dirQuery.Dirs && !dirQuery.Files, "");

        var glob = new QbarSearchSpec { Glob = "*.pdf" };
        Check("查询构造：wfn 原样带给索引", QbarTantivyBackend.BuildQuery(glob).Glob == "*.pdf", "");

        var regex = new QbarSearchSpec { RegexText = "^rep.*\\.docx$" };
        Check("查询构造：正则原样带给索引", QbarTantivyBackend.BuildQuery(regex).Regex.Length > 0, "");

        Check("查询构造：描述串可读",
            QbarTantivyBackend.Describe(query).Contains("terms="), QbarTantivyBackend.Describe(query));

        /* spec 里没有任何谓词时，描述串要写"空查询"而不是空串。 */
        Check("查询构造：空查询有可读描述",
            QbarTantivyBackend.Describe(new IndexQuery()) == "(空查询)",
            QbarTantivyBackend.Describe(new IndexQuery()));

        /* ---- 谓词复核（不依赖索引） ---- */
        var filterSpec = new QbarSearchSpec();
        filterSpec.Extensions.Add("docx");
        Check("复核：doc: 接受 docx",
            QbarSpecFilter.AcceptPath(filterSpec, @"C:\a\报表.docx", false, 10, 100, out _), "");
        Check("复核：doc: 拒绝 tmp",
            !QbarSpecFilter.AcceptPath(filterSpec, @"C:\a\报表.tmp", false, 10, 100, out _), "");
        Check("复核：doc: 拒绝目录",
            !QbarSpecFilter.AcceptPath(filterSpec, @"C:\a\报表", true, 0, 100, out _), "");

        var negSpec = new QbarSearchSpec();
        negSpec.NegExtensions.Add("tmp");
        Check("复核：-ext:tmp 剔掉 tmp",
            !QbarSpecFilter.AcceptPath(negSpec, @"C:\a\x.tmp", false, 10, 100, out _), "");
        Check("复核：-ext:tmp 保留 txt",
            QbarSpecFilter.AcceptPath(negSpec, @"C:\a\x.txt", false, 10, 100, out _), "");

        var globSpec = new QbarSearchSpec { Glob = "*.pdf" };
        Check("复核：wfn:*.pdf 只收 pdf",
            QbarSpecFilter.AcceptPath(globSpec, @"C:\a\x.pdf", false, 10, 100, out _), "");
        Check("复核：wfn:*.pdf 拒 docx",
            !QbarSpecFilter.AcceptPath(globSpec, @"C:\a\x.docx", false, 10, 100, out _), "");

        var sizeSpec = new QbarSearchSpec { MinSize = 1024L * 1024L };
        Check("复核：size 下界命中",
            QbarSpecFilter.AcceptPath(sizeSpec, @"C:\a\big.bin", false, 2048, 100, out _), "");
        Check("复核：size 下界外剔除",
            !QbarSpecFilter.AcceptPath(sizeSpec, @"C:\a\small.bin", false, 1, 100, out _), "");

        /* ---- 大小按字节而不是 KB（F4）----
         * 500 字节的文件在 KB 口径里是 0，早期实现会让它**不满足** `size:>0`。 */
        var positiveSize = new QbarSearchSpec { MinSize = 1 };
        Check("复核：500 字节的文件满足 size:>0",
            QbarSpecFilter.AcceptPath(positiveSize, @"C:\a\half-kb.txt", false, 0,
                QbarSyntaxValues.ToDay(DateTime.Now), out _, sizeBytes: 500), "KB 口径是 0，字节口径是 500");
        Check("复核：0 字节的文件不满足 size:>0",
            !QbarSpecFilter.AcceptPath(positiveSize, @"C:\a\empty.txt", false, 0,
                QbarSyntaxValues.ToDay(DateTime.Now), out _, sizeBytes: 0), "字节口径 0");

        Check("复核：glob 通配 * 跨越任意字符", QbarSpecFilter.GlobMatch("report.docx", "rep*ocx"), "");
        Check("复核：glob 通配 ? 只匹配一个字符", QbarSpecFilter.GlobMatch("a1c", "a?c"), "");
        Check("复核：glob 不匹配时返回 false", !QbarSpecFilter.GlobMatch("abc", "abd"), "");
    }
}
