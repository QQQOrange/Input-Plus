using System;
using System.Diagnostics;
using System.IO;

namespace InputPlus.Core.FileDialog;

/// <summary>在资源管理器里定位一个文件/文件夹。回填失败或用户按 Alt+Enter 时用。</summary>
internal static class DialogReveal
{
    public static bool TryReveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return true;
            }
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return true;
            }
            // 文件已被删/改名：打开它所在的目录（还找得到的话）
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[文件对话框] 在资源管理器里定位失败：" + ex.Message);
        }
        return false;
    }
}
