using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using InputPlus.Core.Settings;

namespace InputPlus.UI.Views;

/// <summary>
/// 平滑滚动页。
///
/// 版式与其它设置页一致：顶部一张卡（只有总开关），左边参数，右边程序例外。
/// 这一页只改 <see cref="AppSettings.Scroll"/> 与 <see cref="AppSettings.ScrollApps"/>，
/// 别的东西一概不碰 —— 滚轮的手感是整台机器的，改动越少越不容易互相干扰。
/// </summary>
public partial class ScrollView : UserControl
{
    private AppHost? _host;

    /// <summary>回填控件时置位：避免把程序化赋值当成用户操作再存一遍。</summary>
    private bool _sync;

    public ScrollView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        var o = _host.Settings.Scroll ??= new ScrollOptions();

        _sync = true;
        try
        {
            EnabledBox.IsChecked = o.Enabled;
            StepBox.Text = o.StepSize.ToString("0", CultureInfo.InvariantCulture);
            DurationBox.Text = o.AnimationTime.ToString("0", CultureInfo.InvariantCulture);
            PulseSlider.Value = Math.Clamp(o.PulseScale, PulseSlider.Minimum, PulseSlider.Maximum);
            PulseText.Text = o.PulseScale.ToString("0.0", CultureInfo.InvariantCulture);
            AccelBox.IsChecked = o.Acceleration;
            AccelDeltaBox.Text = o.AccelerationDelta.ToString("0", CultureInfo.InvariantCulture);
            AccelMaxBox.Text = o.AccelerationMax.ToString("0.#", CultureInfo.InvariantCulture);
            HorizontalBox.IsChecked = o.Horizontal;
            ShiftBox.IsChecked = o.HorizontalShift;
            ReverseBox.IsChecked = o.Reverse;
            TouchpadBox.IsChecked = o.IgnoreTouchpad;
        }
        finally { _sync = false; }

