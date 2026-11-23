using System;
using System.Collections.Generic;
using System.IO;
using InputPlus.Core.Actions;
using InputPlus.Core.FileDialog;
using InputPlus.Core.Interop;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;

namespace InputPlus.Core.Qbar;

/// <summary>这次收藏的是"什么东西"。决定对话框里怎么取默认值。</summary>
public enum FavoriteSource
{
    Unknown = 0,

    /// <summary>浏览器里的当前网页。</summary>
    Browser = 1,

    /// <summary>资源管理器里正在浏览的文件夹。</summary>
    Folder = 2,

    /// <summary>「打开 / 保存 / 选文件夹」对话框当前所在的文件夹。</summary>
    Dialog = 3,

    /// <summary>某个程序本身（取它的 exe）。</summary>
    Application = 4,

    /// <summary>系统桌面（桌面本身也是一个文件夹）。</summary>
    Desktop = 5,

    /// <summary>某个程序当前打开着的那个文件（v1.5.2）。</summary>
    File = 6,
}

/// <summary>
/// 中键菜单弹出那一刻的**上下文快照**（v1.5.0）。
///
/// 为什么必须是"快照"而不是点星标时再去问：
///   · 菜单一弹出，前台就变成我们自己的窗口了 —— 指针、焦点、前台三者都不再指向
///     用户真正在看的那个程序，事后再问只能问出一堆"InputPlus.exe"；
///   · 读地址栏 / 读目录都要跨进程（UIA、Shell COM），几十到几百毫秒，
///     不能放在弹菜单这条路径上。
/// 所以弹菜单时只记几个句柄与类名（全是几十微秒的 Win32 查询），
/// 其余的留给收藏对话框在后台慢慢读。
/// </summary>
public sealed class FavoriteTarget
{
    /// <summary>命中窗口（可能是子窗口，例如 Chromium 的渲染区）。</summary>
    public IntPtr Hwnd { get; init; }

    /// <summary>它的顶层窗口。</summary>
    public IntPtr Root { get; init; }

    public string ClassName { get; init; } = "";

    public string RootClass { get; init; } = "";

    /// <summary>所属进程的 exe 全路径（取不到就是空串）。</summary>
    public string ExePath { get; init; } = "";

    /// <summary>进程名（小写，含 .exe）。</summary>
    public string ProcessName { get; init; } = "";

    /// <summary>窗口标题（跨进程读，可能为空）。</summary>
    public string WindowTitle { get; init; } = "";

    public FavoriteSource Source { get; init; }

    /// <summary>给对话框顶上那行小字用："网页 · Chrome"。</summary>
    public string SourceLabel => Source switch
    {
        FavoriteSource.Browser => Loc.T("网页") + " · " + PrettyExe,
        FavoriteSource.Folder => Loc.T("文件夹") + " · " + PrettyExe,
        FavoriteSource.Dialog => Loc.T("文件对话框") + " · " + PrettyExe,
        FavoriteSource.Desktop => Loc.T("桌面"),
        FavoriteSource.Application => Loc.T("程序") + " · " + PrettyExe,
        FavoriteSource.File => Loc.T("文件") + " · " + PrettyExe,
        _ => Loc.T("未知来源"),
    };

    private string PrettyExe =>
        string.IsNullOrWhiteSpace(ProcessName) ? Loc.T("未知程序") : Path.GetFileNameWithoutExtension(ProcessName);

