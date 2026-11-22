namespace InputPlus.Core.FileDialog;

/// <summary>浮层的搜索范围：Tab 在「全部 → 文件夹 → 文件」之间循环。</summary>
public enum DialogScope
{
    All = 0,
    Files = 1,
    Folders = 2,
}

/// <summary>
/// 浮层里的一次查询：关键词 + 类型范围（Tab 切换）。
///
/// 单独建这个类型是为了让「输入框里是什么」和「拿什么去问搜索内核」分开：
/// 输入法组合期间的半成品、Tab 切出来的范围，都不该直接改写用户看得见的文本。
/// </summary>
public readonly record struct DialogSearchQuery(string Keyword, DialogScope Scope)
{
    public static DialogSearchQuery Create(string? raw, DialogScope scope)
        => new((raw ?? "").Trim(), scope);

    public bool IsEmpty => Keyword.Length == 0;

    /// <summary>Tab 循环：全部 → 文件夹 → 文件 → 全部。</summary>
    public static DialogScope NextScope(DialogScope scope) => scope switch
    {
        DialogScope.All => DialogScope.Folders,
        DialogScope.Folders => DialogScope.Files,
        _ => DialogScope.All,
    };

    /// <summary>范围标签（按钮上的字）。</summary>
    public static string ScopeLabel(DialogScope scope) => scope switch
    {
        DialogScope.Files => "只搜文件",
        DialogScope.Folders => "只搜文件夹",
        _ => "文件和文件夹",
    };

    /// <summary>
    /// 名字里第一个匹配片段（给行内高亮用）。找不到返回 null。
    /// 只做大小写不敏感的 IndexOf，不重复搜索引擎的打分逻辑。
    /// </summary>
    public static string? Highlight(string? name, string? keyword)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(keyword)) return null;
        int at = name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : name.Substring(at, keyword.Length);
    }
}
