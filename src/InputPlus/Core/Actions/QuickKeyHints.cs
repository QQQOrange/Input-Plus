using InputPlus.Core.Input;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Actions;

/// <summary>
/// 「快速上手」页上的一格：触发键 + 组合键 + 动作名 + 一句说明。
///
/// 三个字段都是**给人看的字**，不是配置值：<see cref="Trigger"/> 与 <see cref="Key"/>
/// 已经过 <see cref="KeyName.Display"/> 处理（q → Q），可以直接塞进键帽。
/// </summary>
public sealed record QuickKeyHint(string Trigger, string Key, string Title, string Desc);

/// <summary>
/// 「快速上手」页那一块「最常用的 8 个组合」的**取数规则**（纯函数，无界面依赖）。
///
/// ## 为什么要有这个类
///
/// 这一段以前是 XAML 后面写死的一张表（Q = 打开 Qbar、C = 复制到剪贴板 1 …）。
/// 用户把 Q 改绑成「用谷歌搜索打开 Qbar」之后，它还写着「打开 Qbar」——
/// 页面上写着的东西和按下键真实发生的事不一致，等于骗人。
///
/// 现在每次刷新都从**第 1 层（主层）的真实绑定**现算，显示的名字一律取
/// <see cref="ActionCatalog"/> 的标题，所以和「按键映射」页永远是同一个叫法。
///
/// 抽成静态纯函数是为了让自检能直接验"选哪 8 条、显示什么名字"，
/// 不用先起一整套宿主 + 窗口。
///
/// ## 选取规则（改动这条规则时，请同步改 SelfTest.Guide.cs 的断言）
///
/// 候选 = 第 1 层里"真的绑了动作"的键（<see cref="KeyBinding.IsBound"/>），且：
///   · 组合键能被 <see cref="KeyName.ParseCombo"/> 解析（鼠标键也算，它们的 combo 同样合法）；
///   · 不是触发键本身（<c>press_caps</c> / key 为 capslock 的那条）；
///   · 同一个组合键只留第一条，同一格键帽文字（含修饰键）只出现一次；
///   · 同一个动作只占一格（Caps+V 与 Caps+Shift+V 都是「粘贴剪贴板 1」时，只留最顺手的那个）。
///
/// 排序（前面的先显示，取满 <see cref="MaxCount"/> 条为止）：
///   ① 用户改过 / 新加的在前 —— 与默认方案表逐条比 (Action, Arg)，不同就算改过；
///      填了备注的也算（备注只能是用户自己写的）。这一条直接决定用户
///      "我把 Q 换成谷歌搜索" 之后 Q 还能留在这一块里；
///   ② 用得多的在前（<see cref="UsageStats"/> 里该动作被执行过的次数）；
///   ③ 常用动作优先表（<see cref="ActionPriority"/>）里排得靠前的在前；
///   ④ 修饰键少的在前（不按修饰键的最顺手，其次才是一个修饰键的）；
///   ⑤ 键盘上的物理顺序（QWERTY 从左到右、从上到下）；
///   ⑥ 最后按组合键字符串兜底，保证同一份配置每次算出来的顺序完全一样。
///
/// 为什么不是"把 200 条绑定直接取前 8 条"：那样默认配置下会排出
/// A/B/C/D/E/F/G/H 这种按字母表来的结果 —— 那是配置文件的顺序，不是"最常用"。
/// </summary>
public static class QuickKeyHints
{
    /// <summary>这一块固定 8 格（两列 × 四行），多了会把页面撑长。</summary>
    public const int MaxCount = 8;

