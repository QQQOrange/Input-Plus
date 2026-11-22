using InputPlus.Core.Qbar;
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
/// <param name="MiddleVk">这一下是不是中键（0x04）。</param>
/// <param name="Down">按下还是抬起。</param>
/// <param name="Injected">是不是我们自己注入的（菜单里若有「点击中键」不能再触发自己）。</param>
/// <param name="MenuOpen">菜单窗口是不是正开着。</param>
/// <param name="Enabled">设置里的总开关。</param>
/// <param name="InScope">按下点是否落在生效范围内（桌面 / 桌面+资源管理器）。</param>
/// <param name="Taskbar">指针在任务栏/托盘上（让给 Explorer）。</param>
/// <param name="Fullscreen">前台是真正的全屏程序（游戏/播放器/F11）。</param>
/// <param name="Blank">按下点是不是空白（按下时算一次，抬起直接复用）。</param>
/// <param name="HasMiddleGesture">用户给中键配过手势。</param>
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
/// 中键菜单的**纯策略**：决策表 + 菜单内容构造。
///
/// 为什么把这两件事从 <see cref="InputPlus.UI.GestureService"/> 里拆出来：
///   · 钩子回调里只能做"查表 + 记账"，判断逻辑混在里面既读不懂也测不了；
///   · 这里的两个入口都是纯函数（输入→枚举 / 设置→菜单定义），
///     自检可以一条一条喂，不需要真的开桌面、真的按中键。
/// </summary>
internal static class MiddleMenuPolicy
{
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
    /// 两条来源，二选一：
    ///   · <see cref="AppSettings.MiddleClickMenuId"/> 指向 GestureMenus 里某个菜单 → 用它（克隆一份，
    ///     菜单窗绝不和设置共享引用）；
    ///   · 否则现拼一份动态菜单：收藏文件夹 / 最近打开 / 自定义命令 / 打开设置。
    ///
    /// 上限是 <see cref="GestureMenuDef.MaxItems"/>（12）：再多按不到，只会把窗口撑长。
    /// 配额 4 + 4 + 3 + 1 = 12，"打开设置"永远留在最后一位。
    /// </summary>
    internal static GestureMenuDef Build(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string wanted = (settings.MiddleClickMenuId ?? "").Trim();
        if (wanted.Length > 0)
        {
            GestureMenuDef? chosen = settings.GestureMenus?.FirstOrDefault(m => m.Id == wanted);
            if (chosen != null) return chosen.Clone();
            /* 指向的菜单被删了：不报错、不弹空窗，直接回落到动态菜单 ——
             * 用户下次进设置页会看到下拉已经变回"内置"。 */
        }

        var def = new GestureMenuDef
        {
            Id = "middle.runtime",
            Name = Loc.T("中键菜单"),
            Hint = Loc.T("收藏 / 最近 / 命令"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int reserveForSettings = 1;
        int ceiling = GestureMenuDef.MaxItems - reserveForSettings;

        void Add(string title, string action, string arg)
        {
            if (def.Items.Count >= ceiling) return;
            if (string.IsNullOrWhiteSpace(action)) return;
            string text = (title ?? "").Trim();
            if (text.Length == 0) return;
            /* 同一个目标可能同时在"收藏"和"最近"里 —— 菜单里出现两行一模一样的条目
             * 只会让人以为按错了。动作 + 参数一起当键，避免把不同动作的同名项也吞掉。 */
            if (!seen.Add(action + "\u0000" + arg)) return;
            def.Items.Add(new GestureMenuItem
            {
                /* 图标留空是故意的：菜单窗在"有字形"时会把 1..9 的序号藏起来，
                 * 而中键菜单最值钱的用法就是按数字快选。 */
                Title = text,
                Action = action,
                Arg = arg ?? "",
                Glyph = "",
            });
        }

        // ① 收藏文件夹（设置页里给文件对话框打的星，同一份数据，不新写存储）
        var favorites = settings.FileDialogBar?.Favorites;
        if (favorites != null)
        {
            foreach (var fav in favorites.Take(4))
            {
                if (fav == null || string.IsNullOrWhiteSpace(fav.Path)) continue;
                Add(string.IsNullOrWhiteSpace(fav.Name) ? LeafOf(fav.Path) : fav.Name, "openFile", fav.Path);
            }
        }

        // ② 最近打开（Qbar 空面板用的同一份记录）
        foreach (var recent in RecentTargets.Top(4))
        {
            if (recent == null || string.IsNullOrWhiteSpace(recent.Target)) continue;
            string title = string.IsNullOrWhiteSpace(recent.Title) ? LeafOf(recent.Target) : recent.Title;
            Add(title, recent.Kind == RecentTargetKind.Url ? "webPage" : "openFile", recent.Target);
        }

        // ③ 自定义命令（用户在 Qbar 里配的那些"关键字 → 程序"）
        int added = 0;
        var commands = settings.Qbar?.Commands;
        if (commands != null)
        {
            foreach (var cmd in commands.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path)).Take(3))
            {
                Add(string.IsNullOrWhiteSpace(cmd.Title) ? cmd.Keyword : cmd.Title, "runProgram", CommandLineOf(cmd));
                added++;
            }
        }

        /* 一条命令都没配：给三条"点开就有东西"的内置动作，比一个只有设置的空菜单有用。
         * 不硬塞进菜单定义里存下来 —— 每次现拼，用户之后配了命令就自动换掉。 */
        if (added == 0)
        {
            Add(Loc.T("打开文件资源管理器"), "openExplorer", "");
            Add(Loc.T("Qbar 快速启动"), "qbar", "");
            Add(Loc.T("剪贴板历史"), "clipboardHistory", "");
        }

        // ④ 打开设置（永远最后一条，也是配额里预留的那一格）
        Add(Loc.T("打开设置"), "openSettings", "");

        return def;
    }

    /// <summary>路径的最后一段，用来给没写备注的收藏/最近项当标题。</summary>
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
