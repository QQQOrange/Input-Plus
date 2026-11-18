using System;
using System.Collections.Generic;

using InputPlus.Core.Gestures;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Actions;

/// <summary>
/// 把「已经藏起来的兼容动作」换成界面上看得见的正式动作。
///
/// 为什么要有这一步：
///   动作目录里留着几条 hidden 的兼容动作（比如 openUrl），只为了老配置还能照原样执行。
///   可它们一旦被隐藏，下拉里就没有对应条目了 —— 用户一打开这个手势/按键，
///   下拉只能停在"第一项"，点一次保存就把动作和参数一起丢掉
///   （表现是"以前能用，现在按下去没反应"）。
///
///   以前的做法是把它以「旧功能，建议换掉」的名字塞进当前分类：看得见，但没得选。
///   现在的做法是**换掉**：参数原样搬到正式动作上，功能一点不少，
///   界面上也不会再出现任何"旧功能"字样。
///
/// 只换语义完全一致的（handler 都是同一个实现，参数原样带走）：
///   openUrl(arg)        →  webPage(arg)      都用默认浏览器打开这个网址；
///   esc / enter / tab / space / backspace / delete
///                       →  key:1B / 0D / 09 / 20 / 08 / 2E
///                          （基础分类里那六条隐藏重复项 → 「键盘按键」里的同名项）。
/// runProgram 不需要迁移 —— 它已经升级成「系统功能 → 运行程序 / 命令」正式动作，
/// id 没变，老绑定和手势直接就能用（并且现在会显示参数框）。
/// </summary>
public static class LegacyActionMigration
{
    /// <summary>
    /// 老动作 id → 正式动作 id。两边的 handler 是同一件事
    /// （openUrl/webPage 都走 ctx.OpenUrl，键码动作与 key:XX 都走 Send(vk)），
    /// 所以参数可以原样带走、行为一点不变。
    /// </summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 系统：打开网址 → 直达链接 / 直达网址
        ["openUrl"] = "webPage",

        // 基础里的六个键码动作，和「键盘按键」分类里的 key:XX 完全等价
        ["esc"] = "key:1B",
        ["enter"] = "key:0D",
        ["tab"] = "key:09",
        ["space"] = "key:20",
        ["backspace"] = "key:08",
        ["delete"] = "key:2E",
    };

    /// <summary>把老动作 id 换成正式动作 id；不在表里（或为空）的原样返回。</summary>
    public static string MapId(string? id)
        => string.IsNullOrEmpty(id) ? "" : Map.TryGetValue(id, out var to) ? to : id;

    /// <summary>这条动作 id 需不需要迁移。</summary>
    public static bool Needs(string? id)
        => !string.IsNullOrEmpty(id) && Map.ContainsKey(id);

    /// <summary>
    /// 扫一遍整份设置，把所有老动作换成正式动作。
    /// 返回改了多少处；0 表示没有老配置，调用方不用写盘。
    /// </summary>
    public static int Apply(AppSettings settings)
    {
        if (settings == null) return 0;
        int n = 0;

        // 手势
        foreach (var g in settings.Gestures)
            if (Needs(g.Action)) { g.Action = MapId(g.Action); n++; }

        // 按键绑定：全局层、层内子层、设备方案、原键替换
        n += Sweep(settings.Keys);
        foreach (var layer in settings.Layers)
        {
            n += Sweep(layer.Keys);
            foreach (var sub in layer.SubLayers) n += Sweep(sub.Keys);
        }
        foreach (var profile in settings.Profiles)
        {
            foreach (var layer in profile.Layers) n += Sweep(layer.Keys);
            n += Sweep(profile.Replacements);
        }

        // 宏里的「执行动作」步骤
        foreach (var macro in settings.Macros)
            foreach (var step in macro.Steps)
                if (step.Kind == "action" && Needs(step.ActionId))
                {
                    step.ActionId = MapId(step.ActionId);
                    n++;
                }

        /* 触发键和「双击某个键」也存的是动作 id，同样会碰到上面那个坑：
         * 短按设成 esc/tab 这类隐藏动作后，下拉里没有对应条目，列表看着像"没设置"，
         * 用户一改别的设置保存，短按动作可能被顶成第一项。 */
        if (Needs(settings.Global.TapCapsAction))
        {
            settings.Global.TapCapsAction = MapId(settings.Global.TapCapsAction);
            n++;
        }
        if (Needs(settings.Global.DoubleTapCapsAction))
        {
            settings.Global.DoubleTapCapsAction = MapId(settings.Global.DoubleTapCapsAction);
            n++;
        }
        foreach (var trigger in settings.Triggers)
        {
            if (Needs(trigger.TapAction)) { trigger.TapAction = MapId(trigger.TapAction); n++; }
            if (Needs(trigger.DoubleTapAction)) { trigger.DoubleTapAction = MapId(trigger.DoubleTapAction); n++; }
        }
        foreach (var gestureKey in settings.Global.KeyGestures)
            if (Needs(gestureKey.DoubleTapAction)) { gestureKey.DoubleTapAction = MapId(gestureKey.DoubleTapAction); n++; }

        if (n > 0)
        {
            // 绑定内容变了，缓存的按键表要跟着重建
            settings.Global.BindingsVersion++;
            CoreLog.Info($"[动作] {n} 处老功能已换成正式动作（参数原样保留），功能不变");
        }

        return n;
    }

    private static int Sweep(List<KeyBinding>? list)
    {
        if (list == null) return 0;
        int n = 0;
        foreach (var b in list)
            if (Needs(b.Action)) { b.Action = MapId(b.Action); n++; }
        return n;
    }
}
