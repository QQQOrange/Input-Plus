using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InputPlus.Core.Interop;

namespace InputPlus.Core.Windows;

/// <summary>
/// 「前台程序打开的文件」的那一次识别结果归谁用（v1.5.1）。
///
/// 两个按钮共用同一次扫描，所以这里必须解决三件事：
///   ① **800ms 内复用**：连着点「收藏」「打开位置」，不该跑两遍句柄扫描；
///   ② **一次只有一个在飞**：菜单里手快连点两下，第二下要等第一下的结果，而不是再起一个扫描
///      （扫的是同一个进程，重跑只是把 CPU 花两遍）；
///   ③ **锚点**：菜单一弹出来，前台就变成我们自己的菜单窗口了。
///      所以"要识别的那个窗口"必须在 <see cref="BeginSession"/> 时记下来，
///      点击的时候按锚点扫，**绝不能**重新 GetForegroundWindow。
///
/// 为什么"锚点有效期"是 5 秒而不是 800ms：这两个时钟量的不是同一件事。
/// 800ms 是**结果**的新鲜度（扫描完的文件路径），5 秒是**锚点**的存活期
/// （菜单开着，用户在想点哪个）。菜单自己 400ms 内就可能被判定为"点别处"而收起，
/// 5 秒已经远大于任何真实停留时间；再长就只是让一个过期窗口句柄留在内存里。
/// </summary>
public static class ForegroundFiles
{
    /// <summary>结果的新鲜度（毫秒）。</summary>
    internal const int FreshMs = 800;

    /// <summary>锚点存活期（毫秒）。</summary>
    internal const int AnchorTtlMs = 5000;

    /// <summary>每个进程最多记几条"上次识别成功"（降级链第 ③ 条）。</summary>
    private const int HistoryPerProcess = 5;

    private static readonly object Gate = new();

    private static ForegroundFile? _last;

    private static Task<ForegroundFileResult>? _inflight;

    private static CancellationTokenSource? _inflightCts;

    private static IntPtr _anchorHwnd;

    private static int _anchorPid;

    private static long _anchorAtMs;

    /* 降级链第 ③ 条：同一个程序上次识别到的是哪个文件。
     *
     * 只放内存、不写盘 —— 这个记录的用途是"刚才还能认出来，这会儿认不出了，
     * 先猜还是那个"。跨进程启动之后"当时开着哪个文件"早就过期了，
     * 写盘只会让下次启动去翻一份陈旧数据。 */
    private static readonly Dictionary<string, List<ForegroundFile>> History =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>仅供自检：绕过真实扫描，直接给结论。</summary>
    internal static Func<IntPtr, CancellationToken, ForegroundFileResult>? ScanOverrideForTest { get; set; }

    /// <summary>
    /// 菜单弹出**之前**抓到的那个窗口。星标点下去时前台已经是我们自己的菜单，
    /// 所以"用户到底在看哪个程序"只能从这里拿（v1.5.2 起星标也用它兜底）。
    /// </summary>
    public static IntPtr AnchorHwnd
    {
        get { lock (Gate) return _anchorHwnd; }
    }

    /// <summary>锚点窗口的进程号（0 = 没有锚点）。</summary>
    public static int AnchorPid
    {
        get { lock (Gate) return _anchorPid; }
    }

    /// <summary>当前缓存（菜单拼装时读它决定星标是不是实心）。</summary>
    public static ForegroundFile? Cached
    {
        get { lock (Gate) return _last; }
    }

