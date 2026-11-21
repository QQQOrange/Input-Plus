using System;
using System.Collections.Generic;
using System.Linq;
using InputPlus.Core.Gestures;

namespace InputPlus;

public static partial class SelfTest
{
    /// <summary>
    /// 笔画平滑（Core/Gestures/StrokeSmoother.cs）的底线。
    ///
    /// 平滑的目标是"好看"，但它的**风险**是改形状 —— 一条 L 被磨成斜线，
    /// 用户就会觉得"我画的明明是这个，它认成那个"。所以这里不验"线条是否变漂亮"，
    /// 只验四件事：
    ///   ① 抖动直线的方向串一字不差（识别器最终看的就是它）；
    ///   ② 真拐角一个都不许挪走；
    ///   ③ 开关关掉时逐点相同（零行为差异）；
    ///   ④ 偏差超过门限时整条退回原轨迹（宁可不动）。
    /// </summary>
    private static void TestSmoothing()
    {
        var opts = new GestureOptions();   // SmoothStrokes 默认 true

        /* ---- ① 抖动直线：上下 1.2px 的抖，方向串必须完全一样 ---- */
        var jitter = new List<GestureMath.Pt>();
        for (int i = 0; i <= 400; i += 4)
            jitter.Add(new GestureMath.Pt(i, i / 4 % 2 == 0 ? 0 : 1.2));

        string DirOf(IReadOnlyList<GestureMath.Pt> p) =>
            GesturePattern.FromPoints(p, opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance);

        string dirBefore = DirOf(jitter);
        var smoothed = StrokeSmoother.Smooth(jitter, opts);
        string dirAfter = DirOf(smoothed);

        Check("平滑：抖动直线的方向串不变", dirBefore == dirAfter, dirBefore + " → " + dirAfter);
        Check("平滑：抖动直线真的被磨平（点数不增、抖动幅度变小）",
            smoothed.Count <= jitter.Count && smoothed.Max(p => Math.Abs(p.Y)) < 1.2,
            smoothed.Count + " 点 / 最大抖动 " + smoothed.Max(p => Math.Abs(p.Y)).ToString("F2"));
        Check("平滑：首尾点保留（起手和收笔的位置不能被挪走）",
            smoothed.Count >= 2 && Near(smoothed[0], jitter[0]) && Near(smoothed[^1], jitter[^1]),
            smoothed.Count.ToString());

        /* ---- ② 明显拐角：L 形的拐角点必须在结果里 ---- */
        var corner = new List<GestureMath.Pt>();
        for (int i = 0; i <= 300; i += 4) corner.Add(new GestureMath.Pt(i, i % 8 == 0 ? 0.6 : 0));
        for (int i = 4; i <= 300; i += 4) corner.Add(new GestureMath.Pt(300 + (i % 8 == 0 ? 0.6 : 0), i));

        var smoothedCorner = StrokeSmoother.Smooth(corner, opts);
        var vertex = new GestureMath.Pt(300, 0);
        Check("平滑：拐角点原样保留（没有被 Chaikin 磨圆）",
            smoothedCorner.Any(p => Near(p, vertex, 1.0)),
            "最近点 " + smoothedCorner.Min(p => (p - vertex).Length).ToString("F2") + "px");
        Check("平滑：拐角手势的方向串不变（→↓ 还是 →↓）",
            DirOf(corner) == DirOf(smoothedCorner),
            DirOf(corner) + " → " + DirOf(smoothedCorner));
        Check("平滑：拐角两侧的线段仍然是直的（没把 L 磨成弧）",
            smoothedCorner.Any(p => p.X >= 300 - 1.0 && p.Y >= 250),
            string.Join(",", smoothedCorner.TakeLast(3).Select(p => $"({p.X:F0},{p.Y:F0})")));

        /* ---- ③ 关开关：逐点相同，零行为差异 ---- */
        var off = new GestureOptions { SmoothStrokes = false };
        var untouched = StrokeSmoother.Smooth(corner, off);
        Check("平滑：关掉开关时逐点相同",
            untouched.Count == corner.Count
            && untouched.Select((p, i) => Near(p, corner[i])).All(same => same),
            untouched.Count + " vs " + corner.Count);
        Check("平滑：关掉开关时返回的是新列表（调用方可以随便改）",
            !ReferenceEquals(untouched, corner));

        /* ---- ④ 偏差超限：整条退回原轨迹 ---- */
        /* deviationLimit 只给自检用（正常运行按包围盒 10% 算）。
         * 传 0 表示"一点偏差都不许"：任何真的平滑过后的结果都会超，必须退回原轨迹。 */
        var strict = StrokeSmoother.Smooth(jitter, opts.ActivationDistance, opts.TurnTolerance,
            opts.MaxTurnDistance, deviationLimit: 0.0);
        Check("平滑：偏差超过门限就退回原轨迹",
            strict.Count == jitter.Count && strict.Select((p, i) => Near(p, jitter[i])).All(same => same),
            strict.Count + " vs " + jitter.Count);
        /* 同一条轨迹在正常门限下确实被改过 —— 否则上一条断言可能只是"本来就没变"，
         * 那样门限这条路根本没被验到。 */
        Check("平滑：同一条轨迹在正常门限下确实会变（说明上一条测的是门限）",
            !smoothed.Select((p, i) => i < jitter.Count && Near(p, jitter[i])).All(same => same),
            smoothed.Count + " vs " + jitter.Count);
    }

    /// <summary>两个点够近（默认 1e-9）。用长度判等，避免浮点 == 的坑。</summary>
    private static bool Near(GestureMath.Pt a, GestureMath.Pt b, double tolerance = 1e-9)
        => (a - b).Length <= tolerance;
}
