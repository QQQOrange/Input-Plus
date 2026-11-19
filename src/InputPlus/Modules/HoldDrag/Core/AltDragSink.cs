using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace InputPlus.Modules.HoldDrag;

/// <summary>
/// 拖拽引擎本体 —— 把 altDrag 的 hooks.c 搬成一份 IInputSink。
///
/// 最重要的一条和旧引擎相反：**全部在钩子回调里同步做完**。
/// 旧的 DragEngine 把鼠标事件塞进工作线程的消息队列，队列一积压，松手之后窗口还要
/// 追着几十毫秒前的坐标跑一段；原版 hooks.c 从钩子里直接算几何、直接 MoveWindow，
/// 天生没有这条队列。这份代码照这个结构写。
///
/// 对应关系（hooks.c 行号）：
///   OnKeyboard  → :930-1097（触发键 / Shift / Space / Esc / Ctrl）
///   OnMouse     → :1099-1912（按键分派、抽帧、起拖、松手）
///   MoveMouse   → :632-928（每帧几何 + MoveWindow）
///   :1999-2008 的 MOVE_TIMER（100ms 补帧）在这里是 System.Threading.Timer。
/// </summary>
internal sealed class AltDragSink : IInputSink, IDisposable
{
	private const int MoveTimerIntervalMs = 100;

	private const int RestoreTimerDelayMs = 1000;

	private const uint SendTimeoutMs = 200u;

	private const string TooltipsClass = "tooltips_class32";

	private static readonly int CodeMove = Actions.ToCode(Actions.Move);

	private static readonly int CodeResize = Actions.ToCode(Actions.Resize);

	private readonly Func<AppConfig?> _config;

	/// <summary>钩子线程与两个定时器共用：几何必须串行，否则两帧交错会把窗口写成中间态。</summary>
	private readonly object _gate = new object();

	// —— sharedstate（hooks.c:107-111 + state 结构）：整个拖动期间的状态 ——
	private bool _alt;

	private bool _activated;

	private bool _blockAltUp;

	private bool _blockMouseUp;

	private bool _ignoreCtrl;

	private bool _shift;

	private bool _ctrl;

	private bool _ctrlIsTrigger;

	private int _snap;

	private int _action;

	private nint _hwnd;

	private AltState? _state;

	private Native.POINT _clickPoint;

	private long _clickTick;

	private int _updateRate;

	private Native.POINT _prevPoint;

	private long _frames;

	private Timer? _moveTimer;

	private Timer? _restoreTimer;

	private bool _disposed;

	/// <summary>自检用：不移动真实光标也能喂坐标（null = 用 GetCursorPos）。</summary>
	internal static Func<Native.POINT?>? CursorProbeForTest;

	internal AltDragSink(Func<AppConfig?> config)
	{
		_config = config;
	}

	internal bool IsDragging
	{
		get
		{
			lock (_gate)
			{
				return _action != Actions.CodeNone;
			}
		}
	}

	internal int ActionForTest
	{
		get
		{
			lock (_gate)
			{
				return _action;
			}
		}
	}

	internal int SnapForTest
	{
		get
		{
			lock (_gate)
			{
				return _snap;
			}
		}
	}

	internal nint TargetForTest
	{
		get
		{
			lock (_gate)
			{
				return _hwnd;
			}
		}
	}

	internal long FramesWritten => Interlocked.Read(ref _frames);

	// ---------------- 键盘（hooks.c:930-1097） ----------------

	public bool OnKeyboard(bool down, ref Native.KBDLLHOOKSTRUCT kb)
	{
		AppConfig? appConfig = _config();
		if (appConfig == null)
		{
			return false;
		}
		// 自己补发的假 Ctrl 正在路上：那是 MasqueradeAsControl 干的，不是用户在按 Ctrl。
		if ((kb.flags & Native.LLKHF_INJECTED) != 0 && AltDragCore.InjectedBurst)
		{
			return false;
		}
		lock (_gate)
		{
			try
			{
				return HandleKey(appConfig, down, kb);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
				return false;
			}
		}
	}

