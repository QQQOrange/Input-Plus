using System.Diagnostics;
using System.Runtime.InteropServices;
using InputPlus.Core.Interop;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Windows;

/// <summary>
/// 「唤出窗口」的短促缩放动画：目标窗口从 0.92 倍放大回原位（约 170ms，ease-out）。
///
/// ## 为什么是"缩放归位"
///
/// Capslox 的引擎本体（Rust）里没有任何窗口动画代码 —— <c>AnimateWindow</c> /
/// <c>SetLayeredWindowAttributes</c> / <c>DwmGetWindowAttribute</c> 全都没有命中，
/// 文档里也没有 easing 描述。所以这里按它的视觉风格自己实现：
/// 屏幕上看不到淡入淡出，只有窗口"弹"回自己的位置，短、干、不抢注意力。
///
/// ## 三条硬规则
///
/// ① **纯装饰**：动画失败绝不影响切换窗口本身，所有入口 try/catch 全吞；
/// ② **收尾一定回正**：<c>finally</c> 里无条件把窗口摆回开始时的精确矩形，
///    中途被用户拖过 / 被别的程序改过也要回正（宁可回正，也不要留下一个
///    尺寸不对的窗口 —— 那才是真的坏）；
/// ③ **不阻塞调用方**：动画跑在后台线程上，调用它的是热键队列线程，
///    不能在那里 sleep 170ms。
/// 之所以不用 <see cref="System.Windows.Threading.DispatcherTimer"/>：它要求有活着的
/// WPF Dispatcher，自检（--selftest）和离屏环境里不一定有；而且这里根本不需要
/// 回到 UI 线程做什么 —— SetWindowPos 可以在任意线程调。
/// </summary>
public static class WindowSummonAnimator
{
    /// <summary>起始缩放比例。0.92 = 缩 8%，约 60px（1500px 宽的窗口），看得见但不夸张。</summary>
    public const double StartScale = 0.92;

    /// <summary>默认时长（毫秒）。170ms 是"能感知到、但不会等"的量级。</summary>
    public const int DefaultDurationMs = 170;

    public const int MinDurationMs = 60;
    public const int MaxDurationMs = 400;

    /// <summary>帧步进。10ms ≈ 100fps 的节奏，170ms 走 17 帧。</summary>
    private const int FrameStepMs = 10;

    /// <summary>宽或高小于这个值就不动它（工具条、浮动小面板缩放起来像抽搐）。</summary>
    private const int MinSidePx = 160;

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    /* 同一个窗口同时只允许一个动画。后到的**直接跳过**而不是排队：
     * 用户连按两次窗口键（切过去 → 收起）时，排队会让窗口先抽一下再收起。 */
    private static readonly object Gate = new();
    private static readonly HashSet<long> Busy = new();

