using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InputPlus.Core.Qbar;

/// <summary>最近打开的目标是哪一类 —— 只影响显示时的图标与徽标，不决定怎么打开。</summary>
public enum RecentTargetKind
{
    /// <summary>普通文件（文档、图片、压缩包……）。</summary>
    File,
    /// <summary>文件夹（含 shell:Downloads 这类命名空间）。</summary>
    Folder,
    /// <summary>网址。</summary>
    Url,
    /// <summary>可执行程序（.exe / .bat / .lnk ……）。</summary>
    Program,
}

/// <summary>
/// 一条"最近打开"记录。
/// <para><see cref="Target"/> 是真正要打开的东西（路径或网址），<see cref="Title"/> 只用于显示。</para>
/// <para><see cref="LastUsedTicks"/> 是 UTC 时间刻度，用来排序与淘汰；<see cref="Uses"/> 是用过几次。</para>
/// </summary>
public sealed record RecentTarget(
    string Target,
    string Title,
    RecentTargetKind Kind,
    long LastUsedTicks,
    int Uses);

/// <summary>
/// 「最近打开的项目」—— Qbar 空面板最上面那几条。
///
/// 记录的是<b>我们替用户打开过的东西</b>（直达文件 / 直达网址 / 运行命令里的程序），
/// 不是"用户碰过什么"。所以记录点只在真正启动成功之后，失败不记。
///
/// 存储：数据目录（<see cref="AppPaths.DataDir"/>，和 settings.json 同一层）下的
/// recent-targets.json。上限 40 条，超出按最后使用时间淘汰。
///
/// 为什么写盘要防抖：<see cref="Record"/> 会被动作线程频繁调用（连续打开好几个文件），
/// 每次都同步写文件会让按键动作明显变慢。这里只在内存里改完标脏，
/// 300ms 后用后台定时器合并写一次；UI 线程完全不参与磁盘等待。
///
/// 读盘失败、JSON 损坏一律当空列表继续 —— 这是"锦上添花"的数据，
/// 绝不为了它弹窗或者让打开动作失败。
/// </summary>
public static class RecentTargets
{
    /// <summary>最多留多少条。再多用户也不会去翻，只会把文件撑大。</summary>
    private const int MaxEntries = 40;

    /// <summary>标脏之后隔多久落盘（毫秒）。</summary>
    private const int SaveDelayMs = 300;

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>内存里的列表；null 表示"还没读过盘"，第一次访问时才加载。</summary>
    private static List<RecentTarget>? _items;

    private static bool _dirty;

    private static System.Threading.Timer? _saveTimer;

    /// <summary>自检用：把存储位置指到临时文件。置回 null 恢复默认位置。</summary>
    internal static string? StorePathForTest
    {
        get { lock (Gate) return _storePathOverride; }
        set
        {
            lock (Gate)
            {
                _storePathOverride = string.IsNullOrWhiteSpace(value) ? null : value;
                // 换了位置就把内存清空，下次访问重新读 —— 否则测不出"真的落盘了"
                _items = null;
                _dirty = false;
            }
        }
    }

    private static string? _storePathOverride;

    /// <summary>真正使用的存储文件。</summary>
    private static string StorePath
        => _storePathOverride ?? Path.Combine(AppPaths.DataDir, "recent-targets.json");

    static RecentTargets()
    {
        // 进程退出前把还没落盘的那一次写掉（300ms 防抖窗口内直接关程序也不会丢）
        try { AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(); }
        catch { /* 挂不上就算了，不能因为记"最近"把程序弄崩 */ }
    }

    // ================= 分类 =================

