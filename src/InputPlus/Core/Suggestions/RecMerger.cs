using System;
using System.Collections.Generic;
using System.Linq;

namespace InputPlus.Core.Suggestions;

/// <summary>一次采集的产物。Presenter 只认这个对象，不认识来源。</summary>
public sealed class RecSnapshot
{
    public static readonly RecSnapshot Empty = new()
    {
        Items = Array.Empty<RecItem>(),
        CollectedAt = DateTime.MinValue,
    };

    public IReadOnlyList<RecItem> Items { get; init; } = Array.Empty<RecItem>();

    /// <summary>每个来源的原始条数，排错与自检用。</summary>
    public IReadOnlyDictionary<string, int> SourceCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>失败的来源（id → 错误）。红线 2：失败只是该来源为空，但必须日志可见。</summary>
    public IReadOnlyDictionary<string, string> Failures { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int RawCount { get; init; }

    public long CollectMs { get; init; }

    public DateTime CollectedAt { get; init; } = DateTime.Now;
}

/// <summary>
/// 合并层：去重 → 规则过滤 → 排序 → 截断。
///
/// 去重按规范路径（大小写不敏感）。同一路径多来源命中时保留权重最高的一条，
/// 其余来源的徽标并进 <see cref="RecItem.Badges"/>。
///
/// 排序只按 modified 倒序，其次来源权重，再次名字长度 —— 不引入任何"猜你喜欢"。
/// </summary>
public static class RecMerger
{
    public static RecSnapshot Merge(IEnumerable<RecItem> items, RecRules rules, int maxItems,
        IReadOnlyDictionary<string, int>? sourceCounts = null, long collectMs = 0,
        IReadOnlyDictionary<string, string>? failures = null,
        IReadOnlyList<string>? sourceOrder = null)
    {
        maxItems = Math.Max(1, maxItems);
        var map = new Dictionary<string, RecItem>(StringComparer.OrdinalIgnoreCase);
        int raw = 0;

        foreach (RecItem item in items)
        {
            raw++;
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) continue;
            if (rules.IsExcluded(item.Path, item.IsFolder, out _)) continue;

            string key = item.Key;
            if (!map.TryGetValue(key, out RecItem? existing))
            {
                map[key] = item;
                continue;
            }

            map[key] = Better(existing, item);
        }

        IOrderedEnumerable<RecItem> sorted;
        if (sourceOrder is { Count: > 0 })
        {
            /* 用户排过来源顺序：先按来源分组，组内仍按修改时间倒序。
             * 没在顺序表里的来源排最后（正常不会发生，ResolveSourceOrder 会补齐）。 */
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sourceOrder.Count; i++) rank[sourceOrder[i]] = i;
            sorted = map.Values
                .OrderBy(i => rank.TryGetValue(i.SourceId, out int r) ? r : int.MaxValue)
                .ThenByDescending(i => i.Modified)
                .ThenByDescending(i => i.SourceWeight)
                .ThenBy(i => i.DisplayName.Length);
        }
        else
        {
            sorted = map.Values
                .OrderByDescending(i => i.Modified)
                .ThenByDescending(i => i.SourceWeight)
                .ThenBy(i => i.DisplayName.Length);
        }

        List<RecItem> ordered = sorted
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToList();

        return new RecSnapshot
        {
            Items = ordered,
            RawCount = raw,
            CollectMs = collectMs,
            SourceCounts = sourceCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            Failures = failures ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CollectedAt = DateTime.Now,
        };
    }

    /// <summary>保留权重高的那条，徽标与来源列表合并。</summary>
    private static RecItem Better(RecItem a, RecItem b)
    {
        RecItem winner = b.SourceWeight > a.SourceWeight ? b : a;
        RecItem loser = ReferenceEquals(winner, a) ? b : a;

        var badges = new List<string>(winner.Badges);
        foreach (string badge in loser.Badges)
            if (!badges.Contains(badge, StringComparer.OrdinalIgnoreCase))
                badges.Add(badge);

        /* 胜者自己的 id 也要记进去：否则"同一文件被两个来源命中"时，
         * MergedSources 只剩被合并掉的那个，排错时看不出它自己是谁。 */
        var sources = new List<string> { winner.SourceId };
        foreach (string id in winner.MergedSources)
            if (!sources.Contains(id, StringComparer.OrdinalIgnoreCase)) sources.Add(id);
        foreach (string id in loser.MergedSources.Count > 0 ? loser.MergedSources : new[] { loser.SourceId })
            if (!sources.Contains(id, StringComparer.OrdinalIgnoreCase))
                sources.Add(id);

        return new RecItem
        {
            ItemType = winner.ItemType,
            SourceId = winner.SourceId,
            SourceKind = winner.SourceKind,
            SourceWeight = winner.SourceWeight,
            Path = winner.Path,
            Name = winner.DisplayName,
            Modified = winner.Modified > loser.Modified ? winner.Modified : loser.Modified,
            Badges = badges,
            MergedSources = sources,
        };
    }
}
