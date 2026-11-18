using System;
using System.Collections.Concurrent;
using System.Text;

namespace InputPlus.Core.Qbar;

/// <summary>
/// Qbar 的“缩写搜索”：微信 → WX，Control Panel → CP。
/// 中文取拼音首字母，英文按单词取首字母；结果按字符串缓存，
/// 查询时每个字符都会重跑，不能每次都重新算一遍。
/// </summary>
public static class QbarTextMatch
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>把标题转成大写缩写；没有可用字母时返回空串。</summary>
    public static string Initials(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (Cache.TryGetValue(text, out var cached)) return cached;

        var sb = new StringBuilder(text.Length);
        bool inWord = false;
        foreach (char c in text)
        {
            bool chinese = c >= 0x3400 && c <= 0x9FFF;
            if (chinese)
            {
                sb.Append(TinyPinyin.PinyinHelper.GetPinyinInitials(c.ToString()));
                inWord = false;
            }
            else if (char.IsLetterOrDigit(c))
            {
                if (!inWord) sb.Append(char.ToUpperInvariant(c));
                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        string result = sb.ToString();
        if (Cache.Count > 4096) Cache.Clear();
        Cache[text] = result;
        return result;
    }

    /// <summary>标题本身包含查询，或标题缩写包含查询。</summary>
    public static bool Matches(string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query)) return false;
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;

        // 单字母缩写太容易误命中（“S”能命中一半应用），至少两个字母才走缩写。
        if (query.Length < 2) return false;
        string initials = Initials(text);
        return initials.Length > 0 &&
               initials.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
