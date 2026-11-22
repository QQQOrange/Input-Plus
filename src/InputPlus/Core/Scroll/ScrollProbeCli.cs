using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using InputPlus.Core.Input;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;

namespace InputPlus.Core.Scroll;

/// <summary>
/// `--scroll-probe`：实测"滚轮事件到底有没有进到平滑滚动"。
///
/// 为什么要这个：平滑滚动是纯手感功能，单测只能证明算法；用户报"开了没效果"时
/// 必须分清是三件事中的哪一件：
///   1. 鼠标钩子压根没收到滚轮（钩子冲突/被摘）；
///   2. 收到了但被判成"自己注入/触摸板/例外程序"而放行；
///   3. 平滑了、也注入了，但目标程序不认注入的滚轮。
/// 这里自己装一条鼠标钩子 + 一个 SmoothScrollService，然后**发不带注入标记**的
/// 滚轮（等价于用户手拨），把三组计数打在报告里。
///
/// 注意：会真的向前台窗口滚 8 格（约一屏），诊断完自行滚回去 4 格抵消一部分。
/// 不读写用户配置，只读。
/// </summary>
internal static class ScrollProbeCli
{
    public static int Run()
    {
        var report = new StringBuilder();
        int exit = 0;
        KeyboardHook? hook = null;
        try
        {
            var settings = SettingsStore.Load();
            var o = settings.Scroll ?? new ScrollOptions();
            bool userEnabled = o.Enabled;
            report.AppendLine("InputPlus 平滑滚动探针");
            report.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("数据目录：" + AppPaths.DataDir);
            report.AppendLine("设置：Enabled=" + o.Enabled + " Step=" + o.StepSize + " Anim=" + o.AnimationTime
                              + " Pulse=" + o.PulseScale + " Accel=" + o.Acceleration
                              + " IgnoreTouchpad=" + o.IgnoreTouchpad + " Horizontal=" + o.Horizontal);
            report.AppendLine("例外程序：" + (settings.ScrollApps?.Count ?? 0) + " 条");
            report.AppendLine("前台程序：" + ForegroundWatcher.ProcessNameOf(ForegroundWatcher.ForegroundWindow()));
            report.AppendLine();

            /* 探针自己的服务必须关掉：否则"本进程的平滑滚动"也会发注入滚轮，
             * 就分不清看到的注入事件是"正在运行的那个 Input Plus"还是探针自己。
             * 关掉之后，仍然收到带 CLP! 标记的滚轮，就说明**别的 Input Plus 实例**
             * 正在平滑（这正是我们要验证的）。 */
            o.Enabled = false;

            hook = new KeyboardHook();
            using var service = new SmoothScrollService(() => settings);
            service.Attach(hook);
            hook.Start(mouseOnly: true);
            Thread.Sleep(400);

            report.AppendLine("鼠标钩子安装：" + (hook.MouseHookInstalled ? "是" : "否"));
            if (!hook.MouseHookInstalled) exit = 2;

            long wheel0 = hook.WheelEventCount;
            long physical0 = hook.PhysicalWheelEventCount;
            long injected0 = hook.InjectedWheelEventCount;
            long smooth0 = service.SmoothCount;
            long pixels0 = service.SentPixels;

            // 发 8 格"非本程序标记"的滚轮（等于用户手拨）
            for (int i = 0; i < 8; i++)
            {
                SendWheel(120);
                Thread.Sleep(70);
            }
            Thread.Sleep(900);

            long wheel = hook.WheelEventCount - wheel0;
            long physical = hook.PhysicalWheelEventCount - physical0;
            long injected = hook.InjectedWheelEventCount - injected0;
            long smooth = service.SmoothCount - smooth0;
            long pixels = service.SentPixels - pixels0;

            report.AppendLine("注入滚轮：8 次（每次 +120，dwExtraInfo=0；探针自己的平滑已关闭）");
            report.AppendLine("钩子收到滚轮：" + wheel + " 次（手拨/无标记 " + physical + "，带本程序标记 " + injected + "）");
            report.AppendLine("探针自己的平滑接管：" + smooth + " 次，注入 " + pixels + " 像素（应为 0）");
            report.AppendLine("—— 「带本程序标记」若明显多于 8，说明**正在运行的 Input Plus** 正在平滑并注入。");
            report.AppendLine();
            report.AppendLine("未被接管的分类计数（累计，含探针之前的真实滚轮）：");
            report.AppendLine("  判成自己注入：" + service.RejectedInjected);
            report.AppendLine("  设置关闭：" + service.RejectedDisabled);
            report.AppendLine("  触发键绑定占用：" + service.RejectedBinding);
            report.AppendLine("  程序例外禁用：" + service.RejectedRule);
            report.AppendLine("  触摸板细碎 delta：" + service.RejectedTouchpad);

            if (physical == 0 && injected <= 8)
            {
                report.AppendLine();
                report.AppendLine("结论：钩子没收到手拨滚轮，也没看到正在运行的 Input Plus 注入 —— 检查其它鼠标增强软件/钩子被摘。");
                exit = 3;
            }
            else if (injected > 8)
            {
                report.AppendLine();
                report.AppendLine("结论：正在运行的 Input Plus 平滑滚动链路正常（" + injected + " 个带标记滚轮）。");
            }
            else if (userEnabled)
            {
                report.AppendLine();
                report.AppendLine("结论：设置里开着，但没看到任何带标记的注入滚轮 —— 正在运行的实例没接管。");
                exit = 4;
            }
            else
            {
                report.AppendLine();
                report.AppendLine("结论：设置里平滑滚动是关的，探针只验证了钩子能收到滚轮。");
            }

            // 往回滚 4 格，减少对用户当前窗口的打扰
            for (int i = 0; i < 4; i++) { SendWheel(-120); Thread.Sleep(60); }
        }
        catch (Exception ex)
        {
            report.AppendLine("探针异常：" + ex);
            exit = 1;
        }
        finally
        {
            try { hook?.Dispose(); } catch { }
        }

        string text = report.ToString();
        try { Console.Out.Write(text); Console.Out.Flush(); } catch { }
        try
        {
            string path = CliOutput.ReportPath("scroll-probe.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            try { Console.Out.WriteLine("报告：" + path); } catch { }
        }
        catch (Exception ex) { CoreLog.Warn("写平滑滚动探针报告失败：" + ex.Message); }
        return exit;
    }

    private static void SendWheel(int delta)
    {
        var input = new Interop.NativeMethods.INPUT
        {
            type = Interop.NativeMethods.INPUT_MOUSE,
            U = new Interop.NativeMethods.InputUnion
            {
                mi = new Interop.NativeMethods.MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = Interop.NativeMethods.MOUSEEVENTF_WHEEL,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        Interop.NativeMethods.SendInput(1, new[] { input },
            Marshal.SizeOf<Interop.NativeMethods.INPUT>());
    }
}
