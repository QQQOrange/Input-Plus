using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using InputPlus.Core.Settings;
using InputPlus.Core.Suggestions.Sources;

namespace InputPlus.Core.Suggestions;

/// <summary>
/// Suggestions 层的总装：注册来源 → 后台采集 → 合并 → 缓存 → 供 Presenter 取。
///
/// 线程模型（红线 3、4）：
///   · 采集只在一条专用后台线程（STA，因为 IShellLink 要 STA）上跑，UI 线程不碰目录；
///   · 唤出时不等待采集，直接读 <see cref="Snapshot"/>（上一轮结果 + 磁盘缓存首屏）；
///   · 采集完成只更新时间戳与字段，通过 <see cref="Changed"/> 通知（回调在后台线程，
///     订阅方自己切 Dispatcher）；
///   · 不使用 FileSystemWatcher 做全盘递归监听（只对"目录结构变化"按需重采）。
///
/// 降级（红线 2）：单个来源抛异常只记该来源失败、条数计 0，其它来源照常合并。
/// </summary>
public sealed class SuggestionsService : IDisposable
{
    /// <summary>全局单例。QbarService.DefaultResults 是静态方法，靠它拿到首屏。</summary>
    public static SuggestionsService? Current { get; private set; }

    private readonly Func<AppSettings> _settings;
    private readonly SuggestionConfigStore _store;
    private readonly object _gate = new();

    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEvent _quit = new(false);
    private Thread? _worker;

    private RecSnapshot _snapshot = RecSnapshot.Empty;
    private List<IRecommendationSource> _sources = new();
    private RecRules _rules = new();
    private bool _disposed;
    private int _refreshQueued;
    private long _lastPublishTicks;

    // 隐藏热键：一个零尺寸的 HwndSource 承载 WM_HOTKEY（和文件对话框浮层同一套做法）
    private const int HideHotkeyId = 0x4748;
    private const int WmHotkey = 0x0312;
    private System.Windows.Interop.HwndSource? _hotkeyHost;

    public SuggestionsService(Func<AppSettings> settings, SuggestionConfigStore? store = null)
    {
        _settings = settings ?? (() => new AppSettings());
        _store = store ?? new SuggestionConfigStore();
        Current ??= this;
    }

    /// <summary>采集完成（在后台线程触发）。</summary>
    public event Action<RecSnapshot>? Changed;

    /// <summary>需要告诉用户的一句话（隐藏成功/失败）。Core 不碰 UI，由 AppHost 接走。</summary>
    public event Action<string, string>? Notified;

    public RecSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public bool IsWarm => Snapshot.CollectedAt != DateTime.MinValue;

    public long LastCollectMs => Snapshot.CollectMs;

    public string StorePath => _store.StorePath;

    /// <summary>八个来源固定注册，顺序即展示权重顺序。</summary>
    public static List<IRecommendationSource> CreateSources() => new()
    {
        new DownloadsSource(),
        new DesktopSource(),
        new WeChatSource(),
        new WeComSource(),
        new FeishuSource(),
        new WindowsRecentSource(),
        new DialogHistorySource(),
        new AppHistorySource(),
    };

    public void Start()
    {
        if (_disposed || _worker != null) return;
        ApplyHotkey();

        var options = _settings().Suggestions ?? new SuggestionsOptions();
        try { _store.Replay(options); }
        catch (Exception ex) { CoreLog.Swallow(ex, "重放建议配置"); }

        LoadCache();           // 首屏缓存：毫秒级，唤出时就能看到东西
        Rebuild();

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "InputPlus.Suggestions",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
        RequestRefresh(0);
    }

    /// <summary>设置变化后调用：重建规则/来源并请求一次重采。</summary>
    public void Apply()
    {
        if (_disposed) return;
        Rebuild();
        ApplyHotkey();
        RequestRefresh(120);
    }

