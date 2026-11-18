using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace InputPlus.Core.Gestures;

/// <summary>
/// WGestures 2 的颜色串：<c>#RRGGBB</c> 或 <c>#RRGGBBAA</c>。
/// 注意是 **RGBA**，不是 WPF 的 <c>#AARRGGBB</c> ——
/// prefs.json 里 <c>PathColorRecognized = #FF5900FF</c> 是"不透明的橙红"，
/// 按 ARGB 读会变成紫蓝色，所以这里单独解析。
/// </summary>
public static class Wg2Color
{
    /// <summary>默认未识别轨迹色（对应 prefs.json 的 PathColorNormal）。</summary>
    public const string PathColorNormal = "#00000030";

    /// <summary>默认已识别轨迹色（prefs.json 的 PathColorRecognized）。</summary>
    public const string PathColorRecognized = "#FF5900FF";

    /// <summary>默认未识别标签色（prefs.json 的 LabelColorNormal）。</summary>
    public const string LabelColorNormal = "#00000030";

    /// <summary>默认已执行标签色（prefs.json 的 LabelColorExecuted）。</summary>
    public const string LabelColorExecuted = "#FF5900FF";

    public static Color Parse(string? hex, Color fallback)
    {
        string s = (hex ?? "").Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s[1..];
        if (s.Length is not (6 or 8)) return fallback;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            return fallback;

        if (s.Length == 6)
            return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);

        /* RGBA：前六位是颜色，最后两位是不透明度。 */
        return Color.FromArgb((byte)(v & 0xFF), (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8));
    }

    /// <summary>写回成 <c>#RRGGBBAA</c>（和 prefs.json 一样的口径）。</summary>
    public static string Format(Color c) =>
        $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

    /// <summary>取一个"认得出来"的颜色串；写错的一律退回默认值。</summary>
    public static string Normalize(string? hex, string fallback)
    {
        string s = (hex ?? "").Trim();
        if (!s.StartsWith("#", StringComparison.Ordinal)) s = "#" + s;
        return s.Length is 7 or 9 ? s.ToUpperInvariant() : fallback;
    }
}

/// <summary>
/// WGestures 2 的「允许的触发方式」全集：61 条。
///
/// 来自 <c>run\Resources\gestures.json</c> 的 <c>General.Triggers</c>：
/// 5 条裸鼠标键 + 8 个边角位置 × 7 种（滑动 / 滑动+滚轮 / 滑动+5 颗鼠标键）。
/// 位置用的是 <c>RectEdge</c>/<c>RectCorner</c> 位掩码：
/// Top=1、Right=2、Bottom=4、Left=8，四角是相邻两位的组合。
/// </summary>
public static class Wg2Triggers
{
    public const int EdgeTop = 1;
    public const int EdgeRight = 2;
    public const int EdgeBottom = 4;
    public const int EdgeLeft = 8;

    /// <summary>gestures.json 里的出现顺序：上、右、下、左边缘，再来四角。</summary>
    public static readonly int[] Positions = { 1, 2, 4, 8, 3, 6, 9, 12 };

    /// <summary>WGestures 的鼠标键编号（1 左 / 2 右 / 0 中 / 3 侧键1 / 4 侧键2）。</summary>
    public static readonly int[] MouseCodes = { 1, 2, 0, 3, 4 };

    /// <summary>全部 61 条，顺序和 WGestures 的默认清单一致。</summary>
    public static IReadOnlyList<string> All { get; } = BuildAll();

    private static List<string> BuildAll()
    {
        var list = new List<string>(61);
        foreach (int code in MouseCodes) list.Add("mouse:" + code);
        foreach (int pos in Positions)
        {
            list.Add("edge:" + pos);
            list.Add("edge:" + pos + ":scroll");
            foreach (int code in MouseCodes) list.Add("edge:" + pos + ":mouse:" + code);
        }
        return list;
    }

    /// <summary>Keyboard enhance 的虚拟键码 → WGestures 的鼠标键编号串。</summary>
    public static string MouseKeyOfVk(int vk) => vk switch
    {
        0x01 => "mouse:1",
        0x02 => "mouse:2",
        0x04 => "mouse:0",
        0x05 => "mouse:3",
        0x06 => "mouse:4",
        _ => ""
    };

    /// <summary>Keyboard enhance 的角落编号（1 左上 / 2 右上 / 3 左下 / 4 右下）→ 位掩码。</summary>
    public static int MaskOfCorner(int corner) => corner switch
    {
        1 => 9,    // LeftTop   = Top | Left
        2 => 3,    // RightTop  = Top | Right
        3 => 12,   // LeftBottom= Bottom | Left
        4 => 6,    // RightBottom = Bottom | Right
        _ => 0
    };

