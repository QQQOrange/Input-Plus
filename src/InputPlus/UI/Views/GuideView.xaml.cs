using System.Windows;
using System.Windows.Controls;
using InputPlus.Core.Actions;
using InputPlus.Core.Input;
using InputPlus.Core.Settings;

namespace InputPlus.UI.Views;

public partial class GuideView : UserControl
{
    private AppHost? _host;

    public GuideView()
    {
        InitializeComponent();

        /* 「最常用的 8 个组合」要跟着真实改键走，所以三处都要刷新：
         *   · 页面被切回来（Loaded）—— MainWindow 缓存页面，不重建；
         *   · 窗口从托盘再显示出来（IsVisibleChanged）—— 那时 Loaded 不会再发；
         *   · 设置变了（SettingsChanged）—— 用户在另一页改完绑定，这一页可能正开着。 */
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    public void Initialize(AppHost host)
    {
        _host = host;
        Subscribe();
        Refresh();
    }

    /// <summary>
    /// 每次变可见都重算一遍。
    ///
    /// 页面是缓存复用的：第一次进来走 <see cref="Initialize"/>，之后切走再切回来
    /// 只会触发 Loaded（MainWindow.ShowPage 也会调一次 <see cref="Refresh"/>）。
    /// 只靠 Initialize 的话，用户在「按键映射」改完键再切回来，看到的还是旧组合。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        Refresh();
    }

    /// <summary>离开可视树时退订，避免宿主长期持有旧页面（隐性泄漏）。</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Subscribe()
    {
        if (_host == null) return;
        _host.SettingsChanged -= OnSettingsChanged;
        _host.SettingsChanged += OnSettingsChanged;
    }

    private void Unsubscribe()
    {
        if (_host == null) return;
        _host.SettingsChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        // 不在前台就别跟着重绘：改一次绑定会连着刷所有页面，白白卡一下
        if (!IsVisible) return;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        var s = _host.Settings;
        RunStateText.Text = _host.Engine.Enabled ? Loc.T("已启用") : Loc.T("已暂停");
        // 提示里的键名按**第 1 层实际配的触发键**显示：用户把它换成 F13 之后，
        // 这儿再写死 CapsLock 就成了"照它按却没反应"。
        // 仍是 CapsLock（出厂值）时走原来那句 —— 那句已经有译文，不必多一条待翻译文案。
        string trigger = TriggerLabelOf(s);
        RunStateHint.Text = !_host.Engine.Enabled
            ? Loc.T("点击标题栏的「恢复」重新启用")
            : trigger == "Caps"
                ? Loc.T("按住 CapsLock 即可使用全部功能")
                : Loc.F("按住 {0} 即可使用全部功能", trigger);
        StartupText.Text = s.Global.StartWithWindows ? Loc.T("已开启") : Loc.T("未开启");

        RefreshQbarHint(trigger);

        RebuildQuickKeys(trigger);
    }

