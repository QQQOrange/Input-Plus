using System;
using System.Collections.Generic;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;
using InputPlus.Core.Suggestions;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// 搜索层：把浮层的关键词转成一批 <see cref="DialogSelection"/>。
///
/// 复用 qbar 的文件搜索内核（进程内 tantivy 索引），
/// **不新起进程、不新建索引、不读外部索引文件** —— 只调
/// <see cref="QbarSearchEngine.Run"/> 这一个入口，索引归属与降级由它自己管。
///
/// 空查询时给"最近在此类对话框里选过的项"（Recorder 里已有），
/// 这样用户一唤出浮层就能直接回车。
/// </summary>
internal sealed class DialogSearchProvider
{
    private readonly Func<AppSettings> _settings;
    private QbarQueryContext? _context;

    internal DialogSearchProvider(Func<AppSettings> settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>设置变了（换引擎、改过滤规则）就丢掉缓存的上下文，下次查询重建。</summary>
    internal void Invalidate() => _context = null;

    /// <summary>
    /// 查一次。返回条数不超过 <paramref name="limit"/>。
    /// 任何异常都降级成空列表：浮层里"没结果"比"整个功能挂掉"好。
    /// </summary>
    internal IReadOnlyList<DialogSelection> Search(DialogSearchQuery query, int limit)
    {
        if (limit <= 0) limit = 20;

        try
        {
            if (query.IsEmpty)
                return EmptyQuery(query.Scope, Math.Min(limit, 20));

            var settings = _settings();
            var context = _context ??= QbarSearchEngine.FromSettings(settings);

            /* 多取一些再按 Scope 过滤：只搜文件夹时前 N 条可能全是文件，
             * 取 limit 条会把结果列表白白清空。上限 600 与引擎内部窗口一致。 */
            int window = Math.Clamp(limit * 3, 60, 600);
            var outcome = QbarSearchEngine.Run(context, query.Keyword, window);

            var list = new List<DialogSelection>(Math.Min(window, limit));
            foreach (var hit in outcome.Hits)
            {
                if (query.Scope == DialogScope.Files && hit.IsDir) continue;
                if (query.Scope == DialogScope.Folders && !hit.IsDir) continue;

                string name = string.IsNullOrEmpty(hit.Name) ? hit.Path : hit.Name;
                list.Add(new DialogSelection(hit.Path, hit.IsDir, name));
                if (list.Count >= limit) break;
            }
            return list;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[文件对话框] 搜索失败，本次按无结果处理：" + ex.Message);
            return Array.Empty<DialogSelection>();
        }
    }

    /// <summary>
    /// 空查询：先给"在这个对话框里选过的"，不够再用「搜索前建议」的最近文件补。
    /// 用户一打开另存为就能看到一排能直接回车的项，而不是空白列表。
    /// </summary>
    private static IReadOnlyList<DialogSelection> EmptyQuery(DialogScope scope, int limit)
    {
        var list = new List<DialogSelection>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(DialogSelection item)
        {
            if (list.Count >= limit) return;
            if (scope == DialogScope.Files && item.IsFolder) return;
            if (scope == DialogScope.Folders && !item.IsFolder) return;
            if (!seen.Add(item.Path)) return;
            list.Add(item);
        }

        foreach (var item in DialogRecorder.RecentSelections(limit)) Add(item);

        if (list.Count < limit)
        {
            try
            {
                var recent = SuggestionsService.Current?.FirstScreen(Math.Min(limit * 2, 40));
                if (recent != null)
                {
                    foreach (var rec in recent)
                    {
                        if (rec.ItemType == RecItemType.App) continue;
                        Add(new DialogSelection(rec.Path, rec.ItemType == RecItemType.Folder, rec.DisplayName, "suggestions"));
                        if (list.Count >= limit) break;
                    }
                }
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[文件对话框] 用最近文件补空查询失败：" + ex.Message);
            }
        }

        return list;
    }

    /// <summary>只给自检用：确认上下文能按当前设置建起来。</summary>
    internal bool HasContext => _context != null;
}