    /// <summary>
    /// 常用动作优先表：下标越小越"招牌"，用来决定默认配置下先露哪几条。
    ///
    /// 以冒号结尾的项按**前缀**匹配（<c>qbar:go</c>、<c>macro:xxxx</c> 这种动态 ID 也能归类），
    /// 其余按 ID 完全相等匹配。表里没有的动作排在所有列出的动作之后。
    ///
    /// 排版原则：**字母键上的招牌功能排在功能键上的工具前面** —— 这一块要教的是
    /// "按住触发键再按字母"，先让 Q/Tab/C/V/X/F6 这几个一眼就懂的功能占满前几格，
    /// 计算器（F2）、剪贴板历史（F8）这类功能键排在后面（它们在速查表里一样能查到）。
    ///
    /// 维护提示：想让某类功能更容易出现在「快速上手」上，把它往前挪即可；
    /// 顺序本身没有副作用 —— 它只影响这一块的展示优先级，不改变任何按键行为。
    /// </summary>
    private static readonly string[] ActionPriority =
    {
        // 招牌：面板与搜索
        "qbar", "qbar:", "search:", "qcmdsearch:", "dict.lookup", "qdict.lookup", "qfilter:", "fsearch:",
        // 招牌：计算 / 热字串（Caps+Tab）
        "tabScript",
        // 招牌：剪贴板三件套（C / V / X）
        "copy1", "paste1", "cut1",
        // 招牌：窗口置顶（Caps+F6）
        "winPin",
        // 日常编辑：光标移动与选择（"按住触发键再按字母"这件事的主体）
        "moveLeft", "selectLeft", "moveRight", "selectRight",
        "moveUp", "selectUp", "moveDown", "selectDown",
        "backspace", "delete", "home", "end",
        "selectCurrentLine", "selectAll", "upperCase", "lowerCase",
        // 第二梯队：功能键上的工具与剪贴板扩展
        "mathBoard", "clipboardHistory", "pasteSystem",
        "copy2", "paste2", "cut2", "toggleClipboard",
        "winSnapLeft", "winSnapRight", "winMaximize", "winCenter", "winTransparent",
        // 用户自己配的动作（带参数时本来就已经进"改过"那一档，这里只是兜底）
        "webPage", "openFile", "openFolder", "runProgram", "typeText", "customKey", "winBind",
        "macro:", "lua:", "luaTpl:", "menu:",
        // 其余常用
        "openSettings", "openDocs", "toggleEnabled", "openCalculator", "screenshot", "lockScreen", "killApp",
        "switchNextApp", "showDesktop", "volumeUp", "volumeDown", "volumeMute", "mediaPlayPause",
        "openStartMenu", "openQuickSettings", "openExplorer", "openTaskManager",
        "mouseLeft", "mouseRight", "mouseUp", "mouseDown", "clickLeft", "clickRight", "clickMiddle",
        "wheelUp", "wheelDown",
    };

    /// <summary>
    /// 键位物理顺序（QWERTY）。只用于"同样常用"时的先后；
    /// 与速查表里那张表的顺序一致 —— 两处都按键盘怎么摆来排，用户扫一眼就能对上。
    /// </summary>
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

