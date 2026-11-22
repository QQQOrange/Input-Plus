using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
/* 只挑拖动要用的几个输入类型做别名：整条 using System.Windows.Input 会带进来
 * WPF 自己的 MouseGesture，和手势模型 MouseGesture 撞名（CS0104）。 */
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyboardFocusChangedEventArgs = System.Windows.Input.KeyboardFocusChangedEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using System.Windows.Shapes;
using InputPlus.Core;
using InputPlus.Core.Actions;
using InputPlus.Core.Gestures;
using InputPlus.Core.Settings;
using InputPlus.UI.Windows;

namespace InputPlus.UI.Views;

/// <summary>
/// 鼠标手势页：左边列手势，右边编一条。
///
/// 这一页只做"编辑"，真正的识别在 <see cref="GestureService"/> 里 ——
/// 界面不参与任何钩子逻辑，录制时也只是让服务把下一笔回传过来。
/// </summary>
public partial class GesturesView
{

    /* 回放动画的状态（就在笔画框里画，见 OnReplayClick）。 */
    private System.Windows.Threading.DispatcherTimer? _replayTimer;
    private List<Point> _replayPoints = new();
    private int _replayIndex;
    private int _replaySession;
    /* 放慢之后回放按"已经过了多少毫秒"推进，不再一帧一个点（见 OnReplayTick）。 */
    private System.Diagnostics.Stopwatch? _replayClock;
    private int _replayTotalMs = 1;
    private AppHost? _host;
    private readonly List<GestureRow> _rows = new();

    /* 拖动排序的状态：按住一会儿、或者按住后直接往上/下拖，就进来（见 OnRowPressDown / StartDrag），松手落位。
     * v1.6.4：原来必须一动不动按满 2 秒才算，手一抖就重新计时，实际用下来几乎拖不动；
     * 现在 400ms 到点就拎起来，或者按住后纵向移动超过 8px 立刻开始拖（跟"拖"这个动作对齐）。 */
    private System.Windows.Threading.DispatcherTimer? _pressTimer;
    private Button? _pressButton;
    private MouseGesture? _dragItem;
    private GestureRow? _dragRow;
    private Button? _dragButton;
    private TranslateTransform? _dragShift;
    private int _dragFrom = -1;
    private int _dropIndex = -1;
    private Point _dragStart;
    private double _dragScrollStart;
    private bool _dragging;

    /// <summary>
    /// 这次鼠标抬起是"放下"，不是"点选"。
    ///
    /// 拖起来之后按钮仍然（或可能）拿着鼠标捕获，松手时 ButtonBase 会照常发 Click ——
    /// 那会把右侧编辑器切到这一条上（如果它刚好又是当前选中的，还会把编辑器里的
    /// 未保存改动冲掉）。这里吞掉这一次点击，下一次按下时自动复位。
    /// </summary>
    private bool _suppressClick;

    /// <summary>
    /// 列表排序方式（<see cref="GestureListOrder"/> 的四档之一）。
    /// 和 <c>GestureOptions.ListSort</c> 同步存盘；只有 custom 档允许拖动改顺序。
    /// </summary>
    private string _sortMode = GestureListOrder.Custom;
    private readonly List<TargetRow> _targetRows = new();
    private MouseGesture _current = new();
    private string _scope = "";
    private bool _loading;

    /// <summary>
    /// 「分类 / 功能」两个下拉正在被代码重建。
    ///
    /// 重建过程中 ItemsSource 会整体换掉，SelectionChanged 会先以"什么都没选"的形式
    /// 触发一次 —— 那时若照常回写 <see cref="_current"/>.Action，用户刚绑好的动作和参数
    /// 就被抹成空。凡是在这里重建，都把标志立起来，让两个 SelectionChanged 处理器让路。
    /// </summary>
    private bool _rebuilding;

    /// <summary>编辑器里正在编的多步序列（保存时才写回手势）。</summary>
    private readonly List<GestureStep> _steps = new();

    /// <summary>左侧“边缘手势”不是某个 exe；用一个不会和路径冲突的哨兵值区分。</summary>
    private const string EdgeTargetKey = "\u0001edge";

    private static bool IsEdgeGesture(MouseGesture g) =>
        g.Kind is "hotcorner" or "edgewheel" or "edge";

    public GesturesView()
    {
        InitializeComponent();
        PreviewCanvas.SizeChanged += (_, _) => DrawPreview();

        /* 筛选下拉的宽度是按内容量出来的（见 FitFilterBoxWidth）。字号/字体换了字宽也会变，
         * 所以宽度一有变化就把搜索框跟着对上，免得收开搜索时右边露出一角。 */
        ButtonFilterBox.SizeChanged += (_, _) => SyncSearchPanelWidth();
    }

    public void Initialize(AppHost host)
    {
        _host = host;

        /* 托盘菜单现在也能开关手势。页面正开着的时候值会变，
         * 这里订阅一次保存通知，把总开关拨到实际状态 ——
         * 不能整页 Refresh()：那会把用户正在编辑的那一行重置掉。 */
        _host.SettingsChanged -= OnHostSettingsChanged;
        _host.SettingsChanged += OnHostSettingsChanged;

        _loading = true;
        try { BuildChoices(); }
        finally { _loading = false; }
        Refresh();
    }

    private void OnHostSettingsChanged()
    {
        if (_host == null || _loading) return;
        bool on = _host.Settings.GestureOptions.Enabled;
        if (EnabledBox.IsChecked == on) return;

        _loading = true;
        try { EnabledBox.IsChecked = on; }
        finally { _loading = false; }
    }

    public void Refresh()
    {
        if (_host == null) return;
        _loading = true;
        try
        {
            var opts = _host.Settings.GestureOptions;
            /* 页面上只剩总开关：外观、参数、开关快捷键都进了设置窗，那边自己灌值。 */
            EnabledBox.IsChecked = opts.Enabled;
            _sortMode = GestureListOrder.Normalize(opts.ListSort);
            UpdateSortChecks();
            BuildTargets();
            LoadList(selectId: _current.Id);
        }
        finally { _loading = false; }
    }

    // ───────────────────────── 列表 ─────────────────────────

    // ───────────────────── 作用目标 ─────────────────────

    /// <summary>
    /// 重建左侧「作用目标」：照 WGestures 2 的四类抄 ——
    /// 全局 / 分组 / 应用程序 / 特殊目标（任务栏、桌面）。
    /// 手势里存的是 exe 全路径，这里只显示程序名，完整路径放在 ToolTip。
    /// </summary>
    private void BuildTargets()
    {
        var settings = _host!.Settings;
        var options = settings.GestureOptions;
        options.DisabledTargets ??= new List<string>();
        var list = options.Targets2();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in settings.Gestures)
        {
            if (IsEdgeGesture(g)) continue;   // 边缘手势在左侧单独一栏
            string key = g.AppPath ?? "";
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        var scopePaths = new List<string>();
        foreach (string path in counts.Keys)
            if (!string.IsNullOrWhiteSpace(path) && IsAppScope(path)) scopePaths.Add(path);
        foreach (string path in options.Targets)
            if (!string.IsNullOrWhiteSpace(path)
                && !scopePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                scopePaths.Add(path);
        scopePaths.Sort(StringComparer.OrdinalIgnoreCase);

        _targetRows.Clear();

        /* ① 全局：所有程序里都生效，也是其它目标的默认值来源 */
        counts.TryGetValue("", out int globalCount);
        _targetRows.Add(Row("", Loc.T("全局"), globalCount, "\uE774",
            Loc.T("所有程序里都生效、且优先级低于应用手势"), Visibility.Visible, false, false));

        /* ② 分组：一组程序共用一套触发方式；一条分组都没有时不画标题 */
        var groups = list.Where(t => t.Kind == GestureTargetKind.Group).ToList();
        if (groups.Count > 0) _targetRows.Add(HeaderRow(Loc.T("分组")));
        foreach (var t in groups)
        {
            int count = CountGroup(t, settings);
            _targetRows.Add(Row(t.Id, t.Name, count, "\uE8F1",
                Loc.F("这一组有 {0} 个程序，共用一套触发方式", t.Members?.Count ?? 0),
                Visibility.Collapsed, true, false));
        }

        /* ③ 应用程序：按 exe 全路径匹配 */
        if (scopePaths.Count > 0) _targetRows.Add(HeaderRow(Loc.T("应用程序")));
        foreach (string path in scopePaths)
        {
            counts.TryGetValue(path, out int count);
            bool disabled = options.DisabledTargets.Any(p => SameTarget(p, path));
            _targetRows.Add(Row(path, AppName(path), count, "\uE71D", path,
                Visibility.Visible, false, disabled, deletable: true));
        }

        /* ④ 特殊目标：和 WGestures 一样只有任务栏和桌面两项 */
        _targetRows.Add(HeaderRow(Loc.T("特殊目标")));
        _targetRows.Add(Row(GestureTarget.TaskbarId, Loc.T("任务栏"), 0, "\uE737",
            Loc.T("指针在任务栏上时用这一套触发方式"), Visibility.Collapsed, false, false));
        _targetRows.Add(Row(GestureTarget.DesktopId, Loc.T("桌面"), 0, "\uE7F4",
            Loc.T("指针在桌面上时用这一套触发方式"), Visibility.Collapsed, false, false));

        /* 边缘手势单独一栏，不塞进“作用目标”列表；这里只更新计数和选中态。 */
        int edgeCount = settings.Gestures.Count(IsEdgeGesture);
        EdgeTargetCount.Text = edgeCount > 0 ? edgeCount.ToString() : "";
        bool edgeSelected = string.Equals(_scope, EdgeTargetKey, StringComparison.Ordinal);
        EdgeTargetButton.Tag = edgeSelected;

        // 选中的目标被删光后，退回全局，避免列表莫名其妙停在空页上。
        if (!edgeSelected && _targetRows.All(r => !r.Selected || r.HeaderVisibility == Visibility.Visible))
            _scope = "";

        TargetList.ItemsSource = null;
        TargetList.ItemsSource = _targetRows;
    }

    /// <summary>
    /// 一条可选中的作用目标行。
    /// deletable：应用程序也能从这一栏删掉（分组本来就是 true）。
    /// </summary>
    private TargetRow Row(string scope, string title, int count, string glyph, string tooltip,
        Visibility toggle, bool isGroup, bool disabled, bool deletable = false) => new()
    {
        Scope = scope,
        Title = title,
        Count = count > 0 ? count.ToString() : "",
        Glyph = glyph,
        Tooltip = tooltip,
        Selected = string.Equals(_scope, scope, StringComparison.OrdinalIgnoreCase),
        ToggleText = isGroup ? "" : disabled ? Loc.T("恢复") : Loc.T("禁用"),
        // 菜单项前面那颗小图标：禁用是个"叉"，恢复是"往回走"，跟文字一起读才不歧义。
        ToggleGlyph = disabled ? "\uE71A" : "\uE733",
        ToggleVisibility = toggle,
        ToggleTooltip = disabled
            ? Loc.T("再点一次恢复：这个程序里重新开始触发手势。")
            : Loc.T("这个程序里不触发任何手势，包括边缘手势。"),
        DeleteVisibility = isGroup || deletable ? Visibility.Visible : Visibility.Collapsed,
        DeleteTooltip = isGroup
            ? Loc.T("删掉这个分组（组里的程序不受影响）。")
            : Loc.T("删掉这个作用目标（它下面的手势一起删掉）。"),
        Disabled = disabled,
        DisabledVisibility = disabled ? Visibility.Visible : Visibility.Collapsed,
        // 一个动作都没有的行（全局 / 任务栏 / 桌面）不出竖三点：点了只有空气。
        MenuVisibility = toggle == Visibility.Visible || isGroup || deletable
            ? Visibility.Visible
            : Visibility.Collapsed,
    };

    /// <summary>分组标题行：只当分隔线，不可点。</summary>
    private static TargetRow HeaderRow(string title) => new()
    {
        Title = title,
        HeaderVisibility = Visibility.Visible,
        RowVisibility = Visibility.Collapsed,
    };

    /// <summary>一个分组名下有多少条手势（组本身绑的 + 组里各程序的）。</summary>
    private static int CountGroup(GestureTarget t, AppSettings settings)
    {
        int n = 0;
        foreach (var g in settings.Gestures)
        {
            if (IsEdgeGesture(g)) continue;
            string path = g.AppPath ?? "";
            if (string.Equals(path, t.Id, StringComparison.OrdinalIgnoreCase)) { n++; continue; }
            if ((t.Members ?? new List<string>()).Any(m => SameTarget(m, path))) n++;
        }
        return n;
    }

    // ───────────────────── 作用目标的作用域 ─────────────────────

    private const string GroupPrefix = "group:";

    private static bool IsGroupScope(string s) =>
        s.StartsWith(GroupPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsSpecialScope(string s) =>
        s.StartsWith("special:", StringComparison.OrdinalIgnoreCase);

    /// <summary>是不是某个具体程序（不是全局/边缘/分组/特殊目标）。</summary>
    private static bool IsAppScope(string s) =>
        s.Length > 0 && !string.Equals(s, EdgeTargetKey, StringComparison.Ordinal)
        && !IsGroupScope(s) && !IsSpecialScope(s);

    /// <summary>这个作用域对应的分组（不是分组作用域时返回 null）。</summary>
    private GestureTarget? GroupFor(string scope)
    {
        if (!IsGroupScope(scope) || _host == null) return null;
        return _host.Settings.GestureOptions.Targets2()
            .FirstOrDefault(t => t.Kind == GestureTargetKind.Group
                && string.Equals(t.Id, scope, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>作用目标在界面上的名字，也用作「{0} 允许的触发方式」里的 {0}。</summary>
    private string ScopeTitle(string scope)
    {
        if (string.IsNullOrEmpty(scope)) return Loc.T("全局");
        if (string.Equals(scope, EdgeTargetKey, StringComparison.Ordinal)) return Loc.T("边缘手势");
        if (IsGroupScope(scope)) return GroupFor(scope)?.Name ?? Loc.T("分组");
        if (string.Equals(scope, GestureTarget.TaskbarId, StringComparison.OrdinalIgnoreCase)) return Loc.T("任务栏");
        if (string.Equals(scope, GestureTarget.DesktopId, StringComparison.OrdinalIgnoreCase)) return Loc.T("桌面");
        return AppName(scope);
    }

    /// <summary>取（必要时创建）这个作用域在 TargetList 里的那一条。</summary>
    private GestureTarget? TargetFor(string scope, bool create)
    {
        if (_host == null) return null;
        var list = _host.Settings.GestureOptions.Targets2();
        var t = list.FirstOrDefault(x => string.Equals(x.Id, scope, StringComparison.OrdinalIgnoreCase));
        if (t != null || !create) return t;
        t = new GestureTarget { Id = scope, Name = ScopeTitle(scope) };
        if (IsAppScope(scope)) t.AppPath = scope;
        list.Add(t);
        return t;
    }

    private static string AppName(string path)
    {
        try
        {
            /* 优先用文件里的显示名：exe 名常常是 msedge / explorer / cmd，
             * 摆在一列里像日志而不像"应用程序"。FileDescription 是
             * "Microsoft Edge" / "Windows 资源管理器" / "Windows 命令处理程序"，
             * 读不到（绿色版、被删掉）再退回文件名。 */
            string? desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc))
            {
                string label = desc.Trim();
                /* 「Windows 资源管理器 / Windows 命令处理程序」在一列 248 宽的地方
                   全被截成「Windo…」，两个程序看着一模一样。去掉这个前缀，
                   剩下的才是能区分的那半截；完整路径在 ToolTip 里。 */
                if (label.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase) && label.Length > 8)
                    label = label.Substring(8).Trim();
                return label;
            }

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch { return path; }
    }

    private void OnPickTarget(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TargetRow row) return;
        if (string.Equals(row.Scope, _scope, StringComparison.OrdinalIgnoreCase)) return;
        _scope = row.Scope;
        BuildTargets();
        LoadList(selectId: "");
    }

    /// <summary>同一个 exe 的全路径或文件名一致，就当成同一个目标。</summary>
    private static bool SameTarget(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        string na = System.IO.Path.GetFileName(a);
        string nb = System.IO.Path.GetFileName(b);
        return na.Length > 0 && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>行尾的竖三点：把「禁用 / 恢复」和「删除」摊在按钮下面。</summary>
    private void OnTargetMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu) return;
        // 保险：ContextMenu 平时靠 PlacementTarget 继承 DataContext，开之前先贴一次，
        // 免得菜单项拿到 null 行对象后点「禁用」毫无反应。
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 右键 / 键盘也能打开同一份菜单，开口处把这一行的对象贴到菜单上。
    /// ContextMenu 不在可视树里，靠它自己顺着 PlacementTarget 继承 DataContext 太看时机，
    /// 而两个菜单项都是拿 DataContext 认行对象的（和原来那两颗按钮一样）。
    /// </summary>
    private void OnTargetMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ContextMenu is { } menu) menu.DataContext = fe.DataContext;
    }

