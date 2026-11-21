using System;
using System.Collections.Generic;
using System.Linq;
using InputPlus.Core.Actions;
using InputPlus.Core.Gestures;
using InputPlus.Core.Settings;
using InputPlus.UI;

namespace InputPlus;

/// <summary>
/// 自检：v1.5.0 补齐的八项 WGestures 2 能力里"纯逻辑就能验"的那几条。
///
/// 多步手势（步骤模型 + 逐步比对 + 挑条）、同形手势按作用域层级择优、
/// 沿边移动的贴边判定、手势导入导出往返，以及动作表里 Lua 脚本 / 手势菜单两类动作。
///
/// 这些地方出错时用户看到的是"手势不认了""导进来的手势没了"，不是窗口长得不对，
/// 所以它们必须在自检里有一席之地；窗口那部分交给渲染图。
/// </summary>
public static partial class SelfTest
{
    private static void TestWg2Plus()
    {
        Section("v1.5.0 多步手势 / 层级择优 / 沿边 / 全屏判定");
        TestGestureStepModel();
        TestStepDistance();
        TestPickSteps();
        TestScopeRank();
        TestEdgeGesture();
        TestFullscreenDetection();
        TestWg2PlusActions();
        TestWg2PlusOptions();
        TestLuaScripts();
        TestLuaTemplates();
    }

    /// <summary>把一串屏幕坐标按录制口径做成"一笔"的步骤（归一化 + 扁平化）。</summary>
    private static GestureStep StepOf(params (double X, double Y)[] screen) => new()
    {
        Kind = "stroke",
        Path = GestureMath.ToFlat(GestureMath.Normalize(Pts(screen))),
    };

    private static void TestGestureStepModel()
    {
        var stroke = StepOf((0, 0), (0, -40), (40, -80));
        Check("步骤：三个点的一笔算有形状", stroke.HasPath);
        Check("步骤：一个点不算一笔",
            !new GestureStep { Kind = "stroke", Path = new List<int> { 100, 100 } }.HasPath);
        Check("步骤：两个点就算一笔（直线也算）",
            new GestureStep { Kind = "stroke", Path = new List<int> { 100, 100, 200, 200 } }.HasPath);
        CheckEqual("步骤：一步最多八步", GestureStep.MaxSteps, 8);
        CheckEqual("步骤：点阵按对数还原",
            stroke.Points().Count, stroke.Path.Count / 2);

        var copy = stroke.Clone();
        Check("步骤：克隆是深拷贝（改副本不动原件）",
            !ReferenceEquals(copy.Path, stroke.Path) && copy.Path.Count == stroke.Path.Count);
        if (copy.Path.Count > 0) copy.Path[0] += 12345;
        Check("步骤：改过副本之后原件的点没变",
            stroke.Path.Count > 0 && stroke.Path[0] != copy.Path[0]);

        CheckEqual("步骤：滚轮向上", new GestureStep { Kind = "scroll", Wheel = -1 }.Title(), Loc.T("滚轮向上"));
        CheckEqual("步骤：滚轮向左", new GestureStep { Kind = "scroll", Wheel = -2 }.Title(), Loc.T("滚轮向左"));
        CheckEqual("步骤：滚轮没方向时的标题", new GestureStep { Kind = "scroll", Wheel = 0 }.Title(), Loc.T("滚轮"));
        CheckEqual("步骤：角落", new GestureStep { Kind = "corner", Corner = 2 }.Title(), Loc.T("甩到右上角"));
        CheckEqual("步骤：按键", new GestureStep { Kind = "key", Key = "ctrl+a" }.Title(), Loc.F("按 {0}", "ctrl+a"));
    }

    private static void TestStepDistance()
    {
        var opts = GestureOptions.Default;
        var first = StepOf((0, 0), (0, -40), (40, -80));

        var template = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = "两步",
            Kind = "steps",
            Steps =
            {
                first.Clone(),
                new GestureStep { Kind = "scroll", Wheel = 1 },
            },
        };
        var drawn = new List<GestureStep>
        {
            first.Clone(),
            new GestureStep { Kind = "scroll", Wheel = 1 },
        };

