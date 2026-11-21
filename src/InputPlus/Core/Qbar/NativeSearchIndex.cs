using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using InputPlus.Core.Qbar.Search;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 一条文件/目录搜索结果。<see cref="Score"/> 越大越靠前，调用方只需按返回顺序展示。
///
/// <see cref="SizeKb"/> / <see cref="Day"/> 是语义层加进来的（排序、分面、诊断要显示
/// "多大、什么时候改的"），默认值 0 / -1 表示"这份快照没这个字段" ——
/// 老调用点 new FileSearchHit(path, false, score) 保持原样编译、原样行为。
/// </summary>
/// <summary>
/// 一条候选。Highlights 只有"Everything 后端"会填：它是 SDK 给的精确命中区间，
/// 自建索引那条路仍然是引擎按关键词自己算（见 QbarSearchEngine.BuildHighlights）。
/// </summary>
internal readonly record struct FileSearchHit(string Path, bool IsDir, int Score, int SizeKb = 0, int Day = -1,
                                               int[]? Highlights = null);

/// <summary>
/// Qbar 的文件搜索内核：自己建索引、自己扫盘、自己打分，不依赖 Everything，
/// 也不启动任何外部进程。
///
/// 为什么要有它：Qbar 的文件搜索原先靠外挂 Everything。用户机器上没装、
/// 内置副本被杀软拦掉、或者 Everything 的索引服务没起来，文件搜索就整个消失，
/// 而这时界面只会显示"没有结果"——用户分不清是没找到还是坏了。把索引放进
/// 本进程以后，至少"有没有索引、索引了多少、什么时候建的"是我们自己说了算。
///
/// 三条设计底线：
/// 一、Search 在 UI 线程上每个字符都会被调，所以它只读一份不可变快照；
///     建索引在后台线程往新数组里写，写完一次换引用，绝不拿锁挡住输入。
/// 二、任何入口都不许把异常抛给调用方。搜不到、索引坏了、盘被拔了，
///     都只是一次"没有结果"，不能让输入法跟着崩。
/// 三、文件名不存成 string[]。80 万个 string 光对象头就要 20MB 以上，
///     这里把名字按 UTF-8 连成一个 byte[]，再用 int[] 记起点和所属目录，
///     同样的数据少一半内存，也少 80 万次 GC 扫描。
/// </summary>
internal static class NativeSearchIndex
{
    /// <summary>文件条数上限。到了就停止枚举，索引仍然可用，只是不全。</summary>
    private const int MaxFiles = 800000;

    /// <summary>目录条数上限。目录比文件稀疏得多，20 万足够覆盖普通机器。</summary>
    private const int MaxDirs = 200000;

    /// <summary>单次构建的时间上限。慢盘 / 网络盘不该把后台线程挂到天荒地老。</summary>
    private static readonly TimeSpan MaxBuildTime = TimeSpan.FromSeconds(240);

    /// <summary>缓存有效期。超过就先拿旧索引顶着用，同时后台重建。</summary>
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(12);

    /// <summary>
    /// 落盘格式版本。字段布局一变就加一，老缓存会被当成没有索引。
    /// v2：文件多了 size（KB）和修改日（自 2000-01-01 的天数）两列，
    ///     语义层的 size: / date: / 分面时间桶都靠它们，不再逐条去盘上 stat。
    /// </summary>
    private const int SchemaVersion = 2;

    /// <summary>名字最长 512 个字符。Windows 自身限制更短，这里只是解码缓冲的上限。</summary>
    private const int MaxNameChars = 512;

    /// <summary>每处理这么多候选检查一次超时。4096 次足够便宜，又不会让 1ms 的超时拖太久。</summary>
    private const int TimeoutCheckEvery = 4096;

    /// <summary>文件名打分时"没有命中"的哨兵值。分数本身不会低于 0。</summary>
    private const int NoScore = int.MinValue;

    /// <summary>字节扫描的超时返回值，和"没找到"（-1）区分开。</summary>
    private const int ByteScanTimeout = -2;

    /// <summary>索引缓存目录：&lt;数据目录&gt;\file-index。</summary>
    internal static string IndexDir => Path.Combine(AppPaths.DataDir, "file-index");

    /// <summary>已有可用索引（至少一个文件）。目录命中不算 Ready，因为 Qbar 主要找文件。</summary>
    internal static bool Ready
    {
        get
        {
            var snap = _snap;
            return snap != null && snap.NameStart.Length > 0;
        }
    }

    /// <summary>后台线程是否正在建索引。设置页可以用它决定要不要转圈。</summary>
    internal static bool Building => Volatile.Read(ref _building) == 1;

    /// <summary>文件条数。建索引过程中返回的是"已经扫到多少"，不是最终值。</summary>
    internal static int FileCount
    {
        get
        {
            if (Volatile.Read(ref _building) == 1) return Volatile.Read(ref _liveFiles);
            var snap = _snap;
            return snap == null ? 0 : snap.NameStart.Length;
        }
    }

    /// <summary>目录条数。口径同 <see cref="FileCount"/>。</summary>
    internal static int DirCount
    {
        get
        {
            if (Volatile.Read(ref _building) == 1) return Volatile.Read(ref _liveDirs);
            var snap = _snap;
            return snap == null ? 0 : snap.Dirs.Length;
        }
    }

    /// <summary>给设置页 / 提示条用的一句话。任何时刻读都是完整的，不会读到一半拼接。</summary>
    internal static string Status => _status;

    /// <summary>失败原因；正常时为空串。和 Status 分开，免得界面把"正在建"也当成错误。</summary>
    internal static string Problem => _problem;

