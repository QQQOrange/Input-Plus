using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InputPlus.Core.Qbar.Search;

/// <summary>分词结果的种类。只影响翻译分支，不影响匹配。</summary>
internal enum QbarTokenKind
{
    /// <summary>普通词：交给索引做子串匹配。</summary>
    Term,
    /// <summary>带引号的短语：必须连续出现。</summary>
    Phrase,
    /// <summary>谓词：ext: / size: / date: / wfn: 这类。</summary>
    Predicate,
}

/// <summary>一个原始 token（已归一化、已识别否定）。</summary>
internal readonly record struct QbarToken(string Text, QbarTokenKind Kind, bool Negated, bool Quoted);

/// <summary>
/// 翻译出来的"结构化查询"。索引层只认这个结构，不认识用户语法 ——
/// 语法是交互层的事，索引是数据的事，中间这一层塌了的话，
/// 以后想换索引就得把语法解析再写一遍。
/// </summary>
internal sealed class QbarSearchSpec
{
    internal List<string> Terms { get; } = new List<string>();
    internal List<string> Phrases { get; } = new List<string>();
    internal List<string> NegTerms { get; } = new List<string>();
    internal HashSet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> NegExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal List<string> PathPins { get; } = new List<string>();
    internal List<string> NegPathPins { get; } = new List<string>();

    internal long MinSize { get; set; } = -1;
    internal long MaxSize { get; set; } = -1;
    internal int MinDay { get; set; } = -1;
    internal int MaxDay { get; set; } = -1;

    internal bool FilesOnly { get; set; }
    internal bool FoldersOnly { get; set; }

    /// <summary>wfn:*.pdf 的通配串（单条，后写的覆盖先写的）。</summary>
    internal string Glob { get; set; } = "";

    /// <summary>regex: 的正则串。必须显式开启，且只作用于这一条。</summary>
    internal string RegexText { get; set; } = "";

    /// <summary>content: 请求：当前索引不含正文，翻译层要明说"做不到"，而不是假装搜了。</summary>
    internal bool ContentRequested { get; set; }

    /// <summary>命中的过滤器 Id / 显示名，用来画 chip 和写调试面板。</summary>
    internal List<string> AppliedFilterIds { get; } = new List<string>();
    internal List<string> AppliedFilterNames { get; } = new List<string>();

    /// <summary>首词命中的命令关键字（命令行与普通结果并列展示，所以这里只是"有命令"）。</summary>
    internal string CommandKeyword { get; set; } = "";

    /// <summary>给索引层做纯关键词搜索的串（多个词用空格连接，语义与改造前完全一致）。</summary>
    internal string PlainQuery => Terms.Count == 0 ? "" : string.Join(" ", Terms);

    /// <summary>
    /// 没有任何结构化约束 —— 这时必须走和改造前**一模一样**的搜索路径，
    /// 否则红线「默认查询行为保持一致」就守不住了（顺序、边界分档都会漂）。
    /// </summary>
    internal bool IsPlain =>
        Extensions.Count == 0 && NegExtensions.Count == 0 && NegTerms.Count == 0 &&
        Phrases.Count == 0 && PathPins.Count == 0 && NegPathPins.Count == 0 &&
        MinSize < 0 && MaxSize < 0 && MinDay < 0 && MaxDay < 0 &&
        !FilesOnly && !FoldersOnly && Glob.Length == 0 && RegexText.Length == 0;

    internal bool HasKeywords => Terms.Count > 0 || Phrases.Count > 0;

