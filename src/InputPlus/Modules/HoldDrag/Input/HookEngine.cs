#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
﻿// HoldDrag · Input/HookEngine.cs

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

internal sealed class HookEngine : IDisposable
{
	public const uint WM_REHOOK_MOUSE = 32776u;

	/// <summary>让钩子线程按需装/卸鼠标钩子（对应 altdrag 的 HookMouse / UnhookMouse）。</summary>
	public const uint WM_MOUSE_WANT = 32777u;

	private readonly IInputSink _sink;

	private readonly Native.HookProc _kbProc;

	private readonly Native.HookProc _msProc;

	private nint _kbHook = IntPtr.Zero;

	private nint _msHook = IntPtr.Zero;

	private Thread _hookThread;

	private uint _hookThreadId;

	private readonly ManualResetEventSlim _hookReady = new ManualResetEventSlim(initialState: false);

	private volatile bool _hookQuit;

	/// <summary>1 = 现在需要鼠标事件（按着触发键 / 正在拖）。由 SetMouseHookWanted 改。</summary>
	private volatile int _mouseWanted;

	private volatile int _seenX;

	private volatile int _seenY;

	private volatile bool _seenValid;

	private long _callbackSinceTicks;

	private long _seenCount;

	public static readonly nint MagicExtraInfo = new IntPtr(1215261796);

	public int LastError { get; private set; }

	public bool KeyboardInstalled => _kbHook != IntPtr.Zero;

	/// <summary>钩子线程本身还活着吗。句柄非零但线程已退出 = 钩子收不到任何事件。</summary>
	public bool HookThreadAlive => _hookThread is { IsAlive: true };

	public bool MouseInstalled => _msHook != IntPtr.Zero;

	/// <summary>鼠标钩子此刻是不是「该装着」（按着触发键 / 正在拖）。看门狗据此判断掉事件是不是正常。</summary>
	public bool MouseWanted => _mouseWanted != 0;

	public int ReinstallCount { get; private set; }

	public double CallbackStalledMs
	{
		get
		{
			long num = Interlocked.Read(ref _callbackSinceTicks);
			if (num == 0L)
			{
				return 0.0;
			}
			double num2 = (double)(Stopwatch.GetTimestamp() - num) * 1000.0 / (double)Stopwatch.Frequency;
			if (!(num2 < 0.0))
			{
				return num2;
			}
			return 0.0;
		}
	}

	public long SeenCount => Interlocked.Read(ref _seenCount);

	public bool TryGetLastSeen(out int x, out int y)
	{
		x = _seenX;
		y = _seenY;
		return _seenValid;
	}

	public HookEngine(IInputSink sink)
	{
		_sink = sink;
		_kbProc = KeyboardCallback;
		_msProc = MouseCallback;
	}

	public bool Install()
	{
		// 鼠标钩子按需装（altdrag hooks.c:1914-1934）：这里只认键盘钩子有没有装上。
		/* 句柄非零**不等于**「钩子还活着」：钩子线程因为 GetMessageW 返回 ≤0 退出时，
		 * 句柄还留在字段里。这时直接 return true 会让调用方以为装好了，
		 * 而回调永远不会再来 —— 「按住拖拽按了没反应」，且没有任何重装路径。
		 * 所以这里同时要求线程还活着，并把上一轮的僵尸句柄丢掉。 */
		if (_kbHook != IntPtr.Zero && HookThreadAlive)
		{
			return true;
		}
		if (_kbHook != IntPtr.Zero)
		{
			HookProbe.W("Install(): 上一轮钩子线程已退出，丢弃僵尸句柄 kbHook=0x" + _kbHook.ToString("X"));
			_kbHook = IntPtr.Zero;
			_msHook = IntPtr.Zero;
		}
		LastError = 0;
		_hookQuit = false;
		_hookReady.Reset();
		_hookThread = new Thread(HookThreadMain);
		_hookThread.IsBackground = true;
		_hookThread.Priority = ThreadPriority.AboveNormal;
		_hookThread.Name = "HoldDrag.HookThread";
		_hookThread.Start();
		if (!_hookReady.Wait(3000))
		{
			Log.Error("钩子线程启动超时");
		}
		if (KeyboardInstalled)
		{
			// 键盘钩子是常驻的（altdrag 同样常驻）；鼠标钩子等 sink 按需要。
			return true;
		}
		return false;
	}

