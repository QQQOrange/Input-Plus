using System;
using System.Collections.Generic;
using System.Text;
using TinyPinyin;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 「收藏」时要填进 Qbar 固定项的关键字推导（v1.5.0；v1.5.1 起也负责文件名）。
///
/// 规则来自用户的原话，这里把它写成可测的纯函数：
///   · 网址：取 <c>www</c> 与 <c>com</c> 中间的那一段
///     （<c>www.example.com</c> → <c>example</c>）。"类似的逻辑进一步完善"之后
///     统一成"可注册域名的那个标签"：<c>mail.google.com</c> → google、
///     <c>bbs.szu.edu.cn</c> → szu、<c>example.co.uk</c> → example。
///   · 英文名字：多个单词取每个单词首字母（最多 4 个）；
///     只有一个单词取前 3 个字母。
///   · 中文名字：取拼音首字母（最多 4 个）。
///   · 程序名：与英文名字同一套。
///   · 文件名（v1.5.1）：先剥扩展名 / 锁文件前缀 / 全角，再走上面同一套。
///
/// 为什么必须短且是 ASCII：Qbar 的关键字是"打字就能命中的前缀"，
/// 中文/长串都要多敲好几下，反而比直接搜文件名慢。
/// </summary>
public static class FavoriteKeywords
{
    /// <summary>自动推导出来的关键字最长几个字符。</summary>
    public const int MaxLength = 4;

    /// <summary>用户手改之后允许的最大长度（手改的不受"只取首字母"限制）。</summary>
    public const int MaxTypedLength = 16;

    /// <summary>两段式公共后缀（这几种情况下 <c>com</c> 不是最后一段）。</summary>
    private static readonly string[] TwoLevelSuffixes =
    {
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "ac.cn",
        "co.uk", "org.uk", "ac.uk", "gov.uk",
        "com.hk", "com.tw", "com.sg", "com.au", "com.br", "com.mx",
        "co.jp", "co.kr", "co.in", "co.nz", "co.za",
    };

    /// <summary>可以剥掉的前缀标签（只在还剩至少两段时剥）。</summary>
    private static readonly string[] CommonPrefixes = { "www", "www2", "ww1", "ww2", "m", "wap", "mobile" };

    /// <summary>
    /// 网址 → 关键字。取可注册域名的标签：<c>https://www.example.com/a?b</c> → <c>example</c>。
    /// </summary>
    public static string FromUrl(string? url)
    {
        string raw = (url ?? "").Trim();
        if (raw.Length == 0) return "web";

        string host = HostOf(raw);
        if (host.Length == 0) return FromName(raw);
        return FromHost(host);
    }

    /// <summary>主机名 → 关键字。IP 地址取第一段的最后几位没有意义，直接给 <c>ip</c>。</summary>
    public static string FromHost(string? host)
    {
        string h = (host ?? "").Trim().Trim('.').ToLowerInvariant();
        if (h.Length == 0) return "web";

        if (IsIpLiteral(h)) return "ip";

        string[] labels = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0) return "web";

        // 剥掉 www / m / wap 这类前缀，但至少留两段（"www" 本身不是站点）
        int start = 0;
        while (labels.Length - start > 2 && Array.IndexOf(CommonPrefixes, labels[start]) >= 0) start++;

        int suffix = SuffixLength(labels, start);
        int sld = labels.Length - suffix - 1;
        if (sld < start) sld = start;

        string candidate = labels[sld];
        // 国际化域名在这里是 punycode（xn--…）：剥掉前缀，剩下的至少还能当个短码
        if (candidate.StartsWith("xn--", StringComparison.Ordinal)) candidate = candidate[4..];

