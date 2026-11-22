using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InputPlus.Core.Icons;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;
using InputPlus.Core.Suggestions;
using InputPlus.UI.Windows;

namespace InputPlus.UI.Views;

public partial class QbarSettingsView : UserControl
{
    private AppHost? _host;
    private string _tab = "search";
    private QEntry? _editing;
    private bool _suppress;

    private readonly DispatcherTimer _titleTimer;
    private int _titleToken;

    /* 文件搜索的页内目录：(卡片, 目录按钮)。点按钮滚到卡片，滚动时反过来点亮按钮。 */
    private readonly List<(FrameworkElement Card, Button Chip)> _toc = new();
    private bool _tocSyncing;

    /* 文件索引是后台建的，界面上追进度用；只在这一页活着的时候跑，建完就停。 */
    private DispatcherTimer? _indexTimer;

    private static readonly HttpClient TitleHttp = new() { Timeout = TimeSpan.FromSeconds(4) };

    static QbarSettingsView()
    {
        try
        {
            TitleHttp.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) InputPlus/1.0");
        }
        catch { }
    }

    private static readonly string[] Palette =
    {
        "#2B6CF6", "#6C4CF6", "#22C1E8", "#1F9D62", "#D9822B", "#E5484D", "#111827", "#8A93A6",
    };

    public QbarSettingsView()
    {
        InitializeComponent();
        BuildFileToc();
        _titleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _titleTimer.Tick += async (_, _) =>
        {
            _titleTimer.Stop();
            await FillWebTitleAsync();
        };
    }

    /// <summary>
    /// 渲染/自检用：按 Tag 切标签。
    /// 「文件搜」页平时是折叠的，不做这一步，回归截图里永远看不到它 ——
    /// 排坏了也没人发现。
    /// </summary>
    internal void SelectTabForRender(string tag)
    {
        foreach (object item in Tabs.Items)
        {
            if (item is TabItem t && (t.Tag as string) == tag)
            {
                Tabs.SelectedItem = t;
                break;
            }
        }
        UpdateLayout();
    }

    /// <summary>渲染用：把「文件搜」面板滚到指定比例（0=顶 1=底），回归要看到下半部分。</summary>
    internal void ScrollFilesToForRender(double ratio)
    {
        FileScroll.UpdateLayout();
        FileScroll.ScrollToVerticalOffset(FileScroll.ScrollableHeight * ratio);
        FileScroll.UpdateLayout();
    }

    /// <summary>渲染用：直接滚到某张卡（按目录索引 0..4），方便逐卡截图核对。</summary>
    internal void ScrollFilesToCardForRender(int index)
    {
        if (index < 0 || index >= _toc.Count) return;
        ScrollToCard(_toc[index].Card);
        UpdateLayout();
    }

    /// <summary>渲染用：把右栏滚到底，「文件搜索」那张卡在下面。</summary>
    internal void ScrollRightToEndForRender()
    {
        RightScroll.UpdateLayout();
        RightScroll.ScrollToEnd();
    }

    public void Initialize(AppHost host)
    {
        _host = host;
        BuildSwatches();
        Tabs.SelectedIndex = 0;
        _tab = "search";        // 重新进设置页一律从「搜索指令」开始，别停在上次的标签
        Refresh();
    }

    private void BuildSwatches()
    {
        ColorSwatches.Items.Clear();
        foreach (var color in Palette)
        {
            var btn = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 7, 7),
                Background = (Brush)new BrushConverter().ConvertFromString(color)!,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = color,
                ToolTip = color,
            };
            btn.Template = (ControlTemplate)FindResource("SwatchTemplate");
            btn.Click += (_, _) =>
            {
                if (_host == null) return;
                _host.Settings.Qbar.AccentColor = color;
                _host.SaveSettings();
                MarkSelectedSwatch();
            };
            ColorSwatches.Items.Add(btn);
        }
    }

    private void MarkSelectedSwatch()
    {
        if (_host == null) return;
        string current = _host.Settings.Qbar.AccentColor;
        foreach (Button b in ColorSwatches.Items)
        {
            bool sel = string.Equals(b.Tag as string, current, StringComparison.OrdinalIgnoreCase);
            b.BorderBrush = sel ? (Brush)FindResource("Brush.TextPrimary") : Brushes.Transparent;
        }
    }

    public void Refresh()
    {
        if (_host == null) return;
        _suppress = true;
        ListCountSlider.Value = _host.Settings.Qbar.ListCount;
        RecentCountSlider.Value = _host.Settings.Qbar.RecentCount;
        UpdateRecentCountLabel();
        FontSizeSlider.Value = _host.Settings.Qbar.TextFontSize;
        UpdateFontPreview();
        ShowIconsBox.IsChecked = _host.Settings.Qbar.ShowIcons;
        ImeOnOpenBox.IsChecked = _host.Settings.Qbar.SwitchToEnglishOnOpen;
        ImeBackBox.IsChecked = _host.Settings.Qbar.SwitchBackOnEngine;
        UpdatePositionLabel();

        /* 联想参数也是“控件跟随设置”，不是反过来：
           设置文件里可能被手改成超范围的值，这里顺手夹到界面允许的区间，
           免得滑杆位置和真实请求参数对不上。 */
        SuggestEnabledBox.IsChecked = _host.Settings.Qbar.SuggestEnabled;
        SelectSuggestEngine(_host.Settings.Qbar.SuggestEngine);
        SuggestMaxSlider.Value = Math.Clamp(_host.Settings.Qbar.SuggestMax, 1, 10);
        SuggestTimeoutSlider.Value = Math.Clamp(_host.Settings.Qbar.SuggestTimeoutMs, 200, 5000);
        SuggestCacheSlider.Value = Math.Clamp(_host.Settings.Qbar.SuggestCacheMinutes, 0, 1440);
        UpdateSuggestLabels();
        RefreshFileDialogBar();
        RefreshSuggestions();

        /* 语义层开关：控件跟随设置，越界值夹回界面允许的区间（设置文件可能被手改）。 */
        QbarSearchOptions sem = _host.Settings.Qbar.Search;
        SemanticRulesBox.IsChecked = sem.RulesEnabled;
        // 后端固定 Everything（见 QbarSearchOptions.EverythingOnly），这一页只显示只读说明，没有开关。
        if (SemanticEverythingNote != null) SemanticEverythingNote.Visibility = Visibility.Visible;
        SemanticBuiltinRulesBox.IsChecked = sem.BuiltinRulesEnabled;
        SemanticPinyinBox.IsChecked = sem.PinyinEnabled;
        SemanticFacetsBox.IsChecked = sem.FacetsEnabled;
        SemanticHistoryBox.IsChecked = sem.HistoryEnabled;
        SemanticDebugBox.IsChecked = sem.DebugPanel;
        SemanticWindowSlider.Value = Math.Clamp(sem.MaxCandidates, 200, 20000);
        SemanticDebounceSlider.Value = Math.Clamp(sem.DebounceMs, 0, 200);
        UpdateSemanticLabels();
        _suppress = false;
        MarkSelectedSwatch();
        ShowFilesTab(_tab == "files" || _tab == "middle");
        LoadEntries();
        RefreshFilterList(rebindEditor: true);
        RefreshRuleList(rebindEditor: true);
        RefreshIndexStatus();
    }

    private List<QEntry> CurrentList() => _tab switch
    {
        "run" => _host!.Settings.Run,
        "web" => _host!.Settings.Web,
        "pinned" => _host!.Settings.Qbar.Pinned,
        _ => _host!.Settings.Search,
    };

    private void LoadEntries()
    {
        if (_host == null) return;
        // 「文件搜」标签没有条目列表：左边那块整块让给了过滤器/优先级编辑器。
        if (_tab == "files") return;
        _editing = null;
        _suppress = true;
        EntryList.ItemsSource = null;

        // 过滤：按关键字 / 备注 / 内容模糊匹配，大小写不敏感。
        // 这样内置 300 多个引擎也能很快找到想要的那个。
        // 匹配规则抽在 QEntry.Matches 里，自检能直接验。
        string filter = FilterBox?.Text?.Trim() ?? "";
        var all = CurrentList().ToList();
        /* 默认搜索引擎原来是列表里单独的一行（备注"默认搜索"）。现在那行不再出现，
         * 改成正牌必应行上的「默认」徽标 —— 同一个引擎没必要占两行。
         * 这个 Key 必须在过滤掉之前取出来。 */
        string defaultEngineKey = "";
        if (_tab == "search")
        {
            defaultEngineKey = (all.FirstOrDefault(x =>
                string.Equals((x.Comment ?? "").Trim(), "默认搜索", StringComparison.Ordinal))?.Key ?? "").Trim();
            if (defaultEngineKey.Length > 0)
                all = all.Where(x => !string.Equals((x.Comment ?? "").Trim(), "默认搜索", StringComparison.Ordinal)).ToList();
        }
        int total = all.Count;
        var items = QEntry.Filter(all, filter)
            .Select(e => new EntryVm(e, _tab != "run", _tab == "pinned",
                defaultEngineKey.Length > 0 && string.Equals(e.Key, defaultEngineKey, StringComparison.Ordinal)))
            .ToList();
        EntryList.ItemsSource = items;
        if (items.Count > 0) EntryList.SelectedIndex = 0;
        _suppress = false;
        if (items.Count > 0) BindEditor(items[0].Entry);
        else ClearEditor();

        if (FilterCount != null)
        {
            FilterCount.Text = filter.Length > 0
                ? Loc.F("匹配 {0} / {1}", items.Count, total)
                : Loc.F("共 {0} 项", total);
        }

        ValueHelp.Text = _tab switch
        {
            "search" => Loc.T("用 {q} 代表关键词。可以填网址，例如 https://www.bing.com/search?q={q}；也可以填程序路径并在“参数”里写 -w {q}，直接调用欧路词典这类本地查词程序。"),
            "run" => Loc.T("填程序路径或命令，例如 C:\\Windows\\notepad.exe 或 notepad.exe；参数写在下方“参数”里，{q} 代表 Qbar 中输入的内容。"),
            "web" => Loc.T("填完整网址，例如 https://github.com。"),
            "pinned" => Loc.T("填文件、文件夹、网址或程序路径，例如 D:\\\\项目 或 C:\\\\Windows\\\\notepad.exe；输入名称就能在 Qbar 里直接打开。"),
            _ => "",
        };
        ValueLabel.Text = _tab switch
        {
            "search" => Loc.T("搜索链接"),
            "run" => Loc.T("程序路径"),
            "web" => Loc.T("网址"),
            "pinned" => Loc.T("目标路径 / 网址"),
            _ => Loc.T("内容"),
        };
        EntryHint.Text = _tab switch
        {
            "search" => Loc.T("Qbar 里输入「关键字 内容」即可用对应引擎搜索"),
            "run" => Loc.T("Qbar 里输入关键字回车即可启动"),
            "web" => Loc.T("Qbar 里输入关键字回车即可打开"),
            "pinned" => Loc.T("Qbar 空面板会优先列出固定项，也可以直接搜索名称"),
            _ => "",
        };
    }

    /// <summary>把某条记录回填到右侧编辑框。</summary>
    private void BindEditor(QEntry entry)
    {
        _editing = entry;
        _suppress = true;
        KeyBox.Text = entry.Key;
        ValueBox.Text = entry.Value;
        CommentBox.Text = entry.Comment;
        IconBox.Text = entry.IconPath;
        ArgumentsBox.Text = entry.Arguments;
        AdminBox.IsChecked = entry.RunAsAdmin;
        ArgumentsRow.Visibility = _tab == "web" ? Visibility.Collapsed : Visibility.Visible;
        EditTitle.Text = string.IsNullOrEmpty(entry.Key) ? Loc.T("新条目") : Loc.F("正在编辑：{0}", entry.Key);
        _suppress = false;
        UpdateIconPreview(entry);
    }

    /// <summary>图标预览：有图标显示图标，没有就显示首字（和 Qbar 里的表现一致）。</summary>
    private void UpdateIconPreview(QEntry entry)
    {
        /* 先看内存，再看磁盘上的 favicon 缓存。
           只看 ForEntry 的话，网址永远拿不到图标（它的图标是下载的），
           每次点开条目都要先闪一下首字、等下载回来才换成真图标。 */
        var icon = IconCache.ForEntry(entry);
        if (icon == null) icon = IconCache.GetWebIcon(entry.Value);

        /* 真的没有缓存才去下载：这一条只会在第一次见到这个网址时走到。
         * 用户此时可能已经切到别的条目，所以回调里要再核对一次。 */
        if (icon == null && _tab == "web" && IsWebUrl(entry.Value))
        {
            IconCache.LoadWebIconAsync(entry.Value, image =>
            {
                if (image == null) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_editing, entry)) return;
                    IconPreview.Source = image;
                    IconPreview.Visibility = Visibility.Visible;
                    IconFallback.Visibility = Visibility.Collapsed;
                });
            });
        }

        if (icon != null)
        {
            IconPreview.Source = icon;
            IconPreview.Visibility = Visibility.Visible;
            IconFallback.Visibility = Visibility.Collapsed;
        }
        else
        {
            IconPreview.Source = null;
            IconPreview.Visibility = Visibility.Collapsed;
            IconFallback.Visibility = Visibility.Visible;
            IconFallback.Text = IconCache.Initial(entry.DisplayName);
            IconFallback.Foreground = new SolidColorBrush(IconCache.ColorFor(entry.DisplayName));
        }
    }

    private void OnPickIcon(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editing == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择图标（图片或程序）"),
            Filter = Loc.T("图标或程序|*.png;*.ico;*.jpg;*.jpeg;*.exe;*.dll|所有文件|*.*"),
        };
        if (dlg.ShowDialog() != true) return;
        IconBox.Text = dlg.FileName;
        _editing.IconPath = dlg.FileName;
        _host.SaveSettings();
        UpdateIconPreview(_editing);
        EntryList.Items.Refresh();
    }

    private void ClearEditor()
    {
        _editing = null;
        _suppress = true;
        KeyBox.Text = "";
        ValueBox.Text = "";
        CommentBox.Text = "";
        IconBox.Text = "";
        ArgumentsBox.Text = "";
        AdminBox.IsChecked = false;
        ArgumentsRow.Visibility = _tab == "web" ? Visibility.Collapsed : Visibility.Visible;
        EditTitle.Text = Loc.T("（选择左边的条目，或点新增）");
        _suppress = false;
        IconPreview.Source = null;
        IconPreview.Visibility = Visibility.Collapsed;
        IconFallback.Visibility = Visibility.Visible;
        IconFallback.Text = "?";
        IconFallback.Foreground = (Brush)FindResource("Brush.TextTertiary");
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => LoadEntries();

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || !IsLoaded) return;
        if (Tabs.SelectedItem is TabItem item && item.Tag is string tag)
        {
            _tab = tag;
            bool files = tag == "files";
            bool middle = tag == "middle";
            ShowFilesTab(files || middle);
            if (middle)
            {
                SyncMiddleMenu();
                return;
            }
            if (files)
            {
                /* 进这一页就把两套列表和索引状态刷新一次：
                   用户来这一页就是为了看「现在到底是哪条规则/哪个过滤器在起作用」。 */
                RefreshFilterList(rebindEditor: true);
                RefreshRuleList(rebindEditor: true);
                RefreshCommandList(rebindEditor: true);
                RefreshIndexStatus();
                return;
            }
            LoadEntries();
        }
    }

    private void OnEntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (EntryList.SelectedItem is not EntryVm vm) return;
        BindEditor(vm.Entry);
    }

    // ─────────────────── 附加菜单（v1.3.18） ───────────────────

    /// <summary>
    /// 把附加菜单那一页的控件灌成当前配置。只读配置，不触发保存。
    ///
    /// 这一页原来在「鼠标手势 → 手势设置」里，v1.3.17 搬到 Qbar 面板；
    /// v1.3.18 起内容也从"三个开关"扩成"鼠标唤出 / 键盘唤出 / 例外程序 / 全屏"。
    /// </summary>
    private void SyncMiddleMenu()
    {
        if (_host == null) return;
        var s = _host.Settings;
        _suppress = true;
        try
        {
            MiddleMenuEnabledBox.IsChecked = s.MiddleClickMenuEnabled;

            /* 鼠标键：设置里存的是 middle / x1 / x2，界面上是三个勾选框。
             * 一个都不勾 = 鼠标这一路关掉（代码里空列表就是"没有触发键"）。 */
            var buttons = s.MiddleMenuMouseButtons ?? new List<string>();
            MouseMiddleBox.IsChecked = buttons.Any(b => string.Equals(b, "middle", StringComparison.OrdinalIgnoreCase));
            MouseX1Box.IsChecked = buttons.Any(b => string.Equals(b, "x1", StringComparison.OrdinalIgnoreCase));
            MouseX2Box.IsChecked = buttons.Any(b => string.Equals(b, "x2", StringComparison.OrdinalIgnoreCase));

            string hotkey = (s.MiddleMenuHotkey ?? "").Trim();
            MiddleHotkeyBox.Text = hotkey.Length > 0 ? hotkey : Loc.T("未设置");
            if (TryFindResource(hotkey.Length > 0 ? "Brush.TextPrimary" : "Brush.TextTertiary") is Brush hb)
                MiddleHotkeyBox.Foreground = hb;

            /* 全屏开关和手势页共用一个字段：这里改，手势页那边立刻是同一个值。 */
            AttachFullscreenBox.IsChecked = s.GestureOptions.DisableInFullscreen;


            RefreshExceptionList();
            UpdateMiddleMenuAvailability();
        }
        finally { _suppress = false; }
    }

    /// <summary>总开关关着时，下面几组都变灰 —— 免得用户以为"勾了却没反应"。</summary>
    private void UpdateMiddleMenuAvailability()
    {
        bool on = MiddleMenuEnabledBox.IsChecked == true;
        MouseMiddleBox.IsEnabled = on;
        MouseX1Box.IsEnabled = on;
        MouseX2Box.IsEnabled = on;
        MiddleHotkeyBox.IsEnabled = on;
        AttachFullscreenBox.IsEnabled = on;
        ExceptionList.IsEnabled = on;
    }

    private void OnMiddleMenuToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || _host == null) return;
        _host.Settings.MiddleClickMenuEnabled = MiddleMenuEnabledBox.IsChecked == true;
        UpdateMiddleMenuAvailability();
        _host.SaveSettings();
    }

    /// <summary>中键 / 侧键 1 / 侧键 2 三个勾选框共用：整表重建再落盘。</summary>
    private void OnMouseButtonChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || _host == null) return;
        var buttons = new List<string>();
        if (MouseMiddleBox.IsChecked == true) buttons.Add("middle");
        if (MouseX1Box.IsChecked == true) buttons.Add("x1");
        if (MouseX2Box.IsChecked == true) buttons.Add("x2");
        _host.Settings.MiddleMenuMouseButtons = buttons;
        _host.SaveSettings();
    }

    /// <summary>键盘唤出：录制一个组合键（和手势总开关那颗用同一个捕获窗）。</summary>
    private void OnCaptureAttachHotkey(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        KeyCaptureWindow.Capture(result =>
        {
            if (string.IsNullOrWhiteSpace(result.Combo)) return;
            _host.Settings.MiddleMenuHotkey = result.Combo;
            _host.SaveSettings();                 // 触发 SettingsChanged → 重新注册热键
            MiddleHotkeyBox.Text = result.Combo;
            if (TryFindResource("Brush.TextPrimary") is Brush brush) MiddleHotkeyBox.Foreground = brush;
            _host.Notify(Loc.F("附加菜单唤出键已设为 {0}", result.Combo));
        }, title: Loc.T("按下附加菜单的唤出组合键"));
    }

    private void OnClearAttachHotkey(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.Settings.MiddleMenuHotkey = "";
        _host.SaveSettings();
        MiddleHotkeyBox.Text = Loc.T("未设置");
        if (TryFindResource("Brush.TextTertiary") is Brush brush) MiddleHotkeyBox.Foreground = brush;
    }

    /// <summary>全屏不接管：直接写手势那一份设置，两处永远同一个值。</summary>
    private void OnAttachFullscreenChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || _host == null) return;
        _host.Settings.GestureOptions.DisableInFullscreen = AttachFullscreenBox.IsChecked == true;
        _host.SaveSettings();
    }

    private void OnAddExceptionFile(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择要加入例外的程序"),
            Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        AddException(dlg.FileName);
    }

    /// <summary>「点选窗口」：用仓库现成的窗口选择器，选完按进程号反查 exe 路径。</summary>
    private void OnAddExceptionWindow(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var picker = new InputPlus.UI.Windows.WindowPickerWindow();
        var owner = Window.GetWindow(this);
        if (owner != null) picker.Owner = owner;
        picker.ShowDialog();
        nint hwnd = picker.PickedWindow;
        if (hwnd == 0) return;

        InputPlus.Core.Windows.WindowService.GetWindowThreadProcessIdForTest(hwnd, out uint pid);
        string path = pid == 0 ? "" : InputPlus.Core.Windows.WindowService.GetProcessPath(pid);
        if (string.IsNullOrWhiteSpace(path))
        {
            _host.Notify(Loc.T("读不到这个窗口对应的程序路径"));
            return;
        }
        AddException(path);
    }

    private void AddException(string path)
    {
        if (_host == null || string.IsNullOrWhiteSpace(path)) return;
        var list = _host.Settings.MiddleMenuExceptions ??= new List<string>();
        if (!list.Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
            list.Add(path);
        _host.SaveSettings();
        RefreshExceptionList();
    }

    private void OnRemoveException(object sender, RoutedEventArgs e)
    {
        if (_host == null || ExceptionList.SelectedItem is not AttachExceptionRow row) return;
        _host.Settings.MiddleMenuExceptions?.RemoveAll(x =>
            string.Equals(x, row.FullPath, StringComparison.OrdinalIgnoreCase));
        _host.SaveSettings();
        RefreshExceptionList();
    }

    private void RefreshExceptionList()
    {
        if (_host == null) return;
        var rows = new List<AttachExceptionRow>();
        foreach (var path in _host.Settings.MiddleMenuExceptions ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            rows.Add(new AttachExceptionRow
            {
                FullPath = path,
                Name = System.IO.Path.GetFileName(path),
                Detail = path,
            });
        }
        ExceptionList.ItemsSource = rows;
    }
    /// <summary>固定项行尾那个「中键」勾选框：改的是条目自己的 ShowInMiddleMenu。</summary>
    private void OnEntryMiddleMenuClick(object sender, RoutedEventArgs e)
    {
        if (_suppress || _host == null) return;
        if (sender is not CheckBox box || box.DataContext is not EntryVm vm) return;
        vm.Entry.ShowInMiddleMenu = box.IsChecked == true;
        _host.SaveSettings();
    }


    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var entry = new QEntry { Key = "new", Comment = "" };
        CurrentList().Add(entry);
        _host.SaveSettings();
        _suppress = true;
        EntryList.ItemsSource = null;
        var items = CurrentList().Select(x => new EntryVm(x, _tab != "run", _tab=="pinned")).ToList();
        EntryList.ItemsSource = items;
        EntryList.SelectedIndex = items.Count - 1;
        _suppress = false;
        BindEditor(entry);
        KeyBox.Focus();
        KeyBox.SelectAll();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || EntryList.SelectedItem is not EntryVm vm) return;
        if (vm.Entry.Key == "default")
        {
            _host.Notify(Loc.T("默认搜索不能删除，只能修改"));
            return;
        }
        CurrentList().Remove(vm.Entry);
        _host.SaveSettings();
        LoadEntries();
        _host.Notify(Loc.T("已删除"));
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        _editing.Key = KeyBox.Text.Trim();
        _editing.Value = ValueBox.Text.Trim();
        _editing.Comment = CommentBox.Text.Trim();
        _editing.IconPath = IconBox.Text.Trim();
        _editing.Arguments = ArgumentsBox.Text.Trim();
        _editing.RunAsAdmin = AdminBox.IsChecked == true;
        _host.SaveSettings();
        // 同步刷新左侧列表的显示（保持当前选择与编辑焦点）
        EntryList.Items.Refresh();
        UpdateIconPreview(_editing);

        /* 新建网址：用户往往只粘贴链接，不想再手动写标题。
         * 停手 700ms 后抓一次网页 <title>，只填空备注，绝不覆盖用户已写的备注。 */
        if (_tab == "web" && string.IsNullOrWhiteSpace(_editing.Comment))
        {
            _titleToken++;
            _titleTimer.Stop();
            _titleTimer.Start();
        }
    }

    private static bool IsWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task FillWebTitleAsync()
    {
        if (_host == null || _tab != "web" || _editing == null) return;
        var entry = _editing;
        string url = entry.Value?.Trim() ?? "";
        if (!IsWebUrl(url) || !string.IsNullOrWhiteSpace(entry.Comment)) return;

        int token = ++_titleToken;
        string? title = null;
        try
        {
            string html = await TitleHttp.GetStringAsync(url).ConfigureAwait(false);
            var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success) title = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(title))
        {
            try { title = new Uri(url).Host; } catch { title = null; }
        }
        if (string.IsNullOrWhiteSpace(title)) return;

        await Dispatcher.InvokeAsync(() =>
        {
            // 用户可能已经切走，或自己补了备注；两种情况下都不覆盖。
            if (token != _titleToken || !ReferenceEquals(_editing, entry)
                || !string.IsNullOrWhiteSpace(entry.Comment)) return;
            entry.Comment = title!;
            _suppress = true;
            CommentBox.Text = title!;
            _suppress = false;
            _host.SaveSettings();
            EntryList.Items.Refresh();
        });
    }

    private void OnFlagsChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        _editing.RunAsAdmin = AdminBox.IsChecked == true;
        _editing.Arguments = ArgumentsBox.Text.Trim();
        _host.SaveSettings();
    }

    private void OnAppearanceChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Qbar.ListCount = (int)ListCountSlider.Value;
        _host.Settings.Qbar.RecentCount = (int)RecentCountSlider.Value;
        UpdateRecentCountLabel();
        _host.Settings.Qbar.TextFontSize = FontSizeSlider.Value;
        _host.Settings.Qbar.ShowIcons = ShowIconsBox.IsChecked == true;
        _host.Settings.Qbar.SwitchToEnglishOnOpen = ImeOnOpenBox.IsChecked == true;
        _host.Settings.Qbar.SwitchBackOnEngine = ImeBackBox.IsChecked == true;
        UpdateFontPreview();
        _host.SaveSettings();
    }

    /// <summary>
    /// 「最近打开」条数的实时读数。
    ///
    /// 拖到 0 时必须明确写"不显示" —— 否则用户只会看到滑杆贴最左，
    /// 以为是"少了一条"，还在找为什么面板里没有最近项。
    /// </summary>
    private void UpdateRecentCountLabel()
    {
        if (RecentCountValue == null) return;
        int count = (int)RecentCountSlider.Value;
        RecentCountValue.Text = count <= 0 ? Loc.T("不显示") : Loc.F("显示 {0} 条", count);
    }

    /// <summary>字号预览：让用户直接看到调大字号后的实际效果。</summary>
    private void UpdateFontPreview()
    {
        if (FontPreview == null) return;
        double size = FontSizeSlider.Value;
        FontPreview.FontSize = size;
        // 高度按真实字形高度算，和 Qbar 输入框保持一致的行为
        FontPreview.MinHeight = Core.Text.TextMetrics.MeasureHeight(size, Loc.T("预览 Preview 文字测试 qgp"));
    }

    /* ── 搜索联想 ─────────────────────────────────────────────
       这里的写法和上面的外观卡完全一致：控件事件直接改 _host.Settings.Qbar，
       再交给 AppHost 去防抖落盘。没有额外的 ViewModel，也就不存在“界面改了
       但设置没存”的两份真相。 */

    /// <summary>启用开关和三个滑杆共用：值都直接映射到 QbarOptions 的对应字段。</summary>
    private void OnSuggestOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        QbarOptions q = _host.Settings.Qbar;
        q.SuggestEnabled = SuggestEnabledBox.IsChecked == true;
        q.SuggestMax = (int)Math.Round(SuggestMaxSlider.Value);
        q.SuggestTimeoutMs = (int)Math.Round(SuggestTimeoutSlider.Value);
        q.SuggestCacheMinutes = (int)Math.Round(SuggestCacheSlider.Value);
        UpdateSuggestLabels();
        _host.SaveSettings();
    }

    /// <summary>
    /// 联想引擎下拉。
    ///
    /// 存的是 "auto"/"baidu"/"google"/"bing" 这种稳定字符串，不是界面索引：
    /// 以后往下拉里插一项，老配置也不会突然指向另一个引擎。
    /// </summary>
    private void OnSuggestEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        string raw = (SuggestEngineBox.SelectedItem as ComboBoxItem)?.Tag as string ?? SuggestionEngines.Auto;
        _host.Settings.Qbar.SuggestEngine = SuggestionEngines.Normalize(raw);
        _host.SaveSettings();
    }

    /* ── 语义层 ─────────────────────────────────────────────
       开关的写法和上面两张卡一致：控件事件直接改 QbarOptions.Search，再交给 AppHost 防抖落盘。
       过滤器 / 优先级规则 / 命令的逐条编辑界面留到后续版本，这里先保证"能不能用、窗口多大、
       要不要看调试面板"三个最影响排障的点可调。 */

    private void OnSemanticOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        QbarSearchOptions s = _host.Settings.Qbar.Search;
        s.RulesEnabled = SemanticRulesBox.IsChecked == true;
        s.PreferEverything = true;      // 后端固定 Everything：界面上已经没有"换回本地快照"的入口
        s.EverythingOnly = true;
        s.BuiltinRulesEnabled = SemanticBuiltinRulesBox.IsChecked == true;
        s.PinyinEnabled = SemanticPinyinBox.IsChecked == true;
        s.FacetsEnabled = SemanticFacetsBox.IsChecked == true;
        s.HistoryEnabled = SemanticHistoryBox.IsChecked == true;
        s.DebugPanel = SemanticDebugBox.IsChecked == true;
        s.MaxCandidates = (int)Math.Round(SemanticWindowSlider.Value);
        s.DebounceMs = (int)Math.Round(SemanticDebounceSlider.Value);
        UpdateSemanticLabels();
        _host.SaveSettings();
    }

    private void UpdateSemanticLabels()
    {
        if (SemanticWindowValue == null) return;
        SemanticWindowValue.Text = ((int)Math.Round(SemanticWindowSlider.Value)) + " 条";
        SemanticDebounceValue.Text = ((int)Math.Round(SemanticDebounceSlider.Value)) + " ms";
    }

    private void OnRefreshPinyin(object sender, RoutedEventArgs e)
    {
        QbarPinyinIndex.Invalidate();
        QbarPinyinIndex.Start();
        _host?.Notify(Loc.T("拼音索引正在后台重建"));
    }

    private void OnShowSemanticInfo(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        QbarOptions q = _host.Settings.Qbar;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Loc.T("过滤器（输入关键字按空格就生效，也认 doc: 这种老写法）"));
        foreach (QbarFilter filter in QbarFilterCatalog.Create(q.Filters).All)
            sb.AppendLine("  " + filter.Keyword + ":  " + filter.Name);
        sb.AppendLine();
        sb.AppendLine(Loc.T("命令（首词命中即执行）"));
        foreach (QbarCommand command in QbarCommandStore.Effective(q.Commands))
            sb.AppendLine("  " + command.Keyword + "  " + command.Title);
        sb.AppendLine();
        sb.AppendLine(Loc.F("优先级规则：生效 {0} 条（内置包{1}）",
            QbarPriorityResolver.Create(q.Search.RulesEnabled, q.Search.BuiltinRulesEnabled, q.Priority).Count,
            q.Search.BuiltinRulesEnabled ? Loc.T("已启用") : Loc.T("已关闭")));
        foreach (QbarPriorityRule rule in q.Priority)
            sb.AppendLine("  " + rule.Level + "  " + (rule.FolderPath ?? "") + "  " + (rule.NameRegex ?? ""));
        AppDialog.Info(this, sb.ToString(), Loc.T("Qbar 语义层"));
    }

    /// <summary>下拉框回显：认不出的值统一落到“跟随当前搜索引擎”。</summary>
    private void SelectSuggestEngine(string? configured)
    {
        if (SuggestEngineBox == null) return;
        string engine = SuggestionEngines.Normalize(configured);
        SuggestEngineBox.SelectedIndex = engine switch
        {
            SuggestionEngines.Baidu => 1,
            SuggestionEngines.Google => 2,
            SuggestionEngines.Bing => 3,
            _ => 0,
        };
    }

    /// <summary>
    /// 滑杆读数。
    ///
    /// 0 分钟必须明确写“不缓存”：否则用户看到 0 只会以为坏了，
    /// 或者以为“0 分钟”等于默认值。
    /// </summary>
    private void UpdateSuggestLabels()
    {
        if (SuggestMaxValue == null || SuggestTimeoutValue == null || SuggestCacheValue == null) return;
        SuggestMaxValue.Text = Loc.F("显示 {0} 条", (int)Math.Round(SuggestMaxSlider.Value));
        SuggestTimeoutValue.Text = Loc.F("{0} 毫秒", (int)Math.Round(SuggestTimeoutSlider.Value));
        int minutes = (int)Math.Round(SuggestCacheSlider.Value);
        SuggestCacheValue.Text = minutes <= 0 ? Loc.T("不缓存") : Loc.F("{0} 分钟", minutes);
    }

    /// <summary>
    /// 外观恢复默认：**只重置外观字段**。
    ///
    /// 以前这里整块 `_host.Settings.Qbar = new QbarOptions()` —— 顺手把过滤器、
    /// 优先级规则、命令、固定项目、语义层开关全清了。用户点「恢复默认外观」
    /// 只是想调回颜色字号，结果文件搜索的整套配置没了。
    /// </summary>
    private void OnResetAppearance(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        QbarOptions q = _host.Settings.Qbar;
        QbarOptions def = new();

        q.PanelColor = def.PanelColor;
        q.PanelOpacity = def.PanelOpacity;
        q.Width = def.Width;
        q.TextFontSize = def.TextFontSize;
        q.ListCount = def.ListCount;
        q.LineHeight = def.LineHeight;
        q.AccentColor = def.AccentColor;
        q.ShowIcons = def.ShowIcons;
        q.RecentCount = def.RecentCount;
        q.SwitchToEnglishOnOpen = def.SwitchToEnglishOnOpen;
        q.SwitchBackOnEngine = def.SwitchBackOnEngine;

        _host.SaveSettings();
        Refresh();
        _host.Notify(Loc.T("Qbar 外观已恢复默认"));
    }

    /// <summary>面板位置恢复默认：清掉"拖动过"的标记，下次唤出回到上方居中。</summary>
    private void OnResetPosition(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        QbarOptions q = _host.Settings.Qbar;
        q.CustomPosition = false;
        q.PositionX = 0.5;
        q.PositionY = 0.3;
        _host.SaveSettings();
        UpdatePositionLabel();
        _host.Notify(Loc.T("Qbar 位置已恢复默认"));
    }

    /// <summary>位置状态文案：默认摆位 vs 用户自己拖过的位置。</summary>
    private void UpdatePositionLabel()
    {
        if (PositionValue == null || _host == null) return;
        QbarOptions q = _host.Settings.Qbar;
        PositionValue.Text = q.CustomPosition
            ? Loc.F("已自定义（水平 {0}% · 垂直 {1}%）",
                Math.Round(q.PositionX * 100), Math.Round(q.PositionY * 100))
            : Loc.T("默认（工作区上方居中）");
    }

    /// <summary>
    /// 文件搜索的状态行 —— 现在报的是 Everything 的状态，不是本地快照。
    ///
    /// 语义层把检索整块交给了 Everything（m02881：只用 Everything），所以这一页唯一
    /// 有意义的"索引状态"就是 Everything 有没有在跑、索引库加载完没有。本地快照只在
    /// 自检和基准对照里用，这里**不再偷偷 Start() 去扫盘** —— 一边说只用 Everything、
    /// 一边在后台扫 40 万个文件，是最难查的那种"看起来在工作"。
    ///
    /// 没就绪时每 900ms 刷一次（等 Everything 启动/加载索引），就绪后立刻停表。
    /// </summary>
    private void RefreshIndexStatus()
    {
        if (IndexState == null) return;

        ApplyIndexStatus();
        if (EverythingReady) { _indexTimer?.Stop(); return; }

        /* 没就绪就顺手把内置客户端拉起来（后台线程、内部有并发保护）。
         * 用户打开这一页多半就是"文件搜索不出东西"，让他点第二次才动是多余的。 */
        EverythingBootstrap.WarmupAsync();

        if (_indexTimer == null)
        {
            _indexTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _indexTimer.Tick += OnIndexTimerTick;
        }
        if (!_indexTimer.IsEnabled) _indexTimer.Start();
    }

    /// <summary>Everything 三件套：DLL 能加载、客户端在跑、索引库已加载。</summary>
    private static bool EverythingReady =>
        Core.Qbar.Search.EverythingApi.IsAvailable
        && Core.Qbar.Search.EverythingApi.IsClientRunning
        && Core.Qbar.Search.EverythingApi.IsDbLoaded;

    private void OnIndexTimerTick(object? sender, EventArgs e)
    {
        ApplyIndexStatus();
        if (EverythingReady) _indexTimer?.Stop();
    }

    /// <summary>
    /// 四种状态：Everything 可用 / 没在运行 / 索引还没加载 / SDK 加载不了。
    /// 都只是"暂时搜不到文件"，别画成红色故障；每种状态都给一句下一步怎么办。
    /// </summary>
    private void ApplyIndexStatus()
    {
        if (IndexState == null) return;

        /* 本地快照的信息降级成一行旁注：它还在建也没关系，反正不参与搜索，
         * 但自检和基准对照要看它，所以数字留着。 */
        int files = Core.Qbar.NativeSearchIndex.FileCount;
        int dirs = Core.Qbar.NativeSearchIndex.DirCount;
        IndexInfo.Text = (files > 0 || dirs > 0)
            ? Loc.T("本地快照（只给自检与基准对照用）：") + files.ToString("N0") + " " + Loc.T("个文件")
              + " · " + dirs.ToString("N0") + " " + Loc.T("个文件夹")
            : "";

        if (!Core.Qbar.Search.EverythingApi.IsAvailable)
        {
            IndexState.Text = Loc.T("内置 Everything 没能就绪");
            IndexDetail.Text = (Core.Qbar.Search.EverythingApi.LastErrorText.Length > 0
                                   ? Core.Qbar.Search.EverythingApi.LastErrorText + "\n"
                                   : "")
                               + Loc.T("客户端与 SDK 都内置在本软件里，正常情况下第一次用到时会自动释放到数据目录。这里报错时点「刷新状态」重试一次；仍然不行就把 Everything64.dll 手工放到程序目录。");
            IndexDot.Fill = (Brush)FindResource("Brush.Warning");
            SetStartButton(Loc.T("启动 Everything"), true);
            return;
        }

        if (!Core.Qbar.Search.EverythingApi.IsClientRunning)
        {
            IndexState.Text = Loc.T("内置 Everything 还没启动");
            IndexDetail.Text = Loc.T("点「启动 Everything」当场拉起内置客户端（不弹窗口）。第一次启动要建索引，整机几十万文件通常一两分钟，之后就一直可用。");
            IndexDot.Fill = (Brush)FindResource("Brush.Warning");
            SetStartButton(Loc.T("启动 Everything"), true);
            return;
        }

        if (!Core.Qbar.Search.EverythingApi.IsDbLoaded)
        {
            IndexState.Text = Loc.T("Everything 正在建索引");
            IndexDetail.Text = Loc.T("索引还没加载完，文件搜索先给不出结果。这一行会自己变；也可以点「刷新状态」立刻看一次。");
            IndexDot.Fill = (Brush)FindResource("Brush.Warning");
            SetStartButton(Loc.T("索引加载中…"), false);
            return;
        }

        IndexState.Text = Loc.T("可用");
        IndexDetail.Text = Core.Qbar.Search.EverythingApi.StatusText
                           + " · " + Loc.T("SDK：")
                           + Core.Qbar.Search.EverythingApi.SourceText
                           + (Core.Qbar.Search.EverythingApi.IsAdmin
                               ? ""
                               : "\n" + Loc.T("Everything 不是以管理员身份运行：部分系统目录可能不在它的索引里。用管理员身份运行本软件即可。"));
        IndexDot.Fill = (Brush)FindResource("Brush.Success");
        SetStartButton(Loc.T("Everything 已启动"), false);
    }

    /// <summary>
    /// 启动按钮跟着状态变：已经在跑还摆一个「启动 Everything」，用户会以为上次没点成功，
    /// 点下去也没反应（内部会直接判定已经就绪）。就绪时改成「Everything 已启动」并置灰。
    /// </summary>
    private void SetStartButton(string text, bool enabled)
    {
        if (OpenEverythingButton == null) return;
        OpenEverythingButton.Content = text;
        OpenEverythingButton.IsEnabled = enabled;
        OpenEverythingButton.ToolTip = enabled
            ? Loc.T("启动内置的 Everything 客户端（不弹窗口）。还没装过 Everything 的机器也能直接搜文件。")
            : Loc.T("Everything 已经在运行，不需要再启动。");
    }

    private void OnRefreshIndex(object sender, RoutedEventArgs e) => RefreshIndexStatus();

    /// <summary>
    /// 「启动 Everything」：先起**内置**那份（用户什么都没装也能用），
    /// 内置起不来（被杀软拦、数据目录不可写）再回落到系统里已装的 Everything。
    ///
    /// 不碰 Everything 的全局配置、不读写它的索引库（红线 1）：内置那份跑在
    /// 数据目录的 portable 模式里，和用户自己装的 Everything 互不影响。
    /// </summary>
    private void OnOpenEverything(object sender, RoutedEventArgs e)
    {
        _host?.Notify(Loc.T("正在启动内置 Everything…"));
        Task.Run(() =>
        {
            bool ok = EverythingBootstrap.EnsureClient(4000);
            if (!ok)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "Everything.exe",
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    Core.CoreLog.Warn("启动 Everything 失败：" + ex.Message);
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _host?.Notify(ok
                    ? Loc.T("内置 Everything 已启动；索引建好后文件搜索就能用（这一页会自动刷新）。")
                    : (EverythingBootstrap.LastDetail.Length > 0
                        ? EverythingBootstrap.LastDetail
                        : Loc.T("还没能启动 Everything，稍后再点一次或手动打开它。")));
                RefreshIndexStatus();
            }));
        });
    }

    /* ── 文件搜：过滤器与优先级 ─────────────────────────────
       这两张列表和左边的条目列表用的是两套数据（Qbar.Filters / Qbar.Priority），
       但编辑手感刻意保持一致：控件改动直接写回设置，没有「保存」按钮。
       唯一的例外是优先级正则 —— 编译不过就不落盘。存进去一条编译不过的正则，
       求解器只会当它不存在，用户却以为规则生效了，那种静默最难查。 */

    private QbarFilter? _filterEditing;
    private bool _filterSuppress;
    private QbarPriorityRule? _ruleEditing;
    private bool _ruleSuppress;

    /// <summary>
    /// 切"整页"版式时的显隐：文件搜索与附加菜单都是整页 —— 右边那栏整块让位、左边占满两列。
    ///
    /// 为什么必须同时做这两件事：只把面板显示出来、不收右栏也不撑列宽，
    /// 整页就会挤在左半栏里，右边还杵着一个条目编辑框 —— 看起来像"一个弹窗浮在设置页中间"。
    /// </summary>
    private void ShowFilesTab(bool full)
    {
        bool files = _tab == "files";
        bool middle = _tab == "middle";
        FileSearchPanel.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        MiddleMenuPanel.Visibility = middle ? Visibility.Visible : Visibility.Collapsed;
        if (files) SyncTocToScroll();

        Visibility entryUi = full ? Visibility.Collapsed : Visibility.Visible;
        EntriesFilter.Visibility = entryUi;
        EntryList.Visibility = entryUi;
        EntriesActions.Visibility = entryUi;
        RightScroll.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(EntriesCard, full ? 2 : 1);
    }

    /* ── 文件搜索的页内目录 ─────────────────────────────────────────────
       顶上一排水平按钮，点哪个滚到哪张卡。为什么要做：这一页五张卡连起来
       要滚三屏多，用户从「优先级」滚到「命令」时没有任何位置感；而且
       「哪张卡管什么」在长滚动里最容易忘。目录固定不滚走，滚动时反过来
       点亮当前卡 —— 这就是「目录」和「锚点」的区别。 */

    private void BuildFileToc()
    {
        if (FileToc == null || FileToc.Children.Count > 0) return;
        AddTocChip(Loc.T("索引与后端"), CardBackend);
        AddTocChip(Loc.T("过滤器"), CardFilters);
        AddTocChip(Loc.T("优先级"), CardPriority);
        AddTocChip(Loc.T("文件对话框浮层"), FileDialogBarCard);
        AddTocChip(Loc.T("命令"), CardCommands);
        AddTocChip(Loc.T("语法与调试"), CardSyntax);
        AddTocChip(Loc.T("搜索前建议"), CardSuggestions);
    }

    private void AddTocChip(string text, FrameworkElement card)
    {
        var chip = new Button
        {
            Content = text,
            Style = ResStyle("Button.Ghost"),
            Padding = new Thickness(11, 4, 11, 4),
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 12.5,
            Focusable = false,
            Tag = card,
        };
        chip.Click += (_, _) => ScrollToCard(card);
        FileToc.Children.Add(chip);
        _toc.Add((card, chip));
    }

    /// <summary>滚到某张卡（卡片顶部对齐目录下方）。</summary>
    private void ScrollToCard(FrameworkElement card)
    {
        try
        {
            FileScroll.UpdateLayout();
            double y = card.TransformToAncestor(FileScrollContent).Transform(new Point(0, 0)).Y;
            FileScroll.ScrollToVerticalOffset(Math.Max(0, y - 4));
            SetActiveChip(card);
        }
        catch (InvalidOperationException)
        {
            // 卡片还没进可视树（标签页刚切过来），下一次 ScrollChanged 会补上。
        }
    }

    private void OnFileScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) < 0.01 && e.ExtentHeightChange == 0) return;
        SyncTocToScroll();
    }

    /// <summary>按当前滚动位置点亮「最上面那张还露着的卡」。</summary>
    private void SyncTocToScroll()
    {
        if (_toc.Count == 0 || _tocSyncing) return;
        try
        {
            double top = FileScroll.VerticalOffset + 12;
            FrameworkElement? active = _toc[0].Card;
            foreach ((FrameworkElement card, _) in _toc)
            {
                double y = card.TransformToAncestor(FileScrollContent).Transform(new Point(0, 0)).Y;
                if (y <= top) active = card;
            }
            SetActiveChip(active);
        }
        catch (InvalidOperationException) { }
    }

    private void SetActiveChip(FrameworkElement? card)
    {
        _tocSyncing = true;
        foreach ((FrameworkElement c, Button chip) in _toc)
        {
            if (ReferenceEquals(c, card))
            {
                chip.SetResourceReference(Control.BackgroundProperty, "Brush.Selected");
                chip.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
            }
            else
            {
                chip.ClearValue(Control.BackgroundProperty);
                chip.ClearValue(Control.ForegroundProperty);
            }
        }
        _tocSyncing = false;
    }

    /* 过滤器列表分两段：内置 6 条（永远在）+ 用户自定义。
       内置项可以覆盖、可以停用、可以改扩展名，但不给删 —— 删掉 doc: 这种
       肌肉记忆比不支持更难受，所以「删除」对内置项等于「恢复出厂」。 */

    private void RefreshFilterList(bool rebindEditor)
    {
        if (_host == null) return;
        QbarOptions q = _host.Settings.Qbar;
        var overridden = new HashSet<string>(q.Filters.Select(f => f.Id ?? ""), StringComparer.OrdinalIgnoreCase);
        string keep = _filterEditing?.Id ?? "";

        _filterSuppress = true;
        FilterRows.Children.Clear();

        List<QbarFilter> all = QbarFilterCatalog.Create(q.Filters).All.Select(f => f.Clone()).ToList();
        List<QbarFilter> builtins = all.Where(IsBuiltinFilter).ToList();
        List<QbarFilter> custom = all.Where(f => !IsBuiltinFilter(f)).ToList();

        AddGroupHeader(FilterRows, Loc.T("文件类型"));
        foreach (QbarFilter f in builtins)
        {
            FilterRows.Children.Add(BuildFilterRow(f, true, overridden.Contains(f.Id),
                string.Equals(f.Id, keep, StringComparison.OrdinalIgnoreCase)));
        }

        AddGroupHeader(FilterRows, Loc.T("自定义过滤器"));
        if (custom.Count == 0)
            FilterRows.Children.Add(BuildHintRow(Loc.T("还没有自定义过滤器：点右边「新增」，给它一个关键字和一张扩展名表。")));
        foreach (QbarFilter f in custom)
        {
            FilterRows.Children.Add(BuildFilterRow(f, false, false,
                string.Equals(f.Id, keep, StringComparison.OrdinalIgnoreCase)));
        }
        _filterSuppress = false;

        if (!rebindEditor) return;
        QbarFilter? pick = all.FirstOrDefault(f => string.Equals(f.Id, keep, StringComparison.OrdinalIgnoreCase))
                           ?? all.FirstOrDefault();
        if (pick != null) BindFilter(pick, IsBuiltinFilter(pick));
        else ClearFilterEditor();
    }

    private static bool IsBuiltinFilter(QbarFilter f) =>
        (f.Id ?? "").StartsWith("builtin.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 一行一个过滤器：名称 / 关键字 chip / 扩展名 / 搜索范围 / 开关。
    /// 开关直接落盘（和左边条目列表一样没有「保存」按钮），选中行才回填下面的编辑区。
    /// </summary>
    private Border BuildFilterRow(QbarFilter f, bool builtin, bool overridden, bool selected)
    {
        var grid = new Grid { Margin = new Thickness(12, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock
        {
            Text = FilterGlyph(f.Keyword),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("Brush.TextSecondary"),
        });
        TextBlock title = new()
        {
            Text = string.IsNullOrWhiteSpace(f.Name) ? f.Keyword : f.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 128,
            Opacity = f.Enabled ? 1 : 0.45,
            // 代码自建的 TextBlock 不会继承窗口的 Foreground 以外的任何一套配色，
            // 而默认前景色是黑的 —— 深色主题下"视频/图片"这些名字就成了黑底黑字。
            Foreground = Res("Brush.TextPrimary"),
        };
        name.Children.Add(title);
        if (builtin && overridden) name.ToolTip = Loc.T("这条内置过滤器被改过：「恢复内置」可以还原。");
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var chip = new Border
        {
            Background = Res("Brush.AccentSoft"),
            BorderBrush = Res("Brush.AccentSoftBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = f.Enabled ? 1 : 0.5,
        };
        /* 只显示关键字本身。以前拼了个尾冒号（"wj:"），用户改完关键字看到的就是
         * "wj:" —— 冒号是输入时用的分隔符，不是关键字的一部分，摆在这里纯属误导。 */
        chip.Child = new TextBlock { Text = f.Keyword, FontSize = 11.5, Foreground = Res("Brush.Accent") };
        Grid.SetColumn(chip, 1);
        grid.Children.Add(chip);

        /* 目录过滤器和 file: 都没扩展名：直接写人话，别让「（未设置）」看着像配错了。 */
        string kinds = !string.IsNullOrWhiteSpace(f.Extensions)
            ? f.Extensions.Replace(';', ' ').Trim()
            : f.SearchForFolders
                ? (f.SearchForFiles ? Loc.T("文件与目录") : Loc.T("目录"))
                : Loc.T("所有文件");
        var typeText = new TextBlock
        {
            Text = kinds,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = ResStyle("Text.Tertiary"),
        };
        Grid.SetColumn(typeText, 2);
        grid.Children.Add(typeText);

        string scope = string.IsNullOrWhiteSpace(f.SearchInFolders) ? Loc.T("全部位置") : f.SearchInFolders;
        var scopeText = new TextBlock
        {
            Text = scope,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = scope,
            Style = ResStyle("Text.Tertiary"),
        };
        Grid.SetColumn(scopeText, 3);
        grid.Children.Add(scopeText);

        var toggle = new CheckBox
        {
            Style = TryFindResource("ToggleSwitch") as Style,
            IsChecked = f.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = Loc.T("启用 / 停用这条过滤器"),
        };
        toggle.Click += (_, e) =>
        {
            e.Handled = true;
            f.Enabled = toggle.IsChecked == true;
            UpsertFilter(f);
            RefreshFilterList(rebindEditor: false);
            _host?.Notify(f.Enabled ? Loc.F("已启用 {0}", f.Keyword) : Loc.F("已停用 {0}", f.Keyword));
        };
        Grid.SetColumn(toggle, 4);
        grid.Children.Add(toggle);

        var row = new Border
        {
            Height = 44,
            CornerRadius = new CornerRadius(6),
            Child = grid,
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "Brush.Selected");
        row.MouseEnter += (_, _) => { if (!selected) row.SetResourceReference(Border.BackgroundProperty, "Brush.Hover"); };
        row.MouseLeave += (_, _) => { if (!selected) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (InsideToggle(e.OriginalSource)) return;
            OnFilterRowClick(f, builtin);
        };
        return row;
    }

    private void OnFilterRowClick(QbarFilter f, bool builtin)
    {
        if (_host == null) return;
        QbarFilter live = _host.Settings.Qbar.Filters
            .FirstOrDefault(x => string.Equals(x.Id, f.Id, StringComparison.OrdinalIgnoreCase))?.Clone() ?? f;
        BindFilter(live, builtin);
        RefreshFilterList(rebindEditor: false);
    }

    private static string FilterGlyph(string? keyword) => (keyword ?? "").ToLowerInvariant() switch
    {
        "doc" => "\uE8A5",
        "pic" => "\uEB9F",
        "video" => "\uE714",
        "audio" => "\uE8D6",
        "folder" => "\uE8B7",
        "file" => "\uE7C3",
        _ => "\uE71C",
    };

    private void AddGroupHeader(Panel host, string text)
    {
        host.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(13, 12, 12, 5),
            Foreground = Res("Brush.TextSecondary"),
        });
    }

    private TextBlock BuildHintRow(string text) => new()
    {
        Text = text,
        Margin = new Thickness(13, 4, 12, 12),
        TextWrapping = TextWrapping.Wrap,
        Style = ResStyle("Text.Tertiary"),
    };

    /// <summary>开关内部也会冒泡到行的 MouseLeftButtonUp，靠它把「点开关」和「选中行」分开。</summary>
    private static bool InsideToggle(object? source)
    {
        var d = source as DependencyObject;
        while (d != null)
        {
            if (d is CheckBox) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Transparent;

    private Style? ResStyle(string key) => TryFindResource(key) as Style;

    private void BindFilter(QbarFilter filter, bool builtin)
    {
        _filterEditing = filter;
        _filterSuppress = true;
        FilterNameBox.Text = filter.Name;
        FilterKeywordBox.Text = filter.Keyword;
        FilterExtBox.Text = filter.Extensions;
        FilterExtraBox.Text = filter.ExtraQuery;
        FilterFolderBox.Text = filter.SearchInFolders;
        FilterDateBox.Text = filter.DateModifiedRange;
        FilterEnabledBox.IsChecked = filter.Enabled;
        FilterFilesBox.IsChecked = filter.SearchForFiles;
        FilterFoldersBox.IsChecked = filter.SearchForFolders;
        _filterSuppress = false;
        FilterHint.Text = builtin
            ? Loc.T("内置过滤器：扩展名表和关键字都能改，点「恢复内置」还原。")
            : Loc.T("自定义过滤器：关键字就是输入框里要打的那几个字母，建议只用字母数字。");
    }

    private void ClearFilterEditor()
    {
        _filterEditing = null;
        _filterSuppress = true;
        FilterNameBox.Text = "";
        FilterKeywordBox.Text = "";
        FilterExtBox.Text = "";
        FilterExtraBox.Text = "";
        FilterFolderBox.Text = "";
        FilterDateBox.Text = "";
        FilterEnabledBox.IsChecked = false;
        FilterFilesBox.IsChecked = true;
        FilterFoldersBox.IsChecked = false;
        _filterSuppress = false;
        FilterHint.Text = Loc.T("点「新增」建一个自己的过滤器。");
    }

    /// <summary>关键字归一：去掉冒号、空白和全角字符，剩下的必须能当「关键字:」用。</summary>
    private static string NormalizeKeyword(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(8);
        foreach (char c in text.Trim())
        {
            char ch = c;
            if (ch == '\uFF1A') ch = ':';          // 全角冒号
            if (ch == ':' || char.IsWhiteSpace(ch)) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private void OnFilterFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _filterSuppress || _filterEditing == null) return;

        string keyword = NormalizeKeyword(FilterKeywordBox.Text);
        if (keyword.Length == 0)
        {
            FilterHint.Text = Loc.T("关键字不能为空：它决定输入「关键字:」时能不能命中这条过滤器。");
            return;
        }
        if (!Regex.IsMatch(keyword, "^[0-9A-Za-z_\u4e00-\u9fff-]+$"))
        {
            FilterHint.Text = Loc.T("关键字只能用字母、数字、下划线、短横线或汉字，不能带空格和冒号。");
            return;
        }

        QbarFilter f = _filterEditing;

        /* 关键字是索引键，两条过滤器撞同一个词时后写的会顶掉前一条 —— 界面上看是
         * "改了也没变"。撞了就当场说清楚，别等用户发现 doc 变成了别的类型。 */
        QbarFilter? clash = QbarFilterCatalog.Create(_host.Settings.Qbar.Filters).All.FirstOrDefault(x =>
            !string.Equals(x.Id, f.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
        if (clash != null)
        {
            FilterHint.Text = Loc.F("关键字「{0}」已经被「{1}」占了，换一个。", keyword,
                string.IsNullOrEmpty(clash.Name) ? clash.Keyword : clash.Name);
            return;
        }

        f.Keyword = keyword;
        f.Name = FilterNameBox.Text.Trim();
        f.Extensions = NormalizeExtensions(FilterExtBox.Text);
        f.ExtraQuery = FilterExtraBox.Text.Trim();
        f.SearchInFolders = FilterFolderBox.Text.Trim();
        f.DateModifiedRange = FilterDateBox.Text.Trim();
        UpsertFilter(f);

        /* 列表只换个样子，不回填编辑器：一回填光标就跳到行首，边打边跳没法用。 */
        RefreshFilterList(rebindEditor: false);
        FilterHint.Text = Loc.F("已保存：输入「{0}」再加冒号就带上这条过滤器。", keyword);
    }

    private void OnFilterFlagChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _filterSuppress || _filterEditing == null) return;
        QbarFilter f = _filterEditing;
        f.Enabled = FilterEnabledBox.IsChecked == true;
        f.SearchForFiles = FilterFilesBox.IsChecked == true;
        f.SearchForFolders = FilterFoldersBox.IsChecked == true;
        UpsertFilter(f);
        RefreshFilterList(rebindEditor: false);
    }

    /// <summary>扩展名表归一：去掉点、转小写、分号串（和存储格式一致，改完立刻能看）。</summary>
    private static string NormalizeExtensions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (string part in text.Split(new[] { ';', ',', ' ', '\t', '\r', '\n', '；', '，' },
                                           StringSplitOptions.RemoveEmptyEntries))
        {
            string ext = part.Trim().TrimStart('*', '.').ToLowerInvariant();
            if (ext.Length == 0 || !seen.Add(ext)) continue;
            list.Add(ext);
        }
        return string.Join(';', list);
    }

    private void UpsertFilter(QbarFilter filter)
    {
        if (_host == null) return;
        List<QbarFilter> list = _host.Settings.Qbar.Filters;
        if (string.IsNullOrEmpty(filter.Id))
            filter.Id = "user." + Guid.NewGuid().ToString("N")[..8];

        QbarFilter copy = filter.Clone();
        int at = list.FindIndex(x => string.Equals(x.Id, copy.Id, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) list[at] = copy; else list.Add(copy);
        _host.SaveSettings();
    }

    private void OnFilterAdd(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        QbarOptions q = _host.Settings.Qbar;
        var used = new HashSet<string>(QbarFilterCatalog.Create(q.Filters).All.Select(f => f.Keyword),
                                       StringComparer.OrdinalIgnoreCase);
        int n = 1;
        while (used.Contains("f" + n)) n++;

        var filter = new QbarFilter
        {
            Id = "user." + Guid.NewGuid().ToString("N")[..8],
            Name = Loc.T("新过滤器"),
            Keyword = "f" + n,
            Enabled = true,
            SearchForFiles = true,
        };
        q.Filters.Add(filter);
        _host.SaveSettings();
        _filterEditing = filter.Clone();
        RefreshFilterList(rebindEditor: true);
        FilterNameBox.Focus();
        FilterNameBox.SelectAll();
    }

    private void OnFilterDelete(object sender, RoutedEventArgs e)
    {
        if (_host == null || _filterEditing == null) return;
        QbarOptions q = _host.Settings.Qbar;
        QbarFilter f = _filterEditing;
        bool builtin = f.Id.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase);
        int removed = q.Filters.RemoveAll(x => string.Equals(x.Id, f.Id, StringComparison.OrdinalIgnoreCase));

        if (builtin)
        {
            _host.SaveSettings();
            _filterEditing = null;
            RefreshFilterList(rebindEditor: true);
            _host.Notify(removed > 0 ? Loc.T("已恢复这条内置过滤器的出厂值") : Loc.T("内置过滤器不能删除：可以停用，或者「恢复内置」"));
            return;
        }

        _host.SaveSettings();
        _filterEditing = null;
        RefreshFilterList(rebindEditor: true);
        _host.Notify(Loc.T("过滤器已删除"));
    }

    private void OnFilterResetBuiltin(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        int removed = _host.Settings.Qbar.Filters
            .RemoveAll(x => (x.Id ?? "").StartsWith("builtin.", StringComparison.OrdinalIgnoreCase));
        _host.SaveSettings();
        _filterEditing = null;
        RefreshFilterList(rebindEditor: true);
        _host.Notify(removed > 0 ? Loc.F("已恢复 {0} 个内置过滤器", removed) : Loc.T("内置过滤器本来就是出厂值"));
    }

    /* 优先级规则：内置规则包照抄 Listary 的默认表（高/正常/低/排除），可以逐条停用，
       但出厂表本身不改 —— 改了它等于改默认搜索行为，跟「回归保护」冲突。
       停用只记 key，不动 QbarSearchOptions 里的语义。 */

    private void RefreshRuleList(bool rebindEditor)
    {
        if (_host == null) return;
        QbarOptions q = _host.Settings.Qbar;
        List<QbarPriorityRule> rules = q.Priority;
        string keep = _ruleEditing?.Id ?? "";

        _ruleSuppress = true;
        RuleRows.Children.Clear();

        AddGroupHeader(RuleRows, Loc.T("高优先"));
        if (!AddUserRules(rules, "high", keep))
            RuleRows.Children.Add(BuildHintRow(Loc.T("还没有高优先规则：加一条，让重要目录里的文件排到最前。")));

        AddGroupHeader(RuleRows, Loc.T("正常"));
        AddBuiltinRules(QbarPriorityLevel.Normal);
        AddUserRules(rules, "normal", keep);

        AddGroupHeader(RuleRows, Loc.T("低优先"));
        RuleRows.Children.Add(BuildHideLowRow(q));
        AddBuiltinRules(QbarPriorityLevel.Low);
        AddUserRules(rules, "low", keep);

        AddGroupHeader(RuleRows, Loc.T("从搜索结果中排除"));
        bool any = AddUserRules(rules, "ignored", keep);
        any |= AddUserRules(rules, "dontindex", keep);
        if (!any)
            RuleRows.Children.Add(BuildHintRow(Loc.T("没有排除任何文件夹：点上方「排除文件夹」挑一个目录，它就不会再出现在结果里。")));
        _ruleSuppress = false;

        int builtinCount = QbarPriorityResolver.Create(true, q.Search.BuiltinRulesEnabled, null,
                                                       q.Search.DisabledBuiltins).Count;
        RuleHint.Text = q.Search.RulesEnabled
            ? Loc.F("内置规则包 {0} 条（系统目录、临时文件靠后放），你自己 {1} 条。改完立刻生效，不用重建索引。",
                    builtinCount, rules.Count)
            : Loc.T("优先级规则现在整体关闭（「语法与调试」里的重排开关没勾），这些规则都不会生效。");

        if (!rebindEditor) return;
        QbarPriorityRule? pick = rules.FirstOrDefault(r => string.Equals(r.Id, keep, StringComparison.OrdinalIgnoreCase))
                                 ?? rules.FirstOrDefault();
        if (pick != null) BindRule(pick);
        else ClearRuleEditor();
    }

    private bool AddUserRules(List<QbarPriorityRule> rules, string level, string keep)
    {
        bool any = false;
        foreach (QbarPriorityRule r in rules.Where(r =>
                     string.Equals(string.IsNullOrWhiteSpace(r.Level) ? "low" : r.Level.ToLowerInvariant(),
                                   level, StringComparison.OrdinalIgnoreCase)))
        {
            any = true;
            string text = !string.IsNullOrWhiteSpace(r.FolderPath) ? r.FolderPath : r.NameRegex;
            string sub = !string.IsNullOrWhiteSpace(r.FolderPath) && !string.IsNullOrWhiteSpace(r.NameRegex)
                ? Loc.F("文件名正则 {0}", r.NameRegex)
                : (string.IsNullOrWhiteSpace(r.FolderPath) ? Loc.T("任意目录") : "");
            RuleRows.Children.Add(BuildRuleRow(
                string.IsNullOrWhiteSpace(r.FolderPath) ? "\uE71C" : "\uE8B7", text, sub, r.Enabled,
                on =>
                {
                    r.Enabled = on;
                    SaveRuleFlag(r);
                },
                () => OnRuleRowClick(r),
                string.Equals(r.Id, keep, StringComparison.OrdinalIgnoreCase)));
        }
        return any;
    }

    /// <summary>内置规则包逐条开关：只记「哪些被关掉」，出厂表本身不改（改了等于改默认行为）。</summary>
    private void AddBuiltinRules(QbarPriorityLevel level)
    {
        if (_host == null) return;
        QbarOptions q = _host.Settings.Qbar;
        List<string> disabled = q.Search.DisabledBuiltins ??= new List<string>();
        bool master = q.Search.BuiltinRulesEnabled;
        foreach (var b in QbarPriorityResolver.BuiltinRows().Where(b => b.Level == level))
        {
            string text = QbarPriorityResolver.ExpandEnvironment(b.Text);
            if (text.Length == 0) text = b.Text;
            bool on = master && !disabled.Contains(b.Key, StringComparer.OrdinalIgnoreCase);
            RuleRows.Children.Add(BuildRuleRow(
                b.IsRegex ? "\uE71C" : "\uE8B7", text, b.IsRegex ? Loc.T("文件名正则") : "", on,
                v =>
                {
                    disabled.RemoveAll(k => string.Equals(k, b.Key, StringComparison.OrdinalIgnoreCase));
                    if (!v) disabled.Add(b.Key);
                    q.Search.DisabledBuiltins = disabled;
                    _host.SaveSettings();
                    RefreshRuleList(rebindEditor: false);
                    _host.Notify(v ? Loc.T("这条内置规则已启用") : Loc.T("这条内置规则已停用"));
                },
                null, false));
        }
    }

    private Border BuildHideLowRow(QbarOptions q) => BuildRuleRow(
        "\uE71C", Loc.T("默认不展示系统及隐藏文件的搜索结果"), "", q.Search.HideLowResults,
        v =>
        {
            q.Search.HideLowResults = v;
            _host?.SaveSettings();
            RefreshRuleList(rebindEditor: false);
            _host?.Notify(v ? Loc.T("低优先结果已隐藏（底部会提示隐藏条数）") : Loc.T("低优先结果恢复显示，只靠后排序"));
        },
        null, false);

    private Border BuildRuleRow(string glyph, string title, string subtitle, bool on,
                                Action<bool> onToggle, Action? onSelect, bool selected)
    {
        var grid = new Grid { Margin = new Thickness(12, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("Brush.TextSecondary"),
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = on ? 1 : 0.5,
            ToolTip = title,
        });
        if (!string.IsNullOrEmpty(subtitle))
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Style = ResStyle("Text.Tertiary"),
            });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var toggle = new CheckBox
        {
            Style = TryFindResource("ToggleSwitch") as Style,
            IsChecked = on,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        toggle.Click += (_, e) =>
        {
            e.Handled = true;
            onToggle(toggle.IsChecked == true);
        };
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        var row = new Border
        {
            Height = string.IsNullOrEmpty(subtitle) ? 42 : 50,
            CornerRadius = new CornerRadius(6),
            Child = grid,
            Background = Brushes.Transparent,
            Cursor = onSelect != null ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
        };
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "Brush.Selected");
        row.MouseEnter += (_, _) => { if (!selected) row.SetResourceReference(Border.BackgroundProperty, "Brush.Hover"); };
        row.MouseLeave += (_, _) => { if (!selected) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (InsideToggle(e.OriginalSource)) return;
            if (onSelect == null) return;
            e.Handled = true;
            onSelect();
        };
        return row;
    }

    private void OnRuleRowClick(QbarPriorityRule rule)
    {
        if (_host == null) return;
        QbarPriorityRule live = _host.Settings.Qbar.Priority
            .FirstOrDefault(x => string.Equals(x.Id, rule.Id, StringComparison.OrdinalIgnoreCase))?.Clone() ?? rule;
        BindRule(live);
        RefreshRuleList(rebindEditor: false);
    }

    private void SaveRuleFlag(QbarPriorityRule rule)
    {
        if (_host == null) return;
        List<QbarPriorityRule> list = _host.Settings.Qbar.Priority;
        int at = list.FindIndex(x => string.Equals(x.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) list[at] = rule.Clone();
        _host.SaveSettings();
        RefreshRuleList(rebindEditor: false);
    }

    /// <summary>「排除文件夹」是 dontindex 档的快捷入口：建好规则直接把光标放到目录框。</summary>
    private void OnRuleExcludeFolder(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var rule = new QbarPriorityRule
        {
            Id = "rule." + Guid.NewGuid().ToString("N")[..8],
            Level = "dontindex",
            Enabled = true,
        };
        _host.Settings.Qbar.Priority.Add(rule);
        _host.SaveSettings();
        _ruleEditing = rule.Clone();
        RefreshRuleList(rebindEditor: true);
        RuleHint.Text = Loc.T("正在新建一条「完全排除」规则：点右下的「选…」挑目录，它会从结果里彻底消失。");
        RuleFolderBox.Focus();
    }

    private void BindRule(QbarPriorityRule rule)
    {
        _ruleEditing = rule;
        _ruleSuppress = true;
        RuleFolderBox.Text = rule.FolderPath;
        RuleRegexBox.Text = rule.NameRegex;
        RuleNoteBox.Text = rule.Note;
        RuleEnabledBox.IsChecked = rule.Enabled;
        int at = Array.IndexOf(LevelOrder, string.IsNullOrWhiteSpace(rule.Level) ? "low" : rule.Level.ToLowerInvariant());
        RuleLevelBox.SelectedIndex = at >= 0 ? at : 2;
        _ruleSuppress = false;
    }

    private void ClearRuleEditor()
    {
        _ruleEditing = null;
        _ruleSuppress = true;
        RuleFolderBox.Text = "";
        RuleRegexBox.Text = "";
        RuleNoteBox.Text = "";
        RuleEnabledBox.IsChecked = false;
        RuleLevelBox.SelectedIndex = 2;
        _ruleSuppress = false;
    }

    private static readonly string[] LevelOrder = { "high", "normal", "low", "ignored", "dontindex" };

    private static string LevelKey(ComboBox box)
    {
        int at = box.SelectedIndex;
        return at >= 0 && at < LevelOrder.Length ? LevelOrder[at] : "low";
    }

    private void OnRuleFieldChanged(object sender, TextChangedEventArgs e) => PersistRule();

    private void OnRuleLevelChanged(object sender, SelectionChangedEventArgs e) => PersistRule();

    private void OnRuleFlagChanged(object sender, RoutedEventArgs e) => PersistRule();

    /// <summary>
    /// 正则先编译一次再落盘：编译不过就停在编辑器里并写明原因，
    /// 绝不能出现「看着存上了、其实求解器一直跳过它」。
    /// </summary>
    private void PersistRule()
    {
        if (_host == null || _ruleSuppress || _ruleEditing == null) return;

        string folder = RuleFolderBox.Text.Trim();
        string pattern = RuleRegexBox.Text.Trim();
        if (pattern.Length > 0)
        {
            try { _ = new Regex(pattern); }
            catch (ArgumentException ex)
            {
                RuleHint.Text = Loc.F("正则无效，没有保存：{0}", ex.Message);
                return;
            }
        }
        if (folder.Length == 0 && pattern.Length == 0)
        {
            RuleHint.Text = Loc.T("目录和正则至少要填一个：两个都空等于「所有文件」，会把整个结果集压下去。");
            return;
        }

        QbarPriorityRule rule = _ruleEditing;
        rule.FolderPath = folder;
        rule.NameRegex = pattern;
        rule.Note = RuleNoteBox.Text.Trim();
        rule.Level = LevelKey(RuleLevelBox);
        rule.Enabled = RuleEnabledBox.IsChecked == true;

        List<QbarPriorityRule> list = _host.Settings.Qbar.Priority;
        if (string.IsNullOrEmpty(rule.Id)) rule.Id = "rule." + Guid.NewGuid().ToString("N")[..8];
        QbarPriorityRule copy = rule.Clone();
        int at = list.FindIndex(x => string.Equals(x.Id, copy.Id, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) list[at] = copy; else list.Add(copy);
        _host.SaveSettings();

        RefreshRuleList(rebindEditor: false);
        RuleHint.Text = Loc.T("已保存，下一次输入就按新规则排序（不需要重建索引）。");
    }

    private void OnRuleAdd(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var rule = new QbarPriorityRule
        {
            Id = "rule." + Guid.NewGuid().ToString("N")[..8],
            Level = "low",
            Enabled = true,
        };
        _host.Settings.Qbar.Priority.Add(rule);
        _host.SaveSettings();
        _ruleEditing = rule.Clone();
        RefreshRuleList(rebindEditor: true);
        RuleFolderBox.Focus();
    }

    private void OnRuleDelete(object sender, RoutedEventArgs e)
    {
        if (_host == null || _ruleEditing == null) return;
        int removed = _host.Settings.Qbar.Priority
            .RemoveAll(x => string.Equals(x.Id, _ruleEditing.Id, StringComparison.OrdinalIgnoreCase));
        _host.SaveSettings();
        _ruleEditing = null;
        RefreshRuleList(rebindEditor: true);
        _host.Notify(removed > 0 ? Loc.T("规则已删除") : Loc.T("这条规则还没有保存过"));
    }

    private void OnRulePickFolder(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        // 用 WinForms 的 FolderBrowserDialog：Win7 线（net7.0-windows）没有 .NET 8 的 OpenFolderDialog。
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Loc.T("选择要应用这条规则的文件夹"),
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true,
        };
        if (System.IO.Directory.Exists(RuleFolderBox.Text.Trim()))
            dlg.SelectedPath = RuleFolderBox.Text.Trim();
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(dlg.SelectedPath)) return;
        RuleFolderBox.Text = dlg.SelectedPath;   // TextChanged 负责落盘
    }

    /* ── 命令 ─────────────────────────────────────────────────────────────
       命令表和过滤器表不一样：关键字只能有一条生效（用户同名覆盖内置）。
       所以列表里只列「最终生效的那条」，内置命令只读，想改就新增一条同关键字的
       命令把它盖掉 —— 这比给内置项开个编辑器副本要少一大截状态。 */

    private QbarCommand? _commandEditing;
    private bool _commandSuppress;

    private void RefreshCommandList(bool rebindEditor)
    {
        if (_host == null || CommandRows == null) return;

        List<QbarCommand> table = QbarCommandStore.Effective(_host.Settings.Qbar.Commands);
        var builtinIds = new HashSet<string>(QbarCommandStore.Builtins().Select(c => c.Id),
                                             StringComparer.OrdinalIgnoreCase);

        CommandRows.Children.Clear();
        AddGroupHeader(CommandRows, Loc.T("内置命令"));
        foreach (QbarCommand c in table.Where(c => builtinIds.Contains(c.Id)))
        {
            bool selected = _commandEditing != null &&
                            string.Equals(_commandEditing.Id, c.Id, StringComparison.OrdinalIgnoreCase);
            CommandRows.Children.Add(BuildCommandRow(c, selected, true));
        }

        AddGroupHeader(CommandRows, Loc.T("自己的命令"));
        int userCount = 0;
        foreach (QbarCommand c in table.Where(c => !builtinIds.Contains(c.Id)))
        {
            userCount++;
            bool selected = _commandEditing != null &&
                            string.Equals(_commandEditing.Id, c.Id, StringComparison.OrdinalIgnoreCase);
            CommandRows.Children.Add(BuildCommandRow(c, selected, false));
        }
        if (userCount == 0)
            CommandRows.Children.Add(BuildHintRow(Loc.T("还没有自己的命令：点「+ 新增命令」，例如把 ol 指向自己的词典程序。")));

        if (rebindEditor && _commandEditing != null) BindCommand(_commandEditing);
    }

    private Border BuildCommandRow(QbarCommand command, bool selected, bool builtin)
    {
        var grid = new Grid { Margin = new Thickness(12, 0, 10, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            /* ol 可能落到网页词典，所以用地球图标；E8D2 的字形在 13px 下糊成一团，
               看着像图标没加载。 */
            Text = builtin && command.Id == QbarCommandStore.BuiltinDictId ? "\uE774" : "\uE756",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("Brush.TextSecondary"),
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        string keyword = (command.Keyword ?? "").Trim();
        string program = (command.Path ?? "").Trim();
        string subtitle = program.Length > 0 ? program : Loc.T("Qbar 内置动作");
        if (!string.IsNullOrEmpty(command.Parameter)) subtitle += " " + command.Parameter;

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = keyword,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = Res("Brush.Accent"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(command.Title) ? Loc.T("（没有标题）") : command.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = command.Title,
        });
        text.Children.Add(head);
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = ResStyle("Text.Tertiary"),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var badge = new TextBlock
        {
            Text = builtin ? Loc.T("内置") : Loc.T("自定义"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Style = ResStyle("Text.Tertiary"),
        };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        /* 「中键」勾选框：勾上的命令会出现在中键菜单的「命令」二级菜单里。
         * 放在行尾（徽标右边），和固定项那一列的语义、位置保持一致。 */
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var middle = new CheckBox
        {
            Content = Loc.T("附加菜单"),
            IsChecked = command.ShowInMiddleMenu,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0),
            FontSize = 12,
            ToolTip = Loc.T("勾上后这条命令会出现在附加菜单的「命令」二级菜单里。"),
        };
        middle.Click += (_, e) =>
        {
            e.Handled = true;   // 别再触发整行的"打开这条命令"
            OnCommandMiddleMenuToggle(command, middle.IsChecked == true);
        };
        Grid.SetColumn(middle, 3);
        grid.Children.Add(middle);

        var row = new Border
        {
            Height = 50,
            CornerRadius = new CornerRadius(6),
            Child = grid,
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "Brush.Selected");
        row.MouseEnter += (_, _) => { if (!selected) row.SetResourceReference(Border.BackgroundProperty, "Brush.Hover"); };
        row.MouseLeave += (_, _) => { if (!selected) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OnCommandRowClick(command, builtin);
        };
        return row;
    }

    /// <summary>
    /// 命令行尾的「中键」勾选框。
    ///
    /// 内置命令在设置里没有实体条目（它们来自 QbarCommandStore.Builtins），
    /// 所以第一次勾选时按它的 Id 落一份覆盖副本 —— Effective() 本来就是按 Id 合并的，
    /// 副本一旦存在就以副本为准。副作用是这条命令会从「内置」组挪到「自己的命令」组，
    /// 这正是本程序里"改过内置项"的既有表现，不另造例外。
    /// </summary>
    private void OnCommandMiddleMenuToggle(QbarCommand command, bool on)
    {
        if (_host == null) return;
        var list = _host.Settings.Qbar.Commands;
        QbarCommand? live = list.FirstOrDefault(c =>
            string.Equals(c.Id, command.Id, StringComparison.OrdinalIgnoreCase));
        if (live == null)
        {
            live = command.Clone();
            list.Add(live);
        }
        live.ShowInMiddleMenu = on;
        _host.SaveSettings();
        RefreshCommandList(rebindEditor: false);
    }

    private void OnCommandRowClick(QbarCommand command, bool builtin)
    {
        if (_host == null) return;
        QbarCommand live = _host.Settings.Qbar.Commands
            .FirstOrDefault(x => string.Equals(x.Id, command.Id, StringComparison.OrdinalIgnoreCase))?.Clone()
            ?? command.Clone();

        /* 老配置里 ol 的参数是空的（出厂值 1.7.3 才写进去）。列表行显示的是 Effective()
         * 补过默认值的那份，点进去却绑原始设置 —— 参数框空着，用户以为查词没配参数。
         * 这里补一遍，点开就能看到 -w "{query}"。 */
        QbarCommandStore.EnsureDefaults(live);
        BindCommand(live);
        SetCommandEditorEnabled(true);
        /* 内部动作（新建文件 / 复制路径 / 刷新索引 …）以前把「程序 / 参数 / 工作目录」
           整片禁掉了，用户反馈"内置命令不能编辑"。现在一律可填：
           填了程序就改成启动那个程序，留空才走软件自己的实现 —— 两种都成立。 */
        bool internalAction = QbarCommandStore.IsInternalAction(command.Id);
        SetCommandProgramEnabled(true);
        CommandHint.Text = internalAction
            ? Loc.T("内置动作默认由软件自己完成。在「程序」里填一个可执行文件，就会改成启动它（参数、工作目录照常生效）；ol 填成欧路词典就用欧路查。")
            : builtin
                ? Loc.T("内置命令也能改：换程序、换参数、换关键字都行，改完立刻生效；点「恢复默认」退回出厂值。")
                : Loc.T("改完立刻生效；下次在 Qbar 里打「关键字 空格 参数」就是这条命令。");

        /* 查词这条命令最容易"以为没实现"：装到了非默认目录，程序里没填路径，
           软件就悄悄回落到网页词典。把检测结果写在提示行上，省得用户猜。 */
        if (string.Equals(command.Id, QbarCommandStore.BuiltinDictId, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(live.Path))
        {
            string? eudic = EudicLocator.Find();
            CommandHint.Text += eudic != null
                ? "\n已检测到欧路词典：" + eudic + "（留空就用它）"
                : "\n没检测到欧路词典。装好后重新打开设置即可自动识别，或者在这里直接选它的 exe。";
        }
        RefreshCommandList(rebindEditor: false);
    }

    private void BindCommand(QbarCommand command)
    {
        _commandEditing = command;
        _commandSuppress = true;
        CommandKeywordBox.Text = command.Keyword;
        CommandTitleBox.Text = command.Title;
        CommandPathBox.Text = command.Path;
        CommandArgsBox.Text = command.Parameter;
        CommandWorkDirBox.Text = command.WorkingDirectory;
        CommandAdminBox.IsChecked = command.RunAsAdmin;
        CommandSilentBox.IsChecked = command.Silent;
        _commandSuppress = false;
    }

    private void ClearCommandEditor()
    {
        _commandEditing = null;
        _commandSuppress = true;
        CommandKeywordBox.Text = "";
        CommandTitleBox.Text = "";
        CommandPathBox.Text = "";
        CommandArgsBox.Text = "";
        CommandWorkDirBox.Text = "";
        CommandAdminBox.IsChecked = false;
        CommandSilentBox.IsChecked = false;
        _commandSuppress = false;
        SetCommandEditorEnabled(true);
        CommandHint.Text = Loc.T("点「+ 新增命令」建一条自己的命令。");
    }

    /// <summary>程序/参数/工作目录/提权/静默这几项——内部动作没有它们。</summary>
    private void SetCommandProgramEnabled(bool on)
    {
        CommandPathBox.IsEnabled = on;
        CommandArgsBox.IsEnabled = on;
        CommandWorkDirBox.IsEnabled = on;
        CommandAdminBox.IsEnabled = on;
        CommandSilentBox.IsEnabled = on;
        if (CommandPicker != null) CommandPicker.IsEnabled = on;
    }

    private void SetCommandEditorEnabled(bool on)
    {
        CommandKeywordBox.IsEnabled = on;
        CommandTitleBox.IsEnabled = on;
        CommandPathBox.IsEnabled = on;
        CommandArgsBox.IsEnabled = on;
        CommandWorkDirBox.IsEnabled = on;
        CommandAdminBox.IsEnabled = on;
        CommandSilentBox.IsEnabled = on;
        if (CommandPicker != null) CommandPicker.IsEnabled = on;
    }

    private void OnCommandFieldChanged(object sender, TextChangedEventArgs e) => PersistCommand();

    private void OnCommandFlagChanged(object sender, RoutedEventArgs e) => PersistCommand();

    private void PersistCommand()
    {
        if (_host == null || _commandSuppress || _commandEditing == null) return;

        string keyword = (CommandKeywordBox.Text ?? "").Trim().TrimEnd(':');
        if (keyword.Length == 0)
        {
            CommandHint.Text = Loc.T("关键字不能为空：它决定在 Qbar 里打什么能触发这条命令。");
            return;
        }
        if (!Regex.IsMatch(keyword, "^[0-9A-Za-z_\u4e00-\u9fff-]+$"))
        {
            CommandHint.Text = Loc.T("关键字只能用字母、数字、下划线、短横线或汉字，不能带空格和冒号。");
            return;
        }

        /* 关键字是触发词，重复就意味着"有一条命令永远打不到"。
           同 id 自己不算冲突（改标题、改参数时关键字没变）。 */
        QbarCommand? clash = QbarCommandStore.Effective(_host.Settings.Qbar.Commands)
            .FirstOrDefault(c => string.Equals((c.Keyword ?? "").Trim(), keyword, StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(c.Id, _commandEditing.Id, StringComparison.OrdinalIgnoreCase));
        if (clash != null)
        {
            CommandHint.Text = Loc.F("关键字「{0}」已经被「{1}」占了，换一个。", keyword,
                                     string.IsNullOrWhiteSpace(clash.Title) ? clash.Keyword : clash.Title);
            return;
        }

        QbarCommand command = _commandEditing;
        command.Keyword = keyword;
        command.Title = (CommandTitleBox.Text ?? "").Trim();
        command.Path = (CommandPathBox.Text ?? "").Trim();
        command.Parameter = (CommandArgsBox.Text ?? "").Trim();
        command.WorkingDirectory = (CommandWorkDirBox.Text ?? "").Trim();
        command.RunAsAdmin = CommandAdminBox.IsChecked == true;
        command.Silent = CommandSilentBox.IsChecked == true;
        if (command.Id.Length == 0) command.Id = "cmd." + Guid.NewGuid().ToString("N")[..8];

        List<QbarCommand> list = _host.Settings.Qbar.Commands;
        QbarCommand copy = command.Clone();
        int at = list.FindIndex(x => string.Equals(x.Id, copy.Id, StringComparison.OrdinalIgnoreCase));

        /* 内置命令改回出厂值 = 不存这份副本：设置文件里少一条垃圾，
           而且「恢复默认」和「手动改回去」落到的状态完全一样。 */
        if (QbarCommandStore.SameAsDefault(copy))
        {
            if (at >= 0) list.RemoveAt(at);
        }
        else if (at >= 0) list[at] = copy;
        else list.Add(copy);
        _host.SaveSettings();

        RefreshCommandList(rebindEditor: false);
        CommandHint.Text = QbarCommandStore.IsBuiltin(copy.Id)
            ? Loc.F("已保存：内置命令已按你的改动覆盖，输入「{0}」生效；点「恢复默认」退回出厂值。", keyword)
            : Loc.F("已保存：在 Qbar 里输入「{0} 参数」执行。", keyword);
    }

    private void OnCommandAdd(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        List<QbarCommand> list = _host.Settings.Qbar.Commands;
        var used = new HashSet<string>(QbarCommandStore.Effective(list).Select(c => (c.Keyword ?? "").Trim()),
                                       StringComparer.OrdinalIgnoreCase);
        int n = 1;
        while (used.Contains("my" + n)) n++;

        var command = new QbarCommand
        {
            Id = "cmd." + Guid.NewGuid().ToString("N")[..8],
            Keyword = "my" + n,
            Title = Loc.T("新命令"),
        };
        list.Add(command);
        _host.SaveSettings();
        _commandEditing = command.Clone();
        SetCommandEditorEnabled(true);
        RefreshCommandList(rebindEditor: false);
        BindCommand(_commandEditing);
        CommandKeywordBox.Focus();
        CommandKeywordBox.SelectAll();
    }

    private void OnCommandDelete(object sender, RoutedEventArgs e)
    {
        if (_host == null || _commandEditing == null) return;
        bool builtin = QbarCommandStore.Builtins()
            .Any(c => string.Equals(c.Id, _commandEditing.Id, StringComparison.OrdinalIgnoreCase));
        if (builtin)
        {
            // 内置命令不能"删"（它出厂就有），但可以把你改过的那份丢掉 = 恢复默认。
            int dropped = _host.Settings.Qbar.Commands
                .RemoveAll(x => string.Equals(x.Id, _commandEditing.Id, StringComparison.OrdinalIgnoreCase));
            _host.SaveSettings();
            BindCommand(QbarCommandStore.Default(_commandEditing.Id)!.Clone());
            SetCommandProgramEnabled(true);
            RefreshCommandList(rebindEditor: false);
            _host.Notify(dropped > 0 ? Loc.T("已恢复出厂设置") : Loc.T("这一条本来就是出厂设置"));
            return;
        }
        int removed = _host.Settings.Qbar.Commands
            .RemoveAll(x => string.Equals(x.Id, _commandEditing.Id, StringComparison.OrdinalIgnoreCase));
        _host.SaveSettings();
        ClearCommandEditor();
        RefreshCommandList(rebindEditor: false);
        _host.Notify(removed > 0 ? Loc.T("命令已删除") : Loc.T("这条命令还没有保存过"));
    }

    private void OnCommandPickPath(object sender, RoutedEventArgs e)
    {
        if (_host == null || _commandEditing == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择要执行的程序"),
            Filter = Loc.T("程序|*.exe;*.bat;*.cmd;*.lnk|所有文件|*.*"),
        };
        if (dlg.ShowDialog() != true) return;
        CommandPathBox.Text = dlg.FileName;   // TextChanged 负责落盘
    }

    /// <summary>
    /// 「点选窗口」：直接在屏幕上点目标窗口，反查它的 exe 填进「程序」。
    /// 和按应用/窗口绑定共用 <see cref="WindowPickerWindow"/> —— 选程序这件事
    /// 全软件只有一种交互，别为命令单独造一个。
    /// </summary>
    private void OnCommandPickWindow(object sender, RoutedEventArgs e)
    {
        if (_host == null || _commandEditing == null) return;

        var picker = new WindowPickerWindow();
        if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
        picker.ShowDialog();

        IntPtr handle = picker.PickedWindow;
        if (handle == IntPtr.Zero) return;                  // Esc

        Core.Windows.WindowInfo? info = Core.Windows.WindowService.Describe(handle);
        string path = info?.ExePath ?? "";
        if (path.Length == 0)
        {
            CommandHint.Text = Loc.T("取不到这个窗口的程序路径（可能是提权窗口或系统窗口），改用「选择程序…」。");
            return;
        }
        CommandPathBox.Text = path;                         // TextChanged 负责落盘
        CommandHint.Text = Loc.F("已填入「{0}」。", System.IO.Path.GetFileName(path));
    }

    /// <summary>「恢复默认」：丢掉内置命令的用户副本。</summary>
    private void OnCommandRevert(object sender, RoutedEventArgs e)
    {
        if (_host == null || _commandEditing == null) return;
        if (!QbarCommandStore.IsBuiltin(_commandEditing.Id))
        {
            CommandHint.Text = Loc.T("只有内置命令有「出厂值」；自己加的命令直接删掉就行。");
            return;
        }
        int dropped = _host.Settings.Qbar.Commands
            .RemoveAll(x => string.Equals(x.Id, _commandEditing.Id, StringComparison.OrdinalIgnoreCase));
        _host.SaveSettings();
        BindCommand(QbarCommandStore.Default(_commandEditing.Id)!.Clone());
        SetCommandEditorEnabled(true);
        SetCommandProgramEnabled(true);
        RefreshCommandList(rebindEditor: false);
        CommandHint.Text = dropped > 0
            ? Loc.T("已恢复出厂值。")
            : Loc.T("这一条本来就是出厂值，没有改动可撤。");
    }


    /* ── 文件对话框浮层 ─────────────────────────────────────────
       控件事件直接改 Settings.FileDialogBar，再交给 AppHost 防抖落盘 + 广播
       SettingsChanged；FileDialogBarService.Apply() 收到广播后按新配置重挂监听。
       这里刻意不做“预览”，因为浮层真实行为发生在别人的对话框上，设置页里
       造不出那个上下文，假预览反而误导。 */

    private void OnFileDialogBarChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        FileDialogBarOptions o = _host.Settings.FileDialogBar;
        o.Enabled = FdEnabledBox.IsChecked == true;
        o.ShowOnAppear = FdShowOnAppearBox.IsChecked == true;
        o.ShowOnHotkey = FdShowOnHotkeyBox.IsChecked == true;
        o.UiaBackfill = FdUiaBox.IsChecked == true;
        o.MaxResults = (int)Math.Round(FdMaxSlider.Value);
        o.BarHeight = (int)Math.Round(FdHeightSlider.Value);
        o.PollFallbackMs = (int)Math.Round(FdPollSlider.Value);
        o.FollowMetrics = FdFollowMetricsBox.IsChecked == true;
        UpdateFileDialogLabels();
        _host.SaveSettings();
    }

    /// <summary>
    /// 热键：先本地校验再落盘。
    ///
    /// 校验用的是注册热键那条代码的同一份解析器（<see cref="Core.FileDialog.FileDialogBarService.TryValidateHotkey"/>），
    /// 不是另写一套正则 —— 否则“设置页说能用、真正注册时解析失败”会变成两个真相。
    /// </summary>
    private void OnFileDialogApplyHotkey(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        string text = (FdHotkeyBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            _host.Settings.FileDialogBar.Hotkey = "";
            FdHotkeyHint.Text = Loc.T("清空后不再用热键唤出浮层（仍可按“对话框一出现就浮出”）。");
            _host.SaveSettings();
            return;
        }

        if (!Core.FileDialog.FileDialogBarService.TryValidateHotkey(text, out string detail))
        {
            FdHotkeyHint.Text = Loc.T("这个写法认不出来：需要一到三个修饰键 + 一个主键，例如 Ctrl+Alt+F。");
            return;
        }

        _host.Settings.FileDialogBar.Hotkey = detail;
        FdHotkeyHint.Text = Loc.F("已设为 {0}；若被别的程序占用，日志里会有记录。", detail);
        _host.SaveSettings();
    }

    private void OnFileDialogTypeChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        /* 键名用 DialogTypeKeys，不写字面量：判定侧用的是同一份常量，
         * 改名字时编译器会一起报错，不会出现“设置写了但判定读不到”。 */
        var types = _host.Settings.FileDialogBar.Types;
        types[Core.FileDialog.DialogTypeKeys.FileDialog] = FdTypeFileBox.IsChecked == true;
        types[Core.FileDialog.DialogTypeKeys.FolderDialog] = FdTypeFolderBox.IsChecked == true;
        types[Core.FileDialog.DialogTypeKeys.ExplorerWindow] = FdTypeExplorerBox.IsChecked == true;
        _host.SaveSettings();
    }

    private void OnFileDialogAddProcess(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        string name = (FdProcessBox.Text ?? "").Trim();
        if (name.Length == 0) return;
        // 只收文件名：用户可能顺手粘了一整条路径进来。
        try { name = System.IO.Path.GetFileName(name); } catch { }
        if (name.Length == 0) return;
        List<string> list = _host.Settings.FileDialogBar.DisabledProcesses;
        if (list.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
        {
            _host.Notify(Loc.F("「{0}」已经在列表里了", name));
            return;
        }
        list.Add(name);
        FdProcessBox.Text = "";
        RefreshFileDialogProcessList();
        _host.SaveSettings();
    }

    private void OnFileDialogRemoveProcess(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (FdProcessList.SelectedItem is not string name) return;
        _host.Settings.FileDialogBar.DisabledProcesses.RemoveAll(
            x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        RefreshFileDialogProcessList();
        _host.SaveSettings();
    }

    private void RefreshFileDialogProcessList()
    {
        if (_host == null) return;
        // 每次换一份 List：设置里的集合是普通 List<string>，没有变更通知。
        FdProcessList.ItemsSource = null;
        FdProcessList.ItemsSource = _host.Settings.FileDialogBar.DisabledProcesses.ToList();
    }

    /// <summary>
    /// 把「文件对话框浮层」这张卡滚进可视区（浮条菜单 →「设置」）。
    /// 这张卡是「文件搜索」标签里的一张独立卡片，所以先切标签再滚到它。
    /// </summary>
    internal void ScrollToFileDialogBar()
    {
        try
        {
            /* 这张卡现在在「文件搜索」标签里（自己的卡，不再挤在右栏最下面）：
             * 先切到那个标签，再滚到卡片。 */
            if (Tabs != null && Tabs.SelectedIndex != 1) Tabs.SelectedIndex = 1;
            ShowFilesTab(true);
            FileScroll.UpdateLayout();

            double y = 0;
            try
            {
                y = FileDialogBarCard.TransformToAncestor(FileScrollContent)
                    .Transform(new Point(0, 0)).Y;
            }
            catch { }
            FileScroll.ScrollToVerticalOffset(Math.Max(0, y - 8));
            FileDialogBarCard.BringIntoView();
            Core.CoreLog.Info($"[浮条] 文件搜索页滚动到文件对话框卡：offset={FileScroll.VerticalOffset:0}（卡 y={y:0}）");
        }
        catch (Exception ex) { Core.CoreLog.Swallow(ex, "滚动到文件对话框浮层卡"); }
    }

    /// <summary>出图用：切到「文件搜索」标签并滚到「文件对话框浮层」卡。</summary>
    internal void ScrollToFileDialogBarForRender() => ScrollToFileDialogBar();

    /// <summary>控件跟随设置：设置文件可能被手改，越界值在这里夹回界面区间。</summary>
    private void RefreshFileDialogBar()
    {
        if (_host == null) return;
        FileDialogBarOptions o = _host.Settings.FileDialogBar;
        FdEnabledBox.IsChecked = o.Enabled;
        FdShowOnAppearBox.IsChecked = o.ShowOnAppear;
        FdShowOnHotkeyBox.IsChecked = o.ShowOnHotkey;
        FdUiaBox.IsChecked = o.UiaBackfill;
        FdHotkeyBox.Text = o.Hotkey ?? "";
        FdTypeFileBox.IsChecked = !o.Types.TryGetValue(Core.FileDialog.DialogTypeKeys.FileDialog, out bool f) || f;
        FdTypeFolderBox.IsChecked = !o.Types.TryGetValue(Core.FileDialog.DialogTypeKeys.FolderDialog, out bool d) || d;
        FdTypeExplorerBox.IsChecked = o.Types.TryGetValue(Core.FileDialog.DialogTypeKeys.ExplorerWindow, out bool x) && x;
        FdMaxSlider.Value = Math.Clamp(o.MaxResults, 20, 500);
        FdHeightSlider.Value = Math.Clamp(o.BarHeight, 40, 48);
        FdPollSlider.Value = Math.Clamp(o.PollFallbackMs, 0, 2000);
        FdFollowMetricsBox.IsChecked = o.FollowMetrics;
        RefreshFileDialogProcessList();
        UpdateFileDialogLabels();
    }

    private void UpdateFileDialogLabels()
    {
        if (FdMaxValue == null) return;
        FdMaxValue.Text = Loc.F("{0} 条", (int)Math.Round(FdMaxSlider.Value));
        FdHeightValue.Text = Loc.F("{0} px", (int)Math.Round(FdHeightSlider.Value));
        int poll = (int)Math.Round(FdPollSlider.Value);
        FdPollValue.Text = poll <= 0 ? Loc.T("关闭") : Loc.F("{0} ms", poll);
    }

    /* ── 搜索前建议 ─────────────────────────────────────────────
       同一套写法：控件事件改 Settings.Suggestions → _host.SaveSettings()，
       SuggestionsService.Apply() 收到 SettingsChanged 后重建来源并请求重采。
       注意这里**不做**“把建议列表塞回设置页”这种预览：建议项的排序依赖
       真实文件时间与来源权重，设置页里造不出来。 */

    private static readonly string[] AllSuggestionSourceIds =
    {
        "downloads", "desktop", "wechat", "wecom", "feishu", "windows-recent", "dialog-history", "app-history",
    };

    private static int SuggestionWeight(string id) => id switch
    {
        "downloads" => 70, "desktop" => 65, "wechat" => 80, "wecom" => 75,
        "feishu" => 75, "windows-recent" => 90, "dialog-history" => 100, _ => 110,
    };

    /// <summary>来源表的行集合。拖拽排序直接改这个集合的顺序，UI 自动跟着动。</summary>
    private readonly ObservableCollection<SuggestionSourceRow> _suggestionRows = new();

    private void EnsureSuggestionRows()
    {
        if (_suggestionRows.Count > 0) return;
        foreach (string id in SuggestionsOptions.DefaultSourceOrder)
        {
            _suggestionRows.Add(new SuggestionSourceRow
            {
                Id = id,
                Label = SuggestionLabel(id),
                Weight = SuggestionWeight(id).ToString(),
                Status = Loc.T("尚未采集"),
                Enabled = true,
            });
        }
        SgSourceList.ItemsSource = _suggestionRows;
    }

    private void OnSuggestionsChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        SuggestionsOptions o = _host.Settings.Suggestions;
        o.Show = SgShowBox.IsChecked == true;
        o.ShowCount = (int)Math.Round(SgCountSlider.Value);
        o.MaxCandidates = (int)Math.Round(SgMaxSlider.Value);
        UpdateSuggestionLabels();
        _host.SaveSettings();
    }

    private void OnSuggestionsBudgetChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Suggestions.CollectBudgetMs = (int)Math.Round(SgBudgetSlider.Value);
        UpdateSuggestionLabels();
        _host.SaveSettings();
    }

    /// <summary>立即重新采集：不清缓存、不重建服务，只把采集线程叫醒一次。</summary>
    private void OnSuggestionsRefresh(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var service = _host.Suggestions;
        if (service == null)
        {
            _host.Notify(Loc.T("建议服务未启动（诊断模式下不启用）"));
            return;
        }

        service.RequestRefresh(0);
        SgStatsText.Text = Loc.T("正在重新采集…");
        _host.Notify(Loc.T("已开始重新采集建议"));

        // 采集在后台线程，1.2 秒后把统计刷新一次（不阻塞界面）
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefreshSuggestionStats();
        };
        timer.Start();
    }

    private void OnSuggestionSourceToggle(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;

        /* Sources 的语义是"空 = 全部启用"，所以这里只记"用户关掉了谁"：
         * 全勾时 DisabledSources 为空，以后新增的第九个来源会自然跟着启用。
         * 反过来，如果存"勾了哪几个"，旧配置就会把新来源冻在关闭状态。 */
        if (sender is CheckBox { Tag: string id } box && _suggestionRows.FirstOrDefault(r => r.Id == id) is { } row)
            row.Enabled = box.IsChecked == true;

        _host.Settings.Suggestions.DisabledSources =
            _suggestionRows.Where(r => !r.Enabled).Select(r => r.Id).ToList();
        _host.Settings.Suggestions.Sources = new List<string>();
        _host.SaveSettings();
        RefreshSuggestionStats();
    }

    private void OnSuggestionsApplyFilters(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        SuggestionsOptions o = _host.Settings.Suggestions;

        o.FileTypeFilter = SplitExtensions(SgFilterBox.Text);
        o.Exclusions.FileExtensions = SplitExtensions(SgExtBox.Text);
        // 都清空也不是“什么都不要”：扩展名黑名单空了就退回默认的三项，
        // 否则 tmp/bak/log 会突然挤满建议列表。
        if (o.Exclusions.FileExtensions.Count == 0)
            o.Exclusions.FileExtensions = new List<string> { "tmp", "bak", "log" };

        SgFilterBox.Text = string.Join(";", o.FileTypeFilter);
        SgExtBox.Text = string.Join(";", o.Exclusions.FileExtensions);
        _host.SaveSettings();
        RefreshSuggestionStats();
        _host.Notify(Loc.T("建议过滤已更新"));
    }

    private static List<string> SplitExtensions(string? text)
        => (text ?? "").Split(new[] { ';', ',', ' ', '，', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().TrimStart('.').ToLowerInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void OnSuggestionsApplyHotkey(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        string text = (SgHotkeyBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            _host.Settings.Suggestions.HideFileHotkey = "";
            SgHotkeyBox.Text = "";
            _host.SaveSettings();
            _host.Notify(Loc.T("已关闭建议隐藏热键（右键菜单仍可隐藏）"));
            return;
        }

        var parsed = Core.HotkeyCombo.Parse(text);
        if (parsed == null)
        {
            _host.Notify(Loc.T("热键写法认不出来：需要修饰键 + 主键，例如 Ctrl+Alt+H"));
            return;
        }

        string normalized = Core.HotkeyCombo.Normalize(text);
        _host.Settings.Suggestions.HideFileHotkey = normalized;
        SgHotkeyBox.Text = normalized;
        _host.SaveSettings();
        _host.Notify(Loc.F("建议隐藏热键已设为 {0}", normalized));
    }

    private void OnSuggestionsUnhide(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        var service = _host.Suggestions;
        if (service == null || SgHiddenList.SelectedIndex < 0) return;
        int index = SgHiddenList.SelectedIndex;
        if (index >= _hiddenRows.Count) return;

        HiddenRow row = _hiddenRows[index];
        if (!service.Unhide(row.Path, row.IsFolder, out string error))
        {
            _host.Notify(Loc.T("还原失败：") + error);
            return;
        }
        _host.Notify(Loc.T("已还原，刷新 Qbar 后会出现"));
        RefreshSuggestionStats();
    }

    private void OnSuggestionsUnhideAll(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        var service = _host.Suggestions;
        if (service == null) return;

        int restored = 0;
        string lastError = "";
        foreach (string path in service.HiddenFiles().ToList())
        {
            if (service.Unhide(path, false, out string error)) restored++;
            else lastError = error;
        }
        foreach (string path in service.HiddenFolders().ToList())
        {
            if (service.Unhide(path, true, out string error)) restored++;
            else lastError = error;
        }

        _host.Notify(restored > 0
            ? Loc.F("已还原 {0} 条被隐藏的建议", restored)
            : Loc.T("没有可还原的建议项") + (lastError.Length > 0 ? "：" + lastError : ""));
        RefreshSuggestionStats();
    }

    private sealed record HiddenRow(string Display, string Path, bool IsFolder);

    private readonly List<HiddenRow> _hiddenRows = new();

    /// <summary>来源 id 的界面名字（来源表和顺序都用它）。</summary>
    private static string SuggestionLabel(string id) => id switch
    {
        "downloads" => Loc.T("下载文件夹"),
        "desktop" => Loc.T("桌面"),
        "wechat" => Loc.T("微信接收的文件"),
        "wecom" => Loc.T("企业微信接收的文件"),
        "feishu" => Loc.T("飞书文件"),
        "windows-recent" => Loc.T("Windows 最近使用"),
        "dialog-history" => Loc.T("文件对话框历史"),
        "app-history" => Loc.T("应用内历史"),
        _ => id,
    };

    /// <summary>来源表的一行。拖拽排序改的是集合顺序，不碰任何单独的顺序窗口。</summary>
    public sealed class SuggestionSourceRow : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public string Weight { get; init; } = "";

        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Enabled)));
            }
        }

        private string _status = "";
        public string Status
        {
            get => _status;
            set
            {
                if (string.Equals(_status, value, StringComparison.Ordinal)) return;
                _status = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Status)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private Point _sourceDragStart;
    private int _sourceDragIndex = -1;

    /// <summary>鼠标落点对应的来源行下标（0 起）；点不到行返回 -1。</summary>
    private int SourceRowIndexAt(Point point)
    {
        if (SgSourceList == null) return -1;
        var hit = SgSourceList.InputHitTest(point) as DependencyObject;
        while (hit != null && !ReferenceEquals(hit, SgSourceList))
        {
            if (hit is ContentPresenter presenter)
            {
                int index = SgSourceList.ItemContainerGenerator.IndexFromContainer(presenter);
                if (index >= 0) return index;
            }
            hit = ParentOf(hit);
        }
        return -1;
    }

    /// <summary>
    /// 安全地往上找父级：TextBlock 里的 Run 是 ContentElement，直接
    /// VisualTreeHelper.GetParent 会抛"Run 不是 Visual"（Qbar 就栽在这），
    /// 拖拽时 OriginalSource 恰好经常是 Run。ContentElement 走逻辑树。
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject d)
    {
        try
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(d);
        }
        catch (InvalidOperationException) { }
        if (d is FrameworkContentElement fce) return fce.Parent;
        if (d is FrameworkElement fe) return fe.Parent;
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (DependencyObject? d = source; d != null; d = ParentOf(d))
            if (d is T typed) return typed;
        return null;
    }

    private void OnSuggestionSourceMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 复选框区域留给勾选，不参与拖动；行里其它任何位置都是拖拽手柄。
        if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null) return;
        int index = SourceRowIndexAt(e.GetPosition(SgSourceList));
        if (index < 0) return;
        _sourceDragIndex = index;
        _sourceDragStart = e.GetPosition(SgSourceList);
    }

    private void OnSuggestionSourceMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _sourceDragIndex < 0) return;
        Point now = e.GetPosition(SgSourceList);
        if (Math.Abs(now.X - _sourceDragStart.X) < 6 && Math.Abs(now.Y - _sourceDragStart.Y) < 6) return;

        var data = new DataObject("InputPlusSuggestionSource", _sourceDragIndex);
        DragDrop.DoDragDrop(SgSourceList, data, DragDropEffects.Move);
        _sourceDragIndex = -1;
    }

    private void OnSuggestionSourceDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("InputPlusSuggestionSource") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSuggestionSourceDrop(object sender, DragEventArgs e)
    {
        _sourceDragIndex = -1;
        if (_host == null || !e.Data.GetDataPresent("InputPlusSuggestionSource")) return;
        if (e.Data.GetData("InputPlusSuggestionSource") is not int from) return;

        int to = SourceRowIndexAt(e.GetPosition(SgSourceList));
        if (to < 0 || from < 0 || from >= _suggestionRows.Count || to >= _suggestionRows.Count || from == to) return;

        _suggestionRows.Move(from, to);
        _host.Settings.Suggestions.SourceOrder = _suggestionRows.Select(r => r.Id).ToList();
        _host.SaveSettings();
        _host.Notify(Loc.T("来源顺序已更新，Qbar 空态会按新顺序展示"));
        e.Handled = true;
    }

    /// <summary>控件跟随设置 + 已隐藏列表 + 上次采集统计。</summary>
    private void RefreshSuggestions()
    {
        if (_host == null) return;
        SuggestionsOptions o = _host.Settings.Suggestions;

        SgShowBox.IsChecked = o.Show;
        EnsureSuggestionRows();

        /* 顺序以设置里的 SourceOrder 为准（空 = 出厂顺序）；这里把集合挪成同一顺序，
         * 拖拽后的下一次 Refresh 也走同一条路，不会出现两套顺序。 */
        var order = o.ResolveSourceOrder();
        for (int i = 0; i < order.Count; i++)
        {
            SuggestionSourceRow? row = _suggestionRows.FirstOrDefault(r => r.Id == order[i]);
            if (row == null) continue;
            int current = _suggestionRows.IndexOf(row);
            if (current != i) _suggestionRows.Move(current, i);
        }
        foreach (SuggestionSourceRow row in _suggestionRows) row.Enabled = o.IsSourceEnabled(row.Id);

        SgFilterBox.Text = string.Join(";", o.FileTypeFilter);
        SgExtBox.Text = string.Join(";", o.Exclusions.FileExtensions);
        SgHotkeyBox.Text = o.HideFileHotkey ?? "";
        SgCountSlider.Value = Math.Clamp(o.ShowCount, 0, 50);
        SgMaxSlider.Value = Math.Clamp(o.MaxCandidates, 200, 5000);
        SgBudgetSlider.Value = Math.Clamp(o.CollectBudgetMs, 200, 5000);
        UpdateSuggestionLabels();
        RefreshSuggestionStats();

        /* 首屏还没采完时，状态表会全是「尚未采集」。等后台线程收工再补一次，
         * 用户停在设置页就能看到每个来源的真实条数，不用退出再进。 */
        if (_host.Suggestions is { IsWarm: false })
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RefreshSuggestionStats();
            };
            timer.Start();
        }
    }

    private void UpdateSuggestionLabels()
    {
        if (SgCountValue == null) return;
        SgCountValue.Text = Loc.F("{0} 条", (int)Math.Round(SgCountSlider.Value));
        SgMaxValue.Text = Loc.F("{0} 条", (int)Math.Round(SgMaxSlider.Value));
        if (SgBudgetValue != null) SgBudgetValue.Text = Loc.F("{0} ms", (int)Math.Round(SgBudgetSlider.Value));
    }

    /// <summary>每个来源的"上次采集"格子：条数 / 失败原因 / 未启用 / 尚未采集。</summary>
    private void RefreshSuggestionSourceStates(RecSnapshot snapshot)
    {
        foreach (SuggestionSourceRow row in _suggestionRows)
        {
            if (!row.Enabled)
            {
                row.Status = Loc.T("已关闭");
                continue;
            }
            if (snapshot.CollectedAt == DateTime.MinValue)
            {
                row.Status = Loc.T("尚未采集");
                continue;
            }
            if (snapshot.Failures.TryGetValue(row.Id, out string? failure) && !string.IsNullOrEmpty(failure))
            {
                row.Status = Loc.F("失败：{0}", failure.Length > 40 ? failure[..40] + "…" : failure);
                continue;
            }

            snapshot.SourceCounts.TryGetValue(row.Id, out int count);
            row.Status = count > 0 ? Loc.F("收录 {0} 条", count) : Loc.T("没有数据");
        }
    }

    private void RefreshSuggestionStats()
    {
        if (SgStatsText == null) return;

        var service = _host?.Suggestions;
        if (service == null)
        {
            SgStatsText.Text = Loc.T("建议服务未启动（诊断模式下不启用）。");
            return;
        }

        /* 已隐藏列表：文件在前、文件夹在后，均按路径排序，避免每次刷新顺序乱跳。 */
        _hiddenRows.Clear();
        foreach (string path in service.HiddenFiles().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            _hiddenRows.Add(new HiddenRow(Loc.F("文件  {0}", path), path, false));
        foreach (string path in service.HiddenFolders().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            _hiddenRows.Add(new HiddenRow(Loc.F("文件夹  {0}", path), path, true));
        SgHiddenList.ItemsSource = null;
        SgHiddenList.ItemsSource = _hiddenRows.Select(r => r.Display).ToList();

        /* 一条都没隐藏时不要把 96px 的空框摆在那里 —— 看着像"列表加载失败"。
           空提示代替它，有内容再把列表放回来。 */
        bool hasHidden = _hiddenRows.Count > 0;
        if (SgHiddenEmpty != null)
            SgHiddenEmpty.Visibility = hasHidden ? Visibility.Collapsed : Visibility.Visible;
        if (SgHiddenList != null)
            SgHiddenList.Visibility = hasHidden ? Visibility.Visible : Visibility.Collapsed;

        var snapshot = service.Snapshot;
        RefreshSuggestionSourceStates(snapshot);
        if (snapshot.CollectedAt == DateTime.MinValue)
        {
            SgStatsText.Text = Loc.T("还没采集完：建议会在后台线程里补上，Qbar 这边不会等它。");
            return;
        }

        SgStatsText.Text = Loc.F("上次采集：展示 {0} 条 / 原始 {1} 条，用时 {2} ms。来源明细见上表；某个来源失败只影响它自己。",
            snapshot.Items.Count, snapshot.RawCount, snapshot.CollectMs);
    }

    private void OnTryClick(object sender, RoutedEventArgs e) => _host?.ShowQbar();

    private sealed class EntryVm : System.ComponentModel.INotifyPropertyChanged
    {
        public EntryVm(QEntry entry, bool loadIcon, bool pinned = false, bool isDefaultEngine = false)
        {
            Entry = entry;
            /* 「显示在附加菜单」只对固定项有意义：别的标签里没有附加菜单这一说，
             * 显示出来只会让人以为勾了会把搜索引擎塞进菜单。 */
            MiddleMenuVisibility = pinned ? Visibility.Visible : Visibility.Collapsed;
            DefaultBadgeVisibility = isDefaultEngine ? Visibility.Visible : Visibility.Collapsed;

            /* 网站/搜索引擎显示品牌图标；启动程序列表不预取图标——
             * 程序图标只在 Qbar 搜到 exe/lnk 时按需提取，
             * 避免打开设置页时逐个读 90 多个 exe 拖慢界面。 */
            if (loadIcon)
            {
                try
                {
                    Icon = IconCache.ForEntry(entry);

                    /* 网址条目：先同步问一次磁盘 favicon 缓存。
                     * 有就直接作为第一帧显示 —— 之前这里只有异步回调，
                     * 于是每次进「常用网址」都先画首字、再闪成真图标。 */
                    if (Icon == null) Icon = IconCache.GetWebIcon(entry.Value);
                }
                catch { Icon = null; }
            }
            else
            {
                /* 启动程序列表：先显示首字母，图标在后台解析完再替换。
                 * 这样既有“本地有图标就用图标”的效果，又不会卡住设置页。 */
                Task.Run(() =>
                {
                    ImageSource? image = null;
                    try { image = IconCache.ForEntry(entry); } catch { }
                    if (image == null) return;
                    Application.Current?.Dispatcher.BeginInvoke(() => ApplyIcon(image));
                });
            }

            LetterBrush = new SolidColorBrush(ColorFor(entry.Key));
            Initial = string.IsNullOrWhiteSpace(entry.Key) ? "?"
                : entry.Key[..1].ToUpperInvariant();

            if (Icon == null && (entry.Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                 || entry.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                IconCache.LoadWebIconAsync(entry.Value, image => Application.Current?.Dispatcher.BeginInvoke(() => ApplyIcon(image)));
            }
        }

        private void ApplyIcon(ImageSource? image)
        {
            if (image == null) return;
            Icon = image;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasIconVisibility)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(NoIconVisibility)));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public QEntry Entry { get; }

        /// <summary>这一条会不会出现在中键菜单里（只有固定项能勾）。</summary>
        public bool ShowInMiddleMenu => Entry.ShowInMiddleMenu;

        /// <summary>勾选框的可见性：只有「固定项」标签里才显示。</summary>
        public System.Windows.Visibility MiddleMenuVisibility { get; }

        /// <summary>「默认」徽标的可见性：默认搜索引擎那一行（通常是必应）才有。</summary>
        public System.Windows.Visibility DefaultBadgeVisibility { get; }
        public string Key => Entry.Key;
        public string Value => Entry.Value;
        public string Comment => Entry.DisplayName;   // 默认引擎那条的备注要跟着语言走

        /// <summary>图标（可能为 null）。</summary>
        public System.Windows.Media.ImageSource? Icon { get; private set; }

        /// <summary>没有图标时显示的首字母。</summary>
        public string Initial { get; }

        /// <summary>首字母色块的背景色（按关键字取稳定颜色）。</summary>
        public System.Windows.Media.Brush LetterBrush { get; }

        /// <summary>有图标时显示图片；没有图标时隐藏，避免和首字母色块叠在一起。</summary>
        public System.Windows.Visibility HasIconVisibility =>
            Icon == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        /// <summary>有图标时隐藏色块。</summary>
        public System.Windows.Visibility NoIconVisibility =>
            Icon == null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private static Color ColorFor(string text)
        {
            // 稳定配色：同一关键字永远同一个颜色，不随列表顺序变
            unchecked
            {
                int h = 17;
                foreach (char c in text) h = h * 31 + c;
                var palette = new[]
                {
                    Color.FromRgb(0x2B, 0x6C, 0xF6), Color.FromRgb(0x7C, 0x5C, 0xE0),
                    Color.FromRgb(0x1F, 0xA8, 0x9C), Color.FromRgb(0x2E, 0xA0, 0x4F),
                    Color.FromRgb(0xD9, 0x8A, 0x1F), Color.FromRgb(0xD4, 0x4A, 0x5B),
                };
                return palette[Math.Abs(h) % palette.Length];
            }
        }
    }
}
/// <summary>例外程序列表里的一行。单独一个公开属性类型是给 WPF 绑定用的（匿名类型绑不上）。</summary>
public sealed class AttachExceptionRow
{
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
}