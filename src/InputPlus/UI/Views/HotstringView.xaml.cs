using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using InputPlus.Core;
using InputPlus.Core.Settings;

namespace InputPlus.UI.Views;

/// <summary>
/// 「别名替换」页：三栏 —— 左分组、中列表、右编辑。
///
/// 数据源是 <see cref="GlobalOptions.Snippets"/>（真正参与展开的那一份）。
/// 分组既可以从设置里的 <see cref="GlobalOptions.SnippetGroups"/> 手动新建，
/// 也会自动收进别名身上已经用到的组名 —— 以前只能"先有别名才有组"，
/// 用户没法先建一个空分组再往里放东西。
/// </summary>
public partial class HotstringView : UserControl
{
    private AppHost? _host;
    private Snippet? _editing;
    private bool _suppress;

    /// <summary>刷新分组列表后要选中的组名（改名/新建后用它把选中状态带过去）。</summary>
    private string? _selectGroupAfterRefresh;

    /// <summary>
    /// 下一次刷新**强制回到「全部」**（并清空搜索框）。
    ///
    /// 场景：用户停在「工作」组、搜索框里还有字，然后在剪贴板页加了一条未分组的别名。
    /// 如果照旧按当前分组 + 当前关键词过滤，新建的那条根本不在列表里 ——
    /// 选中为 null，编辑器被收起，用户看到的就是"点完什么都没发生"。
    /// </summary>
    private bool _focusAllGroups;

    /* 用户最后停在哪个分组（「全部」= null，「未分组」= ""）。
     * 新增别名用它兜底：分组列表在重建 ItemsSource 的瞬间没有选中项，
     * 那一刻 CurrentGroup 是 null —— 新别名会被扔进「未分组」，
     * 用户看到的就是"在分组里点新增，建出来的却不在这个组里"。 */
    private string? _lastGroup;

    /// <summary>自动对齐前缀只做一次，避免迁移后再次触发。</summary>
    private bool _prefixAlignedOnce;

    /* 列表顺序：默认按触发词；用户手动拖动重排过之后，改成按他自己排的 Order。
     * 以前这里有个「排序」下拉框（触发词/最近使用/使用次数/自定义），按用户要求
     * 连同「从未使用」勾选一起删掉了 —— 但"拖动重排"这个能力不能跟着消失，
     * 否则删一个控件等于删一个功能。所以这里只保留一个布尔开关：
     * 拖过一次就切到自定义顺序，点列头排序只影响这一次的显示，不改这个开关。 */
    private bool _customOrder;

    /// <summary>
    /// 列表当前这一屏的行（已按分组/筛选/排序过滤好）。
    /// 点列头排序时从这份**原始顺序**重排，不在排好的结果上再排 ——
    /// 否则两列值相同的行会保留上一次排序的次序，看起来像"越点越乱"。
    /// </summary>
    private List<Row> _entryRows = new();

    /* 列头排序状态自己记：DataGrid 的 SortDirection 会在 ItemsSource 换掉时被清掉，
     * 拿它当状态会永远"第一次点击"（用户看到的就是"点了不翻向"）。 */
    private string _aliasSortPath = "";
    private ListSortDirection _aliasSortDir = ListSortDirection.Ascending;

    /// <summary>列表里是否显示展开后的内容（眼睛按钮控制）。</summary>

    /* ---------------- 拖拽状态 ---------------- */

    /// <summary>按下左键时的位置（判断"是点击还是拖拽"）。</summary>
    private Point _dragStartPoint;

    /// <summary>按下左键时命中的别名（拖拽源）。</summary>
    private Snippet? _dragSnippet;

    /// <summary>当前高亮的分组行（拖拽经过时）。</summary>
    private ListBoxItem? _dragHoverItem;

    /// <summary>自定义顺序拖拽时的插入位置指示线。</summary>
    private UI.InsertLineAdorner? _insertLine;

    /// <summary>跟着鼠标走的拖拽小卡片。</summary>
    private UI.DragChipAdorner? _dragAdorner;

    /// <summary>分组行的 Group：null = 全部；"" = 未分组；其它 = 组名。</summary>
    private sealed class GroupRow
    {
        public string? Group { get; init; }
        public string Label { get; init; } = "";
        public int Count { get; init; }
        public bool IsReal => Group is { Length: > 0 };
        public override string ToString() => Label;
    }

    public sealed class Row
    {
        public Snippet Snippet { get; init; } = new();
        public string KeyLabel { get; init; } = "";

        /// <summary>列表左侧小方块里的首字母。</summary>
        public string Initial { get; init; } = "";

        /// <summary>右侧显示的分组名（没分组就是空）。</summary>
        public string GroupLabel { get; init; } = "";
        public string Preview { get; init; } = "";

        /// <summary>备注列（和正文分开：以前列表把备注塞进正文列，用户看到的全是备注）。</summary>
        public string CommentLabel { get; init; } = "";

        /// <summary>
        /// 左侧小方块里显示的**当前前缀符号**（例如 `;`）。
        ///
        /// 它跟着设置里的「前缀符号」走：改前缀会刷新列表，这里自然一起变。
        /// 前缀被清空（不需要前缀）时显示一个中点，避免出现空白方块。
        /// </summary>
        public string PrefixLabel { get; init; } = ";";

        /// <summary>停用的别名整行压暗，一眼能看出来（不用点进去看勾选）。</summary>
        public double Opacity => Snippet.Enabled ? 1.0 : 0.45;

        /// <summary>列表右侧的"用过几次"；从没用过就留空，避免一列全是 0。</summary>
        public string UsageLabel => Snippet.UseCount > 0 ? Snippet.UseCount.ToString() : "";

        public override string ToString() => KeyLabel;
    }