	private bool HandleKey(AppConfig cfg, bool down, Native.KBDLLHOOKSTRUCT kb)
	{
		if (KeysUtil.MatchesKeyboard(cfg.TriggerKey, kb))
		{
			if (down)
			{
				_alt = true;
				_blockAltUp = false;
				_blockMouseUp = false;
				_ctrlIsTrigger = IsCtrlKey(cfg.TriggerKey);
				if (_ctrlIsTrigger)
				{
					_ignoreCtrl = true;
				}
			}
			else
			{
				// hooks.c:1064-1082：还有别的触发键按着就不收尾；否则把"用户按着触发键"瞒过去，
				// 然后只把 alt 放下 —— 拖动本身要继续，直到鼠标键松开（原版就是这样）。
				if (_blockAltUp || _action != Actions.CodeNone)
				{
					AltDragCore.MasqueradeAsControl(_state);
					_ignoreCtrl = false;
				}
				_alt = false;
				if (_action == Actions.CodeNone)
				{
					ResetDragState();
				}
			}
			return cfg.SwallowTrigger;
		}
		if (!_alt)
		{
			return false;
		}
		int num = (int)kb.vkCode;
		switch (num)
		{
		case Native.VK_SHIFT:
			if (down)
			{
				if (!_shift)
				{
					_shift = true;
					_snap = 3;
					MoveMouse(cfg, CursorOrZero());
				}
			}
			else
			{
				_shift = false;
				_snap = cfg.AutoSnap;
				MoveMouse(cfg, CursorOrZero());
			}
			break;
		case Native.VK_SPACE:
			// hooks.c:1085-1088：空格临时关掉吸附（这次拖动剩下的时间都关）。
			if (down && _snap != 0 && (_action != Actions.CodeNone || _activated))
			{
				_snap = 0;
			}
			break;
		case Native.VK_ESCAPE:
			// hooks.c:976-980：Esc 直接放弃这次拖动，而且**不吞**这个键。
			if (down && _action != Actions.CodeNone)
			{
				CancelDrag();
			}
			return false;
		case Native.VK_CONTROL:
			HandleCtrl(cfg, down);
			break;
		}
		return false;
	}

	/// <summary>hooks.c:984-993 / 1052-1059：按住 Ctrl 把光标夹在按下那一刻的显示器里。</summary>
	private void HandleCtrl(AppConfig cfg, bool down)
	{
		if (down)
		{
			if (_action != Actions.CodeNone && !_ignoreCtrl && !_ctrl)
			{
				if (_state != null)
				{
					_state.Locked = false;
					_state.OriginMaximized = false;
					Native.POINT pt = CursorOrZero();
					_state.OriginMonitor = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
				}
				if (_hwnd != IntPtr.Zero)
				{
					Native.SetForegroundWindow(_hwnd);
				}
				MoveMouse(cfg, CursorOrZero());
				_ctrl = true;
			}
		}
		else
		{
			if (_action != Actions.CodeNone && !_ignoreCtrl)
			{
				MoveMouse(cfg, CursorOrZero());
			}
			_ctrl = false;
		}
	}

	// ---------------- 鼠标（hooks.c:1099-1912） ----------------

	public bool OnMouse(int msg, ref Native.MSLLHOOKSTRUCT ms)
	{
		AppConfig? appConfig = _config();
		if (appConfig == null)
		{
			return false;
		}
		// 钩子把坐标换成了"本进程 DPI 空间"，几何一律用物理像素（和多显示器坐标同一套）。
		Native.POINT pt = CoordSpace.ToPhysical(ms.pt);
		switch (msg)
		{
		case Native.WM_MOUSEMOVE:
			return OnMove(appConfig, pt);
		case Native.WM_LBUTTONDOWN:
			return OnButton(appConfig, MouseButton.Left, down: true, pt);
		case Native.WM_LBUTTONUP:
			return OnButton(appConfig, MouseButton.Left, down: false, pt);
		case Native.WM_MBUTTONDOWN:
			return OnButton(appConfig, MouseButton.Middle, down: true, pt);
		case Native.WM_MBUTTONUP:
			return OnButton(appConfig, MouseButton.Middle, down: false, pt);
		case Native.WM_RBUTTONDOWN:
			return OnButton(appConfig, MouseButton.Right, down: true, pt);
		case Native.WM_RBUTTONUP:
			return OnButton(appConfig, MouseButton.Right, down: false, pt);
		case Native.WM_XBUTTONDOWN:
			return OnButton(appConfig, XButtonOf(ms), down: true, pt);
		case Native.WM_XBUTTONUP:
			return OnButton(appConfig, XButtonOf(ms), down: false, pt);
		default:
			return false;
		}
	}

