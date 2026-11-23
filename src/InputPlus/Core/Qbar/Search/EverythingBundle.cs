using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace InputPlus.Core.Qbar.Search;

/// <summary>
/// 内置 Everything：把 SDK 的 x86/x64 DLL 与 Everything 客户端一起打进程序集，
/// 首次用到时释放到数据目录的 everything\ 子目录。
///
/// 为什么要内置：Everything 的 SDK 是**进程外**的薄封装 —— Everything64.dll 只是
/// 拿着 Everything 的窗口句柄做 IPC，所以用户机器上必须同时有 DLL 和客户端进程。
/// 光把 DLL 拷到程序目录还不够：客户端没装的人搜出来永远是空的（本机就是这样，
/// 用户看到的现象是"展开窗口搜不出东西"）。
///
/// 设计取舍：
///   · 用 EmbeddedResource 而不是 Content 文件 —— 发布形态里有单文件 exe，
///     磁盘上没有独立文件可拷；只有嵌进程序集才读得到。
///   · 释放目录放在数据目录（AppPaths.DataDir）而不是 %TEMP%：Everything 的
///     索引库（Everything.db，动辄几十上百 MB）就落在 exe 旁边，放临时目录
///     会被清理工具删掉，下次又要重建全盘索引。
///   · 只覆盖写"大小不一致"的文件：用户手工替换过版本时不至于每次启动都改回去，
///     同时升级内置版本时又能自动换新。
/// </summary>
internal static class EverythingBundle
{
    public const string FolderName = "everything";
    public const string ClientExeName = "Everything.exe";
    public const string Dll64Name = "Everything64.dll";
    public const string Dll32Name = "Everything32.dll";

    private static readonly object Gate = new();
    private static bool _ensured;
    private static string _error = "";

    /// <summary>内置客户端与索引库所在目录（数据目录下的 everything\）。</summary>
    public static string Folder => Path.Combine(AppPaths.DataDir, FolderName);

    /// <summary>内置 Everything 客户端路径（解压后）。</summary>
    public static string ClientPath => Path.Combine(Folder, ClientExeName);

    /// <summary>当前进程位数对应的 SDK DLL 名称。</summary>
    public static string DllName => Environment.Is64BitProcess ? Dll64Name : Dll32Name;

    /// <summary>内置 SDK DLL 路径（解压后）；没内置或解压失败时返回空串。</summary>
    public static string DllPath
    {
        get
        {
            return Ensure() ? Path.Combine(Folder, DllName) : "";
        }
    }

    /// <summary>上一次 Ensure 失败的原因（给状态卡显示）。</summary>
    public static string LastError { get { return _error; } }

    public static bool IsReady
    {
        get { return Ensure() && File.Exists(ClientPath); }
    }

    /// <summary>把内置文件释放到磁盘。可重复调用，只做一次实际 IO。</summary>
    public static bool Ensure()
    {
        lock (Gate)
        {
            if (_ensured) return _error.Length == 0;
            _ensured = true;
            try
            {
                Directory.CreateDirectory(Folder);
                /* 32 位进程也要释放 64 位 DLL 吗？不用 —— 只有同位数的那份能被加载。
                   但客户端 exe 只有一个（x64），32 位进程同样能启动它（跨位数只限制
                   把 32 位模块加载进 64 位进程，启动新进程不受限）。
                   v1.5.2：嵌进程序集的是 .gz（见 csproj 里那段说明），这里解压。
                   三份要么全在、要么重解一遍 —— 用戳记文件判断"这版装好了没"，
                   不再用"文件大小相等"（压缩后大小对不上原文件）。 */
                if (!IsBundleUpToDate())
                {
                    Extract("Everything64.dll.gz", Dll64Name);
                    Extract("Everything32.dll.gz", Dll32Name);
                    Extract("Everything.exe.gz", ClientExeName);
                    WriteStamp();
                }

                EnsureIni();
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                CoreLog.Swallow(ex, "释放内置 Everything");
            }
            return _error.Length == 0;
        }
    }

    /// <summary>戳记文件：记"哪个程序集版本 + 哪三份压缩资源"已经解出来了。</summary>
    private const string StampName = ".bundle";

    private static string StampPath => Path.Combine(Folder, StampName);

