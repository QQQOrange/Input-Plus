using System;
using System.Linq;
using System.Text;

namespace InputPlus.Core;

/// <summary>
/// `--search 关键词 [更多词 ...]`：在命令行里跑一次 Qbar 的文件搜索，把结果写成文本文件。
///
/// 为什么需要：真机上"搜不到刚下载的文件"时，从界面判断不出是索引没建好、根目录没覆盖到、
/// 还是关键词匹配没中；这个开关会先把索引建完再搜，把状态、规模、根目录和命中列表一起
/// 写出来，一眼就能分清。
///
/// 走的是语义层（<see cref="Qbar.Search.QbarSearchEngine"/>）而不是某个后端的私有入口 ——
/// 界面上看到的顺序、档位、高亮就是这里看到的，不会出现"命令行能搜到、界面搜不到"。
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
        Qbar.QbarIndex.Start();

        /* 第一次跑要真扫一遍常用目录：几秒到半分钟都有。最多等 5 分钟，别把命令行挂死。 */
        while (Qbar.QbarIndex.Building && watch.Elapsed.TotalSeconds < 300) System.Threading.Thread.Sleep(200);
        long waited = watch.ElapsedMilliseconds;

        var context = Qbar.Search.QbarSearchEngine.FromSettings(Settings.SettingsStore.Load());
        Qbar.Search.QbarSearchOutcome outcome = Qbar.Search.QbarSearchEngine.Run(context, query, 40);

        var sb = new StringBuilder();
        sb.AppendLine("=== Qbar 文件搜索（Input Plus 进程内索引） ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("关键词: " + query);
        sb.AppendLine("状态: " + Qbar.QbarIndex.Status);
        if (Qbar.QbarIndex.Problem.Length > 0) sb.AppendLine("问题: " + Qbar.QbarIndex.Problem);
        sb.AppendLine("规模: 文件 " + Qbar.QbarIndex.FileCount + " 个，目录 " + Qbar.QbarIndex.DirCount + " 个");
        sb.AppendLine("建立: " + (Qbar.QbarIndex.BuiltAt == DateTime.MinValue
            ? "（还没有）"
            : Qbar.QbarIndex.BuiltAt.ToString("yyyy-MM-dd HH:mm:ss")) + "，等待 " + waited + " ms");
        sb.AppendLine("根目录:");
        foreach (string root in Qbar.QbarIndex.Roots) sb.AppendLine("  " + root);
        sb.AppendLine("后端: " + outcome.BackendText);
        sb.AppendLine("命中: " + outcome.Hits.Count + " 条（候选 " + outcome.RawCount + " 条）");
        foreach (Qbar.Search.QbarSearchHit hit in outcome.Hits)
        {
            sb.AppendLine("  " + (hit.IsDir ? "[目录] " : "[文件] ") + hit.Path
                          + "   (分 " + hit.Score + " / 档 " + hit.Tier + ")");
        }
        sb.AppendLine(outcome.TimingText);

        WriteLine(sb.ToString());
        return outcome.Hits.Count > 0 ? 0 : 3;
    }

    /// <summary>同时写控制台和 %TEMP%\clp-search.txt：程序是 GUI 子系统，控制台经常看不到。</summary>
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
