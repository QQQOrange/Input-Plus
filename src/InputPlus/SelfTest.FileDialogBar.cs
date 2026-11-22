using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InputPlus.Core.FileDialog;

namespace InputPlus;

public static partial class SelfTest
{
    /* 文件对话框浮层（FileDialogBar）的自检。
     *
     * 判型是这套东西的地基：一旦把"保存对话框"认成"文件夹对话框"，
     * 回填就会往文件名框里塞目录，用户看到的是"文件没保存成功"。
     * 所以判型规则按 a~e 五条逐条断言，顺序错一条就会串型。
     *
     * 真实窗口部分：用 CreateWindowExW 造一个 #32770 + 若干子控件，
     * 不显示、不进 Z 序，探针读到的内容和真对话框同源（同一套 EnumChildWindows）。 */

    private static void TestFileDialogBar()
    {
        TestDialogClassifyRules();
        TestDialogPolicySwitches();
        TestDialogProbeRealWindow();
        TestDialogWatcherDebounce();
        TestDialogSearchQueryPlan();
        TestDialogRecorder();
        TestDialogSearchProvider();
        TestDialogHotkeyValidation();
        TestQuickSwitchHistory();
        TestQuickSwitchCatalog();
        TestDialogBarMenuActions();
        TestDialogBarFollow();
        TestDialogBarAnchorGeometry();
    }

