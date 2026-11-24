using System;
using System.Collections.Generic;

namespace InputPlus.Core.HoldDrag;

/// <summary>
/// 「按住拖拽」允许拖的**本程序窗口**白名单。
///
/// 为什么需要白名单而不是"本进程的一律允许"：进程里还有附加菜单、手势覆盖层、OSD、
/// 各种 Toast/HUD —— 它们铺在屏幕上，按住触发键想拖别的窗口时被先拖走，界面就乱了
/// （实测把主窗口拖到 -426,0）。反过来，用户又明确要求"按住拖拽也要能在 Input Plus
/// 自己的窗口上用"，所以只放行**真正的主窗口**：由主窗口在 Loaded 时登记、Closed 时注销。
///
/// 自检里还要能单独构造"自己的窗口"这个场景（<c>DragOwnWindowForTest</c> 是另一条路），
/// 所以这里做成纯静态表，不依赖 WPF。
/// </summary>
internal static class OwnWindowRegistry
{
    private static readonly HashSet<nint> Allowed = new();
    private static readonly object Gate = new();

    /// <summary>登记一个允许拖拽的本进程窗口（主窗口）。</summary>
    internal static void Register(nint hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (Gate) Allowed.Add(hwnd);
    }

    /// <summary>注销（窗口关闭时）。</summary>
    internal static void Unregister(nint hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (Gate) Allowed.Remove(hwnd);
    }

    /// <summary>这个窗口是不是"允许拖拽的本程序窗口"。</summary>
    internal static bool IsAllowed(nint hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        lock (Gate) return Allowed.Contains(hwnd);
    }

    /// <summary>自检用：清空登记表。</summary>
    internal static void ClearForTest()
    {
        lock (Gate) Allowed.Clear();
    }
}
