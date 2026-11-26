using System.Windows;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Text;

/// <summary>
/// 剪贴板"租用"：进入时拍快照，离开时自动还原。
/// 内部记着"我们最后写进去的是什么"，Dispose 用它做守卫 ——
/// 期间用户或别的程序写过剪贴板，就放弃还原，保留人家的内容。
///
/// 存在理由：SelfTest 里"先写、再还原"曾经靠人手工写 finally，
/// 2026-11-23 就漏了一次 —— 哨兵串被永久留在用户剪贴板上，
/// 把用户刚剪切的一整段长文本顶掉了。
///
/// 为什么不是"再补一个 finally"：漏的根因是**还原责任散落在方法体各处**，
/// 谁都能写剪贴板、谁都可能忘记还。租约把"拍快照 / 记账 / 还原"收进一个对象，
/// 只要写操作走它，还原就不可能被漏掉；而内容是"被测对象"自己写进去的那几处，
/// 用 <see cref="NoteOwnWrite"/> 记账即可，不必把被测代码也搬进租约。
/// </summary>
internal sealed class ClipboardLease : IDisposable
{
    private readonly ClipboardBackup _backup = ClipboardBackup.Capture();

    /// <summary>我们最后一次写进去的文字；null = 写的是非文字格式（位图 / 文件列表）。</summary>
    private string? _lastText;

    /// <summary>最后一次写完之后的剪贴板序号，非文字格式用它做守卫。</summary>
    private uint _seqAfterWrite;

    private bool _disposed;

    /// <summary>快照不可用时一律不许动剪贴板（见 ClipboardBackup.IsUsable 的约定）。</summary>
    public bool IsUsable => _backup.IsUsable;

    /// <summary>写文字。返回 false = 剪贴板被占用或快照不可用，调用方按"环境限制"跳过。</summary>
    public bool WriteText(string text)
    {
        if (!_backup.IsUsable) return false;
        if (!Core.Input.ClipboardPaster.TrySetText(text)) return false;
        _lastText = text;
        _seqAfterWrite = Seq();
        return true;
    }

    /// <summary>写非文字格式（位图 / 文件列表）。这类没法用文字守卫，改用序号守卫。</summary>
    public bool WriteData(Action<DataObject> fill, int attempts = 6)
    {
        if (!_backup.IsUsable) return false;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                var data = new DataObject();
                fill(data);
                ClipboardAccess.SetDataObject(data, copy: true);
                _lastText = null;
                _seqAfterWrite = Seq();
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(40); }
            catch { return false; }
        }
        return false;
    }

    /// <summary>
    /// 记账：**被测代码**刚刚自己往剪贴板里写了一段文字，内容与 <paramref name="text"/> 一致。
    ///
    /// 为什么需要它：自检最后那段测的是 `ClipboardService.CopyTextToSystem`
    /// （走它自己的写路径，不经租约）。不记账的话，Dispose 会拿"最后经租约写的内容"
    /// 去比对当前剪贴板，发现不一致 → 判定"用户插了一手" → 放弃还原 ——
    /// 于是哨兵串 `selftest-copy-slot` 被永久留在用户剪贴板上，正是要修的那个事故。
    ///
    /// 只在**我们确实观察到剪贴板里就是这段文字**时调用；没写成功 / 被别人改写时
    /// 不要调用，否则等于把"别人的内容"冒认成自己的，Dispose 就会去覆盖它。
    /// </summary>
    public void NoteOwnWrite(string text)
    {
        _lastText = text;
        _seqAfterWrite = Seq();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        /* 先看"这块剪贴板还是不是我们的"：
         *   ① 文字相等 —— 最理想；
         *   ② 当前文字**包含**我们最后写的那段 —— 剪贴板管理器给内容套了 HTML、
         *      引号或前后缀时，整串文字变了，但我们的内容还在里面。实测里
         *      "偶发没还原"主要就是这一种：守卫要求逐字相等，一被改写就放弃。
         * 只认长度 ≥ 4 的整段，避免把用户随手复制的短词误判成自己的。
         * 都不是，才退回原来的内容 / 序号守卫（宁可不动人家的内容）。 */
        string ours = _lastText ?? "";
        if (ours.Length >= 4 && LooksLikeOurs(SafeGetText(), ours))
        {
            _backup.Restore();
            return;
        }

        /* 文字用内容守卫最稳（剪贴板管理器把内容重写成带 HTML 的版本时，
         * 文字仍然相等，还原照样生效）；图/文件只能用序号守卫。 */
        if (_lastText != null) _backup.Restore(currentTextGuard: _lastText);
        else _backup.Restore(expectedSequence: _seqAfterWrite);
    }

    /// <summary>当前剪贴板的文字；读不到（被占用 / 没有文字）返回空串。</summary>
    private static string SafeGetText()
    {
        try { return ClipboardAccess.ContainsText() ? ClipboardAccess.GetText() : ""; }
        catch { return ""; }
    }

    /// <summary>当前文字是不是"我们写的，或我们写的被包装过"。</summary>
    private static bool LooksLikeOurs(string current, string ours)
        => string.Equals(current, ours, StringComparison.Ordinal)
           || (current.Length > ours.Length && current.Contains(ours, StringComparison.Ordinal));

    private static uint Seq()
    {
        try { return ClipboardAccess.SequenceNumber(); } catch { return 0; }
    }
}
