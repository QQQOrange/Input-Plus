using System;
using System.Collections.Generic;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 手势美化的"落库助手"：把 <see cref="StrokeBeautifier"/> 的结果写进
/// <see cref="MouseGesture"/>（Beauty / RawPath / BeautyLevel / BeautyVersion），
/// 并负责三件调用方不该各写一遍的事：
///
/// <list type="number">
/// <item><b>新建时美化</b>：录制回调里拿到原始轨迹就调 <see cref="Apply"/>；
///       开关关掉时它只清空美化字段，行为与老版本一致。</item>
/// <item><b>旧数据回填</b>：老手势没有 Beauty，第一次被用到时
///       <see cref="Ensure"/> 按当前设置补一份，回填结果会被调用方存盘。</item>
/// <item><b>重新美化 / 恢复原样</b>：设置页的按钮直接调 <see cref="Rebuild"/> / <see cref="UseRaw"/>。</item>
/// </list>
///
/// 为什么单独放一个类而不是塞进 <see cref="MouseGesture"/>：
/// 模型只存数据（还要进 JSON），算法会抛异常、要写日志、依赖设置 —— 那些都不该跟着模型走。
/// </summary>
public static class GestureBeauty
{
    /// <summary>
    /// 新建手势时调用：按设置决定档位，把原始轨迹（屏幕像素）美化后写进模型。
    /// </summary>
    /// <param name="g">刚构造好的手势（<see cref="MouseGesture.Path"/> 必须已经填好）。</param>
    /// <param name="rawPixels">手绘原始轨迹（像素）；给 null 就用模型里的归一化轨迹。</param>
    /// <param name="opts">当前手势设置；null = 出厂默认。</param>
    /// <param name="origin">日志前缀，写清是谁触发的（"录制" / "立即录制" / "回填"）。</param>
    /// <returns>美化结果；轨迹点不够时返回 null（模型已被清成"没有美化模板"）。</returns>
    public static StrokeBeautifyResult? Apply(
        MouseGesture? g, IReadOnlyList<GestureMath.Pt>? rawPixels, GestureOptions? opts, string origin)
    {
        if (g == null) return null;
        opts ??= GestureOptions.Default;
        var level = opts.BeautifyOnCreate ? opts.BeautyLevel : StrokeBeautyLevel.Off;
        return ApplyCore(g, rawPixels, level, origin);
    }

    /// <summary>
    /// 按当前档位**强制**重建一条手势的美化模板（设置页"重新美化"按钮）。
    /// 不看「创建时美化」总开关 —— 用户点了按钮就是要美化，哪怕那个开关是关的。
    /// </summary>
    public static StrokeBeautifyResult? Rebuild(MouseGesture? g, GestureOptions? opts, string origin)
    {
        if (g == null) return null;
        opts ??= GestureOptions.Default;
        return ApplyCore(g, null, opts.BeautyLevel, origin);
    }

    private static StrokeBeautifyResult? ApplyCore(
        MouseGesture g, IReadOnlyList<GestureMath.Pt>? rawPixels, StrokeBeautyLevel level, string origin)
    {
        /* 原始轨迹优先用调用方给的那份（录制时的像素点，没被校正过），
         * 没有才退回模型里的归一化轨迹 —— 重新美化走的就是后者。 */
        var raw = rawPixels != null && rawPixels.Count >= 2 ? rawPixels : g.Points();
        if (raw.Count < 2)
        {
            Clear(g);
            CoreLog.Info($"[手势] {origin}没有可用轨迹，未生成美化模板");
            return null;
        }

        /* 存档里的方向串是这条手势的"身份"：把它交给守门当语义基准。
         * 通配和空串不传 —— 它们没有方向语义，让 Beautify 用原轨迹的即时判定。 */
        string? expected = string.IsNullOrWhiteSpace(g.Pattern) || g.Pattern == GesturePattern.Wildcard
            ? null
            : g.Pattern;

        StrokeBeautifyResult result;
        try
        {
            result = StrokeBeautifier.Beautify(raw, level, expected);
        }
        catch (Exception ex)
        {
            /* 美化是"锦上添花"：算法里任何意外都不能让用户丢掉这一笔手势。 */
            CoreLog.Swallow(ex, "手势轨迹美化");
            Clear(g);
            return null;
        }

        g.RawPath = GestureMath.ToFlat(result.RawPoints);

        if (result.Beautified)
        {
            g.Beauty = GestureMath.ToFlat(result.Points);
            g.BeautyLevel = StrokeBeautyLevels.ToId(result.UsedLevel);
            g.BeautyVersion = StrokeBeautifier.TemplateVersion;
            CoreLog.Info(
                $"[手势] {origin}已美化（{StrokeBeautifier.DisplayName(result.UsedLevel)}" +
                (result.Fallback ? "，降档" : "") +
                $"，偏差 {result.Deviation:0.000}）：{result.Reason}");
        }
        else
        {
            g.Beauty = new List<int>();
            g.BeautyLevel = "";
            g.BeautyVersion = 0;
            CoreLog.Info($"[手势] {origin}保留原始轨迹：{result.Reason}");
        }

        return result;
    }

