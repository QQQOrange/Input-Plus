using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace InputPlus.Modules.HoldDrag;

/// <summary>
/// 拖拽引擎本体 —— 把 AltSnap / altDrag 的 hooks.c 搬成一份 IInputSink。
///
/// 移植来源：AltSnap（https://github.com/RaymondGillibert/AltSnap，GNU GPL v3 或更新版本，
/// Copyright (C) 2022 Raymond Gillibert；上游 AltDrag Copyright (C) 2015 Stefan Sundin）。
/// 行号注释里带「AltSnap」的指 AltSnap-main/hooks.c，其余的沿用更早的 altdrag-master/hooks.c。
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
	/// <summary>
	/// hooks.c:1853 的 MOVE_TIMER 间隔（毫秒）。原版只在 MoveRate/ResizeRate &gt; 1 时起它，
	/// 这里常起：既补帧（钩子被摘、别的程序 SetCursorPos 挪光标时把窗口追上去），
	/// 也顺手做「up 丢了」的体检。它只在**光标真的动了**的时候才写窗口，
	/// 所以空闲时不会凭空产生帧 —— 这是原版 MOVE_TIMER 里 state.prevpt 那句的作用。
	/// </summary>
	private const int MoveTimerMs = 100;

	/// <summary>一帧写窗口超过这个时长就记一次日志，用来抓"慢窗口"（实测 kelivo 一次 WM_SIZE 要一百多毫秒）。</summary>
	private const int SlowWriteMs = 30;

	/// <summary>
	/// 「up 丢了」的体检只有在**钩子也安静了**之后才算数（毫秒）。
	///
	/// 只看 GetAsyncKeyState 会误杀：注入输入（SendInput 脚本、宏）、触控板轻点拖动、
	/// 画笔、远程桌面/向日葵这一类环境里，物理键状态会稳定读回「松」，而钩子明明还在送事件。
	/// 只凭那个读数收尾，现象就是「拖一下就断」—— 而事件还在来就说明手还在拖。
	/// </summary>
	private const int LostUpSilenceMs = 400;

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

	/// <summary>这次拖的是哪个鼠标键。BeginDrag 记下，"up 丢了"的体检要用它复查物理键状态。</summary>
	private MouseButton _dragButton;

	/// <summary>
	/// 写入线程连续读到「鼠标键已经松开」的次数：三次、且钩子已经安静 LostUpSilenceMs，
	/// 才认定那次 up 真的没送到（钩子被系统摘掉 / 被安全软件拦掉）。
	/// </summary>
	private int _lostUpTicks;

	/// <summary>
	/// "up 丢了"的体检只在真的按过物理键时才有意义。自检/脚本注入的拖动没有真实的键按下，
	/// GetAsyncKeyState 永远读到松 —— 这时候查下去会把正常拖动当成"up 丢了"掐掉，
	/// 所以体检先解着，只要哪一帧读到键真的按下就武装起来（真实使用第一帧就武装）。
	/// </summary>
	private bool _lostUpArmed;

	/// <summary>鼠标钩子最后一次送来事件的时刻（TickCount64）。「up 丢了」的体检靠它判断钩子是不是真的安静了。</summary>
	private long _lastMouseTick;

	private bool _ignoreCtrl;

	private bool _shift;

	private bool _ctrl;

	private bool _ctrlIsTrigger;

	/// <summary>
	/// 物理修饰键状态：只由键盘钩子写，任意线程读（和触发键那对状态同一个理由）。
	///
	/// 为什么必须自己记：MoveMouse（Ctrl 夹屏 / Shift 拉满档）会被 100ms 的补帧定时器在线程池
	/// 线程上调用，而 GetAsyncKeyState 在那个线程上会稳定读回 0 —— 实测同一毫秒内 UI 线程
	/// 0xFFFF8000、线程池线程 0x0（见 _triggerDownSeen 那段注释）。altdrag 在钩子/窗口消息
	/// 上下文里读所以没这个问题，我们的补帧是独立线程，只能自己记。
	/// </summary>
	private volatile bool _ctrlPhys;

	private volatile bool _shiftPhys;

	private int _snap;

	private int _action;

	private nint _hwnd;

	private AltState? _state;

	private Native.POINT _clickPoint;

	private long _clickTick;

	private Native.POINT _prevPoint;

	private long _frames;

	/// <summary>
	/// hooks.c:1120 的 state.updaterate：每来一个鼠标移动事件 +1，攒到 rate 个才真写一帧。
	/// 这是原版唯一的抽帧机制 —— 事件计数，不是时间片。
	/// </summary>
	private int _updaterate;

	/// <summary>
	/// hooks.c:3768 / hooks.c:4040 的 state.moving == DRAG_WAIT：起手那一刻不跟手，
	/// 要等光标离开按下点超过 DragOutThresholdPx 才「解锁」开始移动窗口。
	/// DragThreshold 默认 1，但只对「本来就贴着屏幕边」的窗口生效，所以日常感觉不到延迟。
	/// </summary>
	private bool _dragWait;

	/// <summary>上一次移动事件的 msg-&gt;time，给 RezTimer==1/3 的「时间戳变了才写」用。</summary>
	private uint _lastEventTime;

	/// <summary>
	/// hooks.c:1999-2008 的 MOVE_TIMER。拖动期间每 MoveTimerMs 一拍：
	/// 光标和上次记下的位置不一样才补一帧（原版就是这一句），顺带体检 up 有没有丢。
	/// </summary>
	private Timer? _moveTimer;

	/// <summary>最近一次鼠标事件给的坐标（GetCursorPos 读不到时的兜底）。</summary>
	private Native.POINT _pendingPoint;

	/// <summary>慢帧计数，只用来限流日志。</summary>
	private int _slowWrites;

	private Timer? _restoreTimer;

	private bool _disposed;

	/// <summary>1 = 触发键正按着。钩子线程写、别的模块读，所以走 Interlocked。</summary>
	private int _triggerHeld;

	/// <summary>v1.6.0：按着触发键、但还没到长按阈值的那一段（转正之前先吞住）。</summary>
	private bool _triggerPending;

	// v1.6.1：这一次还没转正的按压，到底有没有把触发键吞住。
	// 只有真吞过，抬起时才需要补发一个「按下+抬起」；没吞过再补就是两个回车。

	private int _pendingVk;

	/// <summary>
	/// v1.7.0「吞掉触发键」：这一次按压的触发键是我们吃掉的（设置里打开了 SwallowTrigger）。
	///
	/// 为什么要有这个字段：吞键是可选项，关着的时候行为必须和 v1.6.1 一模一样 ——
	/// 触发键即时透传、抬起不补发。所以「吞没吞过」得按次记账，不能只看配置。
	/// 抬起时只有它 true 才 return true（吞掉 keyup），也才有资格补发。
	/// </summary>
	private bool _triggerSwallowed;

	/// <summary>
	/// v1.7.0：这一次按压已经补发过一次按键了。长按定时器取消、设备白名单取消、抬起补发
	/// 三条路都可能想补发，同一次按压只允许补一次 —— 否则用户按一下 Enter 打出两个回车。
	/// </summary>
	private bool _triggerReplayed;

	/// <summary>
	/// v1.7.0：这一次按压真的起过拖（BeginDrag 走通了）。
	///
	/// 为什么不在 keyup 时看 _action：拖动在**鼠标抬起**时就收尾了（ResetDragState 把 _action 清成
	/// None），而触发键往往还按着几十毫秒 —— keyup 那一刻看 _action 永远看到 None，
	/// 于是一次正常的「按住拖窗口」会被误判成短按，白白补发一个回车（自检实测过）。
	/// 起拖那一刻记账才是准的。
	/// </summary>
	private bool _triggerDragSeen;

	private AppConfig? _pendingConfig;

	private Timer? _triggerTimer;

	/// <summary>长按回调抢不到 _gate 时的重排次数。</summary>
	private int _tickRetry;

	/// <summary>
	/// 触发键的按下状态，只由钩子回调写入（钩子线程写、任意线程读）。
	///
	/// 为什么不拿 GetAsyncKeyState 当主判据：异步键状态是跟着线程/输入队列走的 ——
	/// 长按定时器跑在线程池线程上，在**那个线程**读同一个键会稳定地读回 0
	/// （实测同一毫秒内：UI 线程 0xFFFF8000，Timer 线程 0x0）。于是
	/// 「按够 TriggerHoldMs 转正」这一步永远失败，用户看到的就是「按住拖拽完全没反应」。
	/// 钩子回调收到的 WM_KEYDOWN/WM_KEYUP 才是这件事最权威的来源，用它。
	/// </summary>
	private volatile bool _triggerDownSeen;

	/// <summary>钩子最后一次看到的触发键状态（true = 按下）。</summary>
	private volatile bool _triggerDown;

	/// <summary>钩子最后一次收到触发键事件的 TickCount（自动重复会不停刷新它）。</summary>
	private long _triggerEventTick;

	/// <summary>
	/// 这次按压的触发键是哪只键盘发出的 —— 由线程池线程**异步**解析后写进来。
	///
	/// 为什么不能同步问：设备解析要等 Raw Input 的 WM_INPUT（钩子永远比它早到），
	/// KeyboardDeviceTracker.ResolveDevice 里是 Thread.Sleep(1) 轮询，而系统定时器
	/// 精度只有 15.6ms —— 60 轮实际要 900 多毫秒。这段等待如果发生在键盘钩子回调里，
	/// 就是**全系统键盘卡住将近一秒**（实测 18:10:21.723 → 22.639），而且鼠标钩子
	/// 也因此晚装了一步，用户那一下点击已经过去了。
	/// 现在钩子回调里只读上一次算好的结果，解析放到线程池上跑，200ms 的长按窗口足够。
	/// </summary>
	private volatile bool _triggerDeviceAllowed = true;

	/// <summary>本次按压的设备是否已经解析出来了（false = 还没结论，按"放行"处理）。</summary>
	private volatile bool _triggerDeviceKnown;

	/// <summary>设备解析的代号：新一次按压会作废上一次还没跑完的解析。</summary>
	private int _deviceGen;

	/// <summary>钩子这么久没再见过触发键的任何一个事件，就当它已经松开了（防漏 up 卡死）。</summary>
	private const long TriggerIdleLimitMs = 60000L;

	/// <summary>v1.6.0：把按键解析成设备标识（AppHost 接的是 KeyboardDeviceTracker）。null = 不区分设备。</summary>
	internal static Func<int, string?>? DeviceResolver;

	/// <summary>
	/// 自检桩：设了之后「补发按键」不真发，交给它记账 —— 不然自检会往当前前台窗口敲一串回车。
	/// </summary>
	internal static Action<int>? ReplayProbeForTest;

	/// <summary>自检用：不移动真实光标也能喂坐标（null = 用 GetCursorPos）。</summary>
	internal static Func<Native.POINT?>? CursorProbeForTest;

	/// <summary>自检用：冒充"触发键还按着"（null = 真去问 GetAsyncKeyState）。</summary>
	internal static Func<bool>? TriggerProbeForTest;

	/// <summary>自检用：假造鼠标键的物理状态；null 就走 GetAsyncKeyState。</summary>
	internal static Func<MouseButton, bool>? ButtonProbeForTest;

	/// <summary>
	/// 自检用：允许拖拽本进程自己的窗口。
	/// 正常运行时自家窗口一律不当拖拽目标（HUD/设置窗铺在屏幕上，会把界面拖乱），
	/// 而 SelfTest.HoldDrag 的靶子恰恰是本进程造的窗口，所以那里要把它打开。
	/// </summary>
	internal static bool DragOwnWindowForTest;

	/// <summary>
	/// 按需装卸鼠标钩子（altdrag hooks.c:956 的 HookMouse / :1078 的 UnhookMouse 同款）。null = 不装。
	/// </summary>
	private readonly Action<bool>? _mouseHookWanted;

	internal AltDragSink(Func<AppConfig?> config, Action<bool>? mouseHookWanted = null)
	{
		_config = config;
		_mouseHookWanted = mouseHookWanted;
	}

	/// <summary>
	/// 告诉钩子引擎「接下来要收鼠标事件了 / 不用了」。
	///
	/// 为什么长按计时阶段（还没转正）就得装：兜底路径是在鼠标按下那一刻才 ArmTrigger 的，
	/// 靠的正是这一下点击本身 —— 鼠标钩子没装，这一下永远进不来。
	/// </summary>
	private void WantMouseHook(bool wanted)
	{
		try
		{
			_mouseHookWanted?.Invoke(wanted);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	/// <summary>
	/// 触发键此刻是否按着 —— 给「鼠标手势」让路用：按着触发键说明用户要拖窗口，
	/// 这颗鼠标键不能再被当成手势起笔（否则按住 Enter 点右键会画出手势）。
	/// </summary>
	internal bool TriggerHeld
	{
		get
		{
			if (Interlocked.CompareExchange(ref _triggerHeld, 0, 0) == 0)
			{
				return false;
			}
			/* 锁存位是"按下置 1、抬起清 0"的跨线程旗子，可抬起那一下未必送得到：
			 * 钩子被系统摘掉重装、被安全软件拦掉、前台在拖动中被别的程序抢走焦点……
			 * 旗子留在 1 上，鼠标手势就会被**永久**让路 —— 用户看到的现象正是
			 * "按住拖拽用过一次之后，鼠标手势全都画不出来了，非得重启"。
			 * 所以没在拖的时候用钩子自己记的按下/抬起复核一次
			 * （TriggerStillHeld 带 60 秒空闲兜底），确认触发键早松了就放下旗子。 */
			if (_action != Actions.CodeNone)
			{
				return true;
			}
			AppConfig? appConfig = _config();
			if (appConfig == null)
			{
				return true;
			}
			if (TriggerStillHeld(appConfig))
			{
				return true;
			}
			Interlocked.Exchange(ref _triggerHeld, 0);
			HookProbe.W("TriggerHeld：锁存位是上一次没收尾留下的，触发键其实已经松了 —— 清掉，让鼠标手势重新接管");
			return false;
		}
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

	/// <summary>自检要看的拖动状态（缩放起的那条边/那条轴，九宫格一次定死）。</summary>
	internal AltState? StateForTest
	{
		get
		{
			lock (_gate)
			{
				return _state;
			}
		}
	}

	internal long FramesWritten => Interlocked.Read(ref _frames);

	/// <summary>
	/// 自检用：当前锚点（上一帧用的光标位置）。窗口写完把光标挤走时它必须跟着更新，
	/// 否则补帧定时器会把"被挤出来的位移"当成用户又拖了一下，一圈一圈往外推。
	/// </summary>
	internal Native.POINT AnchorForTest => _prevPoint;

	/// <summary>
	/// 自检用：伪造"按下那一下到了、抬起那一下丢了"的僵尸锁存位。
	/// <paramref name="stillDown"/> 是钩子自己记的状态；false 表示触发键其实早松了。
	/// </summary>
	internal void FakeTriggerForTest(bool latch, bool stillDown)
	{
		Interlocked.Exchange(ref _triggerHeld, latch ? 1 : 0);
		_triggerDownSeen = true;
		_triggerDown = stillDown;
		Interlocked.Exchange(ref _triggerEventTick, Environment.TickCount64);
	}

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
		HookProbe.W("sink.KB down=" + down + " vk=" + kb.vkCode + " flags=0x" + kb.flags.ToString("X"));
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
		int vk = (int)kb.vkCode;
		bool triggerHit = KeysUtil.MatchesKeyboard(cfg.TriggerKey, kb);
		HookProbe.W("HandleKey down=" + down + " vk=" + kb.vkCode + " trigger=" + cfg.TriggerKey + " hit=" + triggerHit + " alt=" + _alt + " pending=" + _triggerPending + " activated=" + _activated + " action=" + _action);
		if (triggerHit)
		{
			// 触发键的权威状态只在这里更新：钩子线程收到什么就是什么。
			// 自动重复会一遍遍发 down，正好用它刷新「还按着」的时间戳。
			bool wasDown = _triggerDown;
			_triggerDown = down;
			_triggerDownSeen = true;
			Interlocked.Exchange(ref _triggerEventTick, Environment.TickCount64);
			if (down)
			{
				// v1.7.0「吞掉触发键」：设置里打开后，触发键的这一下不再送给前台窗口。
				// 出厂仍是关：Enter 当触发键时用户首先要能打字，吞键是给「不想让前台看到我按了它」的人用的。
				if (!wasDown)
				{
					_triggerSwallowed = cfg.SwallowTrigger;
					_triggerReplayed = false;
					_triggerDragSeen = false;
				}
				// v1.6.1：触发键默认即时透传 —— 不吞、不补发（SwallowTrigger=false 时就是老行为）。
				//
				// v1.6.0 是「按下先吞住，够 TriggerHoldMs 才算按住，不够就在抬起时补发一个回车」。
				// 用户感受就是「按 Enter 要愣一下才出来」；补发再被 UIPI（前台是管理员窗口）
				// 或者安全软件挡一下，这一下回车就彻底没了 —— 于是「Enter 功能失效」。
				// 触发键出厂就是 Enter，它本身得能随时打字：这里一律 return false，让系统照常
				// 把键送到前台；「按住」靠物理键状态（TriggerStillHeld）判断，一样能起手。
				if (_activated && !_alt && _action != Actions.CodeNone)
				{
					_alt = true;
				}
				if (_activated && _alt)
				{
					/* hooks.c:1015-1017 这里是 return 1：**拖动期间触发键的自动重复要吞掉**。
					 *
					 * 移植时改成了 return false（想让触发键「随时能打字」），代价是：按住触发键拖窗口的
					 * 那一两秒里，键盘每 30ms 发一次自动重复，全部原样送给前台窗口。触发键出厂是 Enter，
					 * 前台就收到一串回车 —— 在对话框里是「点了确定」，在编辑器里是一堆空行，在游戏里是乱开界面。
					 *
					 * 第一下 keydown 仍然照常透传（见下面 return false 那段注释），被吞的只有自动重复。
					 * 键码相同的 L/R 变体也算同一个键 —— AltDrag 的 Hotkeys 表里同样不分左右。 */
					return true;
				}
				// v1.6.0 设备范围：只认勾中的那几台键盘发出的触发键（配置留空 = 所有键盘）。
				//
				// v1.6.3：这里**不阻塞**。设备解析要等 Raw Input 慢半步到，同步等就要在
				// 键盘钩子回调里 Sleep 900ms（全系统键盘停摆）。改成：回调里只读上一次
				// 已经算好的结论，解析丢到线程池；200ms 后的长按定时器上再核对一次。
				if (!wasDown)
				{
					_triggerDeviceKnown = false;
					_triggerDeviceAllowed = true;
					KickDeviceResolve(cfg, (int)kb.vkCode);
				}
				if (_triggerDeviceKnown && !_triggerDeviceAllowed)
				{
					HookProbe.W("HandleKey：设备不在允许范围，放行。cfg.Device=" + cfg.Device);
					// 这一行是排障的关键：「设置里只勾了某一只键盘，用户换了一把按触发键」时，
					// 现象就是彻底没反应，而日志里必须能看出来原因。
					Log.Info("按住拖拽：这次触发键不是配置里勾选的键盘发出的（只认：" + cfg.Device + "），本次忽略");
					// 吞键模式下这一下按下已经被我们吃掉了，但这只键盘不在白名单里：它的触发键
					// 就该照常打字，所以补发一次把按键还回去，后面的自动重复也恢复透传。
					if (_triggerSwallowed)
					{
						ReplaySwallowedTap(vk);
						_triggerSwallowed = false;
					}
					return false;
				}
				// v1.6.0 长按阈值：先起个定时器，按够了才转正。
				if (cfg.TriggerHoldMs > 0)
				{
					// 键盘自动重复会一遍遍进来：已经在计时就别把阈值重新数一遍，
					// 否则按住不放永远转正不了（v1.6.0 的老 bug）。
					if (!_triggerPending)
					{
						BeginPendingTrigger(cfg, (int)kb.vkCode);
						HookProbe.W("HandleKey：开始长按计时 " + cfg.TriggerHoldMs + "ms");
					}
					return _triggerSwallowed;
				}
				// 阈值 0 = 按下即生效。但设备还没解析完就先别转正：否则「没勾的键盘」
				// 会在阈值 0 下先接管一次，再被长按定时器撤销，等于漏拦。
				// 解析结果回来的那一刻由 KickDeviceResolve 的回调补上一次转正。
				if (_triggerDeviceKnown)
				{
					ArmTrigger(cfg);
				}
				return _triggerSwallowed;
			}
			// 这次按压还没到阈值就松开了 = 一次普通按键。
			// 没开吞键时前台本来就已经收到，这里只收拾自己的状态；开了吞键则相反 ——
			// keydown 被我们吃了，抬起时必须补发一次「按下+抬起」，用户按 Enter 还是得到一个回车。
			if (_triggerPending)
			{
				bool swallowedPending = _triggerSwallowed;
				_triggerSwallowed = false;
				CancelPendingTrigger(replay: swallowedPending);
				return swallowedPending;
			}
			// 用户排障时最想知道的一件事：触发键到底有没有松开。这行和 BeginDrag 的"接管"结对出现，
			// 只看到"接管"、后面没有"已松开"，就说明 keyup 压根没送到钩子（钩子被系统摘了/被别的程序抢了）。
			Log.Info("按住拖拽：触发键已松开（action=" + _action + "）");
			{
				// 已经起拖（不管是真拖了窗口还是只按了鼠标键）就说明这一下是「按住」，
				// 抬起不能再补发 —— 否则拖完窗口前台白白收到一个回车。
				bool dragged = _triggerDragSeen || _action != Actions.CodeNone;
				bool swallowedUp = _triggerSwallowed;
				_triggerSwallowed = false;
				// hooks.c:1064-1082：还有别的触发键按着就不收尾；否则把"用户按着触发键"瞒过去，
				// 然后只把 alt 放下 —— 拖动本身要继续，直到鼠标键松开（原版就是这样）。
				if (_blockAltUp || _action != Actions.CodeNone)
				{
					AltDragCore.MasqueradeAsControl(_state);
					_ignoreCtrl = false;
				}
				_alt = false;
				Interlocked.Exchange(ref _triggerHeld, 0);
				if (_action == Actions.CodeNone)
				{
					// 触发键松了、手也没在拖：ResetDragState 顺手把鼠标钩子卸掉（hooks.c:1084-1086 同款）。
					ResetDragState();
				}
				// 吞键 + 这一下从头到尾没起拖（含阈值 0 的「按下即接管」）：补一个完整的按键。
				if (swallowedUp && !dragged)
				{
					ReplaySwallowedTap(vk);
				}
				return swallowedUp;
			}
		}

		// 修饰键的物理状态无条件记账：altdrag 里这两段都在 state.alt 闸门之外（hooks.c:958-968 / 1053-1059）。
		// 老实现把它们藏在 `if (!_alt) return false` 后面，于是踩了两个坑：
		//   1) 「先按住 Shift/Ctrl、再按触发键起拖」这条最常见的顺序，那一次 keydown 直接丢了；
		//   2) 低级键盘钩子送来的是 VK_LSHIFT/VK_LCONTROL 这类左右分开的键码（altdrag 用的就是它们），
		//      只写 case VK_SHIFT / case VK_CONTROL 永远进不去 —— Shift 拉满档、Ctrl 夹屏因此从没生效过。
		bool isShiftVk = vk == Native.VK_SHIFT || vk == Native.VK_LSHIFT || vk == Native.VK_RSHIFT;
		bool isCtrlVk = vk == Native.VK_CONTROL || vk == Native.VK_LCONTROL || vk == Native.VK_RCONTROL;
		if (isShiftVk)
		{
			_shiftPhys = down;
		}
		else if (isCtrlVk)
		{
			_ctrlPhys = down;
		}

		if (isShiftVk)
		{
			if (down)
			{
				// hooks.c:958-963：Shift 无条件拉满吸附档位（不看是不是在拖）。
				_snap = 3;
				if (!_shift)
				{
					_shift = true;
					// hooks.c:2837：记下按下 Shift 那一刻的光标点 —— 四向扩张时用它锁住 x 轴。
					if (_state != null)
					{
						_state.ShiftPoint = CursorOrZero();
						_state.LockX = true;
					}
					RequestFrame();
				}
				// hooks.c:965-968：拖动中按下 Shift 要吞掉，否则窗口会收到 Shift 去改输入法/选区。
				if (_alt && _action != Actions.CodeNone)
				{
					return true;
				}
			}
			else
			{
				// hooks.c:1088-1091：抬起复位档位并补一帧，否则窗口停在"拉满"那一帧上。
				_shift = false;
				_snap = cfg.AutoSnap;
				// hooks.c:1088-1091：抬起之后不再锁轴。
				if (_state != null)
				{
					_state.LockX = false;
				}
				RequestFrame();
			}
			return false;
		}

		if (vk == Native.VK_SPACE)
		{
			// hooks.c:970-975：空格临时关掉吸附（这次拖动剩下的时间都关），拖的时候连键一起吞掉。
			if (down && _snap != 0 && (_action != Actions.CodeNone || _activated))
			{
				_snap = 0;
				if (_action != Actions.CodeNone)
				{
					return true;
				}
			}
			return false;
		}

		if (vk == Native.VK_ESCAPE)
		{
			// hooks.c:976-980：Esc 直接放弃这次拖动，而且**不吞**这个键 —— 原版这里同样不看 state.alt。
			if (down && _action != Actions.CodeNone)
			{
				CancelDrag();
			}
			return false;
		}

		if (isCtrlVk)
		{
			// hooks.c:984-993 按下 / 1053-1059 抬起。
			HandleCtrl(cfg, down);
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
					// hooks.c:988 只写了一个 origin.maximized=0，可我们的自动最大化读的是"起手时那一份"，
					// 两个都得清，否则按住 Ctrl 拖过界照样会把窗口搬到新屏再最大化。
					_state.OriginMaximized = false;
					_state.OriginMaximizedAtStart = false;
					Native.POINT pt = CursorOrZero();
					_state.OriginMonitor = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
					// hooks.c:2892：按住 Ctrl 那一刻的光标点，四向扩张时用它锁住 y 轴。
					_state.CtrlPoint = pt;
					_state.LockY = true;
				}
				if (_hwnd != IntPtr.Zero)
				{
					Native.SetForegroundWindow(_hwnd);
				}
				RequestFrame();
				_ctrl = true;
			}
		}
		else
		{
			if (_action != Actions.CodeNone && !_ignoreCtrl)
			{
				RequestFrame();
			}
			if (_state != null)
			{
				_state.LockY = false;
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
		// 钩子还活着的最直接证据：事件在来。「up 丢了」的体检（HealthCheckLostUp）拿它当前提，
		// 否则注入输入 / 触控板 / 远程桌面下会把正常拖动当成「up 丢了」掐掉。
		Interlocked.Exchange(ref _lastMouseTick, Environment.TickCount64);
		switch (msg)
		{
		case Native.WM_MOUSEMOVE:
			return OnMove(appConfig, pt, ms.time);
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
				// hooks.c:1487-1489：手上还有活儿的时候又按鼠标键 —— 吞掉。原版注释说得很直白：
				// Block mousedown so AltDrag.exe does not remove cursorwnd.
				if (down && _action != Actions.CodeNone)
				{
					return true;
				}
				if (down)
				{
					_dragButton = button;
					_lostUpArmed = ButtonStillDown(button);
				}
				return down ? BeginDrag(cfg, num, pt) : EndDrag(cfg, num);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
				return false;
			}
		}
	}

	/// <summary>
	/// hooks.c:5476-5514 的 WM_MOUSEMOVE 全序，一行一行搬：
	///   1. SamePt 去重（同坐标的事件不记锚点、不抽帧）；
	///   2. DRAG_WAIT 死区：还在 DragOutThresholdPx 里就整帧让过，出去了先把死区里攒下的
	///      那个锚点补一帧（窗口立刻到位，不会先愣一下再跳）；
	///   3. state.prevpt = pt（锚点只在让过的事件上更新）；
	///   4. 按 conf.RezTimer 抽帧：0 = 数事件（默认）、1 = 只按事件时间、3 = 两者叠加；
	///   5. 挪过就不算双击。
	/// </summary>
	private bool OnMove(AppConfig cfg, Native.POINT pt, uint eventTime)
	{
		bool dragging = _action == CodeMove || _action == CodeResize;
		// hooks.c:5471-5474 SamePt：同一坐标被重复送来（系统重复、触控板抖动）原版直接让过 ——
		// 不记锚点、不抽帧，否则抽帧计数会被白吃一帧，手慢的时候看着就像漏帧。
		if (dragging && pt.X == _prevPoint.X && pt.Y == _prevPoint.Y)
		{
			return false;
		}
		// hooks.c:5479-5486：DRAG_WAIT 死区。
		if (_dragWait)
		{
			if (StillInsideDragOut(cfg, _prevPoint, pt))
			{
				return false;
			}
			_dragWait = false;
			try
			{
				lock (_gate)
				{
					// 先把死区里攒下的锚点补一帧（原版 MouseMove(state.prevpt)）。
					MoveMouse(cfg, _prevPoint);
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
		}
		// hooks.c:5488 —— 锚点只在真正让过的事件上更新（补帧定时器靠它判断钩子漏没漏事件）。
		_prevPoint = pt;
		if (dragging)
		{
			bool write = false;
			if (cfg.RezTimer == 1)
			{
				// hooks.c:5503-5506：只看事件时间，同一时间戳的重复事件不写。
				if (eventTime != _lastEventTime)
				{
					_lastEventTime = eventTime;
					write = true;
				}
			}
			else
			{
				// hooks.c:5508-5511：事件计数抽帧（原版默认），RezTimer==3 再叠一层时间变化。
				int rate = ((_action == CodeMove) ? cfg.MoveRate : cfg.ResizeRate);
				if (rate < 1)
				{
					rate = 1;
				}
				_updaterate = (_updaterate + 1) % rate;
				write = _updaterate == 0;
				if (!write && cfg.RezTimer == 3 && eventTime != _lastEventTime)
				{
					write = true;
				}
				_lastEventTime = eventTime;
			}
			if (write)
			{
				try
				{
					// 和 MOVE_TIMER 互斥：两条线程同时写窗口会把两帧交错的中间态留给目标窗口。
					lock (_gate)
					{
						MoveMouse(cfg, pt);
					}
				}
				catch (Exception ex)
				{
					Log.Error(ex);
				}
			}
		}
		// hooks.c:1129-1134：手挪过一点，之前那一下就不算双击了（WM_MOUSEMOVE 有时没真动也会来）。
		if (pt.X != _clickPoint.X || pt.Y != _clickPoint.Y)
		{
			_clickTick = 0L;
		}
		return false;
	}

	/// <summary>
	/// hooks.c:454-466 的 IsPtDragOut：**还在** DragOutThresholdPx 这个方框里吗。
	/// 原版拿 conf.DragOutThresholdPx 同时当 x/y 两个方向的阈（Tx = Ty）。
	/// </summary>
	private static bool StillInsideDragOut(AppConfig cfg, Native.POINT from, Native.POINT to)
	{
		int limit = Math.Max(0, cfg.DragOutThresholdPx);
		return Math.Abs(to.X - from.X) <= limit && Math.Abs(to.Y - from.Y) <= limit;
	}

	/// <summary>hooks.c:1491-1877：按下鼠标键那一刻的全部判断与起手。</summary>
	private bool BeginDrag(AppConfig cfg, int action, Native.POINT pt)
	{
		HookProbe.W("BeginDrag action=" + action + " pt=" + pt.X + "," + pt.Y + " alt=" + _alt + " pending=" + _triggerPending + " action0=" + _action);
		if (_action != Actions.CodeNone)
		{
			return false;
		}
		// v1.7.0：这一次按压确实在拖窗口 —— 吞键模式下 keyup 就不能再补发按键了。
		_triggerDragSeen = true;
		if (!_alt && _triggerPending)
		{
			// v1.6.0：长按阈值已经过了、定时器还没跑到 —— 这一下点击算数，先转正再往下走。
			ArmTrigger(cfg);
		}
		if (!_alt)
		{
			/* v1.6.1：物理上按着触发键，就当场转正 —— 这是最后一道兜底。
			 *
			 * 为什么不能只信长按定时器：它是 Timer 回调，应用忙、系统降频、机器刚休眠，
			 * 回调都可能晚到甚至丢；而 _alt 一旦没置上，这一整次按住就彻底白按 ——
			 * 表现正是「按住 Enter 点左键、右键都没反应」。按下鼠标这一刻是最后的机会，
			 * 直接问物理键状态：按着就起手，不管前面那条链走到哪一步。
			 * 设备范围仍然要过一遍，否则"只认外接键盘"时笔记本键盘按的键会漏进来。 */
			bool deviceOk = !_triggerDeviceKnown || _triggerDeviceAllowed;
			if (!TriggerStillHeld(cfg) || !deviceOk)
			{
				HookProbe.W("BeginDrag：触发键没按着（stillHeld=" + TriggerStillHeld(cfg) + " deviceKnown=" + _triggerDeviceKnown + " deviceOk=" + deviceOk + "）—— 放弃");
				return false;
			}
			ArmTrigger(cfg);
		}
		// hooks.c:1491-1508：按下鼠标前复查触发键还按着没有。钩子偶尔会漏掉抬起（别的程序抢了焦点，
		// 或者键被系统截走），不查就会"手早松了还在拖着窗口"。查出来已经不在了就把状态收干净、放行这一下。
		if (!_activated && !TriggerStillHeld(cfg))
		{
			HookProbe.W("BeginDrag：复查触发键失败（TriggerStillHeld=false）—— 放弃");
			_alt = false;
			Interlocked.Exchange(ref _triggerHeld, 0);
			_blockAltUp = false;
			_blockMouseUp = false;
			return false;
		}
		nint num = Native.WindowFromPoint(pt);
		HookProbe.W("BeginDrag：WindowFromPoint(" + pt.X + "," + pt.Y + ")=0x" + num.ToString("X"));
		if (num == IntPtr.Zero)
		{
			HookProbe.W("BeginDrag：WindowFromPoint 返回 0");
			return false;
		}
		// hooks.c:1500-1510：Tooltip 窗口会盖住真正的目标，先藏掉再重取。
		if (string.Equals(AppRules.ClassNameOf(num), TooltipsClass, StringComparison.OrdinalIgnoreCase))
		{
			Native.ShowWindow(num, Native.SW_HIDE);
			num = Native.WindowFromPoint(pt);
		}
		nint num2 = Native.GetAncestor(num, Native.GA_ROOT);
		HookProbe.W("BeginDrag：root=0x" + num2.ToString("X") + " class=" + AppRules.ClassNameOf(num2) + " title=" + AppRules.TitleOf(num2));
		if (num2 == IntPtr.Zero)
		{
			HookProbe.W("BeginDrag：GetAncestor 返回 0");
			return false;
		}
		long windowLong = Native.GetWindowLong(num2, Native.GWL_STYLE);
		// 一处有意偏离 hooks.c:1520-1545：原版只认黑名单，而我们的进程里有设置窗、拖拽提示、
		// 各种 HUD/浮层 —— 它们铺在屏幕上，按住触发键想拖别的窗口时极易先把自家窗口拖走（实测发生：
		// 主窗口被拖到 -426,0，界面全乱）。自家窗口永远不是拖拽目标。
		uint ownPid;
		Native.GetWindowThreadProcessId(num2, out ownPid);
		if (ownPid == (uint)Environment.ProcessId && !DragOwnWindowForTest)
		{
			HookProbe.W("BeginDrag：是本进程自己的窗口，跳过 hwnd=0x" + num2.ToString("X"));
			return false;
		}
		// 黑名单（进程名 / 窗口标题 | 窗口类名），语义与 hooks.c 的 blacklisted 一致。
		if (AppRules.IsBlacklisted(cfg, num2))
		{
			HookProbe.W("BeginDrag：窗口在黑名单里，跳过 hwnd=0x" + num2.ToString("X"));
			return false;
		}
		Native.WINDOWPLACEMENT lpwndpl = new Native.WINDOWPLACEMENT
		{
			length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT))
		};
		if (!Native.GetWindowPlacement(num2, ref lpwndpl))
		{
			HookProbe.W("BeginDrag：GetWindowPlacement 失败");
			return false;
		}
		if (!Native.GetWindowRect(num2, out var wnd))
		{
			HookProbe.W("BeginDrag：GetWindowRect 失败");
			return false;
		}
		// hooks.c:1560-1570：没有标题栏、又正好铺满整块屏幕的（全屏游戏/播放器）不接管。
		if ((windowLong & Native.WS_CAPTION) != Native.WS_CAPTION)
		{
			Native.RECT rcMonitor = Native.GetMonitorInfoAt(pt).rcMonitor;
			if (wnd.Left == rcMonitor.Left && wnd.Top == rcMonitor.Top && wnd.Right == rcMonitor.Right && wnd.Bottom == rcMonitor.Bottom)
			{
				HookProbe.W("BeginDrag：无标题栏且铺满显示器 —— 当作全屏程序，不接管。rect=" + wnd + " monitor=" + rcMonitor);
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
		HookProbe.W("BeginDrag：接管 hwnd=0x" + num2.ToString("X") + " action=" + action + " rect=" + wnd.Left + "," + wnd.Top + " " + wnd.Width + "x" + wnd.Height);
		Log.Info("按住拖拽：接管 hwnd=0x" + num2.ToString("X") + "（" + AppRules.ProcessNameOf(num2) + "）action=" + action + " rect=" + wnd.Left + "," + wnd.Top + " " + wnd.Width + "x" + wnd.Height);
		_action = action;
		if (_snap == 0)
		{
			_snap = cfg.AutoSnap;
		}
		_activated = true;
		_blockAltUp = true;
		_hwnd = num2;
		_prevPoint = pt;
		_dragWait = false;
		_lastEventTime = 0u;
		_state = new AltState();
		AltDragCore.RecordOrigin(_state, num2, wnd);
		// SetupMove / SetupResize 会把这个标记改掉，先留一份：AltDrag 只在"最大化窗口被还原"
		// 的移动分支里才在按下的那一刻补一帧。
		bool wasMaximized = _state.OriginMaximized;
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
			// hooks.c:4028-4036：抓到正中间时按 ResizeCenter 决定干什么（0=固定右下、2=转移动、3=按最近边）。
			if (AltDragCore.ApplyResizeCenter(_state, wnd, pt, cfg, out bool actionMove) && actionMove)
			{
				action = CodeMove;
				_action = CodeMove;
				// 转成移动就得按移动的方式重记锚点：九宫格那套 offset（中间带记的是绝对坐标）用在移动上会把窗口甩到屏幕角。
				_state.OffsetX = pt.X - wnd.Left;
				_state.OffsetY = pt.Y - wnd.Top;
				_state.EdgeX = DragEdge.Center;
				_state.EdgeY = DragEdgeY.Center;
			}
		}
		// hooks.c:1661-1672：只有"原本最大化、起拖时被还原"的移动分支才会在按下的那一刻立刻跑
		// 一次 MouseMove，把窗口贴到光标下面。其余分支（包括缩放）第一帧要等真正的鼠标移动 ——
		// 原样照抄，不额外补帧。
		if (action == CodeMove && wasMaximized)
		{
			// 原本最大化、起手被还原的那一帧要立刻贴到光标下面，等不到下一次鼠标移动 —— 主动要一帧。
			_pendingPoint = pt;
			RequestFrame();
		}
		// hooks.c:1841-1847：告诉目标窗口"开始拖了"（iTunes 除外），并按 MoveRate/ResizeRate 起补帧定时器。
		if (!flag)
		{
			Native.SendMessageTimeoutW(num2, Native.WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, SendTimeoutMs, out var _);
		}
		_lostUpTicks = 0;
		_lostUpArmed = ButtonStillDown(_dragButton);
		// hooks.c:1850：抽帧计数归零，然后起 MOVE_TIMER。
		//
		// 体检（up 丢了没有）挂在同一拍上。注意 GetAsyncKeyState 在从没处理过输入的线程上
		// 会稳定读回 0，所以体检自带"起手读到按下才武装"的闸（见 HealthCheckLostUp），
		// 不会把正常拖动掐掉。
		_updaterate = 0;
		// hooks.c:3768 / 4040：起手这一刻该不该先「按住不动」（DRAG_WAIT）。
		_dragWait = ShouldDragWait(cfg, action, num2);
		StartMoveTimer();
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
			HookProbe.W("EndDrag 被 _blockMouseUp 吞掉 action=" + action + " 当前=" + _action);
			return true;
		}
		if (_action == Actions.CodeNone || _action != action)
		{
			HookProbe.W("EndDrag 状态不符 action=" + action + " 当前=" + _action);
			return false;
		}
		int num = _action;
		// 松手这一下补最后一帧：MoveRate/ResizeRate 抽帧时，最后一个移动事件可能还没写窗口，
		// 终点就会停在半路（实测 e2e 差 15px，用户看到的是「松手了窗口还差一截」）。
		// 原版收尾前也是这个思路（hooks.c:1879-1908 走完 WM_EXITSIZEMOVE 才解钩子）。
		// 自检里光标是测试进程的真实位置（不是事件里的点），别拿它补帧，否则几何断言会被搅乱。
		if (!InlineWriteForTest && TryCursor(out var releasePoint) && (releasePoint.X != _prevPoint.X || releasePoint.Y != _prevPoint.Y))
		{
			_prevPoint = releasePoint;
			try
			{
				MoveMouse(cfg, releasePoint, last: true);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
		}
		StopTimers();
		Log.Info("按住拖拽：拖动结束（action=" + num + "）");
		_action = Actions.CodeNone;
		_activated = false;
		if (num == CodeMove || num == CodeResize)
		{
			PostExitSizeMove(_hwnd);
		}
		if (!_alt)
		{
			ResetDragState();
		}
		return true;
	}

	/// <summary>
	/// 拖完要通知窗口「这次拖动结束了」（WM_EXITSIZEMOVE），但发消息是同步的：
	/// 慢窗口（kelivo、Electron/Flutter 一类）当时可能正卡在自己那 150~200ms 的 WM_SIZE 里，
	/// SendMessageTimeout 只能等到超时 —— 实测右键松手那一下被堵了 215ms，而低级钩子的
	/// 超时门槛是 300ms，再慢一点系统就直接把钩子摘掉（用户看到的就是「松手也不停、只能按 Esc」）。
	/// 所以这里丢线程池：通知照发，钩子回调立刻返回。
	/// </summary>
	private void PostExitSizeMove(IntPtr hwnd)
	{
		if (hwnd == IntPtr.Zero || IsITunes(hwnd))
		{
			return;
		}
		ThreadPool.QueueUserWorkItem(_ =>
		{
			try
			{
				Native.SendMessageTimeoutW(hwnd, Native.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, SendTimeoutMs, out var _);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
		});
	}

	/// <summary>
	/// hooks.c:2206-2392 的 MouseMoveNow：一帧几何。
	///
	/// 几何基准是 AltState.Track —— 上一帧自己写下的目标矩形，而不是每帧回读的 GetWindowRect。
	/// hooks.c:2216 那句 static RECT wnd 就是「跟手」的根：重窗口的 WM_SIZE 还没跑完时
	/// GetWindowRect 会读到滞后值，照它算下一帧就是越拖越偏；目标矩形是自洽的，写多少就是多少。
	/// 位置优先现取 GetCursorPos，拿不到才用事件里的坐标兜底（补帧定时器线程上没有事件坐标）。
	/// last = 收尾那一帧（EndDrag 补的最后一帧），走 hooks.c:1472 的 Sleep(8) 与不吸附路径。
	/// </summary>
	private void MoveMouse(AppConfig cfg, Native.POINT fallback, bool last = false)
	{
		// hooks.c:389 的 Enum() 每次都用当前设置里的 Snaplist 判断谁能参与吸附。
		SnapEngine.SnapList = cfg.SnapList;
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
		// hooks.c:2211-2214：别的程序（鼠标捕获、全屏游戏）clip 住光标时，先按 clip 矩形夹一下，
		// 否则算出来的位置会跑到屏幕外，写窗口时被系统拽回来 —— 看着就是「拖到边上卡一下」。
		if (Native.GetClipCursor(out var clipRect) && clipRect.Right > clipRect.Left && clipRect.Bottom > clipRect.Top)
		{
			pt.X = Math.Clamp(pt.X, clipRect.Left, clipRect.Right - 1);
			pt.Y = Math.Clamp(pt.Y, clipRect.Top, clipRect.Bottom - 1);
		}
		// hooks.c:651-657：按住 Ctrl 时把光标夹在起手那块显示器里。
		// 这里在补帧定时器（线程池线程）上也可能跑，GetAsyncKeyState 在那条线程上会读回 0 —— 用钩子记的状态。
		if (!_ctrlIsTrigger && !_ignoreCtrl && altState.OriginMonitor != IntPtr.Zero && _ctrlPhys)
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
		// hooks.c:2216-2228：整个拖动只在这里读一次真实矩形（首帧），之后每帧都用上一帧写下去的目标矩形。
		if (!altState.TrackValid)
		{
			if (!Native.GetWindowRect(_hwnd, out var firstRect))
			{
				return;
			}
			altState.Track = firstRect;
			altState.TrackValid = true;
		}
		Native.RECT wnd = altState.Track;
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
				WriteWindow(posx, posy, wndwidth, wndheight);
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
			// hooks.c:880-918：纯九宫格几何（选边在 SetupResize，收养在 AdoptFollowAxis）。原来是"中间带那条轴先锁死、拖起来再收养"，
			// 那一版之所以失灵，是因为收养时没重算 Offset；现在收养走 AltDragCore.AdoptFollowAxis（会重算）。
			// m05425：边带抓点也让它两轴一起动 —— 斜着拖够阈值就把中间带那一轴收养到对应的边。
			// 收养必须发生在 ResizeGeometry 之前（几何只读 Edge/Offset，不改它们），收养成功就换对角光标。
			if (AltDragCore.AdoptFollowAxis(altState, pt, wnd, cfg))
			{
				AltDragCore.SetResizeCursor(altState.EdgeX, altState.EdgeY);
			}
			AltDragCore.ResizeGeometry(altState, pt, wnd, ref posx, ref posy, ref wndwidth, ref wndheight);
			if (_snap > 0 && num > 0)
			{
				SnapEngine.Resize(_snap, num, _hwnd, altState.EdgeX, altState.EdgeY, ref posx, ref posy, ref wndwidth, ref wndheight);
			}
		}
		// hooks.c:927：算完就写 —— 不挑帧、不比较几何、不交给别的线程。
		// hooks.c:2370-2382：写之前把目标矩形记进 Track，下一帧就从这个目标继续算。
		WriteWindow(posx, posy, wndwidth, wndheight, last);
	}

	/// <summary>
	/// hooks.c:927 的等价物：一帧算完就写，同步。
	///
	/// 原版就一句 `MoveWindow(hwnd, posx, posy, wndwidth, wndheight, TRUE)` —— 不比较几何、
	/// 不异步投递、不交给别的线程。照抄这一句是"跟手"的全部来源：光标事件的回调还没返回，
	/// 窗口已经在它该在的位置上，用户眼睛里没有延迟。
	///
	/// 上一版为了绕开"重窗口 WM_SIZE 要一百多毫秒"，改成 SWP_ASYNCWINDOWPOS + 一条写入线程，
	/// 代价是窗口永远落后钩子一到两帧 —— 这就是"不跟手"的根；顺带养出重复帧堆积、
	/// 松手之后窗口还在动。重窗口的负担原版是用 MoveRate/ResizeRate 抽帧解决的，这里保持一致。
	///
	/// 唯一保留的增量：写完把光标读回来当锚点。窗口的边从光标身上挪过去时系统会重新定位光标，
	/// 那不是用户动的；不修锚点，补帧定时器会把它当成新输入 —— 10Hz 自激，窗口越推越大。
	/// </summary>
	private void WriteWindow(int x, int y, int w, int h, bool last = false)
	{
		if (w < 1 || h < 1)
		{
			return;
		}
		AltState? alt = _state;
		AppConfig? cfg = _config();
		// hooks.c:1508-1567：尺寸没变就是纯移动（走 MOVETHICKBORDERS / MOVEASYNC 那条更便宜的路）。
		bool moveOnly = alt != null && alt.TrackValid && w == alt.Track.Width && h == alt.Track.Height;
		if (last)
		{
			// hooks.c:1472：收尾那一帧原版先 Sleep(8)，让目标窗口把上一轮的 WM_SIZE 处理完再写最后一下。
			Thread.Sleep(8);
		}
		else if (cfg != null && cfg.KickBack && alt != null)
		{
			// hooks.c:1410-1465：先把这个矩形塞进 WM_SIZING，让按字符格对齐的窗口（终端、console）
			// 回一个对齐过的矩形。不然每帧都在和它自己的对齐打架，看着就是抖。
			Native.RECT rc = new Native.RECT(x, y, x + w, y + h);
			if (AltDragCore.LetWindowKickBack(_hwnd, alt, ref rc, moveOnly))
			{
				x = rc.Left;
				y = rc.Top;
				w = rc.Width;
				h = rc.Height;
			}
		}
		long started = Stopwatch.GetTimestamp();
		bool moved;
		if (cfg != null && cfg.AsyncWindowPos)
		{
			// hooks.c:1508-1567 的 MOVEASYNC：整块投递给目标窗口，写完再补一次 WM_SYNCPAINT。
			uint flags = moveOnly ? Native.MOVEASYNC : (Native.RESIZEFLAGS | Native.SWP_ASYNCWINDOWPOS);
			moved = Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, flags);
			Native.SendMessageTimeoutW(_hwnd, Native.WM_SYNCPAINT, IntPtr.Zero, IntPtr.Zero, 0u, 120u, out var _);
		}
		else
		{
			moved = Native.MoveWindow(_hwnd, x, y, w, h, bRepaint: true);
		}
		long costMs = (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;
		if (costMs >= SlowWriteMs && Interlocked.Increment(ref _slowWrites) <= 5)
		{
			Log.Info("按住拖拽：写窗口这一帧花了 " + costMs + "ms（目标窗口自己的消息处理慢，" + x + "," + y + " " + w + "x" + h + "）");
		}
		HookProbe.W("WriteWindow " + x + "," + y + " " + w + "x" + h + " ok=" + moved + " cost=" + costMs + "ms hwnd=0x" + _hwnd.ToString("X"));
		if (moved)
		{
			if (alt != null)
			{
				// 写成功了才更新基准：这一帧的目标矩形就是下一帧的起点。
				alt.Track = new Native.RECT(x, y, x + w, y + h);
				alt.TrackValid = true;
			}
			Interlocked.Increment(ref _frames);
			// 自激环的断点：我们写窗口这件事本身会把光标挤走（边从光标身上挪过去 / 目标窗口拿着鼠标捕获时
			// 系统会重新定位光标）。那个位移会在下一拍被补帧定时器当成"用户又动了"，于是再写一帧、再挤
			// 一次 —— 正好 10Hz，窗口越推越大（实测 19.7 秒里 2562 宽被推到 4662），松手也不停。
			// 写完立刻把光标读回来当新锚点：补帧那边下一次比对就是"没动"，环自己断掉。
			if (TryCursor(out var after) && (after.X != _prevPoint.X || after.Y != _prevPoint.Y))
			{
				_prevPoint = after;
				HookProbe.W("WriteWindow 写窗口把光标挤到了 " + after.X + "," + after.Y + "，锚点跟着走");
			}
		}
	}


	// ---------------- 补帧定时器（hooks.c:1999-2008 的 MOVE_TIMER）+「up 丢了」体检 ----------------

	/// <summary>
	/// 自检专用：只是个"这次拖动是自检发起的"标记，用来让 EndDrag 跳过松手补帧
	/// （自检里光标是测试进程的真实位置，不是事件里的点）。写入路径已经全同步，和它无关。
	/// </summary>
	internal static bool InlineWriteForTest { get; set; }

	/// <summary>
	/// 要一帧：拿实时光标，当场上锁算一遍、同步写一次。
	///
	/// 原版没有"请求帧"这一层 —— Shift/Ctrl 变化、还原定时器、最大化起手这些地方都是直接调
	/// MouseMove()（hooks.c:962/991/1482/1671/1996）。这里照抄：不排队、不节流、不跨线程。
	/// </summary>
	private void RequestFrame()
	{
		AppConfig? appConfig = _config();
		if (appConfig == null || _state == null || _hwnd == IntPtr.Zero)
		{
			return;
		}
		Native.POINT pt = TryCursor(out var live) ? live : _pendingPoint;
		// Monitor 可重入：钩子线程本来就拿着 _gate 的调用点（BeginDrag）不会自己堵自己。
		if (!Monitor.TryEnter(_gate))
		{
			return;
		}
		try
		{
			MoveMouse(appConfig, pt);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		finally
		{
			Monitor.Exit(_gate);
		}
	}

	/// <summary>
	/// hooks.c:1853 的 `SetTimer(g_hwnd, MOVE_TIMER, 100, NULL)`。
	/// 原版只在 MoveRate/ResizeRate > 1 时才起；这里常驻，因为「up 丢了」的体检也挂在同一拍上 ——
	/// 而它并不会多出帧：光标没动就一帧都不写（见 OnMoveTimer）。
	/// </summary>
	private void StartMoveTimer()
	{
		if (_moveTimer == null)
		{
			_moveTimer = new Timer(OnMoveTimer, null, -1, -1);
		}
		_moveTimer.Change(MoveTimerMs, MoveTimerMs);
	}

	/// <summary>
	/// hooks.c:2003-2007：`GetCursorPos(&pt); if (state.prevpt.x != pt.x || state.prevpt.y != pt.y) MouseMove();`
	/// —— 光标没动就一帧都不写（"空闲不出帧"靠的就是这一条，自检里松手后帧数不涨也靠它）。
	///
	/// 原版的定时器跑在 g_hwnd 的消息循环上，与钩子天然串行；这里是线程池线程，
	/// 所以只抢一次不阻塞的锁：钩子正在写就跳过这一拍，绝不反过来把钩子堵住。
	/// </summary>
	private void OnMoveTimer(object? state)
	{
		AppConfig? appConfig = _config();
		if (appConfig == null)
		{
			return;
		}
		if (!Monitor.TryEnter(_gate))
		{
			return;
		}
		try
		{
			if (_action != CodeMove && _action != CodeResize)
			{
				StopMoveTimer();
				return;
			}
			if (!HealthCheckLostUp())
			{
				return;
			}
			if (!TryCursor(out var pt) || (pt.X == _prevPoint.X && pt.Y == _prevPoint.Y))
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
		finally
		{
			Monitor.Exit(_gate);
		}
	}

	/// <summary>
	/// 体检：鼠标键还按着吗？（注入输入 / 触控板 / 远程桌面偶尔会吞掉 up）
	///
	/// 只在"拖起来那一刻读到的键确实是按下"时才武装 —— 没处理过输入的线程上读键盘会稳定返回 0，
	/// 那种环境（自检、脚本注入）体检自己闭嘴，不能反过来把正常拖动掐掉。
	/// 另外要求钩子安静 LostUpSilenceMs、连中 3 拍才判定丢 up。
	/// </summary>
	/// <returns>true = 还在拖（或已经放弃体检）；false = 判定丢了 up，已经收尾。</returns>
	private bool HealthCheckLostUp()
	{
		bool stillDown = ButtonStillDown(_dragButton);
		if (!_lostUpArmed)
		{
			_lostUpArmed = stillDown;
			return true;
		}
		if (stillDown)
		{
			_lostUpTicks = 0;
			return true;
		}
		long quietMs = Environment.TickCount64 - Interlocked.Read(ref _lastMouseTick);
		if (quietMs < LostUpSilenceMs)
		{
			_lostUpTicks = 0;
			return true;
		}
		if (++_lostUpTicks < 3)
		{
			return true;
		}
		AbortLostMouseUp();
		return false;
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
				// 还原之后立刻补一帧，把窗口贴到光标下面（hooks.c:1482 同一处理）。
				_pendingPoint = pt;
				RequestFrame();
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
		}
	}

	private void StopMoveTimer()
	{
		// 只停拍、不 Dispose：这个回调可能正跑在计时器自己的线程上。
		_moveTimer?.Change(-1, -1);
	}

	private void StopTimers()
	{
		StopMoveTimer();
		_restoreTimer?.Change(-1, -1);
	}

	// ---------------- 状态收尾 ----------------

	/// <summary>钩子被重装之后调用：缝隙里丢掉的 up 事件不再来，把状态清干净。</summary>
	internal void Rearm()
	{
		lock (_gate)
		{
			// 钩子重装过，这一次按压的 keyup 已经不保证还会来 —— 吞键记账一起清掉，
			// 免得后面某个无关的 keyup 被当成「该补发的短按」。
			CancelPendingTrigger(replay: false);
			_triggerSwallowed = false;
			_triggerReplayed = false;
			_alt = false;
			_triggerDownSeen = false;
			_triggerDown = false;
			Interlocked.Exchange(ref _triggerEventTick, 0L);
			Interlocked.Exchange(ref _triggerHeld, 0);
			StopTimers();
			ResetDragState();
		}
	}

	/// <summary>这个鼠标键现在还按着吗。自检可以经 ButtonProbeForTest 假造。</summary>
	private static bool ButtonStillDown(MouseButton button)
	{
		Func<MouseButton, bool>? probe = ButtonProbeForTest;
		if (probe != null)
		{
			return probe(button);
		}
		int vk = button switch
		{
			MouseButton.Left => Native.VK_LBUTTON,
			MouseButton.Middle => Native.VK_MBUTTON,
			MouseButton.Right => Native.VK_RBUTTON,
			MouseButton.X1 => 5,
			_ => 6,
		};
		return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
	}

	/// <summary>
	/// 鼠标键早就松了，那次 up 却没送到（钩子被系统摘掉 / 被安全软件拦掉）—— 自己把这次拖动收干净。
	/// 鼠标键早就松了，那次 up 却没送到（钩子被系统摘掉 / 被安全软件拦掉）—— 自己把这次拖动收干净。
	/// 不收的话几何会一直算下去，用户看到的是"手撒开了窗口还在抖"，只有 Esc 能停。
	/// </summary>
	private void AbortLostMouseUp()
	{
		int num = _action;
		StopTimers();
		HookProbe.W("没收到 up 但键已经松开 —— 自动收尾 action=" + num);
		Log.Info("按住拖拽：鼠标键已经松开，但没收到松开事件（钩子被系统摘过），自动结束这次拖动（action=" + num + "）");
		_action = Actions.CodeNone;
		_activated = false;
		_lostUpTicks = 0;
		_lostUpArmed = false;
		if ((num == CodeMove || num == CodeResize) && _hwnd != IntPtr.Zero && Native.IsWindow(_hwnd))
		{
			PostExitSizeMove(_hwnd);
		}
		_blockAltUp = false;
		ResetDragState();
	}

	/// <summary>Esc：放弃这次拖动（hooks.c:976-980 只清 action，不发 EXITSIZEMOVE）。</summary>
	private void CancelDrag()
	{
		Log.Info("按住拖拽：Esc 取消这次拖动（action=" + _action + "）");
		_action = Actions.CodeNone;
		_activated = false;
		StopTimers();
		// hooks.c:977-978：Esc 里紧跟着就是 UnhookMouse()，这次拖动不再接管鼠标。
		WantMouseHook(false);
	}

	/// <summary>
	/// hooks.c:3768 / 4040 的 DragThreshold 判定：
	/// 移动侧 DragThreshold &gt;= 2，或 == 1 且窗口本来就贴着边/最大化；缩放侧 &gt;= 3。
	/// 命中就先挂 DRAG_WAIT —— 光标离开按下点超过 DragOutThresholdPx 才真正开始动窗口。
	/// 默认 DragThreshold=1，只对「本来就贴着屏幕边 / 最大化」的窗口生效，所以日常没有延迟感。
	/// </summary>
	private bool ShouldDragWait(AppConfig cfg, int action, nint hwnd)
	{
		if (action == CodeMove)
		{
			return cfg.DragThreshold >= 2 || (cfg.DragThreshold == 1 && IsAnySnapped(hwnd));
		}
		if (action == CodeResize)
		{
			return cfg.DragThreshold >= 3;
		}
		return false;
	}

	/// <summary>hooks.c:1462 的 IsAnySnapped：最大化，或者已经贴着某条工作区边（Aero 半屏 / 贴边）。</summary>
	private static bool IsAnySnapped(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return false;
		}
		if (Native.IsZoomed(hwnd))
		{
			return true;
		}
		if (!Native.GetWindowRect(hwnd, out var wnd))
		{
			return false;
		}
		Native.RECT rcWork = Native.GetMonitorInfoAt(new Native.POINT((wnd.Left + wnd.Right) / 2, (wnd.Top + wnd.Bottom) / 2)).rcWork;
		if (rcWork.Width < 1 || rcWork.Height < 1)
		{
			return false;
		}
		// 原版用 GetWindowPlacement 的 rcNormalPosition 比显示器的 rcMonitor/rcWork；这里直接拿窗口矩形比工作区，
		// 判据一样：有一条边贴着、另一条轴覆盖了大半屏，就是「已经吸附」的窗口。
		bool touchL = Math.Abs(wnd.Left - rcWork.Left) <= 2;
		bool touchR = Math.Abs(wnd.Right - rcWork.Right) <= 2;
		bool touchT = Math.Abs(wnd.Top - rcWork.Top) <= 2;
		bool touchB = Math.Abs(wnd.Bottom - rcWork.Bottom) <= 2;
		bool tallEnough = wnd.Height * 2 >= rcWork.Height;
		bool wideEnough = wnd.Width * 2 >= rcWork.Width;
		return (touchL && tallEnough) || (touchR && tallEnough) || (touchT && wideEnough) || (touchB && wideEnough);
	}

	private void ResetDragState()
	{
		_state = null;
		_dragWait = false;
		_lastEventTime = 0u;
		_hwnd = IntPtr.Zero;
		_activated = false;
		_blockAltUp = false;
		_blockMouseUp = false;
		_ctrl = false;
		_lostUpTicks = 0;
		_lostUpArmed = false;
		// 手已经离开、状态也收干净了：鼠标钩子不再需要（altdrag hooks.c:1084-1086 的 UnhookMouse）。
		// 下一次按下触发键会重新按需装回来。
		WantMouseHook(false);
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

	/// <summary>
	/// hooks.c:1494-1508 的那段复查：触发键此刻还按着吗。
	///
	/// 判据顺序（v1.6.2）：
	///   1. 自检桩；
	///   2. **钩子自己记的状态** —— 唯一能跨线程读、又不受「异步键状态跟着线程走」
	///      影响的来源。老实现直接问 GetAsyncKeyState，而这段代码最常被
	///      OnTriggerHoldTick（线程池线程）调用，在那里永远读回「没按下」；
	///   3. 钩子还没见过这个键（刚启动 / 钩子没装上 / 触发键是注入进来的）才退回物理状态。
	/// </summary>
	private bool TriggerStillHeld(AppConfig cfg)
	{
		Func<bool>? triggerProbeForTest = TriggerProbeForTest;
		if (triggerProbeForTest != null)
		{
			return triggerProbeForTest();
		}
		if (_triggerDownSeen)
		{
			if (!_triggerDown)
			{
				return false;
			}
			long idle = Environment.TickCount64 - Interlocked.Read(ref _triggerEventTick);
			if (idle > TriggerIdleLimitMs)
			{
				HookProbe.W("TriggerStillHeld：钩子已经 " + idle + "ms 没再见过触发键事件，按松开处理");
				return false;
			}
			return true;
		}
		return KeysUtil.IsPhysicallyDown(cfg.TriggerKey);
	}

	/// <summary>
	/// v1.6.0：这台键盘按的触发键算不算数。配置留空 = 所有键盘都算；
	/// 解析不出设备（老系统、远程桌面、注入按键）也一律算 —— 宁可不拦，别把人挡在门外。
	/// </summary>
	/// <summary>
	/// v1.6.3：把「这只键是哪台键盘按的」丢到线程池去问，钩子回调立刻返回。
	///
	/// 解析本身会阻塞几十~几百毫秒（等 Raw Input、按 15.6ms 的定时器精度轮询），
	/// 放在钩子回调里就是全系统输入卡顿；放在线程池上则完全无害 —— 200ms 的长按
	/// 窗口足够它跑完，跑不完就按"放行"处理（宁可不拦，别把人挡在门外）。
	/// </summary>
	private void KickDeviceResolve(AppConfig cfg, int vk)
	{
		Func<int, string?>? deviceResolver = DeviceResolver;
		if (string.IsNullOrEmpty(cfg.Device) || deviceResolver == null)
		{
			_triggerDeviceAllowed = true;
			_triggerDeviceKnown = true;
			return;
		}
		int gen = Interlocked.Increment(ref _deviceGen);
		ThreadPool.QueueUserWorkItem(_ =>
		{
			bool allowed;
			try
			{
				allowed = DeviceAllows(cfg, vk);
			}
			catch
			{
				allowed = true;
			}
			// 期间又按了一次（或已经松开）就作废这次结果，别让旧结论盖住新的按压。
			if (Volatile.Read(ref _deviceGen) != gen)
			{
				return;
			}
			_triggerDeviceAllowed = allowed;
			_triggerDeviceKnown = true;
			HookProbe.W("设备解析完成：vk=" + vk + " allowed=" + allowed + " cfg.Device=" + cfg.Device);
			// 阈值 0（按下即生效）时，转正就卡在这次解析上 —— 这里补上。
			// 阈值 > 0 的不在这里转正：长按定时器到点时会自己复查设备结论。
			if (!allowed || cfg.TriggerHoldMs > 0 || !_triggerDown || _activated)
			{
				return;
			}
			if (!Monitor.TryEnter(_gate, 50))
			{
				HookProbe.W("设备解析完成，阈值 0：拿不到锁，放弃补转正。vk=" + vk);
				return;
			}
			try
			{
				if (_triggerDown && !_activated && !_alt)
				{
					HookProbe.W("设备解析完成，阈值 0：补转正。vk=" + vk);
					ArmTrigger(cfg);
				}
			}
			finally
			{
				Monitor.Exit(_gate);
			}
		});
	}

	/// <summary>
	/// 自检用：设备解析走线程池，断言必须等它落定，否则测的是「还没解析完」的中间态。
	/// </summary>
	/// <summary>
	/// 自检用：等「按住状态」变成期望值。设备解析在线程池上跑，断言不能假设它已经回来。
	/// </summary>
	internal bool WaitHeldForTest(bool expected, int timeoutMs = 1500)
	{
		long start = Environment.TickCount64;
		while (Environment.TickCount64 - start < timeoutMs)
		{
			if (TriggerHeld == expected)
			{
				Thread.Sleep(20);
				return TriggerHeld == expected;
			}
			Thread.Sleep(2);
		}
		return TriggerHeld == expected;
	}

	internal bool WaitDeviceResolvedForTest(int timeoutMs = 2000)
	{
		long start = Environment.TickCount64;
		while (!_triggerDeviceKnown && Environment.TickCount64 - start < timeoutMs)
		{
			Thread.Sleep(2);
		}
		Thread.Sleep(20);
		HookProbe.W("WaitDeviceResolved：known=" + _triggerDeviceKnown + " allowed=" + _triggerDeviceAllowed + " down=" + _triggerDown + " alt=" + _alt + " act=" + _activated + " held=" + TriggerHeld);
		return _triggerDeviceKnown;
	}

	private static bool DeviceAllows(AppConfig cfg, int vk)
	{
		string wanted = cfg.Device ?? "";
		if (wanted.Length == 0)
		{
			return true;
		}
		Func<int, string?>? deviceResolver = DeviceResolver;
		if (deviceResolver == null)
		{
			return true;
		}
		string? actual;
		try
		{
			actual = deviceResolver(vk);
		}
		catch
		{
			return true;
		}
		if (string.IsNullOrEmpty(actual))
		{
			return true;
		}
		// 多选：配置里可能勾了好几台键盘，列表里的任意一台都算数。
		return AppConfig.DeviceAllowed(wanted, actual);
	}

	/// <summary>v1.6.0：按下了触发键，但还没到长按阈值 —— 先吞住，等定时器或者抬起。</summary>
	private void BeginPendingTrigger(AppConfig cfg, int vk)
	{
		_triggerPending = true;
		_pendingVk = vk;
		_pendingConfig = cfg;
		// 还没转正就先装：兜底路径（按下鼠标那一刻才 ArmTrigger）靠的正是这一下点击本身。
		WantMouseHook(true);
		_triggerTimer ??= new Timer(OnTriggerHoldTick, null, Timeout.Infinite, Timeout.Infinite);
		_triggerTimer.Change(Math.Clamp(cfg.TriggerHoldMs, 1, 5000), Timeout.Infinite);
	}

	/// <summary>按够时间了：这就是"按住触发键"，转正。</summary>
	private void OnTriggerHoldTick(object? state)
	{
		if (_pendingConfig == null || !_triggerPending)
		{
			return;
		}
		// 这个回调跑在线程池线程上，而钩子回调、鼠标拖动都在 _gate 里改同一批状态。
		// 用 TryEnter（带超时）而不是 lock：钩子回调里可能正在等 WM_ENTERSIZEMOVE 的
		// 200ms 超时，长按回调绝不该在这条路上排队；拿不到就等下一轮。
		if (!Monitor.TryEnter(_gate, 50))
		{
			_tickRetry++;
			HookProbe.W("长按计时到点：_gate 忙，第 " + _tickRetry + " 次重排");
			if (_tickRetry <= 40 && _triggerPending)
			{
				_triggerTimer?.Change(20, Timeout.Infinite);
			}
			else
			{
				_tickRetry = 0;
				// 吞过就得还：这条路上 keyup 未必还来，先把按键补发给前台，别让用户白按一下。
				CancelPendingTrigger(replay: _triggerSwallowed);
			}
			return;
		}
		try
		{
			_tickRetry = 0;
			AppConfig? cfg = _pendingConfig;
			if (cfg == null || !_triggerPending)
			{
				return;
			}
			if (!TriggerStillHeld(cfg))
			{
				HookProbe.W("长按计时到点，但触发键已经不在了 —— 取消。hookSeen=" + _triggerDownSeen + " hookDown=" + _triggerDown + " raw=0x" + ((int)Native.GetAsyncKeyState(KeysUtil.ToVirtualKey(cfg.TriggerKey))).ToString("X"));
				// 我键都吞了，用户却已经松手：这一次按压必须还给前台，否则就是「按了没反应」。
				CancelPendingTrigger(replay: _triggerSwallowed);
				return;
			}
			// 设备范围异步解析完了就核对一下：不是勾中的键盘就别转正。
			if (_triggerDeviceKnown && !_triggerDeviceAllowed)
			{
				HookProbe.W("长按计时到点，但这只键盘不在允许范围 —— 取消。cfg.Device=" + cfg.Device);
				Log.Info("按住拖拽：这次触发键不是配置里勾选的键盘发出的（只认：" + cfg.Device + "），本次忽略");
				// 不在白名单的键盘不该被吞：补发一次，让它的触发键照常打字。
				CancelPendingTrigger(replay: _triggerSwallowed);
				return;
			}
			HookProbe.W("长按计时到点：转正。hookSeen=" + _triggerDownSeen + " hookDown=" + _triggerDown);
			ArmTrigger(cfg);
		}
		finally
		{
			Monitor.Exit(_gate);
		}
	}

	/// <summary>把触发键置成"按下"（hooks.c 里 Alt 按下那一步），从这一刻起鼠标归按住拖拽。</summary>
	private void ArmTrigger(AppConfig cfg)
	{
		HookProbe.W("ArmTrigger：转正（_alt=true, triggerHeld=1）");
		Log.Info("按住拖拽：触发键已按住（" + cfg.TriggerKey + "），鼠标交给按住拖拽");
		// 转正了：接下来这一下鼠标点击必须由我们拿到（altdrag hooks.c:956 的 HookMouse）。
		WantMouseHook(true);
		_triggerPending = false;
		_pendingConfig = null;
		_triggerTimer?.Change(Timeout.Infinite, Timeout.Infinite);
		_alt = true;
		Interlocked.Exchange(ref _triggerHeld, 1);
		_blockAltUp = false;
		_blockMouseUp = false;
		_ctrlIsTrigger = IsCtrlKey(cfg.TriggerKey);
		if (_ctrlIsTrigger)
		{
			_ignoreCtrl = true;
		}
	}

	/// <summary>
	/// 取消还没转正的那次按压。replay = 这是一次短按：把按键原样补发给前台，
	/// 用户按 Enter 就该得到回车 —— 触发键不能因为被选作触发键就把自己的功能弄丢。
	/// </summary>
	private void CancelPendingTrigger(bool replay)
	{
		if (!_triggerPending)
		{
			return;
		}
		_triggerPending = false;
		_pendingConfig = null;
		_triggerTimer?.Change(Timeout.Infinite, Timeout.Infinite);
		/* 短按触发键（没到长按阈值）时，BeginPendingTrigger 已经把鼠标钩子装上了 ——
		 * 这里必须把它卸掉，否则用户每按一次普通的 Enter，整条低级鼠标钩子就永久挂在链头：
		 * 之后每一次鼠标移动都要先进我们的回调，还会和手势那套抢链头顺序。
		 * altdrag 的 HookMouse()/UnhookMouse() 是成对的（hooks.c:1084-1086 松手就卸）。 */
		if (_action == Actions.CodeNone && !_alt)
		{
			WantMouseHook(false);
		}
		if (replay)
		{
			// 走带记账的版本：同一次按压在「长按定时器取消」和「抬起」两条路上都要求补发时，
			// 只补一次（用户按一下 Enter 不该得到两个回车）。
			ReplaySwallowedTap(_pendingVk);
		}
		if (_action == Actions.CodeNone)
		{
			// 只是一次短按（或者长按没到点就松了）：没人在拖，鼠标钩子卸掉。
			WantMouseHook(false);
		}
	}

	/// <summary>
	/// 补发一对按下/抬起（打上自己的 MagicExtraInfo，钩子会忽略，不会被当成真的又按了一次）。
	/// </summary>
	/// <summary>
	/// v1.7.0：吞键模式下把吃掉的那一下还给前台（一次按压只还一次）。
	/// </summary>
	private void ReplaySwallowedTap(int vk)
	{
		if (_triggerReplayed)
		{
			return;
		}
		_triggerReplayed = true;
		ReplayKey(vk);
	}

	private static void ReplayKey(int vk)
	{
		if (vk <= 0)
		{
			return;
		}
		Action<int>? probe = ReplayProbeForTest;
		if (probe != null)
		{
			probe(vk);
			return;
		}
		try
		{
			// 方向键、Home/End/PgUp/PgDn、Insert/Delete、小键盘除号、右 Ctrl/右 Alt、NumLock 要带扩展位。
			bool extended = vk == 0x21 || vk == 0x22 || vk == 0x23 || vk == 0x24 || vk == 0x25 ||
			    vk == 0x26 || vk == 0x27 || vk == 0x28 || vk == 0x2D || vk == 0x2E || vk == 0x90 ||
			    vk == 0x6F || vk == 0xA3 || vk == 0xA5 || vk == 0x5C;
			uint flags = extended ? Native.KEYEVENTF_EXTENDEDKEY : 0u;
			Native.KEYBDINPUT[] keys = new Native.KEYBDINPUT[2];
			keys[0].wVk = (ushort)vk;
			keys[0].dwFlags = flags;
			keys[0].dwExtraInfo = HookEngine.MagicExtraInfo;
			keys[1].wVk = (ushort)vk;
			keys[1].dwFlags = flags | Native.KEYEVENTF_KEYUP;
			keys[1].dwExtraInfo = HookEngine.MagicExtraInfo;
			Native.INPUT[] inputs = new Native.INPUT[2];
			for (int i = 0; i < 2; i++)
			{
				inputs[i].type = Native.INPUT_KEYBOARD;
				inputs[i].u.ki = keys[i];
			}
			Native.SendInput(2u, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	private static Native.POINT CursorOrZero()
	{
		if (TryCursor(out var pt))
		{
			return pt;
		}
		return default(Native.POINT);
	}

	/// <summary>
	/// Shift 此刻按着没有（Lower 动作按住 Shift = 最小化，hooks.c:1650 一带）。
	/// 先看键盘钩子记下的物理状态；钩子是在这次按键之后才装的这种边角情况再退回异步键状态兜一次。
	/// </summary>
	private bool IsShiftDown()
	{
		if (_shiftPhys)
		{
			return true;
		}
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
				_restoreTimer?.Dispose();
				_triggerTimer?.Dispose();
				_moveTimer?.Dispose();
				_restoreTimer = null;
				_triggerTimer = null;
				_moveTimer = null;
				ResetDragState();
			}
		}
	}
}