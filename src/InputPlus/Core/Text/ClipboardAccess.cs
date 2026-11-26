using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Text;

/// <summary>
/// 剪贴板后端：本程序所有"读/写系统剪贴板"的动作都从这里走。
///
/// ## 为什么要有这一层
///
/// `--selftest` 会真的去写系统剪贴板：先把剪贴板切成"用户复制过东西"的状态、
/// 再验证快照/还原、再模拟剪贴板管理器包装内容…… 这些**演示道具**落进的
/// 是用户真实的剪贴板。2026-11-26 的现场：
///
///   · 部署脚本每跑一次自检，用户剪贴板就被换成 `用户原内容-回归` /
///     `用户原内容-改写道具`（用例自己造的"用户原内容"），他刚复制的长文本没了；
///   · 哨兵串 `selftest-copy-slot` 还会被本进程的剪贴板监听
///     （AppHost.OnClipboardChanged）和系统 Win+V 历史各记一条 ——
///     还原只还原当前内容，还原不了历史条目，于是"总是出现"。
///
/// 光改用例不够：只要还有一处 `Clipboard.SetText` 直连系统，下一个人加用例时
/// 又会踩。所以把后端抽出来 —— **自检进程整体换成内存实现**，一个字节都不进
/// 系统剪贴板；产品进程照旧用真实现，行为一模一样（门面只是转发）。
///
/// ## 为什么不是"给每个调用点传一个 bool"
///
/// 剪贴板调用散在取词、替换选区、输入文本、宏、脚本、UI 复制按钮等几十处。
/// 挨个传参等于把"我现在是不是自检"这个进程级事实复制几十份，漏一处就是事故。
/// 静态门面只有一个真相源，换后端只有 <see cref="UseMemory"/> 一个入口。
/// </summary>
internal static class ClipboardAccess
{
    /// <summary>
    /// 当前后端。只在进程启动时（自检入口）决定一次，之后只读 ——
    /// 跑自检的过程中换后端会让用例前后读到不一致的世界。
    /// </summary>
    private static IClipboardBackend _current = new SystemClipboardBackend();

    public static IClipboardBackend Current => _current;

    /// <summary>当前是不是"不碰系统剪贴板"的内存实现。</summary>
    public static bool IsMemory => !_current.IsReal;

    /// <summary>切成内存后端：读写都只在这个进程里，系统剪贴板一点不动。</summary>
    public static void UseMemory() => _current = new MemoryClipboardBackend();

    /// <summary>切回系统剪贴板（产品运行时的默认值；自检写了 --real-clipboard 也走它）。</summary>
    public static void UseSystem() => _current = new SystemClipboardBackend();

    // ---------------- 转发 ----------------
    // 保持和 WPF Clipboard 同名同签名，调用点只需把 Clipboard.X 换成 ClipboardAccess.X。

    public static bool ContainsText() => _current.ContainsText();
    public static string GetText() => _current.GetText();
    public static void SetText(string text) => _current.SetText(text);

    public static bool ContainsImage() => _current.ContainsImage();
    public static BitmapSource? GetImage() => _current.GetImage();
    public static void SetImage(BitmapSource image) => _current.SetImage(image);

    public static bool ContainsFileDropList() => _current.ContainsFileDropList();
    public static StringCollection? GetFileDropList() => _current.GetFileDropList();
    public static void SetFileDropList(StringCollection files) => _current.SetFileDropList(files);

    public static IDataObject? GetDataObject() => _current.GetDataObject();
    public static void SetDataObject(IDataObject data, bool copy = true) => _current.SetDataObject(data, copy);
    public static void Clear() => _current.Clear();

    /// <summary>剪贴板序号：本进程或别的程序每改一次内容就 +1。</summary>
    public static uint SequenceNumber() => _current.SequenceNumber();

    /// <summary>是否有别的窗口正独占剪贴板（OpenClipboard 没释放）。</summary>
    public static bool IsOpenByOther() => _current.IsOpenByOther();
}

/// <summary>剪贴板后端。只有两个实现：真的系统剪贴板，和自检用的内存剪贴板。</summary>
internal interface IClipboardBackend
{
    /// <summary>true = 真的在动系统剪贴板。自检的护栏靠它判断"这次会不会碰到用户的东西"。</summary>
    bool IsReal { get; }

    bool ContainsText();
    string GetText();
    void SetText(string text);

    bool ContainsImage();
    BitmapSource? GetImage();
    void SetImage(BitmapSource image);

    bool ContainsFileDropList();
    StringCollection? GetFileDropList();
    void SetFileDropList(StringCollection files);

    IDataObject? GetDataObject();
    void SetDataObject(IDataObject data, bool copy);
    void Clear();

    uint SequenceNumber();
    bool IsOpenByOther();
}

/// <summary>真实现：原样转发给 WPF 的 <see cref="Clipboard"/> + 两个 user32 序号/占用查询。</summary>
internal sealed class SystemClipboardBackend : IClipboardBackend
{
    public bool IsReal => true;

