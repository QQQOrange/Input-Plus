using System;
using System.Collections.Generic;
using System.Text.Json;

namespace InputPlus.Modules.HoldDrag;

/// <summary>
/// 「按住拖拽」的唯一一份配置。字段名与 AltDrag.ini 一一对应（大小写一致），
/// 这样用户拿原版 ini 对照、或者我们照着 ini 写默认值，都不用二次翻译。
///
/// 原来的 44 个字段（触发方式、HUD、外框、撤销、程序规则、布局、抖动……）全部作废：
/// 那些是旧引擎自己的功能，不在"完全照抄 AltDrag"的范围里。
/// </summary>
internal sealed class AppConfig
{
	/// <summary>AltDrag.ini 里 [Blacklist] Blacklist 的默认值，原样搬过来。</summary>
	internal const string DefaultWindowBlacklist = "*|MultitaskingViewFrame,Volume Control|Windows.UI.Core.CoreWindow,*|TaskSwitcherWnd,*|TaskSwitcherOverlayWnd,|#32770";

	/// <summary>AltDrag.ini 的 [Input] 一节：一个键一个动作。</summary>
	internal const string DefaultActionLeft = "Move";

	internal const string DefaultActionMiddle = "Resize";

	internal const string DefaultActionRight = "Resize";

	internal const string DefaultActionX1 = "Nothing";

	internal const string DefaultActionX2 = "Nothing";

	/// <summary>AltDrag.ini [Performance] MoveRate=2。</summary>
	internal const int DefaultMoveRate = 2;

	/// <summary>AltDrag.ini [Performance] ResizeRate=5。</summary>
	internal const int DefaultResizeRate = 5;

	/// <summary>触发键要按住多久才算"按住"（毫秒）。AltDrag.ini 里没有这一项，v1.6.0 加的。</summary>
	internal const int DefaultTriggerHoldMs = 200;

	/// <summary>
	/// 触发拖拽的键（KeysUtil 的 id 格式："K:0x0D" 就是 Enter，"K:LAlt" 是左 Alt，
	/// 鼠标侧键是 "M:1"）。默认 Enter —— 原版 ini 里的 Hotkeys=A4 A5 是"多个键"的写法，
	/// 这里只留一个，改成什么键由用户在界面上自己按。
	/// </summary>
	public string TriggerKey { get; set; } = KeysUtil.DefaultTrigger;

	/// <summary>
	/// 触发键本身要不要吞掉。v1.7.0 起由用户在设置里开关，默认 false = 即时透传。
	///
	/// 打开后的语义（AltDragSink.HandleKey）：keydown 不给前台；这一次按压如果从头到尾
	/// 没起拖（短按），抬起时补发一次完整按键 —— 所以 Enter 当触发键时「按一下还是回车、
	/// 按住拖窗口前台收不到回车」。关着的时候行为与 v1.6.1 完全一致。
	/// </summary>
	public bool SwallowTrigger { get; set; }

	/// <summary>
	/// AltDrag.ini 里没有这一项（v1.6.0 加的）：触发键要按住多久才开始接管鼠标（毫秒）。
	///
	/// Enter 当触发键时必须靠它把"回车"和"按住拖拽"分开：短按照旧是回车，
	/// 按住超过阈值才转成按住拖拽。0 = 按下即接管（AltDrag 的老行为）。
	/// </summary>
	public int TriggerHoldMs { get; set; } = DefaultTriggerHoldMs;

	/// <summary>
	/// 只有这几台键盘按的触发键才生效（InputDevices 的设备标识，多台用 ';' 隔开）。
	/// 留空 = 所有键盘。v1.6.1 起支持多选；v1.6.0 的单值写法原样能读，不用迁移。
	/// </summary>
	public string Device { get; set; } = "";

	/// <summary>
	/// 「一台都不认」的哨兵值。
	///
	/// <see cref="Device"/> 留空代表"所有键盘"，所以"用户把每台都取消掉了"必须有一个
	/// 不等于空串的写法 —— 否则配置一存成空串就又变回"所有键盘"，
	/// 方框会被全部打回去（v1.6.4 之前就是这个鬼畜效果）。
	/// </summary>
	internal const string NoDevice = "none";

	public string ActionLeft { get; set; } = DefaultActionLeft;

	public string ActionMiddle { get; set; } = DefaultActionMiddle;

	public string ActionRight { get; set; } = DefaultActionRight;

	public string ActionX1 { get; set; } = DefaultActionX1;

	public string ActionX2 { get; set; } = DefaultActionX2;

