using System.Runtime.InteropServices;
using InputPlus.Core.App;
using InputPlus.Core.Interop;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Input;

/// <summary>
/// 统一的输入注入器。所有“替用户按键”的动作都走这里，保证：
/// 1) 注入事件带标记，钩子会忽略自己注入的按键，避免自我触发；
/// 2) 修饰键状态被正确还原（按下后一定抬起）；
/// 3) 支持 Unicode 文本直接输入，中文/emoji 也能发。
/// </summary>
public static class InputSender
{
    /// <summary>注入事件附加标记，钩子里用 dwExtraInfo 判断。</summary>
    public static readonly IntPtr InjectionTag = new(0x434C5021); // "CLP!"

    private static readonly int InputSize = System.Runtime.InteropServices.Marshal.SizeOf<INPUT>();

    /// <summary>
    /// 干跑：只跑逻辑，不真的把按键发到系统里。
    ///
    /// 自检 / 离屏渲染时必须打开。这些流程会走「输入文本」「播放宏」
    /// 之类的真实动作 —— 一旦真发出去，就等于往用户当时的前台窗口里
    /// 打字（曾经在 `--selftest` 里打过两千个 a，还顺手打了一句 hi）。
    ///
    /// 放在这里而不是在每个调用点判断：注入只有这一个出口，
    /// 关掉出口就不可能漏，将来新增的测试也不会重犯。
    /// </summary>
    public static bool DryRun { get; set; }

    /// <summary>
    /// 最近一次注入失败的错误码（0 = 没失败过）。
    ///
    /// SendInput 在**前台窗口属于更高完整性级别**时会被 UIPI 拒绝（错误 5），
    /// 这跟"键盘钩子坏了"完全不是一回事。以前注入失败被静默吞掉，
    /// 于是自检判成"钩子收不到按键" → 重装 → 兜底接管，一路把正常状态搞坏。
    /// </summary>
    public static int LastInjectError { get; private set; }