        string clean = KeepAlnum(candidate);
        if (clean.Length > MaxTypedLength) clean = clean[..MaxTypedLength];
        return clean.Length == 0 ? "web" : clean;
    }

    /// <summary>
    /// 名字（文件夹名 / 程序名）→ 关键字。
    /// 多词取首字母（最多 4）、单词取前 3 个字母、中文取拼音首字母（最多 4）。
    ///
    /// v1.5.1 起它只是 <see cref="Compose"/> 的薄壳：文件名（<see cref="KeywordFor"/>）
    /// 与文件夹名走同一套分词和取字母规则，差别只有"要不要先剥扩展名"和兜底词。
    /// 分成两个入口而不是硬塞进一个，是因为**文件夹名里的点不是扩展名**
    /// （"Node.js 笔记"剥一下就只剩 "Node" 了）。
    /// </summary>
    public static string FromName(string? name) => Compose((name ?? "").Trim(), false, "fav");

    // ── 文件名 → 关键字（v1.5.1：附加菜单「收藏当前文件」用）─────────────

    /// <summary>
    /// 版本残留词：和纯数字一样，不是用户的检索锚点。
    ///
    /// 为什么必须有：「测试1.2版」按"中文段逐字取首字母"会得到 <c>csb</c>（多一个"版"），
    /// 而用户记的是 <c>cs</c>。版本后缀在打字时没人会敲，跳过它和跳过 2024 是同一条理由。
    /// </summary>
    private static readonly string[] VersionStopWords = { "版", "版本", "ver", "version" };

    /// <summary>
    /// 文件名 → 关键字（**纯函数**：不碰 IO、不碰 UI、不依赖进程，自检可直接断言）。
    ///
    /// 规则（用户口径，别自创）：
    ///   英文多词 → 每个词首字母，最多 4；英文单词 → 前 3 个字母；
    ///   中文 → 拼音首字母，最多 4；中英混合 → 合并后截 4；一律小写。
    /// <paramref name="widenSingleWord"/> 只给撞车升级链用：单词取的字母数 3 → 4
    /// （<c>rep</c> → <c>repo</c>）。只影响"单英文词"这一条分支 ——
    /// 否则"升级"会把中文、多词的结果也一起改掉，用户看到的就成了另一个词。
    /// </summary>
    public static string KeywordFor(string? fileName, bool widenSingleWord = false)
    {
        string stem = StemOf(fileName);
        return stem.Length == 0 ? "file" : Compose(stem, widenSingleWord, "file");
    }

    /// <summary>
    /// 关键字撞车时的升级链：<c>rep</c> → <c>repo</c> → <c>rep2</c> → <c>rep3</c>…
    ///
    /// 为什么不复用 <see cref="Unique"/>：那条只会在尾巴上加数字（rep2），
    /// 而"同一个文件名再给一个字母"更接近用户对第二次尝试的直觉，也更难和别人的词撞。
    /// 查重与插入必须由调用方放进同一个事务（这里只负责算一个当前没人用的词）。
    /// </summary>
    public static string KeywordForUnique(string? fileName, IEnumerable<string>? taken)
    {
        string baseKey = KeywordFor(fileName);
        if (taken == null) return baseKey;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in taken)
        {
            string k = Sanitize(key);
            if (k.Length > 0) used.Add(k);
        }

        if (!used.Contains(baseKey)) return baseKey;

        string wider = KeywordFor(fileName, widenSingleWord: true);
        if (!string.Equals(wider, baseKey, StringComparison.OrdinalIgnoreCase) && !used.Contains(wider))
            return wider;

        for (int i = 2; i <= 99; i++)
        {
            string candidate = baseKey + i.ToString();
            if (!used.Contains(candidate)) return candidate;
        }
        return baseKey + Guid.NewGuid().ToString("N")[..2];
    }

    /// <summary>
    /// 分词 + 取字母的公共内核。<paramref name="fallback"/> 是"一个可用字符都没有"时的兜底：
    /// 文件名给 <c>file</c>、文件夹/程序名给 <c>fav</c>（沿用 v1.5.0 的老口径，老界面不跳词）。
    /// </summary>
    private static string Compose(string text, bool widen, string fallback)
    {
        var words = Tokenize(text);
        if (words.Count == 0) return fallback;

        /* 整串就是数字（12345、2024）：取首 2 位。
         * 不能落到下面的分词规则 —— 那里的"纯数字 token 跳过"会让结果变成空串。 */
        if (words.Count == 1 && IsAllDigits(words[0]))
            return words[0].Length <= 2 ? words[0] : words[0][..2];

        var keep = new List<string>(words.Count);
        foreach (string w in words)
            if (Array.IndexOf(VersionStopWords, w.ToLowerInvariant()) < 0) keep.Add(w);
        if (keep.Count == 0) return fallback;

        /* 只有一个纯英文词：取前 3 个字母（Report → rep）。
         * 中文单字不走这条 —— 它要按拼音首字母算，不该被截成 3 个。 */
        if (keep.Count == 1 && !ContainsCjk(keep[0]))
        {
            string only = KeepAlnum(keep[0]);
            if (only.Length == 0) return fallback;
            int take = widen ? Math.Min(MaxLength, only.Length) : Math.Min(3, only.Length);
            return only[..take];
        }

        /* 纯数字词（2024 / 01）在有别的词时没有信息量，跳过 ——
         * 否则 "2024 Q3 Report-v2" 会变成 2qr，而用户记的是 qrv。
         * "v2" 不是纯数字，它的首字母 v 保留。 */
        bool hasWord = keep.Exists(w => !IsAllDigits(w));

        var sb = new StringBuilder(MaxLength);
        foreach (string word in keep)
        {
            if (sb.Length >= MaxLength) break;
            if (hasWord && IsAllDigits(word)) continue;

            if (ContainsCjk(word))
            {
                foreach (char c in SafeInitials(word))
                {
                    if (sb.Length >= MaxLength) break;
                    sb.Append(char.ToLowerInvariant(c));
                }
                continue;
            }

            string clean = KeepAlnum(word);
            if (clean.Length == 0) continue;
            sb.Append(char.ToLowerInvariant(clean[0]));
        }

        if (sb.Length == 0)
        {
            string all = KeepAlnum(text);
            return all.Length == 0 ? fallback : all[..Math.Min(2, all.Length)];
        }
        return sb.ToString();
    }

    /// <summary>
    /// 文件名 → 去掉扩展名与锁文件痕迹的词干。
    ///
    /// 两个坑都踩过：
    ///   ① **必须先全角转半角、再剥扩展名**。反过来的话 &quot;Ｒｅｐｏｒｔ．ｐｄｆ&quot; 里那个
    ///      全角句点认不出是分隔符，会被切成 Report + pdf 两个词，关键字变成 rp；
    ///   ② **不能用 Path.GetFileNameWithoutExtension**：它对 &quot;.gitignore&quot; 返回空串
    ///      （把整个名字当扩展名），于是掉进「无可用字符 → file」，用户手打的
    ///      .gitignore 从此搜不到。自己剥最后一段句点就好。
    /// </summary>
    private static string StemOf(string? fileName)
    {
        string name = ToHalfWidth((fileName ?? "").Trim());
        if (name.Length == 0) return "";
        try { name = Path.GetFileName(name); } catch { /* 非法字符：按原串继续 */ }

        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];   // dot == 0 是前导点（.gitignore / .env），不当扩展名

        while (name.StartsWith("~$", StringComparison.Ordinal)) name = name[2..].Trim();   // Office / WPS 锁文件
        name = name.TrimStart('.');
        return name.Trim();
    }

    /// <summary>全角 → 半角（含全角空格）。中文输入法开着时，文件名经常是这副样子。</summary>
    private static string ToHalfWidth(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\u3000') sb.Append(' ');
            else if (c >= '\uFF01' && c <= '\uFF5E') sb.Append((char)(c - 0xFEE0));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 分词：空白与 <c>. _ - + ( ) [ ] { } 、 ， / \</c> 一律当分隔，
    /// 另外在"中文 ↔ 英文/数字"的交界处切一刀（UI 设计稿 → UI + 设计稿）。
    ///
    /// 分隔符不单独列表：它们全都不是字母数字，与
    /// <see cref="char.IsLetterOrDigit(char)"/> 的判定天然互斥 ——
    /// 列一张表只会让人以为加了标点就得回来同步，其实不必。
    /// </summary>
    private static List<string> Tokenize(string text)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        bool curCjk = false;

        foreach (char c in text)
        {
            if (!char.IsLetterOrDigit(c))
            {
                if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            bool cjk = IsCjk(c);
            if (cur.Length > 0 && cjk != curCjk) { words.Add(cur.ToString()); cur.Clear(); }
            curCjk = cjk;
            cur.Append(c);
        }
        if (cur.Length > 0) words.Add(cur.ToString());
        return words;
    }

    /// <summary>
    /// 用户手改之后的关键字清洗：去掉空白（Qbar 的关键字不能带空格，空格是分隔符）、
    /// 只留字母数字与 <c>-</c> <c>_</c>、小写。
    /// </summary>
    public static string Sanitize(string? raw, string fallback = "")
    {
        string text = (raw ?? "").Trim();
        if (text.Length == 0) return fallback;

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (IsLetterOrDigit(c) || c is '-' or '_') sb.Append(char.ToLowerInvariant(c));
        }

        if (sb.Length > MaxTypedLength) sb.Length = MaxTypedLength;
        return sb.Length == 0 ? fallback : sb.ToString();
    }

    /// <summary>
    /// 关键字去重：已经有人用了就依次试 <c>xxx2</c>、<c>xxx3</c>…（最多 99）。
    ///
    /// 为什么必须做：Qbar 的关键字是"打字命中的前缀"，两个条目同名时用户根本分不清
    /// 点的是哪一个；而收藏是**一键**完成的，用户没机会先看一眼冲突。
    /// </summary>
    public static string Unique(string? keyword, IEnumerable<string>? taken)
    {
        string baseKey = Sanitize(keyword, "fav");
        if (taken == null) return baseKey;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in taken)
        {
            string k = Sanitize(key);
            if (k.Length > 0) used.Add(k);
        }

        if (!used.Contains(baseKey)) return baseKey;
        for (int i = 2; i <= 99; i++)
        {
            string candidate = baseKey + i.ToString();
            if (!used.Contains(candidate)) return candidate;
        }
        return baseKey + Guid.NewGuid().ToString("N")[..2];
    }

    // ── 内部工具 ───────────────────────────────────────────────────

    private static string HostOf(string raw)
    {
        try
        {
            if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0)
                return uri.Host;
        }
        catch { }

        /* 没有协议头的写法（"example.com/a"）：先补 https 再试一次。 */
        try
        {
            if (Uri.TryCreate("https://" + raw, UriKind.Absolute, out Uri? uri2) && uri2.Host.Length > 0)
                return uri2.Host;
        }
        catch { }

        return "";
    }

    private static bool IsIpLiteral(string host)
    {
        if (host.Length == 0) return false;
        bool digitsAndDots = true;
        foreach (char c in host)
            if (!char.IsDigit(c) && c != '.') { digitsAndDots = false; break; }
        if (digitsAndDots && host.Contains('.')) return true;
        return host.Contains(':') || host.StartsWith("[", StringComparison.Ordinal);
    }

    private static int SuffixLength(string[] labels, int start)
    {
        if (labels.Length - start >= 3)
        {
            string lastTwo = labels[^2] + "." + labels[^1];
            if (Array.IndexOf(TwoLevelSuffixes, lastTwo) >= 0) return 2;
        }
        return 1;
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF)     // 基本区
        || (c >= 0x3400 && c <= 0x4DBF)  // 扩展 A
        || (c >= 0xF900 && c <= 0xFAFF)  // 兼容区
        || (c >= 0x3040 && c <= 0x30FF); // 日文假名一并按"中文"取首字母

    /// <summary>字母数字（含中文/其它文字都算"字"）。</summary>
    private static bool IsLetterOrDigit(char c) => char.IsLetterOrDigit(c);

    private static bool ContainsCjk(string text)
    {
        foreach (char c in text) if (IsCjk(c)) return true;
        return false;
    }

    private static bool IsAllDigits(string text)
    {
        if (text.Length == 0) return false;
        foreach (char c in text) if (!char.IsDigit(c)) return false;
        return true;
    }

    private static string SafeInitials(string text)
    {
        try
        {
            string value = PinyinHelper.GetPinyinInitials(text);
            return value ?? "";
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "收藏：取拼音首字母");
            return "";
        }
    }

    private static string KeepAlnum(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) && !IsCjk(c)) sb.Append(c);
            else if (c is '-' or '_') sb.Append(c);
        }
        return sb.ToString().ToLowerInvariant();
    }
}
