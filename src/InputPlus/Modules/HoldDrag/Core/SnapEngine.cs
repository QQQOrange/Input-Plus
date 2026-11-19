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
using System.Drawing;
using System.Windows.Forms;

namespace InputPlus.Modules.HoldDrag;

internal static class SnapEngine
{
	private static readonly List<Native.RECT> Monitors = new List<Native.RECT>(8);

	private static readonly List<Native.RECT> Windows = new List<Native.RECT>(256);

	private static readonly object Gate = new object();

	/// <summary>EnumWindows 回调不能被 GC 掉，这里保持一个静态引用。</summary>
	private static readonly Native.EnumWindowsProc EnumProc = OnEnumWindow;

	private static nint _dragged;

	private static nint _progman;

	private static int _ownPid;

	private static int OwnPid
	{
		get
		{
			if (_ownPid == 0)
			{
				_ownPid = Process.GetCurrentProcess().Id;
			}
			return _ownPid;
		}
	}

	/// <summary>
	/// 移动吸附（hooks.c:387-479）。posx / posy 是"贴哪里"，wndwidth / wndheight 只读。
	/// </summary>
	internal static void Move(int snap, int threshold, nint dragged, ref int posx, ref int posy, int wndwidth, int wndheight)
	{
		if (snap <= 0 || threshold <= 0)
		{
			return;
		}
		lock (Gate)
		{
			Collect(snap, dragged);
			int thresholdx = threshold;
			int thresholdy = threshold;
			bool stuckx = false;
			bool stucky = false;
			int stickx = 0;
			int sticky = 0;
			int i = 0;
			int j = 0;
			while (i < Monitors.Count || j < Windows.Count)
			{
				Native.RECT snapwnd;
				bool snapinside;
				if (i < Monitors.Count)
				{
					snapwnd = Monitors[i];
					snapinside = true;
					i++;
				}
				else
				{
					snapwnd = Windows[j];
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
		lock (Gate)
		{
			Collect(snap, dragged);
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
			while (i < Monitors.Count || j < Windows.Count)
			{
				Native.RECT snapwnd;
				bool snapinside;
				if (i < Monitors.Count)
				{
					snapwnd = Monitors[i];
					snapinside = true;
					i++;
				}
				else
				{
					snapwnd = Windows[j];
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

	/// <summary>hooks.c:342-385 的 Enum()。</summary>
	private static void Collect(int snap, nint dragged)
	{
		Monitors.Clear();
		Windows.Clear();
		_dragged = dragged;
		try
		{
			Screen[] allScreens = Screen.AllScreens;
			for (int i = 0; i < allScreens.Length; i++)
			{
				Rectangle bounds = allScreens[i].Bounds;
				Monitors.Add(new Native.RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		try
		{
			_progman = Native.FindWindowW("Progman", "Program Manager");
			// hooks.c:361-368：任务栏在门槛之前就加进来了 —— 它算"屏幕边缘"这一档，
			// 所以 snap=1（只吸屏幕）时任务栏也该能吸上。加窗口（snap>=2）才在门槛之后。
			nint num = Native.FindWindowW("Shell_TrayWnd", null);
			if (num != IntPtr.Zero && Native.GetWindowRect(num, out var lpRect))
			{
				Windows.Add(lpRect);
			}
			if (snap < 2)
			{
				return;
			}
			Native.EnumWindows(EnumProc, IntPtr.Zero);
		}
		catch (Exception ex2)
		{
			Log.Error(ex2);
		}
	}

	/// <summary>hooks.c:239-290 的 EnumWindowsProc 过滤条件。</summary>
	private static bool OnEnumWindow(nint hWnd, nint lParam)
	{
		try
		{
			if (hWnd == _dragged || hWnd == _progman)
			{
				return true;
			}
			// 自己的外框 / HUD 也是可见窗口，吸过去只会互相打架
			Native.GetWindowThreadProcessId(hWnd, out var lpdwProcessId);
			if ((int)lpdwProcessId == OwnPid)
			{
				return true;
			}
			if (!Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd))
			{
				return true;
			}
			long windowLong = Native.GetWindowLong(hWnd, Native.GWL_STYLE);
			if ((windowLong & Native.WS_CAPTION) != Native.WS_CAPTION)
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
			for (int i = 0; i < Windows.Count; i++)
			{
				Native.RECT rECT = Windows[i];
				if (lpRect.Left >= rECT.Left && lpRect.Top >= rECT.Top && lpRect.Right <= rECT.Right && lpRect.Bottom <= rECT.Bottom)
				{
					return true;
				}
			}
			Windows.Add(lpRect);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		return true;
	}
}
