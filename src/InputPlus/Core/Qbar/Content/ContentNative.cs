using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InputPlus.Core.Qbar.Search;

namespace InputPlus.Core.Qbar.Content;

/// <summary>写进正文索引的一条文档。</summary>
internal sealed class ContentDoc
{
    [JsonPropertyName("p")] public string Path { get; set; } = "";
    /// <summary>小写、不带点的扩展名。</summary>
    [JsonPropertyName("x")] public string Ext { get; set; } = "";
    [JsonPropertyName("d")] public int IsDir { get; set; }
    [JsonPropertyName("s")] public long Size { get; set; }
    /// <summary>距 2000-01-01 的天数；-1 = 未知。</summary>
    [JsonPropertyName("t")] public int Day { get; set; } = -1;
    [JsonPropertyName("dp")] public int Depth { get; set; }
    /// <summary>抽取出来的正文（已截断到 <see cref="TextExtractor.MaxChars"/>）。</summary>
    [JsonPropertyName("c")] public string Content { get; set; } = "";
}

/// <summary>交给正文索引的查询（谓词全部下推，和文件名索引同一套思路）。</summary>
internal sealed class ContentQuery
{
    [JsonPropertyName("terms")] public List<string> Terms { get; set; } = new();
    [JsonPropertyName("phrases")] public List<string> Phrases { get; set; } = new();
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
    [JsonPropertyName("sort")] public string Sort { get; set; } = "score";
    [JsonPropertyName("desc")] public bool Desc { get; set; } = true;
}

internal sealed class ContentHit
{
    [JsonPropertyName("p")] public string Path { get; set; } = "";
    [JsonPropertyName("d")] public int IsDir { get; set; }
    [JsonPropertyName("s")] public long Size { get; set; }
    [JsonPropertyName("t")] public int Day { get; set; } = -1;
}

internal sealed class ContentSearchResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("ms")] public double Ms { get; set; }
    [JsonPropertyName("hits")] public List<ContentHit> Hits { get; set; } = new();
    [JsonPropertyName("error")] public string Error { get; set; } = "";
}

internal sealed class ContentStats
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
    [JsonPropertyName("dir")] public string Dir { get; set; } = "";
    [JsonPropertyName("docs")] public long Docs { get; set; }
    [JsonPropertyName("segments")] public int Segments { get; set; }
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("error")] public string Error { get; set; } = "";
}

/// <summary>
/// 正文索引的 P/Invoke 层（ipidx.dll 里第二套 Index）。
///
/// 与 <see cref="TantivyApi"/> 的约定完全一致：UTF-8 字节 + 显式长度、出参是
/// <see cref="IpIdxBuf"/> 并由调用方归还、所有失败都变成可判断的返回值 + LastError。
/// 唯一区别是它指向**另一个索引目录**，所以两套索引可以各自开关、各自重建。
/// </summary>
internal static class ContentNative
{
    private const string Dll = "ipidx";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string LastError { get; private set; } = "";

    internal static bool Available => TantivyApi.IsAvailable;

    internal static int SchemaVersion()
    {
        if (!Available) return -1;
        try { return NativeSchemaVersion(); }
        catch (Exception ex) { LastError = ex.Message; return -1; }
    }

