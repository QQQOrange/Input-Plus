using System;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;

namespace InputPlus.Core.Qbar;

/// <summary><see cref="PinnedFiles.Upsert"/> 的结果。</summary>
/// <param name="Added">true = 新增了一条；false = 已经收藏过（次数已 +1）。</param>
/// <param name="Entry">落库/命中的那一条（调用方拿它的 Key 去做"光标定位"）。</param>
public readonly record struct PinnedUpsertResult(bool Added, QEntry Entry);

/// <summary>
/// 固定项里"按路径去重 + 需要时新增"这一小段（v1.5.1）。
///
/// 为什么从 AppHost 里抽出来：这是**幂等**的全部逻辑 ——
/// 用户连点两下、或者从两个程序里各收藏一次同一个文件，都只该有一条记录。
/// 抽成纯函数（只碰传进来的列表）之后，自检可以直接喂它两次，
/// 不必真的建一个 AppHost、也不必写盘。
/// </summary>
public static class PinnedFiles
{
    /// <summary>去重键：小写 + 去尾斜杠 + 剥 <c>\\?\</c>（与收藏、打开位置共用同一口径）。</summary>
    public static string CompareKey(string? path) => RevealPath.CompareKey(path);

    /// <summary>按路径找固定项；不在返回 null。</summary>
    public static QEntry? Find(IEnumerable<QEntry>? pinned, string? path)
    {
        string key = CompareKey(path);
        if (key.Length == 0 || pinned == null) return null;

        foreach (QEntry entry in pinned)
        {
            if (entry == null) continue;
            if (string.Equals(CompareKey(entry.Value), key, StringComparison.Ordinal)) return entry;
        }
        return null;
    }

    /// <summary>
    /// 把"前台程序打开的这个文件"写进固定项：已存在就只加次数，不存在才新增。
    ///
    /// 这里**不动排序**：新增插在最前面（和星标收藏一致），已存在的原地不动 ——
    /// 用户拖过的固定项顺序不该被一次"再收藏"打乱。
    /// </summary>
    /// <param name="takenKeywords">已经在用的关键字（四张表一起看），用来算不撞车的那个。</param>
    public static PinnedUpsertResult Upsert(
        List<QEntry> pinned,
        ForegroundFile file,
        IEnumerable<string>? takenKeywords,
        bool showInMiddleMenu = true)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(file);

        string path = RevealPath.Clean(file.Path);
        string fileName = LeafOf(path);
        if (fileName.Length == 0) fileName = file.FileName;

        QEntry? existing = Find(pinned, path);
        if (existing != null)
        {
            existing.UseCount++;
            existing.LastUsed = DateTime.Now;
            if (string.IsNullOrWhiteSpace(existing.Source)) existing.Source = file.ProcessName;
            return new PinnedUpsertResult(false, existing);
        }

        var entry = new QEntry
        {
            Key = FavoriteKeywords.KeywordForUnique(fileName, takenKeywords),
            Value = path,                 // 固定项存的就是完整路径（去重键）
            Comment = fileName,           // 备注 = 文件名（含扩展名，与资源管理器显示一致）
            Source = file.ProcessName,
            UseCount = 1,
            LastUsed = DateTime.Now,
            ShowInMiddleMenu = showInMiddleMenu,
        };
        pinned.Insert(0, entry);
        return new PinnedUpsertResult(true, entry);
    }

    private static string LeafOf(string path)
    {
        try { return Path.GetFileName(path) ?? ""; } catch { return ""; }
    }
}
