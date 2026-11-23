using InputPlus.Core.Gestures;
using InputPlus.Core.Qbar.Search;
using System.Text.Json.Serialization;
using InputPlus.Core.Input;

using InputPlus.Core.Scripting;

namespace InputPlus.Core.Settings;

/// <summary>一个按键绑定：组合键 ID -> 动作 + 可选参数。</summary>
public sealed class KeyBinding
{
    public string Combo { get; set; } = "";
    public string Action { get; set; } = "doNothing";
    public string Arg { get; set; } = "";

    /// <summary>
    /// 备注（可选）。用于「直达链接」的三类目标（网址 / 文件夹 / 文件）：
    /// 起个好记的名字，键帽上显示的就是"公司后台"而不是一长串 URL 或路径。
    ///
    /// 默认值是 <c>null</c> 而不是 <c>""</c>：配合 WhenWritingNull，
    /// 没写备注时这个字段根本不出现在设置文件里 —— 配置文件与旧版完全一致。
    /// 读取时统一用 <c>?.Trim()</c> 处理，不会因为 null 出问题。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    [JsonIgnore]
    public bool IsBound => !string.IsNullOrEmpty(Action) && Action != "doNothing";
}

/// <summary>
/// 一条「基础改键」：不按住任何前缀键，把 <see cref="From"/> 换成 <see cref="To"/>。
///
/// 与「层内绑定」的区别：
///   · 层内绑定要按住触发键才生效，一条键可以有多套含义；
///   · 基础改键**始终生效**，语义就是"这个物理键现在等于那个键"。
/// 两者冲突时层内绑定优先（层是"临时接管键盘"）。
/// </summary>
public sealed class KeyRemap
{
    /// <summary>源键名（见 KeyName，如 capslock / esc / lctrl）。</summary>
    public string From { get; set; } = "";

    /// <summary>目标键名。为空表示不生效。</summary>
    public string To { get; set; } = "";

    /// <summary>
    /// 只在这只键盘上生效（VID/PID 或完整设备路径）。留空 = 所有键盘。
    ///
    /// 判定顺序：先看有没有这只键盘的规则，没有再退回"所有键盘"的规则。
    /// 拿不到设备标识时按"所有键盘"处理（fail open）——
    /// 宁可多改一只键盘，也不要在关联失败时让键位规则时灵时不灵。
    /// </summary>
    public string Device { get; set; } = "";

    public string Comment { get; set; } = "";
}

/// <summary>
/// 「按应用自动启用层」的一条规则：前台是这个进程时，把它绑定的层当成**叠加层**。
///
/// 与触发键的层不同：叠加层**不吞未绑定的键** —— 只有真正绑过的组合才执行动作，
/// 其余按键原样放行。否则在 IDE 里打字会被吞掉，这个功能就没法用了。
/// </summary>
public sealed class AppLayerRule
{
    /// <summary>进程名（如 Code.exe）。存文件名即可，匹配时大小写不敏感。</summary>
    public string Process { get; set; } = "";

    /// <summary>要叠加的层 id。</summary>
    public int LayerId { get; set; }
}

/// <summary>
/// 一条「双击动作」：某个键在 <see cref="HotkeyEngine.DoubleTapWindowMs"/> 内被按两次时，
/// 执行指定动作（第二次按下会被吞掉，不会打出第二个字符）。
///
/// 与触发键的 DoubleTapAction 的区别：触发键必须"按住才进层"，会改变这个键的基本行为；
/// 这里对**任何键**都适用，且单击完全保持原样。
/// </summary>
public sealed class KeyGesture
{
    /// <summary>键名（见 KeyName，如 capslock / esc / f1）。</summary>
    public string Key { get; set; } = "";

    /// <summary>双击时执行的动作 id（见 ActionCatalog）。</summary>
    public string DoubleTapAction { get; set; } = "";

    public string Comment { get; set; } = "";
}

/// <summary>
/// 一条「文本片段」：输入触发词后自动展开成一段文字。
///
/// 触发规则（保守，防误触）：
///   · 触发词必须在**词边界**上（前一个字符不是字母/数字）；
///   · 默认要求触发词以非字母数字开头（如 `;mail`），这类前缀几乎不会
///     出现在正常打字里。
/// </summary>
public sealed class Snippet
{
    /// <summary>触发词，如 `;mail`。</summary>
    public string Trigger { get; set; } = "";

    /// <summary>展开后的内容（支持换行）。</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// 是否启用。关掉后只是不展开，配置还留着（片段列表经常有一堆临时用的）。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 只在哪些程序里展开（进程名，逗号分隔，如 `code.exe, notepad.exe`）。
    ///
    /// 留空 = 所有程序都展开（旧行为，默认值）。
    /// 存在的意义：片段误触是不可逆的输入副作用 —— 在聊天窗口里打 `;log`
    /// 也会展开成一大段代码，是这类功能最容易被卸载的原因。
    /// </summary>
    public string Apps { get; set; } = "";

    /// <summary>
    /// 这条别名被展开过多少次。
    ///
    /// 用途：找出"设了但从来没用过"的条目 —— 别名攒多了以后，
    /// 光看列表分不清哪些还在用、哪些是当年试了一下就放着的。
    /// </summary>
    public int UseCount { get; set; }

    /// <summary>最近一次展开的时间（从未用过就是 null）。</summary>
    public DateTime? LastUsed { get; set; }

    /// <summary>
    /// 自定义排序位置（越小越靠前）。
    ///
    /// 只有把列表按「自定义顺序」排时才用它；默认全部为 0，
    /// 此时退回按触发词排 —— 老配置不需要迁移。
    /// </summary>
    public int Order { get; set; }

    public string Comment { get; set; } = "";

    /// <summary>分组（空 = 未分组）。</summary>
    public string Group { get; set; } = "";
}


/// <summary>
/// 文本片段的全局设置。
/// </summary>
public sealed class HotstringOptions
{
    /// <summary>
    /// 触发前缀符号（第一个）。空字符串 = 不需要前缀，直接打触发词。
    ///
    /// 真正的来源是 <see cref="Prefixes"/>；这个字段保留是为了**兼容老配置**
    /// （4.25.0 及以前只存一个字符），读盘时会被并进列表。
    /// </summary>
    public string Prefix { get; set; } = ":";

    /// <summary>
    /// 展开之后替用户按一下 Shift。
    ///
    /// 别名基本都用英文打（触发词不能以字母开头，但整串通常是 ASCII），而正文多数是中文；
    /// 打完拼音再切回来，每展开一次就得多按一次 Shift。
    ///
    /// **默认关（2026-11-23 改）**：按 Shift 就是替用户切换输入法的中/英模式，
    /// 和「软件不该自己换输入法」是同一件事 —— 用户报的正是这个。
    /// 需要的人可以在「别名」页显式勾上；勾了之后软件才会替他按这一下。
    /// 老配置里存着开的人不受影响（他们当初是自己选的）。
    /// </summary>
    public bool RestoreImeAfterExpand { get; set; }

    /// <summary>
    /// 界面上可选的触发符号（全是半角）。
    ///
    /// 只列这一组是因为它们在中文/英文输入法下都是同一个键、同一个字符，
    /// 不会出现"看得到打不出"的情况；顺序就是胶囊里显示的顺序。
    /// </summary>
    public static readonly (string Symbol, string Label)[] SymbolChoices =
    {
        (":", "冒号"),
        ("`", "反引号"),
        ("~", "波浪号"),
        ("!", "感叹号"),
        ("-", "短横线"),
        (",", "逗号"),
        (".", "句号"),
        (";", "分号"),
    };

    /// <summary>符号对应的中文名字（不在列表里时返回空串，界面直接把符号本身当名字）。</summary>
    public static string LabelOf(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return "";
        foreach (var (s, label) in SymbolChoices)
        {
            if (string.Equals(s, symbol, StringComparison.Ordinal)) return label;
        }
        return "";
    }

    /// <summary>
    /// 全部前缀符号（每个字符一个）。可以配多个：`;` `/` `、` 都能带出别名。
    ///
    /// 为什么要多个：中文输入法里全角/半角是同一个键打出来的，
    /// 但屏幕上显示成两个字符（`\` 和 `、`）。只认一个符号时，
    /// 用户改成 `、` 就会发现"怎么打都不触发"。
    /// </summary>
    public List<string> Prefixes { get; set; } = new();

    /// <summary>用全角符号（4.26.0 起不再有界面开关，只用于读取老配置）。</summary>
    public bool FullWidth { get; set; }

    /// <summary>打完触发词再按空格就替换（推荐）；关掉则只认触发键（Caps+Tab）。</summary>
    public bool ExpandOnSpace { get; set; } = true;

    /// <summary>替换掉那个空格（true）还是保留它（false）。</summary>
    public bool SwallowSpace { get; set; } = true;

    /// <summary>列表里优先显示备注（没备注才显示触发词）。</summary>
    public bool ShowNoteFirst { get; set; } = true;

    /// <summary>
    /// 在 token 边界（空格 / 回车 / 输入法提交）用**控件里的真实文本**校对片段缓冲。
    ///
    /// 为什么需要：键盘钩子只看得到物理键。用户退格、用鼠标拖选、用输入法改字之后，
    /// 缓冲里记的和屏幕上显示的就会不一致 —— 那时只有控件里的字说了算。
    /// 关掉它就退回"只信钩子"的老行为（老配置里没有这个字段，默认开）。
    ///
    /// 代价：每次 token 边界读一次焦点控件文本，带 40ms 硬超时（见 TextMirror）。
    /// </summary>
    public bool VerifyWithControlText { get; set; } = true;

    /// <summary>
    /// 列表里是否显示展开后的内容。
    ///
    /// 关掉后只显示备注 —— 别名可能是整段正文/密钥/口令，
    /// 摊在列表里一览无余并不合适（旁边有人、或者正在录屏时）。
    /// </summary>
    public bool ShowTextInList { get; set; }

    /// <summary>分组列表的排序方式：name（按名称）/ count（按条目数）/ custom（自定义拖拽顺序）。</summary>
    public string GroupSortMode { get; set; } = "name";

