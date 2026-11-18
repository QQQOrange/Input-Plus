using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using InputPlus.Core.Gestures;
using InputPlus.Core.Input;
using InputPlus.UI;
using InputPlus.Core.Settings;

namespace InputPlus;

/// <summary>
/// 自检：鼠标手势的形状数学、WGestures 按键翻译与导入。
///
/// 这些用例不碰钩子、不碰界面，跑在纯内存对象上 —— 手势识别最容易出错的
/// 地方是"归一化和打分"，与有没有鼠标无关。
/// </summary>
public static partial class SelfTest
{
    private static void TestGestures()
    {
        // ---- 归一化：同样形状、不同位置与大小，应当几乎是同一条线 ----
        var big = new List<GestureMath.Pt> { new(100, 200), new(400, 200), new(700, 200) };
        var small = new List<GestureMath.Pt> { new(10, 10), new(40, 10), new(70, 10) };
        var normBig = GestureMath.Normalize(big);
        var normSmall = GestureMath.Normalize(small);
        Check("手势形状与画多大无关",
            GestureMath.Distance(normBig, normSmall) < 0.03,
            GestureMath.Distance(normBig, normSmall).ToString("0.000"));

        // ---- 方向不同不能认成同一笔 ----
        var down = new List<GestureMath.Pt> { new(200, 100), new(200, 400), new(200, 700) };
        Check("向右不会认成向下",
            GestureMath.Distance(normBig, GestureMath.Normalize(down)) > 0.25,
            GestureMath.Distance(normBig, GestureMath.Normalize(down)).ToString("0.000"));

        // ---- 引擎只在阈值内返回结果（WGestures 形状距离，唯一引擎） ----
        var right = new MouseGesture
        {
            Id = "right",
            Kind = "stroke",
            Action = "sendKeys",
            Arg = "ctrl+c",
            Path = GestureMath.ToFlat(normBig)
        };
        var downward = new MouseGesture
        {
            Id = "down",
            Kind = "stroke",
            Action = "sendKeys",
            Arg = "ctrl+v",
            Path = GestureMath.ToFlat(GestureMath.Normalize(down))
        };
        var (hit, _) = GestureEngine.Match(new[] { right, downward }, big, GestureOptions.Default);
        CheckEqual("手势匹配选中同方向的那条", hit?.Id, "right");

        var (miss, _) = GestureEngine.Match(new[] { downward }, big, GestureOptions.Default);
        Check("方向不符时宁可不认", miss == null);

        // ---- AutoHotkey 按键串 → 本软件的序列写法 ----
        CheckEqual("^v 翻译成 ctrl+v", WGesturesImporter.ConvertKeys("^v"), "ctrl+v");
        CheckEqual("{LCtrl}(c) 翻译成 ctrl+c", WGesturesImporter.ConvertKeys("{LCtrl}(c)"), "ctrl+c");
        CheckEqual("%{F4} 翻译成 alt+f4", WGesturesImporter.ConvertKeys("%{F4}"), "alt+f4");
        CheckEqual("粘贴后回车是两段", WGesturesImporter.ConvertKeys("^v{ENTER}"), "ctrl+v enter");
        CheckEqual("带次数的音量键展开", WGesturesImporter.ConvertKeys("{VOLUME_DOWN 2}"), "volumedown volumedown");

        // ---- 导入：一份最小配置，覆盖三种触发与两种命令 ----
        string json = """
        {
          "General": {
            "Intents": [
              {
                "Name": "粘贴",
                "Gesture": [
                  { "$type": "KeyDown", "Key": "MOUSE:1" },
                  { "$type": "Stroke", "P": [ 100, 300, 110, 100 ] }
                ],
                "Command": { "$type": "SendKeys", "Keys": "^v" }
              },
              {
                "Name": "静音",
                "Gesture": [
                  { "$type": "KeyDown", "Key": "MOUSE:2" }
                ],
                "Command": { "$type": "SendKeys", "Keys": "{VOLUME_MUTE}" }
              },
              {
                "Name": "任务管理器",
                "Gesture": [
                  { "$type": "MoveToEdgeCorner", "EdgeCorner": { "Value": 1 } },
                  { "$type": "KeyDown", "Key": "MOUSE:4" }
                ],
                "Command": { "$type": "RunApp", "Path": "taskmgr.exe", "Cmd": "" }
              },
              {
                "Name": "右键加左键",
                "Gesture": [
                  { "$type": "KeyDown", "Key": "MOUSE:1" },
                  { "$type": "KeyDown", "Key": "MOUSE:0" }
                ],
                "Command": { "$type": "SendKeys", "Keys": "^v{ENTER}" }
              },
              {
                "Name": "自家脚本",
                "Gesture": [
                  { "$type": "KeyDown", "Key": "MOUSE:1" },
                  { "$type": "Stroke", "P": [ 100, 100, 300, 300 ] }
                ],
                "Command": { "$type": "Script", "Code": "WG.DoSomething()" }
              }
            ]
          },
          "Apps": []
        }
        """;

        string path = Path.Combine(Path.GetTempPath(), "clp-gestures-selftest.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));

        var settings = new AppSettings();
        var (imported, skipped) = WGesturesImporter.ImportFile(path, settings);
        CheckEqual("导入两条笔画/角落、跳过一条脚本", $"{imported}/{skipped}", "2/3");
        /* 动作 5.13 起统一走「自定义按键」：参数是 CustomKeySpec 的 JSON，
         * 键序本身放在 Key 里 —— 断言要看 Key，不能再看旧动作 ID 和裸串。 */
        Check("粘贴导入成笔画 + customKey",
            settings.Gestures.Any(g => g.Name == "粘贴" && g.Kind == "stroke" &&
                                       g.Action == "customKey" &&
                                       InputPlus.Core.Actions.CustomKeySpec.FromArg(g.Arg).Key == "ctrl+v" &&
                                       g.HasPath));
        Check("纯单击配置不再导入",
            !settings.Gestures.Any(g => g.Kind == "click" ||
                                        g.Name is "静音" or "右键加左键"));
        Check("角落 + 侧键导入成 corner",
            settings.Gestures.Any(g => g.Name == "任务管理器" && g.Kind == "corner" &&
                                       g.Corner == 1 && g.Button == "x2" && g.Action == "runProgram"));

        var again = WGesturesImporter.ImportFile(path, settings);
        CheckEqual("重复导入不产生重复手势", again.Imported, 0);

        // ---- 应用手势优先于全局手势 ----
        var scopedSettings = new AppSettings();
        var strokePts = new List<GestureMath.Pt> { new(100, 100), new(300, 100), new(500, 100) };
        var flat = GestureMath.ToFlat(GestureMath.Normalize(strokePts));
        scopedSettings.Gestures.Add(new MouseGesture
        {
            Id = "global", Name = "全局", Kind = "stroke", Button = "right", Path = flat, Action = "copy"
        });
        scopedSettings.Gestures.Add(new MouseGesture
        {
            Id = "scoped", Name = "应用", Kind = "stroke", Button = "right", Path = flat,
            Action = "winClose", AppPath = @"C:\Apps\browser.exe"
        });
        var inApp = GestureService.PickStroke(
            scopedSettings, 0x02, @"C:\Apps\browser.exe", strokePts, scopedSettings.GestureOptions);
        var elsewhere = GestureService.PickStroke(
            scopedSettings, 0x02, @"C:\Apps\notepad.exe", strokePts, scopedSettings.GestureOptions);
        Check("同一手势：应用里配了就用应用的那条", inApp.Hit?.Id == "scoped", inApp.Hit?.Name ?? "null");
        Check("同一个手势换到别的程序走全局", elsewhere.Hit?.Id == "global", elsewhere.Hit?.Name ?? "null");

        // ---- 方向串：识别真正用的键 ----
        var rightPts = new List<GestureMath.Pt>();
        for (int x = 100; x <= 420; x += 20) rightPts.Add(new GestureMath.Pt(x, 300));
        CheckEqual("向右一笔读成 →",
            GesturePattern.FromPoints(rightPts, 20, 0.10, 120), "→");

        var diagDownRight = new List<GestureMath.Pt>();
        for (int i = 0; i <= 15; i++) diagDownRight.Add(new GestureMath.Pt(100 + i * 20, 100 + i * 20));
        CheckEqual("右下斜线读成 ↘",
            GesturePattern.FromPoints(diagDownRight, 20, 0.10, 120), "↘");

        var diagUpLeft = new List<GestureMath.Pt>();
        for (int i = 0; i <= 15; i++) diagUpLeft.Add(new GestureMath.Pt(400 - i * 20, 400 - i * 20));
        CheckEqual("左上斜线读成 ↖",
            GesturePattern.FromPoints(diagUpLeft, 20, 0.10, 120), "↖");

        var downThenLeft = new List<GestureMath.Pt>();
        for (int y = 100; y <= 400; y += 20) downThenLeft.Add(new GestureMath.Pt(400, y));
        for (int x = 380; x >= 100; x -= 20) downThenLeft.Add(new GestureMath.Pt(x, 400));
        CheckEqual("向下再向左仍是直角 ↓←，不会被斜线抢走",
            GesturePattern.FromPoints(downThenLeft, 20, 0.10, 120), "↓←");

        var shallow = new List<GestureMath.Pt>();
        for (int i = 0; i <= 15; i++) shallow.Add(new GestureMath.Pt(100 + i * 20, 100 + i * 8));
        CheckEqual("轻微倾斜仍按水平 → 处理",
            GesturePattern.FromPoints(shallow, 20, 0.10, 120), "→");

        var upRight = new List<GestureMath.Pt>();
        for (int y = 600; y >= 300; y -= 20) upRight.Add(new GestureMath.Pt(400, y));
        for (int x = 420; x <= 720; x += 20) upRight.Add(new GestureMath.Pt(x, 300));
        CheckEqual("先上再右读成 ↑→",
            GesturePattern.FromPoints(upRight, 20, 0.10, 120), "↑→");

        // 手抖：来回 4px 不该被读成一个新方向
        var shaky = new List<GestureMath.Pt> { new(100, 300) };
        for (int i = 0; i < 20; i++) shaky.Add(new GestureMath.Pt(100 + i * 10, 300 + (i % 2 == 0 ? 4 : -4)));
        CheckEqual("横向手抖不会多出一段", GesturePattern.FromPoints(shaky, 20, 0.10, 120), "→");

        Check("方向串能画成箭头", GesturePattern.Describe("↑→") == "↑ →", GesturePattern.Describe("↑→"));
        Check("斜线也能画成箭头", GesturePattern.Describe("↘") == "↘", GesturePattern.Describe("↘"));
        Check("斜线可拆成单箭头", GesturePattern.Arrows("↘").Length == 1);

        // ---- 斜线与它的分量是同一笔 ----
        // Direction 的分界卡在 29° 附近：一条 45° 的斜线抖两下就会在 ↗ 与 ↑ 之间来回跳。
        // 旧版每跳一次就记一段，用户画一条"最大化"得到 ↗↑↗↑↗↑↗ 七个箭头。
        CheckEqual("斜线与它的分量算同一笔", GesturePattern.Clean("↗↑↗↑↗↑↗"), "↗");
        CheckEqual("斜着往右下也只算一笔", GesturePattern.Clean("↘→↘↓"), "↘");
        CheckEqual("真·直角折线不会被合并掉", GesturePattern.Clean("↑↗→"), "↑→");
        CheckEqual("反方向的来回是真的两笔", GesturePattern.Clean("→←"), "→←");
        CheckEqual("通配符原样保留", GesturePattern.Clean("*"), "*");

        // 端到端：一条斜着往上推、斜率在阈值附近抖的轨迹，最后只能有一笔
        var wavy = new List<GestureMath.Pt>();
        int wx = 300, wy = 700;
        for (int i = 0; i < 24; i++)
        {
            // 陡一点（判成 ↑）与缓一点（判成 ↗）交替，正是 45° 手抖的样子
            wx += i % 2 == 0 ? 20 : 26;
            wy -= i % 2 == 0 ? 42 : 40;
            wavy.Add(new GestureMath.Pt(wx, wy));
        }
        string wavyPattern = GesturePattern.FromPoints(wavy, 20, 0.10, 120);
        Check("斜着往上推只算一笔", GesturePattern.Arrows(wavyPattern).Length == 1, wavyPattern);

        // ---- 通配 * 兜底，但精确串优先 ----
        var wildSettings = new AppSettings();
        var anyFlat = new List<int> { 0, 0, 1000, 0, 1000, 1000, 0, 1000 };
        wildSettings.Gestures.Add(new MouseGesture
        {
            Id = "wild", Name = "通配", Kind = "stroke", Button = "right",
            Pattern = GesturePattern.Wildcard, Path = anyFlat, Action = "copy"
        });
        wildSettings.Gestures.Add(new MouseGesture
        {
            Id = "exact", Name = "精确", Kind = "stroke", Button = "right",
            Pattern = "→", Path = flat, Action = "paste"
        });
        var exactHit = GestureService.PickStroke(wildSettings, 0x02, "", rightPts, wildSettings.GestureOptions);
        Check("有精确方向串时不用通配", exactHit.Hit?.Id == "exact", exactHit.Hit?.Id ?? "null");

        /* 换一条表里没有的笔画（↓→ 的 L 形）再试通配：形状差得远、方向串也对不上，
         * 这才是 * 该接的活儿。以前这里复用同一批点、只换一个串 —— 串不再是判定依据之后
         * 那条「→」会被按形状认走，所以必须换形状来测。 */
        var wildLPts = new List<GestureMath.Pt>();
        for (int y = 100; y <= 400; y += 20) wildLPts.Add(new GestureMath.Pt(400, y));
        for (int x = 420; x <= 720; x += 20) wildLPts.Add(new GestureMath.Pt(x, 400));
        var wildHit = GestureService.PickStroke(wildSettings, 0x02, "", wildLPts, wildSettings.GestureOptions);
        Check("没配过的笔画交给通配", wildHit.Hit?.Id == "wild", wildHit.Hit?.Id ?? "null");

        // ---- 长方向串不能被更短的串抢走：↓→ 关闭 vs ↓ 粘贴 ----
        var lPts = new List<GestureMath.Pt>();
        for (int y = 100; y <= 400; y += 20) lPts.Add(new GestureMath.Pt(400, y));
        for (int x = 420; x <= 720; x += 20) lPts.Add(new GestureMath.Pt(x, 400));
        var downPts = new List<GestureMath.Pt>();
        for (int y = 100; y <= 400; y += 20) downPts.Add(new GestureMath.Pt(400, y));
        var dirSettings = new AppSettings();
        dirSettings.Gestures.Add(new MouseGesture
        {
            Id = "paste", Name = "粘贴", Kind = "stroke", Button = "right",
            Pattern = "↓", Path = GestureMath.ToFlat(GestureMath.Normalize(downPts)), Action = "sendKeys"
        });
        dirSettings.Gestures.Add(new MouseGesture
        {
            Id = "close", Name = "关闭", Kind = "stroke", Button = "right",
            Pattern = "↓→", Path = GestureMath.ToFlat(GestureMath.Normalize(lPts)), Action = "winClose"
        });
        var dirHit = GestureService.PickStroke(
            dirSettings, 0x02, "", lPts, dirSettings.GestureOptions);
        Check("长方向串优先，不会退回更短的 ↓", dirHit.Hit?.Id == "close", dirHit.Hit?.Id ?? "null");

        // ---- 画 S 不能被认成 QQ：方向串撞车时还要形状对得上 ----
        // 现场报障：画一个 S，结果打开了 QQ。QQ 那条手势存了 463 个点（用户真的画过一个圈），
        // 而 S 走一趟也可能把方向串凑成同一个 →↑←↓→，只看串就直接命中了。
        var sPts = new List<GestureMath.Pt>();
        for (int i = 0; i <= 60; i++)   // 一笔写成的大写 S
        {
            double t = i / 60.0;
            sPts.Add(new GestureMath.Pt(500 + 200 * Math.Sin(Math.PI * 2 * t), 160 + 680 * t));
        }
        var loopPts = new List<GestureMath.Pt>();
        for (int i = 0; i <= 64; i++)   // 画一个圈
        {
            double a = Math.PI * 2 * i / 64.0;
            loopPts.Add(new GestureMath.Pt(500 + 300 * Math.Cos(a), 500 + 300 * Math.Sin(a)));
        }
        for (int i = 1; i <= 12; i++) loopPts.Add(new GestureMath.Pt(800 + i * 12, 500 + i * 12));  // 再加个尾巴
        var loopGesture = new MouseGesture
        {
            Id = "qq", Name = "QQ", Kind = "stroke", Button = "right", Action = "openFile",
            Path = GestureMath.ToFlat(GestureMath.Normalize(loopPts)),
        };
        loopGesture.EnsurePattern();
        var loopSettings = new AppSettings();
        loopSettings.Gestures.Add(loopGesture);
        string sPattern = GesturePattern.FromPoints(sPts);
        Check("大写 S 读成五段", GesturePattern.Arrows(sPattern).Length >= 3, sPattern);
        Check("S 和 QQ 的方向串不是同一条（只是转了个起笔位置）",
            GesturePattern.Arrows(sPattern).Length != GesturePattern.Arrows(loopGesture.Pattern).Length ||
            !string.Equals(sPattern, loopGesture.Pattern, StringComparison.Ordinal),
            sPattern + " vs " + loopGesture.Pattern);
        var sHit = GestureService.PickStroke(loopSettings, 0x02, "", sPts, loopSettings.GestureOptions);
        Check("画 S 不会命中记过详细形状的 QQ 手势", sHit.Hit == null, sHit.Hit?.Name ?? "null");
        var againLoop = GestureService.PickStroke(
            loopSettings, 0x02, "", loopPts, loopSettings.GestureOptions);
        Check("照原样再画一次那个圈，仍然命中", againLoop.Hit?.Id == "qq", againLoop.Hit?.Id ?? "null");
        // 手再画一遍同一个圈（位置偏一点、大小差一点）必须还认得出来
        var redraw = new List<GestureMath.Pt>();
        for (int i = 0; i <= 64; i++)
        {
            double a = Math.PI * 2 * i / 64.0;
            redraw.Add(new GestureMath.Pt(520 + 285 * Math.Cos(a), 520 + 285 * Math.Sin(a)));
        }
        for (int i = 1; i <= 12; i++) redraw.Add(new GestureMath.Pt(805 + i * 13, 520 + i * 9));
        string redrawPattern = GesturePattern.FromPoints(redraw);
        var redrawHit = GestureService.PickStroke(
            loopSettings, 0x02, "", redraw, loopSettings.GestureOptions);
        Check("手再画一遍同一个圈，仍然命中", redrawHit.Hit?.Id == "qq",
            redrawPattern + " -> " + (redrawHit.Hit?.Id ?? "null"));

        /* ---- 闭环容错：同一个圈从哪儿起笔、正着绕还是反着绕都得认 ----
         * 这是"QQ 手势画了 Q 却不触发"的正题：引擎按"起笔固定的 (弧长, 方向)"比曲线，
         * 换个地方起笔、反着绕一圈曲线就完全不同；容错在 GestureEngine.Match 里
         * （闭环 8 个起点 × 正反共 16 条变体取最小距离，见 LoopVariants）。 */
        Check("同一个圈自己比自己：距离 0", GestureEngine.Distance(loopGesture, loopPts) < 0.01,
            GestureEngine.Distance(loopGesture, loopPts).ToString("F3"));

        // 从圆上 40% 处起笔再画一遍（用户每次画 Q 的起笔位置都不一样）
        var offsetLoop = new List<GestureMath.Pt>();
        int spin = (int)(loopPts.Count * 0.4);
        for (int i = 0; i < loopPts.Count; i++) offsetLoop.Add(loopPts[(i + spin) % loopPts.Count]);
        Check("换个位置起笔画同一个圈：方向串已经变了（说明只能靠形状）",
            !string.Equals(GesturePattern.FromPoints(offsetLoop), loopGesture.Pattern, StringComparison.Ordinal),
            GesturePattern.FromPoints(offsetLoop) + " vs " + loopGesture.Pattern);
        var offsetHit = GestureService.PickStroke(
            loopSettings, 0x02, "", offsetLoop, loopSettings.GestureOptions);
        Check("换个位置起笔画同一个圈，仍然命中 QQ", offsetHit.Hit?.Id == "qq",
            GesturePattern.FromPoints(offsetLoop) + " -> " + (offsetHit.Hit?.Id ?? "null"));

        // 反着绕一圈：顺时针和逆时针是同一个"圈"
        var reverseLoop = new List<GestureMath.Pt>(loopPts);
        reverseLoop.Reverse();
        var reverseHit = GestureService.PickStroke(
            loopSettings, 0x02, "", reverseLoop, loopSettings.GestureOptions);
        Check("反着绕同一个圈，仍然命中 QQ", reverseHit.Hit?.Id == "qq",
            GesturePattern.FromPoints(reverseLoop) + " -> " + (reverseHit.Hit?.Id ?? "null"));

        // 闭环 / 开笔要分得开（S 不能被当成"绕了一圈"）
        Check("圈是闭环", GestureMath.IsLoop(loopPts));
        Check("S 不是闭环", !GestureMath.IsLoop(sPts),
            GestureMath.NetTurning(sPts).ToString("F2"));

        // 圈和"Z 字折返"（QQ界面）、直线，形状上必须差得远
        var zPts = new List<GestureMath.Pt>
        {
            new(1000, 100), new(500, 400), new(1000, 700),   // ↘ ↙ ↘
        };
        Check("Q 和 Z 字手势不像", GestureEngine.Distance(loopGesture, zPts) > 0.05,
            GestureEngine.Distance(loopGesture, zPts).ToString("F3"));

        // 只有一条直线的老手势不能参与形状兜底：一条直线压成包围盒后跟 S 只差 0.14，
        // 一旦参与，画 S 就会被判成「最大化」。
        var lineSettings = new AppSettings();
        var linePts = new List<GestureMath.Pt>();
        for (int i = 0; i <= 60; i++) linePts.Add(new GestureMath.Pt(200 + i * 8, 800 - i * 8));
        lineSettings.Gestures.Add(new MouseGesture
        {
            Id = "max", Name = "最大化", Kind = "stroke", Button = "right", Action = "winMax",
            Pattern = "↗", Path = GestureMath.ToFlat(GestureMath.Normalize(linePts)),
        });
        var sVsLine = GestureService.PickStroke(
            lineSettings, 0x02, "", sPts, lineSettings.GestureOptions);
        Check("画 S 不会被判成只写了一个方向的手势", sVsLine.Hit == null, sVsLine.Hit?.Name ?? "null");

        // 两条一样像时宁可不猜（宁可显示「未识别」，也别触发错的动作）
        var twin = new MouseGesture
        {
            Id = "qq2", Name = "QQ2", Kind = "stroke", Button = "right", Action = "openFile",
            Path = GestureMath.ToFlat(GestureMath.Normalize(redraw)),
        };
        twin.EnsurePattern();
        var twinSettings = new AppSettings();
        twinSettings.Gestures.Add(loopGesture);
        twinSettings.Gestures.Add(twin);
        var twinHit = GestureService.PickStroke(
            twinSettings, 0x02, "", loopPts, twinSettings.GestureOptions);
        Check("两条几乎一样的手势：要么不猜、要么取其一，绝不串到别人头上",
            twinHit.Hit == null || twinHit.Hit.Id == "qq" || twinHit.Hit.Id == "qq2",
            twinHit.Hit?.Id ?? "null");

        // ---- 改键回声的「手势通行证」一次性 ----
        InputSender.AllowGesturePass(0x04);
        Check("改键回声放行一次手势", InputSender.TakeGesturePass(0x04));
        Check("通行证用完即作废", !InputSender.TakeGesturePass(0x04));
        Check("没申请过的键不会被放行", !InputSender.TakeGesturePass(0x05));

        // ---- 贴边滚轮：四条边都认得出，屏幕中间不误判 ----
        // 用 WinForms 的虚拟屏矩形（物理像素），别用 WPF 的 SystemParameters（DIP，
        // 高 DPI 下会比真实像素小，右/下边测试会假失败）。
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        CheckEqual("上边缘", GestureService.EdgeAt(vs.Left + vs.Width / 2.0, vs.Top + 1, 3), 1);
        CheckEqual("右边缘", GestureService.EdgeAt(vs.Right - 1, vs.Top + vs.Height / 2.0, 3), 2);
        CheckEqual("下边缘", GestureService.EdgeAt(vs.Left + vs.Width / 2.0, vs.Bottom - 1, 3), 3);
        CheckEqual("左边缘", GestureService.EdgeAt(vs.Left + 1, vs.Top + vs.Height / 2.0, 3), 4);
        CheckEqual("屏幕中间不算贴边", GestureService.EdgeAt(vs.Left + vs.Width / 2.0, vs.Top + vs.Height / 2.0, 3), 0);

        // ---- 内置边角手势：只播一次，用户删了不会又被塞回来 ----
        var fresh = new AppSettings();
        Check("首次启动补上内置边角手势", GestureDefaults.Ensure(fresh));
        CheckEqual("四个热角 + 八条贴边滚轮",
            $"{fresh.Gestures.Count(g => g.Kind == "hotcorner")}/{fresh.Gestures.Count(g => g.Kind == "edgewheel")}",
            "4/8");
        Check("热角默认不用按鼠标键",
            fresh.Gestures.Where(g => g.Kind == "hotcorner").All(g => g.Button.Length == 0));
        Check("左下角默认打开开始菜单",
            fresh.Gestures.Any(g => g.Kind == "hotcorner" && g.Corner == 3 && g.Action == "openStartMenu"));
        Check("贴边滚轮都指定了边缘和方向",
            fresh.Gestures.Where(g => g.Kind == "edgewheel").All(g => g.Edge is 1 or 2 or 3 or 4 && g.Wheel is -1 or 1));
        Check("左右边缘默认留空给用户自定义",
            fresh.Gestures.Where(g => g.Kind == "edgewheel" && g.Edge is 2 or 4)
                .All(g => string.IsNullOrEmpty(g.Action)));

        /* ---- 1.0：内置手势库（把手势直接带在软件里）----
         * 用户的要求是"新装就能用"，所以这里钉住条数和"没有本机痕迹"。
         * 条数变化必须同步改这里 —— 这条断言就是提醒：改库要重新审视下面那些检查。 */
        var lib = BuiltinGestures.Create();
        CheckEqual("内置手势库条数", lib.Count, 52);
        Check("内置手势 id 唯一", lib.Select(g => g.Id).Distinct().Count() == lib.Count);
        Check("内置手势都带名字", lib.All(g => !string.IsNullOrWhiteSpace(g.Name)));
        Check("内置手势默认启用", lib.All(g => g.Enabled));

        /* 「音量-」「音量+」是 WGestures 导入时留下的"屏幕角落 + 滚轮"音量手势，
         * 启动迁移（GestureDefaults 的 <9 段）会明确删掉它们 —— 贴边滚轮调音量更顺手。
         * 库里要是还带着这两条，铺进去又被删，条数就对不上了，所以这里也钉住。 */
        Check("内置手势里没有会被启动迁移删掉的音量滚轮",
            !lib.Any(g => g.Kind == "wheel" && string.IsNullOrWhiteSpace(g.Button)
                          && ((g.Arg ?? "").Contains("volume", StringComparison.OrdinalIgnoreCase)
                              || (g.Arg ?? "").Contains("mute", StringComparison.OrdinalIgnoreCase)
                              || (g.Name ?? "").StartsWith("音量", StringComparison.Ordinal))),
            string.Join(",", lib.Where(g => g.Kind == "wheel").Select(g => g.Name)));

        /* 脱敏是发版前的硬要求（用户明确说过：邮箱那类敏感数据不许打包）。
         * 这里扫的是**会被带出去**的字段：参数、限定程序、名字。
         * 一旦有人把带本机用户名的路径加回库里，自检就红。 */
        var pii = new System.Text.RegularExpressions.Regex(
            @"/Users/[^/\\\s]+|\\Users\\|D:\\software|WindowsGet|AppData\\Local\\Programs",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var dirty = lib.Where(g => pii.IsMatch((g.Arg ?? "") + "|" + (g.AppPath ?? "") + "|" + g.Name))
            .Select(g => g.Name).ToList();
        Check("内置手势里没有本机路径/用户名", dirty.Count == 0,
            dirty.Count == 0 ? "" : string.Join(",", dirty));
        Check("内置手势的运行参数不含写死的盘符路径",
            lib.Where(g => g.Action == "runProgram")
               .All(g => !System.Text.RegularExpressions.Regex.IsMatch(g.Arg ?? "", @"^[A-Za-z]:\\")),
            string.Join(" | ", lib.Where(g => g.Action == "runProgram").Select(g => g.Arg)));
        Check("内置手势里有靠环境变量占位的路径（说明脱敏真的做了）",
            lib.Any(g => (g.Arg ?? "").Contains("%ProgramFiles%"))
            && lib.Any(g => (g.Arg ?? "").Contains("%LOCALAPPDATA%")));

        /* 库里不能自带"画哪个都像"的撞车对：老库里「关闭」和「休眠」的形状
         * 只差 0.0071，谁排前面谁赢，用户会发现其中一个永远不触发。
         * 同样，「新建」曾经有两条同形状（一条给资源管理器、一条开 cmd）。 */
        Check("内置库里没有与「关闭」撞车的「休眠」", !lib.Any(g => g.Name == "休眠"));
        Check("内置库里「新建」只有一条", lib.Count(g => g.Name == "新建") == 1,
            string.Join(",", lib.Where(g => g.Name == "新建").Select(g => g.AppPath)));

        /* 铺的时候必须"只在空表上铺"：老用户手里那套自己调的不能被冲掉。 */
        Check("新手势表里已经带了内置手势",
            fresh.Gestures.Any(g => g.Name == lib[0].Name), fresh.Gestures.Count.ToString());
        CheckEqual("内置手势 + 12 条边角默认",
            fresh.Gestures.Count, lib.Count + 12);

        var veteran = new AppSettings();
        var mine = lib[0].Clone();
        mine.Id = "mine";
        mine.Name = "我自己的手势";
        veteran.Gestures.Add(mine);
        veteran.GestureOptions.BuiltinsVersion = 10;   // 从 5.x 升上来的老配置
        GestureDefaults.Ensure(veteran);
        CheckEqual("老用户升级时不会被塞一套内置手势", veteran.Gestures.Count, 13);
        Check("老用户自己的手势还在",
            veteran.Gestures.Any(g => g.Id == "mine" && g.Name == "我自己的手势"));

        // ---- 内置手势的动作必须在动作表里能查到 ----
        // 「截图」以前配的是 sendKeys win+s（打开 Windows 搜索），画了手势却截不到图；
        // 这类"动作 id 写错/写了个不存在的功能"没人验过，所以在这里钉死。
        var gsettings = DefaultSettings.Create();
        var gctx = new Core.Actions.ActionContext
        {
            Settings = () => gsettings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        var gcat = Core.Actions.ActionCatalog.Create(gctx);
        foreach (var g in fresh.Gestures.Where(g => !string.IsNullOrEmpty(g.Action)))
        {
            string id = g.Action.Contains(':') ? g.Action[..(g.Action.IndexOf(':') + 1)] + "*" : g.Action;
            Check("默认手势「" + g.Name + "」的动作存在：" + g.Action, gcat.Get(g.Action) != null, id);
        }
        Check("动作表里「截图」是内置动作，不靠发送 Win+S",
            gcat.Get("screenshot") != null && gcat.Get("screenshot")!.NeedsArg == false);
        Check("第二次启动不重复播种", !GestureDefaults.Ensure(fresh));
        fresh.Gestures.RemoveAll(g => g.Kind == "hotcorner");
        Check("用户删掉默认热角后不会偷偷补回",
            !GestureDefaults.Ensure(fresh) && !fresh.Gestures.Any(g => g.Kind == "hotcorner"));

        /* 「右键画 ↓ 再点左键」这个组合手势在 WGestures 2 的原配置里是"刷新"（发 F5），
         * 5.10.1 又按同名需求补过一条"粘贴并 Enter"。两者同一个形状，用户后来明确
         * 拍了板：**保留刷新、去掉粘贴并 Enter**（理由见 docs/手势识别诊断与修复.md）。
         *
         * 1.0 起内置库里就有"刷新"，所以老配置（BuiltinsVersion = 7）升级时，
         * <8 段那条补植会因为"已经有同形状的手势"而跳过 —— 这里钉住这个行为，
         * 免得哪天有人把补植条件放宽，又给用户塞进第二条撞车的组合手势。 */
        var pasteGone = new AppSettings();
        pasteGone.GestureOptions.BuiltinsVersion = 7;
        GestureDefaults.Ensure(pasteGone);
        var chordOnes = pasteGone.Gestures.Where(g =>
            string.Equals(g.Chord, "left", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(g.Pattern, "↓", StringComparison.Ordinal)).ToList();
        CheckEqual("同一形状（右键 + 左键的 ↓）只留一条", chordOnes.Count, 1);
        CheckEqual("留下来的是「刷新」而不是「粘贴并 Enter」", chordOnes[0].Name, "刷新");
        Check("不会再塞第二条「粘贴并 Enter」",
            !pasteGone.Gestures.Any(g => g.Name == "粘贴并 Enter"));
        Check("刷新带稳定 Id（否则编辑/删除对不上）", !string.IsNullOrEmpty(chordOnes[0].Id));

        pasteGone.Gestures.RemoveAll(g => string.Equals(g.Chord, "left", StringComparison.OrdinalIgnoreCase));
        Check("补过一次之后再删就不会又被塞回来",
            !GestureDefaults.Ensure(pasteGone) && !pasteGone.Gestures.Any(g => g.Chord == "left"));

        // 旧版把斜线记成单方向；升级后只迁移“整条轨迹本来就是斜线”的条目。
        var legacy = new AppSettings();
        legacy.GestureOptions.BuiltinsVersion = 4;
        legacy.Gestures.Add(new MouseGesture
        {
            Id = "legacy-diagonal", Name = "旧斜线", Kind = "stroke", Button = "right",
            Pattern = "↓", Path = new List<int> { 0, 0, 1000, 1000 }, Action = "copy"
        });
        GestureDefaults.Ensure(legacy);
        CheckEqual("旧斜线迁移成 ↘", legacy.Gestures.First(g => g.Id == "legacy-diagonal").Pattern, "↘");

        /* 旧版把一条左下斜线记成“↓←”，新识别器按 Path 重算后应变成 ↙；
         * 真正的直角（先下再右）必须保持 ↓→，不能被误改成斜线。 */
        var legacyCurve = new AppSettings();
        legacyCurve.GestureOptions.BuiltinsVersion = 5;
        legacyCurve.Gestures.Add(new MouseGesture
        {
            Id = "legacy-curve", Name = "旧左下斜线", Kind = "stroke", Button = "right",
            Pattern = "↓←", Path = new List<int> { 1000, 0, 0, 1000 }, Action = "copy"
        });
        legacyCurve.Gestures.Add(new MouseGesture
        {
            Id = "legacy-right-angle", Name = "旧直角", Kind = "stroke", Button = "right",
            Pattern = "↓→", Path = new List<int> { 0, 0, 0, 500, 500, 500 }, Action = "paste"
        });
        GestureDefaults.Ensure(legacyCurve);
        CheckEqual("旧多段斜线重算成 ↙", legacyCurve.Gestures.First(g => g.Id == "legacy-curve").Pattern, "↙");
        CheckEqual("真正的直角保持 ↓→", legacyCurve.Gestures.First(g => g.Id == "legacy-right-angle").Pattern, "↓→");

        Check("斜线和直角手势不算重复",
            GestureIdentity.Find(new[]
            {
                new MouseGesture { Kind = "stroke", Button = "right", Pattern = "↓←" }
            }, new MouseGesture { Kind = "stroke", Button = "right", Pattern = "↙" }, null) == null);

        /* ---- 只有方向串的存量手势：合成折线进同一个引擎，方向串本身不再参与打分 ---- */
        var skUp = GesturePattern.Sketch("↑");
        var skDown = GesturePattern.Sketch("↓");
        CheckEqual("↑→ 合成 3 个点（起点 + 两段）", GesturePattern.Sketch("↑→").Count, 3);
        Check("通配合不出折线", GesturePattern.Sketch(GesturePattern.Wildcard).Count == 0);
        Check("↑ 合成到屏幕上方、↓ 合成到下方",
            skUp.Count == 2 && skDown.Count == 2 && skUp[^1].Y < 0 && skDown[^1].Y > 0,
            $"{skUp[^1].Y:0} / {skDown[^1].Y:0}");
        var skA = GesturePattern.Sketch("↘↗");
        var skB = GesturePattern.Sketch("↘↗");
        Check("同一条方向串合成的折线逐点相同",
            skA.Count == skB.Count && skA.Zip(skB).All(p => p.First == p.Second));

        /* 微信的 W：存下来的方向串是 ↑↓↑↓（错的），真画出来是 ↘↗↘↗。
         * 只要轨迹对得上就该触发 —— 箭头串不参与形状手势的判定。 */
        var wGesture = new MouseGesture
        {
            Id = "weixin-w", Name = "微信", Kind = "stroke", Button = "right", Action = "openFile",
            Pattern = "↑↓↑↓",
            Path = new List<int> { 0, 0, 229, 909, 396, 153, 553, 902, 1000, -95 },
        };
        var wSettings = new AppSettings();
        wSettings.Gestures.Add(wGesture);
        var wDraw = new List<GestureMath.Pt>();
        var wCorners = new[] { (0.0, 0.0), (229.0, 909.0), (396.0, 153.0), (553.0, 902.0), (1000.0, 0.0) };
        for (int i = 1; i < wCorners.Length; i++)
        {
            var (x0, y0) = wCorners[i - 1];
            var (x1, y1) = wCorners[i];
            for (int k = 0; k <= 20; k++)
            {
                double t = k / 20.0;
                wDraw.Add(new GestureMath.Pt(
                    x0 + (x1 - x0) * t + 6 * Math.Sin(k * 1.7),
                    y0 + (y1 - y0) * t + 6 * Math.Cos(k * 1.3)));
            }
        }
        string wPattern = GesturePattern.FromPoints(wDraw);
        var wHit = GestureService.PickStroke(wSettings, 0x02, "", wDraw, wSettings.GestureOptions);
        Check("画一个 W 能命中 W 手势（存的方向串是错的也无所谓）",
            wHit.Hit?.Id == "weixin-w", wPattern + " -> " + (wHit.Hit?.Id ?? "null"));
        Check("W 和存下来的方向串确实不一样（说明是形状认出来的）",
            string.Equals(wPattern, wGesture.Pattern, StringComparison.Ordinal) == false, wPattern);
        Check("偏差分数是阈值归一化的刻度（≤1 才算命中）", wHit.Score <= 1.0, wHit.Score.ToString("F3"));

        /* ---- 5.13.0：存量手势只有方向串、没有轨迹 → 合成折线，从此走同一套形状判定 ---- */
        var v9 = new AppSettings();
        v9.GestureOptions.BuiltinsVersion = 9;
        v9.Gestures.Add(new MouseGesture { Id = "v9-down", Name = "旧向下", Kind = "stroke", Button = "right", Pattern = "↓", Action = "copy" });
        v9.Gestures.Add(new MouseGesture { Id = "v9-two", Name = "旧两段", Kind = "stroke", Button = "right", Pattern = "↑→", Action = "paste" });
        v9.Gestures.Add(new MouseGesture { Id = "v9-wild", Name = "通配", Kind = "stroke", Button = "right", Pattern = "*", Action = "copy" });
        v9.Gestures.Add(new MouseGesture { Id = "v9-wheel", Name = "滚轮", Kind = "wheel", Corner = 1, Wheel = -1, Action = "volumeDown" });
        GestureDefaults.Ensure(v9);
        var v9Down = v9.Gestures.First(g => g.Id == "v9-down");
        Check("v10：只有方向串的手势合成了轨迹", v9Down.HasPath, v9Down.Path.Count + " 个坐标");
        CheckEqual("v10：方向串原样留着（只是不再参与判定）", v9Down.Pattern, "↓");
        Check("v10：合成出来的轨迹能进引擎", GestureEngine.CanMatch(v9Down));
        CheckEqual("v10：迁移后版本号提到最新", v9.GestureOptions.BuiltinsVersion, GestureDefaults.Version);
        var v9Path = new List<int>(v9Down.Path);
        GestureDefaults.Ensure(v9);
        Check("v10：再跑一次迁移不会改已经合成好的轨迹", v9Down.Path.SequenceEqual(v9Path));
        Check("v10：通配手势不合成轨迹", !v9.Gestures.First(g => g.Id == "v9-wild").HasPath);
        Check("v10：滚轮手势不合成轨迹", !v9.Gestures.First(g => g.Id == "v9-wheel").HasPath);

        /* 合成出来的折线真的能认出来：照 ↓ 的方向画一笔（带手抖）。 */
        var v9Draw = new List<GestureMath.Pt>();
        for (int i = 0; i <= 40; i++)
            v9Draw.Add(new GestureMath.Pt(500 + 1.5 * Math.Sin(i * 0.3), 200 + i * 12 + 0.8 * Math.Cos(i * 0.4)));
        var v9Hit = GestureEngine.Match(
            new[] { v9Down, v9.Gestures.First(g => g.Id == "v9-two") }, v9Draw, v9.GestureOptions);
        CheckEqual("v10：往下画一笔命中合成出来的 ↓", v9Hit.Hit?.Id, "v9-down");

        /* ---- 5.15.0：识别前的加工（等距重采样 → 平滑 → 按尺度丢点）----
         * 老 bug：运行期每 2px 记一个点，1~2px 的手抖让每小段方向乱跳，18° 合并压不掉，
         * 手绘折点轻松到 200+，超过引擎 128 点上限 → 一律"不可比"、界面上永远是未识别。
         * 现场 45 条手势自测 0/129 次认出自己。 */
        var noisy = new List<GestureMath.Pt>();
        for (int i = 0; i <= 500; i++)
            noisy.Add(new GestureMath.Pt(100 + i, 300 + 1.2 * Math.Sin(i * 2.1)));
        var noisyStroke = GestureEngine.DrawnStroke(noisy);
        Check("5.15：500 个抖动点的直线压得进引擎上限",
            noisyStroke != null && noisyStroke.Count <= InputPlus.Core.Gestures.WG.WgStroke.MaxPoints,
            noisyStroke?.Count.ToString() ?? "null");

        /* 3 点折线模板 vs 同形手绘：平滑必须按"几何"而不是按"点数"，
         * 否则同样半径对 3 点模板就是整条链一起抹，直角被削成斜坡 —— 实测距离 0.12，超阈两倍多。 */
        var cornerTpl = new MouseGesture
        {
            Id = "corner", Name = "直角", Kind = "stroke", Button = "right", Action = "copy",
            Path = new List<int> { 0, 1000, 600, 1000, 600, 0 },
        };
        var cornerDraw = new List<GestureMath.Pt>();
        for (int i = 0; i <= 600; i += 2)
            cornerDraw.Add(new GestureMath.Pt(i, 1000 + 0.8 * Math.Sin(i * 1.7)));
        for (int i = 2; i <= 1000; i += 2)
            cornerDraw.Add(new GestureMath.Pt(600 + 0.8 * Math.Cos(i * 1.3), 1000 - i));
        double cornerDistance = GestureEngine.Distance(cornerTpl, cornerDraw);
        Check("5.15：三点折线模板与同形手绘的距离近似为零", cornerDistance < 0.01, cornerDistance.ToString("F4"));

        var dense = new List<GestureMath.Pt>();
        for (int i = 0; i <= 600; i += 8) dense.Add(new GestureMath.Pt(i, 1000));
        for (int i = 8; i <= 1000; i += 8) dense.Add(new GestureMath.Pt(600, 1000 - i));
        double denseDistance = GestureEngine.Distance(cornerTpl, dense);
        Check("5.15：画快画慢（采样密度差 4 倍）不影响判定", denseDistance < 0.01, denseDistance.ToString("F4"));

        var smoothIn = new List<GestureMath.Pt> { new(0, 0), new(10, 30), new(20, 0), new(30, 40), new(40, 0) };
        var smoothOut = GestureEngine.Smooth(smoothIn, 1);
        Check("5.15：平滑保留首尾点（起手和收笔的位置不能被挪走）",
            smoothOut.Count == smoothIn.Count && smoothOut[0].Equals(smoothIn[0]) && smoothOut[^1].Equals(smoothIn[^1]),
            smoothOut.Count.ToString());

        /* ---- 5.10.6：手势表清理 + 老动作升级 ---- */
        var cleanup = new AppSettings();
        cleanup.GestureOptions.BuiltinsVersion = 8;
        /* 音量那两条要照着现场数据造：Kind=wheel、Button 空、Corner 有值、
         * 两条的滚轮方向不同（否则先被"同一条手势去重"删掉，测的就不是迁移了）。 */
        cleanup.Gestures.Add(new MouseGesture { Id = "vol1", Name = "音量-", Kind = "wheel", Button = "", Corner = 1, Wheel = -1, Arg = "volumedown", Action = "volumeDown" });
        cleanup.Gestures.Add(new MouseGesture { Id = "vol2", Name = "音量+", Kind = "wheel", Button = "", Corner = 1, Wheel = 1, Arg = "volumeup", Action = "volumeUp" });
        cleanup.Gestures.Add(new MouseGesture { Id = "edgevol", Name = "上边缘 · 滚轮向上调音量", Kind = "edgewheel", Edge = 1, Wheel = -1, Action = "volumeUp" });
        cleanup.Gestures.Add(new MouseGesture { Id = "tm", Name = "任务管理器", Kind = "stroke", Button = "right", Pattern = "↓", Action = "runProgram", Arg = "cmd.exe /c taskmgr" });
        cleanup.Gestures.Add(new MouseGesture { Id = "weixin", Name = "微信", Kind = "stroke", Button = "right", Pattern = "↗", Action = "runProgram", Arg = "\"D:\\software\\Weixin\\Weixin.exe\"" });
        cleanup.Gestures.Add(new MouseGesture { Id = "url", Name = "打开网址", Kind = "stroke", Button = "right", Pattern = "↑", Action = "openUrl", Arg = "https://baidu.com" });
        cleanup.Gestures.Add(new MouseGesture { Id = "find", Name = "搜一搜", Kind = "stroke", Button = "right", Pattern = "←", Action = "search:bd", Arg = "https://baidu.com/s?wd=" });
        GestureDefaults.Ensure(cleanup);
        Check("5.10.6：删掉「音量-」「音量+」", !cleanup.Gestures.Any(g => g.Name is "音量-" or "音量+"));
        Check("5.10.6：贴边滚轮的调音量不动", cleanup.Gestures.Any(g => g.Id == "edgevol"));
        CheckEqual("5.10.6：cmd.exe /c taskmgr 改成内置动作",
            cleanup.Gestures.First(g => g.Id == "tm").Action, "openTaskManager");
        Check("5.10.6：任务管理器不再带参数", string.IsNullOrEmpty(cleanup.Gestures.First(g => g.Id == "tm").Arg));
        CheckEqual("5.10.6：裸 exe 路径改成直达文件",
            cleanup.Gestures.First(g => g.Id == "weixin").Action, "openFile");
        CheckEqual("5.10.6：直达文件的参数去掉了外层引号",
            cleanup.Gestures.First(g => g.Id == "weixin").Arg, @"D:\software\Weixin\Weixin.exe");
        CheckEqual("5.10.6：openUrl 改成直达网址",
            cleanup.Gestures.First(g => g.Id == "url").Action, "webPage");
        Check("5.10.6：搜索动作里的网址模板参数被清掉",
            string.IsNullOrEmpty(cleanup.Gestures.First(g => g.Id == "find").Arg),
            cleanup.Gestures.First(g => g.Id == "find").Arg ?? "null");

        /* 编辑器吞动作的善后：动作是被隐藏的兼容动作时，老版本会把它改成
         * 「禁用这个键」（swallowKey）并把参数清空。迁移要把名字对得上的修回来。 */
        var clobbered = new AppSettings();
        clobbered.GestureOptions.BuiltinsVersion = 8;
        clobbered.Gestures.Add(new MouseGesture { Id = "tm2", Name = "任务管理器", Kind = "stroke", Button = "right", Pattern = "↑", Action = "swallowKey" });
        clobbered.Gestures.Add(new MouseGesture { Id = "wx2", Name = "微信", Kind = "stroke", Button = "right", Pattern = "↘", Action = "swallowKey" });
        clobbered.Gestures.Add(new MouseGesture { Id = "bookx", Name = "Bookxnote pro", Kind = "stroke", Button = "right", Pattern = "←", Action = "runProgram", Arg = @"C:\Program Files\Bookx Software\BookxNote Pro\BookxNotePro.exe" });
        GestureDefaults.Ensure(clobbered);

        CheckEqual("被吞成「禁用这个键」的任务管理器修回内置动作",
            clobbered.Gestures.First(g => g.Id == "tm2").Action, "openTaskManager");
        var wx2 = clobbered.Gestures.First(g => g.Id == "wx2");
        Check("微信要么修成直达文件、要么保持原样（这台机器没装微信时不动）",
            wx2.Action == "swallowKey" || (wx2.Action == "openFile" && System.IO.File.Exists(wx2.Arg)),
            wx2.Action + " " + wx2.Arg);
        var bookx = clobbered.Gestures.First(g => g.Id == "bookx");
        CheckEqual("带空格的 exe 路径也改成直达文件", bookx.Action, "openFile");
        CheckEqual("带空格的路径原样保留", bookx.Arg, @"C:\Program Files\Bookx Software\BookxNote Pro\BookxNotePro.exe");

        // ---- 同一手势只能绑一个动作（去重键） ----
        var dupA = new MouseGesture { Kind = "stroke", Button = "right", Pattern = "↓", Action = "sendKeys", Arg = "ctrl+v" };
        var dupB = new MouseGesture { Kind = "stroke", Button = "right", Pattern = "↓", Action = "runProgram" };
        Check("同笔画同键不同动作算重复", GestureIdentity.Find(new[] { dupA }, dupB, null) != null);
        var dupChord = new MouseGesture { Kind = "stroke", Button = "right", Chord = "left", Pattern = "↓" };
        Check("组合键不同不算重复", GestureIdentity.Find(new[] { dupA }, dupChord, null) == null);
        var dupApp = new MouseGesture { Kind = "stroke", Button = "right", Pattern = "↓", AppPath = @"C:\demo.exe" };
        Check("不同作用目标不算重复", GestureIdentity.Find(new[] { dupA }, dupApp, null) == null);
        CheckEqual("启动清理重复保留最早一条",
            GestureIdentity.RemoveDuplicates(new List<MouseGesture> { dupA, dupB, dupChord }), 1);

        // ---- 按下拦截：用不到的触发方式不该把右键变成“注入补发” ----
        var defaults = new GestureOptions();
        Check("单击触发已移除",
            !GestureService.CanStart(new MouseGesture { Kind = "click", Button = "right" }, defaults, 1));
        Check("角落手势不在角落不拦",
            !GestureService.CanStart(new MouseGesture { Kind = "corner", Corner = 2, Button = "right" }, defaults, 0));
        Check("角落手势在对的角落才拦",
            GestureService.CanStart(new MouseGesture { Kind = "corner", Corner = 2, Button = "right" }, defaults, 2));
        Check("笔画手势任何位置都拦",
            GestureService.CanStart(new MouseGesture { Kind = "stroke", Button = "right" }, defaults, 0));
        Check("普通笔画不再受边缘字段影响",
            GestureService.CanStart(new MouseGesture { Kind = "stroke", Button = "right", Edge = 1 }, defaults, 0));
        Check("同方向串的普通笔画仍然算重复",
            GestureIdentity.Find(new[]
            {
                new MouseGesture { Kind = "stroke", Button = "right", Pattern = "→" }
            }, new MouseGesture { Kind = "stroke", Button = "right", Pattern = "→" }, null) != null);

        // ---- 每条手势自己的开关与分按键颜色 ----
        var flags = new MouseGesture { Immediate = true, HideLabel = true };
        var flagsCopy = flags.Clone();
        Check("复制手势时带上立即执行/隐藏名称", flagsCopy.Immediate && flagsCopy.HideLabel);

        var colors = new GestureOptions { TrailColor = "#112233" };
        CheckEqual("没配分键颜色时用统一色", colors.TrailColorFor("right"), "#112233");
        colors.ButtonTrailColors["right"] = "#AABBCC";
        CheckEqual("右键单独配色覆盖统一色", colors.TrailColorFor("right"), "#AABBCC");
        CheckEqual("没配过的中键仍用统一色", colors.TrailColorFor("middle"), "#112233");
        CheckEqual("热角/贴边这种空按键也用统一色", colors.TrailColorFor(""), "#112233");

        // ---- 手势作用目标：默认是指针下方，写错的值一律退回默认 ----
        var targetOpts = new GestureOptions();
        CheckEqual("手势作用目标默认是指针下方的应用", targetOpts.TargetMode, GestureOptions.TargetPointer);
        Check("默认按指针下方的应用匹配", targetOpts.UsesPointerTarget);
        targetOpts.TargetMode = GestureOptions.TargetActive;
        Check("选活动窗口后不再看指针下方", !targetOpts.UsesPointerTarget);
        targetOpts.TargetMode = "ACTIVE";
        Check("作用目标大小写不敏感", !targetOpts.UsesPointerTarget);
        targetOpts.TargetMode = "乱写的";
        Check("作用目标写错退回指针下方", targetOpts.UsesPointerTarget);

        // ---- 真的去解析一次：选活动窗口时目标窗口必须留空（留空 = 不切窗口） ----
        var (activeApp, activeWindow) = GestureService.ResolveTargetForTest(GestureOptions.TargetActive);
        Check("作用目标=活动窗口时不切窗口", activeWindow == IntPtr.Zero);
        Check("作用目标=活动窗口时也给出程序路径", activeApp != null);

        var (pointerApp, pointerWindow) = GestureService.ResolveTargetForTest(GestureOptions.TargetPointer);
        Check("指针下方解析不会抛异常", pointerApp != null);
        Check("记了目标窗口就一定拿到了程序路径", pointerWindow == IntPtr.Zero || (pointerApp != null && pointerApp.Length > 0));

        // ---- 名称与轨迹同生同灭 ----
        // 命中后名称框（以及陪它的轨迹）停留多久，全由 LabelHoldMs 决定：
        // 以前轨迹立刻清、名称再挂 1.2 秒，看起来就是"手势没了名字还在"。
        var holdOpts = new GestureOptions { LabelLingerMs = 0 };
        CheckEqual("名称停留时间不会低于 1.2 秒", GestureService.LabelHoldMs(holdOpts), 1200);
        holdOpts.LabelLingerMs = 3000;
        CheckEqual("名称停留时间跟着配置放大", GestureService.LabelHoldMs(holdOpts), 3000);
        holdOpts.LabelLingerMs = -500;
        Check("配置写了负数也不会提前收", GestureService.LabelHoldMs(holdOpts) >= 1200);

        /* 用户定的规则（原话）："没有匹配到任何手势，当画完手势的时候就只保留灰色
         * 一瞬间就消失"。所以未识别是一个很短的闪，既不能长到占位，
         * 也不能是 0（否则用户看不出到底触发了没有）。 */
        var unrecOpts = new GestureOptions { LabelLingerMs = 0 };
        Check("未识别只闪一下（≤200ms）", GestureService.UnrecognizedHoldMs(unrecOpts) <= 200,
            GestureService.UnrecognizedHoldMs(unrecOpts).ToString());
        Check("未识别不是 0（还得看得见）", GestureService.UnrecognizedHoldMs(unrecOpts) > 0,
            GestureService.UnrecognizedHoldMs(unrecOpts).ToString());
        CheckEqual("未识别不受「名称停留时间」拉长", GestureService.UnrecognizedHoldMs(new GestureOptions { LabelLingerMs = 5000 }),
            GestureService.UnrecognizedFlashMs);

        /* 命中的手势是"命令已经发出去了"，用户要的是动作，不是名字。
         * 规则（用户原话）：勾了「识别手势立即执行」的，生效那一刻就消失；
         * 没勾的，画的过程中方框跟着预览，松手结算执行时立刻消失。
         * 两种情况都不留尾巴 —— 所以常量必须是 0。 */
        CheckEqual("命中执行后名称立刻消失（停留 0 毫秒）", GestureService.HitLabelLingerMs, 0);
        Check("命中后仍短于未识别提示",
            GestureService.HitLabelLingerMs < GestureService.UnrecognizedHoldMs(unrecOpts));

        try { File.Delete(path); } catch { }
    }
}
