using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Suggestions;

/// <summary>一条配置增量操作。</summary>
internal sealed class SuggestionOp
{
    /// <summary>insert / update / delete。</summary>
    public string Op { get; set; } = "insert";

    public string Key { get; set; } = "";

    public string? Value { get; set; }

    public DateTime Ts { get; set; } = DateTime.Now;
}

/// <summary>
/// 建议配置的增量落盘。
///
/// 为什么是 delta 而不是整份 JSON：建议配置是"一堆独立开关 + 两个可增删的集合"，
/// 整份重写会让**并发写**变成"后写覆盖先写"（设置页按"隐藏"、热键同时按"隐藏"时
/// 会丢一条）。增量只追加一条 op，谁后写都不会抹掉对方 —— 底线是重放结果一致。
///
/// 文件：&lt;DataDir&gt;\suggestions.delta.json，内容 {"version":1,"ops":[...]}；
/// op 数超过 <see cref="CompactThreshold"/> 时在写之前压成"每个 key 一条 insert"。
/// 默认值不在文件里，硬编码在 <see cref="SuggestionsOptions"/> 的属性初始值上。
/// </summary>
public sealed class SuggestionConfigStore
{
    public const int CompactThreshold = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public List<SuggestionOp> Ops { get; set; } = new();
    }

    private readonly object _gate = new();
    private readonly List<SuggestionOp> _ops = new();
    private readonly string _path;
    private bool _loaded;

    public SuggestionConfigStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataDir, "suggestions.delta.json");
    }

    public string StorePath => _path;

    internal IReadOnlyList<SuggestionOp> Ops
    {
        get { lock (_gate) { EnsureLoaded(); return _ops.ToList(); } }
    }

    /// <summary>把增量重放到 <paramref name="options"/> 上（就地改）。重放失败只记日志。</summary>
    public void Replay(SuggestionsOptions options)
    {
        if (options == null) return;
        List<SuggestionOp> ops;
        lock (_gate)
        {
            EnsureLoaded();
            ops = _ops.ToList();
        }

        foreach (SuggestionOp op in ops)
        {
            try { Apply(options, op); }
            catch (Exception ex) { CoreLog.Swallow(ex, "重放建议配置增量 " + op.Key); }
        }
    }

    /// <summary>写一条 insert/update（同 key 自动变成 update）。失败返回 false 并给原因。</summary>
    public bool Set(string key, string? value, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(key)) { error = "键为空"; return false; }
        lock (_gate)
        {
            EnsureLoaded();
            bool exists = _ops.Any(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
            _ops.Add(new SuggestionOp { Op = exists ? "update" : "insert", Key = key, Value = value });
        }
        return Save(out error);
    }

    /// <summary>写一条 delete。键不存在也算成功（幂等）。</summary>
    public bool Remove(string key, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(key)) { error = "键为空"; return false; }
        lock (_gate)
        {
            EnsureLoaded();
            _ops.Add(new SuggestionOp { Op = "delete", Key = key, Value = null });
        }
        return Save(out error);
    }

    /// <summary>测试用：清空。</summary>
    internal void ClearForTest()
    {
        lock (_gate)
        {
            _ops.Clear();
            _loaded = true;
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (Exception ex) { CoreLog.Swallow(ex, "清理建议配置增量"); }
        }
    }

    private bool Save(out string error)
    {
        error = "";
        try
        {
            List<SuggestionOp> snapshot;
            lock (_gate) { snapshot = Compacted(); }

            string dir = Path.GetDirectoryName(_path) ?? "";
            if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(new Document { Ops = snapshot }, JsonOptions);
            File.WriteAllText(_path, json, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            CoreLog.Warn("[建议] 配置落盘失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>超过阈值时压成"每个 key 最后一条"。</summary>
    private List<SuggestionOp> Compacted()
    {
        if (_ops.Count <= CompactThreshold) return _ops.ToList();

        var last = new Dictionary<string, SuggestionOp>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (SuggestionOp op in _ops)
        {
            if (!last.ContainsKey(op.Key)) order.Add(op.Key);
            last[op.Key] = op;
        }

        _ops.Clear();
        _ops.AddRange(order.Select(k => last[k]));
        return _ops.ToList();
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
            if (doc?.Ops == null) return;
            foreach (SuggestionOp op in doc.Ops)
                if (op != null && !string.IsNullOrWhiteSpace(op.Key))
                    _ops.Add(op);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[建议] 配置读取失败（按默认值继续）：" + ex.Message);
        }
    }

    /// <summary>把一个 op 应用到配置对象上。键的命名见 <see cref="SuggestionKeys"/>。</summary>
    private static void Apply(SuggestionsOptions options, SuggestionOp op)
    {
        bool delete = string.Equals(op.Op, "delete", StringComparison.OrdinalIgnoreCase);
        switch (SuggestionKeys.KindOf(op.Key, out string arg))
        {
            case SuggestionKeyKind.Show:
                options.Show = !delete && ParseBool(op.Value, true);
                break;
            case SuggestionKeyKind.Hotkey:
                options.HideFileHotkey = delete ? "" : (op.Value ?? "");
                break;
            case SuggestionKeyKind.MaxCandidates:
                if (!delete && int.TryParse(op.Value, out int max)) options.MaxCandidates = max;
                break;
            case SuggestionKeyKind.ShowCount:
                if (!delete && int.TryParse(op.Value, out int show)) options.ShowCount = show;
                break;
            case SuggestionKeyKind.Source:
                /* 来源开关用 DisabledSources 表达：delete 或者 true = 恢复启用；
                 * false = 显式关掉。不用 Sources 是因为空 Sources 的语义是"全开"，
                 * 关掉一个没法用空集合表达，写其余七个又会冻住以后新增的来源。 */
                if (delete || ParseBool(op.Value, true))
                {
                    options.DisabledSources?.RemoveAll(x => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase));
                    options.Sources?.RemoveAll(x => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    options.DisabledSources ??= new List<string>();
                    if (!options.DisabledSources.Any(x => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase)))
                        options.DisabledSources.Add(arg);
                }
                break;
            case SuggestionKeyKind.Extension:
                MutateList(options.Exclusions.FileExtensions, arg, delete);
                break;
            case SuggestionKeyKind.HiddenFile:
                /* 键是规范化后的小写路径（去重/查找用），值里存用户原始写法，
                 * 这样设置页的"已隐藏"列表不会把 D:\Work\Report.PDF 显示成全小写。 */
                MutateList(options.Exclusions.Files, OriginalPath(op, arg), delete);
                break;
            case SuggestionKeyKind.HiddenFolder:
                MutateList(options.Exclusions.Folders, OriginalPath(op, arg), delete);
                break;
            case SuggestionKeyKind.Filter:
                MutateList(options.FileTypeFilter, arg, delete);
                break;
        }
    }

    /// <summary>黑名单项优先用值里的原始路径；老格式（值 "1"）退回键里的规范化路径。</summary>
    private static string OriginalPath(SuggestionOp op, string arg)
        => string.IsNullOrWhiteSpace(op.Value) || op.Value == "1" ? arg : op.Value;

    private static bool ParseBool(string? value, bool fallback)
        => value == null ? fallback : bool.TryParse(value, out bool b) ? b : value == "1" || value == "on";

    private static void MutateList(List<string> list, string value, bool remove)
    {
        if (list == null || string.IsNullOrWhiteSpace(value)) return;
        if (remove)
        {
            list.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
            return;
        }
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
    }
}

internal enum SuggestionKeyKind
{
    Unknown = 0,
    Show,
    Hotkey,
    MaxCandidates,
    ShowCount,
    Source,
    Extension,
    HiddenFile,
    HiddenFolder,
    Filter,
}

/// <summary>建议配置的键名与解析。集中在一处，避免"写的时候一个拼法、读的时候另一个"。</summary>
internal static class SuggestionKeys
{
    public const string Show = "show";
    public const string Hotkey = "hide-hotkey";
    public const string MaxCandidates = "max-candidates";
    public const string ShowCount = "show-count";

    public const string SourcePrefix = "source:";
    public const string ExtensionPrefix = "ext:";
    public const string HiddenFilePrefix = "hide-file:";
    public const string HiddenFolderPrefix = "hide-folder:";
    public const string FilterPrefix = "filter:";

    public static string Source(string id) => SourcePrefix + id;

    public static string Extension(string ext) => ExtensionPrefix + ext.TrimStart('.').ToLowerInvariant();

    public static string HiddenFile(string path) => HiddenFilePrefix + RecItem.NormalizeKey(path);

    public static string HiddenFolder(string path) => HiddenFolderPrefix + RecItem.NormalizeKey(path);

    public static string Filter(string ext) => FilterPrefix + ext.TrimStart('.').ToLowerInvariant();

    public static SuggestionKeyKind KindOf(string key, out string arg)
    {
        arg = "";
        if (string.IsNullOrWhiteSpace(key)) return SuggestionKeyKind.Unknown;
        if (key.Equals(Show, StringComparison.OrdinalIgnoreCase)) return SuggestionKeyKind.Show;
        if (key.Equals(Hotkey, StringComparison.OrdinalIgnoreCase)) return SuggestionKeyKind.Hotkey;
        if (key.Equals(MaxCandidates, StringComparison.OrdinalIgnoreCase)) return SuggestionKeyKind.MaxCandidates;
        if (key.Equals(ShowCount, StringComparison.OrdinalIgnoreCase)) return SuggestionKeyKind.ShowCount;

        foreach (var (prefix, kind) in new[]
                 {
                     (SourcePrefix, SuggestionKeyKind.Source),
                     (ExtensionPrefix, SuggestionKeyKind.Extension),
                     (HiddenFilePrefix, SuggestionKeyKind.HiddenFile),
                     (HiddenFolderPrefix, SuggestionKeyKind.HiddenFolder),
                     (FilterPrefix, SuggestionKeyKind.Filter),
                 })
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            arg = key[prefix.Length..];
            return kind;
        }
        return SuggestionKeyKind.Unknown;
    }
}
