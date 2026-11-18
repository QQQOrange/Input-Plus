using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using InputPlus.Core.Gestures.WG;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 手势识别诊断（--gesture-probe [手势名] [输出文件]）。
///
/// 回答"为什么我这条手势老是不认"：照着配置里每条手势自己画几遍，
/// 看引擎认不认得出它自己 —— 认不出，就是这条手势本身有问题
/// （模板点太多、手绘点太多、或者有另一条更像）。
///
/// 为什么要有这个入口：识别只有一套引擎，界面上只会说"未识别"，
/// 用户手里没有任何数字。这里的数字能直接指向原因：
///   · 手绘折点超过 128  —— 采样太密，引擎判"不可比"；
///   · 两侧点数差 10 倍   —— 模板是方向串合成的两个点，手绘是几十个点；
///   · 距离大于 0.05      —— 形状真的不像（导入的模板和手画的习惯不同）；
///   · 两条只差 0.01      —— 歧义，引擎故意不猜。
/// </summary>
internal static class GestureProbe
{
    public static int Run(string[] args)
    {
        string wanted = "最大化";
        string outPath = Path.Combine(AppPaths.DataDir, "gesture-probe.txt");

        var positional = args.Skip(1)
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .ToList();
        if (positional.Count > 0) wanted = positional[0];
        if (positional.Count > 1) outPath = positional[1];

        var sb = new StringBuilder();
        try
        {
            var settings = SettingsStore.Load();
            var opts = settings.GestureOptions;
            var strokes = (settings.Gestures ?? new List<MouseGesture>())
                .Where(g => g != null && string.Equals(g.Kind, "stroke", StringComparison.OrdinalIgnoreCase))
                .ToList();

            double threshold = GestureEngine.ThresholdOf(opts, immediate: false);
            sb.AppendLine($"阈值 {threshold:0.0000}（立即执行 {GestureEngine.ThresholdOf(opts, true):0.0000}）· 歧义容差 {(opts.WgTieMargin > 0 ? opts.WgTieMargin : 0.01):0.0000}");
            sb.AppendLine($"笔画手势 {strokes.Count} 条，其中 {strokes.Count(g => GestureEngine.CanMatch(g))} 条有几何模板");
            sb.AppendLine();

            sb.AppendLine("== 自己画自己：每条的识别率 ==");
            int okAll = 0, total = 0;
            var failures = new List<string>();
            foreach (var g in strokes.Where(GestureEngine.CanMatch).OrderBy(g => g.Name, StringComparer.Ordinal))
            {
                var shape = GestureEngine.ShapeOf(g);
                if (shape == null) continue;

                var bucket = Bucket(strokes, g);
                int ok = 0;
                var notes = new List<string>();
                foreach (var jitter in new[] { 0.5, 1.5, 3.0 })
                {
                    total++;
                    var drawn = Simulate(shape, jitter, 1.0, 0, 100 + (int)(jitter * 10));
                    var (hit, score) = RuntimeMatch(bucket, drawn, opts);
                    if (hit == g) { ok++; okAll++; continue; }

                    var measured = Measure(bucket, drawn);
                    string why = hit != null
                        ? $"认成了 {hit.Name}"
                        : measured.Count == 0
                            ? "全部不可比（" + Reason(g, drawn) + "）"
                            : measured[0].D <= threshold
                              ? $"歧义：{measured[0].G.Name} {measured[0].D:0.0000} / {(measured.Count > 1 ? $"{measured[1].G.Name} {measured[1].D:0.0000}" : "-")}"
                              : $"距离 {measured[0].D:0.0000} 超阈（{measured[0].G.Name}）";
                    notes.Add($"手抖 {jitter:0.0}px → {why}");
                }

                string line = $"{g.Name}（模板 {TemplateCount(g)} 点）{(string.IsNullOrEmpty(g.AppPath) ? "" : "　[" + Path.GetFileNameWithoutExtension(g.AppPath) + "]")} → {ok}/3";
                if (ok < 3)
                {
                    failures.Add(line + "\n    " + string.Join("\n    ", notes));
                }
                sb.AppendLine(line);
            }
            sb.AppendLine();
            sb.AppendLine($"合计：{okAll}/{total} 次认出了自己（{(total == 0 ? 0 : okAll * 100 / total)}%）");
            sb.AppendLine();
            if (failures.Count > 0)
            {
                sb.AppendLine("== 认不出自己的 ==");
                foreach (var f in failures) sb.AppendLine(f);
                sb.AppendLine();
            }

            /* 撞车：两条模板彼此的距离已经小于阈值 —— 那么"画成其中一条"的另一半概率就是认错，
             * 这不是引擎的问题，是这两条笔画本来就一样，得改一条。 */
            var ties = new List<(string A, string B, double D)>();
            for (int i = 0; i < strokes.Count; i++)
            {
                for (int j = i + 1; j < strokes.Count; j++)
                {
                    string pa = strokes[i].AppPath ?? "", pb = strokes[j].AppPath ?? "";
                    if (pa.Length > 0 && pb.Length > 0 && !string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase))
                        continue;                                   // 分属两个程序，运行时不在同一个桶里
                    if (!string.Equals(strokes[i].Button ?? "", strokes[j].Button ?? "", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(strokes[i].Chord ?? "", strokes[j].Chord ?? "", StringComparison.OrdinalIgnoreCase))
                        continue;                                   // 起手键不同，运行时不会互相抢
                    var ta = GestureEngine.TemplateFor(strokes[i]);
                    var tb = GestureEngine.TemplateFor(strokes[j]);
                    if (ta == null || tb == null) continue;
                    double d = ta.DistanceTo(tb);
                    if (double.IsNaN(d) || double.IsPositiveInfinity(d) || d > threshold) continue;
                    ties.Add((strokes[i].Name, strokes[j].Name, d));
                }
            }

            sb.AppendLine("== 笔画撞车（两条形状几乎一样，画出来只能二选一）==");
            if (ties.Count == 0) sb.AppendLine("没有。");
            else foreach (var t in ties.OrderBy(t => t.D))
                sb.AppendLine($"{t.A}　≈　{t.B}　距离 {t.D:0.0000}");
            sb.AppendLine();

            var target = strokes.FirstOrDefault(g => g.Name.Contains(wanted, StringComparison.Ordinal));
            if (target == null)
            {
                sb.AppendLine($"没有找到名字里带 {wanted} 的手势。");
                Write(outPath, sb);
                return 2;
            }
            var targetShape = GestureEngine.ShapeOf(target);
            if (targetShape == null)
            {
                sb.AppendLine($"目标手势 {target.Name} 没有几何模板。");
                Write(outPath, sb);
                return 3;
            }

            sb.AppendLine($"== 目标手势：{target.Name}（{(string.IsNullOrEmpty(target.AppPath) ? "全局" : target.AppPath)}，模板 {TemplateCount(target)} 点，存了 {target.Path.Count} 个采样点）==");
            sb.AppendLine($"方向串：{(string.IsNullOrEmpty(target.Pattern) ? "-" : target.Pattern)}");
            sb.AppendLine();
            var targetBucket = Bucket(strokes, target);
            var trials = new (string Label, double Jitter, double Scale, double Rotate, int Seed)[]
            {
                ("一模一样", 0.0, 1.00, 0, 1),
                ("手抖 0.5px", 0.5, 1.00, 0, 2),
                ("手抖 1.5px", 1.5, 1.00, 0, 3),
                ("手抖 3px", 3.0, 1.00, 0, 4),
                ("画大 20%", 1.5, 1.20, 0, 5),
                ("画小 20%", 1.5, 0.80, 0, 6),
                ("歪 6 度", 1.5, 1.00, 6, 7),
                ("歪 12 度", 1.5, 1.00, 12, 8),
                ("画快（采样 4px 一记）", 0.5, 1.00, 0, 9),
            };

            foreach (var trial in trials)
            {
                double step = trial.Label.Contains("画快") ? 4.0 : 2.0;
                var drawn = Simulate(targetShape, trial.Jitter, trial.Scale, trial.Rotate, trial.Seed, step);
                var st = GestureEngine.DrawnStroke(drawn);
                var measured = Measure(targetBucket, drawn);
                var (hit, score) = RuntimeMatch(targetBucket, drawn, opts);

                string verdict;
                if (hit == target) verdict = "命中";
                else if (hit != null) verdict = $"命中了别的：{hit.Name}";
                else if (measured.Count == 0) verdict = "未识别：全部不可比（" + Reason(target, drawn) + "）";
                else if (measured[0].D <= threshold)
                    verdict = $"未识别：歧义（{measured[0].G.Name} {measured[0].D:0.0000} / {(measured.Count > 1 ? $"{measured[1].G.Name} {measured[1].D:0.0000}" : "-")}，容差 {(opts.WgTieMargin > 0 ? opts.WgTieMargin : 0.01):0.0000}）";
                else
                    verdict = $"未识别：最近 {measured[0].G.Name} {measured[0].D:0.0000} 超阈 {threshold:0.0000}";

                sb.AppendLine($"[{trial.Label}] 手绘折点 {(st?.Count.ToString() ?? "0")}　{verdict}　分数 {score:0.000}");
                if (measured.Count > 0)
                    sb.AppendLine("    最近五条：" + string.Join("，", measured.Take(5).Select(m => $"{m.G.Name} {m.D:0.0000}")));
            }

            Write(outPath, sb);
            return 0;
        }
        catch (Exception ex)
        {
            sb.AppendLine("诊断失败：" + ex);
            Write(outPath, sb);
            return 1;
        }
    }

