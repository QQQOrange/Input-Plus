using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

#nullable disable

namespace InputPlus.Core.Qbar.Search;

/// <summary>
/// 一条 Everything 命中。Day 是自 2000-01-01 起的天数，与索引快照同一口径。
/// <para>
/// Highlights 是"文件名里命中的区间"，交错存 [起点, 长度, 起点, 长度…]。
/// 由 Everything 自己给出（EVERYTHING_REQUEST_HIGHLIGHTED_FILE_NAME），不是我们猜的 ——
/// 正则、通配符、布尔查询里它才知道到底命中了哪几个字符。
/// </para>
/// </summary>
internal readonly record struct EverythingHit(string Path, bool IsDir, long SizeBytes, int Day, int[] Highlights = null);

/// <summary>
/// Everything SDK 的进程内客户端（Everything64.dll / Everything32.dll）。
///
/// 为什么用 NativeLibrary + 委托，而不是 static DllImport：
///   · DllImport 是按名字在加载时绑定，机器上没装 Everything 时第一次调用就抛
///     DllNotFoundException，而这个类会被界面、CLI、自检同时碰到，抛一次就要到处 try；
///   · 用 TryLoad + TryGetExport 可以"探测式"初始化：找不到就安静地降级到自建索引，
///     找到哪个函数就用哪个，缺函数只影响对应能力（比如老版本没有 GetResultDateCreated）。
///
/// 线程模型：SDK 的查询状态是**进程级全局变量**（SetSearch 与 Query 必须成对），
/// 所以这里所有调用都在同一把锁里，调用方负责把查询放到后台线程（SDK 的 Query 是同步阻塞的，
/// 而且没有取消接口 —— 详见 DECISIONS.md D18）。
/// </summary>
internal static class EverythingApi
{
    private const uint RequestFileName = 0x00000001;
    private const uint RequestFullPath = 0x00000004;
    private const uint RequestSize = 0x00000010;
    private const uint RequestDateModified = 0x00000040;
    /* 高亮区间：Everything 会把命中的片段用 \x01 … \x02 包起来返回（见 Everything.h 的
       Everything_GetResultHighlightedFileNameW）。这一条能省掉我们"按关键词子串再猜一遍"的高亮，
       而且在正则/通配符查询下只有它是对的。 */
    private const uint RequestHighlightedFileName = 0x00002000;

    /// <summary>Everything 的 dm: 与我们的 MinDay/MaxDay 都要用到修改时间，按它降序取候选窗口最稳。</summary>
    private const uint SortDateModifiedDescending = 14;

    private static readonly object Gate = new object();
    private static readonly object InitGate = new object();
    private static bool _tried;
    private static bool _available;
    private static string _dllPath = "";
    private static string _loadError = "";
    private static int _lastErrorCode;
    private static string _lastErrorText = "";

