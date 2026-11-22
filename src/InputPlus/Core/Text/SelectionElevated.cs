using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InputPlus.Core.Text;

/// <summary>
/// 「本程序是普通权限、前台却是管理员程序」时的取词办法：一个**常驻的提权助手**。
///
/// 为什么本地三条通道在这种场景下注定失败（不是 bug，是 Windows 的设计）：
///   · UIA：低完整性进程向高完整性进程要无障碍树，被 UIPI 直接拒；
///   · WM_COPY：同一条 UIPI 规则，消息投不进去；
///   · SendInput：合成的 Ctrl+C 同样被 UIPI 拦，前台窗口收不到。
/// 于是用户看到的就是「{某程序} 以管理员身份运行，系统不允许读取它的选区」，
/// 只能手动 Ctrl+C 兜底 —— 这正是用户报的「管理员程序上直接搜索不能用」。
///
/// 反向的那条路早就有了（<see cref="SelectionProxy"/>：本程序提权、目标普通，
/// 用一个普通权限的一次性代理读）。这里是它的镜像：
///   主进程（普通权限）建命名管道服务端 → 用 runas 拉起同一个 exe 的
///   <c>--pick-high-server</c> 模式（**只弹一次 UAC**）→ 助手连上管道后常驻，
///   每次都替我们读前台窗口的选区。
/// 高完整性进程连低完整性的管道是允许的（低→高才被拒），所以方向没问题。
///
/// 主进程一退出，管道句柄关闭，助手读到 EOF 自己退出 —— 不会留下提权幽灵进程。
/// </summary>
internal static class SelectionElevated
{
    /// <summary>助手的命令行开关。放在 <see cref="Core.ElevationHelper"/> 的免提权白名单里（它自己就是提权进程）。</summary>
    public const string ServerFlag = "--pick-high-server";

    /// <summary>请求：1 字节命令 + 8 字节目标窗口句柄。</summary>
    internal const int RequestBytes = 9;
    internal const byte CmdCapture = 0x01;
    internal const byte CmdExit = 0x00;

    /// <summary>助手读一次选区最多等多久 —— 与 <see cref="TextActions.GetSelectedTextLocal"/> 的默认值错开一点，留出写回管道的时间。</summary>
    private const int HelperCaptureMs = 1200;

    private static readonly object Gate = new();

    private static NamedPipeServerStream? _server;
    private static Process? _proc;

    /* UAC 对话框要等人去点，1.5 秒的等待肯定不够，而在动作队列上死等又会让用户以为卡死。
     * 折中：超时后**不拆**，把连接挂成"待连"，用户点完「是」下一次按键直接复用 ——
     * 否则每按一次热键就弹一次 UAC，比读不到选区更烦人。 */
    private static NamedPipeServerStream? _pendingServer;
    private static Process? _pendingProc;
    private static long _pendingUntil;
    private const int PendingWaitMs = 30000;
    private static bool _declined;      // 用户点了 UAC 的「否」：本次运行不再问
    private static bool _busy;          // 同一时刻只处理一次取词
    private static string _status = "未启动";

    /// <summary>助手进程自己跑在 <see cref="RunServer"/> 里：这时候不能再起助手（会递归弹 UAC）。</summary>
    public static bool InHelper { get; private set; }

    /// <summary>自检用的替身：拿到 (pipeName)，返回 null 表示"起不来"。</summary>
    internal static Func<string, Process?>? LaunchForTest;

    /// <summary>助手已经连上并可用。</summary>
    public static bool IsConnected
    {
        get
        {
            lock (Gate)
            {
                if (_server is { IsConnected: true } && _proc is { HasExited: false }) return true;
                return _pendingServer is { IsConnected: true } && _pendingProc is { HasExited: false };
            }
        }
    }

    /// <summary>用户在这次运行里取消过 UAC 授权。</summary>
    public static bool Declined
    {
        get { lock (Gate) return _declined; }
    }

    /// <summary>给日志与自检看的状态一句话。</summary>
    public static string Status
    {
        get { lock (Gate) return _status; }
    }

    /// <summary>自检用：直接走一次"待连接转正"。</summary>
    internal static void PromotePendingForTest() => PromotePending();

    /// <summary>自检用：UAC 连接最多挂多久。</summary>
    internal static int PendingWaitMsForTest => PendingWaitMs;

    /// <summary>自检用：把状态恢复到"未启动"。</summary>
    internal static void ResetForTest()
    {
        NamedPipeServerStream? pending;
        lock (Gate)
        {
            _declined = false;
            _busy = false;
            _status = "未启动";
            pending = _pendingServer;
            _pendingServer = null;
            _pendingProc = null;
            _pendingUntil = 0;
        }
        try { pending?.Dispose(); } catch { }
    }

