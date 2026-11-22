using InputPlus.Core.Icons;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Scripting;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Gestures;

/// <summary>中键这一下该怎么处理。五个值互斥，由 <see cref="MiddleMenuPolicy.Decide"/> 按固定顺序选出。</summary>
internal enum MiddleMenuAction
{
    /// <summary>不接管：不吞、不弹（"点别的程序"这条主路径）。</summary>
    Ignore = 0,

    /// <summary>菜单正开着又按了一下中键：关掉它，并且按下/抬起都吞（不然底下程序收到孤儿 MouseUp）。</summary>
    CloseMenuAndSwallow = 1,

    /// <summary>这颗键在用户那里配了中键手势：走原来的手势跟踪，本模块只在"没成笔"时接手。</summary>
    StartGesture = 2,

    /// <summary>路径 A：用户没配中键手势 —— 吞下按下，抬起来再按"有没有划动"决定弹不弹。</summary>
    SwallowTap = 3,

    /// <summary>路径 B：手势没成笔（<c>extent &lt; MinMove</c>）—— 弹菜单，代替补发原生中键。</summary>
    MenuInsteadOfTap = 4,
}

/// <summary>
/// <see cref="MiddleMenuPolicy.Decide"/> 的输入。全是很便宜的布尔值，
/// 自检可以直接拼出来喂，不需要真的动鼠标。
/// </summary>
internal readonly record struct MiddleMenuInput(
    bool MiddleVk,
    bool Down,
    bool Injected,
    bool MenuOpen,
    bool Enabled,
    bool InScope,
    bool Taskbar,
    bool Fullscreen,
    bool Blank,
    bool HasMiddleGesture);

/// <summary>
/// 「历史记录」二级菜单里的一条 —— 就是 Qbar 空面板上「搜索前建议」的那几条。
/// 抽成这个类型是为了让 <see cref="MiddleMenuPolicy"/> 不依赖 Suggestions 服务本身
/// （那个服务要读磁盘、要后台采集，自检里根本不该被拖进来）。
/// </summary>
internal readonly record struct MiddleMenuSuggestion(string Title, string Target, bool IsUrl);

/// <summary>
/// 中键菜单的**纯策略**：决策表 + 菜单内容构造。
///
/// 为什么把这两件事从 <see cref="InputPlus.UI.GestureService"/> 里拆出来：
///   · 钩子回调里只能做"查表 + 记账"，判断逻辑混在里面既读不懂也测不了；
///   · 这里的两个入口都是纯函数（输入→枚举 / 设置 → 菜单定义），
///     自检可以一条一条喂，不需要真的开桌面、真的按中键。
/// </summary>
internal static class MiddleMenuPolicy
{
    /// <summary>一级菜单最多几条（二级不限，但父项一共也就三条）。</summary>
    private const int MaxTopLevel = GestureMenuDef.MaxItems;

    /// <summary>每个二级菜单最多几条：再多就得滚，悬停展开的菜单不该出现滚动条。</summary>
    private const int MaxChildren = 10;

    /// <summary>
    /// 决策表。顺序是硬的，改顺序等于改行为：
    ///
    /// 1. 不是中键 → 不管
    /// 2. 注入的 → 不管（菜单里「点击中键」这条动作不能套娃）
    /// 3. 菜单开着 → 只认"再按一下关掉"；抬起啥也不做（抬起已经被吞了）
    /// 4. 开关关 → 全部恢复原样
    /// 5. 任务栏/托盘 → 让给 Explorer
    /// 6. 全屏程序 → 不弹（**独立于**手势的那个"全屏不接管"开关，见 DECISIONS）
    /// 7. 不在范围内 → 不管
    /// 8. 配了中键手势 → 按下交给手势；抬起时"没成笔"才弹菜单
    /// 9. 没配手势 → 按下先吞，抬起再看（路径 A）
    /// </summary>
    internal static MiddleMenuAction Decide(in MiddleMenuInput i)
    {
        if (!i.MiddleVk) return MiddleMenuAction.Ignore;
        if (i.Injected) return MiddleMenuAction.Ignore;
        if (i.MenuOpen) return i.Down ? MiddleMenuAction.CloseMenuAndSwallow : MiddleMenuAction.Ignore;
        if (!i.Enabled) return MiddleMenuAction.Ignore;
        if (i.Taskbar) return MiddleMenuAction.Ignore;
        if (i.Fullscreen) return MiddleMenuAction.Ignore;
        if (!i.InScope) return MiddleMenuAction.Ignore;

        if (i.HasMiddleGesture)
        {
            /* 手势优先：按下时把这一整下让给手势跟踪。真正决定"要不要弹菜单"的是松手时
             * Finish() 里的 extent < MinMove —— 那时才知道用户是点了一下还是划了一笔。 */
            if (i.Down) return MiddleMenuAction.StartGesture;
            return i.Blank ? MiddleMenuAction.MenuInsteadOfTap : MiddleMenuAction.Ignore;
        }

        if (!i.Down) return MiddleMenuAction.Ignore;
        return i.Blank ? MiddleMenuAction.SwallowTap : MiddleMenuAction.Ignore;
    }

