using System.Windows;
using InputPlus.Core.Input;
using Clipboard = System.Windows.Clipboard;

namespace InputPlus.Core.Text;

/// <summary>
/// 文本相关动作：选区读写、光标移动、选择、删除。
/// 全部通过标准按键（Ctrl+Insert / Shift+Insert / 方向键）实现，
/// 所以对编辑器、浏览器、Office、终端等绝大多数程序都通用。
/// </summary>
public static class TextActions
{
    /// <summary>
    /// 剪贴板读写必须串行。取词（先存原文再还原）、替换选区、以及「输入文本」的
    /// 长文本粘贴都在动系统剪贴板 —— 动作队列和宏播放是两个线程，撞上就会
    /// 互相把对方的内容覆盖掉。这个锁是**共用**的，别在别处另开一个。
    /// </summary>
    internal static readonly object ClipGate = new();

    /// <summary>一次“复制”最多等多久（毫秒）。</summary>
    private const int DefaultTimeoutMs = 420;   // 浏览器复制可能较慢，放宽到 420ms

    /// <summary>
    /// 取当前选中的文本；没有选区或取不到时返回空串。
    ///
    /// **三条通道依次尝试**，因为每条都有各自的失效场景：
    ///
    ///   ① **UI Automation**（首选）
    ///      跨进程 COM 调用，直接问程序"你现在选了什么"。
    ///      不碰剪贴板、不模拟按键 —— 因此不受输入队列的权限限制。
    ///      ⚠ 但 Chromium 会拒绝向**提权客户端**暴露无障碍树，
    ///      管理员模式下这一条会失效 —— 由 SelectionProxy 兜底。
    ///      覆盖：Chromium 系浏览器、Electron、Office、现代编辑器。
    ///
    ///   ② **WM_COPY 窗口消息**
    ///      同样不走输入队列；高完整性可向低完整性投递。用于 UIA 没实现 TextPattern 的程序。
    ///
    ///   ③ **SendInput 模拟 Ctrl+C**
    ///      老路子，只在同权限（或本程序未提权）时有效。
    ///      带扫描码、四次分开发（Chromium / Qt 系才认）。
    ///
    /// 每次尝试都用剪贴板序号严格确认"真的复制到了"，
    /// 且结束后恢复用户原有剪贴板。
    /// </summary>
    /// <summary>
    /// 取当前选中的文本；没有选区或取不到时返回空串。
    ///
    /// **管理员模式下多一层兜底**：本地 5 条通道全失败时，交给一个普通权限的
    /// 一次性代理再试一次（<see cref="SelectionProxy"/>）。
    ///
    /// 原因：Chromium 系会拒绝向"提权客户端"暴露无障碍树并过滤合成输入，
    /// 于是管理员模式下 UIA / SendInput 全部失效。代理以普通权限运行，
    /// 客户端完整性与浏览器相同，通道立刻恢复。普通权限模式下这层不会启用，
    /// 行为与之前完全一致。
    /// </summary>
    public static string GetSelectedText(int timeoutMs = DefaultTimeoutMs)
    {
        /* 分流：前台是**管理员程序**、而我们是普通权限 —— 本地三条通道注定失败
         * （UIA 拒绝低→高、WM_COPY 被 UIPI 拦、SendInput 也被拦），
         * 这时直接问提权助手，省掉一整套必然失败的尝试（实测能省 600ms 左右）。
         * 助手还没连上也没关系：TryGetSelectedText 会顺手把它拉起来，
         * 这一次拿不到，下一次就全自动了。 */
        if (!ElevationHelper.IsElevated && !InAnyHelper && ForegroundElevated())
        {
            string viaHigh = SelectionElevated.TryGetSelectedText();
            if (!string.IsNullOrWhiteSpace(viaHigh))
            {
                CoreLog.Info($"[取词] 经提权助手取到 长度={viaHigh.Length}");
                return viaHigh;
            }
        }

        string text = GetSelectedTextLocal(timeoutMs);
        if (!string.IsNullOrWhiteSpace(text)) return text;

        // 代理只为"管理员模式"而存在；代理自己不能再递归调用
        if (!ElevationHelper.IsElevated || InAnyHelper) return text;

        string viaProxy = SelectionProxy.TryGetSelectedText();
        if (!string.IsNullOrWhiteSpace(viaProxy))
        {
            CoreLog.Info($"[取词] 经普通权限代理取到 长度={viaProxy.Length}");
            return viaProxy;
        }

        return text;
    }

