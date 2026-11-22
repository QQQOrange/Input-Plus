using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InputPlus.Core;
using InputPlus.Core.Gestures;
using InputPlus.Core.Settings;
using InputPlus.UI.Windows;

namespace InputPlus.UI.Views;

/// <summary>
/// 手势设置面板：开关快捷键 + 手势外观 + 手势参数。
///
/// 这三块以前摊在手势页的「总开关条」上，一条开关旁边挂着二十多个控件 ——
/// 页面每次打开都在算能滚多高，真正要看的手势列表被压成一条缝。
/// 现在整块搬进 <see cref="GestureSettingsWindow"/>：页面只留开关和「录制新手势」，
/// 想调外观 / 参数的点右边那颗齿轮。
///
/// 所有改动照旧即时落盘（<c>_host.SaveSettings()</c>），没有"确定/取消"——
/// 和这个软件其它页面一致，也和 WGestures 2 的设置窗一致。
/// </summary>
public partial class GestureOptionsView : UserControl
{
    private AppHost? _host;
    private bool _loading;

    public GestureOptionsView()
    {
        InitializeComponent();
    }

    /// <summary>接管设置对象并灌一次初值。<c>host</c> 为空时整个面板只显示默认值。</summary>
    public void Initialize(AppHost host)
    {
        _host = host;
        Sync();
    }

    /// <summary>把当前配置灌进控件（只读配置，不触发保存）。</summary>
    public void Sync()
    {
        if (_host == null) return;
        _loading = true;
        try
        {
            var opts = _host.Settings.GestureOptions;
            string hotkey = opts.ToggleHotkey ?? "";
            bool hotkeySet = !string.IsNullOrWhiteSpace(hotkey);
            /* 没设过的时候框里留一行浅色的「未设置」：空框看起来像刚被人清掉了。 */
            ToggleHotkeyBox.Text = hotkeySet ? hotkey : Loc.T("未设置");
            if (TryFindResource(hotkeySet ? "Brush.TextPrimary" : "Brush.TextTertiary") is Brush hotkeyBrush)
                ToggleHotkeyBox.Foreground = hotkeyBrush;

            MinMoveBox.Text = opts.MinMove.ToString();
            StartTimeoutBox.Text = opts.StartTimeoutMs.ToString();
            ThresholdBox.Text = opts.WgThreshold.ToString("0.000");
            ThresholdBox.ToolTip = Loc.T("笔画形状距离不超过它就认。默认 0.050，越小越严（0.005~0.3）。");
            ActivationBox.Text = opts.ActivationDistance.ToString();
            TurnBox.Text = opts.TurnTolerance.ToString("0.00");
            TrailOpacitySlider.Value = Math.Clamp(opts.TrailOpacity, 0.2, 1.0);
            TrailOpacityText.Text = $"{Math.Round(TrailOpacitySlider.Value * 100):0}%";
            bool pointerTarget = opts.UsesPointerTarget;
            TargetPointerRadio.IsChecked = pointerTarget;
            TargetActiveRadio.IsChecked = !pointerTarget;
            UpdateTargetModeHint(opts);
            UpdateColorSwatches();
            SyncWg2Controls(opts);
            SyncLabelStyle(_host.Settings.GestureLabel);
            SyncMiddleMenu(_host.Settings);
        }
        finally { _loading = false; }
    }

    // ─────────────────── 中键菜单（v1.3.16） ───────────────────

