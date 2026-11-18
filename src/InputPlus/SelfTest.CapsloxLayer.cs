using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using InputPlus.Core.Actions;
using InputPlus.Core.Interop;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;

namespace InputPlus;

/// <summary>
/// 自检：Capslox 3.0 的基础层语义 + 「唤出窗口」动画。
///
/// 两块都是"看得见的行为"，靠肉眼验证不可靠：
///   · 动画的跳过条件一旦写反，用户会看到后台窗口莫名其妙抽一下；
///   · 默认表的迁移一旦写错，用户自己改过的键会被悄悄吃掉。
/// 所以这里把能纯函数化的部分（缓动、缩放矩形、槽位换算）全拆出来断言，
/// 真正碰窗口的部分只用**不该动**的句柄做负例。
/// </summary>
public static partial class SelfTest
{
    // 自检要造一个"本进程自己的窗口"当负例：用系统预定义的 STATIC 类，
    // 不必 RegisterClassEx，也不显示在屏幕上。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    private static void TestCapsloxLayer()
    {
        TestSummonAnimator();
        TestCapsloxSchemeDefaults();
        TestWindowSlotBinding();
        TestCapsloxMigration();
        TestSummonSettings();
    }

    // ---------------- ① 唤出动画：判定 + 缓动 + 缩放矩形 ----------------

    private static void TestSummonAnimator()
    {
        // 明显不该动的句柄
        Check("唤出动画：空句柄不做", !WindowSummonAnimator.IsAnimatable(IntPtr.Zero));
        Check("唤出动画：桌面窗口不做", !WindowSummonAnimator.IsAnimatable(NativeMethods.GetDesktopWindow()));

        /* 本进程自己的窗口：CreateWindowEx 建一个 200x200 的 STATIC（不显示），
         * 它是货真价实的窗口句柄，但属于本进程 → 必须被跳过。 */
        IntPtr own = CreateWindowExW(0, "STATIC", "ke-selftest", 0, 0, 0, 200, 200,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            Check("唤出动画：IsOwnProcess 认得出本进程窗口",
                own == IntPtr.Zero || WindowSummonAnimator.IsOwnProcess(own));

            bool animatable = WindowSummonAnimator.IsAnimatable(own, out string reason);
            Check("唤出动画：本进程窗口不做", own == IntPtr.Zero || !animatable);
            CheckEqual("唤出动画：跳过的原因是「本程序自己的窗口」", reason, "本程序自己的窗口");
        }
        finally
        {
            if (own != IntPtr.Zero) DestroyWindow(own);
        }

        // 缓动：端点 + 单调 + ease-out 的形状
        CheckEqual("缓动：t=0 → 0", WindowSummonAnimator.EaseOutCubic(0), 0.0);
        CheckEqual("缓动：t=1 → 1", WindowSummonAnimator.EaseOutCubic(1), 1.0);

        bool monotonic = true;
        double prev = -1;
        for (int i = 0; i <= 40; i++)
        {
            double v = WindowSummonAnimator.EaseOutCubic(i / 40.0);
            if (v < prev) monotonic = false;
            prev = v;
        }
        Check("缓动：全程单调不减", monotonic);
        Check("缓动：确实是 ease-out（中点已走完 80% 以上）",
            WindowSummonAnimator.EaseOutCubic(0.5) > 0.8,
            WindowSummonAnimator.EaseOutCubic(0.5).ToString("0.###"));

        // 缩放比例：0.92 → 1.0
        Check("缩放：起点是 0.92", Math.Abs(WindowSummonAnimator.ScaleAt(0, 170) - WindowSummonAnimator.StartScale) < 1e-9);
        Check("缩放：终点回到 1.0", Math.Abs(WindowSummonAnimator.ScaleAt(170, 170) - 1.0) < 1e-9);
        CheckEqual("缩放：时长为 0 时直接给 1.0（不做动画）", WindowSummonAnimator.ScaleAt(10, 0), 1.0);
        Check("缩放：中途严格夹在 0.92 与 1.0 之间",
            WindowSummonAnimator.ScaleAt(85, 170) is > 0.92 and < 1.0);

        // 缩放矩形：比例 1 必须逐像素回到原矩形（"收尾回正"的数学保证）
        var rect = new NativeMethods.RECT { Left = 100, Top = 50, Right = 1100, Bottom = 650 };
        var full = WindowSummonAnimator.FrameRect(rect, 1.0);
        Check("缩放矩形：比例 1 时逐像素等于原矩形",
            full.X == 100 && full.Y == 50 && full.Width == 1000 && full.Height == 600,
            $"{full.X},{full.Y},{full.Width},{full.Height}");

        var shrunk = WindowSummonAnimator.FrameRect(rect, 0.92);
        CheckEqual("缩放矩形：宽度按比例缩", shrunk.Width, 920);
        CheckEqual("缩放矩形：高度按比例缩", shrunk.Height, 552);
        Check("缩放矩形：左右留白相等（±2px）",
            Math.Abs((shrunk.X - rect.Left) - (rect.Right - (shrunk.X + shrunk.Width))) <= 2,
            $"{shrunk.X - rect.Left} vs {rect.Right - (shrunk.X + shrunk.Width)}");
        Check("缩放矩形：上下留白相等（±2px）",
            Math.Abs((shrunk.Y - rect.Top) - (rect.Bottom - (shrunk.Y + shrunk.Height))) <= 2,
            $"{shrunk.Y - rect.Top} vs {rect.Bottom - (shrunk.Y + shrunk.Height)}");

        /* DWM 的可见外框比窗口矩形小一圈（Win10 之后那圈透明可拖拽边框）。
         * 比例 1 时仍必须回到**窗口矩形**，否则每次唤出都会把窗口挪偏几像素。 */
        var visual = new NativeMethods.RECT { Left = 92, Top = 42, Right = 1108, Bottom = 658 };
        var framed = WindowSummonAnimator.FrameRect(rect, visual, 1.0);
        Check("缩放矩形：带 DWM 外框偏移时比例 1 仍回正",
            framed.X == 100 && framed.Y == 50 && framed.Width == 1000 && framed.Height == 600,
            $"{framed.X},{framed.Y},{framed.Width},{framed.Height}");

        Check("矩形比较：4px 以内算相同",
            WindowSummonAnimator.NearlySameRect(rect,
                new NativeMethods.RECT { Left = 103, Top = 50, Right = 1100, Bottom = 650 }, 4));
        Check("矩形比较：差 40px 不算相同",
            !WindowSummonAnimator.NearlySameRect(rect,
                new NativeMethods.RECT { Left = 140, Top = 74, Right = 1060, Bottom = 626 }, 8));
    }

