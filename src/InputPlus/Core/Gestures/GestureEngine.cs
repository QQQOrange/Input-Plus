using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using InputPlus.Core.Gestures.WG;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 手势识别接入层：把 Keyboard enhance 的 <see cref="MouseGesture"/> 喂进
/// WGestures 2 的笔画引擎（<see cref="WgEngine"/>），替换原来那套
/// FlowMouse 方向串 + 形状距离的双层判定。
///
/// 为什么换：老的判定先看方向串拐了几个弯，再看形状；两笔"看着一样、拐弯数不同"
/// 的手势会互相打架（↑→ 和 ↗→ 在方向串空间只差 22.5°）。WGestures 的引擎把笔画
/// 压成 (累计弧长 s, 方向角 θ) 曲线，用带 ε 的 DP 量两条曲线的差异 —— 用户画快了、
/// 画大了、歪了一点都还是同一条手势，而"拐弯顺序不同"会实打实地拉大距离。
///
/// 模板有两个来源，但**只有一套判定**：有存档轨迹的用
/// <see cref="MouseGesture.Path"/>（录制/导入时存下的归一化轨迹），只有方向串的存量
/// 手势用 <see cref="GesturePattern.Sketch"/> 把方向串画成等长折线当模板。后者也进
/// 这同一套引擎、这同一个阈值 —— 不再有"形状比一遍、方向串再比一遍"的第二条通路。
///
/// 从方向串合成的折线照样比得动：引擎先把一笔按 18° 合并成折线再比
/// (累计弧长 s, 方向角 θ) 曲线，一条 "↑→" 合并后是 3 个点，用户手画同样两个弯的一笔
/// 合并后也是 3~5 个点，两侧落在同一个量纲上，点数比远在引擎 10 倍门限以内。
///
/// 缓存：模板构建（18° 合并压点 + 弧长/角度签名）不便宜，而一次识别要遍历几十条。
/// 用 <see cref="ConditionalWeakTable{TKey,TValue}"/> 挂在手势对象上：不占序列化字段
/// （<see cref="MouseGesture.Clone"/>/<see cref="MouseGesture.CopyFrom"/> 不用管它），
/// 编辑手势时指纹失配就自动重建。
/// </summary>
public static class GestureEngine
{
    /// <summary>模板点数上限（引擎硬约束：参数侧超过 128 点直接判"不可比"）。</summary>
    public const int MaxTemplatePoints = WgStroke.MaxPoints;

    /// <summary>
    /// 轨迹校正开关。默认开：模板和手绘在 <see cref="StrokeOf"/> 里过同一个
    /// <see cref="StrokeCanonicalizer"/>，画歪的竖线会被吸成和模板同一条竖线。
    /// 自检会临时关掉它，用"不校正"的距离做对照。
    /// </summary>
    internal static bool CanonicalizeEnabled = true;

    /// <summary>
    /// 模板指纹：轨迹点数 + 内容哈希 + 方向串。
    /// 轨迹或方向串改了就要重建模板。
    /// </summary>
    private readonly record struct Signature(
        int PathCount,
        int PathHash,
        string Pattern);

    private sealed class Template
    {
        public Signature? Sig;
        public WgPath? Built;
    }

    private static readonly ConditionalWeakTable<MouseGesture, Template> Cache =
        new ConditionalWeakTable<MouseGesture, Template>();

    /// <summary>
    /// 存档轨迹的指纹（点数 + 内容哈希 + 方向串）。
    /// </summary>
    private static Signature Fingerprint(MouseGesture g)
    {
        var path = g.Path;
        return new Signature(
            path?.Count ?? 0, HashOf(path),
            g.Pattern ?? "");
    }

    private static int HashOf(List<int>? values)
    {
        int h = 17;
        int n = values?.Count ?? 0;
        for (int i = 0; i < n; i++) h = unchecked(h * 31 + values![i]);
        return h;
    }

