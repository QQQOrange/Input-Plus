using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using InputPlus.Core.Qbar.Search;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 谓词复核：一条候选（来自索引）是否真的满足 <see cref="QbarSearchSpec"/>，并给出分数。
///
/// 为什么索引层不负责这件事：索引只做「把候选窗口取回来」，它的 2-gram AND、
/// 去锚点正则、字面量预筛都是**超集**（为了不漏召回）。真正的语义（子串、闭区间、
/// 目录/文件分支、wfn 通配）必须由一个地方说了算，而且要和改造前逐字一致 ——
/// 否则「同一个查询换索引后端结果集不一样」这条红线立刻破。
///
/// 一份实现、两个用途：
///   · <c>QbarTantivyBackend</c> 用它逐条复核索引给的候选；
///   · 自检与命令行诊断用它直接判一条路径（<see cref="AcceptPath"/>）。
/// </summary>
internal sealed class QbarSpecFilter
{
    private readonly QbarSearchSpec _spec;
    private readonly string[] _tokens;
    private readonly Regex? _regex;
    private readonly HashSet<string>? _exts;
    private readonly HashSet<string>? _negExts;
    private readonly string[] _pathPins;
    private readonly string[] _negPathPins;

    internal QbarSpecFilter(QbarSearchSpec spec)
    {
        _spec = spec;
        _tokens = BuildTokens(spec);
        _regex = CompileRegex(spec.RegexText);
        _exts = NormalizeExts(spec.Extensions);
        _negExts = NormalizeExts(spec.NegExtensions);

        _pathPins = new string[spec.PathPins.Count];
        for (int i = 0; i < _pathPins.Length; i++) _pathPins[i] = spec.PathPins[i].Trim();
        _negPathPins = new string[spec.NegPathPins.Count];
        for (int i = 0; i < _negPathPins.Length; i++) _negPathPins[i] = spec.NegPathPins[i].Trim();
    }

    internal int TokenCount => _tokens.Length;

    private static string[] BuildTokens(QbarSearchSpec spec)
    {
        if (spec.Phrases.Count == 0) return spec.Terms.ToArray();
        var list = new List<string>(spec.Terms.Count + spec.Phrases.Count);
        list.AddRange(spec.Terms);
        list.AddRange(spec.Phrases);
        return list.ToArray();
    }

