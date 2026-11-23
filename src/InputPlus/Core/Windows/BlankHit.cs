using System.Runtime.InteropServices;
using System.Windows.Automation;
using InputPlus.Core.Interop;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Windows;

/// <summary>指针底下的壳窗口属于哪一类。判定顺序决定了哪些地方**永远不弹**中键菜单。</summary>
internal enum HitKind
{
    /// <summary>别的程序（浏览器、编辑器、终端、对话框…）—— 一律不接管。</summary>
    Other = 0,

    /// <summary>桌面（Progman / WorkerW / SHELLDLL_DefView 及其 SysListView32）。</summary>
    Desktop = 1,

    /// <summary>资源管理器文件夹窗口里的图标列表（CabinetWClass 里的 DirectUIHWND / SysListView32）。</summary>
    ExplorerItems = 2,
}

/// <summary>命中结论。三个值都必须存在：<see cref="Unknown"/> 表示"问不出来"，调用方按非空白处理。</summary>
internal enum BlankVerdict
{
    /// <summary>没问出来（目标进程卡住 / UIA 超时 / 跨位数）。**不要**当成空白。</summary>
    Unknown = 0,

    /// <summary>确认是空白：图标之间的空地。</summary>
    Blank = 1,

    /// <summary>确认不是空白：图标、文件、任何别的元素。</summary>
    Occupied = 2,
}

/// <summary>一次命中判定的结果。带上 <see cref="Why"/> 是为了日志里能说清"为什么没弹"。</summary>
internal readonly struct BlankProbe
{
    public BlankProbe(BlankVerdict verdict, HitKind kind, IntPtr hwnd, string why)
    {
        Verdict = verdict;
        Kind = kind;
        Hwnd = hwnd;
        Why = why;
    }

    public BlankVerdict Verdict { get; }

    public HitKind Kind { get; }

    public IntPtr Hwnd { get; }

    /// <summary>一句话原因（写日志用，不进界面）。</summary>
    public string Why { get; }

    public bool IsBlank => Verdict == BlankVerdict.Blank;

    public static BlankProbe NotBlank(string why, HitKind kind = HitKind.Other, IntPtr hwnd = default)
        => new(BlankVerdict.Occupied, kind, hwnd, why);
}

/// <summary>
/// 中键菜单的"空白命中"判定：指针底下到底是桌面/资源管理器里的空地，还是一个图标/文件。
///
/// ── 为什么判定必须发生在**按下**那一刻 ─────────────────────────────
/// 抬起时才判就得在钩子回调里等 UIA，用户会觉得"点了半天菜单才出来"。
/// 现在按下时算一次（那时用户还按着，几十毫秒他感觉不到），抬起时只读结论 ——
/// **抬起这条路径是零等待的**，菜单立刻出来。
///
/// ── 两条判定通道，各自用最便宜的办法 ──────────────────────────────
/// · 桌面：图标视图是一个真的 SysListView32，问它 LVM_HITTEST 最准也最快
///   （win32 往返，正常 < 1ms）。跨进程发指针消息要远程分配内存，见 NativeMethods 那段注释。
/// · 资源管理器：Win10/11 的图标是 DirectUI 画的，没有列表控件可问，只能走 UI Automation：
///   ElementFromPoint 拿到元素，再往上找两层看是不是列表项。
///   UIA 是跨进程 COM 调用，目标卡住就会一直阻塞，所以它跑在一条**常驻 STA 线程**上，
///   调用方带硬超时；超时过的窗口进 30 秒冷却，这期间直接判"非空白"（宁可少弹，不要卡顿）。
///
/// ── 三条"宁可不弹"的红线 ─────────────────────────────────────────
/// ① 跨位数：32 位的我们对 64 位 Explorer 发 LVM_HITTEST，指针在对方进程里无效 —— 判 Unknown。
/// ② 目标卡死 / UIA 超时 —— 判 Unknown。
/// ③ 拿不到进程句柄（提权窗口、受保护进程）—— 判 Unknown，且不写日志刷屏。
/// 调用方把 Unknown 一律当"非空白"处理：最坏结果是这次中键没弹菜单，而不是乱弹。
/// </summary>
internal static class BlankHit
{
    /// <summary>桌面 LVM_HITTEST 的预算。真机上正常 0.1~0.5ms，25ms 已经只会在 Explorer 卡死时才到。</summary>
    internal const int DesktopBudgetMs = 25;

