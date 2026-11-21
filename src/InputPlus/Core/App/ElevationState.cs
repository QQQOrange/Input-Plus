using System.Runtime.InteropServices;
using InputPlus.Core.Interop;

namespace InputPlus.Core.App;

/// <summary>
/// 注入（替用户按键 / 移动鼠标）走哪条路。
///
/// 为什么需要这个选择：低级键盘/鼠标钩子与 UIPI 的方向性完全相反 ——
///   * **钩子**看得到"同级或更低完整性"进程的输入：提升进程装的钩子能看到
///     普通程序（浏览器、explorer）的按键，普通进程的钩子看不到提升程序窗口的输入；
///   * **SendInput** 被 UIPI 拦的是"低 → 高"：普通进程往提升窗口送键会被拒
///     （ERROR_ACCESS_DENIED = 5），提升进程往普通窗口送键是允许的。
///
/// 于是"钩子放在提升进程、注入放在普通权限的钩子宿主"这套组合是自洽的：
/// 看得全，且注入端与钩子端在同一完整性级别（注入事件自己也能被看见，
/// 回声抑制 / 防双触发靠的就是这一点）。
/// </summary>
internal enum InjectionRoute
{
    /// <summary>在本进程里直接 SendInput（普通权限下的默认路径）。</summary>
    InProcess,

    /// <summary>下沉给 --hookhost 子进程（普通权限）去 SendInput。</summary>
    HookHost,
}

/// <summary>一次注入为什么注定失败。</summary>
internal enum InjectionBarrier
{
    /// <summary>没有已知障碍。</summary>
    None,

    /// <summary>目标窗口属于提升进程，而我们是普通权限 —— UIPI 必然拦掉。</summary>
    ReceiverElevated,
}

/// <summary>
/// 提权状态的**纯判定**中心。
///
/// 这里不启动任何进程、不碰界面，所以可以被自检直接驱动
/// （自检里不会真的去拉起一个提升进程 —— 那会弹 UAC，把无人值守的
/// --selftest 卡死）。真正启动进程的地方见 <see cref="HookHostLauncher"/>。
/// </summary>
internal static class ElevationState
{
    private const int TokenElevationClass = 20;   // TOKEN_INFORMATION_CLASS.TokenElevation
    private const int TokenQuery = 0x0008;        // TOKEN_QUERY
    private const int ProbeTtlMs = 250;

    private static bool? _cachedElevated;

    /// <summary>当前进程是否提升（缓存 —— 进程令牌在生命周期内不会变）。</summary>
    internal static bool Elevated => _cachedElevated ??= ElevationHelper.IsElevated;

    /// <summary>仅供自检：清掉缓存与探测缓存。</summary>
    internal static void ResetCacheForTest()
    {
        _cachedElevated = null;
        _probeHwnd = IntPtr.Zero;
        _probeAtMs = 0;
        _probeResult = false;
    }

    /// <summary>仅供自检：直接指定"当前是否提升"，不读真实令牌。</summary>
    internal static void ForceElevatedForTest(bool? elevated) => _cachedElevated = elevated;

    /// <summary>
    /// 注入路由：提升 ⇒ 下沉给普通权限的钩子宿主；未提升 ⇒ 本进程直接发。
    ///
    /// 这是本文件唯一要记住的规则，写成纯函数是为了能被自检钉住
    /// （见 SelfTest.Elevation）。
    /// </summary>
    internal static InjectionRoute RouteFor(bool elevated)
        => elevated ? InjectionRoute.HookHost : InjectionRoute.InProcess;

    /// <summary>当前进程的注入路由（不考虑目标窗口）。</summary>
    internal static InjectionRoute Route => RouteFor(Elevated);

    /// <summary>
    /// 结合目标窗口与宿主可用性算出实际走哪条路。
    ///
    /// 两个例外必须压过 <see cref="RouteFor"/>：
    ///   1) **目标窗口是提升的**：普通权限的宿主必然被 UIPI 拦掉（低 → 高），
    ///      而提升的我们直接送是高 → 高，是允许的 —— 只能本进程发；
    ///   2) **宿主没就绪**：没处下沉，退回本进程。
    /// </summary>
    internal static InjectionRoute ResolveRoute(bool selfElevated, bool receiverElevated, bool hookHostLive)
    {
        if (receiverElevated) return InjectionRoute.InProcess;
        if (!hookHostLive) return InjectionRoute.InProcess;
        return RouteFor(selfElevated);
    }

    /// <summary>这次注入注定被拦吗（普通进程 → 提升窗口 = 拦）。</summary>
    internal static InjectionBarrier BarrierFor(bool selfElevated, bool receiverElevated)
        => receiverElevated && !selfElevated ? InjectionBarrier.ReceiverElevated : InjectionBarrier.None;

    internal static string RouteName(InjectionRoute route)
        => route == InjectionRoute.HookHost ? "HookHost" : "InProcess";

    internal static string BarrierText(InjectionBarrier barrier)
    {
        switch (barrier)
        {
            case InjectionBarrier.ReceiverElevated:
                return Loc.T("目标窗口以管理员身份运行，而 InputPlus 当前是普通权限：系统会拦下这次按键。"
                             + "请把目标程序改成普通权限运行，或在设置里开启「以管理员身份运行」后重启 InputPlus。");
            default:
                return "";
        }
    }

    /* ---------------- 前台窗口的完整性级别（廉价探测） ----------------
     *
     * 不用 InputSender.IsForegroundHigherIntegrity()：它发一次
     * SendMessageTimeout(80ms) 探路，放进"每次按键都要走"的注入出口会把
     * 键盘拖出可感知的延迟。这里改成"查进程令牌"，三四个系统调用、几十微秒，
     * 再按 (hwnd, 250ms) 缓存一次 —— 人手速度下等于没有开销。 */

    private static IntPtr _probeHwnd;
    private static long _probeAtMs;
    private static bool _probeResult;

    /// <summary>前台窗口是否属于提升进程（带 250ms 缓存）。</summary>
    internal static bool ForegroundIsElevated()
    {
        IntPtr hwnd;
        try { hwnd = NativeMethods.GetForegroundWindow(); }
        catch { return false; }
        if (hwnd == IntPtr.Zero) return false;

        long now = Environment.TickCount64;
        if (hwnd == _probeHwnd && now - _probeAtMs < ProbeTtlMs) return _probeResult;

        bool result = IsWindowElevated(hwnd);
        _probeHwnd = hwnd;
        _probeResult = result;
        _probeAtMs = now;
        return result;
    }

    /// <summary>某个窗口的进程是否提升。拿不到信息时按"没有提升"处理（宁可照常注入，也不要凭空报错）。</summary>
    internal static bool IsWindowElevated(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr process = IntPtr.Zero;
        IntPtr token = IntPtr.Zero;
        try
        {
            if (NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return false;

            process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return false;

            if (!NativeMethods.OpenProcessToken(process, TokenQuery, out token) || token == IntPtr.Zero) return false;

            if (!GetTokenInformation(token, TokenElevationClass, out uint isElevated, sizeof(uint), out _)) return false;
            return isElevated != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) { try { NativeMethods.CloseHandle(token); } catch { } }
            if (process != IntPtr.Zero) { try { NativeMethods.CloseHandle(process); } catch { } }
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out uint buffer, int length, out int returned);
}
