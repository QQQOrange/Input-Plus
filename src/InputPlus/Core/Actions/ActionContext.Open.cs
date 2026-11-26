using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using InputPlus.Core.Input;
using InputPlus.Core.Qbar;
using InputPlus.Core.Settings;
using InputPlus.Core.Text;
using InputPlus.Core.Windows;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Actions;

/// <summary>
/// 打开网址 / 文件夹 / 文件 / 程序，以及截图。与 ActionContext 同属 partial 类。
/// </summary>
public sealed partial class ActionContext
{

    /// <summary>
    /// 仅供自检：拦截"打开网址"这一步，免得测试真的拉起浏览器。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string>? OpenUrlInterceptForTest { get; set; }

    public void OpenUrl(string url) => OpenUrlCore(url, normalize: true);

    /// <summary>
    /// 打开一个**已经构造好**的网址，不再做规范化。
    ///
    /// 「直接搜索」拼出来的 URL 里，关键词已经过 <see cref="Uri.EscapeDataString"/>
    /// 编码（<c>%E6%B5%8B</c> 这类）。如果再过一遍
    /// <see cref="UrlUtil"/> 的规范化，<c>Uri.ToString()</c> 会把百分号转义
    /// 重新解码成可读字符，长关键词还可能被截断 —— 搜索词就丢了。
    /// 所以这条路径要跳过规范化。
    /// </summary>
    public void OpenUrlRaw(string url) => OpenUrlCore(url, normalize: false);

    private void OpenUrlCore(string url, bool normalize)
    {
        string target = normalize ? (UrlUtil.Normalize(url) ?? url) : url;

        if (OpenUrlInterceptForTest != null)
        {
            OpenUrlInterceptForTest(target);
            return;
        }

        try
        {
            /* 不只是 Process.Start —— 那样浏览器会**在后台**打开
             * （前台锁定把新窗口压在任务栏），用户按下直达网址的键
             * 只看到任务栏闪一下。这里和"直达文件夹"走同一套置前逻辑。 */
            bool web = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            WindowService.LaunchInForeground(target,
                web ? WindowService.ShellTargetKind.Url : WindowService.ShellTargetKind.File);

            // 启动成功之后才记：失败会抛异常走 catch，不会留下"打不开"的最近项
            RecentTargets.Record(target);
        }
        catch (Exception ex) { Notify(UiText.Failed("打开网址", ex.Message)); }
    }

    /// <summary>
    /// 仅供自检：拦截"打开文件夹"，免得测试真的弹出资源管理器。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string>? OpenFolderInterceptForTest { get; set; }

    /// <summary>
    /// 在文件资源管理器里打开一个文件夹，并把它带到**最前面**。
    ///
    /// 为什么交给 shell（UseShellExecute）而不是 `explorer.exe "路径"`：
    ///   · 路径里有空格 / 中文 / 方括号时，命令行引号很容易拼错；
    ///   · 用 shell 打开目录是系统标准行为，OneDrive、库、快捷方式都能认；
    ///   · 顺带支持 `%USERPROFILE%`、`~`、`shell:Downloads` 这些写法。
    ///
    /// 为什么还要额外做"置前"：我们是从后台按下快捷键的，前台是别的程序，
    /// Windows 的前台锁定会把新开的资源管理器窗口压在任务栏 ——
    /// 用户看到的就是"文件夹在后台打开了"。具体做法见
    /// <see cref="Windows.WindowService.OpenFolderInForeground"/>。
    ///
    /// 路径不存在时给明确提示，而不是静默失败 ——
    /// 用户最怕的就是"按了没反应，也不知道为什么"。
    /// </summary>
    public void OpenFolder(string path)
    {
        string target = ExpandFolderPath(path);
        if (target.Length == 0) return;

        if (OpenFolderInterceptForTest != null)
        {
            OpenFolderInterceptForTest(target);
            return;
        }

        try
        {
            /* shell:Downloads / shell:Startup 是 Shell 命名空间，不是文件系统路径，
             * Directory.Exists 永远返回 false。之前这里直接判“不存在”，
             * 导致文档里承诺的虚拟目录全部打不开。 */
            bool shellNamespace = target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
            if (!shellNamespace && !Directory.Exists(target))
            {
                Notify(Loc.F("文件夹不存在：{0}", target));
                return;
            }
            InputPlus.Core.Windows.WindowService.OpenFolderInForeground(target);
            RecentTargets.Record(target);
        }
        catch (Exception ex) { Notify(UiText.Failed("打开文件夹", ex.Message)); }
    }