    /// <summary>
    /// 「全部别名」那张表的列宽，按列序：关键字 / 正文 / 备注 / 分组 / 使用。
    ///
    /// 0 = 这一列保持自适应（正文列是星号宽度，记成像素就把它钉死了）。
    /// 和 Qbar 的 <see cref="QbarOptions.EntryColumnWidths"/> 同一套规则，
    /// 免得两页的表一个记住宽度、一个每次忘。
    /// </summary>
    public List<double> ColumnWidths { get; set; } = new();

    /// <summary>
    /// 4.19.2 之前"列表显示替换内容"默认是开的；新版默认改为显示备注。
    /// 这个标记保证老配置只被重置一次，之后用户自己切回"显示内容"不会被再改。
    /// </summary>
    public bool ListModeMigrated { get; set; } = true;

    /// <summary>
    /// 实际生效的前缀列表：先取 <see cref="Prefixes"/>，为空时用老字段
    /// <see cref="Prefix"/>（并把全角设置换算进去）。
    ///
    /// 每个字符一个前缀，最长 4 个字符（留出 `;;` 这类双符号前缀的余地），
    /// 去重且保持用户书写的顺序 —— 第一个是"默认前缀"，
    /// 新建条目、显示用到的都是它。
    /// </summary>
    public IReadOnlyList<string> EffectivePrefixes
    {
        get
        {
            var list = new List<string>();
            void Add(string? s)
            {
                string t = (s ?? "").Trim();
                if (t.Length == 0 || t.Length > 4) return;
                if (list.Contains(t, StringComparer.Ordinal)) return;
                list.Add(t);
            }

            foreach (string p in Prefixes ?? new List<string>()) Add(p);

            /* 老配置里只有 Prefix（可能还开着全角）。
             * 新配置读进来时 Prefixes 已经有值，这里就不会再插手。 */
            if (list.Count == 0)
                Add(FullWidth ? ToFullWidthOrSelf(Prefix ?? "") : (Prefix ?? ""));

            return list;
        }
    }

    /// <summary>第一个前缀（老代码用它；新代码请用 <see cref="EffectivePrefixes"/>）。</summary>
    public string EffectivePrefix => EffectivePrefixes.Count > 0 ? EffectivePrefixes[0] : "";

    /// <summary>
    /// 把前缀整理成规范形态：列表去空去重，<see cref="Prefix"/> 同步成第一个，
    /// 老的全角开关收编成实际的字面字符后关掉。
    ///
    /// 读盘与"用户改完前缀"两条路径都要调它，否则会出现
    /// "列表里有一份、Prefix 里还是旧的"这种自相矛盾的配置。
    /// </summary>
    public void NormalizePrefixes()
    {
        var list = EffectivePrefixes.ToList();
        Prefixes = list;
        Prefix = list.Count > 0 ? list[0] : "";
        FullWidth = false;
    }

    /// <summary>把半角符号换成全角（只处理常见 ASCII 符号）。</summary>
    public static string ToFullWidthOrSelf(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) sb.Append(ToFullWidthOrSelf(c));
        return sb.ToString();
    }

    public static char ToFullWidthOrSelf(char c) => c switch
    {
        '/' => '／',
        '\\' => '＼',
        ',' => '，',
        '.' => '．',
        ';' => '；',
        ':' => '：',
        '=' => '＝',
        '-' => '－',
        '?' => '？',
        '!' => '！',
        '+' => '＋',
        '#' => '＃',
        '@' => '＠',
        _ => c,
    };
}

