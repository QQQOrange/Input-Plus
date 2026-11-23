using System;
using System.IO;

namespace InputPlus.Core.Windows;

/// <summary>
/// 「收藏当前文件 / 打开文件所在位置」的**明文**诊断（v1.5.4）。
///
/// 为什么要单独一份：程序日志是加密的（`CLP2:` 开头），用户报"这里不对"时
/// 让他去导日志、再解密，来回一轮就半天。这两条链路的状态很少（谁当锚点、
/// 认到什么文件、走了哪一级），写成一行明文放在 `%TEMP%\inputplus-favorite.log`，
/// 用户截个图就能说清。
///
/// 只在两个动作上写，不碰钩子回调；超过 256KB 直接重写，不会无限长。
/// </summary>
internal static class FavoriteTrace
{
    private static readonly object Gate = new();

    /// <summary>诊断文件路径（提示用户时直接给这个）。</summary>
    internal static string Path => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inputplus-favorite.log");

    internal static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > 256 * 1024) File.Delete(Path);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}");
            }
        }
        catch { /* 诊断写不进去不能影响功能 */ }
    }
}
