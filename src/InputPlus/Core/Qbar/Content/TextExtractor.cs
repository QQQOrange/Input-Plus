using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace InputPlus.Core.Qbar.Content;

/// <summary>
/// 正文抽取：把"文件"变成"可索引的文本"。
///
/// 支持面按**能不能离线、准不准**挑的：
///   · docx / xlsx / pptx：Office 的 OPC 就是 zip + xml，用 <see cref="ZipArchive"/> +
///     <see cref="XmlReader"/> 直接取文本节点 —— 不引第三方库，也不会因为用户没装
///     Office / WPS 就抽不出字（Windows 自带的 IFilter 恰恰依赖装没装）；
///   · pdf：PdfPig（纯托管、MIT、无原生依赖）。加密/损坏/扫描件会失败，失败计数会
///     显示在设置页，不假装成功；
///   · 纯文本家族：按 BOM → UTF-8 严格 → GB18030 的顺序解码（中文 txt 大量是 GBK）。
///
/// 三条硬约束：
///   一、**永不抛**：任何一个文件抽失败都只是这一条不索引，不能带崩扫描线程；
///   二、**有限**：单文件字节数、PDF 页数、总字符数都有上限，防一个 2 GB 的日志把进程拖死；
///   三、**可解释**：失败原因用短码返回（unsupported / too-large / empty / encrypted …），
///       设置页能看到"失败 N 个，最多的是 pdf 加密"。
/// </summary>
internal static class TextExtractor
{
    /// <summary>单条文档最多索引多少字符（2-3 gram 的倒排按字符数线性涨）。</summary>
    internal const int MaxChars = 64_000;