    /// <summary>
    /// 取（或建）一条手势的识别模板。null 表示这条手势没法参与形状比较
    /// （既没有轨迹、方向串也是空的或通配）。
    /// </summary>
    public static WgPath? TemplateFor(MouseGesture g) => EntryFor(g)?.Built;

    /// <summary>
    /// 一条手势的几何形状（归一化到 1000 空间的折线），优先级：
    /// ① 存档轨迹（<see cref="MouseGesture.Path"/>）；
    /// ② 方向串合成的等长折线（早期版本、导入时没带点阵的配置）。
    /// 两种形状口径一致 —— 都过了一遍 <see cref="GestureMath.Normalize"/>。
    /// </summary>
    public static List<GestureMath.Pt>? ShapeOf(MouseGesture? g)
    {
        if (g == null) return null;
        if (g.HasPath) return g.Points();
        var sketch = GesturePattern.Sketch(g.Pattern);
        return sketch.Count >= 2 ? sketch : null;
    }

    /// <summary>取（或建）一条手势的模板条目（含"是不是闭环"这类附带信息）。</summary>
    private static Template? EntryFor(MouseGesture g)
    {
        if (g == null) return null;

        var sig = Fingerprint(g);

        var tpl = Cache.GetValue(g, _ => new Template());
        if (tpl.Sig is { } old && old.Equals(sig)) return tpl;

        var shape = ShapeOf(g);
        WgStroke? stroke = shape == null ? null : StrokeOf(shape);

        WgPath? built = null;
        if (stroke != null && stroke.Count >= 2 && stroke.Count <= MaxTemplatePoints)
            built = WgPath.FromStroke(stroke);

        tpl.Sig = sig;
        tpl.Built = built;

        return tpl;
    }

    /// <summary>这条手势有没有可比的几何模板（存过轨迹，或者有方向串能合出折线）。</summary>
    public static bool CanMatch(MouseGesture? g) => g != null && TemplateFor(g) != null;

    /// <summary>
    /// 把一条鼠标轨迹变成引擎的笔画。
    ///
    /// 两件事，缺一不可：
    /// 1. 先把抖动吃掉（见 <see cref="Simplify"/>）。钩子每 2px 记一个点，
    ///    而手感抖动就在 1~2px 这个量级上 —— 不处理的话每一小段的方向都
    ///    翻来覆去，18° 合并几乎一个点都吃不掉，一条 400px 的手势能压出
    ///    两百多个折点，直接撞上引擎"参数侧 &gt; 128 点不可比"的硬门限，
    ///    于是一律判未识别。这是真实的手势"时灵时不灵"的根子。
    /// 2. 再走 18° 合并（<see cref="WgStroke.FromNormalized"/>）。
    /// </summary>
    public static WgStroke? DrawnStroke(IReadOnlyList<GestureMath.Pt> points)
    {
        if (points == null || points.Count < 3) return null;
        return StrokeOf(points);
    }

