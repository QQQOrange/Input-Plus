using System;
using System.IO;
using System.Text;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// 文件对话框浮层的**明文**现场日志（写 <c>%TEMP%\inputplus-dialog-bar.log</c>）。
///
/// 为什么不吃 CoreLog：主日志是加密的，现场只能靠用户复述 ——
/// "浮层到底停在哪一步"（没识别成文件对话框 / 被开关拦了 / 取不到边框 / owner 被 UIPI 拒 /
/// 显示抛异常）在加密日志里一点都读不到。这条链路的失败点全是**跨进程调用**，
/// 每个人踩的坑还不一样（微信是提升进程、某些程序是自绘对话框），
/// 所以必须留一份能直接看的现场。
///
/// 约定：
///   · 只写这一条链路（Watcher → Classify → Policy → FrameBounds → Show），量很小；
///   · 超过 256KB 自动清空重来，绝不无限增长；
///   · <c>INPUTPLUS_DIALOG_TRACE=0</c> 可关掉（排查完想安静时用）；
///   · 写盘失败一律吞掉 —— 日志永远不许影响浮层本身。
/// </summary>
internal static class DialogTrace
{
    private const int MaxBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly bool Enabled =
        !string.Equals(Environment.GetEnvironmentVariable("INPUTPLUS_DIALOG_TRACE"), "0", StringComparison.Ordinal);

    private static string Path_
    {
        get
        {
            try { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inputplus-dialog-bar.log"); }
            catch { return ""; }
        }
    }

    /// <summary>记一行（带毫秒时间戳）。异常一律吞掉。</summary>
    public static void Write(string message)
    {
        if (!Enabled || string.IsNullOrEmpty(message)) return;
        try
        {
            string path = Path_;
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
        catch { /* 现场日志永远不许影响功能 */ }
    }

    /// <summary>当前日志文件路径（诊断页 / 自检显示用；关掉时为空串）。</summary>
    internal static string LogPathForDiag => Enabled ? Path_ : "";
}