    // ---------------- ② 默认表：Capslox 3.0 语义 + 纯增益键位 ----------------

    private static void TestCapsloxSchemeDefaults()
    {
        var s = DefaultSettings.Create();

        CheckEqual("默认表：Caps+Enter = 行尾回车",
            InputPlus.Core.Actions.CustomKeySpec.FromArg(Find(s, "caps_enter")?.Arg).Key, "end enter");
        CheckEqual("默认表：Caps+N = 选整行", Find(s, "caps_n")?.Action, "selectCurrentLine");
        CheckEqual("默认表：Caps+Alt+E = 上移 5", Find(s, "caps_lalt_e")?.Arg, "5");
        CheckEqual("默认表：Caps+Alt+D = 下移 5", Find(s, "caps_lalt_d")?.Arg, "5");
        CheckEqual("默认表：Caps+Alt+A = 词左 5", Find(s, "caps_lalt_a")?.Arg, "5");
        CheckEqual("默认表：Caps+Alt+1 = 记住槽位 1", Find(s, "caps_lalt_1")?.Action, "bindWindowSlot");
        CheckEqual("默认表：Caps+Alt+0 = 记住槽位 10", Find(s, "caps_lalt_0")?.Arg, "10");
        CheckEqual("默认表：Caps+Shift+V = 纯文本粘贴 1", Find(s, "caps_shift_v")?.Action, "paste1");
        CheckEqual("默认表：Caps+Shift+Alt+V = 纯文本粘贴 2", Find(s, "caps_shift_lalt_v")?.Action, "paste2");
        CheckEqual("默认表：Caps+Shift+, = 打开设置", Find(s, "caps_shift_comma")?.Action, "openSettings");
        CheckEqual("默认表：Caps+Shift+/ = 打开设置（edit_keymap 降级）",
            Find(s, "caps_shift_slash")?.Action, "openSettings");

        // Keyboard enhance 独有的键位不许被 Capslox 表冲掉
        CheckEqual("默认表：Caps+Q 仍是 Qbar", Find(s, "caps_q")?.Action, "qbar");
        CheckEqual("默认表：Caps+Tab 仍是 tabScript", Find(s, "caps_tab")?.Action, "tabScript");
        CheckEqual("默认表：Caps+- 仍是贴左边", Find(s, "caps_minus")?.Action, "winSnapLeft");
        CheckEqual("默认表：Caps+= 仍是贴右边", Find(s, "caps_equal")?.Action, "winSnapRight");
        CheckEqual("默认表：Caps+F5 仍是 reload", Find(s, "caps_f5")?.Action, "reload");
        CheckEqual("默认表：Caps+B 仍是下移 10", Find(s, "caps_b")?.Arg, "10");

        CheckEqual("默认表版本号已是最新", s.CapsloxDefaultsVersion, AppSettings.CapsloxDefaultsVersionCurrent);
    }

