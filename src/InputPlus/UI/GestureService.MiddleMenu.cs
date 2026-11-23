using System;
using InputPlus.Core;
using InputPlus.Core.Actions;
using InputPlus.Core.Gestures;
using InputPlus.Core.Interop;
using InputPlus.Core.Qbar;
using InputPlus.Core.Scripting;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;
using InputPlus.UI.Windows;

namespace InputPlus.UI;

/// <summary>
/// 手势服务里"中键在空白处弹菜单"那一段（本项目 v1.3.16）。
///
/// 拆成 partial 文件而不是继续往 <c>GestureService.cs</c> 里堆：那一个文件已经 2500 行，
/// 钩子、识别、覆盖层三件事挤在一起；中键菜单是一条**独立的入口**，
/// 它只借用 OnButton 的四个挂点，逻辑全在这里。
///
/// 四条路径的落点（都在 GestureService.cs，注释里标了"中键菜单"）：
///   ① 菜单开着时再按中键 → 关掉并配对吞掉；
///   ② 抬起配对分支（Line ~459）→ 路径 A 的收尾；
///   ③ 没有中键手势候选 → 路径 A 的按下；
///   ④ Finish() 的「没成笔」分支 → 路径 B 弹菜单代替补发中键；
///   ⑤ OnPoll 的起始超时 → 慢按也算一次点击。
///
/// 三条不许破的规矩：
///   · 吞不吞必须配对：down 吞了 up 就必须吞，靠现有的 <c>_swallowUpVk</c>，不另造一套；
///   · 命中判定只在**按下**时算一次，抬起零等待（用户要的是"点了就出来"）；
///   · 注入事件（injected）一律不弹，<c>InputSender.TakeGesturePass</c> 那套通行证原样不动。
/// </summary>
public sealed partial class GestureService
{
    /// <summary>中键的虚拟键码。KeyboardHook 已经把 WM_MBUTTONDOWN/UP 折成 0x04 送进来。</summary>
    private const int MiddleVk = 0x04;

    /// <summary>键盘唤出的热键 id（和手势总开关那颗错开）。</summary>
    private const int MiddleHotkeyId = 0x4B48;

    /// <summary>
    /// 这一下按键是不是「鼠标唤出附加菜单」的键。
    ///
    /// v1.3.18 起可选中键 / 侧键 1 / 侧键 2（设置里多选），出厂只开中键。
    /// 鼠标这一路**只在系统桌面**算数：别的窗口里中键是自动滚动、侧键是前进后退。
    /// </summary>
    private bool IsMenuButton(int vk)
    {
        var list = Settings().MiddleMenuMouseButtons;
        /* 空列表按"中键"处理：老配置里这个字段根本不存在，
         * 而"空 = 全部关闭"会让升级上来的用户直接失去鼠标唤出（实际就发生过一次）。 */
        if (list == null || list.Count == 0) return vk == MiddleVk;
        foreach (var name in list)
        {
            if (Core.Gestures.GestureButtons.ToVk(name) == vk) return true;
        }
        return false;
    }

    /// <summary>
    /// 「全屏程序里不接管」—— 和鼠标手势里那个开关是**同一个设置**
    /// （<see cref="GestureOptions.DisableInFullscreen"/>），逻辑也一样：
    /// 只有用户打开了它、且前台是真正的全屏程序才拦。附加菜单直接复用，不另立一份。
    /// </summary>
    private bool FullscreenBlocksMiddleMenu()
    {
        try
        {
            return Settings().GestureOptions.DisableInFullscreen && ForegroundIsFullscreen();
        }
        catch { return false; }
    }

