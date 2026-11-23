using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InputPlus.Core.Actions;
using InputPlus.UI.Controls;
using InputPlus.UI.Theme;

namespace InputPlus.UI.Windows;

/// <summary>
/// 动作选择器：一级分类 → 二级分类 → 具体动作，顶部可全局搜索。
///
/// 动作目录有一百多项，原来的 ComboBox 只能靠滚轮翻，找一个「窗口置顶」
/// 要翻很久。这里把「找」拆成两步：先选大类，再选子类，最后只在几十项里挑；
/// 知道名字的人直接搜，搜索结果不再受分类限制。
/// </summary>
public partial class ActionPickerWindow : Window
{
    /// <summary>一级分类 → 它包含的动作分类（ActionInfo.Category 的原文）。</summary>
    private static readonly (string Name, string[] Categories)[] Groups =
    {
        ("文本与光标", new[] { "文本编辑", "光标移动", "输入文本" }),
        ("窗口与系统", new[] { "窗口功能", "窗口绑定", "系统功能", "系统", "智能直达" }),
        ("鼠标", new[] { "鼠标功能" }),
        ("剪贴板", new[] { "剪贴板" }),
        ("按键与宏", new[] { "基础", "自定义按键", "宏" }),
        ("链接与直达", new[] { "直达链接" }),
    };

    private readonly List<ActionInfo> _all;
    private readonly Dictionary<string, Button> _groupButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _subButtons = new(StringComparer.Ordinal);
    private string _group = "";
    private string _category = "";

    public ActionPickerWindow(IEnumerable<ActionInfo> actions, string? selectedId)
    {
        InitializeComponent();

        _all = actions
            .Where(a => !a.Hidden)
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.DisplayCategory, StringComparer.CurrentCulture)
            .ThenBy(a => a.Title, StringComparer.CurrentCulture)
            .ToList();

        SelectedId = selectedId ?? "";

        /* 分类在构造期就装好：Loaded 里会 SearchBox.FocusInput()，
         * 而任何一次键入都要能读到 SelectedTag，晚装就会先跑一遍"没有分类"的
         * 过滤路径，出现"刚打字就闪一下全部结果"。
         *
         * 只给三项：动作列表里真的显示出来的就是「功能名 / 分类」两层，
         * 再加一项「动作 ID」会让下拉里躺着一个用户根本没处看到的列。
         * 「全部」保留原来的多列 OR，老习惯不受影响。 */
        SearchBox.SetCategories(
            new SearchCategory(Loc.T("全部"), "all"),
            new SearchCategory(Loc.T("功能名"), "name"),
            new SearchCategory(Loc.T("分类"), "category"));

        SourceInitialized += (_, _) =>
        {
            try
            {
                bool dark = ThemeManager.Current == ThemeManager.Mode.Dark
                            || (ThemeManager.Current == ThemeManager.Mode.System && ThemeManager.IsDarkSystem);
                WindowChrome.Apply(this, dark, WindowChrome.BackdropType.None);
            }
            catch
            {
                // 只是观感，失败不影响选择
            }
        };