    /// <summary>
    /// 拼出这次要弹的菜单。
    ///
    /// 结构（v1.3.17 起，按用户要求重排）：
    ///   ① 固定项里**勾了「显示在中键菜单」**的那些条目，一条一项；
    ///   ② 「历史记录 ▸」二级菜单 = Qbar 空面板的「搜索前建议」；
    ///   ③ 「命令 ▸」二级菜单 = 勾了「显示在中键菜单」的 Qbar 命令；
    ///   ④ 打开设置（永远最后一条）。
    ///
    /// 不写任何介绍/说明文字：菜单是拿来点的，不是拿来读的（用户明确要求）。
    ///
    /// <paramref name="history"/> 和 <paramref name="urlTitles"/> 都由调用方现取：
    /// 建议要读磁盘、网址标题要联网，这两件事都不该发生在钩子回调里。
    /// </summary>
    internal static GestureMenuDef Build(
        AppSettings settings,
        IReadOnlyList<MiddleMenuSuggestion>? history = null,
        IReadOnlyDictionary<string, string>? urlTitles = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string wanted = (settings.MiddleClickMenuId ?? "").Trim();
        if (wanted.Length > 0)
        {
            GestureMenuDef? chosen = settings.GestureMenus?.FirstOrDefault(m => m.Id == wanted);
            if (chosen != null) return chosen.Clone();
            /* 指向的菜单被删了：不报错、不弹空窗，直接回落到内置结构 ——
             * 用户下次进设置页会看到下拉已经变回"内置"。 */
        }

        var def = new GestureMenuDef
        {
            Id = "middle.runtime",
            Name = Loc.T("附加菜单"),
            /* 不写 Hint 也不显示标题：菜单顶上什么都不写（用户要求）。 */
            Hint = "",
            HideTitle = true,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddTop(GestureMenuItem item)
        {
            if (def.Items.Count >= MaxTopLevel) return;
            if (string.IsNullOrWhiteSpace(item.Title)) return;
            if (!seen.Add(item.Action + "\u0000" + item.Arg + "\u0000" + item.Title)) return;
            def.Items.Add(item);
        }

        // ① 固定项（每个条目自己在 Qbar 设置里勾）
        /* 给「历史记录 / 命令 / 打开设置」各留一格：固定项再多也不能把它们挤掉 ——
         * 菜单最后一条总是"打开设置"，没了就等于用户被关在菜单里。 */
        int pinnedCeiling = Math.Max(1, MaxTopLevel - 3);
        int pinnedCount = 0;
        foreach (var pinned in (settings.Qbar?.Pinned ?? new List<QEntry>()).Where(p => p != null && p.ShowInMiddleMenu))
        {
            if (pinnedCount >= pinnedCeiling) break;
            string target = (pinned.Value ?? "").Trim();
            if (target.Length == 0) continue;
            pinnedCount++;

            bool isUrl = IsUrl(target);
            string title = string.IsNullOrWhiteSpace(pinned.Comment) ? LeafOf(target) : pinned.Comment.Trim();

            /* 备注：网址显示抓回来的网页标题（还没抓到就先空着，菜单不闪）；
             * 文件/文件夹显示它自己的路径，长路径靠菜单里的省略号截。 */
            string note = isUrl
                ? (urlTitles != null && urlTitles.TryGetValue(target, out string? t) ? t : "")
                : target;

            AddTop(new GestureMenuItem
            {
                Title = title,
                Note = note,
                IconPath = isUrl ? target : (ShellIcons.ResolveFilesystemPath(target) ?? target),
                Action = isUrl ? "webPage" : "openFile",
                Arg = target,
            });
        }

        // ② 历史记录 ▸ 搜索前建议
        var historyChildren = new List<GestureMenuItem>();
        foreach (var h in history ?? Array.Empty<MiddleMenuSuggestion>())
        {
            if (historyChildren.Count >= MaxChildren) break;
            if (string.IsNullOrWhiteSpace(h.Target)) continue;
            historyChildren.Add(new GestureMenuItem
            {
                Title = string.IsNullOrWhiteSpace(h.Title) ? LeafOf(h.Target) : h.Title,
                Note = h.Target,
                IconPath = h.IsUrl ? h.Target : (ShellIcons.ResolveFilesystemPath(h.Target) ?? h.Target),
                Action = h.IsUrl ? "webPage" : "openFile",
                Arg = h.Target,
            });
        }
        if (historyChildren.Count > 0)
        {
            AddTop(new GestureMenuItem
            {
                Title = Loc.T("历史记录"),
                Action = "",
                Children = historyChildren,
            });
        }

        // ③ 命令 ▸ 勾选过的 Qbar 命令
        var commandChildren = new List<GestureMenuItem>();
        foreach (var cmd in (settings.Qbar?.Commands ?? new List<QbarCommand>())
                     .Where(c => c != null && c.ShowInMiddleMenu))
        {
            if (commandChildren.Count >= MaxChildren) break;
            string name = string.IsNullOrWhiteSpace(cmd.Title) ? cmd.Keyword : cmd.Title;
            if (string.IsNullOrWhiteSpace(name)) continue;
            commandChildren.Add(new GestureMenuItem
            {
                Title = name.Trim(),
                Note = string.IsNullOrWhiteSpace(cmd.Keyword) ? "" : cmd.Keyword,
                Action = "runProgram",
                Arg = CommandLineOf(cmd),
            });
        }
        if (commandChildren.Count > 0)
        {
            AddTop(new GestureMenuItem
            {
                Title = Loc.T("命令"),
                Action = "",
                Children = commandChildren,
            });
        }

        // ④ 打开设置（永远最后一条）
        AddTop(new GestureMenuItem { Title = Loc.T("打开设置"), Action = "openSettings", Arg = "" });

        return def;
    }

    /// <summary>网址判定：只要协议头，不猜域名（"D:\a.com" 不是网址）。</summary>
    internal static bool IsUrl(string? value) =>
        value != null &&
        (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
         || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>路径的最后一段，用来给没写备注的固定项当标题。</summary>
    private static string LeafOf(string path)
    {
        try
        {
            string trimmed = (path ?? "").TrimEnd('\\', '/');
            string leaf = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(leaf) ? trimmed : leaf;
        }
        catch { return path ?? ""; }
    }

    /// <summary>
    /// Qbar 命令 → 一条能交给「运行命令」动作的命令行。
    ///
    /// 为什么走 runProgram 而不是 QbarCommandStore.Execute：后者是 Qbar 面板内部语义
    /// （"关键字 + 尾串"），中键菜单没有输入框；而 runProgram 就是绑键用的同一条动作管线，
    /// 行为一致、失败提示也一致。代价是 RunAsAdmin / Silent / 工作目录这三个字段不生效 ——
    /// 想要它们的人应该把那条命令绑到键上，菜单只做"常用入口"。
    /// 路径带空格时补引号，否则 cmd 会把它拆成两截。
    /// </summary>
    private static string CommandLineOf(QbarCommand cmd)
    {
        string path = (cmd.Path ?? "").Trim();
        string quoted = path.Contains(' ') ? "\"" + path + "\"" : path;
        string args = (cmd.Parameter ?? "").Trim();
        return args.Length == 0 ? quoted : quoted + " " + args;
    }
}