    /// <summary>资源管理器 UIA 的预算。首次调用含 COM 初始化，之后 5~30ms。</summary>
    internal const int ExplorerBudgetMs = 90;

    /// <summary>某个窗口 UIA 超时后，这么久之内不再试它（毫秒）。</summary>
    private const int SlowWindowCooldownMs = 30_000;

    /// <summary>WIN7 线的资源管理器类名（老式「我的电脑」窗口）。</summary>
    private const string ExplorerClassLegacy = "ExploreWClass";

    /// <summary>Win10/11 的资源管理器文件夹窗口。</summary>
    private const string ExplorerClass = "CabinetWClass";

    private const string DirectUiClass = "DirectUIHWND";
    private const string ListViewClass = "SysListView32";
    private const string DefViewClass = "SHELLDLL_DefView";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, long> SlowWindows = new();

    private static readonly UiaWorker Uia = new();

    /// <summary>
    /// 判断指针位置是不是"可以弹中键菜单的空白"。纯 read-only，不会改变任何窗口状态。
    /// </summary>
    /// <param name="pt">屏幕坐标（物理像素，和 GetCursorPos 同一个坐标系）。</param>
    /// <param name="scope">设置里的生效范围；仅桌面时不碰资源管理器。</param>
    internal static BlankProbe AtPoint(NativeMethods.POINT pt, MiddleClickMenuScope scope)
    {
        IntPtr hwnd;
        try { hwnd = NativeMethods.WindowFromPoint(pt); }
        catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单：取指针窗口"); return BlankProbe.NotBlank("WindowFromPoint 失败"); }
        if (hwnd == IntPtr.Zero) return BlankProbe.NotBlank("指针下没有窗口");

        string cls = WindowService.ClassNameOf(hwnd) ?? "";
        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        string rootCls = root == IntPtr.Zero ? "" : (WindowService.ClassNameOf(root) ?? "");

        /* 桌面的判定用**句柄**兜底一次：GetShellWindow() 返回的就是桌面宿主窗口
         * （Progman）。动态壁纸、多显示器、Win11 的 WorkerW 换宿主时类名会变，
         * 但"root 是 shell 窗口"这一条始终成立。 */
        bool rootIsShell = root != IntPtr.Zero && root == NativeMethods.GetShellWindow();

        /* 第三方桌面 / Launcher：它们的窗口不属于 Progman/WorkerW，但整块屏都是"桌面"。
         * 这里只认**由 explorer.exe 撑起来的顶层窗口**（shell 宿主、部分 launcher 就是这么挂的），
         * 不去猜别的进程 —— 猜错就等于在全屏游戏里乱弹菜单。 */
        bool rootIsExplorer = false;
        if (!rootIsShell && root != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(root, out uint rootPid);
            if (rootPid != 0)
            {
                string exe = WindowService.GetProcessPath(rootPid);
                rootIsExplorer = exe.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase);
            }
        }
        if (IsDesktopClass(cls, rootCls) || rootIsShell || rootIsExplorer)
        {
            /* 指针在桌面上：可能是图标列表，也可能只是桌面背景（壁纸/多显示器拼缝）。
             * 列表控件问得到就精确问，问不到就当空白 —— 桌面上没有别的东西会误伤。 */
            IntPtr list = cls == ListViewClass ? hwnd : NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, ListViewClass, null);
            if (list == IntPtr.Zero && root != IntPtr.Zero && root != hwnd)
                list = NativeMethods.FindWindowEx(root, IntPtr.Zero, ListViewClass, null);
            if (list == IntPtr.Zero) return new BlankProbe(BlankVerdict.Blank, HitKind.Desktop, hwnd, "桌面背景");

