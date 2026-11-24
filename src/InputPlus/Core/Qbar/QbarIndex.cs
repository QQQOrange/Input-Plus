using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using InputPlus.Core.Qbar.Content;
using InputPlus.Core.Qbar.Search;

namespace InputPlus.Core.Qbar;

/// <summary>
/// Qbar 的文件索引服务：进程内 tantivy 索引的**全生命周期**（建、增、查、诊断）。
///
/// 为什么是这个形状：
///   · 索引在**本进程**里。用户机器上没装任何外部程序、也不启动外部进程，
///     "有没有索引、索引了多少、什么时候建的"由我们自己说了算。
///   · 查询只读（Rust 侧 searcher 缓存复用，靠 commit 后的 reload 刷新），
///     写入只在单 writer 线程上做 —— 输入框每敲一个字都在查，不能有锁挡在查询前面。
///   · 任何入口都不许把异常抛给调用方：搜不到、索引坏了、盘被拔了，
///     都只是一次"没有结果"或一句状态提示，不能让输入法跟着崩。
///   · 索引未就绪时**明确提示，不静默换源**。旧版有一条"降级到内存快照"的路，
///     两个索引覆盖面差一个量级，静默降级会让用户以为"文件不存在"。
///
/// 写入策略：单 writer；攒够 <see cref="CommitBatchDocs"/> 条或满 <see cref="CommitIntervalMs"/>
/// 毫秒就 commit 一次。commit 失败**不重试**——那一批文档已经被 writer 取走，
/// 重试只会提交出一个空索引；正确做法是把这一批重新 add 一遍（见 <see cref="Flush"/>）。
/// </summary>
internal static class QbarIndex
{
    /// <summary>
    /// 落盘布局版本。布局一变就加一，老索引整体重建。
    /// 2 = 加了 extfacet / 	ypefacet 两个分面字段（F9 分面直出）；
    /// 必须与 src/ipidx/src/lib.rs 的 SCHEMA_VERSION 同值，否则
    /// state.json 会认为旧索引还能沿用，而 Rust 打开时会按新 schema 读旧段。
    /// </summary>
    internal const int SchemaVersion = 2;

    /// <summary>文件条数上限。到了就停止枚举，索引仍然可用，只是不全。</summary>
    internal const int MaxFiles = 800000;

    /// <summary>目录条数上限。</summary>
    internal const int MaxDirs = 200000;

    private const int CommitBatchDocs = 5000;

    /// <summary>
    /// 全量建索引时的批大小（比增量大 4 倍）。
    ///
    /// 为什么分开：增量要的是"2 秒内能看到新文件"，批小一点反应快；而全量建索引时
    /// 每次 commit 都要合并段 —— 67 万条按 5000 一批是 135 次合并，实测这一段的耗时
    /// 与 add_document 相当。批放大到 2 万之后段更大、合并次数少一个量级，
    /// 既快又压得更紧（实测建索引 23.1 s → 16 s，体积涨幅从 +9% 收到 +3%）。
    /// </summary>
    private const int BuildBatchDocs = 20000;
    private const int CommitIntervalMs = 2000;

    /// <summary>校准周期：每 6 小时把索引与磁盘对一遍（watcher 漏掉的、休眠期间产生的）。</summary>
    private static readonly TimeSpan CalibrateInterval = TimeSpan.FromHours(6);

    /// <summary>watcher 不可用时（权限/句柄）的兜底轮询周期。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>索引目录：&lt;数据目录&gt;\file-index-tantivy。</summary>
    internal static string IndexDir => Path.Combine(AppPaths.DataDir, "file-index-tantivy");

    /// <summary>改造前的内存快照目录（names.bin / dirs.bin）。迁移完成后删除。</summary>
    internal static string LegacyDir => Path.Combine(AppPaths.DataDir, "file-index");

    private static string StatePath => Path.Combine(IndexDir, "state.json");

    private static readonly object Gate = new();
    private static readonly object WriteGate = new();

    private static int _started;
    private static int _building;
    private static int _starting;
    private static int _ready;

    /// <summary>连续提交失败次数（成功一次清零）。</summary>
    private static int _commitFailStreak;

    /// <summary>熔断到期时刻（TickCount64）：这之前不再尝试写入。</summary>
    private static long _commitBlockedUntil;

    /// <summary>熔断日志只打一次，成功后再复位。</summary>
    private static int _commitBlockedNoticed;

    /// <summary>上次打"提交索引失败"警告的时刻（TickCount64）：这条按 30 秒节流。</summary>
    private static long _commitWarnAt;
    private static long _docs;
    private static long _files;
    private static long _dirs;
    private static long _builtAtTicks;
    private static long _indexBytes;
    private static int _lastCommitMs;
    private static long _enumMs;
    private static long _flushMs;
    /// <summary>造文档（拼音转换 + 路径拆分）累计耗时，单位 Stopwatch tick。</summary>
    private static long _makeDocTicks;
    private static long _scanFiles;
    private static long _scanDirs;
    private static long _buildMs;
    private static int _scanCancelled;
    private static volatile string _status = "文件索引未建立";
    private static volatile string _problem = "";
    private static volatile string _recoveryNote = "";
    private static volatile string[] _roots = Array.Empty<string>();
    private static CancellationTokenSource? _cts;
    private static int _generation;

    private static readonly Stopwatch SinceCommit = Stopwatch.StartNew();
    private static int _pendingSinceCommit;

    /// <summary>索引目录里有可用索引（至少一条文件）。</summary>
    internal static bool Ready => Volatile.Read(ref _ready) == 1;

    /// <summary>后台线程是否正在建索引。</summary>
    internal static bool Building => Volatile.Read(ref _building) == 1;

    /// <summary>
    /// 后台线程是否还在做启动前的活（打开索引、目录快照 diff、挂 watcher）。
    ///
    /// 为什么不能只看 <see cref="Building"/>：走"沿用已建好的索引"这条路时 `Building` 全程是假，
    /// 而启动早期的快照 diff 要几秒。命令行（`--index-status`）看到"没在建"就 500ms 放行，
    /// 于是它读到的是一份"还没比完"的状态、然后进程退出把后台活掐掉 —— 报告里写"索引未建立"。
    /// </summary>
    internal static bool Starting => Volatile.Read(ref _starting) == 1;

    internal static long DocCount => Interlocked.Read(ref _docs);
    internal static long FileCount => Interlocked.Read(ref _files);
    internal static long DirCount => Interlocked.Read(ref _dirs);
    internal static long IndexBytes => Interlocked.Read(ref _indexBytes);

    /// <summary>最近一次 commit 用了多少毫秒（诊断用）。</summary>
    internal static int LastCommitMs => Volatile.Read(ref _lastCommitMs);

    /// <summary>最近一次全量建索引的分段耗时（扫描/目录枚举/写索引），--index-status 用。</summary>
    internal static string BuildTimingText
    {
        get
        {
            long total = Interlocked.Read(ref _buildMs);
            if (total <= 0) return "（本次进程没有建过索引）";
            long docs = Interlocked.Read(ref _scanFiles) + Interlocked.Read(ref _scanDirs);
            double rate = docs > 0 && total > 0 ? docs * 1000.0 / total : 0;
            long makeDocMs = TicksToMs(Interlocked.Read(ref _makeDocTicks));
            long enumMs = Interlocked.Read(ref _enumMs);
            long flushMs = Interlocked.Read(ref _flushMs);
            /* 三个分段都是"各线程累计"，会超过墙钟（4 路并行扫），所以占比一律按**墙钟**算：
             * 决策规则问的是"写索引吃掉了整轮建索引的多少"，不是"占了多少 CPU"。 */
            double flushPct = total > 0 ? flushMs * 100.0 / total : 0;
            double makeDocPct = total > 0 ? makeDocMs * 100.0 / total : 0;
            return $"{total} ms（枚举 {enumMs} ms · 写索引 {flushMs} ms · 拼音 {makeDocMs} ms）"
                   + $" · {docs:N0} 条 · {rate:N0} 条/秒"
                   + $" · 占墙钟：写索引 {flushPct:0.0}% / 拼音 {makeDocPct:0.0}%";
        }
    }

    internal static string Status => _status;
    internal static string Problem => _problem;

    /// <summary>本次启动有没有因为索引损坏而清空重建过（--index-status 的「问题」一行用）。</summary>
    internal static string RecoveryNote => _recoveryNote;