    /// <summary>
    /// 保证快照可用：空的 / 没有 exe 的，就从锚点窗口重建一份（v1.5.2）。
    ///
    /// 抽成纯函数是为了能被自检直接喂（`Unknown + 合法句柄 → 认出来`、
    /// `Unknown + 0 → 原样返回`）。这一步曾经写在界面回调里，而回调是排队执行的，
    /// 锚点在那之前就被清掉了 —— 症状就是 WPS 里点星标，对话框写"没认出这次是在哪里点的收藏"。
    /// </summary>
    internal static FavoriteTarget EnsureUsable(FavoriteTarget target, IntPtr anchor)
    {
        if (target == null) return Empty;
        if (target.Source != FavoriteSource.Unknown && target.ExePath.Length > 0) return target;

        return FromWindow(anchor) ?? target;
    }
    /// <summary>
    /// 直接从**一个已知窗口**造快照（v1.5.2）。
    ///
    /// 为什么要这条：菜单一弹出来前台就变成我们自己了，`Capture` 依赖"弹菜单那一刻的前台/
    /// 指针"，在某些路径上会拿到空上下文（实测 WPS 里点星标，对话框说"没认出这次是在哪里点的收藏"）。
    /// 而菜单那边其实一直留着**弹出来之前那个窗口的句柄**（`ForegroundFiles.AnchorHwnd`，
    /// 活 5 秒）。拿它重建一份，星标就不会再"什么都不认识"。
    /// </summary>
    internal static FavoriteTarget? FromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        try
        {
            IntPtr root = SafeRoot(hwnd);
            if (root == IntPtr.Zero) return null;

            string cls = SafeClassName(hwnd);
            string rootCls = SafeClassName(root);
            NativeMethods.GetWindowThreadProcessId(root, out uint pid);
            string exe = pid == 0 ? "" : WindowService.GetProcessPath(pid);
            string exeName = exe.Length == 0 ? "" : Path.GetFileName(exe).ToLowerInvariant();

            FavoriteSource source;
            if (MiddleMenuSurfaces.IsExplorerRoot(rootCls)) source = FavoriteSource.Folder;
            else if (string.Equals(rootCls, "Progman", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(rootCls, "WorkerW", StringComparison.OrdinalIgnoreCase)) source = FavoriteSource.Desktop;
            else if (string.Equals(rootCls, "#32770", StringComparison.OrdinalIgnoreCase)
                     && MiddleMenuSurfaces.IsFileDialogWindow(root)) source = FavoriteSource.Dialog;
            else if (BrowserPageProbe.LooksLikeBrowser(exeName, rootCls)) source = FavoriteSource.Browser;
            else source = FavoriteSource.Application;

            CoreLog.Info($"[收藏] 上下文从锚点重建：hwnd=0x{hwnd.ToInt64():X} class={cls}/{rootCls} " +
                         $"proc={exeName} source={source}");

            return new FavoriteTarget
            {
                Hwnd = hwnd,
                Root = root,
                ClassName = cls,
                RootClass = rootCls,
                ExePath = exe,
                ProcessName = exeName,
                WindowTitle = SafeTitle(root),
                Source = source,
            };
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "收藏：从锚点重建上下文");
            return null;
        }
    }
    /// <summary>抓不到任何上下文时的空快照（星标照旧显示，但对话框会让用户手填）。</summary>
    public static readonly FavoriteTarget Empty = new() { Source = FavoriteSource.Unknown };

    /// <summary>
    /// 抓一次上下文。**只做便宜的事**（几次窗口查询 + 一次进程路径查询），
    /// 读网址 / 读目录都留给 <see cref="FavoriteResolver"/> 在后台做。
    /// </summary>
    /// <param name="cursor">指针位置（物理像素）。</param>
    /// <param name="scope">设置里的中键范围（只影响资源管理器）。</param>
    /// <param name="foreground">弹菜单那一刻的前台窗口。</param>
    /// <param name="preferForeground">
    /// true = 键盘唤出：用户"在看"的是前台窗口，哪怕指针停在桌面上；
    /// false = 中键唤出：用户点的就是指针底下那个窗口。
    /// 两种情况都先排除本程序自己的窗口（否则收藏到的永远是 InputPlus.exe）。
    /// </param>
    internal static FavoriteTarget Capture(NativeMethods.POINT cursor, MiddleClickMenuScope scope,
        IntPtr foreground, bool preferForeground = false)
    {
        MiddleSurfaceProbe probe = MiddleMenuSurfaces.AtPoint(cursor, scope);
        bool fgUsable = foreground != IntPtr.Zero && !WindowSummonAnimator.IsOwnProcess(foreground);
        bool useProbe = probe.IsUsable && !(preferForeground && fgUsable);

        IntPtr root;
        string cls, rootCls;
        MiddleSurface surface;

        if (useProbe)
        {
            root = probe.Root;
            cls = probe.ClassName;
            rootCls = probe.RootClass;
            surface = probe.Surface;
        }
        else if (fgUsable)
        {
            root = SafeRoot(foreground);
            cls = SafeClassName(foreground);
            rootCls = SafeClassName(root);
            surface = MiddleSurface.None;
        }
        else
        {
            return new FavoriteTarget
            {
                Hwnd = probe.Hwnd,
                ClassName = probe.ClassName,
                RootClass = probe.RootClass,
                Source = probe.IsDesktop ? FavoriteSource.Desktop : FavoriteSource.Unknown,
            };
        }

        if (root == IntPtr.Zero) return Empty;

        NativeMethods.GetWindowThreadProcessId(root, out uint pid);
        string exe = pid == 0 ? "" : WindowService.GetProcessPath(pid);
        string exeName = exe.Length == 0 ? "" : Path.GetFileName(exe).ToLowerInvariant();
        string title = SafeTitle(root);

        FavoriteSource source;
        if (surface == MiddleSurface.Desktop) source = FavoriteSource.Desktop;
        else if (surface == MiddleSurface.ExplorerItems) source = FavoriteSource.Folder;
        else if (surface == MiddleSurface.FileDialog) source = FavoriteSource.Dialog;
        else if (MiddleMenuSurfaces.IsExplorerRoot(rootCls)) source = FavoriteSource.Folder;
        else if (string.Equals(rootCls, "#32770", StringComparison.OrdinalIgnoreCase)
                 && MiddleMenuSurfaces.IsFileDialogWindow(root)) source = FavoriteSource.Dialog;
        else if (BrowserPageProbe.LooksLikeBrowser(exeName, rootCls)) source = FavoriteSource.Browser;
        else source = FavoriteSource.Application;

        return new FavoriteTarget
        {
            Hwnd = useProbe ? probe.Hwnd : root,
            Root = root,
            ClassName = cls,
            RootClass = rootCls,
            ExePath = exe,
            ProcessName = exeName,
            WindowTitle = title,
            Source = source,
        };
    }

    private static IntPtr SafeRoot(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            return root == IntPtr.Zero ? hwnd : root;
        }
        catch { return hwnd; }
    }

    private static string SafeClassName(IntPtr hwnd)
    {
        try { return WindowService.ClassNameOf(hwnd) ?? ""; }
        catch { return ""; }
    }

    private static string SafeTitle(IntPtr hwnd)
    {
        try { return DialogNative.TitleOf(hwnd); }
        catch { return ""; }
    }
}