    /// <summary>
    /// 判断一个目标属于哪一类。纯函数：不读写文件内容、不启动进程，
    /// 只做 <see cref="Directory.Exists(string)"/> 与扩展名判断，且全部兜住异常。
    /// </summary>
    public static RecentTargetKind Classify(string target)
    {
        try
        {
            string t = Normalize(target);
            if (t.Length == 0) return RecentTargetKind.File;

            if (t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                return RecentTargetKind.Folder;

            /* 网址优先：https://... 在磁盘上当然不存在，先按网址认下来；
             * 裸域名（example.com）也走这条，否则会被当成"扩展名 .com 的程序"。 */
            if (QbarService.LooksLikeUrl(t)) return RecentTargetKind.Url;

            if (Directory.Exists(t)) return RecentTargetKind.Folder;

            if (IsProgramExtension(Path.GetExtension(t))) return RecentTargetKind.Program;

            return RecentTargetKind.File;
        }
        catch
        {
            return RecentTargetKind.File;
        }
    }

    /// <summary>这些扩展名视为"可执行程序"（打开它等于启动一个程序）。</summary>
    private static bool IsProgramExtension(string? ext)
    {
        if (string.IsNullOrEmpty(ext)) return false;
        return ext.ToLowerInvariant() switch
        {
            ".exe" or ".com" or ".bat" or ".cmd" or ".lnk" or ".msi"
                or ".ps1" or ".py" or ".jar" or ".appref-ms" => true,
            _ => false,
        };
    }

    // ================= 对外接口 =================

    /// <summary>按最后使用时间倒序取前 <paramref name="count"/> 条（返回快照，外部改不动内部状态）。</summary>
    public static IReadOnlyList<RecentTarget> Top(int count)
    {
        if (count <= 0) return Array.Empty<RecentTarget>();

        lock (Gate)
        {
            var items = EnsureLoaded();
            int n = Math.Min(count, items.Count);
            if (n <= 0) return Array.Empty<RecentTarget>();

            var snapshot = new RecentTarget[n];
            items.CopyTo(0, snapshot, 0, n);
            return snapshot;
        }
    }

    /// <summary>当前记录条数。</summary>
    public static int Count
    {
        get
        {
            lock (Gate) return EnsureLoaded().Count;
        }
    }

    /// <summary>
    /// 记一条"刚刚替用户打开了它"。同一个目标（大小写不敏感）会前移并累加次数。
    /// 这个方法只碰内存，磁盘写入由 300ms 防抖定时器在后台完成 ——
    /// 可以在动作线程里随便调，但<b>不要</b>在低级键盘钩子回调里调。
    /// </summary>
    public static void Record(string target, string? title = null)
    {
        string t;
        try { t = Normalize(target); }
        catch { return; }
        if (t.Length == 0) return;

        RecentTargetKind kind;
        try { kind = Classify(t); }
        catch { kind = RecentTargetKind.File; }

        string display;
        try
        {
            display = string.IsNullOrWhiteSpace(title) ? DefaultTitle(t, kind) : title!.Trim();
        }
        catch { display = t; }
        if (display.Length == 0) display = t;
        if (display.Length > 120) display = display[..120];

        lock (Gate)
        {
            var items = EnsureLoaded();

            int existing = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Target, t, StringComparison.OrdinalIgnoreCase))
                {
                    existing = i;
                    break;
                }
            }

            int uses = 1;
            if (existing >= 0)
            {
                uses = items[existing].Uses + 1;
                items.RemoveAt(existing);
            }

            items.Insert(0, new RecentTarget(t, display, kind, DateTime.UtcNow.Ticks, uses));

            while (items.Count > MaxEntries) items.RemoveAt(items.Count - 1);