    /// <summary>索引建成（或缓存载入）的本地时间；没有索引时是 <see cref="DateTime.MinValue"/>。</summary>
    internal static DateTime BuiltAt
    {
        get
        {
            long ticks = Volatile.Read(ref _builtAtTicks);
            return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Local);
        }
    }

    /// <summary>本次索引覆盖的根目录。提权时盘根会取代被它包含的细根，见 <see cref="ComputeRoots"/>。</summary>
    internal static IReadOnlyList<string> Roots => _roots;

    private static readonly object Gate = new object();

    /// <summary>
    /// 当前可搜索的不可变快照。volatile：后台换引用，UI 线程只读，读完一整份
    /// 再开始搜，不会出现"数组换了、计数还是旧的"这种撕裂。
    /// </summary>
    private static volatile Snapshot? _snap;

    /// <summary>当前后台任务的取消源。换引用在 <see cref="Gate"/> 里做。</summary>
    private static CancellationTokenSource? _cts;

    /// <summary>任务代号。旧任务被取消后可能还会跑几百毫秒，只有代号最新的能发布结果。</summary>
    private static int _generation;

    private static int _started;
    private static int _building;
    private static int _liveFiles;
    private static int _liveDirs;
    private static long _builtAtTicks;
    private static volatile string[] _roots = Array.Empty<string>();
    private static volatile string _status = "文件索引未建立";
    private static volatile string _problem = "";

    /// <summary>超时日志只打一次，避免用户在搜索框里每敲一个字就刷一行 Warn。</summary>
    private static int _timeoutLogged;

    /// <summary>
    /// 任何层级都跳过的目录名。按名字整段比较，不做子串匹配 ——
    /// "bin" 要跳，但 "Robin" 是用户的正常文件夹，不能连坐。
    /// </summary>
    private static readonly HashSet<string> SkipDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>
    /// 启动时调一次：能载入缓存就立刻让旧索引可用；过期或根目录变了再后台重建。
    /// 幂等、非阻塞。这里只拉起线程，所有 I/O 都在后台做 —— 启动路径上读 20MB 索引
    /// 足够让首帧卡一下，而用户对"打开软件慢"的容忍度远低于"搜索慢半秒"。
    /// </summary>
    internal static void Start()
    {
        try
        {
            /* 自检会构造大量测试数据，绝不能顺手把用户整块盘扫一遍。
             * 显式设置 INPUTPLUS_TEST_INDEX=1 时才允许，给"自检里验证真实索引"留口子。 */
            if (IsSelfTestProcess() && !TestIndexEnabled()) return;

            lock (Gate)
            {
                if (Volatile.Read(ref _started) == 1) return;
                Volatile.Write(ref _started, 1);
                StartWorkerLocked(loadCacheFirst: true);
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "启动文件索引"); }
    }

    /// <summary>设置页的「重建索引」。旧索引在重建完成前继续可用，不会出现空窗期。</summary>
    internal static void RequestRebuild()
    {
        try
        {
            if (IsSelfTestProcess() && !TestIndexEnabled()) return;

            lock (Gate)
            {
                Volatile.Write(ref _started, 1);
                StartWorkerLocked(loadCacheFirst: false, force: true);
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "重建文件索引"); }
    }

    /// <summary>退出时取消后台任务。快照不清空：万一还有收尾逻辑要搜一下，照样能用。</summary>
    internal static void Stop()
    {
        try
        {
            lock (Gate)
            {
                Volatile.Write(ref _started, 0);
                _generation++;
                var cts = _cts;
                _cts = null;
                cts?.Cancel();
                Volatile.Write(ref _building, 0);
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "停止文件索引"); }
    }

    /// <summary>
    /// UI 线程同步搜索。query 为空或只有 1 个字符直接返回空表（一个字符会命中几万条，
    /// 对用户没有信息量，还白烧 CPU）。超时带着已有结果返回，绝不抛异常。
    /// </summary>
    internal static IReadOnlyList<FileSearchHit> Search(string query, int limit, int timeoutMs)
    {
        try
        {
            var snap = _snap;
            if (snap == null) return Array.Empty<FileSearchHit>();
            return SearchCore(snap, query, limit, timeoutMs, reportTimeout: true);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "文件索引搜索");
            return Array.Empty<FileSearchHit>();
        }
    }

    /// <summary>
    /// 自检专用：只索引给定根目录，结果不写盘、不影响全局快照。
    /// 每次调用都重新扫一遍夹具树（200 个文件，代价可以忽略），
    /// 这样自检之间不会互相污染，也不会因为上一次失败留下半个索引。
    /// </summary>
    internal static IReadOnlyList<FileSearchHit> SearchInForTest(string root, string query, int limit)
        => SearchInForTest(root, query, limit, 5000);

    /// <summary>
    /// 自检专用重载：额外指定超时，用来验证"1ms 也不抛异常"。
    /// 公开签名没有 timeout 参数，所以单独留一个 internal 入口，别让调用方猜。
    /// </summary>
    internal static IReadOnlyList<FileSearchHit> SearchInForTest(string root, string query, int limit, int timeoutMs)
    {
        try
        {
            string? normalized = NormalizeRoot(root);
            if (normalized == null) return Array.Empty<FileSearchHit>();

            var outcome = Build(new[] { normalized }, CancellationToken.None, null);
            if (outcome.Snapshot == null) return Array.Empty<FileSearchHit>();
            return SearchCore(outcome.Snapshot, query, limit, timeoutMs, reportTimeout: false);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "文件索引自检搜索");
            return Array.Empty<FileSearchHit>();
        }
    }


    /// <summary>
    /// 结构化查询入口（语义层用）。spec 没有任何约束时原样交给 SearchCore ——
    /// 保住了红线「同样的输入得到同样的结果集」，也让新能力全部靠语法开启。
    /// </summary>
    internal static IReadOnlyList<FileSearchHit> SearchSpec(QbarSearchSpec spec, int limit, int timeoutMs)
    {
        try
        {
            var snap = _snap;
            if (snap == null || spec == null) return Array.Empty<FileSearchHit>();
            return SearchSpecCore(snap, spec, limit, timeoutMs, reportTimeout: true);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "结构化文件搜索");
            return Array.Empty<FileSearchHit>();
        }
    }

    /// <summary>
    /// 把一条 <see cref="QbarSearchSpec"/> 的判定逻辑打包成可复用的谓词。
    ///
    /// 为什么需要它：Everything 只懂它自己的语法（ext:/dm:/size:/path:），
    /// wfn:（glob）、regex:、文件夹与文件的分流、否定词的边界，它都不按我们的语义解释。
    /// 所以后端流程是「Everything 出候选 → 这里逐条复核 → 交引擎打分」，
    /// 保证同一个查询走 Everything 和走自建索引得到的是**同一批**结果。
    ///
    /// 构造时把扩展名集合、正则、分词结果全部预编译一次：候选窗口 2000 条，
    /// 每条都要过一遍谓词，逐条重建 HashSet 会让复核本身变成瓶颈。
    /// </summary>
    internal sealed class SpecFilter
    {
        private static readonly char[] Separators = { '\\', '/' };

        private readonly QbarSearchSpec _spec;
        private readonly string[] _tokens;
        private readonly Regex? _regex;
        private readonly HashSet<uint>? _extKeys;
        private readonly HashSet<uint>? _negExtKeys;

        internal SpecFilter(QbarSearchSpec spec)
        {
            _spec = spec;
            _tokens = BuildSpecTokens(spec);
            _regex = CompileSpecRegex(spec.RegexText);
            _extKeys = BuildExtKeys(spec.Extensions);
            _negExtKeys = BuildExtKeys(spec.NegExtensions);
        }

        internal int TokenCount => _tokens.Length;

        /// <summary>一条外部结果（来自 Everything）是否等价于自建索引会返回的结果。</summary>
        internal bool Accept(string fullPath, bool isDir, int sizeKb, int day, out int score)
        {
            score = 0;
            if (string.IsNullOrEmpty(fullPath)) return false;
            if (!SizeDayAccept(sizeKb, day)) return false;

            string dir;
            ReadOnlySpan<char> leaf;
            if (isDir)
            {
                dir = fullPath;
                leaf = LastSegment(fullPath.AsSpan());
            }
            else
            {
                int slash = fullPath.LastIndexOfAny(Separators);
                dir = slash > 0 ? fullPath.Substring(0, slash) : string.Empty;
                ReadOnlySpan<char> span = fullPath.AsSpan();
                leaf = slash >= 0 ? span.Slice(slash + 1) : span;
            }
            if (leaf.Length == 0 || leaf.Length > MaxNameChars) return false;

            if (!isDir)
            {
                if (_extKeys != null && _extKeys.Count > 0)
                {
                    uint key = PackExt(LastExtension(leaf));
                    if (key == 0 || !_extKeys.Contains(key)) return false;
                }
                if (_negExtKeys != null && _negExtKeys.Count > 0)
                {
                    uint key = PackExt(LastExtension(leaf));
                    if (key != 0 && _negExtKeys.Contains(key)) return false;
                }

                if (!PathPinMatch(dir, leaf, _spec)) return false;

                for (int i = 0; i < _spec.NegTerms.Count; i++)
                {
                    string neg = _spec.NegTerms[i];
                    if (neg.Length == 0) continue;
                    if (ContainsAscii(leaf, neg)) return false;
                    if (dir.IndexOf(neg, StringComparison.OrdinalIgnoreCase) >= 0) return false;
                }
            }
            else
            {
                /* 目录没有扩展名也没有大小：查询一旦带了 ext:/!ext:/size: 或显式 file:，
                 * 目录就不该出现。这条规则必须和 SearchSpecCore 的 dirsEligible 一致，
                 * 否则同一个查询换后端（Everything ↔ 自建索引）会多出一批目录。 */
                if (_spec.FilesOnly) return false;
                if ((_extKeys != null && _extKeys.Count > 0) || (_negExtKeys != null && _negExtKeys.Count > 0)) return false;
                if (_spec.MinSize >= 0 || _spec.MaxSize >= 0) return false;

                if (!PathPinMatch(dir, ReadOnlySpan<char>.Empty, _spec)) return false;

                for (int i = 0; i < _spec.NegTerms.Count; i++)
                {
                    string neg = _spec.NegTerms[i];
                    if (neg.Length == 0) continue;
                    if (dir.IndexOf(neg, StringComparison.OrdinalIgnoreCase) >= 0) return false;
                }
            }

            if (_spec.Glob.Length > 0 && !GlobMatch(leaf, _spec.Glob)) return false;
            if (_regex != null && !SafeRegexMatch(_regex, leaf, dir)) return false;

            if (isDir)
            {
                if (_tokens.Length == 0)
                {
                    score = 500 - leaf.Length / 8 - DepthOf(dir) * 8;
                    return true;
                }

                for (int t = 0; t < _tokens.Length; t++)
                {
                    if (dir.IndexOf(_tokens[t], StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    return false;
                }

                if (EqualsAscii(leaf, _spec.PlainQuery)) score = 900;
                else if (ContainsAnyAscii(leaf, _tokens)) score = 420;
                else return false;

                score -= leaf.Length / 8;
                score -= DepthOf(dir) * 8;
                return true;
            }

            if (_tokens.Length > 0)
            {
                int matched = ScoreFile(leaf, dir, _spec.PlainQuery, _tokens);
                if (matched == NoScore) return false;
                score = matched;
                return true;
            }

            score = 500 - leaf.Length / 8 - DepthOf(dir) * 8;
            return true;
        }

        /// <summary>
        /// 大小/时间约束。索引里存的是 KB 与"自 2000-01-01 起的天数"，
        /// Everything 返回的是字节与 FILETIME，调用方已经换算过，这里按同一口径比较。
        /// </summary>
        private bool SizeDayAccept(int sizeKb, int day)
        {
            if (_spec.MinSize < 0 && _spec.MaxSize < 0 && _spec.MinDay < 0 && _spec.MaxDay < 0) return true;

            if (_spec.MinSize >= 0 || _spec.MaxSize >= 0)
            {
                long size = (long)sizeKb * 1024L;
                if (_spec.MinSize >= 0 && size < _spec.MinSize) return false;
                if (_spec.MaxSize >= 0 && size > _spec.MaxSize) return false;
            }

            if (_spec.MinDay >= 0 || _spec.MaxDay >= 0)
            {
                if (day < 0 || day == QbarSyntaxValues.UnknownDay) return false;
                if (_spec.MinDay >= 0 && day < _spec.MinDay) return false;
                if (_spec.MaxDay >= 0 && day > _spec.MaxDay) return false;
            }

            return true;
        }
    }

    /// <summary>单条路径的便捷判定（测试与命令行诊断用；批量复核走 <see cref="SpecFilter"/>）。</summary>
    internal static bool AcceptPath(
        QbarSearchSpec spec, string fullPath, bool isDir, int sizeKb, int day, out int score)
    {
        score = 0;
        if (spec == null || string.IsNullOrEmpty(fullPath)) return false;
        return new SpecFilter(spec).Accept(fullPath, isDir, sizeKb, day, out score);
    }


    /// <summary>
    /// 只把含 CJK 的名字交给回调（拼音索引专用的采集口）。
    /// 判断放在字节层：UTF-8 里 CJK 的首字节一定 >= 0xE0，先按字节筛一遍，
    /// 80 万条名字里只解码那几万条，构建才不至于变成一次全量 GC 风暴。
    /// 返回实际访问的名字条数；超时/取消靠 budget 退出。
    /// </summary>
    internal static int CollectCjkNames(Action<string, string> visit, int timeoutMs)
    {
        try
        {
            var snap = _snap;
            if (snap == null || visit == null) return 0;

            var budget = new SearchBudget(timeoutMs);
            int visited = 0;
            int count = snap.NameStart.Length;

            for (int i = 0; i < count; i++)
            {
                if ((i & 0x3FF) == 0 && budget.Expired()) break;

                int start = snap.NameStart[i];
                int end = NameEnd(snap, i);
                if (end <= start) continue;

                bool maybeCjk = false;
                for (int b = start; b < end; b++)
                {
                    if (snap.Names[b] >= 0xE0)
                    {
                        maybeCjk = true;
                        break;
                    }
                }
                if (!maybeCjk) continue;

                string name = DecodeName(snap, i);
                if (name.Length == 0) continue;
                visit(name, snap.Dirs[snap.NameDir[i]]);
                visited++;
            }

            /* 目录名也要收：启动器里"设计稿"这种文件夹和文件一样常被拼音找。
             * 目录串本身就在快照里，这里只按最后一个路径段判断含不含 CJK，
             * 命中不了就走 Path.GetFileName（见 DecodeDirLeaf）。 */
            int dirCount = snap.Dirs.Length;
            for (int i = 0; i < dirCount; i++)
            {
                if ((i & 0x3FF) == 0 && budget.Expired()) break;

                string dir = snap.Dirs[i];
                if (dir.Length == 0) continue;

                bool maybeCjk = false;
                for (int c = 0; c < dir.Length; c++)
                {
                    if (dir[c] >= 0x2E80)
                    {
                        maybeCjk = true;
                        break;
                    }
                }
                if (!maybeCjk) continue;

                string leaf = System.IO.Path.GetFileName(dir);
                if (leaf.Length == 0) continue;
                string parent = System.IO.Path.GetDirectoryName(dir) ?? "";
                if (parent.Length == 0) continue;
                visit(leaf, parent);
                visited++;
            }

            return visited;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "采集 CJK 文件名");
            return 0;
        }
    }

    /// <summary>
    /// 子序列兜底扫描（rprt → report）。原生索引是按子串建的，没有模糊能力，
    /// 所以这条路径只能顺序解码名字做匹配 —— 用 timeoutMs 和 scanLimit 双保险，
    /// 宁可少返回也不许把搜索线程拖死。
    /// </summary>
    internal static IReadOnlyList<FileSearchHit> FuzzyScan(string needle, int scanLimit, int limit, int timeoutMs)
    {
        try
        {
            var snap = _snap;
            if (snap == null || string.IsNullOrEmpty(needle) || limit <= 0) return Array.Empty<FileSearchHit>();
            if (needle.Length < 2 || needle.Length > 32) return Array.Empty<FileSearchHit>();

            var budget = new SearchBudget(timeoutMs);
            var hits = new List<FileSearchHit>(Math.Min(128, limit * 4));
            int count = snap.NameStart.Length;
            if (scanLimit > 0 && scanLimit < count) count = scanLimit;

            for (int i = 0; i < count; i++)
            {
                if ((i & 0xFF) == 0 && budget.Expired()) break;

                int start = snap.NameStart[i];
                int end = NameEnd(snap, i);
                if (end <= start) continue;

                string name = DecodeName(snap, i);
                if (name.Length < needle.Length) continue;

                int score = SubsequenceScore(name, needle);
                if (score <= 0) continue;

                string dir = snap.Dirs[snap.NameDir[i]];
                score -= name.Length / 8;
                score -= DepthOf(dir) * 2;
                AddHit(hits, new FileSearchHit(Path.Combine(dir, name), false, score), limit);
                if (hits.Count >= limit * 4) break;
            }

            if (hits.Count == 0) return Array.Empty<FileSearchHit>();
            SortAndTrim(hits, limit);
            return hits;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "模糊扫描");
            return Array.Empty<FileSearchHit>();
        }
    }

    /// <summary>
    /// 子序列匹配打分：连续命中给得高，跳得越多扣得越多，词首命中额外奖励。
    /// 返回 0 表示不匹配。
    /// </summary>
    private static int SubsequenceScore(string text, string needle)
    {
        int ti = 0;
        int hits = 0;
        int gaps = 0;
        int boundary = 0;
        int last = -1;

        for (int ni = 0; ni < needle.Length; ni++)
        {
            char want = needle[ni];
            bool found = false;
            while (ti < text.Length)
            {
                char c = text[ti];
                if (char.ToLowerInvariant(c) == char.ToLowerInvariant(want))
                {
                    found = true;
                    hits++;
                    if (last >= 0 && ti == last + 1) boundary++;
                    if (ti == 0 || !char.IsLetterOrDigit(text[ti - 1])) boundary++;
                    last = ti;
                    ti++;
                    break;
                }
                ti++;
                gaps++;
            }
            if (!found) return 0;
        }

        int score = 300 + hits * 8 + boundary * 10 - gaps * 2;
        return score < 1 ? 1 : score;
    }
    /// <summary>
    /// 自检专用：把给定根目录建成索引并**发布成全局快照**。
    /// 引擎、拼音索引、模糊扫描这些走的是全局状态（_snap），而 SearchInForTest 系列
    /// 刻意只查临时快照。没有这个入口，那几个用例只能查一份空索引。
    /// </summary>
    internal static int PublishForTest(string root)
    {
        try
        {
            string? normalized = NormalizeRoot(root);
            if (normalized == null) return 0;
            var outcome = Build(new[] { normalized }, CancellationToken.None, null);
            if (outcome.Snapshot == null) return 0;
            if (!TryPublish(_generation, outcome.Snapshot, ReadyStatus(outcome.Snapshot, "（自检）")))
                return 0;
            return outcome.Snapshot.NameStart.Length;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "自检索引发布");
            return 0;
        }
    }
    /// <summary>自检专用：在给定根目录的临时索引上跑结构化查询。</summary>
    internal static IReadOnlyList<FileSearchHit> SearchSpecInForTest(
        string root, QbarSearchSpec spec, int limit, int timeoutMs)
    {
        try
        {
            string? normalized = NormalizeRoot(root);
            if (normalized == null || spec == null) return Array.Empty<FileSearchHit>();
            var outcome = Build(new[] { normalized }, CancellationToken.None, null);
            if (outcome.Snapshot == null) return Array.Empty<FileSearchHit>();
            return SearchSpecCore(outcome.Snapshot, spec, limit, timeoutMs, reportTimeout: false);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "结构化搜索自检");
            return Array.Empty<FileSearchHit>();
        }
    }
    /// <summary>
    /// 自检专用：建索引 → 存盘 → 丢掉内存副本 → 从盘上读回来，回报读回来的
    /// 文件数 / 目录数。写的位置由调用方给（临时目录），不碰真实 IndexDir。
    /// </summary>
    internal static void SaveLoadForTest(string dir, string[] roots, out int files, out int dirs2)
    {
        files = 0;
        dirs2 = 0;
        try
        {
            var normalized = NormalizeRoots(roots);
            int builtFiles = 0;
            int builtDirs = 0;
            {
                var built = Build(normalized, CancellationToken.None, null);
                if (built.Snapshot == null) return;

                builtFiles = built.Snapshot.NameStart.Length;
                builtDirs = built.Snapshot.Dirs.Length;
                if (!SaveSnapshot(built.Snapshot, dir)) return;
            }

            /* built 已经离开作用域，接下来只从盘上读。读回来的结果和内存里那份
             * 完全无关，才是真的验证了持久化。 */
            var loaded = TryLoad(dir);
            if (loaded == null) return;

            files = loaded.NameStart.Length;
            dirs2 = loaded.Dirs.Length;
            if (files != builtFiles || dirs2 != builtDirs)
                CoreLog.Warn($"文件索引自检：存盘前后计数不一致（建 {builtFiles}/{builtDirs}，读 {files}/{dirs2}）");
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "文件索引存取自检");
        }
    }

    private static bool TestIndexEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("INPUTPLUS_TEST_INDEX"), "1",
            StringComparison.Ordinal);

    /// <summary>
    /// 本进程是不是在跑自检。不能只看 <see cref="AppPaths.DataDirIsSelfTest"/>：
    /// 验收脚本会同时带 --selftest 和 INPUTPLUS_DATA，这时 AppPaths 走的是
    /// "外部指定数据目录"分支，DataDirIsSelfTest 反而是 false，于是自检会真的
    /// 去扫用户整块盘（25MB 索引、几十秒、还可能和别的自检抢磁盘）。
    /// 命令行里有 --selftest 就一定按自检处理，除非显式 INPUTPLUS_TEST_INDEX=1。
    /// </summary>
    private static bool IsSelfTestProcess()
    {
        if (AppPaths.DataDirIsSelfTest) return true;

        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--selftest", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>必须在 <see cref="Gate"/> 里调用。取消旧任务、起新线程，立刻返回。</summary>
    /// <param name="force">
    /// 用户点「重建索引」时传 true。缓存还没过期也必须真的重建，否则 Worker 会按
    /// “不陈旧”直接返回，按钮看起来像没反应。
    /// </param>
    private static void StartWorkerLocked(bool loadCacheFirst, bool force = false)
    {
        var old = _cts;
        _cts = null;
        old?.Cancel();

        var cts = new CancellationTokenSource();
        _cts = cts;
        int generation = ++_generation;
        Volatile.Write(ref _building, 1);
        SetStatus(loadCacheFirst ? "正在载入文件索引缓存…" : "正在重建文件索引…");

        var thread = new Thread(() => Worker(generation, cts, loadCacheFirst, force))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "InputPlus 文件索引",
        };
        thread.Start();
    }

    /// <summary>
    /// 后台任务主体：先试着载入旧缓存，再决定要不要重建。
    /// 旧缓存可用时先发布，重建失败也不会把用户打回"没有索引"。
    /// </summary>
    private static void Worker(int generation, CancellationTokenSource cts, bool loadCacheFirst, bool force)
    {
        try
        {
            string dir = IndexDir;

            if (loadCacheFirst)
            {
                SetStatus("正在载入文件索引缓存…");
                var cached = TryLoad(dir);
                if (Cancelled(generation, cts)) return;

                if (cached != null)
                {
                    if (!TryPublish(generation, cached, ReadyStatus(cached, ""))) return;
                }
                else
                {
                    ClearIndexDir(dir);
                }
            }

            if (Cancelled(generation, cts)) return;

            string[] roots = ComputeRoots();
            if (Cancelled(generation, cts)) return;
            _roots = roots;

            var current = _snap;
            bool stale = force
                         || current == null
                         || current.NameStart.Length == 0
                         || DateTime.Now - current.BuiltAt > CacheMaxAge
                         || !SameRoots(current.Roots, roots);

            if (!stale)
            {
                Snapshot fresh = current!;
                Volatile.Write(ref _liveFiles, fresh.NameStart.Length);
                Volatile.Write(ref _liveDirs, fresh.Dirs.Length);
                SetStatus(ReadyStatus(fresh, ""));
                return;
            }

            var outcome = Build(roots, cts.Token, (fileCount, dirCount) =>
            {
                Volatile.Write(ref _liveFiles, fileCount);
                Volatile.Write(ref _liveDirs, dirCount);
                SetStatus("正在建立索引：" + FormatCount(fileCount + dirCount) + "项…");
            });

            if (Cancelled(generation, cts) || outcome.Cancelled) return;
            if (outcome.Snapshot == null)
            {
                string message = string.IsNullOrEmpty(outcome.Error) ? "没有可索引的目录" : outcome.Error!;
                SetProblem("文件索引建立失败：" + message);
                SetStatus("文件索引建立失败：" + message);
                CoreLog.Warn("文件索引建立失败：" + message);
                return;
            }

            string note = outcome.Capped ? "（已达上限：" + outcome.StopReason + "）" : "";
            if (!TryPublish(generation, outcome.Snapshot, ReadyStatus(outcome.Snapshot, note))) return;
            SetProblem("");

            if (!SaveSnapshot(outcome.Snapshot, dir))
            {
                SetProblem("索引已建立，但缓存写入失败");
                SetStatus("索引已建立，但缓存写入失败（下次启动会重新建立）");
            }
        }
        catch (Exception ex)
        {
            SetProblem("文件索引异常：" + ex.Message);
            SetStatus("文件索引异常：" + ex.Message);
            CoreLog.Warn("文件索引异常：" + ex.Message);
        }
        finally
        {
            lock (Gate)
            {
                if (ReferenceEquals(_cts, cts)) _cts = null;
            }
            cts.Dispose();

            if (generation == Volatile.Read(ref _generation))
                Volatile.Write(ref _building, 0);
        }
    }

    private static bool Cancelled(int generation, CancellationTokenSource cts)
        => cts.IsCancellationRequested || generation != Volatile.Read(ref _generation);

    /// <summary>
    /// 发布新快照。锁 <see cref="Gate"/> 是为了和"取消旧任务 / 生成新代号"互斥：
    /// 旧任务即使已经检查完取消标志，也不能在新任务启动之后再把旧索引盖回来。
    /// 锁只包住几次引用赋值，Search 完全不经过这里。
    /// </summary>
    private static bool TryPublish(int generation, Snapshot snapshot, string status)
    {
        lock (Gate)
        {
            if (generation != _generation) return false;

            Volatile.Write(ref _liveFiles, snapshot.NameStart.Length);
            Volatile.Write(ref _liveDirs, snapshot.Dirs.Length);
            Volatile.Write(ref _builtAtTicks, snapshot.BuiltAt.Ticks);
            _roots = snapshot.Roots;
            _snap = snapshot;
            Interlocked.Exchange(ref _timeoutLogged, 0);
            SetStatus(status);
            return true;
        }
    }

    private static string ReadyStatus(Snapshot snapshot, string note)
        => "文件索引就绪：" + FormatCount(snapshot.NameStart.Length) + "个文件、"
           + FormatCount(snapshot.Dirs.Length) + "个目录" + note;

    private static void SetStatus(string status) => _status = status;

    private static void SetProblem(string problem) => _problem = problem;

    // ───────────────────────────── 建索引 ─────────────────────────────

    /// <summary>
    /// 扫盘主循环。显式栈 DFS：递归在深目录树上会把线程栈打穿，而且没法在
    /// 中途停下来保存"已经扫到哪"；栈里放目录路径和它在 _dirs 里的下标。
    ///
    /// 不用 Directory.GetFiles(..., AllDirectories)：那个 API 会先把整层结果
    /// 灌进一个 string[]，80 万文件就是几十 MB 的瞬时分配，正好卡在用户打字的时候。
    /// </summary>
    private static BuildOutcome Build(string[] roots, CancellationToken token, Action<int, int>? progress)
    {
        var outcome = new BuildOutcome();
        var dirs = new List<string>(4096);
        var starts = new List<int>(16384);
        var nameDirs = new List<int>(16384);
        var sizes = new List<uint>(16384);
        var days = new List<ushort>(16384);
        var stream = new MemoryStream(1 << 20);
        var stack = new Stack<DirEntry>(256);
        var watch = Stopwatch.StartNew();
        int lastReport = 0;

        try
        {
            for (int i = 0; i < roots.Length; i++)
            {
                string root = roots[i];
                if (!Directory.Exists(root)) continue;
                int index = dirs.Count;
                dirs.Add(root);
                stack.Push(new DirEntry(root, index));
            }

            if (stack.Count == 0)
            {
                outcome.Snapshot = MakeSnapshot(dirs, stream, starts, nameDirs, sizes, days, roots);
                return outcome;
            }

            while (stack.Count > 0)
            {
                if (token.IsCancellationRequested) { outcome.Cancelled = true; break; }
                if (watch.Elapsed > MaxBuildTime)
                {
                    outcome.Capped = true;
                    outcome.StopReason = "单次构建超过 240 秒";
                    break;
                }

                var current = stack.Pop();
                int inDir = 0;

                /* 枚举本身也可能在中途抛（目录被删、权限被改）。放在 try 里，
                 * 已经处理过的条目保留，未处理的放弃 —— 一个目录不该让整份索引失败。 */
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(current.Path))
                    {
                        if (token.IsCancellationRequested) { outcome.Cancelled = true; break; }
                        inDir++;
                        if ((inDir & 0xFF) == 0 && watch.Elapsed > MaxBuildTime)
                        {
                            outcome.Capped = true;
                            outcome.StopReason = "单次构建超过 240 秒";
                            break;
                        }

                        /* 用 FileInfo 而不是 File.GetAttributes：Length / LastWriteTime
                         * 和 Attributes 来自同一次 stat，等于顺手把 size 和日期也拿到了。
                         * 单独再 stat 一次会让 80 万文件的建索引时间多出一截。 */
                        FileAttributes attrs;
                        long length = 0;
                        DateTime modified = DateTime.MinValue;
                        try
                        {
                            var info = new FileInfo(entry);
                            attrs = info.Attributes;
                            if ((attrs & FileAttributes.Directory) == 0)
                            {
                                length = info.Length;
                                modified = info.LastWriteTime;
                            }
                        }
                        catch { continue; }

                        // System 属性多是系统还原点、卷影副本之类的壳子；Hidden 保留，
                        // 用户自己藏的文件夹照样应该能搜到。
                        if ((attrs & FileAttributes.System) != 0) continue;

                        string name = Path.GetFileName(entry);
                        bool isDir = (attrs & FileAttributes.Directory) != 0;

                        if (isDir)
                        {
                            // ReparsePoint 可能是目录联接。跟进去就是无限循环，直接断在这里。
                            if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                            if (name.Length == 0 || SkipDirNames.Contains(name)) continue;
                            if (dirs.Count >= MaxDirs)
                            {
                                outcome.Capped = true;
                                outcome.StopReason = "目录数已达上限 " + MaxDirs.ToString(CultureInfo.InvariantCulture);
                                break;
                            }

                            int index = dirs.Count;
                            dirs.Add(entry);
                            stack.Push(new DirEntry(entry, index));
                        }
                        else
                        {
                            if (name.Length == 0) continue;
                            if (starts.Count >= MaxFiles)
                            {
                                outcome.Capped = true;
                                outcome.StopReason = "文件数已达上限 " + MaxFiles.ToString(CultureInfo.InvariantCulture);
                                break;
                            }

                            byte[] bytes = Encoding.UTF8.GetBytes(name);
                            if (bytes.Length == 0) continue;

                            starts.Add((int)stream.Length);
                            stream.Write(bytes, 0, bytes.Length);
                            stream.WriteByte(0x0A);
                            nameDirs.Add(current.Index);
                            /* KB 粒度足够回答 size:>1mb 这类问题，4 字节/条也放得下；
                             * 超过 4TB 的单文件饱和处理，不值得为它换成 8 字节。 */
                            long kb = length / 1024;
                            sizes.Add(kb <= 0 ? 0u : (kb > uint.MaxValue ? uint.MaxValue : (uint)kb));
                            days.Add((ushort)QbarSyntaxValues.ToDay(modified));
                        }

                        int total = starts.Count + dirs.Count;
                        if (total - lastReport >= 2000)
                        {
                            lastReport = total;
                            progress?.Invoke(starts.Count, dirs.Count);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException
                                           || ex is UnauthorizedAccessException
                                           || ex is SecurityException)
                {
                    /* 预期内：系统目录、被占用的目录、枚举途中被删的目录。
                     * 不写日志：一次扫盘能碰上几百个，写日志反而把真问题埋了。 */
                }

                if (outcome.Cancelled || outcome.Capped) break;
            }

            if (outcome.Cancelled) return outcome;

            progress?.Invoke(starts.Count, dirs.Count);
            outcome.Snapshot = MakeSnapshot(dirs, stream, starts, nameDirs, sizes, days, roots);
        }
        catch (Exception ex)
        {
            outcome.Error = ex.Message;
        }
        finally
        {
            stream.Dispose();
        }

        return outcome;
    }

    private static Snapshot MakeSnapshot(
        List<string> dirs,
        MemoryStream names,
        List<int> starts,
        List<int> nameDirs,
        List<uint> sizes,
        List<ushort> days,
        string[] roots)
        => new Snapshot(
            dirs.ToArray(),
            names.ToArray(),
            starts.ToArray(),
            nameDirs.ToArray(),
            sizes.ToArray(),
            days.ToArray(),
            DateTime.Now,
            roots);

    /// <summary>
    /// 计算默认根目录。顺序是有意的：先用户自己的东西，再公共/程序目录，
    /// 提权后才把整盘加进来。去重分两步：完全相同的路径只留一份；
    /// 某个根已经包含另一个根时，丢掉被包含的那个 —— 否则 80 万文件会被扫两遍，
    /// 结果里也会出现两条一模一样的路径。
    /// </summary>
    private static string[] ComputeRoots()
    {
        var candidates = new List<string>(16);

        try
        {
            string profile = NormalizeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "";
            AddCandidate(candidates, profile);

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
            AddCandidate(candidates, publicDir);

            AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

            /* 提权才加盘根：普通权限下扫 C:\ 会被大量拒绝访问，又慢又搜不到东西。
             * 盘根会包含前面那些细根，后面的折叠逻辑会把细根去掉。 */
            if (IsElevated())
            {
                try
                {
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        try
                        {
                            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                            AddCandidate(candidates, drive.RootDirectory.FullName);
                        }
                        catch { /* 盘刚好被拔掉 / 没就绪，跳过这一块 */ }
                    }
                }
                catch (Exception ex) { CoreLog.Swallow(ex, "枚举固定磁盘"); }
            }
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
        string? root = NormalizeRoot(path);
        if (root == null) return;
        if (profile.Length > 0 && IsSameOrUnder(root, profile)) return;
        AddCandidate(list, root);
    }

    private static void AddCandidate(List<string> list, string? path)
    {
        string? root = NormalizeRoot(path);
        if (root == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], root, StringComparison.OrdinalIgnoreCase)) return;
        }
        list.Add(root);
    }

    private static string[] NormalizeRoots(string[]? roots)
    {
        if (roots == null || roots.Length == 0) return Array.Empty<string>();
        var result = new List<string>(roots.Length);
        for (int i = 0; i < roots.Length; i++) AddCandidate(result, roots[i]);
        return result.ToArray();
    }

    /// <summary>把路径规范成绝对路径并去掉末尾分隔符（盘根 "C:\" 例外）。不存在就返回 null。</summary>
    private static string? NormalizeRoot(string? path)
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
    private static bool IsSameOrUnder(string path, string root)
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

    private static bool IsElevated()
    {
        try
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "判断管理员权限");
            return false;
        }
    }

    private static bool SameRoots(string[] a, string[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    // ───────────────────────────── 搜索 ─────────────────────────────

    private static IReadOnlyList<FileSearchHit> SearchCore(
        Snapshot snapshot,
        string? query,
        int limit,
        int timeoutMs,
        bool reportTimeout)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<FileSearchHit>();
        string trimmed = query.Trim();
        if (trimmed.Length < 2) return Array.Empty<FileSearchHit>();
        if (limit <= 0) return Array.Empty<FileSearchHit>();

        /* 界面最多传 60。这里再兜一个上限，免得某个调用方传 int.MaxValue
         * 进来，翻倍/乘法直接溢出，或者一次查询把内存打满。 */
        if (limit > 4096) limit = 4096;

        string[] tokens = Tokenize(trimmed);
        if (tokens.Length == 0) return Array.Empty<FileSearchHit>();

        var hits = new List<FileSearchHit>(Math.Min(256, Math.Max(16, limit * 4)));
        var budget = new SearchBudget(timeoutMs);

        ScanFiles(snapshot, trimmed, tokens, limit, hits, budget);
        if (!budget.TimedOut) ScanDirs(snapshot, trimmed, tokens, limit, hits, budget);

        if (hits.Count == 0)
        {
            // 自检里的 1ms 超时只验证"不抛异常"，不能顺手把全局 Status 改成"搜索超时"。
            if (budget.TimedOut && reportTimeout) NoteSearchTimeout();
            return Array.Empty<FileSearchHit>();
        }

        SortAndTrim(hits, limit);
        return hits;
    }

    /// <summary>按空白切词，最多 4 个；多出来的直接丢，用户不会真的靠第 5 个词过滤。</summary>
    private static string[] Tokenize(string query)
    {
        var tokens = new List<string>(4);
        int i = 0;
        while (i < query.Length && tokens.Count < 4)
        {
            while (i < query.Length && char.IsWhiteSpace(query[i])) i++;
            if (i >= query.Length) break;

            int start = i;
            while (i < query.Length && !char.IsWhiteSpace(query[i])) i++;
            if (i > start) tokens.Add(query.Substring(start, i - start));
        }
        return tokens.ToArray();
    }

    // ─────────────────────── 结构化查询（语义层） ───────────────────────

    private static IReadOnlyList<FileSearchHit> SearchSpecCore(
        Snapshot snapshot,
        QbarSearchSpec spec,
        int limit,
        int timeoutMs,
        bool reportTimeout)
    {
        if (limit <= 0) return Array.Empty<FileSearchHit>();
        if (limit > 4096) limit = 4096;

        /* 一个约束都没有 —— 这就是改造前的普通搜索。必须原路走，
         * 连分档权重和 .exe 加成都不许变，否则老用户会觉得结果莫名其妙地换了一批。 */
        if (spec.IsPlain)
            return SearchCore(snapshot, spec.PlainQuery, limit, timeoutMs, reportTimeout);

        string[] tokens = BuildSpecTokens(spec);
        Regex? regex = CompileSpecRegex(spec.RegexText);
        var hits = new List<FileSearchHit>(Math.Min(256, Math.Max(16, limit * 4)));
        var budget = new SearchBudget(timeoutMs);

        if (!spec.FoldersOnly)
            ScanSpecFiles(snapshot, spec, tokens, regex, limit, hits, budget);

        /* 目录没有扩展名、也没有大小，所以一旦查询里带了这两种约束，目录就不该出现。
         * 没有显式 folder: 时也保留目录命中，和改造前一致。 */
        bool dirsEligible = !spec.FilesOnly
                            && spec.Extensions.Count == 0 && spec.NegExtensions.Count == 0
                            && spec.MinSize < 0 && spec.MaxSize < 0;
        if (dirsEligible && !budget.TimedOut)
            ScanSpecDirs(snapshot, spec, tokens, regex, limit, hits, budget);

        if (hits.Count == 0)
        {
            if (budget.TimedOut && reportTimeout) NoteSearchTimeout();
            return Array.Empty<FileSearchHit>();
        }

        SortAndTrim(hits, limit);
        return hits;
    }

    /// <summary>参与相关性打分的词：普通词 + 短语（短语里的空格是名字的一部分，不能拆）。</summary>
    private static string[] BuildSpecTokens(QbarSearchSpec spec)
    {
        if (spec.Phrases.Count == 0) return spec.Terms.ToArray();

        var list = new List<string>(spec.Terms.Count + spec.Phrases.Count);
        list.AddRange(spec.Terms);
        list.AddRange(spec.Phrases);
        return list.ToArray();
    }

    private static Regex? CompileSpecRegex(string pattern)
    {
        if (pattern.Length == 0) return null;
        try
        {
            /* 用户写的正则可能灾难性回溯，250ms 上限是兜底：宁可这条查询不返回，
             * 也不能把搜索线程钉死在 CatastrophicBacktracking 里。 */
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void ScanSpecFiles(
        Snapshot snapshot,
        QbarSearchSpec spec,
        string[] tokens,
        Regex? regex,
        int limit,
        List<FileSearchHit> hits,
        SearchBudget budget)
    {
        int[] starts = snapshot.NameStart;
        if (starts.Length == 0) return;

        HashSet<uint>? extKeys = BuildExtKeys(spec.Extensions);
        HashSet<uint>? negExtKeys = BuildExtKeys(spec.NegExtensions);
        bool anyExt = (extKeys != null && extKeys.Count > 0) || (negExtKeys != null && negExtKeys.Count > 0);

        /* 只有谓词时，每一条通过的候选分值只差"名字长度 / 目录深度"这几十分，
         * 收集到足够多条之后继续扫全盘（几十万条）对最终窗口毫无意义 —— 只是把
         * 整表解码一遍。这里早退：候选窗口 2000 条，收满 32 倍 limit 就够重排了。 */
        /* limit 是候选窗口（引擎给 2000），但"通过谓词"的候选彼此只差几十分，
         * 收够重排要用的量就该停；否则等于把整张表解码一遍。 */
        int sampleCap = Math.Max(limit * 2, 600);
        int accepted = 0;

        if (tokens.Length > 0)
        {
            /* 有词就还能用字节探针定位，只评估真正可能命中的名字。
             * 只探第一个词：其余词由 ScoreFile 的 AND 语义保证，换探针反复扫同一片名字纯属浪费。 */
            byte[] needle = Encoding.UTF8.GetBytes(tokens[0]);
            if (needle.Length == 0) return;

            int from = 0;
            while (from < snapshot.Names.Length)
            {
                if (budget.Expired()) return;

                int offset = FindToken(snapshot.Names, from, needle, budget);
                if (offset == ByteScanTimeout) return;
                if (offset < 0) break;

                int nameIndex = FindNameIndex(starts, offset);
                if (nameIndex < 0)
                {
                    from = offset + 1;
                    continue;
                }

                if (AcceptSpecName(snapshot, nameIndex, spec, tokens, regex, extKeys, negExtKeys, out int score))
                    EmitFileHit(snapshot, nameIndex, score, limit, hits);

                from = NameEnd(snapshot, nameIndex) + 1;
                if (budget.Expired()) return;
            }
            return;
        }

        /* 只有谓词（doc: / date:week 这类）时没有探针可用，只能整表过一遍。
         * 顺序按代价排：先看索引期就存好的 size / day 两列，再按扩展名比字节，
         * 最后才把名字解码成字符 —— 绝大部分名字在前两步就被刷掉了。 */
        for (int i = 0; i < starts.Length; i++)
        {
            if (budget.Expired()) return;
            if (accepted >= sampleCap) return;
            if (!SizeDayOk(snapshot, i, spec)) continue;
            if (anyExt && !ExtBytesAccept(snapshot, i, extKeys, negExtKeys)) continue;
            if (!AcceptSpecName(snapshot, i, spec, tokens, regex, extKeys, negExtKeys, out int score)) continue;
            accepted++;
            EmitFileHit(snapshot, i, score, limit, hits);
        }
    }

    private static void ScanSpecDirs(
        Snapshot snapshot,
        QbarSearchSpec spec,
        string[] tokens,
        Regex? regex,
        int limit,
        List<FileSearchHit> hits,
        SearchBudget budget)
    {
        string[] dirs = snapshot.Dirs;
        int accepted = 0;
        int sampleCap = Math.Max(limit * 2, 600);
        for (int i = 0; i < dirs.Length; i++)
        {
            if (budget.Expired()) return;
            if (accepted >= sampleCap) return;
            string dir = dirs[i];
            if (dir.Length == 0) continue;
            if (!PathPinMatch(dir, ReadOnlySpan<char>.Empty, spec)) continue;

            ReadOnlySpan<char> leaf = LastSegment(dir.AsSpan());
            if (leaf.Length == 0) continue;

            if (spec.Glob.Length > 0 && !GlobMatch(leaf, spec.Glob)) continue;
            if (regex != null && !SafeRegexMatch(regex, leaf, dir)) continue;

            bool rejected = false;
            for (int n = 0; n < spec.NegTerms.Count; n++)
            {
                string neg = spec.NegTerms[n];
                if (neg.Length == 0) continue;
                if (dir.IndexOf(neg, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rejected = true;
                    break;
                }
            }
            if (rejected) continue;

            int score;
            if (tokens.Length > 0)
            {
                bool allInPath = true;
                for (int t = 0; t < tokens.Length; t++)
                {
                    if (dir.IndexOf(tokens[t], StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    allInPath = false;
                    break;
                }
                if (!allInPath) continue;

                if (EqualsAscii(leaf, spec.PlainQuery)) score = 900;
                else if (ContainsAnyAscii(leaf, tokens)) score = 420;
                else continue;
            }
            else
            {
                score = 500;
            }

            score -= leaf.Length / 8;
            score -= DepthOf(dir) * 8;
            accepted++;
            AddHit(hits, new FileSearchHit(dir, true, score), limit);
        }
    }

    /// <summary>
    /// 一条名字是否满足全部结构化约束。命中时给出相关性分；调用方拿到的分
    /// 与改造前 ScoreFile 的分档完全同源，所以新语法不会把老结果的相对次序搅乱。
    /// </summary>
    private static bool AcceptSpecName(
        Snapshot snapshot,
        int nameIndex,
        QbarSearchSpec spec,
        string[] tokens,
        Regex? regex,
        HashSet<uint>? extKeys,
        HashSet<uint>? negExtKeys,
        out int score)
    {
        score = 0;
        if (!SizeDayOk(snapshot, nameIndex, spec)) return false;

        int start = snapshot.NameStart[nameIndex];
        int end = NameEnd(snapshot, nameIndex);
        if (end <= start) return false;

        var bytes = new ReadOnlySpan<byte>(snapshot.Names, start, end - start);
        int charCount;
        try { charCount = Encoding.UTF8.GetCharCount(bytes); }
        catch { return false; }
        if (charCount <= 0 || charCount > MaxNameChars) return false;

        Span<char> buffer = stackalloc char[MaxNameChars];
        int written = Encoding.UTF8.GetChars(bytes, buffer);
        ReadOnlySpan<char> name = buffer.Slice(0, written);
        string dir = snapshot.Dirs[snapshot.NameDir[nameIndex]];

        if (extKeys != null && extKeys.Count > 0)
        {
            uint key = PackExt(LastExtension(name));
            if (key == 0 || !extKeys.Contains(key)) return false;
        }
        if (negExtKeys != null && negExtKeys.Count > 0)
        {
            uint key = PackExt(LastExtension(name));
            if (key != 0 && negExtKeys.Contains(key)) return false;
        }

        if (!PathPinMatch(dir, name, spec)) return false;

        for (int i = 0; i < spec.NegTerms.Count; i++)
        {
            string neg = spec.NegTerms[i];
            if (neg.Length == 0) continue;
            if (ContainsAscii(name, neg)) return false;
            if (dir.IndexOf(neg, StringComparison.OrdinalIgnoreCase) >= 0) return false;
        }

        if (spec.Glob.Length > 0 && !GlobMatch(name, spec.Glob)) return false;
        if (regex != null && !SafeRegexMatch(regex, name, dir)) return false;

        if (tokens.Length > 0)
        {
            int matched = ScoreFile(name, dir, spec.PlainQuery, tokens);
            if (matched == NoScore) return false;
            score = matched;
            return true;
        }

        /* 只有谓词时没有相关性可言：短名字、浅路径靠前，纯粹为了结果稳定、可复现。 */
        score = 500 - name.Length / 8 - DepthOf(dir) * 8;
        return true;
    }

    private static bool SizeDayOk(Snapshot snapshot, int nameIndex, QbarSearchSpec spec)
    {
        if (spec.MinSize < 0 && spec.MaxSize < 0 && spec.MinDay < 0 && spec.MaxDay < 0) return true;

        if (spec.MinSize >= 0 || spec.MaxSize >= 0)
        {
            /* 索引里存的是 KB（整数、向下取整），所以这里是近似比较：
             * 1MB 上下的边界可能差 1KB。要精确到字节就得把 uint64 也塞进快照，
             * 20 万条就多 1.6MB 常驻内存，为一个边界值不值得（见 DECISIONS.md）。 */
            long size = (long)snapshot.NameSizeKb[nameIndex] * 1024L;
            if (spec.MinSize >= 0 && size < spec.MinSize) return false;
            if (spec.MaxSize >= 0 && size > spec.MaxSize) return false;
        }

        if (spec.MinDay >= 0 || spec.MaxDay >= 0)
        {
            int day = snapshot.NameDay[nameIndex];
            if (day == QbarSyntaxValues.UnknownDay) return false;
            if (spec.MinDay >= 0 && day < spec.MinDay) return false;
            if (spec.MaxDay >= 0 && day > spec.MaxDay) return false;
        }

        return true;
    }

    private static void EmitFileHit(
        Snapshot snapshot, int nameIndex, int score, int limit, List<FileSearchHit> hits)
    {
        string name = DecodeName(snapshot, nameIndex);
        if (name.Length == 0) return;

        string dir = snapshot.Dirs[snapshot.NameDir[nameIndex]];
        int sizeKb = snapshot.NameSizeKb[nameIndex] > int.MaxValue ? int.MaxValue : (int)snapshot.NameSizeKb[nameIndex];
        int day = snapshot.NameDay[nameIndex] == QbarSyntaxValues.UnknownDay ? -1 : snapshot.NameDay[nameIndex];
        AddHit(hits, new FileSearchHit(Path.Combine(dir, name), false, score, sizeKb, day), limit);
    }

    private static int NameEnd(Snapshot snapshot, int nameIndex)
    {
        return (nameIndex + 1 < snapshot.NameStart.Length
            ? snapshot.NameStart[nameIndex + 1]
            : snapshot.Names.Length) - 1;
    }

    /// <summary>路径约束：path: 的含义是「路径里含有这一段」，正负两边都用同一套判断。</summary>
    private static bool PathPinMatch(string dir, ReadOnlySpan<char> name, QbarSearchSpec spec)
    {
        for (int i = 0; i < spec.PathPins.Count; i++)
        {
            string pin = spec.PathPins[i].Trim();
            if (pin.Length == 0) continue;
            if (dir.IndexOf(pin, StringComparison.OrdinalIgnoreCase) < 0 && !ContainsAscii(name, pin))
                return false;
        }

        for (int i = 0; i < spec.NegPathPins.Count; i++)
        {
            string pin = spec.NegPathPins[i].Trim();
            if (pin.Length == 0) continue;
            if (dir.IndexOf(pin, StringComparison.OrdinalIgnoreCase) >= 0 || ContainsAscii(name, pin))
                return false;
        }

        return true;
    }

    /// <summary>文件名 glob（* 和 ?），大小写不敏感。逐字符回溯匹配，不构造正则。</summary>
    private static bool GlobMatch(ReadOnlySpan<char> text, string pattern)
    {
        int t = 0;
        int p = 0;
        int star = -1;
        int mark = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharEquals(text[t], pattern[p])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool SafeRegexMatch(Regex regex, ReadOnlySpan<char> name, string dir)
    {
        try
        {
            if (regex.IsMatch(name.ToString())) return true;
            return dir.Length > 0 && regex.IsMatch(dir);
        }
        catch (RegexMatchTimeoutException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static ReadOnlySpan<char> LastExtension(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return ReadOnlySpan<char>.Empty;
        return name.Slice(dot + 1);
    }

    /// <summary>
    /// 扩展名压缩成整数键：每字符 5 bit（a-z→11..36、0-9→1..10），长度 3 bit，
    /// 最多 5 个字符。这样整表扫描时判断扩展名不用给每个名字分配字符串。
    /// </summary>
    private static uint PackExt(ReadOnlySpan<char> ext)
    {
        if (ext.Length == 0 || ext.Length > 5) return 0;

        uint key = (uint)ext.Length << 25;
        for (int i = 0; i < ext.Length; i++)
        {
            char c = ext[i];
            if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
            uint v;
            if (c >= 'a' && c <= 'z') v = (uint)(c - 'a' + 11);
            else if (c >= '0' && c <= '9') v = (uint)(c - '0' + 1);
            else return 0;
            key |= v << (i * 5);
        }
        return key;
    }

    private static uint PackExtBytes(ReadOnlySpan<byte> ext)
    {
        if (ext.Length == 0 || ext.Length > 5) return 0;

        uint key = (uint)ext.Length << 25;
        for (int i = 0; i < ext.Length; i++)
        {
            byte c = ext[i];
            if (c >= (byte)'A' && c <= (byte)'Z') c = (byte)(c + 32);
            uint v;
            if (c >= (byte)'a' && c <= (byte)'z') v = (uint)(c - 'a' + 11);
            else if (c >= (byte)'0' && c <= (byte)'9') v = (uint)(c - '0' + 1);
            else return 0;
            key |= v << (i * 5);
        }
        return key;
    }

    private static HashSet<uint>? BuildExtKeys(ICollection<string> extensions)
    {
        if (extensions.Count == 0) return null;

        var set = new HashSet<uint>();
        foreach (string ext in extensions)
        {
            string trimmed = ext.Trim();
            if (trimmed.StartsWith(".", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
            uint key = PackExt(trimmed.AsSpan());
            if (key != 0) set.Add(key);
        }
        return set;
    }

    private static bool ExtBytesAccept(
        Snapshot snapshot, int nameIndex, HashSet<uint>? extKeys, HashSet<uint>? negExtKeys)
    {
        int start = snapshot.NameStart[nameIndex];
        int end = NameEnd(snapshot, nameIndex);
        if (end <= start) return false;

        var bytes = new ReadOnlySpan<byte>(snapshot.Names, start, end - start);
        int dot = bytes.LastIndexOf((byte)'.');
        uint key = dot < 0 || dot == bytes.Length - 1 ? 0u : PackExtBytes(bytes.Slice(dot + 1));

        if (extKeys != null && extKeys.Count > 0 && (key == 0 || !extKeys.Contains(key))) return false;
        if (negExtKeys != null && negExtKeys.Count > 0 && key != 0 && negExtKeys.Contains(key)) return false;
        return true;
    }

    /// <summary>
    /// 先扫文件名。在 UTF-8 名字连成的字节里找 probe 词，命中后解码这一个名字，
    /// 再拿全部词去比"名字 + 所属目录"。
    ///
    /// 为什么 probe 词可能不是第一个词：像「项目 说明」这种查询，第一个词只出现在
    /// 目录名里，文件名里只有"说明"。只扫第一个词的话，说明.md 永远找不到 ——
    /// 而这正是自检里那条 AND 语义用例。所以第一个词一个名字都没命中时，
    /// 依次换后面的词再扫；一旦某一轮已经有合格结果就停，避免 4 个常见词
    /// 把整个名字区扫四遍。
    /// </summary>
    private static void ScanFiles(
        Snapshot snapshot,
        string query,
        string[] tokens,
        int limit,
        List<FileSearchHit> hits,
        SearchBudget budget)
    {
        byte[] names = snapshot.Names;
        int[] starts = snapshot.NameStart;
        if (starts.Length == 0) return;

        for (int probe = 0; probe < tokens.Length; probe++)
        {
            byte[] needle = Encoding.UTF8.GetBytes(tokens[probe]);
            if (needle.Length == 0) continue;

            int from = 0;
            bool qualified = false;

            while (from < names.Length)
            {
                if (budget.Expired()) return;

                int offset = FindToken(names, from, needle, budget);
                if (offset == ByteScanTimeout) return;
                if (offset < 0) break;

                int nameIndex = FindNameIndex(starts, offset);
                if (nameIndex < 0)
                {
                    from = offset + 1;
                    continue;
                }

                int score = ScoreNameAt(snapshot, nameIndex, query, tokens, out int nameEnd);
                if (score != NoScore)
                {
                    /* 走 EmitFileHit 而不是自己拼 FileSearchHit：这里以前只给
                     * path/score，大小和修改日期全是默认值（0 / -1），结果就是
                     * 普通关键词查询的大小/日期列永远空白、分面桶全落到「更早」。
                     * 元数据只在快照里有，必须从同一个 nameIndex 取。 */
                    int before = hits.Count;
                    EmitFileHit(snapshot, nameIndex, score, limit, hits);
                    if (hits.Count > before) qualified = true;
                }

                // 同一个名字只评估一次。跳到这个名字之后，别在它内部反复命中同一个词。
                from = nameEnd + 1;
                if (budget.Expired()) return;
            }

            if (qualified) return;
        }
    }

    /// <summary>
    /// 名字命中打分。先把"所有词是否都能在 名字+目录 里找到"验掉 ——
    /// 不做这一步的话，"chrome setup" 会把所有 chrome.exe 都当成结果。
    /// </summary>
    private static int ScoreFile(
        ReadOnlySpan<char> name,
        string dir,
        string query,
        string[] tokens)
    {
        bool allInName = true;
        bool firstInName = false;

        for (int i = 0; i < tokens.Length; i++)
        {
            bool inName = ContainsAscii(name, tokens[i]);
            if (i == 0) firstInName = inName;
            if (inName) continue;

            allInName = false;
            if (dir.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) < 0) return NoScore;
        }

        int score;
        if (EqualsAscii(name, query)) score = 1000;
        else if (StartsWithAscii(name, tokens[0])) score = 850;
        else if (allInName) score = 780;
        else if (firstInName) score = 640;       // 700 - 60：其余词只在目录里
        else score = 600;                         // 名字里只有后面的词，第一个词在目录里

        score -= name.Length / 8;
        score -= DepthOf(dir) * 8;
        if (EndsWithAscii(name, ".exe") || EndsWithAscii(name, ".lnk") || EndsWithAscii(name, ".url"))
            score += 12;
        return score;
    }

    /// <summary>
    /// 解码第 nameIndex 个名字并打分。返回 NoScore 表示这个名字不合格。
    /// stackalloc 放在独立方法里：直接写在扫描循环里的话，C# 不会每轮回收栈顶，
    /// 十万个候选就能把线程栈打爆。
    /// </summary>
    private static int ScoreNameAt(
        Snapshot snapshot,
        int nameIndex,
        string query,
        string[] tokens,
        out int nameEnd)
    {
        int start = snapshot.NameStart[nameIndex];
        nameEnd = (nameIndex + 1 < snapshot.NameStart.Length
            ? snapshot.NameStart[nameIndex + 1]
            : snapshot.Names.Length) - 1;

        if (nameEnd <= start || nameEnd >= snapshot.Names.Length) return NoScore;
        if (snapshot.Names[nameEnd] != 0x0A) return NoScore;

        var bytes = new ReadOnlySpan<byte>(snapshot.Names, start, nameEnd - start);
        if (bytes.Length == 0) return NoScore;

        int charCount;
        try { charCount = Encoding.UTF8.GetCharCount(bytes); }
        catch { return NoScore; }

        if (charCount <= 0 || charCount > MaxNameChars) return NoScore;

        Span<char> buffer = stackalloc char[MaxNameChars];
        int written = Encoding.UTF8.GetChars(bytes, buffer);
        return ScoreFile(buffer.Slice(0, written), snapshot.Dirs[snapshot.NameDir[nameIndex]], query, tokens);
    }

    private static string DecodeName(Snapshot snapshot, int nameIndex)
    {
        int start = snapshot.NameStart[nameIndex];
        int end = (nameIndex + 1 < snapshot.NameStart.Length
            ? snapshot.NameStart[nameIndex + 1]
            : snapshot.Names.Length) - 1;
        if (end <= start) return "";
        return Encoding.UTF8.GetString(snapshot.Names, start, end - start);
    }

    /// <summary>
    /// 目录本身也参与匹配。先要求全部词都出现在完整路径里（AND 语义），
    /// 再看最后一段：整段等于 query 给 900，包含任一 token 给 420。
    /// </summary>
    private static void ScanDirs(
        Snapshot snapshot,
        string query,
        string[] tokens,
        int limit,
        List<FileSearchHit> hits,
        SearchBudget budget)
    {
        string[] dirs = snapshot.Dirs;
        for (int i = 0; i < dirs.Length; i++)
        {
            if (budget.Expired()) return;
            string dir = dirs[i];

            bool allInPath = true;
            for (int t = 0; t < tokens.Length; t++)
            {
                if (dir.IndexOf(tokens[t], StringComparison.OrdinalIgnoreCase) >= 0) continue;
                allInPath = false;
                break;
            }
            if (!allInPath) continue;

            ReadOnlySpan<char> name = LastSegment(dir.AsSpan());
            if (name.Length == 0) continue;

            int score;
            if (EqualsAscii(name, query)) score = 900;
            else if (ContainsAnyAscii(name, tokens)) score = 420;
            else continue;

            score -= name.Length / 8;
            score -= DepthOf(dir) * 8;
            AddHit(hits, new FileSearchHit(dir, true, score), limit);
        }
    }

    /// <summary>
    /// 在字节区里找 token 的第一次出现。ASCII 大小写折叠；非 ASCII 字节按原文比
    /// （UTF-8 的字节序列本身就是精确的）。超时返回 <see cref="ByteScanTimeout"/>。
    /// </summary>
    private static int FindToken(byte[] names, int from, byte[] token, SearchBudget budget)
    {
        if (token.Length == 0) return -1;
        int last = names.Length - token.Length;
        byte firstLower = AsciiLower(token[0]);
        byte firstUpper = AsciiUpper(token[0]);

        for (int i = from; i <= last; i++)
        {
            if (budget.ByteCheck(i)) return ByteScanTimeout;

            byte b = names[i];
            if (b != firstLower && b != firstUpper) continue;

            int k = 1;
            while (k < token.Length && ByteEquals(names[i + k], token[k])) k++;
            if (k == token.Length) return i;
        }
        return -1;
    }

    /// <summary>二分找出 offset 属于哪个名字。名字起点是按写入顺序递增的。</summary>
    private static int FindNameIndex(int[] starts, int offset)
    {
        int low = 0;
        int high = starts.Length - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (starts[mid] <= offset)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return found;
    }

    private static void AddHit(List<FileSearchHit> hits, FileSearchHit hit, int limit)
    {
        hits.Add(hit);

        /* 候选可能有几万条（比如 "exe"），全留到最后一并排序会白白堆一大摞
         * 马上要被截掉的命中。这里只保留当前前 8×limit 条，最终 Top N 不会因此
         * 出错：被丢掉的条目至少排在 8×limit 条之后，而 N ≤ limit。 */
        int cap = Math.Max(limit * 8, 128);
        if (hits.Count < cap) return;
        SortAndTrim(hits, Math.Max(limit * 2, 16));
    }

    /// <summary>
    /// 稳定排序：同分时短路径优先，再按路径序数比较。这样即使建索引时目录枚举
    /// 顺序变了，同一份索引、同一个查询也会给出完全相同的顺序。
    /// </summary>
    private static void SortAndTrim(List<FileSearchHit> hits, int keep)
    {
        hits.Sort(CompareHits);
        if (hits.Count > keep) hits.RemoveRange(keep, hits.Count - keep);
    }

    private static int CompareHits(FileSearchHit a, FileSearchHit b)
    {
        int compare = b.Score.CompareTo(a.Score);
        if (compare != 0) return compare;

        compare = a.Path.Length.CompareTo(b.Path.Length);
        if (compare != 0) return compare;

        compare = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        if (compare != 0) return compare;

        return a.IsDir.CompareTo(b.IsDir);
    }

    private static void NoteSearchTimeout()
    {
        SetStatus("搜索超时");
        if (Interlocked.Exchange(ref _timeoutLogged, 1) == 0)
            CoreLog.Warn("文件索引搜索超时：本次没有扫到结果");
    }

    // ───────────────────────────── 字符 / 字节比较 ─────────────────────────────

    /// <summary>
    /// ASCII 忽略大小写，非 ASCII 精确。中文没有大小写，走 == 最快；
    /// 只有两边都在 ASCII 范围才做折叠，避免 ToLower 把某些 Unicode 字符改成另一个字符。
    /// </summary>
    private static bool CharEquals(char a, char b)
        => a == b || (a < 128 && b < 128 && AsciiLower(a) == AsciiLower(b));

    private static char AsciiLower(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;

    private static byte AsciiLower(byte b) => b >= (byte)'A' && b <= (byte)'Z' ? (byte)(b + 32) : b;

    private static byte AsciiUpper(byte b) => b >= (byte)'a' && b <= (byte)'z' ? (byte)(b - 32) : b;

    private static bool ByteEquals(byte a, byte b)
    {
        if (a == b) return true;
        return a < 0x80 && b < 0x80 && AsciiLower(a) == AsciiLower(b);
    }

    private static bool ContainsAscii(ReadOnlySpan<char> haystack, string needle)
    {
        if (needle.Length == 0) return true;
        if (haystack.Length < needle.Length) return false;

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && CharEquals(haystack[i + j], needle[j])) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    private static bool ContainsAnyAscii(ReadOnlySpan<char> haystack, string[] needles)
    {
        for (int i = 0; i < needles.Length; i++)
        {
            if (ContainsAscii(haystack, needles[i])) return true;
        }
        return false;
    }

    private static bool StartsWithAscii(ReadOnlySpan<char> haystack, string prefix)
    {
        if (haystack.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (!CharEquals(haystack[i], prefix[i])) return false;
        }
        return true;
    }

    private static bool EqualsAscii(ReadOnlySpan<char> a, string b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!CharEquals(a[i], b[i])) return false;
        }
        return true;
    }

    private static bool EndsWithAscii(ReadOnlySpan<char> value, string suffix)
    {
        if (value.Length < suffix.Length) return false;
        int offset = value.Length - suffix.Length;
        for (int i = 0; i < suffix.Length; i++)
        {
            if (!CharEquals(value[offset + i], suffix[i])) return false;
        }
        return true;
    }

    /// <summary>取路径最后一段，不分配字符串。盘根 "C:\" 会得到 "C:"。</summary>
    private static ReadOnlySpan<char> LastSegment(ReadOnlySpan<char> path)
    {
        int end = path.Length;
        while (end > 0 && (path[end - 1] == '\\' || path[end - 1] == '/')) end--;

        int start = end;
        while (start > 0 && path[start - 1] != '\\' && path[start - 1] != '/') start--;
        return path.Slice(start, end - start);
    }

    /// <summary>路径深度（分隔符个数）。只用于同分排序，不要求语义精确到"层"。</summary>
    private static int DepthOf(string path)
    {
        int depth = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\' || path[i] == '/') depth++;
        }
        return depth;
    }

    private static string FormatCount(int count)
    {
        if (count >= 10000)
            return (count / 10000.0).ToString("0.#", CultureInfo.InvariantCulture) + " 万";
        return count.ToString(CultureInfo.InvariantCulture);
    }

    // ───────────────────────────── 落盘 / 载入 ─────────────────────────────

    /// <summary>
    /// 存盘。每个文件都是"写 .tmp → File.Move 覆盖"，中途断电最多留下一个
    /// .tmp，不会出现半个 dirs.bin 被下一次启动当成好索引读进来。
    /// meta.txt 最后落：能看到完整 meta，说明这一代数据文件已经全部到位。
    /// </summary>
    private static bool SaveSnapshot(Snapshot snapshot, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);

            var meta = new StringBuilder();
            meta.Append(SchemaVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            meta.Append(snapshot.BuiltAt.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < snapshot.Roots.Length; i++)
                meta.Append(snapshot.Roots[i]).Append('\n');

            byte[] dirs = EncodeDirs(snapshot.Dirs);
            byte[] starts = IntsToBytes(snapshot.NameStart);
            byte[] nameDirs = IntsToBytes(snapshot.NameDir);
            byte[] sizes = UIntsToBytes(snapshot.NameSizeKb);
            byte[] days = UShortsToBytes(snapshot.NameDay);

            if (!WriteAtomic(Path.Combine(dir, "names.bin"), snapshot.Names)) return false;
            if (!WriteAtomic(Path.Combine(dir, "nameStart.bin"), starts)) return false;
            if (!WriteAtomic(Path.Combine(dir, "nameDir.bin"), nameDirs)) return false;
            if (!WriteAtomic(Path.Combine(dir, "nameSize.bin"), sizes)) return false;
            if (!WriteAtomic(Path.Combine(dir, "nameDay.bin"), days)) return false;
            if (!WriteAtomic(Path.Combine(dir, "dirs.bin"), dirs)) return false;
            if (!WriteAtomic(Path.Combine(dir, "meta.txt"), Encoding.UTF8.GetBytes(meta.ToString()))) return false;

            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "保存文件索引");
            return false;
        }
    }

    private static bool WriteAtomic(string path, byte[] data)
    {
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "写索引文件 " + Path.GetFileName(path));
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    private static byte[] EncodeDirs(string[] dirs)
    {
        using (var stream = new MemoryStream(dirs.Length * 32))
        {
            for (int i = 0; i < dirs.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(dirs[i]);
                stream.Write(bytes, 0, bytes.Length);
                stream.WriteByte(0x0A);
            }
            return stream.ToArray();
        }
    }

    private static byte[] IntsToBytes(int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        if (values.Length > 0) Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] UIntsToBytes(uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        if (values.Length > 0) Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] UShortsToBytes(ushort[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        if (values.Length > 0) Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static uint[]? ReadUInts(string path)
    {
        if (!File.Exists(path)) return null;
        byte[] bytes = File.ReadAllBytes(path);
        if ((bytes.Length & 3) != 0) return null;
        uint[] values = new uint[bytes.Length / 4];
        if (values.Length > 0) Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static ushort[]? ReadUShorts(string path)
    {
        if (!File.Exists(path)) return null;
        byte[] bytes = File.ReadAllBytes(path);
        if ((bytes.Length & 1) != 0) return null;
        ushort[] values = new ushort[bytes.Length / 2];
        if (values.Length > 0) Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static int[]? ReadInts(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if ((bytes.Length & 3) != 0) return null;

        int[] values = new int[bytes.Length / 4];
        if (values.Length > 0) Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static string[] ReadDirs(byte[] bytes)
    {
        var dirs = new List<string>();
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0x0A) continue;
            dirs.Add(Encoding.UTF8.GetString(bytes, start, i - start));
            start = i + 1;
        }
        if (start < bytes.Length)
            dirs.Add(Encoding.UTF8.GetString(bytes, start, bytes.Length - start));
        return dirs.ToArray();
    }

    /// <summary>
    /// 读缓存。任何一个文件缺失、版本不对、数组对不上，都当成"没有索引"返回 null，
    /// 由调用方删目录重建。宁可多扫一次盘，也不能拿半份索引去回答用户。
    /// </summary>
    private static Snapshot? TryLoad(string dir)
    {
        try
        {
            string metaPath = Path.Combine(dir, "meta.txt");
            string dirsPath = Path.Combine(dir, "dirs.bin");
            string namesPath = Path.Combine(dir, "names.bin");
            string startsPath = Path.Combine(dir, "nameStart.bin");
            string mapPath = Path.Combine(dir, "nameDir.bin");

            if (!File.Exists(metaPath) || !File.Exists(dirsPath) || !File.Exists(namesPath)
                || !File.Exists(startsPath) || !File.Exists(mapPath))
                return null;

            string[] meta = File.ReadAllLines(metaPath, Encoding.UTF8);
            if (meta.Length < 2) return null;

            if (!string.Equals(meta[0].Trim(), SchemaVersion.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
                return null;

            if (!long.TryParse(meta[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                return null;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;

            var roots = new List<string>(meta.Length - 2);
            for (int i = 2; i < meta.Length; i++)
            {
                if (meta[i].Length > 0) roots.Add(meta[i]);
            }

            byte[] dirsBytes = File.ReadAllBytes(dirsPath);
            byte[] names = File.ReadAllBytes(namesPath);
            int[]? starts = ReadInts(startsPath);
            int[]? nameDirs = ReadInts(mapPath);
            if (starts == null || nameDirs == null) return null;
            string[] dirs = ReadDirs(dirsBytes);

            /* v2 的两列允许"整份缺"（老缓存已经由版本号挡掉），但只要文件在，
             * 长度就必须和名字数完全一致 —— 少一条就会让 size/date 错位到别的文件上，
             * 这种错误不会崩，只会让筛选结果看起来"莫名其妙漏了一部分"，最难查。 */
            string sizePath = Path.Combine(dir, "nameSize.bin");
            string dayPath = Path.Combine(dir, "nameDay.bin");
            uint[]? sizes = ReadUInts(sizePath);
            ushort[]? days = ReadUShorts(dayPath);
            if (sizes == null || days == null) return null;

            if (starts.Length != nameDirs.Length) return null;
            if (sizes.Length != starts.Length || days.Length != starts.Length) return null;
            if (starts.Length > MaxFiles || dirs.Length > MaxDirs) return null;

            if (starts.Length > 0)
            {
                if (names.Length < 2 || names[names.Length - 1] != 0x0A) return null;
                if (starts[0] != 0) return null;

                for (int i = 0; i < starts.Length; i++)
                {
                    if (starts[i] < 0 || starts[i] >= names.Length) return null;
                    if (i > 0 && starts[i] <= starts[i - 1]) return null;

                    int map = nameDirs[i];
                    if (map < 0 || map >= dirs.Length) return null;

                    int end = (i + 1 < starts.Length ? starts[i + 1] : names.Length) - 1;
                    if (end <= starts[i] || names[end] != 0x0A) return null;
                }
            }
            else if (names.Length != 0)
            {
                return null;
            }

            return new Snapshot(dirs, names, starts, nameDirs, sizes, days,
                new DateTime(ticks, DateTimeKind.Local), roots.ToArray());
        }
        catch (Exception ex)
        {
            CoreLog.Warn("读取文件索引缓存失败：" + ex.Message);
            return null;
        }
    }

    private static void ClearIndexDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "删除损坏的文件索引缓存"); }

        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { CoreLog.Swallow(ex, "创建文件索引目录"); }
    }

    // ───────────────────────────── 数据类型 ─────────────────────────────

    /// <summary>
    /// 一次发布就永不修改的索引快照。Search 只读它，所以后台重建完全不用加锁。
    /// 名字的结束位置不单独存：下一个名字的起点减一就是换行符，省 3.2MB。
    /// </summary>
    private sealed class Snapshot
    {
        internal readonly string[] Dirs;
        internal readonly byte[] Names;
        internal readonly int[] NameStart;
        internal readonly int[] NameDir;

        /// <summary>每个文件的字节数 / 1024（饱和到 uint），与 NameStart 等长。</summary>
        internal readonly uint[] NameSizeKb;

        /// <summary>每个文件的修改日（自 2000-01-01 的天数），65535 = 未知。</summary>
        internal readonly ushort[] NameDay;

        internal readonly DateTime BuiltAt;
        internal readonly string[] Roots;

        internal Snapshot(
            string[] dirs,
            byte[] names,
            int[] nameStart,
            int[] nameDir,
            uint[] nameSizeKb,
            ushort[] nameDay,
            DateTime builtAt,
            string[] roots)
        {
            Dirs = dirs;
            Names = names;
            NameStart = nameStart;
            NameDir = nameDir;
            NameSizeKb = nameSizeKb;
            NameDay = nameDay;
            BuiltAt = builtAt;
            Roots = roots;
        }
    }

    private readonly struct DirEntry
    {
        internal readonly string Path;
        internal readonly int Index;

        internal DirEntry(string path, int index)
        {
            Path = path;
            Index = index;
        }
    }

    private sealed class BuildOutcome
    {
        internal Snapshot? Snapshot;
        internal bool Capped;
        internal string StopReason = "";
        internal bool Cancelled;
        internal string? Error;
    }

    /// <summary>
    /// 搜索的时间预算。每 4096 个候选查一次表；字节扫描按 64KB 查一次，
    /// 否则一个 1ms 的超时也要先扫完 30MB 名字区才肯抬头。
    /// </summary>
    private sealed class SearchBudget
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly int _timeoutMs;
        private int _sinceCheck;

        internal bool TimedOut { get; private set; }

        internal SearchBudget(int timeoutMs)
        {
            _timeoutMs = timeoutMs;
        }

        internal bool Expired()
        {
            if (_timeoutMs <= 0) return false;
            if (++_sinceCheck < TimeoutCheckEvery) return false;
            _sinceCheck = 0;
            if (_watch.ElapsedMilliseconds <= _timeoutMs) return false;
            TimedOut = true;
            return true;
        }

        internal bool ByteCheck(int index)
        {
            if (_timeoutMs <= 0) return false;
            if ((index & 0xFFFF) != 0) return false;
            if (_watch.ElapsedMilliseconds <= _timeoutMs) return false;
            TimedOut = true;
            return true;
        }
    }
}
