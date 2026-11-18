using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using InputPlus.Core.Input;

namespace InputPlus.UI.Views;

/// <summary>
/// 帮助页：使用指南 + 快捷键速查。
///
/// 速查表完全由**当前配置**生成（遍历所有层的绑定），
/// 所以用户改完按键映射后，这里立刻跟着变 —— 不会出现
/// 「帮助里写着按 Q 打开面板，实际你已经改绑到 W」 这种尴尬。
/// </summary>
public partial class HelpView : UserControl
{
    private AppHost? _host;

    /// <summary>当前速查表内容：复制按钮用的就是这一份（跟着筛选走）。</summary>
    private List<HelpGroup> _groups = new();

    /// <summary>顶部提示的默认文案；有筛选时会被替换成「命中 N 条」。</summary>
    private string _defaultHint = "";

    /// <summary>「⑪ 鼠标手势」那一节的标题元素，出图时要滚到它。</summary>
    private FrameworkElement? _gestureGuideHeader;

    /// <summary>「全部功能一览」还没生成（或配置变了要重建）。</summary>
    private bool _actionCatalogDirty = true;

    /// <summary>「⑫ 键盘设备」与「⑰ 命令行」两节的标题元素，出图时要滚到它们。</summary>
    private FrameworkElement? _deviceGuideHeader;
    private FrameworkElement? _cliGuideHeader;

    public HelpView()
    {
        InitializeComponent();
        /* 全部功能一览动辄近千条（每个可发送的键、每个搜索引擎、每个直达链接
         * 都是一条动作），默认折叠时就别生成它们 —— 展开再建，省下每次进页的卡顿。 */
        ActionCatalogExpander.Expanded += (_, _) =>
        {
            if (_actionCatalogDirty) BuildActionCatalog();
        };
        // 离开可视树时退订，避免宿主长期持有旧页面（隐性泄漏）
        Unloaded += OnUnloaded;
    }

    public void Initialize(AppHost host)
    {
        _host = host;

        /* 订阅配置变更：在按键映射页改完绑定，这里立刻跟着更新。
         *
         * 之前只在"切到帮助页"时刷新 —— 若帮助页一直开着（分屏），
         * 另一页改了配置，速查表还是旧内容。 */
        _host.SettingsChanged -= OnSettingsChanged;
        _host.SettingsChanged += OnSettingsChanged;

        Refresh();
    }

