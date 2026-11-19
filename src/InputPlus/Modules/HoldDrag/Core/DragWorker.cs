#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
// HoldDrag · Core/DragWorker.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InputPlus.Modules.HoldDrag;

internal sealed class DragWorker : IDisposable
{
	public const uint WM_DOMOUSEMOVE = 32775u;

	public const uint WM_DOWORK = 32774u;

	private readonly Action<Native.POINT> _onMove;

	private readonly ConcurrentQueue<Action> _work = new ConcurrentQueue<Action>();

	private Thread _thread;

	private uint _threadId;

	private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(initialState: false);

	public static long MaxMoveMs;

	public static long MaxIdleMs;

	public static int MaxDrained;

	/// <summary>从"钩子排进队列"到"工作线程真正开始写窗口"的最大/累计延迟（毫秒）。</summary>
	public static long MaxLagMs;

	public static long LagSumMs;

	public static long LagCount;

	private volatile bool _disposed;

	/// <summary>
	/// 鼠标移动的"信箱"：只留**最新**一个坐标，并且线程消息队列里最多一条在途消息。
	///
	/// 以前每个鼠标事件都 PostThreadMessageW 一条，队列深度没有上限。工作线程一旦被抢走
	/// 百来毫秒（实测合并峰值 47 条），这 47 个过期坐标就排在队列里；线程回来时窗口
	/// 要从"几十毫秒前的位置"一路追到光标现在的位置 —— 手感上就是"手停了窗口还在跑"。
	/// 现在坐标只写进信箱、在途最多一条，滞后上限固定成"写一次窗口"的时间。
	/// </summary>
	private readonly object _moveGate = new object();

	private int _moveX;

	private int _moveY;

	private bool _movePending;

	private long _postedAt;

	public bool IsRunning
	{
		get
		{
			if (_thread != null && _ready.IsSet)
			{
				return !_disposed;
			}
			return false;
		}
	}

	public static void ResetStats()
	{
		MaxMoveMs = 0L;
		MaxIdleMs = 0L;
		MaxDrained = 0;
		MaxLagMs = 0L;
		LagSumMs = 0L;
		LagCount = 0L;
	}

	public DragWorker(Action<Native.POINT> onMove)
	{
		_onMove = onMove;
	}

	public void Start()
	{
		if (_thread == null)
		{
			_thread = new Thread(ThreadMain);
			_thread.IsBackground = true;
			/* Highest：这条线程平时阻塞在 GetMessage 上，只有"写一次窗口"（实测 2.4ms，
			   峰值 18.7ms）时才占 CPU。它被抢走的每一毫秒都会变成松手时窗口要追的距离，
			   所以提到 Highest，让写窗口这一步尽量不被普通线程打断。 */
			_thread.Priority = ThreadPriority.Highest;
			_thread.Name = "HoldDrag.DragWorker";
			_thread.Start();
			if (!_ready.Wait(3000))
			{
				Log.Error("拖动工作线程启动超时");
			}
		}
	}

