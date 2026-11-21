using System;
using System.Collections.Generic;

namespace InputPlus.Core.Qbar.Search;

/// <summary>
/// Everything 后端：把语义层已经翻译好的 <see cref="QbarSearchSpec"/> 再还原成一条
/// Everything 查询串，取回候选窗口，然后用与自建索引**同一套**谓词逐条复核。
///
/// 为什么要二次翻译而不是直接用界面上的原始输入：
///   · 原始输入里有 <c>doc:</c> 这类 Listary 关键字，Everything 不认识；
///   · 反过来，Everything 的原生语法（ext:/dm:/size:/path:）我们已经在 spec 里结构化过了，
///     从结构再生成一次，两边（自建索引 / Everything）就共享同一个语义源。
///
/// wfn: / regex: 会下发给 Everything（用它的 wfn: 与 regex: 逐词语法，见 BuildQuery 里的说明）：
/// 不下发就等于查一条空查询，窗口里根本没候选，本地复核再严也筛不出来。
/// content: 仍然不下发——它会让 Everything 去读文件内容，几秒钟起步，与"输入即出结果"冲突。
/// </summary>
internal static class QbarEverythingBackend
{
    /// <summary>最近一次真正发给 Everything 的查询串（查询调试面板用）。</summary>
    internal static string LastQuery { get; private set; } = "";

    internal static string LastDetail { get; private set; } = "";

    /// <summary>最近一次被本地复核拒绝的前几条原始路径（排障用：区分"Everything 没给"和"我们剔了"）。</summary>
    internal static string LastRejectedSample { get; private set; } = "";

    /// <summary>
    /// 跑一次 Everything 查询并做本地复核。
    /// 返回 false = 这条路不可用（没装/没运行/索引没加载/查询报错），调用方降级到自建索引；
    /// 返回 true 且 hits 为空 = Everything 确实没有命中，这是有效结果，不许当失败处理。
    /// </summary>
    internal static bool TryRun(
        QbarSearchSpec spec,
        int window,
        out List<FileSearchHit> hits,
        out string queryUsed,
        out string detail)
    {
        hits = new List<FileSearchHit>();
        queryUsed = "";
        detail = "";

        if (spec == null)
        {
            detail = "空查询不走 Everything";
            return false;
        }

        /* 正则交给本地快照，不是偷懒：Everything 1.4 没有逐词 regex: 语法
         * （实测 regex:^rep.*\.docx$ 恒 0 命中），而 Everything_SetRegex(TRUE) 是整条查询的
         * 全局开关——一开，同一条里的普通词也会被当正则解释。两条路都会给出错误结果，
         * 所以这一类查询显式走本地索引（快照支持正则，见 NativeSearchIndex.SpecFilter）。 */
        bool regexOnly = IsRegexOnly(spec);
        if (spec.RegexText.Length > 0 && !regexOnly)
        {
            detail = "正则 + 其它条件：走本地快照（Everything 的正则开关是整条查询全局的）";
            LastDetail = detail;
            return false;
        }

        string query = BuildQuery(spec);
        if (query.Length == 0)
        {
            detail = "空查询不走 Everything";
            return false;
        }

        queryUsed = query;
        LastQuery = query;

        if (!EverythingApi.IsAvailable)
        {
            detail = EverythingApi.StatusText;
            LastDetail = detail;
            return false;
        }

        /* 索引没加载时 Everything 的 Query 依然"成功"，只是返回 0 条 ——
         * 那会把"索引还在建"显示成"没有这个文件"，比降级更误导人。 */
        if (!EverythingApi.IsDbLoaded)
        {
            detail = EverythingApi.StatusText;
            LastDetail = detail;
            return false;
        }

        /* 两趟取窗口：
         *   第一趟 MatchPath=FALSE —— 只让词命中文件名。本机 115 万条索引实测 34.7ms(p95)，
         *     而开 MatchPath 要 56.0ms：Everything 得为每个候选再扫一遍路径。
         *   第二趟只在前一趟"太稀"时补（< TopUpBelow 条）—— 这时用户其实是在找一个具体文件，
         *     多花 30ms 换全量召回是值得的；结果已经有几十上百条时，用户不会翻到第 41 条之后。
         * 第二趟的结果是第一趟的超集（名字命中必然也是路径命中），并起来去重即可。 */
        /* 阈值取 8 而不是 40：实测 24～26 条命中的查询（rprt / yinhang）如果也补扫，
         * 端到端从 20ms 涨到 55ms —— 二十几条结果对用户已经够选，补扫纯属白花 30ms。
         * 只有"几乎什么都没有"（≤7 条）才值得再扫一趟路径。 */
        const int TopUpBelow = 8;
        if (!EverythingApi.TrySearch(query, window, out List<EverythingHit> raw, out string apiDetail,
                regexOnly, matchPath: false))
        {
            detail = apiDetail;
            LastDetail = detail;
            return false;
        }

        bool toppedUp = false;
        if (!regexOnly && raw.Count < TopUpBelow && window > raw.Count)
        {
            if (EverythingApi.TrySearch(query, window, out List<EverythingHit> wide, out _, regexOnly, matchPath: true))
            {
                toppedUp = true;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < raw.Count; i++) seen.Add(raw[i].Path);
                for (int i = 0; i < wide.Count && raw.Count < window; i++)
                {
                    if (seen.Add(wide[i].Path)) raw.Add(wide[i]);
                }
                apiDetail += " · 路径补扫后 " + raw.Count + " 条";
            }
        }