    /// <summary>
    /// 当前是不是跑在某个"取词辅助进程"里。
    ///
    /// 两个方向各有一个替身（<see cref="SelectionProxy"/>：提权主进程的普通权限代理；
    /// <see cref="SelectionElevated"/>：普通主进程的提权助手），它们自己都不能再往下递归，
    /// 否则一边提权一边降权会无限弹框。
    /// </summary>
    private static bool InAnyHelper => SelectionProxy.InHelper || SelectionElevated.InHelper;

    /// <summary>
    /// 前台窗口是不是提升进程。**用令牌判定**（<see cref="Core.App.ElevationState.ForegroundIsElevated"/>），
    /// 不用 <see cref="InputSender.IsForegroundHigherIntegrity"/>：后者靠一次
    /// SendMessageTimeout(WM_NULL) 探路，实测对某些被提升的窗口（例如 WinForms 程序）
    /// 会返回"没被拦"，于是这条分流永远不触发 —— 用户看到的就是"管理员程序里取词没反应"。
    /// 令牌判定是同一套注入路由早就信赖的证据，两个判断取或，谁先证明"对面更高"就听谁的。
    /// </summary>
    private static bool ForegroundElevated()
        => InputSender.IsForegroundHigherIntegrity() || Core.App.ElevationState.ForegroundIsElevated();