    /// <summary>
    /// 从一组绑定里挑出「最常用的 8 个组合」。
    /// </summary>
    /// <param name="bindings">
    /// 当前层（主层 = 第 1 层）的绑定列表。传空 / null 返回空表，不抛异常 ——
    /// 用户把绑定全删了，页面该给一句提示，而不是崩掉。
    /// </param>
    /// <param name="lookup">动作 ID → 动作信息（界面上传 <c>AppHost.Actions.Get</c>）。</param>
    /// <param name="triggerLabel">触发键在键帽上的写法（"Caps" / "F13"…），由调用方按第 1 层实际触发键取。</param>
    /// <param name="max">要几条，上限 <see cref="MaxCount"/>。</param>
    /// <param name="usage">动作 ID → 被执行过的次数，用于"真用过"优先；null = 没有统计。</param>
    /// <param name="defaults">
    /// 与当前按键方案对应的默认绑定表，用来判断某条"是不是用户改过"。
    /// 传 null 表示无从比较（此时不启用"改过优先"这一档）。
    /// </param>
    public static List<QuickKeyHint> Pick(
        IEnumerable<KeyBinding>? bindings,
        Func<string, ActionInfo?>? lookup,
        string triggerLabel = "Caps",
        int max = MaxCount,
        IReadOnlyDictionary<string, int>? usage = null,
        IEnumerable<KeyBinding>? defaults = null)
    {
        int take = Math.Clamp(max, 0, MaxCount);
        var result = new List<QuickKeyHint>(take);
        if (take == 0 || bindings == null) return result;

        var reference = BuildReference(defaults?.ToList());

        /* 去重两道闸：combo 一样的是同一条绑定（手改配置/迁移残留会出现），
         * 键帽文字一样的是"同一颗键"（用户看来就是一格，列两次等于重复）。 */
        var seenCombos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenKeycaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rows = new List<Row>(64);
        foreach (var binding in bindings)
        {
            if (binding == null || !binding.IsBound) continue;

            /* 组合键可能被人手改配置写成 null/空白 —— ParseCombo 与下面两处都按空串处理，
             * 不能让一颗改坏的键把整页拖崩（快速上手是用户第一次打开就看到的页面）。 */
            string combo = (binding.Combo ?? "").Trim();
            var parsed = KeyName.ParseCombo(combo);
            if (parsed == null) continue;

            var (mods, key) = parsed.Value;

            // 触发键本身不是"组合"，它只是进入这一层的开关
            if (string.Equals(key, "capslock", StringComparison.OrdinalIgnoreCase)) continue;

            if (!seenCombos.Add(combo)) continue;

            string keycap = KeycapOf(mods, key);
            if (!seenKeycaps.Add(keycap)) continue;

            int used = 0;
            if (usage != null && !string.IsNullOrWhiteSpace(binding.Action))
                usage.TryGetValue(binding.Action.Trim(), out used);

            rows.Add(new Row(
                Binding: binding,
                Keycap: keycap,
                Used: used,
                Changed: IsChanged(binding, reference) ? 0 : 1,
                Priority: PriorityOf(binding.Action),
                ModRank: ModRankOf(mods),
                KeyRank: KeyRankOf(key)));
        }

        rows.Sort(Compare);

        /* 同一个动作在**多颗键**上都绑着时只出一格（取排在前面的那颗）。
         *
         * 默认方案里就有这种：Caps+V 和 Caps+Shift+V 都是「粘贴剪贴板 1」。
         * 八格里重复同一个功能等于浪费一格 —— 用户想看的是"八件不同的事"。 */
        var emittedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (result.Count >= take) break;

            string action = (row.Binding.Action ?? "").Trim();
            if (action.Length > 0 && !emittedActions.Add(action)) continue;

            var info = lookup?.Invoke(row.Binding.Action ?? "");

            /* 名字一律取动作目录的标题 —— 用户在这里看到的叫法必须和
             * 「按键映射」页一模一样。目录里查不到（配置来自更新/更旧的版本，
             * 或动作 ID 被手工改坏了）就退回 ID 里可读的那一段，别把机器串摊在界面上。 */
            string title = info != null && !string.IsNullOrWhiteSpace(info.Title)
                ? info.Title
                : ReadableId(row.Binding.Action);

            /* 第二行：备注优先。
             *
             * 直达网址/文件、输入文本、窗口绑定这些动作的参数是路径或正文，
             * 光看动作名（"直达网址"）根本认不出绑的是哪个网址 —— 用户自己写的备注
             * 才是这条绑定在**他这里**的意思，和键帽上显示的是同一套规则。 */
            string desc = !string.IsNullOrWhiteSpace(row.Binding.Note)
                ? row.Binding.Note!.Trim()
                : (info?.Description ?? "");

            result.Add(new QuickKeyHint(
                string.IsNullOrWhiteSpace(triggerLabel) ? "Caps" : triggerLabel.Trim(),
                row.Keycap,
                title,
                desc));
        }

