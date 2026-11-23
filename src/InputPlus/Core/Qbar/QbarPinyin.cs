using System;
using System.Text;
using TinyPinyin;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 文件名 → 拼音。全仓**只有这一份**转换实现：建索引时填 <c>pinyin</c>/<c>pinyin_initials</c>、
/// 查询时判断「这个候选是不是真的拼音命中」，都从这里走。
///
/// 为什么必须共用：两边各写一次的话，`PinyinHelper.GetPinyin(name, "")` 与
/// `GetPinyin(name, " ")` 只是分隔符不同，结果就会对不上 —— 索引里存的是
/// <c>nianduzongjie</c>、查询时算出来的是 <c>nian du zong jie</c>，于是"明明有拼音却搜不到"。
/// </summary>
internal static class QbarPinyin
{
    /// <summary>取全拼（无分隔、小写）。非中文原样保留，标点会被 TinyPinyin 丢掉。</summary>
    internal static string Full(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        try
        {
            string value = PinyinHelper.GetPinyin(name, "");
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace(" ", "").Replace("\t", "").ToLowerInvariant();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "取拼音全拼");
            return "";
        }
    }

    /// <summary>取首字母（小写）。例如「年度总结」→ <c>ndzj</c>。</summary>
    internal static string Initials(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        try
        {
            string value = PinyinHelper.GetPinyinInitials(name);
            return string.IsNullOrEmpty(value) ? "" : value.ToLowerInvariant();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "取拼音首字母");
            return "";
        }
    }

    /// <summary>名字里含不含汉字。只有含汉字的才值得做拼音索引，纯 ASCII 名字的拼音就是它自己。</summary>
    internal static bool HasCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] >= '\u2E80') return true;
        }
        return false;
    }

    /// <summary>
    /// 查询串归一化：去掉空格 / 撇号 / 连字符 / 下划线并转小写。
    /// 用户敲 <c>nian du</c>、<c>nian-du</c>、<c>niandu</c> 都应该等价。
    /// </summary>
    internal static string NormalizeQuery(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = char.ToLowerInvariant(text[i]);
            if (c == ' ' || c == '\'' || c == '-' || c == '_') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary><paramref name="needle"/> 是不是 <paramref name="text"/> 的子序列（可跳字符）。</summary>
    internal static bool IsSubsequence(string text, string needle)
    {
        if (needle.Length == 0) return false;
        int t = 0;
        for (int n = 0; n < needle.Length; n++)
        {
            while (t < text.Length && text[t] != needle[n]) t++;
            if (t >= text.Length) return false;
            t++;
        }
        return true;
    }

    /// <summary>
    /// 拼音命中的质量分（越大越靠前）：首字母前缀 &gt; 全拼前缀 &gt; 全拼子序列。
    /// 与改造前的 <c>QbarPinyinIndex.Query</c> 完全同一套档位 —— 索引换了，
    /// 「哪种命中更可信」这件事没有理由变。
    /// </summary>
    internal static int Quality(string name, string query, out bool matched)
    {
        matched = false;
        string q = NormalizeQuery(query);
        if (q.Length < 2) return 0;

        string ini = Initials(name);
        string whole = Full(name);

        if (ini.Length > 0 && ini.StartsWith(q, StringComparison.Ordinal))
        {
            matched = true;
            return 200 - Math.Min(80, ini.Length - q.Length);
        }
        if (whole.Length > 0 && whole.StartsWith(q, StringComparison.Ordinal))
        {
            matched = true;
            return 150 - Math.Min(60, whole.Length - q.Length);
        }
        if (whole.Length > 0 && IsSubsequence(whole, q))
        {
            matched = true;
            return 80 - Math.Min(40, (name?.Length ?? 0) / 4);
        }
        return 0;
    }
}
