using System;
using System.Collections.Generic;
using System.Linq;

namespace InputPlus.Core.Settings;

/// <summary>
/// 建议的排除项。
///
/// 语义（与设置页一一对应）：
///   · Files       —— 用户按"不建议此文件"藏起来的文件（黑名单，可还原）
///   · Folders     —— 同上，文件夹
///   · FileExtensions —— 排除的扩展名，默认 tmp;bak;log（可配置）
///
/// 系统目录（Windows / Program Files / ProgramData / AppData）不在这里配，
/// 那三条是**代码里的硬规则**（<c>RecRules.SystemFolders()</c>）：
/// 让用户能往建议里加系统目录没有意义，反而会拖慢采集。
/// </summary>
public sealed class SuggestionsExclusions
{
    public List<string> Files { get; set; } = new();

    public List<string> Folders { get; set; } = new();

    public List<string> FileExtensions { get; set; } = new() { "tmp", "bak", "log" };
}

/// <summary>
/// 搜索前建议（空态建议）的配置。
///
/// 默认值全部硬编码在属性初始值上：老配置文件里没有这一段时，反序列化后拿到的就是
/// 下面这份默认 —— 这也是"没有建议配置时行为与改造前一致"的实现基础
/// （Show=true 但来源全空时不会产生任何建议项，列表与改造前完全一样）。
/// </summary>
public sealed class SuggestionsOptions
{
    /// <summary>总开关。关掉后启动器空态与改造前完全一致（红线 8）。</summary>
    public bool Show { get; set; } = true;

    /// <summary>启用的来源 id；空列表 = 全部启用（新加来源不需要用户重新配置）。</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>
    /// 用户在设置页**显式关掉**的来源 id。
    ///
    /// 和 <see cref="Sources"/> 分开存的原因：Sources 空表示"全开"，所以"关掉一个"
    /// 没法用空集合表达；写成 <c>Sources = 其余七个</c> 又会让以后新增的第九个来源
    /// 在旧配置里默认关闭。单独一份"关掉了谁"的黑名单，两个问题一起解决。
    /// </summary>
    public List<string> DisabledSources { get; set; } = new();

    /// <summary>只保留这些扩展名（不带点）；空 = 不过滤。</summary>
    public List<string> FileTypeFilter { get; set; } = new();

    /// <summary>
    /// 来源展示顺序（id 列表）。空 = 按修改时间倒序（1.3.5 及以前的行为）；
    /// 用户在设置页点过"上移/下移"之后写满八个 id，Qbar 空态就按这个顺序分组展示。
    /// 未知 id 忽略，缺失的来源自动补到末尾 —— 以后新增第九个来源不会从列表里消失。
    /// </summary>
    public List<string> SourceOrder { get; set; } = new();

    /// <summary>出厂顺序，与 <c>SuggestionsService.CreateSources()</c> 一致。</summary>
    public static readonly IReadOnlyList<string> DefaultSourceOrder = new[]
    {
        "downloads", "desktop", "wechat", "wecom", "feishu",
        "windows-recent", "dialog-history", "app-history",
    };

    /// <summary>规范化后的顺序：只留已知 id，缺失的按出厂顺序补到末尾。</summary>
    public IReadOnlyList<string> ResolveSourceOrder()
    {
        var result = new List<string>(DefaultSourceOrder.Count);
        if (SourceOrder != null)
        {
            foreach (string id in SourceOrder)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!DefaultSourceOrder.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                if (result.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                result.Add(id);
            }
        }
        foreach (string id in DefaultSourceOrder)
            if (!result.Contains(id, StringComparer.OrdinalIgnoreCase)) result.Add(id);
        return result;
    }

    public SuggestionsExclusions Exclusions { get; set; } = new();

    /// <summary>"不建议此文件/文件夹"的全局热键。空串 = 不注册。</summary>
    public string HideFileHotkey { get; set; } = "Ctrl+Alt+H";

    /// <summary>候选硬上限（合并前的截断值）。</summary>
    public int MaxCandidates { get; set; } = 2000;

    /// <summary>空态最多展示多少条建议。</summary>
    public int ShowCount { get; set; } = 20;

    /// <summary>一次采集的总预算（毫秒）。超预算的来源提前收手，已采集的照常展示。</summary>
    public int CollectBudgetMs { get; set; } = 1200;

    public IEnumerable<string> EnabledSources => Sources;

    public bool IsSourceEnabled(string id)
    {
        if (DisabledSources != null)
            foreach (string s in DisabledSources)
                if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return false;
        if (Sources == null || Sources.Count == 0) return true;
        foreach (string s in Sources)
            if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
