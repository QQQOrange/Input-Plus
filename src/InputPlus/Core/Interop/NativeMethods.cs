using System.Runtime.InteropServices;
using System.Text;

namespace InputPlus.Core.Interop;

/// <summary>Win32 互操作声明（仅保留本程序用到的部分）。</summary>
public static class NativeMethods
{
    // ---------------- 窗口消息 ----------------
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_XBUTTONUP = 0x020C;

    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;

    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_EXTENDED = 0x01;

    // ---------------- 输入 ----------------
    public const uint INPUT_KEYBOARD = 1;
    public const uint INPUT_MOUSE = 0;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    /* 侧键：不是独立 flag，而是 XDOWN/XUP + mouseData = XBUTTON1 / XBUTTON2。 */
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern ushort MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    // ---------------- 钩子 ----------------
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    /* ---- 桌面（Desktop）----
     *
     * 低级钩子绑定的是"装它的那条线程所在的桌面"。远程桌面 / 向日葵 / 切换用户 /
     * 锁屏解锁之后，输入桌面可能和进程所在桌面**不是同一个**：这时 SetWindowsHookEx
     * 照样返回有效句柄，但**回调永远不触发**，而 Raw Input 仍然收得到 ——
     * 这正是"日志里钩子正常、Raw Input 有键、钩子一个没收到"的典型形态。 */
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex,
        System.Text.StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>
    /// 删文件（也能删交替数据流，如 "xxx.exe:Zone.Identifier"）。
    /// .NET 没有删 ADS 的托管 API，只能走这层。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool DeleteFile(string lpFileName);

    // ---------------- 窗口 ----------------
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>按类名找顶层窗口（自检要找任务栏 Shell_TrayWnd 验证 Z 序）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    /* 「输入文本」用真实按键敲字时，必须按**前台窗口所在线程的键盘布局**
     * 把字符映射成虚拟键 —— 否则德语键盘上打 @ 会变成别的字符。 */
    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

    /* 「输入文本」走真实按键时，中文/日文/韩文/越南文输入语言下字母会被
     * 输入法拿去拼候选 —— 用户看到的是候选框，文字没进文本框。
     *
     * ⚠ 这个问题的解法**不是**切布局。曾经的做法是"临时把窗口的输入语言切到
     * 英文布局，打完再切回"，代价是软件替用户换输入法（用户原话：
     * 「input plus 不应该能够自己换输入法，这是个很严重的 bug」），
     * 而且切换是异步的，目标程序不理会或切回失败就把人永久留在英文布局上。
     * 现在：输入法开着就改走 Unicode 注入（不经过布局与输入法），
     * 布局只用来**读**（把字符映射成虚拟键），见 Core/Input/ImeGuard.cs。
     *
     * LoadKeyboardLayout 保留在这里是因为它只把布局加载进进程（不激活任何窗口），
     * 自检用它的 HKL 核对 Shift 位与扫描码。 */
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

    /* 枚举当前线程/系统里已加载的键盘布局。
     * 目前**没有调用方**（原来用来挑一个"用户平时用的中文布局"再切过去）；
     * 声明留着是因为它是只读的，将来做"当前装了哪些输入法"的诊断还会用到。 */
    [DllImport("user32.dll")]
    public static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    /* ImmGetContext / ImmGetConversionStatus 用于**读**输入法状态（组字串、
     * 中/英模式），只读不改，可以放心用。
     *
     * ⚠ ImmSetConversionStatus（写）目前**没有调用方**，也不该再加：
     * 它会把用户的输入法模式改成我们要的样子 —— 和"软件不该自己换输入法"
     * 是同一类事。别名展开遇到组字时用的是 ImmNotifyIME 取消组字，
     * 不是改 conversion mode。 */
    [DllImport("imm32.dll")]
    public static extern IntPtr ImmGetContext(IntPtr hwnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ImmGetConversionStatus(IntPtr himc, out uint conversion, out uint sentence);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ImmSetConversionStatus(IntPtr himc, uint conversion, uint sentence);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 允许低完整性进程向本窗口发送指定消息（UIPI 过滤例外）。
    /// 用于：提权实例的隐藏唤醒窗口接收普通实例的“再打开一次”。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ChangeWindowMessageFilterEx(
        IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

    public const uint MSGFLT_ALLOW = 1;

    /* 读控件文本（TextMirror 用）：中文别名只能从"控件里真有什么字"拿到，
     * 因为输入法组字对键盘钩子是不可见的。 */
    public const int WM_GETTEXT = 0x000D;
    public const int WM_GETTEXTLENGTH = 0x000E;
    public const uint SMTO_BLOCK = 0x0001;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>取某个线程当前真正有焦点的窗口（顶层窗口不认语言切换消息时改投它）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    /// <summary>
    /// 请目标窗口换输入语言。DefWindowProc 会把它转成 ActivateKeyboardLayout。
    ///
    /// ⚠ 目前**没有调用方**，也不该再有：这条消息一投出去，前台的输入语言就被
    /// 我们改了，而切换是异步的、失败就永久留在英文布局上（用户报的
    /// 「input plus 不应该能够自己换输入法」）。需要"打 ASCII 不被输入法吃掉"时
    /// 改走 Unicode 注入，见 Core/Input/InputSender.cs。
    /// </summary>
    public const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    /* 前台锁定超时：Windows 默认 2000ms 内不允许非前台进程抢焦点，
     * 这正是"面板弹出来了但打字进不去"的根因。
     *
     * 用法有坑：
     *   · GET 把值写进 out uint；
     *   · SET 的值放在 **uiParam**，pvParam 必须为 NULL。
     * 临时置 0 抢到焦点后立刻还原（见 WindowService.ForceForeground）。 */
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
        out uint pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
        IntPtr pvParam, uint fWinIni);