        BuildApps();
        UpdateStatus();
    }

    // ---------------- 保存 ----------------

    private ScrollOptions Options => _host!.Settings.Scroll ??= new ScrollOptions();

    private void Save()
    {
        if (_sync || _host == null) return;
        _host.SaveSettings();
        UpdateStatus();
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_sync || _host == null) return;
        Options.Enabled = EnabledBox.IsChecked == true;
        Save();
    }

    /// <summary>四个数字框共用：只认能解析成数字的输入，越界就夹到范围内。</summary>
    private void OnNumberChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync || _host == null) return;
        var o = Options;
        string text = (sender as TextBox)?.Text ?? "";
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return;

        if (ReferenceEquals(sender, StepBox)) o.StepSize = Math.Clamp(v, 10, 1000);
        else if (ReferenceEquals(sender, DurationBox)) o.AnimationTime = Math.Clamp(v, 40, 2000);
        else if (ReferenceEquals(sender, AccelDeltaBox)) o.AccelerationDelta = Math.Clamp(v, 5, 1000);
        else if (ReferenceEquals(sender, AccelMaxBox)) o.AccelerationMax = Math.Clamp(v, 1, 20);
        else return;

        Save();
    }

    private void OnPulseChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync || _host == null) return;
        Options.PulseScale = Math.Round(e.NewValue, 1);
        if (PulseText != null) PulseText.Text = Options.PulseScale.ToString("0.0", CultureInfo.InvariantCulture);
        Save();
    }

    /// <summary>开关类：Tag 就是 ScrollOptions 上的字段名，一个处理器全包。</summary>
    private void OnToggleField(object sender, RoutedEventArgs e)
    {
        if (_sync || _host == null) return;
        var box = sender as CheckBox;
        bool on = box?.IsChecked == true;
        var o = Options;

        switch (box?.Tag as string)
        {
            case "Acceleration": o.Acceleration = on; break;
            case "Horizontal": o.Horizontal = on; break;
            case "HorizontalShift": o.HorizontalShift = on; break;
            case "Reverse": o.Reverse = on; break;
            case "IgnoreTouchpad": o.IgnoreTouchpad = on; break;
            default: return;
        }
        Save();
    }

    // ---------------- 程序例外 ----------------

    private sealed record AppRow(ScrollAppRule Rule, string Name, string StateText, string ToggleText);

    private List<ScrollAppRule> Rules => _host!.Settings.ScrollApps ??= new List<ScrollAppRule>();

    private void BuildApps()
    {
        if (_host == null) return;
        var rows = new List<AppRow>();
        foreach (var rule in Rules)
        {
            rows.Add(new AppRow(
                rule,
                System.IO.Path.GetFileNameWithoutExtension(rule.AppPath),
                rule.Enabled ? Loc.T("跟随全局参数") : Loc.T("不平滑，原样交给系统"),
                rule.Enabled ? Loc.T("禁用") : Loc.T("恢复")));
        }

        AppList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>入口一：从文件管理器挑 exe（程序没在运行也能加）。</summary>
    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择要单独设置的程序"),
            Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        AddApp(dialog.FileName);
    }

    /// <summary>
    /// 入口二：十字准星点选已开的窗口，取它的程序名。
    /// 和其它页共用 <see cref="UI.Windows.WindowPickerWindow"/>。
    /// </summary>
    private void OnAddAppWindow(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var picker = new UI.Windows.WindowPickerWindow();
        if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
        picker.ShowDialog();

        IntPtr h = picker.PickedWindow;
        if (h == IntPtr.Zero) return;                       // 用户按了 Esc

        var info = Core.Windows.WindowService.Describe(h);
        if (info == null || string.IsNullOrEmpty(info.ExePath))
        {
            _host.Notify(Loc.T("取不到这个窗口的程序，改用「选择程序…」挑 exe"));
            return;
        }
        AddApp(info.ExePath);
    }

    /// <summary>两个入口共用：去重、落盘、刷列表、提示，一条龙。</summary>
    private void AddApp(string path)
    {
        if (_host == null || string.IsNullOrWhiteSpace(path)) return;

        string name = System.IO.Path.GetFileName(path);
        if (name.Length == 0) name = path;

        /* 匹配既认全路径也认文件名（见 ScrollAppRule.AppPath 的注释），
           所以去重也要两种都看一遍，否则同一个程序会以两种写法进来两次。 */
        if (Rules.Any(r => string.Equals(r.AppPath, path, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(r.AppPath, name, StringComparison.OrdinalIgnoreCase)))
        {
            _host.Notify(Loc.T("这个程序已经在列表里了。"));
            return;
        }

        Rules.Add(new ScrollAppRule { AppPath = path });
        Save();
        BuildApps();
        _host.Notify(Loc.F("已加入例外列表：{0}", System.IO.Path.GetFileNameWithoutExtension(path)));
    }

    private void OnToggleApp(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if ((sender as Button)?.Tag is not ScrollAppRule rule) return;
        rule.Enabled = !rule.Enabled;
        Save();
        BuildApps();
    }

    private void OnDeleteApp(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if ((sender as Button)?.Tag is not ScrollAppRule rule) return;
        Rules.Remove(rule);
        Save();
        BuildApps();
    }

    /// <summary>出图用：临时塞两条例外程序，看看模板里的名字、状态与按钮长什么样。只改内存不落盘。</summary>
    internal void SetSampleAppsForRender()
    {
        if (_host == null) return;
        var list = Rules;
        list.Clear();
        list.Add(new ScrollAppRule { AppPath = "C:/Program Files/Adobe/Photoshop 2024/Photoshop.exe" });
        list.Add(new ScrollAppRule { AppPath = "C:/Games/Starfield/Starfield.exe", Enabled = false, StepSize = 240 });
        BuildApps();
        UpdateLayout();
    }

    /// <summary>出图用：拍完把假数据撤掉，别留给下一页。</summary>
    internal void ClearSampleAppsForRender()
    {
        if (_host == null) return;
        Rules.Clear();
        BuildApps();
        UpdateLayout();
    }

    // ---------------- 状态行 ----------------

    private void UpdateStatus()
    {
        if (_host == null || StatusText == null) return;

        var o = Options;
        if (!o.Enabled)
        {
            StatusText.Text = Loc.T("已关闭：滚轮事件原样交给系统，一个字节都不改。");
            return;
        }
        if (!_host.Scroll.Installed)
        {
            StatusText.Text = Loc.T("鼠标钩子没装上，平滑滚动暂时不生效。");
            return;
        }

        StatusText.Text = Loc.F("已接管 {0} 次滚轮，注入 {1} 像素。", _host.Scroll.SmoothCount, _host.Scroll.SentPixels);
    }
}
