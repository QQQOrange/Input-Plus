using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using InputPlus.Core.Actions;
using InputPlus.Core.Gestures;
using InputPlus.Core.Icons;
using InputPlus.Core.Interop;
using InputPlus.Core.Localization;
using InputPlus.Core.Windows;

namespace InputPlus.UI.Windows;

/// <summary>
/// <see cref="GestureMenuWindow"/> 的"多级下钻"部分（v1.6.7）。
///
/// ── 一条链是怎么长出来的 ────────────────────────────────────────
///   level 0 : 菜单自己的条目（_entries）
///   level N : 上一级某个条目的 Children（文件夹 = 懒加载出来的子文件夹）
/// 每一级都是一张独立的卡片（Border + ListBox），由 <see cref="CascadePanel"/> 摆放。
/// 卡片位置由窗口算：左沿贴着上一级卡片的右沿，上沿对齐"打开它的那一行"。
///
/// ── 悬停为什么是轮询而不是事件 ──────────────────────────────────
/// 每多一级，窗口可能整体往左让（右边放不下），鼠标相对卡片的位置在同一拍里就变了。
/// MouseEnter/Leave 的事件顺序在这种"布局刚变完"的时刻不可靠，
/// 用 40ms 轮询拿光标位置去问"现在指着哪一行"，再按 150ms/250ms 的延迟决定开/收，
/// 才能同时满足两条相反的验收：斜着移过去不能误关、离开整条链才收。
///
/// ── 收起的三档（对应需求 A / G）────────────────────────────────
///   · 指到**另一行**（不展开的）：250ms 后收起它下面那几级，祖先保持展开；
///   · 指针离开**整条链**：250ms 后收起全部子级（一级菜单留着）；
///   · Esc / 点别处 / 关窗：整条链一并销毁（最后一条由 SafeClose 保证）。
/// 指针只要回到链上任何一张卡片，计时立刻取消。
/// </summary>
public partial class GestureMenuWindow
{
    /// <summary>卡片宽度（和 XAML 里 Menu.Card 的 Width 必须一致）。</summary>
    private const double CardWidth = 160;

    /// <summary>悬停多久才展开下一级。</summary>
    private const int HoverOpenDelayMs = 150;

    /// <summary>指针离开目标多久才收起。</summary>
    private const int HoverCloseDelayMs = 250;

    /// <summary>一级 + 已展开的各级卡片，索引就是层级。</summary>
    private readonly List<MenuLevel> _levels = new();

    /// <summary>悬停轮询（40ms）。只在菜单显示期间跑。</summary>
    private readonly DispatcherTimer _hoverPump = new(DispatcherPriority.Input);

    private bool _hoverPumpRunning;

    /// <summary>本窗口所在显示器的工作区（DIP，屏幕坐标）。</summary>
    private Rect _workArea;

    /// <summary>菜单左上角的期望位置（DIP）。窗口因为内容变多而放不下时按它夹回来。</summary>
    private Point _anchor;

    /// <summary>窗口刚挪过位置的时刻：这一小段时间内不做悬停判定（否则会连环跳级）。</summary>
    private long _settleUntil;

    /// <summary>自检里关掉悬停（否则真实光标位置会让断言飘）。</summary>
    private bool _hoverEnabled = true;

    /// <summary>自检里关掉"每级淡入"（否则刚展开那一拍量到的还是 -6 DIP 的偏移）。</summary>
    private bool _animateLevels = true;

    /// <summary>
    /// 现在是"键盘在操作"：悬停判定整个让位。
    ///
    /// 为什么需要：纯键盘用户（→ 展开、Tab 换层）时鼠标可能停在半米外，
    /// 悬停轮询会认为"指针离开了整条链"，250ms 后把刚用键盘展开的面板全收掉。
    /// 只要鼠标一动就退出这个模式，鼠标用户的行为完全不变。
    /// </summary>
    private bool _keyboardMode;

    private NativeMethods.POINT _lastCursor;
    private bool _hasLastCursor;

    // 当前指着的那一行 + 已经停了多久
    private GestureMenuEntry? _hoverEntry;
    private int _hoverLevel = -1;
    private long _hoverSince;

    /// <summary>待执行的收起动作。</summary>
    private enum PendingAction
    {
        None = 0,
        CloseAll = 1,
        CollapseAbove = 2,
    }

    private PendingAction _pending;
    private long _pendingAt;
    private int _pendingLevel;

    /// <summary>一级 + 已展开的级数。</summary>
    internal int LevelCount => _levels.Count;

    /// <summary>一级卡片的尺寸与位置：面板里唯一一个位置固定的孩子。</summary>
    private void SetupBaseLevel()
    {
        var level = new MenuLevel(0, MainCard, ItemList, null)
        {
            VisibleX = 14,
            VisibleY = 14,
        };
        _levels.Add(level);

        foreach (GestureMenuEntry entry in _entries) entry.SetLevel(0);
        CascadeHost.SetRect(MainCard, new Rect(0, 0, 0, 0));

        AutomationProperties.SetName(ItemList, Loc.T("第 1 级菜单"));
        AutomationProperties.SetHelpText(ItemList,
            Loc.T("上下键选择，右方向键展开下一级，回车打开，Esc 收起"));

        // "这一条是不是文件夹"要碰磁盘：全部丢后台，UI 线程一次 IO 都不做。
        ProbeEntries(_entries);
    }