    /// <summary>打开正文索引目录，0 = 成功。</summary>
    internal static int Open(string dir)
    {
        if (!Available) return -1;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(dir);
            int rc = NativeOpen(bytes, (nuint)bytes.Length);
            if (rc != 0)
            {
                LastError = TakeLastError();
                if (LastError.Length == 0) LastError = "ipidx_content_open 返回 " + rc;
                CoreLog.Warn("打开正文索引失败（" + rc + "）：" + LastError);
            }
            return rc;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CoreLog.Swallow(ex, "打开正文索引");
            return -1;
        }
    }

    internal static void Close()
    {
        if (!Available) return;
        try { NativeClose(); }
        catch (Exception ex) { CoreLog.Swallow(ex, "关闭正文索引"); }
    }

    /// <summary>追加一批文档，返回条数；失败 -1。</summary>
    internal static int AddDocs(IReadOnlyList<ContentDoc> docs)
    {
        if (!Available || docs.Count == 0) return 0;
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
            CoreLog.Swallow(ex, "写入正文文档");
            return -1;
        }
    }

    /// <summary>按路径删除，返回条数；失败 -1。</summary>
    internal static int DeletePaths(IReadOnlyList<string> paths)
    {
        if (!Available || paths.Count == 0) return 0;
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
            CoreLog.Swallow(ex, "删除正文文档");
            return -1;
        }
    }

    internal static bool Commit()
    {
        if (!Available) return false;
        try
        {
            int rc = NativeCommit();
            if (rc != 0)
            {
                LastError = TakeLastError();
                if (LastError.Length == 0) LastError = "ipidx_content_commit 返回 " + rc;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CoreLog.Swallow(ex, "提交正文索引");
            return false;
        }
    }

    internal static bool Clear()
    {
        if (!Available) return false;
        try
        {
            int rc = NativeClear();
            if (rc != 0)
            {
                LastError = TakeLastError();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CoreLog.Swallow(ex, "清空正文索引");
            return false;
        }
    }

    internal static ContentSearchResult Search(ContentQuery query, int limit)
    {
        var result = new ContentSearchResult();
        if (!Available)
        {
            result.Error = TantivyApi.StatusText;
            return result;
        }
        try
        {
            string json = JsonSerializer.Serialize(query, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            uint n = (uint)Math.Max(1, limit);
            IpIdxBuf buf = NativeSearch(bytes, (nuint)bytes.Length, n);
            string text = Utf8(buf);
            return JsonSerializer.Deserialize<ContentSearchResult>(text, JsonOptions) ?? result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            CoreLog.Swallow(ex, "查询正文索引");
            return result;
        }
    }

    internal static ContentStats Stats()
    {
        var stats = new ContentStats();
        if (!Available)
        {
            stats.Error = TantivyApi.StatusText;
            return stats;
        }
        try
        {
            string json = Utf8(NativeStats());
            return JsonSerializer.Deserialize<ContentStats>(json, JsonOptions) ?? stats;
        }
        catch (Exception ex)
        {
            stats.Error = ex.Message;
            CoreLog.Swallow(ex, "读取正文索引统计");
            return stats;
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
        try { return Utf8(NativeLastError()).Trim(); }
        catch { return ""; }
    }

    // ── 原始导入 ──

    [DllImport(Dll, EntryPoint = "ipidx_content_schema_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeSchemaVersion();

    [DllImport(Dll, EntryPoint = "ipidx_content_open", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeOpen(byte[] dir, nuint len);

    [DllImport(Dll, EntryPoint = "ipidx_content_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeClose();

    [DllImport(Dll, EntryPoint = "ipidx_content_add_docs", CallingConvention = CallingConvention.Cdecl)]
    private static extern long NativeAddDocs(byte[] json, nuint len);

    [DllImport(Dll, EntryPoint = "ipidx_content_delete_paths", CallingConvention = CallingConvention.Cdecl)]
    private static extern long NativeDeletePaths(byte[] json, nuint len);

    [DllImport(Dll, EntryPoint = "ipidx_content_commit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeCommit();

    [DllImport(Dll, EntryPoint = "ipidx_content_clear", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeClear();

    [DllImport(Dll, EntryPoint = "ipidx_content_search", CallingConvention = CallingConvention.Cdecl)]
    private static extern IpIdxBuf NativeSearch(byte[] spec, nuint len, uint limit);

    [DllImport(Dll, EntryPoint = "ipidx_content_stats", CallingConvention = CallingConvention.Cdecl)]
    private static extern IpIdxBuf NativeStats();

    [DllImport(Dll, EntryPoint = "ipidx_last_error", CallingConvention = CallingConvention.Cdecl)]
    private static extern IpIdxBuf NativeLastError();

    [DllImport(Dll, EntryPoint = "ipidx_buf_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeBufFree(IpIdxBuf buf);
}
