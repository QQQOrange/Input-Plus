using System;
using System.Collections.Generic;
using System.Linq;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 手势的身份（去重用）。
///
/// 同一条手势 = 同一个作用目标 + 同一种触发方式 + 同一颗起始键/组合键 +
/// 同一个笔画/角落/边缘。动作、名称、备注不参与比较：它们是要被改的东西，
/// 不能拿来区分“这是不是同一条手势”。
/// </summary>
public static class GestureIdentity
{
    public static string Signature(MouseGesture g)
    {
        string app = (g.AppPath ?? "").Trim().ToLowerInvariant();
        string kind = (g.Kind ?? "stroke").Trim().ToLowerInvariant();
        string button = (g.Button ?? "").Trim().ToLowerInvariant();
        string chord = (g.Chord ?? "").Trim().ToLowerInvariant();

        // 笔画优先用方向串；老配置没有方向串时退回扁平点列，至少不会把
        // “两个都没方向串”的完全不同笔画误判成同一条。
        string trigger;
        if (kind == "stroke")
        {
            g.EnsurePattern();
            /* 普通笔画不再有“边缘限制”：同方向串就是同一条手势。
             * Edge 只对贴边滚轮（edgewheel）有意义。 */
            trigger = !string.IsNullOrWhiteSpace(g.Pattern)
                ? $"p:{g.Pattern.Trim()}"
                : "d:" + string.Join(",", g.Path ?? new List<int>());
        }
        else if (kind == "wheel")
        {
            trigger = $"w:{g.Corner}:{g.Wheel}";
        }
        else if (kind == "edgewheel")
        {
            trigger = $"e:{g.Edge}:{g.Wheel}";
        }
        else if (kind is "corner" or "hotcorner")
        {
            trigger = $"c:{g.Corner}";
        }
        else
        {
            trigger = $"x:{g.Corner}:{g.Edge}:{g.Wheel}";
        }

        return $"{app}|{kind}|{button}|{chord}|{trigger}";
    }

    /// <summary>在同一批手势里找与 candidate 重复的那条；excludeId 用于“编辑自己”。</summary>
    public static MouseGesture? Find(IEnumerable<MouseGesture> gestures, MouseGesture candidate, string? excludeId)
    {
        string signature = Signature(candidate);
        return gestures.FirstOrDefault(g =>
            !string.Equals(g.Id, excludeId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Signature(g), signature, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 找形状相近的笔画手势（不是完全相同的方向串）。
    /// 用于保存时提醒“这条和已有手势很像，识别时可能互相抢”。
    /// </summary>
    public static (MouseGesture? Similar, double Score) FindSimilar(
        IEnumerable<MouseGesture> gestures, MouseGesture candidate, double threshold, string? excludeId)
    {
        if (candidate.Kind != "stroke" || !candidate.HasPath) return (null, 1);
        var path = GestureMath.Normalize(candidate.Points());
        MouseGesture? best = null;
        double bestScore = double.MaxValue;

        foreach (var g in gestures)
        {
            if (string.Equals(g.Id, excludeId, StringComparison.OrdinalIgnoreCase)) continue;
            if (g.Kind != "stroke" || !g.HasPath) continue;
            if (!string.Equals((g.AppPath ?? "").Trim(), (candidate.AppPath ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals((g.Button ?? "").Trim(), (candidate.Button ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals((g.Chord ?? "").Trim(), (candidate.Chord ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) continue;

            double score = GestureMath.Distance(path, g.Points());
            if (score < bestScore)
            {
                bestScore = score;
                best = g;
            }
        }

        return bestScore <= threshold ? (best, bestScore) : (null, bestScore);
    }

    /// <summary>
    /// 清掉历史配置里的重复项，保留最早出现的一条。
    ///
    /// 判断"重复"要两层：**触发方式相同**（签名一致）**且形状也相同**。
    /// 只看签名会误删真手势：签名里的笔画部分用的是方向串，而"↓→"这种串
    /// 只记了先往下再往右，不记比例 —— 于是「关闭」（小拐角）和「休眠」
    /// （大 L 形）会撞成一个签名，被当成重复删掉一条，用户看到的就是
    /// "导入进来的手势少了一个"。形状差得开就都留着，交给识别引擎按距离分。
    /// </summary>
    public static int RemoveDuplicates(List<MouseGesture> gestures)
    {
        if (gestures == null || gestures.Count < 2) return 0;

        var kept = new List<MouseGesture>(gestures.Count);
        int removed = 0;
        foreach (var g in gestures)
        {
            if (g == null) continue;
            var twin = kept.FirstOrDefault(k => SameTrigger(k, g));
            if (twin != null && SameShape(twin, g))
            {
                /* 说清楚删了哪一条：这条路径在启动时静默跑，用户只会发现
                 * "我那条手势没了"，日志里得能找到原因。 */
                CoreLog.Info($"[手势] 「{g.Name}」和「{twin.Name}」画法一样（{twin.Button} {twin.Pattern}），只保留排在前面的「{twin.Name}」");
                removed++;
                continue;
            }
            kept.Add(g);
        }

        if (removed == 0) return 0;
        gestures.Clear();
        gestures.AddRange(kept);
        return removed;
    }

    /// <summary>触发方式是否完全一样（作用目标 + 类型 + 起手键 + 组合键 + 笔画方向串）。</summary>
    public static bool SameTrigger(MouseGesture a, MouseGesture b) =>
        string.Equals(Signature(a), Signature(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 两条是不是同一笔画：都记过轨迹就比形状；有一条只有方向串，才退回按串相同算重复。
    /// </summary>
    private static bool SameShape(MouseGesture a, MouseGesture b)
    {
        if (!string.Equals(a.Kind ?? "", b.Kind ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.Kind, "stroke", StringComparison.OrdinalIgnoreCase)) return true;

        if (a.HasPath && b.HasPath)
        {
            /* 方向串一样，就只剩"形状差多少"这一道关。
             *
             * 门槛定在 2%（1000 空间），不是 0.2%：0.2% 曾放过「关闭」和「休眠」
             * 这一对（都是右键 ↓→，形状只差 0.0071），可手画的时候这点差别根本
             * 分不出来 —— 引擎按距离随便挑一条，用户画「关闭」结果触发了「休眠」。
             * 2% 以内算"手画不出来的差别"，直接按重复收掉；超过 2% 的两条
             * （比如一条直着一拐、和一条先往下再往右的 L 形）仍然各自保留。 */
            if (SamePoints(a, b)) return true;
            return GestureMath.Distance(a.Points(), b.Points()) <= 0.02;
        }
        return true;
    }

    /// <summary>两条手势存下来的点阵是否逐点一致。</summary>
    private static bool SamePoints(MouseGesture a, MouseGesture b)
    {
        if (a.Path.Count != b.Path.Count) return false;
        for (int i = 0; i < a.Path.Count; i++)
            if (a.Path[i] != b.Path[i]) return false;
        return true;
    }
}