    // ── 展开 / 收起 ────────────────────────────────────────────────

    /// <summary>
    /// 展开某一行的下一级。已经开着就只把焦点/高亮挪过去，不重建（避免闪一下）。
    /// </summary>
    private MenuLevel? OpenChildLevel(MenuLevel parent, GestureMenuEntry entry, bool focusChild)
    {
        if (!entry.CanExpand)
        {
            /* 叶子（没有子文件夹）：按用户要求"不展开、不显示箭头"。
             * 走到这里说明是从键盘按的 → 什么也不做，别弹个空面板。 */
            return null;
        }

        if (parent.Index + 1 < _levels.Count &&
            ReferenceEquals(_levels[parent.Index + 1].Owner, entry))
        {
            if (focusChild) FocusLevel(_levels[parent.Index + 1]);
            return _levels[parent.Index + 1];
        }

        /* 祖先链判重：同一个目录不许在这条链上出现第二次。
         * 这是"防无限下钻"的第二道闸（第一道是枚举时跳过重解析点）——
         * subst 盘符、映射网络驱动器这类"同一个目录两种写法"只有这里挡得住。 */
        if (entry.FolderPath is { Length: > 0 } path && ChainContainsPath(parent, path))
        {
            entry.SetError(Loc.T("这个目录已在上级展开（防止循环）"));
            return null;
        }

        // 同级切换：只替换这一层及以下，祖先链保持不动。
        CloseLevelsAbove(parent.Index);

        MenuLevel level = CreateLevel(parent.Index + 1, entry);
        _levels.Add(level);
        entry.IsExpanded = true;

        parent.List.ScrollIntoView(entry);
        parent.List.SelectedItem = entry;

        if (entry.Kind == GestureMenuEntry.EntryKind.Folder)
        {
            if (entry.LoadState == GestureMenuEntry.FolderLoadState.None)
                StartFolderLoad(entry, fresh: false);
        }
        else
        {
            // 静态子项（历史记录/命令）里也可能有文件夹，同样要探一次。
            ProbeEntries(entry.Children);
        }

        Reflow();
        if (focusChild)
        {
            // 键盘/箭头按钮展开：把悬停判定让开，别让"鼠标在别处"把刚开的面板收掉。
            _keyboardMode = true;
            FocusLevel(level);
        }
        return level;
    }

    /// <summary>
    /// 收起 <paramref name="keepThrough"/> 以下的各级（keepThrough 自己也留着）。
    /// <paramref name="moveFocus"/> = true 时把焦点送回留下来的那一级（键盘操作用）。
    /// </summary>
    private void CloseLevelsAbove(int keepThrough, bool moveFocus = false)
    {
        if (keepThrough < 0) keepThrough = 0;
        CancelPending();

        if (_levels.Count <= keepThrough + 1)
        {
            _hoverEntry = null;
            return;
        }

        for (int i = _levels.Count - 1; i > keepThrough; i--)
        {
            MenuLevel level = _levels[i];
            if (level.Owner != null) level.Owner.IsExpanded = false;
            CascadeHost.Children.Remove(level.Card);
            level.List.ItemsSource = null;
            _levels.RemoveAt(i);
        }

        _hoverEntry = null;
        Reflow();

        if (moveFocus && _levels.Count > 0)
            FocusLevel(_levels[Math.Min(keepThrough, _levels.Count - 1)]);
    }

    /// <summary>这条链上（从一级到 <paramref name="upTo"/>）是不是已经打开过同一个目录。</summary>
    private bool ChainContainsPath(MenuLevel upTo, string path)
    {
        string key = RevealPath.CompareKey(path);
        if (key.Length == 0) return false;

        for (int i = 1; i <= upTo.Index && i < _levels.Count; i++)
        {
            string? ancestor = _levels[i].Owner?.FolderPath;
            if (ancestor != null && RevealPath.CompareKey(ancestor) == key) return true;
        }
        return false;
    }

    /// <summary>造一张新的层级卡片（样式全部来自 XAML 的 Menu.Card / SubMenuRow）。</summary>
    private MenuLevel CreateLevel(int index, GestureMenuEntry owner)
    {
        var card = new Border { Style = (Style)FindResource("Menu.Card") };

        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = (Brush)FindResource("Brush.TextPrimary"),
            Focusable = true,
            FocusVisualStyle = null,
            ItemContainerStyle = (Style)FindResource("SubMenuRow"),
            ItemTemplate = (DataTemplate)FindResource("SubEntryTemplate"),
            ItemsSource = owner.Children,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        list.PreviewMouseLeftButtonUp += OnEntryClick;

        AutomationProperties.SetName(list, Loc.F("第 {0} 级菜单", index + 1));
        AutomationProperties.SetHelpText(list,
            Loc.T("上下键选择，右方向键展开下一级，回车打开，Esc 收起"));

        foreach (GestureMenuEntry child in owner.Children) child.SetLevel(index);

        card.Child = list;
        CascadeHost.Children.Add(card);
        AnimateIn(card);

        // 这一级的文件夹图标：后台取，回来原地贴（见 QueueIconLoad）。
        QueueIconLoad(owner.Children);

        return new MenuLevel(index, card, list, owner);
    }

