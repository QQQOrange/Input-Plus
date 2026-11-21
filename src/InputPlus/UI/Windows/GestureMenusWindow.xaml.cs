using System.Windows;
using System.Windows.Controls;
using InputPlus.Core.Actions;
using InputPlus.Core.Scripting;

namespace InputPlus.UI.Windows;

/// <summary>
/// 手势菜单管理器（v1.5.0）。
///
/// 左边挑一个菜单，中间改菜单本身（名字 / 说明 / 条目的增删排序），右边改选中的那一条条目。
/// 所有编辑都直接改 AppSettings.GestureMenus 里的对象 —— 输入即所见，没有"应用"按钮；
/// 只有真的动过东西，关闭时才调用一次 SaveSettings()。
/// </summary>
public partial class GestureMenusWindow : Window
{
    private readonly AppHost _host;

    /// <summary>可放进菜单的动作：藏起来的、没 ID 的、重复 ID 的都不要。</summary>
    private readonly List<ActionInfo> _actions;

    private GestureMenuDef? _menu;
    private GestureMenuItem? _item;

    /// <summary>联动刷新时的重入闸门：填控件的动作不该被当成用户在改东西。</summary>
    private bool _loading;

    private bool _dirty;

    public GestureMenusWindow(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        _actions = host.Actions.Items
            .Where(a => !a.Hidden && !string.IsNullOrEmpty(a.Id))
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.DisplayCategory, StringComparer.CurrentCulture)
            .ThenBy(a => a.Title, StringComparer.CurrentCulture)
            .ToList();

        InitializeComponent();

