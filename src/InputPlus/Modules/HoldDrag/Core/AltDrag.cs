#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
// HoldDrag · Core/AltDrag.cs
//
// 这里是 AltDrag（altdrag-master）拖拽核心的逐条移植。凡是"怎么算位置、怎么吸附、
// 怎么分区、最小/最大尺寸从哪来"这类问题，一律以 hooks.c 为准：
//   MouseMove        hooks.c:632-928
//   MoveSnap/ResizeSnap hooks.c:481-580
//   Enum/MoveSnap    hooks.c:225-479
//   起拖             hooks.c:1491-1877
// 对照注释里的行号可以直接翻回原实现。Input-Plus 自己多出来的东西（外框、HUD、抖动、
// 规则、撤销）不在这里，那些与几何无关。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace InputPlus.Modules.HoldDrag;

/// <summary>缩放时被拖动的那条竖边（AltDrag 的 RESIZE_LEFT / RESIZE_CENTER / RESIZE_RIGHT）。</summary>
internal enum DragEdge
{
	Left,
	Center,
	Right
}

/// <summary>缩放时被拖动的那条横边（AltDrag 的 RESIZE_TOP / RESIZE_CENTER / RESIZE_BOTTOM）。</summary>
internal enum DragEdgeY
{
	Top,
	Center,
	Bottom
}

/// <summary>
/// AltDrag 的 wnddb（hooks.c:1595-1612，NUMWNDDB=30）：
/// 记住"上一次 Aero 贴边把窗口缩小之前是多大"，用户从贴边区拖回来时用它还原原尺寸。
/// </summary>
internal sealed class WndDbEntry
{
	public nint Hwnd;

	/// <summary>窗口现在处于"被 Aero 贴边改过尺寸"的状态。</summary>
	public bool Restore;

	/// <summary>贴边之前的大小（还原时要用）。</summary>
	public int Width;

	public int Height;

	/// <summary>上一次写完窗口后从窗口读回来的真实大小。</summary>
	public int LastWidth;

	public int LastHeight;
}

internal static class WndDb
{
	/// <summary>AltDrag 的 NUMWNDDB。</summary>
	private const int Slots = 30;

	private static readonly WndDbEntry[] Items = CreateItems();

	private static readonly object Gate = new object();

	private static int _pos;

	private static WndDbEntry[] CreateItems()
	{
		WndDbEntry[] array = new WndDbEntry[30];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = new WndDbEntry();
		}
		return array;
	}

	/// <summary>照 hooks.c:1595-1612：先按句柄找槽，找不到就占用"下一个没被 restore 占住"的槽。</summary>
	internal static WndDbEntry Touch(nint hwnd)
	{
		lock (Gate)
		{
			for (int i = 0; i < Slots; i++)
			{
				if (Items[i].Hwnd == hwnd)
				{
					return Items[i];
				}
			}
			// 环形向前找一个 restore == false 的槽；全是 restore 的极端情况下最多走 Slots+1 步
			for (int j = 0; j <= Slots; j++)
			{
				if (!Items[_pos].Restore)
				{
					break;
				}
				_pos = ((_pos == Slots - 1) ? 0 : (_pos + 1));
			}
			// hooks.c:1609-1611 只设 hwnd 和 restore，宽高一律不动（restore == false 已经把旧值挡在外面）。
			WndDbEntry wndDbEntry = Items[_pos];
			wndDbEntry.Hwnd = hwnd;
			wndDbEntry.Restore = false;
			return wndDbEntry;
		}
	}
}

/// <summary>一次拖动里 AltDrag 需要跨帧保留的全部状态（hooks.c:73-105 的 State）。</summary>
internal sealed class AltState
{
	public int OffsetX;

	public int OffsetY;

	public DragEdge EdgeX;

	public DragEdgeY EdgeY;

	public bool OriginMaximized;

	/// <summary>按下那一刻是不是最大化（hooks.c 的 state.origin.maximized，全程不清）。
	/// OriginMaximized 会被 SetupMove / SetupResize 清掉，AutoRemaximize 要的是"一开始就是最大化"。
	/// </summary>
	public bool OriginMaximizedAtStart;

	/// <summary>hooks.c 的 state.locked：AutoRemaximize 之后本次拖动不再动窗口。</summary>
	public bool Locked;

	public int OriginWidth;

	public int OriginHeight;

	public int OriginRight;

	public int OriginBottom;

	public nint OriginMonitor;

	public Native.POINT MinTrack;

	public Native.POINT MaxTrack;

	public WndDbEntry Entry;

	/// <summary>hooks.c 的 state.ignorectrl：自己补发的 Ctrl 不算"用户按着 Ctrl"。</summary>
	public bool IgnoreCtrl;
}

internal static class AltDragCore
{
	/// <summary>AERO_THRESHOLD（hooks.c:33）。</summary>
	internal const int AeroThreshold = 5;

	/// <summary>AltDrag 的默认吸附阈值（hooks.c:2246）。</summary>
	internal const int DefaultSnapThreshold = 20;

	/// <summary>九宫格分界：38% / 62%（hooks.c:1700-1710）。</summary>
	private const double SplitLow = 0.38;

	private const double SplitHigh = 0.62;

	private static int Clamp(int v, int lo, int hi)
	{
		if (v < lo)
		{
			return lo;
		}
		if (v > hi)
		{
			return hi;
		}
		return v;
	}