    /// <summary>
    /// 该把哪个窗口当成"用户正在操作的东西"（v1.5.4）。
    ///
    /// 鼠标唤出：**指针底下那个窗口**。不能直接用 <c>GetForegroundWindow()</c> ——
    /// 我们的钩子把中键这一下吞掉了，桌面/资源管理器窗口往往根本没被激活，
    /// 前台还停在上一个程序上。于是"在桌面上点中键"会拿上一个程序的类名去判可用性，
    /// 结果是"打开文件所在位置"没置灰，点下去扫的是别人的进程，最后弹出一个手动选文件的框
    /// （真机上就是这么把用户送进 `D:\software\eudic` 这种不相干的目录的）。
    /// 键盘唤出：用户在看的就是前台窗口，指针常常停在任务栏角落，按指针取反而错。
    /// </summary>
    internal static IntPtr ChooseAnchor(bool fromKeyboard, IntPtr probeRoot, IntPtr foreground)
        => !fromKeyboard && probeRoot != IntPtr.Zero ? probeRoot : foreground;
    /// <summary>
    /// 菜单弹出**之前**调用：把"用户正在看的那个窗口"记成锚点。
    ///
    /// 为什么参数是 hwnd 而不是自己在里面取：调用方（GestureService）已经在别处
    /// 取过一次前台窗口，而且必须在 Show() 之前 —— 那个时机只有它知道。
    /// </summary>
    public static void BeginSession(IntPtr hwnd, int pid)
    {
        lock (Gate)
        {
            bool same = hwnd == _anchorHwnd && pid == _anchorPid;
            _anchorHwnd = hwnd;
            _anchorPid = pid;
            _anchorAtMs = Environment.TickCount64;

            /* 同一窗口再弹一次菜单（用户连按两下中键）：缓存留着，正好用得上。
             * 换了窗口就把旧结果丢掉，免得把上一个程序的文件当成这个程序的。 */
            if (!same) _last = null;
        }
    }

    /// <summary>菜单关掉了：锚点作废，但**结果**留着 —— 800ms 内用户可能又开一次菜单。</summary>
    public static void EndSession()
    {
        lock (Gate)
        {
            _anchorHwnd = IntPtr.Zero;
            _anchorPid = 0;
            _anchorAtMs = 0;
        }
    }

    /// <summary>锚点还活着吗（菜单可能还开着）。</summary>
    private static bool AnchorAlive(out IntPtr hwnd)
    {
        hwnd = _anchorHwnd;
        if (hwnd == IntPtr.Zero) return false;
        return Environment.TickCount64 - _anchorAtMs <= AnchorTtlMs;
    }

