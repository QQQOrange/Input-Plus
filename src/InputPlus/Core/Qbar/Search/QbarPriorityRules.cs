using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace InputPlus.Core.Qbar.Search;

/// <summary>优先级档位。DoNotIndex 是彻底消失，Ignored 是"消失但能解释怎么没的"。</summary>
internal enum QbarPriorityLevel
{
    DoNotIndex = 0,
    Ignored = 1,
    Low = 2,
    Normal = 3,
    High = 4,
}

/// <summary>一条规则的求解结果，带上"是哪条规则干的"，好让 UI 说人话。</summary>
internal readonly record struct QbarPriorityDecision(QbarPriorityLevel Level, string Reason, string Scope);

/// <summary>
/// 优先级规则求解器。Everything 没有这个概念，原生索引也没有 —— 所以这一层
/// 完全在候选结果集上跑：不写回索引，不做持久化改动，改规则立刻生效。
///
/// 求值规则（必须可复现）：
///   1. 作用域匹配 = 目录前缀（按路径段，C:\foo 不匹配 C:\foobar）；
///   2. 同名作用域下，后出现的规则覆盖前面的（用户规则排在默认规则包之后）；
///   3. 作用域更长的规则优先（深层目录的规则压住整个盘的规则）。
/// </summary>
internal sealed class QbarPriorityResolver
{
    private readonly List<Entry> _entries;
    private readonly string _identity;

    private QbarPriorityResolver(List<Entry> entries, string identity)
    {
        _entries = entries;
        _identity = identity;
    }

    /// <summary>规则集身份：同一份规则集求值必须完全一致，出问题能凭它复现。</summary>
    public string Identity => _identity;

    public int Count => _entries.Count;

    public static QbarPriorityResolver Empty { get; } =
        new QbarPriorityResolver(new List<Entry>(), "empty");

    public static QbarPriorityResolver Create(
        bool enabled,
        bool builtinEnabled,
        IEnumerable<QbarPriorityRule>? userRules,
        IEnumerable<string>? disabledBuiltins = null)
    {
        if (!enabled) return Empty;

        var entries = new List<Entry>(32);
        var identity = new System.Text.StringBuilder("rules:");

        if (builtinEnabled)
        {
            var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (disabledBuiltins != null)
            {
                foreach (string key in disabledBuiltins)
                    if (!string.IsNullOrWhiteSpace(key)) off.Add(key.Trim());
            }

            foreach (Entry e in Builtins())
            {
                if (off.Contains(e.Key)) continue;
                entries.Add(e);
            }
            identity.Append("builtin+v2;");
        }

        if (userRules != null)
        {
            foreach (QbarPriorityRule rule in userRules)
            {
                if (rule == null || !rule.Enabled) continue;
                string? folder = NormalizeFolder(rule.FolderPath);
                string pattern = (rule.NameRegex ?? "").Trim();
                if (folder == null && pattern.Length == 0) continue;

                Regex? regex = Compile(pattern);
                if (pattern.Length > 0 && regex == null) continue;   // 非法正则：保存时已经拒过一次，这里就当它不存在

                QbarPriorityLevel level = ParseLevel(rule.Level);
                string label = string.IsNullOrWhiteSpace(rule.Note) ? (folder ?? pattern) : rule.Note!;
                entries.Add(new Entry("user:" + rule.Id, folder, pattern, regex, level, label, entries.Count));
                identity.Append(rule.Id).Append(':').Append(rule.Level).Append(';');
            }
        }

        return entries.Count == 0
            ? Empty
            : new QbarPriorityResolver(entries, identity.ToString());
    }