    /// <summary>
    /// 存量手势第一次被用到时补一份美化模板（<see cref="GestureOptions.BeautifyBackfill"/>）。
    /// 返回 true = 模型被改过，调用方需要存盘。
    /// </summary>
    public static bool Ensure(MouseGesture? g, GestureOptions? opts, string origin)
    {
        if (g == null) return false;
        opts ??= GestureOptions.Default;
        if (!opts.BeautifyOnCreate || !opts.BeautifyBackfill) return false;
        if (!g.HasPath) return false;
        if (g.HasBeauty && g.BeautyVersion >= StrokeBeautifier.TemplateVersion) return false;

        var result = ApplyCore(g, null, opts.BeautyLevel, origin);
        return result != null && result.Beautified;
    }

    /// <summary>批量补算的统计：界面和日志都靠它报「扫了几条、规整了几条、保留了几条」。</summary>
    public readonly record struct EnsureStats(int Scanned, int Beautified, int Kept, int Skipped)
    {
        /// <summary>有手势被真的改过 —— 调用方据此决定要不要写盘。</summary>
        public bool Changed => Beautified > 0;
    }

    /// <summary>
    /// 给一整批手势补算模板（打开手势列表时调用）。
    ///
    /// <para>为什么需要它：<see cref="Ensure"/> 只在「这条手势被命中触发」时才跑，
    /// 于是升级上来的老手势一直停在「原始轨迹」—— 列表缩略图、详情预览、命中后的替换图形
    /// 三处全都看不到美化效果，用户看到的现象就是「美化没生效」。</para>
    ///
    /// <para>已经算过（且模板版本是当前版本）的直接跳过，不重复算；
    /// 认不出来的一律保留原样（<see cref="EnsureStats.Kept"/>），下次算法升级会靠版本号再试一次。</para>
    /// </summary>
    public static EnsureStats EnsureAll(IEnumerable<MouseGesture>? list, GestureOptions? opts, string origin)
    {
        if (list == null) return new EnsureStats(0, 0, 0, 0);
        opts ??= GestureOptions.Default;
        int scanned = 0, beautified = 0, kept = 0, skipped = 0;
        foreach (var g in list)
        {
            if (g == null || !g.HasPath) continue;
            scanned++;
            if (g.HasBeauty && g.BeautyVersion >= StrokeBeautifier.TemplateVersion) { skipped++; continue; }
            if (!opts.BeautifyOnCreate || !opts.BeautifyBackfill) { skipped++; continue; }
            var result = ApplyCore(g, null, opts.BeautyLevel, origin);
            if (result != null && result.Beautified) beautified++;
            else kept++;
        }
        if (scanned > 0)
            CoreLog.Info($"[手势] {origin}：扫描 {scanned} 条，新美化 {beautified} 条，保留原样 {kept} 条，已是最新 {skipped} 条");
        return new EnsureStats(scanned, beautified, kept, skipped);
    }

    /// <summary>清掉美化模板（保留 Path 与 RawPath），显示与匹配退回原始轨迹。</summary>
    public static void Clear(MouseGesture? g)
    {
        if (g == null) return;
        if (g.Beauty == null) g.Beauty = new List<int>();
        else g.Beauty.Clear();
        g.BeautyLevel = "";
        g.BeautyVersion = 0;
    }

    /// <summary>
    /// 「恢复原始轨迹」：把存档的原始轨迹写回 <see cref="MouseGesture.Path"/> 并清掉美化模板。
    /// 没有原始轨迹（老数据）时返回 false，调用方据此提示用户。
    /// </summary>
    public static bool UseRaw(MouseGesture? g)
    {
        if (g == null || !g.HasRaw) return false;
        g.Path = new List<int>(g.RawPath);
        Clear(g);
        g.EnsurePattern();
        CoreLog.Info($"[手势] 已恢复原始轨迹（{g.Title()}）");
        return true;
    }

    /// <summary>
    /// 给界面显示用的一句话状态。
    /// 「没有模板」要分两种说：手绘但没过语义校验（保留手绘形状）、根本没有轨迹 ——
    /// 老文案一律写"原始轨迹"，用户会以为美化压根没跑。
    /// </summary>
    public static string Describe(MouseGesture? g)
    {
        if (g == null) return "原始轨迹";
        if (!g.HasBeauty) return g.HasPath ? "保留手绘形状" : "原始轨迹";
        var level = StrokeBeautyLevels.Parse(g.BeautyLevel);
        return $"已美化（{StrokeBeautifier.DisplayName(level)}）";
    }

    /// <summary>统计一批手势里有多少条已经用上标准图形（设置页那行「已有 X/Y 条」）。</summary>
    public static (int Beautified, int Total) Count(IEnumerable<MouseGesture>? list)
    {
        int beautified = 0, total = 0;
        foreach (var g in list ?? Array.Empty<MouseGesture>())
        {
            if (g == null || !g.HasPath) continue;
            total++;
            if (g.HasBeauty) beautified++;
        }
        return (beautified, total);
    }
}
