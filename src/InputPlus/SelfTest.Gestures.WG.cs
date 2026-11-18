using System;
using System.Collections.Generic;
using System.Globalization;
using InputPlus.Core.Gestures.WG;

namespace InputPlus;

/// <summary>
/// 自检：从 WGestures 2.9.1 移植过来的手势识别引擎（Core/Gestures/WG/）。
///
/// 这批用例的期望值不是随手定的：下面每一条的边界都来自独立控制台工程跑出的
/// 真实距离（同形手绘两次 ≈ 0.010~0.017，异形 ≈ 0.071~0.130），所以断言一律用区间，
/// 不钉具体小数 —— 换个采样密度数字就会动，但"同形远小于门限、异形远大于门限"不会动。
///
/// 数据一律走 Add 采点（含 18° 合并与取整），别在这里手搓 Signature：
/// 那样测的就不是运行时真正走的那个管线了。
/// </summary>
public static partial class SelfTest
{
    private static void TestWGesturesEngine()
    {
        Section("WGestures 手势引擎（形状距离）");

        // ---- 基线：归一化之后，尺度与采样密度都不该影响同一形状 ----
        var straight = WgBuild(WgLine(0, 0, 400, 0, 101));
        var straightRev = WgBuild(new List<WgVec2> { new WgVec2(400, 0), new WgVec2(0, 0) });
        var straight3 = WgBuild(WgScale(WgLine(0, 0, 400, 0, 101), 3.0));
        var straight03 = WgBuild(WgScale(WgLine(0, 0, 400, 0, 101), 0.3));
        var sine = WgBuild(WgSine(0, 400, 2, 30.0, 40.0));

        CheckEqual("WG 引擎：同一笔与自己距离为 0", straight.DistanceTo(straight), 0.0);
        Check("WG 引擎：首尾颠倒不算同一笔",
            straight.DistanceTo(straightRev) >= WgEngine.MaxDistance - 1e-9,
            WgFmt(straight.DistanceTo(straightRev)));
        Check("WG 引擎：放大 3 倍仍是同一笔（弧长归一化）",
            straight.DistanceTo(straight3) < 1e-9, WgFmt(straight.DistanceTo(straight3)));
        Check("WG 引擎：缩小 0.3 倍仍是同一笔",
            straight.DistanceTo(straight03) < 1e-9, WgFmt(straight.DistanceTo(straight03)));

        // 正弦波 122 点 vs 直线 2 点：源码的"点数比 > 10 倍即不可比"先把它挡掉了，
        // 所以这里拿到的是 +∞ 而不是一个很大的有限值。两者都算"明显不同"。
        double waveVsLine = sine.DistanceTo(straight);
        Check("WG 引擎：正弦波不会认成直线",
            double.IsPositiveInfinity(waveVsLine) || waveVsLine > WgEngine.DefaultThreshold,
            WgFmt(waveVsLine));

        // ---- 平移不变 ----
        var polyPts = WgPolyline(5, new double[] { 0, 0, 200, 0, 200, 200, 400, 200 });
        var poly = WgBuild(polyPts);
        var polyShift = WgBuild(WgTranslate(polyPts, 123, -77));
        CheckEqual("WG 引擎：同形平移距离为 0", poly.DistanceTo(polyShift), 0.0);

        // ---- 真人手绘：同一条形状录两次（模板/输入），都带 ±2px 抖动 ----
        // 这是源码能工作的真实前提：模板本身也是录下来的手绘，点数与输入同量级。
        var tLine = WgBuild(WgJitter(WgLine(0, 0, 400, 0, 81), 2.0, 12));
        var tL = WgBuild(WgJitter(polyPts, 2.0, 11));
        var tWave = WgBuild(WgJitter(WgSine(0, 400, 5, 60.0, 42.0), 2.0, 13));
        var dLine = WgBuild(WgJitter(WgLine(0, 0, 400, 0, 79), 2.0, 22));
        var dL = WgBuild(WgJitter(polyPts, 2.0, 21));
        var dWave = WgBuild(WgJitter(WgSine(0, 400, 5, 60.0, 42.0), 2.0, 23));

        Check("WG 引擎：同一手势手抖两次仍是同一手势",
            dL.DistanceTo(tL) < WgEngine.DefaultThreshold &&
            dLine.DistanceTo(tLine) < WgEngine.DefaultThreshold &&
            dWave.DistanceTo(tWave) < WgEngine.DefaultThreshold,
            "L 形 " + WgFmt(dL.DistanceTo(tL)) + " / 横线 " + WgFmt(dLine.DistanceTo(tLine)) +
            " / 波浪 " + WgFmt(dWave.DistanceTo(tWave)));

        // 异形之间必须拉开：实测最小的一对是 0.071，同形最大 0.017，
        // 这 4 倍差距就是 0.05 这个门限的立足点。
        Check("WG 引擎：不同手势之间的距离远大于门限",
            tLine.DistanceTo(dL) > WgEngine.DefaultThreshold &&
            tLine.DistanceTo(dWave) > WgEngine.DefaultThreshold &&
            tL.DistanceTo(dWave) > WgEngine.DefaultThreshold,
            WgFmt(tLine.DistanceTo(dL)) + " / " + WgFmt(tLine.DistanceTo(dWave)) + " / " +
            WgFmt(tL.DistanceTo(dWave)));

        var templates = new List<WgPath>
        {
            WgPath.FromStroke(tLine),
            WgPath.FromStroke(tL),
            WgPath.FromStroke(tWave)
        };
        CheckEqual("WG 引擎：横线命中横线模板",
            WgEngine.BestMatch(templates, WgPath.FromStroke(dLine), WgEngine.DefaultThreshold), 0);
        CheckEqual("WG 引擎：L 形命中 L 形模板",
            WgEngine.BestMatch(templates, WgPath.FromStroke(dL), WgEngine.DefaultThreshold), 1);
        CheckEqual("WG 引擎：波浪命中波浪模板",
            WgEngine.BestMatch(templates, WgPath.FromStroke(dWave), WgEngine.DefaultThreshold), 2);

        // ---- 各种"不可比"：必须 +∞，不能是 NaN，也不能是个混得过门限的小数 ----
        var dot = WgBuild(new List<WgVec2> { new WgVec2(10, 10) });
        var still = WgBuild(new List<WgVec2> { new WgVec2(50, 50), new WgVec2(50, 50), new WgVec2(50, 50) });
        Check("WG 引擎：单点笔不可比",
            double.IsPositiveInfinity(dot.DistanceTo(straight)), WgFmt(dot.DistanceTo(straight)));
        Check("WG 引擎：零长度笔不可比（NaN 必须堵掉）",
            double.IsPositiveInfinity(still.DistanceTo(still)), WgFmt(still.DistanceTo(still)));
        var saw129 = WgBuild(WgSaw(129));
        var saw128 = WgBuild(WgSaw(128));
        Check("WG 引擎：模板点数上限 128",
            double.IsPositiveInfinity(saw129.DistanceTo(saw129)) &&
            !double.IsPositiveInfinity(saw128.DistanceTo(saw128)),
            WgFmt(saw129.DistanceTo(saw129)) + " / " + WgFmt(saw128.DistanceTo(saw128)));
        Check("WG 引擎：点数相差 10 倍以上不可比",
            double.IsPositiveInfinity(WgBuild(WgSaw(30)).DistanceTo(
                WgBuild(new List<WgVec2> { new WgVec2(0, 0), new WgVec2(30, 0) }))));

        // ---- 多步路径：步数必须相等、角必须是同一个角 ----
        var pathStroke = WgPath.FromStroke(poly);
        var pathCorner = new WgPath().Add(new WgStrokeStep(poly)).Add(new WgCornerStep(WgCorner.LeftTop));
        var pathCornerSame = new WgPath().Add(new WgStrokeStep(polyShift)).Add(new WgCornerStep(WgCorner.LeftTop));
        var pathCornerOther = new WgPath().Add(new WgStrokeStep(poly)).Add(new WgCornerStep(WgCorner.RightBottom));
        CheckEqual("WG 路径：笔画 + 同角两步距离为 0", pathCorner.DistanceTo(pathCornerSame), 0.0);
        Check("WG 路径：角不同即不可比",
            double.IsPositiveInfinity(pathCorner.DistanceTo(pathCornerOther)));
        Check("WG 路径：步数不等即不可比",
            double.IsPositiveInfinity(pathStroke.DistanceTo(pathCorner)));
        Check("WG 路径：前缀匹配只看结构",
            pathCorner.StartsWith(pathStroke) && pathStroke.StartsWith(pathStroke) &&
            !pathStroke.StartsWith(pathCorner));
        CheckEqual("WG 路径：克隆后与原件等价", pathCorner.Clone().DistanceTo(pathCorner), 0.0);

        // ---- 诊断串：识别不灵时第一眼要看的东西 ----
        string desc = WgEngine.Describe(poly);
        Check("WG 引擎：诊断串含点数与长度",
            desc.StartsWith("4", StringComparison.Ordinal) &&
            desc.IndexOf("点 长度", StringComparison.Ordinal) > 0,
            desc);

        // ---- 门限收紧后，同一条手绘也必须被拒（说明门限是真的在起作用） ----
        Check("WG 引擎：门限收紧后同一手势也会被拒",
            WgEngine.BestMatch(templates, WgPath.FromStroke(dL), 0.005) < 0,
            WgFmt(dL.DistanceTo(tL)));
    }

