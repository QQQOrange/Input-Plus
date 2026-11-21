using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using InputPlus.Core;
using InputPlus.Core.Actions;
using InputPlus.Core.Icons;
using InputPlus.Core.Input;
using InputPlus.Core.Interop;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;
using InputPlus.Core.Text;
using InputPlus.Core.Update;
using InputPlus.Core.Windows;

namespace InputPlus;

/// <summary>
/// 自检：不依赖界面，验证核心逻辑。
/// 用 `InputPlus.exe --selftest` 运行，退出码 0 表示全部通过。
/// </summary>
public static partial class SelfTest
{

    /* Qbar 面板、搜索引擎、网址识别。
     * 与 SelfTest.cs 同属 partial 类，只按业务域分文件。 */

    /// <summary>
    /// 默认引擎表：国内用拼音首字母、国外用英文前两个字母，且都带图标。
    /// 这些约定一旦被改乱，用户会记不住关键字，所以专门校验。
    /// </summary>
    private static void TestSearchEngines()
    {
        var settings = DefaultSettings.Create();
        var engines = settings.Search
            .Where(x => !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Check("引擎数量 >= 25", engines.Count >= 25, engines.Count.ToString());

        // 国内引擎：关键字应为拼音首字母（小写字母）
        var domestic = new (string Key, string Name)[]
        {
            ("bd", "百度"), ("tb", "淘宝"), ("zh", "知乎"), ("bilis", "哔哩哔哩搜索"),
            ("wx", "微信"), ("wb", "微博"), ("jd", "京东"), ("xhs", "小红书"),
            ("dy", "抖音"), ("db", "豆瓣"), ("csdn", "CSDN"), ("juejin", "掘金"),
            ("gitee", "Gitee"), ("pdd", "拼多多"), ("tx", "腾讯视频"), ("iqy", "爱奇艺"),
        };
        int domesticOk = 0;
        foreach (var (key, name) in domestic)
        {
            var hit = engines.FirstOrDefault(e => e.Key == key);
            if (hit == null) continue;
            domesticOk++;
            if (!string.Equals(hit.Comment, name, StringComparison.Ordinal))
                CoreLog.Warn($"引擎 {key} 的备注应为 {name}，实际 {hit.Comment}");
        }
        CheckEqual("国内引擎全部存在", domesticOk, domestic.Length);

        // 国外引擎：关键字应为英文前两个字母
        var foreign = new (string Key, string Name)[]
        {
            ("go", "Google"), ("bi", "Bing"), ("gh", "GitHub"),
            ("wi", "维基百科"), ("yo", "YouTube"), ("re", "Reddit"),
            ("st", "Startpage"), ("md", "MDN"), ("am", "Amazon"),
            ("fa", "Facebook"), ("np", "npm"), ("nu", "NuGet"),
        };
        int foreignOk = 0;
        foreach (var (key, name) in foreign)
        {
            var hit = engines.FirstOrDefault(e => e.Key == key);
            if (hit == null) continue;
            foreignOk++;
            if (!string.Equals(hit.Comment, name, StringComparison.Ordinal))
                CoreLog.Warn($"引擎 {key} 的备注应为 {name}，实际 {hit.Comment}");
        }
        CheckEqual("国外引擎全部存在", foreignOk, foreign.Length);

        // 每个引擎都要有图标：内置图标是打包资源（EngineIcon），
        // 不再是磁盘文件，所以这里检查 EngineIcon 非空且能真正加载出来。
        int withIcon = engines.Count(e =>
            !string.IsNullOrEmpty(e.EngineIcon) &&
            Core.Icons.IconCache.FromResource(e.EngineIcon) != null);
        CheckEqual("每个引擎都配了可用图标", withIcon, engines.Count);

        // 关键字不能重复（否则 Qbar 无法区分该用哪个引擎）
        int dup = engines.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() > 1);
        CheckEqual("引擎关键字无重复", dup, 0);

        // 每个引擎链接都要带 {q} 占位符
        int withQ = engines.Count(e => e.Value.Contains("{q}"));
        CheckEqual("所有引擎链接都含 {q}", withQ, engines.Count);

        // 引擎表版本号必须是有效数字（用于老用户自动补齐缺失引擎）
        Check("引擎表版本号 >= 1", DefaultSettings.BuiltinEnginesVersion >= 1,
            DefaultSettings.BuiltinEnginesVersion.ToString());

        // 内置图标必须优先于旧的磁盘图标路径。
        //
        // 这是"换了图标却没生效"的回归点：老用户的条目上还挂着早先版本生成的
        // .svg 路径，若优先级搞反，界面就会继续显示旧图标。
        var probeEntry = new QEntry
        {
            Key = "bd",
            Comment = "百度",
            Value = "https://www.baidu.com/s?wd={q}",
            IconPath = @"C:\__not_exist__\old.svg",   // 旧的、已失效的路径
            EngineIcon = "bai_du",
        };
        var picked = Core.Icons.IconCache.ForEntry(probeEntry);
        Check("内置图标优先于旧图标路径", picked != null);
        Check("取到的是内置图标（尺寸 64）",
            picked is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 64 },
            (picked as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth.ToString() ?? "null");
    }

    private static void TestEvaluator()
    {
        CheckEqual("1+2*3 = 7", Eval("1+2*3"), "7");
        CheckEqual("(1+2)*3 = 9", Eval("(1+2)*3"), "9");
        CheckEqual("0.1+0.2 = 0.3", Eval("0.1+0.2"), "0.3");
        CheckEqual("2^10 = 1024", Eval("2^10"), "1024");
        CheckEqual("10/4 = 2.5", Eval("10/4"), "2.5");
        CheckEqual("sqrt(16) = 4", Eval("sqrt(16)"), "4");
        CheckEqual("max(3,7) = 7", Eval("max(3,7)"), "7");
        CheckEqual("pi 近似", Eval("pi").StartsWith("3.14159"), true);
        CheckEqual("全角符号 ×", Eval("6×7"), "42");
        Check("除零报错", !ExpressionEvaluator.TryEvaluate("1/0", out _, out _));
        Check("非法表达式报错", !ExpressionEvaluator.TryEvaluate("1+", out _, out _));
        CheckEqual("负数", Eval("-5+2"), "-3");
        CheckEqual("科学计数法", Eval("1e3+1"), "1001");

        /* 深层嵌套必须被拒绝而不是 Recursion → StackOverflow（后者 catch 不住，会杀进程）。 */
        string deep = new string('(', 10000) + "1" + new string(')', 10000);
        Check("超深括号不崩溃且被拒绝", !ExpressionEvaluator.TryEvaluate(deep, out _, out _));
        Check("超长一元运算不崩溃且被拒绝",
            !ExpressionEvaluator.TryEvaluate(new string('-', 10000) + "1", out _, out _));
        Check("超长表达式被拒绝",
            !ExpressionEvaluator.TryEvaluate(new string('1', ExpressionEvaluator.MaxExpressionLength + 1), out _, out _));
    }

    private static string Eval(string expr) =>
        ExpressionEvaluator.TryEvaluate(expr, out double v, out _) ? ExpressionEvaluator.Format(v) : "<error>";

    private static void TestQbar()
    {
        var settings = DefaultSettings.Create();
        var qbar = new QbarService(() => settings, () => new List<string> { "hello world", "another clip" });

        CheckEqual("Qbar 中文缩写", QbarTextMatch.Initials("微信"), "WX");
        CheckEqual("Qbar 英文单词缩写", QbarTextMatch.Initials("Control Panel"), "CP");
        Check("Qbar 缩写命中中文", QbarTextMatch.Matches("微信", "wx"));
        Check("Qbar 缩写命中英文", QbarTextMatch.Matches("Control Panel", "cp"));
        Check("Qbar 单字母不走缩写", !QbarTextMatch.Matches("微信", "w"));

        var math = qbar.Query("=1+2*3");
        Check("= 开头直接给出计算结果", math.Count > 0 && math[0].Kind == QbarKind.Math, math.FirstOrDefault()?.Title);
        CheckEqual("计算结果正确", math[0].Payload, "7");

        var search = qbar.Query("bd 天气");
        Check("搜索指令命中", search.Count > 0 && search[0].Kind == QbarKind.Search);
        Check("搜索链接带上了关键词", search[0].Payload.Contains("weather") ||
                                      search[0].Payload.Contains(Uri.EscapeDataString("天气")));

        var cmd = qbar.Query("/set");
        Check("内置命令补全", cmd.Any(r => r.Kind == QbarKind.Command && r.Payload == "openSettings"));

        var fallback = qbar.Query("一个不存在的关键词");
        Check("兜底默认搜索", fallback.Count > 0 && fallback.Last().Kind == QbarKind.Search);

        /* 备注的显示规则：
         *   · 内置"默认搜索"那条是程序生成的数据 → 跟着界面语言走（英文界面不该出现中文）；
         *   · 用户自己填的备注是**数据** → 一个字都不能改（翻译用户内容是错的）。
         * 这两条以前混在一起：都是直接返回 Comment。 */
        var defaultEntry = DefaultSettings.Create().Search.First(e => e.Key == "default");
        CheckEqual("内置默认搜索引擎的显示名走语言包",
            defaultEntry.DisplayName, Loc.T("默认搜索"));
        CheckEqual("用户自定义备注原样显示",
            new QEntry { Key = "bd", Comment = "百度一下" }.DisplayName, "百度一下");
        CheckEqual("没有备注时用关键字兜底",
            new QEntry { Key = "gh", Comment = "" }.DisplayName, "gh");

        var clip = qbar.Query("hello");
        Check("剪贴板历史可被搜到", clip.Any(r => r.Kind == QbarKind.Clipboard));

        var empty = qbar.Query("");
        Check("空输入给出默认建议", empty.Count > 0);

        Check("算式识别", QbarService.LooksLikeMath("12*34+5"));
        Check("普通单词不误判为算式", !QbarService.LooksLikeMath("hello world"));
        Check("带等号算式识别", QbarService.LooksLikeMath("1+1="));

        /* 快捷命令：输入关键字后按空格就该直接执行，
         * 而不是先变成标签、再让用户按一次回车。 */
        var quickCmd = qbar.Query("cmd");
        Check("cmd 命中快捷命令", quickCmd.Count > 0 && quickCmd[0].Kind == QbarKind.Run,
            quickCmd.FirstOrDefault()?.Title);
        CheckEqual("cmd 直接启动 cmd.exe", quickCmd[0].Payload, "cmd.exe");
        var quickHosts = qbar.Query("hosts");
        Check("hosts 带管理员参数", quickHosts.Count > 0 && quickHosts[0].RunAsAdmin);
        Check("hosts 指向系统 hosts 文件",
            quickHosts.Count > 0 && quickHosts[0].Arguments.EndsWith(@"\drivers\etc\hosts",
                StringComparison.OrdinalIgnoreCase), quickHosts.FirstOrDefault()?.Arguments);
        Check("快捷命令大小写不敏感", QbarService.FindQuickCommand("CMD") != null);
        Check("未知词不是快捷命令", QbarService.FindQuickCommand("cmdx") == null);

        /* 搜索条目也可以是一个 EXE：欧路词典的 -w {q}。
         * 这正是用户要的“给程序发送搜索词”，不能再拼成网址。 */
        var eudicEntry = new QEntry
        {
            Key = "eu",
            Comment = "欧路词典",
            Value = @"C:\Program Files\Eudic\eudic.exe",
            Arguments = "-w {q}",
        };
        settings.Search.Add(eudicEntry);
        var eudicChip = qbar.ResolveChip("eu");
        Check("程序型搜索条目能变成标签", eudicChip?.Kind == ChipKind.Search);
        var eudicHit = qbar.Query("hello world", eudicChip);
        Check("程序型搜索走 Run 而不是 URL",
            eudicHit.Count > 0 && eudicHit[0].Kind == QbarKind.Run);
        CheckEqual("程序型搜索保留 EXE 路径", eudicHit[0].Payload, eudicEntry.Value);
        CheckEqual("程序型搜索的参数替换了 {q}",
            eudicHit[0].Arguments, "-w \"hello world\"");

        var eudicInline = qbar.Query("eu apple");
        Check("关键字+空格也能直接调用 EXE",
            eudicInline.Count > 0 && eudicInline[0].Kind == QbarKind.Run &&
            eudicInline[0].Arguments == "-w apple");
        CheckEqual("参数模板展开带引号",
            QbarService.ExpandArguments("-w {q}", "hello world"), "-w \"hello world\"");
        CheckEqual("无 {{q}} 模板自动追加参数",
            QbarService.ExpandArguments("--flag", "value"), "--flag value");

        // 每条默认表都要够丰富，且关键字不能互相遮挡（ResolveChip 是按顺序找的）。
        var defaults = DefaultSettings.Create();
        Check("默认网址 >= 100 条", defaults.Web.Count >= 100, defaults.Web.Count.ToString());
        Check("默认启动程序表为空（改为按需搜索）", defaults.Run.Count == 0, defaults.Run.Count.ToString());
        var chipKeys = defaults.Search.Where(e => !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Key)
            .Concat(defaults.Run.Select(e => e.Key))
            .Concat(defaults.Web.Select(e => e.Key))
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Check("搜索/程序/网址关键字互不重复", chipKeys.Count == 0,
            string.Join(",", chipKeys.Take(8)));

        var biliChip = qbar.ResolveChip("bili");
        Check("bili 解析为常用网址首页", biliChip?.Kind == ChipKind.Web &&
            biliChip.Value.Equals("https://www.bilibili.com", StringComparison.OrdinalIgnoreCase),
            biliChip?.Kind + " " + biliChip?.Value);
        Check("bili 搜索改成 bilis", qbar.ResolveChip("bilis")?.Kind == ChipKind.Search);

        // 参数里还留着 {q} 时，执行必须只提示、不能真把字面量传给程序。
        string? guardNotice = null;
        qbar.Execute(
            new QbarResult(QbarKind.Run, "欧路词典", "eudic", "程序", "eudic.exe",
                null, false, "-w {q}"),
            new QbarService.QbarCallbacks(
                notice => guardNotice = notice,
                _ => { }, _ => { }, _ => { }, (_, _, _) => { }, _ => { }, _ => { }));
        Check("缺少关键词时不把 {q} 传给程序", guardNotice != null, guardNotice);

        /* 本地内容源：Windows 设置、PowerShell、固定项。 */
        var display = qbar.Query("显示");
        Check("Windows 设置可被搜到",
            display.Any(r => r.Payload == "ms-settings:display"),
            string.Join(",", display.Select(r => r.Payload).Take(5)));

        var ps = qbar.Query("ps Get-Date");
        Check("ps 前缀生成 PowerShell 结果",
            ps.Any(r => r.Payload == "powershell.exe" && r.Arguments.Contains("Get-Date")),
            string.Join(",", ps.Select(r => r.Arguments).Take(3)));

        settings.Qbar.Pinned.Add(new QEntry
        {
            Key = "项目目录",
            Value = @"C:\Windows",
            Comment = "项目目录",
        });
        var pinnedHit = qbar.Query("项目");
        Check("固定项可被搜到并排在前面",
            pinnedHit.Count > 0 && pinnedHit[0].Kind == QbarKind.File,
            pinnedHit.FirstOrDefault()?.Title);

        TestShellIcons();
    }