	private bool OnButton(AppConfig cfg, MouseButton button, bool down, Native.POINT pt)
	{
		int num = Actions.ToCode(cfg.ActionFor(button));
		lock (_gate)
		{
			try
			{
				return down ? BeginDrag(cfg, num, pt) : EndDrag(cfg, num);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
				return false;
			}
		}
	}

	/// <summary>hooks.c:1104-1130：抽帧 + 记 prevpt。</summary>
	private bool OnMove(AppConfig cfg, Native.POINT pt)
	{
		lock (_gate)
		{
			try
			{
				_prevPoint = pt;
				if (_action != CodeMove && _action != CodeResize)
				{
					return false;
				}
				int num = ((_action == CodeMove) ? cfg.MoveRate : cfg.ResizeRate);
				if (num < 1)
				{
					num = 1;
				}
				// hooks.c:1120-1127：updaterate = (updaterate + 1) % rate，只有落到 0 才真的写窗口。
				_updateRate = (_updateRate + 1) % num;
				if (_updateRate != 0)
				{
					return false;
				}
				if (pt.X != _clickPoint.X || pt.Y != _clickPoint.Y)
				{
					_clickTick = 0L;
				}
				MoveMouse(cfg, pt);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
			return false;
		}
	}

	/// <summary>hooks.c:1491-1877：按下鼠标键那一刻的全部判断与起手。</summary>
	private bool BeginDrag(AppConfig cfg, int action, Native.POINT pt)
	{
		if (_action != Actions.CodeNone)
		{
			return false;
		}
		if (!_alt)
		{
			return false;
		}
		nint num = Native.WindowFromPoint(pt);
		if (num == IntPtr.Zero)
		{
			return false;
		}
		// hooks.c:1500-1510：Tooltip 窗口会盖住真正的目标，先藏掉再重取。
		if (string.Equals(AppRules.ClassNameOf(num), TooltipsClass, StringComparison.OrdinalIgnoreCase))
		{
			Native.ShowWindow(num, Native.SW_HIDE);
			num = Native.WindowFromPoint(pt);
		}
		nint num2 = Native.GetAncestor(num, Native.GA_ROOT);
		if (num2 == IntPtr.Zero)
		{
			return false;
		}
		long windowLong = Native.GetWindowLong(num2, Native.GWL_STYLE);
		// hooks.c:1520-1545 只有黑名单，没有「不拖自己进程的窗口」这一条 —— 原样照抄。
		if (AppRules.IsBlacklisted(cfg, num2))
		{
			return false;
		}
		Native.WINDOWPLACEMENT lpwndpl = new Native.WINDOWPLACEMENT
		{
			length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
		};
		if (!Native.GetWindowPlacement(num2, ref lpwndpl))
		{
			return false;
		}
		if (!Native.GetWindowRect(num2, out var wnd))
		{
			return false;
		}
		// hooks.c:1560-1570：没有标题栏、又正好铺满整块屏幕的（全屏游戏/播放器）不接管。
		if ((windowLong & Native.WS_CAPTION) != Native.WS_CAPTION)
		{
			Native.RECT rcMonitor = Native.GetMonitorInfoAt(pt).rcMonitor;
			if (wnd.Left == rcMonitor.Left && wnd.Top == rcMonitor.Top && wnd.Right == rcMonitor.Right && wnd.Bottom == rcMonitor.Bottom)
			{
				return false;
			}
		}
		// hooks.c:1616-1629：双击 = 最大化 / 半屏（先判双击，再决定这次要不要起拖）。
		if ((action == CodeMove || action == CodeResize) && _clickTick != 0L && Environment.TickCount64 - _clickTick <= Native.GetDoubleClickTime())
		{
			int num3 = action;
			_action = Actions.CodeNone;
			_clickTick = 0L;
			_blockMouseUp = true;
			StopTimers();
			if (num3 == CodeMove)
			{
				AltDragCore.DoubleClickMaximize(num2, pt);
			}
			else
			{
				AltDragCore.DoubleClickAeroHalf(num2, pt);
			}
			return true;
		}
		_action = action;
		if (_snap == 0)
		{
			_snap = cfg.AutoSnap;
		}
		_activated = true;
		_blockAltUp = true;
		_hwnd = num2;
		_prevPoint = pt;
		_state = new AltState();
		AltDragCore.RecordOrigin(_state, num2, wnd);
		_state.Entry = WndDb.Touch(num2);
		if (cfg.AutoFocus)
		{
			Native.SetForegroundWindow(num2);
		}
		bool flag = IsITunes(num2);
		// hooks.c:1731-1794：其它动作做完就结束（做完仍然吞掉这一下）。
		if (action == Actions.ToCode(Actions.Minimize))
		{
			Native.SendMessageW(num2, Native.WM_SYSCOMMAND, Native.SC_MINIMIZE, IntPtr.Zero);
			return true;
		}
		if (action == Actions.ToCode(Actions.Center))
		{
			CenterWindow(num2);
			return true;
		}
		if (action == Actions.ToCode(Actions.AlwaysOnTop))
		{
			ToggleTopmost(num2);
			return true;
		}
		if (action == Actions.ToCode(Actions.Close))
		{
			Native.SendMessageW(num2, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
			return true;
		}
		if (action == Actions.ToCode(Actions.Lower))
		{
			if (IsShiftDown())
			{
				Native.SendMessageW(num2, Native.WM_SYSCOMMAND, Native.SC_MINIMIZE, IntPtr.Zero);
			}
			else
			{
				Native.SetWindowPos(num2, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE);
			}
			return true;
		}
		if (action != CodeMove && action != CodeResize)
		{
			// Nothing：什么都不做，但这一下仍然吞掉（原版同样吞）。
			return true;
		}
		// hooks.c:1619-1625：让目标窗口自己报 ptMinTrackSize / ptMaxTrackSize。
		AltDragCore.QueryMinMax(num2, pt, out var minTrack, out var maxTrack);
		_state.MinTrack = minTrack;
		_state.MaxTrack = maxTrack;
		if (action == CodeMove)
		{
			AltDragCore.SetupMove(_state, num2, pt, ref wnd);
		}
		else
		{
			AltDragCore.SetupResize(_state, num2, pt, ref wnd);
		}
		// 第一帧：窗口立刻贴到光标下面（原版起拖后马上跑一次 MouseMove）。
		MoveMouse(cfg, pt);
		// hooks.c:1841-1847：告诉目标窗口"开始拖了"（iTunes 除外），并按 MoveRate/ResizeRate 起补帧定时器。
		if (!flag)
		{
			Native.SendMessageTimeoutW(num2, Native.WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, SendTimeoutMs, out var _);
		}
		_updateRate = 0;
		if ((action == CodeMove ? cfg.MoveRate : cfg.ResizeRate) > 1)
		{
			StartMoveTimer();
		}
		// hooks.c:1857-1863：补一对假 Ctrl，别让目标窗口以为用户还按着触发键。
		AltDragCore.MasqueradeAsControl(_state);
		_clickTick = Environment.TickCount64;
		_clickPoint = pt;
		return true;
	}

	/// <summary>hooks.c:1879-1908：松开鼠标键 —— 结束本次拖动，并吞掉这一下。</summary>
	private bool EndDrag(AppConfig cfg, int action)
	{
		if (_blockMouseUp)
		{
			_blockMouseUp = false;
			return true;
		}
		if (_action == Actions.CodeNone || _action != action)
		{
			return false;
		}
		int num = _action;
		_action = Actions.CodeNone;
		_activated = false;
		StopTimers();
		if ((num == CodeMove || num == CodeResize) && !IsITunes(_hwnd))
		{
			Native.SendMessageTimeoutW(_hwnd, Native.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, SendTimeoutMs, out var _);
		}
		if (!_alt)
		{
			ResetDragState();
		}
		return true;
	}

	/// <summary>hooks.c:632-928：一帧几何。位置一律现取 GetCursorPos，事件里的坐标只当兜底。</summary>
	private void MoveMouse(AppConfig cfg, Native.POINT fallback)
	{
		if (_action != CodeMove && _action != CodeResize)
		{
			return;
		}
		AltState? altState = _state;
		if (altState == null || _hwnd == IntPtr.Zero)
		{
			return;
		}
		if (!Native.IsWindow(_hwnd))
		{
			CancelDrag();
			return;
		}
		Native.POINT pt;
		if (!TryCursor(out pt))
		{
			pt = fallback;
		}
		// hooks.c:651-657：按住 Ctrl 时把光标夹在起手那块显示器里。
		if (!_ctrlIsTrigger && !_ignoreCtrl && altState.OriginMonitor != IntPtr.Zero && (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0)
		{
			Native.MONITORINFO lpmi = new Native.MONITORINFO
			{
				cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO))
			};
			if (Native.GetMonitorInfoW(altState.OriginMonitor, ref lpmi))
			{
				Native.RECT rcMonitor = lpmi.rcMonitor;
				if (pt.X < rcMonitor.Left)
				{
					pt.X = rcMonitor.Left;
				}
				else if (pt.X >= rcMonitor.Right)
				{
					pt.X = rcMonitor.Right - 1;
				}
				if (pt.Y < rcMonitor.Top)
				{
					pt.Y = rcMonitor.Top;
				}
				else if (pt.Y >= rcMonitor.Bottom)
				{
					pt.Y = rcMonitor.Bottom - 1;
				}
			}
		}
		// hooks.c:663-706：拖到另一块屏且原本是最大化的 —— 在新屏里居中重新最大化，锁住这一帧。
		if (_action == CodeMove && cfg.AutoRemaximize != 0 && AltDragCore.AutoRemaximize(altState, _hwnd, pt))
		{
			if (cfg.AutoRemaximize == 2)
			{
				StartRestoreTimer();
			}
			return;
		}
		if (altState.Locked)
		{
			return;
		}
		if (!Native.GetWindowRect(_hwnd, out var wnd))
		{
			return;
		}
		int num = cfg.SnapThreshold;
		int posx;
		int posy;
		int wndwidth;
		int wndheight;
		if (_action == CodeMove)
		{
			posx = pt.X - altState.OffsetX;
			posy = pt.Y - altState.OffsetY;
			wndwidth = wnd.Width;
			wndheight = wnd.Height;
			AltDragCore.AeroOutcome aeroOutcome = AltDragCore.MoveAero(altState, cfg, _hwnd, pt, ref posx, ref posy, ref wndwidth, ref wndheight);
			if (aeroOutcome == AltDragCore.AeroOutcome.Maximized)
			{
				// 顶部 5px：这一帧已经把窗口最大化了，不要再写一次。
				return;
			}
			if (aeroOutcome == AltDragCore.AeroOutcome.Written)
			{
				// Aero 贴边：立刻写一次真实尺寸（这一帧不走吸附）。
				MoveWindowNow(posx, posy, wndwidth, wndheight);
				AltDragCore.NoteAeroWritten(altState, _hwnd);
				return;
			}
			if (_snap > 0 && num > 0)
			{
				SnapEngine.Move(_snap, num, _hwnd, ref posx, ref posy, wndwidth, wndheight);
			}
		}
		else
		{
			posx = wnd.Left;
			posy = wnd.Top;
			wndwidth = wnd.Width;
			wndheight = wnd.Height;
			AltDragCore.ResizeGeometry(altState, pt, wnd, ref posx, ref posy, ref wndwidth, ref wndheight);
			if (_snap > 0 && num > 0)
			{
				SnapEngine.Resize(_snap, num, _hwnd, altState.EdgeX, altState.EdgeY, ref posx, ref posy, ref wndwidth, ref wndheight);
			}
		}
		// hooks.c:927：每个鼠标事件都无条件写一次，没有"和上次一样就跳过"。
		MoveWindowNow(posx, posy, wndwidth, wndheight);
	}

	private void MoveWindowNow(int x, int y, int w, int h)
	{
		if (w < 1 || h < 1)
		{
			return;
		}
		Native.MoveWindow(_hwnd, x, y, w, h, bRepaint: true);
		Interlocked.Increment(ref _frames);
	}

	// ---------------- 定时器（hooks.c:1999-2008 / 1974-1997） ----------------

	private void StartMoveTimer()
	{
		if (_moveTimer == null)
		{
			_moveTimer = new Timer(OnMoveTick, null, -1, -1);
		}
		_moveTimer.Change(MoveTimerIntervalMs, MoveTimerIntervalMs);
	}

	private void OnMoveTick(object? _)
	{
		AppConfig? appConfig = _config();
		if (appConfig == null)
		{
			return;
		}
		lock (_gate)
		{
			try
			{
				if (_action != CodeMove && _action != CodeResize)
				{
					StopMoveTimer();
					return;
				}
				if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd) || !TryCursor(out var pt))
				{
					return;
				}
				// hooks.c:2004-2007：光标没动就不重写（避免没事找事地刷目标窗口）。
				if (pt.X == _prevPoint.X && pt.Y == _prevPoint.Y)
				{
					return;
				}
				_prevPoint = pt;
				MoveMouse(appConfig, pt);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
		}
	}