	/// <summary>
	/// 按需装卸鼠标钩子。sink 在「按下触发键（含长按计时）」时 call(true)，
	/// 在松手 / 拖完 / Esc / 钩子重装后 call(false) —— 和 altdrag 的 HookMouse()/UnhookMouse() 同一套时序。
	///
	/// 为什么要按需而不是常驻：低级鼠标钩子排在整条钩子链里，常驻就等于每次鼠标移动、
	/// 每次点击都要先进我们的回调（还要先走一次坐标换算），任何一次卡顿都会让整机鼠标发涩；
	/// 而且和别的鼠标钩子工具抢链头，谁最后装谁先拿到，容易互相打架。
	/// </summary>
	public void SetMouseHookWanted(bool wanted)
	{
		Interlocked.Exchange(ref _mouseWanted, wanted ? 1 : 0);
		if (_hookQuit)
		{
			return;
		}
		uint hookThreadId = _hookThreadId;
		if (hookThreadId != 0 && Native.GetCurrentThreadId() == hookThreadId)
		{
			/* v1.6.4：**不在钩子回调里当场装**，一律走消息，回到消息循环再装。
			 *
			 * 实测（18:14–18:16 三轮）：在 WH_KEYBOARD_LL 回调里直接 SetWindowsHookEx(WH_MOUSE_LL)，
			 * 句柄非零、也不报错，但那条钩子**一个事件都收不到**（滚轮、左右键全无），
			 * 表现就是「按了触发键，点鼠标没反应」；同一份代码在 --drag-e2e 里却正常。
			 * 而在消息循环里装（PostThreadMessage → ApplyMouseWanted）就能收到事件。
			 *
			 * 之前之所以改成当场装，是因为 PostThreadMessage 实测要等 700~900ms 才被取出 ——
			 * 那个延迟的真正原因是键盘回调里 DeviceAllows 同步解析设备卡了 928ms（v1.6.4 已改为
			 * 线程池异步解析）。现在回调里只花几十微秒，消息循环马上就能轮到。 */
			HookProbe.W("SetMouseHookWanted(" + wanted + ") 在钩子线程上，改走消息（回调里不装钩子）");
		}
		if (hookThreadId != 0)
		{
			// v1.6.4：投递失败要留痕。这条消息就是「去装/去卸鼠标钩子」的唯一命令，
			// 丢了不会有人再发一次（看门狗 3 秒后才会补），日志里必须看得出来。
			if (!Native.PostThreadMessageW(hookThreadId, WM_MOUSE_WANT, new IntPtr(wanted ? 1 : 0), IntPtr.Zero))
			{
				HookProbe.W("SetMouseHookWanted(" + wanted + ") 投递 WM_MOUSE_WANT 失败，err=" + Marshal.GetLastWin32Error());
			}
			return;
		}
		// 钩子线程还没起来：Install() 会按 _mouseWanted 决定启动时要不要装。
	}

	private void HookThreadMain()
	{
		try
		{
			_hookThreadId = Native.GetCurrentThreadId();
			Native.PeekMessageW(out var _, IntPtr.Zero, 0u, 0u, 0u);
			nint moduleHandleW = Native.GetModuleHandleW(null);
			if (_kbHook == IntPtr.Zero)
			{
				_kbHook = Native.SetWindowsHookExW(13, _kbProc, moduleHandleW, 0u);
				if (_kbHook == IntPtr.Zero)
				{
					LastError = Marshal.GetLastWin32Error();
					Log.Error("键盘钩子安装失败，Win32 错误码 " + LastError);
				}
			}
			// 鼠标钩子按需装（altdrag hooks.c:956 也只在按下热键那一刻才 HookMouse）：
			// 启动时只在「已经有人要求」的情况下装，否则等 WM_MOUSE_WANT。
			if (_msHook == IntPtr.Zero && _mouseWanted != 0)
			{
				InstallMouseOnThisThread("启动");
			}
			_hookReady.Set();
			HookProbe.W("钩子线程就绪 tid=" + _hookThreadId + " kbHook=0x" + _kbHook.ToString("X") + " msHook=0x" + _msHook.ToString("X") + " lastError=" + LastError);
			Native.MSG lpMsg2;
			while (!_hookQuit)
			{
				int num = Native.GetMessageW(out lpMsg2, IntPtr.Zero, 0u, 0u);
				if (num <= 0)
				{
					// 句柄还在、线程却已经退出了 —— 这时 Install() 仍然报"装上了"，但回调永远不来。
					HookProbe.W("钩子线程退出：GetMessageW 返回 " + num + " 错误码 " + Marshal.GetLastWin32Error() + " kbHook=0x" + _kbHook.ToString("X") + " msHook=0x" + _msHook.ToString("X"));
					break;
				}
				if (lpMsg2.message == 32776)
				{
					ReinstallMouseOnThisThread();
				}
				else if (lpMsg2.message == WM_MOUSE_WANT)
				{
					ApplyMouseWanted();
				}
			}
			HookProbe.W("钩子线程结束（_hookQuit=" + _hookQuit + "）");
			/* 线程要退出了：把所有「看起来还装着」的状态清干净。
			 * 不清的话 Install() 和看门狗都会拿这个非零句柄当成「一切正常」。
			 * _hookThreadId 尤其要清 —— 线程 id 会被系统复用，留着它会让后续
			 * PostThreadMessage 投到一个毫不相干的线程上。 */
			_hookQuit = true;
			_hookThreadId = 0;
			_kbHook = IntPtr.Zero;
			_msHook = IntPtr.Zero;
			Log.Error("按住拖拽：钩子线程已退出（GetMessageW 返回 ≤0），钩子全部失效，等待看门狗重建");
		}
		catch (Exception ex)
		{
			Log.Error(ex);
			_hookReady.Set();
		}
	}

