using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using InputPlus.Core.Icons;
using InputPlus.Core.Qbar;

namespace InputPlus.UI.Windows;

/// <summary>
/// 「收藏到 Qbar」对话框（v1.5.0）。
///
/// 附加菜单右上角那颗星标点下去之后弹的就是它：目标路径 / 备注 / 关键字都已经按
/// 当前场景（网页、资源管理器目录、文件对话框目录、桌面、程序）填好，
/// 用户觉得哪里不合适就改，改完点「收藏」写进 Qbar 的固定项。
///
/// 为什么字段是"先空着、随后补上"：
///   读地址栏是 UIA（跨进程 COM）、读目录是 Shell COM，两者都可能要几百毫秒。
///   窗口先出来，用户看到的是"它正在读"，而不是"点了星标之后什么都没发生"。
///   已经动手改过的字段不会被随后到达的自动值覆盖（<see cref="_touched"/>）。
/// </summary>
public partial class FavoriteCaptureWindow : Window
{
    private readonly FavoriteTarget _target;
    private readonly IReadOnlyList<string> _taken;
    private bool _applying;          // 正在用代码写控件：TextChanged 不该算"用户改过"
    private bool _closed;

    /// <summary>用户动过哪几个输入框。动过的字段不再被自动值覆盖。</summary>
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

    /// <summary>解析出来的草稿（图标来源、来源类型从这里取）。</summary>
    private FavoriteDraft? _draft;

    /// <param name="target">中键菜单弹出时抓到的上下文快照。</param>
    /// <param name="takenKeywords">已经占用的关键字（自动去重时用）。</param>
    /// <param name="showInMiddleMenu">「在附加菜单里显示」的初始状态。</param>
    public FavoriteCaptureWindow(FavoriteTarget target, IEnumerable<string>? takenKeywords, bool showInMiddleMenu = true)
    {
        ArgumentNullException.ThrowIfNull(target);

        InitializeComponent();

        _target = target;
        _taken = (takenKeywords ?? Array.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ShowInMenuBox.IsChecked = showInMiddleMenu;
        SourceText.Text = target.SourceLabel;
        Title = Loc.T("收藏到 Qbar");

        /* 标题栏跟随主题：系统标题栏默认浅色，深色界面里会突兀地顶一条白。
         * 必须在 HWND 建好之后调，所以挂在 SourceInitialized 上。 */
        SourceInitialized += (_, _) => ApplyChrome();
        Theme.ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            Theme.ThemeManager.ThemeChanged -= OnThemeChanged;
        };

        Loaded += OnLoaded;
    }

    /// <summary>用户点「收藏」之后的结果；取消时为 null。</summary>
    public FavoriteDraft? Result { get; private set; }

    /// <summary>前台程序正在打开的那个文件（识别不到就是 null）。</summary>
    public string? OpenFile { get; private set; }

    private string? _openFile;
    private string _openFileOwner = "";
    private bool _openFileIsCache;

    /// <summary>用户点的是「收藏这个文件」而不是「收藏」（此时 <see cref="Result"/> 为 null）。</summary>
    public bool FileRequested { get; private set; }

    /// <summary>
    /// 识别回来了：按**收藏按钮的优先级**决定"这次到底收哪个"。
    ///
    ///   ① 浏览器里的网页 → 网址（文件只作为额外一条显示）
    ///   ② 资源管理器 / 文件对话框 / 桌面 → 文件夹（不受影响）
    ///   ③ 那个程序手里开着一个文件 → **直接改判成这个文件**（目标/备注/关键字全换）
    ///   ④ 都不是 → 程序
    ///
    /// 判定本身在 <see cref="FavoriteResolver.PreferFile"/> 里（纯函数，自检逐条钉住），
    /// 这里只负责"把结论填进控件"。
    ///
    /// 用户已经动过的字段不会被覆盖：<see cref="Write"/> 认 <see cref="_touched"/>，
    /// 所以正在手打路径的人不会被打断 —— 他只是看到剩下的字段被补齐。
    /// </summary>
    /// <param name="path">识别到的文件完整路径。</param>
    /// <param name="processName">它是哪个程序打开的（只用于来源那行小字）。</param>
    public void ProvideOpenFile(string? path, string? processName = "", bool fromCache = false)
    {
        if (_closed) return;

        string clean = (path ?? "").Trim();
        if (clean.Length == 0) return;

        _openFile = clean;
        _openFileOwner = (processName ?? "").Trim();
        _openFileIsCache = fromCache;

        /* 缓存副本要说清楚：路径里那串 hash 目录会让人以为走错了地方。 */
        if (fromCache) ShowStatus(Loc.T("这是 WPS 云文档的本地缓存副本，原文件不在本机。"));

        /* 还没解析完：先记下来，等 FillAsync 拿到草稿时一起判（避免两次 Apply 打架）。 */
        if (_draft == null) return;

        if (!FavoriteResolver.PreferFile(_draft.Source, _draft.Target, hasFile: true))
        {
            ShowOpenFileHint(clean);     // 网页/文件夹优先：只多给一条入口
            return;
        }

        OpenFileStrip.Visibility = Visibility.Collapsed;
        SourceText.Text = Loc.T("文件") + (_openFileOwner.Length > 0 ? " · " + Path.GetFileNameWithoutExtension(_openFileOwner) : "");
        Apply(FavoriteResolver.FileDraft(clean, _taken));
        if (_openFileIsCache) ShowStatus(Loc.T("这是 WPS 云文档的本地缓存副本，原文件不在本机。"));
    }