    internal string Describe()
    {
        var sb = new StringBuilder();
        if (Terms.Count > 0) sb.Append("terms=").Append(string.Join("|", Terms)).Append(' ');
        if (Phrases.Count > 0) sb.Append("phrases=").Append(string.Join("|", Phrases)).Append(' ');
        if (NegTerms.Count > 0) sb.Append("not=").Append(string.Join("|", NegTerms)).Append(' ');
        if (Extensions.Count > 0) sb.Append("ext=").Append(Extensions.Count).Append(" ext ");
        if (NegExtensions.Count > 0) sb.Append("!ext=").Append(NegExtensions.Count).Append(" ext ");
        if (PathPins.Count > 0) sb.Append("path=").Append(string.Join("|", PathPins)).Append(' ');
        if (MinSize >= 0 || MaxSize >= 0) sb.Append("size=").Append(MinSize).Append("..").Append(MaxSize).Append(' ');
        if (MinDay >= 0 || MaxDay >= 0) sb.Append("date=").Append(QbarSyntaxValues.DescribeDateRange(MinDay, MaxDay)).Append(' ');
        if (FilesOnly) sb.Append("files-only ");
        if (FoldersOnly) sb.Append("dirs-only ");
        if (Glob.Length > 0) sb.Append("glob=").Append(Glob).Append(' ');
        if (RegexText.Length > 0) sb.Append("regex=").Append(RegexText).Append(' ');
        if (AppliedFilterNames.Count > 0) sb.Append("filter=").Append(string.Join("|", AppliedFilterNames)).Append(' ');
        if (CommandKeyword.Length > 0) sb.Append("command=").Append(CommandKeyword).Append(' ');
        return sb.ToString().TrimEnd();
    }
}

/// <summary>调试面板的一行。Stage 是流水线层名，Detail 是那一层的产出。</summary>
internal readonly record struct QbarDiagEntry(string Stage, string Detail, bool Warn);

/// <summary>
/// 一次翻译的全部中间结果。调试面板直接把它摊开显示 ——
/// 有了这个，翻译层出问题时不用再"输入 A 得到空结果，猜是哪一层吃掉了"。
/// </summary>
internal sealed class QbarTranslateResult
{
    internal string Input { get; set; } = "";
    internal string Normalized { get; set; } = "";
    internal string Tokens { get; set; } = "";
    internal string KeywordDetail { get; set; } = "";
    internal string IndexText { get; set; } = "";
    internal string RequestDetail { get; set; } = "";
    internal QbarSearchSpec Spec { get; } = new QbarSearchSpec();
    internal List<QbarDiagEntry> Stages { get; } = new List<QbarDiagEntry>();

    internal void Stage(string stage, string detail, bool warn = false)
    {
        Stages.Add(new QbarDiagEntry(stage, detail, warn));
    }
}

/// <summary>
/// 查询翻译层：用户输入 → 结构化查询 + 可打印的中间结果。
///
/// 流水线严格分层（normalize → tokenize → resolveKeyword → translate → assemble），
/// 每层都能单独解释自己做了什么。这样做不是为了"架构好看"，而是因为这一层
/// 出了问题时用户只会说"搜不到"，没有逐层输出根本无从定位。
/// </summary>
internal static class QbarQueryTranslator
{
    /// <summary>命令首词之后的所有词都作为参数原样传下去，不参与文件匹配。</summary>
    internal static QbarTranslateResult Translate(string input, QbarFilterCatalog catalog, DateTime now)
    {
        var result = new QbarTranslateResult { Input = input ?? "" };
        string normalized = Normalize(input);
        result.Normalized = normalized;
        result.Stage("normalize", normalized.Length == 0 ? "(empty)" : normalized);

        List<QbarToken> tokens = Tokenize(normalized);
        var tokenText = new StringBuilder();
        foreach (QbarToken token in tokens)
        {
            if (tokenText.Length > 0) tokenText.Append(" | ");
            if (token.Negated) tokenText.Append('!');
            tokenText.Append(token.Kind == QbarTokenKind.Phrase ? "\"" + token.Text + "\"" : token.Text);
        }
        result.Tokens = tokenText.ToString();
        result.Stage("tokenize", result.Tokens.Length == 0 ? "(no token)" : result.Tokens);

        var keywordNotes = new List<string>();
        var display = new List<string>();
        QbarSearchSpec spec = result.Spec;

        foreach (QbarToken token in tokens)
        {
            string text = token.Text;
            if (text.Length == 0) continue;

            if (token.Kind == QbarTokenKind.Phrase)
            {
                if (token.Negated)
                {
                    spec.NegTerms.Add(text);
                    display.Add("!" + QuoteForDisplay(text));
                }
                else
                {
                    spec.Phrases.Add(text);
                    display.Add(QuoteForDisplay(text));
                }
                continue;
            }

            if (TrySplitPredicate(text, out string key, out string value))
            {
                if (!ApplyPredicate(spec, catalog, key, value, token.Negated, keywordNotes, display, result, now))
                {
                    /* 解析不了的 token 不整条丢弃：降级成普通关键词并在调试面板标黄。
                     * 用户打错一个冒号就"什么都搜不到"是最难解释的一类 bug。 */
                    AddTerm(spec, text, token.Negated, display, quote: true);
                    result.Stage("degrade", "unparsed predicate " + text + " -> plain keyword", warn: true);
                }
                continue;
            }

            AddTerm(spec, text, token.Negated, display, quote: NeedsQuoting(text));
        }

        result.KeywordDetail = keywordNotes.Count == 0 ? "(none)" : string.Join("; ", keywordNotes);
        result.Stage("resolveKeyword", result.KeywordDetail);

        if (spec.ContentRequested)
        {
            result.Stage("content", "index has no content, content: ignored", warn: true);
        }

        string query = string.Join(" ", display);
        result.IndexText = query;
        result.Stage("translate", query.Length == 0 ? "(empty query)" : query);
        result.Stage("native", spec.Describe().Length == 0 ? "(no structure)" : spec.Describe());
        return result;
    }

