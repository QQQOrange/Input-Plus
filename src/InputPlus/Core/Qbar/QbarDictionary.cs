using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InputPlus.Core.Qbar;

/// <summary>一条释义。</summary>
public sealed record QbarDictionarySense(string PartOfSpeech, string Text);

/// <summary>一次查词的结果。Error 非空时 Senses 一定是空的。</summary>
public sealed record QbarDictionaryEntry(
    string Word,
    string Phonetic,
    IReadOnlyList<QbarDictionarySense> Senses,
    string Source,
    string? Error = null)
{
    public bool Ok => Error == null && Senses.Count > 0;
}

/// <summary>
/// 软件内查词：英文走 dictionaryapi.dev，中文走 moedict.tw，两边都是公开 JSON 接口，
/// 不需要 key、不需要装客户端。查不到/断网时把原因放在 <see cref="QbarDictionaryEntry.Error"/> 里，
/// 由界面翻译成人话 —— 这一层不弹窗、不写日志风暴。
///
/// 为什么不解析欧路词典网页：它的 DOM 一改就全废；而且用户要的是"在软件里看到释义"，
/// 不是"在软件里打开一个浏览器"。欧路仍然保留为回车时的外部打开方式。
/// </summary>
public static class QbarDictionary
{
    private const int CacheLimit = 200;
    private static readonly HttpClient Http = CreateClient();

