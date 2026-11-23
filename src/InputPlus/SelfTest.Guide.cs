using InputPlus.Core.Actions;
using InputPlus.Core.Settings;

namespace InputPlus;

/// <summary>
/// 自检：「快速上手」页那一块「最常用的 8 个组合」。
///
/// 这一块以前是 XAML 后面写死的一张表，用户改了按键映射它不跟着变
/// （把 Q 换成「用谷歌搜索打开 Qbar」之后，页面上还写着「打开 Qbar」）。
/// 现在取数与显示名的规则抽在 <see cref="QuickKeyHints.Pick"/> 这个纯函数里，
/// 下面钉住三件事：
///   ① 用户改过的键一定出现，而且显示的是**改后的动作名**（不是写死的旧文案）；
///   ② 没有绑定 / 配置被清空时不抛异常，条目数永远 ≤ 8；
///   ③ 同一颗键不会在一格里重复出现（手改配置里同 combo 两条是常见情况）。
/// </summary>
public static partial class SelfTest
{
    private static void TestGuideQuickKeys()
    {
        var settings = DefaultSettings.Create();

        // 动作目录按真实设置建一份：显示名必须来自它，不能另抄一份文案
        var catalog = ActionCatalog.Create(new ActionContext
        {
            Settings = () => settings,
            Clipboard = new ClipboardService(),
            Windows = new WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new MouseService(),
            Notify = _ => { },
        });

        var layer1 = settings.Layers.First(l => l.Id == 1);

        // ---------------- ① 用户把 Q 改成「用谷歌搜索打开 Qbar」 ----------------
        //
        // 用户原话就是这个场景：改完按键，快速上手页还写着「打开 Qbar」。
        var q = layer1.Keys.First(k => string.Equals(k.Combo, "caps_q", StringComparison.OrdinalIgnoreCase));
        q.Action = "qbar:go";          // 动作目录里的「Qbar：Google」
        q.Arg = "";

        var google = catalog.Get("qbar:go");
        Check("前提：动作目录里有 Qbar：Google 这条动作", google != null);

        var hints = QuickKeyHints.Pick(
            layer1.Keys, catalog.Get, "Caps", QuickKeyHints.MaxCount,
            usage: null, defaults: DefaultSettings.CapsloxScheme());

        CheckEqual("快速上手：改过的键排在第一格", hints.FirstOrDefault()?.Key, "Q");
        CheckEqual("快速上手：第一格显示改后的动作名", hints.FirstOrDefault()?.Title, google?.Title ?? "");
        Check("快速上手：不再出现写死的旧文案",
            !hints.Any(h => h.Title.Contains("打开 Qbar", StringComparison.Ordinal)),
            string.Join(" / ", hints.Select(h => h.Title)));

        /* 显示名必须真的来自动作目录：把目录换成"什么都没有"，
         * 标题得跟着变成 ID 的可读形式 —— 变不了就说明文案是写死的。 */
        var noCatalog = QuickKeyHints.Pick(
            layer1.Keys, _ => null, "Caps", QuickKeyHints.MaxCount,
            usage: null, defaults: DefaultSettings.CapsloxScheme());
        Check("快速上手：动作名取自动作目录",
            !hints.Select(h => h.Title).SequenceEqual(noCatalog.Select(h => h.Title)),
            string.Join(" / ", noCatalog.Select(h => h.Title).Take(3)));

        // 同一份配置算两遍，顺序必须一模一样（否则用户会觉得这页在乱跳）
        var again = QuickKeyHints.Pick(
            layer1.Keys, catalog.Get, "Caps", QuickKeyHints.MaxCount,
            usage: null, defaults: DefaultSettings.CapsloxScheme());
        Check("快速上手：同一份配置两次算出来的顺序一致",
            hints.Select(h => h.Key + "=" + h.Title).SequenceEqual(again.Select(h => h.Key + "=" + h.Title)));

        // ---------------- ② 空集合 / 大集合都不出事 ----------------
        CheckEqual("快速上手：空绑定返回空表",
            QuickKeyHints.Pick(Array.Empty<KeyBinding>(), catalog.Get, "Caps").Count, 0);
        CheckEqual("快速上手：绑定集合为 null 不崩",
            QuickKeyHints.Pick(null, catalog.Get, "Caps").Count, 0);
        CheckEqual("快速上手：max=0 时返回空表",
            QuickKeyHints.Pick(layer1.Keys, catalog.Get, "Caps", 0).Count, 0);

        var full = QuickKeyHints.Pick(layer1.Keys, catalog.Get, "Caps");
        Check("快速上手：条目数不超过 8",
            full.Count <= QuickKeyHints.MaxCount, full.Count.ToString());
        Check("快速上手：默认配置能凑出条目", full.Count > 0, full.Count.ToString());
        Check("快速上手：传 999 条上限也还是 8",
            QuickKeyHints.Pick(layer1.Keys, catalog.Get, "Caps", 999).Count <= QuickKeyHints.MaxCount);

        /* 默认方案（没改过任何键）下排在第一位的是 Qbar —— 出厂配置里
         * caps_q 就是它，用户第一次打开这一页看到的应该是招牌功能。 */
        var plain = QuickKeyHints.Pick(
            DefaultSettings.Create().LayerKeys(1), catalog.Get, "Caps", QuickKeyHints.MaxCount,
            usage: null, defaults: DefaultSettings.CapsloxScheme());
        CheckEqual("快速上手：默认配置第一格是 Qbar",
            plain.FirstOrDefault()?.Title, catalog.Get("qbar")?.Title ?? "");
        Check("快速上手：不把触发键本身当成组合",
            plain.All(h => h.Key != "CapsLock"), string.Join(",", plain.Select(h => h.Key)));

        // ---------------- ③ 同一颗键不重复出现 ----------------
        var messy = new List<KeyBinding>
        {
            new() { Combo = "caps_q", Action = "qbar" },
            new() { Combo = "caps_q", Action = "copy1" },      // 同 combo 的第二条：手改配置里很常见
            new() { Combo = "CAPS_Q", Action = "paste1" },     // 只是大小写不同，也是同一条
            new() { Combo = "caps_lalt_q", Action = "paste2" }, // 同键不同修饰键：是另一条，要保留
            new() { Combo = "caps_w", Action = "backspace" },
            new() { Combo = "caps_e", Action = "moveUp" },
        };
        var uniq = QuickKeyHints.Pick(
            messy, catalog.Get, "Caps", QuickKeyHints.MaxCount,
            usage: null, defaults: DefaultSettings.CapsloxScheme());
        Check("快速上手：同一个键帽文字不重复出现",
            uniq.Select(h => h.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == uniq.Count,
            string.Join(",", uniq.Select(h => h.Key)));
        CheckEqual("快速上手：同 combo 的多条只留一条",
            uniq.Count(h => string.Equals(h.Key, "Q", StringComparison.OrdinalIgnoreCase)), 1);
        Check("快速上手：带修饰键的同一颗键是另一条",
            uniq.Any(h => string.Equals(h.Key, "Alt+Q", StringComparison.OrdinalIgnoreCase)),
            string.Join(",", uniq.Select(h => h.Key)));

        /* 同一个动作绑在两颗键上：默认方案里就有（Caps+V 与 Caps+Shift+V 都是
         * 「粘贴剪贴板 1」）。八格里重复同一个功能等于浪费一格，只留最顺手的那颗。 */
        var twoKeysOneAction = new List<KeyBinding>
        {
            new() { Combo = "caps_v", Action = "paste1" },
            new() { Combo = "caps_shift_v", Action = "paste1" },
        };
        var dedup = QuickKeyHints.Pick(
            twoKeysOneAction, catalog.Get, "Caps", QuickKeyHints.MaxCount,
            usage: null, defaults: DefaultSettings.CapsloxScheme());
        CheckEqual("快速上手：同一个动作只占一格", dedup.Count, 1);
        CheckEqual("快速上手：同一动作留最顺手的那颗键", dedup.FirstOrDefault()?.Key, "V");

        // 动作目录里查不到的动作（旧版本写下的 ID / 手改坏的 ID）退回可读形式，不能显示成一串机器 ID
        var unknown = QuickKeyHints.Pick(
            new List<KeyBinding> { new() { Combo = "caps_f9", Action = "qbar:something-new" } },
            _ => null, "Caps");
        CheckEqual("快速上手：查不到动作时退回可读 ID", unknown.FirstOrDefault()?.Title, "something-new");

        // ---------------- ④ 触发键名跟着配置走 ----------------
        var triggerSettings = DefaultSettings.Create();
        CheckEqual("快速上手：触发键是 CapsLock 时键帽写 Caps",
            UI.Views.GuideView.TriggerLabelOf(triggerSettings), "Caps");

        triggerSettings.Triggers.First(t => t.LayerId == 1).Key = "f13";
        CheckEqual("快速上手：触发键换成 F13 后键帽跟着变",
            UI.Views.GuideView.TriggerLabelOf(triggerSettings), "F13");

        var f13Hints = QuickKeyHints.Pick(
            triggerSettings.LayerKeys(1), catalog.Get, UI.Views.GuideView.TriggerLabelOf(triggerSettings),
            QuickKeyHints.MaxCount, usage: null, defaults: DefaultSettings.CapsloxScheme());
        Check("快速上手：每一格的触发键都是当前触发键",
            f13Hints.Count > 0 && f13Hints.All(h => h.Trigger == "F13"),
            string.Join(",", f13Hints.Select(h => h.Trigger).Distinct()));

        /* 触发键全被删掉时退回 Caps：这一格不能空着，
         * 也不能因为 FirstOrDefault 返回 null 就抛异常。 */
        triggerSettings.Triggers.Clear();
        CheckEqual("快速上手：没有触发键时退回 Caps",
            UI.Views.GuideView.TriggerLabelOf(triggerSettings), "Caps");
    }

    /// <summary>
    /// 自检：顶部横幅那两枚键帽。
    ///
    /// 以前写死 `Caps + Q`：用户把 Q 改绑成「用谷歌搜索打开 Qbar」、或者把键换到别处，
    /// 横幅照旧写着 Q —— 快速上手是第一次打开就看的页面，写死最伤。
    /// 取数规则在 <see cref="QuickKeyHints.FindKeycap"/>，这里钉四种情况。
    /// </summary>
    private static void TestGuideBannerKeycaps()
    {
        var defaults = DefaultSettings.CapsloxScheme();

        string? q = QuickKeyHints.FindKeycap(
            defaults, a => string.Equals(a, "qbar", StringComparison.OrdinalIgnoreCase));
        CheckEqual("快速上手横幅：出厂配置里 Qbar 绑在 Q 上", q ?? "", "Q");

        /* 用户把 Q 改成"用谷歌搜索打开 Qbar"（action 变成 qbar:xxx）：
         * 第一层（精确 qbar）找不到，第二层（qbar: 前缀）必须还认得出同一颗键。 */
        var changed = DefaultSettings.CapsloxScheme();
        var qBinding = changed.FirstOrDefault(b => (b.Action ?? "").Trim() == "qbar");
        if (qBinding != null) qBinding.Action = "qbar:go";

        string? changedKey = QuickKeyHints.FindKeycap(
                                 changed, a => string.Equals(a, "qbar", StringComparison.OrdinalIgnoreCase))
                             ?? QuickKeyHints.FindKeycap(
                                 changed, a => a.StartsWith("qbar:", StringComparison.OrdinalIgnoreCase));
        CheckEqual("快速上手横幅：改成 qbar:go 后还是同一颗键", changedKey ?? "", "Q");

        Check("快速上手横幅：完全没绑 Qbar 时返回 null（横幅要收掉第二枚键帽）",
            QuickKeyHints.FindKeycap(new List<KeyBinding>(), a => a == "qbar") == null);

        var withModifier = new List<KeyBinding> { new() { Combo = "caps_alt_q", Action = "qbar" } };
        CheckEqual("快速上手横幅：带修饰键时键帽写 Alt+Q",
            QuickKeyHints.FindKeycap(withModifier, a => a == "qbar") ?? "", "Alt+Q");

        /* 触发键本身不是组合键：把 qbar 绑在"按住触发键那一下"上（combo = press_caps），
         * 横幅不该把它显示成一颗普通键帽 —— 那颗键是进入功能层的开关。 */
        var triggerItself = new List<KeyBinding> { new() { Combo = "press_caps", Action = "qbar" } };
        Check("快速上手横幅：触发键本身不算组合",
            QuickKeyHints.FindKeycap(triggerItself, a => a == "qbar") == null);
    }
}