    public HotstringView()
    {
        InitializeComponent();

        /* 列宽：拖完表头把手攒 600ms 落盘（和 Qbar 设置页同一套）。 */
        EntryList.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnColumnGripperDragCompleted), handledEventsToo: true);

        /* 搜索框的分类：选中哪一列，就只搜那一列。
         * 三个标签直接沿用列表的列头文字（别名关键字 / 别名正文 / 备注），
         * 用户看到的列名和这里的选项一字不差，才不会猜"正文"到底指什么。 */
        FilterField.SetCategories(
            new UI.Controls.SearchCategory(Loc.T("别名关键字"), "key"),
            new UI.Controls.SearchCategory(Loc.T("别名正文"), "text"),
            new UI.Controls.SearchCategory(Loc.T("备注"), "comment"));

        /* 别名框要用输入法打中文，这里显式打开 IME（有些系统设置会把非编辑类窗口的
         * IME 关掉，WPF 不会自己再打开），并盯着"组字"状态。
         *
         * 关键在组字期间**什么都不做**：拼音没提交时 TextChanged 已经在连着触发，
         * 以前每个字母都会 RefreshList()（重设 ItemsSource）+ SaveSettings()，
         * 输入法的组合串被反复打断 —— 用户看到的就是"只能打英文，汉字出不来"。 */
        InputMethod.SetIsInputMethodEnabled(KeyBox, true);

        TextCompositionManager.AddPreviewTextInputStartHandler(KeyBox, (_, _) => _composing = true);

        TextCompositionManager.AddPreviewTextInputUpdateHandler(KeyBox, (_, args) =>
        {
            if ((args.TextComposition?.CompositionText?.Length ?? 0) == 0) _composing = false;
        });

        TextCompositionManager.AddPreviewTextInputHandler(KeyBox, (_, _) => _composing = false);

        KeyBox.LostKeyboardFocus += (_, _) =>
        {
            if (!_composing) return;
            _composing = false;
            OnEditorChanged(KeyBox, new RoutedEventArgs());
        };

    }

    /// <summary>别名框正在输入法组字（拼音还没提交成汉字）。</summary>
    private bool _composing;

    /// <summary>把剪贴板里的文字直接填进别名框：输入法在我们窗口里不工作时也能写中文。</summary>
    private void OnPasteTriggerClick(object sender, RoutedEventArgs e)
    {
        string text = "";
        try
        {
            if (ClipboardAccess.ContainsText()) text = ClipboardAccess.GetText()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取剪贴板");
        }

        if (text.Length == 0)
        {
            _host?.Notify(Loc.T("剪贴板里没有文字"));
            return;
        }

        KeyBox.Text = text;
        KeyBox.Focus();
        KeyBox.CaretIndex = KeyBox.Text.Length;
        OnEditorChanged(KeyBox, new RoutedEventArgs());
    }

    public void Initialize(AppHost host)
    {
        _host = host;
        var opt = host.Settings.Hotstring;

        _suppress = true;
        try
        {
            RefreshPrefixCapsule();
            SpaceBox.IsChecked = opt.ExpandOnSpace;
            SwallowBox.IsChecked = opt.SwallowSpace;
            /* 列表的正文 / 备注是两列，各显示各的（历史字段 ShowTextInList 保留只为兼容老配置，
             * 现在不再参与渲染 —— 见 BuildPreview / BuildCommentLabel）。 */
        }
        finally { _suppress = false; }

        ApplyColumnWidths();      // 用户拖过的列宽先摆回去，再灌数据
        Refresh();
    }

    private List<Snippet> Entries => _host?.Settings.Global.Snippets ?? new List<Snippet>();

    private List<string> CustomGroups => _host?.Settings.Global.SnippetGroups ?? new List<string>();

    private string? CurrentGroup => (GroupList.SelectedItem as GroupRow)?.Group;

    /// <summary>所有分组：手动建的 ∪ 别名里用到的，去重排序。</summary>
    private List<string> AllGroups()
        => _host == null ? new List<string>() : SnippetGroupOps.All(_host.Settings);

    // ---------------- 刷新 ----------------

    private void Refresh()
    {
        AlignStalePrefixOnce();
        if (_host == null) return;

        /* 待选中的别名**先取**（Take 只能取一次），再决定这一轮怎么刷。
         *
         * 顺序很重要：以前是"先刷完列表、再去取待选中项"，于是分组/搜索框
         * 会把新条目过滤掉，`TakePendingAlias()` 的那一次机会就白白丢了。
         * 现在先拿到触发词，就能在刷新之前把过滤条件清掉。 */
        string? pending = _host.TakePendingAlias();
        if (pending != null)
        {
            _focusAllGroups = true;

            /* 清搜索框不能触发 TextChanged 的二次刷新：
             * 那会在分组还没切回「全部」的时候按旧条件重刷一遍列表。 */
            _suppress = true;
            try { FilterField.ClearSearch(); }
            finally { _suppress = false; }
        }

        RefreshPrefixCapsule();
        RefreshGroups();                     // 它会选中「全部」
        RefreshList();
        if (pending != null) FocusSnippet(pending);   // 此时列表已是全量
        SyncEditorToSelection();
    }

    /// <summary>
    /// 胶囊上显示当前触发符号 + 它的中文名字（多前缀时加个 "+N"）。
    /// 前缀为空 = 不需要前缀，显示一个中点，避免胶囊里出现空白。
    /// </summary>
    private void RefreshPrefixCapsule()
    {
        if (PrefixCapsule == null || _host == null) return;

        var prefixes = _host.Settings.Hotstring.EffectivePrefixes;
        string symbol = prefixes.Count > 0 ? prefixes[0] : "";
        PrefixGlyphText.Text = symbol.Length > 0 ? symbol : "·";

        string label = HotstringOptions.LabelOf(symbol);
        string name = label.Length > 0
            ? Loc.T(label)
            : symbol.Length == 0 ? Loc.T("不需要前缀") : Loc.T("旧符号");
        if (prefixes.Count > 1) name += " +" + (prefixes.Count - 1).ToString();
        PrefixNameText.Text = name;
        PrefixCapsule.ToolTip = prefixes.Count == 0
            ? Loc.T("不需要前缀")
            : Loc.F("当前触发符号：{0}。点开可以换成别的", string.Join(" ", prefixes));
    }

    /// <summary>点胶囊：把可选符号列出来，当前那个打勾。</summary>
    private void OnPrefixCapsuleClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        BuildPrefixChoices();
        PrefixPopup.IsOpen = true;
    }

    private void BuildPrefixChoices()
    {
        PrefixChoiceList.Children.Clear();
        var prefixes = _host?.Settings.Hotstring.EffectivePrefixes ?? new List<string>();
        string current = prefixes.Count > 0 ? prefixes[0] : "";

        foreach (var (symbol, label) in HotstringOptions.SymbolChoices)
        {
            bool active = string.Equals(symbol, current, StringComparison.Ordinal);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = symbol,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Width = 22,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = Loc.T(label),
                Style = (Style)FindResource("Text.Body"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (active)
            {
                row.Children.Add(new TextBlock
                {
                    Text = "✓",
                    Margin = new Thickness(12, 0, 0, 0),
                    Foreground = (Brush)FindResource("Brush.Accent"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var item = new Button
            {
                Style = (Style)FindResource("Button.Ghost"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 1, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = symbol,
                Content = row,
                Focusable = false,
            };
            item.Click += OnPrefixChoiceClick;
            PrefixChoiceList.Children.Add(item);
        }
    }

    private void OnPrefixChoiceClick(object sender, RoutedEventArgs e)
    {
        PrefixPopup.IsOpen = false;
        if (sender is Button { Tag: string symbol }) ApplyPrefixSymbol(symbol);
    }

    /// <summary>
    /// 换触发符号：条目里的触发词要跟着搬（`;mail` → `:mail`），否则列表剥不掉旧前缀。
    /// 新符号与当前一致时什么都不做。
    /// </summary>
    private void ApplyPrefixSymbol(string symbol)
    {
        if (_host == null || symbol.Length == 0) return;

        var opt = _host.Settings.Hotstring;
        var before = opt.EffectivePrefixes.ToList();
        if (before.Count == 1 && string.Equals(before[0], symbol, StringComparison.Ordinal)) return;

        opt.Prefixes = new List<string> { symbol };
        opt.NormalizePrefixes();

        int moved = SnippetPrefixOps.MigrateAll(_host.Settings, before, opt.EffectivePrefixes);
        CoreLog.Info($"改触发符号：{string.Join("", before)} → {string.Join("", opt.EffectivePrefixes)}，迁移 {moved} 条");

        _host.SaveSettings();
        Refresh();
        _host.Notify(Loc.F("触发符号已改为「{0}」", symbol));
    }

    private void RefreshGroups()
    {
        if (_host == null) return;

        /* 「全部」= Group 为 null（见下面构造 rows 的第一行）。
         * _focusAllGroups 为真时（刚加完别名）不管用户原来停在哪一组都回到「全部」，
         * 否则维持原来的行为：优先用这一轮显式指定的组，其次用当前选中的组。 */
        string? keepGroup = ResolveKeepGroup(_focusAllGroups, _selectGroupAfterRefresh, CurrentGroup);
        _focusAllGroups = false;
        _selectGroupAfterRefresh = null;
        var all = Entries.ToList();
        var groups = AllGroups();

        var savedOrder = _host.Settings.Global.SnippetGroupOrder ?? new List<string>();

        int CountOf(List<Snippet> all, string g)
            => all.Count(x => string.Equals(x.Group?.Trim(), g, StringComparison.CurrentCultureIgnoreCase));

        int OrderIndex(string g)
        {
            int i = savedOrder.FindIndex(o => string.Equals(o?.Trim(), g, StringComparison.CurrentCultureIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }

        /* 分组只用"自定义顺序"：没拖过的组按名称排在后面，拖过的按保存的顺序。 */
        var ordered = groups.OrderBy(OrderIndex).ThenBy(g => g, StringComparer.CurrentCulture);

        var rows = new List<GroupRow> { new() { Group = null, Label = Loc.T("全部"), Count = all.Count } };
        rows.AddRange(ordered.Select(g => new GroupRow
        {
            Group = g,
            Label = g,
            Count = CountOf(all, g),
        }));
        rows.Add(new GroupRow
        {
            Group = "",
            Label = Loc.T("未分组"),
            Count = all.Count(x => string.IsNullOrWhiteSpace(x.Group)),
        });

        _suppress = true;
        try
        {
            GroupList.ItemsSource = rows;
            GroupList.SelectedItem = rows.FirstOrDefault(g => g.Group == keepGroup) ?? rows[0];

            /* 「全部 / 未分组」不是真分组，改名和删除对它们没有意义。 */
            bool real = (GroupList.SelectedItem as GroupRow)?.IsReal == true;
            RenameGroupButton.IsEnabled = real;
            DeleteGroupButton.IsEnabled = real;
        }
        finally { _suppress = false; }
    }

    private void RefreshList()
    {
        if (_host == null) return;

        var opt = _host.Settings.Hotstring;
        string filter = FilterField.SearchText?.Trim() ?? "";
        string column = FilterField.SelectedTag ?? "key";
        string? group = CurrentGroup;
        var all = Entries.ToList();

        var filtered = all
            .Where(x => group == null
                        || (group.Length == 0
                            ? string.IsNullOrWhiteSpace(x.Group)
                            : string.Equals(x.Group?.Trim(), group, StringComparison.CurrentCultureIgnoreCase)))
            .Where(x => MatchesColumn(x, column, filter, opt.EffectivePrefixes));

        var rows = OrderEntries(filtered)
            .Select(x => new Row
            {
                Snippet = x,
                KeyLabel = SnippetPrefixOps.StripAny(x.Trigger, opt.EffectivePrefixes),
                Initial = InitialOf(SnippetPrefixOps.StripAny(x.Trigger, opt.EffectivePrefixes)),
                GroupLabel = string.IsNullOrWhiteSpace(x.Group) ? "" : x.Group!.Trim(),
                CommentLabel = BuildCommentLabel(x),
                PrefixLabel = opt.EffectivePrefixes.Count == 0
                    ? "·"
                    : string.Join("", opt.EffectivePrefixes),
                Preview = BuildPreview(x),
            })
            .ToList();

        /* 列头上还按着某一列排的话，重建列表之后要接着按它排 ——
         * 否则改一个字、刷新一次，顺序就跳回下拉框那套，看着像排序失灵。 */
        SortRowsByColumn(rows, ActiveSortColumn(), ActiveSortDirection());
        if (_aliasSortPath.Length > 0) SetSortArrow(_aliasSortPath, _aliasSortDir);

        _suppress = true;
        try
        {
            _entryRows = rows;
            EntryList.ItemsSource = rows;
            FilterCount.Text = Loc.F("共 {0} 条", rows.Count);
            EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            /* 空状态要说清"为什么空"：是没建过，还是筛选没命中，还是这个分组空着。
             * 统一一句"还没有别名"会让用户以为数据丢了。 */
            EmptyHint.Text = rows.Count != 0 ? "" :
                filter.Length > 0
                    ? Loc.F("没有匹配「{0}」的别名。", filter)
                    : (group is { Length: > 0 } g
                        ? Loc.F("「{0}」这个分组还空着。可以把别名拖进来，或右键「移到分组」。", g)
                        : Loc.T("还没有别名。点下面的「新增」建一条。"));
            EntryList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Snippet, _editing));
        }
        finally { _suppress = false; }
    }

    private void SyncEditorToSelection()
    {
        int selected = EntryList.SelectedItems.Count;
        bool canAct = CanActOnSelection(selected);
        MoveButton.IsEnabled = canAct;
        DeleteButton.IsEnabled = canAct;
        // 底部工具条 v1.6.3 起是图标按钮（Content 是字体里的字形），不能再往里写文字 ——
        // 写进去就变成「图标字体里没有的中文」= 一排空心方框。选中的条数改由提示与读屏名带出来。
        string moveTip = selected > 1 ? Loc.F("把选中的 {0} 条别名移到某个分组里", selected) : Loc.T("把选中的别名放到某个分组里（也可以直接把别名拖到左侧分组上）");
        string deleteTip = selected > 1 ? Loc.F("删除选中的 {0} 条别名", selected) : Loc.T("删除选中的别名");
        MoveButton.ToolTip = moveTip;
        DeleteButton.ToolTip = deleteTip;
        System.Windows.Automation.AutomationProperties.SetName(MoveButton, moveTip);
        System.Windows.Automation.AutomationProperties.SetName(DeleteButton, deleteTip);

        if (EntryList.SelectedItem is Row row)
        {
            if (!ReferenceEquals(row.Snippet, _editing)) LoadEditor(row.Snippet);
        }
        else
        {
            MoveButton.IsEnabled = false;
            _editing = null;
            ShowEditor(false);
        }
    }

    /// <summary>
    /// 打开页面时自动对齐一次旧前缀。
    ///
    /// 场景：老版本改前缀只改设置、不改条目，于是配置里所有触发词都还带着旧符号
    /// （;mail），而当前前缀已经是 / —— 列表按 / 去剥，剥不掉，每条前面都多出一个 ;。
    ///
    /// 只在**完全无歧义**时自动做：所有带符号前缀的条目用的都是同一个符号，
    /// 且它和当前前缀不同。混合情况交给用户改前缀（改的时候会自动迁移）。
    /// </summary>
    private void AlignStalePrefixOnce()
    {
        if (_prefixAlignedOnce || _host == null) return;
        _prefixAlignedOnce = true;

        var prefixes = _host.Settings.Hotstring.EffectivePrefixes;

        /* 配了多个前缀时不再自动"对齐"：用户可能就是想同时用 `;` 和 `、`，
         * 猜错会把好好的条目全搬走。 */
        if (prefixes.Count != 1) return;
        string current = prefixes[0];

        /* 新旧符号混着的时候 DetectStale 会返回 null —— 那种情况不猜，
         * 交给用户改一次前缀（改前缀一定会整批迁移）。 */
        char? stale = SnippetPrefixOps.DetectStale(Entries, current, out int count);
        if (stale == null || count == 0) return;

        int moved = SnippetPrefixOps.Migrate(_host.Settings, stale.Value.ToString(), current);
        if (moved == 0) return;

        CoreLog.Info($"别名前缀自动对齐：{stale} → {current}，共 {moved} 条");
        _host.SaveSettings();
        _host.Notify(Loc.F("已把 {0} 条别名的旧前缀统一成「{1}」", moved, current));
    }

    // ---------------- 小工具 ----------------

    /// <summary>列表左侧那个圆形首字（已经剥掉前缀的显示名传进来）。</summary>
    private static string InitialOf(string? name)
    {
        string s = name ?? "";
        if (s.Length == 0) return "?";
        char c = char.ToUpperInvariant(s[0]);
        return char.IsLetterOrDigit(c) ? c.ToString() : s[..1];
    }

    private static string Shorten(string? s, int max)
    {
        s = (s ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    private Window? Owner => Window.GetWindow(this);

    private void ShowEditor(bool show)
    {
        EditorCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            /* 每次打开都从顶上开始看：上一次滚到"分组/启用"那一段，再打开另一条
             * 别名时直接停在中段，用户会以为前面的字段没了。 */
            if (EditorScroll != null) EditorScroll.ScrollToTop();
            return;
        }

        _suppress = true;
        try
        {
            KeyBox.Text = "";
            ValueBox.Text = "";
            CommentBox.Text = "";
            AppsBox.Text = "";
            GroupBox.Text = "";
            EnabledBox.IsChecked = false;
            StatusText.Text = "";
        }
        finally { _suppress = false; }
    }

    /// <summary>
    /// 收起编辑栏（标题右边的叉、Esc、以及"选中项没了"三条路共用）。
    ///
    /// 先把列表选中项清掉，再走 <see cref="SyncEditorToSelection"/> 那条
    /// "没有选中 → 收起"的路：_editing、删除/移动按钮的可用状态、编辑栏清空
    /// 都只经一份实现，不会出现"关掉了但删除按钮还亮着"这种半截状态。
    /// </summary>
    private void CloseEditor()
    {
        _editing = null;
        EntryList.UnselectAll();
        ShowEditor(false);
        EntryList.Focus();
    }

    private void OnCloseEditorClick(object sender, RoutedEventArgs e) => CloseEditor();

    private void LoadEditor(Snippet entry)
    {
        _editing = entry;
        ShowEditor(true);

        _suppress = true;
        try
        {
            KeyBox.Text = SnippetPrefixOps.StripAny(entry.Trigger, _host?.Settings.Hotstring.EffectivePrefixes);
            EditTitle.Text = Loc.F("正在编辑：{0}", KeyBox.Text.Length == 0 ? Loc.T("（新别名）") : KeyBox.Text);
            ValueBox.Text = entry.Text ?? "";
            CommentBox.Text = entry.Comment ?? "";
            AppsBox.Text = entry.Apps ?? "";
            EnabledBox.IsChecked = entry.Enabled;
            StatusText.Text = "";
            StatusText.Foreground = (Brush)FindResource("Brush.TextTertiary");

            GroupBox.Text = entry.Group ?? "";

            /* 使用情况：帮用户判断这条还留着有没有意义。 */
            UsageText.Text = entry.UseCount <= 0
                ? Loc.T("还没用过")
                : (entry.LastUsed is { } at
                    ? Loc.F("用过 {0} 次 · 最近 {1:MM-dd HH:mm}", entry.UseCount, at)
                    : Loc.F("用过 {0} 次", entry.UseCount));
        }
        finally { _suppress = false; }
    }

    // ---------------- 分组管理 ----------------

    /// <summary>新建分组，返回组名；用户取消返回 null。</summary>
    private string? AskNewGroup()
    {
        string? name = UI.Windows.TextPromptWindow.Ask(Owner,
            Loc.T("新建分组"),
            Loc.T("起一个名字，之后右键列表里的别名即可「移到分组」。"));

        if (string.IsNullOrWhiteSpace(name)) return null;

        name = name.Trim();
        if (!SnippetGroupOps.Add(_host!.Settings, name))
        {
            _host.Notify(Loc.F("分组「{0}」已经存在", name));
            return name;
        }

        _host.SaveSettings();
        RefreshGroups();
        return name;
    }

    /* ---------------- 分组选择弹层 ---------------- */

    private void OnPickGroupClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var items = new List<string> { "" };
        items.AddRange(AllGroups().Where(g => g.Length > 0));

        GroupPickerList.ItemsSource = items;
        GroupPickerList.SelectedItem = items.FirstOrDefault(g =>
            string.Equals(g, GroupBox.Text?.Trim() ?? "", StringComparison.CurrentCultureIgnoreCase));

        GroupPicker.IsOpen = true;
    }

    private void OnGroupPickerPicked(object sender, MouseButtonEventArgs e)
    {
        if (GroupPickerList.SelectedItem is not string picked) return;

        GroupPicker.IsOpen = false;
        GroupBox.Text = picked;
        GroupBox.CaretIndex = GroupBox.Text.Length;
        SaveEditorFields();
    }

    /* ---------------- 拖拽：把别名拖到分组上 ---------------- */

    private void OnEntryMouseDown(object sender, MouseButtonEventArgs e)
    {
        /* 列头按下 = 排序（行上的按下才是拖拽起手）。 */
        if (SortFromHeader(e.OriginalSource, "down")) return;

        _dragStartPoint = e.GetPosition(EntryList);
        _dragSnippet = null;

        var item = ItemsControl.ContainerFromElement(EntryList, e.OriginalSource as DependencyObject) as DataGridRow;
        if (item?.DataContext is Row row) _dragSnippet = row.Snippet;
    }

    private void OnEntryMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSnippet == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        Point now = e.GetPosition(EntryList);
        if (Math.Abs(now.X - _dragStartPoint.X) < 8 && Math.Abs(now.Y - _dragStartPoint.Y) < 8) return;

        var payload = new DataObject("clp/snippet", _dragSnippet);
        var snippet = _dragSnippet;
        _dragSnippet = null;

        /* 拖拽卡片：
         *   · 挂在**窗口**的装饰层上（挂在列表上会被列表边界裁掉，
         *     鼠标一离开列表就"卡在边缘"，正是用户看到的卡顿感）；
         *   · 位置由 60fps 的定时器从 Mouse.GetPosition 直接读取，
         *     不再依赖 DragOver 事件 —— 那样只有在拖过目标控件时才会更新。
         */
        string label = SnippetPrefixOps.StripAny(snippet.Trigger, _host?.Settings.Hotstring.EffectivePrefixes);
        if (string.IsNullOrEmpty(label))
            label = Shorten(snippet.Comment, 24);

        /* 装饰层挂在**页面自己**身上：Window 本身没有 AdornerLayer，
         * GetAdornerLayer(window) 返回 null —— 之前卡片压根没画出来就是这个原因。
         *
         * 位置更新用 GiveFeedback：DoDragDrop 期间是 OLE 自己的消息循环，
         * WPF 的 DispatcherTimer 不会走，只有 GiveFeedback 会被持续触发。
         * （这也就是用户看到的"卡片不动 / 到边界卡住"。） */
        _dragAdorner = UI.DragChipAdorner.Show(this, label, UI.DragChipAdorner.CursorIn(this));

        GiveFeedbackEventHandler follow = (_, args) =>
        {
            if (_dragAdorner != null)
                _dragAdorner.MoveTo(UI.DragChipAdorner.CursorIn(this));
        };

        EntryList.GiveFeedback += follow;
        try
        {
            DragDrop.DoDragDrop(EntryList, payload, DragDropEffects.Move);
        }
        finally
        {
            EntryList.GiveFeedback -= follow;
            _dragAdorner?.Remove();
            _dragAdorner = null;
            RemoveInsertLine();
            ClearDragHover();
            Mouse.UpdateCursor();
        }
    }

    /* ---- 分组拖拽排序 ---- */

    private Point _groupDragStart;
    private string? _dragGroupName;
    private UI.DragChipAdorner? _groupDragChip;
    private UI.InsertLineAdorner? _groupInsertLine;

    private void OnGroupMouseDown(object sender, MouseButtonEventArgs e)
    {
        _groupDragStart = e.GetPosition(GroupList);

        /* 关键：用 e.OriginalSource 往上找 ListBoxItem。
         * InputHitTest 在这种带 ScrollViewer 的 ListBox 上经常只返回 ScrollViewer，
         * 于是永远拿不到行、拖拽从一开始就没启动过。别名列表就是这么写的，一直好用。 */
        var item = ItemsControl.ContainerFromElement(GroupList, e.OriginalSource as DependencyObject) as ListBoxItem;
        _dragGroupName = (item?.DataContext as GroupRow) is { IsReal: true } row ? row.Group : null;
    }

    /// <summary>
    /// 分组拖拽：与别名列表用同一套 DragDrop 流程（那边已验证可用）。
    /// 之前自己算鼠标捕获/Thumb 都不稳，改成标准拖放最可靠。
    /// </summary>
    private void OnGroupMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragGroupName == null || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(GroupList);
        if (Math.Abs(now.X - _groupDragStart.X) < 8 && Math.Abs(now.Y - _groupDragStart.Y) < 8) return;

        string dragged = _dragGroupName;
        _dragGroupName = null;

        /* 拖动时让分组名"贴着鼠标"：用和别名列表同一个拖拽卡片。
         * DoDragDrop 期间是 OLE 的消息循环，只有 GiveFeedback 会被持续触发，
         * 所以卡片位置在 GiveFeedback 里更新。 */
        _groupDragChip = UI.DragChipAdorner.Show(this, dragged, UI.DragChipAdorner.CursorIn(this));

        GiveFeedbackEventHandler follow = (_, _) =>
        {
            _groupDragChip?.MoveTo(UI.DragChipAdorner.CursorIn(this));
        };

        GroupList.GiveFeedback += follow;
        try
        {
            var data = new DataObject("clp/group", dragged);
            DragDrop.DoDragDrop(GroupList, data, DragDropEffects.Move);
        }
        finally
        {
            GroupList.GiveFeedback -= follow;
            _groupDragChip?.Remove();
            _groupDragChip = null;
            RemoveGroupInsertLine();
            ClearDragHover();
        }
    }

    private void RemoveGroupInsertLine()
    {
        _groupInsertLine?.Remove();
        _groupInsertLine = null;
    }

    private void OnGroupDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("clp/snippet") && !e.Data.GetDataPresent("clp/group"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var hoverItem = ItemsControl.ContainerFromElement(GroupList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var target = hoverItem?.DataContext as GroupRow ?? FindGroupRowAt(e.GetPosition(GroupList));
        if (target is not { IsReal: true } && target is not { Group: "" })
        {
            /* 「全部」只是个视图，不接受放下。 */
            ClearDragHover();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        HighlightDragHover(target);

        /* 插入指示线：落在目标行的上半 → 画在行的上沿（插到它前面）；
         * 下半 → 画在下沿（插到它后面）。空白处 → 画在列表末尾。 */
        _groupInsertLine ??= UI.InsertLineAdorner.Show(GroupList);
        if (_groupInsertLine != null)
        {
            double y;
            if (target == null)
            {
                y = GroupList.ActualHeight;
            }
            else if (GroupList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem container)
            {
                double top = container.TranslatePoint(new Point(0, 0), GroupList).Y;
                y = e.GetPosition(GroupList).Y > top + container.ActualHeight / 2
                    ? top + container.ActualHeight
                    : top;
            }
            else
            {
                y = GroupList.ActualHeight;
            }

            _groupInsertLine.MoveTo(y);
        }

        e.Handled = true;
    }

    private void OnGroupDragLeave(object sender, DragEventArgs e)
    {
        ClearDragHover();
        RemoveGroupInsertLine();
    }

    private void OnGroupDrop(object sender, DragEventArgs e)
    {
        ClearDragHover();

        RemoveGroupInsertLine();

        if (e.Data.GetData("clp/group") is string draggedGroup)
        {
            var dropItem = ItemsControl.ContainerFromElement(GroupList, e.OriginalSource as DependencyObject) as ListBoxItem;
            var dropRow = dropItem?.DataContext as GroupRow;
            if (dropRow is not { IsReal: true } || _host == null) return;

            var names = (GroupList.ItemsSource as List<GroupRow> ?? new List<GroupRow>())
                .Where(r => r.IsReal).Select(r => r.Group!).ToList();

            /* 先移除被拖动的组，再计算目标锚点。否则拖到相邻组下半区时，
             * before 会正好等于 draggedGroup，Reorder 会把它追加到列表末尾。 */
            names.RemoveAll(g => string.Equals(g, draggedGroup, StringComparison.CurrentCultureIgnoreCase));

            int to = names.FindIndex(g => string.Equals(g, dropRow.Group, StringComparison.CurrentCultureIgnoreCase));
            if (to < 0) return;

            /* 指针在目标行下半 → 插到它后面；上半 → 插到它前面。 */
            bool after = dropItem != null && e.GetPosition(dropItem).Y > dropItem.ActualHeight / 2;
            string? before = after
                ? (to + 1 < names.Count ? names[to + 1] : null)
                : names[to];
            SnippetGroupOps.Reorder(_host.Settings, draggedGroup, before);
            _host.SaveSettings();
            _host.Notify(Loc.F("已调整「{0}」的位置", draggedGroup));

            _selectGroupAfterRefresh = draggedGroup;
            RefreshGroups();
            e.Handled = true;
            return;
        }

        if (e.Data.GetData("clp/snippet") is not Snippet snippet) return;

        var target = FindGroupRowAt(e.GetPosition(GroupList));
        if (target == null) return;

        if (target.Group is null)
        {
            /* 全部：不当作分组，只提示一句。 */
            _host?.Notify(Loc.T("「全部」只是查看全部别名，不能作为分组；要取消分组请拖到「未分组」"));
            return;
        }

        MoveTo(snippet, target.Group);
        FlashGroupRow(target);
        e.Handled = true;
    }

    /// <summary>找出某个坐标下的分组行（没有就是 null）。</summary>
    private GroupRow? FindGroupRowAt(Point point)
    {
        var hit = GroupList.InputHitTest(point) as DependencyObject;
        var item = ItemsControl.ContainerFromElement(GroupList, hit) as ListBoxItem;
        return item?.DataContext as GroupRow;
    }

    /// <summary>拖拽经过时把目标分组"抬起来"一点 + 加底色。</summary>
    private void HighlightDragHover(GroupRow row)
    {
        if (GroupList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item) return;
        if (ReferenceEquals(item, _dragHoverItem)) return;

        ClearDragHover();
        _dragHoverItem = item;

        item.Background = (Brush)FindResource("Brush.AccentSoft");
        item.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        item.RenderTransform = scale;

        var grow = new DoubleAnimation(1, 1.03, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void ClearDragHover()
    {
        if (_dragHoverItem == null) return;

        var item = _dragHoverItem;
        _dragHoverItem = null;

        var fade = new DoubleAnimation(1.03, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        if (item.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, fade);
        }

        var brush = new SolidColorBrush(Colors.Transparent);
        item.Background = brush;
    }

    /// <summary>放下之后给目标分组一个"闪一下"的反馈。</summary>
    private void FlashGroupRow(GroupRow row)
    {
        if (GroupList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item) return;

        var brush = new SolidColorBrush(((SolidColorBrush)FindResource("Brush.Accent")).Color)
        {
            Opacity = 0.55,
        };
        item.Background = brush;

        var fade = new DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        fade.Completed += (_, _) => item.Background = Brushes.Transparent;
        brush.BeginAnimation(SolidColorBrush.OpacityProperty, fade);
    }

    /// <summary>保存编辑区里的字段（供弹层选择后调用）。</summary>
    private void SaveEditorFields()
    {
        if (_host == null || _editing == null) return;

        _editing.Group = GroupBox.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(_editing.Group))
            SnippetGroupOps.Add(_host.Settings, _editing.Group);

        _host.SaveSettings();
        RefreshList();
        RefreshGroups();
    }

    private void OnNewGroupClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        string? name = AskNewGroup();
        if (name == null) return;

        // 建完直接选中它，用户马上就能看到"空分组"
        if (GroupList.ItemsSource is List<GroupRow> rows)
        {
            var target = rows.FirstOrDefault(g =>
                string.Equals(g.Group, name, StringComparison.CurrentCultureIgnoreCase));
            if (target != null) GroupList.SelectedItem = target;
        }
    }

    /// <summary>
    /// 分组右键菜单（Shift+F10 / 菜单键也走这里 —— 所以用 ContextMenuOpening，
    /// 而不是只绑鼠标事件）。
    /// </summary>
    private void OnGroupContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_host == null
            || e.Source is not FrameworkElement source
            || source.ContextMenu is not ContextMenu menu)
        {
                e.Handled = true;
            return;
        }

        GroupRow? row = null;

        /* 事件源就是被右键的那个列表项（键盘下是当前项）。 */
        row = source.DataContext as GroupRow;

        if (row is not { IsReal: true })
        {
            e.Handled = true;
            return;
        }

        GroupList.SelectedItem = row;

        menu.Items.Clear();

        var up = new MenuItem { Header = Loc.T("上移") };
        up.Click += (_, _) => MoveGroupBy(row.Group!, -1);
        menu.Items.Add(up);

        var down = new MenuItem { Header = Loc.T("下移") };
        down.Click += (_, _) => MoveGroupBy(row.Group!, +1);
        menu.Items.Add(down);

        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = Loc.T("重命名分组…") };
        rename.Click += (_, _) => RenameGroup(row.Group!);
        menu.Items.Add(rename);

        var del = new MenuItem { Header = Loc.T("删除分组") };
        del.Click += (_, _) => DeleteGroup(row.Group!, row.Count);
        menu.Items.Add(del);

        /* 菜单已经挂在控件上（XAML 里那个空 ContextMenu），
         * WPF 会在本方法返回后自己打开 —— 这里不要再手动 IsOpen。 */
    }

    /// <summary>
    /// 把分组上移/下移一格（右键菜单与 Alt+↑/↓ 都走这里）。
    ///
    /// 拖拽受鼠标环境影响（远程桌面、触摸板都可能拖不动），所以保留一条
    /// **键盘/菜单**的通路，保证排序功能在任何情况下都可用。
    /// </summary>
    private void MoveGroupBy(string group, int delta)
    {
        if (_host == null) return;

        var names = (GroupList.ItemsSource as List<GroupRow> ?? new List<GroupRow>())
            .Where(r => r.IsReal)
            .Select(r => r.Group!)
            .ToList();

        int i = names.FindIndex(g => string.Equals(g, group, StringComparison.CurrentCultureIgnoreCase));
        int j = i + delta;
        if (i < 0 || j < 0 || j >= names.Count) return;

        /* Reorder 的语义是"插到 before 之前"：
         *  上移 → 插到前一个之前；
         *  下移 → 插到后一个的后面（即再下一个之前；没有下一个就放末尾）。 */
        string? before = delta < 0
            ? names[j]
            : (j + 1 < names.Count ? names[j + 1] : null);

        SnippetGroupOps.Reorder(_host.Settings, group, before);
        _host.SaveSettings();

        _selectGroupAfterRefresh = group;
        RefreshGroups();
    }

    private void OnGroupMoveUp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupRow { IsReal: true } row)
            MoveGroupBy(row.Group!, -1);
    }

    private void OnGroupMoveDown(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupRow { IsReal: true } row)
            MoveGroupBy(row.Group!, +1);
    }

    private void OnGroupListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down)) return;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        if (GroupList.SelectedItem is not GroupRow { IsReal: true } row) return;

        MoveGroupBy(row.Group!, e.Key == Key.Up ? -1 : +1);
        e.Handled = true;
    }

    private void OnRenameGroupClick(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is GroupRow { IsReal: true } row) RenameGroup(row.Group!);
    }

    private void OnDeleteGroupClick(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is GroupRow { IsReal: true } row) DeleteGroup(row.Group!, row.Count);
    }

    private void RenameGroup(string oldName)
    {
        if (_host == null) return;

        string? name = UI.Windows.TextPromptWindow.Ask(Owner,
            Loc.T("重命名分组"),
            Loc.T("改名之后，这个组里的别名会一起跟过去。"), oldName);

        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (string.Equals(name, oldName, StringComparison.Ordinal)) return;

        if (SnippetGroupOps.Rename(_host.Settings, oldName, name) < 0)
        {
            _host.Notify(Loc.F("分组「{0}」已经存在", name));
            return;
        }

        _host.SaveSettings();
        _selectGroupAfterRefresh = name;
        Refresh();
        _host.Notify(Loc.F("分组已改名为「{0}」", name));
    }

    private void DeleteGroup(string name, int count)
    {
        if (_host == null) return;

        string question = count > 0
            ? Loc.F("删除分组「{0}」？\n\n这个分组里有 {1} 个别名，它们会回到「未分组」，不会被删除。", name, count)
            : Loc.F("删除分组「{0}」？", name);

        if (!UI.Windows.AppDialog.ConfirmDanger(Owner, question, Loc.T("删除分组"),
                okText: Loc.T("删除"))) return;

        SnippetGroupOps.Delete(_host.Settings, name);

        _host.SaveSettings();
        Refresh();
        _host.Notify(Loc.F("已删除分组「{0}」", name));
    }

    private void MoveTo(Snippet snippet, string group)
    {
        if (_host == null) return;

        SnippetGroupOps.Move(_host.Settings, snippet, group);

        _host.SaveSettings();
        Refresh();
        _host.Notify(string.IsNullOrWhiteSpace(group)
            ? Loc.T("已移到「未分组」")
            : Loc.F("已移到「{0}」", group));
    }

    /// <summary>
    /// 选中指定的那条别名（全局搜索、以及"从剪贴板加别名"两条路径共用）。
    ///
    /// 触发词来自 <see cref="AppHost.PendingAliasTrigger"/>，是**程序自己存下来的原文**，
    /// 所以逐字比较（Ordinal）就够；用区域文化忽略大小写比较反而会把 `;Mail`
    /// 和 `;mail` 这两条不同的别名串成一条。
    ///
    /// 前提：调用它的时候列表已经是全量（强制「全部」+ 清空搜索由 Refresh 负责）。
    /// </summary>
    private void FocusSnippet(string trigger)
    {
        var snippet = Entries.FirstOrDefault(x =>
            string.Equals((x.Trigger ?? "").Trim(), trigger, StringComparison.Ordinal));
        if (snippet == null) return;

        _editing = snippet;
        RefreshList();                 // 让 SelectedItem 落到这条上
        LoadEditor(snippet);
        if (EntryList.SelectedItem is Row row) EntryList.ScrollIntoView(row);
    }

    /// <summary>当前选中的别名（多选时是全部；没选中时退回编辑区那一条）。</summary>
    private List<Snippet> SelectedSnippets()
    {
        var list = new List<Snippet>();
        foreach (var item in EntryList.SelectedItems)
            if (item is Row row) list.Add(row.Snippet);

        if (list.Count == 0 && _editing != null) list.Add(_editing);
        return list;
    }

    // ---------------- 事件 ----------------

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        var opt = _host.Settings.Hotstring;

        opt.ExpandOnSpace = SpaceBox.IsChecked == true;
        opt.SwallowSpace = SwallowBox.IsChecked == true;
        /* RestoreImeAfterExpand 不再有界面、也不再被执行（软件不替用户切输入法）。
         * 老配置里的 true 保留在设置里不动，读得出来、也不会被误当成"用户刚改的"。 */

        _host.SaveSettings();
        RefreshList();
        if (_editing != null) LoadEditor(_editing);
    }

    /// <summary>搜索框里按 Esc 且框里已经空了：焦点回列表（框自己负责清空那一层）。</summary>
    private void OnFilterCancelled(object sender, RoutedEventArgs e) => EntryList.Focus();

    /* ─────────────────── 列表列头：排序（自己接管） ───────────────────
       规则：第一次点升序、第二次降序、第三次又升序，永远在这两态之间翻；
       换一列从升序开始。每次从 _entryRows 的**原始顺序**重排，不叠加。
       为什么不让 DataGrid 自己排：它把 SortDescription 挂到 ItemsSource 的
       CollectionView 上，而我们每次刷新都会换掉 ItemsSource —— 换完它再排一遍，
       两套比较器打架，屏幕顺序和列头指示就对不上（用户报的"点了不排序"）。
       箭头也是我们自己写进列头文字的：CanUserSortColumns=False 之后 DataGrid 不画。 */

    /// <summary>
    /// 从事件源往上找列头，找到就排序。返回 true = 这一下点在列头上。
    ///
    /// 为什么绑的是"按下"而不是"抬起"或 <c>ButtonBase.Click</c>：
    ///   · Click 绑在"能不能排序"上，关掉 CanUserSortColumns 之后就不发了；
    ///   · 抬起那条路会被 DataGrid 的鼠标捕获吃掉（第二下点同一个列头不再冒泡到我们这里）；
    ///   · 按下一定会到，而且"点一下就变"的手感也更快。
    /// </summary>
    private bool SortFromHeader(object? source, string via)
    {
        if (_host == null) return false;

        var header = FindHeader(source);
        if (header == null)
        {
            /* 鼠标被上一次点击的捕获占住时，OriginalSource 不是列头 —— 按坐标补一次命中测试。
             * 这正是"第一下能排、第二下没反应"的原因。 */
            header = FindHeader(EntryList.InputHitTest(Mouse.GetPosition(EntryList)) as DependencyObject);
        }

        if (header?.Column == null) return false;
        string path = header.Column.SortMemberPath ?? "";
        if (path.Length == 0) return false;

        /* 同列再点 → 翻方向；换列 → 从升序开始。永远在"升/降"之间翻，
         * 不会出现第三次点变成"不排序"。 */
        _aliasSortDir = string.Equals(path, _aliasSortPath, StringComparison.Ordinal)
                        && _aliasSortDir == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _aliasSortPath = path;

        ApplyAliasSort(path, _aliasSortDir);
        SetSortArrow(path, _aliasSortDir);

        /* 列头在 mouse-down 时捕获鼠标，而我们同一拍就换掉了 ItemsSource，
         * 它可能收不到 mouse-up，捕获一直挂着 —— 下一次点击就被它吃掉。
         * 排完主动放开，保证连续点列头每次都生效。 */
        if (Mouse.Captured is DependencyObject cap && EntryList.IsAncestorOf(cap)) Mouse.Capture(null);

        AliasSortTrace($"{via} {path} {_aliasSortDir} → {string.Join(",", _entryRows.Select(r => r.KeyLabel))}");
        return true;
    }

    /// <summary>从任意元素往上找列头。</summary>
    private static DataGridColumnHeader? FindHeader(object? source)
    {
        if (source is not DependencyObject src) return null;
        var header = src as DataGridColumnHeader;
        while (header == null)
        {
            src = VisualTreeHelper.GetParent(src);
            if (src == null) return null;
            header = src as DataGridColumnHeader;
        }
        return header;
    }

    /// <summary>
    /// 把 ▲/▼ 画到对应列头上。
    ///
    /// 不用 DataGrid 自带的排序箭头：`CanUserSortColumns=False` 之后它不画（模板里那枚
    /// 箭头跟着"能不能排序"的状态走），于是"点了排序但没有任何指示"——用户只能猜。
    /// 这里直接把箭头写进列头文字，一定看得见，也跟列头同一套字号/颜色。
    /// </summary>
    private void SetSortArrow(string path, ListSortDirection dir)
    {
        if (EntryList?.Columns == null) return;
        foreach (var col in EntryList.Columns)
        {
            string key = col.SortMemberPath ?? "";
            if (key.Length == 0) continue;

            if (!_headerLabels.TryGetValue(key, out string? label))
            {
                label = col.Header as string ?? "";
                _headerLabels[key] = label;
            }

            bool active = string.Equals(key, path, StringComparison.Ordinal);
            col.Header = active
                ? label + (dir == ListSortDirection.Ascending ? "  ▲" : "  ▼")
                : label;
            col.SortDirection = active ? dir : null;   // 读屏/自动化也能读到状态
        }
    }

    /// <summary>列头的原始文字（加箭头之前的那份，切语言重进页面会重建）。</summary>
    private readonly Dictionary<string, string> _headerLabels = new();

    /// <summary>排序现场（明文，最长 64KB）：用户报"点了不排序"时，这一行能看出点没点到、排没排。</summary>
    private static void AliasSortTrace(string line)
    {
        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clp-alias-sort.txt");
            try
            {
                if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 64 * 1024)
                    System.IO.File.Delete(path);
            }
            catch { }
            System.IO.File.AppendAllText(path,
                DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch { /* 诊断写不出去不影响排序 */ }
    }

    /// <summary>按某一列重排当前这一屏（不动数据文件里的顺序）。</summary>
    private void ApplyAliasSort(string column, ListSortDirection dir)
    {
        var list = new List<Row>(_entryRows);
        SortRowsByColumn(list, column, dir);
        BindRows(list, keepSelection: true);
    }

    /// <summary>当前排序列（没排就是空串）。</summary>
    private string ActiveSortColumn() => _aliasSortPath;

    /// <summary>当前排序方向（没排就是升序）。</summary>
    private ListSortDirection ActiveSortDirection() => _aliasSortDir;

    /// <summary>清掉列头箭头（只动显示）。</summary>
    private void ClearSortArrows()
    {
        if (EntryList?.Columns == null) return;
        foreach (var col in EntryList.Columns)
        {
            col.SortDirection = null;
            string key = col.SortMemberPath ?? "";
            if (key.Length > 0 && _headerLabels.TryGetValue(key, out string? label)) col.Header = label;
        }
    }

    /// <summary>彻底放弃列头排序，回到下拉框那套。</summary>
    private void ResetSortState()
    {
        _aliasSortPath = "";
        _aliasSortDir = ListSortDirection.Ascending;
        ClearSortArrows();
    }

    /// <summary>按某一列排（原地排 <paramref name="rows"/>）。列名 = SortMemberPath。</summary>
    private static void SortRowsByColumn(List<Row> rows, string column, ListSortDirection dir)
    {
        if (string.IsNullOrEmpty(column) || rows.Count == 0) return;

        Comparison<Row> cmp;
        if (string.Equals(column, "UsageLabel", StringComparison.Ordinal))
        {
            cmp = (a, b) => a.Snippet.UseCount.CompareTo(b.Snippet.UseCount);
        }
        else
        {
            Func<Row, string> text = column switch
            {
                "KeyLabel" => r => r.KeyLabel,
                "Preview" => r => r.Preview,
                "CommentLabel" => r => r.CommentLabel,
                "GroupLabel" => r => r.GroupLabel,
                _ => _ => "",
            };
            cmp = (a, b) => string.Compare(text(a), text(b), StringComparison.CurrentCultureIgnoreCase);
        }

        if (dir == ListSortDirection.Descending)
        {
            var forward = cmp;
            cmp = (a, b) => forward(b, a);
        }

        /* 值相同就按触发词定序：两次排序之间顺序不会乱跳。 */
        rows.Sort((a, b) =>
        {
            int byColumn = cmp(a, b);
            return byColumn != 0
                ? byColumn
                : string.Compare(a.Snippet.Trigger, b.Snippet.Trigger, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>换一份行、保住选中的那些（重设 ItemsSource 会把选中清掉）。</summary>
    private void BindRows(List<Row> rows, bool keepSelection)
    {
        var keep = keepSelection ? EntryList.SelectedItems.OfType<Row>().ToList() : null;

        _suppress = true;
        try
        {
            _entryRows = rows;
            EntryList.ItemsSource = rows;

            /* 换 ItemsSource 会把列头的 SortDirection 清掉 —— 箭头要重新画一次，
             * 否则"按着某列排"的状态在屏幕上消失，用户以为排序失效。 */
            if (_aliasSortPath.Length > 0) SetSortArrow(_aliasSortPath, _aliasSortDir);

            if (keep is { Count: > 0 })
            {
                foreach (var row in keep)
                {
                    if (rows.Contains(row)) EntryList.SelectedItems.Add(row);
                }
            }
        }
        finally { _suppress = false; }
    }

    /* ─────────────────── 列表列宽记忆 ───────────────────
       和 Qbar 设置页同一套：拖动表头上的把手 → 600ms 后落盘，
       下次进页面照原样摆回去。DataGrid 没有"拖完了"事件，
       唯一可靠的信号是把手自己的 DragCompleted（handledEventsToo=true 才收得到）。 */

    private System.Windows.Threading.DispatcherTimer? _widthSaveTimer;
    private bool _applyingColumnWidths;

    private void OnColumnGripperDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_host == null || _applyingColumnWidths) return;
        if (e.OriginalSource is not Thumb thumb) return;
        if (thumb.TemplatedParent is not DataGridColumnHeader) return;

        if (_widthSaveTimer == null)
        {
            _widthSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600),
            };
            _widthSaveTimer.Tick += (_, _) =>
            {
                _widthSaveTimer!.Stop();
                PersistColumnWidths();
            };
        }
        _widthSaveTimer.Stop();
        _widthSaveTimer.Start();
    }

    private void PersistColumnWidths()
    {
        if (_host == null || EntryList.Columns.Count == 0) return;

        var widths = new List<double>(EntryList.Columns.Count);
        foreach (var col in EntryList.Columns)
            widths.Add(col.Width.IsStar ? 0 : Math.Round(col.ActualWidth, 1));

        _host.Settings.Hotstring.ColumnWidths = widths;
        _host.SaveSettings();
    }

    /// <summary>把设置里记的列宽摆回去。0 = 保持默认（星号列继续自适应）。</summary>
    private void ApplyColumnWidths()
    {
        var saved = _host?.Settings.Hotstring.ColumnWidths;
        if (saved == null || saved.Count == 0 || EntryList.Columns.Count == 0) return;

        _applyingColumnWidths = true;
        try
        {
            for (int i = 0; i < saved.Count && i < EntryList.Columns.Count; i++)
            {
                double w = saved[i];
                if (w <= 0) continue;
                if (w < 40) w = 40;          // 设置文件被手改坏了也拖得回来
                EntryList.Columns[i].Width = new DataGridLength(w);
            }
        }
        finally { _applyingColumnWidths = false; }
    }

    /// <summary>列表顺序：默认按触发词；用户手动拖过之后按他自己排的 Order。</summary>
    private IEnumerable<Snippet> OrderEntries(IEnumerable<Snippet> source)
    {
        if (_customOrder)
            return source.OrderBy(x => x.Order).ThenBy(x => x.Trigger, StringComparer.OrdinalIgnoreCase);
        return source.OrderBy(x => x.Trigger, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 这一条在「选中的那一列」里是否命中关键词。
    ///
    /// 为什么必须按列分开：以前三个字段一起 <c>Contains</c>，用户想找
    /// "备注里写了 xxx"的条目，正文里恰好有同一个词的那些行也全留下来 ——
    /// 分类就等于白选。列标识与搜索框的分类下拉一一对应。
    ///
    /// 「关键字」列额外比对**剥掉前缀**后的样子：列表里显示的是 <c>mail</c>，
    /// 用户照着屏幕打 <c>mail</c> 必须命中，不能因为配置里存的是 <c>;mail</c> 就搜不到。
    /// </summary>
    internal static bool MatchesColumn(Snippet snippet, string column, string filter,
                                       IReadOnlyList<string>? prefixes)
    {
        if (snippet == null) return false;
        if (string.IsNullOrWhiteSpace(filter)) return true;

        if (column == "text")
            return snippet.Text?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false;

        if (column == "comment")
            return snippet.Comment?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false;

        string trigger = snippet.Trigger ?? "";
        if (trigger.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;

        string stripped = SnippetPrefixOps.StripAny(trigger, prefixes);
        return stripped.Length > 0
               && stripped.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /* 搜索框（统一控件）：打字、换分类、清空都走这里。
     * 换分类时控件会先发 CategoryChanged 再发一次 SearchChanged，
     * 所以只挂 SearchChanged 就够，不用两处各刷一遍。 */
    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        RefreshList();
    }

    /// <summary>搜索框里按回车：焦点交给列表，接着就能用方向键挑条目。</summary>
    private void OnFilterSubmitted(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (EntryList.SelectedItem == null && _entryRows.Count > 0)
            EntryList.SelectedItem = _entryRows[0];
        EntryList.Focus();
    }

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null) return;

        if (GroupList.SelectedItem is GroupRow row)
        {
            RenameGroupButton.IsEnabled = row.IsReal;
            DeleteGroupButton.IsEnabled = row.IsReal;
            /* 记住"用户最后真正停在哪一组"。「全部」不是组（Group=null），
             * 但「未分组」是（Group=""）：两者都要记住，新增时才有确定落点。 */
            _lastGroup = row.Group;
        }

        if (_suppress) return;
        RefreshList();
        SyncEditorToSelection();
    }

    /// <summary>页面级快捷键：Ctrl+F 搜索；Esc 收起编辑栏；列表有焦点时 Ctrl+A 全选。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterField.FocusInput();
            e.Handled = true;
            return;
        }

        /* Esc 关编辑栏：这是用户能"退出编辑"的第二条路（第一条是标题右边的叉）。
         * 只在编辑栏开着、且事件还没被别人吃掉时才管 —— 分组下拉、前缀弹层的
         * Esc 是它们自己的事，抢过来会让弹层关不掉。 */
        if (e.Key == Key.Escape && !e.Handled
            && EditorCard.Visibility == Visibility.Visible)
        {
            CloseEditor();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && EntryList.IsKeyboardFocusWithin)
        {
            EntryList.SelectAll();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;

        /* 选中变了 = 「删除 / 移到分组」的可用状态也要跟着变。
         * 以前这里只加载编辑区，按钮状态只在 Refresh()/换分组时算过 ——
         * 于是在某个分组里点中一条别名，删除按钮还是灰的，点了没反应。 */
        SyncEditorToSelection();
    }

    private void OnEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;

        /* 输入法正在组字：这时候的 Text 是"拼音半成品"，落盘和刷新都没有意义，
         * 而且会打断输入法的组合串（汉字永远提交不了）。等提交后再走一次。 */
        if (_composing) return;

        /* 编辑区可能指向一条"已经不在配置里"的条目：
         * 同步恢复覆盖了配置、或者这条在别处被删掉了。
         * 继续往里写只会改到一个游离对象，存进去也会在下一次刷新时消失 ——
         * 用户感受就是"改了没反应"。这里先核对一次，找不到就收起编辑区。 */
        if (!Entries.Any(x => ReferenceEquals(x, _editing)))
        {
            var again = Entries.FirstOrDefault(x =>
                string.Equals(x.Trigger, _editing.Trigger, StringComparison.OrdinalIgnoreCase));

            if (again == null)
            {
                _editing = null;
                ShowEditor(false);
                RefreshList();
                return;
            }

            LoadEditor(again);
        }

        _editing.Trigger = SnippetPrefixOps.ApplyAny(KeyBox.Text, _host.Settings.Hotstring.EffectivePrefixes);
        _editing.Text = ValueBox.Text ?? "";
        _editing.Comment = CommentBox.Text ?? "";
        _editing.Apps = AppsBox.Text?.Trim() ?? "";
        _editing.Group = GroupBox.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(_editing.Group))
            SnippetGroupOps.Add(_host.Settings, _editing.Group);   // 手打的新组名立刻登记，侧栏马上出现
        _editing.Enabled = EnabledBox.IsChecked == true;

        _host.SaveSettings();

        /* 只刷新列表，**不要**重建编辑区。
         * 以前这里调 Refresh()：它会重建分组下拉的选项、再把文本赋回输入框，
         * 于是用户在下拉框里打字打到一半就被清掉 —— 表现就是"分组改不了"。 */
        RefreshList();
        RefreshGroups();
        EditTitle.Text = Loc.F("正在编辑：{0}",
            SnippetPrefixOps.StripAny(_editing.Trigger, _host.Settings.Hotstring.EffectivePrefixes));

        /* 触发词撞车：两条同触发词时只有其中一条会命中，用户很难自己发现。 */
        bool duplicate = _editing.Trigger.Length > 0 && Entries.Any(x =>
            !ReferenceEquals(x, _editing) &&
            string.Equals((x.Trigger ?? "").Trim(), _editing.Trigger.Trim(), StringComparison.CurrentCultureIgnoreCase));

        if (duplicate)
        {
            StatusText.Text = Loc.T("⚠ 这个触发词和另一条别名重复，只有其中一条会生效");
            StatusText.Foreground = (Brush)FindResource("Brush.Warning");
        }
        else
        {
            StatusText.Text = Loc.T("已保存");
            StatusText.Foreground = (Brush)FindResource("Brush.TextTertiary");
        }
    }

    /// <summary>
    /// 底部的「移到分组」按钮。
    ///
    /// 右键菜单在部分环境里不好按出来（用户反馈过），所以给一个**显式按钮**，
    /// 走的是同一份分组列表，不会两处逻辑各写一遍。
    /// </summary>
    private void OnMoveClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var targets = SelectedSnippets();
        if (targets.Count == 0) return;

        var menu = new ContextMenu
        {
            PlacementTarget = MoveButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
        };

        FillMoveMenu(menu, targets);
        menu.IsOpen = true;
    }

    /// <summary>把"移到分组"的菜单项填进给定菜单（右键与底部按钮共用）。</summary>
    private void FillMoveMenu(ContextMenu menu, Snippet snippet)
        => FillMoveMenu(menu, new List<Snippet> { snippet });

    /// <summary>把"移到分组"的菜单项填进给定菜单；支持一次移动多条。</summary>
    private void FillMoveMenu(ContextMenu menu, List<Snippet> snippets)
    {
        menu.Items.Clear();
        if (snippets.Count == 0) return;

        bool allSameGroup = snippets
            .Select(x => (x.Group ?? "").Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Count() == 1;
        string current = allSameGroup ? (snippets[0].Group ?? "").Trim() : "\u0000";

        var move = new MenuItem { Header = snippets.Count > 1
            ? Loc.F("把这 {0} 条移到分组", snippets.Count)
            : Loc.T("移到分组") };

        foreach (string g in AllGroups())
        {
            string target = g;
            var mi = new MenuItem
            {
                Header = target,
                IsCheckable = true,
                IsChecked = string.Equals(current, target, StringComparison.CurrentCultureIgnoreCase),
            };
            mi.Click += (_, _) => MoveMany(snippets, target);
            move.Items.Add(mi);
        }

        if (move.Items.Count > 0) move.Items.Add(new Separator());

        var ungrouped = new MenuItem
        {
            Header = Loc.T("未分组"),
            IsCheckable = true,
            IsChecked = current.Length == 0,
        };
        ungrouped.Click += (_, _) => MoveMany(snippets, "");
        move.Items.Add(ungrouped);

        var create = new MenuItem { Header = Loc.T("新建分组…") };
        create.Click += (_, _) =>
        {
            string? name = AskNewGroup();
            if (name != null) MoveMany(snippets, name);
        };
        move.Items.Add(create);

        menu.Items.Add(move);
    }

    /* ---------------- 自定义顺序：列表内拖放重排 ---------------- */

    private Row? FindRowAt(Point point)
    {
        var hit = EntryList.InputHitTest(point) as DependencyObject;
        var item = ItemsControl.ContainerFromElement(EntryList, hit) as DataGridRow;
        return item?.DataContext as Row;
    }

    private void OnEntryListDragOver(object sender, DragEventArgs e)
    {
        /* 拖进列表 = 重排。以前这条只在「自定义顺序」下生效（靠下拉框切），
         * 下拉框删掉之后就不能再让它只在某个模式下活着 —— 否则拖动会被
         * 判成 None，用户看到的是"拖了半天没反应"。 */
        if (!e.Data.GetDataPresent("clp/snippet"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;

        /* 指示线画在"将要插入的位置"：落在行上半 → 行上沿，下半 → 行下沿；
         * 空白处 → 列表末尾。 */
        _insertLine ??= UI.InsertLineAdorner.Show(EntryList);

        if (_insertLine != null)
        {
            var target = FindRowAt(e.GetPosition(EntryList));
            double y;

            if (target == null)
            {
                y = EntryList.ActualHeight;
            }
            else if (EntryList.ItemContainerGenerator.ContainerFromItem(target) is DataGridRow container)
            {
                var top = container.TranslatePoint(new Point(0, 0), EntryList).Y;
                double half = container.ActualHeight / 2;
                y = e.GetPosition(EntryList).Y > top + half
                    ? top + container.ActualHeight
                    : top;
            }
            else
            {
                y = EntryList.ActualHeight;
            }

            _insertLine.MoveTo(y);
        }

        e.Handled = true;
    }

    private void OnEntryListDrop(object sender, DragEventArgs e)
    {
        RemoveInsertLine();

        if (_host == null) return;
        if (e.Data.GetData("clp/snippet") is not Snippet dragged) return;

        var target = FindRowAt(e.GetPosition(EntryList));

        /* 从这一下开始，列表顺序就是用户自己排的了。列头排序会把显示顺序
         * 覆盖掉，所以先清掉列头状态，刷新后才看得见新的次序。 */
        _customOrder = true;
        ResetSortState();

        /* 以"当前显示顺序"为基准重排，再按顺序重新编号。 */
        var ordered = OrderEntries(_host.Settings.Global.Snippets).ToList();
        ordered.Remove(dragged);

        int index = target == null ? ordered.Count : ordered.IndexOf(target.Snippet);
        if (index < 0) index = ordered.Count;

        /* 落在目标行的下半部分 → 插到它后面。 */
        if (target != null &&
            EntryList.ItemContainerGenerator.ContainerFromItem(target) is DataGridRow container &&
            e.GetPosition(container).Y > container.ActualHeight / 2)
        {
            index++;
        }

        ordered.Insert(Math.Clamp(index, 0, ordered.Count), dragged);

        for (int i = 0; i < ordered.Count; i++) ordered[i].Order = i;

        _host.SaveSettings();
        Refresh();
        e.Handled = true;
    }

    /// <summary>移除插入位置指示线（拖拽结束、离开列表时调）。</summary>
    private void RemoveInsertLine()
    {
        _insertLine?.Remove();
        _insertLine = null;
    }

    /// <summary>把一批别名移到同一个分组。</summary>
    private void MoveMany(List<Snippet> snippets, string group)
    {
        if (_host == null || snippets.Count == 0) return;

        if (snippets.Count == 1)
        {
            MoveTo(snippets[0], group);
            return;
        }

        foreach (var snippet in snippets) SnippetGroupOps.Move(_host.Settings, snippet, group);

        _host.SaveSettings();
        Refresh();

        _host.Notify(string.IsNullOrWhiteSpace(group)
            ? Loc.F("已把 {0} 条别名移到「未分组」", snippets.Count)
            : Loc.F("已把 {0} 条别名移到「{1}」", snippets.Count, group));
    }


    /// <summary>
    /// 列表「别名正文」那一列：显示**替换内容**（换行/连续空白压成单空格，最多 56 字）。
    ///
    /// 以前这一列返回的是备注，"正文"里全是备注文字；现在备注有自己的列。
    /// </summary>
    private static string BuildPreview(Snippet snippet)
    {
        /* 正文列：显示**替换内容**（以前这里返回的是备注，用户看到的就是
         * "别名正文"列里全是备注）。换行/连续空白压成单空格，最多 56 字。 */
        string text = (snippet.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text.Length > 0 ? Shorten(text, 56) : Loc.T("（空正文）");
    }

    /// <summary>备注列：没备注就留空（不要用"（无备注）"占位，整列会显得很吵）。</summary>
    private static string BuildCommentLabel(Snippet snippet)
    {
        string comment = (snippet.Comment ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return comment.Length > 0 ? Shorten(comment, 48) : "";
    }

    /* ---------------- 只在指定程序里展开：两种选择方式 ---------------- */

    /// <summary>方式一：从文件管理器里挑 exe（可多选）。</summary>
    private void OnPickAppFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择要限定的程序"),
            Filter = Loc.T("程序") + " (*.exe)|*.exe|" + Loc.T("所有文件") + " (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        /* 只取文件名：进程名匹配用的是 exe 名字，不是完整路径。 */
        AppendApps(dialog.FileNames.Select(System.IO.Path.GetFileName).Where(n => !string.IsNullOrEmpty(n))!);
    }

    /// <summary>方式二：十字拾取 —— 在那个程序的窗口上点一下。</summary>
    private void OnPickAppWindow(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.WindowPickerWindow { Owner = Window.GetWindow(this) };
            picker.ShowDialog();

            if (picker.PickedWindow == IntPtr.Zero) return;

            var info = Core.Windows.WindowService.Describe(picker.PickedWindow);
            string exe = (info?.ExeName ?? "").Trim();
            if (exe.Length == 0)
            {
                _host?.Notify(Loc.T("没认出这个窗口属于哪个程序"));
                return;
            }

            AppendApps(new[] { exe });
        }
        catch (Exception ex)
        {
            CoreLog.Error("十字拾取程序失败", ex);
            _host?.Notify(Loc.T("拾取失败：") + ex.Message);
        }
    }

    /// <summary>把程序名追加进输入框（去重、统一用逗号分隔）。</summary>
    private void AppendApps(IEnumerable<string> names)
    {
        var list = (AppsBox.Text ?? "")
            .Split(new[] { ',', '，', ';', '；', '、' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        foreach (string name in names)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) continue;
            if (list.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(n);
        }

        AppsBox.Text = string.Join(", ", list);
        _host?.Notify(Loc.T("已加入限定程序"));
    }

    /// <summary>把文本放进剪贴板（失败给提示，不抛）。</summary>
    private void CopyToClipboard(string? text, string doneMessage)
    {
        string value = text ?? "";
        if (value.Length == 0)
        {
            _host?.Notify(Loc.T("这条别名的内容为空"));
            return;
        }

        try
        {
            ClipboardAccess.SetText(value);
            _host?.Notify(doneMessage);
        }
        catch (Exception ex)
        {
            CoreLog.Error("复制到剪贴板失败", ex);
            _host?.Notify(Loc.T("复制失败：") + ex.Message);
        }
    }

    /// <summary>复制一条别名（触发词自动加序号，避免撞车）。</summary>
    private void DuplicateSnippet(Snippet source)
    {
        if (_host == null) return;

        var prefixes = _host.Settings.Hotstring.EffectivePrefixes;
        string baseName = SnippetPrefixOps.StripAny(source.Trigger, prefixes);
        if (baseName.Length == 0) baseName = Loc.T("副本");

        string candidate = baseName + "2";
        int n = 2;
        while (Entries.Any(x => string.Equals(SnippetPrefixOps.StripAny(x.Trigger, prefixes), candidate,
                                               StringComparison.CurrentCultureIgnoreCase)))
        {
            n++;
            candidate = baseName + n;
        }

        var copy = new Snippet
        {
            Trigger = SnippetPrefixOps.ApplyAny(candidate, prefixes),
            Text = source.Text,
            Comment = source.Comment,
            Group = source.Group,
            Apps = source.Apps,
            Enabled = source.Enabled,
        };

        Entries.Add(copy);
        _host.SaveSettings();
        _editing = copy;
        Refresh();
        LoadEditor(copy);
        KeyBox.Focus();
        KeyBox.CaretIndex = KeyBox.Text.Length;
        _host.Notify(Loc.F("已复制为「{0}」", candidate));
    }

    /// <summary>导出"当前分组"的别名（全部视图 = 导出全部）。</summary>
    private void OnExportGroupClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        string? group = CurrentGroup;
        var list = group == null
            ? _host.Settings.Global.Snippets.ToList()
            : _host.Settings.Global.Snippets
                .Where(x => string.Equals((x.Group ?? "").Trim(), group, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        if (list.Count == 0)
        {
            _host.Notify(Loc.T("这个分组里还没有别名"));
            return;
        }

        string name = group == null
            ? "input-plus-aliases.json"
            : $"input-plus-aliases-{SanitizeFileName(group.Length == 0 ? Loc.T("未分组") : group)}.json";

        ExportToFile(list, name);
    }

    /// <summary>把文件名里 Windows 不接受的字符换掉。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string cleaned = new string(chars).Trim();
        return cleaned.Length == 0 ? "aliases" : cleaned;
    }

    /// <summary>导出指定别名到文件（两个导出入口共用）。</summary>
    private void ExportToFile(List<Snippet> snippets, string suggestedName)
    {
        if (_host == null || snippets.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("导出别名"),
            Filter = Loc.T("别名文件 (*.json)|*.json"),
            FileName = suggestedName,
            DefaultExt = ".json",
        };

        if (dlg.ShowDialog(Owner) != true) return;

        try
        {
            System.IO.File.WriteAllText(dlg.FileName,
                SnippetPortable.Export(snippets),
                new System.Text.UTF8Encoding(false));

            _host.Notify(Loc.F("已导出 {0} 条别名", snippets.Count));
        }
        catch (Exception ex)
        {
            CoreLog.Error("导出别名失败", ex);
            _host.Notify(Loc.T("导出失败：") + ex.Message);
        }
    }

    /// <summary>别名体检：重复触发词 / 空内容 / 前缀不对 / 分组未登记。</summary>
    /// <summary>
    /// 「找回丢失的别名」：扫历史副本，把当前配置里没有的条目列出来让用户勾。
    ///
    /// 这个功能存在的理由见 <see cref="SnippetRecovery"/> 的注释：丢条目时界面
    /// 不会有任何痕迹，只有磁盘上的旧副本还留着证据。
    /// </summary>
    private void OnRecoverClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var result = SnippetRecovery.Scan(_host.Settings);
        if (result.Candidates.Count == 0)
        {
            string detail = result.FilesScanned == 0
                ? Loc.T("还没有可用的历史副本：从下一次启动开始，程序每天会留一份设置快照。")
                : Loc.F("看过了 {0} 份历史副本，没有发现当前缺失的别名。", result.FilesScanned);
            UI.Windows.AppDialog.Success(Owner, detail, Loc.T("找回丢失的别名"));
            return;
        }

        var picks = UI.Windows.SnippetRecoverWindow.Ask(Owner, result);
        if (picks == null || picks.Count == 0) return;

        int added = SnippetRecovery.Restore(_host.Settings, picks);
        _host.SaveSettings();
        Refresh();
        _host.Notify(Loc.F("已恢复 {0} 条别名", added));
    }

    private void OnAuditClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var issues = SnippetAudit.Run(_host.Settings);

        /* 没用过的条目不算"问题"，但对清理很有用 —— 附一句，告诉用户去哪儿看。
         * 「使用」列是空的那几条就是没用过的；以前这里还指路"勾上右上角从未使用"，
         * 那个勾选框已按用户要求删掉，提示语必须跟着改，否则等于指向一个不存在的按钮。 */
        int unused = _host.Settings.Global.Snippets.Count(x => x.UseCount <= 0);
        string unusedHint = unused > 0
            ? "\n\n" + Loc.F("另有 {0} 条别名还没用过（列表「使用」列是空的）。", unused)
            : "";

        if (issues.Count == 0)
        {
            UI.Windows.AppDialog.Success(Owner,
                Loc.T("没有发现问题：触发词不重复、内容都不为空、分组也都登记过了。") + unusedHint,
                Loc.T("别名体检"));
            return;
        }

        UI.Windows.AppDialog.Warn(Owner,
            Loc.F("发现 {0} 个问题：", issues.Count) + "\n\n" + SnippetAudit.Describe(issues) + unusedHint,
            Loc.T("别名体检"));

        /* 能自动修的那几类，问一句就修 —— 否则用户看完列表还得自己一条条改。 */
        int fixable = issues.Count(x => x.Title is "触发词重复" or "前缀不对" or "分组未登记");
        if (fixable == 0) return;

        if (!UI.Windows.AppDialog.Confirm(Owner,
                Loc.F("其中 {0} 个可以自动修（前缀换成当前的、分组补登记、重复的加序号）。要修吗？", fixable),
                Loc.T("自动修复"),
                Windows.DialogKind.Question,
                okText: Loc.T("修复")))
        {
            return;
        }

        var fixedResult = SnippetAudit.Fix(_host.Settings);
        _host.SaveSettings();
        Refresh();

        _host.Notify(Loc.F("已修复 {0} 项：前缀 {1}、分组 {2}、触发词 {3}",
            fixedResult.Total, fixedResult.PrefixFixed, fixedResult.GroupsRegistered, fixedResult.TriggersRenamed));
    }

    /// <summary>导出全部别名到一个 JSON 文件。</summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        ExportToFile(_host.Settings.Global.Snippets.ToList(), "input-plus-aliases.json");
    }

    /// <summary>从 JSON 文件导入别名（合并，不删除本地已有条目）。</summary>
    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("导入别名"),
            Filter = Loc.T("别名文件 (*.json)|*.json|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(Owner) != true) return;

        List<SnippetPortable.Entry>? parsed;
        try
        {
            parsed = SnippetPortable.Parse(System.IO.File.ReadAllText(dlg.FileName));
        }
        catch (Exception ex)
        {
            CoreLog.Error("读取别名文件失败", ex);
            _host.Notify(Loc.T("读取失败：") + ex.Message);
            return;
        }

        if (parsed == null)
        {
            _host.Notify(Loc.T("这个文件不是有效的别名文件（需要 JSON）"));
            return;
        }

        /* 有现存数据时才问"合并还是替换"；空表直接导入，少一次打扰。 */
        bool replace = false;
        int existing = _host.Settings.Global.Snippets.Count;
        if (existing > 0)
        {
            bool confirmed = UI.Windows.AppDialog.Confirm(Owner,
                Loc.F("导入会和现有 {0} 条别名合并。\n\n要改成「替换全部」吗？替换会先清空现有别名，且不可撤销。", existing),
                Loc.T("导入别名"),
                Windows.DialogKind.Question,
                okText: Loc.T("替换全部"),
                cancelText: Loc.T("合并导入"));

            replace = confirmed;
        }

        var result = SnippetPortable.Import(parsed, _host.Settings.Global.Snippets, replace);
        _host.SaveSettings();
        _editing = null;
        Refresh();

        _host.Notify(replace
            ? Loc.F("已替换为 {0} 条别名", result.Added)
            : Loc.F("导入完成：新增 {0} 条，更新 {1} 条", result.Added, result.Updated));
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        /* 落点：当前视图停在哪一组就建到哪一组（「全部」= 未分组）。
         * 选中项在刷新过程中可能是 null，这时退回"最后停过的那一组"。 */
        string targetGroup = TargetGroupForNewEntry();

        var entry = new Snippet
        {
            Trigger = "",       // 前缀等用户输入完触发词再补（ApplyAny 会用默认前缀）
            Text = "",
            Comment = Loc.T("新别名"),
            Group = targetGroup,
            Enabled = true,
        };

        if (!string.IsNullOrWhiteSpace(entry.Group)) SnippetGroupOps.Add(_host.Settings, entry.Group);

        Entries.Add(entry);
        _host.SaveSettings();
        CoreLog.Info($"[别名] 新增：分组「{(entry.Group.Length == 0 ? "未分组" : entry.Group)}」"
                     + $"（当前视图 {DescribeGroup(CurrentGroup)}）");

        _editing = entry;
        /* 建完要让用户看见它落在哪：视图切到它所在的分组。 */
        _selectGroupAfterRefresh = entry.Group;
        Refresh();

        /* ⚠ Refresh() 里的 SyncEditorToSelection 只在"选中的不是当前编辑项"时才 LoadEditor，
         * 而这里 _editing 已经等于新条目 —— 于是编辑区永远不显示，
         * 用户点了「新增」只看列表多一行、右边空空。必须显式打开一次。 */
        LoadEditor(entry);
        if (EntryList.SelectedItem is Row added) EntryList.ScrollIntoView(added);

        KeyBox.Focus();
        KeyBox.CaretIndex = KeyBox.Text.Length;
    }

    /// <summary>
    /// 新别名该落在哪个分组：当前选中项优先；没有选中项（刷新瞬间）退回最后停过的那一组。
    /// 「全部」= 未分组（""），不是"上一次的组"。
    /// </summary>
    private string TargetGroupForNewEntry()
        => ResolveNewGroup(GroupList.SelectedItem is GroupRow, (GroupList.SelectedItem as GroupRow)?.Group, _lastGroup);

    /// <summary>
    /// 纯函数：新别名的落点。选中项存在时以它为准（「全部」→ ""），
    /// 选中项缺失（分组列表重建的瞬间）才退回"最后停过的那一组"。
    /// </summary>
    internal static string ResolveNewGroup(bool hasSelection, string? selectedGroup, string? lastGroup)
        => hasSelection ? (selectedGroup ?? "") : (lastGroup ?? "");

    /// <summary>选中条数决定「删除 / 移到分组」是否可用（纯函数，自检覆盖）。</summary>
    internal static bool CanActOnSelection(int selectedCount) => selectedCount > 0;

    /// <summary>
    /// 纯函数：刷新分组列表时要保持哪一组。
    ///
    /// 「全部」用 null 表示（见 <c>GroupRow.Group</c>）。<paramref name="focusAll"/> 为真
    /// （刚加完别名、要选中那一条）时**强制回到「全部」** —— 否则用户停在「工作」组
    /// 时新加的未分组别名根本不在列表里，选中为 null、编辑器收起，
    /// 表现就是"点完什么都没发生"。其余情况沿用原逻辑。
    /// </summary>
    internal static string? ResolveKeepGroup(bool focusAll, string? selectAfterRefresh, string? currentGroup)
        => focusAll ? null : (selectAfterRefresh ?? currentGroup);

    private static string DescribeGroup(string? group)
        => group == null ? "全部" : group.Length == 0 ? "未分组" : group;

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var targets = SelectedSnippets();
        if (targets.Count == 0) return;

        if (targets.Count == 1)
        {
            DeleteSnippet(targets[0]);
            return;
        }

        /* 多条一起删：必须确认。别名是用户攒出来的东西，误删很难恢复。 */
        if (!UI.Windows.AppDialog.Confirm(Owner,
                Loc.F("删除选中的 {0} 条别名？\n\n删除后不可撤销。", targets.Count),
                Loc.T("删除别名"), Windows.DialogKind.Warn, okText: Loc.T("删除")))
        {
            return;
        }

        foreach (var snippet in targets) Entries.Remove(snippet);
        _editing = null;
        _host.SaveSettings();
        Refresh();
        _host.Notify(Loc.F("已删除 {0} 条别名", targets.Count));
    }

    private void DeleteSnippet(Snippet snippet)
    {
        if (_host == null) return;

        Entries.Remove(snippet);
        _host.SaveSettings();
        if (ReferenceEquals(_editing, snippet)) _editing = null;
        Refresh();
    }

    /// <summary>列表里别名的右键菜单：移到分组 / 删除（Shift+F10 同样可用）。</summary>
    private void OnEntryContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_host == null
            || e.Source is not FrameworkElement source
            || source.ContextMenu is not ContextMenu menu)
        {
                e.Handled = true;
            return;
        }

        Row? row = null;

        row = source.DataContext as Row;

        if (row == null)
        {
            e.Handled = true;
            return;
        }

        EntryList.SelectedItem = row;

        FillMoveMenu(menu, row.Snippet);

        string name = SnippetPrefixOps.StripAny(row.Snippet.Trigger, _host.Settings.Hotstring.EffectivePrefixes);

        var copyText = new MenuItem { Header = Loc.T("复制内容") };
        copyText.Click += (_, _) => CopyToClipboard(row.Snippet.Text, Loc.T("内容已复制"));
        menu.Items.Add(copyText);

        var copyTrigger = new MenuItem { Header = Loc.T("复制触发词") };
        copyTrigger.Click += (_, _) => CopyToClipboard(name, Loc.T("触发词已复制"));
        menu.Items.Add(copyTrigger);

        menu.Items.Add(new Separator());

        var dup = new MenuItem { Header = Loc.T("复制一个副本") };
        dup.Click += (_, _) => DuplicateSnippet(row.Snippet);
        menu.Items.Add(dup);

        menu.Items.Add(new Separator());

        var del = new MenuItem { Header = Loc.T("删除这个别名") };
        del.Click += (_, _) => DeleteSnippet(row.Snippet);
        menu.Items.Add(del);

    }
}