    // ---------------- ③ 槽位：写入、覆盖、非法输入、动作表 ----------------

    private static void TestWindowSlotBinding()
    {
        var s = DefaultSettings.Create();
        const string notepad = @"C:\Windows\notepad.exe";

        Check("槽位：3 号写入成功", WindowSlotBinder.TryBind(s, 3, notepad, "notepad", out string message));
        Check("槽位：提示里带程序名", message.Contains("notepad"), message);
        CheckEqual("槽位：caps_3 变成 winBind", Find(s, "caps_3")?.Action, "winBind");
        CheckEqual("槽位：caps_3 的参数是那个 exe", Find(s, "caps_3")?.Arg, notepad);
        CheckEqual("槽位：槽位表里记下了 3 号", WindowSlotBinder.Find(s, 3)?.ExePath, notepad);

        WindowSlotBinder.TryBind(s, 3, @"C:\Windows\explorer.exe", "explorer", out _);
        CheckEqual("槽位：同一个槽位只留一条（覆盖而不是追加）", s.WindowSlots.Count(x => x.Slot == 3), 1);
        CheckEqual("槽位：覆盖后 caps_3 指向新程序", Find(s, "caps_3")?.Arg, @"C:\Windows\explorer.exe");

        Check("槽位：0 号非法（返回 false 而不是抛异常）", !WindowSlotBinder.TryBind(s, 0, notepad, "n", out _));
        Check("槽位：11 号非法", !WindowSlotBinder.TryBind(s, 11, notepad, "n", out _));
        Check("槽位：空路径非法", !WindowSlotBinder.TryBind(s, 2, "", "n", out _));
        Check("槽位：null 路径非法", !WindowSlotBinder.TryBind(s, 2, null, "n", out _));

        CheckEqual("槽位：参数解析 \"99\" 非法", WindowSlotBinder.ParseSlot("99"), 0);
        CheckEqual("槽位：参数解析 \"10\" 合法", WindowSlotBinder.ParseSlot("10"), 10);
        CheckEqual("槽位：参数解析空串非法", WindowSlotBinder.ParseSlot(""), 0);
        CheckEqual("槽位：10 号对应 caps_0", WindowSlotBinder.ComboFor(10), "caps_0");
        CheckEqual("槽位：1 号对应 caps_1", WindowSlotBinder.ComboFor(1), "caps_1");

        // 动作表：只多一个带参数的槽位动作，不是 20 条固定槽位
        var settings = DefaultSettings.Create();
        var ctx = new ActionContext
        {
            Settings = () => settings,
            Clipboard = new ClipboardService(),
            Windows = new WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new MouseService(),
            Notify = _ => { },
        };
        var cat = ActionCatalog.Create(ctx);

        Check("动作表：bindWindowSlot 已注册", cat.Items.Any(i => i.Id == "bindWindowSlot"));
        CheckEqual("动作表：「窗口绑定」分类只有 2 个动作",
            cat.Items.Count(i => i.Category == "窗口绑定"), 2);
        Check("动作表：没有重新引入 winbind* 槽位动作",
            !cat.Items.Any(i => !string.Equals(i.Id, "winBind", StringComparison.Ordinal)
                                && i.Id.StartsWith("winBind", StringComparison.OrdinalIgnoreCase)));
        Check("动作表：非法槽位号 Invoke 返回 false",
            !cat.Invoke("bindWindowSlot", "0") && !cat.Invoke("bindWindowSlot", "abc"));
    }

    // ---------------- ④ 迁移：只改老默认值、幂等、不动经典方案 ----------------

