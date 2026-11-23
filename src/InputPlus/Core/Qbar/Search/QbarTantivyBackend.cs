using System;
using System.Collections.Generic;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Content;

namespace InputPlus.Core.Qbar.Search;

/// <summary>
/// tantivy 后端：把语义层翻译好的 <see cref="QbarSearchSpec"/> 交给进程内索引取候选窗口，
/// 再用与索引**同一套**谓词逐条复核。
///
/// 为什么还要复核：索引侧的 2-gram AND、去锚点正则、字面量预筛都是**超集**
/// （为了不漏召回）。真正的语义 —— 子串是否连续、闭区间边界、目录 vs 文件分支、
/// wfn 通配、正则的锚定 —— 全由 <see cref="QbarSpecFilter"/> 说一不二，
/// 打分的唯一实现在 <see cref="QbarNameScore"/>。这样"同一个查询换后端结果集不一样"
/// 这条红线才守得住。
///
/// 取窗口时故意多要一倍（<see cref="FetchFactor"/>）：索引层按 BM25 + 名字优先加权
/// 排序，和最终打分公式不是一回事；多取一倍后再按真分数裁到 <c>window</c> 条，
/// 命中数超过窗口的查询也不会把真正该排前面的条目挤掉。
/// </summary>
internal static class QbarTantivyBackend
{
    /// <summary>最近一次真正发给索引的查询描述（查询调试面板用）。</summary>
    internal static string LastQuery { get; private set; } = "";

    internal static string LastDetail { get; private set; } = "";

    /// <summary>最近一次被本地复核拒绝的前几条原始路径（排障用：区分"索引没给"和"我们剔了"）。</summary>
    internal static string LastRejectedSample { get; private set; } = "";

    /// <summary>
    /// 最近一次查询索引侧直出的分面（标签 + 条数，与那一次 hits 同一批文档）。
    /// 空 = 索引没给（老索引 / 分面被关）。空时调用方退回"按本页统计"。
    /// </summary>
    internal static List<(string Label, int Count)> LastIndexFacets { get; private set; } = new();

    /// <summary>
    /// 分面直出开关（默认开）。设 INPUTPLUS_QBAR_NOFACETS=1 可关掉 —— 只用于
    /// 实测"分面直出到底多花多少毫秒"的 A/B，正式路径永远开着。
    /// </summary>
    internal static bool FacetsEnabled { get; } =
        Environment.GetEnvironmentVariable("INPUTPLUS_QBAR_NOFACETS") != "1";

    /// <summary>多取几倍候选再裁：见类注释。</summary>
    private const int FetchFactor = 2;

    private const int FetchCap = 20000;

