using System;
using System.Collections.Generic;
using System.Globalization;

namespace InputPlus.Core.Gestures.WG;

/// <summary>
/// 手势识别门面：给一组模板路径 + 一条手绘路径，回答"最像的是哪一个"。
///
/// 这是 WGestures 2.9.1 识别引擎的忠实移植（反编译源码 A/J.cs、A/P.cs、A/G.cs），
/// 判定阈值不是拍脑袋来的，见 <see cref="DefaultThreshold"/> 的出处说明。
///
/// 用法（与源码 A/m.cs:478 的实时识别同构）：
/// <code>
/// int hit = WgEngine.BestMatch(templates, drawnPath, WgEngine.DefaultThreshold);
/// if (hit &gt;= 0) { /* 命中 templates[hit] */ }
/// </code>
/// </summary>
public static class WgEngine
{
    /// <summary>
    /// 引擎内部的"∞"。源码 A/G.cs:307 把 DP 表的每一格初始化为 0.2，
    /// 且只从 &lt; 0.2 的格子向外扩展——0.2 既是初值也是"此路不通"的判据，
    /// 所以它是**表内哨兵**，不是判定阈值。真阈值见 <see cref="DefaultThreshold"/>。
    /// </summary>
    public const double MaxDistance = 0.2;

    /// <summary>
    /// 真实判定阈值：距离 &lt;= 0.05 就算命中。出处：
    ///
    /// · A/m.cs:126 —— private double A_f11 = 0.05;（字段初值，全仓库唯一一处与判定相关的 0.05）
    /// · A/m.cs:305 —— public double b() { return A_f11; }
    /// · A/m.cs:498-499 —— 实时识别主循环：
    ///     double num = item.Gesture.A(P_0);   // 模板.A(手绘)
    ///     if (!(num &gt; b()))                   // 即 num &lt;= 0.05 才进候选
    ///
    /// 也就是说源码里**没有**"灵敏度"设置在调这个值：在 WGCore / WGApp / A 目录下
    /// 搜 Sensitivity、Threshold、Tolerance 全部无命中，0.05 是硬编码常量，
    /// 它的 setter（A/m.cs:312）没有任何调用者。
    /// 另一个常量 0.01（A/m.cs:129）用于同一次识别里多个候选互相太像时的择优
    /// （A/m.cs:522-524），不是命中门限。
    /// </summary>
    public const double DefaultThreshold = 0.05;

    /// <summary>
    /// 在模板里找最像 <paramref name="drawn"/> 的那条，返回下标；没命中返回 -1。
    ///
    /// 方向照抄源码：**模板是 receiver、手绘是参数**（模板.DistanceTo(手绘)）。
    /// 这决定了 128 点上限卡在哪一侧（见 <see cref="WgStroke.DistanceTo"/>）。
    ///
    /// 并列时取下标更小的那条。源码在每层里用 "num &lt;= 旧值" 覆盖，
    /// 等价于并列取后者；只有浮点完全相等时两者才不同，这里选"先出现的赢"，
    /// 让结果对调用方稳定可复现。
    /// </summary>
    public static int BestMatch(IReadOnlyList<WgPath> templates, WgPath drawn, double threshold)
    {
        int index;
        double distance;
        return TryBestMatch(templates, drawn, threshold, out index, out distance) ? index : -1;
    }

    /// <summary>
    /// <see cref="BestMatch"/> 的带距离版本。没命中时 <paramref name="distance"/> 为 +∞。
    ///
    /// "不可比"与"超阈值"在这里合并成同一个结果（都是不命中），
    /// 但距离值原样带出来，方便调用方打印、调阈值。
    /// </summary>
    public static bool TryBestMatch(
        IReadOnlyList<WgPath> templates,
        WgPath drawn,
        double threshold,
        out int index,
        out double distance)
    {
        index = -1;
        distance = double.PositiveInfinity;
        if (templates == null || drawn == null)
        {
            return false;
        }

        for (int i = 0; i < templates.Count; i++)
        {
            WgPath template = templates[i];
            if (template == null)
            {
                continue;
            }

            double d = template.DistanceTo(drawn);

            // NaN 必须显式挡掉：源码的 "!(num > 0.05)" 对 NaN 求值为 true，
            // 会让退化数据（零长度、重复点）命中任意模板。这里选择拒绝。
            if (double.IsNaN(d) || d > threshold)
            {
                continue;
            }

            if (d < distance)
            {
                distance = d;
                index = i;
            }
        }

        return index >= 0;
    }

    /// <summary>
    /// 一笔的诊断串，例如 "6点 长度 812.4"。自检/日志用：
    /// 识别不灵时第一件要确认的就是"采点后到底剩几个点、多长"。
    /// </summary>
    public static string Describe(WgStroke s)
    {
        if (s == null)
        {
            return "0点 长度 0.0";
        }

        return s.Count.ToString(CultureInfo.InvariantCulture) + "点 长度 " +
               s.Length.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