    private static void TestCapsloxMigration()
    {
        /* 造一份"老配置"：版本号 0 + 几条老默认值。
         * caps_lalt_d 故意改成 9 —— 用户自己动过的值，迁移一个字都不许改。 */
        var s = DefaultSettings.Create();
        s.CapsloxDefaultsVersion = 0;
        Set(s, "caps_lalt_e", "moveUp", "3");
        Set(s, "caps_lalt_d", "moveDown", "9");
        Set(s, "caps_enter", "enterWherever", "");
        Set(s, "caps_n", "selectDown", "10");
        Set(s, "caps_lalt_1", "doNothing", "");

        SettingsStore.Normalize(s);

        CheckEqual("迁移：*3 升级成 *5", Find(s, "caps_lalt_e")?.Arg, "5");
        CheckEqual("迁移：用户改过的次数原样保留", Find(s, "caps_lalt_d")?.Arg, "9");
        CheckEqual("迁移：Caps+Enter 升级成行尾回车",
            InputPlus.Core.Actions.CustomKeySpec.FromArg(Find(s, "caps_enter")?.Arg).Key, "end enter");
        CheckEqual("迁移：Caps+N 升级成选整行", Find(s, "caps_n")?.Action, "selectCurrentLine");
        CheckEqual("迁移：Caps+Alt+1 升级成槽位捕获", Find(s, "caps_lalt_1")?.Action, "bindWindowSlot");
        CheckEqual("迁移：版本号升到最新", s.CapsloxDefaultsVersion, AppSettings.CapsloxDefaultsVersionCurrent);

        string once = Snapshot(s);
        SettingsStore.Normalize(s);
        CheckEqual("迁移：跑两遍结果完全一致（幂等）", Snapshot(s), once);

        // 用户删掉的键不补回来
        var del = DefaultSettings.Create();
        del.CapsloxDefaultsVersion = 0;
        del.LayerKeys(1).RemoveAll(k => string.Equals(k.Combo, "caps_lalt_2", StringComparison.OrdinalIgnoreCase));
        SettingsStore.Normalize(del);
        Check("迁移：用户删掉的键不会被补回来", Find(del, "caps_lalt_2") == null);

        // 经典方案不是 3.0 布局：只打版本号
        var classic = DefaultSettings.Create();
        classic.CapsloxDefaultsVersion = 0;
        classic.Global.HotkeyScheme = "capslock_plus";
        Set(classic, "caps_lalt_e", "moveUp", "3");
        SettingsStore.Normalize(classic);
        CheckEqual("迁移：经典方案不动键值", Find(classic, "caps_lalt_e")?.Arg, "3");
        CheckEqual("迁移：经典方案也打上版本号",
            classic.CapsloxDefaultsVersion, AppSettings.CapsloxDefaultsVersionCurrent);
    }

    // ---------------- ⑤ 唤出动画的设置（夹取 / 关掉不影响切换） ----------------

    private static void TestSummonSettings()
    {
        CheckEqual("设置：时长为 0 取默认 170", WindowSummonAnimator.ClampDuration(0),
            WindowSummonAnimator.DefaultDurationMs);
        CheckEqual("设置：时长小于下界夹到 60", WindowSummonAnimator.ClampDuration(10),
            WindowSummonAnimator.MinDurationMs);
        CheckEqual("设置：时长大于上界夹到 400", WindowSummonAnimator.ClampDuration(9999),
            WindowSummonAnimator.MaxDurationMs);
        CheckEqual("设置：170 原样保留", WindowSummonAnimator.ClampDuration(170), 170);

        var defaults = DefaultSettings.Create();
        Check("设置：动画默认是开的", defaults.Global.SummonAnimation);
        CheckEqual("设置：动画默认 170 毫秒", defaults.Global.SummonAnimationMs, 170);

        /* 关掉动画只该影响"播不播"，窗口切换本身必须照旧成功。
         * LaunchInterceptForTest 把"真去启动程序"换成计数桩：
         * 全程不碰任何窗口，也就不可能在自检里弹出东西。 */
        string exe = Path.Combine(Path.GetTempPath(), "ke-selftest-summon.exe");
        var prevHook = WindowBindingService.LaunchInterceptForTest;
        int launched = 0;
        try
        {
            File.WriteAllBytes(exe, Array.Empty<byte>());
            WindowBindingService.LaunchInterceptForTest = _ => { launched++; return true; };

            var on = DefaultSettings.Create();
            on.Global.SummonAnimation = true;
            var off = DefaultSettings.Create();
            off.Global.SummonAnimation = false;

            var svc = new WindowBindingService();
            Check("设置：关掉动画时切换窗口照样成功", svc.ActivateExe(exe, "selftest:summon", off));
            Check("设置：开着动画时切换窗口照样成功", svc.ActivateExe(exe, "selftest:summon", on));
            CheckEqual("设置：两种开关都走到了启动分支", launched, 2);
        }
        finally
        {
            WindowBindingService.LaunchInterceptForTest = prevHook;
            try { if (File.Exists(exe)) File.Delete(exe); } catch { }
        }
    }

    // ---------------- 小工具 ----------------

    private static KeyBinding? Find(AppSettings s, string combo)
        => s.LayerKeys(1).FirstOrDefault(k => k != null &&
            string.Equals(k.Combo, combo, StringComparison.OrdinalIgnoreCase));

    private static void Set(AppSettings s, string combo, string action, string arg)
    {
        var b = Find(s, combo);
        if (b == null) s.LayerKeys(1).Add(new KeyBinding { Combo = combo, Action = action, Arg = arg });
        else { b.Action = action; b.Arg = arg; }
    }

    /// <summary>第 1 层的可比较快照（按组合键排序，免得顺序差异盖住真实变化）。</summary>
    private static string Snapshot(AppSettings s) => string.Join(";",
        s.LayerKeys(1).Where(k => k != null).OrderBy(k => k.Combo, StringComparer.Ordinal)
            .Select(k => k.Combo + "=" + k.Action + "/" + k.Arg));
}
