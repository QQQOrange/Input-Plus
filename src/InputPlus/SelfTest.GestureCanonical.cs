using System;
using System.Collections.Generic;
using System.Linq;
using InputPlus.Core.Gestures;

namespace InputPlus;

/// <summary>
/// 轨迹校正（<see cref="StrokeCanonicalizer"/>）的自检：掰直、保形、两道守门、退化输入，
/// 以及用户最在意的那条 —— 画歪 12° 的竖线仍然能命中严格竖直的模板。
///
/// 断言口径全部落在"几何上可以验证的事实"上：X 跨度、Y 跨度、段数、方向角是不是 45 的整数倍、
/// 方向串前后是否一模一样。不比较点序列本身，免得以后重采样间距一调就红。
/// </summary>
public static partial class SelfTest
{
    private static void TestGestureCanonical()
    {
        TestCanonicalAxisSnap();
        TestCanonicalDiagonal();
        TestCanonicalCorners();
        TestCanonicalLetters();
        TestCanonicalMatch();
        TestCanonicalGates();
        TestCanonicalDegenerate();
    }

    // ── 1 / 2. 竖线、横线吸到轴上 ────────────────────────────────

    private static void TestCanonicalAxisSnap()
    {
        /* 偏 8° 的竖线：画的时候手往右歪一点，是最典型的"不是 90°"。 */
        var tilted = Stroke(new GestureMath.Pt(140, 120), Ray(new GestureMath.Pt(140, 120), 82, 420));
        var straight = StrokeCanonicalizer.Canonicalize(tilted);

        double spanX = Spread(straight, p => p.X);
        Check("8° 竖线校正后所有点 X 相同（±0.01）", spanX <= 0.01, $"X 跨度 {spanX:F6}");
        Check("8° 竖线校正后方向串不变", PatternOf(straight) == PatternOf(tilted),
            $"{PatternOf(tilted)} → {PatternOf(straight)}");
        Check("8° 竖线确实被掰直了（不是守门退回原样）", !SamePoints(tilted, straight),
            $"点数 {tilted.Count} → {straight.Count}，原图 X 跨度 {Spread(tilted, p => p.X):F1}");
        Check("8° 竖线方向没变（还是往下）",
            straight.Count >= 2 && straight[straight.Count - 1].Y > straight[0].Y);

        /* 偏 10° 的横线，同理。 */
        var flat = Stroke(new GestureMath.Pt(100, 300), Ray(new GestureMath.Pt(100, 300), 10, 420));
        var flatOut = StrokeCanonicalizer.Canonicalize(flat);
        double spanY = Spread(flatOut, p => p.Y);
        Check("10° 横线校正后所有点 Y 相同（±0.01）", spanY <= 0.01, $"Y 跨度 {spanY:F6}");
        Check("10° 横线校正后方向串不变", PatternOf(flatOut) == PatternOf(flat),
            $"{PatternOf(flat)} → {PatternOf(flatOut)}");
        Check("10° 横线确实被掰平了", !SamePoints(flat, flatOut),
            $"点数 {flat.Count} → {flatOut.Count}");

        /* 幂等：校正过的轨迹再校正一次不能变形（模板和手绘两侧各跑一次也不会漂）。 */
        var again = StrokeCanonicalizer.Canonicalize(straight);
        bool same = again.Count == straight.Count && SamePoints(again, straight);
        Check("校正幂等（再校正一次不变形）", same, $"点数 {straight.Count} → {again.Count}");
    }

    // ── 3. 45° 斜线保持 45° ──────────────────────────────────────

