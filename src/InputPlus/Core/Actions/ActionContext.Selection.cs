using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using InputPlus.Core.Input;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;
using InputPlus.Core.Text;
using InputPlus.Core.Windows;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Actions;

/// <summary>
/// 选区读取、选中即搜、智能打开、打字与 Tab 脚本。与 ActionContext 同属 partial 类。
/// </summary>
public sealed partial class ActionContext
{

    public bool UndoRedo()
    {
        if (_undoNext) { InputSender.Ctrl(0x59); }   // Ctrl+Y
        else { InputSender.Ctrl(0x5A); }             // Ctrl+Z
        _undoNext = !_undoNext;
        return true;
    }

    /// <summary>
    /// 用指定搜索引擎在默认浏览器里搜索**当前选中的文字**。
    ///
    /// 与「Qbar 搜索」的区别：不弹面板，直接拿选区去搜。
    /// 适合"看到一段文字，按一下就查"这种高频动作。
    /// </summary>
    /// <param name="engineKey">搜索引擎关键字（bd / zh / go…）</param>
    /// <param name="overrideText">自检用：直接指定要搜索的文字</param>
    public bool SearchSelection(string engineKey, string? overrideText = null)
    {
        var entry = Settings().Search.FirstOrDefault(x =>
            x.Key.Equals(engineKey, StringComparison.OrdinalIgnoreCase));

        /* 引擎被删掉/改名后，绑定里还留着 search:旧key。
         * 以前这里直接"这个搜索引擎还没配置"就退出 —— 用户看到的是"按了没反应"；
         * 手势那边更糟：ActionCatalog 根本没注册这个 id，连这句提示都到不了。
         * 现在退一步用第一个可用引擎，至少把搜索做完，并留一行日志说明换过了。 */
        if (entry == null || string.IsNullOrWhiteSpace(entry.Value))
        {
            var fallback = Settings().Search.FirstOrDefault(x =>
                !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(x.Value));
            if (fallback == null)
            {
                Notify("这个搜索引擎还没配置");
                return true;
            }

            CoreLog.Warn($"[搜索] 引擎 {engineKey} 不存在，改用 {fallback.Key}");
            entry = fallback;
        }

        /* ⚠ 这是"直接搜索按了没反应"的**真正原因**，别再改回去：
         *
         * 按键绑定里 search:bd 的 Arg 默认是**空字符串**（不是 null）。
         * 而 "" ?? xxx 的结果是 ""（?? 只认 null）——
         * 于是 overrideText 拿着一个空串，后面"去读选区"的分支永远走不到，
         * 直接落到"没有选中的文字"就 return 了：
         * 既不取词，也不开浏览器，日志里连一条 [取词] 都不会有。
         *
         * v3.0 到 v3.4 换了三套取词方案都没修好，就是因为它不在取词通道上，
         * 而在参数判断这一步。所以：空白参数一律归一成 null，回退去读选区。 */
        if (string.IsNullOrWhiteSpace(overrideText)) overrideText = null;

        string? text = overrideText;

        if (text == null)
        {
            text = CaptureSelectionText();
            if (!string.IsNullOrWhiteSpace(text))
                CoreLog.Info($"[搜索] 已取到选中文字 长度={text.Length}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            /* 所有程序化通道都失败时的**保底**：请用户亲自按一下 Ctrl+C。
             * 真实按键不会被 Chromium / Electron 过滤，也不受 UIPI 影响，
             * 拿到内容就立刻用它搜索 —— 这样"直接搜索"在任何程序里都能用。
             *
             * 提示里必须带上**前台是哪个程序**：不带的话用户只知道"没反应"，
             * 我们也只能靠猜。 */
            string where = ForegroundAppLabel();
            if (InputSender.IsForegroundHigherIntegrity())
            {
                Notify(Loc.F("没读到选中文字：{0} 以管理员身份运行，系统不允许读取它的选区。" +
                             "请按一下 Ctrl+C，我马上用它搜索", where));
            }
            else if (Core.ElevationHelper.IsElevated)
            {
                Notify(Loc.F("没读到选中文字（{0} × 管理员模式）。请按一下 Ctrl+C，我马上用它搜索", where));
            }
            else
            {
                Notify(Loc.F("没读到选中文字（{0}）。请按一下 Ctrl+C，我马上用它搜索", where));
            }

            var waitManualCopy = ManualCopyWaitForTest
                                 ?? new Func<int, string>(Text.TextActions.WaitForManualCopy);
            /* 5 秒太短：提示刚弹出来、眼睛刚看到、手还没按下去就超时了。
             * 真实按键不受任何过滤，值得多等一会儿。 */
            text = waitManualCopy(ManualCopyWaitMs);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify("没有取到选中的文字 —— 先选中要搜索的内容再按这个键");
            return true;
        }

        text = text.Trim();

        // 太长的话不截断会撑爆 GET 请求长度，也会让搜索页体验很差
        const int maxLen = 1000;
        if (text.Length > maxLen)
        {
            /* 截断不能把代理对（emoji 等）劈成两半：
             * 半截代理项会让 Uri.EscapeDataString 直接抛 UriFormatException，
             * 表现是"按了搜索，提示正在搜索，然后什么都没打开"。 */
            int cut = maxLen;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..cut];
        }

        string encoded;
        try
        {
            encoded = Uri.EscapeDataString(text);
        }
        catch (UriFormatException)
        {
            // 极端脏数据（无效代理项）也不该让动作崩掉：退到"逐字符安全编码"
            var sb = new StringBuilder(text.Length * 3);
            foreach (char ch in text)
            {
                if (char.IsSurrogate(ch)) continue;
                sb.Append(Uri.EscapeDataString(ch.ToString()));
            }
            encoded = sb.ToString();
        }
        string url;
        if (entry.Value.Contains("{q}", StringComparison.Ordinal))
        {
            url = entry.Value.Replace("{q}", encoded);
        }
        else
        {
            /* 自定义引擎忘了写 {q} 时，不能把关键词默默丢掉 ——
             * 那样只会打开搜索引擎首页，用户看到的就是"没搜"。
             * 按最常见的约定补一个 q 参数。 */
            CoreLog.Warn($"[搜索] 引擎 {engineKey} 的地址里没有 {{q}}，改为追加 q 参数");
            url = entry.Value + (entry.Value.Contains('?') ? "&q=" : "?q=") + encoded;
        }

        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        /* 提示里带上"到底搜的是什么"。
         * 取词有四条通道，走错一条（比如拿到相邻列表项的名字、上一次复制的
         * 内容）时，用户只会看到浏览器打开了一个不相干的结果页，
         * 完全不知道搜的是哪段字。带上预览就能一眼看出来。 */
        Notify(Loc.F("正在搜索「{0}」…", Preview(text)));
        CoreLog.Info($"[搜索] {engineKey} → {url}");
        OpenUrlRaw(url);   // 已编码，跳过规范化避免搜索词被解码/截断
        return true;
    }

