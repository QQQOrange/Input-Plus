using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 内置的边角手势。
///
/// WGestures 把「屏幕四角 + 四条边」当成一等公民（HotCornerCommands[8]）：
/// 指针贴到角上就能触发，贴到边上滚轮就能调音量/亮度。这里给新用户
/// 播一份能直接用的默认表，用户可以在手势页里改动作、改角落，或者直接删掉。
///
/// 只播一次：<see cref="GestureOptions.BuiltinsVersion"/> 记版本。用户删光默认项后
/// 不会在下次启动时又被塞回来 —— 那比"没有默认手势"更让人恼火。
/// </summary>
public static class GestureDefaults
{
    /* 10 = 5.13.0：把只有方向串的存量手势合成为轨迹（<see cref="GesturePattern.Sketch"/>），
     * 识别统一走 WGestures 笔画引擎，方向串不再参与判定。
     * 11 = 1.0：内置一整套调好的鼠标手势（<see cref="BuiltinGestures"/>），
     * 新装或把手势表清空之后自动铺一份，不用再去找 WGestures 导配置。
     * 12 = 1.2：整表导入 WGestures 配置会把 BuiltinsVersion=11 一起带过来，
     * 于是下面那一段四角热角 / 贴边滚轮一次都没跑过 —— 左上角热角就是这么丢的。
     * 升到 12 让升级（和整表导入之后）重跑一次补种；已经有的角/边自带占位判断，不会叠出来。 */
    public const int Version = 12;