    /// <summary>
    /// Shell 图标解析（Core/Icons/ShellIcons.cs）。
    ///
    /// 关键回归：老 IconCache.FromFile 对 PDF/Office/未知扩展名一律当图片硬解码，
    /// 抛异常返回 null，Qbar 结果行于是退化成字形图标。这里用「未知扩展名」和
    /// 「无扩展名」临时文件钉住「Shell 必须给出图标」，再钉住像素档位
    /// （28 DIP 在 100%/150%/200% 下分别是 32/48/64，不允许拿小图放大）。
    /// </summary>
    private static void TestShellIcons()
    {
        CheckEqual("28 DIP @100% 取 32 像素档", ShellIcons.PickPixels(28, 1.0), 32);
        CheckEqual("28 DIP @125% 取 48 像素档", ShellIcons.PickPixels(28, 1.25), 48);
        CheckEqual("28 DIP @150% 取 48 像素档", ShellIcons.PickPixels(28, 1.5), 48);
        CheckEqual("28 DIP @200% 取 64 像素档", ShellIcons.PickPixels(28, 2.0), 64);
        CheckEqual("16 DIP @100% 取 16 像素档", ShellIcons.PickPixels(16, 1.0), 16);
        CheckEqual("档位上限 256", ShellIcons.PickPixels(300, 1.0), 256);

        CheckEqual("null 路径规范化为空串", ShellIcons.NormalizePath(null), "");
        CheckEqual("纯空白路径规范化为空串", ShellIcons.NormalizePath("   "), "");
        CheckEqual("环境变量被展开",
            ShellIcons.NormalizePath(@"%SystemRoot%\notepad.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"));
        Check("UNC 路径识别为网络路径", ShellIcons.IsNetworkPath(@"\\server\share\a.pdf"));
        Check("本地路径不误判为网络路径", !ShellIcons.IsNetworkPath(@"C:\Windows"));

        string notepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (File.Exists(notepad))
        {
            ImageSource? icon = ShellIcons.RequestSync(notepad, 32, 1.0, 3000);
            Check("notepad.exe 拿到 Shell 图标", icon != null);
            Check("notepad.exe 图标按请求档位取（>=32px）",
                icon is System.Windows.Media.Imaging.BitmapSource bs32 && bs32.PixelWidth >= 32,
                (icon as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth.ToString() ?? "null");
            Check("解析结果进缓存（下次同步命中，UI 线程不用等）",
                ShellIcons.TryGetCached(notepad, 32, 1.0) != null);
            Check("48 档是独立缓存 key，不会串到 32 档",
                ShellIcons.TryGetCached(notepad, 48, 1.0) != ShellIcons.TryGetCached(notepad, 32, 1.0));
            ShellIcons.Invalidate("selftest");
            Check("Invalidate 之后缓存已清空", ShellIcons.TryGetCached(notepad, 32, 1.0) == null);
        }

        /* PDF / Office / 未知扩展名的退化回归：临时文件只要存在，Shell 就必须给图。 */
        string tempDir = Path.GetTempPath();
        string unknown = Path.Combine(tempDir, "inputplus-selftest-" + Guid.NewGuid().ToString("N") + ".zzzq");
        string noExt = Path.Combine(tempDir, "inputplus-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(unknown, "selftest");
            File.WriteAllText(noExt, "selftest");

            ImageSource? unknownIcon = ShellIcons.RequestSync(unknown, 32, 1.0, 3000);
            Check("未知扩展名文件拿到 Shell 图标（PDF 字形退化根因回归）", unknownIcon != null);
            Check("未知扩展名图标不是 16px 小图放大",
                unknownIcon is System.Windows.Media.Imaging.BitmapSource ub && ub.PixelWidth >= 32,
                (unknownIcon as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth.ToString() ?? "null");
            Check("IconCache.FromFile 对未知扩展名不再返回 null",
                IconCache.FromFile(unknown) != null);
            Check("无扩展名文件拿到 Shell 图标",
                ShellIcons.RequestSync(noExt, 32, 1.0, 3000) != null);
        }
        catch (Exception ex)
        {
            Check("临时文件图标用例不抛异常", false, ex.Message);
        }
        finally
        {
            try { File.Delete(unknown); } catch { }
            try { File.Delete(noExt); } catch { }
        }

        /* 真机采样：桌面上的 .lnk / .pdf。没有样本就跳过，不误报失败。
         * （快捷方式的小箭头由 Shell 叠加，看的是"能拿到图"，箭头本身没法在这里断言。） */
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(desktop))
            {
                string? lnk = Directory.EnumerateFiles(desktop, "*.lnk").FirstOrDefault();
                if (lnk != null)
                {
                    Check("桌面快捷方式拿到 Shell 图标", ShellIcons.RequestSync(lnk, 32, 1.0, 3000) != null, lnk);
                }
                string? pdf = Directory.EnumerateFiles(desktop, "*.pdf").FirstOrDefault();
                if (pdf != null)
                {
                    Check("桌面 PDF 拿到关联程序图标", ShellIcons.RequestSync(pdf, 32, 1.0, 3000) != null, pdf);
                    Check("IconCache.FromFile(PDF) 与 Shell 一致（都非 null）",
                        IconCache.FromFile(pdf) != null, pdf);
                }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "自检：桌面图标采样");
        }

        /* 不存在的文件 / 非法路径：返回 null，不能抛，也不能卡住调用方。 */
        var watch = Stopwatch.StartNew();
        ImageSource? missing = ShellIcons.RequestSync(@"C:\__inputplus_no_such_dir__\x.pdf", 32, 1.0, 800);
        watch.Stop();
        Check("不存在的文件返回 null 且不卡住", missing == null && watch.ElapsedMilliseconds < 2000,
            watch.ElapsedMilliseconds + "ms");
        Check("空字符串不解析", ShellIcons.RequestSync("", 32, 1.0, 200) == null);
    }

    /// <summary>Qbar 标签：输入 "bd " 之后 bd 变成标签、输入框只剩搜索词。</summary>
    private static void TestChips()
    {
        var settings = DefaultSettings.Create();
        var qbar = new QbarService(() => settings, () => new List<string>());

        var chip = qbar.ResolveChip("bd");
        Check("bd 能解析成标签", chip != null);
        CheckEqual("标签类型是搜索", chip?.Kind, ChipKind.Search);
        CheckEqual("标签名字优先用备注", chip?.Label, "百度");
        Check("bd 自带图标（优先显示图标）", chip?.HasIcon == true, chip?.IconPath);

        settings.Run.Add(new QEntry { Key = "calc", Value = "calc.exe", Comment = "计算器" });
        var run = qbar.ResolveChip("calc");
        Check("calc 能解析成程序标签", run?.Kind == ChipKind.Run, run?.Kind.ToString());
        Check("程序标签自动带 exe 图标或首字兜底", run != null && (run.HasIcon || run.Initial.Length > 0));

        var web = qbar.ResolveChip("gh");
        Check("gh 解析为搜索（搜索优先于网址）", web?.Kind is ChipKind.Search or ChipKind.Web);

        Check("未知关键字不变成标签", qbar.ResolveChip("不存在的词") == null);
        Check("default 不参与标签", qbar.ResolveChip("default") == null);

        // 标签模式下的查询：只剩搜索词
        var results = qbar.Query("天气", chip);
        Check("标签模式下有结果", results.Count > 0);
        Check("结果用对应引擎", results[0].Payload.Contains("baidu"), results[0].Payload);
        Check("结果带上了关键词", results[0].Payload.Contains(Uri.EscapeDataString("天气")));

        var empty = qbar.Query("", chip);
        Check("标签模式空输入给出提示", empty.Count > 0);

        // 标签模式下 = 仍可计算
        var calc = qbar.Query("=6*7", chip);
        Check("标签模式下仍可直接计算", calc[0].Kind == QbarKind.Math && calc[0].Payload == "42");

        /* 引擎关键字残留在输入框里时**不能**短路。
         *
         * 回归「叉掉引擎标签后搜什么都只出网页搜索」：
         * 叉标签那一下以前会把关键字写回输入框，用户接着打空格得到 "bd "，
         * QueryCore 一看到「第一段是引擎关键字 + 有空格」就直接 return 一条
         * 「在 百度 中搜索…」，文件 / 应用 / 动作全被吃掉。
         * 这里用"默认搜索引擎兜底行"当探针：短路那条路永远不会产出它。 */
        var leftover = qbar.Query("bd ", 12);
        /* 短路那条路只会返回 1 条「bd：…」，本地搜索照常跑就会有一堆候选。
         * 这里不挑具体的行（本机装了什么都能通），只钉住"不止一条"。 */
        Check("引擎关键字后面没内容时不短路（本地候选照常出现）",
            leftover.Count > 1,
            $"{leftover.Count} 条：" + string.Join(" | ", leftover.Take(4).Select(r => r.Badge + ":" + r.Title)));

        var engineOnly = qbar.Query("bd chrome", 12);
        Check("引擎关键字后面带内容时只出一条网页搜索",
            engineOnly.Count == 1 && engineOnly[0].Badge == "bd",
            string.Join(" | ", engineOnly.Select(r => r.Badge + ":" + r.Title)));

        // 没有图标的条目应当给出首字与颜色
        var noIcon = new QEntry { Key = "xx", Value = "https://example.com/?q={q}", Comment = "示例" };
        settings.Search.Add(noIcon);
        var chip2 = qbar.ResolveChip("xx");
        Check("无图标条目可用首字兜底", chip2 != null && !chip2.HasIcon && chip2.Initial == "示");
        Check("首字颜色稳定", IconCache.ColorFor("示例") == IconCache.ColorFor("示例"));

        /* 内置 ol：本机没装欧路词典也必须"有反应"（走网页词典），
           所以这条命令的 Path 是空的、由运行期解析。这里守住两件事：
           它确实出现在命令表里，且文案不是"命令没有配置程序"。 */
        var builtinOlRows = qbar.Query("ol hello");
        var builtinOl = builtinOlRows.FirstOrDefault(r => r.Action == "qcmd:" + QbarCommandStore.BuiltinDictId);
        Check("内置 ol 出现在普通搜索结果里", builtinOl != null,
            string.Join(",", builtinOlRows.Take(4).Select(r => r.Action)));
        Check("内置 ol 的文案说明会走网页词典",
            builtinOl != null && builtinOl.Subtitle.Contains("网页词典", StringComparison.Ordinal),
            builtinOl?.Subtitle);
        CheckEqual("内置 ol 保留用户输入作为参数", builtinOl?.Arguments, "hello");

        /* 自定义命令也能变标签：输入 ol 按空格 → 变成「欧路词典查词」，
           后面打的字直接当 {query} 交给命令。 */
        settings.Qbar.Commands.Add(new QbarCommand
        {
            Id = "user.ol", Keyword = "ol", Title = "欧路词典查词",
            Path = "eudic.exe", Parameter = "-w {query}", RunAsAdmin = true,
        });
        var cmdChip = qbar.ResolveChip("ol");
        Check("命令关键字能变成标签", cmdChip?.Kind == ChipKind.Command, cmdChip?.Kind.ToString());
        CheckEqual("命令标签显示命令名", cmdChip?.Label, "欧路词典查词");
        var cmdRows = qbar.Query("hello", cmdChip);
        Check("命令标签下给出待执行行", cmdRows.Count > 0, cmdRows.Count + " 行");
        CheckEqual("待执行行指向这条命令", cmdRows.Count > 0 ? cmdRows[0].Action : null, "qcmd:user.ol");
        CheckEqual("输入的文字原样进 {query}", cmdRows.Count > 0 ? cmdRows[0].Arguments : null, "hello");
        Check("副标题展开参数模板",
            cmdRows.Count > 0 && cmdRows[0].Subtitle.Contains("-w hello"),
            cmdRows.Count > 0 ? cmdRows[0].Subtitle : "(无结果)");

        // 图标缓存
        Check("内置图标文件已生成", File.Exists(IconUrls.Baidu), IconUrls.Baidu);
    }

