using System.Globalization;
using System.Text;
using InputPlus.Core.Input;

namespace InputPlus.Core.Settings;

/// <summary>
/// 「从剪贴板加一条别名」的纯函数：给一段内容起触发词、整理、查重、截断。
///
/// ## 为什么要单独一个类
///
/// 这段逻辑原来没有 —— 用户只能先复制内容、打开别名页、点新增、粘贴、
/// 自己想一个触发词、再自己补前缀。真正容易出错的是最后一步：
/// 补前缀、判重、超长截断，全都要跟"保存时到底存了什么"一致。
/// 放在界面代码里就没法自检；抽成纯函数之后，验收表里的 18 条能逐条钉住。
///
/// ## 三条硬规则
///
///   · 触发词**不许含空白**（含空白必然匹配不上：缓冲区按物理键拼，空格是边界）；
///   · 前缀**自动补**（用户不用记得当前配的是 `;` 还是 `、`）；
///   · 撞名**自动加后缀**（不弹"触发词已存在"让人自己改，那条路径没人愿意走第二遍）。
/// </summary>
public static class SnippetDraft
{
    /// <summary>别名内容的最大长度（与设置里的输入框上限一致）。</summary>
    public const int MaxTextLength = 32000;

    /// <summary>起不出名字时的兜底词。</summary>
    private const string FallbackWord = "clip";

    /// <summary>备注取多少个字。</summary>
    private const int CommentLength = 30;

    /// <summary>兜底名字最多取几个字（中文一行也能当触发词）。</summary>
    private const int FallbackWordLength = 8;

    /// <summary>ASCII 单词最多取多长。</summary>
    private const int AsciiWordLength = 12;

    /// <summary>
    /// 给一段内容猜一个"词"（**不含前缀**）。
    ///
    /// 顺序：第一个非空行的第一个 ASCII 单词 → 该行去掉空白后的前 8 个字 → "clip"。
    /// 为什么先找 ASCII 单词：英文/代码/命令占实际内容的大多数，
    /// 而英文触发词在任何输入法下都能打出来。
    /// </summary>
    public static string SuggestWord(string? text)
    {
        string line = FirstNonEmptyLine(text);
        if (line.Length == 0) return FallbackWord;

        /* ② 第一个连续 ASCII 单词（字母/数字/下划线），转小写。 */
        for (int i = 0; i < line.Length;)
        {
            if (!IsAsciiWordChar(line[i])) { i++; continue; }

            int start = i;
            while (i < line.Length && IsAsciiWordChar(line[i])) i++;
            int take = Math.Min(AsciiWordLength, i - start);
            return line.Substring(start, take).ToLowerInvariant();
        }

        /* ③ 没有 ASCII 单词：取去掉空白后的前 8 个字（中文可用）。
         *
         * 这里顺手丢掉纯符号（`@#$%` 这种）：整行都是符号时它当不了触发词 ——
         * 用户不会为了展开一串符号去记一个符号触发词，落到 `clip` 更实际。 */
        var sb = new StringBuilder(FallbackWordLength);
        foreach (char c in line)
        {
            if (sb.Length >= FallbackWordLength) break;
            if (char.IsWhiteSpace(c)) continue;
            if (!IsContentChar(c)) continue;
            sb.Append(c);
        }
        if (sb.Length > 0) return sb.ToString();

        /* ④ 兜底。 */
        return FallbackWord;
    }

    /// <summary>给一段内容猜一个**完整触发词**（含前缀、已查重）。</summary>
    public static string SuggestTrigger(string? text, IReadOnlyList<string> prefixes, IEnumerable<Snippet> existing)
        => Normalize(SuggestWord(text), prefixes, existing);