    private static SetSearchW _setSearch = null!;
    private static QueryW _query = null!;
    private static SetUInt _setRequestFlags = null!;
    private static SetUInt _setSort = null!;
    private static SetUInt _setMax = null!;
    private static SetUInt _setOffset = null!;
    private static SetUInt _setMatchPath = null!;
    private static SetUInt _setMatchCase = null!;
    private static SetUInt _setWholeWord = null!;
    private static SetUInt _setRegex = null!;
    private static GetNumResults _getNumResults = null!;
    private static GetNumResults _getTotResults = null!;
    private static GetFullPathNameW _getFullPathName = null!;
    private static GetResultSize _getResultSize = null!;
    private static GetResultDateModified _getResultDateModified = null!;
    private static GetResultNameW _getResultHighlightedName = null!;
    private static IsIndexResult _isFolderResult = null!;
    private static GetUInt _getLastError = null!;
    private static GetUInt _isDbLoaded = null!;
    private static GetUInt _isAdmin = null!;
    private static GetUInt _getMajor = null!;
    private static GetUInt _getMinor = null!;
    private static GetUInt _getRevision = null!;
    private static VoidCall _reset = null!;
    private static VoidCall _cleanUp = null!;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate void SetSearchW(string text);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryW(int wait);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetUInt(uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetNumResults();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetUInt();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void VoidCall();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetFullPathNameW(uint index, IntPtr buffer, uint maxChars);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetResultSize(uint index, out long size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetResultDateModified(uint index, out long fileTime);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsIndexResult(uint index);

    /* LPCWSTR 返回值：是 SDK 内部缓冲的指针，只在本次 Query 的结果有效，
       必须立刻拷成托管字符串，不能留着跨查询用。 */
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate IntPtr GetResultNameW(uint index);

    /// <summary>
    /// 解析 Everything 的"带标记文件名"：命中片段用 U+0001 开头、U+0002 结尾。
    /// 返回交错区间 [起点, 长度, …]，下标是**去掉标记之后**的文件名偏移量。
    ///
    /// 解析失败的形态都当"没有高亮"处理：宁可不加亮，也不能让名字里冒出控制字符。
    /// </summary>
    internal static int[] ParseHighlightedName(string marked)
    {
        if (string.IsNullOrEmpty(marked)) return Array.Empty<int>();
        if (marked.IndexOf('\u0001') < 0 && marked.IndexOf('\u0002') < 0) return Array.Empty<int>();

        var pairs = new List<int>(8);
        var plain = new StringBuilder(marked.Length);
        int start = -1;
        for (int i = 0; i < marked.Length; i++)
        {
            char c = marked[i];
            if (c == '\u0001')
            {
                start = plain.Length;
                continue;
            }
            if (c == '\u0002')
            {
                if (start >= 0 && plain.Length > start)
                {
                    pairs.Add(start);
                    pairs.Add(plain.Length - start);
                }
                start = -1;
                continue;
            }
            plain.Append(c);
        }
        return pairs.Count == 0 ? Array.Empty<int>() : pairs.ToArray();
    }

    public static bool IsAvailable
    {
        get { EnsureLoaded(); return _available; }
    }

    public static string DllPath { get { EnsureLoaded(); return _dllPath; } }

    /// <summary>上一次 <c>Everything_QueryW</c> 自己的耗时（毫秒）。端到端变慢时靠它区分是 SDK 慢还是我们慢。</summary>
    public static long LastQueryMs { get; private set; }

    /// <summary>给界面/诊断用的一句话状态：能用就报版本与库状态，不能用就报原因。</summary>
    public static string StatusText
    {
        get
        {
            EnsureLoaded();
            if (!_available) return _loadError.Length == 0 ? "Everything 未安装（找不到 Everything64.dll）" : _loadError;
            /* 版本号是"连上了没有"的最好判据：Everything 没在运行时 GetMajorVersion 返回 0，
             * 于是版本是 0.0.0。DLL 加载成功 ≠ 客户端在跑 —— 这两种状态混在一句话里，
             * 用户会以为"已就绪"，然后纳闷为什么搜不到东西。 */
            string version = VersionText;
            if (!IsClientRunning) return "Everything 客户端没在运行（SDK DLL 已加载：" + Path.GetFileName(_dllPath) + "）";
            if (!IsDbLoaded) return "Everything " + version + " 正在建立索引（索引库未加载）";
            return "Everything " + version + (IsAdmin ? "（管理员）" : "") + " 索引已加载";
        }
    }

    public static string VersionText
    {
        get
        {
            EnsureLoaded();
            if (!_available) return "?";
            try { return _getMajor() + "." + _getMinor() + "." + _getRevision(); }
            catch { return "?"; }
        }
    }

    /// <summary>Everything 客户端是否在跑（能连上 IPC）。DLL 加载成功不代表客户端在运行。</summary>
    public static bool IsClientRunning
    {
        get
        {
            EnsureLoaded();
            if (!_available) return false;
            if (_getMajor == null) return true;
            try { return _getMajor() > 0; } catch { return false; }
        }
    }

    public static bool IsDbLoaded
    {
        get { EnsureLoaded(); return _available && SafeInt(_isDbLoaded) != 0; }
    }

    public static bool IsAdmin
    {
        get { EnsureLoaded(); return _available && SafeInt(_isAdmin) != 0; }
    }

    public static bool IsProcessElevated
    {
        get { return Core.ElevationHelper.IsElevated; }
    }

    public static string LastErrorText { get { EnsureLoaded(); return _lastErrorText; } }

    private static int SafeInt(GetUInt fn)
    {
        try { return (int)fn(); } catch { return 0; }
    }

    /// <summary>只在第一次调用时探测一遍；失败结论会缓存，避免每个查询都去摸一遍磁盘。</summary>
    private static void EnsureLoaded()
    {
        if (_tried) return;
        lock (InitGate)
        {
            if (_tried) return;
            _tried = true;
            try { Load(); }
            catch (Exception ex)
            {
                _available = false;
                _loadError = "Everything SDK 初始化失败：" + ex.Message;
                CoreLog.Warn(_loadError);
            }
        }
    }

    /* 排序模式可在跑基准时用环境变量覆盖：Sort 决定"取哪 2000 条候选"，
     * 而 Everything 按 DATE_MODIFIED 排需要把全部命中读出来，按 NAME 排走的是索引顺序，
     * 两者延迟差一个数量级。默认取值见 DECISIONS.md D22。 */
    private static uint SortMode
    {
        get
        {
            string raw = Environment.GetEnvironmentVariable("INPUTPLUS_EVERYTHING_SORT");
            if (!string.IsNullOrWhiteSpace(raw) && uint.TryParse(raw, out uint custom) && custom >= 1 && custom <= 26) return custom;
            return SortDateModifiedDescending;
        }
    }

    private static void Load()
    {
        /* 排障与基准用的总闸：设了 INPUTPLUS_DISABLE_EVERYTHING=1 就当作没装。
         * 有了它才能在同一台机器上跑「E 后端 vs 本地索引」的对照，而不用改设置文件。 */
        string off = Environment.GetEnvironmentVariable("INPUTPLUS_DISABLE_EVERYTHING");
        if (!string.IsNullOrWhiteSpace(off) && (off == "1" || off.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            _available = false;
            _loadError = "Everything 后端已在环境变量里关闭（INPUTPLUS_DISABLE_EVERYTHING=1）";
            return;
        }

        foreach (string candidate in CandidatePaths())
        {
            if (candidate.Length == 0 || !File.Exists(candidate)) continue;
            IntPtr module;
            if (!NativeLibrary.TryLoad(candidate, out module)) continue;
            if (!Bind(module))
            {
                NativeLibrary.Free(module);
                continue;
            }
            _dllPath = candidate;
            _available = true;
            _loadError = "";
            CoreLog.Info("Everything SDK 已加载：" + candidate +
                         (IsBundledPath(candidate) ? "（内置）" : "") + "（" + VersionText + "）");
            return;
        }
        _available = false;
        _loadError = "找不到 Everything SDK（内置副本释放失败，程序目录与系统里也没有）";
    }

    /// <summary>这个 DLL 是不是从程序内置副本释放出来的（界面要说清是"内置版"还是"外面装的"）。</summary>
    public static bool IsBundledPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string folder = Path.GetFullPath(EverythingBundle.Folder).TrimEnd('\\') + "\\";
            return Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>DLL 来源的一句话说明（设置页用）。</summary>
    public static string SourceText
    {
        get
        {
            EnsureLoaded();
            if (!_available) return Loc.T("没有可用的 Everything SDK");
            return IsBundledPath(_dllPath) ? Loc.T("内置") : Loc.T("系统已安装");
        }
    }

    /// <summary>
    /// 候选路径：环境变量 → 程序目录 → 内置副本 → 官方安装位置。
    ///
    /// 顺序有两处是刻意的：
    ///   · 环境变量排第一：排障和基准要能强制指定某一版 DLL，别的候选不能抢；
    ///   · 内置副本排在"已安装的 Everything"之前：内置的是我们验证过的那一版，
    ///     用户机器上装过什么版本不可控（版本错配会让导出的函数签名对不上）。
    /// </summary>
    private static IEnumerable<string> CandidatePaths()
    {
        string overridePath = Environment.GetEnvironmentVariable("INPUTPLUS_EVERYTHING_DLL");
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath;

        string appDir = AppContext.BaseDirectory;
        yield return Path.Combine(appDir, Environment.Is64BitProcess ? "Everything64.dll" : "Everything32.dll");
        yield return Path.Combine(appDir, "Everything64.dll");
        yield return Path.Combine(appDir, "Everything32.dll");

        /* 内置副本：第一次枚举到这里时才会把资源释放到数据目录（见 EverythingBundle）。
           释放失败就返回空串，后面的候选照常走 —— 不能因为内置文件坏了就完全不搜。 */
        string bundled = EverythingBundle.DllPath;
        if (!string.IsNullOrWhiteSpace(bundled)) yield return bundled;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "Everything64.dll");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "Everything64.dll");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "Everything32.dll");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Everything", "Everything64.dll");
    }