/// <summary>
/// 收藏对话框里的四个字段 + 一行状态（v1.5.0）。
///
/// 和 <see cref="QEntry"/> 的对应关系：Target → Value、Comment → Comment、
/// Keyword → Key、IconPath → IconPath。中间隔一层是因为"读不到"要能说明原因，
/// 而 QEntry 上没地方放状态文本。
/// </summary>
public sealed class FavoriteDraft
{
    public FavoriteSource Source { get; init; }

    /// <summary>目标路径（网址 / 文件夹 / exe）。读不到时为空串。</summary>
    public string Target { get; set; } = "";

    /// <summary>备注（网页标题 / 文件夹名 / 软件名）。</summary>
    public string Comment { get; set; } = "";

    /// <summary>关键字（Qbar 里打字命中的那个）。</summary>
    public string Keyword { get; set; } = "";

    /// <summary>图标来源：网址取 favicon，路径取 shell 图标。</summary>
    public string IconPath { get; set; } = "";

    /// <summary>给用户看的一句话（读不到网页地址 / 读不到目录时说明原因）。空串 = 不显示。</summary>
    public string Status { get; set; } = "";

    /// <summary>关键字的兜底候选（用户清空关键字时用它，不至于存成空）。</summary>
    public string FallbackKeyword { get; set; } = "fav";

