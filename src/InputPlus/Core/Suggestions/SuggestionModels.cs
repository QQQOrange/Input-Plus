using System;
using System.Collections.Generic;

namespace InputPlus.Core.Suggestions;

/// <summary>建议项的种类。应用（App）只能由"应用内历史/最近程序"这类来源产生。</summary>
public enum RecItemType
{
    File = 0,
    Folder = 1,
    App = 2,
}

/// <summary>
/// 来源的枚举类别。只在合并/排序/埋点时用，用来表达"这条建议从哪一类通道来"。
/// 具体是哪个来源（微信/飞书/桌面…）看 <see cref="RecItem.SourceId"/>。
/// </summary>
public enum SourceKind
{
    /// <summary>从一个目录里枚举出来的（下载、桌面、微信…）。</summary>
    Directory = 0,

    /// <summary>shell:recent 里的 .lnk 解析出来的。</summary>
    WindowsRecent = 1,

    /// <summary>文件对话框浮层记下来的选择历史。</summary>
    DialogHistory = 2,

    /// <summary>启动器自己的使用历史（应用内历史）。</summary>
    ListaryHistory = 3,

    /// <summary>兜底的"最近"（RecentTargets）。</summary>
    Recent = 4,
}

/// <summary>
/// 一条建议。不可变：合并阶段产出新的对象，避免 UI 线程读的时候后台线程还在改。
///
/// 去重的规范是 <see cref="Key"/>：路径小写、去掉末尾分隔符。同一路径被多个来源
/// 命中时保留来源权重最高的那条，其余来源记进 <see cref="Badges"/>。
/// </summary>
public sealed class RecItem
{
    public RecItemType ItemType { get; init; } = RecItemType.File;

    /// <summary>来源标识，例如 downloads / desktop / wechat / wecom / feishu / windows-recent / dialog-history / app-history。</summary>
    public string SourceId { get; init; } = "";

    public SourceKind SourceKind { get; init; } = SourceKind.Directory;

    /// <summary>来源权重，越大越优先。去重时按它决定保留哪一条。</summary>
    public int SourceWeight { get; init; }

    public string Path { get; init; } = "";

    public string Name { get; init; } = "";

    public DateTime Modified { get; init; }

    /// <summary>合并后的徽标文本（"最近"、"微信"、"下载"…）。按权重降序、去重。</summary>
    public IReadOnlyList<string> Badges { get; init; } = Array.Empty<string>();

    /// <summary>命中的全部来源 id（含被合并掉的那些），用于排错。</summary>
    public IReadOnlyList<string> MergedSources { get; init; } = Array.Empty<string>();

    public bool IsFolder => ItemType == RecItemType.Folder;

    /// <summary>去重键：大小写不敏感 + 去掉末尾分隔符。</summary>
    public string Key => NormalizeKey(Path);

    public static string NormalizeKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string p = path.Trim().Replace('/', '\\');
        if (p.Length > 3) p = p.TrimEnd('\\');
        return p.ToLowerInvariant();
    }

    /// <summary>名字为空时用路径末段补一个，保证 Presenter 不会显示空行。</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            try
            {
                string trimmed = Path.TrimEnd('\\');
                int slash = trimmed.LastIndexOf('\\');
                return slash >= 0 && slash + 1 < trimmed.Length ? trimmed[(slash + 1)..] : trimmed;
            }
            catch { return Path; }
        }
    }
}