    /// <summary>
    /// 跟随链路（2.7 a–d）：真 #32770 + 真浮层窗口，连挪 60 次（每次 +3px）。
    ///
    /// 四条断言对应规格里的四条：
    ///   a) 相对位置误差 ≤1px；
    ///   b) "事件 → SetWindowPos 返回" p99 ≤2ms；
    ///   c) 拖动过程中浮层窗口 Width/Height 零变化；
    ///   d) _inApply 期间没有再排几何（自激计数 0），且拖动中一次几何重算都没有。
    /// </summary>
    private static void TestDialogBarFollow()
    {
        nint hwnd = 0;
        UI.Windows.FileDialogBarWindow? bar = null;
        try
        {
            hwnd = CreateWindowExW(0, "#32770", "inputplus-fdb-follow", 0x80000000u /*WS_POPUP*/,
                240, 220, 620, 420, 0, 0, 0, 0);
            if (hwnd == 0)
            {
                Check("跟随：自检对话框建不出来（跳过）", true, "CreateWindowEx 返回 0");
                return;
            }

            bar = new UI.Windows.FileDialogBarWindow();
            DialogRect bounds = DialogNative.FrameBounds(hwnd);
            /* 预热：离屏显示一次再藏起来。第一次预热会建句柄（0 → 有），
             * 之后重复预热不得重建窗口，且结束时必须是隐藏的。
             * 放在 AttachForTest 之前：预热会改窗口尺寸（600x60 离屏），
             * 之后 AttachForTest 会按几何重新摆好，免得被"拖动中尺寸不变"误判。 */
            bar.WarmUp();
            nint warm1 = new System.Windows.Interop.WindowInteropHelper(bar).Handle;
            bar.WarmUp();
            nint warm2 = new System.Windows.Interop.WindowInteropHelper(bar).Handle;
            Check("浮条：预热建出窗口且不重建（句柄稳定）", warm1 != 0 && warm1 == warm2,
                $"0x{warm1:X} → 0x{warm2:X}");
            Check("浮条：预热后保持隐藏", !bar.IsVisible);

            bar.AttachForTest(hwnd, bounds);
            bar.ResetFollowMetricsForTest();

            /* 窗口形态与空态高度：分层窗口（圆角 + 外侧阴影，接触边直角）；
             * 空态 = 输入行 44 + 上下各 1px 边框 + 外侧 8px 阴影边距 = 54 DIP
             * （旧版还多留 20px 的"安全余量"，那才是"什么都没搜也胖一圈"的来由）。 */
            Check("浮条：分层窗口（圆角 + 外侧阴影）", bar.AllowsTransparency);
            Check("浮条：空态高度 = 输入行 + 边框 + 阴影边距", Math.Abs(bar.Height - 54) <= 1, $"{bar.Height:0.#} DIP");

            nint bh = new System.Windows.Interop.WindowInteropHelper(bar).Handle;
            DialogNative.GetWindowRect(bh, out DialogNative.RECT before);
            int width0 = before.Right - before.Left, height0 = before.Bottom - before.Top;

            double worst = 0;
            const int moves = 60;
            for (int i = 0; i < moves; i++)
            {
                DialogNative.SetWindowPos(hwnd, 0, 240 + i * 3, 220, 0, 0,
                    DialogNative.SWP_NOSIZE | DialogNative.SWP_NOZORDER | DialogNative.SWP_NOACTIVATE);
                DialogRect b = DialogNative.FrameBounds(hwnd);
                bar.UpdateBounds(b);

                DialogNative.GetWindowRect(bh, out DialogNative.RECT br);
                int expectedTop = b.Bottom - 2;              // Below：接触边重叠 2px
                double err = Math.Abs(br.Top - expectedTop);
                if (err > worst) worst = err;
            }

            DialogNative.GetWindowRect(bh, out DialogNative.RECT after);
            int width1 = after.Right - after.Left, height1 = after.Bottom - after.Top;

            Check($"跟随：{moves} 次移动相对误差 ≤1px", worst <= 1, $"最大 {worst:0.##}px");
            Check("跟随：p99 ≤2ms", bar.LatencyP99Us <= 2000, $"p99 {bar.LatencyP99Us:0}µs / 应用 {bar.AppliedCount} 次");
            Check("跟随：拖动中窗口尺寸零变化",
                width0 == width1 && height0 == height1,
                $"{width0}x{height0} → {width1}x{height1}");
            Check("跟随：拖动中没有再算几何", bar.GeometryCount == 0, $"几何重算 {bar.GeometryCount} 次");
            Check("跟随：_inApply 期间没有排跟随", bar.FollowScheduledInApply == 0, $"{bar.FollowScheduledInApply} 次");

            /* 「搜一次撑一次」的回归：结果列表上来之后，浮层宽度一个像素都不该变。
             * 旧实现拿 OuterGrid.DesiredSize 量"内容最小宽度"，行里的长路径会把
             * DesiredSize 一路顶大 —— 每搜一次浮层就左右各撑开一截。 */
            double widthBeforeRows = bar.Width;
            string longPath = @"D:\software\图吧工具箱202601\data\" + new string('长', 60) + @"\文件夹说明.txt";
            bar.SetRows(new[]
            {
                new InputPlus.Core.FileDialog.DialogSelection(longPath, false, "文件夹说明.txt", "Ctrl+1"),
                new InputPlus.Core.FileDialog.DialogSelection(@"C:\Users\64381\OneDrive\桌面\简历文件", true, "简历文件", "Ctrl+5"),
            });
            bar.UpdateLayout();
            Check("跟随：结果再长也不改浮层宽度",
                Math.Abs(bar.Width - widthBeforeRows) <= 0.5,
                $"{widthBeforeRows:0.#} → {bar.Width:0.#} DIP");

            /* 把数字写进日志（PASS 行不带明细），交付报告直接引用这几行。 */
            double p50 = bar.LatencyP50Us, p99 = bar.LatencyP99Us;
            InputPlus.Core.CoreLog.Info($"[跟随度量] 新路径：{moves} 次移动 p50={p50:0}µs p99={p99:0}µs "
                         + $"误差max={worst:0.##}px 尺寸 {width0}x{height0}→{width1}x{height1} "
                         + $"几何重算={bar.GeometryCount} 自激={bar.FollowScheduledInApply}");

            /* 旧路径的"一帧成本"：每个 16ms tick 都跑的那套重排（Measure×2 + UpdateLayout×2 +
             * 改 Width/MaxHeight）。这里单独量它的耗时，用来解释 16.7ms 的量化延迟之外还叠了多少。 */
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++)
            {
                bar.MeasureHeavyForTest();
            }
            sw.Stop();
            InputPlus.Core.CoreLog.Info($"[跟随度量] 旧路径单 tick 的重排成本（20 次均值）：{sw.Elapsed.TotalMilliseconds / 20:0.000}ms；"
                         + "旧路径量化延迟 ≈16.7ms（p50）/33ms（p99）");
        }
        catch (Exception ex)
        {
            Check("跟随：60 次移动不抛异常", false, ex.Message);
        }
        finally
        {
            try { bar?.Close(); } catch { }
            if (hwnd != 0) DestroyWindow(hwnd);
        }
    }

    /// <summary>
    /// 浮条菜单的「点了有没有反应」：行 → ActionRequested 这条链必须真的通。
    ///
    /// 用户报「浮条设置里那个打开…对话框设置，点了没反应」时，第一步要分清是
    /// 点击没到行、还是动作没接到宿主。这条用例把窗口建出来（不显示）、
    /// 把菜单填进去、按序号激活第二行，断言发出来的确实是 OpenSettings。
    /// </summary>
    private static void TestDialogBarMenuActions()
    {
        try
        {
            var bar = new UI.Windows.FileDialogBarWindow();
            var fired = new List<QuickSwitchAction>();
            bar.ActionRequested += a => fired.Add(a);

            bar.ShowMenuOverlay();
            Check("浮条菜单：四行都在", bar.OverlayRowCount == 4, bar.OverlayRowCount.ToString());
            CheckEqual("浮条菜单：第一行是切换文件夹", bar.OverlayRowTitleForTest(0),
                QuickSwitchCatalog.Menu()[0].Title);
            CheckEqual("浮条菜单：第二行是设置", bar.OverlayRowTitleForTest(1), QuickSwitchCatalog.Menu()[1].Title);

            bool activated = bar.ActivateOverlayRowForTest(1);
            Check("浮条菜单：第二行能被激活", activated);
            Check("浮条菜单：设置行发出 OpenSettings",
                fired.Count == 1 && fired[0] == QuickSwitchAction.OpenSettings,
                fired.Count == 0 ? "什么都没发出来" : string.Join(",", fired));

            /* 隐藏子菜单也要能走通（「隐藏此搜索栏」的第二级）。 */
            fired.Clear();
            bar.ShowHideOverlay();
            Check("浮条隐藏菜单：三行都在", bar.OverlayRowCount == 3, bar.OverlayRowCount.ToString());
            Check("浮条隐藏菜单：第一行能激活并发出 HideSession",
                bar.ActivateOverlayRowForTest(0) && fired.Count == 1 && fired[0] == QuickSwitchAction.HideSession,
                fired.Count == 0 ? "什么都没发出来" : string.Join(",", fired));

            bar.CloseOverlay(focusInput: false);
        }
        catch (Exception ex)
        {
            Check("浮条菜单：行 → 动作链路", false, ex.Message);
        }
    }

    private static void TestDialogBarAnchorGeometry()
    {
        var work = new DialogRect(0, 0, 1920, 1080);
        var frame = new DialogRect(100, 100, 900, 700);
        DialogRect below = DialogBarAnchor.GetTargetRect(frame, work, 480, 44, out BarPlacement p1);
        CheckEqual("锚点：下方够时贴下沿", p1, BarPlacement.Below);
        CheckEqual("锚点：零间隙", below.Top, frame.Bottom);
        CheckEqual("锚点：居中", below.Left, 100 + (800 - 480) / 2);

        var low = new DialogRect(100, 800, 900, 1070);
        DialogRect above = DialogBarAnchor.GetTargetRect(low, work, 480, 44, out BarPlacement p2);
        CheckEqual("锚点：下方不够时翻上方", p2, BarPlacement.Above);
        CheckEqual("锚点：上方零间隙", above.Bottom, low.Top);

        var left = new DialogRect(-100, 100, 300, 700);
        DialogRect clamped = DialogBarAnchor.GetTargetRect(left, work, 800, 44, out _);
        CheckEqual("锚点：负坐标夹到工作区", clamped.Left, 0);

        var wideFrame = new DialogRect(0, 100, 4000, 700);
        DialogRect wide = DialogBarAnchor.GetTargetRect(wideFrame, work, 1200, 44, out _);
        Check("锚点：超宽不越界", wide.Right <= work.Right && wide.Left >= work.Left);

        var dpiFrame = new DialogRect(300, 200, 1300, 1200);
        var dpiWork = new DialogRect(0, 0, 2560, 1440);
        DialogRect dpi = DialogBarAnchor.GetTargetRect(dpiFrame, dpiWork, 600, 88, out BarPlacement p3);
        CheckEqual("锚点：高 DPI 尺寸/翻转仍按物理像素", p3, BarPlacement.Below);
        CheckEqual("锚点：高 DPI 零间隙", dpi.Top, dpiFrame.Bottom);
    }

    private static void TestQuickSwitchCatalog()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ip-qs-catalog-" + Environment.ProcessId);
        try
        {
            string folder = Directory.CreateDirectory(Path.Combine(dir, "收藏 目录")).FullName;
            var options = new InputPlus.Core.Settings.FileDialogBarOptions();

            Check("QuickSwitch 收藏：新增成功", QuickSwitchCatalog.AddFavorite(options, folder, out string error), error);
            Check("QuickSwitch 收藏：重复目录被拒绝", !QuickSwitchCatalog.AddFavorite(options, folder, out _));
            Check("QuickSwitch 收藏：列表有一条", QuickSwitchCatalog.Favorites(options.Favorites).Count == 1);
            Check("QuickSwitch 收藏：名称取目录名", options.Favorites[0].Name.Length > 0);

            var menu = QuickSwitchCatalog.Menu();
            Check("QuickSwitch 菜单：四个入口", menu.Count == 4);
            Check("QuickSwitch 菜单：含切换文件夹", menu.Any(r => r.Action == QuickSwitchAction.RevealExplorerFolder));
            Check("QuickSwitch 菜单：含设置", menu.Any(r => r.Action == QuickSwitchAction.OpenSettings));
            Check("QuickSwitch 菜单：含隐藏子菜单", menu.Any(r => r.Action == QuickSwitchAction.ShowHideMenu));
            Check("QuickSwitch 菜单：含关闭", menu.Any(r => r.Action == QuickSwitchAction.CloseBar));

            var hide = QuickSwitchCatalog.HideMenu();
            Check("QuickSwitch 隐藏：三种语义", hide.Count == 3
                && hide.Any(r => r.Action == QuickSwitchAction.HideSession)
                && hide.Any(r => r.Action == QuickSwitchAction.HideProcess)
                && hide.Any(r => r.Action == QuickSwitchAction.HideGlobal));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) { InputPlus.Core.CoreLog.Swallow(ex, "清理 QuickSwitch 目录自检"); }
        }
    }

    private static void TestQuickSwitchHistory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ip-qs-history-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(dir);
            string a = Directory.CreateDirectory(Path.Combine(dir, "a")).FullName;
            string b = Directory.CreateDirectory(Path.Combine(dir, "b")).FullName;

            QuickSwitchHistory.ResetForTest();
            QuickSwitchHistory.StorePathForTest = Path.Combine(dir, "history.json");
            QuickSwitchHistory.Clear();
            Check("QuickSwitch 历史：清空后为空", QuickSwitchHistory.Top(20).Count == 0);

            QuickSwitchHistory.Record(a);
            QuickSwitchHistory.Record(a);
            var one = QuickSwitchHistory.Top(20);
            Check("QuickSwitch 历史：重复目录只保留一条", one.Count == 1);
            Check("QuickSwitch 历史：频次累计", one.Count == 1 && one[0].Uses >= 2);
            Check("QuickSwitch 历史：路径正确", one.Count == 1 && string.Equals(one[0].Path, a.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

            QuickSwitchHistory.Record(b);
            var two = QuickSwitchHistory.Top(20);
            Check("QuickSwitch 历史：最新目录排第一", two.Count == 2 && string.Equals(two[0].Path, b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

            QuickSwitchHistory.Clear();
            Check("QuickSwitch 历史：清空只清展示缓存", QuickSwitchHistory.Top(20).Count == 0);
            Check("QuickSwitch 历史：缓存文件写入", File.Exists(Path.Combine(dir, "history.json")));
        }
        finally
        {
            QuickSwitchHistory.ResetForTest();
            try { Directory.Delete(dir, true); } catch (Exception ex) { InputPlus.Core.CoreLog.Swallow(ex, "清理 QuickSwitch 历史自检"); }
        }
    }

    private static DialogChild Child(string cls, int id = 0, string text = "")
        => new(0x1000 + id, cls, id, text, true);

    private static void TestDialogClassifyRules()
    {
        // a) Vista+ 文件对话框
        var vista = DialogClassifier.Classify("#32770", new[] { Child("DirectUIHWND"), Child("Edit") });
        CheckEqual("判型 a：DirectUIHWND → FileDialog", vista.Kind, DialogKind.FileDialog);
        CheckEqual("判型 a：家族 VistaDialog", vista.Family, DialogFamily.VistaDialog);
        Check("判型 a：要挂浮层", vista.WantsBar);

        var vista2 = DialogClassifier.Classify("#32770", new[] { Child("SHELLDLL_DefView") });
        CheckEqual("判型 a：只有 SHELLDLL_DefView 也算 Vista", vista2.Family, DialogFamily.VistaDialog);

        // b) 老式 comdlg32
        var legacy = DialogClassifier.Classify("#32770", new[] { Child("ComboBoxEx32"), Child("Edit") });
        CheckEqual("判型 b：ComboBoxEx32+Edit → 老式", legacy.Family, DialogFamily.LegacyDialog);
        CheckEqual("判型 b：仍是 FileDialog", legacy.Kind, DialogKind.FileDialog);

        // c) 文件夹对话框
        var folder = DialogClassifier.Classify("#32770", new[] { Child("ShellNameSpace Control"), Child("Button") });
        CheckEqual("判型 c：ShellNameSpace Control → FolderDialog", folder.Kind, DialogKind.FolderDialog);
        var folder2 = DialogClassifier.Classify("#32770", new[] { Child("SysTreeView32") });
        CheckEqual("判型 c：SysTreeView32 → FolderDialog", folder2.Family, DialogFamily.FolderPicker);

        // d) 资源管理器：占位，不挂
        var explorer = DialogClassifier.Classify("CabinetWClass", new[] { Child("SHELLDLL_DefView") });
        CheckEqual("判型 d：CabinetWClass → ExplorerWindow", explorer.Kind, DialogKind.ExplorerWindow);
        Check("判型 d：资源管理器不挂浮层", !explorer.WantsBar);

        // e) 其它
        var unknown = DialogClassifier.Classify("#32770", new[] { Child("Edit"), Child("Button") });
        CheckEqual("判型 e：裸 #32770 + Edit → Unknown", unknown.Kind, DialogKind.Unknown);
        Check("判型 e：Unknown 不挂浮层", !unknown.WantsBar);
        var notDialog = DialogClassifier.Classify("Notepad", Array.Empty<DialogChild>());
        CheckEqual("判型 e：非 #32770 → Unknown", notDialog.Kind, DialogKind.Unknown);

        // 顺序：同时有 SysTreeView32 与 DirectUIHWND 时必须先判文件对话框
        var both = DialogClassifier.Classify("#32770",
            new[] { Child("SysTreeView32"), Child("DirectUIHWND") });
        CheckEqual("判型顺序：Vista 优先于 Folder", both.Kind, DialogKind.FileDialog);

        // 判型不看 exe：同一组控件，进程名不同结果必须一致
        CheckEqual("判型与进程名无关：wechat.exe", DialogClassifier.Classify("#32770", new[] { Child("DirectUIHWND") }).Kind, DialogKind.FileDialog);
        CheckEqual("判型与进程名无关：notepad.exe", DialogClassifier.Classify("#32770", new[] { Child("DirectUIHWND") }).Kind, DialogKind.FileDialog);
    }

    private static void TestDialogPolicySwitches()
    {
        var info = new DialogWindowInfo
        {
            Hwnd = 0x1234,
            ProcessName = "wechat.exe",
            ClassName = "#32770",
            Title = "打开",
        };
        var ok = DialogClassifier.Classify("#32770", new[] { Child("DirectUIHWND") });
        var types = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [DialogTypeKeys.FileDialog] = true,
        };

        Check("开关：全开时挂", DialogPolicy.ShouldAttach(info, ok, true, types, Array.Empty<string>(), out _));

        bool on = DialogPolicy.ShouldAttach(info, ok, false, types, Array.Empty<string>(), out string r1);
        Check("开关：总开关关闭 → 不挂", !on);
        Check("开关：理由写明总开关", r1.Contains("总开关"), r1);

        types[DialogTypeKeys.FileDialog] = false;
        bool byType = DialogPolicy.ShouldAttach(info, ok, true, types, Array.Empty<string>(), out string r2);
        Check("开关：类型开关关闭 → 不挂", !byType);
        Check("开关：理由写明类型", r2.Contains(DialogTypeKeys.FileDialog), r2);
        types[DialogTypeKeys.FileDialog] = true;

        bool byProc = DialogPolicy.ShouldAttach(info, ok, true, types, new[] { "WeChat.exe" }, out string r3);
        Check("开关：进程禁用（大小写不敏感）→ 不挂", !byProc);
        Check("开关：理由写明进程", r3.Contains("wechat.exe", StringComparison.OrdinalIgnoreCase), r3);

        var notepad = new DialogWindowInfo
        {
            Hwnd = info.Hwnd,
            ProcessName = "notepad.exe",
            ClassName = info.ClassName,
            Title = info.Title,
        };
        bool otherProc = DialogPolicy.ShouldAttach(notepad, ok, true, types, new[] { "wechat.exe" }, out _);
        Check("开关：禁用别的进程不影响自己", otherProc);

        bool unknown = DialogPolicy.ShouldAttach(
            info, DialogClassification.Unknown, true, types, Array.Empty<string>(), out _);
        Check("开关：Unknown 永不挂", !unknown);
    }

    /// <summary>造一个真 #32770 + Edit 子窗口，验证探针能读到类名/控件 ID/文本。</summary>
    private static void TestDialogProbeRealWindow()
    {
        nint hwnd = 0, edit = 0;
        try
        {
            hwnd = CreateWindowExW(0, "#32770", "inputplus-fdb-probe", 0x80000000u /*WS_POPUP*/,
                0, 0, 300, 200, 0, 0, 0, 0);
            if (hwnd == 0)
            {
                Check("探针：自检窗口建不出来（跳过真实窗口断言）", true, "CreateWindowEx 返回 0");
                return;
            }

            edit = CreateWindowExW(0, "Edit", "hello", 0x50000000u /*WS_CHILD|WS_VISIBLE*/,
                10, 10, 100, 20, hwnd, 0x047C, 0, 0);

            string cls = DialogNative.ClassOf(hwnd);
            CheckEqual("探针：类名读回 #32770", cls, "#32770");

            var children = DialogProbe.Children(hwnd);
            Check("探针：枚举到子窗口", children.Count >= 1, $"实际 {children.Count} 个");
            var found = children.FirstOrDefault(c => string.Equals(c.ClassName, "Edit", StringComparison.OrdinalIgnoreCase));
            Check("探针：子窗口类名为 Edit", found != null);
            CheckEqual("探针：子窗口控件 ID", found?.ControlId ?? 0, 0x047C);
            CheckEqual("探针：子窗口文本", found?.Text ?? "", "hello");

            string report = DialogProbe.Describe(hwnd);
            Check("探针：报告含类名", report.Contains("#32770"));
            Check("探针：报告含控件 ID", report.Contains("0x047C"));

            var verdict = DialogClassifier.Classify("#32770", children);
            CheckEqual("探针：裸 Edit 判为 Unknown", verdict.Kind, DialogKind.Unknown);

            string full = DialogProbe.ClassifyAndDescribe(hwnd);
            Check("探针：一键报告含判型结论", full.Contains("判型："));
        }
        catch (Exception ex)
        {
            Check("探针：真实窗口断言未抛异常", false, ex.Message);
        }
        finally
        {
            if (edit != 0) DestroyWindow(edit);
            if (hwnd != 0) DestroyWindow(hwnd);
        }
    }

    private static void TestDialogWatcherDebounce()
    {
        nint hwnd = 0;
        var w = new FileDialogWatcher();
        try
        {
            hwnd = CreateWindowExW(0, "#32770", "inputplus-fdb-watch", 0x80000000u,
                0, 0, 200, 120, 0, 0, 0, 0);
            if (hwnd == 0)
            {
                Check("监听：自检窗口建不出来（跳过去抖断言）", true);
                return;
            }

            int appeared = 0;
            w.Appeared += _ => appeared++;

            w.Track(hwnd);
            CheckEqual("监听：首次发现触发一次", appeared, 1);
            CheckEqual("监听：跟踪数 1", w.TrackedCount, 1);

            w.Track(hwnd);          // 500ms 内重复 → 必须被去抖
            CheckEqual("监听：500ms 内重复事件被去抖", appeared, 1);
            CheckEqual("监听：去抖后跟踪数仍为 1", w.TrackedCount, 1);

            var info = FileDialogWatcher.Describe(hwnd);
            Check("监听：能取到窗口信息", info != null);
            CheckEqual("监听：进程名是自身", info?.ProcessId ?? 0, (uint)Environment.ProcessId);

            w.Forget(hwnd);
            CheckEqual("监听：Forget 后跟踪归零", w.TrackedCount, 0);

            // 非 #32770 不该进来
            nint plain = CreateWindowExW(0, "STATIC", "inputplus-fdb-plain", 0x80000000u,
                0, 0, 100, 100, 0, 0, 0, 0);
            if (plain != 0)
            {
                int before = appeared;
                w.Track(plain);
                CheckEqual("监听：非 #32770 不跟踪", appeared, before);
                DestroyWindow(plain);
            }
        }
        catch (Exception ex)
        {
            Check("监听：断言未抛异常", false, ex.Message);
        }
        finally
        {
            w.Dispose();
            if (hwnd != 0) DestroyWindow(hwnd);
        }
    }

    /// <summary>搜索侧的纯逻辑：query plan（搜索词 + 目录/文件过滤）不依赖窗口。</summary>
    private static void TestDialogSearchQueryPlan()
    {
        var plan = DialogSearchQuery.Create("报表", DialogScope.All);
        Check("搜索：保留关键词", plan.Keyword == "报表");
        CheckEqual("搜索：默认不限类型", plan.Scope, DialogScope.All);

        CheckEqual("搜索：按 Tab 切到文件夹", DialogSearchQuery.NextScope(DialogScope.All), DialogScope.Folders);
        CheckEqual("搜索：再切到文件", DialogSearchQuery.NextScope(DialogScope.Folders), DialogScope.Files);
        CheckEqual("搜索：再切回全部", DialogSearchQuery.NextScope(DialogScope.Files), DialogScope.All);

        Check("搜索：文件名高亮取首个匹配", DialogSearchQuery.Highlight("季度报表.xlsx", "报表") is { Length: > 0 });
        Check("搜索：空关键词不高亮", DialogSearchQuery.Highlight("季度报表.xlsx", "") == null);
    }

    /// <summary>记录层：成功回填写一行 JSONL，空查询靠它给"最近在此类对话框选过的项"。</summary>
    private static void TestDialogRecorder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ip-fdb-recorder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string store = Path.Combine(dir, "dialog-history.jsonl");
        try
        {
            DialogRecorder.StorePathForTest = store;
            Check("记录：临时库路径生效", DialogRecorder.StorePath == store, DialogRecorder.StorePath);

            DialogRecorder.Record(new DialogSelection(@"C:\Temp\报表.xlsx", false, "报表.xlsx"), "WeChat.exe", BackfillRoute.Uia);
            DialogRecorder.Record(new DialogSelection(@"C:\Temp\附件", true, "附件"), "msedge.exe", BackfillRoute.Keyboard);
            DialogRecorder.Record(new DialogSelection(@"C:\Temp\附件", true, "附件"), "notepad.exe", BackfillRoute.Keyboard);

            Check("记录：文件已落盘", File.Exists(store));
            CheckEqual("记录：只留最近 200 条以内的三条", DialogRecorder.Recent(10).Count, 3);

            var recent = DialogRecorder.RecentSelections(10);
            CheckEqual("记录：空态列表按路径去重", recent.Count, 2);
            CheckEqual("记录：最新的排最前", recent[0].DisplayName, "附件");
            CheckEqual("记录：去重后保留文件夹类型", recent[0].IsFolder, true);
            Check("记录：空态项带来源标签", recent.All(r => r.Source == "最近"), string.Join(",", recent.Select(r => r.Source)));

            string line = File.ReadAllLines(store).FirstOrDefault() ?? "";
            Check("记录：JSONL 含 surface 字段", line.Contains("file_dialog:wechat.exe"), line);
            Check("记录：不记录文件内容（只有元数据字段）", !line.Contains("Content"), line);
        }
        catch (Exception ex)
        {
            Check("记录：断言未抛异常", false, ex.Message);
        }
        finally
        {
            DialogRecorder.ClearForTest();
            DialogRecorder.StorePathForTest = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>搜索提供者：空查询必须走记录层，且受条数上限约束；非空查询不能抛。</summary>
    private static void TestDialogSearchProvider()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ip-fdb-provider", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string store = Path.Combine(dir, "dialog-history.jsonl");
        try
        {
            DialogRecorder.StorePathForTest = store;
            DialogRecorder.Record(new DialogSelection(@"C:\Temp\甲.txt", false, "甲.txt"), "notepad.exe", BackfillRoute.Keyboard);
            DialogRecorder.Record(new DialogSelection(@"C:\Temp\乙.txt", false, "乙.txt"), "notepad.exe", BackfillRoute.Keyboard);

            var provider = new DialogSearchProvider(() => new InputPlus.Core.Settings.AppSettings());
            var empty = provider.Search(DialogSearchQuery.Create("", DialogScope.All), 200);
            CheckEqual("搜索提供者：空查询取记录层两条", empty.Count, 2);
            CheckEqual("搜索提供者：空查询第一条是最新项", empty[0].DisplayName, "乙.txt");

            var limited = provider.Search(DialogSearchQuery.Create("", DialogScope.All), 1);
            CheckEqual("搜索提供者：limit 生效", limited.Count, 1);

            var keyword = provider.Search(DialogSearchQuery.Create("不存在的关键词zzz", DialogScope.All), 20);
            Check("搜索提供者：查无结果返回空数组而不是 null", keyword != null);
        }
        catch (Exception ex)
        {
            Check("搜索提供者：断言未抛异常", false, ex.Message);
        }
        finally
        {
            DialogRecorder.ClearForTest();
            DialogRecorder.StorePathForTest = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestDialogHotkeyValidation()
    {
        Check("热键：Ctrl+Alt+F 合法", FileDialogBarService.TryValidateHotkey("Ctrl+Alt+F", out string normalized), normalized);
        CheckEqual("热键：写法规范化", normalized, "Ctrl+Alt+F");
        Check("热键：乱写被判非法", !FileDialogBarService.TryValidateHotkey("这不是热键", out _));
        Check("热键：空串等于关闭（不算非法）", !FileDialogBarService.TryValidateHotkey("", out _));
    }
}