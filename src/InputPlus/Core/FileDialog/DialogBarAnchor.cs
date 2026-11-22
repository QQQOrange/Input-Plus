using System;
using System.Runtime.InteropServices;

namespace InputPlus.Core.FileDialog;

public enum BarPlacement
{
    Below,
    Above,
}

/// <summary>
/// 对话框浮层的贴合计算器。
///
/// 纯函数 <see cref="GetTargetRect"/> 只吃 DWM 可见边框、工作区、bar 尺寸，
/// 返回贴合矩形；没有窗口副作用，可直接被自检覆盖。
/// </summary>
public sealed class DialogBarAnchor : IDisposable
{
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_POPUP = unchecked((int)0x80000000);

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;

    private nint _dialog;
    private nint _bar;
    private BarPlacement _placement = BarPlacement.Below;

    public nint DialogHandle => _dialog;
    public nint BarHandle => _bar;

    /// <summary>bar 的物理像素宽度/高度；Reflow 前由调用方更新。</summary>
    public int BarWidth { get; set; } = 1;
    public int BarHeight { get; set; } = 44;

    public BarPlacement Placement => _placement;
    public event Action<BarPlacement>? PlacementChanged;

    public bool Attach(nint dialog)
    {
        if (dialog == 0 || !DialogNative.IsWindow(dialog)) return false;
        _dialog = dialog;
        return true;
    }

    public void Detach()
    {
        _dialog = 0;
        _bar = 0;
    }

    public void SetBarHandle(nint bar)
    {
        _bar = bar;
        if (bar != 0) ConfigureBarChrome(bar);
    }

    /// <summary>
    /// 纯函数：给定 DWM 可见边框、工作区、bar 宽高，返回贴合矩形。
    /// 下方放得下就贴底边下方，否则翻到顶边上方；x 始终相对对话框居中并夹在工作区。
    /// </summary>
    public static DialogRect GetTargetRect(DialogRect frame, DialogRect work, int barWidth, int barHeight,
        out BarPlacement placement)
    {
        if (frame.IsEmpty)
        {
            placement = BarPlacement.Below;
            return DialogRect.Empty;
        }

        barWidth = Math.Max(1, barWidth);
        barHeight = Math.Max(1, barHeight);

        int x = frame.Left + (frame.Width - barWidth) / 2;
        int yBelow = frame.Bottom;                    // 从可见底边开始，零 padding
        int yAbove = frame.Top - barHeight;

        if (yBelow + barHeight <= work.Bottom)
        {
            placement = BarPlacement.Below;
            return Clamp(x, yBelow, barWidth, barHeight, work);
        }

        placement = BarPlacement.Above;
        return Clamp(x, yAbove, barWidth, barHeight, work);
    }

    /// <summary>用当前 DWM frame / 工作区算目标矩形；取不到边框返回空。</summary>
    public DialogRect GetTargetRect()
    {
        if (_dialog == 0 || !DialogNative.IsWindow(_dialog)) return DialogRect.Empty;
        DialogRect frame = DialogNative.FrameBounds(_dialog);
        if (frame.IsEmpty || !DialogNative.TryGetWorkArea(_dialog, out DialogNative.RECT workRect))
            return DialogRect.Empty;

        var work = new DialogRect(workRect.Left, workRect.Top, workRect.Right, workRect.Bottom);
        return GetTargetRect(frame, work, BarWidth, BarHeight, out _placement);
    }

    /// <summary>重新计算并 SetWindowPos；坐标全部为物理像素。</summary>
    public bool Reflow()
    {
        if (_dialog == 0 || _bar == 0 || !DialogNative.IsWindow(_dialog) || !DialogNative.IsWindow(_bar))
            return false;

        DialogRect target = GetTargetRect();
        if (target.IsEmpty)
        {
            CoreLog.Warn("[文件对话框] bounds unavailable：取不到 DWM 边框或工作区，不摆浮层");
            return false;
        }

        BarPlacement old = _placement;
        DialogNative.SetWindowPos(_bar, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
            SWP_NOACTIVATE | SWP_NOZORDER);
        if (old != _placement) PlacementChanged?.Invoke(_placement);
        return true;
    }

    private static DialogRect Clamp(int x, int y, int width, int height, DialogRect work)
    {
        if (work.IsEmpty) return new DialogRect(x, y, x + width, y + height);

        if (x < work.Left) x = work.Left;
        if (x + width > work.Right) x = Math.Max(work.Left, work.Right - width);
        if (y < work.Top) y = work.Top;
        if (y + height > work.Bottom) y = Math.Max(work.Top, work.Bottom - height);
        return new DialogRect(x, y, x + width, y + height);
    }

    /// <summary>
    /// 关掉 WPF 窗口的原生 caption/thickframe/border，并禁用 DWM 非客户区渲染，
    /// 避免与对话框原生阴影叠出一条暗缝。阴影只由 XAML 在外侧自绘。
    /// </summary>
    public static void ConfigureBarChrome(nint hwnd)
    {
        if (hwnd == 0) return;
        try
        {
            long style = (long)DialogNative.GetWindowLongPtr(hwnd, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
            style |= WS_POPUP;
            DialogNative.SetWindowLongPtr(hwnd, GWL_STYLE, (nint)style);

            int policy = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "配置对话框浮层窗口边框");
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    public void Dispose() => Detach();
}