    /// <summary>把中键菜单那三个控件灌成当前配置（只读配置，不触发保存）。</summary>
    private void SyncMiddleMenu(AppSettings s)
    {
        MiddleMenuEnabledBox.IsChecked = s.MiddleClickMenuEnabled;

        /* 下拉每次重建：菜单管理里加了/删了菜单，回到这一页就该看到最新的。
         * 重建期间 _loading 还是 true（Sync 的外层），所以 SelectionChanged 不会写盘。 */
        MiddleMenuPickBox.Items.Clear();
        MiddleMenuPickBox.Items.Add(new ComboBoxItem
        {
            Content = Loc.T("内置：收藏 / 最近 / 命令 / 设置"),
            Tag = "",
        });
        foreach (var menu in s.GestureMenus)
        {
            MiddleMenuPickBox.Items.Add(new ComboBoxItem
            {
                Content = menu.DisplayName,
                Tag = menu.Id,
            });
        }

        /* 指向的菜单可能已经被删了：这时下拉停在第一项（内置）。
         * 不在这里顺手改配置 —— 用户只是打开设置页看一眼，不该被悄悄改掉值。 */
        string wanted = (s.MiddleClickMenuId ?? "").Trim();
        int index = 0;
        for (int i = 1; i < MiddleMenuPickBox.Items.Count; i++)
        {
            if (MiddleMenuPickBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        MiddleMenuPickBox.SelectedIndex = index;

        var scope = MiddleMenuScope.ToScope(s.MiddleClickMenuScope);
        MiddleMenuScopeDesktopRadio.IsChecked = scope == MiddleClickMenuScope.Desktop;
        MiddleMenuScopeAllRadio.IsChecked = scope == MiddleClickMenuScope.DesktopAndExplorer;

        UpdateMiddleMenuAvailability();
    }

    /// <summary>总开关关着时，下面两项变灰 —— 免得用户以为"选了菜单却没反应"。</summary>
    private void UpdateMiddleMenuAvailability()
    {
        bool on = MiddleMenuEnabledBox.IsChecked == true;
        MiddleMenuPickBox.IsEnabled = on;
        MiddleMenuScopeDesktopRadio.IsEnabled = on;
        MiddleMenuScopeAllRadio.IsEnabled = on;
    }

    private void OnMiddleMenuToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null) return;
        _host.Settings.MiddleClickMenuEnabled = MiddleMenuEnabledBox.IsChecked == true;
        UpdateMiddleMenuAvailability();
        _host.SaveSettings();
    }

