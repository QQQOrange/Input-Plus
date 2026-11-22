namespace InputPlus.Core.Suggestions;

/// <summary>
/// Qbar 结果列表里"当前选中的建议项"这一份状态。
///
/// 为什么放静态字段而不是让热键去问窗口：
///   · 隐藏热键是全局热键，回调回来时我们不希望为了拿一行数据去翻可视树；
///   · 只有"建议行"才允许被隐藏（普通搜索结果右键菜单另有入口），
///     所以这里同时记住来源 id，热键端只用判断路径非空。
/// 写入点只有一个：QbarWindow 的选中项变化；读点是隐藏热键和右键菜单。
/// </summary>
public static class SuggestionsSelection
{
    /// <summary>当前选中的建议项路径；空 = 当前选中的不是建议行。</summary>
    public static string Path { get; private set; } = "";

    /// <summary>该建议项是文件还是文件夹（隐藏时要写进不同的黑名单）。</summary>
    public static bool IsFolder { get; private set; }

    /// <summary>来自哪个来源（日志用）。</summary>
    public static string SourceId { get; private set; } = "";

    public static bool HasValue => Path.Length > 0;

    public static void Set(string? path, bool isFolder, string sourceId)
    {
        Path = string.IsNullOrWhiteSpace(path) ? "" : path;
        IsFolder = Path.Length > 0 && isFolder;
        SourceId = Path.Length > 0 ? sourceId ?? "" : "";
    }

    public static void Clear() => Set(null, false, "");
}