        Load();
        // 同上：窗体底色是 Brush.Background，标题栏别用 SurfaceAlt，不然顶上贴一条。
        Theme.WindowChrome.AttachTo(this, "Brush.Background");
    }

    // ============================ 装载 / 联动 ============================

    private void Load()
    {
        BuildCategories();
        RefreshMenus(0);
    }

    private void BuildCategories()
    {
        // _actions 已按分类显示名排好序，GroupBy 保留首次出现的顺序，所以分类也是有序的
        List<CategoryChoice> categories = _actions
            .GroupBy(a => a.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CategoryChoice(g.Key))
            .ToList();

        CategoryBox.ItemsSource = categories;
        CategoryBox.SelectedIndex = categories.Count > 0 ? 0 : -1;
    }

    /// <summary>重建左侧菜单列表，并选中第 select 个（越界自动夹取）。</summary>
    private void RefreshMenus(int select)
    {
        List<GestureMenuDef> menus = _host.Settings.GestureMenus;
        bool empty = menus.Count == 0;

        _loading = true;
        try
        {
            MenuList.ItemsSource = menus.Select(m => new GestureMenuListItem(m)).ToList();
            MenuList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            MenuEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            MenuList.SelectedIndex = empty ? -1 : Math.Clamp(select, 0, menus.Count - 1);
        }
        finally
        {
            _loading = false;
        }

        SyncFromSelection();
    }

    /// <summary>把左侧选中项带到中间 / 右边两栏。</summary>
    private void SyncFromSelection()
    {
        _menu = (MenuList.SelectedItem as GestureMenuListItem)?.Menu;
        bool has = _menu != null;

        EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        MenuCard.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        ItemCard.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        DuplicateMenuButton.IsEnabled = has;
        DeleteMenuButton.IsEnabled = has;

        _loading = true;
        try
        {
            NameBox.IsEnabled = has;
            HintBox.IsEnabled = has;
            NameBox.Text = has ? _menu!.Name : "";
            HintBox.Text = has ? _menu!.Hint : "";
        }
        finally
        {
            _loading = false;
        }

        RebuildItemList(has ? 0 : -1);
        SyncItemEditor();
    }

    /// <summary>重建中间条目列表，并选中第 select 条。</summary>
    private void RebuildItemList(int select)
    {
        List<GestureMenuEntryItem> rows = _menu == null
            ? new List<GestureMenuEntryItem>()
            : _menu.Items.Select(i => new GestureMenuEntryItem(i, ActionTitleOf)).ToList();
        bool empty = rows.Count == 0;

        _loading = true;
        try
        {
            ItemList.ItemsSource = rows;
            ItemList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            ItemEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            ItemList.SelectedIndex = empty ? -1 : Math.Clamp(select, 0, rows.Count - 1);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>把选中的条目带到右边编辑区。</summary>
    private void SyncItemEditor()
    {
        _item = (ItemList.SelectedItem as GestureMenuEntryItem)?.Item;
        bool has = _item != null;

        ItemEditorPanel.IsEnabled = has;
        ItemEditorPanel.Opacity = has ? 1.0 : 0.55;
        ItemCardHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

        _loading = true;
        try
        {
            TitleBox.Text = has ? _item!.Title : "";
            GlyphBox.Text = has ? _item!.Glyph : "";
            ArgBox.Text = has ? _item!.Arg : "";
        }
        finally
        {
            _loading = false;
        }

        SelectCategoryAndAction(has ? _item!.Action : "");
        UpdateActionHint();
        UpdateItemButtons();
    }

    private void RefreshMenuRows()
    {
        int index = MenuList.SelectedIndex;
        _loading = true;
        try
        {
            MenuList.Items.Refresh();
            MenuList.SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshItemRows()
    {
        int index = ItemList.SelectedIndex;
        _loading = true;
        try
        {
            ItemList.Items.Refresh();
            ItemList.SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateItemButtons()
    {
        int count = _menu?.Items.Count ?? 0;
        int index = ItemList.SelectedIndex;

        AddButton.IsEnabled = _menu != null;
        RemoveButton.IsEnabled = _item != null;
        UpButton.IsEnabled = _item != null && index > 0;
        DownButton.IsEnabled = _item != null && index >= 0 && index < count - 1;
    }

    // ============================ 动作选择 / 提示 ============================

    private void SelectCategoryAndAction(string actionId)
    {
        ActionInfo? info = string.IsNullOrWhiteSpace(actionId) ? null : _host.Actions.Get(actionId);
        string category = info != null
            ? info.Category
            : (_item == null ? FirstCategory() : SelectedCategory());
        CategoryChoice? choice = FindCategoryChoice(category);

        _loading = true;
        try
        {
            CategoryBox.SelectedItem = choice;
            FillActions(choice?.Category ?? "");
            ActionBox.SelectedItem = choice == null ? null : info;
            if (ActionBox.SelectedItem == null)
            {
                ActionBox.SelectedIndex = -1;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void FillActions(string category)
    {
        ActionBox.ItemsSource = string.IsNullOrEmpty(category)
            ? new List<ActionInfo>()
            : _actions.Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private CategoryChoice? FindCategoryChoice(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return null;
        }

        return (CategoryBox.ItemsSource as IEnumerable<CategoryChoice>)?
            .FirstOrDefault(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase));
    }

    private string SelectedCategory() => (CategoryBox.SelectedItem as CategoryChoice)?.Category ?? "";

    private string FirstCategory() => (CategoryBox.ItemsSource as IEnumerable<CategoryChoice>)?
        .Select(c => c.Category).FirstOrDefault() ?? "";

    private void UpdateActionHint()
    {
        if (_item == null)
        {
            ActionHintText.Text = "";
            return;
        }

        ActionInfo? info = string.IsNullOrWhiteSpace(_item.Action) ? null : _host.Actions.Get(_item.Action);
        if (info == null)
        {
            ActionHintText.Text = Loc.T("还没有选动作 —— 这条点了不会执行任何东西。");
            return;
        }

        ActionHintText.Text = info.NeedsArg && !string.IsNullOrWhiteSpace(info.ArgHint)
            ? Loc.F("{0} · 参数：{1}", info.Description, info.ArgHint)
            : info.Description;
    }

    private string? ActionTitleOf(string actionId)
        => string.IsNullOrWhiteSpace(actionId) ? null : _host.Actions.Get(actionId)?.Title;

    // ============================ 左侧：菜单 ============================

    private void OnMenuSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SyncFromSelection();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _menu == null)
        {
            return;
        }

        _menu.Name = NameBox.Text;
        MarkDirty();
        RefreshMenuRows();
    }

    private void OnHintChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _menu == null)
        {
            return;
        }

        _menu.Hint = HintBox.Text;
        MarkDirty();
    }

    private void OnNewMenuClick(object sender, RoutedEventArgs e)
    {
        GestureMenuDef menu = new GestureMenuDef { Name = NextMenuName() };
        _host.Settings.GestureMenus.Add(menu);
        MarkDirty();
        RefreshMenus(_host.Settings.GestureMenus.Count - 1);
        ScrollMenuIntoView();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnDuplicateMenuClick(object sender, RoutedEventArgs e)
    {
        if (_menu == null)
        {
            return;
        }

        List<GestureMenuDef> menus = _host.Settings.GestureMenus;
        int index = menus.IndexOf(_menu);
        GestureMenuDef copy = _menu.Clone();
        // Clone() 会把 Id 一起拷过来，复制品必须换一个新 Id，否则两个菜单撞 ID
        copy.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        copy.Name = Loc.F("{0} 副本", _menu.DisplayName);

        int insert = index < 0 ? menus.Count : index + 1;
        menus.Insert(insert, copy);
        MarkDirty();
        RefreshMenus(insert);
        ScrollMenuIntoView();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if (_menu == null)
        {
            return;
        }

        List<GestureMenuDef> menus = _host.Settings.GestureMenus;
        int index = menus.IndexOf(_menu);
        if (index < 0)
        {
            return;
        }

        if (!AppDialog.ConfirmDanger(this,
                Loc.F("删除菜单「{0}」？", _menu.DisplayName),
                Loc.T("删除菜单")))
        {
            return;
        }

        menus.RemoveAt(index);
        MarkDirty();
        RefreshMenus(index);
    }

    private string NextMenuName()
    {
        List<GestureMenuDef> menus = _host.Settings.GestureMenus;
        string baseName = Loc.T("新菜单");
        if (!HasMenuNamed(menus, baseName))
        {
            return baseName;
        }

        for (int n = 2; n < 1000; n++)
        {
            string candidate = Loc.F("{0} {1}", baseName, n);
            if (!HasMenuNamed(menus, candidate))
            {
                return candidate;
            }
        }

        return baseName;
    }

    private static bool HasMenuNamed(List<GestureMenuDef> menus, string name)
        => menus.Any(m => string.Equals(m.DisplayName, name, StringComparison.Ordinal));

    private void ScrollMenuIntoView()
    {
        if (MenuList.SelectedItem != null)
        {
            MenuList.ScrollIntoView(MenuList.SelectedItem);
        }
    }

    // ============================ 中间：条目 ============================

    private void OnItemSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SyncItemEditor();
    }

    private void OnAddItemClick(object sender, RoutedEventArgs e)
    {
        if (_menu == null)
        {
            return;
        }

        if (_menu.Items.Count >= GestureMenuDef.MaxItems)
        {
            AppDialog.Warn(this,
                Loc.F("一个菜单最多 {0} 条。", GestureMenuDef.MaxItems),
                Loc.T("手势菜单"));
            return;
        }

        _menu.Items.Add(new GestureMenuItem());
        MarkDirty();
        RebuildItemList(_menu.Items.Count - 1);
        SyncItemEditor();
        ScrollItemIntoView();
        TitleBox.Focus();
    }

    private void OnRemoveItemClick(object sender, RoutedEventArgs e)
    {
        if (_menu == null || _item == null)
        {
            return;
        }

        int index = ItemList.SelectedIndex;
        if (index < 0 || index >= _menu.Items.Count)
        {
            return;
        }

        _menu.Items.RemoveAt(index);
        MarkDirty();
        RebuildItemList(index);
        SyncItemEditor();
    }

    private void OnMoveItemUpClick(object sender, RoutedEventArgs e) => MoveItem(-1);

    private void OnMoveItemDownClick(object sender, RoutedEventArgs e) => MoveItem(1);

    private void MoveItem(int delta)
    {
        if (_menu == null || _item == null)
        {
            return;
        }

        int index = ItemList.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _menu.Items.Count)
        {
            return;
        }

        GestureMenuItem moving = _menu.Items[index];
        _menu.Items.RemoveAt(index);
        _menu.Items.Insert(target, moving);
        MarkDirty();
        RebuildItemList(target);
        SyncItemEditor();
        ScrollItemIntoView();
    }

    private void ScrollItemIntoView()
    {
        if (ItemList.SelectedItem != null)
        {
            ItemList.ScrollIntoView(ItemList.SelectedItem);
        }
    }

    // ============================ 右侧：条目编辑 ============================

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _item == null)
        {
            return;
        }

        _item.Title = TitleBox.Text;
        MarkDirty();
        RefreshItemRows();
    }

    private void OnGlyphChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _item == null)
        {
            return;
        }

        _item.Glyph = GlyphBox.Text;
        MarkDirty();
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        string category = SelectedCategory();
        _loading = true;
        try
        {
            FillActions(category);
        }
        finally
        {
            _loading = false;
        }

        if (_item == null)
        {
            return;
        }

        // 已经选好的动作如果就在这个分类里，保持不动；换到别的分类就得重新选
        ActionInfo? current = string.IsNullOrWhiteSpace(_item.Action) ? null : _host.Actions.Get(_item.Action);
        if (current != null && string.Equals(current.Category, category, StringComparison.OrdinalIgnoreCase))
        {
            _loading = true;
            try
            {
                ActionBox.SelectedItem = current;
            }
            finally
            {
                _loading = false;
            }

            UpdateActionHint();
            return;
        }

        _item.Action = "";
        MarkDirty();
        RefreshItemRows();
        UpdateActionHint();
    }

    private void OnActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _item == null)
        {
            return;
        }

        string id = (ActionBox.SelectedItem as ActionInfo)?.Id ?? "";
        if (string.Equals(_item.Action, id, StringComparison.Ordinal))
        {
            UpdateActionHint();
            return;
        }

        _item.Action = id;
        MarkDirty();
        RefreshItemRows();
        UpdateActionHint();
    }

    private void OnArgChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _item == null)
        {
            return;
        }

        _item.Arg = ArgBox.Text;
        MarkDirty();
        RefreshItemRows();
    }

    // ============================ 收尾 ============================

    internal void SelectFirstForRender()
    {
        _loading = true;
        try
        {
            if (MenuList.Items.Count > 0)
            {
                MenuList.SelectedIndex = 0;
            }
        }
        finally
        {
            _loading = false;
        }

        SyncFromSelection();

        if (ItemList.Items.Count > 0)
        {
            ItemList.SelectedIndex = 0;
            SyncItemEditor();
        }
    }

    private void MarkDirty() => _dirty = true;

    protected override void OnClosed(EventArgs e)
    {
        // 只改内存、关窗落盘：没动过就别写一次盘
        if (_dirty)
        {
            _host.SaveSettings();
        }

        base.OnClosed(e);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>分类下拉项的包装：显示本地化名，取回时用未翻译的原值。</summary>
    private sealed class CategoryChoice
    {
        public CategoryChoice(string category) => Category = category;

        public string Category { get; }

        public override string ToString() => Loc.T(Category);
    }
}