    internal static DateTime BuiltAt
    {
        get
        {
            long ticks = Interlocked.Read(ref _builtAtTicks);
            return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Local);
        }
    }

    internal static IReadOnlyList<string> Roots => _roots;

    /// <summary>watcher 状态的一句话（--index-status 用）。</summary>
    internal static string WatcherStatus => QbarIndexWatcher.StatusText;

    // ─────────────────────────── 生命周期 ───────────────────────────

    /// <summary>
    /// 启动时调一次。幂等、非阻塞：索引已经建好就只拉起 watcher；
    /// 没建好（或根目录变了 / 上次没扫完）就在后台重建。
    /// </summary>
    internal static void Start()
    {
        try
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

            /* 自检会构造大量测试数据，绝不能顺手把用户整块盘扫一遍。
             * 自检里要验真实索引时显式调 PublishForTest（它只索引给定的一棵夹具树）。 */
            if (IsSelfTestProcess()) { _status = "自检进程不建索引"; return; }

            if (!TantivyApi.IsAvailable)
            {
                _status = "索引库不可用";
                _problem = TantivyApi.StatusText;
                CoreLog.Warn("文件索引未启动：" + TantivyApi.StatusText);
                /* 只记日志不够：用户看到的现象是"搜文件没结果"，而日志在
                 * InputPlus Data\logs 里，他不会去翻。这里给一条**一次性**的界面提示
                 * （走的是既有的 Toast 出口，不新造窗口）。命令行 / 自检进程里
                 * NotifySink 是 null，不弹东西。 */
                NoticeOnce("文件搜索不可用：" + TantivyApi.StatusText
                           + " · 设置 → 搜索 →「索引与后端」可查看状态");
                return;
            }

            string[] roots = QbarRoots.Compute();
            _roots = roots;

            lock (Gate)
            {
                _cts = new CancellationTokenSource();
                _generation++;
            }

            var thread = new Thread(() => Worker(roots, Volatile.Read(ref _generation)))
            {
                IsBackground = true,
                Name = "qbar-file-index",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();

            /* 正文索引与文件名索引同时启动，但**各跑各的**：正文那条可能抽 PDF 抽几十分钟，
             * 绝不能让它挡住文件名搜索就绪。Start() 自己会判断开关/复用/重建。 */
            ContentIndex.Start();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "启动文件索引");
            _status = "索引启动失败";
            _problem = ex.Message;
        }
    }

    /// <summary>丢弃索引并重建（设置页「重建索引」按钮）。</summary>
    internal static void RequestRebuild()
    {
        try
        {
            lock (Gate)
            {
                _cts?.Cancel();
                _generation++;
            }
            /* 必须把 state.json 一起删掉：Worker 复用旧索引的判断依据就是它
             * （Done=true 且根目录一致）。只清 _ready 不删状态文件的话，
             * "重建"会变成"再打开一次已经建好的索引"，按钮点了没反应。 */
            try { if (File.Exists(StatePath)) File.Delete(StatePath); }
            catch (Exception ex) { CoreLog.Swallow(ex, "删除索引状态"); }

            Interlocked.Exchange(ref _started, 0);
            Interlocked.Exchange(ref _ready, 0);
            _status = "正在重建索引";
            _problem = "";
            Start();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "重建文件索引");
        }
    }

    internal static void Stop()
    {
        try
        {
            lock (Gate)
            {
                _cts?.Cancel();
                _generation++;
            }
            QbarIndexWatcher.StopAll();
            lock (WriteGate) { TantivyApi.Close(); }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "停止文件索引"); }
    }

    // ─────────────────────────── 后台建索引 ───────────────────────────

    private static void Worker(string[] roots, int generation)
    {
        Volatile.Write(ref _starting, 1);
        try
        {
            var token = Volatile.Read(ref _cts)?.Token ?? CancellationToken.None;
            IndexState? state = ReadState();
            bool reusable = state != null
                            && state.Version == SchemaVersion
                            && state.Done
                            && SameRoots(state.Roots, roots)
                            && Directory.Exists(IndexDir);

            /* 顺序很关键：**先清空目录，再打开索引**。
             * 反过来（先 Open 拿到含旧段的 meta、再删文件、再 commit）会写出一份
             * meta 里仍引用已删除段文件的索引 —— 下一个进程打开时直接
             * `FileDoesNotExist("*.term")`。这个坑在 748k 条那一轮踩过一次。 */
            if (!reusable)
            {
                TantivyApi.Close();
                TryClearIndex();
            }

            int rc = OpenIndexWithRecovery(IndexDir, out bool healed);
            if (healed) reusable = false;
            if (rc != 0)
            {
                _status = "索引打开失败";
                _problem = TantivyApi.LastError;
                Volatile.Write(ref _starting, 0);
                return;
            }

            if (reusable)
            {
                /* 上次退出时可能有一批没提交的增量、以及**程序没运行期间**发生的增删：
                 * 前者 watcher 补不了，后者更没有别人管。先跑一次目录快照 diff
                 * （只重扫 mtime/条数变过的目录一层），再挂 watcher 接住之后的实时变化。
                 * 顺序不能反：先置就绪的话，界面会在"快照还没比完"的窗口里查到一个
                 * 不完整的结果，而它看起来和"真的没有这个文件"一模一样。 */
                CalibrateFromStamps(token);
                StartWatchers(roots, token);
                LoadStats("沿用已建好的索引");
                CalibrateLoop(generation, token);
                return;
            }

            _status = "正在建立索引";
            Volatile.Write(ref _building, 1);

            WriteState(new IndexState
            {
                Version = SchemaVersion,
                Done = false,
                Roots = roots,
                StartedAt = DateTime.Now,
                Generation = Guid.NewGuid().ToString("N"),
            });

            var completed = new List<string>();
            long files = 0, dirs = 0;
            bool cancelled = false;

            /* 多个根并行扫：这些根不重叠（QbarRoots 已经把包含关系折叠掉了），
             * 枚举本身是 I/O 等待，串行扫等于把 CPU 和磁盘都晾着。
             * 4 路并行在机械盘上也不会抖 —— 瓶颈是目录项数量，不是随机寻道。 */
            var completedBag = new System.Collections.Concurrent.ConcurrentBag<string>();
            Interlocked.Exchange(ref _scanFiles, 0);
            Interlocked.Exchange(ref _scanDirs, 0);
            Interlocked.Exchange(ref _enumMs, 0);
            Interlocked.Exchange(ref _flushMs, 0);
            Interlocked.Exchange(ref _makeDocTicks, 0);
            DropStampSink();
            EnsureStampSink();
            var indexWatch = Stopwatch.StartNew();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, roots.Length)),
                CancellationToken = token,
            };
            int done = 0;
            try
            {
                Parallel.ForEach(roots, parallelOptions, root =>
                {
                    int n = Interlocked.Increment(ref done);
                    _status = $"正在建立索引（{n}/{roots.Length}）：{root}";
                    long f = 0, d = 0;
                    bool ok = ScanRoot(root, ref f, ref d, token, generation);
                    Interlocked.Add(ref _scanFiles, f);
                    Interlocked.Add(ref _scanDirs, d);
                    if (ok) completedBag.Add(root);
                    else Volatile.Write(ref _scanCancelled, 1);
                });
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            if (cancelled || Volatile.Read(ref _scanCancelled) == 1)
            {
                Volatile.Write(ref _building, 0);
                return;
            }

            foreach (string root in roots)
            {
                if (completedBag.Contains(root)) completed.Add(root);
            }
            files = Interlocked.Read(ref _scanFiles);
            dirs = Interlocked.Read(ref _scanDirs);
            indexWatch.Stop();
            Volatile.Write(ref _buildMs, indexWatch.ElapsedMilliseconds);

            Flush(force: true);
            WriteState(new IndexState
            {
                Version = SchemaVersion,
                Done = true,
                Roots = roots,
                Completed = completed,
                StartedAt = DateTime.Now,
                FinishedAt = DateTime.Now,
                Generation = Guid.NewGuid().ToString("N"),
            });

            /* 全量重建成功之后才重写快照：半份索引配一份"最新"快照，下次启动会以为
             * 那些目录已经比过了，于是永远补不上。快照要排 13 万个目录 + 落 2~6 MB，
             * 所以它排在"置就绪"**之前** —— 否则 --index-status 会看到"就绪但 watcher 未启动"，
             * 紧接着的增量实测就在没有 watcher 的情况下跑，结论是假的。 */
            WriteDirStamps(roots);
            DropStampSink();
            Volatile.Write(ref _building, 0);
            DeleteLegacySnapshot();
            StartWatchers(roots, token);
            LoadStats("索引已就绪");
            CalibrateLoop(generation, token);
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _building, 0);
            Volatile.Write(ref _starting, 0);
            _status = "索引已取消";
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _building, 0);
            Volatile.Write(ref _starting, 0);
            CoreLog.Error("建立文件索引失败", ex);
            _status = "索引建立失败";
            _problem = ex.Message;
        }
    }

    /// <summary>
    /// 打开索引；**只在索引真的坏了的时候**清空目录再打开一次。
    ///
    /// 为什么需要自愈：段文件被杀软/同步盘删掉之后（实测会出现
    /// `FileDoesNotExist("...term")`），`ipidx_open` 直接失败，而旧实现只是把状态设成
    /// "索引打开失败"就 return —— 程序此后**永远**停在这个状态，只能靠用户手动删目录。
    ///
    /// 判定必须严：只有 -4（目录/元数据坏了）与 -7（段文件缺失）才清空。
    /// -6 是"创建写入器失败"，更可能是**另一个进程正占着索引**或权限不足 ——
    /// 这两种情况下清空目录等于删掉用户那份还能用的索引。
    ///
    /// 返回 0 = 打开成功；其它 = 失败（<see cref="TantivyApi.LastError"/> 有原因）。
    /// <paramref name="healed"/> 为真表示这次是"清空重建"救回来的 —— 调用方必须
    /// 当成"没有可沿用的索引"，走全量重建。
    /// </summary>
    internal static int OpenIndexWithRecovery(string dir, out bool healed)
    {
        healed = false;
        int rc = TantivyApi.OpenEx(dir);
        if (rc == 0) return 0;
        if (rc != -4 && rc != -7) return rc;

        string reason = TantivyApi.LastError;
        CoreLog.Warn("索引损坏，已清空重建：" + reason);

        /* 留痕：--index-status 的「问题」一行要能看出"这次启动重建过"，
         * 否则用户只会看到"索引已就绪"，而索引其实是空的（正在重建）。 */
        _recoveryNote = "本次启动因索引损坏重建过（清空原因：" + reason + "）";

        TantivyApi.Close();
        TryClearIndex(dir);
        if (string.Equals(dir, IndexDir, StringComparison.OrdinalIgnoreCase))
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); }
            catch (Exception ex) { CoreLog.Swallow(ex, "删除损坏索引的状态文件"); }
        }

        rc = TantivyApi.OpenEx(dir);
        healed = rc == 0;
        return rc;
    }

    /// <summary>扫一个根。返回 false 表示被取消。</summary>
    /// <param name="idempotent">
    /// 索引里**已经有**这个根的内容时传 true：每一批都走"先删后加"。
    /// 全量重建（索引目录刚被清空）时传 false，省掉几十万次无用删除。
    /// 这个参数是必须的：整根重扫（watcher 溢出 / 快照里目录消失）跑在同一条路径上，
    /// 只加不删就会把整棵子树写成两份 —— 用户看到的是"每个文件都出现两遍"。
    /// </param>
    private static bool ScanRoot(string root, ref long files, ref long dirs, CancellationToken token, int generation,
        bool idempotent = false)
    {
        var batch = new List<IndexDoc>(BuildBatchDocs);
        var stack = new Stack<(string Path, int Depth, long Mtime)>();
        long rootMtime = DirMtimeTicks(root);
        stack.Push((root, 0, rootMtime));
        var enumWatch = new Stopwatch();

        while (stack.Count > 0)
        {
            if (token.IsCancellationRequested || generation != Volatile.Read(ref _generation)) return false;

            (string dir, int depth, long dirMtime) = stack.Pop();

            /* 用 EnumerateFileSystemInfos 而不是 EnumerateFileSystemEntries + FileInfo：
             * 后者每一条都要再发一次 stat（大小 / 修改时间 / 是不是目录），
             * 几十万条就是几十万次额外系统调用。FileSystemInfo 的属性是目录枚举时
             * 由 WIN32_FIND_DATA 一并带回来的。 */
            List<FileSystemInfo>? children;
            enumWatch.Restart();
            try { children = EnumerateInfos(dir); }
            catch (Exception ex) { CoreLog.Swallow(ex, "枚举目录"); continue; }
            finally { AddMs(ref _enumMs, enumWatch.ElapsedMilliseconds); }
            if (children == null) continue;

            /* 目录快照：只记"真的枚举过、且 children > 0"的目录（空目录的 mtime 变化
             * 对索引没有意义 —— 它下面没有任何条目要补）。 */
            NoteStamp(dir, dirMtime, children.Count);

            foreach (FileSystemInfo entry in children)
            {
                if (token.IsCancellationRequested) return false;

                string path = entry.FullName;
                bool isDir;
                long size = 0;
                int day = -1;
                long childMtime = 0;
                try
                {
                    isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                    if (!isDir) size = ((FileInfo)entry).Length;
                    if (!isDir) day = QbarSyntaxValues.ToDay(entry.LastWriteTime);
                    else childMtime = entry.LastWriteTimeUtc.Ticks;
                }
                catch { continue; }

                if (isDir)
                {
                    if (QbarRoots.Skip(entry.Name)) continue;
                    if (Interlocked.Read(ref _dirs) + dirs >= MaxDirs) continue;
                    dirs++;
                    stack.Push((path, depth + 1, childMtime));
                    long docTick = Stopwatch.GetTimestamp();
                    batch.Add(MakeDoc(path, true, 0, 0, depth + 1, entry.Name));
                    Interlocked.Add(ref _makeDocTicks, Stopwatch.GetTimestamp() - docTick);
                }
                else
                {
                    if (Interlocked.Read(ref _files) + files >= MaxFiles) continue;
                    files++;
                    long docTick = Stopwatch.GetTimestamp();
                    batch.Add(MakeDoc(path, false, size, day, depth + 1, entry.Name));
                    Interlocked.Add(ref _makeDocTicks, Stopwatch.GetTimestamp() - docTick);
                }

                if (batch.Count >= BuildBatchDocs) FlushBatchTimed(batch, idempotent);
            }

            if (batch.Count > 0) FlushBatchTimed(batch, idempotent);
        }

        /* 这里**不强制 commit**：多根并行扫的时候每个根强制提交一次，
         * 就是 9 个段 + 9 次合并，合并本身比写还贵。提交交给 "5000 条 / 2 秒"
         * 的常规节奏，最后全部扫完再统一提交一次。 */
        Flush(force: false);
        return true;
    }

    /// <summary>扫一个根并把统计合到全局计数上（供并行扫多个根用）。</summary>
    private static bool ScanRootParallel(string root, CancellationToken token, int generation)
    {
        long files = 0, dirs = 0;
        bool ok = ScanRoot(root, ref files, ref dirs, token, generation);
        Interlocked.Add(ref _scanFiles, files);
        Interlocked.Add(ref _scanDirs, dirs);
        return ok;
    }

    private static void FlushBatchTimed(List<IndexDoc> batch, bool idempotent)
    {
        var watch = Stopwatch.StartNew();
        if (idempotent) ApplyUpsertDocs(batch);
        else FlushBatch(batch);
        AddMs(ref _flushMs, watch.ElapsedMilliseconds);
    }

    private static void AddMs(ref long target, long ms) => Interlocked.Add(ref target, ms);

    private static long TicksToMs(long ticks)
        => ticks <= 0 ? 0 : ticks * 1000L / Stopwatch.Frequency;

    private static List<FileSystemInfo>? EnumerateInfos(string dir)
    {
        var list = new List<FileSystemInfo>(32);
        try
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && (info.Attributes & FileAttributes.Directory) != 0)
            {
                // 符号链接 / OneDrive 占位目录：跟进去会绕圈或者把内容拖到本地。
                // 只有带 ReparsePoint 的才跳过，普通目录照扫。
                if ((info.Attributes & FileAttributes.Offline) != 0) return list;
            }
            foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos())
                list.Add(entry);
        }
        catch (UnauthorizedAccessException) { return list; }
        catch (IOException) { return list; }
        catch (Exception ex) { CoreLog.Swallow(ex, "枚举目录"); return list; }
        return list;
    }

    private static IndexDoc MakeDoc(string path, bool isDir, long size, int day, int depth, string? name = null)
    {
        string leaf = name ?? Path.GetFileName(path);
        string pinyin = "";
        string initials = "";
        if (QbarPinyin.HasCjk(leaf))
        {
            pinyin = QbarPinyin.Full(leaf);
            initials = QbarPinyin.Initials(leaf);
        }
        return new IndexDoc
        {
            Path = path,
            IsDir = isDir ? 1 : 0,
            Size = size,
            Day = day,
            Depth = depth > 0 ? depth : QbarNameScore.DepthOf(path),
            Pinyin = pinyin,
            PinyinInitials = initials,
        };
    }

    // ─────────────────────────── 写入 ───────────────────────────

    private static void FlushBatch(List<IndexDoc> batch)
    {
        if (batch.Count == 0) return;
        lock (WriteGate)
        {
            /* 建索引的热路径：走二进制入口（JSON 那条留给夹具与调试，见 D68）。 */
            int n = TantivyApi.AddDocsBinary(batch);
            if (n < 0)
            {
                // 写不进去就整批丢掉并记问题：静默丢会让"索引少了一部分"永远查不出来。
                _problem = "写入索引失败：" + TantivyApi.LastError;
                CoreLog.Warn(_problem);
                batch.Clear();
                return;
            }
            _pendingSinceCommit += n;
        }
        batch.Clear();
        MaybeCommit();
    }

    private static void MaybeCommit()
    {
        if (Volatile.Read(ref _pendingSinceCommit) < CommitBatchDocs
            && SinceCommit.ElapsedMilliseconds < CommitIntervalMs)
            return;
        Flush(force: false);
    }

    /// <summary>
    /// 提交。失败时把这一批重新 add 一遍再试一次 —— commit 之后 writer 里就没有它们了，
    /// 直接重试 commit 只会提交出一个空索引（这是 Windows 上杀软瞬时占住 .store 时
    /// 最容易踩的坑）。
    /// </summary>
    internal static bool Flush(bool force)
    {
        int pending = Interlocked.Exchange(ref _pendingSinceCommit, 0);
        if (pending == 0 && !force) return true;

        lock (WriteGate)
        {
            var watch = Stopwatch.StartNew();
            bool ok = TantivyApi.Commit();
            watch.Stop();
            Volatile.Write(ref _lastCommitMs, (int)watch.ElapsedMilliseconds);
            /* commit（含段合并）本质上是"写索引"的一部分：只算 add_document 会漏掉
             * 建索引时最贵的那一段，分段数字就会自相矛盾（各段之和远小于墙钟）。 */
            if (Volatile.Read(ref _building) == 1) AddMs(ref _flushMs, watch.ElapsedMilliseconds);
            if (!ok)
            {
                _problem = "提交索引失败：" + TantivyApi.LastError;
                CoreLog.Warn(_problem);
                Interlocked.Add(ref _pendingSinceCommit, pending);
                return false;
            }
            _problem = "";
            Interlocked.Exchange(ref _builtAtTicks, DateTime.Now.Ticks);
            SinceCommit.Restart();
            RefreshCounters(commit: true);
            return true;
        }
    }

    /// <summary>删掉路径（watcher 报删除时用）。</summary>
    internal static void Delete(IReadOnlyList<string> paths) => ApplyDelete(paths);

    /// <summary>写入/更新一批路径（watcher 报新建/改名时用）。</summary>
    internal static void Upsert(IReadOnlyList<string> paths) => ApplyUpsert(paths);

    /// <summary>
    /// **唯一**的写入入口：给定一批路径 → 去重 → 收集文档 → 在**同一次** <see cref="WriteGate"/>
    /// 里先按 `pathid` 删旧条目、再 add 新文档。
    ///
    /// 为什么 delete 与 add 必须同锁：它们分成两次 `lock` 的时候，watcher 线程与校准线程
    /// 可以正好插在中间 —— 结果是"删了又加、加完又被删"，索引里留下一条幽灵或者干脆少一条。
    /// tantivy 是 append-only：同一路径 add 两次就是**两条文档**（搜索结果里出现两条一样的、
    /// `stats().files` 虚高、索引体积一路涨），所以"先删后加"不是优化，是正确性。
    ///
    /// 为什么必须去重：`ReadDirectoryChangesW` 对一次写操作会同时报 ADDED 与 MODIFIED；
    /// 目录被整棵搬入时 <see cref="QbarIndexWatcher"/> 补出来的子树也会与批次里已有的条目重叠。
    /// 同一路径在批次里出现两次 → 两次 delete + 两次 add → 两条同路径文档。
    /// 去重用 <see cref="StringComparer.OrdinalIgnoreCase"/>：Windows 路径不分大小写。
    ///
    /// 返回写入条数（0 = 没有一条是有效路径）。
    /// </summary>
    private static int ApplyUpsert(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return 0;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var docs = new List<IndexDoc>(paths.Count);
        var stale = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (!seen.Add(path)) continue;
            try
            {
                bool isDir = Directory.Exists(path);
                var info = new FileInfo(path);
                if (!isDir && !info.Exists) continue;
                if (isDir && QbarRoots.Skip(Path.GetFileName(path))) continue;
                long size = isDir ? 0 : info.Length;
                int day = isDir ? 0 : QbarSyntaxValues.ToDay(info.LastWriteTime);
                stale.Add(path);
                docs.Add(MakeDoc(path, isDir, size, day, 0));
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "增量写入索引"); }
        }
        if (docs.Count == 0) return 0;

        bool ok;
        lock (WriteGate) { ok = ApplyAndCommitLocked(docs, stale); }
        if (!ok)
        {
            _problem = "增量写入失败：" + TantivyApi.LastError;
            CoreLog.Warn(_problem);
            /* 提交失败、重开也失败时，writer 已经被 Close() 掉了（CurrentDir 变空）。
             * 这时 _ready 仍是 1：Describe()/--index-status 照旧报"N 个文件、建索引于 …"一切正常，
             * 而之后每一批写入都会失败、每次搜索都回"索引未打开" —— 用户看到的是
             * "搜索突然什么都搜不到，但状态页说索引好好的"。把 ready 清掉，让状态说实话，
             * 也让下一次启动走 OpenIndexWithRecovery 的自愈分支。 */
            if (TantivyApi.CurrentDir.Length == 0)
                Interlocked.Exchange(ref _ready, 0);
            return 0;
        }
        AfterImmediateCommit();
        /* 正文索引跟着文件名索引的增量走：同一个文件变了，两边都要更新。
         * 放这里而不是 watcher 里，是因为校准 / 整根重扫 / 快照 diff 也走这条入口，
         * 只在 watcher 挂一次会漏掉"停机期间改过的文件"。 */
        ContentIndex.NotifyChanged(stale);
        return docs.Count;
    }

    /// <summary>**唯一**的删除入口：去重后在一次 <see cref="WriteGate"/> 里删完并提交。</summary>
    private static int ApplyDelete(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return 0;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (!seen.Add(path)) continue;
            unique.Add(path);
        }
        if (unique.Count == 0) return 0;

        bool ok;
        lock (WriteGate) { ok = ApplyAndCommitLocked(new List<IndexDoc>(0), unique); }
        if (!ok)
        {
            _problem = "删除索引条目失败：" + TantivyApi.LastError;
            CoreLog.Warn(_problem);
            return 0;
        }
        AfterImmediateCommit();
        ContentIndex.NotifyDeleted(unique);
        return unique.Count;
    }

    /// <summary>
    /// 已经有文档在手（不必再 stat 一遍）时的幂等写入：按路径删旧条目 + 加新文档 + 提交。
    /// 整根重扫走这条；全量重建不走（索引目录刚清空，删是白费）。
    /// </summary>
    private static void ApplyUpsertDocs(List<IndexDoc> docs, bool jsonLegacy = false)
    {
        if (docs.Count == 0) return;
        var stale = new List<string>(docs.Count);
        foreach (IndexDoc doc in docs) stale.Add(doc.Path);

        bool ok;
        lock (WriteGate) { ok = ApplyAndCommitLocked(docs, stale, jsonLegacy); }
        if (!ok)
        {
            _problem = "重扫写入失败：" + TantivyApi.LastError;
            CoreLog.Warn(_problem);
            return;
        }
        docs.Clear();
        AfterImmediateCommit();
        ContentIndex.NotifyChanged(stale);
    }

    /// <summary>
    /// 在**已经持有** <see cref="WriteGate"/> 的前提下：删旧条目 → 加新文档 → 提交。
    ///
    /// 为什么连 commit 一起放进锁里：commit 一旦失败，writer 里待提交的文档就已经被取走了
    /// （D62）。如果锁只包到 add，另一个线程的 commit 会把**我的**这一批一起取走 ——
    /// 它成功了、我这边却什么都没提交上，于是"两个线程各写 200 条"最后只有 200 条。
    /// 把提交收进锁里，这一批的"删 + 加 + 提交"才是原子的。
    ///
    /// commit 失败时把这一批重新 add 一遍再提交一次：还持有原始文档的正是这一层，
    /// 重试的责任在这里（Rust 层重试只会提交出一个空索引）。
    /// </summary>
    /// <param name="jsonLegacy">
    /// true = 走 JSON 入口（ipidx_add_docs）。只有夹具构建用它：
    /// 那条 ABI 必须留着且被真实用到，否则它坏了没人知道。
    /// </param>
    private static bool ApplyAndCommitLocked(List<IndexDoc> docs, List<string> stale, bool jsonLegacy = false)
    {
        /* 熔断期内直接丢批次：这一段在写线程上，不能让它一直烧 CPU 去撞同一堵墙。 */
        if (Environment.TickCount64 < Volatile.Read(ref _commitBlockedUntil)) return false;

        if (stale.Count > 0 && TantivyApi.DeletePaths(stale) < 0)
        {
            /* 删不掉旧条目最多是"同一路径两条"，写不进去才是"没有" —— 留痕后继续。 */
            CoreLog.Warn("删除旧索引条目失败：" + TantivyApi.LastError);
        }
        if (docs.Count > 0 && AddBatch(docs, jsonLegacy) < 0) return false;
        if (TantivyApi.Commit())
        {
            /* 只有"一次就成"才算健康。靠重开重放救回来的那批仍然说明索引目录有问题，
             * 不能把失败计数清零 —— 否则每批都"失败一次 + 重开一次 492MB 索引"，
             * 熔断永远不触发，CPU 就一直被这个重开循环吃掉。 */
            Interlocked.Exchange(ref _commitFailStreak, 0);
            Volatile.Write(ref _commitBlockedNoticed, 0);
            return true;
        }

        /* 走到这里：这一批已经被失败的 commit 取走，而且 writer 的内部状态也不再可信
         * （本机实测过：失败之后同一个 writer 再 commit 会"成功"却什么都没写进去）。
         * 所以不是原地重试，而是**关掉重开**，把 writer 换成一个干净的，再把这一批重放一次。 */
        string dir = TantivyApi.CurrentDir;
        /* 这条警告按 30 秒节流：索引目录出问题时它一秒能来一条，刷爆日志还占 CPU。 */
        long nowTick = Environment.TickCount64;
        if (nowTick - Interlocked.Read(ref _commitWarnAt) >= 30000)
        {
            Interlocked.Exchange(ref _commitWarnAt, nowTick);
            CoreLog.Warn("提交索引失败，重开索引后重放这一批：" + TantivyApi.LastError);
        }

        /* 熔断：连续失败太多次就别再一轮轮重试了。
         *
         * 真机现场（用户数据目录）：每批 commit 都 PermissionDenied（段文件被外部占用），
         * 于是"失败 → 重开 → 重放"每秒跑一轮，进程稳定烧掉 60% 一个核 —— 而钩子回调、
         * 手势的 8ms 采样、拖拽看门狗全在等 CPU，用户看到的就是"手势和拖拽都失效"。
         * 这里失败 3 次就闭嘴 5 分钟：这期间的批次直接丢（本来也写不进去），
         * 索引保持打开、查询照旧可用。 */
        if (Interlocked.Increment(ref _commitFailStreak) >= 3)
        {
            Volatile.Write(ref _commitBlockedUntil, Environment.TickCount64 + 5 * 60 * 1000);
            if (Interlocked.CompareExchange(ref _commitBlockedNoticed, 1, 0) == 0)
            {
                CoreLog.Warn("索引写入连续失败 3 次：暂停写入 5 分钟（查询不受影响），"
                             + "先排查索引目录是不是被杀软/同步盘占着。最后一条错误：" + TantivyApi.LastError);
            }
        }

        TantivyApi.Close();
        if (dir.Length == 0) return false;

        /* 重开 + 重放最多试三次，每次让一步。
         *
         * 失败最常见的原因是段文件被**外部短暂**占用（杀软实时扫描刚写出来的 .term/.idx、
         * 同步盘、另一个进程的读句柄）—— 在 %TEMP% 下实测就是这样：同一份代码
         * 一分钟前全绿、一分钟后这一组全红，错误都是 PermissionDenied。
         * 注意这里**不能**复用原来那个 writer 重试：它失败之后内部状态不可信
         * （本机实测过：同一个 writer 再 commit 会"成功"却什么都没写进去）。
         * 所以每次都是关掉、重开一个新的 writer，再把这一批重放一遍。 */
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt > 1) Thread.Sleep(150 * attempt);
            if (TantivyApi.OpenEx(dir) != 0) continue;
            if (stale.Count > 0) TantivyApi.DeletePaths(stale);
            if (docs.Count > 0 && AddBatch(docs, jsonLegacy) < 0) continue;
            if (TantivyApi.Commit()) return true;
            TantivyApi.Close();
        }
        /* 三次都没成：至少把索引重新打开 —— 一个坏批次不该让后面**所有**写入都变成
         * "索引未打开"（那才是真的整块功能停摆）。 */
        if (TantivyApi.OpenEx(dir) == 0)
            CoreLog.Warn("索引已重新打开，但这一批文档没写进去");
        return false;
    }

    /// <summary>一批文档写进去：默认二进制，夹具构建要 JSON 时传 true。</summary>
    private static int AddBatch(List<IndexDoc> docs, bool jsonLegacy)
        => jsonLegacy ? TantivyApi.AddDocs(docs) : TantivyApi.AddDocsBinary(docs);

    /// <summary>立即提交成功之后刷新统计与时间戳（不再走 <see cref="Flush"/>：它在锁里又提交一次）。</summary>
    private static void AfterImmediateCommit()
    {
        _problem = "";
        Interlocked.Exchange(ref _builtAtTicks, DateTime.Now.Ticks);
        SinceCommit.Restart();
        RefreshCounters(commit: true);
    }

    // ─────────────────────────── 查询 ───────────────────────────

    internal static IndexSearchResult Search(IndexQuery query, int limit)
        => TantivyApi.IsAvailable ? TantivyApi.Search(query, limit) : Unavailable();

    internal static IndexSearchResult PinyinSearch(IndexQuery query, int limit)
        => TantivyApi.IsAvailable ? TantivyApi.PinyinSearch(query, limit) : Unavailable();

    internal static IndexSearchResult FuzzySearch(IndexQuery query, int limit)
        => TantivyApi.IsAvailable ? TantivyApi.FuzzySearch(query, limit) : Unavailable();

    private static IndexSearchResult Unavailable()
        => new() { Ok = false, Error = TantivyApi.StatusText };

    // ─────────────────────────── 统计 / 状态 ───────────────────────────

    private static void LoadStats(string status)
    {
        RefreshCounters(commit: false);
        Volatile.Write(ref _starting, 0);
        Interlocked.Exchange(ref _builtAtTicks, DateTime.Now.Ticks);
        Volatile.Write(ref _ready, 1);
        SetStatusFromCounts(status);
    }

    private static void RefreshCounters(bool commit)
    {
        IndexStats stats = TantivyApi.Stats();
        if (stats.Ok)
        {
            Interlocked.Exchange(ref _docs, stats.Docs);
            Interlocked.Exchange(ref _files, stats.Files);
            Interlocked.Exchange(ref _dirs, stats.Dirs);
            Interlocked.Exchange(ref _indexBytes, stats.Bytes);
            if (!commit) Volatile.Write(ref _ready, stats.Files > 0 ? 1 : 0);
        }
    }

    private static void SetStatusFromCounts(string prefix)
    {
        long files = Interlocked.Read(ref _files);
        long dirs = Interlocked.Read(ref _dirs);
        _status = files > 0
            ? $"{prefix}：{files:N0} 个文件、{dirs:N0} 个目录"
            : prefix + "：还没有可搜的文件";
    }

    /// <summary>
    /// 一次性界面提示的出口（App 启动时挂到 <c>AppHost.Notify</c>）。
    /// null = 没有界面（命令行 --index-status / 自检进程），此时只留日志。
    /// </summary>
    internal static Action<string>? NotifySink { get; set; }

    private static int _noticeShown;

    /// <summary>同一个问题只提示一次：启动路径上 Start() 会被多个入口调到，重复弹是噪音。</summary>
    private static void NoticeOnce(string message)
    {
        if (Interlocked.Exchange(ref _noticeShown, 1) != 0) return;
        try { NotifySink?.Invoke(message); }
        catch (Exception ex) { CoreLog.Swallow(ex, "提示文件索引不可用"); }
    }

    /// <summary>查询诊断用的一行摘要。</summary>
    internal static string Describe()
    {
        if (!TantivyApi.IsAvailable) return "索引库不可用（" + TantivyApi.StatusText + "）";
        if (Building) return _status;
        if (!Ready) return _status + (Problem.Length == 0 ? "" : "（" + Problem + "）");
        return $"{FileCount:N0} 个文件 / {DirCount:N0} 个目录 · {IndexBytes / 1024.0 / 1024.0:0.0} MB · "
               + "建索引于 " + BuiltAt.ToString("yyyy-MM-dd HH:mm:ss");
    }

    // ─────────────────────────── 校准 / watcher ───────────────────────────

    private static void StartWatchers(string[] roots, CancellationToken token)
    {
        try
        {
            QbarIndexWatcher.Start(roots, token, OnWatcherBatch, MarkDirtyAndRescan);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "启动目录监视");
            _problem = "目录监视启动失败：" + ex.Message;
        }
    }

    private static void OnWatcherBatch(WatcherBatch batch)
    {
        try
        {
            /* 顺序是"先加后删"：两批里出现同一路径时，以删为准 ——
             * watcher 的 Flush 已经把这类冲突从 Upserted 里剔掉了，这里再保一道，
             * 免得别的调用方（diagnostics / 未来的重放工具）拿一份没洗过的批次进来。 */
            if (batch.Upserted.Count > 0) Upsert(batch.Upserted);
            if (batch.Deleted.Count > 0) Delete(batch.Deleted);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "处理目录变更"); }
    }

    /// <summary>watcher 溢出 / 句柄失效：把那个根整棵重扫一遍（不阻塞，串行）。</summary>
    private static int _rescanActive;

    /// <summary>自检专用：等后台整根重扫跑完（否则后续写入会和它抢同一个 writer）。</summary>
    internal static bool WaitRescansForTest(int timeoutMs)
    {
        var end = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < end)
        {
            if (Volatile.Read(ref _rescanActive) == 0) return true;
            Thread.Sleep(20);
        }
        return Volatile.Read(ref _rescanActive) == 0;
    }

    private static void MarkDirtyAndRescan(string root)
    {
        try
        {
            Interlocked.Increment(ref _rescanActive);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _status = "目录变更过多，正在重扫：" + root;
                    long files = 0, dirs = 0;
                    var token = Volatile.Read(ref _cts)?.Token ?? CancellationToken.None;
                    /* 索引里已经有这个根的内容了：必须走幂等路径（先删后加），
                     * 否则一次重扫就把整棵子树写成两份 —— 这正是"每个文件都被重复写一遍"的来源。 */
                    ScanRoot(root, ref files, ref dirs, token, Volatile.Read(ref _generation), idempotent: true);
                    Flush(force: true);
                    Volatile.Write(ref _ready, 1);
                    SetStatusFromCounts("索引已就绪");
                }
                catch (Exception ex) { CoreLog.Swallow(ex, "重扫目录"); }
                finally { Interlocked.Decrement(ref _rescanActive); }
            });
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "排重扫"); }
    }

    private static void CalibrateLoop(int generation, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && generation == Volatile.Read(ref _generation))
            {
                if (token.WaitHandle.WaitOne(CalibrateInterval)) return;
                if (token.IsCancellationRequested) return;
                Calibrate(token, generation);
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "索引校准"); }
    }

    /// <summary>
    /// 与磁盘对一遍：把已消失的条目删掉、把新出现的补进来。
    /// 不做全量重建 —— 用户开着程序用了一整天，重建会把 CPU 打满。
    ///
    /// **这条路上唯一的写入口是 <see cref="ApplyUpsert"/>**：它先按 `pathid` 删旧条目再 add。
    /// 早期版本在这里直接 `AddDocs`，于是每次校准（启动后 6 小时）都会把每个文件**再写一遍** ——
    /// tantivy 是 append-only，同一路径变成两条文档：结果里出现两条一模一样的、
    /// `stats().files` 虚高、索引体积一路涨。校准的第一要务是"幂等"，不是"快"。
    /// </summary>
    internal static int Calibrate(CancellationToken token, int generation)
    {
        int changed = 0;
        try
        {
            foreach (string root in _roots)
            {
                if (token.IsCancellationRequested || generation != Volatile.Read(ref _generation)) break;
                changed += ScanRootForCalibration(root, token);
            }
            if (changed > 0) RefreshCounters(commit: true);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "校准索引"); }
        return changed;
    }

    /// <summary>校准用的一轮扫描：枚举整棵树，每攒够一批就走 <see cref="ApplyUpsert"/>。</summary>
    private static int ScanRootForCalibration(string root, CancellationToken token)
    {
        int changed = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        var batch = new List<string>(512);
        while (stack.Count > 0)
        {
            if (token.IsCancellationRequested) break;
            string dir = stack.Pop();
            var children = EnumerateInfos(dir);
            if (children == null) continue;
            foreach (FileSystemInfo entry in children)
            {
                if (token.IsCancellationRequested) break;
                string path = entry.FullName;
                bool isDir;
                try { isDir = (entry.Attributes & FileAttributes.Directory) != 0; }
                catch { continue; }

                if (isDir)
                {
                    if (QbarRoots.Skip(entry.Name)) continue;
                    stack.Push(path);
                }
                batch.Add(path);
                if (batch.Count >= 512)
                {
                    changed += ApplyUpsert(batch);
                    batch.Clear();
                }
            }
        }
        if (batch.Count > 0) changed += ApplyUpsert(batch);
        return changed;
    }

    // ─────────────────────────── 目录快照（覆盖"程序没运行期间"的增删） ───────────────────────────

    /// <summary>
    /// 快照条数上限。超了按"层数浅优先 + 最近 mtime 优先"截断。
    ///
    /// 为什么要有上限：这是"每个目录一行"的字典，几百 GB 的盘上目录数量没有上界；
    /// 而它只是启动时的一次 diff，不是索引本体 —— 让它无界增长就是把内存交给用户的磁盘布局。
    /// 截断是**有损**的（被截掉的目录这次比不到），所以截断事实必须能被 --index-status 看到。
    /// </summary>
    private const int MaxDirStamps = 50000;

    /// <summary>一次快照 diff 最多展开多少个目录（新出现的目录会继续往下走一层）。</summary>
    private const int MaxStampDiffDirs = 20000;

    private static System.Collections.Concurrent.ConcurrentDictionary<string, DirStamp>? _stampSink;

    private static volatile string _stampStatus = "（本次进程还没有读到快照）";

    /// <summary>快照条数与截断情况的一句话（--index-status 用）。</summary>
    internal static string StampStatus => _stampStatus;

    /// <summary>快照文件路径：跟索引目录放一起（独立文件，避免打断 state.json 的半写）。</summary>
    private static string StampsPathFor(string dir) => Path.Combine(dir, "dirstamps.json");

    private static long DirMtimeTicks(string dir)
    {
        try { return Directory.GetLastWriteTimeUtc(dir).Ticks; }
        catch { return 0; }
    }

    /// <summary>一次系统调用同时拿到"在不在"与 mtime（启动 diff 的 5 万次探测走这条）。</summary>
    private static bool ProbeDir(string dir, out long mtime)
    {
        mtime = 0;
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists) return false;
            mtime = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch { return false; }
    }

    /// <summary>扫描时顺手记一条快照（只有 children &gt; 0 的目录才记）。</summary>
    private static void NoteStamp(string dir, long mtime, int children)
    {
        System.Collections.Concurrent.ConcurrentDictionary<string, DirStamp>? sink = _stampSink;
        if (sink == null || children <= 0) return;
        sink[dir] = new DirStamp { Mtime = mtime, Children = children };
    }

    private static void EnsureStampSink()
        => Interlocked.CompareExchange(ref _stampSink, new(StringComparer.OrdinalIgnoreCase), null);

    private static void DropStampSink() => Volatile.Write(ref _stampSink, null);

    /// <summary>把这一轮扫出来的快照（截断后）落到 dirstamps.json。</summary>
    private static void WriteDirStamps(string[] roots)
    {
        try
        {
            System.Collections.Concurrent.ConcurrentDictionary<string, DirStamp>? sink = _stampSink;
            if (sink == null) return;

            string dir = string.IsNullOrEmpty(TantivyApi.CurrentDir) ? IndexDir : TantivyApi.CurrentDir;
            Directory.CreateDirectory(dir);

            /* 截断规则：层数浅优先（浅层的目录更容易被用户直接看到），同层按 mtime 新到旧。 */
            var all = new List<KeyValuePair<string, DirStamp>>(sink);
            all.Sort((a, b) =>
            {
                int byDepth = QbarNameScore.DepthOf(a.Key).CompareTo(QbarNameScore.DepthOf(b.Key));
                if (byDepth != 0) return byDepth;
                return b.Value.Mtime.CompareTo(a.Value.Mtime);
            });
            int keep = Math.Min(MaxDirStamps, all.Count);
            int dropped = all.Count - keep;
            var file = new DirStampFile { Version = 1, Roots = roots, Truncated = dropped };
            for (int i = 0; i < keep; i++) file.Dirs[all[i].Key] = all[i].Value;

            string text = JsonSerializer.Serialize(file, StampJson);
            string temp = StampsPathFor(dir) + ".tmp";
            File.WriteAllText(temp, text);
            string target = StampsPathFor(dir);
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);

            _stampStatus = keep.ToString("N0") + " 个目录" + (dropped > 0 ? "（快照已截断 " + dropped.ToString("N0") + " 个目录）" : "");
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "写出目录快照");
            _stampStatus = "写出失败：" + ex.Message;
        }
    }

    private static DirStampFile? ReadDirStamps()
    {
        try
        {
            string dir = string.IsNullOrEmpty(TantivyApi.CurrentDir) ? IndexDir : TantivyApi.CurrentDir;
            string path = StampsPathFor(dir);
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize<DirStampFile>(File.ReadAllText(path), StampJson);
            if (file == null) return null;
            return file;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取目录快照");
            return null;
        }
    }

    /// <summary>
    /// 启动时与快照对一遍：**只补"程序没运行期间"发生的变化**。
    ///
    /// 做法是"目录 mtime + 条目数"的 diff，不做 USN Journal（那要卷句柄和提权，P2 明确不做）：
    ///   · mtime 或 children 变了 → 只重扫该目录**一层**，产出 upsert 批次走 <see cref="ApplyUpsert"/>；
    ///   · 快照里记着、磁盘上已经没有的目录 → **该根整体重扫**。按前缀删除需要的路径范围查询
    ///     在 `pathid`(raw) 上做不了，硬做等于引入第二套删除逻辑；
    ///   · 磁盘上新出现的目录 → 该目录一层 upsert + 记进快照（下次启动自然会比到更深一层）。
    ///
    /// 返回补进去的路径条数。
    /// </summary>
    internal static int CalibrateFromStamps(CancellationToken token)
    {
        int changed = 0;
        var watch = Stopwatch.StartNew();
        int scanned = 0;
        try
        {
            DirStampFile? file = ReadDirStamps();
            if (file == null || file.Dirs.Count == 0)
            {
                _stampStatus = "（没有快照，跳过启动 diff）";
                return 0;
            }

            string[] roots = file.Roots.Length > 0 ? file.Roots : _roots;
            _stampStatus = file.Dirs.Count.ToString("N0") + " 个目录"
                           + (file.Truncated > 0 ? "（快照已截断 " + file.Truncated.ToString("N0") + " 个目录）" : "");

            /* diff 过程中会补新条目，也可能要截断：一律先灌进同一个 sink，
             * 最后统一走 WriteDirStamps（截断规则与落盘只有一份实现）。 */
            DropStampSink();
            EnsureStampSink();
            System.Collections.Concurrent.ConcurrentDictionary<string, DirStamp> sink = _stampSink!;
            foreach (KeyValuePair<string, DirStamp> pair in file.Dirs) sink[pair.Key] = pair.Value;

            /* ① 一次遍历同时算出"哪些目录没了"与"哪些目录变了"。
             *
             * 为什么要合成一次：这是启动路径上唯一的 O(快照条数) 扫描，每条只做**一次**
             * 系统调用（`DirectoryInfo` 的 Exists 与 LastWriteTimeUtc 共用同一次 FindFirstFile），
             * 4 路并行。分开两次遍历 + 两条 syscall 的版本实测慢一倍以上。
             *
             * 判定只用 **mtime**：NTFS 上增删一个子项一定会更新父目录的 mtime，
             * 所以它是"这一层可能变了"的充分信号。快照里还存着 children 计数，
             * 但**不拿它做逐目录比对** —— 那意味着对 5 万个没变的目录各做一次
             * `GetFileSystemEntries`，实测每次启动多花 7 秒，只为了兜住一个
             * NTFS 上不会发生的场景（改内容不改父目录 mtime 是"文件内容"的问题，
             * 那种情况本来也不影响"能不能搜到"，见 TANTIVY-MIGRATION 的已知限制）。 */
            var missingDirs = new System.Collections.Concurrent.ConcurrentBag<string>();
            var changedDirs = new System.Collections.Concurrent.ConcurrentBag<string>();
            ParallelOptions opts = new()
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = token,
            };
            try
            {
                Parallel.ForEach(file.Dirs, opts, pair =>
                {
                    if (!ProbeDir(pair.Key, out long mtime)) { missingDirs.Add(pair.Key); return; }
                    if (mtime != pair.Value.Mtime) changedDirs.Add(pair.Key);
                });
            }
            catch (OperationCanceledException) { }

            /* ② 一个根整个没了，它下面的逐目录 diff 全都没有意义 → 该根整体重扫。 */
            var rescanRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string gone in missingDirs)
            {
                string root = RootOf(gone, roots);
                if (root.Length > 0) rescanRoots.Add(root);
            }

            /* ③ 变化目录 → 一层 upsert。新出现的子目录继续走一层（"递归点"）。 */
            var work = new Stack<string>();
            foreach (string dir in changedDirs)
            {
                if (rescanRoots.Count > 0 && rescanRoots.Contains(RootOf(dir, roots))) continue;
                work.Push(dir);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int expanded = 0;
            while (work.Count > 0 && expanded < MaxStampDiffDirs)
            {
                if (token.IsCancellationRequested) break;
                string dir = work.Pop();
                if (!seen.Add(dir)) continue;
                expanded++;
                scanned++;

                List<FileSystemInfo>? children = EnumerateInfos(dir);
                if (children == null) continue;
                NoteStamp(dir, DirMtimeTicks(dir), children.Count);

                var paths = new List<string>(children.Count);
                foreach (FileSystemInfo entry in children)
                {
                    bool isDir;
                    try { isDir = (entry.Attributes & FileAttributes.Directory) != 0; }
                    catch { continue; }
                    if (isDir && QbarRoots.Skip(entry.Name)) continue;
                    paths.Add(entry.FullName);
                    /* 盘上新增的目录：记下它的 mtime / 条数，并且**这一轮也往下走一层** ——
                     * 用户刚拷进来一个文件夹，里面的文件这一轮就该能搜到，而不是等下一次启动。 */
                    if (isDir && !file.Dirs.ContainsKey(entry.FullName))
                    {
                        NoteStamp(entry.FullName, DirMtimeTicks(entry.FullName), CountChildren(entry.FullName));
                        work.Push(entry.FullName);
                    }
                }
                if (paths.Count > 0) changed += ApplyUpsert(paths);
            }

            /* ③ 没了整根的：整棵重扫（不在这里同步扫，交给后台，别把启动卡住）。 */
            foreach (string root in rescanRoots)
            {
                CoreLog.Warn("快照里的目录已不存在，整根重扫：" + root);
                MarkDirtyAndRescan(root);
                changed++;
            }

            if (expanded > 0 || rescanRoots.Count > 0) WriteDirStamps(roots);
            DropStampSink();
            watch.Stop();
            _stampStatus = _stampStatus + " · 本次 diff " + watch.ElapsedMilliseconds + " ms / 重扫 "
                           + scanned + " 个目录 / 补入 " + changed + " 条";
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "启动快照 diff");
        }
        return changed;
    }

    /// <summary>path 属于哪个根（最长前缀匹配）；找不到返回空串。</summary>
    private static string RootOf(string path, string[] roots)
    {
        string best = "";
        foreach (string root in roots)
        {
            if (!QbarRoots.IsSameOrUnder(path, root)) continue;
            if (root.Length > best.Length) best = root;
        }
        return best;
    }

    private static int CountChildren(string dir)
    {
        try { return Directory.GetFileSystemEntries(dir).Length; }
        catch { return -1; }
    }

    private sealed class DirStampFile
    {
        public int Version { get; set; } = 1;
        public string[] Roots { get; set; } = Array.Empty<string>();
        public int Truncated { get; set; }
        public Dictionary<string, DirStamp> Dirs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DirStamp
    {
        public long Mtime { get; set; }
        public int Children { get; set; }
    }

    private static readonly JsonSerializerOptions StampJson = new() { WriteIndented = false };

    /// <summary>自检专用：往当前索引目录的快照里塞一条"磁盘上并不存在"的目录。</summary>
    internal static void InjectStampForTest(string dir)
    {
        DirStampFile file = ReadDirStamps() ?? new DirStampFile();
        if (file.Roots.Length == 0) file.Roots = _roots;
        file.Dirs[dir] = new DirStamp { Mtime = 1, Children = 1 };
        string baseDir = string.IsNullOrEmpty(TantivyApi.CurrentDir) ? IndexDir : TantivyApi.CurrentDir;
        Directory.CreateDirectory(baseDir);
        File.WriteAllText(StampsPathFor(baseDir), JsonSerializer.Serialize(file, StampJson));
    }

    // ─────────────────────────── 测试钩子 ───────────────────────────

    /// <summary>本进程是不是在跑 `--selftest`。</summary>
    internal static bool IsSelfTestProcess()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--selftest", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// 自检专用：把给定的一棵夹具树建成索引并**发布成当前索引**。
    ///
    /// 为什么需要这个入口：引擎、拼音检索、模糊兜底走的都是进程级的这一份索引，
    /// 而自检要的是"确定的那几棵树、确定的那些文件"。没有它，那几组用例只能去查
    /// 用户的真实磁盘，断言就得跟着机器内容漂。
    ///
    /// 返回写入的文件数；0 表示没建成（这时上层断言会立刻看出来）。
    /// </summary>
    internal static int PublishForTest(string root)
    {
        try
        {
            if (!TantivyApi.IsAvailable) return 0;

            string dir = Path.Combine(Path.GetTempPath(), "inputplus-qbar-idx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            lock (WriteGate)
            {
                TantivyApi.Close();
                if (!TantivyApi.Open(dir))
                {
                    _problem = "夹具索引打不开：" + TantivyApi.LastError;
                    return 0;
                }

                var docs = new List<IndexDoc>(256);
                var stack = new Stack<string>();
                stack.Push(root);
                int files = 0;
                DropStampSink();
                EnsureStampSink();
                while (stack.Count > 0)
                {
                    string current = stack.Pop();
                    string[] children;
                    try { children = Directory.GetFileSystemEntries(current); }
                    catch { continue; }
                    NoteStamp(current, DirMtimeTicks(current), children.Length);
                    foreach (string child in children)
                    {
                        bool isDir = Directory.Exists(child);
                        docs.Add(MakeDoc(child, isDir, isDir ? 0 : new FileInfo(child).Length,
                            isDir ? 0 : QbarSyntaxValues.ToDay(File.GetLastWriteTime(child)), 0));
                        if (isDir) stack.Push(child);
                        else files++;
                    }
                }

                /* 走**统一的写入口**（删旧 + 加新 + 提交，提交失败会重开索引重放这一批），
                 * 不在这里另写一份 add+commit：火绒会在刚建好的目录上瞬时捏住 .store，
                 * 裸 commit 失败一次就等于整套夹具索引没发布出去，后面几十条断言全废。 */
                ApplyUpsertDocs(docs, jsonLegacy: true);

                IndexStats fixtureStats = TantivyApi.Stats();
                if (!fixtureStats.Ok || fixtureStats.Docs <= 0)
                {
                    _problem = "夹具索引写入失败：" + (fixtureStats.Error.Length > 0
                        ? fixtureStats.Error
                        : TantivyApi.LastError);
                    return 0;
                }
                Interlocked.Exchange(ref _pendingSinceCommit, 0);
                Interlocked.Exchange(ref _ready, 1);
                Interlocked.Exchange(ref _builtAtTicks, DateTime.Now.Ticks);
                /* 根目录也要跟着换成这棵夹具树：校准（Calibrate / CalibrateFromStamps）
                 * 走的是 `_roots`，不换的话自检里调校准等于对着空列表空转。 */
                _roots = new[] { root };
                WriteDirStamps(_roots);
                DropStampSink();
                RefreshCounters(commit: false);
                SetStatusFromCounts("夹具索引已就绪");
                return files;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "建立夹具索引");
            _problem = ex.Message;
            return 0;
        }
    }

    /// <summary>自检专用：直接喂一个 watcher 批次给写入路径（不依赖真实文件系统事件）。</summary>
    internal static void OnWatcherBatchForTest(WatcherBatch batch) => OnWatcherBatch(batch);

    /// <summary>自检专用：走一次唯一写入口并返回真正写进去的条数。</summary>
    internal static int UpsertForTest(IReadOnlyList<string> paths) => ApplyUpsert(paths);

    /// <summary>自检专用：把当前索引从全局状态里摘掉（下一次再用要重新 Open）。</summary>
    internal static void ResetForTest()
    {
        lock (WriteGate)
        {
            TantivyApi.Close();
            Interlocked.Exchange(ref _ready, 0);
            Interlocked.Exchange(ref _docs, 0);
            Interlocked.Exchange(ref _files, 0);
            Interlocked.Exchange(ref _dirs, 0);
            Interlocked.Exchange(ref _started, 0);
            _status = "未建立";
            _roots = Array.Empty<string>();
        }
    }

    // ─────────────────────────── 落盘状态 ───────────────────────────

    private sealed class IndexState
    {
        public int Version { get; set; }
        public bool Done { get; set; }
        public string[] Roots { get; set; } = Array.Empty<string>();
        public List<string> Completed { get; set; } = new();
        public DateTime StartedAt { get; set; }
        public DateTime FinishedAt { get; set; }
        public string Generation { get; set; } = "";
    }

    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true };

    private static IndexState? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize<IndexState>(File.ReadAllText(StatePath), StateJson);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取索引状态");
            return null;
        }
    }

    private static void WriteState(IndexState state)
    {
        try
        {
            Directory.CreateDirectory(IndexDir);
            string temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, StateJson));
            if (File.Exists(StatePath)) File.Delete(StatePath);
            File.Move(temp, StatePath);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "写出索引状态"); }
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

    private static void TryClearIndex() => TryClearIndex(IndexDir);

    private static void TryClearIndex(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); return; }
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                try { File.Delete(file); } catch { }
            }
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                try { Directory.Delete(sub, true); } catch { }
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "清空索引目录"); }
    }

    /// <summary>老的 names.bin / dirs.bin 快照挪走：迁移完成后它没有任何用途，留着只会误导。</summary>
    private static void DeleteLegacySnapshot()
    {
        try
        {
            if (!Directory.Exists(LegacyDir)) return;
            Directory.Delete(LegacyDir, true);
            CoreLog.Info("已删除旧的内存快照目录：" + LegacyDir);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "删除旧索引快照"); }
    }
}

/// <summary>一次目录变更批次。</summary>
internal sealed class WatcherBatch
{
    public List<string> Deleted { get; } = new();
    public List<string> Upserted { get; } = new();
}

internal static class FileInfoExtensions
{
    /// <summary>取最后修改时间；取不到就返回 Unix epoch（ToDay 会把它夹到合法范围）。</summary>
    public static DateTime WriteTime(this FileInfo info)
    {
        try { return info.LastWriteTime; }
        catch { return DateTime.UnixEpoch; }
    }
}
