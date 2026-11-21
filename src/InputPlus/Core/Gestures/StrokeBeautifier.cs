using System;
using System.Collections.Generic;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 轨迹美化的强度。
///
/// 和 <see cref="GestureOptions.BeautifyLevel"/> 一样存字符串（off / light / medium / strong）：
/// 配置里写了不认识的词只退回 medium，不会把整份设置读崩。
/// </summary>
public enum StrokeBeautyLevel
{
    /// <summary>不美化，原样保存。</summary>
    Off = 0,

    /// <summary>轻：只去抖、抽稀、直线段拉直 —— 手画的毛刺没了，形状基本不动。</summary>
    Light = 1,

    /// <summary>中（出厂）：在轻的基础上吸附近水平/垂直/45°、近似对称做对称化、拐角磨圆。</summary>
    Medium = 2,

    /// <summary>强：再加等长化与更大的吸附容差 —— 认得出是同一个手势，但形状最"标准"。</summary>
    Strong = 3,
}

/// <summary>强度枚举与存档字符串之间的换算。</summary>
public static class StrokeBeautyLevels
{
    public const string IdOff = "off";
    public const string IdLight = "light";
    public const string IdMedium = "medium";
    public const string IdStrong = "strong";

    public static string ToId(StrokeBeautyLevel level) => level switch
    {
        StrokeBeautyLevel.Off => IdOff,
        StrokeBeautyLevel.Light => IdLight,
        StrokeBeautyLevel.Strong => IdStrong,
        _ => IdMedium,
    };

    /// <summary>没写、写错、写空都按「中」处理（出厂档）。</summary>
    public static StrokeBeautyLevel Parse(string? id)
    {
        if (string.Equals(id, IdOff, StringComparison.OrdinalIgnoreCase)) return StrokeBeautyLevel.Off;
        if (string.Equals(id, IdLight, StringComparison.OrdinalIgnoreCase)) return StrokeBeautyLevel.Light;
        if (string.Equals(id, IdStrong, StringComparison.OrdinalIgnoreCase)) return StrokeBeautyLevel.Strong;
        return StrokeBeautyLevel.Medium;
    }

