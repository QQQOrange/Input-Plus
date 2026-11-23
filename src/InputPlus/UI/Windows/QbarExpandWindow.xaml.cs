using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Icons;
using InputPlus.Core.Qbar.Search;

namespace InputPlus.UI.Windows;

/// <summary>
/// 展开后的文件搜索窗口：左边筛选（类型 + 时间桶），右边名称/路径两列表。
/// 数据来自同一个 <see cref="QbarSearchEngine"/>，所以翻译层、优先级规则、
/// 索引后端在这里原样生效 —— 这里只是换了呈现方式，
/// 不是另一条搜索路径。
/// </summary>
public partial class QbarExpandWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _debounce;
    private List<Row> _all = new List<Row>();
    private List<Row> _view = new List<Row>();
    private int _seq;
    private string _typeKey = "all";
    private string _timeKey = "all";

    /* 排序列：默认 "rank"（语义层的相关度序，和 Qbar 面板里看到的顺序一致）。
     * 点表头切成别的列；点第三次回到相关度序 —— 用户改完自己的排序总要能绕回来。 */
    private string _sortKey = "rank";
    private bool _sortDesc;
    private int _total;
    private int _hidden;

    public QbarExpandWindow(AppHost host, string? query)
    {
        _host = host;
        InitializeComponent();

        /* 圆角、深色标题栏、跟随系统明暗 —— 三件事都在 AttachTo 里：
         * 设 DWMWCP_ROUND（Win11 才真的把窗口画成圆角）、DWMWA_USE_IMMERSIVE_DARK_MODE，
         * 并在 ThemeManager.ThemeChanged 时重刷一次。
         * 以前这个窗口完全没接，深色主题下顶着一圈浅色系统边框、四角还是直角。 */
        InputPlus.UI.Theme.WindowChrome.AttachTo(this, "Brush.Background");

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunQuery(); };

        QueryBox.Text = query ?? "";
        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            QueryBox.CaretIndex = QueryBox.Text.Length;
            RunQuery();
        };
        SizeChanged += (_, _) => FitPathColumn();
        ResultList.AddHandler(GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler(OnHeaderClick));

        /* 图标：虚拟化列表只有"已生成的容器"才有行对象，所以容器生成完和滚动时
         * 各补一轮，而不是一次性给两千行排队 —— 那样后台 STA 线程会被塞爆，
         * 而且大多数请求对应的行用户根本不会看到。 */
        ResultList.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ResultList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                RequestVisibleIcons();
        };
        ResultList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => RequestVisibleIcons()));
    }

    /// <summary>
    /// 行数据。日期/大小在后台线程就格式化成字符串，UI 线程不再算。
    ///
    /// 这里必须是 **public 类型 + public 属性**：WPF 绑定靠反射取属性，
    /// private 嵌套类会绑定失败并静默退回 ToString()，界面上就变成一列
    /// "InputPlus.UI.Windows.QbarExpandWindow+Row"。字段（field）同样绑不上，
    /// 所以一个字段都不能留。
    /// </summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public string Folder { get; set; } = "";
        public string Glyph { get; set; } = "\uE8A5";
        public string DateText { get; set; } = "";
        public string SizeText { get; set; } = "";
        public string TypeKey { get; set; } = "other";
        public string TimeKey { get; set; } = "older";

        /* 排序要原始值：日期列显示的是 "2026-07-08" 字符串，按字符串排会得到
         * "2026-1-9 > 2026-07-08" 这种鬼结果；大小列也一样（"10 MB" vs "9 KB"）。 */
        public long SizeBytes { get; set; }
        public int DayValue { get; set; } = -1;
        public int Rank { get; set; }

        /// <summary>名字按命中区间切好的段，XAML 用附加属性铺成 Inlines（同名不写死颜色）。</summary>
        public IReadOnlyList<QbarTitleSegment> Segments { get; set; } = new List<QbarTitleSegment>();

        /* 图标走 Shell（IShellItemImageFactory / 系统图像列表），跟资源管理器同一套。
         * 异步解析：行容器生成后才去要，取回来再通知界面换图 —— 2000 行同时请求
         * 会把后台线程塞满，滚动时只处理真正可见的那几十行。 */
        public ImageSource? IconSource { get; private set; }
        public bool HasIcon => IconSource != null;
        internal bool IconRequested;

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void SetIcon(ImageSource image)
        {
            IconSource = image;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
        }
    }

    public sealed class FacetVm
    {
        public string Key { get; set; } = "all";
        public string Glyph { get; set; } = "";
        public string Label { get; set; } = "";
        public int Count { get; set; }
        public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _debounce.Stop();
        RunQuery();
    }

    private void RunQuery()
    {
        string text = (QueryBox.Text ?? "").Trim();
        int seq = ++_seq;

        if (text.Length == 0)
        {
            _all = new List<Row>();
            _view = new List<Row>();
            _total = 0;
            _hidden = 0;
            BuildFacets();
            ApplyFilter();
            StatusText.Text = Loc.T("输入关键词开始搜索");
            return;
        }

        QbarSearchOptions options = _host.Settings.Qbar.Search ?? new QbarSearchOptions();
        int limit = Math.Clamp(options.MaxCandidates, 200, 20000);

        Task.Run(() =>
        {
            var ctx = QbarSearchEngine.FromSettings(_host.Settings);
            QbarSearchOutcome outcome = QbarSearchEngine.Run(ctx, text, limit);

            var rows = new List<Row>(outcome.Hits.Count);
            long today = QbarSyntaxValues.ToDay(DateTime.Now);
            for (int i = 0; i < outcome.Hits.Count; i++)
            {
                QbarSearchHit hit = outcome.Hits[i];
                rows.Add(new Row
                {
                    Path = hit.Path,
                    Name = hit.Name,
                    Segments = QbarTitleHighlight.Segments(hit.Name, hit.Highlights),
                    Folder = FolderOf(hit),
                    Glyph = hit.IsDir ? "\uE8B7" : "\uE8A5",
                    DateText = FormatDay(hit.Day),
                    SizeText = hit.IsDir ? "" : FormatSize(hit.SizeBytes),
                    SizeBytes = hit.IsDir ? -1 : hit.SizeBytes,
                    DayValue = hit.Day,
                    Rank = i,
                    TypeKey = TypeOf(hit),
                    TimeKey = TimeOf(hit.Day, today),
                });
            }

            Dispatcher.Invoke(() =>
            {
                if (seq != _seq) return;
                _all = rows;
                _total = outcome.RawCount;
                _hidden = outcome.HiddenCount;
                BuildFacets();
                ApplyFilter();

                if (outcome.TimedOut) StatusText.Text = Loc.T("查询超时，结果可能不完整");
            });
        });
    }

    private static string FolderOf(QbarSearchHit hit)
    {
        try
        {
            string? dir = Path.GetDirectoryName(hit.Path);
            return dir ?? "";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    /// <summary>
    /// 只写日期：索引快照按"天"存修改时间，写成 "yyyy-MM-dd 00:00" 会让用户
    /// 以为那个 00:00 是真时间。宁可少给，也别给假精度。
    /// </summary>
    private static string FormatDay(int day)
    {
        if (day < 0 || day == QbarSyntaxValues.UnknownDay) return "";
        try { return QbarSyntaxValues.FromDay(day).ToString("yyyy-MM-dd", CultureInfo.CurrentCulture); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    private static string FormatSize(long bytes)
    {
        /* 快照按 KB 向下取整，小于 1KB 的文件在这里就是 0。
         * 写「<1 KB」比留空诚实：留空看着像"这条没有大小信息"。 */
        if (bytes <= 0) return "<1 KB";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? ((long)value).ToString(CultureInfo.CurrentCulture) + " B"
                         : value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static string TypeOf(QbarSearchHit hit)
    {
        if (hit.IsDir) return "folder";
        string ext = Path.GetExtension(hit.Path);
        if (ext.StartsWith(".")) ext = ext.Substring(1);
        if (Has(QbarFilterCatalog.DocExtensions, ext)) return "doc";
        if (Has(QbarFilterCatalog.PicExtensions, ext)) return "pic";
        if (Has(QbarFilterCatalog.VideoExtensions, ext)) return "video";
        if (Has(QbarFilterCatalog.AudioExtensions, ext)) return "audio";
        return "other";
    }

    private static bool Has(string list, string ext)
    {
        if (ext.Length == 0) return false;
        return (";" + list + ";").IndexOf(";" + ext + ";", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string TimeOf(int day, long today)
    {
        if (day < 0 || day == QbarSyntaxValues.UnknownDay) return "older";
        long delta = today - day;
        if (delta <= 1) return "d1";
        if (delta <= 7) return "d7";
        if (delta <= 30) return "d30";
        if (delta <= 365) return "d365";
        return "older";
    }

    /// <summary>类型/时间桶计数。只统计本次候选窗口，窗口被截断时底部会写明。</summary>
    private void BuildFacets()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _all.Count; i++)
        {
            Row row = _all[i];
            Bump(counts, row.TypeKey);
            Bump(counts, row.TimeKey);
            Bump(counts, "all");
        }

        int Get(string key) => counts.TryGetValue(key, out int n) ? n : 0;

        var types = new List<FacetVm>
        {
            new FacetVm { Key = "all", Glyph = "\uE8B7", Label = Loc.T("全部"), Count = Get("all") },
            new FacetVm { Key = "folder", Glyph = "\uE8B7", Label = Loc.T("文件夹"), Count = Get("folder") },
            new FacetVm { Key = "doc", Glyph = "\uE8A5", Label = Loc.T("文档"), Count = Get("doc") },
            new FacetVm { Key = "pic", Glyph = "\uEB9F", Label = Loc.T("图片"), Count = Get("pic") },
            new FacetVm { Key = "video", Glyph = "\uE714", Label = Loc.T("视频"), Count = Get("video") },
            new FacetVm { Key = "audio", Glyph = "\uE8D6", Label = Loc.T("音频"), Count = Get("audio") },
            new FacetVm { Key = "other", Glyph = "\uE7C3", Label = Loc.T("其他"), Count = Get("other") },
        };

        var times = new List<FacetVm>
        {
            new FacetVm { Key = "all", Label = Loc.T("全部"), Count = Get("all") },
            new FacetVm { Key = "d1", Label = Loc.T("最近 1 天"), Count = Get("d1") },
            new FacetVm { Key = "d7", Label = Loc.T("最近 7 天"), Count = Get("d7") },
            new FacetVm { Key = "d30", Label = Loc.T("最近 30 天"), Count = Get("d30") },
            new FacetVm { Key = "d365", Label = Loc.T("最近 365 天"), Count = Get("d365") },
            new FacetVm { Key = "older", Label = Loc.T("更早"), Count = Get("older") },
        };

        int typeIndex = types.FindIndex(x => x.Key == _typeKey);
        int timeIndex = times.FindIndex(x => x.Key == _timeKey);

        TypeList.ItemsSource = types;
        TimeList.ItemsSource = times;
        TypeList.SelectedIndex = typeIndex < 0 ? 0 : typeIndex;
        TimeList.SelectedIndex = timeIndex < 0 ? 0 : timeIndex;
    }

    private static void Bump(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out int n);
        counts[key] = n + 1;
    }

    private void ApplyFilter()
    {
        var view = new List<Row>(_all.Count);
        for (int i = 0; i < _all.Count; i++)
        {
            Row row = _all[i];
            if (_typeKey != "all" && row.TypeKey != _typeKey) continue;
            if (_timeKey != "all" && row.TimeKey != _timeKey) continue;
            view.Add(row);
        }

        SortRows(view);
        _view = view;
        ResultList.ItemsSource = view;
        if (view.Count > 0) ResultList.SelectedIndex = 0;
        RequestVisibleIcons();

        bool filtered = _typeKey != "all" || _timeKey != "all";
        ClearFilterButton.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        ClearFilterText.Text = Loc.T("清除筛选") + " (1)";
        FitPathColumn();

        string status;
        if (_total > view.Count)
            status = Loc.F("{0} 个项目（已列出 {1} 条）", _total.ToString("N0", CultureInfo.CurrentCulture),
                           view.Count.ToString("N0", CultureInfo.CurrentCulture));
        else
            status = Loc.F("{0} 个项目", view.Count.ToString("N0", CultureInfo.CurrentCulture));

        if (_hidden > 0)
            status += " · " + Loc.F("已按优先级规则隐藏 {0} 条", _hidden.ToString(CultureInfo.CurrentCulture));

        StatusText.Text = status;
    }

    /// <summary>
    /// 排序。始终以 Rank 作第二键，保证同值条目不会在重新筛选时乱跳 ——
    /// 用户按日期排序时，同一天的文件本该保持相关度顺序，而不是每次都换一遍。
    /// </summary>
    private void SortRows(List<Row> rows) => SortRows(rows, _sortKey, _sortDesc);

    /// <summary>排序本体。抽成静态是为了让自检能直接喂假数据验，不用真起窗口。 </summary>
    internal static void SortRows(List<Row> rows, string key, bool desc)
    {
        if (key == "rank") return;      // _all 本来就是相关度序，别再排一遍

        Comparison<Row> cmp = key switch
        {
            "name" => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            "folder" => (a, b) => string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase),
            "date" => (a, b) => a.DayValue.CompareTo(b.DayValue),
            "size" => (a, b) => a.SizeBytes.CompareTo(b.SizeBytes),
            _ => (a, b) => a.Rank.CompareTo(b.Rank),
        };

        rows.Sort((a, b) =>
        {
            int n = desc ? cmp(b, a) : cmp(a, b);
            return n != 0 ? n : a.Rank.CompareTo(b.Rank);
        });
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Role == GridViewColumnHeaderRole.Padding) return;

        string key = header.Column == null ? ""
            : header.Column == NameColumn ? "name"
            : header.Column == PathColumn ? "folder"
            : header.Column == DateColumn ? "date"
            : header.Column == SizeColumn ? "size"
            : "";

        if (key.Length == 0) return;

        if (_sortKey != key)
        {
            _sortKey = key;
            /* 日期和大小用户要的第一眼几乎一定是"最大的/最新的"；
             * 名字和路径则相反 —— 从 A 开始往下读才顺。 */
            _sortDesc = key is "date" or "size";
        }
        else if (!_sortDesc)
        {
            _sortDesc = true;
        }
        else
        {
            _sortKey = "rank";
            _sortDesc = false;
        }

        UpdateHeaderTitles();
        ApplyFilter();
    }

    /// <summary>渲染用：把窗口切到某个排序列再截图，验证表头箭头与行序。</summary>
    internal void SortForRender(string key, bool desc)
    {
        _sortKey = key;
        _sortDesc = desc;
        UpdateHeaderTitles();
        ApplyFilter();
    }

    private void UpdateHeaderTitles()
    {
        NameColumn.Header = TitleWithArrow(Loc.T("名称"), "name");
        PathColumn.Header = TitleWithArrow(Loc.T("路径"), "folder");
        DateColumn.Header = TitleWithArrow(Loc.T("修改日期"), "date");
        SizeColumn.Header = TitleWithArrow(Loc.T("大小"), "size");
    }

    private string TitleWithArrow(string title, string key)
        => _sortKey == key ? title + (_sortDesc ? " ▼" : " ▲") : title;

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeList.SelectedItem is FacetVm vm)
        {
            _typeKey = vm.Key;
            ApplyFilter();
        }
    }

    private void OnTimeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeList.SelectedItem is FacetVm vm)
        {
            _timeKey = vm.Key;
            ApplyFilter();
        }
    }

    private void OnClearFilter(object sender, RoutedEventArgs e)
    {
        _typeKey = "all";
        _timeKey = "all";
        TypeList.SelectedIndex = 0;
        TimeList.SelectedIndex = 0;
        ApplyFilter();
    }

    /// <summary>
    /// 给"当前真的生成了容器"的行要 Shell 图标。缓存命中就同步换，
    /// 没命中交给 ShellIcons 的后台线程，回来再派发到 UI 线程。
    /// </summary>
    private void RequestVisibleIcons()
    {
        if (ResultList.ItemsSource == null) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pixels = ShellIcons.PickPixels(18, dpi);
        for (int i = 0; i < ResultList.Items.Count; i++)
        {
            if (ResultList.ItemContainerGenerator.ContainerFromIndex(i) is not ListViewItem item) continue;
            if (item.DataContext is not Row row || row.IconRequested) continue;
            row.IconRequested = true;

            ImageSource? cached = ShellIcons.TryGetCached(row.Path, pixels, dpi);
            if (cached != null)
            {
                row.SetIcon(cached);
                continue;
            }

            Row target = row;
            ShellIcons.Request(row.Path, pixels, dpi, image =>
            {
                if (image == null) return;
                Dispatcher.BeginInvoke(new Action(() => target.SetIcon(image)));
            });
        }
    }

    private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelected();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_typeKey != "all" || _timeKey != "all") OnClearFilter(sender, e);
            else Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            OpenSelected();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F5)
        {
            RunQuery();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (ResultList.SelectedItem is Row row) Clipboard.SetText(row.Path);
            e.Handled = true;
        }
    }

    private void OpenSelected()
    {
        if (ResultList.SelectedItem is not Row row) return;
        try
        {
            Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "打开文件");
            StatusText.Text = Loc.F("打不开：{0}", row.Path);
        }
    }

    /// <summary>
    /// 路径列吃掉剩余宽度：窗口拉宽时别让路径还挤在一个定值里，也别在右边留一条空白。
    /// 减掉的 34 是竖直滚动条 + 行左右内边距，跟 Name/Date/Size 三列一起凑满整行。
    /// </summary>
    private void FitPathColumn()
    {
        if (PathColumn == null) return;
        double available = ResultList.ActualWidth - 280 - 118 - 84 - 34;
        PathColumn.Width = Math.Max(160, available);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