        double d = GestureService.StepDistance(template, drawn, opts);
        Check("步距：同一条手势算得出来且很小", d < 0.5, d.ToString("F3"));

        var bigger = new List<GestureStep>
        {
            StepOf((0, 0), (0, -80), (80, -160)),
            new GestureStep { Kind = "scroll", Wheel = 1 },
        };
        double dBig = GestureService.StepDistance(template, bigger, opts);
        Check("步距：画大一圈还算同一条（归一化生效）", dBig < 0.5, dBig.ToString("F3"));

        Check("步距：步数不一样就是无穷远",
            GestureService.StepDistance(template, new List<GestureStep> { first.Clone() }, opts) == double.MaxValue);
        Check("步距：第一步类型不对就是无穷远",
            GestureService.StepDistance(template,
                new List<GestureStep> { new() { Kind = "key", Key = "a" }, new() { Kind = "scroll", Wheel = 1 } },
                opts) == double.MaxValue);
        Check("步距：滚轮方向反了就是无穷远",
            GestureService.StepDistance(template, new List<GestureStep>
            {
                first.Clone(),
                new GestureStep { Kind = "scroll", Wheel = -1 },
            }, opts) == double.MaxValue);

        var keyTemplate = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = "按键步",
            Kind = "steps",
            Steps =
            {
                first.Clone(),
                new GestureStep { Kind = "key", Key = "ctrl+a" },
            },
        };
        Check("步距：同一个组合键写法不同也认（ctrl+A = ctrl+a）",
            GestureService.StepDistance(keyTemplate, new List<GestureStep>
            {
                first.Clone(),
                new GestureStep { Kind = "key", Key = "Ctrl+A" },
            }, opts) < 1);
        Check("步距：键对不上就是无穷远",
            GestureService.StepDistance(keyTemplate, new List<GestureStep>
            {
                first.Clone(),
                new GestureStep { Kind = "key", Key = "ctrl+b" },
            }, opts) == double.MaxValue);

        var cornerTemplate = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = "角落步",
            Kind = "steps",
            Steps =
            {
                new GestureStep { Kind = "corner", Corner = 1 },
                new GestureStep { Kind = "scroll", Wheel = 1 },
            },
        };
        Check("步距：角落对得上",
            GestureService.StepDistance(cornerTemplate, new List<GestureStep>
            {
                new GestureStep { Kind = "corner", Corner = 1 },
                new GestureStep { Kind = "scroll", Wheel = 1 },
            }, opts) == 0);
        Check("步距：角落对不上就是无穷远",
            GestureService.StepDistance(cornerTemplate, new List<GestureStep>
            {
                new GestureStep { Kind = "corner", Corner = 3 },
                new GestureStep { Kind = "scroll", Wheel = 1 },
            }, opts) == double.MaxValue);
    }

    private static void TestPickSteps()
    {
        var opts = GestureOptions.Default;
        var settings = DefaultSettings.Create();
        settings.Gestures.Clear();

        var first = StepOf((0, 0), (0, -40), (40, -80));
        var down = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = "画一笔再下滚",
            Kind = "steps",
            Button = GestureButtons.Right,
            Action = "doNothing",
            Steps = { first.Clone(), new GestureStep { Kind = "scroll", Wheel = 1 } },
        };
        var up = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = "画一笔再上滚",
            Kind = "steps",
            Button = GestureButtons.Right,
            Action = "doNothing",
            Steps = { first.Clone(), new GestureStep { Kind = "scroll", Wheel = -1 } },
        };
        settings.Gestures.Add(down);
        settings.Gestures.Add(up);

        int vk = GestureButtons.ToVk(GestureButtons.Right);
        var (hit, score) = GestureService.PickSteps(settings, vk, "", new List<GestureStep>
        {
            first.Clone(),
            new GestureStep { Kind = "scroll", Wheel = -1 },
        }, opts);
        Check("多步手势：按步骤挑中对应的一条", ReferenceEquals(hit, up), hit?.Name ?? "null");
        Check("多步手势：命中的分数小于 1", score < 1, score.ToString("F3"));

        var (none, _) = GestureService.PickSteps(settings, vk, "", new List<GestureStep> { first.Clone() }, opts);
        Check("多步手势：步数对不上就不认", none == null, none?.Name ?? "null");

        var (otherButton, _) = GestureService.PickSteps(settings, GestureButtons.ToVk(GestureButtons.Middle),
            "", new List<GestureStep> { first.Clone(), new GestureStep { Kind = "scroll", Wheel = -1 } }, opts);
        Check("多步手势：起始键不对就不认", otherButton == null, otherButton?.Name ?? "null");
    }

    private static void TestScopeRank()
    {
        CheckEqual("层级：全局手势排 0", GestureService.ScopeRank(new MouseGesture()), 0);
        CheckEqual("层级：分组手势排 1",
            GestureService.ScopeRank(new MouseGesture { AppPath = "group:work" }), 1);
        CheckEqual("层级：具体程序排 2",
            GestureService.ScopeRank(new MouseGesture { AppPath = @"C:\Tools\code.exe" }), 2);

        var opts = GestureOptions.Default;
        var global = RecentredGesture((0, 0), (0, -40), (40, -80));
        global.Name = "全局那条";
        var scoped = RecentredGesture((0, 0), (0, -40), (40, -80));
        scoped.Name = "程序专属那条";
        scoped.AppPath = @"C:\Tools\code.exe";

        var points = Pts((0, 0), (0, -40), (40, -80));
        var (hit, _) = GestureService.MatchWithScope(new List<MouseGesture> { global, scoped }, points, opts);
        Check("层级择优：同样像的时候程序专属那条赢", ReferenceEquals(hit, scoped), hit?.Name ?? "null");

        var (only, _) = GestureService.MatchWithScope(new List<MouseGesture> { global }, points, opts);
        Check("层级择优：只有全局手势时还是它", ReferenceEquals(only, global), only?.Name ?? "null");

        scoped.Path.Clear();   // 没有形状的专属手势不参与比较
        var (back, _) = GestureService.MatchWithScope(new List<MouseGesture> { global, scoped }, points, opts);
        Check("层级择优：专属那条没有形状时退回全局", ReferenceEquals(back, global), back?.Name ?? "null");
    }

    private static void TestEdgeGesture()
    {
        int left = Core.Interop.NativeMethods.GetSystemMetrics(76);   // SM_XVIRTUALSCREEN
        int top = Core.Interop.NativeMethods.GetSystemMetrics(77);    // SM_YVIRTUALSCREEN
        int width = Core.Interop.NativeMethods.GetSystemMetrics(78);  // SM_CXVIRTUALSCREEN
        int height = Core.Interop.NativeMethods.GetSystemMetrics(79); // SM_CYVIRTUALSCREEN

        var opts = GestureOptions.Default;
        int size = Math.Max(opts.EdgeSize, 12);

        CheckEqual("沿边：贴着上边框算上边", GestureService.EdgeAt(left + width / 2.0, top + 1, size), 1);
        CheckEqual("沿边：贴着左边框算左边", GestureService.EdgeAt(left + 1, top + height / 2.0, size), 4);
        CheckEqual("沿边：屏幕正中不贴边", GestureService.EdgeAt(left + width / 2.0, top + height / 2.0, size), 0);

        var alongTop = Pts(
            (left + 300, top + 1),
            (left + 600, top + 2),
            (left + 900, top + 1));
        Check("沿边：整条轨迹都在上边框里算贴边", GestureService.HugsEdge(alongTop, 1, size));

        var away = Pts(
            (left + 300, top + 200),
            (left + 600, top + 260),
            (left + 900, top + 210));
        Check("沿边：轨迹跑到屏幕中间就不算贴边", !GestureService.HugsEdge(away, 1, size));
        Check("沿边：边号 0 不算贴边", !GestureService.HugsEdge(alongTop, 0, size));

        CheckEqual("沿边：角落判定（左上）", GestureService.CornerAt(left + 1, top + 1, size), 1);
        CheckEqual("沿边：角落判定（屏幕中央为 0）",
            GestureService.CornerAt(left + width / 2.0, top + height / 2.0, size), 0);

        Check("沿边：离指针最近的角是左上", GestureService.NearestCorner(left + 1, top + 1) == 1);
    }

    /// <summary>
    /// 全屏判定的口径：用户要的是"只有真全屏 / 无边框全屏才不接管手势，
    /// 最大化的窗口照常能画"。这里把五种典型窗口摆出来验。
    /// </summary>
    private static void TestFullscreenDetection()
    {
        var monitor = new Core.Interop.NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };

        // 无边框全屏（WS_POPUP，没有标题栏也不能拖边框）：算
        Check("全屏：无边框盖满整块屏幕 → 算真全屏",
            GestureService.IsTrueFullscreen(
                new GestureService.FullscreenFacts("UnityWndClass", false, false, 0),
                monitor, monitor));

        // 独占全屏的游戏常常比屏幕小一两个像素：仍算
        Check("全屏：比屏幕小 2px 的无边框窗口仍算",
            GestureService.IsTrueFullscreen(
                new GestureService.FullscreenFacts("UnityWndClass", false, false, 0),
                new Core.Interop.NativeMethods.RECT { Left = 1, Top = 1, Right = 1919, Bottom = 1079 },
                monitor));

        // 最大化的窗口：不算（这就是用户最在意的那个场景）
        Check("全屏：最大化的窗口不算（手势照常能用）",
            !GestureService.IsTrueFullscreen(
                new GestureService.FullscreenFacts("Chrome_WidgetWin_1", true, false,
                    Core.Interop.NativeMethods.WS_CAPTION | Core.Interop.NativeMethods.WS_THICKFRAME),
                new Core.Interop.NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 },
                monitor));

        // 带标题栏的普通窗口被手动拉到整屏：不算
        Check("全屏：手动拉满的带边框窗口不算",
            !GestureService.IsTrueFullscreen(
                new GestureService.FullscreenFacts("Notepad", false, false,
                    Core.Interop.NativeMethods.WS_CAPTION | Core.Interop.NativeMethods.WS_THICKFRAME),
                monitor, monitor));

        // 只盖住工作区（任务栏那条还在）：不算
        Check("全屏：只铺满工作区（下面留任务栏）不算",
            !GestureService.IsTrueFullscreen(
                new GestureService.FullscreenFacts("UnityWndClass", false, false, 0),
                new Core.Interop.NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 },
                monitor));

        // 桌面和任务栏自己铺满，但不是"程序"
        Check("全屏：桌面 Progman 不算",
            !GestureService.IsTrueFullscreen(
                new GestureService.FullscreenFacts("Progman", false, false, 0), monitor, monitor));
        Check("全屏：最小化的窗口不算",
            !GestureService.IsTrueFullscreen(
                new GestureService.FullscreenFacts("UnityWndClass", false, true, 0), monitor, monitor));
    }

    private static void TestWg2PlusActions()
    {
        var settings = DefaultSettings.Create();
        settings.Scripts.Add(new Core.Scripting.LuaScript
        {
            Id = "ab12cd34",
            Name = "关掉所有记事本",
            Code = "ip.log('hi')",
        });
        settings.GestureMenus.Add(new Core.Scripting.GestureMenuDef
        {
            Id = "menu0001",
            Name = "窗口菜单",
            Items =
            {
                new Core.Scripting.GestureMenuItem { Title = "最小化", Action = "winMinimize" },
            },
        });

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
        var lua = cat.Items.FirstOrDefault(i => i.Id == "lua:ab12cd34");
        Check("动作表：每个 Lua 脚本都是一条动作", lua != null,
            string.Join(",", cat.Items.Where(i => i.Id.StartsWith("lua:")).Select(i => i.Id)));
        CheckEqual("动作表：Lua 动作在「Lua 脚本」分类下", lua?.Category, "Lua 脚本");
        Check("动作表：Lua 动作的标题带脚本名", lua?.Title?.Contains("关掉所有记事本") == true, lua?.Title ?? "");
        Check("动作表：Lua 动作能带参数（传给脚本的 ip.arg）", lua?.NeedsArg == true);
        Check("动作表：有「打开脚本管理」这个入口",
            cat.Items.Any(i => i.Id == "luaManager" && i.Category == "Lua 脚本"));

        var menu = cat.Items.FirstOrDefault(i => i.Id == "menu:menu0001");
        Check("动作表：每个手势菜单都是一条动作", menu != null,
            string.Join(",", cat.Items.Where(i => i.Id.StartsWith("menu:")).Select(i => i.Id)));
        CheckEqual("动作表：菜单动作在「手势菜单」分类下", menu?.Category, "手势菜单");
        Check("动作表：菜单动作的标题带菜单名", menu?.Title?.Contains("窗口菜单") == true, menu?.Title ?? "");
        Check("动作表：有「打开菜单管理」这个入口",
            cat.Items.Any(i => i.Id == "gestureMenuManager" && i.Category == "手势菜单"));

        // 删掉脚本/菜单之后动作表里就该没有它们了（重建一次）
        settings.Scripts.Clear();
        settings.GestureMenus.Clear();
        var bare = Core.Actions.ActionCatalog.Create(ctx);
        Check("动作表：删掉脚本之后动作消失", !bare.Items.Any(i => i.Id.StartsWith("lua:")));
        Check("动作表：删掉菜单之后动作消失", !bare.Items.Any(i => i.Id.StartsWith("menu:")));
        Check("动作表：管理入口一直在",
            bare.Items.Any(i => i.Id == "luaManager") && bare.Items.Any(i => i.Id == "gestureMenuManager"));
    }

    private static void TestLuaScripts()
    {
        Section("v1.5.0 Lua 脚本动作");

        // 基本：print / ip.log 都能把话带回来，ip.arg 能读动作参数
        var basic = Core.Scripting.LuaRunner.Run("print('hello') ip.log('arg=' .. ip.arg)", "自检", null, null, "42");
        Check("Lua：脚本能跑起来", basic.Ok, basic.Error);
        Check("Lua：print 的输出带回来", basic.Output.Contains("hello"), basic.Output);
        Check("Lua：ip.log 的输出带回来", basic.Output.Contains("arg=42"), basic.Output);

        // ip.action 走宿主回调，id 与参数原样传过去
        string? calledId = null;
        string? calledArg = null;
        var act = Core.Scripting.LuaRunner.Run("ip.action('winMinimize', 'x')", "自检", null,
            (id, a) =>
            {
                calledId = id;
                calledArg = a;
                return true;
            });
        Check("Lua：ip.action 能调到动作目录", act.Ok, act.Error);
        CheckEqual("Lua：ip.action 把动作 id 传过去", calledId, "winMinimize");
        CheckEqual("Lua：ip.action 把参数传过去", calledArg, "x");

        // 沙盒：碰不到 os / io —— 写脚本的人不该能读文件、起进程
        var noOs = Core.Scripting.LuaRunner.Run("os.execute('echo hi')", "自检", null, null);
        Check("Lua：沙盒里碰不到 os.execute", !noOs.Ok, noOs.Error);
        Check("Lua：拦住之后有一句人话", noOs.Error.Length > 0, noOs.Error);
        var noIo = Core.Scripting.LuaRunner.Run("return io.open('x')", "自检", null, null);
        Check("Lua：沙盒里碰不到 io.open", !noIo.Ok, noIo.Error);

        // 语法错、运行时错都只算失败，不往外抛
        var syntax = Core.Scripting.LuaRunner.Run("function(", "自检", null, null);
        Check("Lua：语法错算失败而不抛异常", !syntax.Ok, syntax.Error);
        var runtime = Core.Scripting.LuaRunner.Run("local t = nil; return t.x", "自检", null, null);
        Check("Lua：运行时错算失败而不抛异常", !runtime.Ok, runtime.Error);

        // 空脚本、空对象
        var empty = Core.Scripting.LuaRunner.Run("", "自检", null, null);
        Check("Lua：空脚本不报错", empty.Ok, empty.Error);
        var nullScript = Core.Scripting.LuaRunner.Run((Core.Scripting.LuaScript)null!, null, null);
        Check("Lua：空脚本对象给失败提示", !nullScript.Ok, nullScript.Error);

        // 速查表与示例都得在（示例只查非空：它会改剪贴板，自检里不能真跑）
        Check("Lua：速查表里有 ip.action", Core.Scripting.LuaRunner.ApiDoc.Contains("ip.action"));
        Check("Lua：速查表里有 ip.win", Core.Scripting.LuaRunner.ApiDoc.Contains("ip.win"));
        Check("Lua：内置示例不是空的", Core.Scripting.LuaRunner.SampleCode.Length > 20);

        // 动作目录：停用的脚本照样登记（执行时才提示"已停用"）
        var settings = DefaultSettings.Create();
        settings.Scripts.Add(new Core.Scripting.LuaScript
        {
            Id = "ff00ff00",
            Name = "停用的脚本",
            Code = "ip.log('不该跑到这里')",
            Enabled = false,
        });
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        Check("Lua：停用的脚本也登记成动作", 
            Core.Actions.ActionCatalog.Create(ctx).Items.Any(i => i.Id == "lua:ff00ff00"));

        // 模型：没名字时有兜底显示名，克隆保住 Id 且是另一个对象
        var unnamed = new Core.Scripting.LuaScript { Id = "01020304", Name = "  ", Code = "ip.log('x')" };
        Check("Lua：脚本没名字时有个兜底显示名", unnamed.DisplayName.Length > 0, unnamed.DisplayName);
        var copy = unnamed.Clone();
        Check("Lua：脚本克隆保住 Id", copy.Id == unnamed.Id, copy.Id);
        Check("Lua：脚本克隆是另一个对象且代码一起过去",
            !ReferenceEquals(copy, unnamed) && copy.Code == unnamed.Code);
    }

    /// <summary>
    /// 内置脚本模板：不用先写脚本就能直接选的那种动作。
    /// 它们活在代码里（Core/Scripting/LuaTemplates.cs），不进用户配置 ——
    /// 所以"删掉用户脚本"之后它们仍然在动作表里。
    /// </summary>
    private static void TestLuaTemplates()
    {
        Section("v1.5.2 Lua 内置模板");

        var all = Core.Scripting.LuaTemplates.All;
        Check("模板：出厂至少 100 条", all.Count >= 100, $"实际 {all.Count} 条");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Core.Scripting.LuaTemplates.Categories) categories.Add(name);
        Check("模板：分类不少于 8 组", categories.Count >= 8, $"实际 {categories.Count} 组");

        foreach (var tpl in all)
        {
            Check($"模板「{tpl.Id}」的 id 不重复", ids.Add(tpl.Id), tpl.Id);
            Check($"模板「{tpl.Id}」有名字", tpl.Name.Length > 0);
            Check($"模板「{tpl.Id}」有说明", tpl.Description.Length > 0);
            // 一行脚本也是脚本：ip.win.close() 只有 14 个字符，别用长度把人卡住。
            Check($"模板「{tpl.Id}」有代码", tpl.Code.Trim().Length >= 8, tpl.Code.Trim().Length.ToString());
            Check($"模板「{tpl.Id}」的分类在分类表里", categories.Contains(tpl.Category), tpl.Category);
            Check($"模板「{tpl.Id}」不碰 os / io",
                !tpl.Code.Contains("os.") && !tpl.Code.Contains("io."), tpl.Code);
            Check($"模板「{tpl.Id}」要参数就得有参数提示",
                !tpl.NeedsArg || tpl.ArgHint.Length > 0, tpl.ArgHint);

            /* 编译一遍（不执行）：模板是给用户"照抄就能用"的，
             * 一条语法错就等于动作目录里摆了个点了就报错的动作。 */
            string? syntax = null;
            try
            {
                var script = new MoonSharp.Interpreter.Script();
                script.LoadString(tpl.Code);
            }
            catch (Exception ex) { syntax = ex.Message; }
            Check($"模板「{tpl.Id}」的 Lua 代码能编译", syntax == null, syntax ?? "");
        }

        CheckEqual("模板：按 id 能找回来", Core.Scripting.LuaTemplates.Find("upper")?.Name, "剪贴板转大写");
        Check("模板：找不到的 id 返回 null", Core.Scripting.LuaTemplates.Find("nope") == null);

        /* v1.6.1 去重：49 条「原生动作就能做到」的模板从菜单与动作目录里藏起来，
         * 但 id 与代码都留在原地 —— 老配置里的绑定不能断。 */
        var hidden = Core.Scripting.LuaTemplates.Hidden;
        CheckEqual("模板去重：藏起来的正好 49 条", hidden.Count, 49);
        Check("模板去重：总数还是 118（只藏不删）", all.Count == 118 && Core.Scripting.LuaTemplates.Visible.Count() == 118 - hidden.Count,
            $"全部 {all.Count} / 可见 {Core.Scripting.LuaTemplates.Visible.Count()}");
        Check("模板去重：置顶、关窗口这类 1:1 重复的藏了", Core.Scripting.LuaTemplates.IsHidden("topmost") && Core.Scripting.LuaTemplates.IsHidden("win-close"));
        Check("模板去重：启动器预设也藏了", Core.Scripting.LuaTemplates.IsHidden("open-calc") && Core.Scripting.LuaTemplates.IsHidden("dir-documents"));
        Check("模板去重：真正的补充能力不藏", !Core.Scripting.LuaTemplates.IsHidden("upper") && !Core.Scripting.LuaTemplates.IsHidden("time-now"));
        Check("模板去重：藏起来的模板仍能按 id 找到（老绑定不断）", Core.Scripting.LuaTemplates.Find("topmost") != null);
        Check("模板去重：每条藏起来的 id 都真有对应模板",
            hidden.All(id => Core.Scripting.LuaTemplates.All.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))));

        // 动作表：模板全部登记在「Lua 脚本」分类下，且用户脚本全删了也还在
        var settings = DefaultSettings.Create();
        settings.Scripts.Clear();
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
        /* 只核 Visible：与原生动作重复的那 49 条（v1.6.1）已经从目录里藏起来，
         * 它们仍然编译、仍然能通过 Find 执行，只是不再占条目。 */
        foreach (var tpl in Core.Scripting.LuaTemplates.Visible)
        {
            var item = cat.Items.FirstOrDefault(i => i.Id == "luaTpl:" + tpl.Id);
            Check($"动作表：模板「{tpl.Id}」能直接选", item != null);
            CheckEqual($"动作表：模板「{tpl.Id}」在「Lua 脚本」分类", item?.Category, "Lua 脚本");
            CheckEqual($"动作表：模板「{tpl.Id}」标题就是模板名", item?.Title, tpl.Name);
        }
        Check("动作表：一条用户脚本都没有时模板仍在",
            cat.Items.Any(i => i.Id.StartsWith("luaTpl:", StringComparison.Ordinal)));
        int firstTemplate = -1, managerAt = -1;
        for (int i = 0; i < cat.Items.Count; i++)
        {
            if (firstTemplate < 0 && cat.Items[i].Id.StartsWith("luaTpl:", StringComparison.Ordinal)) firstTemplate = i;
            if (managerAt < 0 && cat.Items[i].Id == "luaManager") managerAt = i;
        }
        Check("动作表：模板排在「打开脚本管理」前面",
            firstTemplate >= 0 && managerAt >= 0 && firstTemplate < managerAt,
            $"模板 {firstTemplate} / 管理 {managerAt}");
    }

    private static void TestWg2PlusOptions()
    {
        var opts = GestureOptions.Default;
        Check("偏好：设置窗口默认不置顶", !opts.PrefWindowTopmost);

        // 步骤跟着手势克隆走，别在两条手势之间共享同一个 List
        var a = new MouseGesture { Id = MouseGesture.NewId(), Steps = { StepOf((0, 0), (0, -10), (10, -20)) } };
        var b = a.Clone();
        b.Steps[0].Path[0] += 100;
        Check("克隆：手势的步骤是深拷贝", a.Steps[0].Path[0] != b.Steps[0].Path[0] || a.Steps[0].Path.Count == 0);

        var target = new MouseGesture();
        target.CopyFrom(a);
        Check("克隆：CopyFrom 也把步骤带过去", target.Steps.Count == a.Steps.Count, target.Steps.Count.ToString());
    }
}
