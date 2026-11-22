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

    /// <param name="menu">要弹出的菜单。</param>
    /// <param name="actionTitleOf">动作 id → 中文标题；条目自己没写 Title 时用它兜底。</param>
    public GestureMenuWindow(GestureMenuDef menu, Func<string, string?> actionTitleOf)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(actionTitleOf);

        InitializeComponent();

        MenuTitleText.Text = menu.DisplayName;

        string hint = (menu.Hint ?? "").Trim();
        MenuHintText.Text = hint;
        MenuHintText.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

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
    /// 在鼠标位置弹出菜单，返回用户点中的条目（关掉没选就是 null）。
    /// </summary>
    /// <param name="menu">要弹出的菜单。</param>
    /// <param name="actionTitleOf">动作 id → 中文标题，给没写 Title 的条目兜底。</param>
    /// <param name="owner">可选宿主窗口，有就跟着它走（模态）。</param>
    public static GestureMenuItem? Pick(GestureMenuDef menu, Func<string, string?> actionTitleOf, Window? owner = null)
    {
        var window = new GestureMenuWindow(menu, actionTitleOf);
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
        if (!_ready || _committed) return;
        Close();
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

    // ── 二级菜单 ────────────────────────────────────────────────────

    /// <summary>
    /// 悬停到带子项的条目上 → 右侧展开。
    /// 悬停到普通条目上 → 收起，避免"上一个子菜单还挂在那儿"。
    /// </summary>
    private void OnRowMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not ListBoxItem row || row.DataContext is not GestureMenuEntry entry) return;
        if (entry.HasChildren) OpenSubMenu(entry, row);
        else CloseSubMenu();
    }

    /// <summary>
    /// 离开父项**不**立刻收：指针要从父项挪到右边子项上，中间必然经过一次 MouseLeave。
    /// 收早了用户根本点不到子项。真正收起靠"进入别的行"或整个菜单关闭。
    /// </summary>
    private void OnRowMouseLeave(object sender, MouseEventArgs e) { }

    private void OpenSubMenu(GestureMenuEntry parent, ListBoxItem? row)
    {
        if (parent.Children.Count == 0) { CloseSubMenu(); return; }
        SubList.ItemsSource = parent.Children;
        if (row != null)
        {
            SubPopup.PlacementTarget = row;
            SubPopup.Placement = PlacementMode.Right;
        }
        else
        {
            /* 键盘（→）展开时可能拿不到行容器（列表还没生成容器）：
             * 贴着鼠标展开，总比不展开好。 */
            SubPopup.PlacementTarget = this;
            SubPopup.Placement = PlacementMode.Mouse;
        }
        SubPopup.IsOpen = true;
        parent.IsExpanded = true;
    }

    private void CloseSubMenu()
    {
        if (!SubPopup.IsOpen) return;
        SubPopup.IsOpen = false;
        SubList.ItemsSource = null;
        foreach (var e in _entries) e.IsExpanded = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                /* 二级菜单开着时，Esc 先收二级、再按一次才关菜单：
                 * 和系统菜单的层级一致，不会一按就把整层都收掉。 */
                if (SubPopup.IsOpen) { CloseSubMenu(); return; }
                Close();
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
            Close();
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
public sealed class GestureMenuEntry
{
    public GestureMenuEntry(GestureMenuItem item, int index, string title)
    {
        Item = item;
        Title = title;
        Glyph = (item.Glyph ?? "").Trim();
        Note = (item.Note ?? "").Trim();
        /* 只有 1..9 能直接按键选中，第 10 条以后不给序号（给了也按不到） */
        Index = index < 9 ? (index + 1).ToString() : "";
        /* 图标按"有则显示"排优先级：真实图标 > 字形 > 序号。
         * 三者互斥，模板里各绑一个 Visibility，取反在现场算好，XAML 里不用写转换器。 */
        Icon = ResolveIcon(item.IconPath ?? "", item.Action);
        IconVisibility = Icon != null ? Visibility.Visible : Visibility.Collapsed;
        GlyphVisibility = Icon == null && Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IndexVisibility = Icon == null && Glyph.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteVisibility = Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        /* 子项递归：二级菜单里不再有三级（菜单是拿来快速点的，三层点起来比打字还慢）。 */
        Children = (item.Children ?? new List<GestureMenuItem>())
            .Take(12)
            .Select((child, i) => new GestureMenuEntry(child, i, string.IsNullOrWhiteSpace(child.Title) ? "" : child.Title.Trim()))
            .Where(e => e.Title.Length > 0)
            .ToList();
        ArrowVisibility = Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>原始条目，选中后要交回给调用方。</summary>
    public GestureMenuItem Item { get; }

    /// <summary>1..9 的序号；第 10 条以后是空串。</summary>
    public string Index { get; }

    /// <summary>行首显示的文字：Title，或兜底的动作标题。</summary>
    public string Title { get; }

    /// <summary>行首的图标字形，空串表示没设图标。</summary>
    public string Glyph { get; }

    /// <summary>第二行小字（网址标题 / 路径），空串表示不显示。</summary>
    public string Note { get; }

    /// <summary>解析好的真实图标（文件 / 文件夹 / 程序 / 网址 favicon）；没有就是 null。</summary>
    public ImageSource? Icon { get; }

    public Visibility IconVisibility { get; }

    public Visibility GlyphVisibility { get; }

    public Visibility IndexVisibility { get; }

    public Visibility NoteVisibility { get; }

    public Visibility ArrowVisibility { get; }

    /// <summary>有没有二级菜单。</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>二级菜单的内容（已递归构造好）。</summary>
    public List<GestureMenuEntry> Children { get; }

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
