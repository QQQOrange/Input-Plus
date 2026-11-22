using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Gestures;
using InputPlus.UI.Windows;

namespace InputPlus.UI;

/// <summary>
/// 临时诊断（`--gesture-overlay-diag`）：量「手势轨迹到底什么时候才会出现在屏幕上」。
///
/// 三个场景，全部按运行期真实节奏（8ms 一次 AddPoint）驱动，每拍都采一次屏：
///   ① 正常一笔（320ms）：首帧延迟、可见像素的峰值；
///   ② 快划一笔（48ms 后立刻收笔）：整笔期间有没有被 present 过 —— 用户报的"没显示"；
///   ③ 连划两下：上一笔的淡出定时器（默认 131ms）会不会把下一笔 Hide 掉。
/// 结果写数据目录 gesture-overlay-diag.txt。
/// </summary>
internal static class GestureOverlayDiag
{
    private const string ColorHex = "#FF00FFFF";   // #RRGGBBAA：不透明洋红，采屏按这个颜色找

    private static readonly StringBuilder Log = new();
    private static readonly Stopwatch Clock = new();
    private static readonly GestureOptions Opts = new()
    {
        PathColorNormal = ColorHex,
        PathColorRecognized = ColorHex,
        PathLineWidth = 3,
        TrailOpacity = 1.0,
        ShowPathBorder = false,
        SmoothStrokes = false,
        ShowStartTimeoutIndicator = false,
    };

    private static GestureOverlayWindow? _overlay;
    private static AppHost? _host;

