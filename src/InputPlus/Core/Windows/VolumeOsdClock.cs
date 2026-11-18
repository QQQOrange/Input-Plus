using System;
using System.Threading;

namespace InputPlus.Core.Windows;

/// <summary>
/// 记录“刚刚调过音量”的时间。音量走系统音量键，横条由 Windows 自己画，
/// 我们看不到它的窗口句柄，但知道它大概会显示 2 秒左右。
/// 亮度条据此让开位置，避免和系统音量条叠在一起。
/// </summary>
internal static class VolumeOsdClock
{
    /// <summary>Windows 音量横条大致停留时间（毫秒）。</summary>
    public const long VisibleMs = 2200;

    private static long _lastTick;

    /// <summary>音量横条出现（或再次刷新）时触发。可能来自任意线程。</summary>
    public static event Action? Appeared;

    /// <summary>调音量后调用。会刷新时间戳并通知亮度条重新定位。</summary>
    public static void Notify()
    {
        Interlocked.Exchange(ref _lastTick, Environment.TickCount64);
        Appeared?.Invoke();
    }

    /// <summary>现在是否很可能有系统音量条占着屏幕底部。</summary>
    public static bool IsLikelyVisible
        => Environment.TickCount64 - Volatile.Read(ref _lastTick) < VisibleMs;
}