    /// <summary>
    /// 文字度量：确保任何字号下，输入框高度都够放下文字。
    ///
    /// 这是"文字只显示上半部分"那个 bug 的回归测试：
    /// 只要 MinBoxHeight 算出来的高度小于"文字高 + 上下内边距 + 边框"，
    /// 文字就会被裁，所以这里逐字号验证。
    /// </summary>
    /// <summary>
    /// Qbar 的网址识别与文件路径识别。
    ///
    /// 关键回归点：裸域名（www.baidu.com）要能补协议直达；
    /// 盘符路径（C:\Users）不能被误判成网址，否则会去浏览器搜索一个路径。
    /// </summary>
    private static void TestQbarUrlAndPath()
    {
        Check("识别 https 网址", Core.Qbar.QbarService.LooksLikeUrl("https://example.com"));
        Check("识别 http 网址", Core.Qbar.QbarService.LooksLikeUrl("http://localhost:8080"));
        Check("识别裸域名", Core.Qbar.QbarService.LooksLikeUrl("www.baidu.com"));
        Check("识别 .com 域名", Core.Qbar.QbarService.LooksLikeUrl("google.com"));
        Check("识别 .cn 域名", Core.Qbar.QbarService.LooksLikeUrl("qq.com"));
        Check("识别 localhost", Core.Qbar.QbarService.LooksLikeUrl("localhost"));
        Check("识别 IP", Core.Qbar.QbarService.LooksLikeUrl("192.168.1.1"));

        // 不能误判
        Check("盘符不是网址", !Core.Qbar.QbarService.LooksLikeUrl("C:\\Users"));
        Check("带空格的不是网址", !Core.Qbar.QbarService.LooksLikeUrl("hello world"));
        Check("单个词不是网址", !Core.Qbar.QbarService.LooksLikeUrl("hello"));
        Check("UNC 路径不是网址", !Core.Qbar.QbarService.LooksLikeUrl("\\\\server\\share"));
        Check("空串不是网址", !Core.Qbar.QbarService.LooksLikeUrl(""));

        // 路径识别
        Check("识别盘符路径", Core.Qbar.QbarService.LooksLikePath("C:\\Users"));
        Check("识别斜杠路径", Core.Qbar.QbarService.LooksLikePath("D:/code"));
        Check("识别 UNC 路径", Core.Qbar.QbarService.LooksLikePath("\\\\server\\share"));
        Check("普通词不是路径", !Core.Qbar.QbarService.LooksLikePath("hello"));

        // 真实路径（数据目录一定存在）
        string dir = AppPaths.DataDir;
        Check("数据目录确实存在", Directory.Exists(dir));
        Check("数据目录不是网址", !Core.Qbar.QbarService.LooksLikeUrl(dir));
        Check("数据目录是路径", Core.Qbar.QbarService.LooksLikePath(dir));
    }

    /// <summary>
    /// Qbar 面板内的引擎搜索：按关键字 / 备注 / 内容匹配。
    /// 用户明确要求"主要就通过备注或者关键字来搜索"。
    /// </summary>
    private static void TestQbarEntryFilter()
    {
        var list = new List<QEntry>
        {
            new() { Key = "bd", Value = "https://www.baidu.com/s?wd={q}", Comment = "百度" },
            new() { Key = "zh", Value = "https://www.zhihu.com/search?q={q}", Comment = "知乎" },
            new() { Key = "go", Value = "https://www.google.com/search?q={q}", Comment = "Google" },
            new() { Key = "bi", Value = "https://www.bing.com/search?q={q}", Comment = "必应" },
        };

        CheckEqual("空过滤词返回全部", QEntry.Filter(list, "").Count, 4);
        CheckEqual("null 过滤词返回全部", QEntry.Filter(list, null).Count, 4);
        CheckEqual("只有空格也算不过滤", QEntry.Filter(list, "   ").Count, 4);

        var byKey = QEntry.Filter(list, "bd");
        CheckEqual("按关键字能搜到", byKey.Count, 1);
        CheckEqual("搜到的是百度", byKey[0].Comment, "百度");

        var byComment = QEntry.Filter(list, "知乎");
        CheckEqual("按中文备注能搜到", byComment.Count, 1);
        CheckEqual("搜到的是知乎", byComment[0].Key, "zh");

        var byValue = QEntry.Filter(list, "google.com");
        CheckEqual("按网址内容能搜到", byValue.Count, 1);
        CheckEqual("搜到的是 Google", byValue[0].Key, "go");

        CheckEqual("按大小写不敏感匹配", QEntry.Filter(list, "GOOGLE").Count, 1);
        CheckEqual("英文备注小写也能搜", QEntry.Filter(list, "bing").Count, 1);
        CheckEqual("搜不到时返回空", QEntry.Filter(list, "不存在的词xyz").Count, 0);

        // 真实引擎表规模
        var real = DefaultSettings.Create().Search;
        Check("内置引擎表足够大", real.Count > 200, real.Count.ToString());
        var hit = QEntry.Filter(real, "知乎");
        Check("在真实引擎表里能搜到知乎", hit.Any(e => e.Comment == "知乎"),
            string.Join(",", hit.Select(e => e.Comment)));
        Check("在真实引擎表里能搜到哔哩哔哩",
            QEntry.Filter(real, "哔哩").Any());
    }

    /// <summary>
    /// 「直达网页」分类：网址 + 备注，备注显示在键帽上。
    ///
    /// 验证：
    ///   1. 动作表里有 webPage 且属于「直达网页」分类；
    ///   2. 执行时把（规范化后的）网址交给浏览器；
    ///   3. KeyBinding.Note 能存能读，空值不写进 JSON；
    ///   4. 网址规范化容忍各种写法（不带协议、带 www、内网 http）；
    ///   5. 备注优先于域名显示在键帽上。
    /// </summary>
    private static void TestWebPageCategory()
    {
        var settings = DefaultSettings.Create();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };

        string? opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        string? openedFolder = null;
        Core.Actions.ActionContext.OpenFolderInterceptForTest = p => openedFolder = p;
        string? openedFile = null;
        Core.Actions.ActionContext.OpenFileInterceptForTest = p => openedFile = p;
        try
        {
            var cat = Core.Actions.ActionCatalog.Create(ctx);

            // 1. 两个动作都在「直达链接」分类下
            var wp = cat.Items.FirstOrDefault(i => i.Id == "webPage");
            Check("动作表有 webPage", wp != null,
                string.Join(",", cat.Items.Where(i => i.Category == "直达链接").Select(i => i.Id)));
            CheckEqual("webPage 在「直达链接」分类", wp?.Category, "直达链接");
            CheckEqual("webPage 标题是「直达网址」", wp?.Title, "直达网址");
            Check("webPage 需要参数（网址）", wp?.NeedsArg == true);

            /* openFolder 已经并进「直达文件」：不再出现在界面上，但**不能删** ——
             * 老配置里绑过它的按键要照旧按得动，启动时由 LegacyActionMigration 换掉。 */
            var of = cat.Items.FirstOrDefault(i => i.Id == "openFolder");
            Check("动作表有 openFolder（老 id 兼容）", of != null);
            CheckEqual("openFolder 在「直达链接」分类", of?.Category, "直达链接");
            CheckEqual("openFolder 标题是「直达文件夹」", of?.Title, "直达文件夹");
            Check("openFolder 需要参数（路径）", of?.NeedsArg == true);
            Check("openFolder 已隐藏（入口只剩「直达文件」）", of?.Hidden == true);

            var ofi = cat.Items.FirstOrDefault(i => i.Id == "openFile");
            Check("动作表有 openFile", ofi != null);
            CheckEqual("openFile 在「直达链接」分类", ofi?.Category, "直达链接");
            CheckEqual("openFile 标题是「直达文件」", ofi?.Title, "直达文件");
            Check("openFile 需要参数（路径）", ofi?.NeedsArg == true);
            Check("「直达链接」分类下只有两个可见动作",
                cat.Items.Count(i => i.Category == "直达链接" && !i.Hidden) == 2,
                string.Join(",", cat.Items.Where(i => i.Category == "直达链接").Select(i => i.Id)));

            // 2. 执行时把规范化后的网址传出去
            cat.Invoke("webPage", "https://example.com");
            CheckEqual("执行 webPage 传出网址", opened, "https://example.com/");

            opened = null;
            cat.Invoke("webPage", "github.com");
            Check("不带协议的网址被补成 https",
                opened != null && opened.StartsWith("https://github.com", StringComparison.Ordinal),
                opened ?? "(null)");

            // 3. openFolder 把路径整理后交出去（引号 / 环境变量 / ~ 都要处理）
            cat.Invoke("openFolder", "\"C:\\Windows\"");
            CheckEqual("执行 openFolder 去掉包裹引号", openedFolder, @"C:\Windows");

            openedFolder = null;
            cat.Invoke("openFolder", "  ");
            Check("空路径不执行打开", openedFolder == null);

            // 3b. openFile：路径整理后交给默认程序
            cat.Invoke("openFile", "\"D:/文档/方案.docx\"");
            CheckEqual("执行 openFile 归一正斜杠并去引号", openedFile, @"D:\文档\方案.docx");

            openedFile = null;
            cat.Invoke("openFile", "   ");
            Check("空文件路径不执行打开", openedFile == null);

            /* 3c. openFile 填的是文件夹。
             *
             * 用户真踩过：把 D:\Downloads\...\wb2api-panel-... 这种目录绑成
             * 「直达文件」，按下只弹「文件不存在」—— 因为这条动作以前只认
             * File.Exists。目录必须自动改走「打开文件夹」。 */
            openedFile = null;
            openedFolder = null;
            cat.Invoke("openFile", @"C:\Windows");
            CheckEqual("openFile 填文件夹时改走打开文件夹", openedFolder, @"C:\Windows");
            Check("openFile 填文件夹时不再当文件处理", openedFile == null, openedFile ?? "(null)");
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
            Core.Actions.ActionContext.OpenFolderInterceptForTest = null;
            Core.Actions.ActionContext.OpenFileInterceptForTest = null;
        }

        // 3. Note 字段能存能读，空值不序列化
        var b = new KeyBinding
        {
            Combo = "caps_w", Action = "webPage",
            Arg = "https://example.com", Note = "公司后台",
        };
        CheckEqual("Note 能存", b.Note, "公司后台");

        var noNote = new KeyBinding { Combo = "caps_e", Action = "webPage", Arg = "https://x.com" };
        string json = System.Text.Json.JsonSerializer.Serialize(noNote);
        Check("空备注不出现在 JSON 里", !json.Contains("Note"), json);

        // 中文会被默认转义成 \uXXXX，所以断言"非空"而不是断言字面文本
        string jsonWith = System.Text.Json.JsonSerializer.Serialize(b);
        Check("非空备注出现在 JSON 里", jsonWith.Contains("\"Note\""), jsonWith);

        // 4. 网址规范化
        CheckEqual("裸域名补 https",
            Core.Actions.UrlUtil.Normalize("github.com"), "https://github.com/");
        CheckEqual("已带协议不改写",
            Core.Actions.UrlUtil.Normalize("http://intranet/wiki"), "http://intranet/wiki");
        CheckEqual("空输入返回 null",
            Core.Actions.UrlUtil.Normalize("  "), null);
        CheckEqual("取域名去掉 www",
            Core.Actions.UrlUtil.HostOf("https://www.baidu.com/s?wd=x"), "baidu.com");
        CheckEqual("坏输入不抛异常",
            Core.Actions.UrlUtil.HostOf("!!!not a url!!!"), "网页");

        // 5. 键帽标签：备注优先
        CheckEqual("有备注用备注",
            Core.Actions.UrlUtil.LabelOf("公司后台", "https://example.com"), "公司后台");
        CheckEqual("无备注用域名",
            Core.Actions.UrlUtil.LabelOf("", "https://x.com/a/b"), "x.com");

