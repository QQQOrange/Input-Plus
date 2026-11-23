using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InputPlus.Core.Scroll;

/// <summary>
/// 一段滚轮脉冲：**一次滚轮事件**触发的那一段动画。
///
/// 为什么要"多段"（抄的原版引擎 FUN_14005cb20 的结构）：
/// 快速连滚时事件是一个接一个来的。如果把它们并成"一条动画、每来一格就重置时间线"，
/// 每一格都会把缓动曲线从头再走一遍 —— 曲线前段导数很小，表现就是
/// "每滚一格都重新起步一次"，越滚越黏、越快越顿。原版的做法是每格各开一段、
/// 互不干扰，每帧把各段这一帧的位移**加起来**一次发出去：
/// 连滚时速度是叠上去的，而不是每格从零起步。
/// </summary>
internal sealed class ScrollPulse
{
    /// <summary>这一段总共要滚多少像素（带符号）。</summary>
    public double Target;

    /// <summary>这一段已经发出去多少（整数累加，取整的小数留在这里当下帧的余量）。</summary>
    public double Sent;

    /// <summary>起跳时刻，单位毫秒，取自 <see cref="ScrollClock.NowMs"/>。</summary>
    public long StartTick;

    /// <summary>这一段走完要多少毫秒（= 设置里的"动画时长"，与位移大小无关）。</summary>
    public double Duration;

    /// <summary>发过帧了没有。"起手补一帧"只对第一帧生效。</summary>
    public bool Started;
}

/// <summary>
/// 脉冲队列：添加一段、按时间推进一帧、把余量一次发完。
///
/// 这里刻意不碰定时器和注入口 —— 全是纯逻辑，自检可以给定"现在几点"直接把
/// 几十帧跑完，不需要真的滚屏幕，也不会因为机器卡了一下就测出随机结果。
/// </summary>
internal static class ScrollPulses
{
    /// <summary>
    /// 同时最多叠多少段。原版是一个全局数组 + 一个计数，写入时**没有上限检查**；
    /// 这里给个天花板：正常连滚同时也就十几段，真到 64 段说明有东西在疯狂灌事件，
    /// 再往上加也只是把同一帧的位移做大，没有意义。
    /// </summary>
    public const int MaxCount = 64;

    /// <summary>新来一格：按它自己的时间线单开一段。</summary>
    public static void Add(List<ScrollPulse> pulses, double pixels, long now, double duration)
    {
        if (pixels == 0) return;

        /* 正常连滚同时最多十几段（360 毫秒 ÷ 几十毫秒一格），64 段基本不可达。
         * 真撞上了就把最老一段的余量并进这一段：同向才能并（并进反方向会破坏
         * "一段之内不回跳"的约束），方向相反就把它那点零头丢掉 —— 总比让队列
         * 无限长、或者让最老那段再慢慢爬 360 毫秒好。 */
        while (pulses.Count >= MaxCount)
        {
            ScrollPulse oldest = pulses[0];
            pulses.RemoveAt(0);
            if (Math.Sign(oldest.Target) == Math.Sign(pixels))
                pixels += oldest.Target - oldest.Sent;
        }

        pulses.Add(new ScrollPulse
        {
            Target = pixels,
            Sent = 0,
            StartTick = now,
            Duration = duration > 0 ? duration : 1,
            Started = false,
        });
    }

    /// <summary>
    /// 推进一帧，返回这一帧应该发出去的总像素（各段之和，带符号）。
    /// 走完的段落会被移除。
    /// </summary>
    public static int Step(List<ScrollPulse> pulses, long now, double scale)
    {
        int total = 0;
        for (int i = 0; i < pulses.Count; i++)
        {
            ScrollPulse p = pulses[i];

            /* 时间一律用"现在 − 起跳时刻"算，不靠"上一帧 + 帧间隔"累加。
             * 这样某一帧迟到了（GC、调度、系统忙），下一帧算出来的曲线位置自然更大，
             * 欠下的位移会自动补上 —— 掉帧只会让那一帧的位移变大，不会让整段变短。 */
            double elapsed = now - p.StartTick;
            double t = p.Duration <= 0 ? 1 : elapsed / p.Duration;
            if (t < 0) t = 0;

            bool last = t >= 1;
            double eased = last ? 1 : ScrollMath.Ease(t, scale);
            int delta = ScrollMath.FrameDelta(p.Target, p.Sent, eased, !p.Started);

            p.Started = true;
            p.Sent += delta;
            total += delta;

            if (last)
            {
                pulses.RemoveAt(i);
                i--;
                continue;
            }
            pulses[i] = p;
        }
        return total;
    }

    /// <summary>
    /// 把当前所有段的余量一次算出来（换轴、关开关、退出时用）。
    /// 不清空队列 —— 由调用方决定是不是还要继续。
    /// </summary>
    public static int Rest(List<ScrollPulse> pulses)
    {
        int rest = 0;
        for (int i = 0; i < pulses.Count; i++)
            rest += (int)Math.Round(pulses[i].Target - pulses[i].Sent);
        return rest;
    }
}

/// <summary>
/// 动画用的时钟与定时器精度。
///
/// 为什么不用 <c>Environment.TickCount64</c>：它的分辨率跟着系统定时器走，
/// 没人的时候是 15.6 毫秒一跳。拿它当"过了几毫秒"用，8 毫秒的帧会被量化成
/// 0/15.6/31.2 这种台阶，帧间距忽长忽短 —— 数字看着没错，手感就是不如原版。
/// 原版引擎在动画期间调 <c>timeBeginPeriod(1)</c>、把线程提到 TIME_CRITICAL，
/// 并且用 vblank 对齐节拍，就是为了这件事。
///
/// 这里用 <see cref="Stopwatch"/>（走 QPC，微秒级）算时间，动画期间把系统定时器
/// 精度提到 1 毫秒，停下来就还回去 —— 一直提着会白白增加整机唤醒次数、费电。
/// </summary>
internal static class ScrollClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static bool _raised;

    /// <summary>单调递增的毫秒数（只用来算差值）。</summary>
    public static long NowMs => Watch.ElapsedMilliseconds;

    public static void RaiseResolution()
    {
        lock (Gate)
        {
            if (_raised) return;
            _raised = true;
            try { TimeBeginPeriod(1); } catch { /* 拿不到 1ms 精度就退回系统默认，不影响正确性 */ }
        }
    }

    public static void DropResolution()
    {
        lock (Gate)
        {
            if (!_raised) return;
            _raised = false;
            try { TimeEndPeriod(1); } catch { }
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
