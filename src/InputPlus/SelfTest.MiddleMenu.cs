using System;
using System.Linq;
using System.Text.Json;
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
        bool blank = true,
        bool hasGesture = false)
        => new(middleVk, down, injected, menuOpen, enabled, inScope, taskbar, fullscreen, blank, hasGesture);

    private static void TestMiddleMenu()
    {
        // ── ① 桌面空白：按下接管，抬起弹 ──────────────────────────────
        CheckEqual("中键菜单：桌面空白 + 没配手势 → 按下接管",
            MiddleMenuPolicy.Decide(MiddleCase(down: true)), MiddleMenuAction.SwallowTap);
        CheckEqual("中键菜单：桌面空白 + 没配手势 → 抬起不由决策表处理",
            MiddleMenuPolicy.Decide(MiddleCase(down: false)), MiddleMenuAction.Ignore);

        // ── ② 桌面图标上：不接管（不吞，原样放行）────────────────────
        CheckEqual("中键菜单：点在桌面图标上 → 不接管",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, blank: false)), MiddleMenuAction.Ignore);

        // ── ③ 浏览器等别的程序：不在范围内 → 不接管 ──────────────────
        CheckEqual("中键菜单：浏览器里 → 不接管（自动滚动照旧）",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, inScope: false)), MiddleMenuAction.Ignore);

        // ── ④ 中键画一笔：手势优先 ──────────────────────────────────
        CheckEqual("中键菜单：配了中键手势 → 按下交给手势",
            MiddleMenuPolicy.Decide(MiddleCase(down: true, hasGesture: true)), MiddleMenuAction.StartGesture);
        CheckEqual("中键菜单：手势没成笔 + 空白 → 弹菜单代替补发",
            MiddleMenuPolicy.Decide(MiddleCase(down: false, hasGesture: true)), MiddleMenuAction.MenuInsteadOfTap);
        CheckEqual("中键菜单：手势没成笔但不在空白 → 照旧补发中键",
            MiddleMenuPolicy.Decide(MiddleCase(down: false, hasGesture: true, blank: false)), MiddleMenuAction.Ignore);

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

        // ── LVM_HITTEST 的返回值语义（-1 才是空白）──────────────────
        CheckEqual("命中测试：iItem=-1 判空白", BlankHit.VerdictOfItemIndex(-1), BlankVerdict.Blank);
        CheckEqual("命中测试：iItem=0 判非空白", BlankHit.VerdictOfItemIndex(0), BlankVerdict.Occupied);
        CheckEqual("命中测试：iItem=7 判非空白", BlankHit.VerdictOfItemIndex(7), BlankVerdict.Occupied);

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
            Check("老配置：中键菜单默认关闭", !old.MiddleClickMenuEnabled);
            CheckEqual("老配置：菜单 id 默认空", old.MiddleClickMenuId, "");
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
