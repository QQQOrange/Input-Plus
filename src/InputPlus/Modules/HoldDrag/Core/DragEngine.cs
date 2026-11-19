#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
﻿// HoldDrag · Core/DragEngine.cs

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

internal sealed class DragEngine : IDisposable
{
	private struct Session
	{
		public nint Hwnd;

		public bool Move;

		public int Button;

		public Native.POINT StartCursor;

		public Native.RECT StartRect;

		/// <summary>
		/// AltDrag 那套跨帧状态（offset / 被拖的边 / origin 尺寸 / 最小最大跟踪）。
		/// 是引用类型：Step() 拿到的是会话副本，但几何状态必须共享同一份。
		/// </summary>
		public AltState Alt;

		public int Gen;
	}

	private readonly Func<AppConfig> _cfg;

	private readonly ActivationFrame _frame;

	private readonly DragWorker _worker;

	private static readonly int OwnPid = Process.GetCurrentProcess().Id;

	private readonly object _gate = new object();

	private Session _session;

	private volatile bool _dragging;

	private volatile bool _moved;

	private int _generation;

	private Native.RECT _inset;

	private nint _hoverRaw;

	private nint _hoverHwnd;

	private Native.RECT _hoverRect;

	private bool _hoverRectValid;

	private readonly ShakeDetector _shake = new ShakeDetector();

	private int _shakeGen = -1;

	private volatile bool _frameWanted;

	/// <summary>尺寸 HUD（拖动时显示 W × H）。</summary>
	private readonly HintOverlay _hud = new HintOverlay();

	/// <summary>触发键按住一会儿后浮出来的快捷键提示（只在还没开始拖的时候出现）。</summary>
	private readonly HintOverlay _hint = new HintOverlay();

	private System.Threading.Timer _hintTimer;

	private nint _undoHwnd;

	private Native.RECT _undoRect;

	private long _undoAt;

	/// <summary>已向目标窗口发过 WM_ENTERSIZEMOVE / 事件 10，退出时必须成对补上（见 Cleanup / End）。</summary>
	private volatile bool _sizeMoveActive;

	/// <summary>本次拖动的抽帧倍数（AltDrag 的 MoveRate / ResizeRate；1 = 每个鼠标事件都写）。</summary>
	private int _moveRate = 1;

	private int _resizeRate = 1;

	/// <summary>自检用：塞一个假光标进去，省得为了验"跟着光标走"去挪用户的鼠标。</summary>
	internal static Func<Native.POINT?> CursorProbeForTest;

	/// <summary>抽帧计数（AltDrag 里叫 updaterate）。</summary>
	private int _moveTick;

	/// <summary>本次是移动还是缩放 —— 抽帧和收尾核对都要用，Begin 时定下。</summary>
	private bool _moveMode;

	/// <summary>当前吸附级别（AltDrag 的 sharedstate.snap）：0 关 / 1 只吸屏幕 / 2 窗口外侧 / 3 窗口内侧。</summary>
	private volatile int _snap;

	/// <summary>本次拖动的默认吸附级别（Shift 抬起后回到这里）。</summary>
	private int _snapBase;

	/// <summary>触发键本身就是 Ctrl 时不再做"Ctrl 把光标夹在显示器内"，否则按住触发键就动不了。</summary>
	private bool _ctrlIsTrigger;

	/// <summary>上一帧请求的矩形。收尾时拿它跟窗口的真实矩形比一次（见 ReconcileTail）。</summary>
	private Native.RECT _lastTarget;

	private bool _lastTargetValid;

	/// <summary>Shift 是不是已经按住（键盘自动重复会连发 keydown，照 hooks.c:960 只在第一次补一帧）。</summary>
	private bool _shiftHeld;

	/// <summary>Ctrl 是不是已经按住（hooks.c:984 用 state.ctrl 防止连发）。</summary>
	private bool _ctrlHeld;

	/// <summary>本次拖动的开始时刻（End 时算时长，写进诊断）。</summary>
	private long _dragStartedAt;

	/// <summary>收尾补写的次数与最大偏移（诊断用）。</summary>
	private int _tailFixes;

	private int _tailDriftMax;

	/// <summary>本次拖动里"写完回读"的最大偏差：目标程序咬着不给位时会很大（诊断用）。</summary>
	private int _applyDriftMax;

	/// <summary>本帧序号：写完回读（落点偏差）每 8 帧做一次，省掉 7/8 的跨线程往返。</summary>
	private int _frameNo;

	/// <summary>拖动期间是否已经把系统定时器精度提到 1ms（必须成对 timeEndPeriod）。</summary>
	private bool _timerPeriodRaised;

	/// <summary>轨迹账本里的"我们写了几次"还要算上 Aero 那几帧（SetWindowPlacement / SW_MAXIMIZE 不走 MoveWindowDrag）。</summary>
	private long _traceAeroWrites;
		/// <summary>拖动轨迹（Core/DragTrace.cs）用的累计计数与上一帧快照 —— 只写不读，出问题才有人看。</summary>
		private long _traceEvents;

		private long _traceTimerFrames;

		private Native.RECT _traceRequest;

		private bool _traceRequestValid;

		private int _traceOffsetX;

		private int _traceOffsetY;

		private bool _traceLocked;

	internal static int DiagFrameUpdates;

	internal static int DiagWindowSkipped;

	internal static int DiagWindowWritten;

	private bool _overlaysReady;

	private int _sessionCount;


	private bool _frameInside;

	public bool IsDragging => _dragging;

	public int Button => _session.Button;

	public bool LastMoved => _moved;

	public int SessionCount => _sessionCount;

	internal ActivationFrame Frame => _frame;

	internal bool FrameReallyVisible
	{
		get
		{
			try
			{
				return _frame.IsShown && Native.IsWindowVisible(_frame.Handle);
			}
			catch
			{
				return false;
			}
		}
	}

	internal static void DiagReset()
	{
		DiagFrameUpdates = 0;
		DiagWindowSkipped = 0;
		DiagWindowWritten = 0;
		// 这两组计数器以前只在进程启动时归零，诊断行里的"合并峰值""写耗时平均"
		// 其实是开机以来的累计值，不是这一次拖动的。每次起拖清零，账才对得上。
		DragWorker.ResetStats();
		WindowActions.ResetFrameStats();
	}

	public DragEngine(Func<AppConfig> config)
	{
		_cfg = config;
		_frame = new ActivationFrame();
		_worker = new DragWorker(OnWorkerMove);
		_worker.Start();
	}

	public void Dispose()
	{
		Cancel();
		_hud.Dispose();
		_hint.Dispose();
		_worker.Dispose();
		if (_overlaysReady)
		{
			_frame.Destroy();
			_overlaysReady = false;
		}
		_idleReleaseTimer?.Dispose();
	}

