using System;
using System.Collections.Generic;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 轨迹校正（canonicalize）：把画歪的笔画吸到水平 / 垂直 / 45°，竖线变竖、横线变横，
/// 一笔画出来的字母（C、Z、L、S）也回到规整的形状。
///
/// 关键点：校正同时用在「模板」和「手绘」两侧，而且用的是同一个函数。
/// 用户画的 82° 竖线和模板里的 90° 竖线会被吸成同一条线，所以识别宽容度只会变高，
/// 不会因为"以后画的跟模板不一样"而失灵。
///
/// 顺序：合并近点 → RDP 抽稀 → 按转角切段 → 每段方向吸附 → 沿吸附方向重排折点 →
/// 每段按弧长重采样（~4 单位）→ 整体平移对齐质心。
///
/// 两道守门（照抄 StrokeSmoother 的思路，不过就整条退回原轨迹）：
///   ① 方向串：GesturePattern.FromPoints(校正后) 必须和校正前完全一致；
///   ② 最大偏差：双向 Hausdorff 不能超过外接矩形长边的 15%（夹在 3 ~ 160 之间）。
/// 宁可不校正，也不要把用户的手势掰成别的东西。
/// </summary>
public static class StrokeCanonicalizer
{
    /// <summary>外接矩形小于这个尺寸就当原地画点，直接原样返回。</summary>
    private const double MinShapeExtent = 1.0;

    /// <summary>合并相邻段时，方向差小于这个角度就认为是一条线（不制造假拐点）。</summary>
    private const double MergeTurnDeg = 6.0;

    /// <summary>校正参数。默认值就是线上用的值，自检里可以按需覆盖。</summary>
    public sealed class CanonicalizeOptions
    {
        /// <summary>段方向离最近的水平 / 垂直轴不超过这个角度，就吸附成轴对齐。</summary>
        public double SnapToleranceDeg { get; set; } = 22.0;

        /// <summary>离最近的 45° 斜线不超过这个角度，就吸附成标准斜线。</summary>
        public double DiagonalToleranceDeg { get; set; } = 12.0;

        /// <summary>合并近点的距离（去抖）。</summary>
        public double MergeDistance { get; set; } = 2.0;

        /// <summary>RDP 抽稀阈值，和 GestureEngine.Simplify 的默认口径一致。</summary>
        public double SimplifyEpsilon { get; set; } = 2.0;

        /// <summary>相邻两段夹角超过这个度数就切成两段（拐角）。</summary>
        public double CornerTurnDeg { get; set; } = 35.0;

        /// <summary>切段时要求当前段已经有的长度，避免 2px 抖动凑出拐角。</summary>
        public double MinSegmentLength { get; set; } = 8.0;

        /// <summary>重采样间距（与点集同一坐标系，手绘时就是像素）。</summary>
        public double ResampleSpacing { get; set; } = 4.0;

        /// <summary>
        /// 允许的最大偏差 = 外接矩形长边 × 该比例（再夹到下面两个上下限）。
        /// 取 15%：12° 的歪线吸附后端点只挪 10.4%，能过；再歪的（≈18° 以上）就该
        /// 被守门②拦下来 —— 那已经不是"画歪了"，是另一种形状。
        /// 说明：原需求写的是 0.12 且上限 18px，但 400px 长的一条 8° 斜线光端点横移
        /// 就有 28px，按 18px 卡死会把验收要求的"8° 竖线必须掰直"直接判掉。
        /// </summary>
        public double DeviationRatio { get; set; } = 0.15;

        public double MinDeviationPx { get; set; } = 3.0;

        public double MaxDeviationPx { get; set; } = 160.0;

        /// <summary>输出点数上限，超了就自动放宽重采样间距。</summary>
        public int MaxPoints { get; set; } = 2000;

        /// <summary>自检专用：直接指定偏差上限，用来证明守门②真的会拦。</summary>
        internal double? DeviationLimitOverride { get; set; }
    }