    public static void Start(AppHost host)
    {
        _host = host;
        Clock.Start();
        Log.AppendLine($"虚拟屏幕 {SystemParameters.VirtualScreenWidth:0}x{SystemParameters.VirtualScreenHeight:0} "
                       + $"@({SystemParameters.VirtualScreenLeft:0},{SystemParameters.VirtualScreenTop:0})");

        /* 先证明"采屏 + WPF 窗口能上屏"这条路是通的：贴一个小洋红方块再采它。 */
        var swatch = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Width = 40,
            Height = 40,
            Left = 20,
            Top = 20,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 0, 255)),
            Content = new Border(),
        };
        swatch.Show();
        Pump(300, () =>
        {
            long swatchPx = CountPixels(20, 20, 40, 40);
            swatch.Hide();
            Log.AppendLine($"校准：40x40 洋红方块采到 {swatchPx} 像素（应≈1600）");
            TestNormal();
        });
    }

    /// <summary>① 按 8ms 一拍喂点，同时每拍采屏。</summary>
    private static void TestNormal()
    {
        _overlay = new GestureOverlayWindow();
        _overlay.Left = SystemParameters.VirtualScreenLeft;
        _overlay.Top = SystemParameters.VirtualScreenTop;

        long t0 = Clock.ElapsedMilliseconds;
        _overlay.Begin(Opts, "right");
        _overlay.ShowPassive();
        long showMs = Clock.ElapsedMilliseconds - t0;

        int tick = 0, maxPx = 0;
        long firstSeen = -1;
        long start = Clock.ElapsedMilliseconds;
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) =>
        {
            tick++;
            _overlay!.AddPoint(400 + tick * 10, 400);          // 每拍 10px → 采样点每 2 拍记一个
            long now = Clock.ElapsedMilliseconds;
            long px = CountPixels(380, 380, 400, 80);
            if (px > maxPx) maxPx = (int)px;
            if (firstSeen < 0 && px > 0) firstSeen = now - start;

            if (tick >= 40)
            {
                timer.Stop();
                Log.AppendLine($"① 正常一笔：Show {showMs}ms，首帧 {firstSeen}ms，峰值 {maxPx} 像素，"
                               + $"此刻 {px} 像素，IsVisible={_overlay.IsVisible}");
                /* 收笔：未识别时 GestureService 会 HideOverlay(0) → Clear+Hide */
                _overlay.Clear();
                _overlay.Hide();
                TestFast();
            }
        };
        timer.Start();
    }

    /// <summary>② 快划：48ms 内画完 200px，然后立刻收笔 —— 屏幕上到底出现过没有。</summary>
    private static void TestFast()
    {
        _overlay = new GestureOverlayWindow();
        long t0 = Clock.ElapsedMilliseconds;
        _overlay.Begin(Opts, "right");
        _overlay.ShowPassive();
        long showMs = Clock.ElapsedMilliseconds - t0;

        int tick = 0, maxPx = 0;
        long start = Clock.ElapsedMilliseconds;
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) =>
        {
            tick++;
            _overlay!.AddPoint(400 + tick * 40, 600);          // 8ms 走 40px，很典型的"快划"
            long px = CountPixels(380, 580, 400, 60);
            if (px > maxPx) maxPx = (int)px;

            if (tick >= 6)
            {
                timer.Stop();
                long clearAt = Clock.ElapsedMilliseconds;
                _overlay.Clear();                              // Finish → HideOverlay(0)
                _overlay.Hide();

                long fadeSeen = -1;
                long after = Clock.ElapsedMilliseconds - start;
                var watch = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(160) };
                watch.Tick += (_, _) =>
                {
                    watch.Stop();
                    long late = CountPixels(380, 580, 400, 60);
                    Log.AppendLine($"② 快划一笔：Show {showMs}ms，笔中峰值 {maxPx} 像素，收笔 {clearAt - start}ms 后清屏；"
                                   + $"收笔 160ms 后 {late} 像素（{fadeSeen}）→ "
                                   + (maxPx <= 0 ? "整笔一帧都没上屏" : "笔中有画面"));
                    TestFadeRace();
                };
                watch.Start();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// ③ 连划两下：上一笔 SettleOverlay 排的淡出（默认 131ms）会不会把下一笔的覆盖层
    /// Clear()+Hide() 掉。走的是**真的 GestureService**（反射调私有方法），
    /// 不是另写一份模拟 —— 修之前这里必然 IsVisible=False。
    /// </summary>
    private static void TestFadeRace()
    {
        var svc = _host!.Gestures;
        var type = svc.GetType();
        const System.Reflection.BindingFlags Priv = System.Reflection.BindingFlags.Instance
                                                    | System.Reflection.BindingFlags.NonPublic;

        var settle = type.GetMethod("SettleOverlay", Priv);
        var beginVisual = type.GetMethod("BeginStrokeVisual", Priv);
        var overlayField = type.GetField("_overlay", Priv);
        if (settle == null || beginVisual == null || overlayField == null)
        {
            Log.AppendLine("③ 反射没找到 GestureService 的私有成员，跳过");
            Finish();
            return;
        }

        var previous = new MouseGesture { Kind = "stroke", Name = "上一笔" };
        settle.Invoke(svc, new object[] { previous, 500.0, 500.0 });
        var overlay = (GestureOverlayWindow?)overlayField.GetValue(svc);
        Log.AppendLine($"③ 上一笔结算完：IsVisible={overlay?.IsVisible}（淡出 131ms 后按设计该收）");

        /* 对照组：不开始新手势，淡出到点就该收 —— 证明这条定时器真的会 Hide。 */
        var control = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(320) };
        control.Tick += (_, _) =>
        {
            control.Stop();
            var after = (GestureOverlayWindow?)overlayField.GetValue(svc);
            Log.AppendLine($"③ 对照（不接新手势）：320ms 后 IsVisible={after?.IsVisible}"
                           + (after?.IsVisible == false ? " ← 淡出确实会 Hide" : " ← 淡出没生效，后面这条测不出东西"));
            RunRaceStep();
        };
        control.Start();

        void RunRaceStep()
        {
            settle.Invoke(svc, new object[] { previous, 700.0, 400.0 });
            var begin = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
            begin.Tick += (_, _) =>
            {
                begin.Stop();
                beginVisual.Invoke(svc, null);         // 新手势起手：作废上一笔的收起排期
                var live = (GestureOverlayWindow?)overlayField.GetValue(svc);
                live?.Begin(Opts, "right");
                live?.ShowPassive();
                for (int i = 0; i <= 40; i++) live?.AddPoint(400 + i * 8, 700);

                var check = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(320) };
                check.Tick += (_, _) =>
                {
                    check.Stop();
                    var now = (GestureOverlayWindow?)overlayField.GetValue(svc);
                    long px = CountPixels(380, 660, 400, 80);
                    Log.AppendLine($"③ 连划两下（新手势起手 320ms 后）：IsVisible={now?.IsVisible}，"
                                   + $"{px} 像素 → " + (now?.IsVisible == true ? "没被吃掉" : "被上一笔的淡出 Hide 掉了"));
                    Finish();
                };
                check.Start();
            };
            begin.Start();
        }
    }

    /// <summary>跑一小段消息循环再继续（用于校准时等窗口上屏）。</summary>
    private static void Pump(int ms, Action done)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); done(); };
        timer.Start();
    }

    private static long CountPixels(int x, int y, int w, int h)
    {
        try
        {
            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var buffer = new byte[data.Stride * h];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                long hits = 0;
                for (int row = 0; row < h; row++)
                {
                    int line = row * data.Stride;
                    for (int col = 0; col < w; col++)
                    {
                        byte b = buffer[line + col * 4 + 0];
                        byte gg = buffer[line + col * 4 + 1];
                        byte r = buffer[line + col * 4 + 2];
                        if (r > 200 && b > 200 && gg < 90) hits++;
                    }
                }
                return hits;
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception ex)
        {
            Log.AppendLine("采屏失败：" + ex.Message);
            return 0;
        }
    }

    private static void Finish()
    {
        try { _overlay?.Clear(); _overlay?.Hide(); } catch { }

        string path = Path.Combine(AppPaths.DataDir, "gesture-overlay-diag.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Log.ToString(), new UTF8Encoding(false));
        }
        catch { }

        Environment.Exit(0);
    }
}

