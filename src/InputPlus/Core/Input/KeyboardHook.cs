using System.Diagnostics;
using System.Runtime.InteropServices;
using InputPlus.Core.Interop;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Input;

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL）。
/// 只做“判断 + 排队”，任何耗时动作都要通过回调交给动作队列执行，
/// 否则系统会因为钩子超时把钩子静默移除（上游 AutoHotkey 版本没有这个保护）。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /* 只装鼠标钩子。
     *
     * 手势服务需要一颗“自己的”鼠标钩子：宿主模式（--hookhost）下键盘/鼠标钩子
     * 在最小进程里，主进程的 Engine.Hook 根本没装 —— 手势订阅它等于订阅空气。 */
    private bool _mouseOnly;
    private readonly HookProc _proc;          // 必须保存引用，防止被 GC
    private IntPtr _hook = IntPtr.Zero;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _running;

    private System.Threading.Timer? _watchdog;

    /// <summary>看门狗投递给钩子线程的自定义消息（重装钩子）。</summary>
    private const int WM_APP_REARM = 0x8001;
    private const int WM_APP_PING = 0x0400 + 0x2A;   // 探活：确认钩子线程还在跑（不是被摘/卡死）

    /// <summary>首次巡检延迟：覆盖"装钩失败"这种一开始就不工作的情况。</summary>
    private const int WatchdogFirstDelayMs = 5_000;

    /// <summary>巡检周期。重装只有几微秒，但没必要太频繁。</summary>
    private const int WatchdogIntervalMs = 180_000;

    /// <summary>
    /// 按键事件回调：vk、是否按下、是否注入事件；返回 true 表示吞掉这个按键
    /// （不再传给前台程序）。回调必须极快，不能阻塞。
    /// </summary>
    public event Func<int, bool, bool, bool>? KeyEvent;

    /// <summary>
    /// 回调抛异常时的兜底：由引擎判断这个键是否仍要吞掉（fail-closed）。
    ///
    /// 默认行为是"异常即放行"（fail-open）—— 对普通按键无害，但对触发键
    /// 与层内按键是灾难：层明明已经生效，Tab 却连每一次自动重复都被漏给
    /// 前台，用户看到的就是"切完层焦点还在跳"。
    /// 这里把决定权交回引擎，由它按"当前是否处于层语义"给答案。
    /// </summary>
    public Func<int, bool>? SwallowOnError { get; set; }

    /// <summary>鼠标滚轮回调：滚动格数（上正下负）、是否按住 Caps。</summary>
    public event Action<int, bool, bool>? WheelEvent;

    /// <summary>
    /// 鼠标按键回调：屏幕物理坐标 x、y 和鼠标消息（WM_LBUTTONDOWN 等）。
    /// 只在本程序自己没注入时才触发。回调运行在钩子线程，必须极快。
    ///
    /// 用途：Qbar 面板打开期间判断"点没点到面板外面" —— 任务栏这类
    /// 不会换前台的地方不会产生 Deactivated，只能靠全局钩子兜底。
    /// </summary>
    public event Action<int, int, int>? MouseDownEvent;

    /// <summary>
    /// 鼠标按键的"按下/抬起"，与键盘的 <see cref="KeyEvent"/> 完全同构：
    /// 参数 (vk, down, injected) → 返回 true 表示吞掉这次事件。
    ///
    /// vk 用 0x01~0x06（VK_LBUTTON / RBUTTON / MBUTTON / XBUTTON1 / XBUTTON2），
    /// 与 <see cref="KeyName"/> 里的 mouseLeft… 一一对应 —— 这样改键、层绑定、
    /// 触发键都能复用同一套按键名，不必再维护第二套标识。
    ///
    /// 只在 nCode 有效时触发；注入事件也会送进来（injected = true），
    /// 由引擎统一放行，保证"自己注入的键不会被自己再改一次"。
    /// </summary>
    public event Func<int, bool, bool, bool>? MouseButtonEvent;

    /// <summary>
    /// 手势的**前置**鼠标按键回调：(vk, down, injected) → 是否吞掉。
    ///
    /// 必须先于 <see cref="MouseButtonEvent"/> 触发：用户把侧键配成手势起点时，
    /// 这颗键就不该再走"改键 / 层内动作"那套逻辑，否则同一次按下会既开始画手势
    /// 又执行一遍绑定。返回 true 时后面的回调一概不跑，事件直接吞掉。
    ///
    /// 注入事件也会送进来（injected = true），由手势服务自己决定放行 ——
    /// 这样"识别失败时补发的单击"不会被当成用户又按了一次。
    /// </summary>
    public Func<int, bool, bool, bool>? GestureButtonEvent;

    /// <summary>
    /// 手势的前置滚轮回调：(方向, injected, 是否水平) → 是否吞掉。
    /// 方向：垂直 +1 上滚 / -1 下滚；水平 +1 右拨 / -1 左拨。
    /// 与 <see cref="WheelEvent"/> 的区别同按键：手势优先，没接手才轮到改键/滚动逻辑。
    /// </summary>
    public Func<int, bool, bool, bool>? GestureWheelEvent;

    /// <summary>
    /// 旁观用的按键通知：(vk, down, injected)。**没人看返回值**，纯通知 ——
    /// 不会改变这个键是吞还是放行。
    ///
    /// 用途只有一处：多步手势里的「按一组键」那一步要记账（v1.5.0）。
    /// 为什么不用 <see cref="KeyEvent"/>：那条链路是改键引擎的，返回 true 就会吞键；
    /// 多步手势只是"看着你按了什么"，不该改变按键本身的行为。
    /// 自己注入的键也会报进来（injected = true），由调用方忽略。
    /// </summary>
    public Action<int, bool, bool>? KeySeen;

    /// <summary>
    /// 原始鼠标移动：(屏幕 X, 屏幕 Y)。
    ///
    /// 为什么要有：手势的轨迹采样原先只靠一个 8ms 的 DispatcherTimer，实测在整盘扫索引
    /// 期间它被饿到"一笔只采到 2 个点"，而 DrawnStroke 要求 ≥3 点 —— 表现就是
    /// "鼠标手势全失效（偏差 1.000）"。钩子本身是消息驱动的，每来一条移动就记一点，
    /// 不依赖任何定时器。只在鼠标钩子装上时有效。
    /// </summary>
    public Action<int, int>? MouseMoveEvent;

    /// <summary>
    /// 平滑滚动的前置滚轮回调：(原始 delta, injected, 是否水平) → 是否吞掉。
    ///
    /// 位置在手势之后、<see cref="WheelEvent"/> / 改键之前：手势优先，其次是
    /// "这个滚轮是不是用户的显式绑定"（由滚动服务自己去问引擎），最后才轮到
    /// 平滑滚动。返回 true 时原始事件被吞掉，改由滚动服务分帧注入小 delta。
    ///
    /// 与 <see cref="GestureWheelEvent"/> 的另一个差别：这里给的是**原始 delta**。
    /// 一格是 120，触摸板会发 15/30 这种细碎值 —— 平滑滚动要靠它区分
    /// "这是一格鼠标滚轮"还是"这是触摸板的连续滑动"。
    /// </summary>
    public Func<int, bool, bool, bool>? ScrollWheelEvent;

    private HookProc? _mouseProc;
    private IntPtr _mouseHook = IntPtr.Zero;

    /// <summary>
    /// 钩子是否真正可用。
    /// 不能只看 _running 这个“意愿”标志 —— 钩子句柄为 0 时说明安装失败或被系统摘掉，
    /// 此时 _running 仍是 true，自愈监控会一直以为一切正常。
    /// </summary>
    public bool IsRunning => _running
        && (_mouseOnly ? _mouseHook != IntPtr.Zero : _hook != IntPtr.Zero);

    /// <summary>钩子最近一次异常/失效的时间，用于自愈监控。</summary>
    public DateTime LastErrorTime { get; private set; } = DateTime.MinValue;

    /// <summary>已安装的钩子句柄（供自检使用）。</summary>
    public bool HookInstalled => _hook != IntPtr.Zero;
    public bool MouseHookInstalled => _mouseHook != IntPtr.Zero;

    /// <summary>诊断开关：把收到的每个按键写进日志（排查“按键没反应”时打开）。</summary>
    public bool TraceKeys { get; set; }

    /// <summary>累计收到的按键数（诊断用）。</summary>
    public long KeyEventCount => Interlocked.Read(ref _keyEvents);
    private long _keyEvents;

    /// <summary>累计收到的滚轮事件数（垂直 + 水平，含注入；诊断用）。</summary>
    public long WheelEventCount => Interlocked.Read(ref _wheelEvents);    private long _wheelEvents;
    private long _physicalWheelEvents;
    private long _injectedWheelEvents;

    /// <summary>其中"不带本程序标记"的滚轮数（真正的手拨；诊断用）。</summary>
    public long PhysicalWheelEventCount => Interlocked.Read(ref _physicalWheelEvents);

    /// <summary>其中"带本程序注入标记"的滚轮数（平滑滚动自己发的；诊断用）。</summary>
    public long InjectedWheelEventCount => Interlocked.Read(ref _injectedWheelEvents);

    /// <summary>LLKHF_INJECTED：这个键是别的程序注射的，不是人的手按的。</summary>
    private const uint LLKHF_INJECTED = 0x10;

    private static int _physCtrl, _physAlt, _physShift;

    /// <summary>
    /// 人此刻是否**真的**按着这个修饰键（注射来的不算）。
    ///
    /// `GetAsyncKeyState` 会把别的程序注射的 Ctrl/Alt 也算成"按着"。
    /// 现场就有一个工具在按 Ctrl+C 打点（`dwExtraInfo=0x5153454C46`，一秒几十次），
    /// 于是"Ctrl+滚轮放行"那道闸时真时假 —— 用户看到的是"滚轮有时候失效"。
    /// 修饰键的语义必须只认物理事件。
    /// </summary>
    public bool IsPhysicalModifierDown(int vk) => vk switch
    {
        VK.Control or VK.LControl or VK.RControl => Volatile.Read(ref _physCtrl) != 0,
        VK.Menu or VK.LMenu or VK.RMenu => Volatile.Read(ref _physAlt) != 0,
        VK.Shift or VK.LShift or VK.RShift => Volatile.Read(ref _physShift) != 0,
        _ => false,
    };

    public KeyboardHook()
    {
        _proc = HookCallback;
        _mouseProc = MouseCallback;
    }

    public void Start(bool mouseOnly = false)
    {
        if (_running) return;
        _mouseOnly = mouseOnly;

        /* 实验：CLP_HOOK_UI_THREAD=1 时把钩子装在**调用线程**（界面线程）上。
         *
         * 依据：程序里的 --diag / KeyDiagnostics 就是装在自己的线程上，
         * 在我们的所有测试里从未失效过；而装在后台线程上的那条链路会时好时坏。 */
        if (Environment.GetEnvironmentVariable("CLP_HOOK_UI_THREAD") == "1")
        {
            InstallOnCurrentThread();
            return;
        }

        /* 上一轮线程还没退干净时**等它退出**再装。
         * 注意：这里**不能**"等不到就放弃安装" —— 那等于把钩子彻底弄没了，
         * 而调用方（自愈逻辑）以为已经重装成功。 */
        if (_thread is { IsAlive: true } stale && !stale.Join(3000))
            CoreLog.Warn("上一条钩子线程 3 秒内没退出，仍继续安装新的（旧线程只卸自己装的句柄）");

        _running = true;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();

            /* 句柄必须**先用局部变量接着**。
             *
             * 以前这里直接写 `_hook = SetWindowsHookEx(...)`，退出时也照 `_hook` 卸 ——
             * 一旦出现"旧线程还没退、新线程已经装好"（Stop 的 Join 超时就会这样），
             * 旧线程退出时卸掉的是**新线程的句柄**：新钩子从此不在链上，
             * 句柄非零、线程还活着、状态全正常，但一个按键都收不到。
             * 这正好是"钩子时好时坏、重装也救不回来"的成因。 */
            string desktopText = AlignToInputDesktop();
            var myHook = _mouseOnly
                ? IntPtr.Zero
                : SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            _hook = myHook;
            CoreLog.Info($"钩子线程 tid={_threadId} 安装结果=0x{myHook.ToInt64():X}；{desktopText}");

            /* 实验开关：CLP_NO_MOUSE_HOOK=1 时不装鼠标钩子（排查"键盘钩子被摘"用）。 */
            var myMouseHook = Environment.GetEnvironmentVariable("CLP_NO_MOUSE_HOOK") == "1"
                ? IntPtr.Zero
                : SetWindowsHookEx(WH_MOUSE_LL, _mouseProc!, GetModuleHandle(null), 0);
            _mouseHook = myMouseHook;
            if (!_mouseOnly && _hook == IntPtr.Zero)
            {
                LastErrorTime = DateTime.Now;
                CoreLog.Error($"键盘钩子安装失败，错误码 {Marshal.GetLastWin32Error()}");
            }
            if (_mouseHook == IntPtr.Zero)
            {
                LastErrorTime = DateTime.Now;
                CoreLog.Error($"鼠标钩子安装失败，错误码 {Marshal.GetLastWin32Error()}");
            }
            MSG msg;
            while (_running && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                /* 看门狗的重装请求必须在本线程处理：SetWindowsHookEx 装出来的
                 * 钩子绑定的是"调用它的线程"，换线程装会得到一个没有消息循环
                 * 的钩子，等于装了个不会回调的空壳。 */
                if (msg.message == WM_APP_REARM) { RearmHooks(); continue; }
                if (msg.message == WM_APP_PING) { Interlocked.Increment(ref _pingAcks); continue; }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            CoreLog.Error($"钩子线程退出（_running={_running}）：从此不再收到任何按键");
            /* 线程没了，管理器里的状态必须跟着清 —— 否则看门狗看到 `_running=true` +
             * `_threadId!=0` 就以为还有救，`PostThreadMessage(WM_APP_REARM)` 全打进空气，
             * 钩子永远装不回来（"按住拖拽/手势彻底没反应，重启才好"就是这么来的）。 */
            _running = false;
            _threadId = 0;

            /* 只卸**自己装的**那两个句柄；如果字段已经被新线程换掉，不能动它。 */
            if (myHook != IntPtr.Zero) UnhookWindowsHookEx(myHook);
            if (myMouseHook != IntPtr.Zero) UnhookWindowsHookEx(myMouseHook);
            if (_hook == myHook) _hook = IntPtr.Zero;
            if (_mouseHook == myMouseHook) _mouseHook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "InputPlus.KeyboardHook",
        };
        _thread.Start();
        // 等钩子装好，避免界面已经显示但按键还不生效
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500 && _running
               && (_mouseOnly ? _mouseHook == IntPtr.Zero : _hook == IntPtr.Zero)) Thread.Sleep(10);
        StartWatchdog();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
            if (isDown || isUp)
            {
                Interlocked.Increment(ref _keyEvents);
                // 只忽略“本程序自己发的”按键：靠 dwExtraInfo 标记判断。
                //
                // 不要用 LLKHF_INJECTED —— 它会把所有合成按键都算进去，
                // 包括触摸键盘、屏幕键盘、其他自动化工具和远程桌面输入。
                // 那样一来，用这些方式输入时 Caps 组合键会全部失效。
                bool injectedByUs = data.dwExtraInfo == InputSender.InjectionTag;

                /* 物理修饰键状态：只认**非注射**事件（LLKHF_INJECTED = 0x10）。
                 *
                 * 为什么要单独记一份：`GetAsyncKeyState` 分不清"人按着 Ctrl"和
                 * "别的程序正在注射 Ctrl"。现场就有一个工具在按 Ctrl+C 打点
                 * （`dwExtraInfo=0x5153454C46` = "QSELF"），一秒几十次 —— 于是
                 * 平滑滚动里"Ctrl+滚轮放行"那道闸时真时假，用户看到的就是
                 * "滚轮有时候失效"。修饰键的语义必须是"人的手按着"。 */
                if ((data.flags & LLKHF_INJECTED) == 0)
                {
                    bool down = isDown;
                    switch ((int)data.vkCode)
                    {
                        case VK.Control: case VK.LControl: case VK.RControl:
                            Volatile.Write(ref _physCtrl, down ? 1 : 0); break;
                        case VK.Menu: case VK.LMenu: case VK.RMenu:
                            Volatile.Write(ref _physAlt, down ? 1 : 0); break;
                        case VK.Shift: case VK.LShift: case VK.RShift:
                            Volatile.Write(ref _physShift, down ? 1 : 0); break;
                    }
                }
                if (TraceKeys)
                {
                    CoreLog.Info($"钩子收到 vk=0x{data.vkCode:X2} down={isDown} " +
                                 $"flags=0x{data.flags:X} extra=0x{data.dwExtraInfo.ToInt64():X} 自己发的={injectedByUs}");
                }
                bool swallow = false;
                Volatile.Write(ref _callbackVk, (int)data.vkCode);
                Interlocked.Exchange(ref _callbackStartedTick, Environment.TickCount64);
                try
                {
                    swallow = KeyEvent?.Invoke((int)data.vkCode, isDown, injectedByUs) ?? false;
                }
                catch (Exception ex)
                {
                    /* 异常绝不能等于"放行"。
                     *
                     * 早先是 swallow=false 直接落到 CallNextHookEx：只要引擎在
                     * 层内分支上抛一次（空引用、绑定数据异常、P/Invoke 失败），
                     * 这个键的**每一次自动重复**都会被漏给前台 ——
                     * 表现正是"层已经切好，Tab 还在别的程序里跳焦点"。
                     *
                     * 现在把决定权交回引擎：处于层语义就吞，宁可这次按键
                     * 没反应，也不要把按键漏出去。 */
                    CoreLog.Error($"按键处理回调异常 vk=0x{data.vkCode:X2} down={isDown}", ex);
                    try { swallow = SwallowOnError?.Invoke((int)data.vkCode) ?? false; }
                    catch { swallow = false; }
                }
                finally { Interlocked.Exchange(ref _callbackStartedTick, 0); }

                if (KeySeen != null)
                {
                    try { KeySeen((int)data.vkCode, isDown, injectedByUs); }
                    catch (Exception ex) { CoreLog.Error("按键旁观回调异常", ex); }
                }

                // 返回 1（非零）且不调用 CallNextHookEx 即可吞掉该按键
                if (swallow) return new IntPtr(1);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Error("键盘钩子回调异常", ex);
        }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        finally { ObserveCallbackCost(t0, lParam); }
    }

    /// <summary>
    /// 钩子回调耗时统计。
    ///
    /// 系统有一条硬限制：低级钩子回调超过 LowLevelHooksTimeout（默认 300ms）
    /// 就会被**静默摘掉** —— 之后所有按键都收不到，而句柄还在，IsHookAlive 照样是 true。
    /// 表现就是「录制宏时一个键都没有」「组合键突然全失灵」。
    /// 所以这里把每次回调的耗时量出来：慢的当场记一条，最快能定位是谁卡住了钩子线程。
    /// </summary>
    /// <summary>
    /// 回调"进行中"打点：进入时记时间戳，退出时清零。
    ///
    /// 用途：低级钩子回调如果**卡住不返回**，系统会等它（最长 300ms）然后丢弃这次输入、
    /// 并可能把钩子摘掉。这种阻塞从"回调耗时"里看不出来（它根本没返回），
    /// 只能靠另一个线程观察这个时间戳。症状是"整个系统的按键都变慢/丢，热键全失灵"。
    /// </summary>
    private long _callbackStartedTick;
    private int _callbackVk;

    /// <summary>回调是否正卡着（毫秒）。0 表示没卡。</summary>
    internal long CallbackStuckMs
    {
        get
        {
            long started = Interlocked.Read(ref _callbackStartedTick);
            if (started == 0) return 0;
            long ms = Environment.TickCount64 - started;
            return ms > 2000 ? ms : 0;
        }
    }

    /// <summary>卡住时正在处理的那个键（诊断用）。</summary>
    internal int StuckVk => Volatile.Read(ref _callbackVk);

    private void ObserveCallbackCost(long t0, IntPtr lParam)
    {
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (ms > MaxCallbackCostMs) MaxCallbackCostMs = ms;
        if (ms < 30) return;

        // 限流：卡住时往往连续很多次，别把日志刷爆
        long now = Environment.TickCount64;
        if (now - _lastSlowLogAt < 5000) return;
        _lastSlowLogAt = now;

        int vk = 0;
        try { vk = (int)Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode; } catch { }
        CoreLog.Warn($"钩子回调耗时 {ms:F0}ms（vk=0x{vk:X2}）—— 超过 300ms 系统会直接摘钩，之后不再收到任何按键");
    }

    /// <summary>
    /// 钩子自检：自己敲一个**不带标记**的 F24，看钩子能不能收到。
    ///
    /// 这是唯一能分清"钩子坏了"和"用户还没按键"的办法：
    /// 杀掉一切外部依赖，自己发一个键再自己数。
    /// 收到就说明钩子在链上且能收注入事件，收不到就是真坏了 —— 直接重装。
    /// </summary>
    /// <returns>true=钩子看到了；false=没看到；null=注入本身失败（结论未知，别当成"钩子坏了"）</returns>
    public bool? SelfProbe(int timeoutMs = 400)
    {
        if (!IsRunning) return false;

        long before = Interlocked.Read(ref _keyEvents);
        if (!InputSender.TapUntagged(0x87 /*F24*/)) return null;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Interlocked.Read(ref _keyEvents) >= before + 2) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>钩子线程状态描述（诊断用）。</summary>
    public string ThreadState
    {
        get
        {
            var t = _thread;
            if (t == null) return "(无线程)";
            return t.IsAlive ? $"存活（{t.ThreadState}）" : $"已退出（{t.ThreadState}）";
        }
    }

    /// <summary>钩子回调的最长耗时（毫秒），诊断用。</summary>
    public double MaxCallbackCostMs { get; private set; }

    /// <summary>
    /// **鼠标**回调的最长耗时（毫秒）。
    ///
    /// 以前只有键盘那份统计（<see cref="MaxCallbackCostMs"/>），鼠标回调完全没量过 ——
    /// 而恰恰是它最重：一次按下要解析作用目标（OpenProcess）、跑候选匹配、
    /// 中键还要做 LVM_HITTEST/UIA；滚轮每格都要再来一次。
    /// 这些都在钩子线程上同步跑，超过 LowLevelHooksTimeout 就是"点了没反应"。
    /// 没有这条统计，出问题只能靠猜。
    /// </summary>
    public double MaxMouseCallbackCostMs { get; private set; }

    /// <summary>鼠标回调累计次数（探活用：它不涨就说明钩子已经不在链上了）。</summary>
    public long MouseEventCount => Interlocked.Read(ref _mouseEvents);
    private long _mouseEvents;

    /// <summary>鼠标回调"进行中"打点（同键盘那份，见 <see cref="CallbackStuckMs"/>）。</summary>
    private long _mCallbackStartedTick;
    private int _mCallbackMsg;

    /// <summary>鼠标回调正卡着多久（毫秒）。0 = 没卡。</summary>
    internal long MouseCallbackStuckMs
    {
        get
        {
            long started = Interlocked.Read(ref _mCallbackStartedTick);
            if (started == 0) return 0;
            long ms = Environment.TickCount64 - started;
            return ms > 2000 ? ms : 0;
        }
    }

    /// <summary>卡住时正在处理的消息号（WM_*）。</summary>
    internal int MouseStuckMsg => Volatile.Read(ref _mCallbackMsg);

    private long _lastMouseSlowLogAt;

    private void ObserveMouseCallbackCost(long t0)
    {
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (ms > MaxMouseCallbackCostMs) MaxMouseCallbackCostMs = ms;
        if (ms < 30) return;

        long now = Environment.TickCount64;
        if (now - _lastMouseSlowLogAt < 5000) return;
        _lastMouseSlowLogAt = now;

        int msg = Volatile.Read(ref _mCallbackMsg);
        CoreLog.Warn($"鼠标钩子回调耗时 {ms:F0}ms（msg=0x{msg:X}）—— 超过 LowLevelHooksTimeout 会被摘钩，"
                     + "期间点击/滚动全部排队；请查作用目标解析、命中判定是否又在钩子里做了");
    }

    private long _lastSlowLogAt;

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        /* 计时 + 计数 + 卡点打点。
         *
         * 这条回调是整台机器输入路径上最容易被堵住的地方：一次按下要解析作用目标
         * （OpenProcess）、跑候选匹配，中键还要 LVM_HITTEST/UIA；滚轮每格再来一次。
         * 全都在钩子线程上同步跑，堵住就是"点了没反应 / 滚一格卡一下"，
         * 而以前这里**一点数字都没有**，只能靠猜。 */
        long t0 = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _mouseEvents);
        Volatile.Write(ref _mCallbackMsg, (int)wParam);
        Interlocked.Exchange(ref _mCallbackStartedTick, Environment.TickCount64);
        try
        {
            return MouseCallbackCore(nCode, wParam, lParam);
        }
        finally
        {
            Interlocked.Exchange(ref _mCallbackStartedTick, 0);
            ObserveMouseCallbackCost(t0);
        }
    }

    private IntPtr MouseCallbackCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 探针：确认钩子宿主到底有没有收到这次鼠标事件（排查"按住拖拽收不到鼠标"）。
        // 探针关着时连字符串都不拼 —— 这是鼠标钩子回调里，每次点击/滚轮都会走到的位置。
        if (wParam.ToInt64() != 512 && Modules.HoldDrag.HookProbe.Enabled)
        {
            Modules.HoldDrag.HookProbe.W("HOST-MS nCode=" + nCode + " msg=" + wParam.ToInt64());
        }
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        try
        {
            int msg = (int)wParam;
            if (msg == 512 && MouseMoveEvent is { } onMove)
            {
                /* 纯鼠标移动：手势要靠它采样。先做这一条，下面那些按键分支都不沾它。 */
                var md = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                onMove(md.pt.X, md.pt.Y);
            }
            else if (msg == WM_MOUSEWHEEL)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                Interlocked.Increment(ref _wheelEvents);
                // 与键盘钩子保持一致：只忽略本程序自己发的滚轮事件。
                bool injectedByUs = data.dwExtraInfo == InputSender.InjectionTag;
                if (injectedByUs) Interlocked.Increment(ref _injectedWheelEvents);
                else Interlocked.Increment(ref _physicalWheelEvents);
                if (!injectedByUs)
                {
                    int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    if (delta != 0)
                    {
                        /* 手势优先：按住起始键时滚轮可能是一条手势。
                         * 手势没接手才往下走原有的滚动逻辑。
                         *
                         * 两条各自 try/catch：以前它们在一个 try 块里，手势那条一抛异常，
                         * 这一格滚轮就直接落到 CallNextHookEx —— 平滑滚动**一次都收不到**，
                         * 用户看到的是"平滑滚动时好时坏"。抛异常不该连带打死另一个功能。 */
                        if (GestureWheelEvent is { } gestureWheel)
                        {
                            bool took;
                            try { took = gestureWheel(delta > 0 ? 1 : -1, false, false); }
                            catch (Exception ex) { CoreLog.Swallow(ex, "手势滚轮回调"); took = false; }
                            if (took) return new IntPtr(1);
                        }
                        if (ScrollWheelEvent is { } scrollWheel)
                        {
                            bool took;
                            try { took = scrollWheel(delta, false, false); }
                            catch (Exception ex) { CoreLog.Swallow(ex, "平滑滚动回调"); took = false; }
                            if (took) return new IntPtr(1);
                        }
                        WheelEvent?.Invoke(delta > 0 ? 1 : -1, false, false);
                    }
                }
            }
            else if (msg == WM_MOUSEHWHEEL)
            {
                /* 水平滚轮（滚轮左右拨 / 触摸板左右滑）。
                 *
                 * 它和左右键不一样：没有"按住"，一次拨动就是一个事件。这里把它
                 * 拆成 按下 + 抬起 两条，复用同一套改键 / 替换层逻辑 —— 用户就
                 * 可以像配鼠标中键那样配"滚轮左拨 → 音量减"。
                 *
                 * delta 的正负约定和 Windows 一致：正 = 右拨，负 = 左拨。 */
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                Interlocked.Increment(ref _wheelEvents);
                int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                if (delta != 0)
                {
                    bool injected = data.dwExtraInfo == InputSender.InjectionTag;
                    if (injected) Interlocked.Increment(ref _injectedWheelEvents);
                    else Interlocked.Increment(ref _physicalWheelEvents);
                    if (GestureWheelEvent is { } gestureWheel
                        && gestureWheel(delta > 0 ? 1 : -1, injected, true))
                        return new IntPtr(1);
                    if (ScrollWheelEvent is { } scrollWheel
                        && scrollWheel(delta, injected, true))
                        return new IntPtr(1);
                    int wheelVk = delta > 0 ? VK.WheelRight : VK.WheelLeft;
                    if (MouseButtonEvent is { } wheelHandler)
                    {
                        bool swallowed = wheelHandler(wheelVk, true, injected);
                        wheelHandler(wheelVk, false, injected);
                        if (swallowed) return new IntPtr(1);
                    }
                }
            }
            else if (TryMouseButton(msg, lParam, out int vk, out bool down, out var data))
            {
                bool injected = data.dwExtraInfo == InputSender.InjectionTag;

                /* 手势优先级最高：起点键一旦被某条手势占用，就由手势服务接管
                 * 这颗键的按下/抬起（识别失败时它会补发一次单击，用户不会
                 * 丢掉"右键菜单"这类基本操作）。 */
                if (GestureButtonEvent is { } gestureButton && gestureButton(vk, down, injected))
                {
                    if (Modules.HoldDrag.HookProbe.Enabled)
                        Modules.HoldDrag.HookProbe.W("HOST-MS 被手势吞掉 vk=" + vk + " down=" + down + " injected=" + injected);
                    return new IntPtr(1);
                }

                /* Qbar 的"点面板外面就收起"只关心按下，且**只认真人操作** ——
                 * 自己注入的点击不能把面板关掉。 */
                if (down && msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN
                    && data.dwExtraInfo != InputSender.InjectionTag)
                {
                    MouseDownEvent?.Invoke(data.pt.X, data.pt.Y, msg);
                }

                /* 改键 / 层绑定对鼠标键的处理：与键盘同一个回调约定（返回 true = 吞）。
                 * 放在 Qbar 之后 —— 面板要不要收起是"观察"，改键是"接管"，
                 * 观察必须先看到原始事件。 */
                if (MouseButtonEvent is { } handler)
                {
                    if (handler(vk, down, injected))
                    {
                        if (Modules.HoldDrag.HookProbe.Enabled)
                            Modules.HoldDrag.HookProbe.W("HOST-MS 被改键吞掉 vk=" + vk + " down=" + down + " injected=" + injected);
                        return new IntPtr(1);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Error("鼠标钩子回调异常", ex);
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 鼠标消息 → (虚拟键, 是否按下)。不是按键消息返回 false。
    /// 侧键从 mouseData 的高位字取 XBUTTON1 / XBUTTON2。
    /// </summary>
    private static bool TryMouseButton(int msg, IntPtr lParam, out int vk, out bool down,
        out MSLLHOOKSTRUCT data)
    {
        vk = 0;
        down = false;
        data = default;

        switch (msg)
        {
            case WM_LBUTTONDOWN: vk = 0x01; down = true; break;
            case WM_LBUTTONUP: vk = 0x01; down = false; break;
            case WM_RBUTTONDOWN: vk = 0x02; down = true; break;
            case WM_RBUTTONUP: vk = 0x02; down = false; break;
            case WM_MBUTTONDOWN: vk = 0x04; down = true; break;
            case WM_MBUTTONUP: vk = 0x04; down = false; break;
            case WM_XBUTTONDOWN:
            case WM_XBUTTONUP:
                {
                    var x = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    int button = (int)((x.mouseData >> 16) & 0xFFFF);
                    vk = button == 2 ? 0x06 : 0x05;    // XBUTTON2 / XBUTTON1
                    down = msg == WM_XBUTTONDOWN;
                    data = x;
                    return true;
                }
            default:
                return false;
        }

        data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpmsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    public void Dispose()
    {
        Stop();
        _hook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
    }

    /// <summary>
    /// 让钩子线程在"下一次空闲"时把钩子重装一遍，全程没有空窗。
    ///
    /// 为什么需要：低级钩子被系统静默摘掉（回调超过 LowLevelHooksTimeout）
    /// 之后，SetWindowsHookEx 返回的句柄**仍然非零**，没有任何 API 能查出来。
    /// 唯一可靠的兜底就是定期重装 —— 否则一旦被摘，整个程序直到重启
    /// 都不再拦截任何按键，而界面上一切正常。
    /// </summary>
    private void StartWatchdog()
    {
        _watchdog ??= new System.Threading.Timer(_ =>
        {
            if (!_running || _threadId == 0) return;
            PostThreadMessage(_threadId, WM_APP_REARM, IntPtr.Zero, IntPtr.Zero);
        }, null, WatchdogFirstDelayMs, WatchdogIntervalMs);
    }

    /// <summary>
    /// 重装钩子：**先装新的，再卸旧的**。
    ///
    /// Stop() + Start() 的写法中间有 80ms 全放行窗口，若恰好落在切层瞬间，
    /// 就会表现为"层已经切好，Tab 还是漏出去了"。原子替换没有这个缺口。
    /// </summary>
    private void RearmHooks()
    {
        var fresh = _mouseOnly
            ? IntPtr.Zero
            : SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_mouseOnly) { /* 只装鼠标：键盘句柄留空 */ }
        else if (fresh != IntPtr.Zero)
        {
            var old = _hook;
            _hook = fresh;
            if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
        }
        else
        {
            LastErrorTime = DateTime.Now;
            CoreLog.Error($"键盘钩子重装失败，错误码 {Marshal.GetLastWin32Error()}");
        }

        /* 实验开关 CLP_NO_MOUSE_HOOK=1 时 Start 不装鼠标钩子；看门狗也必须认这个开关，
         * 否则 5 秒后的第一次重装就把它装回去了，排查"键盘钩子被摘"的实验结论被悄悄推翻。 */
        if (Environment.GetEnvironmentVariable("CLP_NO_MOUSE_HOOK") == "1") return;

        var freshMouse = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc!, GetModuleHandle(null), 0);
        if (freshMouse != IntPtr.Zero)
        {
            var oldMouse = _mouseHook;
            _mouseHook = freshMouse;
            if (oldMouse != IntPtr.Zero) UnhookWindowsHookEx(oldMouse);
        }
        else
        {
            LastErrorTime = DateTime.Now;
            CoreLog.Error($"鼠标钩子重装失败，错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>取桌面对像的名字（Default / Winlogon / …）。</summary>
    private static string DesktopName(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(空)";
        var sb = new System.Text.StringBuilder(256);
        return GetUserObjectInformation(h, 2 /*UOI_NAME*/, sb, sb.Capacity, out _) ? sb.ToString() : "(未知)";
    }

    /// <summary>
    /// 把当前线程切到**输入桌面**上（如果本来不是）。
    ///
    /// 必须在装钩子之前调用：钩子绑定的是线程当时的桌面，装完再切无效。
    /// 返回一段可直接写进日志的描述。
    /// </summary>
    private static string AlignToInputDesktop()
    {
        string mine = DesktopName(GetThreadDesktop(GetCurrentThreadId()));

        IntPtr input = OpenInputDesktop(0, false, 0x000F01FF /*DESKTOP_ALL*/);
        if (input == IntPtr.Zero)
            return $"钩子桌面={mine}；输入桌面打不开（错误码 {Marshal.GetLastWin32Error()}）";

        try
        {
            string theirs = DesktopName(input);
            if (string.Equals(mine, theirs, StringComparison.Ordinal))
                return $"钩子桌面={mine}（与输入桌面一致）";

            if (SetThreadDesktop(input))
                return $"钩子桌面={mine} → 已切到输入桌面={theirs}（原来不在同一桌面，钩子收不到按键）";

            return $"钩子桌面={mine} 输入桌面={theirs}，切换失败（错误码 {Marshal.GetLastWin32Error()}）";
        }
        finally { CloseDesktop(input); }
    }

    /// <summary>在调用线程上安装钩子（该线程必须自己泵消息，界面线程满足）。</summary>
    private void InstallOnCurrentThread()
    {
        _uiThreadMode = true;
        _threadId = GetCurrentThreadId();
        string desktopText = AlignToInputDesktop();
        var h = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        _hook = h;
        CoreLog.Info($"钩子装在当前线程 tid={_threadId} 安装结果=0x{h.ToInt64():X}；{desktopText}");
        if (h == IntPtr.Zero)
        {
            LastErrorTime = DateTime.Now;
            CoreLog.Error($"键盘钩子安装失败，错误码 {Marshal.GetLastWin32Error()}");
        }

        _mouseHook = Environment.GetEnvironmentVariable("CLP_NO_MOUSE_HOOK") == "1"
            ? IntPtr.Zero
            : SetWindowsHookEx(WH_MOUSE_LL, _mouseProc!, GetModuleHandle(null), 0);

        _running = true;
        StartWatchdog();
    }

    /// <summary>是否工作在"钩子装在调用线程"模式。</summary>
    private bool _uiThreadMode;

    /// <summary>卸载钩子（可再次 Start 重装，用于自愈）。</summary>
    private long _pingAcks;

    /// <summary>
    /// 钩子线程还活着吗（发一条消息过去，看它有没有处理）。
    ///
    /// 为什么需要：低级钩子**被系统摘掉**之后，句柄还在、`IsRunning` 仍是 true，
    /// 但从此刻起一个按键都收不到 —— 表现就是「宏录制没反应」「组合键突然全失灵」，
    /// 而所有状态显示都正常。线程卡死（回调里死等）也是同样的症状。
    /// 探活能把这三种情况分开：正常 / 线程卡死 / 线程已退出。
    /// </summary>
    public bool Ping(int timeoutMs = 400)
    {
        if (!IsRunning) return false;
        if (_uiThreadMode) return true;      // 界面线程在跑，钩子回调就有人处理
        if (_threadId == 0) return false;
        long before = Interlocked.Read(ref _pingAcks);
        if (!PostThreadMessage(_threadId, WM_APP_PING, IntPtr.Zero, IntPtr.Zero)) return false;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Interlocked.Read(ref _pingAcks) != before) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    public void Stop()
    {
        if (!_running) return;

        if (_uiThreadMode)
        {
            _running = false;
            _watchdog?.Dispose();
            _watchdog = null;
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
            _uiThreadMode = false;
            return;
        }

        _running = false;
        _watchdog?.Dispose();
        _watchdog = null;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);

        /* Join 必须等到线程真的退出。
         *
         * 只等一次 1.5 秒是不够的：回调正在跑（比如刚被我们自己的自检注入打断）时
         * 线程会晚一点退，这时如果直接 Start()，就会出现"两条钩子线程"，
         * 旧线程退出时把新钩子卸掉 —— 见线程体里的注释。
         * 这里最多等 3 秒，仍未退出就明确记一条，让日志能看出这次重装是"带病重装"。 */
        var thread = _thread;
        if (thread != null && thread.IsAlive)
        {
            if (!thread.Join(3000))
                CoreLog.Warn("钩子线程 3 秒内没有退出（回调可能被卡住），本次重装可能不彻底");
        }
        _thread = null;
        _threadId = 0;

        // 兜底：线程自己没来得及卸的句柄，这里补卸
        var h = _hook;
        var mh = _mouseHook;
        _hook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
        if (h != IntPtr.Zero) UnhookWindowsHookEx(h);
        if (mh != IntPtr.Zero) UnhookWindowsHookEx(mh);
    }

    /// <summary>重装钩子。</summary>
    public void Restart()
    {
        /* 线程还活着：**在它自己身上原地重装**（先装新的再卸旧的，没有空窗）。
         *
         * 以前这里是 Stop()+Start()：Stop 会等线程退出（最多 3 秒），等不到就作废，
         * 于是"重装"变成了"把钩子弄没"。原地重装没有这个问题。 */
        if (!_uiThreadMode && _running && _threadId != 0 && _thread is { IsAlive: true })
        {
            PostThreadMessage(_threadId, WM_APP_REARM, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        if (_uiThreadMode)
        {
            /* 已经在界面线程上：直接原地重装（先装新的再卸旧的，没有空窗）。 */
            RearmHooks();
            return;
        }

        /* 记住原来的模式再重来。
         *
         * `Start()` 有一句 `if (_running) return;`，而 `Stop()` 会把 `_running` 清掉；
         * 但 `mouseOnly` **不传回去** —— 一个 mouse-only 实例（手势/平滑滚动用的那条）
         * 线程死过一次之后就变成"键盘钩子实例"：键盘装上了、鼠标再也不装，
         * 于是手势和滚动整场失效，而 `IsRunning` 因为 new IsRunning 语义又永远报 false。
         * 这一句是 1.6.2 顺手带进来的真 bug，这里补掉。 */
        bool mouseOnly = _mouseOnly;
        Stop();
        Thread.Sleep(80);
        Start(mouseOnly);
    }

}