    private static void AddTerm(QbarSearchSpec spec, string text, bool negated, List<string> display, bool quote)
    {
        if (negated)
        {
            spec.NegTerms.Add(text);
            display.Add("!" + (quote ? QuoteForDisplay(text) : text));
        }
        else
        {
            spec.Terms.Add(text);
            display.Add(quote ? QuoteForDisplay(text) : text);
        }
    }

    /// <summary>
    /// 谓词分支。返回 false 表示"这条谓词不认识/值不合法"，由调用方降级成普通关键词。
    /// </summary>
    private static bool ApplyPredicate(
        QbarSearchSpec spec, QbarFilterCatalog catalog, string key, string value, bool negated,
        List<string> notes, List<string> display, QbarTranslateResult result, DateTime now)
    {
        string lower = key.ToLowerInvariant();

        switch (lower)
        {
            case "ext":
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                QbarFilterCatalog.FillExtensions(value, set);
                if (set.Count == 0) return false;
                if (negated)
                {
                    foreach (string ext in set) spec.NegExtensions.Add(ext);
                    display.Add("!ext:" + QbarFilterCatalog.JoinExtensions(set));
                    notes.Add("!ext:" + set.Count);
                }
                else
                {
                    foreach (string ext in set) spec.Extensions.Add(ext);
                    display.Add("ext:" + QbarFilterCatalog.JoinExtensions(set));
                    notes.Add("ext:" + set.Count);
                }
                return true;
            }

            case "size":
            {
                if (!TryParseSizeRange(value, out long min, out long max, out string canonical)) return false;
                if (negated) return false;
                if (min >= 0) spec.MinSize = Math.Max(spec.MinSize, min);
                if (max >= 0) spec.MaxSize = spec.MaxSize < 0 ? max : Math.Min(spec.MaxSize, max);
                display.Add("size:" + canonical);
                notes.Add("size:" + canonical);
                return true;
            }

            case "date":
            case "dm":
            {
                if (!QbarSyntaxValues.TryParseDateRange(value, now, out int min, out int max)) return false;
                if (negated) return false;
                if (min >= 0) spec.MinDay = Math.Max(spec.MinDay, min);
                if (max >= 0) spec.MaxDay = spec.MaxDay < 0 ? max : Math.Min(spec.MaxDay, max);
                display.Add("dm:" + ToDisplayDate(value));
                notes.Add("date:" + value + " -> day " + spec.MinDay + ".." + spec.MaxDay);
                return true;
            }

            case "wfn":
            case "glob":
            {
                if (string.IsNullOrWhiteSpace(value) || negated) return false;
                spec.Glob = value.Trim();
                display.Add("wfn:" + spec.Glob);
                notes.Add("wfn:" + spec.Glob);
                return true;
            }

            case "regex":
            {
                if (string.IsNullOrWhiteSpace(value) || negated) return false;
                spec.RegexText = value;
                display.Add("regex:" + spec.RegexText);
                notes.Add("regex:" + spec.RegexText);
                return true;
            }

            case "content":
            {
                if (negated) return false;
                spec.ContentRequested = true;
                /* 内容检索目前做不到：索引里没有正文（真建了也慢到不可用），
                 * 本地快照只有文件名。与其"接受这个词但什么都不筛"（用户会以为全盘都匹配），
                 * 不如按文件名检索并在调试面板标黄说明。 */
                AddTerm(spec, value, false, display, quote: NeedsQuoting(value));
                notes.Add("content: 无内容索引，已按文件名检索");
                return true;
            }

            case "path":
            case "in":
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                if (negated)
                {
                    spec.NegPathPins.Add(value);
                    display.Add("!path:" + QuoteForDisplay(value));
                }
                else
                {
                    spec.PathPins.Add(value);
                    display.Add("path:" + QuoteForDisplay(value));
                }
                notes.Add("path:" + value);
                return true;
            }
        }

        /* 过滤器关键字：doc: / pic: / 自定义。空值表示"单独激活过滤器"（chip）。 */
        QbarFilter? filter = catalog.TryFind(lower);
        if (filter == null) return false;

        /* 被用户停用的过滤器：整条 token 当没写，既不展开也不降级成普通词。
         * 降级成普通词会让 "doc: 报表" 去搜带冒号的字面量，命中 0 —— 用户只会觉得坏了。 */
        if (!filter.Enabled)
        {
            notes.Add((filter.Keyword.Length == 0 ? filter.Name : filter.Keyword) + ": 已停用 -> 忽略");
            return true;
        }
        if (spec.AppliedFilterIds.Contains(filter.Id)) return true;

        spec.AppliedFilterIds.Add(filter.Id);
        string label = string.IsNullOrWhiteSpace(filter.Name) ? filter.Keyword : filter.Name;
        spec.AppliedFilterNames.Add(label);

        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        QbarFilterCatalog.FillExtensions(filter.Extensions, exts);
        foreach (string ext in exts) spec.Extensions.Add(ext);
        if (exts.Count > 0 && !negated)
        {
            display.Add("ext:" + QbarFilterCatalog.JoinExtensions(exts));
        }

        if (filter.SearchForFolders && !filter.SearchForFiles) spec.FoldersOnly = true;
        else if (!filter.SearchForFolders && filter.SearchForFiles) spec.FilesOnly = true;

        if (!string.IsNullOrWhiteSpace(filter.SearchInFolders))
        {
            foreach (string folder in filter.SearchInFolders.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = folder.Trim();
                if (trimmed.Length == 0) continue;
                spec.PathPins.Add(trimmed);
                display.Add("path:" + QuoteForDisplay(trimmed));
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.DateModifiedRange) &&
            QbarSyntaxValues.TryParseDateRange(filter.DateModifiedRange, now, out int fmin, out int fmax))
        {
            if (fmin >= 0) spec.MinDay = Math.Max(spec.MinDay, fmin);
            if (fmax >= 0) spec.MaxDay = spec.MaxDay < 0 ? fmax : Math.Min(spec.MaxDay, fmax);
            display.Add("dm:" + ToDisplayDate(filter.DateModifiedRange));
        }

        notes.Add("filter " + filter.Keyword + " -> " + exts.Count + " exts");

        /* 过滤器自带的附加查询词再走一遍翻译（只一层，不递归），
         * 这样自定义过滤器可以写成 "psd;ai size:>10mb" 这种组合。 */
        if (!string.IsNullOrWhiteSpace(filter.ExtraQuery))
        {
            QbarTranslateResult inner = Translate(filter.ExtraQuery, catalog, now);
            foreach (string term in inner.Spec.Terms) spec.Terms.Add(term);
            foreach (string term in inner.Spec.Phrases) spec.Phrases.Add(term);
            foreach (string ext in inner.Spec.Extensions) spec.Extensions.Add(ext);
            if (inner.Spec.MinSize >= 0) spec.MinSize = Math.Max(spec.MinSize, inner.Spec.MinSize);
            if (inner.Spec.MaxSize >= 0) spec.MaxSize = spec.MaxSize < 0 ? inner.Spec.MaxSize : Math.Min(spec.MaxSize, inner.Spec.MaxSize);
            if (inner.IndexText.Length > 0) display.Add("(" + inner.IndexText + ")");
        }

        /* "doc:报表" 这种把关键字和关键词粘在一起的写法：冒号后面的值当普通词。 */
        if (!string.IsNullOrWhiteSpace(value))
        {
            AddTerm(spec, value.Trim(), false, display, NeedsQuoting(value));
        }

        return true;
    }

    private static bool TryParseSizeRange(string value, out long min, out long max, out string canonical)
    {
        min = -1;
        max = -1;
        canonical = "";

        string text = value.Trim().Replace(" ", "");
        int split = text.IndexOf("..", StringComparison.Ordinal);
        if (split >= 0)
        {
            string left = text.Substring(0, split);
            string right = text.Substring(split + 2);
            if (!QbarSyntaxValues.TryParseSize(left, out long lo)) return false;
            if (!QbarSyntaxValues.TryParseSize(right, out long hi)) return false;
            min = Math.Min(lo, hi);
            max = Math.Max(lo, hi);
            canonical = left + ".." + right;
            return true;
        }

        if (text.StartsWith(">=", StringComparison.Ordinal)) { if (!QbarSyntaxValues.TryParseSize(text.Substring(2), out min)) return false; canonical = ">=" + text.Substring(2); return true; }
        if (text.StartsWith(">", StringComparison.Ordinal)) { if (!QbarSyntaxValues.TryParseSize(text.Substring(1), out min)) return false; canonical = ">" + text.Substring(1); return true; }
        if (text.StartsWith("<=", StringComparison.Ordinal)) { if (!QbarSyntaxValues.TryParseSize(text.Substring(2), out max)) return false; canonical = "<=" + text.Substring(2); return true; }
        if (text.StartsWith("<", StringComparison.Ordinal)) { if (!QbarSyntaxValues.TryParseSize(text.Substring(1), out max)) return false; canonical = "<" + text.Substring(1); return true; }

        if (!QbarSyntaxValues.TryParseSize(text, out min)) return false;
        canonical = text;
        return true;
    }

    /// <summary>Listary 的日期写法 → 展示用的 dm: 写法。老写法原样保留，翻译串要能直接对照。</summary>
    internal static string ToDisplayDate(string value)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "week": return "thisweek";
            case "month": return "thismonth";
            case "year": return "thisyear";
            default: return value.Trim();
        }
    }

    /// <summary>
    /// 全角 → 半角、去首尾空白、折叠空格。
    /// 中文输入法下 "：" 和 ":" 是两个字符，用户根本分不清自己打的是哪个；
    /// 不做这一步的话 doc：（全角冒号）永远匹配不到过滤器。
    /// </summary>
    internal static string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new StringBuilder(input.Length);
        foreach (char ch in input)
        {
            char mapped = ch switch
            {
                '：' => ':',
                '；' => ';',
                '／' => '/',
                '＼' => '\\',
                '｜' => '|',
                '！' => '!',
                '－' => '-',
                '（' => '(',
                '）' => ')',
                '＊' => '*',
                '？' => '?',
                '．' => '.',
                '　' => ' ',
                '“' => '"',
                '”' => '"',
                '‘' => '\'',
                '’' => '\'',
                '＜' => '<',
                '＞' => '>',
                '＝' => '=',
                '～' => '~',
                '＠' => '@',
                '＃' => '#',
                '％' => '%',
                '＆' => '&',
                '＋' => '+',
                '，' => ',',
                '、' => ',',
                _ => ch,
            };
            sb.Append(mapped);
        }

        string text = sb.ToString().Trim();
        var collapsed = new StringBuilder(text.Length);
        bool lastSpace = false;
        foreach (char ch in text)
        {
            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
            {
                if (lastSpace) continue;
                lastSpace = true;
                collapsed.Append(' ');
            }
            else
            {
                lastSpace = false;
                collapsed.Append(ch);
            }
        }

        return collapsed.ToString();
    }

    /// <summary>
    /// 分词。双引号内不拆词（所以 "年度 总结" 是一个 token），
    /// 反斜杠转义 \" 支持短语里带引号。
    /// </summary>
    internal static List<QbarToken> Tokenize(string normalized)
    {
        var tokens = new List<QbarToken>();
        if (string.IsNullOrEmpty(normalized)) return tokens;

        var sb = new StringBuilder();
        bool inQuote = false;
        bool quoted = false;

        for (int i = 0; i < normalized.Length; i++)
        {
            char ch = normalized[i];
            if (ch == '\\' && inQuote && i + 1 < normalized.Length && normalized[i + 1] == '"')
            {
                sb.Append('"');
                i++;
                continue;
            }

            if (ch == '"')
            {
                if (inQuote)
                {
                    tokens.Add(MakeToken(sb.ToString(), quoted: true));
                    sb.Clear();
                    inQuote = false;
                    quoted = false;
                }
                else
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(MakeToken(sb.ToString(), quoted: false));
                        sb.Clear();
                    }
                    inQuote = true;
                    quoted = true;
                }
                continue;
            }

            if (ch == ' ' && !inQuote)
            {
                if (sb.Length > 0)
                {
                    tokens.Add(MakeToken(sb.ToString(), quoted: false));
                    sb.Clear();
                }
                continue;
            }

            sb.Append(ch);
        }

        if (sb.Length > 0)
        {
            /* 引号没闭合：把剩下的当短语处理，比丢掉更符合直觉（用户正在输入中途也会走到这里）。 */
            tokens.Add(MakeToken(sb.ToString(), quoted || inQuote));
        }

        return tokens;
    }

    private static QbarToken MakeToken(string raw, bool quoted)
    {
        bool negated = false;
        string text = raw;
        if (!quoted && text.Length > 1 && (text[0] == '-' || text[0] == '!'))
        {
            negated = true;
            text = text.Substring(1);
        }

        QbarTokenKind kind = quoted ? QbarTokenKind.Phrase
            : (text.IndexOf(':') > 0 ? QbarTokenKind.Predicate : QbarTokenKind.Term);
        return new QbarToken(text, kind, negated, quoted);
    }

    private static bool TrySplitPredicate(string text, out string key, out string value)
    {
        key = "";
        value = "";
        int colon = text.IndexOf(':');
        if (colon <= 0) return false;
        key = text.Substring(0, colon).Trim();
        value = text.Substring(colon + 1).Trim();
        if (key.Length == 0) return false;
        foreach (char ch in key)
        {
            /* 关键字只允许字母数字下划线和短横线，否则多半是 C:\path 这类路径。 */
            if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '-') return false;
        }
        return true;
    }

    /// <summary>
    /// 展示串的特殊字符。这些字符在"普通关键词"里必须加引号，
    /// 否则用户输入一个左括号就会被当成语法片段，查不到任何东西。
    /// </summary>
    internal static bool NeedsQuoting(string text)
    {
        if (text.Length == 0) return false;
        foreach (char ch in text)
        {
            switch (ch)
            {
                case ' ':
                case '"':
                case '!':
                case '|':
                case ':':
                case '(':
                case ')':
                case '*':
                case '?':
                case '$':
                case '\\':
                    return true;
            }
        }
        return false;
    }

    internal static string QuoteForDisplay(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (char ch in text)
        {
            if (ch == '"') sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>调试面板里把输入解析成"人话"的一行摘要，供 --qbar-query 打印。</summary>
    internal static string Summarize(QbarTranslateResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("input      : " + result.Input);
        sb.AppendLine("normalized : " + result.Normalized);
        sb.AppendLine("tokens     : " + result.Tokens);
        sb.AppendLine("keywords   : " + result.KeywordDetail);
        sb.AppendLine("query      : " + result.IndexText);
        sb.AppendLine("native     : " + result.Spec.Describe());
        return sb.ToString();
    }
}
