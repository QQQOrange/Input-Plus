#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
// HoldDrag · Platform/HookProbe.cs
//
// 现场探针：只有环境变量 HOLDRAG_PROBE=1 时才写文件，写完立刻 flush。
// 存在的理由：「按住拖拽按了没反应」这种问题，用户能看到的现象只有"没反应"，
// 而钩子有没有收到键、触发键有没有转正、窗口有没有被判定成黑名单，
// 这些中间状态一旦不在现场落地，就只剩猜。
//
// 与 Log 的区别：Log 是异步队列（可能因为进程直接退出而丢掉最后几行），
// 探针是同步落盘 —— 现场崩溃、直接 Environment.Exit 都不会丢。

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace InputPlus.Modules.HoldDrag;

internal static class HookProbe
{
	private static readonly object Gate = new object();

	private static readonly bool _enabled = Environment.GetEnvironmentVariable("HOLDRAG_PROBE") == "1";

	private static string _path;

	private static StreamWriter _writer;

	/* 连续失败计数。
	 *
	 * 早先是"一次失败就彻底静音"—— 结果实测里探针跑到一半突然没了下文：
	 * 文件被外部工具短暂占用（我们自己 rm 探针文件、编辑器扫描）就触发一次 IOException，
	 * 之后整轮实验都变成盲测，白跑。现在改成失败就重建 writer 再试，
	 * 只有连续失败太多次才放弃。 */
	private static int _failStreak;

	public static bool Enabled => _enabled;

	public static string Path => _path ?? string.Empty;

	public static void W(string msg)
	{
		if (!_enabled || Volatile.Read(ref _failStreak) > 200)
		{
			return;
		}
		try
		{
			lock (Gate)
			{
				if (_writer == null)
				{
					_path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "holddrag-probe-" + Environment.ProcessId + ".log");
					_writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
					{
						AutoFlush = true
					};
					_writer.WriteLine("===== 探针启动 pid=" + Environment.ProcessId + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " =====");
				}
				_writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + Environment.CurrentManagedThreadId + "] " + msg);
			}
		}
		catch
		{
			// 探针写不进去绝不能影响拖拽本身：丢掉当前 writer，下次重开。
			Interlocked.Increment(ref _failStreak);
			try
			{
				lock (Gate)
				{
					_writer?.Dispose();
					_writer = null;
				}
			}
			catch
			{
				_writer = null;
			}
		}
	}
}
