using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using InputPlus.Core.Gestures;
using InputPlus.Core.Icons;
using InputPlus.Core.Localization;
using InputPlus.Core.Scripting;

namespace InputPlus.UI.Windows;

/// <summary>
/// 弹出菜单里的一行（v1.6.7 起也是"一级一级往下钻"的节点）。
///
/// 单独包一层是因为 WPF 的数据绑定只认公共类型的公共属性，
/// 而 <see cref="GestureMenuItem"/> 上没有"序号""兜底后的标题""解析好的图标"
/// "这一行是第几级""能不能展开"这些**纯显示/纯交互**的东西。
///
/// ── 为什么要有 <see cref="EntryKind"/> ────────────────────────────
/// 一行点下去到底该干什么，全看它是什么：
///   · <see cref="EntryKind.Action"/>：执行动作（老行为，菜单关掉后交给宿主）；
///   · <see cref="EntryKind.Folder"/>：在资源管理器里打开这个文件夹（**跳转优先于展开**）；
///   · <see cref="EntryKind.Parent"/>：没有动作的父项（「历史记录」「命令」），点了只展开；
///   · <see cref="EntryKind.Loading"/> / <see cref="EntryKind.Error"/> / <see cref="EntryKind.Note"/>：
///     懒加载的占位、失败重试、以及"没有子文件夹""只显示前 400 个"这类说明。
///
/// ── 为什么箭头是"能不能展开"的唯一出口 ─────────────────────────
/// 用户明确要求：叶子文件夹**不显示箭头**、悬停也不出面板。
/// 于是 <see cref="ArrowVisibility"/> 只由 <see cref="CanExpand"/> 决定，
/// 而 CanExpand 对文件夹只看"有没有子文件夹"（三态：未知也先给箭头，
/// 让用户点开看到真实错误，而不是被静默当成叶子）。
/// </summary>
public sealed class GestureMenuEntry : INotifyPropertyChanged
{
    /// <summary>一行的种类（决定点击行为）。</summary>
    internal enum EntryKind
    {
        /// <summary>普通动作行：选中后交给宿主执行。</summary>
        Action = 0,

        /// <summary>文件夹行：点击 = 在资源管理器里打开。</summary>
        Folder = 1,

        /// <summary>只有子项的父行（「历史记录」）：点击 = 展开。</summary>
        Parent = 2,

        /// <summary>懒加载占位：正在读这一级目录。</summary>
        Loading = 3,

        /// <summary>读取失败：点一下重试。</summary>
        Error = 4,

        /// <summary>说明行（没有子文件夹 / 条数已截断），不可点。</summary>
        Note = 5,
    }

    /// <summary>文件夹这一级的加载状态。</summary>
    internal enum FolderLoadState
    {
        None = 0,
        Loading = 1,
        Loaded = 2,
        Failed = 3,
    }

    /// <summary>静态子项最多这么多个（老限制：再多按不到，也实在装不下）。</summary>
    internal const int MaxStaticChildren = 12;

    public GestureMenuEntry(GestureMenuItem item, int index, string title, bool deferIcon = false)
    {
        Item = item;
        Title = title;
        Glyph = (item.Glyph ?? "").Trim();
        Note = (item.Note ?? "").Trim();
        /* 序号不再显示（用户要求）：1..9 的快捷选中仍然有效，只是不在行首写数字。 */
        Index = "";
        /* 图标按"有则显示"排优先级：真实图标 > 字形 > 序号。
         * 三者互斥，模板里各绑一个 Visibility，取反在现场算好，XAML 里不用写转换器。
         * deferIcon：文件夹条目**先不取**（几百条 shell 查询不能压在 UI 线程上），
         * 由窗口后台补齐 —— 补齐前显示字形兜底，回来后原地换成真图标。 */
        if (!deferIcon) Icon = ResolveIcon(item.IconPath ?? "", item.Action);
        NoteVisibility = Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        /* 初始的灰/亮照搬条目自己的 Enabled（构造时就已经定好了：见 MiddleMenuPolicy.Build）。 */
        _isEnabled = item.Enabled;

        /* 静态子项：老菜单里的二级（历史记录 / 命令）。
         * 深度不再限制在两层 —— 每一级照样能继续往下（v1.6.7）。 */
        foreach (GestureMenuItem child in (item.Children ?? new List<GestureMenuItem>())
                     .Take(MaxStaticChildren)
                     .Where(c => c != null))
        {
            string childTitle = string.IsNullOrWhiteSpace(child.Title) ? "" : child.Title.Trim();
            if (childTitle.Length == 0) continue;
            Children.Add(new GestureMenuEntry(child, Children.Count, childTitle));
        }

        Kind = Children.Count > 0 && string.IsNullOrWhiteSpace(item.Action)
            ? EntryKind.Parent
            : EntryKind.Action;

        Children.CollectionChanged += (_, _) =>
        {
            Raise(nameof(CanExpand));
            Raise(nameof(ArrowVisibility));
            Raise(nameof(AccessibleStatus));
        };
    }

