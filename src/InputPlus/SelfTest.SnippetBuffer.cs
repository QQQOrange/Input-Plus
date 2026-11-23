using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using InputPlus.Core;
using InputPlus.Core.Input;
using InputPlus.Core.Settings;

namespace InputPlus;

/// <summary>
/// 任务 B（别名流式尾部匹配）的自检：可回退缓冲 + 跨度匹配 + 引擎级退化行为。
///
/// 这一组盯的是"打错一个字母，这条别名就废了"：
///   · 缓冲遇到退格/认不出的键只会清空 → 改对了也不认；
///   · 展开时删几个字符是"猜"的 → 缓冲与屏幕一旦错位就会删掉正文。
///
/// 用例按施工单 §6.2 / §6.3 / §6.4 逐条落地。
/// </summary>
public static partial class SelfTest
{
    /// <summary>逐字符喂进缓冲。</summary>
    private static void Feed(SnippetBuffer buffer, string text)
    {
        foreach (char c in text) buffer.Apply(TextEventKind.Insert, c);
    }

    // ---------------- §6.2 缓冲区 ----------------

    private static void TestSnippetBuffer()
    {
        // ① 逐字符 Insert
        var b1 = new SnippetBuffer();
        Feed(b1, ";mail");
        CheckEqual("缓冲：逐字符插入后的内容", b1.Snapshot(), ";mail");
        CheckEqual("缓冲：逐字符插入后的长度", b1.Length, 5);
        Check("缓冲：初始可信", b1.Trusted);

        /* ② 打错一个字母 → 退格 → 改对。
         *
         * 施工单原文写的是 `;maiil` + 退格 + `l`，但那一串多出来的字母不在末尾，
         * 退格删不到它（退格只能删最后一个字符）。这里用同样含义、操作可行的序列：
         * 多打一个 i 之后退一格，再补上正确的字母。 */
        var b2 = new SnippetBuffer();
        Feed(b2, ";maii");
        b2.Apply(TextEventKind.DeleteBack);
        b2.Apply(TextEventKind.Insert, 'l');
        CheckEqual("缓冲：打错→退格→改对之后是正确触发词", b2.Snapshot(), ";mail");
        Check("缓冲：改对之后仍然可信", b2.Trusted);

        // ③ 退到空之后再退格：标不可信，内容保持空（不抛、不越界）
        var b3 = new SnippetBuffer();
        Feed(b3, ";");
        b3.Apply(TextEventKind.DeleteBack);
        CheckEqual("缓冲：退到空", b3.Length, 0);
        Check("缓冲：退到空仍然可信", b3.Trusted);
        b3.Apply(TextEventKind.DeleteBack);
        Check("缓冲：再退一格就标记不可信", !b3.Trusted);
        CheckEqual("缓冲：不可信时内容仍为空", b3.Length, 0);

        // ④ 中间的 DeleteForward：删右侧、插入符不动
        var b4 = new SnippetBuffer();
        Feed(b4, "abc");
        b4.Apply(TextEventKind.CaretHome);
        b4.Apply(TextEventKind.CaretRight);
        b4.Apply(TextEventKind.DeleteForward);
        CheckEqual("缓冲：DeleteForward 删掉插入符右边那个字", b4.Snapshot(), "ac");

        // ⑤ 插入符左移两次再插字：插在中间，代际号增加
        var b5 = new SnippetBuffer();
        Feed(b5, "abc");
        int genBefore = b5.Generation;
        b5.Apply(TextEventKind.CaretLeft);
        b5.Apply(TextEventKind.CaretLeft);
        CheckEqual("缓冲：只移动插入符不改代际号", b5.Generation, genBefore);
        b5.Apply(TextEventKind.Insert, 'x');
        CheckEqual("缓冲：插入符在中间时插在中间", b5.Snapshot(), "axbc");
        Check("缓冲：插入内容后代际号增加", b5.Generation > genBefore);

        // ⑥ Terminator：内容与代际号都不变
        var b6 = new SnippetBuffer();
        Feed(b6, ";mail");
        int gen6 = b6.Generation;
        b6.Apply(TextEventKind.Terminator);
        CheckEqual("缓冲：终止符不改内容", b6.Snapshot(), ";mail");
        CheckEqual("缓冲：终止符不改代际号", b6.Generation, gen6);

        // ⑦ 超长只留尾部（窗口 64）
        var b7 = new SnippetBuffer();
        Feed(b7, new string('a', 70));
        CheckEqual("缓冲：70 个字只留 64", b7.Length, 64);
        CheckEqual("缓冲：留下的是尾部", b7.Snapshot(), new string('a', 64));

        // ⑧ 不可信之后继续插：内容继续累加，可信标记保持 false
        var b8 = new SnippetBuffer();
        Feed(b8, ";ma");
        b8.Apply(TextEventKind.Untrusted);
        b8.Apply(TextEventKind.Insert, 'i');
        b8.Apply(TextEventKind.Insert, 'l');
        CheckEqual("缓冲：不可信之后内容继续累加", b8.Snapshot(), ";mail");
        Check("缓冲：不可信标记不会被插入清掉", !b8.Trusted);

        // ⑨ 用控件真实文本覆盖
        var b9 = new SnippetBuffer();
        Feed(b9, "坏掉的内容");
        b9.Apply(TextEventKind.Untrusted);
        b9.ReplaceWith(";mail", trusted: true);
        CheckEqual("缓冲：回读之后内容就是控件文本", b9.Snapshot(), ";mail");
        Check("缓冲：回读之后恢复可信", b9.Trusted);

        // ⑩ 并发：一个线程写、一个线程读快照
        var b10 = new SnippetBuffer();
        const string alphabet = ";mail";
        var stop = false;
        Exception? boom = null;
        var writer = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 4000; i++)
                {
                    b10.Apply(TextEventKind.Insert, alphabet[i % alphabet.Length]);
                    if (i % 7 == 0) b10.Apply(TextEventKind.DeleteBack);
                    if (i % 11 == 0) b10.Apply(TextEventKind.Terminator);
                }
            }
            catch (Exception ex) { boom = ex; }
            finally { stop = true; }
        }) { IsBackground = true };
        writer.Start();

        int reads = 0;
        while (!stop)
        {
            string snap = b10.Snapshot();
            reads++;
            if (snap.Length > SnippetMatcher.MaxBuffer) { boom = new Exception("快照超过窗口长度"); break; }
            foreach (char c in snap)
                if (alphabet.IndexOf(c) < 0) { boom = new Exception($"快照里出现了没写进去的字符：{c}"); break; }
        }
        writer.Join(2000);

        Check("缓冲：并发读写不抛异常", boom == null, boom?.Message);
        Check("缓冲：并发期间确实读到了内容", reads > 0, reads.ToString());
    }

    // ---------------- §6.3 跨度匹配 ----------------

    private static void TestSnippetSpan()
    {
        // ① 精确命中：删除数等于触发词长度
        var one = new List<Snippet> { new() { Trigger = ";mail", Text = "a@b.com" } };
        var m1 = SnippetMatcher.MatchSpan(one, ";mail", "");
        Check("跨度：;mail 命中", m1 != null);
        CheckEqual("跨度：删除数 == 触发词长度", m1?.ConsumedLength ?? -1, 5);
        Check("跨度：命中的就是那一条", ReferenceEquals(m1?.Snippet, one[0]));

        // ② 最长优先：;m 与 ;mail 并存时命中长的
        var two = new List<Snippet>
        {
            new() { Trigger = ";m", Text = "短" },
            new() { Trigger = ";mail", Text = "长" },
        };
        var m2 = SnippetMatcher.MatchSpan(two, ";mail", "");
        CheckEqual("跨度：最长优先命中 ;mail", m2?.Snippet.Trigger ?? "", ";mail");
        CheckEqual("跨度：最长优先的删除数", m2?.ConsumedLength ?? -1, 5);

        // ③ 词边界：前面紧挨着字母/数字就不算（"my;mail" 是词中间）
        Check("跨度：abc;mail 不命中（词边界）", SnippetMatcher.MatchSpan(one, "abc;mail", "") == null);

        // ④ 大小写敏感：;Mail 与 ;mail 各自精确命中
        var cases = new List<Snippet>
        {
            new() { Trigger = ";Mail", Text = "大写" },
            new() { Trigger = ";mail", Text = "小写" },
        };
        CheckEqual("跨度：;Mail 命中自己",
            SnippetMatcher.MatchSpan(cases, ";Mail", "")?.Snippet.Text ?? "", "大写");
        CheckEqual("跨度：;mail 命中自己",
            SnippetMatcher.MatchSpan(cases, ";mail", "")?.Snippet.Text ?? "", "小写");

        // ⑤ 折叠幂等（删除数依赖"折叠不改变长度"）
        foreach (string s in new[] { ";mail", "、mail", "：sig", "中文", "“引用”" })
        {
            string once = SnippetMatcher.Fold(s);
            string twice = SnippetMatcher.Fold(once);
            CheckEqual($"折叠幂等：{s}", twice, once);
            CheckEqual($"折叠不改变长度：{s}", once.Length, s.Length);
        }

        // ⑥ 空缓冲 / null 列表：返回 null，不抛
        Check("跨度：空缓冲返回 null", SnippetMatcher.MatchSpan(one, "", "") == null);
        Check("跨度：null 列表返回 null", SnippetMatcher.MatchSpan(null, ";mail", "") == null);

        // ⑦ 旧 Match 与 MatchSpan 一一对应（R1：匹配语义零变化）
        string[] buffers =
        {
            ";mail", ";m", "abc;mail", " ;mail", "hello ;mail", ";Mail", ";mai", "",
            "你好;mail", "；mail", "（;mail）",
        };
        int mismatches = 0;
        foreach (string buffer in buffers)
        {
            var legacy = SnippetMatcher.Match(cases, buffer, "");
            var span = SnippetMatcher.MatchSpan(cases, buffer, "");
            if (!ReferenceEquals(legacy, span?.Snippet)) mismatches++;
        }
        CheckEqual("跨度：旧 Match 与 MatchSpan 结果逐条一致", mismatches, 0);
    }

    // ---------------- §6.4 引擎级 ----------------

    private static void TestSnippetStreamEngine()
    {
        /* ⓪ 组字路径的回删数（纯函数）：这里错一个数，删掉的就是用户的正文。
         *
         * 现场记录（%TEMP%\clp-snippet-last.txt，2026-11-23）长这样：
         *   17:49:57 展开=;ipdv 删除=1 组字=True    ← 只删了前缀，ipdv 原样留着
         *   18:54:26 展开=;ipdv 删除=5 组字=False   ← 正常
         * 前一种来自老逻辑"组字串现在是空的 = 我们取消成功"。可它还有另一种解释：
         * **输入法已经把字母提交进控件了**（空格那一下最常见）—— 那时屏幕上
         * 确实有 `ipdv`，只删 1 个字就是用户说的「第一次输入失败」。 */
        CheckEqual("组字回删：亲手取消了组字 → 只删前缀",
            HotkeyEngine.DecideComposingDeletes(
                cancelledComposition: true, compositionGone: true, committedChars: 0,
                compositionChars: 4, consumedLength: 5, prefixChars: 1), 1);

        CheckEqual("组字回删：组字串自己消失（输入法已提交）→ 按触发词长度删",
            HotkeyEngine.DecideComposingDeletes(
                cancelledComposition: false, compositionGone: true, committedChars: 0,
                compositionChars: 4, consumedLength: 5, prefixChars: 1), 5);

        CheckEqual("组字回删：量到提交出来的字数 → 前缀 + 提交数",
            HotkeyEngine.DecideComposingDeletes(
                cancelledComposition: false, compositionGone: true, committedChars: 4,
                compositionChars: 4, consumedLength: 5, prefixChars: 1), 5);

        CheckEqual("组字回删：取消不掉、提交也量不到 → 前缀 + 组字串长度",
            HotkeyEngine.DecideComposingDeletes(
                cancelledComposition: false, compositionGone: false, committedChars: 0,
                compositionChars: 2, consumedLength: 5, prefixChars: 1), 3);

        CheckEqual("组字回删：连组字串都读不到 → 按触发词长度删",
            HotkeyEngine.DecideComposingDeletes(
                cancelledComposition: false, compositionGone: false, committedChars: 0,
                compositionChars: 0, consumedLength: 5, prefixChars: 1), 5);

        CheckEqual("组字回删：双符号前缀单独算",
            HotkeyEngine.DecideComposingDeletes(
                cancelledComposition: true, compositionGone: true, committedChars: 0,
                compositionChars: 2, consumedLength: 5, prefixChars: 2), 2);

        /* ① 没有别名时：一个按键都不该去读控件文本（R4 的"零开销"）。
         *    用引擎自己的计数器观察 —— 空配置下连探针都不该走。 */
        {
            var s = DefaultSettings.Create();
            foreach (var t in s.Triggers) t.Enabled = false;
            s.Global.Snippets.Clear();

            var q = new ActionQueue();
            var e = new HotkeyEngine(q, () => s, (_, _) => true);

            foreach (int vk in new[] { 0xBA, 'M', 'A', 'I', 'L', 0x20, 0x0D, 0x08 })
            {
                e.SimulateKey(vk, true);
                e.SimulateKey(vk, false);
            }
            DoEvents(60);

            CheckEqual("引擎：空配置不读控件文本", e.TextMirrorReadsForTest, 0);
            CheckEqual("引擎：空配置不展开", e.SnippetMatchesForTest, 0);
            e.Dispose();
            q.Dispose();
        }

        /* ② 展开排队期间缓冲变了 → 这次展开作废。
         *
         * 用 QueueForTest 把投递接管下来，"已排队、未执行"这段窗口才能被可靠地构造出来
         * （真实队列是另一个线程，赌它晚几十微秒才跑等于让用例随机失败）。 */
        {
            var s = DefaultSettings.Create();
            foreach (var t in s.Triggers) t.Enabled = false;
            s.Global.Snippets.Clear();
            s.Hotstring.Prefix = ";";
            s.Hotstring.ExpandOnSpace = false;
            s.Hotstring.RestoreImeAfterExpand = false;
            s.Global.Snippets.Add(new Snippet { Trigger = ";mail", Text = "a@b.com" });

            var q = new ActionQueue();
            var e = new HotkeyEngine(q, () => s, (_, _) => true);
            e.EditableForTest = () => true;

            var captured = new List<Action>();
            e.QueueForTest = a => { lock (captured) captured.Add(a); return true; };

            foreach (int vk in new[] { 0xBA, 'M', 'A', 'I', 'L' })
            {
                e.SimulateKey(vk, true);
                e.SimulateKey(vk, false);
            }

            CheckEqual("引擎：逐键模式下命中就当场上交展开动作", captured.Count, 1);

            /* 用户在这几毫秒里又打了一个字：屏幕上已经不是触发词了。 */
            e.SnippetBufferForTest.Apply(TextEventKind.Insert, 'x');
            if (captured.Count > 0) captured[0]();

            CheckEqual("引擎：缓冲变了就放弃展开（不计数）", e.SnippetMatchesForTest, 0);
            CheckEqual("引擎：缓冲变了就不删字符", e.SnippetDeletesForTest, 0);

            e.Dispose();
            q.Dispose();
        }

        /* ③ 焦点不可编辑 → 放弃展开、一个字符都不删。
         *    这里走真实队列（不再接管），验证"判断发生在队列里"这条路径。 */
        {
            var s = DefaultSettings.Create();
            foreach (var t in s.Triggers) t.Enabled = false;
            s.Global.Snippets.Clear();
            s.Hotstring.Prefix = ";";
            s.Hotstring.ExpandOnSpace = false;
            s.Hotstring.RestoreImeAfterExpand = false;
            s.Global.Snippets.Add(new Snippet { Trigger = ";mail", Text = "a@b.com" });

            var q = new ActionQueue();
            var e = new HotkeyEngine(q, () => s, (_, _) => true);
            e.EditableForTest = () => false;      // 焦点在按钮/只读框上

            foreach (int vk in new[] { 0xBA, 'M', 'A', 'I', 'L' })
            {
                e.SimulateKey(vk, true);
                e.SimulateKey(vk, false);
            }

            WaitUntil(() => e.SnippetMatchesForTest >= 1);
            Check("引擎：不可编辑目标也会走到展开这一步", e.SnippetMatchesForTest >= 1,
                e.SnippetMatchesForTest.ToString());
            CheckEqual("引擎：不可编辑目标一个字符都不删", e.SnippetDeletesForTest, 0);

            e.Dispose();
            q.Dispose();
        }

        /* ④ 退格之后仍然能命中（本次改造的目的本身）。
         *    逐键模式：`;maii` → 退格 → `l`，应当当场展开。 */
        {
            var s = DefaultSettings.Create();
            foreach (var t in s.Triggers) t.Enabled = false;
            s.Global.Snippets.Clear();
            s.Hotstring.Prefix = ";";
            s.Hotstring.ExpandOnSpace = false;
            s.Hotstring.RestoreImeAfterExpand = false;
            s.Global.Snippets.Add(new Snippet { Trigger = ";mail", Text = "a@b.com" });

            var q = new ActionQueue();
            var e = new HotkeyEngine(q, () => s, (_, _) => true);
            e.EditableForTest = () => false;      // 只验证"命中"，不真的敲键盘

            foreach (int vk in new[] { 0xBA, 'M', 'A', 'I', 'I' })
            {
                e.SimulateKey(vk, true);
                e.SimulateKey(vk, false);
            }
            DoEvents(60);
            CheckEqual("引擎：打错（;maii）不展开", e.SnippetMatchesForTest, 0);

            e.SimulateKey(0x08, true);            // 退格
            e.SimulateKey(0x08, false);
            e.SimulateKey(0x4C, true);            // 字母 L
            e.SimulateKey(0x4C, false);

            WaitUntil(() => e.SnippetMatchesForTest >= 1);
            Check("引擎：退格改对之后能命中", e.SnippetMatchesForTest >= 1,
                e.SnippetMatchesForTest.ToString());

            e.Dispose();
            q.Dispose();
        }

        /* ⑤ 空格触发模式：删除数必须是触发词长度（R3），不是缓冲长度。 */
        {
            var s = DefaultSettings.Create();
            foreach (var t in s.Triggers) t.Enabled = false;
            s.Global.Snippets.Clear();
            s.Hotstring.Prefix = ";";
            s.Hotstring.ExpandOnSpace = true;
            s.Hotstring.RestoreImeAfterExpand = false;
            s.Global.Snippets.Add(new Snippet { Trigger = ";mail", Text = "a@b.com" });

            var q = new ActionQueue();
            var e = new HotkeyEngine(q, () => s, (_, _) => true);

            var captured = new List<Action>();
            e.QueueForTest = a => { lock (captured) captured.Add(a); return true; };
            e.EditableForTest = () => true;

            foreach (int vk in new[] { 0xBA, 'M', 'A', 'I', 'L' })
            {
                e.SimulateKey(vk, true);
                e.SimulateKey(vk, false);
            }
            e.SimulateKey(0x20, true);
            e.SimulateKey(0x20, false);

            CheckEqual("引擎：空格命中后交了 1 个展开动作", captured.Count, 1);
            if (captured.Count > 0) captured[0]();
            Check("引擎：空格命中后删了字符", e.SnippetDeletesForTest == 5,
                e.SnippetDeletesForTest.ToString());

            e.Dispose();
            q.Dispose();
        }

        /* ⑥ 用户报的「一个别名第一次输入失败、第二次才成功」（2026-11-23）。
         *
         * 这里钉**状态机**这一侧：同一串按键连打两遍，两遍都必须当场命中，
         * 第一遍不许留下任何"下一次才生效"的东西（残留缓冲、回读锁、
         * 挂起中的展开都会造成这种表现）。
         *
         * 另一半原因在输入法那边：软件曾偷偷把前台窗口的输入语言切到英文，
         * 而切换是**异步**的 —— 第一遍打字时它落在中途，触发词的字母一半进了
         * 组字串、一半进了控件，展开时怎么删都错；第二遍布局已经是英文，
         * 看起来就"正常了"。那条路已整条删掉（由 TestImeGuard 的反射用例钉住）。 */
        {
            var s = DefaultSettings.Create();
            foreach (var t in s.Triggers) t.Enabled = false;
            s.Global.Snippets.Clear();
            s.Hotstring.Prefix = ";";
            s.Hotstring.ExpandOnSpace = true;
            s.Hotstring.RestoreImeAfterExpand = false;
            s.Global.Snippets.Add(new Snippet { Trigger = ";mail", Text = "a@b.com" });

            var q = new ActionQueue();
            var e = new HotkeyEngine(q, () => s, (_, _) => true);
            e.EditableForTest = () => true;

            var captured = new List<Action>();
            e.QueueForTest = a => { lock (captured) captured.Add(a); return true; };

            /* 打一遍 `;mail` + 空格，把排队的展开真跑一次（干跑：不往系统里打字），
             * 返回 (本次命中数, 本次回删数)。 */
            (int Hits, int Deletes) TypeOnce()
            {
                int hitsBefore = e.SnippetMatchesForTest;
                int deletesBefore = e.SnippetDeletesForTest;

                int postedBefore;
                lock (captured) postedBefore = captured.Count;

                foreach (int vk in new[] { 0xBA, 'M', 'A', 'I', 'L' })
                {
                    e.SimulateKey(vk, true);
                    e.SimulateKey(vk, false);
                }
                e.SimulateKey(0x20, true);              // 空格 = 触发
                e.SimulateKey(0x20, false);

                Action? work = null;
                lock (captured) { if (captured.Count > postedBefore) work = captured[postedBefore]; }
                work?.Invoke();

                return (e.SnippetMatchesForTest - hitsBefore, e.SnippetDeletesForTest - deletesBefore);
            }

            var first = TypeOnce();
            CheckEqual("引擎：别名第一次输入就命中（不是第二次才成功）", first.Hits, 1);
            CheckEqual("引擎：第一次命中按触发词长度回删", first.Deletes, 5);

            var second = TypeOnce();
            CheckEqual("引擎：同一别名紧接着第二次输入也命中", second.Hits, 1);
            CheckEqual("引擎：第二次命中同样按触发词长度回删", second.Deletes, 5);

            e.Dispose();
            q.Dispose();
        }

        /* ⑦ 缓冲刚被标成"不可信"（换了焦点 / 上一步有键被引擎吞掉）时，
         * 第一次输入也要命中。这一路会强制回读控件文本，而回读可能把一份
         * 正确的缓冲冲成别的样子 —— 兜底是"回读之前的物理缓冲"（见 ObserveSnippet ④⑤）。 */
        {
            var s = DefaultSettings.Create();
            foreach (var t in s.Triggers) t.Enabled = false;
            s.Global.Snippets.Clear();
            s.Hotstring.Prefix = ";";
            s.Hotstring.ExpandOnSpace = true;
            s.Hotstring.RestoreImeAfterExpand = false;
            s.Global.Snippets.Add(new Snippet { Trigger = ";mail", Text = "a@b.com" });

            var q = new ActionQueue();
            var e = new HotkeyEngine(q, () => s, (_, _) => true);
            e.EditableForTest = () => true;

            /* 展开动作走真实队列时是**异步**执行的（SnippetMatchesForTest 在队列里才加）。
             * 把它抓下来当场干跑，才能断言"这一次就命中"，而不是断言"过一会儿命中"。 */
            var captured = new List<Action>();
            e.QueueForTest = a => { lock (captured) captured.Add(a); return true; };

            e.SnippetBufferForTest.MarkUntrusted();     // "屏幕状态未知"

            int hitsBefore = e.SnippetMatchesForTest;
            foreach (int vk in new[] { 0xBA, 'M', 'A', 'I', 'L' })
            {
                e.SimulateKey(vk, true);
                e.SimulateKey(vk, false);
            }
            e.SimulateKey(0x20, true);
            lock (captured) { foreach (var work in captured) work(); }
            CheckEqual("引擎：缓冲不可信时第一次输入也命中",
                e.SnippetMatchesForTest - hitsBefore, 1);
            e.SimulateKey(0x20, false);

            e.Dispose();
            q.Dispose();
        }

        /* ⑧ 换输入框：上一个框的尾巴不能拼到这一框的触发词前面。
         *
         * 现场：用户点了另一个输入框，直接打 `;mail` —— 缓冲尾部还是上一个框最后打的
         * 几个字母，匹配器看到"字母 + ;mail"，按词边界判据（"my;mail" 不算词首）
         * 拒绝匹配，于是第一次不展开；用户接着按的空格把缓冲清空了，第二遍才成功。
         * 正确做法是**换控件就清缓冲**（见 HotkeyEngine.NoteSnippetFocus）。 */
        {
            var s = DefaultSettings.Create();
            foreach (var t in s.Triggers) t.Enabled = false;
            s.Global.Snippets.Clear();
            s.Hotstring.Prefix = ";";
            s.Hotstring.ExpandOnSpace = true;
            s.Hotstring.RestoreImeAfterExpand = false;
            s.Global.Snippets.Add(new Snippet { Trigger = ";mail", Text = "a@b.com" });

            var q = new ActionQueue();
            var e = new HotkeyEngine(q, () => s, (_, _) => true);
            e.EditableForTest = () => true;

            var captured = new List<Action>();
            e.QueueForTest = a => { lock (captured) captured.Add(a); return true; };

            /* 先在"旧输入框"里打三个字母 —— 它们只属于那个框。 */
            foreach (int vk in new[] { 'A', 'B', 'C' })
            {
                e.SimulateKey(vk, true);
                e.SimulateKey(vk, false);
            }

            /* 用户点到另一个输入框：把记录的焦点改成一个不存在的句柄，
             * 下一次按键就会被判定成"换控件了"。 */
            e.SnippetFocusForTest = new IntPtr(1);

            int hitsBefore = e.SnippetMatchesForTest;
            int deletesBefore = e.SnippetDeletesForTest;
            foreach (int vk in new[] { 0xBA, 'M', 'A', 'I', 'L' })
            {
                e.SimulateKey(vk, true);
                e.SimulateKey(vk, false);
            }
            e.SimulateKey(0x20, true);
            lock (captured) { foreach (var work in captured) work(); }

            CheckEqual("引擎：换输入框后第一次打别名就命中",
                e.SnippetMatchesForTest - hitsBefore, 1);
            CheckEqual("引擎：换输入框后只删触发词（上一个框的字没被删）",
                e.SnippetDeletesForTest - deletesBefore, 5);
            e.SimulateKey(0x20, false);

            e.Dispose();
            q.Dispose();
        }
    }
}