	/// <summary>[General] Aero=1：拖到屏幕上/左/右边缘时按半屏铺开。</summary>
	public bool AeroSnap { get; set; } = true;

	/// <summary>[General] AutoSnap=0..3：0 关、1 屏幕边缘+任务栏、2 再加窗口外侧、3 再加窗口内侧。</summary>
	public int AutoSnap { get; set; } = 3;

	/// <summary>[General] SnapThreshold=20：吸附阈值（像素，不乘 DPI）。</summary>
	public int SnapThreshold { get; set; } = AltDragCore.DefaultSnapThreshold;

	/// <summary>[General] AutoRemaximize=0/1/2：拖到另一块屏时重新最大化；2 表示一秒后还原。</summary>
	public int AutoRemaximize { get; set; } = 1;

	/// <summary>[General] AutoFocus=0：起拖时把目标窗口提到前台。</summary>
	public bool AutoFocus { get; set; }

	/// <summary>[Performance] MoveRate：每 N 个鼠标事件才写一次窗口（1 = 每个事件都写）。出厂 2，同 AltDrag.ini。</summary>
	public int MoveRate { get; set; } = DefaultMoveRate;

	/// <summary>[Performance] ResizeRate：缩放时的抽帧，语义同 <see cref="MoveRate"/>。出厂 5，同 AltDrag.ini。</summary>
	public int ResizeRate { get; set; } = DefaultResizeRate;

	/// <summary>hooks.c:350 出厂值。</summary>
	internal const int DefaultDragThreshold = 1;

	/// <summary>hooks.c:351 出厂值。</summary>
	internal const int DefaultDragOutThresholdPx = 20;

	/// <summary>config.c:2201 出厂值。</summary>
	internal const int DefaultCenterFraction = 24;

	/// <summary>config.c:2203 出厂值（哨兵：按最接近的那条边算）。</summary>
	internal const int DefaultSidesFraction = 255;

	/// <summary>ResizeAxesThreshold 的出厂值（物理像素）。</summary>
	internal const int DefaultResizeAxesThreshold = 60;

	/// <summary>
	/// [Performance] DragThreshold（AltSnap hooks.c:350 出厂 1）：
	/// 0 = 按下就拖；1 = 只有本来吸附/最大化着的窗口才等拖出阈值；2 = 一律等；3 = 缩放也等。
	///
	/// 这是 AltSnap 相对 AltDrag 新增的行为，也是「抓一下就抖」的对策：
	/// 起手先记一个 DRAG_WAIT，光标真挪出 <see cref="DragOutThresholdPx"/> 才开始写窗口
	/// （hooks.c:5479-5485）。默认 1 与原版一致。
	/// </summary>
	public int DragThreshold { get; set; } = DefaultDragThreshold;

	/// <summary>[General] DragOutThresholdPx（hooks.c:351 出厂 20）：拖出多远才算真的开始拖。</summary>
	public int DragOutThresholdPx { get; set; } = DefaultDragOutThresholdPx;

	/// <summary>
	/// [Performance] RezTimer（hooks.c:364 出厂 0）：抽帧用哪把尺子。
	/// 0 = 数事件（<see cref="MoveRate"/> / <see cref="ResizeRate"/>）；1 = 只按事件时间戳变化抽帧；
	/// 3 = 数事件 + 时间戳变化时补一帧。原版三档都在（hooks.c:5496-5512）。
	/// </summary>
	public int RezTimer { get; set; }

	/// <summary>
	/// [General] CenterFraction（config.c:2201 出厂 24）：窗口中间这条带占宽/高的百分比，
	/// 落在这条带里的那条轴算「居中」（两条轴都居中 = 四向扩张）。
	/// </summary>
	public int CenterFraction { get; set; } = DefaultCenterFraction;

	/// <summary>
	/// [General] SidesFraction（config.c:2203 出厂 255）：四条边那圈占的百分比。
	/// 255 是 AltSnap 的哨兵值（远大于 100），效果是落到最接近的那条边
	/// （hooks.c:3839-3856 的 SetEdgeToClosestSide）。等于 CenterFraction 时退回经典九宫格。
	/// </summary>
	public int SidesFraction { get; set; } = DefaultSidesFraction;

	/// <summary>
	/// [General] ResizeCenter（config.c:583 出厂 1）：抓到正中间时干什么。
	/// 0 = 固定改右下；1 = 四向扩张（原版 Norm）；2 = 转成移动；3 = 按对角线选最近边。
	/// </summary>
	public int ResizeCenter { get; set; } = 1;

