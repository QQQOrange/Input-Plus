using System;
using System.Windows;
using System.Windows.Interop;
using InputPlus.Core.Interop;

namespace InputPlus.Core.Windows;

/// <summary>
/// 浮层定位：按**物理像素 + 目标显示器的工作区**摆放。
///
/// 为什么需要它（踩过的坑）：
///   · `SystemParameters.WorkArea` 是"**系统 DPI** 下的 DIP"，而 WPF 的 `Left/Top`
///     是"**窗口所在显示器** DPI 下的 DIP"。在 200% 缩放的机器上两者差一倍，
///     在混 DPI 的多屏上更是互相错位 —— 浮层会跑到屏幕外或压到任务栏。
///   · 而且它永远只认主屏：用户在副屏上打字，Qbar / 提示却弹到主屏。
///
/// 这里统一改成：`MonitorFromWindow` 取显示器 → `GetMonitorInfo` 取工作区（物理像素）
/// → 用 `GetDpiForWindow` 把窗口尺寸换成物理像素 → `SetWindowPos` 直接落位。
/// </summary>
public static class ScreenPlacement
{
    /// <summary>锚点。</summary>
    public enum Anchor
    {
        /// <summary>水平居中、贴工作区顶部（+ 边距）。</summary>
        TopCenter,

        /// <summary>水平居中、落在工作区上方 22% 处（Qbar 的观感位置）。</summary>
        UpperCenter,

        /// <summary>工作区右下角（提示条）。</summary>
        BottomRight,
    }

