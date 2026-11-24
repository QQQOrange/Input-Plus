using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 实时增量：每个根目录一个 <c>ReadDirectoryChangesW</c> 监视线程。
///
/// 为什么用这个 API 而不是轮询：轮询要么慢（间隔大）要么贵（整棵树的 mtime 对比），
/// 而"新建一个文件 2 秒内能搜到"是硬要求。ReadDirectoryChangesW 是内核在变更发生时
/// 往缓冲区里写记录，平时一个字节的代价都没有。
///
/// 三种失败必须处理，否则增量会静默失效：
///   · 缓冲区溢出（一次变更太多）→ 内核返回 ERROR_NOTIFY_ENUM_DIR，这一批记录不完整，
///     只能把这个根整棵重扫（交给 <c>QbarIndex</c>）；
///   · 句柄失效（盘被拔 / 权限变了）→ 同上，并且记状态；
///   · 根目录打不开句柄（网络盘 / 权限）→ 该根退化成 30 秒 mtime 轮询。
/// </summary>
internal static class QbarIndexWatcher
{
    private const int BufferBytes = 64 * 1024;
    /* 250ms → 1500ms：这个间隔直接决定"每秒提交几次索引"。
     * 250ms 时，任何持续写入的目录都会让索引每秒提交数次，而每次提交都要合并段；
     * 合并占满一个核之后，手势采样、钩子回调、滚动帧就都跟着卡。 */
    private const int DebounceMs = 1500;
    private const int MaxBatch = 4096;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private static readonly List<Watcher> Watchers = new();
    private static readonly object Gate = new();
    private static volatile string _status = "未启动";

    internal static string StatusText => _status;

    internal static void Start(string[] roots, CancellationToken token,
        Action<WatcherBatch> onBatch, Action<string> onDirty)
    {
        lock (Gate)
        {
            StopAllLocked();
            foreach (string root in roots)
            {
                var watcher = new Watcher(root, token, onBatch, onDirty);
                Watchers.Add(watcher);
                watcher.Start();
                /* 等它把句柄打开再返回：这样"StartWatchers 之后"再发生的变化一条都不会丢。
                 * 超时（网络盘/慢盘）也放行 —— 那种情况本来就走轮询兜底。 */
                watcher.WaitArmed(2000);
            }
            _status = $"{Watchers.Count} 个根目录正在监视（{DescribeFallbacks()}）";
        }
    }

    internal static void StopAll()
    {
        lock (Gate) StopAllLocked();
    }

    private static void StopAllLocked()
    {
        foreach (Watcher w in Watchers) w.Stop();
        Watchers.Clear();
        _status = "已停止";
    }

    private static string DescribeFallbacks()
    {
        int polling = 0;
        lock (Gate)
        {
            foreach (Watcher w in Watchers) if (w.IsPolling) polling++;
        }
        return polling == 0 ? "全部实时" : polling + " 个退化为 30 秒轮询";
    }

    // ─────────────────────────── 单个根 ───────────────────────────

    private sealed class Watcher
    {
        private readonly string _root;
        private readonly CancellationToken _token;
        private readonly Action<WatcherBatch> _onBatch;
        private readonly Action<string> _onDirty;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly List<string> _upsert = new();
        private readonly List<string> _deleted = new();
        private DateTime _lastFlush = DateTime.UtcNow;
        private volatile bool _polling;

        internal Watcher(string root, CancellationToken token, Action<WatcherBatch> onBatch, Action<string> onDirty)
        {
            _root = root;
            _token = token;
            _onBatch = onBatch;
            _onDirty = onDirty;
            _thread = new Thread(Run) { IsBackground = true, Name = "qbar-watch:" + root };
        }

        internal bool IsPolling => _polling;

        /// <summary>
        /// 监视线程**已经把句柄打开**（或已经退化到轮询）。
        ///
        /// 为什么要等这一下：Start() 只是把线程拉起来，CreateFileW 还没跑完之前
        /// 发生的变更内核不会补发 —— 启动那一两百毫秒里新建的文件于是要等 6 小时校准
        /// 才能搜到。--index-status --increment-check 实测过这个窗口（新建后可见=否）。
        /// </summary>
        private readonly ManualResetEventSlim _arming = new(false);