    private void OnToggleTargetDisabled(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if ((sender as FrameworkElement)?.DataContext is not TargetRow row) return;
        if (!IsAppScope(row.Scope)) return;

        var list = _host.Settings.GestureOptions.DisabledTargets ??= new List<string>();
        int removed = list.RemoveAll(p => SameTarget(p, row.Scope));
        if (removed == 0) list.Add(row.Scope);

        _host.SaveSettings();
        BuildTargets();
        _host.Notify(removed == 0
            ? Loc.F("已禁止「{0}」触发手势", row.Title)
            : Loc.F("已恢复「{0}」的手势", row.Title));
        e.Handled = true;
    }

    private void OnPickEdgeTarget(object sender, RoutedEventArgs e)
    {
        if (string.Equals(_scope, EdgeTargetKey, StringComparison.Ordinal)) return;
        _scope = EdgeTargetKey;
        BuildTargets();
        LoadList(selectId: "");
    }

    /// <summary>入口一：从资源管理器挑 exe（和「按应用自动启用层」同一个交互）。</summary>
    private void OnPickTargetFile(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("手势对哪个程序生效"),
            Filter = Loc.T("程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) AddTarget(dlg.FileName);
    }

    /// <summary>入口二：十字准星点选窗口，取它的 exe 全路径。</summary>
    private void OnPickTargetWindow(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var picker = new UI.Windows.WindowPickerWindow();
        if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
        picker.ShowDialog();

        var h = picker.PickedWindow;
        if (h == IntPtr.Zero) return;       // 用户按了 Esc

        var info = Core.Windows.WindowService.Describe(h);
        if (info == null || string.IsNullOrEmpty(info.ExePath))
        {
            _host.Notify(Loc.T("取不到这个窗口的程序，改用「选择程序…」挑 exe"));
            return;
        }
        AddTarget(info.ExePath);
    }

    private void AddTarget(string path)
    {
        if (_host == null || string.IsNullOrWhiteSpace(path)) return;
        var targets = _host.Settings.GestureOptions.Targets;
        if (!targets.Contains(path, StringComparer.OrdinalIgnoreCase)) targets.Add(path);
        _scope = path;
        _host.SaveSettings();
        BuildTargets();
        LoadList(selectId: "");
    }

    // ───────────────────────── 列表 ─────────────────────────

    /* 这一页的「把当前范围的手势排成一张表」以及编辑器右侧那一摞联动，
     * 都是从仓库里最后一次成功构建的那份程序集里恢复回来的 —— 语义与 1.5.1 一致。 */

private void LoadList(string selectId = "")
{
    string text = FilterBox.Text.Trim();
    var settings = _host!.Settings;
    bool flag = string.Equals(_scope, "\u0001edge", StringComparison.Ordinal);
    bool flag2 = IsSpecialScope(_scope);
    string text2 = (flag ? "" : (SelectedValue(ButtonFilterBox) ?? ""));
    ButtonFilterBox.IsEnabled = !flag;
    var gestureTarget = GroupFor(_scope);
    TextBlock targetTitle = TargetTitle;
    string text3;
    if (flag)
    {
        text3 = Loc.T("边缘手势");
    }
    else if (_scope.Length != 0)
    {
        text3 = ((gestureTarget != null) ? Loc.F("分组「{0}」的手势", gestureTarget.Name) : Loc.F("{0} 的手势", ScopeTitle(_scope)));
    }
    else
    {
        text3 = Loc.T("全局的手势");
    }
    targetTitle.Text = text3;
    TriggerHeader.Text = Loc.F("{0} 允许的触发方式", ScopeTitle(_scope));
    List<InputPlus.Core.Gestures.MouseGesture> list = new List<InputPlus.Core.Gestures.MouseGesture>();
    foreach (InputPlus.Core.Gestures.MouseGesture gesture in settings.Gestures)
    {
        if (flag)
        {
            if (!IsEdgeGesture(gesture))
            {
                continue;
            }
        }
        else
        {
            if (IsEdgeGesture(gesture))
            {
                continue;
            }
            string path = gesture.AppPath ?? "";
            bool num;
            if (_scope.Length != 0)
            {
                if (gestureTarget == null)
                {
                    if (flag2)
                    {
                        continue;
                    }
                    num = string.Equals(path, _scope, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    if (string.Equals(path, gestureTarget.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        goto IL_01fd;
                    }
                    num = (gestureTarget.Members ?? new List<string>()).Any((string m) => SameTarget(m, path));
                }
            }
            else
            {
                num = path.Length == 0;
            }
            if (!num)
            {
                continue;
            }
        }
        goto IL_01fd;
        IL_01fd:
        if (text2.Length <= 0 || string.Equals(gesture.Button ?? "", text2, StringComparison.OrdinalIgnoreCase))
        {
            list.Add(gesture);
        }
    }
    if (_sortMode != "custom")
    {
        list = GestureListOrder.Sort(list, _sortMode, SortTitle);
    }
    /* v1.6.4：任何排序档都能按住拖。
     * 以前只有「自定义顺序」档给拖动手感，别的档 Draggable=false，按住半天毫无反应 ——
     * 用户不知道要先切排序档，只会觉得"拖动坏了"。现在按下就拖，
     * 如果当前不是自定义顺序，StartDrag 会先帮你切过去（见 SwitchToCustomForDrag）。
     * 仍保留一条限制：搜索框里有字时不拖 —— 这时候列表是子集，落位索引对不上整表。 */
    bool flag3 = text.Length == 0 && list.Count > 1;
    _rows.Clear();
    foreach (InputPlus.Core.Gestures.MouseGesture item in list)
    {
        string text4 = ActionCatalog.TitleOf(item.Action) ?? "";
        string text5 = "";
        string text6 = "";
        if (IsEdgeGesture(item))
        {
            if (item.Kind == "hotcorner")
            {
                text5 = CornerName(item.Corner);
                text6 = text4;
            }
            else if (item.Kind == "edgewheel")
            {
                text5 = Loc.F("{0} · 滚轮{1}", EdgeName(item.Edge), WheelName(item.Wheel));
                text6 = text4;
            }
        }
        else
        {
            text5 = item.Title(text4);
            text6 = Describe(item);
        }
        string text7 = text5 + " " + text6 + " " + item.AppPath + " " + item.Note;
        if (text.Length <= 0 || text7.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string badge = ((string.IsNullOrWhiteSpace(item.AppPath) | flag) ? "" : Loc.T("仅此应用"));
            var pointCollection = IconFor(item);
            bool flag4 = pointCollection != null && pointCollection.Count > 1;
            var imageSource = IsEdgeGesture(item) ? EdgeIconFor(item) : null;
            bool flag5 = imageSource != null;
            bool flag6 = flag4 && !flag5;
            bool flag7 = !item.Enabled;
            _rows.Add(new GestureRow(item, text5, text6, badge, IsEdgeGesture(item) ? EdgeGlyph(item) : GlyphFor(item), pointCollection, imageSource, (flag4 | flag5) ? Visibility.Collapsed : Visibility.Visible, (!flag6) ? Visibility.Collapsed : Visibility.Visible, (!flag5) ? Visibility.Collapsed : Visibility.Visible, flag3, flag7 ? 0.48 : 1.0, flag7 ? Loc.T("已停用") : "", (!flag7) ? Visibility.Collapsed : Visibility.Visible));
        }
    }
    GestureList.ItemsSource = null;
    GestureList.ItemsSource = _rows;
    TextBlock emptyHint = EmptyHint;
    string text8;
    if (flag)
    {
        text8 = Loc.T("还没有边缘手势。点右上角「录制新手势」，把触发方式设成「悬停到角落」或「贴边滚轮」。");
    }
    else if (flag2)
    {
        text8 = Loc.T("特殊目标只决定「哪些触发方式允许起手势」，手势本身记在全局或应用程序下面。展开上面的面板调整。");
    }
    else if (gestureTarget != null)
    {
        text8 = Loc.F("「{0}」里还没有手势。点右上角「录制新手势」，会记在这个分组下面。", gestureTarget.Name);
    }
    else
    {
        text8 = ((_scope.Length == 0) ? Loc.T("全局还没有手势。点右上角「录制新手势」，会记在「全局」下面。") : Loc.F("「{0}」还没有手势。点右上角「录制新手势」，会记在这个应用下面。", AppName(_scope)));
    }
    emptyHint.Text = text8;
    EmptyHint.Visibility = ((_rows.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
    SummaryText.Text = ((_rows.Count == 0) ? Loc.T("这个目标下还没有手势") : Loc.F("这个目标 {0} 条 · 全部 {1} 条", _rows.Count, settings.Gestures.Count));
    SummaryText.ToolTip = (flag3 ? Loc.T("按住某一条两秒再拖，可以调整顺序") : null);
    var gestureRow = _rows.FirstOrDefault(r => r.Gesture.Id == selectId) ?? _rows.FirstOrDefault();
    if (gestureRow != null)
    {
        _current = gestureRow.Gesture.Clone();
    }
    else
    {
        _current = new InputPlus.Core.Gestures.MouseGesture
        {
            Id = InputPlus.Core.Gestures.MouseGesture.NewId(),
            Kind = (flag ? "hotcorner" : "stroke"),
            Button = (flag ? "" : "right"),
            Corner = (flag ? 1 : 0),
            Action = (flag ? "switchNextApp" : ""),
            AppPath = ((flag | flag2) ? "" : _scope)
        };
    }
    LoadEditor();
    ListScroll.ScrollToTop();
    BuildTriggers();
}

private string SortTitle(InputPlus.Core.Gestures.MouseGesture g)
{
    if (g.Kind == "hotcorner")
    {
        return CornerName(g.Corner);
    }
    if (g.Kind == "edgewheel")
    {
        return Loc.F("{0} · 滚轮{1}", EdgeName(g.Edge), WheelName(g.Wheel));
    }
    return g.Title(ActionCatalog.TitleOf(g.Action) ?? "");
}

private void LoadEditor()
{
    _loading = true;
    try
    {
        ActionSearchBox.Text = "";
        ActionSearchList.ItemsSource = null;
        ActionSearchPanel.Visibility = Visibility.Collapsed;
        ActionSearchHint.Visibility = Visibility.Visible;
        ActionSearchClear.Visibility = Visibility.Collapsed;
        NameBox.Text = _current.Name;
        SelectTag(ButtonBox, _current.Button);
        SelectTag(ChordBox, _current.Chord);
        SelectTag(KindBox, _current.Kind);
        SelectValue(WheelBox, _current.Wheel);
        SelectValue(CornerBox, _current.Corner);
        SelectValue(EdgeBox, _current.Edge);
        _steps.Clear();
        if (_current.HasSteps)
        {
            _steps.AddRange(_current.Steps.Select((GestureStep s) => s.Clone()));
        }
        RefreshSteps();
        GestureEnabledBox.IsChecked = _current.Enabled;
        ImmediateBox.IsChecked = _current.Immediate;
        HideLabelBox.IsChecked = _current.HideLabel;
        SelectAction(_current.Action);
        ArgBox.Text = _current.Arg;
        EditorNote.Text = _current.Note;
        EditorNote.Visibility = (string.IsNullOrWhiteSpace(_current.Note) ? Visibility.Collapsed : Visibility.Visible);
        UpdateKindRows();
        UpdateArgVisibility();
        DrawPreview();
    }
    finally
    {
        _loading = false;
    }
}

private void UpdateKindRows()
{
    string text = SelectedTag(KindBox) ?? "stroke";
    bool flag = text == "stroke";
    bool flag2 = ((text == "corner" || text == "hotcorner") ? true : false);
    bool flag3 = flag2;
    flag2 = ((text == "wheel" || text == "edgewheel") ? true : false);
    bool flag4 = flag2;
    flag2 = ((text == "edgewheel" || text == "edge") ? true : false);
    bool flag5 = flag2;
    bool flag6 = text == "steps";
    flag2 = ((text == "hotcorner" || text == "edgewheel") ? true : false);
    bool flag7 = flag2;
    StrokeRow.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
    CornerRow.Visibility = ((!flag3) ? Visibility.Collapsed : Visibility.Visible);
    WheelRow.Visibility = ((!flag4) ? Visibility.Collapsed : Visibility.Visible);
    EdgeRow.Visibility = ((!flag5) ? Visibility.Collapsed : Visibility.Visible);
    StepsRow.Visibility = ((!flag6) ? Visibility.Collapsed : Visibility.Visible);
    EdgeLabel.Text = ((text == "edge") ? Loc.T("贴着哪条边") : Loc.T("屏幕边缘"));
    ImmediateBox.IsEnabled = flag;
    ButtonBox.IsEnabled = !flag7;
    ChordBox.IsEnabled = !flag7;
    if (flag7)
    {
        SelectTag(ButtonBox, "");
        SelectTag(ChordBox, "");
    }
}

private void UpdateArgVisibility()
{
    var actionInfo = _host?.Actions.Get(_current.Action);
    bool flag = actionInfo?.NeedsArg ?? false;
    bool flag2 = actionInfo?.Category == "窗口绑定";
    bool flag3 = actionInfo?.Id == "customKey";
    bool flag4 = flag3 || actionInfo?.Id == "sendKeys";
    string text = (flag3 ? CustomKeySpec.FromArg(_current.Arg).Key : "");
    if (flag3)
    {
        ArgBox.Tag = _current.Action;
        if (!string.Equals(ArgBox.Text, text, StringComparison.Ordinal))
        {
            ArgBox.Text = text;
        }
    }
    else if (flag && ArgBox.Tag as string != _current.Action)
    {
        ArgBox.Tag = _current.Action;
        ArgBox.Text = _current.Arg;
    }
    ArgBox.Visibility = ((!((flag && !flag2 && !flag4) | flag3)) ? Visibility.Collapsed : Visibility.Visible);
    ArgHint.Visibility = ((!flag3) ? Visibility.Collapsed : Visibility.Visible);
    ArgCaptureButton.Visibility = ((!flag4) ? Visibility.Collapsed : Visibility.Visible);
    ActionAppPicker.Visibility = ((!flag2) ? Visibility.Collapsed : Visibility.Visible);
    ActionAppStatus.Visibility = ((!flag2) ? Visibility.Collapsed : Visibility.Visible);
    ActionFunctionPanel.Visibility = (flag2 ? Visibility.Collapsed : Visibility.Visible);
    if (flag4)
    {
        string text2 = (flag3 ? text : _current.Arg);
        ArgCaptureButton.Content = (string.IsNullOrWhiteSpace(text2) ? Loc.T("录制按键") : CustomKeyRunner.ComboDisplay(text2));
    }
    if (flag2)
    {
        ActionAppStatus.Text = (string.IsNullOrWhiteSpace(_current.Arg) ? Loc.T("还没有选择程序") : _current.Arg);
    }
}

private void SetActionArg(string arg)
{
    _current.Arg = arg;
    var actionInfo = _host?.Actions.Get(_current.Action);
    if (actionInfo?.Id == "customKey")
    {
        string key = CustomKeySpec.FromArg(arg).Key;
        ArgBox.Text = key;
        ArgCaptureButton.Content = (string.IsNullOrWhiteSpace(key) ? Loc.T("录制按键") : CustomKeyRunner.ComboDisplay(key));
    }
    else
    {
        ArgBox.Text = arg;
        if (actionInfo?.Id == "sendKeys")
        {
            ArgCaptureButton.Content = (string.IsNullOrWhiteSpace(arg) ? Loc.T("录制按键") : arg);
        }
    }
    if (actionInfo?.Category == "窗口绑定")
    {
        ActionAppStatus.Text = (string.IsNullOrWhiteSpace(arg) ? Loc.T("还没有选择程序") : arg);
    }
}

private void OnCaptureArgKey(object sender, RoutedEventArgs e)
{
    if (_host == null)
    {
        return;
    }
    KeyCaptureWindow.Capture((KeyCaptureWindow.CaptureResult result) =>
    {
        if (!string.IsNullOrWhiteSpace(result.Combo))
        {
            if (_host.Actions.Get(_current.Action)?.Id == "customKey")
            {
                CustomKeySpec customKeySpec = CustomKeySpec.FromArg(_current.Arg);
                customKeySpec.Key = CustomKeyRunner.Canonical(result.Combo);
                SetActionArg(customKeySpec.ToArg());
            }
            else
            {
                SetActionArg(result.Combo);
            }
        }
    }, null, Loc.T("录制按键"));
}

private void OnArgBoxCommit(object sender, RoutedEventArgs e)
{
    CommitKeyText();
}

private void OnArgBoxKeyDown(object sender, KeyEventArgs e)
{
    //IL_0001: Unknown result type (might be due to invalid IL or missing references)
    //IL_0007: Invalid comparison between Unknown and I4
    if ((int)e.Key == 6)
    {
        CommitKeyText();
        e.Handled = true;
    }
}

private void CommitKeyText()
{
    if (_host == null)
    {
        return;
    }
    if (_host.Actions.Get(_current.Action)?.Id != "customKey")
    {
        _current.Arg = ArgBox.Text;
        return;
    }
    string text = (ArgBox.Text ?? "").Trim();
    CustomKeySpec customKeySpec = CustomKeySpec.FromArg(_current.Arg);
    string canonical;
    string bad;
    if (text.Length == 0)
    {
        customKeySpec.Key = "";
        SetActionArg(customKeySpec.ToArg());
    }
    else if (!CustomKeyRunner.TryNormalize(text, out canonical, out bad))
    {
        _host.Notify(Loc.F("认不出「{0}」这个键，照着 ctrl+c 或 ctrl+v enter 这样写", bad));
        ArgBox.Text = customKeySpec.Key;
    }
    else
    {
        customKeySpec.Key = canonical;
        SetActionArg(customKeySpec.ToArg());
        ArgCaptureButton.Content = CustomKeyRunner.ComboDisplay(canonical);
    }
}

private void OnPickActionAppFile(object sender, RoutedEventArgs e)
{
    if (_host != null)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("窗口绑定用哪个程序"),
            Filter = Loc.T("程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            CheckFileExists = true
        };
        if (openFileDialog.ShowDialog() == true)
        {
            SetActionArg(openFileDialog.FileName);
        }
    }
}

private void OnPickActionAppWindow(object sender, RoutedEventArgs e)
{
    if (_host == null)
    {
        return;
    }
    WindowPickerWindow windowPickerWindow = new WindowPickerWindow();
    Window window = Window.GetWindow((DependencyObject)(object)this);
    if (window != null)
    {
        windowPickerWindow.Owner = window;
    }
    windowPickerWindow.ShowDialog();
    nint pickedWindow = windowPickerWindow.PickedWindow;
    if (pickedWindow != IntPtr.Zero)
    {
        var windowInfo = Core.Windows.WindowService.Describe(pickedWindow);
        if (windowInfo == null || string.IsNullOrEmpty(windowInfo.ExePath))
        {
            _host.Notify(Loc.T("取不到这个窗口的程序，改用「选择程序…」挑 exe"));
        }
        else
        {
            SetActionArg(windowInfo.ExePath);
        }
    }
}

private static string Describe(InputPlus.Core.Gestures.MouseGesture g)
{
    if (g.Kind == "hotcorner")
    {
        return Loc.F("屏幕{0} · 悬停触发", CornerName(g.Corner));
    }
    if (g.Kind == "edgewheel")
    {
        return Loc.F("屏幕{0} · 滚轮{1}", EdgeName(g.Edge), WheelName(g.Wheel));
    }
    string text = Loc.T(GestureButtons.Display(g.Button));
    if (!string.IsNullOrWhiteSpace(g.Chord))
    {
        text = Loc.F("{0} + {1}", text, Loc.T(GestureButtons.Display(g.Chord)));
    }
    if (g.Kind == "stroke")
    {
        g.EnsurePattern();
    }
    string kind = g.Kind;
    if (!(kind == "corner"))
    {
        if (kind == "wheel")
        {
            return Loc.F("{0} · 滚轮{1}", text, WheelName(g.Wheel));
        }
        return text;
    }
    return Loc.F("{0} · 屏幕{1}", text, CornerName(g.Corner));
}

private static string CornerName(int corner)
{
    return corner switch
    {
        1 => Loc.T("左上角"), 
        2 => Loc.T("右上角"), 
        3 => Loc.T("左下角"), 
        4 => Loc.T("右下角"), 
        _ => Loc.T("任意角落"), 
    };
}

private static string EdgeName(int edge)
{
    return edge switch
    {
        1 => Loc.T("上边缘"), 
        2 => Loc.T("右边缘"), 
        3 => Loc.T("下边缘"), 
        4 => Loc.T("左边缘"), 
        _ => Loc.T("任意边缘"), 
    };
}

private static string WheelName(int wheel)
{
    return wheel switch
    {
        -1 => Loc.T("向上"), 
        1 => Loc.T("向下"), 
        -2 => Loc.T("向左"), 
        2 => Loc.T("向右"), 
        _ => Loc.T("向上"), 
    };
}

private static string GlyphFor(InputPlus.Core.Gestures.MouseGesture g)
{
    switch (g.Kind)
    {
    case "corner":
    case "hotcorner":
        return "\ue7fc";
    case "wheel":
    case "edgewheel":
        return "\ue962";
    default:
        return "\ue962";
    }
}

private static string EdgeGlyph(InputPlus.Core.Gestures.MouseGesture g)
{
    string kind = g.Kind;
    if (!(kind == "hotcorner"))
    {
        if (kind == "edgewheel")
        {
            return g.Edge switch
            {
                1 => "▀", 
                2 => "▐", 
                3 => "▄", 
                4 => "▌", 
                _ => "▭", 
            };
        }
        return "\ue7fc";
    }
    return g.Corner switch
    {
        1 => "◤", 
        2 => "◥", 
        3 => "◣", 
        4 => "◢", 
        _ => "◇", 
    };
}

private static ImageSource EdgeIconFor(InputPlus.Core.Gestures.MouseGesture g)
{
    //IL_005a: Unknown result type (might be due to invalid IL or missing references)
    //IL_0190: Unknown result type (might be due to invalid IL or missing references)
    //IL_0195: Unknown result type (might be due to invalid IL or missing references)
    //IL_01c0: Unknown result type (might be due to invalid IL or missing references)
    //IL_01c5: Unknown result type (might be due to invalid IL or missing references)
    //IL_01ed: Unknown result type (might be due to invalid IL or missing references)
    //IL_01f2: Unknown result type (might be due to invalid IL or missing references)
    //IL_021a: Unknown result type (might be due to invalid IL or missing references)
    //IL_021f: Unknown result type (might be due to invalid IL or missing references)
    //IL_022a: Unknown result type (might be due to invalid IL or missing references)
    //IL_022c: Unknown result type (might be due to invalid IL or missing references)
    //IL_0223: Unknown result type (might be due to invalid IL or missing references)
    //IL_0228: Unknown result type (might be due to invalid IL or missing references)
    //IL_023f: Unknown result type (might be due to invalid IL or missing references)
    //IL_0108: Unknown result type (might be due to invalid IL or missing references)
    DrawingGroup drawingGroup = new DrawingGroup();
    Pen pen = new Pen(new SolidColorBrush(Color.FromRgb(148, 163, 184)), 1.2);
    drawingGroup.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(3.0, 3.0, 18.0, 14.0), 2.0, 2.0)));
    SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
    if (g.Kind == "hotcorner")
    {
        int corner = g.Corner;
        if (corner >= 1 && corner <= 4)
        {
            corner = g.Corner;
            bool flag = ((corner == 2 || corner == 4) ? true : false);
            double num = (flag ? 18 : 6);
            corner = g.Corner;
            flag = (uint)(corner - 3) <= 1u;
            double num2 = (flag ? 14 : 6);
            drawingGroup.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new Point(num, num2), 2.2, 2.2)));
            goto IL_0262;
        }
    }
    if (g.Kind == "edgewheel")
    {
        Rect rect = g.Edge switch
        {
            1 => new Rect(3.0, 2.4, 18.0, 2.6), 
            2 => new Rect(18.4, 3.0, 2.6, 14.0), 
            3 => new Rect(3.0, 15.0, 18.0, 2.6), 
            4 => new Rect(2.4, 3.0, 2.6, 14.0), 
            _ => Rect.Empty, 
        };
        if (!rect.IsEmpty)
        {
            drawingGroup.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(rect, 1.2, 1.2)));
        }
    }
    goto IL_0262;
    IL_0262:
    ((Freezable)drawingGroup).Freeze();
    return new DrawingImage(drawingGroup);
}