	private void StartRestoreTimer()
	{
		if (_restoreTimer == null)
		{
			_restoreTimer = new Timer(OnRestoreTick, null, -1, -1);
		}
		_restoreTimer.Change(RestoreTimerDelayMs, -1);
	}

	private void OnRestoreTick(object? _)
	{
		AppConfig? appConfig = _config();
		if (appConfig == null)
		{
			return;
		}
		lock (_gate)
		{
			try
			{
				if (_action != CodeMove || _state == null || _hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd) || !TryCursor(out var pt))
				{
					return;
				}
				AltDragCore.RestoreAfterRemaximize(_state, _hwnd, pt);
				MoveMouse(appConfig, pt);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
		}
	}

	private void StopMoveTimer()
	{
		_moveTimer?.Change(-1, -1);
	}

	private void StopTimers()
	{
		_moveTimer?.Change(-1, -1);
		_restoreTimer?.Change(-1, -1);
	}

	// ---------------- 状态收尾 ----------------

	/// <summary>钩子被重装之后调用：缝隙里丢掉的 up 事件不再来，把状态清干净。</summary>
	internal void Rearm()
	{
		lock (_gate)
		{
			_alt = false;
			StopTimers();
			ResetDragState();
		}
	}

	/// <summary>Esc：放弃这次拖动（hooks.c:976-980 只清 action，不发 EXITSIZEMOVE）。</summary>
	private void CancelDrag()
	{
		_action = Actions.CodeNone;
		_activated = false;
		StopTimers();
	}