    public static QbarPriorityLevel ParseLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return QbarPriorityLevel.Normal;
        switch (level.Trim().ToLowerInvariant())
        {
            case "high": return QbarPriorityLevel.High;
            case "low": return QbarPriorityLevel.Low;
            case "ignored": return QbarPriorityLevel.Ignored;
            case "dontindex":
            case "do-not-index": return QbarPriorityLevel.DoNotIndex;
            default: return QbarPriorityLevel.Normal;
        }
    }

    /// <summary>求一条结果的档位。没有规则命中一律 Normal。</summary>
    public QbarPriorityDecision Resolve(string fullPath, bool isDir)
    {
        if (_entries.Count == 0) return new QbarPriorityDecision(QbarPriorityLevel.Normal, "", "");

        string name = Path.GetFileName(fullPath);
        string? dir = isDir ? fullPath : Path.GetDirectoryName(fullPath);
        /* 目录规则的匹配对象：目录命中看它自己算不算在规则目录下，文件命中看它所在的目录。
           这里保持改造前的取法（目录命中取父目录），只是算一次给所有规则复用。 */
        string? scopeDir = Path.GetDirectoryName(fullPath);

        QbarPriorityDecision best = new QbarPriorityDecision(QbarPriorityLevel.Normal, "", "");
        int bestScope = -1;
        int bestIndex = -1;

        for (int i = 0; i < _entries.Count; i++)
        {
            Entry e = _entries[i];
            int scope = e.MatchScope(scopeDir, dir, name);
            if (scope < 0) continue;

            if (scope > bestScope || (scope == bestScope && e.Index > bestIndex))
            {
                best = new QbarPriorityDecision(e.Level, e.Label, e.Folder ?? e.Pattern);
                bestScope = scope;
                bestIndex = e.Index;
            }
        }

        return best;
    }

    /// <summary>
    /// 默认规则包。内容照 Listary 的默认优先级表抄，四档各自有明确职责：
    ///   正常 —— 系统里"其实能找、但埋在低权目录里"的几个位置（浏览器缓存、
    ///           开始菜单）。它们的路径比 %USERPROFILE%\AppData 更长，按
    ///           "最长作用域优先"自然把这几棵子树从低权里捞回来；
    ///   低   —— 系统目录、Windows.old、升级残留、AppData、Program Files；
    ///   低 + 文件名正则 —— 临时文件、隐藏文件、node_modules、无扩展名条目；
    ///   从搜索结果中排除 —— 出厂为空（真剔除是要用户自己按的按钮）。
    /// 全部只降权、不隐藏：隐藏由「默认不展示系统及隐藏文件的搜索结果」开关控制。
    /// </summary>
    internal static List<Entry> Builtins()
    {
        var list = new List<Entry>(32);
        int index = 0;

        void Folder(string display, QbarPriorityLevel level)
        {
            string? normalized = NormalizeFolder(display);
            if (normalized == null) return;
            string key = (level == QbarPriorityLevel.Normal ? "normal:" : "low:") + normalized.ToLowerInvariant();
            list.Add(new Entry(key, normalized, "", null, level, display, index++));
        }

        // 正常：默认包里唯一"捞回来"的一档，路径必须原样显示给用户看。
        Folder(@"%USERPROFILE%\AppData\Roaming\Microsoft\Internet Explorer", QbarPriorityLevel.Normal);
        Folder(@"%USERPROFILE%\AppData\Roaming\Microsoft\Windows", QbarPriorityLevel.Normal);
        Folder(@"%ProgramData%\Microsoft\Windows\Start Menu", QbarPriorityLevel.Normal);

        // 低：系统与程序的落脚点。
        Folder(@"%SystemDrive%\Windows.old", QbarPriorityLevel.Low);
        Folder(@"%SystemDrive%\$WINDOWS.~BT", QbarPriorityLevel.Low);
        Folder(@"%SystemDrive%\$Windows.~WS", QbarPriorityLevel.Low);
        Folder(@"%ProgramData%", QbarPriorityLevel.Low);
        Folder(@"%SystemRoot%", QbarPriorityLevel.Low);
        Folder(@"%USERPROFILE%\AppData", QbarPriorityLevel.Low);
        Folder(@"%ProgramW6432%", QbarPriorityLevel.Low);
        Folder(@"%ProgramFiles(x86)%", QbarPriorityLevel.Low);

        // 低：文件名正则（和目录规则同档，靠"匹配到的名字"降权）。
        string[] patterns =
        {
            @"^\",
            @"^~",
            @"^[^.]+$",
            @"^node_modules$",
            @"^\$",
        };

        for (int i = 0; i < patterns.Length; i++)
        {
            Regex? regex = Compile(patterns[i]);
            if (regex == null) continue;
            list.Add(new Entry("regex:" + patterns[i], null, patterns[i], regex,
                                QbarPriorityLevel.Low, patterns[i], index++));
        }

        return list;
    }

    /// <summary>
    /// 内置规则里被用户拨掉的那些键（设置页逐行开关用）。
    /// 顺序保持 Builtins 的顺序，开关状态才能稳定回显。
    /// </summary>
    internal static IReadOnlyList<(string Key, QbarPriorityLevel Level, string Text, bool IsRegex)> BuiltinRows()
    {
        var rows = new List<(string, QbarPriorityLevel, string, bool)>(20);
        foreach (Entry e in Builtins())
            rows.Add((e.Key, e.Level, e.Folder ?? e.Pattern, e.Regex != null));
        return rows;
    }

    private static Regex? Compile(string pattern)
    {
        if (pattern.Length == 0) return null;
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException) { return null; }
    }

    internal static string? NormalizeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string expanded = path.Trim();
        try { expanded = ExpandEnvironment(expanded); }
        catch (ArgumentException) { }

        if (expanded.Length > 3)
        {
            char last = expanded[expanded.Length - 1];
            if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
                expanded = expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return expanded.Length == 0 ? null : expanded;
    }

    /// <summary>
    /// 变量展开，补齐 <see cref="Environment.ExpandEnvironmentVariables"/> 在 32 位进程里的空档：
    /// %ProgramW6432% / %ProgramFiles(x86)% 在 32 位下根本没定义，原样留着会变成一条
    /// 永远匹配不上的规则 —— 界面上看着"生效中"，实际什么都没做，这种静默最难查。
    /// </summary>
    internal static string ExpandEnvironment(string text)
    {
        string expanded = Environment.ExpandEnvironmentVariables(text);
        if (expanded.IndexOf('%') < 0) return expanded;

        string Replace(string s, string token, string? value) =>
            string.IsNullOrEmpty(value) ? s : s.Replace(token, value, StringComparison.OrdinalIgnoreCase);

        expanded = Replace(expanded, "%ProgramW6432%", Environment.GetEnvironmentVariable("ProgramW6432")
                                                   ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        expanded = Replace(expanded, "%ProgramFiles(x86)%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        expanded = Replace(expanded, "%SystemDrive%", Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\'));

        // 展开后还带 % 的，说明这台机器真的没有这个变量：返回空让调用方跳过它。
        return expanded.IndexOf('%') >= 0 ? "" : expanded;
    }

    internal sealed class Entry
    {
        public Entry(string key, string? folder, string pattern, Regex? regex, QbarPriorityLevel level, string label, int index)
        {
            Key = key;
            Folder = folder;
            Pattern = pattern;
            Regex = regex;
            Level = level;
            Label = label;
            Index = index;
        }

        /// <summary>规则身份串：设置页按它记住"哪一条被拨掉了"。</summary>
        public string Key { get; }
        public string? Folder { get; }
        public string Pattern { get; }
        public Regex? Regex { get; }
        public QbarPriorityLevel Level { get; }
        public string Label { get; }
        public int Index { get; }

        /// <summary>
        /// 返回作用域长度（目录前缀长度），不匹配返回 -1。
        /// <para>
        /// scopeDir 由调用方算好传进来（= 文件所在目录）。以前这里每命中一条规则就
        /// 自己 Path.GetDirectoryName 一次：2000 条候选 × 11 条目录规则 = 2 万次分配，
        /// 实测吃掉重排段 10ms 里的大部分。算法不变，只是把这一次路径切分提到循环外。
        /// </para>
        /// </summary>
        public int MatchScope(string? scopeDir, string? dir, string name)
        {
            if (Folder != null)
            {
                if (scopeDir == null) return -1;
                if (!IsSameOrUnder(scopeDir, Folder)) return -1;
            }

            if (Regex != null && !Regex.IsMatch(name)) return -1;

            return Folder?.Length ?? (dir?.Length ?? 0);
        }
    }

    /// <summary>路径段比较：C:\foo 不该命中 C:\foobar。</summary>
    private static bool IsSameOrUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;
        if (root.Length == 0) return false;

        char last = root[root.Length - 1];
        if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);

        return path.Length > root.Length
               && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && (path[root.Length] == Path.DirectorySeparatorChar
                   || path[root.Length] == Path.AltDirectorySeparatorChar);
    }
}
