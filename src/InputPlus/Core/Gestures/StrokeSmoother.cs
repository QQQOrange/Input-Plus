using System;
using System.Collections.Generic;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 笔画平滑：把手画出来的 1~2 像素抖动修顺，形状原样保留。
///
/// 只改「这一笔画出来长什么样」——识别用的模板、回放和缩略图的线条，
/// 不改判定环节：最小识别距离、起始超时在记点时就定了，与平滑无关。
///
/// 顺序是「合并近点 → 删尖刺 → 找拐角 → Chaikin → RDP」，每一步都为下一步服务：
///   · 先按 2px 合并：后面算角度必须在"有长度的相邻段"上算，
///     1px 的两个点能凑出任意角度，不合并就分不清哪个是真拐角；
///   · 尖刺（出去 1~2px 又原路回来）是手抖最典型的形态，单独删掉；
///   · 拐角点当锚点，Chaikin 只磨锚点之间的弧段，真拐角一个都不动；
///   · 最后 RDP 轻度化简：Chaikin 会把点数翻几倍，RDP 把它压回原量级，
///     同时去掉平滑后已经共线的点（这些点对形状没有信息）。
///
/// 两道守门（方向串一致 + 最大偏差）任一不过就整条退回原轨迹：
/// 平滑只是"修饰"，宁可不动也不能把 L 形抹成斜线。
/// </summary>
internal static class StrokeSmoother
{
    /// <summary>相邻点近到这个距离就合并。1~2px 的抖在屏幕上根本看不出来，留着只会干扰角度计算。</summary>
    internal const double MergeDistance = 2.0;

    /// <summary>尖刺的两条边都不超过这个长度，才可能是手抖，而不是真画了一小段折返。</summary>
    internal const double SpikeArm = 2.5;

    /// <summary>两条边夹角超过 150° 才算尖刺（几乎原路返回）。</summary>
    internal const double SpikeTurnDeg = 150.0;

    /// <summary>拐角判定：相邻两段夹角超过 40° 就当拐角保护起来。</summary>
    internal const double CornerTurnDeg = 40.0;

    /// <summary>
    /// 拐角的两条边都得有 4px 才认。2px 的抖动本身就能凑出 50° 夹角，
    /// 只看夹角会把整条线都锁成锚点，平滑等于没做。
    /// </summary>
    internal const double CornerArmMin = 4.0;

    /// <summary>RDP 容差（像素）。2.0 能去掉平滑后的共线点，又不会吃掉真的小弯。</summary>
    internal const double SimplifyEpsilon = 2.0;

    /// <summary>平滑前后最大偏差的上限（像素）。12px 是屏幕上还能看出「线条被挪了」的量级。</summary>
    internal const double MaxDeviationPx = 12.0;

    /// <summary>偏差上限的另一个口径：包围盒对角线的 10%。小笔画按比例收得更紧。</summary>
    internal const double MaxDeviationRatio = 0.10;

    /// <summary>偏差容差的下限：再严就会把正常抖动也算成「改了形状」，开关等于摆设。</summary>
    internal const double MinDeviationPx = 2.0;

    /// <summary>Chaikin 的轮数。两轮足够把 2px 的锯齿磨圆；再多就只是让拐角外扩、白算。</summary>
    private const int ChaikinRounds = 2;

    /// <summary>
    /// 按设置决定要不要平滑。开关关掉（或点数太少）时返回逐点相同的一份拷贝，
    /// 调用方拿去用就行，不需要在两边各写一次判断。
    /// </summary>
    public static List<GestureMath.Pt> Smooth(IReadOnlyList<GestureMath.Pt> points, GestureOptions options)
    {
        if (points == null || points.Count == 0) return new List<GestureMath.Pt>();
        if (!options.SmoothStrokes) return new List<GestureMath.Pt>(points);
        return Smooth(points, options.ActivationDistance, options.TurnTolerance, options.MaxTurnDistance);
    }

    /// <summary>
    /// 平滑主流程。<paramref name="deviationLimit"/> 只给自检用（想验证「超限就退回」时
    /// 不必去构造一条真的会偏 12px 的轨迹）；正常运行传 null，按包围盒比例算。
    /// </summary>
    internal static List<GestureMath.Pt> Smooth(
        IReadOnlyList<GestureMath.Pt> points,
        double activation,
        double turnTolerance,
        double maxTurn,
        double? deviationLimit = null)
    {
        var original = new List<GestureMath.Pt>(points);
        if (points.Count < 3) return original;

        var clean = Denoise(points);
        if (clean.Count < 3) return original;

        var curved = Curve(clean, Corners(clean));
        curved = GestureEngine.Simplify(curved, SimplifyEpsilon);
        if (curved.Count < 2) return original;

        // 守门一：方向串必须一字不差。识别器最终看的就是它，串变了就等于换了条手势。
        if (!SameDirections(original, curved, activation, turnTolerance, maxTurn)) return original;

        // 守门二：整条线的最大偏差。方向串一样、形状却被拉长/压扁的情况在这里拦。
        double limit = deviationLimit ??
                       Math.Clamp(GestureMath.Extent(original) * MaxDeviationRatio, MinDeviationPx, MaxDeviationPx);
        if (Deviation(original, curved) > limit) return original;

        return curved;
    }

