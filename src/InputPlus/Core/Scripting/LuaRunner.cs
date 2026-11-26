using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using InputPlus.Core.Actions;
using InputPlus.Core.Input;
using InputPlus.Core.Text;
using InputPlus.Core.Windows;
using MoonSharp.Interpreter;

namespace InputPlus.Core.Scripting;

/// <summary>
/// 一次 Lua 脚本执行的结果。
///
/// 约定：<see cref="LuaRunner.Run(string, string, ActionContext?, Func{string, string, bool}?, string)"/>
/// **永远不抛异常**，失败原因放在 <see cref="Error"/> 里（中文一句话 + 原始报错），
/// 这样调用方（动作目录、脚本管理窗口、自检）都不用再写 try/catch。
/// </summary>
public sealed class LuaRunResult
{
    /// <summary>脚本正常跑到了结尾（超时 / 语法错 / 运行时错 都是 false）。</summary>
    public bool Ok { get; init; }

    /// <summary>print 和 ip.log 收集到的文本，一行一条；超过上限会截断。</summary>
    public string Output { get; init; } = "";

    /// <summary>失败原因；成功时为空串。</summary>
    public string Error { get; init; } = "";

    /// <summary>实际耗时（毫秒，含启动线程的开销）。</summary>
    public double ElapsedMs { get; init; }
}

/// <summary>
/// Lua 脚本动作的运行时：把一段 Lua 跑起来，同时暴露 <c>ip.*</c> 给脚本调用。
///
/// 为什么单独抽一个 Runner：动作目录里的「Lua 脚本」动作、脚本管理窗口的「运行测试」
/// 和自检走的是同一条执行路径，行为必须一致；沙盒、超时、输出上限这些规矩也只该
/// 有一份实现。
///
/// 沙盒策略：<see cref="CoreModules.Preset_HardSandbox"/>（没有 io / os / dofile /
/// loadfile / require / load），额外补 <see cref="CoreModules.ErrorHandling"/> 让脚本
/// 里能用 pcall。脚本能碰到外面的唯一通道就是 <c>ip</c> 表。
///
/// 线程模型：脚本跑在独立的后台线程上（STA —— ip.text.get/set 要碰 WPF 剪贴板），
/// 调用方在自己的线程上最多等 <see cref="TimeoutMs"/> 毫秒。**不能**把脚本挪到
/// UI 线程去跑：UI 线程正卡在 Join 上等它，再让它 Dispatcher.Invoke 回来就是死锁。
/// </summary>
public static class LuaRunner
{
    /// <summary>单个脚本最多跑多久（毫秒）。超时后调用方立刻拿到结果，脚本线程被丢在后台收尾。</summary>
    public const int TimeoutMs = 5000;

    /// <summary>输出最多保留多少字符，超了截断并补一句提示。</summary>
    public const int MaxOutputChars = 8000;

    /* 脚本里能用到的 API 速查表（脚本管理窗口右侧直接展示，所以排版按等宽字体对齐）。 */
    private const string ApiDocText = @"【基本】
ip.log(...)            打印到「运行结果」，多个参数用制表符隔开
ip.notify(text)        弹一条提示（和软件里其它提示一样）
ip.action(id, arg)     执行动作目录里的动作，返回 true / false
ip.run(cmd)            运行命令（等同「运行命令」动作，只跑命令、不开文件）
ip.open(url)           用默认浏览器打开网址
ip.openFolder(path)    打开文件夹
ip.openFile(path)      用默认程序打开文件
ip.sleep(ms)           等一会儿（超时会被打断，单次最多等到 5 秒）
ip.now()               当前时间，例如 2025-01-30 14:05:09
ip.time()              Unix 时间戳（秒）
ip.arg                 动作参数（字符串）
ip.script              脚本名（字符串）
ip.dryrun              是否干跑（true 时只演练、不真的发按键）

【剪贴板 / 选区】
ip.text.get()          读系统剪贴板里的文字（里面不是文字就返回空串）
ip.text.set(s)         把文字写进系统剪贴板，返回 true / false
ip.text.copy()         复制当前选区（等同 Ctrl+Insert）
ip.text.paste()        把系统剪贴板粘贴到当前窗口（等同 Shift+Insert）
ip.text.slot(n)        独立剪贴板 n 里的文字（n = 1 或 2）

【键盘】
ip.key(""ctrl+shift+a"") 按一下组合键；多段用逗号分隔：""ctrl+k, ctrl+c""
ip.keyDown(""shift"")    按住（只支持「单键 + 修饰键」）
ip.keyUp(""shift"")      松开
                       修饰键写法：ctrl / shift / alt / win

【鼠标】
ip.click(""left"")       点一下鼠标：left / right / middle
ip.wheel(3)            滚轮，正数向上、负数向下
ip.move(dx, dy)        相对移动鼠标（按设置里的鼠标速度放大，和「鼠标移动」动作一致）

【窗口】（都是对当前前台窗口下手）
ip.win.close()         关闭
ip.win.minimize()      最小化
ip.win.maximize()      最大化 / 还原
ip.win.center()        居中
ip.win.topmost()       置顶开关
ip.win.title()         前台窗口标题";

