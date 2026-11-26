using System.Runtime.InteropServices;
using System.Windows;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Text;

/// <summary>
/// 剪贴板"先备份、之后还原"的小工具。
///
/// ## 为什么必须有它
///
/// 取词、替换选区、"算式结果写回"这些功能都要借剪贴板中转一次
/// （先 Ctrl+C 读出来，或者先 SetText 再 Shift+Insert 粘进去）。
/// 中转完必须把用户**原来**的剪贴板内容还回去，否则用户随手复制的东西
/// 就没了 —— 而且不会有任何提示。
///
/// 早先这里只记了"纯文本"：原剪贴板是截图 / 一批文件时，
/// `hadText == false`，还原分支直接 `ClipboardAccess.Clear()` ——
/// 那张截图或那批文件被**永久删掉**了。
///
/// 现在抓的是完整的 <see cref="DataObject"/>（所有格式，含位图与文件列表），
/// 还原时按原格式写回；只有确实拿不到时才退回"至少把文字还回去"。
/// </summary>
internal sealed class ClipboardBackup
{
    /// <summary>完整内容（所有格式）。null = 原本剪贴板是空的，或抓取失败。</summary>
    private DataObject? _data;

    /// <summary>原本的纯文本（完整还原失败时的保底）。</summary>
    private string? _text;

    /// <summary>原本剪贴板就是空的。</summary>
    private bool _wasEmpty;

    /// <summary>
    /// 截图快照（已在抓取时冻结）。
    ///
    /// 为什么单独存：位图在剪贴板里是"延迟渲染"的 —— 只拿到格式名/裸 DIB 流时，
    /// 原属主一旦释放，写回去经常变成"格式在、内容空"。
    /// 抓取时就把像素拷成 BitmapSource 并 Freeze，还原才能稳定。
    /// </summary>
    private System.Windows.Media.Imaging.BitmapSource? _image;

    /// <summary>抓取时出了意外 —— 这时绝不能去动剪贴板。</summary>
    private bool _failed;

    /// <summary>快照是否可用；false 时所有调用方都不应再改剪贴板。</summary>
    internal bool IsUsable => !_failed && (_wasEmpty || _data != null || _text != null || _image != null);

    private ClipboardBackup() { }

    /// <summary>
    /// 给剪贴板拍快照。**必须在 STA 线程调用**（动作队列就是 STA）。
    /// 任何异常都被吃掉：拿不到就标记失败，还原时什么都不做。
    ///
    /// **失败会重试两次（间隔 30ms）**。剪贴板是全局独占资源：剪贴板管理器、
    /// 远程桌面重定向、Excel、微信这类程序会把剪贴板**短暂**锁住，
    /// 这时 `GetDataObject` 直接抛 CLIPBRD_E_CANT_OPEN。
    ///
    /// 以前这里一次失败就返回"不可用"，调用方（取词）拿到不可用快照会
    /// **整段跳过所有模拟复制通道** —— 表现就是"偶尔按了直接搜索没反应"，
    /// 而且没有任何可见提示。短暂占用重试一下就好了，不该升级成功能失败。
    /// </summary>
    public static ClipboardBackup Capture()
    {
        ClipboardBackup b = TryCapture();
        for (int attempt = 1; attempt < CaptureAttempts && !b.IsUsable; attempt++)
        {
            Thread.Sleep(CaptureRetryDelayMs);
            b = TryCapture();
            if (b.IsUsable) CoreLog.Info($"[剪贴板] 第 {attempt + 1} 次快照成功（上一次被占用）");
        }
        return b;
    }

    /// <summary>剪贴板被占用时的重试次数。</summary>
    private const int CaptureAttempts = 3;

    /// <summary>每次重试之间的间隔（毫秒）。太短等于没重试，太长会拖慢热键响应。</summary>
    private const int CaptureRetryDelayMs = 30;