    /// <summary>
    /// 跑一次索引查询并做本地复核。
    /// 返回 false = 这条路不可用（索引库缺失 / 索引未就绪 / 查询报错），调用方给出明确提示；
    /// 返回 true 且 hits 为空 = 索引确实没有命中，这是有效结果，不许当失败处理。
    /// </summary>
    internal static bool TryRun(
        QbarSearchSpec spec,
        int window,
        out List<FileSearchHit> hits,
        out string queryUsed,
        out string detail)
    {
        hits = new List<FileSearchHit>();
        queryUsed = "";
        detail = "";

        if (spec == null)
        {
            detail = "空查询不走索引";
            return false;
        }

        /* 只看正文（`content:` 谓词或「只看正文」过滤器）→ 这次**只**查正文索引。
         * 与文件名检索分两条路走，是为了让"我搜的是正文"这件事有确定语义：
         * 混在一起会出现"名字里没有、正文里也没有，但结果里有"的错觉。 */
        if (spec.ContentOnly || spec.ContentTerms.Count > 0 || spec.ContentPhrases.Count > 0)
        {
            return TryRunContent(spec, window, out hits, out queryUsed, out detail);
        }

        IndexQuery query = BuildQuery(spec);
        queryUsed = Describe(query);
        LastQuery = queryUsed;

        if (!TantivyApi.IsAvailable)
        {
            detail = TantivyApi.StatusText;
            LastDetail = detail;
            return false;
        }

        if (!QbarIndex.Ready)
        {
            /* 索引没就绪时查询会"成功"返回 0 条 —— 那会把"索引还在建"显示成
             * "没有这个文件"，比明确提示更误导人。 */
            detail = QbarIndex.Building
                ? "索引正在建立（" + QbarIndex.Status + "）"
                : "索引未就绪（" + QbarIndex.Status + "）";
            LastDetail = detail;
            return false;
        }

        int fetch = Math.Min(FetchCap, Math.Max(window, window * FetchFactor));
        /* 分面统计要"近 7 天"的起点：索引侧按天数分桶，起点由这里算好带过去。 */
        int today = QbarSyntaxValues.ToDay(DateTime.Now);
        query.FreshDay = today >= 0 ? today - 7 : -1;
        query.Facets = FacetsEnabled;
        IndexSearchResult result = QbarIndex.Search(query, fetch);
        if (!result.Ok)
        {
            detail = "索引查询失败：" + result.Error;
            LastDetail = detail;
            LastIndexFacets = new List<(string Label, int Count)>();
            return false;
        }

        LastIndexFacets = FoldIndexFacets(result.Facets);

        var filter = new QbarSpecFilter(spec);
        int rejected = 0;
        var sample = new List<string>(3);
        var kept = new List<(FileSearchHit Hit, int Score)>(result.Hits.Count);

        foreach (IndexHit raw in result.Hits)
        {
            bool isDir = raw.IsDir == 1;
            int sizeKb = raw.Size <= 0 ? 0 : (int)Math.Min(raw.Size / 1024L, int.MaxValue);
            /* 大小谓词必须用**精确字节**（raw.Size）：KB 字段是给界面显示用的，
             * 1~1023 字节的文件用它会被抹成 0，`size:>0` 直接漏掉这些文件。 */
            if (!filter.Accept(raw.Path, isDir, sizeKb, raw.Day, out int score, raw.Size))
            {
                rejected++;
                if (sample.Count < 3) sample.Add((isDir ? "[目录] " : "[文件] ") + raw.Path);
                continue;
            }
            kept.Add((new FileSearchHit(raw.Path, isDir, score, sizeKb, raw.Day, null), score));
        }

        /* 按真分数裁到 window 条。同分时用「路径短者先、再按序数」——
         * 与 QbarSearchEngine 的排名比较同构，免得同一批候选在两个地方排出两个顺序。 */
        kept.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            int byLength = a.Hit.Path.Length.CompareTo(b.Hit.Path.Length);
            if (byLength != 0) return byLength;
            return string.CompareOrdinal(a.Hit.Path, b.Hit.Path);
        });

        int take = Math.Min(window, kept.Count);
        for (int i = 0; i < take; i++) hits.Add(kept[i].Hit);

        detail = $"{QbarIndex.Status} · 索引 {result.Ms:0.0} ms · 取 {result.Total} 条"
                 + $" · 本地复核剔除 {rejected} 条";
        LastDetail = detail;
        LastRejectedSample = string.Join("；", sample);

        /* 普通关键词也顺手查一遍正文（默认开）。
         * 只在"名字命中还没填满窗口"时补，而且分数压在名字命中之下 ——
         * 用户凭记忆打文件名时，结果顺序不能因为开了正文索引就变。 */
        AppendContentHits(spec, window, hits, ref detail);
        LastDetail = detail;
        return true;
    }

    /// <summary>把 `content:` 查询交给正文索引，再用同一套谓词复核。</summary>
    private static bool TryRunContent(
        QbarSearchSpec spec,
        int window,
        out List<FileSearchHit> hits,
        out string queryUsed,
        out string detail)
    {
        hits = new List<FileSearchHit>();
        detail = "";
        /* 「只看正文」时关键词在 Terms 里（`content: 报表`），`content:报表` 时在
         * ContentTerms 里 —— 两种来源都算，后端不必知道用户用的是哪种写法。 */
        var contentTerms = new List<string>(spec.ContentTerms);
        contentTerms.AddRange(spec.Terms);
        var contentPhrases = new List<string>(spec.ContentPhrases);
        contentPhrases.AddRange(spec.Phrases);
        queryUsed = "content=" + string.Join("|", contentTerms.Concat(contentPhrases));
        LastQuery = queryUsed;

        if (!ContentIndex.Enabled)
        {
            detail = "正文索引已关闭（设置 → 搜索 → 索引与后端 → 正文索引）";
            LastDetail = detail;
            return false;
        }
        if (!ContentIndex.Ready)
        {
            detail = ContentIndex.Building
                ? "正文索引正在建立（" + ContentIndex.Status + "）"
                : "正文索引未就绪（" + ContentIndex.Status + "）";
            LastDetail = detail;
            return false;
        }

        ContentQuery query = BuildContentQuery(spec, contentTerms, contentPhrases);
        int fetch = Math.Min(FetchCap, Math.Max(window, window * FetchFactor));
        ContentSearchResult result = ContentIndex.Search(query, fetch);
        if (!result.Ok)
        {
            detail = "正文检索失败：" + result.Error;
            LastDetail = detail;
            return false;
        }

        /* 复核用的 spec **去掉词**：词已经在正文索引里筛过了，
         * 再拿它去比对文件名会把正文命中全部误杀（文件名里本来就没有这个词）。 */
        var filter = new QbarSpecFilter(Termless(spec));
        var kept = new List<(FileSearchHit Hit, int Score)>(result.Hits.Count);
        foreach (ContentHit raw in result.Hits)
        {
            bool isDir = raw.IsDir == 1;
            int sizeKb = raw.Size <= 0 ? 0 : (int)Math.Min(raw.Size / 1024L, int.MaxValue);
            if (!filter.Accept(raw.Path, isDir, sizeKb, raw.Day, out int score, raw.Size)) continue;
            kept.Add((new FileSearchHit(raw.Path, isDir, score + ContentHitBonus, sizeKb, raw.Day, null), score));
        }

        kept.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            int byLength = a.Hit.Path.Length.CompareTo(b.Hit.Path.Length);
            if (byLength != 0) return byLength;
            return string.CompareOrdinal(a.Hit.Path, b.Hit.Path);
        });

        int take = Math.Min(window, kept.Count);
        for (int i = 0; i < take; i++) hits.Add(kept[i].Hit);

        detail = $"正文索引 {result.Ms:0.0} ms · 命中 {result.Total} 条 · 复核保留 {kept.Count} 条";
        LastDetail = detail;
        LastIndexFacets = new List<(string Label, int Count)>();
        return true;
    }

    /// <summary>普通关键词的正文补充：把正文命中追加在名字命中之后（去重、低分）。</summary>
    private static void AppendContentHits(QbarSearchSpec spec, int window,
                                          List<FileSearchHit> hits, ref string detail)
    {
        try
        {
            if (!ContentIndex.Ready || !ContentIndex.Enabled) return;
            if (!QbarSearchEngine.ContentMergeEnabled) return;
            if (spec.Terms.Count == 0 && spec.Phrases.Count == 0) return;
            if (hits.Count >= window) return;          // 名字命中已经填满窗口，不抢位置

            ContentQuery query = BuildContentQuery(spec, spec.Terms, spec.Phrases);
            int fetch = Math.Max(50, window - hits.Count);
            ContentSearchResult result = ContentIndex.Search(query, fetch);
            if (!result.Ok || result.Hits.Count == 0) return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileSearchHit h in hits) seen.Add(h.Path);

            var filter = new QbarSpecFilter(Termless(spec));
            int added = 0;
            foreach (ContentHit raw in result.Hits)
            {
                if (hits.Count >= window) break;
                if (!seen.Add(raw.Path)) continue;
                bool isDir = raw.IsDir == 1;
                int sizeKb = raw.Size <= 0 ? 0 : (int)Math.Min(raw.Size / 1024L, int.MaxValue);
                if (!filter.Accept(raw.Path, isDir, sizeKb, raw.Day, out int score, raw.Size)) continue;
                hits.Add(new FileSearchHit(raw.Path, isDir, ContentMergeBonus + score, sizeKb, raw.Day, null));
                added++;
            }
            if (added > 0) detail += $" · 正文补充 {added} 条";
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "正文补充检索");
        }
    }

    /// <summary>正文命中 + 这个分（让它排在名字全等/前缀命中之后）。</summary>
    private const int ContentHitBonus = 120;

    /// <summary>并入普通关键词时的分数：比名字命中低一大截，只起"有这件事"的作用。</summary>
    private const int ContentMergeBonus = 60;

    private static ContentQuery BuildContentQuery(QbarSearchSpec spec,
                                                  IEnumerable<string> terms,
                                                  IEnumerable<string> phrases)
    {
        var q = new ContentQuery();
        q.Terms.AddRange(terms);
        q.Phrases.AddRange(phrases);
        q.Ext.AddRange(spec.Extensions);
        q.NegExt.AddRange(spec.NegExtensions);
        q.Path.AddRange(spec.PathPins);
        q.NegPath.AddRange(spec.NegPathPins);
        q.MinSize = spec.MinSize;
        q.MaxSize = spec.MaxSize;
        q.MinDay = spec.MinDay;
        q.MaxDay = spec.MaxDay;
        q.Files = spec.FilesOnly;
        q.Dirs = spec.FoldersOnly;
        return q;
    }

    /// <summary>复制一份"只剩过滤条件、没有关键词"的 spec（给正文命中的复核用）。</summary>
    private static QbarSearchSpec Termless(QbarSearchSpec spec)
    {
        var copy = new QbarSearchSpec();
        copy.NegTerms.AddRange(spec.NegTerms);
        foreach (string e in spec.Extensions) copy.Extensions.Add(e);
        foreach (string e in spec.NegExtensions) copy.NegExtensions.Add(e);
        copy.PathPins.AddRange(spec.PathPins);
        copy.NegPathPins.AddRange(spec.NegPathPins);
        copy.MinSize = spec.MinSize;
        copy.MaxSize = spec.MaxSize;
        copy.MinDay = spec.MinDay;
        copy.MaxDay = spec.MaxDay;
        copy.FilesOnly = spec.FilesOnly;
        copy.FoldersOnly = spec.FoldersOnly;
        copy.Glob = spec.Glob;
        copy.RegexText = spec.RegexText;
        copy.CommandKeyword = spec.CommandKeyword;
        return copy;
    }

    /// <summary>
    /// 把索引直出的原始分面折成界面用的 8 个桶（文档/图片/视频/音频/程序/文件夹/其他/近7天）。
    ///
    /// 为什么在 C# 折而不在 Rust 折：桶的定义来自 `QbarFilterCatalog`（用户可编辑的过滤器清单），
    /// 索引层不该知道"docx 算文档"这种业务规则。索引只负责"每个扩展名多少条"。
    /// </summary>
    private static List<(string Label, int Count)> FoldIndexFacets(List<IndexFacet> facets)
    {
        var result = new List<(string Label, int Count)>(8);
        if (facets == null || facets.Count == 0) return result;

        var doc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var video = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        QbarFilterCatalog.FillExtensions(QbarFilterCatalog.DocExtensions, doc);
        QbarFilterCatalog.FillExtensions(QbarFilterCatalog.PicExtensions, pic);
        QbarFilterCatalog.FillExtensions(QbarFilterCatalog.VideoExtensions, video);
        QbarFilterCatalog.FillExtensions(QbarFilterCatalog.AudioExtensions, audio);

        long docs = 0, pics = 0, videos = 0, audios = 0, programs = 0, others = 0, dirs = 0, fresh = 0;
        foreach (IndexFacet facet in facets)
        {
            switch (facet.Label)
            {
                case "file": continue;
                case "dir": dirs += facet.Count; continue;
                case "fresh": fresh += facet.Count; continue;
            }
            string ext = facet.Label;
            if (ext == "_none") others += facet.Count;
            else if (doc.Contains(ext)) docs += facet.Count;
            else if (pic.Contains(ext)) pics += facet.Count;
            else if (video.Contains(ext)) videos += facet.Count;
            else if (audio.Contains(ext)) audios += facet.Count;
            else if (ext.Equals("exe", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals("lnk", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals("url", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals("bat", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals("cmd", StringComparison.OrdinalIgnoreCase)) programs += facet.Count;
            else others += facet.Count;
        }

        result.Add(("文档", (int)Math.Min(docs, int.MaxValue)));
        result.Add(("图片", (int)Math.Min(pics, int.MaxValue)));
        result.Add(("视频", (int)Math.Min(videos, int.MaxValue)));
        result.Add(("音频", (int)Math.Min(audios, int.MaxValue)));
        result.Add(("程序", (int)Math.Min(programs, int.MaxValue)));
        result.Add(("文件夹", (int)Math.Min(dirs, int.MaxValue)));
        result.Add(("其他", (int)Math.Min(others, int.MaxValue)));
        result.Add(("近7天", (int)Math.Min(fresh, int.MaxValue)));
        return result;
    }

    /// <summary>拼音检索：只在 pinyin / pinyin_initials 上匹配，其余谓词照旧下推。</summary>
    internal static List<IndexHit> Pinyin(string text, int limit, QbarSearchSpec? spec)
    {
        var query = new IndexQuery();
        query.Terms.Add(text);
        CopyFilters(spec, query);
        IndexSearchResult result = QbarIndex.PinyinSearch(query, limit);
        return result.Ok ? result.Hits : new List<IndexHit>();
    }

    /// <summary>模糊兜底：子序列候选集 + 编辑距离 1 的近似命中。</summary>
    internal static List<IndexHit> Fuzzy(string text, int limit, QbarSearchSpec? spec)
    {
        var query = new IndexQuery();
        query.Terms.Add(text);
        CopyFilters(spec, query);
        IndexSearchResult result = QbarIndex.FuzzySearch(query, limit);
        return result.Ok ? result.Hits : new List<IndexHit>();
    }

    /// <summary>把谓词原样搬到 <see cref="IndexQuery"/> 上（拼音/模糊检索也要守 ext:/size:/date:）。</summary>
    private static void CopyFilters(QbarSearchSpec? spec, IndexQuery query)
    {
        if (spec == null) return;
        query.Ext.AddRange(spec.Extensions);
        query.NegExt.AddRange(spec.NegExtensions);
        query.Path.AddRange(spec.PathPins);
        query.NegPath.AddRange(spec.NegPathPins);
        query.MinSize = spec.MinSize;
        query.MaxSize = spec.MaxSize;
        query.MinDay = spec.MinDay;
        query.MaxDay = spec.MaxDay;
        query.Files = spec.FilesOnly;
        query.Dirs = spec.FoldersOnly;
    }

    /// <summary>spec → 索引查询。谓词全部下推，不在 C# 里"先取 2000 再筛"。</summary>
    internal static IndexQuery BuildQuery(QbarSearchSpec spec)
    {
        var query = new IndexQuery();
        query.Terms.AddRange(spec.Terms);
        query.Phrases.AddRange(spec.Phrases);
        query.Neg.AddRange(spec.NegTerms);
        CopyFilters(spec, query);
        query.Glob = spec.Glob;
        query.Regex = spec.RegexText;

        /* 一旦带 ext:/size: 约束，目录就不可能有意义（目录没有扩展名、也没有 size 语义）——
         * 索引那边会被目录填满窗口，复核阶段又全被剔掉，用户看到"零结果"。
         * 这条规则与 QbarSpecFilter 的目录分支同源。 */
        bool dirsUseless = spec.Extensions.Count > 0 || spec.NegExtensions.Count > 0
                           || spec.MinSize >= 0 || spec.MaxSize >= 0;
        if (spec.FoldersOnly)
        {
            query.Dirs = true;
            query.Files = false;
        }
        else if (spec.FilesOnly || dirsUseless)
        {
            query.Files = true;
            query.Dirs = false;
        }

        query.Sort = "score";
        return query;
    }

    /// <summary>打印成一行给人看的查询描述（调试面板 / --qbar-query）。</summary>
    internal static string Describe(IndexQuery query)
    {
        var parts = new List<string>();
        if (query.Terms.Count > 0) parts.Add("terms=" + string.Join("|", query.Terms));
        if (query.Phrases.Count > 0) parts.Add("phrases=" + string.Join("|", query.Phrases));
        if (query.Neg.Count > 0) parts.Add("!" + string.Join("|", query.Neg));
        if (query.Ext.Count > 0) parts.Add("ext=" + string.Join(";", query.Ext));
        if (query.NegExt.Count > 0) parts.Add("!ext=" + string.Join(";", query.NegExt));
        if (query.Path.Count > 0) parts.Add("path=" + string.Join("|", query.Path));
        if (query.NegPath.Count > 0) parts.Add("!path=" + string.Join("|", query.NegPath));
        if (query.MinSize >= 0 || query.MaxSize >= 0) parts.Add($"size={query.MinSize}..{query.MaxSize}");
        if (query.MinDay >= 0 || query.MaxDay >= 0)
            parts.Add("date=" + QbarSyntaxValues.DescribeDateRange(query.MinDay, query.MaxDay));
        if (query.Files) parts.Add("files");
        if (query.Dirs) parts.Add("dirs");
        if (query.Glob.Length > 0) parts.Add("wfn=" + query.Glob);
        if (query.Regex.Length > 0) parts.Add("regex=" + query.Regex);
        return parts.Count == 0 ? "(空查询)" : string.Join(" ", parts);
    }
}