    /// <summary>
    /// 最近一次真正注入按键的时间。
    ///
    /// 引擎的自愈逻辑要参考它：注入的按键会占用输入队列、也会让
    /// `GetAsyncKeyState` 在极短时间内读不准，此时绝不能判"触发键松开了"
    /// 去复位层 —— 那正是"第一次有效、第二次变回原生功能"的根因。
    /// </summary>
    public static DateTime LastInjectAt { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// 启动静默期：这段时间里不往系统里注入任何按键（钩子探针除外）。
    ///
    /// 为什么要它：有用户反馈"程序一启动就莫名打出一个字母 C / 像按着 Ctrl"。
    /// 我们查不到是哪条路径发的（日志里没有痕迹）。与其继续猜，不如在启动的
    /// 头两秒**物理上不许注入**，并且把"试图注入"记进日志 ——
    /// 真有问题时日志会直接指出是哪一次调用，没有问题则用户也看不到副作用。
    /// </summary>
    public static DateTime QuietUntil { get; set; } = DateTime.MinValue;

    /// <summary>注入审计：本次会话注入了几次（只用于日志与自检）。</summary>
    public static int InjectCount { get; private set; }

    /// <summary>注入序号：日志里带 #n，两个进程的日志交错时也能排出先后。</summary>
    public static long InjectSeq => Interlocked.Read(ref _injectSeq);

    private static long _injectSeq;

    /// <summary>本次会话最近一次注入的简述（诊断用）。</summary>
    public static string LastInjectSummary { get; private set; } = "";

    /// <summary>前 N 次注入写进日志，之后不再刷屏。</summary>
    private const int InjectLogLimit = 40;

    private static int _injectLogged;
    private static bool _quietBlockLogged;

    /// <summary>
    /// 当前这批事件是不是"从钩子宿主回放进来"的。
    ///
    /// 宿主内部也走 Send：必须让它直接发，否则会提升进程下沉 → 宿主
    /// 再下沉一次（宿主没有别的宿主可下沉），注入就丢了。
    /// </summary>
    [ThreadStatic] private static bool _fromHost;

    /// <summary>
    /// 最近一次注入被完整性级别挡住的原因（None = 没被挡）。
    ///
    /// 界面拿它决定要不要提示"目标窗口是管理员权限"。以前这种情况
    /// 只剩一个错误码 5，用户看到的是"按了没反应"。
    /// </summary>
    internal static InjectionBarrier LastInjectBarrier { get; private set; }

    /// <summary>
    /// `SendInput` 是否被环境拦掉了（受限环境 / 安全软件 / 会话隔离）。
    ///
    /// 为什么必须暴露：手势的"保住原生右键菜单"靠的是"吞掉点击 → 没成笔就补发一次点击"。
    /// 补发发不出去时，吞掉就等于把用户的点击吃掉 —— 用户看到的是"鼠标右键失效"。
    /// 一旦这个标记为真，<c>GestureService.OnButton</c> 会整下都不接管（fail-open）。
    ///
    /// v1.5.1 起它是**限时熔断**，不再是"一置真就永久"：
    /// 2026-11-23 09:45 真机重启后，开机那阵子的一次注入失败把这个标记永久置真，
    /// 之后整个会话里手势一条都不触发（诊断日志成串 "-> 补发不可用：按下原样放行"），
    /// 用户只能重启软件。**一次瞬时失败不该判无期** —— 现在到点自动重试，
    /// 补发一旦成功回到钩子就立刻解除（见 <see cref="ClearInjectionBlocked"/>）。
    /// </summary>
    public static bool InjectionBlocked => Environment.TickCount64 < Interlocked.Read(ref _blockedUntilMs);

    /* 熔断时长：15s → 30s → 60s → … 封顶 5 分钟。
     * 为什么逐次加长：真是"这台机器注不了入"的环境，重试太勤会每 15 秒吃掉一次点击；
     * 而开机的瞬时故障只需要一次 15 秒的等待。 */
    private static long _blockedUntilMs;
    private static int _blockStrikes;

    private const int BlockBaseMs = 15_000;
    private const int BlockMaxMs = 300_000;

    /// <summary>
    /// 由手势服务报告：补发出去的事件连续几次都没回到钩子 —— 这台机器上 SendInput
    /// 的结果不可信（安全软件 / 输入过滤驱动 / UIPI 会静默丢掉，但返回条数照旧）。
    ///
    /// 置真后一段时间里手势那边整下都不接管（fail-open）：宁可少一个手势，
    /// 也不能让右键/左键点不动；到点自动重试，不会永久失效。
    /// </summary>
    internal static void MarkInjectionBlocked(string reason) => Block(reason);

    /// <summary>
    /// 补发**真的**回到了钩子：立刻解除熔断并把连击清零（v1.5.1）。
    ///
    /// 这是自愈的那一半：开机时的瞬时故障只该让手势停十几秒；
    /// 只要有一次补发成功，就没有理由继续 fail-open。
    /// </summary>
    internal static void ClearInjectionBlocked(string reason)
    {
        if (Interlocked.Exchange(ref _blockedUntilMs, 0) == 0) return;
        Interlocked.Exchange(ref _blockStrikes, 0);
        CoreLog.Info("[注入] 补发已恢复到钩子，解除熔断（" + reason + "）");
    }

    private static void Block(string reason)
    {
        int strike = Interlocked.Increment(ref _blockStrikes);
        long ms = Math.Min(BlockMaxMs, (long)BlockBaseMs << Math.Min(strike - 1, 6));
        Interlocked.Exchange(ref _blockedUntilMs, Environment.TickCount64 + ms);
        CoreLog.Warn($"[注入] 补发不生效（{reason}）—— {ms / 1000}s 内手势不接管鼠标按键，之后自动重试（第 {strike} 次）");
    }


    private static bool Send(params INPUT[] inputs)
    {
        if (BeforeSendForTest is { } probe) probe();

        if (inputs.Length == 0 || DryRun) return true;

        if (DateTime.Now < QuietUntil)
        {
            if (!_quietBlockLogged)
            {
                _quietBlockLogged = true;
                CoreLog.Warn("[注入] 启动静默期内拦下一次注入（可能是这次启动的杂键来源）");
            }
            return true;
        }

        /* 管理员模式先把这批事件下沉给普通权限的钩子宿主（见
         * Core/App/ElevationState.cs 的 RouteFor）。为什么：钩子挂在哪个
         * 完整性级别，就只看得见那个级别以下的输入 —— 注入端必须和钩子端
         * 对齐，否则自己发的键会把自己再触发一次。 */
        bool forwarded = TryForwardToRelay(inputs);

        /* SendInput 会返回"真正插入队列的条数"。返回 0 通常就是 UIPI 拒绝
         * （前台窗口比我们完整性高，错误码 5），以前这里不看返回值，宏播放
         * 看起来"执行了"却什么都没发生。把错误留给 MacroPlayer 转成用户能
         * 看懂的提示，比静默失败强。 */
        uint sent = forwarded
            ? (uint)inputs.Length
            : SendInput((uint)inputs.Length, inputs, InputSize);
        if (!forwarded && sent != inputs.Length)
        {
            LastInjectError = Marshal.GetLastWin32Error();
            ReportInjectFailure(inputs.Length, sent, LastInjectError);

            /* SendInput 一个都没发出去 = 这个进程根本注不了入（受限环境 / 安全软件 / 会话隔离）。
             * 这个事实必须让手势那边知道：它还依赖"吞掉点击再补发"来保住原生右键菜单，
             * 而补发一旦发不出去，吞掉就等于把用户的点击吃掉了。
             * 标记之后 OnButton 会整下都不接管（fail-open），宁可少一个手势，也不能让键失效。 */
            if (sent == 0)
            {
                Block($"SendInput 发不出去（错误 {LastInjectError}）");
            }
            return false;
        }

        LastInjectError = 0;
        LastInjectBarrier = InjectionBarrier.None;
        LastInjectAt = DateTime.Now;
        Interlocked.Increment(ref _injectSeq);
        InjectCount++;

        if (_injectLogged < InjectLogLimit)
        {
            _injectLogged++;
            try
            {
                var parts = new List<string>();
                foreach (var input in inputs)
                {
                    if (input.type == INPUT_KEYBOARD)
                    {
                        int vk = (int)input.U.ki.wVk;
                        bool up = (input.U.ki.dwFlags & KEYEVENTF_KEYUP) != 0;
                        bool uni = (input.U.ki.dwFlags & KEYEVENTF_UNICODE) != 0;
                        parts.Add(uni ? $"U+{vk:X4}" : KeyName.NameOf(vk) + (up ? "↑" : "↓"));
                    }
                    else
                    {
                        parts.Add("mouse");
                    }
                }
                LastInjectSummary = string.Join(" ", parts);
                CoreLog.Info($"[注入#{Interlocked.Read(ref _injectSeq)}] {LastInjectSummary}");
            }
            catch { }
        }

        return true;
    }

    /// <summary>
    /// 管理员模式下把一批事件下沉给普通权限的钩子宿主。
    ///
    /// 返回 false 表示"不归它发"：未提升、下沉通道没接上、或者目标窗口本身
    /// 就是提升的（低 → 高，UIPI 必拦，只有提升的我们直接发才行）。
    /// </summary>
    private static bool TryForwardToRelay(INPUT[] inputs)
    {
        if (_fromHost) return false;
        if (!ElevationState.Elevated) return false;

        bool receiverElevated = ElevationState.ForegroundIsElevated();
        var route = ElevationState.ResolveRoute(true, receiverElevated, InjectionBridge.IsLive);
        if (route != InjectionRoute.HookHost) return false;

        bool ok = InjectionBridge.TryForward(inputs);
        if (ok)
        {
            LastInjectBarrier = InjectionBarrier.None;
            /* 每个键都是一次 SendInput，一次完整手势可能几十次 ——
             * 全量记日志会把 logs 撑爆，也拖慢热路径。头几次写清楚，之后每 200 次报一声。 */
            if (RelayLogDue(InjectionBridge.ForwardedCount))
            {
                CoreLog.Info($"[注入] 已下沉给钩子宿主（累计 {InjectionBridge.ForwardedCount} 次）");
            }
        }
        else if (RelayLogDue(InjectionBridge.ForwardFailedCount))
        {
            CoreLog.Warn("[注入] 下沉失败（钩子宿主不可用），退回本进程发送");
        }
        return ok;
    }

    /// <summary>日志节流：前 5 次每次记，之后每 200 次记一次（热路径不能狂写盘）。</summary>
    private static bool RelayLogDue(int count) => count <= 5 || count % 200 == 0;

    /// <summary>
    /// 宿主侧回放入口：这些事件来自提升进程的主程序，由宿主代替它 SendInput。
    ///
    /// 宿主自己也是"普通权限进程"，所以它发的键在普通程序（浏览器、编辑器）
    /// 眼里就是正常输入 —— 这正是下沉的意义。
    /// </summary>
    internal static bool SendFromHost(INPUT[] inputs)
    {
        bool previous = _fromHost;
        _fromHost = true;
        try { return Send(inputs); }
        finally { _fromHost = previous; }
    }

    /// <summary>注入失败的统一记账：错误码 + 是否被完整性级别挡住（界面要用）。</summary>
    private static void ReportInjectFailure(int total, uint sent, int error)
    {
        var barrier = ElevationState.BarrierFor(ElevationState.Elevated, ElevationState.ForegroundIsElevated());
        LastInjectBarrier = barrier;
        CoreLog.Warn($"[注入] SendInput 失败：写入 {sent}/{total}，错误 {error}");
        if (barrier != InjectionBarrier.None)
        {
            CoreLog.Warn("[注入] 原因：" + ElevationState.BarrierText(barrier));
        }
        else if (error == 5)
        {
            CoreLog.Warn("[注入] 原因：目标窗口拒绝输入（UIPI 拦截，错误 5）");
        }
    }

    /// <summary>宏播放前清掉上一次的注入错误，避免旧错误误伤本次。</summary>
    public static void ResetInjectError() => LastInjectError = 0;

    /// <summary>
    /// 当前前台窗口是否属于更高完整性级别（管理员进程）。
    /// UIPI 下 SendInput 可能直接返回成功但按键被丢弃，先发一个 WM_NULL 探路：
    /// 被 UIPI 拦下时 SendMessageTimeout 返回 0 且 LastError=5。
    /// </summary>
    public static bool IsForegroundHigherIntegrity()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        var ok = NativeMethods.SendMessageTimeoutW(hwnd, 0, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK, 80, out _);
        return ok == IntPtr.Zero && Marshal.GetLastWin32Error() == 5;
    }