    /// <summary>
    /// 仅供自检：拦截"打开文件"，免得测试真的拉起 Office / 播放器。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string>? OpenFileInterceptForTest { get; set; }

    /// <summary>
    /// 用系统默认程序打开一个**文件**（双击它时会发生什么，这里就做什么）。
    ///
    /// 用 shell 而不是手工查注册表：`.psd`、`.docx`、`.mp4` 该用什么程序打开
    /// 完全由系统关联决定，我们没必要也不应该自己判断。
    /// </summary>
    public void OpenFile(string path)
    {
        string target = ExpandPath(path);
        if (target.Length == 0) return;

        /* 「直达文件」原来只认 File.Exists —— 用户把**文件夹**路径填进来
         * （D:\Downloads\xxx 这种）时必然判成"文件不存在"，可按理它明明在。
         * 这里先认目录：是目录就交给 OpenFolder 那一套（含 shell: 与置前）。
         * 顺带把 shell:Downloads 这类命名空间也交给它，不然同样打不开。
         *
         * 这一步必须排在测试拦截**之前**：Directory.Exists 不启动任何进程，
         * 没有副作用；排在后面的话，自检里就永远走不到这条分支，
         * "填了文件夹却报文件不存在"这种回归也就测不出来。 */
        if (Directory.Exists(target)
            || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            OpenFolder(target);
            return;
        }

        if (OpenFileInterceptForTest != null)
        {
            OpenFileInterceptForTest(target);
            return;
        }

        try
        {
            if (!File.Exists(target))
            {
                Notify(Loc.F("文件不存在：{0}", target));
                return;
            }

            /* 和网址/文件夹一样：打开之后把那个程序带到最前面。
             * 具体是谁打开由文件关联决定（.docx → Word、.mp4 → 播放器），
             * WindowService 会自己查关联再认窗口。 */
            WindowService.LaunchInForeground(target, WindowService.ShellTargetKind.File);
            RecentTargets.Record(target);
        }
        catch (Exception ex) { Notify(UiText.Failed("打开这个文件", ex.Message)); }
    }

    /// <summary>
    /// 把用户填的路径整理成可以直接交给 shell 的形式：
    /// 去引号 / `%ENV%` / `~` / `shell:` / 正斜杠归一 / 去尾部分隔符。
    /// 文件夹、文件、选区里来的路径都走这里，保证"同一个路径只有一种写法"。
    /// 抽成静态是为了让自检能直接验，不必真的打开资源管理器。
    /// </summary>
    public static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string p = path.Trim().Trim('"').Trim();
        if (p.Length == 0) return "";

