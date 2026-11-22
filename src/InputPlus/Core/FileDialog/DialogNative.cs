using System.Runtime.InteropServices;
using System.Text;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// 文件对话框这一块用到的 Win32 声明。
///
/// 单独一份而不是塞进 <see cref="InputPlus.Core.Interop.NativeMethods"/>：
/// 那一份是按键/输入共用的，把窗口枚举、DWM、进程查询混进去以后
/// 谁都不敢删改；这里只服务文件对话框，删掉整块就等于关掉本功能。
/// </summary>
internal static class DialogNative
{
    // ---------------- 窗口事件（SetWinEventHook） ----------------
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    /// <summary>系统前台窗口变化（不是对象事件，编号体系不同）。</summary>
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // ---------------- 窗口样式与定位 ----------------
    /// <summary>GetWindow 的 GW_ENABLEDPOPUP：取该窗口当前启用的弹出窗口（模态确认框走这里）。</summary>
    public const int GW_ENABLEDPOPUP = 6;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_HWNDPARENT = -8;
    public const int GW_OWNER = 4;

    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint GA_ROOT = 2;

    // ---------------- 桌面窗口管理器 ----------------
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    // ---------------- 消息 ----------------
    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_SETTEXT = 0x000C;
    public const uint WM_CLOSE = 0x0010;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public delegate void WinEventDelegate(nint hook, uint eventType, nint hwnd,
        int idObject, int idChild, uint thread, uint time);

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWinEventHook(uint min, uint max, nint module,
        WinEventDelegate proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(nint parent, EnumWindowsProc proc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextW(nint hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLengthW(nint hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsHungAppWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hwnd, int cmd);

    [DllImport("user32.dll")]
    public static extern int GetDlgCtrlID(nint hwnd);

    /* 32 位进程里 user32 没有导出 GetWindowLongPtrW（它是宏）。
       两个入口都声明出来，由包装函数按位宽选。 */
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hwnd, int index, int value);

    public static nint GetWindowLongPtr(nint hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    public static nint SetWindowLongPtr(nint hwnd, int index, nint value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, (int)value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hwnd, int cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll")]
    public static extern nint GetFocus();

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(nint hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AllowSetForegroundWindow(uint pid);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(nint process, uint flags,
        StringBuilder name, ref int size);

    // ---------------- 小工具 ----------------

    /// <summary>读窗口类名；失败返回空串。</summary>
    public static string ClassOf(nint hwnd)
    {
        if (hwnd == 0) return "";
        try
        {
            var sb = new StringBuilder(256);
            int n = GetClassNameW(hwnd, sb, sb.Capacity);
            return n > 0 ? sb.ToString(0, n) : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 读窗口标题。
    ///
    /// 跨进程读标题本质是 SendMessage(WM_GETTEXT)：目标没响应时会阻塞到系统超时。
    /// 所以先用 IsHungAppWindow 排除卡死窗口，再取长度，最后才取文本。
    /// 任何一步失败都返回空串 —— 没有标题不影响判型。
    /// </summary>
    public static string TitleOf(nint hwnd)
    {
        if (hwnd == 0) return "";
        try
        {
            if (IsHungAppWindow(hwnd)) return "";
            int len = GetWindowTextLengthW(hwnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(Math.Min(len + 2, 1024));
            int got = GetWindowTextW(hwnd, sb, sb.Capacity);
            return got > 0 ? sb.ToString(0, got) : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 取窗口所在显示器的**物理**工作区。
    ///
    /// 不能直接用 WinForms 的 Screen.WorkingArea：混合 DPI 下它会按进程感知级别
    /// 返回虚拟化坐标，而 SetWindowPos 收的是物理像素 —— 高缩放下两者差 2 倍，
    /// 表现为浮层"贴到屏幕外"（用户看到什么都没出现）。
    /// </summary>
    public static bool TryGetWorkArea(nint hwnd, out RECT work)
    {
        work = default;
        try
        {
            nint monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor == 0) return false;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(monitor, ref info)) return false;
            work = info.rcWork;
            return true;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

    /// <summary>
    /// 取对话框"真实边框"：DWM 的 EXTENDED_FRAME_BOUNDS 不含 Win10 之后的
    /// 透明阴影边距，拿它对齐浮层不会差出 7~11px。取不到退回 GetWindowRect。
    /// </summary>
    public static DialogRect FrameBounds(nint hwnd)
    {
        if (hwnd == 0) return DialogRect.Empty;

        try
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwm,
                    Marshal.SizeOf<RECT>()) == 0)
            {
                var r = new DialogRect(dwm.Left, dwm.Top, dwm.Right, dwm.Bottom);
                if (!r.IsEmpty) return r;
            }
        }
        catch { /* 老系统 / 远程会话没有 DWM：走下面的退路 */ }

        try
        {
            if (GetWindowRect(hwnd, out RECT win))
                return new DialogRect(win.Left, win.Top, win.Right, win.Bottom);
        }
        catch { /* 窗口已经销毁 */ }

        return DialogRect.Empty;
    }

    /// <summary>取窗口所属进程的 exe 全路径；失败返回空串。</summary>
    public static string ProcessPathOf(uint pid)
    {
        if (pid == 0) return "";
        nint h = 0;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == 0) return "";
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : "";
        }
        catch { return ""; }
        finally
        {
            if (h != 0) CloseHandle(h);
        }
    }

    /// <summary>枚举直接子窗口（不递归）。</summary>
    public static List<nint> Children(nint parent)
    {
        var list = new List<nint>();
        if (parent == 0) return list;
        try
        {
            EnumChildWindows(parent, (hwnd, _) =>
            {
                /* EnumChildWindows 会把所有后代都回调过来；这里保留全部，
                 * 因为 IFileDialog 的编辑框常常隔着一层 FloatNotifySink。 */
                list.Add(hwnd);
                return true;
            }, 0);
        }
        catch { /* 目标进程正在销毁窗口 */ }
        return list;
    }
}
