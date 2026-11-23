using System;
using System.Collections.Generic;

namespace InputPlus.Core.Qbar.Search;

internal readonly record struct QbarPinyinHit(string Path, bool IsDir, int Quality, bool Approximate);

/// <summary>
/// 拼音检索的门面。
///
/// 改造前它是一张自己建的内存表（把 CJK 名字的拼音首字母/全拼摊进 string[]）。
/// 现在拼音进了 tantivy 索引本身（<c>pinyin</c> / <c>pinyin_initials</c> 两个字段），
/// 这里只保留**同一套对外形状**，让上层（<c>QbarSearchEngine.TryEnhance</c>）一行都不用改：
///   · <see cref="Ready"/> / <see cref="Building"/> / <see cref="Count"/> 转成索引的状态；
///   · <see cref="Query"/> 走索引拿候选，再用 <see cref="QbarPinyin.Quality"/> 打同一套档位分。
///
/// 这样"拼音转换只许一份实现"这条约束才真正成立：转换在 <see cref="QbarPinyin"/>，
/// 检索在索引，判档在这里 —— 没有第二张表、也没有第二份转换。
/// </summary>
internal static class QbarPinyinIndex
{
    /// <summary>
    /// 拼音入口被显式作废过（自检验"未就绪时老实返回空"这条）。
    /// 索引本身在磁盘上没动 —— 作废的是**这个入口**，所以 <see cref="Query"/> 直接返回空，
    /// 上层会看到 <c>PinyinPending</c> 而不是"搜到了假的拼音结果"。
    /// </summary>
    private static volatile bool _invalidated;

    /// <summary>自检/排障用的一句话。</summary>
    public static string DebugSummary
        => " [docs=" + QbarIndex.DocCount + " ready=" + Ready + " invalidated=" + _invalidated
           + " status=" + QbarIndex.Status + "]";

    public static bool Ready => !_invalidated && QbarIndex.Ready;

    /// <summary>索引还在建的时候，上层的提示条要能说"拼音正在准备"。</summary>
    public static bool Building => !_invalidated && QbarIndex.Building;

    /// <summary>能参与拼音检索的条数（就是索引里的文档数）。</summary>
    public static int Count => (int)Math.Min(int.MaxValue, QbarIndex.DocCount);

    public static string Status => _invalidated ? "已作废（等待重建）" : QbarIndex.Status;

    /// <summary>拼音表已经不在托管堆里了（在索引文件的倒排里），常驻增量为 0。</summary>
    public static long EstimatedBytes => 0;

    /// <summary>拼音索引随主索引一起建，没有单独的构建阶段；这里只清掉"作废"标记。</summary>
    public static void Start()
    {
        _invalidated = false;
        if (!QbarIndex.Ready && !QbarIndex.Building) QbarIndex.Start();
    }

    /// <summary>作废拼音入口（自检用，见 <see cref="_invalidated"/>）。</summary>
    public static void Invalidate()
    {
        _invalidated = true;
    }

    /// <summary>
    /// 查询：首字母前缀 &gt; 全拼前缀 &gt; 全拼子序列。档位与改造前完全一致。
    /// 索引只负责把候选捞出来（2-gram AND 是超集），判档在这一层做。
    /// </summary>
    public static List<QbarPinyinHit> Query(string ascii, int limit)
    {
        var hits = new List<QbarPinyinHit>(16);
        if (limit <= 0) return hits;
        if (_invalidated) return hits;

        string q = QbarPinyin.NormalizeQuery(ascii);
        if (q.Length < 2) return hits;
        if (!QbarIndex.Ready) return hits;

        /* 多要一批：索引给的是"拼音里含这些 2-gram"的超集，判档之后会掉一批。 */
        List<IndexHit> raw = QbarTantivyBackend.Pinyin(q, Math.Min(limit * 8, 400), null);
        if (raw.Count == 0)
        {
            /* 子序列（yh → yinhang）在 2-gram AND 里查不到：两段拼音中间隔着别的字母。
             * 索引侧的模糊入口用「逐字符 1-gram AND」兜住这个必要条件，再来这里判档。 */
            raw = QbarTantivyBackend.Fuzzy(q, Math.Min(limit * 8, 400), null);
        }

        var scored = new List<(int Quality, string Path, bool IsDir)>(raw.Count);
        foreach (IndexHit hit in raw)
        {
            /* 目录也参与：启动器里「设计稿」这种文件夹和文件一样常被拼音找
             * （改造前的 CollectCjkNames 两个都收）。 */
            string name = System.IO.Path.GetFileName(hit.Path);
            int quality = QbarPinyin.Quality(name, q, out bool matched);
            if (!matched) continue;
            scored.Add((quality, hit.Path, hit.IsDir == 1));
        }

        scored.Sort((a, b) => b.Quality != a.Quality
            ? b.Quality.CompareTo(a.Quality)
            : string.CompareOrdinal(a.Path, b.Path));

        int take = Math.Min(limit, scored.Count);
        for (int i = 0; i < take; i++)
            hits.Add(new QbarPinyinHit(scored[i].Path, scored[i].IsDir, scored[i].Quality, false));
        return hits;
    }
}
