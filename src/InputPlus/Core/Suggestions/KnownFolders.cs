using System;
using System.IO;
using Microsoft.Win32;

namespace InputPlus.Core.Suggestions;

/// <summary>
/// 已知文件夹解析。
///
/// 为什么不只用 Environment.GetFolderPath：Downloads 不是 SpecialFolder，
/// 而"下载"被挪到 D 盘是极常见的做法（本机就是 D:\Downloads）。
/// 只认 %UserProfile%\Downloads 会让下载来源恒为 0 —— 建议列表看起来"没坏"，
/// 实际少了一整个来源。这里按资源管理器自己的记录解析：
/// HKCU\...\User Shell Folders\{374DE290-...}（REG_EXPAND_SZ，可能带 %USERPROFILE%），
/// 解析不出来才退回默认路径。只读注册表，不写。
/// </summary>
internal static class KnownFolders
{
    /// <summary>Downloads 的已知文件夹 GUID（FOLDERID_Downloads）。</summary>
    private const string DownloadsGuid = "{374DE290-123F-4565-9164-39C4925E467B}";

    private const string UserShellFolders =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    public static string Downloads()
    {
        string? redirected = Read(DownloadsGuid);
        if (!string.IsNullOrWhiteSpace(redirected))
        {
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(redirected);
                if (expanded.Length > 0) return expanded;
            }
            catch
            {
                // 展开失败就退回默认路径
            }
        }

        string profile = SafeProfile();
        return profile.Length > 0 ? Path.Combine(profile, "Downloads") : "";
    }

    private static string SafeProfile()
    {
        try
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch
        {
            return "";
        }
    }

    private static string? Read(string name)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UserShellFolders, writable: false);
            if (key?.GetValue(name) is string value && value.Length > 0) return value;
        }
        catch
        {
            // 注册表读不到不是错误：退回默认路径即可
        }
        return null;
    }
}
