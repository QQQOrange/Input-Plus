using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using InputPlus.Core.Gestures;
using InputPlus.Core.Localization;
using InputPlus.Core.Settings;

namespace InputPlus.UI.Windows;

/// <summary>
/// 手势轨迹覆盖层：铺满整个虚拟屏幕的一张透明画布。
///
/// 三条硬约束（少一条就会毁掉手感）：
/// <list type="number">
/// <item>**不能抢焦点** —— 画手势时前台窗口必须保持原样，否则右键菜单、
/// 输入焦点全会丢。靠 WS_EX_NOACTIVATE + ShowActivated=false 保证。</item>
/// <item>**不能挡点击** —— 它盖在所有窗口上面，必须 WS_EX_TRANSPARENT 点击穿透，
/// 否则用户画完手势、下一次点击就点在覆盖层上了。</item>
/// <item>**坐标要按像素换算** —— 钩子给的是物理像素，WPF 用的是 DIP；
/// 高分屏下不换算，轨迹会缩到左上角四分之一。</item>
/// </list>
///
/// 轨迹与名称的画法照搬 WGestures 2：三层笔（阴影 / 描边 / 主线，见
/// <c>A/U.cs</c>：描边粗细 = 线宽 + 2×BorderWidth、透明度 = 主色 ×0.75、
/// 阴影 = ×0.075）、点距 20px 过滤 + ≤128 点一轮 Chaikin 切角（<c>A/U.cs</c> 的
/// <c>C_f</c> 与 <c>F()</c>，实现在 <see cref="GestureTrail"/>）、重绘按 16ms 节流
/// （<c>B/X.cs:119</c>）、颜色只在"已识别 / 未识别"之间切换一次（<c>A/T.cs:130</c>）。
///
/// **消失**（这次对齐的重点）：手势一结束就把整条轨迹清掉（<c>A/T.cs:115-119</c>
/// 的 <c>A_f3.D()</c> → <c>U.E()</c>，没有淡出、不跟名称一起停留）；名称框单独按
/// FadeOutExecuted 淡出（<c>A/T.cs:141-150</c>：勾了→停 100ms 后 1ms 消失；
/// 没勾→停 250ms 后 250ms CubicEaseIn 淡出），位置由 GestureLabelStyle.Position 决定
/// （<c>A/T.cs:387-393</c>：y = area.Top + (area.Height - labelH) × (1 - pos)，水平居中）。
/// </summary>
public sealed partial class GestureOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private double _scale = 1;
    private GestureOptions _opts = new();
    private string? _button;
    private bool _labelExecuted;

    /* 轨迹数据（WG2 A/U.cs 的 List<Vec2> + GraphicsPath）与重绘节流时间戳。
     * 点一律存物理像素，只有真正画的时候才换算成 DIP —— WG2 的轨迹窗口
     * 整个画在设备坐标里，换算多一次就多一次浮点误差。 */
    private readonly GestureTrail _trail = new();
    private long _lastPaintAt;

    /* 「匹配后替换」的过渡令牌：每次换图 / 清轨迹都 +1。
     * 旧动画的 Completed 回来时令牌对不上就什么都不做 —— 否则一次换图的尾巴
     * 会把用户刚开始画的下一笔（或者刚显示的名称框）一起擦掉。 */
    private int _replaceToken;

    /// <summary>重绘最小间隔（<c>B/X.cs:119</c>：距上次画满 16ms 才重来一次）。</summary>
    private const int PaintIntervalMs = 16;

    public GestureOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        _scale = GetDpiForWindow(hwnd) / 96.0;
        if (_scale <= 0.1) _scale = 1;
    }

    /* DPI 比例不能只靠 SourceInitialized 那次取：窗口没显示时
     * GetDpiForWindow 可能返回 0，于是 _scale 落到 1，
     * 200% 缩放下轨迹会画到两倍远的地方。每帧按当前视觉树再问一次最稳。 */
    private double Scale
    {
        get
        {
            try
            {
                double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
                return s > 0.1 ? s : 1;
            }
            catch { return 1; }
        }
    }

    /// <summary>物理像素 → 画布坐标（DIP，相对虚拟屏幕左上角）。</summary>
    private Point ToCanvas(double px, double py)
    {
        double scale = Scale;
        return new(px / scale - SystemParameters.VirtualScreenLeft,
                   py / scale - SystemParameters.VirtualScreenTop);
    }

    // ───────────────── 轨迹颜色（照 WGestures 2 的两色模型） ─────────────────

    /// <summary>未识别时的颜色：分键自定义优先（老功能），否则用 WG2 的 PathColorNormal。</summary>
    private Color NormalColor()
    {
        string? custom = null;
        if (!string.IsNullOrEmpty(_button) && _opts.ButtonTrailColors != null &&
            _opts.ButtonTrailColors.TryGetValue(_button!, out string? value) &&
            !string.IsNullOrWhiteSpace(value))
            custom = value;
        return Wg2Color.Parse(custom, Wg2Color.Parse(_opts.PathColorNormal, Colors.Black));
    }

    private Color RecognizedColor()
        => Wg2Color.Parse(_opts.PathColorRecognized, Color.FromRgb(0x59, 0x00, 0xFF));

    /* 描边和阴影不是独立的配置项，而是主色的两个派生：
     * 描边把主色的 RGB 提亮成白、只留 75% 的透明度，阴影 7.5%，
     * 所以换色时三支笔必须一起换，只改主线会留下"彩色线 + 灰描边"。 */
    private void ApplyPathColor(Color color)
    {
        double opacity = Math.Clamp(_opts.TrailOpacity, 0.05, 1.0);
        double width = Math.Clamp(_opts.PathLineWidth, 0.5, 12.0);

        byte A(double factor)
            => (byte)Math.Clamp(Math.Round(color.A * factor * opacity), 0, 255);

        TrailMainSet(color, A(1.0), width);
        TrailBorder.Stroke = new SolidColorBrush(Color.FromArgb(A(0.75), 0xFF, 0xFF, 0xFF));
        // 描边画在主线两侧各 1px：粗细 = 线宽 + 2×BorderWidth（WG2 BorderWidth = 勾选?1:0）
        TrailBorder.StrokeThickness = _opts.ShowPathBorder ? width + 2 : 0;
        TrailBorder.Visibility = _opts.ShowPathBorder ? Visibility.Visible : Visibility.Collapsed;
        TrailShadow.Stroke = new SolidColorBrush(Color.FromArgb(A(0.075), 0, 0, 0));
        TrailShadow.StrokeThickness = width + 4;

        /* 模板层与轨迹层**共用**同一套笔（颜色/粗细/描边开关都照抄）：
         * 匹配后替换时只有形状变，粗细和配色不许跳 —— 那正是"美化"最容易被看穿的地方。 */
        TemplatePath.Stroke = Trail.Stroke;
        TemplatePath.StrokeThickness = Trail.StrokeThickness;
        TemplateBorderPath.Stroke = TrailBorder.Stroke;
        TemplateBorderPath.StrokeThickness = TrailBorder.StrokeThickness;
        TemplateBorderPath.Visibility = TrailBorder.Visibility;
        TemplateShadowPath.Stroke = TrailShadow.Stroke;
        TemplateShadowPath.StrokeThickness = TrailShadow.StrokeThickness;
    }

    private void TrailMainSet(Color color, byte alpha, double width)
    {
        Trail.Stroke = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        Trail.StrokeThickness = width;
    }

    // ───────────────── 名称框（照 WGestures 2 的 GestureLabelView） ─────────────────

    /// <summary>
    /// 名称框外观：底色 / 描边 / 描边粗细来自「鼠标手势 → 手势设置」里的
    /// <see cref="GestureLabelStyle"/>。v1.6.0 曾和右下角提示条合并成一套全局
    /// 外观，v1.6.3 又拆回两份 —— 名称框有自己的出现高度和两档底色，提示条没有。
    /// </summary>
    private void ApplyLabelStyle(bool executed)
    {
        _labelExecuted = executed;
        ApplyLabelLook(LabelBox, GestureLabelStyles.Current, executed);

        /* 白字 + 黑晕：配置里的底色可能很淡（出厂 #00000030），
         * 光靠底色在浅色窗口上会看不清白字，加一圈黑影兜住可读性。 */
        LabelText.Foreground = Brushes.White;
        LabelText.Effect = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 8,
            ShadowDepth = 0,
            Opacity = 0.55
        };
        LabelBox.Effect = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 16,
            ShadowDepth = 0,
            Opacity = 0.45
        };
    }

    /// <summary>
    /// 名称框的「配色」部分：底色 / 圆角 / 描边 / 描边粗细。
    ///
    /// 抽成静态方法是为了让「手势设置」页里的预览跟真正弹出来的框共用同一份算式 ——
    /// 预览另写一遍的话，改完设置和真弹出来的样子迟早会对不上。
    /// </summary>
    internal static void ApplyLabelLook(Border box, GestureLabelStyle style, bool executed)
    {
        var color = Wg2Color.Parse(
            executed ? style.ColorExecuted : style.ColorNormal,
            executed ? Color.FromRgb(0x59, 0x00, 0xFF) : Color.FromArgb(0x55, 0, 0, 0));

        box.Background = new SolidColorBrush(color);
        box.CornerRadius = new CornerRadius(16);

        /* 描边是主线两侧各 BorderWidth 像素，而 WPF 的 BorderThickness 只往内量，
         * 所以画出来的粗细要乘 2 —— 和 WG2 的 LabelBorderWidth 是两套口径。 */
        double border = style.ShowBorder
            ? Math.Max(1.0, style.BorderWidth) * 2
            : 0;
        box.BorderThickness = new Thickness(border);
        box.BorderBrush = border > 0
            ? new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
            : Brushes.Transparent;
    }

    // ───────────────── 起手 ─────────────────

    /// <summary>一次手势开始时调用：清干净上一笔，按配置铺好"未识别"配色。</summary>
    public void Begin(GestureOptions opts, string? button = null)
    {
        _opts = opts ?? new GestureOptions();
        _button = button;
        ClearTrail();
        TimeoutDot.Visibility = Visibility.Collapsed;
        LabelBox.Visibility = Visibility.Collapsed;
        ApplyPathColor(NormalColor());
        ApplyLabelStyle(executed: false);
        LabelBox.Opacity = 1.0;
        LabelBox.BeginAnimation(OpacityProperty, null);
    }

    /// <summary>
    /// 收一个轨迹点（物理像素）。顺序照 <c>A/U.cs</c> + <c>B/X.cs:112-119</c>：
    /// 先按 20px 最小距离进模型（不够近就丢掉），再按 16ms 节流重绘。
    /// 8ms 轮询下每个点都重建几何会把 UI 线程喂满，节流后画面看不出差别。
    /// </summary>
    public void AddPoint(double px, double py)
    {
        /* 采样没到 20px 也要动笔尖：只挪线头，不加采样点。
         * 以前这里直接 return，线就停在最后一个采样点上 ——
         * 20px 一格的"台阶"就是这么来的。 */
        if (_trail.Add(px, py))
        {
            Repaint();
            return;
        }
        _trail.Tail(px, py);
        Repaint();
    }

    public void ReplaceLast(double px, double py)
    {
        _trail.ReplaceLast(px, py);
        Repaint(force: true);
    }

    /// <summary>重建三支笔的几何（三层共用同一个 Geometry 实例，WPF 只需布局一次）。</summary>
    private void Repaint(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastPaintAt < PaintIntervalMs) return;
        _lastPaintAt = now;

        Geometry? geometry = GestureTrail.ToGeometry(
            _trail.Build(smooth: _opts.SmoothStrokes), ToCanvasPointOf);
        Trail.Data = geometry;
        TrailBorder.Data = geometry;
        TrailShadow.Data = geometry;
    }

    /// <summary>
    /// 轨迹立刻消失 —— WG2 <c>A/T.cs:115-119</c> 的手势结束分支（<c>A_f3.D()</c> →
    /// <c>U.E()</c>）：整条清空 + 脏区重画成透明，没有淡出、不跟名称一起停留。
    /// </summary>
    public void ClearTrail()
    {
        _replaceToken++;
        _trail.Clear();
        _lastPaintAt = 0;
        Trail.Data = null;
        TrailBorder.Data = null;
        TrailShadow.Data = null;
        ResetOpacity(Trail);
        ResetOpacity(TrailBorder);
        ResetOpacity(TrailShadow);
        ClearTemplate();
    }

    // ───────────────── 美化模板（匹配后替换，任务四） ─────────────────

    private void ClearTemplate()
    {
        TemplatePath.Data = null;
        TemplateBorderPath.Data = null;
        TemplateShadowPath.Data = null;
        ResetOpacity(TemplatePath);
        ResetOpacity(TemplateBorderPath);
        ResetOpacity(TemplateShadowPath);
    }

    private static void ResetOpacity(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1.0;
    }

    /// <summary>
    /// 匹配成功：把屏幕上的原始轨迹换成存档的美化模板（「匹配后替换」）。
    ///
    /// <paramref name="template"/> 是归一化坐标（0~1000，与 <see cref="MouseGesture.Beauty"/> 同口径）。
    /// 这里把它按**当前这一笔的包围盒**等比摆回屏幕 —— 模板是在哪画都一样的标准图形，
    /// 但用户刚才是画在这里的，替换后位置不能跳。
    ///
    /// <paramref name="transitionMs"/> &gt; 0 时做一次交叉淡入（轨迹 1→0、模板 0→1，
    /// 同一时长、同一条缓动），0 表示立刻换完。
    /// 返回实际用的过渡时长（0 = 已经换好了），调用方据此决定隔多久再执行动作。
    /// </summary>
    public int ReplaceTrailWithTemplate(IReadOnlyList<GestureMath.Pt>? template, int transitionMs)
    {
        if (template == null || template.Count < 2) return 0;
        Geometry? geometry = BuildTemplateGeometry(template);
        if (geometry == null) return 0;

        TemplatePath.Data = geometry;
        TemplateBorderPath.Data = geometry;
        TemplateShadowPath.Data = geometry;

        int ms = Math.Clamp(transitionMs, 0, 1000);
        int token = ++_replaceToken;

        if (ms <= 0)
        {
            FinishTemplateReplace();
            return 0;
        }

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(ms));
        var fadeIn = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = easing };
        var fadeOut = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = easing };
        fadeOut.Completed += (_, _) =>
        {
            if (token != _replaceToken) return;
            FinishTemplateReplace();
        };

        TemplatePath.Opacity = 0;
        TemplateBorderPath.Opacity = 0;
        TemplateShadowPath.Opacity = 0;
        TemplatePath.BeginAnimation(OpacityProperty, fadeIn);
        TemplateBorderPath.BeginAnimation(OpacityProperty, fadeIn);
        TemplateShadowPath.BeginAnimation(OpacityProperty, fadeIn);
        Trail.BeginAnimation(OpacityProperty, fadeOut);
        TrailBorder.BeginAnimation(OpacityProperty, fadeOut);
        TrailShadow.BeginAnimation(OpacityProperty, fadeOut);
        return ms;
    }

    /// <summary>过渡结束（或直接切换）：旧轨迹整条丢掉，模板留在屏幕上。</summary>
    private void FinishTemplateReplace()
    {
        _replaceToken++;
        _trail.Clear();
        _lastPaintAt = 0;
        Trail.Data = null;
        TrailBorder.Data = null;
        TrailShadow.Data = null;
        ResetOpacity(Trail);
        ResetOpacity(TrailBorder);
        ResetOpacity(TrailShadow);
        ResetOpacity(TemplatePath);
        ResetOpacity(TemplateBorderPath);
        ResetOpacity(TemplateShadowPath);
    }

    /// <summary>把归一化模板摆进"这一笔"的包围盒（DIP，画布坐标）。</summary>
    private Geometry? BuildTemplateGeometry(IReadOnlyList<GestureMath.Pt> template)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in template)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        double width = maxX - minX, height = maxY - minY;
        if (width < 0.001 || height < 0.001) return null;

        Rect box = TrailBox();
        double scale = Math.Min(box.Width / width, box.Height / height);
        if (!(scale > 0) || double.IsInfinity(scale)) return null;

        double cx = box.Left + box.Width / 2;
        double cy = box.Top + box.Height / 2;
        double tx = (minX + maxX) / 2;
        double ty = (minY + maxY) / 2;

        var mapped = new List<Point>(template.Count);
        foreach (var p in template)
            mapped.Add(new Point(cx + (p.X - tx) * scale, cy + (p.Y - ty) * scale));

        return GestureTrail.ToGeometry(mapped, point => point);
    }

    /// <summary>
    /// 当前这一笔在画布上的包围盒（DIP）。某一轴几乎为零（横线/竖线）时
    /// 把那一轴撑到 96 DIP，否则等比缩放会算出 0，模板直接缩成一个点。
    /// </summary>
    private Rect TrailBox()
    {
        var points = _trail.Build(smooth: false);
        if (points.Count >= 2)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var point in points)
            {
                Point p = ToCanvas(point.X, point.Y);
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            double w = Math.Max(maxX - minX, 1);
            double h = Math.Max(maxY - minY, 1);
            double cx = (minX + maxX) / 2;
            double cy = (minY + maxY) / 2;
            w = Math.Max(w, 96);
            h = Math.Max(h, 96);
            return new Rect(cx - w / 2, cy - h / 2, w, h);
        }

        /* 没有轨迹点（理论上走不到：替换总是发生在画完之后）→ 窗口正中一个方框。 */
        double cw = Width > 1 ? Width : 800;
        double ch = Height > 1 ? Height : 600;
        return new Rect(cw / 2 - 100, ch / 2 - 100, 200, 200);
    }

    private Point ToCanvasPointOf(Point device) => ToCanvas(device.X, device.Y);

    /// <summary>
    /// 认出来了：三支笔一起换成「已识别」色。WG2 只在命中这一刻换一次色，
    /// 之后哪怕继续画也不回退（<c>A/T.cs:130</c>）。
    /// </summary>
    public void MarkRecognized() => ApplyPathColor(RecognizedColor());

    // ───────────────── 结果 ─────────────────

    /// <summary>起始超时：鼠标尖端一个红色实心圆。只画圆，不显示文字和轨迹。</summary>
    public void ShowTimeoutDot(double px, double py)
    {
        ClearTrail();
        LabelBox.Visibility = Visibility.Collapsed;

        var p = ToCanvas(px, py);
        const double size = 16;
        Canvas.SetLeft(TimeoutDot, p.X - size / 2);
        Canvas.SetTop(TimeoutDot, p.Y - size / 2);
        TimeoutDot.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 显示手势名称。纵向位置取「手势设置 → 名称位置」（0..1）：
    /// <c>y = area.Top + (area.Height - labelH) × (1 - pos)</c>，pos=0 贴底、1 贴顶，
    /// 水平居中（算式照 WG2 的 A/T.cs:387-393）。
    /// <paramref name="keepTrail"/> 为 false 时顺手清掉轨迹。
    /// <paramref name="executed"/> 用「已执行」配色（LabelColorExecuted）。
    /// </summary>
    public void ShowLabel(string text, double px, double py, bool keepTrail = false, bool executed = false)
    {
        if (!keepTrail) ClearTrail();
        TimeoutDot.Visibility = Visibility.Collapsed;

        ApplyLabelStyle(executed);
        LabelText.Text = text;
        LabelBox.Visibility = Visibility.Visible;
        LabelBox.Opacity = 1.0;
        LabelBox.BeginAnimation(OpacityProperty, null);
        LabelBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = LabelBox.DesiredSize;

        double x, y;
        try
        {
            var area = System.Windows.Forms.Screen
                .FromPoint(new System.Drawing.Point((int)px, (int)py)).WorkingArea;
            var topLeft = ToCanvas(area.Left, area.Top);
            var bottomRight = ToCanvas(area.Right, area.Bottom);
            double w = bottomRight.X - topLeft.X;
            double h = bottomRight.Y - topLeft.Y;
            double pos = GestureLabelStyles.Current.Position;
            x = topLeft.X + Math.Max(0, (w - size.Width) / 2);
            y = topLeft.Y + Math.Max(0, (h - size.Height) * (1.0 - pos));
        }
        catch
        {
            double pos = GestureLabelStyles.Current.Position;
            x = Math.Max(0, (Width - size.Width) / 2);
            y = Math.Max(0, (Height - size.Height) * (1.0 - pos));
        }
        Canvas.SetLeft(LabelBox, x);
        Canvas.SetTop(LabelBox, y);
    }

    /// <summary>
    /// 名称框淡出（WG2 的「执行后淡出」语义）：勾了 FadeOutExecuted → 停 100ms 后
    /// 1ms 直接消失；没勾 → 停 250ms 再花 250ms 缓入淡出。
    /// 返回还需要多少毫秒才能彻底看不见（0 = 没有名称框，可以立刻清）。
    /// </summary>
    public int FadeOutLabel()
    {
        if (LabelBox.Visibility != Visibility.Visible) return 0;

        bool fast = _opts.FadeOutExecuted;
        double delay = fast ? 100 : 250;
        double duration = fast ? 1 : 250;

        var fade = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(duration)))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay)
        };
        if (!fast)
            fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
        LabelBox.BeginAnimation(OpacityProperty, fade);
        return (int)Math.Ceiling(delay + duration) + 30;
    }

    /// <summary>只收起名称框，保留正在画的轨迹（实时预览换名时用）。</summary>
    public void HideLabelOnly() => LabelBox.Visibility = Visibility.Collapsed;

    public void Clear()
    {
        LabelBox.BeginAnimation(OpacityProperty, null);
        LabelBox.Opacity = 1.0;
        _labelExecuted = false;
        ClearTrail();
        TimeoutDot.Visibility = Visibility.Collapsed;
        LabelBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>让窗口"存在但不打扰"：不激活、不进 Alt+Tab、不抢鼠标。</summary>
    public void ShowPassive()
    {
        if (!IsVisible) Show();
        // 每次显示都重新压到最上层：别的程序可能刚好也置顶了。
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0010); // NOSIZE | NOMOVE | NOACTIVATE
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