	/// <summary>
	/// [General] ResizeBothAxes（出厂 true）：边带抓点也允许两轴一起变。
	///
	/// <para>关掉 = 严格照上游九宫格：落在边带里的那一轴只动一条边，中间带那一轴纹丝不动。
	/// 打开 = 斜着拖出 <see cref="ResizeAxesThreshold"/> 像素后，把中间带那一轴收养到对应的边
	/// （见 AltDragCore.AdoptFollowAxis）。用户报「右键缩放只能上下或左右」就是九宫格边带太宽导致的，
	/// 这个开关只在手势上纠正，不改选边本身。</para>
	/// </summary>
	public bool ResizeBothAxes { get; set; } = true;

	/// <summary>[General] ResizeAxesThreshold（出厂 60 像素，屏幕物理像素）：斜着拖多远才收养另一轴。0 = 立刻收养。</summary>
	public int ResizeAxesThreshold { get; set; } = DefaultResizeAxesThreshold;
	/// <summary>
	/// AltSnap 的 MOVEASYNC 写窗口方式（hooks.c:1508-1567 的 flag 选择）。
	/// 打开 = SetWindowPos 带 SWP_ASYNCWINDOWPOS，重窗口让路、光标更顺，代价是窗口可能落后一帧。
	/// 出厂关：Input Plus 的"跟手"是同步写换来的，见 AltDragSink.WriteWindow 的说明。
	/// </summary>
	public bool AsyncWindowPos { get; set; }

	/// <summary>
	/// 写窗口之前先问一下目标窗口「这样缩放你打算怎么摆」（hooks.c:1410-1465 的 LetWindowKickBack）。
	/// 终端/控制台这类按字符格对齐的窗口靠它才不会一卡一跳。出厂开。
	/// </summary>
	public bool KickBack { get; set; } = true;

	/// <summary>
	/// 抽帧出厂值改过一次：1.5.3 之前是 1/1（每个鼠标事件都写），现在跟 AltDrag.ini 的
	/// [Performance] 一致，改成 2/5。这个字段记"这份配置已经跟上新版出厂值了"，
	/// 只用来做一次性迁移 —— 用户自己调过的值不算 1，不会被改。
	/// </summary>
	public int RateDefaultsVersion { get; set; }

	/// <summary>[Blacklist] ProcessBlacklist：按进程映像名（一行/逗号分隔，支持 * 通配）。</summary>
	public string ProcessBlacklist { get; set; } = "";

	/// <summary>[Blacklist] Blacklist：进程名或窗口标题|类名，* 通配，见 <see cref="DefaultWindowBlacklist"/>。</summary>
	public string WindowBlacklist { get; set; } = DefaultWindowBlacklist;

	/// <summary>AltDrag.ini 里 [Blacklist] Snaplist 的默认值，原样搬过来（逗号分隔的"左边|类名"）。</summary>
	internal const string DefaultSnapList = "*|BaseWindow_RootWnd,*|SkinWnd,*|ChatSkinWnd,*|SpotifyMainWindow,*|USurface_33202";

	/// <summary>
	/// [Blacklist] Snaplist：没有 WS_CAPTION 的窗口本来不参与吸附，列在这里的算例外
	/// （hooks.c:251 的条件是 "(style &amp; WS_CAPTION) == WS_CAPTION || blacklisted(window, Snaplist)"）。
	/// 界面没有这一项，先按原版默认值走。
	/// </summary>
	public string SnapList { get; set; } = DefaultSnapList;

	internal string ActionFor(MouseButton button)
	{
		return button switch
		{
			MouseButton.Left => ActionLeft,
			MouseButton.Middle => ActionMiddle,
			MouseButton.Right => ActionRight,
			MouseButton.X1 => ActionX1,
			_ => ActionX2,
		} ?? Actions.Nothing;
	}