    /// <summary>本地取词：UIA → WM_COPY → SendInput → 线程级注入 → Ctrl+Insert。</summary>
    public static string GetSelectedTextLocal(int timeoutMs = DefaultTimeoutMs)
    {
        /* ===== 分步诊断 =====
         * 每一步都记日志，定位"到底哪一步失败"用。
         * 用户反馈过好几次"取不到选区"，靠推理已经不可靠了。 */
        long startedAt = Environment.TickCount64;
        string fgTitle = "?";
        string fgClass = "?";
        IntPtr fgHwnd = IntPtr.Zero;
        try
        {
            fgHwnd = Core.Windows.WindowService.Foreground;
            var info = Core.Windows.WindowService.Describe(fgHwnd);
            if (info != null) { fgTitle = info.Title; fgClass = info.ClassName; }
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        CoreLog.Info($"[取词] 前台 hwnd=0x{fgHwnd.ToInt64():X} class={fgClass} title={fgTitle}");

        // ① UIA：不碰剪贴板，最干净
        string viaUia = UiaSelectionReader.GetSelectedText();
        CoreLog.Info($"[取词] UIA 结果长度={viaUia?.Length ?? -1} 内容={Trim4(viaUia)}");
        if (!string.IsNullOrWhiteSpace(viaUia))
        {
            CoreLog.Info($"[取词] 汇总 class={fgClass} 命中=UIA 长度={viaUia.Length} " +
                         $"耗时={Environment.TickCount64 - startedAt}ms");
            return viaUia;
        }

        /* 发送合成 Ctrl+C 之前，先等用户**物理上**按着的修饰键松开。
         *
         * 为什么要等：用户按的是触发键（CapsLock / 层键），动作是在按键
         * 那一刻派发的。如果这时他还按着 Ctrl / Shift / Alt / Win
         * （比如边用 Shift+方向键选词边按热键），我们合成的
         * "Ctrl↓ C↓ C↑ Ctrl↑" 会和真实状态打架：
         *   · 目标程序看到的是 Ctrl+Shift+C（不是复制）；
         *   · 我们抬起的那个 Ctrl 会把**用户的** Ctrl 也抬掉。
         * 两种都表现为"取不到词"，而且间歇出现、极难复现。
         * 等它松开（最多 220ms）比猜要可靠。 */
        WaitPhysicalModifiersUp();

        lock (ClipGate)
        {
            /* 完整快照（含图片 / 文件列表），不再只记纯文本 ——
             * 只记文本时，原剪贴板是截图就会在还原阶段被 Clear() 掉。 */
            var backup = ClipboardBackup.Capture();

            /* 快照拿不到（剪贴板被别的程序长时间独占）时**不再直接放弃**：
             * 放弃等于"选中了文字，按快捷键毫无反应"，用户完全不知道为什么。
             * 现在照常走复制通道，只是收尾**不还原**（没快照就没法还原）。
             * 宁可让剪贴板里留下刚刚这次复制的文字，也不能让功能静默失效。 */
            bool canRestore = backup.IsUsable;
            if (!canRestore)
                CoreLog.Warn("[取词] 剪贴板快照失败，继续取词（结束时不会还原剪贴板）");

            /* 记住"我们自己复制出来的内容"。
             * 收尾还原剪贴板时要用它判断：如果剪贴板里已经不是这段文字，
             * 说明期间用户自己按了 Ctrl+C（或者别的程序写了剪贴板），
             * 这时**绝对不能**还原，否则会把用户刚复制的东西搞丢。 */
            string? copiedByUs = null;
            uint? ownedSequence = null;

            try
            {
                // ② WM_COPY：走窗口消息，绕过输入队列
                bool posted = WindowMessageCopier.PostCopyToFocus();
                CoreLog.Info($"[取词] WM_COPY 已投递={posted}");
                string text = TryCopyVia(() => WindowMessageCopier.PostCopyToFocus(), timeoutMs);
                CoreLog.Info($"[取词] WM_COPY 后剪贴板结果长度={text.Length}");
                if (!string.IsNullOrEmpty(text))
                {
                    copiedByUs = text;
                    ownedSequence = GetClipboardSequenceNumber();
                    return text;
                }

                // ③ SendInput：同权限时有效（0x43 = C）
                text = TryCopyVia(() => InputSender.CopyChord(0x43), timeoutMs);
                if (!string.IsNullOrEmpty(text))
                {
                    copiedByUs = text;
                    ownedSequence = GetClipboardSequenceNumber();
                    return text;
                }

                /* ④ 线程级注入：attach 到目标输入队列 + 同步键盘状态。
                 * 这是最接近真实键盘的一条路，用于 SendInput 被程序过滤的情况。 */
                string viaThread = TryCopyVia(() => ThreadKeyboardSender.SendCtrlChord(0x43), timeoutMs);
                CoreLog.Info($"[取词] 线程级注入结果长度={viaThread.Length}");
                if (!string.IsNullOrEmpty(viaThread))
                {
                    copiedByUs = viaThread;
                    ownedSequence = GetClipboardSequenceNumber();
                    return viaThread;
                }

                // ⑤ 再试 Ctrl+Insert（终端、部分老程序只认这个）
                string last = TryCopyVia(() => InputSender.CopyChord(VK.Insert), timeoutMs);
                CoreLog.Info($"[取词] 最终结果长度={last.Length} 内容={Trim4(last)}");
                if (!string.IsNullOrEmpty(last))
                {
                    copiedByUs = last;
                    ownedSequence = GetClipboardSequenceNumber();
                }
                return last;
            }
            catch (Exception ex)
            {
                CoreLog.Warn("读取选区失败：" + ex.Message);
                return string.Empty;
            }
            finally
            {
                /* 只有拿到过快照才还原；没快照时什么都不能写回剪贴板
                 * （写了反而会把用户原来的内容冲掉）。 */
                if (canRestore) backup.Restore(copiedByUs, ownedSequence);
                else CoreLog.Info("[取词] 快照不可用，跳过还原");

                /* 一行汇总：以后用户再报"取不到选区"，看这一行就知道
                 * 是哪个通道挂的、前台是什么程序、花了多久。 */
                CoreLog.Info($"[取词] 汇总 class={fgClass} UIA=0 复制通道={copiedByUs?.Length ?? 0} " +
                             $"耗时={Environment.TickCount64 - startedAt}ms");
            }
        }
    }

    /// <summary>
    /// 等用户**物理上**按着的 Ctrl / Shift / Alt / Win 松开（最多 budgetMs 毫秒）。
    ///
    /// 合成 Ctrl+C 前必须做这一步：触发键按下时用户可能还按着别的修饰键，
    /// 合成出来的组合就成了 Ctrl+Shift+C（目标程序不认），而且我们抬起的
    /// 那个 Ctrl 会把用户真实的 Ctrl 一起抬掉。
    ///
    /// **不等 CapsLock** —— 它就是层触发键，取词时通常还按着；
    /// 等它会把每次取词都拖上两百多毫秒，而它并不参与 Ctrl+C 的语义。
    /// </summary>
    internal static void WaitPhysicalModifiersUp(int budgetMs = 220)
    {
        long deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline)
        {
            bool busy = InputSender.IsPhysicallyDown(VK.Control)
                     || InputSender.IsPhysicallyDown(VK.Shift)
                     || InputSender.IsPhysicallyDown(VK.Menu)
                     || InputSender.IsPhysicallyDown(VK.LWin)
                     || InputSender.IsPhysicallyDown(VK.RWin);
            if (!busy) return;
            Thread.Sleep(10);
        }
        CoreLog.Info("[取词] 修饰键一直按着，按当前状态继续取词");
    }

