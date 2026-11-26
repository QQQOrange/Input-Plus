using System;
using System.Collections.Generic;
using System.Threading;
using InputPlus.Core.Gestures;
using InputPlus.Core.Input;
using InputPlus.Core.Interop;
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
    /// 帧间隔（毫秒）。照上游：原版是**vblank 对齐**（`D3DKMTWaitForVerticalBlankEvent` + `Sleep(2)`），
    /// 拿不到 vblank 时退到 `Sleep(15)`。这里取 16ms（≈60Hz）而不是以前的 8ms：
    ///
    /// 同一段 360ms 的动画，8ms 要切 45 帧、每帧位移只有 2~5 单位，而 Windows 的经典控件
    /// （EDIT / ListBox / 老 listview）是 `delta / 120` 的**整数除法**，小于 120 的全丢 ——
    /// 帧切得越细，丢得越干净，用户看到的就是"滚轮没反应"。16ms 把每帧位移做大一倍，
    /// 再叠加 OnWheel 里的"经典控件直接放行"，才不会出现"平滑了但滚不动"。
    /// </summary>
    private const int FrameIntervalMs = 16;

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

    /// <summary>因为"注不进去"而放行的格数（见 OnWheel 里 InjectionBlocked 那一段）。</summary>
    private long _rejectedBlocked;

    /// <summary>注入被挡而放行的次数。它一涨就说明"滚了不动"不是设置问题，是注入被拦了。</summary>
    public long RejectedBlocked => Interlocked.Read(ref _rejectedBlocked);

    /* ── 回声校验（见 EchoLooksDead / NoteSwallow）──────────────────
     * 钩子是唯一能证明"注进去的滚轮真的到了"的证人，所以让它计数、这里比对。
     *
     * 和上一版最大的差别：**没有 static、没有时间窗、没有递增退避**。
     * 上游（SmoothScroll）里根本不存在"停接管 30 秒"这种状态 —— 它只在
     * 这一格判断"能不能做"，做不了就原样放行。上一版把状态做成 static + 30s→5min，
     * 结果一次误判（驱动清掉 dwExtraInfo、远程桌面、宿主通道丢回响）就让平滑
     * 消失好几分钟，探针和多实例还互相污染。现在只记"接下来 N 格不接管"，
     * 用格数而不是秒数表达，事件一恢复就自然回正。 */
    internal Func<long>? InjectedEcho { get; set; }

    private long _lastEcho;      // 上次看到的回响累计数
    private long _pendingSince;  // 开始等回响的时刻（TickCount64）；0 = 没在等
    private long _swallowWithoutEcho;

    /// <summary>吞了却等不到回响的次数（诊断用）。</summary>
    public long SwallowWithoutEcho => Interlocked.Read(ref _swallowWithoutEcho);

    /// <summary>接下来还有几格不接管（回声判死后的冷却，按格数计）。</summary>
    private int _passNotches;

    /// <summary>
    /// 多久看不到注入回响就判定"注了也不响"。150ms 是**故意压短**的：
    /// 健康链路第一帧就在 8~16ms 内进队列，150ms 已经是十帧左右；
    /// 而连滚时两格之间通常 60~120ms，压到 150 才能在**同一次连滚里**就放行。
    /// </summary>
    private const int EchoDeadlineMs = 150;

    /// <summary>判死之后放行几格再重新试探。3 格 ≈ 一次连滚的长度，够短不难受。</summary>
    private const int EchoCoolOffNotches = 3;

    /* ── 目标窗口解析（按 HWND 缓存，照上游 FUN_140069ba0） ──────────
     * 上游不是查前台窗口，而是 `GetCursorPos + WindowFromPoint`：Win10 起默认
     * "悬停滚动非活动窗口"，滚轮去的是**指针下**那个窗口，不是前台那个。
     * 解析结果按 HWND 缓存，句柄不变就不重新 OpenProcess。 */
    private static IntPtr _targetHwnd;
    private static string _targetClass = "";
    private static string _targetApp = "";

    /* ── 规则快照 ─────────────────────────────────────────────
     * 以前每格 `rules.ToArray()` —— 钩子回调里每格一次分配，GC 一抖就顶到摘钩线。
     * 现在只在设置变更时重建一次（见 ApplySettings）。 */
    private ScrollAppRule?[] _ruleSnapshot = Array.Empty<ScrollAppRule>();

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

    /// <summary>设置变更后调用：重建例外表快照；关掉开关时把没滚完的余量补完再停。</summary>
    public void ApplySettings()
    {
        ApplySettingsCore();
        if (_settings().Scroll?.Enabled != false) return;
        lock (_gate) FlushLocked();
    }

    /// <summary>
    /// 钩子入口。返回 true = 这次滚轮已经被接管，钩子要吞掉原事件。
    /// </summary>
    public bool OnWheel(int delta, bool injected, bool horizontal)
    {
        bool Pass(string why)
        {
            Trace(why, delta);
            return false;
        }

        if (injected) { Interlocked.Increment(ref _rejectedInjected); return Pass("自己注入的，放行"); }
        if (delta == 0) return false;

        AppSettings settings;
        try { settings = _settings(); } catch { return Pass("读设置失败"); }
        if (Suspended) return Pass("全局暂停");

        var o = settings.Scroll ?? new ScrollOptions();
        if (!o.Enabled) { Interlocked.Increment(ref _rejectedDisabled); return Pass("平滑滚动关着"); }

        /* 注入被挡住时**绝不能吞**这一格。
         *
         * 前台是更高完整性窗口（管理员程序 / UAC 界面）时 `SendInput` 直接返回 0，
         * 而 `InputSender` 会进入熔断（15s 起、逐次加长到 300s）。以前这里不看那个状态，
         * 照样 `return true` 把物理滚轮吃掉、却一个像素都注不进去 ——
         * 用户看到的是"滚轮完全不动"，而界面上开关还是"已开启"。
         * 宁可这一格不平滑，也要让它按原样滚。 */
        if (InputSender.InjectionBlocked) { Interlocked.Increment(ref _rejectedBlocked); return Pass("注入熔断中"); }

        /* 试运行 / 启动静默期：Send 会"假装成功"但一个事件都不发。
         * 这时候吞掉就是纯丢格 —— 滚轮不动，而且没有任何一处记账。 */
        if (InputSender.SendingMuted) { Interlocked.Increment(ref _rejectedBlocked); return Pass("启动静默期"); }

        /* 回声判死之后的冷却：按**格数**放行，不按秒（见字段区注释）。 */
        if (_passNotches > 0)
        {
            _passNotches--;
            Interlocked.Increment(ref _rejectedBlocked);
            return Pass("回声冷却中");
        }

        /* 上一格吞下去之后一直没看到注入回响 → 这一格不吞，并冷却几格。
         * 放在所有"要不要接管"的判断之前：吞了不滚比不平滑严重得多。 */
        if (EchoLooksDead())
        {
            _passNotches = EchoCoolOffNotches;
            Interlocked.Increment(ref _rejectedBlocked);
            return Pass("上一格没回响");
        }

        if (WheelBindingHeld is { } bound && bound(delta > 0 ? 1 : -1))
        {
            Interlocked.Increment(ref _rejectedBinding);
            return Pass("触发键绑定占着");
        }

        /* 目标窗口取**指针下**那个（上游 GetCursorPos + WindowFromPoint）。
         * Win10 起默认"悬停滚动非活动窗口"，滚轮是给指针下那个窗口的，
         * 不是给前台那个 —— 以前按前台判规则，双窗口下会套错程序例外。 */
        ResolveTarget();
        string app = _targetApp;

        /* ① 上游那条硬闸：`%System32%` 下的程序一律**不排队、不吞、直接放行**
         *    （伪代码 FUN_14005bc60 里整段排队代码都在 `if 路径.find(":\\Windows\\System32\\") == npos`
         *    的内层）。记事本、控制台这些全是经典控件，注入 <120 单位会被整数除法丢掉 ——
         *    用户看到的就是"滚轮彻底不动"。 */
        if (IsSystemProgram(app)) { Interlocked.Increment(ref _rejectedRule); return Pass("System32 程序：" + app); }

        /* ② 经典控件的判据（上游用 System32 排除间接覆盖；第三方程序里的
         *    EDIT/ListBox 同样吃 `delta/120` 的整数除法，一并放行）。 */
        if (IsClassicScrollControl(_targetClass)) { Interlocked.Increment(ref _rejectedRule); return Pass("经典控件：" + _targetClass); }

        var rule = FindRuleCached(settings.ScrollApps, app);
        if (rule is { Enabled: false })
        {
            Interlocked.Increment(ref _rejectedRule);
            /* 带上命中的规则本身：用户报"禁了还在平滑"时，日志要能直接看出
             * 命中的是哪一条、目标程序是哪个 —— 否则只能靠猜（见 SameAppOrFamily）。 */
            return Pass("程序例外禁用：" + app + "（规则 " + rule.AppPath + "）");
        }

        /* ③ Ctrl / Alt + 滚轮 = 缩放/切层，上游直接放行。
         *    我们以前没有这条，浏览器里 Ctrl+滚轮 被接走后重新注入，缩放变得一顿一顿。 */
        if (!horizontal && (IsDown(VK.Control) || IsDown(VK.Menu)))
        {
            Interlocked.Increment(ref _rejectedBinding);
            return Pass("Ctrl/Alt 按住");
        }

        if (!horizontal)
        {
            /* 触摸板闸：照上游的**单格**口径，不再用 <120 一把抓 ——
             *   · |delta| < 50(0x32) → 放行（真正的触摸板细碎报点）；
             *   · 只认"一格 120 / 100 的整数倍"，60、30 这种半格 delta 也放行。
             * 上一版写的是 `< 120`（注释还写着 40），于是 100/60 一格的鼠标
             * （远程桌面、虚拟机、部分驱动）整只被当触摸板放走 —— 平滑直接"没效果"。 */
            if (o.IgnoreTouchpad && !LooksLikeNotchDelta(delta))
            {
                Interlocked.Increment(ref _rejectedTouchpad);
                return Pass($"delta={delta} 不像滚一格");
            }

            /* Shift+滚轮 = 横向。Windows 的惯例是"上滚 = 往左"，
             * 所以换算成横向时要取反，否则方向感是反的。 */
            if (o.HorizontalShift && o.Horizontal && IsDown(VK.Shift))
            {
                horizontal = true;
                delta = -delta;
            }
        }

        if (horizontal && !o.Horizontal) return Pass("横向滚轮关着");

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
        if (Math.Abs(pixels) < 0.01) return Pass("算出来是 0");

        /* 换算出来的像素不一定是整数：步长允许写成 137.5（界面是 double.TryParse），
         * 加速倍数更是小数。每格都四舍五入的话，误差会朝同一个方向累积
         * （连滚一百格能差出去几十像素），这里把不足一像素的零头攒到下一格。 */
        int whole = TakeWholePixels(pixels, ref _pixelCarry);
        if (whole != 0)
        {
            Enqueue(whole, horizontal, duration, scale);
        }
        else
        {
            /* 这一格凑不出整像素（步长很小 / 加速倍数是小数）：**别吞**。
             * 吞掉却不排动画 = 用户滚了一格、屏幕上什么都没动；连滚时这种格子
             * 一多，表现就是"滚轮失效"。队列里还有没跑完的段才算安全 ——
             * 那一格会在下一帧补上。 */
            lock (_gate)
            {
                if (_pulses.Count == 0)
                {
                    Interlocked.Increment(ref _rejectedBlocked);
                    return Pass("这一格凑不出整像素");
                }
            }
        }

        /* 只记第一次：滚轮是高频事件，每次写日志会拖慢钩子（还可能被系统摘钩）。
         * 有这一行，"开了没效果"到底是没接管、还是注入了没反应，一眼能分清。 */
        if (Interlocked.Increment(ref _smoothCount) == 1)
            CoreLog.Info($"[平滑滚动] 首次接管滚轮：delta={delta} 目标={whole}px 时长={duration:0}ms 加速度={accel:0.##} 前台={app}");

        NoteSwallow();
        return true;
    }

    /// <summary>
    /// 吞掉一格之前/之后各问一次的问题：**自己注进去的滚轮有没有回到钩子上**。
    ///
    /// 为什么要有这一条（用户 2026-11-24 报"滚轮失效"）：前面那几条 fail-open
    /// 都只看 `SendInput` 的返回值，可真正要命的是"返回成功、事件却没到"的那一类：
    ///   · 管理员模式把事件下沉给钩子宿主，宿主没接住（TryForwardToRelay 只保证送出去）；
    ///   · 各种过滤驱动把注入吃掉，条数照旧；
    ///   · 钩子宿主→主进程那条通道是**可丢**的，滚轮事件丢了主进程连"要不要接管"都不知道。
    /// 这些情形下每一格都被吞掉、每一格都没反应，而且不会自愈 —— 滚轮就此"死了"。
    ///
    /// 钩子是整个会话里唯一看得见"输入队列里到底有没有东西"的地方，所以让它当证人。
    /// <see cref="EchoDeadlineMs"/> 内没有回响就退化成原生滚动。
    ///
    /// 判定必须放在**吞之前**（<see cref="EchoLooksDead"/>），不能在吞之后：
    /// 用户滚两格就停手时，"吞完再判"永远轮不到 —— 而他看到的是滚轮一点反应都没有。
    /// 吞之后的 <see cref="NoteSwallow"/> 只负责记下"该看到回响了"。
    /// </summary>
    private bool EchoLooksDead()
    {
        Func<long>? echo = InjectedEcho;
        if (echo == null) return false;

        long since = Volatile.Read(ref _pendingSince);
        if (since == 0) return false;
        if (Environment.TickCount64 - since < EchoDeadlineMs) return false;

        long seen;
        try { seen = echo(); } catch { return false; }
        if (seen < 0) return false;

        if (seen != Interlocked.Read(ref _lastEcho))
        {
            /* 迟到的回响总比没有好：链路是活的，清干净继续接管。 */
            Interlocked.Exchange(ref _lastEcho, seen);
            Volatile.Write(ref _pendingSince, 0);
            return false;
        }

        /* 判死：这一格不吞，并冷却若干**格**（不是若干秒）。
         * 上游连这个状态都没有，只在"这一格能不能做"上判断；我们保留一条**有界**的
         * 冷却，是因为注入侧还可能被 UIPI / 下沉通道静默吃掉 —— 但不能像上一版那样
         * 一停就是 30 秒到 5 分钟，那等于把"平滑"整个关掉。 */
        Volatile.Write(ref _pendingSince, 0);
        Interlocked.Increment(ref _swallowWithoutEcho);
        return true;
    }

    /// <summary>吞下一格之后记一笔：从现在开始等回响（<see cref="EchoLooksDead"/> 会看它）。</summary>
    private void NoteSwallow()
    {
        Func<long>? echo = InjectedEcho;
        if (echo == null) return;

        long seen;
        try { seen = echo(); } catch { return; }
        if (seen < 0) return;

        if (seen != Interlocked.Read(ref _lastEcho))
        {
            /* 有回响：注入链路是活的。 */
            Interlocked.Exchange(ref _lastEcho, seen);
            Volatile.Write(ref _pendingSince, 0);
            _passNotches = 0;
        }
        else if (Volatile.Read(ref _pendingSince) == 0)
        {
            Volatile.Write(ref _pendingSince, Environment.TickCount64);
        }
    }

    /* ── 目标窗口与四道闸（照上游） ────────────────────────────── */

    /// <summary>
    /// 解析**指针下**那个窗口的类名与所属程序，按 HWND 缓存。
    /// 上游 FUN_140069ba0 就是这么做的：HWND 不变不重新 OpenProcess。
    /// </summary>
    private void ResolveTarget()
    {
        /* 自检必须能给定"目标窗口"，否则用例结果取决于跑自检时鼠标停在哪 ——
         * 停在记事本上就会被 System32 闸放行，用例随机红。 */
        var spy = TargetForTest?.Invoke() ?? DefaultTargetForTest;
        if (spy is { } target)
        {
            _targetClass = target.Class;
            _targetApp = target.App;
            _targetHwnd = new IntPtr(1);
            return;
        }

        IntPtr hwnd = IntPtr.Zero;
        try
        {
            if (NativeMethods.GetCursorPos(out var pt)) hwnd = NativeMethods.WindowFromPoint(pt);
        }
        catch { /* 拿不到就按"没有目标"处理，后面所有闸都会放行 */ }

        if (hwnd == _targetHwnd && _targetClass.Length != 0) return;

        _targetHwnd = hwnd;
        _targetClass = ClassOf(hwnd);

        /* 程序路径要从**根窗口**取：WindowFromPoint 给的是最深的子窗口，
         * 子窗口的 pid 与根窗口一致，但 GetAncestor 更稳（有的控件是跨进程的）。 */
        string app = "";
        try
        {
            IntPtr root = NativeMethods.GetAncestor(hwnd, 2 /* GA_ROOT */);
            if (root == IntPtr.Zero) root = hwnd;
            uint pid = NativeMethods.GetWindowThreadProcessId(root, out _);
            app = WindowService.GetProcessPath(pid);
        }
        catch { }
        _targetApp = app;
    }

    /// <summary>仅供自检：给定目标窗口的类名/程序路径，跳过真实 HWND 解析。</summary>
    internal Func<(string Class, string App)>? TargetForTest { get; set; }

    /// <summary>仅供自检：全局默认的目标窗口（用例里每处 new 都要配一遍太啰嗦）。</summary>
    internal static (string Class, string App)? DefaultTargetForTest { get; set; }

    /// <summary>仅供自检：替代 InputSender.IsPhysicallyDown（否则按住 Ctrl 跑自检就全红）。</summary>
    internal Func<int, bool>? IsKeyDownForTest { get; set; }

    /// <summary>仅供自检：同上，全局默认。</summary>
    internal static Func<int, bool>? DefaultIsKeyDownForTest { get; set; }

    private bool IsDown(int vk)
        => IsKeyDownForTest is { } t ? t(vk)
         : DefaultIsKeyDownForTest is { } d ? d(vk)
         /* 用钩子记的**物理**状态，不用 GetAsyncKeyState：别的程序注射的 Ctrl/Alt
          * 会被后者算成"按着"（现场就有一个 QSELF 工具在狂发 Ctrl+C），
          * 那样"Ctrl+滚轮放行"就会时真时假 = "滚轮有时候失效"。 */
         : _hook is { } h ? h.IsPhysicalModifierDown(vk)
         : InputSender.IsPhysicallyDown(vk);

    private static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        try
        {
            var sb = new System.Text.StringBuilder(64);
            int n = NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return n > 0 ? sb.ToString(0, n) : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 上游那条硬闸：`%System32%` 下的程序一律不平滑。
    /// 它们是经典控件（记事本 / 控制台 / 资源管理器的一部分面板），注入的
    /// &lt;120 单位会被 `delta / 120` 的整数除法丢掉 —— 那正是"滚轮失效"。
    /// </summary>
    internal static bool IsSystemProgram(string? appPath)
    {
        if (string.IsNullOrEmpty(appPath)) return false;
        return appPath!.IndexOf(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// 经典滚动控件：吃 `delta/120` 整数除法，注入小 delta 等于没滚。
    /// 上游没有这个名单（靠 System32 排除间接覆盖），第三方程序里的同名控件
    /// 照样会中招，所以单独列出来。
    /// </summary>
    internal static bool IsClassicScrollControl(string? className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        switch (className)
        {
            case "Edit":
            case "RichEdit":
            case "RichEdit20A":
            case "RichEdit20W":
            case "RichEdit50W":
            case "RichEditD2DPT":
            case "ListBox":
            case "ComboBox":
            case "ComboBoxL":
            case "ScrollBar":
            case "SysListView32":
            case "SysTreeView32":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 这一格 delta 像不像"鼠标在滚一格"。
    ///
    /// 上游口径（伪代码 FUN_14005bc60）：`|delta| &lt; 0x32` 直接放行；
    /// 并且只对 120（一格）或 100 的整数倍做平滑 —— 触摸板/精密滚轮发的是
    /// 1~30 这种连续小量，60/30 这种半格 delta 多来自远程桌面或特殊驱动。
    /// 差别：上游要求"最近三次都是 120/100 倍数"才动，我们**单格**就判 ——
    /// 否则每一段连滚的头三格都会漏给系统，手感更差（这是有意偏离，写在注释里）。
    /// </summary>
    internal static bool LooksLikeNotchDelta(int delta)
    {
        int d = Math.Abs(delta);
        if (d < 50) return false;                 // 上游 0x32
        if (d % 120 == 0) return true;            // 一格 120，或一次合并来的 240/360
        return d % 100 == 0;                      // 100 的整数倍（部分驱动）
    }

    private void ApplySettingsCore()
    {
        /* 快照只在设置变更时重建：钩子回调里再也不能出现 ToArray 分配。 */
        var rules = _settings().ScrollApps;
        _ruleSnapshot = rules == null ? Array.Empty<ScrollAppRule>() : rules.ToArray();
        _targetHwnd = IntPtr.Zero;      // 例外表变了，缓存的解析结果也可能过期
        _targetClass = "";
        _targetApp = "";
    }

    /// <summary>在快照上找这个程序的例外（无分配）。</summary>
    private ScrollAppRule? FindRuleCached(List<ScrollAppRule>? rules, string app)
    {
        if (string.IsNullOrWhiteSpace(app)) return null;
        if (_ruleSnapshot.Length == 0 && rules is { Count: > 0 }) ApplySettingsCore();
        var snapshot = _ruleSnapshot;
        for (int i = 0; i < snapshot.Length; i++)
        {
            var rule = snapshot[i];
            if (rule == null) continue;
            if (GestureTriggerMap.SameApp(rule.AppPath, app)) return rule;
        }
        return null;
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

    private void Send(int delta, bool horizontal)
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
                /* 注不进去就冷却几格：否则物理滚轮被我们吃掉、屏幕上却一像素没动 ——
                 * 用户看到的就是"滚轮彻底失效"，而且每一格都继续被吞。 */
                _passNotches = EchoCoolOffNotches;
            }
        }
    }

    private static long _injectionFailures;
    public long InjectionFailures => Interlocked.Read(ref _injectionFailures);

    /* 上一版这里有一段 static 的"暂时别接管滚轮"（30 秒 → 5 分钟递增退避）。
     * 它和上游的模型正好相反：上游没有任何跨事件的状态，只回答"这一格能不能做"，
     * 做不了就原样放行。static 还让它跨实例、跨 --scroll-probe 互相污染 ——
     * 一次误判就让平滑消失好几分钟，而且没人知道为什么。
     * 现在只剩 OnWheel 里那个按**格数**的 _passNotches 冷却。 */

    /// <summary>
    /// 仅供自检：把回声校验/冷却的状态清回初始值。
    ///
    /// 现在这些都是**实例**字段（上一版是 static，会跨实例、跨探针互相污染），
    /// 所以自检只要对自己的实例调它就行。
    /// </summary>
    internal void ResetEchoForTest()
    {
        Volatile.Write(ref _pendingSince, 0);
        Interlocked.Exchange(ref _lastEcho, 0);
        _passNotches = 0;
    }

    /// <summary>
    /// 诊断：`CLP_SCROLL_TRACE=1` 时把每一格的**判定理由**写进 `%TEMP%\clp-scroll.log`
    /// （前 200 行，之后自己闭嘴，别把钩子拖垮）。
    ///
    /// 为什么需要：`--scroll-probe` 只能证明"钩子收到了、有没有带标记的注入"，
    /// 分不清"OnWheel 根本没被调用"和"被某道闸放行了"。有了这一行，滚一格就能看出
    /// 是 System32 闸、经典控件闸、delta 口径，还是压根没进来。
    /// </summary>
    private static void Trace(string reason, int delta)
    {
        if (!_traceOn) return;
        if (Interlocked.Increment(ref _traceLines) > 200) return;
        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clp-scroll.log");
            System.IO.File.AppendAllText(path,
                $"{DateTime.Now:HH:mm:ss.fff} delta={delta,5} {reason}{Environment.NewLine}");
        }
        catch { }
    }

    private static readonly bool _traceOn =
        Environment.GetEnvironmentVariable("CLP_SCROLL_TRACE") == "1";
    private static int _traceLines;

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
        /* 先拍快照再遍历：这个方法是从**钩子回调**里进来的，而 rules 是界面线程上
         * 会被增删的活 List。以前直接 foreach 活表，用户"加一条程序例外"的那一下
         * 正好撞上滚轮，就抛 InvalidOperationException —— 那一格滚轮直接漏给系统。 */
        ScrollAppRule?[] snapshot = rules.ToArray();
        foreach (var rule in snapshot)
        {
            if (rule == null) continue;
            if (SameAppOrFamily(rule.AppPath, app)) return rule;
        }
        return null;
    }

    /// <summary>
    /// 规则里的程序与当前目标算不算"同一个程序"。
    ///
    /// 比 <see cref="GestureTriggerMap.SameApp"/> 多两条，都是用户实测踩出来的：
    ///
    ///   ① **同族进程**（wps / wpspdf / wpscloudsvr）：WPS 的 PDF 阅读器是单独进程，
    ///      用户在设置里禁了 "WPS"，切到 PDF 里滚动照样平滑 —— 那是同一套软件；
    ///   ② **WPS 套件**（wps 文字 / et 表格 / wpp 演示 / wpspdf）：它们装在同一个
    ///      `…\office6\` 目录下，是同一份安装。用户在"平滑滚动 → 程序例外"里挑的
    ///      是他当下那个窗口的 exe（多半是 wps.exe），之后在**表格**里滚 ——
    ///      et.exe 与 wps.exe 既不是同名也不是前缀，于是规则整条失效。
    ///      2026-11-26 的报障就是这一条。
    ///
    /// 要求"同一个 office6 目录"，是为了不把别的软件里恰好叫 et.exe / wps.exe
    /// 的程序一起禁掉。
    /// </summary>
    internal static bool SameAppOrFamily(string? rulePath, string? targetPath)
    {
        if (GestureTriggerMap.SameApp(rulePath, targetPath)) return true;

        string a = StemOf(rulePath), b = StemOf(targetPath);
        if (a.Length == 0 || b.Length == 0) return false;

        if (ForegroundFileProbe.SameFamily(a, b)) return true;

        return IsWpsSuiteName(a) && IsWpsSuiteName(b) && SameWpsInstall(rulePath!, targetPath!);
    }

    /// <summary>WPS 套件里的几个主程序名（去扩展名、小写）。</summary>
    private static readonly string[] WpsSuiteNames = { "wps", "et", "wpp", "wpspdf", "wpscloudsvr", "wpsoffice" };

    private static bool IsWpsSuiteName(string stem) =>
        WpsSuiteNames.Contains(stem, StringComparer.OrdinalIgnoreCase);

    private static string StemOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFileNameWithoutExtension(path).ToLowerInvariant(); }
        catch { return ""; }
    }

    /// <summary>
    /// 两个 WPS 程序是不是同一份安装：取 <c>…\office6\</c> 之前的目录比较。
    /// 拿不到目录（规则里只写了文件名）就返回 false —— 宁可让规则不生效，
    /// 也不能凭一个裸名字把别人家同名的 exe 一起禁了。
    /// </summary>
    private static bool SameWpsInstall(string a, string b)
    {
        string ra = InstallRootOf(a), rb = InstallRootOf(b);
        return ra.Length > 0 && rb.Length > 0
            && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
    }

    private static string InstallRootOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            int at = full.LastIndexOf(@"\office6\", StringComparison.OrdinalIgnoreCase);
            return at > 0 ? full[..at] : "";
        }
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