    /// <summary>
    /// 用某个**文件搜索过滤器**（wd = 文档 / pic = 图片 / 自己加的）搜当前选中的文字。
    ///
    /// 场景：在资源管理器里选中一个词、或在网页上看到一段名字，按一下绑好的键 ——
    /// Qbar 面板打开时**已经挂好过滤器 chip**，结果只在这个类型里。
    /// 与 <see cref="SearchSelection"/> 的区别只在"最后把词交给谁"：那边是浏览器，
    /// 这边是 Qbar 面板；取词那一段完全共用（三层提示、Ctrl+C 保底、长度截断），
    /// 不另写一套，否则"读不到选区"时的体验会两样。
    /// </summary>
    /// <param name="filterIdOrKeyword">过滤器 Id（builtin.doc），或关键字（wd / doc）</param>
    /// <param name="overrideText">自检用：直接指定要搜的文字</param>
    public bool SearchSelectionAsFile(string filterIdOrKeyword, string? overrideText = null)
    {
        var all = QbarFilterCatalog.Create(Settings().Qbar.Filters).All;
        string key = (filterIdOrKeyword ?? "").Trim();

        /* 先按 Id 认，再按关键字认：过滤器被改过关键字之后，
         * 绑定里留着的还是 Id —— 只按关键字找会让"改了 wd 就失效"。 */
        QbarFilter? filter =
            all.FirstOrDefault(f => string.Equals(f.Id, key, StringComparison.Ordinal))
            ?? all.FirstOrDefault(f => f.Keyword.Equals(key, StringComparison.OrdinalIgnoreCase));

        if (filter == null)
        {
            /* 过滤器被删掉之后，绑定里还留着 qfilter:旧Id。
             * 报"没反应"等于让用户去猜，直接说清是哪一条没了。 */
            Notify(Loc.F("找不到这个过滤器：{0}（到设置 → Qbar → 文件搜索 → 过滤器里看一眼）", key));
            return true;
        }

        /* 文件名/标题很少超过 200 字。整段网页正文塞进 Qbar 输入框，
         * 结果只会是一屏无关文件，还把过滤器 chip 和输入框一起挤变形。 */
        if (!TryCaptureWord(Loc.T("搜文件"), overrideText, 300, out string text)) return true;

        CoreLog.Info($"[文件搜索] 过滤器={filter.Id} 词长={text.Length}");
        RequestQbarFileSearch(filter.Id, text);
        return true;
    }

