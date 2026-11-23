using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InputPlus.Core.Qbar.Search;

/// <summary>一个由 ipidx.dll 分配的缓冲：UTF-8 字节 + 字节数。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IpIdxBuf
{
    public IntPtr Ptr;
    public nuint Len;
}

/// <summary>索引层回的一条命中。字段名短是因为它要跨 FFI 走 JSON，每行几十万条。</summary>
internal sealed class IndexHit
{
    [JsonPropertyName("p")] public string Path { get; set; } = "";
    [JsonPropertyName("d")] public int IsDir { get; set; }
    [JsonPropertyName("s")] public long Size { get; set; }
    [JsonPropertyName("t")] public int Day { get; set; } = -1;
    /// <summary>1 = 拼音命中。</summary>
    [JsonPropertyName("py")] public int Pinyin { get; set; }
    /// <summary>0 正常 / 1 子序列候选 / 2 编辑距离近似。</summary>
    [JsonPropertyName("fz")] public int Fuzzy { get; set; }
}

/// <summary>索引侧直出的一条分面计数（标签 + 条数，两者来自同一次查询）。</summary>
internal sealed class IndexFacet
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("count")] public long Count { get; set; }
}

internal sealed class IndexSearchResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("ms")] public double Ms { get; set; }
    [JsonPropertyName("hits")] public List<IndexHit> Hits { get; set; } = new();
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    /// <summary>分面直出：扩展名各桶 + file/dir + fresh（与 hits 同一批文档）。</summary>
    [JsonPropertyName("facets")] public List<IndexFacet> Facets { get; set; } = new();
}

internal sealed class IndexStats
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
    [JsonPropertyName("dir")] public string Dir { get; set; } = "";
    [JsonPropertyName("docs")] public long Docs { get; set; }
    [JsonPropertyName("segments")] public int Segments { get; set; }
    [JsonPropertyName("files")] public long Files { get; set; }
    [JsonPropertyName("dirs")] public long Dirs { get; set; }
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("error")] public string Error { get; set; } = "";
}

/// <summary>索引层写入的一条文档。</summary>
internal sealed class IndexDoc
{
    [JsonPropertyName("p")] public string Path { get; set; } = "";
    [JsonPropertyName("d")] public int IsDir { get; set; }
    [JsonPropertyName("s")] public long Size { get; set; }
    [JsonPropertyName("t")] public int Day { get; set; } = -1;
    [JsonPropertyName("dp")] public int Depth { get; set; }
    [JsonPropertyName("py")] public string Pinyin { get; set; } = "";
    [JsonPropertyName("pi")] public string PinyinInitials { get; set; } = "";
}

/// <summary>
/// 交给索引层的查询。字段与 <see cref="QbarSearchSpec"/> 一一对应，
/// 只是换成短名字 + 数组形式（跨 FFI 的 JSON 越小越好）。
/// </summary>
internal sealed class IndexQuery
{
    [JsonPropertyName("terms")] public List<string> Terms { get; set; } = new();
    [JsonPropertyName("phrases")] public List<string> Phrases { get; set; } = new();
    [JsonPropertyName("neg")] public List<string> Neg { get; set; } = new();
    [JsonPropertyName("ext")] public List<string> Ext { get; set; } = new();
    [JsonPropertyName("negext")] public List<string> NegExt { get; set; } = new();
    [JsonPropertyName("path")] public List<string> Path { get; set; } = new();
    [JsonPropertyName("negpath")] public List<string> NegPath { get; set; } = new();
    [JsonPropertyName("minsize")] public long MinSize { get; set; } = -1;
    [JsonPropertyName("maxsize")] public long MaxSize { get; set; } = -1;
    [JsonPropertyName("minday")] public int MinDay { get; set; } = -1;
    [JsonPropertyName("maxday")] public int MaxDay { get; set; } = -1;
    [JsonPropertyName("files")] public bool Files { get; set; }
    [JsonPropertyName("dirs")] public bool Dirs { get; set; }
    [JsonPropertyName("glob")] public string Glob { get; set; } = "";
    [JsonPropertyName("regex")] public string Regex { get; set; } = "";
    /// <summary>"近 7 天"分面的起点（自 2000-01-01 的天数）；-1 = 不算这一桶。</summary>
    [JsonPropertyName("freshday")] public int FreshDay { get; set; } = -1;
    /// <summary>要不要收集分面（默认 true；A/B 测分面开销时关掉）。</summary>
    [JsonPropertyName("facets")] public bool Facets { get; set; } = true;
    [JsonPropertyName("sort")] public string Sort { get; set; } = "score";
    [JsonPropertyName("desc")] public bool Desc { get; set; } = true;
}

