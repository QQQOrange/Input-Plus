using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InputPlus.Core.Gestures;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Scripting;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;

namespace InputPlus;

/// <summary>
/// 自检：中键菜单（v1.3.16）。
///
/// 这一组全部是**纯对象**：决策表、类名分类、菜单内容、老配置默认值。
/// 不去碰鼠标钩子、不真的点桌面 —— 那两样在 CI 机器上不存在，写进去只会变成
/// "本机绿、CI 红"的假用例。真机那部分（LVM_HITTEST / UIA）放在文件末尾，
/// 探不到桌面就明确标成"跳过"，不伪装成通过。
/// </summary>
public static partial class SelfTest
{
    /// <summary>造一条决策输入，只写关心的字段。</summary>
    private static MiddleMenuInput MiddleCase(
        bool down,
        bool middleVk = true,
        bool injected = false,
        bool menuOpen = false,
        bool enabled = true,
        bool inScope = true,
        bool taskbar = false,
        bool fullscreen = false,
        bool takeable = true,
        bool hasGesture = false)
        => new(middleVk, down, injected, menuOpen, enabled, inScope, taskbar, fullscreen, takeable, hasGesture);

    private static void TestMiddleMenu()
    {
        // ── ① 桌面空白：按下接管，抬起弹 ──────────────────────────────
        CheckEqual("中键菜单：桌面空白 + 没配手势 → 按下接管",
            MiddleMenuPolicy.Decide(MiddleCase(down: true)), MiddleMenuAction.SwallowTap);
        CheckEqual("中键菜单：桌面空白 + 没配手势 → 抬起不由决策表处理",
            MiddleMenuPolicy.Decide(MiddleCase(down: false)), MiddleMenuAction.Ignore);

        // ── ② 桌面图标上：不接管（不吞，原样放行）────────────────────
        CheckEqual("中键菜单：点在桌面图标上 → 不接管",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, takeable: false)), MiddleMenuAction.Ignore);

        // ── ③ 浏览器等别的程序：不在范围内 → 不接管 ──────────────────
        CheckEqual("中键菜单：浏览器里 → 不接管（自动滚动照旧）",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, inScope: false)), MiddleMenuAction.Ignore);

        // ── ④ 中键画一笔：手势优先 ──────────────────────────────────
        CheckEqual("中键菜单：配了中键手势 → 按下交给手势",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, hasGesture: true)), MiddleMenuAction.StartGesture);
        CheckEqual("中键菜单：手势没成笔 + 可弹 → 弹菜单代替补发",
            MiddleMenuPolicy.Decide(MiddleCase(down: false, hasGesture: true)), MiddleMenuAction.MenuInsteadOfTap);
        CheckEqual("中键菜单：手势没成笔但不可弹 → 照旧补发中键",
            MiddleMenuPolicy.Decide(MiddleCase(down: false, hasGesture: true, takeable: false)), MiddleMenuAction.Ignore);

        /* ── ④'' 手势**不受"范围"限制**（v1.5 修）──────────────────
         *
         * 这一组是"WPS / Edge / Electron 程序里中键手势全没了"的护栏（真机复现：
         * 诊断日志里 `arm: vk=0x04 hasGesture=True` 之后紧跟"不在附加菜单的范围里：中键原样放行"）。
         * 「范围」只决定**菜单**能不能弹，不决定这颗键归不归我们：
         *   · 配了手势 → 按下在任何窗口都交给手势；
         *   · 没配手势 → 范围外原样放行（浏览器自动滚动 / 编辑器粘贴）。 */
        CheckEqual("中键手势：范围外（浏览器/WPS）+ 配了手势 → 按下照样交给手势",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, inScope: false, takeable: false, hasGesture: true)),
            MiddleMenuAction.StartGesture);
        CheckEqual("中键手势：范围外没成笔 → 不弹菜单（交给补发原生中键）",
            MiddleMenuPolicy.Decide(MiddleCase(down: false, inScope: false, takeable: false, hasGesture: true)),
            MiddleMenuAction.Ignore);
        CheckEqual("中键菜单：范围外 + 没配手势 → 中键原样放行（不吞不补发）",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, inScope: false, takeable: false)),
            MiddleMenuAction.Ignore);

        /* ── ④' 资源管理器与文件对话框（v1.5.0）────────────────────────
         * 这两处"点在文件上"也要弹，所以 InScope=true + Takeable=true；
         * 决策表本身不区分落点，区分在 GestureService 那边算 Takeable 的时候。 */
        CheckEqual("中键菜单：资源管理器文件列表 → 按下接管",
            MiddleMenuPolicy.Decide(MiddleCase(down: true)), MiddleMenuAction.SwallowTap);
        CheckEqual("中键菜单：文件对话框 → 按下接管",
            MiddleMenuPolicy.Decide(MiddleCase(down: true)), MiddleMenuAction.SwallowTap);

        // ── ⑤ 注入的中键：一律不弹 ─────────────────────────────────
        CheckEqual("中键菜单：注入的中键 → 不接管",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, injected: true)), MiddleMenuAction.Ignore);
        CheckEqual("中键菜单：注入 + 菜单开着，注入优先",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, injected: true, menuOpen: true)), MiddleMenuAction.Ignore);

        // ── ⑥ 开关关 / 别的键 / 任务栏 / 全屏 ───────────────────────
        CheckEqual("中键菜单：开关关闭 → 全部恢复原样",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, enabled: false)), MiddleMenuAction.Ignore);
        CheckEqual("中键菜单：不是中键 → 不接管",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, middleVk: false)), MiddleMenuAction.Ignore);
        CheckEqual("中键菜单：任务栏/托盘 → 让给 Explorer",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, taskbar: true)), MiddleMenuAction.Ignore);
        CheckEqual("中键菜单：全屏程序 → 不弹",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, fullscreen: true)), MiddleMenuAction.Ignore);

        // ── 菜单开着：再按中键关掉，且按下/抬起都吞 ─────────────────
        CheckEqual("中键菜单：菜单开着再按下 → 关掉并吞",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, menuOpen: true)), MiddleMenuAction.CloseMenuAndSwallow);
        CheckEqual("中键菜单：菜单开着时的抬起 → 吞（不重复处理）",
            MiddleMenuPolicy.Decide(MiddleCase(down: false, menuOpen: true)), MiddleMenuAction.Ignore);

        // ── 壳窗口分类：哪些地方**永远**不弹 ────────────────────────
        Check("类名：Progman 是桌面", BlankHit.IsDesktopClass("Progman", "Progman"));
        Check("类名：WorkerW 下的 SysListView32 是桌面图标", BlankHit.IsDesktopClass("SysListView32", "WorkerW"));
        Check("类名：SHELLDLL_DefView 是桌面", BlankHit.IsDesktopClass("SHELLDLL_DefView", "Progman"));
        Check("类名：DirectUIHWND 挂在 CabinetWClass 下是资源管理器列表",
            BlankHit.IsExplorerItemsClass("DirectUIHWND", "CabinetWClass"));
        Check("类名：文件对话框（#32770）里的 DirectUIHWND 不算",
            !BlankHit.IsExplorerItemsClass("DirectUIHWND", "#32770"));
        Check("类名：浏览器窗口不算（类名不是 DirectUIHWND）",
            !BlankHit.IsExplorerItemsClass("Chrome_WidgetWin_1", "Chrome_WidgetWin_1"));
        Check("类名：Win7 的 ExploreWClass 也算资源管理器",
            BlankHit.IsExplorerItemsClass("SysListView32", "ExploreWClass"));

        /* ── 中键作用范围（v1.4.3 起是"只在桌面"，v1.5.0 扩成四处）──
         *
         * 这两条是"浏览器里中键又不弹新标签页了"那个 bug 的护栏：
         * 按下那一刻要先问"指针在不在能接管的地方"，不在就直接放行（不吞、不补发）。
         * 判据是白名单 —— 资源管理器文件夹里也有 SysListView32，
         * 按"含 SysListView32 就算桌面"的宽判据会把那里的中键吞掉。 */
        Check("中键范围：Progman 算桌面", BlankHit.IsDesktopShellClasses("Progman", "Progman", false));
        Check("中键范围：桌面图标列表（root=WorkerW）算桌面",
            BlankHit.IsDesktopShellClasses("SysListView32", "WorkerW", false));
        Check("中键范围：GetShellWindow 兜底算桌面",
            BlankHit.IsDesktopShellClasses("SysListView32", "", true));
        Check("中键范围：浏览器窗口不算桌面",
            !BlankHit.IsDesktopShellClasses("Chrome_WidgetWin_1", "Chrome_WidgetWin_1", false));
        Check("中键范围：资源管理器文件夹里的列表**不算**桌面（它是 ExplorerItems）",
            !BlankHit.IsDesktopShellClasses("SysListView32", "CabinetWClass", false));
        Check("中键范围：文件对话框里的列表**不算**桌面",
            !BlankHit.IsDesktopShellClasses("SysListView32", "#32770", false));

        // ── 落点分类（v1.5.0）：四种界面 + 三条"永远不接管" ──────────
        CheckEqual("落点：桌面背景 → Desktop",
            MiddleMenuSurfaces.Classify("Progman", "Progman", false, true, false, out _), MiddleSurface.Desktop);
        CheckEqual("落点：root 是 shell 窗口 → Desktop",
            MiddleMenuSurfaces.Classify("SysListView32", "", true, true, false, out _), MiddleSurface.Desktop);
        CheckEqual("落点：资源管理器文件列表 → ExplorerItems",
            MiddleMenuSurfaces.Classify("DirectUIHWND", "CabinetWClass", false, true, false, out _),
            MiddleSurface.ExplorerItems);
        CheckEqual("落点：范围仅桌面时资源管理器 → None",
            MiddleMenuSurfaces.Classify("DirectUIHWND", "CabinetWClass", false, false, false, out _),
            MiddleSurface.None);
        CheckEqual("落点：打开/保存对话框 → FileDialog",
            MiddleMenuSurfaces.Classify("DirectUIHWND", "#32770", false, true, true, out _),
            MiddleSurface.FileDialog);
        CheckEqual("落点：不是文件对话框的 #32770（消息框）→ None",
            MiddleMenuSurfaces.Classify("Button", "#32770", false, true, false, out _), MiddleSurface.None);
        CheckEqual("落点：范围仅桌面时连文件对话框也不接管",
            MiddleMenuSurfaces.Classify("DirectUIHWND", "#32770", false, false, true, out _), MiddleSurface.None);
        CheckEqual("落点：浏览器的正文区 → None（自动滚动照旧）",
            MiddleMenuSurfaces.Classify("Chrome_RenderWidgetHostHWND", "Chrome_WidgetWin_1", false, true, false, out _),
            MiddleSurface.None);
        CheckEqual("落点：资源管理器的地址栏编辑框 → None（中键是粘贴）",
            MiddleMenuSurfaces.Classify("Edit", "CabinetWClass", false, true, false, out _), MiddleSurface.None);
        CheckEqual("落点：对话框里的文件名编辑框 → None",
            MiddleMenuSurfaces.Classify("Edit", "#32770", false, true, true, out _), MiddleSurface.None);

        // ── LVM_HITTEST 的返回值语义（-1 才是空白）──────────────────
        CheckEqual("命中测试：iItem=-1 判空白", BlankHit.VerdictOfItemIndex(-1), BlankVerdict.Blank);
        CheckEqual("命中测试：iItem=0 判非空白", BlankHit.VerdictOfItemIndex(0), BlankVerdict.Occupied);
        CheckEqual("命中测试：iItem=7 判非空白", BlankHit.VerdictOfItemIndex(7), BlankVerdict.Occupied);

        /* 复核用的矩形包含判断：左闭右开、上闭下开。
           LVM_HITTEST 报了一个下标之后，还要确认光标真的落在那个图标的包围盒里 ——
           真机上见过它报 0 而光标在空白处，中键菜单就永远弹不出来。 */
        var iconRect = new Core.Interop.NativeMethods.RECT { Left = 10, Top = 20, Right = 60, Bottom = 80 };
        Check("命中测试：图标矩形包含判断（左闭右开 / 上闭下开）",
            BlankHit.RectContains(iconRect, 10, 20)
            && BlankHit.RectContains(iconRect, 59, 79)
            && !BlankHit.RectContains(iconRect, 60, 79)
            && !BlankHit.RectContains(iconRect, 59, 80)
            && !BlankHit.RectContains(iconRect, 9, 50)
            && !BlankHit.RectContains(iconRect, 30, 81),
            "边界 10,20 / 59,79 算在内，60,79 与 59,80 不算");

        // ── 范围存储：字符串 → 枚举，坏值回落 ───────────────────────
        CheckEqual("范围：desktop 解析为仅桌面",
            MiddleMenuScope.ToScope("desktop"), MiddleClickMenuScope.Desktop);
        CheckEqual("范围：未知值回落默认（桌面+资源管理器）",
            MiddleMenuScope.ToScope("whatever"), MiddleClickMenuScope.DesktopAndExplorer);
        CheckEqual("范围：null 回落默认",
            MiddleMenuScope.ToScope(null), MiddleClickMenuScope.DesktopAndExplorer);
        CheckEqual("范围：存储串往返一致",
            MiddleMenuScope.ToScope(MiddleMenuScope.ToStorage(MiddleClickMenuScope.Desktop)),
            MiddleClickMenuScope.Desktop);
        Check("范围：仅桌面不包含资源管理器", !MiddleMenuScope.IncludesExplorer(MiddleClickMenuScope.Desktop));

        // ── 老配置读进来必须能跑（新字段全走默认值）────────────────
        const string oldJson = """{"SchemaVersion":1,"Global":{},"GestureOptions":{"Enabled":true}}""";
        AppSettings? old = null;
        bool oldOk = true;
        try { old = JsonSerializer.Deserialize<AppSettings>(oldJson, SettingsStore.JsonOptions); }
        catch (Exception ex) { oldOk = false; Check("老配置：反序列化不抛", false, ex.Message); }
        if (oldOk && old != null)
        {
            Check("老配置：附加菜单默认开启（用户要求桌面上中键就能唤出）", old.MiddleClickMenuEnabled);
            CheckEqual("老配置：菜单 id 默认空", old.MiddleClickMenuId, "");
            CheckEqual("老配置：鼠标唤出默认只有中键",
                string.Join(",", old.MiddleMenuMouseButtons ?? new List<string>()), "middle");
            CheckEqual("老配置：范围默认桌面+资源管理器",
                MiddleMenuScope.ToScope(old.MiddleClickMenuScope), MiddleClickMenuScope.DesktopAndExplorer);

            bool normalized = true;
            try { SettingsStore.Normalize(old); }
            catch (Exception ex) { normalized = false; Check("老配置：Normalize 不抛", false, ex.Message); }
            if (normalized) Check("老配置：Normalize 能跑通", true);
        }

        // ── 菜单内容（v1.3.17 结构）：固定项 / 历史记录 / 命令 / 打开设置 ───
        var s = new AppSettings { MiddleClickMenuEnabled = true };
        s.Qbar.Pinned.Add(new QEntry { Key = "工作", Value = @"D:\work", ShowInMiddleMenu = true });
        s.Qbar.Pinned.Add(new QEntry { Key = "没勾", Value = @"D:\ignored", ShowInMiddleMenu = false });
        s.Qbar.Pinned.Add(new QEntry
        {
            Key = "官网",
            Value = "https://example.com/",
            ShowInMiddleMenu = true,
            Comment = "示例站",
        });
        s.Qbar.Commands.Add(new QbarCommand
        {
            Id = "c1",
            Keyword = "nt",
            Title = "记事本",
            Path = @"C:\Windows\notepad.exe",
            ShowInMiddleMenu = true,
        });
        s.Qbar.Commands.Add(new QbarCommand
        {
            Id = "c2",
            Keyword = "np",
            Title = "不勾的命令",
            Path = @"C:\Windows\notepad.exe",
            ShowInMiddleMenu = false,
        });

        var history = new List<MiddleMenuSuggestion>
        {
            new("报告.docx", @"D:\报告.docx", false),
            new("Bing", "https://bing.com", true),
        };
        var titles = new Dictionary<string, string> { ["https://example.com/"] = "示例站首页" };

        var def = MiddleMenuPolicy.Build(s, history, titles);
        Check("菜单：有内容", def.Items.Count > 0, def.Items.Count.ToString());
        Check("菜单：标题栏不写介绍（Hint 为空）", def.Hint.Length == 0, def.Hint);

        // 固定项：只收勾了的，且文件夹走 openFile、网址走 webPage
        Check("菜单：勾了中键的固定项进菜单",
            def.Items.Any(i => i.Arg == @"D:\work" && i.Action == "openFile"));
        Check("菜单：没勾的固定项不进菜单", def.Items.All(i => i.Arg != @"D:\ignored"));
        Check("菜单：网址固定项走「直达网址」",
            def.Items.Any(i => i.Arg == "https://example.com/" && i.Action == "webPage"));
        Check("菜单：网址备注用抓回来的网页标题",
            def.Items.Any(i => i.Arg == "https://example.com/" && i.Note == "示例站首页"));
        Check("菜单：文件夹条目带图标来源",
            def.Items.Any(i => i.Arg == @"D:\work" && i.IconPath.Length > 0));

        // 历史记录 ▸ 搜索前建议（二级）
        var hist = def.Items.FirstOrDefault(i => i.Children.Count > 0 && i.Title == Loc.T("历史记录"));
        Check("菜单：有「历史记录」二级菜单", hist != null, "没找到");
        Check("菜单：历史记录里是建议的内容", hist != null && hist.Children.Count == 2,
            hist == null ? "-" : hist.Children.Count.ToString());
        Check("菜单：历史记录里网址走 webPage",
            hist != null && hist.Children.Any(c => c.Action == "webPage" && c.Arg == "https://bing.com"));

        // 命令 ▸ 只收勾了的
        var cmds = def.Items.FirstOrDefault(i => i.Children.Count > 0 && i.Title == Loc.T("命令"));
        Check("菜单：有「命令」二级菜单", cmds != null, "没找到");
        Check("菜单：命令二级里只出现勾选的",
            cmds != null && cmds.Children.Count == 1 && cmds.Children[0].Title == "记事本",
            cmds == null ? "-" : string.Join(",", cmds.Children.Select(c => c.Title)));
        Check("菜单：命令走「运行命令」",
            cmds != null && cmds.Children.All(c => c.Action == "runProgram"));

        Check("菜单：最后一条是打开设置",
            def.Items.Count > 0 && def.Items[def.Items.Count - 1].Action == "openSettings",
            def.Items.Count == 0 ? "空" : def.Items[def.Items.Count - 1].Action);
        Check("菜单：一级条目标题都不为空", def.Items.All(i => !string.IsNullOrWhiteSpace(i.Title)));

        /* ── 顶部「打开文件所在位置」的灰/亮（v1.5 修）──────────────────
         *
         * 判据只有一个：**有没有认出文件**。
         * 以前判的是"前台不是桌面/资源管理器"，于是 Electron / 浏览器里这一条永远亮着，
         * 点下去识别不到，就弹出一个"选择要定位的文件"（DeepSeekRuntime 里真发生的那一幕）。
         * 这四条把"默认灰 → 认出才亮"钉死。 */
        var unknown = MiddleMenuPolicy.Build(s, history, titles,
            new ForegroundFileState(true, "deepseekruntime.exe", "", false));
        var unknownItem = unknown.Items.FirstOrDefault(i => i.Action == "revealForegroundFile");
        Check("中键菜单：没认出文件时「打开文件所在位置」是灰的",
            unknownItem != null && !unknownItem.Enabled);
        Check("中键菜单：没认出文件时第二行是「识别中…」",
            unknownItem != null && unknownItem.Note == Loc.T("识别中…"));

        var known = MiddleMenuPolicy.Build(s, history, titles,
            new ForegroundFileState(true, "wps.exe", "季度总结.pptx", false));
        var knownItem = known.Items.FirstOrDefault(i => i.Action == "revealForegroundFile");
        Check("中键菜单：认出文件后「打开文件所在位置」才亮",
            knownItem != null && knownItem.Enabled);
        Check("中键菜单：认出文件后第二行写文件名",
            knownItem != null && knownItem.Note == "季度总结.pptx");

        var shell = MiddleMenuPolicy.Build(s, history, titles,
            new ForegroundFileState(false, "explorer.exe", "", false));
        var shellItem = shell.Items.FirstOrDefault(i => i.Action == "revealForegroundFile");
        Check("中键菜单：桌面/资源管理器里那一条同样是灰的",
            shellItem != null && !shellItem.Enabled);
        Check("中键菜单：桌面/资源管理器里第二行写「前台没有打开文件」",
            shellItem != null && shellItem.Note == Loc.T("前台没有打开文件"));

        // 固定项塞满 → 必须按 12 上限截断，且"打开设置"不能被挤掉
        for (int i = 0; i < 10; i++)
            s.Qbar.Pinned.Add(new QEntry { Key = "f" + i, Value = @"D:\f" + i, ShowInMiddleMenu = true });
        var capped = MiddleMenuPolicy.Build(s, history, titles);
        Check("菜单：超过上限时截断到 MaxItems",
            capped.Items.Count <= GestureMenuDef.MaxItems, capped.Items.Count.ToString());
        Check("菜单：截断后仍然保留打开设置",
            capped.Items.Any(i => i.Action == "openSettings"));

        // 指定了一份手势菜单 → 用那一份（克隆，不和设置共享对象）
        var s2 = new AppSettings { MiddleClickMenuId = "m1" };
        s2.GestureMenus.Add(new GestureMenuDef
        {
            Id = "m1",
            Name = "我的菜单",
            Items = { new GestureMenuItem { Title = "x", Action = "qbar" } },
        });
        var picked = MiddleMenuPolicy.Build(s2);
        CheckEqual("菜单：指定 id 时用那一份", picked.Name, "我的菜单");
        Check("菜单：指定菜单是克隆出来的（不与设置共享引用）",
            !ReferenceEquals(picked, s2.GestureMenus[0]));

        // 指定的菜单被删了 → 回落到内置动态菜单，不报错、不弹空窗
        var s3 = new AppSettings { MiddleClickMenuId = "gone" };
        var fallback = MiddleMenuPolicy.Build(s3);
        Check("菜单：指向已删除的菜单时回落到内置",
            fallback.Items.Any(i => i.Action == "openSettings"), fallback.Name);

        // ── 真机部分：探不到桌面就明确跳过 ──────────────────────────
        TestMiddleMenuLive();
    }

    /// <summary>
    /// 真机那一段：只在有真实桌面的机器上跑。
    ///
    /// 判据不是"有没有窗口"，而是"能不能找到桌面的 SysListView32" ——
    /// CI 的服务会话里没有 Explorer，找不到就直接记一条"跳过"，
    /// 这样报告里能看出**这条没验过**，而不是假装它绿了。
    /// </summary>
    /// <summary>
    /// 附加菜单的版式：一级和二级必须"拼在一起、长得一样"。
    ///
    /// 用户报过四件事：二级行太高、每条都拖着 URL、左右边距太大、二级压在一级菜单上。
    /// 前三件靠模板/行容器/宽度约束解决，第四件靠 PlaceSubMenu（Custom 摆放）。
    /// 这里把结论钉成几何断言 —— 出图只能给人看，机器还得有一条能红的线。
    /// </summary>
    private static void TestAttachMenuLayout()
    {
        var def = new GestureMenuDef { HideTitle = true };
        def.Items.Add(new GestureMenuItem
        {
            Title = "历史记录",
            Glyph = "\uE81C",
            Children =
            {
                new GestureMenuItem { Title = "季度总结", Note = "https://example.com/a/very/long/path" },
                new GestureMenuItem { Title = "周报", Note = "https://example.com/weekly" },
            },
        });
        def.Items.Add(new GestureMenuItem
        {
            Title = "命令",
            Children = { new GestureMenuItem { Title = "翻译", Note = "fy" } },
        });

        var win = new UI.Windows.GestureMenuWindow(def, _ => "", null)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            /* 必须放在**屏幕内**：二级菜单的摆放会问"右边还放不放得下"，
             * 窗口挪到 -32000 时所有候选都在屏外，WPF 会自己挑一个，
             * 量出来的位置就不代表真实使用场景了。 */
            Left = SystemParameters.WorkArea.Left + 40,
            Top = SystemParameters.WorkArea.Top + 40,
        };

        try
        {
            win.HoverEnabledForTest = false;
            win.AnimationsEnabledForTest = false;
            win.Show();
            DoEvents(150);
            win.UpdateLayout();

            var l1HeightBefore = win.CardForTest.ActualHeight;

            var sub = win.OpenSubMenuForRender(0);
            Check("附加菜单：带子项的条目能展开二级菜单", sub != null);
            if (sub == null) return;

            win.UpdateLayout();
            DoEvents(80);

            var card = win.CardForTest;
            CheckEqual("附加菜单：二级卡片宽度和一级一致",
                Math.Round(sub.ActualWidth), Math.Round(card.ActualWidth));

            /* 回归（v1.5.9.7）：二级展开会把窗口撑高，一级卡片以前跟着 Stretch 一起被拉长。
             * 现在一级是 VerticalAlignment=Top，高度必须保持不变。 */
            CheckEqual("附加菜单：展开二级不会把一级卡片拉长",
                Math.Round(card.ActualHeight), Math.Round(l1HeightBefore));

            win.ItemListForTest.UpdateLayout();
            win.SubListForTest.UpdateLayout();
            var topRow = win.ItemListForTest.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            var subRow = win.SubListForTest.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Check("附加菜单：一级/二级的行容器都生成了", topRow != null && subRow != null);

            if (topRow != null && subRow != null)
            {
                CheckEqual("附加菜单：二级行高和一级一致",
                    Math.Round(subRow.ActualHeight), Math.Round(topRow.ActualHeight));
                Check("附加菜单：二级行高不超过 28（收窄过）",
                    subRow.ActualHeight <= 28, subRow.ActualHeight.ToString("0.#"));

                /* 二级行里不该出现那个 URL：模板换成了只画标题的 SubEntryTemplate。 */
                bool showsUrl = Texts(subRow).Any(t => t.Contains("https://example.com", StringComparison.Ordinal));
                Check("附加菜单：二级行不显示链接地址", !showsUrl, string.Join(" | ", Texts(subRow)));
                Check("附加菜单：二级行显示名称", Texts(subRow).Any(t => t == "季度总结"),
                    string.Join(" | ", Texts(subRow)));
            }

            /* 覆盖判定：二级卡片左沿必须在一级卡片右沿之外（拼接，不是叠上去）。 */
            Point cardRight = card.PointToScreen(new Point(card.ActualWidth, 0));
            Point subLeft = sub.PointToScreen(new Point(0, 0));
            Check("附加菜单：二级拼在一级外侧（不覆盖）", subLeft.X >= cardRight.X - 1,
                $"一级右沿={cardRight.X:0.#} 二级左沿={subLeft.X:0.#} · {win.SubPlacementForTest}");

            /* 拼接判定：两块之间不许留缝（以前 2 DIP，200% 缩放下看着就是 4 个像素）。 */
            double dpi = VisualTreeHelper.GetDpi(win).DpiScaleX;
            double gapDip = (subLeft.X - cardRight.X) / Math.Max(0.1, dpi);
            Check("附加菜单：二级紧贴一级（间隙 ≤ 1 DIP）", Math.Abs(gapDip) <= 1,
                $"{gapDip:0.##} DIP · {win.SubPlacementForTest}");
        }
        finally
        {
            win.Close();
        }

        static System.Collections.Generic.IEnumerable<string> Texts(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) yield return tb.Text;
                foreach (string nested in Texts(child)) yield return nested;
            }
        }
    }

    /// <summary>
    /// 滚动条宽度：全局 10 → 7，附加菜单再细到 4。
    ///
    /// 这类"像素级手感"的改动没有断言就很容易被后人改回去（或者被模板覆盖掉）。
    /// 这里直接量真实控件：一个普通 ScrollViewer + 附加菜单二级列表。
    /// </summary>
    private static void TestThinScrollBars()
    {
        var host = new Window
        {
            Width = 180,
            Height = 120,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Left + 40,
            Top = SystemParameters.WorkArea.Top + 40,
        };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
        scroll.Content = new Border { Width = 60, Height = 600 };
        host.Content = scroll;
        try
        {
            host.Show();
            DoEvents(150);
            host.UpdateLayout();

            var bar = FindDescendant<System.Windows.Controls.Primitives.ScrollBar>(scroll);
            Check("滚动条：全局宽度收窄（≤ 8 DIP）", bar != null && bar.ActualWidth <= 8,
                bar == null ? "没找到" : bar.ActualWidth.ToString("0.#"));
        }
        finally { host.Close(); }

        string dir = Path.Combine(Path.GetTempPath(), "ip-bar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            /* v1.6.7：附加菜单的子级**只列文件夹**，所以这里要造目录；
             * 而且得造到"比一屏还高"才看得见滚动条 —— 现在每一级的 MaxHeight
             * 是工作区高度（用户要求"超出视口才内部滚动"），十几行根本滚不起来。 */
            for (int i = 0; i < 80; i++)
                Directory.CreateDirectory(Path.Combine(dir, "d" + i.ToString("000")));

            var def = new GestureMenuDef { HideTitle = true };
            def.Items.Add(new GestureMenuItem
            {
                Title = "目录",
                Arg = dir,
                Action = "openFile",
                IconPath = dir,
            });

            var win = new UI.Windows.GestureMenuWindow(def, _ => "", null)
            {
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.WorkArea.Left + 60,
                Top = SystemParameters.WorkArea.Top + 60,
            };
            try
            {
                win.HoverEnabledForTest = false;
                win.AnimationsEnabledForTest = false;
                win.Show();
                DoEvents(150);
                win.OpenFolderPeekForRender(0);
                win.UpdateLayout();
                DoEvents(80);

                var bar = FindDescendant<System.Windows.Controls.Primitives.ScrollBar>(win.SubListForTest);
                Check("滚动条：附加菜单二级更细（≤ 5 DIP）",
                    bar != null && bar.ActualWidth > 0 && bar.ActualWidth <= 5,
                    bar == null ? "没找到" : $"{bar.ActualWidth:0.#} 可见={bar.IsVisible}");
            }
            finally { win.Close(); }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 多级下钻（v1.6.7）—— 用户这一版的核心需求。
    ///
    /// 验收里能机器验的部分全在这里：
    ///   · 任意层级都能展开出下一级，直到没有子文件夹；
    ///   · 叶子不显示箭头、展开不出空面板；
    ///   · 每级卡片贴在上一级右侧、纵向对齐那一行、不溢出屏幕；
    ///   · 点任意层级的文件夹 → 走 FolderOpen（拦截后拿到归一化路径），菜单关闭；
    ///   · 祖先链判重（防循环）。重解析点由枚举选项跳过，见 FolderTree 的结构断言。
    /// </summary>
    private static void TestFolderCascade()
    {
        string root = Path.Combine(Path.GetTempPath(), "ip-cascade-" + Guid.NewGuid().ToString("N"));
        string deep = Path.Combine(root, "alpha", "beta", "gamma");
        string leaf = Path.Combine(root, "zeta");

        try
        {
            Directory.CreateDirectory(deep);
            Directory.CreateDirectory(leaf);
            File.WriteAllText(Path.Combine(leaf, "only-file.txt"), "x");   // 只有文件的目录 = 叶子
            File.WriteAllText(Path.Combine(root, "a-file.txt"), "x");      // 子级不该列文件

            /* 隐藏 / 系统目录不列：AppData、$RECYCLE.BIN 这类地方列出来只会让人误点，
             * 而且它们是"点进去能绕回用户目录"的主要来源之一。 */
            Directory.CreateDirectory(Path.Combine(root, "hidden-dir"));
            File.SetAttributes(Path.Combine(root, "hidden-dir"),
                FileAttributes.Hidden | FileAttributes.System);

            // ── ① 枚举：只列目录、排序、三态、缓存 ──────────────────
            FolderTree.ClearCache();
            var listing = FolderTree.List(root);
            Check("目录树：枚举成功", listing.Ok, listing.Detail);
            Check("目录树：只列子文件夹（文件不进来）",
                listing.Items.All(i => i.Name != "a-file.txt"),
                string.Join(" | ", listing.Items.Select(i => i.Name)));
            Check("目录树：隐藏/系统目录不进菜单",
                listing.Items.All(i => i.Name != "hidden-dir"),
                string.Join(" | ", listing.Items.Select(i => i.Name)));
            CheckEqual("目录树：按名字排序",
                string.Join(",", listing.Items.Select(i => i.Name)),
                "alpha,zeta");

            var alpha = listing.Items.First(i => i.Name == "alpha");
            var zeta = listing.Items.First(i => i.Name == "zeta");
            Check("目录树：有下级的标 true", alpha.HasSubfolders == true, alpha.HasSubfolders?.ToString() ?? "null");
            Check("目录树：只有文件的目录标 false", zeta.HasSubfolders == false, zeta.HasSubfolders?.ToString() ?? "null");
            Check("目录树：第二次走缓存", FolderTree.List(root).FromCache);
            Check("目录树：fresh 绕过缓存", !FolderTree.List(root, fresh: true).FromCache);
            CheckEqual("目录树：不存在的目录 → Missing",
                FolderTree.List(Path.Combine(root, "nope")).Failure, FolderTree.FailureKind.Missing);
            Check("目录树：枚举时跳过重解析点（防无限下钻的第一道闸）",
                FolderTree.SkipsReparsePointsForTest);

            // ── ② 窗口：一级 → 二级 → 三级 → 四级 ──────────────────
            var def = new GestureMenuDef { HideTitle = true };
            def.Items.Add(new GestureMenuItem { Title = "root", Arg = root, Action = "openFile" });

            var win = new UI.Windows.GestureMenuWindow(def, _ => "", null)
            {
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.WorkArea.Left + 40,
                Top = SystemParameters.WorkArea.Top + 40,
            };

            string? launched = null;
            try
            {
                win.HoverEnabledForTest = false;
                win.AnimationsEnabledForTest = false;
                win.Show();
                DoEvents(200);
                win.UpdateLayout();

                CheckEqual("下钻：一级能展开出二级", win.ExpandForTest(0, 0), 1);
                CheckEqual("下钻：二级内容 = 子文件夹数",
                    win.LevelListForTest(1).Items.Count, listing.Items.Count);

                int alphaIndex = IndexOfEntry(win, 1, "alpha");
                int zetaIndex = IndexOfEntry(win, 1, "zeta");
                Check("下钻：二级里能找到 alpha", alphaIndex >= 0, $"alpha={alphaIndex}");
                Check("下钻：二级里能找到 zeta", zetaIndex >= 0, $"zeta={zetaIndex}");
                if (alphaIndex < 0 || zetaIndex < 0) return;

                CheckEqual("下钻：有下级的行显示箭头",
                    win.EntryForTest(1, alphaIndex)?.ArrowVisibility ?? Visibility.Collapsed, Visibility.Visible);
                CheckEqual("下钻：叶子（只有文件）不显示箭头",
                    win.EntryForTest(1, zetaIndex)?.ArrowVisibility ?? Visibility.Visible, Visibility.Collapsed);

                /* 图标：二级起的文件夹行必须带**真图标来源**（和资源管理器同一套 shell 图标），
                 * 不是统一字形。图标本身是后台取的，这里验"来源路径给对了"。 */
                Check("下钻：二级行带资源管理器图标来源",
                    win.EntryForTest(1, alphaIndex)?.Item.IconPath == alpha.Path,
                    win.EntryForTest(1, alphaIndex)?.Item.IconPath ?? "null");

                CheckEqual("下钻：叶子展开不出面板（返回 -1）", win.ExpandForTest(1, zetaIndex), -1);
                CheckEqual("下钻：叶子不增加层级", win.LevelCountForTest, 2);

                int third = win.ExpandForTest(1, alphaIndex);
                CheckEqual("下钻：二级 → 三级", third, 2);
                int betaIndex = IndexOfEntry(win, 2, "beta");
                Check("下钻：三级里能找到 beta", betaIndex >= 0, $"beta={betaIndex}");
                if (betaIndex < 0) return;

                int fourth = win.ExpandForTest(2, betaIndex);
                CheckEqual("下钻：三级 → 四级", fourth, 3);
                int gammaIndex = IndexOfEntry(win, 3, "gamma");
                Check("下钻：四级里是 gamma", gammaIndex >= 0, $"gamma={gammaIndex}");
                if (gammaIndex < 0) return;

                var gamma = win.EntryForTest(3, gammaIndex);
                CheckEqual("下钻：最深一层只有 gamma", win.LevelListForTest(3).Items.Count, 1);
                CheckEqual("下钻：最深一层叶子无箭头",
                    gamma?.ArrowVisibility ?? Visibility.Visible, Visibility.Collapsed);
                Check("下钻：无障碍标签带层级",
                    gamma?.AccessibleLabel.Contains("第 4 级") == true, gamma?.AccessibleLabel ?? "null");
                Check("下钻：箭头按钮有无障碍名字",
                    !string.IsNullOrWhiteSpace(gamma?.ExpandLabel), gamma?.ExpandLabel ?? "null");

                // ── ③ 版式：贴在右侧、纵向对齐父行、不溢出屏幕 ──────
                var card1 = win.LevelCardForTest(1);
                var card2 = win.LevelCardForTest(2);
                Point right1 = card1.PointToScreen(new Point(card1.ActualWidth, 0));
                Point left2 = card2.PointToScreen(new Point(0, 0));
                Check("下钻：三级卡片贴在二级右侧（不覆盖）", left2.X >= right1.X - 1,
                    $"二级右沿={right1.X:0.#} 三级左沿={left2.X:0.#}");
                Check("下钻：三级紧贴二级（间隙 ≤ 1 DIP）",
                    Math.Abs(left2.X - right1.X) <= 2, $"{left2.X - right1.X:0.##} px");

                // 纵向：三级卡片顶 = 二级（alpha 那一行所在级）里 beta 那一行的顶
                win.LevelListForTest(1).UpdateLayout();
                if (win.LevelListForTest(1).ItemContainerGenerator.ContainerFromIndex(betaIndex) is ListBoxItem betaRow)
                {
                    Point rowTop = betaRow.PointToScreen(new Point(0, 0));
                    Check("下钻：子面板与父条目顶端对齐", Math.Abs(left2.Y - rowTop.Y) <= 2,
                        $"行顶={rowTop.Y:0.#} 卡片顶={left2.Y:0.#}");
                }

                Rect work = SystemParameters.WorkArea;
                Check("下钻：整条链不溢出屏幕",
                    win.Left >= work.Left - 1 && win.Left + win.ActualWidth <= work.Right + 1
                    && win.Top >= work.Top - 1 && win.Top + win.ActualHeight <= work.Bottom + 1,
                    $"窗口=({win.Left:0.#},{win.Top:0.#}) {win.ActualWidth:0.#}x{win.ActualHeight:0.#}");

                // ── ④ 祖先链判重 ─────────────────────────────────────
                Check("下钻：同一目录已在链上 → 判为循环", win.WouldCycleForTest(1, root));
                Check("下钻：不同目录不判循环", !win.WouldCycleForTest(1, Path.Combine(root, "alpha", "beta")));

                // ── ⑤ 点击任意层级文件夹 → 资源管理器（拦截）─────────
                launched = null;
                InputPlus.Core.Actions.FolderOpen.InterceptForTest = p => launched = p;
                win.ClickForTest(3, gammaIndex);
                DoEvents(500);

                CheckEqual("点击深层文件夹：交给 explorer 的路径正确",
                    launched ?? "", InputPlus.Core.Actions.FolderOpen.Normalize(deep));
                Check("点击深层文件夹：菜单请求关闭", win.FolderOpenRequested);
                Check("点击深层文件夹：不再把动作交回宿主（避免开两个窗口）", win.Selected == null);
                Check("点击深层文件夹：没有因此多展开一级", win.LevelCountForTest <= 4);
            }
            finally
            {
                InputPlus.Core.Actions.FolderOpen.InterceptForTest = null;
                try { win.Close(); } catch { }
            }

            TestFolderCascadeErrorState(root);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// 读目录失败：要显示"无权访问/目录已不存在"并且能重试；重试期间是 loading 占位。
    /// 网络路径、慢盘同理（同一个 <see cref="FolderTree.List"/> 出口）。
    /// </summary>
    private static void TestFolderCascadeErrorState(string root)
    {
        string gone = Path.Combine(root, "gone");
        Directory.CreateDirectory(Path.Combine(gone, "child"));

        var def = new GestureMenuDef { HideTitle = true };
        def.Items.Add(new GestureMenuItem { Title = "gone", Arg = gone, Action = "openFile" });

        var win = new UI.Windows.GestureMenuWindow(def, _ => "", null)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Left + 80,
            Top = SystemParameters.WorkArea.Top + 80,
        };

        try
        {
            win.HoverEnabledForTest = false;
            win.AnimationsEnabledForTest = false;
            win.Show();
            DoEvents(200);
            win.UpdateLayout();

            CheckEqual("读取失败：先展开出二级", win.ExpandForTest(0, 0), 1);
            Directory.Delete(gone, true);   // 目录在菜单开着的时候消失了

            win.ReloadFolderForTest(0, 0);
            CheckEqual("读取失败：重读时先显示 loading 占位",
                win.EntryForTest(1, 0)?.Kind ?? UI.Windows.GestureMenuEntry.EntryKind.Action,
                UI.Windows.GestureMenuEntry.EntryKind.Loading);

            DoEvents(500);
            var first = win.EntryForTest(1, 0);
            CheckEqual("读取失败：占位换成错误行",
                first?.Kind ?? UI.Windows.GestureMenuEntry.EntryKind.Action,
                UI.Windows.GestureMenuEntry.EntryKind.Error);
            Check("读取失败：错误行标红", first?.IsError == true);
            Check("读取失败：错误行提示可重试",
                first?.Title.Contains("重试") == true, first?.Title ?? "null");

            win.ClickForTest(1, 0);   // 点错误行 = 重试，不该抛
            DoEvents(300);
            Check("读取失败：错误行可点击重试（不崩）", win.LevelCountForTest >= 1);

            /* Esc：用户要求一次收起**整条展开链**（不是一层一层收）。 */
            win.CollapseChainForTest();
            CheckEqual("键盘：Esc 收起整条链（回到一级）", win.LevelCountForTest, 1);

            /* 点一个已经不存在的文件夹：必须在行上写出原因，不能"点了没反应"。 */
            win.ClickForTest(0, 0);
            DoEvents(400);
            var folderRow = win.EntryForTest(0, 0);
            Check("调起失败：行上给出明确原因",
                folderRow?.IsError == true && (folderRow?.Note.Length ?? 0) > 0,
                folderRow?.Note ?? "null");
        }
        finally
        {
            try { win.Close(); } catch { }
        }
    }

    /// <summary>
    /// 路径处理：归一化 + 命令行拼写（对应验收里"空格/中文/UNC/驱动器根/超长路径"）。
    /// 真开资源管理器没有意义（CI 没有桌面），这里只钉"交给 explorer 的那串字符"。
    /// </summary>
    private static void TestFolderOpenPaths()
    {
        string temp = Path.GetTempPath();
        CheckEqual("打开文件夹：展开 %TEMP%", InputPlus.Core.Actions.FolderOpen.Normalize("%TEMP%\\"),
            InputPlus.Core.Actions.FolderOpen.Normalize(temp));
        CheckEqual("打开文件夹：正斜杠归一 + 去尾斜杠",
            InputPlus.Core.Actions.FolderOpen.Normalize("C:/Windows/System32/"), @"C:\Windows\System32");
        CheckEqual("打开文件夹：盘符根保留反斜杠",
            InputPlus.Core.Actions.FolderOpen.Normalize(@"C:\"), @"C:\");
        CheckEqual("打开文件夹：盘符根多打的斜杠也收成一个",
            InputPlus.Core.Actions.FolderOpen.Normalize(@"C:\\"), @"C:\");
        CheckEqual("打开文件夹：去引号",
            InputPlus.Core.Actions.FolderOpen.Normalize("\"C:\\Windows\""), @"C:\Windows");
        CheckEqual("打开文件夹：UNC 去尾斜杠",
            InputPlus.Core.Actions.FolderOpen.Normalize(@"\\server\share\docs\"), @"\\server\share\docs");
        CheckEqual("打开文件夹：剥 \\\\?\\ 前缀",
            InputPlus.Core.Actions.FolderOpen.Normalize(@"\\?\C:\Windows"), @"C:\Windows");
        CheckEqual("打开文件夹：剥 \\\\?\\UNC 前缀",
            InputPlus.Core.Actions.FolderOpen.Normalize(@"\\?\UNC\srv\share"), @"\\srv\share");
        CheckEqual("打开文件夹：~ 展开到用户目录",
            InputPlus.Core.Actions.FolderOpen.Normalize("~"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        CheckEqual("命令行：盘符根不加引号（C:\\\" 会被读成 C:\"）",
            InputPlus.Core.Actions.FolderOpen.CommandLineForTest(@"C:\"), @"explorer.exe C:\");
        CheckEqual("命令行：空格整体加引号",
            InputPlus.Core.Actions.FolderOpen.CommandLineForTest(@"C:\Program Files"),
            "explorer.exe \"C:\\Program Files\"");
        CheckEqual("命令行：中文与 & 不用额外转义",
            InputPlus.Core.Actions.FolderOpen.CommandLineForTest(@"D:\资料 & 备份"),
            "explorer.exe \"D:\\资料 & 备份\"");
        CheckEqual("命令行：UNC 加引号",
            InputPlus.Core.Actions.FolderOpen.CommandLineForTest(@"\\srv\share\a b"),
            "explorer.exe \"\\\\srv\\share\\a b\"");

        var missing = InputPlus.Core.Actions.FolderOpen.TryLaunch(
            Path.Combine(temp, "ip-none-" + Guid.NewGuid().ToString("N")));
        Check("打开文件夹：不存在时明确失败（不静默）", !missing.Ok && missing.Error.Length > 0, missing.Error);

        string? seen = null;
        InputPlus.Core.Actions.FolderOpen.InterceptForTest = p => seen = p;
        try
        {
            var ok = InputPlus.Core.Actions.FolderOpen.TryLaunch(temp);
            Check("打开文件夹：存在的目录能调起", ok.Ok, ok.Error);
            CheckEqual("打开文件夹：交给 explorer 的是归一化后的绝对路径",
                seen ?? "", InputPlus.Core.Actions.FolderOpen.Normalize(temp));
        }
        finally { InputPlus.Core.Actions.FolderOpen.InterceptForTest = null; }

        /* 超长路径（>260 字符）：不带前缀的 Directory.Exists 认不出来，带 \\?\ 才行。
         * 这条用例真的造一个几百字符深的目录，验"归一化 → 存在性 → 交给 explorer 的形式"。 */
        string longRoot = Path.Combine(Path.GetTempPath(), "ip-long-" + Guid.NewGuid().ToString("N")[..8]);
        string deep = longRoot;
        try
        {
            for (int i = 0; i < 24; i++) deep = Path.Combine(deep, "abcdefghijklmnop");
            Directory.CreateDirectory(@"\\?\" + deep);
            Check("超长路径：确实超过 260 字符", deep.Length > 260, deep.Length.ToString());

            string? seenLong = null;
            InputPlus.Core.Actions.FolderOpen.InterceptForTest = p => seenLong = p;
            var result = InputPlus.Core.Actions.FolderOpen.TryLaunch(deep);
            Check("超长路径：能调起（不静默失败）", result.Ok, result.Error);
            /* 两种形态都算对：系统开了长路径支持（LongPathsEnabled）时就是原样的绝对路径，
             * 否则要补 \\?\ 前缀 —— explorer 不认不带前缀的超长路径，会静默什么都不做。 */
            Check("超长路径：交给 explorer 的形态可用",
                seenLong != null
                && (seenLong.StartsWith(@"\\?\") || string.Equals(seenLong, deep, StringComparison.OrdinalIgnoreCase))
                && seenLong.EndsWith("abcdefghijklmnop", StringComparison.OrdinalIgnoreCase),
                seenLong ?? "null");
        }
        catch (Exception ex)
        {
            Check("超长路径：用例执行", false, ex.Message);
        }
        finally
        {
            InputPlus.Core.Actions.FolderOpen.InterceptForTest = null;
            try { Directory.Delete(@"\\?\" + longRoot, true); } catch { }
        }
    }

    /// <summary>第 <paramref name="level"/> 级里名字叫 <paramref name="title"/> 的行下标（没有 = -1）。</summary>
    private static int IndexOfEntry(UI.Windows.GestureMenuWindow win, int level, string title)
    {
        var list = win.LevelListForTest(level);
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.Items[i] is UI.Windows.GestureMenuEntry entry &&
                string.Equals(entry.Title, title, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            T? nested = FindDescendant<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void TestMiddleMenuLive()
    {
        IntPtr shell = InputPlus.Core.Interop.NativeMethods.GetShellWindow();
        IntPtr defView = shell == IntPtr.Zero
            ? IntPtr.Zero
            : InputPlus.Core.Interop.NativeMethods.FindWindowEx(shell, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr list = defView == IntPtr.Zero
            ? IntPtr.Zero
            : InputPlus.Core.Interop.NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);

        if (list == IntPtr.Zero)
        {
            Check("中键菜单：真机命中判定（无桌面，跳过）", true, "跳过：这个会话里没有桌面图标列表");
            return;
        }

        /* ① 直接打列表控件：这一段不经过 WindowFromPoint，所以哪怕桌面被别的窗口盖住，
         *    跨进程那段（远程分配 + LVM_HITTEST）也会真的执行一次。
         *    这是整条链路里最容易出事的一步 —— 位数、权限、超时任一不对就永远问不出结论。 */
        var origin = new InputPlus.Core.Interop.NativeMethods.POINT(4, 4);
        var direct = BlankHit.ProbeListViewDirect(list, origin, out string directWhy);
        Check("中键菜单：跨进程 LVM_HITTEST（" + direct + " / " + directWhy + "）",
            direct != BlankVerdict.Unknown, directWhy);

        /* ② 走完整入口：会话里桌面被盖住时得到 Occupied/Other 是**正确**答案
         *    （"指针底下不是桌面"），这里只验它不崩、且给出的原因说得清。 */
        var spots = new[]
        {
            new InputPlus.Core.Interop.NativeMethods.POINT(4, 4),
            new InputPlus.Core.Interop.NativeMethods.POINT(400, 300),
        };

        var parts = new List<string>();
        foreach (var spot in spots)
        {
            var probe = BlankHit.AtPoint(spot, MiddleClickMenuScope.Desktop);
            parts.Add($"({spot.X},{spot.Y})={probe.Kind}/{probe.Verdict}");
        }

        Check("中键菜单：完整入口不崩（" + string.Join("；", parts) + "）", true);
    }
}