    // ---------------- 数据构造（全部走 Add，与实时识别同一条管线） ----------------

    private static WgStroke WgBuild(List<WgVec2> pts)
    {
        var s = new WgStroke();
        for (int i = 0; i < pts.Count; i++)
        {
            s.Add(pts[i].X, pts[i].Y);
        }

        return s;
    }

    private static List<WgVec2> WgLine(double x0, double y0, double x1, double y1, int n)
    {
        var pts = new List<WgVec2>(n);
        for (int i = 0; i < n; i++)
        {
            double t = n == 1 ? 0.0 : (double)i / (n - 1);
            pts.Add(new WgVec2(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t));
        }

        return pts;
    }

    private static List<WgVec2> WgSine(double x0, double x1, double step, double amp, double period)
    {
        var pts = new List<WgVec2>();
        for (double x = x0; x <= x1 + 1e-9; x += step)
        {
            pts.Add(new WgVec2(x, amp * Math.Sin(x / period)));
        }

        return pts;
    }

    /// <summary>按步长采样一串折线段（x0,y0,x1,y1,x2,y2,…）。</summary>
    private static List<WgVec2> WgPolyline(double step, double[] xy)
    {
        var pts = new List<WgVec2>();
        for (int i = 0; i + 3 < xy.Length; i += 2)
        {
            double x0 = xy[i];
            double y0 = xy[i + 1];
            double x1 = xy[i + 2];
            double y1 = xy[i + 3];
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int n = Math.Max(1, (int)Math.Round(len / step));
            for (int k = 0; k < n; k++)
            {
                double t = (double)k / n;
                pts.Add(new WgVec2(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t));
            }
        }

        pts.Add(new WgVec2(xy[xy.Length - 2], xy[xy.Length - 1]));
        return pts;
    }