/// <summary>
/// ipidx.dll 的 P/Invoke 层。
///
/// 三条约定（见 TANTIVY-MIGRATION.md）：
///   一、跨边界字符串一律 UTF-8 <c>byte[]</c> + 显式长度。不用 string，不用
///       <c>MarshalAs(LPStr)</c> —— Windows 的 ANSI 代码页会把中文路径变成问号，
///       而这个程序的主要用户就是中文路径。
///   二、出参是 <see cref="IpIdxBuf"/>，谁分配谁释放：拿到就 <see cref="FreeBuf"/>。
///   三、dll 不在（没随发布产物走 / 被杀软删了）时**不抛异常**，只是
///       <see cref="IsAvailable"/> 为假、<see cref="StatusText"/> 说明原因 ——
///       搜索功能退化成"索引不可用"，而不是让整个输入法崩掉。
/// </summary>
internal static class TantivyApi
{
    private const string Dll = "ipidx";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static bool _probed;
    private static bool _available;
    private static string _status = "未探测";

    /// <summary>dll 能不能用。第一次读会真的 ping 一次（不是只看文件在不在）。</summary>
    internal static bool IsAvailable
    {
        get { Probe(); return _available; }
    }

    /// <summary>dll 状态的一句话说明（设置页 / --index-status / 自检都用它）。</summary>
    internal static string StatusText
    {
        get { Probe(); return _status; }
    }

    internal static string LastError { get; private set; } = "";

    /// <summary>当前打开着的索引目录（空 = 没打开）。自检与诊断拿它定位段文件。</summary>
    internal static string CurrentDir { get; private set; } = "";

