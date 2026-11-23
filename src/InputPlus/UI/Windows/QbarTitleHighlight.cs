using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using InputPlus.Core.Qbar.Search;

namespace InputPlus.UI.Windows;

/// <summary>
/// 标题里的一段文字。Match = 这段是搜索命中的片段，要强调。
/// 数据层只给"区间"，界面层才决定怎么画（加粗 / 下划线 / 换色）——
/// 在数据层拼 [标记] 会把文件名变成不能直接复制、不能直接打开的字符串。
/// </summary>
/* public：展开窗口的 Row 是 public 的绑定对象，属性类型不能比它更封闭，
   否则 WPF 绑定时编译器直接报 CS0053。 */
public sealed class QbarTitleSegment
{
    public QbarTitleSegment(string text, bool match)
    {
        Text = text;
        Match = match;
    }

    public string Text { get; }

    public bool Match { get; }
}

/// <summary>
/// 附加属性：把 TitleSegments 渲染成 TextBlock 的 Inlines。
///
/// 为什么不用"Run 的 Text 绑到段列表"：TextBlock 的 Inlines 不是可绑定集合，
/// 用 ItemsControl 又拿不到单行省略号（Qbar 结果行必须一行截断）。
/// 附加属性是唯一既不丢省略号、又不用在数据层拼字符串的做法。
/// </summary>
internal static class QbarTitleHighlight
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments",
        typeof(IReadOnlyList<QbarTitleSegment>),
        typeof(QbarTitleHighlight),
        new PropertyMetadata(null, OnSegmentsChanged));

    public static void SetSegments(DependencyObject element, IReadOnlyList<QbarTitleSegment>? value)
        => element.SetValue(SegmentsProperty, value);

    public static IReadOnlyList<QbarTitleSegment>? GetSegments(DependencyObject element)
        => (IReadOnlyList<QbarTitleSegment>?)element.GetValue(SegmentsProperty);

    /// <summary>
    /// 把"名字 + 命中区间"切成段。区间是搜索引擎给的偏移量（后端会
    /// 通过 SDK 返回它自己算的精确命中位置），这里只切不拼字符串 ——
    /// 在数据层插标记会把带 &lt; &amp; 的文件名弄坏，也会让复制出来的名字变脏。
    /// </summary>
    public static IReadOnlyList<QbarTitleSegment> Segments(string? text, IReadOnlyList<QbarHighlight>? marks)
    {
        string title = text ?? "";
        if (title.Length == 0 || marks == null || marks.Count == 0)
            return new List<QbarTitleSegment>(1) { new QbarTitleSegment(title, false) };

        var segments = new List<QbarTitleSegment>(marks.Count * 2 + 1);
        int cursor = 0;
        foreach (QbarHighlight mark in marks)
        {
            /* 三件事必须在切段前挡掉，否则会切出"重复的字"或"没命中也加亮"：
               ① 起点是负的（数据坏了）→ 丢掉，别夹到 0 假装命中前两个字符；
               ② 长度 <= 0 → 丢掉；
               ③ 起点落在上一段已消费的区间里（区间重叠/乱序）→ 丢掉，
                  夹到 cursor 会把已经从名字里消费掉的字符再吐一遍。 */
            if (mark.Start < 0 || mark.Length <= 0 || mark.Start < cursor) continue;

            int start = Math.Min(mark.Start, title.Length);
            int end = Math.Min(start + mark.Length, title.Length);
            if (end <= start) continue;
            if (start > cursor)
                segments.Add(new QbarTitleSegment(title.Substring(cursor, start - cursor), false));
            segments.Add(new QbarTitleSegment(title.Substring(start, end - start), true));
            cursor = end;
        }

        if (cursor < title.Length)
            segments.Add(new QbarTitleSegment(title.Substring(cursor), false));
        if (segments.Count == 0) segments.Add(new QbarTitleSegment(title, false));
        return segments;
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;

        block.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<QbarTitleSegment> segments || segments.Count == 0) return;

        for (int i = 0; i < segments.Count; i++)
        {
            QbarTitleSegment segment = segments[i];
            var run = new Run(segment.Text);
            if (segment.Match)
            {
                /* 强调只用字重 + 下划线，不写死颜色：结果行的颜色由
                 * ApplyRowPalette 按面板明暗统一刷，这里一旦写死颜色，
                 * 浅色面板上就会出现"看不太清"的高亮。 */
                run.FontWeight = FontWeights.SemiBold;
                run.TextDecorations = TextDecorations.Underline;
            }
            block.Inlines.Add(run);
        }
    }
}
