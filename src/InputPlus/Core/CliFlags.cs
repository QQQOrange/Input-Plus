namespace InputPlus.Core;

/// <summary>
/// 命令行开关的集中定义。
///
/// 为什么要有这个类：同一批"诊断 / 测试"开关原先在三个地方各写一遍
/// （App 的干跑判定、App 的单实例判定、StartupPassword 的免密码门判定），
/// 三份清单迟早会不一致 —— 实际已经出过一次：
/// `--render` 在"免密码门"清单里、却不在"免单实例"清单里，
/// 于是主程序开着的时候跑 `--render` 会被单实例判掉，**一张图都不出**，
/// 退出码还是 0，看起来像"渲染器坏了"。
/// </summary>
public static class CliFlags
{
    /// <summary>
    /// 诊断 / 测试类开关：跑完即退，不装常驻托盘，也不代表用户想再开一个实例。
    ///
    /// 这些模式：
    ///   · 不弹启动密码（弹了会卡死在无人值守的脚本里）；
    ///   · 不参与单实例互斥（否则主程序一开着，诊断命令就静默失效）。
    /// </summary>
    private static readonly string[] DiagnosticFlags =
    {
        "--selftest", "--render", "--render-diag", "--osd", "--diag",
        "--hwtest", "--webdav-test", "--svg2png", "--reveal", "--pick-test",
        "--help", "--hooktest", "--dump-triggers", "--dump-bindings",
        "--probe-key", "--selection-test", "--gesture-probe", "--gesture-import",
        "--drag-e2e", "--drag-target", "--qbar-query", "--qbar-bench",
        "--index-status", "--settings-dump", "--qbar-command", "--dict", "--holddrag-config",
        "--content-status", "--content-build", "--content-query", "--content-extract",
        "--shell-probe", "--dialogprobe", "--filedialog-e2e", "--suggestions-probe", "--scroll-probe",
        "--foreground-file", "--reveal-file", "--office-recent",
        "--gesture-overlay-diag", "--capture-test",
    };

