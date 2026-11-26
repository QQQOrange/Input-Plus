using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using InputPlus.Core.Actions;
using InputPlus.Core.Localization;
using InputPlus.Core.Scripting;

namespace InputPlus.UI.Windows;

/// <summary>
/// 菜单的弹出菜单（v1.5.0；v1.6.7 起支持**任意层级**的横向下钻）。
///
/// ── 为什么是独立 Window，而不是 ContextMenu / Popup ──────────────
/// · ContextMenu 自己管键盘（Esc/方向键/回车都在它手里），做不了"1..9 快选"，
///   也没法让第一条默认带着选中态；
/// · Popup 挂在别的窗口上，宿主窗口一动它就跟着跑，而且没有自己的激活状态，
///   "点别处就收"得靠全局钩子。
/// 独立窗口 + Topmost + Deactivated 关闭 = 和系统右键菜单同一套手感，
/// 并且键盘焦点天然在这个窗口里。
///
/// ── 交互 ────────────────────────────────────────────────────────
/// · 鼠标点动作条目 → 立刻执行并关掉；
/// · 鼠标点**文件夹**（任何一级）→ 在资源管理器里打开它，菜单随即关闭
///   （跳转优先于展开：悬停已经把它展开了也照样跳转）；
/// · 鼠标悬停在文件夹上 → 右侧展开它的直接子文件夹，可以一直往下；
/// · 点行尾的 ▸ 按钮 → 只展开（触屏没有 hover，这是它的下钻入口）；
/// · 1..9（含小键盘）→ 直接执行一级菜单第 n 条；
/// · ↑/↓ 换选中，→ 展开、← 收起、Enter 打开、Tab 在层级间移动、Esc 收起整条链；
/// · Deactivated / 点别处 → 整个菜单关掉（子面板一并销毁）。
///
/// ── 位置 ────────────────────────────────────────────────────────
/// 弹出即钉在光标处，没有标题栏、不可拖动、不可缩放；随后只在"会超出工作区"时
/// 才被夹回来（不夹的话菜单会有半边看不见）。横向放不下时走滚动，见 CascadePanel。
/// </summary>
public partial class GestureMenuWindow : Window
{
    /// <summary>一级卡片里的一行一行。</summary>
    private readonly List<GestureMenuEntry> _entries;

    /// <summary>已经决定过结果：之后的 Deactivated / 重复敲回车都不再改 Selected。</summary>
    private bool _committed;

    /// <summary>第一次排版完成之前收到的 Deactivated 一律忽略（见 <see cref="OnDeactivated"/>）。</summary>
    private bool _ready;

    /// <summary>
    /// 已经在关窗流程里了。
    ///
    /// 为什么必须有：<c>Close()</c> 内部会走一次激活切换 —— WPF 在关闭过程中又收到
    /// <c>WmActivate(WA_INACTIVE)</c>，于是 <see cref="OnDeactivated"/> 再调一次 <c>Close()</c>，
    /// 直接抛 <c>InvalidOperationException: 在窗口关闭期间…</c>，异常冒到界面线程
    /// （日志里的"界面线程异常"）。后果不只是多一条日志：窗口可能在"已关/未关"之间
    /// 卡住，<c>Closed</c> 不再触发，调用方的 <c>_middleMenu</c> 就永远是旧值 ——
    /// **附加菜单此后再也弹不出来**（用户 2026-11-22 报的"中键菜单消失了"）。
    /// </summary>
    private bool _closing;

    /// <summary>菜单真正上屏的时刻（tick）。刚弹出那 400ms 内不认 Deactivated，见 <see cref="OnDeactivated"/>。</summary>
    private long _shownAt;

    /// <summary>这次弹菜单时抓到的"用户在看什么"；null = 不显示收藏星标（手势菜单）。</summary>
    private readonly Core.Qbar.FavoriteTarget? _favorite;

    /// <param name="menu">要弹出的菜单。</param>
    /// <param name="actionTitleOf">动作 id → 中文标题；条目自己没写 Title 时用它兜底。</param>
    /// <param name="favorite">
    /// 可选中键菜单的上下文快照。非 null 时右上角出现收藏星标，
    /// 点它就把"当前网页 / 当前文件夹 / 当前程序"变成一条 Qbar 固定项。
    /// </param>
    public GestureMenuWindow(GestureMenuDef menu, Func<string, string?> actionTitleOf,
        Core.Qbar.FavoriteTarget? favorite = null)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(actionTitleOf);