    /* ── 查词 ───────────────────────────────────────────────────────
       两条路都从"选中的那一小段文字"出发，区别只在最后把词交给谁：
         · 直接搜索：立刻把词送进欧路词典（没装就开网页词典）—— 按一下就出词典窗口；
         · Qbar 搜索：打开面板、ol 变 chip、词填好 —— 想按 Esc 或者换个词再查。
       取词不另写一套：三层提示 + Ctrl+C 保底 + 截断必须和文件搜索一致，
       否则同一个"读不到选区"的场景会给出两种解释。 */

    /// <summary>查词时最多取多少字：整段正文塞给词典只会查不到。</summary>
    private const int DictionaryMaxLen = 120;

    /// <summary>
    /// 「直接搜索」分类里的查词：把选中的词直接送进欧路词典。
    ///
    /// 与面板里按回车那条路的差别只是"要不要先看见面板"：
    /// 这条路按下去就出词典窗口，适合"看到不认识的词，按一下看释义"。
    /// </summary>
    /// <param name="overrideText">自检用：直接指定要查的词</param>
    public bool LookupSelectionInDictionary(string? overrideText = null)
    {
        if (!TryCaptureWord(Loc.T("查词"), overrideText, DictionaryMaxLen, out string word)) return true;

        QbarCommand? template = DictionaryLauncher.EffectiveCommand(Settings().Qbar.Commands);
        DictionaryLaunchResult run = DictionaryLauncher.Launch(word, template);
        CoreLog.Info($"[查词] 词长={word.Length} 目标={(run.External ? "欧路词典" : "网页词典")}");

        /* 外部程序成功时 launcher 不给文案（它只负责启动），这里补一句"查的是哪个词"；
           走网页时那条"没装欧路"的说明由 launcher 带上来，别覆盖掉。 */
        Notify(run.Ok && run.External ? Loc.F("查词：{0}", Preview(word)) : run.Message);
        return true;
    }

