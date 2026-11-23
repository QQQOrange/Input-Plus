using System.Diagnostics;
using System.Reflection;

namespace InputPlus.Core.App;

/// <summary>宿主是怎么被拉起来的（诊断/日志用）。</summary>
internal enum HostLaunchMode
{
    /// <summary>直接 CreateProcess：子进程继承我们的令牌（未提升时这就够了）。</summary>
    Direct,

    /// <summary>经 Shell.Application 的 ShellExecute：由 explorer 代劳 → 普通权限。</summary>
    ShellExecute,

    /// <summary>经 explorer.exe 转发：同样是普通权限，ShellExecute 不可用时的兜底。</summary>
    Explorer,
}

/// <summary>
/// 启动 --hookhost 子进程。
///
/// 唯一的难点是**完整性级别**：默认的 CreateProcess 会让子进程继承父进程的
/// 令牌 —— 提升的 InputPlus 会拉起一个提升的宿主，于是"注入下沉"就没意义了
/// （宿主和主进程同级，UIPI 边界一模一样）。要让子进程落在 medium IL，
/// 只能借 explorer 的手：explorer 自己是普通权限，它创建的子进程也是。
///
/// 三种手段按可靠性排序：
///   1. Shell.Application（COM，out-of-proc，宿主是 explorer）—— 参数可靠；
///   2. explorer.exe 命令行转发 —— 老办法，Win10/11 上仍然可用；
///   3. 直接 CreateProcess —— 宿主跟着我们提升。注意这**不是失败**：
///      提升的宿主往普通窗口注入是允许的（高 → 低），功能照样正常，
///      只是没能做到最小权限。
/// </summary>
internal static class HookHostLauncher
{
    /// <summary>最近一次启动用的方式。</summary>
    internal static HostLaunchMode LastMode { get; private set; } = HostLaunchMode.Direct;

    /// <summary>最近一次启动的细节（失败原因 / COM 异常），写进日志用。</summary>
    internal static string LastDetail { get; private set; } = "";