    private static void TestCanonicalDiagonal()
    {
        /* 48° 落在"离 45° 只有 3°、离水平 42°"的区间：必须吸成 45°，不能被掰成水平。 */
        var diag = Stroke(new GestureMath.Pt(80, 80), Ray(new GestureMath.Pt(80, 80), 48, 400));
        var diagOut = StrokeCanonicalizer.Canonicalize(diag);
        var legs = RunsOf(diagOut);
        double angle = legs.Count > 0 ? legs[0].Angle : double.NaN;
        Check("48° 斜线吸成严格的 45°", legs.Count == 1 && Math.Abs(Math.Abs(angle) - 45.0) <= 1e-6,
            $"段数 {legs.Count}，方向角 {angle:F4}°");
        Check("45° 斜线没被掰成水平 / 垂直", Math.Abs(angle - 0) > 1 && Math.Abs(Math.Abs(angle) - 90) > 1,
            $"{angle:F4}°");
        Check("45° 斜线方向串不变", PatternOf(diagOut) == PatternOf(diag),
            $"{PatternOf(diag)} → {PatternOf(diagOut)}");

        /* 30° 既不在 22° 轴窗里、也不在 12° 斜窗里 —— 有些手势本来就是斜的，别硬掰。 */
        var odd = Stroke(new GestureMath.Pt(100, 100), Ray(new GestureMath.Pt(100, 100), 30, 400));
        var oddOut = StrokeCanonicalizer.Canonicalize(odd);
        var oddLegs = RunsOf(oddOut);
        Check("30° 斜线保持原方向（不在吸附窗口内）",
            oddLegs.Count == 1 && Math.Abs(oddLegs[0].Angle - 30.0) <= 1e-6,
            $"段数 {oddLegs.Count}，方向角 {(oddLegs.Count > 0 ? oddLegs[0].Angle : double.NaN):F4}°");
    }

    // ── 4. L 形：偏 3° + 偏 7° → 严格直角 ───────────────────────

    private static void TestCanonicalCorners()
    {
        var corner = Ray(new GestureMath.Pt(120, 100), 87, 300);      // 往下，偏 3°
        var end = Ray(corner, -7, 300);                               // 往右，偏 7°
        var l = Stroke(new GestureMath.Pt(120, 100), corner, end);
        var lOut = StrokeCanonicalizer.Canonicalize(l);
        var legs = RunsOf(lOut);

        Check("L 形校正后仍是 2 段（拐点只有一个）", legs.Count == 2, $"段数 {legs.Count}");
        bool right = legs.Count == 2 &&
                     Math.Abs(legs[0].Dx * legs[1].Dx + legs[0].Dy * legs[1].Dy) /
                     (legs[0].Len * legs[1].Len) <= 1e-6;
        Check("L 形两段是严格直角", right,
            legs.Count == 2 ? $"点积归一 {(legs[0].Dx * legs[1].Dx + legs[0].Dy * legs[1].Dy) / (legs[0].Len * legs[1].Len):E3}" : "");
        bool axes = legs.Count == 2 && Math.Abs(legs[0].Dx) <= 1e-6 && Math.Abs(legs[1].Dy) <= 1e-6;
        Check("L 形第一段竖直、第二段水平", axes,
            legs.Count == 2 ? $"段1 ({legs[0].Dx:E2},{legs[0].Dy:F1}) 段2 ({legs[1].Dx:F1},{legs[1].Dy:E2})" : "");
        Check("L 形方向串不变", PatternOf(lOut) == PatternOf(l),
            $"{PatternOf(l)} → {PatternOf(lOut)}");
    }

    // ── 5. 字母：Z / C / S ───────────────────────────────────────