    /// <summary>
    /// `--help` 打印的用法说明。
    ///
    /// 为什么要有：诊断开关已经十来个（`--pick-test`、`--osd-hold` 这类
    /// 连 README 里都没写），用户报问题时我们常说"跑一下 --selftest"，
    /// 但他不知道该跑哪个、参数是什么。用法表跟着代码走，还能被自检盯住
    /// （见 SelfTest：每个诊断开关都必须出现在这张表里）。
    /// </summary>
    public const string Usage =
        "Input Plus Input Plus —— 命令行用法\n" +
        "\n" +
        "  不带参数                        正常启动（驻留托盘）\n" +
        "  --help                          显示这份说明后退出\n" +
        "\n" +
        "  自检与诊断（跑完即退，不装托盘、不弹启动密码）\n" +
        "  --selftest                      跑全部自检；退出码 0 = 全过（日志在数据目录 selftest.log）\n" +
        "  --only <片段>                    配合 --selftest：只跑用例方法名含该片段的项（例如 --only=Qbar）\n" +
        "  --real-clipboard                配合 --selftest：剪贴板用例改跑真机（默认为内存后端，绝不碰系统剪贴板）\n" +
         "  --diag                          环境诊断（钩子、前台会话、注入链路）；发现问题时退出码非 0\n" +
         "  --hooktest                      只装键盘钩子并自己敲两下，检查钩子是否真的能收到按键\n" +
         "  --dump-triggers                 打印解密后的触发键/层配置（排查触发键不生效时用）\n" +
         "  --dump-bindings                打印每一层的全部按键绑定（排查「没设置成功却被记住」）\n" +
         "  --capture-test                 打开自定义按键捕获窗并自检三路输入来源\n" +
         "  --probe-key <键>               拿当前配置判定一个键：会不会被吞、被哪条规则吞\n" +
        "  --gesture-probe [手势名] [文件]  说清这条手势为什么认不出来：超阈、两条都像、还是没模板\n" +
        "  --gesture-overlay-diag          实测轨迹覆盖层：建窗/首帧要多久、快划有没有上屏、连划两下会不会被上一笔的淡出吃掉、收笔留不留残影（写数据目录 gesture-overlay-diag.txt）\n" +
        "  --gesture-import <文件> [--keep]  把手势表整表换成 WGestures 2 的配置（.wg28bw 或 gestures.json）\n" +
        "  --hwtest                        硬件测试：真的调一次音量与亮度（会出声/变亮）\n" +
        "  --pick-test <结果文件>           验证「选中文字直接搜索」的取词链路，结果写进文件\n" +
        "  --selection-test <结果文件>      在本进程读一次当前选区（UIA/复制各通道），结果写进文件\n" +
        "  --drag-e2e [结果文件] [--live]    端到端验证「按住拖拽」：真装钩子、真发左右键，看窗口动不动\n" +
        "  --drag-target <句柄文件>        给 --drag-e2e 当靶子的窗口进程（由 e2e 自己拉起，不用手敲）\n" +
        "  --holddrag-config               打印「按住拖拽」的运行期真实配置 + 九宫格选边演算（缩放只能单轴时用）\n" +
        "  --shell-probe [文件] [--dismiss] 前台是不是开始菜单/搜索这类 shell 浮层，并试收起一次\n" +
        "  --dialogprobe                  打印当前所有文件对话框的子控件清单与判型结果（写数据目录 logs\\dialog-probe.txt）\n" +
        "  --filedialog-e2e               起一个真对话框，实测一次回填能不能被它接受（写数据目录 logs\\filedialog-e2e.txt）\n" +
        "  --suggestions-probe            真机跑一遍「搜索前建议」采集：各来源条数、首屏耗时、前 20 条（写数据目录 logs\\suggestions-probe.txt）\n" +
        "  --office-recent <文件名>         只在 WPS/Office 的「最近文件」里按名字找真实路径（写 %TEMP%\\clp-office-recent.txt）\n" +
        "  --foreground-file [pid]         认一次「这个程序当前打开的文件」，打印命中/耗时/为什么没命中（写 %TEMP%\\clp-foreground-file.txt）\n" +
        "  --scroll-probe                 实测滚轮钩子→平滑滚动的链路：钩子收到几次、接管几次、为什么放行（写数据目录 logs\\scroll-probe.txt）\n" +
        "  --webdav-test <地址> [用户名] [密码]   实测一次 WebDAV 往返\n" +
        "  --svg2png <源目录> <目标目录>     把 SVG 图标批量转成 PNG（维护图标表时用）\n" +
        "\n" +
        "  界面与出图\n" +
        "  --render <目录>                 把每个页面/浮层渲染成 PNG（用于文档与回归）\n" +
        "  --render-diag <目录>            同上，并输出布局度量与未翻译文案审计\n" +
        "  --osd [百分比]                   显示一次亮度横条（默认 50），配合 --osd-hold 控制停留\n" +
        "  --osd-hold <毫秒>               横条停留时长（默认 1900）\n" +
        "  --qbar                          直接打开 Qbar\n" +
        "  --qbar-query \"doc: 报表\"          跑一次语义层查询，打印翻译各层与耗时（结果写 %TEMP%\\clp-qbar-query.txt）\n" +
        "  --qbar-bench                    跑 20 条基准查询，输出耗时/命中/旧入口比对（写 %TEMP%\\clp-qbar-bench.txt）\n" +
        "  --settings-dump                 只读打印当前读到的设置：过滤器/命令/规则/加密与解锁状态（写 %TEMP%\\clp-settings-dump.txt）\n" +
        "  --qbar-command \"ol hello\"        只解析命令（关键字/路径/占位符展开），不执行（写 %TEMP%\\clp-qbar-command.txt）\n" +
        "  --index-status                  文件索引体检：条数/体积/构建进度/最近提交/watcher/查询 p50-p95（写 %TEMP%\\clp-index-status.txt）\n" +
        "  --content-status                正文索引体检：开关/条数/体积/进度/失败数/索引层状态（写 %TEMP%\\clp-content-status.txt）\n" +
        "  --content-build [--rebuild]     同步建一次正文索引（--rebuild 丢弃现有索引），跑完打印耗时与条数\n" +
        "  --content-query \"词\"             只查正文索引，打印前 50 条命中（写 %TEMP%\\clp-content-query.txt）\n" +
        "  --content-extract <文件>         只跑一个文件的正文抽取，打印字数/耗时/失败原因（写 %TEMP%\\clp-content-extract.txt）\n" +
        "  --dict <词>                      只跑查词链路（欧路/网页词典接口 + 解析），打印释义与耗时（写 %TEMP%\\clp-qbar-dict.txt）\n" +
        "  --reveal <路径>                  按文件关联打开该文件/网址，并检查它是否到了前台\n" +
        "  --reveal-file <路径>             走一次「打开文件所在位置」：父目录 + 选中该文件（写 %TEMP%\\clp-reveal-file.txt）\n" +
        "  --tray                          启动但不显示主窗口（只驻留托盘）\n" +
        "  --trace                         启动并记录每个按键（写进 logs/app.log）\n" +
        "  --updated                       上一次是自动更新拉起的（用于提示「已更新到 x.y.z」）\n" +
        "\n" +
        "数据目录：默认在程序旁边的 InputPlus Data，可用环境变量 INPUTPLUS_DATA 改。\n";

