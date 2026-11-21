using System;
using System.Diagnostics;
using InputPlus.Core.Qbar.Search;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 一次查词落地之后的结果。
/// <para><c>Target</c>：真的去查的地方 —— 外部程序路径，或者网页地址。</para>
/// <para><c>External</c>：true = 送进欧路词典；false = 打开了网页词典。</para>
/// </summary>
internal readonly record struct DictionaryLaunchResult(bool Ok, bool External, string Target, string Message);

/// <summary>
/// 查词的**唯一出口**：装了欧路就把词送过去（出厂参数 <c>-w "{query}"</c>），
/// 没装就打开网页词典。
///
/// 为什么要单独一层：面板里按回车（ol 命令）、「直接搜索」动作、
/// 「Qbar 搜索」动作，三条路最后都得落到同一个地方。以前面板里把 <c>-w</c>
/// 写死在代码里、设置里的参数框却是空的 —— 用户看不出到底怎么配的，
/// 也没法把参数换成 <c>--word</c> 之类的写法。现在程序、参数、工作目录、
/// 提权、静默全部走 <see cref="QbarCommandStore.Execute"/>，
/// 只有"程序从哪来"这一件事在这里决定。
/// </summary>
internal static class DictionaryLauncher
{
    /// <summary>
    /// 自检用：替换"去哪个程序"这一步。真机上 <see cref="EudicLocator"/> 会
    /// 找到用户装的欧路，测试里绝对不能真把它拉起来（会抢焦点、弹窗）。
    /// </summary>
    internal static Func<string?>? FindProgramForTest { get; set; }

    /// <summary>自检用：替换"启动外部程序"这一步（避免测试真起进程）。</summary>
    internal static Action<QbarCommand, string>? ExecuteForTest { get; set; }

    /// <summary>自检用：替换"打开网页"这一步（避免测试真开浏览器）。</summary>
    internal static Action<string>? OpenWebForTest { get; set; }

    /// <summary>
    /// 查一个词。
    /// </summary>
    /// <param name="word">要查的词（调用方已经 trim 过）</param>
    /// <param name="template">ol 命令的当前配置（可为 null = 全用出厂值）</param>
    /// <param name="currentFolder">展开 {current_folder} 用</param>
    /// <param name="actionPath">展开 {action_path} 用</param>
    public static DictionaryLaunchResult Launch(string word, QbarCommand? template,
                                               string? currentFolder = null, string? actionPath = null)
    {
        string query = (word ?? "").Trim();
        if (query.Length == 0)
            return new DictionaryLaunchResult(false, false, "",
                Loc.T("先选中要查的词，或者输入要查的词再回车"));

        /* 程序从哪来：用户自己指定的优先（设置里"选择程序…"/"点选窗口"/手填），
           否则交给定位器（PATH → App Paths → 开始菜单快捷方式 → 常见目录）。 */
        string program = (template?.Path ?? "").Trim();
        if (program.Length == 0)
            program = (FindProgramForTest != null ? FindProgramForTest() : EudicLocator.Find()) ?? "";

        if (program.Length > 0)
        {
            QbarCommand external = template?.Clone() ?? new QbarCommand
            {
                Id = QbarCommandStore.BuiltinDictId,
                Keyword = QbarCommandStore.DictKeyword,
                Title = Loc.T("查词"),
            };
            external.Path = program;

            /* 旧配置里 ol 的参数是空的（1.7.2 之前出厂没填）：
               留空时补默认模板，否则升级上来的用户看到的只是"欧路打开了个空窗口"。 */
            if (string.IsNullOrWhiteSpace(external.Parameter))
                external.Parameter = QbarCommandStore.DefaultDictParameter;

            if (ExecuteForTest != null)
            {
                ExecuteForTest(external, query);
                return new DictionaryLaunchResult(true, true, program, "");
            }

            QbarCommandResult run = QbarCommandStore.Execute(external, query, currentFolder, actionPath);
            /* 失败文案已经分好层（找不到程序 / 提权被取消 / 路径非法），
               原样往上传，别在这里糊成一句"查词失败"。 */
            return new DictionaryLaunchResult(run.Ok, true, program, run.Message);
        }

        string url = DictionaryUrl(query);
        try
        {
            if (OpenWebForTest != null) OpenWebForTest(url);
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return new DictionaryLaunchResult(true, false, url,
                Loc.T("本机没有装欧路词典，已在浏览器里打开网页词典：") + query);
        }
        catch (Exception ex)
        {
            CoreLog.Error("打开网页词典失败", ex);
            return new DictionaryLaunchResult(false, false, url, Loc.T("打开词典失败：") + ex.Message);
        }
    }

    /// <summary>网页词典地址（欧路网页版）。抽出来只为了自检能直接比对。</summary>
    internal static string DictionaryUrl(string word) =>
        "https://dict.eudic.net/dicts/en/" + Uri.EscapeDataString((word ?? "").Trim());

    /// <summary>
    /// 从命令表里取"查词"那一条：先按 Id 认，再按关键字认 ——
    /// 用户把 ol 改叫"词典"之后，Id 还是 builtin.dict，绑定与动作都不能失效。
    /// </summary>
    public static QbarCommand? EffectiveCommand(System.Collections.Generic.IEnumerable<QbarCommand>? user)
    {
        var table = QbarCommandStore.Effective(user);
        foreach (QbarCommand c in table)
        {
            if (string.Equals(c.Id, QbarCommandStore.BuiltinDictId, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        foreach (QbarCommand c in table)
        {
            if (string.Equals((c.Keyword ?? "").Trim(), QbarCommandStore.DictKeyword, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return null;
    }
}
