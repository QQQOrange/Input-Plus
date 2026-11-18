using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
/* 只挑拖动要用的几个输入类型做别名：整条 using System.Windows.Input 会带进来
 * WPF 自己的 MouseGesture，和手势模型 MouseGesture 撞名（CS0104）。 */
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
    private AppHost? _host;
    private readonly List<GestureRow> _rows = new();

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

    /// <summary>左侧“边缘手势”不是某个 exe；用一个不会和路径冲突的哨兵值区分。</summary>
    private const string EdgeTargetKey = "\u0001edge";

    private static bool IsEdgeGesture(MouseGesture g) =>
        g.Kind is "hotcorner" or "edgewheel";

    public GesturesView()
    {
        InitializeComponent();
        PreviewCanvas.SizeChanged += (_, _) => DrawPreview();
        SizeChanged += (_, _) => UpdateOptionsScrollLimit();
    }

    /// <summary>
    /// 给「手势外观 / 手势参数」那张卡片算一个高度上限：展开后超出的部分在卡片里滚，
    /// 下面三栏（作用目标 / 手势列表 / 详情）就不会被 Auto 行挤成 0 高。
    /// 这三栏至少留 260 DIP 左右，其余给卡片。
    /// </summary>
    private void UpdateOptionsScrollLimit()
    {
        double h = ActualHeight;
        if (h <= 0) return;
        OptionsScroll.MaxHeight = Math.Max(200, h - 380);
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
            EnabledBox.IsChecked = opts.Enabled;
            string hotkey = opts.ToggleHotkey ?? "";
            bool hotkeySet = !string.IsNullOrWhiteSpace(hotkey);
            /* 没设过的时候框里留一行浅色的「未设置」：空框看起来像刚被人清掉了。 */
            ToggleHotkeyBox.Text = hotkeySet ? hotkey : Loc.T("未设置");
            if (TryFindResource(hotkeySet ? "Brush.TextPrimary" : "Brush.TextTertiary") is Brush hotkeyBrush)
                ToggleHotkeyBox.Foreground = hotkeyBrush;
            MinMoveBox.Text = opts.MinMove.ToString();
            StartTimeoutBox.Text = opts.StartTimeoutMs.ToString();
            ThresholdBox.Text = opts.WgThreshold.ToString("0.000");
            ThresholdBox.ToolTip = Loc.T("笔画形状距离不超过它就认。默认 0.050，越小越严（0.005~0.3）。");
            ActivationBox.Text = opts.ActivationDistance.ToString();
            TurnBox.Text = opts.TurnTolerance.ToString("0.00");
            TrailOpacitySlider.Value = Math.Clamp(opts.TrailOpacity, 0.2, 1.0);
            TrailOpacityText.Text = $"{Math.Round(TrailOpacitySlider.Value * 100):0}%";
            bool pointerTarget = opts.UsesPointerTarget;
            TargetPointerRadio.IsChecked = pointerTarget;
            TargetActiveRadio.IsChecked = !pointerTarget;
            UpdateTargetModeHint(opts);
            UpdateColorSwatches();
            SyncWg2Controls(opts);
            _sortMode = GestureListOrder.Normalize(opts.ListSort);
            SelectTag(SortBox, _sortMode);
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

    private void LoadList(string selectId = "")
    {
        string filter = FilterBox.Text.Trim();
        var settings = _host!.Settings;
        bool edgeScope = string.Equals(_scope, EdgeTargetKey, StringComparison.Ordinal);
        bool specialScope = IsSpecialScope(_scope);
        string buttonFilter = edgeScope ? "" : (SelectedValue(ButtonFilterBox) ?? "");
        ButtonFilterBox.IsEnabled = !edgeScope;

        var group = GroupFor(_scope);
        TargetTitle.Text = edgeScope
            ? Loc.T("边缘手势")
            : _scope.Length == 0
                ? Loc.T("全局的手势")
                : group != null
                    ? Loc.F("分组「{0}」的手势", group.Name)
                    : Loc.F("{0} 的手势", ScopeTitle(_scope));
        TriggerHeader.Text = Loc.F("{0} 允许的触发方式", ScopeTitle(_scope));

        /* 先收集"这一档该显示哪些"，再套排序 —— 排序只影响显示顺序，
         * settings.Gestures 本身按兵不动，切回「自定义顺序」还是用户拖出来的那个顺序。 */
        var candidates = new List<MouseGesture>();
        foreach (var g in settings.Gestures)
        {
            if (edgeScope)
            {
                if (!IsEdgeGesture(g)) continue;
            }
            else
            {
                if (IsEdgeGesture(g)) continue;   // 不让边缘手势混进全局/应用列表
                string path = g.AppPath ?? "";
                bool matches =
                    _scope.Length == 0 ? path.Length == 0
                    : group != null
                        ? string.Equals(path, group.Id, StringComparison.OrdinalIgnoreCase)
                          || (group.Members ?? new List<string>()).Any(m => SameTarget(m, path))
                    : specialScope ? false
                    : string.Equals(path, _scope, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
            }
            if (buttonFilter.Length > 0 &&
                !string.Equals(g.Button ?? "", buttonFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            candidates.Add(g);
        }

        if (_sortMode != GestureListOrder.Custom)
            candidates = GestureListOrder.Sort(candidates, _sortMode, SortTitle);

        /* 手柄只在「自定义顺序 + 没在筛选」时露出来：其余三档拖了也不算数，
         * 摆一根拖不动的把手比不摆更让人困惑。 */
        bool canDrag = _sortMode == GestureListOrder.Custom && filter.Length == 0 && candidates.Count > 1;

        _rows.Clear();
        foreach (var g in candidates)
        {
            string actionTitle = ActionCatalog.TitleOf(g.Action) ?? "";
            string title = "", subtitle = "";
            if (IsEdgeGesture(g))
            {
                /* 边缘手势列表只写生效位置，不把动作名混进标题；
                 * 动作放在副标题里。四角/四边的范围图标由 EdgeIconFor 画。 */
                if (g.Kind == "hotcorner")
                {
                    title = CornerName(g.Corner);
                    subtitle = actionTitle;
                }
                else if (g.Kind == "edgewheel")
                {
                    title = Loc.F("{0} · 滚轮{1}", EdgeName(g.Edge), WheelName(g.Wheel));
                    subtitle = actionTitle;
                }
            }
            else
            {
                title = g.Title(actionTitle);
                subtitle = Describe(g);
            }
            string hay = title + " " + subtitle + " " + g.AppPath + " " + g.Note;
            if (filter.Length > 0 && hay.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            /* 以前这里显示「WG」——用户看不出那是"从 WGestures 导入"的标记，
             反而会以为手势只能在 WGestures 里用。现在只在"限定了程序"时
             显示一颗小标签，导入来源只在详情里用一句话说明。 */
            string badge = string.IsNullOrWhiteSpace(g.AppPath) || edgeScope
                ? ""
                : Loc.T("仅此应用");
            var icon = IconFor(g);
            bool hasIcon = icon is { Count: > 1 };
            var edgeIcon = IsEdgeGesture(g) ? EdgeIconFor(g) : null;
            bool hasEdgeIcon = edgeIcon != null;
            bool showStroke = hasIcon && !hasEdgeIcon;
            bool disabled = !g.Enabled;
            _rows.Add(new GestureRow(
                g, title, subtitle, badge, IsEdgeGesture(g) ? EdgeGlyph(g) : GlyphFor(g), icon, edgeIcon,
                hasIcon || hasEdgeIcon ? Visibility.Collapsed : Visibility.Visible,
                showStroke ? Visibility.Visible : Visibility.Collapsed,
                hasEdgeIcon ? Visibility.Visible : Visibility.Collapsed,
                canDrag ? Visibility.Visible : Visibility.Collapsed,
                disabled ? 0.48 : 1.0,
                disabled ? Loc.T("已停用") : "",
                disabled ? Visibility.Visible : Visibility.Collapsed));
        }

        GestureList.ItemsSource = null;
        GestureList.ItemsSource = _rows;
        EmptyHint.Text = edgeScope
            ? Loc.T("还没有边缘手势。点右上角「录制新手势」，把触发方式设成「悬停到角落」或「贴边滚轮」。")
            : specialScope
                ? Loc.T("特殊目标只决定「哪些触发方式允许起手势」，手势本身记在全局或应用程序下面。展开上面的面板调整。")
                : group != null
                    ? Loc.F("「{0}」里还没有手势。点右上角「录制新手势」，会记在这个分组下面。", group.Name)
                    : _scope.Length == 0
                        ? Loc.T("全局还没有手势。点右上角「录制新手势」，会记在「全局」下面。")
                        : Loc.F("「{0}」还没有手势。点右上角「录制新手势」，会记在这个应用下面。", AppName(_scope));
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = _rows.Count == 0
            ? Loc.T("这个目标下还没有手势")
            : Loc.F("这个目标 {0} 条 · 全部 {1} 条", _rows.Count, settings.Gestures.Count);
        if (canDrag) SummaryText.Text += Loc.T(" · 按住左侧的手柄可以拖动排序");

        var pick = _rows.FirstOrDefault(r => r.Gesture.Id == selectId) ?? _rows.FirstOrDefault();
        if (pick != null)
        {
            _current = pick.Gesture.Clone();
        }
        else
        {
            // 一条都没有时也要把右侧填成"一条合法的默认手势"，
            // 否则下拉框是空的，用户点「保存」才发现什么都没选。
            _current = new MouseGesture
            {
                Id = MouseGesture.NewId(),
                Kind = edgeScope ? "hotcorner" : "stroke",
                Button = edgeScope ? "" : GestureButtons.Right,
                Corner = edgeScope ? 1 : 0,
                Action = edgeScope ? "switchNextApp" : "",
                AppPath = edgeScope || specialScope ? "" : _scope,
            };
        }
        LoadEditor();

        // 列表重建后 WPF 有时会保留上一次的滚动位置，导致第一行只露出半截。
        // 列表是一次性重建的（没有虚拟化），直接回到顶部最稳。
        ListScroll.ScrollToTop();

        // 左边换了作用目标，「允许的触发方式」整栏跟着换一套三态。
        BuildTriggers();
    }

    /// <summary>
    /// 排序用的标题：和列表里那一行同一套说法（边缘手势的标题是「左上角」「上边缘 · 滚轮向上」，
    /// 模型里没有这个字段），否则"按名称"排出来的顺序和肉眼看到的标题对不上。
    /// </summary>
    private string SortTitle(MouseGesture g)
    {
        if (g.Kind == "hotcorner") return CornerName(g.Corner);
        if (g.Kind == "edgewheel") return Loc.F("{0} · 滚轮{1}", EdgeName(g.Edge), WheelName(g.Wheel));
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
            GestureEnabledBox.IsChecked = _current.Enabled;
            ImmediateBox.IsChecked = _current.Immediate;
            HideLabelBox.IsChecked = _current.HideLabel;
            SelectAction(_current.Action);
            ArgBox.Text = _current.Arg;
            EditorNote.Text = _current.Note;
            EditorNote.Visibility = string.IsNullOrWhiteSpace(_current.Note) ? Visibility.Collapsed : Visibility.Visible;
            UpdateKindRows();
            UpdateArgVisibility();
            DrawPreview();
        }
        finally { _loading = false; }
    }

    private void UpdateKindRows()
    {
        string kind = SelectedTag(KindBox) ?? "stroke";
        bool stroke = kind == "stroke";
        bool corner = kind is "corner" or "hotcorner";
        bool wheel = kind is "wheel" or "edgewheel";
        bool edge = kind == "edgewheel";
        bool buttonless = kind is "hotcorner" or "edgewheel";

        StrokeRow.Visibility = stroke ? Visibility.Visible : Visibility.Collapsed;
        CornerRow.Visibility = corner ? Visibility.Visible : Visibility.Collapsed;
        WheelRow.Visibility = wheel ? Visibility.Visible : Visibility.Collapsed;
        EdgeRow.Visibility = edge ? Visibility.Visible : Visibility.Collapsed;
        EdgeLabel.Text = Loc.T("屏幕边缘");
        ImmediateBox.IsEnabled = stroke;   // 只有画笔画才有「画到一半就执行」

        // 热角和贴边滚轮不需要按任何键：把按键行收掉，免得存进一个没用的 Button。
        ButtonBox.IsEnabled = !buttonless;
        ChordBox.IsEnabled = !buttonless;
        if (buttonless)
        {
            SelectTag(ButtonBox, "");
            SelectTag(ChordBox, "");
        }
        // 不再自动把空键改回右键：「不按键」是用户明确选的，保存时再校验。
    }

    private void UpdateArgVisibility()
    {
        var info = _host?.Actions.Get(_current.Action);
        bool needs = info?.NeedsArg == true;
        bool windowBind = info?.Category == "窗口绑定";
        bool customKey = info?.Id == "customKey";
        bool keyArg = customKey || info?.Id is "sendKeys";

        /* 自定义按键的参数是 JSON，但用户要看/要改的是「ctrl+c」这段文本：
         * 文本框里放键文本，落盘时再包成 JSON（见 OnSaveClick / OnArgBoxCommit）。
         * 这样录制、点列表、手打三条路都汇到同一个文本框里，看到的就是最终结果。 */
        string keyText = customKey ? CustomKeySpec.FromArg(_current.Arg).Key : "";
        if (customKey)
        {
            ArgBox.Tag = _current.Action;
            if (!string.Equals(ArgBox.Text, keyText, StringComparison.Ordinal))
                ArgBox.Text = keyText;
        }
        else if (needs && ArgBox.Tag as string != _current.Action)
        {
            ArgBox.Tag = _current.Action;
            ArgBox.Text = _current.Arg;
        }

        // 一次只露一个参数控件：窗口绑定用选程序按钮，按键用文本框 + 录制按钮，
        // 其它才用普通文本框。
        ArgBox.Visibility = (needs && !windowBind && !keyArg) || customKey
            ? Visibility.Visible
            : Visibility.Collapsed;
        ArgHint.Visibility = customKey ? Visibility.Visible : Visibility.Collapsed;
        ArgCaptureButton.Visibility = keyArg ? Visibility.Visible : Visibility.Collapsed;
        ActionAppPicker.Visibility = windowBind ? Visibility.Visible : Visibility.Collapsed;
        ActionAppStatus.Visibility = windowBind ? Visibility.Visible : Visibility.Collapsed;
        ActionFunctionPanel.Visibility = windowBind ? Visibility.Collapsed : Visibility.Visible;

        if (keyArg)
        {
            string buttonText = customKey ? keyText : _current.Arg;
            ArgCaptureButton.Content = string.IsNullOrWhiteSpace(buttonText)
                ? Loc.T("录制按键")
                : CustomKeyRunner.ComboDisplay(buttonText);
        }
        if (windowBind)
            ActionAppStatus.Text = string.IsNullOrWhiteSpace(_current.Arg)
                ? Loc.T("还没有选择程序")
                : _current.Arg;
    }

    private void SetActionArg(string arg)
    {
        _current.Arg = arg;
        var info = _host?.Actions.Get(_current.Action);
        if (info?.Id == "customKey")
        {
            string key = CustomKeySpec.FromArg(arg).Key;
            ArgBox.Text = key;
            ArgCaptureButton.Content = string.IsNullOrWhiteSpace(key)
                ? Loc.T("录制按键")
                : CustomKeyRunner.ComboDisplay(key);
        }
        else
        {
            ArgBox.Text = arg;
            if (info?.Id == "sendKeys")
                ArgCaptureButton.Content = string.IsNullOrWhiteSpace(arg) ? Loc.T("录制按键") : arg;
        }
        if (info?.Category == "窗口绑定")
            ActionAppStatus.Text = string.IsNullOrWhiteSpace(arg) ? Loc.T("还没有选择程序") : arg;
    }

    private void OnCaptureArgKey(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        UI.Windows.KeyCaptureWindow.Capture(result =>
        {
            if (string.IsNullOrWhiteSpace(result.Combo)) return;
            var info = _host.Actions.Get(_current.Action);
            if (info?.Id == "customKey")
            {
                var spec = CustomKeySpec.FromArg(_current.Arg);
                spec.Key = CustomKeyRunner.Canonical(result.Combo);
                SetActionArg(spec.ToArg());
                return;
            }
            SetActionArg(result.Combo!);
        }, title: Loc.T("录制按键"));
    }

    /// <summary>文本框失焦：把「ctrl+c」落成 JSON。</summary>
    private void OnArgBoxCommit(object sender, RoutedEventArgs e) => CommitKeyText();

    /// <summary>文本框里按回车：和失焦一样提交（手打完不用再点别处）。</summary>
    private void OnArgBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        CommitKeyText();
        e.Handled = true;
    }

    /// <summary>
    /// 把文本框里的按键文本写回动作参数。
    ///
    /// 只在失焦 / 回车时做，不在 TextChanged 里做：用户打到一半是「ctrl+」，
    /// 每敲一个字符就规范化一次会把还没写完的内容判成非法、光标也会跳。
    /// 认不出来的输入不覆盖原配置，退回上一个能用的值并提示 ——
    /// 否则用户以为改成功了，按下去却什么都不发。
    /// </summary>
    private void CommitKeyText()
    {
        if (_host == null) return;
        var info = _host.Actions.Get(_current.Action);
        if (info?.Id != "customKey")
        {
            _current.Arg = ArgBox.Text;
            return;
        }

        string typed = (ArgBox.Text ?? "").Trim();
        var spec = CustomKeySpec.FromArg(_current.Arg);

        if (typed.Length == 0)
        {
            spec.Key = "";
            SetActionArg(spec.ToArg());
            return;
        }

        if (!CustomKeyRunner.TryNormalize(typed, out string canonical, out string bad))
        {
            _host.Notify(Loc.F("认不出「{0}」这个键，照着 ctrl+c 或 ctrl+v enter 这样写", bad));
            ArgBox.Text = spec.Key;
            return;
        }

        spec.Key = canonical;
        SetActionArg(spec.ToArg());
        ArgCaptureButton.Content = CustomKeyRunner.ComboDisplay(canonical);
    }

    private void OnPickActionAppFile(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("窗口绑定用哪个程序"),
            Filter = Loc.T("程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) SetActionArg(dlg.FileName);
    }

    private void OnPickActionAppWindow(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var picker = new UI.Windows.WindowPickerWindow();
        if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
        picker.ShowDialog();

        var h = picker.PickedWindow;
        if (h == IntPtr.Zero) return;
        var info = Core.Windows.WindowService.Describe(h);
        if (info == null || string.IsNullOrEmpty(info.ExePath))
        {
            _host.Notify(Loc.T("取不到这个窗口的程序，改用「选择程序…」挑 exe"));
            return;
        }
        SetActionArg(info.ExePath);
    }

    private static string Describe(MouseGesture g)
    {
        if (g.Kind == "hotcorner")
            return Loc.F("屏幕{0} · 悬停触发", CornerName(g.Corner));
        if (g.Kind == "edgewheel")
            return Loc.F("屏幕{0} · 滚轮{1}", EdgeName(g.Edge), WheelName(g.Wheel));

        string button = Loc.T(GestureButtons.Display(g.Button));
        if (!string.IsNullOrWhiteSpace(g.Chord))
            button = Loc.F("{0} + {1}", button, Loc.T(GestureButtons.Display(g.Chord)));
        if (g.Kind == "stroke") g.EnsurePattern();
        /* 5.13 起列表里不再画方向串箭头：一根笔画的样子就是列表左边那张缩略图，
         * 有轨迹就写按键名；没轨迹的老数据在启动迁移时已经合成出轨迹了，
         * 这里只剩「按键名」一条口径，不再出现 ↑→ 那种字符。 */
        return g.Kind switch
        {
            "corner" => Loc.F("{0} · 屏幕{1}", button, CornerName(g.Corner)),
            "wheel" => Loc.F("{0} · 滚轮{1}", button, WheelName(g.Wheel)),
            _ => button
        };
    }

    private static string CornerName(int corner) => corner switch
    {
        1 => Loc.T("左上角"),
        2 => Loc.T("右上角"),
        3 => Loc.T("左下角"),
        4 => Loc.T("右下角"),
        _ => Loc.T("任意角落")
    };

    private static string EdgeName(int edge) => edge switch
    {
        1 => Loc.T("上边缘"),
        2 => Loc.T("右边缘"),
        3 => Loc.T("下边缘"),
        4 => Loc.T("左边缘"),
        _ => Loc.T("任意边缘")
    };

    private static string WheelName(int wheel) => wheel switch
    {
        -1 => Loc.T("向上"),
        1 => Loc.T("向下"),
        -2 => Loc.T("向左"),
        2 => Loc.T("向右"),
        _ => Loc.T("向上")
    };

    private static string GlyphFor(MouseGesture g) => g.Kind switch
    {
        "corner" or "hotcorner" => "\uE7FC",   // Corner
        "wheel" or "edgewheel" => "\uE962",
        _ => "\uE962"           // Mouse
    };

    /// <summary>四角/四边各用一个方向符号，列表左侧一眼能分清位置。</summary>
    private static string EdgeGlyph(MouseGesture g) => g.Kind switch
    {
        "hotcorner" => g.Corner switch { 1 => "◤", 2 => "◥", 3 => "◣", 4 => "◢", _ => "◇" },
        "edgewheel" => g.Edge switch { 1 => "▀", 2 => "▐", 3 => "▄", 4 => "▌", _ => "▭" },
        _ => "\uE7FC"
    };

    /// <summary>
    /// 画一个屏幕范围示意：灰框代表屏幕，红点/红线表示这个手势的生效角落或边缘。
    /// 比统一鼠标图标直观，也不用依赖图标字体里是否真的有对应字形。
    /// </summary>
    private static ImageSource EdgeIconFor(MouseGesture g)
    {
        var group = new DrawingGroup();
        var outlinePen = new Pen(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)), 1.2);
        group.Children.Add(new GeometryDrawing(
            null, outlinePen, new RectangleGeometry(new Rect(3, 3, 18, 14), 2, 2)));

        var red = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        if (g.Kind == "hotcorner" && g.Corner is >= 1 and <= 4)
        {
            double x = g.Corner is 2 or 4 ? 18 : 6;
            double y = g.Corner is 3 or 4 ? 14 : 6;
            group.Children.Add(new GeometryDrawing(
                red, null, new EllipseGeometry(new Point(x, y), 2.2, 2.2)));
        }
        else if (g.Kind == "edgewheel")
        {
            Rect band = g.Edge switch
            {
                1 => new Rect(3, 2.4, 18, 2.6),
                2 => new Rect(18.4, 3, 2.6, 14),
                3 => new Rect(3, 15, 18, 2.6),
                4 => new Rect(2.4, 3, 2.6, 14),
                _ => Rect.Empty
            };
            if (!band.IsEmpty)
                group.Children.Add(new GeometryDrawing(red, null, new RectangleGeometry(band, 1.2, 1.2)));
        }

        group.Freeze();
        return new DrawingImage(group);
    }

    /// <summary>把这条路手势缩成 22×22 的迷你折线，列表左侧直接显示笔画。</summary>
    private static PointCollection? IconFor(MouseGesture g)
    {
        if (g.Kind != "stroke" || !g.HasPath) return null;

        var pts = GestureMath.Resample(GestureMath.Normalize(g.Points()), 24);
        if (pts.Count < 2) return null;

        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double spanX = Math.Max(1, maxX - minX), spanY = Math.Max(1, maxY - minY);
        const double box = 22, pad = 1.5;
        double scale = Math.Min((box - pad * 2) / spanX, (box - pad * 2) / spanY);
        double offX = (box - spanX * scale) / 2, offY = (box - spanY * scale) / 2;

        var icon = new PointCollection(pts.Count);
        foreach (var p in pts)
            icon.Add(new Point(offX + (p.X - minX) * scale, offY + (p.Y - minY) * scale));
        return icon;
    }

    // ───────────────────────── 选项 ─────────────────────────

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

        /* 排序下拉：只有「自定义顺序」允许拖动手柄改表，其余三档只影响显示。 */
        SortBox.ItemsSource = new List<Choice>
        {
            new(GestureListOrder.Custom, Loc.T("自定义顺序")),
            new(GestureListOrder.ByName, Loc.T("按名称")),
            new(GestureListOrder.ByTrigger, Loc.T("按触发方式")),
            new(GestureListOrder.ByAction, Loc.T("按动作"))
        };
        SortBox.DisplayMemberPath = "Text";
        SortBox.SelectedValuePath = "Value";
        SelectTag(SortBox, GestureListOrder.Custom);

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
            new("edgewheel", Loc.T("贴边滚轮"))
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

    private void OnButtonFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ButtonFilterBox.SelectedItem == null) return;
        LoadList(selectId: _current.Id);
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string mode = GestureListOrder.Normalize(SelectedValue(SortBox));
        if (string.Equals(mode, _sortMode, StringComparison.Ordinal)) return;
        _sortMode = mode;
        _host!.Settings.GestureOptions.ListSort = mode;
        _host.SaveSettings();
        LoadList(selectId: _current.Id);
    }

    /* ─────────────────── 拖动排序（只在「自定义顺序」下） ───────────────────
     *
     * 手柄在每行最左边，按下之后鼠标交给 GestureList 捕获：
     * 行的 Button 就收不到抬起事件，拖动过程不会顺手把那一行"选中"（右侧编辑器不跳）。
     *
     * 落点是"插到第几条之前"——每一行的上半/下半分界当插入位，
     * 松手时交给 GestureListOrder.MoveWithin 只重排看得见的这几条，
     * 别的应用/分组的手势在整表里的位置一点不动。 */

    private MouseGesture? _dragItem;
    private int _dragFrom = -1;
    private int _dropIndex = -1;
    private Point _dragStart;

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (_loading || !GestureListOrder.IsCustom(_sortMode)) return;
        if ((sender as FrameworkElement)?.DataContext is not GestureRow row) return;
        int from = _rows.IndexOf(row);
        if (from < 0) return;
        _dragItem = row.Gesture;
        _dragFrom = from;
        _dropIndex = -1;
        _dragStart = e.GetPosition(ListScroll);
        GestureList.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // 抬起丢了（切了窗口）也要把拖动收干净
            FinishDrag(null);
            return;
        }
        var p = e.GetPosition(ListScroll);
        if (_dropIndex < 0 && Math.Abs(p.Y - _dragStart.Y) < 4) return;   // 手抖不算拖动

        // 拖到上下边缘先滚一截：列表长的目标（全局 60 多条）不然根本拖不到头
        if (p.Y < 26) ListScroll.ScrollToVerticalOffset(Math.Max(0, ListScroll.VerticalOffset - 16));
        else if (p.Y > ListScroll.ActualHeight - 26)
            ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset + 16);

        ShowDropLine(DropIndexAt(e.GetPosition(ListScroll)));
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem == null) return;
        FinishDrag(e.GetPosition(ListScroll));
        e.Handled = true;
    }

    private void OnGripLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragItem != null) FinishDrag(null);
    }

    /// <summary>指针落在哪两行之间：返回"插到第几条之前"，等于行数就是插到最末尾。</summary>
    private int DropIndexAt(Point p)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (GestureList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            double top;
            try { top = c.TransformToAncestor(ListScroll).Transform(new Point(0, 0)).Y; }
            catch (InvalidOperationException) { continue; }   // 行不在可视树里（被虚拟化掉了）
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
            try
            {
                var corner = c.TransformToAncestor(ListScroll).Transform(new Point(0, 0));
                y = index >= _rows.Count ? corner.Y + c.ActualHeight : corner.Y;
            }
            catch (InvalidOperationException) { y = 0; }
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
        _dragItem = null;
        _dragFrom = -1;
        _dropIndex = -1;
        DropLine.Visibility = Visibility.Collapsed;
        if (GestureList.IsMouseCaptured) GestureList.ReleaseMouseCapture();
    }

    /// <summary>出图用：切到某一档排序并回报行数/手柄可见数（渲染时不动设置）。</summary>
    internal string[] SetSortForRender(string mode)
    {
        _loading = true;
        try
        {
            _sortMode = GestureListOrder.Normalize(mode);
            SelectTag(SortBox, _sortMode);
        }
        finally { _loading = false; }

        LoadList(selectId: _current.Id);
        return new[]
        {
            "排序：" + (SelectedValue(SortBox) ?? "?"),
            "这一档行数：" + _rows.Count + "（全表 " + _host!.Settings.Gestures.Count + " 条）",
            "手柄可见：" + _rows.Count(r => r.GripVisibility == Visibility.Visible) + " 行",
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

    /// <summary>
    /// 切换「手势作用目标」。只改一个字段：匹配哪个程序、执行前要不要切窗口
    /// 都由 GestureService.ResolveTarget 在每次起手时现算，不用重挂钩子。
    /// </summary>
    private void OnTargetModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        o.TargetMode = TargetPointerRadio.IsChecked == true
            ? GestureOptions.TargetPointer
            : GestureOptions.TargetActive;
        UpdateTargetModeHint(o);
        _host.SaveSettings();
    }

    /// <summary>右边的说明随选择变：两种模式最直观的差别就是"会不会把窗口切到前面"。</summary>
    private void UpdateTargetModeHint(GestureOptions o)
    {
        TargetModeHint.Text = o.UsesPointerTarget
            ? Loc.T("动作发给指针底下的那个程序，执行前把它切到前面。")
            : Loc.T("动作只发给当前的活动窗口，不切换窗口。");
    }

    private void OnTrailOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        o.TrailOpacity = Math.Clamp(e.NewValue, 0.2, 1.0);
        TrailOpacityText.Text = $"{Math.Round(o.TrailOpacity * 100):0}%";
        _host.SaveSettings();
    }

    // ─────────────────── WGestures 2 的手势外观偏好 ───────────────────

    /// <summary>把 WG2 那套外观偏好灌进控件（只读配置，不触发保存）。</summary>
    private void SyncWg2Controls(GestureOptions o)
    {
        PathBox.IsChecked = o.ShowPath;
        PathBorderBox.IsChecked = o.ShowPathBorder;
        GestureNameBox.IsChecked = o.ShowGestureName;
        FadeOutBox.IsChecked = o.FadeOutExecuted;
        LabelBorderBox.IsChecked = o.ShowLabelBorder;
        TimeoutIndicatorBox.IsChecked = o.ShowStartTimeoutIndicator;

        PathWidthSlider.Value = Math.Clamp(o.PathLineWidth, 1.0, 4.0);
        PathWidthText.Text = PathWidthSlider.Value.ToString("0.0");
        NamePosSlider.Value = Math.Clamp(o.GestureNamePos, 0.0, 1.0);
        NamePosText.Text = $"{Math.Round(NamePosSlider.Value * 100):0}%";

        UpdateWg2Swatches();
    }

    /// <summary>
    /// 六个外观勾选共用：Tag 就是字段名。
    /// ShowPath 是计算属性，真正的开关是 ShowTrail —— 两个一起写，
    /// 否则老配置里 ShowTrail=false 会把刚打开的勾选又压回去。
    /// </summary>
    private void OnWg2ToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null || sender is not CheckBox box) return;
        var o = _host.Settings.GestureOptions;
        bool on = box.IsChecked == true;
        switch (box.Tag as string)
        {
            case "ShowPath":
                o.ShowPath = on;
                o.ShowTrail = on;
                break;
            case "ShowPathBorder": o.ShowPathBorder = on; break;
            case "ShowGestureName": o.ShowGestureName = on; break;
            case "FadeOutExecuted": o.FadeOutExecuted = on; break;
            case "ShowLabelBorder": o.ShowLabelBorder = on; break;
            case "ShowStartTimeoutIndicator": o.ShowStartTimeoutIndicator = on; break;
            default: return;
        }
        _host.SaveSettings();
        UpdateWg2Swatches();
    }

    /// <summary>粗细与名称位置两个滑杆。</summary>
    private void OnWg2SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        if (ReferenceEquals(sender, PathWidthSlider))
        {
            o.PathLineWidth = Math.Clamp(e.NewValue, 1.0, 4.0);
            PathWidthText.Text = o.PathLineWidth.ToString("0.0");
        }
        else if (ReferenceEquals(sender, NamePosSlider))
        {
            o.GestureNamePos = Math.Clamp(e.NewValue, 0.0, 1.0);
            NamePosText.Text = $"{Math.Round(o.GestureNamePos * 100):0}%";
        }
        else return;
        _host.SaveSettings();
    }

    /// <summary>四个色块：轨迹（已识别 / 未识别）、手势名称（未执行 / 已执行）。</summary>
    private void OnPickWg2Color(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null || sender is not FrameworkElement el) return;
        var o = _host.Settings.GestureOptions;
        string tag = el.Tag as string ?? "";
        string current = tag switch
        {
            "pathRecognized" => o.PathColorRecognized,
            "pathNormal" => o.PathColorNormal,
            "labelNormal" => o.LabelColorNormal,
            "labelExecuted" => o.LabelColorExecuted,
            _ => "",
        };
        if (current.Length == 0) return;

        var before = Wg2Color.Parse(current, Colors.Magenta);
        /* ColorDialog 没有 alpha：先按不透明色挑，挑完把原 alpha 贴回去。
           WG2 的默认色本来就是「同一个紫、两种透明度」，这么处理正好保形。 */
        var picked = PickColor($"#{before.A:X2}{before.R:X2}{before.G:X2}{before.B:X2}");
        if (picked == null) return;
        var after = Color.FromArgb(before.A, picked.Value.R, picked.Value.G, picked.Value.B);
        string hex = Wg2Color.Format(after);
        switch (tag)
        {
            case "pathRecognized": o.PathColorRecognized = hex; break;
            case "pathNormal": o.PathColorNormal = hex; break;
            case "labelNormal": o.LabelColorNormal = hex; break;
            case "labelExecuted": o.LabelColorExecuted = hex; break;
            default: return;
        }
        _host.SaveSettings();
        UpdateWg2Swatches();
    }

    private void UpdateWg2Swatches()
    {
        if (_host == null) return;
        var o = _host.Settings.GestureOptions;
        SetRgbaSwatch(PathRecognizedSwatch, o.PathColorRecognized);
        SetRgbaSwatch(PathNormalSwatch, o.PathColorNormal);
        SetRgbaSwatch(LabelNormalSwatch, o.LabelColorNormal);
        SetRgbaSwatch(LabelExecutedSwatch, o.LabelColorExecuted);
    }

    /// <summary>
    /// 色块按原色的 alpha 画，但垫一个下限（0x66）：WG2 出厂的未识别色是 0x30 的 alpha，
    /// 原样画几乎看不见，纯不透明又会把「淡」这个信息丢掉。原始 #RRGGBBAA 放 ToolTip。
    /// </summary>
    private static void SetRgbaSwatch(Border swatch, string hex)
    {
        var c = Wg2Color.Parse(hex, Colors.Magenta);
        byte a = Math.Max(c.A, (byte)0x66);
        swatch.Background = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        swatch.ToolTip = hex.ToUpperInvariant();
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

    /// <summary>出图用：把「外观 / 参数」两块展开（默认都收起），拍完再收回。</summary>
    internal void SetPreferencesExpandedForRender(bool expanded)
    {
        AppearanceExpander.IsExpanded = expanded;
        ParamExpander.IsExpanded = expanded;
        if (!expanded) return;
        UpdateLayout();
        AppearanceExpander.UpdateLayout();
        ParamExpander.UpdateLayout();
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

    private void OnOptionsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        if (int.TryParse(MinMoveBox.Text, out int min) && min is >= 4 and <= 400) o.MinMove = min;
        if (int.TryParse(StartTimeoutBox.Text, out int timeout) && timeout is >= 100 and <= 3000) o.StartTimeoutMs = timeout;
        if (double.TryParse(ThresholdBox.Text, out double th) && th is >= 0.005 and <= 0.3)
            o.WgThreshold = th;
        if (int.TryParse(ActivationBox.Text, out int act) && act is >= 4 and <= 200) o.ActivationDistance = act;
        if (double.TryParse(TurnBox.Text, out double turn) && turn is >= 0 and <= 1) o.TurnTolerance = turn;
        _host.SaveSettings();
    }

    private void OnCaptureToggleHotkey(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        KeyCaptureWindow.Capture(result =>
        {
            if (string.IsNullOrWhiteSpace(result.Combo)) return;
            _host.Settings.GestureOptions.ToggleHotkey = result.Combo;
            _host.SaveSettings();
            ToggleHotkeyBox.Text = result.Combo;
            if (TryFindResource("Brush.TextPrimary") is Brush hotkeyBrush)
                ToggleHotkeyBox.Foreground = hotkeyBrush;
            _host.Gestures.ApplyHotkey();
            _host.Notify(Loc.F("鼠标手势开关快捷键已设为 {0}", result.Combo));
        }, title: Loc.T("按下鼠标手势总开关快捷键"));
    }

    // ───────────────────────── 轨迹颜色 ─────────────────────────

    private void OnPickTrailColor(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        string key = (sender as FrameworkElement)?.Tag as string ?? "uniform";
        var o = _host.Settings.GestureOptions;
        string initial = key == "uniform" ? o.TrailColor : o.TrailColorFor(key);
        var picked = PickColor(initial);
        if (picked == null) return;

        string hex = $"#{picked.Value.R:X2}{picked.Value.G:X2}{picked.Value.B:X2}";
        if (key == "uniform")
        {
            /* 统一色就是“所有键都回落到它”：清掉分键覆盖，
             * 否则改了统一色看起来没反应。 */
            o.TrailColor = hex;
            o.ButtonTrailColors ??= new Dictionary<string, string>();
            o.ButtonTrailColors.Clear();
        }
        else
        {
            o.ButtonTrailColors ??= new Dictionary<string, string>();
            o.ButtonTrailColors[key] = hex;
        }
        _host.SaveSettings();
        UpdateColorSwatches();
    }

    private void OnResetTrailColors(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.Settings.GestureOptions.ButtonTrailColors ??= new Dictionary<string, string>();
        _host.Settings.GestureOptions.ButtonTrailColors.Clear();
        _host.SaveSettings();
        UpdateColorSwatches();
    }

    private void UpdateColorSwatches()
    {
        if (_host == null) return;
        var o = _host.Settings.GestureOptions;

        /* 没配分键颜色时，画出来的是 WG2 的「未识别」色（RGBA），
           色块也跟着显示它 —— 老版本这里显示的是统一色，而那个色已经不再参与绘制。 */
        SetButtonSwatch(RightSwatch, o, "right");
        SetButtonSwatch(MiddleSwatch, o, "middle");
        SetButtonSwatch(X1Swatch, o, "x1");
        SetButtonSwatch(X2Swatch, o, "x2");
        SetButtonSwatch(LeftSwatch, o, "left");
    }

    private static void SetButtonSwatch(Border swatch, GestureOptions o, string key)
    {
        if (HasOverride(o, key))
        {
            SetSwatch(swatch, o.TrailColorFor(key), true);
            return;
        }
        SetRgbaSwatch(swatch, o.PathColorNormal);
        /* 淡到看得出是「跟随」，不是真的配了这色。 */
        swatch.Opacity = 0.28;
        swatch.ToolTip = Loc.T("未单独配色，跟随「未识别」色");
    }

    private static bool HasOverride(GestureOptions o, string key) =>
        o.ButtonTrailColors != null &&
        o.ButtonTrailColors.TryGetValue(key, out string? value) &&
        !string.IsNullOrWhiteSpace(value);

    private static void SetSwatch(Border swatch, string color, bool custom)
    {
        try
        {
            if (new BrushConverter().ConvertFromString(color) is Brush brush)
                swatch.Background = brush;
        }
        catch { /* 配置里写了坏颜色就当默认色，不要拦住整个页面 */ }
        swatch.Opacity = custom ? 1 : 0.45;
        swatch.ToolTip = color;
    }

    private static System.Drawing.Color? PickColor(string initial)
    {
        System.Drawing.Color start;
        try { start = System.Drawing.ColorTranslator.FromHtml(initial); }
        catch { start = System.Drawing.Color.FromArgb(0x3B, 0x82, 0xF6); }

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = start,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.Color : null;
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
        bool buttonless = g.Kind is "hotcorner" or "edgewheel";
        g.Button = buttonless ? "" : (SelectedValue(ButtonBox) ?? "");
        g.Chord = buttonless ? "" : (SelectedTag(ChordBox) ?? "");
        if (string.Equals(g.Chord, g.Button, StringComparison.OrdinalIgnoreCase)) g.Chord = "";
        g.Wheel = (g.Kind is "wheel" or "edgewheel") && int.TryParse(SelectedValue(WheelBox), out int w) ? w : 0;
        g.Corner = (g.Kind is "corner" or "wheel" or "hotcorner") && int.TryParse(SelectedValue(CornerBox), out int c) ? c : 0;
        g.Edge = g.Kind == "edgewheel" && int.TryParse(SelectedValue(EdgeBox), out int edge) ? edge : 0;
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
        /* 隐藏的兼容动作（打开网址 / 运行程序）目标就写在 Arg 里，参数框本来不显示，
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

    // ───────────────────────── 预览 ─────────────────────────

    private void DrawPreview()
    {
        PreviewLine.Points.Clear();
        PatternBadge.Text = "";

        /* 箭头退休（5.13）：识别只看形状，方向串不再是手势的"样子"，
         * 画成一串箭头只会让人以为还要照着方向串去画。没轨迹就直说没录过。 */
        if (!_current.HasPath)
        {
            PatternBadge.FontSize = 13;
            PatternBadge.FontWeight = FontWeights.Normal;
            if (TryFindResource("Brush.TextTertiary") is Brush tertiary)
                PatternBadge.Foreground = tertiary;
            PatternBadge.Text = Loc.T("还没有笔画：点「录制」画一笔，或者从别的软件导入一条。");
            return;
        }

        _current.EnsurePattern();

        double w = PreviewCanvas.ActualWidth > 10 ? PreviewCanvas.ActualWidth : 340;
        double h = PreviewCanvas.ActualHeight > 10 ? PreviewCanvas.ActualHeight : 118;
        const double pad = 14;

        var pts = GestureMath.Normalize(_current.Points());
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double spanX = Math.Max(1, maxX - minX), spanY = Math.Max(1, maxY - minY);
        double scale = Math.Min((w - pad * 2) / spanX, (h - pad * 2) / spanY);
        // 居中：直线笔画也不会贴着左边画
        double offX = (w - spanX * scale) / 2, offY = (h - spanY * scale) / 2;

        foreach (var p in pts)
            PreviewLine.Points.Add(new Point(offX + (p.X - minX) * scale, offY + (p.Y - minY) * scale));
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
    /// 手势列表的一行。<see cref="GripVisibility"/> 决定最左边那根拖动手柄露不露出来
    /// （只有「自定义顺序」+ 没在筛选时才给拖）。
    /// </summary>
    private sealed record GestureRow(
        MouseGesture Gesture, string Title, string Subtitle, string Badge, string Glyph,
        PointCollection? IconPoints, ImageSource? EdgeIcon,
        Visibility GlyphVisibility, Visibility StrokeVisibility, Visibility EdgeIconVisibility,
        Visibility GripVisibility,
        double RowOpacity, string StateText, Visibility StateVisibility);

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
        public string ToggleTooltip { get; init; } = "";
        public Visibility ToggleVisibility { get; init; } = Visibility.Collapsed;
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