    /// <summary>
    /// 校正一条轨迹。点数 &lt; 3、原地画点、以及守门不通过时，原样返回（新 List）。
    /// </summary>
    public static List<GestureMath.Pt> Canonicalize(IReadOnlyList<GestureMath.Pt> points, CanonicalizeOptions? options = null)
    {
        var original = points == null ? new List<GestureMath.Pt>() : new List<GestureMath.Pt>(points);
        if (original.Count < 3) return original;
        if (GestureMath.Extent(original) < MinShapeExtent) return original;

        var o = options ?? new CanonicalizeOptions();

        // 1) 去抖 + 抽稀：复用引擎自己的口径，别另造一套。
        var thin = GestureEngine.Simplify(MergeNear(original, o.MergeDistance), o.SimplifyEpsilon);
        if (thin.Count < 2) return original;

        // 2) 按转角切段。
        var segments = Split(thin, o);
        if (segments.Count == 0) return original;

        // 3) 每段吸附方向后，从同一个起点逐段走折点：拐点天然共享，夹角正好 90° / 45°。
        var canonical = Rebuild(thin, segments, o);
        if (canonical.Count < 2) return original;

        // 4) 整体平移对齐质心，吸附造成的横向位移被摊到两边（偏差减半）。
        var shift = Centroid(original) - Centroid(canonical);
        for (int i = 0; i < canonical.Count; i++)
            canonical[i] = new GestureMath.Pt(canonical[i].X + shift.X, canonical[i].Y + shift.Y);

        // 守门①：方向语义一个字都不能变。
        string before = GesturePattern.FromPoints(original);
        string after = GesturePattern.FromPoints(canonical);
        if (!string.Equals(before, after, StringComparison.Ordinal)) return original;

        // 守门②：双向 Hausdorff 偏差。
        double extent = GestureMath.Extent(original);
        double limit = o.DeviationLimitOverride
            ?? Math.Clamp(extent * o.DeviationRatio, o.MinDeviationPx, o.MaxDeviationPx);
        if (Deviation(original, canonical) > limit) return original;

        return canonical;
    }

    // ── 去抖与切段 ────────────────────────────────────────────────────────

    /// <summary>相邻点近到 MergeDistance 就合并，首尾点必须保留。</summary>
    private static List<GestureMath.Pt> MergeNear(IReadOnlyList<GestureMath.Pt> points, double distance)
    {
        var result = new List<GestureMath.Pt>(points.Count) { points[0] };
        for (int i = 1; i < points.Count - 1; i++)
        {
            if (distance <= 1e-9 || (points[i] - result[result.Count - 1]).Length >= distance)
                result.Add(points[i]);
        }
        var last = points[points.Count - 1];
        if (result.Count == 1 || (last - result[result.Count - 1]).Length > 1e-9)
            result.Add(last);
        return result;
    }

    /// <summary>一段：起点 / 终点是抽稀后点集里的下标，A、B 是它们的坐标。</summary>
    private readonly record struct Segment(int From, int To, GestureMath.Pt A, GestureMath.Pt B);

    private static List<Segment> Split(List<GestureMath.Pt> thin, CanonicalizeOptions o)
    {
        var cuts = new List<int> { 0 };
        int anchor = 0;
        for (int i = 1; i < thin.Count - 1; i++)
        {
            var run = thin[i] - thin[anchor];
            var step = thin[i + 1] - thin[i];
            if (run.Length < o.MinSegmentLength || step.Length < 1e-9) continue;
            if (TurnDeg(run, step) > o.CornerTurnDeg)
            {
                cuts.Add(i);
                anchor = i;
            }
        }
        cuts.Add(thin.Count - 1);

        var list = new List<Segment>();
        for (int c = 0; c < cuts.Count - 1; c++)
        {
            int from = cuts[c], to = cuts[c + 1];
            if (to <= from) continue;
            list.Add(new Segment(from, to, thin[from], thin[to]));
        }
        return MergeCollinear(thin, list, o);
    }

    /// <summary>相邻两段方向差不到 MergeTurnDeg 的，其实是一条线：并成一段，免得留下一个假拐点。</summary>
    private static List<Segment> MergeCollinear(List<GestureMath.Pt> thin, List<Segment> segments, CanonicalizeOptions o)
    {
        var result = new List<Segment>();
        foreach (var seg in segments)
        {
            if (result.Count > 0)
            {
                var prev = result[result.Count - 1];
                var d1 = prev.B - prev.A;
                var d2 = seg.B - seg.A;
                if (d1.Length < 1e-9 || d2.Length < 1e-9 || TurnDeg(d1, d2) < MergeTurnDeg)
                {
                    result[result.Count - 1] = new Segment(prev.From, seg.To, thin[prev.From], thin[seg.To]);
                    continue;
                }
            }
            result.Add(seg);
        }
        return result;
    }