    // ---- DWM：NativeMethods 里只有 Set 版，Get 版放这里，不往那个大文件里塞 ----

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);

    // ===================== 纯函数（自检直接测这些） =====================

    /// <summary>ease-out cubic：起步快、收尾慢。端点 0→0、1→1，全程单调递增。</summary>
    internal static double EaseOutCubic(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double u = 1 - t;
        return 1 - u * u * u;
    }

    /// <summary>
    /// 把设置里填的时长夹进 60~400 毫秒；≤0 视为"用默认值"。
    /// 设置界面与播放走**同一个**函数，保证 UI 上看到的就是实际生效的。
    /// </summary>
    internal static int ClampDuration(int ms)
    {
        if (ms <= 0) return DefaultDurationMs;
        if (ms < MinDurationMs) return MinDurationMs;
        if (ms > MaxDurationMs) return MaxDurationMs;
        return ms;
    }

    /// <summary>某一时刻（毫秒）应该用的缩放比例。</summary>
    internal static double ScaleAt(int elapsedMs, int durationMs)
    {
        if (durationMs <= 0) return 1.0;
        double t = elapsedMs / (double)durationMs;
        return StartScale + (1.0 - StartScale) * EaseOutCubic(t);
    }

    /// <summary>
    /// 算出某一帧该把**窗口矩形**摆成什么样：绕 <paramref name="visual"/> 的中心
    /// 缩放，同时保持窗口矩形与可见外框之间的固定偏移 —— 这样缩放过程中
    /// 屏幕上的可见边框始终居中，不会一边缩一边往右下飘。
    ///
    /// <paramref name="scale"/> 为 1 时结果必须**逐像素等于** <paramref name="window"/>，
    /// 这是"收尾回正"的数学保证（自检也断言这一条）。
    /// </summary>
    internal static (int X, int Y, int Width, int Height) FrameRect(RECT window, RECT visual, double scale)
    {
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;

        double visualCx = (visual.Left + visual.Right) / 2.0;
        double visualCy = (visual.Top + visual.Bottom) / 2.0;
        double offX = ((window.Left + window.Right) / 2.0) - visualCx;
        double offY = ((window.Top + window.Bottom) / 2.0) - visualCy;

        int w = (int)Math.Round(window.Width * scale);
        int h = (int)Math.Round(window.Height * scale);
        int x = (int)Math.Round(visualCx + offX - w / 2.0);
        int y = (int)Math.Round(visualCy + offY - h / 2.0);
        return (x, y, w, h);
    }

    internal static (int X, int Y, int Width, int Height) FrameRect(RECT window, double scale)
        => FrameRect(window, window, scale);

    /// <summary>两个矩形四条边都在 <paramref name="tol"/> 像素内算相同。</summary>
    internal static bool NearlySameRect(RECT a, RECT b, int tol)
        => Math.Abs(a.Left - b.Left) <= tol
        && Math.Abs(a.Top - b.Top) <= tol
        && Math.Abs(a.Right - b.Right) <= tol
        && Math.Abs(a.Bottom - b.Bottom) <= tol;

    /// <summary>这个窗口是不是本程序自己的（设置窗口、Qbar 面板等）。</summary>
    internal static bool IsOwnProcess(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid != 0 && pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    // ===================== 判定 =====================

    /// <summary>这个窗口能不能做唤出动画（纯判定，不动窗口）。</summary>
    public static bool IsAnimatable(IntPtr hwnd) => IsAnimatable(hwnd, out _);

    /// <summary>同上，额外给出不能动的**原因**（自检和排查用）。</summary>
    internal static bool IsAnimatable(IntPtr hwnd, out string reason)
    {
        reason = "";
        try
        {
            if (hwnd == IntPtr.Zero) { reason = "空句柄"; return false; }
            if (!IsWindow(hwnd)) { reason = "不是窗口"; return false; }
            if (IsOwnProcess(hwnd)) { reason = "本程序自己的窗口"; return false; }
            if (!IsWindowVisible(hwnd)) { reason = "窗口不可见"; return false; }
            if (IsIconic(hwnd)) { reason = "最小化"; return false; }
            if (IsZoomed(hwnd)) { reason = "最大化"; return false; }
            if (IsCloaked(hwnd)) { reason = "UWP 幽灵窗口"; return false; }

            RECT win = WindowService.GetRect(hwnd);
            RECT vis = VisualRect(hwnd, win);
            if (vis.Width < MinSidePx || vis.Height < MinSidePx) { reason = "窗口太小"; return false; }
            if (CoversWorkArea(hwnd, vis)) { reason = "全屏/伪全屏"; return false; }

            /* 激活没成功就别动它 —— 否则用户看到的是"后台窗口抽搐了一下"。
             * 注意这一条放在最后：前面的判定都更便宜、也更容易解释。 */
            if (GetForegroundWindow() != hwnd) { reason = "不在前台"; return false; }

            return true;
        }
        catch
        {
            reason = "判定异常";
            return false;
        }
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            // S_OK 才算读到；老系统 / 非 DWM 环境按"没 cloaked"处理，不因此跳过。
            return DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                   && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 窗口的"真实可见外框"：DWM 的 EXTENDED_FRAME_BOUNDS 去掉了 Win10 之后
    /// 那圈透明的可拖拽边框，取它算中心才不会偏。取不到就退回窗口矩形。
    /// </summary>
    private static RECT VisualRect(IntPtr hwnd, RECT fallback)
    {
        try
        {
            int size = Marshal.SizeOf(typeof(RECT));
            if (DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, size) == 0
                && frame.Width > 0 && frame.Height > 0)
            {
                return frame;
            }
        }
        catch
        {
            // 无 DWM（老系统 / 离屏 / 远程会话）时走 GetWindowRect
        }
        return fallback;
    }

    /// <summary>窗口几乎盖满所在显示器的工作区 → 视为全屏或伪全屏，不做缩放。</summary>
    private static bool CoversWorkArea(IntPtr hwnd, RECT vis)
    {
        try
        {
            RECT work = WindowService.WorkArea(hwnd);
            if (work.Width <= 0 || work.Height <= 0) return false;
            if (NearlySameRect(vis, work, 8)) return true;

            long overlapW = Math.Min(vis.Right, work.Right) - Math.Max(vis.Left, work.Left);
            long overlapH = Math.Min(vis.Bottom, work.Bottom) - Math.Max(vis.Top, work.Top);
            if (overlapW <= 0 || overlapH <= 0) return false;

            // 98.5% 以上：既盖住全屏无边框程序，也放过"刚好最大化但没真最大化"的窗口。
            return overlapW * overlapH * 1000 >= (long)work.Width * work.Height * 985;
        }
        catch
        {
            return false;
        }
    }

    // ===================== 播放 =====================

    /// <summary>
    /// 播一次唤出动画。**永不抛异常**：任何失败都静默返回。
    /// </summary>
    /// <param name="hwnd">目标窗口（应当是刚被激活的那个顶层窗口）</param>
    /// <param name="durationMs">时长；≤0 用默认值，超出 60~400 自动夹取</param>
    public static void Play(IntPtr hwnd, int durationMs)
    {
        try
        {
            if (!IsAnimatable(hwnd, out _)) return;

            int ms = ClampDuration(durationMs);

            long key = hwnd.ToInt64();
            lock (Gate)
            {
                if (!Busy.Add(key)) return;   // 已经在动 → 后到的直接跳过
            }

            var thread = new Thread(() => Run(hwnd, ms))
            {
                IsBackground = true,              // 关程序时不拖住进程退出
                Name = "WindowSummonAnimator",
            };
            thread.Start();
        }
        catch
        {
            // 动画是纯装饰，不影响窗口切换
        }
    }

    private static void Run(IntPtr hwnd, int durationMs)
    {
        RECT origin = default;
        bool hasOrigin = false;
        try
        {
            origin = WindowService.GetRect(hwnd);
            hasOrigin = origin.Width > 0 && origin.Height > 0;
            if (!hasOrigin) return;

            RECT visual = VisualRect(hwnd, origin);
            var clock = Stopwatch.StartNew();

            while (true)
            {
                int elapsed = (int)clock.ElapsedMilliseconds;
                if (elapsed >= durationMs) break;

                // 中途被最小化/最大化/关掉就停手（收尾仍会把窗口摆回原矩形）。
                if (!IsWindow(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd)) break;

                double scale = ScaleAt(elapsed, durationMs);
                var f = FrameRect(origin, visual, scale);
                Move(hwnd, f.X, f.Y, f.Width, f.Height);

                int wait = Math.Min(FrameStepMs, durationMs - elapsed);
                if (wait > 0) Thread.Sleep(wait);
            }
        }
        catch
        {
            // 吞掉：任何一帧失败都不该让调用方看到异常
        }
        finally
        {
            /* 无条件回正。哪怕窗口在中途被拖动 / 被别的程序改过尺寸，
             * 也摆回开始时的矩形 —— 留一个 0.93 倍大小的窗口是更糟的结果。 */
            try
            {
                if (hasOrigin && IsWindow(hwnd) && !IsIconic(hwnd))
                    Move(hwnd, origin.Left, origin.Top, origin.Width, origin.Height);
            }
            catch
            {
            }

            lock (Gate) Busy.Remove(hwnd.ToInt64());
        }
    }

    private static void Move(IntPtr hwnd, int x, int y, int w, int h)
    {
        try
        {
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        }
        catch
        {
        }
    }
}
