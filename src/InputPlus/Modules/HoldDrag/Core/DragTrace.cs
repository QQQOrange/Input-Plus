#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
// HoldDrag · Core/DragTrace.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace InputPlus.Modules.HoldDrag;

/// <summary>
/// 拖动轨迹账本：只为一件事存在 —— 有人报"手停了，窗口还在动"时，把责任分清楚。
///
/// 拖动期间每 8ms 采一次「光标 / 窗口真实矩形 / 我们这一帧请求的矩形 / offset /
/// 写窗口累计次数 / 收到的移动事件累计 / 100ms 补帧累计 / 吸附级别」，
/// 有任何一项变化记一行，没变化每 250ms 记一行；松手之后再采 1200ms，覆盖"松手以后"那段。
/// 一份轨迹追加到数据目录的 drag-trace.txt，保留最近 <see cref="MaxBlocks"/> 次拖动。
///
/// 判读（这是它唯一的价值）：
///   矩形在动、写次数没涨   → 不是我们干的，第三方在动这个窗口；
///   写次数在涨、光标没动   → 我们自己算错了（offset 变了 / 吸附改了口径）；
///   光标在动、事件数没涨   → 光标自己在动（驱动平滑、SetCursorPos），我们只是跟着它；
///   光标在动、事件数也在涨 → 真实输入没停（手、触摸板惯性、远程桌面回传的平滑光标）。
/// </summary>
internal static class DragTrace
{
	/// <summary>一次采样的快照。全部是值，跨线程传不会撕裂引用。</summary>
	internal struct Sample
	{
		public bool Ok;

		public Native.POINT Cursor;

		public Native.RECT Rect;

		/// <summary>DWM 扩展边框（DWMWA_EXTENDED_FRAME_BOUNDS）：屏幕上真正画出来的那圈。
		/// 它和 GetWindowRect 不一样时，说明画面和窗口矩形已经分家了。</summary>
		public Native.RECT Frame;

		public bool FrameOk;

		public Native.POINT Event;

		public Native.RECT Request;

		public bool RequestOk;

		public int OffsetX;

		public int OffsetY;

		public long Writes;

		public long Events;

		public long TimerFrames;

		public int Snap;

		public bool Move;

		public bool Locked;
	}

	private const string FileName = "drag-trace.txt";

	/// <summary>采样间隔。系统时钟粒度 15.6ms 时会退化成每两个时钟片一次，够看。</summary>
	private const int IntervalMs = 8;

	/// <summary>松手之后还采多久 —— "跑一段距离"通常就发生在这段时间里。</summary>
	private const int TailMs = 3000;

	private const int MaxLines = 2400;

	/// <summary>文件里保留最近几次拖动（覆盖写会盖掉上一次，而"跑一段"往往隔几次才撞见）。</summary>
	private const int MaxBlocks = 4;

	private static readonly object Gate = new object();

	/// <summary>全进程共用的单调时钟，用来把 End 的时刻和采样时刻放进同一条时间轴。</summary>
	private static readonly Stopwatch Clock = Stopwatch.StartNew();

	private static int _gen;

	private static long _endedAt = -1;

	private static string _path = "";

	/// <summary>起拖状态那一行（移动/缩放、起手是否为最大化、抽帧、吸附基线、起点矩形、窗口类名）。</summary>
	private static string _header = "";