        internal bool WaitArmed(int timeoutMs) => _arming.Wait(timeoutMs);

        internal void Start() => _thread.Start();

        internal void Stop()
        {
            _stop.Set();
            try { _thread.Join(500); } catch { }
        }

        private void Run()
        {
            try { RunCore(); }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "目录监视");
                _onDirty(_root);
            }
        }

        private void RunCore()
        {
            using SafeFileHandle handle = Native.CreateFileW(
                _root, FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                _polling = true;
                _arming.Set();
                PollLoop();
                return;
            }

            _arming.Set();
            var buffer = new byte[BufferBytes];
            while (!_token.IsCancellationRequested && !_stop.IsSet)
            {
                int bytes;
                bool ok = Native.ReadDirectoryChangesW(
                    handle, buffer, BufferBytes, watchSubtree: true,
                    notify: FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME
                            | FILE_NOTIFY_CHANGE_SIZE | FILE_NOTIFY_CHANGE_LAST_WRITE,
                    out bytes, IntPtr.Zero, IntPtr.Zero);

                if (!ok)
                {
                    // 溢出 / 句柄失效：这一批不可信，整根重扫。
                    _onDirty(_root);
                    return;
                }
                if (bytes == 0) continue;

                Parse(buffer, bytes);
                if (DateTime.UtcNow - _lastFlush >= TimeSpan.FromMilliseconds(DebounceMs)
                    || _upsert.Count + _deleted.Count >= MaxBatch)
                    Flush();
            }
            Flush();
        }

        private void Parse(byte[] buffer, int bytes)
        {
            int offset = 0;
            while (offset >= 0 && offset + 12 <= bytes)
            {
                int next = BitConverter.ToInt32(buffer, offset);
                int action = BitConverter.ToInt32(buffer, offset + 4);
                int nameLength = BitConverter.ToInt32(buffer, offset + 8);
                if (nameLength <= 0 || offset + 12 + nameLength > bytes) break;

                string relative = Encoding.Unicode.GetString(buffer, offset + 12, nameLength);
                string full = Path.Combine(_root, relative);
                /* 自己的数据目录产生的变更**一律忽略**。
                 *
                 * 这是自激回路的那一刀：索引段文件、app.log、settings.json 都在
                 * 盘根之下，watcher 又监听着 SIZE/LAST_WRITE —— 不忽略的话，
                 * "写索引"本身会立刻变成"要写索引"，每 2 秒一轮，CPU 永久打满
                 * （实测主进程常驻 ~1.8 个核）。 */
                if (!QbarRoots.IsOwnData(full))
                switch (action)
                {
                    case 2: // FILE_ACTION_REMOVED
                        _deleted.Add(full);
                        break;
                    case 4: // FILE_ACTION_RENAMED_OLD_NAME
                        _deleted.Add(full);
                        _deleted.AddRange(ExpandChildren(full));
                        break;
                    case 1: // ADDED
                    case 3: // MODIFIED
                    case 5: // RENAMED_NEW_NAME
                        _upsert.Add(full);
                        _upsert.AddRange(ExpandChildren(full));
                        break;
                }
                if (next == 0) break;
                offset += next;
            }
        }

        /// <summary>
        /// 目录被整棵搬进来/改名时，只有一条目录记录，里面的文件一条都不会报；
        /// 所以见到目录变更就把它的子树补齐（有上限，超了交给整根重扫）。
        /// </summary>
        private List<string> ExpandChildren(string path)
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(path)) return list;
                var stack = new Stack<string>();
                stack.Push(path);
                int guard = 0;
                while (stack.Count > 0)
                {
                    if (++guard > 5000) { _onDirty(_root); break; }
                    string dir = stack.Pop();
                    foreach (string child in Directory.EnumerateFileSystemEntries(dir))
                    {
                        list.Add(child);
                        if (Directory.Exists(child)) stack.Push(child);
                    }
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "补全目录子树"); }
            return list;
        }

        private void Flush()
        {
            if (_upsert.Count == 0 && _deleted.Count == 0)
            {
                _lastFlush = DateTime.UtcNow;
                return;
            }

            /* 交给回调之前先把两边的重复项压掉，并解决"同一路径既增又删"。
             *
             * 为什么必须在这里做而不是交给下游：一次写操作会同时报 ADDED 与 MODIFIED
             * （同一条路径进 `_upsert` 两次）；目录被整棵搬入时 `ExpandChildren` 补出来的
             * 子树会和批次里已有的条目重叠；改名的旧名与新名分别落进两条列表是正常现象，
             * 但**同一个路径**同时出现在两边就一定是一次交错 —— 那时要以删为准：
             * 文件已经不在了，把它加进索引是错的（下一次查询会给出一条不存在的路径）。 */
            var upsertSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deletedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deleted = new List<string>(deletedSeen.Count);
            foreach (string path in _deleted)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (deletedSeen.Add(path)) deleted.Add(path);
            }

            var upserted = new List<string>(_upsert.Count);
            foreach (string path in _upsert)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (deletedSeen.Contains(path)) continue;   // 同一批里既增又删 → 删优先
                if (upsertSeen.Add(path)) upserted.Add(path);
            }

            _upsert.Clear();
            _deleted.Clear();
            _lastFlush = DateTime.UtcNow;

            if (upserted.Count == 0 && deleted.Count == 0) return;

            var batch = new WatcherBatch();
            batch.Upserted.AddRange(upserted);
            batch.Deleted.AddRange(deleted);
            try { _onBatch(batch); }
            catch (Exception ex) { CoreLog.Swallow(ex, "处理目录变更批次"); }
        }

        // ── 退化路径：30 秒 mtime / 条数对比 ──

        private void PollLoop()
        {
            var snapshot = new Dictionary<string, (DateTime Mtime, int Count)>(StringComparer.OrdinalIgnoreCase);
            while (!_token.IsCancellationRequested && !_stop.IsSet)
            {
                if (_stop.Wait(PollInterval)) return;
                try
                {
                    bool changed = false;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var stack = new Stack<string>();
                    stack.Push(_root);
                    int guard = 0;
                    while (stack.Count > 0 && guard++ < 20000)
                    {
                        string dir = stack.Pop();
                        seen.Add(dir);
                        DateTime mtime;
                        int count;
                        try
                        {
                            mtime = Directory.GetLastWriteTimeUtc(dir);
                            count = Directory.GetFileSystemEntries(dir).Length;
                        }
                        catch { continue; }

                        if (!snapshot.TryGetValue(dir, out var old) || old.Mtime != mtime || old.Count != count)
                            changed = true;
                        snapshot[dir] = (mtime, count);

                        try
                        {
                            foreach (string child in Directory.EnumerateDirectories(dir)) stack.Push(child);
                        }
                        catch { }
                    }
                    if (snapshot.Count != seen.Count) changed = true;
                    if (changed) _onDirty(_root);
                }
                catch (Exception ex) { CoreLog.Swallow(ex, "目录轮询"); }
            }
        }
    }

    // ─────────────────────────── P/Invoke ───────────────────────────

    private const int FILE_LIST_DIRECTORY = 0x0001;
    private const int FILE_SHARE_READ = 0x00000001;
    private const int FILE_SHARE_WRITE = 0x00000002;
    private const int FILE_SHARE_DELETE = 0x00000004;
    private const int OPEN_EXISTING = 3;
    private const int FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private const int FILE_NOTIFY_CHANGE_FILE_NAME = 0x00000001;
    private const int FILE_NOTIFY_CHANGE_DIR_NAME = 0x00000002;
    private const int FILE_NOTIFY_CHANGE_SIZE = 0x00000008;
    private const int FILE_NOTIFY_CHANGE_LAST_WRITE = 0x00000010;

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName, int desiredAccess, int shareMode, IntPtr security,
            int creation, int flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadDirectoryChangesW(
            SafeFileHandle handle, byte[] buffer, int bufferLength,
            [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, int notify,
            out int bytesReturned, IntPtr overlapped, IntPtr completionRoutine);
    }
}