    /// <summary>
    /// 拉起宿主。
    ///
    /// <paramref name="wantMedium"/> 为 true 时优先走"借 explorer 之手"的两条路，
    /// 失败再退回直接启动（宿主会跟着提升，功能不受影响）。
    /// </summary>
    /// <param name="process">拿到的进程对象。经 explorer 拉起时拿不到句柄（null）——
    /// 宿主会在握手时把自己的 PID 报回来，由调用方补上。</param>
    internal static bool TryStart(string exe, string[] args, bool wantMedium, out Process? process)
    {
        process = null;
        LastDetail = "";

        if (wantMedium)
        {
            if (TryShellExecute(exe, args))
            {
                LastMode = HostLaunchMode.ShellExecute;
                LastDetail = "经 Shell.Application 启动（预期普通权限）";
                return true;
            }

            if (TryExplorer(exe, args))
            {
                LastMode = HostLaunchMode.Explorer;
                LastDetail = "经 explorer.exe 转发启动（预期普通权限）";
                return true;
            }

            LastDetail = "借 explorer 之手失败：" + LastDetail + "；退回直接启动";
        }

        LastMode = HostLaunchMode.Direct;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
                CreateNoWindow = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            process = Process.Start(psi);
            if (process == null)
            {
                LastDetail = "Process.Start 返回 null";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastDetail = "直接启动失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 只要"普通权限"这一条路：Shell.Application → explorer.exe，两条都失败就返回 false。
    ///
    /// 与 <see cref="TryStart"/> 的区别是**没有直接启动的退路** —— 用在"降权重启"
    /// 这种"必须是 medium IL"的场合：直接 CreateProcess 只会继承提升的令牌，
    /// 结果是"用户以为降权了，其实还是管理员"。
    /// </summary>
    internal static bool TryStartMedium(string exe, string[] args, out string detail)
    {
        LastDetail = "";

        if (TryShellExecute(exe, args))
        {
            LastMode = HostLaunchMode.ShellExecute;
            LastDetail = "经 Shell.Application 启动（预期普通权限）";
            detail = LastDetail;
            return true;
        }

        if (TryExplorer(exe, args))
        {
            LastMode = HostLaunchMode.Explorer;
            LastDetail = "经 explorer.exe 转发启动（预期普通权限）";
            detail = LastDetail;
            return true;
        }

        detail = LastDetail.Length > 0 ? LastDetail : "借 explorer 之手失败";
        return false;
    }

    /// <summary>
    /// 只要"普通权限"、但参数是**已经拼好的一行**（v1.5.1，「打开文件所在位置」用）。
    ///
    /// 为什么不复用上面那个 string[] 版本：它会把每个参数再用
    /// <see cref="BuildArgumentLine"/> 包一层引号并转义，而 explorer 要的是
    /// `/select,"D:\a b\x.pdf"` 这种**整体一对引号**的写法 —— 二次包装之后参数会变成
    /// `"/select,\"D:\a b\x.pdf\""`，explorer 认不出来，于是"点了没反应"。
    /// </summary>
    internal static bool TryStartMediumRaw(string exe, string argumentLine, out string detail)
    {
        LastDetail = "";

        if (TryShellExecuteRaw(exe, argumentLine))
        {
            LastMode = HostLaunchMode.ShellExecute;
            LastDetail = "经 Shell.Application 启动（预期普通权限）";
            detail = LastDetail;
            return true;
        }

        if (TryExplorerRaw(argumentLine))
        {
            LastMode = HostLaunchMode.Explorer;
            LastDetail = "经 explorer.exe 转发启动（预期普通权限）";
            detail = LastDetail;
            return true;
        }

        detail = LastDetail.Length > 0 ? LastDetail : "借 explorer 之手失败";
        return false;
    }
    /// <summary>把参数拼成一行（含空格的加引号）。</summary>
    internal static string BuildArgumentLine(string[] args)
    {
        if (args == null || args.Length == 0) return "";
        var parts = new List<string>(args.Length);
        foreach (string a in args)
        {
            if (a.Length == 0) { parts.Add("\"\""); continue; }
            parts.Add(a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0
                ? "\"" + a.Replace("\"", "\\\"") + "\""
                : a);
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Shell.Application 的 ShellExecute：explorer 是 out-of-proc COM 宿主，
    /// 由它 CreateProcess，子进程就是 medium IL。用反射调用是为了不引入
    /// COM 互操作程序集（也不需要 dynamic / Microsoft.CSharp）。
    /// </summary>
    private static bool TryShellExecute(string exe, string[] args) => TryShellExecuteRaw(exe, BuildArgumentLine(args));

    /// <summary>同上，但参数行已经拼好、不再二次加引号。</summary>
    private static bool TryShellExecuteRaw(string exe, string argumentLine)
    {
        object? shell = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) { LastDetail = "找不到 Shell.Application"; return false; }

            shell = Activator.CreateInstance(type);
            if (shell == null) { LastDetail = "Shell.Application 创建失败"; return false; }

            type.InvokeMember(
                "ShellExecute",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { exe, argumentLine, AppContext.BaseDirectory, "open", 0 });

            return true;
        }
        catch (Exception ex)
        {
            LastDetail = (ex is TargetInvocationException { InnerException: { } inner })
                ? inner.Message
                : ex.Message;
            return false;
        }
        finally
        {
            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
            {
                try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { }
            }
        }
    }

    /// <summary>explorer.exe "程序" 参数… —— explorer 把请求转给已在运行的 shell（普通权限）。</summary>
    private static bool TryExplorer(string exe, string[] args) => TryExplorerRaw("\"" + exe + "\" " + BuildArgumentLine(args));

    /// <summary>同上，但参数行已经拼好。</summary>
    private static bool TryExplorerRaw(string line)
    {
        try
        {
            var psi = new ProcessStartInfo("explorer.exe", line) { UseShellExecute = true };
            using var p = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            LastDetail = "explorer.exe 转发失败：" + ex.Message;
            return false;
        }
    }
}