    /// <summary>
    /// 把用户（或 <see cref="SuggestWord"/>）给的词整理成最终触发词。
    ///
    /// 顺序不能乱：先去空白 → 判"是否已经带前缀" → 补前缀 → 校验 → 查重。
    /// 调换任何两步都会出现"补了两遍前缀"或"查重查的是裸词"这类问题。
    /// </summary>
    /// <returns>最终触发词；<c>""</c> 表示这次没法加（调用方应中止并提示）。</returns>
    public static string Normalize(string input, IReadOnlyList<string> prefixes, IEnumerable<Snippet> existing)
    {
        var list = (prefixes ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        /* ① 去首尾空白；空就退回自动猜一个。 */
        string word = (input ?? "").Trim();
        if (word.Length == 0) word = SuggestWord(null);

        /* ② 触发词不许含空白 —— 缓冲区里空白就是边界，带空白的触发词永远匹配不上。 */
        word = StripWhitespace(word);
        if (word.Length == 0) word = FallbackWord;

        /* ⑤ 前缀列表为空：无法补出合法触发词，直接中止（调用方给出明确提示）。 */
        if (list.Count == 0) return "";

        /* ③④ 已经带任一前缀就原样保留，否则补第一个前缀。 */
        string candidate = !string.Equals(SnippetPrefixOps.StripAny(word, list), word, StringComparison.Ordinal)
            ? word
            : SnippetPrefixOps.ApplyAny(word, list);

        /* ⑤ 合法性：不合法就强制补一次前缀再判；仍然不合法 → 中止。 */
        if (!SnippetMatcher.IsValidTrigger(candidate))
        {
            candidate = list[0] + StripWhitespace(candidate);
            if (!SnippetMatcher.IsValidTrigger(candidate)) return "";
        }

        /* ⑥ 查重：折叠后**逐字**相等才算撞名（`;Mail` 与 `;mail` 是两条）。 */
        var others = (existing ?? Enumerable.Empty<Snippet>()).Where(x => x != null).ToList();
        if (!Conflicts(candidate, others)) return candidate;

        string prefix = list[0];
        string stem = SnippetPrefixOps.StripAny(candidate, list);
        if (stem.Length == 0) stem = FallbackWord;

        /* ⑥ 依次试 `前缀 + 词干 + N`（2…99）。 */
        for (int n = 2; n <= 99; n++)
        {
            string alt = prefix + stem + n.ToString(CultureInfo.InvariantCulture);
            if (!Conflicts(alt, others)) return alt;
        }

        /* ⑥ 词干的 98 个号码全被占了，换成底词再扫一遍。 */
        for (int n = 2; n <= 99; n++)
        {
            string alt = prefix + FallbackWord + n.ToString(CultureInfo.InvariantCulture);
            if (!Conflicts(alt, others)) return alt;
        }

        return "";
    }

    /// <summary>
    /// 生成一条可以入库的别名；内容为空、或触发词无法整理时返回 null。
    ///
    /// 只做"内容 → 别名"的搬运，不碰设置、不写盘 —— 落库与通知由调用方负责，
    /// 这样这条路径在自检里可以完整跑一遍（不依赖界面、不依赖配置文件）。
    /// </summary>
    public static Snippet? Build(string text, string triggerInput,
                                 IReadOnlyList<string> prefixes, IEnumerable<Snippet> existing,
                                 string group = "")
    {
        /* ① 空内容没有可加的别名。 */
        if (string.IsNullOrWhiteSpace(text)) return null;

        /* ② 触发词整理不出来就中止。 */
        string trigger = Normalize(triggerInput, prefixes, existing);
        if (trigger.Length == 0) return null;

        /* ③ 内容：去掉 \0（Win32 文本框把它当字符串结尾，留着会把后面的内容截掉），
         *    其它控制字符（换行/制表）原样保留。 */
        string body = text.Replace("\0", "");
        if (body.Length > MaxTextLength)
        {
            int cut = MaxTextLength;
            /* 截断点落在代理对中间会留下半个字符（显示成方块），往前退一位。 */
            if (char.IsHighSurrogate(body[cut - 1])) cut--;
            body = body.Substring(0, cut);
        }

        /* ④ 备注取第一个非空行（超长内容在列表里才有辨识度）。 */
        string comment = BuildComment(body);

        /* ⑤ */
        return new Snippet
        {
            Trigger = trigger,
            Text = body,
            Comment = comment,
            Group = group ?? "",
            Enabled = true,
        };
    }

    /// <summary>两个触发词是否撞名：折叠后逐字相等（区分大小写）。</summary>
    private static bool Conflicts(string candidate, List<Snippet> existing)
    {
        string folded = SnippetMatcher.Fold(candidate);
        foreach (var s in existing)
        {
            if (string.Equals(folded, SnippetMatcher.Fold(s.Trigger), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>备注：第一个非空行，换行/制表折成空格，取前 30 个字。</summary>
    private static string BuildComment(string body)
    {
        foreach (string raw in body.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string flat = raw.Replace('\r', ' ').Replace('\t', ' ').Trim();
            if (flat.Length == 0) continue;

            return flat.Length > CommentLength ? flat.Substring(0, CommentLength) : flat;
        }
        return Loc.T("来自剪贴板");
    }

    /// <summary>第一个非空行，去首尾空白。</summary>
    private static string FirstNonEmptyLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (string raw in text!.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0) return line;
        }
        return "";
    }

    private static string StripWhitespace(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s!.Length);
        foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    private static bool IsAsciiWordChar(char c)
        => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';

    /// <summary>能当触发词正文的字符：字母/数字/下划线/汉字等（纯符号不算）。</summary>
    private static bool IsContentChar(char c)
        => IsAsciiWordChar(c) || char.IsLetterOrDigit(c);
}
