using System;
using System.Diagnostics;
using System.IO;
using InputPlus.Core.Localization;
using InputPlus.Core.Windows;

namespace InputPlus.Core.Actions;

/// <summary>
/// "在资源管理器里打开这个文件夹"（v1.6.7，附加菜单多级下钻的落点）。
///
/// ── 打开方式（全局只有这一种）──────────────────────────────────────
/// 一律 <c>explorer.exe "路径"</c>：
///   · **默认在已有的资源管理器窗口里导航**（那个文件夹已经开着就直接复用）；
///   · 需要"每次新窗口"时把 <see cref="NewWindow"/> 置 true，
///     参数变成 <c>explorer.exe /n,"路径"</c> —— 别在调用点各拼各的。
///
/// 为什么不直接 <c>Process.Start(路径, UseShellExecute=true)</c>：
///   效果虽然一样，但那条路对**长路径**和**驱动器根**的失败是静默的（explorer 不吭声），
///   而这里要在菜单里给出"文件不存在 / 无权限 / 启动失败"的明确提示。
///   CreateProcess 至少会把"根本起不来"如实抛回来。
///
/// ── 路径处理（每一条都对应一个真实坑）────────────────────────────
///   · <c>%USERPROFILE%</c> / <c>%APPDATA%</c> / <c>~</c>：先展开；
///   · 相对路径：先归一化成绝对路径（<see cref="Path.GetFullPath(string)"/>）；
///   · 正斜杠统一成反斜杠；末尾反斜杠去掉，但**盘符根 <c>C:\</c> 保留**；
///   · <c>\\?\</c> / <c>\\?\UNC\</c>：交给 explorer 前剥掉（shell 不认），
///     但存在性判断会先试"带前缀的形式"，用来覆盖 >260 字符的超长路径；
///   · 含空格 / 中文 / <c>&amp;</c> ：整条路径包一对双引号，**绝不做二次拼接**；
///   · 盘符根不能加引号：<c>"C:\"</c> 在命令行解析里会被读成 <c>C:"</c>
///     （结尾反斜杠把收尾引号转义了），而根目录不可能含空格，干脆裸写；
///   · UNC（<c>\\server\share\...</c>）按普通带引号路径走，共享根不加尾斜杠。
///
/// 整体**同步**：调用方负责 <c>Task.Run</c> 包起来（网络路径的目录存在性判断会阻塞）。
/// </summary>
internal static class FolderOpen
{
    /// <summary>
    /// 每次开新窗口（<c>explorer.exe /n,路径</c>）。
    /// 默认 false = 在已有资源管理器窗口里打开（用户需求里的默认行为）。
    /// </summary>
    internal static bool NewWindow { get; set; }

    /// <summary>仅供自检：拦截真正的启动，只记录最终的路径（置 null 时真开）。</summary>
    internal static Action<string>? InterceptForTest { get; set; }

    /// <summary>一次"打开文件夹"的结果。<see cref="Target"/> 是归一化后真正交给 explorer 的路径。</summary>
    internal readonly record struct LaunchResult(bool Ok, string Target, string Error)
    {
        internal static LaunchResult Fail(string target, string error) => new(false, target, error);
        internal static LaunchResult Done(string target) => new(true, target, "");
    }