    /// <summary>
    /// 这些模式会经过"输入文本 / 播放宏 / 复制"等真实动作，
    /// 必须切成干跑 —— 否则等于往用户当时的前台窗口里打字。
    ///
    /// 注意**不含** `--hwtest`：它就是要真的调一次音量/亮度。
    /// </summary>
    private static readonly string[] DryRunFlags =
    {
        "--selftest", "--render", "--render-diag", "--diag", "--webdav-test",
    };

    /// <summary>参数里有任意一个诊断开关。</summary>
    public static bool IsDiagnostic(IEnumerable<string>? args) => AnyOf(args, DiagnosticFlags);

    /// <summary>仅供自检：诊断开关清单（用来盯住"用法说明漏了某个开关"）。</summary>
    internal static IReadOnlyList<string> DiagnosticFlagsForTest => DiagnosticFlags;

    /// <summary>仅供自检：干跑开关清单。</summary>
    internal static IReadOnlyList<string> DryRunFlagsForTest => DryRunFlags;

    /// <summary>参数里有任意一个"必须干跑"的开关。</summary>
    public static bool NeedsDryRun(IEnumerable<string>? args) => AnyOf(args, DryRunFlags);

    /// <summary>参数里是否含指定开关（不区分大小写）。</summary>
    public static bool Has(IEnumerable<string>? args, string flag)
        => args != null && args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 取开关的值，两种写法都认：<c>--only=Qbar</c> 与 <c>--only Qbar</c>。
    /// 没写值、或值被别的开关顶掉（<c>--only --selftest</c>）时返回 null。
    ///
    /// 为什么两种都认：脚本里 `--only=Qbar` 少一个括号不嵌套，但人在命令行里
    /// 习惯敲空格；只支持一种迟早会被写错一次而静默跑成全量。
    /// </summary>
    public static string? ValueOf(IEnumerable<string>? args, string flag)
    {
        if (args == null) return null;

        string prefix = flag + "=";
        bool sawFlag = false;

        foreach (string raw in args)
        {
            if (raw == null) continue;

            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string inline = raw.Substring(prefix.Length).Trim();
                return inline.Length == 0 ? null : inline;
            }

            if (sawFlag)
            {
                if (raw.StartsWith("--", StringComparison.Ordinal)) return null;
                return raw.Trim();
            }

            if (raw.Equals(flag, StringComparison.OrdinalIgnoreCase)) sawFlag = true;
        }

        return null;
    }

    private static bool AnyOf(IEnumerable<string>? args, string[] flags)
        => args != null && args.Any(a => flags.Contains(a, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 打印用法。
    ///
    /// 本程序是 WinExe（没有自己的控制台），`Console.WriteLine` 默认**什么也看不到** ——
    /// 从 cmd 里跑 `--help` 的人会以为命令没执行。所以先 AttachConsole 到父进程的控制台，
    /// 实在没有（双击运行）就把用法写进数据目录的 usage.txt，并在日志里留一行路径。
    /// </summary>
    public static void PrintUsage()
    {
        try { AttachConsole(-1); } catch { /* 没有父控制台：正常情况（双击启动） */ }

        try { Console.WriteLine(Usage); } catch { }

        try
        {
            string path = System.IO.Path.Combine(AppPaths.DataDir, "usage.txt");
            System.IO.File.WriteAllText(path, Usage, System.Text.Encoding.UTF8);
            CoreLog.Info("用法已写入：" + path);
        }
        catch (Exception e)
        {
            CoreLog.Warn("写 usage.txt 失败：" + e.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
