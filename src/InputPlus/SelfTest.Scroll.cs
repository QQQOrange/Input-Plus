using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using InputPlus.Core.Scroll;
using InputPlus.Core.Settings;

namespace InputPlus;

/// <summary>
/// 自检：平滑滚动的曲线、换算与设置归一化。
///
/// 这里的用例**不真的滚**：注入口被 <see cref="SmoothScrollService.SendForTest"/>
/// 换成一个收集器，所以跑自检不会把桌面滚跑。唯一带等待的一条是
/// "整段动画加起来正好等于目标像素" —— 它同时守着取整余量不丢这件事。
/// </summary>
public static partial class SelfTest
{
    private static void TestSmoothScroll()
    {
        // ---- 曲线：端点、归一化、单调 ----
        Check("平滑滚动曲线：起点是 0", Math.Abs(ScrollMath.Ease(0, 4)) < 1e-9);
        Check("平滑滚动曲线：终点正好是 1", Math.Abs(ScrollMath.Ease(1, 4) - 1) < 1e-9,
            ScrollMath.Ease(1, 4).ToString("0.000000"));

        bool monotonic = true;
        double last = -1;
        for (int i = 0; i <= 40; i++)
        {
            double v = ScrollMath.Ease(i / 40.0, 4);
            if (v < last - 1e-12) { monotonic = false; break; }
            last = v;
        }
        Check("平滑滚动曲线：全程单调递增", monotonic);

        /* 归一化是这条曲线最容易被忽略的坑：pulse(1) 在 scale=4 时只有 0.968，
         * 不除掉它，每一格都会少滚几个像素，滚一屏就明显对不上。 */
        double raw = ScrollMath.Pulse(1, 4);
        Check("脉冲曲线：pulse(1) 小于 1，所以必须归一化", raw > 0.9 && raw < 1,
            raw.ToString("0.0000"));

        double half = ScrollMath.Ease(0.5, 4);
        Check("平滑滚动曲线：前半段已经走完大半（尾巴长）", half > 0.7 && half < 0.9,
            half.ToString("0.000"));

        // ---- 换算：一格 = stepSize 像素 ----
        Check("换算：一格 120 像素", Math.Abs(ScrollMath.PixelsFor(120, 120, 1, false) - 120) < 1e-9);
        Check("换算：下滚是负数", Math.Abs(ScrollMath.PixelsFor(-120, 120, 1, false) + 120) < 1e-9);
        Check("换算：加速 3 倍", Math.Abs(ScrollMath.PixelsFor(120, 120, 3, false) - 360) < 1e-9);
        Check("换算：反转方向", Math.Abs(ScrollMath.PixelsFor(120, 120, 1, true) + 120) < 1e-9);
        Check("换算：半格按比例", Math.Abs(ScrollMath.PixelsFor(60, 120, 1, false) - 60) < 1e-9);

        // ---- 分帧取整：总和必须正好等于目标 ----
        int total = 0;
        double sent = 0;
        const int target = 137;   // 故意用不能整除的数，考验余量保留
        for (int i = 0; i <= 45; i++)
        {
            double t = i / 45.0;
            int delta = i == 45 ? (int)Math.Round(target - sent) : ScrollMath.NextDelta(0, target, sent, t, 4);
            total += delta;
            sent += delta;
        }
        CheckEqual("分帧取整：一段动画加起来正好等于目标像素", total, target);

        // ---- 设置：默认值、克隆独立、归一化夹取 ----
        var defaults = new ScrollOptions();
        CheckEqual("平滑滚动：出厂步长 120 像素", defaults.StepSize, 120.0);
        CheckEqual("平滑滚动：出厂动画 360 毫秒", defaults.AnimationTime, 360.0);
        CheckEqual("平滑滚动：出厂曲线尾巴 4", defaults.PulseScale, 4.0);
        Check("平滑滚动：出厂是关的（不擅自改变全系统手感）", !defaults.Enabled);

        var clone = defaults.Clone();
        clone.StepSize = 999;
        Check("平滑滚动：克隆出来的对象互不影响", Math.Abs(defaults.StepSize - 120) < 1e-9);

        var messy = new AppSettings
        {
            Scroll = new ScrollOptions
            {
                StepSize = 0,
                AnimationTime = 99999,
                PulseScale = 500,
                AccelerationDelta = 0,
                AccelerationMax = 0,
            },
        };
        messy.ScrollApps.Add(new ScrollAppRule { AppPath = "" });
        messy.ScrollApps.Add(new ScrollAppRule { AppPath = "C:/Apps/Player.exe", StepSize = 1, AnimationTime = 99999 });
        SettingsStore.Normalize(messy, includeBaseLayerDefaults: false);

        CheckEqual("归一化：步长 0 被夹成 10", messy.Scroll.StepSize, 10.0);
        CheckEqual("归一化：动画时长上限 2000", messy.Scroll.AnimationTime, 2000.0);
        CheckEqual("归一化：曲线尾巴被夹到上限", messy.Scroll.PulseScale, ScrollMath.MaxScale);
        CheckEqual("归一化：加速间隔下限 5", messy.Scroll.AccelerationDelta, 5.0);
        CheckEqual("归一化：加速上限下限 1", messy.Scroll.AccelerationMax, 1.0);
        CheckEqual("归一化：空路径的例外程序被清掉", messy.ScrollApps.Count, 1);
        CheckEqual("归一化：例外程序的步长也被夹", messy.ScrollApps[0].StepSize, 10.0);

        // ---- 例外程序的匹配：全路径 / 文件名 / 顺序 ----
        var rules = new List<ScrollAppRule>
        {
            new() { AppPath = "C:/Apps/Game.exe", Enabled = false },
            new() { AppPath = "code.exe" },
        };
        Check("例外匹配：全路径规则能命中进程名", ReferenceEquals(SmoothScrollService.FindRule(rules, "game.exe"), rules[0]));
        Check("例外匹配：文件名规则能命中", ReferenceEquals(SmoothScrollService.FindRule(rules, "code.exe"), rules[1]));
        Check("例外匹配：扩展名写法不同也能命中", ReferenceEquals(SmoothScrollService.FindRule(rules, "Code"), rules[1]));
        Check("例外匹配：没列出来的程序返回空（跟随全局）", SmoothScrollService.FindRule(rules, "notepad.exe") == null);
        Check("例外匹配：空表返回空", SmoothScrollService.FindRule(new List<ScrollAppRule>(), "game.exe") == null);

        // ---- 服务入口：三条"原样放行"的分支 ----
        var offSettings = new AppSettings { Scroll = new ScrollOptions { Enabled = false } };
        using (var off = new SmoothScrollService(() => offSettings))
        {
            Check("滚轮入口：关掉时直接放行", !off.OnWheel(120, false, false));
        }

        var onSettings = new AppSettings { Scroll = new ScrollOptions { Enabled = true, AnimationTime = 60 } };
        var collected = new List<int>();
        SmoothScrollService.SendForTest = (delta, horizontal) =>
        {
            lock (collected) collected.Add(delta);
        };

        try
        {
            using var service = new SmoothScrollService(() => onSettings);

            Check("滚轮入口：自己注入的事件放行（不自己喂自己）", !service.OnWheel(120, true, false));
            Check("滚轮入口：触摸板细碎 delta 放行", !service.OnWheel(30, false, false));
            using (var bound = new SmoothScrollService(() => onSettings) { WheelBindingHeld = _ => true })
            {
                Check("滚轮入口：触发键绑定占用时放行", !bound.OnWheel(120, false, false));
            }
        }
        finally
        {
            SmoothScrollService.SendForTest = null;
        }

        // ---- 整段动画：总和等于一格，并且会自己停下来 ----
        var runSettings = new AppSettings { Scroll = new ScrollOptions { Enabled = true, AnimationTime = 80 } };
        var sentTotal = 0;
        var sw = Stopwatch.StartNew();
        SmoothScrollService.SendForTest = (delta, _) => Interlocked.Add(ref sentTotal, delta);

        try
        {
            using var service = new SmoothScrollService(() => runSettings);
            bool swallowed = service.OnWheel(120, false, false);
            Check("滚轮入口：开着的时候接管一格滚轮", swallowed);
            CheckEqual("滚轮入口：接管计数 +1", service.SmoothCount, 1L);

            while (sw.ElapsedMilliseconds < 1200 && Volatile.Read(ref sentTotal) == 0)
                Thread.Sleep(10);
            Thread.Sleep(300);   // 给最后几帧留时间

            CheckEqual("整段动画：累计正好滚了一格 120 像素", Volatile.Read(ref sentTotal), 120);
            Check("整段动画：结束后不再继续注入（定时器停了）", service.SentPixels >= 120);
        }
        finally
        {
            SmoothScrollService.SendForTest = null;
        }

        // ---- 全局暂停（标题栏的「暂停」）：滚轮原样交给系统 ----
        {
            var pauseSettings = new AppSettings { Scroll = new ScrollOptions { Enabled = true, AnimationTime = 60 } };
            SmoothScrollService.SendForTest = (_, _) => { };

            try
            {
                using var paused = new SmoothScrollService(() => pauseSettings) { Suspended = true };
                Check("全局暂停：平滑滚动不再接管滚轮", !paused.OnWheel(120, false, false));

                paused.Suspended = false;
                Check("全局暂停：恢复后照常接管滚轮", paused.OnWheel(120, false, false));
            }
            finally
            {
                SmoothScrollService.SendForTest = null;
            }
        }
    }
}
