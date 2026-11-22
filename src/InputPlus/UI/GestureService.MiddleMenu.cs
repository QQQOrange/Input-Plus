using System;
using InputPlus.Core;
using InputPlus.Core.Actions;
using InputPlus.Core.Gestures;
using InputPlus.Core.Interop;
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
        public GestureMath.Pt Start;
        public BlankProbe Probe;
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
        AttachDiag($"menu-open-swallow: down={down}");
        if (_middleMenu == null) return false;

        if (down)
        {
            MiddleMenuClose();
            Interlocked.Exchange(ref _swallowUpVk, vk);
        }
        return true;
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
        // 鼠标唤出只在系统桌面：资源管理器里中键/侧键有它自己的用途，不接管。
        var scope = MiddleClickMenuScope.Desktop;
        var probe = BlankHit.AtPoint(new NativeMethods.POINT((int)Math.Round(px), (int)Math.Round(py)), scope);

        /* 范围判断用的是"命中的是哪种壳"，不是设置里的字面值：
         * 范围写"仅桌面"时，资源管理器的列表必须当成不在范围内。 */
        bool inScope = probe.Kind == HitKind.Desktop
            || (probe.Kind == HitKind.ExplorerItems && MiddleMenuScope.IncludesExplorer(scope));

        var action = MiddleMenuPolicy.Decide(new MiddleMenuInput(
            MiddleVk: true,
            Down: true,
            Injected: false,
            MenuOpen: _middleMenu != null,
            Enabled: true,
            InScope: inScope,
            /* 任务栏在 OnButton 里已经先放行了（IsShellTaskbarAtCursor），这里传 false；
             * 全屏要自己再判一次 —— 手势那个开关（DisableInFullscreen）出厂是关的，
             * 而"全屏不弹菜单"是这一条功能的硬要求，不能跟着它走。 */
            Taskbar: false,
            Fullscreen: FullscreenBlocksMiddleMenu(),
            Blank: probe.IsBlank,
            HasMiddleGesture: hasMiddleGesture));

        AttachDiag($"arm-decide: kind={probe.Kind} blank={probe.IsBlank} scope={inScope} fgFull={FullscreenBlocksMiddleMenu()} action={action}");
        bool take = action is MiddleMenuAction.StartGesture or MiddleMenuAction.SwallowTap;
        if (!take) return false;

        lock (_gate)
        {
            _middleTap = new MiddleTap
            {
                Armed = true,
                Start = new GestureMath.Pt(px, py),
                Probe = probe,
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
        if (!pending.Armed || !pending.Probe.IsBlank) return;

        var (px, py) = CursorPos();
        double dx = px - pending.Start.X;
        double dy = py - pending.Start.Y;
        double moved = Math.Sqrt(dx * dx + dy * dy);

        /* 用和手势同一个门槛（MinMove，可调 4~400）。
         * 达到门槛 = 用户其实在划 —— 只是他没给中键配任何手势，那就什么都不发生。 */
        int minMove = Math.Max(4, Settings().GestureOptions.MinMove);
        if (moved >= minMove) return;

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
        // 鼠标唤出只在系统桌面：资源管理器里中键/侧键有它自己的用途，不接管。
        var scope = MiddleClickMenuScope.Desktop;
        bool inScope = pending.Armed && (pending.Probe.Kind == HitKind.Desktop
            || (pending.Probe.Kind == HitKind.ExplorerItems && MiddleMenuScope.IncludesExplorer(scope)));

        var action = MiddleMenuPolicy.Decide(new MiddleMenuInput(
            MiddleVk: true,
            Down: false,
            Injected: injectedStart,
            MenuOpen: _middleMenu != null,
            Enabled: settings.MiddleClickMenuEnabled,
            InScope: inScope,
            Taskbar: false,
            Fullscreen: FullscreenBlocksMiddleMenu(),
            Blank: pending.Armed && pending.Probe.IsBlank,
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
    /// </summary>
    private void MiddleMenuOnSilentCancel(int vk, double px, double py)
    {
        if (!IsMenuButton(vk)) return;

        MiddleTap pending;
        lock (_gate)
        {
            pending = _middleTap;
            _middleTap = default;
        }
        if (!pending.Armed || !pending.Probe.IsBlank) return;
        MiddleMenuOpen(Settings());
    }

    // ───────────────────────── 弹 / 关 ─────────────────────────

    private void MiddleMenuOpen(AppSettings settings)
    {
        if (_middleMenu != null) return;

        /* 建窗必须在界面线程：WPF 对象绑定在创建它的 Dispatcher 上。
         * 钩子线程 new 出来的窗口之后每次 Show 都会抛线程亲和性异常，
         * 而那条异常会被 Ui() 吞掉 —— 表现就是"按了没反应"（覆盖层那边踩过一次，见 EnsureOverlay）。
         *
         * 拼菜单也一起搬进来：里面的「历史记录」要读建议缓存、图标要查 shell，
         * 这些都不该占着钩子线程做。 */
        Ui(() =>
        {
            if (_middleMenu != null) return;

            GestureMenuDef? def;
            try { def = MiddleMenuPolicy.Build(settings, MiddleMenuHistory(), MiddleMenuUrlTitles(settings)); }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "中键菜单：拼菜单");
                return;
            }
            if (def == null || def.Items.Count == 0) return;

            try
            {
                var win = new GestureMenuWindow(def, ActionCatalog.TitleOf);
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

            }
            catch (Exception ex)
            {
                _middleMenu = null;
                CoreLog.Error("中键菜单弹出失败", ex);
            }
        });
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
        MiddleMenuOpen(s);
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
        var picked = win.Selected;
        if (picked == null) return;   // Esc / 点别处 / 再按中键：什么都不执行

        /* 和「弹出菜单：xxx」那条动作走同一个出口：菜单里的条目就是一个普通的动作 id + 参数，
         * 没有第二套执行路径。 */
        try { _host.InvokeAction(picked.Action, picked.Arg); }
        catch (Exception ex) { CoreLog.Swallow(ex, "中键菜单动作"); }
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
    private static readonly bool AttachDiagOn = Environment.GetEnvironmentVariable("INPUTPLUS_ATTACH_DIAG") == "1";

    internal static void AttachDiag(string message)
    {
        if (!AttachDiagOn) return;
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inputplus-attach-diag.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
        }
        catch { }
    }
}
