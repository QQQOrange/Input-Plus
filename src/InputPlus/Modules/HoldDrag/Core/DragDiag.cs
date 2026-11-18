#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
// HoldDrag · Core/DragDiag.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InputPlus.Modules.HoldDrag;

/// <summary>
/// 拖动链路的明文诊断账本：每次拖动结束记一行，落在"数据目录\drag-diag.txt"。
///
/// 为什么另起一份而不是用 <see cref="Log"/>：Log 要点开 Verbose 才有细节，
/// 而这份东西是给"用户说手停了窗口还在跑"这种反馈用的 —— 一行就能读出
/// 链路积压多少帧、目标程序写一次要多久、最后一帧到底有没有落到光标上。
/// 只记数字，不记窗口标题，滚动保留最近 200 行。
/// </summary>
internal static class DragDiag
{
	private const string FileName = "drag-diag.txt";

	private const int MaxLines = 400;

	private const int KeepLines = 200;

	private static readonly object Gate = new object();

	private static string _dir = "";

	/// <summary>账本文件路径；还没 <see cref="Init"/> 过就是空串。</summary>
	public static string Path { get; private set; } = "";

	public static void Init(string dir)
	{
		lock (Gate)
		{
			_dir = dir ?? "";
			Path = (_dir.Length == 0) ? "" : System.IO.Path.Combine(_dir, FileName);
		}
	}

	/// <summary>记一行。没开账本（或写盘失败）就静默跳过 —— 拖动不能因为写日志失败而中断。</summary>
	public static void Note(string line)
	{
		lock (Gate)
		{
			if (Path.Length == 0 || string.IsNullOrEmpty(line))
			{
				return;
			}
			try
			{
				Directory.CreateDirectory(_dir);
				Rotate();
				string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine;
				File.AppendAllText(Path, text, Encoding.UTF8);
			}
			catch
			{
				// 账本只是排查工具，写不进去就不写。
			}
		}
	}

	private static void Rotate()
	{
		try
		{
			if (!File.Exists(Path))
			{
				return;
			}
			string[] array = File.ReadAllLines(Path);
			if (array.Length < MaxLines)
			{
				return;
			}
			int num = Math.Max(0, array.Length - KeepLines);
			List<string> list = new List<string>(KeepLines + 1);
			for (int i = num; i < array.Length; i++)
			{
				list.Add(array[i]);
			}
			File.WriteAllLines(Path, list, Encoding.UTF8);
		}
		catch
		{
		}
	}
}