        // 6. 直达文件夹：路径展开 + 键帽标签
        CheckEqual("去掉包裹引号", Core.Actions.ActionContext.ExpandFolderPath("\"D:\\Work\"") , @"D:\Work");
        CheckEqual("正斜杠归一", Core.Actions.ActionContext.ExpandFolderPath("D:/Work/A"), @"D:\Work\A");
        CheckEqual("去掉尾部反斜杠", Core.Actions.ActionContext.ExpandFolderPath(@"D:\Work\"), @"D:\Work");
        CheckEqual("盘符根目录保留", Core.Actions.ActionContext.ExpandFolderPath(@"D:\"), @"D:\");
        CheckEqual("空输入返回空串", Core.Actions.ActionContext.ExpandFolderPath("   "), "");
        Check("shell: 虚拟路径原样保留",
            Core.Actions.ActionContext.ExpandFolderPath("shell:Downloads")
                .Equals("shell:Downloads", StringComparison.OrdinalIgnoreCase));
        Check("~ 展开成用户目录",
            !Core.Actions.ActionContext.ExpandFolderPath("~").Contains('~'),
            Core.Actions.ActionContext.ExpandFolderPath("~"));

        CheckEqual("文件夹有备注用备注",
            UI.Views.KeysView.FolderLabelOf("项目", @"D:\Work\2026\项目文档"), "项目");
        CheckEqual("文件夹无备注用文件夹名",
            UI.Views.KeysView.FolderLabelOf("", @"D:\Work\项目文档"), "项目文档");
        CheckEqual("文件夹名为空时兜底",
            UI.Views.KeysView.FolderLabelOf("", ""), "文件夹");

        // 7. 直达文件：键帽标签用文件名
        CheckEqual("文件有备注用备注",
            UI.Views.KeysView.FileLabelOf("周报", @"D:\文档\方案.docx"), "周报");
        CheckEqual("文件无备注用文件名（去扩展名）",
            UI.Views.KeysView.FileLabelOf("", @"D:\文档\方案.docx"), "方案");
        CheckEqual("文件名兜底",
            UI.Views.KeysView.FileLabelOf("", ""), "文件");
        CheckEqual("ExpandPath 与 ExpandFolderPath 行为一致",
            Core.Actions.ActionContext.ExpandPath("D:/Work/A/"),
            Core.Actions.ActionContext.ExpandFolderPath("D:/Work/A/"));

        // 8. 打开之后要把目标窗口带到前台：先从 shell 命令里认出"目标程序"
        CheckEqual("带引号的 open 命令能取出 exe",
            Core.Windows.WindowService.ParseExeFromShellCommand(
                "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" -- \"%1\""),
            @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        CheckEqual("不带引号（路径有空格）也能取出 exe",
            Core.Windows.WindowService.ParseExeFromShellCommand(
                @"C:\Program Files\Mozilla Firefox\firefox.exe -osint -url %1"),
            @"C:\Program Files\Mozilla Firefox\firefox.exe");
        CheckEqual("环境变量会被展开",
            Core.Windows.WindowService.ParseExeFromShellCommand(
                @"%SystemRoot%\explorer.exe %1"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        CheckEqual("空命令返回空串",
            Core.Windows.WindowService.ParseExeFromShellCommand(""), "");
        CheckEqual("没有 exe 的命令返回空串",
            Core.Windows.WindowService.ParseExeFromShellCommand("rundll32 foo"), "");
        Check("两个 exe 名比较忽略路径与大小写",
            Core.Windows.WindowService.SameExeName("chrome.exe", @"C:\X\CHROME.EXE"));
        Check("不同程序不会被认成同一个",
            !Core.Windows.WindowService.SameExeName("chrome.exe", "msedge.exe"));

        /* 关联查询会返回"命令模板"而不是程序路径 —— 必须被挡掉。
         *
         * 真实故障（用户日志）：打开一个 .exe 文件时
         *   `AssociatedExe` 对 .exe 返回了字面量 "%1"，
         * 于是"要等的窗口属于哪个程序"变成 %1，轮询 3.5 秒、29 次全落空，
         * 日志写"没能置前"，用户看到的是窗口在后台打开。 */
        CheckEqual("命令模板 %1 不被当成程序路径",
            Core.Windows.WindowService.NormalizeAssocExe("%1"), "");
        CheckEqual("带引号带参数的命令只取 exe",
            Core.Windows.WindowService.NormalizeAssocExe("\"C:\\Program Files\\App\\app.exe\" \"%1\""),
            @"C:\Program Files\App\app.exe");
        CheckEqual("不带引号带参数的命令截到 .exe",
            Core.Windows.WindowService.NormalizeAssocExe(@"C:\Windows\notepad.exe %1"),
            @"C:\Windows\notepad.exe");
        CheckEqual("纯路径原样返回",
            Core.Windows.WindowService.NormalizeAssocExe(@"C:\Windows\notepad.exe"),
            @"C:\Windows\notepad.exe");
        CheckEqual("非 exe 结果被丢弃",
            Core.Windows.WindowService.NormalizeAssocExe("rundll32.exe foo.dll"),
            "rundll32.exe");
        CheckEqual("打开 .exe 文件时目标就是它自己",
            Core.Windows.WindowService.AssociatedExe(@"C:\Windows\notepad.exe"),
            @"C:\Windows\notepad.exe");
    }

    /// <summary>
    /// 「直达链接」的智能识别（「智能直达」分类里那个）：选中什么就开什么。
    ///
    /// 这个功能的风险全在**误判**上：把一句话当网址塞进浏览器、
    /// 把 `reade.md` 当域名打开，比"什么都不做"更打扰人。
    /// 所以这里用可控的假文件系统，把各种边界情况逐条钉住：
    ///
    ///   1. 动作必须归在「智能直达」，和打开网址 / 运行命令并列；
    ///   2. 网址（带协议 / 裸域名 / 引号包裹 / 末尾句号）；
    ///   3. 文件夹与文件按**真实存在性**区分，不存在时给出准确的"不存在"类型；
    ///   4. 路径的各种写法：引号、正斜杠、环境变量、UNC、file:///、shell:；
    ///   5. 不是链接的内容一律不乱开（多行、普通句子、像域名的文件名）。
    /// </summary>
    private static void TestSmartOpen()
    {
        // ---------- 1. 动作归位 ----------
        var settings = DefaultSettings.Create();
        var notes = new List<string>();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new Core.Actions.MouseService(),
            Notify = n => notes.Add(n),
        };

        var cat = Core.Actions.ActionCatalog.Create(ctx);
        var so = cat.Items.FirstOrDefault(i => i.Id == "smartOpen");
        Check("动作表有 smartOpen", so != null);
        CheckEqual("smartOpen 在「智能直达」分类", so?.Category, "智能直达");
        CheckEqual("smartOpen 标题是「智能直达」", so?.Title, "智能直达");
        Check("smartOpen 不需要预填参数", so?.NeedsArg == false);
        Check("「智能直达」分类下没有别的可见动作",
            cat.Items.Where(i => i.Category == "智能直达").All(i => i.Id == "smartOpen"),
            string.Join(",", cat.Items.Where(i => i.Category == "智能直达").Select(i => i.Id)));

        // ---------- 2. 识别：用假文件系统，避免依赖测试机磁盘 ----------
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\文档\方案.docx", @"C:\Windows\notepad.exe",
            /* 用来验证"没加引号的带空格路径 + 参数"：只有真在盘上的前缀才算命令。 */
            @"C:\Program Files\App\app.exe",
        };
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\Work\项目", @"D:\Work\2026", @"C:\Windows",
        };
        static bool Has(HashSet<string> set, string p) => set.Contains(p);

        Core.Actions.SmartTarget Hit(string text) =>
            Core.Actions.SmartTargetDetector.Detect(text, p => Has(files, p), p => Has(dirs, p));

        // 2a. 网址
        CheckEqual("带协议的网址", Hit("https://github.com/a?b=1").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("http 内网地址", Hit("http://intranet/wiki").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("裸域名", Hit("github.com").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("带 www 与路径", Hit("www.baidu.com/s?wd=x").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("mailto 也算链接", Hit("mailto:a@b.com").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("网址末尾的中文句号被去掉",
            Hit("https://a.com。").Target, "https://a.com");
        CheckEqual("引号包裹的网址也能认",
            Hit("「https://example.com/path」").Target, "https://example.com/path");

        // 2b. 文件夹 / 文件：存在性优先
        CheckEqual("存在的目录 → 文件夹", Hit(@"D:\Work\项目").Kind, Core.Actions.SmartKind.Folder);
        CheckEqual("引号包裹的路径", Hit("\"D:\\Work\\项目\"").Kind, Core.Actions.SmartKind.Folder);
        CheckEqual("正斜杠 + 尾分隔符", Hit("D:/Work/项目/").Target, @"D:\Work\项目");
        CheckEqual("存在的文件 → 文件", Hit(@"D:\文档\方案.docx").Kind, Core.Actions.SmartKind.File);
        CheckEqual("文件目标做了路径归一", Hit("D:/文档/方案.docx").Target, @"D:\文档\方案.docx");
        CheckEqual("带空格路径", Hit(@"C:\Windows\notepad.exe").Kind, Core.Actions.SmartKind.File);

        // 2c. 不存在时也要说清是"文件不存在"还是"文件夹不存在"
        CheckEqual("不存在的文件", Hit(@"D:\文档\没有这个.docx").Kind, Core.Actions.SmartKind.MissingFile);
        CheckEqual("不存在的文件夹", Hit(@"D:\工作\2026规划").Kind, Core.Actions.SmartKind.MissingFolder);
        CheckEqual("结尾带分隔符按文件夹算", Hit(@"D:\没有这个目录\").Kind, Core.Actions.SmartKind.MissingFolder);
        CheckEqual("UNC 路径", Hit(@"\\nas\share\doc").Kind, Core.Actions.SmartKind.MissingFolder);

        // 2d. 特殊写法
        CheckEqual("file:/// 地址", Hit("file:///D:/文档/方案.docx").Kind, Core.Actions.SmartKind.File);
        CheckEqual("shell: 虚拟目录", Hit("shell:Downloads").Kind, Core.Actions.SmartKind.Folder);
        CheckEqual("shell: 目标原样保留", Hit("shell:Downloads").Target, "shell:Downloads");
        Check("环境变量被展开",
            !Hit(@"%USERPROFILE%\Desktop").Target.Contains('%'),
            Hit(@"%USERPROFILE%\Desktop").Target);

        // 2e. 不像链接的内容：一个都不许开
        CheckEqual("多行选区不认", Hit("第一行\r\n第二行").Kind, Core.Actions.SmartKind.None);
        CheckEqual("普通句子不认", Hit("今天天气不错").Kind, Core.Actions.SmartKind.None);
        CheckEqual("没有一个点的单词不认", Hit("hello").Kind, Core.Actions.SmartKind.None);
        CheckEqual("像域名的文件名（readme.md）不认", Hit("readme.md").Kind, Core.Actions.SmartKind.None);
        CheckEqual("像域名的脚本名（build.ps1）不认", Hit("build.ps1").Kind, Core.Actions.SmartKind.None);
        CheckEqual("空输入不认", Hit("   ").Kind, Core.Actions.SmartKind.None);
        CheckEqual("超长文本不认",
            Core.Actions.SmartTargetDetector.Detect(new string('a', 3000),
                _ => false, _ => false).Kind, Core.Actions.SmartKind.None);

        // 2e-2. 命令行：程序 + 参数也算"目标"，但它归「运行命令」而不是直达链接
        /* 以前这一串会被当成网址丢给浏览器（UrlUtil 认不出、shell 说"找不到文件"），
         * 用户填了命令行却得不到任何提示。区分命令和普通句子的唯一依据是
         * "第一个词像不像程序"，外加 shell 前缀（cmd / powershell / wt…）。 */
        CheckEqual("程序 + 参数 → 命令",
            Hit("notepad.exe D:\\笔记.txt").Kind, Core.Actions.SmartKind.Command);
        CheckEqual("命令的目标保留整行（参数原样带过去）",
            Hit("notepad.exe D:\\笔记.txt").Target, "notepad.exe D:\\笔记.txt");
        CheckEqual("引号包住的程序路径 + 参数 → 命令",
            Hit("\"C:\\Windows\\notepad.exe\" -x").Kind, Core.Actions.SmartKind.Command);
        CheckEqual("没加引号的带空格路径 + 参数 → 命令",
            Hit(@"C:\Program Files\App\app.exe -x").Kind, Core.Actions.SmartKind.Command);
        CheckEqual("shell 前缀（cmd /c）→ 命令",
            Hit("cmd /c dir").Kind, Core.Actions.SmartKind.Command);
        CheckEqual("shell 前缀（powershell -File）→ 命令",
            Hit("powershell -File build.ps1").Kind, Core.Actions.SmartKind.Command);
        CheckEqual("两个普通单词不是命令", Hit("hello world").Kind, Core.Actions.SmartKind.None);
        CheckEqual("中文 + 空格不是命令", Hit("今天 天气").Kind, Core.Actions.SmartKind.None);
        CheckEqual("带空格的真实文件路径仍是文件",
            Hit(@"C:\Program Files\App\app.exe").Kind, Core.Actions.SmartKind.File);

        // ---------- 2f. 识别结果 → 该绑哪个动作 ----------
        /* 「直达链接」面板不再让用户选类型：类型由识别结果决定。
         * 这张对照表错一行，用户就会"填了网址却被当文件夹打开"。 */
        CheckEqual("网址 → 直达网址",
            UI.Views.KeysView.LinkActionFor(Core.Actions.SmartKind.Url), "webPage");
        /* 文件夹也落到「直达文件」：OpenFile 自己会先看 Directory.Exists。
         * 两个入口并存时用户得先判断自己填的是文件还是文件夹，选错就是"文件不存在"。 */
        CheckEqual("文件夹 → 直达文件",
            UI.Views.KeysView.LinkActionFor(Core.Actions.SmartKind.Folder), "openFile");
        CheckEqual("不存在的文件夹也算文件（按下去提示不存在）",
            UI.Views.KeysView.LinkActionFor(Core.Actions.SmartKind.MissingFolder), "openFile");
        CheckEqual("文件 → 直达文件",
            UI.Views.KeysView.LinkActionFor(Core.Actions.SmartKind.File), "openFile");
        CheckEqual("不存在的文件也算文件",
            UI.Views.KeysView.LinkActionFor(Core.Actions.SmartKind.MissingFile), "openFile");
        CheckEqual("认不出就不动原来的动作",
            UI.Views.KeysView.LinkActionFor(Core.Actions.SmartKind.None), null);
        CheckEqual("命令行 → 运行命令",
            UI.Views.KeysView.LinkActionFor(Core.Actions.SmartKind.Command), "runProgram");

        // ---------- 2f-2. 「直达链接」与「运行命令」共用一个目标框 ----------
        /* 这两类动作的 Arg 都是"一整行目标文字"，切分类时不能互相清空 ——
         * 否则点一下「直达链接」，绑好的命令行就被抹成空串。 */
        Check("运行命令吃命令行参数",
            UI.Views.KeysView.IsContentArgAction("runProgram"));
        Check("直达链接与运行命令共用一个参数族",
            UI.Views.KeysView.SameArgFamily("runProgram", "webPage"));
        Check("反向也算同一族",
            UI.Views.KeysView.SameArgFamily("openFile", "runProgram"));
        Check("步进动作和命令行不是一族",
            !UI.Views.KeysView.SameArgFamily("moveChar", "runProgram"));

        // ---------- 2g. 提示文案必须说清"按下去会发生什么" ----------
        string HintOf(string text) => UI.Views.KeysView.LinkHintFor(Hit(text), text);

        Check("网址提示提到浏览器", HintOf("https://a.com").Contains("浏览器"), HintOf("https://a.com"));
        /* 只有"整理后的地址和原文不一样"时才把真实地址说出来
         * （裸域名 a.com 的 https 补全发生在打开时，Detect 不动它）。 */
        Check("修剪掉的标点不误伤地址",
            HintOf("https://a.com。").Contains("会打开 https://a.com"), HintOf("https://a.com。"));
        Check("地址没变化时不啰嗦",
            !HintOf("https://a.com").Contains("会打开"), HintOf("https://a.com"));
        Check("存在的文件夹提示用资源管理器",
            HintOf(@"D:\Work\项目").Contains("资源管理器"), HintOf(@"D:\Work\项目"));
        Check("存在的文件提示用默认程序",
            HintOf(@"D:\文档\方案.docx").Contains("默认程序"), HintOf(@"D:\文档\方案.docx"));
        Check("文件夹不存在要说清",
            HintOf(@"D:\工作\2026规划").Contains("文件夹不存在"), HintOf(@"D:\工作\2026规划"));
        Check("文件不存在要说清",
            HintOf(@"D:\文档\没有这个.docx").Contains("文件不存在"), HintOf(@"D:\文档\没有这个.docx"));
        Check("认不出时教用户怎么写",
            HintOf("今天天气不错").Contains("浏览"), HintOf("今天天气不错"));
        Check("命令提示说清参数会原样传过去",
            HintOf("notepad.exe D:\\笔记.txt").Contains("原样"), HintOf("notepad.exe D:\\笔记.txt"));
        Check("空输入是引导而不是报错",
            HintOf("").Contains("自动识别"), HintOf(""));
        Check("空输入时不该报「看不出」",
            !HintOf("").Contains("看不出"), HintOf(""));

        // ---------- 3. 动作执行：三类目标分别落到三个通道 ----------
        string? openedUrl = null, openedFolder = null, openedFile = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => openedUrl = u;
        Core.Actions.ActionContext.OpenFolderInterceptForTest = p => openedFolder = p;
        Core.Actions.ActionContext.OpenFileInterceptForTest = p => openedFile = p;

        string tmp = Path.Combine(Path.GetTempPath(), "ke_smart_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        string tmpFile = Path.Combine(tmp, "方案.txt");
        File.WriteAllText(tmpFile, "selftest");

        try
        {
            ctx.CaptureTextForTest = () => tmp;
            cat.Invoke("smartOpen", "");
            CheckEqual("选中文件夹 → 交给资源管理器", openedFolder, tmp);
            Check("选中文件夹 → 不该走浏览器", openedUrl == null);

            openedFolder = null;
            ctx.CaptureTextForTest = () => tmpFile;
            cat.Invoke("smartOpen", "");
            CheckEqual("选中文件 → 交给默认程序", openedFile, tmpFile);

            openedFile = null;
            openedUrl = null;
            ctx.CaptureTextForTest = () => "github.com";
            cat.Invoke("smartOpen", "");
            Check("选中裸域名 → 走浏览器（自动补 https）",
                openedUrl != null && openedUrl.StartsWith("https://github.com", StringComparison.Ordinal),
                openedUrl ?? "(null)");
            Check("裸域名不该走文件通道", openedFile == null && openedFolder == null);

            openedUrl = null;
            notes.Clear();
            ctx.CaptureTextForTest = () => "这是一段普通文字";
            cat.Invoke("smartOpen", "");
            Check("普通文字什么都不开",
                openedUrl == null && openedFolder == null && openedFile == null);
            Check("普通文字给出明确提示",
                notes.Any(n => n.Contains("既不是网址")), string.Join(" | ", notes));

            notes.Clear();
            ctx.CaptureTextForTest = () => Path.Combine(tmp, "没有这个.docx");
            cat.Invoke("smartOpen", "");
            Check("文件不存在时提示「文件不存在」",
                notes.Any(n => n.Contains("文件不存在")), string.Join(" | ", notes));
            Check("文件不存在时不去打开", openedFile == null);

            notes.Clear();
            ctx.CaptureTextForTest = () => "";
            ctx.ClipboardTextForTest = () => "";
            ctx.ManualCopyWaitForTest = _ => "";
            cat.Invoke("smartOpen", "");
            Check("没选区、剪贴板也空着时什么都不做（不弹提示）",
                notes.Count == 0 && openedUrl == null && openedFolder == null && openedFile == null,
                string.Join(" | ", notes));

            // 没有选区 → 用剪贴板里最新的一条（用户要的就是这条路径）
            openedFolder = null;
            notes.Clear();
            ctx.CaptureTextForTest = () => "";
            ctx.ClipboardTextForTest = () => tmp;
            cat.Invoke("smartOpen", "");
            CheckEqual("没选区时打开剪贴板里的文件夹", openedFolder, tmp);

            // 剪贴板里是普通文字 → 同样不触发、不提示
            openedUrl = null;
            openedFolder = null;
            openedFile = null;
            notes.Clear();
            ctx.ClipboardTextForTest = () => "这是一段普通文字";
            cat.Invoke("smartOpen", "");
            Check("剪贴板里不是链接时什么都不开",
                openedUrl == null && openedFolder == null && openedFile == null);
            Check("剪贴板里不是链接时不打扰用户", notes.Count == 0, string.Join(" | ", notes));

            // 有选区时优先用选区，不看剪贴板
            openedFolder = null;
            ctx.CaptureTextForTest = () => tmp;
            ctx.ClipboardTextForTest = () => @"C:\不存在的\剪贴板路径";
            cat.Invoke("smartOpen", "");
            CheckEqual("有选区时忽略剪贴板", openedFolder, tmp);
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
            Core.Actions.ActionContext.OpenFolderInterceptForTest = null;
            Core.Actions.ActionContext.OpenFileInterceptForTest = null;
            ctx.CaptureTextForTest = null;
            ctx.ManualCopyWaitForTest = null;
            ctx.ClipboardTextForTest = null;
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 网页标题抓取：解析 HTML 的 title，处理实体与空白。
    /// </summary>
    private static void TestWebTitleExtract()
    {
        CheckEqual("基本 title",
            Core.Actions.WebTitleFetcher.ExtractTitle("<html><head><title>Hello</title></head></html>"),
            "Hello");

        CheckEqual("大写标签也认",
            Core.Actions.WebTitleFetcher.ExtractTitle("<TITLE>GitHub</TITLE>"), "GitHub");

        CheckEqual("带属性的 title",
            Core.Actions.WebTitleFetcher.ExtractTitle("<title lang=\"zh\">知乎</title>"), "知乎");

        CheckEqual("HTML 实体解码",
            Core.Actions.WebTitleFetcher.ExtractTitle("<title>A &amp; B &lt;C&gt;</title>"), "A & B <C>");

        CheckEqual("折叠空白",
            Core.Actions.WebTitleFetcher.ExtractTitle("<title>  很多\n   空格  </title>"), "很多 空格");

        Check("没有 title 返回 null",
            Core.Actions.WebTitleFetcher.ExtractTitle("<html>no title</html>") == null);
        Check("空 HTML 返回 null",
            Core.Actions.WebTitleFetcher.ExtractTitle("") == null);

        // 过长标题要截断（备注字段不该塞几百字）
        string longTitle = new string('字', 200);
        var got = Core.Actions.WebTitleFetcher.ExtractTitle($"<title>{longTitle}</title>");
        Check("超长标题被截断", got != null && got.Length <= 60, (got?.Length ?? 0).ToString());

        // 通过测试钩子验证"抓取 → 标题"整条链（不真的发网络请求）
        Core.Actions.WebTitleFetcher.InterceptForTest = _ => "抓到的标题";
        try
        {
            CheckEqual("抓取链返回标题",
                Core.Actions.WebTitleFetcher.Fetch("example.com"), "抓到的标题");
            Check("非法网址不去抓",
                Core.Actions.WebTitleFetcher.Fetch("  ") == null);
        }
        finally
        {
            Core.Actions.WebTitleFetcher.InterceptForTest = null;
        }
    }

    /// <summary>
    /// Qbar 面板配色要按底色亮度推导，浅色主题下不能"白字白底"。
    /// </summary>
    private static void TestQbarThemeContrast()
    {
        var t = typeof(InputPlus.UI.Windows.QbarWindow);
        var m = t.GetMethod("IsLightColor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("QbarWindow 有亮度判定", m != null);
        if (m == null) return;

        bool IsLight(string hex) =>
            (bool)m.Invoke(null, new object[]
                { (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex) })!;

        Check("白色判为浅色", IsLight("#FFFFFF"));
        Check("浅灰判为浅色", IsLight("#F0F0F0"));
        Check("深色判为非浅色", !IsLight("#1C1F27"));
        Check("纯黑判为非浅色", !IsLight("#000000"));
        Check("中蓝判为非浅色", !IsLight("#2B6CF6"));
    }

    /// <summary>
    /// Qbar 里"引擎胶囊"的文字必须两种明暗下都看得清。
    ///
    /// 用户报的是：胶囊里表示引擎（备注）的字颜色不对，白天看着发糊。
    /// 根因有两个，都出在"字色跟着面板明暗走"这件事上：
    ///   1. 引擎名用的是**次级灰**（浅色 #5B6478 / 深色 #C9D6EC），
    ///      压在胶囊那层 13% 叠色上对比度不够；
    ///   2. 首字块底色是**品牌色**（各家深浅不一），跟着面板走就会出现
    ///      "浅色面板 → 深灰字压深蓝牌子"这种隐形组合。
    ///
    /// 现在：引擎名用主文字色；首字块按品牌色自身亮度挑黑白。
    /// 这里用 WCAG 对比度把关（≥ 4.5 是正文级可读的下限）。
    /// </summary>
    private static void TestQbarChipTextContrast()
    {
        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        // 两种面板底色（跟随主题时的实际取值），以及胶囊那层 13% 叠色
        var panels = new (string Name, Color Panel, Color Veil)[]
        {
            ("浅色面板", C("#F4F5F7"), Colors.Black),
            ("深色面板", C("#1C1F27"), Colors.White),
        };

        foreach (var (name, panel, veil) in panels)
        {
            Color chipBase = UI.Windows.QbarWindow.Overlay(panel, veil, 0x22 / 255.0);
            Color text = UI.Windows.QbarWindow.PrimaryOn(panel);

            double ratio = UI.Windows.QbarWindow.Contrast(text, chipBase);
            Check($"{name}：胶囊里的引擎名对比度 ≥ 4.5", ratio >= 4.5, $"{ratio:F2}:1");

            /* 换回旧的次级灰，必须**明确不达标** —— 否则说明这条断言
             * 根本没在管颜色（比如两边都算成同一个值）。 */
            Color old = UI.Windows.QbarWindow.PrimaryOn(panel) == Colors.White
                ? C("#C9D6EC") : C("#5B6478");
            double oldRatio = UI.Windows.QbarWindow.Contrast(old, chipBase);
            Check($"{name}：旧的次级灰确实不达标（回归指向）", oldRatio < ratio,
                $"旧={oldRatio:F2}:1 新={ratio:F2}:1");
        }

        // 首字块：品牌色深浅不一，黑白必须自己挑，且都得达到正文级
        var brands = new (string Name, string Hex)[]
        {
            ("百度蓝", "#2932E1"),
            ("微软蓝", "#0078D4"),
            ("必应青", "#008373"),
            ("谷歌红", "#EA4335"),
            ("必应黄", "#FFB900"),
            ("白", "#FFFFFF"),
            ("黑", "#000000"),
            ("中灰", "#808080"),
        };

        foreach (var (name, hex) in brands)
        {
            Color bg = C(hex);
            Color picked = UI.Windows.QbarWindow.BestTextOn(bg);
            double ratio = UI.Windows.QbarWindow.Contrast(picked, bg);

            Check($"首字块（{name}）上的字色对比度 ≥ 4.5", ratio >= 4.5,
                $"{ratio:F2}:1  color=#{picked.R:X2}{picked.G:X2}{picked.B:X2}");

            // 必须真的是"两者里更高的那个"，不是碰巧蒙对
            Color other = picked == Colors.White ? C("#161A23") : Colors.White;
            Check($"首字块（{name}）选的是更清楚的那个颜色",
                ratio >= UI.Windows.QbarWindow.Contrast(other, bg) - 1e-9);
        }

        // 深色牌子给白字、浅色牌子给深字 —— 这条最直观，也最能挡住"忘了挑"
        Check("深蓝品牌色上用白字", UI.Windows.QbarWindow.BestTextOn(C("#2932E1")) == Colors.White);
        Check("亮黄品牌色上用深字", UI.Windows.QbarWindow.BestTextOn(C("#FFB900")) != Colors.White);
    }

    /// <summary>
    /// 设置页顶上那排页签（搜索指令 / 启动程序 / 常用网址 / 固定项）的字必须看得清。
    ///
    /// 用户报的是：夜间模式下这几个页签没选中时"背景是黑的、字也是黑的"。
    /// 根因不在 chip 上 —— 页签模板只在 IsSelected 里给了文字前景色，
    /// 未选中态没有任何前景色 setter，于是继承 WPF 默认黑色，
    /// 压在夜间卡片底色 Surface(#1A1E27) 上正好黑底黑字。
    ///
    /// 现在普通 / 悬停 / 选中三态的字色都显式绑主题画刷，这里逐态算 WCAG 对比度
    /// （≥ 4.5 是正文级可读的下限）。页签平时落在卡片底（Surface），换主题或换宿主
    /// 也可能落在页面底（Background），两个底色都验，省得只对了一半。
    /// </summary>
    private static void TestQbarTabContrast()
    {
        var saved = UI.Theme.ThemeManager.Current;

        try
        {
            var themes = new (string Name, UI.Theme.ThemeManager.Mode Mode)[]
            {
                ("日间", UI.Theme.ThemeManager.Mode.Light),
                ("夜间", UI.Theme.ThemeManager.Mode.Dark),
            };

            foreach (var (name, mode) in themes)
            {
                UI.Theme.ThemeManager.Apply(mode);

                Color Get(string key)
                {
                    string hex = BrushColor(key);
                    Check($"{name}：主题里有 {key}", hex.Length > 0, hex);
                    return hex.Length > 0 ? (Color)ColorConverter.ConvertFromString(hex) : Colors.Transparent;
                }

                Color surface = Get("Brush.Surface");
                Color background = Get("Brush.Background");
                Color hover = Get("Brush.Hover");
                Color accentSoft = Get("Brush.AccentSoft");
                Color primary = Get("Brush.TextPrimary");
                Color secondary = Get("Brush.TextSecondary");

                foreach (var (where, back) in new[] { ("卡片底色", surface), ("页面底色", background) })
                {
                    double idle = UI.Windows.QbarWindow.Contrast(secondary, back);
                    Check($"{name}·{where}：页签未选中的字对比度 ≥ 4.5", idle >= 4.5, $"{idle:F2}:1");
                }

                double hovering = UI.Windows.QbarWindow.Contrast(primary, hover);
                Check($"{name}：页签悬停态的字对比度 ≥ 4.5", hovering >= 4.5, $"{hovering:F2}:1");
                Check($"{name}：悬停底色与卡片底色不同（反馈看得见）", hover != surface, $"{hover} vs {surface}");

                double selected = UI.Windows.QbarWindow.Contrast(primary, accentSoft);
                Check($"{name}：页签选中态的字对比度 ≥ 4.5", selected >= 4.5, $"{selected:F2}:1");
                Check($"{name}：选中底色与未选中底色不同（状态分得开）", accentSoft != surface,
                    $"{accentSoft} vs {surface}");

                /* 回归指向：要是还把前景色留给默认值，夜间就是黑字压深底。
                 * 这条断言逼着上面几条真的在管颜色，而不是两边都算成同一个值。 */
                if (mode == UI.Theme.ThemeManager.Mode.Dark)
                {
                    double blackOnDark = UI.Windows.QbarWindow.Contrast(Colors.Black, surface);
                    Check("夜间：黑字压在卡片底色上确实不足 4.5（旧问题的来源）", blackOnDark < 4.5,
                        $"{blackOnDark:F2}:1");
                }
            }
        }
        finally
        {
            UI.Theme.ThemeManager.Apply(saved);
        }
    }

    /// <summary>
    /// 文件搜索只有一个内核：Input Plus 自己的索引（Core/Qbar/NativeSearchIndex.cs）。
    ///
    /// 这里验的是"接线对不对"，不是"索引全不全" —— 扫到哪一步取决于数据目录里
    /// 已经落盘的快照，自检不该跟着它翻车，更不该顺手拉起一次扫盘。
    /// </summary>
    private static void TestQbarNativeIndex()
    {
        string dir = Core.Qbar.NativeSearchIndex.IndexDir ?? "";
        Check("文件索引目录落在数据目录下",
            dir.Length > 0 && dir.StartsWith(AppPaths.DataDir, StringComparison.OrdinalIgnoreCase), dir);

        /* 一次不可能命中的查询：既验"不抛异常"，也验"没建索引时安静地返回空表"。
         * 自检的数据目录是一次性的，所以这里必然走"还没有索引"那条路。 */
        string probe = "zz-inputplus-selftest-" + Guid.NewGuid().ToString("N");
        int count = -1;
        try { count = Core.Qbar.NativeSearchIndex.Search(probe, 5, 300).Count; }
        catch (Exception ex) { Check("文件索引查询不抛异常", false, ex.GetType().Name + ": " + ex.Message); }
        if (count >= 0)
        {
            Check("文件索引查询不抛异常", true, probe);
            Check("不相干的词搜不到东西", count == 0, count.ToString());
        }

        /* 用户报的旧行为：Qbar 会先自己扫桌面/文档，并给一条"稍后再输入一次"的占位。
         * 那条路已经拆了，不许再回来。 */
        var settings = DefaultSettings.Create();
        var qbar = new QbarService(() => settings, () => new List<string>());
        var results = qbar.Query("system32", 12);
        Check("文件搜索不再出现“稍后再输入一次”的占位",
            !results.Any(r => r.Subtitle != null && r.Subtitle.Contains("稍后再输入一次")), "");
    }

    /// <summary>
    /// Qbar 打开期间"点面板外面就收起"的判定。
    ///
    /// 用户要求：面板出来之后，点任何不是面板的地方都该消失。
    /// 任务栏是最容易漏的一种 —— 点了它前台窗口不变，Deactivated 不
    /// 触发，面板会一直挂着；所以打开期间挂一个全局鼠标钩子兜底，
    /// 落在窗口矩形外就收起。这里验的就是那个纯矩形判定。
    /// </summary>
    private static void TestQbarOutsideClick()
    {
        // 窗口矩形（物理像素）：左上 (100,100)，右下 (900,500)
        const int L = 100, T = 100, R = 900, B = 500;

        Check("点面板中心不收起", !UI.Windows.QbarWindow.IsOutsidePanel(500, 300, L, T, R, B));
        Check("点左上外要收起", UI.Windows.QbarWindow.IsOutsidePanel(50, 50, L, T, R, B));
        Check("点右上外要收起", UI.Windows.QbarWindow.IsOutsidePanel(1000, 300, L, T, R, B));
        Check("点左下外要收起", UI.Windows.QbarWindow.IsOutsidePanel(500, 900, L, T, R, B));
        Check("点右下外要收起", UI.Windows.QbarWindow.IsOutsidePanel(1000, 900, L, T, R, B));

        // 左/上取闭区间、右/下取开区间 —— 与 Windows 的命中测试一致
        Check("点最左一列不算窗外", !UI.Windows.QbarWindow.IsOutsidePanel(L, 300, L, T, R, B));
        Check("点最右一列算窗外", UI.Windows.QbarWindow.IsOutsidePanel(R, 300, L, T, R, B));
        Check("点最上一行不算窗外", !UI.Windows.QbarWindow.IsOutsidePanel(500, T, L, T, R, B));
        Check("点最下一行算窗外", UI.Windows.QbarWindow.IsOutsidePanel(500, B, L, T, R, B));

        /* 回归：删掉的双击处理器不许回来。它和"单击即执行"叠加，
         * 双击手势会把同一个结果执行两遍（开两个窗口/两个标签页）。 */
        var dbl = typeof(UI.Windows.QbarWindow).GetMethod("OnResultDoubleClick",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Check("QbarWindow 没有双击执行处理器（执行只由单击负责）", dbl == null);
    }

    /// <summary>
    /// 「直接搜索」：用选中文本 + 指定引擎，在默认浏览器里搜索。
    /// </summary>
    private static void TestDirectSearch()
    {
        var settings = DefaultSettings.Create();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };

        var cat = Core.Actions.ActionCatalog.Create(ctx);

        // ① 动作存在且分类正确
        var bdAction = cat.Items.FirstOrDefault(i => i.Id == "search:bd");
        Check("动作表有 search:bd", bdAction != null,
            string.Join(",", cat.Items.Where(i => i.Category == "直接搜索").Select(i => i.Id)));
        CheckEqual("search:bd 在「直接搜索」分类", bdAction?.Category, "直接搜索");
        Check("有「直接搜索」分类的多个动作",
            cat.Items.Count(i => i.Category == "直接搜索") >= 5,
            cat.Items.Count(i => i.Category == "直接搜索").ToString());

        // ② v2.8 起直接搜索覆盖**全部**引擎（下拉支持输入过滤，不怕多）
        Check("直接搜索覆盖全部引擎",
            cat.Items.Count(i => i.Category == "直接搜索") >= 50,
            cat.Items.Count(i => i.Category == "直接搜索").ToString());

        // ③ 执行时把选中文本编码进 URL 并交给浏览器
        string? opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        try
        {
            // 用 overrideText 绕开真实的剪贴板读取
            ctx.SearchSelection("bd", "测试 关键词");
            Check("搜索打开了网址", opened != null, opened ?? "(null)");
            if (opened != null)
            {
                Check("用的是百度的引擎", opened.Contains("baidu.com", StringComparison.OrdinalIgnoreCase), opened);
                Check("关键词被正确编码",
                    opened.Contains(Uri.EscapeDataString("测试 关键词"), StringComparison.Ordinal),
                    opened);
            }
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
        }

        // ④ 取不到选区时不打开浏览器（用测试桩绕开真实剪贴板）
        ctx.CaptureTextForTest = () => "";
        ctx.ManualCopyWaitForTest = _ => "";
        opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        try
        {
            ctx.SearchSelection("bd", "   ");
            Check("取不到选区时不搜索", opened == null, opened ?? "(null)");
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
        }

        // ⑤ 未知引擎安全返回且不崩
        Check("未知引擎不抛异常", ctx.SearchSelection("no_such_engine", "abc"));

        TestDirectSearchEmptyArgRegression(ctx, cat);
        TestSelectionRobustness();
    }

    /// <summary>
    /// 「直接搜索」里的自定义命令（qcmdsearch:）：把选中的文字交给 Qbar 命令。
    ///
    /// 这条链路支撑「选中单词按一下键直接查欧路词典」：
    /// 命令表 → 动作项 → 读选区 → 套参数模板。真跑会开 exe，
    /// 所以用拦截钩子看实际交出去的参数。
    /// </summary>
    private static void TestCommandSearchAction()
    {
        var settings = DefaultSettings.Create();
        settings.Qbar.Commands.Add(new QbarCommand
        {
            Id = "user.ol", Keyword = "ol", Title = "欧路词典查词",
            Path = "eudic.exe", Parameter = "-w {query}", RunAsAdmin = true,
        });

        var notes = new List<string>();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new Core.Actions.MouseService(),
            Notify = n => notes.Add(n),
        };
        var cat = Core.Actions.ActionCatalog.Create(ctx);

        var item = cat.Items.FirstOrDefault(i => i.Id == "qcmdsearch:user.ol");
        Check("动作表有自定义命令的直接搜索项", item != null,
            string.Join(",", cat.Items.Where(i => i.Id.StartsWith("qcmdsearch:")).Select(i => i.Id)));
        CheckEqual("归入「直接搜索」分类", item?.Category, "直接搜索");
        Check("动作名带上命令名", item != null && item.Title.Contains("欧路词典查词"), item?.Title);
        Check("内部命令（没有程序）不进直接搜索",
            !cat.Items.Any(i => i.Id == "qcmdsearch:" + InputPlus.Core.Qbar.Search.QbarCommandStore.BuiltinNewFileId),
            string.Join(",", cat.Items.Where(i => i.Id.StartsWith("qcmdsearch:")).Select(i => i.Id)));

        QbarCommand? ran = null;
        string? ranText = null;
        Core.Actions.ActionContext.RunQbarCommandInterceptForTest = (c, t) => { ran = c; ranText = t; };
        try
        {
            Check("按 Id 能执行命令", ctx.SearchSelectionWithCommand("user.ol", "hello"));
            CheckEqual("交给命令的是选中的文字", ranText, "hello");
            CheckEqual("解析到的是这条命令", ran?.Id, "user.ol");
            Check("成功后给用户一句反馈",
                notes.Any(n => n.Contains("欧路词典查词")), string.Join(" | ", notes));

            Check("按关键字也能找到同一条命令",
                ctx.SearchSelectionWithCommand("ol", "world") && ranText == "world", ranText);

            // 换行/多词原样传：加不加引号是用户在模板里决定的
            ctx.SearchSelectionWithCommand("user.ol", "  two words  ");
            CheckEqual("文字只去首尾空白", ranText, "two words");
        }
        finally
        {
            Core.Actions.ActionContext.RunQbarCommandInterceptForTest = null;
        }

        ran = null;
        notes.Clear();
        Check("未知命令不抛异常", ctx.SearchSelectionWithCommand("no_such_cmd", "x"));
        Check("未知命令有明确提示",
            notes.Any(n => n.Contains("找不到这条命令")), string.Join(" | ", notes));
        Check("未知命令不执行任何东西", ran == null);

        // 没有选区：要提示「先选中」，而不是把空串交给 exe
        ran = null;
        ranText = null;
        notes.Clear();
        ctx.CaptureTextForTest = () => "   ";
        ctx.ManualCopyWaitForTest = _ => "";
        Core.Actions.ActionContext.RunQbarCommandInterceptForTest = (c, t) => { ran = c; ranText = t; };
        try
        {
            // 返回 true = 这次按键已被处理（哪怕只是提示），执行与否看下面两条
            Check("没有选中文字时按键仍算已处理", ctx.SearchSelectionWithCommand("user.ol"));
            Check("没有选中文字有提示",
                notes.Any(n => n.Contains("没有取到选中的文字")), string.Join(" | ", notes));
            Check("没有选中文字时命令参数为空", ran == null && string.IsNullOrEmpty(ranText));
        }
        finally
        {
            Core.Actions.ActionContext.RunQbarCommandInterceptForTest = null;
            ctx.CaptureTextForTest = null;
        }
    }

    /// <summary>
    /// 取词链路的几个易错点（v5.9.8）：
    /// 提示里的文字摘要不能把换行/代理对搞坏；
    /// 合成 Ctrl+C 之前等修饰键松开的那步必须很快返回，不能把动作卡住。
    /// </summary>
    private static void TestSelectionRobustness()
    {
        Section("直接搜索 · 取词健壮性（v5.9.8）");

        CheckEqual("摘要原样返回短文本", Core.Actions.ActionContext.Preview("abc"), "abc");
        CheckEqual("摘要把换行压成空格", Core.Actions.ActionContext.Preview("a\r\nb"), "a b");
        CheckEqual("摘要把连续空白压成一个", Core.Actions.ActionContext.Preview("a    b"), "a b");
        CheckEqual("摘要空输入返回空串", Core.Actions.ActionContext.Preview(null), "");
        CheckEqual("摘要去掉首尾空白", Core.Actions.ActionContext.Preview("  ab  "), "ab");

        string longText = new string('a', 40);
        string cut = Core.Actions.ActionContext.Preview(longText, 10);
        Check("摘要超长时截断", cut.Length == 11 && cut.EndsWith("…", StringComparison.Ordinal), cut);

        // 截断点正好落在 emoji 的中间时不能劈开代理对（劈开会渲染成方块）
        string emoji = new string('a', 17) + "\U0001F600" + "tail";
        string emojiCut = Core.Actions.ActionContext.Preview(emoji, 18);
        Check("摘要截断不劈开代理对",
            !emojiCut.Any(char.IsSurrogate),
            "len=" + emojiCut.Length + " 尾部=" + (int)emojiCut[^2]);

        // 等修饰键松开：没按键时应当立刻返回（上限给足，避免测试机卡顿误报）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool threw = false;
        try
        {
            Core.Text.TextActions.WaitPhysicalModifiersUp();
        }
        catch (Exception ex)
        {
            threw = true;
            CoreLog.Swallow(ex, "自检：等修饰键松开");
        }
        sw.Stop();
        Check("等修饰键松开不抛异常", !threw);
        Check("等修饰键松开不会拖住动作", sw.ElapsedMilliseconds < 1500, sw.ElapsedMilliseconds + "ms");
    }

    /// <summary>
    /// 回归测试：按键绑定里 Arg 是**空字符串**时，直接搜索必须照常工作。
    ///
    /// 这是 v3.5.0 修掉的核心 bug —— 绑定 <c>search:bd</c> 的 Arg 默认是 ""，
    /// 老代码用 <c>overrideText ?? …</c> 判断，空串不算 null，
    /// 于是永远判定"没有选中的文字"，按了键什么也不发生。
    /// 这个用例必须走**动作表**（和真实热键同一条路径），不能直接调 SearchSelection，
    /// 否则测不到参数传递这一层。
    /// </summary>
    private static void TestDirectSearchEmptyArgRegression(Core.Actions.ActionContext ctx,
                                                           Core.Actions.ActionCatalog cat)
    {
        Section("直接搜索 · 空参数回归（v3.5.0）");

        ctx.CaptureTextForTest = () => "被选中的文字ABC";
        ctx.ManualCopyWaitForTest = _ => "";

        string? opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        try
        {
            // ① 空字符串参数（真实绑定的默认值）
            opened = null;
            bool ok = cat.Invoke("search:bd", "");
            Check("空参数时动作执行成功", ok);
            Check("空参数时真的搜索了", opened != null, opened ?? "(null)");
            if (opened != null)
            {
                Check("空参数时关键词正确",
                    opened.Contains(Uri.EscapeDataString("被选中的文字ABC"), StringComparison.Ordinal),
                    opened);
            }

            // ② 纯空白参数（绑定编辑器里手敲空格）
            opened = null;
            cat.Invoke("search:bd", "   ");
            Check("空白参数时真的搜索了", opened != null, opened ?? "(null)");

            // ③ null 参数（老调用方式）
            opened = null;
            cat.Invoke("search:bd", null!);
            Check("null 参数时真的搜索了", opened != null, opened ?? "(null)");

            // ④ 显式参数优先于选区
            opened = null;
            cat.Invoke("search:bd", "显式参数XYZ");
            Check("显式参数优先",
                opened != null && opened.Contains(Uri.EscapeDataString("显式参数XYZ"), StringComparison.Ordinal),
                opened ?? "(null)");

            // ⑤ 主动取词失败 → 保底「手动 Ctrl+C」拿到的内容也要能搜
            ctx.CaptureTextForTest = () => "";
            ctx.ManualCopyWaitForTest = _ => "手动复制的内容";
            opened = null;
            cat.Invoke("search:bd", "");
            Check("保底手动复制路径可用",
                opened != null && opened.Contains(Uri.EscapeDataString("手动复制的内容"), StringComparison.Ordinal),
                opened ?? "(null)");

            // ⑥ 引擎地址里没写 {q} 时不能把关键词丢掉
            var noPlaceholder = DefaultSettings.Create();
            noPlaceholder.Search.Add(new Core.Settings.QEntry
            {
                Key = "noplaceholder",
                Value = "https://example.com/search",
                Comment = "自定义",
            });
            var ctx2 = new Core.Actions.ActionContext
            {
                Settings = () => noPlaceholder,
                Clipboard = new Core.Actions.ClipboardService(),
                Windows = new Core.Actions.WindowActions(),
                WindowBindings = new WindowBindingService(),
                Mouse = new Core.Actions.MouseService(),
                Notify = _ => { },
                CaptureTextForTest = () => "关键词QQ",
            };
            opened = null;
            ctx2.SearchSelection("noplaceholder");
            Check("缺 {q} 时仍带上关键词",
                opened != null && opened.Contains(Uri.EscapeDataString("关键词QQ"), StringComparison.Ordinal),
                opened ?? "(null)");
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
            ctx.CaptureTextForTest = null;
            ctx.ManualCopyWaitForTest = null;
        }
    }

    /// <summary>
    /// 「直接搜索」应覆盖 Qbar 面板里的**全部**搜索引擎，而不只是常用的几个。
    /// </summary>
    private static void TestDirectSearchCoversAllEngines()
    {
        var settings = DefaultSettings.Create();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        var cat = Core.Actions.ActionCatalog.Create(ctx);

        // 设置里有多少可用引擎，就应该有多少 search: 动作
        int engineCount = settings.Search.Count(x =>
            !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(x.Value));

        // 「直接搜索」里还有自定义命令（qcmdsearch:），这一步只数引擎
        int actionCount = cat.Items.Count(i =>
            i.Category == "直接搜索" && i.Id.StartsWith("search:", StringComparison.Ordinal));

        Check("直接搜索动作数 = 全部引擎数",
            actionCount == engineCount, $"{actionCount} vs {engineCount}");
        Check("引擎数量足够多（>50）", engineCount > 50, engineCount.ToString());

        // 抽查几个非常用引擎也在
        foreach (string key in new[] { "bd", "zh", "go" })
        {
            Check($"常用引擎 {key} 有动作", cat.Items.Any(i => i.Id == "search:" + key));
        }
    }

    /// <summary>
    /// 文件搜索过滤器要作为动作出现在「Qbar 搜索」和「直接搜索」两个分类里。
    ///
    /// 用户的原话是"把 qbar 本地搜索过滤器规则（wd 只搜索文档 等等功能）
    /// 加入直接搜索和 qbar 搜索分类，作为功能" —— 也就是能绑到按键上。
    /// </summary>
    private static void TestFilterActions()
    {
        var settings = DefaultSettings.Create();
        var notes = new List<string>();
        var requests = new List<(string? Id, string? Query)>();

        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new Core.Actions.MouseService(),
            Notify = n => notes.Add(n),
        };
        ctx.QbarFileSearchRequested += (id, query) => requests.Add((id, query));

        // 关掉的过滤器不该出现在绑定列表里
        settings.Qbar.Filters.Add(new InputPlus.Core.Qbar.Search.QbarFilter
        {
            Id = "user.off", Name = "关掉的过滤器", Keyword = "zzoff", Enabled = false,
        });

        var cat = Core.Actions.ActionCatalog.Create(ctx);

        var qbarItem = cat.Get("qfilter:builtin.doc");
        Check("过滤器「文档」有「Qbar 搜索」动作", qbarItem != null);
        Check("过滤器动作归在「Qbar 搜索」分类", qbarItem?.Category == "Qbar 搜索", qbarItem?.Category ?? "(null)");

        var directItem = cat.Get("fsearch:builtin.doc");
        Check("过滤器「文档」有「直接搜索」动作", directItem != null);
        Check("过滤器动作归在「直接搜索」分类", directItem?.Category == "直接搜索", directItem?.Category ?? "(null)");

        Check("关掉的过滤器不注册动作", cat.Get("qfilter:user.off") == null && cat.Get("fsearch:user.off") == null);

        // ①Qbar 搜索：只带过滤器打开，不带词
        requests.Clear();
        bool ok = cat.Invoke("qfilter:builtin.doc", "");
        Check("执行过滤器动作返回成功", ok);
        Check("过滤器动作请求的是 builtin.doc",
            requests.Count == 1 && requests[0].Id == "builtin.doc" && requests[0].Query == null,
            string.Join(",", requests.Select(r => r.Id + "|" + (r.Query ?? "null"))));

        // ②直接搜索：取到词之后带着词把面板打开（取词用自检钩子，不碰真实选区）
        requests.Clear();
        ctx.CaptureTextForTest = () => "  报表  ";
        ok = cat.Invoke("fsearch:builtin.doc", "");
        Check("直接搜索过滤器返回成功", ok);
        Check("选区文字被 trim 后带进面板",
            requests.Count == 1 && requests[0].Id == "builtin.doc" && requests[0].Query == "报表",
            string.Join(",", requests.Select(r => r.Id + "|" + (r.Query ?? "null"))));

        // ③过滤器被删掉之后，绑定里留着旧 Id 也要说清是哪条没了
        notes.Clear();
        ctx.CaptureTextForTest = () => "报表";
        ok = cat.Invoke("fsearch:builtin.doc", "");
        Check("过滤器还在时不再报错", notes.Count == 0, string.Join(" | ", notes));

        bool handled = ctx.SearchSelectionAsFile("user.已经没了", "报表");
        Check("找不到过滤器时仍然吞掉这次按键", handled);
        Check("找不到过滤器时给明确提示",
            notes.Any(n => n.Contains("找不到这个过滤器")), string.Join(" | ", notes));

        ctx.CaptureTextForTest = null;
    }

    /// <summary>
    /// 查词动作（dict.lookup / qdict.lookup）：欧路词典也要能绑键。
    ///
    /// 这条自检刻意**不启动任何进程**：真机上欧路装着的，
    /// 跑测试弹出词典窗口抢焦点是最糟的副作用 —— 三个钩子全拦掉。
    /// </summary>
    private static void TestDictionaryActions()
    {
        var settings = DefaultSettings.Create();
        var notes = new List<string>();
        var words = new List<string?>();

        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new Core.Actions.MouseService(),
            Notify = n => notes.Add(n),
        };
        ctx.QbarDictionaryRequested += w => words.Add(w);

        var cat = Core.Actions.ActionCatalog.Create(ctx);

        var direct = cat.Get("dict.lookup");
        Check("查词有「直接搜索」动作", direct != null);
        Check("查词动作归在「直接搜索」分类", direct?.Category == "直接搜索", direct?.Category ?? "(null)");

        var qbarItem = cat.Get("qdict.lookup");
        Check("查词有「Qbar 搜索」动作", qbarItem != null);
        Check("查词动作归在「Qbar 搜索」分类", qbarItem?.Category == "Qbar 搜索", qbarItem?.Category ?? "(null)");

        // ① Qbar 搜索：取词 → 请求面板带词打开
        ctx.CaptureTextForTest = () => "  银行  ";
        words.Clear();
        bool ok = cat.Invoke("qdict.lookup", "");
        Check("查词动作返回成功", ok);
        Check("查词的词被 trim 后交给面板",
            words.Count == 1 && words[0] == "银行",
            string.Join(",", words.Select(w => w ?? "null")));

        try
        {
            // ② 没装欧路：走网页词典（钩子拦掉浏览器）
            string? opened = null;
            Core.Qbar.DictionaryLauncher.FindProgramForTest = () => null;
            Core.Qbar.DictionaryLauncher.OpenWebForTest = url => opened = url;

            notes.Clear();
            ctx.CaptureTextForTest = () => "hello";
            ok = cat.Invoke("dict.lookup", "");
            Check("直接查词返回成功", ok);
            Check("没装欧路时打开网页词典",
                opened != null && opened.Contains("dict.eudic.net") && opened.EndsWith("/hello", StringComparison.Ordinal),
                opened ?? "(null)");
            Check("网页兜底会说明没装欧路", notes.Any(n => n.Contains("网页词典")), string.Join(" | ", notes));

            // ③ 有欧路：交给命令执行，参数留空要补出厂 -w "{query}"
            Core.Qbar.Search.QbarCommand? ran = null;
            string ranTail = "";
            Core.Qbar.DictionaryLauncher.FindProgramForTest = () => @"C:\tools\eudic.exe";
            Core.Qbar.DictionaryLauncher.ExecuteForTest = (cmd, tail) => { ran = cmd; ranTail = tail; };

            notes.Clear();
            ok = cat.Invoke("dict.lookup", "hello");
            Check("有欧路时交给命令执行", ok && ran != null);
            Check("参数留空补出厂模板",
                ran?.Parameter == Core.Qbar.Search.QbarCommandStore.DefaultDictParameter,
                ran?.Parameter ?? "(null)");
            Check("程序来自定位器", ran?.Path == @"C:\tools\eudic.exe", ran?.Path ?? "(null)");
            Check("交给程序的词没有多余空白", ranTail == "hello", ranTail);
            Check("成功时不再提示网页词典", notes.All(n => !n.Contains("网页词典")), string.Join(" | ", notes));

            // ④ 用户指定的程序和参数优先于定位器（换别的词典也照走）
            var custom = new Core.Qbar.Search.QbarCommand
            {
                Id = "user.ol", Keyword = "ol", Path = @"D:\dict\user.exe", Parameter = "--word {query}",
            };
            ran = null;
            var r = Core.Qbar.DictionaryLauncher.Launch("hello", custom);
            Check("用户指定的程序优先", r.Ok && ran?.Path == @"D:\dict\user.exe", ran?.Path ?? "(null)");
            Check("用户参数模板原样保留", ran?.Parameter == "--word {query}", ran?.Parameter ?? "(null)");

            // ⑤ 空词什么都不启动
            ran = null;
            var empty = Core.Qbar.DictionaryLauncher.Launch("   ", null);
            Check("空词只给提示、不启动程序", !empty.Ok && empty.Message.Length > 0 && ran == null, empty.Message);

            // ⑥ 网页地址要编码中文，否则浏览器会把词截断
            string url = Core.Qbar.DictionaryLauncher.DictionaryUrl("银行");
            Check("网页词典地址会编码中文",
                url.StartsWith("https://dict.eudic.net/dicts/en/", StringComparison.Ordinal) && !url.Contains('行'),
                url);
        }
        finally
        {
            Core.Qbar.DictionaryLauncher.FindProgramForTest = null;
            Core.Qbar.DictionaryLauncher.ExecuteForTest = null;
            Core.Qbar.DictionaryLauncher.OpenWebForTest = null;
            ctx.CaptureTextForTest = null;
        }
    }

    private static void TestQbarEngineActions()
    {
        var settings = DefaultSettings.Create();
        string? requested = "(未请求)";
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        ctx.QbarRequested += key => requested = key ?? "(null)";

        var cat = Core.Actions.ActionCatalog.Create(ctx);

        // 常见引擎都必须有动作
        foreach (string key in new[] { "bd", "zh", "bilis", "go", "bi", "tb", "jd", "gh", "db", "wx" })
        {
            var item = cat.Items.FirstOrDefault(i => i.Id == "qbar:" + key);
            Check($"引擎 {key} 有可用动作", item != null,
                string.Join(",", cat.Items.Where(i => i.Id.StartsWith("qbar:")).Select(i => i.Id)));
            if (item == null) continue;
            Check($"引擎 {key} 的动作在「Qbar 搜索」分类", item.Category == "Qbar 搜索", item.Category);
        }

        /* 「Qbar 搜索」保留常用引擎的快捷动作（数量受控），
         * 其余引擎走「Qbar 快速启动」的参数 —— 那个动作填什么关键字就用什么引擎，
         * 所以"面板里所有引擎"在按键映射里都能用。 */
        /* 「Qbar 搜索」现在注册**全部**引擎（用户要求"按键映射里要能选到所有引擎"）。
         * 早先那次"动作太多导致键盘页卡死"的真实原因是巡检定时器，与条数无关；
         * 定时器在诊断模式已停用，所以这里改为校验"覆盖完整"。 */
        int engineActions = cat.Items.Count(i => i.Id.StartsWith("qbar:"));
        var expectedQbarKeys = new HashSet<string>(
            settings.Search
                .Where(x => !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => x.Key),
            StringComparer.OrdinalIgnoreCase);
        foreach (var web in settings.Web.Where(x => !string.IsNullOrWhiteSpace(x.Value)))
        {
            if (!string.IsNullOrWhiteSpace(web.Key)) expectedQbarKeys.Add(web.Key);
        }
        int engines = expectedQbarKeys.Count;

        Check("引擎动作覆盖全部引擎", engineActions == engines, $"{engineActions}/{engines}");
        Check("引擎动作够用（>= 20）", engineActions >= 20, engineActions.ToString());

        var biliAction = cat.Items.FirstOrDefault(i => i.Id == "qbar:bili");
        Check("bili 有打开首页的 Qbar 动作", biliAction != null && biliAction.Category == "Qbar 网址",
            biliAction?.Category);

        var qbarAction = cat.Items.First(i => i.Id == "qbar");
        Check("Qbar 动作接受引擎参数", qbarAction.NeedsArg, "");

        requested = "(未请求)";
        cat.Invoke("qbar", "bili");
        Check("Qbar 动作把参数当引擎传下去", requested == "bili", requested);

        // default 引擎不该成为动作
        Check("default 引擎没有动作", !cat.Items.Any(i => i.Id == "qbar:default"));

        // 执行动作必须把关键字传出来
        cat.Invoke("qbar:bd", "");
        CheckEqual("执行 qbar:bd 时传出引擎关键字", requested, "bd");

        cat.Invoke("qbar:zh", "");
        CheckEqual("执行 qbar:zh 时传出引擎关键字", requested, "zh");
    }

    /// <summary>
    /// 空面板最上面的「最近打开的项目」。
    ///
    /// 验四件事：
    ///   · 分类（文件/文件夹/网址/程序）认得对，且坏输入不抛；
    ///   · 同目标去重并前移、Top(n) 截断；
    ///   · 落盘往返（写出去 → 重新读回来还在）与坏 JSON 容错；
    ///   · 只在**空面板**出现：带关键字、带引擎标签、RecentCount=0 时都不许冒出来。
    ///
    /// 全程把存储指到临时文件（StorePathForTest），跑完恢复，
    /// 不碰用户真正的 recent-targets.json。
    /// </summary>
    private static void TestQbarRecentTargets()
    {
        // ① 分类器：四种目标 + 空串
        CheckEqual("最近项：网址归为 Url",
            RecentTargets.Classify("https://github.com/input-plus"), RecentTargetKind.Url);
        CheckEqual("最近项：文件夹归为 Folder",
            RecentTargets.Classify(Path.GetTempPath().TrimEnd('\\')), RecentTargetKind.Folder);
        CheckEqual("最近项：可执行程序归为 Program",
            RecentTargets.Classify(@"C:\Windows\notepad.exe"), RecentTargetKind.Program);
        CheckEqual("最近项：普通文件归为 File",
            RecentTargets.Classify(@"C:\Windows\win.ini"), RecentTargetKind.File);
        CheckEqual("最近项：空目标不抛异常", RecentTargets.Classify(""), RecentTargetKind.File);

        string? savedStore = RecentTargets.StorePathForTest;
        string dir = Path.Combine(Path.GetTempPath(), "ip-qbar-recent-" + Guid.NewGuid().ToString("N"));
        string store = Path.Combine(dir, "recent-targets.json");

        try
        {
            RecentTargets.StorePathForTest = store;
            RecentTargets.Clear();

            // ② 去重：同一目标（大小写不敏感）只留一条，前移并累加次数
            RecentTargets.Record(@"D:\自检\甲.txt");
            RecentTargets.Record(@"D:\自检\乙.txt");
            RecentTargets.Record(@"D:\自检\甲.txt");

            var top = RecentTargets.Top(10);
            Check("最近项：重复打开只留一条", top.Count == 2, top.Count.ToString());
            CheckEqual("最近项：重复打开后前移", top[0].Target, @"D:\自检\甲.txt");
            Check("最近项：重复打开累加次数", top[0].Uses == 2, top[0].Uses.ToString());
            CheckEqual("最近项：标题默认取文件名", top[0].Title, "甲.txt");

            // ③ Top(n) 截断
            for (int i = 0; i < 5; i++) RecentTargets.Record($@"D:\自检\第{i}.txt");
            Check("最近项：Top(n) 按条数截断", RecentTargets.Top(3).Count == 3,
                RecentTargets.Top(3).Count.ToString());

            // ④ 落盘往返：写出去 → 丢掉内存 → 重新读，还在
            RecentTargets.Flush();
            RecentTargets.Reload();
            var loaded = RecentTargets.Top(10);
            Check("最近项：落盘之后重新读还在",
                loaded.Any(x => x.Target == @"D:\自检\第4.txt"), loaded.Count.ToString());

            // ⑤ 坏文件：当空列表继续，不抛
            File.WriteAllText(store, "{ 这不是 JSON，是坏文件");
            RecentTargets.Reload();
            bool threw = false;
            int brokenCount = -1;
            try { brokenCount = RecentTargets.Count; }
            catch { threw = true; }
            Check("最近项：文件损坏不抛异常且按空列表继续",
                !threw && brokenCount == 0, threw ? "抛了" : brokenCount.ToString());

            // ⑥ 空面板：最近项排在最前面，徽标是「最近」
            var settings = DefaultSettings.Create();
            settings.Qbar.RecentCount = 5;
            var qbar = new QbarService(() => settings, () => new List<string>());

            RecentTargets.Record(@"D:\自检\空面板目标.txt", "空面板目标");
            var empty = qbar.Query("", 20);
            Check("最近项：空面板第一行就是它",
                empty.Count > 0
                && empty[0].Badge == Loc.T("最近")
                && empty[0].Payload == @"D:\自检\空面板目标.txt",
                empty.Count > 0 ? empty[0].Title : "空");
            CheckEqual("最近项：空面板显示的是标题", empty[0].Title, "空面板目标");

            // ⑦ 非空查询里看不到
            var typed = qbar.Query("chrome", 20);
            Check("最近项：带关键字唤出时不出现",
                typed.All(x => x.Badge != Loc.T("最近")), typed.Count.ToString());

            // ⑧ 带搜索引擎标签唤出（空输入）也看不到
            var chip = new QbarChip
            {
                Kind = ChipKind.Search,
                Key = "bd",
                DisplayName = "百度",
                Value = "https://www.baidu.com/s?wd={q}",
            };
            var chipped = qbar.Query("", chip, 20);
            Check("最近项：带搜索引擎唤出时不出现",
                chipped.All(x => x.Badge != Loc.T("最近")), chipped.Count.ToString());

            // ⑨ RecentCount = 0 时整组消失
            settings.Qbar.RecentCount = 0;
            var off = qbar.Query("", 20);
            Check("最近项：RecentCount=0 时空面板不显示",
                off.All(x => x.Badge != Loc.T("最近")), off.Count.ToString());

            // ⑩ 设置取值夹到 0..20
            settings.Qbar.RecentCount = 99;
            SettingsStore.Normalize(settings);
            CheckEqual("最近项：设置上限夹到 20", settings.Qbar.RecentCount, 20);
            settings.Qbar.RecentCount = -3;
            SettingsStore.Normalize(settings);
            CheckEqual("最近项：设置下限夹到 0", settings.Qbar.RecentCount, 0);
        }
        finally
        {
            RecentTargets.StorePathForTest = savedStore;
            RecentTargets.Reload();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { /* 自检清理失败无副作用 */ }
        }
    }
}