    /// <summary>逐个导出解析；核心函数缺一个就判定这个 DLL 不可用（半残的客户端比没有更糟）。</summary>
    private static bool Bind(IntPtr module)
    {
        try
        {
            _setSearch = Get<SetSearchW>(module, "Everything_SetSearchW");
            _query = Get<QueryW>(module, "Everything_QueryW");
            if (_query == null) _query = Get<QueryW>(module, "Everything_Query");
            _setRequestFlags = Get<SetUInt>(module, "Everything_SetRequestFlags");
            _setSort = Get<SetUInt>(module, "Everything_SetSort");
            _setMax = Get<SetUInt>(module, "Everything_SetMax");
            _setOffset = Get<SetUInt>(module, "Everything_SetOffset");
            _setMatchPath = Get<SetUInt>(module, "Everything_SetMatchPath");
            _setMatchCase = Get<SetUInt>(module, "Everything_SetMatchCase");
            _setWholeWord = Get<SetUInt>(module, "Everything_SetMatchWholeWord");
            _setRegex = Get<SetUInt>(module, "Everything_SetRegex");
            _getNumResults = Get<GetNumResults>(module, "Everything_GetNumResults");
            _getTotResults = Get<GetNumResults>(module, "Everything_GetTotResults");
            _getFullPathName = Get<GetFullPathNameW>(module, "Everything_GetResultFullPathNameW");
            _getResultSize = Get<GetResultSize>(module, "Everything_GetResultSize");
            _getResultDateModified = Get<GetResultDateModified>(module, "Everything_GetResultDateModified");
            _getResultHighlightedName = Get<GetResultNameW>(module, "Everything_GetResultHighlightedFileNameW");
            _isFolderResult = Get<IsIndexResult>(module, "Everything_IsFolderResult");
            _getLastError = Get<GetUInt>(module, "Everything_GetLastError");
            _isDbLoaded = Get<GetUInt>(module, "Everything_IsDBLoaded");
            _isAdmin = Get<GetUInt>(module, "Everything_IsAdmin");
            _getMajor = Get<GetUInt>(module, "Everything_GetMajorVersion");
            _getMinor = Get<GetUInt>(module, "Everything_GetMinorVersion");
            _getRevision = Get<GetUInt>(module, "Everything_GetRevision");
            _reset = Get<VoidCall>(module, "Everything_Reset");
            _cleanUp = Get<VoidCall>(module, "Everything_CleanUp");
            return _setSearch != null && _query != null && _getNumResults != null && _getFullPathName != null;
        }
        catch (Exception ex)
        {
            _loadError = "Everything SDK 导出解析失败：" + ex.Message;
            return false;
        }
    }