    /* 新建脚本时「插入示例」用的那段代码，必须能直接跑通。 */
    private const string SampleText = @"-- 示例：整理剪贴板里的文字，再写回去
-- 这段能直接跑：点「运行测试」看结果，改成自己需要的就行。

local text = ip.text.get()
if text == """" then
    ip.notify(""剪贴板里没有文字，先复制点什么"")
    return
end

ip.log(""读到的长度："", string.len(text))

-- 去掉首尾空白（Lua 的字符串模式匹配）
local cleaned = string.gsub(text, ""^%s*(.-)%s*$"", ""%1"")
ip.text.set(cleaned)

ip.log(""已写回剪贴板"")
ip.notify(""剪贴板已整理："" .. string.sub(cleaned, 1, 20))

-- 想连动作目录里的功能一起用，就这样写（写错只返回 false，不会打断脚本）：
-- ip.action(""paste1"", """")     -- 粘贴独立剪贴板 1
-- ip.key(""ctrl+shift+a"")        -- 发一组键
-- ip.sleep(120)                 -- 等 120 毫秒
-- ip.log(ip.now())              -- 打一行时间";

    /// <summary>中文速查表：脚本里能用哪些 <c>ip.*</c>，窗口右侧就显示这段文字。</summary>
    public static string ApiDoc { get; } = ApiDocText.TrimEnd('\r', '\n');

    /// <summary>一段能直接跑的示例脚本（「插入示例」按钮用它）。</summary>
    public static string SampleCode { get; } = SampleText.TrimEnd('\r', '\n');

    /// <summary>
    /// 跑一条脚本对象（动作目录里的「Lua 脚本」动作走这里）。
    /// </summary>
    /// <param name="script">脚本；它的 <see cref="LuaScript.Code"/> 会被执行。</param>
    /// <param name="ctx">动作上下文，为 null 时 <c>ip</c> 里依赖它的 API 一律变成空操作。</param>
    /// <param name="invoke">执行动作目录里的动作（一般是 AppHost.InvokeAction）。</param>
    /// <param name="arg">动作参数，脚本里用 <c>ip.arg</c> 取。</param>
    public static LuaRunResult Run(LuaScript script, ActionContext? ctx, Func<string, string, bool>? invoke, string arg = "")
    {
        if (script == null)
        {
            return new LuaRunResult
            {
                Ok = false,
                Error = Loc.T("脚本对象是空的"),
            };
        }

        return Run(script.Code, script.Name, ctx, invoke, arg);
    }

