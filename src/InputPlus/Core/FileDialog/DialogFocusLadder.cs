using System.Runtime.InteropServices;
using InputPlus.Core;
using InputPlus.Core.FileDialog;
using InputPlus.Core.Input;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// 抢键盘的阶梯（见 FILE-DIALOG-BAR.md 6.4）。
///
/// 背景：本程序不是前台进程，Windows 默认拒绝后台进程 SetForegroundWindow。
/// 四级降级分别是：直接抢 → AttachThreadInput 后抢 → 模拟一次 Alt 解锁前台限制后抢
/// → 临时低级键盘钩子把字符重定向进浮层。全部失败就放弃本次显示，只写日志。
/// </summary>
internal static class DialogFocusLadder
{
    internal enum Result
    {
        Foreground = 1,
        AttachThreadInput = 2,
        AltTap = 3,
        NeedsKeyboardHook = 4,
        Failed = 5,
    }

    /// <summary>尝试让浮层拿到键盘。返回 <see cref="Result.NeedsKeyboardHook"/> 时由调用方决定装不装钩子。</summary>
    public static Result TryTakeKeyboard(nint barHwnd, out string detail)
    {
        detail = "";
        if (barHwnd == 0)
        {
            detail = "浮层句柄为空";
            return Result.Failed;
        }

        DialogNative.ShowWindow(barHwnd, DialogNative.SW_SHOWNOACTIVATE);

        if (TryForeground(barHwnd))
        {
            detail = "SetForegroundWindow";
            return Result.Foreground;
        }

        nint foreground = DialogNative.GetForegroundWindow();
        uint foregroundThread = foreground != 0
            ? DialogNative.GetWindowThreadProcessId(foreground, out _)
            : 0;
        uint selfThread = DialogNative.GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != selfThread)
                attached = DialogNative.AttachThreadInput(foregroundThread, selfThread, true);

            if (TryForeground(barHwnd))
            {
                detail = attached ? "AttachThreadInput + SetForegroundWindow" : "AttachThreadInput 失败后仍抢到前台";
                return Result.AttachThreadInput;
            }
        }
        finally
        {
            if (attached) DialogNative.AttachThreadInput(foregroundThread, selfThread, false);
        }

        /* 模拟一次 Alt 按下抬起：系统看到"用户刚操作过键盘"，会临时放开前台限制。
         * 这是 Listary 同款做法，不改任何系统设置。 */
        try
        {
            InputSender.Tap(VK.Menu);
            Thread.Sleep(30);
            if (TryForeground(barHwnd))
            {
                detail = "模拟 Alt 后 SetForegroundWindow";
                return Result.AltTap;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "模拟 Alt 抢前台");
        }

        detail = $"四级抢前台全部失败（前台线程 {foregroundThread}）";
        return Result.NeedsKeyboardHook;
    }

    private static bool TryForeground(nint hwnd)
    {
        try
        {
            DialogNative.SetForegroundWindow(hwnd);
            for (int i = 0; i < 8; i++)
            {
                if (DialogNative.GetForegroundWindow() == hwnd) return true;
                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "SetForegroundWindow");
        }
        return false;
    }
}
