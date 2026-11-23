using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InputPlus.Core.Settings;
using InputPlus.UI.Windows;

namespace InputPlus.UI.Views;

public partial class ClipboardView : UserControl
{
    private AppHost? _host;

    public ClipboardView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        EnableBox.IsChecked = _host.Clipboard.ClipboardFeatureOn;
        Slot1Text.Text = Preview(_host.Clipboard.SlotText(1));
        Slot2Text.Text = Preview(_host.Clipboard.SlotText(2));
        HistoryList.ItemsSource = _host.Clipboard.History
            .Select(t => new HistoryVm(t))
            .ToList();
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Loc.T("（空）");
        string flat = text.Replace("\r", "").Replace("\n", " ⏎ ");
        return flat.Length > 400 ? flat[..400] + "…" : flat;
    }

    private void OnToggleEnabled(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.Clipboard.ClipboardFeatureOn = EnableBox.IsChecked == true;
        _host.Settings.Global.AllowClipboard = _host.Clipboard.ClipboardFeatureOn;
        _host.SaveSettings();
        _host.Notify(_host.Clipboard.ClipboardFeatureOn ? Loc.T("独立剪贴板已开启") : Loc.T("独立剪贴板已关闭"));
    }

    /* 这两个按钮以前是「粘贴」—— 但在设置窗口里点它时，前台窗口**就是设置窗口自己**，
     * 于是 Paste(slot) 只是往设置页发了个 Ctrl+V：页面上没有输入框，
     * 用户看到的就是"点了没反应"。改成复制到系统剪贴板，结果确定且随时可用。 */
    private void OnCopy1(object sender, RoutedEventArgs e) => _host?.Clipboard.CopySlotToSystem(1);

    private void OnCopy2(object sender, RoutedEventArgs e) => _host?.Clipboard.CopySlotToSystem(2);

    private void OnClear1(object sender, RoutedEventArgs e)
    {
        _host?.Clipboard.ClearSlot(1);
        Refresh();
    }

    private void OnClear2(object sender, RoutedEventArgs e)
    {
        _host?.Clipboard.ClearSlot(2);
        Refresh();
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        _host?.Clipboard.ClearHistory();
        Refresh();
    }

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_host == null || HistoryList.SelectedItem is not HistoryVm vm) return;
        /* 双击一条历史 = 把它拿走（复制到系统剪贴板）。
         * 以前这里是 PasteText → 翻成快捷键发往**设置窗口自己**，同样"没反应"。 */
        _host.Clipboard.CopyTextToSystem(vm.Text);
    }

    // ---------------- 剪贴板 → 别名 ----------------

    /// <summary>
    /// 行内「加入别名」被双击时把事件截下来。
    ///
    /// 为什么必须截：按钮在 ListBox 的行里，双击会**冒泡**到 ListBox 的
    /// MouseDoubleClick —— 那条路是"复制到系统剪贴板"。用户在按钮上双击
    /// （手重点就会）时既想加别名、又顺手把内容换成了系统剪贴板。
    /// 这里只写一句 Handled，不动别的逻辑。
    /// </summary>
    private void OnAddAliasDoubleClickGuard(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnAddToAlias(object sender, RoutedEventArgs e)
    {
        /* 按钮知道自己是哪一行（DataContext 就是那一行的 vm），
         * 不去读 SelectedItem —— 否则右键改选中、键盘改选中都会串行。 */
        if (sender is FrameworkElement { DataContext: HistoryVm vm }) AddToAlias(vm.Text);
    }

    private void OnAddSlot1ToAlias(object sender, RoutedEventArgs e) => AddSlotToAlias(1);

    private void OnAddSlot2ToAlias(object sender, RoutedEventArgs e) => AddSlotToAlias(2);

    private void AddSlotToAlias(int slot)
    {
        if (_host == null) return;
        string? text = _host.Clipboard.SlotText(slot);
        if (string.IsNullOrWhiteSpace(text))
        {
            /* 不做 IsEnabled 绑定（那会牵动 Refresh 里的绑定逻辑）：
             * 格子空的时候点一下给一句明确提示就够了。 */
            _host.Notify(Loc.T("这一格是空的"));
            return;
        }
        AddToAlias(text);
    }

    /// <summary>右键菜单弹之前先把光标下那一行选中；没点在行上就不弹。</summary>
    private void OnHistoryRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        var item = ItemsControl.ContainerFromElement(HistoryList, source) as ListBoxItem;
        if (item?.DataContext is HistoryVm) HistoryList.SelectedItem = item.DataContext;
    }

    private void OnHistoryContextOpening(object sender, ContextMenuEventArgs e)
    {
        /* 列表是空的、或者点在空白处：没有"那条内容"可选，菜单不该出现。 */
        if (HistoryList.SelectedItem is not HistoryVm) e.Handled = true;
    }

    private void OnAddHistoryToAlias(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryVm vm) AddToAlias(vm.Text);
    }

    private void OnCopyHistoryToSystem(object sender, RoutedEventArgs e)
    {
        if (_host == null || HistoryList.SelectedItem is not HistoryVm vm) return;
        _host.Clipboard.CopyTextToSystem(vm.Text);
    }

    /// <summary>
    /// 「剪贴板 → 别名」的唯一入口（行内按钮、右键菜单、两张卡片都走这里）。
    ///
    /// 流程：问触发词 → <see cref="SnippetDraft.Build"/>（补前缀/查重/截断）
    ///      → 入库并保存 → 把最终触发词挂到 <see cref="AppHost.PendingAliasTrigger"/>
    ///      → 打开别名页（由它负责切「全部」、清搜索、选中这一条）。
    /// </summary>
    private void AddToAlias(string? text)
    {
        if (_host == null) return;

        /* 空内容：没有可加的别名。历史列表里理论上不会有空串，
         * 但槽位是用户随时能清空的，这里必须自己兜住。 */
        if (string.IsNullOrWhiteSpace(text))
        {
            _host.Notify(Loc.T("这条剪贴板内容是空的"));
            return;
        }

        var prefixes = _host.Settings.Hotstring.EffectivePrefixes;
        if (prefixes.Count == 0)
        {
            _host.Notify(Loc.T("先在「别名替换」里设一个前缀符号"));
            return;
        }

        var snippets = _host.Settings.Global.Snippets;

        /* 提示里把"当前前缀"写成真的那个符号：写死「;」会在用户改成 `、`
         * 或 `:` 时变成一句假说明。 */
        string hint = Loc.F(
            "给这段内容起一个触发词。不用写前缀，会自动补「{0}」。确定后会跳到「别名替换」页，你可以在那里继续改内容和分组。",
            prefixes[0]);

        /* 已经有内容完全相同的别名：提前说一声，别让用户以为是新的一条。 */
        var same = snippets.FirstOrDefault(x => string.Equals(x.Text ?? "", text, StringComparison.Ordinal));
        if (same != null) hint += Loc.F("（已有一条内容相同的别名：{0}）", same.Trigger);

        string? typed = TextPromptWindow.Ask(
            Window.GetWindow(this),
            Loc.T("加入别名"),
            hint,
            SnippetDraft.SuggestTrigger(text, prefixes, snippets));

        /* 取消：什么都不做、不提示、不保存。 */
        if (typed == null) return;

        var snippet = SnippetDraft.Build(text, typed, prefixes, snippets);
        if (snippet == null)
        {
            _host.Notify(Loc.T("这个触发词没法用，换一个再试"));
            return;
        }

        snippets.Add(snippet);
        _host.SaveSettings();

        /* 选中链路：别名页取走这个触发词后自己切组、清搜索、选中并打开编辑器。 */
        _host.PendingAliasTrigger = snippet.Trigger;
        _host.ShowMainWindow("hotstrings");

        /* 通知里写清两件用户看不见的事：最终用了哪个触发词（撞名会被改）、
         * 内容有没有被截断。 */
        string notice = Loc.F("已加入别名 {0}", snippet.Trigger);
        string plain = SnippetDraft.Normalize(typed, prefixes, Array.Empty<Snippet>());
        if (!string.Equals(plain, snippet.Trigger, StringComparison.Ordinal))
            notice += Loc.F("（{0} 已被占用，自动改成了 {1}）", plain, snippet.Trigger);
        if (text.Length > SnippetDraft.MaxTextLength)
            notice += Loc.F("（已截断到 {0} 字）", SnippetDraft.MaxTextLength);

        _host.Notify(notice);
    }

    private sealed class HistoryVm
    {
        public HistoryVm(string text) => Text = text;
        public string Text { get; }
        public string Preview
        {
            get
            {
                string flat = Text.Replace("\r", "").Replace("\n", " ⏎ ");
                return flat.Length > 160 ? flat[..160] + "…" : flat;
            }
        }
    }
}