    public bool ContainsText() => Clipboard.ContainsText();
    public string GetText() => Clipboard.ContainsText() ? Clipboard.GetText() : "";
    public void SetText(string text) => Clipboard.SetText(text);

    public bool ContainsImage() => Clipboard.ContainsImage();
    public BitmapSource? GetImage() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
    public void SetImage(BitmapSource image) => Clipboard.SetImage(image);

    public bool ContainsFileDropList() => Clipboard.ContainsFileDropList();
    public StringCollection? GetFileDropList() => Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList() : null;
    public void SetFileDropList(StringCollection files) => Clipboard.SetFileDropList(files);

    public IDataObject? GetDataObject() => Clipboard.GetDataObject();
    public void SetDataObject(IDataObject data, bool copy) => Clipboard.SetDataObject(data, copy: copy);
    public void Clear() => Clipboard.Clear();

    public uint SequenceNumber() => GetClipboardSequenceNumber();
    public bool IsOpenByOther() => GetOpenClipboardWindow() != IntPtr.Zero;
}

/// <summary>
/// 内存剪贴板：自检专用。行为和系统剪贴板对齐的部分只有"写进去能原样读出来、
/// 每次写序号 +1、Clear 之后内容为空" —— 用例要验证的正是这几条语义，
/// 归属、跨进程这些系统行为它们本来也验不了（那些靠真机手工验）。
///
/// 故意**不**模拟"被别的程序占用"：自检跑在内存后端上，不会再有
/// CLIPBRD_E_CANT_OPEN 这种环境噪声把用例随机刷红 —— 这也顺带解决了
/// 之前"剪贴板被占用就整条跳过"导致回归用例形同虚设的问题。
/// </summary>
internal sealed class MemoryClipboardBackend : IClipboardBackend
{
    private readonly object _gate = new();
    private DataObject? _data;
    private string? _text;
    private uint _seq = 1;

    public bool IsReal => false;

    public bool ContainsText()
    {
        lock (_gate) return _text != null;
    }

    public string GetText()
    {
        lock (_gate) return _text ?? "";
    }

    public void SetText(string text)
    {
        lock (_gate)
        {
            var data = new DataObject();
            data.SetText(text);
            _data = data;
            _text = text;
            _seq++;
        }
    }

    public bool ContainsImage()
    {
        lock (_gate) return ImageLocked() != null;
    }

    public BitmapSource? GetImage()
    {
        lock (_gate) return ImageLocked();
    }

    public void SetImage(BitmapSource image)
    {
        lock (_gate)
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, image, true);
            _data = data;
            _text = null;
            _seq++;
        }
    }

    public bool ContainsFileDropList()
    {
        lock (_gate) return FilesLocked() != null;
    }

    public StringCollection? GetFileDropList()
    {
        lock (_gate) return FilesLocked();
    }

    public void SetFileDropList(StringCollection files)
    {
        lock (_gate)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, files.Cast<string>().ToArray());
            _data = data;
            _text = null;
            _seq++;
        }
    }

    public IDataObject? GetDataObject()
    {
        lock (_gate) return _data;
    }

    public void SetDataObject(IDataObject data, bool copy)
    {
        lock (_gate)
        {
            /* 调用方拿到的可能是它自己 new 的 DataObject，也可能只是一份
             * 临时拼出来的对象。这里**复制一份格式**存下来，语义跟系统剪贴板
             * 的"复制进剪贴板"一致：之后调用方再改原对象，剪贴板里不该跟着变。 */
            var copyOf = new DataObject();
            bool any = false;
            foreach (string format in data.GetFormats(autoConvert: false))
            {
                try
                {
                    object? value = data.GetData(format, autoConvert: false);
                    if (value == null) continue;
                    if (value is MemoryStream ms) value = new MemoryStream(ms.ToArray());
                    copyOf.SetData(format, value, false);
                    any = true;
                }
                catch
                {
                    /* 单个格式取不到不影响其它格式——和 ClipboardBackup 的取舍一致 */
                }
            }

            _data = any ? copyOf : null;
            _text = any && copyOf.GetDataPresent(DataFormats.UnicodeText, false)
                ? copyOf.GetData(DataFormats.UnicodeText, false) as string
                : null;
            _seq++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _data = null;
            _text = null;
            _seq++;
        }
    }

    public uint SequenceNumber()
    {
        lock (_gate) return _seq;
    }

    /// <summary>内存后端没有别人能占住剪贴板。</summary>
    public bool IsOpenByOther() => false;

    private BitmapSource? ImageLocked()
    {
        try { return _data?.GetData(DataFormats.Bitmap, false) as BitmapSource; }
        catch { return null; }
    }

    private StringCollection? FilesLocked()
    {
        try
        {
            if (_data?.GetData(DataFormats.FileDrop, false) is not string[] files) return null;
            var list = new StringCollection();
            list.AddRange(files);
            return list;
        }
        catch { return null; }
    }
}
