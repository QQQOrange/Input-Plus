using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Windows;

/// <summary>
/// 强制退出当前前台程序（动作「系统功能 → 强制退出当前应用」的实现）。
///
/// 为什么单独一个类而不是在 ActionCatalog 里就地写：
/// 「该不该杀」的判定（自己 / 桌面外壳 / 系统关键进程）是纯逻辑，
/// 自检要能直接喂 pid + 进程名验，不必真去开进程杀一遍。
///
/// 注意：这里只做**结束进程**。想「关掉当前窗口」用「窗口功能 → 关闭窗口」，
/// 那个发的是 WM_CLOSE，程序还能弹「要保存吗」；这里是 TaskKill，
/// 所以界面文案写的是「强制退出」，用户得知道后果。
/// </summary>
internal static class ForceKill
{
    /// <summary>执行结果：成功了没有，以及一句给人看的话。</summary>
    public readonly record struct Result(bool Ok, string Message);

    /* 不许杀的名字。分四组：
     *   1. 自己（含 hookhost 子进程，同名 InputPlus.exe）—— 自杀等于把热键全废掉
     *   2. 桌面外壳 explorer.exe —— 桌面上按一下就把任务栏带走
     *   3. 系统会话进程 —— 这些死掉是蓝屏 / 立即注销，不是「退出一个应用」
     *   4. 系统占位进程 */
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "inputplus.exe",
        "explorer.exe",
        "winlogon.exe", "csrss.exe", "services.exe", "lsass.exe",
        "wininit.exe", "smss.exe", "dwm.exe", "svchost.exe",
        "fontdrvhost.exe", "audiodg.exe", "sihost.exe", "ctfmon.exe",
        "system", "system idle process", "registry", "memory compression",
    };

    /// <summary>
    /// 判定这个进程能不能杀。能杀返回 null，不能杀返回原因（中文，直接给用户看）。
    /// 抽出来是为了让自检能穷举 —— 判定写错一次就是杀掉用户的任务栏。
    /// </summary>
    internal static string? Blocker(uint pid, string name, uint selfPid)
    {
        if (pid == 0) return "当前没有前台窗口，不知道该退出谁";
        if (pid == selfPid) return "不能退出自己";
        if (pid <= 4) return "系统进程不能被结束";
        if (name.Length == 0) return "读不到前台进程的信息（可能权限不够）";

        if (name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return "当前前台是资源管理器或桌面，结束后任务栏也会消失，已跳过";

        if (!Protected.Contains(name)) return null;

        /* 注意这里只挡**当前这个**进程名。改名前的 KeyboardEnhance.exe 故意不在名单里：
         * 升级后老实例可能还挂在后台占着钩子，用户想强制退出它是合理需求。 */
        if (name.Equals("inputplus.exe", StringComparison.OrdinalIgnoreCase))
            return "不能退出自己";

        return $"「{name}」是系统关键进程，结束它会让整个会话崩溃，已跳过";
    }

    /// <summary>结束当前前台窗口所属的进程。</summary>
    public static Result Foreground()
    {
        IntPtr hwnd = ForegroundWatcher.ForegroundWindow();
        if (hwnd == IntPtr.Zero) return new Result(false, "当前没有前台窗口，不知道该退出谁");

        GetWindowThreadProcessId(hwnd, out uint pid);
        string name = ForegroundWatcher.ProcessNameOf(hwnd);

        string? blocked = Blocker(pid, name, (uint)Environment.ProcessId);
        if (blocked != null) return new Result(false, blocked);

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string display = proc.ProcessName;

            /* entireProcessTree: 浏览器 / 编辑器都是一棵树，
             * 只杀主进程会留下渲染进程当孤儿，窗口还在那儿。 */
            proc.Kill(true);
            return new Result(true, $"已强制退出 {display}");
        }
        catch (ArgumentException)
        {
            // 进程在这一瞬间自己退了：结果就是用户想要的，不算失败
            return new Result(true, "那个程序已经退出了");
        }
        catch (Win32Exception ex)
        {
            return new Result(false, ex.NativeErrorCode == 5
                ? $"结束「{name}」需要管理员权限，当前权限不够"
                : $"结束「{name}」失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new Result(false, $"结束「{name}」失败：{ex.Message}");
        }
    }
}