    private static T Get<T>(IntPtr module, string name) where T : Delegate
    {
        IntPtr export;
        if (!NativeLibrary.TryGetExport(module, name, out export) || export == IntPtr.Zero) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    /// <summary>
    /// 跑一次查询。返回 false 表示 Everything 这条路过不去（没装 / 没运行 / 索引没加载 / 查询出错），
    /// 调用方应当降级到自建索引并把 detail 显示给用户。
    /// </summary>
    /// <param name="regexMode">
    /// 整条查询按正则解释（<c>Everything_SetRegex(TRUE)</c>）。这个开关是**进程级全局**的，
    /// 而且会把查询里的每个词都当正则——所以只允许"查询串本身就是一条正则"时打开，
    /// 由调用方（QbarEverythingBackend）保证，别在外面随手传 true。
    /// </param>
    /// <param name="matchPath">
    /// 每个词是否也允许命中路径。TRUE 召回更全（与本地索引一致），代价是本机 115 万条索引上
    /// 实测多花 20–25ms（p95 34.7ms → 56.0ms）。调用方先用 FALSE 拿一窗"名字命中"，
    /// 结果太稀再补一次 TRUE，见 QbarEverythingBackend.TryRun。
    /// </param>
    public static bool TrySearch(string query, int maxResults, out List<EverythingHit> hits, out string detail,
        bool regexMode = false, bool matchPath = true)
    {
        hits = new List<EverythingHit>();
        detail = "";
        EnsureLoaded();
        if (!_available)
        {
            detail = StatusText;
            return false;
        }

        /* SDK 的查询状态是进程级全局的：两个线程各 SetSearch 一次，后一个会把前一个的
         * 条件覆盖掉，拿回来的结果是别人的。整段加锁，别图省事。 */
        lock (Gate)
        {
            try
            {
                if (_reset != null) _reset();
                _setSearch(query ?? "");
                if (_setRequestFlags != null)
                    _setRequestFlags(RequestFileName | RequestFullPath | RequestSize | RequestDateModified
                                     | (_getResultHighlightedName != null ? RequestHighlightedFileName : 0));
                /* 这些 setter 在老版本 SDK 里可能缺失（Everything 1.3 没有 SetSort/SetRequestFlags）。
                 * 缺了就不设，退回 SDK 默认值——比直接 NullReference 崩掉强。 */
                if (_setMax != null) _setMax((uint)Math.Max(1, Math.Min(20000, maxResults)));
                if (_setOffset != null) _setOffset(0);
                if (_setSort != null) _setSort(SortMode);
                /* MatchPath=TRUE：自建索引的语义是"每个词出现在文件名或所在目录里"，
                 * Everything 默认只匹配文件名，不开这个开关两条路的召回集合会明显不同。 */
                if (_setMatchPath != null) _setMatchPath((uint)(matchPath && !regexMode ? 1 : 0));
                if (_setMatchCase != null) _setMatchCase(0);
                if (_setWholeWord != null) _setWholeWord(0);
                if (_setRegex != null) _setRegex((uint)(regexMode ? 1 : 0));

                var clock = System.Diagnostics.Stopwatch.StartNew();
                int ok = _query(1);
                clock.Stop();
                LastQueryMs = (long)clock.Elapsed.TotalMilliseconds;
                if (ok == 0)
                {
                    detail = DescribeLastError();
                    return false;
                }

                uint count = _getNumResults();
                IntPtr buffer = Marshal.AllocHGlobal(32768 * 2);
                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        /* 返回实际写入的字符数（不含结尾 NUL）；0 = 这一条取不到路径，跳过。
                         * 缓冲区给到 32768 wchar：Everything 的长路径上限是 32767，够用。 */
                        if (_getFullPathName(i, buffer, 32768) == 0) continue;
                        string path = Marshal.PtrToStringUni(buffer) ?? "";
                        if (path.Length == 0) continue;
                        bool isDir = _isFolderResult != null && _isFolderResult(i) != 0;
                        long size = 0;
                        if (_getResultSize != null) _getResultSize(i, out size);
                        long fileTime = 0;
                        if (_getResultDateModified != null) _getResultDateModified(i, out fileTime);
                        int[] marks = null;
                        if (_getResultHighlightedName != null)
                        {
                            IntPtr marked = _getResultHighlightedName(i);
                            if (marked != IntPtr.Zero)
                            {
                                string text = Marshal.PtrToStringUni(marked);
                                if (!string.IsNullOrEmpty(text)) marks = ParseHighlightedName(text);
                            }
                        }
                        hits.Add(new EverythingHit(path, isDir, size, ToDay(fileTime), marks));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                /* TotResults 是"这次查询的总命中数"，NumResults 才是被 SetMax 截断后的窗口大小。
                 * 调试面板必须两个都给：只报窗口大小会让人误以为搜索引擎只找到这么点东西。 */
                uint total = _getTotResults != null ? _getTotResults() : count;
                detail = StatusText + " · 命中 " + count + " 条" + (total > count ? "（总 " + total + "，窗口已截断）" : "");
                return true;
            }
            catch (Exception ex)
            {
                detail = "Everything 查询异常：" + ex.Message;
                CoreLog.Swallow(ex, "Everything 查询");
                return false;
            }
        }
    }

    private static int ToDay(long fileTime)
    {
        if (fileTime <= 0) return -1;
        try
        {
            DateTime time = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
            return QbarSyntaxValues.ToDay(time);
        }
        catch { return -1; }
    }

    private static string DescribeLastError()
    {
        uint code = 0;
        try { if (_getLastError != null) code = _getLastError(); } catch { }
        _lastErrorCode = (int)code;
        switch (code)
        {
            /* 编号照抄 Everything.h 的 EVERYTHING_ERROR_*（0..9）。
             * 这里最容易出的错是"文案对不上号"—— 比如把 6 说成调用顺序，
             * 实际 6 是 INVALIDINDEX；用户按提示去查只会查到错的方向。 */
            case 0: _lastErrorText = "Everything 报告无错误（返回码却是失败）"; break;
            case 1: _lastErrorText = "Everything 内存不足"; break;
            case 2: _lastErrorText = "Everything 没在运行（IPC 连接失败）"; break;
            case 3: _lastErrorText = "Everything 注册窗口类失败"; break;
            case 4: _lastErrorText = "Everything 创建监听窗口失败"; break;
            case 5: _lastErrorText = "Everything 创建监听线程失败"; break;
            case 6: _lastErrorText = "Everything 结果下标越界"; break;
            case 7: _lastErrorText = "Everything 调用顺序不对（SetSearch 与 Query 必须成对）"; break;
            case 8: _lastErrorText = "Everything 请求数据无效（SetRequestFlags 必须在 Query 之前）"; break;
            case 9: _lastErrorText = "Everything 参数无效"; break;
            default: _lastErrorText = "Everything 错误码 " + code; break;
        }
        return _lastErrorText;
    }

    /// <summary>退出前调用：让 Everything 客户端释放它注册的窗口与线程。</summary>
    public static void Shutdown()
    {
        if (!_available) return;
        lock (Gate)
        {
            try { if (_cleanUp != null) _cleanUp(); }
            catch (Exception ex) { CoreLog.Swallow(ex, "Everything 清理"); }
        }
    }
}