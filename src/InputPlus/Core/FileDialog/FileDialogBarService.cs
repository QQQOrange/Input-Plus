using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using InputPlus.Core.Input;
using InputPlus.Core.Settings;
using InputPlus.Core.Suggestions;
using InputPlus.UI.Windows;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// 服务层：把五层串起来 —— Watcher → Classifier → Bar → Backfill → Recorder。
///
/// 只做"编排"：不直接碰 Win32 细节（找窗口/取边框在 Watcher 与 DialogNative），
/// 不直接碰 UI 细节（浮层自己的键盘、定位在 FileDialogBarWindow），
/// 不直接碰回填细节（在 DialogBackfill）。这样每一层都能单独测。
///
/// 生命周期：AppHost 启动时 <see cref="Start"/>，设置变化时 <see cref="Apply"/>，
/// 退出时 <see cref="Dispose"/>。
/// </summary>
public sealed class FileDialogBarService : IDisposable
{
    // RegisterHotKey
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4644;              // 'FD'
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly Func<AppSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DialogSearchProvider _search;

    private FileDialogWatcher? _watcher;
    private FileDialogBarWindow? _bar;
    private HwndSource? _hotkeyHost;
    private DispatcherTimer? _fallbackTimer;
    private DispatcherTimer? _chipTimer;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _reShowTimer;

    /* 「对话框出现了但还不是前台」的短重试：120ms 一次，最多 6 次。 */
    private DispatcherTimer? _appearRetryTimer;
    private bool _navigating;
    private int _chipBusy;

    private DialogWindowInfo? _current;
    private DialogClassification _classification = DialogClassification.Unknown;

    private DialogNative.WinEventDelegate? _foregroundProc;
    private nint _foregroundHook;

    private bool _started;
    private bool _disposed;

    /// <summary>"仅本次"隐藏的是哪个对话框；新对话框不继承。</summary>
    private nint _sessionHiddenHwnd;

    /// <summary>保存设置（收藏/隐藏状态要持久化）。由 AppHost 注入。</summary>
    public Action? PersistSettings { get; set; }

    /// <summary>打开文件对话框相关设置页。由 AppHost 注入。</summary>
    public Action? OpenSettings { get; set; }

    /// <summary>最近一次查询耗时（毫秒），只保留一小段，用来在日志里看 p95 有没有退化。</summary>
    private readonly Queue<double> _queryMs = new();

