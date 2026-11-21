using System;
using System.Linq;
using System.Text;
using System.Threading;

namespace InputPlus.Core;

/// <summary>
/// `--search 关键词 [更多词 ...]`：在命令行里跑一次 Qbar 的本地文件搜索，把结果写成文本文件。
///
/// 为什么需要：Qbar 的文件搜索现在完全由 Input Plus 自己的索引提供，不再依赖任何第三方软件。
/// 真机上"搜不到刚下载的文件"时，从界面判断不出是索引没建好、根目录没覆盖到、还是关键词匹配没中；
/// 这个开关会先把索引建完再搜，把状态、规模、根目录和命中列表一起写出来，一眼就能分清。
/// </summary>
internal static class NativeSearchCli
{
    public static int Run(string[] args)
    {
        string query = string.Join(" ", args
            .SkipWhile(a => !string.Equals(a, "--search", StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))).Trim();

        if (query.Length == 0)
        {
            WriteLine("用法：InputPlus.exe --search 关键词");
            WriteLine("（可以给多个词，按「都要包含」匹配；结果同时写进 %TEMP%\\clp-search.txt）");
            return 2;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Qbar.NativeSearchIndex.Start();

        /* 第一次跑要真扫一遍常用目录：几百毫秒到半分钟都有。最多等 5 分钟，别把命令行挂死。 */
        while (Qbar.NativeSearchIndex.Building && watch.Elapsed.TotalSeconds < 300) Thread.Sleep(200);
        long waited = watch.ElapsedMilliseconds;

        var hits = Qbar.NativeSearchIndex.Search(query, 40, 15000);
        var sb = new StringBuilder();
        sb.AppendLine("=== Qbar 文件搜索（Input Plus 内置索引） ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("关键词: " + query);
        sb.AppendLine("状态: " + Qbar.NativeSearchIndex.Status);
        if (Qbar.NativeSearchIndex.Problem.Length > 0) sb.AppendLine("问题: " + Qbar.NativeSearchIndex.Problem);
        sb.AppendLine("规模: 文件 " + Qbar.NativeSearchIndex.FileCount + " 个，目录 " + Qbar.NativeSearchIndex.DirCount + " 个");
        sb.AppendLine("建立: " + (Qbar.NativeSearchIndex.BuiltAt == DateTime.MinValue
            ? "（还没有）"
            : Qbar.NativeSearchIndex.BuiltAt.ToString("yyyy-MM-dd HH:mm:ss")) + "，等待 " + waited + " ms");
        sb.AppendLine("根目录:");
        foreach (string root in Qbar.NativeSearchIndex.Roots) sb.AppendLine("  " + root);
        sb.AppendLine("命中: " + hits.Count + " 条");
        foreach (var hit in hits)
        {
            sb.AppendLine("  " + (hit.IsDir ? "[目录] " : "[文件] ") + hit.Path + "   (分 " + hit.Score + ")");
        }

        string text = sb.ToString();
        WriteLine(text);
        return hits.Count > 0 ? 0 : 3;
    }

    /// <summary>同时写控制台和 %TEMP%\\clp-search.txt：程序是 GUI 子系统，控制台经常看不到。</summary>
    private static void WriteLine(string text)
    {
        try { Console.Out.WriteLine(text); } catch { }
        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clp-search.txt");
            System.IO.File.WriteAllText(path, text, Encoding.UTF8);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "写出文件搜索结果"); }
    }
}