    private static INPUT KeyInput(int vk, bool up, bool unicode = false, ushort scan = 0)
    {
        uint flags = 0;
        if (up) flags |= KEYEVENTF_KEYUP;
        if (unicode) flags |= KEYEVENTF_UNICODE;

        /* ⚠ 这里**不能**加 KEYEVENTF_SCANCODE。
         *
         * 一旦指定 KEYEVENTF_SCANCODE，系统就只按 wScan 投递、忽略 wVk，
         * 目标程序拿到的按键信息不完整 —— Chromium / Qt 系程序
         * （Chrome、Edge、微信）会判定"这不是一次正常快捷键"而不复制。
         *
         * 正确做法：**同时**填 wVk 和 wScan，但**不带** SCANCODE 标志。
         * 系统按虚拟键走正常投递路径，wScan 作为附带信息一并送达，
         * 需要看扫描码的程序同样能认出来。
         *
         * 扩展键必须带 KEYEVENTF_EXTENDEDKEY，否则方向键、Home/End、
         * Ins/Del、小键盘会被"按扫描码识别按键"的程序当成小键盘变体。 */
        /* ⚠ Unicode 模式下 vk 参数其实是**字符码**，不是虚拟键码，
         * 绝不能按虚拟键的规则判断"是不是扩展键"。
         *
         * 否则会踩一串隐蔽的坑（IsExtendedKey 的判定值域和 ASCII 完全重叠）：
         *   '.' = 0x2E → 被当成 Delete
         *   'o' = 0x6F → 被当成小键盘 /
         *   ',' = 0x2C → 被当成 PrintScreen
         *   '!' = 0x21 → 被当成 PgUp ……
         * 加了 EXTENDEDKEY 之后目标程序收到的就不是这个字符，
         * 表现是**凭空少字**：输入邮箱 `@qq.com` 会变成 `@qqcm`。
         * 这正是"邮箱输不全"的根因。 */
        if (!unicode && IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

        /* 扫描码：不传就自己查一个。
         *
         * 为什么不能留 0：靠**扫描码**认键的程序拿到 wScan=0 的合成按键会直接
         * 认不出来。RustDesk 这类远程桌面客户端就是典型 —— 它把本地按键的
         * 扫描码原样转给被控端，扫描码为 0 的按键只有一部分能落地：
         * 实测 14 个字符的密码，落进去的正好是**不需要 Shift 的那 7 个**，
         * 大写字母与符号（要 Shift 的）全丢 —— 用户看到的就是
         * "明明 14 位，只输了 11 位，密码框报大小写/长度不对"。
         *
         * 同一个坑在 Ctrl+C 上已经踩过一次（见 CopyChord 的注释），
         * 那次只修了复制；凡是"要目标程序认出这是一个正常按键"的场合，
         * 扫描码都不能缺。所以放在这里统一补，而不是每个调用点各写一遍。
         *
         * Unicode 模式例外：那时 vk 是字符码，不是虚拟键码，查不了也不该查。 */
        if (!unicode && scan == 0) scan = ScanOf(vk);

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)(unicode ? 0 : vk),
                    wScan = unicode ? (ushort)vk : scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = InjectionTag,
                },
            },
        };
    }

    /// <summary>
    /// 该虚拟键是否属于"扩展键"（需要 KEYEVENTF_EXTENDEDKEY 的那批）。
    ///
    /// 判定规则：VK &gt;= 0x21 且**不在**主键盘区的那些导航 / 编辑键，
    /// 加上小键盘（NumLock 关闭时它们是方向/编辑语义）、右边的修饰键、
    /// Break / PrintScreen。
    /// </summary>
    private static bool IsExtendedKey(int vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 => true,   // PgUp/PgDn/End/Home
        0x25 or 0x26 or 0x27 or 0x28 => true,   // ←↑→↓
        0x2D or 0x2E => true,                   // Insert/Delete
        0xAD or 0xAE or 0xAF => true,           // 静音/音量-/音量+（多媒体键，扫描码带 E0）
        0xB0 or 0xB1 or 0xB2 or 0xB3 => true,   // 下一曲/上一曲/停止/播放暂停
        0x5B or 0x5C => true,                   // LWin/RWin
        0x6F => true,                           // Numpad /（数字Enter/小键盘运算符在部分布局也是）
        0x90 or 0x91 => true,                   // NumLock/ScrollLock
        0xA0 or 0xA1 or 0xA3 or 0xA5 => true,   // RShift/LControl(极少数布局)/RControl/RAlt
        0x13 => true,                           // Pause
        0x2C => true,                           // PrintScreen
        _ => false,
    };

    /// <summary>按下或抬起一个虚拟键。</summary>
    /// <summary>
    /// 自检用：替换"真实注入"。默认 null = 正常发键。
    /// 有了它才能验证改键的按下/抬起是否成对 —— 否则自检会真的往系统里打字。
    /// </summary>
    internal static Action<int, bool>? SenderForTest { get; set; }

    /// <summary>
    /// 自检用：真正调用 SendInput **之前**执行一次。
    /// 低级钩子是在 SendInput 内部同步回调的，自检要复现"注入的事件当场回到钩子"
    /// 这个时序，才能验证"记账早于发送"（见鼠标改键的串键回归）。
    /// </summary>
    internal static Action? BeforeSendForTest { get; set; }

    /* ---- 修饰键账本 ----
     *
     * 记录"我们发过按下、还没发抬起"的修饰键。用途只有一个：出现任何意外
     * （异常、动作被取消、进程即将退出）时能把这些键**主动松开**。
     * 只记修饰键：它们粘住的后果最严重（整机每个键都变成快捷键），
     * 而普通字符键粘住最多多一个字母。
     */
    private static readonly Dictionary<int, long> HeldModifiers = new();
    private static readonly object HeldGate = new();

    /// <summary>当前还欠着抬起的修饰键数量（诊断 / 自检用）。</summary>
    public static int TrackedModifierCount
    {
        get { lock (HeldGate) return HeldModifiers.Count; }
    }

    /* ---- 最近注入时刻（防回声） ----
     *
     * 有些输入工具（手势、宏、剪贴板增强）会把我们注入的事件**原样复制**再发一遍，
     * 复制时 dwExtraInfo 标记会丢 —— 我们自己再看就是"用户真的按下了这个键"。
     * 改键遇到这种情况会自我循环（Ctrl→Alt→Ctrl→…）。
     *
     * 这里记下"每个键最近一次由我们注入的时刻"，改键遇到 30ms 内的同一个键
     * 一律当成回声，直接放行不处理。
     */
    private static readonly Dictionary<int, long> InjectedAt = new();
    private static readonly object InjectedGate = new();

    /* 「还没回来的注入」账本。
     *
     * 30ms 的时间窗对键盘够用（注入是同步回调的），对鼠标却不一定：
     * 鼠标驱动常驻软件会把 SendInput 的事件截下来、抹掉 dwExtraInfo 再重新发一遍，
     * 这个来回可能几十毫秒；期间界面线程要是正忙（播放宏、画 OSD），钩子排队更久，
     * 30ms 就漏了 —— 表现正是"按侧键跑了中键的宏"。
     *
     * 所以改成配对记账：每注入一个键记一笔，等它回到钩子时销账；
     * 带标记回来（正常情况）销一笔，不带标记回来（驱动抹了标记）也销一笔并放行。
     * 只有账上确实有"还没回来的注入"时才拦，不会误伤正常的连按。 */
    private const int EchoWindowMs = 1500;
    private const int EchoPendingMax = 4;
    private static readonly Dictionary<int, Queue<long>> InjectedPending = new();

    /* 「最近一次看到这个键的注入回声」的时刻。
     *
     * 配对记账只管"账上还有没有没回来的注入"。但现场实测（CLP_MOUSE_LOG=1）会看到
     * 这样的序列：带标记的中键回来（销账）→ 4ms 后系统里又冒出一颗**没有标记**的中键。
     * 来源是第三方鼠标工具（WGestures、厂商驱动、滚轮增强那类常驻进程）：它们把
     * SendInput 的事件复制一份、抹掉 dwExtraInfo 再发出来。
     *
     * 账已经被带标记的那一份销掉了，副本就没人认，于是被当成"用户真按了中键"，
     * 中键上的宏再跑一遍 —— 用户看到的就是"按一次侧键，既点了中键又跑了宏"。
     * 所以再记一个"刚见过回声"的时间戳，短窗内同键的无标记事件按副本处理。 */
    private static readonly Dictionary<int, long> EchoSeenAt = new();

    /// <summary>这个键是不是刚刚由我们注入过（用来识别回声）。</summary>
    public static bool WasInjectedRecently(int vk, int withinMs = 30)
    {
        lock (InjectedGate)
        {
            if (!InjectedAt.TryGetValue(vk, out long at)) return false;
            return Environment.TickCount64 - at <= withinMs;
        }
    }

    /// <summary>
    /// 这个键最近一次"带标记的注入回声"是不是刚过去 <paramref name="withinMs"/> 毫秒。
    ///
    /// 专用在"副本"上：第三方鼠标工具会把我们注入的事件复制一份、抹掉标记再发一遍。
    /// 带标记的那一份已经把配对账销掉了，副本只能靠这个时间戳认。
    /// </summary>
    /* “改键回声”通行证：把侧键换成中键之后，那颗中键是我们注入的，
     * 但用户确实按了鼠标 —— 手势服务默认跳过注入事件，这里给它开一张
     * 一次性的票，让这次中键仍然能当手势起点。 */
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> GesturePass = new();

    public static void AllowGesturePass(int vk, int withinMs = 1200)
    {
        if (vk == 0) return;
        GesturePass[vk] = Environment.TickCount64 + Math.Max(100, withinMs);
        if (GesturePass.Count > 32) GesturePass.Clear();
    }

    public static bool TakeGesturePass(int vk)
    {
        if (!GesturePass.TryRemove(vk, out long until)) return false;
        return Environment.TickCount64 <= until;
    }

    public static bool WasEchoSeenRecently(int vk, int withinMs)
    {
        lock (InjectedGate)
        {
            if (!EchoSeenAt.TryGetValue(vk, out long at)) return false;
            return Environment.TickCount64 - at <= withinMs;
        }
    }

    /// <summary>账上还有几笔"注入后没回来"的记录（诊断用）。</summary>
    public static int PendingEchoCount(int vk)
    {
        lock (InjectedGate)
        {
            return InjectedPending.TryGetValue(vk, out var q) ? q.Count : 0;
        }
    }

    /// <summary>
    /// 我们注入的键回到了钩子（带标记）：把对应的"待回声"账销掉。
    ///
    /// 不销账的话，之后用户真的按一次这个键会被当成回声吞掉一次 ——
    /// 而正常情况（标记没丢）恰恰是最常见的路径。
    /// </summary>
    public static void MarkInjectedEcho(int vk)
    {
        lock (InjectedGate)
        {
            long now = Environment.TickCount64;
            if (EchoSeenAt.Count > 128) EchoSeenAt.Clear();
            EchoSeenAt[vk] = now;

            if (!InjectedPending.TryGetValue(vk, out var q) || q.Count == 0) return;
            q.Dequeue();
            if (q.Count == 0) InjectedPending.Remove(vk);
        }
    }

    /// <summary>
    /// 这个键带着"没有注入标记"回来，但账上还有未销的注入 —— 判定为回声。
    ///
    /// 认了就从账上销一笔，返回 true（调用方放行，不参与改键 / 替换层）。
    /// 账上没有就返回 false，让真正的按键照常走规则。
    /// </summary>
    public static bool ConsumeInjectedEcho(int vk, int withinMs = EchoWindowMs)
    {
        lock (InjectedGate)
        {
            if (!InjectedPending.TryGetValue(vk, out var q) || q.Count == 0) return false;

            long now = Environment.TickCount64;
            while (q.Count > 0 && now - q.Peek() > withinMs) q.Dequeue();
            if (q.Count == 0)
            {
                InjectedPending.Remove(vk);
                return false;
            }

            q.Dequeue();
            if (q.Count == 0) InjectedPending.Remove(vk);
            return true;
        }
    }

    private static bool IsModifierVk(int vk) => vk is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or      // 通用码 + Win
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5; // 左右码

    public static void KeyDown(int vk)
    {
        SendKey(vk, up: false);
        if (!IsModifierVk(vk)) return;
        lock (HeldGate) HeldModifiers[vk] = Environment.TickCount64;
    }

    public static void KeyUp(int vk)
    {
        SendKey(vk, up: true);
        if (!IsModifierVk(vk)) return;
        lock (HeldGate) HeldModifiers.Remove(vk);
    }

    /// <summary>这个修饰键当前是不是"我们注入按下、还没抬起"的状态。</summary>
    public static bool IsHeldByUs(int vk)
    {
        lock (HeldGate) return HeldModifiers.ContainsKey(vk);
    }

    /// <summary>
    /// 纠偏判定：只有**我们自己按下却没抬起**的修饰键才允许自动松手。
    ///
    /// 以前只看"系统说它按着 + 引擎没收到真实按下"——用户自己按住的 Ctrl/Alt
    /// 一旦因为钩子重装、宿主机掉线而没进 _physDown，就会被当成漏抬起**强制松开**，
    /// 于是正在进行的 Ctrl+C（手势工具、复制粘贴）当场失效。
    /// </summary>
    public static bool ShouldAutoRelease(int vk, bool physicallyDown)
        => !physicallyDown && IsHeldByUs(vk);

    /// <summary>把这些修饰键全部松开（退出 / 暂停 / 钩子丢失时用）。</summary>
    public static int ReleaseTrackedModifiers()
    {
        int[] keys;
        lock (HeldGate)
        {
            if (HeldModifiers.Count == 0) return 0;
            keys = HeldModifiers.Keys.ToArray();
            HeldModifiers.Clear();
        }

        foreach (int vk in keys)
        {
            try { SendKey(vk, up: true); }
            catch (Exception ex) { CoreLog.Swallow(ex, "松开修饰键"); }
        }
        return keys.Length;
    }

    /// <summary>
    /// 修饰键按住超过 <paramref name="maxAgeMs"/> 且最近没有任何注入 →
    /// 判定为"漏了抬起"，直接松开并记日志。
    ///
    /// 用途：兜住"某个动作抛异常 / 线程被结束 / 队列被丢弃"导致的目标键悬空。
    /// 正在播放宏时不调用（宏可以合法地按住修饰键很久）。
    /// </summary>
    public static int ReleaseStaleModifiers(int maxAgeMs = 10_000)
    {
        List<int>? stale = null;
        long now = Environment.TickCount64;

        lock (HeldGate)
        {
            if (HeldModifiers.Count == 0) return 0;

            foreach (var pair in HeldModifiers.ToArray())
            {
                if (now - pair.Value < maxAgeMs) continue;
                (stale ??= new List<int>()).Add(pair.Key);
                HeldModifiers.Remove(pair.Key);
            }
        }

        if (stale == null) return 0;

        foreach (int vk in stale)
        {
            try { SendKey(vk, up: true); }
            catch (Exception ex) { CoreLog.Swallow(ex, "松开超时修饰键"); }
        }

        CoreLog.Warn($"[注入] 修饰键按下超过 {maxAgeMs / 1000}s，已补发抬起：{string.Join(",", stale.Select(KeyName.NameOf))}");
        return stale.Count;
    }

    private static void SendKey(int vk, bool up)
    {
        if (SenderForTest is { } spy) { spy(vk, up); return; }

        /* 与鼠标走同一本账：注入时记一笔（InjectedAt + 待回声队列），
         * 回来的那一颗再由钩子销账。键盘回声一般是同步的，但这本账
         * 也给"别的工具复制键盘事件"留了兜底。 */
        NoteInjected(vk);
        Send(KeyInput(vk, up));
    }

    /// <summary>
    /// 敲一个**不带本程序标记**的键（dwExtraInfo = 0）。
    ///
    /// 只给"钩子自检"用：普通注入带 <see cref="InjectionTag"/>，会被钩子当成
    /// "自己发的"放行 —— 那样探不到"钩子到底还能不能收键"。
    /// 自检键固定用 F24（0x87）：常规程序不认这个键，按下去不会有副作用。
    /// </summary>
    public static bool TapUntagged(int vk)
    {
        if (DryRun) return false;

        var down = KeyInput(vk, up: false);
        down.U.ki.dwExtraInfo = IntPtr.Zero;     // 关键：不带标记
        if (SendInput(1, new[] { down }, InputSize) == 0)
        {
            LastInjectError = Marshal.GetLastWin32Error();
            return false;
        }

        System.Threading.Thread.Sleep(12);

        var upInput = KeyInput(vk, up: true);
        upInput.U.ki.dwExtraInfo = IntPtr.Zero;
        if (SendInput(1, new[] { upInput }, InputSize) == 0)
        {
            LastInjectError = Marshal.GetLastWin32Error();
            return false;
        }

        LastInjectAt = DateTime.Now;
        return true;
    }

    /// <summary>完整敲击一个虚拟键（按下+抬起）。鼠标键 0x01~0x06 走鼠标注入。</summary>
    public static void Tap(int vk)
    {
        if (IsMouseVk(vk))
        {
            Press(vk, down: true);
            Press(vk, down: false);
            return;
        }
        Send(KeyInput(vk, false), KeyInput(vk, true));
    }

    /// <summary>
    /// 补发一次鼠标点击，按下与抬起之间留 <paramref name="holdMs"/> 毫秒。
    ///
    /// 为什么不能像键盘那样「按下 + 抬起」一拍发完：真实的 mouse click 从按下到抬起
    /// 通常有 40~150ms，而在同一批 SendInput 里发完的点击，Explorer 桌面、Chromium
    /// 这类目标会当成一次合成事件、跳过它的默认动作 —— 右键不弹菜单、中键不开新标签页，
    /// 都是这么来的（用户看到的就是「鼠标键失效」）。
    /// 补发走的是线程池线程，睡这几十毫秒不影响钩子回调。
    /// </summary>
    public static void TapMouseWithHold(int vk, int holdMs = 40)
    {
        if (!IsMouseVk(vk)) { Tap(vk); return; }
        if (SenderForTest is { } spy) { spy(vk, false); spy(vk, true); return; }

        SendMouseAtCursor(vk, down: true);
        try
        {
            int wait = Math.Clamp(holdMs, 0, 200);
            if (wait > 0) Thread.Sleep(wait);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "补发鼠标点击：按住时间"); }
        SendMouseAtCursor(vk, down: false);
    }

    /// <summary>
    /// 补发用的鼠标键：**带上绝对坐标**（整个虚拟桌面）。
    ///
    /// 这是照 WGestures 2 的做法（它的补发就是这么发的）：目标程序拿到的
    /// 是一条"指着某个屏幕坐标的按键事件"，而我们的老写法只发一个裸的
    /// RIGHTDOWN —— 坐标靠系统取当前光标，Explorer 桌面 / Chromium 这类目标
    /// 会当成合成事件、跳过默认动作（右键不弹菜单、中键不开新标签页）。
    /// 顺带把 XBUTTON 的 mouseData 一起带上，侧键补发才不会变成"XBUTTON1"。
    /// </summary>
    private static void SendMouseAtCursor(int vk, bool down)
    {
        uint flag = down
            ? vk switch
            {
                0x01 => MOUSEEVENTF_LEFTDOWN,
                0x02 => MOUSEEVENTF_RIGHTDOWN,
                0x04 => MOUSEEVENTF_MIDDLEDOWN,
                0x05 or 0x06 => MOUSEEVENTF_XDOWN,
                _ => 0u,
            }
            : vk switch
            {
                0x01 => MOUSEEVENTF_LEFTUP,
                0x02 => MOUSEEVENTF_RIGHTUP,
                0x04 => MOUSEEVENTF_MIDDLEUP,
                0x05 or 0x06 => MOUSEEVENTF_XUP,
                _ => 0u,
            };
        if (flag == 0) return;

        int dx = 0, dy = 0;
        uint position = 0;
        try
        {
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
            {
                /* 绝对坐标按"虚拟桌面"换算：多显示器时只用主屏尺寸会把右屏的点击
                 * 落回左屏，补发的点击就点到了别的地方。 */
                int vx = NativeMethods.GetSystemMetrics(76);   // SM_XVIRTUALSCREEN
                int vy = NativeMethods.GetSystemMetrics(77);   // SM_YVIRTUALSCREEN
                int vw = NativeMethods.GetSystemMetrics(78);   // SM_CXVIRTUALSCREEN
                int vh = NativeMethods.GetSystemMetrics(79);   // SM_CYVIRTUALSCREEN
                if (vw > 1 && vh > 1)
                {
                    dx = (int)Math.Round((pt.X - vx) * 65535.0 / (vw - 1));
                    dy = (int)Math.Round((pt.Y - vy) * 65535.0 / (vh - 1));
                    position = 0x0001u /*MOVE*/ | 0x8000u /*ABSOLUTE*/ | 0x4000u /*VIRTUALDESK*/;
                }
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "补发鼠标：取绝对坐标"); }

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = vk switch { 0x05 => 1u, 0x06 => 2u, _ => 0u },
                    dwFlags = flag | position,
                    dwExtraInfo = InjectionTag,
                },
            },
        };
        /* 记账要早于 Send：低级钩子会在 SendInput 内部同步回调（见 MouseDown 那段）。 */
        NoteInjected(vk);
        Send(input);
    }

    /// <summary>敲击一个虚拟键 N 次（等同于 SendInput {Left 5}）。</summary>
    public static void Tap(int vk, int times)
    {
        if (times <= 0) return;
        if (IsMouseVk(vk))
        {
            for (int i = 0; i < times; i++) Tap(vk);
            return;
        }
        var arr = new INPUT[times * 2];
        for (int i = 0; i < times; i++)
        {
            arr[i * 2] = KeyInput(vk, false);
            arr[i * 2 + 1] = KeyInput(vk, true);
        }
        Send(arr);
    }

    /// <summary>这个虚拟键码是不是鼠标按键（左/右/中/侧 1/侧 2）。</summary>
    public static bool IsMouseVk(int vk)
        => vk is >= VK.LButton and <= 0x06 or VK.WheelLeft or VK.WheelRight;

    /// <summary>按住一组修饰键，执行动作，再按相反顺序释放。</summary>
    public static void WithModifiers(IEnumerable<int> modifiers, Action action)
    {
        var mods = modifiers.Distinct().ToList();
        foreach (var m in mods) KeyDown(m);
        try
        {
            action();
        }
        finally
        {
            for (int i = mods.Count - 1; i >= 0; i--) KeyUp(mods[i]);
        }
    }

    public static void Chord(int modifier, int vk)
    {
        KeyDown(modifier);
        try { Tap(vk); }
        finally { KeyUp(modifier); }
    }

    public static void Ctrl(int vk) => Chord(VK.Control, vk);
    public static void Shift(int vk) => Chord(VK.Shift, vk);
    public static void CtrlShift(int vk)
    {
        KeyDown(VK.Control);
        KeyDown(VK.Shift);
        try { Tap(vk); }
        finally
        {
            KeyUp(VK.Shift);
            KeyUp(VK.Control);
        }
    }

    /// <summary>
    /// 发一次 Ctrl+&lt;vk&gt;，但**带扫描码、分四次发送、每次间隔 8ms**。
    ///
    /// 为什么不能直接用 <see cref="Ctrl"/>：那条路径的 <c>wScan</c> 是 0，
    /// 而 Chromium（Chrome / Edge）与 Qt（微信 4.x）这类程序在处理快捷键时
    /// 会看扫描码 —— 留 0 的合成按键有被忽略的风险，表现就是
    /// "在浏览器里选中文字后按复制，程序收不到"。
    ///
    /// 另外四个按键必须分开发：塞在同一次 SendInput 里，
    /// 部分程序的按键队列会漏掉后半截（Ctrl 按下但 C 没到）。
    /// </summary>
    /// <param name="vk">与 Ctrl 组合的主键</param>
    /// <param name="gapMs">每次发送之间的间隔（毫秒）</param>
    public static void CopyChord(int vk, int gapMs = 8)
    {
        ushort ctrlScan = ScanOf(VK.Control);
        ushort keyScan = ScanOf(vk);

        void Send(int k, ushort scan, bool up)
        {
            var input = KeyInput(k, up, unicode: false, scan: scan);
            if (!DryRun) SendInput(1, new[] { input }, InputSize);
            if (gapMs > 0) Thread.Sleep(gapMs);
        }

        Send(VK.Control, ctrlScan, up: false);
        try
        {
            Send(vk, keyScan, up: false);
            Send(vk, keyScan, up: true);
        }
        finally
        {
            /* 无论如何都要把 Ctrl 抬起来：漏掉这一下，用户会看到
             * "程序像是一直按着 Ctrl"，随后点什么都变成快捷键。 */
            Send(VK.Control, ctrlScan, up: true);
        }
    }

    /// <summary>取虚拟键对应的扫描码（取不到则返回 0，退回不带扫描码的行为）。</summary>
    private static ushort ScanOf(int vk)
    {
        try
        {
            ushort scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
            return (ushort)(scan & 0xFF);
        }
        catch { return 0; }
    }

    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>文本输入方式。</summary>
    public enum TextMode
    {
        /// <summary>
        /// 自动（默认）：长文本走剪贴板粘贴；短文本纯 ASCII 用**真实按键**逐字敲，
        /// 其它用 Unicode 注入。
        ///
        /// 为什么短文本用"真实按键"：密码框（浏览器、网银控件、部分安全软件）
        /// 会过滤掉 <c>KEYEVENTF_UNICODE</c> 这种"没有扫描码"的合成输入 ——
        /// 表现就是"文字打不进去，但普通输入框正常"。真实按键带完整扫描码，
        /// 和手打没有区别，这类框都认。
        ///
        /// 为什么长文本改用剪贴板：逐字注入在长文本上会被目标程序的输入吞吐
        /// 卡住，实测会零星丢字/错字符（见 <see cref="ClipboardPaster"/>）。
        ///
        /// ⚠ 输入法开着时短 ASCII 也改走 Unicode：软件不再为了敲几个字母
        /// 去切用户的输入语言（见 <see cref="TryTextAsKeys"/>）。
        /// </summary>
        Auto,

        /// <summary>
        /// 强制用真实按键（受键盘布局与输入法影响，但最像手打）。
        ///
        /// 注意它**不切换输入语言**：输入法开着时字母仍可能被拼成候选，
        /// 想稳一点就用 Auto（会自动退回 Unicode）。
        /// </summary>
        Keys,

        /// <summary>强制 Unicode 注入（不经过布局与输入法，中文/emoji 用它）。</summary>
        Unicode,
    }

    /// <summary>输入任意文本。</summary>
    public static void Text(string text, TextMode mode = TextMode.Auto)
    {
        if (string.IsNullOrEmpty(text)) return;

        /* 长文本改走剪贴板粘贴。
         *
         * 逐字注入在长文本上会被目标程序的输入吞吐卡住 —— 实测 Win11 记事本
         * 86 字一批能丢 10%，同一段文字在**英文布局**下也照样丢，所以这不是
         * 输入法的问题，是那条通道本身的上限。剪贴板粘贴一次完成，长度无关。
         *
         * 只改 Auto（默认）这一档：显式选了 Keys / Unicode 的用户是有具体理由的
         * （密码框不许粘贴、目标程序不认 Ctrl+V），不擅自把他们选的通道换掉。
         * 短文本也不受影响，仍然走原来的路 —— 那条路不碰剪贴板。
         *
         * TryPaste 返回 false（剪贴板拿不全 / 写不进去 / 不在 STA 线程）时
         * 原路继续，一个字都不会少。 */
        if (mode == TextMode.Auto && ShouldUseClipboard(text) && ClipboardPaster.TryPaste(text)) return;

        /* 通道选择：
         *   · Auto（默认）：输入法没开时，短 ASCII 走真实按键 —— 密码框那类
         *     不认 Unicode 注入的控件只认它；输入法开着就自动退回 Unicode，
         *     绝不为了敲几个字母去动用户的输入法；
         *   · Keys：同样走真实按键。原来这里会先把目标窗口的输入语言切到英文，
         *     那条路已经删掉 —— 现在它和 Auto 的区别只剩"不先在剪贴板/Unicode
         *     里挑"，能不能真敲出来取决于输入法状态（敲不出来就退回 Unicode）。 */
        if (mode == TextMode.Keys || (mode == TextMode.Auto && LooksLikePlainAscii(text)))
        {
            if (TryTextAsKeys(text)) return;

            /* 按键模拟不可用（输入法开着 / 布局映射不出来 / 没有前台窗口）时退回 Unicode。
             * 强制 Keys 也一样退回 —— 什么都不输入比输入得"不够完美"更糟。 */
        }

        TextUnicode(text);
    }

    /// <summary>
    /// 这段文字够不够长、值得改走剪贴板粘贴。
    ///
    /// 只看长度：换行、中文、emoji 在剪贴板路径里都没有额外风险，
    /// 反而是逐字注入更难处理的那几种。
    /// </summary>
    internal static bool ShouldUseClipboard(string text)
        => text.Length >= ClipboardPaster.Threshold;

    /// <summary>
    /// 这段文字适不适合用"真实按键"逐字敲。
    ///
    /// 只放行可打印 ASCII（0x20~0x7E）且不含换行：
    ///   · 中文/emoji 必须走 Unicode（没有对应的按键）；
    ///   · 换行会变成 Enter，可能把表单提交掉 —— 多行一律走 Unicode 粘贴式注入。
    /// </summary>
    public static bool LooksLikePlainAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char ch in text)
            if (ch < 0x20 || ch > 0x7E) return false;
        return true;
    }

    // ---------------- 大写锁定 ----------------

    /// <summary>
    /// 大写锁定当前是否开启。
    ///
    /// 必须用 <c>GetKeyState</c>：它的最低位是真正的 toggle 位。
    /// <c>GetAsyncKeyState</c> 的最低位是"上次调用之后按过没有"，
    /// 连读两次第二次就变成 false —— 拿它当开关用会时对时错。
    /// </summary>
    internal static bool CapsLockOn => (GetKeyState(VK.CapsLock) & 1) != 0;

    /// <summary>
    /// 这个字符是不是"大写锁定会翻转 Shift 语义"的键（A–Z / a–z）。
    ///
    /// 大写锁定只影响**字母键**：Caps + 1 还是 1，Caps + Shift + 1 还是 !。
    /// 所以数字与符号一律不参与翻转 —— 这也是判断规则必须按"字符是不是字母"
    /// 而不是"这个键有没有 Shift 位"的原因（'!' 同样需要 Shift，但不翻转）。
    ///
    /// 本类只处理可打印 ASCII（见 <see cref="LooksLikePlainAscii"/>），
    /// 判断 ASCII 字母就够了，不必操心非拉丁字母布局。
    /// </summary>
    internal static bool IsCapsSensitive(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    /// <summary>
    /// 实际要不要按 Shift = 布局给出的 Shift ⊕ 大写锁定（只对字母生效）。
    /// </summary>
    internal static bool NeedsShift(bool shiftFromLayout, bool capsSensitive, bool capsOn)
        => shiftFromLayout ^ (capsSensitive && capsOn);

    /// <summary>
    /// 自检用：按英文（美国）布局把一段 ASCII 算成"每个字符要不要按 Shift"。
    /// 返回 null = 布局取不到或字符映射不了（极少数环境），调用方跳过断言。
    /// </summary>
    internal static bool[]? ShiftPlanForTest(string text, bool capsOn)
    {
        IntPtr layout = ImeGuard.EnglishLayout;
        if (layout == IntPtr.Zero) return null;

        var result = new bool[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            short scan = VkKeyScanEx(text[i], layout);
            if (scan == -1) return null;
            bool shiftFromLayout = (((scan >> 8) & 0xFF) & 0x01) != 0;
            result[i] = NeedsShift(shiftFromLayout, IsCapsSensitive(text[i]), capsOn);
        }
        return result;
    }

    /// <summary>
    /// 用真实按键逐字敲一段纯 ASCII。返回 false = 这条路走不通，调用方应改用 Unicode。
    ///
    /// 关键点：
    ///   1. 用**前台窗口所在线程的键盘布局**做映射（<c>VkKeyScanEx</c>），
    ///      否则德语键盘上打 `@` 会出错；
    ///   2. 需要 Ctrl/Alt 才能打出的字符直接放弃（会干扰系统快捷键）；
    ///   3. 输入语言是中文/日文/韩文/越南文时字母会被输入法拼成候选 ——
    ///      默认**放弃真实按键**，让调用方走 Unicode（不经过布局与输入法，
    ///      一样能落字，而且完全不碰用户的输入法）；
    ///      只有显式打开 <see cref="ForceKeyTyping"/>（"目标程序不认 Unicode 注入"）
    ///      时才继续；
    ///   4. 老式 IMM32 程序额外摘一次输入法上下文 —— **只在 ForceKeyTyping 时做**，
    ///      因为"摘上下文"本身也是改动这个窗口的输入法状态；
    ///   5. **大写锁定开着时字母的 Shift 语义是反的**，每批发送前把
    ///      「布局给的 Shift」与「CapsLock」异或一次，否则整串字母的大小写
    ///      会对调（见 <see cref="NeedsShift"/>）。
    ///
    /// ⚠ **绝不切换输入语言**：老做法是 WM_INPUTLANGCHANGEREQUEST 切英文布局、
    /// 敲完再切回来。切换是异步的，目标程序一不理会（或切回失败），
    /// 用户就被永久留在英文布局上 —— 用户的原话是"input plus 不应该能够自己换输入法"。
    /// </summary>
    public static bool TryTextAsKeys(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!LooksLikePlainAscii(text)) return false;

        IntPtr target = GetForegroundWindow();
        if (target == IntPtr.Zero) return false;

        uint tid = GetWindowThreadProcessId(target, out _);
        if (tid == 0) return false;

        /* 用户当前的键盘布局：只用来**映射**字符 → 虚拟键，不激活、不改它。 */
        IntPtr layout = GetKeyboardLayout(tid);
        if (layout == IntPtr.Zero) return false;

        /* 输入法开着时直接放弃真实按键（上层会自动退回 Unicode 注入）。
         * 干跑（自检 / 离屏渲染）时也当成"输入法开着"：那时前台是开发者自己的窗口，
         * 什么都不能碰。 */
        bool forceKey = ForceKeyTyping;
        if ((DryRun || ImeGuard.IsComposing(layout)) && !forceKey) return false;

        /* 先把整段文字映射成 (虚拟键, 布局是否需要 Shift, 是否受大写锁定影响)，
         * 有一个映射不了就整体放弃。 */
        var plan = new List<(int Vk, bool Shift, bool CapsSensitive)>(text.Length);
        foreach (char ch in text)
        {
            short scan = VkKeyScanEx(ch, layout);
            if (scan == -1) return false;

            int vk = scan & 0xFF;
            int state = (scan >> 8) & 0xFF;
            if (vk == 0) return false;
            if ((state & 0x06) != 0) return false;      // 需要 Ctrl / Alt
            plan.Add((vk, (state & 0x01) != 0, IsCapsSensitive(ch)));
        }

        /* 摘 IMM32 上下文只对**老式程序**有效，而且它确实改变了这个窗口的输入法状态
         * （虽然用完就还回去）。所以只在用户显式要求真实按键时才做；
         * 默认档宁可改走 Unicode，也不碰用户的输入法。
         * 干跑时一律不摘：那时前台是开发者自己的窗口，键盘布局/输入法都不该被测试影响。 */
        IntPtr oldIme = forceKey && !DryRun ? SuppressIme(target) : IntPtr.Zero;
        try
        {
            const int Batch = 32;
            for (int start = 0; start < plan.Count; start += Batch)
            {
                int len = Math.Min(Batch, plan.Count - start);
                var arr = new List<INPUT>(len * 4);

                /* 每批读一次大写锁定。
                 *
                 * 为什么必须读：布局给的 Shift 是"大写锁定关着"时的答案。
                 * 大写锁定开着时字母键的语义是**反的** ——
                 *   Caps + A = a，Caps + Shift + A = A，
                 * 于是整串字母的大小写照原样翻个面：用户要 "Abc"，
                 * 打出来是 "aBC"，密码框立刻报"大小写错误"。
                 * 这在密码场景里最要命：错一次就可能被计一次失败。
                 *
                 * 为什么按批读而不是全程读一次：2000 字的文本要发几百毫秒，
                 * 期间用户真去按了 CapsLock 也能立刻跟上（代价只是一次
                 * GetKeyState，可以忽略）。 */
                bool capsOn = CapsLockOn;

                for (int i = start; i < start + len; i++)
                {
                    var (vk, shiftFromLayout, capsSensitive) = plan[i];
                    bool shift = NeedsShift(shiftFromLayout, capsSensitive, capsOn);

                    if (shift) arr.Add(KeyInput(VK.Shift, false));
                    arr.Add(KeyInput(vk, false));
                    arr.Add(KeyInput(vk, true));
                    if (shift) arr.Add(KeyInput(VK.Shift, true));
                }

                Send(arr.ToArray());
                if (start + len < plan.Count) Thread.Sleep(6);
            }
        }
        finally
        {
            RestoreIme(target, oldIme);
        }

        return true;
    }

    /// <summary>
    /// 是否强制用"真实按键"输入文本（输入法开着时也照敲）。
    ///
    /// 默认 false：输入法开着时改走 Unicode 注入，绝不碰用户的输入法。
    /// 只有在"目标程序不认 Unicode 注入"（个别游戏、老式控件）时才需要打开。
    ///
    /// 注意它**不会**切换输入语言 —— 那个能力已经整条删掉了。
    /// </summary>
    public static bool ForceKeyTyping { get; set; }

    // ---------------- 输入法临时挂起 ----------------

    [System.Runtime.InteropServices.DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    /// <summary>
    /// 临时把输入法从前台窗口上摘下来，返回原来的上下文（用于恢复）。
    ///
    /// 不摘的话，中文输入法会把逐字发送的字母当成拼音组合，
    /// 屏幕上出现候选框而不是要输入的文字。
    ///
    /// 只在用户显式要求"真实按键"（<see cref="ForceKeyTyping"/> / TextMode.Keys）时调用：
    /// 摘上下文本身就是**改动这个窗口的输入法状态**，默认档宁可走 Unicode。
    /// </summary>
    private static IntPtr SuppressIme(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            return ImmAssociateContext(hwnd, IntPtr.Zero);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void RestoreIme(IntPtr hwnd, IntPtr old)
    {
        try
        {
            if (hwnd != IntPtr.Zero && old != IntPtr.Zero) ImmAssociateContext(hwnd, old);
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    /// <summary>
    /// 自检用：这个虚拟键发出去时带的扫描码。
    ///
    /// 存在的意义是守住"扫描码不能是 0"这条底线（见 <see cref="KeyInput"/> 里的长注释）：
    /// 缺扫描码时，靠扫描码认键的程序（远程桌面客户端、部分密码控件）会漏键 ——
    /// 14 位密码只落 11 位就是这么来的。
    /// </summary>
    internal static ushort ScanForTest(int vk) => KeyInput(vk, up: false).U.ki.wScan;

    /// <summary>自检用：按英文布局把字符翻成 (vk|shift 位)，用于核对扫描码。</summary>
    internal static short VkKeyScanForTest(char ch)
    {
        IntPtr layout = ImeGuard.EnglishLayout;
        return layout == IntPtr.Zero ? (short)-1 : VkKeyScanEx(ch, layout);
    }

    /// <summary>自检用：Unicode 模式下 wScan/字符码不能被"补扫描码"动过。</summary>
    internal static ushort UnicodeScanForTest(char ch)
        => KeyInput(ch, up: false, unicode: true).U.ki.wScan;

    /// <summary>输入任意文本（走 Unicode 注入，不经过键盘布局）。</summary>
    private static void TextUnicode(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        /* 分批发送。
         *
         * 一次 SendInput 塞进几百上千个 INPUT，慢程序（Electron / 远程桌面 /
         * 老式编辑框）的消息队列消化不完，后半截会被丢掉 —— 用户看到的就是
         * "长文本只打出来一半"。
         * 每批 32 字符（64 个 INPUT），批次之间让出几毫秒。
         */
        const int Batch = 32;
        for (int start = 0; start < text.Length; )
        {
            int len = Math.Min(Batch, text.Length - start);

            // 别把代理对（emoji）劈在两批之间：末尾是高位代理就留到下一批。
            // 下一批必须从被保留的高位代理重新开始，所以循环增量要用实际发送的 len。
            if (start + len < text.Length && len > 1 && char.IsHighSurrogate(text[start + len - 1]))
                len--;

            var arr = new List<INPUT>(len * 2);
            for (int i = start; i < start + len; i++)
            {
                char ch = text[i];
                arr.Add(KeyInput(ch, false, unicode: true));
                arr.Add(KeyInput(ch, true, unicode: true));
            }
            Send(arr.ToArray());

            start += len;
            if (start < text.Length) Thread.Sleep(6);
        }
    }

    /// <summary>
    /// 仅供自检：返回"用 Unicode 方式输入这个字符"时实际带上的 KEYEVENTF_* 标志。
    ///
    /// 存在的意义是守住一个很隐蔽的回归：Unicode 模式下 <c>vk</c> 参数是字符码，
    /// 一旦被当成虚拟键去判"扩展键"，'.'(0x2E)、'o'(0x6F)、','(0x2C) 这些
    /// 就会被打上 EXTENDEDKEY，目标程序收到的就不是原字符 ——
    /// 表现是输入邮箱少了字符。
    /// </summary>
    internal static uint FlagsForUnicodeForTest(char ch)
        => KeyInput(ch, false, unicode: true).U.ki.dwFlags;

    // ---------------- 鼠标 ----------------

    public static void Wheel(int notches, bool horizontal = false)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)(notches * 120)),
                    dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
                    dwExtraInfo = InjectionTag,
                },
            },
        };
        Send(input);
    }

    /// <summary>
    /// 发一个任意大小的滚轮 delta（平滑滚动用）。
    ///
    /// 与 <see cref="Wheel"/> 只差一条：这里不乘 120。Windows 要求
    /// <c>mouseData</c> 是"像素/格"的整数倍，但**不要求**它是 120 的倍数 ——
    /// Chromium 系与 UWP 的 ScrollViewer 都按比例累加，所以 37、-12 这种小 delta
    /// 也能滚出平滑的一段。老式窗口会把它按格取整，这类程序里平滑会退化成整格。
    /// </summary>
    public static bool WheelDelta(int delta, bool horizontal = false)
    {
        if (delta == 0) return true;
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
                    dwExtraInfo = InjectionTag,
                },
            },
        };
        return Send(input);
    }

    public static void Click(bool right = false)
    {
        var down = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN, dwExtraInfo = InjectionTag },
            },
        };
        var up = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP, dwExtraInfo = InjectionTag },
            },
        };
        Send(down, up);
    }

    /// <summary>
    /// 只发鼠标键的"按下"。
    ///
    /// 侧键（0x05 / 0x06）在 SendInput 里不是独立 flag，而是
    /// XDOWN/XUP + mouseData = 1 / 2（XBUTTON1 / XBUTTON2）。
    /// </summary>
    public static void MouseDown(int vk)
    {
        /* 自检钩子：鼠标也会真的点下去（点一下桌面可能选中图标、点一下浏览器可能跳转），
         * 所以测试时必须拦在这里，和键盘的 SenderForTest 一个待遇。 */
        if (SenderForTest is { } spy) { spy(vk, false); return; }

        uint flag = vk switch
        {
            0x01 => MOUSEEVENTF_LEFTDOWN,
            0x02 => MOUSEEVENTF_RIGHTDOWN,
            0x04 => MOUSEEVENTF_MIDDLEDOWN,
            0x05 or 0x06 => MOUSEEVENTF_XDOWN,
            /* 水平滚轮没有"按住"这回事：一次拨动就是一个 HWHEEL 事件。
             * 方向看 mouseData 的正负（负＝左拨，正＝右拨）。 */
            VK.WheelLeft or VK.WheelRight => MOUSEEVENTF_HWHEEL,
            _ => 0,
        };
        if (flag == 0) return;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = flag,
                    mouseData = vk switch
                    {
                        0x05 => 1u,
                        0x06 => 2u,
                        VK.WheelLeft => unchecked((uint)-120),
                        VK.WheelRight => 120u,
                        _ => 0u,
                    },
                    dwExtraInfo = InjectionTag,
                },
            },
        };
        /* 记账必须早于 Send：低级钩子是在 SendInput **内部**同步回调的，
         * 等 Send 返回再记就已经晚了 —— 自己注入的中键会先走一遍改键，
         * 于是"侧键→中键、中键→R"会变成按侧键直接出 R。 */
        NoteInjected(vk);
        Send(input);
    }

    /// <summary>
    /// 只发鼠标键的"抬起"。
    ///
    /// 善后必须用这个（而不是裸 mouse_event）：抬起带上 <see cref="InjectionTag"/> 之后，
    /// 我们自己的钩子会按"注入"直接放行，不会被改键 / 层逻辑误当成用户按下的键吞掉。
    /// </summary>
    public static void MouseUp(int vk)
    {
        if (SenderForTest is { } spy) { spy(vk, true); return; }

        /* 滚轮没有抬起：拨一下就已经结束了。这里直接返回，不再补一条 HWHEEL，
         * 否则一次"发送滚轮"会真的滚两格。 */
        if (vk is VK.WheelLeft or VK.WheelRight) return;

        uint flag = vk switch
        {
            0x01 => MOUSEEVENTF_LEFTUP,
            0x02 => MOUSEEVENTF_RIGHTUP,
            0x04 => MOUSEEVENTF_MIDDLEUP,
            0x05 or 0x06 => MOUSEEVENTF_XUP,
            _ => 0,
        };
        if (flag == 0) return;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = flag,
                    mouseData = vk switch { 0x05 => 1u, 0x06 => 2u, _ => 0u },
                    dwExtraInfo = InjectionTag,
                },
            },
        };
        /* 记账必须早于 Send：低级钩子是在 SendInput **内部**同步回调的，
         * 等 Send 返回再记就已经晚了 —— 自己注入的中键会先走一遍改键，
         * 于是"侧键→中键、中键→R"会变成按侧键直接出 R。 */
        NoteInjected(vk);
        Send(input);
    }

    /// <summary>
    /// 按下 / 抬起一个"输入键" —— 键盘键与鼠标键统一入口。
    ///
    /// 改键既能把键盘键换成鼠标键（Caps → 左键），也能反过来（侧键 → Ctrl），
    /// 所以发送端不能再假设参数一定是键盘虚拟码。
    /// </summary>
    public static void Press(int vk, bool down)
    {
        if (vk is >= 0x01 and <= 0x06)
        {
            if (down) MouseDown(vk); else MouseUp(vk);
            return;
        }

        if (down) KeyDown(vk); else KeyUp(vk);
    }

    /// <summary>统一松手（善后路径用）。</summary>
    public static void ReleaseInput(int vk) => Press(vk, down: false);

    /// <summary>记一条"这个键刚被我们注入过"（回声识别用；鼠标键也要记）。</summary>
    private static void NoteInjected(int vk, bool force = false)
    {
        lock (InjectedGate)
        {
            long now = Environment.TickCount64;
            if (InjectedAt.Count > 128) InjectedAt.Clear();
            InjectedAt[vk] = now;

            /* 干跑（自检 / 渲染）根本没把事件发出去，也就不会有回声回来。
             * 这时候还记账的话，后面真正的按键会被当成"还没回来的回声"吞掉。 */
            if (DryRun && !force) return;

            if (!InjectedPending.TryGetValue(vk, out var q))
            {
                q = new Queue<long>();
                InjectedPending[vk] = q;
            }

            /* 超时没回来的注入不能留着 —— 否则用户真按一次会被当成回声吞掉。 */
            while (q.Count > 0 && now - q.Peek() > EchoWindowMs) q.Dequeue();
            q.Enqueue(now);
            while (q.Count > EchoPendingMax) q.Dequeue();
        }
    }

    /// <summary>自检用：干跑下也记一笔"待回声"，用来验证配对记账。</summary>
    internal static void NoteInjectedForTest(int vk) => NoteInjected(vk, force: true);

    /// <summary>相对移动鼠标（像素）。</summary>
    public static void MoveRelative(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = 0x0001 /*MOVE*/, dwExtraInfo = InjectionTag },
            },
        };
        Send(input);
    }

    /// <summary>当前物理按键是否按下（用于判断用户是否仍按住修饰键）。</summary>
    public static bool IsPhysicallyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
