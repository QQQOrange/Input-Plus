using System;
using System.Text;
using System.Threading;
using InputPlus.Core.Interop;
using InputPlus.Core.Windows;

namespace InputPlus.Core;

/// <summary>
/// `--foreground-file [pid]`：把"识别前台程序当前打开的文件"这条链路单独跑一次，
/// 结果写进 <c>%TEMP%\clp-foreground-file.txt</c>，退出码 0 = 认出来了。
///
/// 为什么要有这个入口：这条链路的成败跟**机器上装了什么程序、跑在什么权限**强相关
/// （WPS 的 /prometheus 命令行、Office 的锁文件、管理员启动的目标进程…），
/// 用户报"收藏没反应"时，让他跑一条命令比来回问十句快。
/// 仓库里已有 <c>--suggestions-probe</c>、<c>--dialogprobe</c> 这些先例。
///
/// 为什么支持指定 pid：从一个终端窗口里跑命令时，前台**就是终端自己**，
/// 永远问不出"记事本打开的那个 txt"。带上 pid 才能对着一个具体进程验。
/// </summary>
internal static class ForegroundFileCli
{
    /// <summary>
    /// `--office-recent &lt;文件名或路径&gt;`：只在 WPS / Office 的"最近文件"里按文件名找真实路径。
    ///
    /// 为什么要单独一条：WPS 里点收藏/定位失败时，先跑它就能分清是
    /// "WPS 根本没记这个文件" 还是 "记了但我们没读出来"。
    /// </summary>
    internal static int RunOfficeRecent(string? nameOrPath)
    {
        string name = (nameOrPath ?? "").Trim();
        try { name = System.IO.Path.GetFileName(name); } catch { }
        if (name.Length == 0) return 2;

        string? found = InputPlus.Core.Windows.OfficeRecentFiles.FindByFileName(name);
        string text = found == null
            ? $"WPS / Office 最近文件里没有：{name}"
            : $"命中：{found}";

        try { NativeMethods.AttachConsole(-1); Console.WriteLine(text); } catch { }
        try
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clp-office-recent.txt"), text);
        }
        catch { }
        return found == null ? 1 : 0;
    }
    internal static int Run(string[] args)
    {
        int pid = PidFrom(args);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        IntPtr hwnd = IntPtr.Zero;
        if (pid > 0)
        {
            hwnd = TopWindowOf(pid);
        }
        else
        {
            hwnd = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint live);
            pid = (int)live;
        }

        string proc = ForegroundWatcher.ProcessNameOf(hwnd);
        string cls = hwnd == IntPtr.Zero ? "" : (WindowService.ClassNameOf(hwnd) ?? "");
        string title = ForegroundWatcher.TitleOf(hwnd);
        string exe = ForegroundFileProbe.ProcessExePath(pid);

        var ct = new CancellationTokenSource(ForegroundFileProbe.BudgetMs * 4).Token;
        /* 没有顶层窗口（隐藏控制台、后台播放器）照样能扫 —— 句柄表跟窗口没关系。
         * 只有"按 pid 也开不了这个进程"才是真的失败。 */
        ForegroundFileResult result = hwnd == IntPtr.Zero
            ? ForegroundFileProbe.ScanProcess(pid, ct)
            : ForegroundFileProbe.Scan(hwnd, ct);
        watch.Stop();

        var sb = new StringBuilder();
        sb.AppendLine("Input Plus · 前台文件识别探针");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("提权: " + (App.ElevationState.Elevated ? "是（管理员）" : "否（普通权限）"));
        sb.AppendLine("窗口: 0x" + hwnd.ToInt64().ToString("X") + "  类名=" + cls);
        sb.AppendLine("进程: pid=" + pid + "  " + proc + "  " + exe);
        sb.AppendLine("标题: " + title);
        sb.AppendLine("可点: " + (ForegroundFileProbe.CanActOnForegroundFile(proc, cls) ? "是" : "否（资源管理器/桌面/任务栏）"));

        /* 星标那条路用的上下文：Capture 万一落空，就从窗口句柄重建 —— 这里把重建结果打出来。 */
        var rebuilt = InputPlus.Core.Qbar.FavoriteTarget.FromWindow(hwnd);
        sb.AppendLine("上下文重建: " + (rebuilt == null
            ? "(失败)"
            : $"source={rebuilt.Source} proc={rebuilt.ProcessName} class={rebuilt.ClassName}/{rebuilt.RootClass}"));
        sb.AppendLine("耗时: " + watch.ElapsedMilliseconds + " ms（预算 " + ForegroundFileProbe.BudgetMs + " ms）");
        sb.AppendLine("结果: " + result.Outcome + (result.File == null ? "" : "  " + result.File.Path));
        if (result.Why.Length > 0) sb.AppendLine("原因: " + result.Why);

        /* 诊断用：把扫描到的原始路径与"为什么被丢掉"逐条列出来。
         * 没有这一段，"识别失败"只能靠猜 —— WPS / Edge 这种多进程程序尤其如此。 */
        if (result.File == null && pid > 0)
        {
            var (raw, why) = ForegroundFileProbe.EnumerateOpenFilesOnce(pid, ForegroundFileProbe.BudgetMs);
            sb.AppendLine();
            sb.AppendLine($"原始命中 {raw.Count} 条（{why}）：");
            int shown = 0;
            foreach (string rawPath in raw)
            {
                if (shown++ >= 25) { sb.AppendLine("  …"); break; }
                uint attrs = 0;
                long size = -1;
                int rank = ForegroundFileProbe.RankOf(rawPath);
                try { attrs = (uint)System.IO.File.GetAttributes(rawPath); } catch { attrs = 0xFFFFFFFF; }
                try { size = System.IO.File.Exists(rawPath) ? new System.IO.FileInfo(rawPath).Length : -1; } catch { size = -1; }

                string verdict;
                if ((attrs & 0x10) != 0) verdict = "目录";
                else if (!System.IO.File.Exists(rawPath)) verdict = "文件不存在";
                else if (size == 0) verdict = "0 字节";
                else if (rank < 0) verdict = "扩展名不在白名单";
                else if (ForegroundFileProbe.IsNoise(rawPath, attrs, size)) verdict = "噪声过滤";
                else verdict = "应当入选（打分把它排掉了？）";

                sb.AppendLine($"  [{verdict}] rank={rank} size={size} {rawPath}");
            }
        }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clp-foreground-file.txt");
        try { System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8); }
        catch (Exception ex) { CoreLog.Warn("[前台文件] 写探针结果失败：" + ex.Message); }

        try
        {
            NativeMethods.AttachConsole(-1);
            Console.WriteLine(sb.ToString());
        }
        catch { /* 没有父控制台：结果已经写进文件 */ }

        CoreLog.Info("[前台文件] 探针：" + result.Outcome + (result.File == null ? "" : " " + result.File.Path));
        return result.File != null ? 0 : 1;
    }

    /// <summary>参数里第一个纯数字，或 <c>--pid=1234</c>。没有返回 0。</summary>
    private static int PidFrom(string[] args)
    {
        foreach (string a in args)
        {
            string text = a;
            if (text.StartsWith("--pid=", StringComparison.OrdinalIgnoreCase)) text = text[6..];
            if (text.Length == 0 || !int.TryParse(text, out int pid)) continue;
            if (pid > 0) return pid;
        }
        return 0;
    }

    /// <summary>某个进程的顶层可见窗口（挑面积最大的那个，和 win 版工具一致）。</summary>
    private static IntPtr TopWindowOf(int pid)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;

        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner != (uint)pid) return true;
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;

                if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect)) return true;
                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area <= bestArea) return true;
                bestArea = area;
                best = hwnd;
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "前台文件：按 pid 找窗口"); }

        return best;
    }
}
