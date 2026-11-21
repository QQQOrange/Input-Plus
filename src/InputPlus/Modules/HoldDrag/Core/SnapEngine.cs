#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
// HoldDrag · Core/SnapEngine.cs
//
// 拖动时的边缘吸附 —— 逐条照 AltDrag 的 Enum / MoveSnap / ResizeSnap 移植：
//   Enum        hooks.c:225-385
//   MoveSnap    hooks.c:387-479
//   ResizeSnap  hooks.c:481-580
//
// 和旧实现的区别（旧实现只吸"当前显示器工作区四条边"）：
//   1) 吸附目标 = 每块显示器的**完整矩形**（不是工作区）+ 任务栏 + 所有有标题栏的顶层窗口；
//   2) snap 分级：1 = 只吸屏幕边，2 = 再吸窗口外侧，3 = 再吸窗口内侧；
//   3) 阈值是"每命中一次就收缩到该次差值"，所以最终吸到的是最近的那条边；
//   4) 缩放时只吸"真正在拖的那条边"，并据此改写宽高。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;

namespace InputPlus.Modules.HoldDrag;

internal static class SnapEngine
{
	/// <summary>
	/// 一份吸附目标快照：每块显示器的完整矩形 + 任务栏 + 所有带标题栏的顶层窗口。
	/// 整份替换、绝不就地改 —— 钩子线程读、后台线程建，靠这一条免锁。
	/// </summary>
	private sealed class Target
	{
		internal List<Native.RECT> Monitors = new List<Native.RECT>(8);

		internal List<Native.RECT> Windows = new List<Native.RECT>(256);

		internal nint Progman;

		internal int Snap = -1;

		internal nint Dragged;

		internal long At;
	}

	private static volatile Target _target = new Target();

	/// <summary>后台正在枚举（同一时刻只跑一份）。</summary>
	private static int _refreshing;

	/// <summary>后台线程正在填的那份快照（EnumWindows 的回调要用）。</summary>
	[ThreadStatic]
	private static Target _building;

	private static readonly object Gate = new object();

	/// <summary>EnumWindows 回调不能被 GC 掉，这里保持一个静态引用。</summary>
	private static readonly Native.EnumWindowsProc EnumProc = OnEnumWindow;

	/// <summary>
	/// [Blacklist] Snaplist（hooks.c:251）：没有 WS_CAPTION 的窗口本来不参与吸附，列在这里的算例外。
	/// 由 AltDragSink 每帧从配置同步过来；静态字段，后台枚举线程直接读。
	/// </summary>
	internal static volatile string SnapList = AppConfig.DefaultSnapList;

	// 吸附目标是"活着"的，但窗口集合在一个拖动的几百毫秒里几乎不会变。而 EnumWindows +
	// 每窗 GetWindowRect 一次要 20~40ms —— 这一段**绝不能跑在钩子线程上**，否则每刷一次
	// 就顿一下（缩放时尤其明显：目标程序自己还在重排）。所以第一次同步取一份，之后只在
	// 后台刷，钩子线程永远读上一份。
	private const int CacheMs = 120;

