using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Modules.HoldDrag;
using Loc = InputPlus.Core.Localization.Loc;

namespace InputPlus.UI.Views;

/// <summary>
/// 「按住拖拽」页。
///
/// 引擎是 AltDrag 的 hooks.c 原样搬进来的（<see cref="AltDragSink"/> + <see cref="HookEngine"/>），
/// 这一页只做两件事：开关它、把它的参数摆出来。
///
/// 参数不是一对一镜像成 InputPlus 的字段，而是整份 JSON 存在
/// <see cref="Core.Settings.AppSettings.HoldDragJson"/> 里：
///   · 引擎自己那份 <see cref="AppConfig"/> 是唯一真相，页面拿它的副本编辑；
///   · 改完写回 JSON → 引擎重新解析，<b>不重装钩子</b>
///     （触发键、阈值、黑名单都是每次事件现读，重装只会让正在拖的窗口断掉）；
///   · 于是它天然跟着密码库、云端备份、整份导入导出走。
/// </summary>
internal partial class HoldDragView : UserControl
{
    private AppHost? _host;

    /// <summary>正在编辑的副本（改完才写回设置）。</summary>
    private AppConfig? _cfg;

    /// <summary>回填控件时置位：避免把程序化赋值当成用户操作再存一遍。</summary>
    private bool _sync;

    /// <summary>参数框逐字符触发，攒一下再交给引擎（否则每敲一个字都要重新解析一遍配置）。</summary>
    private DispatcherTimer? _commitTimer;

    public HoldDragView()
    {
        InitializeComponent();
        BuildChoices();
    }

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    // ---------------- 选项表 ----------------

    /// <summary>下拉一律用 ComboBoxItem{Content,Tag}：这套主题的自定义模板不认 DisplayMemberPath。</summary>
    private void BuildChoices()
    {
        FillActionBox(LeftBox);
        FillActionBox(RightBox);
        FillActionBox(MiddleBox);
        FillActionBox(X1Box);
        FillActionBox(X2Box);

        AutoSnapBox.Items.Add(Item(Loc.T("关"), "0"));
        AutoSnapBox.Items.Add(Item(Loc.T("只贴屏幕边和任务栏"), "1"));
        AutoSnapBox.Items.Add(Item(Loc.T("再加别的窗口的外侧"), "2"));
        AutoSnapBox.Items.Add(Item(Loc.T("再加别的窗口的内侧（默认）"), "3"));

        AutoRemaxBox.Items.Add(Item(Loc.T("关"), "0"));
        AutoRemaxBox.Items.Add(Item(Loc.T("开（拖到别的屏上就最大化）"), "1"));
        AutoRemaxBox.Items.Add(Item(Loc.T("开，并且一秒后自动还原大小"), "2"));
    }

    private static ComboBoxItem Item(string text, string tag) =>
        new() { Content = text, Tag = tag };

    private static void FillActionBox(ComboBox box)
    {
        foreach (string id in Actions.All)
        {
            box.Items.Add(Item(Actions.DisplayName(id), id));
        }
    }

