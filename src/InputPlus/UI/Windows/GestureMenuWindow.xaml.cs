using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
/// · ↑/↓ 换选中（到头绕回），Enter 执行当前选中，Esc / 点别处收起来。
/// </summary>
public partial class GestureMenuWindow : Window
{
    /// <summary>菜单离光标多远（DIP）。留一条缝，不然光标正好压在第一行上。</summary>
    private const double CursorGap = 10;

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
    /// 把窗口摆到鼠标旁边。
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
        double workWidth = workBottomRight.X - workTopLeft.X;
        double workHeight = workBottomRight.Y - workTopLeft.Y;

        double width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        double height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
        bool sized = width > 0 && height > 0;

        // 默认贴在光标右下；右边 / 下边放不下就翻到左 / 上（和系统右键菜单一样）
        double x = at.X + CursorGap;
        double y = at.Y + CursorGap;
        if (sized)
        {
            if (x + width > workTopLeft.X + workWidth) x = at.X - width - CursorGap;
            if (y + height > workTopLeft.Y + workHeight) y = at.Y - height - CursorGap;
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
            e.Handled = true;
            Accept(entry);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
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
/// 而 <see cref="GestureMenuItem"/> 上没有"序号"和"兜底后的标题"这两个纯显示用的东西。
/// </summary>
public sealed class GestureMenuEntry
{
    public GestureMenuEntry(GestureMenuItem item, int index, string title)
    {
        Item = item;
        Title = title;
        Glyph = (item.Glyph ?? "").Trim();
        // 只有 1..9 能直接按键选中，第 10 条以后不给序号（给了也按不到）
        Index = index < 9 ? (index + 1).ToString() : "";
    }

    /// <summary>原始条目，选中后要交回给调用方。</summary>
    public GestureMenuItem Item { get; }

    /// <summary>1..9 的序号；第 10 条以后是空串。</summary>
    public string Index { get; }

    /// <summary>行首显示的文字：Title，或兜底的动作标题。</summary>
    public string Title { get; }

    /// <summary>图标的字形字符，空串表示没设图标。</summary>
    public string Glyph { get; }

    /// <summary>有没有图标字形（没有就显示序号）。</summary>
    public bool HasGlyph => Glyph.Length > 0;
}
