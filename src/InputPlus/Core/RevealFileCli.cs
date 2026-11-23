using System;
using System.Text;
using System.Threading;
using InputPlus.Core.Windows;

namespace InputPlus.Core;

/// <summary>
/// `--reveal-file &lt;路径&gt;`：把「打开文件所在位置」这条链路单独跑一次，
/// 结果写进 <c>%TEMP%\clp-reveal-file.txt</c>，退出码 0 = 请求确实交给了 shell。
///
/// 与 <c>--reveal</c> 的区别：那个是"按关联打开这个文件/网址"，这个**只**打开它所在的
/// 文件夹并（默认）选中它 —— 两条链路完全不同的语义，混在一个开关里最容易测错对象。
///
/// 会顺便报告"打开后前台是谁"：资源管理器没被置前时，用户看到的就是"点了没反应"，
/// 而这条只有真开一次才知道（和 <c>--reveal</c> 同一个理由）。
/// </summary>
internal static class RevealFileCli
{
    internal static int Run(string path)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        RevealTarget plan = RevealPath.Plan(path, openFolderOnly: false);

        var sb = new StringBuilder();
        sb.AppendLine("Input Plus · 打开文件所在位置探针");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("提权: " + (App.ElevationState.Elevated ? "是（管理员）" : "否（普通权限）"));
        sb.AppendLine("输入: " + path);
        sb.AppendLine("清洗后: " + plan.Path);
        sb.AppendLine("目录: " + plan.Folder + (plan.WentUp ? "（文件已不在，上溯）" : ""));
        sb.AppendLine("可达: " + (plan.Reachable ? "是" : "否"));
        sb.AppendLine("选中: " + (plan.Select ? "是" : "否"));
        sb.AppendLine("参数: " + RevealPath.ExplorerArguments(plan));

        var before = WindowService.ForegroundInfo();
        sb.AppendLine("打开前前台: " + Describe(before));

        WindowService.RevealOutcome outcome = WindowService.RevealInForeground(path, openFolderOnly: false);
        sb.AppendLine("通道: " + (outcome.Channel.Length > 0 ? outcome.Channel : "(无)"));
        sb.AppendLine("结果: " + (outcome.Launched ? "已交给 shell" : "失败") +
                      (outcome.Why.Length > 0 ? "  " + outcome.Why : ""));

        /* 给 explorer 一点时间把窗口开出来并置前，再看一眼前台。 */
        Thread.Sleep(2500);
        var after = WindowService.ForegroundInfo();
        sb.AppendLine("打开后前台: " + Describe(after));
        bool cabinet = after != null && string.Equals(after.ClassName, "CabinetWClass", StringComparison.OrdinalIgnoreCase);
        sb.AppendLine("资源管理器在前台: " + (cabinet ? "是" : "否"));
        if (!cabinet)
        {
            sb.AppendLine("提示：explorer 可能复用了别的窗口，或前台锁定拒绝了置前（不影响功能，只是没跳到眼前）。");
        }

        watch.Stop();
        sb.AppendLine("耗时: " + watch.ElapsedMilliseconds + " ms");

        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clp-reveal-file.txt");
        try { System.IO.File.WriteAllText(file, sb.ToString(), Encoding.UTF8); }
        catch (Exception ex) { CoreLog.Warn("[打开位置] 写探针结果失败：" + ex.Message); }

        try
        {
            Interop.NativeMethods.AttachConsole(-1);
            Console.WriteLine(sb.ToString());
        }
        catch { /* 没有父控制台：结果已经写进文件 */ }

        CoreLog.Info("[打开位置] 探针：" + (outcome.Launched ? "OK " + outcome.Channel : "失败 " + outcome.Why));
        return outcome.Launched ? 0 : 1;
    }

    private static string Describe(WindowInfo? info)
        => info == null ? "(无)" : $"{info.Title} [{info.ClassName}] {info.ExeName}";
}
