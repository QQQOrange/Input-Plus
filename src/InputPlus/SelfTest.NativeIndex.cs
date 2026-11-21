using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using InputPlus.Core.Qbar;

namespace InputPlus;

public static partial class SelfTest
{
    /// <summary>
    /// 自研文件索引（Core/Qbar/NativeSearchIndex.cs）的底线。
    ///
    /// 这里不去验"扫得快不快"，只验四类会直接让用户觉得"搜索坏了"的事：
    ///   ① 精确名、子串、多词 AND、中文目录都能命中；
    ///   ② 大小写不改变结果，limit / 超时不会越界或抛异常；
    ///   ③ 返回的路径必须真实存在（路径拼接错一位，UI 点开就是"文件不存在"）；
    ///   ④ 存盘再读回不能丢项（丢一项就等于某类文件永远搜不到）。
    ///
    /// 夹具固定 197 个文件 / 18 个目录，其中 190 个是 filler —— 数量足够
    /// 触发 limit 截断，又不至于让自检本身变慢。
    /// </summary>
    private static void TestNativeFileIndex()
    {
        string root = Path.Combine(Path.GetTempPath(), "inputplus-index-test-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(Path.GetTempPath(), "inputplus-index-store-" + Guid.NewGuid().ToString("N"));

        try
        {
            BuildIndexFixture(root);

            /* ---- ① 精确名：Chrome.exe 必须排第一 ---- */
            var exact = NativeSearchIndex.SearchInForTest(root, "chrome.exe", 10);
            bool exactFirst = exact.Count > 0
                              && !exact[0].IsDir
                              && string.Equals(Path.GetFileName(exact[0].Path), "Chrome.exe",
                                  StringComparison.OrdinalIgnoreCase);
            Check("文件索引：精确名 chrome.exe 命中 Chrome.exe 且排第一", exactFirst, DescribeHits(exact));

            /* ---- ② 子串 ---- */
            var setup = NativeSearchIndex.SearchInForTest(root, "setup", 10);
            Check("文件索引：子串 setup 命中 chrome-setup.exe",
                HasHit(setup, "chrome-setup.exe", isDir: false), DescribeHits(setup));

            /* ---- ③ 多词 AND：第一个词只在目录里，第二个词在文件名里 ---- */
            /* 这条专门防"只扫第一个词"的实现：项目A 是目录，说明.md 是文件，
             * 两个词必须分别从路径和名字里凑齐才算命中。 */
            var andQuery = NativeSearchIndex.SearchInForTest(root, "项目 说明", 10);
            Check("文件索引：多词「项目 说明」按 AND 命中 说明.md",
                HasHit(andQuery, "说明.md", isDir: false), DescribeHits(andQuery));

            /* ---- ④ 中文目录 ---- */
            var chineseDir = NativeSearchIndex.SearchInForTest(root, "设计稿", 10);
            Check("文件索引：中文「设计稿」命中该目录",
                HasHit(chineseDir, "设计稿", isDir: true), DescribeHits(chineseDir));

            /* ---- ⑤ 大小写：结果条数、顺序、分数都必须一致 ---- */
            var upper = NativeSearchIndex.SearchInForTest(root, "CHROME", 10);
            var lower = NativeSearchIndex.SearchInForTest(root, "chrome", 10);
            bool sameCase = upper.Count == lower.Count;
            if (sameCase)
            {
                for (int i = 0; i < upper.Count; i++)
                {
                    if (string.Equals(upper[i].Path, lower[i].Path, StringComparison.Ordinal)
                        && upper[i].Score == lower[i].Score) continue;
                    sameCase = false;
                    break;
                }
            }
            Check("文件索引：大小写 CHROME 与 chrome 结果一致", sameCase,
                upper.Count + " 条 / " + lower.Count + " 条");

            /* ---- ⑥ 不存在的词 ---- */
            var none = NativeSearchIndex.SearchInForTest(root, "zzzz-no-such-token", 10);
            Check("文件索引：不存在的词返回 0 条", none.Count == 0, DescribeHits(none));

            /* ---- ⑦ limit 截断 ---- */
            var limited = NativeSearchIndex.SearchInForTest(root, "txt", 3);
            Check("文件索引：limit=3 时结果不超过 3 条且非空",
                limited.Count > 0 && limited.Count <= 3,
                limited.Count.ToString(CultureInfo.InvariantCulture));

            /* ---- ⑧ 超时：1ms 只能返回已有结果，不能抛 ---- */
            bool threw = false;
            IReadOnlyList<FileSearchHit>? timed = null;
            try { timed = NativeSearchIndex.SearchInForTest(root, "txt", 5, 1); }
            catch { threw = true; }
            Check("文件索引：timeoutMs=1 不抛异常且不超过 limit",
                !threw && timed != null && timed.Count <= 5,
                threw ? "抛异常" : (timed == null ? "null" : timed.Count.ToString(CultureInfo.InvariantCulture)));

            /* ---- ⑨ 存盘再读回：计数必须和夹具本身对得上 ---- */
            NativeSearchIndex.SaveLoadForTest(cache, new[] { root }, out int files, out int dirs);
            CountTreeForTest(root, out int expectedFiles, out int expectedDirs);
            Check("文件索引：存盘再读回后文件数与夹具一致", files == expectedFiles,
                files + " vs " + expectedFiles);
            Check("文件索引：存盘再读回后目录数与夹具一致", dirs == expectedDirs,
                dirs + " vs " + expectedDirs);

            /* ---- ⑩ 每条结果的路径都必须真实存在 ---- */
            /* 路径拼接是这类索引最容易出错的地方：目录少了分隔符、根目录带了尾斜杠，
             * 界面上看起来有结果，点下去却是"文件不存在"。这里逐条折回文件系统验。 */
            var probe = NativeSearchIndex.SearchInForTest(root, "txt", 20);
            bool allExist = probe.Count > 0;
            for (int i = 0; i < probe.Count; i++)
            {
                bool exists = probe[i].IsDir
                    ? Directory.Exists(probe[i].Path)
                    : File.Exists(probe[i].Path);
                if (exists) continue;
                allExist = false;
                break;
            }
            Check("文件索引：返回的每个路径都真实存在", allExist, DescribeHits(probe));
        }
        finally
        {
            DeleteFixtureTree(root);
            DeleteFixtureTree(cache);
        }
    }

    /// <summary>
    /// 造一棵固定形状的夹具树。名字里故意混了大小写、中文、多级目录和空格查询，
    /// 让上面每一条断言都打在真实的匹配路径上，而不是"碰巧命中"。
    /// </summary>
    private static void BuildIndexFixture(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "设计稿"));
        Directory.CreateDirectory(Path.Combine(root, "项目A", "文档"));
        Directory.CreateDirectory(Path.Combine(root, "a", "b", "c"));

