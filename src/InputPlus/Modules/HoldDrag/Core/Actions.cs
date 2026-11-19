using System;

namespace InputPlus.Modules.HoldDrag;

/// <summary>被拖拽的鼠标键。与 AltDrag.ini 的 [Input] 一节一一对应。</summary>
internal enum MouseButton
{
	Left,
	Middle,
	Right,
	X1,
	X2
}

/// <summary>
/// hooks.c:1131-1190 的 GetAction 那套：一个鼠标键配一个动作。
/// 存的是字符串 id（配置里可读、可手改），引擎里用 <see cref="ToCode"/> 换成紧凑的整数。
/// </summary>
internal static class Actions
{
	internal const string Move = "Move";

	internal const string Resize = "Resize";

	internal const string Close = "Close";

	internal const string Minimize = "Minimize";

	internal const string Lower = "Lower";

	internal const string AlwaysOnTop = "AlwaysOnTop";

	internal const string Center = "Center";

	internal const string Nothing = "Nothing";

	/// <summary>界面下拉里的顺序（也是 ToCode 的编号顺序）。</summary>
	internal static readonly string[] All = new string[8] { Move, Resize, Close, Minimize, Lower, AlwaysOnTop, Center, Nothing };

	/// <summary>动作的整数编号；0 表示"没有动作"（hooks.c 的 ACTION_NONE）。</summary>
	internal const int CodeNone = 0;

	internal static int ToCode(string? id)
	{
		int num = Array.IndexOf(All, Normalize(id));
		return (num < 0) ? 0 : (num + 1);
	}

	/// <summary>认不出来就落到 Nothing（原版默认值），绝不抛。</summary>
	internal static string Normalize(string? id)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return Nothing;
		}
		foreach (string item in All)
		{
			if (string.Equals(item, id.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return item;
			}
		}
		return Nothing;
	}

	internal static string DisplayName(string? id)
	{
		return Normalize(id) switch
		{
			"Move" => Loc.T("移动窗口"),
			"Resize" => Loc.T("改窗口大小"),
			"Close" => Loc.T("关闭窗口"),
			"Minimize" => Loc.T("最小化"),
			"Lower" => Loc.T("放到最下层"),
			"AlwaysOnTop" => Loc.T("置顶 / 取消置顶"),
			"Center" => Loc.T("居中"),
			_ => Loc.T("什么都不做"),
		};
	}
}
