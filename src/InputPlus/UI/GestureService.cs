using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Actions;
using InputPlus.Core.Gestures;
using InputPlus.Core.Input;
using InputPlus.Core.Interop;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;
using InputPlus.UI.Windows;

namespace InputPlus.UI;

/// <summary>
/// 鼠标手势服务：从钩子里拿"真实的"鼠标按键与滚轮，攒出一条轨迹，
/// 识别成动作后交给和键盘完全相同的动作管线执行。
///
/// 为什么这件事只能在我们自己的钩子里做：WGestures 那类工具靠低级钩子
/// 观察全局鼠标；而我们**就是**钩子的主人。手势起点是物理按键，
/// 不需要把合成事件再喂回系统（那正是以前"侧键→中键→手势"永远失灵的原因：
/// 合成鼠标事件带着 LLMHF_INJECTED，手势软件一律不理）。
///
/// 线程模型：钩子回调在钩子线程，绘制/执行在界面线程。
/// 共享状态用 <see cref="_gate"/> 保护，钩子回调里只做"记账 + BeginInvoke"。
/// </summary>
public sealed class GestureService : IDisposable
{
    private readonly AppHost _host;
    private readonly object _gate = new();

    private GestureOverlayWindow? _overlay;
    private DispatcherTimer? _poll;
    private DispatcherTimer? _linger;
    /* 名称框淡出的第二段定时器：停留结束 → 开始淡出 → 淡完才清窗口。 */
    private DispatcherTimer? _fade;

    private readonly List<GestureMath.Pt> _points = new();
    private GestureMath.Pt _start;
    private GestureMath.Pt _last;
    private int _vk;
    private bool _active;
    private string _appPath = "";
    private MouseGesture? _cornerCandidate;
    private GesturePattern.Tracker? _tracker;
    private long _activeSince;
    private long _lastMoveAt;
    private bool _activeInjected;
    /* 画的过程中实时显示“现在最像哪条手势”的名称，避免用户等到松手才知道。 */
    private string _previewTitle = "";

    /* 上一次跑判定时记到第几个点。形状比的是整笔轨迹，v5.13 起再没有“方向串变没变”
     * 这种便宜的判据了，于是按点数节流：每多记 3 个点（约 6px）才进一次引擎，
     * 跟手又不至于让 8ms 的轮询每一下都跑一遍 DP。 */
    private int _previewCount;

    /* 防止“按住鼠标乱划”：滑动窗口里最多允许 20 次新手势，
     * 超过后短暂停用手势并提示，避免误触/脚本刷动作。 */
    private readonly Queue<long> _gestureBurst = new();
    private long _gestureBurstBlockedUntil;
    private const int GestureBurstLimit = 20;
    private const int GestureSegmentLimit = 20;
    private const long GestureBurstWindowMs = 10_000;
    private const long GestureBurstCooldownMs = 3_000;

    /* 立即执行后，物理鼠标键还按着：它的抬起必须由我们吞掉，
     * 否则系统会收到一个"没有按下"的抬起。
     * 0 = 没有待收尾的立即执行；否则是那个鼠标键的 vk。 */
    private int _swallowUpVk;

    /* 热角（悬停到屏幕角）监视器。WGestures 用鼠标路径跟踪器顺带判角，
     * 我们已经有独立的鼠标钩子，但"没按键、只是把指针停在角上"不会产生
     * 任何鼠标消息，所以必须自己轮询指针位置。 */
    private DispatcherTimer? _hotWatch;
    private bool _hotArmed = true;

    /* 鼠标手势总开关的全局热键。用 RegisterHotKey 而不是键盘钩子：
     * 钩子已经被Input Plus主引擎占用，手势服务自己再装一颗会重复收键；
     * RegisterHotKey 不抢输入、也不需要管理员权限。 */
    private HwndSource? _hotkeySource;
    private const int ToggleHotkeyId = 0x4B47;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private Action<MouseGesture>? _recordDone;

    /* 手势专用鼠标钩子。
     *
     * 不能用 Engine.Hook：宿主模式（--hookhost）下真正的钩子在最小进程里，
     * 主进程这颗是没装过的空壳 —— 订阅它等于一条手势也收不到。
     * 这里自己装一颗**只装鼠标**的钩子，和宿主/引擎互不影响。 */
    private KeyboardHook? _gestureHook;

    /* 手势动作执行后的抑制窗：动作里可能再发鼠标键（“点击中键”之类），
     * 那些注入不能被当成新手势的开头。 */
    private long _gestureSuppressUntil;