    /// <summary>会话内缓存：同一个词反复查（打字过程里每个前缀都会查一次）不该反复打网络。</summary>
    private static readonly Dictionary<string, QbarDictionaryEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "InputPlus/1.6 (+https://github.com/QQQOrange/Input-Plus)");
        return client;
    }

    /// <summary>纯 ASCII 且没有 CJK = 英文词，走英文接口；其余走中文接口。</summary>
    public static bool IsChinese(string word)
    {
        foreach (char c in word)
        {
            if (c >= 0x4e00 && c <= 0x9fff) return true;
            if (c >= 0x3400 && c <= 0x4dbf) return true;
        }
        return false;
    }

    public static async Task<QbarDictionaryEntry> LookupAsync(string word, CancellationToken ct)
    {
        string key = (word ?? "").Trim();
        if (key.Length == 0) return new QbarDictionaryEntry("", "", Array.Empty<QbarDictionarySense>(), "none", "没有输入要查的词");

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out QbarDictionaryEntry? hit)) return hit;
        }

        QbarDictionaryEntry entry;
        try
        {
            entry = IsChinese(key) ? await LookupChineseAsync(key, ct).ConfigureAwait(false)
                                   : await LookupEnglishAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new QbarDictionaryEntry(key, "", Array.Empty<QbarDictionarySense>(), "none", "已取消");
        }
        catch (TaskCanceledException)
        {
            entry = new QbarDictionaryEntry(key, "", Array.Empty<QbarDictionarySense>(), "net", "查词超时（6 秒）");
        }
        catch (Exception ex)
        {
            entry = new QbarDictionaryEntry(key, "", Array.Empty<QbarDictionarySense>(), "net", "查词失败：" + ex.Message);
        }

        if (entry.Ok) Remember(key, entry);
        return entry;
    }

    private static void Remember(string key, QbarDictionaryEntry entry)
    {
        lock (Gate)
        {
            if (Cache.Count >= CacheLimit) Cache.Clear();   // 简单粗暴但够用：词典查询没有顺序语义
            Cache[key] = entry;
        }
    }

    // ---------------- 英文 ----------------

    private static async Task<QbarDictionaryEntry> LookupEnglishAsync(string word, CancellationToken ct)
    {
        string url = "https://api.dictionaryapi.dev/api/v2/entries/en/" + Uri.EscapeDataString(word);
        string body = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
        return ParseEnglish(word, body);
    }

    /// <summary>解析 dictionaryapi.dev 的响应（单独拆出来，自检可以拿固定 JSON 喂它）。</summary>
    public static QbarDictionaryEntry ParseEnglish(string word, string json)
    {
        var senses = new List<QbarDictionarySense>();
        string phonetic = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "dictionaryapi",
                    "词典里没有这个词");

            foreach (JsonElement entry in doc.RootElement.EnumerateArray())
            {
                if (phonetic.Length == 0 && entry.TryGetProperty("phonetic", out JsonElement ph))
                    phonetic = (ph.GetString() ?? "").Trim();
                if (phonetic.Length == 0 && entry.TryGetProperty("phonetics", out JsonElement phonetics) &&
                    phonetics.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement p in phonetics.EnumerateArray())
                    {
                        if (p.TryGetProperty("text", out JsonElement t) && !string.IsNullOrWhiteSpace(t.GetString()))
                        {
                            phonetic = t.GetString()!.Trim();
                            break;
                        }
                    }
                }

                if (!entry.TryGetProperty("meanings", out JsonElement meanings) ||
                    meanings.ValueKind != JsonValueKind.Array) continue;

                foreach (JsonElement meaning in meanings.EnumerateArray())
                {
                    string pos = meaning.TryGetProperty("partOfSpeech", out JsonElement p) ? (p.GetString() ?? "") : "";
                    if (!meaning.TryGetProperty("definitions", out JsonElement defs) ||
                        defs.ValueKind != JsonValueKind.Array) continue;
                    foreach (JsonElement def in defs.EnumerateArray())
                    {
                        string text = def.TryGetProperty("definition", out JsonElement d) ? (d.GetString() ?? "") : "";
                        text = Clean(text);
                        if (text.Length == 0) continue;
                        senses.Add(new QbarDictionarySense(ShortPos(pos), text));
                        if (senses.Count >= 12) break;
                    }
                    if (senses.Count >= 12) break;
                }
                if (senses.Count >= 12) break;
            }
        }
        catch (JsonException)
        {
            return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "dictionaryapi", "返回内容不是合法 JSON");
        }

        if (senses.Count == 0)
            return new QbarDictionaryEntry(word, phonetic, Array.Empty<QbarDictionarySense>(), "dictionaryapi", "词典里没有这个词");
        return new QbarDictionaryEntry(word, phonetic, senses, "dictionaryapi");
    }

    // ---------------- 中文 ----------------

    private static async Task<QbarDictionaryEntry> LookupChineseAsync(string word, CancellationToken ct)
    {
        /* 三路兜底，顺序是有讲究的：
           1) 有道 jsonapi 的 newhh 段 = 《现代汉语规范词典》，**简体繁体都认**，
              带拼音、词性、正经中文释义 —— 有它就够了；
           2) 萌典（moedict.tw）只有繁体：查「银行」直接 404，但它的释义更长、
              带注音，繁体词命中时质量更好；
           3) 爱词霸 suggest：前两个都没结果时给一串英译，来源里写清「英译」。
           全挂才报"没查到"，绝不在没有结果时显示空白列表。 */
        string firstError;
        try
        {
            string url = "https://dict.youdao.com/jsonapi?q=" + Uri.EscapeDataString(word);
            string body = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            QbarDictionaryEntry zh = ParseYoudaoChinese(word, body);
            if (zh.Ok) return zh;
            firstError = zh.Error ?? "没有这个词";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            firstError = ex.Message;
        }

        string moeError;
        try
        {
            string url = "https://www.moedict.tw/uni/" + Uri.EscapeDataString(word);
            string body = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            QbarDictionaryEntry zh = ParseChinese(word, body);
            if (zh.Ok) return zh;
            moeError = zh.Error ?? "没有这个词";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            moeError = ex.Message;
        }

        try
        {
            string url = "https://dict-mobile.iciba.com/interface/index.php"
                         + "?c=word&m=getsuggest&nums=1&is_need_mean=1&word=" + Uri.EscapeDataString(word);
            string body = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            QbarDictionaryEntry iciba = ParseIciba(word, body);
            if (iciba.Ok) return iciba;
            return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "iciba",
                "没查到这个词（有道：" + firstError + "；萌典：" + moeError
                + "；爱词霸：" + (iciba.Error ?? "没有这个词") + "）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "none", "查词失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 解析有道 jsonapi 里 newhh 段（《现代汉语规范词典》）。
    ///
    /// 别看整份响应有十几个段，中文词真正有用的就是 newhh：拼音 + 词性 + 释义。
    /// simple 段只有拼音，baike 段是百科摘要（顺带当一条补充释义，标「百科」）。
    /// </summary>
    public static QbarDictionaryEntry ParseYoudaoChinese(string word, string json)
    {
        var senses = new List<QbarDictionarySense>();
        string phonetic = "";
        string source = "youdao";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "youdao", "返回内容格式不对");

            if (doc.RootElement.TryGetProperty("newhh", out JsonElement newhh) &&
                newhh.ValueKind == JsonValueKind.Object)
            {
                if (newhh.TryGetProperty("source", out JsonElement src) &&
                    src.TryGetProperty("name", out JsonElement srcName))
                    source = "youdao·" + (srcName.GetString() ?? "现代汉语");

                if (newhh.TryGetProperty("dataList", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        if (phonetic.Length == 0 && item.TryGetProperty("pinyin", out JsonElement py))
                            phonetic = (py.GetString() ?? "").Trim();

                        if (!item.TryGetProperty("sense", out JsonElement senseList) ||
                            senseList.ValueKind != JsonValueKind.Array) continue;

                        foreach (JsonElement sense in senseList.EnumerateArray())
                        {
                            string pos = sense.TryGetProperty("cat", out JsonElement cat)
                                ? (cat.GetString() ?? "").Trim()
                                : "";
                            if (pos.Length == 0 && item.TryGetProperty("cat", out JsonElement cat2))
                                pos = (cat2.GetString() ?? "").Trim();

                            if (!sense.TryGetProperty("def", out JsonElement defs) ||
                                defs.ValueKind != JsonValueKind.Array) continue;

                            foreach (JsonElement def in defs.EnumerateArray())
                            {
                                string text = Clean(def.GetString() ?? "");
                                if (text.Length == 0) continue;
                                senses.Add(new QbarDictionarySense(pos, text));
                                if (senses.Count >= 12) break;
                            }
                            if (senses.Count >= 12) break;
                        }
                        if (senses.Count >= 12) break;
                    }
                }
            }

            /* 拼音另一条来源：simple.word[0].phone。newhh 缺席时靠它补上。 */
            if (phonetic.Length == 0 && doc.RootElement.TryGetProperty("simple", out JsonElement simple) &&
                simple.ValueKind == JsonValueKind.Object &&
                simple.TryGetProperty("word", out JsonElement words) && words.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement w in words.EnumerateArray())
                {
                    if (w.TryGetProperty("phone", out JsonElement phone) &&
                        !string.IsNullOrWhiteSpace(phone.GetString()))
                    {
                        phonetic = phone.GetString()!.Trim();
                        break;
                    }
                }
            }

            /* 百科摘要当最后一条补充：有些词（人名、地名、术语）只有百科讲得清。 */
            if (senses.Count < 12 && doc.RootElement.TryGetProperty("baike", out JsonElement baike) &&
                baike.ValueKind == JsonValueKind.Object &&
                baike.TryGetProperty("summarys", out JsonElement sums) && sums.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement s in sums.EnumerateArray())
                {
                    if (!s.TryGetProperty("summary", out JsonElement sum)) continue;
                    string text = Clean(sum.GetString() ?? "");
                    if (text.Length == 0) continue;
                    senses.Add(new QbarDictionarySense("百科", text));
                    break;
                }
            }
        }
        catch (JsonException)
        {
            return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "youdao", "返回内容不是合法 JSON");
        }

        if (senses.Count == 0)
            return new QbarDictionaryEntry(word, phonetic, Array.Empty<QbarDictionarySense>(), "youdao", "没有这个词");
        return new QbarDictionaryEntry(word, phonetic, senses, source);
    }

    /// <summary>
    /// 解析爱词霸的 suggest 响应：简体中文词 → 英译列表。
    ///
    /// 结构是 message[0].means[].means[]，一层套一层；词性那边基本是空的，
    /// 所以统一标成「英译」，让用户在列表里一眼看出这不是中文释义。
    /// </summary>
    public static QbarDictionaryEntry ParseIciba(string word, string json)
    {
        var senses = new List<QbarDictionarySense>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("message", out JsonElement msg) ||
                msg.ValueKind != JsonValueKind.Array)
                return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "iciba", "没有这个词");

            foreach (JsonElement m in msg.EnumerateArray())
            {
                if (!m.TryGetProperty("means", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement group in groups.EnumerateArray())
                {
                    if (!group.TryGetProperty("means", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        string text = Clean(item.GetString() ?? "");
                        if (text.Length == 0) continue;
                        senses.Add(new QbarDictionarySense("英译", text));
                        if (senses.Count >= 12) break;
                    }
                    if (senses.Count >= 12) break;
                }
                if (senses.Count >= 12) break;
            }
        }
        catch (JsonException)
        {
            return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "iciba", "返回内容不是合法 JSON");
        }

        if (senses.Count == 0)
            return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "iciba", "没有这个词");
        return new QbarDictionaryEntry(word, "", senses, "iciba·英译");
    }

    /// <summary>解析萌典（moedict.tw）的响应。</summary>
    public static QbarDictionaryEntry ParseChinese(string word, string json)
    {
        var senses = new List<QbarDictionarySense>();
        string phonetic = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "moedict", "词典里没有这个词");

            if (doc.RootElement.TryGetProperty("heteronyms", out JsonElement het) && het.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement h in het.EnumerateArray())
                {
                    if (phonetic.Length == 0 && h.TryGetProperty("bopomofo", out JsonElement b))
                        phonetic = (b.GetString() ?? "").Trim();

                    if (!h.TryGetProperty("definitions", out JsonElement defs) || defs.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (JsonElement def in defs.EnumerateArray())
                    {
                        string text = def.TryGetProperty("def", out JsonElement d) ? (d.GetString() ?? "") : "";
                        text = Clean(text);
                        if (text.Length == 0) continue;
                        string pos = def.TryGetProperty("type", out JsonElement t) ? (t.GetString() ?? "").Trim() : "";
                        senses.Add(new QbarDictionarySense(pos, text));
                        if (senses.Count >= 12) break;
                    }
                    if (senses.Count >= 12) break;
                }
            }
        }
        catch (JsonException)
        {
            return new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "moedict", "返回内容不是合法 JSON");
        }

        if (senses.Count == 0)
            return new QbarDictionaryEntry(word, phonetic, Array.Empty<QbarDictionarySense>(), "moedict", "词典里没有这个词");
        return new QbarDictionaryEntry(word, phonetic, senses, "moedict");
    }

    // ---------------- 小工具 ----------------

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>去掉 HTML 标签、把 Wiktionary 常用的缩进符号和多余空白收掉。</summary>
    internal static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string text = TagRegex.Replace(raw, "");
        text = text.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");
        text = text.Replace('\u3000', ' ');
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return text.TrimStart('：', ':', '-', '—', '＋', '+').Trim();
    }

    private static string ShortPos(string pos)
    {
        if (string.IsNullOrWhiteSpace(pos)) return "";
        return pos.Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            "noun" => "n.",
            "verb" => "v.",
            "adjective" => "adj.",
            "adverb" => "adv.",
            "pronoun" => "pron.",
            "preposition" => "prep.",
            "conjunction" => "conj.",
            "interjection" => "int.",
            "determiner" => "det.",
            "numeral" => "num.",
            _ => pos.Trim(),
        };
    }
}
