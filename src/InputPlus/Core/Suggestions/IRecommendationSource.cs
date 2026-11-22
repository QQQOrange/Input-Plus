using System;
using System.Collections.Generic;
using System.Diagnostics;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Suggestions;

/// <summary>
/// 采集时给来源的上下文。刻意只放"读"的东西：来源不允许改设置、不允许碰 UI。
/// </summary>
public sealed class SuggestionContext
{
    public SuggestionContext(AppSettings settings, RecRules rules, int maxItems)
    {
        Settings = settings;
        Rules = rules;
        MaxItems = maxItems;
    }

    public AppSettings Settings { get; }

    public RecRules Rules { get; }

    /// <summary>候选硬上限（默认 2000）。来源可以自己再压低。</summary>
    public int MaxItems { get; }
}

/// <summary>
/// 采集预算：统一计时 + 取消。来源在循环里查 <see cref="Expired"/>，超时就停手，
/// 已经产出的条目照样返回 —— 宁可少几条，也不要让唤出变慢。
/// </summary>
public sealed class SuggestionBudget
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public SuggestionBudget(int maxItems, int budgetMs)
    {
        MaxItems = Math.Max(1, maxItems);
        BudgetMs = Math.Max(50, budgetMs);
    }

    public int MaxItems { get; }

    public int BudgetMs { get; }

    public long ElapsedMs => _watch.ElapsedMilliseconds;

    public bool Expired => _watch.ElapsedMilliseconds > BudgetMs;
}

/// <summary>
/// 建议来源。一个来源 = 一个"从哪儿发现候选路径"的通道。
///
/// 契约（红线 4、红线 2）：
///   · <see cref="Probe"/> 只做目录存在性判断，必须毫秒级返回，在登记阶段被调用；
///   · <see cref="Collect"/> 只在后台线程跑，可以枚举文件，超预算要提前收手；
///   · 任何异常都由采集器兜住 —— 一个来源炸了只是它自己为空。
/// </summary>
public interface IRecommendationSource
{
    /// <summary>稳定标识，写进 <see cref="RecItem.SourceId"/> 与日志。</summary>
    string Id { get; }

    SourceKind Kind { get; }

    /// <summary>来源权重，越大越优先（同一路径去重时保留高权重的）。</summary>
    int Weight { get; }

    /// <summary>行内徽标文字，例如"微信"、"下载"。</summary>
    string Badge { get; }

    /// <summary>毫秒级探活：目录/文件在不在。不存在返回 false，采集器直接跳过本来源。</summary>
    bool Probe(SuggestionContext context);

    /// <summary>真正的枚举。只在后台线程调用。</summary>
    IReadOnlyList<RecItem> Collect(SuggestionContext context, SuggestionBudget budget);
}

/// <summary>
/// 采集规则：排除扩展名、排除目录、黑名单。
///
/// 三条规则的判定顺序是"黑名单 → 排除目录 → 排除扩展名"：黑名单是用户显式按的
/// "不建议"，优先级最高，连日志里都不该再出现。
/// </summary>
public sealed class RecRules
{
    public static readonly string[] DefaultExtensions = { "tmp", "bak", "log" };

    private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _folders = new();
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="extensions">要排除的扩展名（默认 tmp;bak;log）。</param>
    /// <param name="folders">要排除的目录（默认系统目录）。</param>
    /// <param name="hidden">黑名单路径（用户按"不建议"藏起来的）。</param>
    /// <param name="allowedExtensions">白名单：非空时只保留这些扩展名。</param>
    public RecRules(IEnumerable<string>? extensions = null,
        IEnumerable<string>? folders = null,
        IEnumerable<string>? hidden = null,
        IEnumerable<string>? allowedExtensions = null)
    {
        foreach (string ext in allowedExtensions ?? Array.Empty<string>())
        {
            string e = (ext ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (e.Length > 0) _allowed.Add(e);
        }

        foreach (string ext in extensions ?? DefaultExtensions)
        {
            string e = (ext ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (e.Length > 0) _extensions.Add(e);
        }

        foreach (string folder in folders ?? SystemFolders())
        {
            string f = (folder ?? "").Trim();
            if (f.Length == 0) continue;
            _folders.Add(f.TrimEnd('\\'));
        }

        foreach (string path in hidden ?? Array.Empty<string>())
        {
            string key = RecItem.NormalizeKey(path);
            if (key.Length > 0) _hidden.Add(key);
        }
    }

    /// <summary>要排除的扩展名（不带点，小写）。</summary>
    public IReadOnlyCollection<string> Extensions => _extensions;

    public IReadOnlyCollection<string> Folders => _folders;

    public IReadOnlyCollection<string> Hidden => _hidden;

    public static IEnumerable<string> SystemFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public bool IsHidden(string? path) => _hidden.Contains(RecItem.NormalizeKey(path));

    /// <summary>path 是否落在某个被排除的目录里（按目录边界判断，不做裸前缀匹配）。</summary>
    public bool IsInExcludedFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full = path.TrimEnd('\\');
        foreach (string folder in _folders)
        {
            if (folder.Length == 0) continue;
            if (full.Equals(folder, StringComparison.OrdinalIgnoreCase)) return true;
            if (full.Length > folder.Length + 1 &&
                full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                (full[folder.Length] == '\\' || full[folder.Length] == '/'))
                return true;
        }
        return false;
    }

    public bool IsExcludedExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext;
        try { ext = System.IO.Path.GetExtension(path).TrimStart('.'); }
        catch { return false; }
        return ext.Length > 0 && _extensions.Contains(ext);
    }

    /// <summary>最终判定。返回 true 表示这条不该进建议列表。</summary>
    public bool IsExcluded(string? path, bool isFolder, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path)) { reason = "空路径"; return true; }
        if (IsHidden(path)) { reason = "黑名单"; return true; }
        if (IsInExcludedFolder(path)) { reason = "系统目录"; return true; }
        if (!isFolder && IsExcludedExtension(path)) { reason = "排除扩展名"; return true; }
        if (!isFolder && !IsAllowedExtension(path)) { reason = "不在类型白名单里"; return true; }
        return false;
    }

    /// <summary>白名单为空表示不过滤；非空时文件扩展名必须在里面。</summary>
    public bool IsAllowedExtension(string? path)
    {
        if (_allowed.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext;
        try { ext = System.IO.Path.GetExtension(path).TrimStart('.'); }
        catch { return false; }
        return ext.Length > 0 && _allowed.Contains(ext);
    }
}
