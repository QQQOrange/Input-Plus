using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using InputPlus.Core.Input;
using InputPlus.Core.Settings;
using InputPlus.Core.Text;
using InputPlus.Core.Windows;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Actions;

public sealed partial class ActionContext
{

    public required Func<AppSettings> Settings { get; init; }
    public required ClipboardService Clipboard { get; init; }
    public required WindowActions Windows { get; init; }
    public required WindowBindingService WindowBindings { get; init; }
    public required MouseService Mouse { get; init; }
    public required Action<string> Notify { get; init; }

    public event Action<string?>? QbarRequested;
    /* 带过滤器的 Qbar 打开请求：第一个参数是 QbarFilter.Id，第二个是要搜的词。
     * 单独一条事件而不是复用 QbarRequested，是因为预填协议只有"引擎关键字 + 空格"，
     * 过滤器要变成 chip 必须走面板里的 ApplyFilter（它还要清输入框、亮 chip），
     * 靠字符串前缀硬凑会在"过滤器关键字和引擎同名"时打架。 */
    public event Action<string?, string?>? QbarFileSearchRequested;
    /* 带词的 Qbar 查词请求：参数是要查的词（可为 null = 只打开面板等用户打字）。
     * 同样不能复用 QbarRequested —— 那边只认引擎关键字预填，
     * 而"查词"要求把 ol 变成 chip（chip 才能把后面的输入当单词而不是文件名）。 */
    public event Action<string?>? QbarDictionaryRequested;
    public event Action? MathBoardRequested;
    public event Action? SettingsRequested;
    public event Action? HelpRequested;
    public event Action? ClipboardHistoryRequested;
    public event Action? ReloadRequested;
    public event Action? ToggleEnabledRequested;
    public event Action? ScreenshotRequested;
    public event Action? MacrosRequested;
    public event Action? LuaScriptsRequested;
    public event Action? GestureMenusRequested;
    public event Action<string>? GestureMenuRequested;

    /// <summary>
    /// 请求打开 Qbar。
    /// </summary>
    /// <param name="engineKey">默认搜索引擎的关键字（如 "zh" = 知乎）；
    /// 为空时用设置里的 default 引擎。绑定按键时可以指定，
    /// 实现"按 Caps+G 打开就是 Google 搜索"。</param>
    public void RequestQbar(string? engineKey = null) => QbarRequested?.Invoke(engineKey);

    /// <summary>
    /// 请求打开 Qbar 并直接挂上某个文件搜索过滤器（wd = 文档 / pic = 图片…）。
    /// </summary>
    /// <param name="filterId">QbarFilter.Id（内置是 builtin.doc 这种，用户项是 user.xxx）</param>
    /// <param name="query">可选：顺手带进去的搜索词</param>
    public void RequestQbarFileSearch(string? filterId, string? query = null)
        => QbarFileSearchRequested?.Invoke(filterId, query);

    /// <summary>
    /// 请求打开 Qbar 并直接查一个词（ol 变成 chip，词已经填好）。
    /// </summary>
    /// <param name="word">要查的词；null = 只打开面板，等用户自己接着打</param>
    public void RequestQbarDictionary(string? word = null)
        => QbarDictionaryRequested?.Invoke(word);
    public void RequestMathBoard() => MathBoardRequested?.Invoke();
    public void RequestOpenSettings() => SettingsRequested?.Invoke();

    /// <summary>请求弹出附加菜单（键位绑定「附加菜单」动作时走这里）。</summary>
    public void RequestAttachMenu() => AttachMenuRequested?.Invoke();

    /// <summary>附加菜单请求的订阅点，由 AppHost 接。</summary>
    public event Action? AttachMenuRequested;

    /// <summary>请求「收藏当前文件」：把前台程序当前打开的文件收进固定项（v1.5.1）。</summary>
    public void RequestFavoriteForegroundFile() => FavoriteForegroundFileRequested?.Invoke();