        InitializeComponent();

        _favorite = favorite;
        if (favorite != null)
        {
            FavoriteButton.Visibility = Visibility.Visible;
            FavoriteButton.ToolTip = Loc.T("收藏当前位置到 Qbar");
        }

        /* 附加菜单弹出时**什么标题都不写**（用户要求）：整块标题行连同下面那条分隔线一起收掉。 */
        MenuTitleText.Text = menu.DisplayName;
        TitleRow.Visibility = menu.HideTitle ? Visibility.Collapsed : Visibility.Visible;
        TitleDivider.Visibility = menu.HideTitle ? Visibility.Collapsed : Visibility.Visible;

        /* 没有标题时，这条 HeaderRow 里只剩右上角那颗星标。
         * 它原来还带着 4 DIP 的外边距和 24 DIP 的按钮高度，于是菜单最上面
         * 白白多出一条 ~34 DIP 的空白带（比一行还高，用户 2026-11-23 报的
         * "收藏按钮上面有非常大的空隙"）。这里把外边距收掉，星标 18×18，
         * 顶部只留卡片自己的 4 DIP 内边距 —— 星标紧贴右上角，菜单也短一截。 */
        if (menu.HideTitle) HeaderRow.Margin = new Thickness(0);

        string hint = (menu.Hint ?? "").Trim();
        MenuHintText.Text = hint;
        MenuHintText.Visibility = !menu.HideTitle && hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 最多 12 条：再多的部分根本按不到，直接截断
        int count = Math.Min(menu.Items.Count, GestureMenuDef.MaxItems);
        _entries = new List<GestureMenuEntry>(count);
        for (int i = 0; i < count; i++)
        {
            GestureMenuItem item = menu.Items[i];
            _entries.Add(new GestureMenuEntry(item, i, ResolveTitle(item, actionTitleOf)));
        }

        ItemList.ItemsSource = _entries;

        bool empty = _entries.Count == 0;
        ItemList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        // 一级卡片进面板；顺手给每一行标上层级、把"是不是文件夹"的探测排到后台。
        SetupBaseLevel();

        Deactivated += OnDeactivated;

        // 先把 HWND 建出来：没有它就没有 PresentationSource，
        // 也就拿不到"物理像素 → DIP"的换算矩阵（和 QbarWindow 一个做法）。
        new WindowInteropHelper(this).EnsureHandle();

