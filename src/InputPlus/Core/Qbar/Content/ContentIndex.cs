using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq;
using System.Text.Json;
using System.Threading;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Qbar.Content;

/// <summary>
/// 正文索引服务：把白名单文件里的文字抽出来，写进**自己那套** tantivy 索引
/// （`%Data%\file-content-tantivy`），与文件名索引完全解耦。
///
/// 为什么解耦：
///   · 正文索引体积是文件名索引的十几倍，段合并会拖慢"打两个字就出结果"的查询；
///   · 用户要能在设置里单独关掉/重建/清空它，而不牵连 67 万条文件名索引；
///   · 抽 PDF 可能卡住（损坏文件），绝不能让它把文件搜索也一起卡死。
///
/// 线程模型：
///   · 一个后台**构建**线程（首次/重建）：广度优先枚举 → 抽取 → 批量写 → 提交；
///   · 一个后台**增量**线程：吃 watcher 丢进来的路径，去抖后先删后加。
/// 两者都 BelowNormal 优先级、单消费者，宁可慢也不和前台抢 IO。
/// </summary>
internal static class ContentIndex
{
    /// <summary>与 Rust 的 `CONTENT_SCHEMA_VERSION` 必须同值；不一致就整体重建。</summary>
    internal const int SchemaVersion = 1;

    /// <summary>扫描阶段最多看多少条目录项（不是"收录多少文件"）。</summary>
    private const long MaxScanEntries = 1_500_000;

    /// <summary>扫描阶段最多花多少毫秒 —— 扫盘根时目录项可能是几百万条。</summary>
    private const int MaxScanMs = 180_000;

    /// <summary>抽取批次大小：太大提交一次要合并很久，太小段太碎。</summary>
    private const int BuildBatch = 512;

    /// <summary>增量每轮最多处理多少个文件（剩下的丢弃，等下次重建补）。</summary>
    private const int IncrementalBatch = 40;

    /// <summary>一次进度快照（界面只认这个）。</summary>
    internal readonly record struct ProgressInfo(
        bool Building, string Phase, int Percent, long Docs, long Failed, long Skipped, long Visited);

    /// <summary>进度变化。界面（右下角进度条 + 设置页）订阅它；回调可能在后台线程。</summary>
    internal static event Action<ProgressInfo>? ProgressChanged;

    private static string DefaultDir => Path.Combine(AppPaths.DataDir, "file-content-tantivy");

    private static string _dir = DefaultDir;
    private static string StatePath => Path.Combine(_dir, "state.json");

    private static readonly object Gate = new();
    private static readonly object WriteGate = new();
    private static readonly ConcurrentQueue<string> Changed = new();
    private static readonly ConcurrentQueue<string> Deleted = new();

    private static int _started;
    private static int _ready;
    private static int _building;
    private static int _incrementStarted;
    private static CancellationTokenSource? _cts;

    private static volatile string _status = "未启动";
    private static volatile string _problem = "";
    private static long _docs;
    private static long _bytes;
    private static long _failed;
    private static long _skipped;
    private static long _addedThisRun;
    private static int _lastCommitMs;
    private static int _progress;      // 0-100，-1 = 不确定
    private static volatile string _phase = "空闲";
    private static long _visited;
    private static long _lastPublishTick;

    private static QbarContentOptions _options = new();

    /// <summary>本次构建用的根目录（诊断/命令行用）。</summary>
    private static string[] _roots = Array.Empty<string>();

    internal static IReadOnlyList<string> Roots => _roots;

    // ─────────────────────────── 对外状态 ───────────────────────────

    internal static string IndexDir => _dir;

    internal static bool Enabled
    {
        get { lock (Gate) return _options.Enabled; }
    }

    internal static bool Ready => Volatile.Read(ref _ready) == 1;

    internal static bool Building => Volatile.Read(ref _building) == 1;

    internal static string Status => _status;

    internal static string Problem => _problem;

    internal static long Docs => Interlocked.Read(ref _docs);

    internal static long Bytes => Interlocked.Read(ref _bytes);

    internal static long Failed => Interlocked.Read(ref _failed);

    internal static long Skipped => Interlocked.Read(ref _skipped);

    internal static int LastCommitMs => Volatile.Read(ref _lastCommitMs);

