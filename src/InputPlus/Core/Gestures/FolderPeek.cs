using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 把一个文件夹里的条目按"文件夹在前、名字排序"分页取出来 —— 附加菜单里悬停固定文件夹时展开用。
///
/// 为什么分页而不是一次列完：目录可能有上千条，一次建一千个菜单行既慢又没法看。
/// 先给一页（默认 10 条，见 <see cref="PageSizeFor"/>），鼠标滚到底再取下一页，
/// 于是"先显示 10 个、剩下的滚出来"是自然结果，而不是硬截断。
///
/// 三条纪律：
///   · 隐藏/系统条目（desktop.ini、Thumbs.db）不进菜单；
///   · 总条数封顶 <see cref="MaxTotal"/>，防止一个几十万文件的目录把菜单拖死；
///   · 任何 IO 异常都当"空文件夹"，绝不让菜单因为一个目录没权限就弹不出来。
/// </summary>
internal static class FolderPeek
{
    /// <summary>一条候选：路径 + 显示名 + 是不是文件夹。</summary>
    internal sealed record Entry(string Path, string Name, bool IsFolder);

    /// <summary>一个目录最多列这么多条（再多的部分不列，滚也滚不完）。</summary>
    internal const int MaxTotal = 500;

    /// <summary>默认每页条数（调用方一般用设置里的「搜索前建议」条数覆盖）。</summary>
    internal const int DefaultPageSize = 10;

    /// <summary>每页条数上下限：太小滚起来累，太大又不叫"先显示一点"。</summary>
    internal static int PageSizeFor(int configured)
        => Math.Clamp(configured <= 0 ? DefaultPageSize : configured, 10, 50);

    /// <summary>
    /// 取第 <paramref name="offset"/> 条起的 <paramref name="count"/> 条；<paramref name="hasMore"/> 表示后面还有。
    ///
    /// 每次调用都重新枚举 + 排序：目录内容会变（用户刚往里存了东西），缓存反而会骗人。
    /// 条目数量在几百这个量级，重新排一次的代价可以忽略。
    /// </summary>
    internal static IReadOnlyList<Entry> Page(string folder, int offset, int count, out bool hasMore)
    {
        hasMore = false;
        if (string.IsNullOrWhiteSpace(folder) || count <= 0 || offset < 0) return Array.Empty<Entry>();

        List<Entry> all;
        try { all = All(folder); }
        catch { return Array.Empty<Entry>(); }

        if (all.Count > MaxTotal) all = all.Take(MaxTotal).ToList();
        if (offset >= all.Count) return Array.Empty<Entry>();

        int take = Math.Min(count, all.Count - offset);
        hasMore = offset + take < all.Count;
        return all.GetRange(offset, take);
    }

    /// <summary>整目录（已按"文件夹在前、名字排序"排好、已过滤隐藏项）。</summary>
    internal static List<Entry> All(string folder)
    {
        var dirs = new List<Entry>();
        var files = new List<Entry>();

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        foreach (string path in Directory.EnumerateFileSystemEntries(folder, "*", options))
        {
            try
            {
                FileAttributes attr = File.GetAttributes(path);
                bool isDir = (attr & FileAttributes.Directory) != 0;

                string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (name.Length == 0) continue;

                var entry = new Entry(path, name, isDir);
                (isDir ? dirs : files).Add(entry);
            }
            catch
            {
                /* 单条读不到就跳过，不影响整页 */
            }
        }

        dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var all = new List<Entry>(dirs.Count + files.Count);
        all.AddRange(dirs);
        all.AddRange(files);
        return all;
    }
}