    private static void TestCanonicalLetters()
    {
        /* Z：→ 偏 6°、↙ 偏 9°、→ 偏 5°。三段都得落在水平 / 45° 上。
         * 中间那道斜线特意取 141°（偏 9°）：GesturePattern 的 20px 起手窗口在拐角
         * 处量的是"上一段 + 这一段的混合向量"，斜线偏得多（例如 128°）会被报成 ↓、
         * 吸附成精确 135° 后又报成 ↙ —— 方向串一改，守门① 就整条退回原样。
         * 141° 前后都报 ↙，正好能验证"吸附之后方向语义没变"。 */
        var z = Stroke(
            new GestureMath.Pt(100, 100),
            Ray(new GestureMath.Pt(100, 100), 6, 220),
            Ray(Ray(new GestureMath.Pt(100, 100), 6, 220), 141, 240),
            Ray(Ray(Ray(new GestureMath.Pt(100, 100), 6, 220), 141, 240), -5, 220));
        var zOut = StrokeCanonicalizer.Canonicalize(z);
        var zLegs = RunsOf(zOut);
        Check("Z 形段数不变（3 段）", zLegs.Count == 3, $"段数 {zLegs.Count}");
        Check("Z 形方向串不变", PatternOf(zOut) == PatternOf(z),
            $"{PatternOf(z)} → {PatternOf(zOut)}");
        Check("Z 形每段都严格水平 / 垂直 / 45°", zLegs.Count == 3 && zLegs.All(Aligned),
            $"{PatternOf(z)} → {PatternOf(zOut)}  ∠ " + string.Join(" ", zLegs.Select(g => g.Angle.ToString("F2"))));

        /* C：← 偏 8°、↓ 偏 7°、→ 偏 3° —— 三口都轴对齐。 */
        var cStart = new GestureMath.Pt(320, 90);
        var c1 = Ray(cStart, 188, 260);
        var c2 = Ray(c1, 97, 260);
        var c = Stroke(cStart, c1, c2, Ray(c2, 357, 260));
        var cOut = StrokeCanonicalizer.Canonicalize(c);
        var cLegs = RunsOf(cOut);
        Check("C 形段数不变（3 段）", cLegs.Count == 3, $"段数 {cLegs.Count}");
        Check("C 形方向串不变", PatternOf(cOut) == PatternOf(c),
            $"{PatternOf(c)} → {PatternOf(cOut)}");
        Check("C 形每段都严格水平 / 垂直", cLegs.Count == 3 && cLegs.All(g => Math.Abs(g.Angle - Math.Round(g.Angle / 90.0) * 90.0) <= 1e-6),
            string.Join(" ", cLegs.Select(g => g.Angle.ToString("F2"))));

        /* S：来回折一条线（→ ← →），偏 5~6°。 */
        var sStart = new GestureMath.Pt(100, 90);
        var s1 = Ray(sStart, 6, 200);
        var s2 = Ray(s1, 186, 200);
        var s = Stroke(sStart, s1, s2, Ray(s2, -5, 200));
        var sOut = StrokeCanonicalizer.Canonicalize(s);
        var sLegs = RunsOf(sOut);
        Check("S 形段数不变（3 段，回折不合并）", sLegs.Count == 3, $"段数 {sLegs.Count}");
        Check("S 形方向串不变", PatternOf(sOut) == PatternOf(s),
            $"{PatternOf(s)} → {PatternOf(sOut)}");
        Check("S 形每段都严格轴对齐", sLegs.Count == 3 && sLegs.All(Aligned),
            string.Join(" ", sLegs.Select(g => g.Angle.ToString("F2"))));
    }

    // ── 6. 用户最在意的那条：画歪还能不能触发 ───────────────────

    private static void TestCanonicalMatch()
    {
        /* 模板是"严格竖直"，用户实际会画成偏 12°。两侧走同一个校正函数，
         * 校正后都是同一条竖线，所以必须命中。 */
        var templatePath = GestureMath.ToFlat(
            GestureMath.Normalize(Stroke(new GestureMath.Pt(600, 200), Ray(new GestureMath.Pt(600, 200), 90, 400))));

        MouseGesture MakeTemplate() => new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = "自检-竖线",
            Kind = "stroke",
            Path = new List<int>(templatePath),
            Pattern = "↓"
        };

        var on = MakeTemplate();
        var off = MakeTemplate();
        var drawn = Stroke(new GestureMath.Pt(500, 300), Ray(new GestureMath.Pt(500, 300), 102, 400));   // 偏 12°

        var (hit, score) = GestureEngine.Match(new[] { on }, drawn, GestureOptions.Default);
        double dOn = GestureEngine.Distance(on, drawn);

        double dOff;
        bool flag = GestureEngine.CanonicalizeEnabled;
        try
        {
            GestureEngine.CanonicalizeEnabled = false;
            dOff = GestureEngine.Distance(off, drawn);
        }
        finally
        {
            GestureEngine.CanonicalizeEnabled = flag;
        }