	/// <summary>在钩子线程上装鼠标钩子（调用方必须就是这条线程：低级钩子只能装在有消息循环、且会一直泵消息的线程上）。</summary>
	private bool InstallMouseOnThisThread(string why)
	{
		try
		{
			_msHook = Native.SetWindowsHookExW(14, _msProc, Native.GetModuleHandleW(null), 0u);
			if (_msHook == IntPtr.Zero)
			{
				LastError = Marshal.GetLastWin32Error();
				Log.Error("鼠标钩子安装失败（" + why + "），Win32 错误码 " + LastError);
				HookProbe.W("鼠标钩子安装失败（" + why + "）错误码 " + LastError);
				return false;
			}
			if (Native.GetCursorPos(out var lpPoint))
			{
				_seenX = lpPoint.X;
				_seenY = lpPoint.Y;
				_seenValid = true;
			}
			HookProbe.W("鼠标钩子已装（" + why + "）");
			return true;
		}
		catch (Exception ex)
		{
			Log.Error(ex);
			return false;
		}
	}

	/// <summary>钩子线程上响应 WM_MOUSE_WANT：该装就装、该卸就卸。</summary>
	private void ApplyMouseWanted()
	{
		if (_hookQuit)
		{
			return;
		}
		bool want = _mouseWanted != 0;
		HookProbe.W("ApplyMouseWanted want=" + want + " installed=" + (_msHook != IntPtr.Zero));
		if (want)
		{
			if (_msHook == IntPtr.Zero)
			{
				InstallMouseOnThisThread("按需");
			}
			return;
		}
		if (_msHook != IntPtr.Zero && Native.UnhookWindowsHookEx(_msHook))
		{
			_msHook = IntPtr.Zero;
			HookProbe.W("鼠标钩子已卸（松手/拖完）");
		}
	}

