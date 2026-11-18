using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Gestures;
using InputPlus.Core.Input;

namespace InputPlus.UI.Windows;

/// <summary>
/// 录制一条手势：在一个专门的窗口里画一笔，窗口自己收鼠标事件。
///
/// 为什么不复用全局钩子录制：设置页点「录制」之后，用户要把焦点切到
/// 别的窗口上画，焦点一变、按钮就丢了；而且录完还得判断"刚才那颗是侧键
/// 还是中键"，全靠猜。在这里画，按的是哪颗键、轨迹什么样，都是确定的。
///
/// 侧键（XButton1/2）WPF 的 MouseDown 不一定报，所以额外挂了一个窗口消息钩子
/// 收 WM_XBUTTONDOWN / WM_XBUTTONUP，两条路共用同一套开始/结束逻辑。
/// </summary>
public partial class GestureRecordWindow : Window
{
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONUP = 0x0208;

    private readonly List<GestureMath.Pt> _points = new();
    private GesturePattern.Tracker? _tracker;
    private int _vk;
    private int _chordVk;
    private bool _tracking;
    private HwndSource? _source;

    /* 看门狗：抬起消息丢了（鼠标捕获被别的窗口抢走、切屏、驱动吞消息）时，
     * 状态会永远停在"正在画"，用户看到的就是"点重新录制之后画笔画卡住"。
     * 定时对一下物理按键状态，松了却还在画就立刻收尾。 */
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>画完之后的结果；没画或点取消时是 null。</summary>
    public RecordedGesture? Result { get; private set; }

    /// <summary>采样参数，由设置页传入当前的 GestureOptions。</summary>
    public GestureOptions Options { get; set; } = new();

