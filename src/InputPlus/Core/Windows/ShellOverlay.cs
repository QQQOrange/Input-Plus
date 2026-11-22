using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using InputPlus.Core.Input;

namespace InputPlus.Core.Windows;

/// <summary>
/// 判断"前台是不是一层盖在桌面上的 shell 浮层（开始菜单 / 搜索 / 任务视图）"，
/// 以及在这种浮层上补充手势所必需的两个小动作：收起浮层、强制重画轨迹窗。
///
/// 为什么需要它：Windows 11 的开始菜单是 <c>StartMenuExperienceHost</c> / <c>SearchHost</c>
/// 的 XAML 岛窗口，它跑在 Z 序带 6（见 <see cref="OsdBandSurface"/> 的注释），
/// 而我们的轨迹窗是普通 topmost WPF 窗口（带 0/1）——窗口本身在，指针事件也照样被
/// 我们的鼠标钩子收到，但**画出来的轨迹被开始菜单整块盖住**，
/// 用户看到的现象就是"在开始菜单里划不出手势"。
///
/// 抢 Z 序这条路上有两条，都走不通：
///   · <c>CreateWindowInBand(band 16)</c> 要 uiAccess 签名，自检里一直是 Win32 错误 5；
///   · 就算抢到了也不该抢 —— 轨迹画在开始菜单上面会挡住它的文字。
///
/// 所以换个思路：指针一移动就先把这层浮层收起来，轨迹立刻可见，
/// 手势的作用目标也能落回下面真正想操作的那个窗口。
/// 纯点击不移动时不收：右键菜单之类还能照常补发。
/// </summary>
internal static class ShellOverlay
{
    /// <summary>会盖满桌面的 shell 浮层进程。ShellExperienceHost 留着不会误判（它不常驻前台）。</summary>
    private static readonly string[] OverlayProcesses =
    {
        "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost", "LockApp",
    };

    /// <summary>
    /// 这些类名的窗口一定是 shell 浮层，不必再看进程名。
    ///
    /// 本机实测（Win11 24H2）：开始菜单的前台窗口类是
    /// <c>Windows.UI.Core.CoreWindow</c>（进程 <c>SearchHost</c>，键盘上按 Win 出来的就是它）。
    /// 不同版本上任务视图/搜索框还会用 <c>XamlExplorerHostIslandWindow</c>。
    /// </summary>
    private static readonly string[] OverlayClasses =
    {
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
    };

    /// <summary>当前前台是不是一层 shell 浮层。</summary>
    public static bool IsUp() => IsUp(WindowService.Foreground);

    /// <summary>
    /// 指定句柄是不是一层 shell 浮层：类名或进程名命中白名单之一，且窗口可见。
    ///
    /// 可见性必须查 —— 开始菜单收起后句柄还在，只是看不见了；
    /// 只看进程名会把"刚收起来的开始菜单"继续当浮层，于是每次手势都多发一次 Esc。
    /// </summary>
    public static bool IsUp(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;

        IntPtr root = Root(hwnd);
        if (root == IntPtr.Zero) return false;

        string cls = ClassOf(root);
        foreach (string name in OverlayClasses)
            if (cls.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;

        string exe = ExeOf(root);
        foreach (string name in OverlayProcesses)
            if (exe.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>给自检与日志用的一句话描述："SearchHost.exe / Windows.UI.Core.CoreWindow"。</summary>
    public static string Describe(IntPtr hwnd)
    {
        IntPtr root = Root(hwnd);
        string exe = ExeOf(root);
        string cls = ClassOf(root);
        return (exe.Length > 0 ? exe + ".exe" : "?") + " / " + (cls.Length > 0 ? cls : "?");
    }

    /// <summary>
    /// 收起前台这层 shell 浮层（开始菜单 / 搜索 / 任务视图），异步、不等结果。
    ///
    /// 分三步走，前一步失败才走下一步，因为三招的"体面程度"依次下降：
    ///   1. <c>PostMessage(WM_CLOSE)</c> —— 开始菜单认这个，收得干净还带动画；
    ///   2. 250ms 后还在，给前台发 Esc（开始菜单/搜索的通用退出口）；
    ///   3. 再 250ms 还在，用 <see cref="InputSender.Tap"/> 真敲一次 Esc
    ///      （走 SendInput，某些 XAML 岛不认 PostMessage 的按键）。
    ///
    /// 每步之前都要重新查一遍 <see cref="IsUp"/>：用户在动画里已经划走、或者
    /// 前台自己换了，就立刻停手 —— 绝不能在别的窗口上补发 Esc。
    /// 返回 true 表示"确实对一层浮层动了手"。
    /// </summary>
    public static bool Dismiss()
    {
        IntPtr fg = WindowService.Foreground;
        if (!IsUp(fg)) return false;

        IntPtr root = Root(fg);
        if (root == IntPtr.Zero) return false;

        PostMessageW(root, WmClose, IntPtr.Zero, IntPtr.Zero);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (!IsUp()) return;
                IntPtr target = Root(WindowService.Foreground);
                if (target == IntPtr.Zero) return;
                PostMessageW(target, WmKeyDown, new IntPtr(VkEscape), new IntPtr(1));
                PostMessageW(target, WmKeyUp, new IntPtr(VkEscape), unchecked((IntPtr)(long)0xC0000001));

                await Task.Delay(250).ConfigureAwait(false);
                if (!IsUp()) return;
                InputSender.Tap(VkEscape);
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "收起 shell 浮层"); }
        });

        return true;
    }

    /// <summary>
    /// 把轨迹窗重新压到最上层（不激活、不改尺寸、不抢焦点）。
    ///
    /// WPF 的顶层窗口（带 1~4）永远盖不住开始菜单（带 6），但"收起开始菜单之后"
    /// 光靠打开时那一次 SetWindowPos 有时还不够：切换前台会让别的窗口重新置顶一次，
    /// 刚画的轨迹于是又沉下去。收起浮层之后显式再抬一次最省事。
    /// </summary>
    public static void Raise(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
    }

    // ───────────────── 小工具 ─────────────────

    private const int GwlStyle = -16;
    private const int WsChild = 0x40000000;
    private const uint WmClose = 0x0010;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkEscape = 0x1B;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    /// <summary>跳到最外层不带 WS_CHILD 的祖先 —— 开始菜单的 XAML 岛自己就是子窗口。</summary>
    private static IntPtr Root(IntPtr hwnd)
    {
        IntPtr cur = hwnd;
        for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
        {
            if ((GetWindowLongW(cur, GwlStyle) & WsChild) == 0) return cur;
            IntPtr parent = GetParent(cur);
            if (parent == IntPtr.Zero) return cur;
            cur = parent;
        }
        return cur;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        int n = GetClassNameW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : "";
    }

    /// <summary>进程名（不含 .exe）。拿不到就返回空串 —— 判定不能抛异常。</summary>
    private static string ExeOf(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);
}
