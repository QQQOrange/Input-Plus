using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using InputPlus.Core.Interop;
using InputPlus.Core.Suggestions;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Windows;

/// <summary>一次识别出来的"前台程序当前打开的那个文件"。</summary>
/// <param name="Path">完整路径（已剥 <c>\\?\</c>，已确认文件存在）。</param>
/// <param name="ProcessName">前台程序名（小写含 .exe），例如 <c>wpspdf.exe</c>。</param>
/// <param name="Hwnd">识别时用的前台窗口句柄 —— 缓存键的一半。</param>
/// <param name="Pid">前台进程号 —— 缓存键的另一半。</param>
/// <param name="At">识别完成的本地时间（缓存按它算 800ms）。</param>
public sealed record ForegroundFile(string Path, string ProcessName, IntPtr Hwnd, int Pid, DateTime At, bool FromCache = false)
{
    /// <summary>文件名（含扩展名，与资源管理器显示一致）—— 收藏时的备注就是它。</summary>
    public string FileName
    {
        get
        {
            try { return System.IO.Path.GetFileName(Path); } catch { return ""; }
        }
    }
}

/// <summary>识别为什么没成 —— 日志、提示与降级决策都靠它，别只回 null。</summary>
public enum ForegroundFileOutcome
{
    /// <summary>拿到了。</summary>
    Ok = 0,

    /// <summary>前台是资源管理器 / 桌面 / 任务栏：那不是"某个程序打开了文件"。</summary>
    NotActionable = 1,

    /// <summary>打不开目标进程（普通权限碰提权目标、服务、受保护进程）。**静默降级**。</summary>
    Denied = 2,

    /// <summary>能开进程，但句柄表里挑不出像"用户正在看的文件"的东西。</summary>
    NoCandidate = 3,

    /// <summary>800ms 预算用完（浏览器这类几千个句柄的进程会遇到）。</summary>
    Timeout = 4,

    /// <summary>宿主不支持（认得不出 File 的 ObjectTypeIndex 这类）。</summary>
    Unsupported = 5,
}

/// <summary>识别结果。<see cref="File"/> 为 null 时看 <see cref="Outcome"/> 与 <see cref="Why"/>。</summary>
public readonly record struct ForegroundFileResult(ForegroundFile? File, ForegroundFileOutcome Outcome, string Why, int Pid = 0)
{
    internal static ForegroundFileResult Fail(ForegroundFileOutcome outcome, string why, int pid = 0) => new(null, outcome, why, pid);
    internal static ForegroundFileResult Ok(ForegroundFile file) => new(file, ForegroundFileOutcome.Ok, "", file.Pid);
}

/// <summary>扫描时的一个候选（只留打分要用的那几个事实）。</summary>
/// <param name="Path">完整路径。</param>
/// <param name="Rank">扩展名在白名单里的序号（越小越靠前）。</param>
/// <param name="WriteTimeUtc">最后写入时间（UTC）。</param>
/// <param name="InSystemDir">是否落在 %SystemRoot% / Program Files 这类运行时目录下。</param>
internal readonly record struct ForegroundFileCandidate(string Path, int Rank, DateTime WriteTimeUtc, bool InSystemDir)
{
    /// <summary>扩展名在不在白名单里。</summary>
    internal bool Allowed => Rank >= 0;

    /// <summary>文件名（判断"窗口标题里有没有它"用）。</summary>
    internal string FileName
    {
        get
        {
            try { return System.IO.Path.GetFileName(Path); } catch { return ""; }
        }
    }
}

/// <summary>
/// 「前台程序当前打开的文件」识别（v1.5.1）。
///
/// ── 怎么拿 ────────────────────────────────────────────────────────
/// <c>GetForegroundWindow</c> → PID → <c>OpenProcess(QUERY_INFORMATION|DUP_HANDLE)</c>
/// → <c>NtQueryInformationProcess(ProcessHandleInformation /*51*/)</c> 拿**这一个进程**的
/// 句柄快照 → 只对 File 类型的句柄 <c>DuplicateHandle</c> / <c>GetFileType</c> /
/// <c>NtQueryObject</c> 取名 → NT 设备路径转盘符 → 噪声过滤 → 打分取一个。
///
/// ── 三条硬约束（都踩过）────────────────────────────────────────────
/// ① **不用 SystemHandleInformation**：Win11 24H2 起非管理员返回
///    STATUS_ACCESS_DENIED，Process Explorer 要提权就是这条。
/// ② **先探 File 的 ObjectTypeIndex，再逐个取名**：对管道 / 事件 / 同步对象调
///    NtQueryObject 有概率挂在里面不返回，靠类型过滤绕开比加超时可靠。
/// ③ **全部放后台 + 800ms 预算**：浏览器主进程动辄几千句柄，卡在 UI 线程上就是
///    "点一下中键，界面停半秒"。
///
/// ── 提权之后会发生什么 ────────────────────────────────────────────
/// <c>OpenProcess</c> 的权限方向是单向的：提权进程能开普通进程（所以提权后这个功能
/// 反而更准），普通进程开提权进程会被拒（错误码 5 → 静默降级）。
/// 我们**不启用 SeDebugPrivilege**：为了收藏一个文件把调试特权打开，
/// 等于把 SYSTEM 进程也交到这个功能手里，出的错一点都不值。
/// </summary>
internal static class ForegroundFileProbe
{
    /// <summary>识别预算（毫秒）。超过它按"识别失败"处理，绝不把按钮卡住。</summary>
    internal const int BudgetMs = 800;

    /// <summary>一个进程最多处理多少个 File 句柄（Chrome 主进程能到几千，别无限跑）。</summary>
    internal const int MaxFileHandles = 4096;

    /// <summary>每处理多少个句柄查一次取消与预算。</summary>
    private const int CheckEvery = 128;

    /// <summary>
    /// 扩展名白名单，**顺序即权重**（媒体在前）。
    /// 不在表里的一律出局 —— 不做"猜类型"，那必然把 dll / pak 猜进来。
    /// </summary>
    private static readonly string[] WhitelistExtensions =
    {
        // 媒体
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m4v",
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".ape", ".wma",
        // 图片
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".psd", ".heic", ".svg", ".ico",
        // 文档
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".rtf", ".csv", ".epub",
        // 工程
        ".ai", ".fig", ".sketch", ".blend", ".max", ".dwg",
    };

