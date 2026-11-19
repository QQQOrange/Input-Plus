using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace InputPlus.Modules.HoldDrag;

/// <summary>
/// 原版 [Blacklist] 那套：按进程映像名 / 进程+类名决定"这扇窗完全不接管"。
///
/// 原来的 AppRules 是 Input-Plus 自己的"按程序规则"（Ignore / MoveOnly / ResizeOnly / NoFrame），
/// 那是旧引擎的功能面，AltDrag 只有"接管 / 不接管"两档，所以整份换掉。
/// </summary>
internal static class AppRules
{
	private static readonly Dictionary<nint, (string Process, string ClassName)> Cache = new Dictionary<nint, (string, string)>();

	private static readonly object Gate = new object();

	internal static (string Process, string ClassName) Describe(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return (string.Empty, string.Empty);
		}
		lock (Gate)
		{
			if (Cache.TryGetValue(hwnd, out var value))
			{
				return value;
			}
		}
		string pure = ProcessNameOf(hwnd);
		string classNameW = ClassNameOf(hwnd);
		lock (Gate)
		{
			if (Cache.Count > 256)
			{
				Cache.Clear();
			}
			Cache[hwnd] = (pure, classNameW);
		}
		return (pure, classNameW);
	}

	internal static string ClassNameOf(nint hwnd)
	{
		StringBuilder stringBuilder = new StringBuilder(256);
		if (Native.GetClassNameW(hwnd, stringBuilder, stringBuilder.Capacity) <= 0)
		{
			return string.Empty;
		}
		return stringBuilder.ToString();
	}

	/// <summary>进程映像名，带 .exe（原版 ini 里就是这么写的，例如 "chrome.exe"）。</summary>
	internal static string ProcessNameOf(nint hwnd)
	{
		try
		{
			uint pid;
			Native.GetWindowThreadProcessId(hwnd, out pid);
			if (pid == 0u)
			{
				return string.Empty;
			}
			using Process process = Process.GetProcessById((int)pid);
			string text = process.ProcessName;
			return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text : (text + ".exe");
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	internal static bool IsOwnWindow(nint hwnd)
	{
		uint pid;
		Native.GetWindowThreadProcessId(hwnd, out pid);
		return pid == (uint)Environment.ProcessId;
	}

	/// <summary>命中任何一条黑名单就不接管。null/空配置 = 只走原版默认的那几条。</summary>
	internal static bool IsBlacklisted(AppConfig cfg, nint hwnd)
	{
		var (text, text2) = Describe(hwnd);
		if (MatchesProcesses(cfg.ProcessBlacklist, text))
		{
			return true;
		}
		return MatchesWindows(cfg.WindowBlacklist, text, text2);
	}

	/// <summary>[Blacklist] ProcessBlacklist：一行（或逗号）一条进程映像名，支持 * 与 ?。</summary>
	internal static bool MatchesProcesses(string? list, string process)
	{
		foreach (string item in Rows(list))
		{
			if (Wildcard(item, process) || Wildcard(item, StripExe(process)))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>[Blacklist] Blacklist：一条 "进程|类名"，两边都可以是空或 *。</summary>
	internal static bool MatchesWindows(string? list, string process, string className)
	{
		foreach (string item in Rows(list))
		{
			int num = item.IndexOf('|');
			string text = item;
			string text2 = (num < 0) ? string.Empty : item.Substring(num + 1).Trim();
			if (num >= 0)
			{
				text = item.Substring(0, num).Trim();
			}
			if (Wildcard(text, process) || Wildcard(text, StripExe(process)))
			{
				if (Wildcard(text2, className))
				{
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>一行一条文本 → 干净的条目列表（去空行、去首尾空白）。界面和匹配共用。</summary>
	internal static List<string> Rows(string? text)
	{
		List<string> list = new List<string>();
		if (string.IsNullOrWhiteSpace(text))
		{
			return list;
		}
		string[] array = text.Split(new char[4] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string text2 in array)
		{
			string text3 = text2.Trim();
			if (text3.Length != 0)
			{
				list.Add(text3);
			}
		}
		return list;
	}

	private static string StripExe(string process)
	{
		return process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process.Substring(0, process.Length - 4) : process;
	}

	/// <summary>空/通配符都是"任何值"；带 * ? 的走正则，其余按忽略大小写的整串比较。</summary>
	private static bool Wildcard(string pattern, string value)
	{
		string text = pattern.Trim();
		if (text.Length == 0 || text == "*")
		{
			return true;
		}
		if (text.IndexOf('*') < 0 && text.IndexOf('?') < 0)
		{
			return string.Equals(text, value, StringComparison.OrdinalIgnoreCase);
		}
		try
		{
			string text2 = "^" + Regex.Escape(text).Replace("\\*", ".*").Replace("\\?", ".") + "$";
			return Regex.IsMatch(value, text2, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}
		catch (Exception)
		{
			return false;
		}
	}
}
