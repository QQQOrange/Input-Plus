using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Qbar.Search;

/// <summary>
/// 一次查询的输入上下文。由 QbarProviders 从设置里现搭，Engine 只读不写。
/// Catalog/Resolver 做惰性缓存：设置里改一条规则不该让每次按键都重新编译一遍正则。
/// </summary>
internal sealed class QbarQueryContext
{
    private QbarFilterCatalog? _catalog;
    private QbarPriorityResolver? _resolver;
    private string _signature = "";

    public QbarSearchOptions Options { get; set; } = new QbarSearchOptions();
    public List<QbarFilter> Filters { get; set; } = new List<QbarFilter>();
    public List<QbarPriorityRule> Rules { get; set; } = new List<QbarPriorityRule>();
    public List<QbarCommand> Commands { get; set; } = new List<QbarCommand>();

    public QbarFilterCatalog Catalog
    {
        get
        {
            QbarFilterCatalog? cached = _catalog;
            if (cached != null) return cached;
            cached = QbarFilterCatalog.Create(Filters);
            _catalog = cached;
            return cached;
        }
    }

    /// <summary>规则集身份串：只有它变了才重建 resolver（重建要编译正则，不便宜）。</summary>
    public QbarPriorityResolver Resolver
    {
        get
        {
            string signature = BuildSignature();
            if (_resolver != null && signature == _signature) return _resolver;

            _resolver = QbarPriorityResolver.Create(Options.RulesEnabled, Options.BuiltinRulesEnabled, Rules,
                                                    Options.DisabledBuiltins);
            _signature = signature;
            return _resolver;
        }
    }

    private string BuildSignature()
    {
        var sb = new StringBuilder(64);
        sb.Append(Options.RulesEnabled ? '1' : '0').Append(Options.BuiltinRulesEnabled ? '1' : '0');
        sb.Append(Options.HideLowResults ? '1' : '0');
        /* 逐条关内置规则必须进签名：否则用户拨了开关，resolver 还按旧签名命中缓存，
         * 表现就是"设置页改了，搜索一点变化都没有"，只能重启才好。 */
        foreach (string key in Options.DisabledBuiltins)
            sb.Append(key).Append('~');
        sb.Append('|');
        sb.Append(Filters.Count).Append('|');
        foreach (QbarFilter filter in Filters)
        {
            if (filter == null) continue;
            sb.Append(filter.Keyword).Append(',')
              .Append(filter.Enabled ? '1' : '0').Append(',')
              .Append(filter.ExtraQuery).Append(';');
        }
        sb.Append('#');
        foreach (QbarPriorityRule rule in Rules)
        {
            if (rule == null) continue;
            sb.Append(rule.Id).Append(',').Append(rule.Level).Append(',')
              .Append(rule.Enabled ? '1' : '0').Append(';');
        }
        return sb.ToString();
    }
}

internal readonly record struct QbarHighlight(int Start, int Length);

/// <summary>一条文件结果。Name/Highlights 是给 UI 直接用的，别在界面层再拆路径。</summary>
internal sealed class QbarSearchHit
{
    public QbarSearchHit(string path, bool isDir, int score, int tier, long sizeBytes, int day)
    {
        Path = path;
        IsDir = isDir;
        Score = score;
        Tier = tier;
        SizeBytes = sizeBytes;
        Day = day;
        Name = isDir ? LeafOf(path) : System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string Name { get; }
    public bool IsDir { get; }
    public int Score { get; }
    public int Tier { get; }
    public long SizeBytes { get; }
    public int Day { get; }

    public List<QbarHighlight> Highlights { get; } = new List<QbarHighlight>();

    private static string LeafOf(string path)
    {
        if (path.Length == 0) return path;
        string trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) return path;

        int slash = trimmed.LastIndexOfAny(new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar });
        return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
    }
}

internal sealed class QbarSearchOutcome
{
    public List<QbarSearchHit> Hits { get; } = new List<QbarSearchHit>();
    public QbarTranslateResult? Translation { get; set; }

    public int RawCount { get; set; }
    public int HiddenIgnored { get; set; }
    public int HiddenDoNotIndex { get; set; }

    /// <summary>「默认不展示系统及隐藏文件」打开后被挡掉的低权结果数。</summary>
    public int HiddenLow { get; set; }
    public List<string> HiddenSamples { get; } = new List<string>();

    public double TranslateMs { get; set; }
    public double SearchMs { get; set; }
    public double RankMs { get; set; }
    public double TotalMs { get; set; }

