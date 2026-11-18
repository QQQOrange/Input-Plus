namespace InputPlus.Core.Settings;

/// <summary>
/// 平滑滚动的参数。字段名与默认值照抄 SmoothScroll 的 <c>AppOptions</c>
/// （source/SmoothScrollGUI/SmoothScrollGUI/AppOptions.cs），这样"照着它的手感调"
/// 是可能的：同一个数字在两边含义一致。
///
/// 与它的差别只有一处：<see cref="Enabled"/> 出厂是关的。
/// 它装完就接管整个系统的滚轮，我们是在一个Input Plus软件里加功能，
/// 不该在用户没点过任何东西的时候改变全系统的滚动手感。
/// </summary>
public sealed class ScrollOptions
{
    /// <summary>总开关。关掉时滚轮事件原样放行，一个字节都不改。</summary>
    public bool Enabled { get; set; }

    /// <summary>一格滚轮（120）滚动多少像素。</summary>
    public double StepSize { get; set; } = 120;

    /// <summary>一格滚轮滚完这段距离用的时间（毫秒）。</summary>
    public double AnimationTime { get; set; } = 360;

    /// <summary>
    /// 缓动曲线的"尾巴"长度：4 = 尾巴是前半段的 4 倍长（SmoothScroll 默认）。
    /// 越大越"滑"，越小越干脆。
    /// </summary>
    public double PulseScale { get; set; } = 4;

    /// <summary>连续快速滚动时按倍数加速（SmoothScroll 默认关）。</summary>
    public bool Acceleration { get; set; }

    /// <summary>两次滚轮间隔小于这个毫秒数才算"连续快滚"。</summary>
    public double AccelerationDelta { get; set; } = 70;

    /// <summary>加速的上限倍数。</summary>
    public double AccelerationMax { get; set; } = 7;

    /// <summary>横向滚轮（触摸板左右滑 / 滚轮左右拨）也平滑。</summary>
    public bool Horizontal { get; set; } = true;

    /// <summary>按住 Shift 滚轮时，垂直滚动转成横向平滑滚动。</summary>
    public bool HorizontalShift { get; set; } = true;

    /// <summary>方向反过来（滚轮上 = 内容往下）。</summary>
    public bool Reverse { get; set; }

    /// <summary>
    /// 触摸板/精密滚轮发的细碎 delta（小于一格 120）交给系统原样处理。
    ///
    /// 这类设备本来就是连续的，再插一层缓动只会发黏；而且它们一格能发几十个
    /// 小事件，全吞下来会明显增加钩子回调的负担。
    /// </summary>
    public bool IgnoreTouchpad { get; set; } = true;

    public ScrollOptions Clone() => new()
    {
        Enabled = Enabled,
        StepSize = StepSize,
        AnimationTime = AnimationTime,
        PulseScale = PulseScale,
        Acceleration = Acceleration,
        AccelerationDelta = AccelerationDelta,
        AccelerationMax = AccelerationMax,
        Horizontal = Horizontal,
        HorizontalShift = HorizontalShift,
        Reverse = Reverse,
        IgnoreTouchpad = IgnoreTouchpad,
    };
}

/// <summary>
/// 单个程序的平滑滚动设置。留着 <c>null</c> 的字段表示"跟随全局" ——
/// 与手势的作用目标表一个思路：只在用户真的改过某一项时才存这一项。
/// </summary>
public sealed class ScrollAppRule
{
    /// <summary>exe 全路径；匹配时也接受文件名（见 <c>GestureTriggerMap.SameApp</c>）。</summary>
    public string AppPath { get; set; } = "";

    /// <summary>这个程序里要不要平滑滚动。false = 整个放行，交给系统。</summary>
    public bool Enabled { get; set; } = true;

    public double? StepSize { get; set; }

    public double? AnimationTime { get; set; }

    public ScrollAppRule Clone() => new()
    {
        AppPath = AppPath,
        Enabled = Enabled,
        StepSize = StepSize,
        AnimationTime = AnimationTime,
    };
}