    /// <summary>前台程序是不是用户在「例外程序」里加过的那几个。</summary>
    private bool IsMiddleMenuException(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        try
        {
            foreach (var raw in Settings().MiddleMenuExceptions ?? new List<string>())
            {
                string item = (raw ?? "").Trim();
                if (item.Length == 0) continue;
                if (string.Equals(item, exe, StringComparison.OrdinalIgnoreCase)) return true;
                // 只写了 exe 名（没有路径）也认：用户从「点选窗口」加进来的常常只是名字
                if (string.Equals(Path.GetFileName(item), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 按下时记下的"这一次中键点击"。
    ///
    /// 为什么按下就记：抬起那一刻要立刻弹菜单，任何跨进程查询（UIA）都会变成可感知的延迟。
    /// 所以命中判定在按下时做完（用户还按着，感觉不到），抬起只读这里的结论。
    /// </summary>
    private struct MiddleTap
    {
        public bool Armed;

        /// <summary>这一下该弹菜单（桌面空白 / 资源管理器 / 文件对话框）。</summary>
        public bool Takeable;

        /// <summary>落在哪种界面上（决定收藏星标该收藏什么）。</summary>
        public MiddleSurface Surface;

        public GestureMath.Pt Start;

        /// <summary>判定原因，只给诊断日志用。</summary>
        public string Why;
    }

    private MiddleTap _middleTap;

    /* 当前开着的菜单窗。钩子线程读、界面线程写，所以是 volatile ——
     * 少了它，钩子线程可能一直看到旧值，"再按一下中键关掉"就会时灵时不灵。 */
    private volatile GestureMenuWindow? _middleMenu;

    // ───────────────────────── 挂点 ─────────────────────────

    /// <summary>
    /// 菜单正开着时又来了中键：按下就关掉，按下与抬起都吞。
    ///
    /// 为什么抬起也要吞：按下被吞之后如果抬起漏给系统，前台程序会收到一个没有配对的
    /// MouseUp —— 浏览器会莫名其妙开始自动滚动（GestureService.cs 顶部那段事故注释）。
    /// </summary>
    internal bool MiddleMenuSwallowWhileOpen(int vk, bool down, bool injected)
    {
        if (injected || !IsMenuButton(vk)) return false;
        /* 先判菜单在不在：以前这行诊断在判空之前，等于**每一下中键/侧键**都写一次盘，
         * 而它跑在钩子回调里（见 AttachDiag 的注释）—— 热路径上白付一次文件系统开销。 */
        if (_middleMenu == null) return false;
        AttachDiag($"menu-open-swallow: down={down}");

        if (down)
        {
            MiddleMenuClose();
            Interlocked.Exchange(ref _swallowUpVk, vk);
        }
        return true;
    }

    /// <summary>
    /// 指针此刻在不在「附加菜单可以弹」的地方 —— 只决定**菜单**能不能弹。
    ///
    /// 从 v1.5.0 起有四处：系统桌面（空白处）、资源管理器文件夹窗口（文件/文件夹上或空白处）、
    /// 打开/保存文件对话框、选文件夹对话框。范围外的地方（浏览器正文、编辑器、终端…）
    /// 菜单不弹，那里中键各有原生用途。
    ///
    /// ⚠ v1.5 起它**不再**是"中键归不归我们"的依据：那颗键如果在这里配了手势，
    /// 按下照样由手势接管（判据是 <see cref="GestureService.Candidates"/> 非空）。
    /// 以前两者被当成一回事，结果是"配了中键手势的软件里中键手势全没了"。
    ///
    /// 判据只看窗口类名/shell 句柄，全程几次窗口查询；**不问**图标列表、**不碰** UIA。
    /// 桌面要不要算"空白"由 <see cref="DesktopBlankAt"/> 在按下那一刻再问一次。
    /// </summary>
    private bool MiddleMenuInScope()
    {
        try
        {
            var (px, py) = CursorPos();
            var pt = new NativeMethods.POINT((int)Math.Round(px), (int)Math.Round(py));
            var scope = MiddleMenuScope.ToScope(Settings().MiddleClickMenuScope);
            var probe = MiddleMenuSurfaces.AtPoint(pt, scope);
            AttachDiag($"scope: surface={probe.Surface} why={probe.Why}");
            return probe.IsUsable;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "附加菜单：判定作用范围");
            return false;
        }
    }

    /// <summary>
    /// 桌面上这一点是不是空白（图标之间的空地）。只有桌面需要这一步 ——
    /// 资源管理器与文件对话框里"点在文件上"照样该弹菜单，判空白的跨进程 UIA 纯属白花时间。
    /// </summary>
    private static bool DesktopBlankAt(double px, double py)
    {
        try
        {
            var probe = BlankHit.AtPoint(
                new NativeMethods.POINT((int)Math.Round(px), (int)Math.Round(py)),
                MiddleClickMenuScope.Desktop);
            return probe.IsBlank;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "附加菜单：桌面空白判定");
            return false;
        }
    }

    /// <summary>
    /// 路径 A：用户没给中键配手势（或者手势总开关关着）时，按下这一下要不要接管。
    /// 返回 true = 已吞掉按下（抬起由 <see cref="_swallowUpVk"/> 配对吞）。
    /// </summary>
    private bool MiddleMenuTryPlainDown(int vk, AppSettings settings, bool hasMiddleGesture)
    {
        if (!IsMenuButton(vk)) return false;
        if (!MiddleMenuArm(vk, settings, hasMiddleGesture)) return false;
        Interlocked.Exchange(ref _swallowUpVk, vk);
        return true;
    }

    /// <summary>
    /// 按下时算一次"这一下算不算中键点击"，并把命中结论记进 <see cref="_middleTap"/>。
    /// 两条路径共用：路径 A 靠它决定吞不吞，路径 B 靠它决定松手时弹不弹。
    /// </summary>
    private bool MiddleMenuArm(int vk, AppSettings settings, bool hasMiddleGesture)
    {
        AttachDiag($"arm: vk=0x{vk:X2} hasGesture={hasMiddleGesture} enabled={settings?.MiddleClickMenuEnabled} buttons=[{string.Join(",", settings?.MiddleMenuMouseButtons ?? new List<string>())}]");
        if (!IsMenuButton(vk)) return false;

        /* 先无条件清掉上一笔留下的记录：手势画成功后 Finish 不会再读它，
         * 留着的话下一笔"没成笔"会读到上一次的命中结论（在文件上弹出菜单）。
         * 清在开关判断之前 —— 关掉开关的那一次也要把旧记录带走。 */
        lock (_gate) _middleTap = default;

        if (settings == null || !settings.MiddleClickMenuEnabled) return false;

        var (px, py) = CursorPos();
        var pt = new NativeMethods.POINT((int)Math.Round(px), (int)Math.Round(py));
        var scope = MiddleMenuScope.ToScope(settings.MiddleClickMenuScope);
        var probe = MiddleMenuSurfaces.AtPoint(pt, scope);

        /* 这一下该不该弹：桌面只在空白处弹（压在图标上仍然交给 shell）；
         * 资源管理器与文件对话框无条件弹 —— 那是用户明确要的两处新入口。 */
        bool takeable = probe.Surface switch
        {
            MiddleSurface.Desktop => DesktopBlankAt(px, py),
            MiddleSurface.ExplorerItems => true,
            MiddleSurface.FileDialog => true,
            _ => false,
        };

        var action = MiddleMenuPolicy.Decide(new MiddleMenuInput(
            MiddleVk: true,
            Down: true,
            Injected: false,
            MenuOpen: _middleMenu != null,
            Enabled: true,
            InScope: probe.IsUsable,
            /* 任务栏在 OnButton 里已经先放行了（IsShellTaskbarAtCursor），这里传 false；
             * 全屏要自己再判一次 —— 手势那个开关（DisableInFullscreen）出厂是关的，
             * 而"全屏不弹菜单"是这一条功能的硬要求，不能跟着它走。 */
            Taskbar: false,
            Fullscreen: FullscreenBlocksMiddleMenu(),
            Takeable: takeable,
            HasMiddleGesture: hasMiddleGesture));

        AttachDiag($"arm-decide: surface={probe.Surface} takeable={takeable} why={probe.Why} fgFull={FullscreenBlocksMiddleMenu()} action={action}");
        bool take = action is MiddleMenuAction.StartGesture or MiddleMenuAction.SwallowTap;
        if (!take) return false;

        lock (_gate)
        {
            _middleTap = new MiddleTap
            {
                Armed = true,
                Takeable = takeable,
                Surface = probe.Surface,
                Start = new GestureMath.Pt(px, py),
                Why = probe.Why,
            };
        }
        return true;
    }

    /// <summary>
    /// 路径 A 的收尾（在 OnButton 顶端"配对吞抬起"那个分支里调用）。
    /// 位移没到「最小识别距离」才算一次点击；真在划就什么都不做（按下与抬起都已配对吞掉）。
    /// </summary>
    private void MiddleMenuOnSwallowedUp(int vk)
    {
        if (!IsMenuButton(vk)) return;

        MiddleTap pending;
        lock (_gate)
        {
            pending = _middleTap;
            _middleTap = default;
        }
        if (!pending.Armed) return;

        var (px, py) = CursorPos();
        double dx = px - pending.Start.X;
        double dy = py - pending.Start.Y;
        double moved = Math.Sqrt(dx * dx + dy * dy);

        /* 用和手势同一个门槛（MinMove，可调 4~400）。
         * 达到门槛 = 用户其实在划 —— 这一整下按手势处理（按下与抬起都已配对吞掉）。 */
        int minMove = Math.Max(4, Settings().GestureOptions.MinMove);
        if (moved >= minMove)
        {
            AttachDiag("  -> 划了一笔：交给手势");
            return;
        }

        if (!pending.Takeable)
        {
            /* 没落在"该弹菜单"的地方（桌面压在图标上 / 别的窗口 / 判定不出空白）。
             *
             * 这一下本来就不该被我们吃掉，但**按下时已经吞了**（那一刻必须按"可能是手势"处理），
             * 只吞不还就是用户说的"中键功能好像失效"：既没弹菜单，原生中键也没了。
             * 现在确认它只是点了一下 → 补发一次原生中键，浏览器的自动滚动 /
             * 资源管理器与桌面原有的中键行为照旧。
             *
             * 走 QueueReplayTap 这个唯一入口（静默期 + 线程池 + 补发自检都在里面），
             * 补发用的是 InputSender（带 InjectionTag），我们自己的钩子认得出这是注入的，
             * 不会再套娃。 */
            AttachDiag("  -> 非空白/范围外的单击：补发原生中键");
            QueueReplayTap(vk);
            return;
        }

        MiddleMenuOpen(Settings());
    }

    /// <summary>
    /// 路径 B：手势没成笔（<c>extent &lt; MinMove</c>）时，用菜单代替补发一次原生中键。
    /// 返回 true = 已经弹了菜单，<c>Finish</c> 不要再补发 Tap。
    /// </summary>
    private bool MiddleMenuInterceptsTap(int vk, double px, double py, bool injectedStart, MiddleTap pending)
    {
        if (!IsMenuButton(vk)) return false;

        var settings = Settings();

        var action = MiddleMenuPolicy.Decide(new MiddleMenuInput(
            MiddleVk: true,
            Down: false,
            Injected: injectedStart,
            MenuOpen: _middleMenu != null,
            Enabled: settings.MiddleClickMenuEnabled,
            InScope: pending.Armed && pending.Surface != MiddleSurface.None,
            Taskbar: false,
            Fullscreen: FullscreenBlocksMiddleMenu(),
            Takeable: pending.Armed && pending.Takeable,
            HasMiddleGesture: true));

        if (action != MiddleMenuAction.MenuInsteadOfTap) return false;
        MiddleMenuOpen(settings);
        return true;
    }

    /// <summary>
    /// 起始超时（按下后半天不动的那一笔）也按"没成笔"处理。
    ///
    /// 没有这一句，慢按一次中键会变成最难解释的行为：菜单不弹，原生中键也被吞掉
    /// （OnPoll 的 startTimedOut 分支会 ResetActive 并吞掉抬起）。
    ///
    /// **返回 true = 菜单已接手这一下**，调用方就不要再补发原生中键
    /// （否则用户会同时得到"菜单 + 一次自动滚动"）；返回 false 时必须补发，
    /// 不然这一下按了等于没按 —— 范围外的中键（浏览器、WPS、Electron 程序）
    /// 走的正是 false 这条。
    /// </summary>
    private bool MiddleMenuOnSilentCancel(int vk, double px, double py)
    {
        if (!IsMenuButton(vk)) return false;

        MiddleTap pending;
        lock (_gate)
        {
            pending = _middleTap;
            _middleTap = default;
        }
        if (!pending.Armed || !pending.Takeable) return false;
        MiddleMenuOpen(Settings());
        return true;
    }

    /// <summary>
    /// 这一下中键是不是"按下时已经登记、而且落在能弹菜单的地方"。
    ///
    /// 只在"按下被吞、最后没成笔"的那几条收尾分支里用：是 → 松手时会弹菜单，
    /// 不能同时补发原生中键；否 → 必须补发，绝不能让这一下凭空消失。
    /// </summary>
    private bool MiddleMenuTapPending()
    {
        lock (_gate) return _middleTap.Armed && _middleTap.Takeable;
    }

    // ───────────────────────── 弹 / 关 ─────────────────────────

    private void MiddleMenuOpen(AppSettings settings, bool fromKeyboard = false)
    {
        /* 建窗必须在界面线程：WPF 对象绑定在创建它的 Dispatcher 上。
         * 钩子线程 new 出来的窗口之后每次 Show 都会抛线程亲和性异常，
         * 而那条异常会被 Ui() 吞掉 —— 表现就是"按了没反应"（覆盖层那边踩过一次，见 EnsureOverlay）。
         *
         * 拼菜单也一起搬进来：里面的「历史记录」要读建议缓存、图标要查 shell，
         * 这些都不该占着钩子线程做。 */
        Ui(() =>
        {
            /* 残留引用就地清掉：上一次窗口如果在关闭过程中抛过异常（见 GestureMenuWindow.SafeClose
             * 的注释），Closed 没跑到，_middleMenu 就永远是旧值 —— 之后每次中键都从这里早退，
             * 用户看到的就是「附加菜单此后再也不弹了」。判 IsVisible 必须在界面线程（WPF 对象不能跨线程碰）。 */
            if (_middleMenu is { IsVisible: true }) return;
            _middleMenu = null;
            /* 「收藏当前文件 / 打开文件所在位置」的锚点必须在 Show() **之前**记：
             * 菜单一上屏，前台就变成我们自己了，之后再问"前台是哪个程序"只会问出 InputPlus。
             * 这里抓一次窗口/进程/类名，连同可不可点一起交给菜单去置灰。 */
            /* 鼠标唤出按"指针底下那个窗口"取锚点：这一下中键被我们吞了，
             * 桌面/资源管理器并不会因此变成前台，GetForegroundWindow 还是上一个程序。 */
            IntPtr probeRoot = IntPtr.Zero;
            try
            {
                var (px, py) = CursorPos();
                var probe = MiddleMenuSurfaces.AtPoint(
                    new NativeMethods.POINT((int)Math.Round(px), (int)Math.Round(py)),
                    MiddleMenuScope.ToScope(settings.MiddleClickMenuScope));
                if (probe.IsUsable) probeRoot = probe.Root;
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单：取指针窗口"); }

            IntPtr anchor = ForegroundFiles.ChooseAnchor(fromKeyboard, probeRoot, NativeMethods.GetForegroundWindow());
            NativeMethods.GetWindowThreadProcessId(anchor, out uint anchorPid);
            string anchorProc = ForegroundWatcher.ProcessNameOf(anchor);
            string anchorClass = WindowService.ClassNameOf(anchor) ?? "";
            bool actionable = ForegroundFileProbe.CanActOnForegroundFile(anchorProc, anchorClass);
            FavoriteTrace.Write($"menu: anchor=0x{anchor.ToInt64():X} proc={anchorProc} class={anchorClass} " +
                                $"actionable={actionable} fromKeyboard={fromKeyboard}");

            ForegroundFile? cachedFile = ForegroundFiles.Cached;
            bool cachedHit = cachedFile != null && cachedFile.Pid == (int)anchorPid;
            var foregroundState = new ForegroundFileState(
                actionable,
                anchorProc,
                cachedHit ? cachedFile!.FileName : "",
                cachedHit && _host.IsPinnedFile(cachedFile!.Path));

            ForegroundFiles.BeginSession(anchor, (int)anchorPid);

            GestureMenuDef? def;
            try { def = MiddleMenuPolicy.Build(settings, MiddleMenuHistory(), MiddleMenuUrlTitles(settings), foregroundState); }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "中键菜单：拼菜单");
                return;
            }
            if (def == null || def.Items.Count == 0) return;

            /* 收藏星标的上下文必须在**建窗之前**抓：窗口一 Show，前台就变成我们自己了，
             * 之后再去问"用户在看哪个程序"只会问出 InputPlus.exe。 */
            var favorite = MiddleMenuFavoriteTarget(settings, fromKeyboard);

            try
            {
                var win = new GestureMenuWindow(def, ActionCatalog.TitleOf, favorite)
                {
                    /* 悬停展开固定文件夹时先取多少条：跟「搜索前建议」的条数设置走
                     * （用户明确要求"具体显示条目受搜索前建议的约束"）。 */
                    FolderPageSize = Core.Gestures.FolderPeek.PageSizeFor(
                        settings.Suggestions?.ShowCount ?? Core.Gestures.FolderPeek.DefaultPageSize),
                };
                win.Closed += (_, _) => OnMiddleMenuClosed(win);
                _middleMenu = win;
                win.Show();

                /* 拿到键盘焦点：Esc / 1..9 / ↑↓ 全靠它。
                 *
                 * 为什么 Show() 不够：这一下窗口不是用户点击唤起的，我们进程也不是前台进程
                 * （前台还是他刚才在用的那个程序）。Windows 的前台锁定会直接拒绝
                 * Show() 内部的 SetForegroundWindow —— 窗口照样 Topmost 显示，
                 * 但键盘焦点留在原程序里，于是"菜单出来了，Esc 关不掉、数字键没反应"，
                 * 连 Deactivated（点别处收起）都不会触发。
                 * 这里复用仓库里那套三级阶梯（热键唤出 Qbar 用的同一个函数），
                 * 默认只走前两级，不改任何全局系统设置。 */
                var handle = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                if (handle != IntPtr.Zero && !WindowService.ForceForeground(handle))
                    CoreLog.Warn("[中键菜单] 抢前台失败：Esc / 数字键可能按不到");

                /* 预热：不等用户点就先扫一次。点了才扫的话，第一次点击要等 800ms
                 * （WPS/浏览器句柄多的时候真会等满）。返回值到了就**原地**补到菜单上：
                 * 文件名显示在第二行，已经在收藏里的那颗星变实心，
                 * 而「打开文件所在位置」的灰/亮就靠这里的 hasFile 决定 ——
                 * 没扫到文件时它必须一直是灰的（v1.5 修：以前 Electron / 浏览器里
                 * 这一条永远亮着，点下去弹"选择要定位的文件"）。
                 * 所以**失败也要通知**：以前这里 `if (file == null) return;` 让失败彻底
                 * 没机会把条目刷成灰的。
                 *
                 * 桌面 / 资源管理器 / 任务栏不扫：菜单里那一条本来就建成了灰的，
                 * 扫也只会立刻返回 NotActionable。 */
                if (actionable)
                {
                    _ = ForegroundFiles.GetOrRefreshAsync().ContinueWith(t =>
                    {
                        ForegroundFile? file = t.IsCompletedSuccessfully ? t.Result.File : null;
                        bool already = file != null && _host.IsPinnedFile(file.Path);
                        Ui(() =>
                        {
                            try { win.MarkForegroundFile(file?.FileName ?? "", already, file != null); }
                            catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单：补当前文件信息"); }
                        });
                    }, TaskScheduler.Default);
                }

            }
            catch (Exception ex)
            {
                _middleMenu = null;
                CoreLog.Error("中键菜单弹出失败", ex);
            }
        });
    }

    /// <summary>
    /// 抓"这次要收藏什么"：中键唤出看指针底下，键盘唤出看前台窗口。
    /// 抓不到也返回 <see cref="FavoriteTarget.Empty"/> —— 星标照旧在，
    /// 点开会给一个空表单让用户手填，而不是"这个位置不能收藏"。
    /// </summary>
    private static FavoriteTarget MiddleMenuFavoriteTarget(AppSettings settings, bool fromKeyboard)
    {
        try
        {
            var (px, py) = CursorPos();
            var scope = MiddleMenuScope.ToScope(settings.MiddleClickMenuScope);
            return FavoriteTarget.Capture(
                new NativeMethods.POINT((int)Math.Round(px), (int)Math.Round(py)),
                scope,
                NativeMethods.GetForegroundWindow(),
                preferForeground: fromKeyboard);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "附加菜单：抓收藏上下文");
            return FavoriteTarget.Empty;
        }
    }

    // ───────────────────────── 键盘唤出 ─────────────────────────

    private System.Windows.Interop.HwndSource? _middleHotkeySource;

    /* 已经注册过的组合键。初值故意不是空串，让首次 ApplyMiddleHotkey 一定走一遍注册。 */
    private string _middleHotkeyApplied = "\u0001";

    /// <summary>
    /// 按设置注册 / 注销「键盘唤出」的全局热键。
    ///
    /// 为什么用 RegisterHotKey 而不是再装一颗键盘钩子：钩子已经由主引擎占着，
    /// 手势服务再装一颗会重复收键；热键不抢输入、也不要管理员权限。
    /// 组合键解析失败或被人占用时只记日志 —— 附加菜单还有鼠标那一路能用。
    /// </summary>
    internal void ApplyMiddleHotkey()
    {
        string combo = (Settings().MiddleMenuHotkey ?? "").Trim();
        if (string.Equals(combo, _middleHotkeyApplied, StringComparison.Ordinal)) return;

        if (_middleHotkeySource == null)
        {
            var p = new System.Windows.Interop.HwndSourceParameters("InputPlusAttachMenuHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            _middleHotkeySource = new System.Windows.Interop.HwndSource(p);
            _middleHotkeySource.AddHook(MiddleHotkeyWndProc);
        }

        UnregisterHotKey(_middleHotkeySource.Handle, MiddleHotkeyId);
        _middleHotkeyApplied = combo;
        if (combo.Length == 0) return;

        var parsed = HotkeyCombo.Parse(combo);
        if (parsed == null)
        {
            CoreLog.Warn("[附加菜单] 键盘唤出组合键解析失败：" + combo);
            return;
        }
        if (!RegisterHotKey(_middleHotkeySource.Handle, MiddleHotkeyId, parsed.Value.Mods, (uint)parsed.Value.Vk))
            CoreLog.Warn("[附加菜单] 键盘唤出注册失败（组合键被别的程序占了？）：" + combo);
    }

    /// <summary>退出时把热键与隐藏窗口一起卸掉，不留孤儿注册。</summary>
    internal void ReleaseMiddleHotkey()
    {
        try
        {
            if (_middleHotkeySource == null) return;
            UnregisterHotKey(_middleHotkeySource.Handle, MiddleHotkeyId);
            _middleHotkeySource.Dispose();
            _middleHotkeySource = null;
            _middleHotkeyApplied = "\u0001";
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "附加菜单：卸热键"); }
    }

    private IntPtr MiddleHotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || wParam.ToInt32() != MiddleHotkeyId) return IntPtr.Zero;
        handled = true;
        try { MiddleMenuOpenFromKeyboard(); }
        catch (Exception ex) { CoreLog.Swallow(ex, "附加菜单：键盘唤出"); }
        return IntPtr.Zero;
    }

    /// <summary>动作「附加菜单」的入口：和键盘唤出同一条路径（全屏/例外仍然拦）。</summary>
    internal void OpenAttachMenuFromAction() => MiddleMenuOpenFromKeyboard();

    /// <summary>
    /// 键盘唤出：**所有场景**都能弹（不受"只在桌面"限制），但全屏程序与例外程序除外。
    /// 位置仍然是鼠标处 —— 由 <c>PlaceAtCursor</c> 统一决定。
    /// </summary>
    private void MiddleMenuOpenFromKeyboard()
    {
        var s = Settings();
        if (!s.MiddleClickMenuEnabled) return;
        if (FullscreenBlocksMiddleMenu()) return;
        if (IsMiddleMenuException(ForegroundExe())) return;
        /* fromKeyboard: 收藏的上下文看**前台**（用户在看的是那个程序），不看指针底下 ——
         * 热键唤出时指针常常停在任务栏或桌面角落，按指针抓会收藏错东西。 */
        MiddleMenuOpen(s, fromKeyboard: true);
    }

    // ── 历史记录（= 搜索前建议）与网址标题 ─────────────────────────

    /// <summary>
    /// 二级菜单「历史记录」的内容：直接用 Qbar 空面板那一屏建议。
    ///
    /// 为什么不自己再采集一套：那一屏就是「用户最近真正用过的文件/文件夹」，
    /// 已经做过缓存与去抖（首屏 0.3ms）；再写一套只会两边不一致。
    /// 服务没就绪（启动早期 / 关掉了建议）时返回空，菜单里干脆不出现这一项。
    /// </summary>
    private IReadOnlyList<MiddleMenuSuggestion> MiddleMenuHistory()
    {
        var list = new List<MiddleMenuSuggestion>();
        try
        {
            var screen = _host.Suggestions?.FirstScreen(8);
            if (screen == null) return list;
            foreach (var rec in screen)
            {
                if (rec == null || string.IsNullOrWhiteSpace(rec.Path)) continue;
                list.Add(new MiddleMenuSuggestion(rec.Name, rec.Path, MiddleMenuPolicy.IsUrl(rec.Path)));
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单：取历史记录"); }
        return list;
    }

    /* 网址标题缓存：键是网址，值是抓回来的 <title>。
     * 只放内存：这是"锦上添花"的显示数据，丢了下次再抓就行，不值得写盘。 */
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> UrlTitleCache = new(StringComparer.OrdinalIgnoreCase);

    /* 正在抓的网址：同一个网址只发一次请求，菜单连开十次也不会打十次网络。 */
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> UrlTitleInFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 给固定项里的网址准备备注：有缓存就用缓存，没有就**顺手排一个后台抓取**，
    /// 这次先不显示，下次打开菜单时标题就在了。
    ///
    /// 为什么不在拼菜单时同步抓：那是在 UI 线程上，一次网络往返就能让"点一下中键"
    /// 变成"卡半秒才弹菜单"。用户要的是立刻出现。
    /// </summary>
    private IReadOnlyDictionary<string, string> MiddleMenuUrlTitles(AppSettings settings)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pinned in (settings.Qbar?.Pinned ?? new List<QEntry>()))
            {
                if (pinned == null || !pinned.ShowInMiddleMenu) continue;
                string url = (pinned.Value ?? "").Trim();
                if (!MiddleMenuPolicy.IsUrl(url)) continue;

                if (UrlTitleCache.TryGetValue(url, out string? cached) && !string.IsNullOrWhiteSpace(cached))
                {
                    titles[url] = cached;
                    continue;
                }

                if (!UrlTitleInFlight.TryAdd(url, 1)) continue;
                _ = Task.Run(() =>
                {
                    try
                    {
                        string? title = WebTitleFetcher.Fetch(url);
                        if (!string.IsNullOrWhiteSpace(title)) UrlTitleCache[url] = title.Trim();
                    }
                    catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单：抓网页标题"); }
                    finally { UrlTitleInFlight.TryRemove(url, out _); }
                });
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单：整理网址标题"); }
        return titles;
    }


    private void OnMiddleMenuClosed(GestureMenuWindow win)
    {
        _middleMenu = null;

        try
        {
            /* 星标：菜单已经收了，剩下的交给宿主（弹收藏对话框 → 写进 Qbar 固定项）。
             * 和其它动作一样走宿主那条出口，菜单窗自己不认识 Settings/SaveSettings。 */
            if (win.FavoriteRequested)
            {
                try { _host.BeginFavoriteCapture(win.Favorite ?? FavoriteTarget.Empty); }
                catch (Exception ex) { CoreLog.Swallow(ex, "附加菜单：收藏"); }
                return;
            }

            var picked = win.Selected;
            if (picked == null) return;   // Esc / 点别处 / 再按中键：什么都不执行

            /* 和「弹出菜单：xxx」那条动作走同一个出口：菜单里的条目就是一个普通的动作 id + 参数，
             * 没有第二套执行路径。 */
            try { _host.InvokeAction(picked.Action, picked.Arg); }
            catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单动作"); }
        }
        finally
        {
            /* 锚点在这里才作废。上面两条出口（星标 / 顶部那两条入口）都可能**立刻**再做一次识别，
             * 而识别的锚点就是"菜单弹出之前那个窗口" —— 清早了，问出来的只会是菜单自己。 */
            ForegroundFiles.EndSession();
        }
    }
    private void MiddleMenuClose()
    {
        var win = _middleMenu;
        if (win == null) return;
        Ui(() =>
        {
            try { win.Close(); }
            catch (Exception ex) { CoreLog.Swallow(ex, "关闭中键菜单"); }
        });
    }
    // ── 临时诊断（INPUTPLUS_ATTACH_DIAG=1 时才写）：排查"鼠标唤不出"断在哪一步 ──


    internal static void AttachDiag(string message)
    {
        /* ⚠ 这个函数**会在钩子回调里**被调用（每一下鼠标键都可能走到）。
         *
         * 以前这里是同步的 `new FileInfo(...)` + `File.AppendAllText(...)`：
         * 一次磁盘慢（云同步 / 杀软扫描 / 机械盘）就能把钩子回调拖过 300ms，
         * 而超过 300ms 系统会**直接摘掉钩子并丢掉这次输入** —— 表现是
         * "手势忽然全失效、鼠标键也没反应"，且日志里什么都看不出来。
         * 现在只入队，写盘交给一条后台线程；队列满了就丢（日志永远不能拖住输入）。 */
        if (!DiagEnabled) return;
        if (DiagQueue.Count > 512) return;
        DiagQueue.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message);
        if (Interlocked.Exchange(ref _diagPumpStarted, 1) == 0)
        {
            try
            {
                var t = new System.Threading.Thread(DiagPump)
                {
                    IsBackground = true,
                    Name = "InputPlus.GestureDiag",
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                };
                t.Start();
            }
            catch { Interlocked.Exchange(ref _diagPumpStarted, 0); }
        }
    }

    /// <summary>诊断行是否落盘：`INPUTPLUS_ATTACH_DIAG=0` 可关掉。</summary>
    private static readonly bool DiagEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("INPUTPLUS_ATTACH_DIAG"), "0", StringComparison.Ordinal);

    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> DiagQueue = new();
    private static int _diagPumpStarted;

    private static void DiagPump()
    {
        var sb = new System.Text.StringBuilder(4096);
        while (true)
        {
            try
            {
                Thread.Sleep(250);
                if (DiagQueue.IsEmpty) continue;

                sb.Clear();
                while (sb.Length < 8192 && DiagQueue.TryDequeue(out string? line))
                    sb.Append(line).Append(Environment.NewLine);
                if (sb.Length == 0) continue;

                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inputplus-attach-diag.log");
                try
                {
                    var info = new System.IO.FileInfo(path);
                    if (info.Exists && info.Length > 256 * 1024) System.IO.File.Delete(path);
                }
                catch { }
                System.IO.File.AppendAllText(path, sb.ToString());
            }
            catch
            {
                // 诊断线程绝不能死，也绝不能反过来影响主流程
                try { Thread.Sleep(1000); } catch { }
            }
        }
    }
}