    /// <summary>
    /// 重排段的分段耗时（命中 INPUTPLUS_QBAR_PROFILE=1 时才填）。
    /// 只在排障时开：Stopwatch 本身很便宜，但没人看的字段不值得进热路径。
    /// </summary>
    public string Profile { get; set; } = "";

    /// <summary>本次结果由谁提供："everything" / "native"（自建快照）/ "off"（只用 Everything 但它不可用，结果为 0）。</summary>
    public string Backend { get; set; } = "native";

    /// <summary>实际发给 Everything 的查询串（走自建索引时为空）。</summary>
    public string BackendQuery { get; set; } = "";

    /// <summary>后端的一句话说明：可用性、命中数、被本地复核剔除多少条。</summary>
    public string BackendDetail { get; set; } = "";

    public bool TimedOut { get; set; }
    public bool FuzzyUsed { get; set; }
    public bool PinyinUsed { get; set; }
    public bool PinyinPending { get; set; }

    /// <summary>本页统计：类型桶计数。只在候选窗口内数，绝不为此另发一次查询。</summary>
    public List<(string Label, int Count)> Facets { get; } = new List<(string Label, int Count)>();

    public int HiddenCount => HiddenIgnored + HiddenDoNotIndex + HiddenLow;

    /// <summary>分面摘要（"本页"口径）。界面直接显示这一行，不自己拼。</summary>
    public string FacetSummary()
    {
        var parts = new List<string>();
        for (int i = 0; i < Facets.Count; i++)
        {
            (string label, int count) = Facets[i];
            if (count <= 0) continue;
            parts.Add(label + " " + count);
        }
        return parts.Count == 0 ? "" : "本页：" + string.Join(" · ", parts);
    }

    public string TimingText =>
        "翻译 " + TranslateMs.ToString("0.0") + "ms · 检索 " + SearchMs.ToString("0.0") +
        "ms · 重排 " + RankMs.ToString("0.0") + "ms · 合计 " + TotalMs.ToString("0.0") + "ms";

    /// <summary>查询调试面板里的后端一行：谁答的、问的什么、剔了多少。</summary>
    public string BackendText
    {
        get
        {
            string head = Backend switch
            {
                "everything" => "Everything",
                "off" => "Everything（不可用，未回退本地快照）",
                _ => "本地索引",
            };
            string query = BackendQuery.Length == 0 ? "" : " · \"" + BackendQuery + "\"";
            string detail = BackendDetail.Length == 0 ? "" : " · " + BackendDetail;
            return "后端 " + head + query + detail;
        }
    }
}

/// <summary>
/// 语义层的主入口：输入文本 → 翻译 → 原生索引 → 优先级剔除 → 本地重排 → 高亮。
/// 这一层不许碰 UI，也不许碰 Everything —— 它只是"把用户的话变成一次结构化查询"。
/// </summary>
internal static class QbarSearchEngine
{
    private const int TierBonusHigh = 120;
    private const int TierBonusLow = -120;

    /// <summary>从设置现搭一份查询上下文。调用方（Providers / 界面后台线程）共用，避免各自抄一遍字段映射。</summary>
    public static QbarQueryContext FromSettings(AppSettings settings)
    {
        var context = new QbarQueryContext();
        if (settings == null) return context;

        var qbar = settings.Qbar;
        context.Options = qbar.Search ?? new QbarSearchOptions();
        context.Filters = qbar.Filters ?? new List<QbarFilter>();
        context.Rules = qbar.Priority ?? new List<QbarPriorityRule>();
        context.Commands = qbar.Commands ?? new List<QbarCommand>();
        return context;
    }