            _dirty = true;
            ScheduleSave();
        }
    }

    /// <summary>清空记录（内存 + 文件）。</summary>
    public static void Clear()
    {
        string path;
        lock (Gate)
        {
            _items = new List<RecentTarget>();
            _dirty = false;
            path = StorePath;
        }

        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { CoreLog.Warn($"[最近打开] 清空失败：{ex.Message}"); }
    }

    // ================= 内部：加载 / 保存 =================

    private static List<RecentTarget> EnsureLoaded()
    {
        if (_items != null) return _items;
        _items = LoadFrom(StorePath);
        return _items;
    }

    private static List<RecentTarget> LoadFrom(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new List<RecentTarget>();

            /* 首次访问是在调用方线程上（多半是 UI 线程）同步读的，所以先卡住大小：
             * 40 条正常也就几 KB，超过 512 KB 只可能是被别的程序写坏了 ——
             * 当空列表继续，别把一份垃圾读进内存、也别卡住界面。 */
            if (info.Length > 512 * 1024)
            {
                CoreLog.Warn($"[最近打开] 存储文件异常膨胀（{info.Length} 字节），按空列表继续");
                return new List<RecentTarget>();
            }

            string json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<RecentTarget>>(json, JsonOptions);
            if (list == null) return new List<RecentTarget>();

            var clean = new List<RecentTarget>(Math.Min(list.Count, MaxEntries));
            foreach (var item in list)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Target)) continue;
                var safe = string.IsNullOrWhiteSpace(item.Title)
                    ? item with { Title = item.Target }
                    : item;
                clean.Add(safe);
                if (clean.Count >= MaxEntries) break;
            }
            return clean;
        }
        catch (Exception ex)
        {
            // 文件损坏 / 没权限：当空列表继续，绝不打扰用户
            CoreLog.Warn($"[最近打开] 读取失败，按空列表继续：{ex.Message}");
            return new List<RecentTarget>();
        }
    }

    private static void ScheduleSave()
    {
        _saveTimer ??= new System.Threading.Timer(_ => FlushIfDirty(), null,
            Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(SaveDelayMs, Timeout.Infinite);
    }

    private static void FlushIfDirty()
    {
        List<RecentTarget>? snapshot;
        string path;

        lock (Gate)
        {
            if (!_dirty || _items == null) return;
            _dirty = false;
            snapshot = new List<RecentTarget>(_items);
            path = StorePath;
        }

        SaveTo(path, snapshot);
    }

    /// <summary>立刻落盘（自检用；进程退出时也会调）。</summary>
    internal static void Flush()
    {
        List<RecentTarget>? snapshot;
        string path;

        lock (Gate)
        {
            if (_items == null) return;
            _dirty = false;
            snapshot = new List<RecentTarget>(_items);
            path = StorePath;
        }

        SaveTo(path, snapshot);
    }

    /// <summary>丢掉内存里的副本，下次访问重新读盘（自检用）。</summary>
    internal static void Reload()
    {
        lock (Gate)
        {
            _items = null;
            _dirty = false;
        }
    }

    private static void SaveTo(string path, List<RecentTarget> items)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(items, JsonOptions);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"[最近打开] 保存失败：{ex.Message}");
        }
    }

    // ================= 内部：文本整理 =================

    /// <summary>去空白/引号，本地路径统一反斜杠并去尾部斜杠；网址原样保留。</summary>
    private static string Normalize(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "";

        string t = target.Trim().Trim('"').Trim();
        if (t.Length == 0) return "";
        if (t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return t;

        bool looksLocal = t.Contains('\\') || t.Contains('/') || (t.Length > 1 && t[1] == ':');
        if (looksLocal
            && !t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            t = t.Replace('/', '\\');
            if (t.Length > 3 && t.EndsWith('\\')) t = t.TrimEnd('\\');
        }

        return t;
    }

    /// <summary>没给标题时的默认显示名：路径取文件名/目录名，网址取主机名。</summary>
    private static string DefaultTitle(string target, RecentTargetKind kind)
    {
        try
        {
            if (kind == RecentTargetKind.Url)
            {
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                    return uri.Host;
                return Truncate(target, 60);
            }

            string p = target.TrimEnd('\\');
            string name = Path.GetFileName(p);
            if (name.Length > 0) return name;
            return Truncate(target, 60);
        }
        catch
        {
            return Truncate(target, 60);
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