    /// <summary>真正拍一次快照。</summary>
    private static ClipboardBackup TryCapture()
    {
        var b = new ClipboardBackup();
        try
        {
            b._text = ClipboardAccess.ContainsText() ? ClipboardAccess.GetText() : null;

            IDataObject? src = ClipboardAccess.GetDataObject();
            if (src == null) { b._wasEmpty = true; return b; }

            string[] formats = src.GetFormats(autoConvert: false);
            if (formats.Length == 0) { b._wasEmpty = b._text == null; return b; }

            var copy = new DataObject();
            bool anyCopied = false;
            foreach (string format in formats)
            {
                try
                {
                    object? data = src.GetData(format, autoConvert: false);
                    if (data == null) continue;

                    /* HTML / RTF / 位图这类格式里放的常是 MemoryStream，
                     * 取出来时位置已经在末尾 —— 原样写回会变成空数据。
                     * 统一复制一份从头读的。 */
                    if (data is MemoryStream ms) data = new MemoryStream(ms.ToArray());

                    copy.SetData(format, data, false);
                    anyCopied = true;
                }
                catch
                {
                    /* 单个格式取不到不影响其它格式 */
                }
            }

            /* 位图单独物化一次，覆盖掉 copy 里的裸 DIB 项。 */
            try
            {
                if (ClipboardAccess.ContainsImage())
                {
                    var img = ClipboardAccess.GetImage();
                    if (img != null)
                    {
                        if (img.CanFreeze) img.Freeze();
                        b._image = img;
                        copy.SetData(DataFormats.Bitmap, img, true);
                        anyCopied = true;
                    }
                }
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[剪贴板] 位图快照失败：" + ex.Message);
            }

            if (!anyCopied && b._text == null)
            {
                b._failed = true;
                CoreLog.Warn("[剪贴板] 没有成功复制任何格式，本次不动剪贴板");
                return b;
            }

            b._data = anyCopied ? copy : null;
        }
        catch (Exception ex)
        {
            b._failed = true;
            CoreLog.Warn("[剪贴板] 快照失败，本次不动剪贴板：" + ex.Message);
        }
        return b;
    }

    /// <summary>
    /// 还原。
    /// </summary>
    /// <param name="currentTextGuard">
    /// 非 null 时先核对"当前剪贴板里的文字还是不是这个" —— 不是就说明
    /// 期间用户自己按了 Ctrl+C（或别的程序写了剪贴板），不能再覆盖回去。
    /// </param>
    /// <param name="expectedSequence">
    /// 还原前要求当前剪贴板序号仍等于这个值，用于识别用户在中转期间复制了新内容。
    /// </param>
    public void Restore(string? currentTextGuard = null, uint? expectedSequence = null)
    {
        if (_failed) return;

        if (expectedSequence is uint seq)
        {
            try
            {
                if (ClipboardAccess.SequenceNumber() != seq)
                {
                    CoreLog.Info("[剪贴板] 序号已变化，跳过还原，保留用户的新内容");
                    return;
                }
            }
            catch
            {
                return;
            }
        }

        if (currentTextGuard != null)
        {
            try
            {
                string current = ClipboardAccess.ContainsText() ? ClipboardAccess.GetText() : "";
                if (!string.Equals(current, currentTextGuard, StringComparison.Ordinal))
                {
                    CoreLog.Info("[剪贴板] 已被用户/其它程序改写，跳过还原");
                    return;
                }
            }
            catch
            {
                return;
            }
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_image != null)
                {
                    /* 从完整快照出发，只替换位图项；重建一个空 DataObject
                     * 会把同一份剪贴板里的文件列表 / HTML / RTF 一起丢掉。 */
                    var data = _data ?? new DataObject();
                    data.SetData(DataFormats.Bitmap, _image, true);
                    if (!string.IsNullOrEmpty(_text)) data.SetText(_text!);
                    ClipboardAccess.SetDataObject(data, copy: true);
                }
                else if (_data != null) ClipboardAccess.SetDataObject(_data, copy: true);
                else if (_text != null) ClipboardAccess.SetText(_text);
                else if (_wasEmpty && expectedSequence != null) ClipboardAccess.Clear();
                return;
            }
            catch (ExternalException) when (attempt < 2)
            {
                Thread.Sleep(25);       // 剪贴板被别的程序短暂占用，正常现象
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[剪贴板] 还原失败：" + ex.Message);
                /* 完整还原失败时至少把文字还回去 —— 但只在原本**有**文字时做，
                 * 原本没有文字就保持现状，绝不清空（清空就是把内容弄丢了）。 */
                if (!string.IsNullOrEmpty(_text))
                {
                    try { ClipboardAccess.SetText(_text); } catch { }   /* 预期内：清理动作，失败无副作用 */
                }
                return;
            }
        }
    }

}
