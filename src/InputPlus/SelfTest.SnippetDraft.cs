using System;
using System.Collections.Generic;
using System.Linq;
using InputPlus.Core.Settings;

namespace InputPlus;

/// <summary>
/// 任务 A（剪贴板 → 别名）的自检：起触发词、补前缀、查重、截断。
///
/// 这一组盯的是"点完什么都没发生"与"存进去却永远触发不了"：
///   · 触发词没补前缀 → 列表里显示正常、打字不认；
///   · 撞名没后缀 → 保存被别处覆盖；
///   · 超长没截断 → 配置里塞进几十万字，读盘就开始卡。
///
/// 用例按施工单 §6.1 逐条落地。
/// </summary>
public static partial class SelfTest
{
    private static readonly IReadOnlyList<string> SemiPrefixes = new List<string> { ";" };

    private static IEnumerable<Snippet> NoSnippets() => Array.Empty<Snippet>();

    private static IEnumerable<Snippet> Existing(params string[] triggers)
        => triggers.Select(t => new Snippet { Trigger = t, Text = "x" }).ToList();

    private static void TestSnippetDraft()
    {
        /* ① 英文：取第一个 ASCII 单词 */
        CheckEqual("草稿：英文取第一个词", SnippetDraft.SuggestWord("hello world"), "hello");
        CheckEqual("草稿：英文触发词补前缀", SnippetDraft.SuggestTrigger("hello world", SemiPrefixes, NoSnippets()), ";hello");

        /* ② 中文：没有 ASCII 单词时用汉字 */
        CheckEqual("草稿：中文内容直接用汉字", SnippetDraft.SuggestTrigger("你好世界", SemiPrefixes, NoSnippets()), ";你好世界");

        /* ③ 纯符号：当不了触发词，落到兜底词 */
        CheckEqual("草稿：纯符号落到 clip", SnippetDraft.SuggestTrigger("@#$%", SemiPrefixes, NoSnippets()), ";clip");

        /* ④ 已经是裸词 */
        CheckEqual("草稿：裸词补前缀", SnippetDraft.SuggestTrigger("mail", SemiPrefixes, NoSnippets()), ";mail");

        /* ⑤ 已经带前缀：不重复补 */
        CheckEqual("草稿：已带前缀不重复补", SnippetDraft.SuggestTrigger(";mail", SemiPrefixes, NoSnippets()), ";mail");

        /* ⑥ 多前缀：认列表里的任意一个，原样保留 */
        var multi = new List<string> { "、", ";" };
        CheckEqual("草稿：多前缀里已有的那个原样保留",
            SnippetDraft.SuggestTrigger("、mail", multi, NoSnippets()), "、mail");

        /* ⑦ 撞名：自动加 2 */
        CheckEqual("草稿：撞名自动加后缀", SnippetDraft.SuggestTrigger("mail", SemiPrefixes, Existing(";mail")), ";mail2");

        /* ⑧ 连号：2 到 9 都被占了就是 10 */
        var many = new List<string> { ";mail" };
        for (int n = 1; n <= 9; n++) many.Add(";mail" + n);
        CheckEqual("草稿：连号全部占满后取 10",
            SnippetDraft.SuggestTrigger("mail", SemiPrefixes, Existing(many.ToArray())), ";mail10");

        /* ⑨ 大小写敏感：;Mail 存在时 ;mail 不算撞名 */
        CheckEqual("草稿：大小写不同不算撞名",
            SnippetDraft.SuggestTrigger("mail", SemiPrefixes, Existing(";Mail")), ";mail");

        /* ⑩ 首尾空白 */
        CheckEqual("草稿：首尾空白被去掉", SnippetDraft.SuggestTrigger("  mail  ", SemiPrefixes, NoSnippets()), ";mail");

        /* ⑪ 中间空白：触发词不许含空白 */
        CheckEqual("草稿：中间空白被去掉", SnippetDraft.SuggestTrigger("中 文", SemiPrefixes, NoSnippets()), ";中文");

        /* ⑫ 前缀列表为空：无法补出合法触发词，明确中止 */
        CheckEqual("草稿：没有前缀时返回空串",
            SnippetDraft.Normalize("mail", Array.Empty<string>(), NoSnippets()), "");
        Check("草稿：没有前缀时 Build 返回 null",
            SnippetDraft.Build("正文", "mail", Array.Empty<string>(), NoSnippets()) == null);

        /* ⑬ 超长内容截断到 32000 */
        var longText = new string('a', 40000);
        var built = SnippetDraft.Build(longText, "mail", SemiPrefixes, NoSnippets());
        CheckEqual("草稿：40000 字截断到 32000", built?.Text.Length ?? -1, SnippetDraft.MaxTextLength);
        CheckEqual("草稿：截断时触发词照旧", built?.Trigger ?? "", ";mail");

        /* ⑭ 截断点落在代理对中间：不许留下半个字符 */
        string pair = new string('a', SnippetDraft.MaxTextLength - 1) + char.ConvertFromUtf32(0x1F600);
        var builtPair = SnippetDraft.Build(pair, "smile", SemiPrefixes, NoSnippets());
        string pairText = builtPair?.Text ?? "";
        bool lone = false;
        for (int i = 0; i < pairText.Length; i++)
        {
            if (char.IsHighSurrogate(pairText[i])
                && (i + 1 >= pairText.Length || !char.IsLowSurrogate(pairText[i + 1]))) lone = true;
            if (char.IsLowSurrogate(pairText[i])
                && (i == 0 || !char.IsHighSurrogate(pairText[i - 1]))) lone = true;
        }
        Check("草稿：截断不产生孤立代理项", !lone, lone ? "出现了孤立代理项" : null);
        Check("草稿：截断后退了一个字符", pairText.Length == SnippetDraft.MaxTextLength - 1,
            pairText.Length.ToString());

        /* ⑮ 备注：首行 200 字只取 30 */
        var commentLong = SnippetDraft.Build(new string('b', 200), "note", SemiPrefixes, NoSnippets());
        CheckEqual("草稿：备注取前 30 个字", commentLong?.Comment.Length ?? -1, 30);

        /* ⑯ 备注：跳过开头的空行 */
        var commentSkip = SnippetDraft.Build("\n\n  第一行有效内容  \n第二行", "c", SemiPrefixes, NoSnippets());
        CheckEqual("草稿：备注取第一个非空行", commentSkip?.Comment ?? "", "第一行有效内容");

        /* ⑰ \0 去掉，其它字符保留 */
        var withNul = SnippetDraft.Build("a\0b\r\nc\td", "nul", SemiPrefixes, NoSnippets());
        CheckEqual("草稿：内容里的 \\0 被去掉", withNul?.Text ?? "", "ab\r\nc\td");

        /* ⑱ 全空白内容：中止 */
        Check("草稿：全空白内容 Build 返回 null",
            SnippetDraft.Build("  \r\n\t ", "blank", SemiPrefixes, NoSnippets()) == null);

        /* 附加：分组与启用状态按约定落库 */
        var grouped = SnippetDraft.Build("正文", "mail", SemiPrefixes, NoSnippets(), "工作");
        CheckEqual("草稿：分组原样带过去", grouped?.Group ?? "", "工作");
        Check("草稿：新建的别名默认启用", grouped?.Enabled == true);
        CheckEqual("草稿：备注与触发词一起产出", grouped?.Comment ?? "", "正文");

        /* 选中链路（A5）：加完别名后刷新分组时，必须强制回到「全部」，
         * 否则停在别的分组/被搜索词过滤时新条目不在列表里，选中为空 →
         * 编辑器收起 → 用户看到"点完什么都没发生"。 */
        CheckEqual<string?>("选中链路：加完别名强制回到「全部」",
            UI.Views.HotstringView.ResolveKeepGroup(true, "工作", "工作"), null);
        CheckEqual<string?>("选中链路：平时保持当前分组",
            UI.Views.HotstringView.ResolveKeepGroup(false, null, "工作"), "工作");
        CheckEqual<string?>("选中链路：显式指定的分组优先",
            UI.Views.HotstringView.ResolveKeepGroup(false, "常用", "工作"), "常用");
        CheckEqual<string?>("选中链路：「全部」用 null 表示",
            UI.Views.HotstringView.ResolveKeepGroup(false, null, null), null);
    }
}
