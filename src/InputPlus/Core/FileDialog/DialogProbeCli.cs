using System.Text;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// <c>InputPlus.exe --dialogprobe</c>：把当前所有顶层对话框的「类名 + 子控件清单 + 判型」打印出来。
///
/// M1 的验收手段就是这个命令：随便打开一个"另存为"对话框，跑一次，
/// 就能看到它被认成 FileDialog/VistaDialog/LegacyDialog/FolderDialog 中的哪一类，
/// 以及文件名框的实际类名和控件 ID —— 判型表里的规则必须和这里打出来的东西对得上。
///
/// 输出走三条路：父控制台（有的话）、数据目录 logsdialog-probe.txt。
/// 故意**不弹窗**：红线要求任何失败都不许用模态框打断用户。
/// </summary>
internal static class DialogProbeCli
{
    public static int Run()
    {
        string text;
        try
        {
            text = BuildReport();
        }
        catch (Exception ex)
        {
            text = "探测失败：" + ex.Message + Environment.NewLine + ex;
            CoreLog.Warn("[文件对话框] 探测失败：" + ex.Message);
            Write(text);
            return 3;
        }

        Write(text);
        return 0;
    }

    private static string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 文件对话框探测 ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("自身进程: " + Environment.ProcessId + "，" + Environment.OSVersion.VersionString);
        sb.AppendLine();

        var dialogs = DialogProbe.TopLevelDialogWindows();
        sb.AppendLine($"顶层 #32770 窗口: {dialogs.Count} 个");
        foreach (nint hwnd in dialogs)
        {
            sb.AppendLine();
            sb.AppendLine(DialogProbe.ClassifyAndDescribe(hwnd));
        }

        sb.AppendLine();
        var explorers = DialogProbe.TopLevelExplorerWindows();
        sb.AppendLine($"顶层资源管理器窗口: {explorers.Count} 个（本次只占位，不挂浮层）");
        foreach (nint hwnd in explorers)
        {
            sb.AppendLine();
            sb.AppendLine(DialogProbe.ClassifyAndDescribe(hwnd));
        }

        if (dialogs.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("没有发现 #32770 顶层窗口。先打开一个「另存为 / 打开文件」对话框，再跑这条命令。");
        }

        return sb.ToString().TrimEnd();
    }

    private static void Write(string text)
    {
        bool hasConsole = false;
        try
        {
            if (Interop.NativeMethods.AttachConsole(-1))
            {
                hasConsole = true;
                var stdout = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
                Console.WriteLine();
                Console.WriteLine(text);
            }
        }
        catch { /* 没有父控制台是常态（双击启动） */ }

        string path = CliOutput.ReportPath("dialog-probe.txt");
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[文件对话框] 探测报告写盘失败：" + ex.Message);
        }

        try { CoreLog.Info("[文件对话框] 探测报告已写入 " + path); } catch { }

        if (hasConsole)
        {
            try { Interop.NativeMethods.FreeConsole(); } catch { }
        }
    }
}
