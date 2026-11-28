using System;

namespace InputPlus.Core.Windows;

/// <summary>
/// 「打开文件所在位置」要走哪条通道（v1.5.1）。
///
/// 三条路的能力不一样，所以顺序要看身份：
///   · <see cref="InProcSelect"/>：in-proc 调 <c>SHOpenFolderAndSelectItems</c>。
///     最正 —— 复用已经打开的同名文件夹窗口（不闪新窗）、自带选中、对库文件夹与
///     网络位置的处理都由 shell 负责。缺点是**提权时可能被 shell 拒绝**
///     （高完整性客户端去驱动 medium IL 的 shell 对象）。
///   · <see cref="ShellLaunch"/>：让 <c>Shell.Application</c>（out-of-proc，挂在 shell 里）
///     去 CreateProcess("explorer.exe", "/select,\"路径\""）。子进程是 medium IL，
///     不会继承我们的管理员令牌；文件夹也已打开时同样会复用。
///   · <see cref="DirectExplorer"/>：我们自己 <c>Process.Start("explorer.exe", …)</c>。
///     最后手段：提权时会继承提升令牌（explorer 多数情况下会转给已在跑的 shell 然后自退，
///     但这不是契约）。
/// </summary>
internal enum RevealChannel
{
    /// <summary>in-proc 选中（默认，未提权时首选）。</summary>
    InProcSelect = 0,

    /// <summary>借 shell 之手启动 explorer（提权时首选）。</summary>
    ShellLaunch = 1,

    /// <summary>自己启动 explorer（兜底）。</summary>
    DirectExplorer = 2,
}

/// <summary>
/// 提权状态下"打开位置"走哪条路 —— 纯函数，自检可以直接喂 <c>true</c>/<c>false</c>，
/// 不需要真的把进程提权（那会弹 UAC，把无人值守的自检卡死）。
///
/// 为什么提权时把"借 shell 之手"提到最前：不是因为它更准，而是因为**先保证不越权**。
/// 提权进程里任何一次 <c>ShellExecute(文件路径)</c> 都会变成"用管理员身份打开这个文档"，
/// explorer 一旦继承令牌，后续用户在那个窗口里双击的东西也可能跟着提权。
/// 功能和风险之间，这里选风险。
/// </summary>
internal static class RevealStrategy
{
    /// <summary>按身份给出尝试顺序（每条失败才走下一条）。</summary>
    internal static RevealChannel[] Plan(bool elevated) => elevated
        ? new[] { RevealChannel.ShellLaunch, RevealChannel.InProcSelect, RevealChannel.DirectExplorer }
        : new[] { RevealChannel.InProcSelect, RevealChannel.ShellLaunch, RevealChannel.DirectExplorer };

    /// <summary>通道的中文名（日志与提示用，别在调用点再拼一套）。</summary>
    internal static string Name(RevealChannel channel) => channel switch
    {
        RevealChannel.InProcSelect => "SHOpenFolderAndSelectItems",
        RevealChannel.ShellLaunch => "Shell.Application",
        _ => "explorer.exe",
    };

    /// <summary>
    /// 这条通道这次该不该用（v1.7.10）。
    ///
    /// 唯一的例外是 <see cref="RevealChannel.InProcSelect"/>：它调的是
    /// <c>SHOpenFolderAndSelectItems</c>，语义是"在**父目录**里选中这一项"。
    /// 目标本身就是目录时（"只开目录"模式 / "打开程序所在文件夹"那条退路），
    /// 它会打开**上一级**并把目录选中 —— 实测 <c>D:\software\Kelivo</c> 开出来的是
    /// <c>D:\software</c>。所以"不选中"的场合一律跳过它，改用
    /// <c>explorer.exe "&lt;目录&gt;"</c> 那两条（打的就是目录本身，
    /// 提权时也仍然借 shell 之手，不继承令牌）。
    /// </summary>
    internal static bool ShouldTry(RevealChannel channel, bool select)
        => channel != RevealChannel.InProcSelect || select;
}

/// <summary>
/// 一次"要打开哪里"的决定。全部由 <see cref="RevealPath.Plan"/> 这个纯函数算出来 ——
/// 这样"文件被删了要上溯到哪一层""提权时用哪种写法"都能在自检里断言，
/// 不必真的拉起资源管理器。
/// </summary>
/// <param name="Path">要交给 shell 的路径（已剥 <c>\\?\</c>、已确认存在）。</param>
/// <param name="Folder">要打开的那个目录。</param>
/// <param name="Select">true = 在 <see cref="Folder"/> 里选中 <see cref="Path"/>。</param>
/// <param name="WentUp">原文件已经不在了，目录是逐级上溯找出来的。</param>
/// <param name="Reachable">false = 连一个存在的上级目录都没有（U 盘拔了、网络断了）。</param>
internal readonly record struct RevealTarget(
    string Path,
    string Folder,
    bool Select,
    bool WentUp,
    bool Reachable)
{
    /// <summary>一条都不可达时的占位。</summary>
    internal static RevealTarget Unreachable(string path) => new(path, "", false, true, false);
}