    public const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    public const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    public const uint SPIF_SENDCHANGE = 0x0002;

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>
    /// 窗口是不是"没在响应"（Vista+，user32）。
    ///
    /// 为什么需要：<see cref="GetWindowText"/> 对**别的进程**的窗口本质上是
    /// <c>SendMessage(WM_GETTEXT)</c>，目标卡住时调用方要等系统超时（数秒）。
    /// 我们的取标题代码跑在按键动作线程上 —— 一旦前台那个程序正好卡死
    /// （IDE 编译、游戏加载），按一个读标题的键就会把整条动作队列堵住，
    /// 表现为"所有快捷键都没反应"。取标题前先问一句，卡住就跳过。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    // ---- 顶层窗口 Z 序枚举（点选窗口时用来绕过自己的全屏遮罩）----
    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public const uint GW_HWNDNEXT = 2;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /* 显示器自己的缩放（Win8.1+，Shcore）。
     *
     * GetDpiForWindow 给的是"窗口当前所在显示器"的缩放；而横条要放到
     * **目标显示器**上（可能是另一块），移动之前必须知道那块屏的缩放，
     * 否则会拿着 A 屏的倍率去算 B 屏的像素 —— 多屏且缩放不同时必然错位。
     *
     * 失败（老系统 / 该 API 不可用）时调用方会退回 GetDpiForWindow。 */
    [DllImport("Shcore.dll", ExactSpelling = true)]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType,
        out uint dpiX, out uint dpiY);

    /// <summary>MDT_EFFECTIVE_DPI：用户实际设置的缩放（考虑兼容性重定向）。</summary>
    public const int MDT_EFFECTIVE_DPI = 0;

    /* 任务栏矩形（ABM_GETTASKBARPOS）。
     *
     * ⚠ 已删除。它曾经被用来"让亮度横条避开任务栏"，但这个前提是错的：
     *   任务栏固定时 rcWork 已经扣除了它；任务栏自动隐藏时 rcWork 等于整屏，
     *   而系统横条实测（3840×2160@200%，黑底截图量边缘）**仍然贴着屏幕底**
     *   （下沿距底 28 px），并没有额外让开。多让一次的结果就是整条横条
     *   比系统高 96 px —— 用户反馈"比系统音量条高一个横条"。
     *   定位只用 rcWork，见 OsdWindow.Place()。 */

    /* 窗口所在显示器的 DPI（Win10 1607+）。
     * 定位窗口必须用它，不能用 SystemParameters.WorkArea —— 后者是
     * "系统 DPI 下的 DIP"，而 Left/Top 是"窗口所在显示器 DPI 下的 DIP"，
     * 200% 缩放时两者差一倍，窗口会直接落到屏幕外。 */
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;   // 鼠标点击穿透
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>不发 WM_WINDOWPOSCHANGING（置顶重提升时避免被自己的处理拦下）。</summary>
    public const uint SWP_NOSENDCHANGING = 0x0400;

    /// <summary>
    /// 放到 Z 序最前。
    ///
    /// 与 <see cref="HWND_TOPMOST"/> 的区别是**关键**：
    /// HWND_TOPMOST 只保证"在所有非 topmost 窗口之上"，
    /// 在两个**同为 topmost** 的窗口之间不改变次序 —— 所以任务栏/播放器悬浮条
    /// 顶上来之后，反复发 HWND_TOPMOST 是抢不回来的（实测一秒没动静）。
    /// HWND_TOP 才是"放到 Z 序最顶"，对已经 topmost 的窗口来说
    /// 就等于"站到 topmost 组的最前面"。
    /// </summary>
    public static readonly IntPtr HWND_TOP = new(0);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public static readonly IntPtr HWND_BOTTOM = new(1);

    public const uint GA_ROOT = 2;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>MonitorFromPoint 的兜底：点不在任何显示器上时给主显示器。</summary>
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    // ---------------- 进程 ----------------
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentProcessId();

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    // ================= 窗口带（window band）与分层窗口 =================

    /* 为什么需要这一整块 —— 亮度横条被开始菜单遮挡的根因。
     *
     * Windows 8 起，外壳把窗口分进若干「带」（band）。Z 序先比 band、再比
     * 带内的先后。实测（tools/probe_start_band.py）本机：
     *
     *   普通窗口 / 任务栏        band 1  (DESKTOP)
     *   开始菜单 / 搜索          band 6  (IMMERSIVE_INACTIVEMOBODY)
     *   系统音量条（Shell 自己） band ≥ 6 —— 和开始菜单同一层，所以盖不住
     *
     * 普通进程能用 SetWindowPos 干的事**全都在 band 1 之内**：
     * HWND_TOPMOST 只影响同带内次序，HWND_TOP 也只到 band 1 的顶。
     * 所以不管怎么重提升，开始菜单一打开就是压在我们上面。
     *
     * 唯一不需要 UIAccess 签名的出路：**创建时**就把窗口生进更高的带
     * （CreateWindowInBand）。实测普通进程被允许的带上限是 16：
     * band 2~12 一律返回 ERROR_ACCESS_DENIED(5)，16 可以，
     * 而且 16 > 6 —— 实测能压在开始菜单之上（tools/probe_band16_overlap.py）。
     *
     * SetWindowBand 对已存在的窗口无效（同样 ACCESS_DENIED），所以只能在
     * CreateWindowInBand 里一次定死；WPF 的 Window 不提供这个能力，
     * 于是横条需要一个自己的分层窗口来当宿主。
     */

    /// <summary>band 值（Win8+ 的 ZBID_*）。只列本程序用得到的。</summary>
    public const uint ZBID_DESKTOP = 1;
    public const uint ZBID_IMMERSIVE_MOGO = 4;              // 开始菜单
    public const uint ZBID_IMMERSIVE_INACTIVEMOBODY = 6;    // 搜索/开始（实测）
    public const uint ZBID_UIACCESS = 11;

    /// <summary>
    /// 实测普通进程能创建的最高带。16 在本机（Win11 23H2）可用且高于开始菜单；
    /// 17 起又是 ACCESS_DENIED。换系统版本若不允许，创建会失败并回退老路径。
    /// </summary>
    public const uint ZBID_ABOVE_SHELL = 16;

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    /// <summary>UpdateLayeredWindow 的混合参数（Pack=1 是硬要求）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public uint[] bmiColors;
    }

    public const uint WS_POPUP = 0x80000000;

    /// <summary>有标题栏（含边框）的窗口：真全屏 / 无边框全屏都不会带这两位。</summary>
    public const int WS_CAPTION = 0x00C00000;

    /// <summary>可以用鼠标拖边框改大小的窗口。</summary>
    public const int WS_THICKFRAME = 0x00040000;

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    public const uint AC_SRC_OVER = 0x00;
    public const uint AC_SRC_ALPHA = 0x01;
    public const uint ULW_ALPHA = 0x02;
    public const uint ULW_OPAQUE = 0x04;

    public const int SW_SHOWNA = 8;
    public const int SW_HIDE = 0;

    /// <summary>
    /// 未文档化导出。**按序号之外的名字导出**，所以能用 DllImport 直接绑；
    /// 不存在时（Win7 / 某些精简版）会抛 EntryPointNotFoundException，
    /// 调用方必须接住并回退。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowInBand(
        int dwExStyle, IntPtr atomClass, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam, uint dwBand);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>读窗口所在带。窗口不在任何带里（普通窗口）时返回 false。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowBand(IntPtr hWnd, out uint pdwBand);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey,
        ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits,
        IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr ho);

    // ---------------- 剪贴板 ----------------

    /// <summary>
    /// 剪贴板内容每被改写一次就 +1（读取不加）。用来判断"这期间别人动过剪贴板没有" ——
    /// 比对比文本内容可靠：用户复制了同样的文字也算被改写。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    /// <summary>当前持有剪贴板的窗口；没人持有时为 0。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetOpenClipboardWindow();

    // ---------------- 从各文件收敛过来的声明 ----------------
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeConsole();

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int access, bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr proc, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(IntPtr existing, int access, IntPtr attrs, int impLevel, int tokenType, out IntPtr newToken);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint number);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint arraySize, IntPtr monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode, out int vcpType, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr proc, int flags, System.Text.StringBuilder name, ref int size);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
    public static extern uint AssocQueryString(uint flags, uint str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// 把 "@%SystemRoot%\System32\xxx.dll,-123" 这种**资源引用**解析成人类能读的名字。
    ///
    /// 设备管理器里看到的 "HID Keyboard Device" 就存在注册表的 DeviceDesc 里，
    /// 但现代 HID 设备的 DeviceDesc 全是资源引用，直接读只有一串 @...dll,-123。
    /// SHLoadIndirectString 正是系统自己解析这个格式的入口，一次调用、无 COM 初始化。
    /// </summary>
    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SHLoadIndirectString")]
    public static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    /// <summary>ShellExecuteEx 的参数包。布局照 shellapi.h，x64 下 cbSize=112。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    /// <summary>让 Shell 按 IDList 走外壳动词（"properties" 靠它才走系统属性页）。0x0000000C = INVOKEIDLIST | NOASYNC。</summary>
    public const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);

    /* ── 桌面图标列表的命中测试（中键菜单用） ─────────────────────────────
     *
     * 要回答的问题只有一个：指针底下是桌面图标，还是图标之间的空白？
     * 桌面的图标视图是一个 SysListView32，Explorer 自己知道答案，问它就完了，
     * 别去猜（图标名占多宽、网格间距多少、换了字体/缩放全都会变）。
     *
     * 为什么必须远程分配内存：LVM_HITTEST 的 lParam 是 LVHITTESTINFO*，
     * 对方进程会拿它当指针直接读。传本进程的栈地址过去，在 Explorer 里
     * 就是一段野指针 —— 轻则 iItem 乱给（在空白处弹出菜单），重则 Explorer 崩掉。
     * 所以：OpenProcess → VirtualAllocEx → WriteProcessMemory → 发消息 → ReadProcessMemory → 释放。
     *
     * 位数陷阱：消息号 ≥ WM_USER 的带指针消息**不做跨位数封送**。
     * 32 位的 InputPlus 往 64 位 Explorer 里发，lParam 会被截成 32 位，
     * 对方解引用到的是垃圾地址。调用方必须先比位数（见 BlankHit.SameBitnessAsTarget）。 */
    public const uint LVM_HITTEST = 0x1000 + 18;

    /// <summary>
    /// LVM_GETITEMRECT：wParam = 项下标，lParam = RECT*（跨进程时同样要远程分配）。
    /// 进来之前把 RECT.left 设成要哪种矩形（<see cref="LVIR_BOUNDS"/> = 图标 + 文字的整体包围盒）。
    ///
    /// 用处：给 LVM_HITTEST 的结果做一次复核 —— 真机上见过它报 0（"压在第一个图标上"）
    /// 而光标其实在空白处，那样中键菜单就永远弹不出来。
    /// </summary>
    public const uint LVM_GETITEMRECT = 0x1000 + 14;

    /// <summary>LVM_GETITEMRECT 的 RECT.left 取值：整个项的包围盒（图标 + 标签）。</summary>
    public const int LVIR_BOUNDS = 0;

    /// <summary>LVM_HITTEST 的结构：`pt` 是**列表控件的客户区坐标**，不是屏幕坐标。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LVHITTESTINFO
    {
        public POINT pt;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;

    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize,
        uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    /// <summary>屏幕坐标 → 指定窗口的客户区坐标（LVM_HITTEST 要的是客户区坐标）。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    /// <summary>按类名找子窗口：从 SHELLDLL_DefView 下面捞 SysListView32 用。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string? lpszClass, string? lpszWindow);

    /// <summary>目标进程是不是 WOW64（32 位跑在 64 位系统上）。被 XP 拒绝时返回 false。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWow64Process(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    // ── 「收藏当前文件 / 打开文件所在位置」（v1.5.1）─────────────────────
    //
    // 这一组里只有一件事是硬的：**从目标进程的句柄表里认出"它正在看的那个文件"**。
    //
    // 为什么不用 NtQuerySystemInformation(SystemHandleInformation)：Win11 24H2 起
    // 非管理员调用直接返回 STATUS_ACCESS_DENIED(0xC0000022)，Process Explorer 现在
    // 要求提权就是这条。**单进程快照**（下面这个 ProcessHandleInformation）没有这个限制，
    // 代价是要自己对每个进程开一次句柄。
    //
    // 为什么 Filter 用 ObjectTypeIndex 而不是"试着查名字"：对管道 / 事件 / 同步对象
    // 调 NtQueryObject(ObjectNameInformation) 有概率**挂在里面不返回**（同步对象尤其明显），
    // 靠类型过滤绕开比加超时可靠 —— 见 ForegroundFileProbe 里先探 File 的 index。

    public const int PROCESS_QUERY_INFORMATION = 0x0400;
    public const int PROCESS_DUP_HANDLE = 0x0040;
    public const uint DUPLICATE_SAME_ACCESS = 0x0002;
    public const uint FILE_TYPE_DISK = 0x0001;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    /// <summary>NtQueryInformationProcess 的 ProcessHandleInformation（51）。</summary>
    public const int ProcessHandleInformation = 51;

    /// <summary>NtQueryObject 的 ObjectNameInformation（1）。</summary>
    public const int ObjectNameInformation = 1;

    /// <summary>NtQueryInformationProcess / NtQueryObject 的"缓冲区太小"返回码。</summary>
    public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    /// <summary>
    /// 句柄表里的一项。三处 <c>ULONG_PTR</c> 用 <see cref="IntPtr"/> 表达 ——
    /// x64 下 8 字节、x86 下 4 字节，结构体大小自动是 40 / 28，
    /// 手写定长字段在两条线之间必然有一边错位。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_HANDLE_TABLE_ENTRY_INFO
    {
        public IntPtr HandleValue;
        public IntPtr HandleCount;
        public IntPtr PointerCount;
        public uint GrantedAccess;
        public uint ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    /// <summary>UNICODE_STRING。Buffer 的偏移按位宽自动算（x64 = 8、x86 = 4）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    /// <summary>
    /// 进程句柄快照。<c>NumberOfHandles</c> 之后紧跟一个
    /// <see cref="PROCESS_HANDLE_TABLE_ENTRY_INFO"/> 数组 —— 数组起点是
    /// <c>2 * IntPtr.Size</c>，不要按可见字段硬编码偏移。
    /// </summary>
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryObject(IntPtr handle, int objectInformationClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetFileType(IntPtr handle);

    /// <summary>查询 <c>C:</c> 这类设备名对应的 NT 路径（<c>\Device\HarddiskVolume3</c>）。</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int QueryDosDevice(string deviceName, StringBuilder targetPath, int maxLength);

    [DllImport("kernel32.dll")]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFileAttributes(string fileName);

    /// <summary>枚举子窗口：UWP 的 ApplicationFrameHost 底下才是真正在显示内容的那个窗口。</summary>
    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    /// <summary>把路径解析成 PIDL（资源管理器定位的入口）。返回 S_OK(0) 才算拿到。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    /// <summary>
    /// 打开父文件夹并选中指定项。用它的三个理由：能**复用已打开的同名文件夹窗口**
    /// （不闪新窗）、自带选中、对库文件夹与网络路径的处理是 shell 自己的。
    /// 比 <c>explorer.exe /select,</c> 稳，后者只是路径被删/驱动器未就绪时的退路。
    /// </summary>
    [DllImport("shell32.dll")]
    public static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint itemCount, IntPtr[]? items, uint flags);

    /// <summary>释放 SHParseDisplayName 给出的 PIDL。</summary>
    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    // ── 进程树（v1.5.1 补）：WPS / 浏览器把文档与渲染放在子进程里，
    //     只盯前台窗口那个 PID 会一无所获（实测 WPS：窗口进程 wps.exe 里没有文档句柄，
    //     真正的 xlsx 句柄在子进程 et.exe 里）。

    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    public const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

}