    // ── 吸附与重建 ────────────────────────────────────────────────────────

    /// <summary>段方向吸附：先看水平 / 垂直轴，再看 45° 斜线，都不沾边就保持原方向。</summary>
    private static (double X, double Y) Snap(Segment s, CanonicalizeOptions o)
    {
        double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return (1, 0);

        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        double axis = Math.Round(deg / 90.0) * 90.0;
        if (Math.Abs(Norm180(deg - axis)) <= o.SnapToleranceDeg) return Unit(axis);

        double diagonal = Math.Round((deg - 45.0) / 90.0) * 90.0 + 45.0;
        if (Math.Abs(Norm180(deg - diagonal)) <= o.DiagonalToleranceDeg) return Unit(diagonal);

        return Unit(deg);
    }

    /// <summary>段长 = 这一段所有点在吸附方向上的投影跨度，吸附后长度只会缩、不会凭空变长。</summary>
    private static double Span(List<GestureMath.Pt> thin, Segment s, double ux, double uy)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int i = s.From; i <= s.To; i++)
        {
            double t = (thin[i].X - s.A.X) * ux + (thin[i].Y - s.A.Y) * uy;
            if (t < min) min = t;
            if (t > max) max = t;
        }
        return max > min ? max - min : 0;
    }

    /// <summary>按吸附后的方向逐段走折点，再按弧长重采样（每段至少 2 个点，接缝只出现一次）。</summary>
    private static List<GestureMath.Pt> Rebuild(List<GestureMath.Pt> thin, List<Segment> segments, CanonicalizeOptions o)
    {
        int n = segments.Count;
        var dirs = new (double X, double Y, double Len)[n];
        double total = 0;
        for (int i = 0; i < n; i++)
        {
            var (ux, uy) = Snap(segments[i], o);
            double len = Span(thin, segments[i], ux, uy);
            dirs[i] = (ux, uy, len);
            total += len;
        }

        double spacing = o.ResampleSpacing <= 0.01 ? 0.01 : o.ResampleSpacing;
        if (total > 0 && total / spacing > o.MaxPoints) spacing = total / o.MaxPoints;

        var result = new List<GestureMath.Pt>();
        var cur = thin[segments[0].From];
        for (int i = 0; i < n; i++)
        {
            var (ux, uy, len) = dirs[i];
            var next = new GestureMath.Pt(cur.X + ux * len, cur.Y + uy * len);
            int steps = Math.Max(2, (int)Math.Ceiling(len / spacing) + 1);
            for (int k = i == 0 ? 0 : 1; k < steps; k++)
            {
                double t = (double)k / (steps - 1);
                result.Add(new GestureMath.Pt(cur.X + (next.X - cur.X) * t, cur.Y + (next.Y - cur.Y) * t));
            }
            cur = next;
        }
        return result;
    }

    // ── 小工具（与 StrokeSmoother 同一套写法） ────────────────────────────

    private static GestureMath.Pt Centroid(IReadOnlyList<GestureMath.Pt> points)
    {
        if (points.Count == 0) return new GestureMath.Pt(0, 0);
        double x = 0, y = 0;
        foreach (var p in points) { x += p.X; y += p.Y; }
        return new GestureMath.Pt(x / points.Count, y / points.Count);
    }

    private static double Norm180(double deg)
    {
        while (deg > 180.0) deg -= 360.0;
        while (deg < -180.0) deg += 360.0;
        return deg;
    }

    private static (double X, double Y) Unit(double deg)
    {
        double r = deg * Math.PI / 180.0;
        return (Math.Cos(r), Math.Sin(r));
    }

    /// <summary>两条边的夹角（度）。零长度边按 0° 处理。</summary>
    private static double TurnDeg(GestureMath.Pt a, GestureMath.Pt b)
    {
        double la = a.Length, lb = b.Length;
        if (la < 1e-9 || lb < 1e-9) return 0;
        double cos = (a.X * b.X + a.Y * b.Y) / (la * lb);
        cos = Math.Clamp(cos, -1, 1);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    private static double Deviation(IReadOnlyList<GestureMath.Pt> original, IReadOnlyList<GestureMath.Pt> canonical)
        => Math.Max(MaxPointToPolyline(original, canonical), MaxPointToPolyline(canonical, original));

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
}