    /// <summary>系统目录下这些扩展名一定是运行时资源，不是"用户打开的文件"。</summary>
    private static readonly string[] RuntimeExtensions =
    {
        ".dll", ".mui", ".pak", ".dat", ".pma", ".clb", ".log", ".ico", ".exe", ".ttf",
    };

    /// <summary>前台是这些进程时，两个按钮一起置灰（那不是在"打开文件"）。</summary>
    private static readonly string[] NotActionableProcesses = { "explorer.exe", "progman.exe", "shellhost.exe" };

    /// <summary>桌面 / 任务栏的窗口类名 —— 它们的宿主也是 explorer，但类名更准。</summary>
    private static readonly string[] NotActionableClasses =
        { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "SHELLDLL_DefView" };

    private static readonly Dictionary<string, int> ExtensionRank = BuildRank();

    private static Dictionary<string, int> BuildRank()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < WhitelistExtensions.Length; i++) map[WhitelistExtensions[i]] = i;
        return map;
    }

    // ================= 纯判定（自检直接喂） =================

    /// <summary>
    /// 这两个入口在前台是这个程序时能不能用。
    ///
    /// 判两条：进程名（explorer.exe）与窗口类名（Progman / Shell_TrayWnd…）——
    /// 桌面和任务栏虽然都挂在 explorer 里，但类名先认出来更稳
    /// （第三方桌面把 Progman 换掉时，仍然是一块"没有文件"的桌面）。
    /// </summary>
    internal static bool CanActOnForegroundFile(string? processName, string? className)
    {
        string proc = (processName ?? "").Trim();
        string cls = (className ?? "").Trim();

        foreach (string c in NotActionableClasses)
            if (string.Equals(cls, c, StringComparison.OrdinalIgnoreCase)) return false;

        if (proc.Length == 0) return false;
        foreach (string p in NotActionableProcesses)
            if (string.Equals(proc, p, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    /// <summary>扩展名 → 白名单序号；不在白名单里返回 -1。</summary>
    internal static int RankOf(string? path)
    {
        string ext = ExtensionOf(path);
        return ext.Length > 0 && ExtensionRank.TryGetValue(ext, out int rank) ? rank : -1;
    }

    /// <summary>
    /// 这条路径该不该被丢掉。全部是"一定会误收藏"的东西：
    /// 目录（**目录句柄的 GetFileType 也是 FILE_TYPE_DISK，光靠它拦不住**）、
    /// 隐藏/临时件、Office/WPS 的锁文件、0 字节占位、系统目录下的运行时资源。
    /// </summary>
    /// <param name="systemRoots">系统目录前缀；传 null 用真实环境（自检传假前缀）。</param>
    internal static bool IsNoise(string? path, uint attributes, long size, IReadOnlyList<string>? systemRoots = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) return true;
        if (size == 0) return true;

        string name = LeafOf(path);
        if (name.Length == 0) return true;
        if (name.StartsWith(".", StringComparison.Ordinal)) return true;      // .tmp / .lock / 隐藏文件
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;     // Office 锁文件
        if (name.StartsWith("~", StringComparison.Ordinal)) return true;      // WPS 临时件

        /* 系统目录下只剩"运行时资源"这一种可能：dll/mui/pak/字体/日志。
         * 不按目录一刀切 —— 用户完全可能真的在编辑 Program Files 里的一个 .md。 */
        if (IsUnderSystemRoot(path, systemRoots ?? SystemRoots) && IsRuntimeExtension(ExtensionOf(path)))
            return true;

        return false;
    }

    /// <summary>
    /// 多个候选里挑一个。
    ///
    /// 顺序（越前面越优先）：
    ///   ① 窗口标题里出现了这个文件名 —— 几乎不会错，直接锁定；
    ///   ② 写得更晚的（正在编辑的文件最新被写）；
    ///   ③ 不在系统目录下的；
    ///   ④ 扩展名白名单里的序号（媒体 &gt; 图片 &gt; 文档 &gt; 工程）；
    ///   ⑤ 路径短的（同分时给个稳定结果，别每次扫出来换一个）。
    ///
    /// 为什么写时间排在"文件类型"前面：WPS 同时开着正稿和一个模板时，
    /// "刚被写过"比"是 pdf 还是 docx"更能说明用户在看哪个。
    /// </summary>
    /// <param name="windowTitle">前台窗口标题（可为空 = 不做标题锁定）。</param>
    internal static string? ChooseBest(IReadOnlyList<ForegroundFileCandidate>? candidates, string? windowTitle)
    {
        if (candidates == null || candidates.Count == 0) return null;

        string title = windowTitle ?? "";
        bool AnyHit(ForegroundFileCandidate c) =>
            title.Length > 0 && c.FileName.Length > 2 &&
            title.IndexOf(c.FileName, StringComparison.OrdinalIgnoreCase) >= 0;

        ForegroundFileCandidate? best = null;
        foreach (ForegroundFileCandidate c in candidates)
        {
            if (!c.Allowed) continue;
            if (best == null) { best = c; continue; }

            if (Better(c, best.Value, AnyHit)) best = c;
        }

        return best?.Path;
    }

    /// <summary>比较两个候选，<paramref name="a"/> 更优返回 true。</summary>
    private static bool Better(ForegroundFileCandidate a, ForegroundFileCandidate b, Func<ForegroundFileCandidate, bool> titleHit)
    {
        bool ha = titleHit(a), hb = titleHit(b);
        if (ha != hb) return ha;

        if (a.WriteTimeUtc != b.WriteTimeUtc) return a.WriteTimeUtc > b.WriteTimeUtc;

        if (a.InSystemDir != b.InSystemDir) return !a.InSystemDir;

        if (a.Rank != b.Rank) return a.Rank < b.Rank;

        return a.Path.Length < b.Path.Length;
    }

    // ================= 真实扫描 =================

    /// <summary>
    /// 识别前台窗口所属进程"正在看的文件"。
    ///
    /// 降级链（每条都记日志，别静默）：
    ///   ① 句柄快照（覆盖 90%）；
    ///   ② 进程命令行里的路径（WPS 是 <c>/prometheus /pdf "D:\x.pdf"</c>，直接能抠出来）；
    ///   ③ 交给调用方（<see cref="ForegroundFiles"/> 还有"同进程上次成功"这一层）。
    /// </summary>
    /// <param name="hwnd">前台窗口句柄 —— **必须是"菜单弹出之前"抓的那个**，不是点击时的。</param>
    internal static ForegroundFileResult Scan(IntPtr hwnd, CancellationToken ct = default, int budgetMs = BudgetMs)
    {
        if (hwnd == IntPtr.Zero) return ForegroundFileResult.Fail(ForegroundFileOutcome.NoCandidate, "没有前台窗口");

        IntPtr target = ResolveRealWindow(hwnd, out string className);
        string processName = ForegroundWatcher.ProcessNameOf(target);

        GetWindowThreadProcessId(target, out uint pid);
        if (pid == 0) return ForegroundFileResult.Fail(ForegroundFileOutcome.NoCandidate, "拿不到前台进程号");

        if (!CanActOnForegroundFile(processName, className))
            return ForegroundFileResult.Fail(ForegroundFileOutcome.NotActionable,
                $"前台是 {processName}/{className}：那不是某个程序打开的文件");

        return ScanProcess((int)pid, ct, budgetMs, hwnd, ForegroundWatcher.TitleOf(target));
    }

    /// <summary>
    /// 按 pid 识别（没有窗口也行）。
    ///
    /// 单独一条入口有两个用途：
    ///   · <c>--foreground-file &lt;pid&gt;</c> 诊断：后台播放器、隐藏控制台的进程根本没有顶层窗口，
    ///     但它们确实持有文件句柄；
    ///   · 窗口属于 broker（UWP / 商店应用）时，按 pid 直扫比按窗口找更不容易空手。
    /// </summary>
    internal static ForegroundFileResult ScanProcess(
        int pid, CancellationToken ct = default, int budgetMs = BudgetMs,
        IntPtr hwnd = default, string? windowTitle = null)
    {
        if (pid <= 0) return ForegroundFileResult.Fail(ForegroundFileOutcome.NoCandidate, "进程号无效");

        string exePath = ProcessExePath(pid);
        string processName = LeafOf(exePath);
        if (processName.Length == 0) processName = "pid" + pid;
        string title = windowTitle ?? "";
        var watch = Stopwatch.StartNew();

        /* ⓪ 微信：图片 / 视频**不在用户文件里**，先去它自己的数据目录找。
         *
         * 为什么放在句柄扫描之前：微信 4.x 默认以管理员身份运行，普通权限连它的进程都
         * 打不开（OpenProcess → 错误 5），扫句柄这条路注定空手；
         * 而同族子进程、命令行、窗口标题（标题只有「微信」）也都不可能给出用户文件。
         * 与其花掉整份 800ms 预算再回 NoCandidate，不如先走这条**只碰文件系统**的路。
         * 详见 <see cref="WeChatMedia"/>。 */
        if (WeChatMedia.IsWeChatProcess(processName) || WeChatMedia.IsWeChatPath(exePath))
        {
            string? media = WeChatMedia.FindCurrent(Math.Min(budgetMs, 400), ct);
            if (media != null) return Ok(media, processName, hwnd, pid, "微信当前图片/视频");
            CoreLog.Info($"[收藏] 前台是微信，但没找到最近的图片/视频：{processName}");
        }

        /* ① 窗口进程自己的句柄表（覆盖 90%，真正的单进程程序一次就中） */
        var (paths, why) = EnumerateOpenFilesOnce(pid, budgetMs, ct);
        string? best = ChooseBest(Score(paths), title);
        if (best != null) return ForegroundFileResult.Ok(new ForegroundFile(best, processName, hwnd, pid, DateTime.Now));

        /* 手里只有"缓存副本"的那种（WPS 云文档）先记下来，作为最后一级兜底：
         * 它不是用户的原文件，但确实是这个程序此刻打开着的那份内容。 */
        string? cacheCandidate = FirstCachePath(paths);
        CoreLog.Info($"[收藏] 句柄快照没挑出候选：pid={pid} proc={processName} 原始命中 {paths.Count} 条（{why}）");

        /* ② 窗口进程的命令行。比扫子进程更可信：命令行里的路径就是用户双击的那个
         * （WPS 云文档除外，那种情况它给的是云端 id）。 */
        string? fromCommand = FirstAccepted(PathsInCommandLine(pid, ct));
        if (fromCommand != null) return Ok(fromCommand, processName, hwnd, pid, "命令行");

        /* ③ 同族进程（父 + 子）。WPS 的文档句柄在 et.exe / wpspdf.exe 里，
         * Edge 的 PDF 渲染在子进程里 —— 只盯窗口 PID 会一无所获（实测）。 */
        foreach (int other in RelatedProcesses(pid))
        {
            if (ct.IsCancellationRequested) break;
            int remaining = budgetMs - (int)watch.ElapsedMilliseconds;
            if (remaining < 120) break;

            string? cmd = FirstAccepted(PathsInCommandLine(other, ct));
            if (cmd != null) return Ok(cmd, processName, hwnd, pid, $"子进程 {other} 命令行");

            var (childPaths, childWhy) = EnumerateOpenFilesOnce(other, remaining, ct);
            string? childBest = ChooseBest(Score(childPaths), title);
            if (childBest != null) return Ok(childBest, processName, hwnd, pid, $"子进程 {other} 句柄");
            cacheCandidate ??= FirstCachePath(childPaths);
            CoreLog.Info($"[收藏] 同族进程 pid={other} 也没挑出：{childPaths.Count} 条（{childWhy}）");
        }

        /* ④ 窗口标题里的文件名 → 最近文档。
         * 有些程序（WPS 从「开始菜单」启动、再在程序里打开文件）既没有路径在命令行里，
         * 手里的也只是云缓存副本；只有标题里那个文件名是真的。
         * 系统「最近使用的文件」里存着用户真正的那个路径（.lnk），按文件名精确匹配即可。 */
        string? fromTitle = RecentPathForTitle(title);
        if (fromTitle != null) return Ok(fromTitle, processName, hwnd, pid, "窗口标题 + 最近文档");

        /* ⑤ 最后一级：它手里只有云缓存副本（WPS 云文档就是这样）。
         * 原文件在本地根本不存在（整盘按名字搜过），拿这份副本至少能让人看见内容在哪儿；
         * 调用方会明确告诉用户"这是缓存副本"，不会假装它是原文件。 */
        if (cacheCandidate != null) return Ok(cacheCandidate, processName, hwnd, pid, "云缓存副本", fromCache: true);

        return ForegroundFileResult.Fail(
            watch.ElapsedMilliseconds >= budgetMs ? ForegroundFileOutcome.Timeout : ForegroundFileOutcome.NoCandidate,
            $"没找到 {processName} 打开的文件（{why}）", pid);
    }

    /// <summary>命中一条可用的结果。</summary>
    private static ForegroundFileResult Ok(string path, string processName, IntPtr hwnd, int pid, string how, bool fromCache = false)
    {
        CoreLog.Info($"[收藏] 命中（{how}）{(fromCache ? "（缓存副本）" : "")}：{path}");
        return ForegroundFileResult.Ok(new ForegroundFile(path, processName, hwnd, pid, DateTime.Now, fromCache));
    }

    /// <summary>原始命中里第一条"缓存目录里的白名单文件"（WPS 云文档的本地工作副本）。</summary>
    internal static string? FirstCachePath(IReadOnlyList<string>? paths)
    {
        if (paths == null) return null;
        foreach (string path in paths)
        {
            if (!IsCachePath(path)) continue;
            if (RankOf(path) < 0) continue;
            long size = SizeOf(path);
            if (size <= 0) continue;
            /* 缓存目录里同样有 ~$ 锁文件：不排掉就会把"锁"当成文档本身。 */
            if (IsNoise(path, AttributesOf(path), size)) continue;
            return RevealPath.Clean(path);
        }
        return null;
    }

    /// <summary>第一条"能当用户文件"的路径（存在、不噪声、白名单内、不是缓存）。</summary>
    private static string? FirstAccepted(IEnumerable<string>? candidates)
    {
        if (candidates == null) return null;
        foreach (string candidate in candidates)
        {
            if (candidate.Length == 0) continue;
            if (IsNoise(candidate, AttributesOf(candidate), SizeOf(candidate))) continue;
            if (RankOf(candidate) < 0) continue;
            if (IsCachePath(candidate)) continue;
            return candidate;
        }
        return null;
    }

    /// <summary>
    /// 同族的其他进程：**子进程优先**（文档/渲染都在子进程里），再父进程。
    /// "同族"按 exe 名判断（<c>wps</c> / <c>wpspdf</c> / <c>et</c> 算一家；<c>msedge</c> 全部同名）。
    /// 最多 6 个，每个都要开句柄表 —— 不设上限会把一次收藏拖成一次全系统体检。
    /// </summary>
    internal static List<int> RelatedProcesses(int pid)
    {
        var result = new List<int>(6);
        try
        {
            var all = SnapshotProcesses();
            int parent = all.FirstOrDefault(p => p.Pid == pid).Parent;

            /* 先看**安装目录**：WPS 的 wps.exe / et.exe / wpp.exe / wpspdf.exe 名字完全不同，
             * 但都在同一个 office6 目录下 —— 只按名字认亲会漏掉真正持有文档的那个进程
             * （实测：窗口是 wps.exe，xlsx 的句柄在 et.exe 里）。 */
            string selfDir = DirOfExe(pid);

            foreach (var p in all)
            {
                if (p.Pid == pid || p.Pid <= 4) continue;
                if (p.Parent != pid) continue;                       // 只取直接子进程

                bool family = SameFamily(StemOfExe(all.FirstOrDefault(x => x.Pid == pid).Name), StemOfExe(p.Name));
                if (!family && selfDir.Length > 0)
                {
                    string childDir = DirOfExe(p.Pid);
                    family = childDir.Length > 0
                             && string.Equals(childDir, selfDir, StringComparison.OrdinalIgnoreCase);
                }
                if (!family) continue;

                result.Add(p.Pid);
                if (result.Count >= 5) break;
            }

            if (parent > 4 && result.Count < 6)
            {
                var parentEntry = all.FirstOrDefault(p => p.Pid == parent);
                if (parentEntry.Pid == parent && SameFamily(StemOfExe(parentEntry.Name), StemOfExe(all.FirstOrDefault(x => x.Pid == pid).Name)))
                    result.Add(parent);
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：枚举同族进程"); }
        return result;
    }

    /// <summary>进程 exe 所在目录（拿不到返回空串）。</summary>
    private static string DirOfExe(int pid)
    {
        string exe = ProcessExePath(pid);
        if (exe.Length == 0) return "";
        try { return Path.GetDirectoryName(exe) ?? ""; } catch { return ""; }
    }
    /// <summary>当前进程列表（pid / 父进程 / exe 名）。</summary>
    internal static List<(int Pid, int Parent, string Name)> SnapshotProcesses()
    {
        var list = new List<(int, int, string)>(256);
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return list;

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return list;
            do
            {
                list.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? ""));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：枚举进程快照"); }
        finally { CloseHandle(snapshot); }
        return list;
    }

    /// <summary>exe 名 → 家族名（去扩展名、小写）。</summary>
    private static string StemOfExe(string? exeName)
    {
        string name = (exeName ?? "").Trim();
        if (name.Length == 0) return "";
        try { name = Path.GetFileNameWithoutExtension(name); } catch { }
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// 两个 exe 名算不算"同一个程序的不同进程"：完全相同，或一方是另一方的前缀且至少 3 个字母
    /// （wps / wpspdf / wpscloudsvr 一家；msedge / identity_helper 不是）。
    /// </summary>
    internal static bool SameFamily(string? a, string? b)
    {
        string x = (a ?? "").Trim(), y = (b ?? "").Trim();
        if (x.Length < 3 || y.Length < 3) return false;
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase)
            || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 窗口标题 → 最近文档里的真实路径。
    ///
    /// 只认"标题第一段恰好是一个白名单扩展名的文件名"，再到
    /// <c>%APPDATA%\Microsoft\Windows\Recent</c> 里找同名的 <c>.lnk</c>（最新的优先），
    /// 用仓库里现成的 <see cref="ShellLinkReader"/> 解出目标路径，并且要求它现在还存在。
    /// 找不到就返回 null —— 这一步是"锦上添花"，绝不能因为猜错把用户带到别处去。
    /// </summary>
    internal static string? RecentPathForTitle(string? windowTitle)
    {
        string name = FileNameFromTitle(windowTitle);
        if (name.Length == 0) return null;
        if (RankOf(name) < 0) return null;   // 只认白名单里的类型（.pdf/.docx/.mp4…）

        try
        {
            string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (recent.Length == 0 || !Directory.Exists(recent)) return null;

            var hits = new List<(string Lnk, DateTime At)>();
            foreach (string lnk in Directory.EnumerateFiles(recent, "*.lnk"))
            {
                string leaf = Path.GetFileNameWithoutExtension(lnk);
                if (!string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase)) continue;
                try { hits.Add((lnk, File.GetLastWriteTimeUtc(lnk))); } catch { }
            }
            if (hits.Count == 0) return OfficeRecentFiles.FindByFileName(name);

            hits.Sort((x, y) => y.At.CompareTo(x.At));
            foreach (var hit in hits)
            {
                string? target = ShellLinkReader.TargetOf(hit.Lnk);
                if (string.IsNullOrWhiteSpace(target)) continue;
                if (!File.Exists(target)) continue;
                if (RankOf(target) < 0) continue;
                CoreLog.Info($"[收藏] 标题里的文件名在最近文档里命中：{target}");
                return target;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：按标题查最近文档"); }

        /* 系统「最近」里没有（WPS 就不往里写），还有 WPS / Office 自己的最近列表。 */
        return OfficeRecentFiles.FindByFileName(name);
    }

    /// <summary>
    /// 窗口标题 → 文件名。
    ///
    /// 两步：
    ///   ① 按 <c>" - "</c> / <c>" — "</c> 切掉尾巴上的程序名；
    ///   ② 再**截到一个白名单扩展名为止** —— 浏览器会往标题里塞别的东西：
    ///      Edge 多标签时是「xxx.pdf 和另外 2 个页面 - 个人 - Microsoft Edge」，
    ///      切完还剩「…pdf 和另外 2 个页面」，按名字查最近文档就永远查不到。
    ///      有了这一步，"文件名后面挂什么"都不影响。
    /// </summary>
    internal static string FileNameFromTitle(string? windowTitle)
    {
        string text = (windowTitle ?? "").Trim();
        if (text.Length == 0) return "";

        foreach (string dash in new[] { " - ", " — ", " – ", "－" })
        {
            int cut = text.IndexOf(dash, StringComparison.Ordinal);
            if (cut > 0) { text = text[..cut]; break; }
        }
        return TrimToFileName(text.Trim());
    }

    /// <summary>
    /// 截到最后一个"白名单扩展名"的结尾。找不到就原样返回（调用方会用 RankOf 判掉）。
    ///
    /// 边界要卡住：扩展名后面必须是空白 / 标点 / 结尾，否则 <c>.md</c>、<c>.ts</c>
    /// 会在普通单词里乱命中（"readme.md5"、"notes.tsx"）。
    /// </summary>
    private static string TrimToFileName(string text)
    {
        int end = -1;
        foreach (string ext in WhitelistExtensions)
        {
            int at = text.LastIndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (at <= 0) continue;
            int stop = at + ext.Length;
            if (stop < text.Length && (char.IsLetterOrDigit(text[stop]) || text[stop] == '_')) continue;
            if (stop > end) end = stop;
        }
        return end > 0 ? text[..end].Trim() : text;
    }
    /// <summary>
    /// 枚举某个进程正在打开的文件（**原始命中，未过滤**，只要名字能转成本地路径）。
    ///
    /// 单独暴露出来是为了自检：扫自己一定成功（不需要任何权限），
    /// 于是"能不能认出文件"这件事与"当前是不是管理员"彻底解耦 ——
    /// 提权/不提权的 CI 都跑同一条断言。
    /// </summary>
    internal static (List<string> Paths, string Why) EnumerateOpenFilesOnce(int pid, int budgetMs, CancellationToken ct = default)
    {
        var result = new List<string>();
        if (pid <= 0) return (result, "进程号无效");

        IntPtr process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            /* 错误码 5：普通权限碰上提权目标 / 服务 / 受保护进程。
             * **静默降级**，不弹错误框也不刷 Warn —— 这在中键菜单里是常态，
             * 每按一次中键刷一条日志只会把真问题淹掉。 */
            return (result, err == 5 ? "目标进程权限更高（错误 5）" : "打不开目标进程（错误 " + err + "）");
        }

        IntPtr snapshot = IntPtr.Zero;
        IntPtr nameBuffer = IntPtr.Zero;
        try
        {
            uint fileTypeIndex = ProbeFileObjectTypeIndex();
            if (fileTypeIndex == uint.MaxValue) return (result, "探不到 File 对象的类型序号");

            int need = 0;
            int status = NtQueryInformationProcess(process, ProcessHandleInformation, IntPtr.Zero, 0, out need);
            if (status != unchecked((int)STATUS_INFO_LENGTH_MISMATCH) && status < 0)
                return (result, $"句柄快照不可用（0x{status:X8}）");

            /* 句柄表在两次调用之间会变（目标进程正开着/关着东西），
             * 所以分配时留 64KB 余量，不够再重来一次 —— 最多三次。 */
            int size = Math.Max(need, 64 * 1024) + 64 * 1024;
            int used = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                snapshot = Marshal.AllocHGlobal(size);
                status = NtQueryInformationProcess(process, ProcessHandleInformation, snapshot, size, out used);
                if (status >= 0) break;
                Marshal.FreeHGlobal(snapshot);
                snapshot = IntPtr.Zero;
                if (status != unchecked((int)STATUS_INFO_LENGTH_MISMATCH)) break;
                size = Math.Max(size * 2, used + 64 * 1024);
            }
            if (snapshot == IntPtr.Zero || status < 0)
                return (result, $"句柄快照读取失败（0x{status:X8}）");

            long count = Marshal.ReadIntPtr(snapshot, 0).ToInt64();
            int header = IntPtr.Size * 2;
            int entrySize = Marshal.SizeOf<PROCESS_HANDLE_TABLE_ENTRY_INFO>();
            if (count <= 0 || count > 1_000_000) return (result, "句柄数异常：" + count);

            nameBuffer = Marshal.AllocHGlobal(64 * 1024);
            var watch = Stopwatch.StartNew();
            var stopwatch = watch;
            int seen = 0, files = 0, budgetHit = 0;
            IntPtr self = GetCurrentProcess();

            for (long i = 0; i < count; i++)
            {
                if (files >= MaxFileHandles) { budgetHit = 2; break; }

                if ((i & (CheckEvery - 1)) == 0)
                {
                    if (ct.IsCancellationRequested) { budgetHit = 3; break; }
                    if (stopwatch.ElapsedMilliseconds > budgetMs) { budgetHit = 1; break; }
                }

                var entry = Marshal.PtrToStructure<PROCESS_HANDLE_TABLE_ENTRY_INFO>(snapshot + header + (int)(i * entrySize));
                if (entry.ObjectTypeIndex != fileTypeIndex) continue;

                seen++;
                if (entry.HandleValue == IntPtr.Zero) continue;

                if (!DuplicateHandle(process, entry.HandleValue, self, out IntPtr dup, 0, false, DUPLICATE_SAME_ACCESS))
                    continue;

                try
                {
                    /* 目录句柄的 GetFileType 也是 FILE_TYPE_DISK，这里只为省掉非磁盘对象，
                     * 真正的目录过滤在 IsNoise（看 attribute）里。 */
                    if (GetFileType(dup) != FILE_TYPE_DISK) continue;

                    string ntName = QueryObjectName(dup, nameBuffer, 64 * 1024);
                    if (ntName.Length == 0) continue;

                    string dos = DosDeviceMap.ToDosPath(ntName);
                    if (dos.Length == 0) continue;      // 不是本地/网络盘上的东西（\Device\NamedPipe…）

                    result.Add(dos);
                    files++;
                }
                finally { CloseHandle(dup); }
            }

            string why = budgetHit switch
            {
                1 => $"预算 {budgetMs}ms 用尽",
                2 => $"File 句柄超过 {MaxFileHandles} 个，提前收工",
                3 => "被取消",
                _ => $"{seen} 个文件句柄",
            };
            return (result, why);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "收藏：枚举进程文件句柄");
            return (result, ex.GetType().Name);
        }
        finally
        {
            if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
            if (snapshot != IntPtr.Zero) Marshal.FreeHGlobal(snapshot);
            CloseHandle(process);
        }
    }

    /// <summary>
    /// 探出"File 对象"的 ObjectTypeIndex：在自己进程里开一个确定存在的文件，
    /// 在自己的句柄快照里找到这个句柄值，读它那一项的类型序号。
    ///
    /// 为什么值得多一次快照：之后只对这一个 index 的句柄取名，
    /// 管道 / 事件 / 同步对象连碰都不碰 —— 那些对象上的 NtQueryObject 可能永远不返回。
    ///
    /// 两个必须这么写的细节：
    ///   ① 句柄值一定属于**我们自己**。ObjectTypeIndex 是内核对象类型的全系统编号，
    ///      在自己进程里问出来一样管用，不必去碰目标进程；
    ///   ② 查询要用**真句柄**（OpenProcess 自己的 pid），不能用 GetCurrentProcess()
    ///      的伪句柄 —— ProcessHandleInformation 要求 PROCESS_QUERY_INFORMATION，
    ///      伪句柄上过不去（实测直接返回失败，症状就是"探不到 File 的类型序号"）。
    /// </summary>
    private static uint ProbeFileObjectTypeIndex()
    {
        string probe = Environment.ProcessPath ?? "";
        if (probe.Length == 0) return uint.MaxValue;

        IntPtr process = IntPtr.Zero;
        IntPtr snapshot = IntPtr.Zero;
        try
        {
            using SafeHandle? file = OpenProbeHandle(probe);
            if (file == null || file.IsInvalid) return uint.MaxValue;
            IntPtr handle = file.DangerousGetHandle();

            process = OpenProcess(PROCESS_QUERY_INFORMATION, false, (uint)Environment.ProcessId);
            if (process == IntPtr.Zero) return uint.MaxValue;

            int need;
            int status = NtQueryInformationProcess(process, ProcessHandleInformation, IntPtr.Zero, 0, out need);
            if (status != unchecked((int)STATUS_INFO_LENGTH_MISMATCH) && status < 0) return uint.MaxValue;

            int size = Math.Max(need, 64 * 1024) + 64 * 1024;
            snapshot = Marshal.AllocHGlobal(size);
            status = NtQueryInformationProcess(process, ProcessHandleInformation, snapshot, size, out _);
            if (status < 0) return uint.MaxValue;

            long count = Marshal.ReadIntPtr(snapshot, 0).ToInt64();
            int header = IntPtr.Size * 2;
            int entrySize = Marshal.SizeOf<PROCESS_HANDLE_TABLE_ENTRY_INFO>();

            for (long i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<PROCESS_HANDLE_TABLE_ENTRY_INFO>(snapshot + header + (int)(i * entrySize));
                if (entry.HandleValue != handle) continue;
                return entry.ObjectTypeIndex;
            }
            return uint.MaxValue;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "收藏：探 File 对象类型序号");
            return uint.MaxValue;
        }
        finally
        {
            if (snapshot != IntPtr.Zero) Marshal.FreeHGlobal(snapshot);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }
    /// <summary>开一个只读探测句柄（自己进程用，只为问出 File 的类型序号）。</summary>
    private static SafeHandle? OpenProbeHandle(string path)
    {
        try { return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch { return null; }
    }

    /// <summary>NtQueryObject(ObjectNameInformation) 取名；失败返回空串。</summary>
    private static string QueryObjectName(IntPtr handle, IntPtr buffer, int length)
    {
        int status = NtQueryObject(handle, ObjectNameInformation, buffer, length, out _);
        if (status < 0) return "";

        var us = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
        if (us.Buffer == IntPtr.Zero || us.Length == 0) return "";
        return Marshal.PtrToStringUni(us.Buffer, us.Length / 2) ?? "";
    }

    /// <summary>
    /// 目标窗口的"真实"窗口：UWP / 商店应用的顶层窗口属于 ApplicationFrameHost，
    /// 真正画内容的是它底下的子窗口（属于另一个进程）。取不到子窗口就按失败走降级。
    /// </summary>
    internal static IntPtr ResolveRealWindow(IntPtr hwnd, out string className)
    {
        className = WindowService.ClassNameOf(hwnd) ?? "";
        if (hwnd == IntPtr.Zero) return hwnd;

        string proc = ForegroundWatcher.ProcessNameOf(hwnd);
        if (!string.Equals(proc, "applicationframehost.exe", StringComparison.OrdinalIgnoreCase))
            return hwnd;

        IntPtr found = IntPtr.Zero;
        try
        {
            EnumChildWindows(hwnd, (child, _) =>
            {
                string childProc = ForegroundWatcher.ProcessNameOf(child);
                if (childProc.Length == 0) return true;
                if (string.Equals(childProc, "applicationframehost.exe", StringComparison.OrdinalIgnoreCase)) return true;
                found = child;
                return false;   // 找到就停
            }, IntPtr.Zero);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：找 UWP 的真实子窗口"); }

        if (found == IntPtr.Zero) return hwnd;
        className = WindowService.ClassNameOf(found) ?? className;
        return found;
    }

    /// <summary>
    /// 从命令行里抠出"像是被打开的文件"的路径（降级链第 ② 条）。
    ///
    /// WPS 的写法是 <c>wps /prometheus /pdf "D:\x.pdf"</c>，直接从参数里认；
    /// 只认扩展名在白名单里、且文件真的存在的那些 —— 别把安装路径当文件。
    /// </summary>
    internal static List<string> PathsInCommandLine(int pid, CancellationToken ct = default)
    {
        var found = new List<string>();
        IntPtr process = OpenProcess(PROCESS_QUERY_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return found;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            ct.ThrowIfCancellationRequested();
            const int ProcessCommandLineInformation = 60;
            int need;
            int status = NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out need);
            if (status != unchecked((int)STATUS_INFO_LENGTH_MISMATCH) && status < 0) return found;

            buffer = Marshal.AllocHGlobal(Math.Max(need, 4096) + 4096);
            status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, Math.Max(need, 4096) + 4096, out _);
            if (status < 0) return found;

            var us = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
            if (us.Buffer == IntPtr.Zero || us.Length == 0) return found;
            string line = Marshal.PtrToStringUni(us.Buffer, us.Length / 2) ?? "";
            if (line.Length == 0) return found;

            foreach (string token in SplitCommandLine(line))
            {
                string path = token.Trim().Trim('"');
                if (path.Length < 4) continue;
                if (RankOf(path) < 0) continue;
                try { if (!File.Exists(path)) continue; } catch { continue; }
                if (!found.Contains(path, StringComparer.OrdinalIgnoreCase)) found.Add(path);
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：读命令行"); }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(process);
        }
        return found;
    }

    /// <summary>按引号与空格切命令行（引号内的空格不切）。</summary>
    private static List<string> SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; cur.Append(c); continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        return parts;
    }

    /// <summary>把原始路径列表变成带属性的候选（过滤在这里做，打分在 <see cref="ChooseBest"/>）。</summary>
    internal static List<ForegroundFileCandidate> Score(IReadOnlyList<string> paths)
    {
        var list = new List<ForegroundFileCandidate>(paths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in paths)
        {
            string clean = RevealPath.Clean(path);
            if (clean.Length == 0) continue;
            if (!seen.Add(clean)) continue;

            uint attrs = AttributesOf(clean);
            if (IsNoise(clean, attrs, SizeOf(clean))) continue;
            if (IsCachePath(clean)) continue;

            int rank = RankOf(clean);
            if (rank < 0) continue;

            list.Add(new ForegroundFileCandidate(clean, rank, WriteTimeOf(clean), IsUnderSystemRoot(clean, SystemRoots)));
        }
        return list;
    }

    // ================= 小工具 =================

    /// <summary>系统目录前缀（运行时资源都在这几个下面）。</summary>
    private static readonly string[] SystemRoots = BuildSystemRoots();

    private static string[] BuildSystemRoots()
    {
        var roots = new List<string>(6);
        void Add(Environment.SpecialFolder folder)
        {
            try
            {
                string p = Environment.GetFolderPath(folder);
                if (p.Length > 0) roots.Add(p.TrimEnd('\\'));
            }
            catch { }
        }

        Add(Environment.SpecialFolder.Windows);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.CommonApplicationData);   // %ProgramData%
        Add(Environment.SpecialFolder.ApplicationData);         // %AppData%
        Add(Environment.SpecialFolder.LocalApplicationData);    // %LocalAppData%
        return roots.ToArray();
    }

    /// <summary>
    /// 某个进程的可执行文件全路径（拿不到返回空串）。
    ///
    /// 用途只有一个：「打开文件所在位置」识别失败时，退一步打开**这个程序**所在的目录。
    /// 权限更高的目标会返回空串（错误 5），与识别本身同一条静默降级规则。
    /// </summary>
    internal static string ProcessExePath(int pid)
    {
        if (pid <= 0) return "";
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(process, 0, sb, ref size)) return "";
            return sb.ToString(0, size);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "收藏：取进程可执行文件路径");
            return "";
        }
        finally { CloseHandle(process); }
    }

    /// <summary>
    /// 前台程序自己的 exe 完整路径（退路用：打开它所在的文件夹**并选中它**）。
    ///
    /// v1.7.14：退路从"打开文件夹"改成"打开文件夹并选中 exe" —— 用户要的是
    /// 「所在位置」那颗按钮的一贯语义：**资源管理器里该有东西被选中**。
    /// 只给目录的话，窗口开出来谁也没选中，用户会以为按钮只做了一半。
    /// </summary>
    /// <param name="processName">前台进程名（可为空；空了就只能直接问 pid）。</param>
    internal static string HostExeOf(int pid, string? processName)
    {
        string exe = ProcessExePath(pid);
        if (exe.Length == 0) exe = ProcessExePathByFamily(processName);
        return ExeUsableForFallback(exe) ? exe : "";
    }

    /// <summary>
    /// exe 路径 → 该打开的文件夹（不该给退路时返回空串）。纯函数，自检可以直接喂假路径。
    ///
    /// **系统组件一律不给退路**：开始菜单的搜索框（<c>searchhost.exe</c>）、
    /// UWP 的 <c>ApplicationFrameHost.exe</c>、锁屏这些锚点都会命中"前台是个程序"，
    /// 但打开 <c>C:\Windows\System32</c> 或 <c>…\SystemApps\…</c> 对用户毫无意义，
    /// 还容易让人以为程序乱来。这种情况退回"这里没有正在打开的文件"更诚实。
    /// </summary>
    internal static string FolderForHostExe(string? exePath)
    {
        string exe = (exePath ?? "").Trim().Trim('"');
        if (!ExeUsableForFallback(exe)) return "";
        try
        {
            string? dir = Path.GetDirectoryName(exe);
            return dir == null ? "" : dir;
        }
        catch { return ""; }
    }

    /// <summary>这个 exe 能不能拿来做「程序所在文件夹」这条退路（纯判定）。</summary>
    private static bool ExeUsableForFallback(string? exePath)
    {
        string exe = (exePath ?? "").Trim().Trim('"');
        if (exe.Length == 0) return false;
        return !IsUnderWindowsDirectory(exe);
    }

    /// <summary>这个路径是不是落在 %SystemRoot% 下面（System32 / SystemApps / WinSxS…）。</summary>
    private static bool IsUnderWindowsDirectory(string path)
    {
        try
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (win.Length == 0) return false;
            return IsUnderSystemRoot(path, new[] { win });
        }
        catch { return false; }
    }

    /// <summary>
    /// 同名进程里任意一个能问出 exe 路径就用它（同名的 exe 路径必然相同）。
    ///
    /// ⚠ 这里**不能**读 <c>Process.MainModule</c>：那要 <c>PROCESS_QUERY_INFORMATION|VM_READ</c>，
    /// 微信这种主进程受保护 / 提升的程序一律回错误 5；而 <see cref="ProcessExePath"/> 用的
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> 是专门为"跨完整性级别只看路径"设计的，
    /// 微信的**子进程**都能过（实测：主进程 22100 拒，29448/31168/35452 全过）。
    /// </summary>
    internal static string ProcessExePathByFamily(string? processName)
    {
        string name = (processName ?? "").Trim();
        if (name.Length == 0) return "";
        string bare = LeafOf(name);
        if (bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) bare = bare[..^4];
        if (bare.Length == 0) return "";

        try
        {
            foreach (Process p in Process.GetProcessesByName(bare))
            {
                int pid;
                try { pid = p.Id; }
                catch { continue; }
                finally { p.Dispose(); }

                string path = ProcessExePath(pid);
                if (path.Length > 0) return path;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "取同名进程的 exe 路径"); }
        return "";
    }
    /// <summary>
    /// 是不是"程序的缓存副本"，而不是用户心里那个文件。
    ///
    /// 实测：WPS 云文档打开时，句柄指向 <c>…\WPS Cloud Files\.362964933\cachedata\2F30…\书名.xlsx</c> ——
    /// 名字对得上、存在、大小也对，但它在缓存目录里，打开它的上级只会让人更困惑。
    /// 这种路径**不当作识别结果**，让链路继续往下走（标题 → 最近文档 → 手动选择），
    /// 用户至少能拿到自己真正的那个位置。
    /// </summary>
    internal static bool IsCachePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string p = path.Trim();
        return p.Contains(@"\cachedata\", StringComparison.OrdinalIgnoreCase)
            || p.Contains(@"\inetcache\", StringComparison.OrdinalIgnoreCase)
            || p.Contains(@"\temporary internet files\", StringComparison.OrdinalIgnoreCase)
            || p.Contains(@"\wps cloud files\.", StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>路径是不是落在某个系统目录下（大小写不敏感、按目录边界比，别把 Program FilesX 当前缀）。</summary>
    internal static bool IsUnderSystemRoot(string? path, IReadOnlyList<string>? roots)
    {
        if (string.IsNullOrWhiteSpace(path) || roots == null) return false;
        string p = path.Trim();
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string r = root.TrimEnd('\\');
            if (p.Length <= r.Length) continue;
            if (!p.StartsWith(r, StringComparison.OrdinalIgnoreCase)) continue;
            if (p[r.Length] != '\\') continue;   // Program FilesX 不是 Program Files 的子目录
            return true;
        }
        return false;
    }

    private static bool IsRuntimeExtension(string ext)
    {
        if (ext.Length == 0) return false;
        foreach (string e in RuntimeExtensions)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string ExtensionOf(string? path)
    {
        try { return Path.GetExtension(path ?? ""); } catch { return ""; }
    }

    private static string LeafOf(string? path)
    {
        try { return Path.GetFileName(path ?? ""); } catch { return ""; }
    }

    private static uint AttributesOf(string path)
    {
        try { return GetFileAttributes(path); } catch { return 0xFFFFFFFF; }
    }

    private static long SizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch { return -1; }
    }

    private static DateTime WriteTimeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }
}
