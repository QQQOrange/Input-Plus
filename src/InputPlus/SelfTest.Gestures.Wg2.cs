using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using InputPlus.Core.Gestures;
using InputPlus.UI;

namespace InputPlus;

/// <summary>
/// 自检：WGestures 2 的「作用目标 / 触发方式 / 外观偏好」这一层（Core/Gestures/Wg2Triggers.cs）。
///
/// 这一层不改识别，改的是"这一笔算不算数、画的时候长什么样"，所以用例盯的是
/// 继承顺序与三态覆盖 —— 顺序错了，用户把某个键在某个程序里关掉也不会生效。
/// </summary>
public static partial class SelfTest
{
    private static void TestWg2Layer()
    {
        Section("WGestures 2 作用目标与触发方式");

        // ---- 颜色：WG2 存的是 RGBA，WPF 用的是 ARGB，顺序搞反就是一片透明 ----
        var purple = Wg2Color.Parse(Wg2Color.PathColorRecognized, Colors.Black);
        // #FF5900FF 按 RGBA 读是"不透明的橙红"；按 WPF 的 ARGB 读会变成紫蓝 —— 这一条就是防这个。
        Check("WG2 颜色：RGBA 解析（#FF5900FF 是橙红，不是紫蓝）",
            purple.A == 0xFF && purple.R == 0xFF && purple.G == 0x59 && purple.B == 0x00,
            $"{purple.A:X2}{purple.R:X2}{purple.G:X2}{purple.B:X2}");
        CheckEqual("WG2 颜色：解析再格式化还是原串",
            Wg2Color.Format(purple), Wg2Color.PathColorRecognized);

        var faint = Wg2Color.Parse(Wg2Color.PathColorNormal, Colors.Black);
        Check("WG2 颜色：默认未识别色是 0x30 的透明度（A 在最后两位）",
            faint.A == 0x30, $"{faint.A:X2}");

        CheckEqual("WG2 颜色：坏值退回默认色",
            Wg2Color.Normalize("不是颜色", Wg2Color.PathColorNormal), Wg2Color.PathColorNormal);

        // ---- 触发方式：61 条 = 5 鼠标键 + 8 个位置 ×（滑动 + 滚轮 + 5 键）----
        CheckEqual("WG2 触发方式：一共 61 条", Wg2Triggers.All.Count, 61);
        CheckEqual("WG2 触发方式：没有重复",
            Wg2Triggers.All.Distinct(StringComparer.Ordinal).Count(), 61);
        Check("WG2 触发方式：每条都有中文名",
            Wg2Triggers.All.All(k => Wg2Triggers.Label(k).Length > 0 && !Wg2Triggers.Label(k).Contains(':')),
            Wg2Triggers.All.FirstOrDefault(k => Wg2Triggers.Label(k).Contains(':') || Wg2Triggers.Label(k).Length == 0));
        CheckEqual("WG2 触发方式：8 个位置（4 边 + 4 角）", Wg2Triggers.Positions.Length, 8);
        CheckEqual("WG2 触发方式：角落编号对位掩码",
            string.Join(",", new[] { 1, 2, 3, 4 }.Select(Wg2Triggers.MaskOfCorner)), "9,3,12,6");
        CheckEqual("WG2 触发方式：虚拟键码对鼠标键编号",
            string.Join(",", new[] { 0x01, 0x02, 0x04, 0x05, 0x06 }.Select(Wg2Triggers.MouseKeyOfVk)),
            "mouse:1,mouse:2,mouse:0,mouse:3,mouse:4");
        CheckEqual("WG2 触发方式：键盘键码不算鼠标触发方式",
            Wg2Triggers.MouseKeyOfVk(0x41), "");
        CheckEqual("WG2 触发方式：位置 · 动作的拼法",
            Wg2Triggers.Label("edge:1:scroll"),
            Wg2Triggers.PositionName(Wg2Triggers.EdgeTop) + " · " + Loc.T("滚轮"));

        // ---- 作用目标：全局 + 桌面 + 任务栏是出厂三件套 ----
        var fresh = GestureTargetDefaults.All();
        CheckEqual("WG2 作用目标：出厂三个", fresh.Count, 3);
        CheckEqual("WG2 作用目标：顺序是 全局 / 桌面 / 任务栏",
            string.Join(",", fresh.Select(t => t.Kind)),
            "Global,Desktop,Taskbar");
        CheckEqual("WG2 作用目标：全局默认 61 条全启用",
            fresh[0].Triggers.Count(kv => kv.Value), 61);

        var partial = new List<GestureTarget>
        {
            new() { Id = "group:a", Name = "临时组", Members = new List<string> { "C:/Tools/a.exe" } }
        };
        GestureTargetDefaults.Ensure(partial);
        CheckEqual("WG2 作用目标：老配置缺全局时补在最前面",
            partial[0].Kind, GestureTargetKind.Global);
        Check("WG2 作用目标：补齐后分组还在",
            partial.Any(t => t.Id == "group:a"));
        int before = partial.Count;
        GestureTargetDefaults.Ensure(partial);
        CheckEqual("WG2 作用目标：补齐是幂等的", partial.Count, before);

        // ---- 继承顺序：特殊目标 → 应用 → 分组 → 全局 ----
        var app = "C:/Apps/browser.exe";
        var grouped = "C:/Apps/editor.exe";
        var options = new GestureOptions
        {
            TargetList = new List<GestureTarget>
            {
                GestureTargetDefaults.Global(),
                new()
                {
                    Id = "group:dev", Name = "开发工具",
                    Members = new List<string> { grouped },
                    Triggers = new Dictionary<string, bool> { ["mouse:2"] = false },
                },
                new()
                {
                    Id = app, Name = "browser",
                    AppPath = app,
                    Triggers = new Dictionary<string, bool> { ["mouse:2"] = true },
                },
                GestureTargetDefaults.Desktop(),
                GestureTargetDefaults.Taskbar(),
            }
        };
        var map = new GestureTriggerMap(options);

        Check("WG2 继承：没人设置过的触发方式默认允许",
            map.Allows("C:/Apps/other.exe", false, false, "mouse:4"));
        Check("WG2 继承：应用自己的设置优先于全局",
            map.Allows(app, false, false, "mouse:2"));
        Check("WG2 继承：分组设置对组内程序生效",
            !map.Allows(grouped, false, false, "mouse:2"));
        Check("WG2 继承：组外程序不受分组影响",
            map.Allows("C:/Apps/other.exe", false, false, "mouse:2"));

        options.TargetList.First(t => t.Id == "group:dev").Triggers["mouse:2"] = true;
        map = new GestureTriggerMap(options);
        Check("WG2 继承：全局关掉后，分组启用能把它打开",
            map.Allows(grouped, false, false, "mouse:2"));

        options.TargetList.First(t => t.Id == app).Triggers["mouse:2"] = false;
        map = new GestureTriggerMap(options);
        Check("WG2 继承：应用设置压过所属分组",
            !map.Allows(app, false, false, "mouse:2"));

        // 后建的分组优先（WGestures 里后加的组盖住先加的组）
        options.TargetList.Add(new GestureTarget
        {
            Id = "group:dev2", Name = "开发工具（新）",
            Members = new List<string> { grouped },
            Triggers = new Dictionary<string, bool> { ["mouse:2"] = false },
        });
        map = new GestureTriggerMap(options);
        Check("WG2 继承：同属两个分组时后建的优先",
            !map.Allows(grouped, false, false, "mouse:2"));

        // 特殊目标
        options.TargetList.First(t => t.Kind == GestureTargetKind.Taskbar).Triggers["mouse:2"] = false;
        options.TargetList.First(t => t.Kind == GestureTargetKind.Desktop).Triggers["mouse:2"] = true;
        // 上面为了让"应用压过分组"成立，把 app 的 mouse:2 关掉了；这里先还原，
        // 否则下面比的是应用的设置，验不出任务栏这一层。
        options.TargetList.First(t => t.Id == app).Triggers.Remove("mouse:2");
        map = new GestureTriggerMap(options);
        Check("WG2 继承：任务栏的设置只对任务栏生效",
            !map.Allows(app, false, true, "mouse:2") && map.Allows(app, false, false, "mouse:2"));
        Check("WG2 继承：桌面的设置只对桌面生效",
            map.Allows("", true, false, "mouse:2"));

        // 整个目标关掉
        Check("WG2 继承：没设 NoGestures 时不拦",
            !map.IsBlocked(grouped, false, false));
        options.TargetList.First(t => t.Id == "group:dev2").NoGestures = true;
        Check("WG2 继承：分组设了 NoGestures 就拦住组内程序",
            new GestureTriggerMap(options).IsBlocked(grouped, false, false));

        // 并集判定：谁都不要的触发方式，连事件都不该吞
        var quiet = new GestureOptions
        {
            TargetList = new List<GestureTarget> { GestureTargetDefaults.Global() }
        };
        quiet.TargetList[0].Triggers["mouse:3"] = false;
        Check("WG2 继承：没人启用时 AnyEnables 为假",
            !new GestureTriggerMap(quiet).AnyEnables("mouse:3"));

        // ---- 分组作用域的手势：组内程序共用一套 ----
        var groupGesture = new MouseGesture { AppPath = "group:dev", Kind = "stroke" };
        var groupOptions = new GestureOptions
        {
            TargetList = new List<GestureTarget>
            {
                GestureTargetDefaults.Global(),
                new()
                {
                    Id = "group:dev", Name = "开发工具",
                    Members = new List<string> { grouped },
                },
            }
        };
        Check("WG2 分组手势：组内程序命中分组手势",
            GestureService.AppMatches(groupGesture, grouped, groupOptions));
        Check("WG2 分组手势：组外程序不命中",
            !GestureService.AppMatches(groupGesture, "C:/Apps/other.exe", groupOptions));
        groupOptions.TargetList[1].Members.Clear();
        Check("WG2 分组手势：空分组谁都不命中",
            !GestureService.AppMatches(groupGesture, grouped, groupOptions));
        Check("WG2 分组手势：没作用域的手势算全局",
            GestureService.AppMatches(new MouseGesture(), grouped, groupOptions));

        // ---- 轨迹开关：老配置的 ShowTrail=false 要能压住新开关 ----
        var legacy = new GestureOptions { ShowTrail = false };
        Check("WG2 外观：老配置关过轨迹，新开关跟着关",
            !legacy.ShowPath);
        legacy.ShowPath = true;
        legacy.ShowTrail = true;
        Check("WG2 外观：两个开关都打开才画轨迹",
            legacy.ShowPath);
    }
}