	private void ReinstallMouseOnThisThread()
	{
		if (_hookQuit)
		{
			return;
		}
		try
		{
			if (_msHook != IntPtr.Zero)
			{
				Native.UnhookWindowsHookEx(_msHook);
				_msHook = IntPtr.Zero;
			}
			nint moduleHandleW = Native.GetModuleHandleW(null);
			_msHook = Native.SetWindowsHookExW(14, _msProc, moduleHandleW, 0u);
			if (_msHook == IntPtr.Zero)
			{
				LastError = Marshal.GetLastWin32Error();
				Log.Error("抢占链头：鼠标钩子重装失败，Win32 错误码 " + LastError);
				return;
			}
			if (Native.GetCursorPos(out var lpPoint))
			{
				_seenX = lpPoint.X;
				_seenY = lpPoint.Y;
				_seenValid = true;
			}
			ReinstallCount++;
			Log.Info("鼠标钩子已就地重装并顶到钩子链头部（激活时抢占，第三方手势工具不再先拿到鼠标键）");
			// 这里刻意【不】触发 HooksReinstalled：这是激活瞬间「抢占链头」的计划内重装，
			// 一瞬间完成、不丢事件。若当成钩子出事，Coordinator 会在刚激活的同一刻把触发状态复位，
			// 用户看到的就是「按了触发键，外框闪一下就没了、鼠标完全拖不动」。
			// 只有 Reinstall()（看门狗救援，真的会丢事件）才广播。
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	public bool ReinstallMouseForChainPriority()
	{
		uint hookThreadId = _hookThreadId;
		if (hookThreadId != 0 && _msHook != IntPtr.Zero)
		{
			return Native.PostThreadMessageW(hookThreadId, WM_REHOOK_MOUSE, IntPtr.Zero, IntPtr.Zero);
		}
		if (hookThreadId != 0 && _mouseWanted != 0)
		{
			// 按需模式下钩子没装着，重装整条链没有意义，直接让它装就行。
			return Native.PostThreadMessageW(hookThreadId, WM_MOUSE_WANT, new IntPtr(1), IntPtr.Zero);
		}
		return Reinstall();
	}

	public bool Reinstall()
	{
		Log.Info("看门狗判定键鼠钩子已失效，正在重新安装");
		Uninstall();
		_seenValid = false;
		bool flag = Install();
		ReinstallCount++;
		Log.Info("钩子重装" + (flag ? "成功" : ("仍然失败，错误码 " + LastError)));
		if (flag)
		{
			RaiseReinstalled();
		}
		return flag;
	}

	/// <summary>
	/// 钩子重装完成后触发。重装期间的按键/鼠标事件全部丢失，
	/// 依赖"按下与抬起成对到达"的状态（触发键按住态、拖动会话）必须借此复位。
	/// </summary>
	internal event Action HooksReinstalled;

	private void RaiseReinstalled()
	{
		try
		{
			HooksReinstalled?.Invoke();
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	private void EnterCallback()
	{
		Interlocked.Exchange(ref _callbackSinceTicks, Stopwatch.GetTimestamp());
	}

	private void LeaveCallback()
	{
		Interlocked.Exchange(ref _callbackSinceTicks, 0L);
	}

	private void MarkSeen(Native.MSLLHOOKSTRUCT ms)
	{
		_seenX = ms.pt.X;
		_seenY = ms.pt.Y;
		_seenValid = true;
		Interlocked.Increment(ref _seenCount);
	}

	public void Uninstall()
	{
		// 先立"正在退出"的旗子，队列里已经排着的 WM_REHOOK_MOUSE 才会被丢掉。
		// 不立的话，那个消息会被即将结束的钩子线程处理，把鼠标钩子重新装在一条马上要消失的线程上：
		// 句柄非零、钩子实际已死，下一次 Install() 看到 _msHook != 0 就跳过安装，于是拖动彻底失灵。
		_hookQuit = true;
		if (_kbHook != IntPtr.Zero)
		{
			Native.UnhookWindowsHookEx(_kbHook);
			_kbHook = IntPtr.Zero;
		}
		if (_msHook != IntPtr.Zero)
		{
			Native.UnhookWindowsHookEx(_msHook);
			_msHook = IntPtr.Zero;
		}
		if (_hookThreadId != 0)
		{
			Native.PostThreadMessageW(_hookThreadId, 18u, IntPtr.Zero, IntPtr.Zero);
			_hookThreadId = 0u;
		}
	}

	public void Dispose()
	{
		Uninstall();
	}

	private nint KeyboardCallback(int nCode, nint wParam, nint lParam)
	{
		EnterCallback();
		try
		{
			if (nCode >= 0 && _sink != null)
			{
				Native.KBDLLHOOKSTRUCT kb = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
				HookProbe.W("KB-in nCode=" + nCode + " msg=" + ((IntPtr)wParam).ToInt64() + " vk=" + kb.vkCode + " scan=" + kb.scanCode + " flags=0x" + kb.flags.ToString("X") + " extra=0x" + kb.dwExtraInfo.ToInt64().ToString("X"));
				if (kb.dwExtraInfo != MagicExtraInfo)
				{
					int num = (int)((IntPtr)wParam).ToInt64();
					bool flag = num == 256 || num == 260;
					bool flag2 = num == 257 || num == 261;
					if ((flag | flag2) && _sink.OnKeyboard(flag, ref kb))
					{
						return new IntPtr(1);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		finally
		{
			LeaveCallback();
		}
		return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
	}

	private nint MouseCallback(int nCode, nint wParam, nint lParam)
	{
		EnterCallback();
		try
		{
			/* 无条件探针（连 nCode<0 也记）：用来区分"回调根本没进"和"进了但被过滤掉"。
			 * 排查"钩子装上了却收不到鼠标"时这是第一现场证据。 */
			if (((IntPtr)wParam).ToInt64() != 512)
			{
				HookProbe.W("MS-cb nCode=" + nCode + " msg=" + ((IntPtr)wParam).ToInt64() + " sink=" + (_sink != null));
			}
			if (nCode >= 0 && _sink != null)
			{
				Native.MSLLHOOKSTRUCT ms = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
				ms.pt = CoordSpace.ToProcess(ms.pt);
				MarkSeen(ms);
				int num = (int)((IntPtr)wParam).ToInt64();
				if (num != 512)
				{
					HookProbe.W("MS-in msg=" + num + " pt=" + ms.pt.X + "," + ms.pt.Y + " flags=0x" + ms.flags.ToString("X") + " extra=0x" + ms.dwExtraInfo.ToInt64().ToString("X"));
				}
				if (Log.Verbose && (num == 513 || num == 514 || num == 516 || num == 517 || num == 519 || num == 520))
				{
					Log.Trace("[探针] 钩子收到按键 msg=" + num + " flags=0x" + ms.flags.ToString("X") + " extra=" + ((IntPtr)ms.dwExtraInfo).ToInt64().ToString("X"));
				}
				if (ms.dwExtraInfo != MagicExtraInfo && _sink.OnMouse(num, ref ms))
				{
					return new IntPtr(1);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		finally
		{
			LeaveCallback();
		}
		return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
	}
}
