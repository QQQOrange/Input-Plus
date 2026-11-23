using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 文件索引要覆盖哪些根目录。
///
/// 为什么单独一份：这是「索引里到底有什么」的唯一来源，启动全量建索引、
/// 增量重扫、以及拒绝访问时的降级判断全都读它。散落在索引实现里的时候，
/// 改一处忘一处就会出「启动扫了盘根、增量只扫用户目录」这类不一致。
///
/// 规则（与改造前逐条一致，不许"顺手优化"）：
///   · 用户目录、被重定向的桌面/文档/下载/图片/视频、Public、Program Files(x86)、ProgramData；
///   · 只有提权时才加盘根 —— 普通权限扫 C:\ 会被大量拒绝访问，又慢又搜不到东西；
///   · 某个根已经包含另一个根时丢掉被包含的那个，否则同一批文件会被扫两遍、
///     结果里也会出现两条一模一样的路径。
/// </summary>
internal static class QbarRoots
{
    /// <summary>
    /// 任何层级都跳过的目录名。按名字**整段**比较，不做子串匹配 ——
    /// "bin" 要跳，但 "Robin" 是用户的正常文件夹，不能连坐。
    /// </summary>
    internal static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin",
        "System Volume Information",
        "WinSxS",
        "servicing",
        "node_modules",
        ".git",
        ".vs",
        "obj",
        "bin",
    };

    /// <summary>是否该跳过这一层目录。空名字（盘根）不跳。</summary>
    internal static bool Skip(string dirName) => SkipDirNames.Contains(dirName);

    internal static string[] Compute()
    {
        var candidates = new List<string>(16);

        try
        {
            string profile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "";
            Add(candidates, profile);

            /* OneDrive 会把桌面/文档/图片重定向到 %USERPROFILE% 之外。
             * 只有在确实被重定向时才加：否则它们本来就在用户目录里，
             * 再加一遍只是把同一批文件扫两次。下载没有对应的 SpecialFolder，
             * 用 %USERPROFILE%\Downloads 兜底。 */
            AddRedirected(candidates, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), profile);
            AddRedirected(candidates, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), profile);
            if (profile.Length > 0)
                AddRedirected(candidates, Path.Combine(profile, "Downloads"), profile);
            AddRedirected(candidates, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), profile);
            AddRedirected(candidates, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), profile);

            string? publicDir = Environment.GetEnvironmentVariable("PUBLIC");
            if (string.IsNullOrWhiteSpace(publicDir) && profile.Length > 0)
            {
                string? parent = Path.GetDirectoryName(profile);
                if (!string.IsNullOrEmpty(parent)) publicDir = Path.Combine(parent, "Public");
            }
            Add(candidates, publicDir);

            Add(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            Add(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            Add(candidates, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

            /* 固定磁盘的盘根**一律**加：改造前走进程外索引时，它能覆盖整块盘
             * （用户搜 D:\某处\某文件 是能搜到的）。索引搬进本进程后如果只扫用户目录，
             * 那些盘上的文件会突然"搜不到了" —— 这不是性能问题，是覆盖面回归。
             * 普通权限下盘根里的系统目录会被拒绝访问，枚举时直接跳过（代价很小），
             * 但用户自己的数据目录一个都不会漏。 */
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                        Add(candidates, drive.RootDirectory.FullName);
                    }
                    catch { /* 盘刚好被拔掉 / 没就绪，跳过这一块 */ }
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "枚举固定磁盘"); }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "计算文件索引根目录");
        }

        var result = new List<string>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            string candidate = candidates[i];
            bool covered = false;
            for (int j = 0; j < result.Count; j++)
            {
                if (!IsSameOrUnder(candidate, result[j])) continue;
                covered = true;
                break;
            }
            if (covered) continue;

            for (int j = result.Count - 1; j >= 0; j--)
            {
                if (IsSameOrUnder(result[j], candidate)) result.RemoveAt(j);
            }
            result.Add(candidate);
        }

        return result.ToArray();
    }

    private static void AddRedirected(List<string> list, string? path, string profile)
    {
        string? root = Normalize(path);
        if (root == null) return;
        if (profile.Length > 0 && IsSameOrUnder(root, profile)) return;
        Add(list, root);
    }

    internal static void Add(List<string> list, string? path)
    {
        string? root = Normalize(path);
        if (root == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], root, StringComparison.OrdinalIgnoreCase)) return;
        }
        list.Add(root);
    }

    /// <summary>把路径规范成绝对路径并去掉末尾分隔符（盘根 "C:\" 例外）。不存在就返回 null。</summary>
    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) return null;
            if (full.Length > 3)
            {
                char last = full[full.Length - 1];
                if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
                    full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return full.Length == 0 ? null : full;
        }
        catch { return null; }
    }

    /// <summary>path 是否等于 root 或位于 root 之下。按完整路径段判断，避免 C:\foo 命中 C:\foobar。</summary>
    internal static bool IsSameOrUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;
        if (root.Length == 0) return false;

        char last = root[root.Length - 1];
        if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);

        return path.Length > root.Length
               && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && (path[root.Length] == Path.DirectorySeparatorChar
                   || path[root.Length] == Path.AltDirectorySeparatorChar);
    }

    internal static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "判断管理员权限");
            return false;
        }
    }
}