    /// <summary>
    /// 把一条形状（手绘轨迹或手势模板）加工成引擎要的那一笔：
    /// 轨迹校正 → 平滑 → 按尺度丢抖动 → 归一化 → 18° 合并。
    ///
    /// **两侧必须走同一条路**：模板也是人手画下来存着的，里面同样带着手抖。
    /// 只平滑手绘那一侧，就会变成"干净直线 vs 抖动的模板"，距离反而更大 ——
    /// 这个坑真的踩过：平滑上线后，直线手势全好了，而带手抖模板的
    /// 「最大化」从 3/3 掉到 0/3。校正是同一个道理，而且要求更硬：
    /// 模板和手绘必须过**同一个函数**（见 <see cref="StrokeCanonicalizer"/>）。
    /// </summary>
    private static WgStroke? StrokeOf(IReadOnlyList<GestureMath.Pt> shape)
    {
        double extent = GestureMath.Extent(shape);
        if (extent < 1) return null;                              // 没动过的轨迹不是一笔

        /* 轨迹校正放在这里，是因为模板（EntryFor）和手绘（DrawnStroke）都从这条路走 ——
         * 一个入口，只校正一次，不会双重校正；缓存指纹仍按原始 Path 算，不受影响。
         * 守门不通过时 Canonicalize 原样返回，所以这一步对识别是"只赚不赔"。 */
        if (CanonicalizeEnabled) shape = StrokeCanonicalizer.Canonicalize(shape);

        /* 照抄 WG2：这里**不重采样、不平滑**。
         *
         * WG2 的识别输入就是「U.cs 那个 20 设备像素采样出来的点」直接过 18° 合并
         * （A/P.cs 的 A(Vec2)），中间没有任何等距化或滑动平均。
         * InputPlus 以前多做了 Resample + Smooth 两件事，删掉是为了让"你画什么"
         * 和"模板是什么"严格同源 —— 平滑会把直角磨圆、重采样会把短手势的折点摊平，
         * 两边一起偏之后阈值就没法对着 WG2 校准了。 */
        var norm = GestureMath.Normalize(shape);

        /* 容差跟整笔尺度走（归一化后最长边 1000）：4 个单位 ≈ 千分之四屏宽。 */
        double epsilon = Math.Max(0.75, GestureMath.Extent(norm) * 0.004);
        var stroke = Build(norm, epsilon);

        /* 兜底：WG2 在 A/P.cs:307 是"模板 >128 点直接不可比"，没有重压这一步。
         * 这里留着它只是不想让长手势一律判死（输入仍是 20px 采样点，一般情况下碰不到）；
         * 它只调 RDP 容差，不引入任何平滑。 */
        for (int guard = 0; stroke != null && stroke.Count > MaxTemplatePoints && guard < 8; guard++)
        {
            epsilon *= 1.7;
            stroke = Build(norm, epsilon);
        }

        return stroke == null || stroke.Count < 2 ? null : stroke;
    }

    private static WgStroke? Build(IReadOnlyList<GestureMath.Pt> points, double epsilon)
    {
        var thin = Simplify(points, epsilon);
        if (thin.Count < 2) return null;
        return WgStroke.FromNormalized(GestureMath.Normalize(thin));
    }

    /// <summary>
    /// 按"离直线多远"来丢点（Ramer–Douglas–Peucker）。
    /// 比按角度丢点稳：手抖是高频小幅垂直于笔迹方向的摆动，RDP 只看偏差，
    /// 而 18° 合并看的是方向，两者互补 —— 顺序上必须先 RDP 再合并。
    /// </summary>
    internal static List<GestureMath.Pt> Simplify(IReadOnlyList<GestureMath.Pt> points, double epsilon)
    {
        int n = points.Count;
        if (n < 3 || !(epsilon > 0)) return new List<GestureMath.Pt>(points);

        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;

        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            if (last <= first + 1) continue;

            double ax = points[first].X, ay = points[first].Y;
            double bx = points[last].X, by = points[last].Y;
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;

            double worst = -1;
            int worstAt = -1;
            for (int i = first + 1; i < last; i++)
            {
                double px = points[i].X - ax, py = points[i].Y - ay;
                double deviation;
                if (len2 <= 1e-12)
                {
                    deviation = Math.Sqrt(px * px + py * py);
                }
                else
                {
                    double t = (px * dx + py * dy) / len2;
                    if (t < 0) t = 0;
                    else if (t > 1) t = 1;
                    double ex = px - dx * t, ey = py - dy * t;
                    deviation = Math.Sqrt(ex * ex + ey * ey);
                }

                if (deviation > worst)
                {
                    worst = deviation;
                    worstAt = i;
                }
            }

            if (worstAt < 0 || worst <= epsilon) continue;
            keep[worstAt] = true;
            stack.Push((first, worstAt));
            stack.Push((worstAt, last));
        }