    private void OnMiddleMenuPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _host == null) return;
        _host.Settings.MiddleClickMenuId =
            (MiddleMenuPickBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _host.SaveSettings();
    }

    private void OnMiddleMenuScopeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null) return;
        _host.Settings.MiddleClickMenuScope = MiddleMenuScope.ToStorage(
            MiddleMenuScopeDesktopRadio.IsChecked == true
                ? MiddleClickMenuScope.Desktop
                : MiddleClickMenuScope.DesktopAndExplorer);
        _host.SaveSettings();
    }

    /// <summary>渲染工具用：滚回顶上（内容现在分三张卡，没有折叠面板了）。</summary>
    internal void ExpandAllForRender() => OptionsScroll.ScrollToTop();

    /// <summary>渲染用：滚到卡片底部（参数卡的最后几行）。</summary>
    internal void ScrollToEndForRender() => OptionsScroll.ScrollToEnd();


    /// <summary>
    /// 切换「手势作用目标」。只改一个字段：匹配哪个程序、执行前要不要切窗口
    /// 都由 GestureService.ResolveTarget 在每次起手时现算，不用重挂钩子。
    /// </summary>
    private void OnTargetModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        o.TargetMode = TargetPointerRadio.IsChecked == true
            ? GestureOptions.TargetPointer
            : GestureOptions.TargetActive;
        UpdateTargetModeHint(o);
        _host.SaveSettings();
    }

    /// <summary>右边的说明随选择变：两种模式最直观的差别就是"会不会把窗口切到前面"。</summary>
    private void UpdateTargetModeHint(GestureOptions o)
    {
        TargetModeHint.Text = o.UsesPointerTarget
            ? Loc.T("动作发给指针底下的那个程序，执行前把它切到前面。")
            : Loc.T("动作只发给当前的活动窗口，不切换窗口。");
    }

    private void OnTrailOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        o.TrailOpacity = Math.Clamp(e.NewValue, 0.2, 1.0);
        TrailOpacityText.Text = $"{Math.Round(o.TrailOpacity * 100):0}%";
        _host.SaveSettings();
    }

    // ─────────────────── WGestures 2 的手势外观偏好 ───────────────────

    /// <summary>把 WG2 那套外观偏好灌进控件（只读配置，不触发保存）。</summary>
    private void SyncWg2Controls(GestureOptions o)
    {
        PathBox.IsChecked = o.ShowPath;
        PathBorderBox.IsChecked = o.ShowPathBorder;
        GestureNameBox.IsChecked = o.ShowGestureName;
        FadeOutBox.IsChecked = o.FadeOutExecuted;
        TimeoutIndicatorBox.IsChecked = o.ShowStartTimeoutIndicator;
        FullscreenBox.IsChecked = o.DisableInFullscreen;
        TopmostBox.IsChecked = o.PrefWindowTopmost;

        PathWidthSlider.Value = Math.Clamp(o.PathLineWidth, 1.0, 4.0);
        PathWidthText.Text = PathWidthSlider.Value.ToString("0.0");

        /* 笔画平滑：只影响画出来的那条线，不影响识别（点列照旧）。 */
        SmoothStrokesBox.IsChecked = o.SmoothStrokes;

        UpdateWg2Swatches();
    }


    /// <summary>
    /// 六个外观勾选共用：Tag 就是字段名。
    /// ShowPath 是计算属性，真正的开关是 ShowTrail —— 两个一起写，
    /// 否则老配置里 ShowTrail=false 会把刚打开的勾选又压回去。
    /// </summary>
    private void OnWg2ToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null || sender is not CheckBox box) return;
        var o = _host.Settings.GestureOptions;
        bool on = box.IsChecked == true;
        switch (box.Tag as string)
        {
            case "ShowPath":
                o.ShowPath = on;
                o.ShowTrail = on;
                break;
            case "ShowPathBorder": o.ShowPathBorder = on; break;
            case "ShowGestureName": o.ShowGestureName = on; break;
            case "FadeOutExecuted": o.FadeOutExecuted = on; break;
            case "SmoothStrokes": o.SmoothStrokes = on; break;
            case "ShowStartTimeoutIndicator": o.ShowStartTimeoutIndicator = on; break;
            case "DisableInFullscreen": o.DisableInFullscreen = on; break;
            case "PrefWindowTopmost":
                o.PrefWindowTopmost = on;
                if (Window.GetWindow(this) is Window win) win.Topmost = on;
                break;
            default: return;
        }
        _host.SaveSettings();
        UpdateWg2Swatches();
    }

    /// <summary>轨迹粗细滑杆。</summary>
    private void OnWg2SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        if (ReferenceEquals(sender, PathWidthSlider))
        {
            o.PathLineWidth = Math.Clamp(e.NewValue, 1.0, 4.0);
            PathWidthText.Text = o.PathLineWidth.ToString("0.0");
        }
        else return;
        _host.SaveSettings();
    }

    /// <summary>两个色块：轨迹（已识别 / 未识别）。名称的颜色是另一组字段，由 OnPickLabelColor 处理。</summary>
    private void OnPickWg2Color(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null || sender is not FrameworkElement el) return;
        var o = _host.Settings.GestureOptions;
        string tag = el.Tag as string ?? "";
        string current = tag switch
        {
            "pathRecognized" => o.PathColorRecognized,
            "pathNormal" => o.PathColorNormal,
            _ => "",
        };
        if (current.Length == 0) return;

        /* ColorDialog 没有 alpha：PickRgba 会先按不透明色挑、挑完把原 alpha 贴回去。 */
        if (ColorSwatch.PickRgba(current) is not { } hex) return;
        switch (tag)
        {
            case "pathRecognized": o.PathColorRecognized = hex; break;
            case "pathNormal": o.PathColorNormal = hex; break;
            default: return;
        }
        _host.SaveSettings();
        UpdateWg2Swatches();
    }

    private void UpdateWg2Swatches()
    {
        if (_host == null) return;
        var o = _host.Settings.GestureOptions;
        SetRgbaSwatch(PathRecognizedSwatch, o.PathColorRecognized);
        SetRgbaSwatch(PathNormalSwatch, o.PathColorNormal);
    }

    /// <summary>
    /// 色块按原色的 alpha 画，但垫一个下限（0x66）：WG2 出厂的未识别色是 0x30 的 alpha，
    /// 原样画几乎看不见，纯不透明又会把「淡」这个信息丢掉。原始 #RRGGBBAA 放 ToolTip。
    /// </summary>
    private static void SetRgbaSwatch(Border swatch, string hex) => ColorSwatch.Apply(swatch, hex);

    // ─────────────────── 名称框的外观（v1.6.3 从「通知样式」搬回来） ───────────────────

    /* 这五项只属于手势名称，和右下角的提示条是两套配置：
     * v1.6.0 把它们合并成全局「通知样式」，结果两边都调不顺手 ——
     * 名称框要跟着手势挑高度、要分两档底色，提示条固定贴角、只有一种底色。 */

    /// <summary>把名称框外观灌进控件并刷一次预览（只读配置，不触发保存）。</summary>
    private void SyncLabelStyle(GestureLabelStyle style)
    {
        LabelBorderBox.IsChecked = style.ShowBorder;

        LabelBorderWidthSlider.Value = Math.Clamp(style.BorderWidth, 0.5, 3.0);
        LabelBorderWidthText.Text = LabelBorderWidthSlider.Value.ToString("0.0");

        LabelPosSlider.Value = Math.Clamp(style.Position, 0.0, 1.0);
        LabelPosText.Text = $"{Math.Round(LabelPosSlider.Value * 100):0}%";

        UpdateLabelSwatches();
        UpdateLabelPreview();
    }

    /// <summary>
    /// 预览：直接调 GestureOverlayWindow 的配色算式（不必真弹一个全屏覆盖层）。
    /// 预览另算一遍的话，改完设置和真弹出来的样子迟早会对不上。
    /// </summary>
    private void UpdateLabelPreview()
    {
        if (_host == null) return;
        var style = _host.Settings.GestureLabel;
        GestureOverlayWindow.ApplyLabelLook(LabelPreviewNormal, style, executed: false);
        GestureOverlayWindow.ApplyLabelLook(LabelPreviewExecuted, style, executed: true);
    }

    private void OnLabelBorderToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null) return;
        _host.Settings.GestureLabel.ShowBorder = LabelBorderBox.IsChecked == true;
        _host.SaveSettings();
        UpdateLabelPreview();
    }

    /// <summary>描边粗细：写回配置后立刻重画预览。</summary>
    private void OnLabelBorderWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _host == null) return;
        var style = _host.Settings.GestureLabel;
        style.BorderWidth = Math.Round(Math.Clamp(e.NewValue, 0.5, 3.0), 1);
        LabelBorderWidthText.Text = style.BorderWidth.ToString("0.0");
        _host.SaveSettings();
        UpdateLabelPreview();
    }

    /// <summary>出现高度：0 贴底、1 贴顶（算式见 GestureOverlayWindow.ShowLabel）。</summary>
    private void OnLabelPositionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _host == null) return;
        var style = _host.Settings.GestureLabel;
        style.Position = Math.Clamp(e.NewValue, 0.0, 1.0);
        LabelPosText.Text = $"{Math.Round(style.Position * 100):0}%";
        _host.SaveSettings();
    }

    /// <summary>平时 / 执行后 两个底色，Tag 决定改哪个（取色保留原透明度）。</summary>
    private void OnPickLabelColor(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null || sender is not FrameworkElement el) return;
        string tag = el.Tag as string ?? "";
        var style = _host.Settings.GestureLabel;
        string current = tag switch
        {
            "normal" => style.ColorNormal,
            "executed" => style.ColorExecuted,
            _ => "",
        };
        if (current.Length == 0) return;

        if (ColorSwatch.PickRgba(current) is not { } hex) return;
        if (tag == "normal") style.ColorNormal = hex;
        else style.ColorExecuted = hex;
        _host.SaveSettings();
        UpdateLabelSwatches();
        UpdateLabelPreview();
    }

    private void UpdateLabelSwatches()
    {
        if (_host == null) return;
        var style = _host.Settings.GestureLabel;
        SetRgbaSwatch(LabelNormalSwatch, style.ColorNormal);
        SetRgbaSwatch(LabelExecutedSwatch, style.ColorExecuted);
    }

    private void OnOptionsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        if (int.TryParse(MinMoveBox.Text, out int min) && min is >= 4 and <= 400) o.MinMove = min;
        if (int.TryParse(StartTimeoutBox.Text, out int timeout) && timeout is >= 100 and <= 3000) o.StartTimeoutMs = timeout;
        if (double.TryParse(ThresholdBox.Text, out double th) && th is >= 0.005 and <= 0.3)
            o.WgThreshold = th;
        if (int.TryParse(ActivationBox.Text, out int act) && act is >= 4 and <= 200) o.ActivationDistance = act;
        if (double.TryParse(TurnBox.Text, out double turn) && turn is >= 0 and <= 1) o.TurnTolerance = turn;
        _host.SaveSettings();
    }

    private void OnCaptureToggleHotkey(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        KeyCaptureWindow.Capture(result =>
        {
            if (string.IsNullOrWhiteSpace(result.Combo)) return;
            _host.Settings.GestureOptions.ToggleHotkey = result.Combo;
            _host.SaveSettings();
            ToggleHotkeyBox.Text = result.Combo;
            if (TryFindResource("Brush.TextPrimary") is Brush hotkeyBrush)
                ToggleHotkeyBox.Foreground = hotkeyBrush;
            _host.Gestures.ApplyHotkey();
            _host.Notify(Loc.F("鼠标手势开关快捷键已设为 {0}", result.Combo));
        }, title: Loc.T("按下鼠标手势总开关快捷键"));
    }

    /// <summary>出厂状态：空字符串 → 服务把热键注销，不占用任何组合键。</summary>
    private void OnClearToggleHotkey(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.Settings.GestureOptions.ToggleHotkey = "";
        _host.SaveSettings();
        ToggleHotkeyBox.Text = Loc.T("未设置");
        if (TryFindResource("Brush.TextTertiary") is Brush offBrush)
            ToggleHotkeyBox.Foreground = offBrush;
        _host.Gestures.ApplyHotkey();
        _host.Notify(Loc.T("鼠标手势开关快捷键已清除"));
    }

    // ───────────────────────── 轨迹颜色 ─────────────────────────

    private void OnPickTrailColor(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        string key = (sender as FrameworkElement)?.Tag as string ?? "uniform";
        var o = _host.Settings.GestureOptions;
        string initial = key == "uniform" ? o.TrailColor : o.TrailColorFor(key);
        var picked = PickColor(initial);
        if (picked == null) return;

        string hex = $"#{picked.Value.R:X2}{picked.Value.G:X2}{picked.Value.B:X2}";
        if (key == "uniform")
        {
            /* 统一色就是“所有键都回落到它”：清掉分键覆盖，
             * 否则改了统一色看起来没反应。 */
            o.TrailColor = hex;
            o.ButtonTrailColors ??= new Dictionary<string, string>();
            o.ButtonTrailColors.Clear();
        }
        else
        {
            o.ButtonTrailColors ??= new Dictionary<string, string>();
            o.ButtonTrailColors[key] = hex;
        }
        _host.SaveSettings();
        UpdateColorSwatches();
    }

    private void OnResetTrailColors(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.Settings.GestureOptions.ButtonTrailColors ??= new Dictionary<string, string>();
        _host.Settings.GestureOptions.ButtonTrailColors.Clear();
        _host.SaveSettings();
        UpdateColorSwatches();
    }

    private void UpdateColorSwatches()
    {
        if (_host == null) return;
        var o = _host.Settings.GestureOptions;

        /* 没配分键颜色时，画出来的是 WG2 的「未识别」色（RGBA），
           色块也跟着显示它 —— 老版本这里显示的是统一色，而那个色已经不再参与绘制。 */
        SetButtonSwatch(RightSwatch, o, "right");
        SetButtonSwatch(MiddleSwatch, o, "middle");
        SetButtonSwatch(X1Swatch, o, "x1");
        SetButtonSwatch(X2Swatch, o, "x2");
        SetButtonSwatch(LeftSwatch, o, "left");
    }

    private static void SetButtonSwatch(Border swatch, GestureOptions o, string key)
    {
        if (HasOverride(o, key))
        {
            SetSwatch(swatch, o.TrailColorFor(key), true);
            return;
        }
        SetRgbaSwatch(swatch, o.PathColorNormal);
        /* 淡到看得出是「跟随」，不是真的配了这色。 */
        swatch.Opacity = 0.28;
        swatch.ToolTip = Loc.T("未单独配色，跟随「未识别」色");
    }

    private static bool HasOverride(GestureOptions o, string key) =>
        o.ButtonTrailColors != null &&
        o.ButtonTrailColors.TryGetValue(key, out string? value) &&
        !string.IsNullOrWhiteSpace(value);

    private static void SetSwatch(Border swatch, string color, bool custom)
    {
        try
        {
            if (new BrushConverter().ConvertFromString(color) is Brush brush)
                swatch.Background = brush;
        }
        catch { /* 配置里写了坏颜色就当默认色，不要拦住整个设置窗 */ }
        swatch.Opacity = custom ? 1 : 0.45;
        swatch.ToolTip = color;
    }

    private static System.Drawing.Color? PickColor(string initial) => ColorSwatch.Pick(initial);
}