	/// <summary>
	/// 移动吸附（hooks.c:387-479）。posx / posy 是"贴哪里"，wndwidth / wndheight 只读。
	/// </summary>
	internal static void Move(int snap, int threshold, nint dragged, ref int posx, ref int posy, int wndwidth, int wndheight)
	{
		if (snap <= 0 || threshold <= 0)
		{
			return;
		}
		Target t = EnsureFresh(snap, dragged);
		lock (Gate)
		{
			int thresholdx = threshold;
			int thresholdy = threshold;
			bool stuckx = false;
			bool stucky = false;
			int stickx = 0;
			int sticky = 0;
			int i = 0;
			int j = 0;
			while (i < t.Monitors.Count || j < t.Windows.Count)
			{
				Native.RECT snapwnd;
				bool snapinside;
				if (i < t.Monitors.Count)
				{
					snapwnd = t.Monitors[i];
					snapinside = true;
					i++;
				}
				else
				{
					snapwnd = t.Windows[j];
					// snap == 2 只吸窗口外侧；3 连内侧一起吸
					snapinside = snap != 2;
					j++;
				}
				// X 轴：两条矩形的竖直范围要有重叠（含阈值）才比水平边
				if ((snapwnd.Top - thresholdx < posy && posy < snapwnd.Bottom + thresholdx) || (posy - thresholdx < snapwnd.Top && snapwnd.Top < posy + wndheight + thresholdx))
				{
					bool flag = snapinside || posy + wndheight - thresholdx < snapwnd.Top || snapwnd.Bottom < posy + thresholdx;
					if (posx - thresholdx < snapwnd.Right && snapwnd.Right < posx + thresholdx)
					{
						stuckx = true;
						stickx = snapwnd.Right;
						thresholdx = snapwnd.Right - posx;
					}
					else if (flag && posx + wndwidth - thresholdx < snapwnd.Right && snapwnd.Right < posx + wndwidth + thresholdx)
					{
						stuckx = true;
						stickx = snapwnd.Right - wndwidth;
						thresholdx = snapwnd.Right - (posx + wndwidth);
					}
					else if (flag && posx - thresholdx < snapwnd.Left && snapwnd.Left < posx + thresholdx)
					{
						stuckx = true;
						stickx = snapwnd.Left;
						thresholdx = snapwnd.Left - posx;
					}
					else if (posx + wndwidth - thresholdx < snapwnd.Left && snapwnd.Left < posx + wndwidth + thresholdx)
					{
						stuckx = true;
						stickx = snapwnd.Left - wndwidth;
						thresholdx = snapwnd.Left - (posx + wndwidth);
					}
				}
				// Y 轴同构
				if ((snapwnd.Left - thresholdy < posx && posx < snapwnd.Right + thresholdy) || (posx - thresholdy < snapwnd.Left && snapwnd.Left < posx + wndwidth + thresholdy))
				{
					bool flag2 = snapinside || posx + wndwidth - thresholdy < snapwnd.Left || snapwnd.Right < posx + thresholdy;
					if (posy - thresholdy < snapwnd.Bottom && snapwnd.Bottom < posy + thresholdy)
					{
						stucky = true;
						sticky = snapwnd.Bottom;
						thresholdy = snapwnd.Bottom - posy;
					}
					else if (flag2 && posy + wndheight - thresholdy < snapwnd.Bottom && snapwnd.Bottom < posy + wndheight + thresholdy)
					{
						stucky = true;
						sticky = snapwnd.Bottom - wndheight;
						thresholdy = snapwnd.Bottom - (posy + wndheight);
					}
					else if (flag2 && posy - thresholdy < snapwnd.Top && snapwnd.Top < posy + thresholdy)
					{
						stucky = true;
						sticky = snapwnd.Top;
						thresholdy = snapwnd.Top - posy;
					}
					else if (posy + wndheight - thresholdy < snapwnd.Top && snapwnd.Top < posy + wndheight + thresholdy)
					{
						stucky = true;
						sticky = snapwnd.Top - wndheight;
						thresholdy = snapwnd.Top - (posy + wndheight);
					}
				}
			}
			if (stuckx)
			{
				posx = stickx;
			}
			if (stucky)
			{
				posy = sticky;
			}
		}
	}

