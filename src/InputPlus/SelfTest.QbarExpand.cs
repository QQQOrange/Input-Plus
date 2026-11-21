using System;
using System.Collections.Generic;
using System.Linq;
using InputPlus.Core.Qbar.Search;
using InputPlus.UI.Windows;

namespace InputPlus;

public static partial class SelfTest
{
    /// <summary>
    /// 展开窗口的表头排序。
    ///
    /// 这段逻辑只活在窗口的代码后置里，界面上靠"点一下表头"验证，回归时看不出来；
    /// 而且最典型的错法很隐蔽 —— 拿显示字符串排序（"10 MB" 排在 "9 KB" 前面、
    /// "2026-1-9" 排在 "2026-07-08" 后面），肉眼扫一遍根本发现不了。
    /// 所以喂假数据直接把两个坑钉死。
    /// </summary>
    private static void TestQbarExpandSort()
    {
        static QbarExpandWindow.Row R(string name, string folder, long size, int day, int rank)
            => new()
            {
                Name = name,
                Folder = folder,
                SizeBytes = size,
                DayValue = day,
                Rank = rank,
            };

        // rank 0 是"最大的"，但名字排最后 —— 每个用例都要能区分"按值排"和"没排"。
        var rows = new List<QbarExpandWindow.Row>
        {
            R("a.txt", @"C:\small", 9 * 1024, 100, 0),
            R("b.txt", @"C:\big", 10L * 1024 * 1024, 300, 1),
            R("c.txt", @"C:\mid", 512 * 1024, 200, 2),
        };

        QbarExpandWindow.SortRows(rows, "size", true);
        Check("展开窗口排序：大小降序按字节值而不是显示字符串",
            string.Join(",", rows.Select(r => r.Name)) == "b.txt,c.txt,a.txt",
            string.Join(",", rows.Select(r => r.Name)));

        QbarExpandWindow.SortRows(rows, "size", false);
        Check("展开窗口排序：大小升序",
            string.Join(",", rows.Select(r => r.Name)) == "a.txt,c.txt,b.txt",
            string.Join(",", rows.Select(r => r.Name)));

        QbarExpandWindow.SortRows(rows, "date", true);
        Check("展开窗口排序：日期降序按天序号而不是格式化文本",
            string.Join(",", rows.Select(r => r.Name)) == "b.txt,c.txt,a.txt",
            string.Join(",", rows.Select(r => r.Name)));

        QbarExpandWindow.SortRows(rows, "name", false);
        Check("展开窗口排序：名称升序",
            string.Join(",", rows.Select(r => r.Name)) == "a.txt,b.txt,c.txt",
            string.Join(",", rows.Select(r => r.Name)));

        // 同值不许乱序：用户按日期排完再筛选，同一天的几条应该保持原来的相关度序。
        var ties = new List<QbarExpandWindow.Row>
        {
            R("z.txt", @"C:\x", 1, 500, 0),
            R("y.txt", @"C:\x", 1, 500, 1),
            R("x.txt", @"C:\x", 1, 500, 2),
        };
        QbarExpandWindow.SortRows(ties, "date", true);
        Check("展开窗口排序：同值时保持原相关度序",
            string.Join(",", ties.Select(r => r.Name)) == "z.txt,y.txt,x.txt",
            string.Join(",", ties.Select(r => r.Name)));

        // 回到 rank：不能把 _all 的顺序搞乱，否则"清除排序"之后列表就变了。
        var back = new List<QbarExpandWindow.Row>
        {
            R("second.txt", @"C:\x", 3, 10, 1),
            R("first.txt", @"C:\x", 2, 20, 0),
        };
        QbarExpandWindow.SortRows(back, "rank", false);
        Check("展开窗口排序：rank 不做任何改动",
            back[0].Name == "second.txt" && back[1].Name == "first.txt",
            string.Join(",", back.Select(r => r.Name)));

        // 目录的大小是 -1（表示"不适用"），升序时不许因此跑到最前面冒充最小的文件。
        var dirs = new List<QbarExpandWindow.Row>
        {
            R("folder", @"C:\x", -1, 100, 0),
            R("file.txt", @"C:\x", 2048, 100, 1),
        };
        QbarExpandWindow.SortRows(dirs, "size", true);
        Check("展开窗口排序：目录按 -1 排在小文件之后（降序）",
            dirs[0].Name == "file.txt",
            string.Join(",", dirs.Select(r => r.Name)));
    }

    /// <summary>
    /// 高亮切段。窗口里"为什么这一条命中"全靠它 —— 段切错的表现是：
    /// 要么整行都没加亮（区间越界被全裁），要么文件名里冒出一截重复文字。
    /// </summary>
    private static void TestQbarTitleSegments()
    {
        static string Render(IReadOnlyList<QbarTitleSegment> segments)
            => string.Join("|", segments.Select(s => (s.Match ? "*" : "") + s.Text));

        IReadOnlyList<QbarTitleSegment> plain = QbarTitleHighlight.Segments("Report.dll", null);
        Check("高亮切段：没有区间时整段普通",
            Render(plain) == "Report.dll", Render(plain));

        var marks = new List<QbarHighlight> { new(0, 6) };
        Check("高亮切段：前缀命中切成 命中+其余",
            Render(QbarTitleHighlight.Segments("Report.dll", marks)) == "*Report|.dll",
            Render(QbarTitleHighlight.Segments("Report.dll", marks)));

        var middle = new List<QbarHighlight> { new(3, 2) };
        Check("高亮切段：中段命中前后都保留",
            Render(QbarTitleHighlight.Segments("abcdef", middle)) == "abc|*de|f",
            Render(QbarTitleHighlight.Segments("abcdef", middle)));

        // 越界与零长度：宁可少加亮，也不能抛异常或把名字拼坏。
        var broken = new List<QbarHighlight> { new(5, 100), new(-3, 2), new(2, 0) };
        Check("高亮切段：越界/负数/零长区间被裁掉且不抛异常",
            Render(QbarTitleHighlight.Segments("abc", broken)) == "abc",
            Render(QbarTitleHighlight.Segments("abc", broken)));

        var overlap = new List<QbarHighlight> { new(1, 3), new(2, 2) };
        Check("高亮切段：重叠区间不重复出字",
            Render(QbarTitleHighlight.Segments("abcdef", overlap)) == "a|*bcd|ef",
            Render(QbarTitleHighlight.Segments("abcdef", overlap)));
    }
}
