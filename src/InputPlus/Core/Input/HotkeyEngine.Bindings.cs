using InputPlus.Core.Macro;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Input;

/// <summary>
/// 片段观察与展开、层内按键处理、层绑定表、滚轮与释放。
/// </summary>
public sealed partial class HotkeyEngine
{
    /// <summary>等长候选时的等待窗口。</summary>
    public const int SnippetWaitMs = 120;

    private SnippetMatch? _snippetPending;
    private DateTime _snippetPendingAt;

    /// <summary>挂起那一刻的缓冲代际号：变了就说明用户又动了手，这次展开作废。</summary>
    private int _snippetPendingGen;

    /// <summary>挂起那一刻输入法是不是在组字（决定展开时要不要先取消组字）。</summary>
    private bool _snippetPendingComposing;

    /// <summary>挂起那一刻输入法组字串的长度（= 被吞掉、还没上屏的字母数）。</summary>
    private int _snippetPendingCompositionChars;

    /// <summary>
    /// 定时器每 tick 调一次：等待窗口过了、而且缓冲没再变（说明用户没继续打），
    /// 就把刚才挂起的那条片段展开。
    /// </summary>
    internal void FlushPendingSnippet()
    {
        var pending = _snippetPending;
        if (pending == null) return;
        if ((DateTime.Now - _snippetPendingAt).TotalMilliseconds < SnippetWaitMs) return;

        _snippetPending = null;

        /* 缓冲变了 = 用户在继续打（或换了键），交给后面新的匹配。
         *
         * 这里以前是"把缓冲读出来、跟挂起时的字符串比一遍"。改成比代际号：
         * 一个整数就够，不必为了比较再拷一次字符串（钩子线程和这里会抢同一份内容）。 */
        if (_snippetBuffer.Generation != _snippetPendingGen) return;
        ExpandSnippet(pending.Value, _snippetPendingComposing, _snippetPendingCompositionChars);
    }

    /// <summary>
    /// 补删"触发词残留"。
    ///
    /// 为什么需要：组字期间的 Backspace 会被输入法拿去删拼音，屏幕上那几个字一个都没少；
    /// 而我们的 deleteCount 是按"应该删几个"算的，不是按"实际删掉几个"。所以删完必须
    /// 用控件文本核对一次 —— 尾巴上还剩几个字就补删几个。
    ///
    /// 安全边界：
    ///   · 只在尾巴**以触发词的后缀结尾**时才删，且至少 2 个字（单字太容易误伤正文）；
    ///   · 每轮重新读一次，读不到就停；
    ///   · 最多 3 轮，宁可留一点残字也不无限退格。
    /// </summary>
    private void RemoveSnippetLeftover(Snippet hit)
    {
        string foldedTrigger = SnippetMatcher.Fold(hit.Trigger ?? "");
        if (foldedTrigger.Length < 2) return;

        for (int round = 0; round < 4; round++)
        {
            System.Threading.Thread.Sleep(70);
            try
            {
                if (!TextMirror.TryReadTail(SnippetMatcher.MaxBuffer, out string tail)) return;
                string foldedTail = SnippetMatcher.Fold(SnippetMatcher.Trim(tail));
                int keep = TrailingTriggerLength(foldedTail, foldedTrigger);
                /* 至少 3 个字才补删（触发词本身更短时按它的长度）：
                 * 2 个字的巧合太多 —— 前一截正文正好以 "ym" 结尾不该被当成残留。 */
                int minKeep = Math.Min(foldedTrigger.Length, 3);
                if (keep < minKeep) return;

                for (int i = 0; i < keep; i++)
                {
                    InputSender.Tap(0x08);
                    SnippetDeletesForTest++;
                    System.Threading.Thread.Sleep(6);
                }
                CoreLog.Info($"[片段] 触发词残留 {keep} 字，补删一轮（{hit.Trigger}）");
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "补删触发词残留");
                return;
            }
        }