    /// <summary>
    /// 归一化成"可以直接交给 shell"的绝对路径（纯字符串处理，不碰磁盘）。
    /// 空 / 全是空白 / 只有一个引号 → 返回空串。
    /// </summary>
    internal static string Normalize(string? raw)
    {
        string p = (raw ?? "").Trim().Trim('"').Trim();
        if (p.Length == 0) return "";

        /* 长路径前缀先剥：后面所有判断都按 shell 认的形式做。
         * \\?\UNC\srv\share → \\srv\share（注意 UNC 前缀是 8 个字符）。 */
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
            p = p[4..];
        else if (p.StartsWith(@"\??\", StringComparison.Ordinal))
            p = p[4..];

        try { p = Environment.ExpandEnvironmentVariables(p); }
        catch { /* 变量写坏了就按原样用 */ }

        if (p == "~")
            p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]);

        p = p.Replace('/', '\\');

        bool unc = p.StartsWith(@"\\", StringComparison.Ordinal);
        if (!unc)
        {
            try
            {
                if (!Path.IsPathFullyQualified(p)) p = Path.GetFullPath(p);
            }
            catch { /* 归一化失败就交给后面的 Directory.Exists 去否掉 */ }
            p = p.Trim();
        }

        // 末尾反斜杠：盘符根（C:\）保留，其余去掉（UNC 共享根也不带尾斜杠）
        if (p.Length > 3 && p.EndsWith('\\')) p = p.TrimEnd('\\');

        /* "C:\\" 这种多打了一个反斜杠的写法：上面那步会把它削成 "C:"，
         * 那样既不合法也不再是"根目录"。这里补回来，保证"盘符根只有一种写法"。 */
        if (p.Length == 2 && p[1] == ':' && char.IsLetter(p[0])) p += '\\';
        return p;
    }

    /// <summary>
    /// 交给 <c>explorer.exe</c> 的参数里，这一条路径该怎么写。
    ///
    /// 盘符根裸写（理由见类注释）；其余整体加一对双引号 ——
    /// 空格、中文、<c>&amp;</c>、逗号、括号都靠这一对引号兜住，不做任何额外转义。
    /// </summary>
    internal static string Quote(string? path)
    {
        string p = path ?? "";
        if (p.Length == 0) return "";
        if (p.Length == 3 && p[1] == ':' && p[2] == '\\') return p;   // C:\
        return "\"" + p + "\"";
    }

    /// <summary>
    /// 打开文件夹。失败时 <see cref="LaunchResult.Error"/> 是**给用户看的**一句话
    /// （路径不存在 / 无权限 / 进程启动失败），不是异常堆栈。
    /// </summary>
    internal static LaunchResult TryLaunch(string? raw)
    {
        string target = Normalize(raw);
        if (target.Length == 0) return LaunchResult.Fail("", Loc.T("路径为空"));

        /* shell: 命名空间（shell:Downloads / shell:Startup）不是文件系统路径，
         * Directory.Exists 永远 false，得原样交给 explorer。 */
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return Launch(target, Quote(target));

        bool exists;
        try { exists = Directory.Exists(target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            exists = false;
        }

        if (!exists)
        {
            /* 超长路径：不带前缀的 Directory.Exists 会失败，带 \\?\ 的才认。
             * 这种路径交给 explorer 也得用带前缀的形式（Win10 1903+ 认），
             * 否则它静默什么都不做 —— 那正是用户最讨厌的"点了没反应"。 */
            string longForm = target.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + target[2..]
                : @"\\?\" + target;

            bool longExists;
            try { longExists = target.Length >= 240 && Directory.Exists(longForm); }
            catch { longExists = false; }

            if (!longExists)
                return LaunchResult.Fail(target, Loc.F("文件夹不存在或无法访问：{0}", target));

            target = longForm;
        }

        if (InterceptForTest != null)
        {
            InterceptForTest(target);
            return LaunchResult.Done(target);
        }

        return Launch(target, NewWindow ? "/n," + Quote(target) : Quote(target));
    }

    /// <summary>真正把 explorer 拉起来；<c>CreateProcess</c> 失败要如实报出来。</summary>
    private static LaunchResult Launch(string target, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = false,
                Arguments = arguments,
            };
            using Process? p = Process.Start(psi);
            return LaunchResult.Done(target);
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"[打开文件夹] 启动 explorer 失败：{ex.Message}（目标 {target}，参数 {arguments}）");
            return LaunchResult.Fail(target, Loc.F("启动资源管理器失败：{0}", ex.Message));
        }
    }

    /// <summary>仅供自检：一条路径最终会变成什么命令行（不启动任何进程）。</summary>
    internal static string CommandLineForTest(string? raw)
    {
        string target = Normalize(raw);
        if (target.Length == 0) return "";
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return "explorer.exe " + Quote(target);
        return "explorer.exe " + (NewWindow ? "/n," : "") + Quote(target);
    }
}