    /// <summary>
    /// 打开索引并**返回原始返回码**（0 = 成功）。
    ///
    /// 为什么要露返回码：`ipidx_open` 已经把失败原因分成了几档，而"索引坏了"与
    /// "另一个进程占着索引"需要完全相反的处理 —— 前者该清空重建，后者清空就是删用户的索引。
    ///   -4 = 目录/元数据打不开（段文件缺失、meta 引用了不存在的段）
    ///   -6 = 创建写入器失败（锁被占、权限不足）
    ///   -7 = 创建读取器失败（段文件缺失最常见的那一类）
    /// </summary>
    internal static int OpenEx(string dir)
    {
        CurrentDir = "";
        if (!IsAvailable) return -1;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(dir);
            int rc = NativeOpen(bytes, (nuint)bytes.Length);
            if (rc != 0)
            {
                LastError = TakeLastError();
                if (LastError.Length == 0) LastError = "ipidx_open 返回 " + rc;
                CoreLog.Warn("打开文件索引失败（" + rc + "）：" + LastError);
                return rc;
            }
            CurrentDir = dir;
            return 0;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CoreLog.Swallow(ex, "打开文件索引");
            return -1;
        }
    }

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        try
        {
            string text = Utf8(NativePing());
            _available = text.StartsWith("ipidx/", StringComparison.Ordinal);
            _status = _available ? text : ("ipidx.dll 应答异常：" + text);
        }
        catch (DllNotFoundException)
        {
            _available = false;
            _status = "找不到 ipidx.dll（随程序发布的原生索引库）";
        }
        catch (EntryPointNotFoundException ex)
        {
            _available = false;
            _status = "ipidx.dll 版本不匹配：" + ex.Message;
        }
        catch (BadImageFormatException ex)
        {
            _available = false;
            _status = "ipidx.dll 位数不对（需要 x64）：" + ex.Message;
        }
        catch (Exception ex)
        {
            _available = false;
            _status = "加载 ipidx.dll 失败：" + ex.Message;
        }
    }

    /// <summary>重新探测一次（自检里"把 dll 拷进来再看"这种场景用）。</summary>
    internal static void ResetProbe()
    {
        _probed = false;
        _available = false;
        _status = "未探测";
    }

    // ── 高层封装：每个都吞掉异常并返回可判断的失败值 ──

    internal static bool Open(string dir) => OpenEx(dir) == 0;

    internal static void Close()
    {
        CurrentDir = "";
        if (!IsAvailable) return;
        try { NativeClose(); }
        catch (Exception ex) { CoreLog.Swallow(ex, "关闭文件索引"); }
    }

    /// <summary>追加一批文档。返回写入条数，失败返回 -1。</summary>
    internal static int AddDocs(IReadOnlyList<IndexDoc> docs)
    {
        if (!IsAvailable || docs.Count == 0) return 0;
        try
        {
            string json = JsonSerializer.Serialize(docs, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            long n = NativeAddDocs(bytes, (nuint)bytes.Length);
            if (n < 0)
            {
                LastError = TakeLastError();
                return -1;
            }
            return (int)n;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CoreLog.Swallow(ex, "写入索引文档");
            return -1;
        }
    }

    /// <summary>
    /// 二进制入口：把一批文档按固定布局写进去（建索引的热路径走这条）。
    ///
    /// 布局（全部小端，见 docs/qbar/TANTIVY-MIGRATION.md §4）：
    /// <code>
    /// u32 magic = 0x49504458 ("IPDX")
    /// u32 count
    /// count × 48 字节定长头：
    ///   u32 path_off, u32 path_len
    ///   u8  is_dir, u8 pad[3]
    ///   u64 size
    ///   i64 day
    ///   u32 depth
    ///   u32 pinyin_off, u32 pinyin_len
    ///   u32 initials_off, u32 initials_len
    /// u32 pool_len
    /// pool_len 字节 UTF-8 字符串池
    /// </code>
    ///
    /// 为什么不用 `Marshal` / 结构体封送：那需要 `Marshal.StructureToPtr` + 手工字符串指针，
    /// 中文路径一旦走了 ANSI 封送就变成问号。这里全程托管字节缓冲，跨边界只是一段 UTF-8。
    /// 返回写入条数，失败 -1。
    /// </summary>
    internal static int AddDocsBinary(IReadOnlyList<IndexDoc> docs)
    {
        if (!IsAvailable || docs.Count == 0) return 0;
        try
        {
            byte[] bytes = BuildBinaryPayload(docs);
            long n = NativeAddDocsBin(bytes, (nuint)bytes.Length);
            if (n < 0)
            {
                LastError = TakeLastError();
                return -1;
            }
            return (int)n;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CoreLog.Swallow(ex, "写入索引文档（二进制）");
            return -1;
        }
    }

    /// <summary>二进制入参的条数上限里的常量：与 Rust 侧 <c>BIN_DOC_HEADER</c> 必须一致。</summary>
    internal const int BinDocHeaderBytes = 48;

    private const uint BinMagic = 0x49504458;

    /// <summary>
    /// 拼一段二进制入参。
    ///
    /// 写法说明：任务书写的是 `ArrayBufferWriter&lt;byte&gt; + BinaryWriter`，但 `BinaryWriter`
    /// 只接受 `Stream` —— 包一层适配器等于每次批次多拷一份 2 MB 的载荷。这里用
    /// `ArrayBufferWriter&lt;byte&gt;` + `BinaryPrimitives` 直接写目标缓冲（同样是托管字节，
    /// 同样不碰 `Marshal`），去掉了那次多余的整段拷贝。见 DECISIONS D68。
    /// </summary>
    private static byte[] BuildBinaryPayload(IReadOnlyList<IndexDoc> docs)
    {
        /* 字符串池单独攒：定长头必须全部排在池前面，而池的偏移在写头的时候就要求已知。
         * 空串（大多数文件没有拼音）统一指到池首的 0 长度段，不为它们各写一次。 */
        var pool = new ArrayBufferWriter<byte>(docs.Count * 16);
        var buffer = new ArrayBufferWriter<byte>(8 + docs.Count * BinDocHeaderBytes + 4 + docs.Count * 64);

        Span<byte> head = buffer.GetSpan(8);
        BinaryPrimitives.WriteUInt32LittleEndian(head, BinMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)docs.Count);
        buffer.Advance(8);

        foreach (IndexDoc doc in docs)
        {
            (uint pathOff, uint pathLen) = AppendPooled(pool, doc.Path);
            (uint pyOff, uint pyLen) = AppendPooled(pool, doc.Pinyin);
            (uint piOff, uint piLen) = AppendPooled(pool, doc.PinyinInitials);

            Span<byte> h = buffer.GetSpan(BinDocHeaderBytes);
            h[..BinDocHeaderBytes].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(h, pathOff);
            BinaryPrimitives.WriteUInt32LittleEndian(h[4..], pathLen);
            h[8] = (byte)(doc.IsDir == 1 ? 1 : 0);
            BinaryPrimitives.WriteUInt64LittleEndian(h[12..], (ulong)Math.Max(0, doc.Size));
            BinaryPrimitives.WriteInt64LittleEndian(h[20..], doc.Day);
            BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)Math.Max(0, doc.Depth));
            BinaryPrimitives.WriteUInt32LittleEndian(h[32..], pyOff);
            BinaryPrimitives.WriteUInt32LittleEndian(h[36..], pyLen);
            BinaryPrimitives.WriteUInt32LittleEndian(h[40..], piOff);
            BinaryPrimitives.WriteUInt32LittleEndian(h[44..], piLen);
            buffer.Advance(BinDocHeaderBytes);
        }

        Span<byte> tail = buffer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(tail, (uint)pool.WrittenCount);
        buffer.Advance(4);
        pool.WrittenSpan.CopyTo(buffer.GetSpan(pool.WrittenCount));
        buffer.Advance(pool.WrittenCount);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>把一段字符串追加到池里，返回（相对池首偏移，字节数）。空串不占空间。</summary>
    private static (uint Offset, uint Length) AppendPooled(ArrayBufferWriter<byte> pool, string? text)
    {
        if (string.IsNullOrEmpty(text)) return (0u, 0u);
        int bytes = Encoding.UTF8.GetByteCount(text);
        uint offset = (uint)pool.WrittenCount;
        Encoding.UTF8.GetBytes(text, pool.GetSpan(bytes));
        pool.Advance(bytes);
        return (offset, (uint)bytes);
    }

    /// <summary>按路径删除（大小写不敏感）。返回条数，失败返回 -1。</summary>
    internal static int DeletePaths(IReadOnlyList<string> paths)
    {
        if (!IsAvailable || paths.Count == 0) return 0;
        try
        {
            string json = JsonSerializer.Serialize(paths, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            long n = NativeDeletePaths(bytes, (nuint)bytes.Length);
            if (n < 0)
            {
                LastError = TakeLastError();
                return -1;
            }
            return (int)n;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CoreLog.Swallow(ex, "删除索引文档");
            return -1;
        }
    }

    /// <summary>提交。失败时调用方必须把这一批文档重新 add 一遍（commit 之后 writer 里就没有它们了）。</summary>
    internal static bool Commit()
    {
        if (!IsAvailable) return false;
        try
        {
            int rc = NativeCommit();
            if (rc != 0)
            {
                LastError = TakeLastError();
                if (LastError.Length == 0) LastError = "ipidx_commit 返回 " + rc;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CoreLog.Swallow(ex, "提交索引");
            return false;
        }
    }

    internal static IndexSearchResult Search(IndexQuery query, int limit)
        => ReadSearch(0, Serialize(query), limit, "查询索引");

    /// <summary>拼音专用检索（只匹配 pinyin / pinyin_initials，其余谓词照样下推）。</summary>
    internal static IndexSearchResult PinyinSearch(IndexQuery query, int limit)
        => ReadSearch(1, Serialize(query), limit, "拼音检索");

    /// <summary>模糊兜底：子序列候选集 + 编辑距离 1 的近似命中。</summary>
    internal static IndexSearchResult FuzzySearch(IndexQuery query, int limit)
        => ReadSearch(2, Serialize(query), limit, "模糊检索");

    internal static IndexStats Stats()
    {
        var stats = new IndexStats();
        if (!IsAvailable)
        {
            stats.Error = StatusText;
            return stats;
        }
        try
        {
            string json = Utf8(NativeStats());
            return JsonSerializer.Deserialize<IndexStats>(json, JsonOptions) ?? new IndexStats();
        }
        catch (Exception ex)
        {
            stats.Error = ex.Message;
            CoreLog.Swallow(ex, "读取索引统计");
            return stats;
        }
    }

    private static string Serialize(IndexQuery query)
        => JsonSerializer.Serialize(query, JsonOptions);

    /// <summary>统一读回：0 = 主查询 / 1 = 拼音 / 2 = 模糊。</summary>
    private static IndexSearchResult ReadSearch(int kind, string json, int limit, string what)
    {
        var result = new IndexSearchResult();
        if (!IsAvailable)
        {
            result.Error = StatusText;
            return result;
        }
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            uint n = (uint)Math.Max(1, limit);
            IpIdxBuf buf = kind switch
            {
                1 => NativePinyinSearch(bytes, (nuint)bytes.Length, n),
                2 => NativeFuzzySearch(bytes, (nuint)bytes.Length, n),
                _ => NativeSearch(bytes, (nuint)bytes.Length, n),
            };
            string text = Utf8(buf);
            var parsed = JsonSerializer.Deserialize<IndexSearchResult>(text, JsonOptions);
            if (parsed == null)
            {
                result.Error = "索引返回了空结果";
                return result;
            }
            return parsed;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            CoreLog.Swallow(ex, what);
            return result;
        }
    }

    private static string Utf8(IpIdxBuf buf)
    {
        try
        {
            if (buf.Ptr == IntPtr.Zero || buf.Len == 0) return "";
            var bytes = new byte[(int)buf.Len];
            Marshal.Copy(buf.Ptr, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            if (buf.Ptr != IntPtr.Zero && buf.Len > 0) NativeBufFree(buf);
        }
    }

    private static string TakeLastError()
    {
        try
        {
            return Utf8(NativeLastError()).Trim();
        }
        catch { return ""; }
    }

    // ── 原始导入 ──

    [DllImport(Dll, EntryPoint = "ipidx_ping", CallingConvention = CallingConvention.Cdecl)]
    private static extern IpIdxBuf NativePing();

    [DllImport(Dll, EntryPoint = "ipidx_open", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeOpen(byte[] dir, nuint len);

    [DllImport(Dll, EntryPoint = "ipidx_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeClose();

    [DllImport(Dll, EntryPoint = "ipidx_add_docs", CallingConvention = CallingConvention.Cdecl)]
    private static extern long NativeAddDocs(byte[] json, nuint len);

    [DllImport(Dll, EntryPoint = "ipidx_add_docs_bin", CallingConvention = CallingConvention.Cdecl)]
    private static extern long NativeAddDocsBin(byte[] payload, nuint len);

    [DllImport(Dll, EntryPoint = "ipidx_delete_paths", CallingConvention = CallingConvention.Cdecl)]
    private static extern long NativeDeletePaths(byte[] json, nuint len);

    [DllImport(Dll, EntryPoint = "ipidx_commit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeCommit();

    [DllImport(Dll, EntryPoint = "ipidx_search", CallingConvention = CallingConvention.Cdecl)]
    private static extern IpIdxBuf NativeSearch(byte[] spec, nuint len, uint limit);

    [DllImport(Dll, EntryPoint = "ipidx_pinyin_search", CallingConvention = CallingConvention.Cdecl)]
    private static extern IpIdxBuf NativePinyinSearch(byte[] spec, nuint len, uint limit);

    [DllImport(Dll, EntryPoint = "ipidx_fuzzy_search", CallingConvention = CallingConvention.Cdecl)]
    private static extern IpIdxBuf NativeFuzzySearch(byte[] spec, nuint len, uint limit);

    [DllImport(Dll, EntryPoint = "ipidx_stats", CallingConvention = CallingConvention.Cdecl)]
    private static extern IpIdxBuf NativeStats();

    [DllImport(Dll, EntryPoint = "ipidx_last_error", CallingConvention = CallingConvention.Cdecl)]
    private static extern IpIdxBuf NativeLastError();

    [DllImport(Dll, EntryPoint = "ipidx_buf_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeBufFree(IpIdxBuf buf);
}
