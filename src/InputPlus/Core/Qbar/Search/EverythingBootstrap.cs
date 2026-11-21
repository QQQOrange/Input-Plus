using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace InputPlus.Core.Qbar.Search;

/// <summary>
/// 让"内置 Everything"真的能跑起来的那一层：SDK 就绪 + 客户端进程就绪。
///
/// 背景：Everything 的 SDK 不是搜索引擎，它只是 IPC 客户端。没有 Everything 进程时
/// 所有查询都返回 0 条 —— 界面上看起来就是"文件搜索坏了"，而不是"缺个后台程序"。
/// 所以这里在第一次搜索/打开面板时把内置客户端拉起来，并把状态说清楚。
///
/// 线程模型：全部在后台线程调用（启动进程 + 轮询状态最多几百毫秒），
/// 绝不放到 UI 线程，否则面板第一次打开会卡一下（红线 2）。
/// </summary>
internal static class EverythingBootstrap
{
    private static int _starting;
    private static string _lastDetail = "";

    /// <summary>最近一次启动尝试的说明（设置页/调试面板直接显示）。</summary>
    public static string LastDetail { get { return _lastDetail; } }

    /// <summary>SDK + 客户端都在位，可以查询。</summary>
    public static bool Ready
    {
        get
        {
            return EverythingApi.IsAvailable && EverythingApi.IsClientRunning && EverythingApi.IsDbLoaded;
        }
    }

    /// <summary>
    /// 确保 Everything 客户端在跑。已经在跑就立刻返回；没在跑就启动内置那份。
    /// </summary>
    /// <param name="waitMs">最多等多久让客户端把窗口建出来（0 = 只发起、不等待）。</param>
    public static bool EnsureClient(int waitMs)
    {
        if (!EverythingApi.IsAvailable)
        {
            _lastDetail = EverythingApi.StatusText;
            return false;
        }
        if (EverythingApi.IsClientRunning)
        {
            _lastDetail = "";
            return true;
        }

        /* 并发保护：Qbar 面板和展开窗口可能同时触发。第二个调用直接返回当前状态，
           不要起两个 Everything 进程（第二个只会把第一个的窗口拉到前台）。 */
        if (Interlocked.Exchange(ref _starting, 1) == 1) return EverythingApi.IsClientRunning;

        try
        {
            if (!EverythingBundle.Ensure())
            {
                _lastDetail = Loc.T("内置 Everything 释放失败：") + EverythingBundle.LastError;
                return false;
            }
            if (!File.Exists(EverythingBundle.ClientPath))
            {
                _lastDetail = Loc.T("程序里没有内置 Everything 客户端");
                return false;
            }

            /* -startup 让 Everything 以"开机自启"的姿态启动（不进前台、不弹主窗口），
               -minimized 再兜一层。用户不会看到第二个窗口跳出来。 */
            var psi = new ProcessStartInfo(EverythingBundle.ClientPath)
            {
                Arguments = "-startup -minimized",
                WorkingDirectory = EverythingBundle.Folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
            _lastDetail = Loc.T("正在启动内置 Everything…");

            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < Math.Max(0, waitMs))
            {
                if (EverythingApi.IsClientRunning) break;
                Thread.Sleep(120);
            }
            if (!EverythingApi.IsClientRunning)
            {
                _lastDetail = Loc.T("内置 Everything 已启动，但还没准备好；稍后会自动连上。");
                return false;
            }
            _lastDetail = "";
            CoreLog.Info("[Qbar] 已启动内置 Everything：" + EverythingBundle.ClientPath);
            return true;
        }
        catch (Exception ex)
        {
            _lastDetail = Loc.T("启动内置 Everything 失败：") + ex.Message;
            CoreLog.Swallow(ex, "启动内置 Everything");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _starting, 0);
        }
    }

    /// <summary>
    /// 后台预热：启动客户端，然后等索引库加载完（首次建索引可能要几十秒）。
    /// 调用方不等待 —— 这是"让用户下次打开面板就是好的"的准备动作。
    /// </summary>
    public static void WarmupAsync()
    {
        Task.Run(() =>
        {
            try
            {
                if (!EverythingApi.IsAvailable) return;
                if (!EverythingApi.IsClientRunning) EnsureClient(4000);
                /* 索引没加载完也先返回：查询路径自己会显示"索引建立中"，
                   在这里同步等只会白占一个线程。 */
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "Everything 预热"); }
        });
    }

    /// <summary>给设置页用的一句话状态。</summary>
    public static string StatusText()
    {
        if (!EverythingApi.IsAvailable) return EverythingApi.StatusText;
        if (!EverythingApi.IsClientRunning) return Loc.T("Everything 没在运行（文件搜索会返回空）");
        if (!EverythingApi.IsDbLoaded) return Loc.T("Everything 正在建立索引，稍等");
        return EverythingApi.StatusText;
    }
}
