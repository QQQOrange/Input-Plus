using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Input;
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
            TriggerHoldBox.Text = Num(cfg.TriggerHoldMs);
            SwallowTriggerBox.IsChecked = cfg.SwallowTrigger;
            FillDeviceScope(cfg.Device);
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
        RefreshProcessList();
        RefreshStatus();
    }

    /// <summary>
    /// 把「程序例外」那串文本摊成一行一条的清单。
    ///
    /// 清单是只读投影，真相永远是 <see cref="AppConfig.ProcessBlacklist"/> 那串文本
    /// （引擎也读那一份）；所以手输框改一个字，这里跟着重画一遍就够了。
    /// </summary>
    private void RefreshProcessList()
    {
        if (_cfg == null || ProcessExceptionList == null) return;
        var rows = new System.Collections.Generic.List<HoldDragExceptionRow>();
        foreach (string name in AppRules.Rows(_cfg.ProcessBlacklist))
        {
            rows.Add(new HoldDragExceptionRow(name, Loc.T("完全不接管"), name));
        }
        ProcessExceptionList.ItemsSource = rows;
        if (ProcessExceptionEmpty != null)
        {
            ProcessExceptionEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
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

    /// <summary>
    /// 设备清单（v1.6.1 起多选）：一台键盘一行，最前面一个方框。
    ///
    /// 语义：勾上的键盘按的触发键才算数；一台都不勾 = 所有键盘。
    /// 为什么做成"弹层 + 方框"而不是下拉：下拉单选表达不了"勾好几台"；
    /// 而且「按键映射」页的设备清单就是这个样子，两页共用同一套手势
    /// （点胶囊弹清单、方框＝这台算不算），用户不用重新学。
    /// </summary>
    private void FillDeviceScope(string configured)
    {
        if (DeviceList == null) return;

        DeviceList.Items.Clear();
        string[] keys = AppConfig.SplitDevices(configured);

        /* 三态：留空 = 所有键盘、清单 = 只认这几台、AppConfig.NoDevice = 一台都不认。
         *
         * 「所有键盘」这一行是总开关：勾着的时候下面每台都打勾（"它们全在生效"）。
         * 取消它 → 变成"一台都不认"，下面的勾跟着一起消失。
         *
         * 之所以要有 NoDevice 这个哨兵：取消完如果存成空串，下一帧读回来又是
         * "所有键盘"，勾会啪地全打回去（v1.6.3 的鬼畜就是这么来的）。 */
        bool none = AppConfig.DeviceScopeIsNone(configured);
        bool all = !none && keys.Length == 0;

        // 第一行是"不挑设备"：勾上它就等于清空下面的选择。
        DeviceList.Items.Add(DeviceRow("", Loc.T("所有键盘"), Loc.T("不挑设备，哪个键盘按都算"), false, all, 0));

        var devices = InputDevices.All().Where(d => d.HasKeyboard).ToList();

        /* 同名设备（"HID Keyboard Device" 能连着来好几条）用 VID:PID 当副标题，
           否则用户根本分不清勾的到底是哪一只；名字独一无二时照旧显示类别。 */
        var dup = devices
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (InputDeviceInfo dev in devices)
        {
            known.Add(dev.Key);
            string alias = InputDevices.AliasOf(dev.Key);
            string detail = alias.Length > 0
                ? dev.Name
                : dup.Contains(dev.Name) ? CompactId(dev.Key) : dev.Kind;
            bool on = all || keys.Any(k => string.Equals(k, dev.Key, StringComparison.OrdinalIgnoreCase));
            DeviceList.Items.Add(DeviceRow(dev.Key, alias.Length > 0 ? alias : dev.Name, detail, dev.IsMouse, on, ++index));
        }

        // 勾过但这会儿没接上的设备（拔了、换了口）：照样列出来并保持勾选，
        // 别让设置悄悄退回"所有键盘"。
        foreach (string key in keys)
        {
            // "none" 是"一台都不勾"的哨兵值，不是设备；当成设备列出来会多一行「none 这次没接上」。
            if (AppConfig.DeviceScopeIsNone(key)) continue;
            if (known.Contains(key)) continue;
            DeviceList.Items.Add(DeviceRow(key, InputDevices.DisplayNameOf(key), Loc.T("这次没接上"), false, true, ++index));
        }

        DeviceText.Text = all
            ? Loc.T("所有键盘")
            : none
                ? Loc.T("未勾选任何键盘")
                : keys.Length == 1
                    ? InputDevices.DisplayNameOf(keys[0])
                    : Loc.F("{0} 台键盘", keys.Length);
        DeviceHint.Text = devices.Count == 0
            ? Loc.T("没找到键盘：插上设备后点「刷新」。")
            : all
                ? Loc.F("共 {0} 台键盘。现在不挑设备：哪个键盘按的触发键都算，所以下面都打勾。要去掉某几台，就把它们的勾取消。", devices.Count)
                : none
                    ? Loc.F("共 {0} 台键盘。现在一台都没勾：触发键按了也不会动窗口。勾上要认的那几台，或者勾最上面的「所有键盘」。", devices.Count)
                    : Loc.F("共 {0} 台键盘。只有勾着的 {1} 台按的触发键才算数。", devices.Count, keys.Length);
    }

    /// <summary>
    /// 清单里的一行：方框在左，然后是"设备类型图标 + 序号"，再往右是名字与类别。
    ///
    /// 以前只有名字和类别，几条全是文字的时候一眼看不出"这是键盘还是鼠标"，
    /// 也数不清到底列了几台（同名设备更是完全糊在一起）。现在每一行前面有
    /// 一个键盘/鼠标字形和它的序号，条目边界就清楚了。
    /// </summary>
    private FrameworkElement DeviceRow(string key, string name, string detail, bool isMouse, bool on, int index)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var check = new CheckBox
        {
            IsChecked = on,
            Tag = key,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = key.Length == 0
                ? Loc.T("勾上＝不挑设备，所有键盘都算")
                : Loc.T("打勾＝这台键盘按的触发键才算数"),
        };
        check.Click += OnDeviceChecked;
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        /* 设备类型图标：用系统的 Fluent 图标字体，比 "⌨" 这种散落的 Unicode 符号
         * 统一得多（那个符号在雅黑里是彩色的、大小还跟字号不对齐）。 */
        line.Children.Add(new TextBlock
        {
            Text = isMouse ? "\uE962" : "\uE765",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        /* 序号：一台一台数得清。第一行「所有键盘」不带序号（index==0）。 */
        if (index > 0)
        {
            line.Children.Add(new TextBlock
            {
                Text = index.ToString() + ".",
                Opacity = 0.5,
                FontSize = 11,
                MinWidth = 16,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        line.Children.Add(new TextBlock
        {
            Text = name,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        if (detail.Length > 0 && !string.Equals(detail, name, StringComparison.Ordinal))
        {
            line.Children.Add(new TextBlock
            {
                Text = detail,
                Margin = new Thickness(6, 0, 0, 0),
                Opacity = 0.55,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 120,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
        }
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);
        return grid;
    }

    private void OnDeviceButtonClick(object sender, RoutedEventArgs e)
    {
        // m01050：这颗胶囊要做成开合开关 —— 点一下展开，再点一下收起（以前只会打开）。
        if (DevicePopup.IsOpen)
        {
            DevicePopup.IsOpen = false;
            return;
        }

        FillDeviceScope(_cfg?.Device ?? "");
        DevicePopup.IsOpen = true;
    }

    private void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        FillDeviceScope(_cfg?.Device ?? "");
    }

    private void OnDeviceChecked(object sender, RoutedEventArgs e)
    {
        if (_sync || _cfg == null) return;
        if (sender is not CheckBox box) return;

        string key = (box.Tag as string) ?? "";
        bool on = box.IsChecked == true;

        /* 三态与级联全交给 AppConfig.ApplyDeviceToggle 这个纯函数（那边有自检）：
         *
         *   勾「所有键盘」        → 留空，下面每台都显示打勾（all 由配置推出来）
         *   取消「所有键盘」      → "none"，下面勾一起消失
         *   在"所有键盘"下取消一台 → 其余那些写成显式清单（画面上它就变空的）
         *   把最后一台也取消      → "none"，不会打回"所有键盘"
         *
         * 设备名单要现场读一次：从"所有键盘"里摘掉一台时需要知道"其余那些"是谁。 */
        var present = InputDevices.All().Where(d => d.HasKeyboard).Select(d => d.Key).ToList();
        _cfg.Device = AppConfig.ApplyDeviceToggle(_cfg.Device, key, on, present);
        FillDeviceScope(_cfg.Device);
        ScheduleCommit();
    }

    /// <summary>设备标识压成能并排对比的短写法：VID_1532&amp;PID_00A4 → 1532:00A4。</summary>
    private static string CompactId(string key)
    {
        string k = InputDevices.Normalize(key);
        if (k.StartsWith(InputDevices.MousePrefix, StringComparison.Ordinal)) k = k[InputDevices.MousePrefix.Length..];
        int vid = k.IndexOf("VID_", StringComparison.Ordinal);
        int pid = k.IndexOf("PID_", StringComparison.Ordinal);
        if (vid >= 0 && pid >= 0 && k.Length >= pid + 8 && k.Length >= vid + 8)
        {
            return k.Substring(vid + 4, 4) + ":" + k.Substring(pid + 4, 4);
        }
        return k.Length <= 10 ? k : k[..10];
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
            case "AeroSnap": _cfg.AeroSnap = on; break;
            case "AutoFocus": _cfg.AutoFocus = on; break;
            case "SwallowTrigger": _cfg.SwallowTrigger = on; break;
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
            case "TriggerHoldMs": _cfg.TriggerHoldMs = Math.Clamp(n, 0, 5000); break;
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
            case "ProcessBlacklist":
                _cfg.ProcessBlacklist = text;
                RefreshProcessList();
                break;
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

    /// <summary>清单里那条「删除」：把它从程序例外里拿掉，窗口恢复接管。</summary>
    private void OnRemoveProcess(object sender, RoutedEventArgs e)
    {
        if (_host == null || _cfg == null) return;
        if (sender is not FrameworkElement el || el.Tag is not string value || value.Length == 0) return;

        var lines = new System.Collections.Generic.List<string>(AppRules.Rows(_cfg.ProcessBlacklist));
        if (lines.RemoveAll(s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) == 0) return;
        _cfg.ProcessBlacklist = string.Join("\n", lines);
        ProcessBlacklistBox.Text = _cfg.ProcessBlacklist;   // TextChanged 会再写一次 _cfg 并安排落盘
        RefreshProcessList();
        CommitNow();
    }

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
        _cfg.MoveRate = AppConfig.DefaultMoveRate;
        _cfg.ResizeRate = AppConfig.DefaultResizeRate;
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
        // 「程序例外」搬到右栏首屏之后，左栏最底下只剩「窗口例外」—— 滚到底拍它。
        LeftScroll.ScrollToEnd();
    }
}

/// <summary>
/// 右栏「程序例外」清单的一行：<see cref="Name"/> 是程序名（可能带 * 通配），
/// <see cref="StateText"/> 是给人看的一句状态，<see cref="Value"/> 是删回文本时用的原始那一行。
/// 行模型跟「平滑滚动」右栏那份一样，两页的清单长得一样。
/// </summary>
public sealed record HoldDragExceptionRow(string Name, string StateText, string Value);