    public static bool IsKnown(string? id) =>
        string.Equals(id, IdOff, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, IdLight, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, IdMedium, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, IdStrong, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 一次美化的结果。所有点都是归一化坐标（最长边 1000，落盘口径与
/// <see cref="MouseGesture.Path"/> 完全一致），调用方直接 <see cref="GestureMath.ToFlat"/> 即可。
/// </summary>
public sealed class StrokeBeautifyResult
{
    internal StrokeBeautifyResult(
        IReadOnlyList<GestureMath.Pt> rawPoints,
        IReadOnlyList<GestureMath.Pt> points,
        StrokeBeautyLevel requested,
        StrokeBeautyLevel used,
        bool beautified,
        bool fallback,
        string reason,
        string patternBefore,
        string patternAfter,
        double deviation,
        string rawPattern)
    {
        RawPoints = rawPoints;
        Points = points;
        RequestedLevel = requested;
        UsedLevel = used;
        Beautified = beautified;
        Fallback = fallback;
        Reason = reason;
        PatternBefore = patternBefore;
        PatternAfter = patternAfter;
        Deviation = deviation;
        RawPattern = rawPattern;
    }

    /// <summary>归一化后的原始轨迹（调试、重新美化用）。</summary>
    public IReadOnlyList<GestureMath.Pt> RawPoints { get; }

    /// <summary>美化后的轨迹；<see cref="Beautified"/> 为 false 时就是 <see cref="RawPoints"/>。</summary>
    public IReadOnlyList<GestureMath.Pt> Points { get; }

    public StrokeBeautyLevel RequestedLevel { get; }

    /// <summary>实际用上的档位：守门失败会降档，全失败就是 <see cref="StrokeBeautyLevel.Off"/>。</summary>
    public StrokeBeautyLevel UsedLevel { get; }

    /// <summary>真的换了形状（false = 保留原始轨迹）。</summary>
    public bool Beautified { get; }

    /// <summary>没能按请求的档位美化（降档或整条回退）。</summary>
    public bool Fallback { get; }

    /// <summary>人话说明：通过时的偏差，或者为什么回退。</summary>
    public string Reason { get; }

    /// <summary>
    /// 语义基准方向串：守门要求美化后的形状与它一致（「美化没有改手势」的判据）。
    ///
    /// 新建手势走 <see cref="GestureBeauty"/> 时这里是手势存档里的
    /// <see cref="MouseGesture.Pattern"/>，直接调 <see cref="Beautifier"/> 时是
    /// <see cref="GesturePattern.FromNormalized"/> 对原轨迹的判定。
    /// </summary>
    public string PatternBefore { get; }
    public string PatternAfter { get; }

    /// <summary>
    /// 现有识别器对**原始轨迹**的判定（<see cref="GesturePattern.FromNormalized"/>）。
    /// 与 <see cref="PatternBefore"/> 不同，说明守门用的是手势存档的方向 ——
    /// 录制按真实像素判、存档按归一化路径判，两个口径偶尔会不一致。
    /// </summary>
    public string RawPattern { get; }

    /// <summary>美化前后在现有识别器里的形状距离（0 = 完全一样）。</summary>
    public double Deviation { get; }

    /// <summary>方向串一致 —— 美化没有把这条手势改成别的方向语义。</summary>
    public bool SameSemantics => string.Equals(PatternBefore, PatternAfter, StringComparison.Ordinal);
}

/// <summary>
/// 轨迹美化（手势标准化）：把手画的抖线修成规整、好看的图形，同时保住方向语义。
///
/// 只在**创建手势**时跑一次，结果和模板一起落盘（<see cref="MouseGesture.Beauty"/>）；
/// 触发阶段只查表、不重算 —— 手里画的那条线永远原样显示，匹配成功后才换成这个标准图形。
///
/// 流程（顺序不能换）：
///   ① 去抖平滑（滑动平均，拐角由后面的抽稀保住）
///   ② 等距重采样（不同鼠标采样率、画快画慢都落到同一口径）
///   ③ RDP 抽稀（只留折点）
///   ④ 折点切段 + 共线合并（别把一条直线上 1px 的抖动记成拐角）
///   ⑤ 方向吸附（近水平 / 垂直 / 45° 拉直，容差跟强度走）
///   ⑥ 近似垂直轴对称的做对称化（W、M、⊥ 这类对称图形）
///   ⑦ 强档段长等长化（3~8 段的对称图形，长短差不到一倍才做）
///   ⑧ 拐角磨圆（统一视觉风格，圆角半径按整笔尺度）
///   ⑨ 近似闭合的闭合；闭环且径向偏差小的拟合成椭圆（圆、Q 的圈）
///   ⑩ 归一化到 0~1000
///
/// **两道守门**（任何一道不过就降档，全档不过就退回原始轨迹并记日志）：
///   ① 方向串：<see cref="GesturePattern.FromNormalized"/> 前后必须完全一致 —— 美化不许改语义；
///   ② 识别器：把原轨迹当成模板，用 <see cref="GestureEngine.Distance"/> 比美化后的形状，
///      必须落在阈值内（这条就是"美化前后在现有识别器里是同一个手势"）。
///
/// 方向性手势（L、勾、↑↓）不做旋转归一：<see cref="GestureMath.Normalize"/> 只平移 + 等比缩放，
/// 对称化只做**垂直轴镜像**，镜像映射 i ↔ n-1-i 保住起笔/收笔的先后顺序。
/// </summary>
public static class StrokeBeautifier
{
    /// <summary>点数上限保护：手绘一笔最多也就几百点，超过两千的一律先抽样（防卡 UI）。</summary>
    public const int MaxRawPoints = 2000;

    /// <summary>原始轨迹小于这个包围盒对角线就当"点了一下"，不美化。</summary>
    private const double MinExtentPx = 24;

    /// <summary>输出点数：够画顺滑，又不至于让落盘的 JSON 膨胀。</summary>
    private const int OutputPoints = 64;

    /// <summary>
    /// 美化算法版本（<see cref="MouseGesture.BeautyVersion"/> 存的就是它）。
    /// 改了算法或参数就把这个数 +1 —— 存量手势的模板会被判成"过期"，
    /// 下次用到时自动重建（见 <see cref="GestureBeauty.Ensure"/>），不用用户手动重画。
    /// <para>v2：改成"先按弧长加密再平滑 + 按转角找折点（不再用 RDP 抽稀）+ 两点直线直接吸附"，
    /// 修掉方框被抹成对角线、↑↓↑↓ 退化成一条线、内建方向手势永远不美化三个问题。</para>
    /// </summary>
    public const int TemplateVersion = 2;

    /// <summary>对称判定容差（占整笔尺度的比例）。</summary>
    private const double SymmetryTolerance = 0.10;

    /// <summary>每档的具体参数。字段全 readonly，纯查表。</summary>
    private readonly struct Rules
    {
        public readonly int SmoothWindow;
        public readonly int SmoothPasses;
        public readonly double SimplifyEpsilon;
        public readonly double CornerDeg;
        public readonly double MinSegment;
        public readonly double SnapDeg;
        public readonly double RoundRadius;
        public readonly double LengthBlend;
        public readonly bool Symmetry;
        public readonly bool FitLoop;
        public readonly double EngineLimit;
        public readonly double HausdorffLimit;

        public Rules(
            int smoothWindow, int smoothPasses, double simplifyEpsilon, double cornerDeg,
            double minSegment, double snapDeg, double roundRadius, double lengthBlend,
            bool symmetry, bool fitLoop, double engineLimit, double hausdorffLimit)
        {
            SmoothWindow = smoothWindow;
            SmoothPasses = smoothPasses;
            SimplifyEpsilon = simplifyEpsilon;
            CornerDeg = cornerDeg;
            MinSegment = minSegment;
            SnapDeg = snapDeg;
            RoundRadius = roundRadius;
            LengthBlend = lengthBlend;
            Symmetry = symmetry;
            FitLoop = fitLoop;
            EngineLimit = engineLimit;
            HausdorffLimit = hausdorffLimit;
        }
    }

    /* 容差随档位走。EngineLimit 取 0.04~0.075：手势识别的命中阈值是 0.05，
     * 美化后的形状离原轨迹超过这个距离，就有可能"美化完反而不认自己"——
     * 那还不如不美化，所以守门直接按这个尺子卡。 */
    private static readonly Rules LightRules = new(
        smoothWindow: 3, smoothPasses: 1, simplifyEpsilon: 6, cornerDeg: 34,
        minSegment: 18, snapDeg: 8, roundRadius: 0, lengthBlend: 0,
        symmetry: false, fitLoop: false, engineLimit: 0.040, hausdorffLimit: 0.16);

    private static readonly Rules MediumRules = new(
        smoothWindow: 5, smoothPasses: 2, simplifyEpsilon: 12, cornerDeg: 30,
        minSegment: 20, snapDeg: 12, roundRadius: 10, lengthBlend: 0,
        symmetry: true, fitLoop: true, engineLimit: 0.055, hausdorffLimit: 0.22);

    private static readonly Rules StrongRules = new(
        smoothWindow: 7, smoothPasses: 2, simplifyEpsilon: 18, cornerDeg: 26,
        minSegment: 22, snapDeg: 16, roundRadius: 16, lengthBlend: 0.45,
        symmetry: true, fitLoop: true, engineLimit: 0.075, hausdorffLimit: 0.28);

    private static Rules RulesFor(StrokeBeautyLevel level) => level switch
    {
        StrokeBeautyLevel.Light => LightRules,
        StrokeBeautyLevel.Strong => StrongRules,
        _ => MediumRules,
    };

    /// <summary>
    /// 美化一条手绘轨迹。返回的对象永远可用：失败时 <see cref="StrokeBeautifyResult.Points"/>
    /// 就是归一化后的原始轨迹，调用方不必再判空。
    /// </summary>
    /// <param name="raw">原始轨迹（Raw Stroke），屏幕像素或已归一化都行 —— 这里会自己归一化。</param>
    /// <param name="level">请求的强度；<see cref="StrokeBeautyLevel.Off"/> 直接原样返回。</param>
    /// <param name="expectedPattern">
    /// 语义基准方向串（可选）。传手势存档里的 <see cref="MouseGesture.Pattern"/> 时，
    /// 守门就按"这条手势当初被识别成什么"来判 —— 录制按真实像素判方向（20px 起手）、
    /// 存档路径按归一化坐标判方向（50/1000 起手），两个口径对同一笔偶尔给出不同方向串，
    /// 不传这个参数会把本来同义的美化结果白白回退掉。空串 / 通配符 = 不指定。
    /// </param>
    public static StrokeBeautifyResult Beautify(
        IReadOnlyList<GestureMath.Pt>? raw, StrokeBeautyLevel level, string? expectedPattern = null,
        Action<string>? trace = null)
    {
        var prepared = Prepare(raw);
        if (prepared.Count < 2)
            return Unchanged(prepared, level, "轨迹点太少，没有可美化的形状", "", "");

        if (GestureMath.Extent(prepared) < MinExtentPx)
            return Unchanged(prepared, level, "这一笔太小了（不足 24 像素），保持原始轨迹", "", "");

        var rawNorm = GestureMath.Normalize(prepared);
        if (GestureMath.Extent(rawNorm) < 1)
            return Unchanged(rawNorm, level, "这一笔没有位移，保持原始轨迹", "", "");

        string rawPattern = GesturePattern.FromNormalized(rawNorm);

        /* 语义基准优先用手势存档的方向串：它才是"这条手势在识别器里的身份"。
         * 没有存档方向（新笔直录、导入）时才用原轨迹的即时判定。 */
        string before = GesturePattern.Clean(expectedPattern);
        bool fromExpectation = before.Length > 0 && before != GesturePattern.Wildcard;
        if (!fromExpectation) before = rawPattern;

        if (before.Length == 0)
            return Unchanged(rawNorm, level, "识别不出方向，保持原始轨迹", before, before, rawPattern);

        if (level == StrokeBeautyLevel.Off)
            return Unchanged(rawNorm, level, "美化已关闭", before, before, rawPattern);

        /* 降档重试：强 → 中 → 轻。哪一档先过守门就用哪一档 ——
         * 这比"强档不过就整条放弃"合理：用户要的是好看，不是某个具体档位。 */
        var rejects = new List<string>();
        for (var tryLevel = level; tryLevel >= StrokeBeautyLevel.Light; tryLevel--)
        {
            var candidate = TryBuild(rawNorm, tryLevel, trace);
            trace?.Invoke($"{StrokeBeautyLevels.ToId(tryLevel)}: 折点重建 = {(candidate == null ? "null" : candidate.Count + " 点")}");
            if (candidate == null || candidate.Count < 2) { rejects.Add($"{StrokeBeautyLevels.ToId(tryLevel)} 档没有可用折点"); continue; }

            candidate = GestureMath.Resample(GestureMath.Normalize(candidate), OutputPoints);
            if (candidate.Count < 2) { rejects.Add($"{StrokeBeautyLevels.ToId(tryLevel)} 档重采样失败"); continue; }

            if (!Gate(rawNorm, candidate, before, tryLevel, out double deviation, out string why))
            {
                trace?.Invoke($"{StrokeBeautyLevels.ToId(tryLevel)}: 守门不过 —— {why}（美化后方向串 {GesturePattern.FromNormalized(candidate)}，原轨迹 {rawPattern}）");
                rejects.Add($"{StrokeBeautyLevels.ToId(tryLevel)} 档 {why}");
                continue;
            }

            bool downgraded = tryLevel != level;
            string note = downgraded ? $"按 {StrokeBeautyLevels.ToId(tryLevel)} 档通过（{why}）" : why;
            if (fromExpectation && !string.Equals(before, rawPattern, StringComparison.Ordinal))
                note += $"；方向基准用手势存档 {before}（原轨迹即时判定为 {rawPattern}）";
            return new StrokeBeautifyResult(
                rawNorm, candidate, level, tryLevel,
                beautified: true, fallback: downgraded,
                reason: note,
                patternBefore: before,
                patternAfter: GesturePattern.FromNormalized(candidate),
                deviation: deviation,
                rawPattern: rawPattern);
        }

        string tail = rejects.Count > 0 ? "（" + string.Join("；", rejects) + "）" : "";
        return Unchanged(rawNorm, level, "美化后语义或形状偏差过大，已保留原始轨迹" + tail, before, before, rawPattern);
    }

    /// <summary>强度 → 人话（日志与设置页共用）。</summary>
    public static string DisplayName(StrokeBeautyLevel level) => level switch
    {
        StrokeBeautyLevel.Off => "不美化",
        StrokeBeautyLevel.Light => "轻",
        StrokeBeautyLevel.Strong => "强",
        _ => "中",
    };

    // ───────────────────────── 主流程 ─────────────────────────

    private static List<GestureMath.Pt>? TryBuild(IReadOnlyList<GestureMath.Pt> rawNorm, StrokeBeautyLevel level, Action<string>? trace = null)
    {
        var r = RulesFor(level);

        /* 两点直线：↑ → ↓ ← ↗ 这些内建方向手势存的就是两个点，本来没有"折点"可修，
         * 但它们恰恰是最该被拉直的一类 —— 吸附到最近的 45° 轴、长度不变即可。
         * 以前这里直接判"轨迹点太少"，结果所有内建手势永远显示「原始轨迹」，
         * 用户看到的就是"美化根本没生效"。 */
        if (rawNorm.Count == 2)
        {
            var delta = rawNorm[1] - rawNorm[0];
            if (delta.Length < 1e-6) return null;
            var unit = SnapDirection(delta, r.SnapDeg);
            var line = new List<GestureMath.Pt>(2)
            {
                rawNorm[0],
                new GestureMath.Pt(rawNorm[0].X + unit.X * delta.Length, rawNorm[0].Y + unit.Y * delta.Length),
            };
            trace?.Invoke($"{StrokeBeautyLevels.ToId(level)}: 两点直线 → 吸附 {Math.Atan2(unit.Y, unit.X) * 180 / Math.PI:0}°");
            return line;
        }

        /* 闭环单独走拟合：圆画得再抖，拟合出来的正圆也不会改方向语义，
         * 而且比"抽稀成多边形"好看得多。拟合失败再走折线路径。 */
        if (r.FitLoop && TryFitLoop(rawNorm, out var loop)) return loop;

        /* 顺序不能反：**先按弧长加密，再平滑**。
         *
         * 存档轨迹常常只有 3~15 个点（方向串合成的、导入的、早期版本存的）。按点序滑窗
         * 是横向平均，点少的时候一个窗口就能跨过整条边：一个 5 点的方框（↑→↓←）先平滑
         * 会被抹成 (0,0)→(333,-667)→… 的圆角形，5 点窗口再来一遍直接塌成对角线
         * —— 实测「截图」这条 ↑→↓← 就这么被美化成了 ↗↙，守门必然不过。
         * 加密到约 2 单位一个点之后，同样的窗口只吃掉 6~14 单位的毛刺，拐角原地不动。 */
        int denseCount = Math.Clamp((int)(PolyLength(rawNorm) / 2.0), 64, 400);
        var dense = GestureMath.Resample(rawNorm, denseCount);
        var pts = Smooth(dense, r.SmoothWindow, r.SmoothPasses);

        /* 这里**不能**用 RDP 抽稀。RDP 量的是"点到首尾连线"的垂距，而 ↑↓↑↓ 这种原路
         * 折返的笔画整条都落在那条直线上 —— 垂距全是 0，四个折点会被一起丢掉，
         * 美化结果退化成一条直线（实测「任务管理器」就是 ↑↓↑↓ → ↑↓）。
         * 转折点必须按**转角**找：Corners 直接在加密后的点序上扫转角，
         * 折返是 180°，一定留得住；顺带把相邻共线的抖动点并掉。 */
        if (pts.Count < 2) return null;

        var anchors = Corners(pts, r);

        /* 折点收口：抖出来的"假折点"会把后一段按 12° 容差吸成 45° 斜线，圆角再一磨，
         * 整条直线就歪了（实测竖线横向残余 14.2/1000，本该是 0）。
         * 判据两条：转角 < 45° 且离前后折点连线不到 simplifyEpsilon ——
         * 真拐角（90°）、原路折返（180°）都过不了这关，不会被误并。 */
        /* 容差比抽稀阈值宽 1.6 倍：这一段的去向本来就是"被吸成一条直线"，
         * 手抖留下的横向偏移（实测 14/1000）得整段吃掉，否则后一段会被吸成斜的。 */
        anchors = Straighten(anchors, pts, r.SimplifyEpsilon * 1.6);

        trace?.Invoke($"{StrokeBeautyLevels.ToId(level)}: 抽稀 {pts.Count} 点 → 折点 {anchors.Count}：{string.Join(" ", anchors.Take(12).Select(p => $"({p.X:0},{p.Y:0})"))}");
        if (anchors.Count < 2) return null;

        if (r.Symmetry) Symmetrize(anchors);

        var rebuilt = Rebuild(anchors, r.SnapDeg, r.LengthBlend);
        if (rebuilt.Count < 2) return null;

        if (r.RoundRadius > 0) rebuilt = RoundCorners(rebuilt, r.RoundRadius);

        /* 近似闭合（首尾距离不到整笔的 10%）就真的闭合：画圈、画 Q 的接缝不该留个豁口。
         * 只对"本来就是圈"的笔画做 —— IsLoop 看的是累计转角，不是首尾距离。 */
        if (GestureMath.IsLoop(rawNorm) && rebuilt.Count > 2)
        {
            double extent = GestureMath.Extent(rebuilt);
            if ((rebuilt[^1] - rebuilt[0]).Length <= extent * 0.10)
                rebuilt[^1] = rebuilt[0];
        }

        return rebuilt;
    }

    /// <summary>
    /// 守门：方向串一致 + 现有识别器的形状距离 + 双向 Hausdorff。
    /// 三条都过才算"美化没有改变手势语义"。
    /// </summary>
    private static bool Gate(
        IReadOnlyList<GestureMath.Pt> rawNorm,
        IReadOnlyList<GestureMath.Pt> candidate,
        string patternBefore,
        StrokeBeautyLevel level,
        out double deviation,
        out string reason)
    {
        var r = RulesFor(level);
        deviation = 1;

        string after = GesturePattern.FromNormalized(candidate);
        if (!string.Equals(patternBefore, after, StringComparison.Ordinal))
        {
            reason = $"方向串 {patternBefore} → {after}";
            return false;
        }

        deviation = RecognizerDistance(rawNorm, candidate);
        if (deviation > r.EngineLimit)
        {
            reason = $"形状距离 {deviation:0.000} 超过 {r.EngineLimit:0.000}";
            return false;
        }

        double hausdorff = Hausdorff(rawNorm, candidate);
        double limit = r.HausdorffLimit * GestureMath.Size;
        if (hausdorff > limit)
        {
            reason = $"最大偏移 {hausdorff:0} 超过 {limit:0}";
            return false;
        }

        reason = $"方向一致、形状距离 {deviation:0.000}";
        return true;
    }

    /// <summary>
    /// 用现有识别器量"原轨迹 → 美化形状"的距离：把原轨迹包成一条临时手势当模板，
    /// 再让引擎拿美化后的点去比。这是验收里"美化前后判为同一手势"的直接依据。
    /// </summary>
    private static double RecognizerDistance(IReadOnlyList<GestureMath.Pt> rawNorm, IReadOnlyList<GestureMath.Pt> candidate)
    {
        try
        {
            var probe = new MouseGesture
            {
                Button = GestureButtons.Right,
                Kind = "stroke",
                Path = GestureMath.ToFlat(rawNorm),
            };
            return GestureEngine.Distance(probe, candidate);
        }
        catch
        {
            /* 识别器抛异常只可能是数据坏了：按"不一致"处理，宁可保存原始轨迹。 */
            return 1;
        }
    }

    private static StrokeBeautifyResult Unchanged(
        IReadOnlyList<GestureMath.Pt> points,
        StrokeBeautyLevel level,
        string reason,
        string patternBefore,
        string patternAfter,
        string rawPattern = "")
    {
        var norm = points.Count > 0 ? GestureMath.Normalize(points) : points;
        return new StrokeBeautifyResult(
            norm, norm, level, StrokeBeautyLevel.Off,
            beautified: false, fallback: level != StrokeBeautyLevel.Off,
            reason: reason,
            patternBefore: patternBefore,
            patternAfter: patternAfter.Length > 0 ? patternAfter : patternBefore,
            deviation: 0,
            rawPattern: rawPattern.Length > 0 ? rawPattern : patternBefore);
    }

    // ───────────────────────── 几何 ─────────────────────────

    /// <summary>去 NaN、去重复点、点数上限保护。</summary>
    private static List<GestureMath.Pt> Prepare(IReadOnlyList<GestureMath.Pt>? raw)
    {
        var list = new List<GestureMath.Pt>();
        if (raw == null) return list;

        foreach (var p in raw)
        {
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) ||
                double.IsInfinity(p.X) || double.IsInfinity(p.Y)) continue;
            if (list.Count > 0 && (p - list[^1]).Length < 1e-6) continue;
            list.Add(p);
        }

        if (list.Count <= MaxRawPoints) return list;

        /* 超长轨迹按步长抽样，但必须保住最后一个点（收笔方向是语义）。 */
        int stride = (int)Math.Ceiling(list.Count / (double)MaxRawPoints);
        var sampled = new List<GestureMath.Pt>(MaxRawPoints + 1);
        for (int i = 0; i < list.Count; i += stride) sampled.Add(list[i]);
        if ((sampled[^1] - list[^1]).Length > 1e-6) sampled.Add(list[^1]);
        return sampled;
    }

