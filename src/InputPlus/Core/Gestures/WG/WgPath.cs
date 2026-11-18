using System;
using System.Collections.Generic;

namespace InputPlus.Core.Gestures.WG;

/// <summary>
/// 手势能"停在"的屏幕边缘/角落。对应源码的 RectEdgeCorner（边的枚举里带角）。
///
/// 值本身没有语义，比较用相等：走到的位置不同就是完全不同的手势，
/// 不存在"相似"一说——见 <see cref="WgCornerStep.DistanceTo"/>。
/// </summary>
public enum WgCorner
{
    None,
    LeftTop,
    RightTop,
    LeftBottom,
    RightBottom,
    LeftEdge,
    RightEdge,
    TopEdge,
    BottomEdge
}

/// <summary>
/// 一步。一条手势是若干步的顺序序列：中间是笔迹（<see cref="WgStrokeStep"/>），
/// 也可以是"把鼠标甩到屏幕某个角"（<see cref="WgCornerStep"/>）。
/// 同源码的抽象类 <c>a.o</c>。
/// </summary>
public abstract class WgStep
{
    /// <summary>两步之间的距离；不可比时 +∞。同源码 <c>o.A(o)</c>。</summary>
    public abstract double DistanceTo(WgStep other);

    /// <summary>深拷贝。放在基类上强制实现，避免 <see cref="WgPath.Clone"/> 漏掉某个子类。</summary>
    public abstract WgStep Clone();
}

/// <summary>一笔（Stroke 步骤）。同源码 <c>a.p</c>。</summary>
public sealed class WgStrokeStep : WgStep
{
    public WgStrokeStep(WgStroke stroke)
    {
        Stroke = stroke ?? throw new ArgumentNullException(nameof(stroke));
    }

    public WgStroke Stroke { get; }

    public override double DistanceTo(WgStep other) =>
        other is WgStrokeStep s ? Stroke.DistanceTo(s.Stroke) : double.PositiveInfinity;

    public override WgStep Clone() => new WgStrokeStep(Stroke.Clone());
}

/// <summary>
/// "移动到某个边缘/角落"步骤。同源码 <c>a.P</c>：距离只有两种取值——
/// 同一个位置 0，否则 +∞。手指划到哪个角是离散语义，没有中间态。
/// </summary>
public sealed class WgCornerStep : WgStep
{
    public WgCornerStep(WgCorner corner)
    {
        Corner = corner;
    }

    public WgCorner Corner { get; }

    public override double DistanceTo(WgStep other) =>
        other is WgCornerStep c && c.Corner == Corner ? 0.0 : double.PositiveInfinity;

    public override WgStep Clone() => new WgCornerStep(Corner);
}

/// <summary>
/// 一条完整手势 = 步骤序列。同源码 A/J.cs 的 <c>J</c>（它就是个 RxList&lt;o&gt;）。
///
/// 两条路径的可比性由两件事决定：步数必须相等，每步都要可比。
/// 任何一步不可比，整条路径就是 +∞（没有"部分匹配"这回事）。
/// </summary>
public sealed class WgPath
{
    private readonly List<WgStep> _steps = new List<WgStep>(4);

    public int Count => _steps.Count;

    public IReadOnlyList<WgStep> Steps => _steps;

    /// <summary>追加一步，返回自身以便链式构造。</summary>
    public WgPath Add(WgStep step)
    {
        if (step == null)
        {
            throw new ArgumentNullException(nameof(step));
        }

        _steps.Add(step);
        return this;
    }

    /// <summary>
    /// 路径距离 = 各步距离之和。逐字移植 A/J.cs:21 的 <c>J.A(J)</c>。
    ///
    /// 为什么分成两轮而不是一趟算完：源码这么写是为了让"角落里甩出来的手势"
    /// 先做便宜的步骤比较（角步骤只有 0/∞），一旦不可比就直接返回 +∞，
    /// 完全不用去碰几十个点的笔画 DP。数值结果与一趟相加完全相同。
    /// 这里保留两轮，包括那个"只有存在笔迹对时才跑第二轮"的条件。
    /// </summary>
    public double DistanceTo(WgPath other)
    {
        if (other == null)
        {
            return double.PositiveInfinity;
        }

        int count = _steps.Count;
        if (count == 0 || count != other._steps.Count)
        {
            return double.PositiveInfinity;
        }

        double sum = 0.0;
        int strokePairs = count;
        for (int i = 0; i < count; i++)
        {
            WgStep a = _steps[i];
            WgStep b = other._steps[i];
            if (!(a is WgStrokeStep) || !(b is WgStrokeStep))
            {
                strokePairs--;
                double d = a.DistanceTo(b);
                if (double.IsPositiveInfinity(d))
                {
                    return double.PositiveInfinity;
                }

                sum += d;
            }
        }

        if (strokePairs > 0)
        {
            for (int i = 0; i < count; i++)
            {
                if (_steps[i] is WgStrokeStep a && other._steps[i] is WgStrokeStep b)
                {
                    double d = a.DistanceTo(b);
                    if (double.IsPositiveInfinity(d))
                    {
                        return double.PositiveInfinity;
                    }

                    sum += d;
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// 前缀匹配：<paramref name="prefix"/> 的每一步都能和本路径同位置的步对上。
    /// 逐字移植 A/J.cs:65 的 <c>J.a(J)</c>。
    ///
    /// 注意它**不看距离大小**，只看是否 +∞——也就是"结构上能不能对上"，
    /// 不是"形状像不像"。源码里它是实时识别时的候选筛选
    /// （TriggerDef.STARTS_WITH，A/m.cs:573），真正打分的在后头。
    /// </summary>
    public bool StartsWith(WgPath prefix)
    {
        if (prefix == null)
        {
            return false;
        }

        int count = prefix._steps.Count;
        if (count > _steps.Count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (double.IsPositiveInfinity(_steps[i].DistanceTo(prefix._steps[i])))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>深拷贝：笔迹复制、角步骤重建（它不可变，但保持"完全独立"的语义）。</summary>
    public WgPath Clone()
    {
        WgPath copy = new WgPath();
        for (int i = 0; i < _steps.Count; i++)
        {
            copy._steps.Add(_steps[i].Clone());
        }

        return copy;
    }

    /// <summary>把一笔包成只有一步的路径。</summary>
    public static WgPath FromStroke(WgStroke s) => new WgPath().Add(new WgStrokeStep(s));

    public override string ToString()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < _steps.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" → ");
            }

            switch (_steps[i])
            {
                case WgStrokeStep s:
                    sb.Append("笔迹(").Append(s.Stroke.Count).Append("点)");
                    break;
                case WgCornerStep c:
                    sb.Append(c.Corner);
                    break;
                default:
                    sb.Append("步骤");
                    break;
            }
        }

        return sb.ToString();
    }
}