    public static QbarSearchOutcome Run(QbarQueryContext context, string input, int limit)
    {
        var outcome = new QbarSearchOutcome();
        if (context == null) return outcome;

        var total = Stopwatch.StartNew();
        string text = (input ?? "").Trim();
        if (limit <= 0) limit = 12;

        var translate = Stopwatch.StartNew();
        QbarTranslateResult translation = QbarQueryTranslator.Translate(text, context.Catalog, DateTime.Now);
        outcome.Translation = translation;
        outcome.TranslateMs = translate.Elapsed.TotalMilliseconds;

        if (translation.Normalized.Length == 0)
        {
            outcome.TotalMs = total.Elapsed.TotalMilliseconds;
            return outcome;
        }

        QbarSearchSpec spec = translation.Spec;
        int window = Math.Max(200, Math.Min(20000, context.Options.MaxCandidates));

        /* 只有谓词（doc: / date:week 这类）时没有相关性可排：所有候选取的都是
         * 同一个长度/深度公式算出来的分。这时要 2000 条候选纯属浪费 —— 索引层得
         * 为它把整张表解码一遍。降到 600 条，页面照样塞得满，扫描量掉一个量级。 */
        if (!spec.HasKeywords) window = Math.Min(window, 600);

        var search = Stopwatch.StartNew();

        /* 后端选择：Everything 能用就用它（索引由 Everything 维护，覆盖面比自建快照大），
         * 任何一步不成立就退回自建快照。注意 TryRun 返回 true 但 0 条是**有效结果**，
         * 不是失败——不能再补一次本地查询，否则"Everything 里没有"会被伪装成"本地有"。 */
        IReadOnlyList<FileSearchHit> raw = Array.Empty<FileSearchHit>();
        bool usedEverything = false;
        if (context.Options.PreferEverything)
        {
            usedEverything = QbarEverythingBackend.TryRun(
                spec, window, out List<FileSearchHit> everythingHits, out string everythingQuery, out string backendDetail);
            if (usedEverything)
            {
                raw = everythingHits;
                outcome.Backend = "everything";
                outcome.BackendQuery = everythingQuery;
            }
            outcome.BackendDetail = backendDetail;
        }

        if (!usedEverything)
        {
            /* 只用 Everything：拿不到候选就返回空，让界面显示"Everything 没就绪"，
             * 而不是悄悄换成本地快照 —— 两个索引的覆盖面差一个量级，静默降级会让
             * 用户以为"文件不存在"。本地快照只在显式 PreferEverything=false 时用
             * （自检、--qbar-bench 的对照，见 DECISIONS D25）。 */
            /* 正则例外：Everything 1.4 走不了正则（见 QbarEverythingBackend.TryRun 的说明），
             * 这一类查询本来就该由本地快照回答，标签也要如实写成本地索引，
             * 不能挂一句"Everything 不可用"让用户去修一个没坏的东西。 */
            bool localRegex = spec.RegexText.Length > 0;
            if (context.Options.PreferEverything && context.Options.EverythingOnly && !localRegex)
            {
                outcome.Backend = "off";
                if (outcome.BackendDetail.Length == 0)
                    outcome.BackendDetail = "Everything 不可用（客户端没运行或索引未加载），按设置不回退本地快照";
            }
            else
            {
                raw = NativeSearchIndex.SearchSpec(spec, window, context.Options.QueryTimeoutMs);
                outcome.Backend = "native";
                if (outcome.BackendDetail.Length == 0)
                    outcome.BackendDetail = context.Options.PreferEverything
                        ? "Everything 不可用，已降级到本地索引"
                        : "已在设置里关闭 Everything 后端";
                if (localRegex) outcome.BackendDetail = "正则查询由本地快照执行（Everything 只有全局正则开关）";
            }
        }
        outcome.SearchMs = search.Elapsed.TotalMilliseconds;
        outcome.RawCount = raw.Count;

        var rank = Stopwatch.StartNew();
        QbarPriorityResolver resolver = context.Options.RulesEnabled
            ? context.Resolver
            : QbarPriorityResolver.Empty;

        bool profiling = Environment.GetEnvironmentVariable("INPUTPLUS_QBAR_PROFILE") == "1";
        var profile = profiling ? new System.Text.StringBuilder() : null;
        var stage = profiling ? Stopwatch.StartNew() : null;

        var keep = new List<(FileSearchHit Hit, int Tier, int Score)>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            FileSearchHit hit = raw[i];
            QbarPriorityDecision decision = resolver.Resolve(hit.Path, hit.IsDir);
            if (decision.Level == QbarPriorityLevel.DoNotIndex)
            {
                outcome.HiddenDoNotIndex++;
                Remember(outcome.HiddenSamples, hit.Path);
                continue;
            }
            if (decision.Level == QbarPriorityLevel.Ignored)
            {
                outcome.HiddenIgnored++;
                Remember(outcome.HiddenSamples, hit.Path);
                continue;
            }
            /* 「默认不展示系统及隐藏文件的搜索结果」：把 Low 档直接挡在结果外。
             * 只挡 Low，不动 Normal/High —— 用户打开这个开关是想少看噪音，
             * 不是想把 C:\Windows 里的东西永久判死；关掉开关立刻回来。 */
            if (context.Options.HideLowResults && decision.Level == QbarPriorityLevel.Low)
            {
                outcome.HiddenLow++;
                Remember(outcome.HiddenSamples, hit.Path);
                continue;
            }

            int tier = decision.Level == QbarPriorityLevel.High ? 2
                     : decision.Level == QbarPriorityLevel.Low ? 0
                     : 1;
            int bonus = tier == 2 ? TierBonusHigh : tier == 0 ? TierBonusLow : 0;
            keep.Add((hit, tier, hit.Score + bonus));
        }