	/// <summary>起一份新轨迹。上一份还没写完就被顶掉（连着拖两次），只留新的。</summary>
	public static void Begin(string path, nint hwnd, Func<nint, Sample> sampler, string header = null)
	{
		if (string.IsNullOrEmpty(path) || sampler == null)
		{
			return;
		}
		Thread thread;
		int gen;
		lock (Gate)
		{
			_gen++;
			gen = _gen;
			_path = path;
			_header = header ?? "";
			_endedAt = -1;
			ThreadStart start = delegate
			{
				Run(gen, hwnd, sampler);
			};
			thread = new Thread(start)
			{
				IsBackground = true,
				Name = "HoldDrag.DragTrace",
				Priority = ThreadPriority.BelowNormal
			};
		}
		try
		{
			thread.Start();
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}

	/// <summary>拖动结束：再采 <see cref="TailMs"/> 就收工写盘。</summary>
	public static void End()
	{
		lock (Gate)
		{
			if (_endedAt < 0)
			{
				_endedAt = Clock.ElapsedMilliseconds;
			}
		}
	}

	/// <summary>轨迹文件名（跟着诊断账本放同一目录）。</summary>
	public static string PathFor(string diagPath)
	{
		if (string.IsNullOrEmpty(diagPath))
		{
			return "";
		}
		try
		{
			string directoryName = System.IO.Path.GetDirectoryName(diagPath);
			return string.IsNullOrEmpty(directoryName) ? "" : System.IO.Path.Combine(directoryName, FileName);
		}
		catch (Exception ex)
		{
			Log.Error(ex);
			return "";
		}
	}

	private static void Run(int gen, nint hwnd, Func<nint, Sample> sampler)
	{
		List<string> lines = new List<string>();
		try
		{
			long t0 = Clock.ElapsedMilliseconds;
			long lastLineAt = long.MinValue;
			Sample prev = default(Sample);
			bool first = true;
			while (true)
			{
				Thread.Sleep(IntervalMs);
				if (gen != Volatile.Read(ref _gen))
				{
					// 被下一次拖动接管：这份不写了，文件归新的那份。
					return;
				}
				long endedAt;
				lock (Gate)
				{
					endedAt = _endedAt;
				}
				if (endedAt >= 0 && Clock.ElapsedMilliseconds - endedAt > TailMs)
				{
					break;
				}
				Sample sample = sampler(hwnd);
				long now = Clock.ElapsedMilliseconds - t0;
				if (!sample.Ok)
				{
					if (endedAt >= 0)
					{
						break;
					}
					continue;
				}
				bool fence = first || now - lastLineAt >= 250 || sample.Cursor.X != prev.Cursor.X || sample.Cursor.Y != prev.Cursor.Y || sample.Rect.Left != prev.Rect.Left || sample.Rect.Top != prev.Rect.Top || sample.Rect.Right != prev.Rect.Right || sample.Rect.Bottom != prev.Rect.Bottom || sample.Writes != prev.Writes || sample.Events != prev.Events || sample.TimerFrames != prev.TimerFrames || sample.Request.Left != prev.Request.Left || sample.Request.Top != prev.Request.Top || sample.Request.Right != prev.Request.Right || sample.Request.Bottom != prev.Request.Bottom || sample.RequestOk != prev.RequestOk || sample.OffsetX != prev.OffsetX || sample.OffsetY != prev.OffsetY || sample.Snap != prev.Snap || sample.Locked != prev.Locked || sample.FrameOk != prev.FrameOk || sample.Frame.Left != prev.Frame.Left || sample.Frame.Top != prev.Frame.Top || sample.Frame.Right != prev.Frame.Right || sample.Frame.Bottom != prev.Frame.Bottom;
				if (fence)
				{
					lines.Add(Format(now, sample, first ? "" : Note(prev, sample)));
					lastLineAt = now;
				}
				prev = sample;
				first = false;
				if (lines.Count > MaxLines)
				{
					lines.RemoveRange(0, MaxLines / 2);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
		Write(gen, lines);
	}

	/// <summary>这一行相对上一行发生了什么 —— 判读全靠它。</summary>
	private static string Note(Sample prev, Sample now)
	{
		StringBuilder sb = new StringBuilder();
		bool rectMoved = now.Rect.Left != prev.Rect.Left || now.Rect.Top != prev.Rect.Top || now.Rect.Right != prev.Rect.Right || now.Rect.Bottom != prev.Rect.Bottom;
		if (rectMoved)
		{
			sb.Append(now.Writes > prev.Writes ? " ←我们写的" : " ←矩形动了但我们没写");
		}
		if (now.Cursor.X != prev.Cursor.X || now.Cursor.Y != prev.Cursor.Y)
		{
			if (now.Events > prev.Events)
			{
				sb.Append(" ←光标动+有事件");
			}
			else
			{
				sb.Append(" ←光标动了但没有事件");
			}
		}
		else if (now.Events > prev.Events)
		{
			sb.Append(" ←有事件、光标没动");
		}
		if (now.FrameOk && prev.FrameOk && !rectMoved && (now.Frame.Left != prev.Frame.Left || now.Frame.Top != prev.Frame.Top))
		{
			sb.Append(" ←框在动、矩没动（画面在动）");
		}
		if (now.Writes == prev.Writes && now.TimerFrames > prev.TimerFrames)
		{
			sb.Append(" ←补帧没写成");
		}
		return sb.ToString();
	}

	private static string Format(long ms, Sample s, string note)
	{
		StringBuilder sb = new StringBuilder(160);
		sb.Append('+').Append(ms.ToString("D6", CultureInfo.InvariantCulture)).Append("ms");
		sb.Append(" 光=").Append(s.Cursor.X).Append(',').Append(s.Cursor.Y);
		sb.Append(" 矩=").Append(s.Rect.Left).Append(',').Append(s.Rect.Top).Append(' ').Append(s.Rect.Width).Append('x').Append(s.Rect.Height);
		if (s.RequestOk)
		{
			sb.Append(" 求=").Append(s.Request.Left).Append(',').Append(s.Request.Top).Append(' ').Append(s.Request.Width).Append('x').Append(s.Request.Height);
		}
		if (s.FrameOk)
		{
			sb.Append(" 框=").Append(s.Frame.Left).Append(',').Append(s.Frame.Top).Append(' ').Append(s.Frame.Width).Append('x').Append(s.Frame.Height);
		}
		sb.Append(" 偏=").Append(s.OffsetX).Append(',').Append(s.OffsetY);
		sb.Append(" 写=").Append(s.Writes);
		sb.Append(" 事=").Append(s.Events);
		sb.Append(" 补=").Append(s.TimerFrames);
		sb.Append(" 吸=").Append(s.Snap);
		sb.Append(s.Move ? " 移" : " 缩");
		if (s.Locked)
		{
			sb.Append(" 锁");
		}
		sb.Append(note);
		return sb.ToString();
	}

	private static void Write(int gen, List<string> lines)
	{
		try
		{
			string path;
			lock (Gate)
			{
				if (gen != _gen)
				{
					return;
				}
				path = _path;
			}
			if (string.IsNullOrEmpty(path) || lines.Count == 0)
			{
				return;
			}
			StringBuilder sb = new StringBuilder(64 * 1024);
			sb.AppendLine("# HoldDrag 拖动轨迹 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "（保留最近 " + MaxBlocks + " 次拖动）");
			sb.AppendLine("# 8ms 采样，受系统时钟粒度影响实际约 8~16ms；有变化才记一行，没变化每 250ms 记一行；松手后再采 3000ms");
			sb.AppendLine("# 光=光标 矩=窗口真实矩形 框=DWM 扩展边框（屏幕上的实际画面） 求=这一帧请求的矩形 偏=offset 写=写窗口累计 事=收到的移动事件累计 补=100ms 补帧累计 吸=吸附级别");
			string header;
			lock (Gate)
			{
				header = _header;
			}
			if (header.Length > 0)
			{
				sb.AppendLine("# 起拖：" + header);
			}
			for (int i = 0; i < lines.Count; i++)
			{
				sb.AppendLine(lines[i]);
			}
			// 保留最近几次：读回旧文件，按表头切开，丢最旧的，再把这一次接在后面。
			string merged = sb.ToString();
			try
			{
				if (File.Exists(path))
				{
					string old = File.ReadAllText(path, Encoding.UTF8);
					string[] parts = old.Replace("\r\n", "\n").Split(new string[1] { "# HoldDrag 拖动轨迹 " }, StringSplitOptions.None);
					int first = Math.Max(1, parts.Length - MaxBlocks + 1);
					if (first < parts.Length)
					{
						StringBuilder head = new StringBuilder(old.Length);
						for (int k = first; k < parts.Length; k++)
						{
							if (k > first)
							{
								head.Append('\n');
							}
							head.Append("# HoldDrag 拖动轨迹 ").Append(parts[k].TrimEnd('\n'));
						}
						merged = head.Append("\n\n").Append(merged).ToString();
					}
				}
			}
			catch (Exception ex2)
			{
				Log.Error(ex2);
			}
			File.WriteAllText(path, merged, new UTF8Encoding(false));
		}
		catch (Exception ex)
		{
			Log.Error(ex);
		}
	}
}