    /// <summary>
    /// 运行期的候选桶：同一作用域 + 全局（分组命中这里不模拟）。
    ///
    /// 必须**按触发键分桶**：运行时 GestureService.Candidates 先按按下的那颗键
    /// （以及组合键）筛一遍，右键的 → 和滚轮键的 → 根本不会同台比较。
    /// 少了这一层，诊断里会凭空多出一堆"撞车"，看着像引擎认不出来，
    /// 其实是两条手势压根不用同一颗键起手。
    /// </summary>
    private static List<MouseGesture> Bucket(List<MouseGesture> all, MouseGesture target) =>
        all.Where(g => !g.IsWildcard && GestureEngine.CanMatch(g))
            .Where(g => string.IsNullOrEmpty(g.AppPath) || g.AppPath == target.AppPath)
            .Where(g => string.Equals(g.Button ?? "", target.Button ?? "", StringComparison.OrdinalIgnoreCase))
            .Where(g => string.Equals(g.Chord ?? "", target.Chord ?? "", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// 跟运行期一样分两轮：先只在"这个程序的"里找，找不到再退回全局。
    /// 少了这一层，一条专门给某个程序配的手势会被全局的同形手势抢走，
    /// 诊断就会报"认成了别人"，而实际运行时它是对的。
    /// </summary>
    private static (MouseGesture? Hit, double Score) RuntimeMatch(
        List<MouseGesture> bucket, IReadOnlyList<GestureMath.Pt> drawn, GestureOptions opts)
    {
        var scoped = bucket.Where(g => !string.IsNullOrWhiteSpace(g.AppPath)).ToList();
        var global = bucket.Where(g => string.IsNullOrWhiteSpace(g.AppPath)).ToList();
        foreach (var round in new[] { scoped, global })
        {
            if (round.Count == 0) continue;
            var (hit, score) = GestureEngine.MatchRelease(round, drawn, opts);
            if (hit != null) return (hit, score);
        }
        return (null, 1);
    }

    private static List<(MouseGesture G, double D)> Measure(List<MouseGesture> bucket, List<GestureMath.Pt> drawn)
    {
        var measured = new List<(MouseGesture G, double D)>();
        foreach (var g in bucket)
        {
            double d = GestureEngine.Distance(g, drawn);
            if (!double.IsNaN(d) && !double.IsPositiveInfinity(d)) measured.Add((g, d));
        }
        measured.Sort((a, b) => a.D.CompareTo(b.D));
        return measured;
    }

    private static int TemplateCount(MouseGesture g)
    {
        var path = GestureEngine.TemplateFor(g);
        if (path == null) return 0;
        foreach (var step in path.Steps)
            if (step is WgStrokeStep s) return s.Stroke.Count;
        return 0;
    }

    /// <summary>说清"为什么这条跟手绘不可比"：点太多、点数差太多、还是模板本身为空。</summary>
    private static string Reason(MouseGesture g, List<GestureMath.Pt> drawn)
    {
        var stroke = GestureEngine.DrawnStroke(drawn);
        int tc = TemplateCount(g);
        if (stroke == null) return "手绘太短，压不成一笔";
        if (tc == 0) return "模板是空的";
        if (stroke.Count > WgStroke.MaxPoints)
            return $"手绘折点 {stroke.Count} 个，超过引擎上限 {WgStroke.MaxPoints}";
        if (tc > stroke.Count * WgStroke.MaxPointRatio || stroke.Count > tc * WgStroke.MaxPointRatio)
            return $"模板 {tc} 点、手绘 {stroke.Count} 点，差超过 {WgStroke.MaxPointRatio} 倍";
        return $"模板 {tc} 点、手绘 {stroke.Count} 点，距离算出来不是有限值";
    }

    private static void Write(string path, StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            CoreLog.Info("手势诊断已写入：" + path);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("手势诊断写入失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把一条模板"手画"出来：按 500px 的尺度铺开，每 2px 记一个点、每点加一点垂直抖动，
    /// 再整体缩放 / 旋转。这套口径和运行期一致（钩子那边也是 2px 记一个点）。
    /// </summary>
    private static List<GestureMath.Pt> Simulate(
        IReadOnlyList<GestureMath.Pt> shape, double jitterPx, double scale, double rotateDeg, int seed, double step = 2.0)
    {
        var rng = new Random(seed);
        double rad = rotateDeg * Math.PI / 180.0;

        double minX = shape.Min(p => p.X), maxX = shape.Max(p => p.X);
        double minY = shape.Min(p => p.Y), maxY = shape.Max(p => p.Y);
        double span = Math.Max(1.0, Math.Max(maxX - minX, maxY - minY));
        double k = 500.0 / span * scale;

        var src = new List<GestureMath.Pt>(shape.Count);
        foreach (var p in shape)
        {
            double x = (p.X - minX) * k, y = (p.Y - minY) * k;
            src.Add(new GestureMath.Pt(
                x * Math.Cos(rad) - y * Math.Sin(rad) + 700,
                x * Math.Sin(rad) + y * Math.Cos(rad) + 500));
        }

        var drawn = new List<GestureMath.Pt> { src[0] };
        for (int i = 1; i < src.Count; i++)
        {
            double dx = src[i].X - src[i - 1].X, dy = src[i].Y - src[i - 1].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;

            double ux = dx / len, uy = dy / len;
            int n = Math.Max(1, (int)Math.Round(len / step));
            for (int m = 1; m <= n; m++)
            {
                double t = (double)m / n;
                double wobble = jitterPx <= 0 ? 0 : (rng.NextDouble() * 2 - 1) * jitterPx;
                drawn.Add(new GestureMath.Pt(
                    src[i - 1].X + dx * t - uy * wobble,
                    src[i - 1].Y + dy * t + ux * wobble));
            }
        }

        return drawn;
    }
}