    /// <summary>
    /// 摆放窗口（只挪位置，不改尺寸、不改 Z 序、不抢焦点）。
    /// </summary>
    /// <param name="window">已 Show 或至少已建句柄的窗口。</param>
    /// <param name="anchor">锚点。</param>
    /// <param name="marginX">左右边距（DIP）。</param>
    /// <param name="marginY">上下边距（DIP）。</param>
    /// <param name="target">以哪个窗口所在显示器为准；默认用自己。</param>
    /// <returns>摆放成功返回 true；拿不到句柄/显示器信息时返回 false（调用方可退回旧逻辑）。</returns>
    public static bool Place(Window window, Anchor anchor,
        double marginX = 12, double marginY = 12, IntPtr target = default)
    {
        try
        {
            if (!TryResolveWork(window, target, out IntPtr hwnd, out var work, out double scale)) return false;

            // 尺寸用 DIP → 物理像素；窗口还没排过版时退回显式 Width/Height
            double dipW = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
            double dipH = window.ActualHeight > 1 ? window.ActualHeight : window.Height;
            int w = (int)Math.Round(Math.Max(0, dipW) * scale);
            int h = (int)Math.Round(Math.Max(0, dipH) * scale);

            int mx = (int)Math.Round(marginX * scale);
            int my = (int)Math.Round(marginY * scale);

            var (x, y) = ComputePoint(anchor, work, w, h, mx, my);

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("浮层定位失败，退回默认位置：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 摆放窗口：**水平居中**，纵向按 0..1 的比例落在工作区里
    /// （0 = 贴工作区顶，1 = 贴工作区底，越界自动夹住）。
    ///
    /// 手势名称框的「位置」就吃这个（`top + (workHeight - h) * (1 - position)`）；
    /// 提示条则固定贴右下角，走带 <see cref="Anchor"/> 的重载，不再读这里的比例。
    /// </summary>
    /// <param name="window">已 Show 或至少已建句柄的窗口。</param>
    /// <param name="position">0..1 的纵向位置（0 贴顶、1 贴底）。</param>
    /// <param name="target">以哪个窗口所在显示器为准；默认用自己。</param>
    public static bool PlaceByFraction(Window window, double position, IntPtr target = default)
    {
        try
        {
            if (!TryResolveWork(window, target, out IntPtr hwnd, out var work, out double scale)) return false;

            // 尺寸用 DIP → 物理像素；窗口还没排过版时退回显式 Width/Height
            double dipW = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
            double dipH = window.ActualHeight > 1 ? window.ActualHeight : window.Height;
            int w = (int)Math.Round(Math.Max(0, dipW) * scale);
            int h = (int)Math.Round(Math.Max(0, dipH) * scale);

            var (x, y) = ComputeFractionPoint(work, w, h, position);

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("通知摆位失败，退回默认位置：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 纯函数：水平居中 + 按比例纵向定位，返回窗口左上角的**物理像素**坐标。
    /// 跟 <see cref="ComputePoint"/> 一样拆出来，方便脱开屏幕做断言。
    /// </summary>
    internal static (int X, int Y) ComputeFractionPoint(
        NativeMethods.RECT work, int w, int h, double position)
    {
        double pos = double.IsNaN(position) ? 0.0 : Math.Clamp(position, 0.0, 1.0);
        int x = work.Left + (work.Width - w) / 2;
        int y = work.Top + (int)Math.Round((work.Height - h) * (1.0 - pos));

        // 和 ComputePoint 同样的夹取：窗口比工作区大时贴左上，不跑到屏幕外
        int maxX = Math.Max(work.Left, work.Right - w);
        int maxY = Math.Max(work.Top, work.Bottom - h);
        x = Math.Clamp(x, work.Left, maxX);
        y = Math.Clamp(y, work.Top, maxY);
        return (x, y);
    }

    /// <summary>
    /// 取「以哪块屏为准」的工作区与缩放。Place / PlaceByFraction 共用，
    /// 免得两处各写一遍 MonitorFromWindow + GetMonitorInfo + ScaleOf。
    /// </summary>
    private static bool TryResolveWork(Window window, IntPtr target,
        out IntPtr hwnd, out NativeMethods.RECT work, out double scale)
    {
        hwnd = IntPtr.Zero;
        work = default;
        scale = 1.0;

        var helper = new WindowInteropHelper(window);
        hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) hwnd = helper.EnsureHandle();
        if (hwnd == IntPtr.Zero) return false;

        IntPtr from = target != IntPtr.Zero ? target : hwnd;
        IntPtr monitor = NativeMethods.MonitorFromWindow(from, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var mi = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;

        work = mi.rcWork;
        if (work.Width <= 0 || work.Height <= 0) return false;

        scale = ScaleOf(monitor, hwnd);
        return true;
    }

    /// <summary>
    /// 取窗口当前所在显示器的工作区，单位是**该显示器 DPI 下的 DIP**。
    /// 主窗口夹取尺寸、按键页计算最小宽度都用它，避免再混用主屏 WorkArea。
    /// </summary>
    public static bool TryGetWorkAreaDip(Window window, out Rect workArea)
    {
        workArea = Rect.Empty;
        try
        {
            var helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) hwnd = helper.EnsureHandle();
            if (hwnd == IntPtr.Zero) return false;

            IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var mi = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;

            var work = mi.rcWork;
            if (work.Width <= 0 || work.Height <= 0) return false;

            double scale = ScaleOf(monitor, hwnd);
            workArea = new Rect(
                work.Left / scale, work.Top / scale,
                work.Width / scale, work.Height / scale);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("读取显示器工作区失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 纯函数：算出窗口左上角的**物理像素**坐标。
    ///
    /// 单独拆出来有两个原因：
    ///   ① 可以脱开屏幕做单元测试（负坐标的副屏、混 DPI、超小工作区都能造）；
    ///   ② 顺手补上**越界夹取** —— 窗口比工作区还宽/高时（小副屏 + 大浮层、
    ///      或者用户把缩放调到 200% 后换了个小屏），原来的算式会算出负坐标，
    ///      浮层整块跑到屏幕外，用户以为"功能没反应"。
    /// </summary>
    internal static (int X, int Y) ComputePoint(Anchor anchor,
        NativeMethods.RECT work, int w, int h, int mx, int my)
    {
        int x, y;
        switch (anchor)
        {
            case Anchor.BottomRight:
                x = work.Right - w - mx;
                y = work.Bottom - h - my;
                break;

            case Anchor.UpperCenter:
                x = work.Left + (work.Width - w) / 2;
                y = work.Top + (int)Math.Round(work.Height * 0.22);
                break;

            default: // TopCenter
                x = work.Left + (work.Width - w) / 2;
                y = work.Top + my;
                break;
        }

        /* 夹进工作区：窗口比工作区大的时候，宁可贴左上角也不要跑到屏幕外。
         * 上界用 Math.Max 兜住"窗口更大"的情况，否则 Min 会把坐标压成负数。 */
        int maxX = Math.Max(work.Left, work.Right - w);
        int maxY = Math.Max(work.Top, work.Bottom - h);
        x = Math.Clamp(x, work.Left, maxX);
        y = Math.Clamp(y, work.Top, maxY);
        return (x, y);
    }

    /// <summary>
    /// **目标显示器**的缩放（拿不到就退回窗口所在显示器，再不行按 1.0）。
    ///
    /// 必须问显示器而不是窗口：Qbar / Toast 是在 `Open()` 里、窗口句柄刚建好
    /// 还停在上一块屏（常常是主屏）的时候摆位的。按 `GetDpiForWindow` 取缩放，
    /// 100% 主屏 + 150% 副屏的组合下会把 760 DIP 宽的面板算成 760 物理像素，
    /// 而 WPF 随后按 150% 重新缩放成 1140 —— 面板整体偏右、Toast 压到任务栏下面。
    /// </summary>
    private static double ScaleOf(IntPtr monitor, IntPtr hwnd)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI,
                                               out uint dx, out _) == 0 && dx > 0)
                return dx / 96.0;
        }
        catch
        {
            // 老系统（Win7）没有这个 API，走下面的兜底
        }

        try
        {
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch
        {
            // 同样：极老系统上没有
        }
        return 1.0;
    }
}