    private static void Select(ComboBox box, string? tag)
    {
        foreach (object o in box.Items)
        {
            if (o is ComboBoxItem it && string.Equals(it.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = it;
                return;
            }
        }
        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    // ---------------- 读 / 写 ----------------

    public void Refresh()
    {
        if (_host == null) return;
        try
        {
            _cfg = _host.HoldDrag.EditableConfig();
            Fill();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("读设置失败：") + ex.Message;
        }
    }

    private void Fill()
    {
        if (_host == null || _cfg == null) return;
        AppConfig cfg = _cfg;
        _sync = true;
        try
        {
            EnabledBox.IsChecked = _host.Settings.HoldDragEnabled;
            TriggerKeyButton.Content = KeysUtil.DisplayName(cfg.TriggerKey);
            SwallowBox.IsChecked = cfg.SwallowTrigger;
            Select(LeftBox, cfg.ActionLeft);
            Select(RightBox, cfg.ActionRight);
            Select(MiddleBox, cfg.ActionMiddle);
            Select(X1Box, cfg.ActionX1);
            Select(X2Box, cfg.ActionX2);
            Select(AutoSnapBox, Num(cfg.AutoSnap));
            SnapThresholdBox.Text = Num(cfg.SnapThreshold);
            AeroBox.IsChecked = cfg.AeroSnap;
            Select(AutoRemaxBox, Num(cfg.AutoRemaximize));
            AutoFocusBox.IsChecked = cfg.AutoFocus;
            MoveRateBox.Text = Num(cfg.MoveRate);
            ResizeRateBox.Text = Num(cfg.ResizeRate);
            ProcessBlacklistBox.Text = cfg.ProcessBlacklist;
            WindowBlacklistBox.Text = cfg.WindowBlacklist;
        }
        finally
        {
            _sync = false;
        }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_host == null) return;
        var svc = _host.HoldDrag;
        string state;
        if (!_host.Settings.HoldDragEnabled)
        {
            state = Loc.T("已关闭");
        }
        else if (svc.Installed)
        {
            state = Loc.T("正在接管鼠标");
        }
        else
        {
            state = Loc.T("没有启动");
        }

        string text = state + " · " + Loc.T("钩子被系统重装 ") + svc.Rescues.ToString(CultureInfo.InvariantCulture) + Loc.T(" 次");
        if (!string.IsNullOrEmpty(svc.LastError))
        {
            text += "\n" + Loc.T("上次出错：") + svc.LastError;
        }
        StatusText.Text = text;
    }

    private void ScheduleCommit()
    {
        if (_sync || _cfg == null || _host == null) return;
        _commitTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _commitTimer.Tick -= OnCommitTick;
        _commitTimer.Tick += OnCommitTick;
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private void OnCommitTick(object? sender, EventArgs e)
    {
        _commitTimer?.Stop();
        CommitNow();
    }

    private void CommitNow()
    {
        if (_host == null || _cfg == null) return;
        try
        {
            _host.HoldDrag.ApplyEditedConfig(_cfg);
            _host.SaveSettings();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("保存失败：") + ex.Message;
            return;
        }
        RefreshStatus();
    }

    // ---------------- 控件事件 ----------------

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_sync || _host == null) return;
        _host.Settings.HoldDragEnabled = EnabledBox.IsChecked == true;
        _host.SaveSettings();
        RefreshStatus();
    }

    private void OnPickTriggerKey(object sender, RoutedEventArgs e)
    {
        if (_host == null || _cfg == null) return;
        var win = new UI.Windows.TriggerKeyWindow(_cfg.TriggerKey);
        if (Window.GetWindow(this) is { } owner) win.Owner = owner;
        if (win.ShowDialog() != true || string.IsNullOrEmpty(win.SelectedKey)) return;
        _cfg.TriggerKey = win.SelectedKey!;
        TriggerKeyButton.Content = KeysUtil.DisplayName(_cfg.TriggerKey);
        ScheduleCommit();
    }

    private void OnResetTriggerKey(object sender, RoutedEventArgs e)
    {
        if (_sync || _cfg == null) return;
        _cfg.TriggerKey = KeysUtil.DefaultTrigger;
        TriggerKeyButton.Content = KeysUtil.DisplayName(_cfg.TriggerKey);
        ScheduleCommit();
    }

