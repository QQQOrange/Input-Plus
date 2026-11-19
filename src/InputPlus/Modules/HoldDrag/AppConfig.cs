using System;
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

	/// <summary>
	/// 触发拖拽的键（KeysUtil 的 id 格式："K:0x0D" 就是 Enter，"K:LAlt" 是左 Alt，
	/// 鼠标侧键是 "M:1"）。默认 Enter —— 原版 ini 里的 Hotkeys=A4 A5 是"多个键"的写法，
	/// 这里只留一个，改成什么键由用户在界面上自己按。
	/// </summary>
	public string TriggerKey { get; set; } = KeysUtil.DefaultTrigger;

	/// <summary>触发键本身要不要吞掉。默认不吞（原版不吞 Alt），改成 Enter 以后尤其不能吞。</summary>
	public bool SwallowTrigger { get; set; }

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

	/// <summary>[General] MoveRate：每 N 个鼠标事件才写一次窗口（1 = 每个事件都写）。</summary>
	public int MoveRate { get; set; } = 1;

	/// <summary>[General] ResizeRate：缩放时的抽帧，语义同 <see cref="MoveRate"/>。</summary>
	public int ResizeRate { get; set; } = 1;

	/// <summary>[Blacklist] ProcessBlacklist：按进程映像名（一行/逗号分隔，支持 * 通配）。</summary>
	public string ProcessBlacklist { get; set; } = "";

	/// <summary>[Blacklist] Blacklist：进程|类名，* 通配，见 <see cref="DefaultWindowBlacklist"/>。</summary>
	public string WindowBlacklist { get; set; } = DefaultWindowBlacklist;

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
		ActionLeft = Actions.Normalize(ActionLeft);
		ActionMiddle = Actions.Normalize(ActionMiddle);
		ActionRight = Actions.Normalize(ActionRight);
		ActionX1 = Actions.Normalize(ActionX1);
		ActionX2 = Actions.Normalize(ActionX2);
		AutoSnap = Math.Clamp(AutoSnap, 0, 3);
		SnapThreshold = Math.Clamp(SnapThreshold, 0, 200);
		AutoRemaximize = Math.Clamp(AutoRemaximize, 0, 2);
		MoveRate = Math.Clamp(MoveRate <= 0 ? 1 : MoveRate, 1, 8);
		ResizeRate = Math.Clamp(ResizeRate <= 0 ? 1 : ResizeRate, 1, 8);
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