    /// <summary>
    /// 「Qbar 搜索」分类里的查词：打开面板、挂 ol chip、词填好。
    /// 想在面板里顺手改词、或者复制某条释义时走这条。
    /// </summary>
    public bool SearchSelectionWithDictionary(string? overrideText = null)
    {
        if (!TryCaptureWord(Loc.T("查词"), overrideText, DictionaryMaxLen, out string word)) return true;
        CoreLog.Info($"[查词] 交给 Qbar 面板 词长={word.Length}");
        RequestQbarDictionary(word);
        return true;
    }

    /// <summary>
    /// 取一小段要处理的文字：选区 → 请用户按 Ctrl+C → 截断。
    /// <paramref name="action"/> 只影响提示里的动词（"搜文件" / "查词"），
    /// 提示本身必须和别的取词路径一模一样 —— 用户不该因为换了条功能就看到不同的解释。
    /// </summary>
    private bool TryCaptureWord(string action, string? overrideText, int maxLen, out string text)
    {
        text = "";
        if (string.IsNullOrWhiteSpace(overrideText)) overrideText = null;
        string? captured = overrideText ?? CaptureSelectionText();

        if (string.IsNullOrWhiteSpace(captured))
        {
            string where = ForegroundAppLabel();
            Notify(InputSender.IsForegroundHigherIntegrity()
                ? Loc.F("没读到选中文字：{0} 以管理员身份运行，系统不允许读取它的选区。请按一下 Ctrl+C，我马上用它{1}", where, action)
                : Core.ElevationHelper.IsElevated
                    ? Loc.F("没读到选中文字（{0} × 管理员模式）。请按一下 Ctrl+C，我马上用它{1}", where, action)
                    : Loc.F("没读到选中文字（{0}）。请按一下 Ctrl+C，我马上用它{1}", where, action));

            var waitManualCopy = ManualCopyWaitForTest
                                 ?? new Func<int, string>(Text.TextActions.WaitForManualCopy);
            captured = waitManualCopy(ManualCopyWaitMs);
        }

        if (string.IsNullOrWhiteSpace(captured))
        {
            Notify(Loc.F("没取到要{0}的文字 —— 先选中它再按这个键", action));
            return false;
        }

        text = captured.Trim();
        if (text.Length > maxLen)
        {
            int cut = maxLen;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;   // 别把 emoji 劈成半个
            text = text[..cut];
        }
        return text.Length > 0;
    }

