using System.Diagnostics;
using System.IO;

namespace InputPlus.Core.Windows;

/// <summary>
/// **起外部进程的唯一入口** —— 凡是"把一个路径交给系统去打开/执行"的地方都从这里出 <c>ProcessStartInfo</c>。
///
/// 为什么必须收敛到一处（2026-11-26 的事故）：
///   不显式给 <c>WorkingDirectory</c> 时，Windows 让子进程**继承父进程的当前目录**。
///   而我们是常驻程序，自己的当前目录往往是安装目录（<c>D:\software\myself</c> 这种）。
///   于是被我们拉起来的程序会把"当前目录"当成自己的家：
///   在那个目录里找/生成配置文件、把日志和数据写到我们脚边。
///   真实案例：wb2api.exe 的 config.json / auths / data 全按当前目录解析，
///   用键启动它就从 InputPlus 目录读（没找到就**生成一份新的**），
///   在资源管理器里双击却正常 —— 差别只在"谁给的当前目录"。
///
/// 所以规则只有一条：**目标是本机磁盘上的路径，就把它自己的目录当工作目录交下去。**
/// 这不是"某个程序的特殊照顾"，是所有同类程序（git、ffmpeg、node、各类面板）的共同预期。
///
/// 反面清单（这些**不该**设工作目录，设了反而有副作用）：
///   · 网址 <c>https://…</c>：浏览器不认这个字段；
///   · 协议 <c>ms-settings:</c> / <c>shell:Downloads</c>：没有对应目录；
///   · 裸命令名 <c>notepad</c> / <c>explorer.exe</c>：靠 PATH 解析，当前目录无意义；
///   · 目录 <c>D:\下载</c>：shell 只是"打开它"，工作目录跟它无关。
/// 这四类 <see cref="WorkDirFor"/> 一律返回空串，调用方也就不会写这个字段。
///
/// 还有一个不该去"纠正"的地方：<c>.lnk</c> 快捷方式自带"起始位置"，
/// shell 解析时会用快捷方式里的值覆盖我们给的 <c>lpDirectory</c> —— 那是对的，别改。
/// </summary>
public static class ExternalLaunch
{
    /// <summary>去掉首尾空白与shell 允许的那对引号；空输入返回空串（永不返回 null）。</summary>
    private static string Clean(string? target)
        => (target ?? "").Trim().Trim('"').Trim();

    /// <summary>
    /// 这个目标是不是"本机磁盘上的路径"（而不是网址 / 协议 / PATH 里的裸命令名）。
    ///
    /// 判断只看形状，不碰磁盘：带分隔符才算路径。
    /// 这样 <c>notepad.exe</c>（PATH 名）与 <c>C:\Windows\notepad.exe</c>（路径）能分开，
    /// 而 <c>ms-settings:</c> 这种没有分隔符的协议也不会被误判成相对路径。
    /// </summary>
    public static bool IsLocalPath(string? target)
    {
        string t = Clean(target);
        if (t.Length == 0) return false;
        if (LooksLikeUri(t)) return false;
        return t.IndexOf('\\') >= 0 || t.IndexOf('/') >= 0;
    }

    /// <summary>
    /// 像不像"有 scheme 的东西"：<c>http://…</c>、<c>ms-settings:</c>、<c>shell:Downloads</c>、<c>mailto:…</c>。
    /// 单字母 + 冒号是盘符（<c>C:\…</c>），要放行，别当成 scheme。
    /// </summary>
    private static bool LooksLikeUri(string t)
    {
        if (t.StartsWith("//", StringComparison.Ordinal)) return true;              // //host/path
        if (t.Contains("://", StringComparison.Ordinal)) return true;               // https://…
        int colon = t.IndexOf(':');
        if (colon <= 0) return false;
        if (colon == 1 && char.IsLetter(t[0])) return false;                        // C: 盘符
        return true;                                                                // scheme:
    }

    /// <summary>
    /// 这个目标该用哪个目录当工作目录；返回空串 = "不设这个字段"。
    ///
    /// 取的是**目标所在目录**，不是我们的目录，也不是"当前打开的文件夹" ——
    /// 后两种都会让程序在自己的家之外读配置（这正是本次要修的病）。
    /// 目录不存在时同样返回空串：给了也起不来，不如让系统按原样处理并如实报错。
    /// </summary>
    public static string WorkDirFor(string? target)
    {
        string t = Clean(target);
        if (!IsLocalPath(t)) return "";

        string full;
        try { full = Path.GetFullPath(t); }
        catch { return ""; }                    // 路径里有非法字符：交给系统去报错

        try
        {
            /* 目录：shell 是"打开它"，工作目录与它无关。
             * 硬塞父目录只会让"在文件夹里按 Shift+右键开终端"之类的后续行为变得莫名其妙。 */
            if (Directory.Exists(full)) return "";

            string dir = Path.GetDirectoryName(full) ?? "";
            return dir.Length > 0 && Directory.Exists(dir) ? dir : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 交给 shell 打开（<c>UseShellExecute=true</c>）：文件走关联程序、网址走浏览器、
    /// <c>.lnk</c> 走快捷方式、协议走注册的处理器。本机路径自动带上自己的工作目录。
    /// </summary>
    /// <param name="target">文件路径 / 网址 / 协议 / PATH 里的命令名。</param>
    /// <param name="arguments">命令行参数；为空就不写（写空串会让某些程序收到一个空参数）。</param>
    /// <param name="runAs">要 UAC 提权（等价"以管理员身份运行"）。</param>
    /// <param name="silent">隐藏窗口（Qbar 命令的"静默运行"）。</param>
    public static ProcessStartInfo ForTarget(string target, string? arguments = null,
        bool runAs = false, bool silent = false)
    {
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;

        string dir = WorkDirFor(target);
        if (dir.Length > 0) psi.WorkingDirectory = dir;

        if (runAs) psi.Verb = "runas";
        if (silent)
        {
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.CreateNoWindow = true;
        }
        return psi;
    }

    /// <summary>
    /// 直接建进程（<c>UseShellExecute=false</c>）：参数原样传、不经过 shell 的引号与通配符改写。
    /// 「运行命令」这类"我明确知道要跑哪个 exe"的场合用它；工作目录默认取 exe 自己的目录。
    /// </summary>
    /// <param name="exe">可执行文件路径（带分隔符的完整路径，或 PATH 里的名字）。</param>
    /// <param name="arguments">原样传给程序的参数串。</param>
    /// <param name="workDir">调用方指定的工作目录；留空才按 exe 所在目录补。</param>
    public static ProcessStartInfo ForProgram(string exe, string? arguments = null, string workDir = "")
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;

        string dir = Clean(workDir);
        if (dir.Length == 0) dir = WorkDirFor(exe);
        if (dir.Length > 0 && Directory.Exists(dir)) psi.WorkingDirectory = dir;
        return psi;
    }
}