	/// <summary>
	/// 缩放吸附（hooks.c:481-580）：和移动同一套遍历，但每条分支都要求"被拖的正是这条边"，
	/// 命中后按边改写宽高（贴右边就改宽度，贴左边还要同时挪左边）。
	/// </summary>
	internal static void Resize(int snap, int threshold, nint dragged, DragEdge edgeX, DragEdgeY edgeY, ref int posx, ref int posy, ref int wndwidth, ref int wndheight)
	{
		if (snap <= 0 || threshold <= 0)
		{
			return;
		}
		Target t = EnsureFresh(snap, dragged);
		lock (Gate)
		{
			int thresholdx = threshold;
			int thresholdy = threshold;
			bool stuckleft = false;
			bool stucktop = false;
			bool stuckright = false;
			bool stuckbottom = false;
			int stickleft = 0;
			int sticktop = 0;
			int stickright = 0;
			int stickbottom = 0;
			int i = 0;
			int j = 0;
			while (i < t.Monitors.Count || j < t.Windows.Count)
			{
				Native.RECT snapwnd;
				bool snapinside;
				if (i < t.Monitors.Count)
				{
					snapwnd = t.Monitors[i];
					snapinside = true;
					i++;
				}
				else
				{
					snapwnd = t.Windows[j];
					snapinside = snap != 2;
					j++;
				}
				if ((snapwnd.Top - thresholdx < posy && posy < snapwnd.Bottom + thresholdx) || (posy - thresholdx < snapwnd.Top && snapwnd.Top < posy + wndheight + thresholdx))
				{
					bool flag = snapinside || posy + wndheight - thresholdx < snapwnd.Top || snapwnd.Bottom < posy + thresholdx;
					if (edgeX == DragEdge.Left && posx - thresholdx < snapwnd.Right && snapwnd.Right < posx + thresholdx)
					{
						stuckleft = true;
						stickleft = snapwnd.Right;
						thresholdx = snapwnd.Right - posx;
					}
					else if (edgeX == DragEdge.Right && flag && posx + wndwidth - thresholdx < snapwnd.Right && snapwnd.Right < posx + wndwidth + thresholdx)
					{
						stuckright = true;
						stickright = snapwnd.Right;
						thresholdx = snapwnd.Right - (posx + wndwidth);
					}
					else if (edgeX == DragEdge.Left && flag && posx - thresholdx < snapwnd.Left && snapwnd.Left < posx + thresholdx)
					{
						stuckleft = true;
						stickleft = snapwnd.Left;
						thresholdx = snapwnd.Left - posx;
					}
					else if (edgeX == DragEdge.Right && posx + wndwidth - thresholdx < snapwnd.Left && snapwnd.Left < posx + wndwidth + thresholdx)
					{
						stuckright = true;
						stickright = snapwnd.Left;
						thresholdx = snapwnd.Left - (posx + wndwidth);
					}
				}
				if ((snapwnd.Left - thresholdy < posx && posx < snapwnd.Right + thresholdy) || (posx - thresholdy < snapwnd.Left && snapwnd.Left < posx + wndwidth + thresholdy))
				{
					bool flag2 = snapinside || posx + wndwidth - thresholdy < snapwnd.Left || snapwnd.Right < posx + thresholdy;
					if (edgeY == DragEdgeY.Top && posy - thresholdy < snapwnd.Bottom && snapwnd.Bottom < posy + thresholdy)
					{
						stucktop = true;
						sticktop = snapwnd.Bottom;
						thresholdy = snapwnd.Bottom - posy;
					}
					else if (edgeY == DragEdgeY.Bottom && flag2 && posy + wndheight - thresholdy < snapwnd.Bottom && snapwnd.Bottom < posy + wndheight + thresholdy)
					{
						stuckbottom = true;
						stickbottom = snapwnd.Bottom;
						thresholdy = snapwnd.Bottom - (posy + wndheight);
					}
					else if (edgeY == DragEdgeY.Top && flag2 && posy - thresholdy < snapwnd.Top && snapwnd.Top < posy + thresholdy)
					{
						stucktop = true;
						sticktop = snapwnd.Top;
						thresholdy = snapwnd.Top - posy;
					}
					else if (edgeY == DragEdgeY.Bottom && posy + wndheight - thresholdy < snapwnd.Top && snapwnd.Top < posy + wndheight + thresholdy)
					{
						stuckbottom = true;
						stickbottom = snapwnd.Top;
						thresholdy = snapwnd.Top - (posy + wndheight);
					}
				}
			}
			if (stuckleft)
			{
				wndwidth = wndwidth + posx - stickleft;
				posx = stickleft;
			}
			if (stucktop)
			{
				wndheight = wndheight + posy - sticktop;
				posy = sticktop;
			}
			if (stuckright)
			{
				wndwidth = stickright - posx;
			}
			if (stuckbottom)
			{
				wndheight = stickbottom - posy;
			}
		}
	}

	/// <summary>
	/// 拿一份可用的吸附目标。第一次 / 换了被拖窗口 / 换了档位 → 当场枚举一次（每次拖动最多一次）；
	/// 其余情况只在后台刷，钩子线程立刻拿到上一份 —— 那 20~40ms 永远不进钩子。
	/// </summary>
	private static Target EnsureFresh(int snap, nint dragged)
	{
		Target cur = _target;
		if (cur.At != 0L && cur.Snap == snap && cur.Dragged == dragged)
		{
			if (Environment.TickCount64 - cur.At >= CacheMs && Interlocked.Exchange(ref _refreshing, 1) == 0)
			{
				QueueRefresh(snap, dragged);
			}
			return cur;
		}
		Target built = Collect(snap, dragged);
		_target = built;
		return built;
	}