	public void PostMove(Native.POINT pt)
	{
		if (_disposed || _threadId == 0)
		{
			return;
		}
		lock (_moveGate)
		{
			_moveX = pt.X;
			_moveY = pt.Y;
			// 已经有一条在途：只更新信箱里的坐标，不再往队列里塞第二条。
			// 工作线程醒来时按信箱里的最新坐标写一次就够了。
			if (_movePending)
			{
				return;
			}
			_movePending = true;
			_postedAt = Environment.TickCount64;
		}
		if (!Native.PostThreadMessageW(_threadId, 32775u, new IntPtr(pt.X), new IntPtr(pt.Y)))
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			lock (_moveGate)
			{
				_movePending = false;
			}
			if (lastWin32Error != 0)
			{
				Log.Trace("投递鼠标移动失败，Win32 错误码 " + lastWin32Error);
			}
		}
	}

	public void Post(Action work)
	{
		if (work != null && !_disposed && _threadId != 0)
		{
			_work.Enqueue(work);
			if (!Native.PostThreadMessageW(_threadId, 32774u, IntPtr.Zero, IntPtr.Zero))
			{
				// 投递失败（线程消息队列满）时不能 TryDequeue：队列是 FIFO，
				// 出队的会是最早那条而不是刚入队的这条，丢旧的留新的，语义反了。
				// 让活计留在队列里，下次投递成功时一起处理。
				Log.Trace("投递任务失败（线程消息队列可能已满），任务已留在队列中");
			}
		}
	}

	private void ThreadMain()
	{
		_threadId = Native.GetCurrentThreadId();
		Native.PeekMessageW(out var _, IntPtr.Zero, 0u, 0u, 0u);
		_ready.Set();
		try
		{
			while (!_disposed)
			{
				long tickCount = Environment.TickCount64;
				int messageW = Native.GetMessageW(out var lpMsg2, IntPtr.Zero, 0u, 0u);
				long num = Environment.TickCount64 - tickCount;
				if (num > MaxIdleMs)
				{
					MaxIdleMs = num;
				}
				if (messageW == 0 || messageW < 0)
				{
					break;
				}
				if (lpMsg2.hwnd != IntPtr.Zero)
				{
					continue;
				}
				if (lpMsg2.message == 18)
				{
					break;
				}
				if (lpMsg2.message == 32774)
				{
					Action result;
					while (_work.TryDequeue(out result))
					{
						try
						{
							result();
						}
						catch (Exception ex)
						{
							Log.Error(ex);
						}
					}
				}
				else
				{
					if (lpMsg2.message != 32775)
					{
						continue;
					}
					int num2 = 0;
					Native.MSG lpMsg3;
					while (Native.PeekMessageW(out lpMsg3, IntPtr.Zero, 32775u, 32775u, 1u))
					{
						lpMsg2 = lpMsg3;
						num2++;
					}
					if (num2 > MaxDrained)
					{
						MaxDrained = num2;
					}
					if (num2 > 0 && Log.Verbose)
					{
						Log.Trace("合并掉了 " + num2 + " 个过期的鼠标移动位置");
					}
					// 坐标一律取信箱里的最新值，消息自带的那对参数只当兜底：
					// 在途标记在调用 _onMove 之前清掉，这样写窗口的这两毫秒里新来的事件
					// 还能再排一条，最后一次移动不会丢。
					Native.POINT obj;
					long num4 = 0L;
					lock (_moveGate)
					{
						obj = new Native.POINT(_moveX, _moveY);
						if (_movePending)
						{
							num4 = Environment.TickCount64 - _postedAt;
						}
						_movePending = false;
					}
					if (num4 > 0)
					{
						LagSumMs += num4;
						LagCount++;
						if (num4 > MaxLagMs)
						{
							MaxLagMs = num4;
						}
					}
					if (obj.X == 0 && obj.Y == 0)
					{
						obj = new Native.POINT((int)((IntPtr)lpMsg2.wParam).ToInt64(), (int)((IntPtr)lpMsg2.lParam).ToInt64());
					}
					long tickCount2 = Environment.TickCount64;
					try
					{
						_onMove(obj);
					}
					catch (Exception ex2)
					{
						Log.Error(ex2);
					}
					long num3 = Environment.TickCount64 - tickCount2;
					if (num3 > MaxMoveMs)
					{
						MaxMoveMs = num3;
					}
				}
			}
		}
		catch (Exception ex3)
		{
			Log.Error(ex3);
		}
		finally
		{
			_ready.Set();
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		try
		{
			if (_threadId != 0)
			{
				Native.PostThreadMessageW(_threadId, 18u, IntPtr.Zero, IntPtr.Zero);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		try
		{
			if (_thread != null && _thread.IsAlive)
			{
				_thread.Join(1000);
			}
		}
		catch (Exception ex2)
		{
			Log.Error(ex2);
		}
		_thread = null;
		_threadId = 0u;
		Action result;
		while (_work.TryDequeue(out result))
		{
		}
	}
}
