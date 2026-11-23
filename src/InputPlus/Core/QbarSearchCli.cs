using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;

namespace InputPlus.Core;

/// <summary>
/// 查询翻译层的命令行入口：--qbar-query "doc: 报表" 与 --qbar-bench。
///
/// 为什么需要：语义层把一句用户输入翻成索引查询，中间要过归一化、分词、关键字解析、
/// 语法生成、规则剔除、本地重排六道手。真机上"搜不到"时，从界面只能看到最终列表，
/// 分不清是翻译歪了、规则把结果吃了、还是索引没建好 —— 这两个开关把每一层的中间结果
/// 和每一步的耗时都摊开，排障不用猜。
///
/// --qbar-bench 只报数字（命中 / 候选 / 耗时），不再做"新重复一致"：
/// 索引换内核之后旧入口已经不存在，程序内没有第二个能自比的实现。
/// 「与改造前一致」这条红线改由 docs\\qbar\\golden 下的基线 +
/// tools/qbar-golden-compare.ps1 承担（见 TANTIVY-MIGRATION.md）。
/// </summary>
internal static class QbarSearchCli
{
    /// <summary>基准用的 20 条查询：覆盖纯词、子序列、拼音、过滤器、谓词、短语、特殊字符、系统目录。</summary>
    private static readonly string[] BenchQueries =
    {
        "report", "rprt", "yh", "yinhang", "doc:", "doc: 报表",
        "ext:pdf;docx size:>1mb", "date:week", "-ext:tmp", "\"年度 总结\"",
        "(", "C:\\Windows", "pic:", "video:", "audio:",
        "folder:", "size:10mb..1gb", "wfn:*.pdf", "reporter", "说明",
    };

