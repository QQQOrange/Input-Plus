namespace InputPlus.Core.Input;

/// <summary>
/// 缓冲里发生的一类**文本事件**。
///
/// 为什么要把按键翻译成事件、而不是直接往字符串上追加：
/// 别名是靠"屏幕尾巴上是什么"判定的，而屏幕上的字会因为**退格**变短。
/// 以前缓冲区只会追加、遇到认不出的键就清空 —— 于是"打错一个字母、
/// 退格改对"这条最常见的路径永远走不通（缓冲早被清掉了）。
///
/// 这里只保留三类语义：
///   · 插入 / 删除 —— 会改变内容，所以要记代际号；
///   · 移动插入符 —— 只改变"下一次删/插在哪"，不算内容变动；
///   · 判定用的终止符 —— 只告诉调用方"这是一个 token 边界"，不写缓冲。
/// </summary>
public enum TextEventKind
{
    Insert,
    DeleteBack,
    DeleteForward,
    CaretLeft,
    CaretRight,
    CaretHome,
    CaretEnd,

    /// <summary>空格 / 回车 / 输入法提交：只用于判定，不写入缓冲。</summary>
    Terminator,

    /// <summary>发生过无法精确建模的编辑（被吞掉的键、组合键、鼠标点击……）。</summary>
    Untrusted,
}

/// <summary>
/// 可回退的"屏幕尾巴"缓冲区（纯逻辑，无 IO、无界面、无线程）。
///
/// ## 它要解决什么
///
/// 展开别名 = 按 N 次 Backspace 再打内容。N 必须等于**屏幕上真的多出来的字符数**。
/// 旧实现用一个只会追加的 StringBuilder 猜这个数：退格清空缓冲、认不出的键清空缓冲，
/// 于是"打错 → 退格 → 改对 → 空格"永远不命中。用户看到的就是
/// "打错一个字母，这条别名就废了"。
///
/// 现在缓冲跟着屏幕一起**回退**：退格就真的删掉尾巴上的一个字，
/// 删到空还继续删就把自己标成"不可信"（屏幕可能被别的输入源改过），
/// 下次判定前由调用方回读控件真实文本（见 TextMirror）来重新对齐。
///
/// ## 三条不变量
///
/// 1. 内容只反映**最后一个硬边界之后**的 token（超长只留尾部）；
/// 2. 删除是一等事件，**不清空**；
/// 3. 内容每变一次 <see cref="Generation"/> +1 —— 已经排队的展开靠它作废，
///    避免"用户已经继续打字了，程序还在删正文"。
///
/// ## 线程
///
/// 钩子线程写、界面线程（动作队列）读。所有公开成员内部都拿同一把锁 ——
/// 临界区只有几十个字符的拷贝，锁竞争可以忽略；无锁实现要处理 ABA，
/// 出错的表现是"偶发删错字"，不值当。
/// </summary>
public sealed class SnippetBuffer
{
    /// <summary>保留的最大字符数（与匹配器的窗口一致）。</summary>
    private const int Capacity = SnippetMatcher.MaxBuffer;

    private readonly object _gate = new();
    private char[] _chars = new char[Capacity];
    private int _caret;
    private int _len;
    private bool _trusted = true;
    private int _gen;
    private int _contentGen;
    private long _lastChange;

    /// <summary>当前缓冲里有几个字符。</summary>
    public int Length { get { lock (_gate) return _len; } }

    /// <summary>内容变过几次。只增不减，用来作废已经排队的展开。</summary>
    public int Generation { get { lock (_gate) return _gen; } }

    /// <summary>
    /// **只有内容真的变了**才会增加的代际号。
    ///
    /// 为什么要跟 <see cref="Generation"/> 分开：排队中的展开只该被"用户又打了字"作废，
    /// 不该被"吞了一个键 / 点了一下鼠标"作废 —— 后两者没改变屏幕上的一个字符。
    /// 合成一个号的话，空格自己的**抬起**事件就会把刚排好的展开冲掉
    /// （按下时命中、抬起时标记不可信），表现就是"按空格永远不展开"。
    /// </summary>
    public int ContentGeneration { get { lock (_gate) return _contentGen; } }

    /// <summary>缓冲是否还与屏幕一致（false = 下次判定前应回读真实文本）。</summary>
    public bool Trusted { get { lock (_gate) return _trusted; } }

    /// <summary>最近一次内容变动的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    public long LastChangeTick { get { lock (_gate) return _lastChange; } }

    /// <summary>拷贝一份当前内容（线程安全）。</summary>
    public string Snapshot()
    {
        lock (_gate) return new string(_chars, 0, _len);
    }

    /// <summary>
    /// 把内容拷到 <paramref name="destination"/>（钩子上用，零分配）。
    /// 不足的部分填 '\0'，调用方按 <see cref="Length"/> 取有效长度。
    /// </summary>
    public void CopyTo(Span<char> destination)
    {
        lock (_gate)
        {
            int n = Math.Min(_len, destination.Length);
            for (int i = 0; i < n; i++) destination[i] = _chars[i];
            for (int i = n; i < destination.Length; i++) destination[i] = '\0';
        }
    }