    // ── 数据 ────────────────────────────────────────────────────────

    /// <summary>原始条目，选中后要交回给调用方。</summary>
    public GestureMenuItem Item { get; }

    /// <summary>1..9 的序号；第 10 条以后是空串。</summary>
    public string Index { get; }

    private string _title = "";
    private string _note = "";

    /// <summary>行上显示的文字：Title，或兜底的动作标题（读取出错时会换成错误说明）。</summary>
    public string Title
    {
        get => _title;
        private set
        {
            if (_title == value) return;
            _title = value;
            Raise(nameof(Title));
            Raise(nameof(AccessibleLabel));
            Raise(nameof(ExpandLabel));
        }
    }

    private Visibility _noteVisibility;
    private bool _isEnabled;
    private bool _isError;

    /// <summary>第二行小字（网址标题 / 路径 / 错误说明），空串表示不显示。</summary>
    public string Note
    {
        get => _note;
        private set
        {
            if (_note == value) return;
            _note = value;
            Raise(nameof(Note));
            NoteVisibility = _note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Raise(nameof(AccessibleHint));
        }
    }

    private ImageSource? _icon;

    /// <summary>
    /// 解析好的真实图标（文件 / 文件夹 / 程序 / 网址 favicon）；没有就是 null。
    ///
    /// 文件夹条目（二级起）**不在构造时取**：那里的路径来自目录枚举，一次可能几百条，
    /// 而取 shell 图标要走一遍 shell —— 在 UI 线程上做就是"菜单卡一下"。
    /// 先按 null 建出来（显示字形兜底），由窗口在后台线程补齐，
    /// 回来后走这个属性的通知原地换成资源管理器的图标（见 <see cref="SetIcon"/>）。
    /// </summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            Raise(nameof(Icon));
            Raise(nameof(IconVisibility));
            Raise(nameof(GlyphVisibility));
            Raise(nameof(IndexVisibility));
        }
    }

    public Visibility IconVisibility => _icon != null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GlyphVisibility => _icon == null && Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IndexVisibility => _icon == null && Glyph.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>还缺一个真实图标吗（后台补图标时用）。</summary>
    internal bool NeedsIcon => _icon == null && !string.IsNullOrWhiteSpace(Item.IconPath);

    /// <summary>后台线程取回来的图标（已 Freeze，可跨线程）贴到这一行上。</summary>
    internal void SetIcon(ImageSource? icon)
    {
        if (icon != null) Icon = icon;
    }

    public Visibility NoteVisibility
    {
        get => _noteVisibility;
        private set { _noteVisibility = value; Raise(nameof(NoteVisibility)); }
    }

    /// <summary>行首的图标字形，空串表示没设图标。</summary>
    public string Glyph
    {
        get => _glyph;
        private set
        {
            if (_glyph == value) return;
            _glyph = value;
            Raise(nameof(Glyph));
            Raise(nameof(GlyphVisibility));
            Raise(nameof(IndexVisibility));
        }
    }

    private string _glyph = "";

    // ── 层级与下钻 ──────────────────────────────────────────────────

    /// <summary>这一行在第几级（0 = 一级菜单）。无障碍标签与点击路由都靠它。</summary>
    public int Level { get; private set; }

    internal void SetLevel(int level)
    {
        if (Level == level) return;
        Level = level;
        Raise(nameof(AccessibleLabel));
        Raise(nameof(AccessibleStatus));
    }

    /// <summary>一行的种类。文件夹行会在这里从 Action 升级成 Folder（探测回来后）。</summary>
    internal EntryKind Kind { get; set; } = EntryKind.Action;

    /// <summary>可展开的文件夹路径；不是文件夹就是 null。</summary>
    internal string? FolderPath { get; set; }

    /// <summary>
    /// 这个文件夹里有没有子文件夹（三态）。
    /// <c>null</c> = 还没探出来（没权限 / 网络超时 / 待探测）—— 此时**照样给箭头**。
    /// </summary>
    internal bool? HasSubfolders
    {
        get => _hasSubfolders;
        set
        {
            if (_hasSubfolders == value) return;
            _hasSubfolders = value;
            Raise(nameof(CanExpand));
            Raise(nameof(ArrowVisibility));
            Raise(nameof(AccessibleStatus));
        }
    }

    private bool? _hasSubfolders;

    /// <summary>按"有没有下一级"决定能不能展开 / 要不要画箭头。</summary>
    public bool CanExpand => Kind switch
    {
        /* 文件夹只看"有没有子文件夹"：已经知道是空的（false）就绝不再弹面板 ——
         * 用户要求叶子悬停不展开、不显示箭头。三态里的 null（没权限/超时）
         * 照样给箭头，让用户点开看到真实错误。 */
        EntryKind.Folder => _hasSubfolders != false,
        EntryKind.Parent => Children.Count > 0,
        EntryKind.Error => RetryTarget != null,
        _ => false,
    };

    /// <summary>右侧展开箭头要不要显示（只由 <see cref="CanExpand"/> 决定，不另存字段）。</summary>
    public Visibility ArrowVisibility => CanExpand ? Visibility.Visible : Visibility.Collapsed;

    private bool _isExpanded;

    /// <summary>当前这一级的下一级是不是开着（只用于高亮与无障碍状态）。</summary>
    internal bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise(nameof(AccessibleStatus));
        }
    }

    /// <summary>二级菜单的内容（静态子项 + 悬停展开文件夹后追加的条目）。</summary>
    public ObservableCollection<GestureMenuEntry> Children { get; } =
        new ObservableCollection<GestureMenuEntry>();

    /// <summary>下一级现在有几个条目（自检与无障碍标签用）。</summary>
    public int ChildCount => Children.Count;

    // ── 文件夹的懒加载状态 ──────────────────────────────────────────

    internal FolderLoadState LoadState { get; set; }

    /// <summary>读取失败的说明（给用户看的一句话）。</summary>
    internal string LoadError { get; set; } = "";

    /// <summary>"重试"要重读哪个文件夹（错误行指回它所属的文件夹行）。</summary>
    internal GestureMenuEntry? RetryTarget { get; set; }

    /// <summary>这一行是不是已经开始过"是不是文件夹"的探测（避免重复 IO）。</summary>
    internal bool Probed { get; set; }

    /// <summary>开始读这个文件夹：先塞一行"正在读取…"占位。</summary>
    internal void BeginFolderLoad()
    {
        LoadState = FolderLoadState.Loading;
        LoadError = "";
        Children.Clear();
        Children.Add(new GestureMenuEntry(
            new GestureMenuItem { Title = Loc.T("正在读取…"), Enabled = false },
            0, Loc.T("正在读取…"))
        {
            Kind = EntryKind.Loading,
            Level = Level + 1,
        });
        Raise(nameof(AccessibleStatus));
    }

    /// <summary>把一次枚举结果灌进下一级（必须在 UI 线程调用）。</summary>
    internal void ApplyListing(FolderTree.Listing listing)
    {
        Children.Clear();
        LoadError = "";

        if (!listing.Ok)
        {
            LoadState = FolderLoadState.Failed;
            LoadError = DescribeFailure(listing);

            /* 错误行**自己**标红（不是父项）：父项的名字还是对的，
             * 要红的是"这个目录读不出来"这一句。点它 = 重试。 */
            var errorRow = new GestureMenuEntry(
                new GestureMenuItem { Title = Loc.F("{0} · 点此重试", LoadError), Enabled = true },
                0, Loc.F("{0} · 点此重试", LoadError))
            {
                Kind = EntryKind.Error,
                Level = Level + 1,
                RetryTarget = this,
                HasSubfolders = true,
            };
            errorRow.SetError(null, error: true);
            Children.Add(errorRow);

            /* 读不出来时保留箭头（三态里的"未知"）：用户还能再悬停/点击重试，
             * 而不是被当成一个空文件夹从此不再展开。 */
            HasSubfolders = null;
            Raise(nameof(AccessibleStatus));
            return;
        }

        foreach (FolderTree.Item item in listing.Items)
        {
            Children.Add(FromFolder(item, Level + 1));
        }

        if (listing.Truncated)
        {
            Children.Add(new GestureMenuEntry(
                new GestureMenuItem { Title = Loc.F("只显示前 {0} 个", FolderTree.MaxChildren), Enabled = false },
                0, Loc.F("只显示前 {0} 个", FolderTree.MaxChildren))
            {
                Kind = EntryKind.Note,
                Level = Level + 1,
            });
        }
        else if (listing.Items.Count == 0)
        {
            Children.Add(new GestureMenuEntry(
                new GestureMenuItem { Title = Loc.T("（没有子文件夹）"), Enabled = false },
                0, Loc.T("（没有子文件夹）"))
            {
                Kind = EntryKind.Note,
                Level = Level + 1,
            });
        }

        LoadState = FolderLoadState.Loaded;
        HasSubfolders = listing.Items.Count > 0 ? true : false;
        Raise(nameof(AccessibleStatus));
    }

    /// <summary>一条目录 → 菜单行（只建对象，不碰磁盘）。</summary>
    internal static GestureMenuEntry FromFolder(FolderTree.Item item, int level)
    {
        var model = new GestureMenuItem
        {
            Title = item.Name,
            /* 图标留空、路径给出：真图标由窗口在**后台**取（shell 那一套，和资源管理器一致），
             * 每行一次 shell 查询压在 UI 线程上就是把菜单卡住。 */
            IconPath = item.Path,
            Action = "openFile",
            Arg = item.Path,
            Enabled = true,
        };

        return new GestureMenuEntry(model, 0, item.Name, deferIcon: true)
        {
            Kind = EntryKind.Folder,
            FolderPath = item.Path,
            HasSubfolders = item.HasSubfolders,
            Level = level,
            Probed = true,
        };
    }

    private static string DescribeFailure(FolderTree.Listing listing) => listing.Failure switch
    {
        FolderTree.FailureKind.Denied => Loc.T("无权访问"),
        FolderTree.FailureKind.Missing => Loc.T("目录已不存在"),
        FolderTree.FailureKind.Unreachable => Loc.T("网络位置不可达"),
        _ => Loc.T("读取失败"),
    };

    // ── 外观状态 ────────────────────────────────────────────────────

    /// <summary>
    /// 这一行现在能不能点。
    ///
    /// **必须**是带通知的属性（外面模板的 0.4 透明度绑在它上面）：
    /// 附加菜单顶部「打开文件所在位置」在弹菜单那一刻还不知道有没有文件，
    /// 预热（识别）回来后要把灰/亮**原地**刷出来 —— 绑 <see cref="GestureMenuItem.Enabled"/>
    /// 那种普通属性是刷不动的（菜单已经上屏，不重建 ItemsSource 就永远显示初始状态）。
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set { _isEnabled = value; Raise(nameof(IsEnabled)); }
    }

    /// <summary>错误态（红色文字）：读取失败、调起失败都走它。</summary>
    public bool IsError
    {
        get => _isError;
        private set
        {
            if (_isError == value) return;
            _isError = value;
            Raise(nameof(IsError));
        }
    }

    /// <summary>
    /// 灰/亮一起改两处：这里（管外观）与 <see cref="GestureMenuItem.Enabled"/>（管点击，
    /// <c>Accept</c> 里会再判一次）。两处必须同步，否则会出现"看着是灰的、点下去却执行了"。
    /// </summary>
    internal void SetEnabled(bool on)
    {
        Item.Enabled = on;
        if (_isEnabled != on) IsEnabled = on;
    }

    /// <summary>调起资源管理器期间：置灰 + 不可点，防止抖手点两下开两个窗口。</summary>
    internal void SetBusy(bool busy)
    {
        SetEnabled(!busy);
        if (busy) Raise(nameof(AccessibleStatus));
    }

    /// <summary>换第二行小字（预热回来补文件名 / 调起失败补原因）。</summary>
    internal void UpdateNote(string note) => Note = (note ?? "").Trim();

    /// <summary>把星标换成实心（已经在固定项里了）。</summary>
    internal void UpdateFavoriteGlyph(bool favorited)
    {
        Glyph = favorited ? "\uE735" : "\uE734";
    }

    /// <summary>读取/调起失败时给一行红字；恢复时清掉。</summary>
    internal void SetError(string? message, bool error = true)
    {
        IsError = error;
        if (error && !string.IsNullOrWhiteSpace(message)) UpdateNote(message!);
    }

    /// <summary>原来的标题（失败提示会临时把它换掉，重试时要能还原）。</summary>
    private string? _savedTitle;

    /// <summary>
    /// 调起资源管理器失败：把原因**显示在这一行上**（绝不静默失败）。
    ///
    /// 一级条目有第二行，原因写在那里、标题不动；
    /// 子级条目只有标题一行（用户要求子级不写第二行），所以直接把标题换成原因 ——
    /// 宁可暂时看不出这一行原来是哪个文件夹，也不能"点了没反应"。
    /// </summary>
    internal void ShowFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        IsError = true;
        UpdateNote(message);
        if (Level <= 0) return;

        _savedTitle ??= Title;
        Title = message;
    }

    /// <summary>重试前把上一次的失败提示清掉（标题还原）。</summary>
    internal void ClearFailure()
    {
        IsError = false;
        if (_savedTitle == null) return;
        Title = _savedTitle;
        _savedTitle = null;
        UpdateNote("");
    }

    // ── 无障碍 ──────────────────────────────────────────────────────
    //
    // WPF 没有 role / aria-*，对应的是 UI Automation：
    //   · 列表 → ControlType.List（≈ role=menu）；
    //   · 行   → ControlType.ListItem（≈ role=menuitem）；
    //   · Name / HelpText / ItemStatus ≈ aria-label / aria-description / aria-expanded。
    // 这三个属性在行容器的样式里绑到 ListBoxItem 上（见 GestureMenuWindow.xaml 的 MenuRow）。

    /// <summary>读屏念的第一句："名字，第 N 级"。</summary>
    public string AccessibleLabel => Loc.F("{0}，第 {1} 级", Title, Level + 1);

    /// <summary>补充说明：路径或备注。</summary>
    public string AccessibleHint => FolderPath is { Length: > 0 } folder
        ? folder
        : (Note.Length > 0 ? Note : "");

    /// <summary>当前状态：可展开 / 已展开 / 读取中 / 读取失败。</summary>
    public string AccessibleStatus
    {
        get
        {
            if (Kind == EntryKind.Loading) return Loc.T("正在读取");
            if (Kind == EntryKind.Error || LoadState == FolderLoadState.Failed) return Loc.T("读取失败");
            if (!CanExpand) return "";
            return IsExpanded ? Loc.T("已展开") : Loc.T("可展开");
        }
    }

    /// <summary>展开按钮的无障碍名字（箭头按钮绑它）。</summary>
    public string ExpandLabel => Loc.F("展开「{0}」的下一级", Title);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── 图标 ────────────────────────────────────────────────────────

    /// <summary>
    /// 图标解析：网址取 favicon，其余当磁盘路径取 shell 图标。
    ///
    /// 解析失败一律返回 null（回落到字形/序号）—— 菜单不能因为一个图标没取到就缺一块。
    /// 网址没缓存时顺手排一次后台下载，下次打开菜单就有图标了。
    /// </summary>
    private static ImageSource? ResolveIcon(string iconPath, string action)
    {
        string p = iconPath.Trim();
        if (p.Length == 0) return null;
        try
        {
            bool isUrl = p.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(action, "webPage", StringComparison.OrdinalIgnoreCase);

            if (isUrl)
            {
                var web = IconCache.GetWebIcon(p);
                if (web == null) IconCache.LoadWebIconAsync(p, _ => { });
                return web;
            }

            return IconCache.FromFile(p) ?? IconCache.FromExecutable(p);
        }
        catch { return null; }
    }
}