    /// <summary>滑动平均；首尾点钉死（起笔/收笔位置是语义的一部分）。</summary>
    private static List<GestureMath.Pt> Smooth(IReadOnlyList<GestureMath.Pt> pts, int window, int passes)
    {
        if (window < 3 || pts.Count < 3 || passes <= 0) return new List<GestureMath.Pt>(pts);

        var current = new List<GestureMath.Pt>(pts);
        int half = window / 2;
        for (int pass = 0; pass < passes; pass++)
        {
            var next = new List<GestureMath.Pt>(current.Count) { current[0] };
            for (int i = 1; i < current.Count - 1; i++)
            {
                double sx = 0, sy = 0;
                int n = 0;
                for (int k = -half; k <= half; k++)
                {
                    int j = i + k;
                    if (j < 0 || j >= current.Count) continue;
                    sx += current[j].X;
                    sy += current[j].Y;
                    n++;
                }
                next.Add(n > 0 ? new GestureMath.Pt(sx / n, sy / n) : current[i]);
            }
            next.Add(current[^1]);
            current = next;
        }
        return current;
    }

    /// <summary>按转角切折点，再把共线的中间点并掉。</summary>
    private static List<GestureMath.Pt> Corners(IReadOnlyList<GestureMath.Pt> pts, Rules r)
    {
        var anchors = new List<GestureMath.Pt> { pts[0] };
        int anchor = 0;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var run = pts[i] - pts[anchor];
            var step = pts[i + 1] - pts[i];
            if (run.Length < r.MinSegment || step.Length < 1e-6) continue;
            if (TurnDeg(run, step) > r.CornerDeg)
            {
                anchors.Add(pts[i]);
                anchor = i;
            }
        }
        anchors.Add(pts[^1]);