public sealed class QEntry
{
    /// <summary>
    /// 这条记录是否匹配过滤词。
    ///
    /// 按**关键字 / 备注 / 内容**三个字段做大小写不敏感的包含匹配 ——
    /// 用户记不住关键字时可以用中文备注找（"剪贴板""截图"），
    /// 也可以用网址片段找。空过滤词 = 全部匹配。
    ///
    /// 抽成静态方法是为了让自检能直接验，而不必去驱动界面控件。
    /// </summary>
    public static bool Matches(QEntry entry, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string f = filter.Trim();
        return (entry.Key?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
               || (entry.Comment?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
               || (entry.Value?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>按过滤词筛一组记录。</summary>
    public static List<QEntry> Filter(IEnumerable<QEntry> source, string? filter) =>
        source.Where(e => Matches(e, filter)).ToList();

    /// <summary>
    /// 按**某一列**匹配（设置页搜索框里选的那一列）。
    ///
    /// 为什么单独给「内容」一条路：这一列装的是 URL 模板、程序路径、片段正文，
    /// 常常是多行 / 带制表符的。用户按屏幕上的样子打"两个词"，原来的 Contains
    /// 会因为词中间夹着换行而永远搜不到 —— 所以内容列先把连续空白折成一个空格再比。
    /// 其余列保持"原样包含"，和以前逐字节一致（自检钉着那一套）。
    /// </summary>
    public static bool MatchesColumn(QEntry entry, string? filter, string? column)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string f = filter.Trim();

        switch (column)
        {
            case "key":
                return entry.Key?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false;

            case "comment":
                return entry.Comment?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false;

            case "value":
                return ValueContains(entry.Value, f);

            default:
                return Matches(entry, f);
        }
    }

    /// <summary>
    /// 内容列专用：先按原样比一次（快，也覆盖单行最常见的情况），
    /// 不中再把两边空白折起来比 —— 多行正文里的"相邻两行"于是也能被搜到。
    /// </summary>
    private static bool ValueContains(string? haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack)) return false;
        if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return FoldWhitespace(haystack).Contains(FoldWhitespace(needle), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把连续空白（换行、制表、多个空格）折成一个空格，并去掉首尾空白。</summary>
    private static string FoldWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>
    /// 分组（文本片段用）。空 = 未分组。
    ///
    /// 片段多了以后列表会长得没法看，同一个主题（"地址""账号""常用语"）放一组，
    /// 列表里按组显示、组内按触发词排序。
    /// </summary>
    public string Group { get; set; } = "";

    /// <summary>是否启用（文本片段用；关掉就只是留在列表里不参与替换）。</summary>
    public bool Enabled { get; set; } = true;
    public string Comment { get; set; } = "";

    /// <summary>
    /// 自定义图标（png / ico / exe 路径）。为空时：
    /// 程序条目会自动取该 exe 的图标，搜索/网址条目则显示首字母色块。
    /// </summary>
    public string IconPath { get; set; } = "";

    /// <summary>
    /// 内置图标的资源名（对应 Assets/engine-icons 里的一张 PNG）。
    ///
    /// 与 <see cref="IconPath"/> 的区别：IconPath 指向磁盘上的用户自定义文件，
    /// 而 EngineIcon 指向**随程序打包**的内置图标 —— 内置引擎表用的都是后者，
    /// 这样即使用户清掉数据目录，图标也不会丢，也不依赖任何网络下载。
    /// </summary>
    public string EngineIcon { get; set; } = "";

    /// <summary>Run 条目：是否以管理员身份启动。</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>Run 条目：启动参数。</summary>
    public string Arguments { get; set; } = "";

    /// <summary>
    /// 这一条是否出现在中键菜单里（只有「固定项」用得上）。
    ///
    /// 为什么是**每个条目自己**的开关而不是整张表一个开关：固定项里通常混着
    /// "每天必点的几个目录"和"留作备查的网址"，用户要的正是这种粒度。
    /// 出厂 false —— 老配置升级上来中键菜单不会被塞满一堆没挑过的东西。
    /// </summary>
    public bool ShowInMiddleMenu { get; set; }

    /// <summary>
    /// 「来源」：把这一条收进固定项时，前台程序是谁（例如 <c>wpspdf</c>、<c>notepad</c>）。
    ///
    /// 只用于排查 —— 用户报「这条怎么多出来的」时，日志里有它就能一句话答完。
    /// **不参与排序**：固定项的顺序就是用户在设置页里拖出来的顺序，
    /// 为一个来源字段去插队，等于把用户的手工排序弄乱。
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// 用过几次（v1.5.1）。目前只有「收藏当前文件」在"已经收藏过"时 +1 ——
    /// 它是"这条确实被反复需要"的证据，将来清理长期不用的固定项要用它。
    /// 老配置里没有这个字段，反序列化取默认值 0，不需要迁移。
    /// </summary>
    public int UseCount { get; set; }

    /// <summary>最近一次用它的时间（本地时间）。null = 从没用过。</summary>
    public DateTime? LastUsed { get; set; }

    /// <summary>标签上显示的名字：优先用备注，其次用关键字。</summary>
    /// <summary>
    /// 界面上显示的备注。
    ///
    /// 「默认搜索」这条是**程序生成的数据**（默认引擎表的第一条），
    /// 它不像用户自己填的备注 —— 该跟着界面语言走。否则英文界面下
    /// 下拉里会突兀地出现中文"默认搜索"，连图标兜底字母都变成"默"。
    /// 其余情况（用户备注 / 没备注时用关键字）原样返回。
    /// </summary>
    [JsonIgnore]
    public string DisplayName =>
        string.Equals(Comment?.Trim(), "默认搜索", System.StringComparison.Ordinal)
            ? Localization.Loc.T("默认搜索")
            : string.IsNullOrWhiteSpace(Comment) ? Key : Comment.Trim();

    public QEntry Clone() => new()
    {
        Key = Key, Value = Value, Comment = Comment, IconPath = IconPath,
        EngineIcon = EngineIcon,
        RunAsAdmin = RunAsAdmin, Arguments = Arguments,
        ShowInMiddleMenu = ShowInMiddleMenu,
        /* 这三个也要带上：收藏那条路靠它们做"已收藏过 → 次数 +1"，
         * 漏了就等于每次双击都把证据抹掉（设置页里那份是活对象，
         * 但备份/同步是走 Clone/序列化的，漏字段会在往返里丢）。 */
        Source = Source, UseCount = UseCount, LastUsed = LastUsed,
    };
}

public sealed class GlobalOptions
{
    /// <summary>开机自启（写入 HKCU Run）。</summary>
    /// <summary>
    /// 触发键左上角那颗星相对键名的倍数（1.0–2.5，默认 1.5）。
    ///
    /// 之前是写死的 1.5，用户问"在哪调"——设置页没有入口，只能改代码。
    /// 现在做成设置项，实时生效。
    /// </summary>
    public double TriggerStarScale { get; set; } = 1.5;

    /// <summary>
    /// 绑定内容版本号：每次保存设置时 +1。
    ///
    /// 层绑定字典是缓存复用的，早先只按"List 实例 + 条数"判断失效 ——
    /// `ResetLayerOne` 是**清空后原地重新填充**，条数一变（或恰好不变）都可能
    /// 命中旧缓存，出现"界面显示默认动作、键盘还执行旧动作"。加上版本号即可。
    /// </summary>
    public int BindingsVersion { get; set; }

    /// <summary>
    /// 别名的分组自定义顺序（拖拽后的顺序）。空 = 还没拖过。
    /// 分组本身是"名字集合"，顺序必须单独存，否则每次刷新又按名字排回去了。
    /// </summary>
    public List<string> SnippetGroupOrder { get; set; } = new();

    public bool StartWithWindows { get; set; }


    /// <summary>热键方案：capslox（默认）或 capslock_plus（3.0 之前的经典布局）。</summary>
    public string HotkeyScheme { get; set; } = "capslox";

    /// <summary>独立剪贴板功能（Caps+C/X/V 使用 CapsLock+ 专属剪贴板）。</summary>
    public bool AllowClipboard { get; set; } = true;

    /// <summary>Caps+LAlt 时鼠标速度 1~20。</summary>
    public int MouseSpeed { get; set; } = 3;

    /// <summary>短按 CapsLock 的行为。</summary>
    public string TapCapsAction { get; set; } = "toggleCapsLock";

    /// <summary>动作执行后在屏幕右下角显示轻提示。</summary>
    public bool ShowToast { get; set; } = true;

    /// <summary>
    /// 「输入文本」用哪种方式打字：auto（默认）/ keys / unicode。
    ///
    /// 默认 auto = 纯 ASCII 走**真实按键**，其它走 Unicode 注入。
    /// 密码框会过滤掉"没有扫描码"的 Unicode 合成输入，所以默认改成了真实按键；
    /// 极少数程序反过来只认 Unicode，那种情况下在设置里改成 unicode 即可。
    /// </summary>
    public string TextInputMode { get; set; } = "auto";

    /// <summary>
    /// 音量键一次调多少（百分比）。
    ///
    /// 音量本身走系统音量键（这样才有系统横条），而系统步进是固定的
    /// （通常 2%），所以这里按"发几次键"来凑：设 5% 就按系统步进发 2~3 次，
    /// 并用累计误差补偿，长期平均正好等于设定值。
    /// </summary>
    public int VolumeStep { get; set; } = 5;

    /// <summary>亮度键一次调多少（百分比）。亮度是我们自己调的，可以精确到 1%。</summary>
    public int BrightnessStep { get; set; } = 10;

    /// <summary>
    /// 宏录制的固定延迟（毫秒）。0 = 按真实按键间隔（默认）。
    ///
    /// 存在的理由：录"点一串按钮"这类操作时，人手按的真实间隔忽快忽慢，
    /// 回放出来节奏很脏；固定成 100~300ms 反而稳定，也更好改。
    /// </summary>
    public int MacroFixedDelayMs { get; set; }

    /// <summary>
    /// 是否以管理员身份启动。
    ///
    /// **默认 false**，这是刻意的。
    ///
    /// 管理员权限会让程序无法向普通权限的窗口注入输入（UIPI 限制），
    /// 于是"选中文字 → 直接搜索"这类功能在浏览器、编辑器里**全部失效**。
    /// 而绝大多数功能（键盘钩子、窗口绑定、宏、Qbar）并不需要管理员权限。
    ///
    /// 只有确实要操作提权窗口时才需要打开它。
    /// </summary>
    /// <summary>
    /// 默认以管理员身份运行。
    ///
    /// 为什么默认开：本地搜索要读全盘索引、提权打开程序也是常用动作，
    /// 每次点"以管理员运行"都弹一次 UAC 更烦。代价是键盘钩子在管理员进程里
    /// 收不到普通权限窗口的按键（Windows 的 UIPI 限制），所以 <see cref="ElevationHelper"/> 保留
    /// 了 CLP_SKIP_ELEVATE=1 逃生开关，设置页也能一键取消。
    /// </summary>
    public bool RunAsAdmin { get; set; } = true;

    /// <summary>
    /// 自动更新完成后是否弹出「已更新到 x.x.x」提示。
    /// 开机自启的用户每次更新都被弹一次很烦，所以独立成一个开关（默认开）。
    /// </summary>
    public bool ShowUpdateToast { get; set; } = true;

    /// <summary>
    /// 每次组合键触发时在屏幕上显示动作名称。
    /// 默认关闭；开启后能一眼看出“按键有没有被识别”，排查问题时非常有用。
    /// </summary>
    public bool ShowActionFeedback { get; set; }

    /// <summary>关闭托盘图标（不推荐）。</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// 用快捷键**唤出**一个已经在运行的窗口时，播一段短促的缩放归位动画。
    ///
    /// 只有"切过去 / 从最小化恢复"才播；"把前台窗口收起来"不播 ——
    /// 最小化本身就是一个明确的空间变化，再叠一层缩放只会显得乱。
    /// 默认开：它只影响观感，关掉也不改变任何切换行为。
    /// </summary>
    public bool SummonAnimation { get; set; } = true;

    /// <summary>唤出动画时长（毫秒）。默认 170ms；会被夹到 60~400。</summary>
    public int SummonAnimationMs { get; set; } = 170;


    /// <summary>
    /// 文本片段：输入触发词自动展开。默认空，不影响任何现有输入。
    /// </summary>
    public List<Snippet> Snippets { get; set; } = new();

    /// <summary>
    /// 别名替换的分组名（用户手动建的）。
    ///
    /// 以前分组是从片段身上"推"出来的 —— 空分组根本存不下来，
    /// 于是用户没法"先建个组再往里放"。现在单独存一份，
    /// 与片段里实际用到的组名取并集显示。
    /// </summary>
    public List<string> SnippetGroups { get; set; } = new();

    /// <summary>
    /// 双击动作：任意键连按两次触发一个动作。默认空，不影响任何现有行为。
    /// </summary>
    public List<KeyGesture> KeyGestures { get; set; } = new();

    /// <summary>
    /// 按应用自动启用层：前台窗口的进程命中规则时，把对应层当作叠加层。
    /// 默认空，不影响任何现有行为。
    /// </summary>
    public List<AppLayerRule> AppLayers { get; set; } = new();

    /// <summary>
    /// 基础改键表：不按住触发键也生效的全局重映射。
    ///
    /// 典型用途：Caps→Esc、Ctrl↔Alt、把笔记本上闲置的键变成 Home/End。
    /// 只在**基础层**生效；层内绑定优先，键盘钩子也不会因此改变触发键判定。
    /// </summary>
    public List<KeyRemap> BaseRemaps { get; set; } = new();

    /// <summary>
    /// 「全部生效」：勾上之后，所有插在电脑上的键盘/鼠标都受本软件影响，
    /// 逐台设备的勾选**不再起作用**。
    ///
    /// 默认 true —— 老用户升级上来看到的行为必须和以前完全一致，
    /// 不能因为新增了"设备范围"就悄悄把某台设备关掉。
    /// </summary>
    public bool DeviceScopeAll { get; set; } = true;

    /// <summary>
    /// 设备备注：设备 key（VID/PID 或完整路径）→ 用户自己起的名字。
    ///
    /// 为什么要这个：同一批键盘在系统里的设备名经常一模一样 —— 五条
    /// 「HID Keyboard Device」，五条「USB Input Device」。用户在设置里根本分不清
    /// 哪一条是手里这只，于是"按设备分别设置"就变成了盲选。备注之后，
    /// 界面上所有出现设备名的地方（胶囊、清单、改键说明、托盘）都用这个名字。
    ///
    /// 键一定先经 <see cref="Input.InputDevices.Normalize"/> 归一，
    /// 否则换个小写的设备路径就查不到备注了。
    /// </summary>
    public Dictionary<string, string> DeviceAliases { get; set; } = new();

    /// <summary>
    /// 一次性迁移标记：基础层的 Caps+Q / Caps+R 补齐成 Qbar / Delete 已经做过。
    ///
    /// 默认方案里这两条本来就是 qbar / delete，但老配置里可能被写成空绑定
    /// （doNothing）或被删掉过 —— 升级上来的用户看到的就是"Caps+Q 按了没反应"。
    /// 补一次就置位，之后用户自己改成什么都不再动。
    /// </summary>
    public bool BaseLayerDefaultsFixed { get; set; }

    /// <summary>主题：system / light / dark。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// 界面字体：系统里**已安装字体**的族名（例如 <c>Microsoft YaHei UI</c>、<c>思源黑体 CN</c>）。
    ///
    /// 空字符串 = 出厂字体（<c>FontManager.BuiltInUi</c>：微软雅黑 + 西文兜底）。
    /// 这个值一改，整个软件的界面文字立刻跟着变 —— 菜单、按钮、各页面、附加功能窗口、
    /// 浮层与提示条都算，因为它们共用 <c>Font.Ui</c> 这一个资源键（见 FontManager 的说明）。
    ///
    /// 存的是**字体族名而不是索引**：换机后同名就还原，没装则整串退回出厂字体，
    /// 不会因为"这台机器少了个字体"变成一片空白。
    /// </summary>
    public string UiFont { get; set; } = "";

    /// <summary>
    /// 等宽区域（Lua 脚本编辑器、公式输入、录制计时）是否也跟着 <see cref="UiFont"/> 变。
    ///
    /// 默认 **true**：用户说"只要是这个软件的字体都要跟着改"，那就不该留一半是原来的。
    /// 留这个开关是因为等宽字体换成比例字体后，脚本缩进和计时的数字会跳宽 ——
    /// 想保住等宽观感的人把它关掉即可（关掉就回到 Cascadia Mono / Consolas）。
    /// </summary>
    public bool UiFontMono { get; set; } = true;

    /// <summary>
    /// 「按键映射」页的键盘大小档位（small / medium / large / xlarge）。
    ///
    /// 这个值同时决定两件事，别只当它是"显示比例"看：
    ///   · 每次打开按键映射页，键盘直接按这档渲染；
    ///   · 这个页面的**最小窗口尺寸** = 装下这档键盘所需的尺寸，
    ///     所以窗口不会被缩到把键帽压糊。
    /// </summary>
    public string KeyboardSize { get; set; } = KeyboardSizePreset.DefaultId;

    /// <summary>界面语言：zh-CN / en-US。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>首次运行引导是否已完成。</summary>
    public bool OnboardingDone { get; set; }

    /// <summary>启动时是否打开主窗口。</summary>
    public bool OpenWindowOnStartup { get; set; }

    /// <summary>主窗口关闭时最小化到托盘而不是退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>按住 Caps 组合键时，动作是否可重复触发（打字流场景更顺手）。</summary>
    public bool RepeatWhenHeld { get; set; }

    /// <summary>双击 CapsLock 时触发动作（默认：打开 Qbar）。</summary>
    public bool DoubleTapCaps { get; set; }

    public string DoubleTapCapsAction { get; set; } = "qbar";

    /// <summary>鼠标移动动作的基础步长。</summary>
    public int MouseMoveStep { get; set; } = 10;

    /// <summary>自动更新：勾选后每次启动都会静默检查，有新版本再提示。</summary>
    public bool AutoUpdate { get; set; }

    /// <summary>上次检查更新的时间（避免启动时频繁请求）。</summary>
    public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue;

    /// <summary>用户点过“跳过这个版本”的版本号。</summary>
    public string SkippedVersion { get; set; } = "";

    /// <summary>
    /// GitHub 个人访问令牌（PAT），用来把更新检查的接口配额从 60 次/小时
    /// 提到 5000 次/小时。
    ///
    /// 为什么需要它：匿名请求 GitHub API 是按 **IP** 限流的 —— 60 次/小时。
    /// 公司/校园网、家里多台机器、或开了代理的人，经常在一天里就把这点配额用光，
    /// 于是"检查更新"永远报"访问频率已达上限"。带上令牌就按**账号**限流，
    /// 5000 次/小时，正常使用根本碰不到。
    ///
    /// 只读公开仓库**不需要勾选任何 scope**（classic token 留空即可，
    /// fine-grained 只需要 Public repositories 的 read）。令牌只存在本机设置里：
    /// **明文导出会把它摘掉**（见 ExportImport.NeverExport），加密备份才会带上。
    /// </summary>
    public string GitHubToken { get; set; } = "";
}

/// <summary>
/// WebDAV 同步与备份设置。
///
/// 地址、用户名、密码都只存在本机设置文件里，不会被导出到明文配置
/// （除非用户在导出时显式勾选"同步与备份设置"这个隐私组）。
/// </summary>
public sealed class SyncOptions
{
    /// <summary>WebDAV 服务器地址（目录形式）。</summary>
    public string WebDavUrl { get; set; } = "";
    public string WebDavUser { get; set; } = "";
    public string WebDavPass { get; set; } = "";

    /// <summary>启动软件时自动同步一次。</summary>
    public bool SyncOnStart { get; set; }

    /// <summary>关闭软件时自动同步一次。</summary>
    public bool SyncOnExit { get; set; }

    /// <summary>
    /// 备份密码。
    ///
    /// 它有两个作用，别当成一回事：
    ///   * 给**备份文件**加密（本地 + 云端都算）；
    ///   * 给**本地用户数据**加密（settings.json 整份变密文）。
    /// 所以设了它之后，磁盘上不会再有明文用户数据；
    /// 备份也一律是密文，没有"没密码就明文"的退路。
    /// </summary>
    public string BackupPassword { get; set; } = "";

    /// <summary>本地 / 云端各保留最近几份备份。默认 10。</summary>
    public int KeepLocalBackups { get; set; } = 10;

    /// <summary>自动清掉多少天之前的备份（0 = 不按天数清）。默认 7。</summary>
    public int CleanupDays { get; set; } = 7;

    /// <summary>按间隔自动备份（本地必写，配了网盘就一起上传）。</summary>
    public bool AutoBackup { get; set; }

    /// <summary>自动备份间隔（分钟）。</summary>
    public int AutoBackupMinutes { get; set; } = 60;

    /// <summary>上次同步时间。</summary>
    public DateTime LastSyncAt { get; set; } = DateTime.MinValue;

    /// <summary>上次同步结果的一句话说明。</summary>
    public string LastSyncInfo { get; set; } = "";
}

/// <summary>
/// 启动密码（有人叫它"应用密码"）。
///
/// ⚠ 与 <see cref="SyncOptions.BackupPassword"/>（备份密码）**完全不是一回事**，
/// 用户点名强调过：备份密码管"备份文件和本地数据加密"，设在「同步与备份」页；
/// 启动密码只管"谁能打开这个软件"，设在「设置」页。两套字段、两套校验、
/// 互不影响 —— 关掉启动密码，备份加密照旧；改备份密码，启动密码也不动。
///
/// 这里只存**镜像**（盐 + PBKDF2 派生值 + 迭代次数），随 settings.json 走；
/// 启动时的校验真相在数据目录的 startup.json（见 <see cref="StartupPassword"/>）——
/// 因为 settings.json 可能被备份密码加密，解锁失败时读不出来，
/// 那时要是只认 settings.json，密码门会被悄悄跳过。
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>是否开着启动密码（与 startup.json 的存在状态保持一致）。</summary>
    public bool StartupPasswordEnabled { get; set; }

    /// <summary>PBKDF2 盐（base64）。空 = 没设过。</summary>
    public string StartupPasswordSalt { get; set; } = "";

    /// <summary>PBKDF2 派生值（base64）。不存明文，也不可逆推密码。</summary>
    public string StartupPasswordHash { get; set; } = "";

    /// <summary>设密码时的 PBKDF2 迭代次数（调参后老密码照样能验）。</summary>
    public int StartupPasswordIterations { get; set; }

    /// <summary>设置里有没有一份可用的密码记录（不等于门开着 —— 门看 startup.json）。</summary>
    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(StartupPasswordSalt)
                            && !string.IsNullOrEmpty(StartupPasswordHash)
                            && StartupPasswordIterations > 0;
}

/// <summary>
/// 触发键（Trigger）配置。
///
/// 参照键盘驱动的做法：用户按下一个"触发键"，
/// 短按（未超阈值）保留原键盘功能，长按超过阈值后
/// 临时切换到该触发键绑定的层，层内按键执行自定义动作。
///
/// 默认只有一个触发键 = CapsLock，绑第 1 层，
/// 这样老用户的行为完全不变。
/// </summary>
public sealed class TriggerKey
{
    /// <summary>触发的物理键名（KeyName.NameOf 形式），例如 "capslock"。</summary>
    public string Key { get; set; } = "";

    /// <summary>该触发键绑定到哪一层（1-based；对应 <see cref="LayerOptions.Id"/>）。</summary>
    public int LayerId { get; set; } = 1;

    /// <summary>
    /// 长按阈值（毫秒）。按住超过这个时间才进入层模式；
    /// 在此之前松开则视为"短按"，走 <see cref="TapAction"/>。
    /// </summary>
    public int HoldMs { get; set; } = 200;

    /// <summary>短按时要执行的动作（默认切换大小写；可设为 Esc / 无操作等）。</summary>
    public string TapAction { get; set; } = "toggleCapsLock";

    /// <summary>
    /// 按住触发键期间又按了别的键时，**立即进入层**而不等长按阈值。
    ///
    /// 开（默认）：Caps 按住后马上按 Q 就能触发组合键，手感与旧版一致。
    /// 关：必须按满 <see cref="HoldMs"/> 才进层，组合键要"等一下"。
    /// </summary>
    public bool HoldOnOtherKey { get; set; } = true;

    /// <summary>双击触发键时执行的动作（空 = 关闭双击功能）。</summary>
    public string DoubleTapAction { get; set; } = "";

    /// <summary>是否启用这个触发键。</summary>
    public bool Enabled { get; set; } = true;

    public string Display => string.IsNullOrEmpty(Key) ? "(未设置)" : Key;

    // ---------------- 供列表直接绑定的只读展示属性 ----------------
    //
    // 触发键列表那一行要显示「键名 / 绑定到哪一层 / 阈值 / 启停」。
    // 这些属性直接给界面绑，省得界面自己拼字符串
    // （之前 XAML 绑了 KeyLabel/Summary/Detail 但类上没有，整行是空的）。

    /// <summary>键帽上显示的名字。</summary>
    public string KeyLabel => string.IsNullOrEmpty(Key)
        ? "?"
        : Input.KeyName.Display(Key);

    /// <summary>主标题：这一项绑到第几层。</summary>
    public string Summary => LayerId <= 0 ? Loc.T("未绑定层") : Loc.F("第 {0} 层", LayerId);

    /// <summary>副标题：阈值 + 短按动作，一眼看清这个触发键怎么用。</summary>
    public string Detail
    {
        get
        {
            /* 短按动作的名字从**动作表**里取，不再写死 switch。
             *
             * 之前只映射了 toggleCapsLock / esc / qbar / doNothing 四个，
             * 用户把短按改成「保留按键原功能」（keepKey）后，
             * 列表上显示的是动作 ID 甚至"无"，看起来像设置没生效。 */
            string tap;
            if (TapAction == "doNothing")
                tap = Loc.T("无");
            else if (string.IsNullOrEmpty(TapAction))
                // 空值在引擎里的语义就是"补发原键"，等价于 keepKey
                tap = Loc.T("保留原功能");
            else
                tap = Actions.ActionCatalog.TitleOf(TapAction) ?? TapAction;

            string hold = HoldOnOtherKey ? Loc.T("一按别的键就进层") : Loc.F("长按 {0}ms 进层", HoldMs);
            return Loc.F("{0}　·　短按：{1}", hold, tap);
        }
    }

    /// <summary>列表里的位置（供"启停"复选框回写用）。</summary>
    public int Index { get; set; }
}

/// <summary>
/// 键盘层（Layer）。
///
/// 一个层就是一套独立的"触发键 + 按键 → 动作/宏"映射表。
/// 层与层之间互不干扰；同一层内再支持 Alt / Win 修饰键，
/// 组合键 ID 形如 <c>caps_q</c> / <c>caps_lalt_q</c>。
/// </summary>
public sealed class LayerOptions
{
    /// <summary>层 ID，从 1 开始，对应触发键的 <see cref="TriggerKey.LayerId"/>。</summary>
    public int Id { get; set; } = 1;

    /// <summary>层名称，界面上显示用，例如「基础层」「Layer 2」。</summary>
    public string Name { get; set; } = "";

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 这一页的鼠标键改动是否生效。
    ///
    /// 关掉后，本层（含子层）的 mouseLeft/Right/Middle/X1/X2 绑定全部放行，
    /// 不会执行动作，也不会被层吞掉。键盘绑定不受影响。
    /// 默认开，保证老配置升级后行为不变。
    /// </summary>
    public bool MouseKeysEnabled { get; set; } = true;

    /// <summary>该层的按键绑定列表。</summary>
    public List<KeyBinding> Keys { get; set; } = new();

    /// <summary>
    /// 该层的**层内修饰键 / 子层**定义。
    ///
    /// 每个层最多 10 个子层：进入本层后再按住某个键，
    /// 就能用到另一套独立绑定。
    ///
    /// 子层键可以是**任意物理键**（Ctrl、Shift、Tab、CapsLock…），
    /// 也可以给每个子层起**自定义名字**（键帽和图例上显示这个名字）。
    ///
    /// 为空时回退到默认 4 个（Ctrl / Shift / Alt / Win），
    /// 保证老配置行为不变。
    /// </summary>
    public List<SubLayerDef> SubLayers { get; set; } = new();

    /// <summary>
    /// 旧版字段：只存修饰键位标记。
    ///
    /// 保留用于向后兼容 —— 老配置文件里是 <c>[1, 2, 4, 8]</c> 这样的整数数组。
    /// 读进来后由 <see cref="Normalize"/> 转成 <see cref="SubLayers"/>。
    /// 写入时若 <see cref="SubLayers"/> 为空则继续用这个字段，避免版本回退丢配置。
    /// </summary>
    public List<int> SubLayerModifiers { get; set; } = new();

    /// <summary>每个层允许的最大子层数。</summary>
    public const int MaxSubLayers = 10;

    /// <summary>默认的子层定义（与旧版行为一致：Ctrl / Shift / Alt / Win）。</summary>
    public static List<SubLayerDef> DefaultSubLayers() => new()
    {
        new SubLayerDef { Key = "ctrl", Name = "Ctrl" },
        new SubLayerDef { Key = "shift", Name = "Shift" },
        new SubLayerDef { Key = "alt", Name = "Alt" },
        new SubLayerDef { Key = "win", Name = "Win" },
    };

    /// <summary>默认的层内修饰键位标记（旧接口，保留兼容）。</summary>
    public static List<int> DefaultSubLayerModifiers() => new()
    {
        (int)KeyModifiers.Ctrl,
        (int)KeyModifiers.Shift,
        (int)KeyModifiers.Alt,
        (int)KeyModifiers.Win,
    };

    /// <summary>
    /// 实际生效的子层列表。
    ///
    /// 优先级：SubLayers（新） > SubLayerModifiers（旧） > 默认四个。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<SubLayerDef> EffectiveSubLayers
    {
        get
        {
            if (SubLayers.Count > 0) return SubLayers.Take(MaxSubLayers).ToList();

            // 老配置：把位标记还原成子层定义
            if (SubLayerModifiers.Count > 0)
            {
                return SubLayerModifiers
                    .Take(MaxSubLayers)
                    .Select(m => SubLayerDef.FromModifierBits(m))
                    .ToList();
            }

            return DefaultSubLayers();
        }
    }
}

/// <summary>
/// 一个子层（层内修饰键）的定义。
///
/// <see cref="Key"/> 是任意键名（ctrl / shift / tab / capslock / f13…），
/// <see cref="Name"/> 是显示名（留空则自动用键名）。
/// </summary>
public sealed class SubLayerDef
{
    /// <summary>子层触发键的键名（与 KeyName 的命名一致）。</summary>
    public string Key { get; set; } = "";

    /// <summary>显示名。留空时自动取键名。</summary>
    public string? Name { get; set; }

    /// <summary>该子层的按键绑定（combo 里带修饰前缀）。</summary>
    public List<KeyBinding> Keys { get; set; } = new();

    /// <summary>界面上显示的文字。</summary>
    [JsonIgnore]
    public string Display =>
        !string.IsNullOrWhiteSpace(Name) ? Name!.Trim() : KeyName.Display(Key);

    /// <summary>把位标记转成子层定义（用于迁移旧配置）。</summary>
    public static SubLayerDef FromModifierBits(int bits)
    {
        var mods = (KeyModifiers)bits;
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("alt");
        if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");

        string key = parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join("_", parts),      // 复合位标记暂时存成 ctrl_shift
        };

        return new SubLayerDef { Key = key };
    }
}

/// <summary>
/// 宏动作（Macro）序列中的一步。
///
/// 参照键盘驱动的宏方案，一个宏就是一串"带时间的输入事件"：
/// 按下键 / 抬起键 / 等待固定毫秒 / 等待随机毫秒区间。
/// 文本输入则拆成逐字符的"按下+抬起"序列，
/// 这样才能在游戏和不响应粘贴的程序里生效。
/// </summary>
public sealed class MacroStep
{
    /// <summary>
    /// 步骤类型：keyDown / keyUp / delay / delayRandom / text / setVar，
    /// 以及流程控制 if / else / endIf / loop / endLoop / waitFor / retry / endRetry，
    /// 和执行内置动作的 action。
    ///
    /// 流程控制是**成对标记**而不是嵌套结构：宏仍然是一条平铺的列表，
    /// 靠 <c>Core.Macro.MacroFlow</c> 在播放前算出配对关系。
    /// 这样老版本存的宏（没有这些标记）可以原样播放，JSON 也不会变成树。
    /// </summary>
    public string Kind { get; set; } = "delay";

