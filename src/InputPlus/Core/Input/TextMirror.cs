using System.Runtime.InteropServices;
using System.Text;
using InputPlus.Core.Interop;

namespace InputPlus.Core.Input;

/// <summary>
/// 读"当前焦点控件里的最后几个字符"。
///
/// 为什么需要它：别名靠低级键盘钩子累积字符，而钩子只能看到**物理键** ——
/// 中文是输入法组字之后才落进编辑框的，钩子永远看不到「中文」这两个字。
/// 所以中文别名只能从"控件里真正有什么字"这个方向去拿。
///
/// 只在配置里含非 ASCII 触发词时才被调用（见 HotkeyEngine.ObserveSnippet），
/// 而且只问一次、超时 40ms、失败就退化成原来的按键缓冲区 —— 不能因为
/// 某个程序不响应 WM_GETTEXT 就把打字卡住。
/// </summary>
internal static class TextMirror
{
    /// <summary>取焦点控件文本的结尾（最多 <paramref name="maxChars"/> 个字符）。</summary>
    public static bool TryReadTail(int maxChars, out string text)
        => TryReadTail(maxChars, out text, out _);

    /// <summary>
    /// 取结尾，并带回**整段文本的字符数**。
    ///
    /// 为什么需要总数：触发词被输入法吃进组字串时，要让 IME 提交、再算"它吐了几个字"。
    /// 只比 64 字的尾巴算不出差值 —— 控件里超过 64 字时，前后的尾巴长度都是 64，
    /// 差值恒为 0（第一版就是这么写的）。总数来自 WM_GETTEXTLENGTH，同一个消息里就有。
    /// </summary>
    public static bool TryReadTail(int maxChars, out string text, out int totalLength)
    {
        text = "";
        totalLength = 0;
        if (maxChars <= 0) return false;

        try
        {
            IntPtr target = FocusedControl();
            if (target == IntPtr.Zero) return false;

            /* 先问长度：有控件（尤其浏览器自绘）会返回 0，那就没必要再取内容。 */
            if (!TrySend(target, NativeMethods.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, out IntPtr lenPtr))
                return false;

            int len = lenPtr.ToInt32();
            if (len <= 0) return false;
            totalLength = len;

            int take = Math.Min(len, Math.Max(maxChars, 16));
            var buffer = new StringBuilder(take + 1);
            if (!TrySend(target, NativeMethods.WM_GETTEXT, (IntPtr)(take + 1), buffer, out _))
                return false;

            string value = buffer.ToString();
            if (value.Length == 0) return false;

            text = value.Length <= maxChars ? value : value[^maxChars..];
            return true;
        }
        catch
        {
            // 读不到就是读不到：调用方会用按键缓冲区继续，不影响原有功能。
            return false;
        }
    }

    /// <summary>
    /// 读回来的这段尾巴是不是其实只是**窗口标题**。
    ///
    /// Chromium / Electron 系（浏览器正文、微信、新版记事本、各种套壳客户端）里
    /// hwndFocus 指向的是顶层窗口，WM_GETTEXT 拿到的是标题栏文字，而不是输入框里那行字。
    /// 把这个标题当成"控件里的字"，上游就会连着做错判断：
    ///   · 认为触发词不在屏幕上 → 判定"被输入法吃了" → 只回删 1 个字，
    ///     `;ipdv` 原样留在原地（用户 2026-11-23 报的就是这个）；
    ///   · 缓冲为空时把标题当权威文本灌进去 → 之后再也匹配不到任何别名。
    ///
    /// 判据很便宜：和前台窗口标题相等，或者尾巴就是标题的后缀（尾巴最多 64 字，标题更长时是后缀）。
    /// 认错一次的代价只是"这次不回读"，比拿标题当正文小得多。
    /// </summary>
    public static bool LooksLikeWindowTitle(string? tail)
    {
        string t = (tail ?? "").Trim();
        if (t.Length < 3) return false;

        try
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            /* 和 TryReadTail 走同一条带 40ms 超时的 SendMessage：
             * 这个函数在钩子线程上被调用，前台窗口卡死时**绝不能**把我们拖住。 */
            if (!TrySend(fg, NativeMethods.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, out IntPtr lenPtr))
                return false;

            int len = lenPtr.ToInt32();
            if (len <= 0) return false;

            int take = Math.Min(len, 256);
            var sb = new StringBuilder(take + 1);
            if (!TrySend(fg, NativeMethods.WM_GETTEXT, (IntPtr)(take + 1), sb, out _)) return false;

            string title = sb.ToString().Trim();
            if (title.Length < 3) return false;

            if (string.Equals(t, title, StringComparison.OrdinalIgnoreCase)) return true;

            /* 尾巴是标题的结尾：控件里的字被截成 64 字时，正好等于标题的后缀。 */
            return t.Length <= title.Length
                   && title.EndsWith(t, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;      // 探针失败就按"这是正常文本"处理，维持改造前的行为
        }
    }

    /// <summary>前台窗口里真正带插入符的控件（拿不到就退化成前台窗口本身）。</summary>
    private static IntPtr FocusedControl()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return IntPtr.Zero;

        uint threadId = NativeMethods.GetWindowThreadProcessId(fg, out _);
        if (threadId != 0)
        {
            var info = new NativeMethods.GUITHREADINFO { cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            if (NativeMethods.GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != IntPtr.Zero)
                return info.hwndFocus;
        }

        return fg;
    }

    /// <summary>带超时的 SendMessage：卡住的窗口不会被我们拖住。</summary>
    private static bool TrySend(IntPtr hwnd, int msg, IntPtr wParam, object? text, out IntPtr result)
    {
        result = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (text is StringBuilder sb)
            {
                /* 用一块固定大小的缓冲区：StringBuilder 的容量已经算好，
                 * 多留两个字符给结尾的 \0。 */
                int bytes = (sb.Capacity + 1) * 2;
                buffer = Marshal.AllocHGlobal(bytes);
                for (int i = 0; i < bytes; i++) Marshal.WriteByte(buffer, i, 0);

                if (!TrySend(hwnd, msg, wParam, buffer, out _))
                    return false;

                sb.Clear();
                sb.Append(Marshal.PtrToStringUni(buffer) ?? "");
                return true;
            }

            return NativeMethods.SendMessageTimeoutW(hwnd, (uint)msg, wParam, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK, 40, out result) != IntPtr.Zero;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>带超时的 SendMessage（IntPtr 载荷版）。</summary>
    private static bool TrySend(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
        => NativeMethods.SendMessageTimeoutW(hwnd, (uint)msg, wParam, lParam,
            NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK, 40, out result) != IntPtr.Zero;
}
