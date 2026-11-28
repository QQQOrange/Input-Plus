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
///
/// <c>Takeable</c>（v1.5.0 之前叫 Blank）不再是"桌面空白"的同义词：
/// 桌面要求空白（压在图标上交给 shell），资源管理器与文件对话框**无条件**为 true。
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
    bool Takeable,
    bool HasMiddleGesture);

/// <summary>
/// 「历史记录」二级菜单里的一条 —— 就是 Qbar 空面板上「搜索前建议」的那几条。
/// 抽成这个类型是为了让 <see cref="MiddleMenuPolicy"/> 不依赖 Suggestions 服务本身
/// （那个服务要读磁盘、要后台采集，自检里根本不该被拖进来）。
/// </summary>
internal readonly record struct MiddleMenuSuggestion(string Title, string Target, bool IsUrl);

/// <summary>
/// 附加菜单顶部"当前文件"两个入口的状态（v1.5.1）。
///
/// 由 GestureService 在**弹菜单之前**抓：那一刻前台还是用户正在用的程序，
/// 菜单一 Show 前台就变成我们自己了（所以这里不能自己去 GetForegroundWindow）。
/// </summary>
/// <param name="Actionable">前台是资源管理器 / 桌面 / 任务栏时为 false —— 两个入口一起置灰。</param>
/// <param name="ProcessName">前台程序名（排查用，也显示在第二行小字里）。</param>
/// <param name="FileName">已经识别到的文件名（预热命中时才有）。</param>
/// <param name="AlreadyFavorited">这个文件已经在固定项里了 → 星标显示实心。</param>
internal readonly record struct ForegroundFileState(
    bool Actionable,
    string ProcessName,
    string FileName,
    bool AlreadyFavorited)
{
    /// <summary>"还没识别到"的空状态：两条入口照常出现，但第二行没有文件名。</summary>
    internal static ForegroundFileState ForProcess(string processName) => new(true, processName, "", false);
}

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
    /// 7. 配了中键手势 → 按下交给手势（**不看范围**，手势在任何程序里都该能用）；
    ///    抬起时"没成笔"才回头问菜单：范围里且可弹 → 弹，否则交给补发
    /// 8. 没配手势 + 不在范围内 → 不管（浏览器正文、编辑器、终端…的原生中键）
    /// 9. 没配手势 + 在范围内 → 按下先吞，抬起再看（路径 A）
    /// </summary>
    internal static MiddleMenuAction Decide(in MiddleMenuInput i)
    {
        if (!i.MiddleVk) return MiddleMenuAction.Ignore;
        if (i.Injected) return MiddleMenuAction.Ignore;
        if (i.MenuOpen) return i.Down ? MiddleMenuAction.CloseMenuAndSwallow : MiddleMenuAction.Ignore;
        if (!i.Enabled) return MiddleMenuAction.Ignore;
        if (i.Taskbar) return MiddleMenuAction.Ignore;
        if (i.Fullscreen) return MiddleMenuAction.Ignore;

        /* ── 手势优先于"弹菜单的作用范围"（v1.5 修） ─────────────────────
         *
         * 「范围」（桌面 / 资源管理器 / 文件对话框）回答的是**菜单能不能在这里弹**，
         * 它从来不是"这颗键归不归我们"的回答。v1.5.0 把两件事合成了一条 if，
         * 代价是：WPS / 浏览器 / Electron 程序里，配了中键手势的按下**连手势都不再开始**
         * （OnButton 里那句"不在附加菜单的范围里：中键原样放行"把整下放行了）。
         * 现场日志 `arm: vk=0x04 hasGesture=True` 后面紧跟这一句，就是这条。
         *
         * 分工改回来：
         *   · 这颗键在这里配了手势 → 按下照样接管，手势在任何程序里都能用；
         *     松手时"没成笔"才回头问菜单（能弹就弹，不能弹就补发一次原生点击）。
         *   · 没配手势 → 只有落在范围内才接管（浏览器自动滚动、编辑器粘贴照旧）。 */
        if (i.HasMiddleGesture)
        {
            if (i.Down) return MiddleMenuAction.StartGesture;
            return i.InScope && i.Takeable ? MiddleMenuAction.MenuInsteadOfTap : MiddleMenuAction.Ignore;
        }

        if (!i.InScope) return MiddleMenuAction.Ignore;

        if (!i.Down) return MiddleMenuAction.Ignore;
        return i.Takeable ? MiddleMenuAction.SwallowTap : MiddleMenuAction.Ignore;
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
        IReadOnlyDictionary<string, string>? urlTitles = null,
        ForegroundFileState? foreground = null)
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

        /* ⓪ 当前文件的两个入口（v1.5.1）。
         *
         * 放最前是因为它俩的语义是"用户刚才在用的东西"，比任何固定项都更贴近当下；
         * 编号快选 1/2 也就落在它们身上。
         * foreground 为 null（手势菜单、自检的老用例）时整段不出现 ——
         * 结构与 v1.5.0 完全一致，老断言不用改。 */
        if (foreground is { } fg)
        {
            string note = fg.FileName.Length > 0
                ? fg.FileName
                : (fg.Actionable ? Loc.T("识别中…") : Loc.T("前台没有打开文件"));

            /* v1.5.2：**只留「打开文件所在位置」**。
             * 「收藏当前文件」已经并进右上角那颗星（星标弹出的收藏对话框里多了一条
             * 「它正在打开：xxx」+「收藏这个文件」），功能重复摆两处只会让人犹豫点哪个。
             *
             * v1.5：**只有"已经认出文件"才点亮**。
             * 以前 Enabled = fg.Actionable，而 Actionable 只回答"前台不是桌面/资源管理器"，
             * 于是 DeepSeekRuntime / Edge 这类程序（Electron / 浏览器，句柄里根本没有用户文件）
             * 里这一条永远是亮的；点下去识别不到，就弹出"选择要定位的文件" ——
             * 用户看到的是"我点了一下，它让我自己找个文件"，完全不知道发生了什么。
             * 现在默认灰，预热（识别）回来的那一拍才根据 hasFile 决定亮不亮
             * （见 GestureMenuWindow.MarkForegroundFile）。
             *
             * **v1.7.9：重新变成"认出程序就点亮"**，因为这一条现在有第二层 ——
             * 认不出文件时退一步打开**这个程序自己的 exe 所在文件夹**
             * （见 AppHost.BeginRevealForegroundFile）。所以"看着是亮的，点下去一定有东西打开"
             * 重新成立；当初担心的"点完弹出选择文件的对话框"那条兜底早就不在了。 */
            AddTop(new GestureMenuItem
            {
                Title = Loc.T("打开文件所在位置"),
                Glyph = "\uE838",                                    // MDL2：FolderOpen
                Note = note,
                Action = "revealForegroundFile",
                Enabled = fg.Actionable,
            });
        }
        // ① 固定项（每个条目自己在 Qbar 设置里勾）
        /* 给「历史记录 / 命令 / 打开设置」各留一格：固定项再多也不能把它们挤掉 ——
         * 菜单最后一条总是"打开设置"，没了就等于用户被关在菜单里。 */
        /* 顶部多两条入口时，给固定项留的格子也要少两条（新入口和"打开设置"都不能被挤掉）。 */
        int pinnedCeiling = Math.Max(1, MaxTopLevel - (foreground != null ? 4 : 3));
        int pinnedCount = 0;
        foreach (var pinned in (settings.Qbar?.Pinned ?? new List<QEntry>()).Where(p => p != null && p.ShowInMiddleMenu))
        {
            if (pinnedCount >= pinnedCeiling) break;
            string target = (pinned.Value ?? "").Trim();
            if (target.Length == 0) continue;
            pinnedCount++;

            bool isUrl = IsUrl(target);
            /* 只显示**备注**：这是用户在 Qbar 里给这一条起的名字。
             * 备注空时才退回关键字（否则菜单里会出现一行空白）。
             * 内容（Value：路径/网址）一律不显示 —— 那是实现细节，不该占菜单的行。 */
            string title = !string.IsNullOrWhiteSpace(pinned.Comment)
                ? pinned.Comment.Trim()
                : (!string.IsNullOrWhiteSpace(pinned.Key) ? pinned.Key.Trim() : LeafOf(target));

            /* 备注：网址显示抓回来的网页标题（还没抓到就先空着，菜单不闪）；
             * 文件/文件夹显示它自己的路径，长路径靠菜单里的省略号截。 */
            string note = isUrl
                ? (urlTitles != null && urlTitles.TryGetValue(target, out string? t) ? t : "")
                : "";   // 文件/文件夹不写第二行：菜单里只留备注（用户明确要求）

            AddTop(new GestureMenuItem
            {
                Title = title,
                Note = note,
                IconPath = isUrl ? target : (ShellIcons.ResolveFilesystemPath(target) ?? target),
                Action = isUrl ? "webPage" : "openFile",
                Arg = target,
            });
        }

        /* 启动程序 / 常用网址：和固定项一样，勾了「附加菜单」就进菜单。
         * 标题只取备注（为空退回关键字），内容不显示 —— 和固定项同一套规则。 */
        foreach (var run in (settings.Run ?? new List<QEntry>()).Where(p => p != null && p.ShowInMiddleMenu))
        {
            string target = (run.Value ?? "").Trim();
            if (target.Length == 0) continue;
            string title = !string.IsNullOrWhiteSpace(run.Comment)
                ? run.Comment.Trim()
                : (!string.IsNullOrWhiteSpace(run.Key) ? run.Key.Trim() : LeafOf(target));
            string quoted = target.Contains(' ') ? "\"" + target + "\"" : target;
            string args = (run.Arguments ?? "").Trim();
            AddTop(new GestureMenuItem
            {
                Title = title,
                Note = "",
                IconPath = ShellIcons.ResolveFilesystemPath(target) ?? target,
                Action = "runProgram",
                Arg = args.Length == 0 ? quoted : quoted + " " + args,
            });
        }

        foreach (var web in (settings.Web ?? new List<QEntry>()).Where(p => p != null && p.ShowInMiddleMenu))
        {
            string target = (web.Value ?? "").Trim();
            if (target.Length == 0) continue;
            string title = !string.IsNullOrWhiteSpace(web.Comment)
                ? web.Comment.Trim()
                : (!string.IsNullOrWhiteSpace(web.Key) ? web.Key.Trim() : LeafOf(target));
            AddTop(new GestureMenuItem
            {
                Title = title,
                Note = (urlTitles != null && urlTitles.TryGetValue(target, out string? t) ? t : ""),
                IconPath = target,
                Action = "webPage",
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
                Glyph = "\uE81C",
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
                Glyph = "\uE756",
                Action = "",
                Children = commandChildren,
            });
        }

        // ④ 打开设置（永远最后一条）
        AddTop(new GestureMenuItem
        {
            Title = Loc.T("打开设置"),
            Glyph = "\uE713",   // 和软件里其它"设置"入口同一个齿轮字形
            Action = "openSettings",
            Arg = "",
        });

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