    /* 设置页弹「录一条新手势」时临时挂起：
     * 那个窗口要自己收鼠标按下/抬起，全局手势如果还在这儿拦一道，
     * 画出来的笔会被吞掉，窗口里什么都收不到。 */
    /// <summary>
    /// 设置页弹「录一条新手势」时临时挂起。
    ///
    /// 挂起时必须把正在画的那一笔收掉：录制窗口会自己截鼠标，抬起事件
    /// 不会再回到手势钩子；如果这里还留着 <c>_active=true</c>，下一次右键
    /// 会被当成"上一笔的抬起"吞掉 —— 用户看到的就是右键偶尔失灵。
    /// </summary>
    private volatile bool _suspended;
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            if (value)
            {
                ResetActive();
                _hotArmed = false;   // 关掉录制窗后先离开角/边，避免立刻触发
            }
            else
            {
                _hotArmed = false;
            }
        }
    }

    /* 手势开始时鼠标下的那个顶层窗口。
     *
     * 手势是"在某个窗口上画"的，但画的时候左键/右键被我们吞掉了，
     * 那个窗口根本没被激活。WGestures 的做法是把动作发给画手势时
     * 鼠标下的窗口；这里记下句柄，执行前先把它切到前台，
     * 这样「↓→ 关闭」的 Ctrl+W 才会落在用户以为的那个窗口上。 */
    private IntPtr _targetWindow;

    /* 补发单击之后的一小段静默期：这段里同一个键再来（包括被别的鼠标
     * 工具复制出来的无标记副本）都直接放行，不再开新手势。 */
    private readonly Dictionary<int, long> _replayUntil = new();

    /* 组合手势触发后，那颗"第二键"的抬起还要吞掉一次，
     * 否则系统会看到一次"无按下"的抬起。 */
    private int _chordSwallow;

    public GestureService(AppHost host)
    {
        _host = host;
    }

    /// <summary>是否正在等用户画一笔（设置页的「录制」按钮）。</summary>
    public bool IsRecording { get; private set; }

    /// <summary>正在绘制中（自检和界面用它判断状态，不参与逻辑）。</summary>
    public bool IsTracking
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>
    /// 手势用的鼠标钩子：平滑滚动也挂在同一条上。
    /// WH_MOUSE_LL 是全局单点资源，装两条只会互相拖慢（每条都在同一条系统
    /// 钩子链上），所以后来的功能一律复用这一条。
    /// </summary>
    internal KeyboardHook? Hook => _gestureHook;

    public void Attach()
    {
        try
        {
            _gestureHook = new KeyboardHook();
            _gestureHook.GestureButtonEvent += OnButton;
            _gestureHook.GestureWheelEvent += OnWheel;
            _gestureHook.Start(mouseOnly: true);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "安装手势鼠标钩子"); }

        // 第一次启动时把 WGestures 的手势搬过来：用户升级上来就能直接用，
        // 不用先学会"导入"在哪。放到空闲优先级，等主窗口建好再改设置。
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(
                new Action(InitializeDefaults),
                DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "安排手势自动导入"); }

        ApplyHotkey();
        StartHotWatch();
    }

    /// <summary>
    /// 先导入 WGestures 配置，再补内置边角手势，最后把老动作换成正式动作（顺序不能反）。
    /// </summary>
    private void InitializeDefaults()
    {
        AutoImportOnce();
        try
        {
            if (GestureDefaults.Ensure(_host.Settings)) _host.SaveSettings();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "补齐内置边角手势"); }

        /* 老配置里的兼容动作（openUrl 这类）在这里一次性换成正式动作。
         * 不做的话，界面上打开那一条只能显示成"旧功能"，用户一保存就丢功能。
         * 换完参数不变、执行路径也不变，只是 id 变成界面上选得出来的那个。 */
        try
        {
            if (InputPlus.Core.Actions.LegacyActionMigration.Apply(_host.Settings) > 0) _host.SaveSettings();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "老动作换成正式动作"); }
    }

    /// <summary>只在"没导过 + 自己还没手势 + 找得到 WGestures 配置"时导入一次。</summary>
    private void AutoImportOnce()
    {
        try
        {
            var settings = _host.Settings;
            var options = settings.GestureOptions;
            if (options == null || !string.IsNullOrEmpty(options.AutoImportSource)) return;
            if (settings.Gestures.Count > 0) return;

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WGestures2");
            if (!Directory.Exists(root)) return;

            string? dir = Directory.GetDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (dir == null) return;

            string file = Path.Combine(dir, "gestures.json");
            if (!File.Exists(file)) return;

            var (imported, skipped) = WGesturesImporter.ImportFile(file, settings);
            options.AutoImportSource = file;
            _host.SaveSettings();

            if (imported > 0)
                _host.Notify(Loc.F("已从 WGestures 导入 {0} 条手势（{1} 条需手动处理）", imported, skipped));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "自动导入手势"); }
    }

    public void Dispose()
    {
        try
        {
            if (_gestureHook != null)
            {
                _gestureHook.GestureButtonEvent -= OnButton;
                _gestureHook.GestureWheelEvent -= OnWheel;
                _gestureHook.Dispose();
                _gestureHook = null;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "卸载手势钩子"); }

        _poll?.Stop();
        _linger?.Stop();
        _hotWatch?.Stop();
        try
        {
            if (_hotkeySource != null)
            {
                UnregisterHotKey(_hotkeySource.Handle, ToggleHotkeyId);
                _hotkeySource.RemoveHook(HotkeyWndProc);
                _hotkeySource.Dispose();
                _hotkeySource = null;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "注销手势热键"); }
        try { _overlay?.Close(); } catch (Exception ex) { CoreLog.Swallow(ex, "关闭手势覆盖层"); }
    }

    // ───────────────────────── 录制 ─────────────────────────

    /// <summary>
    /// 进入录制：下一笔（任意按键）不执行动作，而是把轨迹回传给界面。
    /// 设置页用它实现「按住画一个」。
    /// </summary>
    public void BeginRecord(Action<MouseGesture> done)
    {
        lock (_gate)
        {
            _recordDone = done;
            IsRecording = true;
            _active = false;
            _points.Clear();
            _tracker = null;
        }
    }

    public void CancelRecord()
    {
        lock (_gate)
        {
            _recordDone = null;
            IsRecording = false;
            _tracker = null;
        }
        HideOverlay(0);
    }

    // ───────────────────────── 钩子入口 ─────────────────────────

    private bool OnButton(int vk, bool down, bool injected)
    {
        /* 立即执行发生在鼠标键还按着的时候。那一下抬起不能放给系统，
         * 否则前台窗口会收到一个没有配对的 MouseUp。 */
        if (!down && !injected && Interlocked.CompareExchange(ref _swallowUpVk, 0, vk) == vk)
            return true;

        if (Suspended) return false;

        /* 任务栏/托盘是 Explorer 自己吃右键的地方：我们一旦把 down 吞掉，
         * 再注入一次右键，Explorer 往往不认（尤其是托盘图标的弹出菜单），
         * 表现为“任务栏闪一下、菜单没出来”。这里干脆不接管任务栏上的右键。 */
        if (down && !injected && IsShellTaskbarAtCursor()) return false;

        lock (_gate)
        {
            if (!down && _chordSwallow == vk) { _chordSwallow = 0; return true; }
        }

        if (injected)
        {
            /* 注入的鼠标键默认不算手势开头 —— 否则“点击中键”这类动作会自己
             * 触发一轮新手势，越点越深。唯一的例外：改键把侧键换成中键时，
             * 那颗中键是我们自己发的，但用户的手指确实按了侧键。这种“改键
             * 回声”由 InputSender 发一张一次性通行证，允许它当手势起点。 */
            if (Environment.TickCount64 < Interlocked.Read(ref _gestureSuppressUntil)) return false;

            /* 通行证只在"按下"时用一次；抬起要去收尾已经开始的这一笔，
             * 不能再要一张新票 —— 否则侧键→中键这种一次按压只发一张票的
             * 场景，按下开始了、抬起却进不来，手势永远收不了尾。 */
            if (down)
            {
                if (!InputSender.TakeGesturePass(vk)) return false;
            }
            else
            {
                bool tracking;
                lock (_gate) tracking = _active && _vk == vk;
                if (!tracking) return false;
            }
        }

        var settings = _host.Settings;
        var opts = settings.GestureOptions;

        bool recording;
        lock (_gate) recording = IsRecording;
        if (recording) return HandleRecordButton(vk, down);

        if (opts is not { Enabled: true })
        {
            if (IsTracking) ResetActive();
            return false;
        }
        if (GestureButtons.FromVk(vk).Length == 0) return false;   // 只处理 5 个鼠标键

        if (down)
        {
            bool active;
            int current;
            bool injectedStart;
            lock (_gate)
            {
                active = _active;
                current = _vk;
                injectedStart = _activeInjected;
            }

            /* 抬起事件丢了（UAC 安全桌面、锁屏、钩子被摘掉）会留下 _active。
             * 这时如果再来一次同一颗键的按下，抬起会被当成"上一笔的收尾"
             * 吞掉，系统只看到按下、看不到抬起 —— 右键菜单就不弹了。
             * 同一颗鼠标键在还没收笔时又按下，只可能是上一笔的抬起丢了：
             * 鼠标键没有自动重复，组合手势按的也是另一颗键。直接收干净重来。 */
            if (active && current == vk && !injectedStart)
            {
                ResetActive();
                lock (_gate)
                {
                    active = _active;
                    current = _vk;
                }
            }

            lock (_gate)
            {
                /* 刚补发出去的那一下会带着别人的标记回来（鼠标增强工具会把
                 * 事件复制一份重发），别再当成新手势的开头，原样交给系统。 */
                if (_replayUntil.TryGetValue(vk, out long until))
                {
                    /* 静默期里再来的一颗，多半是鼠标增强工具复制我们补发的那一下
                     * （标记被抹掉）。放过去的话系统会看到两次右键，
                     * 菜单刚弹出来又被第二下关掉。 */
                    if (Environment.TickCount64 <= until) return false;
                    _replayUntil.Remove(vk);
                }
            }

            if (active)
            {
                /* 画到一半又按了第二颗鼠标键：先当"组合手势"试一次
                 * （WGestures 的「按住右键再点左键」就是这种）。
                 * 对不上就把这颗键原样交给系统，别把右键菜单之类的弄丢。 */
                if (TryChord(settings, current, vk, opts)) return true;
                return false;
            }

            /* 「手势作用目标」决定这一笔算在哪个程序头上、执行前要不要切窗口：
             * 指针下方 = 画在哪就用哪；活动窗口 = 只看当前焦点，不切窗口。 */
            var (app, targetWindow) = ResolveTarget(opts);
            /* 用户在「作用目标」里给某个程序点了“禁用手势”：
             * 这个程序里连全局手势也不触发，右键/侧键原样交给系统。 */
            if (IsTargetDisabled(app)) return false;

            var (px, py) = CursorPos();
            int cornerAt = CornerAt(px, py, opts.CornerSize);

            /* 「{作用目标} 允许的触发方式」—— 这就是 WGestures 2 目标页那一栏。
             * 某个按键在"当前这个作用目标"上被关掉，就等同于这个键不参与手势：
             * 完全不吞、原样交给系统。判定顺序见 GestureTriggerMap.Allows：
             * 特殊目标（任务栏→桌面）→ 应用程序 → 所属分组 → 全局。 */
            var specials = SpecialTargetsAt();
            var triggers = new GestureTriggerMap(opts);
            if (triggers.IsBlocked(app, specials.Desktop, specials.Taskbar)) return false;
            string triggerKey = TriggerKeyOf(vk, cornerAt);
            if (triggerKey.Length > 0 &&
                !triggers.Allows(app, specials.Desktop, specials.Taskbar, triggerKey))
                return false;

            /* 这里**不能**带 includeWheel：一条"右键 + 滚轮"的手势不应该让
             * 每次右键按下都被吞掉。那种手势在 OnWheel 里靠"物理键是否按下"
             * 识别，不需要开一笔轨迹；否则普通右键也要靠补发合成点击，
             * 在提权窗口/安全软件下就表现为"右键偶尔没反应"。
             *
             * 同理，只有"这一次按下去真有可能触发"的手势才值得吞：
             *   · 单击手势必须开了「单击也触发」；
             *   · 角落手势必须指针真的在对应角落；
             *   · 笔画手势任何位置都可能画，保留。
             * 否则一条用不到的单击/角落手势会让每次右键都走"吞掉 → 注入补发"，
             * 在管理员窗口里补发被 UIPI 丢掉，右键菜单就再也弹不出来。 */
            var candidates = Candidates(settings, vk, app, includeWheel: false)
                .Where(g => CanStart(g, opts, cornerAt))
                .ToList();
            if (candidates.Count == 0) return false;

            /* 防止乱画：同一时间窗里连续起笔太多次就提示并暂停一会儿。
             * 只统计真正会开一笔的按下，不会影响普通右键菜单。 */
            long burstNow = Environment.TickCount64;
            if (burstNow < _gestureBurstBlockedUntil)
            {
                Interlocked.Exchange(ref _swallowUpVk, vk);
                ShowToast("手势太频繁，已达到限制", px, py);
                return true;
            }
            while (_gestureBurst.Count > 0 && burstNow - _gestureBurst.Peek() > GestureBurstWindowMs)
                _gestureBurst.Dequeue();
            if (_gestureBurst.Count >= GestureBurstLimit)
            {
                _gestureBurst.Clear();
                _gestureBurstBlockedUntil = burstNow + GestureBurstCooldownMs;
                Interlocked.Exchange(ref _swallowUpVk, vk);
                ShowToast("手势太频繁，已达到限制", px, py);
                return true;
            }
            _gestureBurst.Enqueue(burstNow);

            lock (_gate)
            {
                _active = true;
                _activeSince = Environment.TickCount64;
                _lastMoveAt = _activeSince;
                _activeInjected = injected;
                _previewTitle = "";
                _previewCount = 0;
                _vk = vk;
                _points.Clear();
                _start = new GestureMath.Pt(px, py);
                _last = _start;
                _points.Add(_start);
                _appPath = app;
                _targetWindow = targetWindow;
                _cornerCandidate = CornerCandidate(candidates, px, py, opts.CornerSize);
                _tracker = new GesturePattern.Tracker(opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance);
                _tracker.Start(px, py);
            }

            var o = opts;
            _linger?.Stop();
            Ui(() =>
            {
                var overlay = EnsureOverlay();
                overlay.Begin(o, GestureButtons.FromVk(vk));
                overlay.ShowPassive();
                overlay.AddPoint(px, py);
                StartPoll(o);
            });
            return true;
        }

        lock (_gate)
        {
            if (!_active || vk != _vk) return false;
        }

        Finish(execute: true);
        return true;
    }

    /// <summary>按住 <paramref name="primaryVk"/> 时又点了 <paramref name="chordVk"/>：按组合手势处理。</summary>
    private bool TryChord(AppSettings settings, int primaryVk, int chordVk, GestureOptions opts)
    {
        string chordName = GestureButtons.FromVk(chordVk);
        if (chordName.Length == 0) return false;

        string app;
        var drawn = new List<GestureMath.Pt>();
        lock (_gate)
        {
            app = _appPath;
            drawn.AddRange(_points);
        }

        /* 还没起手就按第二颗键，不能当成组合手势：那只是用户起手时手抖带了一下别的键。
         * "起手"看指针走了多远（起手距离），不再看方向串 —— v5.13 起没有方向串判定。 */
        if (GestureMath.Extent(drawn) < opts.ActivationDistance) return false;

        var sameKey = Candidates(settings, primaryVk, app, includeWheel: false, includeChords: true)
            .Where(g => string.Equals(g.Chord, chordName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sameKey.Count == 0) return false;

        /* 识别还是那一套：这一笔的形状和"同一颗第二键"的候选比，最像的那条就是它。
         * 通配（"*"）没有形状，跟 PickStroke 一样只当最后兜底。 */
        foreach (var g in sameKey) g.EnsurePattern();
        var shaped = sameKey.Where(g => !g.IsWildcard).ToList();
        MouseGesture? hit = shaped.Count > 0 ? GestureEngine.Match(shaped, drawn, opts).Hit : null;
        hit ??= sameKey.FirstOrDefault(g => g.IsWildcard);

        if (hit == null) return false;

        lock (_gate)
        {
            _active = false;
            _tracker = null;
            _chordSwallow = chordVk;
            /* 组合手势在第二颗键按下时就执行，此时起始键还按着。
             * 它的抬起也必须吞掉，否则系统会收到一个没有按下的 MouseUp。 */
            _swallowUpVk = primaryVk;
        }
        var (cx, cy) = CursorPos();
        Execute(hit, cx, cy);
        SettleOverlay(hit, cx, cy);
        CoreLog.Info($"组合手势命中：{hit.Title(ActionCatalogTitle(hit))}");
        return true;
    }

    private bool HandleRecordButton(int vk, bool down)
    {
        if (down)
        {
            var (px, py) = CursorPos();
            lock (_gate)
            {
                _active = true;
                _vk = vk;
                _points.Clear();
                _start = new GestureMath.Pt(px, py);
                _last = _start;
                _points.Add(_start);
                var recordOpts = _host.Settings.GestureOptions;
                _tracker = new GesturePattern.Tracker(recordOpts.ActivationDistance, recordOpts.TurnTolerance, recordOpts.MaxTurnDistance);
                _tracker.Start(px, py);
            }
            var opts = _host.Settings.GestureOptions;
            _linger?.Stop();
            Ui(() =>
            {
                var overlay = EnsureOverlay();
                overlay.Begin(opts, GestureButtons.FromVk(vk));
                overlay.ShowPassive();
                overlay.AddPoint(px, py);
                StartPoll(opts);
            });
            return true;
        }

        lock (_gate)
        {
            if (!_active || vk != _vk) return false;
        }
        Finish(execute: false);
        return true;
    }

    private bool OnWheel(int direction, bool injected, bool horizontal)
    {
        if (Suspended) return false;
        if (injected) return false;

        var settings = _host.Settings;
        if (settings.GestureOptions is not { Enabled: true })
        {
            if (IsTracking) ResetActive();
            return false;
        }

        int wheel = horizontal ? (direction > 0 ? 2 : -2) : (direction > 0 ? -1 : 1);
        var (cx, cy) = CursorPos();
        var (app, wheelTarget) = ResolveTarget(settings.GestureOptions);
        if (IsTargetDisabled(app))
        {
            if (IsTracking) ResetActive();
            return false;
        }

        bool active;
        int vk;
        string activeApp;
        lock (_gate)
        {
            active = _active;
            vk = _vk;
            activeApp = _appPath;
        }

        if (active)
        {
            var hit = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "wheel" && g.Wheel == wheel &&
                GestureButtons.ToVk(g.Button) == vk && AppMatches(g, activeApp, settings.GestureOptions));

            if (hit == null) return false;

            Execute(hit, cx, cy);
            Finish(execute: false, keepOverlayFor: hit.HideLabel ? null : hit.Title(ActionCatalogTitle(hit)));
            return true;
        }

        /* "角落 + 滚轮"本来就不用按任何键：把手贴到屏幕角上拨一下滚轮。
           WGestures 里音量/亮度那几条就是这个形状，导入后 Button 留空，
           所以这里单独给"没按住键"的情况开一条路。 */
        int corner = CornerAt(cx, cy, settings.GestureOptions.CornerSize);
        if (corner != 0)
        {
            var cornerHit = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "wheel" && g.Wheel == wheel &&
                string.IsNullOrWhiteSpace(g.Button) &&
                (g.Corner == 0 || g.Corner == corner) &&
                AppMatches(g, app, settings.GestureOptions));

            if (cornerHit != null)
            {
                SetActionTarget(wheelTarget);
                Execute(cornerHit, cx, cy);
                ShowLabelOnly(cornerHit, cx, cy);
                return true;
            }
        }

        /* 贴边滚轮（WGestures 的 ScreenEdge 玩法）：指针在屏幕最上沿时
         * 滚轮调音量、最下沿调亮度。 */
        int edge = EdgeAt(cx, cy, settings.GestureOptions.EdgeSize);
        if (edge != 0)
        {
            var edgeHit = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "edgewheel" && g.Edge == edge && g.Wheel == wheel &&
                AppMatches(g, app, settings.GestureOptions));

            if (edgeHit != null)
            {
                SetActionTarget(wheelTarget);
                Execute(edgeHit, cx, cy);
                ShowLabelOnly(edgeHit, cx, cy);
                return true;
            }
        }

        /* 按住某颗鼠标键再滚：按下没有被吞（上面 OnButton 不再为纯滚轮手势
         * 开轨迹），所以这里直接看物理键状态，不依赖 _active。 */
        foreach (int buttonVk in MouseButtonVks)
        {
            if (!InputSender.IsPhysicallyDown(buttonVk)) continue;

            var held = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "wheel" && g.Wheel == wheel &&
                GestureButtons.ToVk(g.Button) == buttonVk && AppMatches(g, app, settings.GestureOptions));

            if (held == null) continue;

            SetActionTarget(wheelTarget);
            Execute(held, cx, cy);
            ShowLabelOnly(held, cx, cy);
            return true;
        }

        return false;
    }

    private static readonly int[] MouseButtonVks =
    {
        0x01, 0x02, 0x04, 0x05, 0x06
    };

    /// <summary>把这一笔的目标窗口记下来；<see cref="IntPtr.Zero"/> = 就用当前焦点窗口。</summary>
    private void SetActionTarget(IntPtr target)
    {
        lock (_gate) _targetWindow = target;
    }

    /// <summary>这一笔不切窗口，动作发给当前前台窗口。</summary>
    private void ClearActionTarget() => SetActionTarget(IntPtr.Zero);

    private void StartHotWatch()
    {
        try
        {
            _hotWatch ??= new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _hotWatch.Tick -= OnHotWatch;
            _hotWatch.Tick += OnHotWatch;
            _hotWatch.Start();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "启动热角检测"); }
    }

    /// <summary>
    /// 重新注册「鼠标手势总开关」热键。设置为空就只注销，不报错。
    /// 由设置页在录制完快捷键后调用，也在 Attach 时调用一次。
    /// </summary>
    public void ApplyHotkey()
    {
        try
        {
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(ApplyHotkey));
                return;
            }

            _hotkeySource ??= CreateHotkeySource();
            if (_hotkeySource == null) return;

            UnregisterHotKey(_hotkeySource.Handle, ToggleHotkeyId);
            string combo = Settings().GestureOptions.ToggleHotkey ?? "";
            var parsed = ParseHotkey(combo);
            if (parsed == null) return;

            if (!RegisterHotKey(_hotkeySource.Handle, ToggleHotkeyId,
                    parsed.Value.Mods | MOD_NOREPEAT, (uint)parsed.Value.Vk))
                CoreLog.Warn($"注册鼠标手势开关热键失败：{combo}");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "注册鼠标手势开关热键"); }
    }

    private HwndSource? CreateHotkeySource()
    {
        try
        {
            var parameters = new HwndSourceParameters("InputPlusGestureHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            var source = new HwndSource(parameters);
            source.AddHook(HotkeyWndProc);
            return source;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "创建手势热键窗口");
            return null;
        }
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == ToggleHotkeyId)
        {
            ToggleGestures();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleGestures()
    {
        var options = Settings().GestureOptions;
        options.Enabled = !options.Enabled;
        if (!options.Enabled) ResetActive();
        _host.SaveSettings();
        _host.Notify(options.Enabled ? Loc.T("鼠标手势已开启") : Loc.T("鼠标手势已关闭"));
        CoreLog.Info($"鼠标手势总开关：{(options.Enabled ? "开" : "关")}");
    }

    private static (uint Mods, int Vk)? ParseHotkey(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        uint mods = 0;
        int vk = 0;
        foreach (string raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= MOD_CONTROL;
                    continue;
                case "shift":
                    mods |= MOD_SHIFT;
                    continue;
                case "alt":
                case "menu":
                    mods |= MOD_ALT;
                    continue;
                case "win":
                case "windows":
                case "lwin":
                    mods |= MOD_WIN;
                    continue;
            }
            int? parsed = KeyName.VkOf(part);
            if (parsed == null || parsed.Value == 0) return null;
            vk = parsed.Value;
        }
        return vk == 0 ? null : (mods, vk);
    }

    /// <summary>
    /// 悬停到屏幕角触发（WGestures 的 HotCorner）。
    ///
    /// 只在"没有按键、没有在画手势、指针真的停在角上"时触发；进入热角时
    /// 如果按着鼠标键（拖窗口贴角），先解除武装，必须离开热角重置区才允许
    /// 再触发 —— 否则把窗口拖到左上角松手就会误触。
    /// </summary>
    private void OnHotWatch(object? sender, EventArgs e)
    {
        try
        {
            var settings = _host.Settings;
            var opts = settings.GestureOptions;
            if (opts is not { Enabled: true } || Suspended || IsRecording || IsTracking) return;

            var (px, py) = CursorPos();
            var (app, hotTarget) = ResolveTarget(opts);
            if (IsTargetDisabled(app)) return;
            int corner = CornerAt(px, py, opts.HotCornerSize);

            if (corner != 0 && AnyMouseButtonDown())
            {
                _hotArmed = false;
                return;
            }

            if (!_hotArmed)
            {
                if (CornerAt(px, py, opts.HotCornerReset) == 0) _hotArmed = true;
                return;
            }

            if (corner == 0) return;

            var hit = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "hotcorner" &&
                (g.Corner == 0 || g.Corner == corner) &&
                AppMatches(g, app, settings.GestureOptions));

            if (hit == null) return;

            _hotArmed = false;
            SetActionTarget(hotTarget);
            Execute(hit, px, py);
            ShowLabelOnly(hit, px, py);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "热角触发"); }
    }

    private static bool AnyMouseButtonDown()
    {
        foreach (int vk in MouseButtonVks)
            if (InputSender.IsPhysicallyDown(vk)) return true;
        return false;
    }

    // ───────────────────────── 采样与收尾 ─────────────────────────

    /// <summary>丢掉当前这一笔（抬起丢了、选项被关掉、进入录制模式）。</summary>
    private void ResetActive()
    {
        Interlocked.Exchange(ref _swallowUpVk, 0);
        lock (_gate)
        {
            _active = false;
            _activeInjected = false;
            _tracker = null;
            _cornerCandidate = null;
            _previewTitle = "";
            _previewCount = 0;
            _points.Clear();
            _chordSwallow = 0;
            _targetWindow = IntPtr.Zero;
        }
        _poll?.Stop();
        HideOverlay(0);
    }

    private void StartPoll(GestureOptions opts)
    {
        _poll ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        _poll.Tick -= OnPoll;
        _poll.Tick += OnPoll;
        _poll.Start();
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        bool stale;
        lock (_gate)
        {
            /* 不能用 GetAsyncKeyState 判断“键还按着没有”：手势起点键的
             * WM_*BUTTONDOWN 已经被我们吞掉，系统队列里根本没有这次按下，
             * 查询结果会是 0。以前这里一进 OnPoll 就把刚开的一笔当成
             * “抬起丢了”清掉，表现就是右键画不出任何手势。
             * 现在只保留 8 秒超时兜底；抬起丢失主要由下一次同键按下复位，
             * 以及 Suspended/禁用时的 ResetActive 处理。 */
            stale = _active && Environment.TickCount64 - _activeSince > 8000;
        }
        if (stale)
        {
            ResetActive();
            return;
        }

        var (px, py) = CursorPos();
        bool add;
        bool tooManySegments = false;
        bool startTimedOut = false;
        string pattern;
        int vk;
        string app;
        int minMove = Settings().GestureOptions.MinMove;
        int timeoutMs = Math.Clamp(Settings().GestureOptions.StartTimeoutMs, 100, 3000);
        List<GestureMath.Pt>? snapshot = null;
        lock (_gate)
        {
            if (!_active) return;
            double dx = px - _last.X, dy = py - _last.Y;
            add = dx * dx + dy * dy >= 4;   // 2px 一处，够画圆也够省内存
            long now = Environment.TickCount64;
            if (add)
            {
                _last = new GestureMath.Pt(px, py);
                _points.Add(_last);
                _lastMoveAt = now;
            }
            if (add) _ = _tracker?.Move(px, py);
            pattern = _tracker?.Pattern ?? "";
            tooManySegments = pattern.Length >= GestureSegmentLimit;
            startTimedOut = !add
                && now - _lastMoveAt >= timeoutMs
                && GestureMath.Extent(_points) < minMove;
            vk = _vk;
            app = _appPath;
            /* 每多记 3 个点判一次：画一条直线时也不会只在起手那一下判过一次。
             * 长度门槛由 UpdatePreview / TryImmediate 自己按 MinMove 卡。 */
            if (_points.Count - _previewCount >= 3)
            {
                _previewCount = _points.Count;
                snapshot = new List<GestureMath.Pt>(_points);
            }
        }

        if (tooManySegments)
        {
            ResetActive();
            Interlocked.Exchange(ref _swallowUpVk, vk);
            ShowToast("手势划动太多，已达到限制", px, py);
            return;
        }

        if (startTimedOut)
        {
            ResetActive();
            Interlocked.Exchange(ref _swallowUpVk, vk);
            ShowTimeoutDot(px, py);
            CoreLog.Info("手势起始超时");
            return;
        }

        if (!add) return;
        if (Settings().GestureOptions.ShowPath)
            _overlay?.AddPoint(px, py);

        /* 画到能认出来就先显示名称，并且随形状变化实时换；
         * 真正命中/松手时再用最终名称覆盖（名字和轨迹一起留到点，见 ShowLabel）。 */
        if (snapshot is not null) UpdatePreview(vk, app, snapshot, px, py);

        /* 这一条开了「识别手势立即执行」：这一笔刚够像某条手势就直接执行，
         * 不再等鼠标抬起。覆盖层只显示最终手势名。 */
        if (snapshot is not null && TryImmediate(vk, app, snapshot, px, py)) return;
    }

    private void UpdatePreview(int vk, string app, List<GestureMath.Pt> points, double px, double py)
    {
        var opts = Settings().GestureOptions;
        if (GestureMath.Extent(points) < opts.MinMove) return;

        var hit = PickStroke(Settings(), vk, app, points, opts).Hit;
        if (hit == null)
        {
            if (_previewTitle.Length == 0) return;
            _previewTitle = "";
            Ui(() => _overlay?.HideLabelOnly());
            return;
        }

        string title = hit.Title(ActionCatalogTitle(hit));
        if (title == _previewTitle) return;
        _previewTitle = title;
        Ui(() =>
        {
            var overlay = EnsureOverlay();
            overlay.ShowPassive();
            /* 认出来的这一刻轨迹换成「已识别」色（WG2 是 A/T.cs:130 一次性切色）。 */
            overlay.MarkRecognized();
            overlay.ShowLabel(title, px, py, keepTrail: true);
        });
    }

    /// <summary>
    /// 画到已经能唯一确定一条「立即执行」手势时，不再等鼠标抬起。
    /// 只在 OnPoll 里调用（界面线程）。
    /// </summary>
    private bool TryImmediate(int vk, string app, List<GestureMath.Pt> points, double px, double py)
    {
        var opts = Settings().GestureOptions;
        if (GestureMath.Extent(points) < opts.MinMove) return false;

        lock (_gate)
        {
            if (!_active || _vk != vk) return false;
        }

        var strokes = Candidates(Settings(), vk, app, includeWheel: false)
            .Where(g => g.Kind == "stroke")
            .ToList();
        if (strokes.Count == 0) return false;
        foreach (var g in strokes) g.EnsurePattern();

        /* 画到 ↑ 时如果还配了 ↑→，现在执行就会把长手势永远截断。
         * 等这一笔不再像任何"更长候选的前一段"，才算能确定 —— 判据按形状比，
         * 见 GestureEngine.AwaitsLonger。 */
        if (GestureEngine.AwaitsLonger(strokes, points, opts)) return false;

        strokes = strokes.Where(g => g.Immediate).ToList();
        if (strokes.Count == 0) return false;

        var scoped = strokes.Where(g => !string.IsNullOrWhiteSpace(g.AppPath)).ToList();
        var global = strokes.Where(g => string.IsNullOrWhiteSpace(g.AppPath)).ToList();

        MouseGesture? hit = null;
        double score = 1;

        /* 立即执行要更保守：WGestures 引擎的阈值再收紧一档、对"两条都像"零容忍
         * —— 画到一半猜错就是误触发。 */
        var (immediateHit, immediateScore) = GestureEngine.Match(scoped, points, opts, immediate: true);
        if (immediateHit == null)
            (immediateHit, immediateScore) = GestureEngine.Match(global, points, opts, immediate: true);
        if (immediateHit != null) { hit = immediateHit; score = immediateScore; }

        /* 通配手势只认形状真正匹配的，不能像 PickStroke 那样兜底取第一条：
         * 画到一半就执行，猜错一次就是误触发。没存过轨迹的通配（纯 "*"）
         * 在这里不参与 —— 它没有形状可比，画到一半时无从判断"猜对没猜对"。 */
        if (hit == null)
        {
            foreach (var bucket in new[] { scoped, global })
            {
                var wild = bucket.Where(g => g.IsWildcard && GestureEngine.CanMatch(g)).ToList();
                if (wild.Count == 0) continue;
                var (candidate, matchScore) = GestureEngine.Match(wild, points, opts, immediate: true);
                if (candidate != null) { hit = candidate; score = matchScore; break; }
            }
        }

        if (hit == null) return false;

        /* 先记下“这次抬起要吞”，再复核状态：抬起可能刚好抢先跑完 Finish。 */
        Interlocked.Exchange(ref _swallowUpVk, vk);
        lock (_gate)
        {
            if (!_active || _vk != vk)
            {
                Interlocked.Exchange(ref _swallowUpVk, 0);
                return false;
            }
            _active = false;
            _activeInjected = false;
            _tracker = null;
            _cornerCandidate = null;
            _points.Clear();
        }
        _poll?.Stop();

        Execute(hit, px, py);
        /* 立即执行模式：命令下去了，覆盖层按 WG2 的「执行后淡出」规则收
         * （见 SettleOverlay / GestureOptions.FadeOutExecuted）。 */
        SettleOverlay(hit, px, py);
        CoreLog.Info($"手势立即执行：{hit.Title(ActionCatalogTitle(hit))}（偏差 {score:0.000}）");
        return true;
    }

    /// <summary>
    /// 收尾：先判定，再决定是执行动作、还是把这一下还原成普通单击。
    ///
    /// 「还原成单击」是右键手势能用的前提 —— 我们吞掉了按下与抬起，
    /// 如果没画出手势又不补发一次右键，用户的右键菜单就永远打不开了。
    /// </summary>
    private void Finish(bool execute, string? keepOverlayFor = null)
    {
        bool recording;
        lock (_gate) recording = IsRecording;
        List<GestureMath.Pt> points;
        int vk;
        string app;
        MouseGesture? corner;
        string pattern;
        lock (_gate)
        {
            _active = false;
            _activeInjected = false;
            points = new List<GestureMath.Pt>(_points);
            _points.Clear();
            vk = _vk;
            app = _appPath;
            corner = _cornerCandidate;
            _previewTitle = "";
            _previewCount = 0;
            pattern = _tracker?.Pattern ?? "";
            _tracker = null;
        }
        _poll?.Stop();

        var opts = Settings().GestureOptions;
        double extent = GestureMath.Extent(points);
        var (px, py) = CursorPos();

        /* 不是录制、也不是松手结算：这是滚轮在跟踪中途按到了动作，
         * 只停止这一笔并保留一下名称。以前这里会误走录制分支。 */
        if (!execute && !recording)
        {
            if (string.IsNullOrWhiteSpace(keepOverlayFor)) HideOverlay(0);
            else ShowLabel(keepOverlayFor!, px, py);
            return;
        }

        MouseGesture? hit = null;
        double score = 1;
        if (execute)
        {
            if (extent >= opts.MinMove)
            {
                (hit, score) = PickStroke(Settings(), vk, app, points, opts);
            }
            else if (corner != null)
            {
                hit = corner;
            }

            if (hit == null && extent < opts.MinMove)
            {
                /* 没画出形状 → 用户只是想点一下。补发一次被吞掉的按键。
                 * 带注入标记，所以不会再被自己的钩子接住。 */
                /* 补发不能在钩子回调里做：SendInput 会同步把事件送回同一个
                 * 钩子线程，回调里再发就自锁，要等系统超时（实测 1 秒）才返回，
                 * 表现就是"按一下鼠标，整台电脑卡一下"。丢到线程池发。 */
                lock (_gate) { _replayUntil[vk] = Environment.TickCount64 + 300; }
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { InputSender.Tap(vk); }
                    catch (Exception ex) { CoreLog.Swallow(ex, "手势补发单击"); }
                });
            }
        }
        else
        {
            // 录制模式：把轨迹交回界面，不执行
            Action<MouseGesture>? done;
            lock (_gate)
            {
                done = _recordDone;
                _recordDone = null;
                IsRecording = false;
            }
            var recorded = new MouseGesture
            {
                Id = MouseGesture.NewId(),
                Button = GestureButtons.FromVk(vk),
                Kind = "stroke",
                Path = GestureMath.ToFlat(GestureMath.Normalize(points)),
                Pattern = GesturePattern.FromPoints(
                    points, opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance)
            };
            HideOverlay(0);
            try { done?.Invoke(recorded); }
            catch (Exception ex) { CoreLog.Swallow(ex, "手势录制回调"); }
            return;
        }

        if (hit != null)
        {
            Execute(hit, px, py);
            string title = hit.Title(ActionCatalogTitle(hit));
            /* 松手结算模式：画的过程中方框一直在（预览），用户松手 →
             * 结算并执行 → 名称换成「已执行」配色再淡出。 */
            SettleOverlay(hit, px, py);
            CoreLog.Info($"手势命中：{title}（偏差 {score:0.000}）");
        }
        else
        {
            if (extent >= opts.MinMove)
            {
                ShowUnrecognized(px, py);
                CoreLog.Info($"手势未命中（偏差 {score:0.000}）");
            }
            else HideOverlay(0);
        }
    }

    private void Execute(MouseGesture g, double px, double py)
    {
        if (string.IsNullOrWhiteSpace(g.Action)) return;
        string action = g.Action;
        string arg = g.Arg;
        /* 老配置里可能残留上一个动作的参数（例如从 sendKeys 换成“直接搜索”）。
         * 不需要参数的动作必须把 Arg 清掉，否则 SearchSelection 会把残留串
         * 当成“显式关键词”，反而搜不到当前选中的文字。 */
        if (_host.Actions.Get(action) is { NeedsArg: false } actionInfo && actionInfo.Id != "customKey") arg = "";

        IntPtr target;
        lock (_gate)
        {
            target = _targetWindow;
            _targetWindow = IntPtr.Zero;
        }

        Interlocked.Exchange(ref _gestureSuppressUntil, Environment.TickCount64 + 700);
        Ui(() =>
        {
            void Run()
            {
                try { _host.InvokeAction(action, arg); }
                catch (Exception ex) { CoreLog.Swallow(ex, "执行手势动作"); }
            }

            /* 先切窗口再发键：快捷键型动作（Ctrl+W / Ctrl+C …）落在焦点窗口上，
             * 而画手势时那颗按键被我们吞了，焦点还停在上一个窗口。切过去
             * 等一帧，让目标窗口真的拿到焦点，再发键。 */
            if (target != IntPtr.Zero)
            {
                try
                {
                    if (WindowService.Activate(target))
                    {
                        /* 切完窗口不要睡 70ms：把动作排到输入优先级队列的下一拍，
                         * 既让焦点切换落地，又满足"松手立刻生效"。 */
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null)
                            dispatcher.BeginInvoke(new Action(Run), DispatcherPriority.Input);
                        else
                            Run();
                        return;
                    }
                }
                catch (Exception ex) { CoreLog.Swallow(ex, "切到手势目标窗口"); }
            }

            Run();
        });
    }

    /// <summary>
    /// 指针是不是在任务栏/托盘上。Explorer 的托盘右键菜单不认注入的右键，
    /// 所以这里不接管；直接让 Explorer 自己处理。
    /// </summary>
    private static bool IsShellTaskbarAtCursor()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var pt)) return false;
            var hwnd = NativeMethods.WindowFromPoint(pt);
            for (int i = 0; i < 2 && hwnd != IntPtr.Zero; i++)
            {
                var sb = new StringBuilder(64);
                if (NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0)
                {
                    string cls = sb.ToString();
                    if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                        or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")
                        return true;
                }
                hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            }

            /* 任务栏自动隐藏时，指针可能落在已经缩下去的空白处，
             * WindowFromPoint 拿不到 Shell_TrayWnd；底部这条带子直接让给 Explorer。 */
            int screenH = NativeMethods.GetSystemMetrics(1); // SM_CYSCREEN
            if (screenH > 0 && pt.Y >= screenH - 80) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 指针现在是不是压在「特殊目标」上（桌面 / 任务栏）。
    /// WGestures 2 的目标树里这两项不按 exe 匹配，而是按壳窗口匹配
    /// （类名 Progman / WorkerW / Shell_TrayWnd…），所以单独判一次。
    /// </summary>
    private static (bool Desktop, bool Taskbar) SpecialTargetsAt()
    {
        bool taskbar = IsShellTaskbarAtCursor();
        bool desktop = false;
        try
        {
            if (NativeMethods.GetCursorPos(out var pt))
            {
                IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
                if (hwnd != IntPtr.Zero)
                {
                    string? cls = WindowService.ClassNameOf(hwnd);
                    if (cls == null || cls is "Progman" or "WorkerW" or "SHELLDLL_DefView")
                    {
                        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
                        string? rootCls = root == IntPtr.Zero ? null : WindowService.ClassNameOf(root);
                        desktop = rootCls is "Progman" or "WorkerW";
                    }
                }
                else
                {
                    // 窗口底下什么都没有：就是桌面。
                    desktop = true;
                }
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "判断特殊作用目标"); }
        return (desktop, taskbar);
    }

    /// <summary>
    /// 这一笔按下的「触发方式」键，格式和 WGestures 2 的 TriggerDef 一致：
    /// 普通按下是 <c>mouse:C</c>；在屏幕边角按下是 <c>edge:V:mouse:C</c>
    /// （V 是 RectEdge/RectCorner 的位掩码，见 <see cref="Wg2Triggers.MaskOfCorner"/>）。
    /// </summary>
    internal static string TriggerKeyOf(int vk, int cornerAt)
    {
        string mouse = Wg2Triggers.MouseKeyOfVk(vk);
        if (mouse.Length == 0) return "";
        return cornerAt == 0 ? mouse : "edge:" + Wg2Triggers.MaskOfCorner(cornerAt) + ":" + mouse;
    }

    /// <summary>鼠标位置下的顶层窗口；属于本进程（设置页/浮窗）时返回 0，不去切它。</summary>
    private static IntPtr RootWindowAtCursor()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var pt)) return IntPtr.Zero;
            var hwnd = NativeMethods.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;

            hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return IntPtr.Zero;
            return hwnd;
        }
        catch { return IntPtr.Zero; }
    }

    private static string ActionCatalogTitle(MouseGesture g)
        => ActionCatalog.TitleOf(g.Action) ?? "";

    // ───────────────────────── 查询 ─────────────────────────

    /// <summary>
    /// 这一次按下是否值得开一笔手势。只有真正可能触发的触发方式才拦：
    /// 角落手势看指针是否真在对应角落，笔画手势任何位置都可能画。
    /// 用不到的手势如果也拦，右键就会被迫走“吞掉 → 注入补发”，提权窗口里补发被
    /// UIPI 丢掉，表现就是右键菜单偶尔不弹。
    /// </summary>
    internal static bool CanStart(MouseGesture g, GestureOptions opts, int cornerAt) => g.Kind switch
    {
        "corner" => cornerAt != 0 && (g.Corner == 0 || g.Corner == cornerAt),
        "stroke" => true,
        _ => false
    };

    private static List<MouseGesture> Candidates(AppSettings settings, int vk, string app, bool includeWheel, bool includeChords = false)
    {
        var list = new List<MouseGesture>();
        foreach (var g in settings.Gestures)
        {
            if (!g.Enabled) continue;
            if (!includeChords && g.Chord.Length > 0) continue;   // 组合手势只在按住时点第二键触发
            if (GestureButtons.ToVk(g.Button) != vk) continue;
            if (!includeWheel && g.Kind == "wheel") continue;
            if (g.Kind == "stroke")
            {
                g.EnsurePattern();
                if (!g.HasPath && string.IsNullOrEmpty(g.Pattern)) continue;
            }
            if (!AppMatches(g, app, settings.GestureOptions)) continue;
            list.Add(g);
        }
        return list;
    }

    /// <summary>
    /// 笔画匹配：应用手势优先。给某个程序单独配过的手势，先只在"这个程序的"里找；
    /// 找不到再退回全局的 —— 否则全局的"↑ 上滚"会抢在浏览器的"↑ 新标签页"前面。
    ///
    /// 识别只有一套：<see cref="GestureEngine"/>（WGestures 2 的笔画引擎）。
    /// 记过轨迹的手势直接比形状；只有方向串的存量手势由 <see cref="GesturePattern.Sketch"/>
    /// 把方向串画成等长折线，进同一个引擎、同一个阈值 —— v5.13 起没有
    /// "形状没中就退回方向串"的第二层判定，也不再出现两套刻度谁更像。
    ///
    /// 顺序是硬的：应用专属 → 全局 → 应用专属通配 → 全局通配。
    /// 这样"浏览器里 ↑ 是新标签、桌面 ↑ 是上滚"能同时成立，而通配只是兜底，
    /// 永远不会抢走一条真配了笔画的手势。
    /// </summary>
    internal static (MouseGesture? Hit, double Score) PickStroke(
        AppSettings settings, int vk, string app,
        IReadOnlyList<GestureMath.Pt> points, GestureOptions opts)
    {
        var strokes = Candidates(settings, vk, app, includeWheel: false)
            .Where(g => g.Kind == "stroke")
            .ToList();
        foreach (var g in strokes) g.EnsurePattern();

        var scoped = strokes.Where(g => !string.IsNullOrWhiteSpace(g.AppPath)).ToList();
        var global = strokes.Where(g => string.IsNullOrWhiteSpace(g.AppPath)).ToList();

        /* ① 应用专属 → ② 全局：都交给 GestureEngine 打分（记过轨迹的比形状；
         * 只有方向串的先合成折线再比形状）。分数刻度 1.0 = 刚好卡在阈上。 */
        foreach (var bucket in new[] { scoped, global })
        {
            var (hit, score) = GestureEngine.MatchRelease(bucket, points, opts);
            if (hit != null) return (hit, score);
        }

        /* ③④ 通配 * 永远最后，不会抢走一条真配了笔画的手势。 */
        foreach (var bucket in new[] { scoped, global })
        {
            var wild = bucket.Where(g => g.IsWildcard).ToList();
            if (wild.Count == 0) continue;
            /* 通配就是"随便什么形状都算"：不看形状、不打分，直接命中第一条。
             * 它会走到这里，本身就意味着前面没有任何一条真配了形状的手势接住这一笔。 */
            return (wild[0], 0);
        }

        return (null, 1);
    }

    /// <summary>
    /// 这条手势在这个程序里算不算数。作用域为空 = 全局，任何程序都算。
    /// 分组作用域（group:xxx）按组里的程序列表匹配 —— 这是 WGestures 分组的本意：
    /// 一组程序共用一套手势，不是给组另存一份。
    /// </summary>
    internal static bool AppMatches(MouseGesture g, string foreground, GestureOptions options)
    {
        if (string.IsNullOrWhiteSpace(g.AppPath)) return true;
        if (g.AppPath.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
        {
            var group = options.TargetList?.FirstOrDefault(t =>
                string.Equals(t.Id, g.AppPath, StringComparison.OrdinalIgnoreCase));
            if (group?.Members == null || group.Members.Count == 0) return false;
            return group.Members.Any(m => SameApp(m, foreground));
        }
        return SameApp(g.AppPath, foreground);
    }

    /// <summary>这个程序是否被用户设成“所有手势都不触发”。</summary>
    private bool IsTargetDisabled(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return false;
        var disabled = Settings().GestureOptions.DisabledTargets;
        if (disabled == null || disabled.Count == 0) return false;
        foreach (string target in disabled)
            if (SameApp(target, app)) return true;
        return false;
    }

    private static bool SameApp(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return string.Equals(
                System.IO.Path.GetFileNameWithoutExtension(a),
                System.IO.Path.GetFileNameWithoutExtension(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static MouseGesture? CornerCandidate(List<MouseGesture> candidates, double px, double py, int size)
    {
        int corner = CornerAt(px, py, size);
        if (corner == 0) return null;
        return candidates.FirstOrDefault(g => g.Kind == "corner" && (g.Corner == 0 || g.Corner == corner));
    }

    /// <summary>指针在哪个角：1 左上、2 右上、3 左下、4 右下、0 不在角上。</summary>
    public static int CornerAt(double px, double py, int size)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);

        bool nearLeft = px <= left + size;
        bool nearRight = px >= right - size;
        bool nearTop = py <= top + size;
        bool nearBottom = py >= bottom - size;

        if (nearLeft && nearTop) return 1;
        if (nearRight && nearTop) return 2;
        if (nearLeft && nearBottom) return 3;
        if (nearRight && nearBottom) return 4;
        return 0;
    }

    /// <summary>指针贴着哪条屏幕边：1 上、2 右、3 下、4 左、0 不在边上。</summary>
    public static int EdgeAt(double px, double py, int size)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);

        bool nearLeft = px <= left + size;
        bool nearRight = px >= right - size;
        bool nearTop = py <= top + size;
        bool nearBottom = py >= bottom - size;

        if (nearTop) return 1;
        if (nearRight) return 2;
        if (nearBottom) return 3;
        if (nearLeft) return 4;
        return 0;
    }

    /// <summary>桌面、任务栏这类壳窗口不算"应用"，匹配手势时仍按活动窗口算。</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>
    /// 这一次手势该作用在哪个窗口上 —— 由设置里的「手势作用目标」决定。
    /// 返回 (程序路径, 执行动作前要切过去的窗口)：
    ///   · pointer（默认）= 指针下方的应用，同时把那个窗口记成目标窗口，执行前切过去；
    ///   · active = 当前活动窗口，目标窗口留空 —— 不切窗口，动作直接发给焦点窗口。
    ///
    /// 指针底下是桌面/任务栏、或者拿不到进程路径时退回活动窗口：
    /// 否则用户一旦在「作用目标」里禁用了 explorer.exe，桌面上就再也画不出手势。
    /// </summary>
    internal static (string App, IntPtr Window) ResolveTarget(GestureOptions opts)
    {
        if (opts is not { UsesPointerTarget: true }) return (ForegroundExe(), IntPtr.Zero);

        IntPtr hwnd = RootWindowAtCursor();
        if (hwnd == IntPtr.Zero) return (ForegroundExe(), IntPtr.Zero);

        string? cls = WindowService.ClassNameOf(hwnd);
        if (cls != null && ShellClasses.Contains(cls)) return (ForegroundExe(), IntPtr.Zero);

        string exe = ExeOfWindow(hwnd);
        return exe.Length == 0 ? (ForegroundExe(), IntPtr.Zero) : (exe, hwnd);
    }

    /// <summary>自检用：按指定作用目标解析一次，只读、不改任何运行状态。</summary>
    internal static (string App, IntPtr Window) ResolveTargetForTest(string mode) =>
        ResolveTarget(new GestureOptions { TargetMode = mode });

    private static string ExeOfWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return "";
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == 0 ? "" : WindowService.GetProcessPath(pid);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "取窗口程序路径");
            return "";
        }
    }

    private static string ForegroundExe() => ExeOfWindow(WindowService.Foreground);

    private AppSettings Settings() => _host.Settings;

    // ───────────────────────── 覆盖层 ─────────────────────────

    private void Ui(Action action)
    {
        var app = Application.Current;
        if (app == null) return;
        try
        {
            if (app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.BeginInvoke(action);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "手势界面调度"); }
    }

    private GestureOverlayWindow EnsureOverlay()
    {
        if (_overlay != null) return _overlay;
        _overlay = new GestureOverlayWindow();
        _overlay.Closed += (_, _) => _overlay = null;
        return _overlay;
    }

    /// <summary>没有轨迹的一次性触发（角落手势、角落 + 滚轮）：只闪一下名字。</summary>
    private void ShowLabelOnly(MouseGesture g, double px, double py)
    {
        if (g.HideLabel) { HideOverlay(0); return; }
        EnsureOverlay();
        ShowLabel(g.Title(ActionCatalogTitle(g)), px, py);
    }

    private void ShowUnrecognized(double px, double py)
    {
        Ui(() =>
        {
            var overlay = EnsureOverlay();
            overlay.ShowPassive();
            overlay.ShowUnrecognized(px, py);
            /* 「未识别」和轨迹一起留、一起变灰、一起消失。
             * 原来的 120ms 根本看不清 —— 用户看到的只是轨迹闪一下就没了，
             * 也不知道是"没认出来"还是"根本没触发"。 */
            HideOverlay(UnrecognizedHoldMs(Settings().GestureOptions));
        });
    }

    private void ShowTimeoutDot(double px, double py)
    {
        /* 「超时后显示此标志」关掉时，超时就只是安静地结束，屏幕什么都不画。 */
        if (!Settings().GestureOptions.ShowStartTimeoutIndicator)
        {
            HideOverlay(0);
            return;
        }
        Ui(() =>
        {
            var overlay = EnsureOverlay();
            overlay.ShowPassive();
            overlay.ShowTimeoutDot(px, py);
            HideOverlay(700);
        });
    }

    private void ShowToast(string text, double px, double py)
    {
        Ui(() =>
        {
            var overlay = EnsureOverlay();
            overlay.ShowPassive();
            overlay.ShowLabel(text, px, py);
            HideOverlay(1600);
        });
        CoreLog.Info($"显示手势提示：{text}");
    }

    /// <param name="holdMs">
    /// 这次提示停留多久；null = 用配置里的 <see cref="LabelHoldMs"/>。
    /// 命中并执行了动作时**不走这里**（直接 <see cref="HideOverlay"/> 收掉，
    /// 见 <see cref="HitLabelLingerMs"/>），这里只剩预览 / 未识别 / 滚轮换层。
    /// </param>
    private void ShowLabel(string text, double px, double py, int? holdMs = null)
    {
        Ui(() =>
        {
            /* 兜底创建：立即执行/特殊路径可能还没建过覆盖层；
             * ShowLabel 以前只在 _overlay 非空时显示，导致日志有名字但屏幕没有。 */
            var overlay = EnsureOverlay();
            /* 无条件重新置顶：手势动作经常紧接着激活/打开一个窗口
             * （比如 Win+E 打开资源管理器），如果只在“首次显示”时置顶，
             * 新窗口会把胶囊压到下面，用户就只看到轨迹、看不到名字。 */
            overlay.ShowPassive();
            /* 名称和轨迹一起留、一起消失。
             *
             * 以前这里是 keepTrail=false：命中瞬间轨迹被清掉，名称还在屏幕
             * 底部多挂 1.2 秒 —— 用户看到的就是「手势已经没了，名字还赖着」。
             * 现在轨迹留着陪名称，HideOverlay 到点时把两者一起清掉。 */
            overlay.ShowLabel(text, px, py, keepTrail: true);

            /* 动作打开的窗口可能在这一帧之后才完成激活，160ms 后再顶一次，
             * 防止资源管理器/浏览器把胶囊盖住。只顶一次，不改变窗口状态。 */
            var raise = new DispatcherTimer(DispatcherPriority.Send)
            {
                Interval = TimeSpan.FromMilliseconds(160)
            };
            raise.Tick += (_, _) =>
            {
                raise.Stop();
                overlay.ShowPassive();
            };
            raise.Start();
        });
        CoreLog.Info($"显示手势名：{text}");
        /* 这段时长同时也是轨迹留在屏幕上的时长 —— HideOverlay 到点把
         * 名称和轨迹一起清掉，两者不会分先后消失。
         * 命中执行时根本不到这里（当场 HideOverlay 收掉），只有预览/滚轮那种
         * 没有执行动作的提示才按配置的 1.2 秒以上留着。 */
        HideOverlay(holdMs ?? LabelHoldMs(Settings().GestureOptions));
    }

    /// <summary>
    /// 手势命中后，名称框（以及陪它的轨迹）在屏幕上停留多久。
    ///
    /// 单独抽出来是为了自检能盯住这条不变量：名称和轨迹**同一时刻**消失，
    /// 而不是轨迹立刻没、名称再挂一段时间。
    /// </summary>
    internal static int LabelHoldMs(GestureOptions opts)
        => Math.Max(1200, opts.LabelLingerMs);

    /// <summary>
    /// 命中并**执行了动作**之后，名称框（连同轨迹）还留在屏幕上多久 —— <b>0，立刻消失</b>。
    ///
    /// 用户定的规则（两种模式都要"执行完就没"）：
    /// ① 手势勾了「识别手势立即执行」→ 方向串一对上就执行，执行那一刻方框立刻收；
    /// ② 没勾 → 画的过程中方框跟着预览（<see cref="UpdatePreview"/>），
    ///    等鼠标键松开、<see cref="Finish"/> 结算执行，**松手这一刻**立刻收。
    /// 两种情况都不留尾巴；只有"没执行动作"的提示（预览、未识别、滚轮换层）
    /// 才按 <see cref="LabelHoldMs"/> / <see cref="UnrecognizedHoldMs"/> 继续留着。
    /// </summary>
    internal const int HitLabelLingerMs = 0;

    /// <summary>
    /// 「未识别」闪一下就走：手画完没匹配到任何手势时，灰色轨迹和「未识别」两个字
    /// 只保留 <see cref="UnrecognizedFlashMs"/> 就消失。
    ///
    /// 用户定的规则（原话）："没有匹配到任何手势，当画完手势的时候就只保留灰色一瞬间就消失"。
    /// 所以这里**不跟**「名称停留时间」联动 —— 那是给"已执行"的回执用的，
    /// 没认出来的东西不该在屏幕上占位。名称和轨迹走同一个定时器
    /// （见 <see cref="HideOverlay"/>），到点一起清掉。
    /// </summary>
    internal const int UnrecognizedFlashMs = 120;

    /// <summary>未识别的停留毫秒数（保留 <paramref name="opts"/> 参数只是为了不动调用点与自检签名）。</summary>
    internal static int UnrecognizedHoldMs(GestureOptions opts) => UnrecognizedFlashMs;

    /// <summary>
    /// 命中并执行之后覆盖层怎么收 —— WGestures 2 的「执行后淡出」：
    /// 勾了 FadeOutExecuted（默认）→ 名称换「已执行」配色，停 100ms 后消失；
    /// 没勾 → 停 250ms，再用 250ms 缓入淡出。手势自己勾了「执行时不显示手势名称」
    /// 或者全局关了「显示手势名称」，就当场清掉，一个像素都不留。
    /// </summary>
    private void SettleOverlay(MouseGesture g, double px, double py)
    {
        var opts = Settings().GestureOptions;
        if (g.HideLabel || !opts.ShowGestureName)
        {
            HideOverlay(HitLabelLingerMs);
            return;
        }

        string title = g.Title(ActionCatalogTitle(g));
        Ui(() =>
        {
            var overlay = EnsureOverlay();
            overlay.ShowPassive();
            overlay.MarkRecognized();
            overlay.ShowLabel(title, px, py, keepTrail: true, executed: true);
        });
        HideOverlayFading();
    }

    /// <summary>立刻开始淡出名称框，淡完再清窗口（WG2 的 T.cs:141-150）。</summary>
    private void HideOverlayFading()
    {
        _linger?.Stop();
        Ui(() =>
        {
            int fade = _overlay?.FadeOutLabel() ?? 0;
            if (fade <= 0)
            {
                _overlay?.Clear();
                _overlay?.Hide();
                return;
            }

            _fade ??= new DispatcherTimer(DispatcherPriority.Background);
            _fade.Stop();
            _fade.Interval = TimeSpan.FromMilliseconds(fade);
            _fade.Tick -= OnFadeDone;
            _fade.Tick += OnFadeDone;
            _fade.Start();
        });
    }

    private void OnFadeDone(object? sender, EventArgs e)
    {
        _fade?.Stop();
        Ui(() =>
        {
            _overlay?.Clear();
            _overlay?.Hide();
        });
    }

    private void HideOverlay(int delayMs)
    {
        _linger?.Stop();
        _fade?.Stop();
        if (delayMs <= 0)
        {
            Ui(() =>
            {
                _overlay?.Clear();
                _overlay?.Hide();
            });
            return;
        }

        _linger ??= new DispatcherTimer(DispatcherPriority.Background);
        _linger.Stop();
        _linger.Interval = TimeSpan.FromMilliseconds(delayMs);
        _linger.Tick -= OnLinger;
        _linger.Tick += OnLinger;
        _linger.Start();
    }

    private void OnLinger(object? sender, EventArgs e)
    {
        _linger?.Stop();
        _fade?.Stop();
        Ui(() =>
        {
            _overlay?.Clear();
            _overlay?.Hide();
        });
    }

    // ───────────────────────── Win32 ─────────────────────────

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static (double X, double Y) CursorPos()
    {
        try
        {
            if (GetCursorPos(out var p)) return (p.X, p.Y);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "读取指针位置"); }
        return (0, 0);
    }
}