        WriteFixture(root, "Chrome.exe", "MZ");
        WriteFixture(root, "chrome-setup.exe", "MZ");
        WriteFixture(root, "notes.txt", "notes");
        WriteFixture(root, "报告-2026.docx", "docx");
        WriteFixture(Path.Combine(root, "设计稿"), "首页.png", "png");
        WriteFixture(Path.Combine(root, "项目A", "文档"), "说明.md", "# 说明");
        WriteFixture(Path.Combine(root, "a", "b", "c"), "DeepSeekHarness.log", "log");

        /* 190 个 filler：保证 "txt" 的结果数远超 limit，能真正走到截断分支。 */
        for (int i = 0; i < 190; i++)
        {
            string dir = Path.Combine(root, "批量", (i % 10).ToString("00", CultureInfo.InvariantCulture));
            WriteFixture(dir, "filler-" + i.ToString("000", CultureInfo.InvariantCulture) + ".txt", "x");
        }
    }

    private static void WriteFixture(string dir, string name, string text)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text, Encoding.UTF8);
    }

    private static bool HasHit(IReadOnlyList<FileSearchHit> hits, string name, bool isDir)
    {
        for (int i = 0; i < hits.Count; i++)
        {
            if (hits[i].IsDir != isDir) continue;
            if (string.Equals(Path.GetFileName(hits[i].Path), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string DescribeHits(IReadOnlyList<FileSearchHit> hits)
    {
        if (hits.Count == 0) return "0 条";

        var text = new StringBuilder();
        int count = Math.Min(4, hits.Count);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) text.Append(" | ");
            text.Append(hits[i].Path).Append(" (").Append(hits[i].Score).Append(')');
        }
        if (hits.Count > count) text.Append(" | …");
        return text.ToString();
    }

    /// <summary>
    /// 数夹具树的文件/目录数。故意不用 Directory.GetFiles(..., AllDirectories)：
    /// 那个 API 会一次性分配整棵树的 string[]，生产代码里已经禁用了，自检也不留这个口子。
    /// </summary>
    private static void CountTreeForTest(string root, out int files, out int dirs)
    {
        files = 0;
        dirs = 1;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    FileAttributes attrs;
                    try { attrs = File.GetAttributes(entry); } catch { continue; }
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        dirs++;
                        stack.Push(entry);
                    }
                    else
                    {
                        files++;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void DeleteFixtureTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { /* 自检清理失败不改变结论；临时目录留给系统清理 */ }
    }
}