	public void Precreate()
	{
		try
		{
			_hud.Start();
			_hint.Start();
			_frame.EnsureCreated();
			_overlaysReady = true;
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	public void DeliverCursor(Native.POINT pt)
	{
		if (!_dragging)
		{
			return;
		}
		_lastEventPt = pt;
		Interlocked.Increment(ref _traceEvents);
		// 抽帧（照 AltDrag 的 MoveRate / ResizeRate）：不是省性能，是让"写窗口"这一步
		// 永远有空档。每来一个鼠标事件就排一帧进去，队列只会在目标程序慢的时候越积越长，
		// 表现出来就是"手停了窗口还在挪"。
		int rate = (_moveMode ? _moveRate : _resizeRate);
		if (rate > 1)
		{
			_moveTick = (_moveTick + 1) % rate;
			if (_moveTick != 0)
			{
				return;
			}
		}
		_worker.PostMove(pt);
	}

	/// <summary>
	/// 绕过 <see cref="DeliverCursor"/> 的抽帧，立刻排一帧进去。
	/// AltDrag 在两个地方是"事件里当场 MouseMove()"的：最大化窗口被还原之后（hooks.c:1671）、
	/// Shift 刚按下时（hooks.c:962）。这两处都不能等用户再动一下鼠标 ——
	/// 一个会让窗框停在原处，一个会让"按住 Shift 强制吸内侧"看起来像没生效。
	/// 带上代次检查：真排到队里时如果已经换了下一次拖动，这一帧就作废。
	/// </summary>
	private void PostFrame(Session s, Native.POINT pt, bool forceWrite)
	{
		int gen = s.Gen;
		_worker.Post(() =>
		{
			if (gen != _generation)
			{
				return;
			}
			Step(pt, s, forceWrite);
		});
	}

	/// <summary>
	/// "此刻的光标在哪"。自检里可以换成假的光标（<see cref="CursorProbeForTest"/>），
	/// 别的地方一律当真去问系统 —— 这一句就是"窗口为什么不会追着旧坐标跑"的全部来源。
	/// </summary>
	private static bool TryLiveCursor(out Native.POINT pt)
	{
		Func<Native.POINT?> func = CursorProbeForTest;
		if (func != null)
		{
			Native.POINT? fake = func();
			pt = (fake ?? default(Native.POINT));
			return fake.HasValue;
		}
		return Native.GetCursorPos(out pt);
	}

	public bool Begin(nint hwnd, bool move, int button, Native.POINT pt)
	{
		// 一旦真的开始拖，提示就该让位（它只是"还没动手时"的说明）
		CancelHintTimer();
		if (hwnd != IntPtr.Zero)
		{
			if (move && !AppRules.AllowsMove(hwnd))
			{
				Log.Info("[规则] 该程序只允许缩放，忽略这次移动");
				return false;
			}
			if (!move && !AppRules.AllowsResize(hwnd))
			{
				Log.Info("[规则] 该程序只允许移动，忽略这次缩放");
				return false;
			}
		}
		return BeginCore(hwnd, move, button, pt);
	}

	private bool BeginCore(nint hwnd, bool move, int button, Native.POINT pt)
	{
		AppConfig appConfig = _cfg();
		if (!Native.GetWindowRect(hwnd, out var lpRect))
		{
			Log.Info("[诊断] 拖动无法开始：拿不到目标窗口矩形 句柄=" + ((IntPtr)hwnd).ToInt64().ToString("X"));
			return false;
		}
		bool flag = Native.IsZoomed(hwnd);
		bool flag2 = !flag && WindowActions.IsFullscreen(hwnd, lpRect);
		// 不能缩放（没有 WS_THICKFRAME 的固定大小对话框）就干脆不接管缩放：
		// 用 SetWindowPos 硬改是能改的，但那会绕过窗口自己的最小/最大跟踪，把对话框压变形而且它自己修复不了。
		if (!move && !flag && !flag2 && !WindowActions.IsResizable(hwnd))
		{
			Log.Info("[诊断] 目标窗口不可缩放（无 WS_THICKFRAME）→ 放行不接管 句柄=" + ((IntPtr)hwnd).ToInt64().ToString("X"));
			return false;
		}
		// 最大化窗口一律交给 AltDrag 的 SetupMove / SetupResize 处理，这里不提前还原：
		//   移动 hooks.c:1661-1683 —— 按"光标在窗口里的比例"折算 offset，再 SW_RESTORE，并当场补一帧 MouseMove；
		//   缩放 hooks.c:1685-1711 —— 先把 rcNormalPosition 铺满整块显示器再 SW_RESTORE，然后按九宫格起手。
		// 提前还原会让 RecordOrigin 读到一个"已经是普通窗口"的状态：origin.maximized 丢了、origin 宽高也变成了
		// 还原后的尺寸，抓点和 Aero 还原全都会错位。
		// 剩下的只有一种 Input-Plus 特有情况：没有标题栏、正好铺满整块显示器的真·全屏窗口 ——
		// AltDrag 对这类窗口是直接放行的（hooks.c:1567-1574），这里保留"触发时退出全屏"把它降级成普通窗口。
		if (flag2)
		{
			if (!appConfig.ExitFullscreenOnTrigger)
			{
				Log.Info("[诊断] 目标是无标题栏全屏窗口，且『触发时退出全屏』已关闭 → 放行不接管 键=" + button + " 移动=" + move);
				return false;
			}
			List<Action> pending = new List<Action>();
			if (!WindowActions.ExitFullscreenToWindowed(hwnd, ref lpRect, pt, (Action w) =>
			{
				pending.Add(w);
			}))
			{
				Log.Info("[诊断] 目标是无标题栏全屏窗口，降级成普通窗口失败 → 放行不接管 句柄=" + ((IntPtr)hwnd).ToInt64().ToString("X"));
				return false;
			}
			if (pending.Count > 0)
			{
				int cornerPct = appConfig.CornerPercent;
				int btn = button;
				bool mv = move;
				Native.POINT cur = pt;
				nint target = hwnd;
				PostTargetWork(() =>
				{
					for (int i = 0; i < pending.Count; i++)
					{
						try
						{
							pending[i]();
						}
						catch (Exception ex2)
						{
							Log.Error(ex2);
						}
					}
					if (Native.GetWindowRect(target, out var lpRect2))
					{
						ReconcileStartRect(target, btn, mv, cur, cornerPct, lpRect2);
					}
				});
			}
		}
		// —— 起拖状态照 AltDrag（hooks.c:1583-1690）——
		// origin 必须在任何还原动作之前记下来（最大化窗口的"还原尺寸"就是从这里取的）。
		AltState alt = new AltState();
		Native.RECT startWnd = lpRect;
		if (Native.GetWindowRect(hwnd, out var liveWnd))
		{
			startWnd = liveWnd;
		}
		AltDragCore.RecordOrigin(alt, hwnd, startWnd);
		// 最小/最大尺寸问窗口自己要（WM_GETMINMAXINFO），不是拍脑袋用系统的 SM_CXMINTRACK。
		AltDragCore.QueryMinMax(hwnd, pt, out alt.MinTrack, out alt.MaxTrack);
		alt.Entry = WndDb.Touch(hwnd);
		// 这一步之后 SetupMove 会把"最大化"吃掉（还原成普通窗口），所以要先留个记号。
		bool wasMaximized = alt.OriginMaximized;
		if (move)
		{
			AltDragCore.SetupMove(alt, hwnd, pt, ref startWnd);
		}
		else
		{
			AltDragCore.SetupResize(alt, hwnd, pt, ref startWnd);
		}
		Native.RECT inset = ComputeInset(hwnd, startWnd);
		lock (_gate)
		{
			_session = default;
			_session.Hwnd = hwnd;
			_session.Move = move;
			_session.Button = button;
			_session.StartCursor = pt;
			_session.StartRect = startWnd;
			_session.Alt = alt;
			_moved = false;
			_generation++;
			_session.Gen = _generation;
			_inset = inset;
			_frameWanted = appConfig.ShowActivationFrame;
			_dragging = true;
		}
		WindowActions.ResetSessionKickCache();
		DiagReset();
		_moveMode = move;
		_moveRate = Math.Clamp(appConfig.MoveRate, 1, 8);
		_resizeRate = Math.Clamp(appConfig.ResizeRate, 1, 8);
		_moveTick = 0;
		_dragStartedAt = Environment.TickCount64;
		// hooks.c:1849-1854：抽帧时源码另起一个 100ms 的 MOVE_TIMER 兜底 ——
		// 钩子被系统掐掉/消息没送到时，只要光标动过就补一帧。
		StartMovePollTimer(move);
		// 我们把"排进队列 → 开始写窗口"这段延迟压下来的两个前提：定时器精度 1ms + 吸附目标缓存。
		// 默认 15.6ms 的 tick 会把这段延迟反复抹到 15/31ms，看上去就是窗口总比手慢半拍。
		if (!_timerPeriodRaised)
		{
			_timerPeriodRaised = Native.TimeBeginPeriod(1) == 0u;
		}
		// 吸附级别照 AltDrag 的 sharedstate.snap：0 = 关、1 = 只吸屏幕、2 = 再吸窗口外侧、3 = 再吸窗口内侧。
		// Input-Plus 只有一个"贴边吸附"开关，打开就按 3 走（这是 AltDrag 的完整行为）。
		_snapBase = (appConfig.SnapToEdges ? Math.Clamp(appConfig.SnapLevel, 1, 3) : 0);
		// 起拖时 Shift 已经按着，就按"吸窗口内侧"来（hooks.c:958 把 snap 直接立成 3，
		// 这个状态是跨拖动保留的，不会因为换了个窗口就退回默认档）。
		_shiftHeld = IsShiftDown();
		_snap = (_shiftHeld ? 3 : _snapBase);
		// Ctrl 的物理状态也要接上：起拖时已经按着 Ctrl，抬起事件才不会被漏掉（hooks.c:984 的 state.ctrl 同理）。
		_ctrlHeld = IsCtrlDown();
		_ctrlIsTrigger = KeysUtil.ToVirtualKey(appConfig.TriggerKeyId) == Native.VK_CONTROL;
		_lastTargetValid = false;
		_tailFixes = 0;
		_tailDriftMax = 0;
		_applyDriftMax = 0;
		_frameNo = 0;
		_traceAeroWrites = 0L;
		_traceEvents = 0L;
		_traceTimerFrames = 0L;
		_traceRequestValid = false;
		_traceRequest = default(Native.RECT);
		_traceLocked = false;
		_lastEventPt = pt;
		// 轨迹账本：只在数据目录里留最近一次拖动的逐帧快照（Core/DragTrace.cs）。
		string traceHeader = (move ? "移动" : "缩放") + " 起手=" + (wasMaximized ? "最大化" : "普通") + " 抽帧=" + (move ? _moveRate : _resizeRate) + " 吸附基线=" + _snapBase + " 起点=" + startWnd.Left + "," + startWnd.Top + " " + startWnd.Width + "x" + startWnd.Height + " 光标=" + pt.X + "," + pt.Y + " 类名=" + Native.GetClassName(hwnd) + " 进程=" + DescribeProcess(hwnd) + " DPI=" + Native.DpiOfWindow(hwnd) + DescribeMonitor(pt);
		DragTrace.Begin(DragTrace.PathFor(DragDiag.Path), hwnd, SampleForTrace, traceHeader);
		_frameInside = false;
		WindowActions.NotifySizeMove(hwnd, start: true);
		_sizeMoveActive = true;
		// hooks.c:1666-1671：最大化窗口按 pt-offset 还原之后，源码当场 MouseMove() 摆一帧。
		// 少了这一帧，窗口会停在"最大化矩形"的位置上，直到用户再动一下鼠标才跟上光标。
		if (wasMaximized && move && TryLiveCursor(out var firstPt))
		{
			PostFrame(_session, firstPt, forceWrite: true);
		}
		// hooks.c:1857-1863：按住 Alt 去点窗口，窗口会把这当成"要开菜单"。
		// 源码起拖时无条件补发一对 Ctrl（不判断热键），这里照做。
		// 触发键恰好是 Ctrl 时，源码在 hooks.c:949-953 把 ignorectrl 整个拖动期间常立 —— 等价于这里先立住。
		if (_ctrlIsTrigger)
		{
			alt.IgnoreCtrl = true;
		}
		AltDragCore.MasqueradeAsControl(alt);
		try
		{
			GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		if (appConfig.BringToFront)
		{
			nint target3 = hwnd;
			Thread thread = new Thread(() =>
			{
				try
				{
					WindowActions.ForceForeground(target3);
				}
				catch (Exception ex2)
				{
					Log.Error(ex2);
				}
			});
			thread.IsBackground = true;
			thread.Name = "HoldDrag.Raise";
			thread.Start();
		}
		if (appConfig.ShowActivationFrame && AppRules.AllowsFrame(hwnd))
		{
			_frame.HideOverlay();
			ShowDragFrame(startWnd, appConfig);
			Native.RECT rECT = FrameRectOf(startWnd, appConfig, out var inset2);
			Log.Info("[诊断] 拖动框已请求显示 开关=" + appConfig.ShowActivationFrame + " 就绪=" + _overlaysReady + " 目标矩形=" + startWnd.Left + "," + startWnd.Top + " " + startWnd.Width + "x" + startWnd.Height + " 可见缩进=" + _inset.Left + "/" + _inset.Top + "/" + _inset.Right + "/" + _inset.Bottom + " 外框矩形=" + rECT.Left + "," + rECT.Top + " " + rECT.Width + "x" + rECT.Height + (inset2 ? " 模式=内缩" : " 模式=外扩") + " 抬到最顶=" + _frame.RaiseAboveAll + " 显示=" + _frame.IsShown + " 请求=" + (_frame.ShowRequestedForTest ? "已投递" : "未投递"));
		}
		Log.Info((move ? "开始移动" : "开始缩放") + " 句柄=" + ((IntPtr)hwnd).ToInt64().ToString("X"));
		Interlocked.Increment(ref _sessionCount);
		return true;
	}

	/// <summary>
	/// 撤销上一次拖动：把窗口矩形还原回拖动开始前。
	/// 只在 5 秒内有效，且窗口还活着 —— 否则用户按 Ctrl+Z 会去改一个已经关掉的窗口。
	/// </summary>
	public bool TryUndoLast()
	{
		if (_undoAt == 0L || _undoHwnd == IntPtr.Zero)
		{
			return false;
		}
		if (Environment.TickCount64 - _undoAt > 5000)
		{
			_undoAt = 0L;
			return false;
		}
		if (!Native.IsWindow(_undoHwnd))
		{
			_undoAt = 0L;
			return false;
		}
		try
		{
			WindowActions.SetDragRect(_undoHwnd, _undoRect);
			Log.Info("[撤销] 已把窗口 " + ((IntPtr)_undoHwnd).ToInt64().ToString("X") + " 还原到 " + _undoRect.Left + "," + _undoRect.Top + " " + _undoRect.Width + "x" + _undoRect.Height);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
			return false;
		}
		_undoAt = 0L;
		return true;
	}

	internal bool HasUndoForTest => _undoAt != 0L && Environment.TickCount64 - _undoAt <= 5000;

	/// <summary>自检：直接显示一次 HUD，验证分层窗口从创建到渲染整条链路。</summary>
	internal void ProbeShowHudForTest(string text)
	{
		_hud.Start();
		Native.RECT rect = new Native.RECT(200, 200, 900, 700);
		_hud.ShowText(text, rect, 1f);
	}

	internal bool HudVisibleForTest
	{
		get
		{
			Thread.Sleep(80);
			return _hud.VisibleForTest;
		}
	}

	internal void ProbeHideHintsForTest()
	{
		_hud.Hide();
			}

	internal void ArmUndoForTest(nint hwnd, Native.RECT rect)
	{
		_undoHwnd = hwnd;
		_undoRect = rect;
		_undoAt = Environment.TickCount64;
	}

	/// <summary>光标所在显示器的工作区（不含任务栏）。</summary>
	private static Native.RECT WorkAreaOfPoint(Native.POINT pt)
	{
		try
		{
			Rectangle workingArea = Screen.FromPoint(new Point(pt.X, pt.Y)).WorkingArea;
			return new Native.RECT(workingArea.Left, workingArea.Top, workingArea.Right, workingArea.Bottom);
		}
		catch
		{
			return new Native.RECT(0, 0, 1920, 1080);
		}
	}

	/// <summary>
	/// Shift 的当前状态（hooks.c:958-963：按住 Shift 强制吸到窗口内侧，也就是 snap=3）。
	/// </summary>
	private static bool IsShiftDown()
	{
		try
		{
			return (Native.GetAsyncKeyState(0x10) & 0x8000) != 0;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Ctrl 的当前状态。hooks.c:984 用 state.ctrl 记"这一次按下已经处理过"，
	/// 起拖那一刻如果 Ctrl 已经按着，抬起事件也得能对上号。
	/// </summary>
	private static bool IsCtrlDown()
	{
		try
		{
			return (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
		}
		catch
		{
			return false;
		}
	}

	private static Native.RECT ComputeInset(nint hwnd, Native.RECT wr)
	{
		if (!Native.TryGetVisibleRect(hwnd, out var r))
		{
			return default;
		}
		if (r.Width <= 0 || r.Height <= 0)
		{
			return default;
		}
		int num = r.Left - wr.Left;
		int num2 = r.Top - wr.Top;
		int num3 = wr.Right - r.Right;
		int num4 = wr.Bottom - r.Bottom;
		if (num < 0 || num2 < 0 || num3 < 0 || num4 < 0)
		{
			return default;
		}
		if (num > 40 || num2 > 40 || num3 > 40 || num4 > 40)
		{
			return default;
		}
		return new Native.RECT(num, num2, num3, num4);
	}

	private void OnWorkerMove(Native.POINT pt)
	{
		Session session;
		lock (_gate)
		{
			if (!_dragging)
			{
				return;
			}
			session = _session;
		}
		Step(pt, session);
	}

	private void Step(Native.POINT pt, Session s, bool forceWrite = false)
	{
		try
		{
			if (s.Hwnd == IntPtr.Zero)
			{
				return;
			}
			if (!Native.IsWindow(s.Hwnd))
			{
				Cancel();
				return;
			}
			// 照 AltDrag：位置一律在"要写的那一刻"现取 GetCursorPos，事件里带的坐标只当兜底。
			// 钩子也好、工作线程也好，只要有一环排队，事件里的坐标就是"过去的坐标"：
			// 把它们写下去，就是手停了窗口还在跑，最后停在离光标老远的地方。
			if (TryLiveCursor(out var livePt))
			{
				pt = livePt;
			}
			AppConfig appConfig = _cfg();
			// hooks.c:651-657：按住 Ctrl 时把光标夹在"按下那一刻所在的显示器"里，
			// 这样拖动过程中不会一不小心把窗口拖到另一块屏上去。
			AltState altNow = s.Alt;
			if (!_ctrlIsTrigger && altNow != null && !altNow.IgnoreCtrl && altNow.OriginMonitor != IntPtr.Zero && (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0)
			{
				Native.MONITORINFO lpmi = new Native.MONITORINFO
				{
					cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO))
				};
				if (Native.GetMonitorInfoW(altNow.OriginMonitor, ref lpmi))
				{
					Native.RECT rcMonitor = lpmi.rcMonitor;
					pt.X = ((pt.X < rcMonitor.Left) ? rcMonitor.Left : ((pt.X >= rcMonitor.Right) ? (rcMonitor.Right - 1) : pt.X));
					pt.Y = ((pt.Y < rcMonitor.Top) ? rcMonitor.Top : ((pt.Y >= rcMonitor.Bottom) ? (rcMonitor.Bottom - 1) : pt.Y));
				}
			}
			int num = pt.X - s.StartCursor.X;
			int num2 = pt.Y - s.StartCursor.Y;
			if (Math.Abs(num) > 2 || Math.Abs(num2) > 2)
			{
				_moved = true;
			}
			// —— 以下几何逐条照 AltDrag 的 MouseMove（hooks.c:700-927）——
			// 和旧实现最大的差别：不再用"起点矩形 + 总位移"，而是每一帧重新读窗口矩形，
			// 再按 pt - offset 摆。窗口被目标程序钳制过（到达最小尺寸、分块重画）之后，
			// 下一帧就自己纠回来，误差不会一直背着走。
			AltState alt = s.Alt;
			if (alt == null)
			{
				return;
			}
			_traceLocked = alt.Locked;
			// hooks.c:663-706：AutoRemaximize 的优先级高于 locked —— 原本最大化的窗口被拖到另一块显示器上时，
			// 在新显示器里按 origin 尺寸居中、重新最大化，然后锁住本次拖动。
			if (s.Move && appConfig.AutoRemaximize && AltDragCore.AutoRemaximize(alt, s.Hwnd, pt))
			{
				// hooks.c:687-690：AutoRemaximize=2 时一秒后自己还原（RESTORE_TIMER）。
				if (appConfig.AutoRemaximizeRestore)
				{
					ScheduleRemaxRestore(s);
				}
				_moved = true;
				_lastTargetValid = false;
				return;
			}
			// hooks.c:708-710：锁定之后本次拖动不再动窗口，HUD / 外框也就停在最后一帧。
			if (alt.Locked)
			{
				_lastTargetValid = false;
				return;
			}
			if (!Native.GetWindowRect(s.Hwnd, out var wnd))
			{
				return;
			}
			// hooks.c:225-230：吸附阈值就是 ini 里的 SnapThreshold，不按 DPI 缩放。
			int threshold = appConfig.SnapThreshold;
			Native.RECT rECT;
			bool alreadyWritten = false;
			if (s.Move)
			{
				int posx = pt.X - alt.OffsetX;
				int posy = pt.Y - alt.OffsetY;
				int wndwidth = wnd.Width;
				int wndheight = wnd.Height;
				AltDragCore.AeroOutcome aeroOutcome = AltDragCore.MoveAero(alt, appConfig, s.Hwnd, pt, ref posx, ref posy, ref wndwidth, ref wndheight);
				if (aeroOutcome == AltDragCore.AeroOutcome.Maximized)
				{
					// 上边 = 最大化：AltDrag 在这里直接 return，这一帧不写窗口
					rECT = (Native.GetWindowRect(s.Hwnd, out var maxed) ? maxed : wnd);
					// 这一帧是真的把窗口写成最大化了（MoveAero 里 SetWindowPlacement / SW_MAXIMIZE），
					// 计入轨迹的"写"，否则下一行会误标成"矩形动了但我们没写"。
					Interlocked.Increment(ref _traceAeroWrites);
					alreadyWritten = true;
				}
				else if (aeroOutcome == AltDragCore.AeroOutcome.Written)
				{
					// Aero 区：AltDrag 是立刻 MoveWindow 再回读真实尺寸，而且这一帧不走吸附
					Native.RECT aeroRect = new Native.RECT(posx, posy, posx + wndwidth, posy + wndheight);
					WindowActions.MoveWindowDrag(s.Hwnd, aeroRect);
					AltDragCore.NoteAeroWritten(alt, s.Hwnd);
					rECT = (Native.GetWindowRect(s.Hwnd, out var back) ? back : aeroRect);
					alreadyWritten = true;
				}
				else
				{
					if (_snap > 0 && threshold > 0)
					{
						SnapEngine.Move(_snap, threshold, s.Hwnd, ref posx, ref posy, wndwidth, wndheight);
					}
					rECT = new Native.RECT(posx, posy, posx + wndwidth, posy + wndheight);
				}
			}
			else
			{
				int posx = wnd.Left;
				int posy = wnd.Top;
				int wndwidth = wnd.Width;
				int wndheight = wnd.Height;
				AltDragCore.ResizeGeometry(alt, pt, wnd, ref posx, ref posy, ref wndwidth, ref wndheight);
				if (_snap > 0 && threshold > 0)
				{
					SnapEngine.Resize(_snap, threshold, s.Hwnd, alt.EdgeX, alt.EdgeY, ref posx, ref posy, ref wndwidth, ref wndheight);
				}
				rECT = new Native.RECT(posx, posy, posx + wndwidth, posy + wndheight);
			}
			// 记下"这一帧请求的是哪儿"：收尾时用它跟窗口的真实矩形对一次账（ReconcileTail）。
			_lastTarget = rECT;
			_lastTargetValid = true;
			_traceRequest = rECT;
			_traceRequestValid = true;
			_traceOffsetX = alt.OffsetX;
			_traceOffsetY = alt.OffsetY;
			// 先把窗口挪过去，再画 HUD 和外框 —— 顺序照 AltDrag：一个鼠标移动事件进来，
			// 第一件事就是让窗口跟上光标。装饰晚一帧看不出来，可它们若排在前面，
			// 那份开销就直接变成用户手上的延迟。
			if (alreadyWritten)
			{
				// Aero 帧（贴边 / 最大化）在这一帧里已经自己写过窗口了
				DiagWindowWritten++;
			}
			else
			{
				// hooks.c:927 —— AltDrag 每个鼠标移动事件都无条件 MoveWindow，
				// 没有"和上次一样就跳过"，也没有响应性探测。窗口的位置永远只由
				// 这一帧的几何决定；贴边那一帧尤其不能跳过，否则会停在上一帧的尺寸上。
				WindowActions.MoveWindowDrag(s.Hwnd, rECT);
				DiagWindowWritten++;
			}
			// 落点偏差：写完立刻回读一次。差得越多，说明目标程序越跟不上我们的帧
			//（钳制尺寸、卡在重排里）——用户看到的"窗口比手慢"就是它，和链路积压无关。
			// 落点偏差：写完回读一次。原来每帧都读 —— 每帧多一次跨线程往返进目标线程，
			// 目标程序越重这笔开销越明显。改成每 8 帧读一次：偏差是"跟不上"的累积症状，
			// 不会只落在没采样的那几帧里，最大值照样抓得到。
			_frameNo++;
			if (_dragging && !alreadyWritten && (_frameNo & 7) == 0 && Native.GetWindowRect(s.Hwnd, out var appliedRect))
			{
				int appliedDrift = Math.Max(Math.Max(Math.Abs(appliedRect.Left - rECT.Left), Math.Abs(appliedRect.Top - rECT.Top)), Math.Max(Math.Abs(appliedRect.Width - rECT.Width), Math.Abs(appliedRect.Height - rECT.Height)));
				if (appliedDrift > _applyDriftMax)
				{
					_applyDriftMax = appliedDrift;
				}
			}

			if (appConfig.ShowDragHud)
			{
				string text = rECT.Width + " × " + rECT.Height;
				if (appConfig.DragHudShowPosition)
				{
					text = text + "  ·  " + rECT.Left + "," + rECT.Top;
				}
				_hud.ShowText(text, FrameRectOf(rECT, appConfig, out var _), (float)Native.DpiOfWindow(s.Hwnd) / 96f);
			}
			if (appConfig.ShowActivationFrame && AppRules.AllowsFrame(s.Hwnd))
			{
				Native.RECT rectNow = FrameRectOf(rECT, appConfig, out var inset);
				_frame.RaiseAboveAll = true;
				if (rectNow.Width >= 4 && rectNow.Height >= 4)
				{
					lock (_gate)
					{
						if (_dragging && _session.Gen == s.Gen && _frameWanted)
						{
							_frame.ApplyTheme(appConfig.FrameColorId, appConfig.FrameStyleId, appConfig.FrameThickness, appConfig.FrameRadius, appConfig.FrameOpacity, appConfig.EffectiveFrameColor);
							// 拖动中每帧都用合并信箱投递（同一时刻最多一条在途），不再每帧 AsyncWindowPos：
							// 覆盖层线程忙的时候 SetRectNow 会把十几条 SetWindowPos 堆在它的消息队列里，
							// 松手之后才追上，屏幕上就是虚线框还在滑一段。SetRect 只留最新一帧。
							_frame.SetRect(rectNow);
							DiagFrameUpdates++;
						}
					}
				}
			}
			if (Log.Verbose)
			{
				Native.GetWindowRect(s.Hwnd, out var lpRect2);
				string[] array = new string[9]
				{
					s.Move ? Loc.T("移动") : Loc.T("缩放"),
					Loc.T(" 光标="),
					pt.X.ToString(),
					",",
					pt.Y.ToString(),
					Loc.T(" 期望="),
					null,
					null,
					null
				};
				Native.RECT rECT2 = rECT;
				array[6] = rECT2.ToString();
				array[7] = Loc.T(" 实际=");
				rECT2 = lpRect2;
				array[8] = rECT2.ToString();
				Log.Trace(string.Concat(array));
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	public void End()
	{
		if (!_dragging)
		{
			return;
		}
		try
		{
			AppConfig appConfig = _cfg();
			Session s;
			nint hwnd;
			lock (_gate)
			{
				s = _session;
				hwnd = _session.Hwnd;
			}
			bool cursorPos = Native.GetCursorPos(out var pt);
			lock (_gate)
			{
				_dragging = false;
				_generation++;
			}
			if (cursorPos && hwnd != IntPtr.Zero)
			{
				// 最后一帧几何写入与"退出尺寸移动"通知必须在同一处、且顺序固定：
				// 很多程序是在 WM_EXITSIZEMOVE 里保存窗口位置的，通知先到就会把"松手前"的矩形存下来。
				_sizeMoveActive = false;
				FinishTail(hwnd, s, pt, s.Move, _dragStartedAt);
				_undoHwnd = hwnd;
				_undoRect = s.StartRect;
				_undoAt = Environment.TickCount64;
			}
			else if (hwnd != IntPtr.Zero)
			{
				_sizeMoveActive = false;
				WindowActions.NotifySizeMove(hwnd, start: false);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		finally
		{
			Cleanup();
		}
	}

	/// <summary>
	/// 收尾对账：最后那一帧写下去之后，把窗口的真实矩形读回来，跟"我们要求的位置"比一次。
	/// 对不上就再写一次（这一条路不带任何跳过判断），并把残余偏差记进诊断。
	///
	/// 为什么要这一步：整条链路（钩子 → 工作线程 → 目标程序）里任何一环掉帧，
	/// 都会让窗口停在"比光标慢半拍"的地方。用户看到的就是"我手停了，它还在跑，最后位置不对"。
	/// AltDrag 靠"钩子里同步写"避免这件事，我们隔着一条工作线程，只能在收尾时把账对平。
	/// </summary>
	/// <returns>补写之后仍然不一致的最大像素数（0 = 已经严丝合缝）。</returns>
	private int ReconcileTail(nint hwnd, Native.RECT want, bool wantValid)
	{
		try
		{
			if (!wantValid || hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
			{
				return 0;
			}
			if (!Native.GetWindowRect(hwnd, out var actual))
			{
				return 0;
			}
			if (!TailMismatch(want, actual, out var dx, out var dy, out var dw, out var dh))
			{
				return 0;
			}
			WindowActions.SetDragRect(hwnd, want);
			_tailFixes++;
			int num = MaxAbs(dx, dy, dw, dh);
			if (num > _tailDriftMax)
			{
				_tailDriftMax = num;
			}
			if (!Native.GetWindowRect(hwnd, out var after))
			{
				return 0;
			}
			TailMismatch(want, after, out var dx2, out var dy2, out var dw2, out var dh2);
			return MaxAbs(dx2, dy2, dw2, dh2);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
			return 0;
		}
	}

	/// <summary>
	/// 收尾帧：就地把窗口摆到"松手这一瞬间"的光标位置，再跟真实矩形对一次账。
	///
	/// 这件活以前是丢给工作线程做的，可 End() 的 finally 里 Cleanup() 会立刻把 _session.Hwnd
	/// 清成 0 —— 那条工作项醒来时 Step() 读到的是 0，IsWindow(0) 把它半路饿死：最后一帧根本
	/// 没写下去，_lastTarget 还停在上一次成功的那帧上，紧接着的 ReconcileTail 拿着这份旧矩形去
	/// "纠正"窗口，于是松手的一瞬间窗口会被拽回几帧之前的位置。
	/// AltDrag 在钩子里同步写窗口，压根没有这条时间差，这里照它的样子在钩子线程上写完再发
	/// WM_EXITSIZEMOVE。一次拖动只多这几毫秒，换的是"松手即落位"。
	/// </summary>
	/// <summary>
	/// 给 <see cref="DragTrace"/> 的一帧快照。在别的线程上跑，只读字段、只做 GetCursorPos / GetWindowRect，
	/// 不碰任何锁 —— 拖动本身一刻都不能等它。
	/// </summary>
	/// <summary>被拖窗口的进程名（轨迹表头用，读不到就给 pid）。</summary>
	private static string DescribeProcess(nint hwnd)
	{
		try
		{
			Native.GetWindowThreadProcessId(hwnd, out var processId);
			if (processId == 0)
			{
				return "?";
			}
			return System.Diagnostics.Process.GetProcessById((int)processId).ProcessName + ":" + processId;
		}
		catch (Exception ex)
		{
			Log.Error(ex);
			return "?";
		}
	}

	/// <summary>光标所在显示器的屏幕矩形与工作区矩形（轨迹表头用）。</summary>
	private static string DescribeMonitor(Native.POINT pt)
	{
		try
		{
			Native.MONITORINFO monitorInfo = Native.GetMonitorInfoAt(pt);
			return " 屏=" + monitorInfo.rcMonitor.Left + "," + monitorInfo.rcMonitor.Top + " " + monitorInfo.rcMonitor.Width + "x" + monitorInfo.rcMonitor.Height + " 工作区=" + monitorInfo.rcWork.Left + "," + monitorInfo.rcWork.Top + " " + monitorInfo.rcWork.Width + "x" + monitorInfo.rcWork.Height;
		}
		catch (Exception ex)
		{
			Log.Error(ex);
			return "";
		}
	}

	private DragTrace.Sample SampleForTrace(nint hwnd)
	{
		DragTrace.Sample sample = default(DragTrace.Sample);
		try
		{
			if (!Native.IsWindow(hwnd) || !Native.GetCursorPos(out var cursor) || !Native.GetWindowRect(hwnd, out var rect))
			{
				return sample;
			}
			sample.Ok = true;
			sample.Cursor = cursor;
			sample.Rect = rect;
			// DWM 扩展边框：屏幕上真正画出来的那圈。它和 GetWindowRect 分家时，说明"画面在动、矩形没动"。
			Native.RECT frame;
			if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf(typeof(Native.RECT))) == 0 && frame.Width > 0 && frame.Height > 0)
			{
				sample.Frame = frame;
				sample.FrameOk = true;
			}
			sample.Event = _lastEventPt;
			sample.Request = _traceRequest;
			sample.RequestOk = _traceRequestValid;
			sample.OffsetX = _traceOffsetX;
			sample.OffsetY = _traceOffsetY;
			sample.Writes = WindowActions.FramesWritten + Interlocked.Read(ref _traceAeroWrites);
			sample.Events = Interlocked.Read(ref _traceEvents);
			sample.TimerFrames = Interlocked.Read(ref _traceTimerFrames);
			sample.Snap = _snap;
			sample.Move = _moveMode;
			sample.Locked = _traceLocked;
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		return sample;
	}

	private void FinishTail(nint hwnd, Session session, Native.POINT pt, bool moveMode, long startedAt)
	{
		int residual = 0;
		try
		{
			Step(pt, session, forceWrite: true);
			residual = ReconcileTail(hwnd, _lastTarget, _lastTargetValid);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		WindowActions.NotifySizeMove(hwnd, start: false);
		NoteDragDiag(moveMode, startedAt, residual);
		_hud.Hide();
		TraceTail(hwnd, moveMode);
	}

	/// <summary>
	/// 松手后的 400ms 里盯着目标窗口的矩形：只要它还在变，就说明"手停了窗口还在跑"是真的，
	/// 而且能把每一次变化的时间、落点、当时的光标一起记下来 —— 光标不动而矩形在动，责任在我们；
	/// 两者一起动，那是输入还在来。没有变化就一行都不写，账本保持干净。
	/// </summary>
	private void TraceTail(nint hwnd, bool moveMode)
	{
		try
		{
			int gen;
			lock (_gate)
			{
				gen = _generation;
			}
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					if (!Native.GetWindowRect(hwnd, out var prev))
					{
						return;
					}
					Stopwatch sw = Stopwatch.StartNew();
					List<string> list = new List<string>();
					long framesAtStart = WindowActions.FramesWritten;
					while (sw.ElapsedMilliseconds < 400L)
					{
						Thread.Sleep(8);
						lock (_gate)
						{
							if (gen != _generation)
							{
								return;
							}
						}
						if (!Native.GetWindowRect(hwnd, out var now))
						{
							return;
						}
						if (now.Left != prev.Left || now.Top != prev.Top || now.Width != prev.Width || now.Height != prev.Height)
						{
							Native.GetCursorPos(out var cur);
							list.Add("    +" + sw.ElapsedMilliseconds + "ms → " + now.Left + "," + now.Top + " " + now.Width + "x" + now.Height + " 光标=" + cur.X + "," + cur.Y);
							prev = now;
						}
					}
					// 责任划分：这 400ms 里我们一帧都没写，矩形却变了，那就是目标程序自己在动；
					// 我们还在写，才是拖拽核的问题。光看"窗口动了"分不出来，得看谁在写。
					long writtenDuring = WindowActions.FramesWritten - framesAtStart;
					if (list.Count > 0)
					{
						DragDiag.Note((moveMode ? "移动" : "缩放") + " 尾迹：松手后又动了 " + list.Count + " 次（400ms 采样，同期我们写了 " + writtenDuring + " 帧）" + Environment.NewLine + string.Join(Environment.NewLine, list));
					}
				}
				catch (Exception ex2)
				{
					Log.Error(ex2);
				}
			});
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	/// <summary>请求的矩形和窗口真实矩形差在哪（四个分量各自带符号）。</summary>
	internal static bool TailMismatch(Native.RECT want, Native.RECT actual, out int dx, out int dy, out int dw, out int dh)
	{
		dx = actual.Left - want.Left;
		dy = actual.Top - want.Top;
		dw = (actual.Right - actual.Left) - (want.Right - want.Left);
		dh = (actual.Bottom - actual.Top) - (want.Bottom - want.Top);
		return dx != 0 || dy != 0 || dw != 0 || dh != 0;
	}

	private static int MaxAbs(int a, int b, int c, int d)
	{
		return Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), Math.Max(Math.Abs(c), Math.Abs(d)));
	}

	/// <summary>
	/// 每次拖动结束往"数据目录\drag-diag.txt"写一行明文账 —— 这是排查"手停了窗口还在跑"
	/// 唯一靠得住的东西：合并峰值说明链路积压了多少、写耗时说明目标程序多慢、
	/// 收尾补写说明最后一帧到底有没有落到光标上。
	/// </summary>
	private void NoteDragDiag(bool moveMode, long startedAt, int residual)
	{
		try
		{
			long num = Environment.TickCount64 - startedAt;
			int num2 = DiagWindowWritten + DiagWindowSkipped;
			double num3 = ((WindowActions.FramesWritten > 0) ? (WindowActions.TotalWriteMs / (double)WindowActions.FramesWritten) : 0.0);
			string text = (moveMode ? "移动" : "缩放")
				+ " 时长=" + num + "ms"
				+ " 帧: 请求=" + num2 + " 写=" + DiagWindowWritten + " 跳过=" + DiagWindowSkipped + " 漂移修正=" + WindowActions.FramesDrifted
				+ " | 写耗时 平均=" + num3.ToString("0.0") + "ms 最大=" + WindowActions.MaxWriteMs.ToString("0.0") + "ms"
				+ " | 滞后 最大=" + DragWorker.MaxLagMs + "ms 平均=" + ((DragWorker.LagCount > 0) ? (DragWorker.LagSumMs / (double)DragWorker.LagCount).ToString("0.0") : "0.0") + "ms"
				+ " | 合并峰值=" + DragWorker.MaxDrained
				+ " | 收尾补写=" + _tailFixes + " 残余偏差=" + residual + "px 最大=" + _tailDriftMax + "px"
				+ " | 落点 最大偏差=" + _applyDriftMax + "px"
				+ " | 钩子 最大=" + Coordinator.HookMaxMsForTest.ToString("0.0") + "ms 慢=" + Coordinator.SlowHookCountForTest + "次";
			DragDiag.Note(text);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	public void UpdateFinal(Native.POINT pt)
	{
		if (_dragging)
		{
			Session session;
			lock (_gate)
			{
				session = _session;
			}
			Step(pt, session, forceWrite: true);
		}
	}

	internal void PostTargetWork(Action work)
	{
		if (work == null)
		{
			return;
		}
		if (_worker != null && _worker.IsRunning)
		{
			_worker.Post(work);
			return;
		}
		try
		{
			work();
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	private void ReconcileStartRect(nint hwnd, int button, bool move, Native.POINT pt, int cornerPercent, Native.RECT real)
	{
		lock (_gate)
		{
			if (!_dragging || _moved || _session.Hwnd != hwnd || _session.Button != button)
			{
				return;
			}
			Native.RECT startRect = _session.StartRect;
			if (startRect.Left == real.Left && startRect.Top == real.Top && startRect.Right == real.Right && startRect.Bottom == real.Bottom)
			{
				return;
			}
			_session.StartRect = real;
			_inset = ComputeInset(hwnd, real);
			// 降级落账之后窗口是真的换了位置/尺寸，AltDrag 那套 offset 得按新矩形重算一次，
			// 否则下一帧会按旧基准摆，窗口直接跳一下。
			AltDragCore.Rebase(_session.Alt, move, pt, real);
			Log.Info("[诊断] 降级落账后校正会话矩形：" + startRect.Left + "," + startRect.Top + " " + startRect.Width + "x" + startRect.Height + " → " + real.Left + "," + real.Top + " " + real.Width + "x" + real.Height);
		}
	}

	internal void HideOverlaysNow()
	{
		_frameWanted = false;
		_dragging = false;
		try
		{
			_frame.HideNowSync();
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	internal void EnforceFrameInvariant(bool triggerActive)
	{
		try
		{
			if (!_dragging && (!triggerActive || _hoverHwnd == IntPtr.Zero || !_hoverRectValid) && _frame.IsShown)
			{
				Log.Info("[兜底] 外框本不该显示（不在拖动、也没有悬停目标）→ 强制收掉");
				_frameWanted = false;
				_frame.HideNowSync();
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	internal void StepForTest(Native.POINT pt)
	{
		if (_dragging)
		{
			Session session;
			lock (_gate)
			{
				session = _session;
			}
			Step(pt, session);
		}
	}

	public void Cancel()
	{
		if (_dragging)
		{
			lock (_gate)
			{
				_dragging = false;
				_generation++;
			}
			Cleanup();
		}
	}

	private System.Threading.Timer _movePollTimer;

	private System.Threading.Timer _remaxTimer;

	private Native.POINT _lastEventPt;

	private int _pollGeneration;

	private int _remaxGeneration;

	/// <summary>
	/// hooks.c:1849-1854 的 MOVE_TIMER：MoveRate / ResizeRate 大于 1 时，源码另开一个 100ms 定时器。
	/// 它不是节流，是补帧 —— 低层鼠标钩子被系统静默摘掉（LowLevelHooksTimeout）时，窗口不会停在半路。
	/// </summary>
	private void StartMovePollTimer(bool move)
	{
		try
		{
			if ((move ? _moveRate : _resizeRate) <= 1)
			{
				StopMovePollTimer();
				return;
			}
			lock (_gate)
			{
				_pollGeneration = _generation;
			}
			System.Threading.Timer timer = _movePollTimer;
			if (timer == null)
			{
				_movePollTimer = new System.Threading.Timer(OnMovePollTick, null, 100, 100);
				return;
			}
			timer.Change(100, 100);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	private void StopMovePollTimer()
	{
		try
		{
			_movePollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	/// <summary>hooks.c:1999-2008：光标位置和最后一次鼠标事件里的坐标不同就当场补一帧 MouseMove()。</summary>
	private void OnMovePollTick(object state)
	{
		try
		{
			if (!_dragging)
			{
				StopMovePollTimer();
				return;
			}
			Session session;
			int num;
			lock (_gate)
			{
				session = _session;
				num = _pollGeneration;
			}
			if (session.Gen != num)
			{
				StopMovePollTimer();
				return;
			}
			if (!TryLiveCursor(out var pt))
			{
				return;
			}
			if (pt.X == _lastEventPt.X && pt.Y == _lastEventPt.Y)
			{
				return;
			}
			_lastEventPt = pt;
			Interlocked.Increment(ref _traceTimerFrames);
			PostFrame(session, pt, forceWrite: false);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	/// <summary>
	/// hooks.c:687-690 的 RESTORE_TIMER：AutoRemaximize=2 时，重新最大化一秒后把窗口还原成普通窗口，
	/// 按光标在新显示器里的相对位置重算 offset，再补一帧。
	/// </summary>
	private void ScheduleRemaxRestore(Session session)
	{
		try
		{
			lock (_gate)
			{
				_remaxGeneration = session.Gen;
			}
			System.Threading.Timer timer = _remaxTimer;
			if (timer == null)
			{
				_remaxTimer = new System.Threading.Timer(OnRemaxRestoreTick, null, 1000, Timeout.Infinite);
				return;
			}
			timer.Change(1000, Timeout.Infinite);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	private void StopRemaxRestoreTimer()
	{
		try
		{
			_remaxTimer?.Change(Timeout.Infinite, Timeout.Infinite);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	private void OnRemaxRestoreTick(object state)
	{
		try
		{
			if (!_dragging)
			{
				return;
			}
			Session session;
			int num;
			lock (_gate)
			{
				session = _session;
				num = _remaxGeneration;
			}
			if (session.Gen != num || session.Alt == null || !session.Move)
			{
				return;
			}
			if (!TryLiveCursor(out var pt))
			{
				return;
			}
			AltDragCore.RestoreAfterRemaximize(session.Alt, session.Hwnd, pt);
			PostFrame(session, pt, forceWrite: true);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}


	private void Cleanup()
	{
		try
		{
			_frameWanted = false;
			StopMovePollTimer();
			StopRemaxRestoreTimer();
			if (_timerPeriodRaised)
			{
				_timerPeriodRaised = false;
				Native.TimeEndPeriod(1);
			}
			DragTrace.End();
			// Cancel 路径（暂停、改配置、目标窗口没了）不会走 End 的通知，
			// 这里补发一次，否则目标窗口会一直以为自己还在尺寸拖动循环里（部分程序会卡在"拖拽模式"）。
			if (_sizeMoveActive)
			{
				_sizeMoveActive = false;
				nint hwnd;
				lock (_gate)
				{
					hwnd = _session.Hwnd;
				}
				WindowActions.NotifySizeMove(hwnd, start: false);
			}
			if (_overlaysReady)
			{
				_frame.HideNowSync();
				_hud.Hide();
			}
			try
			{
				GCSettings.LatencyMode = GCLatencyMode.Interactive;
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
			// v1.3 空闲内存回收：拖动结束后外框位图就是一块纯闲置的大缓冲
			//（4K 屏上每个外框 ~33MB 托管 + ~33MB 非托管）。旧版一直攥着不放，
			// "用完一次拖窗，常驻内存就高几十 MB"。这里延迟 15 秒释放：
			// 期间若又开始拖动（位图会被 Rebuild 复用）就取消，避免抖动。
			ScheduleIdleBufferRelease();
		}
		catch (Exception ex2)
		{
			Log.Error(ex2);
		}
		finally
		{
			InvalidateHover();
			lock (_gate)
			{
				_session.Hwnd = IntPtr.Zero;
				_session.Button = -1;
			}
		}
	}

	private System.Threading.Timer _idleReleaseTimer;

	private int _idleReleaseGeneration;

	/// <summary>
	/// 15 秒后释放外框像素缓冲。
	///
	/// 必须用 System.Threading.Timer：这里经常由钩子线程（End）调用，
	/// 而 System.Windows.Forms.Timer 的 WM_TIMER 只会派发给"创建它的那条线程的消息泵"，
	/// 钩子线程的循环里从不 DispatchMessage —— 定时器永远不会触发，文档里承诺的几十 MB 回收是死代码。
	/// ReleaseBuffersForIdle 只动 GDI 缓冲与锁，不碰窗口消息，因此在线程池线程上执行是安全的。
	/// </summary>
	private void ScheduleIdleBufferRelease()
	{
		try
		{
			int num;
			lock (_gate)
			{
				num = _generation;
			}
			_idleReleaseGeneration = num;
			System.Threading.Timer timer = _idleReleaseTimer;
			if (timer == null)
			{
				timer = new System.Threading.Timer(OnIdleReleaseTick, null, Timeout.Infinite, Timeout.Infinite);
				_idleReleaseTimer = timer;
			}
			timer.Change(15000, Timeout.Infinite);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	private void OnIdleReleaseTick(object state)
	{
		try
		{
			int num;
			bool dragging = _dragging;
			lock (_gate)
			{
				num = _generation;
			}
			if (dragging || num != _idleReleaseGeneration || _frameWanted || (_hoverRectValid && _hoverHwnd != IntPtr.Zero))
			{
				return;
			}
			// 真闲着：把外框的像素缓冲放掉（窗口句柄保留，下次拖动 Rebuild 会重建）。
			_frame.ReleaseBuffersForIdle();
			Log.Trace("[内存] 拖动已结束 15 秒，外框像素缓冲已释放");
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	private void ShowDragFrame(Native.RECT windowRect, AppConfig c)
	{
		if (_overlaysReady)
		{
			Native.RECT rect = FrameRectOf(windowRect, c, out var _);
			_frame.ApplyTheme(c.FrameColorId, c.FrameStyleId, c.FrameThickness, c.FrameRadius, c.FrameOpacity, c.EffectiveFrameColor);
			if (rect.Width >= 4 && rect.Height >= 4)
			{
				_frame.SetRect(rect);
			}
		}
	}

	private void ShowFrame(Native.RECT windowRect)
	{
		if (_overlaysReady && _frameWanted)
		{
			AppConfig appConfig = _cfg();
			_frame.ApplyTheme(appConfig.FrameColorId, appConfig.FrameStyleId, appConfig.FrameThickness, appConfig.FrameRadius, appConfig.FrameOpacity, appConfig.EffectiveFrameColor);
			Native.RECT rect = FrameRectOf(windowRect, appConfig, out var inset);
			_frame.RaiseAboveAll = true;
			if (rect.Width >= 4 && rect.Height >= 4)
			{
				_frame.SetRect(rect);
			}
		}
	}

	internal bool TryShowFrame(Native.RECT windowRect, AppConfig c, int gen)
	{
		if (!_overlaysReady)
		{
			return false;
		}
		Native.RECT rect = FrameRectOf(windowRect, c, out var inset);
		_frame.RaiseAboveAll = true;
		if (rect.Width < 4 || rect.Height < 4)
		{
			return false;
		}
		lock (_gate)
		{
			if (!_dragging || _session.Gen != gen || !_frameWanted)
			{
				return false;
			}
			_frame.SetRect(rect);
			return true;
		}
	}

	private Native.RECT FrameRectOf(Native.RECT windowRect, AppConfig c, out bool inset)
	{
		int num = FrameStyles.MarginFor(c.FrameStyleId, c.FrameThickness);
		Native.RECT rECT = new Native.RECT(windowRect.Left + _inset.Left, windowRect.Top + _inset.Top, windowRect.Right - _inset.Right, windowRect.Bottom - _inset.Bottom);
		if (!FrameStyles.FillsScreen(rECT, num, _frameInside))
		{
			inset = false;
			_frameInside = false;
			return new Native.RECT(rECT.Left - num, rECT.Top - num, rECT.Right + num, rECT.Bottom + num);
		}
		inset = true;
		_frameInside = true;
		return rECT;
	}


	/// <summary>
	/// 触发键按住 1.2 秒还没开始拖 → 在光标旁浮一条操作提示。
	///
	/// 键盘分屏、Shift 关吸附这些能力没有可见入口，用户基本发现不了；
	/// 与其在设置里写一堆说明，不如在真正用得到的那一刻提示一次。
	/// </summary>
	private void ArmHintTimer(Native.POINT pt)
	{
		try
		{
			if (!_cfg().ShowTriggerHint)
			{
				return;
			}
			if (_hintTimer == null)
			{
				_hintTimer = new System.Threading.Timer(OnHintTimer, null, Timeout.Infinite, Timeout.Infinite);
			}
			_hintTimer.Change(1200, Timeout.Infinite);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	/// <summary>提示浮层的延时回调（复用同一个 Timer，避免在高频路径上反复 new）。</summary>
	private void OnHintTimer(object state)
	{
		try
		{
			lock (_gate)
			{
				if (_dragging || !_hoverRectValid || !_frameWanted)
				{
					return;
				}
			}
			Native.RECT rect = _hoverRect;
			nint hwnd = _hoverHwnd;
			string text = HintText();
			if (!string.IsNullOrEmpty(text))
			{
				_hint.ShowText(text, rect, (float)Native.DpiOfWindow(hwnd) / 96f);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	private string HintText()
	{
		try
		{
			AppConfig appConfig = _cfg();
			List<string> list = new List<string>();
			list.Add(Loc.T("左键") + " " + Actions.DisplayName(appConfig.ActionLeft));
			list.Add(Loc.T("右键") + " " + Actions.DisplayName(appConfig.ActionRight));
			if (appConfig.KeyboardTiling)
			{
				list.Add(Loc.T("方向键 分屏"));
			}
			if (appConfig.SnapToEdges && appConfig.SnapThreshold > 0)
			{
				// hooks.c:958：按住 Shift 是把档位顶到 3（吸窗口内侧），不是"关掉吸附"。
				list.Add(Loc.T("Shift 吸窗口内侧"));
			}
			if (appConfig.AeroSnap)
			{
				list.Add(Loc.T("贴边分屏"));
			}
			return string.Join("  ·  ", list);
		}
		catch
		{
			return string.Empty;
		}
	}

	internal void ProbeShowTriggerHintForTest()
	{
		_hint.Start();
		Native.POINT pt = default(Native.POINT);
		Native.GetCursorPos(out pt);
		Native.RECT rect = new Native.RECT(pt.X - 200, pt.Y - 100, pt.X + 400, pt.Y + 300);
		_hint.ShowText(HintText(), rect, 1f);
	}

	internal void ProbeHideTriggerHintForTest()
	{
		CancelHintTimer();
	}

	internal bool HintVisibleForTest
	{
		get
		{
			Thread.Sleep(100);
			return _hint.VisibleForTest;
		}
	}

	internal string HintTextForTest => HintText();

	internal static bool ProbeShiftDownForTest() => IsShiftDown();

	/// <summary>
	/// 键盘对吸附的临时影响，照 hooks.c:1030-1090：
	/// Shift 按住 → 强制吸到窗口内侧（3）；Shift 抬起 → 回到默认级别。
	/// Space 按下 → 本次拖动关掉吸附。
	/// </summary>
	internal void ObserveKey(int vk, bool down)
	{
		if (vk == Native.VK_SHIFT)
		{
			_snap = (down ? 3 : _snapBase);
			if (down && !_shiftHeld)
			{
				_shiftHeld = true;
				// hooks.c:958-963：状态一改就当场 MouseMove()，不等下一次鼠标事件。
				if (_dragging && TryLiveCursor(out var shiftPt))
				{
					PostFrame(_session, shiftPt, forceWrite: false);
				}
			}
			else if (!down)
			{
				_shiftHeld = false;
				// hooks.c:1088-1092：Shift 抬起也要当场 MouseMove()，否则吸附级别换回来了窗口还停在内侧。
				if (_dragging && TryLiveCursor(out var shiftUpPt))
				{
					PostFrame(_session, shiftUpPt, forceWrite: false);
				}
			}
			return;
		}
		if (vk == Native.VK_CONTROL)
		{
			if (!down)
			{
				// hooks.c:1052-1059：抬起时先补一帧再清 ctrl —— 被夹住的光标要立刻把窗口带到准确位置。
				if (_ctrlHeld)
				{
					_ctrlHeld = false;
					if (_dragging && !_ctrlIsTrigger && TryLiveCursor(out var ctrlUpPt))
					{
						PostFrame(_session, ctrlUpPt, forceWrite: false);
					}
				}
				return;
			}
			// hooks.c:984-993：拖动中重新按下 Ctrl → 解开 locked、清掉 origin.maximized（这次拖动不再自动重新最大化），
			// 把 origin.monitor 重新对准光标所在显示器，然后把窗口提到前台并当场走一帧。
			if (!_ctrlHeld)
			{
				_ctrlHeld = true;
				if (_dragging && !_ctrlIsTrigger && _session.Alt != null && TryLiveCursor(out var ctrlPt))
				{
					AltState alt = _session.Alt;
					alt.Locked = false;
					alt.OriginMaximized = false;
					alt.OriginMaximizedAtStart = false;
					alt.OriginMonitor = Native.MonitorFromPoint(ctrlPt, 2u);
					Native.SetForegroundWindow(_session.Hwnd);
					PostFrame(_session, ctrlPt, forceWrite: false);
				}
			}
			return;
		}
		if (vk == Native.VK_SPACE && down && _snap != 0)
		{
			_snap = 0;
		}
	}

	private void CancelHintTimer()
	{
		try
		{
			_hintTimer?.Dispose();
			_hintTimer = null;
			_hint.Hide();
		}
		catch
		{
		}
	}

	public void ShowHoverFrame(Native.POINT pt)
	{
		if (!_overlaysReady || _dragging)
		{
			return;
		}
		AppConfig appConfig = _cfg();
		if (!appConfig.ShowActivationFrame)
		{
			HideHoverFrame();
			return;
		}
		nint num = Native.WindowFromPoint(pt);
		if (num == IntPtr.Zero)
		{
			HideHoverFrame();
			return;
		}
		if (num != _hoverHwnd || !_hoverRectValid)
		{
			CancelHintTimer();
			ArmHintTimer(pt);
		}
		if (num == _hoverRaw)
		{
			if (_hoverHwnd == IntPtr.Zero || !Native.IsWindow(_hoverHwnd))
			{
				InvalidateHover();
			}
			else
			{
				if (Native.GetWindowRect(_hoverHwnd, out var lpRect))
				{
					if (!_frame.IsShown || !_hoverRectValid || lpRect.Left != _hoverRect.Left || lpRect.Top != _hoverRect.Top || lpRect.Right != _hoverRect.Right || lpRect.Bottom != _hoverRect.Bottom)
					{
						_hoverRect = lpRect;
						_hoverRectValid = true;
						_frameWanted = true;
						ShowFrame(lpRect);
					}
					return;
				}
				InvalidateHover();
			}
		}
		nint num2 = WindowActions.FindTarget(pt, appConfig.AllowFullscreenWindows || appConfig.ExitFullscreenOnTrigger);
		if (num2 == IntPtr.Zero)
		{
			_hoverRaw = num;
			HideHoverFrame();
			return;
		}
		if (!Native.GetWindowRect(num2, out var lpRect2))
		{
			_hoverRaw = num;
			HideHoverFrame();
			return;
		}
		_hoverRaw = num;
		_hoverHwnd = num2;
		_hoverRect = lpRect2;
		_hoverRectValid = true;
		_inset = ComputeInset(num2, lpRect2);
		_frameWanted = true;
		ShowFrame(lpRect2);
	}

	public void RefreshOverlayTheme()
	{
		InvalidateHover();
		if (!_overlaysReady)
		{
			return;
		}
		try
		{
			AppConfig appConfig = _cfg();
			_frame.ApplyTheme(appConfig.FrameColorId, appConfig.FrameStyleId, appConfig.FrameThickness, appConfig.FrameRadius, appConfig.FrameOpacity, appConfig.EffectiveFrameColor);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	public void InvalidateHover()
	{
		_hoverRaw = IntPtr.Zero;
		_hoverHwnd = IntPtr.Zero;
		_hoverRectValid = false;
	}

	public void HideHoverFrame()
	{
		_hoverRaw = IntPtr.Zero;
		_hoverHwnd = IntPtr.Zero;
		_hoverRectValid = false;
		if (!_dragging)
		{
			_frameWanted = false;
			if (_overlaysReady)
			{
				_frame.HideOverlay();
			}
		}
	}
}
