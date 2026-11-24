using InputPlus.Core.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using InputPlus.Core.Gestures;
using InputPlus.Core.Input;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Settings;

/// <summary>设置的读写（JSON，带注释友好的格式、原子写入与自动备份）。</summary>
public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Gate = new();

    /* ---- 保存节流 ----------------------------------------------------------
     *
     * 现状：全仓 71 处调用 SaveSettings()，其中参数框的 TextChanged 是**逐字符**触发。
     * Save() 里是"序列化 + AES + 写 tmp + 复制 bak + move"全套同步 IO，
     * 打一个三位数就是三次，拖滑杆每 tick 一次 —— 输入卡顿与无谓磁盘写入都从这来。
     *
     * 现在的做法：
     *   1. RequestSave() 只记一个"待保存"，400ms 内有新改动就顺延（防抖）；
     *   2. 到时在**调用线程（界面线程）**上序列化成字符串 —— 对象图只在这条线程被改，
     *      这样后台线程永远不会去枚举正在变动的集合（之前的一个偶发崩溃源）；
     *   3. 加密与落盘交给一条后台写线程，按"后写覆盖先写"合并。
     *
     * 退出/恢复等关键路径仍然可以调 Save() 同步落盘。
     */
    private const int DebounceMs = 400;
    private static AppSettings? _pendingSave;
    private static System.Threading.Timer? _debounce;
    private static string? _queuedJson;
    private static Thread? _writerThread;
    private static readonly AutoResetEvent WriteSignal = new(false);

    /// <summary>
    /// 把回调切到界面线程（由 AppHost 注入）。
    ///
    /// 序列化必须在"改对象图的那条线程"上做，否则后台线程会去枚举
    /// 用户正在增删的 List，抛"集合已修改"。Core 层不引用 WPF，
    /// 所以由上层注入一个 marshal 函数。
    /// </summary>
    public static Action<Action>? Marshal { get; set; }

    /// <summary>
    /// 设置文件最后写入时间（本地时间）。
    /// 启动时同步要拿它跟云端备份的修改时间比"谁新用谁"。
    /// 文件不存在时返回最小时间，这样云端那份会被判定为更新。
    /// </summary>
    public static DateTime LastWriteTimeUtc
    {
        get
        {
            try
            {
                var fi = new FileInfo(AppPaths.SettingsFile);
                return fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }

    /// <summary>
    /// 把另一份设置的内容整体拷进目标对象（保持引用不变）。
    ///
    /// 用于"从备份恢复"：界面各处都持有同一个 AppSettings 引用，
    /// 直接替换引用会让它们指向旧对象，所以必须原地修改。
    /// </summary>
    public static void CopyInto(AppSettings target, AppSettings source)
    {
        var src = JsonSerializer.Serialize(source, JsonOptions);
        var copy = JsonSerializer.Deserialize<AppSettings>(src, JsonOptions);
        if (copy == null) return;

        target.Global = copy.Global;
        target.Qbar = copy.Qbar;
        target.Sync = copy.Sync;
        target.Security = copy.Security;
        target.Keys = copy.Keys;
        target.Layers = copy.Layers;
        target.Triggers = copy.Triggers;
        target.Macros = copy.Macros;
        target.Search = copy.Search;
        target.Run = copy.Run;
        target.Web = copy.Web;
        target.HotStrings = copy.HotStrings;
        target.Hotstring = copy.Hotstring;
        target.SchemaVersion = copy.SchemaVersion;
        target.BuiltinEnginesVersion = copy.BuiltinEnginesVersion;
        target.BuiltinWebVersion = copy.BuiltinWebVersion;
        target.BuiltinRunVersion = copy.BuiltinRunVersion;
        target.CapsloxDefaultsVersion = copy.CapsloxDefaultsVersion;
    }

    /// <summary>所有绑定列表（各层的 Keys + 老的顶层 Keys）。</summary>
    private static IEnumerable<List<KeyBinding>> AllBindingLists(AppSettings s)
    {
        yield return s.Keys;
        foreach (var layer in s.Layers) yield return layer.Keys;
    }

    /// <summary>
    /// 把对象图里的 null 洗干净：字符串 → ""、集合 → 空集合、列表里的 null 元素 → 删掉。
    ///
    /// 只走 InputPlus 自己的类型（按命名空间判断），也只处理有 setter 的
    /// 公共属性；层数限制 8 层防环（配置对象图是树，这里只是保险）。
    /// </summary>
    private static void SanitizeNulls(object? root, int depth = 0)
    {
        if (root == null || depth > 8) return;

        foreach (var prop in root.GetType()
                     .GetProperties(System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;

            var type = prop.PropertyType;

            if (type == typeof(string))
            {
                if (prop.GetValue(root) == null) prop.SetValue(root, "");
                continue;
            }

            // 泛型列表：补空集合 / 删 null 元素 / 递归进元素
            if (type.IsGenericType &&
                typeof(System.Collections.IList).IsAssignableFrom(type))
            {
                if (prop.GetValue(root) is not System.Collections.IList list)
                {
                    try { prop.SetValue(root, Activator.CreateInstance(type)); } catch { }
                    continue;
                }

                var element = type.GetGenericArguments()[0];
                if (element == typeof(string))
                {
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] == null) list[i] = "";
                    continue;
                }
                if (element.IsValueType) continue;

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] == null) { list.RemoveAt(i); continue; }
                    SanitizeNulls(list[i], depth + 1);
                }
                continue;
            }

            // 配置里的嵌套对象（GlobalOptions / LayerOptions / Macro …）
            if (type.IsClass && type.Namespace != null &&
                type.Namespace.StartsWith("InputPlus", StringComparison.Ordinal))
            {
                var value = prop.GetValue(root);
                if (value == null)
                {
                    try { prop.SetValue(root, Activator.CreateInstance(type)); } catch { }
                    continue;
                }
                SanitizeNulls(value, depth + 1);
            }
        }
    }

    /// <summary>
    /// 上次 <see cref="Load"/> 失败时给用户看的说明；正常时为 null。
    ///
    /// 以前这里的处理是"复制成 settings.json.broken，然后返回默认设置"，
    /// 日志里留一行 ERROR —— 但用户看不到日志，表现就是**开机后所有配置都没了**，
    /// 而且没有任进何提示，只能以为"软件把我的设置弄丢了"。
    /// 现在把失败原因挂在这里，由 AppHost 启动后提示一次。
    /// </summary>
    public static string? LoadFailure { get; private set; }

    public static AppSettings Load()    {
        lock (Gate)
        {
            LoadFailure = null;
            try
            {
                /* 先把"防抖中 / 排队中"的保存落盘，再读文件。
                 *
                 * 没有这一步，"保存 → 立刻重新加载"这类组合会读到**旧文件**：
                 * 恢复默认设置、导入旧版 ini、导入配置文件、恢复备份
                 * 都是 SaveSettings() 紧跟 ReloadAll()，而 SaveSettings 只
                 * 存下对象并挂了一个 400ms 的防抖计时器 —— 文件这时还是旧的。
                 * 于是界面弹了"已恢复"，内存里却换回了旧配置，
                 * 400ms 后计时器又把新配置写进磁盘，两边彻底对不上；
                 * 下一次任何改动都会把旧对象再存一遍，恢复的内容永久丢失。
                 *
                 * 这里只做"把已经决定要写的写掉"，不改任何业务行为。 */
                if (_pendingSave is { } pending)
                {
                    _pendingSave = null;
                    _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
                    string? pendingJson = SerializeLocked(pending);
                    if (pendingJson != null) _queuedJson = pendingJson;
                }
                DrainWritesLocked();

                Directory.CreateDirectory(AppPaths.DataDir);
                if (!File.Exists(AppPaths.SettingsFile))
                {
                    var fresh = DefaultSettings.Create();
                    Save(fresh);
                    return fresh;
                }

                /* 每天留一份原始副本：别名"更新几次就不见了"这类问题，
                 * 事后只能靠历史文件找回来（程序无法凭空知道丢过什么）。 */
                SnippetRecovery.SnapshotFile();

                string raw = File.ReadAllText(AppPaths.SettingsFile);

                /* 没有设置备份密码时也会用**本机密钥**加密（CLP2:）——
                 * 保证"任何情况下设置都不落明文"。 */
                if (LocalCrypto.IsEncrypted(raw))
                {
                    string? plainText = LocalCrypto.TryDecrypt(raw);
                    if (plainText == null)
                    {
                        _dataLocked = true;
                        LoadFailure = "设置文件已加密，但本机密钥无法解密，已进入只读保护，不会覆盖原文件。" +
                                      (LocalCrypto.LastError is { Length: > 0 } ke ? "\n" + ke : "");
                        CoreLog.Error("设置文件已用本机密钥加密，但当前用户解不开 —— 进入只读保护");
                        return DefaultSettings.Create();
                    }
                    raw = plainText;
                }

                /* 加密过的设置：先解锁再解密。
                 *
                 * 解不开时**绝不能**返回默认值然后让它覆盖原文件 ——
                 * 那等于把用户所有配置一次性抹掉（而且不可逆）。
                 * 这里进入"只读保护"：返默认值给界面先用着，
                 * 但 Save 全部跳过，等上层拿到密码解锁后再正常读写。 */
                if (DataVault.LooksEncrypted(raw))
                {
                    if (!DataVault.IsUnlocked) DataVault.TryUnlockCached();
                    string? plain = DataVault.Decrypt(raw);
                    if (plain == null)
                    {
                        _dataLocked = true;
                        LoadFailure = "设置已加密，但当前无法解密（密钥缓存可能已失效）。已进入只读保护，请用备份密码解锁。";
                        CoreLog.Error("设置已加密但当前无法解密（密钥缓存失效）—— 进入只读保护，不会覆盖原文件");
                        return DefaultSettings.Create();
                    }
                    raw = plain;
                }

                var s = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions) ?? DefaultSettings.Create();

                /* 结构上"什么都没有"的文件 → 当成坏的，用默认设置。
                 *
                 * 合法 JSON 但只有 `{"Global":{...}}` 这种：`Normalize` 会给它补上
                 * 10 个空层和一个 CapsLock 触发键，于是程序**照常启动、但一个快捷键都不响应** ——
                 * 用户看到的是"软件坏了"，日志里却一句解释都没有。
                 * 这种文件不可能是一次正常保存产生的（保存一定会写 Keys/Layers/Triggers），
                 * 多半是被别的工具截断、或者手改时删多了。宁可给默认值并留一条日志。 */
                if (IsStructurallyEmpty(s))
                {
                    CoreLog.Warn("设置文件里没有任何按键 / 层 / 触发键（可能被截断或手动清空），尝试从备份恢复");
                    string broken = PreserveBrokenCopy();

                    if (TryLoadBackup(out var restored, out string backupError))
                    {
                        _skipNextBackupCopy = true;   // 不要让坏主文件覆盖唯一一份好备份
                        LoadFailure = "设置文件结构不完整，已从上次备份恢复。" +
                                      (broken.Length > 0 ? "\n损坏的原文件已另存为 " + broken + "。" : "");
                        CoreLog.Warn("设置结构不完整，已从 .bak 恢复");
                        TryRepairPrimary(restored);
                        return restored;
                    }

                    LoadFailure = "设置文件结构不完整，本次先用默认设置启动。" +
                                  (broken.Length > 0
                                      ? "\n原文件已另存为 " + broken + "，可手工修复后换回去。"
                                      : "") +
                                  (backupError.Length > 0 ? "\n备份也无法读取：" + backupError : "");
                    return DefaultSettings.Create();
                }

                Normalize(s);
                bool migrated = MigrateBuiltinEngines(s) | MigrateBuiltinWebAndRun(s)
                                | MigrateSendKeysToCustomKey(s);

                /* 这份配置来自**更新的版本**：本程序不认识的字段在反序列化时已经丢了，
                 * 再自动 Save 一次就等于把新版配置整份降级覆盖（换回新版本也读不回来）。
                 * 这种文件只读、不自动回写；用户主动改设置时仍会写（那是他的选择）。 */
                if (s.SchemaVersion > AppSettings.CurrentSchemaVersion)
                {
                    CoreLog.Warn($"设置文件版本 {s.SchemaVersion} 比本程序支持的 {AppSettings.CurrentSchemaVersion} 新："
                                 + "本次只读，不自动回写，避免把新版本的字段覆盖掉");
                    return s;
                }

                /* 已经设了备份密码、但这份文件还是明文的旧数据 → 立刻以加密形式重存，
                 * 让"设了密码就不再留明文"当场生效，而不是等下一次改设置。 */
                bool needsEncrypt = !LocalCrypto.IsEncrypted(File.ReadAllText(AppPaths.SettingsFile))
                                    && !(DataVault.IsEnabled && DataVault.IsUnlocked
                                         && DataVault.LooksEncrypted(File.ReadAllText(AppPaths.SettingsFile)));

                if (migrated || needsEncrypt) Save(s);

                /* 顺手清掉明文副本（.bak / .broken / .tmp）。
                 *
                 * 每次都清而不是只在"用户改密码"时清：数据目录可能刚被迁移过，
                 * 老目录里的明文 .bak 会跟着搬进来；程序也可能在崩溃时留下明文 .tmp。
                 * 只要加密是开着的，这些文件就不该存在于磁盘上。 */
                if (DataVault.IsEnabled && DataVault.IsUnlocked)
                    DataVault.PurgePlaintextCopies(AppPaths.SettingsFile);

                return s;
            }
            catch (Exception ex)
            {
                CoreLog.Error("读取设置失败，将尝试从备份恢复", ex);

                string brokenPath = PreserveBrokenCopy();

                /* 先尝试 .bak：正常保存每次都会留下上一份完整配置。
                 * 旧实现直接返回默认值，用户明明有备份却什么都恢复不了。 */
                if (TryLoadBackup(out var restored, out string backupError))
                {
                    _skipNextBackupCopy = true;
                    LoadFailure = "设置文件读不出来，已从上次备份恢复。" +
                                  (brokenPath.Length > 0 ? "\n损坏的原文件已另存为 " + brokenPath + "。" : "");
                    CoreLog.Warn("主设置文件损坏，已从 .bak 恢复");
                    TryRepairPrimary(restored);
                    return restored;
                }

                /* 本机密钥读不出来时，磁盘上的密文都还等着原钥匙，绝不能写入默认值。
                 * 其它解析错误只保留 .broken 副本并允许用户随后主动修改/重置。 */
                if (LocalCrypto.LastError is { Length: > 0 })
                    _dataLocked = true;

                /* 主文件和备份都读不出来时，本次只能拿默认值让界面先起来；
                 * 但**绝不能**让这份默认值随后落盘把原文件覆盖掉。
                 * 之前正是这条路径在“文件被占用/复制到一半”时静默清空过用户配置。
                 * 进入只读保护，等用户修好文件或从历史副本恢复。 */
                if (File.Exists(AppPaths.SettingsFile))
                    _dataLocked = true;

                string keyNote = LocalCrypto.LastError is { Length: > 0 }
                    ? "\n" + LocalCrypto.LastError
                    : "";

                LoadFailure = (brokenPath.Length > 0
                                  ? "设置文件读不出来，已把原文件另存为 " + brokenPath + "，\n"
                                  : "设置文件读不出来（原文件可能已被占用或权限不足）。\n") +
                              "本次先用默认设置启动，并已进入只读保护，不会覆盖原文件。" +
                              (backupError.Length > 0 ? "\n备份也无法读取：" + backupError : "") +
                              keyNote;

                var s = DefaultSettings.Create();
                return s;
            }
        }
    }

    /// <summary>
    /// 数据因为"加密但解不开"而处于只读保护状态。
    ///
    /// 界面必须据此拦住"保存"，否则会用空配置覆盖掉用户那份还等着解锁的数据。
    /// </summary>
    public static bool IsDataLocked { get { lock (Gate) return _dataLocked; } }

    private static bool _dataLocked;

    /// <summary>
    /// 主文件损坏、本次从 .bak 恢复时，跳过下一次"把当前主文件复制成 .bak"。
    /// 否则第一次自动保存会把唯一一份完好备份覆盖成坏文件。
    /// </summary>
    private static bool _skipNextBackupCopy;

    /// <summary>
    /// 用密码解锁并重新读一遍设置（上层弹完密码框后调用）。
    /// 返回 false = 密码不对。
    /// </summary>
    public static bool UnlockAndReload(string password, out AppSettings settings)
    {
        lock (Gate)
        {
            settings = DefaultSettings.Create();
            if (!DataVault.UnlockWithPassword(password)) return false;

            _dataLocked = false;
            settings = Load();
            return true;
        }
    }

    /// <summary>请求保存（防抖 400ms）。界面上的高频改动都走这里。</summary>
    public static void RequestSave(AppSettings settings)
    {
        lock (Gate)
        {
            _pendingSave = settings;
            _debounce ??= new System.Threading.Timer(_ =>
            {
                Action<Action>? marshal = Marshal;
                if (marshal != null) marshal(SaveDeferredNow);
                else SaveDeferredNow();
            }, null, Timeout.Infinite, Timeout.Infinite);

            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>把当前待保存内容序列化并排队（必须在界面线程调用）。</summary>
    private static void SaveDeferredNow()
    {
        AppSettings? pending;
        lock (Gate)
        {
            pending = _pendingSave;
            _pendingSave = null;
        }
        if (pending != null) SaveDeferred(pending);
    }

    /// <summary>
    /// 保存但**不等落盘**：序列化（调用线程）→ 交给后台写线程。
    ///
    /// 界面上的高频保存走这条：加密 + 三次文件操作都在后台，
    /// 界面线程只花一次序列化的时间。
    /// </summary>
    public static void SaveDeferred(AppSettings settings)
    {
        string? json;
        lock (Gate) json = SerializeLocked(settings);
        if (json == null) return;

        lock (Gate) _queuedJson = json;
        EnsureWriter();
        WriteSignal.Set();
    }

    /// <summary>
    /// 立刻把待保存的内容落盘（退出、切页面、用户明确点"保存"时用）。
    /// 同步执行，保证函数返回时文件已经写好。
    /// </summary>
    public static void FlushPending()
    {
        AppSettings? pending;
        lock (Gate)
        {
            pending = _pendingSave;
            _pendingSave = null;
            _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (pending != null) Save(pending);
        else DrainWrites();
    }

    /// <summary>把已排队的写入全部落盘（同步，退出前用）。</summary>
    public static void DrainWrites()
    {
        lock (Gate) DrainWritesLocked();
    }

    /// <summary>
    /// 后台写线程：只负责"加密 + 落盘"。
    ///
    /// 队列里永远只留最新一份（<see cref="_queuedJson"/>）：
    /// 用户连打十个字符，磁盘上只会留下最后一次的结果，中间态没有意义。
    /// </summary>
    private static void EnsureWriter()
    {
        /* 判断"线程还活着"而不是"句柄非空"。
         *
         * 同一个坑在亮度服务里刚踩过（非空句柄 + 已死线程 = 永远不重建，
         * 之后所有写入都石沉大海）。这里用 IsAlive 兜住：万一写线程因为
         * 磁盘满 / 权限变更之类的意外退出，下一次保存还能重新拉起它。 */
        if (_writerThread is { IsAlive: true }) return;

        _writerThread = new Thread(() =>
        {
            while (true)
            {
                WriteSignal.WaitOne(2000);

                // 兜底：DrainWrites 内部已逐处 try/catch，但别让线程真死掉
                try { DrainWrites(); }
                catch (Exception ex) { CoreLog.Error("设置写入线程出错（已继续运行）", ex); }
            }
        })
        {
            IsBackground = true,
            Name = "InputPlus.SettingsWriter",
            Priority = ThreadPriority.BelowNormal,
        };
        _writerThread.Start();
    }

    /// <summary>
    /// 同步保存：函数返回时文件已经写好。
    /// 退出、恢复备份、切换加密这类"必须立刻落盘"的场景用它；
    /// 界面上的普通改动请用 <see cref="RequestSave"/>。
    ///
    /// 返回 false = 这次没写成功（只读保护 / 未解锁 / 磁盘错误）。
    /// 换密钥这种"写不进去就会丢数据"的操作必须看返回值。
    /// </summary>
    public static bool Save(AppSettings settings)
    {
        string? json;
        lock (Gate) json = SerializeLocked(settings);
        if (json == null) return false;

        lock (Gate)
        {
            _queuedJson = json;      // 覆盖尚未落盘的旧内容，保证顺序
            return DrainWritesLocked();
        }
    }

    /// <summary>
    /// 序列化（调用线程）。返回 null 表示这次不该写盘（只读保护 / 未解锁）。
    /// 只碰内存，不做 IO —— 所以可以放心在界面线程调用。
    /// </summary>
    private static string? SerializeLocked(AppSettings settings)
    {
        // 只读保护期间一律不落盘 —— 这份内存里的对象是默认值，写下去就是灾难
        if (_dataLocked)
        {
            CoreLog.Warn("设置处于只读保护状态，本次保存已跳过");
            return null;
        }

        if (DataVault.IsEnabled && !DataVault.IsUnlocked)
        {
            CoreLog.Warn("加密已启用但尚未解锁，本次保存已跳过");
            return null;
        }

        /* 结构为空的配置只可能来自“某个进程读到了默认对象/临时数据后误保存”，
         * 一旦落盘就会把用户真实的按键、层、别名一起抹掉。
         * 首次安装时目标文件还不存在，这里放行默认配置；已有配置则拒绝覆盖。 */
        if (File.Exists(AppPaths.SettingsFile) && IsStructurallyEmpty(settings))
        {
            CoreLog.Error("拒绝保存结构不完整的设置：目标文件已存在，避免覆盖用户配置");
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(settings, JsonOptions);
        }
        catch (Exception ex)
        {
            CoreLog.Error("序列化设置失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 把损坏的主文件另存一份。已有 .broken 时加时间戳，不覆盖上一次的恢复副本。
    /// 返回副本路径；复制失败或源文件不存在时返回空串。
    /// </summary>
    private static string PreserveBrokenCopy()
    {
        try
        {
            string source = AppPaths.SettingsFile;
            if (!File.Exists(source)) return "";

            string broken = source + ".broken";
            if (File.Exists(broken))
                broken = source + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff");

            File.Copy(source, broken, false);
            CoreLog.Warn("设置损坏副本已保存：" + broken);
            return broken;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("保存设置损坏副本失败：" + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// 尝试从 settings.json.bak 恢复一份完整配置。只读，不修改磁盘。
    /// 支持当前两种加密格式，也兼容旧版明文备份。
    /// </summary>
    private static bool TryLoadBackup(out AppSettings settings, out string error)
    {
        string bakPath = AppPaths.SettingsFile + ".bak";
        string goodPath = AppPaths.SettingsFile + ".good";
        /* .tmp 也参与恢复：一次保存如果在最后那步 File.Move 之前失败，
         * 最新的一份内容就停在 .tmp 里，而它以前从不被读回 —— 下次保存直接覆盖掉。 */
        string tmpPath = AppPaths.SettingsFile + ".tmp";

        /* 先读**更新的那一份**。
         *
         * .bak 装的是「这次保存之前」的那一代，.good 装的是「最近一次成功保存」——
         * 主文件坏掉时，用户手里最新的配置在 .good 里。以前固定先读 .bak，
         * 于是恢复出来的配置比最后一次保存旧一代；更糟的是随后的修复写盘
         * 会把 .good 一起覆盖成那一代，最新副本当场消失，换回新版本也读不回来。
         * 读时间戳失败时退回原来的顺序（.bak 优先），与旧行为一致。 */
        string[] all = { bakPath, goodPath, tmpPath };
        var present = new List<string>(3);
        foreach (string p in all)
        {
            try { if (File.Exists(p)) present.Add(p); } catch { }
        }
        /* 按修改时间从新到旧（同刻时 .bak 在前，与旧行为一致）。 */
        present.Sort((a, b) =>
        {
            try { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); }
            catch { return string.CompareOrdinal(a, b); }
        });
        string[] candidates = present.Count > 0 ? present.ToArray() : new[] { bakPath, goodPath };

        /* out 参数必须先有值：一份候选都读不出来时，调用方拿到的就是默认设置。 */
        settings = DefaultSettings.Create();
        string[] errors = new string[candidates.Length];
        for (int i = 0; i < candidates.Length; i++)
        {
            if (TryLoadFile(candidates[i], out settings, out errors[i])
                && !IsStructurallyEmpty(settings))
            {
                error = "";
                return true;
            }
        }

        string ErrOf(string path)
        {
            int idx = Array.FindIndex(candidates, p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            string e = idx >= 0 && idx < errors.Length ? errors[idx] : "";
            return e.Length > 0 ? e : "结构不完整";
        }
        string bakError = File.Exists(bakPath) ? ErrOf(bakPath) : "文件不存在";
        string goodError = File.Exists(goodPath) ? ErrOf(goodPath) : "文件不存在";
        error = "备份：" + bakError + "；快照：" + goodError;
        return false;
    }

    /// <summary>
    /// 从任意一份设置文件读配置（只读，不写盘、不改磁盘上的任何东西）。
    ///
    /// 用途：`--dump-snippets` 核对备份、以及"找回丢失的别名"扫描历史副本。
    /// 与主流程的区别只有"路径由调用方给"这一条。
    /// </summary>
    internal static bool TryLoadFile(string path, out AppSettings settings, out string error)
    {
        settings = DefaultSettings.Create();
        error = "";
        try
        {
            if (!File.Exists(path))
            {
                error = "文件不存在";
                return false;
            }

            string raw = File.ReadAllText(path);
            if (LocalCrypto.IsEncrypted(raw))
            {
                raw = LocalCrypto.TryDecrypt(raw)
                      ?? throw new InvalidOperationException("这份文件用本机密钥加密，当前无法解密");
            }

            if (DataVault.LooksEncrypted(raw))
            {
                if (!DataVault.IsUnlocked) DataVault.TryUnlockCached();
                raw = DataVault.Decrypt(raw)
                      ?? throw new InvalidOperationException("这份文件已加密，当前无法解密");
            }

            var candidate = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions);
            if (candidate == null) throw new InvalidOperationException("内容为空");
            if (IsStructurallyEmpty(candidate))
                throw new InvalidOperationException("里面没有任何按键、层或宏");

            Normalize(candidate);
            MigrateBuiltinEngines(candidate);
            MigrateBuiltinWebAndRun(candidate);
            MigrateSendKeysToCustomKey(candidate);
            settings = candidate;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            CoreLog.Warn("读取设置文件失败（" + path + "）：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 从 .bak 恢复后立刻把主文件修好（同时保留 .bak）。
    /// 失败也不影响本次启动：内存里已经是恢复出来的配置，下一次保存会再试。
    /// </summary>
    private static void TryRepairPrimary(AppSettings settings)
    {
        try
        {
            if (!Save(settings))
                CoreLog.Warn("从备份恢复后修复主设置文件失败；本次仍使用内存中的恢复配置");
        }
        catch (Exception ex)
        {
            CoreLog.Warn("从备份恢复后修复主设置文件异常：" + ex.Message);
        }
    }

    /// <summary>加密 + 原子写入（只在写线程或同步保存时执行）。返回是否写成功。</summary>
    private static bool DrainWritesLocked()
    {
        bool ok = true;
        while (_queuedJson != null)
        {
            string json = _queuedJson;
            _queuedJson = null;
            ok = WriteJsonLocked(json);
        }
        return ok;
    }

    private static bool IsUsableSettingsFile(string path)
    {
        if (!File.Exists(path)) return false;
        return TryLoadFile(path, out var loaded, out _) && !IsStructurallyEmpty(loaded);
    }

    private static bool WriteJsonLocked(string json)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            // 启用了备份密码 → 用它加密；否则用本机密钥（DPAPI）加密。
            // 两种情况下磁盘上都不会出现明文设置。
            if (DataVault.IsEnabled)
            {
                if (!DataVault.IsUnlocked)
                {
                    CoreLog.Warn("加密已启用但尚未解锁，本次保存已跳过");
                    return false;
                }
                json = DataVault.Encrypt(json);
            }
            else
            {
                json = LocalCrypto.Encrypt(json);
            }

            var tmp = AppPaths.SettingsFile + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            /* 从 .bak 恢复后的第一次保存：主文件仍是坏的，不能拿它覆盖唯一的好备份。
             * 平时也要先确认主文件本身结构完整，不能把空壳/截断文件轮换成 .bak。 */
            if (!_skipNextBackupCopy && IsUsableSettingsFile(AppPaths.SettingsFile))
            {
                /* 先写 .bak.tmp 再原子替换。
                 * 直接 File.Copy 到 .bak 时，进程如果在复制途中被杀（部署脚本
                 * 是 Stop-Process -Force，设置保存又是 400ms 防抖），
                 * 唯一的好备份就会变成半截文件 —— 用户看到的就是"配置没了"。 */
                string bak = AppPaths.SettingsFile + ".bak";
                string bakTmp = bak + ".tmp";
                File.Copy(AppPaths.SettingsFile, bakTmp, true);
                File.Move(bakTmp, bak, true);
            }

            File.Move(tmp, AppPaths.SettingsFile, true);

            /* 再留一份“最近一次成功保存”的快照。它不参与日常 .bak 轮换，
             * 即使主文件和 .bak 都被空壳污染，仍能从这里恢复。
             * 快照失败不影响本次主文件已写入成功。
             *
             * 走 tmp + Move：直接 Copy 到 .good 时，进程如果在复制途中被杀，
             * .good 会变成半截文件 —— 而它是主文件坏掉时**唯一**还有最新一代的副本。
             * .bak 早就改成这套写法了，这里跟上。 */
            try
            {
                string goodTmp = AppPaths.SettingsFile + ".good.tmp";
                File.Copy(AppPaths.SettingsFile, goodTmp, true);
                File.Move(goodTmp, AppPaths.SettingsFile + ".good", true);
            }
            catch (Exception ex) { CoreLog.Warn("保存设置快照失败：" + ex.Message); }

            _skipNextBackupCopy = false;
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Error("保存设置失败", ex);
            /* 写盘抛异常时 _skipNextBackupCopy 以前会一直留 true：
             * 之后每一次保存都跳过 .bak 轮换，备份悄悄停在很久以前那一代。 */
            _skipNextBackupCopy = false;
            return false;
        }
    }

    // ---------------- 动作合并：发送按键序列 → 自定义按键 ----------------

    /// <summary>
    /// 把「发送按键序列」（<c>sendKeys</c>）的配置搬到「自定义按键」（<c>customKey</c>）。
    ///
    /// 为什么合并：两个动作干的是同一件事 —— 把一段按键发给当前窗口。
    /// 区别只有 customKey 多两个能力（绑定应用、以管理员身份启动），
    /// 功能上是超集。两个入口并存的结果是用户在动作列表里选哪个都得试一次。
    /// 合并之后：按键映射、手势、宏、原键替换只剩一种动作 id、一种参数格式。
    ///
    /// 参数怎么搬：老 Arg 是 <c>ctrl+v enter</c> 这种文本，正好就是新
    /// <see cref="CustomKeySpec.Key"/> 的写法，规范化后包进 JSON 即可，
    /// 所以"粘贴并 Enter"这类多段序列一条都不会丢。
    /// 已经手工改成别的动作的绑定不碰（只认 Action 仍然是 sendKeys 的）。
    /// </summary>
    private static bool MigrateSendKeysToCustomKey(AppSettings s)
    {
        bool changed = false;

        static string? SpecOf(string arg)
        {
            var spec = new InputPlus.Core.Actions.CustomKeySpec
            {
                Key = InputPlus.Core.Actions.CustomKeyRunner.Canonical(arg),
            };
            return spec.IsReady ? spec.ToArg() : "";
        }

        static bool Move(KeyBinding b, ref bool flag)
        {
            if (!string.Equals(b.Action, "sendKeys", StringComparison.OrdinalIgnoreCase)) return false;
            b.Action = "customKey";
            b.Arg = SpecOf(b.Arg) ?? "";
            flag = true;
            return true;
        }

        foreach (var layer in s.Layers)
            foreach (var b in layer.Keys)
                Move(b, ref changed);

        foreach (var b in s.Keys) Move(b, ref changed);
        foreach (var b in s.Replacements) Move(b, ref changed);

        foreach (var g in s.Gestures)
        {
            if (!string.Equals(g.Action, "sendKeys", StringComparison.OrdinalIgnoreCase)) continue;
            g.Action = "customKey";
            g.Arg = SpecOf(g.Arg) ?? "";
            changed = true;
        }

        /* 宏里也有"执行动作"这种步骤（Kind=action + ActionId/ActionArg）。
         * 不搬的话，老宏会在播放时报"找不到动作"，
         * 而用户完全看不出是版本升级造成的。 */
        foreach (var m in s.Macros)
            foreach (var st in m.Steps)
            {
                if (!string.Equals(st.Kind, "action", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(st.ActionId, "sendKeys", StringComparison.OrdinalIgnoreCase)) continue;
                st.ActionId = "customKey";
                st.ActionArg = SpecOf(st.ActionArg) ?? "";
                changed = true;
            }

        return changed;
    }

    /// <summary>供自检调用：和启动时走同一条迁移。</summary>
    internal static bool MigrateSendKeysForTest(AppSettings s) => MigrateSendKeysToCustomKey(s);

    /// <summary>供自检调用的迁移入口（与启动时走同一条逻辑）。</summary>
    internal static bool MigrateBuiltinEnginesForTest(AppSettings s) => MigrateBuiltinEngines(s);

    /// <summary>
    /// 把新增的内置搜索引擎补进用户已有设置。
    ///
    /// 为什么需要它：设置文件一旦存在，启动时走的就是"读取"分支，
    /// 不会再套用默认值。于是每次扩充引擎表，老用户都看不到新引擎。
    ///
    /// 策略（保守，不破坏用户自定义）：
    ///   * 用户已有的关键字：原样保留（哪怕改过链接或备注）；
    ///   * 用户没有的关键字：把内置引擎补上；
    ///   * 只补不删，用户自己新增的引擎不受影响。
    /// </summary>
    private static bool MigrateBuiltinEngines(AppSettings s)
    {
        /* 6：bili 现在是常用网址（B 站首页）的关键字，
         * B 站搜索改用 bilis，避免同一个关键字在两张表里打架。 */
        if (s.BuiltinEnginesVersion < 6)
        {
            var oldBili = s.Search.FirstOrDefault(e =>
                e.Key.Equals("bili", StringComparison.OrdinalIgnoreCase));
            if (oldBili != null)
            {
                var existing = s.Search.FirstOrDefault(e =>
                    e.Key.Equals("bilis", StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    oldBili.Key = "bilis";
                    oldBili.Comment = "哔哩哔哩搜索";
                    CoreLog.Info("搜索引擎 bili 已重命名为 bilis，bili 留给常用网址");
                }
                else
                {
                    s.Search.Remove(oldBili);
                    CoreLog.Info("搜索引擎 bili 与 bilis 重复，移除旧 bili");
                }
            }
        }

        /* 9：Twitter 改名 X（v1.4.3）。
         *
         * 站方早就把名字改成 X 了，搜索指令里还写着「Twitter」、链接还是
         * twitter.com/search —— 用户按「x 网站」去找是找不到的。关键字一起
         * 缩成 x（更短、和站名一致），备注写「X 网站」。
         *
         * 只动"还是老 twitter 搜索链接"的那条：用户自己把链接改到别处的，
         * 说明他在拿这个关键字干别的，保留原样。 */
        if (s.BuiltinEnginesVersion < 9)
        {
            var oldTwitter = s.Search.FirstOrDefault(e =>
                e.Key.Equals("tw2", StringComparison.OrdinalIgnoreCase));
            if (oldTwitter != null &&
                (oldTwitter.Value ?? "").Contains("twitter.com/search", StringComparison.OrdinalIgnoreCase))
            {
                var existingX = s.Search.FirstOrDefault(e =>
                    e.Key.Equals("x", StringComparison.OrdinalIgnoreCase));
                if (existingX == null)
                {
                    oldTwitter.Key = "x";
                    oldTwitter.Value = "https://x.com/search?q={q}";
                    oldTwitter.Comment = "X 网站";
                    oldTwitter.EngineIcon = "twitter";   // 内置图标表里 X 用的还是这一颗
                    CoreLog.Info("搜索引擎 tw2 已改名为 x（Twitter → X 网站）");
                }
                else
                {
                    s.Search.Remove(oldTwitter);
                    CoreLog.Info("搜索引擎 tw2 与 x 重复，移除旧 tw2");
                }
            }
        }

        if (s.BuiltinEnginesVersion >= DefaultSettings.BuiltinEnginesVersion) return false;

        // 旧版用过、现在已改名的关键字：直接删掉，免得用户看到 g 和 go 两个 Google。
        // 只删"还保持原始内容"的条目；如果用户改过链接，说明他自己在用，保留。
        var renamedAway = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["g"] = "go",    // Google
            ["b"] = "bi",    // Bing
            ["wk"] = "wi",   // Wikipedia
            ["m"] = "md",    // MDN
        };
        foreach (var (oldKey, newKey) in renamedAway)
        {
            var old = s.Search.FirstOrDefault(e => e.Key.Equals(oldKey, StringComparison.OrdinalIgnoreCase));
            if (old == null) continue;
            var target = DefaultSettings.Create().Search
                .FirstOrDefault(e => e.Key.Equals(newKey, StringComparison.OrdinalIgnoreCase));
            if (target != null && old.Value == target.Value)
            {
                s.Search.Remove(old);
                CoreLog.Info($"搜索引擎 {oldKey} 已重命名为 {newKey}，移除旧条目");
            }
        }

        /* 8：v1.3.0 换表时复用了几个老关键字（ba / db / re / st / tw / wa / so / me），
         * 它们在老版本里指的是别的站点（百度备用 / 豆瓣 / Reddit / Stack Overflow /
         * Twitter / WolframAlpha / 搜狗 / 韦氏词典）。按关键字合并的结果是：老条目保住了
         * 自己的链接，却套上了新站点的图标，同一站点还各多出一条（db2 / re2 …）。
         * 这里按"站点域名 + 路径"合并重复条目，并把图标改回与站点相符的那个。 */
        int deduped = DedupeSearchBySite(s.Search);

        // 先清理"同一个站点、关键字却不同"的旧条目。
        //
        // 背景：早先版本亲手造过一批引擎（tr 翻译 / ai ChatGPT / yh Yahoo Japan …），
        // 后来改用 QuickSearch 的表，同一站点关键字变了（ai → gpt），
        // 链接写法也常有出入（尾部多一个参数就判不出相同）。
        // 若不清理，用户列表里会出现两个 ChatGPT，看着很乱。
        //
        // 判据用"站点域名"而不是完整链接 —— 域名相同即视为同一站点。
        // 两条限制保证不误删：只删图标还是本程序生成的旧条目；用户改过图标的不动。
        var builtinDomains = new HashSet<string>(
            DefaultSettings.Create().Search
                .Where(e => !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
                .Select(e => DomainOf(e.Value))
                .Where(d => d.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var builtinKeys = new HashSet<string>(
            DefaultSettings.Create().Search.Select(e => e.Key),
            StringComparer.OrdinalIgnoreCase);

        int removedLegacy = s.Search.RemoveAll(e =>
            !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !builtinKeys.Contains(e.Key) &&
            IsOurGeneratedIcon(e.IconPath) &&
            builtinDomains.Contains(DomainOf(e.Value)));

        var builtin = DefaultSettings.Create().Search
            .Where(e => !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int added = 0, upgraded = 0;
        foreach (var engine in builtin)
        {
            var existing = s.Search.FirstOrDefault(
                e => e.Key.Equals(engine.Key, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                s.Search.Add(new QEntry
                {
                    Key = engine.Key,
                    Value = engine.Value,
                    Comment = engine.Comment,
                    IconPath = engine.IconPath,
                    EngineIcon = engine.EngineIcon,
                });
                added++;
                continue;
            }

            // 已有条目：把旧的图标路径换成内置图标。
            //
            // 为什么要换：早先版本把图标渲染成磁盘上的 .svg（%LocalAppData% 下），
            // 那些是程序自己画的简化图标；现在统一改用 QuickSearch 的 PNG 内置图标。
            // 只动图标，不碰用户改过的链接和备注。
            if (string.IsNullOrEmpty(existing.EngineIcon) && !string.IsNullOrEmpty(engine.EngineIcon))
            {
                existing.EngineIcon = engine.EngineIcon;
                // 旧的 IconPath 指向程序自己生成的 svg，清掉它才会优先用内置图标
                if (IsOurGeneratedIcon(existing.IconPath)) existing.IconPath = "";
                upgraded++;
            }
        }

        /* 还有两条老条目指着早先版本渲染出来的 svg（%LocalAppData%\CapsLockPlus\icons\gmail.svg、
         * yahoo.svg），那些文件随版本升级早就没了，Qbar 里一直是空白图标 —— 图标和站点对不上
         * 的投诉里就剩这两处。按站点补上内置图标，只清自家生成的路径。 */
        int legacyIconFixed = ReviveLegacyIconPaths(s.Search);
        if (legacyIconFixed > 0)
            CoreLog.Info($"旧图标路径清理：{legacyIconFixed} 个条目改用内置图标");

        // 补完新引擎再校图标：老关键字撞名后留下的"豆瓣配 DBLP 的图"这类错位，
        // 按站点域名改回正确的内置图标。
        int iconsAligned = AlignSearchIconsBySite(s.Search);
        if (iconsAligned > 0)
            CoreLog.Info($"搜索指令图标纠正：{iconsAligned} 个条目的图标与站点不符，已改回");

        s.BuiltinEnginesVersion = DefaultSettings.BuiltinEnginesVersion;
        if (added > 0 || upgraded > 0 || removedLegacy > 0 || deduped > 0)
            CoreLog.Info($"搜索引擎表更新：新增 {added} 个，图标升级 {upgraded} 个，" +
                         Loc.F("清理旧条目 {0} 个、重复站点 {1} 个（引擎表版本 → {2}）",
                               removedLegacy, deduped, s.BuiltinEnginesVersion));
        return true;
    }

    /// <summary>
    /// 补齐内置的“启动程序 / 常用网址”表。
    ///
    /// 与搜索引擎表不同，这两张表以前只有个位数条目，老用户的设置文件里
    /// 早就固化了，永远不会看到新加的条目。这里按 Key 去重追加：
    /// 用户自己加的、改过的同 Key 条目一律保留，不覆盖。
    /// </summary>
    private static bool MigrateBuiltinWebAndRun(AppSettings s)
    {
        s.Run ??= new List<QEntry>();
        s.Web ??= new List<QEntry>();

        bool changed = false;

        if (s.BuiltinRunVersion < DefaultSettings.BuiltinRunVersion)
        {
            /* 4.33 起默认不再填充启动程序表。
             * 老设置里由我们自动加进去的内置条目要清掉，但只删
             * Key + Value 完全匹配的内置项，用户自己改过路径的条目保留。 */
            int removed = RemoveBuiltinEntries(s.Run, DefaultSettings.BuiltinRunEntries());
            s.BuiltinRunVersion = DefaultSettings.BuiltinRunVersion;
            changed = true;
            if (removed > 0)
                CoreLog.Info($"启动程序表清理：移除 {removed} 个内置项（版本 → {s.BuiltinRunVersion}）");
        }

        if (s.BuiltinWebVersion < DefaultSettings.BuiltinWebVersion)
        {
            var builtinWeb = DefaultSettings.BuiltinWebEntries();
            int added = MergeBuiltinEntries(s.Web, builtinWeb);
            /* 老用户的常用网址条目早就固化了：老版本的内置项只有 Key / Value，
             * 没有 EngineIcon，Qbar 只能去抓站点 favicon —— 抓到的旧图、以及
             * 豆瓣 / 爱奇艺那种"图标和网站对不上"的缓存图就是这么来的。
             * 这里把内置图标补到已有条目上（只补空值），从此图标随程序走。 */
            int iconUpgraded = UpgradeBuiltinWebIcons(s.Web, builtinWeb);
            s.BuiltinWebVersion = DefaultSettings.BuiltinWebVersion;
            changed = true;
            if (added > 0)
                CoreLog.Info($"常用网址表更新：新增 {added} 个（版本 → {s.BuiltinWebVersion}）");
            if (iconUpgraded > 0)
                CoreLog.Info($"常用网址图标升级：{iconUpgraded} 个改用内置图标");
        }

        changed |= ResolveLegacyQbarKeyConflicts(s);

        return changed;
    }

    /// <summary>
    /// 清理历史上跨表重复的关键字。
    ///
    /// 旧版本的“常用网址”默认只有 gh / bili 两个短键，而搜索表里本来就有
    /// gh（GitHub 搜索）和 bili（B 站搜索）。新版把网址补全后，这两组会各剩
    /// 两个同名项，Qbar 只能按优先级选一个。这里把旧网址 gh 并到 github，
    /// 把旧搜索 bl（B 站）并到 bilis，保留语义更明确的键。
    /// </summary>
    private static bool ResolveLegacyQbarKeyConflicts(AppSettings s)
    {
        bool changed = false;

        var webGh = s.Web.FirstOrDefault(e => e.Key.Equals("gh", StringComparison.OrdinalIgnoreCase));
        if (webGh != null)
        {
            var webGithub = s.Web.FirstOrDefault(e => e.Key.Equals("github", StringComparison.OrdinalIgnoreCase));
            if (webGithub != null)
            {
                s.Web.Remove(webGh);
                CoreLog.Info("常用网址 gh 与 github 重复，移除旧 gh（保留 github）");
            }
            else
            {
                webGh.Key = "github";
                CoreLog.Info("常用网址 gh 已重命名为 github");
            }
            changed = true;
        }

        var bilis = s.Search.FirstOrDefault(e => e.Key.Equals("bilis", StringComparison.OrdinalIgnoreCase));
        var searchBili = s.Search.FirstOrDefault(e =>
            e.Key.Equals("bili", StringComparison.OrdinalIgnoreCase) &&
            e.Value.Contains("search.bilibili.com", StringComparison.OrdinalIgnoreCase));
        if (searchBili != null)
        {
            if (bilis != null)
            {
                s.Search.Remove(searchBili);
                CoreLog.Info("搜索指令 bili 与 bilis 重复，移除旧 bili（保留 bilis）");
            }
            else
            {
                searchBili.Key = "bilis";
                CoreLog.Info("搜索指令 bili 已重命名为 bilis");
            }
            changed = true;
        }

        var legacyBl = s.Search.FirstOrDefault(e =>
            e.Key.Equals("bl", StringComparison.OrdinalIgnoreCase) &&
            e.Value.Contains("search.bilibili.com", StringComparison.OrdinalIgnoreCase));
        if (legacyBl != null)
        {
            if (bilis != null)
            {
                s.Search.Remove(legacyBl);
                CoreLog.Info("搜索指令 bl 与 bilis 重复，移除旧 bl（保留 bilis）");
            }
            else
            {
                legacyBl.Key = "bilis";
                CoreLog.Info("搜索指令 bl 已重命名为 bilis");
            }
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 给已有的“常用网址”条目补上内置图标。
    ///
    /// 只补 EngineIcon 为空、且 Key + Value 都与内置项一致的条目：
    /// 用户把网址改成自己公司内网、或者换了站点，就不去塞一个不相干的图标。
    /// 用户自己填过 EngineIcon / IconPath 的一律不动。
    /// </summary>
    private static int UpgradeBuiltinWebIcons(List<QEntry> target, List<QEntry> builtins)
    {
        int n = 0;
        foreach (var builtin in builtins)
        {
            if (string.IsNullOrWhiteSpace(builtin.Key) || string.IsNullOrEmpty(builtin.EngineIcon)) continue;

            var existing = target.FirstOrDefault(
                e => e.Key.Equals(builtin.Key, StringComparison.OrdinalIgnoreCase));
            if (existing == null) continue;
            if (!string.IsNullOrEmpty(existing.EngineIcon)) continue;
            if (!string.Equals(existing.Value, builtin.Value, StringComparison.OrdinalIgnoreCase)) continue;

            existing.EngineIcon = builtin.EngineIcon;
            // 同搜索引擎表：程序早年生成的 svg 图标路径要清掉，否则会盖掉内置图标
            if (IsOurGeneratedIcon(existing.IconPath)) existing.IconPath = "";
            n++;
        }
        return n;
    }

    /// <summary>
    /// 清掉"指向已经不存在的 svg"的图标路径，并按站点补内置图标。
    ///
    /// 早先版本把图标渲染成 &lt;数据目录&gt;/icons/*.svg（那时数据目录还叫 CapsLockPlus），
    /// 升级后这些文件不再生成，配置里的路径就成了死引用：Qbar 取图返回 null，
    /// 条目只剩一个字。Gmail、Yahoo Japan 两条正是这么空着的。
    /// 判据是"路径以 .svg 结尾、文件确实不存在、父目录叫 icons" —— 自家生成的产物，
    /// 用户手填的 png/ico 路径不会被碰。
    /// </summary>
    private static int ReviveLegacyIconPaths(List<QEntry> entries)
    {
        int n = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.IconPath)) continue;
            if (!entry.IconPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(entry.IconPath)) continue;

            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(entry.IconPath)) ?? ""; }
            catch { continue; }
            if (!string.Equals(Path.GetFileName(dir), "icons", StringComparison.OrdinalIgnoreCase)) continue;

            if (string.IsNullOrEmpty(entry.EngineIcon))
            {
                string host = DomainOf(entry.Value);
                entry.EngineIcon = host switch
                {
                    "mail.google.com" or "gmail.com" => "gmail",
                    // Yahoo Japan 是红色 Y!，全球站是紫色 —— 两套标识不能混用
                    "yahoo.co.jp" or "search.yahoo.co.jp" => "yahoo_jp",
                    "yahoo.com" or "search.yahoo.com" => "yahoo",
                    _ => "",
                };
            }
            entry.IconPath = "";
            n++;
        }
        return n;
    }

    /// <summary>
    /// 合并"同一个站点、链接却重复"的搜索指令。
    ///
    /// 为什么会有重复：v1.3.0 换成 QuickSearch 的引擎表时复用了几个老关键字
    /// （ba / db / re / st / tw / wa / so / me），它们在老版本里指的是别的站点
    /// （百度备用 / 豆瓣 / Reddit / Stack Overflow / Twitter / WolframAlpha / 搜狗 / 韦氏词典）。
    /// 迁移按关键字合并，老条目保住自己的链接却套上了新站点的图标，同一站点还各多出一条，
    /// 于是列表里出现两个豆瓣、两个 Reddit，链接一模一样。
    ///
    /// 判据用"站点域名 + 路径"：www. / 大小写 / 结尾斜杠 / 查询串的差异不算不同站点。
    /// 只删"图标明显是别家"的那条；两条图标都对（用户自己另配的）时不动。
    /// </summary>
    private static int DedupeSearchBySite(List<QEntry> entries)
    {
        var icons = BuiltinIconBySite();

        // 内置表里每个关键字现在指向哪个站点：条目自己的站点跟它对不上，就是撞名留下的旧货
        var keySite = DefaultSettings.Create().Search
            .Where(e => !IsDefaultEngine(e))
            .GroupBy(e => e.Key ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => SearchSiteOf(g.First().Value), StringComparer.OrdinalIgnoreCase);

        int removed = 0;

        var groups = entries
            .Where(e => !IsDefaultEngine(e))
            .GroupBy(e => SearchSiteOf(e.Value), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            if (!icons.TryGetValue(group.Key, out string? canonical)) continue;
            if (string.IsNullOrEmpty(canonical)) continue;

            // 要留的那条：图标与站点相符，而且它的关键字在内置表里就是指这个站点。
            // 先挑这条，才不会把内置的 go 当成重复删掉、留下改名前的老键 gg。
            QEntry? keeper =
                group.FirstOrDefault(e => string.Equals(e.EngineIcon, canonical, StringComparison.OrdinalIgnoreCase)
                                          && keySite.TryGetValue(e.Key ?? "", out string? ownSite)
                                          && string.Equals(ownSite, group.Key, StringComparison.OrdinalIgnoreCase))
                ?? group.FirstOrDefault(e => string.Equals(e.EngineIcon, canonical, StringComparison.OrdinalIgnoreCase));
            if (keeper == null) continue;

            foreach (var duplicate in group.ToList())
            {
                if (ReferenceEquals(duplicate, keeper)) continue;

                // 关键字在内置表里指的是别的站点 = 撞名留下的旧条目。
                // 例：db 在内置表里是 DBLP，这条却写着豆瓣 —— 一定是老版本残留。
                bool hijacked = keySite.TryGetValue(duplicate.Key ?? "", out string? keySite_)
                                && keySite_.Length > 0
                                && !string.Equals(keySite_, group.Key, StringComparison.OrdinalIgnoreCase);

                // 关键字已经不在内置表里、图标是别家的、备注还写着站点名：同一批机器生成的
                bool stray = !keySite.ContainsKey(duplicate.Key ?? "")
                             && !string.Equals(duplicate.EngineIcon, canonical, StringComparison.OrdinalIgnoreCase)
                             && string.Equals((duplicate.Comment ?? "").Trim(),
                                              (keeper.Comment ?? "").Trim(), StringComparison.Ordinal);

                // 关键字不在内置表里，但备注和图标跟留下的那条一模一样：老版本改过名的残留（gg → go）
                bool renamedLeftover = !keySite.ContainsKey(duplicate.Key ?? "")
                                       && string.Equals(duplicate.EngineIcon, canonical, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals((duplicate.Comment ?? "").Trim(),
                                                        (keeper.Comment ?? "").Trim(), StringComparison.Ordinal);

                if (!hijacked && !stray && !renamedLeftover) continue;   // 用户自己另配的一条，保留

                entries.Remove(duplicate);
                removed++;
                CoreLog.Info($"搜索指令 {duplicate.Key}（{duplicate.Comment}）与 {keeper.Key} 是同一个站点，移除重复的一条");
            }
        }

        return removed;
    }

    /// <summary>
    /// 把图标与站点不符的搜索指令改回正确的内置图标。
    ///
    /// 典型症状：条目写着"豆瓣"、链接是 douban.com，图标却是 DBLP 的
    /// —— 老关键字撞名的后遗症。只有两种情况才改：
    ///   * 图标是空的；
    ///   * 图标属于别的站点（能查出来是谁的、且不是这个站点的）。
    /// 用户手填的图标文件、以及认不出来的图标一律不动。
    /// </summary>
    private static int AlignSearchIconsBySite(List<QEntry> entries)
    {
        var icons = BuiltinIconBySite();
        var iconDomains = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in DefaultSettings.Create().Search)
        {
            if (string.IsNullOrEmpty(b.EngineIcon)) continue;
            if (!iconDomains.TryGetValue(b.EngineIcon, out HashSet<string>? set))
                iconDomains[b.EngineIcon] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(DomainOf(b.Value));
        }

        int n = 0;
        foreach (var entry in entries)
        {
            if (IsDefaultEngine(entry)) continue;
            if (!icons.TryGetValue(SearchSiteOf(entry.Value), out string? canonical)) continue;
            if (string.IsNullOrEmpty(canonical)) continue;
            if (string.Equals(entry.EngineIcon, canonical, StringComparison.OrdinalIgnoreCase)) continue;
            // 用户自己指定了图标文件，按他的来
            if (!string.IsNullOrWhiteSpace(entry.IconPath) && !IsOurGeneratedIcon(entry.IconPath)) continue;

            string icon = entry.EngineIcon ?? "";
            bool blank = icon.Length == 0;
            bool foreign = !blank &&
                           iconDomains.TryGetValue(icon, out HashSet<string>? domains) &&
                           !domains.Contains(DomainOf(entry.Value));
            if (!blank && !foreign) continue;

            entry.EngineIcon = canonical;
            if (IsOurGeneratedIcon(entry.IconPath)) entry.IconPath = "";
            n++;
            CoreLog.Info($"搜索指令 {entry.Key}（{entry.Comment}）的图标与站点不符，改用 {canonical}");
        }

        return n;
    }

    /// <summary>内置引擎表：站点签名 → 该站点的内置图标（同一站点只留一个）。</summary>
    private static Dictionary<string, string> BuiltinIconBySite()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in DefaultSettings.Create().Search)
        {
            if (string.IsNullOrEmpty(b.EngineIcon)) continue;
            string site = SearchSiteOf(b.Value);
            if (site.Length == 0) continue;
            map.TryAdd(site, b.EngineIcon);
        }
        return map;
    }

    /// <summary>
    /// 站点签名：域名 + 路径，用来判断两条搜索链接是不是同一个站点的搜索。
    /// 忽略协议、www. 前缀、大小写、结尾斜杠和查询串；解析不出来时返回空串，
    /// 调用方据此跳过判定（宁可不合，也不误删）。
    /// </summary>
    internal static string SearchSiteOf(string url)
    {
        string host = DomainOf(url);
        if (host.Length == 0) return "";

        string path = "";
        try
        {
            string s = (url ?? "").Trim();
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s[(scheme + 3)..];
            int slash = s.IndexOf('/');
            if (slash >= 0) path = s[slash..];
            int cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) path = path[..cut];
            path = path.TrimEnd('/').ToLowerInvariant();
        }
        catch
        {
            path = "";
        }

        return host + (path.Length == 0 ? "/" : path);
    }

    private static bool IsDefaultEngine(QEntry e) =>
        e.Key != null && e.Key.Equals("default", StringComparison.OrdinalIgnoreCase);

    private static int MergeBuiltinEntries(List<QEntry> target, List<QEntry> builtins)
    {
        var keys = new HashSet<string>(
            target.Select(e => e.Key ?? "").Where(k => k.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        int added = 0;
        foreach (var entry in builtins)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || !keys.Add(entry.Key)) continue;
            target.Add(entry);
            added++;
        }
        return added;
    }

    /// <summary>
    /// 删除与内置项 Key + Value 完全一致的条目（忽略大小写）。
    /// 用户改过路径的条目 Value 不再匹配，会保留下来。
    /// </summary>
    private static int RemoveBuiltinEntries(List<QEntry> target, List<QEntry> builtins)
    {
        var keys = new HashSet<string>(
            builtins.Where(e => !string.IsNullOrWhiteSpace(e.Key))
                    .Select(e => (e.Key ?? "") + "\u0000" + (e.Value ?? "")),
            StringComparer.OrdinalIgnoreCase);

        int before = target.Count;
        target.RemoveAll(e => keys.Contains((e.Key ?? "") + "\u0000" + (e.Value ?? "")));
        return before - target.Count;
    }

    /// <summary>
    /// 取链接的站点域名（小写，去掉 www. 前缀），用于判断两个引擎是否同一站点。
    /// 解析不出来时返回空串，调用方据此跳过判定（宁可不删，也不误删）。
    /// </summary>
    private static string DomainOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            var s = url.Trim();
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s[(scheme + 3)..];
            int slash = s.IndexOfAny(new[] { '/', '?', '#' });
            if (slash >= 0) s = s[..slash];
            int colon = s.IndexOf(':');
            if (colon >= 0) s = s[..colon];
            s = s.ToLowerInvariant();
            if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
            return s;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 判断某个图标路径是否是"本程序早先生成到数据目录里的图标"。
    /// 这类路径可以直接替换成内置图标；用户自己指定的路径则不动。
    ///
    /// 判定放宽到"路径位于名为 icons 的目录下、且是 .svg"：
    /// 早先版本把图标渲染成 &lt;数据目录&gt;/icons/*.svg，是本程序自己生成的产物，
    /// 不存在用户手写这种路径的情况。放宽后即使用户改过数据目录位置也能识别。
    /// </summary>
    private static bool IsOurGeneratedIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return false;

            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir)) return false;

            var ours = Path.Combine(Path.GetFullPath(AppPaths.DataDir), "icons");
            if (string.Equals(dir.TrimEnd('\\'), ours.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;

            // 兼容数据目录被改过的情况：父目录名是 icons，且再上一层含 InputPlus
            if (string.Equals(Path.GetFileName(dir), "icons", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                return parent.Contains("InputPlus", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// "结构上什么都没有"：没有任何按键、层、触发键、宏、搜索/程序/网址条目。
    ///
    /// 用来识别被截断的设置文件 —— 正常保存一定至少写进 10 个层和 1 个触发键，
    /// 所以这种组合只可能来自外部破坏。单看某一项是不够的：
    /// 用户完全可能把按键全删光（那时层和触发键还在）。
    /// </summary>
    internal static bool IsStructurallyEmpty(AppSettings s) =>
        (s.Keys?.Count ?? 0) == 0
        && (s.Layers?.Count ?? 0) == 0
        && (s.Triggers?.Count ?? 0) == 0
        && (s.Macros?.Count ?? 0) == 0
        && (s.Search?.Count ?? 0) == 0
        && (s.Run?.Count ?? 0) == 0
        && (s.Web?.Count ?? 0) == 0
        && (s.HotStrings?.Count ?? 0) == 0;

    /// <summary>仅供自检：结构为空的判定规则。</summary>
    internal static bool LooksStructurallyEmptyForTest(AppSettings s) => IsStructurallyEmpty(s);

    /// <summary>补齐缺失字段、修正非法值。</summary>
    /// <summary>
    /// 文件搜索后端的旧字段迁移。
    ///
    /// v1.5.x 的配置里有两个布尔（键名由 QbarSearchOptions.LegacyWord 拼出来，
    /// 见那边的注释），默认都是 true。索引内核换成进程内 tantivy 之后它们再没有意义，
    /// 但**不能直接删掉读逻辑** —— 读过老配置的用户升级后设置页会显示一个
    /// 自己没选过的值。所以：
    ///   · 只要读到任意一个 false，就认定用户当初选的是"本地索引"，记成
    ///     <see cref="Core.Qbar.Search.QbarFileBackend.Native"/>；
    ///   · 其余情况（含两个字段都缺，即新配置）→ <see cref="Core.Qbar.Search.QbarFileBackend.Tantivy"/>；
    ///   · 迁移完把两个旧字段清成 null，写回时它们就消失了
    ///     （JsonOptions 的 DefaultIgnoreCondition = WhenWritingNull）。
    /// 两个枚举值都落到同一个进程内索引上，不存在第二条后端路径。
    /// </summary>
    private static void NormalizeQbarBackend(Core.Qbar.Search.QbarSearchOptions options)
    {
        bool sawLegacy = options.LegacyPreferBackend.HasValue || options.LegacyOnlyBackend.HasValue;
        if (sawLegacy)
        {
            bool local = options.LegacyPreferBackend == false || options.LegacyOnlyBackend == false;
            options.Backend = local
                ? Core.Qbar.Search.QbarFileBackend.Native
                : Core.Qbar.Search.QbarFileBackend.Tantivy;
        }
        options.LegacyPreferBackend = null;
        options.LegacyOnlyBackend = null;

        if (options.MaxCandidates < 200) options.MaxCandidates = 200;
        if (options.MaxCandidates > 20000) options.MaxCandidates = 20000;
    }

    public static void Normalize(AppSettings s, bool includeBaseLayerDefaults = true)
    {
        /* 先把对象图里的 null 洗掉，再谈"补齐/夹取"。
         *
         * 设置文件是纯文本：用户会手改，导入的配置来自另一台机器，
         * 同步下来的备份也可能被外部工具处理过 —— 这些来源都可能写出
         * `"Key": null`。而 `JsonSerializer` 对**非可空 string 属性照样赋 null**
         * （它不看可空性注解），于是后面 `x.Key.Equals(...)`、`e.Key.Contains(...)`
         * 这些在加载路径上的调用直接抛 NullReferenceException。
         *
         * 代价不是"某个功能失灵"：Load() 会走"读取失败 → 回退默认设置"分支，
         * 用户看到的是**一整份配置没了**（只在旁边留一个 settings.json.broken）。
         * 所以这里用一趟反射把"字符串补成空串、集合补成空集合、列表里的 null 元素删掉"，
         * 顺手也就覆盖了以后新加的字段（逐个手写必然漏）。 */
        SanitizeNulls(s);

        s.Global ??= new GlobalOptions();
        s.Qbar ??= new QbarOptions();
        s.Qbar.Pinned ??= new List<QEntry>();
        s.Qbar.Search ??= new Core.Qbar.Search.QbarSearchOptions();
        NormalizeQbarBackend(s.Qbar.Search);
        s.Sync ??= new SyncOptions();
        s.Keys ??= new List<KeyBinding>();
        s.Search ??= new List<QEntry>();
        s.Run ??= new List<QEntry>();
        s.Web ??= new List<QEntry>();
        s.HotStrings ??= new List<QEntry>();

        /* 别名替换的分组：去掉空白项、去重（忽略大小写），保持用户顺序。 */
        s.Global.SnippetGroups ??= new List<string>();
        var seenGroups = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        s.Global.SnippetGroups = s.Global.SnippetGroups
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Where(seenGroups.Add)
            .ToList();
        s.Triggers ??= new List<TriggerKey>();
        s.Layers ??= new List<LayerOptions>();
        s.Macros ??= new List<Macro>();
        s.Global.MouseSpeed = Math.Clamp(s.Global.MouseSpeed, 1, 20);
        // 步进允许 1~50：太小没感觉，太大一下到底
        s.Global.VolumeStep = Math.Clamp(s.Global.VolumeStep, 1, 50);
        s.Global.BrightnessStep = Math.Clamp(s.Global.BrightnessStep, 1, 50);
        /* 键盘档位：老配置没有这个字段（null），手改配置也可能写错。
         * 统一归一化成合法档位 —— 否则按键映射页算不出该用多大。 */
        s.Global.KeyboardSize = KeyboardSizePreset.Resolve(s.Global.KeyboardSize).Id;
        /* 令牌：手改配置时可能带上换行/前缀，读进来先洗一遍。
         * 洗成空串就是"没填"，所有判断都走匿名路径。 */
        s.Global.GitHubToken = Core.Update.UpdateService.NormalizeToken(s.Global.GitHubToken);
        s.Qbar.ListCount = Math.Clamp(s.Qbar.ListCount, 3, 12);
        /* 空面板的"最近打开"条数：0 = 不显示；上限和界面滑杆一致。
         * 老配置里没有这个字段时，反序列化会保留 QbarOptions 的默认值 5；
         * 这里只夹范围，绝不能把 0 当"没设置"改写成 5 —— 0 是用户主动选的合法值。 */
        s.Qbar.RecentCount = Math.Clamp(s.Qbar.RecentCount, 0, 20);

        /* 平滑滚动：老配置里没有这一段（null），手改也可能写出越界值。
         * 不夹的话 0 会造成"一格滚 0 像素"—— 表现是"滚轮彻底失灵"，
         * 而用户只会觉得是软件坏了。范围与界面滑杆的物理范围一致。 */
        s.Scroll ??= new ScrollOptions();
        s.Scroll.StepSize = Math.Clamp(s.Scroll.StepSize, 10, 1000);
        s.Scroll.AnimationTime = Math.Clamp(s.Scroll.AnimationTime, 40, 2000);
        s.Scroll.PulseScale = Math.Clamp(s.Scroll.PulseScale, Core.Scroll.ScrollMath.MinScale, Core.Scroll.ScrollMath.MaxScale);
        s.Scroll.AccelerationDelta = Math.Clamp(s.Scroll.AccelerationDelta, 5, 1000);
        s.Scroll.AccelerationMax = Math.Clamp(s.Scroll.AccelerationMax, 1, 20);
        s.ScrollApps ??= new List<ScrollAppRule>();
        s.ScrollApps.RemoveAll(r => r == null || string.IsNullOrWhiteSpace(r.AppPath));
        foreach (var rule in s.ScrollApps)
        {
            if (rule.StepSize is { } stepSize) rule.StepSize = Math.Clamp(stepSize, 10, 1000);
            if (rule.AnimationTime is { } animation) rule.AnimationTime = Math.Clamp(animation, 40, 2000);
        }
        /* 文件对话框浮层 / 搜索前建议（1.3.4 新增，1.3.5 调整默认值）。
         *
         * 1.3.4 把 ShowOnAppear 默认成了 false，结果用户打开「另存为」看不到浮层，
         * 以为功能没实现。1.3.5 起默认 true；老配置靠 DefaultsVersion 迁移一次，
         * 迁移后用户再手动关掉就不会被改回去。 */
        s.FileDialogBar ??= new FileDialogBarOptions();
        if (s.FileDialogBar.DefaultsVersion < FileDialogBarOptions.DefaultsVersionCurrent)
        {
            s.FileDialogBar.ShowOnAppear = true;
            s.FileDialogBar.DefaultsVersion = FileDialogBarOptions.DefaultsVersionCurrent;
        }
        s.FileDialogBar.MaxResults = Math.Clamp(s.FileDialogBar.MaxResults, 20, 500);
        s.FileDialogBar.RowLimit = Math.Clamp(s.FileDialogBar.RowLimit, 1, 9);
        s.FileDialogBar.BarHeight = Math.Clamp(s.FileDialogBar.BarHeight, 40, 48);
        s.FileDialogBar.PollFallbackMs = Math.Clamp(s.FileDialogBar.PollFallbackMs, 0, 2000);
        s.FileDialogBar.Types ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        s.FileDialogBar.DisabledProcesses ??= new List<string>();
        s.FileDialogBar.Favorites ??= new List<FavoriteDirectory>();
        s.FileDialogBar.Favorites.RemoveAll(f => f == null || string.IsNullOrWhiteSpace(f.Path));
        var seenFavorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        s.FileDialogBar.Favorites = s.FileDialogBar.Favorites
            .Where(f => seenFavorites.Add(f.Path.Trim()))
            .GroupBy(f => f.Path.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        foreach (var favorite in s.FileDialogBar.Favorites)
        {
            favorite.Path = favorite.Path.Trim();
            if (string.IsNullOrWhiteSpace(favorite.Name)) favorite.Name = Path.GetFileName(favorite.Path.TrimEnd('\\'));
        }

        s.Suggestions ??= new SuggestionsOptions();
        s.Suggestions.ShowCount = Math.Clamp(s.Suggestions.ShowCount, 0, 50);
        s.Suggestions.MaxCandidates = Math.Clamp(s.Suggestions.MaxCandidates, 200, 5000);
        s.Suggestions.CollectBudgetMs = Math.Clamp(s.Suggestions.CollectBudgetMs, 200, 5000);
        s.Suggestions.Exclusions ??= new SuggestionsExclusions();
        s.Suggestions.Exclusions.Files ??= new List<string>();
        s.Suggestions.Exclusions.Folders ??= new List<string>();
        s.Suggestions.Exclusions.FileExtensions ??= new List<string>();
        s.Suggestions.Sources ??= new List<string>();
        s.Suggestions.DisabledSources ??= new List<string>();
        s.Suggestions.FileTypeFilter ??= new List<string>();
        s.Suggestions.SourceOrder ??= new List<string>();

        /* 手势名称 / 提示条（v1.6.3）：v1.6.0 曾把两者合并成一套「通知样式」，
         * 用下来两边的需求并不一样（名称框跟着手势、提示条贴死右下角），所以又拆回两套。
         *
         * 搬运分两档，靠 GestureLabel.MigrateVersion 记方向：
         *   · 0 + 合并期那份 MigrateVersion ≥ 1 → v1.6.0 的用户：值搬给名称框，
         *     同时复制一份给提示条 —— 它当时吃的就是这套外观，不复制等于把用户调过的样子丢掉；
         *   · 0 + 合并期那份没动过 → v1.6.0 之前的配置，五个字段还散在 GestureOptions 上；
         *     那时的提示条走主题色、没有可调项，所以保持出厂值不动。
         * 搬完置 2：以后用户改了哪一份，都不会再被老字段压回去。 */
        s.GestureOptions ??= new GestureOptions();
        s.GestureLabel ??= new GestureLabelStyle();
        s.Toasts ??= new ToastStyle();
        if (s.GestureLabel.MigrateVersion < GestureLabelStyle.MigrateVersionCurrent)
        {
            var merged = s.Notifications;
            if (merged != null && merged.MigrateVersion >= NotificationStyle.MigrateVersionCurrent)
            {
                s.GestureLabel.ShowBorder = merged.ShowBorder;
                s.GestureLabel.BorderWidth = merged.BorderWidth;
                s.GestureLabel.Position = merged.Position;
                if (!string.IsNullOrWhiteSpace(merged.ColorNormal))
                    s.GestureLabel.ColorNormal = merged.ColorNormal;
                if (!string.IsNullOrWhiteSpace(merged.ColorExecuted))
                    s.GestureLabel.ColorExecuted = merged.ColorExecuted;

                /* 提示条只吃「平时」那一档：它没有「执行完」的第二种状态。 */
                s.Toasts.ShowBorder = merged.ShowBorder;
                s.Toasts.BorderWidth = merged.BorderWidth;
                if (!string.IsNullOrWhiteSpace(merged.ColorNormal))
                    s.Toasts.Color = merged.ColorNormal;
            }
            else
            {
                var legacyStyle = s.GestureOptions;
                s.GestureLabel.ShowBorder = legacyStyle.ShowLabelBorder;
                s.GestureLabel.BorderWidth = legacyStyle.LabelBorderWidth;
                if (!string.IsNullOrWhiteSpace(legacyStyle.LabelColorNormal))
                    s.GestureLabel.ColorNormal = legacyStyle.LabelColorNormal;
                if (!string.IsNullOrWhiteSpace(legacyStyle.LabelColorExecuted))
                    s.GestureLabel.ColorExecuted = legacyStyle.LabelColorExecuted;
                s.GestureLabel.Position = legacyStyle.GestureNamePos;
            }
            s.GestureLabel.MigrateVersion = GestureLabelStyle.MigrateVersionCurrent;
        }

        /* 合并期那份只读：搬完就丢掉。留着它，配置里会同时存在两代数据，
         * 用户手改配置时根本分不清该改哪一段（而且 Save 会把 null 省略，
         * 下次写文件时这一段自然消失）。 */
        s.Notifications = null;

        // 手改配置能写出越界值：描边 0 会让白边彻底消失（看起来像「设置没生效」），
        // 位置越界会把名称框钉在屏幕外。
        s.GestureLabel.BorderWidth = Math.Clamp(s.GestureLabel.BorderWidth, 0.5, 3);
        s.GestureLabel.Position = Math.Clamp(s.GestureLabel.Position, 0, 1);
        s.Toasts.BorderWidth = Math.Clamp(s.Toasts.BorderWidth, 0.5, 3);

        /* 提示条外观回退到 v1.6.0 之前（v1.0~v1.2 的旧观感）：
         * 老配置若三项都还是 v1.6.3 的出厂值（白边开 / 1.0 / #00000030），
         * 说明用户从没调过，搬回「跟随主题 + 不画可调白边 + 0.5」；
         * 只要动过其中任意一项就原样保留 —— 用户挑过的样子优先。
         * MigrateVersion 保证只搬一次，之后用户再调回同样的值也不会被压回去。 */
        if (s.Toasts.MigrateVersion < ToastStyle.MigrateVersionCurrent)
        {
            if (s.Toasts.ShowBorder
                && Math.Abs(s.Toasts.BorderWidth - 1.0) < 0.0001
                && string.Equals(s.Toasts.Color, Wg2Color.LabelColorNormal, StringComparison.OrdinalIgnoreCase))
            {
                s.Toasts.ShowBorder = false;
                s.Toasts.BorderWidth = 0.5;
                s.Toasts.Color = "";
            }
            s.Toasts.MigrateVersion = ToastStyle.MigrateVersionCurrent;
        }

        var labelStyleDefaults = GestureLabelStyle.Default;
        if (string.IsNullOrWhiteSpace(s.GestureLabel.ColorNormal))
            s.GestureLabel.ColorNormal = labelStyleDefaults.ColorNormal;
        if (string.IsNullOrWhiteSpace(s.GestureLabel.ColorExecuted))
            s.GestureLabel.ColorExecuted = labelStyleDefaults.ColorExecuted;
        if (string.IsNullOrWhiteSpace(s.Toasts.Color))
            s.Toasts.Color = ToastStyle.Default.Color;

        /* 已删除的动作：老配置里可能还绑着，统一改成"什么都不做"。
         *
         * 不这么做的话，按下去会提示"未知动作：translate" —— 用户不知道
         * 是软件坏了还是功能被删了。翻译功能整块移除，这里把它清干净。
         *
         * 窗口槽位（winbindActivate1~10 / winbindBind1~10 / bindWindowSlot）同理：
         * 槽位机制已经整块删除，但老配置里还留着这些 id。不清掉的话每按一次
         * 就写两条 WARN 日志（"未知动作" / "动作未执行"），实际行为却是什么都不做 ——
         * 用户看到的只有"这个键按了没反应"。 */
        int retiredActions = 0;
        foreach (var list in AllBindingLists(s))
        {
            foreach (var b in list)
            {
                if (string.IsNullOrEmpty(b.Action)) continue;

                bool retired =
                    string.Equals(b.Action, "translate", StringComparison.OrdinalIgnoreCase) ||
                    b.Action.StartsWith("winbindActivate", StringComparison.OrdinalIgnoreCase) ||
                    b.Action.StartsWith("winbindBind", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(b.Action, "bindWindowSlot", StringComparison.OrdinalIgnoreCase);

                if (!retired) continue;
                b.Action = "doNothing";
                b.Arg = "";
                retiredActions++;
            }
        }
        if (retiredActions > 0)
            CoreLog.Info($"[设置] 清理 {retiredActions} 条指向已移除动作的绑定");

        /* 翻译相关的搜索入口也一并清掉（v4.5.10）。
         *
         * 默认搜索列表里曾经有 5 个翻译站点（百度/搜狗/谷歌/必应词典 + DeepL），
         * 翻译功能整块移除后它们就没理由再出现。判据是**链接一字不差** ——
         * 用户改过链接说明他自己在用，绝不误删。 */
        string[] retiredSearchUrls =
        {
            "https://fanyi.baidu.com/#auto/zh/{q}",
            "https://fanyi.sogou.com/text?keyword={q}",
            "https://translate.google.com/?q={q}",
            "https://cn.bing.com/dict/search?q={q}",
            "https://www.deepl.com/translator#zh/en/{q}",
        };

        int retiredSearch = s.Search.RemoveAll(e =>
            !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            retiredSearchUrls.Contains(e.Value, StringComparer.OrdinalIgnoreCase));

        if (retiredSearch > 0)
            CoreLog.Info($"[设置] 移除 {retiredSearch} 个翻译搜索入口");

        /* 备份保留策略：老配置里没有这两个字段，反序列化后是 0。
         *
         * 0 在业务上是"不限制"，跟默认值 10 完全相反 ——
         * 不在这里补默认值，老用户一升级就会变成"永远不清"，备份越堆越多；
         * 而 0 份保留又会把刚写的备份自己删掉。两头都不能要，必须夹住。 */
        if (s.Sync.KeepLocalBackups <= 0) s.Sync.KeepLocalBackups = 10;
        s.Sync.KeepLocalBackups = Math.Clamp(s.Sync.KeepLocalBackups, 1, 200);
        if (s.Sync.CleanupDays < 0) s.Sync.CleanupDays = 0;
        s.Sync.CleanupDays = Math.Clamp(s.Sync.CleanupDays, 0, 3650);

        if (s.Sync.AutoBackupMinutes <= 0) s.Sync.AutoBackupMinutes = 60;
        s.Sync.AutoBackupMinutes = Math.Clamp(s.Sync.AutoBackupMinutes, 5, 1440);

        /* Qbar 面板底色：老配置里是写死的深色值（#1C1F27），
         * 导致浅色主题下"白字白底"看不见。
         *
         * 这里把**旧版的默认深色值**迁移成 auto（跟随主题）——
         * 只迁移这一个已知的旧默认值，用户自己选的颜色不动。 */
        if (string.Equals(s.Qbar.PanelColor, "#1C1F27", StringComparison.OrdinalIgnoreCase))
            s.Qbar.PanelColor = "auto";
        /* 布局方案只有两套，写错就退回新版；但**保留用户选过的经典方案**。
         *
         * 早先这里无条件写成 capslox，理由是"按键映射页不再提供切换"。
         * 可首次运行引导仍然会问用户选哪套 —— 选了经典、按了"恢复默认按键"，
         * 结果恢复出来是新版布局：界面问了一次、存了一次、读回来又改掉，
         * 用户看到的就是"我选的方案不生效"。
         *
         * 现在只做"非法值兜底"，合法值原样保留：
         *   · 引导页 → ApplyHotkeyScheme(选的方案) → 存进 settings；
         *   · 下次启动读回来仍是那套 → 「恢复默认按键」恢复的也是那一套。 */
        string scheme = (s.Global.HotkeyScheme ?? "").Trim().ToLowerInvariant();
        if (scheme != "capslock_plus") scheme = "capslox";
        s.Global.HotkeyScheme = scheme;

        NormalizeLayers(s);

        /* 设备方案也要"读回来就干净"：
         *   · 通用设置只允许一份（历史上可能被写成好几份）；
         *   · 空层、重复设备、非法层号在这里统一收掉；
         *   · 没写过 Profiles 的老设置完全不受影响（列表为空＝什么都不做）。 */
        DeviceProfiles.Normalize(s);

        /* 老 KeyRemap.Device 字段（4.20.7 之前写了但不生效）现在真的按设备分流了，
         * 读盘时把历史写法归一到运行时设备键，免得老规则静默失效。 */
        DeviceProfiles.NormalizeLegacyDeviceKeys(s);

        /* 基础层的 Caps+Q / Caps+R：默认方案里写的是 Qbar / Delete，但老配置里
         * 这两条可能被存成空绑定（doNothing）或被删过。读盘/手动归一化时补；
         * 导入部分分组时传 false，避免“文件里没有 Keys”被理解成要新增默认键。 */
        if (includeBaseLayerDefaults) EnsureBaseLayerDefaults(s);

        /* 默认表版本升级：老配置的 Caps/Alt 键位 → Capslox 3.0 语义。
         * 和上面补洞同一个门控：导入部分分组（false）时不动，
         * 因为那份文件并不代表用户的完整设置。幂等性见方法注释。 */
        if (includeBaseLayerDefaults) MigrateCapsloxDefaults(s);

        /* 别名前缀：老配置只存了一个 Prefix（可能还开着"全角"）。
         * 这里把列表物化一次，顺便把全角开关折算成字面字符 ——
         * 之后运行时只读 Prefixes，不必到处判断两个字段谁优先。 */
        s.Hotstring?.NormalizePrefixes();

        // 去掉重复的绑定项，保留最后一条
        var dup = s.Keys.GroupBy(k => k.Combo, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        foreach (var g in dup)
        {
            var last = g.Last();
            s.Keys.RemoveAll(k => string.Equals(k.Combo, last.Combo, StringComparison.OrdinalIgnoreCase));
            s.Keys.Add(last);
        }
    }

    /// <summary>
    /// 保证"层 / 触发键"这套新结构始终处于可用状态：
    ///   * 老设置文件没有 Triggers / Layers，这里补齐默认值；
    ///   * 老设置里的 Keys 视为第 1 层的绑定，行为完全不变；
    ///   * 默认 10 层，每层独立；只创建缺失的层，不覆盖用户改过的。
    /// </summary>
    /// <summary>
    /// 基础层的 Q / R 补默认动作（Qbar 面板 / Delete）。
    ///
    /// 只在"这条绑定不存在"或"它现在是 doNothing"时补 —— 用户明明把 Caps+R 改成
    /// 了别的动作，这里一动手就等于把他的设置吃掉。补过之后置标记，永不再跑。
    /// </summary>
    private static void EnsureBaseLayerDefaults(AppSettings s)
    {
        if (s.Global == null) return;
        /* 每次加载都补齐「缺失或仍是 doNothing」的默认项。
         * 不能因为 BaseLayerDefaultsFixed 就整段跳过：老配置里这个标记早就置位了，
         * 但 Q/R 可能仍是空绑定，用户会觉得“默认值没生效”。已有非空动作不覆盖。 */
        s.Global.BaseLayerDefaultsFixed = true;

        var keys = s.LayerKeys(1);
        if (keys == null) return;

        Fix("caps_q", "qbar");
        Fix("caps_r", "delete");

        void Fix(string combo, string action)
        {
            var b = keys.FirstOrDefault(x => string.Equals(x.Combo, combo, StringComparison.OrdinalIgnoreCase));
            if (b == null)
            {
                keys.Add(new KeyBinding { Combo = combo, Action = action });
                return;
            }
            if (string.IsNullOrWhiteSpace(b.Action) || b.Action == "doNothing")
            {
                b.Action = action;
                b.Arg = "";
            }
        }
    }

    /// <summary>
    /// 把老默认表升级到 Capslox 3.0 语义：<c>cx-shift-*</c>，以及 Alt 层"一次走 5 格"（老默认是 3 格）。
    ///
    /// ## 只改"还是老默认值"的那些键
    ///
    /// 判据是**二元组完全相等**：当前的 (Action, Arg) 等于老默认的 (Action, Arg)。
    /// 用户只要动过其中任何一个 —— 换动作、改次数、甚至只清空了参数 —— 这条就原样留着。
    /// 升级默认值绝不能变成"吃掉用户设置"，这是整段代码唯一的设计目标。
    ///
    /// 三条补充规则：
    ///   · 用户把这条绑定**删了** → 也不补回来（删掉是明确意图）；
    ///   · 新 combo（<c>caps_shift_v</c> 等）只在**完全不存在**时添加；
    ///   · 经典方案（<c>capslock_plus</c>）不是 Capslox 3.0 布局 → 只打版本号，一个字不改。
    ///
    /// 幂等：版本号是主闸门，二元组相等判断是第二道锁 —— 即使版本号被外部改回 0
    /// （手改设置文件、从老备份恢复），第二次跑也不会再把值挪一遍：
    /// 那时 <c>*5</c> 的当前值已经不是老默认的 <c>*3</c> 了。
    /// </summary>
    private static void MigrateCapsloxDefaults(AppSettings s)
    {
        if (s == null) return;
        if (s.CapsloxDefaultsVersion >= AppSettings.CapsloxDefaultsVersionCurrent) return;

        var keys = s.LayerKeys(1);
        if (keys == null || s.Global == null)
        {
            s.CapsloxDefaultsVersion = AppSettings.CapsloxDefaultsVersionCurrent;
            return;
        }

        /* 经典方案保持 3.0 之前的布局：升级只打版本号。 */
        if (string.Equals((s.Global.HotkeyScheme ?? "").Trim(), "capslock_plus", StringComparison.OrdinalIgnoreCase))
        {
            s.CapsloxDefaultsVersion = AppSettings.CapsloxDefaultsVersionCurrent;
            return;
        }

        KeyBinding? Find(string combo)
        {
            foreach (var k in keys)
            {
                if (k != null && string.Equals(k.Combo, combo, StringComparison.OrdinalIgnoreCase)) return k;
            }
            return null;
        }

        static bool Same(string? a, string b)
            => string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase);

        /* 「自定义按键」参数要的是 JSON，迁移时得把好读的 "end enter" 包一层。
         * 这个类里没有 DefaultSettings.Ck（那是另一个类的私有方法），就地再写一份。 */
        static string Ck(string keys) => new InputPlus.Core.Actions.CustomKeySpec
        {
            Key = InputPlus.Core.Actions.CustomKeyRunner.Canonical(keys),
        }.ToArg();

        void Upgrade(string combo, string oldAction, string oldArg, string newAction, string newArg)
        {
            var b = Find(combo);
            if (b == null) return;                                              // 用户删了 → 不补
            if (!Same(b.Action, oldAction) || !Same(b.Arg, oldArg)) return;      // 用户改过 → 不动
            b.Action = newAction;
            b.Arg = newArg;
        }

        void AddIfMissing(string combo, string action, string arg)
        {
            if (Find(combo) != null) return;
            keys.Add(new KeyBinding { Combo = combo, Action = action, Arg = arg });
        }

        // ---- 1) 老默认值 → Capslox 3.0 语义 ----

        // cx-enter = "end enter"（老默认是"任意位置回车"）
        Upgrade("caps_enter", "enterWherever", "", "customKey", Ck("end enter"));

        // cx-n / cx-alt-n = 选整行（老默认是往下选 10 / 30 行）
        Upgrade("caps_n", "selectDown", "10", "selectCurrentLine", "");
        Upgrade("caps_lalt_n", "selectDown", "30", "selectCurrentLine", "");

        // cx-alt-字母 = *5（老默认一律 *3）
        Upgrade("caps_lalt_a", "moveWordLeft", "3", "moveWordLeft", "5");
        Upgrade("caps_lalt_d", "moveDown", "3", "moveDown", "5");
        Upgrade("caps_lalt_e", "moveUp", "3", "moveUp", "5");
        Upgrade("caps_lalt_g", "moveWordRight", "3", "moveWordRight", "5");
        Upgrade("caps_lalt_h", "selectWordLeft", "3", "selectWordLeft", "5");
        Upgrade("caps_lalt_i", "selectUp", "3", "selectUp", "5");
        Upgrade("caps_lalt_k", "selectDown", "3", "selectDown", "5");
        Upgrade("caps_lalt_dot", "selectWordRight", "3", "selectWordRight", "5");

        // ---- 2) Capslox 3.0 才有的 combo：只在缺失时补 ----
        AddIfMissing("caps_shift_v", "paste1", "");
        AddIfMissing("caps_shift_lalt_v", "paste2", "");
        AddIfMissing("caps_shift_comma", "openSettings", "");
        AddIfMissing("caps_shift_slash", "openSettings", "");

        s.CapsloxDefaultsVersion = AppSettings.CapsloxDefaultsVersionCurrent;
    }

    private static void NormalizeLayers(AppSettings s)
    {
        const int DefaultLayerCount = 10;

        // 层
        if (s.Layers.Count == 0)
        {
            for (int i = 1; i <= DefaultLayerCount; i++)
            {
                s.Layers.Add(new LayerOptions
                {
                    Id = i,
                    Name = i == 1 ? "基础层" : Loc.F("第 {0} 层", i),
                    Enabled = true,
                    // 老用户的 Keys 就是第 1 层
                    Keys = i == 1 ? s.Keys : new List<KeyBinding>(),
                });
            }
        }
        else
        {
            // 补齐到 10 层（用户可能手动删过）
            for (int i = 1; i <= DefaultLayerCount; i++)
            {
                if (!s.Layers.Any(l => l.Id == i))
                    s.Layers.Add(new LayerOptions { Id = i, Name = Loc.F("第 {0} 层", i) });
            }
            s.Layers = s.Layers.OrderBy(l => l.Id).ToList();

            // 第 1 层若为空而 Keys 有内容，把 Keys 当作第 1 层
            var first = s.Layers.First(l => l.Id == 1);
            if (first.Keys.Count == 0 && s.Keys.Count > 0) first.Keys = s.Keys;
        }

        /* ⚠ `Settings.Keys` 必须**始终**是第 1 层那一份列表（同一个对象）。
         *
         * 引擎查第 1 层时先看 Layers[0].Keys，而界面/旧代码走 Settings.Keys；
         * 两者一旦不是同一个 List，就会出现"界面上改了但按键没生效"
         * 或者"第 5 层新建的绑定跑到第 1 层去"这类诡异问题。
         * 这里统一成同一份引用，从根上消除这种可能。 */
        {
            var first = s.Layers.First(l => l.Id == 1);
            if (first.Keys.Count == 0 && s.Keys.Count > 0) first.Keys = s.Keys;
            s.Keys = first.Keys;
        }

        // 触发键：老用户默认 CapsLock → 第 1 层，短按行为沿用全局设置
        if (s.Triggers.Count == 0)
        {
            s.Triggers.Add(new TriggerKey
            {
                Key = "capslock",
                LayerId = 1,
                HoldMs = 200,
                TapAction = s.Global.TapCapsAction,
                DoubleTapAction = s.Global.DoubleTapCaps ? s.Global.DoubleTapCapsAction : "",
                Enabled = true,
            });
        }
        else
        {
            foreach (var t in s.Triggers)
            {
                if (t.LayerId < 1 || t.LayerId > s.Layers.Count) t.LayerId = 1;
                t.HoldMs = Math.Clamp(t.HoldMs, 50, 2000);
            }
        }
    }
}

/// <summary>默认设置：完整复刻上游 CapsLock+ 的 capslox 布局。</summary>
public static class DefaultSettings
{
    /// <summary>
    /// 内置搜索引擎表的版本号。新增内置引擎时 +1，老用户启动时会自动补齐缺失项。
    ///   1 = 最初的 10 个引擎
    ///   2 = 扩充到 29 个（国内用拼音首字母、国外用英文前两字母，全部带图标）
    ///   3 = 再补 18 个（Yahoo / DuckDuckGo / WolframAlpha / Google 图片·地图 /
    ///       Netflix / Gmail / Drive / YT Music / Gist / 搜狗 / Ecosia / Brave /
    ///       Spotify / 词典 / ChatGPT），共 47 个
    ///   4 = 改用 QuickSearch（ui/data/engines.json）的完整引擎表，共 304 个，
    ///       图标全部换为其内置 PNG
    ///   5 = 旧条目改用内置图标（替换掉磁盘上的 svg 图标路径）
    ///   6 = B 站搜索关键字 bili 改为 bilis，bili 留给常用网址首页
    ///   7 = 清掉指向已删除 svg 的死图标路径，按站点补内置图标
    ///   8 = 合并同一站点的重复条目（老关键字撞名留下的豆瓣 / Reddit / 搜狗 …），
    ///       并把图标改回与站点相符的那个
    /// </summary>
    public const int BuiltinEnginesVersion = 9;

    /// <summary>
    /// 3 = 内置网址条目补 EngineIcon（图标随程序走，不再依赖站点 favicon 缓存）
    /// </summary>
    public const int BuiltinWebVersion = 3;

    /// <summary>
    /// 内置常用网址表。新增时把 <see cref="BuiltinWebVersion"/> +1，
    /// 老用户启动时会按 Key 去重补齐（不覆盖用户改过的条目）。
    /// </summary>
    public static List<QEntry> BuiltinWebEntries() => new()
    {
new() { Key = "github", Value = "https://github.com", Comment = "GitHub", EngineIcon = "github" },
            new() { Key = "gitlab", Value = "https://gitlab.com", Comment = "GitLab", EngineIcon = "gitlab" },
            new() { Key = "giteecom", Value = "https://gitee.com", Comment = "Gitee", EngineIcon = "gitee" },
            new() { Key = "stackoverflow", Value = "https://stackoverflow.com", Comment = "Stack Overflow", EngineIcon = "stackoverflow_gu_ge" },
            new() { Key = "csdnnet", Value = "https://www.csdn.net", Comment = "CSDN", EngineIcon = "csdn" },
            new() { Key = "juejinim", Value = "https://juejin.cn", Comment = "稀土掘金", EngineIcon = "jue_jin" },
            new() { Key = "segmentfault", Value = "https://segmentfault.com", Comment = "SegmentFault", EngineIcon = "segmentfault" },
            new() { Key = "v2ex", Value = "https://www.v2ex.com", Comment = "V2EX", EngineIcon = "v2ex_gu_ge" },
            new() { Key = "sspaicom", Value = "https://sspai.com", Comment = "少数派", EngineIcon = "shao_shu_pai" },
            new() { Key = "36kr", Value = "https://36kr.com", Comment = "36氪", EngineIcon = "36kr" },
            new() { Key = "huxiu", Value = "https://www.huxiu.com", Comment = "虎嗅", EngineIcon = "hu_xiu" },
            new() { Key = "ifanr", Value = "https://www.ifanr.com", Comment = "爱范儿", EngineIcon = "ifanr" },
            new() { Key = "thepaper", Value = "https://www.thepaper.cn", Comment = "澎湃新闻", EngineIcon = "peng_pai_xin_wen" },
            new() { Key = "zhihu", Value = "https://www.zhihu.com", Comment = "知乎", EngineIcon = "zhi_hu" },
            new() { Key = "douban", Value = "https://www.douban.com", Comment = "豆瓣", EngineIcon = "dou_ban" },
            new() { Key = "baike", Value = "https://baike.baidu.com", Comment = "百度百科", EngineIcon = "bai_du_bai_ke" },
            new() { Key = "wikipedia", Value = "https://zh.wikipedia.org", Comment = "维基百科", EngineIcon = "wei_ji_bai_ke" },
            new() { Key = "bilibili", Value = "https://www.bilibili.com", Comment = "哔哩哔哩", EngineIcon = "bi_li_bi_li" },
            // bili 同时是搜索指令表里的 B 站搜索；网址表优先，
            // 输入 bili + 空格直接得到首页地址，而不是“B 站搜索”标签。
            new() { Key = "bili", Value = "https://www.bilibili.com", Comment = "哔哩哔哩", EngineIcon = "bi_li_bi_li" },
            new() { Key = "youku", Value = "https://www.youku.com", Comment = "优酷", EngineIcon = "you_ku" },
            new() { Key = "iqiyicom", Value = "https://www.iqiyi.com", Comment = "爱奇艺", EngineIcon = "ai_qi_yi" },
            new() { Key = "tencentvideo", Value = "https://v.qq.com", Comment = "腾讯视频", EngineIcon = "teng_xun_shi_pin" },
            new() { Key = "mgtv", Value = "https://www.mgtv.com", Comment = "芒果 TV", EngineIcon = "mang_guo_tv" },
            new() { Key = "netflix", Value = "https://www.netflix.com", Comment = "Netflix", EngineIcon = "netflix" },
            new() { Key = "disneyplus", Value = "https://www.disneyplus.com", Comment = "Disney+", EngineIcon = "disneyplus" },
            new() { Key = "primevideo", Value = "https://www.primevideo.com", Comment = "Prime Video", EngineIcon = "primevideo" },
            new() { Key = "youtube", Value = "https://www.youtube.com", Comment = "YouTube", EngineIcon = "youtube" },
            new() { Key = "twitch", Value = "https://www.twitch.tv", Comment = "Twitch", EngineIcon = "twitch" },
            new() { Key = "spotifyweb", Value = "https://open.spotify.com", Comment = "Spotify", EngineIcon = "spotify" },
            new() { Key = "wyy", Value = "https://music.163.com", Comment = "网易云音乐", EngineIcon = "wang_yi_yun_yin_yue" },
            new() { Key = "qqmusic", Value = "https://y.qq.com", Comment = "QQ 音乐", EngineIcon = "qq_yin_yue" },
            new() { Key = "kugou", Value = "https://www.kugou.com", Comment = "酷狗音乐", EngineIcon = "kugou" },
            new() { Key = "kuwo", Value = "https://www.kuwo.cn", Comment = "酷我音乐", EngineIcon = "ku_wo_yin_yue" },
            new() { Key = "ximalaya", Value = "https://www.ximalaya.com", Comment = "喜马拉雅", EngineIcon = "ximalaya" },
            new() { Key = "weread", Value = "https://weread.qq.com", Comment = "微信读书", EngineIcon = "wei_xin_du_shu" },
            new() { Key = "taobao", Value = "https://www.taobao.com", Comment = "淘宝", EngineIcon = "tao_bao" },
            new() { Key = "tmall", Value = "https://www.tmall.com", Comment = "天猫", EngineIcon = "tian_mao" },
            new() { Key = "jdcom", Value = "https://www.jd.com", Comment = "京东", EngineIcon = "jing_dong" },
            new() { Key = "pinduoduo", Value = "https://www.pinduoduo.com", Comment = "拼多多", EngineIcon = "pin_duo_duo" },
            new() { Key = "suning", Value = "https://www.suning.com", Comment = "苏宁易购", EngineIcon = "su_ning_yi_gou" },
            new() { Key = "vipcom", Value = "https://www.vip.com", Comment = "唯品会", EngineIcon = "wei_pin_hui" },
            new() { Key = "amazon", Value = "https://www.amazon.com", Comment = "Amazon", EngineIcon = "ya_ma_xun" },
            new() { Key = "ebay", Value = "https://www.ebay.com", Comment = "eBay", EngineIcon = "ebay" },
            new() { Key = "aliexpress", Value = "https://www.aliexpress.com", Comment = "AliExpress", EngineIcon = "aliexpress" },
            new() { Key = "1688", Value = "https://www.1688.com", Comment = "1688", EngineIcon = "1688" },
            new() { Key = "booking", Value = "https://www.booking.com", Comment = "Booking.com", EngineIcon = "booking" },
            new() { Key = "airbnb", Value = "https://www.airbnb.com", Comment = "Airbnb", EngineIcon = "airbnb" },
            new() { Key = "ctrip", Value = "https://www.ctrip.com", Comment = "携程", EngineIcon = "ctrip" },
            new() { Key = "12306", Value = "https://www.12306.cn", Comment = "12306", EngineIcon = "12306" },
            new() { Key = "didi", Value = "https://www.didiglobal.com", Comment = "滴滴", EngineIcon = "didi" },
            new() { Key = "amap", Value = "https://www.amap.com", Comment = "高德地图", EngineIcon = "gao_de_di_tu" },
            new() { Key = "baidumap", Value = "https://map.baidu.com", Comment = "百度地图", EngineIcon = "bai_du_di_tu" },
            new() { Key = "weibo", Value = "https://weibo.com", Comment = "微博", EngineIcon = "wei_bo" },
            new() { Key = "xiaohongshu", Value = "https://www.xiaohongshu.com", Comment = "小红书", EngineIcon = "xiao_hong_shu" },
            new() { Key = "douyin", Value = "https://www.douyin.com", Comment = "抖音", EngineIcon = "dou_yin" },
            new() { Key = "kuaishou", Value = "https://www.kuaishou.com", Comment = "快手", EngineIcon = "kuai_shou" },
            new() { Key = "wechatweb", Value = "https://weixin.qq.com", Comment = "微信", EngineIcon = "wechat" },
            new() { Key = "qqcom", Value = "https://www.qq.com", Comment = "腾讯网", EngineIcon = "qqcom" },
            new() { Key = "telegramweb", Value = "https://telegram.org", Comment = "Telegram", EngineIcon = "telegram" },
            new() { Key = "discordweb", Value = "https://discord.com", Comment = "Discord", EngineIcon = "discord" },
            new() { Key = "twitter", Value = "https://twitter.com", Comment = "Twitter / X", EngineIcon = "twitter" },
            new() { Key = "xcom", Value = "https://x.com", Comment = "X", EngineIcon = "twitter" },
            new() { Key = "reddit", Value = "https://www.reddit.com", Comment = "Reddit", EngineIcon = "reddit" },
            new() { Key = "facebook", Value = "https://www.facebook.com", Comment = "Facebook", EngineIcon = "facebook" },
            new() { Key = "instagram", Value = "https://www.instagram.com", Comment = "Instagram", EngineIcon = "instagram" },
            new() { Key = "linkedin", Value = "https://www.linkedin.com", Comment = "LinkedIn", EngineIcon = "linkedin" },
            new() { Key = "tiktok", Value = "https://www.tiktok.com", Comment = "TikTok", EngineIcon = "tiktok" },
            new() { Key = "medium", Value = "https://medium.com", Comment = "Medium", EngineIcon = "medium" },
            new() { Key = "notion", Value = "https://www.notion.so", Comment = "Notion", EngineIcon = "notion" },
            new() { Key = "figma", Value = "https://www.figma.com", Comment = "Figma", EngineIcon = "figma" },
            new() { Key = "canva", Value = "https://www.canva.com", Comment = "Canva", EngineIcon = "canva" },
            new() { Key = "miro", Value = "https://miro.com", Comment = "Miro", EngineIcon = "miro" },
            new() { Key = "trello", Value = "https://trello.com", Comment = "Trello", EngineIcon = "trello" },
            new() { Key = "asana", Value = "https://asana.com", Comment = "Asana", EngineIcon = "asana" },
            new() { Key = "slackweb", Value = "https://slack.com", Comment = "Slack", EngineIcon = "slack" },
            new() { Key = "zoomweb", Value = "https://zoom.us", Comment = "Zoom", EngineIcon = "zoom" },
            new() { Key = "teamsweb", Value = "https://www.microsoft.com/microsoft-teams", Comment = "Microsoft Teams", EngineIcon = "teams" },
            new() { Key = "google", Value = "https://www.google.com", Comment = "Google", EngineIcon = "google" },
            new() { Key = "baidu", Value = "https://www.baidu.com", Comment = "百度", EngineIcon = "bai_du" },
            new() { Key = "bing", Value = "https://www.bing.com", Comment = "Bing", EngineIcon = "bing" },
            new() { Key = "duckduckgo", Value = "https://duckduckgo.com", Comment = "DuckDuckGo", EngineIcon = "duckduckgo" },
            new() { Key = "yandex", Value = "https://yandex.com", Comment = "Yandex", EngineIcon = "yandex" },
            new() { Key = "naver", Value = "https://www.naver.com", Comment = "Naver", EngineIcon = "naver" },
            new() { Key = "yahoo", Value = "https://www.yahoo.com", Comment = "Yahoo", EngineIcon = "yahoo" },
            new() { Key = "office", Value = "https://www.office.com", Comment = "Microsoft 365", EngineIcon = "office" },
            new() { Key = "microsoft", Value = "https://www.microsoft.com", Comment = "Microsoft", EngineIcon = "microsoft" },
            new() { Key = "apple", Value = "https://www.apple.com", Comment = "Apple", EngineIcon = "apple" },
            new() { Key = "windows", Value = "https://www.microsoft.com/windows", Comment = "Windows", EngineIcon = "windows" },
            new() { Key = "azure", Value = "https://azure.microsoft.com", Comment = "Azure", EngineIcon = "azure" },
            new() { Key = "aws", Value = "https://aws.amazon.com", Comment = "AWS", EngineIcon = "aws" },
            new() { Key = "aliyuncom", Value = "https://www.aliyun.com", Comment = "阿里云", EngineIcon = "aliyun" },
            new() { Key = "tencentcloud", Value = "https://cloud.tencent.com", Comment = "腾讯云", EngineIcon = "tencentcloud" },
            new() { Key = "huaweicloud", Value = "https://www.huaweicloud.com", Comment = "华为云", EngineIcon = "huaweicloud" },
            new() { Key = "vercel", Value = "https://vercel.com", Comment = "Vercel", EngineIcon = "vercel" },
            new() { Key = "netlify", Value = "https://www.netlify.com", Comment = "Netlify", EngineIcon = "netlify" },
            new() { Key = "cloudflare", Value = "https://www.cloudflare.com", Comment = "Cloudflare", EngineIcon = "cloudflare" },
            new() { Key = "npm", Value = "https://www.npmjs.com", Comment = "npm", EngineIcon = "npm" },
            new() { Key = "nuget", Value = "https://www.nuget.org", Comment = "NuGet", EngineIcon = "nuget" },
            new() { Key = "dockerhub", Value = "https://hub.docker.com", Comment = "Docker Hub", EngineIcon = "docker_hub" },
            new() { Key = "huggingface", Value = "https://huggingface.co", Comment = "Hugging Face", EngineIcon = "hugging_face" },
            new() { Key = "modelscope", Value = "https://www.modelscope.cn", Comment = "ModelScope", EngineIcon = "modelscope" },
            new() { Key = "kaggle", Value = "https://www.kaggle.com", Comment = "Kaggle", EngineIcon = "kaggle" },
            new() { Key = "leetcodecn", Value = "https://leetcode.cn", Comment = "力扣", EngineIcon = "leetcode" },
            new() { Key = "nowcoder", Value = "https://www.nowcoder.com", Comment = "牛客", EngineIcon = "nowcoder" },
            new() { Key = "luogu", Value = "https://www.luogu.com.cn", Comment = "洛谷", EngineIcon = "luogu" },
            new() { Key = "codeforces", Value = "https://codeforces.com", Comment = "Codeforces", EngineIcon = "codeforces" },
            new() { Key = "coursera", Value = "https://www.coursera.org", Comment = "Coursera", EngineIcon = "coursera" },
            new() { Key = "udemy", Value = "https://www.udemy.com", Comment = "Udemy", EngineIcon = "udemy" },
            new() { Key = "bbc", Value = "https://www.bbc.com", Comment = "BBC", EngineIcon = "bbc" },
            new() { Key = "cnn", Value = "https://www.cnn.com", Comment = "CNN", EngineIcon = "cnn" },
            new() { Key = "reuters", Value = "https://www.reuters.com", Comment = "Reuters", EngineIcon = "lu_tou_she" },
            new() { Key = "nytimes", Value = "https://www.nytimes.com", Comment = "纽约时报", EngineIcon = "nytimes" },
            new() { Key = "theguardian", Value = "https://www.theguardian.com", Comment = "The Guardian", EngineIcon = "theguardian" },
            new() { Key = "economist", Value = "https://www.economist.com", Comment = "经济学人", EngineIcon = "economist" },
            new() { Key = "bloomberg", Value = "https://www.bloomberg.com", Comment = "Bloomberg", EngineIcon = "bloomberg" },
            new() { Key = "forbes", Value = "https://www.forbes.com", Comment = "Forbes", EngineIcon = "forbes" },
            new() { Key = "hbr", Value = "https://hbr.org", Comment = "哈佛商业评论", EngineIcon = "hbr" },
            new() { Key = "mit", Value = "https://www.mit.edu", Comment = "MIT", EngineIcon = "mit" },
            new() { Key = "stanford", Value = "https://www.stanford.edu", Comment = "Stanford", EngineIcon = "stanford" },
            new() { Key = "harvard", Value = "https://www.harvard.edu", Comment = "Harvard", EngineIcon = "harvard" },
            new() { Key = "arxiv", Value = "https://arxiv.org", Comment = "arXiv", EngineIcon = "arxiv" },
            new() { Key = "claudeai", Value = "https://claude.ai", Comment = "Claude", EngineIcon = "claude" },
            new() { Key = "deepseekchat", Value = "https://chat.deepseek.com", Comment = "DeepSeek", EngineIcon = "deepseek" },
            new() { Key = "geminiapp", Value = "https://gemini.google.com", Comment = "Gemini", EngineIcon = "gemini" },
            new() { Key = "pplx", Value = "https://www.perplexity.ai", Comment = "Perplexity", EngineIcon = "perplexity_ai" },
            new() { Key = "kimichat", Value = "https://kimi.moonshot.cn", Comment = "Kimi", EngineIcon = "kimi" },
            new() { Key = "doubaocom", Value = "https://www.doubao.com", Comment = "豆包", EngineIcon = "dou_bao" },
            new() { Key = "chatgpt", Value = "https://chatgpt.com", Comment = "ChatGPT", EngineIcon = "chatgpt" },
    };

    public const int BuiltinRunVersion = 2;

    /// <summary>
    /// 内置启动程序表。只写可执行文件名/协议，不写死安装路径 ——
    /// 具体装在哪由 App Paths / PATH / 系统协议决定，用户也可自行改成绝对路径。
    /// </summary>
    public static List<QEntry> BuiltinRunEntries() => new()
    {
new() { Key = "notepad", Value = "notepad.exe", Comment = "记事本" },
            new() { Key = "calc", Value = "calc.exe", Comment = "计算器" },
            new() { Key = "mspaint", Value = "mspaint.exe", Comment = "画图" },
            new() { Key = "explorer", Value = "explorer.exe", Comment = "文件资源管理器" },
            new() { Key = "cmd", Value = "cmd.exe", Comment = "命令提示符" },
            new() { Key = "taskmgr", Value = "taskmgr.exe", Comment = "任务管理器" },
            new() { Key = "powershell", Value = "powershell.exe", Comment = "PowerShell" },
            new() { Key = "wt", Value = "wt.exe", Comment = "Windows Terminal" },
            new() { Key = "regedit", Value = "regedit.exe", Comment = "注册表编辑器" },
            new() { Key = "control", Value = "control.exe", Comment = "控制面板" },
            new() { Key = "services", Value = "services.msc", Comment = "服务" },
            new() { Key = "devmgmt", Value = "devmgmt.msc", Comment = "设备管理器" },
            new() { Key = "diskmgmt", Value = "diskmgmt.msc", Comment = "磁盘管理" },
            new() { Key = "eventvwr", Value = "eventvwr.msc", Comment = "事件查看器" },
            new() { Key = "mstsc", Value = "mstsc.exe", Comment = "远程桌面" },
            new() { Key = "snippingtool", Value = "snippingtool.exe", Comment = "截图工具" },
            new() { Key = "wsl", Value = "wsl.exe", Comment = "WSL" },
            new() { Key = "settings", Value = "ms-settings:", Comment = "Windows 设置" },
            new() { Key = "store", Value = "ms-windows-store:", Comment = "Microsoft Store" },
            new() { Key = "camera", Value = "microsoft.windows.camera:", Comment = "相机" },
            new() { Key = "photos", Value = "ms-photos:", Comment = "照片" },
            new() { Key = "code", Value = "Code.exe", Comment = "Visual Studio Code" },
            new() { Key = "cursor", Value = "Cursor.exe", Comment = "Cursor" },
            new() { Key = "sublime", Value = "sublime_text.exe", Comment = "Sublime Text" },
            new() { Key = "notepad3", Value = "Notepad3.exe", Comment = "Notepad3" },
            new() { Key = "typora", Value = "Typora.exe", Comment = "Typora" },
            new() { Key = "obsidian", Value = "Obsidian.exe", Comment = "Obsidian" },
            new() { Key = "logseq", Value = "Logseq.exe", Comment = "Logseq" },
            new() { Key = "eudic", Value = "eudic.exe", Comment = "欧路词典" },
            new() { Key = "youdao", Value = "YoudaoDict.exe", Comment = "有道词典" },
            new() { Key = "wechat", Value = "WeChat.exe", Comment = "微信" },
            new() { Key = "qqapp", Value = "QQ.exe", Comment = "QQ" },
            new() { Key = "dingtalk", Value = "DingTalk.exe", Comment = "钉钉" },
            new() { Key = "feishu", Value = "Feishu.exe", Comment = "飞书" },
            new() { Key = "telegram", Value = "Telegram.exe", Comment = "Telegram" },
            new() { Key = "discord", Value = "Discord.exe", Comment = "Discord" },
            new() { Key = "slack", Value = "slack.exe", Comment = "Slack" },
            new() { Key = "zoom", Value = "Zoom.exe", Comment = "Zoom" },
            new() { Key = "teams", Value = "msteams:", Comment = "Microsoft Teams" },
            new() { Key = "edge", Value = "msedge.exe", Comment = "Microsoft Edge" },
            new() { Key = "chrome", Value = "chrome.exe", Comment = "Google Chrome" },
            new() { Key = "firefox", Value = "firefox.exe", Comment = "Firefox" },
            new() { Key = "brave", Value = "brave.exe", Comment = "Brave" },
            new() { Key = "vlc", Value = "vlc.exe", Comment = "VLC" },
            new() { Key = "potplayer", Value = "PotPlayerMini64.exe", Comment = "PotPlayer" },
            new() { Key = "mpv", Value = "mpv.exe", Comment = "mpv" },
            new() { Key = "spotify", Value = "Spotify.exe", Comment = "Spotify" },
            new() { Key = "steam", Value = "steam.exe", Comment = "Steam" },
            new() { Key = "epic", Value = "EpicGamesLauncher.exe", Comment = "Epic Games" },
            new() { Key = "powertoys", Value = "PowerToys.exe", Comment = "PowerToys" },
            new() { Key = "snipaste", Value = "Snipaste.exe", Comment = "Snipaste" },
            new() { Key = "sharex", Value = "ShareX.exe", Comment = "ShareX" },
            new() { Key = "7zip", Value = "7zFM.exe", Comment = "7-Zip" },
            new() { Key = "bandizip", Value = "Bandizip.exe", Comment = "Bandizip" },
            new() { Key = "winrar", Value = "WinRAR.exe", Comment = "WinRAR" },
            new() { Key = "wiztree", Value = "WizTree.exe", Comment = "WizTree" },
            new() { Key = "hwinfo", Value = "HWiNFO64.exe", Comment = "HWiNFO" },
            new() { Key = "cpuz", Value = "cpuz_x64.exe", Comment = "CPU-Z" },
            new() { Key = "gpuz", Value = "GPU-Z.exe", Comment = "GPU-Z" },
            new() { Key = "putty", Value = "putty.exe", Comment = "PuTTY" },
            new() { Key = "xshell", Value = "Xshell.exe", Comment = "Xshell" },
            new() { Key = "mobaxterm", Value = "MobaXterm.exe", Comment = "MobaXterm" },
            new() { Key = "finalshell", Value = "FinalShell.exe", Comment = "FinalShell" },
            new() { Key = "winscp", Value = "WinSCP.exe", Comment = "WinSCP" },
            new() { Key = "filezilla", Value = "filezilla.exe", Comment = "FileZilla" },
            new() { Key = "wireshark", Value = "Wireshark.exe", Comment = "Wireshark" },
            new() { Key = "fiddler", Value = "Fiddler.exe", Comment = "Fiddler" },
            new() { Key = "postman", Value = "Postman.exe", Comment = "Postman" },
            new() { Key = "apifox", Value = "Apifox.exe", Comment = "Apifox" },
            new() { Key = "burp", Value = "BurpSuiteCommunity.exe", Comment = "Burp Suite" },
            new() { Key = "navicat", Value = "Navicat.exe", Comment = "Navicat" },
            new() { Key = "dbeaver", Value = "dbeaver.exe", Comment = "DBeaver" },
            new() { Key = "redis", Value = "redis-cli.exe", Comment = "redis-cli" },
            new() { Key = "mysql", Value = "mysql.exe", Comment = "MySQL" },
            new() { Key = "gitbash", Value = "git-bash.exe", Comment = "Git Bash" },
            new() { Key = "dockerdesktop", Value = "Docker Desktop.exe", Comment = "Docker Desktop" },
            new() { Key = "idea", Value = "idea64.exe", Comment = "IntelliJ IDEA" },
            new() { Key = "pycharm", Value = "pycharm64.exe", Comment = "PyCharm" },
            new() { Key = "webstorm", Value = "webstorm64.exe", Comment = "WebStorm" },
            new() { Key = "goland", Value = "goland64.exe", Comment = "GoLand" },
            new() { Key = "clion", Value = "clion64.exe", Comment = "CLion" },
            new() { Key = "androidstudio", Value = "studio64.exe", Comment = "Android Studio" },
            new() { Key = "excel", Value = "EXCEL.EXE", Comment = "Excel" },
            new() { Key = "word", Value = "WINWORD.EXE", Comment = "Word" },
            new() { Key = "powerpoint", Value = "POWERPNT.EXE", Comment = "PowerPoint" },
            new() { Key = "onenote", Value = "ONENOTE.EXE", Comment = "OneNote" },
            new() { Key = "outlook", Value = "OUTLOOK.EXE", Comment = "Outlook" },
            new() { Key = "wps", Value = "wps.exe", Comment = "WPS 文字" },
            new() { Key = "wpset", Value = "et.exe", Comment = "WPS 表格" },
            new() { Key = "wpp", Value = "wpp.exe", Comment = "WPS 演示" },
            new() { Key = "acrobat", Value = "Acrobat.exe", Comment = "Adobe Acrobat" },
            new() { Key = "sumatra", Value = "SumatraPDF.exe", Comment = "SumatraPDF" },
            new() { Key = "calibre", Value = "calibre.exe", Comment = "Calibre" },
    };

    public static AppSettings Create()
    {
        var s = new AppSettings
        {
            BuiltinEnginesVersion = BuiltinEnginesVersion,
            BuiltinWebVersion = BuiltinWebVersion,
            BuiltinRunVersion = BuiltinRunVersion,
            CapsloxDefaultsVersion = AppSettings.CapsloxDefaultsVersionCurrent,
        };
        s.Keys = CapsloxScheme();

        /* 新装的配置本来就是默认方案：Caps+Q = Qbar、Caps+R = Delete 都在
         * CapsloxScheme() 里。迁移只用来给"老配置"补洞，新配置直接标记已完成 ——
         * 否则后面 Normalize 会再往里塞一遍（同一份配置里出现两条同 combo 的绑定）。 */
        s.Global.BaseLayerDefaultsFixed = true;
        s.Search = new List<QEntry>
        {
            new() { Key = "default", Value = "https://www.bing.com/search?q={q}", Comment = "默认搜索" },

            // ---- 引擎表由 QuickSearch 的 ui/data/engines.json 转换而来 ----
            // 生成脚本：tools/qs_final.py ；图标已内置为 Assets/engine-icons/*.png。
            // 关键字规则：国内引擎取拼音首字母，国外引擎取英文名前两个字母。
            new() { Key = "bd", Value = "https://www.baidu.com/s?wd={q}&ie=utf-8", Comment = "百度", EngineIcon = "bai_du" },
            new() { Key = "zh", Value = "https://www.zhihu.com/search?q={q}", Comment = "知乎", EngineIcon = "zhi_hu" },
            new() { Key = "bilis", Value = "https://search.bilibili.com/all?keyword={q}", Comment = "哔哩哔哩搜索", EngineIcon = "bi_li_bi_li" },
            new() { Key = "tb", Value = "https://s.taobao.com/search?q={q}", Comment = "淘宝", EngineIcon = "tao_bao" },
            new() { Key = "wiki", Value = "https://zh.wikipedia.org/wiki/{q}", Comment = "维基百科", EngineIcon = "wei_ji_bai_ke" },
            new() { Key = "deepseek", Value = "https://chat.deepseek.com/?q={q}", Comment = "DeepSeek", EngineIcon = "deepseek" },
            new() { Key = "gpt", Value = "https://chatgpt.com/?q={q}", Comment = "ChatGPT", EngineIcon = "chatgpt" },
            new() { Key = "wngp", Value = "https://115.com/?mode=search&search_value={q}", Comment = "115 网盘", EngineIcon = "115_wang_pan" },
            new() { Key = "e166", Value = "https://s.1688.com/selloffer/offer_search.htm?keywords={q}", Comment = "1688", EngineIcon = "1688" },
            new() { Key = "diny", Value = "https://www.1905.com/search/?q={q}", Comment = "1905 电影网", EngineIcon = "1905_dian_ying_wang" },
            new() { Key = "so", Value = "https://www.so.com/s?ie=utf-8&q={q}", Comment = "360", EngineIcon = "360" },
            new() { Key = "dm", Value = "https://so.3dmgame.com/?keyword={q}", Comment = "3DM", EngineIcon = "3dm" },
            new() { Key = "px", Value = "https://500px.com/search?q={q}", Comment = "500px", EngineIcon = "500px" },
            new() { Key = "ao", Value = "https://archiveofourown.org/works/search?work_search%5Bquery%5D={q}", Comment = "AO3", EngineIcon = "ao3" },
            new() { Key = "ap", Value = "https://apkpure.com/search?q={q}", Comment = "APKPure", EngineIcon = "apkpure" },
            new() { Key = "ac", Value = "https://www.acfun.cn/search/?type=complex&keyword={q}", Comment = "AcFun", EngineIcon = "acfun" },
            new() { Key = "al", Value = "https://www.aliexpress.com/wholesale?SearchText={q}", Comment = "AliExpress", EngineIcon = "aliexpress" },
            new() { Key = "al2", Value = "https://alternativeto.net/browse/search/?q={q}", Comment = "AlternativeTo", EngineIcon = "alternativeto" },
            new() { Key = "am", Value = "https://www.amazon.com/s?k={q}", Comment = "Amazon", EngineIcon = "ya_ma_xun" },
            new() { Key = "apache", Value = "https://issues.apache.org/jira/secure/QuickSearch.jspa?searchString={q}", Comment = "Apache Issues", EngineIcon = "apache_issues" },
            new() { Key = "arch", Value = "https://wiki.archlinux.org/index.php?search={q}", Comment = "Arch Wiki", EngineIcon = "arch_wiki" },
            new() { Key = "ar", Value = "https://www.artstation.com/search?q={q}", Comment = "ArtStation", EngineIcon = "artstation" },
            new() { Key = "bb2", Value = "https://www.bbc.co.uk/search?q={q}", Comment = "BBC", EngineIcon = "bbc" },
            new() { Key = "ba", Value = "https://bandcamp.com/search?q={q}", Comment = "Bandcamp", EngineIcon = "bandcamp" },
            new() { Key = "be", Value = "https://www.behance.net/search/projects?search={q}", Comment = "Behance", EngineIcon = "behance" },
            new() { Key = "bi", Value = "https://cn.bing.com/search?q={q}", Comment = "Bing", EngineIcon = "bi_ying" },
            new() { Key = "bitbucket", Value = "https://bitbucket.org/repo/all?name={q}", Comment = "Bitbucket", EngineIcon = "bitbucket" },
            new() { Key = "bl", Value = "https://bsky.app/search?q={q}", Comment = "Bluesky", EngineIcon = "bluesky" },
            new() { Key = "br", Value = "https://search.brave.com/search?q={q}", Comment = "Brave 搜索", EngineIcon = "brave_sou_suo" },
            new() { Key = "cn", Value = "https://edition.cnn.com/search?q={q}", Comment = "CNN", EngineIcon = "cnn" },
            new() { Key = "csdn", Value = "https://so.csdn.net/so/search?q={q}", Comment = "CSDN", EngineIcon = "csdn" },
            new() { Key = "ch", Value = "https://chrome.google.com/webstore/search/{q}", Comment = "Chrome商店", EngineIcon = "chrome_shang_dian" },
            new() { Key = "claude", Value = "https://claude.ai/new?q={q}", Comment = "Claude", EngineIcon = "claude" },
            new() { Key = "codeberg", Value = "https://codeberg.org/explore/repos?q={q}", Comment = "Codeberg", EngineIcon = "codeberg" },
            new() { Key = "co2", Value = "https://www.codecademy.com/search?query={q}", Comment = "Codecademy", EngineIcon = "codecademy" },
            new() { Key = "conda", Value = "https://anaconda.org/search?q={q}", Comment = "Conda", EngineIcon = "conda" },
            new() { Key = "copilot", Value = "https://copilot.microsoft.com/?q={q}", Comment = "Copilot", EngineIcon = "copilot" },
            new() { Key = "co", Value = "https://www.coursera.org/search?query={q}", Comment = "Coursera", EngineIcon = "coursera" },
            new() { Key = "cr", Value = "https://www.crunchyroll.com/search?q={q}", Comment = "Crunchyroll", EngineIcon = "crunchyroll" },
            new() { Key = "db", Value = "https://dblp.org/search?q={q}", Comment = "DBLP", EngineIcon = "dblp" },
            new() { Key = "dev", Value = "https://dev.to/search?q={q}", Comment = "DEV", EngineIcon = "dev" },
            new() { Key = "da", Value = "https://www.dailymotion.com/search/{q}", Comment = "Dailymotion", EngineIcon = "dailymotion" },
            new() { Key = "de2", Value = "https://www.deviantart.com/search?q={q}", Comment = "DeviantArt", EngineIcon = "deviantart" },
            new() { Key = "docker", Value = "https://hub.docker.com/search?q={q}", Comment = "Docker Hub", EngineIcon = "docker_hub" },
            new() { Key = "dr", Value = "https://dribbble.com/search/{q}", Comment = "Dribbble", EngineIcon = "dribbble" },
            new() { Key = "dr2", Value = "https://www.dropbox.com/search/personal?query={q}", Comment = "Dropbox", EngineIcon = "dropbox" },
            new() { Key = "dd", Value = "https://duckduckgo.com/?q={q}", Comment = "DuckDuckGo", EngineIcon = "duckduckgo" },
            new() { Key = "eb2", Value = "https://www.ebsco.com/search?q={q}", Comment = "EBSCO", EngineIcon = "ebsco" },
            new() { Key = "ec", Value = "https://www.ecosia.org/search?q={q}", Comment = "Ecosia", EngineIcon = "ecosia" },
            new() { Key = "ed", Value = "https://microsoftedge.microsoft.com/addons/search/{q}", Comment = "Edge商店", EngineIcon = "edge_shang_dian" },
            new() { Key = "ep", Value = "https://store.epicgames.com/zh-CN/search?q={q}", Comment = "Epic 商店", EngineIcon = "epic_shang_dian" },
            new() { Key = "et", Value = "https://www.etsy.com/search?q={q}", Comment = "Etsy", EngineIcon = "etsy" },
            new() { Key = "fd", Value = "https://search.f-droid.org/?q={q}", Comment = "F-Droid", EngineIcon = "f_droid" },
            new() { Key = "fa", Value = "https://www.facebook.com/search/results.php?q={q}", Comment = "Facebook", EngineIcon = "facebook" },
            new() { Key = "fr2", Value = "https://www.thefreedictionary.com/{q}", Comment = "Free Dictionary", EngineIcon = "free_dictionary" },
            new() { Key = "fr", Value = "https://www.freepik.com/search?query={q}", Comment = "Freepik", EngineIcon = "freepik" },
            new() { Key = "go2", Value = "https://www.gog.com/games?query={q}", Comment = "GOG", EngineIcon = "gog" },
            new() { Key = "gemini", Value = "https://gemini.google.com/app?q={q}", Comment = "Gemini", EngineIcon = "gemini" },
            new() { Key = "ge", Value = "https://genius.com/search?q={q}", Comment = "Genius", EngineIcon = "genius" },
            new() { Key = "gi", Value = "https://giphy.com/search/{q}", Comment = "Giphy", EngineIcon = "giphy" },
            new() { Key = "gh", Value = "https://github.com/search?q={q}", Comment = "GitHub", EngineIcon = "github" },
            new() { Key = "gl", Value = "https://gitlab.com/search?search={q}", Comment = "GitLab", EngineIcon = "gitlab" },
            new() { Key = "gitee", Value = "https://search.gitee.com/?q={q}", Comment = "Gitee", EngineIcon = "gitee" },
            new() { Key = "gopkg", Value = "https://pkg.go.dev/search?q={q}", Comment = "Go Packages", EngineIcon = "go_packages" },
            new() { Key = "go4", Value = "https://www.goodreads.com/search?q={q}", Comment = "Goodreads", EngineIcon = "goodreads" },
            new() { Key = "go", Value = "https://www.google.com/search?q={q}&ie=utf-8&oe=utf-8", Comment = "Google", EngineIcon = "google" },
            new() { Key = "go3", Value = "https://drive.google.com/drive/u/0/search?q={q}", Comment = "Google Drive", EngineIcon = "google_drive" },
            new() { Key = "gr", Value = "https://greasyfork.org/scripts?q={q}&utf8=✓", Comment = "GreasyFork", EngineIcon = "greasyfork" },
            new() { Key = "grok", Value = "https://grok.com/?q={q}", Comment = "Grok", EngineIcon = "grok" },
            new() { Key = "hn", Value = "https://hn.algolia.com/?q={q}", Comment = "Hacker News", EngineIcon = "hacker_news" },
            new() { Key = "hf", Value = "https://huggingface.co/models?search={q}", Comment = "Hugging Face", EngineIcon = "hugging_face" },
            new() { Key = "hu", Value = "https://www.humblebundle.com/store/search?search={q}", Comment = "Humble Bundle", EngineIcon = "humble_bundle" },
            new() { Key = "ie", Value = "https://ieeexplore.ieee.org/search/searchresult.jsp?queryText={q}", Comment = "IEEE Xplore", EngineIcon = "ieee_xplore" },
            new() { Key = "ie2", Value = "https://standards.ieee.org/search/?q={q}", Comment = "IEEE 标准", EngineIcon = "ieee_biao_zhun" },
            new() { Key = "ig2", Value = "https://www.igdb.com/search?type=1&q={q}", Comment = "IGDB", EngineIcon = "igdb" },
            new() { Key = "ig", Value = "https://www.ign.com/search?q={q}", Comment = "IGN", EngineIcon = "ign" },
            new() { Key = "ip", Value = "https://www.ip138.com/iplookup.asp?ip={q}", Comment = "IP138", EngineIcon = "ip138" },
            new() { Key = "it2", Value = "https://www.ithome.com/search/{q}.html", Comment = "IT之家", EngineIcon = "it_zhi_jia" },
            new() { Key = "ic2", Value = "https://icon-sets.iconify.design/?query={q}", Comment = "Iconify", EngineIcon = "iconify" },
            new() { Key = "infoq", Value = "https://www.infoq.cn/search?q={q}", Comment = "InfoQ", EngineIcon = "infoq" },
            new() { Key = "in", Value = "https://archive.org/search?query={q}", Comment = "Internet Archive", EngineIcon = "internet_archive" },
            new() { Key = "js", Value = "http://www.jstor.org/action/doAdvancedSearch?q0={q}", Comment = "JSTOR", EngineIcon = "jstor" },
            new() { Key = "kh", Value = "https://www.khanacademy.org/search?page_search_query={q}", Comment = "Khan Academy", EngineIcon = "khan_academy" },
            new() { Key = "kimi", Value = "https://www.kimi.com/?q={q}", Comment = "Kimi", EngineIcon = "kimi" },
            new() { Key = "la", Value = "https://www.last.fm/search?q={q}", Comment = "Last.fm", EngineIcon = "last_fm" },
            new() { Key = "leetcode", Value = "https://leetcode.cn/search/?q={q}", Comment = "LeetCode", EngineIcon = "leetcode" },
            new() { Key = "li", Value = "https://www.linguee.com/english-chinese/search?query={q}", Comment = "Linguee", EngineIcon = "linguee" },
            new() { Key = "li2", Value = "https://www.linkedin.com/search/results/all/?keywords={q}", Comment = "LinkedIn", EngineIcon = "linkedin" },
            new() { Key = "lobsters", Value = "https://lobste.rs/search?q={q}", Comment = "Lobsters", EngineIcon = "lobsters" },
            new() { Key = "lo", Value = "https://lottiefiles.com/search?q={q}", Comment = "Lottiefiles", EngineIcon = "lottiefiles" },
            new() { Key = "md", Value = "https://developer.mozilla.org/zh-CN/search?q={q}", Comment = "MDN", EngineIcon = "mdn" },
            new() { Key = "me2", Value = "https://mega.nz/search?q={q}", Comment = "MEGA", EngineIcon = "mega" },
            new() { Key = "mi2", Value = "https://ocw.mit.edu/search/?q={q}", Comment = "MIT OpenCourseWare", EngineIcon = "mit_opencourseware" },
            new() { Key = "maven", Value = "https://mvnrepository.com/search?q={q}", Comment = "Maven", EngineIcon = "maven" },
            new() { Key = "me", Value = "https://www.metacritic.com/search/{q}/", Comment = "Metacritic", EngineIcon = "metacritic" },
            new() { Key = "mi", Value = "https://apps.microsoft.com/search?query={q}", Comment = "Microsoft Store", EngineIcon = "microsoft_store" },
            new() { Key = "mistral", Value = "https://chat.mistral.ai/chat?q={q}", Comment = "Mistral", EngineIcon = "mistral" },
            new() { Key = "mj", Value = "https://www.mojeek.com/search?q={q}", Comment = "Mojeek", EngineIcon = "mojeek" },
            new() { Key = "ne", Value = "https://www.nexusmods.com/search/?gsearch={q}", Comment = "Nexus Mods", EngineIcon = "nexus_mods" },
            new() { Key = "nu", Value = "https://www.nuget.org/packages?q={q}", Comment = "NuGet", EngineIcon = "nuget" },
            new() { Key = "on", Value = "https://onedrive.live.com/?q={q}", Comment = "OneDrive", EngineIcon = "onedrive" },
            new() { Key = "op2", Value = "https://opencritic.com/search?criteria={q}", Comment = "OpenCritic", EngineIcon = "opencritic" },
            new() { Key = "op", Value = "https://www.openstreetmap.org/search?query={q}", Comment = "OpenStreetMap", EngineIcon = "openstreetmap" },
            new() { Key = "packagist", Value = "https://packagist.org/search/?q={q}", Comment = "Packagist", EngineIcon = "packagist" },
            new() { Key = "pa", Value = "https://papago.naver.com/?sk=auto&tk=zh-CN&st={q}", Comment = "Papago", EngineIcon = "papago" },
            new() { Key = "pa2", Value = "https://paperswithcode.com/search?q={q}", Comment = "Papers with Code", EngineIcon = "papers_with_code" },
            new() { Key = "perplexity", Value = "https://www.perplexity.ai/search?q={q}", Comment = "Perplexity AI", EngineIcon = "perplexity_ai" },
            new() { Key = "pe", Value = "https://www.pexels.com/search/{q}/", Comment = "Pexels", EngineIcon = "pexels" },
            new() { Key = "pi3", Value = "https://www.pinterest.com/search/pins/?q={q}", Comment = "Pinterest", EngineIcon = "pinterest" },
            new() { Key = "pi2", Value = "https://pixabay.com/zh/images/search/{q}/", Comment = "Pixabay", EngineIcon = "pixabay" },
            new() { Key = "poe", Value = "https://poe.com/search?q={q}", Comment = "Poe", EngineIcon = "poe" },
            new() { Key = "pr2", Value = "https://www.producthunt.com/search?q={q}", Comment = "Product Hunt", EngineIcon = "product_hunt" },
            new() { Key = "pr", Value = "https://www.gutenberg.org/ebooks/search/?query={q}", Comment = "Project Gutenberg", EngineIcon = "project_gutenberg" },
            new() { Key = "pu", Value = "https://pubmed.ncbi.nlm.nih.gov/?term={q}", Comment = "PubMed", EngineIcon = "pubmed" },
            new() { Key = "pypi", Value = "https://pypi.org/search/?q={q}", Comment = "PyPI", EngineIcon = "pypi" },
            new() { Key = "qq", Value = "https://y.qq.com/portal/search.html#page=1&searchid=1&remoteplace=txt.yqq.top&t=song&w={q}", Comment = "QQ音乐", EngineIcon = "qq_yin_yue" },
            new() { Key = "quora", Value = "https://www.quora.com/search?q={q}", Comment = "Quora", EngineIcon = "quora" },
            new() { Key = "qw", Value = "https://www.qwant.com/?q={q}", Comment = "Qwant", EngineIcon = "qwant" },
            new() { Key = "qwen", Value = "https://chat.qwen.ai/?q={q}", Comment = "Qwen Chat", EngineIcon = "qwen_chat" },
            new() { Key = "re2", Value = "https://www.reddit.com/search/?q={q}", Comment = "Reddit", EngineIcon = "reddit" },
            new() { Key = "re", Value = "https://www.researchgate.net/search?q={q}", Comment = "ResearchGate", EngineIcon = "researchgate" },
            new() { Key = "gems", Value = "https://rubygems.org/search?query={q}", Comment = "RubyGems", EngineIcon = "rubygems" },
            new() { Key = "sc", Value = "https://www.sciencedirect.com/search?qs={q}", Comment = "ScienceDirect", EngineIcon = "sciencedirect" },
            new() { Key = "se2", Value = "https://www.semanticscholar.org/search?q={q}", Comment = "Semantic Scholar", EngineIcon = "semantic_scholar" },
            new() { Key = "sh", Value = "https://shopee.sg/search?keyword={q}", Comment = "Shopee", EngineIcon = "shopee" },
            new() { Key = "si2", Value = "https://www.similarweb.com/website/{q}/", Comment = "SimilarWeb", EngineIcon = "similarweb" },
            new() { Key = "so4", Value = "https://www.softpedia.com/search.php?q={q}", Comment = "Softpedia", EngineIcon = "softpedia" },
            new() { Key = "so3", Value = "https://soundcloud.com/search?q={q}", Comment = "SoundCloud", EngineIcon = "soundcloud" },
            new() { Key = "sourcegraph", Value = "https://sourcegraph.com/search?q={q}", Comment = "Sourcegraph", EngineIcon = "sourcegraph" },
            new() { Key = "sp", Value = "https://open.spotify.com/search/{q}", Comment = "Spotify", EngineIcon = "spotify" },
            new() { Key = "se", Value = "https://stackexchange.com/search?q={q}", Comment = "Stack Exchange", EngineIcon = "stack_exchange" },
            new() { Key = "so2", Value = "https://stackoverflow.com/search?q={q}", Comment = "StackOverflow", EngineIcon = "stackoverflow_gu_ge" },
            new() { Key = "st", Value = "https://www.startpage.com/sp/search?query={q}", Comment = "Startpage", EngineIcon = "startpage" },
            new() { Key = "st2", Value = "https://store.steampowered.com/search/?term={q}", Comment = "Steam", EngineIcon = "steam" },
            new() { Key = "ta", Value = "https://www.taptap.cn/search/{q}", Comment = "TapTap", EngineIcon = "taptap" },
            new() { Key = "te", Value = "https://www.temu.com/search_result.html?search_key={q}", Comment = "Temu", EngineIcon = "temu" },
            new() { Key = "th", Value = "https://www.threads.net/search?q={q}", Comment = "Threads", EngineIcon = "threads" },
            new() { Key = "ti", Value = "https://www.tiktok.com/search?q={q}", Comment = "TikTok", EngineIcon = "tiktok" },
            new() { Key = "tu", Value = "https://www.tumblr.com/search/{q}", Comment = "Tumblr", EngineIcon = "tumblr" },
            new() { Key = "tw", Value = "https://www.twitch.tv/search?term={q}", Comment = "Twitch", EngineIcon = "twitch" },
            new() { Key = "x", Value = "https://x.com/search?q={q}", Comment = "X 网站", EngineIcon = "twitter" },
            new() { Key = "ud", Value = "https://www.udemy.com/courses/search/?q={q}", Comment = "Udemy", EngineIcon = "udemy" },
            new() { Key = "un", Value = "https://unsplash.com/s/photos/{q}", Comment = "Unsplash", EngineIcon = "unsplash" },
            new() { Key = "vi", Value = "https://vimeo.com/search?q={q}", Comment = "Vimeo", EngineIcon = "vimeo" },
            new() { Key = "vi2", Value = "https://www.virustotal.com/gui/search/{q}", Comment = "VirusTotal", EngineIcon = "virustotal" },
            new() { Key = "vo", Value = "https://www.vocabulary.com/dictionary/{q}", Comment = "Vocabulary.com", EngineIcon = "vocabulary_com" },
            new() { Key = "wo", Value = "http://apps.webofknowledge.com/UA_GeneralSearch.do?fieldCount=3&action=search&product=UA&search_mode=GeneralSearch&max_field_count=25&max_field_notice=Notice%3A+You+cannot+add+another+field.&input_invalid_notice=Search+Error%3A+Please+enter+a+search+term.&input_invalid_notice_limits=+%3Cbr%2F%3ENote%3A+Fields+displayed+in+scrolling+boxes+must+be+combined+with+at+least+one+other+search+field.&sa_img_alt=Select+terms+from+the+index&value(input1)={q}&value%28select1%29=TI&value%28hidInput1%29=initVoid&value%28hidShowIcon1%29=0&value%28bool_1_2%29=AND&value%28input2%29=&value%28select2%29=AU&value%28hidInput2%29=initAuthor&value%28hidShowIcon2%29=1&value%28bool_2_3%29=AND&value%28input3%29=&value%28select3%29=SO&value%28hidInput3%29=initSource&value%28hidShowIcon3%29=1&limitStatus=collapsed&expand_alt=Expand+these+settings&expand_title=Expand+these+settings&collapse_alt=Collapse+these+settings&collapse_title=Collapse+these+settings&SinceLastVisit_UTC=&SinceLastVisit_DATE=×panStatus=display%3A+block&timeSpanCollapsedListStatus=display%3A+none&period=Range+Selection&range=ALL&ssStatus=display%3Anone&ss_lemmatization=On&ss_query_language=&rsStatus=display%3Anone&rs_rec_per_page=10&rs_sort_by=PY.D%3BLD.D%3BVL.D%3BSO.A%3BPG.A%3BAU.A&rs_refinePanel=visibility%3Ashow", Comment = "WOS", EngineIcon = "wos" },
            new() { Key = "wa", Value = "https://www.walmart.com/search?q={q}", Comment = "Walmart", EngineIcon = "walmart" },
            new() { Key = "wa2", Value = "https://web.archive.org/web/*/{q}", Comment = "Wayback Machine", EngineIcon = "wayback_machine" },
            new() { Key = "wikihow", Value = "https://zh.wikihow.com/wikiHowTo?search={q}", Comment = "WikiHow", EngineIcon = "wikihow" },
            new() { Key = "wi", Value = "https://en.wikipedia.org/w/index.php?search={q}", Comment = "Wikipedia（英文）", EngineIcon = "wei_ji_bai_ke" },
            new() { Key = "wo2", Value = "https://www.wolframalpha.com/input?i={q}", Comment = "Wolfram Alpha", EngineIcon = "wolfram_alpha" },
            new() { Key = "ya", Value = "https://search.yahoo.com/search?p={q}", Comment = "Yahoo", EngineIcon = "yahoo" },
            new() { Key = "yx", Value = "https://yandex.com/search/?text={q}", Comment = "Yandex", EngineIcon = "yandex" },
            new() { Key = "ya2", Value = "https://yandex.com/images/search?text={q}", Comment = "Yandex 图片", EngineIcon = "yandex_tu_pian" },
            new() { Key = "yo", Value = "https://www.youtube.com/results?search_query={q}", Comment = "YouTube", EngineIcon = "youtube" },
            new() { Key = "yom", Value = "https://music.youtube.com/search?q={q}", Comment = "YouTube Music", EngineIcon = "youtube_music" },
            new() { Key = "ar2", Value = "https://arxiv.org/search/?query={q}", Comment = "arXiv", EngineIcon = "arxiv" },
            new() { Key = "crates", Value = "https://crates.io/search?q={q}", Comment = "crates.io", EngineIcon = "crates_io" },
            new() { Key = "eb", Value = "https://www.ebay.com/sch/i.html?_nkw={q}", Comment = "eBay", EngineIcon = "ebay" },
            new() { Key = "ed2", Value = "https://www.edx.org/search?q={q}", Comment = "edX", EngineIcon = "edx" },
            new() { Key = "fl", Value = "https://www.flickr.com/search/?q={q}", Comment = "flickr", EngineIcon = "flickr" },
            new() { Key = "fr3", Value = "https://www.freecodecamp.org/news/search?query={q}", Comment = "freeCodeCamp", EngineIcon = "freecodecamp" },
            new() { Key = "ic", Value = "https://www.iconfont.cn/search/index?searchType=icon&q={q}", Comment = "iconfont", EngineIcon = "iconfont" },
            new() { Key = "it", Value = "https://itch.io/search?q={q}", Comment = "itch.io", EngineIcon = "itch_io" },
            new() { Key = "ni", Value = "https://www.nicovideo.jp/search/{q}", Comment = "niconico", EngineIcon = "niconico" },
            new() { Key = "np", Value = "https://www.npmjs.com/search?q={q}", Comment = "npm", EngineIcon = "npm" },
            new() { Key = "pi", Value = "https://www.pixiv.net/tags/{q}", Comment = "pixiv", EngineIcon = "pixiv" },
            new() { Key = "wh", Value = "https://who.is/whois/{q}", Comment = "who.is", EngineIcon = "who_is" },
            new() { Key = "dngx", Value = "https://dxy.com/search?keyword={q}", Comment = "丁香医生", EngineIcon = "ding_xiang_yi_sheng" },
            new() { Key = "wnfn", Value = "http://www.wanfangdata.com.cn/search/searchList.do?searchType=all&searchWord={q}", Comment = "万方", EngineIcon = "wan_fang" },
            new() { Key = "xich", Value = "https://www.xiachufang.com/search/?keyword={q}", Comment = "下厨房", EngineIcon = "xia_chu_fang" },
            new() { Key = "dngf", Value = "https://so.eastmoney.com/web/s?keyword={q}", Comment = "东方财富", EngineIcon = "dong_fang_cai_fu" },
            new() { Key = "mo", Value = "https://www.icourse163.org/search.htm?search={q}", Comment = "中国大学MOOC", EngineIcon = "zhong_guo_da_xue_mooc" },
            new() { Key = "zhng", Value = "https://sou.chinanews.com.cn/search.do?q={q}", Comment = "中国新闻网", EngineIcon = "zhong_guo_xin_wen_wang" },
            new() { Key = "ltin", Value = "https://search.rakuten.co.jp/search/mall/{q}/", Comment = "乐天市场", EngineIcon = "le_tian_shi_chang" },
            new() { Key = "ymxn", Value = "https://www.amazon.cn/s?k={q}", Comment = "亚马逊", EngineIcon = "ya_ma_xun" },
            new() { Key = "jd", Value = "https://search.jd.com/Search?keyword={q}&enc=utf-8", Comment = "京东", EngineIcon = "jing_dong" },
            new() { Key = "shnm", Value = "https://search.smzdm.com/?c=home&s={q}", Comment = "什么值得买", EngineIcon = "shen_me_zhi_de_mai" },
            new() { Key = "jnrt", Value = "https://www.toutiao.com/search/?keyword={q}", Comment = "今日头条", EngineIcon = "jin_ri_tou_tiao" },
            new() { Key = "qzhz", Value = "https://www.qcc.com/web/search?key={q}", Comment = "企查查", EngineIcon = "qi_zha_zha" },
            new() { Key = "yuk", Value = "https://so.youku.com/search_video/q_{q}", Comment = "优酷", EngineIcon = "you_ku" },
            new() { Key = "fngh", Value = "https://search.ifeng.com/sofeng/search.action?q={q}", Comment = "凤凰网", EngineIcon = "feng_huang_wang" },
            new() { Key = "jinq", Value = "https://dictionary.cambridge.org/dictionary/english-chinese-simplified/{q}", Comment = "剑桥词典", EngineIcon = "jian_qiao_ci_dian" },
            new() { Key = "cnblogs", Value = "https://zzk.cnblogs.com/s?w={q}", Comment = "博客园", EngineIcon = "bo_ke_yuan" },
            new() { Key = "jk", Value = "https://web.okjike.com/search?q={q}", Comment = "即刻", EngineIcon = "ji_ke" },
            new() { Key = "gshw", Value = "https://so.gushiwen.cn/search.aspx?value={q}", Comment = "古诗文网", EngineIcon = "gu_shi_wen_wang" },
            new() { Key = "mgyn", Value = "https://music.migu.cn/v3/search?keyword={q}", Comment = "咪咕音乐", EngineIcon = "mi_gu_yin_yue" },
            new() { Key = "wipn", Value = "https://category.vip.com/suggest.php?keyword={q}", Comment = "唯品会", EngineIcon = "wei_pin_hui" },
            new() { Key = "dzhn", Value = "https://www.dianping.com/search/keyword/1/0_{q}", Comment = "大众点评", EngineIcon = "da_zhong_dian_ping" },
            new() { Key = "tinq", Value = "https://www.tianqi.com/so/?keyword={q}", Comment = "天气网", EngineIcon = "tian_qi_wang" },
            new() { Key = "tinm", Value = "https://list.tmall.com/search_product.htm?q={q}", Comment = "天猫", EngineIcon = "tian_mao" },
            new() { Key = "tiny", Value = "https://www.tianyancha.com/search?key={q}", Comment = "天眼查", EngineIcon = "tian_yan_cha" },
            new() { Key = "tipn", Value = "https://so.pcauto.com.cn/cs/s?q={q}", Comment = "太平洋汽车", EngineIcon = "tai_ping_yang_qi_che" },
            new() { Key = "yngs", Value = "https://search.cctv.com/search.php?qtext={q}", Comment = "央视网", EngineIcon = "yang_shi_wang" },
            new() { Key = "tt", Value = "https://so.toutiao.com/search?keyword={q}", Comment = "头条搜索", EngineIcon = "tou_tiao_sou_suo" },
            new() { Key = "kk", Value = "https://quark.sm.cn/s?q={q}", Comment = "夸克搜索", EngineIcon = "kua_ke_sou_suo" },
            new() { Key = "kukw", Value = "https://pan.quark.cn/search?q={q}", Comment = "夸克网盘", EngineIcon = "kua_ke_wang_pan" },
            new() { Key = "hodf", Value = "https://www.haodf.com/search/?kw={q}", Comment = "好大夫在线", EngineIcon = "hao_da_fu_zai_xian" },
            new() { Key = "hokn", Value = "https://haokan.baidu.com/web/search/page?query={q}", Comment = "好看视频", EngineIcon = "hao_kan_shi_pin" },
            new() { Key = "kngf", Value = "https://search.kongfz.com/product_result/?key={q}", Comment = "孔夫子旧书网", EngineIcon = "kong_fu_zi_jiu_shu_wang" },
            new() { Key = "yji", Value = "https://www.ikea.cn/cn/zh/search/products/?q={q}", Comment = "宜家", EngineIcon = "yi_jia" },
            new() { Key = "xiom", Value = "https://app.mi.com/searchAll?keywords={q}", Comment = "小米应用商店", EngineIcon = "xiao_mi_ying_yong_shang_dian" },
            new() { Key = "xhs", Value = "https://www.xiaohongshu.com/search_result?keyword={q}", Comment = "小红书", EngineIcon = "xiao_hong_shu" },
            new() { Key = "sspai", Value = "https://sspai.com/search/post/{q}", Comment = "少数派", EngineIcon = "shao_shu_pai" },
            new() { Key = "oschina", Value = "https://www.oschina.net/search?q={q}", Comment = "开源中国", EngineIcon = "kai_yuan_zhong_guo" },
            new() { Key = "dngd", Value = "https://search.dangdang.com/?key={q}", Comment = "当当", EngineIcon = "dang_dang" },
            new() { Key = "wx", Value = "https://weixin.sogou.com/weixin?ie=utf8&type=2&query={q}", Comment = "微信(搜狗)", EngineIcon = "wei_xin_sou_gou" },
            new() { Key = "wixn", Value = "https://weread.qq.com/web/search/books?keyword={q}", Comment = "微信读书", EngineIcon = "wei_xin_du_shu" },
            new() { Key = "wb", Value = "https://s.weibo.com/weibo/{q}", Comment = "微博", EngineIcon = "wei_bo" },
            new() { Key = "byng3", Value = "https://www.bing.com/images/search?q={q}", Comment = "必应图片", EngineIcon = "bi_ying" },
            new() { Key = "byng2", Value = "https://www.bing.com/maps?q={q}", Comment = "必应地图", EngineIcon = "bi_ying" },
            new() { Key = "kuis", Value = "https://www.kuaishou.com/search/video?searchKey={q}", Comment = "快手", EngineIcon = "kuai_shou" },
            new() { Key = "kuid", Value = "https://www.kuaidi100.com/?nu={q}", Comment = "快递100", EngineIcon = "kuai_di_100" },
            new() { Key = "mkwn", Value = "https://www.imooc.com/search/?words={q}", Comment = "慕课网", EngineIcon = "mu_ke_wang" },
            new() { Key = "dngc", Value = "https://www.dongchedi.com/search?keyword={q}", Comment = "懂车帝", EngineIcon = "dong_che_di" },
            new() { Key = "dy", Value = "https://www.douyin.com/search/{q}", Comment = "抖音", EngineIcon = "dou_yin" },
            new() { Key = "pdd", Value = "https://mobile.yangkeduo.com/search_result.html?search_key={q}", Comment = "拼多多", EngineIcon = "pin_duo_duo" },
            new() { Key = "juejin", Value = "https://juejin.cn/search?query={q}", Comment = "掘金", EngineIcon = "jue_jin" },
            new() { Key = "suhs", Value = "https://so.tv.sohu.com/mts?wd={q}", Comment = "搜狐视频", EngineIcon = "sou_hu_shi_pin" },
            new() { Key = "sogou", Value = "https://www.sogou.com/web?query={q}", Comment = "搜狗", EngineIcon = "sou_gou" },
            new() { Key = "sugu2", Value = "https://pic.sogou.com/pics?query={q}", Comment = "搜狗图片", EngineIcon = "sou_gou" },
            new() { Key = "wy", Value = "https://yiyan.baidu.com/?q={q}", Comment = "文心一言", EngineIcon = "wen_xin_yi_yan" },
            new() { Key = "xnln", Value = "https://search.sina.com.cn/?q={q}", Comment = "新浪新闻", EngineIcon = "xin_lang_xin_wen" },
            new() { Key = "ych", Value = "https://so.yiche.com/?q={q}", Comment = "易车", EngineIcon = "yi_che" },
            new() { Key = "jnji", Value = "https://search.jjwxc.net/search.php?kw={q}", Comment = "晋江文学城", EngineIcon = "jin_jiang_wen_xue_cheng" },
            new() { Key = "zp", Value = "https://chatglm.cn/?q={q}", Comment = "智谱清言", EngineIcon = "zhi_pu_qing_yan" },
            new() { Key = "yudo", Value = "https://youdao.com/w/{q}", Comment = "有道词典", EngineIcon = "you_dao_ci_dian" },
            new() { Key = "guokr", Value = "https://www.guokr.com/search/?q={q}", Comment = "果壳", EngineIcon = "guo_ke" },
            new() { Key = "klns", Value = "https://www.collinsdictionary.com/dictionary/english/{q}", Comment = "柯林斯词典", EngineIcon = "ke_lin_si_ci_dian" },
            new() { Key = "hndi", Value = "https://www.zdic.net/hans/{q}", Comment = "汉典", EngineIcon = "han_dian" },
            new() { Key = "qchz", Value = "https://sou.autohome.com.cn/zonghe?q={q}", Comment = "汽车之家", EngineIcon = "qi_che_zhi_jia" },
            new() { Key = "hailuo", Value = "https://hailuoai.com/?q={q}", Comment = "海螺AI", EngineIcon = "hai_luo_ai" },
            new() { Key = "hic", Value = "https://dict.cn/{q}", Comment = "海词", EngineIcon = "hai_ci" },
            new() { Key = "yumn", Value = "https://so.gamersky.com/?s={q}", Comment = "游民星空", EngineIcon = "you_min_xing_kong" },
            new() { Key = "pngp", Value = "https://www.thepaper.cn/searchResult?searchWord={q}", Comment = "澎湃新闻", EngineIcon = "peng_pai_xin_wen" },
            new() { Key = "iqch", Value = "https://aiqicha.baidu.com/s?q={q}", Comment = "爱企查", EngineIcon = "ai_qi_cha" },
            new() { Key = "ikqc", Value = "https://so.xcar.com.cn/?q={q}", Comment = "爱卡汽车", EngineIcon = "ai_ka_qi_che" },
            new() { Key = "iqy", Value = "https://so.iqiyi.com/so/q_{q}", Comment = "爱奇艺", EngineIcon = "ai_qi_yi" },
            new() { Key = "nijn", Value = "https://www.oxfordlearnersdictionaries.com/search/english/?q={q}", Comment = "牛津词典", EngineIcon = "niu_jin_ci_dian" },
            new() { Key = "bidt", Value = "https://image.baidu.com/search/index?tn=baiduimage&ie=utf-8&word={q}", Comment = "百度图片", EngineIcon = "bai_du_zhi_dao" },
            new() { Key = "bidd", Value = "https://map.baidu.com/search?querytype=s&wd={q}", Comment = "百度地图", EngineIcon = "bai_du_di_tu" },
            new() { Key = "bidx", Value = "http://xueshu.baidu.com/s?wd={q}", Comment = "百度学术", EngineIcon = "bai_du_xue_shu" },
            new() { Key = "bwk", Value = "https://wenku.baidu.com/search?word={q}", Comment = "百度文库", EngineIcon = "bai_du_wen_ku" },
            new() { Key = "bidh", Value = "https://hanyu.baidu.com/s?wd={q}", Comment = "百度汉语", EngineIcon = "bai_du_han_yu" },
            new() { Key = "bb", Value = "https://baike.baidu.com/search/word?pic=1&sug=1&word={q}", Comment = "百度百科", EngineIcon = "bai_du_bai_ke" },
            new() { Key = "bzd", Value = "https://zhidao.baidu.com/search?word={q}", Comment = "百度知道", EngineIcon = "bai_du_zhi_dao" },
            new() { Key = "bjy", Value = "https://jingyan.baidu.com/search?word={q}", Comment = "百度经验", EngineIcon = "bai_du_jing_yan" },
            new() { Key = "bidw", Value = "https://pan.baidu.com/disk/home?#/search?key={q}", Comment = "百度网盘", EngineIcon = "bai_du_wang_pan" },
            new() { Key = "zd", Value = "https://zhida.zhihu.com/?q={q}", Comment = "知乎直答", EngineIcon = "zhi_hu_zhi_da" },
            new() { Key = "zhwn", Value = "http://epub.cnki.net/kns/brief/default_result.aspx?txt_1_value1={q}&dbPrefix=SCDB&db_opt=CJFQ%2CCJFN%2CCDFD%2CCMFD%2CCPFD%2CIPFD%2CCCND%2CCCJD%2CHBRD&singleDB=SCDB&action=scdbsearch", Comment = "知网", EngineIcon = "zhi_wang" },
            new() { Key = "mt", Value = "https://metaso.cn/?q={q}", Comment = "秘塔AI搜索", EngineIcon = "mi_ta_ai_sou_suo" },
            new() { Key = "zhnk", Value = "https://www.zcool.com.cn/search/content?&word={q}", Comment = "站酷", EngineIcon = "zhan_ku" },
            new() { Key = "zhnz", Value = "https://tool.chinaz.com/?q={q}", Comment = "站长之家", EngineIcon = "zhan_zhang_zhi_jia" },
            new() { Key = "dyci", Value = "https://www.yicai.com/search?keys={q}", Comment = "第一财经", EngineIcon = "di_yi_cai_jing" },
            new() { Key = "jianshu", Value = "https://www.jianshu.com/search?q={q}", Comment = "简书", EngineIcon = "jian_shu" },
            new() { Key = "zngh", Value = "https://search.zongheng.com/s?keyword={q}", Comment = "纵横中文网", EngineIcon = "zong_heng_zhong_wen_wang" },
            new() { Key = "wks", Value = "https://zh.wikisource.org/w/index.php?search={q}", Comment = "维基文库", EngineIcon = "wei_ji_wen_ku" },
            new() { Key = "wngy2", Value = "https://you.163.com/search?keyword={q}", Comment = "网易严选", EngineIcon = "wang_yi_yan_xuan" },
            new() { Key = "wngy4", Value = "https://study.163.com/search.htm?keyword={q}", Comment = "网易云课堂", EngineIcon = "wang_yi_yun_ke_tang" },
            new() { Key = "wngy", Value = "https://music.163.com/#/search/m/?s={q}", Comment = "网易云音乐", EngineIcon = "wang_yi_yun_yin_yue" },
            new() { Key = "wngy3", Value = "https://www.163.com/search?keyword={q}", Comment = "网易新闻", EngineIcon = "wang_yi_xin_wen" },
            new() { Key = "maimai", Value = "https://maimai.cn/web/search_center?type=gossip&query={q}&highlight=true", Comment = "脉脉", EngineIcon = "mo_mo" },
            new() { Key = "txcloud", Value = "https://cloud.tencent.com/developer/search/article-{q}", Comment = "腾讯云社区", EngineIcon = "teng_xun_yun_she_qu" },
            new() { Key = "yb", Value = "https://yuanbao.tencent.com/?q={q}", Comment = "腾讯元宝", EngineIcon = "teng_xun_yuan_bao" },
            new() { Key = "tngx", Value = "https://map.qq.com/?what={q}", Comment = "腾讯地图", EngineIcon = "teng_xun_di_tu" },
            new() { Key = "tx", Value = "https://v.qq.com/x/search/?q={q}", Comment = "腾讯视频", EngineIcon = "teng_xun_shi_pin" },
            new() { Key = "tngx2", Value = "https://pc.qq.com/search.html#!keyword={q}", Comment = "腾讯软件中心", EngineIcon = "teng_xun_ruan_jian_zhong_xin" },
            new() { Key = "tv", Value = "https://so.mgtv.com/so?k={q}", Comment = "芒果TV", EngineIcon = "mang_guo_tv" },
            new() { Key = "hubn", Value = "https://huaban.com/search/?q={q}", Comment = "花瓣", EngineIcon = "hua_ban" },
            new() { Key = "snng", Value = "https://search.suning.com/{q}/", Comment = "苏宁易购", EngineIcon = "su_ning_yi_gou" },
            new() { Key = "runoob", Value = "https://www.runoob.com/?s={q}", Comment = "菜鸟教程", EngineIcon = "cai_niao_jiao_cheng" },
            new() { Key = "meng", Value = "https://zh.moegirl.org.cn/index.php?search={q}", Comment = "萌娘百科", EngineIcon = "meng_niang_bai_ke" },
            new() { Key = "hxi", Value = "https://www.huxiu.com/search.html?s={q}", Comment = "虎嗅", EngineIcon = "hu_xiu" },
            new() { Key = "hp", Value = "https://bbs.hupu.com/search?q={q}", Comment = "虎扑", EngineIcon = "hu_pu" },
            new() { Key = "xgus", Value = "https://www.ixigua.com/search/{q}", Comment = "西瓜视频", EngineIcon = "xi_gua_shi_pin" },
            new() { Key = "ggdt", Value = "https://www.google.com/maps/search/{q}", Comment = "谷歌地图", EngineIcon = "gu_ge_di_tu" },
            new() { Key = "ggdq", Value = "https://earth.google.com/web/search/{q}", Comment = "谷歌地球", EngineIcon = "gu_ge_di_qiu" },
            new() { Key = "ggxu", Value = "https://scholar.google.com/scholar?hl=zh-CN&q={q}", Comment = "谷歌学术", EngineIcon = "gu_ge_xue_shu" },
            new() { Key = "ggxn", Value = "https://news.google.com/search?q={q}&hl=zh-CN&gl=CN&ceid=CN:zh-Hans", Comment = "谷歌新闻", EngineIcon = "gu_ge_xin_wen" },
            new() { Key = "doubao", Value = "https://www.doubao.com/chat/?q={q}", Comment = "豆包", EngineIcon = "dou_bao" },
            new() { Key = "db2", Value = "https://www.douban.com/search?q={q}", Comment = "豆瓣", EngineIcon = "dou_ban" },
            new() { Key = "dubn", Value = "https://search.douban.com/movie/subject_search?search_text={q}", Comment = "豆瓣电影", EngineIcon = "dou_ban" },
            new() { Key = "dushu", Value = "https://search.douban.com/book/subject_search?search_text={q}", Comment = "豆瓣读书", EngineIcon = "dou_ban" },
            new() { Key = "tiba", Value = "https://tieba.baidu.com/f?kw={q}&ie=utf-8", Comment = "贴吧", EngineIcon = "tie_ba" },
            new() { Key = "qdin", Value = "https://www.qidian.com/so/{q}.html", Comment = "起点中文网", EngineIcon = "qi_dian_zhong_wen_wang" },
            new() { Key = "ltus", Value = "https://www.reuters.com/site-search/?query={q}", Comment = "路透社", EngineIcon = "lu_tou_she" },
            new() { Key = "tongyi", Value = "https://www.tongyi.com/qianwen/?q={q}", Comment = "通义", EngineIcon = "tong_yi" },
            new() { Key = "kn", Value = "https://www.coolapk.com/algo?keyword={q}", Comment = "酷安", EngineIcon = "ku_an" },
            new() { Key = "kwyn", Value = "http://www.kuwo.cn/search/list?type=all&key={q}", Comment = "酷我音乐", EngineIcon = "ku_wo_yin_yue" },
            new() { Key = "si", Value = "http://search.5sing.kugou.com/?keyword={q}", Comment = "酷狗5sing", EngineIcon = "ku_gou_5sing" },
            new() { Key = "jnsh", Value = "https://www.iciba.com/word?w={q}", Comment = "金山词霸", EngineIcon = "jin_shan_ci_ba" },
            new() { Key = "xiny", Value = "https://www.goofish.com/search?q={q}", Comment = "闲鱼", EngineIcon = "xian_yu" },
            new() { Key = "aliyun", Value = "https://developer.aliyun.com/search?q={q}", Comment = "阿里云开发者", EngineIcon = "a_li_yun_kai_fa_zhe" },
            new() { Key = "lynp", Value = "https://www.alipan.com/search?q={q}", Comment = "阿里云盘", EngineIcon = "a_li_yun_pan" },
            new() { Key = "xuqi", Value = "https://xueqiu.com/k?q={q}", Comment = "雪球", EngineIcon = "xue_qiu" },
            new() { Key = "wish", Value = "https://www.merriam-webster.com/dictionary/{q}", Comment = "韦氏词典", EngineIcon = "wei_shi_ci_dian" },
            new() { Key = "godd", Value = "https://www.amap.com/search?query={q}", Comment = "高德地图", EngineIcon = "gao_de_di_tu" },
        };
        /* 启动程序表默认留空：Qbar 已经会从开始菜单搜索应用，
         * 再塞 90 多个 exe 反而把常用结果挤到后面。用户需要固定项时自己加。 */
        s.Run = new List<QEntry>();
        s.Web = BuiltinWebEntries();
        s.HotStrings = new List<QEntry>
        {
            new() { Key = "clp", Value = "CapsLock+", Comment = "展开成 CapsLock+" },
            new() { Key = "@@", Value = "你的邮箱@example.com", Comment = "邮箱" },
        };

        // 键盘层：默认 10 层，第 1 层就是上面的 Keys；触发键默认只有 CapsLock。
        for (int i = 1; i <= 10; i++)
        {
            s.Layers.Add(new LayerOptions
            {
                Id = i,
                Name = i == 1 ? "基础层" : Loc.F("第 {0} 层", i),
                Enabled = true,
                Keys = i == 1 ? s.Keys : new List<KeyBinding>(),
            });
        }
        s.Triggers.Add(new TriggerKey
        {
            Key = "capslock",
            LayerId = 1,
            HoldMs = 200,
            TapAction = s.Global.TapCapsAction,
            DoubleTapAction = s.Global.DoubleTapCaps ? s.Global.DoubleTapCapsAction : "",
            Enabled = true,
        });

        return s;
    }

    /// <summary>Capslox 布局（CapsLock+ 3.0 之后的默认布局）。</summary>
    public static List<KeyBinding> CapsloxScheme() => new()
    {
        B("press_caps", "toggleCapsLock"),

        B("caps_a", "moveWordLeft"), B("caps_b", "moveDown", "10"), B("caps_c", "copy1"),
        B("caps_d", "moveDown"), B("caps_e", "moveUp"), B("caps_f", "moveRight"),
        B("caps_g", "moveWordRight"), B("caps_h", "selectWordLeft"), B("caps_i", "selectUp"),
        B("caps_j", "selectLeft"), B("caps_k", "selectDown"), B("caps_l", "selectRight"),
        B("caps_m", "doNothing"), B("caps_n", "selectCurrentLine"), B("caps_o", "selectEnd"),
        B("caps_p", "home"), B("caps_q", "qbar"), B("caps_r", "delete"),
        B("caps_s", "moveLeft"), B("caps_t", "moveUp", "10"), B("caps_u", "selectHome"),
        B("caps_v", "paste1"), B("caps_w", "backspace"), B("caps_x", "cut1"),
        B("caps_y", "selectUp", "10"), B("caps_z", "doNothing"),

        B("caps_backquote", "doNothing"),

        /* Caps + Shift：Capslox 3.0 的三条（cx-shift-*）。
         *
         *   cx-shift-v = 纯文本粘贴 slot1 —— Keyboard enhance 的 paste1 本来就走
         *                "只写 CF_UNICODETEXT 再 Shift+Insert"，语义完全一致；
         *   cx-shift-, = show_preferences  → openSettings；
         *   cx-shift-/ = edit_keymap       → 没有独立的"键位表"窗口，降级为 openSettings
         *                （按键映射就在设置里，一步之差）。
         *
         * 组合键 id 由 KeyName.BuildComboEx 按 ctrl→shift→lalt→win 拼，
         * 所以"Shift + ," = caps_shift_comma。 */
        B("caps_shift_v", "paste1"),
        B("caps_shift_comma", "openSettings"),
        B("caps_shift_slash", "openSettings"),

        /* 数字键 1~0 的默认绑定。
         *
         * 旧版这里是 winbindActivate1~10（固定 10 个窗口槽位），槽位机制已经
         * 移除，改由「窗口绑定」动作承担。但**默认不能直接填 winBind** ——
         * 它需要一个程序路径参数，空参数的 winBind 按下去只会弹提示。
         * 所以默认留空（doNothing），由用户自己去按键映射页绑程序。 */
        B("caps_1", "doNothing"), B("caps_2", "doNothing"), B("caps_3", "doNothing"),
        B("caps_4", "doNothing"), B("caps_5", "doNothing"), B("caps_6", "doNothing"),
        B("caps_7", "doNothing"), B("caps_8", "doNothing"), B("caps_9", "doNothing"),
        B("caps_0", "doNothing"),
        B("caps_minus", "winSnapLeft"), B("caps_equal", "winSnapRight"),
        B("caps_backspace", "deleteLine"), B("caps_tab", "tabScript"),
        B("caps_leftSquareBracket", "deleteToLineBeginning"), B("caps_rightSquareBracket", "doNothing"),
        B("caps_backslash", "doNothing"), B("caps_semicolon", "end"), B("caps_quote", "doNothing"),
        /* cx-enter = "end enter"：先到行尾再回车（在行中间按回车不会把行劈开）。
         * 这正是 Capslox 3.0 的基础层语义，用按键序列动作原样表达。 */
        B("caps_enter", "customKey", Ck("end enter")), B("caps_comma", "selectCurrentWord"),
        B("caps_dot", "selectWordRight"), B("caps_slash", "deleteToLineEnd"),
        B("caps_space", "enter"), B("caps_ralt", "doNothing"),

        B("caps_f1", "openDocs"), B("caps_f2", "mathBoard"), B("caps_f3", "doNothing"),
        B("caps_f4", "winTransparent"), B("caps_f5", "reload"), B("caps_f6", "winPin"),
        B("caps_f7", "doNothing"), B("caps_f8", "clipboardHistory"), B("caps_f9", "doNothing"),
        B("caps_f10", "doNothing"), B("caps_f11", "doNothing"), B("caps_f12", "toggleClipboard"),

        // Caps + LAlt
        B("caps_lalt_a", "moveWordLeft", "5"), B("caps_lalt_b", "moveDown", "30"), B("caps_lalt_c", "copy2"),
        B("caps_lalt_d", "moveDown", "5"), B("caps_lalt_e", "moveUp", "5"), B("caps_lalt_f", "moveRight", "5"),
        B("caps_lalt_g", "moveWordRight", "5"), B("caps_lalt_h", "selectWordLeft", "5"),
        B("caps_lalt_i", "selectUp", "5"), B("caps_lalt_j", "selectLeft", "5"), B("caps_lalt_k", "selectDown", "5"),
        B("caps_lalt_l", "selectRight", "5"), B("caps_lalt_m", "doNothing"), B("caps_lalt_n", "selectCurrentLine"),
        B("caps_lalt_o", "selectToPageEnd"), B("caps_lalt_p", "moveToPageBeginning"), B("caps_lalt_q", "doNothing"),
        B("caps_lalt_r", "forwardDeleteWord"), B("caps_lalt_s", "moveLeft", "5"), B("caps_lalt_t", "moveUp", "30"),
        B("caps_lalt_u", "selectToPageBeginning"), B("caps_lalt_v", "paste2"),
        /* cx-alt-shift-v = 纯文本粘贴 slot2（Alt+Shift 同时按住时组合键 id 是
         * caps_shift_lalt_v —— BuildComboEx 按 ctrl→shift→lalt→win 拼）。 */
        B("caps_shift_lalt_v", "paste2"), B("caps_lalt_w", "deleteWord"),
        B("caps_lalt_x", "cut2"), B("caps_lalt_y", "selectUp", "30"), B("caps_lalt_z", "doNothing"),

        B("caps_lalt_backquote", "doNothing"),
        /* cx-alt-1..0 曾经是「把当前前台窗口记到槽位 N」（bindWindowSlot）。
         *
         * v1.3.1 起整套窗口绑定管理（10 个槽位 + 那个动作）按用户要求彻底删除：
         * 想用一个键打开某个程序，直接给它绑「直达文件」动作就行，不用先记槽位。
         * 这里退回 doNothing —— 绝不留下指向已删除动作的死键。 */
        B("caps_lalt_1", "doNothing"), B("caps_lalt_2", "doNothing"),
        B("caps_lalt_3", "doNothing"), B("caps_lalt_4", "doNothing"),
        B("caps_lalt_5", "doNothing"), B("caps_lalt_6", "doNothing"),
        B("caps_lalt_7", "doNothing"), B("caps_lalt_8", "doNothing"),
        B("caps_lalt_9", "doNothing"), B("caps_lalt_0", "doNothing"),
        B("caps_lalt_minus", "doNothing"), B("caps_lalt_equal", "doNothing"),
        B("caps_lalt_backspace", "deleteAll"), B("caps_lalt_tab", "doNothing"),
        B("caps_lalt_leftSquareBracket", "deleteToPageBeginning"), B("caps_lalt_rightSquareBracket", "doNothing"),
        B("caps_lalt_backslash", "doNothing"), B("caps_lalt_semicolon", "moveToPageEnd"),
        B("caps_lalt_quote", "doNothing"), B("caps_lalt_enter", "doNothing"),
        B("caps_lalt_comma", "selectCurrentLine"), B("caps_lalt_dot", "selectWordRight", "5"),
        B("caps_lalt_slash", "deleteToPageEnd"), B("caps_lalt_space", "doNothing"),
        B("caps_lalt_ralt", "doNothing"),
        B("caps_lalt_f1", "doNothing"), B("caps_lalt_f2", "doNothing"), B("caps_lalt_f3", "doNothing"),
        B("caps_lalt_f4", "doNothing"), B("caps_lalt_f5", "doNothing"), B("caps_lalt_f6", "doNothing"),
        B("caps_lalt_f7", "doNothing"), B("caps_lalt_f8", "doNothing"), B("caps_lalt_f9", "doNothing"),
        B("caps_lalt_f10", "doNothing"), B("caps_lalt_f11", "doNothing"), B("caps_lalt_f12", "doNothing"),

        B("caps_lalt_wheelUp", "mouseSpeedUp"), B("caps_lalt_wheelDown", "mouseSpeedDown"),

        // Caps + Win（旧版是窗口槽位 1~10，槽位机制已移除）
        B("caps_win_1", "doNothing"), B("caps_win_2", "doNothing"), B("caps_win_3", "doNothing"),
        B("caps_win_4", "doNothing"), B("caps_win_5", "doNothing"), B("caps_win_6", "doNothing"),
        B("caps_win_7", "doNothing"), B("caps_win_8", "doNothing"), B("caps_win_9", "doNothing"),
        B("caps_win_0", "doNothing"),
    };

    /// <summary>经典布局（CapsLock+ 3.0 之前）。</summary>
    public static List<KeyBinding> ClassicScheme()
    {
        var list = CapsloxScheme();
        void Set(string combo, string action, string arg = "")
        {
            var b = list.FirstOrDefault(x => x.Combo == combo);
            if (b == null) list.Add(B(combo, action, arg));
            else { b.Action = action; b.Arg = arg; }
        }
        Set("caps_b", "moveDown", "5");
        Set("caps_m", "selectDown", "5");
        Set("caps_n", "selectWordRight");
        /* 经典方案是 3.0 **之前**的布局：Caps+Enter 原本是「任意位置回车」、
         * Caps+Alt+N 是往下选 30 行。基础表已经升级成 Capslox 3.0 语义，
         * 这两条在这里显式盖回去，免得经典用户跟着一起变。 */
        Set("caps_enter", "enterWherever");
        Set("caps_lalt_n", "selectDown", "30");
        Set("caps_t", "doNothing");
        Set("caps_y", "moveUp", "5");
        Set("caps_z", "undoRedo");
        /* 经典布局里 Caps+` 原是"激活第 9 个窗口槽位"。槽位机制已经整块移除，
         * 那个动作 id（winbindActivate9）在动作表里根本不存在 ——
         * 直接把旧 id 写进默认表，等于给经典方案的用户留了一个
         * "按下去没反应、日志里报未知动作"的死键。 */
        Set("caps_backquote", "doNothing");
        Set("caps_9", "doNothing");
        Set("caps_0", "selectUp", "5");
        Set("caps_minus", "pageUp");
        Set("caps_equal", "pageDown");
        Set("caps_leftSquareBracket", "doNothing");
        Set("caps_rightSquareBracket", "doNothing");
        Set("caps_comma", "doNothing");
        Set("caps_dot", "doNothing");
        Set("caps_slash", "doNothing");
        Set("caps_f3", "mediaNext");
        Set("caps_lalt_a", "winLeft");
        Set("caps_lalt_d", "winDown");
        Set("caps_lalt_e", "winUp");
        Set("caps_lalt_f", "winRight");
        Set("caps_lalt_q", "winCenter");
        Set("caps_lalt_r", "winNextApp");
        Set("caps_lalt_w", "winPrevApp");
        Set("caps_lalt_z", "winSendToBottom");
        Set("caps_lalt_minus", "moveToPageBeginning");
        Set("caps_lalt_equal", "moveToPageEnd");
        Set("caps_lalt_backspace", "backspace");
        return list;
    }

    private static KeyBinding B(string combo, string action, string arg = "") =>
        new() { Combo = combo, Action = action, Arg = arg };

    /// <summary>
    /// 「自定义按键」的参数：给一段好读的按键文本（<c>end enter</c>），
    /// 包成动作参数要的 JSON。
    ///
    /// 出厂默认值也走规范化，保证写入设置文件的形态与用户手填完全一致 ——
    /// 否则同一件事在文件里会出现 `{"Key":"end enter"}` 和 `{"Key":"End Enter"}` 两种。
    /// </summary>
    private static string Ck(string keys) =>
        new InputPlus.Core.Actions.CustomKeySpec
        {
            Key = InputPlus.Core.Actions.CustomKeyRunner.Canonical(keys),
        }.ToArg();

    /// <summary>
    /// 把**第 1 层（基础层）**的绑定重置成指定方案的默认值，其它 9 层不动。
    ///
    /// 保留用户自己加过、预设里没有的组合键 —— 宁可少恢复一条默认值，
    /// 也不能把用户自己攒的组合键清掉。
    ///
    /// ⚠ 必须**就地**改第 1 层那份 List，绝不能写 <c>s.Keys = preset</c>。
    ///
    /// 为什么（这是一次真实的"点了没反应"）：
    /// <c>s.Keys</c> 与 <c>Layers[0].Keys</c> 必须是**同一个 List 实例**
    /// （见 NormalizeLayers 末尾那段）。一旦换掉 s.Keys 的引用：
    ///   · 引擎按层遍历，读的是 Layers[0].Keys —— 旧数据，等于没重置；
    ///   · 界面 CurrentLayerKeys 也优先取 Layers —— 同样看不到变化；
    ///   · 落盘后再读回来，NormalizeLayers 会把 Layers[0].Keys 重新指给
    ///     s.Keys，新预设被彻底丢掉。
    /// 症状就是"点了恢复默认，键盘上什么都没变，重启也没变"。
    /// </summary>
    /// <summary>
    /// 把**指定层**恢复成默认。
    ///
    /// 第 1 层：按内置方案重建（就是原来的 <see cref="ResetLayerOne"/>）；
    /// 其它层：默认配置里那些层本来就是空的，所以"恢复默认"= 清空该层绑定。
    /// 子层（Ctrl/Alt/Shift/Win 子层）有自己的编辑入口，这里不动。
    /// </summary>
    public static void ResetLayer(AppSettings s, int layerId, string scheme)
    {
        if (layerId <= 1)
        {
            ResetLayerOne(s, scheme);
            return;
        }

        var layer = s.Layers.FirstOrDefault(l => l.Id == layerId);
        layer?.Keys.Clear();
    }

    public static void ResetLayerOne(AppSettings s, string scheme)
    {
        s.Global.HotkeyScheme = scheme;

        /* 刚套上一整套预设，Q/R 的默认动作已经在了；标记一下，
         * 免得之后哪次 Normalize 又按"老配置补洞"的逻辑去动它。 */
        s.Global.BaseLayerDefaultsFixed = true;

        /* 刚套上一整套预设，默认表就是最新版本 —— 不置这一位的话，
         * 下次启动迁移会再跑一遍：三元组虽然多半对不上（不会真改坏），
         * 但那是白跑一趟，而且会让"恢复默认"看起来像没生效。 */
        s.CapsloxDefaultsVersion = AppSettings.CapsloxDefaultsVersionCurrent;

        var preset = scheme == "capslock_plus" ? ClassicScheme() : CapsloxScheme();

        var layer1 = s.Layers.FirstOrDefault(l => l.Id == 1);
        if (layer1 == null)
        {
            layer1 = new LayerOptions { Id = 1, Name = "基础层" };
            s.Layers.Insert(0, layer1);
        }
        if (layer1.Keys.Count == 0 && s.Keys.Count > 0) layer1.Keys = s.Keys;

        var target = layer1.Keys;
        s.Keys = target;                       // 同源，别留两份分叉的 List

        foreach (var custom in target.Where(k => preset.All(p =>
                     !p.Combo.Equals(k.Combo, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            preset.Add(custom);
        }

        target.Clear();
        target.AddRange(preset);
    }
}
