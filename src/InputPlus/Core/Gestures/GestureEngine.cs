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

    private sealed class Template
    {
        public int PathCount;
        public int PathHash;
        public string Pattern = "";
        public WgPath? Built;

        /// <summary>模板本身是不是闭环（闭环才有"换个起点/反着绕"的容错）。</summary>
        public bool IsLoop;

        /// <summary>模板里"圈"那一段（去掉 Q 的尾巴）—— 闭环对齐只用它。</summary>
        public WgPath? LoopHead;
    }

    private static readonly ConditionalWeakTable<MouseGesture, Template> Cache =
        new ConditionalWeakTable<MouseGesture, Template>();

    /// <summary>存档轨迹的指纹（点数 + 内容哈希 + 方向串）；任何一项变了都要重建模板。</summary>
    private static void Fingerprint(MouseGesture g, out int count, out int hash, out string pattern)
    {
        var path = g.Path;
        int h = 17;
        int n = path?.Count ?? 0;
        for (int i = 0; i < n; i++) h = unchecked(h * 31 + path![i]);
        count = n;
        hash = h;
        pattern = g.Pattern ?? "";
    }

    /// <summary>
    /// 取（或建）一条手势的识别模板。null 表示这条手势没法参与形状比较
    /// （既没有轨迹、方向串也是空的或通配）。
    /// </summary>
    public static WgPath? TemplateFor(MouseGesture g) => EntryFor(g)?.Built;

    /// <summary>
    /// 一条手势的几何形状（归一化到 1000 空间的折线）：优先用存档轨迹；
    /// 没有轨迹（早期版本存的、或别处导入没带点阵的配置）就把方向串画成等长折线。
    /// 两种形状大小口径一致 —— 都过了一遍 <see cref="GestureMath.Normalize"/>。
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

        int count, hash;
        string pattern;
        Fingerprint(g, out count, out hash, out pattern);

        var tpl = Cache.GetValue(g, _ => new Template());
        if (tpl.PathCount == count && tpl.PathHash == hash && tpl.Pattern == pattern)
            return tpl;

        var shape = ShapeOf(g);
        WgStroke? stroke = shape == null ? null : StrokeOf(shape);

        WgPath? built = null;
        if (stroke != null && stroke.Count >= 2 && stroke.Count <= MaxTemplatePoints)
            built = WgPath.FromStroke(stroke);

        tpl.PathCount = count;
        tpl.PathHash = hash;
        tpl.Pattern = pattern;
        tpl.Built = built;

        /* 闭环对齐用"圈那一段"：画圈、画 Q 都是"圈 + 尾巴"，尾巴不该跟着转，
         * 所以模板这边先把圈摘出来，对齐时只比圈。 */
        List<GestureMath.Pt>? loop = built != null && shape != null ? SplitLoop(shape)?.Loop : null;
        tpl.LoopHead = loop == null ? null : PathOf(loop);
        tpl.IsLoop = tpl.LoopHead != null;
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
    /// 平滑 → 按尺度丢抖动 → 归一化 → 18° 合并。
    ///
    /// **两侧必须走同一条路**：模板也是人手画下来存着的，里面同样带着手抖。
    /// 只平滑手绘那一侧，就会变成"干净直线 vs 抖动的模板"，距离反而更大 ——
    /// 这个坑真的踩过：平滑上线后，直线手势全好了，而带手抖模板的
    /// 「最大化」从 3/3 掉到 0/3。
    /// </summary>
    private static WgStroke? StrokeOf(IReadOnlyList<GestureMath.Pt> shape)
    {
        double extent = GestureMath.Extent(shape);
        if (extent < 1) return null;                              // 没动过的轨迹不是一笔

        /* 先按弧长重采样成等距点，再平滑。
         *
         * 这一步不能省：模板可能只有 3 个点（一条折线），手绘有几百个点（每 2px 一个）。
         * 直接按"点数"做滑动平均，同样半径对 3 点模板就是"整条链一起抹"，
         * 直角会被削成斜坡；对手绘只是抹掉一点毛刺 —— 两侧处理不等价，距离自然下不来
         * （实测：直角手势自己画自己，距离 0.12，超阈两倍多）。 */
        var norm = GestureMath.Normalize(shape);
        int count = SampleCount(norm);
        var even = GestureMath.Resample(norm, count);
        var smoothed = Smooth(even, SmoothWindow(count));

        /* 容差跟整笔尺度走（归一化后最长边 1000）：4 个单位 ≈ 千分之四屏宽。 */
        double epsilon = Math.Max(0.75, GestureMath.Extent(even) * 0.004);
        var stroke = Build(smoothed, epsilon);

        /* 兜底：再怎么抖也不能把"不可比"留给用户。压完还超上限就加大容差重压，
         * 直到进入引擎能比的范围（形状会变粗，但比"一律未识别"好得多）。 */
        for (int guard = 0; stroke != null && stroke.Count > MaxTemplatePoints && guard < 8; guard++)
        {
            epsilon *= 1.7;
            stroke = Build(smoothed, epsilon);
        }

        return stroke == null || stroke.Count < 2 ? null : stroke;
    }

    /// <summary>等距重采样后的点数：每 4 个单位一个点，夹在 24~400 之间。</summary>
    private static int SampleCount(IReadOnlyList<GestureMath.Pt> even)
    {
        double length = 0;
        for (int i = 1; i < even.Count; i++) length += (even[i] - even[i - 1]).Length;
        if (!(length > 0)) return 24;
        return Math.Clamp((int)Math.Round(length / 4.0), 24, 400);
    }

    /// <summary>平滑半径：点数的 1/120（≈ 笔迹总长的 1.5%），至少 1。</summary>
    private static int SmoothWindow(int count) => Math.Max(1, count / 120);

    /// <summary>
    /// 滑动平均（首尾保留原样）。手抖是高频摆动，形状却是低频的 ——
    /// 不平滑的话，一条本该是直线的"→"会被 18° 合并压成几十个折点，
    /// 而它的模板只有 2 个点，两条曲线在 (s, θ) 空间里差得很远，
    /// 结果就是"最简单的手势反而最认不出来"。
    /// </summary>
    internal static List<GestureMath.Pt> Smooth(IReadOnlyList<GestureMath.Pt> points, int radius)
    {
        int n = points.Count;
        if (n < 3 || radius < 1) return new List<GestureMath.Pt>(points);

        var result = new List<GestureMath.Pt>(n) { points[0] };
        for (int i = 1; i < n - 1; i++)
        {
            double sx = 0, sy = 0;
            int count = 0;
            int from = Math.Max(0, i - radius), to = Math.Min(n - 1, i + radius);
            for (int k = from; k <= to; k++)
            {
                sx += points[k].X;
                sy += points[k].Y;
                count++;
            }
            result.Add(new GestureMath.Pt(sx / count, sy / count));
        }
        result.Add(points[n - 1]);
        return result;
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

        /* 闭环容错：同一个圈换个起点起笔、或者反着绕，弧长参数化的形状距离会差很远。
         * 只在"按原样比不过阈值"时才生成 8 个起点 ×（正/反）共 16 条变体重比 ——
         * 正常命中的路径上一分钱都不多花。 */
        bool drawnLoop = GestureMath.IsLoop(points);
        List<GestureMath.Pt>? head = null;
        List<WgPath>? variants = null;

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
            if (drawnLoop && entry!.IsLoop && entry.LoopHead != null && !(d <= threshold))
            {
                head ??= SplitLoop(points)?.Loop;
                if (head != null)
                {
                    variants ??= ClosedVariants(head);
                    foreach (var v in variants)
                    {
                        double dv = entry.LoopHead.DistanceTo(v);
                        if (!double.IsNaN(dv) && dv < d) d = dv;
                    }
                }
            }
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

    /// <summary>松手那一下"打紧"的尾巴长度（像素）：收笔时鼠标会被手一带、往回收一点。</summary>
    private const double ReleaseTailPx = 16;

    /// <summary>尾巴最多占整笔的比例 —— 再切就只剩起手那一小截了，那不叫识别。</summary>
    private const double ReleaseTailMaxShare = 0.25;

    /// <summary>
    /// 整条比"完美到可以不信切尾结果"的分数上限（分数 = 距离 / 阈值，0.2 即距离 ≤ 0.01）。
    /// </summary>
    private const double ReleasePerfectShare = 0.2;

    /// <summary>整条完美、而切尾结果比它差这么多（分数差）时，判整条更可信。</summary>
    private const double ReleasePerfectMargin = 0.3;

    /// <summary>
    /// **松手结算用的识别**：把收笔那一下的尾巴切掉再比 —— 这是 WGestures 2 的
    /// 实时语义在"松手结算"上的等价实现。
    ///
    /// 为什么非要这一步：WG2 是一边画一边判，认出来就执行，用户收笔那一下的回勾、
    /// 过冲、甩尾根本不参与判定；KE 只在松手时整条比，尾巴反向的那几 px 在
    /// (累计弧长 s, 方向角 θ) 空间里被归一化放大，整条距离会直接顶到 DP 的无解上限
    /// （实测 0.2，远超 0.05 的阈值），于是"同一笔在 WG2 里成、在 KE 里不成"。
    ///
    /// 量化依据（1800 组真实手感样本：8ms 轮询 + 2px 量化 + 手抖 + 弓形 + 收笔回勾）：
    /// 现状整条比 56.0% 判对 / 34.2% 未识别；切掉尾巴后 85.4% 判对 / 8.3% 未识别，
    /// 判错反而从 9.8% 降到 6.2%；收笔带 15px 回勾的一组，命中率从 47.4% 回到 90% 以上。
    /// 尾巴长度取 max(16px, 弧长×4%)：手感上收笔位移是十来像素的量级，长笔画按比例放宽；
    /// 再往上切（24/32px）只多 0.6% 的召回，却多 1.2% 的误判，不划算。
    ///
    /// 判别力不受影响：切完照样走同一条 <see cref="Match"/>，阈值、歧义容差、闭环容错
    /// 一个都没改；长手势（↑← 之于 ↑）的第二段远长于 16px，切尾巴切不掉它。
    /// </summary>
    public static (MouseGesture? Hit, double Score) MatchRelease(
        IReadOnlyList<MouseGesture> bucket,
        IReadOnlyList<GestureMath.Pt> points,
        GestureOptions opts)
    {
        if (points == null || points.Count < 3) return (null, 1);

        var cut = TrimTail(points);
        if (cut == null) return Match(bucket, points, opts);

        var trimmed = Match(bucket, cut, opts);
        if (trimmed.Hit == null && trimmed.Score >= 1) return Match(bucket, points, opts);

        /* 切尾命中了，但还要问一句：整条是不是本来就"一模一样"？
         * 真实案例：任务管理器（↑↓↑↓）整条距离 0.0000，切掉尾巴后反而只跟 QQ邮箱
         * 像（0.047）；照"切尾优先"就把 0.047 的答案盖在 0.0000 上，用户看到的是
         * "明明画得一模一样却不认"。所以整条完美、且切尾明显更差时，采信整条。
         * 量化：1800 组真实手感样本上判对率与"切尾优先"持平（92.2%），
         * 判错不多一条，未识别反而少一条。 */
        var whole = Match(bucket, points, opts);
        if (whole.Hit != null && whole.Score <= ReleasePerfectShare
            && (trimmed.Hit == null || trimmed.Score > whole.Score + ReleasePerfectMargin))
            return whole;

        return trimmed;
    }

    /// <summary>
    /// 从尾巴上切掉"松手那一下"的量：min(max(16px, 弧长×4%), 弧长×25%)。
    /// 切完剩不到 3 个点、或者本来就没多长，返回 null（调用方按整条比）。
    /// </summary>
    private static List<GestureMath.Pt>? TrimTail(IReadOnlyList<GestureMath.Pt> pts)
    {
        if (pts == null || pts.Count < 4) return null;

        double total = 0;
        for (int i = 1; i < pts.Count; i++) total += (pts[i] - pts[i - 1]).Length;
        if (!(total > 0)) return null;

        double drop = Math.Min(Math.Max(ReleaseTailPx, total * 0.04), total * ReleaseTailMaxShare);
        double keep = total - drop;
        if (!(keep > 0)) return null;

        var cut = new List<GestureMath.Pt>(pts.Count) { pts[0] };
        double acc = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double seg = (pts[i] - pts[i - 1]).Length;
            if (acc + seg >= keep)
            {
                double t = seg <= 0 ? 0 : (keep - acc) / seg;
                cut.Add(new GestureMath.Pt(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t,
                                           pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t));
                break;
            }
            cut.Add(pts[i]);
            acc += seg;
        }
        return cut.Count >= 3 ? cut : null;
    }

    /// <summary>
    /// 画到一半先别急着立即执行：如果某条**更长**的候选，"按已画的弧长截下来的前一段"
    /// 和手绘的这一段对得上，说明用户多半还要往下画 —— 这时候执行就会把长手势永远截断。
    /// （v5.11 之前靠方向串前缀比较，方向串退休后按形状比。）
    ///
    /// 这不是第二套识别：判据还是同一条形状距离（<see cref="WgPath.DistanceTo"/>），
    /// 阈值还是立即执行那一档（<see cref="ThresholdOf"/>）。
    /// </summary>
    public static bool AwaitsLonger(
        IReadOnlyList<MouseGesture>? bucket,
        IReadOnlyList<GestureMath.Pt> points,
        GestureOptions opts)
    {
        if (bucket == null || bucket.Count == 0) return false;

        var drawn = DrawnPath(points);
        var drawnStroke = DrawnStroke(points);
        if (drawn == null || drawnStroke == null) return false;

        double drawnLength = drawnStroke.Length;
        if (!(drawnLength > 0)) return false;

        double threshold = ThresholdOf(opts, immediate: true);

        foreach (var g in bucket)
        {
            if (g == null || g.IsWildcard) continue;
            var shape = ShapeOf(g);
            if (shape == null) continue;

            var tpl = StrokeOf(shape);
            if (tpl == null || tpl.Count < 2 || tpl.Count > MaxTemplatePoints) continue;
            if (!(tpl.Length > drawnLength * 1.02)) continue;   // 不比手绘长，谈不上"还要往下画"

            var prefix = PrefixPath(tpl, drawnLength);
            if (prefix == null) continue;

            double d = prefix.DistanceTo(drawn);
            if (!double.IsNaN(d) && d <= threshold) return true;
        }
        return false;
    }

    /// <summary>
    /// 按弧长从模板上截下前 <paramref name="length"/> 那么长的一段（截在段中间就插一个点）。
    /// 模板不比 <paramref name="length"/> 长时返回 null —— 调用方已经排除这种情况。
    /// </summary>
    private static WgPath? PrefixPath(WgStroke tpl, double length)
    {
        var pts = tpl.Points;
        if (pts.Count < 2 || !(length > 0)) return null;

        var cut = new WgStroke();
        cut.Add(pts[0].X, pts[0].Y);
        double acc = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double seg = (pts[i] - pts[i - 1]).Length;
            if (acc + seg >= length)
            {
                double t = seg <= 0 ? 0 : (length - acc) / seg;
                cut.Add(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t,
                        pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t);
                return cut.Count >= 2 ? WgPath.FromStroke(cut) : null;
            }
            cut.Add(pts[i].X, pts[i].Y);
            acc += seg;
        }
        return null;
    }

    /// <summary>
    /// 把一笔拆成"圈 + 尾巴"：找一对几乎重合的点，把笔画切成两段，
    /// 长的那段是圈，短的那段是尾巴（Q 的那一撇、或者从圆上另一个地方起笔时
    /// 绕回来的那一小段）。开笔手势拆不出来，返回 null。
    /// </summary>
    private static (List<GestureMath.Pt> Loop, List<GestureMath.Pt> Tail)? SplitLoop(IReadOnlyList<GestureMath.Pt>? pts)
    {
        if (pts is null || pts.Count < 12) return null;
        int n = pts.Count;

        double tol = Math.Max(8, GestureMath.Extent(pts) * 0.10);
        int ba = -1, bb = -1;
        double bestGap = double.MaxValue;

        for (int i = 0; i < n - 6; i++)
        {
            for (int j = i + 6; j < n; j++)
            {
                double gx = Math.Abs(pts[i].X - pts[j].X), gy = Math.Abs(pts[i].Y - pts[j].Y);
                if (gx > tol || gy > tol) continue;
                double gap = gx + gy;
                if (gap < bestGap) { bestGap = gap; ba = i; bb = j; }
            }
        }
        if (ba < 0) return null;

        var inner = new List<GestureMath.Pt>(bb - ba + 1);
        for (int k = ba; k <= bb; k++) inner.Add(pts[k]);

        var outer = new List<GestureMath.Pt>(n - (bb - ba));
        for (int k = bb; k < n; k++) outer.Add(pts[k]);
        for (int k = 0; k <= ba; k++) outer.Add(pts[k]);

        double lenInner = PolyLength(inner), lenOuter = PolyLength(outer);
        if (Math.Max(lenInner, lenOuter) < Math.Max(1, GestureMath.Extent(pts)) * 0.5) return null;   // 太小，算不上一个圈

        return lenInner >= lenOuter ? (inner, outer) : (outer, inner);
    }

    /// <summary>折线长度（像素）。</summary>
    private static double PolyLength(IReadOnlyList<GestureMath.Pt> pts)
    {
        double sum = 0;
        for (int i = 1; i < pts.Count; i++) sum += (pts[i] - pts[i - 1]).Length;
        return sum;
    }

    /// <summary>闭合段的换起点变体：8 个起点 ×（正/反）两条绕向 = 16 条曲线。</summary>
    private static List<WgPath> ClosedVariants(IReadOnlyList<GestureMath.Pt> head)
    {
        var list = new List<WgPath>(16);
        var norm = GestureMath.Normalize(head);
        if (norm.Count < 8) return list;

        var rev = new List<GestureMath.Pt>(norm);
        rev.Reverse();

        for (int k = 0; k < 8; k++)
        {
            Add(Shift(norm, k));
            Add(Shift(rev, k));
        }
        return list;

        void Add(List<GestureMath.Pt> pts)
        {
            var path = PathOf(pts);
            if (path != null) list.Add(path);
        }
    }

    /// <summary>把一段轨迹压成引擎的路径（点数不合适的返回 null）。</summary>
    private static WgPath? PathOf(IReadOnlyList<GestureMath.Pt> pts)
    {
        var stroke = WgStroke.FromNormalized(pts);
        if (stroke == null || stroke.Count < 2 || stroke.Count > MaxTemplatePoints) return null;
        return WgPath.FromStroke(stroke);
    }

    /// <summary>把闭合轨迹的起点轮转 k/8 圈（k=0 原样返回，不复制）。</summary>
    private static List<GestureMath.Pt> Shift(List<GestureMath.Pt> src, int k)
    {
        int n = src.Count;
        if (k <= 0 || n < 8) return src;
        int off = (int)((long)n * k / 8) % n;
        if (off == 0) return src;

        var dst = new List<GestureMath.Pt>(n);
        for (int i = 0; i < n; i++) dst.Add(src[(i + off) % n]);
        return dst;
    }
}