    /// <summary>Kind 为 keyDown/keyUp 时的键名（KeyName 形式，如 "a"、"ctrl"）。</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// Kind 为 text 时要输入的文本（整体一步，播放时逐字符发送并自动加微小延迟）。
    /// </summary>
    public string Text { get; set; } = "";

    /// <summary>Kind 为 delay 时的固定等待毫秒。</summary>
    public int DelayMs { get; set; }

    /// <summary>Kind 为 delayRandom 时的随机区间下界。</summary>
    public int MinMs { get; set; }

    /// <summary>Kind 为 delayRandom 时的随机区间上界。</summary>
    public int MaxMs { get; set; }

    /// <summary>
    /// Kind 为 if 时的条件类型：app（前台进程）/ title（窗口标题）/
    /// clipboard（剪贴板包含）/ clipboardEmpty（剪贴板为空）。
    /// </summary>
    public string Cond { get; set; } = "app";

    /// <summary>条件的比较值（app / title / clipboard 用；clipboardEmpty 忽略）。</summary>
    public string CondValue { get; set; } = "";

    /// <summary>Kind 为 if 时是否取反。</summary>
    public bool Not { get; set; }

    /// <summary>Kind 为 loop 时的循环次数；0 或负数 = 一直循环（由取消或步数上限终止）。</summary>
    public int Count { get; set; } = 3;