    /// <summary>只显示"它正在打开：xxx + 收藏这个文件"那条（网页/文件夹之外的那条额外入口）。</summary>
    private void ShowOpenFileHint(string path)
    {
        string name = "";
        try { name = System.IO.Path.GetFileName(path); } catch { name = path; }
        OpenFileText.Text = Loc.F("它正在打开：{0}", name);
        OpenFileText.ToolTip = path;
        OpenFileStrip.Visibility = Visibility.Visible;
    }

    private void OnFavoriteOpenFile(object sender, RoutedEventArgs e)
    {
        if (_openFile == null) return;
        FileRequested = true;
        DialogResult = true;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        FocusField();
        await FillAsync();
    }

    /// <summary>后台把上下文补成草稿，再回界面线程填字段。</summary>
    private async Task FillAsync()
    {
        FavoriteDraft draft;
        try
        {
            draft = await Task.Run(() => FavoriteResolver.Resolve(_target, _taken));
        }
        catch (Exception ex)
        {
            Core.CoreLog.Swallow(ex, "收藏：解析上下文");
            ShowStatus(Loc.T("没能读取当前页面的信息，请手动填写。"));
            return;
        }

        if (_closed) return;

        /* 文件比"程序"更贴近用户想收的东西 —— 但只在网址/文件夹都不成立时才顶上来
         * （判定顺序见 FavoriteResolver.PreferFile）。 */
        if (_openFile != null && FavoriteResolver.PreferFile(draft.Source, draft.Target, hasFile: true))
        {
            OpenFileStrip.Visibility = Visibility.Collapsed;
            SourceText.Text = Loc.T("文件")
                + (_openFileOwner.Length > 0 ? " · " + System.IO.Path.GetFileNameWithoutExtension(_openFileOwner) : "");
            Apply(FavoriteResolver.FileDraft(_openFile, _taken));
            if (_openFileIsCache) ShowStatus(Loc.T("这是 WPS 云文档的本地缓存副本，原文件不在本机。"));
            return;
        }

        Apply(draft);
    }
    private void Apply(FavoriteDraft draft)
    {
        _draft = draft;

        Write(TargetBox, "target", draft.Target);
        Write(CommentBox, "comment", draft.Comment);
        Write(KeywordBox, "keyword", draft.Keyword);

        string iconPath = (draft.IconPath ?? "").Trim();
        IconPathText.Text = iconPath.Length == 0 ? "" : Loc.T("图标：") + iconPath;
        ApplyIcon(iconPath);

        if (!string.IsNullOrWhiteSpace(draft.Status)) ShowStatus(draft.Status);
        else HideStatus();

        /* 自动值已经把目标路径填上了，但用户可能想直接改备注 ——
         * 焦点给"第一个还算空"的字段：备注空着就给备注，否则给目标路径。 */
        FocusField();
    }

    /// <summary>写一个输入框，除非用户已经动过它。</summary>
    private void Write(System.Windows.Controls.TextBox box, string field, string value)
    {
        if (_touched.Contains(field)) return;
        _applying = true;
        try { box.Text = value ?? ""; }
        finally { _applying = false; }
    }

    private void FocusField()
    {
        if (string.IsNullOrWhiteSpace(CommentBox.Text)) { CommentBox.Focus(); CommentBox.SelectAll(); return; }
        TargetBox.Focus();
        TargetBox.CaretIndex = TargetBox.Text.Length;
    }