	private void ResetDragState()
	{
		_state = null;
		_hwnd = IntPtr.Zero;
		_activated = false;
		_blockAltUp = false;
		_blockMouseUp = false;
		_ctrl = false;
		_updateRate = 0;
	}

	// ---------------- 小工具 ----------------

	private static bool TryCursor(out Native.POINT pt)
	{
		Func<Native.POINT?>? cursorProbeForTest = CursorProbeForTest;
		if (cursorProbeForTest != null)
		{
			Native.POINT? pOINT = cursorProbeForTest();
			if (pOINT.HasValue)
			{
				pt = pOINT.GetValueOrDefault();
				return true;
			}
			pt = default(Native.POINT);
			return false;
		}
		return Native.GetCursorPos(out pt);
	}

	private static Native.POINT CursorOrZero()
	{
		if (TryCursor(out var pt))
		{
			return pt;
		}
		return default(Native.POINT);
	}

	private static bool IsShiftDown()
	{
		try
		{
			return (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsCtrlKey(string? id)
	{
		int num = KeysUtil.ToVirtualKey(id ?? "");
		return num == 17 || num == 162 || num == 163;
	}

	private static bool IsITunes(nint hwnd)
	{
		return AppRules.ClassNameOf(hwnd).IndexOf("iTunes", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static MouseButton XButtonOf(Native.MSLLHOOKSTRUCT ms)
	{
		return ((ms.mouseData >> 16) & 0xFFFF) == 1 ? MouseButton.X1 : MouseButton.X2;
	}

	private static void CenterWindow(nint hwnd)
	{
		AltState altState = new AltState();
		if (!Native.GetWindowRect(hwnd, out var wnd))
		{
			return;
		}
		AltDragCore.RecordOrigin(altState, hwnd, wnd);
		Native.POINT pt = default(Native.POINT);
		Native.GetCursorPos(out pt);
		Native.RECT rcWork = Native.GetMonitorInfoAt(pt).rcWork;
		int num = rcWork.Left + (rcWork.Width - altState.OriginWidth) / 2;
		int num2 = rcWork.Top + (rcWork.Height - altState.OriginHeight) / 2;
		Native.MoveWindow(hwnd, num, num2, altState.OriginWidth, altState.OriginHeight, bRepaint: true);
	}

	private static void ToggleTopmost(nint hwnd)
	{
		long windowLong = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
		bool flag = (windowLong & Native.WS_EX_TOPMOST) != 0;
		Native.SetWindowPos(hwnd, flag ? Native.HWND_NOTOPMOST : Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE);
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (!_disposed)
			{
				_disposed = true;
				StopTimers();
				_moveTimer?.Dispose();
				_restoreTimer?.Dispose();
				_moveTimer = null;
				_restoreTimer = null;
				ResetDragState();
			}
		}
	}
}