	/// <summary>
	/// 把 ';' 分隔的设备列表拆成数组（去空、去重、保序）。
	///
	/// 为什么列表还是一个字符串：整份配置是 JSON 存进 HoldDragJson 的。
	/// 用字符串既兼容 v1.6.0 的单值写法（老配置不用迁移），也不用给解析器加数组类型 ——
	/// 单值和多值走同一条代码路径，判定的地方只有一处。
	/// </summary>
	internal static string[] SplitDevices(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return Array.Empty<string>();
		}
		var list = new List<string>(4);
		foreach (string part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			string key = part.Trim();
			if (key.Length == 0) continue;
			bool seen = false;
			foreach (string have in list)
			{
				if (string.Equals(have, key, StringComparison.OrdinalIgnoreCase))
				{
					seen = true;
					break;
				}
			}
			if (!seen) list.Add(key);
		}
		return list.ToArray();
	}

	/// <summary>设备列表 → 存储用的 ';' 分隔字符串（同样去空去重，保序）。</summary>
	internal static string JoinDevices(IEnumerable<string>? keys)
	{
		if (keys == null)
		{
			return "";
		}
		var parts = new List<string>(4);
		foreach (string key in keys)
		{
			string k = (key ?? "").Trim();
			if (k.Length == 0) continue;
			bool seen = false;
			foreach (string have in parts)
			{
				if (string.Equals(have, k, StringComparison.OrdinalIgnoreCase))
				{
					seen = true;
					break;
				}
			}
			if (!seen) parts.Add(k);
		}
		return string.Join(";", parts);
	}

	/// <summary>这台设备（<paramref name="actual"/>）够不够资格触发。</summary>
	/// <remarks>
	/// 列表为空 = 所有键盘；解析不出设备（老系统、远程桌面、注入按键）也一律放行 ——
	/// 宁可不拦，别把人挡在门外。
	/// </remarks>
	internal static bool DeviceAllowed(string? configured, string? actual)
	{
		if (DeviceScopeIsNone(configured))
		{
			return false;   // 用户把每台都取消掉了：触发键一律不算数
		}
		string[] wanted = SplitDevices(configured);
		if (wanted.Length == 0)
		{
			return true;
		}
		if (string.IsNullOrEmpty(actual))
		{
			return true;
		}
		foreach (string key in wanted)
		{
			if (string.Equals(key, actual, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>配置是否明确表达"一台键盘都不认"。</summary>
	internal static bool DeviceScopeIsNone(string? configured)
	{
		return string.Equals((configured ?? string.Empty).Trim(), NoDevice, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>配置是否表达"不挑设备"（留空）。</summary>
	internal static bool DeviceScopeIsAll(string? configured)
	{
		if (DeviceScopeIsNone(configured))
		{
			return false;
		}
		return SplitDevices(configured).Length == 0;
	}

	/// <summary>设备名单给人看的说法（日志、提示、胶囊文字都用它）。</summary>
	internal static string DeviceLabel(string? configured)
	{
		if (DeviceScopeIsNone(configured))
		{
			return "未勾选任何键盘";
		}
		string[] keys = SplitDevices(configured);
		return keys.Length == 0 ? "所有键盘" : string.Join("；", keys);
	}

	/// <summary>
	/// 勾上或取消清单里的一行，返回新的 <see cref="Device"/> 值。
	///
	/// 这里只算字符串，不碰界面 —— "勾「所有键盘」时下面每台都打勾、取消它时
	/// 下面一起空掉"这种级联是显示层按「配置 + 现有哪些设备」统一渲染出来的，
	/// 配置本身只要能表达三种状态就够了：留空＝所有键盘、清单＝只认这几台、
	/// <see cref="NoDevice"/>＝一台都不认。
	/// </summary>
	/// <param name="configured">当前配置值。</param>
	/// <param name="key">被点的那一行：空串＝最上面那行「所有键盘」。</param>
	/// <param name="on">点完之后是不是勾上的状态。</param>
	/// <param name="present">界面上现在列着的设备（从"所有键盘"里摘掉某台时要用它算"其余那些"）。</param>
	internal static string ApplyDeviceToggle(string? configured, string? key, bool on, IEnumerable<string>? present)
	{
		string id = (key ?? string.Empty).Trim();
		bool all = DeviceScopeIsAll(configured);
		bool none = DeviceScopeIsNone(configured);

		if (id.Length == 0)
		{
			// 「所有键盘」这一行就是三态开关：勾上＝不挑设备，取消＝一台都不认。
			return on ? string.Empty : NoDevice;
		}

		var keys = new List<string>(SplitDevices(configured));
		if (on)
		{
			if (all || none)
			{
				// 之前"全认"或"全不认"：单独勾这一台＝只认这一台。
				return id;
			}
			bool seen = false;
			foreach (string have in keys)
			{
				if (string.Equals(have, id, StringComparison.OrdinalIgnoreCase))
				{
					seen = true;
					break;
				}
			}
			if (!seen)
			{
				keys.Add(id);
			}
			return JoinDevices(keys);
		}

		if (all && present != null)
		{
			/* 画面上每台都打着勾（因为"所有键盘"），用户取消了其中一台：
			   意思就是"除了这一台，别的都认"。 */
			var rest = new List<string>(4);
			foreach (string dev in present)
			{
				string d = (dev ?? string.Empty).Trim();
				if (d.Length == 0) continue;
				if (string.Equals(d, id, StringComparison.OrdinalIgnoreCase)) continue;
				rest.Add(d);
			}
			return rest.Count == 0 ? NoDevice : JoinDevices(rest);
		}

		keys.RemoveAll(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
		// 取消完最后一台 = 一台都不认；写回空串会变成"所有键盘"，方框又全打回来。
		return keys.Count == 0 ? NoDevice : JoinDevices(keys);
	}

	internal AppConfig Clone()
	{
		return Parse(Serialize());
	}

	internal string Serialize()
	{
		return JsonSerializer.Serialize(this, JsonOptions);
	}

	internal static AppConfig Parse(string? json)
	{
		AppConfig appConfig;
		if (string.IsNullOrWhiteSpace(json))
		{
			appConfig = new AppConfig();
		}
		else
		{
			try
			{
				appConfig = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
			}
			catch (Exception)
			{
				// 坏 JSON 一律回默认值：宁可回到原版手感，也不要让一页设置打不开。
				appConfig = new AppConfig();
			}
		}
		appConfig.Normalize();
		return appConfig;
	}

	/// <summary>把越界/空值收回到合法范围。所有入口（存盘、导入、界面）都走这里。</summary>
	internal void Normalize()
	{
		if (string.IsNullOrWhiteSpace(TriggerKey))
		{
			TriggerKey = KeysUtil.DefaultTrigger;
		}
		TriggerHoldMs = Math.Clamp(TriggerHoldMs, 0, 5000);
		// v1.7.0：吞键是用户可选项，这里不再像 v1.6.1 那样无条件写回 false —— 值原样保留。
		// 默认值仍是 false（见属性声明），老配置里没有这一项时读出来就是 false，
		// 不会升级之后突然开始在钩子里吞键。
		// 多选：'a;b;c' 拆开、去空、去重，再拼回去（顺手把老的单值写法规整掉）。
		Device = JoinDevices(SplitDevices(Device));
		ActionLeft = Actions.Normalize(ActionLeft);
		ActionMiddle = Actions.Normalize(ActionMiddle);
		ActionRight = Actions.Normalize(ActionRight);
		ActionX1 = Actions.Normalize(ActionX1);
		ActionX2 = Actions.Normalize(ActionX2);
		AutoSnap = Math.Clamp(AutoSnap, 0, 3);
		SnapThreshold = Math.Clamp(SnapThreshold, 0, 200);
		AutoRemaximize = Math.Clamp(AutoRemaximize, 0, 2);
		// 一次性迁移，必须排在夹取之前：老配置里 MoveRate/ResizeRate 的出厂值是 1，
		// 换成 AltDrag 的 2/5；用户自己调成别的数的保持不动。
		if (RateDefaultsVersion < 2)
		{
			if (MoveRate == 1)
			{
				MoveRate = DefaultMoveRate;
			}
			if (ResizeRate == 1)
			{
				ResizeRate = DefaultResizeRate;
			}
			RateDefaultsVersion = 2;
		}
		MoveRate = Math.Clamp(MoveRate <= 0 ? 1 : MoveRate, 1, 8);
		ResizeRate = Math.Clamp(ResizeRate <= 0 ? 1 : ResizeRate, 1, 8);
		// AltSnap 那一组几何参数（hooks.c:350-364 / config.c:2201-2203）。
		// 范围照原版的范围夹：档位 0..3、阈值 0..200px、两个百分比 0..255、ResizeCenter 0..3。
		DragThreshold = Math.Clamp(DragThreshold, 0, 3);
		DragOutThresholdPx = Math.Clamp(DragOutThresholdPx, 0, 200);
		RezTimer = RezTimer switch { 1 => 1, 3 => 3, _ => 0 };
		CenterFraction = Math.Clamp(CenterFraction, 0, 100);
		SidesFraction = Math.Clamp(SidesFraction, 0, 255);
		ResizeCenter = Math.Clamp(ResizeCenter, 0, 3);
		ResizeAxesThreshold = Math.Clamp(ResizeAxesThreshold, 0, 400);
		ProcessBlacklist ??= "";
		WindowBlacklist ??= "";
	}

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = false,
		PropertyNameCaseInsensitive = true,
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};
}
