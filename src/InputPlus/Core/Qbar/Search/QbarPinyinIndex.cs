using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using TinyPinyin;

namespace InputPlus.Core.Qbar.Search;

internal readonly record struct QbarPinyinHit(string Path, bool IsDir, int Quality);

/// <summary>
/// 轻量拼音索引：只收录名字里含 CJK 的文件（通常不到全量的十分之一），
/// 每条存 名字全拼 + 首字母串，两条都用小写。
///
/// 为什么自己建：原生索引只做字节子串匹配，Everything 也一样 —— 拼音是它们共同的死角。
/// 为什么不落盘：构建一次几十万条名字，耗时在秒级；落盘的读写/版本/失效成本
/// 反而更高，先在内存里做，后面真嫌慢再考虑缓存（见 DECISIONS.md）。
/// </summary>
internal static class QbarPinyinIndex
{
    /// <summary>上限：再大就先把内存吃掉了。真实机器上 CJK 名字极少超过这个量级。</summary>
    private const int MaxEntries = 300000;

    /// <summary>
    /// 这张表的内存硬上限（估算口径同 <see cref="EstimatedBytes"/>）。
    /// 条数上限管不住内存 —— 长名字每条能到 300 字节，30 万条就是 80MB。
    /// 到了这个数就停止收录并在状态行写明，宁可拼音少覆盖一点，也别把进程撑爆。
    /// </summary>
    private const long MemoryBudget = 24L * 1024 * 1024;

    /// <summary>单条名字的固定开销（3 个 string 头 + 数组槽位），估算用。</summary>
    private const long EntryOverhead = 78;

    private static readonly object Gate = new object();

    /* 只存目录引用和名字引用：目录串来自索引快照，是同一批 string 实例，
     * 不为每条名字再拼一次完整路径 —— Path.Combine 会给每个 CJK 名字额外分配
     * 一条几十字符的新串，几十万条就是几十 MB，纯属自找。 */
    private static string[] _dirs = Array.Empty<string>();
    private static string[] _names = Array.Empty<string>();
    private static string[] _initials = Array.Empty<string>();
    private static string[] _full = Array.Empty<string>();

    private static volatile bool _ready;
    private static volatile bool _building;
    private static volatile int _count;
    private static volatile string _status = "未构建";
    private static int _started;