    /// <summary>
    /// 读一次前台窗口的选区。取不到返回空串（调用方照旧退回"请按 Ctrl+C"）。
    ///
    /// 第一次调用会拉起助手（UAC 弹窗）；用户点「是」之前我们就超时返回了，
    /// 所以第一次通常拿不到、第二次开始全自动 —— 提示文案里会写清楚这一点。
    /// </summary>
    public static string TryGetSelectedText(int budgetMs = 1800, IntPtr target = default)
    {
        /* 已经是管理员进程：本地通道本来就是"高→高"，不需要助手
         * （而低→高的那条路在这里也不成立）。 */
        if (ElevationHelper.IsElevated || InHelper) return "";

        lock (Gate)
        {
            if (_declined || _busy) return "";
            _busy = true;
        }

        try
        {
            PromotePending();   // 上一次的 UAC 可能已经点过「是」了，先把它转正
            if (!IsConnected)
            {
                /* 连 UAC 的点击时间一起等：1.5 秒够用户点「是」，
                 * 又不会把动作队列堵到用户以为卡死。 */
                if (!StartHelper(Math.Min(budgetMs, 1500))) return "";
            }

            return Request(target == IntPtr.Zero ? ForegroundOrZero() : target, budgetMs);
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"[取词] 提权助手不可用：{ex.Message}");
            Disconnect();
            return "";
        }
        finally
        {
            lock (Gate) _busy = false;
        }
    }

    // ───────────────── 主进程这一侧 ─────────────────

    /// <summary>
    /// 起一个提权助手并等它连上。返回 true 表示"管道通了，可以发请求"。
    ///
    /// 用户取消 UAC 时把 <c>_declined</c> 钉住：本次运行里不再弹窗 ——
    /// 每按一次热键弹一次 UAC 比"功能不可用"更让人恼火。
    /// </summary>
    private static bool StartHelper(int waitMs)
    {
        Process? proc;
        string pipeName = "KeyEnhanceHigh_" + Guid.NewGuid().ToString("N");

        if (LaunchForTest != null)
        {
            proc = LaunchForTest(pipeName);
            if (proc == null) { lock (Gate) { _declined = true; _status = "启动失败（自检替身）"; } return false; }
        }
        else
        {
            proc = Launch(pipeName);
            if (proc == null) { lock (Gate) _declined = true; return false; }
        }

        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            Task wait = server.WaitForConnectionAsync();
            if (!wait.Wait(waitMs))
            {
                /* 用户还没点 UAC（或者点得太慢）：**先别拆**。
                 * 拆了就等于"每按一次热键弹一次 UAC"，比读不到选区更烦人；
                 * 挂成待连，用户点完「是」下一次按键直接复用这条通道。 */
                lock (Gate)
                {
                    _pendingServer = server;
                    _pendingProc = proc;
                    _pendingUntil = Environment.TickCount64 + PendingWaitMs;
                    _status = "等待授权（点「是」之后下次按键自动生效）";
                }
                _ = wait.ContinueWith(_ => PromotePending(), TaskScheduler.Default);
                CoreLog.Info("[取词] UAC 还没点，先把提权助手挂起等连接");
                return false;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"[取词] 提权助手连接失败：{ex.Message}");
            server.Dispose();
            Kill(proc);
            return false;
        }

        lock (Gate)
        {
            _server = server;
            _proc = proc;
            _status = "已连接";
        }
        CoreLog.Info("[取词] 提权助手已连上（管理员程序上的直接搜索从现在起可以自动取词）");
        return true;
    }

    /// <summary>
    /// 把"等 UAC 时挂起的连接"转正：用户点完「是」、助手连上管道之后，
    /// 下一次取词直接用这条通道，不再弹窗。等待任务的 ContinueWith 也会调它。
    ///
    /// 三种结局：还活着 → 转正；还没到 30 秒 → 继续等；等过期了 → 拆掉并杀助手。
    /// </summary>
    private static void PromotePending()
    {
        NamedPipeServerStream? server;
        Process? proc;
        long until;
        lock (Gate)
        {
            server = _pendingServer;
            proc = _pendingProc;
            until = _pendingUntil;
        }
        if (server == null) return;

        bool alive;
        try { alive = server.IsConnected && proc is { HasExited: false }; }
        catch { alive = false; }

        if (alive)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(_pendingServer, server)) return;   // 已经被别人处理过
                _server = server;
                _proc = proc;
                _pendingServer = null;
                _pendingProc = null;
                _status = "已连接";
            }
            CoreLog.Info("[取词] 提权助手已连上（管理员程序上的直接搜索从现在起可以自动取词）");
            return;
        }

        if (Environment.TickCount64 < until) return;   // 还在等人点 UAC，不催

        lock (Gate)
        {
            if (!ReferenceEquals(_pendingServer, server)) return;
            _pendingServer = null;
            _pendingProc = null;
            _status = "等待授权超时";
        }
        CoreLog.Info($"[取词] 等了 {PendingWaitMs / 1000} 秒也没等到管理员授权，这次先放弃");
        try { server.Dispose(); } catch { }
        Kill(proc);
    }

    /// <summary>用 runas 拉起同一个 exe 的助手模式。返回 null 表示用户取消 / 起不来。</summary>
    private static Process? Launch(string pipeName)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "InputPlus.exe";
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                /* 只传管道名：助手不认别的参数，也就不可能被别的程序借用成"随便读选区"的工具。 */
                Arguments = ServerFlag + " \"" + pipeName + "\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var proc = Process.Start(psi);
            lock (Gate) _status = proc == null ? "启动失败" : "正在申请管理员授权";
            return proc;
        }
        catch (Exception ex)
        {
            /* NativeErrorCode 1223 = ERROR_CANCELLED：用户在 UAC 上点了「否」。 */
            bool cancelled = ex is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 1223;
            lock (Gate) _status = cancelled ? "用户取消了授权" : "启动失败：" + ex.Message;
            CoreLog.Info(cancelled
                ? "[取词] 用户取消了提权助手的授权，本次运行改用「请按 Ctrl+C」"
                : $"[取词] 提权助手启动失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>发一次取词请求并读回结果。任何 I/O 异常都拆掉连接，下次重新拉助手。</summary>
    private static string Request(IntPtr target, int budgetMs)
    {
        NamedPipeServerStream? server;
        lock (Gate) server = _server;
        if (server == null || !server.IsConnected) return "";

        WriteRequest(server, target);

        /* 读要能超时：助手那边可能在 UIA 上卡住（某些程序不响应无障碍请求）。
         * 放进 Task 里等，超时就拆连接 —— 阻塞在 Read 上会让动作队列一直挂着。 */
        Task<string> read = Task.Run(() => ReadReply(server) ?? "");
        if (!read.Wait(budgetMs))
        {
            CoreLog.Warn($"[取词] 提权助手 {budgetMs}ms 内没回话，拆掉连接");
            Disconnect();
            return "";
        }
        return read.Result;
    }

    private static void Disconnect()
    {
        NamedPipeServerStream? server;
        Process? proc;
        NamedPipeServerStream? pending;
        Process? pendingProc;
        lock (Gate)
        {
            server = _server;
            proc = _proc;
            pending = _pendingServer;
            pendingProc = _pendingProc;
            _server = null;
            _proc = null;
            _pendingServer = null;
            _pendingProc = null;
            _status = "未连接";
        }
        try { server?.Dispose(); } catch { }
        try { pending?.Dispose(); } catch { }
        Kill(proc);
        Kill(pendingProc);
    }

    private static void Kill(Process? proc)
    {
        try
        {
            if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true);
        }
        catch { }   /* 预期内：进程可能已经自己退了 */
    }

    private static IntPtr ForegroundOrZero()
    {
        try { return Core.Windows.WindowService.Foreground; }
        catch { return IntPtr.Zero; }
    }

    // ───────────────── 助手那一侧 ─────────────────

    /// <summary>
    /// 如果命令行里有 <c>--pick-high-server &lt;管道名&gt;</c>，就地跑成助手并返回 true。
    ///
    /// 必须在 <c>App.OnStartup</c> 里、单实例检查与提权逻辑**之前**调用：
    /// 助手是被 runas 拉起来的，走正常启动流程会先撞上"已有实例在运行"直接退出。
    /// </summary>
    public static bool TryRunServer(string[] args, out int exitCode)
    {
        exitCode = 0;
        int at = Array.IndexOf(args, ServerFlag);
        if (at < 0) return false;

        string? pipeName = at + 1 < args.Length ? args[at + 1] : null;
        exitCode = string.IsNullOrWhiteSpace(pipeName) ? 2 : RunServer(pipeName!);
        return true;
    }

    /// <summary>
    /// 助手的命令循环：连回主进程 → 收请求 → 读选区 → 写回复，
    /// 直到主进程关闭管道（读到 EOF）或收到退出命令。
    /// </summary>
    internal static int RunServer(string pipeName)
    {
        InHelper = true;
        CoreLog.Info($"[取词] 提权助手启动，管道={pipeName}");

        using NamedPipeClientStream? client = ConnectWithRetry(pipeName, 8000);
        if (client == null)
        {
            CoreLog.Warn("[取词] 提权助手连不回主进程，退出");
            return 3;
        }

        return ServeLoop(client);
    }

    /// <summary>
    /// 助手的命令循环本体（拆出来是为了自检能拿 MemoryStream 直接喂请求）。
    /// 返回 0 = 正常收工（主进程关了管道 / 发了退出命令）。
    /// </summary>
    internal static int ServeLoop(Stream client)
    {
        var head = new byte[RequestBytes];
        while (true)
        {
            if (!ReadExactly(client, head, head.Length))
            {
                CoreLog.Info("[取词] 主进程已退出，提权助手收工");
                return 0;
            }
            if (head[0] == CmdExit) return 0;
            if (head[0] != CmdCapture) continue;

            var target = (IntPtr)BitConverter.ToInt64(head, 1);
            string text = CaptureFor(target);
            WriteReply(client, text);
        }
    }

    /// <summary>自检替身：给一个假实现就不用真去读选区（也就不会去拉 UAC）。</summary>
    internal static Func<IntPtr, string>? CaptureForTest;

    /// <summary>
    /// 助手真正读选区的地方。
    ///
    /// 只在"前台已经变成我们自己的面板"时才把目标窗口抢回前台：捕获发生在面板弹出**之前**，
    /// 正常情况前台就是用户那个管理员程序，不需要任何焦点操作（动焦点会把用户的选区弄丢）。
    /// </summary>
    private static string CaptureFor(IntPtr target)
    {
        if (CaptureForTest != null) return CaptureForTest(target);
        try
        {
            if (target != IntPtr.Zero)
            {
                IntPtr fg = Core.Windows.WindowService.Foreground;
                if (fg != target && IsOwnWindow(fg))
                {
                    Core.Windows.WindowService.Activate(target);
                    Thread.Sleep(80);
                }
            }
            string text = TextActions.GetSelectedTextLocal(HelperCaptureMs);
            CoreLog.Info($"[取词] 提权助手读到 长度={text.Length}");
            return text;
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"[取词] 提权助手读选区异常：{ex.Message}");
            return "";
        }
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch { return false; }
    }

    private static NamedPipeClientStream? ConnectWithRetry(string pipeName, int budgetMs)
    {
        long deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline)
        {
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                client.Connect(250);
                return client;
            }
            catch (TimeoutException) { client.Dispose(); }
            catch (IOException) { client.Dispose(); Thread.Sleep(60); }
        }
        return null;
    }

    // ───────────────── 协议（两个方向都只走这几个函数，方便自检） ─────────────────

    /// <summary>主进程 → 助手：1 字节命令 + 8 字节目标句柄（小端）。</summary>
    internal static void WriteRequest(Stream stream, IntPtr target)
    {
        var buf = new byte[RequestBytes];
        buf[0] = CmdCapture;
        BitConverter.TryWriteBytes(new Span<byte>(buf, 1, 8), target.ToInt64());
        stream.Write(buf, 0, buf.Length);
        stream.Flush();
    }

    /// <summary>助手 ← 主进程：读请求头。读到 EOF 返回 false（主进程没了）。</summary>
    internal static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = stream.Read(buffer, done, count - done);
            if (n <= 0) return false;
            done += n;
        }
        return true;
    }

    /// <summary>助手 → 主进程：4 字节长度 + UTF-8 正文。</summary>
    internal static void WriteReply(Stream stream, string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text ?? "");
        var head = new byte[4];
        BitConverter.TryWriteBytes(head, body.Length);
        stream.Write(head, 0, head.Length);
        if (body.Length > 0) stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    /// <summary>主进程 ← 助手：读回复。拿不到就返回 null（调用方当"没取到"处理）。</summary>
    internal static string? ReadReply(Stream stream)
    {
        var head = new byte[4];
        if (!ReadExactly(stream, head, head.Length)) return null;
        int len = BitConverter.ToInt32(head, 0);
        if (len < 0 || len > 4 * 1024 * 1024) return null;   // 明显是脏数据，别去分配
        if (len == 0) return "";
        var body = new byte[len];
        if (!ReadExactly(stream, body, body.Length)) return null;
        return Encoding.UTF8.GetString(body);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