    public GestureRecordWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => DetachHook();
        KeyDown += OnKeyDown;
        _watch.Tick += (_, _) => Watchdog();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(WndProc);
    }

    private void DetachHook()
    {
        try { _source?.RemoveHook(WndProc); } catch { }
        _source = null;
        _watch.Stop();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        /* 左/右/中键的抬起：WPF 正常会派发到 OnPadUp，但捕获被抢、
         * 或者别的窗口把鼠标抢走时这一路会丢，状态就永远停在"正在画"。
         * 这里做兜底收尾，**不设 handled**，让 WPF 那一路照常走（Finish 幂等）。 */
        if (msg is WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP)
        {
            int upVk = msg switch { WM_LBUTTONUP => 0x01, WM_RBUTTONUP => 0x02, _ => 0x04 };
            if (_tracking && _vk == upVk)
            {
                CoreLog.Info($"[手势] 窗口消息兜底收尾（抬起 vk=0x{upVk:X2}）");
                Finish();
            }
            return IntPtr.Zero;
        }

        if (msg is WM_XBUTTONDOWN or WM_XBUTTONUP)
        {
            int which = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
            int vk = which == 2 ? 0x06 : 0x05;     // XBUTTON1=1 / XBUTTON2=2

            if (msg == WM_XBUTTONDOWN)
            {
                if (!_tracking) StartAt(vk, ScreenToPad(lParam));
                else if (vk != _vk) SetChord(vk);
                handled = true;
            }
            else
            {
                if (_tracking && _vk == vk) Finish();
                /* 第二颗侧键的抬起只吞掉，留给 Finish 读 _chordVk。 */
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void OnPadDown(object sender, MouseButtonEventArgs e)
    {
        int vk = e.ChangedButton switch
        {
            MouseButton.Left => 0x01,
            MouseButton.Right => 0x02,
            MouseButton.Middle => 0x04,
            MouseButton.XButton1 => 0x05,
            MouseButton.XButton2 => 0x06,
            _ => 0
        };
        if (vk == 0) return;

        /* 已经按住起始键在画了：再按另一颗鼠标键就是组合手势的第二键。
         * 不重新起笔，也不释放当前轨迹。 */
        if (_tracking)
        {
            if (vk != _vk) SetChord(vk);
            e.Handled = true;
            return;
        }

        StartAt(vk, e.GetPosition(Pad));
        Pad.CaptureMouse();
        e.Handled = true;
    }

    private void OnPadMove(object sender, MouseEventArgs e)
    {
        if (!_tracking) return;

        /* 移动时顺手对一下物理按键：已经松了却还在画，说明抬起消息丢了，
         * 立刻收尾，别让用户对着"正在画"的界面干等。 */
        if (!IsButtonDown(_vk))
        {
            CoreLog.Info($"[手势] 移动时发现按键已松开，直接收尾（vk=0x{_vk:X2}）");
            Finish();
            return;
        }

        Add(e.GetPosition(Pad));
    }

    private void OnPadUp(object sender, MouseButtonEventArgs e)
    {
        if (!_tracking) return;
        int vk = e.ChangedButton switch
        {
            MouseButton.Left => 0x01,
            MouseButton.Right => 0x02,
            MouseButton.Middle => 0x04,
            MouseButton.XButton1 => 0x05,
            MouseButton.XButton2 => 0x06,
            _ => 0
        };
        /* 第二键抬起：吞掉即可，_chordVk 要留到 Finish 生成结果。 */
        if (vk == _chordVk)
        {
            e.Handled = true;
            return;
        }

        if (vk != _vk) return;

        if (Pad.IsMouseCaptured) Pad.ReleaseMouseCapture();
        Finish();
        e.Handled = true;
    }

    private void OnPadLostCapture(object sender, MouseEventArgs e)
    {
        if (_tracking) Finish();
    }

    // ───────────────────────── 采样 ─────────────────────────

    private void StartAt(int vk, Point p)
    {
        var opts = Options;
        _points.Clear();
        _vk = vk;
        _chordVk = 0;
        _tracking = true;
        Result = null;
        UseButton.IsEnabled = false;

        var start = new GestureMath.Pt(p.X, p.Y);
        _points.Add(start);
        _tracker = new GesturePattern.Tracker(opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance);
        _tracker.Start(p.X, p.Y);

        TrailLine.Points.Clear();
        TrailLine.Points.Add(p);
        PadHint.Visibility = Visibility.Collapsed;
        LiveBadge.Visibility = Visibility.Visible;
        LiveButton.Text = ButtonName(vk);
        LiveChord.Text = "";
        LiveChord.Visibility = Visibility.Collapsed;
        StatusText.Text = Loc.T("画吧，松手就录好；想录组合手势，画完先别松手，再按另一颗鼠标键。");

        /* 侧键走的是窗口消息，不 Capture 的话鼠标一离开画板就收不到抬起，
         * 状态会卡在"正在画"。CaptureMouse 之后抬起一定回到这里。 */
        if (!Pad.IsMouseCaptured) Pad.CaptureMouse();

        _watch.Start();
    }

    /* ───────────────────────── 防卡住 ───────────────────────── */

    /// <summary>
    /// 定时兜底：物理按键已经松开，但还停在"正在画"，就收尾。
    /// 抬起消息可能被别的窗口抢走（Alt+Tab、切屏、全屏游戏、远程桌面），
    /// 只靠 WPF 的 MouseUp 不足以覆盖这些情况。
    /// </summary>
    private void Watchdog()
    {
        if (!_tracking)
        {
            _watch.Stop();
            return;
        }

        if (IsButtonDown(_vk)) return;

        CoreLog.Info($"[手势] 抬起事件丢了，按物理按键状态收尾（vk=0x{_vk:X2}）");
        Finish();
    }

    /// <summary>物理按键此刻是不是还按着（看门狗用，读的是系统全局状态）。</summary>
    private static bool IsButtonDown(int vk) =>
        (InputPlus.Core.Interop.NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void SetChord(int vk)
    {
        _chordVk = vk;
        LiveChord.Text = " + " + ButtonName(vk);
        LiveChord.Visibility = Visibility.Visible;
        StatusText.Text = Loc.F("组合手势：{0} + {1}。松开始按键就完成录制。", ButtonName(_vk), ButtonName(vk));
    }

    private void Add(Point p)
    {
        if (_points.Count > 0)
        {
            var last = _points[^1];
            double dx = p.X - last.X, dy = p.Y - last.Y;
            if (dx * dx + dy * dy < 2.0) return;      // 1.4px 以下的抖动不记
        }

        _points.Add(new GestureMath.Pt(p.X, p.Y));
        TrailLine.Points.Add(p);

        /* 形状由画板上那条轨迹自己表达，角标里不再刷箭头 ——
         * 但 _tracker 仍然要喂：没有轨迹的老手势只有方向串可比，兜底还靠它。 */
        _tracker?.Move(p.X, p.Y);
    }

    private void Finish()
    {
        _tracking = false;
        _watch.Stop();
        if (Pad.IsMouseCaptured) Pad.ReleaseMouseCapture();

        var opts = Options;
        double extent = GestureMath.Extent(_points);
        string pattern = _tracker?.Pattern ?? "";
        if (string.IsNullOrEmpty(pattern) && _points.Count > 1)
            pattern = GesturePattern.FromPoints(_points, opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance);

        if (extent < 24 || pattern.Length == 0)
        {
            Result = null;
            UseButton.IsEnabled = false;
            StatusText.Text = Loc.T("这一笔太短了（或者只是点了一下）。再画长一点，至少甩出二三十像素。");
            return;
        }

        List<int> flat = GestureMath.ToFlat(GestureMath.Normalize(_points));
        string chord = _chordVk == 0 ? "" : ButtonNameValue(_chordVk);
        Result = new RecordedGesture(ButtonNameValue(_vk), flat, pattern, chord);
        UseButton.IsEnabled = true;
        string combo = chord.Length == 0
            ? ButtonName(_vk)
            : Loc.F("{0} + {1}", ButtonName(_vk), ButtonName(_chordVk));
        StatusText.Text = Loc.F("录好了：{0}　——　点「用这一笔」填进手势。", combo);
    }

    // ───────────────────────── 按钮 ─────────────────────────

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _tracking = false;
        _watch.Stop();
        Result = null;
        _points.Clear();
        _tracker = null;
        _chordVk = 0;
        TrailLine.Points.Clear();
        LiveBadge.Visibility = Visibility.Collapsed;
        LiveChord.Visibility = Visibility.Collapsed;
        PadHint.Visibility = Visibility.Visible;
        UseButton.IsEnabled = false;
        StatusText.Text = Loc.T("重画：按住鼠标键，在框里再画一笔。");
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    private void OnUseClick(object sender, RoutedEventArgs e)
    {
        if (Result == null) return;
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = null;
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Result != null)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static string ButtonName(int vk)
        => Loc.T(GestureButtons.Display(GestureButtons.FromVk(vk)));

    private static string ButtonNameValue(int vk) => GestureButtons.FromVk(vk);

    private Point ScreenToPad(IntPtr lParam)
    {
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        try { return Pad.PointFromScreen(new Point(x, y)); }
        catch { return new Point(0, 0); }
    }
}

/// <summary>录制结果：起始键（right/middle/x1/x2…）、归一化轨迹、方向串、可选的第二键。</summary>
public sealed record RecordedGesture(string Button, List<int> Path, string Pattern, string Chord = "");