        return result;
    }

    /// <summary>
    /// 找出"某个动作"绑在哪颗键上，返回键帽文字（**不含**触发键）。
    ///
    /// 快速上手横幅上那两枚键帽以前写死 `Caps + Q`：用户把 Q 改绑成
    /// "用谷歌搜索打开 Qbar"、或者干脆换到别的键之后，横幅照旧写着 Q ——
    /// 页面上说的和按下键会发生的事对不上。找不到返回 null（调用方自己决定怎么退化）。
    /// </summary>
    public static string? FindKeycap(IEnumerable<KeyBinding>? bindings, Func<string, bool> matches)
    {
        if (bindings == null || matches == null) return null;

        foreach (var binding in bindings)
        {
            if (binding == null || !binding.IsBound) continue;

            string action = (binding.Action ?? "").Trim();
            if (action.Length == 0 || !matches(action)) continue;

            var parsed = KeyName.ParseCombo((binding.Combo ?? "").Trim());
            if (parsed == null) continue;

            var (mods, key) = parsed.Value;
            if (string.Equals(key, "capslock", StringComparison.OrdinalIgnoreCase)) continue;

            return KeycapOf(mods, key);
        }
        return null;
    }

    /// <summary>
    /// 默认表 → (组合键 → 绑定)。用于判断"这一条用户改过没有"。
    /// 表为空时返回 null（表示无从比较，见 <paramref name="Pick"/> 的说明）。
    /// </summary>
    private static Dictionary<string, KeyBinding>? BuildReference(List<KeyBinding>? defaults)
    {
        if (defaults == null || defaults.Count == 0) return null;

        var map = new Dictionary<string, KeyBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defaults)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.Combo)) continue;
            map[d.Combo.Trim()] = d;      // 同 combo 重复时以后者为准（与设置里的行为一致）
        }
        return map;
    }

    /// <summary>这条绑定是不是"用户改过 / 新加的"（改过 = 与默认方案的 (Action, Arg) 不一致）。</summary>
    private static bool IsChanged(KeyBinding binding, Dictionary<string, KeyBinding>? reference)
    {
        // 备注只能是用户自己写的：填了就是"这一条我动过"
        if (!string.IsNullOrWhiteSpace(binding.Note)) return true;

        // 没有默认表：判断不了，一律按"没改过"，排序退化成 ②③④⑤
        if (reference == null) return false;

        if (!reference.TryGetValue((binding.Combo ?? "").Trim(), out var d)) return true;   // 默认方案里没这颗键 → 用户自己加的

        return !Same(d.Action, binding.Action) || !Same(d.Arg, binding.Arg);
    }

    private static bool Same(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>动作在常用动作优先表里的位置；不在表里就排在所有列出的动作之后。</summary>
    private static int PriorityOf(string? action)
    {
        string id = (action ?? "").Trim();
        if (id.Length == 0) return ActionPriority.Length;

        for (int i = 0; i < ActionPriority.Length; i++)
        {
            string p = ActionPriority[i];
            bool hit = p.EndsWith(':')
                ? id.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                : string.Equals(id, p, StringComparison.OrdinalIgnoreCase);
            if (hit) return i;
        }
        return ActionPriority.Length;
    }

    /// <summary>修饰键数量：没有修饰键的组合最"顺手"，排在前面。</summary>
    private static int ModRankOf(KeyModifiers mods)
    {
        int n = 0;
        if (mods.HasFlag(KeyModifiers.Ctrl)) n++;
        if (mods.HasFlag(KeyModifiers.Shift)) n++;
        if (mods.HasFlag(KeyModifiers.Alt)) n++;
        if (mods.HasFlag(KeyModifiers.Win)) n++;
        return n;
    }

    private static int KeyRankOf(string key)
    {
        int idx = Array.IndexOf(KeyOrder, KeyName.Display(key));
        return idx < 0 ? KeyOrder.Length : idx;
    }

    /// <summary>键帽上的文字：无修饰键就是键名，有修饰键写成 <c>Alt+Q</c>。</summary>
    private static string KeycapOf(KeyModifiers mods, string key)
    {
        string name = KeyName.Display(key);
        if (mods == KeyModifiers.None) return name;

        var parts = new List<string>(4);
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Win)) parts.Add("Win");

        // 拼成一个键帽而不是三个键帽：这一格只有半张卡片宽，再拆就把功能名挤没了
        return string.Join("+", parts) + "+" + name;
    }

    /// <summary>动作目录里查不到时的兜底名：取 ID 里冒号后面那段（<c>qbar:go</c> → <c>go</c>）。</summary>
    internal static string ReadableId(string? id)
    {
        string s = (id ?? "").Trim();
        if (s.Length == 0) return "";

        int colon = s.IndexOf(':');
        return colon >= 0 && colon + 1 < s.Length ? s[(colon + 1)..].Trim() : s;
    }

    private static int Compare(Row a, Row b)
    {
        int c = a.Changed.CompareTo(b.Changed);          // ① 动手改过的最前
        if (c != 0) return c;

        c = b.Used.CompareTo(a.Used);                    // ② 真的按过的次数多的在前
        if (c != 0) return c;

        c = a.Priority.CompareTo(b.Priority);            // ③ 常用动作优先表
        if (c != 0) return c;

        c = a.ModRank.CompareTo(b.ModRank);              // ④ 修饰键少的在前
        if (c != 0) return c;

        c = a.KeyRank.CompareTo(b.KeyRank);              // ⑤ 键盘物理顺序
        if (c != 0) return c;

        return string.Compare(a.Binding.Combo, b.Binding.Combo, StringComparison.OrdinalIgnoreCase);  // ⑥ 兜底：完全确定
    }

    /// <summary>排序用的一行（只在本类内部流转）。</summary>
    private readonly record struct Row(
        KeyBinding Binding, string Keycap, int Used, int Changed, int Priority, int ModRank, int KeyRank);
}