    /// <summary>组装成 Qbar 固定项。</summary>
    public QEntry ToEntry(bool showInMiddleMenu) => new()
    {
        Key = FavoriteKeywords.Sanitize(Keyword, FallbackKeyword),
        Value = Target.Trim(),
        Comment = Comment.Trim(),
        IconPath = IconPath.Trim(),
        ShowInMiddleMenu = showInMiddleMenu,
        Enabled = true,
    };
}

/// <summary>
/// 把 <see cref="FavoriteTarget"/> 补成 <see cref="FavoriteDraft"/>（v1.5.0）。
///
/// 这里会**跨进程/联网**（UIA 读地址栏、Shell COM 读目录、抓网页标题），
/// 所以只能在后台线程调用；对话框先弹出来，字段随后填。
/// </summary>
internal static class FavoriteResolver
{
    internal static FavoriteDraft Resolve(FavoriteTarget target, IEnumerable<string>? takenKeywords)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.Source switch
        {
            FavoriteSource.Browser => ResolveBrowser(target, takenKeywords),
            FavoriteSource.Folder => ResolveFolder(target, takenKeywords, dialog: false),
            FavoriteSource.Dialog => ResolveFolder(target, takenKeywords, dialog: true),
            FavoriteSource.Desktop => ResolveDesktop(target, takenKeywords),
            FavoriteSource.Application => ResolveApplication(target, takenKeywords),
            _ => new FavoriteDraft
            {
                Source = FavoriteSource.Unknown,
                Status = Loc.T("没认出这次是在哪里点的收藏，请手动填写目标路径。"),
            },
        };
    }

    /// <summary>
    /// 收藏按钮要收哪一个 —— **纯函数，判定顺序是硬的**（用户口径）：
    ///
    ///   ① 浏览器里打开着网页（http/https）→ 收**网址**
    ///   ② 资源管理器 / 文件对话框 / 桌面     → 收**文件夹**
    ///   ③ 那个程序手里正开着一个文件          → 收**这个文件**
    ///   ④ 以上都不是                          → 收**这个程序**
    ///
    /// 所以"文件"只有在**前面两条都不成立**时才顶上去：浏览器里的网页地址比
    /// 它顺带下载到本地的那个文件更接近用户此刻在看的东西；
    /// 而资源管理器窗口根本没有"打开的文件"这回事。
    /// </summary>
    /// <param name="source">快照判出来的落点类型。</param>
    /// <param name="resolvedTarget">已经解析出来的目标（网址/目录）；空串表示还没解析出来。</param>
    /// <param name="hasFile">识别到了"它打开的文件"。</param>
    internal static bool PreferFile(FavoriteSource source, string? resolvedTarget, bool hasFile)
    {
        if (!hasFile) return false;

        switch (source)
        {
            case FavoriteSource.Browser:
                /* 网页优先。只有"根本读不到地址"（或读到的是 file:// 本地文件）才让位。 */
                return string.IsNullOrWhiteSpace(resolvedTarget) || IsLocalFileUrl(resolvedTarget);

            case FavoriteSource.Folder:
            case FavoriteSource.Dialog:
            case FavoriteSource.Desktop:
                return false;

            default:
                return true;
        }
    }

    /// <summary><c>file:///D:/a.pdf</c> 这种地址其实是本地文件，不是网页。</summary>
    internal static bool IsLocalFileUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "它打开的那个文件" → 草稿。备注就是文件名，关键字按文件名规则推导，
    /// 图标用文件自己的 shell 图标 —— 和固定项里其它文件条目完全一致。
    /// </summary>
    internal static FavoriteDraft FileDraft(string path, IEnumerable<string>? taken)
    {
        string clean = RevealPath.Clean(path);
        string name = LeafOf(clean);
        string key = FavoriteKeywords.KeywordFor(name);
        return new FavoriteDraft
        {
            Source = FavoriteSource.File,
            Target = clean,
            Comment = name,
            Keyword = FavoriteKeywords.KeywordForUnique(name, taken),
            FallbackKeyword = key.Length == 0 ? "file" : key,
            IconPath = clean,
        };
    }
    /// <summary>浏览器：地址栏读网址、抓回网页标题当备注、favicon 当图标。</summary>
    private static FavoriteDraft ResolveBrowser(FavoriteTarget target, IEnumerable<string>? taken)
    {
        string? url = BrowserPageProbe.ReadUrl(target.Root != IntPtr.Zero ? target.Root : target.Hwnd);
        string title = CleanPageTitle(target.WindowTitle, target.ProcessName);

        if (string.IsNullOrWhiteSpace(url))
        {
            /* 读不到就留空让用户粘贴 —— 硬猜一个域名比空着更糟。 */
            string fallbackKey = FavoriteKeywords.FromName(title);
            return new FavoriteDraft
            {
                Source = FavoriteSource.Browser,
                Comment = title,
                Keyword = FavoriteKeywords.Unique(fallbackKey, taken),
                FallbackKeyword = fallbackKey,
                Status = Loc.T("没能读到当前网页地址（浏览器没开无障碍接口？），请手动粘贴网址。"),
            };
        }

        /* 网页标题在地址栏里读不到，只能联网抓；抓不到就用窗口标题
         * （Chrome 的窗口标题就是"页面标题 - Google Chrome"）。 */
        string note = WebTitleFetcher.Fetch(url) ?? title;
        if (string.IsNullOrWhiteSpace(note)) note = FavoriteKeywords.FromUrl(url);

        string key = FavoriteKeywords.FromUrl(url);
        return new FavoriteDraft
        {
            Source = FavoriteSource.Browser,
            Target = url,
            Comment = note,
            Keyword = FavoriteKeywords.Unique(key, taken),
            FallbackKeyword = key,
            IconPath = url,
        };
    }

    /// <summary>资源管理器 / 文件对话框：取当前所在目录。</summary>
    private static FavoriteDraft ResolveFolder(FavoriteTarget target, IEnumerable<string>? taken, bool dialog)
    {
        string? path = null;
        try
        {
            path = dialog
                ? QuickSwitchFolderProbe.DialogFolder(target.Root)
                : QuickSwitchFolderProbe.ExplorerFolderOf(target.Root);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：读目录"); }

        if (string.IsNullOrWhiteSpace(path))
        {
            return new FavoriteDraft
            {
                Source = dialog ? FavoriteSource.Dialog : FavoriteSource.Folder,
                Comment = Loc.T("没能读到当前所在的文件夹，请手动填写路径。"),
                Keyword = FavoriteKeywords.Unique("folder", taken),
                FallbackKeyword = "folder",
                Status = Loc.T("没能读到当前所在的文件夹，请手动填写路径。"),
            };
        }

        return FolderDraft(path, taken, dialog ? FavoriteSource.Dialog : FavoriteSource.Folder);
    }

    private static FavoriteDraft ResolveDesktop(FavoriteTarget target, IEnumerable<string>? taken)
    {
        string path = "";
        try { path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：取桌面目录"); }

        if (string.IsNullOrWhiteSpace(path))
            return FolderDraft("", taken, FavoriteSource.Desktop);

        return FolderDraft(path, taken, FavoriteSource.Desktop);
    }

    /// <summary>文件夹 → 草稿。备注用文件夹名、关键字按中英文规则推导、图标用文件夹自己的 shell 图标。</summary>
    private static FavoriteDraft FolderDraft(string path, IEnumerable<string>? taken, FavoriteSource source)
    {
        string name = LeafOf(path);
        string key = FavoriteKeywords.FromName(name);
        return new FavoriteDraft
        {
            Source = source,
            Target = path.Trim(),
            Comment = name,
            Keyword = FavoriteKeywords.Unique(key, taken),
            FallbackKeyword = key,
            /* 图标来源写路径本身：Qbar 会走 SHGetFileInfo 取"资源管理器里看到的那个图标"
             * （含用户自定义的文件夹图标 / 快捷方式叠加箭头），和双击打开时一致。 */
            IconPath = path.Trim(),
        };
    }

    /// <summary>程序：exe 路径 + 软件名 + 软件图标。</summary>
    private static FavoriteDraft ResolveApplication(FavoriteTarget target, IEnumerable<string>? taken)
    {
        string exe = (target.ExePath ?? "").Trim();
        if (exe.Length == 0 || !File.Exists(exe))
        {
            return new FavoriteDraft
            {
                Source = FavoriteSource.Application,
                Comment = string.IsNullOrWhiteSpace(target.WindowTitle) ? target.ProcessName : target.WindowTitle,
                Keyword = FavoriteKeywords.Unique(FavoriteKeywords.FromName(target.ProcessName), taken),
                Status = Loc.T("拿不到这个程序的 exe 路径（提权窗口？），请手动填写。"),
            };
        }

        string name = DisplayNameOf(exe);
        string key = FavoriteKeywords.FromName(name);
        return new FavoriteDraft
        {
            Source = FavoriteSource.Application,
            Target = exe,
            Comment = name,
            Keyword = FavoriteKeywords.Unique(key, taken),
            FallbackKeyword = key,
            IconPath = exe,
        };
    }

    // ── 小工具 ─────────────────────────────────────────────────────

    /// <summary>程序的"软件名称"：优先文件属性里的说明（"微信"），没有才退回文件名。</summary>
    internal static string DisplayNameOf(string exe)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            string desc = (info.FileDescription ?? "").Trim();
            if (desc.Length > 0) return desc;
            string product = (info.ProductName ?? "").Trim();
            if (product.Length > 0) return product;
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：读程序说明"); }

        try { return Path.GetFileNameWithoutExtension(exe); }
        catch { return exe; }
    }

    /// <summary>
    /// 浏览器窗口标题 → 页面标题：剥掉尾巴上的" - Google Chrome"/" — Mozilla Firefox"之类。
    /// 只剥**已知的**浏览器名，不按"最后一个 - "乱切（页面标题里本来就有 <c>-</c>）。
    /// </summary>
    internal static string CleanPageTitle(string? windowTitle, string? exeName)
    {
        string title = (windowTitle ?? "").Trim();
        if (title.Length == 0) return "";

        string[] suffixes =
        {
            " - Google Chrome", " - Chromium", " - Microsoft​ Edge", " - Microsoft Edge",
            " — Mozilla Firefox", " - Mozilla Firefox", " - Firefox",
            " - Brave", " - Vivaldi", " - Opera", " - Opera GX", " - 360安全浏览器",
            " - 360极速浏览器", " - QQ浏览器", " - 搜狗高速浏览器", " - Internet Explorer",
            " - Arc",
        };

        foreach (string suffix in suffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                string cut = title[..^suffix.Length].Trim();
                if (cut.Length > 0) return cut;
            }
        }

        /* 便携版/非主流浏览器的尾巴：形如 " - <exe 名>"，只在它和进程名对得上时才剥。 */
        if (!string.IsNullOrWhiteSpace(exeName))
        {
            string stem = Path.GetFileNameWithoutExtension(exeName);
            foreach (string dash in new[] { " - ", " — ", " – " })
            {
                string tail = dash + stem;
                if (stem.Length > 2 && title.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    string cut = title[..^tail.Length].Trim();
                    if (cut.Length > 0) return cut;
                }
            }
        }

        return title;
    }

    /// <summary>路径最后一段（根目录 "D:\" 取 "D盘"）。</summary>
    internal static string LeafOf(string? path)
    {
        string p = (path ?? "").Trim().TrimEnd('\\', '/');
        if (p.Length == 0) return "";
        try
        {
            string leaf = Path.GetFileName(p);
            if (!string.IsNullOrWhiteSpace(leaf)) return leaf;
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：取目录名"); }

        // "D:" 这种盘根
        if (p.Length == 2 && p[1] == ':') return p[0] + Loc.T(" 盘");
        return p;
    }
}
