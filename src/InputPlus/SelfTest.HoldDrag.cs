using System;
using InputPlus.Core.HoldDrag;
using InputPlus.Core.Settings;
using InputPlus.Modules.HoldDrag;

namespace InputPlus;

/// <summary>
/// 自检：并进来的「按住拖拽」。
///
/// 引擎本体（钩子、拖窗）在这里**不启动** —— 自检跑在用户桌面上，
/// 装一个全局鼠标钩子会把用户的鼠标拖歪。所以只验到"配置这一层"：
/// 默认关、JSON 往返、越界值被收敛、关着的时候绝不装钩子。
/// 之所以强调默认关：触发键默认是 Enter，装上就等于抢走回车，
/// 用户只会觉得键盘坏了。
/// </summary>
public static partial class SelfTest
{
    private static void TestHoldDrag()
    {
        var fresh = new AppSettings();
        Check("按住拖拽：默认是关的", !fresh.HoldDragEnabled);
        CheckEqual("按住拖拽：默认没存过配置", fresh.HoldDragJson, "");

        // ---- 空串要能解析成一份能用的默认配置 ----
        var cfg = ConfigStore.Parse("");
        Check("按住拖拽：空配置解析得出默认值", cfg != null);
        if (cfg is null) cfg = new AppConfig();
        CheckEqual("按住拖拽：默认触发键是 Enter", cfg.TriggerKeyId, "K:0x0D");
        Check("按住拖拽：默认长按时长是 300ms", cfg.HoldMs == 300, cfg.HoldMs.ToString());
        CheckEqual("按住拖拽：左键默认拖", cfg.ActionLeft, Actions.Move);
        CheckEqual("按住拖拽：右键默认缩放", cfg.ActionRight, Actions.Resize);
        CheckEqual("按住拖拽：中键默认最大化", cfg.ActionMiddle, Actions.Maximize);

        // ---- JSON 往返：存下来的东西能原样读回来 ----
        cfg.TriggerKeyId = "K:0x20";
        cfg.TriggerMode = "Toggle";
        cfg.HoldMs = 450;
        cfg.FrameStyleId = "Aurora";
        cfg.FrameColorId = "Accent";
        cfg.SnapThreshold = 12;
        cfg.Blacklist = "game.exe";
        string json = ConfigStore.Serialize(cfg);
        var back = ConfigStore.Parse(json);
        CheckEqual("按住拖拽：往返保住触发键", back.TriggerKeyId, "K:0x20");
        CheckEqual("按住拖拽：往返保住触发方式", back.TriggerMode, "Toggle");
        Check("按住拖拽：往返保住长按时长", back.HoldMs == 450, back.HoldMs.ToString());
        CheckEqual("按住拖拽：往返保住外框样式", back.FrameStyleId, "Aurora");
        CheckEqual("按住拖拽：往返保住外框颜色", back.FrameColorId, "Accent");
        Check("按住拖拽：往返保住吸附阈值", back.SnapThreshold == 12, back.SnapThreshold.ToString());
        CheckEqual("按住拖拽：往返保住黑名单", back.Blacklist, "game.exe");

        // ---- 坏值一律收敛，不许把引擎带崩 ----
        var wild = ConfigStore.Parse("");
        wild.HoldMs = 99999;
        wild.FrameThickness = 0;
        wild.FrameOpacity = 500;
        wild.SnapThreshold = 400;
        wild.TriggerKeyId = "K:0x00";
        wild.FrameStyleId = "不存在的样式";
        wild.ActionLeft = "不存在的动作";
        var fixedUp = ConfigStore.Parse(ConfigStore.Serialize(wild));
        Check("按住拖拽：长按时长被夹回 3000", fixedUp.HoldMs == 3000, fixedUp.HoldMs.ToString());
        Check("按住拖拽：粗细被夹到最小 1", fixedUp.FrameThickness == 1, fixedUp.FrameThickness.ToString());
        Check("按住拖拽：不透明度被夹回 100", fixedUp.FrameOpacity == 100, fixedUp.FrameOpacity.ToString());
        Check("按住拖拽：吸附阈值被夹回 40", fixedUp.SnapThreshold == 40, fixedUp.SnapThreshold.ToString());
        CheckEqual("按住拖拽：无效触发键回落 Enter", fixedUp.TriggerKeyId, "K:0x0D");
        Check("按住拖拽：无效样式回落默认样式",
            string.Equals(fixedUp.FrameStyleId, FrameStyles.DefaultId, StringComparison.Ordinal), fixedUp.FrameStyleId);
        CheckEqual("按住拖拽：无效动作回落拖动", fixedUp.ActionLeft, Actions.Move);

        // ---- 外框样式只留三套：细线环、虚线环、流光边 ----
        Check("按住拖拽：外框样式只剩三套", FrameStyles.All.Length == 3, FrameStyles.All.Length.ToString());
        CheckEqual("按住拖拽：三套样式是 虚线环/细线环/流光边",
            string.Join("|", FrameStyles.All), "Dashed|Hairline|Aurora");
        Check("按住拖拽：细线环还在", FrameStyles.IsValid("Hairline"));
        Check("按住拖拽：流光边还在", FrameStyles.IsValid("Aurora"));
        Check("按住拖拽：旧样式 柔光 已经删掉", !FrameStyles.IsValid("Glow"));
        Check("按住拖拽：旧样式 霓虹 已经删掉", !FrameStyles.IsValid("Neon"));
        Check("按住拖拽：旧样式 底部条 已经删掉", !FrameStyles.IsValid("Underline"));
        var legacyStyle = ConfigStore.Parse("{\"FrameStyleId\":\"Glow\"}");
        CheckEqual("按住拖拽：老配置里的旧样式自动回落虚线环", legacyStyle.FrameStyleId, FrameStyles.DefaultId);

        // ---- 关着的时候：装配一次也不许装钩子 ----
        var settings = new AppSettings();
        using (var service = new HoldDragService(() => settings))
        {
            service.ApplySettings();
            Check("按住拖拽：关着时不会装钩子", !service.Installed);
            Check("按住拖拽：关着时也不报错", string.IsNullOrEmpty(service.LastError), service.LastError);

            var editable = service.EditableConfig();
            Check("按住拖拽：没配置时也能给出可编辑的默认值", editable != null && editable.HoldMs == 300);
            if (editable is null) editable = new AppConfig();

            editable.HoldMs = 520;
            editable.TriggerKeyId = "K:0x20";
            int persisted = 0;
            var withSave = new HoldDragService(() => settings, _ => persisted++);
            Check("按住拖拽：改参数能写回设置", withSave.ApplyEditedConfig(editable));
            withSave.Dispose();
            Check("按住拖拽：写回时通知了宿主保存", persisted == 1, persisted.ToString());

            var saved = ConfigStore.Parse(settings.HoldDragJson);
            Check("按住拖拽：写回的参数读得出来", saved.HoldMs == 520, saved.HoldMs.ToString());
            CheckEqual("按住拖拽：写回的触发键读得出来", saved.TriggerKeyId, "K:0x20");
            Check("按住拖拽：改参数不会顺手把功能打开", !settings.HoldDragEnabled);
            Check("按住拖拽：写回之后钩子仍然没装", !withSave.Installed);
        }

        // ---- 触发键不能被配成一个普通字母键（否则打字都变成拖窗） ----
        Check("按住拖拽：候选触发键都解析得出虚拟键码",
            KeysUtil.ToVirtualKey("K:0x0D") != 0 && KeysUtil.ToVirtualKey("K:0x20") != 0
            && KeysUtil.ToVirtualKey("K:LShift") != 0);
        Check("按住拖拽：Enter 显示成可读名字而不是裸 id",
            KeysUtil.DisplayName("K:0x0D").Length > 0 && KeysUtil.DisplayName("K:0x0D") != "K:0x0D");


        // ---- 触发键候选表覆盖住了要显示的名字（下拉框不能出现裸 id） ----
        Check("按住拖拽：候选触发键都有可读名字",
            KeysUtil.DisplayName("M:1").Length > 0 && KeysUtil.DisplayName("K:LShift").Length > 0);

        // ---- 黑名单/规则：两个挑程序的入口与手打共用同一个追加逻辑 ----
        CheckEqual("黑名单追加：空清单直接放进去",
            UI.Views.HoldDragView.AppendToken("", "obs64.exe"), "obs64.exe");
        CheckEqual("黑名单追加：换行分隔、保持原顺序",
            UI.Views.HoldDragView.AppendToken("obs64.exe\ngame*.exe", "code.exe"),
            "obs64.exe\ngame*.exe\ncode.exe");
        CheckEqual("黑名单追加：同一个程序不会进两次（忽略大小写）",
            UI.Views.HoldDragView.AppendToken("OBS64.exe", "obs64.exe"), "OBS64.exe");
        CheckEqual("黑名单追加：空白行会被抹掉",
            UI.Views.HoldDragView.AppendToken("  obs64.exe  \n\n", "code.exe"), "obs64.exe\ncode.exe");

        CheckEqual("按程序规则：新程序写成 程序名=行为",
            UI.Views.HoldDragView.UpsertRule("", "wps.exe", "Ignore"), "wps.exe=Ignore");
        CheckEqual("按程序规则：同名换行为，不新增一行",
            UI.Views.HoldDragView.UpsertRule("wps.exe=Ignore", "wps.exe", "MoveOnly"), "wps.exe=MoveOnly");
        CheckEqual("按程序规则：不同程序各占一行",
            UI.Views.HoldDragView.UpsertRule("wps.exe=Ignore", "code.exe", "NoFrame"),
            "wps.exe=Ignore\ncode.exe=NoFrame");
        CheckEqual("按程序规则：行为留空按完全不接管算",
            UI.Views.HoldDragView.UpsertRule("", "game.exe", ""), "game.exe=Ignore");
        // 手打用分号（引擎自己的写法）也得拆开，不能把 "; code.exe=MoveOnly" 当成行为名
        CheckEqual("按程序规则：分号分隔的清单也拆得开",
            UI.Views.HoldDragView.UpsertRule("wps.exe=Ignore; code.exe=MoveOnly", "code.exe", "NoFrame"),
            "wps.exe=Ignore\ncode.exe=NoFrame");

        // ---- 清单是配置的视图：删掉一条就等于把它从配置里拿掉 ----
        CheckEqual("程序例外：删一条不影响别的", UI.Views.HoldDragView.RemoveToken("a.exe\nb.exe", "b.exe"), "a.exe");
        CheckEqual("程序例外：删不存在的条目什么都不变",
            UI.Views.HoldDragView.RemoveToken("a.exe", "zz.exe"), "a.exe");
        CheckEqual("按程序规则：删规则按程序名匹配",
            UI.Views.HoldDragView.RemoveRule("wps.exe=Ignore; code.exe=MoveOnly", "wps.exe"), "code.exe=MoveOnly");

        // ---- 拖动不再按屏幕刷新率节流（AltDrag 那套：每个鼠标移动事件立刻落位） ----
        // 这两个配置字段和节流 API 已经删掉，留着的话就会有人"顺手"再把它接回移动路径。
        Check("按住拖拽：配置里已经没有「跟随刷新率」",
            typeof(AppConfig).GetProperty("DragRefreshMode") is null && typeof(AppConfig).GetProperty("DragRefreshHz") is null);
        Check("按住拖拽：拖动线程没有帧间隔节流",
            typeof(DragWorker).GetProperty("FrameIntervalMs") is null
            && typeof(AppConfig).GetMethod("FrameIntervalMs") is null);

        // ---- 拖动跟手：「手停了窗口还在跑」的病根 ----
        TestHoldDragTailMath();
        TestHoldDragLiveCursor();
    }

