using System.Windows.Media;
using InputPlus.Core.Icons;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Qbar;

public enum ChipKind
{
    /// <summary>搜索：bd 关键词 → 百度搜索。</summary>
    Search,
    /// <summary>启动程序：calc → 计算器。</summary>
    Run,
    /// <summary>打开网址：gh → GitHub。</summary>
    Web,
    /// <summary>
    /// 命令：ol → 欧路词典查词。
    /// 和 Run 的区别：命令带参数模板（{query} 等），激活后输入的文字是**参数**，
    /// 不是程序名本身；执行走 QbarCommandStore，所以 runas / silent / workdir 一并生效。
    /// </summary>
    Command,
}

/// <summary>
/// Qbar 输入框里的“标签”（Chip）。
/// 用户输入 "bd" 再按空格，bd 就从纯文本变成一枚标签，输入框里只剩下要搜索的内容。
/// 有图标就显示图标，没有图标就显示名字。
/// </summary>
public sealed class QbarChip
{
    public ChipKind Kind { get; init; }
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Value { get; init; } = "";
    public string IconPath { get; init; } = "";
    public bool RunAsAdmin { get; init; }
    public string Arguments { get; init; } = "";

    /// <summary>命令标签对应的命令 Id（Kind=Command 时有效）。</summary>
    public string CommandId { get; init; } = "";

    /// <summary>解析出来的图标；为 null 时界面显示文字标签。</summary>
    public ImageSource? Icon { get; init; }

    public bool HasIcon => Icon != null;

    /// <summary>没有图标时用的品牌色与首字。</summary>
    public Color Accent { get; init; } = Colors.SteelBlue;
    public string Initial { get; init; } = "";

    /// <summary>标签上的文字（没有图标时显示）。</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Key : DisplayName;

    /// <summary>
    /// 条目的**备注**（没有备注就是空串，**不退回关键字**）。
    ///
    /// 与 <see cref="Label"/> 唯一的区别就是那条兜底：胶囊必须有个名字才点得动，
    /// 所以 Label 会拿关键字兜；而 Qbar 的结果行按用户要求"没备注就只显示图标"，
    /// 那里一律读这个值 —— 关键字在结果行里任何情况下都不出现。
    /// </summary>
    public string Note { get; init; } = "+";

    public static QbarChip FromEntry(ChipKind kind, QEntry entry) => new()
    {
        Kind = kind,
        Key = entry.Key,
        DisplayName = entry.DisplayName,
        Note = (entry.Comment ?? "").Trim(),
        Value = entry.Value,
        IconPath = entry.IconPath,
        RunAsAdmin = entry.RunAsAdmin,
        Arguments = entry.Arguments,
        Icon = IconCache.ForEntry(entry),
        Accent = IconCache.ColorFor(entry.DisplayName),
        Initial = IconCache.Initial(entry.DisplayName),
    };

    /// <summary>
    /// 自定义命令 → 标签。标题为空就退回关键字，否则用户看到的是一个空胶囊。
    /// 图标先按程序路径去取（有图标才知道自己按的是哪个程序），取不到再用首字。
    /// </summary>
    public static QbarChip FromCommand(QbarCommand cmd, string keyword) => new()
    {
        Kind = ChipKind.Command,
        Key = keyword,
        DisplayName = string.IsNullOrWhiteSpace(cmd.Title) ? keyword : cmd.Title,
        CommandId = cmd.Id,
        Value = (cmd.Path ?? "").Trim(),
        Arguments = cmd.Parameter ?? "",
        RunAsAdmin = cmd.RunAsAdmin,
        Icon = IconCache.FromExecutable(cmd.Path),
        Accent = IconCache.ColorFor(string.IsNullOrWhiteSpace(cmd.Title) ? keyword : cmd.Title),
        Initial = IconCache.Initial(string.IsNullOrWhiteSpace(cmd.Title) ? keyword : cmd.Title),
    };

    /// <summary>把标签拼回“关键字 + 内容”的形式，用于执行。</summary>
    public string Compose(string tail) => string.IsNullOrEmpty(tail) ? Key : $"{Key} {tail}";
}