    /// <summary>强锯齿：保证 18° 合并吃不掉点，用来打点数上限/点数比这两道门。</summary>
    private static List<WgVec2> WgSaw(int n)
    {
        var pts = new List<WgVec2>(n);
        for (int i = 0; i < n; i++)
        {
            pts.Add(new WgVec2(i * 3, (i % 2 == 0) ? 0 : 60));
        }

        return pts;
    }

    private static List<WgVec2> WgScale(List<WgVec2> pts, double k)
    {
        var r = new List<WgVec2>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            r.Add(new WgVec2(pts[i].X * k, pts[i].Y * k));
        }

        return r;
    }

    private static List<WgVec2> WgTranslate(List<WgVec2> pts, double dx, double dy)
    {
        var r = new List<WgVec2>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            r.Add(new WgVec2(pts[i].X + dx, pts[i].Y + dy));
        }

        return r;
    }

    /// <summary>模拟手抖：每个采样点加 ±amp 均匀噪声，种子固定以便复现。</summary>
    private static List<WgVec2> WgJitter(List<WgVec2> pts, double amp, int seed)
    {
        var rnd = new Random(seed);
        var r = new List<WgVec2>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            double dx = (rnd.NextDouble() * 2.0 - 1.0) * amp;
            double dy = (rnd.NextDouble() * 2.0 - 1.0) * amp;
            r.Add(new WgVec2(pts[i].X + dx, pts[i].Y + dy));
        }

        return r;
    }

    private static string WgFmt(double v)
    {
        if (double.IsPositiveInfinity(v)) { return "+inf"; }
        if (double.IsNaN(v)) { return "NaN"; }
        return v.ToString("0.000000", CultureInfo.InvariantCulture);
    }
}