    /// <summary>
    /// 横幅那两枚键帽 + 第 2 步里的"打开 Qbar"跟着真实绑定走。
    ///
    /// 以前这一段是写死的 `Caps + Q` 和"按 Caps+Q：…"：用户把 Q 改绑成
    /// 「用谷歌搜索打开 Qbar」、或者把触发键换成 F13 之后，这一页说的和按下键
    /// 会发生的事对不上 —— 快速上手是第一次打开就看的页面，骗人最伤。
    ///
    /// 找法两层：先找"就是 Qbar"的绑定（action = qbar），再退一步找 `qbar:xxx`
    /// （那是"带某个搜索引擎的 Qbar"，用户改成谷歌搜索就落在这里）。
    /// 一颗都没绑：收掉第二枚键帽、文案也去掉 Qbar 那半句 —— 留着等于告诉用户
    /// "按这个键有用"，而那个键根本没绑。
    /// </summary>
    private void RefreshQbarHint(string trigger)
    {
        if (_host == null) return;

        HeroTriggerKeycap.Text = trigger;

        var layer = _host.Settings.LayerKeys(1);
        string? qbarKey = QuickKeyHints.FindKeycap(
                              layer, a => string.Equals(a, "qbar", StringComparison.OrdinalIgnoreCase))
                          ?? QuickKeyHints.FindKeycap(
                              layer, a => a.StartsWith("qbar:", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(qbarKey))
        {
            HeroQbarRow.Visibility = Visibility.Collapsed;
            HeroSubtitle.Text = Loc.T("按住触发键就是功能键：字母键移动 / 选择光标，数字键切换窗口。");
            Step2Text.Text = Loc.T("输入程序名直接回车启动，输入关键词回车搜索，输入 =1+2*3 直接算结果。");
            return;
        }

        HeroQbarRow.Visibility = Visibility.Visible;
        HeroQbarKeycap.Text = qbarKey;
        HeroSubtitle.Text = Loc.F(
            "按住触发键就是功能键：字母键移动 / 选择光标，数字键切换窗口，{0} 打开搜索面板。", qbarKey);

        /* 键帽上如果自带修饰键（Alt+Q 这种），就别再拼一个触发键前缀，
         * 否则会读成 "Caps+Alt+Q" —— 那是两个组合键叠在一起，按不出来。 */
        string combo = qbarKey!.Contains('+') ? qbarKey : trigger + "+" + qbarKey;
        Step2Text.Text = Loc.F(
            "按 {0}：输入程序名直接回车启动，输入关键词回车搜索，输入 =1+2*3 直接算结果。", combo);
    }

    /// <summary>
    /// 重建「最常用的 8 个组合」。
    ///
    /// 以前这里是一张写死的表（Q = 打开 Qbar、C = 复制到剪贴板 1 …）：用户把 Q 改绑成
    /// 「用谷歌搜索打开 Qbar」之后，这一块还写着「打开 Qbar」——页面上说的和按下键真正
    /// 发生的事情对不上，等于骗人。
    ///
    /// 现在改成数据驱动，规则全部在 <see cref="QuickKeyHints.Pick"/> 里（纯函数、可自检）：
    ///   · 数据源是**第 1 层（主层）的真实绑定**，不是文案；
    ///   · 名字取动作目录标题，和「按键映射」页同一个叫法；
    ///   · 用户改过的、真按过的排前面，而不是把两百条绑定按配置顺序取前八条。
    /// </summary>
    private void RebuildQuickKeys(string trigger)
    {
        if (_host == null) return;

        var hints = QuickKeyHints.Pick(
            _host.Settings.LayerKeys(1),
            _host.Actions.Get,
            trigger,
            QuickKeyHints.MaxCount,
            UsageCounts(),
            DefaultTableOf(_host.Settings));

        QuickKeys.ItemsSource = hints;

        // 一条都没有时别留一张空白卡片：那看起来像页面坏了
        QuickKeyEmpty.Visibility = hints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 触发键在键帽上的写法。
    ///
    /// 组合键在配置里统一带 <c>caps_</c> 前缀（老格式），所以真正那颗触发键只能从
    /// <see cref="AppSettings.Triggers"/> 里取 —— 用户把触发键换成 F13，这里就该是 F13。
    /// 没有触发键（配置刚被清空）时退回 Caps：那是出厂方案的名字。
    /// </summary>
    internal static string TriggerLabelOf(AppSettings settings)
    {
        var trigger = settings.Triggers?.FirstOrDefault(t => t.LayerId == 1 && t.Enabled)
                      ?? settings.Triggers?.FirstOrDefault(t => t.LayerId == 1);

        string key = (trigger?.Key ?? "").Trim();
        if (key.Length == 0) return "Caps";

        // 键帽上一直写 Caps 这个短名：写全称 CapsLock 会比旁边那颗键宽出一截
        return string.Equals(key, "capslock", StringComparison.OrdinalIgnoreCase)
            ? "Caps"
            : KeyName.Display(key);
    }

    /// <summary>动作被真正执行过的次数（"最常用"的第一手证据；没有统计就是空表）。</summary>
    private static IReadOnlyDictionary<string, int> UsageCounts()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (action, count) in Core.UsageStats.ActionRanking(200))
            map[action] = count;
        return map;
    }

    /// <summary>
    /// 当前按键方案的默认绑定表 —— 用来判断某条绑定"是不是用户改过"。
    ///
    /// 必须跟着方案走：经典方案（capslock_plus）和 Capslox 3.0 的基础层不是同一张表，
    /// 拿错了会把整个基础层都判成"用户改过"，排序就全乱了。
    /// </summary>
    private static IReadOnlyList<KeyBinding> DefaultTableOf(AppSettings settings)
        => string.Equals((settings.Global?.HotkeyScheme ?? "").Trim(), "capslock_plus",
            StringComparison.OrdinalIgnoreCase)
            ? DefaultSettings.ClassicScheme()
            : DefaultSettings.CapsloxScheme();

    /// <summary>
    /// 内容至少铺满可视区域。
    ///
    /// 最大化时整页内容只有几百 DIP 高，ScrollViewer 下方会留下大片空白
    /// （用户的原话是"快速上手页面最大化之后，右边和下边是空的"）。
    /// 右边靠横向铺满解决；下边靠这里：把视口高度同步成内容的最小高度，
    /// 最后那张卡片所在的 * 行就会把剩余空间吃掉。
    ///
    /// 不能写死 MinHeight：窗口可以拉成任意大小，视口高度只有运行时才知道。
    /// </summary>
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double chrome = PageBody.Margin.Top + PageBody.Margin.Bottom;
        PageBody.MinHeight = Math.Max(0, PageScroll.ViewportHeight - chrome);
    }

    private void OnStartSetup(object sender, RoutedEventArgs e)
    {
        _host?.ShowOnboarding();
    }

    private void OnOpenHelp(object sender, RoutedEventArgs e)
    {
        (Window.GetWindow(this) as Windows.MainWindow)?.Navigate("help");
    }
}
