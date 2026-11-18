using System;
using System.Collections.Generic;
using System.Linq;
using InputPlus.Core.Actions;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 手势列表的排序口径，以及"按住手柄拖动改顺序"要用到的搬移逻辑。
///
/// 为什么单独放一个类：这一页的列表是"整表的一个子集"（全局 / 某个应用 / 某个分组 /
/// 边缘手势各自一段，还能再按按键筛），拖动只该动看得见的那几条，
/// 别的应用的手势位置不能被顺手搅乱。这套槽位算术放到界面里就没人验得了，
/// 所以做成纯函数，自检直接调。
/// </summary>
public static class GestureListOrder
{
    /// <summary>自定义顺序：就是 <c>Settings.Gestures</c> 里的物理顺序，拖出来的就是它。</summary>
    public const string Custom = "custom";

    /// <summary>按名称：标题的字典序。</summary>
    public const string ByName = "name";

    /// <summary>按触发方式：先按类型（笔画 → 滚轮 → 角落 → 热角 → 贴边滚轮），再按键位。</summary>
    public const string ByTrigger = "trigger";

    /// <summary>按动作：动作标题的字典序。</summary>
    public const string ByAction = "action";

    /// <summary>下拉里的四档，按显示顺序。</summary>
    public static readonly string[] Modes = { Custom, ByName, ByTrigger, ByAction };

    /// <summary>把配置里读出来的字符串收敛成四档之一；不认识的一律当自定义顺序。</summary>
    public static string Normalize(string? mode)
    {
        string m = (mode ?? "").Trim().ToLowerInvariant();
        foreach (string known in Modes)
            if (string.Equals(m, known, StringComparison.Ordinal)) return known;
        return Custom;
    }

    /// <summary>这一档是不是"用户自己拖出来的顺序"（只有它允许拖动）。</summary>
    public static bool IsCustom(string? mode) => string.Equals(Normalize(mode), Custom, StringComparison.Ordinal);

    /// <summary>
    /// 按某一档排序。<paramref name="titleOf"/> 是"这条手势显示成什么标题"
    /// （边缘手势的标题是"左上角""上边缘 · 滚轮向上"，由页面拼出来，模型里没有），
    /// 不传就退回 <see cref="MouseGesture.Name"/>。
    ///
    /// 用 OrderBy + 第二/第三把钥匙：稳定排序 ⇒ 同一把钥匙的条目保持原相对顺序，
    /// 来回切排序不会把用户拖好的表搅乱。
    /// </summary>
    public static List<MouseGesture> Sort(
        IEnumerable<MouseGesture>? gestures, string? mode, Func<MouseGesture, string>? titleOf = null)
    {
        var list = new List<MouseGesture>();
        if (gestures != null) list.AddRange(gestures);
        string picked = Normalize(mode);
        if (picked == Custom || list.Count < 2) return list;

        Func<MouseGesture, string> title = titleOf ?? (g => g.Name ?? "");
        switch (picked)
        {
            case ByName:
                return list
                    .OrderBy(g => title(g), StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(g => g.Kind ?? "", StringComparer.Ordinal)
                    .ThenBy(g => g.Pattern ?? "", StringComparer.Ordinal)
                    .ToList();

            case ByTrigger:
                return list
                    .OrderBy(g => KindRank(g.Kind))
                    .ThenBy(g => ButtonRank(g.Button))
                    .ThenBy(g => ChordRank(g.Chord))
                    .ThenBy(g => g.Corner)
                    .ThenBy(g => g.Edge)
                    .ThenBy(g => g.Wheel)
                    .ThenBy(g => title(g), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

            case ByAction:
                return list
                    .OrderBy(ActionTitle, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(g => title(g), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

            default:
                return list;
        }
    }

    /// <summary>
    /// 在"只显示了一部分"的列表里把第 <paramref name="from"/> 条搬到
    /// <paramref name="insertBefore"/> 这个插入点之前（等于可见条数 = 搬到最末尾）。
    ///
    /// 做法：先按引用找出可见条目在整表里的槽位，重排之后**只往这些槽位上回填**，
    /// 其余位置的条目原地不动 —— 所以"全局"里拖动不会把某个应用专属手势挤到别处去。
    /// </summary>
    /// <returns>搬完之后这条在整表里的新下标；没搬（参数越界、视图与表对不上）返回 -1。</returns>
    public static int MoveWithin(
        List<MouseGesture>? all, IReadOnlyList<MouseGesture>? visible, int from, int insertBefore)
    {
        if (all == null || visible == null) return -1;
        if (from < 0 || from >= visible.Count) return -1;

        var visibleSet = new HashSet<MouseGesture>(visible);
        var slots = new List<int>();
        for (int i = 0; i < all.Count; i++)
            if (visibleSet.Contains(all[i])) slots.Add(i);
        if (slots.Count != visible.Count || slots.Count == 0) return -1;

        var ordered = visible.ToList();
        var moving = ordered[from];
        ordered.RemoveAt(from);
        int to = insertBefore;
        if (to > from) to--;                 // 插到后面时自己已经被抽走了，插入点要往前挪一格
        to = Math.Clamp(to, 0, ordered.Count);
        ordered.Insert(to, moving);

        for (int k = 0; k < slots.Count; k++) all[slots[k]] = ordered[k];
        return slots[to];
    }

    /// <summary>按名称把一条手势在整表里往前/往后挪一格（给键盘快捷键和自检用）。</summary>
    public static bool StepWithin(List<MouseGesture>? all, IReadOnlyList<MouseGesture>? visible, int index, int delta)
    {
        if (all == null || visible == null) return false;
        int target = index + delta;
        if (index < 0 || index >= visible.Count || target < 0 || target >= visible.Count) return false;
        return MoveWithin(all, visible, index, delta > 0 ? target + 1 : target) >= 0;
    }

    private static string ActionTitle(MouseGesture g)
    {
        string title = ActionCatalog.TitleOf(g.Action) ?? "";
        return title.Length > 0 ? title : "\uFFFF";   // 没设动作的排最后（占位符比任何中文都大）
    }

    private static int KindRank(string? kind) => (kind ?? "") switch
    {
        "stroke" => 0,
        "wheel" => 1,
        "corner" => 2,
        "hotcorner" => 3,
        "edgewheel" => 4,
        _ => 5,
    };

    private static int ButtonRank(string? button) => (button ?? "").ToLowerInvariant() switch
    {
        "left" => 0,
        "right" => 1,
        "middle" => 2,
        "x1" => 3,
        "x2" => 4,
        _ => 5,                                      // 边缘手势没有按键，排在最后
    };

    private static int ChordRank(string? chord) => string.IsNullOrWhiteSpace(chord) ? 0 : 1;
}