    /// <summary>
    /// 跑一段 Lua 代码。
    /// </summary>
    /// <param name="code">Lua 源码。</param>
    /// <param name="scriptName">脚本名：报错时显示，脚本里用 <c>ip.script</c> 取。</param>
    /// <param name="ctx">动作上下文，为 null 时依赖它的 API 一律空转。</param>
    /// <param name="invoke">转发给 ActionCatalog 执行动作的回调，为 null 时 ip.action 恒为 false。</param>
    /// <param name="arg">动作参数（脚本里是 <c>ip.arg</c>）。</param>
    public static LuaRunResult Run(string code, string scriptName, ActionContext? ctx, Func<string, string, bool>? invoke, string arg = "")
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            /* 空脚本算成功：新建的脚本还没写内容时点「运行测试」，不该报错。 */
            return new LuaRunResult { Ok = true };
        }

        var stopwatch = Stopwatch.StartNew();
        var state = new RunState(scriptName, ctx, invoke, arg);

        var worker = new Thread(() => Execute(state, code))
        {
            IsBackground = true,
            Name = "InputPlus.Lua",
        };

        /* STA：ip.text.get / ip.text.set 走的是 WPF 剪贴板，必须是 STA 公寓。
         * 非 Windows 平台没有公寓概念（SetApartmentState 会抛 NotSupportedException），跳过即可。 */
        try { worker.SetApartmentState(ApartmentState.STA); }
        catch (NotSupportedException) { }

        try
        {
            worker.Start();
        }
        catch (Exception ex)
        {
            return new LuaRunResult
            {
                Ok = false,
                Error = Loc.F("脚本线程启动失败：{0}", ex.Message),
                ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            };
        }

        if (worker.Join(TimeoutMs))
        {
            return state.BuildResult(stopwatch.Elapsed.TotalMilliseconds);
        }

        /* 超时：先通知脚本线程收手（ip.sleep / 后续 API 会立刻空转），然后马上返回。
         * 线程是后台线程，就算它还在慢慢收尾也不会拖住程序退出。 */
        state.Abandon();
        return new LuaRunResult
        {
            Ok = false,
            Output = state.BuildOutput(),
            Error = Loc.T("脚本运行超过 5 秒，已放弃（线程还在后台收尾，下次别写死循环）"),
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>
    /// 自检用：清掉跨次运行的静态状态。
    ///
    /// 目前每次运行的状态全在 <see cref="RunState"/> 里，Runner 自身不存东西，
    /// 所以这里是空实现 —— 留着这个入口，以后真加了缓存（比如编译结果）不用改自检。
    /// </summary>
    public static void ResetForTest()
    {
    }

    /// <summary>脚本线程的主体：搭沙盒 → 挂 ip 表 → 执行 → 把结果写回 state。</summary>
    private static void Execute(RunState state, string code)
    {
        try
        {
            /* 硬沙盒 + ErrorHandling：没有 io / os / dofile / loadfile / require / load，
             * 但 pcall / xpcall 可用（用户写"容错调用"时这是常识工具）。 */
            var script = new Script(CoreModules.Preset_HardSandbox | CoreModules.ErrorHandling);

            /* print 也进「运行结果」：用户最熟悉 print，不能让它石沉大海。 */
            script.Options.DebugPrint = text => state.Append(text);

            script.Globals.Set("ip", DynValue.NewTable(BuildApi(script, state)));

            string chunkName = string.IsNullOrWhiteSpace(state.ScriptName) ? "lua" : state.ScriptName;
            script.DoString(code, null, chunkName);
            state.Complete(true, "");
        }
        catch (SyntaxErrorException ex)
        {
            state.Complete(false, Loc.T("语法错误：") + Explain(ex));
        }
        catch (ScriptRuntimeException ex)
        {
            state.Complete(false, Loc.T("脚本出错：") + Explain(ex));
        }
        catch (Exception ex)
        {
            state.Complete(false, Loc.T("运行失败：") + ex.Message);
        }
    }

    /// <summary>取报错文字：MoonSharp 的 DecoratedMessage 带「哪一行」，比 Message 有用。</summary>
    private static string Explain(InterpreterException ex)
    {
        string message = string.IsNullOrWhiteSpace(ex.DecoratedMessage) ? ex.Message : ex.DecoratedMessage;
        return string.IsNullOrWhiteSpace(message) ? Loc.T("未知错误") : message;
    }

    /// <summary>
    /// 把 <c>ip</c> 表搭出来。
    ///
    /// 每个 API 都套了 try/catch：单个 API 出错只往「运行结果」里记一行（[ip.xxx] 原因），
    /// 返回 nil / false，脚本继续往下跑 —— 用户传错个参数不该让整段脚本当场结束。
    /// </summary>
    private static Table BuildApi(Script script, RunState state)
    {
        var ip = new Table(script);
        var text = new Table(script);
        var win = new Table(script);

        DynValue nil = DynValue.NewNil();
        DynValue no = DynValue.NewBoolean(false);

        void Add(Table target, string fullName, Func<CallbackArguments, DynValue> body, DynValue fallback)
        {
            int dot = fullName.LastIndexOf('.');
            string key = dot >= 0 ? fullName.Substring(dot + 1) : fullName;
            target.Set(key, DynValue.NewCallback(delegate (ScriptExecutionContext context, CallbackArguments args)
            {
                if (state.Cancelled) return fallback;       // 已经超时了：后面的调用一律空转
                try
                {
                    return body(args);
                }
                catch (Exception ex)
                {
                    state.Append("[" + fullName + "] " + ex.Message);
                    return fallback;
                }
            }, fullName));
        }

        // ---------------- 基本 ----------------
        Add(ip, "ip.log", args =>
        {
            var parts = new List<string>(args.Count);
            for (int i = 0; i < args.Count; i++) parts.Add(args[i].ToPrintString());
            state.Append(string.Join("\t", parts));
            return nil;
        }, nil);

        Add(ip, "ip.notify", args =>
        {
            string message = ArgString(args, 0);
            if (message.Length > 0) state.Ctx?.Notify(message);
            return nil;
        }, nil);

        Add(ip, "ip.action", args =>
        {
            string id = ArgString(args, 0);
            string actionArg = ArgString(args, 1);
            if (id.Length == 0 || state.Invoke == null) return no;
            return DynValue.NewBoolean(state.Invoke(id, actionArg));
        }, no);

        Add(ip, "ip.run", args =>
        {
            string command = ArgString(args, 0);
            if (command.Length > 0) state.Ctx?.RunProgram(command);
            return nil;
        }, nil);

        Add(ip, "ip.open", args =>
        {
            string url = ArgString(args, 0);
            if (url.Length > 0) state.Ctx?.OpenUrl(url);
            return nil;
        }, nil);

        Add(ip, "ip.openFolder", args =>
        {
            string path = ArgString(args, 0);
            if (path.Length > 0) state.Ctx?.OpenFolder(path);
            return nil;
        }, nil);

        Add(ip, "ip.openFile", args =>
        {
            string path = ArgString(args, 0);
            if (path.Length > 0) state.Ctx?.OpenFile(path);
            return nil;
        }, nil);

        Add(ip, "ip.sleep", args =>
        {
            state.Sleep(ArgNumber(args, 0));
            return nil;
        }, nil);

        Add(ip, "ip.now", _ => DynValue.NewString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), DynValue.NewString(""));

        Add(ip, "ip.time", _ => DynValue.NewNumber(DateTimeOffset.Now.ToUnixTimeSeconds()), DynValue.NewNumber(0));

        // ---------------- 键盘 ----------------
        Add(ip, "ip.key", args => DynValue.NewBoolean(SendCombo(ArgString(args, 0))), no);
        Add(ip, "ip.keyDown", args => DynValue.NewBoolean(HoldCombo(ArgString(args, 0), down: true)), no);
        Add(ip, "ip.keyUp", args => DynValue.NewBoolean(HoldCombo(ArgString(args, 0), down: false)), no);

        // ---------------- 鼠标 ----------------
        Add(ip, "ip.click", args =>
        {
            string kind = ArgString(args, 0).Trim().ToLowerInvariant();
            switch (kind)
            {
                case "":
                case "left":
                    InputSender.Click(false);
                    return DynValue.NewBoolean(true);
                case "right":
                    InputSender.Click(true);
                    return DynValue.NewBoolean(true);
                case "middle":
                    InputSender.Tap(0x04 /* VK_MBUTTON：Tap 内部按虚拟码分派到鼠标按下/抬起 */);
                    return DynValue.NewBoolean(true);
                default:
                    return no;
            }
        }, no);

        Add(ip, "ip.wheel", args =>
        {
            /* 和 InputSender.Wheel 的符号约定一致：正数向上滚（Windows 的 mouseData 正数=远离用户）。 */
            InputSender.Wheel((int)Math.Round(ArgNumber(args, 0)));
            return nil;
        }, nil);

        Add(ip, "ip.move", args =>
        {
            int dx = (int)Math.Round(ArgNumber(args, 0));
            int dy = (int)Math.Round(ArgNumber(args, 1));
            MouseService? mouse = state.Ctx?.Mouse;
            if (mouse == null) return no;
            return DynValue.NewBoolean(mouse.Move(dx, dy));
        }, no);

        // ---------------- 剪贴板 / 选区 ----------------
        Add(text, "ip.text.get", _ => DynValue.NewString(ReadClipboardText()), DynValue.NewString(""));

        Add(text, "ip.text.set", args => DynValue.NewBoolean(ClipboardPaster.TrySetText(ArgString(args, 0))), no);

        Add(text, "ip.text.copy", _ =>
        {
            TextActions.Copy();      // Ctrl+Insert：复制当前选区到系统剪贴板
            return nil;
        }, nil);

        Add(text, "ip.text.paste", _ =>
        {
            ClipboardService? clipboard = state.Ctx?.Clipboard;
            if (clipboard != null) return DynValue.NewBoolean(clipboard.PasteSystem());
            TextActions.Paste();     // 没有上下文时退到 Shift+Insert
            return DynValue.NewBoolean(true);
        }, no);

        Add(text, "ip.text.slot", args =>
        {
            int slot = (int)Math.Round(ArgNumber(args, 0));
            string value = state.Ctx?.Clipboard.SlotText(slot) ?? "";
            return DynValue.NewString(value);
        }, DynValue.NewString(""));

        // ---------------- 窗口（当前前台窗口） ----------------
        Add(win, "ip.win.close", _ => Bool(state.Ctx?.Windows.Close()), no);
        Add(win, "ip.win.minimize", _ => Bool(state.Ctx?.Windows.Minimize()), no);
        Add(win, "ip.win.maximize", _ => Bool(state.Ctx?.Windows.ToggleMaximize()), no);
        Add(win, "ip.win.center", _ => Bool(state.Ctx?.Windows.Center()), no);
        Add(win, "ip.win.topmost", _ => Bool(state.Ctx?.Windows.ToggleTopmost()), no);
        Add(win, "ip.win.title", _ => DynValue.NewString(WindowService.ForegroundInfo()?.Title ?? ""), DynValue.NewString(""));

        // ---------------- 只读变量 ----------------
        ip.Set("arg", DynValue.NewString(state.Arg));
        ip.Set("script", DynValue.NewString(state.ScriptName));
        ip.Set("dryrun", DynValue.NewBoolean(InputSender.DryRun));
        ip.Set("text", DynValue.NewTable(text));
        ip.Set("win", DynValue.NewTable(win));

        return ip;
    }

    /// <summary>按一下组合键；支持 "ctrl+k, ctrl+c" 这种多段写法（和设置里的多段触发键同一套解析）。</summary>
    private static bool SendCombo(string combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return false;

        IReadOnlyList<string> segments = CustomKeyRunner.SplitCombos(combo);
        bool sent = false;

        for (int i = 0; i < segments.Count; i++)
        {
            string segment = (segments[i] ?? "").Trim();
            if (segment.Length == 0) continue;

            if (sent) Thread.Sleep(30);     // 多段之间留点间隔，像人手按的

            var (modifiers, vk) = CustomKeyRunner.ResolveCombo(segment);
            if (vk == 0) continue;          // 键名不认识：跳过这一段，别报错

            if (modifiers.Length == 0)
            {
                InputSender.Tap(vk);
            }
            else
            {
                /* WithModifiers 内部是"正序按下、逆序松开"，并且会处理修饰键卡住的情况，
                 * 所以这里不要自己 KeyDown/KeyUp —— 那等于绕开它的善后逻辑。 */
                InputSender.WithModifiers(modifiers, () => InputSender.Tap(vk));
            }

            sent = true;
        }

        return sent;
    }

    /// <summary>按下 / 抬起一组键（只支持「单键 + 修饰键」，多段请用 ip.key）。</summary>
    private static bool HoldCombo(string combo, bool down)
    {
        if (string.IsNullOrWhiteSpace(combo)) return false;

        var (modifiers, vk) = CustomKeyRunner.ResolveCombo(combo.Trim());

        /* 只写了一个修饰键（比如 ip.keyDown("shift")）：ResolveCombo 只给出修饰键列表，
         * 主键为 0 —— 这种情况就把那个修饰键当成主键按下去。 */
        if (vk == 0 && modifiers.Length == 1)
        {
            vk = modifiers[0];
            modifiers = Array.Empty<int>();
        }

        if (vk == 0) return false;

        if (down)
        {
            for (int i = 0; i < modifiers.Length; i++) InputSender.KeyDown(modifiers[i]);
            InputSender.KeyDown(vk);
        }
        else
        {
            InputSender.KeyUp(vk);                                  // 主键先抬
            for (int i = modifiers.Length - 1; i >= 0; i--) InputSender.KeyUp(modifiers[i]);
        }

        return true;
    }

    /// <summary>读系统剪贴板里的纯文本；里面不是文本、或者被别的程序占着，都返回空串。</summary>
    private static string ReadClipboardText()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!ClipboardAccess.ContainsText()) return "";
                return ClipboardAccess.GetText() ?? "";
            }
            catch (Exception)
            {
                /* 剪贴板被别的程序占着是常态（浏览器右键复制的那一瞬间），等一下再试。 */
                Thread.Sleep(15);
            }
        }

        return "";
    }

    /// <summary>bool? → Lua 的 true/false（上下文缺失一律 false，脚本里好判断）。</summary>
    private static DynValue Bool(bool? value) => DynValue.NewBoolean(value == true);

    /// <summary>取第 index 个参数；没传就是 nil（不会抛越界）。</summary>
    private static DynValue Arg(CallbackArguments args, int index)
        => index >= 0 && index < args.Count ? args[index] : DynValue.NewNil();

    /// <summary>参数转字符串：nil → 空串，其它按 Lua 的显示规则（true / 42 / 表地址）。</summary>
    private static string ArgString(CallbackArguments args, int index)
    {
        DynValue value = Arg(args, index);
        if (value.IsNil()) return "";
        return value.Type == DataType.String ? value.String : value.ToPrintString();
    }

    /// <summary>参数转数字：数字直接用，数字字符串也认，其它一律 0。</summary>
    private static double ArgNumber(CallbackArguments args, int index)
    {
        DynValue value = Arg(args, index);
        if (value.Type == DataType.Number) return value.Number;
        if (value.Type == DataType.String
            && double.TryParse(value.String, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return number;
        }

        return 0;
    }

    /// <summary>
    /// 一次运行的共享状态：收集输出、提供取消信号、回填结果。
    ///
    /// 只有脚本线程写、调用方线程在 Join 之后读（Join 自带内存屏障），所以除了输出队列
    /// 之外都不需要加锁。
    /// </summary>
    private sealed class RunState
    {
        /* 收够这么多就不再往后堆：死循环里疯狂 ip.log 也不会把内存吃光。 */
        private const int OutputHardCap = MaxOutputChars + 2000;

        private readonly ConcurrentQueue<string> _lines = new();
        private readonly CancellationTokenSource _cancel = new();

        private int _usedChars;
        private bool _ok;
        private string _error = "";

        public RunState(string scriptName, ActionContext? ctx, Func<string, string, bool>? invoke, string arg)
        {
            ScriptName = scriptName ?? "";
            Ctx = ctx;
            Invoke = invoke;
            Arg = arg ?? "";
        }

        public string ScriptName { get; }
        public ActionContext? Ctx { get; }
        public Func<string, string, bool>? Invoke { get; }
        public string Arg { get; }

        /// <summary>调用方已经放弃这次运行（超时）。</summary>
        public bool Cancelled => _cancel.IsCancellationRequested;

        /// <summary>收一行输出。print 可能带换行，这里去掉尾部的换行，保证一行一条。</summary>
        public void Append(string? line)
        {
            string text = (line ?? "").TrimEnd('\r', '\n');
            int room = OutputHardCap - _usedChars;
            if (room <= 0) return;
            if (text.Length > room) text = text.Substring(0, room);
            _usedChars += text.Length + 1;
            _lines.Enqueue(text);
        }

        /// <summary>输出正文；超过 <see cref="MaxOutputChars"/> 截断并补一句提示。</summary>
        public string BuildOutput()
        {
            string output = string.Join("\n", _lines.ToArray());
            if (output.Length > MaxOutputChars)
            {
                output = output.Substring(0, MaxOutputChars) + "\n…（输出过长已截断）";
            }

            return output;
        }

        /// <summary>
        /// 睡一会儿，但随时能被取消叫醒。
        ///
        /// 两个上限：单次最多 TimeoutMs（ip.sleep(1e9) 不会把线程钉死），
        /// 累计到 TimeoutMs 就直接跳出（反正调用方已经放弃了）。分片 25ms 是为了
        /// 取消后能立刻返回，而不是等满整段时间。
        /// </summary>
        public void Sleep(double milliseconds)
        {
            double remaining = Math.Clamp(milliseconds, 0, TimeoutMs);
            var clock = Stopwatch.StartNew();

            while (remaining > 0 && !Cancelled)
            {
                int slice = (int)Math.Min(25, Math.Ceiling(remaining));
                _cancel.Token.WaitHandle.WaitOne(slice);
                remaining -= slice;
                if (clock.ElapsedMilliseconds >= TimeoutMs) break;
            }
        }

        /// <summary>调用方放弃这次运行：脚本线程的后续 API 全部空转。</summary>
        public void Abandon()
        {
            try { _cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>脚本线程写回结果（Ok / Error）。</summary>
        public void Complete(bool ok, string error)
        {
            _ok = ok;
            _error = error ?? "";
        }

        /// <summary>Join 成功之后由调用方线程取结果。</summary>
        public LuaRunResult BuildResult(double elapsedMs) => new()
        {
            Ok = _ok,
            Output = BuildOutput(),
            Error = _error,
            ElapsedMs = elapsedMs,
        };
    }
}
