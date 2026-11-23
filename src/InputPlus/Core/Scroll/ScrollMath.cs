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
///
/// 这些公式与原生引擎反编译出来的 <c>FUN_14005d210</c> 逐字一致（同一个两段式指数、
/// 同一个 <c>exp(-1)</c> 接缝、同一个 <c>pulseNormalize</c> 乘法），所以曲线本身
/// 不用改 —— 手感差异全部出在"怎么分帧、怎么取整、几段动画怎么叠加"上。
/// </summary>
public static class ScrollMath
{
    public const double MinScale = 0.5;
    public const double MaxScale = 20;

    /// <summary>
    /// 起手补一帧的量级（原版常量 0.99）。
    ///
    /// 原版给每段动画的"已发量"初值取 ±0.99（方向取反），使得 <c>ceil</c> 在
    /// **第一帧**就一定吐出 1 像素 —— 见 <see cref="FrameDelta"/> 里的对照说明。
    /// 这里只留常量表达"原版补了多少"，实际并不直接把这个值塞进已发量。
    /// </summary>
    public const double FirstFrameBias = 0.99;

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
    /// 这一帧该发多少像素（带符号）。
    ///
    /// <paramref name="target"/> 是一段动画总共要走的距离，<paramref name="sent"/> 是这段
    /// 已经发出去的量，<paramref name="eased"/> 是归一化曲线在当前时刻的值。
    ///
    /// 取整用 <c>Ceiling</c>（原版引擎就是 <c>ceil(target*eased - sent)</c>），而不是
    /// <c>Round</c>：<c>ceil</c> 保证"已发量"永远 ≥ 曲线上的理论位置，取整丢掉的
    /// 小数自动留在 <paramref name="sent"/> 里滚到下一帧，所以误差不会越积越大。
    ///
    /// 与原版有两处刻意的差别（都是为了不漂移，理由见注释）：
    ///   ① 原版把已发量初值设成 ∓0.99 来保证第一帧必动。代价是 <see cref="Ease"/> 在
    ///      第一帧就吐出 2 像素（本该 1 个），而且只有正方向多吐 —— 实测原版**一格
    ///      120 像素实际滚 121、向下滚 120**，上下滚同样的格数会失衡，连滚三格差 3 像素。
    ///      这里改成"只有真的算出 0 的第一帧才补 1 像素"，末端由补齐收回，两个方向都精确
    ///      （自检 <c>取整模型：原版的起手偏置让正方向每格多滚 1 像素</c> 把那 1 像素量了出来）。
    ///   ② 起手补的那 1 像素 + <c>ceil</c> 的取整误差，会在下一帧算出一个反向的 1 像素
    ///      （原版也有这个回跳：滚轮往上时中间夹一帧往下）。这里直接夹掉，
    ///      保证一段动画的位移序列单调 —— 自检守着这条。
    /// </summary>
    public static int FrameDelta(double target, double sent, double eased, bool firstFrame)
    {
        if (target == 0) return 0;

        /* 末帧：把剩下的整数像素一次补齐。
         * 这一句是"滚一百格也不会偏"的关键 —— 不补齐的话每段都会留下亚像素残渣，
         * 高速连滚时几十段残渣加起来就是肉眼可见的偏移。 */
        if (eased >= 1) return (int)Math.Round(target - sent);

        int delta = (int)Math.Ceiling(target * eased - sent);

        /* 起手补一帧：曲线在 t 很小时几乎为 0，向下滚时 ceil 会连续吐 0，
         * 表现是"往下滚比往上滚慢半拍"。补一个像素，让第一个动画帧必动。 */
        if (delta == 0 && firstFrame) delta = target > 0 ? 1 : -1;

        /* 一段动画里不许往回动（理由见上面的 ②）。 */
        if (delta != 0 && Math.Sign(delta) != Math.Sign(target)) delta = 0;
        return delta;
    }

    /// <summary>一格滚轮换算成像素（含方向与加速倍数）。</summary>
    public static double PixelsFor(int delta, double stepSize, double accel, bool reverse)
    {
        double px = delta / 120.0 * stepSize * (accel <= 0 ? 1 : accel);
        return reverse ? -px : px;
    }

    /// <summary>
    /// 连续快速滚动时的加速倍数。
    ///
    /// 原版（引擎的鼠标钩子 FUN_14005bc60，反编译里 SendInput 之前那段）不是
    /// "每快一格加 0.5 倍"那种阶跃式加速，而是按**两次滚轮的时间间隔比例**连续算：
    /// <code>
    /// if (gap &lt; accelerationDelta) {
    ///     f = (accelerationDelta / gap + 1) * 0.5;   // 间隔减半 → 1.5 倍
    ///     if (f &gt; accelerationMax) f = accelerationMax;
    ///     pixels *= f;
    /// }
    /// </code>
    /// 差别在手感上很直接：阶跃式加速里"间隔 69ms"和"间隔 5ms"拿到同一个倍数，
    /// 用户感觉是"忽然窜一下、松手还在窜"；比例式则是慢慢压上去、一停就回 1 倍，
    /// 而且它**不记忆状态**，所以不会出现"停顿半秒后再滚第一格还是加速状态"。
    /// </summary>
    public static double AccelerationFactor(double gapMs, double deltaMs, double max)
    {
        if (max <= 1) return 1;
        if (deltaMs <= 0) return 1;
        if (gapMs >= deltaMs) return 1;   // 不够快，不加速
        if (gapMs <= 0) return max;       // 同一毫秒内的两格（或时钟回退）
        double f = (deltaMs / gapMs + 1) * 0.5;
        if (f < 1) return 1;
        return f > max ? max : f;
    }
}
