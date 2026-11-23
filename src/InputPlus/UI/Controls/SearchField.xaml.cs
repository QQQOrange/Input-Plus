using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InputPlus.UI.Controls;

/// <summary>
/// 搜索框里的一个「分类」：用户先选它，再输关键词，就只搜这一列。
/// </summary>
public sealed class SearchCategory
{
    public SearchCategory(string label, string tag)
    {
        Label = label;
        Tag = tag;
    }

    /// <summary>下拉里显示的文字（已本地化）。</summary>
    public string Label { get; }

    /// <summary>给宿主页面认的稳定标识，别拿显示文字当条件。</summary>
    public string Tag { get; }

    public override string ToString() => Label;
}

/// <summary>
/// 全软件统一的搜索框（放大镜 / 清空 / 分类下拉都在框里面）。
///
/// 宿主页面只需要做三件事：设 <see cref="Placeholder"/>、按需
/// <see cref="SetCategories"/>、在 <see cref="SearchChanged"/> /
/// <see cref="CategoryChanged"/> 里重算过滤结果 —— 过滤规则本身留在页面里，
/// 因为每一页"搜哪些列"是业务知识（别名搜关键字/正文/备注，按键映射搜
/// 功能名/分类/已绑定的键），控件不该知道。
/// </summary>
public partial class SearchField : UserControl
{
    /// <summary>每次键入（或清空）都发一次；宿主据此重跑过滤。</summary>
    public event RoutedEventHandler? SearchChanged;

    /// <summary>分类换了。宿主应把已有输入重新过滤一遍（不是清空）。</summary>
    public event RoutedEventHandler? CategoryChanged;

    /// <summary>用户按了回车。多数页面不需要（边打边筛），留给"跳转第一个结果"这种用法。</summary>
    public event RoutedEventHandler? SearchSubmitted;

    /// <summary>用户按了 Esc 且框里没有字 —— 宿主可以借此收起弹出层。</summary>
    public event RoutedEventHandler? SearchCancelled;

    private bool _syncing;

    /// <summary>装填分类/程序化改选中时，暂时不发 SearchChanged / CategoryChanged。</summary>
    private bool _suppressEvents;

    public SearchField()
    {
        InitializeComponent();
    }

    // ---------- 依赖属性 ----------

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SearchField),
        new PropertyMetadata("", OnPlaceholderChanged));

    /// <summary>输入框为空时显示在框里的灰字。</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText), typeof(string), typeof(SearchField),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSearchTextPropertyChanged));

    /// <summary>框里的文字。代码里可以读它来判断"有没有在筛选"。</summary>
    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value ?? "");
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SearchField field) field.PlaceholderText.Text = (string)(e.NewValue ?? "");
    }

    private static void OnSearchTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (SearchField)d;
        if (field._syncing) return;
        string text = (string)(e.NewValue ?? "");
        if (field.SearchInput.Text == text) return;
        field._syncing = true;
        try
        {
            field.SearchInput.Text = text;
            field.RefreshChrome();
        }
        finally { field._syncing = false; }
    }

    // ---------- 公开操作 ----------

    /// <summary>
    /// 设定分类。传 0 项 = 这一页没有分类（下拉与竖线一起收掉）；传 1 项也收掉
    /// （只有一个选项的下拉是纯粹的噪声）。
    ///
    /// 装填期间**不发事件**：宿主页面通常在构造函数里调它，那一刻 _host 还没赋值，
    /// 一进来就触发一次过滤只会让每个页面都要写 `_host == null` 守卫，
    /// 而且用户还没开始搜就先筛了一遍，纯属噪声。
    /// </summary>
    public void SetCategories(params SearchCategory[] categories)
    {
        bool was = _suppressEvents;
        _suppressEvents = true;
        try
        {
            CategoryBox.Items.Clear();
            foreach (var category in categories ?? Array.Empty<SearchCategory>())
                CategoryBox.Items.Add(category);

            bool show = CategoryBox.Items.Count > 1;
            CategoryBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            CategorySeparator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            /* 只有一个分类时下拉收掉，但仍然要选中它 —— 页面照样靠 SelectedTag
             * 决定"只搜哪一列"，不选就等于没有分类，过滤逻辑会退回"全列搜"。 */
            if (CategoryBox.Items.Count > 0 && CategoryBox.SelectedIndex < 0)
                CategoryBox.SelectedIndex = 0;
        }
        finally { _suppressEvents = was; }
    }

    /// <summary>当前选中的分类标识；没有分类时返回 null。</summary>
    public string? SelectedTag => (CategoryBox.SelectedItem as SearchCategory)?.Tag;

    /// <summary>
    /// 框里那个真正的 TextBox。
    ///
    /// 给需要拿它当锚点的页面用（例如按键映射的结果弹层要按输入框的插入符定位）。
    /// 不要拿它去改文字 —— 文字请走 <see cref="SearchText"/>，否则会绕开
    /// 依赖属性与 <see cref="SearchChanged"/> 的同步。
    /// </summary>
    public TextBox InputBox => SearchInput;

    /// <summary>按标识选分类；找不到就保持原样。</summary>
    public void SelectCategory(string? tag)
    {
        if (tag == null) return;
        foreach (var item in CategoryBox.Items.OfType<SearchCategory>())
        {
            if (string.Equals(item.Tag, tag, StringComparison.Ordinal))
            {
                CategoryBox.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>把焦点放进输入框并全选（Ctrl+F 之类的地方用得上）。</summary>
    public void FocusInput()
    {
        SearchInput.Focus();
        Keyboard.Focus(SearchInput);
        SearchInput.SelectAll();
    }

    /// <summary>清空输入（不发 CategoryChanged）。</summary>
    public void ClearSearch()
    {
        if (SearchInput.Text.Length == 0) return;
        SearchInput.Clear();   // 会走 OnInputTextChanged → SearchChanged
    }

    // ---------- 内部 ----------

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            SetCurrentValue(SearchTextProperty, SearchInput.Text ?? "");
            RefreshChrome();
        }
        finally { _syncing = false; }

        SearchChanged?.Invoke(this, new RoutedEventArgs());
    }

    /// <summary>清空按钮 / 占位文字的显隐：纯外观，跟着文字走。</summary>
    private void RefreshChrome()
    {
        ClearButton.Visibility = string.IsNullOrEmpty(SearchInput.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        SearchInput.Clear();
        FocusInput();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SearchInput.Text.Length > 0)
            {
                SearchInput.Clear();
                e.Handled = true;
                return;
            }
            SearchCancelled?.Invoke(this, new RoutedEventArgs());
            return;
        }

        if (e.Key == Key.Enter)
        {
            SearchSubmitted?.Invoke(this, new RoutedEventArgs());
            // 不设 Handled：调用方可能还想自己处理（比如跳转后继续留给 DataGrid）
        }
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, CategoryBox)) return;
        if (_suppressEvents) return;
        CategoryChanged?.Invoke(this, new RoutedEventArgs());
        // 换了分类就等于重新定义了"搜什么"，把已有文字再过滤一遍；
        // 这一下对用户是"结果立刻变了"，但输入框里的字不动。
        SearchChanged?.Invoke(this, new RoutedEventArgs());
    }
}
