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
/// --qbar-bench 同时充当回归闸门：同一批查询分别走旧入口（NativeSearchIndex.Search）
/// 和新引擎（QbarSearchEngine.Run），逐条比 top-N 顺序是否一致 —— 红线要求
/// 「默认查询行为与改造前保持一致」，这条必须能自动验。
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
        sb.AppendLine("索引状态: " + NativeSearchIndex.Status);
        if (NativeSearchIndex.Problem.Length > 0) sb.AppendLine("索引问题: " + NativeSearchIndex.Problem);
        sb.AppendLine("索引规模: 文件 " + NativeSearchIndex.FileCount + " 个，目录 " + NativeSearchIndex.DirCount + " 个");
        sb.AppendLine("等待索引: " + waited + " ms");
        /* 引擎拿不到快照时不会填 Translation（例如索引还没建好），这时现翻一次，
         * 让调试面板始终能看到"这句话被翻成了什么"。 */
        QbarTranslateResult translation = outcome.Translation
            ?? QbarQueryTranslator.Translate(query, QbarFilterCatalog.Create(context.Filters), DateTime.Now);

        sb.AppendLine();
        sb.AppendLine("归一化: " + translation.Normalized);
        sb.AppendLine("索引查询: " + translation.EverythingText);
        sb.AppendLine("后端: " + outcome.BackendText);
        if (QbarEverythingBackend.LastRejectedSample.Length > 0)
            sb.AppendLine("复核样本: " + QbarEverythingBackend.LastRejectedSample);
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
    /// --everything-status：Everything SDK 的体检。装没装、版本、索引加载没加载、
    /// 能不能真的查出一条结果，一次说清 —— 排查"为什么搜索是空的"时第一条命令。
    /// </summary>
    public static int RunEverythingStatus(string[]? args = null)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        bool start = args != null && args.Any(a => a.Equals("--start", StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        sb.AppendLine("=== Everything SDK 状态 ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        bool available = EverythingApi.IsAvailable;   // 读它会触发 Load（含内置副本释放）
        sb.AppendLine("内置目录: " + EverythingBundle.Folder);
        sb.AppendLine("内置客户端: " + (File.Exists(EverythingBundle.ClientPath) ? "已释放" : "未释放"));
        sb.AppendLine("SDK 可用: " + available);
        sb.AppendLine("DLL: " + (EverythingApi.DllPath.Length == 0 ? "（未找到）" : EverythingApi.DllPath));
        sb.AppendLine("DLL 来源: " + EverythingApi.SourceText);
        sb.AppendLine("版本: " + EverythingApi.VersionText);
        sb.AppendLine("索引已加载: " + EverythingApi.IsDbLoaded);
        sb.AppendLine("Everything 以管理员运行: " + EverythingApi.IsAdmin);
        sb.AppendLine("当前进程是管理员: " + (Core.ElevationHelper.IsElevated ? "是" : "否"));
        sb.AppendLine("状态: " + EverythingApi.StatusText);

        if (start && !EverythingBootstrap.Ready)
        {
            sb.AppendLine();
            sb.AppendLine("启动内置 Everything…");
            bool started = EverythingBootstrap.EnsureClient(8000);
            sb.AppendLine("启动结果: " + (started ? "客户端已在运行" : "还没起来"));
            sb.AppendLine("说明: " + EverythingBootstrap.LastDetail);
            sb.AppendLine("索引已加载: " + EverythingApi.IsDbLoaded);
            sb.AppendLine("Everything 以管理员运行: " + EverythingApi.IsAdmin);
            sb.AppendLine("状态: " + EverythingApi.StatusText);
        }
        sb.AppendLine();

        if (EverythingApi.IsAvailable)
        {
            var sw = Stopwatch.StartNew();
            bool ok = EverythingApi.TrySearch("ext:exe", 5, out List<EverythingHit> hits, out string detail);
            sw.Stop();
            sb.AppendLine("实测查询 ext:exe：" + (ok ? "成功" : "失败") + "，用时 " +
                          sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms，返回 " + hits.Count + " 条");
            sb.AppendLine("详情: " + detail);
            for (int i = 0; i < hits.Count; i++) sb.AppendLine("  " + hits[i].Path);

            sb.AppendLine();
            QbarQueryContext context = BuildContext();

            /* 探针别写死 "doc:"：过滤器关键字是用户资产（有人把 doc 改成 WD），
             * 写死的话探针会在人家的配置上显示 0 条，看着像后端坏了。
             * 取第一个带扩展名的过滤器，用它的真实关键字探。 */
            QbarFilterCatalog probeCatalog = QbarFilterCatalog.Create(context.Filters);
            QbarFilter? probe = probeCatalog.All.FirstOrDefault(f => f.Enabled && !string.IsNullOrEmpty(f.Extensions));
            string probeQuery = probe == null ? "ext:docx" : probe.Keyword + ":";
            QbarSearchOutcome outcome = QbarSearchEngine.Run(context, probeQuery, 10);
            sb.AppendLine("语义层 " + probeQuery + " → " + outcome.BackendText);
            sb.AppendLine("命中 " + outcome.Hits.Count + " 条；" + outcome.TimingText);
        }

        Write("clp-everything-status.txt", sb.ToString());
        return EverythingApi.IsAvailable && EverythingApi.IsDbLoaded ? 0 : 1;
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
        sb.AppendLine("索引状态: " + NativeSearchIndex.Status + "，文件 " + NativeSearchIndex.FileCount +
                      " 个，目录 " + NativeSearchIndex.DirCount + " 个，等待 " + waited + " ms");
        sb.AppendLine("构建于: " + (NativeSearchIndex.BuiltAt == DateTime.MinValue
            ? "（还没有）"
            : NativeSearchIndex.BuiltAt.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine("候选窗口: " + context.Options.MaxCandidates + " 条；规则数: " + context.Resolver.Count);
        string backend = !context.Options.PreferEverything
            ? "仅本地索引（自检/基准对照）"
            : context.Options.EverythingOnly
                ? "仅 Everything（拿不到就空结果，不回退）"
                : "Everything 优先（失败回退本地索引）";
        sb.AppendLine("后端: " + backend + " · " + EverythingApi.StatusText);
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

        /* ---- 2) 20 条查询：端到端 + 与旧入口逐条比对 ---- */
        sb.AppendLine("查询\t后端\t命中\t候选\t隐藏\t中位 ms\t最大 ms\t翻译 us\t旧入口比对");
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

            sb.AppendLine(query.Replace("\t", " ") + "\t" + (last.Backend == "everything" ? "E" : last.Backend == "off" ? "X" : "N") +
                          "\t" + last.Hits.Count + "\t" + last.RawCount + "\t" +
                          last.HiddenCount + "\t" + median.ToString("0.0") + "\t" + max.ToString("0.0") + "\t" +
                          (quoteWatch.Elapsed.TotalMilliseconds * 1000.0 / quoteRuns).ToString("0.0") + "\t" +
                          CompareWithLegacy(context, query, last));
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
    /// 回归闸门：纯关键词查询，旧入口给出的每一条结果都必须仍然出现在新结果里。
    ///
    /// 比的是"集合"而不是"顺序"：默认规则包会把系统目录降权，顺序本来就该变
    /// （用例 11 明确要求），继续拿顺序当回归标准只会天天报假警。新结果比旧的多
    /// 是正常的 —— 旧入口的候选窗口只有 40 条，新引擎按 2000 条取候选再重排。
    /// </summary>
    private static string CompareWithLegacy(QbarQueryContext context, string query, QbarSearchOutcome outcome)
    {
        QbarTranslateResult? translation = outcome.Translation;
        if (translation == null || !translation.Spec.IsPlain) return "非纯关键词，跳过";

        /* 拿"规则全关"的引擎跟旧入口比顺序 —— 这才测得到翻译层和扫描层有没有走样。
         * 默认规则包本来就会改顺序（用例 11 要求 C:\Windows 排后面），把带规则的
         * 结果拿去跟旧入口比，测的就不是回归而是规则是否生效了。 */
        bool rulesEnabled = context.Options.RulesEnabled;
        QbarSearchOutcome bare;
        context.Options.RulesEnabled = false;
        try
        {
            bare = QbarSearchEngine.Run(context, query, 40);
        }
        finally
        {
            context.Options.RulesEnabled = rulesEnabled;
        }

        IReadOnlyList<FileSearchHit> legacy = NativeSearchIndex.Search(query, 40, 15000);
        var legacyPaths = legacy.Where(h => !h.IsDir).Select(h => h.Path).ToList();
        var barePaths = bare.Hits.Where(h => !h.IsDir).Select(h => h.Path).ToList();

        /* 比"旧结果的相对顺序"而不是逐位相等：增强路径（拼音 / 子序列）会给新结果
         * 追加旧入口根本没有的条目，逐位比就永远对不上。真正要守的是
         * 旧入口给的每一条都还在，且它们之间的先后关系没被打乱。 */
        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < barePaths.Count; i++) indexOf[barePaths[i]] = i;

        int missing = 0;
        int outOfOrder = 0;
        int cursor = -1;
        foreach (string path in legacyPaths)
        {
            if (!indexOf.TryGetValue(path, out int at))
            {
                missing++;
                continue;
            }
            if (at < cursor) outOfOrder++;
            else cursor = at;
        }

        var rankedPaths = outcome.Hits.Where(h => !h.IsDir).Select(h => h.Path).ToList();
        int moved = 0;
        int rankedShared = Math.Min(barePaths.Count, rankedPaths.Count);
        for (int i = 0; i < rankedShared; i++)
        {
            if (!string.Equals(barePaths[i], rankedPaths[i], StringComparison.OrdinalIgnoreCase)) moved++;
        }

        string head;
        if (legacyPaths.Count == 0) head = "一致（0 条）";
        else if (missing > 0) head = "缺失 " + missing + "/" + legacyPaths.Count + " 条";
        else if (outOfOrder > 0) head = "乱序 " + outOfOrder + " 条";
        else head = "一致（" + legacyPaths.Count + " 条）";

        return moved > 0 ? head + "；规则重排 " + moved + " 条" : head;
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
        NativeSearchIndex.Start();
        while (NativeSearchIndex.Building && watch.Elapsed.TotalSeconds < 300) Thread.Sleep(200);
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