        var result = new List<GestureMath.Pt>(n);
        for (int i = 0; i < n; i++)
            if (keep[i]) result.Add(points[i]);
        return result;
    }

    /// <summary>同 <see cref="DrawnStroke"/>，包成可比的路径（单段笔画）。</summary>
    public static WgPath? DrawnPath(IReadOnlyList<GestureMath.Pt> points)
    {
        var stroke = DrawnStroke(points);
        return stroke == null ? null : WgPath.FromStroke(stroke);
    }

    /// <summary>一条手势对一条轨迹的原始形状距离；+∞ = 不可比（点数差太远、压成 1 点等）。</summary>
    public static double Distance(MouseGesture g, IReadOnlyList<GestureMath.Pt> points)
    {
        var tpl = TemplateFor(g);
        var drawn = DrawnPath(points);
        if (tpl == null || drawn == null) return double.PositiveInfinity;

        /* 方向要照抄引擎：模板是 receiver、手绘是参数 ——
         * 128 点上限卡在参数侧（见 WgStroke.DistanceTo）。 */
        return tpl.DistanceTo(drawn);
    }

    /// <summary>
    /// 识别阈值：立即执行用更严的一档（<see cref="GestureOptions.WgImmediateThreshold"/>）。
    /// 设置文件被手改坏了也别把识别彻底关死：退回引擎默认值。
    /// </summary>
    public static double ThresholdOf(GestureOptions? opts, bool immediate)
    {
        double threshold = opts == null
            ? WgEngine.DefaultThreshold
            : immediate ? opts.WgImmediateThreshold : opts.WgThreshold;
        if (!(threshold > 0.0001) || threshold > WgEngine.MaxDistance)
            threshold = WgEngine.DefaultThreshold;
        return threshold;
    }

    /// <summary>
    /// 在一桶候选里挑最像的一条。分数按阈值归一化（1.0 = 刚好卡在阈上，0 = 一模一样），
    /// 界面上那句"偏差 0.0xx"读的就是它。方向串打分（0.30 编辑距离）已在 v5.13 删掉，
    /// 这里没有第二个刻度需要对齐。
    /// </summary>
    /// <param name="immediate">
    /// true = 画到一半就执行：阈值再收紧一档、对"两条都像"零容忍（猜错就是误触发）。
    /// </param>
    public static (MouseGesture? Hit, double Score) Match(
        IReadOnlyList<MouseGesture> bucket,
        IReadOnlyList<GestureMath.Pt> points,
        GestureOptions opts,
        bool immediate = false)
    {
        if (bucket == null || bucket.Count == 0) return (null, 1);

        double threshold = ThresholdOf(opts, immediate);
        double tie = opts.WgTieMargin > 0 ? opts.WgTieMargin : 0.01;
        if (immediate) tie = Math.Max(tie, 0.02);

        var drawn = DrawnPath(points);
        if (drawn == null) return (null, 1);


        MouseGesture? best = null;
        double bestDistance = double.MaxValue;
        double secondDistance = double.MaxValue;

        foreach (var g in bucket)
        {
            if (g == null || g.IsWildcard) continue;    // 通配由调用方单独兜底
            var entry = EntryFor(g);
            var tpl = entry?.Built;
            if (tpl == null) continue;

            double d = tpl.DistanceTo(drawn);
            if (double.IsNaN(d) || double.IsPositiveInfinity(d)) continue;

            if (d < bestDistance)
            {
                secondDistance = bestDistance;
                bestDistance = d;
                best = g;
            }
            else if (d < secondDistance)
            {
                secondDistance = d;
            }
        }

        if (best == null) return (null, 1);

        double score = bestDistance >= threshold ? 1 : bestDistance / threshold;
        if (bestDistance > threshold) return (null, 1);

        /* 歧义：两条候选的差距在容差内，说明这一笔本来就悬在中间 —— 不猜。
         * 但距离小到容差以内（几乎一模一样）时不必再看次佳，那就是它。 */
        if (bestDistance > tie && secondDistance - bestDistance <= tie)
            return (null, score);

        return (best, score);
    }

}