        Loaded += (_, _) =>
        {
            /* 主窗口可以贴在屏幕右边缘，CenterOwner 会把对话框推到屏幕外。
             * 统一夹到主屏工作区，保证标题栏和“选择”按钮都看得见。 */
            var work = SystemParameters.WorkArea;
            Width = Math.Min(Width, Math.Max(MinWidth, work.Width - 24));
            Height = Math.Min(Height, Math.Max(MinHeight, work.Height - 24));
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
            BuildGroupButtons();
            SearchBox.FocusInput();
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;

            /* 焦点还在搜索框里、框里又有字时，把 Esc 让给控件去"清空" ——
             * 这正是统一搜索框的既有语义（第一次 Esc 清字，第二次收起）。
             * 其它情况（焦点在大类 Chip / 列表上）照旧一键关闭对话框：
             * 焦点不在输入框里时用户按 Esc 想的就是"退出"。 */
            if (SearchBox.SearchText.Length > 0 && SearchBox.IsKeyboardFocusWithin) return;

            DialogResult = false;
            e.Handled = true;
        };
    }

    /// <summary>用户选中的动作 ID；空串表示「关闭双击」。</summary>
    public string SelectedId { get; private set; }

    private void BuildGroupButtons()
    {
        GroupPanel.Children.Clear();
        _groupButtons.Clear();

        foreach (var (name, cats) in Groups)
        {
            if (!_all.Any(a => cats.Contains(a.Category, StringComparer.OrdinalIgnoreCase))) continue;
            AddChip(GroupPanel, name, () => SelectGroup(name), _groupButtons);
        }

        // 没被上面任何一组收进去的分类，统一进「其他」，避免动作“消失”。
        var known = Groups.SelectMany(g => g.Categories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_all.Any(a => !known.Contains(a.Category)))
            AddChip(GroupPanel, "其他", () => SelectGroup("其他"), _groupButtons);
    }

    private void SelectGroup(string name)
    {
        _group = name;
        _category = "";
        foreach (var (key, button) in _groupButtons)
            SetChipState(button, key == name);

        SubPanel.Children.Clear();
        _subButtons.Clear();

        var cats = CategoriesOf(name);
        foreach (var cat in cats)
        {
            int count = _all.Count(a => string.Equals(a.Category, cat, StringComparison.OrdinalIgnoreCase));
            if (count == 0) continue;
            string label = $"{Loc.T(cat)} ({count})";
            string captured = cat;
            var button = AddChip(SubPanel, label, () => SelectCategory(captured), _subButtons, cat);
            if (cat == _category) SetChipState(button, true);
        }

        ActionList.ItemsSource = null;
        SearchBox.ClearSearch();
        SearchBox.FocusInput();
    }

    private IEnumerable<string> CategoriesOf(string group)
    {
        if (group == "其他")
        {
            var known = Groups.SelectMany(g => g.Categories).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _all.Select(a => a.Category).Where(c => !known.Contains(c)).Distinct();
        }

        var def = Groups.FirstOrDefault(g => g.Name == group);
        return def.Categories ?? Array.Empty<string>();
    }

    private void SelectCategory(string category)
    {
        _category = category;
        foreach (var (key, button) in _subButtons)
            SetChipState(button, string.Equals(key, category, StringComparison.OrdinalIgnoreCase));

        ShowActions(_all.Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase)));
        SearchBox.FocusInput();
    }

    private void ShowActions(IEnumerable<ActionInfo> actions)
    {
        var list = actions.ToList();
        ActionList.ItemsSource = list;
        if (list.Count == 0) return;

        var current = list.FirstOrDefault(a => string.Equals(a.Id, SelectedId, StringComparison.OrdinalIgnoreCase));
        ActionList.SelectedItem = current ?? list[0];
        ActionList.ScrollIntoView(ActionList.SelectedItem);
    }

    /// <summary>
    /// 关键词过滤。分类决定「只搜哪一列」：选了「分类」就只按分类命中，
    /// 动作描述里恰好有同一个词不会再把这条留下来 —— 旧写法是多列 OR，
    /// 用户选了分类却还是搜出一堆别的东西，等于分类没生效。
    /// </summary>
    private void OnSearchChanged(object sender, RoutedEventArgs e)
    {
        string text = SearchBox.SearchText.Trim();
        if (text.Length == 0)
        {
            // 空关键词 = 不过滤：回到「当前二级分类的全部动作」，没选分类就清空列表。
            if (_category.Length > 0)
                ShowActions(_all.Where(a => string.Equals(a.Category, _category, StringComparison.OrdinalIgnoreCase)));
            else
                ActionList.ItemsSource = null;
            return;
        }

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // 参数类型写全：switch 表达式里的匿名 lambda 靠目标类型推断，写明确省得编译器绕。
        Func<ActionInfo, string, bool> matches = SearchBox.SelectedTag switch
        {
            "name" => (ActionInfo a, string w) => a.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase),
            "category" => (ActionInfo a, string w) => a.DisplayCategory.Contains(w, StringComparison.CurrentCultureIgnoreCase),
            // 「全部」沿用原来的多列 OR：动作 ID 不单列一项（列表里看不到它），但整体搜还能搜到。
            _ => (ActionInfo a, string w) =>
                a.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                a.Description.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                a.DisplayCategory.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                a.Id.Contains(w, StringComparison.OrdinalIgnoreCase),
        };

        ShowActions(_all.Where(a => words.All(w => matches(a, w))));
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ActionList.SelectedItem is ActionInfo) Accept();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (ActionList.SelectedItem is not ActionInfo info) return;
        SelectedId = info.Id;
        DialogResult = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        SelectedId = "";
        DialogResult = true;
    }

    private static Button AddChip(Panel panel, string label, Action action,
        Dictionary<string, Button> map, string? key = null)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Style = (Style)Application.Current.FindResource("Button.Base"),
        };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
        map[key ?? label] = button;
        return button;
    }

    private static void SetChipState(Button button, bool active)
    {
        button.Background = active
            ? (Brush)Application.Current.FindResource("Brush.Accent")
            : (Brush)Application.Current.FindResource("Brush.SurfaceAlt");
        button.Foreground = active
            ? (Brush)Application.Current.FindResource("Brush.Surface")
            : (Brush)Application.Current.FindResource("Brush.TextPrimary");
    }
}