    /// <summary>
    /// 合并近点、删掉尖刺。合并时总是保留最后一个点：鼠标松开的位置是用户
    /// 实际停下的地方，丢了会让笔画尾巴缩一截。
    /// </summary>
    private static List<GestureMath.Pt> Denoise(IReadOnlyList<GestureMath.Pt> points)
    {
        var merged = new List<GestureMath.Pt>(points.Count) { points[0] };
        for (int i = 1; i < points.Count; i++)
            if ((points[i] - merged[^1]).Length >= MergeDistance) merged.Add(points[i]);
        if ((points[^1] - merged[^1]).Length > 1e-9) merged.Add(points[^1]);

        /* 尖刺可能连着来（抖两下就是两个），删掉一个之后邻域又变成新的尖刺，
         * 所以扫到没有为止。点数不多，这种写法比一次扫描的边界处理可靠。 */
        bool removed = true;
        while (removed && merged.Count >= 3)
        {
            removed = false;
            for (int i = 1; i < merged.Count - 1; i++)
            {
                var back = merged[i] - merged[i - 1];
                var forth = merged[i + 1] - merged[i];
                if (back.Length > SpikeArm || forth.Length > SpikeArm) continue;
                if (TurnDeg(back, forth) < SpikeTurnDeg) continue;
                merged.RemoveAt(i);
                removed = true;
                break;
            }
        }

        return merged;
    }

    /// <summary>找出所有拐角（含首尾），返回下标。Chaikin 以这些点分段，段内才做磨圆。</summary>
    private static List<int> Corners(IReadOnlyList<GestureMath.Pt> pts)
    {
        var anchors = new List<int> { 0 };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var back = pts[i] - pts[i - 1];
            var forth = pts[i + 1] - pts[i];
            if (back.Length < CornerArmMin || forth.Length < CornerArmMin) continue;
            if (TurnDeg(back, forth) > CornerTurnDeg) anchors.Add(i);
        }
        anchors.Add(pts.Count - 1);
        return anchors;
    }

    /// <summary>逐段（锚点之间）做 Chaikin，接缝处只保留一个锚点，拐角因此原样不动。</summary>
    private static List<GestureMath.Pt> Curve(IReadOnlyList<GestureMath.Pt> pts, IReadOnlyList<int> anchors)
    {
        var result = new List<GestureMath.Pt>(pts.Count * 2);
        for (int k = 0; k < anchors.Count - 1; k++)
        {
            int from = anchors[k], to = anchors[k + 1];
            if (to <= from) continue;

            var span = new List<GestureMath.Pt>(to - from + 1);
            for (int i = from; i <= to; i++) span.Add(pts[i]);
            span = Chaikin(span, ChaikinRounds);

            int skip = result.Count > 0 ? 1 : 0;   // 接缝点已经在前一段的末尾
            for (int i = skip; i < span.Count; i++) result.Add(span[i]);
        }

        return result.Count >= 2 ? result : new List<GestureMath.Pt>(pts);
    }

    /// <summary>Chaikin 切角：每段取 1/4 和 3/4 两点，首尾保留。两轮之后折线就成弧了。</summary>
    private static List<GestureMath.Pt> Chaikin(List<GestureMath.Pt> pts, int rounds)
    {
        var cur = pts;
        for (int r = 0; r < rounds && cur.Count >= 3; r++)
        {
            var next = new List<GestureMath.Pt>(cur.Count * 2) { cur[0] };
            for (int i = 0; i < cur.Count - 1; i++)
            {
                var a = cur[i];
                var b = cur[i + 1];
                next.Add(new GestureMath.Pt(a.X * 0.75 + b.X * 0.25, a.Y * 0.75 + b.Y * 0.25));
                next.Add(new GestureMath.Pt(a.X * 0.25 + b.X * 0.75, a.Y * 0.25 + b.Y * 0.75));
            }
            next.Add(cur[^1]);
            cur = next;
        }
        return cur;
    }

    private static bool SameDirections(
        IReadOnlyList<GestureMath.Pt> before,
        IReadOnlyList<GestureMath.Pt> after,
        double activation,
        double turnTolerance,
        double maxTurn)
    {
        string a = GesturePattern.FromPoints(before, activation, turnTolerance, maxTurn);
        string b = GesturePattern.FromPoints(after, activation, turnTolerance, maxTurn);
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// 平滑前后的最大偏差（双向 Hausdorff，点到折线）：只量原轨迹上的点不够，
    /// Chaikin 会往弧内侧加新点，那些点有没有偏出去得反过来再量一遍。
    /// </summary>
    private static double Deviation(IReadOnlyList<GestureMath.Pt> original, IReadOnlyList<GestureMath.Pt> smoothed)
        => Math.Max(MaxPointToPolyline(original, smoothed), MaxPointToPolyline(smoothed, original));

    private static double MaxPointToPolyline(IReadOnlyList<GestureMath.Pt> points, IReadOnlyList<GestureMath.Pt> poly)
    {
        double worst = 0;
        foreach (var p in points) worst = Math.Max(worst, PointToPolyline(p, poly));
        return worst;
    }

    private static double PointToPolyline(GestureMath.Pt p, IReadOnlyList<GestureMath.Pt> poly)
    {
        if (poly.Count == 0) return double.MaxValue;
        if (poly.Count == 1) return (p - poly[0]).Length;

        double best = double.MaxValue;
        for (int i = 0; i < poly.Count - 1; i++)
        {
            var a = poly[i];
            var b = poly[i + 1];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 <= 1e-12 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = Math.Clamp(t, 0, 1);
            double ex = p.X - (a.X + dx * t), ey = p.Y - (a.Y + dy * t);
            double d = Math.Sqrt(ex * ex + ey * ey);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>两条边的夹角（度）。零长度边按 0° 处理，调用方都已先排除过短边。</summary>
    private static double TurnDeg(GestureMath.Pt a, GestureMath.Pt b)
    {
        double la = a.Length, lb = b.Length;
        if (la < 1e-9 || lb < 1e-9) return 0;
        double cos = (a.X * b.X + a.Y * b.Y) / (la * lb);
        cos = Math.Clamp(cos, -1, 1);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }
}
