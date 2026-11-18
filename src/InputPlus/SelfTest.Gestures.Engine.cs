using System;
using System.Collections.Generic;
using System.Linq;
using InputPlus.Core.Gestures;
using InputPlus.Core.Gestures.WG;
using InputPlus.Core.Settings;
using InputPlus.UI;

namespace InputPlus;

/// <summary>
/// 自检：手势识别引擎接入层（<see cref="GestureEngine"/>）。
///
/// 这里验的不是 WGestures 引擎内部的 DP（那是 <c>TestWGesturesEngine</c> 的事），
/// 而是"接进来之后还讲不讲道理"：
///   · 同样的形状、画大了画偏了，必须还认得；
///   · 反向画、换个形状，必须不认；
///   · 只有方向串、没存轨迹的老手势把方向串合成折线，进同一个引擎命中；
///   · 空手势、通配手势没有形状，不参与比较。
/// </summary>
public static partial class SelfTest
{
    private static MouseGesture PathGesture(params double[] flat)
    {
        return new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = "自检手势",
            Kind = "stroke",
            Path = flat.Select(v => (int)Math.Round(v)).ToList(),
        };
    }

    /// <summary>把一串真实屏幕坐标按录制时的口径存成手势（归一化 + 扁平化）。</summary>
    private static MouseGesture RecentredGesture(params (double X, double Y)[] screen)
    {
        var pts = screen.Select(p => new GestureMath.Pt(p.X, p.Y)).ToList();
        return new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = "自检手势",
            Kind = "stroke",
            Path = GestureMath.ToFlat(GestureMath.Normalize(pts)),
        };
    }

    private static List<GestureMath.Pt> Pts(params (double X, double Y)[] screen)
        => screen.Select(p => new GestureMath.Pt(p.X, p.Y)).ToList();

    private static void TestGestureEngine()
    {
        var opts = GestureOptions.Default;

        // ---- 只有一套识别引擎：阈值与引擎常量对齐，立即执行阈值更严 ----
        CheckEqual("默认阈值等于引擎常量", opts.WgThreshold, WgEngine.DefaultThreshold);
        Check("立即执行阈值比松开判定更严", opts.WgImmediateThreshold < opts.WgThreshold);

        /* ---- 只有方向串的老手势：合成折线，进同一个引擎 ----
         * v5.13 把"形状没中就退回方向串"的第二层删掉了，这类手势也得有个形状：
         * 方向串画成等长折线（GesturePattern.Sketch）再交给引擎。引擎先按 18° 合并，
         * 折线是 3 个点、手画同样两个弯的一笔合并后也是 3~5 个点，点数比在 10 倍闸门以内。 */
        var upRight = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = "↑→",
            Kind = "stroke",
            Pattern = "↑→",
        };
        Check("只有方向串的手势也能合成几何模板", GestureEngine.CanMatch(upRight),
            $"合成 {GestureEngine.ShapeOf(upRight)?.Count ?? 0} 个点");

        var patSettings = new AppSettings();
        patSettings.Gestures.Add(upRight);
        var drawnUpRight = Pts((400, 400), (400, 260), (400, 200), (520, 200), (600, 200));
        var patHit = GestureService.PickStroke(
            patSettings, 0x02, "", drawnUpRight, patSettings.GestureOptions);
        Check("只有方向串的手势按合成折线命中", patHit.Hit?.Id == upRight.Id,
            patHit.Hit?.Name ?? $"null（偏差 {patHit.Score:0.000}）");

        // ---- 拐错方向不能认成同一条 ----
        var drawnUpLeft = Pts((400, 400), (400, 200), (200, 200));
        var wrongHit = GestureService.PickStroke(
            patSettings, 0x02, "", drawnUpLeft, patSettings.GestureOptions);
        Check("拐错方向不认「↑→」", wrongHit.Hit == null, wrongHit.Hit?.Name ?? "null");

        // ---- 存过轨迹的手势：画大两倍、位置挪开，仍然认得 ----
        var rightThenDown = RecentredGesture((0, 0), (300, 0), (300, 100));
        var scaled = Pts((500, 300), (1100, 300), (1100, 500));
        var (hit2, score2) = GestureEngine.Match(new[] { rightThenDown }, scaled, opts);
        Check("WG 引擎认得放大平移后的同一笔", ReferenceEquals(hit2, rightThenDown), $"score={score2:0.000}, 距离={GestureEngine.Distance(rightThenDown, scaled):0.000}");

        // ---- 手抖几个像素不该影响判定 ----
        var noisy = Pts((500, 300), (800, 303), (1100, 297), (1101, 400), (1099, 500));
        var (hit3, _) = GestureEngine.Match(new[] { rightThenDown }, noisy, opts);
        Check("±5px 手抖仍然认得", ReferenceEquals(hit3, rightThenDown), $"距离={GestureEngine.Distance(rightThenDown, noisy):0.000}");

        // ---- 反着画不是同一笔 ----
        var reversed = Pts((1100, 500), (1100, 300), (500, 300));
        var (hit4, _) = GestureEngine.Match(new[] { rightThenDown }, reversed, opts);
        Check("反向画不认同一笔", hit4 == null, $"距离={GestureEngine.Distance(rightThenDown, reversed):0.000}");

        // ---- 形状不同（直线 vs 折线）不认 ----
        var straightDown = Pts((800, 100), (800, 400), (800, 700));
        var (hit5, _) = GestureEngine.Match(new[] { rightThenDown }, straightDown, opts);
        Check("直线不认成「右再下」", hit5 == null, $"距离={GestureEngine.Distance(rightThenDown, straightDown):0.000}");

        // ---- 多条候选里挑最像的那条 ----
        /* 诱饵必须换一套方向：WG 的距离是 ∫(Δθ)²ds，s 方向可以 warp，
         * 所以「先右再下」不管拐点在前 75% 还是前 85% 距离都是 0（同一段方向序列）。
         * 早先拿 (0,0)->(900,0)->(900,150) 当诱饵，它和正解方向序列相同，引擎分不出来。 */
        var straightShorter = RecentredGesture((0, 0), (900, 0), (900, -150));
        var (best, _) = GestureEngine.Match(new[] { straightShorter, rightThenDown }, scaled, opts);
        Check("多条候选挑最近的一条", ReferenceEquals(best, rightThenDown), best?.Name ?? "null");

        // ---- 立即执行阈值必须比抬手阈值更严 ----
        Check("立即执行阈值更严", opts.WgImmediateThreshold < opts.WgThreshold,
            $"{opts.WgImmediateThreshold} vs {opts.WgThreshold}");

        // ---- 没有轨迹、也没有可解析方向串的手势不参与比较 ----
        var empty = new MouseGesture { Id = MouseGesture.NewId(), Name = "空", Kind = "stroke" };
        Check("空手势不参与引擎比较", !GestureEngine.CanMatch(empty));
        Check("方向串里混了通配合不出折线", GesturePattern.Sketch("*↑").Count == 0,
            $"{GesturePattern.Sketch("*↑").Count} 个点");
        Check("通配手势不参与引擎比较",
            !GestureEngine.CanMatch(new MouseGesture
            {
                Id = MouseGesture.NewId(), Name = "*", Kind = "stroke", Pattern = GesturePattern.Wildcard,
            }));

        // ---- 模板缓存要被指纹失效：改完轨迹必须按新形状算 ----
        var mutable = RecentredGesture((0, 0), (300, 0), (300, 100));
        double before = GestureEngine.Distance(mutable, straightDown);
        mutable.Path = GestureMath.ToFlat(GestureMath.Normalize(
            Pts((0, 0), (0, 100), (0, 300))));
        double after = GestureEngine.Distance(mutable, straightDown);
        Check("改轨迹后模板失效重建", before > after, $"{before:0.000} → {after:0.000}");
    }
}