    /// <summary>纯文本家族一次最多读多少字节（超出截断，不跳过）。</summary>
    internal const long MaxReadBytes = 8L * 1024 * 1024;

    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf",
        "docx", "docm", "dotx",
        "xlsx", "xlsm", "xltx",
        "pptx", "pptm",
        "txt", "text", "md", "markdown", "log", "rst", "tex",
        "json", "xml", "csv", "tsv", "ini", "cfg", "conf", "toml", "yaml", "yml", "properties",
        "html", "htm", "xhtml",
        "cs", "java", "js", "mjs", "ts", "tsx", "jsx", "py", "rs", "go", "c", "cc", "cpp", "h", "hpp",
        "css", "scss", "less", "sql", "sh", "bash", "ps1", "bat", "cmd", "lua", "vb", "php", "rb",
    };

    /// <summary>这个扩展名有没有抽取器。</summary>
    internal static bool Supports(string ext) => !string.IsNullOrEmpty(ext) && Supported.Contains(ext);

    /// <summary>默认白名单（设置页「正文索引」的出厂值）。</summary>
    internal static readonly string[] DefaultExtensions =
    {
        "pdf", "docx", "xlsx", "pptx", "txt", "md", "html", "htm", "csv", "json", "log",
    };

    internal static bool TryExtract(string path, string ext, long maxBytes, int maxPdfPages,
                                    out string text, out string reason)
    {
        text = "";
        reason = "";
        try
        {
            if (!Supports(ext)) { reason = "unsupported"; return false; }

            var info = new FileInfo(path);
            if (!info.Exists) { reason = "missing"; return false; }
            if (info.Length <= 0) { reason = "empty-file"; return false; }

            switch (ext.ToLowerInvariant())
            {
                case "docx":
                case "docm":
                case "dotx":
                    text = FromOpc(path, "word/document.xml");
                    break;
                case "xlsx":
                case "xlsm":
                case "xltx":
                    text = FromXlsx(path);
                    break;
                case "pptx":
                case "pptm":
                    text = FromPptx(path);
                    break;
                case "pdf":
                    text = FromPdf(path, maxPdfPages);
                    break;
                case "html":
                case "htm":
                case "xhtml":
                    text = StripHtml(ReadTextHead(path, Math.Min(info.Length, maxBytes)));
                    break;
                default:
                    text = ReadTextHead(path, Math.Min(info.Length, maxBytes));
                    break;
            }

            text = Clean(text);
            if (text.Length == 0) { reason = "empty"; return false; }
            if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
            return true;
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase))
        {
            // 加密的 PDF/Office：最常见的一类失败，单独标出来（设置页能看到计数）
            reason = "encrypted";
            return false;
        }
        catch (InvalidDataException) { reason = "corrupt"; return false; }
        catch (UnauthorizedAccessException) { reason = "denied"; return false; }
        catch (IOException) { reason = "io"; return false; }
        catch (Exception ex)
        {
            reason = "error:" + ex.GetType().Name;
            return false;
        }
    }

    // ─────────────────────────── 纯文本 ───────────────────────────

    private static string ReadTextHead(string path, long take)
    {
        if (take <= 0) return "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        int n = (int)Math.Min(take <= 0 ? MaxReadBytes : take, MaxReadBytes);
        var buf = new byte[n];
        int read = 0;
        while (read < n)
        {
            int got = fs.Read(buf, read, n - read);
            if (got <= 0) break;
            read += got;
        }
        if (read == 0) return "";

        // BOM 优先
        if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
            return Encoding.UTF8.GetString(buf, 3, read - 3);
        if (read >= 2 && buf[0] == 0xFF && buf[1] == 0xFE)
            return Encoding.Unicode.GetString(buf, 2, read - 2);
        if (read >= 2 && buf[0] == 0xFE && buf[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(buf, 2, read - 2);

        // 有 NUL 的基本是二进制（把 .cs 之外的东西误判成文本时挡一下）
        int nul = 0;
        for (int i = 0; i < read; i++) if (buf[i] == 0 && ++nul > 4) return "";

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buf, 0, read);
        }
        catch (DecoderFallbackException)
        {
            /* 中文 txt / 老 csv 大量是 GBK。GB18030 是它的超集，
             * 解不出来也不会抛（非法字节会变成替换字符）。 */
            return Gb18030.GetString(buf, 0, read);
        }
    }

    private static Encoding? _gb18030;

    private static Encoding Gb18030
    {
        get
        {
            if (_gb18030 != null) return _gb18030;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _gb18030 = Encoding.GetEncoding(54936, EncoderFallback.ReplacementFallback,
                                                DecoderFallback.ReplacementFallback);
            }
            catch
            {
                // 系统连代码页都没有时退回 Latin1 至少不丢结构（只会乱码，不会抛）
                _gb18030 = Encoding.Latin1;
            }
            return _gb18030;
        }
    }

    // ─────────────────────────── OPC（docx/xlsx/pptx） ───────────────────────────

    private static string FromOpc(string path, string entryName)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(entryName);
        if (entry == null) return "";
        using var stream = entry.Open();
        return XmlText(stream);
    }

    private static string FromXlsx(string path)
    {
        var sb = new StringBuilder(4096);
        using var zip = ZipFile.OpenRead(path);

        // 共享字符串表：单元格里的文字都在这里
        var shared = zip.GetEntry("xl/sharedStrings.xml");
        if (shared != null)
        {
            using var s = shared.Open();
            sb.Append(XmlText(s)).Append(' ');
        }

        // 内联字符串/数字：直接扫每张表里的 <t>/<v> 文本节点
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            using var s = entry.Open();
            sb.Append(XmlText(s)).Append(' ');
        }
        return sb.ToString();
    }

    private static string FromPptx(string path)
    {
        var sb = new StringBuilder(4096);
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("ppt/slides/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            using var s = entry.Open();
            sb.Append(XmlText(s)).Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>从一个 xml 流里取全部文本节点（忽略标签结构，对正文索引足够）。</summary>
    private static string XmlText(Stream stream)
    {
        var sb = new StringBuilder(2048);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = false,
            CloseInput = false,
        });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                sb.Append(reader.Value).Append(' ');
            if (sb.Length > MaxChars * 4) break;   // 先兜一个上限，后面还会截
        }
        return sb.ToString();
    }

    // ─────────────────────────── PDF ───────────────────────────

    private static string FromPdf(string path, int maxPages)
    {
        if (maxPages <= 0) maxPages = 50;
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        var sb = new StringBuilder(4096);
        int page = 0;
        foreach (var p in doc.GetPages())
        {
            if (++page > maxPages) break;
            sb.Append(p.Text).Append(' ');
            if (sb.Length > MaxChars * 2) break;
        }
        return sb.ToString();
    }

    // ─────────────────────────── 清洗 ───────────────────────────

    private static readonly Regex ScriptStyle = new(
        @"<(script|style)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);

    private static string StripHtml(string html)
    {
        if (html.Length == 0) return "";
        string noScript = ScriptStyle.Replace(html, " ");
        return Tags.Replace(noScript, " ");
    }

    /// <summary>压空白、去控制字符：倒排索引不需要它们，留着只会让摘要难读。</summary>
    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        bool lastSpace = false;
        foreach (char c in text)
        {
            if (c == '\uFFFD') continue;
            if (char.IsControl(c))
            {
                if (c is '\n' or '\r' or '\t') { if (!lastSpace) { sb.Append(' '); lastSpace = true; } }
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) { sb.Append(' '); lastSpace = true; }
                continue;
            }
            sb.Append(c);
            lastSpace = false;
        }
        return sb.ToString().Trim();
    }
}
