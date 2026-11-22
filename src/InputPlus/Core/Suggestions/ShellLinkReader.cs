using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace InputPlus.Core.Suggestions;

/// <summary>
/// 读 .lnk 的目标路径。
///
/// 为什么不用 WScript.Shell：那要拖一个 COM 互操作程序集；为什么不用"猜 Recent 目录
/// 里文件的 mtime"：Recent 里的 .lnk 名字会被系统改名（"报表 (2).xlsx.lnk"），
/// 拿它反推目标路径会错。这里直接问 shell 的 IShellLink。
///
/// 线程要求：IShellLink 是 apartment-threaded，调用线程必须是 STA —— 采集线程建的时候
/// 已经 SetApartmentState(STA)，这里再兜一层，万一以后换线程也不会静默失败。
/// </summary>
internal static class ShellLinkReader
{
    /// <summary>读失败返回 null（损坏的 .lnk、指向已删除卷、权限不足都算失败）。</summary>
    public static string? TargetOf(string lnkPath) => TryTargetOf(lnkPath, out string? target, out _) ? target : null;

    /// <summary>
    /// 带失败原因的重载：探针用它把"0 条"到底是被过滤了还是 COM 挂了区分开。
    /// 失败原因只用于诊断，生产路径走 <see cref="TargetOf"/> 不记日志（240 个 lnk 会刷屏）。
    /// </summary>
    public static bool TryTargetOf(string lnkPath, out string? target, out string? error)
    {
        target = null;
        error = null;
        object? instance = null;
        try
        {
            instance = new ShellLinkCoClass();
            var link = (IShellLinkW)instance;
            ((IPersistFile)link).Load(lnkPath, 0 /* STGM_READ */);

            /* 缓冲区用 StringBuilder 而不是 char[]：LPWStr 的 [Out] 数组在
             * .NET 的 COM 封送里行为随运行时版本变过（某些版本上不回收），
             * StringBuilder 是这条 API 的常规写法，稳。 */
            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0 /* SLGP_RAWPATH */);
            string path = buffer.ToString().TrimEnd('\0');
            if (path.Length > 0)
            {
                target = path;
                return true;
            }

            // 有些 .lnk 指向的是 shell 命名空间（"这台电脑"），GetPath 给空串。
            error = "GetPath 返回空串";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + "：" + ex.Message;
            return false;
        }
        finally
        {
            if (instance != null && Marshal.IsComObject(instance))
            {
                try { Marshal.FinalReleaseComObject(instance); } catch { }
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