    // ---------------- 收尾对账的算术 ----------------
    // 松手时最后那一帧写下去之后要跟窗口的真实矩形对一次，对不上就补写。
    // 这段全是整数减法，单独钉住，免得哪天"顺手优化"把符号或宽高算反。
    private static void TestHoldDragTailMath()
    {
        var want = new Native.RECT(100, 100, 400, 300);

        Check("收尾对账：严丝合缝不算偏",
            !DragEngine.TailMismatch(want, new Native.RECT(100, 100, 400, 300), out _, out _, out _, out _));

        bool off = DragEngine.TailMismatch(want, new Native.RECT(103, 96, 403, 296), out int dx, out int dy, out int dw, out int dh);
        Check("收尾对账：偏 3,-4 能带符号算出来",
            off && dx == 3 && dy == -4 && dw == 0 && dh == 0, dx + "," + dy + "," + dw + "," + dh);

        bool sized = DragEngine.TailMismatch(want, new Native.RECT(100, 100, 420, 280), out _, out _, out int dw2, out int dh2);
        Check("收尾对账：目标自己改了尺寸也算偏",
            sized && dw2 == 20 && dh2 == -20, dw2 + "," + dh2);
    }

    // ---------------- 拖动只认「此刻的光标」 ----------------
    // 用户的原话是"我手停了、鼠标都不动了，窗口还会跑一段时间，最后跑到不是我要的位置"。
    // 病根：钩子 → 工作线程 → 目标程序这条链会排队，事件里带的坐标是"过去的坐标"，
    // 把它写下去，窗口就是在追历史。AltDrag 的做法是每个事件到了现取 GetCursorPos。
    //
    // 这里用**真窗口 + 假光标**把这条规则钉死：事件里的坐标再怎么离谱，窗口也只认光标。
    // 假光标省得为了跑一次自检去挪用户的鼠标；真窗口则保证走的是真正的 SetWindowPos 链路。
    private static void TestHoldDragLiveCursor()
    {
        nint hwnd = IntPtr.Zero;
        uint tid = 0u;
        var ready = new System.Threading.ManualResetEventSlim(initialState: false);
        var pump = new System.Threading.Thread(() =>
        {
            // 窗口必须建在一台会转消息的线程上：工作线程 SetWindowPos 时要跨线程发消息，
            // 那边不抽消息就会把写窗口的那一帧卡死（测试反而会超时）。
            hwnd = Native.CreateWindowExW(0u, "STATIC", "inputplus-drag-selftest", 0u, 100, 100, 300, 200, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
            tid = Native.GetCurrentThreadId();
            ready.Set();
            Native.MSG msg;
            while (Native.GetMessageW(out msg, IntPtr.Zero, 0u, 0u) > 0)
            {
                Native.DispatchMessageW(ref msg);
            }
        });
        pump.IsBackground = true;
        pump.Start();
        ready.Wait(3000);

        Func<Native.POINT?> previous = DragEngine.CursorProbeForTest;
        DragEngine? engine = null;
        try
        {
            if (hwnd == IntPtr.Zero)
            {
                Check("按住拖拽跟手：自检窗口建不出来（跳过拖动行为断言）", true, "CreateWindowEx 返回 0");
                return;
            }
            Native.SetWindowPos(hwnd, IntPtr.Zero, 100, 100, 300, 200, 4u);

            var cfg = ConfigStore.Parse("");
            cfg.SnapToEdges = false;
            cfg.ShowDragHud = false;
            cfg.ShowActivationFrame = false;
            cfg.BringToFront = false;
            cfg.ShakeMinimizeOthers = false;
            cfg.DragThreshold = 0;
            cfg.DragOutThresholdPx = 0;
            cfg.MoveRate = 1;
            DragDiag.Init(InputPlus.Core.AppPaths.DataDir);
            engine = new DragEngine(() => cfg);

            int fx = 1000;
            int fy = 800;
            DragEngine.CursorProbeForTest = () => new Native.POINT(fx, fy);
            bool began = engine.Begin(hwnd, move: true, button: 0, pt: new Native.POINT(1000, 800));
            Check("按住拖拽跟手：能开始拖动自检窗口", began);

            // ① 事件里的坐标离光标 600px：旧实现会把窗口拽过去，现在它必须纹丝不动
            for (int i = 0; i < 8; i++)
            {
                engine.DeliverCursor(new Native.POINT(1600, 1200));
            }
            System.Threading.Thread.Sleep(180);
            Native.GetWindowRect(hwnd, out Native.RECT r1);
            Check("按住拖拽跟手：事件里的过期坐标不会把窗口带跑",
                Math.Abs(r1.Left - 100) <= 1 && Math.Abs(r1.Top - 100) <= 1, r1.Left + "," + r1.Top);

            // ② 光标真的走了 40,50 → 窗口必须正好跟 40,50（哪怕事件里给的是 0,0）
            fx = 1040;
            fy = 850;
            engine.DeliverCursor(new Native.POINT(0, 0));
            System.Threading.Thread.Sleep(180);
            Native.GetWindowRect(hwnd, out Native.RECT r2);
            Check("按住拖拽跟手：光标走 40,50，窗口跟着走 40,50",
                Math.Abs(r2.Left - 140) <= 1 && Math.Abs(r2.Top - 150) <= 1, r2.Left + "," + r2.Top);

            // ③ 松手：收尾那一帧之后窗口必须还停在光标对应的位置
            engine.End();
            System.Threading.Thread.Sleep(300);
            Native.GetWindowRect(hwnd, out Native.RECT r3);
            Check("按住拖拽收尾：松手后停在光标对应的位置，不再自己往前跑",
                Math.Abs(r3.Left - r2.Left) <= 1 && Math.Abs(r3.Top - r2.Top) <= 1, r3.Left + "," + r3.Top);

            bool wrote = false;
            string diagPath = DragDiag.Path;
            try
            {
                wrote = diagPath.Length > 0 && System.IO.File.Exists(diagPath)
                    && System.IO.File.ReadAllText(diagPath).Contains("收尾补写");
            }
            catch
            {
            }
            Check("按住拖拽：拖动结束会往 drag-diag.txt 记一笔明文账", wrote, diagPath);
        }
        finally
        {
            DragEngine.CursorProbeForTest = previous;
            try
            {
                engine?.Dispose();
            }
            catch
            {
            }
            if (hwnd != IntPtr.Zero)
            {
                Native.DestroyWindow(hwnd);
            }
            if (tid != 0u)
            {
                Native.PostThreadMessageW(tid, 18u, IntPtr.Zero, IntPtr.Zero);
            }
            try
            {
                pump.Join(1000);
            }
            catch
            {
            }
            ready.Dispose();
        }
    }
}
