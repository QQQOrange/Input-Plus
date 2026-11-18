using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// 引擎是 HoldDrag 原样搬进来的（<see cref="Coordinator"/> + <see cref="HookEngine"/>），
/// 这一页只做两件事：开关它、把它的参数摆出来。
///
/// 参数不是一对一镜像成 InputPlus 的字段，而是整份 JSON 存在
/// <see cref="Core.Settings.AppSettings.HoldDragJson"/> 里：
///   · 引擎自己那份 <see cref="AppConfig"/> 是唯一真相，页面拿它的副本编辑；
///   · 改完写回 JSON → 引擎重新解析 + OnConfigChanged，**不重装钩子**
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

    private static List<HoldDragPick> TriggerPicks()
    {
        // 常用触发键；KeysUtil 认识任何 "K:0xNN" / 鼠标侧键 "M:1"/"M:2"。
        string[] ids =
        {
            "K:0x0D", "K:0x20", "K:LShift", "K:RShift", "K:LCtrl", "K:RCtrl",
            "K:LAlt", "K:RAlt", "K:0x5B", "K:0x09", "K:0x14", "M:1", "M:2",
        };
        var list = new List<HoldDragPick>();
        foreach (string id in ids) list.Add(new HoldDragPick(id, Loc.T(KeysUtil.DisplayName(id))));
        return list;
    }

    private void BuildChoices()
    {
        /* 下拉项一律用 ComboBoxItem：Content 放显示名，Tag 放配置里的 id。
           不用对象 + DisplayMemberPath —— 这套自定义模板的 ContentPresenter
           只认 SelectionBoxItemTemplate，属性设了也不生效，界面会退化成打印
           对象本身（第一版截图里整排下拉都是 HoldDragPick { Id = ..., Title = ... }）。 */
        Fill(TriggerBox, TriggerPicks());
        Fill(ModeBox, new List<HoldDragPick>
        {
            new("Hold", Loc.T("按住")),
            new("Toggle", Loc.T("切换")),
        });

        var actions = new List<HoldDragPick>();
        foreach (string id in Actions.All) actions.Add(new HoldDragPick(id, Loc.T(Actions.DisplayName(id))));
        Fill(LeftBox, actions);
        Fill(MiddleBox, actions);
        Fill(RightBox, actions);

        var styles = new List<HoldDragPick>();
        foreach (string id in FrameStyles.All) styles.Add(new HoldDragPick(id, Loc.T(FrameStyles.DisplayName(id))));
        Fill(StyleBox, styles);

        var colors = new List<HoldDragPick>();
        foreach (var c in FrameColors.All) colors.Add(new HoldDragPick(c.Id, Loc.T(c.Name)));
        Fill(ColorBox, colors);

        Fill(RefreshModeBox, new List<HoldDragPick>
        {
            new("Auto", Loc.T("自动")),
            new("Custom", Loc.T("自定义")),
        });
    }

    private static void Fill(ComboBox box, List<HoldDragPick> picks)
    {
        box.Items.Clear();
        foreach (var pick in picks) box.Items.Add(new ComboBoxItem { Content = pick.Title, Tag = pick.Id });
    }

    /// <summary>按 id 选中一项（id 不在表里就清空，避免下拉显示成别的值）。</summary>
    private static void Select(ComboBox box, string? id)
    {
        foreach (object item in box.Items)
        {
            if (item is ComboBoxItem row && string.Equals(row.Tag as string, id, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = row;
                return;
            }
        }
        box.SelectedIndex = -1;
    }

    private static string? IdOf(ComboBox? box) => (box?.SelectedItem as ComboBoxItem)?.Tag as string;

    // ---------------- 回填 ----------------

    public void Refresh()
    {
        if (_host == null) return;
        _cfg = _host.HoldDrag.EditableConfig();
        Fill();
    }

    /// <summary>把 <see cref="_cfg"/> 摊到控件上（重新读设置时用 Refresh，出图时用这个）。</summary>
    private void Fill()
    {
        if (_host == null || _cfg == null) return;

        _sync = true;
        try
        {
            EnabledBox.IsChecked = _host.Settings.HoldDragEnabled;

            Select(TriggerBox, _cfg.TriggerKeyId);
            Select(ModeBox, _cfg.TriggerMode);
            HoldMsBox.Text = Num(_cfg.HoldMs);

            Select(LeftBox, _cfg.ActionLeft);
            Select(RightBox, _cfg.ActionRight);
            Select(MiddleBox, _cfg.ActionMiddle);

            FrameBox.IsChecked = _cfg.ShowActivationFrame;
            Select(StyleBox, _cfg.FrameStyleId);
            Select(ColorBox, _cfg.FrameColorId);
            ThicknessBox.Text = Num(_cfg.FrameThickness);
            RadiusBox.Text = Num(_cfg.FrameRadius);
            OpacityBox.Text = Num(_cfg.FrameOpacity);

            SnapBox.IsChecked = _cfg.SnapToEdges;
            SnapBox2.Text = Num(_cfg.SnapThreshold);
            TilingBox.IsChecked = _cfg.KeyboardTiling;
            ShakeBox.IsChecked = _cfg.ShakeMinimizeOthers;
            HudBox.IsChecked = _cfg.ShowDragHud;
            HudPosBox.IsChecked = _cfg.DragHudShowPosition;
            HintBox.IsChecked = _cfg.ShowTriggerHint;

            DblMaxBox.IsChecked = _cfg.DoubleClickLeftMaximize;
            DblCloseBox.IsChecked = _cfg.DoubleClickRightZone;
            RestoreBox.IsChecked = _cfg.RestoreMaximizedWhenMoved;
            TopBox.IsChecked = _cfg.BringToFront;
            FullBox.IsChecked = _cfg.AllowFullscreenWindows;
            ExitFullBox.IsChecked = _cfg.ExitFullscreenOnTrigger;
            OwnBox.IsChecked = _cfg.AllowDragOwnWindows;
            UndoBox.IsChecked = _cfg.UndoEnabled;

            Select(RefreshModeBox, _cfg.DragRefreshMode);
            RefreshHzBox.Text = Num(_cfg.DragRefreshHz);
            DragThresholdBox.Text = Num(_cfg.DragThreshold);
            DragOutBox.Text = Num(_cfg.DragOutThresholdPx);
            CornerBox.Text = Num(_cfg.CornerPercent);

            BlacklistBox.Text = _cfg.Blacklist ?? "";
            RulesBox.Text = _cfg.AppRules ?? "";
        }
        finally { _sync = false; }

        UpdateStatus();
    }

    private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    // ---------------- 写回 ----------------

    private void ScheduleCommit()
    {
        if (_sync || _host == null || _cfg == null) return;
        _commitTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        if (_commitTimer.Tag == null)
        {
            _commitTimer.Tag = "wired";
            _commitTimer.Tick += (_, _) =>
            {
                _commitTimer!.Stop();
                CommitNow();
            };
        }
        _commitTimer.Stop();
        _commitTimer.Start();
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
            CoreLog.Swallow(ex, "应用按住拖拽参数");
        }
        UpdateStatus();
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_sync || _host == null) return;
        _host.Settings.HoldDragEnabled = EnabledBox.IsChecked == true;
        // 开关要立刻生效（装/卸钩子），不能等防抖。
        _host.SaveSettings();
        UpdateStatus();
    }

    private void OnToggleField(object sender, RoutedEventArgs e)
    {
        if (_sync || _cfg == null) return;
        bool on = (sender as CheckBox)?.IsChecked == true;
        switch ((sender as CheckBox)?.Tag as string)
        {
            case "ShowActivationFrame": _cfg.ShowActivationFrame = on; break;
            case "SnapToEdges": _cfg.SnapToEdges = on; break;
            case "KeyboardTiling": _cfg.KeyboardTiling = on; break;
            case "ShakeMinimizeOthers": _cfg.ShakeMinimizeOthers = on; break;
            case "ShowDragHud": _cfg.ShowDragHud = on; break;
            case "DragHudShowPosition": _cfg.DragHudShowPosition = on; break;
            case "ShowTriggerHint": _cfg.ShowTriggerHint = on; break;
            case "DoubleClickLeftMaximize": _cfg.DoubleClickLeftMaximize = on; break;
            case "DoubleClickRightZone": _cfg.DoubleClickRightZone = on; break;
            case "RestoreMaximizedWhenMoved": _cfg.RestoreMaximizedWhenMoved = on; break;
            case "BringToFront": _cfg.BringToFront = on; break;
            case "AllowFullscreenWindows": _cfg.AllowFullscreenWindows = on; break;
            case "ExitFullscreenOnTrigger": _cfg.ExitFullscreenOnTrigger = on; break;
            case "AllowDragOwnWindows": _cfg.AllowDragOwnWindows = on; break;
            case "UndoEnabled": _cfg.UndoEnabled = on; break;
            default: return;
        }
        ScheduleCommit();
    }

    private void OnComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || _cfg == null) return;
        if (sender is not ComboBox box) return;
        string? value = IdOf(box);
        if (string.IsNullOrEmpty(value)) return;

        switch (box.Tag as string)
        {
            case "TriggerKeyId": _cfg.TriggerKeyId = value; break;
            case "TriggerMode": _cfg.TriggerMode = value; break;
            case "ActionLeft": _cfg.ActionLeft = value; break;
            case "ActionRight": _cfg.ActionRight = value; break;
            case "ActionMiddle": _cfg.ActionMiddle = value; break;
            case "FrameStyleId": _cfg.FrameStyleId = value; break;
            case "FrameColorId": _cfg.FrameColorId = value; break;
            case "DragRefreshMode": _cfg.DragRefreshMode = value; break;
            default: return;
        }
        ScheduleCommit();
    }

    /// <summary>数字框共用：Tag 就是 AppConfig 上的字段名，能解析才写。</summary>
    private void OnNumberChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync || _cfg == null) return;
        var box = sender as TextBox;
        if (!int.TryParse(box?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return;

        switch (box!.Tag as string)
        {
            case "HoldMs": _cfg.HoldMs = v; break;
            case "FrameThickness": _cfg.FrameThickness = v; break;
            case "FrameRadius": _cfg.FrameRadius = v; break;
            case "FrameOpacity": _cfg.FrameOpacity = v; break;
            case "SnapThreshold": _cfg.SnapThreshold = v; break;
            case "DragRefreshHz": _cfg.DragRefreshHz = v; break;
            case "DragThreshold": _cfg.DragThreshold = v; break;
            case "DragOutThresholdPx": _cfg.DragOutThresholdPx = v; break;
            case "CornerPercent": _cfg.CornerPercent = v; break;
            default: return;
        }
        ScheduleCommit();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync || _cfg == null) return;
        if (ReferenceEquals(sender, BlacklistBox)) _cfg.Blacklist = BlacklistBox.Text ?? "";
        else if (ReferenceEquals(sender, RulesBox)) _cfg.AppRules = RulesBox.Text ?? "";
        else return;
        ScheduleCommit();
    }

    // ---------------- 挑程序：两个入口与手打共用同一条校验 ----------------

    /// <summary>
    /// 把新条目并进「一行一条」的清单：去空白、忽略大小写去重、保持原顺序。
    /// 黑名单与规则框都用它 —— 手打和挑选走同一段逻辑，不会一边去重一边不去重。
    /// </summary>
    internal static string AppendToken(string? existing, string? token)
    {
        string add = (token ?? "").Trim();
        var lines = SplitTokens(existing);
        if (add.Length == 0) return string.Join("\n", lines);
        if (!lines.Any(s => string.Equals(s, add, StringComparison.OrdinalIgnoreCase))) lines.Add(add);
        return string.Join("\n", lines);
    }

    /// <summary>按程序规则：同名（= 前面那段）就换行为，否则追加一条。</summary>
    internal static string UpsertRule(string? existing, string? program, string? mode)
    {
        string name = (program ?? "").Trim();
        string m = string.IsNullOrWhiteSpace(mode) ? "Ignore" : mode.Trim();
        var lines = SplitTokens(existing);
        if (name.Length == 0) return string.Join("\n", lines);

        int at = lines.FindIndex(s =>
        {
            int eq = s.IndexOf('=');
            string key = (eq < 0 ? s : s[..eq]).Trim();
            return string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
        });
        string entry = name + "=" + m;
        if (at >= 0) lines[at] = entry;
        else lines.Add(entry);
        return string.Join("\n", lines);
    }

    private static List<string> SplitTokens(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    /// <summary>挑一个程序的名字（不是全路径：黑名单/规则认的是程序名）。取消返回空。</summary>
    private string PickProgramName(bool byWindow)
    {
        if (byWindow)
        {
            var picker = new UI.Windows.WindowPickerWindow();
            if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
            picker.ShowDialog();

            IntPtr h = picker.PickedWindow;
            if (h == IntPtr.Zero) return "";                 // 用户按了 Esc

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
            Title = Loc.T("选择要单独设置的程序"),
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

    private void OnBlacklistPickFile(object sender, RoutedEventArgs e) => AddToBlacklist(PickProgramName(false));

    private void OnBlacklistPickWindow(object sender, RoutedEventArgs e) => AddToBlacklist(PickProgramName(true));

    private void AddToBlacklist(string name)
    {
        if (_sync || _cfg == null) return;
        if (string.IsNullOrWhiteSpace(name))
        {
            _host?.Notify(Loc.T("没挑到程序：可以用「选择程序…」直接选 exe"));
            return;
        }

        string merged = AppendToken(BlacklistBox.Text, name);
        if (string.Equals(merged, BlacklistBox.Text ?? "", StringComparison.Ordinal))
        {
            _host?.Notify(Loc.F("{0} 已经在黑名单里了", name));
            return;
        }
        BlacklistBox.Text = merged;     // TextChanged 会写进 _cfg 并安排一次落盘
        _host?.Notify(Loc.F("已加入黑名单：{0}", name));
    }

    private void OnRulePickFile(object sender, RoutedEventArgs e) => AddToRules(PickProgramName(false));

    private void OnRulePickWindow(object sender, RoutedEventArgs e) => AddToRules(PickProgramName(true));

    private void AddToRules(string name)
    {
        if (_sync || _cfg == null) return;
        if (string.IsNullOrWhiteSpace(name))
        {
            _host?.Notify(Loc.T("没挑到程序：可以用「选择程序…」直接选 exe"));
            return;
        }

        string mode = (RuleModeBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Ignore";
        RulesBox.Text = UpsertRule(RulesBox.Text, name, mode);
        _host?.Notify(Loc.F("已加入规则：{0}", name + "=" + mode));
    }

    /// <summary>
    /// 出图用：把左栏滚到最下面，好拍「黑名单」与「按程序规则」那两块
    /// （它们在这页最底下，默认视口里看不见）。
    /// </summary>
    internal void ScrollListsForRender()
    {
        FindScroll(this)?.ScrollToEnd();
    }

    private static System.Windows.Controls.ScrollViewer? FindScroll(System.Windows.DependencyObject? node)
    {
        if (node == null) return null;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is System.Windows.Controls.ScrollViewer sv) return sv;
            var deep = FindScroll(child);
            if (deep != null) return deep;
        }
        return null;
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _cfg = new AppConfig();
        Refresh();
        CommitNow();
        _host.Notify(Loc.T("按住拖拽已恢复默认参数（开关状态没变）"));
    }

    // ---------------- 状态行 ----------------

    private void UpdateStatus()
    {
        if (_host == null || StatusText == null) return;
        var svc = _host.HoldDrag;

        if (!_host.Settings.HoldDragEnabled)
        {
            StatusText.Text = Loc.T("已关闭：钩子没有装上，鼠标和键盘的行为一点都没变。");
            return;
        }
        if (!svc.Installed)
        {
            StatusText.Text = string.IsNullOrEmpty(svc.LastError)
                ? Loc.T("已启用，但钩子还没装上。")
                : Loc.F("已启用，但钩子装不上：{0}", svc.LastError);
            return;
        }

        string trigger = Loc.T(KeysUtil.DisplayName(_cfg?.TriggerKeyId ?? ""));
        string mode = string.Equals(_cfg?.TriggerMode, "Toggle", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("按一下进入 / 再按一下退出")
            : Loc.T("按住才生效");
        StatusText.Text = Loc.F("运行中：{0}（{1}）已接管。", trigger, mode);
        if (svc.Rescues > 0)
            StatusText.Text += " " + Loc.F("钩子被系统摘掉后已自动救回 {0} 次。", svc.Rescues);
    }

    /// <summary>出图用：把开关与几个参数摆成好看的样子（只改内存，不落盘）。</summary>
    internal void SetSampleForRender()
    {
        if (_host == null) return;
        _cfg = _host.HoldDrag.EditableConfig();

        /* 先掐住回写：动这些控件会触发各自的 Changed，那一步会真写设置。 */
        _sync = true;
        try
        {
            /* 总开关保持关：右栏状态行是按真实设置写的，勾上会出现
               「开关是开着的、状态却说已关闭」的矛盾截图。出厂本来就是关，
               截图也照实拍关的样子。 */
            EnabledBox.IsChecked = false;
            _cfg.TriggerKeyId = "K:0x0D";
            _cfg.HoldMs = 500;
            _cfg.ActionLeft = "Move";
            _cfg.ActionRight = "Resize";
            _cfg.ActionMiddle = "Maximize";
            _cfg.FrameStyleId = "Glow";
            _cfg.FrameColorId = "Accent";
            _cfg.Blacklist = "game.exe\nobs64.exe";
        }
        finally { _sync = false; }

        Fill();
        UpdateLayout();
    }
}
/// <summary>
/// 下拉框的一项：Id 是配置里存的值，Title 是给人看的中文名。
///
/// 它**不直接喂给 ComboBox**（喂对象会踩两个坑：DisplayMemberPath 在这套自定义模板里
/// 不生效，且裁剪下发版里私有类型走 TypeDescriptor 也可能读不到属性，界面会退化成
/// 打印 HoldDragPick { Id = ..., Title = ... }）。<see cref="Fill"/> 会把它翻译成
/// ComboBoxItem（Content=Title、Tag=Id），界面永远只看到字符串。
/// </summary>
public sealed record HoldDragPick(string Id, string Title);