    /// <summary>
    /// Kind 为 waitFor 时的最长等待毫秒。
    /// 超过就判定这一步失败 —— 失败会交给外层「重试」块，没有重试块就整段中止。
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>Kind 为 action 时要执行的动作 id（见 ActionCatalog）。</summary>
    public string ActionId { get; set; } = "";

    /// <summary>Kind 为 action 时传给动作的参数（多数动作不需要）。</summary>
    public string ActionArg { get; set; } = "";

    /// <summary>
    /// 变量名。
    ///
    /// 两种用法：
    ///   · Kind 为 setVar 时 —— 要写入的变量名；
    ///   · Kind 为 loop 时 —— 把"当前是第几轮"写进这个变量（留空则用 i）。
    ///
    /// 循环计数走这条而不是另开一个步骤类型：用户想要的本来就是
    /// "循环 10 次，每次用当前序号"，多一步只会多一处忘配。
    /// </summary>
    public string VarName { get; set; } = "";

    /// <summary>
    /// Kind 为 setVar 时要写入的值。可以含 `{变量}` 占位符（写入前会展开）。
    /// </summary>
    public string VarValue { get; set; } = "";

    public MacroStep Clone() => new()
    {
        Kind = Kind, Key = Key, Text = Text,
        DelayMs = DelayMs, MinMs = MinMs, MaxMs = MaxMs,
        Cond = Cond, CondValue = CondValue, Not = Not, Count = Count,
        TimeoutMs = TimeoutMs, ActionId = ActionId, ActionArg = ActionArg,
        VarName = VarName, VarValue = VarValue,
    };
}

/// <summary>
/// 一个可绑定的宏。
///
/// 存在设置文件里，可以在界面上录制、编辑、绑定到任意层的任意按键。
/// </summary>
public sealed class Macro
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "新宏";
    public List<MacroStep> Steps { get; set; } = new();

    /// <summary>循环播放次数（1 = 播放一次；0 = 一直循环直到用户中断）。</summary>
    public int Repeat { get; set; } = 1;

    /// <summary>备注。</summary>
    public string Comment { get; set; } = "";

    public Macro Clone() => new()
    {
        Id = Id, Name = Name, Comment = Comment, Repeat = Repeat,
        Steps = Steps.Select(s => s.Clone()).ToList(),
    };

    /// <summary>
    /// 估算总时长（用于界面显示）。
    ///
    /// 循环体按次数放大：不算这一项的话，"循环 50 次 + 每次等 200ms"会显示成几百毫秒，
    /// 与真实感受差两个数量级，用户会以为循环没生效。
    /// 无限循环按 1 次算 —— 显示成"无限"没有意义，也没法参与加法。
    /// </summary>
    public int EstimatedMs
    {
        get
        {
            int total = 0;
            var stack = new Stack<(int Outer, int Count)>();

            foreach (var s in Steps)
            {
                switch (s.Kind)
                {
                    case "loop":
                    {
                        int count = s.Count <= 0 ? 1 : Math.Min(s.Count, 1000);
                        stack.Push((total, count));
                        total = 0;
                        break;
                    }
                    case "endLoop":
                        if (stack.Count > 0)
                        {
                            var (outer, count) = stack.Pop();
                            total = outer + total * count;
                        }
                        break;

                    /* 流程标记本身不耗时，别当成一次按键。
                     * waitFor 的时长取决于条件什么时候成立，无法预估，按 0 算。 */
                    case "if":
                    case "else":
                    case "endIf":
                    case "retry":
                    case "endRetry":
                    case "waitFor":
                        break;

                    case "delay": total += s.DelayMs; break;
                    case "delayRandom": total += (s.MinMs + s.MaxMs) / 2; break;
                    case "text": total += s.Text.Length * 12; break;
                    default: total += 8; break;   // 一次按键的按下+抬起
                }
            }

            return total;
        }
    }