    public FileDialogBarService(Func<AppSettings> settings, Dispatcher dispatcher)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _search = new DialogSearchProvider(settings);
    }

    /// <summary>当前是否已启用（监听在跑）。</summary>
    public bool IsRunning => _started;

    /// <summary>浮层是否正在显示（自检/排错用）。</summary>
    public bool IsBarVisible => _bar is { IsOpen: true };

    /// <summary>Watcher 跟踪中的窗口数（退出后必须归零）。</summary>
    public int TrackedDialogs => _watcher?.TrackedCount ?? 0;

    // ---------------- 启停 ----------------

    /// <summary>按当前设置启动。总开关关闭时什么都不做（不装钩子、不起定时器）。</summary>
    public void Start()
    {
        if (_disposed || _started) return;
        Apply();
    }

    /// <summary>
    /// 设置变化后重新应用：总开关、热键、类型开关、禁用进程。
    /// 关闭时把窗口监听、轮询定时器、热键、浮层全部收掉 —— 不留后台轮询。
    /// </summary>
    public void Apply()
    {
        if (_disposed) return;

        var options = _settings().FileDialogBar;
        _search.Invalidate();

        if (!options.Enabled)
        {
            Stop();
            CoreLog.Info("[文件对话框] 总开关关闭：已卸载监听/热键/浮层");
            return;
        }

        if (_watcher == null)
        {
            var watcher = new FileDialogWatcher();
            watcher.Appeared += OnAppeared;
            watcher.Destroyed += OnDestroyed;
            watcher.BoundsChanged += OnBoundsChanged;

            if (!watcher.Start())
            {
                watcher.Dispose();
                CoreLog.Warn("[文件对话框] 监听安装失败，本次不启用浮层（对话框本身不受影响）");
                return;
            }

            _watcher = watcher;
            EnsureForegroundHook();
        }

        _started = true;
        ApplyHotkey();
        ApplyFallbackTimer(options.PollFallbackMs);

        // 热键模式 + 当前已有前台对话框：不动；显示策略由 Appeared / 热键决定。
        if (!options.ShowOnAppear) HideBar("切换为热键模式");
    }

    private void Stop()
    {
        _started = false;
        HideBar("停用");
        _current = null;
        _classification = DialogClassification.Unknown;

        _fallbackTimer?.Stop();
        _fallbackTimer = null;
        _appearRetryTimer?.Stop();
        _appearRetryTimer = null;

        UnregisterHotkey();

        if (_watcher != null)
        {
            _watcher.Appeared -= OnAppeared;
            _watcher.Destroyed -= OnDestroyed;
            _watcher.BoundsChanged -= OnBoundsChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        UnhookForeground();
    }

    // ---------------- Watcher → Classifier → Bar ----------------

    /// <summary>
    /// 预热浮层窗口（启动空闲时调，可重复调）。失败只记日志 —— 预热不是功能前提。
    /// </summary>
    public void WarmUp()
    {
        try
        {
            if (!_started) return;
            if (!_settings().FileDialogBar.Enabled) return;
            EnsureBar().WarmUp();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "预热文件对话框浮层（服务层）"); }
    }

    private void OnAppeared(DialogWindowInfo info)
    {
        try
        {
            if (!_started || info == null) return;

            /* 同一个对话框的重复 SHOW（250ms 合并窗口之外的第二次）：
             * 浮层已经挂着就什么都不用做，否则会把用户正在用的输入状态清掉。 */
            if (_bar is { IsOpen: true } && _bar.DialogHandle == info.Hwnd) return;

            var options = _settings().FileDialogBar;
            var classification = Classify(info);
            if (!DialogPolicy.ShouldAttach(info, classification, options.Enabled, options.Types,
                    options.DisabledProcesses, out string reason))
            {
                CoreLog.Info($"[文件对话框] 不挂浮层：{info.ProcessName} {info.ClassName} —— {reason}");
                return;
            }

            /* 同一时刻只有一个"当前对话框"。新对话框出现时旧的那个要么已经关了，
             * 要么退到后台 —— 两种情况都不该继续显示旧浮层。 */
            if (_current != null && _current.Hwnd != info.Hwnd)
                HideBar("切换对话框");

            if (_sessionHiddenHwnd == info.Hwnd && _sessionHiddenHwnd != 0)
            {
                CoreLog.Info("[文件对话框] 本对话框已选『仅本次隐藏』，跳过");
                return;
            }

            _current = info;
            _classification = classification;

            CoreLog.Info($"[文件对话框] 候选：{info.ProcessName} pid={info.ProcessId} " +
                         $"类型={classification.Kind}/{classification.Family} 标题=\"{info.Title}\"");

            if (!options.ShowOnAppear) return;
            if (!IsForegroundWindow(info.Hwnd))
            {
                /* SHOW 常常比"切到前台"早一步到：以前这里只是等下一个前台事件，
                 * 万一那个事件先到过（或丢了），浮层就永远不出现 —— 就是"有时候有、有时候没有"。
                 * 现在挂一个短重试：最多 6 次 × 120ms，谁先到位谁显示。 */
                CoreLog.Info("[文件对话框] 不是前台窗口，等切到前台再显示（挂 120ms 重试）");
                ScheduleAppearRetry(info);
                return;
            }

            ShowBar(info, classification, takeKeyboard: false);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "处理文件对话框出现");
        }
    }

    private void OnDestroyed(nint hwnd)
    {
        try
        {
            if (_sessionHiddenHwnd == hwnd) _sessionHiddenHwnd = 0;
            _reShowTimer?.Stop();
            _reShowTimer = null;
            _appearRetryTimer?.Stop();
            _appearRetryTimer = null;
            if (_current != null && _current.Hwnd == hwnd)
            {
                _current = null;
                _classification = DialogClassification.Unknown;
            }

            if (_bar != null && _bar.IsOpen && _bar.DialogHandle == hwnd)
                HideBar("对话框已销毁");
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "处理文件对话框销毁");
        }
    }

    private void OnBoundsChanged(nint hwnd, DialogRect bounds)
    {
        try
        {
            if (_bar == null || !_bar.IsOpen) return;
            if (_bar.DialogHandle != hwnd) return;
            _bar.UpdateBounds(bounds);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "跟随对话框位置");
        }
    }

    // ---------------- Bar ----------------

    private void ShowBar(DialogWindowInfo info, DialogClassification classification, bool takeKeyboard)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var options = _settings().FileDialogBar;
            var bounds = DialogNative.FrameBounds(info.Hwnd);
            if (bounds.IsEmpty)
            {
                CoreLog.Warn("[文件对话框] 取对话框边框失败，本次不显示浮层");
                return;
            }

            var bar = EnsureBar();
            bar.SetBarHeight((int)Math.Round(options.BarHeight));
            bar.MetricsEnabled = options.FollowMetrics;
            if (!bar.ShowFor(info, bounds, takeKeyboard, out string detail))
            {
                CoreLog.Warn($"[文件对话框] 浮层显示失败：{detail}");
                return;
            }

            _current = info;
            _classification = classification;

            /* 一出现只给一条输入栏：列表不展开、不盖住对话框。
             * 用户开始打字才查；清空时再给"最近用过"作为空态。 */
            bar.CloseOverlay(false);
            bar.SetRows(Array.Empty<DialogSelection>(), "输入关键词搜索文件 · Tab 切换文件/文件夹");
            bar.SetStatus("输入关键词开始搜索");
            bar.SetFolderChip(null);
            StartChipTimer();
            RefreshChipNow();
            CoreLog.Info($"[文件对话框] 浮层已显示（{detail}），输入栏就位"
                         + $"；显示耗时 {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0}ms");
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "显示文件对话框浮层");
        }
    }

    private FileDialogBarWindow EnsureBar()
    {
        if (_bar != null) return _bar;

        var bar = new FileDialogBarWindow();
        bar.QueryChanged += OnQueryChanged;
        bar.CommitRequested += OnCommitRequested;
        bar.RevealRequested += OnRevealRequested;
        bar.Dismissed += () => HideBar("Esc");
        bar.FavoritesRequested += OnFavoritesRequested;
        bar.HistoryRequested += OnHistoryRequested;
        bar.MenuRequested += OnMenuRequested;
        bar.FolderChipRequested += OnFolderChipRequested;
        bar.FolderPicked += OnFolderPicked;
        bar.ActionRequested += OnQuickSwitchAction;
        _bar = bar;
        return bar;
    }

    /// <summary>
    /// 本程序自己的窗口要到前台了（主窗口 / 设置页等）：主动把浮条收掉。
    ///
    /// 为什么不能只靠前台钩子：<c>WINEVENT_SKIPOWNPROCESS</c> 会把"本进程窗口变成前台"
    /// 这个事件直接跳过 —— 于是从浮条菜单点「设置」之后，浮条会一直挂在屏幕上
    /// 盖着设置页，直到用户点到别的程序才消失。
    /// </summary>
    public void HideForOwnWindow()
    {
        try { HideBar("本程序窗口到前台"); }
        catch (Exception ex) { CoreLog.Swallow(ex, "本程序窗口到前台时收起浮条"); }
    }

    private void HideBar(string reason)
    {
        try
        {
            _reShowTimer?.Stop();
            _reShowTimer = null;
            _chipTimer?.Stop();
            _chipTimer = null;
            _statusTimer?.Stop();
            _statusTimer = null;
            _bar?.CloseOverlay(false);
            if (_bar == null || !_bar.IsOpen) return;
            _bar.HideBar();
            CoreLog.Info("[文件对话框] 浮层隐藏：" + reason);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "隐藏文件对话框浮层");
        }
    }

    // ---------------- 搜索 ----------------

    private void OnQueryChanged(string text, DialogScope scope)
    {
        try
        {
            if (_bar == null || !_bar.IsOpen) return;

            var options = _settings().FileDialogBar;
            var query = DialogSearchQuery.Create(text, scope);

            var sw = Stopwatch.StartNew();
            var rows = _search.Search(query, Math.Max(5, Math.Min(options.MaxResults, 200)));
            _bar.RowLimit = options.RowLimit;
            sw.Stop();
            NoteQueryTime(sw.Elapsed.TotalMilliseconds);

            string empty = query.IsEmpty
                ? "输入关键词搜索文件 · Tab 切换文件/文件夹"
                : "没有匹配的结果";
            _bar.SetRows(rows, empty);
            _bar.SetStatus(query.IsEmpty
                ? (rows.Count > 0 ? "最近使用" : DialogSearchQuery.ScopeLabel(scope))
                : $"{rows.Count} 条 · {DialogSearchQuery.ScopeLabel(scope)}");
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "文件对话框搜索");
        }
    }

    private void NoteQueryTime(double ms)
    {
        _queryMs.Enqueue(ms);
        while (_queryMs.Count > 50) _queryMs.Dequeue();
        if (ms > 80) CoreLog.Warn($"[文件对话框] 查询耗时 {ms:F0}ms（超过 80ms 预算）");
    }

    /// <summary>最近 50 次查询的 p95（自检/排错用）。</summary>
    public double QueryP95Ms
    {
        get
        {
            if (_queryMs.Count == 0) return 0;
            var list = new List<double>(_queryMs);
            list.Sort();
            int index = Math.Min(list.Count - 1, (int)Math.Ceiling(list.Count * 0.95) - 1);
            return list[Math.Max(0, index)];
        }
    }

    // ---------------- 回填 ----------------

    private void OnCommitRequested(DialogSelection item)
    {
        try
        {
            var info = _current;
            if (info == null || item == null) return;
            if (_bar == null || !_bar.IsOpen) return;
            if (!DialogNative.IsWindow(info.Hwnd))
            {
                HideBar("对话框已关闭");
                return;
            }

            var classification = _classification;
            var options = _settings().FileDialogBar;
            _bar.SetStatus("正在写入对话框…");

            /* 回填要按坐标点窗口、要等 2 秒校验、要动剪贴板 —— 必须在独立 STA
             * 线程上做：UI 线程一卡，对话框那边看起来就像死了一样（红线 4）。 */
            var thread = new Thread(() =>
            {
                BackfillOutcome outcome;
                try
                {
                    outcome = DialogBackfill.Submit(info, classification, item, options);
                }
                catch (Exception ex)
                {
                    outcome = BackfillOutcome.Fail(BackfillRoute.None, "回填异常：" + ex.Message);
                    CoreLog.Error("[文件对话框] 回填异常", ex);
                }

                if (outcome.Success)
                    DialogRecorder.Record(item, info.ProcessName, outcome.Route);

                _dispatcher.BeginInvoke(new Action(() => AfterSubmit(info, item, outcome)));
            })
            {
                IsBackground = true,
                Name = "InputPlus.FileDialog.Backfill",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "提交文件对话框选择");
        }
    }

    private void AfterSubmit(DialogWindowInfo info, DialogSelection item, BackfillOutcome outcome)
    {
        if (_disposed) return;

        if (outcome.Success)
        {
            CoreLog.Info($"[文件对话框] 回填成功 route={outcome.Route} path=\"{item.Path}\" —— {outcome.Detail}");
            HideBar("回填成功");
            return;
        }

        CoreLog.Warn($"[文件对话框] 回填失败 route={outcome.Route} path=\"{item.Path}\" —— {outcome.Detail}");

        /* 回填失败不弹模态框，也不把用户困在浮层里：
         * 直接在资源管理器里定位这个文件 —— 用户仍然能拖进去/复制，
         * 这就是"不需要（或不能）发送时跳到该文件所在文件夹"的那条路。 */
        if (DialogReveal.TryReveal(item.Path))
        {
            HideBar("回填失败，已在资源管理器定位");
            return;
        }

        if (!DialogNative.IsWindow(info.Hwnd)) { HideBar("对话框已关闭"); return; }
        if (_bar is { IsOpen: true })
        {
            string detail = outcome.Detail.Length > 60 ? outcome.Detail.Substring(0, 60) + "…" : outcome.Detail;
            _bar.SetStatus("没能写入对话框：" + detail);
        }
    }

    private void OnFavoritesRequested()
    {
        try
        {
            /* 收藏按钮 = Qbar 面板的「固定项」（用户按 Ctrl+P 固定的条目），
             * 不再读 FileDialogBar.Favorites。固定项是文件夹时，鼠标悬停会展开明细。 */
            var pinned = _settings().Qbar.Pinned ?? new List<QEntry>();
            var rows = new List<QuickSwitchRow>();
            foreach (var entry in pinned)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Value)) continue;
                string value = entry.Value.Trim();
                bool isDir = SafeDirectory(value);
                bool isFile = !isDir && SafeFile(value);
                if (!isDir && !isFile) continue; // 网址/命令固定项不参与目录导航

                rows.Add(new QuickSwitchRow
                {
                    Title = entry.DisplayName,
                    Subtitle = value,
                    Glyph = isDir ? "\uE8B7" : "\uE7C3",
                    Path = isDir ? value : System.IO.Path.GetDirectoryName(value) ?? value,
                    IconPath = value,
                    Expandable = isDir,
                });
            }
            if (rows.Count == 0)
                rows.Add(new QuickSwitchRow
                {
                    Title = "暂无固定项",
                    Subtitle = "在 Qbar 里按 Ctrl+P 固定文件或文件夹",
                    Glyph = "\uE734",
                    Enabled = false,
                });

            _bar?.ClearQueryForOverlay();
            _bar?.ShowFavoritesOverlay(rows, addEntry: false);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "打开固定项浮层"); }
    }

    private void OnHistoryRequested()
    {
        try
        {
            /* 历史按钮 = Qbar 空态的「搜索前建议」内容；文件取父目录导航，
             * 文件夹直接导航。不再显示目录历史缓存。 */
            var rows = new List<QuickSwitchRow>();
            var recent = SuggestionsService.Current?.FirstScreen(20);
            if (recent != null)
            {
                foreach (var item in recent)
                {
                    if (item.ItemType == RecItemType.App) continue;
                    string path = item.Path;
                    if (item.ItemType == RecItemType.File)
                    {
                        try { path = System.IO.Path.GetDirectoryName(path) ?? path; }
                        catch { }
                    }
                    rows.Add(new QuickSwitchRow
                    {
                        Title = item.DisplayName,
                        Subtitle = item.Path,
                        Glyph = item.ItemType == RecItemType.Folder ? "\uE8B7" : "\uE7C3",
                        Path = path,
                        IconPath = item.Path,
                    });
                }
            }
            if (rows.Count == 0)
                rows.Add(new QuickSwitchRow
                {
                    Title = "暂无建议",
                    Subtitle = "先在 Qbar 里搜索或打开一些文件",
                    Glyph = "\uE81C",
                    Enabled = false,
                });

            _bar?.ClearQueryForOverlay();
            _bar?.ShowHistoryOverlay(rows);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "打开建议浮层"); }
    }

    private static bool SafeDirectory(string path)
    {
        try { return System.IO.Directory.Exists(path); } catch { return false; }
    }

    private static bool SafeFile(string path)
    {
        try { return System.IO.File.Exists(path); } catch { return false; }
    }

    private void OnMenuRequested()
    {
        try { _bar?.ShowMenuOverlay(); }
        catch (Exception ex) { CoreLog.Swallow(ex, "打开 QuickSwitch 菜单"); }
    }

    private void OnFolderChipRequested() => NavigateToExplorerFolder();

    private void OnFolderPicked(string path) => NavigateTo(path);

    private void OnQuickSwitchAction(QuickSwitchAction action)
    {
        try
        {
            var info = _current;
            /* 每一次菜单动作都留一行日志：用户报"点了没反应"时，先看日志有没有这一行 ——
             * 没有 = 点击根本没到菜单行；有 = 动作到了、问题在后面（窗口没抢到前台之类）。 */
            CoreLog.Info($"[浮条] 菜单动作：{action}" + (info != null ? $"（对话框 {info.ProcessName}）" : ""));
            switch (action)
            {
                case QuickSwitchAction.OpenSettings:
                    if (OpenSettings == null)
                    {
                        CoreLog.Warn("[浮条] OpenSettings 没有接上（AppHost 没赋值），这次点击不会有任何反应");
                        _bar?.SetStatus("设置入口没接上，请反馈日志");
                    }
                    else OpenSettings();
                    break;
                case QuickSwitchAction.RevealExplorerFolder:
                    NavigateToExplorerFolder();
                    break;
                case QuickSwitchAction.ShowHideMenu:
                    _bar?.ShowHideOverlay();
                    break;
                case QuickSwitchAction.CloseBar:
                    HideBar("菜单关闭");
                    break;
                case QuickSwitchAction.HideSession:
                    if (info != null) _sessionHiddenHwnd = info.Hwnd;
                    HideBar("仅本次隐藏");
                    break;
                case QuickSwitchAction.HideProcess:
                    if (info != null)
                    {
                        var options = _settings().FileDialogBar;
                        string name = (info.ProcessName ?? "").ToLowerInvariant();
                        if (name.Length > 0 && !options.DisabledProcesses.Contains(name, StringComparer.OrdinalIgnoreCase))
                            options.DisabledProcesses.Add(name);
                        PersistSettings?.Invoke();
                    }
                    HideBar("在此应用中禁用");
                    break;
                case QuickSwitchAction.HideGlobal:
                    _settings().FileDialogBar.Enabled = false;
                    PersistSettings?.Invoke();
                    HideBar("全局关闭");
                    break;
                case QuickSwitchAction.AddFavorite:
                    AddCurrentFolderToFavorites();
                    break;
                case QuickSwitchAction.ClearHistory:
                    QuickSwitchHistory.Clear();
                    _bar?.SetStatus("历史已清除");
                    OnHistoryRequested();
                    break;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "处理 QuickSwitch 菜单动作"); }
    }

    private string? DialogFolder()
        => _current == null ? null : QuickSwitchFolderProbe.DialogFolder(_current.Hwnd);

    private void AddCurrentFolderToFavorites()
    {
        string? folder = DialogFolder();
        if (string.IsNullOrWhiteSpace(folder))
        {
            _bar?.SetStatus("取不到对话框当前目录，无法添加收藏");
            return;
        }

        var options = _settings().FileDialogBar;
        if (!QuickSwitchCatalog.AddFavorite(options, folder, out string error))
        {
            _bar?.SetStatus(error);
            return;
        }

        PersistSettings?.Invoke();
        _bar?.SetStatus("已添加收藏");
        OnFavoritesRequested();
    }

    private void NavigateToExplorerFolder()
    {
        string? folder = QuickSwitchFolderProbe.ExplorerFolder();
        if (string.IsNullOrWhiteSpace(folder))
        {
            _bar?.SetStatus("没找到正在浏览的资源管理器窗口");
            return;
        }
        NavigateTo(folder);
    }

    private void NavigateTo(string? folder)
    {
        if (_navigating) return;
        var info = _current;
        if (info == null || string.IsNullOrWhiteSpace(folder)) return;
        if (!DialogNative.IsWindow(info.Hwnd)) { HideBar("对话框已关闭"); return; }

        string path = folder.Trim();
        _navigating = true;
        _bar?.SetStatus("正在导航…");

        var classification = _classification;
        var options = _settings().FileDialogBar;
        var thread = new Thread(() =>
        {
            BackfillOutcome outcome;
            try { outcome = DialogBackfill.Navigate(info, classification, path, options); }
            catch (Exception ex) { outcome = BackfillOutcome.Fail(BackfillRoute.None, "导航异常：" + ex.Message); }

            _dispatcher.BeginInvoke(new Action(() => AfterNavigate(info, path, outcome)));
        })
        {
            IsBackground = true,
            Name = "InputPlus.QuickSwitch.Navigate",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void AfterNavigate(DialogWindowInfo info, string folder, BackfillOutcome outcome)
    {
        _navigating = false;
        try
        {
            if (outcome.Success)
            {
                QuickSwitchHistory.Record(folder);
                _bar?.SetStatus("已导航：" + (System.IO.Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder));
                ScheduleStatusClear();
                RefreshChipNow();
                CoreLog.Info($"[QuickSwitch] 已导航 \"{folder}\"（{outcome.Route}）—— {outcome.Detail}");
            }
            else
            {
                _bar?.SetStatus("导航失败：" + (outcome.Detail.Length > 60 ? outcome.Detail[..60] + "…" : outcome.Detail));
                ScheduleStatusClear();
                CoreLog.Warn($"[QuickSwitch] 导航失败 \"{folder}\" —— {outcome.Detail}");
            }

            if (!DialogNative.IsWindow(info.Hwnd)) HideBar("对话框已关闭");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "处理导航结果"); }
    }

    private void ScheduleStatusClear()
    {
        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            _statusTimer = null;
            if (_bar is { OverlayOpen: false }) _bar.SetStatus("");
        };
        _statusTimer.Start();
    }

    /// <summary>
    /// 收起之后的重试：**事件驱动、立刻一次**，失败 30ms 再试一次就放弃。
    ///
    /// 以前是 250ms 的定时器 —— 用户切回对话框后要等整整 250ms 浮条才回来，
    /// 这就是"切走再切回要等一下"的来由。现在先就地试一次（前台刚回到对话框时
    /// 这一下通常就成），再补一个 30ms 的短重试兜住事件顺序。
    /// </summary>
    private void StartReShowTimer()
    {
        TryReShowOnce();

        _reShowTimer?.Stop();
        _reShowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _reShowTimer.Tick += (_, _) =>
        {
            _reShowTimer?.Stop();
            _reShowTimer = null;
            TryReShowOnce();
        };
        _reShowTimer.Start();
    }

    /// <summary>
    /// 「对话框出现了但还不是前台」的短重试。
    ///
    /// 为什么要它：SHOW 事件经常比"对话框真的到前台"早几十毫秒；以前只靠下一个前台事件，
    /// 而那个事件可能已经先到（或丢了），于是这次出现就永远不会显示浮层 —— 用户看到的就是
    /// "有时候有、有时候没有"。现在最多 6 × 120ms，一旦它是前台就显示。
    /// </summary>
    private void ScheduleAppearRetry(DialogWindowInfo info)
    {
        try
        {
            _appearRetryTimer?.Stop();
            _appearRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            int tries = 0;
            _appearRetryTimer.Tick += (_, _) =>
            {
                if (++tries > 6)
                {
                    _appearRetryTimer?.Stop();
                    _appearRetryTimer = null;
                    return;
                }
                if (_bar is { IsOpen: true })
                {
                    _appearRetryTimer?.Stop();
                    _appearRetryTimer = null;
                    return;
                }
                if (info.Hwnd == 0 || !DialogNative.IsWindow(info.Hwnd)) { _appearRetryTimer?.Stop(); _appearRetryTimer = null; return; }
                if (!IsForegroundWindow(info.Hwnd)) return;

                _appearRetryTimer?.Stop();
                _appearRetryTimer = null;
                if (!_settings().FileDialogBar.ShowOnAppear) return;
                CoreLog.Info("[文件对话框] 对话框已到前台，补显示浮层（重试命中）");
                ShowBar(info, _classification, takeKeyboard: false);
            };
            _appearRetryTimer.Start();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "安排对话框出现重试"); }
    }

    private void TryReShowOnce()    {
        try
        {
            var info = _current;
            if (info == null || _bar is { IsOpen: true }) return;
            if (_sessionHiddenHwnd == info.Hwnd) return;
            if (!_settings().FileDialogBar.ShowOnAppear) return;
            if (!IsForegroundWindow(info.Hwnd)) return;
            ShowBar(info, _classification, takeKeyboard: false);
            CoreLog.Info("[文件对话框] 浮层立即重显示（不等 250ms）");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "重新显示文件对话框浮层"); }
    }

    private void StartChipTimer()
    {
        _chipTimer?.Stop();
        _chipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _chipTimer.Tick += (_, _) => RefreshChipNow();
        _chipTimer.Start();
    }

    private void RefreshChipNow()
    {
        if (_bar == null || !_bar.IsOpen || _current == null) return;
        if (Interlocked.Exchange(ref _chipBusy, 1) == 1) return;

        nint dialog = _current.Hwnd;
        Task.Run(() =>
        {
            string? explorer = QuickSwitchFolderProbe.ExplorerFolder();
            string? current = QuickSwitchFolderProbe.DialogFolder(dialog);
            _dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _chipBusy, 0);
                if (_bar == null || !_bar.IsOpen) return;
                bool same = explorer != null && current != null
                            && string.Equals(explorer.TrimEnd('\\'), current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                _bar.SetFolderChip(same ? null : explorer);
            }));
        });
    }

    /// <summary>Alt+Enter：不碰对话框，直接在资源管理器里定位选中项。</summary>
    private void OnRevealRequested(DialogSelection item)
    {
        try
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;
            bool ok = DialogReveal.TryReveal(item.Path);
            CoreLog.Info($"[文件对话框] Alt+Enter 定位 \"{item.Path}\" → {(ok ? "成功" : "失败")}");
            HideBar(ok ? "已在资源管理器定位" : "定位失败");
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "文件对话框定位");
        }
    }

    // ---------------- 热键 ----------------

    private void ApplyHotkey()
    {
        var options = _settings().FileDialogBar;
        if (!options.ShowOnHotkey) { UnregisterHotkey(); return; }

        try
        {
            _hotkeyHost ??= CreateHotkeyHost();
            if (_hotkeyHost == null) return;

            UnregisterHotKey(_hotkeyHost.Handle, HotkeyId);

            /* 热键被别的程序占用是常态（本机 Ctrl+Alt+F 就被占了）。浮层默认已经
             * "对话框一出现就显示"，热键只是补充，所以尝试几个备选，失败也只记日志。 */
            string[] candidates =
            {
                options.Hotkey,
                "Ctrl+Shift+Alt+F",
                "Ctrl+Alt+Shift+F",
                "Ctrl+Alt+D",
            };

            foreach (string combo in candidates)
            {
                if (string.IsNullOrWhiteSpace(combo)) continue;
                var parsed = ParseHotkey(combo);
                if (parsed == null) continue;
                if (!RegisterHotKey(_hotkeyHost.Handle, HotkeyId,
                        parsed.Value.Mods | ModNoRepeat, (uint)parsed.Value.Vk))
                    continue;

                if (!string.Equals(combo, options.Hotkey, StringComparison.OrdinalIgnoreCase))
                    CoreLog.Info("[文件对话框] 配置的热键 " + options.Hotkey + " 被占用，改用备用热键 " + combo);
                return;
            }

            CoreLog.Warn("[文件对话框] 所有候选热键都注册失败；不影响「对话框一出现就显示」");
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "注册文件对话框热键");
        }
    }

    private HwndSource? CreateHotkeyHost()
    {
        try
        {
            var parameters = new HwndSourceParameters("InputPlusFileDialogHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            var source = new HwndSource(parameters);
            source.AddHook(HotkeyWndProc);
            return source;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "创建文件对话框热键窗口");
            return null;
        }
    }

    private nint HotkeyWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            ToggleBar();
        }
        return 0;
    }

    private void UnregisterHotkey()
    {
        try
        {
            if (_hotkeyHost != null) UnregisterHotKey(_hotkeyHost.Handle, HotkeyId);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "注销文件对话框热键");
        }
    }

    /// <summary>热键：显示 → 隐藏；否则给当前对话框挂上浮层并抢键盘。</summary>
    public void ToggleBar()
    {
        if (_disposed) return;

        var options = _settings().FileDialogBar;
        if (!options.Enabled) return;

        if (_bar is { IsOpen: true })
        {
            HideBar("热键切换");
            return;
        }

        var info = ResolveTargetDialog(out string reason);
        if (info == null)
        {
            CoreLog.Info("[文件对话框] 热键：当前没有可挂的文件对话框（" + reason + "）");
            return;
        }

        var classification = Classify(info);
        if (!DialogPolicy.ShouldAttach(info, classification, options.Enabled, options.Types,
                options.DisabledProcesses, out string why))
        {
            CoreLog.Info("[文件对话框] 热键：判定不挂浮层（" + why + "）");
            return;
        }

        _current = info;
        _classification = classification;
        ShowBar(info, classification, takeKeyboard: true);
    }

    /// <summary>当前该挂哪个对话框：前台窗口优先，其次最近一次发现的候选。</summary>
    private DialogWindowInfo? ResolveTargetDialog(out string reason)
    {
        reason = "";

        nint fg = DialogNative.GetForegroundWindow();
        if (fg != 0)
        {
            nint root = DialogNative.GetAncestor(fg, 2 /* GA_ROOT */);
            if (root != 0) fg = root;

            if (DialogNative.IsWindow(fg) && DialogNative.ClassOf(fg) == DialogProbe.DialogClass)
            {
                var info = FileDialogWatcher.Describe(fg);
                if (info != null) return info;
            }
        }

        if (_current != null && DialogNative.IsWindow(_current.Hwnd) && DialogNative.IsWindowVisible(_current.Hwnd))
            return _current;

        reason = fg == 0 ? "没有前台窗口" : "前台是 " + DialogNative.ClassOf(fg);
        return null;
    }

    private static (uint Mods, int Vk)? ParseHotkey(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;

        uint mods = 0;
        int vk = 0;
        foreach (string raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= ModControl;
                    continue;
                case "shift":
                    mods |= ModShift;
                    continue;
                case "alt":
                case "menu":
                    mods |= ModAlt;
                    continue;
                case "win":
                case "windows":
                case "lwin":
                    mods |= ModWin;
                    continue;
            }

            int? parsed = KeyName.VkOf(part);
            if (parsed == null || parsed.Value == 0) return null;
            vk = parsed.Value;
        }
        return vk == 0 ? null : (mods, vk);
    }

    /// <summary>
    /// 设置页用的热键校验：成功时 detail 是规范化写法（Mods 顺序固定、主键用可读名），
    /// 失败时返回 false。和 <see cref="ParseHotkey"/> 共用同一份解析器，
    /// 避免“设置页说能用、真正注册时解析失败”的两份真相。
    /// </summary>
    public static bool TryValidateHotkey(string? combo, out string detail)
    {
        detail = "";
        var parsed = ParseHotkey(combo);
        if (parsed == null) return false;

        var parts = new List<string>(5);
        if ((parsed.Value.Mods & ModControl) != 0) parts.Add("Ctrl");
        if ((parsed.Value.Mods & ModShift) != 0) parts.Add("Shift");
        if ((parsed.Value.Mods & ModAlt) != 0) parts.Add("Alt");
        if ((parsed.Value.Mods & ModWin) != 0) parts.Add("Win");
        /* 单个字母键统一大写：设置页显示 Ctrl+Alt+F 比 Ctrl+Alt+f 更像用户自己写的，
           也让"校验后回写"不会因为大小写把输入改得面目全非。 */
        string key = KeyName.NameOrGenerated(parsed.Value.Vk);
        if (key.Length == 1) key = key.ToUpperInvariant();
        parts.Add(key);
        detail = string.Join("+", parts);
        return true;
    }

    // ---------------- 前台监听 / 兜底轮询 ----------------

    private void EnsureForegroundHook()
    {
        if (_foregroundHook != 0) return;
        try
        {
            _foregroundProc = OnForegroundEvent;
            _foregroundHook = DialogNative.SetWinEventHook(
                DialogNative.EVENT_SYSTEM_FOREGROUND, DialogNative.EVENT_SYSTEM_FOREGROUND,
                0, _foregroundProc, 0, 0,
                DialogNative.WINEVENT_OUTOFCONTEXT | DialogNative.WINEVENT_SKIPOWNPROCESS);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "安装前台窗口监听");
        }
    }

    private void UnhookForeground()
    {
        if (_foregroundHook == 0) return;
        try { DialogNative.UnhookWinEvent(_foregroundHook); } catch { }
        _foregroundHook = 0;
        _foregroundProc = null;
    }

    private void OnForegroundEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            // 事件在安装线程（UI 线程）上投递；重入 ShowFor 会打架，统一排队一次
            _dispatcher.BeginInvoke(new Action(OnForegroundChanged), DispatcherPriority.Input);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "处理前台切换");
        }
    }

    private void OnForegroundChanged()
    {
        if (_disposed || !_started) return;

        nint fg = DialogNative.GetForegroundWindow();
        if (fg == 0) return;

        // 浮层自己拿到前台（抢焦点阶梯成功过）不算"离开对话框"
        if (_bar != null && _bar.IsOpen && fg == new WindowInteropHelper(_bar).Handle) return;

        /* 菜单开着的时候，我们正在给菜单抢键盘：抢的过程中前台可能短暂落在
         * 本进程的另一个窗口上。只要还是我们自己的进程、菜单还开着，就不算
         * 离开对话框 —— 否则"点开更多"的一瞬间整条浮条就没了。 */
        if (_bar is { IsOpen: true, OverlayOpen: true }
            && DialogNative.GetWindowThreadProcessId(fg, out uint fgOwner) != 0
            && fgOwner == (uint)Environment.ProcessId)
            return;

        var options = _settings().FileDialogBar;
        nint root = DialogNative.GetAncestor(fg, 2);
        if (root == 0) root = fg;

        if (DialogNative.ClassOf(root) != DialogProbe.DialogClass)
        {
            if (_bar is { IsOpen: true })
            {
                /* 拖动中（对话框在动、鼠标还按着）不要收：前台可能只是短暂跳到别处，
                 * 收掉再回来就是"浮条没了、几百毫秒后才出现"。 */
                if (_bar is { Dragging: true } && _bar.DialogHandle != 0
                    && DialogNative.IsWindow(_bar.DialogHandle))
                    return;

                /* 把"抢走前台的是谁"写清楚：浮条菜单自己会 Activate()，
                 * 一旦系统把前台交给了别的窗口，这里就会整条收起 ——
                 * 用户看到的是"点了菜单，菜单没了"。 */
                CoreLog.Info($"[文件对话框] 前台离开对话框：fg=0x{fg:X} class={DialogNative.ClassOf(fg)} "
                             + $"root=0x{root:X} rootClass={DialogNative.ClassOf(root)} "
                             + $"title=\"{DialogNative.TitleOf(fg)}\" pid={FgPid(fg)}");
                HideBar("前台已离开文件对话框");
                StartReShowTimer();
            }
            return;
        }

        var info = FileDialogWatcher.Describe(root);
        if (info == null) return;

        if (_current == null || _current.Hwnd != info.Hwnd)
        {
            var classification = Classify(info);
            if (!DialogPolicy.ShouldAttach(info, classification, options.Enabled, options.Types,
                    options.DisabledProcesses, out _))
            {
                if (_bar is { IsOpen: true }) HideBar("前台对话框不挂浮层");
                return;
            }

            _current = info;
            _classification = classification;
        }

        if (!options.ShowOnAppear)
        {
            if (_bar is { IsOpen: true } && _bar.DialogHandle != info.Hwnd) HideBar("前台切到另一个对话框");
            return;
        }

        if (_bar is { IsOpen: true } && _bar.DialogHandle == info.Hwnd) return;
        ShowBar(info, _classification, takeKeyboard: false);
    }

    private void ApplyFallbackTimer(int pollMs)
    {
        if (pollMs < 100) pollMs = 100;

        if (_fallbackTimer == null)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(pollMs),
            };
            timer.Tick += (_, _) =>
            {
                try { _watcher?.PollOnce(); }
                catch (Exception ex) { CoreLog.Swallow(ex, "文件对话框兜底轮询"); }
            };
            _fallbackTimer = timer;
        }

        _fallbackTimer.Interval = TimeSpan.FromMilliseconds(pollMs);
        if (!_fallbackTimer.IsEnabled) _fallbackTimer.Start();
    }

    // ---------------- 工具 ----------------

    private static DialogClassification Classify(DialogWindowInfo info)
    {
        var children = DialogProbe.Children(info.Hwnd);
        return DialogClassifier.Classify(info.ClassName, children);
    }

    private static bool IsForegroundWindow(nint hwnd)
    {
        nint fg = DialogNative.GetForegroundWindow();
        if (fg == 0) return false;
        if (fg == hwnd) return true;
        return DialogNative.GetAncestor(fg, 2) == hwnd;
    }

    /// <summary>前台窗口属于哪个进程（诊断日志用，0 = 读不到）。</summary>
    private static uint FgPid(nint hwnd)
    {
        try { DialogNative.GetWindowThreadProcessId(hwnd, out uint pid); return pid; }
        catch { return 0; }
    }

    // Win32 热键
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        if (_bar != null)
        {
            try
            {
                _bar.QueryChanged -= OnQueryChanged;
                _bar.CommitRequested -= OnCommitRequested;
                _bar.Close();
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "关闭文件对话框浮层");
            }
            _bar = null;
        }

        if (_hotkeyHost != null)
        {
            try { _hotkeyHost.Dispose(); } catch { }
            _hotkeyHost = null;
        }

        CoreLog.Info("[文件对话框] 服务已释放");
    }
}