    public static int RunQuery(string[] args)
    {
        /* 控制台默认走系统代码页，中文在大括号输出里会变成一片问号；
         * 基准报告要贴进文档，先把编码钉死成 UTF-8。 */
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
        string query = TakeQuery(args, "--qbar-query");
        if (query.Length == 0)
        {
            Write("clp-qbar-query.txt", "用法：InputPlus.exe --qbar-query \"doc: 报表\"");
            return 2;
        }

        long waited = EnsureIndex();
        QbarQueryContext context = BuildContext();

        var watch = Stopwatch.StartNew();
        QbarSearchOutcome outcome = QbarSearchEngine.Run(context, query, 40);
        watch.Stop();

        var sb = new StringBuilder();
        sb.AppendLine("=== Qbar 查询调试（语义层） ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("输入: " + query);
        sb.AppendLine("索引状态: " + QbarIndex.Status);
        if (QbarIndex.Problem.Length > 0) sb.AppendLine("索引问题: " + QbarIndex.Problem);
        sb.AppendLine("索引规模: 文件 " + QbarIndex.FileCount + " 个，目录 " + QbarIndex.DirCount + " 个");
        sb.AppendLine("等待索引: " + waited + " ms");
        /* 引擎拿不到快照时不会填 Translation（例如索引还没建好），这时现翻一次，
         * 让调试面板始终能看到"这句话被翻成了什么"。 */
        QbarTranslateResult translation = outcome.Translation
            ?? QbarQueryTranslator.Translate(query, QbarFilterCatalog.Create(context.Filters), DateTime.Now);

        sb.AppendLine();
        sb.AppendLine("归一化: " + translation.Normalized);
        sb.AppendLine("索引查询: " + translation.IndexText);
        sb.AppendLine("后端: " + outcome.BackendText);
        if (QbarTantivyBackend.LastRejectedSample.Length > 0)
            sb.AppendLine("复核样本: " + QbarTantivyBackend.LastRejectedSample);
        sb.AppendLine("请求: " + translation.RequestDetail);
        sb.AppendLine("上下文: " + translation.KeywordDetail);
        sb.AppendLine("分词: " + translation.Tokens);
        sb.AppendLine("各层:");
        foreach (QbarDiagEntry stage in translation.Stages)
            sb.AppendLine("  [" + stage.Stage + "] " + stage.Detail + (stage.Warn ? "   <-- 需要留意" : ""));
        sb.AppendLine();
        sb.AppendLine("候选: " + outcome.RawCount + " 条，展示 " + outcome.Hits.Count + " 条，端到端 " +
                      watch.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
        if (outcome.HiddenCount > 0)
            sb.AppendLine("被规则隐藏: " + outcome.HiddenCount + " 条（Ignored " + outcome.HiddenIgnored +
                          " / DoNotIndex " + outcome.HiddenDoNotIndex + "）");
        sb.AppendLine(outcome.TimingText);
        if (outcome.Profile.Length > 0) sb.AppendLine("  重排分段: " + outcome.Profile);
        if (outcome.FuzzyUsed) sb.AppendLine("模糊匹配: 已启用");
        if (outcome.PinyinUsed) sb.AppendLine("拼音索引: 命中");
        if (outcome.PinyinPending) sb.AppendLine("拼音索引: 正在后台构建");
        string facets = outcome.FacetSummary();
        if (facets.Length > 0) sb.AppendLine(facets);
        sb.AppendLine();
        sb.AppendLine("命中:");
        foreach (QbarSearchHit hit in outcome.Hits)
        {
            /* 大小/日期也打出来：分面桶和"为什么这条排在前面"都靠它俩，
                只给路径的话，分面全 0 时看不出是数据没取到还是文件真没有。 */
            string extra = "";
            if (!hit.IsDir && hit.SizeBytes > 0) extra += " / " + SizeText(hit.SizeBytes);
            if (hit.Day >= 0 && hit.Day != QbarSyntaxValues.UnknownDay)
            {
                try { extra += " / " + QbarSyntaxValues.FromDay(hit.Day).ToString("yyyy-MM-dd"); }
                catch (ArgumentOutOfRangeException) { }
            }
            /* 高亮区间也打出来（用「」把命中的片段括起来）：界面上的加亮完全靠它，
                CLI 不带这一行就没法判断"没加亮"是区间为空还是 UI 没画。 */
            string nameAndMarks = hit.Name;
            if (hit.Highlights.Count > 0)
            {
                var sbName = new StringBuilder(hit.Name);
                foreach (QbarHighlight mark in hit.Highlights.OrderByDescending(h => h.Start))
                {
                    int at = Math.Clamp(mark.Start, 0, sbName.Length);
                    int len = Math.Clamp(mark.Length, 0, sbName.Length - at);
                    if (len <= 0) continue;
                    sbName.Insert(at + len, "」");
                    sbName.Insert(at, "「");
                }
                nameAndMarks = sbName.ToString();
            }

            sb.AppendLine("  " + (hit.IsDir ? "[目录] " : "[文件] ") + hit.Path +
                          "   (分 " + hit.Score + " / 档 " + hit.Tier + extra + ")" +
                          (nameAndMarks != hit.Name ? "  ← " + nameAndMarks : ""));
        }

        Write("clp-qbar-query.txt", sb.ToString());
        return outcome.Hits.Count > 0 ? 0 : 3;
    }

    private static string SizeText(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>
    /// --index-status：文件索引的体检。条数、体积、构建进度、最近一次提交、watcher 状态、
    /// 查询 p50/p95，一次说清 —— 排查"为什么搜索是空的 / 为什么刚建的文件搜不到"时的第一条命令。
    /// </summary>
    public static int RunIndexStatus(string[]? args = null)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        bool rebuild = args != null && args.Any(a => a.Equals("--rebuild", StringComparison.OrdinalIgnoreCase));

        if (rebuild) QbarIndex.RequestRebuild();
        long waited = EnsureIndex();

        var sb = new StringBuilder();
        sb.AppendLine("=== 文件索引状态（进程内 tantivy） ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("原生库: " + TantivyApi.StatusText);
        sb.AppendLine("索引目录: " + QbarIndex.IndexDir);
        sb.AppendLine("旧快照目录: " + (Directory.Exists(QbarIndex.LegacyDir) ? "仍存在（下次建索引时删除）" : "已清理"));
        sb.AppendLine("状态: " + QbarIndex.Status);
        if (QbarIndex.Problem.Length > 0) sb.AppendLine("问题: " + QbarIndex.Problem);
        /* 自愈过的话必须单独说一句：否则用户看到的是"索引已就绪"，而索引其实是空的
         * （正在重建）—— 那会被当成"搜索功能坏了"。 */
        if (QbarIndex.RecoveryNote.Length > 0) sb.AppendLine("问题: " + QbarIndex.RecoveryNote);
        sb.AppendLine("正在建立: " + QbarIndex.Building);
        sb.AppendLine("根目录: " + (QbarIndex.Roots.Count == 0 ? "（还没算）" : string.Join(" ；", QbarIndex.Roots)));
        sb.AppendLine("等待索引: " + waited + " ms");

        IndexStats stats = TantivyApi.Stats();
        sb.AppendLine();
        sb.AppendLine("文档总数: " + stats.Docs);
        sb.AppendLine("文件 / 目录: " + stats.Files + " / " + stats.Dirs);
        sb.AppendLine("段数: " + stats.Segments);
        sb.AppendLine("索引体积: " + (stats.Bytes / 1024.0 / 1024.0).ToString("0.0") + " MB（"
                      + (stats.Docs > 0 ? (stats.Bytes / (double)stats.Docs).ToString("0") : "?") + " 字节/文档）");
        sb.AppendLine("schema 版本: " + stats.SchemaVersion);
        sb.AppendLine("建索引于: " + (QbarIndex.BuiltAt == DateTime.MinValue
            ? "（还没有）"
            : QbarIndex.BuiltAt.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine("最近一次提交耗时: " + QbarIndex.LastCommitMs + " ms");
        sb.AppendLine("建索引分段: " + QbarIndex.BuildTimingText);
        sb.AppendLine("watcher: " + QbarIndex.WatcherStatus);
        sb.AppendLine("目录快照: " + QbarIndex.StampStatus);

        // 查询延迟：热态跑几轮，给 p50 / p95。数字要能脚本解析，格式固定。
        QbarQueryContext context = BuildContext();
        var samples = new List<double>();
        foreach (string query in new[] { "report", "说明", "doc:", "ext:pdf;docx size:>1mb" })
        {
            for (int i = 0; i < 8; i++)
            {
                var sw = Stopwatch.StartNew();
                QbarSearchOutcome outcome = QbarSearchEngine.Run(context, query, 40);
                sw.Stop();
                if (outcome.Backend != "off") samples.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        samples.Sort();
        double p50 = samples.Count == 0 ? 0 : samples[samples.Count / 2];
        double p95 = samples.Count == 0 ? 0 : samples[(int)Math.Min(samples.Count - 1, Math.Ceiling(samples.Count * 0.95) - 1)];
        sb.AppendLine();
        sb.AppendLine("查询 p50: " + p50.ToString("0.0") + " ms");
        sb.AppendLine("查询 p95: " + p95.ToString("0.0") + " ms");
        sb.AppendLine("样本数: " + samples.Count);

        sb.AppendLine();
        sb.AppendLine("工作集: " + (Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0).ToString("0.0") + " MB");
        sb.AppendLine("专用内存: " + (Process.GetCurrentProcess().PrivateMemorySize64 / 1024.0 / 1024.0).ToString("0.0") + " MB");

        if (args != null && args.Any(a => a.Equals("--increment-check", StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine();
            sb.AppendLine(RunIncrementCheck());
        }

        Write("clp-index-status.txt", sb.ToString());
        return stats.Ok && stats.Files > 0 ? 0 : 1;
    }

    /// <summary>
    /// 增量实测：在第一个索引根下真的建一个文件，然后**在本进程里**轮询"能不能搜到"，
    /// 删掉之后同样轮询"是不是搜不到了"。
    ///
    /// 为什么必须在同一个进程里测：watcher 是进程内的线程，跨进程跑一条命令测的是
    /// "上次退出前提交了没有"，那是另一件事。这里量的是"从落盘到可检索"的真实延迟。
    /// </summary>
    private static string RunIncrementCheck()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 增量实测 ===");
        if (QbarIndex.Roots.Count == 0)
        {
            sb.AppendLine("没有索引根，跳过");
            return sb.ToString().TrimEnd();
        }

        string root = PickWritableRoot();
        if (root.Length == 0)
        {
            sb.AppendLine("没有可写的索引根，跳过");
            return sb.ToString().TrimEnd();
        }
        sb.AppendLine("落点: " + root);
        string name = "inputplus-increment-probe-" + Guid.NewGuid().ToString("N")[..8];
        string path = Path.Combine(root, name + ".txt");
        try
        {
            File.WriteAllText(path, "probe", Encoding.UTF8);
            var watch = Stopwatch.StartNew();
            bool seen = PollUntil(() => CountHits(name) > 0, 5000, out long addMs);
            sb.AppendLine("新建后可见: " + (seen ? "是" : "否") + " · 延迟 " + addMs + " ms"
                          + (seen ? (addMs <= 2000 ? "（≤2s 达标）" : "（>2s 超标）") : ""));

            File.Delete(path);
            bool gone = PollUntil(() => CountHits(name) == 0, 5000, out long delMs);
            sb.AppendLine("删除后不可见: " + (gone ? "是" : "否") + " · 延迟 " + delMs + " ms"
                          + (gone ? (delMs <= 2000 ? "（≤2s 达标）" : "（>2s 超标）") : ""));
        }
        catch (Exception ex)
        {
            sb.AppendLine("增量实测失败：" + ex.Message);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        return sb.ToString().TrimEnd();
    }

    private static bool PollUntil(Func<bool> condition, int timeoutMs, out long elapsedMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                elapsedMs = watch.ElapsedMilliseconds;
                return true;
            }
            Thread.Sleep(100);
        }
        elapsedMs = watch.ElapsedMilliseconds;
        return condition();
    }

    /// <summary>
    /// 找一个**真能写**的索引根。盘根（C:\）在普通权限下写不进去，
    /// 而"增量实测"要在被监视的目录里真的建一个文件，所以优先用用户目录。
    /// </summary>
    private static string PickWritableRoot()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string root in QbarIndex.Roots)
        {
            if (profile.Length > 0 && QbarRoots.IsSameOrUnder(profile, root) && CanWrite(profile)) return profile;
        }
        foreach (string root in QbarIndex.Roots)
        {
            if (CanWrite(root)) return root;
        }
        return "";
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".inputplus-write-probe-" + Guid.NewGuid().ToString("N")[..6]);
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>按名字数命中（走的是和界面完全一样的那条链路）。</summary>
    private static int CountHits(string name)
    {
        var spec = new QbarSearchSpec();
        spec.Terms.Add(name);
        IndexSearchResult result = QbarIndex.Search(QbarTantivyBackend.BuildQuery(spec), 5);
        return result.Ok ? result.Hits.Count : 0;
    }

    public static int RunBench(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
        long waited = EnsureIndex();
        QbarQueryContext context = BuildContext();
        QbarFilterCatalog catalog = QbarFilterCatalog.Create(context.Filters);
        DateTime now = DateTime.Now;

        var sb = new StringBuilder();
        sb.AppendLine("=== Qbar 语义层基准 ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("索引状态: " + QbarIndex.Status + "，文件 " + QbarIndex.FileCount +
                      " 个，目录 " + QbarIndex.DirCount + " 个，等待 " + waited + " ms");
        sb.AppendLine("构建于: " + (QbarIndex.BuiltAt == DateTime.MinValue
            ? "（还没有）"
            : QbarIndex.BuiltAt.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine("候选窗口: " + context.Options.MaxCandidates + " 条；规则数: " + context.Resolver.Count);
        sb.AppendLine("后端: 进程内 tantivy 索引 · " + TantivyApi.StatusText);
        sb.AppendLine();

        /* ---- 1) 翻译层自身开销 ---- */
        const string translateProbe = "doc: 报表 ext:pdf size:>1mb date:week -ext:tmp \"年度 总结\"";
        for (int i = 0; i < 50; i++) QbarQueryTranslator.Translate(translateProbe, catalog, now);
        const int translateRuns = 2000;
        var tw = Stopwatch.StartNew();
        for (int i = 0; i < translateRuns; i++) QbarQueryTranslator.Translate(translateProbe, catalog, now);
        tw.Stop();
        double translateUs = tw.Elapsed.TotalMilliseconds * 1000.0 / translateRuns;
        sb.AppendLine("翻译层: " + translateRuns + " 次平均 " + translateUs.ToString("0.00") + " us/次（预算 5000 us）");
        sb.AppendLine();

        /* ---- 2) 20 条查询：端到端 + 重复一致性 ---- */
        sb.AppendLine("查询\t后端\t命中\t候选\t隐藏\t中位 ms\t最大 ms\t翻译 us\t重复一致");
        var totals = new List<double>();
        foreach (string query in BenchQueries)
        {
            var runs = new List<double>();
            QbarSearchOutcome last = QbarSearchEngine.Run(context, query, 40);
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                last = QbarSearchEngine.Run(context, query, 40);
                sw.Stop();
                runs.Add(sw.Elapsed.TotalMilliseconds);
            }
            runs.Sort();
            double median = runs[runs.Count / 2];
            double max = runs[runs.Count - 1];
            totals.Add(median);

            var quoteWatch = Stopwatch.StartNew();
            const int quoteRuns = 200;
            for (int i = 0; i < quoteRuns; i++) QbarQueryTranslator.Translate(query, catalog, now);
            quoteWatch.Stop();

            sb.AppendLine(query.Replace("\t", " ") + "\t" + (last.Backend == "off" ? "X" : "T") +
                          "\t" + last.Hits.Count + "\t" + last.RawCount + "\t" +
                          last.HiddenCount + "\t" + median.ToString("0.0") + "\t" + max.ToString("0.0") + "\t" +
                          (quoteWatch.Elapsed.TotalMilliseconds * 1000.0 / quoteRuns).ToString("0.0") + "\t" +
                          CompareStability(context, query, last));
        }

        totals.Sort();
        double p95 = totals[(int)Math.Min(totals.Count - 1, Math.Ceiling(totals.Count * 0.95) - 1)];
        sb.AppendLine();
        sb.AppendLine("20 条查询端到端中位数的 p95: " + p95.ToString("0.0") + " ms（预算 50 ms）");
        sb.AppendLine("中位数的最小/最大: " + totals[0].ToString("0.0") + " / " + totals[totals.Count - 1].ToString("0.0") + " ms");
        sb.AppendLine();

        /* ---- 3) 本地重排：2000 条候选窗口 ---- */
        sb.AppendLine(MeasureRerank(context));
        sb.AppendLine();

        /* ---- 4) CJK 名字索引：构建耗时与内存 ---- */
        sb.AppendLine(MeasurePinyinIndex());

        Write("clp-qbar-bench.txt", sb.ToString());
        return 0;
    }

    /// <summary>
    /// 回归闸门：同一条查询跑两次，结果必须逐位一致。
    ///
    /// 为什么不再比"旧入口"：索引内核换成 tantivy 之后旧入口（内存快照扫描）已经删除，
    /// 进程内没有第二个能自比的实现。「与改造前保持一致」这条红线改由
    /// docs\qbar\golden 的基线 + tools/qbar-golden-compare.ps1 承担；
    /// 这里守的是另一半 —— 同一次运行里重复查询不能漂移
    /// （索引层用 BM25 排序，一旦把 searcher 缓存/重载写错，表现为"同样的词两次结果不同"）。
    /// </summary>
    private static string CompareStability(QbarQueryContext context, string query, QbarSearchOutcome outcome)
    {
        if (outcome.Backend == "off") return "索引不可用，跳过";

        QbarSearchOutcome again = QbarSearchEngine.Run(context, query, 40);
        var first = outcome.Hits.Select(h => h.Path).ToList();
        var second = again.Hits.Select(h => h.Path).ToList();

        int diff = 0;
        int shared = Math.Min(first.Count, second.Count);
        for (int i = 0; i < shared; i++)
        {
            if (!string.Equals(first[i], second[i], StringComparison.OrdinalIgnoreCase)) diff++;
        }
        if (first.Count != second.Count) diff += Math.Abs(first.Count - second.Count);

        return diff == 0 ? "稳定（" + first.Count + " 条）" : "两次不一致 " + diff + " 条";
    }

    /// <summary>2000 条候选的重排耗时：直接跑引擎（候选窗口即 2000），单独计 RankMs。</summary>
    private static string MeasureRerank(QbarQueryContext context)
    {
        var samples = new List<double>();
        int candidates = 0;
        /* 这些词在真实索引里有几千条命中，能真正把 2000 条候选窗口填满 ——
         * 用 doc: 那种谓词查询测重排是测不出窗口成本的（谓词只取 600 条）。 */
        foreach (string query in new[] { "txt", "dll", "exe", "report", "说明" })
        {
            for (int i = 0; i < 5; i++)
            {
                QbarSearchOutcome outcome = QbarSearchEngine.Run(context, query, 40);
                candidates = Math.Max(candidates, outcome.RawCount);
                samples.Add(outcome.RankMs);
            }
        }
        samples.Sort();
        return "本地重排: 候选最多 " + candidates + " 条，RankMs 中位 " +
               samples[samples.Count / 2].ToString("0.00") + " ms，最大 " +
               samples[samples.Count - 1].ToString("0.00") + " ms（预算 50 ms）";
    }

    /// <summary>CJK 名字索引：构建耗时 + 常驻内存。索引只收录名字含汉字，通常远小于总文件数。</summary>
    private static string MeasurePinyinIndex()
    {
        long before = GC.GetTotalMemory(true);
        var watch = Stopwatch.StartNew();
        QbarPinyinIndex.Invalidate();
        QbarPinyinIndex.Start();

        while (QbarPinyinIndex.Building && watch.Elapsed.TotalSeconds < 300) Thread.Sleep(50);
        watch.Stop();

        long after = GC.GetTotalMemory(true);
        var sb = new StringBuilder();
        /* GC.GetTotalMemory 的差值只能当参考：托管堆已经几百 MB 时，
         * 几 MB 的索引埋在里面看不出来。真正的口径是 EstimateBytes（结构估算），
         * 两个数一起报，谁也别替谁下结论。 */
        sb.AppendLine("CJK 名字索引: " + QbarPinyinIndex.Count + " 条，状态 " + QbarPinyinIndex.Status +
                      "，构建 " + watch.ElapsedMilliseconds + " ms，估算占用 " +
                      (QbarPinyinIndex.EstimatedBytes / 1024.0 / 1024.0).ToString("0.00") + " MB" +
                      "（GC 增量 " + ((after - before) / 1024.0 / 1024.0).ToString("0.0") +
                      " MB；按同一口径外推到 300000 条约 " +
                      (QbarPinyinIndex.Count > 0
                          ? (QbarPinyinIndex.EstimatedBytes * (300000.0 / QbarPinyinIndex.Count) / 1024.0 / 1024.0).ToString("0.0")
                          : "?") + " MB；实际有 24 MB 硬上限，超了停止收录）");

        foreach (string probe in new[] { "yh", "yinhang", "bg" })
        {
            var queryWatch = Stopwatch.StartNew();
            int hits = QbarPinyinIndex.Query(probe, 5).Count;
            queryWatch.Stop();
            sb.AppendLine("  拼音 " + probe + ": 命中 " + hits + " 条，耗时 " +
                          queryWatch.Elapsed.TotalMilliseconds.ToString("0.00") + " ms");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// --qbar-command "ol hello"：只跑命令解析与占位符展开，**不执行**。
    ///
    /// 为什么要它：命令行的命中发生在界面层（QbarService 拼动作行、回车才执行），
    /// 真机上说"敲了 ol 没反应"时，命令行这一份能直接指出是"没命中命令"、
    /// "命中了但程序路径找不到"，还是"参数模板没展开"——三者修法完全不同。
    /// 故意不执行：诊断开关不该有副作用，尤其 RunAsAdmin 的命令会弹 UAC。
    /// </summary>
    public static int RunCommand(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        string input = TakeQuery(args, "--qbar-command");
        var sb = new StringBuilder();
        sb.AppendLine("=== Qbar 命令调试（不执行，只解析） ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("输入: " + (input.Length == 0 ? "（空）" : input));

        Settings.AppSettings settings = SettingsStore.Load();
        List<QbarCommand> builtins = QbarCommandStore.Builtins();
        List<QbarCommand> effective = QbarCommandStore.Effective(settings.Qbar.Commands);
        int userCount = settings.Qbar.Commands.Count;
        sb.AppendLine("命令表: " + effective.Count + " 条（出厂 " + builtins.Count + " / 设置里存了 " + userCount + " 条）");
        foreach (QbarCommand item in effective)
            sb.AppendLine("  " + item.Keyword + " → " + item.Title + (QbarCommandStore.IsInternalAction(item.Id) ? "（内部动作）" : ""));

        QbarCommand? command = QbarCommandStore.Match(effective, input, out string tail);
        sb.AppendLine();
        if (command == null)
        {
            sb.AppendLine("命中: 无 —— 首个词没有对上任何命令关键字，回车会按「打开选中项」处理");
            sb.AppendLine("提示: 关键字要完全相等（不分大小写），后面必须跟空格再跟参数。");
            Write("clp-qbar-command.txt", sb.ToString());
            return 1;
        }

        sb.AppendLine("命中: " + command.Keyword + " | " + command.Title + " | id=" + command.Id);
        sb.AppendLine("  尾词: " + (tail.Length == 0 ? "（空）" : tail));
        sb.AppendLine("  模板: 程序=" + Show(command.Path) + " / 参数=" + Show(command.Parameter) +
                      " / 工作目录=" + Show(command.WorkingDirectory));
        sb.AppendLine("  开关: RunAsAdmin=" + command.RunAsAdmin + " / Silent=" + command.Silent);

        string cwd = Environment.CurrentDirectory;
        string sample = Path.Combine(cwd, "示例 文件.txt");
        string path = QbarCommandStore.Expand(command.Path, tail, cwd, sample).Trim();
        string arguments = QbarCommandStore.Expand(command.Parameter, tail, cwd, sample);
        string workdir = QbarCommandStore.Expand(command.WorkingDirectory, tail, cwd, sample).Trim();

        sb.AppendLine("  展开: 程序=" + Show(path) + " / 参数=" + Show(arguments));
        sb.AppendLine("        工作目录=" + Show(workdir) + " / 当前目录=" + cwd);
        sb.AppendLine("        占位符示例: action_path=" + sample + "（仅用于展示，文件不存在）");
        if (path.Length == 0)
            sb.AppendLine("  结论: 程序路径展开后为空 —— 执行会直接拒绝（「命令没有配置程序路径」）");
        else if (Directory.Exists(path))
            sb.AppendLine("  结论: 程序路径是一个目录 —— 执行会拒绝（「命令路径是一个目录」）");
        else if (File.Exists(path))
            sb.AppendLine("  结论: 程序存在，可直接执行");
        else
        {
            string? resolved = ResolveOnPath(path);
            sb.AppendLine(resolved == null
                ? "  结论: 既不是绝对路径也不在 PATH / App Paths 里 —— 交 shell 解析大概率失败"
                : "  结论: 交给 shell 按 PATH 解析 → " + resolved);
        }
        if (QbarCommandStore.IsInternalAction(command.Id))
            sb.AppendLine("  内部动作: 是 —— 真正执行在界面层（QbarWindow.RunInternalCommand），本诊断只解析");

        Write("clp-qbar-command.txt", sb.ToString());
        return 0;
    }

    private static string Show(string? text) => string.IsNullOrEmpty(text) ? "（空）" : text!;

    /// <summary>按 PATH 找一遍裸程序名，只用于诊断输出（真正的解析仍交给 shell）。</summary>
    private static string? ResolveOnPath(string path)
    {
        if (path.IndexOf(Path.DirectorySeparatorChar) >= 0 || path.IndexOf('/') >= 0) return null;
        string[] exts = { "", ".exe", ".cmd", ".bat", ".com" };
        string? dirs = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(dirs)) return null;
        foreach (string dir in dirs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in exts)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), path + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    private static QbarQueryContext BuildContext()
    {
        /* Load 自己会兜底并在 LoadFailure 里留原因，这里不再接一层 catch：
         * 万一它真抛了，命令行的堆栈比一句"读取设置失败"有用得多。 */
        return QbarSearchEngine.FromSettings(SettingsStore.Load());
    }

    private static long EnsureIndex()
    {
        var watch = Stopwatch.StartNew();
        QbarIndex.Start();
        /* Start() 只是拉起后台线程，"正在建立 / 正在核对快照"这两个标志要等线程真的跑起来
         * 才置位。所以既要等 Ready，也要等 Building / Starting 都归零；
         * 只有"什么都没在干、也没就绪"（启动失败）才给 500ms 宽限放行，别把命令行挂死。 */
        while (watch.Elapsed.TotalSeconds < 300)
        {
            if (QbarIndex.Ready && !QbarIndex.Building && !QbarIndex.Starting) break;
            if (!QbarIndex.Building && !QbarIndex.Starting && watch.ElapsedMilliseconds > 500) break;
            Thread.Sleep(200);
        }
        return watch.ElapsedMilliseconds;
    }

    private static string TakeQuery(string[] args, string flag)
    {
        return string.Join(" ", args
            .SkipWhile(a => !string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))).Trim();
    }

    /// <summary>
    /// --dict 词：把查词链路整体跑一遍（HTTP + 解析 + 结果行），不开 Qbar。
    ///
    /// 为什么要它：Qbar 里的查词是异步 + 界面驱动，出了问题只能靠眼睛看列表；
    /// 命令行这一份能给出"走的是哪个词典、耗时多少、解析出几条释义"，
    /// 网络不通时也能一眼看出是接口挂了还是解析写错了。
    /// </summary>
    public static int RunDictionary(string[]? args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        string word = "";
        for (int i = 0; args != null && i < args.Length - 1; i++)
            if (args[i].Equals("--dict", StringComparison.OrdinalIgnoreCase)) { word = args[i + 1]; break; }
        word = word.Trim();

        var sb = new StringBuilder();
        sb.AppendLine("查词: " + (word.Length == 0 ? "（没有给词）" : word));
        /* 欧路装在哪，直接写进查词排障输出的第一屏：
           "查词跑到了网页"这件事九成是没找到本地词典，而不是词典接口坏了。 */
        sb.AppendLine("本地欧路词典:");
        foreach (string line in EudicLocator.Describe().Split('\n')) sb.AppendLine("  " + line);
        if (word.Length == 0)
        {
            Write("clp-qbar-dict.txt", sb.ToString());
            return 2;
        }

        QbarDictionaryEntry entry;
        var sw = Stopwatch.StartNew();
        try
        {
            entry = QbarDictionary.LookupAsync(word, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            entry = new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "", ex.Message);
        }
        sw.Stop();

        sb.AppendLine("耗时: " + sw.ElapsedMilliseconds + " ms");
        sb.AppendLine("来源: " + (entry.Source.Length > 0 ? entry.Source : "（无）")
                      + (entry.Error == null ? "" : " · 错误: " + entry.Error));
        if (entry.Phonetic.Length > 0) sb.AppendLine("音标: " + entry.Phonetic);
        for (int i = 0; i < entry.Senses.Count; i++)
        {
            QbarDictionarySense sense = entry.Senses[i];
            sb.AppendLine("  " + (i + 1) + ". "
                          + (sense.PartOfSpeech.Length > 0 ? "[" + sense.PartOfSpeech + "] " : "")
                          + sense.Text);
        }
        if (entry.Senses.Count == 0) sb.AppendLine("  （没有释义）");

        Write("clp-qbar-dict.txt", sb.ToString());
        return entry.Ok ? 0 : 3;
    }

    /// <summary>同时写控制台、%TEMP% 和数据目录：程序是 GUI 子系统，只看控制台经常什么都看不到。</summary>
    private static void Write(string fileName, string text)
    {
        try { Console.Out.WriteLine(text); } catch { }
        foreach (string dir in new[] { System.IO.Path.GetTempPath(), AppPaths.DataDir })
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) continue;
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, fileName), text, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "写出查询调试结果");
            }
        }
    }
}