        /* 复核用与索引同源的谓词：Everything 的 MatchPath 是"词可命中路径"，
         * 但它不看 size:/dm: 的边界细则、不认 NegTerms 的路径部分、也不做 wfn: 的 glob。
         * 不复核就会出现"同一个查询换后端结果集不一样"的回归（红线第 7 条）。 */
        var filter = new NativeSearchIndex.SpecFilter(spec);
        int rejected = 0;
        var sample = new List<string>(3);
        for (int i = 0; i < raw.Count; i++)
        {
            EverythingHit hit = raw[i];
            int sizeKb = hit.SizeBytes <= 0 ? 0 : (int)Math.Min(hit.SizeBytes / 1024L, int.MaxValue);
            int day = hit.Day;
            if (!filter.Accept(hit.Path, hit.IsDir, sizeKb, day, out int score))
            {
                rejected++;
                if (sample.Count < 3) sample.Add((hit.IsDir ? "[目录] " : "[文件] ") + hit.Path);
                continue;
            }
            hits.Add(new FileSearchHit(hit.Path, hit.IsDir, score, sizeKb, day, hit.Highlights));
        }

        detail = apiDetail + " · SDK " + EverythingApi.LastQueryMs + " ms"
                 + (toppedUp ? "（含路径补扫）" : "") + " · 本地复核剔除 " + rejected + " 条";
        LastDetail = detail;
        LastRejectedSample = string.Join("；", sample);
        return true;
    }

    /// <summary>
    /// spec → Everything 查询串。空格连接即 AND，<c>!</c> 为 NOT，<c>ext:a;b</c> 为 OR 列表。
    /// 普通词一律加引号：Everything 的 <c>(</c> <c>|</c> <c>$</c> 都是特殊字符，
    /// 不加引号就会出现"输入一个括号就查不到东西"。加引号只是让它按字面匹配，不影响模糊性。
    /// </summary>
    /// <summary>
    /// 只有一条正则、没有任何别谓词 —— 这时可以安全地打开 Everything 的全局正则开关。
    /// 一旦混进普通词或 ext:/size:/dm:，那个开关会把它们也当正则解释，只能退回本地快照。
    /// </summary>
    internal static bool IsRegexOnly(QbarSearchSpec spec)
    {
        if (spec == null || spec.RegexText.Length == 0) return false;
        return spec.Terms.Count == 0 && spec.Phrases.Count == 0 && spec.NegTerms.Count == 0
               && spec.Extensions.Count == 0 && spec.NegExtensions.Count == 0
               && spec.PathPins.Count == 0 && spec.NegPathPins.Count == 0
               && spec.MinSize < 0 && spec.MaxSize < 0 && spec.MinDay < 0 && spec.MaxDay < 0
               && !spec.FilesOnly && !spec.FoldersOnly;
    }

    internal static string BuildQuery(QbarSearchSpec spec)
    {
        /* 纯正则：整串就是正则，不能加引号（引号会变成正则的一部分）、
         * 也不能拼别的谓词（开了正则开关，ext:/size: 会被当成正则文本）。 */
        if (IsRegexOnly(spec)) return spec.RegexText;

        var parts = new List<string>();

        for (int i = 0; i < spec.Terms.Count; i++)
        {
            string term = spec.Terms[i];
            if (term.Length > 0) parts.Add(Quote(term));
        }

        for (int i = 0; i < spec.Phrases.Count; i++)
        {
            string phrase = spec.Phrases[i];
            if (phrase.Length > 0) parts.Add(Quote(phrase));
        }

        for (int i = 0; i < spec.NegTerms.Count; i++)
        {
            string neg = spec.NegTerms[i];
            if (neg.Length > 0) parts.Add("!" + Quote(neg));
        }

        if (spec.Extensions.Count > 0)
            parts.Add("ext:" + string.Join(";", spec.Extensions));

        if (spec.NegExtensions.Count > 0)
            parts.Add("!ext:" + string.Join(";", spec.NegExtensions));

        for (int i = 0; i < spec.PathPins.Count; i++)
        {
            string pin = spec.PathPins[i].Trim();
            if (pin.Length > 0) parts.Add("path:" + Quote(pin));
        }

        for (int i = 0; i < spec.NegPathPins.Count; i++)
        {
            string pin = spec.NegPathPins[i].Trim();
            if (pin.Length > 0) parts.Add("!path:" + Quote(pin));
        }

        /* 一旦带 ext:/size: 约束，目录就不可能有意义（目录没有扩展名、也没有 size 语义）——
         * 自建索引那边靠 dirsEligible 直接跳过目录，Everything 那边必须显式加 file:，
         * 否则窗口会被目录填满（实测 -ext:tmp 按名字排序时前 600 条全是目录，
         * 复核阶段全被剔掉，用户看到"零结果"）。这和 SpecFilter 的目录分支是同一条规则。 */
        bool dirsUseless = spec.Extensions.Count > 0 || spec.NegExtensions.Count > 0
                           || spec.MinSize >= 0 || spec.MaxSize >= 0;
        if (spec.FoldersOnly) parts.Add("folder:");
        else if (spec.FilesOnly || dirsUseless) parts.Add("file:");

        /* wfn: 与 regex: 必须交给 Everything 自己算，理由不是"懒"：
         * 本地复核虽然能筛，但**筛之前窗口里得先有它们**——查询里只剩这两个谓词时，
         * 不下发就等于发给 Everything 一条空查询（0 条候选），复核再好也筛不出东西。
         *   · wfn: 是 Everything 原生语法，整名通配，直接用；
         *   · regex: 是 Everything 1.4 的**逐词**前缀语法，只让这一个词按正则解释。
         *     绝不能改用 Everything_SetRegex(TRUE)——那是进程级全局开关，
         *     会把同一条查询里的普通词（比如 "报表"）一起当正则，直接查不到东西。 */
        if (spec.Glob.Length > 0) parts.Add("wfn:" + QuoteRaw(spec.Glob));
        /* regex: 不在这里下发，见 TryRun 开头的说明：1.4 的 regex: 实测恒 0 命中。 */

        string size = SizeToken(spec.MinSize, spec.MaxSize);
        if (size.Length > 0) parts.Add(size);

        string date = DateToken(spec.MinDay, spec.MaxDay);
        if (date.Length > 0) parts.Add(date);

        return string.Join(" ", parts);
    }

    /// <summary>
    /// 只为了保护空格而加引号，<b>不动反斜杠</b>。
    /// 给 wfn:/regex: 用：正则里的 <c>\.</c>、<c>\d</c> 和 glob 里的 <c>C:\dir\*</c> 都带着反斜杠，
    /// 一旦按普通词那样把反斜杠加倍（<c>Quote</c> 的行为），Everything 收到的是字面双反斜杠，
    /// 正则就变成"匹配一个反斜杠后面跟 .docx"，实测直接 0 命中。
    /// </summary>
    private static string QuoteRaw(string text)
    {
        if (text.Length == 0) return text;
        for (int i = 0; i < text.Length; i++)
            if (char.IsWhiteSpace(text[i]))
                return text.IndexOf('"') >= 0 ? text : "\"" + text + "\"";
        return text;
    }

    /// <summary>加引号并转义内部引号；Everything 用反斜杠转义引号。</summary>
    private static string Quote(string text)
    {
        if (text.Length == 0) return "\"\"";
        if (!NeedsQuote(text)) return text;
        return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static bool NeedsQuote(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) return true;
            if (c == '(' || c == ')' || c == '|' || c == '!' || c == '$' || c == '"' || c == '&' || c == '^') return true;
        }
        return false;
    }

    /// <summary>大小谓词。用 &gt;= / &lt;= 而不是 &gt; / &lt;：spec 里的边界是闭区间。</summary>
    internal static string SizeToken(long min, long max)
    {
        if (min <= 0 && max < 0) return "";
        if (min > 0 && max >= 0) return "size:" + FormatSize(min) + ".." + FormatSize(max);
        if (min > 0) return "size:>=" + FormatSize(min);
        return "size:<=" + FormatSize(max);
    }

    /// <summary>尽量用 kb/mb/gb 后缀：Everything 对后缀的解析是精确的，纯字节数反而不好读。</summary>
    private static string FormatSize(long bytes)
    {
        const long kb = 1024L;
        const long mb = kb * 1024L;
        const long gb = mb * 1024L;
        if (bytes >= gb && bytes % gb == 0) return (bytes / gb) + "gb";
        if (bytes >= mb && bytes % mb == 0) return (bytes / mb) + "mb";
        if (bytes >= kb && bytes % kb == 0) return (bytes / kb) + "kb";
        return bytes.ToString();
    }

    /// <summary>时间谓词：dm: 支持单日、区间、&gt;= / &lt;=，正好覆盖 MinDay/MaxDay 的闭区间语义。</summary>
    internal static string DateToken(int minDay, int maxDay)
    {
        if (minDay < 0 && maxDay < 0) return "";
        if (minDay >= 0 && maxDay >= 0)
            return "dm:" + DayText(minDay) + ".." + DayText(maxDay);
        if (minDay >= 0) return "dm:>=" + DayText(minDay);
        return "dm:<=" + DayText(maxDay);
    }

    private static string DayText(int day)
    {
        DateTime time = QbarSyntaxValues.FromDay(day);
        return time.ToString("yyyy-MM-dd");
    }
}