    internal static int Progress => Volatile.Read(ref _progress);

    internal static string Phase => _phase;

    internal static long Visited => Interlocked.Read(ref _visited);

    /// <summary>发一次进度快照。节流到"百分比变了或过了 400ms"，否则扫描阶段会刷爆界面。</summary>
    private static void PublishProgress(int percent, bool force = false)
    {
        int now = Environment.TickCount;
        int last = (int)Interlocked.Read(ref _lastPublishTick);
        if (!force && percent == _progress && unchecked(now - last) < 400) return;
        Interlocked.Exchange(ref _lastPublishTick, now);
        _progress = percent;

        var handler = ProgressChanged;
        if (handler == null) return;
        try
        {
            handler(new ProgressInfo(Building, _phase, percent, Docs, Failed, Skipped, Visited));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "正文索引进度回调"); }
    }

    /// <summary>失败原因计数（诊断用）：reason → 条数。</summary>
    private static readonly ConcurrentDictionary<string, int> _failReasons = new(StringComparer.OrdinalIgnoreCase);

    internal static string FailReasons
    {
        get
        {
            if (_failReasons.IsEmpty) return "";
            var parts = _failReasons.OrderByDescending(kv => kv.Value).Take(6)
                                    .Select(kv => kv.Key + " " + kv.Value);
            return string.Join(" · ", parts);
        }
    }

    internal static string Describe()
    {
        if (!_options.Enabled) return "正文索引：已关闭";
        string head = "正文索引：" + FormatCount(_docs) + " 条";
        if (_bytes > 0) head += " · " + FormatSize(_bytes);
        if (Building)
        {
            int p = Progress;
            head += " · 建立中" + (p >= 0 ? " " + p + "%" : "");
        }
        else if (Ready)
        {
            head += " · 上次提交 " + LastCommitMs + " ms";
        }
        if (_failed > 0) head += " · 抽取失败 " + _failed;
        if (_problem.Length > 0) head += " · 问题：" + _problem;
        return head;
    }

    internal static string FormatCount(long n)
        => n >= 10000 ? (n / 10000.0).ToString("0.0") + " 万" : n.ToString();

    internal static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
        if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
        return bytes + " B";
    }

    // ─────────────────────────── 生命周期 ───────────────────────────

    /// <summary>
    /// 启动（幂等、非阻塞）。关掉开关时只关闭索引，不建任何东西。
    /// </summary>
    internal static void Start()
    {
        try
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

            if (QbarIndex.IsSelfTestProcess())
            {
                _status = "自检进程不建正文索引";
                return;
            }

            _options = ReadOptions();
            if (!_options.Enabled)
            {
                _status = "正文索引已关闭";
                Interlocked.Exchange(ref _started, 0);
                return;
            }
            if (!ContentNative.Available)
            {
                _problem = ContentNative.Available ? "" : TantivyApi.StatusText;
                _status = "正文索引不可用";
                CoreLog.Warn("正文索引未启动：" + _problem);
                return;
            }

            _dir = string.IsNullOrWhiteSpace(_options.IndexDir) ? DefaultDir : _options.IndexDir!.Trim();
            Directory.CreateDirectory(_dir);

            int rc = ContentNative.Open(_dir);
            if (rc != 0)
            {
                _problem = ContentNative.LastError;
                _status = "打开正文索引失败";
                CoreLog.Warn("打开正文索引失败：" + _problem);
                return;
            }

            lock (Gate)
            {
                _cts = new CancellationTokenSource();
            }

            var state = ReadState();
            string[] roots = BuildRoots();
            _roots = roots;
            bool reusable = state != null
                            && state.Version == SchemaVersion
                            && state.Done
                            && SameRoots(state.Roots, roots)
                            && Directory.Exists(_dir)
                            && state.BuiltAtTicks > 0;

            if (reusable)
            {
                LoadStats("正文索引就绪");
                Volatile.Write(ref _ready, 1);
                StartIncremental();
                return;
            }

            StartBuild(roots, clearFirst: true);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "启动正文索引");
            _problem = ex.Message;
            _status = "正文索引启动失败";
            Interlocked.Exchange(ref _started, 0);
        }
    }

    internal static void Stop()
    {
        try
        {
            lock (Gate)
            {
                _cts?.Cancel();
            }
            ContentNative.Close();
            Volatile.Write(ref _ready, 0);
            Interlocked.Exchange(ref _started, 0);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "停止正文索引"); }
    }

    /// <summary>丢弃正文索引并重建（设置页按钮）。</summary>
    internal static void RequestRebuild()
    {
        try
        {
            lock (Gate)
            {
                _cts?.Cancel();
            }
            ContentNative.Close();
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }

            Interlocked.Exchange(ref _started, 0);
            Volatile.Write(ref _ready, 0);
            _status = "正在重建正文索引";
            _problem = "";
            _docs = 0;
            _bytes = 0;
            _failed = 0;
            Start();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "重建正文索引"); }
    }

    /// <summary>清空全部正文文档（保留索引目录）。</summary>
    internal static bool ClearAll()
    {
        try
        {
            lock (WriteGate)
            {
                if (!ContentNative.Clear())
                {
                    _problem = ContentNative.LastError;
                    return false;
                }
            }
            Interlocked.Exchange(ref _docs, 0);
            Interlocked.Exchange(ref _bytes, 0);
            WriteState(Array.Empty<string>(), done: false);
            Volatile.Write(ref _ready, 0);
            _status = "正文索引已清空";
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "清空正文索引");
            _problem = ex.Message;
            return false;
        }
    }

    // ─────────────────────────── 增量 ───────────────────────────

    /// <summary>文件名索引的 upsert 会同步投一份进来（新增/修改）。</summary>
    internal static void NotifyChanged(IEnumerable<string> paths)
    {
        if (!Ready || !_options.Enabled) return;
        /* 只收**用户目录**里的文件。
         * 文件名索引扫的是盘根（C:\、D:\），它报上来的路径里有大量程序目录、
         * 源码仓库、构建产物 —— 那些不该进正文索引（体积/耗时都会失控），
         * 也超出用户在设置页看到的那句"索引用户目录下的文档"的承诺。 */
        string[] roots = _roots;
        if (roots.Length == 0) return;
        /* 队列上限：文件名索引一次全量扫描会报上来几十万条路径，正文这边
         * 抽不完也没必要排队 —— 超过上限就丢，下一次「重建正文索引」会补上。 */
        if (Changed.Count > 20000) return;

        foreach (string p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            bool inside = false;
            foreach (string root in roots)
            {
                if (QbarRoots.IsSameOrUnder(p, root)) { inside = true; break; }
            }
            if (inside) Changed.Enqueue(p);
        }
    }

    /// <summary>删除事件：正文索引只删不抽。</summary>
    internal static void NotifyDeleted(IEnumerable<string> paths)
    {
        if (!_options.Enabled) return;
        foreach (string p in paths) Deleted.Enqueue(p);
    }

    private static void StartIncremental()
    {
        if (Interlocked.CompareExchange(ref _incrementStarted, 1, 0) != 0) return;
        var thread = new Thread(IncrementalLoop)
        {
            IsBackground = true,
            Name = "qbar-content-incr",
            Priority = ThreadPriority.Lowest,
        };
        thread.Start();
    }

    private static void IncrementalLoop()
    {
        while (true)
        {
            try
            {
                Thread.Sleep(600);
                if (!_options.Enabled) continue;

                var deleted = new List<string>();
                while (Deleted.TryDequeue(out string? d) && deleted.Count < 2000) deleted.Add(d);

                var changed = new List<string>();
                while (Changed.TryDequeue(out string? c) && changed.Count < IncrementalBatch) changed.Add(c);

                if (deleted.Count == 0 && changed.Count == 0) continue;

                ApplyIncremental(changed, deleted);
                /* 增量也要限速：文件名索引一次全量扫描会灌进来几万条路径，
                 * 连续抽 PDF 会把整机拖死（第一版就是这样）。剩下的留给下一轮，
                 * 队列超过上限时干脆丢弃，等用户点「重建正文索引」。 */
                Thread.Sleep(changed.Count >= IncrementalBatch ? 400 : 0);
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "正文索引增量");
            }
        }
    }

    private static void ApplyIncremental(List<string> changed, List<string> deleted)
    {
        // 去重（大小写不敏感，Windows 路径）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        changed = changed.Where(p => p.Length > 0 && seen.Add(p)).ToList();
        deleted = deleted.Where(p => p.Length > 0 && seen.Add(p)).ToList();

        /* 删除立即提交（少、而且不提交就等于"删了还搜得到"）。 */
        if (deleted.Count > 0)
        {
            lock (WriteGate)
            {
                ContentNative.DeletePaths(deleted);
                if (!ContentNative.Commit())
                {
                    _problem = "提交正文失败：" + ContentNative.LastError;
                    return;
                }
            }
            Interlocked.Add(ref _docs, -deleted.Count);
        }

        foreach (string path in changed)
        {
            if (TryMakeDoc(path, out ContentDoc? doc, out _)) IncrPending.Add(doc!);
        }

        /* **攒批提交**：以前每 40 条提交一次，于是 2 万条 = 500 次提交 = 500 轮段合并，
         * 整机被拖到"点什么都没反应"（用户报的卡死）。现在攒到 200 条或 1.5 秒才提交一次。 */
        long now = Environment.TickCount64;
        bool due = IncrPending.Count >= 200
                   || (IncrPending.Count > 0 && now >= Interlocked.Read(ref _incrCommitAt));
        if (!due) return;

        var docs = new List<ContentDoc>(IncrPending);
        IncrPending.Clear();
        Interlocked.Exchange(ref _incrCommitAt, now + 1500);

        lock (WriteGate)
        {
            if (docs.Count > 0 && ContentNative.AddDocs(docs) < 0)
            {
                _problem = "写入正文失败：" + ContentNative.LastError;
                return;
            }
            var watch = Stopwatch.StartNew();
            if (!ContentNative.Commit())
            {
                _problem = "提交正文失败：" + ContentNative.LastError;
                return;
            }
            Volatile.Write(ref _lastCommitMs, (int)watch.ElapsedMilliseconds);
        }

        if (docs.Count > 0) Interlocked.Add(ref _docs, docs.Count);
        ReloadStatsQuietly();
    }

    private static readonly List<ContentDoc> IncrPending = new();
    private static long _incrCommitAt;

    // ─────────────────────────── 查询 ───────────────────────────

    internal static ContentSearchResult Search(ContentQuery query, int limit)
    {
        if (!Ready)
        {
            return new ContentSearchResult { Ok = false, Error = "正文索引未就绪（" + _status + "）" };
        }
        return ContentNative.Search(query, limit);
    }

    // ─────────────────────────── 构建 ───────────────────────────

    private static void StartBuild(string[] roots, bool clearFirst)
    {
        Volatile.Write(ref _building, 1);
        Interlocked.Exchange(ref _addedThisRun, 0);
        Interlocked.Exchange(ref _visited, 0);
        _failReasons.Clear();
        _phase = "扫描";
        _status = "正在建立正文索引";
        Volatile.Write(ref _progress, -1);

        var token = _cts?.Token ?? CancellationToken.None;
        var thread = new Thread(() => BuildWorker(roots, clearFirst, token))
        {
            IsBackground = true,
            Name = "qbar-content-build",
            /* 最低优先级：这一步会把整机拖慢（用户报的"卡死"就是这么来的），
             * 宁可慢一点，也不能让前台打字卡。 */
            Priority = ThreadPriority.Lowest,
        };
        thread.Start();
        PublishProgress(-1, force: true);
    }

    private static void BuildWorker(string[] roots, bool clearFirst, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var docs = new List<ContentDoc>(BuildBatch);
        long added = 0, failed = 0, skipped = 0, visited = 0;

        try
        {
            if (clearFirst)
            {
                lock (WriteGate) ContentNative.Clear();
            }

            /* ---- ① 扫描阶段 ----
             * 这里必须**有预算**：用户选了 C:\ 之后目录项是几百万条，
             * 第一版只限制"收录多少个候选文件"，于是扫描阶段能跑十几分钟、
             * 界面上永远是 0%（第一版的事故）。现在按"看过的目录项"和"墙钟"双上限，
             * 而且每 1024 条就往界面报一次"扫描到哪儿了"。 */
            _phase = "扫描";
            _status = "正在扫描文件";
            Volatile.Write(ref _progress, -1);
            PublishProgress(-1, force: true);

            var scanWatch = Stopwatch.StartNew();
            var files = new List<(string Path, string Ext)>();
            bool budgetHit = false;

            foreach (string root in roots)
            {
                foreach (string file in EnumerateFiles(root, token))
                {
                    if (token.IsCancellationRequested) break;

                    visited++;
                    if ((visited & 0x3FF) == 0)
                    {
                        Interlocked.Exchange(ref _visited, visited);
                        _status = "正在扫描：" + FormatCount(visited) + " 项 · 候选 "
                                  + FormatCount(files.Count);
                        PublishProgress(-1);
                        if (visited >= MaxScanEntries || scanWatch.ElapsedMilliseconds >= MaxScanMs)
                        {
                            budgetHit = true;
                            break;
                        }
                    }

                    if (files.Count >= _options.MaxFiles) { budgetHit = true; break; }

                    string ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                    if (!TextExtractor.Supports(ext)) { skipped++; continue; }
                    if (!ExtAllowed(ext)) { skipped++; continue; }
                    files.Add((file, ext));
                }
                if (token.IsCancellationRequested || budgetHit || files.Count >= _options.MaxFiles) break;
            }

            Interlocked.Exchange(ref _visited, visited);
            if (budgetHit)
            {
                /* 命中预算 = 这一轮只索引一部分。状态里写明，设置页也显示，
                 * 用户才知道"不是全盘都进来了"，可以改范围再重建。 */
                _problem = "扫描达到上限（" + FormatCount(visited) + " 项 / "
                           + (int)scanWatch.Elapsed.TotalSeconds + " s），本轮只索引已收集的 "
                           + FormatCount(files.Count) + " 个文件";
                CoreLog.Warn("正文索引：" + _problem);
            }

            /* ---- ② 抽取阶段 ---- */
            _phase = "抽取";
            int total = Math.Max(1, files.Count);
            int done = 0;
            Volatile.Write(ref _progress, 0);
            _status = "正在抽取正文 0/" + FormatCount(total);
            PublishProgress(0, force: true);

            foreach (var (path, ext) in files)
            {
                if (token.IsCancellationRequested) break;
                done++;

                if (done % 8 == 0)
                {
                    int pct = (int)Math.Min(99, done * 100L / total);
                    _status = "正在抽取正文 " + FormatCount(done) + "/" + FormatCount(total);
                    PublishProgress(pct);
                }

                if (!TryMakeDoc(path, ext, out ContentDoc? doc, out string why))
                {
                    failed++;
                    _failReasons.AddOrUpdate(why, 1, (_, n) => n + 1);
                    continue;
                }
                docs.Add(doc!);

                if (docs.Count >= BuildBatch)
                {
                    if (!FlushBatch(docs, out string error))
                    {
                        _problem = error;
                        break;
                    }
                    added += BuildBatch;
                    docs.Clear();
                    /* 批次之间让一下 IO：合并段是这里最贵的一段，
                     * 连续跑会把整机拖到"点什么都没反应"（用户报的卡死就是这样）。 */
                    System.Threading.Thread.Sleep(8);
                }
            }

            if (docs.Count > 0 && !token.IsCancellationRequested)
            {
                if (!FlushBatch(docs, out string error)) _problem = error;
                else added += docs.Count;
            }

            Interlocked.Exchange(ref _docs, added);
            Interlocked.Exchange(ref _failed, failed);
            Interlocked.Exchange(ref _skipped, skipped);
            Volatile.Write(ref _lastCommitMs, (int)watch.ElapsedMilliseconds);
            ReloadStatsQuietly();

            if (!token.IsCancellationRequested)
            {
                WriteState(roots, done: true);
                Volatile.Write(ref _ready, 1);
                Volatile.Write(ref _progress, 100);
                _phase = "就绪";
                _status = "正文索引就绪";
                StartIncremental();
            }
            else
            {
                _phase = "已中断";
                _status = "正文索引建立被中断";
            }
        }
        catch (OperationCanceledException)
        {
            _phase = "已中断";
            _status = "正文索引建立被中断";
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "建立正文索引");
            _problem = ex.Message;
            _phase = "失败";
            _status = "建立正文索引失败";
        }
        finally
        {
            Volatile.Write(ref _building, 0);
            /* 收尾一定要报一次：界面据此关掉右下角那条常驻进度、弹"已完成"。
             * 中断/失败也算收尾，否则进度条会永远挂在那儿。 */
            PublishProgress(token.IsCancellationRequested ? -1 : 100, force: true);
        }
    }

    private static bool FlushBatch(List<ContentDoc> docs, out string error)
    {
        error = "";
        lock (WriteGate)
        {
            if (ContentNative.AddDocs(docs) < 0)
            {
                error = "写入正文失败：" + ContentNative.LastError;
                return false;
            }
            var watch = Stopwatch.StartNew();
            if (!ContentNative.Commit())
            {
                error = "提交正文失败：" + ContentNative.LastError;
                return false;
            }
            Volatile.Write(ref _lastCommitMs, (int)watch.ElapsedMilliseconds);
        }
        return true;
    }

    /// <summary>一条文件 → 一条正文文档；抽不出来返回 false（不写空文档，避免每轮重抽）。</summary>
    private static bool TryMakeDoc(string path, out ContentDoc? doc, out string reason)
    {
        doc = null;
        reason = "";
        try
        {
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return TryMakeDoc(path, ext, out doc, out reason);
        }
        catch { return false; }
    }

    private static bool TryMakeDoc(string path, string ext, out ContentDoc? doc, out string reason)
    {
        doc = null;
        reason = "";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { reason = "missing"; return false; }
            if (info.Length > _options.MaxFileBytes) { reason = "too-large"; return false; }
            if ((info.Attributes & (FileAttributes.System | FileAttributes.Temporary)) != 0 && !_options.IncludeHidden)
            {
                reason = "system";
                return false;
            }

            if (!TextExtractor.TryExtract(path, ext, _options.MaxFileBytes, _options.MaxPdfPages,
                                          out string text, out reason))
                return false;

            doc = new ContentDoc
            {
                Path = path,
                Ext = ext,
                IsDir = 0,
                Size = info.Length,
                Day = QbarSyntaxValues.ToDay(info.LastWriteTime),
                Depth = QbarNameScore.DepthOf(path),
                Content = text,
            };
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return false;
        }
    }

    // ─────────────────────────── 根目录 / 枚举 ───────────────────────────

    /// <summary>
    /// 正文索引的根：只用**用户目录**，不碰盘根。
    ///
    /// 注意不能拿 <see cref="QbarRoots.Compute"/> 的结果再过滤：那一份为了文件名索引
    /// 会把用户目录**折叠进盘根**（"C:\ 已经覆盖 C:\Users\x"），于是过滤掉盘根之后
    /// 一个根都不剩 —— 第一版就是这么踩的（构建 0 条、状态却"就绪"）。
    /// 所以这里自己列：用户目录 + 被 OneDrive 重定向的那几个。
    /// </summary>
    private static string[] BuildRoots()
    {
        var list = new List<string>();

        void Consider(string? path, bool allowDriveRoot)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch { return; }
            if (!allowDriveRoot && full.Length <= 3) return;   // "C:\" 这种盘根
            if (!Directory.Exists(full)) return;
            foreach (string existing in list)
            {
                if (QbarRoots.IsSameOrUnder(full, existing)) return;   // 已被更上层覆盖
            }
            string normalized = full.EndsWith(":\\", StringComparison.Ordinal)
                ? full                          // 盘根保留 "D:\" 形式（去掉斜杠会变成 "D:"，语义不同）
                : full.TrimEnd('\\');
            list.Add(normalized);
        }

        /* 用户在设置页选过范围：**完全按他选的来**（盘根也允许 —— 那是明确的选择）。
         * 没选过才退回"用户目录"，这样默认行为不会在升级后突然开始扫整块盘。 */
        if (_options.Roots != null && _options.Roots.Count > 0)
        {
            foreach (string root in _options.Roots) Consider(root, allowDriveRoot: true);
            return list.ToArray();
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "";
        Consider(profile, allowDriveRoot: false);
        Consider(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), allowDriveRoot: false);
        Consider(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), allowDriveRoot: false);
        if (profile.Length > 0) Consider(Path.Combine(profile, "Downloads"), allowDriveRoot: false);
        Consider(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), allowDriveRoot: false);
        Consider(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), allowDriveRoot: false);

        return list.ToArray();
    }

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "WinSxS", "$Recycle.Bin", "System Volume Information", "servicing",
        "$SysReset", "Recovery", "MSOCache", "Config.Msi", "PerfLogs", "Windows.old",
        "node_modules", ".git", ".svn", "obj", "bin", "Debug", "Release",
        "AppData", "Application Data", "ProgramData", "Program Files", "Program Files (x86)",
        "Temp", "tmp", "cache", "Cache", "Caches", "packages", "NuGet", ".nuget",
        ".venv", "venv", "__pycache__", ".gradle", ".m2", ".cargo", ".rustup",
        "target", "dist", "build", "out", "vendor", "OneDriveTemp",
        "file-index-tantivy", "file-content-tantivy",
    };

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken token)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (token.IsCancellationRequested) yield break;
            string dir = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch { continue; }

            foreach (FileSystemInfo e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (e is DirectoryInfo d)
                {
                    if (SkipDirs.Contains(d.Name)) continue;
                    stack.Push(d.FullName);
                }
                else if (e is FileInfo f)
                {
                    yield return f.FullName;
                }
            }
        }
    }

    private static bool ExtAllowed(string ext)
    {
        if (_options.Extensions == null) return false;
        foreach (string raw in _options.Extensions.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Trim().TrimStart('.').Equals(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ─────────────────────────── 状态 / 统计 ───────────────────────────

    private sealed class ContentState
    {
        public int Version { get; set; }
        public bool Done { get; set; }
        public string[] Roots { get; set; } = Array.Empty<string>();
        public long BuiltAtTicks { get; set; }
    }

    private static ContentState? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize<ContentState>(File.ReadAllText(StatePath));
        }
        catch { return null; }
    }

    private static void WriteState(string[] roots, bool done)
    {
        try
        {
            var state = new ContentState
            {
                Version = SchemaVersion,
                Done = done,
                Roots = roots,
                BuiltAtTicks = done ? DateTime.Now.Ticks : 0,
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "写正文索引状态"); }
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

    private static void ReloadStatsQuietly()
    {
        try
        {
            ContentStats stats = ContentNative.Stats();
            if (!stats.Ok) return;
            Interlocked.Exchange(ref _docs, stats.Docs);
            Interlocked.Exchange(ref _bytes, stats.Bytes);
        }
        catch { /* 统计失败不影响索引本身 */ }
    }

    private static void LoadStats(string status)
    {
        ReloadStatsQuietly();
        _status = status;
    }

    private static QbarContentOptions ReadOptions()
    {
        try
        {
            QbarContentOptions opt = SettingsStore.Load().Qbar.Content ?? new QbarContentOptions();

            /* 三个环境变量只用于诊断/自检（和 INPUTPLUS_QBAR_NOFACETS 同类）：
             * 让"只抽 300 个 txt 看看链路通不通"这种验证不必去改用户的设置文件。 */
            string? envExts = Environment.GetEnvironmentVariable("INPUTPLUS_CONTENT_EXTS");
            if (!string.IsNullOrWhiteSpace(envExts)) opt.Extensions = envExts;

            string? envMax = Environment.GetEnvironmentVariable("INPUTPLUS_CONTENT_MAXFILES");
            if (int.TryParse(envMax, out int maxFiles) && maxFiles > 0) opt.MaxFiles = maxFiles;

            string? envMb = Environment.GetEnvironmentVariable("INPUTPLUS_CONTENT_MAXMB");
            if (int.TryParse(envMb, out int maxMb) && maxMb > 0) opt.MaxFileBytes = maxMb * 1024 * 1024;

            /* 逃生开关：索引把机器拖住时，用 INPUTPLUS_CONTENT_DISABLE=1 启动一次，
             * 不进正文索引也能用文件名搜索。比让用户去设置页点开关更可靠。 */
            if (Environment.GetEnvironmentVariable("INPUTPLUS_CONTENT_DISABLE") == "1") opt.Enabled = false;

            return opt;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取正文索引设置");
            return new QbarContentOptions();
        }
    }
}
