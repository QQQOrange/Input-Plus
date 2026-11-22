using System;
using System.Collections.Generic;
using System.Threading;
using InputPlus.Core.Gestures;
using InputPlus.Core.Input;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;

namespace InputPlus.Core.Scroll;

/// <summary>
/// 平滑滚动（功能名与参数语义来自 SmoothScroll，算法按它的开源曲线重写）。
///
/// 工作方式：滚轮事件在鼠标钩子里被截下来，换算成"要滚多少像素"，
/// 再由一个 8ms 的定时器按缓动曲线分帧注入 <c>MOUSEEVENTF_WHEEL</c> 的小 delta。
/// 注入的事件带 <see cref="InputSender.InjectionTag"/>，钩子会认出是自己发的并放行，
/// 所以不会自己喂自己。
///
/// 三条铁律（按顺序判，任一不满足就原样放行）：
///   1. 手势先挑 —— 钩子已经把"按住起始键滚轮"交给手势服务了，走到这里说明没被吞；
///   2. 触发键上的滚轮绑定先挑 —— Caps+Alt+滚轮 这类组合键是用户的显式绑定；
///   3. 用户关了、或这个程序在例外表里被关掉，就一个字节都不改。
/// </summary>
public sealed class SmoothScrollService : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly object _gate = new();

    private KeyboardHook? _hook;
    private Timer? _timer;

    // ---- 一段动画的状态（全部在 _gate 里改）----
    private bool _running;
    private bool _horizontal;
    private double _target;
    private double _from;
    private double _sent;
    private long _startTick;
    private double _duration = 360;
    private double _scale = 4;

    // ---- 加速 ----
    private double _accel = 1;
    private long _lastWheelTick;

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

            /* 正在缓动的那一段立刻停手：暂停之后还在替用户滚屏幕很奇怪。 */
            lock (_gate)
            {
                _running = false;
                _target = 0;
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

        long now = Environment.TickCount64;
        if (o.Acceleration && now - _lastWheelTick <= Math.Max(1, o.AccelerationDelta))
            _accel = Math.Min(Math.Max(1, o.AccelerationMax), Math.Max(1, _accel) + 0.5);
        else
            _accel = 1;
        _lastWheelTick = now;

        double pixels = ScrollMath.PixelsFor(delta, step, o.Acceleration ? _accel : 1, o.Reverse);
        if (Math.Abs(pixels) < 0.01) return false;

        Enqueue(pixels, horizontal, duration, scale);

        /* 只记第一次：滚轮是高频事件，每次写日志会拖慢钩子（还可能被系统摘钩）。
         * 有这一行，"开了没效果"到底是没接管、还是注入了没反应，一眼能分清。 */
        if (Interlocked.Increment(ref _smoothCount) == 1)
            CoreLog.Info($"[平滑滚动] 首次接管滚轮：delta={delta} 目标={pixels:0}px 时长={duration:0}ms 加速度={_accel:0.##} 前台={app}");
        return true;
    }

    private void Enqueue(double pixels, bool horizontal, double duration, double scale)
    {
        lock (_gate)
        {
            if (_running && _horizontal != horizontal) FlushLocked();

            if (!_running)
            {
                _target = 0;
                _from = 0;
                _sent = 0;
            }

            _running = true;
            _horizontal = horizontal;
            _target += pixels;
            _from = _sent;
            _startTick = Environment.TickCount64;
            _duration = Math.Clamp(duration, 40, 2000);
            _scale = scale;
        }

        EnsureTimer();
    }

    private void EnsureTimer()
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => Tick(), null, 8, 8);
        }
    }

    private void Tick()
    {
        double send = 0;
        bool horizontal = false;

        lock (_gate)
        {
            if (!_running)
            {
                StopTimerLocked();
                return;
            }

            long elapsed = Environment.TickCount64 - _startTick;
            double t = elapsed / _duration;

            int delta;
            if (t >= 1)
            {
                delta = (int)Math.Round(_target - _sent);
                send = delta;
                horizontal = _horizontal;
                _sent = _target;
                _running = false;
                StopTimerLocked();
            }
            else
            {
                delta = ScrollMath.NextDelta(_from, _target, _sent, t, _scale);
                if (delta == 0) return;
                send = delta;
                horizontal = _horizontal;
                _sent += delta;
            }
        }

        try
        {
            if ((int)send != 0) Send((int)send, horizontal);
            Interlocked.Add(ref _sentPixels, Math.Abs((long)(int)send));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "注入平滑滚动"); }
    }

    /// <summary>把当前这段动画剩下的像素一次发完（换轴、关开关、退出时用）。</summary>
    private void FlushLocked()
    {
        if (!_running) return;
        int rest = (int)Math.Round(_target - _sent);
        bool horizontal = _horizontal;

        _sent = _target;
        _running = false;
        StopTimerLocked();

        if (rest == 0) return;
        try { Send(rest, horizontal); }
        catch (Exception ex) { CoreLog.Swallow(ex, "补发平滑滚动余量"); }
    }

    private static void Send(int delta, bool horizontal)
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

    private static long _injectionFailures;
    public long InjectionFailures => Interlocked.Read(ref _injectionFailures);

    private void StopTimerLocked()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // ---------------- 纯函数（自检直接对着跑）----------------

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