    /// <summary>位掩码 → 中文位置名。</summary>
    public static string PositionName(int mask) => mask switch
    {
        EdgeTop => Loc.T("屏幕上边缘"),
        EdgeRight => Loc.T("屏幕右边缘"),
        EdgeBottom => Loc.T("屏幕底边缘"),
        EdgeLeft => Loc.T("屏幕左边缘"),
        9 => Loc.T("屏幕左上角"),
        3 => Loc.T("屏幕右上角"),
        6 => Loc.T("屏幕右下角"),
        12 => Loc.T("屏幕左下角"),
        _ => ""
    };

    /// <summary>鼠标键编号 → 中文名（和 WGestures 的 tr.json 一致）。</summary>
    public static string MouseName(int code) => code switch
    {
        1 => Loc.T("鼠标左键"),
        2 => Loc.T("鼠标右键"),
        0 => Loc.T("鼠标中键"),
        3 => Loc.T("鼠标侧键1"),
        4 => Loc.T("鼠标侧键2"),
        _ => ""
    };

    /// <summary>一条触发方式在界面上显示的名字。</summary>
    public static string Label(string? key)
    {
        string raw = key ?? "";
        string[] parts = raw.Split(':');
        if (parts.Length == 2 && parts[0] == "mouse")
        {
            string? mouse = int.TryParse(parts[1], out int code) ? MouseName(code) : null;
            return mouse is { Length: > 0 } ? mouse : raw;
        }

        if (parts.Length >= 2 && parts[0] == "edge" && int.TryParse(parts[1], out int mask))
        {
            string where = PositionName(mask);
            string what = parts.Length switch
            {
                2 => Loc.T("鼠标滑动"),
                3 when parts[2] == "scroll" => Loc.T("滚轮"),
                4 when parts[2] == "mouse" && int.TryParse(parts[3], out int c) => MouseName(c),
                _ => ""
            };
            if (what.Length == 0) return raw;
            return where.Length == 0 ? what : where + " · " + what;
        }

        return raw;
    }
}

/// <summary>WGestures 2 的四种作用目标 + 全局。</summary>
public enum GestureTargetKind
{
    /// <summary>全局：所有程序里都生效，也是其它目标的默认值来源。</summary>
    Global,

    /// <summary>分组：一组程序共用一套触发方式。</summary>
    Group,

    /// <summary>应用程序：按 exe 全路径匹配。</summary>
    App,

    /// <summary>桌面。</summary>
    Desktop,

    /// <summary>任务栏。</summary>
    Taskbar
}

/// <summary>
/// 一个作用目标。字段和 WGestures 的 Target 基类一一对应：
/// Id / Name / Triggers（触发方式三态）/ 成员（分组）或路径（应用）。
/// </summary>
public sealed class GestureTarget
{
    /// <summary>稳定标识：<c>""</c>=全局、<c>group:xxx</c>、exe 全路径、<c>special:desktop</c>、<c>special:taskbar</c>。</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string AppPath { get; set; } = "";

    /// <summary>分组包含的程序（exe 全路径）。</summary>
    public List<string> Members { get; set; } = new();

    /// <summary>触发方式覆盖：键是 <see cref="Wg2Triggers"/> 的条目，值 = 启用/禁用。缺省 = 跟随上一层。</summary>
    public Dictionary<string, bool> Triggers { get; set; } = new();

    /// <summary>这个目标是否生效（对应 WGestures 的“禁用”）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>在这个目标里完全不触发手势（含边缘手势）。</summary>
    public bool NoGestures { get; set; }

    public GestureTargetKind Kind => KindOf(Id);

    public static GestureTargetKind KindOf(string? id)
    {
        string s = id ?? "";
        if (s.Length == 0) return GestureTargetKind.Global;
        if (s.StartsWith("group:", StringComparison.OrdinalIgnoreCase)) return GestureTargetKind.Group;
        if (string.Equals(s, DesktopId, StringComparison.OrdinalIgnoreCase)) return GestureTargetKind.Desktop;
        if (string.Equals(s, TaskbarId, StringComparison.OrdinalIgnoreCase)) return GestureTargetKind.Taskbar;
        return GestureTargetKind.App;
    }

    public const string DesktopId = "special:desktop";
    public const string TaskbarId = "special:taskbar";

    /// <summary>取这一层对某条触发方式的显式设置；没设置过返回 null（= 跟随上一层）。</summary>
    public bool? TriggerOf(string key)
    {
        if (Triggers == null) return null;
        return Triggers.TryGetValue(key ?? "", out bool on) ? on : null;
    }

    public void SetTrigger(string key, bool? value)
    {
        Triggers ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        if (key == null) return;
        if (value == null) Triggers.Remove(key);
        else Triggers[key] = value.Value;
    }

    public GestureTarget Clone() => new()
    {
        Id = Id,
        Name = Name,
        AppPath = AppPath,
        Members = new List<string>(Members ?? new List<string>()),
        Triggers = Triggers == null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(Triggers, StringComparer.Ordinal),
        Enabled = Enabled,
        NoGestures = NoGestures
    };
}