            var verdict = HitTestListView(list, pt, DesktopBudgetMs, out string why);
            return new BlankProbe(verdict, HitKind.Desktop, list, why);
        }

        if (IsExplorerItemsClass(cls, rootCls))
        {
            if (scope == MiddleClickMenuScope.Desktop)
                return BlankProbe.NotBlank("范围仅桌面", HitKind.ExplorerItems, hwnd);

            /* 资源管理器也分两种：控制面板/回收站这类还是真 SysListView32，能走快路；
             * Win10/11 的文件列表是 DirectUI，只能问 UIA。 */
            if (cls == ListViewClass)
            {
                var verdict = HitTestListView(hwnd, pt, DesktopBudgetMs, out string why);
                return new BlankProbe(verdict, HitKind.ExplorerItems, hwnd, why);
            }

            var uia = ProbeExplorerWithUia(hwnd, pt);
            return new BlankProbe(uia, HitKind.ExplorerItems, hwnd, uia == BlankVerdict.Blank ? "UIA：空白" : "UIA：命中元素/超时");
        }

        return BlankProbe.NotBlank($"不是桌面也不是资源管理器列表（类名 {cls} / root={rootCls}）");
    }

    /// <summary>
    /// 指针此刻是不是在**系统桌面**上（Progman / WorkerW / shell 宿主，含桌面图标列表）。
    ///
    /// 和 <see cref="AtPoint"/> 的区别：这里只做类名与 shell 窗口判断，
    /// **不问**图标列表（不发 LVM_HITTEST）、**不碰** UIA，全程只是几次窗口查询。
    /// 它是钩子回调里用的"快速门"：中键只在桌面上才归本程序管（见 GestureService.OnButton），
    /// 别处的每一次中键都要在 1ms 级别放行，不能为了判个命中把浏览器卡一下。
    ///
    /// 严格排除资源管理器文件夹窗口：那里的 SysListView32 也叫这个类名，
    /// 用 <see cref="IsDesktopClass"/> 会把"文件夹里的空白"当成桌面，
    /// 于是中键在资源管理器里被吞掉 —— 正是用户说的"其他页面别动我的中键"。
    /// </summary>
    internal static bool DesktopShellAtPoint(NativeMethods.POINT pt)
    {
        try
        {
            IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;

            string cls = WindowService.ClassNameOf(hwnd) ?? "";
            IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            string rootCls = root == IntPtr.Zero ? "" : (WindowService.ClassNameOf(root) ?? "");

            /* 桌面的三种形态：宿主窗口本身就是 Progman/WorkerW；
             * 指针落在 SHELLDLL_DefView 或它的 SysListView32 上（root 才是 Progman）；
             * 以及 GetShellWindow() 兜底（换宿主、多显示器拼接时类名会变）。 */
            return IsDesktopShellClasses(cls, rootCls, root != IntPtr.Zero && root == NativeMethods.GetShellWindow());
        }
        catch (Exception ex)
        {
            /* 判不出来就当"不在桌面"：宁可这次中键走原生行为，也不要乱吞。 */
            CoreLog.Swallow(ex, "中键菜单：桌面快速判定");
            return false;
        }
    }

    /// <summary>
    /// <see cref="DesktopShellAtPoint"/> 的判定本体（纯函数，自检可以直接喂类名）。
    ///
    /// 注意这里是**白名单**，不是 <see cref="IsDesktopClass"/> 那种"含 SysListView32"的宽判据：
    /// 资源管理器文件夹窗口里也有 SysListView32，按宽判据会把"文件夹里的空白"当桌面，
    /// 于是中键在资源管理器里被吞掉。
    /// </summary>
    internal static bool IsDesktopShellClasses(string cls, string rootCls, bool rootIsShell)
    {
        if (rootIsShell) return true;
        if (cls is "Progman" or "WorkerW" or DefViewClass) return true;
        return rootCls is "Progman" or "WorkerW";
    }

    /// <summary>
    /// 是不是"桌面"这一族窗口。抽成纯函数是为了自检能直接喂类名，不用真开桌面。
    /// </summary>
    internal static bool IsDesktopClass(string cls, string rootCls)
    {
        if (cls is "Progman" or "WorkerW" or DefViewClass or ListViewClass) return true;
        return rootCls is "Progman" or "WorkerW";
    }

    /// <summary>是不是资源管理器文件夹窗口里的图标列表。</summary>
    internal static bool IsExplorerItemsClass(string cls, string rootCls)
    {
        if (rootCls != ExplorerClass && rootCls != ExplorerClassLegacy) return false;
        // DUIViewWndClassName 是 Win10 中间那层；老版本直接把 DirectUIHWND 挂在窗口下。
        return cls is DirectUiClass or ListViewClass or "DUIViewWndClassName" or "CtrlNotifySink";
    }

    /// <summary>LVM_HITTEST 的返回值 → 结论。抽出来是为了自检能直接验 -1 这一个分支。</summary>
    internal static BlankVerdict VerdictOfItemIndex(long itemIndex) =>
        itemIndex < 0 ? BlankVerdict.Blank : BlankVerdict.Occupied;

    /// <summary>
    /// 直接对某个列表控件做一次命中测试（自检用）。
    ///
    /// 为什么单独开这个口子：<see cref="AtPoint"/> 要先过 WindowFromPoint，
    /// 而自检跑起来的会话里桌面常常被别的窗口（本程序自己的界面、远程桌面）盖住 ——
    /// 那时 AtPoint 连桌面都摸不到，跨进程那段代码一次都不会执行，"PASS"是假的。
    /// 这里绕过窗口查找，直接喂一个真实的 SysListView32 句柄，验的是真本事：
    /// OpenProcess → VirtualAllocEx → WriteProcessMemory → SendMessageTimeout → 返回值。
    /// </summary>
    internal static BlankVerdict ProbeListViewDirect(IntPtr list, NativeMethods.POINT screenPt, out string why)
        => HitTestListView(list, screenPt, DesktopBudgetMs, out why);

    // ── 通道一：桌面 / 老式列表控件 ────────────────────────────────────

    /// <summary>
    /// 对一个 SysListView32 做 LVM_HITTEST。跨进程指针消息，全部带预算与位数门。
    /// </summary>
    private static BlankVerdict HitTestListView(IntPtr list, NativeMethods.POINT screenPt, int budgetMs, out string why)
    {
        why = "";
        NativeMethods.GetWindowThreadProcessId(list, out uint pid);
        if (pid == 0) { why = "拿不到进程号"; return BlankVerdict.Unknown; }

        IntPtr proc = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE
            | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false, pid);
        if (proc == IntPtr.Zero) { why = "OpenProcess 被拒（提权/受保护进程）"; return BlankVerdict.Unknown; }

        try
        {
            if (!SameBitnessAsTarget(proc, out string bitnessWhy))
            {
                /* 位数不一致时这条消息发过去对方会解引用到垃圾地址 —— 绝对不能试。
                 * x86 版（Win7 专线）在 64 位桌面上就落在这条分支里，那是**预期**的：
                 * 那一版没有中键菜单，比 Explorer 崩掉好。 */
                why = bitnessWhy;
                return BlankVerdict.Unknown;
            }

            int size = Marshal.SizeOf<NativeMethods.LVHITTESTINFO>();
            IntPtr remote = NativeMethods.VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)size,
                NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
            if (remote == IntPtr.Zero) { why = "VirtualAllocEx 失败"; return BlankVerdict.Unknown; }

            try
            {
                var info = new NativeMethods.LVHITTESTINFO
                {
                    /* LVM_HITTEST 要的是**列表控件的客户区坐标**，GetCursorPos 给的是屏幕坐标。
                     * 少了这一次 ScreenToClient，命中结果永远是 -1（全屏都是"空白"）。 */
                    pt = screenPt,
                    iItem = -1,
                };
                if (!NativeMethods.ScreenToClient(list, ref info.pt)) { why = "ScreenToClient 失败"; return BlankVerdict.Unknown; }

                if (!WriteStruct(proc, remote, info, size)) { why = "WriteProcessMemory 失败"; return BlankVerdict.Unknown; }

                IntPtr ok = NativeMethods.SendMessageTimeoutW(list, NativeMethods.LVM_HITTEST, IntPtr.Zero, remote,
                    NativeMethods.SMTO_ABORTIFHUNG, (uint)Math.Max(1, budgetMs), out IntPtr result);
                if (ok == IntPtr.Zero) { why = "LVM_HITTEST 超时/失败"; return BlankVerdict.Unknown; }

                /* 返回值就是 iItem：-1 = 没命中任何图标。 */
                long index = result.ToInt64();
                if (index < 0)
                {
                    why = "LVM_HITTEST: -1";
                    return BlankVerdict.Blank;
                }

                /* 复核一次：真机上出现过 LVM_HITTEST 报 0（"压在第一个图标上"）而光标明明在空白处
                 * 的情况 —— 结果是中键菜单怎么点都不出来（诊断日志里全是
                 * "kind=Desktop blank=False why=LVM_HITTEST: 0"）。
                 * 这里再问这个图标的包围盒，点不在盒子里就按空白处理：
                 * 宁可多弹一次菜单，也不要"点了没反应"。
                 * 查询本身失败时（分配/超时/读回失败）保持原来的判定，不误伤真实命中。 */
                if (PointInsideItem(proc, list, index, info.pt, out string rectWhy))
                {
                    why = "LVM_HITTEST: " + index + "（点在图内）" + rectWhy;
                    return BlankVerdict.Occupied;
                }

                why = "LVM_HITTEST: " + index + "（点不在该图标矩形内 → 按空白）" + rectWhy;
                return BlankVerdict.Blank;
            }
            finally
            {
                NativeMethods.VirtualFreeEx(proc, remote, IntPtr.Zero, NativeMethods.MEM_RELEASE);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "中键菜单：LVM_HITTEST");
            why = "异常";
            return BlankVerdict.Unknown;
        }
        finally
        {
            NativeMethods.CloseHandle(proc);
        }
    }

    /// <summary>
    /// 这个点（列表控件的客户区坐标）是不是真的落在这个图标的包围盒里。
    ///
    /// 返回 false 表示"不在"，调用方会把这次命中降级成空白；
    /// 查询本身失败时**返回 true**（保持原来的"命中"判定）—— 宁可少弹一次，
    /// 也不要因为一次分配/超时把压在图标上的点击当成空白，破坏桌面图标的中键语义。
    /// </summary>
    private static bool PointInsideItem(IntPtr proc, IntPtr list, long index,
        NativeMethods.POINT clientPt, out string why)
    {
        why = "";
        int size = Marshal.SizeOf<NativeMethods.RECT>();
        IntPtr remote = NativeMethods.VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)size,
            NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
        if (remote == IntPtr.Zero) { why = "（矩形缓冲分配失败，保持命中）"; return true; }

        try
        {
            /* RECT.left 进来时是"要哪一种矩形"：0 = LVIR_BOUNDS（图标 + 文字）。 */
            var seed = new NativeMethods.RECT { Left = NativeMethods.LVIR_BOUNDS };
            if (!WriteStruct(proc, remote, seed, size)) { why = "（矩形缓冲写入失败，保持命中）"; return true; }

            IntPtr ok = NativeMethods.SendMessageTimeoutW(list, NativeMethods.LVM_GETITEMRECT,
                (IntPtr)index, remote, NativeMethods.SMTO_ABORTIFHUNG, (uint)DesktopBudgetMs, out _);
            if (ok == IntPtr.Zero) { why = "（GETITEMRECT 超时，保持命中）"; return true; }

            var bytes = new byte[size];
            if (!NativeMethods.ReadProcessMemory(proc, remote, bytes, (IntPtr)size, out _))
            {
                why = "（矩形读回失败，保持命中）";
                return true;
            }

            var rc = BytesToRect(bytes);
            bool inside = RectContains(rc, clientPt.X, clientPt.Y);
            why = $" rect={rc.Left},{rc.Top},{rc.Right},{rc.Bottom} pt={clientPt.X},{clientPt.Y}";
            return inside;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "中键菜单：GETITEMRECT 复核");
            why = "（复核异常，保持命中）";
            return true;
        }
        finally
        {
            NativeMethods.VirtualFreeEx(proc, remote, IntPtr.Zero, NativeMethods.MEM_RELEASE);
        }
    }

    /// <summary>RECT 的包含判断。抽出来是为了自检能直接验边界（左闭右开、上闭下开）。</summary>
    internal static bool RectContains(NativeMethods.RECT rc, int x, int y) =>
        x >= rc.Left && x < rc.Right && y >= rc.Top && y < rc.Bottom;

    private static NativeMethods.RECT BytesToRect(byte[] bytes)
    {
        var rc = new NativeMethods.RECT();
        rc.Left = BitConverter.ToInt32(bytes, 0);
        rc.Top = BitConverter.ToInt32(bytes, 4);
        rc.Right = BitConverter.ToInt32(bytes, 8);
        rc.Bottom = BitConverter.ToInt32(bytes, 12);
        return rc;
    }

    private static bool WriteStruct<T>(IntPtr proc, IntPtr remote, T value, int size) where T : struct    {
        IntPtr local = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, local, false);
            var bytes = new byte[size];
            Marshal.Copy(local, bytes, 0, size);
            return NativeMethods.WriteProcessMemory(proc, remote, bytes, (IntPtr)size, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    /// <summary>
    /// 我们和目标是不是同一个位数。跨位数发 ≥WM_USER 的带指针消息会打到野地址，
    /// 所以这一条是硬门，不是优化。
    /// </summary>
    private static bool SameBitnessAsTarget(IntPtr proc, out string why)
    {
        why = "";
        bool target64 = Environment.Is64BitOperatingSystem;
        try
        {
            if (NativeMethods.IsWow64Process(proc, out bool wow64))
                target64 = !wow64 && Environment.Is64BitOperatingSystem;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "中键菜单：判目标位数");
            why = "判不出目标位数";
            return false;
        }

        if (target64 == Environment.Is64BitProcess) return true;
        why = target64 ? "目标 64 位、本进程 32 位" : "目标 32 位、本进程 64 位";
        return false;
    }

    // ── 通道二：资源管理器的 DirectUI 图标列表（UI Automation） ─────────

    private static BlankVerdict ProbeExplorerWithUia(IntPtr hwnd, NativeMethods.POINT pt)
    {
        long now = Environment.TickCount64;
        if (SlowWindows.TryGetValue(hwnd, out long until) && now < until)
            return BlankVerdict.Unknown;   // 刚卡过一次：30 秒内不再打扰它

        bool? blank = Uia.Run(() => UiaBlankAt(pt), ExplorerBudgetMs);
        if (blank == null)
        {
            SlowWindows[hwnd] = now + SlowWindowCooldownMs;
            if (SlowWindows.Count > 64) SlowWindows.Clear();   // 防无界增长（窗口句柄会被复用）
            return BlankVerdict.Unknown;
        }
        return blank.Value ? BlankVerdict.Blank : BlankVerdict.Occupied;
    }

    /// <summary>在 STA 线程上跑：命中的元素是不是"列表项"。null = 问不出来。</summary>
    private static bool? UiaBlankAt(NativeMethods.POINT pt)
    {
        AutomationElement? hit;
        try { hit = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y)); }
        catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单：ElementFromPoint"); return null; }
        if (hit == null) return null;

        /* 为什么往上找两层：DirectUI 里真正被命中的常常是列表项内部的 Text / Pane，
         * 只看命中元素本身会把"点在文件名上"误判成空白。
         * 为什么只看三层：再往上就是整个视图容器了，那里必然不是项 —— 多找只会误判。 */
        AutomationElement? cur = hit;
        for (int i = 0; i < 3 && cur != null; i++)
        {
            try
            {
                ControlType ct = cur.Current.ControlType;
                if (ct == ControlType.ListItem || ct == ControlType.DataItem || ct == ControlType.TreeItem)
                    return false;    // 命中项 → 不是空白
            }
            catch (ElementNotAvailableException) { return null; }
            catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单：读 UIA 控件类型"); return null; }

            try { cur = TreeWalker.ControlViewWalker.GetParent(cur); }
            catch (Exception) { break; }
        }

        return true;
    }

    /// <summary>
    /// UIA 的载体：一条常驻 STA 线程。
    ///
    /// 为什么常驻：每次新开线程都要重新初始化 COM + UIA 客户端，首次 50~150ms；
    /// 常驻之后每次就只剩一次跨进程往返。
    /// 为什么不排队：目标窗口卡死时上一次调用回不来，排队只会把钩子线程一起拖住 ——
    /// 所以"忙"的时候直接放弃，调用方按 Unknown 处理。
    /// </summary>
    private sealed class UiaWorker
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _signal = new(0, 1);
        private Thread? _thread;
        private Func<bool?>? _work;
        private bool? _result;
        private bool _busy;

        internal bool? Run(Func<bool?> work, int timeoutMs)
        {
            lock (_gate)
            {
                if (_busy) return null;
                _busy = true;
                _work = work;
                _result = null;
                EnsureThreadLocked();
            }
            _signal.Release();

            long deadline = Environment.TickCount64 + Math.Max(1, timeoutMs);
            lock (_gate)
            {
                while (_busy)
                {
                    int left = (int)(deadline - Environment.TickCount64);
                    if (left <= 0) return null;   // 超时：这次算 Unknown，线程回来时自己清 _busy
                    Monitor.Wait(_gate, left);
                }
                return _result;
            }
        }

        private void EnsureThreadLocked()
        {
            if (_thread != null) return;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "InputPlus.BlankHit.Uia",
            };
            // UIA 是 COM，必须是 STA。这条线只跑命中判定，不碰任何界面对象。
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Loop()
        {
            while (true)
            {
                _signal.Wait();
                Func<bool?>? work;
                lock (_gate) work = _work;

                bool? result = null;
                try { result = work?.Invoke(); }
                catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单：UIA 工作线程"); }

                lock (_gate)
                {
                    _result = result;
                    _work = null;
                    _busy = false;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }
}