        /* 排序必须和原生索引同一套比较（分 → 路径长度 → 序数），否则"同样输入同样结果"
         * 这条回归红线会在同分条目上漂移，用户只会觉得"怎么每次顺序都不太一样"。 */
        if (profile != null)
        {
            profile.Append("规则 ").Append(stage!.Elapsed.TotalMilliseconds.ToString("0.0")).Append("ms");
            stage.Restart();
        }

        keep.Sort(CompareRanked);

        if (profile != null)
        {
            profile.Append(" · 排序 ").Append(stage!.Elapsed.TotalMilliseconds.ToString("0.0")).Append("ms");
            stage.Restart();
        }

        int take = Math.Min(limit, keep.Count);
        for (int i = 0; i < take; i++)
        {
            (FileSearchHit hit, int tier, int score) = keep[i];
            var view = new QbarSearchHit(hit.Path, hit.IsDir, score, tier,
                (long)hit.SizeKb * 1024L, hit.Day);
            ApplySdkHighlights(view, hit.Highlights);
            BuildHighlights(view, translation);
            outcome.Hits.Add(view);
        }

        BuildFacets(outcome);

        if (profile != null)
        {
            profile.Append(" · 视图 ").Append(stage!.Elapsed.TotalMilliseconds.ToString("0.0"));
            profile.Append("ms · 候选 ").Append(raw.Count).Append(" 条");
            outcome.Profile = profile.ToString();
        }