private static PointCollection? IconFor(InputPlus.Core.Gestures.MouseGesture g)
{
    //IL_018f: Unknown result type (might be due to invalid IL or missing references)
    if (g.Kind != "stroke" || !g.HasPath)
    {
        return null;
    }
    /* 缩略图画的是校正后的那一笔：和识别、回放用的是同一个形状。 */
    List<GestureMath.Pt> list = GestureMath.Resample(
        GestureMath.Normalize(InputPlus.Core.Gestures.StrokeCanonicalizer.Canonicalize(g.Points())), 24);
    if (list.Count < 2)
    {
        return null;
    }
    double num = list.Min((GestureMath.Pt p) => p.X);
    double num2 = list.Max((GestureMath.Pt p) => p.X);
    double num3 = list.Min((GestureMath.Pt p) => p.Y);
    double num4 = list.Max((GestureMath.Pt p) => p.Y);
    double num5 = Math.Max(1.0, num2 - num);
    double num6 = Math.Max(1.0, num4 - num3);
    double num7 = Math.Min(19.0 / num5, 19.0 / num6);
    double num8 = (22.0 - num5 * num7) / 2.0;
    double num9 = (22.0 - num6 * num7) / 2.0;
    PointCollection pointCollection = new PointCollection(list.Count);
    foreach (GestureMath.Pt item in list)
    {
        pointCollection.Add(new Point(num8 + (item.X - num) * num7, num9 + (item.Y - num3) * num7));
    }
    return pointCollection;
}

    // ───────────────────────── 选项 ─────────────────────────

    /// <summary>ComboBox.Base 模板里给内容让出的横向空间：ContentPresenter 的 12 + 32（箭头），外加边框 2。</summary>
    private const double ComboChromeWidth = 44 + 2;

    /// <summary>搜索按钮的宽（Button.Icon 默认 32）与它和下拉之间的间距，搜索面板要盖住这两样。</summary>
    private const double SearchButtonSlot = 6 + 32;

    /// <summary>
    /// 按内容给「全部按键」那颗筛选下拉定宽。
    ///
    /// 以前写死 96：中文「全部按键」四个字在模板里被左右让出 46px 之后正好差一点，
    /// 任何一项都会被裁掉半个字；换英文/德文更糟。这里把每个选项量一遍取最宽，
    /// 加模板占位再留 6px 余量，下限仍是 96 —— 只为「全部按键」这种短词撑住基本观感。
    /// </summary>
    private void FitFilterBoxWidth()
    {
        var typeface = new Typeface(ButtonFilterBox.FontFamily, ButtonFilterBox.FontStyle,
            ButtonFilterBox.FontWeight, ButtonFilterBox.FontStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double widest = 0;
        foreach (var item in ButtonFilterBox.Items.OfType<Choice>())
        {
            var ft = new FormattedText(item.Text ?? "", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, ButtonFilterBox.FontSize,
                System.Windows.Media.Brushes.Black, pixelsPerDip);
            widest = Math.Max(widest, ft.WidthIncludingTrailingWhitespace);
        }

        /* 只给 MinWidth、不给 Width：ComboBox 自己会按最宽项量出 DesiredSize，
           写死 Width 等于又把"够不够"这件事钉死了。 */
        ButtonFilterBox.MinWidth = Math.Max(96, Math.Ceiling(widest) + ComboChromeWidth + 6);
        SyncSearchPanelWidth();
    }

    /// <summary>搜索面板正好盖住「筛选下拉 + 搜索按钮」，宽度跟着下拉走。</summary>
    private void SyncSearchPanelWidth()
    {
        double width = ButtonFilterBox.ActualWidth > 0 ? ButtonFilterBox.ActualWidth : ButtonFilterBox.MinWidth;
        SearchPanel.Width = width + SearchButtonSlot;
    }

    private void BuildChoices()
    {
        ButtonBox.ItemsSource = new List<Choice> { new("", Loc.T("不按键")) }
            .Concat(GestureButtons.All.Select(b => new Choice(b, Loc.T(GestureButtons.Display(b)))))
            .ToList();
        ButtonBox.DisplayMemberPath = "Text";
        ButtonBox.SelectedValuePath = "Value";

        /* 列表筛选：只按“起始键”分类，不改变手势本身。 */
        ButtonFilterBox.ItemsSource = new List<Choice> { new("", Loc.T("全部按键")) }
            .Concat(GestureButtons.All.Select(b => new Choice(b, Loc.T(GestureButtons.Display(b)))))
            .ToList();
        ButtonFilterBox.DisplayMemberPath = "Text";
        ButtonFilterBox.SelectedValuePath = "Value";
        SelectTag(ButtonFilterBox, "");
        FitFilterBoxWidth();

        ChordBox.ItemsSource = new List<Choice> { new("", Loc.T("不组合")) }
            .Concat(GestureButtons.All.Select(b => new Choice(b, Loc.T(GestureButtons.Display(b)))))
            .ToList();
        ChordBox.DisplayMemberPath = "Text";
        ChordBox.SelectedValuePath = "Value";

        KindBox.ItemsSource = new List<Choice>
        {
            new("stroke", Loc.T("画一笔")),
            new("corner", Loc.T("甩到角落单击")),
            new("wheel", Loc.T("按住 + 滚轮")),
            new("hotcorner", Loc.T("悬停到角落")),
            new("edgewheel", Loc.T("贴边滚轮")),
            new("edge", Loc.T("沿边移动")),
            new("steps", Loc.T("多步手势"))
        };
        KindBox.DisplayMemberPath = "Text";
        KindBox.SelectedValuePath = "Value";

        WheelBox.ItemsSource = new List<Choice>
        {
            new("-1", Loc.T("向上")),
            new("1", Loc.T("向下")),
            new("-2", Loc.T("向左")),
            new("2", Loc.T("向右"))
        };
        WheelBox.DisplayMemberPath = "Text";
        WheelBox.SelectedValuePath = "Value";

        CornerBox.ItemsSource = new List<Choice>
        {
            new("0", Loc.T("任意角落")),
            new("1", Loc.T("左上角")),
            new("2", Loc.T("右上角")),
            new("3", Loc.T("左下角")),
            new("4", Loc.T("右下角"))
        };
        CornerBox.DisplayMemberPath = "Text";
        CornerBox.SelectedValuePath = "Value";

        EdgeBox.ItemsSource = new List<Choice>
        {
            new("0", Loc.T("任意边缘")),
            new("1", Loc.T("上边缘")),
            new("2", Loc.T("右边缘")),
            new("3", Loc.T("下边缘")),
            new("4", Loc.T("左边缘"))
        };
        EdgeBox.DisplayMemberPath = "Text";
        EdgeBox.SelectedValuePath = "Value";

        /* 多步序列的三种下拉：滚轮 / 角落 / 边缘，选项与上面三行完全一致。 */
        StepWheelBox.ItemsSource = new List<Choice>
        {
            new("-1", Loc.T("向上")),
            new("1", Loc.T("向下")),
            new("-2", Loc.T("向左")),
            new("2", Loc.T("向右"))
        };
        StepWheelBox.DisplayMemberPath = "Text";
        StepWheelBox.SelectedValuePath = "Value";
        StepWheelBox.SelectedIndex = 1;

        StepCornerBox.ItemsSource = new List<Choice>
        {
            new("1", Loc.T("左上角")),
            new("2", Loc.T("右上角")),
            new("3", Loc.T("左下角")),
            new("4", Loc.T("右下角"))
        };
        StepCornerBox.DisplayMemberPath = "Text";
        StepCornerBox.SelectedValuePath = "Value";
        StepCornerBox.SelectedIndex = 0;

        StepEdgeBox.ItemsSource = new List<Choice>
        {
            new("1", Loc.T("上边缘")),
            new("2", Loc.T("右边缘")),
            new("3", Loc.T("下边缘")),
            new("4", Loc.T("左边缘"))
        };
        StepEdgeBox.DisplayMemberPath = "Text";
        StepEdgeBox.SelectedValuePath = "Value";
        StepEdgeBox.SelectedIndex = 1;

        var categories = _host!.Actions.Items
            .Where(i => !i.Hidden && i.Id != "doNothing")
            .Select(i => i.Category)
            .Distinct()
            .ToList();
        ActionCategoryBox.ItemsSource = categories
            .Select(c => new Choice(c, Loc.T(c)))
            .ToList();
        ActionCategoryBox.DisplayMemberPath = "Text";
        ActionCategoryBox.SelectedValuePath = "Value";

        if (ActionCategoryBox.Items.Count > 0) ActionCategoryBox.SelectedIndex = 0;
        BuildActionChoices(null);
    }

    /// <summary>
    /// “分类 + 功能”两级下拉：只列当前分类下的动作。
    ///
    /// <paramref name="selectId"/> 是"必须挂得住"的那一条 —— 老配置里存着
    /// 界面上不再单独列出的动作时（历史上 openUrl / runProgram 被隐藏过，
    /// 六个键码动作也重复过），也绝不能让下拉退回到第一项：
    /// 用户点一次保存，动作和参数就一起丢了，表现是"这个手势以前能用，现在没反应"。
    ///
    /// 以前的做法是把它塞进列表、标题后面加「（旧功能，建议换掉）」——
    /// 用户看得见却换不掉。现在的做法：
    ///   · 能对齐到它自己的分类就先把分类切过去（比如 runProgram → 系统功能）；
    ///   · 实在对不上（真·隐藏动作）就以**本名**挂在当前分类里，功能一样能选能存。
    /// 启动时 LegacyActionMigration 会把这类动作换成正式动作，正常配置走不到第二条。
    /// </summary>
    private void BuildActionChoices(string? selectId)
    {
        if (_host == null) return;
        string category = SelectedValue(ActionCategoryBox) ?? "";

        _rebuilding = true;
        try
        {
            /* 先把分类对齐到这条动作自己的分类。
             * 只在"当前分类里确实没有它"时才切，否则用户自己点分类会被弹回去。 */
            if (!string.IsNullOrEmpty(selectId) && !HasVisibleAction(category, selectId!))
            {
                var info = _host.Actions.Get(selectId!);
                if (info != null && info.Category != category && CategoryVisible(info.Category))
                {
                    SelectTag(ActionCategoryBox, info.Category);
                    category = info.Category;
                }
            }

            var actions = _host.Actions.Items
                .Where(i => !i.Hidden && i.Id != "doNothing" && i.Category == category)
                .Select(i => new Choice(i.Id, i.Title))
                .ToList();

            // 真·隐藏动作（界面上没有等价项）：以本名挂住，不写任何"旧功能"字样
            if (!string.IsNullOrEmpty(selectId) && !actions.Any(a => a.Value == selectId))
            {
                var hidden = _host.Actions.Get(selectId!);
                if (hidden != null) actions.Insert(0, new Choice(hidden.Id, hidden.Title));
            }

            ActionBox.ItemsSource = actions;
            ActionBox.DisplayMemberPath = "Text";
            ActionBox.SelectedValuePath = "Value";
            if (!string.IsNullOrEmpty(selectId) && actions.Any(a => a.Value == selectId))
                SelectTag(ActionBox, selectId!);
            else if (actions.Count > 0)
                ActionBox.SelectedIndex = 0;
        }
        finally { _rebuilding = false; }
    }

    /// <summary>某个分类下有没有这条"看得见"的动作。</summary>
    private bool HasVisibleAction(string category, string actionId)
        => _host!.Actions.Items.Any(i => !i.Hidden && i.Id != "doNothing"
                                         && i.Category == category &&
                                         string.Equals(i.Id, actionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>分类下拉里有没有这一项（隐藏动作所属的旧分类没有）。</summary>
    private bool CategoryVisible(string category)
        => ActionCategoryBox.Items.OfType<Choice>()
            .Any(c => string.Equals(c.Value, category, StringComparison.Ordinal));

    /// <summary>按动作反查分类，先把分类切过去，再填功能下拉。</summary>
    private void SelectAction(string? actionId)
    {
        string category = _host?.Actions.Get(actionId ?? "")?.Category ?? "";
        if (string.IsNullOrEmpty(category))
            category = SelectedValue(ActionCategoryBox) ?? "";
        SelectTag(ActionCategoryBox, category);
        BuildActionChoices(actionId);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        LoadList(selectId: _current.Id);
    }

    /* ─────────────────── 搜索：放大镜 ↔ 输入框 ───────────────────
     *
     * 平时这块地方只占 32 像素（一个放大镜按钮），点开才换成输入框，
     * 这样列表能多出一整行的宽度；收起时不丢已有的字——失焦只在没字时收。 */

    private void OnToggleSearch(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        // 三件东西占同一个格子：展开时把「按键筛选 + 放大镜」换成输入框，宽度不变，标题不抖。
        ButtonFilterBox.Visibility = Visibility.Collapsed;
        SearchButton.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Visible;
        // 刚由 Collapsed 变 Visible 时布局还没跑，直接 Focus 会落空，等一轮输入再抢焦点。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FilterBox.Focus();
            FilterBox.CaretIndex = FilterBox.Text.Length;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseSearch()
    {
        if (_loading) return;
        SearchPanel.Visibility = Visibility.Collapsed;
        ButtonFilterBox.Visibility = Visibility.Visible;
        SearchButton.Visibility = Visibility.Visible;
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true;
        // Esc 一回到底：先清字（TextChanged 会把列表还原），再收成放大镜。
        if (FilterBox.Text.Length > 0) FilterBox.Clear();
        CloseSearch();
        SearchButton.Focus();
    }

    private void OnFilterLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (FilterBox.Text.Length == 0) CloseSearch();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        FilterBox.Clear();
        FilterBox.Focus();
    }

    private void OnButtonFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ButtonFilterBox.SelectedItem == null) return;
        LoadList(selectId: _current.Id);
    }

    /// <summary>排序按钮只有图标，点它才把四档菜单摊在按钮下面。</summary>
    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        UpdateSortChecks();
        if (SortButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = SortButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>选中一档：自定义顺序之外都只影响显示，不改 <c>Settings.Gestures</c> 的物理顺序。</summary>
    private void OnSortPick(object sender, RoutedEventArgs e)
    {
        string mode = GestureListOrder.Normalize((sender as MenuItem)?.Tag as string);
        UpdateSortChecks();
        if (_loading || string.Equals(mode, _sortMode, StringComparison.Ordinal)) return;
        _sortMode = mode;
        _host!.Settings.GestureOptions.ListSort = mode;
        _host.SaveSettings();
        LoadList(selectId: _current.Id);
    }

    /// <summary>把勾打到当前档上（<see cref="MenuItem"/> 自己也会翻，所以点完必须再刷一次）。</summary>
    private void UpdateSortChecks()
    {
        if (SortMenu is null) return;
        foreach (MenuItem item in SortMenu.Items.OfType<MenuItem>())
            item.IsChecked = string.Equals(GestureListOrder.Normalize(item.Tag as string), _sortMode,
                                           StringComparison.Ordinal);
    }

    /// <summary>排序档位的中文名，出图/自检回报用。</summary>
    private static string SortLabel(string mode) => GestureListOrder.Normalize(mode) switch
    {
        GestureListOrder.ByName => Loc.T("按名称"),
        GestureListOrder.ByTrigger => Loc.T("按触发方式"),
        GestureListOrder.ByAction => Loc.T("按动作"),
        _ => Loc.T("自定义顺序"),
    };

    /* ─────────────────── 拖动排序（只在「自定义顺序」下） ───────────────────
     *
     * 没有手柄了：按住一条手势超过 2 秒才进拖动，短按仍然是"选中这一条"。
     * 计时到点之后鼠标交给 GestureList 捕获：
     * 行的 Button 就收不到抬起事件，拖动过程不会顺手把那一行"选中"（右侧编辑器不跳）。
     *
     * 落点是"插到第几条之前"——每一行的上半/下半分界当插入位，
     * 松手时交给 GestureListOrder.MoveWithin 只重排看得见的这几条，
     * 别的应用/分组的手势在整表里的位置一点不动。 */

    /// <summary>
    /// 按住多久才认定"要拖"，不是"要看"。
    ///
    /// 2 秒是 v1.6.3 的取值，实际用下来大多数人按到一半就放弃了，还以为是功能坏了。
    /// 400ms 与"长按"的直觉接近，又比双击间隔（500ms）短一点，不会和"点选"打架 ——
    /// 真正的误触保护交给下面那条 8px 的位移阈值。
    /// </summary>
    private static readonly TimeSpan DragHoldDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>按住之后纵向挪动超过这么多像素，立刻开始拖（不必等计时器）。</summary>
    private const double DragStartMove = 8.0;

    private void OnRowPressDown(object sender, MouseButtonEventArgs e)
    {
        // 刻意不标 e.Handled：这一次按下也可能只是一次普通点击，行按钮得照常走完点击流程。
        if (_loading) return;
        if (sender is not Button button || button.DataContext is not GestureRow row || !row.Draggable) return;

        CancelPress();   // 连点两行时，上一棵计时还没收官
        _suppressClick = false;   // 新的一次按下：这一下该有的点击照常给
        _pressButton = button;
        _dragStart = e.GetPosition(ListScroll);

        _pressTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Input)
        {
            Interval = DragHoldDelay,
        };
        _pressTimer.Tick += OnPressHold;
        _pressTimer.Start();
    }

    /// <summary>按住到点：进拖动。中途松手 / 行丢了捕获都会把表停掉（见 OnRowPressUp）。</summary>
    private void OnPressHold(object? sender, EventArgs e)
    {
        var button = _pressButton;
        CancelPress();
        // 这 400ms 里鼠标可能已经在别处松开了（比如切换窗口时抬起事件丢了），这里再确认一次。
        if (button == null || System.Windows.Input.Mouse.LeftButton != MouseButtonState.Pressed) return;
        if (button.DataContext is not GestureRow row) return;
        StartDrag(button, row);
    }

    /// <summary>
    /// 按住的同时已经在往上/下挪：直接开始拖，不用等计时器。
    ///
    /// 长按 400ms 已经不长，但"我想拖"的意图是在手指动的那一瞬就有的；
    /// 这里跟手势识别的习惯一致 —— 按住 + 移动 = 拖，按住不动才是等其他判定。
    /// </summary>
    private void OnRowPressMove(object sender, MouseEventArgs e)
    {
        if (_dragging || _pressButton == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelPress(); return; }

        double dy = Math.Abs(e.GetPosition(ListScroll).Y - _dragStart.Y);
        if (dy < DragStartMove) return;

        var button = _pressButton;
        CancelPress();
        if (button?.DataContext is GestureRow row) StartDrag(button, row);
    }

    /// <summary>把这一行拎起来（轻微放大 + 影子 + 半透明），鼠标交给列表接着跟手。</summary>
    private void StartDrag(Button button, GestureRow row)
    {
        /* 排序不是「自定义顺序」时，先切过来再拖。
         * 以前这种档位下 Draggable=false，按住半天毫无反应 ——
         * 而"把一条手势往下拖"本身就说明他想自己排顺序。
         * 切换会重建整个列表，所以要按手势对象把新的行和按钮找回来。 */
        if (!GestureListOrder.IsCustom(_sortMode) && !SwitchToCustomForDrag(row.Gesture, out button, out row))
            return;

        int from = _rows.IndexOf(row);
        if (from < 0 || _rows.Count < 2) return;

        _dragging = true;
        _dragItem = row.Gesture;
        _dragRow = row;
        _dragButton = button;
        _dragFrom = from;
        _dropIndex = -1;
        _dragScrollStart = ListScroll.VerticalOffset;

        /* 浮起来的观感。别直接给 button.Opacity 赋值：它绑着 RowOpacity（停用的行本来就 0.48），
           本地赋值会把那条绑定顶掉，复位之后再也变不回停用行的样子。
           这里用动画占住 Opacity，复位时把动画清掉，绑定值自己就回来了。 */
        _dragShift = new TranslateTransform();
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1.02, 1.02), _dragShift } };
        button.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.3,
        };
        button.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.HoldEnd });

        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeNS;

        /* 鼠标交给列表：行按钮收不到抬起事件，拖动过程就不会顺手"选中"这一行。
         *
         * 但**不能依赖这一次捕获**：Buttons 自己拿着捕获，某些情况下
         * CaptureMouse 会返回 false。捕获留在按钮上其实也能拖 ——
         * 鼠标事件会从按钮冒泡到列表，OnDragMove / OnDragUp 照样收得到，
         * 所以这里只是"优先转移"，失败就保持原样，绝不让拖动因为捕获失败而没反应。 */
        if (!GestureList.CaptureMouse()) button.CaptureMouse();

        // 松手时按钮可能还是会发一次 Click（捕获没转走的情况下），这一下不算点选。
        _suppressClick = true;
        CoreLog.Info($"手势拖动：开始 {row.Title}（第 {from + 1} 条，排序={_sortMode}）");
        ShowDropLine(from);
    }

    /// <summary>
    /// 把排序档切到「自定义顺序」，并按手势把新的行和行按钮找回来。
    ///
    /// 切换会 <see cref="LoadList"/> 重建整个列表（行对象、按钮、可视树全是新的），
    /// 所以调用方手里那份 button/row 会失效 —— 这里负责换新的。
    /// 失败就返回 false，调用方放弃这一次拖动（宁可不拖，也不能拖动一个已经不在列表里的行）。
    /// </summary>
    private bool SwitchToCustomForDrag(InputPlus.Core.Gestures.MouseGesture gesture,
        out Button button, out GestureRow row)
    {
        button = null!;
        row = null!;
        if (_host is null) return false;

        try
        {
            string from = _sortMode;
            _sortMode = GestureListOrder.Custom;
            _host.Settings.GestureOptions.ListSort = _sortMode;
            _host.SaveSettings();
            UpdateSortChecks();
            CoreLog.Info("手势拖动：排序从「" + SortLabel(from) + "」切到「自定义顺序」");
        }
        catch (Exception ex)
        {
            CoreLog.Warn("手势拖动：切到自定义顺序失败，" + ex.Message);
            return false;
        }

        LoadList(selectId: gesture.Id);
        GestureList.UpdateLayout();   // 容器是懒生成的，量一次才会站出来

        int index = _rows.FindIndex(r => string.Equals(r.Gesture.Id, gesture.Id, StringComparison.Ordinal));
        if (index < 0) return false;
        if (GestureList.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container) return false;
        if (FindRowButton(container) is not { } found) return false;

        button = found;
        row = _rows[index];
        return true;
    }

    /// <summary>在可视树里找第一个 Button（行模板的根就是那颗按钮）。</summary>
    private static Button? FindRowButton(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is Button button) return button;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindRowButton(VisualTreeHelper.GetChild(root, i)) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>短按松手：那一颗还是普通点击（选中），这里只把计时收掉。</summary>
    private void OnRowPressUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) return;   // 已经在拖了，鼠标在列表手里，这条抬起事件轮不到行
        CancelPress();
    }

    /// <summary>按下之后丢了捕获（切窗口、被别的控件抢走）：别在半秒后凭空开始拖。</summary>
    private void OnRowPressLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragging) return;   // 进拖动的那一下行会丢捕获（鼠标转给列表），不能当成松手
        CancelPress();
    }

    private void CancelPress()
    {
        if (_pressTimer != null)
        {
            _pressTimer.Stop();
            _pressTimer = null;
        }
        _pressButton = null;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // 抬起丢了（切了窗口）也要把拖动收干净
            FinishDrag(null);
            return;
        }
        var p = e.GetPosition(ListScroll);

        // 跟手：条目跟着指针上下走。列表自动滚动时指针在视口里没动、内容动了，
        // 得把滚动量补回去，否则条目会从指头底下溜走。
        if (_dragShift != null)
            _dragShift.Y = (p.Y - _dragStart.Y) + (ListScroll.VerticalOffset - _dragScrollStart);

        // 拖到上下边缘先滚一截：列表长的目标（全局 60 多条）不然根本拖不到头
        if (p.Y < 26) ListScroll.ScrollToVerticalOffset(Math.Max(0, ListScroll.VerticalOffset - 16));
        else if (p.Y > ListScroll.ActualHeight - 26)
            ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset + 16);

        ShowDropLine(DropIndexAt(p));
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem == null) return;
        FinishDrag(e.GetPosition(ListScroll));
        e.Handled = true;
    }

    private void OnDragLostCapture(object sender, MouseEventArgs e)
    {
        /* LostMouseCapture 是冒泡事件，这是踩过的坑（v1.6.4 之前按住拖拽完全没反应）：
         * StartDrag 把捕获从行按钮转给列表的那一瞬间，按钮"丢捕获"的那一下也会冒到这里，
         * 于是刚立起来的拖动被当成"捕获丢了"立刻收掉 —— 看上去就是按住半天毫无反应，
         * 只在列表顶上留一根插入线。真正的"列表自己丢了捕获"才是要收尾的那种。 */
        if (!ReferenceEquals(e.OriginalSource, GestureList)) return;
        if (GestureList.IsMouseCaptured) return;
        if (_dragItem != null) FinishDrag(null);
    }

    /// <summary>
    /// 一行的顶边在列表里的 y。
    /// 被拎起来的那一行带着 RenderTransform（已经跟着指针跑了），
    /// 量插入位要的是它在布局里的原位，所以把位移减回去。
    /// </summary>
    private double RowTop(FrameworkElement container)
    {
        double top;
        try { top = container.TransformToAncestor(ListScroll).Transform(new Point(0, 0)).Y; }
        catch (InvalidOperationException) { return double.NaN; }   // 行不在可视树里（被虚拟化掉了）
        if (_dragShift != null && container is ContentPresenter { Content: GestureRow row }
            && ReferenceEquals(row, _dragRow))
            top -= _dragShift.Y;
        return top;
    }

    /// <summary>指针落在哪两行之间：返回"插到第几条之前"，等于行数就是插到最末尾。</summary>
    private int DropIndexAt(Point p)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (GestureList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            double top = RowTop(c);
            if (double.IsNaN(top)) continue;
            if (p.Y < top + c.ActualHeight / 2) return i;
        }
        return _rows.Count;
    }

    private void ShowDropLine(int index)
    {
        _dropIndex = index;
        if (_rows.Count == 0)
        {
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }

        int probe = Math.Min(index, _rows.Count - 1);
        double y = 0;
        if (GestureList.ItemContainerGenerator.ContainerFromIndex(probe) is FrameworkElement c)
        {
            double top = RowTop(c);
            if (!double.IsNaN(top)) y = index >= _rows.Count ? top + c.ActualHeight : top;
        }

        DropLine.Width = Math.Max(0, ListScroll.ViewportWidth - 6);
        Canvas.SetLeft(DropLine, 0);
        Canvas.SetTop(DropLine, Math.Max(0, y - 1));
        DropLine.Visibility = Visibility.Visible;
    }

    private void FinishDrag(Point? dropAt)
    {
        var item = _dragItem;
        int from = _dragFrom;
        int insertBefore = dropAt.HasValue ? DropIndexAt(dropAt.Value) : _dropIndex;
        ResetDragState();

        if (item == null || from < 0 || insertBefore < 0) return;
        // 插到自己前面、或者紧跟在自己后面 = 位置没变，不用动表也不用重画
        if (insertBefore == from || insertBefore == from + 1) return;

        var visible = _rows.Select(r => r.Gesture).ToList();
        int landed = GestureListOrder.MoveWithin(_host!.Settings.Gestures, visible, from, insertBefore);
        if (landed < 0) return;

        double offset = ListScroll.VerticalOffset;
        _host.SaveSettings();
        LoadList(selectId: item.Id);
        // LoadList 会回到顶部（列表整块重建），拖完还站在原地才不晕
        ListScroll.ScrollToVerticalOffset(offset);
    }

    private void ResetDragState()
    {
        CancelPress();
        if (_dragButton is { } button)
        {
            button.BeginAnimation(UIElement.OpacityProperty, null);   // 动画摘掉，退回 RowOpacity 那条绑定
            button.RenderTransform = null;
            button.Effect = null;
        }
        _dragItem = null;
        _dragRow = null;
        _dragButton = null;
        _dragShift = null;
        _dragFrom = -1;
        _dropIndex = -1;
        _dragging = false;
        DropLine.Visibility = Visibility.Collapsed;
        System.Windows.Input.Mouse.OverrideCursor = null;
        if (GestureList.IsMouseCaptured) GestureList.ReleaseMouseCapture();
    }

    /// <summary>出图用：切到某一档排序并回报行数/可拖动行数（渲染时不动设置）。</summary>
    internal string[] SetSortForRender(string mode)
    {
        _loading = true;
        try
        {
            _sortMode = GestureListOrder.Normalize(mode);
            UpdateSortChecks();
        }
        finally { _loading = false; }

        LoadList(selectId: _current.Id);
        return new[]
        {
            "排序：" + SortLabel(_sortMode),
            "这一档行数：" + _rows.Count + "（全表 " + _host!.Settings.Gestures.Count + " 条）",
            "可拖动行：" + _rows.Count(r => r.Draggable) + " 行",
            "前四行：" + string.Join(" | ", _rows.Take(4).Select(r => r.Title)),
            // 边缘手势那一栏的账也顺手报一遍：整表导入会把四角悬停/贴边滚轮冲掉，
            // 只看全局行数是发现不了的（它们不在这一档里）。
            "边角：" + string.Join(" / ", _host!.Settings.Gestures
                .GroupBy(g => g.Kind)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Key + " " + x.Count())),
            "四角悬停：" + string.Join(" ", _host.Settings.Gestures
                .Where(g => g.Kind == "hotcorner").Select(g => g.Corner).OrderBy(c => c)),
        };
    }

    private void OnPickGesture(object sender, RoutedEventArgs e)
    {
        /* 刚拖完放下的那一下不算点选：否则右侧编辑器会被切到这条上，
           要是它正好是当前选中的，编辑器里没保存的改动还会被冲掉。 */
        if (_suppressClick)
        {
            _suppressClick = false;
            return;
        }
        if ((sender as FrameworkElement)?.DataContext is not GestureRow row) return;
        _current = row.Gesture.Clone();
        LoadEditor();
    }

    private void OnEditorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateKindRows();
    }

    private void OnActionCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _rebuilding) return;
        BuildActionChoices(null);
        _current.Action = SelectedValue(ActionBox) ?? "";
        UpdateArgVisibility();
    }

    private void OnActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _rebuilding) return;
        string old = _current.Action;
        _current.Action = SelectedValue(ActionBox) ?? "";
        if (!string.Equals(old, _current.Action, StringComparison.OrdinalIgnoreCase))
        {
            // 换功能就清掉旧参数：窗口绑定留下上一个按键序列、
            // 或 sendKeys 留下上一段路径，都会让用户看不懂。
            _current.Arg = "";
            ArgBox.Text = "";
            ArgBox.Tag = null;
        }
        UpdateArgVisibility();
    }

    /// <summary>动作搜索：跨分类找功能，点结果直接选中，不用先翻分类。</summary>
    private void OnActionSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _host == null) return;

        string query = ActionSearchBox.Text.Trim();
        ActionSearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActionSearchClear.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (query.Length == 0)
        {
            ActionSearchList.ItemsSource = null;
            ActionSearchPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var hits = _host.Actions.Items
            .Where(i => !i.Hidden && i.Id != "doNothing")
            .Where(i => i.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || i.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || i.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(30)
            .Select(i => new Choice(i.Id, i.Category + " · " + i.Title))
            .ToList();
        ActionSearchList.ItemsSource = hits;
        ActionSearchPanel.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClearActionSearch(object sender, RoutedEventArgs e)
    {
        ActionSearchBox.Text = "";
        ActionSearchHint.Visibility = Visibility.Visible;
        ActionSearchClear.Visibility = Visibility.Collapsed;
        ActionSearchPanel.Visibility = Visibility.Collapsed;
        ActionSearchBox.Focus();
    }

    private void OnActionSearchPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ActionSearchList.SelectedItem is not Choice pick) return;

        SelectAction(pick.Value);
        _current.Action = pick.Value;
        UpdateArgVisibility();
        ActionSearchBox.Text = "";
        ActionSearchList.ItemsSource = null;
        ActionSearchList.SelectedItem = null;
        ActionSearchPanel.Visibility = Visibility.Collapsed;
    }

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        o.Enabled = EnabledBox.IsChecked == true;
        _host.SaveSettings();
    }


    // ─────────────────── WGestures 2 的「允许的触发方式」 ───────────────────

    private static readonly Brush PillOnBg = Frozen(Color.FromArgb(0x33, 0x22, 0xC5, 0x5E));
    private static readonly Brush PillOnFg = Frozen(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush PillOffBg = Frozen(Color.FromArgb(0x33, 0xEF, 0x44, 0x44));
    private static readonly Brush PillOffFg = Frozen(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush PillAutoBg = Frozen(Color.FromArgb(0x22, 0x9A, 0x9A, 0x9A));
    private static readonly Brush PillAutoFg = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 61 条触发方式的三态：启用 / 禁用 / 默认（跟随上层）。
    /// 和作用目标一样，存的是「相对上层的覆盖」，没有覆盖就走 TargetList 里的继承链。
    /// </summary>
    private void BuildTriggers()
    {
        if (_host == null) return;
        var target = TargetFor(_scope, create: false);
        var rows = new List<TriggerRow>(Wg2Triggers.All.Count);
        int on = 0, off = 0;
        foreach (string key in Wg2Triggers.All)
        {
            bool? state = target?.TriggerOf(key);
            if (state == true) on++;
            else if (state == false) off++;
            (string text, Brush bg, Brush fg, string tip) = state switch
            {
                true => (Loc.T("启用"), PillOnBg, PillOnFg, Loc.T("点一下改成禁用")),
                false => (Loc.T("禁用"), PillOffBg, PillOffFg, Loc.T("点一下改回默认")),
                _ => (Loc.T("默认"), PillAutoBg, PillAutoFg, Loc.T("点一下改成启用")),
            };
            rows.Add(new TriggerRow(key, Wg2Triggers.Label(key), text, bg, fg, tip));
        }
        TriggerList.ItemsSource = null;
        TriggerList.ItemsSource = rows;
        TriggerSummary.Text = on == 0 && off == 0
            ? Loc.T("全部跟随上层")
            /* 中列只有 500 上下，长句会被截成「禁用（」：压成两段短词。 */
            : Loc.F("启用 {0} · 禁用 {1}", on, off);
    }

    /// <summary>点一条：默认 → 启用 → 禁用 → 默认。</summary>
    private void OnTriggerClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not FrameworkElement el || el.Tag is not string key) return;
        var target = TargetFor(_scope, create: true);
        if (target == null) return;
        bool? next = target.TriggerOf(key) switch
        {
            null => true,
            true => false,
            _ => (bool?)null,
        };
        target.SetTrigger(key, next);
        _host.SaveSettings();
        BuildTriggers();
    }

    /// <summary>全部启用 / 全部禁用 / 全部默认。</summary>
    private void OnAllTriggers(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not FrameworkElement el) return;
        bool? value = (el.Tag as string) switch
        {
            "on" => true,
            "off" => false,
            _ => (bool?)null,
        };
        var target = TargetFor(_scope, create: value != null);
        if (target == null) return;
        foreach (string key in Wg2Triggers.All) target.SetTrigger(key, value);
        _host.SaveSettings();
        BuildTriggers();
    }

    /// <summary>
    /// 出图用：这一栏默认收起，展开一次才有人用眼睛验过 61 条胶囊的排版。
    /// </summary>
    internal void SetTriggersExpandedForRender(bool expanded)
    {
        TriggerExpander.IsExpanded = expanded;
        if (expanded)
        {
            TriggerList.UpdateLayout();
            TriggerExpander.UpdateLayout();
        }
    }

    /// <summary>
    /// 齿轮按钮：外观 / 参数 / 开关快捷键都在这个窗里。改动即时生效，关窗即可。
    /// </summary>
    /// <summary>当前开着的「手势设置」窗（同一时间只留一个）。</summary>
    private GestureSettingsWindow? _settingsWindow;

    private void OnOpenGestureSettings(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        /* 用 Show 而不是 ShowDialog：模态的时候点主窗只会"叮"一声，窗还赖在那儿；
         * 用户要的是"点别处就关"。非模态之后，GestureSettingsWindow 自己在
         * 失去焦点时判断是不是回到了主窗，是就关掉。 */
        if (_settingsWindow is { IsVisible: true } open)
        {
            open.Activate();
            return;
        }

        var dlg = new GestureSettingsWindow(_host) { Owner = Window.GetWindow(this) };
        _settingsWindow = dlg;
        dlg.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, dlg)) _settingsWindow = null;
        };
        dlg.Show();
    }
    /// <summary>
    /// 出图用：把第一个应用程序作用目标标成「已禁用」，拍一张确认胶囊、灰标题、浅红底都画出来了。
    /// 只改内存不落盘，拍完用 false 收回去。
    /// </summary>
    internal bool SetFirstAppDisabledForRender(bool disabled)
    {
        if (_host == null) return false;
        var options = _host.Settings.GestureOptions;
        options.DisabledTargets ??= new List<string>();
        var row = _targetRows.FirstOrDefault(r =>
            r.RowVisibility == Visibility.Visible && IsAppScope(r.Scope));
        if (row is null) return false;
        options.DisabledTargets.RemoveAll(p => SameTarget(p, row.Scope));
        if (disabled) options.DisabledTargets.Add(row.Scope);
        BuildTargets();
        UpdateLayout();
        return true;
    }

    /// <summary>
    /// 出图用：选中第一条用指定动作的手势，并把「分类 / 功能」两个下拉的实际情况读出来。
    ///
    /// 下拉弹层不在窗口的可截图范围里，所以列表内容当文本一起返回；
    /// 用来验"界面上再没有旧功能字样、动作换成了正式的那一条、参数框也显示出来"。
    /// </summary>
    internal string[] SelectActionForRender(string actionId)
    {
        if (_host == null) return Array.Empty<string>();
        var row = _rows.FirstOrDefault(r =>
            string.Equals(r.Gesture.Action, actionId, StringComparison.OrdinalIgnoreCase));
        if (row == null) return new[] { "没有用手势动作 " + actionId };

        _current = row.Gesture.Clone();
        LoadEditor();
        UpdateLayout();

        /* 把「分类 / 功能」那一段滚进可视区：默认停在顶部，
         * 截出来的图只有名称和触发方式，验不到下拉里到底写了什么。 */
        try
        {
            var sv = FindAncestorScroll(ArgBox);
            if (sv != null)
            {
                var pos = ArgBox.TransformToAncestor(sv).Transform(new Point(0, 0));
                sv.ScrollToVerticalOffset(sv.VerticalOffset + pos.Y - 120);
                sv.UpdateLayout();
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "出图：滚动到动作下拉"); }

        return new[]
        {
            "手势：" + _current.Name,
            "分类：" + (SelectedValue(ActionCategoryBox) ?? ""),
            "功能：" + (SelectedValue(ActionBox) ?? ""),
            "参数框：" + (ArgBox.Visibility == Visibility.Visible ? "显示" : "隐藏"),
            "下拉项：" + string.Join(" | ", ActionBox.Items.OfType<Choice>().Select(c => c.Text)),
        };
    }

    /// <summary>
    /// 出图：造一条「多步手势」并选中，让步骤清单（StepsRow）露出来。
    /// 只在 --render 的临时数据目录里跑，写完不存盘，也不会进用户的设置。
    /// </summary>
    internal string[] AddStepsSampleForRender()
    {
        if (_host == null) return Array.Empty<string>();

        var g = _host.Settings.Gestures.FirstOrDefault(x => x.Id == "rendersteps");
        if (g == null)
        {
            var raw = new List<GestureMath.Pt>
            {
                new(0, 0), new(2, -12), new(9, -28), new(20, -46),
            };
            g = new MouseGesture
            {
                Id = "rendersteps",
                Name = "画一笔再上滚",
                Kind = "steps",
                Button = "right",
                Action = "winMinimize",
                Steps =
                {
                    new GestureStep
                    {
                        Kind = "stroke",
                        Path = GestureMath.ToFlat(GestureMath.Normalize(raw)),
                    },
                    new GestureStep { Kind = "scroll", Wheel = -1 },
                },
            };
            _host.Settings.Gestures.Add(g);
        }

        LoadList(g.Id);
        _current = g.Clone();
        LoadEditor();
        UpdateLayout();

        /* 步骤清单在编辑卡的下半截，不滚过去的话截出来只有名称和触发方式。 */
        try
        {
            var sv = FindAncestorScroll(StepsRow);
            if (sv != null)
            {
                var pos = StepsRow.TransformToAncestor(sv).Transform(new Point(0, 0));
                sv.ScrollToVerticalOffset(sv.VerticalOffset + pos.Y - 320);
                sv.UpdateLayout();
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "出图：滚动到步骤清单"); }

        return new[]
        {
            "手势：" + _current.Name,
            "触发方式：" + (SelectedValue(KindBox) ?? ""),
            "步骤数：" + _steps.Count.ToString(),
            "步骤：" + string.Join(" | ", _steps.Select(s => s.Title())),
        };
    }

    /// <summary>往上找最近的 ScrollViewer（出图滚动用）。</summary>
    private static ScrollViewer? FindAncestorScroll(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ScrollViewer sv) return sv;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // ───────────────────────── 分组 ─────────────────────────

    private void OnNewGroup(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        string? name = TextPromptWindow.Ask(System.Windows.Window.GetWindow(this),
            Loc.T("新建分组"), Loc.T("给这一组程序起个名字"), Loc.T("新分组"));
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var list = _host.Settings.GestureOptions.Targets2();
        var group = new GestureTarget
        {
            Id = GroupPrefix + Guid.NewGuid().ToString("N"),
            Name = name,
            Members = new List<string>(),
        };
        list.Add(group);
        _host.SaveSettings();
        _scope = group.Id;
        BuildTargets();
        LoadList();
    }

    /// <summary>删除按钮的统一入口：分组退回全局，应用程序连手势一起删。</summary>
    private void OnDeleteTarget(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if ((sender as FrameworkElement)?.DataContext is not TargetRow row) return;

        if (IsAppScope(row.Scope))
        {
            DeleteAppTarget(row);
            e.Handled = true;
            return;
        }
        if (!IsGroupScope(row.Scope)) return;

        var options = _host.Settings.GestureOptions;
        var list = options.Targets2();
        int removed = list.RemoveAll(t => string.Equals(t.Id, row.Scope, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;

        /* 组里绑着的手势不能凭空消失：把它们退回全局，免得用户以为丢了。
           只改作用域，别的字段一律不动。 */
        foreach (var g in _host.Settings.Gestures)
            if (string.Equals(g.AppPath ?? "", row.Scope, StringComparison.OrdinalIgnoreCase))
                g.AppPath = "";

        if (string.Equals(_scope, row.Scope, StringComparison.OrdinalIgnoreCase)) _scope = "";
        _host.SaveSettings();
        BuildTargets();
        LoadList();
    }

    /// <summary>
    /// 删掉一个应用程序作用目标。
    /// 这一栏里的应用是「有手势」或「手动加进来的」，删干净必须四样一起清：
    /// 它名下的手势、作用目标条目、禁用标记、作用目标模型里那条 App。
    /// 手势数量先数出来写在确认框里，别让人点完才发现手势没了。
    /// </summary>
    private void DeleteAppTarget(TargetRow row)
    {
        var settings = _host!.Settings;
        var options = settings.GestureOptions;

        int count = settings.Gestures.Count(g => !IsEdgeGesture(g) && SameTarget(g.AppPath ?? "", row.Scope));
        string message = count > 0
            ? Loc.F("删除「{0}」这个作用目标？它下面的 {1} 条手势会一起删掉。", row.Title, count)
            : Loc.F("删除「{0}」这个作用目标？", row.Title);
        if (!AppDialog.ConfirmDanger(System.Windows.Window.GetWindow(this), message, Loc.T("删除作用目标")))
            return;

        settings.Gestures.RemoveAll(g => !IsEdgeGesture(g) && SameTarget(g.AppPath ?? "", row.Scope));
        options.Targets.RemoveAll(p => SameTarget(p, row.Scope));
        (options.DisabledTargets ??= new List<string>()).RemoveAll(p => SameTarget(p, row.Scope));
        options.Targets2().RemoveAll(t => t.Kind == GestureTargetKind.App && SameTarget(t.Id, row.Scope));

        if (string.Equals(_scope, row.Scope, StringComparison.OrdinalIgnoreCase)) _scope = "";
        _host.SaveSettings();
        BuildTargets();
        LoadList();
        _host.Notify(Loc.F("已删除作用目标「{0}」", row.Title));
    }


    // ───────────────────────── 编辑 ─────────────────────────

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        /* 新建时给个好用的默认值：否则动作下拉是空的，用户画完一笔点保存
         * 只会收到「先给手势选一个动作」，看起来就像"加不进去"。 */
        _current = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = Loc.T("新手势"),
            Button = _scope == EdgeTargetKey ? "" : "right",
            Kind = _scope == EdgeTargetKey ? "hotcorner" : "stroke",
            Corner = _scope == EdgeTargetKey ? 1 : 0,
            Action = _scope == EdgeTargetKey ? "switchNextApp" : "customKey",
            AppPath = _scope == EdgeTargetKey ? "" : _scope,
            Enabled = true,
        };
        LoadEditor();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var g = _current;
        g.Name = NameBox.Text.Trim();
        g.Kind = SelectedTag(KindBox) ?? "stroke";
        /* 多步序列跟着保存 —— 它是这条手势的主内容，不是附加参数。 */
        g.Steps = _steps.Select(s => s.Clone()).ToList();
        bool buttonless = g.Kind is "hotcorner" or "edgewheel";
        g.Button = buttonless ? "" : (SelectedValue(ButtonBox) ?? "");
        g.Chord = buttonless ? "" : (SelectedTag(ChordBox) ?? "");
        if (string.Equals(g.Chord, g.Button, StringComparison.OrdinalIgnoreCase)) g.Chord = "";
        g.Wheel = (g.Kind is "wheel" or "edgewheel") && int.TryParse(SelectedValue(WheelBox), out int w) ? w : 0;
        g.Corner = (g.Kind is "corner" or "wheel" or "hotcorner") && int.TryParse(SelectedValue(CornerBox), out int c) ? c : 0;
        g.Edge = g.Kind is "edgewheel" or "edge" && int.TryParse(SelectedValue(EdgeBox), out int edge) ? edge : 0;
        g.Enabled = GestureEnabledBox.IsChecked == true;
        g.Immediate = g.Kind == "stroke" && ImmediateBox.IsChecked == true;
        g.HideLabel = HideLabelBox.IsChecked == true;
        string pickedAction = SelectedValue(ActionBox) ?? "";
        /* 下拉框没选上东西时不要写空：宁可保留原来的动作，
         * 也不能把一条配好的手势改成"没有功能"。 */
        if (!string.IsNullOrEmpty(pickedAction)) g.Action = pickedAction;
        var actionInfo = _host.Actions.Get(g.Action);
        /* 不需要参数的动作（尤其是「直接搜索」）保留旧 Arg 会把上一次的
         * 参数当成搜索词，导致“不搜索选中的文字”。保存时统一清干净。
         * customKey 的 NeedsArg 虽然是 false，但 Arg 里是自定义按键 JSON。 */
        if (actionInfo?.Id == "customKey")
        {
            /* 文本框里是「ctrl+c」这种键文本，落盘要的是 JSON。
             * 保存前再规范化一次：用户可能没失焦就直接点了保存。 */
            var spec = CustomKeySpec.FromArg(g.Arg);
            spec.Key = CustomKeyRunner.Canonical(ArgBox.Text);
            g.Arg = spec.ToArg();
        }
        else if (actionInfo?.NeedsArg == true)
            g.Arg = ArgBox.Text;
        else if (actionInfo?.Hidden != true)
            g.Arg = "";
        /* 隐藏的兼容动作（如旧 id openFolder）目标就写在 Arg 里，参数框本来不显示，
         * 这里保留原值 —— 否则保存一次就把命令清空了。 */
        /* 限定程序不再在手势里选：由左边选中的「作用目标」决定，
         * 编辑时原样保留，避免把应用手势改成全局手势。 */
        g.AppPath = _current.AppPath ?? "";

        if (string.IsNullOrWhiteSpace(g.Action))
        {
            _host.Notify(Loc.T("先给手势选一个动作"));
            return;
        }
        if (g.Action == "winBind" && string.IsNullOrWhiteSpace(g.Arg))
        {
            _host.Notify(Loc.T("窗口绑定要先选一个程序"));
            return;
        }
        if (g.Kind == "stroke" && !g.HasPath)
        {
            _host.Notify(Loc.T("还没录制笔画 —— 点「按住鼠标键画一笔」"));
            return;
        }
        if (g.Kind == "hotcorner" && g.Corner is < 1 or > 4)
        {
            _host.Notify(Loc.T("悬停到角落要选一个具体角落"));
            return;
        }
        if (g.Kind == "edgewheel" && g.Edge is < 1 or > 4)
        {
            _host.Notify(Loc.T("贴边滚轮要选一条屏幕边缘"));
            return;
        }
        if (g.Kind == "steps" && g.Steps.Count < 2)
        {
            _host.Notify(Loc.T("多步手势至少要两步 —— 先往下面的清单里加两步"));
            return;
        }
        if (g.Kind == "steps" && string.IsNullOrEmpty(g.Button))
        {
            _host.Notify(Loc.T("多步手势要选一个起始键（按住它一路做完）"));
            return;
        }
        if (g.Kind is "stroke" or "corner" && string.IsNullOrEmpty(g.Button))
        {
            _host.Notify(Loc.T("这个触发方式要选一个起始键"));
            return;
        }

        var duplicate = GestureIdentity.Find(_host.Settings.Gestures, g, g.Id);
        if (duplicate != null)
        {
            string title = (string.IsNullOrWhiteSpace(duplicate.Name)
                ? ActionCatalog.TitleOf(duplicate.Action)
                : duplicate.Name) ?? "";
            _host.Notify(Loc.F("这个手势已经绑定了「{0}」，不能重复", title));
            return;
        }

        /* 方向串不同但形状很像时仍然允许保存 —— 有些用户就是想要两个相近手势。
         * 但得提醒一句：识别时它们可能互相抢，尤其是画 Q 这种圆+尾巴的形状。 */
        var (similar, score) = GestureIdentity.FindSimilar(_host.Settings.Gestures, g, 0.10, g.Id);
        if (similar != null)
        {
            string title = (string.IsNullOrWhiteSpace(similar.Name)
                ? ActionCatalog.TitleOf(similar.Action)
                : similar.Name) ?? "";
            _host.Notify(Loc.F("和「{0}」很像（相似度 {1:0}%），识别时可能互相抢",
                title, (1 - score) * 100));
        }

        var existing = _host.Settings.Gestures.FirstOrDefault(x => x.Id == g.Id);
        if (existing == null)
        {
            _host.Settings.Gestures.Add(g.Clone());
        }
        else
        {
            existing.CopyFrom(g);
        }

        _host.SaveSettings();
        _scope = IsEdgeGesture(g) ? EdgeTargetKey : (g.AppPath ?? "");
        BuildTargets();
        LoadList(selectId: g.Id);
        _host.Notify(Loc.T("手势已保存"));
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var existing = _host.Settings.Gestures.FirstOrDefault(x => x.Id == _current.Id);
        if (existing == null) return;
        _host.Settings.Gestures.Remove(existing);
        _host.SaveSettings();
        _current = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Kind = _scope == EdgeTargetKey ? "hotcorner" : "stroke",
            Button = _scope == EdgeTargetKey ? "" : GestureButtons.Right,
            Corner = _scope == EdgeTargetKey ? 1 : 0,
            Action = _scope == EdgeTargetKey ? "switchNextApp" : "",
            AppPath = _scope == EdgeTargetKey ? "" : _scope,
        };
        BuildTargets();
        LoadList(selectId: "");
        _host.Notify(Loc.T("手势已删除"));
    }


    /// <summary>
    /// 录制：弹一个专门的窗口让用户在里面画，窗口自己收鼠标事件。
    ///
    /// 不用全局钩子录，是因为"设置页里点一下按钮 → 去别的窗口上画"这条路
    /// 一遇到焦点变化就断；在自家窗口里画，按的是哪颗键、笔画什么样，全都确定。
    /// </summary>
    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new UI.Windows.GestureRecordWindow
        {
            Owner = Window.GetWindow(this),
            Options = _host.Settings.GestureOptions,
        };

        bool accepted;
        _host.Gestures.Suspended = true;
        try { accepted = dlg.ShowDialog() == true; }
        finally { _host.Gestures.Suspended = false; }

        if (!accepted || dlg.Result == null) return;

        var recorded = dlg.Result;
        CoreLog.Info($"录制返回：path={recorded.Path?.Count ?? 0} pattern={recorded.Pattern} button={recorded.Button}");

        // 先拿“录完之后会变成什么样”的副本查重；命中就整笔作废，
        // 不覆盖当前编辑器里的旧手势。
        if (recorded.Path == null || recorded.Path.Count < 4)
        {
            CoreLog.Warn($"录制结果没有有效轨迹：path={recorded.Path?.Count ?? 0} pattern={recorded.Pattern}");
            _host.Notify(Loc.T("这次没有录到笔画，请重新录一次"));
            return;
        }

        var candidate = _current.Clone();
        candidate.Path = recorded.Path;
        candidate.Pattern = recorded.Pattern;
        candidate.Button = recorded.Button.Length > 0 ? recorded.Button : _current.Button;
        candidate.Chord = recorded.Chord;
        candidate.Kind = "stroke";
        if (string.IsNullOrWhiteSpace(candidate.AppPath) && _scope != EdgeTargetKey)
            candidate.AppPath = _scope;

        /* 先把录到的笔画放进编辑器，保证用户立刻能看到这条轨迹；
         * 重复只阻止保存，不再把整笔丢掉（否则看起来像“录完是空的”）。 */
        _current = candidate;
        LoadEditor();
        CoreLog.Info($"编辑器笔画：{_current.Path?.Count ?? 0} / HasPath={_current.HasPath}");

        var duplicate = GestureIdentity.Find(_host.Settings.Gestures, candidate, _current.Id);
        if (duplicate != null)
        {
            string title = (string.IsNullOrWhiteSpace(duplicate.Name)
                ? ActionCatalog.TitleOf(duplicate.Action)
                : duplicate.Name) ?? "";
            _host.Notify(Loc.F("这个手势已经绑定了「{0}」，改一下笔画或动作再保存", title));
            return;
        }

        _host.Notify(Loc.T("笔画已录制，记得点保存"));
    }

    private void OnRecordNewClick(object sender, RoutedEventArgs e)
    {
        OnNewClick(sender, e);
        OnRecordClick(sender, e);
    }

    // ───────────────────────── 多步手势（v1.5.0） ─────────────────────────

    /// <summary>刷新「步骤」清单；每一步都能单独删掉。</summary>
    private void RefreshSteps()
    {
        var rows = new List<GestureStepRow>(_steps.Count);
        for (int i = 0; i < _steps.Count; i++)
            rows.Add(new GestureStepRow((i + 1) + ".", _steps[i].Title(), _steps[i]));

        StepList.ItemsSource = rows;
        StepsEmpty.Visibility = _steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepsHint.Text = Loc.F("{0} / {1} 步", _steps.Count, GestureStep.MaxSteps);
    }

    private void AddStep(GestureStep step)
    {
        if (_steps.Count >= GestureStep.MaxSteps)
        {
            _host?.Notify(Loc.F("最多 {0} 步", GestureStep.MaxSteps));
            return;
        }
        _steps.Add(step);
        RefreshSteps();
    }

    private void OnRemoveStep(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GestureStep step) return;
        _steps.Remove(step);
        RefreshSteps();
    }

    private void OnClearSteps(object sender, RoutedEventArgs e)
    {
        _steps.Clear();
        RefreshSteps();
    }

    /// <summary>画一笔：复用主录制窗口，只取它的轨迹当一步。</summary>
    private void OnAddStrokeStep(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new UI.Windows.GestureRecordWindow
        {
            Owner = Window.GetWindow(this),
            Options = _host.Settings.GestureOptions,
        };

        bool accepted;
        _host.Gestures.Suspended = true;
        try { accepted = dlg.ShowDialog() == true; }
        finally { _host.Gestures.Suspended = false; }

        if (!accepted || dlg.Result == null) return;
        var path = dlg.Result.Path;
        if (path == null || path.Count < 4)
        {
            _host.Notify(Loc.T("这次没有录到笔画，请重新录一次"));
            return;
        }

        AddStep(new GestureStep { Kind = "stroke", Path = new List<int>(path) });
    }

    /// <summary>按一组键：用按键捕获窗拿 ctrl+a 这种写法。</summary>
    private void OnAddKeyStep(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        UI.Windows.KeyCaptureWindow.Capture(result =>
        {
            if (string.IsNullOrWhiteSpace(result.Combo)) return;
            AddStep(new GestureStep
            {
                Kind = "key",
                Key = CustomKeyRunner.Canonical(result.Combo)
            });
        }, title: Loc.T("录一步按键"));
    }

    private void OnAddWheelStep(object sender, RoutedEventArgs e)
    {
        int wheel = int.TryParse(SelectedValue(StepWheelBox), out int w) ? w : -1;
        AddStep(new GestureStep { Kind = "scroll", Wheel = wheel });
    }

    private void OnAddCornerStep(object sender, RoutedEventArgs e)
    {
        int corner = int.TryParse(SelectedValue(StepCornerBox), out int c) ? c : 1;
        if (corner == 0) corner = 1;
        AddStep(new GestureStep { Kind = "corner", Corner = corner });
    }

    private void OnAddEdgeStep(object sender, RoutedEventArgs e)
    {
        int edge = int.TryParse(SelectedValue(StepEdgeBox), out int value) ? value : 2;
        if (edge == 0) edge = 2;
        AddStep(new GestureStep { Kind = "edge", Edge = edge });
    }

    /// <summary>
    /// 回放：就在上面的「笔画」框里把这一笔重画一遍，只看不执行。
    ///
    /// 以前是往屏幕上糊一层覆盖层（GestureService.Replay），画在光标旁边、还要等它收 ——
    /// 用户看完得先满屏找它在哪。现在底下的静态线压暗，回放线在原位一笔一笔走，笔尖带个小圆点，
    /// 位置永远是"这条手势本来待的地方"。
    ///
    /// 节奏上不再"一帧一个点"：那样点密的笔画会快得像闪一下，看不清走向。
    /// 改成按时间推进、整段放慢到原来的 1.8 倍（约 0.55 倍速），
    /// 进度过一条三次曲线 —— 起笔慢、中间快、收笔再慢，最后淡出，看着才像在画而不是在跳。
    /// </summary>
    private void OnReplayClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var pts = BuildPreviewPoints();
        if (pts == null || pts.Count < 2)
        {
            _host.Notify(Loc.T("这条手势还没有笔画可以回放"));
            return;
        }

        StopReplay();

        _replayPoints = pts;
        _replayIndex = 0;
        ReplayLine.Points.Clear();
        ReplayLine.Visibility = Visibility.Visible;
        ReplayHead.Visibility = Visibility.Visible;
        PreviewLine.Opacity = 0.22;

        /* 笔尖先站在起点上，不然第一帧是空框里凭空冒出来一个点。 */
        Point first = pts[0];
        Canvas.SetLeft(ReplayHead, first.X - ReplayHead.Width / 2);
        Canvas.SetTop(ReplayHead, first.Y - ReplayHead.Height / 2);

        /* 先按老公式算"每点多少毫秒"，再乘 1.8 得总时长：
         * 点多的笔画本来就该慢一点，所以这里仍然按点数伸缩，只是整体拉长。 */
        int perPoint = Math.Clamp(1200 / Math.Max(1, pts.Count), 6, 24);
        _replayTotalMs = Math.Clamp((int)Math.Round(perPoint * (double)pts.Count * 1.8), 900, 3200);

        _replayClock = System.Diagnostics.Stopwatch.StartNew();
        /* 16ms 一帧、按经过的时间算画到第几个点，卡一下也不会把整体拖长。 */
        _replayTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _replayTimer.Tick += OnReplayTick;
        _replayTimer.Start();
    }

    private void OnReplayTick(object? sender, EventArgs e)
    {
        double elapsed = _replayClock?.Elapsed.TotalMilliseconds ?? _replayTotalMs;
        double progress = Math.Clamp(elapsed / Math.Max(1, _replayTotalMs), 0.0, 1.0);

        /* 三次缓入缓出：0.5 之前加速，之后减速，两头都慢。
           进度 → 目标点数，缺几个就补几个，所以点密的那段自动"每帧多个点"。 */
        double eased = progress < 0.5
            ? 4 * progress * progress * progress
            : 1 - Math.Pow(-2 * progress + 2, 3) / 2;
        int target = Math.Clamp((int)Math.Round(eased * _replayPoints.Count), 0, _replayPoints.Count);
        while (_replayIndex < target) AddReplayPoint(_replayPoints[_replayIndex++]);

        if (progress >= 1.0) FinishReplay();
    }

    private void AddReplayPoint(Point p)
    {
        ReplayLine.Points.Add(p);
        Canvas.SetLeft(ReplayHead, p.X - ReplayHead.Width / 2);
        Canvas.SetTop(ReplayHead, p.Y - ReplayHead.Height / 2);
    }

    /// <summary>画完了：停表，让线和笔尖淡掉，淡完再收干净。</summary>
    private void FinishReplay()
    {
        if (_replayTimer != null)
        {
            _replayTimer.Stop();
            _replayTimer.Tick -= OnReplayTick;
            _replayTimer = null;
        }
        _replayClock?.Stop();
        _replayClock = null;

        /* 收尾用 session 认领：淡出的这 0.26 秒里用户又点了回放 / 换了手势，
         * session 就变了，这个回调什么也不做，不会把新的一次掐掉。 */
        int session = _replaySession;
        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fade.Completed += (_, _) =>
        {
            if (session == _replaySession) StopReplay();
        };
        ReplayLine.BeginAnimation(UIElement.OpacityProperty, fade);
        ReplayHead.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>收掉回放：停定时器、清线、静态线恢复不透明。切手势 / 改形状 / 重画预览都会先调它。</summary>
    private void StopReplay()
    {
        _replaySession++;
        if (_replayTimer != null)
        {
            _replayTimer.Stop();
            _replayTimer.Tick -= OnReplayTick;
            _replayTimer = null;
        }
        _replayClock?.Stop();
        _replayClock = null;
        _replayTotalMs = 1;

        /* 淡出是动画占着 Opacity 打的，不清掉的话下一次回放会从 0 开始（等于看不见）。 */
        ReplayLine.BeginAnimation(UIElement.OpacityProperty, null);
        ReplayHead.BeginAnimation(UIElement.OpacityProperty, null);
        ReplayLine.Opacity = 1.0;
        ReplayHead.Opacity = 1.0;

        _replayPoints = new List<Point>();
        _replayIndex = 0;
        ReplayLine.Points.Clear();
        ReplayLine.Visibility = Visibility.Collapsed;
        ReplayHead.Visibility = Visibility.Collapsed;
        PreviewLine.Opacity = 1.0;
    }

    // ───────────────────────── 预览 ─────────────────────────

    private void DrawPreview()
    {
        StopReplay();
        PreviewLine.Points.Clear();
        PatternBadge.Text = "";

        /* 没录过就什么都不画：框里空着，下面那颗「按住鼠标键画一笔（重新录制）」
         * 已经把下一步说清楚了，再叠一行灰字只是噪音。 */
        _current.EnsurePattern();
        var pts = BuildPreviewPoints();
        if (pts == null) return;

        foreach (var p in pts) PreviewLine.Points.Add(p);
    }

    /// <summary>
    /// 把这一笔归一化到预览框里（居中、四周留 14px）。没笔画时返回 null。
    /// 静态预览和回放动画共用这一份换算 —— 两边画出来必须是同一条线。
    /// </summary>
    private List<Point>? BuildPreviewPoints()
    {
        if (!_current.HasPath) return null;

        double w = PreviewCanvas.ActualWidth > 10 ? PreviewCanvas.ActualWidth : 340;
        double h = PreviewCanvas.ActualHeight > 10 ? PreviewCanvas.ActualHeight : 118;
        const double pad = 14;

        /* 预览 / 回放画的是"存下来之后会是什么样"：就是校正后的那一笔，
         * 用户在详情里看到的就是识别时用的形状，不会出现"预览是斜的、实际按竖线认"的错位。 */
        var pts = GestureMath.Normalize(
            InputPlus.Core.Gestures.StrokeCanonicalizer.Canonicalize(_current.Points()));
        if (pts.Count < 2) return null;

        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double spanX = Math.Max(1, maxX - minX), spanY = Math.Max(1, maxY - minY);
        double scale = Math.Min((w - pad * 2) / spanX, (h - pad * 2) / spanY);
        // 居中：直线笔画也不会贴着左边画
        double offX = (w - spanX * scale) / 2, offY = (h - spanY * scale) / 2;

        var list = new List<Point>(pts.Count);
        foreach (var p in pts)
            list.Add(new Point(offX + (p.X - minX) * scale, offY + (p.Y - minY) * scale));
        return list;
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static void SelectTag(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<Choice>())
            if (string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static void SelectValue(ComboBox box, int value)
        => SelectTag(box, value.ToString());

    private static string? SelectedValue(ComboBox box) => (box.SelectedItem as Choice)?.Value;

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as Choice)?.Value;

    private sealed record Choice(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>
    /// 手势列表的一行。<see cref="Draggable"/> 决定这一行能不能长按拖动
    /// （只有「自定义顺序」+ 没在筛选时才给拖）。
    /// </summary>
    /// <summary>「步骤」清单的一行：序号 + 人话 + 原对象（删除按引用摘）。</summary>
    private sealed record GestureStepRow(string Index, string Title, GestureStep Step);

    private sealed record GestureRow(
        MouseGesture Gesture, string Title, string Subtitle, string Badge, string Glyph,
        PointCollection? IconPoints, ImageSource? EdgeIcon,
        Visibility GlyphVisibility, Visibility StrokeVisibility, Visibility EdgeIconVisibility,
        /* 这一行现在能不能拖（自定义顺序 + 没在筛）。没有手柄了，拖动靠"按住 2 秒"。 */
        bool Draggable,
        double RowOpacity, string StateText, Visibility StateVisibility)
    {
        /// <summary>能拖的行给一句提示，让"按住两秒"这件事有地方说。</summary>
        public string? DragTooltip => Draggable ? Loc.T("按住这条往上/下拖，就能调整它的顺序") : null;
    }

    /// <summary>
    /// 左侧「作用目标」的一行。四类共用一个类型：
    /// 标题行（分组 / 应用程序 / 特殊目标）靠 <see cref="HeaderVisibility"/> 变成分隔线，
    /// 分组多一颗「删除」按钮，应用多一颗「禁用 / 恢复」按钮。
    /// </summary>
    private sealed class TargetRow
    {
        public string Scope { get; init; } = "";
        public string Title { get; init; } = "";
        public string Count { get; init; } = "";
        public string Glyph { get; init; } = "";
        public string Tooltip { get; init; } = "";
        public bool Selected { get; init; }
        public string ToggleText { get; init; } = "";
        public string ToggleGlyph { get; init; } = "";
        public string ToggleTooltip { get; init; } = "";
        public Visibility ToggleVisibility { get; init; } = Visibility.Collapsed;
        public Visibility MenuVisibility { get; init; } = Visibility.Collapsed;
        public string DeleteTooltip { get; init; } = "";
        public Visibility DeleteVisibility { get; init; } = Visibility.Collapsed;
        public bool Disabled { get; init; }
        public Visibility DisabledVisibility { get; init; } = Visibility.Collapsed;
        public Visibility HeaderVisibility { get; init; } = Visibility.Collapsed;
        public Visibility RowVisibility { get; init; } = Visibility.Visible;
    }

    /// <summary>「允许的触发方式」的一行：三态（启用 / 禁用 / 默认）。</summary>
    private sealed record TriggerRow(
        string Key, string Label, string StateText, Brush StateBackground, Brush StateForeground,
        string Tooltip);
}
