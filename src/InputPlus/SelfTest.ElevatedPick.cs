using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using InputPlus.Core;
using InputPlus.Core.Interop;
using InputPlus.Core.Text;
using InputPlus.Core.Windows;

namespace InputPlus;

public static partial class SelfTest
{
    /// <summary>提权取词助手：协议、命令循环、状态机。</summary>
    private static void TestSelectionElevated()
    {
        // ---- 请求帧：1 字节命令 + 8 字节目标窗口句柄 ----
        using (var ms = new MemoryStream())
        {
            SelectionElevated.WriteRequest(ms, new IntPtr(0x1234));
            byte[] bytes = ms.ToArray();
            CheckEqual("提权取词的请求帧长度", bytes.Length, SelectionElevated.RequestBytes);
            Check("提权取词的请求帧是取词命令", bytes.Length > 0 && bytes[0] == SelectionElevated.CmdCapture);
            CheckEqual("提权取词的请求帧带目标窗口", BitConverter.ToInt64(bytes, 1), 0x1234L);
        }

        // ---- 回复帧：4 字节长度 + UTF-8 ----
        using (var ms = new MemoryStream())
        {
            SelectionElevated.WriteReply(ms, "豆瓣 搜索");
            ms.Position = 0;
            CheckEqual("提权取词的回复帧能读回中文", SelectionElevated.ReadReply(ms), "豆瓣 搜索");
        }

        using (var ms = new MemoryStream())
        {
            SelectionElevated.WriteReply(ms, "");
            ms.Position = 0;
            CheckEqual("提权取词的回复帧能表示空选区", SelectionElevated.ReadReply(ms), "");
        }

        // 脏长度：负数、超大、超过 4MB 上限，都不能把助手拖死
        foreach (int bad in new[] { -7, int.MaxValue, 5 * 1024 * 1024 })
        {
            using var ms = new MemoryStream();
            byte[] head = BitConverter.GetBytes(bad);
            ms.Write(head, 0, 4);
            ms.Position = 0;
            Check($"提权取词遇到脏长度 {bad} 返回空而不是抛", SelectionElevated.ReadReply(ms) == null);
        }

        // ---- 命令循环：喂 MemoryStream 就当是管道 ----
        Func<IntPtr, string>? oldCapture = SelectionElevated.CaptureForTest;
        try
        {
            SelectionElevated.CaptureForTest = _ => "选中的文字";
            using (var ms = new MemoryStream())
            {
                SelectionElevated.WriteRequest(ms, new IntPtr(0x22));
                ms.Position = 0;    // 写完要回头读：同一根管道是双向的，位置得自己归零
                int code = SelectionElevated.ServeLoop(ms);
                ms.Position = 0;
                byte[] skip = new byte[SelectionElevated.RequestBytes];
                _ = ms.Read(skip, 0, skip.Length);
                CheckEqual("提权助手的命令循环遇到 EOF 正常收工", code, 0);
                CheckEqual("提权助手把选区写回主进程", SelectionElevated.ReadReply(ms), "选中的文字");
            }

            // 退出命令：一个字节就够，不该去读后面的内容
            using (var ms = new MemoryStream(new byte[] { SelectionElevated.CmdExit }))
            {
                CheckEqual("提权助手收到退出命令就退出", SelectionElevated.ServeLoop(ms), 0);
            }

            // 未知命令：跳过，不写回复
            using (var ms = new MemoryStream(new byte[] { 0x7F, 0, 0, 0, 0, 0, 0, 0, 0 }))
            {
                CheckEqual("提权助手跳过不认识的命令", SelectionElevated.ServeLoop(ms), 0);
                CheckEqual("跳过的命令不写回复", ms.Length, SelectionElevated.RequestBytes);
            }
        }
        finally
        {
            SelectionElevated.CaptureForTest = oldCapture;
        }

        // ---- 状态机：拉不起来（用户点取消 / UAC 被关）就别反复骚扰 ----
        SelectionElevated.ResetForTest();
        Func<string, Process?>? oldLaunch = SelectionElevated.LaunchForTest;
        try
        {
            SelectionElevated.LaunchForTest = _ => null;
            string text = SelectionElevated.TryGetSelectedText(600, IntPtr.Zero);
            CheckEqual("提权助手拉不起来时返回空", text, "");
            if (ElevationHelper.IsElevated)
            {
                Check("自检本身是管理员：助手按设计不启用", !SelectionElevated.IsConnected);
            }
            else
            {
                Check("授权没成功就标记为「已拒绝」", SelectionElevated.Declined);
                Check("拒绝之后状态不再是「未启动」", SelectionElevated.Status != "未启动");
                CheckEqual("再调用一次也不会有选区（不重复拉 UAC）", SelectionElevated.TryGetSelectedText(300, IntPtr.Zero), "");
            }
        }
        finally
        {
            SelectionElevated.LaunchForTest = oldLaunch;
            SelectionElevated.ResetForTest();
        }

        // ---- 等 UAC 时的"待连"：没有待连接也不能把自己搅乱 ----
        string statusBefore = SelectionElevated.Status;
        SelectionElevated.PromotePendingForTest();
        CheckEqual("没有待连接时转正不动状态", SelectionElevated.Status, statusBefore);
        Check("待连接的等待窗口够用户点 UAC（≥20 秒）", SelectionElevated.PendingWaitMsForTest >= 20000);

        Check("重置之后「已拒绝」清掉", !SelectionElevated.Declined);
        CheckEqual("重置之后没有连接", SelectionElevated.IsConnected, false);
        Check("提权助手的启动参数在免提权白名单里",
            ElevationHelper.NoElevateArgsForTest.Any(a => string.Equals(a, SelectionElevated.ServerFlag, StringComparison.OrdinalIgnoreCase)));
        Check("提权助手的启动参数是诊断参数之外的单列项",
            !CliFlags.DiagnosticFlagsForTest.Any(a => string.Equals(a, SelectionElevated.ServerFlag, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>手势：识别开始菜单/搜索这类 shell 浮层。</summary>
    private static void TestShellOverlay()
    {
        Check("零句柄不当成 shell 浮层", !ShellOverlay.IsUp(IntPtr.Zero));
        Check("零句柄的猜测是问号", ShellOverlay.Describe(IntPtr.Zero).StartsWith("?", StringComparison.Ordinal));
        Check("随便一个无效句柄不当成 shell 浮层", !ShellOverlay.IsUp(new IntPtr(0x1)));

        IntPtr fg = WindowService.Foreground;
        uint fgPid = 0;
        if (fg != IntPtr.Zero) NativeMethods.GetWindowThreadProcessId(fg, out fgPid);
        if (fg != IntPtr.Zero && fgPid == (uint)Environment.ProcessId)
        {
            // 自检自己的窗口当然不是开始菜单；以前只要"类名像 CoreWindow"就会误判
            Check("前台是自己的窗口时不当成 shell 浮层", !ShellOverlay.IsUp(fg));
            Check("前台是自己的窗口时不发关闭请求", !ShellOverlay.Dismiss());
        }

        // 拉高层级只是加 topmost，不该有任何异常；零句柄要能安全跳过
        ShellOverlay.Raise(IntPtr.Zero);
        Check("Raise 对零句柄不抛异常", true);

        // 常驻的服务/桌面类名不能被当成 shell 浮层
        Check("桌面（Progman）不算 shell 浮层", !string.Equals(ShellOverlay.Describe(WindowService.Foreground), "Progman.exe / Progman", StringComparison.Ordinal) || !ShellOverlay.IsUp(WindowService.Foreground));
    }
}