    /// <summary>
    /// 列表里显示的名字：空名字给个占位。
    ///
    /// 真实用户配置里就存在 Name 为空的宏（早期版本"新建"后没填名字），
    /// 直接绑 Name 会在列表里出现一行空白，动作下拉里变成「播放宏：」。
    /// </summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "（未命名宏）" : Name;

    /// <summary>列表副标题：步数与循环次数。
    ///
    /// 界面里宏列表绑定了 Summary / DurationText，但这两个属性曾是**不存在的** ——
    /// 结果整个宏列表只有名字和图标，副标题与时长永远空白。
    /// </summary>
    public string Summary
    {
        get
        {
            string repeat = Repeat <= 0 ? Loc.T("循环播放") : Repeat == 1 ? Loc.T("播放一次") : Loc.F("播放 {0} 次", Repeat);
            return Loc.F("{0} 步　·　{1}", Steps.Count, repeat);
        }
    }

    /// <summary>列表右侧的时长文字（估算）。</summary>
    public string DurationText
    {
        get
        {
            int ms = EstimatedMs;
            return ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:0.#}s";
        }
    }
}

public sealed class QbarOptions
{
    public string PanelColor { get; set; } = "auto";   // auto = 跟随主题
    public string PanelOpacity { get; set; } = "0.96";
    public double TextFontSize { get; set; } = 20;
    public int ListCount { get; set; } = 6;
    public double LineHeight { get; set; } = 38;
    public string AccentColor { get; set; } = "#4C8DFF";
    public double Width { get; set; } = 760;
    public bool ShowIcons { get; set; } = true;

    /// <summary>
    /// 用户拖动过 Qbar 面板，下次唤出就落回那个位置。
    ///
    /// false = 走默认位置（工作区上方 22% 居中）。只在真的拖动过之后才置 true：
    /// 默认摆位要跟着前台窗口所在显示器走，一旦固定成"上次那个坐标"，
    /// 换块屏唤出反而会跑到另一块屏上去。
    /// </summary>
    public bool CustomPosition { get; set; }

    /// <summary>
    /// 面板左上角在**工作区**里的比例（0..1，0 = 贴左/贴上，1 = 贴右/贴下）。
    ///
    /// 存比例而不是像素：换分辨率、改缩放、插拔副屏之后像素会落到屏幕外，
    /// 比例至少还能落在同一块屏的相对位置上。
    /// 分母是工作区本身而不是"工作区减面板"：Qbar 高度随结果条数变，
    /// 拿可用空间当分母会让同一个比例在不同高度下算出不同的位置。
    /// 0.5/0.3 ≈ 默认的"上方居中"一带。
    /// </summary>
    public double PositionX { get; set; } = 0.5;

    /// <inheritdoc cref="PositionX"/>
    public double PositionY { get; set; } = 0.3;

    /// <summary>
    /// 空面板最上面先列几条"最近打开的项目"。
    ///
    /// 0 = 不显示。只在**直接唤出** Qbar（没有关键字、没有指定搜索引擎）时出现；
    /// 带引擎标签或已经输入关键字时列表是搜索/候选结果，不掺最近项。
    /// </summary>
    public int RecentCount { get; set; } = 5;

    /// <summary>
    /// 面板一出来就先切到英文输入。
    ///
    /// 引擎关键字（bd / gg / zhihu）只能用英文字母打，而中文输入法下敲下去是拼音，
    /// 面板里根本拼不出 "bd"。勾上之后打开面板会替你按一下 Shift 切到英文。
    /// </summary>
    public bool SwitchToEnglishOnOpen { get; set; }

    /// <summary>
    /// 引擎标签定下来之后切回中文。
    ///
    /// 关键字认出来之后要打的通常是搜索内容（中文），不切回来还得手动按一次 Shift。
    /// 和 <see cref="SwitchToEnglishOnOpen"/> 配对使用最顺手，但两者可以单独开。
    /// </summary>
    public bool SwitchBackOnEngine { get; set; }

    /// <summary>
    /// 是否在 Qbar 里显示搜索联想。
    ///
    /// 默认开：联想只在“已选定搜索引擎 + 有关键词”时出现，平时完全不占地方；
    /// 关掉之后也不会影响任何现有搜索路径，只是不再发联想请求。
    /// </summary>
    public bool SuggestEnabled { get; set; } = true;

    /// <summary>
    /// 联想词来自哪家：auto / baidu / google / bing。
    ///
    /// auto = 跟随当前搜索源（看标签里 URL 的域名），这样用户选“百度”搜索时
    /// 联想也自然是百度的词，不用再配一次。存字符串而不是枚举，是为了
    /// 以后加引擎时老配置文件仍然能读（未知值一律按 auto 处理）。
    /// </summary>
    public string SuggestEngine { get; set; } = "auto";

    /// <summary>
    /// 最多显示几条联想（1..10）。
    ///
    /// 面板高度是 SizeToContent，联想多了会把结果列表往下挤；
    /// 默认 6 条和结果列表的默认条数一致，视觉上比较稳。
    /// </summary>
    public int SuggestMax { get; set; } = 6;

    /// <summary>
    /// 单次联想请求的超时（毫秒，200..5000）。
    ///
    /// 联想是“打字过程中的辅助”，超过 800ms 用户基本已经打完下一个词了，
    /// 再回来也没意义；所以默认值取得比一般网络请求短。
    /// </summary>
    public int SuggestTimeoutMs { get; set; } = 800;

    /// <summary>
    /// 联想结果的内存缓存时长（分钟，0..1440）。
    ///
    /// 同一个词删一个字再打回来很常见，缓存能省掉重复请求；
    /// 0 = 不缓存，每次都发请求（调试接口时用）。
    /// </summary>
    public int SuggestCacheMinutes { get; set; } = 10;

    /// <summary>
    /// 固定在 Qbar 空面板/搜索里的文件、文件夹、网址或程序。
    /// 和搜索/程序/网址三张表分开：这里不参与“关键字+空格”的标签解析，
    /// 只是把常用目标直接摆在候选里，输入名称就能命中。
    /// </summary>
    public List<QEntry> Pinned { get; set; } = new();

    /// <summary>
    /// 文件搜索语义层（过滤器 / 优先级 / 命令 / 拼音 / 调试面板）的全局开关。
    /// 挂在 Qbar 下面而不是 AppSettings 顶层：它只影响 Qbar 面板里的文件搜索。
    /// </summary>
    public QbarSearchOptions Search { get; set; } = new();

    /// <summary>过滤器表（doc:/pic:/... 内置项也在这里落一份副本，用户改了同名的会覆盖内置）。</summary>
    public List<QbarFilter> Filters { get; set; } = new();

    /// <summary>优先级规则（不含内置规则包，内置包由代码生成）。</summary>
    public List<QbarPriorityRule> Priority { get; set; } = new();

    /// <summary>用户命令表（内置命令由代码生成，这里只存用户新增/覆盖的）。</summary>
    public List<QbarCommand> Commands { get; set; } = new();

    /// <summary>
    /// 正文（全文）索引的参数。与文件名索引分开：正文索引有自己的目录、
    /// 自己的开关和重建按钮，关掉它不影响按文件名搜索。
    /// </summary>
    public QbarContentOptions Content { get; set; } = new();

    /// <summary>
    /// 条目表（搜索指令 / 启动程序 / 常用网址 / 固定项）四列的宽度，按列序：[关键字, 内容, 备注, 附加菜单]。
    ///
    /// 0 或缺失 = 用默认值（关键字 96 / 内容 自适应 / 备注 140 / 附加菜单 84）。
    /// 「内容」那列默认是星号宽度（跟着面板伸缩），用户手动拖过之后才会存成像素值 ——
    /// 存 0 就表示"这列还是自适应"，恢复时不要把它钉成固定宽度，否则窗口一放大右边就空一截。
    ///
    /// 为什么要记：用户把「备注」拉宽过一次，下次进这一页又变回原样，等于每次都在跟它对打。
    /// </summary>
    public List<double> EntryColumnWidths { get; set; } = new();
}

/// <summary>
/// 正文索引（全文搜索）的参数。
///
/// 为什么单独一类而不是塞进 <see cref="QbarSearchOptions"/>：那一类是"查询期"的
/// 参数（窗口大小、去抖、模糊），改一条立刻生效；这里的每一条都影响**建索引**
/// （抽哪些、抽多大、放哪），改完要重建才生效，设置页也要如实提示这一点。
/// </summary>
public sealed class QbarContentOptions
{
    /// <summary>总开关。关掉后 Qbar 只按文件名/路径搜索（老行为）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>要抽正文的扩展名，分号/逗号分隔，不带点。</summary>
    public string Extensions { get; set; } = "pdf;docx;xlsx;pptx;txt;md;html;htm;csv;json;log";

    /// <summary>单文件上限（字节）。超过就跳过 —— 正文索引的体积与这个值线性相关。</summary>
    public int MaxFileBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>PDF 最多读多少页。</summary>
    public int MaxPdfPages { get; set; } = 50;

    /// <summary>最多索引多少个文件（防止第一次打开就把几十万文件全抽一遍）。</summary>
    public int MaxFiles { get; set; } = 200_000;

    /// <summary>是否连隐藏/系统文件一起抽。默认否。</summary>
    public bool IncludeHidden { get; set; }

    /// <summary>正文索引目录。空 = 数据目录下的 file-content-tantivy。</summary>
    public string IndexDir { get; set; } = "";

