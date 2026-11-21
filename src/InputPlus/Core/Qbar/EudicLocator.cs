using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 找欧路词典装在哪。
///
/// 为什么不能只写死 Program Files：实测用户把欧路装到了 D:\software\eudic，
/// 于是"查词"永远走网页兜底，看着像没实现。安装路径是用户自由选的，唯一可靠
/// 的线索是它自己写进系统的三样东西：开始菜单快捷方式、App Paths 注册表项、
/// PATH。三样都查，查不到才认输（认输=开网页词典）。
/// </summary>
internal static class EudicLocator
{
    /// <summary>欧路各版本用过的进程名。新版是 eudic.exe，老版是 eudic_win.exe。</summary>
    internal static readonly string[] ExeNames = { "eudic.exe", "eudic_win.exe" };

    /// <summary>找到就返回完整路径，找不到返回 null。</summary>
    internal static string? Find() => Find(null);

    /// <summary>测试用：允许注入"开始菜单根目录"，避免断言跑在真机上。</summary>
    internal static string? Find(string? startMenuRoot)
    {
        return FromPath() ?? FromAppPaths() ?? FromStartMenu(startMenuRoot) ?? FromKnownDirs();
    }

    /// <summary>
    /// 排障用：把每一层找到什么打出来。「查词不生效」时用户只看到网页词典打开，
    /// 光看现象猜不出是没装、还是装到了非默认目录 —— 这张表直接给答案。
    /// </summary>
    internal static string Describe()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        string? byPath = FromPath();
        string? byAppPaths = byPath == null ? FromAppPaths() : null;
        string? byMenu = byPath == null && byAppPaths == null ? FromStartMenu(null) : null;
        string? byDirs = byPath == null && byAppPaths == null && byMenu == null ? FromKnownDirs() : null;

        sb.AppendLine("PATH: " + (byPath ?? "没找到"));
        sb.AppendLine("App Paths 注册表: " + (byPath != null ? "（上一层已命中，跳过）" : byAppPaths ?? "没找到"));
        sb.AppendLine("开始菜单快捷方式: " + (byPath != null || byAppPaths != null ? "（上一层已命中，跳过）" : byMenu ?? "没找到"));
        sb.AppendLine("常见安装目录: " + (byPath != null || byAppPaths != null || byMenu != null ? "（上一层已命中，跳过）" : byDirs ?? "没找到"));
        sb.AppendLine("最终使用: " + (Find() ?? "没找到（查词时回落到网页词典）"));
        return sb.ToString().TrimEnd();
    }

    /// <summary>快捷方式名/文件名看着像欧路吗（eudic / 欧路）。</summary>
    internal static bool LooksLikeEudic(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf("eudic", StringComparison.OrdinalIgnoreCase) >= 0
               || name.IndexOf("欧路", StringComparison.Ordinal) >= 0;
    }

    /// <summary>这个路径是不是一个存在的欧路可执行文件。</summary>
    internal static bool IsEudicExe(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string name = Path.GetFileName(path);
        foreach (string exe in ExeNames)
        {
            if (string.Equals(name, exe, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return true;
        }
        return false;
    }

    private static string? FromPath()
    {
        try
        {
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar)) return null;
            foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string exe in ExeNames)
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { /* PATH 里可能有非法字符的目录，跳过 */ }
                }
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "扫描 PATH 找欧路词典"); }
        return null;
    }

    /// <summary>
    /// 注册表 App Paths：装过欧路的机器上通常有
    /// HKLM/HKCU \ SOFTWARE \ Microsoft \ Windows \ CurrentVersion \ App Paths \ eudic.exe
    /// 键的默认值就是完整路径。32/64 位视图各查一遍，32 位进程才看得到 32 位装的。
    /// </summary>
    private static string? FromAppPaths()
    {
        const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (string exe in ExeNames)
                {
                    try
                    {
                        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using RegistryKey? key = baseKey.OpenSubKey(sub + exe);
                        if (key?.GetValue(null) is string value && IsEudicExe(value.Trim('"'))) return value.Trim('"');
                    }
                    catch (Exception ex) { CoreLog.Swallow(ex, "读 App Paths 找欧路词典"); }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 开始菜单快捷方式：覆盖"装在任意盘任意目录"的情况——安装程序一定会往开始菜单
    /// 放一个「欧路词典 eudic.lnk」。解析出目标就是真实安装路径。
    /// </summary>
    private static string? FromStartMenu(string? root)
    {
        List<string> roots = new List<string>();
        if (!string.IsNullOrEmpty(root)) roots.Add(root);
        else
        {
            roots.Add(Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\Windows\Start Menu\Programs"));
            roots.Add(Environment.ExpandEnvironmentVariables(@"%AppData%\Microsoft\Windows\Start Menu\Programs"));
        }

        foreach (string folder in roots)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string lnk in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
                {
                    if (!LooksLikeEudic(Path.GetFileName(lnk))) continue;
                    string? target = ShortcutTarget.Resolve(lnk);
                    if (target != null && IsEudicExe(target)) return target;
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "扫描开始菜单找欧路词典"); }
        }
        return null;
    }

    /// <summary>最后的老办法：几个常见安装目录。</summary>
    private static string? FromKnownDirs()
    {
        string[] dirs =
        {
            @"%ProgramFiles%\eudic",
            @"%ProgramFiles%\Eudic",
            @"%ProgramFiles(x86)%\eudic",
            @"%ProgramFiles(x86)%\Eudic",
            @"%ProgramW6432%\eudic",
            @"%LocalAppData%\Programs\Eudic",
            @"%SystemDrive%\eudic",
        };
        foreach (string raw in dirs)
        {
            try
            {
                string dir = Environment.ExpandEnvironmentVariables(raw);
                if (dir.IndexOf('%') >= 0) continue;   // 32 位进程里 *W6432* 可能没展开
                foreach (string exe in ExeNames)
                {
                    string path = Path.Combine(dir, exe);
                    if (File.Exists(path)) return path;
                }
            }
            catch { }
        }
        return null;
    }
}

/// <summary>
/// 解析 .lnk 的目标路径。只用 IShellLink + IPersistFile 两个 COM 接口，
/// 不引第三方库——为了一个查词路径装个依赖不值。
/// </summary>
internal static class ShortcutTarget
{
    /// <summary>解析失败（文件不存在、不是快捷方式、目标是 MSI 广告页）返回 null。</summary>
    internal static string? Resolve(string lnkPath)
    {
        try
        {
            if (!File.Exists(lnkPath)) return null;
            IShellLinkW link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            StringBuilder buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            string path = buffer.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "解析快捷方式 " + lnkPath);
            return null;
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    /* 只声明到 GetPath 为止：COM 接口按 vtable 槽位调用，前几个槽对上了就能取路径。
       后面那些 Set* 用不到，写出来只会让这里更难读。 */
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }
}
