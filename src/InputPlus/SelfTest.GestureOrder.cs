using System;
using System.Collections.Generic;
using System.Linq;
using InputPlus.Core.Gestures;
using InputPlus.Core.Settings;

namespace InputPlus;

/* 手势列表的排序与拖动手柄（1.2 加的）。
 *
 * 排序有三档是"只读视图"（按名称 / 按触发方式 / 按动作），
 * 只有"自定义顺序"允许拖动手柄；拖动必须**只在自己看得见的那些行里挪**，
 * 否则被筛选/被程序限定挡住的手势会跟着一起乱。
 * 这两个约束都在这里钉死，免得以后改列表逻辑时悄悄破掉。 */
public static partial class SelfTest
{
    private static void TestGestureListOrder()
    {
        CheckEqual("排序方式默认是自定义顺序", new GestureOptions().ListSort, GestureListOrder.Custom);

        // 认不出来的词（老配置、手改坏的 json）一律退回自定义顺序
        CheckEqual("空排序方式 = 自定义", GestureListOrder.Normalize(null), GestureListOrder.Custom);
        CheckEqual("乱写的排序方式 = 自定义", GestureListOrder.Normalize("byColor"), GestureListOrder.Custom);
        CheckEqual("排序方式不区分大小写", GestureListOrder.Normalize("  NAME "), GestureListOrder.ByName);
        Check("只有自定义顺序是可拖的",
            GestureListOrder.IsCustom(GestureListOrder.Custom)
            && !GestureListOrder.IsCustom(GestureListOrder.ByName));

        var a = G("a", "Aaa");
        var b = G("b", "Bbb");
        var c = G("c", "Ccc");

        // 少于两条不动（拖也没意义）
        var one = new List<MouseGesture> { a };
        var oneSorted = GestureListOrder.Sort(one, GestureListOrder.ByName, g => g.Name);
        Check("只有一条时原样返回", oneSorted.Count == 1 && ReferenceEquals(oneSorted[0], a));

        var byName = GestureListOrder.Sort(new[] { c, a, b }, GestureListOrder.ByName, g => g.Name);
        CheckEqual("按名称：C A B → A B C", string.Join("", byName.Select(g => g.Id)), "abc");

        // 同名时保持原顺序（OrderBy 稳定）：否则每点一次下拉，列表就自己重排一次
        var tie = GestureListOrder.Sort(new[] { G("x", "Same"), G("y", "Same") }, GestureListOrder.ByName, g => g.Name);
        CheckEqual("按名称：同名保持原顺序", string.Join("", tie.Select(g => g.Id)), "xy");

        // 按触发方式：笔画 → 滚轮 → 角落 → 悬停角落 → 贴边滚轮
        var strokeR = G("r", "笔画右键");
        var strokeL = G("l", "笔画左键");
        strokeL.Button = "left";
        var wheelR = G("w", "滚轮");
        wheelR.Kind = "wheel"; wheelR.Wheel = 0;
        var cornerG = G("c", "角落");
        cornerG.Kind = "corner"; cornerG.Corner = 2;
        var hotG = G("h", "悬停角落");
        hotG.Kind = "hotcorner"; hotG.Corner = 1;
        var edgeG = G("e", "贴边滚轮");
        edgeG.Kind = "edgewheel"; edgeG.Edge = 3; edgeG.Wheel = 1;
        var byTrigger = GestureListOrder.Sort(
            new[] { edgeG, hotG, cornerG, wheelR, strokeR, strokeL },
            GestureListOrder.ByTrigger, g => g.Name);
        CheckEqual("按触发方式：笔画在前、贴边滚轮最后",
            string.Join("", byTrigger.Select(g => g.Id)), "lrwche");
        CheckEqual("按触发方式：同一类里按按键排（左键在右键前）", byTrigger[0].Button, "left");

        // 按动作：同一个动作的行必须挨在一起（标题是中文，排序结果跟语言有关，
        // 但"同类相邻"这一点与语言无关，钉这个更稳）
        // 用动作表里真有的两条（全选 / 行首）。中文标题的具体先后跟系统语言有关，
        // 所以只钉"同一个动作的行必须连在一起"，这条与语言无关。
        var p1 = G("p1", "甲"); p1.Action = "selectAll";
        var c1 = G("c1", "乙"); c1.Action = "home";
        var p2 = G("p2", "丙"); p2.Action = "selectAll";
        var c2 = G("c2", "丁"); c2.Action = "home";
        var byAction = GestureListOrder.Sort(new[] { p1, c1, p2, c2 }, GestureListOrder.ByAction, g => g.Name);
        CheckEqual("按动作：一条都不丢", byAction.Count, 4);
        var actionSeq = byAction.Select(g => g.Action).ToList();
        int groups = actionSeq.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int switches = actionSeq.Zip(actionSeq.Skip(1), (x, y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase) ? 0 : 1).Sum();
        Check("按动作：同一个动作的行连在一起", switches == groups - 1,
            string.Join(",", actionSeq));

        // ---- 拖动排序：只动看得见的那几条 ----
        var all = new List<MouseGesture> { a, b, c, G("d", "Ddd"), G("e", "Eee") };
        var visible = new List<MouseGesture> { all[0], all[2], all[4] };   // a c e
        int slot = GestureListOrder.MoveWithin(all, visible, 0, 3);        // 把 a 拖到最末尾
        CheckEqual("拖动：a 拖到末尾，落点是 e 原来的槽位", slot, 4);
        CheckEqual("拖动：整表只有可见的槽位换了位置（b/d 原地不动）",
            string.Join("", all.Select(g => g.Id)), "cbeda");

        // 拖到自己身上 / 拖到自己后面一格 = 没动
        var all2 = new List<MouseGesture> { a, b, c };
        CheckEqual("拖动：插到自己前面 = 没动", GestureListOrder.MoveWithin(all2, all2, 1, 1), 1);
        CheckEqual("拖动：插到自己后面 = 没动", GestureListOrder.MoveWithin(all2, all2, 1, 2), 1);
        CheckEqual("拖动：没动时表也不变", string.Join("", all2.Select(g => g.Id)), "abc");

        CheckEqual("拖动：下标越界不干活", GestureListOrder.MoveWithin(all2, all2, 9, 0), -1);
        CheckEqual("拖动：可见集合里混进了表外的条目就不干活",
            GestureListOrder.MoveWithin(all2, new List<MouseGesture> { a, G("zz", "Zzz") }, 0, 1), -1);

        // 手柄上下微调（键盘走的也是这条）
        var all3 = new List<MouseGesture> { a, b, c };
        Check("微调：往下挪一格", GestureListOrder.StepWithin(all3, all3, 0, 1) && all3[1] == a);
        Check("微调：已经到底再往下不动", !GestureListOrder.StepWithin(all3, all3, 2, 1));

        /* 左上角边缘手势为什么会丢：整表导入 WGestures 配置时，BuiltinsVersion 跟着
         * 老配置一起变成 11，补种段一次都没跑。1.2 把 Version 抬到 12，这次升级会把
         * 四角悬停 + 八条贴边滚轮补回来。
         * 另外 WGestures 的"到角落点一下"（kind=corner）和我们的"悬停在角落"
         * （kind=hotcorner）是两条不同的触发路径，同一个角落各留一条是对的行为 ——
         * 以前占位守卫把两种情况混在一起判，导入的「静音」(corner 1) 就把左上角
         * 永远挡住了。 */
        var imported = new AppSettings();
        imported.GestureOptions.BuiltinsVersion = 11;
        imported.Gestures.Add(new MouseGesture
        {
            Id = "mute-corner", Name = "静音", Kind = "corner", Button = "right",
            Corner = 1, Action = "volumeMute",
        });
        GestureDefaults.Ensure(imported);
        Check("导入过 WGestures 配置的班子也会把左上角悬停补回来",
            imported.Gestures.Any(g => g.Kind == "hotcorner" && g.Corner == 1),
            string.Join(",", imported.Gestures.Select(g => g.Kind + g.Corner)));
        Check("左上角悬停挂着切换软件",
            imported.Gestures.FirstOrDefault(g => g.Kind == "hotcorner" && g.Corner == 1)?.Action == "switchNextApp");
        Check("同一个角落的「静音」（到角落点一下）仍然在",
            imported.Gestures.Any(g => g.Id == "mute-corner"));
        CheckEqual("四角悬停齐全（左上/右上/右下/左下）",
            imported.Gestures.Count(g => g.Kind == "hotcorner"), 4);

        // 已经补过就不能再补一次
        int before = imported.Gestures.Count(g => g.Kind == "hotcorner");
        GestureDefaults.Ensure(imported);
        CheckEqual("补过一次不会再补第二次", imported.Gestures.Count(g => g.Kind == "hotcorner"), before);
    }

    private static MouseGesture G(string id, string name) => new()
    {
        Id = id, Name = name, Kind = "stroke", Button = "right", Pattern = "↓",
    };
}