/// <summary>左侧菜单列表的一行。</summary>
public sealed class GestureMenuListItem
{
    public GestureMenuListItem(GestureMenuDef menu) => Menu = menu;

    public GestureMenuDef Menu { get; }

    public string Name => Menu.DisplayName;

    public string Sub => Loc.F("{0} 个条目", Menu.Items.Count);
}

/// <summary>中间条目列表的一行。</summary>
public sealed class GestureMenuEntryItem
{
    private readonly Func<string, string?> _titleOf;

    public GestureMenuEntryItem(GestureMenuItem item, Func<string, string?> actionTitleOf)
    {
        Item = item;
        _titleOf = actionTitleOf;
    }

    public GestureMenuItem Item { get; }

    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Item.Title))
            {
                return Item.Title.Trim();
            }

            string? actionTitle = string.IsNullOrWhiteSpace(Item.Action) ? null : _titleOf(Item.Action);
            return string.IsNullOrWhiteSpace(actionTitle) ? Loc.T("（还没选动作）") : actionTitle!;
        }
    }

    public string Sub
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Item.Action))
            {
                return Loc.T("（还没选动作）");
            }

            string actionTitle = _titleOf(Item.Action) ?? Item.Action;
            return string.IsNullOrWhiteSpace(Item.Arg)
                ? actionTitle
                : Loc.F("{0} · 参数 {1}", actionTitle, Item.Arg.Trim());
        }
    }
}