    /// <summary>请求「打开文件所在位置」：在资源管理器里定位前台程序当前打开的文件（v1.5.1）。</summary>
    public void RequestRevealForegroundFile() => RevealForegroundFileRequested?.Invoke();

    /* 这两个请求都由 AppHost 接：识别要读别的进程、收藏要写设置，
     * 都属于"宿主才知道怎么办"的事，ActionContext 只负责把"用户点了"这件事传出去。 */
    public event Action? FavoriteForegroundFileRequested;
    public event Action? RevealForegroundFileRequested;
    public void RequestHelp() => HelpRequested?.Invoke();
    public void RequestOpenMacros() => MacrosRequested?.Invoke();
    public void RequestOpenLuaScripts() => LuaScriptsRequested?.Invoke();
    public void RequestOpenGestureMenus() => GestureMenusRequested?.Invoke();
    public void RequestGestureMenu(string menuId) => GestureMenuRequested?.Invoke(menuId);
    public void RequestOpenClipboardHistory() => ClipboardHistoryRequested?.Invoke();
    public void RequestReload() => ReloadRequested?.Invoke();
    public void RequestToggleEnabled() => ToggleEnabledRequested?.Invoke();
    public void RequestScreenshot() => ScreenshotRequested?.Invoke();

    /// <summary>
    /// 动作参数没填时的统一出口：提示"缺什么"，并按**失败**返回。
    ///
    /// 为什么单独做这一件事：直达网址 / 直达文件 / 输入文本 / 运行命令
    /// 这些动作以前遇到空参数是 `if (empty) return;` —— 按下去**什么都不发生**，
    /// 而 <c>AppHost.InvokeAction</c> 还会记一条"动作：直达文件"的成功日志。
    /// 用户只会认为"这个键坏了"，日志里也看不出原因。
    ///
    /// 现在：明确告诉用户缺哪一项、去哪儿填，并且返回 false，
    /// 于是失败通知（Toast + 日志）会照常走一遍。
    /// </summary>
    /// <param name="what">缺的是什么，例如 "网址" / "文件夹路径"</param>
    public bool MissingArg(string what)
    {
        Notify(Loc.F("这个键还没填{0}：到「按键映射」里选中它，在右边把它填上", what));
        return false;
    }

    /// <summary>
    /// 播放指定 ID 的宏。arg 可覆盖重复次数（空 = 用宏自身设置）。
    ///
    /// 返回 **是否开始播放**（宏不存在 / 已在播放 / 流程控制配对错误都算没开始）。
    /// 为什么要这个返回值：宏里可以放"执行内置动作"的步骤，其中就有 `macro:xxx`。
    /// 以前这里返回 void、动作目录那边无条件 `return true`，于是
    /// "宏 A 调用一个已被删除的宏 B"会**静默继续**，用户只看到 A 少做了一半。
    /// 现在会如实返回失败，宏在那一格中止并说明原因。
    /// </summary>
    public bool PlayMacro(string macroId, string? arg)
    {
        var macro = Settings().Macros.FirstOrDefault(m => m.Id == macroId);
        if (macro == null)
        {
            Notify("宏不存在，可能已被删除");
            return false;
        }
        int? repeat = int.TryParse(arg, out int n) && n > 0 ? n : null;

        /* 用 TryPlay：流程控制配错时不播，并把原因弹出来。
         * 直接 Play 的话表现是"按了快捷键什么都不发生"，用户只会以为程序坏了。 */
        if (!MacroPlayer.TryPlay(macro, repeat))
        {
            Notify(Loc.F("没能播放宏「{0}」：{1}", macro.Name, MacroPlayer.LastError ?? Loc.T("未知原因")));
            return false;
        }
        Notify(Loc.F("播放宏：{0}", macro.Name));
        return true;
    }

    /// <summary>全局宏播放器（一个进程一个就够了）。</summary>
    public Macro.MacroPlayer MacroPlayer { get; set; } = new();

    private bool _undoNext;
}
