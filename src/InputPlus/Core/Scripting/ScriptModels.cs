using System;
using System.Collections.Generic;
using System.Linq;

namespace InputPlus.Core.Scripting;

/// <summary>
/// 一段 Lua 脚本。
///
/// 存在设置文件里（<see cref="Settings.AppSettings.Scripts"/>），动作目录里
/// 每个脚本注册成一条「Lua 脚本」分类的动作，绑到按键、手势、宏里都行。
/// 脚本运行在独立线程上，拿到的 <c>ip</c> 表就是软件自己的功能入口
/// （发键、跑动作、开关窗口、读剪贴板、弹提示……），见 <see cref="LuaRunner"/>。
/// </summary>
public sealed class LuaScript
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "新脚本";

    /// <summary>备注：写清楚这个脚本干什么，列表里显示成第二行。</summary>
    public string Comment { get; set; } = "";

    /// <summary>Lua 源码（Lua 5.2，MoonSharp）。</summary>
    public string Code { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>最近一次运行的结果（界面显示用，不参与序列化判断）。</summary>
    public string LastResult { get; set; } = "";

    public LuaScript Clone() => new()
    {
        Id = Id,
        Name = Name,
        Comment = Comment,
        Code = Code,
        Enabled = Enabled,
        LastResult = LastResult
    };

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? "未命名脚本" : Name.Trim();
}

/// <summary>
/// 一个手势菜单（v1.5.0）。
///
/// 手势 / 按键触发它时，鼠标旁边弹出这一列条目；点哪条就执行哪条的动作。
/// 条目就是普通的（动作 ID + 参数），所以"菜单里放什么"和"按键能做什么"是同一套能力。
/// </summary>
public sealed class GestureMenuDef
{
    public const int MaxItems = 12;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "手势菜单";

    /// <summary>菜单标题右边的小字（可空）。</summary>
    public string Hint { get; set; } = "";

    public List<GestureMenuItem> Items { get; set; } = new();

    public GestureMenuDef Clone() => new()
    {
        Id = Id,
        Name = Name,
        Hint = Hint,
        Items = Items.Select(i => i.Clone()).ToList()
    };

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? "未命名菜单" : Name.Trim();
}

/// <summary>手势菜单里的一条。</summary>
public sealed class GestureMenuItem
{
    public string Title { get; set; } = "";

    /// <summary>条目左侧的图标字形（Segoe MDL2 Assets 的字符，可空）。</summary>
    public string Glyph { get; set; } = "";

    public string Action { get; set; } = "";

    public string Arg { get; set; } = "";

    public GestureMenuItem Clone() => new()
    {
        Title = Title,
        Glyph = Glyph,
        Action = Action,
        Arg = Arg
    };
}