    /// <summary>
    /// 生效范围。空 = 用户目录（桌面 / 文档 / 下载 / 图片 / 视频）。
    /// 可以写盘根（<c>D:\</c>）表示整块盘，也可以写具体文件夹；
    /// 改完要点「重建正文索引」才生效（旧索引里已有的条目不会自动消失）。
    /// </summary>
    public List<string> Roots { get; set; } = new();

    /// <summary>
    /// 普通关键词（不带 content:）是否也去正文里找。
    /// 关掉之后只有显式写 content:词 才查正文。
    /// </summary>
    public bool MergeIntoKeywordSearch { get; set; } = true;
}

/// <summary>
/// 文件对话框搜索栏（见 FILE-DIALOG-BAR.md）。
///
/// 默认值全部写在属性初始值上：老配置反序列化时缺这些字段就自动取默认，
/// 不需要再在 SettingsStore 里写迁移。
/// </summary>
public sealed class FileDialogBarOptions
{
    /// <summary>
    /// 默认值版本：0 = 1.3.4 及以前（那时 ShowOnAppear 默认 false）。
    /// 1.3.5 起默认改成 true（用户要的就是打开另存为就有一条搜索栏），
    /// 由 SettingsStore.Normalize 对老配置做一次迁移；迁移后用户再关掉就不再改。
    /// </summary>
    public int DefaultsVersion { get; set; }

    public const int DefaultsVersionCurrent = 2;

    /// <summary>总开关。false = 不装钩子、不建窗口。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>对话框一出现就显示（默认开：用户要的就是"打开另存为就有一条搜索栏"）。</summary>
    public bool ShowOnAppear { get; set; } = true;

    /// <summary>
    /// 收藏的目录（QuickSwitch 的 ★）。这是普通设置项，跟 Favorites 一起走
    /// settings.json 的保存/备份/同步，不另建一套存储。
    /// </summary>
    public List<FavoriteDirectory> Favorites { get; set; } = new();

    /// <summary>按热键显示（默认开）。</summary>
    public bool ShowOnHotkey { get; set; } = true;

    /// <summary>显示/隐藏浮层的全局热键。</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+F";

    /// <summary>
    /// 按类型开关（键见 <c>DialogTypeKeys</c>）。
    ///
    /// 字典里没有的键按「开」处理 —— 新增类型时老配置不必迁移。
    /// </summary>
    public Dictionary<string, bool> Types { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [InputPlus.Core.FileDialog.DialogTypeKeys.FileDialog] = true,
        [InputPlus.Core.FileDialog.DialogTypeKeys.FolderDialog] = true,
        [InputPlus.Core.FileDialog.DialogTypeKeys.ExplorerWindow] = false,
    };

    /// <summary>按进程名禁用（小写、带 .exe）。</summary>
    public List<string> DisabledProcesses { get; set; } = new();

    /// <summary>Vista+ 对话框优先走 UIA ValuePattern（失败自动退化到键盘+剪贴板）。</summary>
    public bool UiaBackfill { get; set; } = true;

    /// <summary>结果上限（本地重排后展示）。</summary>
    public int MaxResults { get; set; } = 200;

    /// <summary>
    /// 浮层一次呈现几条结果（1..9，默认 6）。
    ///
    /// 它同时是快捷键的上限：行尾的 Ctrl+1..N 就是按位置发的，所以数字键最多到 9。
    /// 条数再多只会把脚注和下面的地址面包屑挤掉，而且超过 9 也没有键可按。
    /// </summary>
    public int RowLimit { get; set; } = 6;

    /// <summary>浮层输入行高度（DIP）。</summary>
    public double BarHeight { get; set; } = 44;

    /// <summary>SHOW 事件丢失时的兜底轮询间隔（毫秒，只枚举顶层窗口）。</summary>
    public int PollFallbackMs { get; set; } = 300;

    /// <summary>
    /// 跟随度量（默认关）：每秒往日志写一行 p50/p99 延迟与像素误差。
    /// 排查"拖动时浮条跟不跟得上"时打开，平时关着不产生任何日志。
    /// </summary>
    public bool FollowMetrics { get; set; }
}