        // shell: 开头的虚拟路径（shell:Downloads / shell:Startup…）原样交给系统
        if (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return p;

        p = Environment.ExpandEnvironmentVariables(p);

        if (p == "~")
            p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (p.StartsWith("~/") || p.StartsWith("~\\"))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]);

        // 统一分隔符，去掉末尾多余的 \（"C:\" 这种根目录要保留）
        p = p.Replace('/', '\\');
        if (p.Length > 3 && p.EndsWith('\\')) p = p.TrimEnd('\\');
        return p;
    }

    /// <summary>旧名字，保留给已有调用点；行为与 <see cref="ExpandPath"/> 一致。</summary>
    public static string ExpandFolderPath(string? path) => ExpandPath(path);

    /// <summary>
    /// 仅供自检：拦截"运行命令"这一步，免得测试真的拉起进程。
    /// 参数是（程序, 参数, 是否交给 cmd）；置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string, string, bool>? RunCommandInterceptForTest { get; set; }

    /// <summary>一行命令最终会被当成什么 —— 决定"怎么执行"，也决定"要不要拒绝"。</summary>
    internal enum CommandKind
    {
        /// <summary>空参数，什么都不做。</summary>
        Empty,
        /// <summary>目标是文件夹：这不是命令，提示改用「直达文件」。</summary>
        Folder,
        /// <summary>目标是普通文件：同上，不在这里用关联程序打开。</summary>
        File,
        /// <summary>程序（.exe / .com）：直接 CreateProcess，参数原样传。</summary>
        Program,
        /// <summary>其余：内置命令、无扩展名的程序、.bat/.cmd —— 交给 cmd /c。</summary>
        Command,
    }

    /// <summary>
    /// 一行命令拆出来的执行计划。<see cref="Target"/> 是用户原样的整行，
    /// 交给 cmd 时用它（引号、&amp;&amp;、管道都保持原样，不做二次拼接）。
    /// </summary>
    internal readonly record struct CommandPlan(CommandKind Kind, string Exe, string Args, string Target);

    /// <summary>
    /// 判断一行文字是「命令」还是「文件/文件夹」。
    ///
    /// 这是「运行命令」和「直达文件」不再打架的关键：两边都接受一整行文字，
    /// 但只有这里返回 Program/Command 的才会真的启动进程；
    /// 文件、文件夹一律返回自己的类型，由调用方提示"改用直达文件"。
    /// 纯函数（除了 File/Directory.Exists 这一次只读探测），自检可以直接验。
    /// </summary>
    internal static CommandPlan PlanCommand(string command)
    {
        string cmd = (command ?? "").Trim();
        if (cmd.Length == 0) return new CommandPlan(CommandKind.Empty, "", "", "");

        var (exe, args) = SplitCommand(cmd);

        /* shell:Downloads 这类虚拟目录不是文件系统路径，Directory.Exists 永远 false，
         * 但它确实是"文件夹"，不能拿去当命令执行。 */
        if (exe.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return new CommandPlan(CommandKind.Folder, exe, args, cmd);

        if (Directory.Exists(exe)) return new CommandPlan(CommandKind.Folder, exe, args, cmd);
        if (File.Exists(exe) && !IsProgramFile(exe))
            return new CommandPlan(CommandKind.File, exe, args, cmd);

        /* .bat / .cmd 和没有扩展名的命令都走 cmd.exe：前者是脚本，后者是
         * ipconfig / ping / dir 这类由 cmd 解析的东西，CreateProcess 认不出来。 */
        if (IsProgramFile(exe)) return new CommandPlan(CommandKind.Program, exe, args, cmd);
        return new CommandPlan(CommandKind.Command, exe, args, cmd);
    }

    /// <summary>
    /// 把一整行拆成「程序 + 参数」。三种写法都要认：
    ///   · "C:\Program Files\App\app.exe" -x   —— 引号包住程序；
    ///   · C:\Program Files\App\app.exe -x      —— 没引号但路径里有空格（切到第一个 .exe 为止）；
    ///   · notepad.exe D:\笔记.txt                —— 普通程序 + 参数。
    /// 整行本身就是一个存在的程序文件时优先当"只有程序"，否则
    /// C:\Program Files\App\app.exe 会被第一个空格切成 C:\Program。
    /// </summary>
    private static (string Exe, string Args) SplitCommand(string cmd)
    {
        if (!cmd.StartsWith('"'))
        {
            string whole = SafeExpand(cmd);
            if (IsProgramFile(whole) && File.Exists(whole)) return (whole, "");
        }

        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return (SafeExpand(cmd[1..end]), cmd[(end + 1)..].Trim());
            return (SafeExpand(cmd.Trim('"')), "");
        }

        int cut = ProgramExtEnd(cmd);
        if (cut > 0)
        {
            string head = SafeExpand(cmd[..cut].Trim());
            if (File.Exists(head)) return (head, cmd[cut..].Trim());
        }

        int sp = cmd.IndexOf(' ');
        if (sp > 0) return (SafeExpand(cmd[..sp]), cmd[(sp + 1)..].Trim());
        return (SafeExpand(cmd), "");
    }

    /// <summary>字符串里第一个 .exe / .com 的结束位置（没找到返回 -1）。</summary>
    private static int ProgramExtEnd(string s)
    {
        int best = -1;
        foreach (string ext in new[] { ".exe", ".com" })
        {
            int at = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (at > 0 && (best < 0 || at + ext.Length < best)) best = at + ext.Length;
        }
        return best;
    }

    /// <summary>是不是"程序"（能被 CreateProcess 直接执行）。.bat/.cmd 不算，它们要 cmd 解释。</summary>
    private static bool IsProgramFile(string path)
    {
        string ext = "";
        try { ext = Path.GetExtension(path); } catch { return false; }
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>展开 %VAR%；展开失败就按原样用（老配置里可能写着不存在的变量）。</summary>
    private static string SafeExpand(string s)
    {
        try { return Environment.ExpandEnvironmentVariables(s); }
        catch { return s; }
    }

    /// <summary>
    /// 运行一条命令（「系统功能 → 运行命令」）。语义只有"跑命令"，不再兼职打开文件：
    /// 打开文件/文件夹全仓库只剩「直达文件」一个入口，两个功能因此不再重复。
    ///
    /// 执行方式按类型分两种：
    ///   · .exe / .com → ProcessStartInfo(UseShellExecute=false) 直接启动，
    ///     参数原样传过去，不经过 shell 的引号/通配符改写；
    ///   · 其余（内置命令、无扩展名、.bat/.cmd）→ cmd.exe /s /c "整行"。
    /// 为什么不用 UseShellExecute=true 一把梭：那等于把任意文件交回给系统关联，
    /// 填一个 .docx 进来就又变成了「直达文件」—— 这正是要解开的冲突。
    ///
    /// 为什么 cmd 那条不藏窗口：命令的输出就在控制台里，藏起来的话
    /// 命令写错也只会"按了没反应"；控制台本身就是这条动作的反馈。
    /// </summary>
    public void RunProgram(string command)
    {
        var plan = PlanCommand(command);
        switch (plan.Kind)
        {
            case CommandKind.Empty:
                return;

            /* 用户把文件/文件夹填进了「运行命令」：明确告诉他这不是命令、该去哪儿打开，
             * 而不是"帮他打开" —— 那会重新造出第二个「直达文件」。 */
            case CommandKind.Folder:
                Notify(Loc.F("「{0}」是文件夹，不是命令；要打开它请用「直达文件」", plan.Exe));
                return;
            case CommandKind.File:
                Notify(Loc.F("「{0}」是文件，不是命令；要打开它请用「直达文件」", plan.Exe));
                return;
        }

        if (RunCommandInterceptForTest != null)
        {
            RunCommandInterceptForTest(plan.Exe, plan.Args, plan.Kind == CommandKind.Command);
            return;
        }

        try
        {
            ProcessStartInfo psi;
            if (plan.Kind == CommandKind.Command)
            {
                /* /s /c "整行"：/s 让 cmd 只剥掉最外层一对引号，里面用户的引号原样保留。
                 * 不自己拼参数 —— 路径带空格时手工拼引号几乎必错，和用户在 cmd 里
                 * 输入同一行命令的效果一致（路径带空格要自己加引号）。 */
                psi = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    Arguments = "/s /c \"" + plan.Target + "\"",
                };
            }
            else
            {
                /* 工作目录 = exe 自己的目录，由 ExternalLaunch 统一补。
                 * 不给的话子进程会继承我们的当前目录，它写的日志/配置就落到程序目录外面去了
                 * （2026-11-26：wb2api 的 config.json 就是这么堆到 InputPlus 目录里的）。 */
                psi = ExternalLaunch.ForProgram(plan.Exe, plan.Args);
            }

            Process.Start(psi);

            /* 只有"真的启动了一个程序"才算最近打开；走 cmd.exe 的命令行不算 ——
             * 那是一次性动作，记下 cmd.exe 没有任何意义。 */
            if (plan.Kind == CommandKind.Program) RecentTargets.Record(plan.Exe);
        }
        catch (Exception ex) { Notify(UiText.Failed("运行命令", ex.Message)); }
    }

    /// <summary>
    /// 这个目标现在是不是一个文件夹（含 shell: 命名空间）。
    /// 文件与文件夹合并成「直达文件」之后，键帽和速查表要靠它决定说"打开文件夹"还是"打开文件"。
    /// </summary>
    internal static bool IsFolderTarget(string? path)
    {
        string p = ExpandPath(path);
        if (p.Length == 0) return false;
        return p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || Directory.Exists(p);
    }

    public bool Screenshot()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("SnippingTool.exe") { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Notify(UiText.Failed("启动截图工具", ex.Message));
                return false;
            }
        }
    }
}