        Check("画歪 12° 的竖线仍命中严格竖直模板", ReferenceEquals(hit, on),
            $"score={score:F4}，距离={dOn:F4}（阈值 {GestureOptions.Default.WgThreshold:F3}）");
        Check("校正后距离比不校正更贴合模板", dOn <= dOff + 1e-9,
            $"校正 {dOn:F4} / 不校正 {dOff:F4}");
        Check("校正后距离落在阈值内（宽容度）", dOn <= GestureOptions.Default.WgThreshold,
            $"距离 {dOn:F4} vs 阈值 {GestureOptions.Default.WgThreshold:F3}");
    }

    // ── 7. 守门：会被掰坏的方向必须原样返回 ─────────────────────

    private static void TestCanonicalGates()
    {
        /* 偏 22°：正好在吸附容差边界上，"吸过去"会把端点横移 ~19% 长度，
         * 超过守门②的 15% —— 这时候宁可不动。 */
        var edge = Stroke(new GestureMath.Pt(200, 120), Ray(new GestureMath.Pt(200, 120), 68, 400));
        var edgeOut = StrokeCanonicalizer.Canonicalize(edge);
        Check("守门②：22° 边界斜线（吸过去会挪 19%）原样返回", SamePoints(edge, edgeOut),
            $"X 跨度 {Spread(edgeOut, p => p.X):F2}，点数 {edge.Count} → {edgeOut.Count}");

        /* 同一个输入把守门②放宽，它确实会被吸成竖线 —— 证明是守门拦下的，不是没吸附。 */
        var wide = StrokeCanonicalizer.Canonicalize(edge,
            new StrokeCanonicalizer.CanonicalizeOptions { DeviationRatio = 1.0 });
        Check("守门②放宽后同一条线确实被吸成竖直（守门是唯一原因）",
            Spread(wide, p => p.X) <= 0.01 && !SamePoints(edge, wide),
            $"X 跨度 {Spread(wide, p => p.X):F4}");

        /* 守门①：真实的 128° 斜线 Z。GesturePattern 的 20px 起手窗口在拐角处量的是
         * "上一段 + 这一段"的混合向量，128° 会被读成 ↓；吸附成精确 135° 之后读成 ↙。
         * 方向串一改，整条退回原样 —— 宁可不校正，也不把 Z 掰成别的方向序列。 */
        var sharp = Stroke(
            new GestureMath.Pt(100, 100),
            Ray(new GestureMath.Pt(100, 100), 3, 220),
            Ray(Ray(new GestureMath.Pt(100, 100), 3, 220), 128, 240),
            Ray(Ray(Ray(new GestureMath.Pt(100, 100), 3, 220), 128, 240), -4, 220));
        var sharpOut = StrokeCanonicalizer.Canonicalize(sharp);
        Check("守门①：方向串会被改掉的 128° 斜线 Z 原样返回", SamePoints(sharp, sharpOut),
            $"{PatternOf(sharp)} → {PatternOf(sharpOut)}，点数 {sharp.Count} → {sharpOut.Count}");

        /* 守门② 放到无穷大，它照样原样返回 —— 拦下它的是守门①，不是偏差。 */
        var sharpLoose = StrokeCanonicalizer.Canonicalize(sharp,
            new StrokeCanonicalizer.CanonicalizeOptions { DeviationLimitOverride = 1e9 });
        Check("守门①是唯一原因：偏差上限放到无穷大照样原样返回",
            SamePoints(sharp, sharpLoose), PatternOf(sharpLoose));

        /* 用 deviationLimit 直接卡死：照抄 StrokeSmoother 的自检写法。 */
        var tilted = Stroke(new GestureMath.Pt(140, 120), Ray(new GestureMath.Pt(140, 120), 82, 420));
        var blocked = StrokeCanonicalizer.Canonicalize(tilted,
            new StrokeCanonicalizer.CanonicalizeOptions { DeviationLimitOverride = 0 });
        Check("守门②：偏差上限为 0 时 8° 竖线原样返回", SamePoints(tilted, blocked),
            $"点数 {tilted.Count} → {blocked.Count}");
    }

    // ── 8. 退化输入 ─────────────────────────────────────────────

    private static void TestCanonicalDegenerate()
    {
        bool ok = true;
        string detail = "";
        try
        {
            var empty = StrokeCanonicalizer.Canonicalize(new List<GestureMath.Pt>());
            var nul = StrokeCanonicalizer.Canonicalize(null!);
            var one = StrokeCanonicalizer.Canonicalize(new[] { new GestureMath.Pt(10, 10) });
            var two = StrokeCanonicalizer.Canonicalize(new[]
            {
                new GestureMath.Pt(10, 10), new GestureMath.Pt(40, 14)
            });
            var dot = new List<GestureMath.Pt>();
            for (int i = 0; i < 50; i++) dot.Add(new GestureMath.Pt(20, 20));
            var dotOut = StrokeCanonicalizer.Canonicalize(dot);

            ok = empty.Count == 0 && nul.Count == 0 && one.Count == 1 && two.Count == 2 &&
                 dotOut.Count == 50;
            detail = $"空 {empty.Count} / null {nul.Count} / 1点 {one.Count} / 2点 {two.Count} / 原地 50 点 → {dotOut.Count}";
        }
        catch (Exception ex)
        {
            ok = false;
            detail = ex.GetType().Name + ": " + ex.Message;
        }
        Check("退化输入（null / 空 / 1 点 / 2 点 / 原地画点）不抛异常且点数不变", ok, detail);
    }

    // ── 造数据与小工具 ──────────────────────────────────────────

    /// <summary>从 start 沿 deg 方向走 length，返回终点（屏幕坐标系，y 向下）。</summary>
    private static GestureMath.Pt Ray(GestureMath.Pt start, double deg, double length)
    {
        double r = deg * Math.PI / 180.0;
        return new GestureMath.Pt(start.X + Math.Cos(r) * length, start.Y + Math.Sin(r) * length);
    }

    /// <summary>把折线的每个拐角按 2px 采样成一条"手绘"轨迹（接缝只留一个点）。</summary>
    private static List<GestureMath.Pt> Stroke(params GestureMath.Pt[] corners)
    {
        var list = new List<GestureMath.Pt>();
        for (int i = 0; i < corners.Length - 1; i++)
        {
            var a = corners[i];
            var b = corners[i + 1];
            double len = (b - a).Length;
            int n = Math.Max(2, (int)Math.Ceiling(len / 2.0));
            for (int k = i == 0 ? 0 : 1; k <= n; k++)
            {
                double t = (double)k / n;
                list.Add(new GestureMath.Pt(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }
        return list;
    }

    /// <summary>一条"边"：累积方向向量。转角超过 20° 就切段。</summary>
    private readonly record struct Leg(double Dx, double Dy)
    {
        public double Len => Math.Sqrt(Dx * Dx + Dy * Dy);
        public double Angle => Math.Atan2(Dy, Dx) * 180.0 / Math.PI;
    }

    private static List<Leg> RunsOf(IReadOnlyList<GestureMath.Pt> pts)
    {
        var legs = new List<Leg>();
        for (int i = 1; i < pts.Count; i++)
        {
            var d = pts[i] - pts[i - 1];
            if (d.Length < 1e-9) continue;
            if (legs.Count > 0)
            {
                var prev = legs[legs.Count - 1];
                if (TurnDegOf(prev.Dx, prev.Dy, d.X, d.Y) <= 20.0)
                {
                    legs[legs.Count - 1] = new Leg(prev.Dx + d.X, prev.Dy + d.Y);
                    continue;
                }
            }
            legs.Add(new Leg(d.X, d.Y));
        }
        return legs;
    }

    private static double TurnDegOf(double ax, double ay, double bx, double by)
    {
        double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
        if (la < 1e-9 || lb < 1e-9) return 0;
        double cos = Math.Clamp((ax * bx + ay * by) / (la * lb), -1, 1);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    /// <summary>方向是不是 45 的整数倍（水平 / 垂直 / 45° 斜线）。</summary>
    private static bool Aligned(Leg leg)
        => Math.Abs(leg.Angle - Math.Round(leg.Angle / 45.0) * 45.0) <= 1e-6;

    private static double Spread(IReadOnlyList<GestureMath.Pt> pts, Func<GestureMath.Pt, double> selector)
        => pts.Count == 0 ? 0 : pts.Max(selector) - pts.Min(selector);

    private static bool SamePoints(IReadOnlyList<GestureMath.Pt> a, IReadOnlyList<GestureMath.Pt> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if ((a[i] - b[i]).Length > 1e-9) return false;
        return true;
    }

    private static string PatternOf(IReadOnlyList<GestureMath.Pt> pts) => GesturePattern.FromPoints(pts);
}