    /// <summary>喂一个文本事件。</summary>
    public void Apply(TextEventKind kind, char ch = '\0')
    {
        lock (_gate)
        {
            switch (kind)
            {
                case TextEventKind.Insert:
                    Insert(ch);
                    break;

                case TextEventKind.DeleteBack:
                    /* 已经退到缓冲开头：屏幕上可能还有字（缓冲只是尾巴），
                     * 这时删掉的到底是什么我们不知道 → 标记不可信，内容不动。 */
                    if (_caret == 0) { MarkUntrustedCore(); return; }
                    RemoveAt(_caret - 1);
                    _caret--;
                    Bump(contentChanged: true);
                    break;

                case TextEventKind.DeleteForward:
                    if (_caret >= _len) { MarkUntrustedCore(); return; }
                    RemoveAt(_caret);
                    Bump(contentChanged: true);
                    break;

                case TextEventKind.CaretLeft:
                    if (_caret > 0) _caret--;
                    break;

                case TextEventKind.CaretRight:
                    if (_caret < _len) _caret++;
                    break;

                case TextEventKind.CaretHome:
                    _caret = 0;
                    break;

                case TextEventKind.CaretEnd:
                    _caret = _len;
                    break;

                case TextEventKind.Terminator:
                    /* 只是"一个 token 到此为止"，不写缓冲、不算内容变动 ——
                     * 否则每次空格都会把排队中的展开作废掉。 */
                    break;

                case TextEventKind.Untrusted:
                    MarkUntrustedCore();
                    break;
            }
        }
    }

    /// <summary>标记"缓冲可能已经和屏幕不一致"，但**保留内容**。</summary>
    public void MarkUntrusted() => Apply(TextEventKind.Untrusted);

    /// <summary>清空并恢复可信（展开完、换 token、配置变了都用它）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _len = 0;
            _caret = 0;
            _trusted = true;
            Bump(contentChanged: true);
        }
    }

    /// <summary>用控件里的真实文本覆盖缓冲（只留尾部 <see cref="Capacity"/> 个字符）。</summary>
    public void ReplaceWith(string? text, bool trusted)
    {
        lock (_gate)
        {
            string value = text ?? "";
            int take = Math.Min(value.Length, Capacity);
            EnsureCapacity(take);
            int start = value.Length - take;
            for (int i = 0; i < take; i++) _chars[i] = value[start + i];
            _len = take;
            _caret = _len;
            _trusted = trusted;
            Bump(contentChanged: true);
        }
    }

    /// <summary>只留尾部 <paramref name="max"/> 个字符。</summary>
    public void TrimTail(int max)
    {
        lock (_gate)
        {
            int limit = Math.Max(0, max);
            if (_len <= limit) return;
            DropHead(_len - limit);
            Bump(contentChanged: true);
        }
    }

    // ---------------- 内部（调用方已持锁） ----------------

    private void Insert(char c)
    {
        EnsureCapacity(_len + 1);

        /* 插入点在中间时要先把右边腾开 —— 光标回退之后再打字是常见操作
         * （"把 `;miil` 改成 `;mail`"），这里不对的话缓冲会和屏幕错位。 */
        if (_caret < _len) Array.Copy(_chars, _caret, _chars, _caret + 1, _len - _caret);
        _chars[_caret] = c;
        _caret++;
        _len++;

        /* 超出窗口就只留尾部：判定只用尾巴，留全量只会无限增长。 */
        if (_len > Capacity) DropHead(_len - Capacity);

        Bump(contentChanged: true);
    }

    private void RemoveAt(int index)
    {
        if (index < 0 || index >= _len) return;
        if (index < _len - 1) Array.Copy(_chars, index + 1, _chars, index, _len - index - 1);
        _len--;
        _chars[_len] = '\0';
    }

    private void DropHead(int count)
    {
        if (count <= 0) return;
        if (count >= _len) { _len = 0; _caret = 0; return; }

        Array.Copy(_chars, count, _chars, 0, _len - count);
        _len -= count;
        _caret = Math.Max(0, _caret - count);
    }

    private void MarkUntrustedCore()
    {
        _trusted = false;
        /* 内容没变 → 不动 <see cref="ContentGeneration"/>：
         * 这次编辑没让屏幕上的字少一个（被吞掉的键、鼠标点击都属于这类）。 */
        Bump(contentChanged: false);
    }

    private void Bump(bool contentChanged)
    {
        _gen++;
        if (contentChanged) _contentGen++;
        _lastChange = Environment.TickCount64;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _chars.Length) return;
        int size = _chars.Length;
        while (size < needed) size += Capacity;
        var grown = new char[size];
        Array.Copy(_chars, grown, _len);
        _chars = grown;
    }
}