    private static HashSet<string>? NormalizeExts(ICollection<string> extensions)
    {
        if (extensions.Count == 0) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string ext in extensions)
        {
            string trimmed = ext.Trim();
            if (trimmed.StartsWith(".", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
            if (trimmed.Length > 0) set.Add(trimmed);
        }
        return set.Count == 0 ? null : set;
    }

    private static Regex? CompileRegex(string pattern)
    {
        if (pattern.Length == 0) return null;
        try
        {
            /* 用户写的正则可能灾难性回溯，250ms 上限是兜底：宁可这条查询不返回，
             * 也不能把搜索线程钉死在 CatastrophicBacktracking 里。 */
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 这条候选能不能要；能要就给出分数。
    /// <paramref name="sizeBytes"/> 是**精确字节数**（索引层原样给出的那个）；传 -1 表示
    /// 调用方只有 KB 口径，此时退回"KB × 1024"（与改造前的行为一致）。
    /// </summary>
    internal bool Accept(string fullPath, bool isDir, int sizeKb, int day, out int score, long sizeBytes = -1)
    {
        score = 0;
        if (string.IsNullOrEmpty(fullPath)) return false;
        if (!SizeDayAccept(sizeKb, day, sizeBytes)) return false;

        QbarNameScore.Split(fullPath, isDir, out string dir, out string leaf);
        if (leaf.Length == 0 || leaf.Length > QbarNameScore.MaxNameChars) return false;

        if (!isDir)
        {
            string ext = QbarNameScore.LastExtension(leaf.AsSpan()).ToString();
            if (_exts != null && (_exts.Count == 0 || !_exts.Contains(ext))) return false;
            if (_negExts != null && ext.Length > 0 && _negExts.Contains(ext)) return false;

            if (!PathPinMatch(dir, leaf)) return false;

            for (int i = 0; i < _spec.NegTerms.Count; i++)
            {
                string neg = _spec.NegTerms[i];
                if (neg.Length == 0) continue;
                if (QbarNameScore.Contains(leaf, neg)) return false;
                if (dir.IndexOf(neg, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
        }
        else
        {
            /* 目录没有扩展名也没有大小：查询一旦带了 ext:/!ext:/size: 或显式 file:，
             * 目录就不该出现。这条规则必须和索引层的 isdir 下推一致，
             * 否则同一个查询会多出一批目录。 */
            if (_spec.FilesOnly) return false;
            if (_spec.Extensions.Count > 0 || _spec.NegExtensions.Count > 0) return false;
            if (_spec.MinSize >= 0 || _spec.MaxSize >= 0) return false;

            if (!PathPinMatch(dir, "")) return false;

            for (int i = 0; i < _spec.NegTerms.Count; i++)
            {
                string neg = _spec.NegTerms[i];
                if (neg.Length == 0) continue;
                if (dir.IndexOf(neg, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
        }

        if (_spec.Glob.Length > 0 && !GlobMatch(leaf, _spec.Glob)) return false;
        if (_regex != null && !SafeRegexMatch(_regex, leaf, dir)) return false;

        if (isDir)
        {
            if (_tokens.Length == 0)
            {
                score = QbarNameScore.Baseline(leaf, dir);
                return true;
            }

            for (int t = 0; t < _tokens.Length; t++)
            {
                if (dir.IndexOf(_tokens[t], StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return false;
            }

            if (QbarNameScore.EqualsIgnoreCase(leaf, _spec.PlainQuery)) score = 900;
            else if (ContainsAny(leaf, _tokens)) score = 420;
            else return false;

            score -= leaf.Length / 8;
            score -= QbarNameScore.DepthOf(dir) * 8;
            return true;
        }

        if (_tokens.Length > 0)
        {
            int matched = QbarNameScore.Score(leaf, dir, _spec.PlainQuery, _tokens);
            if (matched == QbarNameScore.NoScore) return false;
            score = matched;
            return true;
        }

        score = QbarNameScore.Baseline(leaf, dir);
        return true;
    }

    private bool PathPinMatch(string dir, string leaf)
    {
        for (int i = 0; i < _pathPins.Length; i++)
        {
            string pin = _pathPins[i];
            if (pin.Length == 0) continue;
            if (dir.IndexOf(pin, StringComparison.OrdinalIgnoreCase) < 0
                && !QbarNameScore.Contains(leaf, pin))
                return false;
        }

        for (int i = 0; i < _negPathPins.Length; i++)
        {
            string pin = _negPathPins[i];
            if (pin.Length == 0) continue;
            if (dir.IndexOf(pin, StringComparison.OrdinalIgnoreCase) >= 0
                || QbarNameScore.Contains(leaf, pin))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 大小/时间约束。
    ///
    /// **大小一律按字节比较**（闭区间）：索引层返回的 `s` 就是精确字节数，早期版本把它
    /// `/1024` 塞进 KB 字段、这里再 `*1024` 还原 —— 1~1023 字节的文件被抹成 0，
    /// 于是 `size:>0` 会漏掉所有小文件。KB 字段只留给界面显示（<see cref="FileSearchHit.SizeKb"/>）。
    /// </summary>
    private bool SizeDayAccept(int sizeKb, int day, long sizeBytes)
    {
        if (_spec.MinSize < 0 && _spec.MaxSize < 0 && _spec.MinDay < 0 && _spec.MaxDay < 0) return true;

        if (_spec.MinSize >= 0 || _spec.MaxSize >= 0)
        {
            long size = sizeBytes >= 0 ? sizeBytes : (long)sizeKb * 1024L;
            if (_spec.MinSize >= 0 && size < _spec.MinSize) return false;
            if (_spec.MaxSize >= 0 && size > _spec.MaxSize) return false;
        }

        if (_spec.MinDay >= 0 || _spec.MaxDay >= 0)
        {
            if (day < 0 || day == QbarSyntaxValues.UnknownDay) return false;
            if (_spec.MinDay >= 0 && day < _spec.MinDay) return false;
            if (_spec.MaxDay >= 0 && day > _spec.MaxDay) return false;
        }

        return true;
    }

    private static bool ContainsAny(string text, string[] needles)
    {
        for (int i = 0; i < needles.Length; i++)
        {
            if (QbarNameScore.Contains(text, needles[i])) return true;
        }
        return false;
    }

    /// <summary>文件名 glob（* 和 ?），大小写不敏感。逐字符回溯匹配，不构造正则。</summary>
    internal static bool GlobMatch(string text, string pattern)
    {
        int t = 0;
        int p = 0;
        int star = -1;
        int mark = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || QbarNameScore.EqualsIgnoreCase(
                    text[t].ToString(), pattern[p].ToString())))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool SafeRegexMatch(Regex regex, string name, string dir)
    {
        try
        {
            if (regex.IsMatch(name)) return true;
            return dir.Length > 0 && regex.IsMatch(dir);
        }
        catch (RegexMatchTimeoutException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// 单条路径的便捷判定（自检与命令行诊断用；批量复核走 <see cref="Accept"/>）。
    /// <paramref name="sizeBytes"/> 语义同 <see cref="Accept"/>：-1 = 只有 KB 口径。
    /// </summary>
    internal static bool AcceptPath(QbarSearchSpec spec, string fullPath, bool isDir, int sizeKb, int day,
        out int score, long sizeBytes = -1)
    {
        score = 0;
        if (spec == null || string.IsNullOrEmpty(fullPath)) return false;
        return new QbarSpecFilter(spec).Accept(fullPath, isDir, sizeKb, day, out score, sizeBytes);
    }
}
