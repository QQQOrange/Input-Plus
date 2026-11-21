#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
// HoldDrag · Input/HookWatchdog.cs

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

internal sealed class HookWatchdog : IDisposable
{
	private readonly HookEngine _hooks;

	private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();

	private Native.POINT _prevCursor;

	private int _prevSeenX;

	private int _prevSeenY;

	private bool _hasPrev;

	private const int RequiredStrikes = 3;

	private const int CooldownMs = 5000;

	private const double CallbackStallAlertMs = 2000.0;

	private bool _stallAlerted;

	private int _strikes;

	private long _lastRescueAt;

	private long _prevSeenCount;

	/// <summary>
	/// 「要装鼠标钩子、但一直没装上」连续命中的次数。
	///
	/// 鼠标钩子是按需装的：AltDragSink 调 SetMouseHookWanted(true)，HookEngine 往钩子线程
	/// PostThreadMessage(WM_MOUSE_WANT) 让它在自己的线程上 SetWindowsHookEx —— 不能装在键盘
	/// 回调里（实测装出来的句柄非零却收不到任何事件）。
	///
	/// 这条路径没有返回值：消息可能没送到（钩子线程刚退出 / 消息队列还没建好 / 被安全软件
	/// 拦了），SetWindowsHookEx 也可能失败。失败之后**没有任何人再试一次**，于是按下触发键
	/// 完全没反应，直到下次重启 —— 这就是「按住拖拽时好时坏」最像的一类现场。
	/// 看门狗每秒跑一次，这里专门盯「想装但没装」这个状态。
	/// </summary>
	private int _mouseMissTicks;

	/// <summary>上一次补装鼠标钩子的时刻（TickCount64），防止每秒刷一次日志。</summary>
	private long _lastMouseRetryAt;

	public int Checks { get; private set; }

	public int Rescues { get; private set; }

	public HookWatchdog(HookEngine hooks)
	{
		_hooks = hooks;
		_timer.Interval = 1000;
		_timer.Tick += OnTick;
	}

	public void Start()
	{
		_hasPrev = false;
		_prevSeenX = 0;
		_prevSeenY = 0;
		Checks = 0;
		Rescues = 0;
		_timer.Start();
	}

	public void Stop()
	{
		try
		{
			_timer.Stop();
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		try
		{
			_timer.Stop();
			_timer.Dispose();
		}
		catch
		{
		}
	}

	private void OnTick(object sender, EventArgs e)
	{
		try
		{
			CheckOnce();
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	public bool CheckOnce()
	{
		Checks++;
		CheckCallbackStall();
		// 鼠标钩子现在是按需装的（不拖的时候本来就卸着）：这段时间收不到鼠标事件完全正常，
		// 绝不能当成「钩子掉了」去重装 —— 否则空闲时每 5 秒白重装一次。
		if (!_hooks.MouseInstalled)
		{
			_strikes = 0;
			_hasPrev = false;
			RepairMissingMouseHook();
			return false;
		}
		_mouseMissTicks = 0;
		if (!Native.GetCursorPos(out var lpPoint))
		{
			return false;
		}
		long seenCount = _hooks.SeenCount;
		if (seenCount != _prevSeenCount)
		{
			_prevSeenCount = seenCount;
			_prevCursor = lpPoint;
			_strikes = 0;
			return false;
		}
		bool flag = _hooks.TryGetLastSeen(out var x, out var y);
		if (!_hasPrev || !flag)
		{
			_prevCursor = lpPoint;
			_prevSeenX = x;
			_prevSeenY = y;
			_prevSeenCount = seenCount;
			_hasPrev = true;
			return false;
		}
		bool num = lpPoint.X != _prevCursor.X || lpPoint.Y != _prevCursor.Y;
		bool flag2 = x != _prevSeenX || y != _prevSeenY;
		_prevCursor = lpPoint;
		_prevSeenX = x;
		_prevSeenY = y;
		if (num && !flag2)
		{
			_strikes++;
		}
		else
		{
			_strikes = 0;
		}
		if (_strikes < 3)
		{
			return false;
		}
		long tickCount = Environment.TickCount64;
		if (tickCount - _lastRescueAt < 5000)
		{
			return false;
		}
		_strikes = 0;
		_lastRescueAt = tickCount;
		Rescues++;
		_hooks.Reinstall();
		_hasPrev = false;
		return true;
	}

	/// <summary>
	/// v1.6.4：补装丢失的鼠标钩子。
	///
	/// 只在「确实需要鼠标钩子」时才动（_hooks.MouseWanted）：闲时它本来就该是卸着的。
	/// 连续 3 秒都要装却没装上，才认为是那一次 PostThreadMessage/SetWindowsHookEx 丢了，
	/// 重新提一次要求；每 5 秒最多补一次，日志只在这一轮第一次失败时写一行。
	/// </summary>
	private void RepairMissingMouseHook()
	{
		if (!_hooks.MouseWanted)
		{
			_mouseMissTicks = 0;
			return;
		}
		if (Interlocked.Increment(ref _mouseMissTicks) < 3)
		{
			return;
		}
		long now = Environment.TickCount64;
		if (now - Interlocked.Read(ref _lastMouseRetryAt) < 5000)
		{
			return;
		}
		Interlocked.Exchange(ref _lastMouseRetryAt, now);
		if (_mouseMissTicks == 3)
		{
			Log.Info("按住拖拽：鼠标钩子应该在装却没装上（键盘钩子正常），正在补装一次。");
		}
		_hooks.SetMouseHookWanted(wanted: true);
	}

	private void CheckCallbackStall()
	{
		try
		{
			double callbackStalledMs = _hooks.CallbackStalledMs;
			if (callbackStalledMs >= 2000.0)
			{
				if (!_stallAlerted)
				{
					_stallAlerted = true;
					Log.Error("钩子回调已卡住 " + (int)callbackStalledMs + " ms —— 低级钩子回调正在阻塞，全系统键鼠事件都会排在它后面（整机输入无响应）。最常见的原因：钩子线程上出现了没有上限的同步跨进程窗口调用，请检查 DragEngine/WindowActions。");
				}
			}
			else if (_stallAlerted && callbackStalledMs == 0.0)
			{
				_stallAlerted = false;
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}
}
