using System;
using System.IO;
using System.Text;

namespace InputPlus.UI;

/// <summary>
/// 鼠标手势的**明文**现场日志（写 <c>%TEMP%\inputplus-gesture.log</c>）。
///
/// 为什么需要：用户反复报"画出手势之后没松手，轨迹自己没了"，而主日志是加密的，
/// 复述里也看不出是哪条收尾路径干的（收尾路径有七八条：命中/未识别/起始超时/
/// 分段过多/立即执行/抬手丢失/看门狗/禁用…）。这里把每一次"收笔"和"收起覆盖层"
/// 连原因一起记下来，用户复现一次就能直接定位。
///
/// 约定：只写手势这条链；超过 256KB 自清；<c>INPUTPLUS_GESTURE_TRACE=0</c> 可关；
/// 写盘失败一律吞掉（绝不影响手感）。
/// </summary>
internal static class GestureTrace
{
    private const int MaxBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly bool Enabled =
        !string.Equals(Environment.GetEnvironmentVariable("INPUTPLUS_GESTURE_TRACE"), "0", StringComparison.Ordinal);

    private static string LogPath
    {
        get
        {
            try { return Path.Combine(Path.GetTempPath(), "inputplus-gesture.log"); }
            catch { return ""; }
        }
    }

    public static void Write(string message)
    {
        if (!Enabled || string.IsNullOrEmpty(message)) return;
        try
        {
            string path = LogPath;
            if (path.Length == 0) return;
            lock (Gate)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    try { File.Delete(path); } catch { }
                }
                File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch { }
    }

    internal static string LogPathForDiag => Enabled ? LogPath : "";
}