    /// <summary>
    /// 注册/重注册"不建议此文件"全局热键。
    /// 必须在有消息循环的 UI 线程上调（Start/Apply 都是）；headless 模式下没人调，
    /// 也就不会占热键。
    /// </summary>
    public void ApplyHotkey()
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ApplyHotkey));
                return;
            }

            _hotkeyHost ??= CreateHotkeyHost();
            if (_hotkeyHost == null) return;

            NativeHotkey.UnregisterHotKey(_hotkeyHost.Handle, HideHotkeyId);

            string combo = _settings().Suggestions?.HideFileHotkey ?? "";
            if (string.IsNullOrWhiteSpace(combo)) return;

            var parsed = HotkeyCombo.Parse(combo);
            if (parsed == null)
            {
                CoreLog.Warn("建议隐藏热键写法无效：" + combo);
                return;
            }

            // MOD_NOREPEAT：按住不放不该反复隐藏（一秒钟能把几十条塞进黑名单）
            if (!NativeHotkey.RegisterHotKey(_hotkeyHost.Handle, HideHotkeyId,
                    parsed.Value.Mods | NativeHotkey.ModNoRepeat, (uint)parsed.Value.Vk))
                CoreLog.Warn("注册建议隐藏热键失败：" + combo);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "注册建议隐藏热键"); }
    }

    /// <summary>隐藏当前在 Qbar 里选中的那条建议（热键入口）。</summary>
    public bool HideSelected(out string error)
    {
        string path = SuggestionsSelection.Path;
        if (path.Length == 0)
        {
            error = "当前没有选中建议项";
            return false;
        }
        return Hide(path, SuggestionsSelection.IsFolder, out error);
    }

    private System.Windows.Interop.HwndSource? CreateHotkeyHost()
    {
        try
        {
            var parameters = new System.Windows.Interop.HwndSourceParameters("InputPlusSuggestionsHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            var source = new System.Windows.Interop.HwndSource(parameters);
            source.AddHook(HotkeyWndProc);
            return source;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "创建建议热键窗口");
            return null;
        }
    }

    private nint HotkeyWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt32() != HideHotkeyId) return 0;

        handled = true;
        try
        {
            if (HideSelected(out string error))
                Notified?.Invoke("已隐藏，可在设置里还原", "info");
            else
                Notified?.Invoke("隐藏失败：" + error, "error");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "隐藏建议项"); }
        return 0;
    }

    private static class NativeHotkey
    {
        public const uint ModNoRepeat = 0x4000;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(nint hwnd, int id);
    }

    /// <summary>请求一次采集。<paramref name="debounceMs"/> 用来把连续变化合成一次。</summary>
    public void RequestRefresh(int debounceMs = 250)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        if (debounceMs > 0)
        {
            // 不额外开线程：用线程池延迟后唤醒采集线程，避免高频调用把采集线程堵住
            _ = System.Threading.Tasks.Task.Delay(debounceMs).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                _signal.Set();
            }, System.Threading.Tasks.TaskScheduler.Default);
            return;
        }
        Interlocked.Exchange(ref _refreshQueued, 0);
        _signal.Set();
    }

    /// <summary>
    /// 首屏：只读快照，不触发采集。
    ///
    /// 但会补一层"采集之后才打开的文件"：后台采集几十秒一轮，用户刚打开一张图就来看
    /// 历史记录时，快照里还没有它。只解析 Recent 里比快照新的那几条 .lnk（正常 0～3 条），
    /// 直接插到最前面 —— 这是"刚打开的文件应该排第一"的唯一代价可以接受的做法。
    /// </summary>
    public IReadOnlyList<RecItem> FirstScreen(int count)
    {
        count = Math.Clamp(count, 0, 200);
        if (count == 0) return Array.Empty<RecItem>();

        IReadOnlyList<RecItem> items = Snapshot.Items;
        try
        {
            /* 只在"已经采过一轮"之后补头：冷启动（没有快照）时首屏必须为空，
             * 那是"还没准备好"的明确信号，不能拿几条新文件冒充首屏。 */
            var fresh = Snapshot.CollectedAt == DateTime.MinValue
                ? Array.Empty<RecItem>()
                : Sources.WindowsRecentSource.NewSince(Snapshot.CollectedAt, 5);
            if (fresh.Count > 0)
            {
                var merged = new List<RecItem>(fresh);
                var seen = new HashSet<string>(fresh.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
                foreach (RecItem item in items)
                    if (seen.Add(item.Key)) merged.Add(item);
                items = merged;
            }
        }
        catch { /* 补头失败就当没有：首屏绝不能因为它抛异常 */ }

        return items.Count <= count ? items : items.Take(count).ToList();
    }

    /// <summary>
    /// 把某个路径加入黑名单（右键"不建议此文件/文件夹"）。
    ///
    /// 顺序刻意如此：先写盘（delta），成功后再改内存 —— 写盘失败时列表里那条
    /// 必须还在（红线 5：隐藏动作不能"看起来成功"）。
    /// </summary>
    public bool Hide(string path, bool isFolder, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path)) { error = "路径为空"; return false; }

        string key = isFolder ? SuggestionKeys.HiddenFolder(path) : SuggestionKeys.HiddenFile(path);
        if (!_store.Set(key, path, out error)) return false;

        var options = _settings().Suggestions ?? new SuggestionsOptions();
        List<string> list = isFolder ? options.Exclusions.Folders : options.Exclusions.Files;
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
        PersistSettings();
        Rebuild();
        RemoveFromSnapshot(path);
        RequestRefresh(80);
        CoreLog.Info("[建议] 已隐藏：" + path);
        return true;
    }

    /// <summary>还原某条被隐藏的建议（设置页"还原已隐藏的建议"）。</summary>
    public bool Unhide(string path, bool isFolder, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path)) { error = "路径为空"; return false; }

        string key = isFolder ? SuggestionKeys.HiddenFolder(path) : SuggestionKeys.HiddenFile(path);
        if (!_store.Remove(key, out error)) return false;

        var options = _settings().Suggestions ?? new SuggestionsOptions();
        List<string> list = isFolder ? options.Exclusions.Folders : options.Exclusions.Files;
        list.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        PersistSettings();
        Rebuild();
        RequestRefresh(80);
        return true;
    }

    public IReadOnlyList<string> HiddenFiles() => (_settings().Suggestions?.Exclusions.Files ?? new List<string>()).ToList();

    public IReadOnlyList<string> HiddenFolders() => (_settings().Suggestions?.Exclusions.Folders ?? new List<string>()).ToList();

    /// <summary>每个来源的原始条数（上次采集）。</summary>
    public IReadOnlyDictionary<string, int> SourceCounts => Snapshot.SourceCounts;

    public IReadOnlyDictionary<string, string> Failures => Snapshot.Failures;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _quit.Set(); _signal.Set(); } catch { }
        try { _worker?.Join(500); } catch { }
        _worker = null;
        try
        {
            if (_hotkeyHost != null)
            {
                NativeHotkey.UnregisterHotKey(_hotkeyHost.Handle, HideHotkeyId);
                _hotkeyHost.Dispose();
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "释放建议热键"); }
        _hotkeyHost = null;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    // ── 内部 ──

    private void Rebuild()
    {
        var options = _settings().Suggestions ?? new SuggestionsOptions();
        _rules = new RecRules(
            options.Exclusions?.FileExtensions,
            folders: null,           // null = 用硬编码的系统目录
            hidden: (options.Exclusions?.Files ?? new List<string>())
                .Concat(options.Exclusions?.Folders ?? new List<string>()),
            allowedExtensions: options.FileTypeFilter);

        _sources = CreateSources()
            .Where(s => options.IsSourceEnabled(s.Id))
            .ToList();
    }

    private void WorkerLoop()
    {
        var waitHandles = new WaitHandle[] { _signal, _quit };
        while (true)
        {
            int index = WaitHandle.WaitAny(waitHandles);
            if (index == 1 || _disposed) return;

            try
            {
                RecSnapshot snapshot = CollectOnce();
                bool anyChanged;
                lock (_gate)
                {
                    anyChanged = snapshot.RawCount != _snapshot.RawCount
                                 || snapshot.Items.Count != _snapshot.Items.Count
                                 || snapshot.CollectedAt - _snapshot.CollectedAt > TimeSpan.FromSeconds(30);
                    _snapshot = snapshot;
                }
                SaveCache(snapshot);
                CoreLog.Info($"[建议] 采集完成：{snapshot.Items.Count} 条（原始 {snapshot.RawCount}）用时 {snapshot.CollectMs}ms"
                             + (snapshot.Failures.Count > 0 ? "，失败来源 " + string.Join(",", snapshot.Failures.Keys) : ""));
                if (anyChanged) SafeRaise(snapshot);
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "采集搜索前建议");
            }
        }
    }

    private RecSnapshot CollectOnce()
    {
        var options = _settings().Suggestions ?? new SuggestionsOptions();
        var budget = new SuggestionBudget(options.MaxCandidates, options.CollectBudgetMs);
        var items = new List<RecItem>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (IRecommendationSource source in _sources)
        {
            if (_disposed) break;
            try
            {
                // 登记阶段只探活（毫秒级），真正的枚举在下面
                if (!source.Probe(new SuggestionContext(_settings(), _rules, options.MaxCandidates)))
                {
                    counts[source.Id] = 0;
                    continue;
                }

                IReadOnlyList<RecItem> got = source.Collect(
                    new SuggestionContext(_settings(), _rules, options.MaxCandidates), budget);
                int before = items.Count;
                items.AddRange(got);
                counts[source.Id] = items.Count - before;
            }
            catch (Exception ex)
            {
                counts[source.Id] = 0;
                failures[source.Id] = ex.Message;
                CoreLog.Warn($"[建议] 来源 {source.Id} 采集失败（按空处理）：{ex.Message}");
            }
        }

        long ms = budget.ElapsedMs;
        /* SourceOrder 为空 = 保持 1.3.5 的"全局按修改时间倒序"；
         * 用户在设置里排过顺序才切换成"按来源分组"。 */
        var order = options.SourceOrder is { Count: > 0 } ? options.ResolveSourceOrder() : null;
        var snapshot = RecMerger.Merge(items, _rules, options.MaxCandidates, counts, ms, failures, order);
        return new RecSnapshot
        {
            Items = snapshot.Items,
            RawCount = snapshot.RawCount,
            CollectMs = snapshot.CollectMs,
            SourceCounts = snapshot.SourceCounts,
            Failures = snapshot.Failures,
            CollectedAt = DateTime.Now,
        };
    }

    private void RemoveFromSnapshot(string path)
    {
        string key = RecItem.NormalizeKey(path);
        lock (_gate)
        {
            var kept = _snapshot.Items.Where(i => !string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
            _snapshot = new RecSnapshot
            {
                Items = kept,
                RawCount = _snapshot.RawCount,
                CollectMs = _snapshot.CollectMs,
                SourceCounts = _snapshot.SourceCounts,
                Failures = _snapshot.Failures,
                CollectedAt = _snapshot.CollectedAt,
            };
        }
        SafeRaise(Snapshot);
    }

    private void SafeRaise(RecSnapshot snapshot)
    {
        _lastPublishTicks = Environment.TickCount64;
        try { Changed?.Invoke(snapshot); }
        catch (Exception ex) { CoreLog.Swallow(ex, "通知建议更新"); }
    }

    private void PersistSettings()
    {
        try { SettingsPersist?.Invoke(); }
        catch (Exception ex) { CoreLog.Swallow(ex, "保存建议设置"); }
    }

    /// <summary>由 AppHost 注入：把当前 <see cref="AppSettings"/> 交给 SettingsStore 落盘。</summary>
    public Action? SettingsPersist { get; set; }

    // ── 首屏缓存 ──

    private sealed class CacheDoc
    {
        public DateTime SavedAt { get; set; }
        public long CollectMs { get; set; }
        public Dictionary<string, int> SourceCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Failures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<CacheItem> Items { get; set; } = new();
    }

    private sealed class CacheItem
    {
        public string ItemType { get; set; } = "File";
        public string SourceId { get; set; } = "";
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime Modified { get; set; }
        public List<string> Badges { get; set; } = new();
        public List<string> MergedSources { get; set; } = new();
    }

    /// <summary>首屏缓存文件（采集完成后落盘，下次唤出秒开）。探针也要读它，故公开。</summary>
    public static string CachePath => Path.Combine(AppPaths.DataDir, "suggestions-cache.json");

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var doc = JsonSerializer.Deserialize<CacheDoc>(File.ReadAllText(CachePath));
            if (doc?.Items == null || doc.Items.Count == 0) return;

            var items = doc.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Path))
                .Select(i => new RecItem
                {
                    ItemType = Enum.TryParse(i.ItemType, out RecItemType t) ? t : RecItemType.File,
                    SourceId = i.SourceId,
                    SourceKind = SourceKind.Recent,
                    SourceWeight = 0,
                    Path = i.Path,
                    Name = i.Name,
                    Modified = i.Modified,
                    Badges = i.Badges,
                    MergedSources = i.MergedSources.Count > 0 ? i.MergedSources : new[] { i.SourceId },
                })
                .Where(i => !_rules.IsExcluded(i.Path, i.IsFolder, out _))
                .ToList();

            lock (_gate)
            {
                _snapshot = new RecSnapshot
                {
                    Items = items,
                    RawCount = items.Count,
                    CollectMs = doc.CollectMs,
                    // 各来源计数与失败原因一起缓存：否则重启后设置页的统计行
                    // 要等下一轮采集才有内容，看起来像"来源全挂了"。
                    SourceCounts = doc.SourceCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    Failures = doc.Failures ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    CollectedAt = doc.SavedAt,
                };
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取建议首屏缓存");
        }
    }

    private void SaveCache(RecSnapshot snapshot)
    {
        try
        {
            var doc = new CacheDoc
            {
                SavedAt = snapshot.CollectedAt,
                CollectMs = snapshot.CollectMs,
                SourceCounts = new Dictionary<string, int>(snapshot.SourceCounts, StringComparer.OrdinalIgnoreCase),
                Failures = new Dictionary<string, string>(snapshot.Failures, StringComparer.OrdinalIgnoreCase),
                Items = snapshot.Items.Take(40).Select(i => new CacheItem
                {
                    ItemType = i.ItemType.ToString(),
                    SourceId = i.SourceId,
                    Path = i.Path,
                    Name = i.DisplayName,
                    Modified = i.Modified,
                    Badges = i.Badges.ToList(),
                    MergedSources = i.MergedSources.ToList(),
                }).ToList(),
            };
            string dir = Path.GetDirectoryName(CachePath) ?? "";
            if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(doc,
                new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "写建议首屏缓存");
        }
    }
}
