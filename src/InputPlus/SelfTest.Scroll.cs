using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using InputPlus.Core.Input;
using InputPlus.Core.Scroll;
using InputPlus.Core.Settings;

namespace InputPlus;

/// <summary>
/// 自检：平滑滚动的曲线、取整余量、多段叠加与加速。
///
/// 这里的用例**不真的滚**：注入口被 <see cref="SmoothScrollService.SendForTest"/>
/// 换成一个收集器，所以跑自检不会把桌面滚跑。只有两条带等待的用例是走真实定时器的
/// （"一段动画加起来正好等于目标像素"、"三格连滚加起来正好三格"）。
///
/// 余量和叠加这两组全是对 <see cref="ScrollPulses"/> 的纯函数断言：自己给"现在几点"，
/// 几十帧一次跑完，不依赖机器快慢 —— 否则在慢机器上就会变成随机失败。
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

        /* ---- 与原生引擎的曲线公式逐点对照 ----
         * 原版反编译出来的 FUN_14005d210 就是这个两段式指数：
         *   x *= pulseScale;
         *   x < 1 ? x - (1 - exp(-x))
         *         : exp(-1) + (1 - exp(-1)) * (1 - exp(-(x-1)));
         * 再乘 pulseNormalize，帧里再按 1/pulse(1) 归一化。
         * 曲线我们没动，这条是给"以后有人想顺手改曲线"留的护栏。 */
        static double OriginalPulse(double x, double scale)
        {
            double v = x * scale;
            if (v >= 1)
            {
                double start = Math.Exp(-1);
                return start + (1 - start) * (1 - Math.Exp(-(v - 1)));
            }
            return v - (1 - Math.Exp(-v));
        }

        double curveDiff = 0;
        for (int i = 0; i <= 200; i++)
        {
            double t = i / 200.0;
            double theirs = OriginalPulse(t, 4) / OriginalPulse(1, 4);
            curveDiff = Math.Max(curveDiff, Math.Abs(ScrollMath.Ease(t, 4) - theirs));
        }
        Check("平滑滚动曲线：与原版引擎公式逐点一致（201 个采样点）", curveDiff < 1e-12,
            curveDiff.ToString("0.0e0"));

        // ---- 换算：一格 = stepSize 像素 ----
        Check("换算：一格 120 像素", Math.Abs(ScrollMath.PixelsFor(120, 120, 1, false) - 120) < 1e-9);
        Check("换算：下滚是负数", Math.Abs(ScrollMath.PixelsFor(-120, 120, 1, false) + 120) < 1e-9);
        Check("换算：加速 3 倍", Math.Abs(ScrollMath.PixelsFor(120, 120, 3, false) - 360) < 1e-9);
        Check("换算：反转方向", Math.Abs(ScrollMath.PixelsFor(120, 120, 1, true) + 120) < 1e-9);
        Check("换算：半格按比例", Math.Abs(ScrollMath.PixelsFor(60, 120, 1, false) - 60) < 1e-9);

        // ---- 分帧取整：总和必须正好等于目标 ----
        int total = 0;
        double sent = 0;
        bool firstFrame = true;
        const int target = 137;   // 故意用不能整除的数，考验余量保留
        for (int i = 0; i <= 45; i++)
        {
            double t = i / 45.0;
            int delta = ScrollMath.FrameDelta(target, sent, ScrollMath.Ease(t, 4), firstFrame);
            firstFrame = false;
            total += delta;
            sent += delta;
        }
        CheckEqual("分帧取整：一段动画加起来正好等于目标像素", total, target);

        /* ---- 取整模型：原版的 ∓0.99 起手偏置 / 末帧补齐 / 不许回跳 ----
         * 这三条是"越滚越偏"和"往上滚很快、往下滚慢半拍"的根。 */

        /* 原版把还没开始动的"已发量"设成 -0.99（正方向），于是第一帧的 ceil 就从
         * 1 像素变成 2 像素；整段走完**净多滚 1 像素**，而且只有正方向多 ——
         * 同样的格数上下滚会失衡。这里把那个代价量出来，免得以后有人"照原版抄回去"。 */
        static int RunOriginalBias(double target, double bias, int frameMs, double duration)
        {
            double sent = bias;
            int sum = 0;
            for (long t = 0; ; t += frameMs)
            {
                if (t >= duration) return sum + (int)Math.Ceiling(target - sent);
                int d = (int)Math.Ceiling(target * ScrollMath.Ease(t / duration, 4) - sent);
                sum += d;
                sent += d;
            }
        }

        CheckEqual("取整模型：原版的起手偏置让正方向每格多滚 1 像素（120 → 121）",
            RunOriginalBias(120, -ScrollMath.FirstFrameBias, 16, 360), 121);
        CheckEqual("取整模型：我们的取整让一格就是 120，不多不少",
            RunPulses(120, Enumerable.Repeat(16, 30), 360), 120);

        /* 我们只在第一帧真的算出 0 时才补 1 像素，而且末端补齐会把它收回去。 */
        CheckEqual("取整模型：第一帧算出 0 时补一个像素（向下滚不再慢半拍）",
            ScrollMath.FrameDelta(-120, 0, 0.0001, firstFrame: true), -1);
        CheckEqual("取整模型：补过之后不会在下一帧往回跳",
            ScrollMath.FrameDelta(-120, -1, 0.002, firstFrame: false), 0);
        CheckEqual("取整模型：末帧把剩下的零头一次补齐",
            ScrollMath.FrameDelta(137, 135.4, 1, firstFrame: false), 2);

        /* 同样的 137 像素、不同的帧率、中间还卡一下：末帧补齐之后必须分毫不差。 */
        static int RunPulses(double pixels, IEnumerable<int> frameGaps, double duration)
        {
            var pulses = new List<ScrollPulse>();
            ScrollPulses.Add(pulses, pixels, 0, duration);

            int sum = 0;
            long now = 0;
            foreach (int gap in frameGaps)
            {
                now += gap;
                sum += ScrollPulses.Step(pulses, now, 4);
            }

            /* 兜底：再多跑几帧，确保一定走完（走完的段会被移除，这个循环必然结束）。 */
            while (pulses.Count > 0)
            {
                now += 16;
                sum += ScrollPulses.Step(pulses, now, 4);
            }
            return sum;
        }

        var framings = new (string Name, int[] Gaps)[]
        {
            ("8 毫秒一帧", Enumerable.Repeat(8, 60).ToArray()),
            ("16 毫秒一帧（60Hz）", Enumerable.Repeat(16, 30).ToArray()),
            ("33 毫秒一帧（30Hz）", Enumerable.Repeat(33, 20).ToArray()),
            ("中途卡了 120 毫秒", new[] { 8, 8, 8, 120, 8, 8, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16 }),
        };

        foreach (var (name, gaps) in framings)
        {
            int got = RunPulses(120, gaps, 360);
            Check($"余量：一格 120 像素、{name}，累计也不差分毫（误差 < 1 像素）",
                Math.Abs(got - 120) < 1, got.ToString());
        }

        int odd = RunPulses(137, Enumerable.Repeat(16, 30), 360);
        CheckEqual("余量：137 像素这种除不尽的也不丢零头", odd, 137);

        /* ---- 像素层余量：步长写成小数时，零头必须带到下一格 ----
         * 界面允许把步长填成 137.5（double.TryParse），加速倍数也是小数。
         * 每格四舍五入的话十格就差 5 像素，滚久了就是"比原生滚得远"。 */
        double pixelCarry = 0;
        int pixelSum = 0;
        for (int i = 0; i < 10; i++) pixelSum += SmoothScrollService.TakeWholePixels(137.5, ref pixelCarry);
        CheckEqual("余量：步长 137.5 连滚 10 格，累计正好 1375 像素（零头不丢）", pixelSum, 1375);
        Check("余量：进位之后残留的零头不超过半像素", Math.Abs(pixelCarry) <= 0.5 + 1e-9,
            pixelCarry.ToString("0.###"));

        double downCarry = 0;
        int downSum = 0;
        for (int i = 0; i < 10; i++) downSum += SmoothScrollService.TakeWholePixels(-137.5, ref downCarry);
        CheckEqual("余量：反向也一样不丢零头（-1375）", downSum, -1375);

        /* 不到一像素的位移先攒着，攒够再发：5 次 0.4 合计 2 像素，
         * 一次都不许凭空多发（否则慢速精密滚轮会滚得比手快）。 */
        double tinyCarry = 0;
        int tinySum = 0;
        for (int i = 0; i < 5; i++) tinySum += SmoothScrollService.TakeWholePixels(0.4, ref tinyCarry);
        CheckEqual("余量：5 次 0.4 像素合计正好 2 像素（不够一像素的先攒着）", tinySum, 2);

        /* ---- 多段叠加：这是"连滚不如原版丝滑"的正主 ----
         * 原版每一格单开一段动画，每帧把各段求和发出去；我们以前是"一条动画，
         * 每来一格就把时间线重置"。前者的速度会叠上去，后者每格都重新起步。 */
        var stacked = new List<ScrollPulse>();
        ScrollPulses.Add(stacked, 120, 0, 360);
        ScrollPulses.Add(stacked, 120, 0, 360);
        ScrollPulses.Add(stacked, 120, 0, 360);
        int firstSum = ScrollPulses.Step(stacked, 8, 4);

        var single = new List<ScrollPulse>();
        ScrollPulses.Add(single, 120, 0, 360);
        int firstOne = ScrollPulses.Step(single, 8, 4);

        Check("叠加：三格同时到达时，第一帧的位移是单格的三倍（合成一帧更大的位移）",
            firstSum >= firstOne * 3, $"{firstSum} vs {firstOne}");

        var staggered = new List<ScrollPulse>();
        ScrollPulses.Add(staggered, 120, 0, 360);
        ScrollPulses.Add(staggered, 120, 8, 360);
        ScrollPulses.Add(staggered, 120, 16, 360);

        int staggeredSum = 0;
        for (long now = 8; now <= 500; now += 8) staggeredSum += ScrollPulses.Step(staggered, now, 4);
        CheckEqual("叠加：三格错开 8 毫秒到达，累计正好 360 像素（时间线没有被重置）",
            staggeredSum, 360);

        /* 新一格到达时，**旧那一段的已发量不许被清零** —— 清零就是老代码"每格重新
         * 起步"的根源：用户看到的是滚到一半忽然减速、再慢慢加回来。 */
        var noReset = new List<ScrollPulse>();
        ScrollPulses.Add(noReset, 120, 0, 360);
        for (long now = 8; now <= 120; now += 8) ScrollPulses.Step(noReset, now, 4);
        double sentBefore = noReset[0].Sent;
        ScrollPulses.Add(noReset, 120, 120, 360);
        CheckEqual("叠加：新一格到达时，旧那一段的已发量原封不动", noReset[0].Sent, sentBefore);
        ScrollPulses.Step(noReset, 128, 4);
        Check("叠加：旧那一段接着走自己的时间线（已发量继续增长，不是从头再来）",
            noReset[0].Sent > sentBefore, $"{sentBefore} → {noReset[0].Sent}");

        /* ---- 方向反转：叠加相消，而不是"把上一段清零再反向" ---- */
        var reverse = new List<ScrollPulse>();
        for (int i = 0; i < 3; i++) ScrollPulses.Add(reverse, 120, i * 16, 360);          // 先上 3 格
        for (int i = 0; i < 3; i++) ScrollPulses.Add(reverse, -120, 160 + i * 16, 360);   // 10 帧后反向下 3 格

        int reverseNet = 0;
        bool upMonotonic = true;
        int framesToFlip = -1;
        int firstAfterReversal = 0;
        bool gotFirstAfter = false;
        int frame = 0;
        for (long now = 8; now <= 900; now += 8)
        {
            int d = ScrollPulses.Step(reverse, now, 4);
            reverseNet += d;
            frame++;

            if (now < 160)
            {
                /* 反转之前：累计位移必须一直朝上（取整误差不许把方向拧过来）。 */
                if (d < 0) upMonotonic = false;
            }
            else
            {
                if (!gotFirstAfter) { firstAfterReversal = d; gotFirstAfter = true; }
                if (d < 0 && framesToFlip < 0) framesToFlip = frame;
            }
        }

        Check("反转：反转之前的每一帧都朝上（累计位移单调，不回跳）", upMonotonic);
        CheckEqual("反转：三格上 + 三格下，净位移归零（两段各自都精确落地）", reverseNet, 0);

        /* 原版是"旧那段继续衰减、新那段从头加速"，两段相消，所以反向后**头一帧
         * 不会立刻倒车**（硬切到新方向 = 用户感觉被拽了一下）。
         * 这一条守着"没有 flush、没有清零重来"。 */
        Check("反转：反向后头一帧不会硬切到新方向（先相消，再反向）",
            gotFirstAfter && firstAfterReversal >= 0, firstAfterReversal.ToString());

        /* 相消必须在整段时长内完成：不能一直卡在旧方向。
         * 60 帧 = 480 毫秒，是 360 毫秒动画时长的 1.3 倍；实测在反向之后约 56 毫秒
         * （第 27 帧，绝对时间 216 毫秒）就出现了朝新方向的帧。 */
        Check("反转：相消在动画时长量级内完成（不会一直卡在旧方向）",
            framesToFlip > 0 && framesToFlip < 60, framesToFlip.ToString());

        /* 反向那一段自己也不许往回走：正方向上"先减速到 0 再反向"是错的。 */
        var oneWay = new List<ScrollPulse>();
        ScrollPulses.Add(oneWay, -120, 0, 360);
        bool downOnly = true;
        for (long now = 8; now <= 400; now += 8)
        {
            int d = ScrollPulses.Step(oneWay, now, 4);
            if (d > 0) downOnly = false;
        }
        Check("反转：单独一段向下滚的过程中没有任何一帧往上", downOnly);

        /* ---- 加速：原版是按间隔比例连续算，不是"每快一格加固定倍数" ---- */
        CheckEqual("加速：间隔等于门限时不加速", ScrollMath.AccelerationFactor(70, 70, 7), 1.0);
        CheckEqual("加速：间隔比门限还长时不加速", ScrollMath.AccelerationFactor(200, 70, 7), 1.0);
        CheckEqual("加速：间隔减半 → 1.5 倍", ScrollMath.AccelerationFactor(35, 70, 7), 1.5);
        CheckEqual("加速：同一毫秒两格 → 直接到上限", ScrollMath.AccelerationFactor(0, 70, 7), 7.0);
        CheckEqual("加速：倍数封顶", ScrollMath.AccelerationFactor(1, 70, 3), 3.0);
        CheckEqual("加速：上限为 1 时永远不加速", ScrollMath.AccelerationFactor(1, 70, 1), 1.0);

        // ---- 掉帧：卡顿之后的这一帧要把欠的位移补上 ----
        var stalled = new List<ScrollPulse>();
        ScrollPulses.Add(stalled, 120, 0, 360);
        int steady = 0;
        for (long now = 8; now <= 240; now += 8) steady = ScrollPulses.Step(stalled, now, 4);
        int afterStall = ScrollPulses.Step(stalled, 336, 4);   // 卡了 96 毫秒
        Check("掉帧：卡顿之后的这一帧位移明显变大（欠的位移自动补上）",
            afterStall > steady, $"{afterStall} vs {steady}");

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

        /* WPS 套件：同一个 office6 目录下的 wps / et / wpp 是一份安装。
         * 2026-11-26 报障：用户禁了 wps.exe，在 WPS 表格（et.exe）里照样平滑 ——
         * 因为 et 与 wps 既不同名也不是前缀，老的 SameApp 直接判"没这条规则"。 */
        const string office6 = @"D:\software\WPS Office\12.1.0.29690\office6\";
        var wpsRules = new List<ScrollAppRule>
        {
            new() { AppPath = office6 + "wps.exe", Enabled = false },
        };
        Check("例外匹配：禁了 wps 也管住同一套装的 et（表格）",
            SmoothScrollService.FindRule(wpsRules, office6 + "et.exe")?.Enabled == false);
        Check("例外匹配：禁了 wps 也管住 wpp（演示）",
            SmoothScrollService.FindRule(wpsRules, office6 + "wpp.exe")?.Enabled == false);
        Check("例外匹配：wps 与 wpspdf 算一家（同前缀族）",
            SmoothScrollService.FindRule(wpsRules, office6 + "wpspdf.exe")?.Enabled == false);
        Check("例外匹配：别处同名的 et.exe 不受牵连",
            SmoothScrollService.FindRule(wpsRules, @"D:\tools\et.exe") == null);
        Check("例外匹配：另一份 WPS 安装不受牵连",
            SmoothScrollService.FindRule(wpsRules, @"C:\WPS\office6\et.exe") == null);

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
        /* HorizontalShift 关掉：跑自检时用户如果正好按着 Shift 滚，那一格会被
         * 转成横向并取反，这条断言就会莫名其妙失败。 */
        var runSettings = new AppSettings
        {
            Scroll = new ScrollOptions { Enabled = true, AnimationTime = 80, HorizontalShift = false },
        };
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

        // ---- 真实定时器 + 三格连滚：走完整链路也不能吃掉任何一格 ----
        var burstSettings = new AppSettings
        {
            Scroll = new ScrollOptions { Enabled = true, AnimationTime = 80, HorizontalShift = false },
        };
        var burstTotal = 0;
        SmoothScrollService.SendForTest = (delta, _) => Interlocked.Add(ref burstTotal, delta);

        try
        {
            using var burst = new SmoothScrollService(() => burstSettings);
            bool b1 = burst.OnWheel(120, false, false);
            bool b2 = burst.OnWheel(120, false, false);
            bool b3 = burst.OnWheel(120, false, false);
            Check("连滚：三格都被接管", b1 && b2 && b3);
            CheckEqual("连滚：接管计数 +3", burst.SmoothCount, 3L);

            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 2000 && Volatile.Read(ref burstTotal) != 360)
                Thread.Sleep(10);
            Thread.Sleep(150);

            CheckEqual("连滚：三格加起来正好 360 像素（没有哪一格被吃掉）",
                Volatile.Read(ref burstTotal), 360);
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

        /* ---- 四道闸（照上游）：目标程序 / 控件类 / 修饰键 / delta 口径 ----
         *
         * 用户 2026-11-23 报"滚轮失效"。上游 SmoothScroll 的两道硬闸我们一直没搬：
         *   ① `%System32%` 下的程序一律不平滑（伪代码里整段排队都在 `if 路径不含 System32` 内层）；
         *   ② Ctrl/Alt + 滚轮直接放行。
         * 再加上经典控件（EDIT/ListBox）吃 `delta/120` 整数除法这一条，
         * 就解释了"开了平滑之后某些程序滚不动"。
         */
        {
            var gateSettings = new AppSettings
            {
                Scroll = new ScrollOptions { Enabled = true, AnimationTime = 60, HorizontalShift = false },
            };
            var savedTarget = SmoothScrollService.DefaultTargetForTest;
            var savedKeys = SmoothScrollService.DefaultIsKeyDownForTest;
            SmoothScrollService.SendForTest = (_, _) => { };

            try
            {
                SmoothScrollService.DefaultIsKeyDownForTest = _ => false;

                SmoothScrollService.DefaultTargetForTest = ("Edit", @"C:\Apps\foo.exe");
                using (var s = new SmoothScrollService(() => gateSettings))
                    Check("闸：经典控件 Edit 放行（让它整格滚）", !s.OnWheel(120, false, false));

                SmoothScrollService.DefaultTargetForTest = ("Chrome_WidgetWin_1", @"C:\Windows\System32\notepad.exe");
                using (var s = new SmoothScrollService(() => gateSettings))
                    Check("闸：System32 下的程序放行（上游整块排除）", !s.OnWheel(120, false, false));

                SmoothScrollService.DefaultTargetForTest = ("Chrome_WidgetWin_1", @"C:\Apps\chrome.exe");
                using (var s = new SmoothScrollService(() => gateSettings))
                {
                    Check("闸：普通程序照常接管", s.OnWheel(120, false, false));
                    Check("delta 口径：60（半格）放行", !s.OnWheel(60, false, false));
                    Check("delta 口径：30（触摸板）放行", !s.OnWheel(30, false, false));
                    Check("delta 口径：100 的整数倍接管", s.OnWheel(100, false, false));
                    Check("delta 口径：240（两格合并）接管", s.OnWheel(240, false, false));
                }

                SmoothScrollService.DefaultIsKeyDownForTest = vk => vk == VK.Control;
                using (var s = new SmoothScrollService(() => gateSettings))
                    Check("闸：Ctrl+滚轮 放行给目标程序（缩放）", !s.OnWheel(120, false, false));

                SmoothScrollService.DefaultIsKeyDownForTest = vk => vk == VK.Menu;
                using (var s = new SmoothScrollService(() => gateSettings))
                    Check("闸：Alt+滚轮 放行给目标程序", !s.OnWheel(120, false, false));
            }
            finally
            {
                SmoothScrollService.DefaultTargetForTest = savedTarget;
                SmoothScrollService.DefaultIsKeyDownForTest = savedKeys;
                SmoothScrollService.SendForTest = null;
            }
        }

        /* ---- 纯函数：delta 像不像"鼠标滚一格"（上游 0x32 + 120/100 口径） ---- */
        Check("delta 判定：<50 一律不算滚轮格", !SmoothScrollService.LooksLikeNotchDelta(49));
        Check("delta 判定：120 算", SmoothScrollService.LooksLikeNotchDelta(120));
        Check("delta 判定：-120 算", SmoothScrollService.LooksLikeNotchDelta(-120));
        Check("delta 判定：240 算（合并的两格）", SmoothScrollService.LooksLikeNotchDelta(240));
        Check("delta 判定：100 算（部分驱动）", SmoothScrollService.LooksLikeNotchDelta(100));
        Check("delta 判定：60 不算", !SmoothScrollService.LooksLikeNotchDelta(60));

        /* ---- 回声校验：吞了却看不到注入回响 → 冷却三格，再自动回到接管 ----
         *
         * 第一版这里用的是 static 的"停接管 30 秒→5 分钟"，一次误判就让平滑消失
         * 好几分钟，连 `--scroll-probe` 都会跟着被污染。上游根本没有这种状态，
         * 它只回答"这一格能不能做"。现在改成按**格数**冷却，事件恢复就自然回正。
         */
        {
            var echoSettings = new AppSettings
            {
                Scroll = new ScrollOptions { Enabled = true, AnimationTime = 60, HorizontalShift = false },
            };
            var savedTarget = SmoothScrollService.DefaultTargetForTest;
            var savedKeys = SmoothScrollService.DefaultIsKeyDownForTest;
            SmoothScrollService.SendForTest = (_, _) => { };
            SmoothScrollService.DefaultTargetForTest = ("Chrome_WidgetWin_1", @"C:\Apps\chrome.exe");
            SmoothScrollService.DefaultIsKeyDownForTest = _ => false;

            try
            {
                /* ① 死链路：注入计数永远不动 */
                long deadEcho = 0;
                using (var dead = new SmoothScrollService(() => echoSettings)
                {
                    InjectedEcho = () => Volatile.Read(ref deadEcho),
                })
                {
                    dead.ResetEchoForTest();
                    Check("回声：第一格照常接管（还没到判定期）", dead.OnWheel(120, false, false));
                    Thread.Sleep(300);   // 超过 EchoDeadlineMs(150)
                    Check("回声：上一格没回响，这一格直接放行", !dead.OnWheel(120, false, false));
                    CheckEqual("回声：记账一次", dead.SwallowWithoutEcho, 1L);
                    Check("回声：冷却第 1 格放行", !dead.OnWheel(120, false, false));
                    Check("回声：冷却第 2 格放行", !dead.OnWheel(120, false, false));
                    Check("回声：冷却第 3 格放行", !dead.OnWheel(120, false, false));
                    Check("回声：冷却用完自动回到接管（不会永久躺平）", dead.OnWheel(120, false, false));
                }

                /* ② 活链路：回响每次都涨，就该一直吞（别把正常机器误判成死的） */
                long liveEcho = 0;
                using (var live = new SmoothScrollService(() => echoSettings)
                {
                    InjectedEcho = () => Interlocked.Increment(ref liveEcho),
                })
                {
                    live.ResetEchoForTest();
                    bool l1 = live.OnWheel(120, false, false);
                    Thread.Sleep(300);
                    bool l2 = live.OnWheel(120, false, false);
                    Check("回声：有回响就一直接管（不误伤正常机器）", l1 && l2);
                    CheckEqual("回声：有回响时不记这笔账", live.SwallowWithoutEcho, 0L);
                }
            }
            finally
            {
                SmoothScrollService.DefaultTargetForTest = savedTarget;
                SmoothScrollService.DefaultIsKeyDownForTest = savedKeys;
                SmoothScrollService.SendForTest = null;
            }
        }
    }
}