        /* 四轮之后还在 = 这个程序的输入法不听劝。留一份**明文**现场
         * （%TEMP%\clp-snippet-last.txt）：日志是加密的，用户报"没删掉"时
         * 这一行能直接看出是哪个进程、尾巴上剩了什么。 */
        try
        {
            if (TextMirror.TryReadTail(SnippetMatcher.MaxBuffer, out string last)
                && TrailingTriggerLength(SnippetMatcher.Fold(SnippetMatcher.Trim(last)), foldedTrigger) >= 2)
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                              + " 触发词残留：" + hit.Trigger
                              + " | 前台=" + (ForegroundProcess ?? "-")
                              + " | 尾巴=" + last
                              + Environment.NewLine;
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clp-snippet-last.txt"),
                    line, System.Text.Encoding.UTF8);
            }
        }
        catch { /* 诊断写不出去不影响输入 */ }
    }

    /// <summary>尾巴以触发词的多长后缀结尾（0 = 没残留）。折叠后的比较，与匹配同一套规则。</summary>
    private static int TrailingTriggerLength(string foldedTail, string foldedTrigger)
    {
        int max = Math.Min(foldedTail.Length, foldedTrigger.Length);
        for (int k = max; k >= 2; k--)
        {
            if (foldedTail.EndsWith(foldedTrigger[^k..], StringComparison.Ordinal)) return k;
        }
        return 0;
    }
    /// <summary>
    /// 让输入法把当前组字串**提交**掉，再用控件文本的差值算出它提交了几个字符。
    ///
    /// 为什么这是通用的路：组字内容在 TSF 程序里拿不到（ImmGetContext 返回的上下文
    /// 没有 GCS_COMPSTR），但"按一下空格 → 输入法把候选落到控件里"对所有 IME 都成立。
    /// 提交之后控件里多了几个字，就删几个字 —— 不猜触发词长度，也不碰 Esc。
    ///
    /// 返回 false = 读不到控件文本（自绘控件 / 权限更高的窗口），调用方退回 IMM32。
    /// </summary>
    private bool TryCommitImeComposition(out int committedChars)
    {
        committedChars = 0;
        try
        {
            if (!TextMirror.TryReadTail(SnippetMatcher.MaxBuffer, out string before, out int beforeLen)) return false;

            /* 读回来的是窗口标题（Electron / Chromium）：前后两次都等于标题、差值恒为 0，
             * 用它算"输入法吐了几个字"等于每次都得 0 —— 旧实现据此只回删 1 个字。 */
            if (TextMirror.LooksLikeWindowTitle(before)) return false;

            /* 注入一个空格：它带本程序的注入标记，自己的钩子会直接放行（不吞、不展开），
             * 对前台来说和用户按了一下空格完全一样 —— 输入法据此提交组字串。 */
            InputSender.Tap(0x20);
            System.Threading.Thread.Sleep(70);

            if (!TextMirror.TryReadTail(SnippetMatcher.MaxBuffer, out string after, out int afterLen)) return false;

            /* 用**整段长度**求差，不用尾巴长度：控件里超过 64 字时尾巴永远是 64 字，
             * 差值恒为 0（第一版踩过）。长度来自 WM_GETTEXTLENGTH，同一批消息里就有。 */
            int delta = afterLen - beforeLen;
            if (delta <= 0) return false;      // 没量到（或文本变短）：这次读数不可信

            /* 组字还在 = 输入法没吃这一下（或它把空格当成了分隔符）。
             * 此时"提交几个字"无从判断，交给调用方走 IMM32 / 放弃。 */
            if (ImeCompositionText().Length > 0) return false;

            _ = after;
            committedChars = delta;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 这个窗口能不能安全处理"输入法组字"：要么读得到控件文本（提交后算差值），
    /// 要么有 IMM 上下文（能直接取消）。两条都没有时，空格不该被吞掉 ——
    /// 吞了也展开不了，用户只会觉得"空格没反应"。
    /// </summary>
    private static bool CanHandleImeComposition()
        => TextMirror.TryReadTail(SnippetMatcher.MaxBuffer, out string tail) && tail.Length > 0
           && !TextMirror.LooksLikeWindowTitle(tail)
           || CanCancelImeComposition();

    /// <summary>
    /// 真正执行展开（删触发词 + 打内容）。
    ///
    /// <paramref name="composing"/>：这一瞬间输入法是不是正在组字。
    /// 中文（拼音）模式下触发词的字母被输入法吃进组字串，屏幕上只剩前缀那一个字
    /// （`；`）—— 此时按"触发词长度"回删会删掉正文、还会把拼音留在输入法里。
    /// <paramref name="compositionChars"/>：组字串里有几个字符（0 = 读不到）。
    /// </summary>
    private void ExpandSnippet(SnippetMatch match, bool composing = false, int compositionChars = 0)
    {
        var hit = match.Snippet;

        /* 复位**之后**立刻取代际号：这是"展开这一瞬间的屏幕状态"。
         * 排队执行时如果已经不是这个号，说明用户在这几毫秒里又打了字，
         * 那这一次展开就作废 —— 宁可少展开一次，也不能去删用户刚打的正文。
         *
         * 比的是**内容代际号**（ContentGeneration），不是总代际号：
         * 空格自己的抬起事件会把缓冲标成不可信，那只是记账、屏幕上没变一个字，
         * 不该把刚排好的这次展开冲掉。 */
        _snippetBuffer.Reset();
        int expected = _snippetBuffer.ContentGeneration;
        _snippetPending = null;

        /* 使用统计：只在内存里加，落盘交给调用方（AppHost 的防抖保存）。 */
        try
        {
            hit.UseCount++;
            hit.LastUsed = DateTime.Now;
            SnippetUsed?.Invoke();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "记录别名使用次数"); }

        /* 删几个字符由**命中的触发词**决定，不由缓冲长度决定：
         *   · 精确匹配时 ConsumedLength == Trigger.Length，删除数与改造前逐字一致；
         *   · 缓冲不可信、回读又失败时也走这个数（失败模式矩阵的要求）。 */
        int deleteCount = match.ConsumedLength;
        string text = hit.Text ?? "";

        /* 触发词已经打进前台了，所以要先删掉再输入。
         * 投递到动作队列：钩子里做 IO/发送会拖慢每一次按键。 */
        PostForTest(() =>
        {
            try
            {
                if (_snippetBuffer.ContentGeneration != expected) return;

                /* 计数紧跟"代际号没变"这一句：被作废的展开不算命中，
                 * 否则自检里"缓冲变了就不展开"这条用例观察不到区别。 */
                SnippetMatchesForTest++;

                if (SnippetDebugOn)
                {
                    CoreLog.Info($"[片段DBG] 开始展开 {hit.Trigger}：删除={deleteCount} 内容={text.Length} 字 " +
                                 $"可编辑={EditableForTest()} 前台={ForegroundProcess ?? "-"}");
                }

                /* 焦点不在可编辑控件上就放弃展开：
                 * 否则 Backspace 会去删别的东西、内容会打到别处。
                 * 判断放在队列里（STA），不能放钩子里。 */
                if (!EditableForTest()) return;

                /* 回删之前的控件文本快照：只有诊断用一个用途（写 %TEMP% 的明文现场），
                 * 读不到也不影响展开 —— 删多少仍然按触发词长度/组字判定来。 */
                bool mirrorReadable = TextMirror.TryReadTail(SnippetMatcher.MaxBuffer, out string mirrorBefore);

                /* 输入法正在组字：屏幕上只有前缀那一个字，触发词的字母还在输入法里。
                 * 具体走哪条路见下面 composing 分支（顺序在 2026-11-23 改过：
                 * 先 IMM 取消，再"提交 + 量差值"，最后按触发词长度兜底）。
                 * 为什么绝不发 Esc：微信 / QQ 用 Esc 关当前对话窗口（v1.5.8.2 事故）。 */
                if (composing)
                {
                    /* 分支顺序按"能不能算准"排，不按"哪条看着更聪明"排：
                     *   ① 组字串读得到 → 那几个字母**从没上过屏**（组字串就是还没落进控件的字），
                     *      取消组字之后屏幕上只剩前缀，回删前缀那 1 个字就干净。最准，也不发按键；
                     *   ② 读不到组字串、但控件文本读得到 → 让输入法提交，再用整段长度差算它吐了几个字；
                     *   ③ 都不可测 → 按触发词长度回删（v1.5.8.2 的老行为，至少不会只删 1 个字）。
                     *
                     * 为什么不再"先提交再量"：Chromium / Electron 里 WM_GETTEXT 拿到的是**窗口标题**，
                     * 前后差值恒为 0，旧实现据此只回删 1 个字 —— 屏幕上的 `;ipdv` 原样留着，
                     * 用户看到的就是"关键字没删、只删了空格"（2026-11-23 现场）。
                     *
                     * ⚠ 判"取消成功"必须同时看两件事：我们**亲手取消掉了一个非空组字串**，
                     * 而且现在组字串是空的。只看后者会把"输入法已经把字母提交进控件"
                     * 也当成"取消成功"，于是只删掉前缀、`ipdv` 留在屏幕上 ——
                     * 那句「一个别名我第一次输入是失败的，第二次才顺利触发」就是这么来的。 */
                    bool compositionGone = false;
                    bool cancelledComposition = false;
                    int committed = 0;

                    if (compositionChars > 0)
                    {
                        cancelledComposition = CancelImeComposition(out bool hadComposition) && hadComposition;
                        compositionGone = ImeCompositionText().Length == 0;
                        if (compositionGone) System.Threading.Thread.Sleep(45);
                    }

                    /* 组字串已经没了、我们也没取消掉什么 → 字母是输入法**提交**掉的
                     * （空格那一下最常见）。它已经在控件里了，这时再去"补一个空格催提交"
                     * 只会往正文里多打一个空格，所以只在组字串还在时才试提交。 */
                    if (!compositionGone && TryCommitImeComposition(out int n) && n > 0)
                    {
                        committed = n;
                        compositionGone = true;
                    }

                    deleteCount = DecideComposingDeletes(
                        cancelledComposition, compositionGone, committed,
                        compositionChars, match.ConsumedLength, PrefixChars(match));

                    CoreLog.Info($"[片段] 输入法组字（{hit.Trigger}）：取消={cancelledComposition} " +
                                 $"组字已消失={compositionGone} 提交={committed} 字，回删 {deleteCount}");
                }

                for (int i = 0; i < deleteCount; i++)
                {
                    InputSender.Tap(0x08);          // Backspace
                    SnippetDeletesForTest++;
                    /* 20ms 而不是 6ms：目标程序（尤其 Chromium 系）在极短间隔里
                     * 会把连续合成按键合并/丢弃，用户看到的就是"一个字符都没删"。 */
                    System.Threading.Thread.Sleep(20);
                }

                /* 回删之后**再核对一遍**：中文输入法在组字期间会把 Backspace 吃掉
                 * （那是"删拼音"），于是触发词原样留在屏幕上 —— 用户看到的是
                 * `;asym` 还在、正文接在后面。这里读一次控件文本，只要尾巴上还残留
                 * 触发词的后缀就补删，最多 3 轮。 */
                RemoveSnippetLeftover(hit);

                /* 留一份**明文**现场（最长 256KB，超过就重写）：
                 * 日志本身是加密的，用户报"没删掉"时这一行能直接看出
                 * 删了几个、组字判定是什么、控件文本读不读得到。 */
                try
                {
                    TextMirror.TryReadTail(SnippetMatcher.MaxBuffer, out string afterTail);
                    string line = DateTime.Now.ToString("HH:mm:ss")
                                  + " 展开=" + hit.Trigger
                                  + " 删除=" + deleteCount
                                  + " 组字=" + composing
                                  + " 镜像=" + (mirrorReadable ? "可读" : "不可读")
                                  + " 前=[" + mirrorBefore + "]"
                                  + " 后=[" + afterTail + "]"
                                  + " 前台=" + (ForegroundProcess ?? "-")
                                  + Environment.NewLine;
                    string tracePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clp-snippet-last.txt");
                    try
                    {
                        if (System.IO.File.Exists(tracePath)
                            && new System.IO.FileInfo(tracePath).Length > 256 * 1024)
                            System.IO.File.Delete(tracePath);
                    }
                    catch { }
                    System.IO.File.AppendAllText(tracePath, line, System.Text.Encoding.UTF8);
                }
                catch { /* 诊断写不出去不影响输入 */ }
                /* 组字路径下**必须**走 Unicode 注入：短 ASCII 正文默认是"真实按键逐字敲"，
                 * 而那些按键会先被输入法拿走变成拼音 —— 屏幕上什么都不出现，
                 * 用户看到的就是"窗口不关了，但别名也没展开"（v1.5.8.2 的第二种表现）。
                 * Unicode 注入不经过键盘布局与输入法，中文/英文都能直接落字。 */
                InputSender.Text(text, composing ? InputSender.TextMode.Unicode : InputSender.TextMode.Auto);

                /* 展开后**不主动改输入法状态** —— 连"替用户按一下 Shift"也不做。
                 *
                 * 这里原来有两层：① 把"前缀阶段偷偷切走的输入语言"切回来；
                 * ② 用户显式勾选「展开后自动按一下 Shift」时补一下 Shift。
                 * ① 已经整条删掉（见 HotkeyEngine.Triggers.cs 里的说明）；② 也删掉了 ——
                 * 用户的原话是「input plus 不应该能够自己换输入法」，而按 Shift 就是换
                 * 中/英模式，勾选框只是把"软件替你换"写在了明面上，结果一样：
                 * 打完别名，用户的中文输入法自己变成了英文。
                 *
                 * 设置项 RestoreImeAfterExpand 保留字段只为读旧配置不报错，不再有任何执行路径。 */

                CoreLog.Info($"片段展开：{hit.Trigger} → {text.Length} 字");

            }
            catch (Exception ex) { CoreLog.Warn("片段展开失败：" + ex.Message); }
        });
    }

    /// <summary>
    /// 触发词里"前缀"占几个字：`;ipdv` → 1，多符号前缀 `;;x` → 2。
    ///
    /// 组字路径下要单独删前缀（字母已经被输入法取消掉了），所以这个数不能写死 1。
    /// </summary>
    private int PrefixChars(SnippetMatch match)
    {
        try
        {
            int consumed = Math.Max(1, match.ConsumedLength);
            var prefixes = _settings().Hotstring?.EffectivePrefixes;
            if (prefixes == null || prefixes.Count == 0) return 1;

            string key = SnippetPrefixOps.StripAny(match.Snippet.Trigger, prefixes);
            return Math.Clamp(consumed - SnippetMatcher.Fold(key).Length, 1, 4);
        }
        catch { return 1; }
    }

    /// <summary>
    /// 组字路径下到底该回删几个字符（纯函数，自检逐条钉住）。
    ///
    /// 这个数决定"删掉的是触发词、还是用户的正文"，所以只按**观察到的事实**算：
    ///   · <paramref name="cancelledComposition"/>：我们亲手取消掉了一个**非空**组字串
    ///     → 那几个字母从没上过屏，屏幕上只剩前缀那一个字；
    ///   · <paramref name="compositionGone"/>：现在组字串是空的；
    ///   · <paramref name="committedChars"/>：让输入法提交之后它吐了几个字。
    ///
    /// 最容易写错的是"组字串没了、但我们没取消"：那不是"字母被取消了"，而是
    /// **输入法已经把它提交进了控件**（屏幕上确实有 `ipdv`）。以前这里一律按
    /// "取消成功"处理、只删前缀，用户看到的就是「第一次输入失败」。
    /// </summary>
    internal static int DecideComposingDeletes(
        bool cancelledComposition, bool compositionGone, int committedChars,
        int compositionChars, int consumedLength, int prefixChars)
    {
        int prefix = Math.Max(1, prefixChars);
        int consumed = Math.Max(1, consumedLength);

        if (cancelledComposition && compositionGone) return prefix;      // 字母真的被取消掉了
        if (compositionGone && committedChars > 0) return prefix + committedChars;
        if (compositionGone) return consumed;                            // 提交掉了：字母已经在屏幕上
        if (compositionChars > 0) return prefix + Math.Min(compositionChars, consumed);
        return consumed;
    }

    /// <summary>动作队列投递：自检可以接管，用来在"已排队、未执行"这段窗口里插一脚。</summary>
    private void PostForTest(Action action)
    {
        var hook = QueueForTest;
        if (hook != null) { hook(action); return; }
        _queue.Post(action);
    }

    /// <summary>
    /// 虚拟键 → 字符（US 布局）。认不出返回 null。
    ///
    /// 不走 ToUnicodeEx：那个 API 会改变死键状态，可能干扰输入法。
    /// 这里只认最基本的 ASCII，认不出就让缓冲区断开 —— 安全优先。
    /// </summary>
    private static char? AsciiCharOf(int vk, bool shift)
    {
        if (vk >= 0x41 && vk <= 0x5A) return (char)((shift ? 'A' : 'a') + (vk - 0x41));
        if (vk >= 0x30 && vk <= 0x39)
        {
            if (!shift) return (char)vk;
            const string shiftedDigits = ")!@#$%^&*(";
            return shiftedDigits[vk - 0x30];
        }

        return vk switch
        {
            0x20 => ' ',
            0xBA => shift ? ':' : ';',
            0xBB => shift ? '+' : '=',
            0xBC => shift ? '<' : ',',
            0xBD => shift ? '_' : '-',
            0xBE => shift ? '>' : '.',
            0xBF => shift ? '?' : '/',
            0xC0 => shift ? '~' : '`',
            0xDB => shift ? '{' : '[',
            0xDC => shift ? '|' : '\\',
            0xDD => shift ? '}' : ']',
            0xDE => shift ? '"' : '\'',
            _ => null,
        };
    }

    private bool OnKeyDownInLayer(int vk, bool wasPhysicallyDown, string? device = null)
    {
        /* 「欠着的抬起」只在**本次按下的第一次**记账。
         *
         * 系统自动重复的 down 不能重复记账：用户可能先按住 Down 不放，
         * 再按住触发键进层 —— 这时 Down 的原始 down 早就放给前台了，
         * 若在层内把它的每个重复 down 都记成"按下被吞过"，
         * 松手时那次 up 就会被补吞，前台只收到 down、收不到 up
         * （游戏 / 远程桌面会一直认为这个键按着）。
         * wasPhysicallyDown 正是"这不可能是第一次按下"的判据。 */
        void OweUp(int code)
        {
            if (!wasPhysicallyDown) _swallowedUp.TryAdd(code, 0);
        }

        /* 修饰键单键绑定：caps_lalt = 只看文件夹 这类。
         *
         * 修饰键在本函数开头只"记状态 + 吞掉"就返回，压根走不到下面的 combo 查找，
         * 于是 combo 里只有修饰键的绑定永远命中不了 —— 用户按 Caps+LAlt，
         * 面板什么都不会弹（他就说成"这个功能没实现"）。
         *
         * 这里在记录状态之后补一次查找：用"其余修饰键 + 本键名"拼 combo。
         * 一个修饰键可能以具体码（0xA4 LAlt）或通用码（0x12 Alt）到来，
         * 两种名字都试一遍，谁绑了算谁。 */
        void TryModifierBinding(KeyModifiers exclude, params string[] names)
        {
            KeyModifiers others = KeyModifiers.None;
            if (_ctrlDown) others |= KeyModifiers.Ctrl;
            if (_shiftDown) others |= KeyModifiers.Shift;
            if (_laltDown) others |= KeyModifiers.Alt;
            if (_lwinDown || _rwinDown) others |= KeyModifiers.Win;
            others &= ~exclude;      // 它自己不算"别的修饰键"

            var settings = _settings();
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                string id = KeyName.BuildComboEx(others, name);
                KeyBinding? hit = FindInLayer(settings, _activeLayer, id, device);
                if (hit == null || !hit.IsBound) continue;

                // 具体码与通用码打的是同一条绑定：只执行一次
                if (string.Equals(id, _lastModifierCombo, StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.Now - _lastModifierAt).TotalMilliseconds < 250) return;
                _lastModifierCombo = id;
                _lastModifierAt = DateTime.Now;

                ExecuteBinding(hit, vk, oweUp: !wasPhysicallyDown);
                return;
            }
        }

        /* 修饰键：记录状态并吞掉（否则会触发系统级 Alt / Win 菜单）。
         *
         * ⚠ 左右与通用码都要认。早先只认 LControl/LShift/LMenu：
         *   · 按**右** Ctrl 组合会去查无修饰的 caps_k，绑了就错误执行；
         *   · 部分键盘/驱动只发 0x11/0x10/0x12 这三个通用码，
         *     完全没有分支处理，组合键永远拼不出来。 */
        if (vk is VK.LMenu or VK.RMenu or 0x12)
        {
            _laltDown = true;
            OweUp(vk);
            TryModifierBinding(KeyModifiers.Alt, KeyName.NameOf(vk) ?? "", "alt", "lalt", "ralt");
            return true;
        }
        if (vk is VK.LWin or VK.RWin)
        {
            if (vk == VK.LWin) _lwinDown = true; else _rwinDown = true;
            OweUp(vk);
            TryModifierBinding(KeyModifiers.Win, KeyName.NameOf(vk) ?? "");
            return true;
        }
        if (vk is VK.LControl or VK.RControl or 0x11)
        {
            _ctrlDown = true;
            OweUp(vk);
            TryModifierBinding(KeyModifiers.Ctrl, KeyName.NameOf(vk) ?? "", "ctrl", "lctrl", "rctrl");
            return true;
        }
        if (vk is VK.LShift or VK.RShift or 0x10)
        {
            _shiftDown = true;
            OweUp(vk);
            TryModifierBinding(KeyModifiers.Shift, KeyName.NameOf(vk) ?? "", "shift", "lshift", "rshift");
            return true;
        }

        /* ---- 安全阀：Esc 永远退出层 ----
         *
         * 层里"没绑定的键也吞掉"，一旦状态卡住，用户会觉得整个键盘失灵，
         * 只能退出程序。给一个一定能按到的出口：Esc。
         * 吞掉它（不发给前台），避免 Esc 在游戏/对话框里触发别的行为。 */
        if (vk == VK.Escape)
        {
            CoreLog.Info("层里按了 Esc：退出层");
            ResetTriggerState("按 Esc 退出层");
            return true;
        }

        string? keyName = KeyName.NameOf(vk);

        /* 不认识的键（键名表里没有）→ 同样吞掉。
         *
         * 用户的要求是"按住触发键时，按的任何键都是层的按键；层里没绑定
         * 就等于空操作"。以前这里放行，结果是按了个没收录的键会漏到前台，
         * 和"层完全接管键盘"的预期不符。 */
        if (keyName == null)
        {
            _usedOtherKey = true;
            _swallowedDown.TryAdd(vk, 0);
            OweUp(vk);
            return true;
        }

        /* 用物理状态校正一遍修饰键标记再拼 combo。
         * 漏掉一次修饰键抬起事件就会让整层的组合键永久错位
         * （全部拼成 caps_ctrl_xxx），这里是唯一的纠偏机会。 */
        SyncModifierFlagsWithPhysical();

        var settings = _settings();

        /* ---- 子层键：可以是任意物理键（Tab / CapsLock / F13…） ----
         *
         * 早期的"层内修饰键"只认 Ctrl/Shift/Alt/Win 四个修饰键；
         * 现在子层键允许任意键，所以要先检查这个键是不是当前层配置的子层键。
         *
         * 命中就**吞掉并记住**，接下来的按键会带上这个前缀去查绑定
         * （例如 Tab 子层 + Q → caps_tab_q）。 */
        /* 已按住的子层键的**系统自动重复**：直接吞掉。
         *
         * 不能让它落进下面的 combo 查找 —— 那会把重复 down 拼成
         * caps_tab_tab 这种不存在的组合键；万一用户真的绑了这个 ID，
         * 按住子层键就会反复触发它。 */
        if (!string.IsNullOrEmpty(_subLayerKeyDown) &&
            string.Equals(keyName, _subLayerKeyDown, StringComparison.OrdinalIgnoreCase))
        {
            _swallowedDown.TryAdd(vk, 0);
            OweUp(vk);
            return true;
        }

        if (string.IsNullOrEmpty(_subLayerKeyDown) && IsSubLayerKey(settings, keyName))
        {
            _subLayerKeyDown = keyName;
            _swallowedDown.TryAdd(vk, 0);   // 记下 down：抬起时成对清理、重复时能被识别
            OweUp(vk);
            SubLayerChanged?.Invoke(keyName);
            return true;
        }

        var mods = KeyModifiers.None;
        if (_laltDown) mods |= KeyModifiers.Alt;
        if (_lwinDown || _rwinDown) mods |= KeyModifiers.Win;
        if (_ctrlDown) mods |= KeyModifiers.Ctrl;
        if (_shiftDown) mods |= KeyModifiers.Shift;

        /* 有非修饰键子层按住时，只查"子层前缀 + 键"的绑定。
         *
         * **不再回退**到普通查法 —— 这一条和触发层保持同一套语义：
         * 按住子层键 = 键盘交给这个子层，子层里没绑就是空操作。
         * 以前会在子层查不到时退回 caps_q，于是"子层里没绑的键"
         * 会去执行基础层的功能，用户看到的就是"子层根本没生效"。
         *
         * ⚠ 修饰键子层（ctrl / ctrl_shift）本来就在 combo 里带上修饰前缀，
         * 走的是下面的 comboId，不受这条影响。 */
        string comboId = KeyName.BuildComboEx(mods, keyName);
        KeyBinding? binding = string.IsNullOrEmpty(_subLayerKeyDown)
            ? FindInLayer(settings, _activeLayer, comboId, device)
            : FindInLayer(settings, _activeLayer, ComboForSubLayer(_subLayerKeyDown, mods, keyName), device);

        if (binding == null || !binding.IsBound)
        {
            /* 层内没有绑定的键 → **吞掉**，恢复成"按了什么都不发生"。
             *
             * v4.5.4 按用户要求改了语义：按住触发键 = 键盘完全交给这一层。
             *   · 绑了功能 → 执行功能；
             *   · 没绑    → 空操作（既不执行功能，也不恢复原生输入）。
             *
             * 以前这里是 return false（放行），于是"按住触发键按个没绑的键"
             * 会打出原字符 —— 用户的原话是"只会恢复成按键原有的功能"，
             * 他认为这是错的：层的语义应该是接管，而不是"没绑就漏过去"。
             *
             * up 事件也要一起吞：只吞 down 会让前台程序收到"没有按下过
             * 的抬起"，个别程序（游戏、远程桌面）会因此判定输入异常。 */
            _usedOtherKey = true;
            _swallowedDown.TryAdd(vk, 0);
            OweUp(vk);
            return true;
        }

        return ExecuteBinding(binding, vk, oweUp: !wasPhysicallyDown);
    }

    /// <summary>
    /// 执行一条绑定：动作节流、吞键记账、连续量动作登记，然后返回 true（吞掉这个键）。
    ///
    /// 抽出来是因为「原键替换层」要走**完全一样**的执行路径 —— 层里能做的
    /// （可重复动作、连续量、150ms 去重）替换层里也能做，用户不该感到两套语义。
    /// 唯一的前提是调用方已经决定"这个键归这条绑定管"。
    /// </summary>
    private bool ExecuteBinding(KeyBinding binding, int vk, bool oweUp)
    {
        var settings = _settings();

        string action = binding.Action;
        string arg = binding.Arg;

        bool canRepeat = settings.Global.RepeatWhenHeld && IsRepeatable(action);
        bool isRepeat = _swallowedDown.ContainsKey(vk);
        if (isRepeat)
        {
            /* 连续量动作由引擎自己的计时器驱动，系统的自动重复一律吞掉。
             * 两套节奏叠在一起会变成"按住后突然加速"，而且速率随
             * 用户的键盘设置变化，完全不可控。 */
            if (IsContinuous(action)) return true;
            if (!canRepeat) return true;
            if (vk == _lastRepeatVk && (DateTime.Now - _lastRepeatTime).TotalMilliseconds < 28) return true;
            _lastRepeatTime = DateTime.Now;
        }

        _lastRepeatVk = vk;
        _swallowedDown.TryAdd(vk, 0);
        /* 「欠着的抬起」只在**本次按下的第一次**记账（见 OnKeyDownInLayer 里的同名逻辑）：
         * 系统自动重复的 down 不能重复记账，否则松手时补吞一个前台从没收到过的 up。 */
        if (oweUp) _swallowedUp.TryAdd(vk, 0);
        _usedCombo = true;

        /* 同一个动作在 150ms 内重复请求 → 丢弃。
         *
         * 防御的是"同一时刻有两个钩子/两套引擎"这类事故：一个按键被两边各执行一次，
         * 用户看到的就是"按一下冒出来两个 QQ"。
         * 连续量动作（音量/亮度）和可重复动作（方向键）不参与去重，它们本来就是高频的。 */
        if (!IsContinuous(action) && !IsRepeatable(action) && IsDuplicateAction(action, arg))
        {
            CoreLog.Info($"动作去重：{action}（150ms 内重复请求，已忽略）");
            return true;
        }

        _queue.Post(() =>
        {
            try
            {
                _invoke(action, arg);
                ActionTriggered?.Invoke(action);
            }
            catch (Exception ex) { CoreLog.Error($"动作 {action} 执行失败", ex); }
        });

        /* 连续量动作：记下这一笔，之后由计时器按固定节奏一直执行，
         * 直到**这个键**松开。用户看到的就是"按住音量键，音量一条线地涨"。
         *
         * 每个键各存一份，而不是共用一个槽位：按住亮度+ 的时候再按音量+，
         * 两个键都应该继续各自生效（旧实现里后者会把前者顶掉，
         * 松开其中一个就把另一条也停了）。 */
        if (IsContinuous(action))
        {
            var state = new RepeatState
            {
                Action = action,
                /* 连续重复明确传 1：动作自己就知道"这一步要细调"。
                 *
                 * 以前靠"距上次调用的时间差"去猜是不是连发，结果调度一抖
                 * 就会猜错 —— 长按中途突然跳一大格，看起来一格一格地顿。 */
                Arg = "1",
                StartedAt = DateTime.Now,
                NextAt = DateTime.Now.AddMilliseconds(RepeatDelayMs),
            };
            lock (_repeatGate) _repeats[vk] = state;
        }

        return true;
    }

    /// <summary>层模式下松开某个键。返回 true 表示吞掉。</summary>
    private bool OnKeyUpInLayer(int vk, string? device = null)
    {
        bool swallow = _swallowedUp.TryRemove(vk, out _);
        _swallowedDown.TryRemove(vk, out _);

        // 松开就停：只停这个键，其它还按着的连续键照旧
        StopContinuousRepeat(vk);

        /* 非修饰键的子层键松开 → 清掉前缀状态。
         *
         * 不清的话，用户按了 Tab 子层再松开，之后按任何键都会被拼成
         * caps_tab_xxx，看起来就是"Tab 子层粘住了"。 */
        if (!string.IsNullOrEmpty(_subLayerKeyDown) &&
            string.Equals(KeyName.NameOf(vk), _subLayerKeyDown, StringComparison.OrdinalIgnoreCase))
        {
            _subLayerKeyDown = "";
            SubLayerChanged?.Invoke("");
        }

        /* ⚠ 左右修饰键和通用码都要清。
         *
         * 早先只写 LControl/LShift/LMenu —— 用户按**右** Ctrl 组合后松手，
         * _ctrlDown 还是 true，之后按任何键都会被拼成 Ctrl 组合键
         * （caps_ctrl_q 之类），看起来就是"组合键时灵时不灵"。
         * 很多人习惯用右手小指按右 Ctrl，这条路径并不罕见。 */
        if (vk is VK.LMenu or VK.RMenu or 0x12) _laltDown = false;
        if (vk is VK.LWin or VK.RWin) { _lwinDown = false; _rwinDown = false; }
        if (vk is VK.LControl or VK.RControl or 0x11) _ctrlDown = false;
        if (vk is VK.LShift or VK.RShift or 0x10) _shiftDown = false;

        return swallow;
    }



    /// <summary>
    /// 把"执行了什么动作"记进日志。
    ///
    /// 用户报"启动后莫名打出一个字母"时，日志里能直接看到是不是某个动作被触发；
    /// 干跑（自检/渲染）不记，免得刷屏。
    /// </summary>
    private static void LogAction(string action, string arg)
    {
        if (InputSender.DryRun) return;
        try
        {
            CoreLog.Info(arg.Length == 0 ? $"[动作] {action}" : $"[动作] {action} {arg}");
        }
        catch { }
    }

    private Dictionary<string, KeyBinding>? LayerBindingMap(AppSettings s, int layerId)
    {
        var layer = s.Layers.FirstOrDefault(l => l.Id == layerId);
        List<KeyBinding>? keys = layer?.Keys;
        if (keys == null || keys.Count == 0)
            keys = layerId == 1 ? s.Keys : null;      // 老设置没有层结构时，Keys 即第 1 层
        if (keys == null) return null;

        int version = s.Global?.BindingsVersion ?? 0;

        if (_layerMapCache.TryGetValue(layerId, out var cached)
            && ReferenceEquals(cached.Src, keys)
            && cached.Map.Count == keys.Count
            && cached.Version == version)
            return cached.Map;

        var map = new Dictionary<string, KeyBinding>(keys.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            if (string.IsNullOrEmpty(k.Combo)) continue;
            map[k.Combo] = k;      // 重复时保留最后一条，与 SettingsStore.Normalize 的行为一致
        }

        _layerMapCache[layerId] = (map, keys, version);
        return map;
    }

    private void OnWheel(int direction, bool shift, bool ctrl)
    {
        if (!_triggerHeld || !_layerActive || !Enabled || Suspended) return;

        // 滚轮事件不走 OnKeyEvent，修饰键状态同样要用物理状态校正
        SyncModifierFlagsWithPhysical();
        if (!_laltDown) return;
        var settings = _settings();
        string combo = direction > 0 ? "caps_lalt_wheelUp" : "caps_lalt_wheelDown";
        var binding = FindInLayer(settings, _activeLayer, combo);
        if (binding == null || !binding.IsBound) return;
        _usedCombo = true;
        _queue.Post(() => _invoke(binding.Action, binding.Arg));
    }

    /// <summary>
    /// 这个方向的滚轮现在是不是被触发键绑定占着（只读判断，不执行动作）。
    ///
    /// 平滑滚动在吞滚轮之前必须问一句：一旦它把真滚轮吞掉，注入回来的事件带着
    /// 注入标记，<see cref="OnWheel"/> 再也收不到 —— 用户会觉得"Caps+Alt+滚轮
    /// 在开了平滑滚动之后就失灵了"。判断条件与 <see cref="OnWheel"/> 一字不差。
    /// </summary>
    public bool HasWheelBinding(int direction)
    {
        if (!_triggerHeld || !_layerActive || !Enabled || Suspended) return false;

        SyncModifierFlagsWithPhysical();
        if (!_laltDown) return false;
        var settings = _settings();
        string combo = direction > 0 ? "caps_lalt_wheelUp" : "caps_lalt_wheelDown";
        var binding = FindInLayer(settings, _activeLayer, combo);
        return binding is { IsBound: true };
    }

    /// <summary>
    /// 该动作是否适合长按自动重复。
    /// </summary>
    public static bool IsRepeatable(string action) => action switch
    {
        "moveLeft" or "moveRight" or "moveUp" or "moveDown" => true,
        "moveWordLeft" or "moveWordRight" => true,
        "pageUp" or "pageDown" or "pageMoveLineUp" or "pageMoveLineDown" => true,
        "selectLeft" or "selectRight" or "selectUp" or "selectDown" => true,
        "selectWordLeft" or "selectWordRight" => true,
        "backspace" or "delete" or "deleteWord" or "forwardDeleteWord" => true,
        "mouseLeft" or "mouseRight" or "mouseUp" or "mouseDown" => true,
        "wheelUp" or "wheelDown" => true,
        _ => false,
    };

    public void Dispose()
    {
        /* 先松改键目标键，再卸钩子 —— 顺序反了就没机会补发了（钩子没了，
         * 但系统里那个目标键还按着）。 */
        try { ReleaseRemappedKeys(); } catch (Exception ex) { CoreLog.Swallow(ex, "退出时松开改键目标键"); }

        if (_holdTimer != null)
        {
            _holdTimer.Stop();
            _holdTimer = null;
        }
        _hook.Dispose();
        _physDown.Clear();
    }
}