	private static void QueueRefresh(int snap, nint dragged)
	{
		ThreadPool.QueueUserWorkItem(delegate
		{
			try
			{
				_target = Collect(snap, dragged);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
			finally
			{
				Interlocked.Exchange(ref _refreshing, 0);
			}
		});
	}

	/// <summary>hooks.c:342-385 的 Enum()。</summary>
	private static Target Collect(int snap, nint dragged)
	{
		Target t = new Target();
		t.Snap = snap;
		t.Dragged = dragged;
		_building = t;
		try
		{
			try
			{
				Screen[] allScreens = Screen.AllScreens;
				for (int i = 0; i < allScreens.Length; i++)
				{
					Rectangle bounds = allScreens[i].Bounds;
					t.Monitors.Add(new Native.RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
			try
			{
				// hooks.c:361-368：任务栏在门槛之前就加进来了 —— 它算"屏幕边缘"这一档，
				// 所以 snap=1（只吸屏幕）时任务栏也该能吸上。加窗口（snap>=2）才在门槛之后。
				nint toolbar = Native.FindWindowW("Shell_TrayWnd", null);
				if (toolbar != IntPtr.Zero && Native.GetWindowRect(toolbar, out var tray))
				{
					t.Windows.Add(tray);
				}
				if (snap >= 2)
				{
					t.Progman = Native.FindWindowW("Progman", "Program Manager");
					Native.EnumWindows(EnumProc, IntPtr.Zero);
				}
			}
			catch (Exception ex2)
			{
				Log.Error(ex2);
			}
		}
		finally
		{
			_building = null;
		}
		t.At = Environment.TickCount64;
		return t;
	}

	/// <summary>hooks.c:239-290 的 EnumWindowsProc 过滤条件。</summary>
	private static bool OnEnumWindow(nint hWnd, nint lParam)
	{
		Target t = _building;
		if (t == null)
		{
			return true;
		}
		try
		{
			if (hWnd == t.Dragged || hWnd == t.Progman)
			{
				return true;
			}
			if (!Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd))
			{
				return true;
			}
			// hooks.c:251：没有 WS_CAPTION 的跳过 —— 除非它列在 Snaplist 里。
			long windowLong = Native.GetWindowLong(hWnd, Native.GWL_STYLE);
			if ((windowLong & Native.WS_CAPTION) != Native.WS_CAPTION
				&& !AppRules.MatchesWindows(SnapList, AppRules.ProcessNameOf(hWnd), AppRules.TitleOf(hWnd), AppRules.ClassNameOf(hWnd)))
			{
				return true;
			}
			if (!Native.GetWindowRect(hWnd, out var lpRect))
			{
				return true;
			}
			if (Native.IsZoomed(hWnd))
			{
				// 最大化窗口的"矩形"是它那块显示器，裁一下再参与吸附
				nint num = Native.MonitorFromWindow(hWnd, 2u);
				Native.MONITORINFO lpmi = new Native.MONITORINFO
				{
					cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO))
				};
				if (num != IntPtr.Zero && Native.GetMonitorInfoW(num, ref lpmi))
				{
					Native.RECT rcMonitor = lpmi.rcMonitor;
					lpRect = new Native.RECT(Math.Max(lpRect.Left, rcMonitor.Left), Math.Max(lpRect.Top, rcMonitor.Top), Math.Min(lpRect.Right, rcMonitor.Right), Math.Min(lpRect.Bottom, rcMonitor.Bottom));
				}
			}
			// 被已收集的某个矩形完全包含 → 丢掉（重叠去重）
			for (int i = 0; i < t.Windows.Count; i++)
			{
				Native.RECT rECT = t.Windows[i];
				if (lpRect.Left >= rECT.Left && lpRect.Top >= rECT.Top && lpRect.Right <= rECT.Right && lpRect.Bottom <= rECT.Bottom)
				{
					return true;
				}
			}
			t.Windows.Add(lpRect);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		return true;
	}
}
