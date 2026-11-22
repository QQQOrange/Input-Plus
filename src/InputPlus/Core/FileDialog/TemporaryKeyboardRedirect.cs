using System.Runtime.InteropServices;
using System.Text;
using InputPlus.Core;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// 第四级兜底：临时低级键盘钩子。
///
/// 只在"浮层拿不到前台"时安装，装完立刻开始计时：15 秒没有输入、或调用方
/// 主动 Dispose、或输入了 Enter/Esc，都立即卸载。红线要求绝不能常驻。
/// 这个钩子只看按键，不记录、不落盘、不外发。
/// </summary>
internal sealed class TemporaryKeyboardRedirect : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, HookProc proc, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buffer,
        int count, uint flags, nint layout);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    private readonly HookProc _proc;   // 必须保活：委托被回收后系统回调会崩
    private nint _hook;
    private long _lastInputTicks;
    private System.Threading.Timer? _idle;
    private int _disposed;

    public event Action<char>? Char;
    public event Action? Backspace;
    public event Action? Enter;
    public event Action? Escape;
    public event Action<int>? OtherKey;

    public static TemporaryKeyboardRedirect? Install()
    {
        try
        {
            var redirect = new TemporaryKeyboardRedirect();
            nint hook = SetWindowsHookExW(WH_KEYBOARD_LL, redirect._proc,
                GetModuleHandleW(null), 0);
            if (hook == 0)
            {
                CoreLog.Warn("安装临时键盘钩子失败，Win32 错误 " + Marshal.GetLastWin32Error());
                return null;
            }
            redirect._hook = hook;
            redirect._lastInputTicks = Environment.TickCount64;
            redirect._idle = new System.Threading.Timer(_ => redirect.CheckIdle(), null, 5000, 5000);
            CoreLog.Info("浮层取不到前台，临时安装键盘重定向钩子（15 秒无输入自动卸载）");
            return redirect;
        }
        catch (Exception ex)
        {
            CoreLog.Error("安装临时键盘钩子异常", ex);
            return null;
        }
    }

    private TemporaryKeyboardRedirect()
    {
        _proc = Callback;
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);

        try
        {
            int msg = (int)wParam;
            if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN)
                return CallNextHookEx(_hook, code, wParam, lParam);

            if (IsDown(VK_CONTROL) || IsDown(VK_MENU)) /* Ctrl/Alt 组合放行，别吞用户的快捷键 */
                return CallNextHookEx(_hook, code, wParam, lParam);

            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            _lastInputTicks = Environment.TickCount64;

            switch (info.vkCode)
            {
                case 0x08: Backspace?.Invoke(); return 1;
                case 0x0D: Enter?.Invoke(); return 1;
                case 0x1B: Escape?.Invoke(); return 1;
                case 0x25: OtherKey?.Invoke(0x25); return 1;
                case 0x26: OtherKey?.Invoke(0x26); return 1;
                case 0x28: OtherKey?.Invoke(0x28); return 1;
                case 0x27: OtherKey?.Invoke(0x27); return 1;
            }

            string text = ToText(info);
            if (text.Length > 0)
            {
                foreach (char c in text) Char?.Invoke(c);
                return 1;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "键盘重定向回调");
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static string ToText(KBDLLHOOKSTRUCT info)
    {
        try
        {
            var state = new byte[256];
            if (!GetKeyboardState(state)) return "";
            var buffer = new StringBuilder(8);
            int n = ToUnicodeEx(info.vkCode, info.scanCode, state, buffer, buffer.Capacity, 0,
                GetKeyboardLayout(0));
            return n > 0 ? buffer.ToString(0, n) : "";
        }
        catch { return ""; }
    }

    private static bool IsDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vk);

    private void CheckIdle()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Environment.TickCount64 - _lastInputTicks < 15000) return;
        CoreLog.Info("键盘重定向钩子 15 秒无输入，自动卸载");
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _idle?.Dispose();
            _idle = null;
            if (_hook != 0)
            {
                UnhookWindowsHookEx(_hook);
                _hook = 0;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "卸载键盘重定向钩子");
        }
    }
}
