using System;

namespace InputPlus.Core.Scroll;

/// <summary>
/// 平滑滚动的曲线与取整，全是纯函数 —— 自检直接对着它们跑，
/// 不需要真的滚一下鼠标。
///
/// 曲线来自 SmoothScroll 同作者的开源库 smoothscroll.js：
/// <code>
/// pulse_(x) {
///   x *= pulseScale;
///   if (x < 1) val = x - (1 - exp(-x));
///   else { start = exp(-1); x -= 1; val = start + (1 - start) * (1 - exp(-x)); }
///   return val * pulseNormalize;
/// }
/// </code>
/// 它两段拼接：前半段加速（起步不突兀），后半段指数收敛（尾巴长、停得软）。
/// 注意 <c>pulse(1)</c> 并不等于 1（scale=4 时约 0.968），所以必须按它归一化，
/// 否则一格滚轮永远差几个像素 —— 长时间滚下去会越差越多。
/// </summary>
public static class ScrollMath
{
    public const double MinScale = 0.5;
    public const double MaxScale = 20;

    /// <summary>原始脉冲曲线（未归一化）。</summary>
    public static double Pulse(double x, double scale)
    {
        if (x <= 0) return 0;
        double s = Math.Clamp(scale, MinScale, MaxScale);
        double v = x * s;
        if (v < 1) return v - (1 - Math.Exp(-v));

        double start = Math.Exp(-1);
        v -= 1;
        return start + (1 - start) * (1 - Math.Exp(-v));
    }

    /// <summary>归一化后的缓动：<c>Ease(0)=0</c>、<c>Ease(1)=1</c>，中间单调递增。</summary>
    public static double Ease(double t, double scale)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double max = Pulse(1, scale);
        if (max <= 1e-9) return t;
        return Math.Clamp(Pulse(t, scale) / max, 0, 1);
    }

    /// <summary>
    /// 这一帧应该发多少像素（带符号）。
    ///
    /// <paramref name="from"/> 是本段动画的起点（新滚轮事件到来时 = 已经发出的量），
    /// <paramref name="target"/> 是目标总量，<paramref name="sent"/> 是已经发出的量。
    /// 用"想要的位置 − 已发出"取整，余数天然留在下一帧，不会累积误差。
    /// </summary>
    public static int NextDelta(double from, double target, double sent, double t, double scale)
    {
        double want = from + (target - from) * Ease(t, scale);
        return (int)Math.Round(want - sent);
    }

    /// <summary>一格滚轮换算成像素（含方向与加速倍数）。</summary>
    public static double PixelsFor(int delta, double stepSize, double accel, bool reverse)
    {
        double px = delta / 120.0 * stepSize * (accel <= 0 ? 1 : accel);
        return reverse ? -px : px;
    }
}
