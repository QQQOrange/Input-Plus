using System;
using System.IO;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 一条文件/目录搜索结果。<see cref="Score"/> 越大越靠前，调用方只需按返回顺序展示。
///
/// <see cref="SizeKb"/> / <see cref="Day"/> 是语义层用的（排序、分面、诊断要显示
/// "多大、什么时候改的"），默认值 0 / -1 表示"这份结果没这个字段"。
///
/// <see cref="Highlights"/> 恒为 null：索引层不再返回命中区间，高亮一律由
/// <c>QbarSearchEngine.BuildHighlights</c> 按关键词算（全仓只有那一份实现）。
/// 保留这个字段是为了不动调用点，也因为离线工装/自检里还有构造它的地方。
/// </summary>
internal readonly record struct FileSearchHit(string Path, bool IsDir, int Score, int SizeKb = 0, int Day = -1,
                                               int[]? Highlights = null);

/// <summary>
/// 文件名打分。**全仓唯一一份**：索引后端换成了 tantivy，但"这个名字有多匹配"
/// 这件事与索引无关，仍然是这一套档位。
///
/// 档位（与改造前逐字一致，改动必须在 DECISIONS 里说明为什么）：
///   全等 1000 / 前缀 850 / 全部词都在名字里 780 / 首词在名字里 640 / 只有后面的词在名字里 600，
///   再按名字长度和目录深度扣分，可执行/快捷方式 +12。
/// </summary>
internal static class QbarNameScore
{
    /// <summary>没有任何词命中时的哨兵值。分数本身不会低于 0。</summary>
    internal const int NoScore = int.MinValue;

    /// <summary>名字最长这么多个字符。Windows 自身限制更短，这里只是解码缓冲的上限。</summary>
    internal const int MaxNameChars = 512;

    private static readonly char[] Separators = { '\\', '/' };

    /// <summary>取路径的最后一段（文件名 / 目录名）。</summary>
    internal static ReadOnlySpan<char> Leaf(ReadOnlySpan<char> path)
    {
        int slash = path.LastIndexOfAny(Separators);
        return slash >= 0 ? path.Slice(slash + 1) : path;
    }

    /// <summary>拆出「目录 + 文件名」。目录是空串表示没有父目录。</summary>
    internal static void Split(string fullPath, bool isDir, out string dir, out string leaf)
    {
        int slash = fullPath.LastIndexOfAny(Separators);
        if (isDir)
        {
            // 目录的"名字"是它自己那一段，dir 是完整路径（与改造前的 SpecFilter 一致）
            dir = fullPath;
            leaf = Leaf(fullPath.AsSpan()).ToString();
            return;
        }
        dir = slash > 0 ? fullPath.Substring(0, slash) : string.Empty;
        leaf = slash >= 0 ? fullPath.Substring(slash + 1) : fullPath;
    }

    /// <summary>路径深度 = 路径里分隔符的个数。</summary>
    internal static int DepthOf(string path)
    {
        int depth = 0;
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c == '\\' || c == '/') depth++;
        }
        return depth;
    }

    /// <summary>文件的最后一个扩展名（不含点）。没有扩展名返回空 span。</summary>
    internal static ReadOnlySpan<char> LastExtension(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return ReadOnlySpan<char>.Empty;
        return name.Slice(dot + 1);
    }

    /// <summary>单个检索词 → 分数。返回 <see cref="NoScore"/> 表示这个词哪儿都没命中。</summary>
    internal static int Score(string name, string dir, string query, string[] tokens)
    {
        bool allInName = true;
        bool firstInName = false;

        for (int i = 0; i < tokens.Length; i++)
        {
            bool inName = Contains(name, tokens[i]);
            if (i == 0) firstInName = inName;
            if (inName) continue;

            allInName = false;
            if (dir.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) < 0) return NoScore;
        }

        int score;
        if (EqualsIgnoreCase(name, query)) score = 1000;
        else if (tokens.Length > 0 && StartsWith(name, tokens[0])) score = 850;
        else if (allInName) score = 780;
        else if (firstInName) score = 640;       // 700 - 60：其余词只在目录里
        else score = 600;                         // 名字里只有后面的词，第一个词在目录里

        score -= name.Length / 8;
        score -= DepthOf(dir) * 8;
        if (EndsWith(name, ".exe") || EndsWith(name, ".lnk") || EndsWith(name, ".url"))
            score += 12;
        return score;
    }

    /// <summary>没有任何关键词时的基线分：越短越浅越靠前。</summary>
    internal static int Baseline(string name, string dir) => 500 - name.Length / 8 - DepthOf(dir) * 8;

    /// <summary>
    /// 子序列匹配打分：连续命中给得高，跳得越多扣得越多，词首命中额外奖励。
    /// 返回 0 表示不匹配（与改造前的 <c>SubsequenceScore</c> 同一套权重）。
    ///
    /// 谁在用它：索引的模糊入口负责把「每个字符都出现」的候选捞出来（这是子序列的必要
    /// 条件，所以不漏），真正的子序列判定和这个分数只在这里算一次。
    /// </summary>
    internal static int Subsequence(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;

        int ti = 0;
        int hits = 0;
        int gaps = 0;
        int boundary = 0;
        int last = -1;

        for (int ni = 0; ni < needle.Length; ni++)
        {
            char want = needle[ni];
            bool found = false;
            while (ti < text.Length)
            {
                char c = text[ti];
                if (char.ToLowerInvariant(c) == char.ToLowerInvariant(want))
                {
                    found = true;
                    hits++;
                    if (last >= 0 && ti == last + 1) boundary++;
                    if (ti == 0 || !char.IsLetterOrDigit(text[ti - 1])) boundary++;
                    last = ti;
                    ti++;
                    break;
                }
                ti++;
                gaps++;
            }
            if (!found) return 0;
        }

        int score = 300 + hits * 8 + boundary * 10 - gaps * 2;
        return score < 1 ? 1 : score;
    }

    // ── 大小写不敏感的 ASCII 比较（与改造前的语义一致：只对 ASCII 折叠） ──

    internal static bool Contains(string haystack, string needle)
    {
        if (needle.Length == 0) return false;
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static bool StartsWith(string text, string prefix)
        => prefix.Length > 0 && text.Length >= prefix.Length
           && string.Compare(text, 0, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;

    internal static bool EqualsIgnoreCase(string text, string other)
        => string.Equals(text, other, StringComparison.OrdinalIgnoreCase);

    internal static bool EndsWith(string text, string suffix)
    {
        if (suffix.Length == 0 || text.Length < suffix.Length) return false;
        return string.Compare(text, text.Length - suffix.Length, suffix, 0, suffix.Length,
                   StringComparison.OrdinalIgnoreCase) == 0;
    }

    /// <summary>
    /// 有没有「更大的候选」也可能命中：给别名（hotstring）等待逻辑用的判断。
    /// </summary>
    internal static bool LeafHasCjk(string path) => QbarPinyin.HasCjk(Path.GetFileName(path));
}