    private void OnFieldChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_applying) return;
        if (ReferenceEquals(sender, TargetBox)) _touched.Add("target");
        else if (ReferenceEquals(sender, CommentBox)) _touched.Add("comment");
        else if (ReferenceEquals(sender, KeywordBox)) _touched.Add("keyword");
    }

    // ── 图标预览 ───────────────────────────────────────────────────

    private void ApplyIcon(string iconPath)
    {
        ImageSource? image = null;
        try
        {
            if (LooksLikeUrl(iconPath))
            {
                image = IconCache.GetWebIcon(iconPath);
                if (image == null)
                {
                    /* 没缓存就排一次后台下载，回来再换图 —— 不阻塞对话框。 */
                    IconCache.LoadWebIconAsync(iconPath, loaded =>
                        Dispatcher.BeginInvoke(new Action(() => SetIconImage(loaded))));
                }
            }
            else if (iconPath.Length > 0)
            {
                image = IconCache.FromFile(iconPath) ?? IconCache.FromExecutable(iconPath);
            }
        }
        catch (Exception ex)
        {
            Core.CoreLog.Swallow(ex, "收藏：取图标");
        }
        SetIconImage(image);
    }

    private void SetIconImage(ImageSource? image)
    {
        if (_closed) return;
        IconPreview.Source = image;
        IconPreview.Visibility = image != null ? Visibility.Visible : Visibility.Collapsed;
        IconFallback.Visibility = image != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool LooksLikeUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // ── 状态行 ─────────────────────────────────────────────────────

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideStatus() => StatusText.Visibility = Visibility.Collapsed;

    // ── 收尾 ───────────────────────────────────────────────────────

    private void OnOk(object sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void Accept()
    {
        string target = (TargetBox.Text ?? "").Trim();
        if (target.Length == 0)
        {
            ShowStatus(Loc.T("目标路径不能为空。"));
            TargetBox.Focus();
            return;
        }

        string comment = (CommentBox.Text ?? "").Trim();
        if (comment.Length == 0) comment = FallbackComment(target);

        string fallbackKey = _draft != null && !string.IsNullOrWhiteSpace(_draft.FallbackKeyword)
            ? _draft.FallbackKeyword
            : FavoriteKeywords.FromName(comment);
        string keyword = FavoriteKeywords.Sanitize(KeywordBox.Text, fallbackKey);

        /* 手改过的关键字如果刚好撞上别的条目，这里再让一步：
         * 不去偷偷改用户写的字，只是把"新加的这一条"挪开一格。 */
        if (!_touched.Contains("keyword")) keyword = FavoriteKeywords.Unique(keyword, _taken);

        Result = new FavoriteDraft
        {
            Source = _target.Source,
            Target = target,
            Comment = comment,
            Keyword = keyword,
            /* 图标沿用解析结果；解析没给出图标（读不到地址栏之类）就用目标本身：
             * 网址取 favicon、路径取 shell 图标，两条路 Qbar 都认。 */
            IconPath = string.IsNullOrWhiteSpace(_draft?.IconPath) ? target : _draft!.IconPath.Trim(),
            FallbackKeyword = fallbackKey,
        };

        DialogResult = true;
    }

    /// <summary>备注留空时用它：网址给域名，路径给最后一段。</summary>
    private static string FallbackComment(string target)
    {
        if (LooksLikeUrl(target)) return Core.Actions.UrlUtil.HostOf(target);
        string leaf = FavoriteResolver.LeafOf(target);
        return leaf.Length == 0 ? target : leaf;
    }

    /// <summary>「在附加菜单里显示」的当前状态。</summary>
    public bool ShowInMiddleMenu => ShowInMenuBox.IsChecked == true;

    // ── 主题 ───────────────────────────────────────────────────────

    private void ApplyChrome()
    {
        try
        {
            bool dark = Theme.ThemeManager.Current == Theme.ThemeManager.Mode.Dark
                        || (Theme.ThemeManager.Current == Theme.ThemeManager.Mode.System
                            && Theme.ThemeManager.IsDarkSystem);
            Theme.WindowChrome.Apply(this, dark, Theme.WindowChrome.BackdropType.None);
        }
        catch
        {
            // 只是观感，失败不影响功能
        }
    }

    private void OnThemeChanged(bool dark) => Dispatcher.BeginInvoke(ApplyChrome);
}