	/// <summary>
	/// hooks.c:1619-1625：先按系统默认值填一份 MINMAXINFO，再让目标窗口自己填
	/// ptMinTrackSize / ptMaxTrackSize。系统的 SendMessage 会替 0..WM_USER 的消息做封送，
	/// 所以这个指针跨进程是安全的（这也是 AltDrag 敢这么写的原因）。
	/// 原实现 ptMaxTrackSize.y 误用了 SM_CXMAXTRACK，这里按意图取 SM_CYMAXTRACK。
	/// </summary>
	internal static void QueryMinMax(nint hwnd, Native.POINT pt, out Native.POINT minTrack, out Native.POINT maxTrack)
	{
		minTrack = new Native.POINT(Native.GetSystemMetrics(Native.SM_CXMINTRACK), Native.GetSystemMetrics(Native.SM_CYMINTRACK));
		maxTrack = new Native.POINT(Native.GetSystemMetrics(Native.SM_CXMAXTRACK), Native.GetSystemMetrics(Native.SM_CYMAXTRACK));
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		Native.MONITORINFO monitorInfoAt = Native.GetMonitorInfoAt(pt);
		int num = Marshal.SizeOf(typeof(Native.MINMAXINFO));
		IntPtr intPtr = IntPtr.Zero;
		try
		{
			intPtr = Marshal.AllocHGlobal(num);
			Native.MINMAXINFO structure = new Native.MINMAXINFO
			{
				ptMaxSize = new Native.POINT(Native.GetSystemMetrics(Native.SM_CXMAXIMIZED), Native.GetSystemMetrics(Native.SM_CYMAXIMIZED)),
				ptMaxPosition = new Native.POINT(monitorInfoAt.rcWork.Left - 8, monitorInfoAt.rcWork.Top - 8),
				ptMinTrackSize = minTrack,
				ptMaxTrackSize = maxTrack
			};
			Marshal.StructureToPtr(structure, intPtr, fDeleteOld: false);
			if (Native.SendMessageTimeoutW(hwnd, Native.WM_GETMINMAXINFO, IntPtr.Zero, intPtr, Native.SMTO_ABORTIFHUNG, 500u, out var _) != IntPtr.Zero)
			{
				Native.MINMAXINFO mINMAXINFO = Marshal.PtrToStructure<Native.MINMAXINFO>(intPtr);
				if (mINMAXINFO.ptMinTrackSize.X > 0 && mINMAXINFO.ptMinTrackSize.Y > 0)
				{
					minTrack = mINMAXINFO.ptMinTrackSize;
				}
				if (mINMAXINFO.ptMaxTrackSize.X > 0 && mINMAXINFO.ptMaxTrackSize.Y > 0)
				{
					maxTrack = mINMAXINFO.ptMaxTrackSize;
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		finally
		{
			if (intPtr != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}
	}

	/// <summary>hooks.c:1583-1593：记下"按下那一刻"窗口的还原尺寸与所在显示器。</summary>
	internal static void RecordOrigin(AltState alt, nint hwnd, Native.RECT wnd)
	{
		alt.OriginMaximized = Native.IsZoomed(hwnd);
		alt.OriginMaximizedAtStart = alt.OriginMaximized;
		alt.OriginWidth = wnd.Width;
		alt.OriginHeight = wnd.Height;
		Native.WINDOWPLACEMENT lpwndpl = new Native.WINDOWPLACEMENT
		{
			length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
		};
		if (Native.GetWindowPlacement(hwnd, ref lpwndpl) && lpwndpl.rcNormalPosition.Width > 0 && lpwndpl.rcNormalPosition.Height > 0)
		{
			alt.OriginWidth = lpwndpl.rcNormalPosition.Width;
			alt.OriginHeight = lpwndpl.rcNormalPosition.Height;
		}
		alt.OriginMonitor = Native.MonitorFromWindow(hwnd, 2u);
	}

	/// <summary>
	/// hooks.c:1661-1683：移动的 offset。
	/// 最大化窗口按"光标在窗口里的比例"折算到还原尺寸，然后立刻还原并重算一帧 —— 这样
	/// 从最大化状态往下拖时，窗口的抓点不会突然跳到别处。
	/// </summary>
	internal static void SetupMove(AltState alt, nint hwnd, Native.POINT pt, ref Native.RECT wnd)
	{
		// hooks.c:1650-1659：先判"要不要还原成贴边之前的大小"，随即把 wnddb 的 restore 清掉。
		// 清标记放在分支之前：任何一条提前 return 都不会留下"下次还会还原"的脏状态。
		WndDbEntry entry = alt.Entry;
		bool restore = entry != null && entry.Restore && entry.LastWidth == alt.OriginWidth && entry.LastHeight == alt.OriginHeight;
		if (restore)
		{
			alt.OriginWidth = entry.Width;
			alt.OriginHeight = entry.Height;
		}
		if (entry != null)
		{
			entry.Restore = false;
		}
		if (alt.OriginMaximized || Native.IsZoomed(hwnd))
		{
			alt.OffsetX = (int)((double)(pt.X - wnd.Left) / (double)Math.Max(1, wnd.Width) * (double)alt.OriginWidth);
			alt.OffsetY = (int)((double)(pt.Y - wnd.Top) / (double)Math.Max(1, wnd.Height) * (double)alt.OriginHeight);
			Native.WINDOWPLACEMENT lpwndpl = new Native.WINDOWPLACEMENT
			{
				length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
			};
			if (Native.GetWindowPlacement(hwnd, ref lpwndpl))
			{
				lpwndpl.showCmd = Native.SW_RESTORE;
				Native.SetWindowPlacement(hwnd, ref lpwndpl);
			}
			else
			{
				Native.ShowWindow(hwnd, Native.SW_RESTORE);
			}
			if (Native.GetWindowRect(hwnd, out var lpRect))
			{
				wnd = lpRect;
			}
			alt.OriginMaximized = false;
			return;
		}
		// hooks.c:1673-1677：从 Aero 贴边区拖回来时，先把窗口按"贴边之前的大小"摆到 pt-offset，
		// 再让下面算好的 offset 生效。只改 offset 不摆窗口的话，第一帧会从贴边后那个小矩形
		// 一步跳到光标下面 —— 看起来就是抖一下，尺寸也要等下一帧才还原。
		if (restore)
		{
			alt.OffsetX = (int)((double)(pt.X - wnd.Left) / (double)Math.Max(1, wnd.Width) * (double)alt.OriginWidth);
			alt.OffsetY = (int)((double)(pt.Y - wnd.Top) / (double)Math.Max(1, wnd.Height) * (double)alt.OriginHeight);
			// hooks.c:1679：源码这里就是 MoveWindow(..., TRUE)，和 Aero 分支同一个动作 —— 让窗口自己去过一遍
			// WM_WINDOWPOSCHANGING / WM_SIZE，别用 SetWindowPos 那条更"安静"的路子。
			Native.MoveWindow(hwnd, pt.X - alt.OffsetX, pt.Y - alt.OffsetY, alt.OriginWidth, alt.OriginHeight, true);
			if (Native.GetWindowRect(hwnd, out var lpRectRestored))
			{
				wnd = lpRectRestored;
			}
			return;
		}
		alt.OffsetX = pt.X - wnd.Left;
		alt.OffsetY = pt.Y - wnd.Top;
	}

	/// <summary>
	/// hooks.c:1685-1746：缩放的起手。
	/// 最大化窗口先恢复成"铺满整块显示器"（不是还原尺寸），再按 38%/62% 把窗口切成九宫格。
	/// </summary>
	internal static void SetupResize(AltState alt, nint hwnd, Native.POINT pt, ref Native.RECT wnd)
	{
		Native.MONITORINFO monitorInfoAt = Native.GetMonitorInfoAt(pt);
		Native.RECT rcMonitor = monitorInfoAt.rcMonitor;
		// hooks.c:1688 把 rcNormalPosition 设成 fmon（整块显示器），1689 的 wnd 却是 mon（rcWork 工作区）。
		// 这两者不是一回事（任务栏占掉的那一条）：恢复位置铺满整块屏，真正写窗口的尺寸按工作区。
		Native.RECT rcWork = monitorInfoAt.rcWork;
		if (alt.OriginMaximized || Native.IsZoomed(hwnd))
		{
			Native.WINDOWPLACEMENT lpwndpl = new Native.WINDOWPLACEMENT
			{
				length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
			};
			if (Native.GetWindowPlacement(hwnd, ref lpwndpl))
			{
				lpwndpl.rcNormalPosition = rcMonitor;
				lpwndpl.showCmd = Native.SW_RESTORE;
				Native.SetWindowPlacement(hwnd, ref lpwndpl);
			}
			else
			{
				Native.ShowWindow(hwnd, Native.SW_RESTORE);
			}
			wnd = rcWork;
			alt.OriginWidth = wnd.Width;
			alt.OriginHeight = wnd.Height;
			alt.OriginMaximized = false;
			// hooks.c:1710：MoveWindow(state.hwnd, wnd.left, wnd.top, origin.width, origin.height, TRUE)
			Native.MoveWindow(hwnd, wnd.Left, wnd.Top, wnd.Width, wnd.Height, true);
		}
		Rebase(alt, move: false, pt, wnd);
		SetResizeCursor(alt.EdgeX, alt.EdgeY);
	}

	/// <summary>hooks.c:1796-1817：按九宫格换成对应的缩放光标。</summary>
	internal static void SetResizeCursor(DragEdge x, DragEdgeY y)
	{
		try
		{
			int num;
			if ((y == DragEdgeY.Top && x == DragEdge.Left) || (y == DragEdgeY.Bottom && x == DragEdge.Right))
			{
				num = Native.IDC_SIZENWSE;
			}
			else if ((y == DragEdgeY.Top && x == DragEdge.Right) || (y == DragEdgeY.Bottom && x == DragEdge.Left))
			{
				num = Native.IDC_SIZENESW;
			}
			else if (y != DragEdgeY.Center && x == DragEdge.Center)
			{
				num = Native.IDC_SIZENS;
			}
			else if (x != DragEdge.Center && y == DragEdgeY.Center)
			{
				num = Native.IDC_SIZEWE;
			}
			else
			{
				num = Native.IDC_SIZEALL;
			}
			nint hCursor = Native.LoadCursorW(IntPtr.Zero, (nint)num);
			if (hCursor != IntPtr.Zero)
			{
				Native.SetCursor(hCursor);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	/// <summary>
	/// 按"当前窗口矩形"重新算一次 offset / 被拖的边。起拖时用，降级落账后也用它校正。
	/// 移动：offset = 光标 - 左上角；缩放：38%/62% 九宫格，CENTER 记绝对坐标、RIGHT/BOTTOM 记到边的距离。
	/// </summary>
	internal static void Rebase(AltState alt, bool move, Native.POINT pt, Native.RECT wnd)
	{
		if (alt == null)
		{
			return;
		}
		if (move)
		{
			alt.OffsetX = pt.X - wnd.Left;
			alt.OffsetY = pt.Y - wnd.Top;
			return;
		}
		int num = wnd.Width;
		int num2 = wnd.Height;
		if (num <= 0)
		{
			num = 1;
		}
		if (num2 <= 0)
		{
			num2 = 1;
		}
		if ((double)(pt.X - wnd.Left) < (double)num * SplitLow)
		{
			alt.EdgeX = DragEdge.Left;
			alt.OffsetX = pt.X - wnd.Left;
		}
		else if ((double)(pt.X - wnd.Left) < (double)num * SplitHigh)
		{
			alt.EdgeX = DragEdge.Center;
			alt.OffsetX = pt.X;
		}
		else
		{
			alt.EdgeX = DragEdge.Right;
			alt.OffsetX = wnd.Right - pt.X;
		}
		if ((double)(pt.Y - wnd.Top) < (double)num2 * SplitLow)
		{
			alt.EdgeY = DragEdgeY.Top;
			alt.OffsetY = pt.Y - wnd.Top;
		}
		else if ((double)(pt.Y - wnd.Top) < (double)num2 * SplitHigh)
		{
			alt.EdgeY = DragEdgeY.Center;
			alt.OffsetY = pt.Y;
		}
		else
		{
			alt.EdgeY = DragEdgeY.Bottom;
			alt.OffsetY = wnd.Bottom - pt.Y;
		}
		// Input-Plus 的偏离（用户可见的一条）：AltDrag 在"只有一条轴落在中间带"时把那一轴锁死，
		// 抓左边框中段上下拖只改宽、不改高（hooks.c:1700-1710 的九宫格原样透传）。用户按的是
		// "按住触发键随便拖，两个方向都能调"，所以把锁死的那一轴换成"离得近的那条边"：
		// 上半→上边，下半→下边，左半→左边，右半→右边。两轴都落在中间带时仍走 AltDrag 的
		// 四向对称扩张，那条手感不动。
		bool xCenter = alt.EdgeX == DragEdge.Center;
		bool yCenter = alt.EdgeY == DragEdgeY.Center;
		if (xCenter && !yCenter)
		{
			if ((pt.X - wnd.Left) * 2 < num)
			{
				alt.EdgeX = DragEdge.Left;
				alt.OffsetX = pt.X - wnd.Left;
			}
			else
			{
				alt.EdgeX = DragEdge.Right;
				alt.OffsetX = wnd.Right - pt.X;
			}
		}
		else if (yCenter && !xCenter)
		{
			if ((pt.Y - wnd.Top) * 2 < num2)
			{
				alt.EdgeY = DragEdgeY.Top;
				alt.OffsetY = pt.Y - wnd.Top;
			}
			else
			{
				alt.EdgeY = DragEdgeY.Bottom;
				alt.OffsetY = wnd.Bottom - pt.Y;
			}
		}
		alt.OriginRight = wnd.Right;
		alt.OriginBottom = wnd.Bottom;
	}

	/// <summary>
	/// hooks.c:663-706 的 AutoRemaximize：原来最大化的窗口被拖到别的显示器上时，
	/// 在新显示器里按 origin 尺寸居中、重新最大化，并把本次拖动锁住（window 不再跟光标）。
	/// 返回 true = 这一帧已经处理完，调用方不要再去算几何、也不要写窗口。
	/// </summary>
	internal static bool AutoRemaximize(AltState alt, nint hwnd, Native.POINT pt)
	{
		if (!alt.OriginMaximizedAtStart || alt.Locked)
		{
			return false;
		}
		nint monitor = Native.MonitorFromPoint(pt, 2u);
		if (monitor == IntPtr.Zero || monitor == alt.OriginMonitor)
		{
			return false;
		}
		Native.MONITORINFO monitorInfoAt = Native.GetMonitorInfoAt(pt);
		Native.RECT rcWork = monitorInfoAt.rcWork;
		Native.RECT rcMonitor = monitorInfoAt.rcMonitor;
		if (rcWork.Width <= 0 || rcWork.Height <= 0)
		{
			return false;
		}
		Native.WINDOWPLACEMENT lpwndpl = new Native.WINDOWPLACEMENT
		{
			length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
		};
		if (!Native.GetWindowPlacement(hwnd, ref lpwndpl))
		{
			return false;
		}
		// hooks.c:672-679：用 fmon 定原点、rcWork 定中心，还原尺寸取 origin；
		// 已经是最大化的话先 SW_RESTORE 一次，否则 SetWindowPlacement 不会重算还原矩形。
		int left = rcMonitor.Left + rcWork.Width / 2 - alt.OriginWidth / 2;
		int top = rcMonitor.Top + rcWork.Height / 2 - alt.OriginHeight / 2;
		lpwndpl.rcNormalPosition = new Native.RECT(left, top, left + alt.OriginWidth, top + alt.OriginHeight);
		if (Native.IsZoomed(hwnd))
		{
			lpwndpl.showCmd = Native.SW_RESTORE;
			Native.SetWindowPlacement(hwnd, ref lpwndpl);
		}
		lpwndpl.showCmd = Native.SW_MAXIMIZE;
		Native.SetWindowPlacement(hwnd, ref lpwndpl);
		// hooks.c:681-683：把新显示器记成 origin（源码自称 dirty hack），并锁住本次拖动。
		alt.OriginMonitor = monitor;
		alt.Locked = true;
		return true;
	}
	/// <summary>
	/// hooks.c:1974-1997 的 RESTORE_TIMER：AutoRemaximize=2 的一秒后 —— 解锁、按光标在新显示器里的
	/// 相对位置重算 offset、SW_RESTORE，调用方随后补一帧 MouseMove()。
	/// </summary>
	internal static bool RestoreAfterRemaximize(AltState alt, nint hwnd, Native.POINT pt)
	{
		alt.Locked = false;
		Native.MONITORINFO monitorInfoAt = Native.GetMonitorInfoAt(pt);
		Native.RECT rcMonitor = monitorInfoAt.rcMonitor;
		if (rcMonitor.Width <= 0 || rcMonitor.Height <= 0)
		{
			return false;
		}
		alt.OffsetX = (int)Math.Round((double)(pt.X - rcMonitor.Left) / (double)rcMonitor.Width * (double)alt.OriginWidth);
		alt.OffsetY = (int)Math.Round((double)(pt.Y - rcMonitor.Top) / (double)rcMonitor.Height * (double)alt.OriginHeight);
		Native.WINDOWPLACEMENT lpwndpl = new Native.WINDOWPLACEMENT
		{
			length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
		};
		if (!Native.GetWindowPlacement(hwnd, ref lpwndpl))
		{
			return false;
		}
		lpwndpl.showCmd = Native.SW_RESTORE;
		return Native.SetWindowPlacement(hwnd, ref lpwndpl);
	}

	internal enum AeroOutcome
	{
		None,
		Written,
		Maximized
	}

	/// <summary>
	/// hooks.c:726-873 的移动部分：先按 pt-offset 定位，再按 Aero 区（角 2*5px、边 5px）改写，
	/// 都不命中就交给吸附。返回 Written 表示这一帧由 Aero 决定（此时不走吸附），
	/// Maximized 表示已经 SetWindowPlacement 最大化（这一帧不写窗口）。
	/// </summary>
	internal static AeroOutcome MoveAero(AltState alt, AppConfig cfg, nint hwnd, Native.POINT pt, ref int posx, ref int posy, ref int wndwidth, ref int wndheight)
	{
		// 源码这里是独立的 Aero 开关（AltDrag.ini 的 Aero），和 AutoSnap 互不影响：
		// 把自动吸附全关掉，贴边分屏/顶部最大化依然工作。
		if (!cfg.AeroSnap)
		{
			return AeroOutcome.None;
		}
		Native.MONITORINFO monitorInfoAt = Native.GetMonitorInfoAt(pt);
		Native.RECT rcWork = monitorInfoAt.rcWork;
		Native.RECT rcMonitor = monitorInfoAt.rcMonitor;
		if (rcWork.Width <= 0 || rcWork.Height <= 0)
		{
			return AeroOutcome.None;
		}
		bool max = Native.IsZoomed(hwnd);
		// 还原窗口：光标离开"贴边带"就先把窗口还原，免得 Aero 直接改写最大化窗口的矩形
		if (max && (pt.Y >= rcWork.Top + AeroThreshold || (rcMonitor.Left < pt.X && pt.X < rcWork.Left + 2 * AeroThreshold) || (rcWork.Right - 2 * AeroThreshold < pt.X && pt.X < rcMonitor.Right)))
		{
			Native.WINDOWPLACEMENT lpwndpl = new Native.WINDOWPLACEMENT
			{
				length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
			};
			if (Native.GetWindowPlacement(hwnd, ref lpwndpl))
			{
				lpwndpl.showCmd = Native.SW_RESTORE;
				Native.SetWindowPlacement(hwnd, ref lpwndpl);
				wndwidth = lpwndpl.rcNormalPosition.Width;
				wndheight = lpwndpl.rcNormalPosition.Height;
			}
			else
			{
				Native.ShowWindow(hwnd, Native.SW_RESTORE);
				if (Native.GetWindowRect(hwnd, out var lpRect))
				{
					wndwidth = lpRect.Width;
					wndheight = lpRect.Height;
				}
			}
		}
		WndDbEntry entry = alt.Entry;
		int num = rcWork.Right - rcWork.Left;
		int num2 = rcWork.Bottom - rcWork.Top;
		int num3 = 2 * AeroThreshold;
		bool flag = false;
		if (pt.X < rcWork.Left + num3 && pt.Y < rcWork.Top + num3)
		{
			// 左上四分之一
			wndwidth = num / 2;
			wndheight = num2 / 2;
			posx = rcWork.Left;
			posy = rcWork.Top;
			flag = true;
		}
		else if (rcWork.Right - num3 < pt.X && pt.Y < rcWork.Top + num3)
		{
			// 右上四分之一
			wndwidth = Clamp(num / 2, alt.MinTrack.X, alt.MaxTrack.X);
			wndheight = num2 / 2;
			posx = rcWork.Right - wndwidth;
			posy = rcWork.Top;
			flag = true;
		}
		else if (pt.X < rcWork.Left + num3 && rcWork.Bottom - num3 < pt.Y)
		{
			// 左下四分之一
			wndwidth = num / 2;
			wndheight = Clamp(num2 / 2, alt.MinTrack.Y, alt.MaxTrack.Y);
			posx = rcWork.Left;
			posy = rcWork.Bottom - wndheight;
			flag = true;
		}
		else if (rcWork.Right - num3 < pt.X && rcWork.Bottom - num3 < pt.Y)
		{
			// 右下四分之一
			wndwidth = Clamp(num / 2, alt.MinTrack.X, alt.MaxTrack.X);
			wndheight = Clamp(num2 / 2, alt.MinTrack.Y, alt.MaxTrack.Y);
			posx = rcWork.Right - wndwidth;
			posy = rcWork.Bottom - wndheight;
			flag = true;
		}
		else if (pt.Y < rcWork.Top + AeroThreshold)
		{
			// 上边：hooks.c:784-802。已经最大化（这一帧开头取到的快照）就什么都不做，
			// 只是这一帧不写窗口（源码这里是裸 return）；只有本来不是最大化才去最大化，
			// 而且要先把还原矩形摆到这块显示器中央 —— 否则从最大化拖到别的屏再还原时，窗口会跳回原来那块屏。
			if (!max)
			{
				if (entry != null)
				{
					entry.Restore = false;
				}
				Native.WINDOWPLACEMENT lpwndpl2 = new Native.WINDOWPLACEMENT
				{
					length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
				};
				if (Native.GetWindowPlacement(hwnd, ref lpwndpl2))
				{
					int left = rcMonitor.Left + (rcWork.Right - rcWork.Left) / 2 - alt.OriginWidth / 2;
					int top = rcMonitor.Top + (rcWork.Bottom - rcWork.Top) / 2 - alt.OriginHeight / 2;
					lpwndpl2.rcNormalPosition = new Native.RECT(left, top, left + alt.OriginWidth, top + alt.OriginHeight);
					lpwndpl2.showCmd = Native.SW_MAXIMIZE;
					Native.SetWindowPlacement(hwnd, ref lpwndpl2);
				}
				else
				{
					Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
				}
			}
			return AeroOutcome.Maximized;
		}
		else if (pt.Y < rcWork.Top + num3)
		{
			// 上边的 10px 带：横向铺满、上半屏
			wndwidth = Clamp(num, alt.MinTrack.X, alt.MaxTrack.X);
			wndheight = num2 / 2;
			posx = rcWork.Left + num / 2 - wndwidth / 2;
			posy = rcWork.Top;
			flag = true;
		}
		else if (rcWork.Bottom - AeroThreshold < pt.Y)
		{
			// 下边：横向铺满、下半屏
			wndwidth = Clamp(num, alt.MinTrack.X, alt.MaxTrack.X);
			wndheight = Clamp(num2 / 2, alt.MinTrack.Y, alt.MaxTrack.Y);
			posx = rcWork.Left + num / 2 - wndwidth / 2;
			posy = rcWork.Bottom - wndheight;
			flag = true;
		}
		else if (pt.X < rcWork.Left + AeroThreshold)
		{
			// 左边：左半屏，垂直居中铺满
			wndwidth = num / 2;
			wndheight = Clamp(num2, alt.MinTrack.Y, alt.MaxTrack.Y);
			posx = rcWork.Left;
			posy = rcWork.Top + num2 / 2 - wndheight / 2;
			flag = true;
		}
		else if (rcWork.Right - AeroThreshold < pt.X)
		{
			// 右边：右半屏，垂直居中铺满
			wndwidth = Clamp(num / 2, alt.MinTrack.X, alt.MaxTrack.X);
			wndheight = Clamp(num2, alt.MinTrack.Y, alt.MaxTrack.Y);
			posx = rcWork.Right - wndwidth;
			posy = rcWork.Top + num2 / 2 - wndheight / 2;
			flag = true;
		}
		if (flag)
		{
			if (entry != null)
			{
				entry.Restore = true;
			}
		}
		else if (entry != null && entry.Restore)
		{
			// 从贴边区拖回中间：把原尺寸还回去
			entry.Restore = false;
			wndwidth = alt.OriginWidth;
			wndheight = alt.OriginHeight;
		}
		if (entry != null && entry.Restore)
		{
			entry.Width = alt.OriginWidth;
			entry.Height = alt.OriginHeight;
			return AeroOutcome.Written;
		}
		return AeroOutcome.None;
	}

	/// <summary>Aero 写完窗口后回读真实尺寸（hooks.c:869-870）。</summary>
	internal static void NoteAeroWritten(AltState alt, nint hwnd)
	{
		WndDbEntry entry = alt.Entry;
		if (entry != null && Native.GetWindowRect(hwnd, out var lpRect))
		{
			entry.LastWidth = lpRect.Width;
			entry.LastHeight = lpRect.Height;
		}
	}

	/// <summary>
	/// hooks.c:875-924：缩放几何。全部以"当前窗口矩形"为基准，不用起点矩形 ——
	/// 这样目标程序夹了尺寸、或者分块重画（PuTTY 那类）时，下一帧会自动把误差吸收掉。
	/// </summary>
	internal static void ResizeGeometry(AltState alt, Native.POINT pt, Native.RECT wnd, ref int posx, ref int posy, ref int wndwidth, ref int wndheight)
	{
		WndDbEntry entry = alt.Entry;
		if (entry != null)
		{
			entry.Restore = false;
		}
		if (alt.EdgeX == DragEdge.Center && alt.EdgeY == DragEdgeY.Center)
		{
			// 中间：四向扩张（左右上下一起长），每帧把 offset 归到当前光标
			int num = pt.X - alt.OffsetX;
			int num2 = pt.Y - alt.OffsetY;
			posx = wnd.Left - num;
			posy = wnd.Top - num2;
			wndwidth = wnd.Width + 2 * num;
			wndheight = wnd.Height + 2 * num2;
			alt.OffsetX = pt.X;
			alt.OffsetY = pt.Y;
			return;
		}
		if (alt.EdgeY == DragEdgeY.Top)
		{
			wndheight = Clamp(wnd.Bottom - pt.Y + alt.OffsetY, alt.MinTrack.Y, alt.MaxTrack.Y);
			posy = alt.OriginBottom - wndheight;
		}
		else if (alt.EdgeY == DragEdgeY.Center)
		{
			posy = wnd.Top;
			wndheight = wnd.Height;
		}
		else
		{
			posy = wnd.Top;
			wndheight = pt.Y - posy + alt.OffsetY;
		}
		if (alt.EdgeX == DragEdge.Left)
		{
			wndwidth = Clamp(wnd.Right - pt.X + alt.OffsetX, alt.MinTrack.X, alt.MaxTrack.X);
			posx = alt.OriginRight - wndwidth;
		}
		else if (alt.EdgeX == DragEdge.Center)
		{
			posx = wnd.Left;
			wndwidth = wnd.Width;
		}
		else
		{
			posx = wnd.Left;
			wndwidth = pt.X - posx + alt.OffsetX;
		}
	}

	/// <summary>
	/// hooks.c:1649-1684 的移动双击：最大化；如果窗口原来在别的显示器，
	/// 先把还原矩形挪到光标这块显示器中央再最大化（否则还原时窗口会跑到另一块屏）。
	/// </summary>
	internal static void DoubleClickMaximize(nint hwnd, Native.POINT pt)
	{
		Native.WINDOWPLACEMENT lpwndpl = new Native.WINDOWPLACEMENT
		{
			length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
		};
		if (!Native.GetWindowPlacement(hwnd, ref lpwndpl))
		{
			return;
		}
		// hooks.c:1630-1644：双击时如果窗口已经最大化，这里什么都不做（再 SetWindowPlacement 一次
		// 等于把还原矩形重写一遍，Aero 双击还原时就回不到原来那块地方了）。
		if (Native.IsZoomed(hwnd))
		{
			return;
		}
		nint num = Native.MonitorFromWindow(hwnd, 2u);
		nint num2 = Native.MonitorFromPoint(pt, 2u);
		if (num2 != IntPtr.Zero && num != num2)
		{
			// hooks.c:1636-1641：rcWork 定中心，rcMonitor 定原点 —— 源码就是 fmon.left+(mon.right-mon.left)/2。
			Native.MONITORINFO monitorInfoAt = Native.GetMonitorInfoAt(pt);
			Native.RECT rcMonitor = monitorInfoAt.rcMonitor;
			Native.RECT rcWork = monitorInfoAt.rcWork;
			int num3 = (lpwndpl.rcNormalPosition.Width > 0) ? lpwndpl.rcNormalPosition.Width : Math.Max(1, rcWork.Width / 2);
			int num4 = (lpwndpl.rcNormalPosition.Height > 0) ? lpwndpl.rcNormalPosition.Height : Math.Max(1, rcWork.Height / 2);
			lpwndpl.rcNormalPosition = new Native.RECT(rcMonitor.Left + rcWork.Width / 2 - num3 / 2, rcMonitor.Top + rcWork.Height / 2 - num4 / 2, 0, 0);
			lpwndpl.rcNormalPosition.Right = lpwndpl.rcNormalPosition.Left + num3;
			lpwndpl.rcNormalPosition.Bottom = lpwndpl.rcNormalPosition.Top + num4;
		}
		lpwndpl.showCmd = Native.SW_MAXIMIZE;
		Native.SetWindowPlacement(hwnd, ref lpwndpl);
	}

	/// <summary>hooks.c:1749-1794 的缩放双击：按光标所在的九宫格把窗口摆成那半屏 / 那块四分之一。</summary>
	internal static void DoubleClickAeroHalf(nint hwnd, Native.POINT pt)
	{
		// hooks.c:1686-1712：最大化窗口先按"铺满工作区"的尺寸还原成普通窗口，
		// 后面的九宫格比例与新位置全都以这个矩形为准。
		bool maximized = Native.IsZoomed(hwnd);
		Native.MONITORINFO monitorInfoAt = Native.GetMonitorInfoAt(pt);
		Native.RECT rcWork = monitorInfoAt.rcWork;
		if (rcWork.Width <= 0 || rcWork.Height <= 0)
		{
			return;
		}
		Native.RECT wnd;
		if (maximized)
		{
			wnd = rcWork;
			Native.WINDOWPLACEMENT lpwndpl = new Native.WINDOWPLACEMENT
			{
				length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
			};
			if (Native.GetWindowPlacement(hwnd, ref lpwndpl))
			{
				lpwndpl.rcNormalPosition = monitorInfoAt.rcMonitor;
				lpwndpl.showCmd = Native.SW_RESTORE;
				Native.SetWindowPlacement(hwnd, ref lpwndpl);
			}
			Native.MoveWindow(hwnd, wnd.Left, wnd.Top, wnd.Width, wnd.Height, true);
		}
		else if (!Native.GetWindowRect(hwnd, out wnd))
		{
			return;
		}
		// hooks.c:1716：这里刻意不用 rcNormalPosition 的宽高 —— 用的是"看得见"的矩形。
		AltState alt = new AltState();
		RecordOrigin(alt, hwnd, wnd);
		if (maximized)
		{
			// hooks.c:1706-1708：origin 的宽高被换成工作区尺寸。
			alt.OriginWidth = wnd.Width;
			alt.OriginHeight = wnd.Height;
		}
		QueryMinMax(hwnd, pt, out alt.MinTrack, out alt.MaxTrack);
		int num = rcWork.Width;
		int num2 = rcWork.Height;
		double num3 = (double)(pt.X - wnd.Left) / (double)Math.Max(1, wnd.Width);
		double num4 = (double)(pt.Y - wnd.Top) / (double)Math.Max(1, wnd.Height);
		DragEdge dragEdge = ((num3 < SplitLow) ? DragEdge.Left : ((num3 < SplitHigh) ? DragEdge.Center : DragEdge.Right));
		DragEdgeY dragEdgeY = ((num4 < SplitLow) ? DragEdgeY.Top : ((num4 < SplitHigh) ? DragEdgeY.Center : DragEdgeY.Bottom));
		int num5 = Clamp(num / 2, alt.MinTrack.X, alt.MaxTrack.X);
		int num6 = Clamp(num2 / 2, alt.MinTrack.Y, alt.MaxTrack.Y);
		int num7 = rcWork.Left;
		int num8 = rcWork.Top;
		if (dragEdgeY == DragEdgeY.Center)
		{
			num6 = Clamp(num2, alt.MinTrack.Y, alt.MaxTrack.Y);
			num8 += num2 / 2 - num6 / 2;
		}
		else if (dragEdgeY == DragEdgeY.Bottom)
		{
			num8 = rcWork.Bottom - num6;
		}
		if (dragEdge == DragEdge.Center && dragEdgeY != DragEdgeY.Center)
		{
			num5 = Clamp(num, alt.MinTrack.X, alt.MaxTrack.X);
			num7 += num / 2 - num5 / 2;
		}
		else if (dragEdge == DragEdge.Center)
		{
			// hooks.c:1771-1773：正中间那一格保持窗口现在的宽度与左边。
			num5 = wnd.Width;
			num7 = wnd.Left;
		}
		else if (dragEdge == DragEdge.Right)
		{
			num7 = rcWork.Right - num5;
		}
		// hooks.c:1778：源码这里也是 MoveWindow，不是 SetWindowPos。
		Native.MoveWindow(hwnd, num7, num8, num5, num6, true);
		// hooks.c:1780-1790：回读真实尺寸（有些窗口会按块对齐），再用 origin 的宽高记进 wnddb。
		WndDbEntry entry = WndDb.Touch(hwnd);
		if (Native.GetWindowRect(hwnd, out var after))
		{
			entry.LastWidth = after.Width;
			entry.LastHeight = after.Height;
		}
		if (!entry.Restore)
		{
			entry.Width = alt.OriginWidth;
			entry.Height = alt.OriginHeight;
			entry.Restore = true;
		}
	}

	/// <summary>
	/// 自己补发的假 Ctrl 正在路上。钩子线程看到 <see cref="Native.LLKHF_INJECTED"/> 且这个标志立着时，
	/// 不能把它当成"用户按了触发键" —— 触发键配成 Ctrl 时，那一对假 Ctrl 会当场把拖动结束掉。
	/// （源码里状态机和钩子在同一份代码里，一个 ignorectrl 就够；这里是两个模块，得有个标志。）
	/// </summary>
	internal static volatile bool InjectedBurst;

	/// <summary>
	/// hooks.c:1857-1863：起拖时无条件发一对 Ctrl 按下/抬起，把"用户按着触发键"这件事从窗口眼里瞒过去
	/// （否则按住 Alt 点窗口，窗口会去激活菜单栏）。
	/// </summary>
	internal static void MasqueradeAsControl(AltState alt)
	{
		bool priorIgnoreCtrl = alt != null && alt.IgnoreCtrl;
		try
		{
			// hooks.c:1858-1863：SendInput 前后把 ignorectrl 立起来又放下，
			// 免得这一对"假的 Ctrl"被自己的拖动逻辑当成用户真的按了 Ctrl（那会把光标夹在一块屏里）。
			// 触发键就是 Ctrl 时（hooks.c:949-953）这个标志是整个拖动期间常立的，所以这里只做
			// "临时立起、事后恢复原值"，不能无脑写成 false。
			if (alt != null)
			{
				alt.IgnoreCtrl = true;
			}
			InjectedBurst = true;
			Native.KEYBDINPUT[] array = new Native.KEYBDINPUT[2];
			array[0].wVk = (ushort)Native.VK_CONTROL;
			array[1].wVk = (ushort)Native.VK_CONTROL;
			array[1].dwFlags = 2u;
			nint messageExtraInfo = Native.GetMessageExtraInfo();
			array[0].dwExtraInfo = messageExtraInfo;
			array[1].dwExtraInfo = messageExtraInfo;
			Native.INPUT[] array2 = new Native.INPUT[2];
			for (int i = 0; i < 2; i++)
			{
				array2[i].type = 1u;
				array2[i].u.ki = array[i];
			}
			Native.SendInput(2u, array2, Marshal.SizeOf(typeof(Native.INPUT)));
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		finally
		{
			InjectedBurst = false;
			if (alt != null)
			{
				alt.IgnoreCtrl = priorIgnoreCtrl;
			}
		}
	}
}