        // 先摆一次（这时还没排版，只知道光标在哪，估个位置），
        // Loaded 里拿到真实尺寸后再精确夹一次。
        PlaceAtCursor();
    }

    /// <summary>用户点中的那条；Esc / 点别处收起来时为 null。</summary>
    public GestureMenuItem? Selected { get; private set; }

    /// <summary>
    /// 用户点的是右上角的收藏星标（此时 <see cref="Selected"/> 为 null）。
    ///
    /// 为什么不在这里直接弹收藏对话框：那个对话框要读地址栏/目录，属于"收藏"这条链路的
    /// 事；菜单窗只管"用户点了什么"，执行留给 <see cref="GestureService"/> —— 和条目
    /// 选中后交给 <c>InvokeAction</c> 是同一个分工。
    /// </summary>
    public bool FavoriteRequested { get; private set; }

    /// <summary>
    /// 用户点了一个文件夹：菜单**自己**把资源管理器打开了（路径归一化、明确报错都在窗里做），
    /// 调用方不要再走一遍 <c>openFile</c> 动作 —— 否则会开两个窗口。
    /// </summary>
    public bool FolderOpenRequested { get; private set; }

    /// <summary>弹菜单时抓到的上下文快照（没有星标时为 null）。</summary>
    public Core.Qbar.FavoriteTarget? Favorite => _favorite;

    /// <summary>
    /// 预热（或点击后的识别）回来了：把"当前文件叫什么 / 是不是已经在收藏里 /
    /// 到底有没有文件"补到顶部两颗入口上。菜单已经显示着，所以走 INotifyPropertyChanged
    /// 原地改，不重建 ItemsSource（重建会让当前选中项跳回第一行）。
    ///
    /// <paramref name="hasFile"/> 是这一版（v1.5）新增的**唯一**依据：
    /// 没有文件时两个入口一起置灰 —— 以前只判"前台不是桌面/资源管理器"，
    /// 于是 Electron / 浏览器（DeepSeekRuntime、Edge…）里永远是亮的，
    /// 点下去识别不到，就弹出一个"选择要定位的文件"，把用户送到一个
    /// 和他刚才在做的事毫无关系的地方。
    /// </summary>
    public void MarkForegroundFile(string fileName, bool favorited, bool hasFile)
    {
        foreach (GestureMenuEntry entry in _entries)
        {
            string action = entry.Item.Action ?? "";
            if (string.Equals(action, "favoriteForegroundFile", StringComparison.OrdinalIgnoreCase))
            {
                entry.UpdateFavoriteGlyph(favorited);
                entry.SetEnabled(hasFile);
                entry.UpdateNote(hasFile
                    ? (favorited ? Loc.T("已在收藏中") : fileName)
                    : Loc.T("前台没有打开文件"));
            }
            else if (string.Equals(action, "revealForegroundFile", StringComparison.OrdinalIgnoreCase))
            {
                entry.SetEnabled(hasFile);
                entry.UpdateNote(hasFile ? fileName : Loc.T("前台没有打开文件"));
            }
        }
    }

    /// <summary>
    /// 在鼠标位置弹出菜单，返回用户点中的条目（关掉没选就是 null）。
    /// </summary>
    /// <param name="menu">要弹出的菜单。</param>
    /// <param name="actionTitleOf">动作 id → 中文标题，给没写 Title 的条目兜底。</param>
    /// <param name="owner">可选宿主窗口，有就跟着它走（模态）。</param>
    /// <param name="favorite">可选：中键菜单的上下文快照（有它才显示收藏星标）。</param>
    public static GestureMenuItem? Pick(GestureMenuDef menu, Func<string, string?> actionTitleOf,
        Window? owner = null, Core.Qbar.FavoriteTarget? favorite = null)
    {
        var window = new GestureMenuWindow(menu, actionTitleOf, favorite);
        if (owner != null) window.Owner = owner;
        window.ShowDialog();
        return window.Selected;
    }

    // ── 定位 ────────────────────────────────────────────────────────

    /// <summary>物理像素 → 本窗口当前所在显示器 DPI 下的 DIP。</summary>
    private Matrix DeviceToDip()
    {
        if (PresentationSource.FromVisual(this) == null) return Matrix.Identity;
        /* 直接用 DPI 缩放拼矩阵：CompositionTarget.TransformFromDevice 在不同
         * WPF 版本里既能是 MatrixTransform 也见过直接给 Matrix，写死哪一个都可能编不过。 */
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        if (dpi.DpiScaleX <= 0 || dpi.DpiScaleY <= 0) return Matrix.Identity;
        return new Matrix(1 / dpi.DpiScaleX, 0, 0, 1 / dpi.DpiScaleY, 0, 0);
    }

    // ── 事件 ────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 这时 ActualWidth/Height 才是真尺寸：混 DPI 下按真实尺寸夹一次才准
        PlaceAtCursor();
        _ready = true;
        _shownAt = Environment.TickCount64;

        // 第一条默认选中：回车就能执行，用户不动鼠标也走得通
        if (_entries.Count > 0)
        {
            ItemList.SelectedIndex = 0;
            ItemList.ScrollIntoView(_entries[0]);
        }

        ItemList.Focus();
        Keyboard.Focus(ItemList);

        // 悬停判定走轮询（见 EvaluateHover）：窗口会因为"多了一级"而整体挪位，
        // 靠 MouseEnter/Leave 记账在挪位那一拍必然算错。
        StartHoverPump();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 点别处就收（和系统菜单一致）。
        // 刚弹出来的那一瞬间可能先收到一次 Deactivated（激活还没走完），
        // 那时关掉就成了"菜单一闪就没"，所以等排版完才认。
        if (!_ready || _committed || _closing) return;

        /* 再给 400ms 的宽限：Show() 之后那一次激活切换（无论 ForceForeground 成没成）
         * 都可能先送来一条 WA_INACTIVE；用户真去点别处不可能这么快。
         * 少了这一条，前台抢不到时菜单会"闪一下就没了"，和没弹一样。 */
        if (Environment.TickCount64 - _shownAt < 400) return;
        SafeClose();
    }

    /// <summary>
    /// 关窗，但**绝不重入、绝不把异常漏到界面线程**。
    ///
    /// WPF 的 <c>Close()</c> 会在内部触发激活切换，Deactivated 里再调一次就是
    /// "在窗口关闭期间 Close()" 的 <see cref="InvalidOperationException"/>。
    /// 三条关窗路径（失去激活 / Esc / 选中条目）全部走这里。
    /// </summary>
    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        try { StopHoverPump(); } catch { /* 窗口都没了 */ }
        try { Close(); }
        catch (InvalidOperationException) { /* 已经在关闭流程里：WPF 自己收尾 */ }
        catch (Exception ex) { InputPlus.Core.CoreLog.Swallow(ex, "附加菜单：关闭窗口"); }
    }

    // ── 点击 ────────────────────────────────────────────────────────

    private void OnEntryClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        MenuLevel? level = LevelOf(list);
        if (level == null) return;

        // 只认真的点在条目上的那一下：点在列表空白处不该执行任何东西
        if (RowOf(list, e.OriginalSource) is not { } row || row.DataContext is not GestureMenuEntry entry) return;

        /* 点在箭头按钮上：只展开，不跳转。
         * 触屏没有 hover，这是它唯一的下钻入口（用户需求 F 的"由箭头专门负责展开"）。
         * 必须在 Preview 里吃掉：让 Button 自己收 Click 的话，
         * 同一拍 ListBox 的 PreviewMouseLeftButtonUp 会先把它当成"点行"。 */
        if (IsExpandButton(e.OriginalSource))
        {
            e.Handled = true;
            OpenChildLevel(level, entry, focusChild: true);
            return;
        }

        e.Handled = true;
        Activate(level, entry);
    }

    /// <summary>点在右上角的星标：关掉菜单，把"这次要收藏什么"交回给调用方。</summary>
    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_committed || _favorite == null) return;

        _committed = true;
        Selected = null;
        FavoriteRequested = true;

        try { DialogResult = true; }
        catch (InvalidOperationException) { SafeClose(); }
    }

    /// <summary>
    /// 悬停事件只留个空壳：真正的悬停判定在 <see cref="EvaluateHover"/> 里按 40ms 轮询。
    ///
    /// 为什么不用 MouseEnter/Leave 记账：菜单每多一级就会整体挪位（右边放不下要往左让），
    /// 那一拍鼠标相对卡片的位置已经变了，Enter/Leave 的顺序完全不可靠 ——
    /// 靠它收合会出现"斜着移过去就把子菜单关了"或"鼠标回来了还关着"。
    /// </summary>
    private void OnRowMouseEnter(object sender, MouseEventArgs e) { }

    private void OnRowMouseLeave(object sender, MouseEventArgs e) { }

    // ── 键盘 ────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                /* 用户要求：Esc 收起**整条展开链**（不是一层一层收）。
                 * 链已经收完了再按一次才关菜单。 */
                if (LevelCount > 1) { CloseLevelsAbove(0, moveFocus: true); return; }
                SafeClose();
                return;

            case Key.Enter:
                e.Handled = true;
                ActivateFocused();
                return;

            case Key.Right:
                e.Handled = true;
                ExpandFocused();
                return;

            case Key.Left:
                e.Handled = true;
                if (LevelCount > 1) CloseLevelsAbove(LevelCount - 2, moveFocus: true);
                return;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                return;

            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                return;

            case Key.Tab:
                e.Handled = true;
                MoveLevelFocus(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                return;
        }

        int digit = DigitOf(e.Key);
        if (digit > 0 && digit <= _entries.Count)
        {
            e.Handled = true;
            Activate(_levels[0], _entries[digit - 1]);
        }
    }

    /// <summary>1..9 与小键盘 1..9；别的键返回 0。</summary>
    private static int DigitOf(Key key)
    {
        // Key 里 D0..D9、NumPad0..NumPad9 各自连续，按整数比较比写 18 个 case 清楚
        int code = (int)key;
        if (code >= (int)Key.D1 && code <= (int)Key.D9) return code - (int)Key.D0;
        if (code >= (int)Key.NumPad1 && code <= (int)Key.NumPad9) return code - (int)Key.NumPad0;
        return 0;
    }

    // ── 执行 ────────────────────────────────────────────────────────

    /// <summary>
    /// 回车 / 点击 / 数字键的公共出口。一行到底干什么，全看它的 <see cref="GestureMenuEntry.Kind"/>。
    /// </summary>
    private void Activate(MenuLevel level, GestureMenuEntry entry)
    {
        if (_committed) return;

        switch (entry.Kind)
        {
            case GestureMenuEntry.EntryKind.Loading:
            case GestureMenuEntry.EntryKind.Note:
                return;

            case GestureMenuEntry.EntryKind.Error:
                if (entry.RetryTarget != null) StartFolderLoad(entry.RetryTarget, fresh: true);
                return;

            case GestureMenuEntry.EntryKind.Parent:
                OpenChildLevel(level, entry, focusChild: true);
                return;
        }

        /* 文件夹：**跳转优先于展开**（用户需求 B）。
         * 悬停已经把它展开了也不影响 —— 点行就是打开资源管理器。 */
        if (entry.FolderPath is { Length: > 0 })
        {
            _ = JumpToFolderAsync(entry);
            return;
        }

        /* 没有动作的父项（历史记录/命令里万一出现）：点了只展开，不执行。 */
        if (entry.Children.Count > 0 && string.IsNullOrWhiteSpace(entry.Item.Action))
        {
            OpenChildLevel(level, entry, focusChild: true);
            return;
        }

        /* 置灰的条目不能执行：键盘回车与 1..9 快选都能把它选中，
         * 而这两颗入口在前台没有文件时按下去什么也做不了 ——
         * 执行等于给用户一个"按了没反应"。 */
        if (!entry.Item.Enabled) return;

        _committed = true;
        Selected = entry.Item;

        // ShowDialog 打开时用 DialogResult 收场（调用方拿得到 true）；
        // 非模态打开时设 DialogResult 会抛，只能直接关。
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            SafeClose();
        }
    }

    /// <summary>
    /// 点文件夹 → 在资源管理器里打开，成功才关菜单。
    ///
    /// 为什么**异步**：网络路径的 <c>Directory.Exists</c> 能阻塞好几秒，
    /// 放在 UI 线程上就是"点了以后整个菜单卡死"。这里在后台线程判定 + 启动，
    /// 期间把那一行置灰（<c>SetBusy</c>）防重复点击；失败就恢复并写一行红字，
    /// 菜单不关 —— 用户能立刻看到原因、也能再点一次。
    /// </summary>
    private async System.Threading.Tasks.Task JumpToFolderAsync(GestureMenuEntry entry)
    {
        if (_committed || entry.FolderPath is not { Length: > 0 } folder) return;

        entry.SetBusy(true);
        entry.ClearFailure();
        FolderOpen.LaunchResult result;
        try
        {
            result = await System.Threading.Tasks.Task.Run(() => FolderOpen.TryLaunch(folder));
        }
        catch (Exception ex)
        {
            result = FolderOpen.LaunchResult.Fail(folder, ex.Message);
        }

        if (_closing || _committed) return;

        if (!result.Ok)
        {
            entry.SetBusy(false);
            entry.ShowFailure(result.Error);
            InputPlus.Core.CoreLog.Warn($"[附加菜单] 打开文件夹失败：{result.Error}");
            return;
        }

        _committed = true;
        FolderOpenRequested = true;
        Selected = null;

        try { DialogResult = true; }
        catch (InvalidOperationException) { SafeClose(); }
    }

    // ── 行/卡片查找的小工具 ─────────────────────────────────────────

    /// <summary>
    /// 从命中的元素往上找出它所属的那一行。
    /// 不用 VisualTreeHelper 手写循环：命中的可能是 Run（不是 Visual），
    /// <c>ItemsControl.ContainerFromElement</c> 两种情况都认。
    /// </summary>
    private static ListBoxItem? RowOf(ListBox list, object? source)
        => source is DependencyObject element
            ? ItemsControl.ContainerFromElement(list, element) as ListBoxItem
            : null;

    /// <summary>命中的元素是不是行尾那颗展开箭头。</summary>
    private static bool IsExpandButton(object? source)
    {
        var node = source as DependencyObject;
        int guard = 0;
        while (node != null && guard++ < 12)
        {
            if (node is Button button && Equals(button.Tag, "expand")) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>条目上显示的文字：写了 Title 就用它，否则用动作标题兜底。</summary>
    private static string ResolveTitle(GestureMenuItem item, Func<string, string?> actionTitleOf)
    {
        if (!string.IsNullOrWhiteSpace(item.Title)) return item.Title.Trim();

        if (!string.IsNullOrWhiteSpace(item.Action))
        {
            string? fromAction = actionTitleOf(item.Action);
            if (!string.IsNullOrWhiteSpace(fromAction)) return fromAction!.Trim();
        }

        return Loc.T("（还没选动作）");
    }
}