    // ── 图标（资源管理器的真图标）──────────────────────────────────

    /// <summary>一批图标最多取这么久（毫秒）。网络路径断掉时不至于一条一条等超时。</summary>
    private const int IconBudgetMs = 2000;

    /// <summary>正在后台取图标的行（避免同一行被重复排队）。</summary>
    private readonly HashSet<GestureMenuEntry> _iconsInFlight = new();

    /// <summary>
    /// 给一批行补**真实图标**（和资源管理器同一套 shell 图标）。
    ///
    /// 为什么不在构造行时同步取：二级起一次可能出来几百条，每条都要走一遍 shell；
    /// 本地目录是毫秒级，网络路径则可能一条卡到 1.2 秒（<c>IconCache</c> 的同步等待上限），
    /// 压在 UI 线程上就是用户说的"菜单卡一下"。
    /// 这里整批丢后台、限时 2 秒，回来后在 UI 线程原地换 —— 行高不变，不触发重排。
    /// </summary>
    private void QueueIconLoad(IEnumerable<GestureMenuEntry> entries)
    {
        var pending = new List<(GestureMenuEntry Entry, string Path)>();
        foreach (GestureMenuEntry entry in entries)
        {
            if (!entry.NeedsIcon || _iconsInFlight.Contains(entry)) continue;
            string path = (entry.Item.IconPath ?? "").Trim();
            if (path.Length == 0) continue;
            _iconsInFlight.Add(entry);
            pending.Add((entry, path));
        }
        if (pending.Count == 0) return;

        Dispatcher dispatcher = Dispatcher;
        Task.Run(() =>
        {
            var results = new List<(GestureMenuEntry Entry, System.Windows.Media.ImageSource? Icon)>(pending.Count);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach ((GestureMenuEntry entry, string path) in pending)
            {
                if (watch.ElapsedMilliseconds > IconBudgetMs) break;
                ImageSource? icon = null;
                try { icon = IconCache.FromFile(path); }
                catch { icon = null; }
                results.Add((entry, icon));
            }
            return results;
        }).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                foreach ((GestureMenuEntry entry, ImageSource? icon) in task.Result)
                {
                    _iconsInFlight.Remove(entry);
                    if (_closing) continue;
                    entry.SetIcon(icon);
                }
            }));
        }, TaskScheduler.Default);
    }

    /// <summary>淡入 + 轻微从左往右 6 DIP（150ms）。系统关了动画就完全不播。</summary>
    private void AnimateIn(Border card)
    {
        /* SystemParameters.ClientAreaAnimation 就是 WPF 侧的 prefers-reduced-motion：
         * 用户在"辅助功能 → 视觉效果"里关掉动画时它变 false。 */
        if (!_animateLevels || !SystemParameters.ClientAreaAnimation) return;

        card.Opacity = 0;
        var move = new TranslateTransform(-6, 0);
        card.RenderTransform = move;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        var slide = new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        card.BeginAnimation(OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    // ── 懒加载 ──────────────────────────────────────────────────────

    /// <summary>
    /// 后台读一个目录的**直接子文件夹**，回来后灌进那一行的 Children。
    /// <paramref name="fresh"/> = true 是"重试"路径（绕过会话缓存）。
    /// </summary>
    private void StartFolderLoad(GestureMenuEntry entry, bool fresh)
    {
        if (entry.FolderPath is not { Length: > 0 } folder) return;
        if (entry.LoadState == GestureMenuEntry.FolderLoadState.Loading) return;

        entry.BeginFolderLoad();
        entry.SetError(null, error: false);
        Reflow();

        Dispatcher dispatcher = Dispatcher;
        Task.Run(() => FolderTree.List(folder, fresh)).ContinueWith(task =>
        {
            FolderTree.Listing listing = task.IsCompletedSuccessfully
                ? task.Result
                : FolderTree.Listing.Failed(
                    FolderTree.FailureKind.Other,
                    task.Exception?.GetBaseException().Message ?? "");

            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_closing) return;
                entry.ApplyListing(listing);
                // 新列出来的子文件夹要补真图标（后台，不挡这一拍）。
                QueueIconLoad(entry.Children);

                // 这一级正开着才重排（内容从"正在读取"变成真实行，卡片高度会变）
                if (_levels.Any(l => ReferenceEquals(l.Owner, entry))) Reflow();
            }));
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 给一批条目做"是不是文件夹"的后台探测（一级菜单的固定文件夹、静态子项）。
    ///
    /// 为什么不在构造条目时同步 <c>Directory.Exists</c>：网络路径上这一句能卡几秒，
    /// 而它跑在"菜单弹出"这条 UI 路径上 —— 用户看到的就是"中键按下去半天没反应"。
    /// </summary>
    private void ProbeEntries(IEnumerable<GestureMenuEntry> entries)
    {
        var pending = new List<GestureMenuEntry>();
        foreach (GestureMenuEntry entry in entries)
        {
            if (entry.Probed) continue;
            entry.Probed = true;
            if (entry.Kind != GestureMenuEntry.EntryKind.Action) continue;
            if (!LooksLikeFolderCandidate(entry.Item)) continue;
            pending.Add(entry);
        }
        if (pending.Count == 0) return;

        Dispatcher dispatcher = Dispatcher;
        Task.Run(() =>
        {
            var rows = new List<(GestureMenuEntry Entry, bool IsFolder, bool? HasSub)>(pending.Count);
            foreach (GestureMenuEntry entry in pending)
            {
                string path = FolderOpen.Normalize(entry.Item.Arg);
                bool isFolder = false;
                try
                {
                    isFolder = path.Length > 0 && Directory.Exists(FolderTree.ForIo(path));
                }
                catch { isFolder = false; }

                bool? hasSub = isFolder ? FolderTree.ProbeHasSubfolders(path) : null;
                rows.Add((entry, isFolder, hasSub));
            }
            return rows;
        }).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_closing) return;
                bool changed = false;
                foreach ((GestureMenuEntry entry, bool isFolder, bool? hasSub) in task.Result)
                {
                    if (!isFolder) continue;
                    entry.Kind = GestureMenuEntry.EntryKind.Folder;
                    entry.FolderPath = FolderOpen.Normalize(entry.Item.Arg);
                    entry.HasSubfolders = hasSub;
                    changed = true;
                }
                if (changed)
                {
                    // 刚被认成文件夹的行：图标可能还没取到（网络/冷缓存），后台补一次。
                    QueueIconLoad(task.Result.Where(r => r.IsFolder).Select(r => r.Entry));
                    if (_levels.Count > 1) Reflow();
                }
            }));
        }, TaskScheduler.Default);
    }

    /// <summary>看着像路径、而且不是网址 / shell: 命名空间（只是粗筛，真伪由后台探测定）。</summary>
    private static bool LooksLikeFolderCandidate(InputPlus.Core.Scripting.GestureMenuItem item)
    {
        if (!string.Equals(item.Action, "openFile", StringComparison.OrdinalIgnoreCase)) return false;

        string arg = (item.Arg ?? "").Trim();
        if (arg.Length == 0) return false;
        if (arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        if (arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (arg.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return false;

        if (arg.Length >= 3 && char.IsLetter(arg[0]) && arg[1] == ':' && (arg[2] == '\\' || arg[2] == '/'))
            return true;
        if (arg.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return arg.Contains('\\') || arg.Contains('/');
    }

    // ── 悬停 ────────────────────────────────────────────────────────

    private void StartHoverPump()
    {
        if (_hoverPumpRunning) return;
        _hoverPumpRunning = true;
        _hoverPump.Interval = TimeSpan.FromMilliseconds(40);
        _hoverPump.Tick += OnHoverTick;
        _hoverPump.Start();
    }

    private void StopHoverPump()
    {
        if (!_hoverPumpRunning) return;
        _hoverPumpRunning = false;
        try
        {
            _hoverPump.Stop();
            _hoverPump.Tick -= OnHoverTick;
        }
        catch { /* 窗口都关了 */ }
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        try { EvaluateHover(); }
        catch (Exception ex) { InputPlus.Core.CoreLog.Swallow(ex, "附加菜单：悬停判定"); }
    }

    /// <summary>一次悬停判定（每 40ms 一次）。</summary>
    private void EvaluateHover()
    {
        if (!_hoverEnabled || !_ready || _closing || _committed || !IsVisible) return;
        if (_levels.Count == 0) return;

        long now = Environment.TickCount64;
        if (now < _settleUntil) return;

        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return;

        /* 鼠标动过 → 退出键盘模式（之后按鼠标的规则走）。 */
        if (_hasLastCursor && (cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y))
            _keyboardMode = false;
        _lastCursor = cursor;
        _hasLastCursor = true;

        if (_keyboardMode) return;

        MenuLevel? hit = null;
        foreach (MenuLevel level in _levels)
        {
            if (ContainsScreenPoint(level.Card, cursor)) { hit = level; break; }
        }

        if (hit == null)
        {
            /* 离开整条链（父面板 + 全部子面板）：250ms 后收起所有子级，
             * 一级菜单保留 —— 它本身就是这次右键菜单。 */
            _hoverEntry = null;
            ScheduleCloseAll(now);
            FlushPending(now);
            return;
        }

        if (!TryEntryAt(hit, cursor, out ListBoxItem? row, out GestureMenuEntry? entry))
        {
            /* 卡片空白处或滚动条：**什么都不做**。
             * 用户要求"滚动条不影响收合判定"——在这里收合，边滚边关，没法用。 */
            _hoverEntry = null;
            CancelPending();
            return;
        }

        if (entry is null) return;   // TryEntryAt 只在有行时返回 true，这里只是让编译器也满意

        if (entry.CanExpand)
        {
            CancelPending();

            if (ReferenceEquals(_hoverEntry, entry) && _hoverLevel == hit.Index)
            {
                if (now - _hoverSince >= HoverOpenDelayMs)
                    OpenChildLevel(hit, entry, focusChild: false);
            }
            else
            {
                _hoverEntry = entry;
                _hoverLevel = hit.Index;
                _hoverSince = now;
            }
            return;
        }

        /* 指到一行**不展开**的条目上：同级切换 / 叶子。
         * 250ms 后把它下面那几级收掉，祖先链保持不变（用户要求 A 的最后一条）。 */
        _hoverEntry = entry;
        _hoverLevel = hit.Index;
        _hoverSince = now;
        ScheduleCollapseAbove(hit.Index, now);
        FlushPending(now);
    }

    private void ScheduleCloseAll(long now)
    {
        if (_pending == PendingAction.CloseAll) return;
        _pending = PendingAction.CloseAll;
        _pendingAt = now;
        _pendingLevel = 0;
    }

    private void ScheduleCollapseAbove(int level, long now)
    {
        if (level + 1 >= _levels.Count) { CancelPending(); return; }
        if (_pending == PendingAction.CollapseAbove && _pendingLevel == level) return;
        _pending = PendingAction.CollapseAbove;
        _pendingAt = now;
        _pendingLevel = level;
    }

    private void CancelPending()
    {
        _pending = PendingAction.None;
        _pendingLevel = 0;
    }

    /// <summary>计时到点就执行（悬停轮询里每拍调一次）。</summary>
    private void FlushPending(long now)
    {
        if (_pending == PendingAction.None) return;
        if (now - _pendingAt < HoverCloseDelayMs) return;

        PendingAction action = _pending;
        int level = _pendingLevel;
        CancelPending();

        if (action == PendingAction.CloseAll) CloseLevelsAbove(0);
        else CloseLevelsAbove(level);
    }

    /// <summary>光标（物理像素）是不是落在这张卡片上。</summary>
    private static bool ContainsScreenPoint(FrameworkElement element, NativeMethods.POINT screen)
    {
        Point local = ToLocal(element, screen);
        return local.X >= 0 && local.Y >= 0
               && local.X <= element.ActualWidth && local.Y <= element.ActualHeight;
    }

    /// <summary>光标下面是不是某一行；不是（空白/滚动条）返回 false。</summary>
    private static bool TryEntryAt(MenuLevel level, NativeMethods.POINT screen,
        out ListBoxItem? row, out GestureMenuEntry? entry)
    {
        row = null;
        entry = null;

        Point local = ToLocal(level.List, screen);
        if (local.X < 0 || local.Y < 0 ||
            local.X > level.List.ActualWidth || local.Y > level.List.ActualHeight)
            return false;

        object? hit = level.List.InputHitTest(local);
        if (hit is not DependencyObject element) return false;

        row = ItemsControl.ContainerFromElement(level.List, element) as ListBoxItem;
        if (row?.DataContext is GestureMenuEntry found)
        {
            entry = found;
            return true;
        }
        return false;
    }

    /// <summary>屏幕物理像素 → 元素本地 DIP（只用来做命中，不参与布局）。</summary>
    private static Point ToLocal(FrameworkElement element, NativeMethods.POINT screen)
    {
        Point origin = element.PointToScreen(new Point(0, 0));
        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
        return new Point((screen.X - origin.X) / sx, (screen.Y - origin.Y) / sy);
    }

    // ── 布局 ────────────────────────────────────────────────────────

    /// <summary>
    /// 重排整条链：先量、再逐级定位、最后把窗口夹回工作区。
    ///
    /// 为什么要量两次：新卡片加进面板时还没量过（DesiredSize 是 0），
    /// 得先让 WPF 走一遍 Measure 才拿得到真实高度 —— 而高度决定它要不要上翻。
    /// </summary>
    private void Reflow()
    {
        if (_levels.Count == 0 || CascadeHost == null || !_ready) return;

        CascadeHost.UpdateLayout();

        Rect work = WorkAreaInWindow();
        double listMax = Math.Max(140, work.Height - 44);
        double minTop = work.Top + 2;

        MenuLevel root = _levels[0];
        root.VisibleX = 14;
        root.VisibleY = 14;
        CascadeHost.SetRect(root.Card, new Rect(0, 0, 0, 0));
        root.List.MaxHeight = listMax;

        for (int i = 1; i < _levels.Count; i++)
        {
            MenuLevel prev = _levels[i - 1];
            MenuLevel cur = _levels[i];
            cur.List.MaxHeight = listMax;

            double rowTop = RowTopInCard(prev, cur.Owner);
            double cardHeight = Math.Max(24,
                cur.Card.DesiredSize.Height - cur.Card.Margin.Top - cur.Card.Margin.Bottom);

            double vx = prev.VisibleX + (prev.Card.ActualWidth > 0 ? prev.Card.ActualWidth : CardWidth);
            double vy = prev.VisibleY + rowTop;

            /* 超出视口底部就往上翻对齐，超出顶部就往下压回工作区（用户需求 C）。
             * 每张卡片自己的 ListBox 有 MaxHeight + 内部滚动，所以"太高"由它自己消化。 */
            double maxTop = Math.Max(minTop, work.Bottom - cardHeight - 2);
            if (vy > maxTop) vy = maxTop;
            if (vy < minTop) vy = minTop;

            cur.VisibleX = vx;
            cur.VisibleY = vy;
            cur.Outer = new Rect(vx - cur.Card.Margin.Left, vy - cur.Card.Margin.Top, 0, 0);
            CascadeHost.SetRect(cur.Card, cur.Outer);
        }

        CascadeHost.UpdateLayout();
        ClampWindow();
        ScrollDeepestIntoView();

        SubPlacementForTest = _levels.Count <= 1
            ? $"层级={_levels.Count}"
            : $"层级={_levels.Count} 二级={_levels[1].Card.ActualWidth:0.#}x{_levels[1].Card.ActualHeight:0.#} "
              + $"top={_levels[1].MarginTopForTest:0.#} 窗口右沿={Left + ActualWidth:0.#}";
    }

    /// <summary>父卡片里，打开下一级的那一行距离卡片顶部的 DIP（会自动夹进卡片范围）。</summary>
    private static double RowTopInCard(MenuLevel level, GestureMenuEntry? owner)
    {
        if (owner == null) return 0;
        try
        {
            if (level.List.ItemContainerGenerator.ContainerFromItem(owner) is not ListBoxItem row)
                return 0;

            Point point = row.TranslatePoint(new Point(0, 0), level.Card);
            double max = Math.Max(0, level.Card.ActualHeight - row.ActualHeight);
            return Math.Min(Math.Max(0, point.Y), max);
        }
        catch { return 0; }
    }

    /// <summary>工作区换算到"窗口坐标"（卡片的 VisibleX/Y 用的就是这套坐标系）。</summary>
    private Rect WorkAreaInWindow()
    {
        if (_workArea.IsEmpty)
        {
            Rect fallback = SystemParameters.WorkArea;
            return new Rect(0, 0, fallback.Width, fallback.Height);
        }

        double left = double.IsNaN(Left) ? 0 : Left;
        double top = double.IsNaN(Top) ? 0 : Top;
        return new Rect(_workArea.X - left, _workArea.Y - top, _workArea.Width, _workArea.Height);
    }

    /// <summary>按锚点摆窗口，再夹进工作区（右边/下边放不下就往左/上让）。</summary>
    private void ClampWindow()
    {
        if (_workArea.IsEmpty) return;

        double width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        double height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;

        double maxX = Math.Max(_workArea.Left, _workArea.Right - width);
        double maxY = Math.Max(_workArea.Top, _workArea.Bottom - height);
        double x = Math.Min(Math.Max(_anchor.X, _workArea.Left), maxX);
        double y = Math.Min(Math.Max(_anchor.Y, _workArea.Top), maxY);

        bool moved = double.IsNaN(Left) || double.IsNaN(Top)
                     || Math.Abs(Left - x) > 0.5 || Math.Abs(Top - y) > 0.5;

        Left = x;
        Top = y;

        if (!moved) return;

        /* 窗口挪了：鼠标底下的东西换了。先停一拍悬停判定，
         * 否则"多出一级 → 窗口往左让 → 鼠标压到别的行 → 又展开"会连环触发。 */
        _settleUntil = Environment.TickCount64 + 220;
        _hoverEntry = null;
        CancelPending();
    }

    /// <summary>
    /// 把窗口钉在光标旁边 —— 左上是**光标那一点**，只留 2 DIP 的缝。
    ///
    /// 坐标全部走"物理像素 → 本窗口所在显示器的 DIP"这一条路：
    /// <c>Left/Top</c> 的坐标系是该显示器 DPI 下的 DIP，而
    /// <c>SystemParameters.WorkArea</c> 是**系统 DPI** 下的 DIP ——
    /// 多屏且缩放不同时两者差一倍，直接拿 WorkArea 夹光标会跑到屏幕外，
    /// 所以工作区也从 <c>GetMonitorInfo</c> 取物理像素再换算。
    /// </summary>
    private void PlaceAtCursor()
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return;

        IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        Matrix deviceToDip = DeviceToDip();

        Point at = deviceToDip.Transform(new Point(cursor.X, cursor.Y));
        Point workTopLeft = deviceToDip.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
        Point workBottomRight = deviceToDip.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));

        _workArea = new Rect(workTopLeft, new Size(
            Math.Max(0, workBottomRight.X - workTopLeft.X),
            Math.Max(0, workBottomRight.Y - workTopLeft.Y)));

        // 先按工作区把"能长多大"钉死：横向放不下走滚动，而不是把卡片挤出屏幕。
        ApplyWorkAreaLimits();

        double width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        double height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
        bool sized = width > 0 && height > 0;

        /* 左上加 2px：正好压在光标上会盖住指针，看着像菜单"卡"在鼠标下面；
         * 2px 又小到肉眼感觉不出偏移，位置就是"鼠标那里"。 */
        const double anchorOffset = 2;
        double x = at.X + anchorOffset;
        double y = at.Y + anchorOffset;
        if (sized)
        {
            if (x + width > _workArea.Right) x = at.X - width - anchorOffset;
            if (y + height > _workArea.Bottom) y = at.Y - height - anchorOffset;
        }

        // 最后夹进工作区：宁可压住光标，也不能有半边在屏幕外
        double maxX = _workArea.Left + Math.Max(0, _workArea.Width - (sized ? width : 0));
        double maxY = _workArea.Top + Math.Max(0, _workArea.Height - (sized ? height : 0));
        _anchor = new Point(
            Math.Min(Math.Max(x, _workArea.Left), maxX),
            Math.Min(Math.Max(y, _workArea.Top), maxY));

        ClampWindow();
    }

    /// <summary>窗口永远不超过工作区：横向交给 CascadeScroll 的滚动条。</summary>
    private void ApplyWorkAreaLimits()
    {
        if (_workArea.IsEmpty) return;
        CascadeScroll.MaxWidth = Math.Max(240, _workArea.Width - 8);
        CascadeScroll.MaxHeight = Math.Max(180, _workArea.Height - 8);
    }

    /// <summary>横向放不下时把最新一级滚进视野（用户不用自己去拖那条 4 DIP 的滚动条）。</summary>
    private void ScrollDeepestIntoView()
    {
        if (CascadeScroll.ScrollableWidth <= 0) return;

        MenuLevel last = _levels[^1];
        double need = last.VisibleX + (last.Card.ActualWidth > 0 ? last.Card.ActualWidth : CardWidth)
                      + 14 - CascadeScroll.ViewportWidth;
        if (need <= 0) return;

        CascadeScroll.ScrollToHorizontalOffset(
            Math.Min(Math.Max(need, 0), CascadeScroll.ScrollableWidth));
    }

    // ── 键盘导航（层级间）──────────────────────────────────────────

    private MenuLevel FocusedLevel()
    {
        foreach (MenuLevel level in _levels)
            if (level.List.IsKeyboardFocusWithin) return level;
        return _levels[^1];
    }

    private void FocusLevel(MenuLevel level)
    {
        if (level.List.Items.Count > 0 && level.List.SelectedIndex < 0)
            level.List.SelectedIndex = 0;

        level.List.Focus();
        Keyboard.Focus(level.List);

        if (level.List.SelectedItem != null) level.List.ScrollIntoView(level.List.SelectedItem);
    }

    private void MoveLevelFocus(int delta)
    {
        if (_levels.Count == 0) return;
        int current = FocusedLevel().Index;
        int target = Math.Clamp(current + delta, 0, _levels.Count - 1);
        if (target == current) return;

        _keyboardMode = true;
        FocusLevel(_levels[target]);
    }

    private void MoveSelection(int delta)
    {
        MenuLevel level = FocusedLevel();
        int count = level.List.Items.Count;
        if (count == 0) return;

        int index = level.List.SelectedIndex;
        index = index < 0 ? 0 : (index + delta + count) % count;
        level.List.SelectedIndex = index;
        level.List.ScrollIntoView(level.List.Items[index]);
    }

    private void ExpandFocused()
    {
        MenuLevel level = FocusedLevel();
        if (level.List.SelectedItem is GestureMenuEntry entry && entry.CanExpand)
            OpenChildLevel(level, entry, focusChild: true);
    }

    private void ActivateFocused()
    {
        MenuLevel level = FocusedLevel();
        if (level.List.SelectedItem is GestureMenuEntry entry)
        {
            Activate(level, entry);
            return;
        }
        if (level.List.Items.Count > 0) level.List.SelectedIndex = 0;
    }

    private MenuLevel? LevelOf(ListBox list)
    {
        foreach (MenuLevel level in _levels)
            if (ReferenceEquals(level.List, list)) return level;
        return null;
    }

    // ── 自检 / 离屏渲染的入口 ──────────────────────────────────────

    /// <summary>自检/诊断用：上一次展开时的尺寸与位置。</summary>
    internal string SubPlacementForTest { get; private set; } = "";

    /// <summary>渲染 / 自检用：一级卡片。</summary>
    internal FrameworkElement CardForTest => MainCard;

    /// <summary>渲染 / 自检用：一级列表（要看真实行高）。</summary>
    internal ListBox ItemListForTest => ItemList;

    /// <summary>渲染 / 自检用：第一级的子级列表（没展开时回落到一级）。</summary>
    internal ListBox SubListForTest => _levels.Count > 1 ? _levels[1].List : ItemList;

    /// <summary>渲染 / 自检用：第一级的子级卡片（没展开时回落到一级）。</summary>
    internal FrameworkElement SubCardForTest => _levels.Count > 1 ? _levels[1].Card : MainCard;

    /// <summary>自检用：整条链现在有几级。</summary>
    internal int LevelCountForTest => _levels.Count;

    /// <summary>自检用：第 <paramref name="index"/> 级的列表。</summary>
    internal ListBox LevelListForTest(int index) => _levels[index].List;

    /// <summary>自检用：第 <paramref name="index"/> 级的卡片。</summary>
    internal FrameworkElement LevelCardForTest(int index) => _levels[index].Card;

    /// <summary>自检用：第 <paramref name="level"/> 级第 <paramref name="index"/>"/> 行。</summary>
    internal GestureMenuEntry? EntryForTest(int level, int index)
        => level >= 0 && level < _levels.Count && index >= 0 && index < _levels[level].List.Items.Count
            ? _levels[level].List.Items[index] as GestureMenuEntry
            : null;

    /// <summary>自检用：关掉悬停轮询（真实光标会让断言飘）。</summary>
    internal bool HoverEnabledForTest
    {
        get => _hoverEnabled;
        set
        {
            _hoverEnabled = value;
            if (!value) CancelPending();
        }
    }

    /// <summary>自检 / 出图用：关掉每级展开动画（量位置时要的是静止值）。</summary>
    internal bool AnimationsEnabledForTest
    {
        get => _animateLevels;
        set => _animateLevels = value;
    }

    /// <summary>
    /// 自检 / 出图用：同步展开某一级的某一行，返回新一级的下标（-1 = 没能展开）。
    /// 文件夹会**同步**枚举（不经过后台线程），这样断言与截图都是确定的。
    /// </summary>
    internal int ExpandForTest(int levelIndex, int index)
    {
        if (levelIndex < 0 || levelIndex >= _levels.Count) return -1;
        MenuLevel level = _levels[levelIndex];
        if (index < 0 || index >= level.List.Items.Count) return -1;
        if (level.List.Items[index] is not GestureMenuEntry entry) return -1;

        level.List.UpdateLayout();

        /* 测试里的行可能还没等到后台探测（"这一条是不是文件夹"）。
         * 这里同步补一次，出图与断言才是确定的（只碰一次磁盘，走的是测试路径）。 */
        if (entry.Kind == GestureMenuEntry.EntryKind.Action && LooksLikeFolderCandidate(entry.Item))
        {
            string path = FolderOpen.Normalize(entry.Item.Arg);
            if (path.Length > 0 && Directory.Exists(FolderTree.ForIo(path)))
            {
                entry.Kind = GestureMenuEntry.EntryKind.Folder;
                entry.FolderPath = path;
                entry.HasSubfolders = FolderTree.ProbeHasSubfolders(path);
            }
        }

        if (entry.Kind == GestureMenuEntry.EntryKind.Folder
            && entry.CanExpand
            && entry.LoadState == GestureMenuEntry.FolderLoadState.None
            && entry.FolderPath is { Length: > 0 } folder)
        {
            entry.BeginFolderLoad();
            entry.ApplyListing(FolderTree.List(folder, false));
            QueueIconLoad(entry.Children);
        }

        MenuLevel? child = OpenChildLevel(level, entry, focusChild: false);
        UpdateLayout();
        return child?.Index ?? -1;
    }

    /// <summary>自检用：强制重读某一行的目录（走真实的异步路径，用来验 loading / 错误 / 重试）。</summary>
    internal void ReloadFolderForTest(int level, int index)
    {
        if (level < 0 || level >= _levels.Count) return;
        if (index < 0 || index >= _levels[level].List.Items.Count) return;
        if (_levels[level].List.Items[index] is GestureMenuEntry entry &&
            entry.Kind == GestureMenuEntry.EntryKind.Folder)
            StartFolderLoad(entry, fresh: true);
    }

    /// <summary>自检用：等价于点了一下某一行（走和真实点击同一条 <see cref="Activate"/>）。</summary>
    internal void ClickForTest(int level, int index)
    {
        if (level < 0 || level >= _levels.Count) return;
        if (index < 0 || index >= _levels[level].List.Items.Count) return;
        if (_levels[level].List.Items[index] is GestureMenuEntry entry) Activate(_levels[level], entry);
    }

    /// <summary>自检用：这条链上是不是已经打开过这个目录（防循环判定的本体）。</summary>
    internal bool WouldCycleForTest(int level, string folderPath)
        => level >= 0 && level < _levels.Count && ChainContainsPath(_levels[level], folderPath);

    /// <summary>自检用：等价于按 Esc（收起整条展开链，一级菜单留着）。</summary>
    internal void CollapseChainForTest() => CloseLevelsAbove(0);

    /// <summary>渲染 / 自检用：按序号展开一级菜单某一项的下一级，返回那张卡片。</summary>
    internal FrameworkElement? OpenSubMenuForRender(int index)
    {
        int child = ExpandForTest(0, index);
        return child >= 0 && child < _levels.Count ? _levels[child].Card : null;
    }

    /// <summary>渲染 / 自检用：展开固定文件夹的第一页（同步版，出图与断言都靠它）。</summary>
    internal FrameworkElement? OpenFolderPeekForRender(int index) => OpenSubMenuForRender(index);

    /// <summary>一级卡片在面板里的父级定位（自检里读 Margin.Top 的替代）。</summary>
    private sealed class MenuLevel
    {
        internal MenuLevel(int index, Border card, ListBox list, GestureMenuEntry? owner)
        {
            Index = index;
            Card = card;
            List = list;
            Owner = owner;
        }

        internal int Index { get; }
        internal Border Card { get; }
        internal ListBox List { get; }
        internal GestureMenuEntry? Owner { get; }

        internal double VisibleX;
        internal double VisibleY;
        internal Rect Outer;

        /// <summary>自检拼描述串用：卡片相对窗口顶部的位置。</summary>
        internal double MarginTopForTest => Outer.Y + Card.Margin.Top;
    }
}