    /// <summary>自检/排障用的一句话：条数、就绪位、快照版本是否对得上、前几个首字母串。</summary>
    public static string DebugSummary
    {
        get
        {
            var sb = new StringBuilder(120);
            sb.Append(" [count=").Append(_count)
              .Append(" ready=").Append(_ready)
              .Append(" src=").Append(_sourceTicks)
              .Append(" idx=").Append(NativeSearchIndex.BuiltAt.Ticks)
              .Append(" sample=");
            int limit = _count < 4 ? _count : 4;
            for (int i = 0; i < limit; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_initials[i]).Append('/').Append(_full[i]);
            }
            sb.Append(']');
            return sb.ToString();
        }
    }

    public static bool Ready => _ready;
    public static bool Building => _building;
    public static int Count => _count;
    public static string Status => _status;

    /// <summary>
    /// 这张索引自己占多少托管内存（估算）：4 个引用数组 + 每条名字的
    /// 名字/首字母/全拼三条串。目录串是索引快照里的实例，这里不重复计入。
    /// 基准报告要用它，因为 GC.GetTotalMemory 的差值在托管堆已经很大时分辨不出几 MB。
    /// </summary>
    public static long EstimatedBytes
    {
        get
        {
            int count = _count;
            long bytes = (long)count * (4 * 8);
            for (int i = 0; i < count; i++)
            {
                bytes += ((long)_names[i].Length + _initials[i].Length + _full[i].Length) * 2L;
                bytes += 3 * 26L;
            }
            return bytes;
        }
    }

    public static void Start()
    {
        if (_ready || _building) return;
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

        _building = true;
        _status = "构建中";

        var thread = new Thread(BuildWorker)
        {
            IsBackground = true,
            Name = "qbar-pinyin-index",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public static void Invalidate()
    {
        lock (Gate)
        {
            _dirs = Array.Empty<string>();
            _names = Array.Empty<string>();
            _initials = Array.Empty<string>();
            _full = Array.Empty<string>();
            _count = 0;
            _ready = false;
            _status = "未构建";
        }
        Interlocked.Exchange(ref _started, 0);
    }

    /// <summary>这张表是按哪一版索引快照建的；索引重建过就得作废（名字可能已经变了）。</summary>
    private static long _sourceTicks;

    private static void BuildWorker()
    {
        try
        {
            var dirs = new List<string>(4096);
            var names = new List<string>(4096);
            var initials = new List<string>(4096);
            var full = new List<string>(4096);

            long bytes = 0;
            bool capped = false;

            NativeSearchIndex.CollectCjkNames((name, dir) =>
            {
                if (capped || dirs.Count >= MaxEntries) return;

                string? initial = SafeInitials(name);
                string? pinyin = SafeFull(name);
                if (initial == null || pinyin == null) return;

                if (bytes >= MemoryBudget)
                {
                    capped = true;
                    return;
                }

                bytes += EntryOverhead + ((long)name.Length + initial.Length + pinyin.Length) * 2L;
                dirs.Add(dir);
                names.Add(name);
                initials.Add(initial);
                full.Add(pinyin);
            }, 120000);

            lock (Gate)
            {
                _dirs = dirs.ToArray();
                _names = names.ToArray();
                _initials = initials.ToArray();
                _full = full.ToArray();
                _count = _dirs.Length;
                _sourceTicks = NativeSearchIndex.BuiltAt.Ticks;
                _ready = _count > 0;
                _status = !_ready ? "无中文文件名"
                        : capped ? ("就绪 " + _count + " 条（已达内存上限，只收了一部分）")
                        : ("就绪 " + _count + " 条");
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "构建拼音索引");
            _status = "构建失败";
        }
        finally
        {
            _building = false;
        }
    }

    private static string? SafeInitials(string name)
    {
        try
        {
            string value = PinyinHelper.GetPinyinInitials(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.ToLowerInvariant();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "取拼音首字母");
            return null;
        }
    }

    private static string? SafeFull(string name)
    {
        try
        {
            string value = PinyinHelper.GetPinyin(name, "");
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Replace(" ", "").ToLowerInvariant();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "取全拼");
            return null;
        }
    }

    /// <summary>
    /// 查询：首字母前缀 &gt; 全拼前缀 &gt; 全拼子序列（yh 能命中 yinhang）。
    /// 子序列放在最后，因为它的假阳性最多，只配当兜底。
    /// </summary>
    public static List<QbarPinyinHit> Query(string ascii, int limit)
    {
        var hits = new List<QbarPinyinHit>(16);
        if (limit <= 0) return hits;

        /* 索引重建过 → 这张表的名字可能已经对不上了，先作废并在后台重来。
         * 本次返回空（调用方会看到「拼音索引正在建立」的提示），不阻塞查询。 */
        if (_ready && NativeSearchIndex.BuiltAt.Ticks != _sourceTicks)
        {
            Invalidate();
            Start();
            return hits;
        }

        if (!_ready) return hits;

        string q = Normalize(ascii);
        if (q.Length < 2) return hits;

        string[] dirs = _dirs;
        string[] names = _names;
        string[] initials = _initials;
        string[] full = _full;

        var scored = new List<(int Quality, int Index)>(32);

        for (int i = 0; i < dirs.Length; i++)
        {
            int quality = 0;
            string ini = initials[i];
            string whole = full[i];

            if (ini.StartsWith(q, StringComparison.Ordinal))
                quality = 200 - Math.Min(80, ini.Length - q.Length);
            else if (whole.StartsWith(q, StringComparison.Ordinal))
                quality = 150 - Math.Min(60, whole.Length - q.Length);
            else if (IsSubsequence(whole, q))
                quality = 80 - Math.Min(40, names[i].Length / 4);

            if (quality > 0) scored.Add((quality, i));
        }

        if (scored.Count == 0) return hits;

        scored.Sort((a, b) => b.Quality != a.Quality
            ? b.Quality.CompareTo(a.Quality)
            : a.Index.CompareTo(b.Index));

        int take = Math.Min(limit, scored.Count);
        for (int i = 0; i < take; i++)
        {
            int index = scored[i].Index;
            hits.Add(new QbarPinyinHit(System.IO.Path.Combine(dirs[index], names[index]), false, scored[i].Quality));
        }

        return hits;
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = char.ToLowerInvariant(text[i]);
            if (c == ' ' || c == '\'' || c == '-' || c == '_') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsSubsequence(string text, string needle)
    {
        int t = 0;
        for (int n = 0; n < needle.Length; n++)
        {
            while (t < text.Length && text[t] != needle[n]) t++;
            if (t >= text.Length) return false;
            t++;
        }
        return true;
    }
}