    /// <summary>
    /// 拿"前台程序打开的文件"：缓存新鲜就直接给，否则扫一次。
    ///
    /// 返回的 Task **不会**因为取消而抛（取消按"识别失败"处理）——
    /// 调用点在菜单的点击回调里，抛出去只会变成一条无意义的界面线程异常。
    /// </summary>
    public static Task<ForegroundFileResult> GetOrRefreshAsync(CancellationToken ct = default)
    {
        IntPtr hwnd;
        int pid;

        lock (Gate)
        {
            if (!AnchorAlive(out hwnd))
            {
                hwnd = SafeForegroundWindow();
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint live);
                pid = (int)live;
                _anchorHwnd = hwnd;
                _anchorPid = pid;
                _anchorAtMs = Environment.TickCount64;
            }
            else
            {
                pid = _anchorPid;
            }

            ForegroundFile? cached = _last;
            if (cached != null && cached.Hwnd == hwnd && cached.Pid == pid
                && (DateTime.Now - cached.At).TotalMilliseconds <= FreshMs)
                return Task.FromResult(ForegroundFileResult.Ok(cached));

            /* 一次只允许一个在飞：连点两下合并成同一个 Task。 */
            if (_inflight != null && !_inflight.IsCompleted) return _inflight;

            _inflightCts?.Dispose();
            _inflightCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            CancellationTokenSource cts = _inflightCts;
            IntPtr target = hwnd;
            int targetPid = pid;

            _inflight = Task.Run(() => RunScan(target, targetPid, cts.Token), CancellationToken.None);
            return _inflight;
        }
    }

    /// <summary>扫描 + 记历史 + 填缓存。异常在这里收口，绝不漏到 Task 之外。</summary>
    private static ForegroundFileResult RunScan(IntPtr hwnd, int pid, CancellationToken ct)
    {
        ForegroundFileResult result;
        try
        {
            var overrideScan = ScanOverrideForTest;
            result = overrideScan != null
                ? overrideScan(hwnd, ct)
                : ForegroundFileProbe.Scan(hwnd, ct);

            /* 句柄快照失败 → 看这个程序上次认出来的是什么（降级链第 ③ 条）。 */
            if (result.File == null && result.Outcome is ForegroundFileOutcome.NoCandidate or ForegroundFileOutcome.Timeout)
            {
                ForegroundFile? remembered = RememberedFor(pid);
                if (remembered != null)
                {
                    result = ForegroundFileResult.Ok(remembered with { Hwnd = hwnd, Pid = pid, At = DateTime.Now });
                    CoreLog.Info($"[收藏] 句柄没结果，改用该进程上次认出来的文件：{remembered.Path}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = ForegroundFileResult.Fail(ForegroundFileOutcome.Timeout, "被取消");
        }
        catch (Exception ex)
        {
            CoreLog.Error("收藏：识别前台文件失败", ex);
            result = ForegroundFileResult.Fail(ForegroundFileOutcome.NoCandidate, ex.GetType().Name);
        }

        lock (Gate)
        {
            if (result.File != null)
            {
                _last = result.File;
                NoteSuccessLocked(result.File);
            }
            _inflight = null;
        }
        return result;
    }

    /// <summary>把一次成功记进"按进程"的历史（同路径前移，最多 5 条）。</summary>
    private static void NoteSuccessLocked(ForegroundFile file)
    {
        if (file.ProcessName.Length == 0) return;
        if (!History.TryGetValue(file.ProcessName, out List<ForegroundFile>? list))
        {
            list = new List<ForegroundFile>(HistoryPerProcess);
            History[file.ProcessName] = list;
        }

        list.RemoveAll(f => string.Equals(f.Path, file.Path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, file);
        while (list.Count > HistoryPerProcess) list.RemoveAt(list.Count - 1);

        /* 进程名进进出出（每次重开浏览器都是同一条），不淘汰就无限长。 */
        if (History.Count > 64)
        {
            string? oldest = null;
            long oldestAt = long.MaxValue;
            foreach (var pair in History)
            {
                long at = pair.Value.Count > 0 ? pair.Value[^1].At.Ticks : 0;
                if (at >= oldestAt) continue;
                oldestAt = at;
                oldest = pair.Key;
            }
            if (oldest != null && !string.Equals(oldest, file.ProcessName, StringComparison.OrdinalIgnoreCase))
                History.Remove(oldest);
        }
    }

    /// <summary>
    /// 这个进程上次认出来的、**现在还在**的文件（最新的优先）。
    /// 只认同一个 pid，避免"重开程序后 pid 复用了别人的记录"。
    /// </summary>
    internal static ForegroundFile? RememberedFor(int pid)
    {
        lock (Gate)
        {
            foreach (var pair in History)
            {
                foreach (ForegroundFile f in pair.Value)
                {
                    if (f.Pid != pid) continue;
                    try { if (!File.Exists(f.Path)) continue; } catch { continue; }
                    return f;
                }
            }
        }
        return null;
    }

    /// <summary>仅供自检：清空缓存、锚点与历史。</summary>
    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _last = null;
            _inflight = null;
            _inflightCts?.Dispose();
            _inflightCts = null;
            _anchorHwnd = IntPtr.Zero;
            _anchorPid = 0;
            _anchorAtMs = 0;
            History.Clear();
        }
        ScanOverrideForTest = null;
    }

    /// <summary>把一次识别结果直接塞进缓存（自检与"手动选择文件"兜底用）。</summary>
    internal static void SeedForTest(ForegroundFile file)
    {
        lock (Gate)
        {
            _last = file;
            NoteSuccessLocked(file);
        }
    }

    private static IntPtr SafeForegroundWindow()
    {
        try { return NativeMethods.GetForegroundWindow(); }
        catch { return IntPtr.Zero; }
    }
}