    /// <summary>
    /// 用一条 Qbar 命令处理**当前选中的文字**。
    ///
    /// 场景：配一条命令 <c>ol</c>（欧路词典 <c>-w {query}</c>），把它绑到一个键上；
    /// 在任意程序里选中一个单词，按这个键 —— 词典直接带着这个词打开。
    ///
    /// 与 <see cref="SearchSelection"/> 的区别只在"谁来处理"：
    /// 那边是浏览器 + 网址模板，这边是任意程序 + 参数模板（{query} / {action_path} / …），
    /// runas / silent / workdir 全都按命令自己的配置生效 —— 取词那一段完全共用，
    /// 所以"读不到选区"时的三层提示、Ctrl+C 保底、长度截断都不会两套。
    /// </summary>
    /// <param name="commandIdOrKeyword">命令 Id，或命令关键字（ol / 词典）</param>
    /// <param name="overrideText">自检用：直接指定要处理的文字</param>
    public bool SearchSelectionWithCommand(string commandIdOrKeyword, string? overrideText = null)
    {
        string key = (commandIdOrKeyword ?? "").Trim();
        List<QbarCommand> table = QbarCommandStore.Effective(Settings().Qbar.Commands);
        QbarCommand? command =
            table.FirstOrDefault(c => string.Equals(c.Id, key, StringComparison.Ordinal))
            ?? table.FirstOrDefault(c => c.Keyword.Equals(key, StringComparison.OrdinalIgnoreCase));

        if (command == null)
        {
            /* 命令被删掉/改了关键字之后，绑定里还留着旧 Id。
             * 报"没反应"等于让用户去猜，直接说清是哪条命令没了。 */
            Notify(Loc.F("找不到这条命令：{0}（到 Qbar → 文件搜 → 命令里看一眼）", key));
            return true;
        }

        if (string.IsNullOrWhiteSpace(overrideText)) overrideText = null;
        string? text = overrideText ?? CaptureSelectionText();
        if (string.IsNullOrWhiteSpace(text))
        {
            string where = ForegroundAppLabel();
            Notify(InputSender.IsForegroundHigherIntegrity()
                ? Loc.F("没读到选中文字：{0} 以管理员身份运行，系统不允许读取它的选区。请按一下 Ctrl+C，我马上用它查词", where)
                : Loc.F("没读到选中文字（{0}）。请按一下 Ctrl+C，我马上用它查词", where));

            var waitManualCopy = ManualCopyWaitForTest
                                 ?? new Func<int, string>(Text.TextActions.WaitForManualCopy);
            text = waitManualCopy(ManualCopyWaitMs);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify(Loc.T("没有取到选中的文字 —— 先选中要查询的内容再按这个键"));
            return true;
        }

        text = text.Trim();
        if (text.Length > 2000) text = text[..2000];   // 参数不是网址，长度上限只防手滑全选

        /* 拦截钩子只替换"真正启动程序"这一步，成功文案照发 ——
         * 否则自检看到的是一半流程，用户看到的那句反馈反而没人测。 */
        if (RunQbarCommandInterceptForTest != null)
        {
            RunQbarCommandInterceptForTest(command, text);
        }
        else
        {
            QbarCommandResult run = QbarCommandStore.Execute(command, text, null, null);
            if (!run.Ok)
            {
                // 失败文案由 QbarCommandStore 分好层（找不到程序 / 提权被取消 / 路径非法），别在这里糊成一句
                Notify(run.Message);
                return true;
            }
        }

        string name = string.IsNullOrWhiteSpace(command.Title) ? command.Keyword : command.Title;
        Notify(Loc.F("用「{0}」查「{1}」…", name, Preview(text)));
        CoreLog.Info($"[命令搜索] {name} ← {Preview(text)}");
        return true;
    }

    /// <summary>
    /// 取当前选区文字：主动取词优先，其次用「选中即捕获」的新鲜结果。
    ///
    /// 顺序是刻意这样排的：
    ///   ① 主动取词不需要用户额外操作，是"选中 + 按键"一步到位的正路；
    ///   ② 监听器的结果只在**几秒内**才用，否则会搜到很久以前复制的内容。
    /// 两条都拿不到才交给调用方去请用户手动 Ctrl+C。
    /// </summary>
    private string CaptureSelectionText()
    {
        if (CaptureTextForTest != null) return CaptureTextForTest() ?? "";

        /* 记住"按下这个键"的时刻。
         * 监听器里的内容是用户上一次 Ctrl+C 的结果 —— 如果那次复制发生在这之前
         * 很久（换了个程序、改了选区），拿它去搜就是**搜别的东西**，
         * 用户看到的现象就是"没有搜我选中的文字"。所以只认紧贴这次按键的捕获。 */
        DateTime invokedAt = DateTime.UtcNow;

        // ① UIA → WM_COPY → SendInput → 线程级注入 → Ctrl+Insert
        string text = Text.TextActions.GetSelectedText();
        if (!string.IsNullOrWhiteSpace(text)) return text;

        // ② 就在按下这个键前后自己按过 Ctrl+C，就别再折腾了
        var watcher = SelectionWatcher;
        if (watcher != null
            && !string.IsNullOrWhiteSpace(watcher.LastSelection)
            && watcher.LastSelectionAt > DateTime.MinValue
            && watcher.LastSelectionAt >= invokedAt - WatcherGrace)
        {
            string fresh = watcher.LastSelection;
            CoreLog.Info($"[搜索] 用选区监听器捕获的内容 长度={fresh.Length} " +
                         $"距今={(long)(DateTime.UtcNow - watcher.LastSelectionAt).TotalMilliseconds}ms");
            return fresh;
        }

        if (watcher != null && !string.IsNullOrWhiteSpace(watcher.LastSelection))
        {
            // 有一条旧捕获，但它比这次按键早太多：宁可请用户再按一次 Ctrl+C，
            // 也不能把旧内容当成本次选区（那是"搜错东西"最常见的原因）。
            CoreLog.Info($"[搜索] 忽略了 {Math.Max(0, (long)(DateTime.UtcNow - watcher.LastSelectionAt).TotalMilliseconds)}ms 前的旧捕获");
        }

        return string.Empty;
    }