/// <summary>
/// 触发方式的解析器：按 WGestures 的顺序 Special → App → 所属分组 → 全局，
/// 第一层有显式设置的就按它，都没有就用全局的（再没有就是启用）。
///
/// 另外保留 WGestures 的“并集”判定（<c>A/Q.cs:293-333</c>）：
/// 一条触发方式如果哪儿都没有启用，整条就不算手势 —— 连吞事件都不吞。
/// </summary>
public sealed class GestureTriggerMap
{
    private readonly List<GestureTarget> _targets;

    public GestureTriggerMap(GestureOptions options)
        => _targets = options?.TargetList ?? new List<GestureTarget>();

    public GestureTarget? Find(string? id) =>
        _targets.FirstOrDefault(t => string.Equals(t.Id, id ?? "", StringComparison.OrdinalIgnoreCase));

    public GestureTarget Global =>
        Find("") ?? new GestureTarget { Id = "", Name = Loc.T("全局") };

    /// <summary>这条触发方式是否还有任意一个目标启用它。</summary>
    public bool AnyEnables(string key)
    {
        foreach (var t in _targets)
            if (t.Enabled && t.TriggerOf(key) == true) return true;
        return false;
    }

    /// <summary>
    /// 当前这一笔（程序 / 桌面 / 任务栏）下，某条触发方式是否允许。
    /// </summary>
    public bool Allows(string? app, bool desktop, bool taskbar, string key)
    {
        if (string.IsNullOrEmpty(key)) return true;

        // ① 特殊目标
        if (taskbar)
        {
            bool? v = Find(GestureTarget.TaskbarId)?.TriggerOf(key);
            if (v != null) return v.Value;
        }
        if (desktop)
        {
            bool? v = Find(GestureTarget.DesktopId)?.TriggerOf(key);
            if (v != null) return v.Value;
        }

        // ② 应用程序本身
        if (!string.IsNullOrWhiteSpace(app))
        {
            foreach (var t in _targets)
            {
                if (t.Kind != GestureTargetKind.App || !t.Enabled) continue;
                if (!SameApp(t.AppPath.Length > 0 ? t.AppPath : t.Id, app)) continue;
                bool? v = t.TriggerOf(key);
                if (v != null) return v.Value;
            }

            // ③ 程序所属的分组（后建的优先，和 WGestures 一致）
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var g = _targets[i];
                if (g.Kind != GestureTargetKind.Group || !g.Enabled) continue;
                if (!(g.Members ?? new List<string>()).Any(m => SameApp(m, app))) continue;
                bool? v = g.TriggerOf(key);
                if (v != null) return v.Value;
            }
        }

        // ④ 全局
        return Global.TriggerOf(key) ?? true;
    }

    /// <summary>这个程序（含所在分组、特殊目标）是不是被整个关掉了。</summary>
    public bool IsBlocked(string? app, bool desktop, bool taskbar)
    {
        foreach (var t in _targets)
        {
            if (!t.NoGestures) continue;
            if (t.Kind == GestureTargetKind.Taskbar && taskbar) return true;
            if (t.Kind == GestureTargetKind.Desktop && desktop) return true;
            if (t.Kind == GestureTargetKind.App && SameApp(t.AppPath.Length > 0 ? t.AppPath : t.Id, app)) return true;
            if (t.Kind == GestureTargetKind.Group &&
                (t.Members ?? new List<string>()).Any(m => SameApp(m, app))) return true;
        }
        return false;
    }

    internal static bool SameApp(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return string.Equals(
                System.IO.Path.GetFileNameWithoutExtension(a),
                System.IO.Path.GetFileNameWithoutExtension(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>默认的作用目标清单：全局 + 桌面 + 任务栏，和 WGestures 出厂状态一致。</summary>
public static class GestureTargetDefaults
{
    /// <summary>全局目标：61 条触发方式全部启用。</summary>
    public static GestureTarget Global()
    {
        var t = new GestureTarget { Id = "", Name = Loc.T("全局") };
        foreach (string key in Wg2Triggers.All) t.Triggers[key] = true;
        return t;
    }

    public static GestureTarget Desktop() => new()
    {
        Id = GestureTarget.DesktopId,
        Name = Loc.T("桌面")
    };

    public static GestureTarget Taskbar() => new()
    {
        Id = GestureTarget.TaskbarId,
        Name = Loc.T("任务栏")
    };

    public static List<GestureTarget> All() => new() { Global(), Desktop(), Taskbar() };

    /// <summary>补齐缺失的全局/桌面/任务栏，并保证全局那份 61 条齐全（老配置迁移用）。</summary>
    public static void Ensure(List<GestureTarget>? list)
    {
        if (list == null) return;
        if (list.All(t => t.Kind != GestureTargetKind.Global)) list.Insert(0, Global());
        if (list.All(t => t.Kind != GestureTargetKind.Desktop)) list.Add(Desktop());
        if (list.All(t => t.Kind != GestureTargetKind.Taskbar)) list.Add(Taskbar());

        var global = list.First(t => t.Kind == GestureTargetKind.Global);
        global.Triggers ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string key in Wg2Triggers.All)
            if (!global.Triggers.ContainsKey(key)) global.Triggers[key] = true;
    }
}