        outcome.RankMs = rank.Elapsed.TotalMilliseconds;
        /* 增强（拼音 / 子序列）候选来自**本地快照**，而 Everything 模式下主结果来自**
         * Everything 的索引**。两者不同源，所以只要 Everything 这条路走不通（Backend="off"），
         * 就一条增强都不能补 —— 否则界面上会出现"后端不可用"却列着 24 条本地命中的怪状，
         * 用户没法判断这些文件到底在不在 Everything 的索引里。 */
        if (outcome.Backend.Length > 0 && outcome.Backend != "off")
            TryEnhance(context, translation, text, outcome);
        outcome.TotalMs = total.Elapsed.TotalMilliseconds;
        return outcome;
    }

    /// <summary>
    /// 分面计数：只数"本页看得见的这些"（outcome.Hits）。
    /// 为什么不按整个候选窗口数：窗口 2000 条里绝大多数用户根本翻不到，
    /// 数出来的是"库里有几个"而不是"这里有几个"，和眼前的列表对不上就是骗人。
    /// 所以界面上必须标"本页"。
    /// </summary>
    private static void BuildFacets(QbarSearchOutcome outcome)
    {
        if (outcome.Hits.Count == 0) return;

        var doc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var video = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        QbarFilterCatalog.FillExtensions(QbarFilterCatalog.DocExtensions, doc);
        QbarFilterCatalog.FillExtensions(QbarFilterCatalog.PicExtensions, pic);
        QbarFilterCatalog.FillExtensions(QbarFilterCatalog.VideoExtensions, video);
        QbarFilterCatalog.FillExtensions(QbarFilterCatalog.AudioExtensions, audio);

        int docs = 0, pics = 0, videos = 0, audios = 0, programs = 0, others = 0, dirs = 0, fresh = 0;
        int today = QbarSyntaxValues.ToDay(DateTime.Now);

        for (int i = 0; i < outcome.Hits.Count; i++)
        {
            QbarSearchHit hit = outcome.Hits[i];
            if (hit.IsDir) dirs++;
            else
            {
                string ext = System.IO.Path.GetExtension(hit.Path);
                if (ext.StartsWith(".")) ext = ext.Substring(1);
                if (doc.Contains(ext)) docs++;
                else if (pic.Contains(ext)) pics++;
                else if (video.Contains(ext)) videos++;
                else if (audio.Contains(ext)) audios++;
                else if (ext.Equals("exe", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals("lnk", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals("url", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals("bat", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals("cmd", StringComparison.OrdinalIgnoreCase)) programs++;
                else others++;
            }

            if (hit.Day >= 0 && hit.Day != QbarSyntaxValues.UnknownDay && today - hit.Day is >= 0 and <= 7) fresh++;
        }

        outcome.Facets.Add(("文档", docs));
        outcome.Facets.Add(("图片", pics));
        outcome.Facets.Add(("视频", videos));
        outcome.Facets.Add(("音频", audios));
        outcome.Facets.Add(("程序", programs));
        outcome.Facets.Add(("文件夹", dirs));
        outcome.Facets.Add(("其他", others));
        outcome.Facets.Add(("近7天", fresh));
    }

    private static void Remember(List<string> samples, string path)
    {
        if (samples.Count < 5) samples.Add(path);
    }

    private static int CompareRanked((FileSearchHit Hit, int Tier, int Score) a, (FileSearchHit Hit, int Tier, int Score) b)
    {
        int byScore = b.Score.CompareTo(a.Score);
        if (byScore != 0) return byScore;

        int byLength = a.Hit.Path.Length.CompareTo(b.Hit.Path.Length);
        if (byLength != 0) return byLength;

        return string.Compare(a.Hit.Path, b.Hit.Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 高亮只算"名字里命中的片段"。数据层只给区间，拼字符串是 UI 的事 ——
    /// 在这里插标记会把路径变成不可用的字符串（复制、打开都要用原文）。
    /// </summary>
    private static void BuildHighlights(QbarSearchHit hit, QbarTranslateResult translation)
    {
        if (hit.Name.Length == 0) return;

        /* Everything 已经给了精确区间就别再猜一遍：它按自己的匹配规则（含正则、通配符、
           MatchPath）算出真正命中的字符，我们按关键词子串硬找会在这些查询里标错位置。 */
        if (hit.Highlights.Count > 0) return;

        for (int i = 0; i < translation.Spec.Terms.Count; i++)
            AddRanges(hit, translation.Spec.Terms[i]);

        for (int i = 0; i < translation.Spec.Phrases.Count; i++)
            AddRanges(hit, translation.Spec.Phrases[i]);
    }

    /// <summary>把 SDK 交回来的交错区间 [起点,长度,…] 落到名字上（越界的一律裁掉）。</summary>
    private static void ApplySdkHighlights(QbarSearchHit hit, int[]? marks)
    {
        if (marks == null || marks.Length < 2 || hit.Name.Length == 0) return;
        for (int i = 0; i + 1 < marks.Length; i += 2)
        {
            int start = marks[i];
            int length = marks[i + 1];
            if (start < 0 || length <= 0 || start >= hit.Name.Length) continue;
            if (start + length > hit.Name.Length) length = hit.Name.Length - start;
            hit.Highlights.Add(new QbarHighlight(start, length));
        }
    }

    private static void AddRanges(QbarSearchHit hit, string needle)
    {
        if (string.IsNullOrEmpty(needle) || needle.Length > hit.Name.Length) return;

        int from = 0;
        int added = 0;
        while (from <= hit.Name.Length - needle.Length && added < 4)
        {
            int at = hit.Name.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;

            if (!Overlaps(hit, at, needle.Length))
            {
                hit.Highlights.Add(new QbarHighlight(at, needle.Length));
                added++;
            }
            from = at + Math.Max(1, needle.Length);
        }
    }

    private static bool Overlaps(QbarSearchHit hit, int start, int length)
    {
        for (int i = 0; i < hit.Highlights.Count; i++)
        {
            QbarHighlight h = hit.Highlights[i];
            if (start < h.Start + h.Length && h.Start < start + length) return true;
        }
        return false;
    }

    /// <summary>
    /// 拼音 + 子序列兜底。两者都是"候选窗口内找不到时才启用"，否则一次普通查询
    /// 会额外扫几十万条名字，性能预算立刻爆掉。
    /// </summary>
    private static void TryEnhance(QbarQueryContext context, QbarTranslateResult translation, string text, QbarSearchOutcome outcome)
    {
        QbarSearchOptions options = context.Options;
        if (!options.FuzzyWhenSparse) return;
        if (!translation.Spec.IsPlain) return;
        if (text.Length < Math.Max(2, options.MinFuzzyLength)) return;
        if (outcome.Hits.Count >= options.FuzzyThreshold) return;
        if (!IsAsciiWord(text)) return;

        /* 增强出来的候选也必须过优先级闸门：否则"目录被设为 DoNotIndex"这种设置
         * 会被子序列兜底偷偷绕过去（第一版就踩了这个坑 —— 190 条被剔除，24 条又溜回来）。 */
        QbarPriorityResolver resolver = options.RulesEnabled ? context.Resolver : QbarPriorityResolver.Empty;

        try
        {
            if (options.PinyinEnabled)
            {
                if (!QbarPinyinIndex.Ready)
                {
                    QbarPinyinIndex.Start();
                    outcome.PinyinPending = true;
                }
                else
                {
                    List<QbarPinyinHit> pinyin = QbarPinyinIndex.Query(text, 24);
                    if (pinyin.Count > 0)
                    {
                        outcome.PinyinUsed = true;
                        MergePinyin(pinyin, translation, outcome, resolver, options.HideLowResults);
                    }
                }
            }

            if (outcome.Hits.Count < options.FuzzyThreshold)
            {
                IReadOnlyList<FileSearchHit> fuzzy =
                    NativeSearchIndex.FuzzyScan(text, 400000, 24, Math.Min(120, options.QueryTimeoutMs));
                if (fuzzy.Count > 0)
                {
                    outcome.FuzzyUsed = true;
                    MergeFuzzy(fuzzy, translation, outcome, resolver, options.HideLowResults);
                }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "模糊/拼音增强");
        }
    }

    private static void MergePinyin(List<QbarPinyinHit> hits, QbarTranslateResult translation,
        QbarSearchOutcome outcome, QbarPriorityResolver resolver, bool hideLow)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < outcome.Hits.Count; i++) seen.Add(outcome.Hits[i].Path);

        for (int i = 0; i < hits.Count && outcome.Hits.Count < 40; i++)
        {
            QbarPinyinHit hit = hits[i];
            if (!seen.Add(hit.Path)) continue;
            if (!PassPriority(resolver, hit.Path, hit.IsDir, outcome, hideLow, out int bonus)) continue;

            var view = new QbarSearchHit(hit.Path, hit.IsDir, 200 + hit.Quality + bonus, 1, 0, -1);
            BuildHighlights(view, translation);
            outcome.Hits.Add(view);
        }
    }

    private static void MergeFuzzy(IReadOnlyList<FileSearchHit> hits, QbarTranslateResult translation,
        QbarSearchOutcome outcome, QbarPriorityResolver resolver, bool hideLow)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < outcome.Hits.Count; i++) seen.Add(outcome.Hits[i].Path);

        for (int i = 0; i < hits.Count && outcome.Hits.Count < 40; i++)
        {
            FileSearchHit hit = hits[i];
            if (!seen.Add(hit.Path)) continue;
            if (!PassPriority(resolver, hit.Path, hit.IsDir, outcome, hideLow, out int bonus)) continue;

            var view = new QbarSearchHit(hit.Path, hit.IsDir, 150 + hit.Score / 4 + bonus, 1, (long)hit.SizeKb * 1024L, hit.Day);
            BuildHighlights(view, translation);
            outcome.Hits.Add(view);
        }
    }

    /// <summary>增强候选的优先级闸门：被隐藏的记进诊断并丢弃，其余把档位加成带出去。</summary>
    private static bool PassPriority(QbarPriorityResolver resolver, string path, bool isDir,
        QbarSearchOutcome outcome, bool hideLow, out int bonus)
    {
        bonus = 0;
        QbarPriorityDecision decision = resolver.Resolve(path, isDir);
        if (decision.Level == QbarPriorityLevel.DoNotIndex)
        {
            outcome.HiddenDoNotIndex++;
            Remember(outcome.HiddenSamples, path);
            return false;
        }
        if (decision.Level == QbarPriorityLevel.Ignored)
        {
            outcome.HiddenIgnored++;
            Remember(outcome.HiddenSamples, path);
            return false;
        }
        if (hideLow && decision.Level == QbarPriorityLevel.Low)
        {
            outcome.HiddenLow++;
            Remember(outcome.HiddenSamples, path);
            return false;
        }
        if (decision.Level == QbarPriorityLevel.High) bonus = TierBonusHigh;
        else if (decision.Level == QbarPriorityLevel.Low) bonus = TierBonusLow;
        return true;
    }

    private static bool IsAsciiWord(string text)
    {
        if (text.Length == 0) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ' || c == '-' || c == '_') continue;
            bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            bool digit = c >= '0' && c <= '9';
            if (!letter && !digit) return false;
        }
        return true;
    }
}