/// <summary>一个收藏目录（QuickSwitch ★ 的数据项）。</summary>
public sealed class FavoriteDirectory
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// 内置搜索引擎表的版本号。
    ///
    /// 每次发布新增内置引擎时把这个数字加一，启动时会自动把**缺失的**内置引擎
    /// 补进用户的设置里（已存在或用户自己改过的条目不覆盖）。
    /// 否则老用户永远看不到新引擎 —— 因为设置文件已经存在，不会再走默认值分支。
    /// </summary>
    public int BuiltinEnginesVersion { get; set; }

    /// <summary>内置常用网址表的版本号；新增网址时 +1，老用户启动时自动补齐。</summary>
    public int BuiltinWebVersion { get; set; }

    /// <summary>内置启动程序表的版本号；新增程序时 +1，老用户启动时自动补齐。</summary>
    public int BuiltinRunVersion { get; set; }

    /// <summary>
    /// 基础层（第 1 层）默认绑定的版本号：0 = 还是 Keyboard enhance 的老默认表。
    ///
    /// 和上面几个 Builtin*Version 是同一个套路，但这里升级的是**绑定**，
    /// 所以迁移只在"当前值仍等于老默认值"时才覆盖 —— 用户自己改过的键一律不动。
    /// 1 = 已经升级到 Capslox 3.0 语义。
    /// </summary>
    public int CapsloxDefaultsVersion { get; set; }

    /// <summary>
    /// 基础层默认表的**当前**版本（见 <see cref="CapsloxDefaultsVersion"/>）。
    /// 放在这里而不是 SettingsStore：DefaultSettings（生成默认表）和
    /// SettingsStore（迁移）两个类都要用，挂在数据类上才不会绕圈引用。
    /// </summary>
    public const int CapsloxDefaultsVersionCurrent = 1;

    public GlobalOptions Global { get; set; } = new();
    public QbarOptions Qbar { get; set; } = new();

    /// <summary>文件对话框搜索栏的开关与外观。</summary>
    public FileDialogBarOptions FileDialogBar { get; set; } = new();

    /// <summary>启动器空态"搜索前建议"的开关、来源与黑名单。</summary>
    public SuggestionsOptions Suggestions { get; set; } = new();

    public SyncOptions Sync { get; set; } = new();

    /// <summary>启动密码（与备份密码独立的另一套，见 <see cref="SecurityOptions"/>）。</summary>
    public SecurityOptions Security { get; set; } = new();
    public List<KeyBinding> Keys { get; set; } = new();


    public List<QEntry> Search { get; set; } = new();
    public List<QEntry> Run { get; set; } = new();
    public List<QEntry> Web { get; set; } = new();
    public List<QEntry> HotStrings { get; set; } = new();

    /// <summary>文本片段的全局设置（前缀符号、全/半角、空格触发）。</summary>
    public HotstringOptions Hotstring { get; set; } = new();

    /// <summary>触发键列表（可多个；每个绑定一层）。</summary>
    public List<TriggerKey> Triggers { get; set; } = new();

    /// <summary>层列表（默认 10 层）。</summary>
    public List<LayerOptions> Layers { get; set; } = new();

    /// <summary>宏列表。</summary>
    public List<Macro> Macros { get; set; } = new();

    /// <summary>
    /// Lua 脚本（v1.5.0）。每个脚本在动作目录里是一条「Lua 脚本」分类的动作，
    /// 可以像普通动作一样绑到按键或手势上；脚本里能调用软件已有的动作与输入能力。
    /// </summary>
    public List<LuaScript> Scripts { get; set; } = new();

    /// <summary>手势菜单（v1.5.0）：一个菜单一列条目，每个条目指向一个已有动作。</summary>
    public List<GestureMenuDef> GestureMenus { get; set; } = new();

    /// <summary>
    /// 原键替换层：**不需要触发键**的一组绑定。
    ///
    /// 与 <see cref="Layers"/> 的区别只有一条：层要按住触发键才生效，
    /// 这里按下去就执行，等于"这个物理键现在就是这个功能"。
    /// 一条 <c>space → 1</c> 的绑定写进来，空格就直接变成 1；
    /// 鼠标中键写成一组快捷键，按中键就打出那组快捷键。
    ///
    /// 与 <see cref="GlobalOptions.BaseRemaps"/> 的区别：改键只能换成
    /// 另一个键，这里可以绑任意动作（发送组合键、宏、打开程序、文本…）。
    /// 两者都命中同一个键时，替换层优先（见 HotkeyEngine.Replacement.cs）。
    /// </summary>
    public List<KeyBinding> Replacements { get; set; } = new();

    /// <summary>
    /// 原键替换页的鼠标键改动是否生效。
    /// 关掉后替换层对 mouseLeft/Right/Middle/X1/X2 全部放行，键盘替换不受影响。
    /// </summary>
    public bool ReplaceMouseKeysEnabled { get; set; } = true;

    /// <summary>
    /// 设备方案：按键盘 / 鼠标分开的一套设置（含「通用设置」那一份）。
    ///
    /// 老配置里没有这一段时，<see cref="Keys"/> + <see cref="GlobalOptions.BaseRemaps"/>
    /// 就是全部行为，升级上来不会有任何变化。
    /// </summary>
    public List<DeviceProfile> Profiles { get; set; } = new();

    /// <summary>
    /// 鼠标手势：在任意程序上按住鼠标键画一笔 → 执行一个动作。
    ///
    /// 与「原键替换层」的分工：替换层是"这颗键从此就是这个功能"，
    /// 手势是"按住这颗键再画个形状"。两者可以绑同一颗键 —— 手势服务
    /// 挂在钩子最前面，真按下时它先决定要不要接管。
    /// </summary>
    public List<MouseGesture> Gestures { get; set; } = new();

    /// <summary>手势功能的全局选项（开关、轨迹、阈值）。</summary>
    public GestureOptions GestureOptions { get; set; } = new();

    /// <summary>
    /// 手势名称框的外观（设置落在「鼠标手势 → 手势设置」）。
    ///
    /// v1.6.0 曾把它和右下角提示条合并成一套，用下来两边要的东西并不一样，
    /// 于是又拆成两份各自独立的配置 —— 改名字框的底色不该把提示条也带着变。
    /// 老配置里的值由 SettingsStore.Normalize 迁移过来（见 <see cref="GestureLabelStyle.MigrateVersion"/>）。
    /// </summary>
    public GestureLabelStyle GestureLabel { get; set; } = new();

    /// <summary>
    /// 右下角提示条的外观：描边 + 一种底色，位置固定贴右下角。
    /// 与 <see cref="GestureLabel"/> 互不影响。
    /// </summary>
    public ToastStyle Toasts { get; set; } = new();

    /// <summary>
    /// v1.6.0 合并期的「通知样式」，**只读兼容字段**：老配置里的值由
    /// <see cref="SettingsStore.Normalize"/> 搬到 <see cref="GestureLabel"/> 与
    /// <see cref="Toasts"/>，搬完置 null（null 不会被写回文件）。
    /// 新代码一律用上面两份，不要再读这里。
    /// </summary>
    public NotificationStyle? Notifications { get; set; }

    /// <summary>
    /// 平滑滚动的参数（字段与默认值照 SmoothScroll 的 AppOptions）。
    /// 出厂是关的：见 <see cref="ScrollOptions.Enabled"/> 的说明。
    /// </summary>
    public ScrollOptions Scroll { get; set; } = new();

    /// <summary>
    /// 按程序覆盖平滑滚动。列表里没有的程序一律跟随 <see cref="Scroll"/>；
    /// 规则里为 null 的字段也跟随全局（只有用户真改过某一项才存那一项）。
    /// </summary>
    public List<ScrollAppRule> ScrollApps { get; set; } = new();

    /// <summary>
    /// 「按住拖拽」总开关（长按触发键拖动 / 调整窗口，引擎来自 HoldDrag）。
    /// </summary>
    public bool HoldDragEnabled { get; set; }

    /// <summary>
    /// 「按住拖拽」的全部参数，以 JSON 文本存放。
    ///
    /// schema 归 HoldDrag 模块自己管（触发键、长按时长、外框样式、吸附阈值、按程序规则…），
    /// 宿主只负责"随整份设置一起存取"，于是它天然跟着密码库、云端备份、整份导入导出走，
    /// 不需要在 AppSettings 里再抄一份 40 多个字段的镜像。
    /// </summary>
    public string HoldDragJson { get; set; } = "";

    /// <summary>
    /// 中键在桌面 / 资源管理器**空白处**单击时，弹出 Input Plus 自己的菜单。
    ///
    /// 出厂关，理由有三条，都写在 DECISIONS.md 里：
    ///   ① 中键这颗键全系统都在用 —— 浏览器自动滚动、CAD/3D 平移、资源管理器开新标签，
    ///      默认接管等于悄悄改掉用户已经形成肌肉记忆的一颗键；
    ///   ② 菜单内容是"收藏 / 最近 / 自定义命令"，新装用户点开是一片空，先给开关更诚实；
    ///   ③ 关着的时候行为与升级前**完全一致**，老配置读进来不需要任何迁移。
    /// </summary>
    public bool MiddleClickMenuEnabled { get; set; } = true;

    /// <summary>
    /// 鼠标唤出用的键：middle / x1 / x2（可多选，出厂只开中键）。
    /// 鼠标这一路**只在系统桌面**生效 —— 别的窗口里中键/侧键各有用途（浏览器自动滚动、
    /// 资源管理器前进后退），接管了就是抢键。
    /// </summary>
    public List<string> MiddleMenuMouseButtons { get; set; } = new() { "middle" };

    /// <summary>
    /// 键盘唤出的组合键（例如 <c>ctrl+alt+d</c>）。空 = 不注册。
    /// 键盘这一路在**所有场景**都能唤出（全屏程序与例外程序除外），位置仍在鼠标处。
    /// </summary>
    public string MiddleMenuHotkey { get; set; } = "";

    /// <summary>
    /// 例外程序：这些 exe 里连键盘唤出都不弹（存全路径，比较时也允许只比文件名）。
    /// 用「选择程序 / 点选窗口」两个选择器加进来，和本程序其它地方的口径一致。
    /// </summary>
    public List<string> MiddleMenuExceptions { get; set; } = new();

    /// <summary>
    /// 中键菜单用哪一份：空 = 内置动态菜单（收藏文件夹 / 最近打开 / 自定义命令 / 打开设置，
    /// 每次现拼）；非空 = <see cref="GestureMenus"/> 里那个 Id 的菜单。
    ///
    /// 为什么留一个空串而不是必选：这两件事本来就是两种需求 ——
    /// "随手在桌面点一下，给我常去的地方"（动态）和"我配好的一份固定菜单"（静态）。
    /// 空串而不是 null：JSON 里缺这项、写 null、写空串都落到同一个默认，反序列化不会拐进 null 分支。
    /// </summary>
    public string MiddleClickMenuId { get; set; } = "";

    /// <summary>
    /// 中键菜单的生效范围，存字符串而不是枚举 —— 和 <see cref="InputPlus.Core.Gestures.GestureOptions.TargetMode"/>
    /// 同一个理由：配置里被人手改成别的词时只退回默认，不会让整份设置反序列化抛异常
    /// （那会触发 SettingsStore.Load 的"读取失败 → 回退默认设置"，用户会觉得设置全丢了）。
    /// 取值见 <see cref="MiddleMenuScope"/> 的常量。
    /// </summary>
    public string MiddleClickMenuScope { get; set; } = MiddleMenuScope.DesktopAndExplorer;

    /// <summary>
    /// 「打开文件所在位置」的行为，存字符串而不是枚举 —— 和
    /// <see cref="MiddleClickMenuScope"/> 同一个理由：配置被人手改成别的词时
    /// 只退回默认，不会让整份设置反序列化抛异常。
    /// 取值见 <see cref="FileLocationMode"/>，默认选中文件（用户要的是"这个文件在哪"）。
    /// </summary>
    public string OpenFileLocationMode { get; set; } = FileLocationMode.SelectFile;

    public KeyBinding? FindBinding(string combo) =>
        Keys.FirstOrDefault(k => string.Equals(k.Combo, combo, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 拿某一层的按键绑定（找不到层时返回 null）。
    /// 老设置文件没有层概念，此时 <see cref="Keys"/> 就是事实上的第 1 层。
    /// </summary>
    public List<KeyBinding> LayerKeys(int layerId)
    {
        var layer = Layers.FirstOrDefault(l => l.Id == layerId);
        if (layer == null)
        {
            return layerId == 1 ? Keys : new List<KeyBinding>();
        }
        return layer.Keys;
    }

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, SettingsStore.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
    }
}

/// <summary>
/// 中键菜单的生效范围：只在桌面，还是桌面 + 资源管理器文件夹窗口。
///
/// 两套表示各干一件事：
///   · <see cref="AppSettings.MiddleClickMenuScope"/> 存**字符串**（老配置容错，见那边的注释）；
///   · 代码里比较、传参用这个**枚举**，别在各处写 "desktop" 字面量。
/// 两边靠 <see cref="ToScope"/> / <see cref="ToStorage"/> 互转，解析失败的字符串一律按
/// <see cref="DesktopAndExplorer"/> 处理 —— 默认范围就是"两个地方都管"，
/// 用户真改成不认识的值，回到默认比"突然只在桌面生效"更不容易让人以为坏了。
/// </summary>
public enum MiddleClickMenuScope
{
    /// <summary>只在桌面空白处弹。</summary>
    Desktop = 0,

    /// <summary>桌面 + 资源管理器文件夹窗口的空白处。</summary>
    DesktopAndExplorer = 1,
}

/// <summary>中键菜单范围：存储用的字符串常量与解析/归一化。</summary>
public static class MiddleMenuScope
{
    /// <summary>只在桌面空白处弹。</summary>
    public const string Desktop = "desktop";

    /// <summary>桌面 + 资源管理器文件夹窗口的空白处（出厂值）。</summary>
    public const string DesktopAndExplorer = "desktopExplorer";

    /// <summary>字符串 → 枚举。null / 空 / 不认识的词一律回落到 <see cref="MiddleClickMenuScope.DesktopAndExplorer"/>。</summary>
    public static MiddleClickMenuScope ToScope(string? stored) => (stored ?? "").Trim().ToLowerInvariant() switch
    {
        Desktop => MiddleClickMenuScope.Desktop,
        DesktopAndExplorer => MiddleClickMenuScope.DesktopAndExplorer,
        _ => MiddleClickMenuScope.DesktopAndExplorer,
    };

    /// <summary>枚举 → 存储用的字符串。</summary>
    public static string ToStorage(MiddleClickMenuScope scope) =>
        scope == MiddleClickMenuScope.Desktop ? Desktop : DesktopAndExplorer;

    /// <summary>这个范围管不管资源管理器文件夹窗口。</summary>
    public static bool IncludesExplorer(MiddleClickMenuScope scope) =>
        scope == MiddleClickMenuScope.DesktopAndExplorer;
}

/// <summary>
/// 「打开文件所在位置」（v1.5.1）的两种行为。
///
/// 默认选中文件，是因为用户点它的动机几乎总是"这个文件到底在哪"：
/// 资源管理器把父目录打开、光标落在那个文件上，一眼就能看见位置；
/// 只开一个空目录还得自己再找一遍。但"选中"会让窗口滚动一下，
/// 有人就是不喜欢，所以留一个只开目录的开关（设置 → 附加菜单）。
/// </summary>
public static class FileLocationMode
{
    /// <summary>父文件夹里选中这个文件（出厂值）。</summary>
    public const string SelectFile = "selectFile";

    /// <summary>只把父文件夹打开，不定位、不选中。</summary>
    public const string OpenFolderOnly = "openFolderOnly";

    /// <summary>
    /// 存储串 → 是不是"只开目录"。空 / 不认识的词一律按"选中"处理 ——
    /// 那是更常用、也更符合"我要看这个文件在哪"的行为。
    /// </summary>
    public static bool IsOpenFolderOnly(string? stored) =>
        string.Equals((stored ?? "").Trim(), OpenFolderOnly, StringComparison.OrdinalIgnoreCase);

    /// <summary>存储串 → 规范化的存储串（写回设置时用，避免把脏值一次次存回去）。</summary>
    public static string Normalize(string? stored) =>
        IsOpenFolderOnly(stored) ? OpenFolderOnly : SelectFile;
}

