using System;
using System.Collections.Generic;
using System.Linq;
using InputPlus.Core.Gestures;
using InputPlus.UI.Windows;

namespace InputPlus;

/// <summary>
/// 自检：手势美化（任务四）。
///
/// 分三层验：
/// <list type="number">
/// <item><b>算法</b>：抖动的手绘轨迹经过 <see cref="StrokeBeautifier"/> 之后，
///       方向串（语义）与识别器打分都还得是"同一个手势"，太小的笔画与关闭档要原样返回。</item>
/// <item><b>落库</b>：<see cref="GestureBeauty"/> 写/清/回填/恢复原始轨迹四件事，
///       以及 <see cref="MouseGesture.Clone"/> 会不会把模板字段带过去（漏一个就是存档丢模板）。</item>
/// <item><b>引擎与覆盖层</b>：<see cref="GestureEngine.ShapeOf"/> 优先用模板；
///       <see cref="GestureOverlayWindow.ReplaceTrailWithTemplate"/> 能把模板画上、把旧轨迹清掉。</item>
/// </list>
/// </summary>
public static partial class SelfTest
{
    private static void TestGestureBeauty()
    {
        // ---- ① 抖动的手绘 L：该美化的要美化，且方向语义不许变 ----
        // 采样密度按真实鼠标来（10px 左右一个点、±3px 抖动）：光标的 125Hz 采样
        // 在 400px 的笔画上大约就是这个密度。更极端的手抖见 ①b。
        var shakyL = Combine(
            JitterLine(120, 120, 520, 150, 40, 3, seed: 11),
            JitterLine(520, 150, 540, 520, 40, 3, seed: 12));

        var lResult = StrokeBeautifier.Beautify(shakyL, StrokeBeautyLevel.Medium);
        Check("美化：抖动 L 形生成模板", lResult.Beautified, lResult.Reason);
        Check("美化：L 形方向串不变", lResult.SameSemantics,
            $"{lResult.PatternBefore} -> {lResult.PatternAfter}");
        Check("美化：L 形识别器距离在阈值内", lResult.Deviation <= 0.055 + 0.0001,
            lResult.Deviation.ToString("0.000"));
        Check("美化：输出点数受控", lResult.Points.Count is >= 2 and <= 128,
            lResult.Points.Count.ToString());

        // ---- ①b 识别器自己把重抖动 L 读成 ↘：如实回退；有存档方向时按存档判 ----
        var heavyL = Combine(
            JitterLine(120, 120, 520, 150, 24, 5, seed: 11),
            JitterLine(520, 150, 540, 520, 24, 5, seed: 12));
        var heavyResult = StrokeBeautifier.Beautify(heavyL, StrokeBeautyLevel.Medium);
        Check("美化：识别器读作斜线的重抖动笔按回退处理",
            !heavyResult.Beautified &&
            heavyResult.Reason.Contains("保留原始轨迹", StringComparison.Ordinal),
            $"{heavyResult.PatternBefore} / {heavyResult.Reason}");
        Check("美化：回退时方向串记的是识别器判定", heavyResult.RawPattern == heavyResult.PatternBefore,
            $"{heavyResult.RawPattern} vs {heavyResult.PatternBefore}");

        var heavyGesture = new MouseGesture
        {
            Id = MouseGesture.NewId(), Kind = "stroke", Action = "sendKeys", Arg = "ctrl+l",
            Path = GestureMath.ToFlat(GestureMath.Normalize(heavyL)),
            Pattern = "→↓",   // 录制时按真实像素判出来的方向：存档里就是这个
        };
        var heavyApplied = GestureBeauty.Apply(heavyGesture, heavyL, new GestureOptions(), "自检重抖动");
        Check("美化：按手势存档方向校验时重抖动 L 也能规整",
            heavyGesture.HasBeauty && heavyApplied is { Beautified: true },
            heavyApplied?.Reason ?? "null");

        // ---- ② 竖线：吸附之后应当基本没有横向漂移 ----
        var shakyV = JitterLine(400, 100, 410, 620, 30, 6, seed: 21);
        var vResult = StrokeBeautifier.Beautify(shakyV, StrokeBeautyLevel.Medium);
        double vSpread = vResult.Points.Count == 0
            ? double.MaxValue
            : vResult.Points.Max(p => p.X) - vResult.Points.Min(p => p.X);
        Check("美化：竖线生成模板", vResult.Beautified, vResult.Reason);
        Check("美化：竖线被拉直（横向残余 < 8/1000）", vSpread < 8, vSpread.ToString("0.0"));

        // ---- ③ 圆圈：闭合性要保住（首尾距离接近 0） ----
        var circle = JitterCircle(500, 500, 170, 64, 6, seed: 31);
        var cResult = StrokeBeautifier.Beautify(circle, StrokeBeautyLevel.Medium);
        double closure = cResult.Points.Count < 2
            ? double.MaxValue
            : Distance(cResult.Points[0], cResult.Points[^1]);
        Check("美化：圆圈生成模板", cResult.Beautified, cResult.Reason);
        Check("美化：圆圈仍然闭合（首尾 < 40/1000）", closure < 40, closure.ToString("0.0"));

        // ---- ④ 随手涂鸦：不许抛异常，退回原样时 Points 就是原轨迹 ----
        var scribble = new List<GestureMath.Pt>();
        var rnd = new Random(41);
        for (int i = 0; i < 60; i++)
            scribble.Add(new GestureMath.Pt(100 + rnd.Next(0, 500), 100 + rnd.Next(0, 400)));
        StrokeBeautifyResult scribbleResult;
        try
        {
            scribbleResult = StrokeBeautifier.Beautify(scribble, StrokeBeautyLevel.Strong);
            Check("美化：涂鸦不抛异常且有点输出", scribbleResult.Points.Count >= 2,
                scribbleResult.Points.Count.ToString());
            Check("美化：涂鸦要么过守门要么原样",
                !scribbleResult.Beautified || scribbleResult.SameSemantics,
                $"{scribbleResult.PatternBefore} -> {scribbleResult.PatternAfter}");
        }
        catch (Exception ex)
        {
            Check("美化：涂鸦不抛异常", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ---- ⑤ 边界：关闭档、太小的一笔、点数上限 ----
        var off = StrokeBeautifier.Beautify(shakyL, StrokeBeautyLevel.Off);
        Check("美化：关闭档不生成模板", !off.Beautified && !off.Fallback, off.Reason);

        var tiny = new List<GestureMath.Pt> { new(10, 10), new(16, 14), new(20, 18) };
        var tinyResult = StrokeBeautifier.Beautify(tiny, StrokeBeautyLevel.Medium);
        Check("美化：太小的笔画原样保留", !tinyResult.Beautified, tinyResult.Reason);

        var dense = new List<GestureMath.Pt>();
        for (int i = 0; i < 5000; i++)
            dense.Add(new GestureMath.Pt(i * 0.2, 300 + (i % 7)));
        var denseResult = StrokeBeautifier.Beautify(dense, StrokeBeautyLevel.Light);
        Check("美化：5000 点被抽样到上限以内",
            denseResult.RawPoints.Count <= StrokeBeautifier.MaxRawPoints + 1,
            denseResult.RawPoints.Count.ToString());

        // ---- ⑥ 落库：Apply / Ensure / UseRaw / 开关 ----
        var opts = new GestureOptions { BeautifyOnCreate = true, BeautifyBackfill = true };
        var created = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Kind = "stroke",
            Action = "sendKeys",
            Arg = "ctrl+c",
            Path = GestureMath.ToFlat(GestureMath.Normalize(shakyL)),
        };
        created.EnsurePattern();
        var applyResult = GestureBeauty.Apply(created, shakyL, opts, "自检");
        Check("落库：创建时美化写进模型", created.HasBeauty && applyResult is { Beautified: true },
            applyResult?.Reason ?? "null");
        Check("落库：模板档位与版本写对",
            created.BeautyLevel is "light" or "medium" or "strong" &&
            created.BeautyVersion == StrokeBeautifier.TemplateVersion,
            $"{created.BeautyLevel}/{created.BeautyVersion}");
        Check("落库：原始轨迹一并存档", created.HasRaw);

        var offOpts = new GestureOptions { BeautifyOnCreate = false };
        var offGesture = new MouseGesture
        {
            Id = MouseGesture.NewId(), Kind = "stroke", Action = "sendKeys", Arg = "ctrl+v",
            Path = GestureMath.ToFlat(GestureMath.Normalize(shakyL)),
        };
        offGesture.EnsurePattern();
        GestureBeauty.Apply(offGesture, shakyL, offOpts, "自检");
        Check("落库：关掉开关不生成模板", !offGesture.HasBeauty);
        Check("落库：关掉开关仍存原始轨迹", offGesture.HasRaw);

        var legacy = new MouseGesture
        {
            Id = MouseGesture.NewId(), Kind = "stroke", Action = "sendKeys", Arg = "ctrl+x",
            Path = GestureMath.ToFlat(GestureMath.Normalize(shakyL)),
        };
        legacy.EnsurePattern();
        bool ensured = GestureBeauty.Ensure(legacy, opts, "自检回填");
        Check("落库：旧手势回填生成模板", ensured && legacy.HasBeauty, ensured ? "" : "Ensure=false");
        Check("落库：回填幂等", !GestureBeauty.Ensure(legacy, opts, "自检回填"), "第二次仍返回 true");

        var noBackfill = new GestureOptions { BeautifyOnCreate = true, BeautifyBackfill = false };
        var legacy2 = legacy.Clone();
        legacy2.Beauty.Clear();
        legacy2.BeautyLevel = "";
        legacy2.BeautyVersion = 0;
        Check("落库：关掉回填不补算", !GestureBeauty.Ensure(legacy2, noBackfill, "自检回填"));

        var useRaw = created.Clone();
        var rawBefore = new List<int>(useRaw.RawPath);
        bool restored = GestureBeauty.UseRaw(useRaw);
        Check("落库：恢复原始轨迹", restored && !useRaw.HasBeauty && useRaw.Path.SequenceEqual(rawBefore),
            restored ? "" : "UseRaw=false");

        var cloned = created.Clone();
        Check("落库：Clone 带上模板字段",
            cloned.Beauty.SequenceEqual(created.Beauty) &&
            cloned.RawPath.SequenceEqual(created.RawPath) &&
            cloned.BeautyLevel == created.BeautyLevel &&
            cloned.BeautyVersion == created.BeautyVersion);

        var copy = new MouseGesture { Id = MouseGesture.NewId() };
        copy.CopyFrom(created);
        Check("落库：CopyFrom 带上模板字段",
            copy.Beauty.SequenceEqual(created.Beauty) &&
            copy.RawPath.SequenceEqual(created.RawPath) &&
            copy.BeautyLevel == created.BeautyLevel);

        // ---- ⑦ 引擎：ShapeOf 优先模板；用模板匹配同一个手势仍然命中 ----
        var shape = GestureEngine.ShapeOf(created);
        Check("引擎：ShapeOf 优先用手势模板",
            shape != null && shape.Count == created.BeautyPoints().Count &&
            Math.Abs(shape[0].X - created.BeautyPoints()[0].X) < 1e-9,
            shape == null ? "null" : shape.Count.ToString());

        var drawAgain = Combine(
            JitterLine(160, 160, 560, 190, 24, 6, seed: 77),
            JitterLine(560, 190, 580, 560, 24, 6, seed: 78));
        var (hit, _) = GestureEngine.Match(new[] { created }, drawAgain, opts);
        CheckEqual("引擎：模板手势仍然认得出同一笔", hit?.Id, created.Id);

        var downStroke = JitterLine(300, 150, 300, 650, 30, 4, seed: 79);
        var (miss, _) = GestureEngine.Match(new[] { created }, downStroke, opts);
        Check("引擎：不同手势不会误命中", miss == null, miss?.Id ?? "");

        // ---- ⑧ 覆盖层：模板画上、旧轨迹清掉、过渡时长按设置返回 ----
        try
        {
            var overlay = new GestureOverlayWindow();
            var templatePoints = created.BeautyPoints();
            Check("覆盖层：手势有模板可替换", templatePoints.Count >= 2, templatePoints.Count.ToString());
            int ms = overlay.ReplaceTrailWithTemplate(templatePoints, 0);
            Check("覆盖层：立即替换返回 0", ms == 0, ms.ToString());
            Check("覆盖层：模板已画上、旧轨迹已清",
                overlay.TemplatePath.Data != null && overlay.Trail.Data == null,
                overlay.TemplatePath.Data == null ? "模板空" : "旧轨迹未清");

            int animated = overlay.ReplaceTrailWithTemplate(templatePoints, 140);
            Check("覆盖层：过渡替换返回实际时长", animated == 140, animated.ToString());
            overlay.ClearTrail();
            Check("覆盖层：清轨迹会连模板一起复位", overlay.TemplatePath.Data == null);
        }
        catch (Exception ex)
        {
            Check("覆盖层：替换模板可用", false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ───────────────────────── 造点工具 ─────────────────────────

    /// <summary>一段带确定性抖动的直线（同 seed 每次生成的轨迹完全一样）。</summary>
    private static List<GestureMath.Pt> JitterLine(
        double x0, double y0, double x1, double y1, int steps, double jitter, int seed)
    {
        var rnd = new Random(seed);
        var list = new List<GestureMath.Pt>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            list.Add(new GestureMath.Pt(
                x0 + (x1 - x0) * t + (rnd.NextDouble() * 2 - 1) * jitter,
                y0 + (y1 - y0) * t + (rnd.NextDouble() * 2 - 1) * jitter));
        }
        return list;
    }

    /// <summary>把几段折线接成一笔（去掉每段的重复起点）。</summary>
    private static List<GestureMath.Pt> Combine(params List<GestureMath.Pt>[] parts)
    {
        var list = new List<GestureMath.Pt>();
        foreach (var part in parts)
        {
            int start = list.Count > 0 && part.Count > 0 &&
                        Math.Abs(list[^1].X - part[0].X) < 0.001 &&
                        Math.Abs(list[^1].Y - part[0].Y) < 0.001
                ? 1
                : 0;
            for (int i = start; i < part.Count; i++) list.Add(part[i]);
        }
        return list;
    }

    /// <summary>一圈带径向抖动的圆（起点在正上方，顺时针）。</summary>
    private static List<GestureMath.Pt> JitterCircle(
        double cx, double cy, double radius, int steps, double jitter, int seed)
    {
        var rnd = new Random(seed);
        var list = new List<GestureMath.Pt>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double a = -Math.PI / 2 + Math.PI * 2 * i / steps;
            double r = radius + (rnd.NextDouble() * 2 - 1) * jitter;
            list.Add(new GestureMath.Pt(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r));
        }
        return list;
    }

    private static double Distance(GestureMath.Pt a, GestureMath.Pt b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