    /// <summary>请用户手动按 Ctrl+C 时最多等多久（毫秒）。</summary>
    private const int ManualCopyWaitMs = 8000;

    /// <summary>
    /// 监听器捕获的内容比这次按键早多少以内还算"同一次操作"（宽限）。
    /// 用户选中文字与按快捷键之间通常不到一秒；给到 1.5 秒既能容忍慢手，
    /// 又不至于把上一段复制的内容当成本次选区。
    /// </summary>
    private static readonly TimeSpan WatcherGrace = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// 前台程序的名字，用来放进"没读到选中文字"的提示里。
    /// 取不到 exe 名就退回窗口类名，再不行给个泛称 —— 这个值只用于提示，不许抛。
    /// </summary>
    private static string ForegroundAppLabel()
    {
        try
        {
            var info = Core.Windows.WindowService.ForegroundInfo();
            if (info != null)
            {
                if (!string.IsNullOrWhiteSpace(info.ExeName)) return info.ExeName;
                if (!string.IsNullOrWhiteSpace(info.ClassName)) return info.ClassName;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "取前台程序名");
        }
        return Loc.T("前台窗口");
    }

    /// <summary>
    /// 把取到的文字压成一行短摘要（提示 / 日志里用）。
    /// 换行与制表符当空格处理，超长截断；截断点不能劈开代理对。
    /// </summary>
    internal static string Preview(string? text, int max = 18)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string flat = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        while (flat.Contains("  ", StringComparison.Ordinal))
            flat = flat.Replace("  ", " ");
        if (flat.Length <= max) return flat;
        int cut = max;
        if (char.IsHighSurrogate(flat[cut - 1])) cut--;
        return flat[..cut] + "…";
    }

    /// <summary>
    /// 「选中即捕获」监听器。用户在任何程序里 Ctrl+C 时，
    /// 这里就存下了选中的文字 —— 比主动取词可靠得多
    /// （Electron / Chromium 会过滤合成输入，但用户的真实按键不会被拦）。
    /// </summary>
    public Core.Text.SelectionWatcher? SelectionWatcher { get; set; }

    /// <summary>
    /// 自检用：替换真实的取词通道，直接返回指定文字。
    /// 自检跑在没有真实选区的环境里，必须能绕开剪贴板/UI Automation。
    /// </summary>
    public Func<string?>? CaptureTextForTest { get; set; }

    /// <summary>
    /// 自检用：替换"请用户按 Ctrl+C 再等待"的保底逻辑，避免自检真的卡 4 秒。
    /// </summary>
    public Func<int, string>? ManualCopyWaitForTest { get; set; }

    /// <summary>自检用：拦下"选中文字 → Qbar 命令"的最终执行，避免自检真的启动词典。</summary>
    public static Action<QbarCommand, string>? RunQbarCommandInterceptForTest { get; set; }

    /// <summary>
    /// 自检用：指定"剪贴板里最新的一条内容"，避免自检受到真实剪贴板（可能是用户刚复制的密码）影响。
    /// 返回 null / 空串表示剪贴板是空的。
    /// </summary>
    public Func<string?>? ClipboardTextForTest { get; set; }

    /// <summary>
    /// 把一段文字直接输入到当前焦点处（具体走哪条通道见
    /// <see cref="InputSender.Text"/>：长文本剪贴板粘贴，短文本真实按键 / Unicode）。
    /// </summary>
    public bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        try
        {
            // 太长会有明显延迟，限制一下避免误操作
            const int maxLen = 2000;
            string payload = text.Length > maxLen ? text[..maxLen] : text;

            /* 输入方式跟着设置走：
             *   auto（默认）→ 长文本剪贴板粘贴；短文本纯 ASCII 用真实按键
             *                 （密码框认这个），其它 Unicode；
             *   keys        → 强制真实按键；
             *   unicode     → 强制 Unicode 注入。
             * 老配置里没有这个字段，反序列化后是默认值 auto，行为即"修好密码框"。 */
            string mode = Settings().Global.TextInputMode ?? "auto";
            var input = mode.ToLowerInvariant() switch
            {
                "keys" => InputSender.TextMode.Keys,
                "unicode" => InputSender.TextMode.Unicode,
                _ => InputSender.TextMode.Auto,
            };

            InputSender.Text(payload, input);
        }
        catch (Exception ex)
        {
            Notify(UiText.Failed("输入文字", ex.Message));
            CoreLog.Warn("输入文字失败：" + ex.Message);
        }
        return true;
    }

    /// <summary>
    /// 「直达链接」：把当前**选中的文字**识别成网址 / 文件夹 / 文件并打开。
    ///
    /// 与 <see cref="OpenUrl"/>、<see cref="OpenFolder"/> 的区别：
    /// 那两个是"先填地址、按键就开"，目标在设置里写死；
    /// 这个是"眼前有什么就开什么"，用户选中一段文字按下即可，不用预先配置。
    ///
    /// 也正因为目标来自选区，它被归到「智能直达」分类，和「打开网址」「运行命令」并列 ——
    /// 三者都是"给什么开什么"，只是参数来源不同（手填 / 选区）。
    /// </summary>
    /// <param name="overrideText">自检用：直接指定"选中的文字"，绕开真实取词。</param>
    public bool SmartOpen(string? overrideText = null)
    {
        string text = overrideText ?? "";
        bool fromSelection = !string.IsNullOrWhiteSpace(text);

        if (!fromSelection)
        {
            text = CaptureSelectionText();
            fromSelection = !string.IsNullOrWhiteSpace(text);
            if (fromSelection)
                CoreLog.Info($"[直达链接] 已取到选中文字 长度={text.Length}");
        }

        if (!fromSelection)
        {
            /* 没有选区：改看剪贴板里最新的一条。
             *
             * 顺序＝系统剪贴板（资源管理器里 Ctrl+C 复制的文件/文件夹就在这里，是"路径"而不是文本）
             *        → 本软件记下的历史第一条（有些程序复制完会立刻清空系统剪贴板）。
             *
             * 4.32.0 起不再弹「请按一下 Ctrl+C」：用户要的是"没选区就看剪贴板"，
             * 剪贴板也空着就当这次没按 —— 静默返回，不打扰正在打字的人。 */
            text = NewestClipboardText() ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                CoreLog.Info("[直达链接] 没有选区，剪贴板里也没有内容：这次不触发");
                return true;
            }
            CoreLog.Info($"[直达链接] 没有选区，改用剪贴板里的内容 长度={text.Length}");
        }

        var hit = SmartTargetDetector.Detect(text);
        CoreLog.Info($"[直达链接] 识别结果={hit.Kind} 目标={hit.Target} 原文长度={text.Length}");

        switch (hit.Kind)
        {
            case SmartKind.Url:
                Notify(Loc.F("打开网址：{0}", UrlUtil.HostOf(hit.Target)));
                OpenUrl(hit.Target);
                break;

            case SmartKind.Folder:
                Notify("正在打开文件夹…");
                OpenFolder(hit.Target);
                break;

            case SmartKind.File:
                Notify("正在用默认程序打开…");
                OpenFile(hit.Target);
                break;

            case SmartKind.MissingFolder:
                Notify(Loc.F("文件夹不存在：{0}", hit.Target));
                break;

            case SmartKind.MissingFile:
                Notify(Loc.F("文件不存在：{0}", hit.Target));
                break;

            default:
                /* 普通文字（一句话、一个单词）不是链接。不要试图去搜索 ——
                 * 这个动作的名字是「智能直达」，猜错了打开浏览器比"什么都不做"更烦人。
                 *
                 * 选区内容不对时提一句（用户刚选了东西、正等着结果）；
                 * 剪贴板内容不对时完全静默 —— 他压根没打算用这个功能，
                 * 只是顺路按了一下，弹提示反而打断打字。 */
                if (fromSelection)
                    Notify("选中的内容既不是网址，也不是文件夹或文件路径");
                else
                    CoreLog.Info("[直达链接] 剪贴板里的内容不是链接：这次不触发");
                break;
        }

        return true;
    }

    /// <summary>
    /// 剪贴板里最新的一条：先看系统剪贴板（文件/文件夹优先，资源管理器复制的是路径不是文本），
    /// 再退回本软件记下的历史第一条。
    /// </summary>
    private string? NewestClipboardText()
    {
        if (ClipboardTextForTest != null) return ClipboardTextForTest();

        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList();
                if (files is { Count: > 0 }) return files[0];
            }
            if (System.Windows.Clipboard.ContainsText())
                return System.Windows.Clipboard.GetText();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取系统剪贴板");
        }

        return Clipboard.History.FirstOrDefault();
    }

    /// <summary>Caps+Tab：把光标前的算式算出结果 / 展开热字串。</summary>
    public bool RunTabScript()    {
        var settings = Settings();
        string selected = Text.TextActions.GetSelectedText();
        if (!string.IsNullOrEmpty(selected))
        {
            string replaced = ExpandHotString(selected, settings);
            if (replaced == selected && ExpressionEvaluator.TryEvaluate(selected.TrimEnd('='), out var v, out _))
                replaced = ExpressionEvaluator.Format(v);
            if (replaced != selected) Text.TextActions.ReplaceSelection(replaced);
            return true;
        }

        // 没有选区：选中光标左边这一“词”，尝试计算 / 展开
        Text.TextActions.SelectLineToCaret(out string left);
        if (string.IsNullOrEmpty(left)) return true;

        string expanded = ExpandHotString(left, settings);
        if (expanded != left)
        {
            Text.TextActions.ReplaceSelection(expanded);
            return true;
        }

        string trimmed = left.TrimEnd('=', ' ');
        if (ExpressionEvaluator.TryEvaluate(trimmed, out double value, out string err))
        {
            string result = ExpressionEvaluator.Format(value);
            Notify($"{trimmed} = {result}");
            Text.TextActions.ReplaceSelection(left.TrimEnd().EndsWith('=') ? left + result : result);
            return true;
        }

        Notify(UiText.Failed("算出结果", err));

        /* 失败时要把选区收起来。
         *
         * SelectLineToCaret 是用 Shift+Home 选中"从行首到光标"的，
         * 成功分支会 ReplaceSelection（顺带把选区替换掉）；
         * 失败分支原来直接 return —— 选中的那段前缀留在原地，
         * 用户接着打一个字，整行前半段就被替换成了那个字。 */
        InputSender.Tap(0x27);   // VK_RIGHT：光标右移一格，顺带取消选择
        return true;
    }

    private static string ExpandHotString(string text, AppSettings settings)
    {
        foreach (var hs in settings.HotStrings)
        {
            if (string.IsNullOrEmpty(hs.Key)) continue;
            if (text.EndsWith(hs.Key, StringComparison.Ordinal))
                return text[..^hs.Key.Length] + hs.Value.Replace("\\n", Environment.NewLine);
        }
        return text;
    }
}