    private void OnComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || _cfg == null) return;
        if (sender is not ComboBox box || box.SelectedItem is not ComboBoxItem item) return;
        string tag = (box.Tag as string) ?? "";
        string value = (item.Tag as string) ?? "";
        switch (tag)
        {
            case "ActionLeft": _cfg.ActionLeft = value; break;
            case "ActionRight": _cfg.ActionRight = value; break;
            case "ActionMiddle": _cfg.ActionMiddle = value; break;
            case "ActionX1": _cfg.ActionX1 = value; break;
            case "ActionX2": _cfg.ActionX2 = value; break;
            case "AutoSnap":
                _cfg.AutoSnap = ParseInt(value, _cfg.AutoSnap);
                break;
            case "AutoRemaximize":
                _cfg.AutoRemaximize = ParseInt(value, _cfg.AutoRemaximize);
                break;
            default: return;
        }
        ScheduleCommit();
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_sync || _cfg == null) return;
        if (sender is not CheckBox box) return;
        bool on = box.IsChecked == true;
        switch (box.Tag as string)
        {
            case "SwallowTrigger": _cfg.SwallowTrigger = on; break;
            case "AeroSnap": _cfg.AeroSnap = on; break;
            case "AutoFocus": _cfg.AutoFocus = on; break;
            default: return;
        }
        ScheduleCommit();
    }

    private void OnNumberChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync || _cfg == null) return;
        if (sender is not TextBox box) return;
        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return;
        switch (box.Tag as string)
        {
            case "SnapThreshold": _cfg.SnapThreshold = Math.Clamp(n, 0, 200); break;
            case "MoveRate": _cfg.MoveRate = Math.Clamp(n, 1, 8); break;
            case "ResizeRate": _cfg.ResizeRate = Math.Clamp(n, 1, 8); break;
            default: return;
        }
        ScheduleCommit();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync || _cfg == null) return;
        if (sender is not TextBox box) return;
        string text = box.Text.Replace("\r\n", "\n");
        switch (box.Tag as string)
        {
            case "ProcessBlacklist": _cfg.ProcessBlacklist = text; break;
            case "WindowBlacklist": _cfg.WindowBlacklist = text; break;
            default: return;
        }
        ScheduleCommit();
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var fresh = new AppConfig();
        _cfg = fresh;
        Fill();
        CommitNow();
    }

    // ---------------- 加程序例外 ----------------

    private void OnPickProcess(object sender, RoutedEventArgs e) => AddProcess(PickProgramName(false));

    private void OnPickWindow(object sender, RoutedEventArgs e) => AddProcess(PickProgramName(true));

    private void AddProcess(string name)
    {
        if (_host == null || _cfg == null || string.IsNullOrWhiteSpace(name)) return;
        var lines = new System.Collections.Generic.List<string>(AppRules.Rows(_cfg.ProcessBlacklist));
        if (lines.Exists(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
        {
            _host.Notify(Loc.T("这个程序已经在例外列表里了"));
            return;
        }
        lines.Add(name);
        _cfg.ProcessBlacklist = string.Join("\n", lines);
        ProcessBlacklistBox.Text = _cfg.ProcessBlacklist;
        CommitNow();
    }

    /// <summary>挑一个程序的名字（不是全路径：例外列表认的是程序名）。取消返回空。</summary>
    private string PickProgramName(bool byWindow)
    {
        if (byWindow)
        {
            var picker = new UI.Windows.WindowPickerWindow();
            if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
            picker.ShowDialog();

            IntPtr h = picker.PickedWindow;
            if (h == IntPtr.Zero) return "";

            var info = Core.Windows.WindowService.Describe(h);
            if (info == null || string.IsNullOrEmpty(info.ExePath))
            {
                _host?.Notify(Loc.T("取不到这个窗口的程序，改用「选择程序…」挑 exe"));
                return "";
            }
            return FileNameOf(info.ExePath);
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择要从接管里排除的程序"),
            Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return "";
        return FileNameOf(dlg.FileName);
    }

    private static string FileNameOf(string path)
    {
        try { return System.IO.Path.GetFileName(path); } catch { return ""; }
    }

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    /* ---- 出图用的三个小钩子（--render 走这里，正常运行时没人调） ----
       这一页默认是关着的、参数也是默认值，直接拍出来是一屏灰字；
       这里只改界面上的显示，不写设置，拍完页面照旧。 */
    internal void SetSampleForRender()
    {
        if (_cfg == null)
        {
            return;
        }
        _cfg.TriggerKey = "K:LAlt";
        _cfg.SwallowTrigger = false;
        _cfg.ActionLeft = Actions.Move;
        _cfg.ActionMiddle = Actions.Resize;
        _cfg.ActionRight = Actions.Resize;
        _cfg.ActionX1 = Actions.Center;
        _cfg.ActionX2 = Actions.Nothing;
        _cfg.AutoSnap = 3;
        _cfg.SnapThreshold = 20;
        _cfg.AeroSnap = true;
        _cfg.AutoRemaximize = 1;
        _cfg.AutoFocus = false;
        _cfg.MoveRate = 2;
        _cfg.ResizeRate = 3;
        _cfg.ProcessBlacklist = "wps.exe" + Environment.NewLine + "code.exe";
        Fill();
        RefreshStatus();
    }

    internal void ScrollSnapForRender()
    {
        if (LeftScroll == null)
        {
            return;
        }
        // BringIntoView 在还没排完版时会被忽略，这里直接按控件坐标算偏移。
        Point at = SnapThresholdBox.TranslatePoint(new Point(0.0, 0.0), LeftScroll);
        LeftScroll.ScrollToVerticalOffset(LeftScroll.VerticalOffset + at.Y - 120.0);
    }

    internal void ScrollListsForRender()
    {
        if (LeftScroll == null)
        {
            return;
        }
        // 让「程序例外」标题落在顶部，出图时不会把上一节的标题切一半。
        Point at = ProcessBlacklistBox.TranslatePoint(new Point(0.0, 0.0), LeftScroll);
        LeftScroll.ScrollToVerticalOffset(LeftScroll.VerticalOffset + at.Y - 370.0);
    }
}
