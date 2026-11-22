using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Suggestions;

/// <summary>
/// `--suggestions-probe`：真机跑一遍建议采集并把结果写进日志目录。
///
/// 存在意义：建议的验收指标（首屏 ≤80ms、各来源条数、失败来源、2000 条上限）
/// 在自检里造不出来 —— 它依赖这台机器上真实的下载目录、最近记录与对话框历史。
/// 探针跑两次就能验"首屏走缓存"：第一次冷启动采集并落缓存，第二次 Start() 之后
/// FirstScreen 立刻有数据。
/// </summary>
internal static class SuggestionsProbeCli
{
    private const int WarmTimeoutMs = 8000;

    public static int Run()
    {
        try { InputPlus.Core.Interop.NativeMethods.AttachConsole(-1); } catch { }

        var report = new StringBuilder();
        int exit = 0;
        var settings = new AppSettings();

        try
        {
            using var service = new SuggestionsService(() => settings);
            var clock = Stopwatch.StartNew();
            service.Start();
            long startMs = clock.ElapsedMilliseconds;

            // 首屏：Start() 返回后立刻取（缓存命中时应该是 0ms 级）
            var firstClock = Stopwatch.StartNew();
            IReadOnlyList<RecItem> first = service.FirstScreen(20);
            double firstMs = firstClock.Elapsed.TotalMilliseconds;

            int waited = 0;
            while (!service.IsWarm && waited < WarmTimeoutMs)
            {
                Thread.Sleep(50);
                waited += 50;
            }
            long warmMs = clock.ElapsedMilliseconds;
            if (!service.IsWarm) exit = 2;

            RecSnapshot snapshot = service.Snapshot;
            report.AppendLine("InputPlus 搜索前建议探针");
            report.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("数据目录：" + AppPaths.DataDir);
            report.AppendLine("缓存文件：" + SuggestionsService.CachePath + "（存在=" + File.Exists(SuggestionsService.CachePath) + "）");
            report.AppendLine("增量配置：" + service.StorePath);
            report.AppendLine("Start() 返回：" + startMs + " ms");
            report.AppendLine("Start() 后首屏：" + firstMs.ToString("0.0") + " ms，条数=" + first.Count + "（缓存命中=" + (first.Count > 0) + "）");
            report.AppendLine("采集完成（IsWarm）：" + warmMs + " ms（等待 " + waited + " ms）");
            report.AppendLine("采集本身：" + snapshot.CollectMs + " ms，原始 " + snapshot.RawCount + " 条，合并后 " + snapshot.Items.Count + " 条");
            report.AppendLine();
            report.AppendLine("各来源：");
            foreach (var (id, count) in snapshot.SourceCounts.OrderByDescending(kv => kv.Value))
            {
                string failure = snapshot.Failures.TryGetValue(id, out string? message) ? "  [失败] " + message : "";
                report.AppendLine("  " + id.PadRight(16) + count.ToString().PadLeft(5) + failure);
            }
            if (snapshot.Failures.Count == 0) report.AppendLine("  （无失败来源）");

            /* 诊断：Windows 最近记录这一项最容易"静默为 0"（COM 解析失败被 catch 吞掉），
               抽样几个 .lnk 把解析结果摊开写进报告，别让 0 条看起来像"用户没最近文件"。 */
            report.AppendLine();
            report.AppendLine("最近记录解析抽样：");
            try
            {
                string recentDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Recent");
                if (!Directory.Exists(recentDir))
                {
                    report.AppendLine("  目录不存在：" + recentDir);
                }
                else
                {
                    var links = Directory.GetFiles(recentDir, "*.lnk")
                        .OrderByDescending(File.GetLastWriteTime)
                        .Take(5)
                        .ToList();
                    report.AppendLine("  .lnk 总数=" + Directory.GetFiles(recentDir, "*.lnk").Length + "，取最近 " + links.Count + " 个：");
                    foreach (string lnk in links)
                    {
                        ShellLinkReader.TryTargetOf(lnk, out string? target, out string? error);
                        bool exists = target is { Length: > 0 } && (File.Exists(target) || Directory.Exists(target));
                        report.AppendLine("  · " + Path.GetFileName(lnk) + " → " + (target ?? "<null>")
                            + "（存在=" + exists + (error is { Length: > 0 } ? "，原因=" + error : "") + "）");
                    }
                }
            }
            catch (Exception ex) { report.AppendLine("  抽样失败：" + ex.Message); }

            report.AppendLine();
            report.AppendLine("首屏 " + snapshot.Items.Count + " 条（按修改时间倒序）：");
            int index = 0;
            foreach (RecItem item in snapshot.Items.Take(20))
            {
                index++;
                report.AppendLine("  " + index.ToString().PadLeft(2) + ". [" + item.ItemType + "] " + item.DisplayName
                    + " | " + item.Path + " | " + string.Join("·", item.Badges)
                    + " | " + item.SourceId + " | " + item.Modified.ToString("yyyy-MM-dd HH:mm"));
            }

            /* 首屏 80ms 的判据：缓存命中时 FirstScreen 必须是 0ms 级。
             * 本轮没有缓存（首次运行）时只记录，不算失败。 */
            if (first.Count > 0 && firstMs > 80) exit = 3;
        }
        catch (Exception ex)
        {
            report.AppendLine("探针异常：" + ex);
            exit = 1;
        }

        string text = report.ToString();
        try { Console.Out.Write(text); Console.Out.Flush(); } catch { }
        try
        {
            string path = CliOutput.ReportPath("suggestions-probe.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception ex) { CoreLog.Warn("写建议探针报告失败：" + ex.Message); }
        try { InputPlus.Core.Interop.NativeMethods.FreeConsole(); } catch { }
        return exit;
    }
}