    /// <summary>
    /// 发一次复制快捷键，等剪贴板序号变化，返回新内容（失败返回空串）。
    ///
    /// **不预先清空剪贴板** —— 清空会让前台程序收到"剪贴板被占用"的干扰，
    /// 浏览器尤其容易因此丢弃复制请求。改用"序号是否变化"来判断，
    /// 只要变了就说明这次复制确实发生了。
    /// </summary>
    private static string TryCopyVia(Action sendCopy, int timeoutMs)
    {
        uint before;
        try { before = GetClipboardSequenceNumber(); }
        catch { return string.Empty; }

        try { sendCopy(); } catch { return string.Empty; }

        /* 等剪贴板序号变化，最多等 timeoutMs。
         *
         * 以前是 `Thread.Sleep(35)` 然后查一次 —— 慢一点的目标程序
         * （浏览器在对大段选区跑 JS、Office、远程桌面）35ms 还没写完，
         * 这里就判"没复制到"，接着换下一条通道，最后报"没读到选中文字"。
         * 用户看到的是"偶尔失灵"，而且越长的选区越容易失灵。
         * 改成轮询：序号一变就立刻往下读，没变就一直等到预算用完。 */
        long deadline = Environment.TickCount64 + Math.Max(60, timeoutMs);
        uint afterSeq = before;
        while (true)
        {
            try { afterSeq = GetClipboardSequenceNumber(); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            if (afterSeq != before) break;
            if (Environment.TickCount64 >= deadline) break;
            Thread.Sleep(5);
        }

        bool changed = afterSeq != before;
        CoreLog.Info($"[取词] 剪贴板序号 before={before} after={afterSeq} 变化={changed}");
        if (!changed) return string.Empty;

        /* 序号变了 = 复制确实发生。但内容可能还在路上：
         * 再等一小段，并重试读取，避免"序号变了但读不到文本"。 */
        for (int i = 0; i < 12; i++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string s = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            Thread.Sleep(12);
        }
        return string.Empty;
    }

    private static string SafeContainsText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : "(无文本)"; }
        catch (Exception ex) { return "(异常:" + ex.Message + ")"; }
    }

    private static string Trim4(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "(空)";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 40 ? s : s[..40] + "…";
    }

    /// <summary>等待剪贴板序号发生变化（= 前台程序确实写入了新内容）。</summary>
    private static bool WaitForClipboardChange(uint before, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                if (GetClipboardSequenceNumber() != before)
                {
                    // 序号变了，再确认内容可读（有些程序会先置空再写入）
                    if (Clipboard.ContainsText()) return true;
                }
            }
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            Thread.Sleep(10);
        }
        return false;
    }


    /// <summary>用给定文本替换当前选区（没有选区则插入到光标处）。</summary>
    public static void ReplaceSelection(string text)
    {
        lock (ClipGate)
        {
            /* 整份备份：原来是什么（图片 / 文件 / 富文本）就还什么。
             * 旧实现只认纯文本，没有文本时 finally 里 Clipboard.Clear() ——
             * 用户刚截的图就这么没了。 */
            var backup = ClipboardBackup.Capture();
            if (!backup.IsUsable)
            {
                CoreLog.Warn("[替换选区] 剪贴板快照失败，放弃写入（避免覆盖用户原剪贴板）");
                return;
            }

            uint? expectedSequence = null;
            try
            {
                Clipboard.SetText(text);
                expectedSequence = GetClipboardSequenceNumber();
                Thread.Sleep(18);
                InputSender.Shift(VK.Insert);   // Shift+Insert == 粘贴
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                CoreLog.Warn("替换选区失败：" + ex.Message);
            }
            finally
            {
                Thread.Sleep(90);
                backup.Restore(expectedSequence: expectedSequence);
            }
        }
    }

    public static void Copy(int timeoutMs = DefaultTimeoutMs)
    {
        InputSender.Ctrl(VK.Insert);
        Thread.Sleep(10);
    }

    public static void Cut() => InputSender.Ctrl(0x58); // X

    public static void Paste() => InputSender.Shift(VK.Insert);

    /// <summary>选中当前行（Home + Shift+End）。</summary>
    public static void SelectLine()
    {
        InputSender.Tap(VK.Home);
        Thread.Sleep(6);
        InputSender.Shift(VK.End);
    }

    /// <summary>把选中的文本包起来，例如选中 foo 按 Caps+9 得到 (foo)。</summary>
    public static void WrapSelection(string open, string close)
    {
        string sel = GetSelectedText();
        if (string.IsNullOrEmpty(sel))
        {
            ReplaceSelection(open + close);
            // 把光标放回括号中间
            for (int i = 0; i < close.Length; i++) InputSender.Tap(VK.Left);
            return;
        }
        ReplaceSelection(open + sel + close);
    }

    public static void ChangeCase(CaseMode mode)
    {
        string sel = GetSelectedText();
        if (string.IsNullOrEmpty(sel)) return;
        string result = mode switch
        {
            CaseMode.Upper => sel.ToUpperInvariant(),
            CaseMode.Lower => sel.ToLowerInvariant(),
            CaseMode.Title => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(sel.ToLowerInvariant()),
            _ => sel,
        };
        if (result != sel) ReplaceSelection(result);
    }

    /// <summary>
    /// 选中“从行首到光标”的那一段，并返回其文本（对应 Caps+Tab 的算式识别）。
    /// </summary>
    public static void SelectLineToCaret(out string text)
    {
        lock (ClipGate)
        {
            var backup = ClipboardBackup.Capture();
            if (!backup.IsUsable)
            {
                text = string.Empty;
                CoreLog.Warn("[算式取词] 剪贴板快照失败，放弃本次操作");
                return;
            }

            uint? expectedSequence = null;
            try
            {
                Clipboard.Clear();
                InputSender.Shift(VK.Home);
                Thread.Sleep(12);
                uint before = GetClipboardSequenceNumber();
                InputSender.Ctrl(VK.Insert);
                if (!WaitForClipboardChange(before, 240))
                {
                    text = string.Empty;
                    InputSender.Tap(VK.Right);   // 取消选择，避免影响用户
                    return;
                }
                expectedSequence = GetClipboardSequenceNumber();
                text = Clipboard.GetText();
            }
            catch
            {
                text = string.Empty;
            }
            finally
            {
                backup.Restore(expectedSequence: expectedSequence);
            }
        }
    }

    /// <summary>
    /// 最后的保底通道：请用户**亲自**按一下 Ctrl+C，我们顺手接住。
    ///
    /// 为什么这条一定有效：程序合成的 Ctrl+C 会被 Chromium / Electron 过滤，
    /// 也可能被 UIPI 拦掉，但**用户真实按下的 Ctrl+C 谁也拦不住**。
    ///
    /// 实现上是"旁观"而不是"参与"：只盯剪贴板序号，一变就读内容。
    /// 期间不写剪贴板，所以不会打乱用户自己的剪贴板。
    /// </summary>
    /// <param name="timeoutMs">最多等多久</param>
    /// <returns>用户复制到的文字；超时返回空串。</returns>
    public static string WaitForManualCopy(int timeoutMs = 4000)
    {
        uint seq;
        try { seq = GetClipboardSequenceNumber(); }
        catch { return string.Empty; }

        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(40);

            uint now;
            try { now = GetClipboardSequenceNumber(); }
            catch { return string.Empty; }
            if (now == seq) continue;

            CoreLog.Info("[取词] 检测到剪贴板变化（视为用户手动复制）");

            /* 序号变了不等于数据已经写好：浏览器 / OLE 可能是先占位再填。
             * 给它最多 ~480ms，读到非空文本就走。 */
            for (int i = 0; i < 12; i++)
            {
                Thread.Sleep(40);
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            CoreLog.Info($"[取词] 手动复制成功 长度={text.Length} 内容={Trim4(text)}");
                            return text;
                        }
                    }
                }
                catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            }

            // 这次变化读不出文本（可能复制的是图片/文件），继续等下一次
            seq = now;
        }

        CoreLog.Info("[取词] 等待手动复制超时");
        return string.Empty;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public enum CaseMode { Upper, Lower, Title }
}