    /// <summary>宿主配置变化：重建速查表与指南。</summary>
    private void OnSettingsChanged()
    {
        if (_host == null || !IsVisible) return;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host != null) _host.SettingsChanged -= OnSettingsChanged;
    }

    public void Refresh()
    {
        if (_host == null) return;
        UpdateTriggerHint();
        BuildGuide();
        RefreshActionCatalog();
        Build(FilterBox.Text?.Trim() ?? "");
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Build(FilterBox.Text?.Trim() ?? "");

    /// <summary>顶部提示改为**当前实际使用的触发键**，不再写死 CapsLock。</summary>
    private void UpdateTriggerHint()
    {
        if (_host == null) return;

        // 优先用第 1 层的触发键（最常用的那个）作为代表
        var primary = _host.Settings.Triggers
            .Where(t => t.Enabled)
            .OrderBy(t => t.LayerId)
            .FirstOrDefault();

        if (primary == null)
        {
            TriggerKeyText.Text = Loc.T("（未设置）");
            _defaultHint = Loc.T("还没有触发键 —— 到「触发键与层」页给某一层分配一个。");
            HintText.Text = _defaultHint;
            return;
        }

        TriggerKeyText.Text = DescribeKey(primary.Key);
        _defaultHint = Loc.T("再看下面的按键 —— 未绑定的键可以到「按键映射」里自己加。")
                     + Loc.T("每一层可以用不同的触发键，速查表按层分列。");
        HintText.Text = _defaultHint;
    }

    private static string DescribeKey(string key)
    {
        // 组合键 ID → 可读文字
        var parsed = KeyName.ParseCombo(key);
        if (parsed != null)
        {
            var (mods, k) = parsed.Value;
            if (mods == KeyModifiers.None) return KeyName.Display(k);
            var parts = new List<string>();
            if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
            if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (mods.HasFlag(KeyModifiers.Win)) parts.Add("Win");
            return string.Join("+", parts) + "+" + KeyName.Display(k);
        }
        return KeyName.Display(key);
    }

    /// <summary>
    /// 生成「使用指南」。
    ///
    /// 内容跟随实际配置（层数、子层、宏数量），而不是写死一段说明 ——
    /// 用户看到的是「我这里现在是怎么配的」。
    /// </summary>
    private void BuildGuide()
    {
        if (_host == null) return;
        GuidePanel.Children.Clear();

        var s = _host.Settings;

        AddSection(Loc.T("① 触发键与图层"), new[]
        {
            Loc.T("按住触发键（默认 CapsLock）就是「功能键」，此时再按其他键会执行你在「按键映射」里配的功能。"),
            Loc.T("一共 10 个图层，每层可以有「自己的触发键」（CapsLock、F13、右 Alt…），互不干扰。"),
            Loc.T("触发键只能是单个按键 —— 组合键留给层内绑定用，避免「同样按 Ctrl+K，到底算触发还是算绑定」的歧义。"),
            Loc.F("当前配置：{0} 个启用的触发键。", s.Triggers.Count(t => t.Enabled)),
        });

        AddSection(Loc.T("② 子层（层内修饰键）"), new[]
        {
            Loc.T("进入某一层后，再按住修饰键（Ctrl / Shift / Alt / Win 及其组合），可以用到「另一套独立绑定」。"),
            Loc.T("每层最多 10 个子层，在「按键映射」页点「设置子层…」自由勾选。"),
            Loc.T("例：基础层按 Q 打开面板，按住 Ctrl 再按 Q 则可以是「删除前一个单词」。"),
            Loc.T("每个子层都有独立的按键绑定，互不覆盖。"),
        });

        AddSection(Loc.T("③ 按键映射"), new[]
        {
            Loc.T("左半是完整 108 键键盘：点任意键即可编辑它的功能，修改立刻生效。"),
            Loc.T("深蓝色键帽 = 已绑定功能；浅蓝色 = 未绑定。键帽上方的小字是「功能分类」，中间是功能名，下方是键名。"),
            Loc.T("右侧编辑区只有一个搜索框：「编辑绑定」标题旁带放大镜的那个。打字时先列当前层已绑定的键（点中跳过去），再列全部功能（点中直接应用到当前键）。"),
            Loc.T("「分类」决定功能下拉里显示哪一类；「窗口绑定」「直达链接」「输入文本」这三类会换成专用的编辑界面。"),
        });

        AddSection(Loc.T("④ 窗口绑定"), new[]
        {
            Loc.T("分类选「窗口绑定」后，有两个按钮：点选窗口（准星点一下已开的窗口）或选择程序文件。"),
            Loc.T("绑定后按该键：程序没开就自动启动，已开着就切过去，已经在前台再按一次会最小化。"),
            Loc.T("exe 名不一定是软件名（idea64.exe 是 IDEA），可以在「显示名称」里改成你认得的名字。"),
        });

        AddSection(Loc.T("⑤ 直达链接"), new[]
        {
            Loc.T("这里有两种用法，别混：一种是「先填好、按键就开」，一种是「选中什么就开什么」。"),
            Loc.T("【先填好】分类选「直达链接」，再选类型：直达网址用默认浏览器打开，直达文件夹在资源管理器里打开，直达文件用系统默认程序打开（和双击一样）。"),
            Loc.T("网址写成 github.com 也行，会自动补 https://；填完会自动抓网页标题当备注。文件夹和文件都能点「浏览…」直接挑。"),
            Loc.T("路径支持 %USERPROFILE%\\Desktop、~\\Downloads、shell:Downloads 这些写法；备注会显示在键帽上，不写备注时显示域名 / 文件夹名 / 文件名。"),
            Loc.T("【选中什么就开什么】分类选「智能直达」（这一类里只有它一个动作）：在任意程序里选中一段文字，按这个键 —— 是网址就开浏览器、是文件夹就开资源管理器、是文件就用默认程序打开。没有选中文字时，它会看你剪贴板里最新的一条：刚复制过网址、文件或文件夹，按一下就打开；两样都不是链接就什么都不做。不用预先配置。"),
            Loc.T("智能识别支持带协议的网址、裸域名、盘符路径、UNC 网络路径、环境变量路径，以及 file:/// 开头的地址；选中普通句子时会明确告诉你「这不是链接」，不会乱开浏览器。"),
        });

        AddSection(Loc.T("⑤b 自定义按键"), new[]
        {
            Loc.T("分类选「自定义按键」：设一个要发送的键 + 一个应用。按这个键时先看应用在不在运行 —— 在运行就直接把键发过去，没运行就先启动它、等窗口出来再发。"),
            Loc.T("典型用法：把 F13 绑给微信，按一下就切到微信并按 F13；或者把某个键绑给常驻的笔记软件，按下去直接在里面触发它的快捷键。"),
            Loc.T("「以管理员身份启动」只在需要拉起这个程序时生效，会弹一次 UAC；程序本来就在跑时不受影响。"),
            Loc.T("「把应用主窗口切到前台」默认关：不动它的窗口，按键直接交给系统（应用把它注册成全局热键时后台也能收到）；开了会先切前台再发键，但会抢走焦点。"),
            Loc.T("「启动后等多久再发键」默认 900 毫秒 —— 窗口出现不代表能收键盘，大型软件要调大。"),
            Loc.T("备注会显示在键帽上；不填时显示绑定的应用名。"),
        });

        AddSection(Loc.T("⑥ 直接搜索"), new[]
        {
            Loc.T("分类选「直接搜索」，再挑一个搜索引擎（百度、知乎、B站、Google…）。"),
            Loc.T("用法：在任意程序里选中一段文字，按这个键 —— 直接用该引擎在默认浏览器里搜索。"),
            Loc.T("不经过 Qbar 面板，是「看到就查」的最快路径。没选中文字时会提示先选中。"),
            Loc.T("它和「Qbar 搜索」的区别：Qbar 搜索是打开面板再输入；直接搜索是拿选区直接搜。"),
        });

        AddSection(Loc.T("⑦ 宏"), new[]
        {
            Loc.T("「宏」页可以录制一串按键操作，然后把整个宏绑到一个键上。"),
            Loc.T("录制中的按键不会被拦截，录完在列表里可以调整每一步的延迟。"),
            Loc.T("绑定时分类选「宏」，再从功能下拉里选具体那个宏。"),
            Loc.T("录不到按键时：标题下会说明是「钩子没工作」还是「还没敲键盘」。前者到「测试」页看钩子状态，或运行 InputPlus.exe --diag。"),
            Loc.T("远程桌面（向日葵 / ToDesk / RDP）也能录：这类按键是注入的，钩子常常收不到，录制会同时读 Raw Input 那一路，日志里写成「Raw Input 补录」。"),
            Loc.T("「固定延迟」勾上后，录出来的每个键之间都用你填的毫秒数（默认 120），回放节奏均匀；不勾就照你真实按的间隔录。"),
            Loc.T("触发键可以设成 Tab 等任意键：长按进入对应层期间，这个键的按下与自动重复都会被吞掉，只有短按松手才补发一次原功能。"),
            Loc.F("当前配置：{0} 个宏。", s.Macros.Count),
        });

        AddSection(Loc.T("⑧ 剪贴板与同步"), new[]
        {
            Loc.T("内置 10 组独立剪贴板（Caps+C / Caps+V 那一套），与系统剪贴板分开。"),
            Loc.T("「剪贴板」页里每条记录都能一键「复制」到系统剪贴板（不是往本程序里粘贴），历史记录双击也能复制。"),
            Loc.T("所有用户数据都在软件旁边的「InputPlus Data」文件夹里（settings.json 是配置，keys 是密钥，logs 是日志），备份在「InputPlus Back」里 —— 拷走这两个文件夹就带走了全部数据和历史。"),
            Loc.T("加密分两层，别混：设了「备份密码」之后 settings.json 整份变成密文（密钥由密码派生，PBKDF2 60 万次迭代）；没设密码时它是明文。日志这类文件则始终用本机密钥加密（AES-256-GCM，密钥由 Windows DPAPI 绑当前用户），换用户或换机器都读不出来。"),
            Loc.T("「同步与备份」支持 WebDAV：可以把整套配置同步到网盘，也能导入导出。导出默认加密、含全部字段；关掉加密则是明文，会剔除密码类字段。"),
        });

        AddSection(Loc.T("⑧b 别名替换"), new[]
        {
            Loc.T("在「别名替换」页配一条：别名 + 展开内容。之后在任意程序里打这个别名再按空格，就会自动替换成那段内容。"),
            Loc.T("触发词必须以 ; / . 这类符号开头（不能是字母数字），而且要落在词首 —— 这样正常打字不会误触；区分大小写。"),
            Loc.T("可以限定「只在某些程序里展开」：直接填进程名，或点「选择程序…」从文件管理器挑，或点「点选窗口」用十字准星点目标窗口（自动取程序名）；重复的会自动去掉。"),
            Loc.T("也可以随时用列表里的勾选框停用；点列表任意一行就能载回来修改。不知道从哪下手就点「填入示例」，表单会填好一条能直接用的范例。"),
            Loc.T("不确定为什么不展开？用下面的「试算」框打一遍，它会直接说是被程序范围挡住、被停用，还是触发词不合法。"),
            Loc.T("同时配了 ;m 和 ;mail 时，命中会先等 120ms 看你会不会接着打 —— 所以长触发词也有机会生效。"),
        });

        AddSection(Loc.T("⑨ Qbar 快捷启动"), new[]
        {
            Loc.T("按住触发键再按 Q（默认 Caps+Q）打开 Qbar：一个输入框同时管搜索、启动程序、算数、打开网址。也可以在「触发键与层」里把某个触发键的双击动作设成「打开 Qbar」，双击就能开。"),
            Loc.T("输入网址关键字再按空格：地址会整条填进输入框（例如 bili → https://www.bilibili.com），回车直接在浏览器打开 —— 不会再变成一个小标签。"),
            Loc.T("输入搜索关键字再按空格（例如 bd）：会变成一个引擎标签，接着打关键词、回车就是搜索。"),
            Loc.T("输入程序名再按空格（例如 calc）：变成程序标签，回车启动；输入框里直接粘完整网址、算式也能用。"),
            Loc.T("↑ ↓ 选择结果，Tab 补全关键字，Esc 关闭；结果里同时会列出浏览器历史，方便回到刚看过的页面。"),
            Loc.T("不记得关键字？打开面板后直接打几个字母，按空格试一次 —— 认不出来就还在输入框里，不会丢字。"),
        });

        AddSection(Loc.T("⑩ 找东西：四个搜索框"), new[]
        {
            Loc.T("窗口右上角「搜索」：跨全应用找功能、宏、网址，回车直接执行或跳过去。"),
            Loc.T("按键映射 → 编辑绑定标题旁（放大镜）：先找当前层已绑定的键，再找全部功能；点一条就跳过去或直接绑上。"),
            Loc.T("本页（快捷键速查）右上：按组合键、功能名、说明筛选；「复制速查表」可以把结果整段复制走。"),
            Loc.T("Qbar 面板页右上：在几百条搜索引擎 / 常用网址 / 启动程序里按关键字、备注过滤。"),
        });

        _gestureGuideHeader = AddSection(Loc.T("⑪ 鼠标手势"), new[]
        {
            Loc.T("在任意程序里按住鼠标右键 / 中键 / 侧键画一笔，松手就执行对应动作 —— 不离开键盘、不切窗口。左边列表里选「全局」改所有程序通用的一套，选某个程序就只改它自己的（同一笔画两边都配了时，那个程序的优先）。"),
            Loc.T("录制：右上「录制新手势」画一笔就成；已有手势想重画，进详情页点「按住鼠标键画一笔（重新录制）」。详情页还能改起始键（右键 / 中键 / 侧键 1 / 侧键 2 / 左键）、触发方式、组合键（例如按住右键再滚一格）、滚轮方向、感应角落，以及要执行的动作。"),
            Loc.T("「笔画」那一行画的就是这一笔本身（按外框缩放进框里，不改形状）。识别比的就是这条曲线：画快画慢、画大画小、落在屏幕哪个位置、起笔偏几度都不影响结果。早期版本只存方向串、没存轨迹的手势，会在启动时按方向串合成一条折线补进轨迹（v5.13 的 v10 迁移），所以这里画出来的都是真实轨迹，界面上也不再出现一串箭头 —— 方向串只是给人看的摘要。列表里同样不写「按形状识别 · N 个点」：有没有轨迹看缩略图就知道。"),
                        Loc.T("识别引擎就是 WGestures 2 那一套（v5.11 起启用，v5.13 起是唯一引擎，没有开关也没有备用通路）：把一笔压成「累计弧长 s + 方向角 θ」的曲线，两条曲线之间算 ∫(Δθ)²·ds 的耦合距离 —— 画快画慢、画大画小、起点偏几像素都不改变结果（实测同一个形状 0.010~0.017、不同形状 0.071 以上，阈值取 0.05）。有轨迹的手势全部走这条通道；连轨迹都没有的（导入到一半、还没存盘的那些）由程序按方向串临时合成一条折线，照样进同一个引擎、同一个阈值。"),
                        Loc.T("兜底与边界：闭环手势（画圈）额外保留一层旧引擎的起点 / 绕向容错 —— WG 引擎按固定起笔比，换个起笔角度就是另一条曲线，而人画圈记不住从哪儿起。同一个形状一般在 0.017 以内、不同形状 0.071 往上，所以别把匹配容差调到 0.05 以上太多，否则像的手势会互相吞并。两条候选的得分咬得太近（差 0.01 以内）时宁可不执行，也不猜一个。"),
                        Loc.T("录的是 Q 就只认 Q，画成 S 不会被硬套。方向串（↖↑↗…）只是给你看的摘要，不参与判定。"),
            Loc.T("最小识别距离（默认 30 像素）：整笔轨迹的外框对角线小于它，就当「手滑」，既不识别也不预览。手抖、想更宽松就调小；老是误触（例如想右键拖拽却被当成手势）就调大。"),
            Loc.T("起始超时（默认 500 毫秒）：按下鼠标后停住不动超过这么久、并且移动范围还没到最小识别距离，就放弃这次手势（屏幕上留一个小圆点提示），这样「按住右键想等一下再拖」不会画出半个手势。按住不动慢慢想事的人可以把它调大。"),
            Loc.T("起手距离（默认 20 像素）：必须离开起点这么远才算「开始画」，用来滤掉按下鼠标那一下的手指抖动。调大 = 要更明确地移动才开始记；调小 = 很小的笔画也能画出来。"),
            Loc.T("换向系数（默认 0.10，也有人叫它「幻象系数」）：判断「拐弯了没有」的灵敏度。新方向要离上一个转折点足够远才算新笔画，阈值 = 起手距离 + 本段已走的长度 × 这个系数 —— 也就是说笔越画越长，要拐弯就得拐得更远。调大 = 更不容易分出笔画（适合画大弧线、手抖的人）；调小 = 更容易分出笔画（适合小折线手势，例如 Z、M）。内部还有一道 120 像素的转折上限，单笔再长也不会被一段抖动切开。"),
                        Loc.T("匹配容差：WG 引擎下这是形状距离阈值（默认 0.05，范围 0.005~0.3），0 = 一模一样、0.2 以上基本算不像；旧版引擎下它是归一化分数（默认 0.26，范围 0.05~0.9）。老认不出来就往大调一点（0.06~0.07 一般够用），老被认成别的手势就往小调。"),
            Loc.T("页面顶部只剩一行总开关（启用手势、手势开关热键、条数统计），其余都收在两块折叠面板里，同一个设置只出现一次（v5.12 起）。「手势外观」管画法：轨迹开关、两侧描边、识别前 / 识别后的颜色、粗细（1~4，默认 1.5）、整条轨迹的透明度，以及手势名称的开关、边框、未执行 / 已执行配色、贴屏幕的高度（默认距底 25%）、执行后是否立刻淡出、超时提示点显不显示。这些都只是画给你看的，不影响识别。轨迹还可以给右键、中键、侧键 1、侧键 2、左键各配一种颜色，没配的那几格色块是淡的，表示跟随上面的「未识别」色。「手势参数」管识别和行为：作用目标（指针下方的应用 / 活动的应用窗口）、最小识别距离、匹配容差、起手距离、换向系数。详情页勾「执行时不显示手势名称」后，松手不再闪那个名字方框。"),
            Loc.T("命中之后名称方框当场跟着轨迹一起收（停留 0 毫秒）：勾了「识别手势立即执行」的，形状一对上就执行、执行那一刻就消失；没勾的，画的过程中方框一直跟着预览，鼠标键一松开、结算执行的同时立刻消失。以前命中后还要挂 1.2 秒，命令都跑完了方框还在，反而像没执行成功。没匹配到任何手势时不一样：灰色轨迹和「未识别」只闪一下（约 0.12 秒）就消失，跟画完的那一下同步，不在屏幕上占位。"),
            Loc.T("动作参数里的「录制按键」：录键盘组合照常按；要绑鼠标键（左 / 右 / 中 / 侧键 1 / 侧键 2）就点窗口里那排按钮，点一下等于按了一下那颗键。捕获窗不装鼠标钩子，所以点「确定绑定」那一下永远不会被录成鼠标左键。"),
            Loc.T("手势作用目标：「指针下方的应用」= 手势画在哪个程序上，动作就发给哪个程序，执行前把它切到前面（默认）；「活动的应用窗口」= 动作只发给当前活动窗口，不切窗口、不抢焦点。"),
            Loc.T("作用目标分四类（v5.12 起）：全局（所有程序通用）、分组（把几个程序归成一组，共用一套手势和触发方式）、应用程序（单个 exe）、特殊目标（任务栏 / 桌面）。判定顺序是 特殊目标 → 应用程序 → 所属分组 → 全局，就近的那层说了算。左边「新建分组…」建组；组里的手势照样按整笔形状比，只是命中范围不同。"),
            Loc.T("每个作用目标上方还有一栏「允许的触发方式」（WGestures 2 的 TriggerList）：61 条 = 5 颗鼠标键 + 8 个边角位置 ×（滑动 / 滑动 + 滚轮 / 滑动 + 5 颗鼠标键）。点一条就在「默认 → 启用 → 禁用」之间循环，右边三个按钮批量改；「默认」就是跟随上一层。某条触发方式在所有目标里都关着时，那颗键完全不参与手势 —— 不吞事件、原样交给系统。"),
            Loc.T("特殊目标只决定「在那个位置上，哪些触发方式允许起手势」：在任务栏上关掉「鼠标右键」，只有任务栏上的右键不再画手势，别处不受影响；要改手势本身仍然去全局或对应应用程序下面。"),
            Loc.T("手势开关热键：随手暂停 / 恢复整套手势识别（例如玩游戏时）。「边缘手势」里的四角悬停（左上切换软件、右上快速设置、左下开始菜单、右下下一个桌面）和上 / 下边缘滚轮（音量）都是手势的一种，可以逐条改动作，动作清空即停用。"),
            Loc.T("嫌进设置太麻烦：右键托盘图标也能一键开关手势（菜单里那行「鼠标手势：已开启 / 已关闭」）。它和本页「启用手势」是同一份配置，从哪边改都立刻生效，页面开着时勾选状态会跟着变。"),
            Loc.T("不想在某程序里触发：左边列表选中那个程序，点它右边的开关 —— 这个程序里任何手势都不触发（包括边缘手势），再点一次恢复。"),
            Loc.T("画了没反应怎么查：① 列表上方「启用手势」是否勾上、这条手势的「启用手势」是否勾上；② 当前选中的是「全局」还是这个程序，手势存在另一侧就会看着像没反应；③ 是否画得太短（最小识别距离）或还没离开起点（起手距离）；④ 认错了就调匹配容差；⑤ 日志里搜「[手势]」，每条都记着最终命中的手势名和偏差（形状距离，越小越像）。"),
            Loc.T("动作五花八门都能绑：窗口（最大化 / 最小化 / 关闭 / 置顶）、多媒体与音量、亮度、剪贴板、直达网址 / 文件夹 / 文件、宏、截图… 全部来自同一个动作表，列表里看到的都能用。截图请用「系统功能 → 截图」（调起系统截图工具），别再绑成「发送 Win+S」——那是打开 Windows 搜索。"),
        });


        AddSection(Loc.T("⑪b 平滑滚动"), new[]
        {
            Loc.T("滚轮原来是一格一跳：一格 120 像素、瞬间到位。v5.14 起多了「平滑滚动」页，把同一格拆成一段缓动曲线 —— 开始快、尾巴长，滚长网页时眼睛跟得上。曲线与 SmoothScroll 用的那条一样（pulseScale 越大尾巴越长），默认值也一致：步长 120 像素、动画 360 毫秒、曲线尾巴 4。"),
            Loc.T("出厂是关的：滚轮手感是整台机器的，装完软件就改掉它并不礼貌。想要就在页面右上打开总开关，状态行会实时写「已接管 N 次滚轮，注入 M 像素」，一眼就能看出钩子有没有在工作；显示「已关闭」或「鼠标钩子没装上」就说明那一路没通。"),
            Loc.T("连续快滚时加速（默认关）：两格间隔小于「加速触发间隔」（默认 70 毫秒）时，每格再多滚一点，最多到「最多加速到几倍」（默认 7 倍）。「反转方向」把上下对调。「触摸板精细滚动交给系统」默认开 —— 触摸板一格会发几十个小 delta，本来就是连续的，再插一层缓动会发黏。"),
            Loc.T("「程序例外」右栏是「选择程序…」和「点选窗口」两个按钮：前者挑 exe，后者直接点屏幕上的那个窗口（认它背后运行的 exe）。挑出来的程序点「禁用」= 它在这整个程序里不平滑、滚轮原样交给系统（游戏、修图这类要精确一格一格滚的），点「恢复」就跟着全局；也可以只给它单独设步长和动画时长。没列出来的程序都跟随左边的全局参数。"),
            Loc.T("平滑滚动和鼠标手势共用同一条鼠标钩子，顺序是 手势 → 平滑滚动 → 原来的滚轮绑定：给某个程序绑了「Caps+LAlt+滚轮」时，那一下仍然归绑定，不会被平滑吃掉；自己注入的滚轮带标记，不会再被自己接管，所以不会越滚越快。"),
        });
        AddSection(Loc.T("⑪c 按住拖拽"), new[]
        {
            Loc.T("v1.1 起，以前单独发的 HoldDrag 长在里面了：按住触发键，左键拖窗口就是移动、右键拖是改大小、中键拖是最大化／还原，松手完成。默认触发键是回车，按住多久才算「按住」由「长按时长」决定（默认 300 毫秒，越短越跟手、也越容易和普通点击抢）。"),
            Loc.T("出厂是关的，因为默认触发键回车会抢回车键 —— 在页面右上打开总开关以后，状态行才变成「运行中：Enter 回车（按住才生效）已接管。」。想把触发键换成空格、Shift 或鼠标侧键都行；「触发方式」选「切换」就是按一下进入、再按一下退出。"),
            Loc.T("外框：按住触发键时给要操作的窗口画一圈，告诉你现在拖的是谁。三套画法（虚线环、细线环、流光边）、十七种颜色或跟随系统强调色，粗细、圆角、不透明度都能调。贴边吸附默认阈值 8 像素，方向键分屏、双击左键最大化、拖动 HUD、Ctrl+Z 撤销都在同一个页面里开关。"),
            Loc.T("和鼠标手势不打架：手势要求「按着起始键画笔画」，按住拖拽要求「按住触发键按鼠标」，两套钩子各认各的事件。拖不动多半是对方窗口以管理员身份运行（提权窗口普通权限的钩子接管不了），或者这个程序在「按程序规则」「黑名单」里被排除掉了。"),
            Loc.T("配置存在设置里的 HoldDragJson 一个字段：跟着加密备份、WebDAV 同步、按项导出一起走，换机器不用重新配。"),
        });
        _deviceGuideHeader = AddSection(Loc.T("⑫ 键盘设备与设备专属方案"), new[]
        {
            Loc.T("「键盘设备」卡片只是清单：系统认到的每一台键盘、鼠标都列在这里。勾选框决定「本软件管不管这台设备」，不改任何按键绑定的内容。"),
            Loc.T("方案分三档：通用设置（对所有设备生效，没被单独覆盖的键都落在这里）、设备方案（只对勾选的设备生效，同名键优先）、独立方案（连通用设置都不继承，没配过的键在这台设备上就没有绑定）。"),
            Loc.T("在「按键映射」页右上角的设备胶囊里切换现在要改哪一份：胶囊上写着「通用设置」或设备名，标题下面一行会再确认一次「现在编辑：通用设置」或「现在只改这台设备：xxx」。"),
            Loc.T("胶囊里的设备清单可以：给每台设备打勾或取消（取消 = 这台设备完全不归本软件管）、加备注（只改显示名，不动识别信息）、恢复系统名称、清空这台设备的「原键替换层」或单独清空某一层。"),
            Loc.T("复合键盘（一个机箱带多媒体键、旋钮）常被系统拆成好几条设备，这里已按物理设备归并；笔记本自带键盘和 USB 键盘是两条，各自配各自的。"),
            Loc.T("鼠标键（中键 / 侧键 / 滚轮）走同一套方案；每层的设置里还有「鼠标键改动是否生效」，关掉后这一层完全不碰你的鼠标键。"),
            Loc.T("设备识别靠 Raw Input。被安全软件拦住、或系统不给读设备信息时，卡片会写「设备识别未启用」，这时所有设备共用同一份设置 —— 功能照常，只是没法按设备分开。"),
            Loc.T("新键盘插上没反应：先看卡片里它在不在（不在就点「刷新」）、有没有打勾；再看设备胶囊是不是停在「通用设置」，停在通用设置时改的是所有设备。"),
            Loc.T("「按应用自动启用层」是另一件事：它按前台程序自动切层，和按设备分方案互不冲突，可以同时用。"),
        });

        AddSection(Loc.T("⑬ 触发键的短按、长按与双击"), new[]
        {
            Loc.T("触发键不是一个开关，而是三种手势：短按、长按、双击。都在「触发键与层」页右侧逐条编辑，每条触发键可以有自己的三种行为。"),
            Loc.T("长按阈值（默认 200 毫秒，可设 50~2000）：按下后超过这么久才进层；在此之前松手就算短按。调小起手更快，调大更不容易误进层。"),
            Loc.T("短按动作是「这个键原本的活」：默认切换大小写锁，也可以改成 Esc 或什么都不做。"),
            Loc.T("「按住触发键时按别的键，立即进层」默认开启。关掉的话每个组合键都要先等满阈值，手感发涩。层内的「子层键」不受这个开关影响，永远立刻生效。"),
            Loc.T("双击动作默认留空（= 关闭双击）：420 毫秒内连按两次触发键就执行你选的动作，典型用法是「打开 Qbar」。用搜索框挑动作，点「应用」生效，点「清除」关掉。"),
            Loc.T("触发键只能是单个按键，组合键留给层内绑定 —— 否则「同样按 Ctrl+K，到底算触发还是算绑定」永远说不清。"),
            Loc.T("每条触发键前面的勾可以临时停用：停用后这个键恢复原本功能，配置留着，下次再勾上即可。"),
        });

        AddSection(Loc.T("⑭ 设置页逐项说明"), new[]
        {
            Loc.T("启动与运行：开机自动启动（写当前用户注册表，不需要管理员）、关闭窗口时最小化到托盘、启动时打开设置窗口、操作后显示轻提示。"),
            Loc.T("同一张卡片里还有：使用统计（「查看明细」看动作排行与最近 7 天，「清空统计」归零）、键盘设备清单、「更新完成后弹提示」、「显示按键反馈」（每次组合键生效时在屏幕角落闪一下功能名）。"),
            Loc.T("「以管理员身份启动」：打开后下次启动过一次 UAC，用来接管任务管理器这类高权限程序；状态行写着当前权限，旁边两个按钮是「立即重启以生效」和「以普通权限重启」。"),
            Loc.T("「启动密码保护」卡的是「谁能打开这个软件」，和备份密码完全是两回事。忘了密码可以删掉数据目录里的 startup.json 解除保护，其他设置不受影响。"),
            Loc.T("按键行为里的「输入文本方式」决定文字怎么打进光标处：自动（长文本走剪贴板粘贴、短英文走真实按键、中文走 Unicode 注入）、真实按键（逐字敲，可能被程序吞字）、Unicode 注入（不经过键盘，密码框打不进）。"),
            Loc.T("同一张卡片里还有：按住组合键时连续触发、鼠标移动速度（1~20）、音量调节幅度、亮度调节幅度。"),
            Loc.T("外观：主题（浅色 / 深色 / 跟随系统）、触发键星标大小、界面语言（往语言包目录扔一个新的 json，它就会出现在下拉里）。"),
            Loc.T("数据与维护：卡片顶部写着当前数据目录的真实路径；下面是导入、打开设置文件、打开日志、重新加载配置、重置全部设置。重置会把按键映射、Qbar 条目、窗口绑定、外观一次性恢复默认且不能撤销 —— 想留后路，先去「同步与备份」点一次「立即备份并上传」。"),
            Loc.T("两张默认收起的小节（「按应用自动启用层」「文本片段」）点标题展开；收起只是不占地方，配置照旧生效。"),
        });

        AddSection(Loc.T("⑮ 备份、恢复与回滚"), new[]
        {
            Loc.T("两个文件夹就是全部：exe 旁边的 InputPlus Data（配置、密钥、日志）和 InputPlus Back（自动打包的 .inputplus）。换电脑、重装系统，拷这两个文件夹就够了。"),
            Loc.T("换 exe 不会动这两个文件夹 —— 升级只替换程序文件，配置和历史备份原地不动。"),
            Loc.T("每次保存都会留一份 settings.json.bak（上一版）和 settings.json.good（最后一个结构完整的版本）。启动时发现主文件坏了会从它们恢复，坏文件另存成 settings.json.broken-时间戳，不会直接覆盖掉。"),
            Loc.T("加密开着但还没解锁时，那一次保存会直接跳过（日志里写「加密已启用但尚未解锁，本次保存已跳过」），绝不会拿空配置盖掉你的数据。"),
            Loc.T("本地备份的保留策略：只保留最近 N 份（默认 10）、清掉 N 天前的（默认 7，0 = 不按天数清），最新那份永远保留。自动备份按间隔打包，配置没变就跳过。"),
            Loc.T("启动时同步 / 退出时同步 / 自动备份是三个独立开关：启动时同步比较云端与本机哪份新，谁新用谁；退出时同步最多等 8 秒。"),
            Loc.T("WebDAV（坚果云等）：填服务器地址、用户名、密码（坚果云用「应用密码」）和备份密码，传上去的是密文。「从云端恢复…」能列出云端所有备份挑一份。"),
            Loc.T("导出配置可以按分组勾选，默认加密（用备份密码）；明文导出会剔除密码类字段。导入是合并式的：只覆盖文件里包含的项，你没导出的部分不会被清掉。"),
            Loc.T("误操作后想回退：先在「从备份恢复…」里挑最近一份；程序本身出问题，同目录下的 InputPlus.exe.bak-时间戳 是替换前的旧版本，换回去即可。"),
        });

        AddSection(Loc.T("⑯ 管理员权限、密码与隐私"), new[]
        {
            Loc.T("默认以普通权限运行：不装驱动、不改系统设置，开机自启也只写当前用户的注册表。"),
            Loc.T("什么时候需要提权：任务管理器、以管理员运行的程序属于更高的完整性级别，Windows 不允许普通权限的进程向它们注入输入 —— 这时「输入文本」「选中文字直接搜索」会失效，热键也可能收不到。"),
            Loc.T("反过来，管理员模式下也无法向普通权限的窗口注入输入。两种权限混用最容易出怪问题：热键整体失灵时，点设置页的「以普通权限重启」。"),
            Loc.T("启动密码只拦启动，存的是 PBKDF2 哈希；它不随备份走，也不进导出文件 —— 换机器要重新设一次。"),
            Loc.T("备份密码用来加密备份文件与本地数据（设了之后 settings.json 就是密文）。不设备份密码，云端备份用不了。"),
            Loc.T("本机密钥（keys/local.key）用 Windows DPAPI 绑当前用户：换用户或换机器解不开，程序不会覆盖它，只会拒绝解密 —— 这时按提示重新设一次密码，或从备份恢复。"),
            Loc.T("网络：除了你自己配的 WebDAV、以及检查更新时访问 GitHub，程序不往任何地方发送数据。日志与配置都不会上传。"),
        });

        _cliGuideHeader = AddSection(Loc.T("⑰ 命令行开关与多套配置"), new[]
        {
            Loc.T("这些开关直接加在 exe 后面运行：--selftest 跑内置自检、--diag 出诊断、--hooktest 测钩子、--dump-triggers / --dump-bindings 导出当前触发键与按键绑定、--render 出整套界面截图、--help 打印下面这段完整清单。"),
            Loc.T("诊断类开关跑完就退：不驻留托盘、不弹启动密码，也不影响正在运行的那个实例。--selftest 会在临时目录里跑，不会碰你的真实数据。"),
            Loc.T("想同时留几套配置：给环境变量 INPUTPLUS_DATA 指一个文件夹再启动，程序只用那份配置；这种模式下启动同步与自动备份会自动关闭，免得把调试数据传上云端。"),
            Loc.T("自检结果在数据目录的 logs/selftest.log，报告同样写在 logs 下。"),
        });

        GuidePanel.Children.Add(BuildUsageBlock());

        AddSection(Loc.T("⑱ 常见问题"), new[]
        {
            Loc.T("改了按键没生效？确认当前在正确的层（看键盘上方的层下拉），以及引擎没有处于「暂停」状态。"),
            Loc.T("锁屏（Win+L）后热键失效是系统限制 —— 锁屏界面在独立的安全桌面上，任何程序都无法在上面拦截键盘。"),
            Loc.T("开机自启在「设置」里开关；它写入的是当前用户的注册表，不需要管理员权限。"),
            Loc.T("宏录制一个键都没录到？先看「测试」页的钩子状态，再看日志里的「宏录制开始 / 结束」两行 —— 里面写着钩子是否正常、这期间钩子实际收到多少按键。"),
            Loc.T("别名替换不展开？检查前缀符号是否对得上、别名是否启用、以及「只在这些程序里展开」有没有限制。"),
            Loc.T("远程桌面 / 向日葵里热键失灵？这类按键是注入的，钩子可能收不到；钩子连续 5 秒没有按键时程序会改由 Raw Input 触发热键，日志里有一条说明。"),
            Loc.T("程序每 60 秒给自己发一个 F24 探钩子：收不到就自动重装。若连续三次都装不上，会提示「钩子装不上」——这时热键仍能用，但按下的键也会传给当前程序，请把本程序加入安全软件白名单。"),
            Loc.T("钩子重装也救不回来时，程序会自动重启一次自己（新进程的钩子通常是好的），日志里会写明「自动重启程序以恢复」；15 分钟内最多一次，不会反复重启。"),
            Loc.T("日志现在是异步写的：按键路径不碰磁盘。以前每写一行都刷盘，磁盘一慢就会把钩子回调卡过 300ms，系统直接丢弃输入并摘掉钩子 —— 那正是「热键突然全失灵」的元凶。"),
            Loc.T("长按触发键却一直输出这个键？说明钩子当前不在链上、热键正靠 Raw Input 兜底（能触发但吞不掉按键）。看日志里有没有「钩子自检失败」，程序会自己重装并重启恢复。"),
        });
    }

    private FrameworkElement AddSection(string title, string[] paragraphs)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Brush.TextPrimary"),
            Margin = new Thickness(0, GuidePanel.Children.Count == 0 ? 0 : 14, 0, 6),
        };
        GuidePanel.Children.Add(header);

        foreach (string p in paragraphs)
        {
            GuidePanel.Children.Add(new TextBlock
            {
                Text = "· " + p,
                Style = (Style)FindResource("Text.Secondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 0, 4),
            });
        }

        return header;
    }

    /// <summary>
    /// 出图用：展开使用指南并滚到「鼠标手势」那一节。
    ///
    /// 指南默认折叠，展开后这一节也远在几百像素以外 ——
    /// 不专门滚过去，新写的 16 条参数说明就没法用截图回归。
    /// </summary>
    internal void ShowGestureGuideForRender()
    {
        GuideExpander.IsExpanded = true;
        UpdateLayout();
        if (_gestureGuideHeader is null) return;
        double y = _gestureGuideHeader.TransformToAncestor(GuidePanel).Transform(default).Y;
        GuideScroll.ScrollToVerticalOffset(Math.Max(0, y - 6));
        UpdateLayout();
    }

    /// <summary>
    /// 出图用：⑪ 那一节有二十多条，GuideScroll 只有 420 高，一屏只放得下九条。
    /// 滚到这一节的尾巴（下一节标题前一屏），新加的那几条才有人验过排版。
    /// </summary>
    internal void ShowGestureGuideTailForRender()
    {
        GuideExpander.IsExpanded = true;
        UpdateLayout();
        if (_deviceGuideHeader is null) return;
        double y = _deviceGuideHeader.TransformToAncestor(GuidePanel).Transform(default).Y;
        GuideScroll.ScrollToVerticalOffset(Math.Max(0, y - GuideScroll.ViewportHeight + 8));
        UpdateLayout();
    }
    /// <summary>出图用：恢复折叠状态，别把展开的样子留给下一页。</summary>
    internal void ResetGuideForRender()
    {
        GuideScroll.ScrollToTop();
        GuideExpander.IsExpanded = false;
        UpdateLayout();
    }

    /// <summary>
    /// 命令行那段说明，直接引用 <see cref="Core.CliFlags.Usage"/> 原文。
    ///
    /// 不另抄一份：抄一份迟早与实现走偏（以前就有过帮助里写着某个开关、
    /// 实际早就改名的事）。这段本来就是整块等宽排版，放进来零成本，
    /// 而且以后加开关，帮助页自动跟着变。
    /// </summary>
    private FrameworkElement BuildUsageBlock()
    {
        return new Border
        {
            Background = (Brush)FindResource("Brush.SurfaceAlt"),
            BorderBrush = (Brush)FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(2, 0, 0, 6),
            Child = new TextBlock
            {
                Text = Core.CliFlags.Usage,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = 11.5,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("Brush.TextSecondary"),
            },
        };
    }

    /// <summary>
    /// 把动作表整份摊成清单：用户问「这软件到底有哪些功能」时，
    /// 只能翻下拉框的话等于没回答。分组顺序跟着动作表本身走，
    /// 组内按标题排序，方便按名字找。
    /// </summary>
    private void RefreshActionCatalog()
    {
        _actionCatalogDirty = true;
        ActionCatalogPanel.Children.Clear();
        if (_host == null) return;
        int count = _host.Actions.Items.Count(i => !i.Hidden);
        ActionCatalogHint.Text = Loc.F("共 {0} 条，都能在按键映射、手势或 Qbar 里绑上", count);
    }

    private void BuildActionCatalog()
    {
        ActionCatalogPanel.Children.Clear();
        if (_host == null) return;
        _actionCatalogDirty = false;

        var items = _host.Actions.Items.Where(i => !i.Hidden).ToList();

        bool first = true;
        foreach (var group in items.GroupBy(i => i.DisplayCategory))
        {
            ActionCatalogPanel.Children.Add(new TextBlock
            {
                // 带上条数：像「键盘按键」这种上百条的组，先让人知道要滚多远
                Text = group.Key + " · " + group.Count(),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Brush.TextPrimary"),
                Margin = new Thickness(0, first ? 0 : 14, 0, 6),
            });
            first = false;

            foreach (var info in group.OrderBy(i => i.Title, StringComparer.CurrentCulture))
            {
                ActionCatalogPanel.Children.Add(new TextBlock
                {
                    Text = ActionLine(info),
                    Style = (Style)FindResource("Text.Secondary"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 0, 0, 3),
                });
            }
        }
    }

    /// <summary>
    /// 单条动作的展示文字：标题 +（需要填东西时的参数提示）+ 说明。
    ///
    /// 抽成静态纯函数是为了能写自检 —— 参数提示漏掉的话，
    /// 用户在下拉里选中「打开网址」却根本看不出还得填网址。
    /// </summary>
    internal static string ActionLine(Core.Actions.ActionInfo info)
    {
        var sb = new StringBuilder("· ").Append(info.Title);
        if (info.NeedsArg && !string.IsNullOrWhiteSpace(info.ArgHint))
            sb.Append(" (").Append(info.ArgHint).Append(')');
        if (!string.IsNullOrWhiteSpace(info.Description))
            sb.Append(" — ").Append(info.Description);
        return sb.ToString();
    }

    /// <summary>出图用：展开指南并滚到指定小节（⑫ 设备方案、⑰ 命令行）。</summary>
    internal void ShowGuideSectionForRender(string section)
    {
        GuideExpander.IsExpanded = true;
        UpdateLayout();
        var header = section == "devices" ? _deviceGuideHeader : _cliGuideHeader;
        if (header is null) return;
        double y = header.TransformToAncestor(GuidePanel).Transform(default).Y;
        GuideScroll.ScrollToVerticalOffset(Math.Max(0, y - 6));
        UpdateLayout();
    }

    /// <summary>出图用：展开「全部功能一览」并回到顶部。</summary>
    internal void ShowActionCatalogForRender()
    {
        ActionCatalogExpander.IsExpanded = true;
        UpdateLayout();
        ActionCatalogScroll.ScrollToTop();
        UpdateLayout();
    }

    /// <summary>出图用：恢复折叠状态，别把展开的样子留给下一页。</summary>
    internal void ResetActionCatalogForRender()
    {
        ActionCatalogScroll.ScrollToTop();
        ActionCatalogExpander.IsExpanded = false;
        UpdateLayout();
    }

    private void Build(string filter)
    {
        if (_host == null) return;
        var groups = new List<HelpGroup>();
        var byCategory = new Dictionary<string, List<HelpItem>>();

        /* 遍历**所有层**的绑定。
         *
         * 每层用各自的触发键做前缀 —— 之前写死 "Caps"，第 2 层
         * 配的是 F13 也显示 "Caps+..."，与实际完全对不上。 */
        foreach (var layer in _host.Settings.Layers.OrderBy(l => l.Id))
        {
            var keys = layer.Keys.Count > 0
                ? layer.Keys
                : (layer.Id == 1 ? _host.Settings.Keys : layer.Keys);

            var trigger = _host.Settings.Triggers.FirstOrDefault(t => t.LayerId == layer.Id);
            string layerPrefix = trigger != null
                ? DescribeKey(trigger.Key)
                : Loc.F("第{0}层", layer.Id);

            foreach (var binding in keys.Where(k => k.IsBound))
            {
                var parsed = KeyName.ParseCombo(binding.Combo);
                if (parsed == null) continue;
                var (mods, key) = parsed.Value;
                if (key == "capslock") continue;

                string modPart = mods switch
                {
                    KeyModifiers.None => "",
                    _ => "+" + string.Join("+", new[]
                    {
                        mods.HasFlag(KeyModifiers.Ctrl) ? "Ctrl" : null,
                        mods.HasFlag(KeyModifiers.Shift) ? "Shift" : null,
                        mods.HasFlag(KeyModifiers.Alt) ? "Alt" : null,
                        mods.HasFlag(KeyModifiers.Win) ? "Win" : null,
                    }.Where(x => x != null)),
                };
                string combo = $"{layerPrefix}{modPart}+{KeyName.Display(key)}";

                var info = _host.Actions.Get(binding.Action);
                if (info == null) continue;

                string desc = DescribeBinding(binding, info);

                if (filter.Length > 0 &&
                    !combo.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !info.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !desc.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!byCategory.TryGetValue(info.DisplayCategory, out var list))
                {
                    list = new List<HelpItem>();
                    byCategory[info.DisplayCategory] = list;
                }
                list.Add(new HelpItem(combo, info.Title, desc));
            }
        }

        // 内置的固定说明
        byCategory.TryAdd(Loc.T("按键小技巧"), new List<HelpItem>());
        var panel = byCategory[Loc.T("按键小技巧")];
        if (filter.Length == 0)
        {
            panel.Add(new HelpItem(Loc.T("短按触发键"), Loc.T("切换大小写"),
                Loc.T("不按其他键时松开的默认行为，可在「触发键与层」里改")));
            panel.Add(new HelpItem(Loc.T("双击触发键"), Loc.T("打开 Qbar"),
                Loc.F("{0} 毫秒内连按两下", Core.Input.HotkeyEngine.DoubleTapWindowMs)));
            panel.Add(new HelpItem(Loc.T("Qbar 内"), Loc.T("↑ ↓ 选择"), Loc.T("Enter 执行，Esc 关闭，Tab 补全")));
            panel.Add(new HelpItem(Loc.T("Qbar 内"), "=1+2*3", Loc.T("直接计算并把结果复制到剪贴板")));
        }

        string[] order =
        {
            Loc.T("光标移动"), Loc.T("文本编辑"), Loc.T("剪贴板"), Loc.T("窗口功能"), Loc.T("窗口绑定"),
            Loc.T("智能直达"), Loc.T("直达链接"), Loc.T("自定义按键"), Loc.T("宏"),
            Loc.T("键盘按键"), Loc.T("鼠标功能"), Loc.T("系统功能"), Loc.T("基础"),
        };
        foreach (var cat in order)
        {
            if (!byCategory.TryGetValue(cat, out var items) || items.Count == 0) continue;
            groups.Add(new HelpGroup(cat, items.Count.ToString(), items));
        }
        foreach (var kv in byCategory)
        {
            if (order.Contains(kv.Key) || kv.Value.Count == 0) continue;
            groups.Add(new HelpGroup(kv.Key, kv.Value.Count.ToString(), kv.Value));
        }

        // 每组内部按"层 → 修饰键 → 键盘顺序"排列，查找更快
        foreach (var g in groups)
        {
            g.Items.Sort((a, b) =>
            {
                int la = LayerRank(a.Combo), lb = LayerRank(b.Combo);
                if (la != lb) return la.CompareTo(lb);
                return KeyRank(a.Combo).CompareTo(KeyRank(b.Combo));
            });
        }

        _groups = groups;
        int total = 0;
        foreach (var g in groups) total += g.Items.Count;

        /* 空态与计数：以前筛不到东西时只剩一张空白卡片，
           用户分不清是「没搜到」还是「页面没加载」。 */
        EmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (total == 0)
        {
            EmptyTitle.Text = filter.Length > 0 ? Loc.T("没有匹配的快捷键") : Loc.T("还没有任何绑定");
            EmptyDesc.Text = filter.Length > 0
                ? Loc.T("换个关键词试试 —— 可以搜按键名、功能名或说明。")
                : Loc.T("到「按键映射」页点一个键、选一个功能，这里就会多一条。");
        }

        HintText.Text = filter.Length > 0
            ? Loc.F("筛选「{0}」· 命中 {1} 条", filter, total)
            : _defaultHint;
        CopySheetButton.IsEnabled = total > 0;

        Groups.ItemsSource = groups;
    }

    /// <summary>
    /// 把当前速查表（含筛选结果）复制成纯文本。
    ///
    /// 速查表最常见的用途是发给别人或贴进笔记 —— 截图会被裁、会糊，
    /// 纯文本可以直接改。格式保持一层一条，方便粘到 Markdown 里。
    /// </summary>
    private void OnCopySheet(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.F("Input Plus · 快捷键速查（v{0}）", Core.Update.UpdateService.CurrentVersion));
        sb.AppendLine();

        int total = 0;
        foreach (var g in _groups)
        {
            sb.AppendLine("## " + g.Title + "（" + g.Items.Count + "）");
            foreach (var item in g.Items)
            {
                sb.Append("- ").Append(item.Combo).Append("  ").Append(item.Title);
                if (item.Desc.Length > 0) sb.Append(" —— ").Append(item.Desc);
                sb.AppendLine();
                total++;
            }
            sb.AppendLine();
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            CopySheetButton.Content = Loc.T("已复制");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CopySheetButton.Content = Loc.T("复制速查表");
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            /* 剪贴板被别的程序独占时 SetText 会抛 —— 只在提示条上说明，不弹窗打断。 */
            Core.CoreLog.Swallow(ex, "复制速查表");
            HintText.Text = Loc.T("复制失败：剪贴板正被其它程序占用，稍后再试。");
        }
    }

    /// <summary>
    /// 一条绑定该显示什么说明。
    ///
    /// 窗口绑定显示程序名（或自定义名），直达网页显示备注/网址 ——
    /// 和其他页面保持同一套规则，避免"这里显示覆盖、那里显示 Photoshop"。
    /// </summary>
    internal static string DescribeBinding(Core.Settings.KeyBinding binding, Core.Actions.ActionInfo info)
    {
        string id = binding.Action ?? "";
        string? note = string.IsNullOrWhiteSpace(binding.Note) ? null : binding.Note!.Trim();

        if (id == "customKey")
        {
            // 备注优先：键帽上显示的就是备注，速查表跟着一致，免得两处叫法不同
            if (note != null) return note;
            var spec = Core.Actions.CustomKeySpec.FromArg(binding.Arg);
            if (!spec.IsReady) return Loc.T("自定义按键（还没配好）");
            return spec.HasApp
                ? Loc.F("把 {0} 发给 {1}", Core.Actions.CustomKeyRunner.ComboDisplay(spec.Key), spec.ProcessName)
                : Loc.F("发送 {0}（未绑定应用，发给当前窗口）", Core.Actions.CustomKeyRunner.ComboDisplay(spec.Key))
                 + (spec.RunAsAdmin ? Loc.T("（需要时以管理员身份启动）") : "")
                 + (spec.BringFront ? Loc.T("，会先切到前台") : "");
        }

        if (id == "winBind" && !string.IsNullOrWhiteSpace(binding.Arg))
        {
            string name = note ?? System.IO.Path.GetFileNameWithoutExtension(binding.Arg);
            return Loc.F("切到 {0}（再按一次最小化）", name);
        }

        if (id == "webPage")
            return Loc.T("打开 ") + Core.Actions.UrlUtil.LabelOf(note, binding.Arg);

        // 直达文件夹：说清"打开的是哪个目录"，目录名比整条路径好认
        if (id == "openFolder")
            return Loc.T("打开文件夹 ") + UI.Views.KeysView.FolderLabelOf(note, binding.Arg);

        // 直达文件：同理，用文件名代替整条路径
        if (id == "openFile")
            return Loc.T("打开文件 ") + UI.Views.KeysView.FileLabelOf(note, binding.Arg);

        /* 输入文本：**只显示备注，内容一律隐藏**（用户明确要求）。
         *
         * 这个动作的参数就是文字本身 —— 邮箱、地址、常用回复，
         * 而速查表是最容易被截图分享的一页。以前走到下面的
         * Loc.T("（参数 xxx）")分支，整段文字直接摊在屏幕上。
         * 需要认出是哪一条就靠备注；没填备注时也不回退显示正文。 */
        if (id == "typeText")
            return note == null ? Loc.T("输入文本（内容已隐藏）") : Loc.T("输入文本 · ") + note;

        /* 其余动作统一走"备注优先"（用户明确要求）。
         *
         * 宏最典型：参数是 `macro:3` / 重复次数这种机器串，
         * 拼出来就是「播放宏：每日备份（参数 3）」，用户自己写的备注
         * 「备份」反而看不到。凡是填了备注就以备注为准，
         * 没填才退回「动作说明 + 参数」。 */
        if (note != null) return note;

        return binding.Arg.Length > 0
            ? Loc.F("{0}（参数 {1}）", info.Description, binding.Arg)
            : info.Description;
    }

    private static int LayerRank(string combo) =>
        combo.Contains("Shift", StringComparison.Ordinal) ? 1 :
        combo.Contains("Ctrl", StringComparison.Ordinal) ? 2 :
        combo.Contains("Alt", StringComparison.Ordinal) ? 3 :
        combo.Contains("Win", StringComparison.Ordinal) ? 4 : 0;

    private static int KeyRank(string combo)
    {
        int idx = combo.LastIndexOf('+');
        string key = idx >= 0 ? combo[(idx + 1)..] : combo;
        int rank = Array.IndexOf(KeyOrder, key);
        return rank < 0 ? 999 : rank;
    }

    /// <summary>按键盘上从左到右、从上到下的顺序（帮助页读起来更自然）。</summary>
    private static readonly string[] KeyOrder =
    {
        "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P",
        "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'",
        "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/",
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
        "`", "-", "=", "[", "]", "\\",
        "Backspace", "Tab", "Enter", "Space", "RAlt",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "PgUp", "PgDn", "Home", "End", "Up", "Down", "Left", "Right",
    };

    private sealed record HelpItem(string Combo, string Title, string Desc);

    private sealed class HelpGroup
    {
        public HelpGroup(string title, string count, List<HelpItem> items)
        {
            Title = title;
            Count = count;
            Items = items;
        }

        public string Title { get; }
        public string Count { get; }
        public List<HelpItem> Items { get; }
    }
}
