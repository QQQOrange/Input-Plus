using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using InputPlus.Core.Icons;
using InputPlus.Core.Interop;
using InputPlus.Core.Scripting;

namespace InputPlus.UI.Windows;

/// <summary>
/// 手势菜单的弹出菜单（v1.5.0）—— 鼠标旁边那一列条目。
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
/// · 鼠标点一条 → 立刻执行并关掉；
/// · 1..9（含小键盘）→ 直接执行第 n 条；
/// · ↑/↓ 换选中（到头绕回），Enter 执行当前选中，Esc / 点别处收起来；
/// · v1.3.17：带二级菜单的条目（Children.Count &gt; 0）**悬停自动展开**，
///   子项同样能点；1..9 仍只作用于一级。
///
/// ── 位置 ────────────────────────────────────────────────────────
/// 弹出即钉在光标处，没有标题栏、不可拖动、不可缩放；随后只在"会超出工作区"时
/// 才被夹回来（不夹的话菜单会有半边看不见）。
/// </summary>
public partial class GestureMenuWindow : Window
{
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

        /* 二级列表滚到底 → 追加下一页（固定文件夹的"先显示 10 个、剩下的滚出来"）。
         * 用 ScrollViewer.ScrollChanged（滚轮/拖动都会发）；handledEventsToo 是因为
         * ListBox 内部会先处理一次。 */
        SubList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnSubListScrollChanged), true);

        bool empty = _entries.Count == 0;
        ItemList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

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
    /// 悬停固定文件夹时**先**取多少条。调用方按「搜索前建议」的条数设置传进来
    /// （<see cref="Core.Gestures.FolderPeek.PageSizeFor"/> 会夹到 10..50）。
    /// </summary>
    public int FolderPageSize { get; set; } = Core.Gestures.FolderPeek.DefaultPageSize;

    /// <summary>当前二级菜单正在展示的"文件夹预览"条目；null = 普通静态子项。</summary>
    private GestureMenuEntry? _peekEntry;

    /// <summary>正在取下一页（滚动会连续触发，别叠着跑）。</summary>
    private bool _peekLoading;

    /// <summary>
    /// 用户点的是右上角的收藏星标（此时 <see cref="Selected"/> 为 null）。
    ///
    /// 为什么不在这里直接弹收藏对话框：那个对话框要读地址栏/目录，属于"收藏"这条链路的
    /// 事；菜单窗只管"用户点了什么"，执行留给 <see cref="GestureService"/> —— 和条目
    /// 选中后交给 <c>InvokeAction</c> 是同一个分工。
    /// </summary>
    public bool FavoriteRequested { get; private set; }

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

    /// <summary>
    /// 把窗口摆到鼠标旁边 —— 左上是**光标那一点**，不再留缝。
    ///
    /// 坐标全部走"物理像素 → 本窗口所在显示器的 DIP"这一条路：
    /// <c>Left/Top</c> 的坐标系是该显示器 DPI 下的 DIP，而
    /// <c>SystemParameters.WorkArea</c> 是**系统 DPI** 下的 DIP ——
    /// 多屏且缩放不同时两者差一倍，直接拿 WorkArea 夹光标会跑到屏幕外，
    /// 所以工作区也从 <c>GetMonitorInfo</c> 取物理像素再换算。
    ///
    /// 只有"右下放不下"时才往左/上翻、最后夹进工作区：用户要的是"就出现在鼠标那里"，
    /// 但半边在屏幕外等于没出现，所以夹紧优先。
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
        double workWidth = workBottomRight.X - workTopLeft.X;
        double workHeight = workBottomRight.Y - workTopLeft.Y;

        double width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        double height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
        bool sized = width > 0 && height > 0;

        /* 左上加 2px：正好压在光标上会盖住指针，看着像菜单"卡"在鼠标下面；
         * 2px 又小到肉眼感觉不出偏移，位置就是"鼠标那里"。 */
        const double anchor = 2;
        double x = at.X + anchor;
        double y = at.Y + anchor;
        if (sized)
        {
            if (x + width > workTopLeft.X + workWidth) x = at.X - width - anchor;
            if (y + height > workTopLeft.Y + workHeight) y = at.Y - height - anchor;
        }

        // 最后夹进工作区：宁可压住光标，也不能有半边在屏幕外
        double maxX = workTopLeft.X + Math.Max(0, workWidth - (sized ? width : 0));
        double maxY = workTopLeft.Y + Math.Max(0, workHeight - (sized ? height : 0));
        Left = Math.Min(Math.Max(x, workTopLeft.X), maxX);
        Top = Math.Min(Math.Max(y, workTopLeft.Y), maxY);
    }

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

    /// <summary>
    /// 从命中的元素往上找出它所属的那一行。
    /// 不用 VisualTreeHelper 手写循环：命中的可能是 Run（不是 Visual），
    /// <c>ItemsControl.ContainerFromElement</c> 两种情况都认。
    /// </summary>
    private ListBoxItem? RowOf(object? source)
        => source is DependencyObject element
            ? ItemsControl.ContainerFromElement(ItemList, element) as ListBoxItem
            : null;

    /// <summary>同上，但只在二级菜单的列表里找。</summary>
    private ListBoxItem? SubRowOf(object? source)
        => source is DependencyObject element
            ? ItemsControl.ContainerFromElement(SubList, element) as ListBoxItem
            : null;

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
        try { Close(); }
        catch (InvalidOperationException) { /* 已经在关闭流程里：WPF 自己收尾 */ }
        catch (Exception ex) { InputPlus.Core.CoreLog.Swallow(ex, "附加菜单：关闭窗口"); }
    }

    private void OnEntryClick(object sender, MouseButtonEventArgs e)
    {
        // 只认真的点在条目上的那一下：点在列表空白处不该执行任何东西
        if (RowOf(e.OriginalSource) is { } row && row.DataContext is GestureMenuEntry entry)
        {
            /* 父项（带二级菜单）被点：不执行任何动作，改为把二级菜单钉开 ——
             * 触屏 / 手抖的人也能用，不必非得悬停。 */
            if (entry.HasChildren)
            {
                e.Handled = true;
                OpenSubMenu(entry, row);
                return;
            }

            e.Handled = true;
            Accept(entry);
        }
    }

    private void OnSubEntryClick(object sender, MouseButtonEventArgs e)
    {
        if (SubRowOf(e.OriginalSource) is { } row && row.DataContext is GestureMenuEntry entry)
        {
            e.Handled = true;
            Accept(entry);
        }
    }

    /// <summary>
    /// 点右上角的星标：关掉菜单，把"这次要收藏什么"交回给调用方。
    ///
    /// 和 <see cref="Accept"/> 共用 <see cref="_committed"/>：点过星标之后，
    /// 之后任何一次"失去激活 / 回车"都不该再改结论。
    /// </summary>
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

    // ── 二级菜单 ────────────────────────────────────────────────────

    /// <summary>
    /// 悬停到带子项的条目上 → 右侧展开。
    /// 悬停到普通条目上 → 收起，避免"上一个子菜单还挂在那儿"。
    /// </summary>
    private void OnRowMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not ListBoxItem row || row.DataContext is not GestureMenuEntry entry) return;

        /* 二级菜单现在和一级共用 MenuRow（行高、悬停、选中一套观感），
         * 于是这个处理器也会收到二级行的 MouseEnter —— 它属于 SubList，
         * 不该触发"换父项/收起"。不加这道闸，鼠标一进二级菜单就把它自己关了。 */
        if (!ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), ItemList)) return;

        if (entry.HasChildren) OpenSubMenu(entry, row);
        else if (entry.FolderPath is { Length: > 0 }) OpenFolderPeek(entry, row);
        else CloseSubMenu();
    }

    /// <summary>
    /// 悬停到**固定文件夹**上：先把二级菜单打开（空列表），后台取第一页后填进来。
    ///
    /// 目录枚举放后台线程：一个上万项的目录排一次序要几十毫秒，放在鼠标悬停回调里
    /// 会让整个菜单卡一下。完成后还要核对"用户是不是已经移到别的条目上了"，
    /// 否则慢一步的结果会盖掉人家现在在看的东西。
    /// </summary>
    private void OpenFolderPeek(GestureMenuEntry entry, ListBoxItem? row)
    {
        if (entry.FolderPath is not { Length: > 0 }) return;

        _peekEntry = entry;
        OpenSubMenu(entry, row);
        LoadFolderPage(entry, firstPage: true);
    }

    /// <summary>取一页文件夹条目并追加到二级菜单（firstPage = 清空后从 0 开始）。</summary>
    private void LoadFolderPage(GestureMenuEntry entry, bool firstPage)
    {
        if (entry.FolderPath is not { Length: > 0 } folder || _peekLoading) return;

        _peekLoading = true;
        int page = Core.Gestures.FolderPeek.PageSizeFor(FolderPageSize);
        int offset = firstPage ? 0 : entry.Children.Count;

        Task.Run(() =>
        {
            try
            {
                var items = Core.Gestures.FolderPeek.Page(folder, offset, page, out bool hasMore);
                return (items, hasMore);
            }
            catch { return (Array.Empty<Core.Gestures.FolderPeek.Entry>(), false); }
        }).ContinueWith(task =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _peekLoading = false;
                if (firstPage && !ReferenceEquals(_peekEntry, entry)) return;

                var (items, hasMore) = task.Result;
                var menuItems = items.Select(ToMenuItem).ToList();
                entry.AppendFolderItems(menuItems, hasMore);
            }));
        }, TaskScheduler.Default);
    }

    /// <summary>文件夹里的一条 → 菜单条目（点开就打开它）。</summary>
    private static GestureMenuItem ToMenuItem(Core.Gestures.FolderPeek.Entry entry) => new()
    {
        Title = entry.Name,
        Action = "openFile",
        Arg = entry.Path,
        IconPath = entry.Path,
        Enabled = true,
    };

    /// <summary>滚到接近底部就取下一页。</summary>
    private void OnSubListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_peekEntry is not { } entry || _peekLoading || !entry.HasMoreFolderItems) return;

        /* 只认"用户往下滚"（VerticalChange > 0）：
         * 追加一页之后 ExtentHeight 会变大，如果这里不设闸就会自己连环加载，
         * 一个上千项的目录能在一帧里全灌进来 —— 那就不是分页了。 */
        if (e.VerticalChange <= 0 || e.ExtentHeight <= 0) return;

        double remaining = e.ExtentHeight - e.VerticalOffset - e.ViewportHeight;
        if (remaining > 48) return;      // 48 DIP ≈ 还差不到两行

        LoadFolderPage(entry, firstPage: false);
    }


    /// <summary>
    /// 离开父项**不**立刻收：指针要从父项挪到右边子项上，中间必然经过一次 MouseLeave。
    /// 收早了用户根本点不到子项。真正收起靠"进入别的行"或整个菜单关闭。
    /// </summary>
    private void OnRowMouseLeave(object sender, MouseEventArgs e) { }

    private void OpenSubMenu(GestureMenuEntry parent, ListBoxItem? row)
    {
        /* 空列表只有一种情况允许：**固定文件夹**正在后台取第一页（箭头已经亮着）。
         * 其余空子项照旧当作"没有二级"，直接收起。 */
        if (parent.Children.Count == 0 && parent.FolderPath == null) { CloseSubMenu(); return; }

        /* 静态子项（历史记录 / 命令）打开时清掉"文件夹预览"的上下文，
         * 否则后续的滚动会把上一个文件夹的下一页追加进来。 */
        if (parent.Children.Count > 0) _peekEntry = null;

        SubList.ItemsSource = parent.Children;
        SubCard.Visibility = Visibility.Visible;

        /* 纵向对齐父项、横向 0 间隙（"紧紧贴在一级菜单上"）：
         * SubCard 在窗口坐标里从 14（一级卡片的投影留白）开始，
         * 于是它的 Margin.Top = 14 + 行在卡片里的 Y。 */
        double rowY = row != null ? row.TranslatePoint(new Point(0, 0), MainCard).Y : 0;
        SubCard.Margin = new Thickness(0, 14 + Math.Max(0, rowY), 14, 0);

        /* 窗口是 SizeToContent：多出一列之后重新布局，右边戳出工作区就把整扇窗往左挪 ——
         * 一级菜单跟着动一点，但两块始终拼在一起、都在屏内。 */
        UpdateLayout();
        KeepOnScreen();

        SubPlacementForTest = $"二级={SubCard.ActualWidth:0.#}x{SubCard.ActualHeight:0.#} "
                            + $"top={SubCard.Margin.Top:0.#} 窗口右沿={Left + ActualWidth:0.#}";
    }

    /// <summary>二级菜单当前是否展开。</summary>
    private bool IsSubMenuOpen => SubCard.Visibility == Visibility.Visible;

    /// <summary>窗口右下角戳出工作区就往回收（宽度会长出一条二级卡片）。</summary>
    private void KeepOnScreen()
    {
        try
        {
            PresentationSource? source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null) return;

            Rect work = Core.Windows.ScreenPlacement.TryGetWorkAreaDip(this, out Rect area) && !area.IsEmpty
                ? area
                : SystemParameters.WorkArea;

            Point origin = source.CompositionTarget.TransformFromDevice
                .Transform(PointToScreen(new Point(0, 0)));

            double overflowX = origin.X + ActualWidth - work.Right;
            if (overflowX > 0) Left -= overflowX;

            double overflowY = origin.Y + ActualHeight - work.Bottom;
            if (overflowY > 0) Top -= overflowY;
        }
        catch { /* 量不到就保持原样 */ }
    }

    /// <summary>自检/诊断用：上一次展开时的尺寸与位置。</summary>
    internal string SubPlacementForTest { get; private set; } = "";

    /// <summary>渲染 / 自检用：一级卡片。</summary>
    internal FrameworkElement CardForTest => MainCard;

    /// <summary>渲染 / 自检用：一级列表（要看真实行高）。</summary>
    internal ListBox ItemListForTest => ItemList;

    /// <summary>渲染 / 自检用：二级列表。</summary>
    internal ListBox SubListForTest => SubList;

    /// <summary>渲染 / 自检用：二级卡片。</summary>
    internal FrameworkElement SubCardForTest => SubCard;

    /// <summary>渲染 / 自检用：按序号展开某一项的二级菜单，返回二级卡片。</summary>
    internal FrameworkElement? OpenSubMenuForRender(int index)
    {
        if (index < 0 || index >= _entries.Count) return null;
        var parent = _entries[index];
        if (!parent.HasChildren) return null;

        ItemList.UpdateLayout();
        var row = ItemList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
        OpenSubMenu(parent, row);
        UpdateLayout();
        return SubCard;
    }

    /// <summary>渲染 / 自检用：展开固定文件夹的第一页（同步版，出图与断言都靠它）。</summary>
    internal FrameworkElement? OpenFolderPeekForRender(int index)
    {
        if (index < 0 || index >= _entries.Count) return null;
        var entry = _entries[index];
        if (entry.FolderPath is not { Length: > 0 } folder) return null;

        ItemList.UpdateLayout();
        var row = ItemList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
        _peekEntry = entry;
        OpenSubMenu(entry, row);

        var page = Core.Gestures.FolderPeek.Page(
            folder, 0, Core.Gestures.FolderPeek.PageSizeFor(FolderPageSize), out bool hasMore);
        entry.AppendFolderItems(page.Select(ToMenuItem).ToList(), hasMore);

        UpdateLayout();
        return SubCard;
    }

    private void CloseSubMenu()
    {
        if (!IsSubMenuOpen) return;
        SubCard.Visibility = Visibility.Collapsed;
        SubList.ItemsSource = null;
        _peekEntry = null;
        _peekLoading = false;
        foreach (var e in _entries) e.IsExpanded = false;

        /* 收起后窗口会缩回一级宽度：位置保持左上角不动，不需要再挪回去。 */
        UpdateLayout();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                /* 二级菜单开着时，Esc 先收二级、再按一次才关菜单：
                 * 和系统菜单的层级一致，不会一按就把整层都收掉。 */
                if (IsSubMenuOpen) { CloseSubMenu(); return; }
                SafeClose();
                return;

            case Key.Enter:
                e.Handled = true;
                Accept(ItemList.SelectedIndex);
                return;

            // 方向键自己处理（要绕回），顺便压住 ListBox 的默认导航，
            // 免得按下键时选中悄悄换到别的地方
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                return;

            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                return;

            case Key.Right:
                /* 键盘展开二级：选中项有子项时向右打开。 */
                e.Handled = true;
                if (ItemList.SelectedIndex >= 0 && ItemList.SelectedIndex < _entries.Count)
                {
                    var entry = _entries[ItemList.SelectedIndex];
                    if (entry.HasChildren &&
                        ItemList.ItemContainerGenerator.ContainerFromIndex(ItemList.SelectedIndex) is ListBoxItem row)
                        OpenSubMenu(entry, row);
                    else if (entry.HasChildren) OpenSubMenu(entry, null);
                }
                return;

            case Key.Left:
                e.Handled = true;
                CloseSubMenu();
                return;
        }

        int digit = DigitOf(e.Key);
        if (digit > 0 && digit <= _entries.Count)
        {
            e.Handled = true;
            Accept(digit - 1);
        }
    }

    // ── 选中与提交 ──────────────────────────────────────────────────

    private void MoveSelection(int delta)
    {
        if (_entries.Count == 0) return;

        int index = ItemList.SelectedIndex;
        index = index < 0 ? 0 : (index + delta + _entries.Count) % _entries.Count;
        ItemList.SelectedIndex = index;
        ItemList.ScrollIntoView(_entries[index]);
    }

    private void Accept(int index)
    {
        if (index < 0 || index >= _entries.Count) return;
        Accept(_entries[index]);
    }

    private void Accept(GestureMenuEntry entry)
    {
        if (_committed) return;
        /* 父项没有动作：直接点是展开（见 OnEntryClick），这里再挡一道，
         * 免得键盘回车把"历史记录"这种父项当成空动作执行掉。 */
        if (entry.HasChildren && string.IsNullOrWhiteSpace(entry.Item.Action)) return;

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

    /// <summary>1..9 与小键盘 1..9；别的键返回 0。</summary>
    private static int DigitOf(Key key)
    {
        // Key 里 D0..D9、NumPad0..NumPad9 各自连续，按整数比较比写 18 个 case 清楚
        int code = (int)key;
        if (code >= (int)Key.D1 && code <= (int)Key.D9) return code - (int)Key.D0;
        if (code >= (int)Key.NumPad1 && code <= (int)Key.NumPad9) return code - (int)Key.NumPad0;
        return 0;
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

/// <summary>
/// 弹出菜单里的一行。
///
/// 单独包一层是因为 WPF 的数据绑定只认公共类型的公共属性，
/// 而 <see cref="GestureMenuItem"/> 上没有"序号""兜底后的标题""解析好的图标"
/// 这些纯显示用的东西。
/// </summary>
public sealed class GestureMenuEntry : System.ComponentModel.INotifyPropertyChanged
{
    public GestureMenuEntry(GestureMenuItem item, int index, string title)
    {
        Item = item;
        Title = title;
        Glyph = (item.Glyph ?? "").Trim();
        Note = (item.Note ?? "").Trim();
        /* 只有 1..9 能直接按键选中，第 10 条以后不给序号（给了也按不到） */
        /* 序号不再显示（用户要求）：1..9 的快捷选中仍然有效，只是不在行首写数字。 */
        Index = "";
        /* 图标按"有则显示"排优先级：真实图标 > 字形 > 序号。
         * 三者互斥，模板里各绑一个 Visibility，取反在现场算好，XAML 里不用写转换器。 */
        Icon = ResolveIcon(item.IconPath ?? "", item.Action);
        IconVisibility = Icon != null ? Visibility.Visible : Visibility.Collapsed;
        GlyphVisibility = Icon == null && Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IndexVisibility = Icon == null && Glyph.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteVisibility = Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        /* 初始的灰/亮照搬条目自己的 Enabled（构造时就已经定好了：见 MiddleMenuPolicy.Build）。 */
        _isEnabled = item.Enabled;

        /* 子项递归：二级菜单里不再有三级（菜单是拿来快速点的，三层点起来比打字还慢）。 */
        Children = new System.Collections.ObjectModel.ObservableCollection<GestureMenuEntry>(
            (item.Children ?? new List<GestureMenuItem>())
                .Take(12)
                .Select((child, i) => new GestureMenuEntry(child, i, string.IsNullOrWhiteSpace(child.Title) ? "" : child.Title.Trim()))
                .Where(e => e.Title.Length > 0));

        /* 悬停展开固定文件夹：Action=openFile 且目标真的是目录、又没有静态子项。
         * 箭头要提前亮出来 —— 用户得先看见"这个能展开"才会把鼠标停上去。 */
        FolderPath = PeekableFolderOf(item);
        ArrowVisibility = Children.Count > 0 || FolderPath != null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// 这一项是不是"可以悬停展开的文件夹"。是就返回路径，否则 null。
    /// 只认打开动作指向的**现存目录**：网址、程序、被删掉的路径都不算。
    /// </summary>
    private static string? PeekableFolderOf(GestureMenuItem item)
    {
        try
        {
            if (!string.Equals(item.Action, "openFile", StringComparison.OrdinalIgnoreCase)) return null;
            string path = (item.Arg ?? "").Trim();
            if (path.Length == 0) return null;
            return Directory.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>原始条目，选中后要交回给调用方。</summary>
    public GestureMenuItem Item { get; }

    /// <summary>1..9 的序号；第 10 条以后是空串。</summary>
    public string Index { get; }

    /// <summary>行首显示的文字：Title，或兜底的动作标题。</summary>
    public string Title { get; }

    private string _glyph = "";
    private string _note = "";
    private Visibility _glyphVisibility;
    private Visibility _noteVisibility;
    private bool _isEnabled;

    /// <summary>
    /// 这一行现在能不能点。
    ///
    /// **必须**是带通知的属性（外面模板的 0.4 透明度绑在它上面）：
    /// 附加菜单顶部「打开文件所在位置」在弹菜单那一刻还不知道有没有文件，
    /// 预热（识别）回来后要把灰/亮**原地**刷出来 —— 绑 <see cref="GestureMenuItem.Enabled"/>
    /// 那种普通属性是刷不动的（菜单已经上屏，不重建 ItemsSource 就永远显示初始状态）。
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set { _isEnabled = value; Raise(nameof(IsEnabled)); }
    }

    /// <summary>
    /// 灰/亮一起改两处：这里（管外观）与 <see cref="GestureMenuItem.Enabled"/>（管点击，
    /// <c>Accept</c> 里会再判一次）。两处必须同步，否则会出现"看着是灰的、点下去却执行了"。
    /// </summary>
    internal void SetEnabled(bool on)
    {
        Item.Enabled = on;
        if (_isEnabled != on) IsEnabled = on;
    }

    /// <summary>行首的图标字形，空串表示没设图标。</summary>
    public string Glyph
    {
        get => _glyph;
        private set { _glyph = value; Raise(nameof(Glyph)); }
    }

    /// <summary>第二行小字（网址标题 / 路径），空串表示不显示。</summary>
    public string Note
    {
        get => _note;
        private set { _note = value; Raise(nameof(Note)); }
    }

    /// <summary>解析好的真实图标（文件 / 文件夹 / 程序 / 网址 favicon）；没有就是 null。</summary>
    public ImageSource? Icon { get; }

    public Visibility IconVisibility { get; }

    public Visibility GlyphVisibility
    {
        get => _glyphVisibility;
        private set { _glyphVisibility = value; Raise(nameof(GlyphVisibility)); }
    }

    public Visibility IndexVisibility { get; }

    public Visibility NoteVisibility
    {
        get => _noteVisibility;
        private set { _noteVisibility = value; Raise(nameof(NoteVisibility)); }
    }

    public Visibility ArrowVisibility
    {
        get => _arrowVisibility;
        private set { _arrowVisibility = value; Raise(nameof(ArrowVisibility)); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <summary>
    /// 预热回来之后补上第二行（菜单已经上屏了，所以必须走 INotifyPropertyChanged）。
    /// 空串会把整行折叠起来，行高不变。
    /// </summary>
    internal void UpdateNote(string note)
    {
        Note = (note ?? "").Trim();
        NoteVisibility = Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把星标换成实心（已经在固定项里了）。</summary>
    internal void UpdateFavoriteGlyph(bool favorited)
    {
        Glyph = favorited ? "\uE735" : "\uE734";
        GlyphVisibility = Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>有没有二级菜单。</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>二级菜单的内容（静态子项 + 悬停展开文件夹后追加的条目）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<GestureMenuEntry> Children { get; }

    /// <summary>可悬停展开的文件夹路径；不是文件夹就是 null。</summary>
    public string? FolderPath { get; }

    /// <summary>这个文件夹是不是还能继续往下翻（滚动到底再取一页用）。</summary>
    public bool HasMoreFolderItems
    {
        get => _hasMoreFolderItems;
        private set { _hasMoreFolderItems = value; Raise(nameof(HasMoreFolderItems)); }
    }

    private bool _hasMoreFolderItems;
    private Visibility _arrowVisibility;

    /// <summary>把下一页文件夹条目追加到二级菜单（必须在 UI 线程调用）。</summary>
    internal void AppendFolderItems(IEnumerable<GestureMenuItem> items, bool hasMore)
    {
        foreach (GestureMenuItem item in items)
        {
            var child = new GestureMenuEntry(item, Children.Count, item.Title?.Trim() ?? "");
            if (child.Title.Length == 0) continue;
            Children.Add(child);
        }

        HasMoreFolderItems = hasMore;
        ArrowVisibility = Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>二级菜单当前是否展开（只用于高亮，没参与选中逻辑）。</summary>
    public bool IsExpanded { get; set; }

    /// <summary>
    /// 图标解析：网址取 favicon，其余当磁盘路径取 shell 图标。
    ///
    /// 解析失败一律返回 null（回落到字形/序号）—— 菜单不能因为一个图标没取到就缺一块。
    /// 网址没缓存时顺手排一次后台下载，下次打开菜单就有图标了。
    /// </summary>
    private static ImageSource? ResolveIcon(string iconPath, string action)
    {
        string p = iconPath.Trim();
        if (p.Length == 0) return null;
        try
        {
            bool isUrl = p.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(action, "webPage", StringComparison.OrdinalIgnoreCase);

            if (isUrl)
            {
                var web = IconCache.GetWebIcon(p);
                if (web == null) IconCache.LoadWebIconAsync(p, _ => { });
                return web;
            }

            return IconCache.FromFile(p) ?? IconCache.FromExecutable(p);
        }
        catch { return null; }
    }
}
