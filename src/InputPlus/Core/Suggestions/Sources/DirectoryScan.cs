using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InputPlus.Core.Suggestions.Sources;

/// <summary>
/// 目录枚举的公共实现。所有"从一个文件夹里挑最近文件"的来源都走这里，
/// 保证枚举深度、条数上限、异常处理的行为一致。
///
/// 硬规则：只在后台采集线程调用；单个目录枚举最多看 <see cref="MaxProbeEntries"/> 个条目，
/// 再多就按名字取前若干个 —— 目的是不让某个十万文件的目录拖慢唤出。
/// </summary>
internal static class DirectoryScan
{
    public const int MaxProbeEntries = 3000;

    public static bool DirExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    public static bool FileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path); }
        catch { return false; }
    }

    public static DateTime SafeModified(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// 展开 "<c>...\WeChat Files\*\FileStorage\File</c>" 这种带一段通配的路径。
    ///
    /// 支持**任意一段**的单层 '*'（不一定是最后一段）：微信/企业微信/飞书的账号目录
    /// 后面还跟着 `\msg\file`、`\FileStorage\File` 这些固定尾巴。
    /// 不支持递归通配，也不支持多个 '*'：那会退化成全盘搜索，正是红线 4 禁止的。
    ///
    /// 以前这里把 '*' 之后的所有段一起当成"搜索掩码"传给 <c>EnumerateDirectories</c>，
    /// 掩码里带反斜杠是非法模式 —— 整个方法直接抛异常被吞掉，返回空表。
    /// 结果就是"微信 / 企业微信 / 飞书三个来源永远是 0 条"（真实账号目录明明在）。
    /// </summary>
    public static IReadOnlyList<string> Expand(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return Array.Empty<string>();
        try
        {
            string full = Environment.ExpandEnvironmentVariables(pattern);
            int star = full.IndexOf('*');
            if (star < 0)
                return DirExists(full) ? new[] { full } : Array.Empty<string>();

            // '*' 只能有一个
            if (full.IndexOf('*', star + 1) >= 0) return Array.Empty<string>();

            int slash = full.LastIndexOf('\\', star);
            if (slash <= 0) return Array.Empty<string>();

            string parent = full[..slash];

            /* 掩码只取 '*' 所在的那一段；它后面的固定段原样拼回每个结果。 */
            int nextSlash = full.IndexOf('\\', star);
            string mask = nextSlash < 0 ? full[(slash + 1)..] : full[(slash + 1)..nextSlash];
            string tail = nextSlash < 0 ? "" : full[nextSlash..];

            if (!DirExists(parent)) return Array.Empty<string>();

            var list = new List<string>();
            foreach (string dir in Directory.EnumerateDirectories(parent, mask, SearchOption.TopDirectoryOnly)
                         .Take(16))
            {
                string candidate = tail.Length == 0 ? dir : dir + tail;
                if (DirExists(candidate)) list.Add(candidate);
            }
            return list;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 把 <paramref name="directory"/> 里最近修改的条目塞进 <paramref name="sink"/>。
    /// </summary>
    /// <param name="depth">1 = 只看本层；2 = 再看一层子目录。</param>
    /// <param name="take">本目录最多贡献多少条。</param>
    public static int Newest(string directory, int depth, int take, List<RecItem> sink,
        string sourceId, SourceKind kind, int weight, string badge,
        SuggestionBudget budget, bool includeFiles = true, bool includeFolders = true)
    {
        if (!DirExists(directory) || take <= 0) return 0;

        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = depth > 1,
                MaxRecursionDepth = Math.Max(0, depth - 1),
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };

            var infos = new List<FileSystemInfo>(256);
            int seen = 0;
            foreach (FileSystemInfo info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
            {
                if (++seen > MaxProbeEntries) break;
                if (budget.Expired) break;

                bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                if (isDir ? !includeFolders : !includeFiles) continue;
                infos.Add(info);
                if (infos.Count >= MaxProbeEntries) break;
            }

            int added = 0;
            foreach (FileSystemInfo info in infos
                         .OrderByDescending(i => SafeModified(i.FullName))
                         .Take(take))
            {
                sink.Add(new RecItem
                {
                    ItemType = (info.Attributes & FileAttributes.Directory) != 0 ? RecItemType.Folder : RecItemType.File,
                    SourceId = sourceId,
                    SourceKind = kind,
                    SourceWeight = weight,
                    Path = info.FullName,
                    Name = info.Name,
                    Modified = SafeModified(info.FullName),
                    Badges = new[] { badge },
                    MergedSources = new[] { sourceId },
                });
                added++;
            }
            return added;
        }
        catch
        {
            return 0;
        }
    }
}