        /* 共线合并：方向差不到 8° 的相邻段其实是一条线，中间的"折点"是抖出来的。 */
        var merged = new List<GestureMath.Pt> { anchors[0] };
        for (int i = 1; i < anchors.Count - 1; i++)
        {
            var d1 = anchors[i] - merged[^1];
            var d2 = anchors[i + 1] - anchors[i];
            if (d1.Length > 1e-6 && d2.Length > 1e-6 && TurnDeg(d1, d2) < 8.0) continue;
            merged.Add(anchors[i]);
        }
        if (merged.Count == 1 || (anchors[^1] - merged[^1]).Length > 1e-6) merged.Add(anchors[^1]);
        return merged;
    }

    /// <summary>
    /// 折点收口：把一个"本来就该是直线"上的假折点去掉。
    /// 只看两条 —— 转角小于 45°，且这个点离前后两个折点的连线不到 <paramref name="epsilon"/>。
    /// 真拐角是 90°、原路折返是 180°，两条都过不了，所以 L / 方框 / ↑↓↑↓ 一个都不会丢。
    /// </summary>
    private static List<GestureMath.Pt> Straighten(
        List<GestureMath.Pt> anchors, IReadOnlyList<GestureMath.Pt> dense, double epsilon)
    {
        if (anchors.Count < 3 || epsilon <= 0) return anchors;
        var output = new List<GestureMath.Pt>(anchors.Count) { anchors[0] };
        for (int i = 1; i < anchors.Count - 1; i++)
        {
            var prev = output[^1];
            var cur = anchors[i];
            var next = anchors[i + 1];
            double chord = (next - prev).Length;
            if (chord < 1e-6) { output.Add(cur); continue; }

            /* 沿这一笔走过的长度 vs 首尾直线距离：原路折返是 2.0、直角拐弯是 1.41、
             * 一段带抖的直线基本就是 1.0。走路长度明显更长 = 这个折点承载着形状，留着；
             * 差不多长 = 它只是抖动，接着再看它离连线有多远。 */
            double walked = PathBetween(dense, prev, next);
            if (walked > chord * 1.35) { output.Add(cur); continue; }
            if (PointToPolyline(cur, new[] { prev, next }) <= epsilon) continue;

            output.Add(cur);
        }
        output.Add(anchors[^1]);
        return output;
    }

    /// <summary>沿点序从 <paramref name="a"/> 走到 <paramref name="b"/> 的折线长度（找不到就返回无穷大）。</summary>
    private static double PathBetween(IReadOnlyList<GestureMath.Pt> pts, GestureMath.Pt a, GestureMath.Pt b)
    {
        int ia = IndexNear(pts, a, 0);
        if (ia < 0) return double.MaxValue;
        int ib = IndexNear(pts, b, ia + 1);
        if (ib < 0) return double.MaxValue;
        double sum = 0;
        for (int i = ia + 1; i <= ib; i++) sum += (pts[i] - pts[i - 1]).Length;
        return sum;
    }

    private static int IndexNear(IReadOnlyList<GestureMath.Pt> pts, GestureMath.Pt p, int from)
    {
        for (int i = from; i < pts.Count; i++)
            if ((pts[i] - p).Length < 1e-9) return i;
        return -1;
    }

    /// <summary>
    /// 按折点重建折线：每段方向吸附到最近的 45° 倍数（容差内），
    /// 长度默认照原段长（<paramref name="lengthBlend"/> &gt; 0 时向平均长度靠拢）。
    /// 逐段累加而不是直接搬端点，吸附后的拐角才正好是 90° / 45°。
    /// </summary>
    private static List<GestureMath.Pt> Rebuild(IReadOnlyList<GestureMath.Pt> anchors, double snapDeg, double lengthBlend)
    {
        int n = anchors.Count - 1;
        var rebuilt = new List<GestureMath.Pt>(anchors.Count) { anchors[0] };
        if (n <= 0) return rebuilt;

        var lengths = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            lengths[i] = (anchors[i + 1] - anchors[i]).Length;
            sum += lengths[i];
        }
        double mean = sum / n;

        var current = anchors[0];
        for (int i = 0; i < n; i++)
        {
            var delta = anchors[i + 1] - anchors[i];
            if (delta.Length < 1e-6) continue;
            var unit = SnapDirection(delta, snapDeg);
            double length = lengthBlend > 0 && mean > 1e-6
                ? lengths[i] * (1 - lengthBlend) + mean * lengthBlend
                : lengths[i];
            current = new GestureMath.Pt(current.X + unit.X * length, current.Y + unit.Y * length);
            rebuilt.Add(current);
        }
        return rebuilt;
    }

    /// <summary>方向吸附：最近的水平 / 垂直 / 45° 斜线，容差内才吸。</summary>
    private static GestureMath.Pt SnapDirection(GestureMath.Pt delta, double toleranceDeg)
    {
        double deg = Math.Atan2(delta.Y, delta.X) * 180.0 / Math.PI;
        double snapped = Math.Round(deg / 45.0) * 45.0;
        if (Math.Abs(Norm180(deg - snapped)) <= toleranceDeg) deg = snapped;
        double rad = deg * Math.PI / 180.0;
        return new GestureMath.Pt(Math.Cos(rad), Math.Sin(rad));
    }

    /// <summary>
    /// 近似垂直轴对称就对称化（W、M、⊥ 这类）。
    ///
    /// 判定在等距重采样的 32 个点上做（点序号才有几何意义），映射用 i ↔ n-1-i：
    /// 这个映射**保留先后顺序**，所以 L、勾这类方向性手势即使碰巧对称也不会被翻转。
    /// 对称化只把每对点往中点拉一半，不动起笔/收笔的相对方位。
    /// </summary>
    private static void Symmetrize(List<GestureMath.Pt> anchors)
    {
        if (anchors.Count < 3) return;

        var probe = GestureMath.Resample(anchors, 32);
        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (var p in probe)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
        }
        double extent = GestureMath.Extent(probe);
        if (extent < 1) return;

        double cx = (minX + maxX) / 2.0;
        double error = 0;
        for (int i = 0; i < probe.Count; i++)
        {
            var mirror = probe[probe.Count - 1 - i];
            error += Math.Abs(probe[i].X - (2 * cx - mirror.X)) + Math.Abs(probe[i].Y - mirror.Y);
        }
        error /= probe.Count;
        if (error > extent * SymmetryTolerance) return;

        int n = anchors.Count;
        for (int i = 0; i < n / 2; i++)
        {
            var a = anchors[i];
            var b = anchors[n - 1 - i];
            double ax = (a.X + (2 * cx - b.X)) / 2.0;
            double ay = (a.Y + b.Y) / 2.0;
            double bx = (b.X + (2 * cx - a.X)) / 2.0;
            double by = (b.Y + a.Y) / 2.0;
            anchors[i] = new GestureMath.Pt(ax, ay);
            anchors[n - 1 - i] = new GestureMath.Pt(bx, by);
        }
        if (n % 2 == 1)
        {
            int mid = n / 2;
            anchors[mid] = new GestureMath.Pt(cx, anchors[mid].Y);
        }
    }

    /// <summary>拐角磨圆：每个折点切掉一小段，插入二次贝塞尔。半径按相邻段长夹住，不会吃掉整段。</summary>
    private static List<GestureMath.Pt> RoundCorners(IReadOnlyList<GestureMath.Pt> poly, double radius)
    {
        if (poly.Count < 3 || radius <= 0) return new List<GestureMath.Pt>(poly);

        var result = new List<GestureMath.Pt>(poly.Count * 6) { poly[0] };
        for (int i = 1; i < poly.Count - 1; i++)
        {
            var prev = poly[i - 1];
            var corner = poly[i];
            var next = poly[i + 1];
            var v1 = prev - corner;
            var v2 = next - corner;
            double l1 = v1.Length, l2 = v2.Length;
            if (l1 < 1e-6 || l2 < 1e-6) continue;

            double r = Math.Min(radius, Math.Min(l1, l2) * 0.45);
            var a = new GestureMath.Pt(corner.X + v1.X / l1 * r, corner.Y + v1.Y / l1 * r);
            var b = new GestureMath.Pt(corner.X + v2.X / l2 * r, corner.Y + v2.Y / l2 * r);

            result.Add(a);
            const int steps = 6;
            for (int k = 1; k <= steps; k++)
            {
                double t = (double)k / (steps + 1);
                result.Add(Quadratic(a, corner, b, t));
            }
            result.Add(b);
        }
        result.Add(poly[^1]);
        return result;
    }

    private static GestureMath.Pt Quadratic(GestureMath.Pt a, GestureMath.Pt c, GestureMath.Pt b, double t)
    {
        double u = 1 - t;
        return new GestureMath.Pt(
            u * u * a.X + 2 * u * t * c.X + t * t * b.X,
            u * u * a.Y + 2 * u * t * c.Y + t * t * b.Y);
    }

    /// <summary>
    /// 闭环拟合：累计转角绕了一圈、首尾又接得上，就用椭圆（圆形是特例）替掉手画的圈。
    /// 起点角与绕向都照原样保留 —— 顺时针画的圈不会变成逆时针。
    /// </summary>
    private static bool TryFitLoop(IReadOnlyList<GestureMath.Pt> rawNorm, out List<GestureMath.Pt>? fitted)
    {
        fitted = null;
        if (rawNorm.Count < 8) return false;
        if (!GestureMath.IsLoop(rawNorm)) return false;

        double extent = GestureMath.Extent(rawNorm);
        if (extent < 1) return false;
        if ((rawNorm[^1] - rawNorm[0]).Length > extent * 0.35) return false;

        double cx = 0, cy = 0;
        foreach (var p in rawNorm) { cx += p.X; cy += p.Y; }
        cx /= rawNorm.Count;
        cy /= rawNorm.Count;

        double sx = 0, sy = 0;
        foreach (var p in rawNorm)
        {
            sx += Math.Abs(p.X - cx);
            sy += Math.Abs(p.Y - cy);
        }
        /* 均匀铺满一圈的点：E|x-cx| = 2r/π。乘回去就是半径。 */
        double rx = sx / rawNorm.Count * Math.PI / 2.0;
        double ry = sy / rawNorm.Count * Math.PI / 2.0;
        if (rx < 1 || ry < 1) return false;

        /* 径向偏差太大说明这不是圈（Q 的尾巴、螺旋、画歪的 C）—— 别硬拟合成椭圆。 */
        double radial = 0;
        foreach (var p in rawNorm)
        {
            double dx = (p.X - cx) / rx, dy = (p.Y - cy) / ry;
            radial += Math.Abs(Math.Sqrt(dx * dx + dy * dy) - 1);
        }
        radial /= rawNorm.Count;
        if (radial > 0.22) return false;

        double ang0 = Math.Atan2((rawNorm[0].Y - cy) / ry, (rawNorm[0].X - cx) / rx);
        double dir = GestureMath.NetTurning(rawNorm) >= 0 ? 1 : -1;

        var points = new List<GestureMath.Pt>(OutputPoints);
        for (int k = 0; k < OutputPoints; k++)
        {
            double ang = ang0 + dir * 2 * Math.PI * k / (OutputPoints - 1);
            points.Add(new GestureMath.Pt(cx + rx * Math.Cos(ang), cy + ry * Math.Sin(ang)));
        }
        fitted = points;
        return true;
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static double PolyLength(IReadOnlyList<GestureMath.Pt> pts)
    {
        double sum = 0;
        for (int i = 1; i < pts.Count; i++) sum += (pts[i] - pts[i - 1]).Length;
        return sum;
    }

    private static double Norm180(double deg)
    {
        while (deg > 180.0) deg -= 360.0;
        while (deg < -180.0) deg += 360.0;
        return deg;
    }

    private static double TurnDeg(GestureMath.Pt a, GestureMath.Pt b)
    {
        double la = a.Length, lb = b.Length;
        if (la < 1e-9 || lb < 1e-9) return 0;
        double cos = Math.Clamp((a.X * b.X + a.Y * b.Y) / (la * lb), -1, 1);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    private static double Hausdorff(IReadOnlyList<GestureMath.Pt> a, IReadOnlyList<GestureMath.Pt> b)
        => Math.Max(MaxPointToPolyline(a, b), MaxPointToPolyline(b, a));

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
