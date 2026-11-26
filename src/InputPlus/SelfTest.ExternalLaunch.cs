using System.Diagnostics;
using System.IO;
using InputPlus.Core.Windows;

namespace InputPlus;

/// <summary>
/// 外部程序启动（<see cref="ExternalLaunch"/>）的自检。
///
/// 这一组的存在理由很具体：**子进程的工作目录继承父进程**，
/// 而我们是个常驻程序，当前目录常年停在安装目录。忘了给工作目录，
/// 被拉起来的程序就会把配置读到/写到我们脚边 ——
/// 2026-11-26 的 wb2api 事故（键启动读不到自己的 config.json，双击却正常）就是这么发生的。
/// 所以这里钉死三件事：判断规则、取值结果、以及"绝不等于我们的当前目录"。
/// </summary>
public static partial class SelfTest
{
    private static void TestExternalLaunch()
    {
        /* ── 1. 什么算"本机路径" ──
         * 只按形状判断，不碰磁盘：带分隔符才算路径。 */
        Check("路径判定：带盘符的完整路径", ExternalLaunch.IsLocalPath(@"C:\Windows\notepad.exe"));
        Check("路径判定：相对路径", ExternalLaunch.IsLocalPath(Path.Combine("dist", "app.exe")));
        Check("路径判定：网址不是路径", !ExternalLaunch.IsLocalPath("https://github.com/QQQOrange/Input-Plus"));
        Check("路径判定：协议不是路径", !ExternalLaunch.IsLocalPath("shell:Downloads"));
        Check("路径判定：PATH 里的裸命令名不是路径", !ExternalLaunch.IsLocalPath("notepad"));
        Check("路径判定：带 .exe 的裸命令名不是路径", !ExternalLaunch.IsLocalPath("explorer.exe"));

        /* ── 2. 工作目录取值 ──
         * 文件 → 它自己的目录；其余（目录/网址/协议/裸命令名）一律"不设"。 */
        CheckEqual("工作目录：exe 取所属目录",
            ExternalLaunch.WorkDirFor(@"C:\Windows\notepad.exe"), @"C:\Windows");
        CheckEqual("工作目录：system32 里的程序取它自己的目录",
            ExternalLaunch.WorkDirFor(@"C:\Windows\System32\cmd.exe"), @"C:\Windows\System32");
        CheckEqual("工作目录：打开一个目录时不设", ExternalLaunch.WorkDirFor(@"C:\Windows"), "");
        CheckEqual("工作目录：网址不设", ExternalLaunch.WorkDirFor("https://example.com/a/b"), "");
        CheckEqual("工作目录：协议不设", ExternalLaunch.WorkDirFor("ms-settings:"), "");
        CheckEqual("工作目录：命名空间不设", ExternalLaunch.WorkDirFor("shell:Downloads"), "");
        CheckEqual("工作目录：裸命令名不设", ExternalLaunch.WorkDirFor("notepad.exe"), "");
        CheckEqual("工作目录：空输入安全", ExternalLaunch.WorkDirFor("   "), "");

        /* ── 3. 拼出来的 ProcessStartInfo ── */
        var file = ExternalLaunch.ForTarget(@"C:\Windows\notepad.exe");
        CheckEqual("shell 打开文件：走 shell", file.UseShellExecute, true);
        CheckEqual("shell 打开文件：工作目录是它的目录", file.WorkingDirectory, @"C:\Windows");
        Check("shell 打开文件：不提权", file.Verb.Length == 0);

        var admin = ExternalLaunch.ForTarget(@"C:\Windows\notepad.exe", runAs: true);
        CheckEqual("shell 提权启动：Verb=runas", admin.Verb, "runas");
        CheckEqual("shell 提权启动：照样带工作目录", admin.WorkingDirectory, @"C:\Windows");

        var arg = ExternalLaunch.ForTarget(@"C:\Windows\notepad.exe", "readme.txt");
        CheckEqual("shell 带参数：参数原样写上", arg.Arguments, "readme.txt");

        var url = ExternalLaunch.ForTarget("https://example.com/");
        CheckEqual("打开网址：不设工作目录", url.WorkingDirectory, "");

        var prog = ExternalLaunch.ForProgram(@"C:\Windows\System32\cmd.exe", "/c echo hi");
        CheckEqual("直接建进程：不走 shell", prog.UseShellExecute, false);
        CheckEqual("直接建进程：工作目录是 exe 的目录", prog.WorkingDirectory, @"C:\Windows\System32");
        CheckEqual("直接建进程：参数原样", prog.Arguments, "/c echo hi");

        var overridden = ExternalLaunch.ForProgram(@"C:\Windows\System32\cmd.exe", "", @"C:\Windows");
        CheckEqual("直接建进程：调用方给的工作目录优先", overridden.WorkingDirectory, @"C:\Windows");

        /* ── 4. 回归守卫：绝不能再把我们的当前目录传下去 ──
         * 这条是本次修复的靶心。当前目录恰好等于目标目录时不算失败（那是等号成立，不是串味）。 */
        string target = @"C:\Windows\System32\cmd.exe";
        string ours = Environment.CurrentDirectory.TrimEnd('\\');
        string theirs = Path.GetDirectoryName(target)!.TrimEnd('\\');
        Check("回归守卫：目标的工作目录不是我们的当前目录",
            string.Equals(ours, theirs, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ExternalLaunch.ForTarget(target).WorkingDirectory, ours,
                StringComparison.OrdinalIgnoreCase),
            $"当前目录={ours}，目标目录={theirs}");
    }
}
