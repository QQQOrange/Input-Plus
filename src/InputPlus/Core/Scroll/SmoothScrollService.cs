using System;
using System.Collections.Generic;
using System.Threading;
using InputPlus.Core.Gestures;
using InputPlus.Core.Input;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;

namespace InputPlus.Core.Scroll;

/// <summary>
/// 平滑滚动（功能名与参数语义来自 SmoothScroll，动画模型按它的原生引擎重写）。
///
/// 工作方式：滚轮事件在鼠标钩子里被截下来，换算成"要滚多少像素"，然后
/// **每一格各开一段动画**，由一个自校正的帧定时器按缓动曲线分帧注入
/// <c>MOUSEEVENTF_WHEEL</c> 的小 delta。
/// 注入的事件带 <see cref="InputSender.InjectionTag"/>，钩子会认出是自己发的并放行，
/// 所以不会自己喂自己。
///
/// 三条铁律（按顺序判，任一不满足就原样放行）：
///   1. 手势先挑 —— 钩子已经把"按住起始键滚轮"交给手势服务了，走到这里说明没被吞；
///   2. 触发键上的滚轮绑定先挑 —— Caps+Alt+滚轮 这类组合键是用户的显式绑定；
///   3. 用户关了、或这个程序在例外表里被关掉，就一个字节都不改。
///
/// 为什么是"多段动画"而不是"一条动画随时改目标"：
/// 后者每来一格就要把时间线重置一次（曲线回到起点、速度回到 0），连滚时表现是
/// "每一格都重新起步"；前者是各段并行、每帧求和，连滚时速度是叠上去的。
/// 这是"不如原版丝滑"最主要的一条，详见 <see cref="ScrollPulse"/>。
/// </summary>
public sealed class SmoothScrollService : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly object _gate = new();

    /// <summary>注入串行化：帧线程与钩子线程（换轴补发）都可能发，别让两条 delta 交错。</summary>
    private static readonly object SendGate = new();

    private KeyboardHook? _hook;
    private Timer? _timer;

    /// <summary>
    /// 帧间隔（毫秒）。原版是按显示器刷新率对齐 vblank 走的（60Hz ≈ 16.7ms，
    /// 拿不到 vblank 时退回 15ms 一轮）；这里固定 8ms：注入的滚轮事件不会自己
    /// 产生画面，多出来的帧只是把同一段位移切得更细，在 120/144Hz 屏上反而更跟手，
    /// 真正影响手感的是**帧间距要均匀**，这一点靠下面 <see cref="_nextFrameTick"/>
    /// 的绝对节拍保证。
    /// </summary>
    private const int FrameIntervalMs = 8;

    // ---- 正在跑的动画（全部在 _gate 里改）----
    private readonly List<ScrollPulse> _pulses = new();
    private bool _horizontal;
    private double _scale = 4;
    private long _nextFrameTick;

    // ---- 加速 ----
    private long _lastWheelTick;

    /// <summary>
    /// 不足一像素的零头，攒到下一格。只在钩子线程上改（<see cref="OnWheel"/> 是唯一写点）。
    /// 恒在 ±0.5 之间，所以不会出现"多滚一格"这种可见副作用。
    /// </summary>
    private double _pixelCarry;

    private long _smoothCount;
    private long _sentPixels;

    /* ---- 诊断计数：为什么没平滑 ----
     * 滚轮是整机最高频的事件之一，不能每次都写日志（会拖垮钩子、还可能被系统摘钩）。
     * 这里只累加计数，由 --scroll-probe / 设置页状态行按需读取。 */
    private long _rejectedInjected;
    private long _rejectedDisabled;
    private long _rejectedBinding;
    private long _rejectedRule;
    private long _rejectedTouchpad;

    /// <summary>诊断：被判定为"本程序自己注入"而放行的次数。</summary>
    public long RejectedInjected => Interlocked.Read(ref _rejectedInjected);
    public long RejectedDisabled => Interlocked.Read(ref _rejectedDisabled);
    public long RejectedBinding => Interlocked.Read(ref _rejectedBinding);
    public long RejectedRule => Interlocked.Read(ref _rejectedRule);
    public long RejectedTouchpad => Interlocked.Read(ref _rejectedTouchpad);

    public SmoothScrollService(Func<AppSettings> settings) => _settings = settings;

    /// <summary>
    /// 问一句"这个方向的滚轮是不是被用户的触发键绑定占着"。
    /// 命中就不平滑 —— 否则注入事件带着标记回来，绑定再也收不到真滚轮，
    /// 表现是"开了平滑滚动之后 Caps+Alt+滚轮 失灵"。
    /// </summary>
    public Func<int, bool>? WheelBindingHeld { get; set; }

    public bool Installed => _hook != null;

    /// <summary>
    /// 全局暂停（标题栏的「暂停」按钮）。
    ///
    /// 和"关掉平滑滚动"不是一回事：暂停不改用户的设置，只是因为整个软件被按了
    /// 暂停，滚轮就该原样交给系统。恢复时这面旗子落回去，设置照旧生效。
    /// </summary>
    private volatile bool _suspended;
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            if (!value) return;

            /* 正在缓动的那几段立刻停手：暂停之后还在替用户滚屏幕很奇怪。
             * 和"关开关"不同，这里**不补发余量** —— 用户按暂停就是要立刻停住。 */
            lock (_gate)
            {
                _pulses.Clear();
                StopTimerLocked();
            }
        }
    }

    /// <summary>
    /// 仅供自检：替换真正的注入口。
    /// 否则一跑自检就会真的滚一下桌面 —— 那种"测试通过但桌面被滚跑了"的行为
    /// 和 InputSender.SenderForTest 是同一个道理。
    /// </summary>
    internal static Action<int, bool>? SendForTest { get; set; }

    /// <summary>已接管的滚轮事件数（界面状态行用）。</summary>
    public long SmoothCount => Interlocked.Read(ref _smoothCount);

    /// <summary>已经注入出去的像素总量（绝对值，自检与调试用）。</summary>
    public long SentPixels => Interlocked.Read(ref _sentPixels);

    public void Attach(KeyboardHook? hook)
    {
        if (hook == null || ReferenceEquals(_hook, hook)) return;
        _hook = hook;
        hook.ScrollWheelEvent -= OnWheel;
        hook.ScrollWheelEvent += OnWheel;
    }

    /// <summary>设置变更后调用：关掉开关时把没滚完的余量补完再停。</summary>
    public void ApplySettings()
    {
        if (_settings().Scroll?.Enabled != false) return;
        lock (_gate) FlushLocked();
    }

    /// <summary>
    /// 钩子入口。返回 true = 这次滚轮已经被接管，钩子要吞掉原事件。
    /// </summary>
    public bool OnWheel(int delta, bool injected, bool horizontal)
    {
        if (injected) { Interlocked.Increment(ref _rejectedInjected); return false; }
        if (delta == 0) return false;

        AppSettings settings;
        try { settings = _settings(); } catch { return false; }
        if (Suspended) return false;

        var o = settings.Scroll ?? new ScrollOptions();
        if (!o.Enabled) { Interlocked.Increment(ref _rejectedDisabled); return false; }

        if (WheelBindingHeld is { } bound && bound(delta > 0 ? 1 : -1))
        {
            Interlocked.Increment(ref _rejectedBinding);
            return false;
        }

        string app = ForegroundApp();
        var rule = FindRule(settings.ScrollApps, app);
        if (rule is { Enabled: false }) { Interlocked.Increment(ref _rejectedRule); return false; }

        if (!horizontal)
        {
            /* 触摸板/精密滚轮：delta 不是 120 的整数倍，事件又密又碎。
             * 交给系统原样处理，手感本来就连续。 */
            if (o.IgnoreTouchpad && Math.Abs(delta) < 120) { Interlocked.Increment(ref _rejectedTouchpad); return false; }

            /* Shift+滚轮 = 横向。Windows 的惯例是"上滚 = 往左"，
             * 所以换算成横向时要取反，否则方向感是反的。 */
            if (o.HorizontalShift && o.Horizontal && InputSender.IsPhysicallyDown(VK.Shift))
            {
                horizontal = true;
                delta = -delta;
            }
        }

        if (horizontal && !o.Horizontal) return false;

        double step = rule?.StepSize ?? o.StepSize;
        double duration = rule?.AnimationTime ?? o.AnimationTime;
        double scale = o.PulseScale;

        /* 加速：按"两次滚轮隔了多久"算一个连续倍数，不记忆状态。
         * 详情见 ScrollMath.AccelerationFactor —— 原版就是这么算的，
         * 阶跃式的"每快一格 +0.5 倍"在连滚松手时会突然窜一下。 */
        long now = ScrollClock.NowMs;
        double gap = now - _lastWheelTick;
        _lastWheelTick = now;
        double accel = o.Acceleration ? ScrollMath.AccelerationFactor(gap, o.AccelerationDelta, o.AccelerationMax) : 1;

        double pixels = ScrollMath.PixelsFor(delta, step, accel, o.Reverse);
        if (Math.Abs(pixels) < 0.01) return false;

        /* 换算出来的像素不一定是整数：步长允许写成 137.5（界面是 double.TryParse），
         * 加速倍数更是小数。每格都四舍五入的话，误差会朝同一个方向累积
         * （连滚一百格能差出去几十像素），这里把不足一像素的零头攒到下一格。 */
        int whole = TakeWholePixels(pixels, ref _pixelCarry);
        if (whole != 0) Enqueue(whole, horizontal, duration, scale);

        /* 只记第一次：滚轮是高频事件，每次写日志会拖慢钩子（还可能被系统摘钩）。
         * 有这一行，"开了没效果"到底是没接管、还是注入了没反应，一眼能分清。 */
        if (Interlocked.Increment(ref _smoothCount) == 1)
            CoreLog.Info($"[平滑滚动] 首次接管滚轮：delta={delta} 目标={whole}px 时长={duration:0}ms 加速度={accel:0.##} 前台={app}");
        return true;
    }

    /// <summary>把这一格排进动画队列：新开一段，不碰已经在跑的其它段。</summary>
    private void Enqueue(double pixels, bool horizontal, double duration, double scale)
    {
        lock (_gate)
        {
            /* 换轴必须先把另一条轴的余量发完：一次注入只能带一个方向的 delta，
             * 混在一起会把横向位移当纵向发出去。（原版这里是直接把新事件的
             * dwFlags 套到所有段上，横向纵向会串 —— 这点不跟它保持一致。） */
            if (_pulses.Count > 0 && _horizontal != horizontal) FlushLocked();

            _horizontal = horizontal;
            _scale = scale;

            long now = ScrollClock.NowMs;
            ScrollPulses.Add(_pulses, pixels, now, Math.Clamp(duration, 40, 2000));
            StartTimerLocked(now);
        }
    }

    /// <summary>没有定时器就起一个；已经在跑就让它继续按自己的节拍走。</summary>
    private void StartTimerLocked(long now)
    {
        if (_timer != null) return;
        _nextFrameTick = now + FrameIntervalMs;
        ScrollClock.RaiseResolution();
        /* 一次性定时器，每帧自己排下一帧（见 Tick）。
         *
         * 迟到的旧回调不会踩坏新的定时器：Enqueue 永远是"先加一段、再建定时器"，
         * 所以"队列空 + 定时器非空"这个组合不会出现 —— 迟到的回调看到队列空时
         * 那个定时器只可能是它自己那一代，Dispose 掉是安全的；看到队列非空则只会
         * 给当前定时器多排一帧，而位移是按绝对时间算的，多一帧也不会算错。 */
        _timer = new Timer(static state => ((SmoothScrollService)state!).Tick(), this,
            FrameIntervalMs, Timeout.Infinite);
    }

    private void Tick()
    {
        int delta = 0;
        bool horizontal = false;

        lock (_gate)
        {
            if (_pulses.Count == 0)
            {
                StopTimerLocked();
                return;
            }

            delta = ScrollPulses.Step(_pulses, ScrollClock.NowMs, _scale);
            horizontal = _horizontal;

            if (_pulses.Count == 0)
            {
                /* 全部走完：停表、把系统定时器精度还回去。
                 * 一直提着 1ms 精度会让整机每秒多醒 1000 次，笔记本上很费电。 */
                StopTimerLocked();
            }
            else
            {
                /* 节拍自校正：下一帧排在上一个目标时刻 + 8ms，而不是"从现在起 8ms"。
                 * 后者会把每次回调的延迟一路累加（定时器只会晚不会早），
                 * 表现是整体越来越慢、帧间距忽长忽短 —— 这正是"看着在动但就是不丝滑"。
                 * 落后超过一整帧就别补了（连发只会让画面更抖），重新对齐。 */
                long now = ScrollClock.NowMs;
                _nextFrameTick += FrameIntervalMs;
                if (_nextFrameTick <= now) _nextFrameTick = now + FrameIntervalMs;
                int delay = (int)Math.Max(1, _nextFrameTick - now);
                _timer?.Change(delay, Timeout.Infinite);
            }
        }

        if (delta == 0) return;

        try
        {
            Send(delta, horizontal);
            Interlocked.Add(ref _sentPixels, Math.Abs((long)delta));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "注入平滑滚动"); }
    }

    /// <summary>把当前所有段剩下的像素一次发完（换轴、关开关、退出时用）。</summary>
    private void FlushLocked()
    {
        if (_pulses.Count == 0) { StopTimerLocked(); return; }

        int rest = ScrollPulses.Rest(_pulses);
        bool horizontal = _horizontal;
        _pulses.Clear();
        StopTimerLocked();

        if (rest == 0) return;
        try { Send(rest, horizontal); }
        catch (Exception ex) { CoreLog.Swallow(ex, "补发平滑滚动余量"); }
    }

    private static void Send(int delta, bool horizontal)
    {
        if (delta == 0) return;

        /* 串行化注入：帧线程和钩子线程都可能进来（换轴补发），
         * 两条 delta 交错会让"先横后竖"变成"先竖后横"。 */
        lock (SendGate)
        {
            if (SendForTest is { } spy) { spy(delta, horizontal); return; }
            if (!InputSender.WheelDelta(delta, horizontal))
            {
                /* SendInput 失败最常见的原因是前台窗口权限更高（UIPI）。
                 * 只记第一次：否则每个动画帧都会刷一行，日志会炸。 */
                if (Interlocked.Increment(ref _injectionFailures) == 1)
                    CoreLog.Warn("[平滑滚动] 注入滚轮失败（前台程序权限更高时会被系统拦截），后续同类失败不再记录");
            }
        }
    }

    private static long _injectionFailures;
    public long InjectionFailures => Interlocked.Read(ref _injectionFailures);

    private void StopTimerLocked()
    {
        _timer?.Dispose();
        _timer = null;
        ScrollClock.DropResolution();
    }

    // ---------------- 纯函数（自检直接对着跑）----------------

    /// <summary>
    /// 取整数像素，把小数零头留在 <paramref name="carry"/> 里带到下一格。
    ///
    /// 为什么不能直接 <c>Math.Round</c> 掉：一格 137.5 像素，四舍五入每次都往上偏
    /// 0.5，十格就差 5 像素、一百格差 50 像素 —— 用户看到的是"滚着滚着比原生滚得远"。
    /// 用 <c>AwayFromZero</c> 而不是默认的银行家舍入，是为了 0.5 永远朝远离零的方向进位，
    /// 正反两个方向对称（银行家舍入在正方向会一半舍一半进，手感上像抽帧）。
    /// </summary>
    internal static int TakeWholePixels(double pixels, ref double carry)
    {
        double want = pixels + carry;
        double whole = Math.Round(want, MidpointRounding.AwayFromZero);
        carry = want - whole;
        return (int)whole;
    }

    /// <summary>这个前台程序有没有专门的设置（没有就跟随全局）。</summary>
    internal static ScrollAppRule? FindRule(List<ScrollAppRule>? rules, string app)
    {
        if (rules == null || rules.Count == 0 || string.IsNullOrWhiteSpace(app)) return null;
        foreach (var rule in rules)
        {
            if (rule == null) continue;
            if (GestureTriggerMap.SameApp(rule.AppPath, app)) return rule;
        }
        return null;
    }

    private static string ForegroundApp()
    {
        try { return ForegroundWatcher.ProcessNameOf(ForegroundWatcher.ForegroundWindow()); }
        catch { return ""; }
    }

    public void Dispose()
    {
        lock (_gate) FlushLocked();
        if (_hook != null)
        {
            _hook.ScrollWheelEvent -= OnWheel;
            _hook = null;
        }
    }
}
