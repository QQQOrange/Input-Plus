using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
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

    /* 虚拟屏幕矩形（物理像素）。和 SystemParameters 那组 DIP 值不是一回事，
     * 见 SyncBounds 的注释。 */
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private double _scale = 1;
    private GestureOptions _opts = new();
    private string? _button;
    private bool _labelExecuted;

    /* 轨迹数据（WG2 A/U.cs 的 List<Vec2> + GraphicsPath）与重绘节流时间戳。
     * 点一律存物理像素，只有真正画的时候才换算成 DIP —— WG2 的轨迹窗口
     * 整个画在设备坐标里，换算多一次就多一次浮点误差。 */
    private readonly GestureTrail _trail = new();
    private long _lastPaintAt;

    /* 起手后的第一帧必须画出去：节流按 16ms 算，而第一拍常常落在节流窗口里
     * （起手 → 第一次移动只隔 8ms），于是"点了、手动了、屏幕还是空的"。
     * 这一位只对每一笔的第一次 AddPoint 生效。 */
    private bool _firstPaintPending;

    /// <summary>重绘最小间隔（<c>B/X.cs:119</c>：距上次画满 16ms 才重来一次）。</summary>
    private const int PaintIntervalMs = 16;

    /* ── 「空一帧再藏」─────────────────────────────────────────────
     *
     * 这块是"上一笔闪一下"的根子，改动前先说清楚为什么。
     *
     * WPF（AllowsTransparency=true 的分层窗口）是**把整窗位图提交给系统**才上屏的，
     * 而提交只发生在窗口**可见**的时候。所以：
     *     Clear()   → 只是把 WPF 这边的视觉树清空，屏幕上那一帧还在；
     *     Hide()    → 同一拍里执行，WPF 根本没机会提交"空画布"；
     *     Show()    → 系统先把上一次提交的位图贴出来（上一笔的轨迹），
     *                 要等新的一帧渲染完才被盖掉 —— 用户看到的就是"上一笔闪一下"。
     *
     * 这也正好解释了现象为什么只出现在**未命中**的手势上：命中的那条走
     * SettleOverlay → 名称框淡出 100~500ms，窗口一直可见，空画布早就提交上去了；
     * 未命中走 HideOverlay(0)，清空与隐藏在同一拍，表面留着的还是上一笔。
     *
     * WGestures 2 没有这个问题，因为它的轨迹窗**从不隐藏**：B/X.cs 的 x 建好后
     * A(true) 就是加 WS_VISIBLE（a/E.cs:244 的 A(bool)），手势结束时 A/T.cs:115-119
     * 只把位图刷成透明，窗口一直可见，所以透明帧永远来得及提交。
     *
     * 这里不动窗口生命周期（全屏置顶窗一直挂着不好），改成等价的做法：
     * 藏之前先让"空画布"真的上屏一帧，之后表面就是透明的，Show() 出来自然是空的。 */
    private int _hideToken;
    private DispatcherTimer? _hideFallback;

    /// <summary>两帧 ≈ 32ms：第 1 帧提交清空后的画布，第 2 帧确认已经上屏。</summary>
    private const int BlankFramesBeforeHide = 2;

    /// <summary>渲染回调万一不来（远程桌面 / 合成被关掉），最多等这么久就照藏。</summary>
    private const int BlankHideFallbackMs = 160;


    public GestureOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        /* 关窗时把还没跑完的"空帧后隐藏"作废：CompositionTarget.Rendering 是静态事件，
         * 漏一个订阅就等于把整个窗口（和它整屏的视觉树）钉在内存里。 */
        Closed += (_, _) =>
        {
            _hideToken++;
            if (_hideFallback is { } fallback)
            {
                try { fallback.Stop(); } catch (Exception) { }
                _hideFallback = null;
            }
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        _scale = GetDpiForWindow(hwnd) / 96.0;
        if (_scale <= 0.1) _scale = 1;
        /* HWND 有了才知道真实 DPI：按它把窗口摆到整个虚拟屏幕上。 */
        SyncBounds();
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

    /// <summary>物理像素 → 画布坐标（DIP，相对窗口左上角）。</summary>
    private Point ToCanvas(double px, double py)
    {
        /* 用窗口自己的 Left/Top 当归一化原点，而不是 SystemParameters 里那组
         * 按主屏 DPI 折算的虚拟屏 DIP 值：多屏不同缩放时两者不等，轨迹会整体偏移。 */
        double scale = Scale;
        return new(px / scale - Left, py / scale - Top);
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
        box.CornerRadius = new CornerRadius(InputPlus.UI.ThemeMetrics.RadiusCard);

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
        /* 新手势来了：上一笔排的"空帧后隐藏"必须作废，否则它会在这一笔
         * 刚起手的两帧里把窗口藏掉（轨迹还在、屏幕上一个像素没有）。 */
        CancelPendingHide();
        SyncBounds();
        _opts = opts ?? new GestureOptions();
        _button = button;
        ClearTrail();
        _firstPaintPending = true;
        TimeoutDot.Visibility = Visibility.Collapsed;
        LabelBox.Visibility = Visibility.Collapsed;
        ApplyPathColor(NormalColor());
        ApplyLabelStyle(executed: false);
        LabelBox.Opacity = 1.0;
        LabelBox.BeginAnimation(OpacityProperty, null);
    }

    /// <summary>
    /// 覆盖层铺满整个虚拟屏幕，但尺寸只在建窗时取过一次。换分辨率、插拔显示器、
    /// 远程桌面改尺寸之后窗口还是旧大小 —— 画到外面去的轨迹会被裁掉。
    /// 每一笔起手对一次，代价只是四次比较。
    ///
    /// 尺寸按**物理像素**的虚拟屏算再除以窗口 DPI，不直接用 SystemParameters 的
    /// 那组 DIP：混合 DPI（主屏 100% + 副屏 200%）下它是按主屏折算的，拿它当
    /// 窗口的 DIP 尺寸会让窗口只覆盖到副屏的一半，副屏上的轨迹被裁掉。
    /// </summary>
    private void SyncBounds()
    {
        double scale = Scale;
        double left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        double top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        double width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        double height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (width < 1 || height < 1)
        {
            /* 拿不到物理尺寸（极少见）：退回 SYSTEM 那组 DIP，至少能画在主屏上。 */
            left = SystemParameters.VirtualScreenLeft;
            top = SystemParameters.VirtualScreenTop;
            width = SystemParameters.VirtualScreenWidth;
            height = SystemParameters.VirtualScreenHeight;
            scale = 1.0;
        }

        left /= scale;
        top /= scale;
        width /= scale;
        height /= scale;

        if (Math.Abs(Left - left) > 0.5) Left = left;
        if (Math.Abs(Top - top) > 0.5) Top = top;
        if (Math.Abs(Width - width) > 0.5) Width = width;
        if (Math.Abs(Height - height) > 0.5) Height = height;
    }

    /// <summary>
    /// 预热：把窗口真正建出来、上一次屏，再藏回去。
    ///
    /// 为什么要有：<c>Show()</c> 要建 HWND + 首次布局合成，实测 30ms 上下；
    /// 这笔钱以前是在"右键按下、第一笔手势已经开始了"的那一刻付的 ——
    /// 手快的时候整条轨迹都画在还没上屏的窗口里，看起来就是"滑了没显示"。
    /// 服务启动时在空闲优先级调一次，之后每笔起手只剩 SetWindowPos。
    /// </summary>
    public void WarmUp()
    {
        ShowPassive();
        Hide();
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
        bool added = _trail.Add(px, py);
        if (!added) _trail.Tail(px, py);

        /* 这一笔的第一帧绕过 16ms 节流：否则起手那一下常常落在节流窗口里，
         * 屏幕上要等到下一个 16ms 边界才有东西 —— 快划时就是"整笔都没显示"。 */
        Repaint(_firstPaintPending);
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
            _trail.Build(), ToCanvasPointOf);
        Trail.Data = geometry;
        TrailBorder.Data = geometry;
        TrailShadow.Data = geometry;

        /* 直到真的有一条能画的线摆上去，才算"第一帧画过了"：
         * 起手那一下只有 1 个点，几何是 null，屏幕上还是空的。 */
        if (geometry != null) _firstPaintPending = false;
    }

    /// <summary>
    /// 轨迹立刻消失 —— WG2 <c>A/T.cs:115-119</c> 的手势结束分支（<c>A_f3.D()</c> →
    /// <c>U.E()</c>）：整条清空 + 脏区重画成透明，没有淡出、不跟名称一起停留。
    /// </summary>
    public void ClearTrail()
    {
        GestureTrace.Write($"ClearTrail caller={new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name}");
        _trail.Clear();
        _firstPaintPending = false;
        _lastPaintAt = 0;
        Trail.Data = null;
        TrailBorder.Data = null;
        TrailShadow.Data = null;
        ResetOpacity(Trail);
        ResetOpacity(TrailBorder);
        ResetOpacity(TrailShadow);
    }

    private static void ResetOpacity(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1.0;
    }

    /// <summary>新的内容要显示 / 新手势起手：作废还没跑完的"空帧后隐藏"。</summary>
    public void CancelPendingHide() => _hideToken++;

    /// <summary>
    /// 收起覆盖层：**先把画布清空、等空画布真的上屏，再 Hide()**。
    ///
    /// 所有"收起来"的路径都必须走这里。直接 <see cref="Clear"/> + <see cref="Window.Hide"/>
    /// 会把上一笔的画面留在窗口表面上：隐藏期间 WPF 不提交新帧，下一次 Show()
    /// 先把旧位图贴出来、再等新帧覆盖 —— 就是用户报的"上一笔闪一下"
    /// （详细机制见字段区那段注释）。
    ///
    /// 两帧之后才真藏：第 1 帧把清空后的画布提交给系统，第 2 帧确认它已经上屏。
    /// 这两帧里窗口是空的，用户看不见任何东西；真藏之前要是又起手了
    /// （<see cref="CancelPendingHide"/>），这一下就作废，绝不藏掉新的一笔。
    /// </summary>
    public void HideWhenBlank()
    {
        Clear();
        if (!IsVisible)
        {
            _hideToken++;          // 已经藏着了：没有"旧画面"可留，顺手作废之前的排期
            return;
        }

        int token = ++_hideToken;
        int frames = 0;

        var fallback = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(BlankHideFallbackMs)
        };

        void Finish()
        {
            CompositionTarget.Rendering -= OnFrame;
            try { fallback.Stop(); } catch (Exception) { }
            if (ReferenceEquals(_hideFallback, fallback)) _hideFallback = null;
            if (token != _hideToken) return;   // 中途又起手 / 又有新内容：不许藏
            try { Hide(); } catch (Exception) { /* 窗口已经关了 */ }
        }

        void OnFrame(object? sender, EventArgs e)
        {
            if (token != _hideToken) { CompositionTarget.Rendering -= OnFrame; return; }
            if (++frames < BlankFramesBeforeHide) return;
            Finish();
        }

        CompositionTarget.Rendering += OnFrame;
        /* 上一次没跑完的兜底作废；它自己那帧回调看到号对不上会退订。 */
        try { _hideFallback?.Stop(); } catch (Exception) { }
        _hideFallback = fallback;
        fallback.Tick += (_, _) => Finish();
        fallback.Start();
    }

    private Point ToCanvasPointOf(Point device) => ToCanvas(device.X, device.Y);

    /// <summary>
    /// 认出来了：三支笔一起换成「已识别」色。WG2 只在命中这一刻换一次色，
    /// 之后哪怕继续画也不回退（<c>A/T.cs:130</c>）。
    /// </summary>
    public void MarkRecognized() => ApplyPathColor(RecognizedColor());

    /// <summary>
    /// 未识别收尾：把轨迹换成"未识别"配色。
    ///
    /// 照抄 WG2 <c>A/T.cs:506</c> 的 <c>A_f3.A(gestureIntent != null ? c() : C())</c>——
    /// 识别结果一出来，轨迹窗口先按结果换色，再在 <c>:511</c> 把不透明度归零。
    /// 以前 InputPlus 的未命中分支直接 <c>Clear + Hide</c> 同一拍，屏幕上连
    /// "变灰"的那一帧都没有，看起来就是"划完凭空消失"。
    ///
    /// 这里只负责换色；什么时候收、怎么让那一帧真的画出去，由
    /// <see cref="GestureService"/> 的未命中分支决定。
    /// </summary>
    public void MarkUnrecognized() => ApplyPathColor(NormalColor());

    /// <summary>
    /// 没有匹配到任何手势：轨迹换成「未识别」色，并在名称位置写上「未识别」，
    /// 两者一起留一小会儿再消失（**v1.7.12 恢复**；v1.7.8 为了"完全照抄 WG2"删掉过）。
    ///
    /// 为什么要把这段加回来：删掉之后"画了一笔但没认出来"在屏幕上是**凭空消失** ——
    /// 用户分不清"没认出来"和"根本没触发"（用户原话：应该提示未识别，
    /// 鼠标轨迹和文字一起变灰，然后再消失）。
    ///
    /// 颜色用的是配置里的「未识别」色（<see cref="GestureOptions.PathColorNormal"/>，
    /// 出厂 #00000030 = 19% 的黑，看起来就是灰的）；名字用的是「未识别」标签色
    /// （<c>LabelColorNormal</c>），同样是灰的。两者由调用方同一个定时器一起收。
    /// </summary>
    public void ShowUnrecognized(double px, double py)
    {
        ApplyPathColor(NormalColor());
        ShowLabel(Loc.T("未识别"), px, py, keepTrail: true);
    }

    /// <summary>仅供自检：名称框里写的字（没写字时为空串）。</summary>
    internal string LabelTextForTest => LabelText.Text ?? "";

    /// <summary>仅供自检：轨迹主线的颜色（ARGB）。</summary>
    internal Color TrailColorForTest
        => Trail.Stroke is SolidColorBrush brush ? brush.Color : Colors.Transparent;

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
        /* 要显示了 = 之前排的"空帧后隐藏"不能再跑（否则它会在这一帧里把窗口藏掉）。 */
        CancelPendingHide();
        bool wasHidden = !IsVisible;
        if (wasHidden) Show();

        /* 从隐藏状态出来的第一帧，系统贴的是上一次提交的位图。正常路径下那张图
         * 已经是空的了（见 HideWhenBlank），但窗口一旦被别的路径藏过（用户手动
         * 关掉、WarmUp、异常收尾），这里强制重画一次，让新的一帧尽快盖上去。 */
        if (wasHidden) Root.InvalidateVisual();

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