    private static bool IsBundleUpToDate()
    {
        try
        {
            if (!File.Exists(StampPath)) return false;
            if (File.ReadAllText(StampPath).Trim() != BundleStamp()) return false;
            return File.Exists(Path.Combine(Folder, Dll64Name))
                && File.Exists(Path.Combine(Folder, Dll32Name))
                && File.Exists(ClientPath);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "内置 Everything：读戳记");
            return false;
        }
    }

    private static void WriteStamp()
    {
        try { File.WriteAllText(StampPath, BundleStamp()); }
        catch (Exception ex) { CoreLog.Swallow(ex, "内置 Everything：写戳记"); }
    }

    /// <summary>
    /// 当前这版程序集对应的戳记：程序集版本 + 三份资源的长度。
    /// 升级（换过程序集）或重新打包（资源长度变了）都会自动重解。
    /// </summary>
    private static string BundleStamp()
    {
        Assembly assembly = typeof(EverythingBundle).Assembly;
        var sb = new StringBuilder(64);
        sb.Append(assembly.GetName().Version).Append('|');
        foreach (string name in PackedResourceNames)
        {
            using Stream? source = assembly.GetManifestResourceStream("InputPlus.Assets.Everything." + name);
            sb.Append(source?.Length ?? -1).Append('|');
        }
        return sb.ToString();
    }

    private static readonly string[] PackedResourceNames =
    {
        "Everything64.dll.gz", "Everything32.dll.gz", "Everything.exe.gz",
    };

    /// <summary>
    /// 维护内置客户端的便携配置。
    ///
    /// 必须**每次启动都对账**，不能"文件存在就跳过"：这几项写错会让内置客户端
    /// 悄悄去用 %APPDATA%\Everything 的全局配置与索引库 —— 既违背"别碰用户
    /// 已装的 Everything"，也会让索引与界面状态对不上。文件里别的键（用户手工
    /// 调过的列宽之类）原样保留，只覆盖我们自己负责的那几项。
    /// </summary>
    private static void EnsureIni()
    {
        string ini = Path.Combine(Folder, "Everything.ini");
        var wanted = new (string Key, string Value)[]
        {
            ("portable", "1"),                                        // 配置与索引库都放本目录
            ("run_as_admin", Core.ElevationHelper.IsElevated ? "1" : "0"),
            ("minimized", "1"),
            ("show_tray_icon", "0"),
            ("check_for_updates_on_startup", "0"),
            ("auto_remove_missing_files", "1"),
        };

        var lines = File.Exists(ini)
            ? new List<string>(File.ReadAllLines(ini))
            : new List<string> { "; Input Plus 内置 Everything 的便携配置", "[Everything]" };
        if (!lines.Any(l => l.Trim().StartsWith("[", StringComparison.Ordinal)))
        {
            lines.Insert(0, "[Everything]");
        }

        bool changed = false;
        foreach ((string key, string value) in wanted)
        {
            int index = lines.FindIndex(l =>
            {
                string t = l.TrimStart();
                return t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase);
            });
            string line = key + "=" + value;
            if (index < 0) { lines.Add(line); changed = true; }
            else if (lines[index].Trim() != line) { lines[index] = line; changed = true; }
        }

        if (changed) File.WriteAllLines(ini, lines);
    }

    /// <summary>
    /// 解一份 gz 出来。资源名 = <c>InputPlus.Assets.Everything.&lt;文件名&gt;.gz</c>。
    ///
    /// 先写 .tmp 再改名：Everything 客户端正在运行时原文件被占用，
    /// 这一步失败就保留旧的继续用（不影响本轮搜索），绝不把半截文件留在原位。
    /// </summary>
    private static void Extract(string resourceName, string fileName)
    {
        string target = Path.Combine(Folder, fileName);
        Assembly assembly = typeof(EverythingBundle).Assembly;
        using Stream? packed = assembly.GetManifestResourceStream("InputPlus.Assets.Everything." + resourceName);
        if (packed == null)
        {
            /* 资源没打进程序集（例如自检用的精简构建）：安静跳过，
               让上层回落到"已安装的 Everything"那条路。 */
            return;
        }

        string temp = target + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(packed, CompressionMode.Decompress))
            {
                gz.CopyTo(file);
            }

            /* 客户端正在运行的时候替换 exe 会失败（文件被占用）——
               那就保留旧文件继续用，不影响本轮搜索。 */
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }
        catch (IOException)
        {
            TryDelete(temp);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temp);
        }
        catch (InvalidDataException ex)
        {
            /* 包坏了（截断 / 不是 gzip）：别让整个 Ensure 崩掉，留一条能查的日志。 */
            TryDelete(temp);
            _error = "内置 " + fileName + " 解压失败：" + ex.Message;
            CoreLog.Swallow(ex, "内置 Everything：解压 " + fileName);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>内置客户端的版本说明（设置页与诊断用）。</summary>
    public static string Describe()
    {
        if (!Ensure()) return Loc.T("内置 Everything 不可用：") + _error;
        if (!File.Exists(ClientPath)) return Loc.T("程序里没有内置 Everything 客户端");
        return Loc.F("已内置 Everything 客户端与 SDK（{0}）", Folder);
    }
}
