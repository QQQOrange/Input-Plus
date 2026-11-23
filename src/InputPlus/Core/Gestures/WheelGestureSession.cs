using System;

namespace InputPlus.Core.Gestures;

/// <summary>一格滚轮落到「按住 + 滚轮」会话上得出的结论。</summary>
public enum WheelOutcomeKind
{
    /// <summary>没有会话：这一格不归会话管，继续交给角落 / 贴边 / 原生滚动。</summary>
    None,

    /// <summary>这一格配了手势：执行 <see cref="WheelOutcome.Hit"/>，并吞掉这一格滚轮。</summary>
    Execute,

    /// <summary>会话还在、但这一格没配手势（多半是反方向没绑）：不执行也不吞，会话保留。</summary>
    Unbound,
}

/// <summary>一格滚轮的处理结论。</summary>
public readonly struct WheelOutcome
{
    public WheelOutcome(WheelOutcomeKind kind, MouseGesture? hit = null)
    {
        Kind = kind;
        Hit = hit;
    }

    public WheelOutcomeKind Kind { get; }

    /// <summary>要执行的那条手势（只有 <see cref="WheelOutcomeKind.Execute"/> 有值）。</summary>
    public MouseGesture? Hit { get; }
}

/// <summary>
/// 「按住右键连滚换标签」的会话状态机。
///
/// **用户要的语义**：右键按住不放，滚几格就执行几次动作；松开右键会话才结束。
/// 以前是一格一格重新按右键 —— 因为滚轮那一下被当成"这一笔做完了"，动作执行完就收尾。
///
/// WGestures 2 凭什么能连发（读的是 _归档/WGestures 2/src，那边的类名被混淆过）：
///   · <c>B/E.cs</c> 的 <c>e.A(object, ref C)</c>：把滚轮 delta 攒成"一格"
///     （110 阈值 + 250ms 静默补发，方向反转时先把累积按符号清零），再当作一次
///     **按键事件**发出去 —— 滚轮不算轨迹点，也不参与笔画形状；
///   · <c>A/K.cs</c> 的 <c>K.A(n, Vec2)</c>：会话活着就把这一格挂到 raw gesture 上并
///     **重新识别一次**，然后 <c>A_f12 = j.A</c> —— 会话不结束；
///     只有 <c>K.A(bool)</c>（鼠标键抬起，见 <c>B/d.cs::A()</c>）才收尾；
///   · <c>a/m.cs</c> 的 <c>a.m.A(J)</c> 第 534 行：
///     <c>A_f17.intent == 命中 &amp;&amp; 最后一步是鼠标移动</c> 才跳过重复执行 ——
///     所以「同一条手势 + 再挪一下鼠标」不再执行，「同一条手势 + 再滚一格」会再执行一次。
///   这三条合起来就是"画线一次、滚轮每格一次"。
///
/// 我们这边不需要自己攒 delta：钩子（<c>KeyboardHook.MouseCallback</c>）已经把滚轮换算成
/// ±1 格（水平 ±2）的**离散方向**，一个事件就是一格 —— 等价于 WG2 攒完之后的那个"键"。
/// 于是这里只剩"会话"本身：一格里要不要执行、会话何时结束、执行过记不记。
///
/// 三个必须说清的取舍：
/// ① **滚轮不带坐标，也不进轨迹**：<c>OnWheel</c> 只用指针位置摆名字框、算动作目标；
///    往 <c>_points</c> 里加点的只有 8ms 轮询。手势一旦执行过滚轮动作，
///    已画的那半截轨迹要立刻清掉（见 <c>GestureService.EnterWheelMode</c>），
///    否则松手时它还会被当成一笔笔画再执行一次。
/// ② **动作执行期间又滚一格：不丢，也不自己排队**。低级钩子回调由系统串行投递，
///    而动作本身是丢给界面线程的（<c>GestureService.Execute</c> 走 <c>Ui()</c>），
///    所以后到的格子天然按顺序排在后面。我们再排一次队就是双份，还会打乱顺序。
/// ③ **方向反转不重置任何"累积"**：这一层本来就没有累积，每一格按**自己的方向**找手势。
///    反方向没绑手势就原样放行这一格（页面照常滚），会话保留 —— 继续往原方向滚照样连发。
///    唯一记着的是 <see cref="Fired"/>：这一下按下已经执行过动作，
///    松开时靠它决定"绝不能补发一次右键"（否则换完标签会蹦出一个右键菜单）。
/// </summary>
public sealed class WheelGestureSession
{
    /// <summary>会话绑定的触发键（vk）。0 = 没有会话。</summary>
    public int Vk { get; private set; }

    /// <summary>这一下按下期间真的执行过动作 —— 抬起时不能再补发一次右键。</summary>
    public bool Fired { get; private set; }

    /// <summary>这一下按下期间执行了几次（诊断与自检用）。</summary>
    public int Hits { get; private set; }

    /// <summary>这一下按下期间方向反转了几次（诊断与自检用）。</summary>
    public int Reversals { get; private set; }

    public bool Active => Vk != 0;

    private int _lastSign;

    /// <summary>
    /// 这一下按下归滚轮会话管。同一个键重复调用是幂等的 ——
    /// 同一个按下里第二格滚轮还会再进来一次，不能把计数清掉。
    /// </summary>
    public void Begin(int vk)
    {
        if (vk == 0 || vk == Vk) return;
        Vk = vk;
        Fired = false;
        Hits = 0;
        Reversals = 0;
        _lastSign = 0;
    }

    /// <summary>这一格已经执行了动作：记账。<c>false</c> = 当前没有会话。</summary>
    public bool Hit(int wheel)
    {
        if (Vk == 0) return false;
        Fired = true;
        Hits++;
        TrackDirection(wheel);
        return true;
    }

    /// <summary>
    /// 一格滚轮进来：问会话"这一格要不要执行"。
    /// <paramref name="resolve"/> 负责按方向找手势（调用方那边还带应用作用域、Enabled 等判断）。
    /// </summary>
    public WheelOutcome Wheel(int wheel, Func<int, MouseGesture?> resolve)
    {
        if (Vk == 0) return new WheelOutcome(WheelOutcomeKind.None);

        var hit = resolve?.Invoke(wheel);
        if (hit == null)
        {
            /* 没配上也要记方向：反方向"没绑"同样是一次反转。 */
            TrackDirection(wheel);
            return new WheelOutcome(WheelOutcomeKind.Unbound);
        }

        Hit(wheel);
        return new WheelOutcome(WheelOutcomeKind.Execute, hit);
    }

    /// <summary>
    /// 触发键抬起来了：会话结束。
    /// 返回 true = 这次抬起属于滚轮会话 —— 按下当初被我方接管，抬起也必须吞掉，
    /// 否则目标程序会收到一个"没有按下"的 MouseUp。
    /// </summary>
    public bool End(int vk)
    {
        if (vk == 0 || vk != Vk) return false;
        Reset();
        return true;
    }

    /// <summary>无条件收干净（中止、看门狗、抬手丢失都走这里）。</summary>
    public void Reset()
    {
        Vk = 0;
        Fired = false;
        Hits = 0;
        Reversals = 0;
        _lastSign = 0;
    }

    /// <summary>只记方向，不参与任何阈值判断 —— 这一层不合帧，见类注释③。</summary>
    private void TrackDirection(int wheel)
    {
        int sign = Math.Sign(wheel);
        if (sign == 0) return;
        if (_lastSign != 0 && sign != _lastSign) Reversals++;
        _lastSign = sign;
    }
}