    /// <summary>补齐默认边角手势；返回是否改动了设置（调用方负责保存）。</summary>
    public static bool Ensure(AppSettings settings)
    {
        if (settings?.GestureOptions == null) return false;
        var options = settings.GestureOptions;

        /* 旧版本留下的“只点一下”手势和 WGestures 的“WG 设置”不再执行，
         * 每次启动顺手清掉；用户自己新建的笔画手势不受影响。 */
        bool changed = settings.Gestures.RemoveAll(g =>
            g.Kind == "click" ||
            (g.FromWGestures && (g.Name == "WG 设置" || g.Action == "openSettings"))) > 0;

        /* 5.8.7 曾给普通笔画加过「边缘限制」，用户反馈这个功能不该存在：
         * 普通笔画恢复为任何位置都能起手；贴边滚轮仍然保留 Edge。 */
        foreach (var g in settings.Gestures)
        {
            if (g.Kind == "stroke" && g.Edge != 0)
            {
                g.Edge = 0;
                changed = true;
            }
        }

        // 旧配置里可能已经存在同一手势绑多个动作的重复项；保留最早的一条。
        changed |= GestureIdentity.RemoveDuplicates(settings.Gestures) > 0;

        /* 1.0：内置手势库。
         *
         * 只在手势表是空的时候铺 —— 老用户手里已经有一套自己调好的（甚至是从
         * WGestures 导进来的），再塞一遍只会多出一堆重复项，还得挨个删。
         * 所以升级到 1.0 时对存量用户完全静默，只有全新安装、或者用户自己
         * 把手势删光的场景才会看到这些内置手势。
         *
         * 位置很关键：必须紧跟在清理之后、那些历史版本迁移块**之前**。
         * 反过来放的话，迁移块（比如补「粘贴并 Enter」）会先塞一条进来，
         * 表就不空了，全新安装反而一条内置手势都铺不上 —— 这条是真踩过的坑。 */
        if (options.BuiltinsVersion < 11 && settings.Gestures.Count == 0)
        {
            var builtin = BuiltinGestures.Create();
            if (builtin.Count > 0)
            {
                settings.Gestures.AddRange(builtin);
                CoreLog.Info($"[手势] 已铺入内置手势库（{builtin.Count} 条），可在手势页直接改");
                changed = true;
            }
        }

        /* 5.9.0 把左下角默认从“上一个虚拟桌面”改成“开始菜单”。
         * 只迁移仍是旧默认的那一条；用户自己改过动作就不动。 */
        if (options.BuiltinsVersion < 3)
        {
            var leftBottom = settings.Gestures.FirstOrDefault(g =>
                g.Kind == "hotcorner" && g.Corner == 3 &&
                g.Action == "prevDesktop" && g.Name == "左下角 · 上一个桌面");
            if (leftBottom != null)
            {
                leftBottom.Action = "openStartMenu";
                leftBottom.Name = "左下角 · 开始菜单";
                changed = true;
            }
        }

        /* 5.9.0 把右上角默认从“显示桌面”改成“快速设置”。
         * 同样只迁移仍是旧默认的那一条。 */
        if (options.BuiltinsVersion < 4)
        {
            var rightTop = settings.Gestures.FirstOrDefault(g =>
                g.Kind == "hotcorner" && g.Corner == 2 &&
                g.Action == "showDesktop" && g.Name == "右上角 · 显示桌面");
            if (rightTop != null)
            {
                rightTop.Action = "openQuickSettings";
                rightTop.Name = "右上角 · 快速设置";
                changed = true;
            }
        }

        /* 5.9.1 开始支持四个斜线方向。旧版把斜线记成单方向（↓ 或 ←），
         * 这里只迁移“整条轨迹本来就是一条斜线”的条目；直角手势（↓←）
         * 和普通横竖手势不动，避免把用户的手势表改乱。 */
        if (options.BuiltinsVersion < 5)
        {
            foreach (var g in settings.Gestures)
            {
                if (g.Kind != "stroke" || !g.HasPath || g.IsWildcard || g.Pattern.Length != 1) continue;
                string normalized = GesturePattern.FromNormalized(g.Points());
                if (normalized.Length == 1 && GesturePattern.IsDiagonal(normalized))
                {
                    g.Pattern = normalized;
                    changed = true;
                }
            }

            // 迁移后可能和原本就写成斜线的条目撞车，再洗一遍。
            changed |= GestureIdentity.RemoveDuplicates(settings.Gestures) > 0;
        }

        /* 5.9.4：旧版识别器只会记 ↑↓←→，很多实际是斜线的笔画被记成了
         * “↓←”这类直角串。用现在的八方向识别器按原始 Path 重算一遍，
         * 让该显示 ↘↙↗↖ 的条目真正显示斜箭头；真正的直角手势重算后
         * 仍然是 ↓←，不会被误改。没有 Path 的旧数据无法重算，保持原样。 */
        if (options.BuiltinsVersion < 6)
        {
            foreach (var g in settings.Gestures)
            {
                if (g.Kind != "stroke" || !g.HasPath || g.IsWildcard) continue;
                string normalized = GesturePattern.FromNormalized(g.Points());
                if (string.IsNullOrWhiteSpace(normalized) || normalized == GesturePattern.Wildcard) continue;
                if (!string.Equals(g.Pattern, normalized, StringComparison.Ordinal))
                {
                    g.Pattern = normalized;
                    changed = true;
                }
            }

            changed |= GestureIdentity.RemoveDuplicates(settings.Gestures) > 0;
        }

        /* 5.9.9：三件事一起做，全是"录进去了但按了没反应/显示不对"的老数据。
         *
         * ① 一条斜线被记成 ↗↑↗↑↗↑↗（七个箭头）：斜着划的时候角度在 45° 附近抖，
         *    旧识别器每抖一次就多记一段。这里按合并规则收敛成一条，显示与匹配
         *    才对得上（识别时也走同一个 Clean，见 GesturePattern.Tracker.Pattern）。
         * ② 「截图」早期存的是 sendKeys win+s —— Win+S 是搜索，不是截图；
         *    改成内置的 screenshot 动作（ms-screenclip: / 截图工具）。
         * ③ search:<引擎> 里的引擎被用户删掉后，这个动作压根没注册，按下去只有
         *    一声"未能执行"。换成第一个可用引擎，并清掉像 URL 模板的脏参数。 */
        if (options.BuiltinsVersion < 7)
        {
            string fallbackEngine = FirstSearchEngine(settings);

            foreach (var g in settings.Gestures)
            {
                if (g.Kind == "stroke" && !g.IsWildcard && !string.IsNullOrEmpty(g.Pattern))
                {
                    string cleaned = GesturePattern.Clean(g.Pattern);
                    if (!string.Equals(cleaned, g.Pattern, StringComparison.Ordinal))
                    {
                        CoreLog.Info($"[手势] 方向串合并：{g.Pattern} → {cleaned}（{g.Name}）");
                        g.Pattern = cleaned;
                        changed = true;
                    }
                }

                if (IsKeyAction(g.Action) && IsWinSChord(KeyTextOf(g)))
                {
                    CoreLog.Info($"[手势] 「{g.Name}」原来发 Win+S（那是搜索），改成系统截图动作");
                    g.Action = "screenshot";
                    g.Arg = "";
                    changed = true;
                }

                if (g.Action != null &&
                    g.Action.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
                {
                    string key = g.Action["search:".Length..].Trim();
                    bool exists = settings.Search.Any(e =>
                        string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(e.Value));
                    if (!exists && fallbackEngine.Length > 0)
                    {
                        CoreLog.Info($"[手势] 「{g.Name}」的搜索引擎 {key} 已不存在，改用 {fallbackEngine}");
                        g.Action = "search:" + fallbackEngine;
                        if (LooksLikeUrlTemplate(g.Arg)) g.Arg = "";
                        changed = true;
                    }
                }
            }
        }

        /* 5.10.1：补回「粘贴并 Enter」。
         *
         * 这是"右键 + 左键"的组合手势：按住右键往下画一笔 ↓，画完**先别松手**，
         * 再点一下左键，松开始按键就触发。动作是按键序列 ctrl+v enter ——
         * 聊天框里粘完直接发出去，比"粘贴"多一步回车。
         * 用户清理手势表时把它一起删了，这里补回来。
         * 判重按"形状"看：已经有一条 右键+左键 ↓ 的（不管叫什么名）就不再加，
         * 免得和用户自己建的那条撞车。版本只升一次，删掉后不会每次启动又回来。 */
        if (options.BuiltinsVersion < 8)
        {
            bool hasPasteEnter = settings.Gestures.Any(g =>
                g.Kind == "stroke" &&
                string.Equals(g.Chord, "left", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(g.Pattern, "↓", StringComparison.Ordinal));
            if (!hasPasteEnter)
            {
                settings.Gestures.Add(new MouseGesture
                {
                    Id = MouseGesture.NewId(),
                    Name = "粘贴并 Enter",
                    Kind = "stroke",
                    Button = "right",
                    Chord = "left",
                    Pattern = "↓",
                    Action = "customKey",
                    Arg = new InputPlus.Core.Actions.CustomKeySpec
                    {
                        Key = InputPlus.Core.Actions.CustomKeyRunner.Canonical("ctrl+v enter"),
                    }.ToArg(),
                    Enabled = true,
                });
                CoreLog.Info("[手势] 已补回组合手势「粘贴并 Enter」（按住右键画 ↓，画完点一下左键）");
                changed = true;
            }
        }

        /* 5.10.6：手势表大扫除 + 老动作升级。
         *
         * ① 「音量-」「音量+」是 WGestures 导入时留下的"屏幕角落 + 滚轮"，用户明确说
         *    用不上 —— 删掉（贴边滚轮那两条音量手势是另一个 Kind，不动）。
         * ② runProgram / openUrl 是隐藏的兼容动作：界面里选不到、执行还经常失败
         *    （任务管理器写成 "cmd.exe /c taskmgr"，微信写成 D:\software\Weixin\Weixin.exe）。
         *    能对上内置动作的就换过去：taskmgr → 打开任务管理器，文档路径 → 直达文件，
         *    网址 → 直达网址；认不出的保持原样，免得把用户的动作改坏
         *    （exe 路径现在也算"认得出"—— 收窄后的「运行命令」本来就能启动它）。
         * ③ search:<key> 的 Arg 如果是一整条 URL 模板，会被当成搜索词拼进地址里 —— 清掉。 */
        if (options.BuiltinsVersion < 9)
        {
            int removed = settings.Gestures.RemoveAll(g =>
                g.Kind == "wheel" && string.IsNullOrWhiteSpace(g.Button) &&
                (IsVolumeAction(g) || (g.Name ?? "").StartsWith("音量", StringComparison.Ordinal)));
            if (removed > 0)
            {
                CoreLog.Info($"[手势] 删掉 {removed} 条「屏幕角落 + 滚轮」的音量手势（贴边滚轮调音量更顺手）");
                changed = true;
            }

            foreach (var g in settings.Gestures)
            {
                if (string.Equals(g.Action, "runProgram", StringComparison.OrdinalIgnoreCase))
                {
                    string cmd = (g.Arg ?? "").Trim().Trim('"').Trim();
                    if (cmd.Contains("taskmgr", StringComparison.OrdinalIgnoreCase))
                    {
                        CoreLog.Info($"[手势] 「{g.Name}」原来跑 {cmd}，改成内置的「打开任务管理器」");
                        g.Action = "openTaskManager";
                        g.Arg = "";
                        changed = true;
                    }
                    /* 裸 exe 路径**不再**改走「直达文件」：动作收窄成「运行命令」之后，
                     * exe 本来就是它能直接启动的东西（UseShellExecute=false 建进程），
                     * 改过去等于让打开文件的入口去干运行程序的事。
                     * 真正要救的是"把 .docx / .pdf 当程序填"的老手势 —— 那些在新语义下
                     * 会被拒绝（提示改用直达文件），迁到「直达文件」才和原来一样双击打开。 */
                    else if (LooksLikeDocumentPath(cmd))
                    {
                        CoreLog.Info($"[手势] 「{g.Name}」改成「直达文件」：{cmd}");
                        g.Action = "openFile";
                        g.Arg = cmd;
                        changed = true;
                    }
                }
                else if (string.Equals(g.Action, "openUrl", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(g.Arg))
                {
                    CoreLog.Info($"[手势] 「{g.Name}」的「打开网址」改成「直达网址」");
                    g.Action = "webPage";
                    changed = true;
                }

                /* ④ 编辑器吞动作的善后。
                 *
                 * 5.10.3 及更早，手势的动作如果是隐藏的兼容动作（runProgram / openUrl），
                 * 「功能」下拉框挂不上它，会退回分类里的第一项 —— 也就是「禁用这个键」。
                 * 用户只是重画了一次笔画、点了保存，动作就变成 swallowKey、参数也被清空，
                 * 表现是"这个手势以前好好的，现在按下去什么都不做"。
                 *
                 * 这里只修名字能对上、且"禁用这个键"明显是误伤的几条：
                 * 任务管理器 → 打开任务管理器；微信 → 直达微信主程序（找得到才改）。 */
                if (string.Equals(g.Action, "swallowKey", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(g.Arg))
                {
                    string name = g.Name ?? "";
                    if (name.Contains("任务管理", StringComparison.Ordinal))
                    {
                        CoreLog.Info($"[手势] 「{name}」的动作被改成了「禁用这个键」，已修回「打开任务管理器」");
                        g.Action = "openTaskManager";
                        changed = true;
                    }
                    else if (name.Contains("微信", StringComparison.Ordinal) ||
                             name.Contains("WeChat", StringComparison.OrdinalIgnoreCase))
                    {
                        string? weixin = FindWeixinExe();
                        if (weixin != null)
                        {
                            CoreLog.Info($"[手势] 「{name}」的动作被改成了「禁用这个键」，已修回「直达文件」：{weixin}");
                            g.Action = "openFile";
                            g.Arg = weixin;
                            changed = true;
                        }
                    }
                }

                if (g.Action != null &&
                    g.Action.StartsWith("search:", StringComparison.OrdinalIgnoreCase) &&
                    LooksLikeUrlTemplate(g.Arg))
                {
                    CoreLog.Info($"[手势] 「{g.Name}」的搜索参数其实是一整条网址模板，已清掉");
                    g.Arg = "";
                    changed = true;
                }
            }

            changed |= GestureIdentity.RemoveDuplicates(settings.Gestures) > 0;
        }

        /* 5.13.0：识别只剩一套引擎（WGestures 笔画引擎），方向串不再是判定依据。
         *
         * 存量手势里有一部分只有方向串、没存过轨迹（手输的、老导入的、5.10.1 补的
         * 「粘贴并 Enter」）。它们以前靠方向串比对命中；方向串退休后必须有个形状才进得了
         * 引擎 —— 这里把方向串合成为折线写进 Path（GesturePattern.Sketch），从此和录制
         * 出来的手势走完全一样的判定。通配 * 没有形状，跳过。 */
        if (options.BuiltinsVersion < 10)
        {
            int filled = 0;
            foreach (var g in settings.Gestures)
            {
                if (!string.Equals(g.Kind, "stroke", StringComparison.Ordinal)) continue;
                if (g.HasPath || g.IsWildcard) continue;

                string pattern = GesturePattern.Clean(g.Pattern);
                if (pattern.Length == 0) continue;

                var sketch = GesturePattern.Sketch(pattern);
                if (sketch.Count < 2) continue;

                g.Path = GestureMath.ToFlat(sketch);
                g.Pattern = pattern;
                filled++;
            }
            if (filled > 0)
            {
                CoreLog.Info($"[手势] {filled} 条只有方向串的手势已合成为轨迹，统一走形状识别");
                changed = true;
            }
        }

        if (options.BuiltinsVersion >= Version) return changed;

        AddHotCorner(settings, 1, "switchNextApp", "左上角 · 切换软件");
        AddHotCorner(settings, 2, "openQuickSettings", "右上角 · 快速设置");
        AddHotCorner(settings, 3, "openStartMenu", "左下角 · 开始菜单");
        AddHotCorner(settings, 4, "nextDesktop", "右下角 · 下一个桌面");

        AddEdgeWheel(settings, 1, -1, "volumeUp", "上边缘 · 滚轮向上调音量");
        AddEdgeWheel(settings, 1, 1, "volumeDown", "上边缘 · 滚轮向下调音量");
        AddEdgeWheel(settings, 3, -1, "brightnessUp", "下边缘 · 滚轮向上调亮度");
        AddEdgeWheel(settings, 3, 1, "brightnessDown", "下边缘 · 滚轮向下调亮度");

        // 左右边缘先占位，动作留空，用户在边缘手势栏里自己挑功能。
        AddEdgeWheel(settings, 2, -1, "", "右边缘 · 滚轮向上");
        AddEdgeWheel(settings, 2, 1, "", "右边缘 · 滚轮向下");
        AddEdgeWheel(settings, 4, -1, "", "左边缘 · 滚轮向上");
        AddEdgeWheel(settings, 4, 1, "", "左边缘 · 滚轮向下");

        options.BuiltinsVersion = Version;
        return true;
    }

    /// <summary>
    /// 这个动作是不是"发一段按键"（自定义按键，以及还没迁移的旧 sendKeys）。
    ///
    /// 为什么要判两个 id：迁移在设置载入时跑，而这里可能在迁移之前就被调用
    /// （例如刚反序列化、还没走完迁移链的自检路径）。两个都认才不留死角。
    /// </summary>
    private static bool IsKeyAction(string? action) =>
        string.Equals(action, "customKey", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(action, "sendKeys", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 手势实际要发的按键文本。
    ///
    /// customKey 把键存在 JSON 里（<c>{"Key":"win+s"}</c>），
    /// 直接拿 Arg 去比对会把 JSON 的花括号当成按键字符；
    /// 其它动作（老的 sendKeys、内置动作）的 Arg 本来就是裸文本。
    /// </summary>
    private static string KeyTextOf(MouseGesture g)
    {
        if (string.Equals(g.Action, "customKey", StringComparison.OrdinalIgnoreCase))
            return InputPlus.Core.Actions.CustomKeySpec.FromArg(g.Arg).Key;
        return g.Arg ?? "";
    }

    /// <summary>
    /// 参数是不是 Win+S 这个组合（容忍 "win+s" / "Win + S" / "WINS" 几种写法）。
    /// 用来把老「截图」手势从"按键序列"迁到内置截图动作上。
    /// </summary>
    private static bool IsWinSChord(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return false;
        var sb = new System.Text.StringBuilder(arg.Length);
        foreach (char c in arg)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString().Equals("wins", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是不是"调音量"的动作（volumeUp / volumeDown / volumedown / mute 都算）。</summary>
    private static bool IsVolumeAction(MouseGesture g)
    {
        string arg = (g.Arg ?? "").ToLowerInvariant();
        return arg.Contains("volume", StringComparison.Ordinal) || arg.Contains("mute", StringComparison.Ordinal);
    }

    /// <summary>
    /// 参数是不是"一个存在、且不是可执行文件"的本地文件（.docx / .pdf / .mp4…）。
    ///
    /// 只给老手势迁移用：这类参数以前靠 UseShellExecute 交给关联程序打开，
    /// 收窄后的「运行命令」会拒绝它（并提示改用「直达文件」），所以启动时换成「直达文件」。
    /// 判断依据是文件真实存在 + 扩展名不是程序/脚本，而不是"看起来像路径"：
    /// 带空格的一整行可能是命令（notepad.exe D:\笔记.txt），猜错就会把命令改成打开文件。
    /// </summary>
    private static bool LooksLikeDocumentPath(string? cmd)
    {
        string s = (cmd ?? "").Trim().Trim('"').Trim();
        if (s.Length == 0) return false;
        try
        {
            s = Environment.ExpandEnvironmentVariables(s);
            if (!System.IO.File.Exists(s)) return false;
            string ext = System.IO.Path.GetExtension(s);
            foreach (string program in new[] { ".exe", ".com", ".bat", ".cmd" })
            {
                if (ext.Equals(program, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 找微信主程序（Weixin.exe / WeChat.exe）。
    /// 用来把"动作被编辑器吞掉"的微信手势修回来，找不到就返回 null。
    /// </summary>
    private static string? FindWeixinExe()
    {
        foreach (string sub in new[]
                 {
                     @"SOFTWARE\Tencent\Weixin", @"SOFTWARE\WOW6432Node\Tencent\Weixin",
                     @"SOFTWARE\Tencent\WeChat", @"SOFTWARE\WOW6432Node\Tencent\WeChat",
                 })
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(sub);
                if (key?.GetValue("InstallPath") is string dir && !string.IsNullOrWhiteSpace(dir))
                {
                    foreach (string exe in new[] { "Weixin.exe", "WeChat.exe" })
                    {
                        string p = Path.Combine(dir, exe);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { /* 读注册表失败就当没装，不影响启动 */ }
        }

        var roots = new List<string?>
        {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        };
        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (string rel in new[]
                     {
                         @"Tencent\Weixin\Weixin.exe", @"Tencent\WeChat\WeChat.exe",
                         @"Tencent\Weixin\WeChat.exe",
                     })
            {
                try
                {
                    string p = Path.Combine(root, rel);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }

        /* 装在自定义目录（D:\software\Weixin 这种）时注册表和 ProgramFiles 都找不到。
         * 那就看它现在是不是正开着 —— 开着就一定能拿到真实路径。 */
        foreach (string name in new[] { "Weixin", "WeChat" })
        {
            try
            {
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    try
                    {
                        string? p = proc.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
                    }
                    catch { /* 位数不同的进程读不到 MainModule，跳过 */ }
                    finally { proc.Dispose(); }
                }
            }
            catch { /* 枚举进程失败就当没开，不影响启动 */ }
        }
        return null;
    }

    /// <summary>参数长得像搜索引擎的 URL 模板（"https://…/s?wd={q}" 之类），对"直接搜索"没有意义。</summary>
    private static bool LooksLikeUrlTemplate(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return false;
        return arg.Contains("{q}", StringComparison.OrdinalIgnoreCase)
               || arg.Contains("://", StringComparison.Ordinal)
               || arg.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>配置里第一个能用的搜索引擎 key（跳过 default，它只是"默认引擎"标记）。</summary>
    private static string FirstSearchEngine(AppSettings settings)
    {
        foreach (var e in settings.Search)
        {
            if (string.IsNullOrWhiteSpace(e.Key)) continue;
            if (e.Key.Equals("default", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(e.Value)) continue;
            return e.Key;
        }
        return "";
    }

    private static void AddHotCorner(AppSettings settings, int corner, string action, string name)
    {
        /* 占位只看 hotcorner（悬停）。以前把 WGestures 导进来的 "corner"（到角上点一下）
         * 也算作占位 —— 整套 WGestures 配置导进来之后（左上角被「静音」占着），
         * 悬停热角永远补不回来，边缘手势栏里就只剩一条角落点击。
         * 两者在引擎里是两条不同的触发路径（悬停 vs 按下），同一个角上可以各留一条：
         * 指针进角先出悬停热角，再点一下才走 corner 手势。 */
        if (settings.Gestures.Any(g => g.Kind == "hotcorner" && g.Corner == corner)) return;
        settings.Gestures.Add(new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = name,
            Button = "",
            Chord = "",
            Kind = "hotcorner",
            Corner = corner,
            Wheel = 0,
            Action = action,
            Enabled = true,
        });
    }

    private static void AddEdgeWheel(AppSettings settings, int edge, int wheel, string action, string name)
    {
        if (settings.Gestures.Any(g => g.Kind == "edgewheel" && g.Edge == edge && g.Wheel == wheel)) return;
        settings.Gestures.Add(new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = name,
            Button = "",
            Chord = "",
            Kind = "edgewheel",
            Edge = edge,
            Wheel = wheel,
            Action = action,
            Enabled = true,
        });
    }
}