/// <summary>
/// 「打开文件所在位置」的路径处理（v1.5.1，纯函数）。
///
/// 三件必须做的事，缺一个都会踩：
///   ① 剥掉 <c>\\?\</c> 前缀 —— shell 不认它，<c>SHParseDisplayName</c> 直接失败；
///      但**存在性判断要用带前缀的形式**（那是 <c>System.IO</c> 认的），
///      所以清理与探测分开：先按原样判存在，再清理后交给 shell。
///   ② 文件被删/改名 → 从父目录逐级上溯到第一个存在的目录，打开那个，
///      而不是弹一个空窗口或者报错。
///   ③ 网络路径离线时 explorer 会同步阻塞几秒 —— 那是调用方的事（放后台 + 超时），
///      这里只负责把路径写对：整体加引号，路径里的逗号空格都安全。
///
/// 为什么不让调用点各自 <c>Path.GetDirectoryName</c> 一遍：
/// 仓库里已经有三个地方在拼 <c>explorer.exe /select,</c>，各拼各的必然有一条漏引号。
/// </summary>
internal static class RevealPath
{
    /// <summary>UNC 前缀 <c>\\?\UNC\</c> 的长度。</summary>
    private const int UncPrefixLength = 8;

    /// <summary>
    /// 清洗成 shell 认的形式：去引号、剥长路径前缀、统一反斜杠、去尾部斜杠
    /// （盘符根 <c>C:\</c> 保留）。
    /// </summary>
    internal static string Clean(string? raw)
    {
        string p = (raw ?? "").Trim().Trim('"').Trim();
        if (p.Length == 0) return "";

        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[UncPrefixLength..];      // \\?\UNC\srv\share → \\srv\share
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
            p = p[4..];                            // \\?\C:\a → C:\a
        else if (p.StartsWith(@"\??\", StringComparison.Ordinal))
            p = p[4..];                            // 偶尔从别处抄来的 NT 形式

        p = p.Replace('/', '\\');
        if (p.Length > 3 && p.EndsWith('\\')) p = p.TrimEnd('\\');
        return p;
    }

    /// <summary>
    /// 去重用的路径键：小写 + 去尾斜杠 + 剥长路径前缀。
    ///
    /// 同一个文件在收藏表里只能有一条 —— 用户从 WPS 收一次、从记事本再收一次，
    /// 两次拿到的路径可能一个带 <c>\\?\</c>、一个大写盘符，按字符串直接比就成了两条。
    /// </summary>
    internal static string CompareKey(string? path)
    {
        string p = Clean(path);
        return p.Length == 0 ? "" : p.ToLowerInvariant();
    }

    /// <summary>父目录（已清洗）；已经是根目录时返回空串。</summary>
    internal static string ParentOf(string path)
    {
        try
        {
            string? parent = Path.GetDirectoryName(path);
            return parent == null ? "" : Clean(parent);
        }
        catch { return ""; }
    }

    /// <summary>
    /// 决定这次要打开哪里。
    ///
    /// <paramref name="fileExists"/> / <paramref name="dirExists"/> 可注入：
    /// 自检要造"文件已删、上级还在""整条链都不可达"这些场景，不能真去删盘上的东西。
    /// 传 null 就走真实文件系统（失败一律当"不存在"，绝不把异常漏到 UI）。
    /// </summary>
    /// <param name="openFolderOnly">设置里选了「只开目录」时为 true：不选中、不 /select。</param>
    internal static RevealTarget Plan(
        string? rawPath,
        bool openFolderOnly,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? dirExists = null)
    {
        /* 先按**原样**判存在（带 \\?\ 的形式 System.IO 才认），再清洗给 shell 用。 */
        string probe = (rawPath ?? "").Trim().Trim('"').Trim();
        if (probe.Length == 0) return RevealTarget.Unreachable("");

        string file = probe;
        bool isFile = fileExists != null ? fileExists(file) : SafeFileExists(file);
        bool isDir = !isFile && (dirExists != null ? dirExists(file) : SafeDirExists(file));

        string cleaned = Clean(probe);

        /* 传进来的就是个目录（不该发生，但别把目录当成"文件已删"）：直接开它。 */
        if (isDir)
            return new RevealTarget(cleaned, cleaned, Select: false, WentUp: false, Reachable: true);

        if (isFile)
        {
            string folder = ParentOf(probe);
            bool select = !openFolderOnly;
            return new RevealTarget(cleaned, folder, select, WentUp: false, Reachable: folder.Length > 0);
        }

        /* 文件已经不在（删了 / 改名了 / 盘还没就绪）：从父目录逐级上溯。
         * 用户点"这个文件在哪"，得到的应该是"它原来那一带"，而不是一句错误。 */
        string dir = ParentOf(probe);
        while (dir.Length > 0 && !(dirExists != null ? dirExists(dir) : SafeDirExists(dir)))
            dir = ParentOf(dir);

        return dir.Length == 0
            ? RevealTarget.Unreachable(cleaned)
            : new RevealTarget(cleaned, dir, Select: false, WentUp: true, Reachable: true);
    }

    /// <summary>
    /// 交给 <c>explorer.exe</c> 的参数：选中要 <c>/select,"完整路径"</c>，
    /// 只开目录就一个整体加引号的路径。
    ///
    /// 引号是必须的：<c>/select,</c> 后面走的是命令行，路径里的逗号、空格、括号
    /// 全都不需要额外转义 —— 只要整体被一对双引号包住。
    /// </summary>
    internal static string ExplorerArguments(in RevealTarget target)
    {
        if (target.Folder.Length == 0) return "";

        /* 选中：/select,"完整路径"。**只给这一个参数** ——
         * 再额外塞一个目录进去会让 explorer 开两个东西（目录一个、选中一个）。
         * 路径整体一对引号：逗号、空格、括号、中文全都不用再转义。 */
        if (target.Select && target.Path.Length > 0) return "/select,\"" + target.Path + "\"";

        return "\"" + target.Folder + "\"";
    }

    /// <summary>取最后一段（文件名 / 目录名）；取不到返回空串。</summary>
    internal static string LeafOf(string? path)
    {
        try
        {
            string p = Clean(path).TrimEnd('\\');
            string leaf = Path.GetFileName(p);
            return leaf ?? "";
        }
        catch { return ""; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }
}
