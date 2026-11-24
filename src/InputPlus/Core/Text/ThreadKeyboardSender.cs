using System.Runtime.InteropServices;

using static InputPlus.Core.Interop.NativeMethods;
namespace InputPlus.Core.Text;

/// <summary>
/// **线程级**键盘注入。
///
/// 存在的理由：有些程序（Electron / 部分国产客户端）会过滤
/// <c>SendInput</c> 造出来的合成按键 —— 它们在系统里带 LLKHF_INJECTED
/// 标记，容易被当成"非真实输入"而忽略。
///
/// 线程级注入走的是另一条路：把自己的输入队列 **attach** 到目标线程，
/// 让目标程序"以为"按键就是它自己队列里的。配合
/// <see cref="SetKeyboardState"/> 把 Ctrl 的真实状态写进去，
/// 目标程序用 GetKeyState 判断修饰键时才能读到正确结果。
///
/// 注意：这是**尽力而为**的兜底通道，失败就失败，不影响其它路径。
/// </summary>
public static class ThreadKeyboardSender
{

    [DllImport("user32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_CONTROL = 0x11;
    private const int VK_C = 0x43;
    private const int VK_INSERT = 0x2D;
    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>
    /// 用线程级注入发一次 Ctrl+&lt;vk&gt;。
    ///
    /// 步骤（参考 QuickSearch 的 <c>post_ctrl_c_to_focus</c>）：
    ///   1. attach 到前台线程的输入队列
    ///   2. 把 Ctrl 置为按下（**只在用户本来没按着的时候**）
    ///   3. PostMessage 发 WM_KEYDOWN / WM_KEYUP
    ///   4. 只撤我们自己置的那一位，然后 detach
    ///
    /// ★ 2026-11-24 修三个缺陷（用户交接单，全部是"卡键"级）：
    ///
    /// ① 原来还原时 `SetKeyboardState(saved)` 把**整块 256 字节快照**写回去。
    ///    saved 是 attach 之后、`Thread.Sleep(10)` **之前**取的；这 10ms 里用户的真实
    ///    按键会进共享队列，然后被旧快照整块盖掉。最短触发路径：
    ///    按住 Ctrl 拖选 → 松开 Ctrl → 立刻取词 —— 目标线程还没处理完 keyup，
    ///    saved[VK_CONTROL] 还是 0x80，第 99 行原样写回去，
    ///    **Ctrl 就永久卡在那个窗口的队列里**。
    ///    现在只动 VK_CONTROL 这一位，且只在"是我们置的"时候动。
    ///
    /// ② 原来在写 Ctrl 位之前不看用户本来是否按着 —— 等于接管了他的真实状态，
    ///    而他松手的那一刻我们并不知道。现在 `(saved[VK_CONTROL] & 0x80) == 0` 才写。
    ///
    /// ③ 原来所有返回值都不检查、也没有 finally：还原失败静默跳过；
    ///    detach 失败会让**两个线程的输入队列永久黏连**（比卡键更糟）。现在两条都记日志。
    /// </summary>
    public static bool SendCtrlChord(int vk)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        uint fgTid = GetWindowThreadProcessId(fg, out _);
        if (fgTid == 0) return false;

        uint myTid = GetCurrentThreadId();
        if (fgTid == myTid) return false;

        bool attached = false;
        bool injectedCtrl = false;

        try
        {
            attached = AttachThreadInput(myTid, fgTid, true);

            if (attached)
            {
                var saved = new byte[256];
                /* 用户本来就按着 Ctrl 时别动它 —— 改了就得由我们替他决定何时松开，
                 * 而他松手的那一刻我们并不知道。 */
                if (GetKeyboardState(saved) && (saved[VK_CONTROL] & 0x80) == 0)
                {
                    var down = (byte[])saved.Clone();
                    down[VK_CONTROL] = 0x80;
                    injectedCtrl = SetKeyboardState(down);
                }
            }

            uint scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
            var dnLParam = new IntPtr((int)(1 | (scan << 16)));
            var upLParam = new IntPtr((int)(1 | (scan << 16) | 0xC0000000u));

            bool any = PostMessage(fg, WM_KEYDOWN, new IntPtr(vk), dnLParam);
            System.Threading.Thread.Sleep(10);
            PostMessage(fg, WM_KEYUP, new IntPtr(vk), upLParam);
            return any;
        }
        finally
        {
            /* 只撤我们自己置的那一位，不拿整块旧快照覆盖 ——
             * 否则这 10ms 里用户的真实按键会被写回旧值，包括他刚松开的 Ctrl。 */
            if (injectedCtrl)
            {
                var now = new byte[256];
                if (GetKeyboardState(now))
                {
                    now[VK_CONTROL] = 0x00;
                    if (!SetKeyboardState(now))
                        CoreLog.Warn("[线程注入] 还原 Ctrl 位失败，目标线程可能残留按下状态");
                }
            }

            if (attached && !AttachThreadInput(myTid, fgTid, false))
                CoreLog.Warn("[线程注入] detach 失败，输入队列可能黏连");
        }
    }
}
