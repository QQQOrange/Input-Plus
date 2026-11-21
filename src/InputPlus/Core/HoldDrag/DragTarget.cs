using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace InputPlus.Core;

/// <summary>
/// `--drag-target <句柄文件>`：给 `--drag-e2e` 当靶子的一次性窗口进程。
///
/// 为什么要专门做一个靶子进程：e2e 需要一个"别的程序的、不提权的、能压到最前"的普通窗口。
/// 实测这台机器上现成的候选都不合格 ——
///   · regedit 带 autoElevate：UIPI 会让 SetWindowPos/SetForegroundWindow/MoveWindow 全部静默失败，
///     于是靶子永远被人盖着，窗口"不动"根本不是功能问题；
///   · Win11 的记事本是商店应用：启动器进程立刻退出，主窗口落在别的进程上，拿不到句柄；
///   · cmd 多跑在 Windows Terminal 里：窗口同样属于别的进程。
/// 自己开一个 WS_OVERLAPPEDWINDOW 最干净：有标题栏（能被按住拖拽接管）、不提权、可控置顶。
///
/// 协议：创建好窗口后把句柄写进句柄文件；父进程删掉 "<句柄文件>.stop" 之外不用管我们，
/// 我们最多活 120 秒，或看到 stop 文件就退。
/// </summary>
internal static class DragTarget
{
    private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int WS_VISIBLE = 0x10000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly nint HWND_TOPMOST = new IntPtr(-1);

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_PAINT = 0x000F;

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, int style,
                                               int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(nint hWnd, nint rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    // 回调必须被字段/局部变量强引用住，否则 GC 一收，窗口过程就指向野指针。
    private static WndProc? _proc;

    /* 靶子自己的"点击到达"记录。
     *
     * 排查「钩子装上了却收不到鼠标」时，必须先分清两种情况：
     *   a) 点击根本没到窗口 —— 有别的钩子把它吞了（我们排在它后面，自然也收不到）；
     *   b) 点击到了窗口，只是我们的钩子没收到 —— 我们的钩子不在链上。
     * 窗口过程是最终裁判：它收到了，就说明点击确实被投递到了这个窗口。 */
    private static string? _mouseLog;

    /// <summary>返回退出码：0 = 正常跑完（被 stop 或超时）。</summary>
    public static int Run(string? hwndFile)
    {
        if (string.IsNullOrEmpty(hwndFile))
        {
            return 2;
        }

        _proc = WindowProc;
        _mouseLog = hwndFile + ".mouse";
        nint inst = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEXW)),
            lpfnWndProc = _proc,
            hInstance = inst,
            lpszClassName = "InputPlusHoldDragTarget",
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            return 3;
        }

        nint hwnd = CreateWindowExW(0u, wc.lpszClassName, "Input Plus 拖拽靶子", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                                    260, 180, 640, 420, 0, 0, inst, 0);
        if (hwnd == 0)
        {
            return 4;
        }

        SetWindowPos(hwnd, HWND_TOPMOST, 260, 180, 640, 420, SWP_SHOWWINDOW);
        ShowWindow(hwnd, 5);
        SetForegroundWindow(hwnd);

        try
        {
            File.WriteAllText(hwndFile, "0x" + hwnd.ToInt64().ToString("X"), new UTF8Encoding(false));
        }
        catch
        {
            return 5;
        }

        string stopFile = hwndFile + ".stop";
        long deadline = Environment.TickCount64 + 120000;
        while (Environment.TickCount64 < deadline)
        {
            if (File.Exists(stopFile))
            {
                break;
            }
            // 一边抽消息一边等：窗口过程必须活着，否则窗口变"无响应"，SetWindowPos 之类会超时。
            MSG msg;
            while (PeekMessageW(out msg, 0, 0, 0, 1u))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(10);
        }

        DestroyWindow(hwnd);
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG msg, nint hWnd, uint min, uint max, uint remove);

    private static nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        LogMouse(msg);
        switch (msg)
        {
            case WM_PAINT:
                ValidateRect(hWnd, 0);
                return 0;
            case WM_CLOSE:
                DestroyWindow(hWnd);
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>把收到的鼠标按键消息追加到 "&lt;句柄文件&gt;.mouse"（只在 201/202/204/205/522 上写）。</summary>
    private static void LogMouse(uint msg)
    {
        if (msg is not (0x0201u or 0x0202u or 0x0204u or 0x0205u or 0x020Au))
        {
            return;
        }
        string? path = _mouseLog;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            File.AppendAllText(path,
                DateTime.Now.ToString("HH:mm:ss.fff") + " msg=0x" + msg.ToString("X4") + "\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // 记录失败不影响靶子工作。
        }
    }
}
