using System;
using System.IO;

namespace InputPlus.Core.Windows;

/// <summary>
/// 云占位文件（OneDrive / 坚果云 / 任何走"按需下载"的同步盘）识别。
///
/// ── 为什么必须有这一道闸 ────────────────────────────────────────
/// 同步盘的"云端独有"文件在本机只是一个**占位符**：文件名、大小、时间都在，
/// 内容不在。任何一次 <c>File.ReadAllBytes</c> / <c>FileStream</c> / 缩略图解码
/// 都会让同步盘把**整个文件下载回来**（Windows 的 "recall on data access"）。
///
/// InputPlus 的正文索引（全文搜索）会遍历用户目录并抽取文件内容 ——
/// 如果它不跳过占位符，用户就会看到"我只开了个搜索，OneDrive 却在疯狂下载"。
/// 这不是 OneDrive 的问题，是我们读了不该读的文件。
///
/// ── 判据 ────────────────────────────────────────────────────────
/// 两个属性位，任一置位就当作"内容不在本地"：
///   · <see cref="FileAttributes.Offline"/> (0x1000)：文件被"释放"到云端；
///   · <see cref="FileAttributes.RecallOnDataAccess"/> (0x400000)：
///     Windows 的云文件占位符 —— **读一下就触发下载**，OneDrive 的
///     "仅在线 / 按需文件"就是这一位。
/// 读属性本身不触发下载（那是目录元数据），所以这个判断是安全的；
/// 而 <c>Length</c> / <c>LastWriteTime</c> 同理，都可以放心用。
/// </summary>
internal static class CloudPlaceholder
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS。.NET 的枚举里有，这里再写一份便于自检。</summary>
    internal const int RecallOnDataAccessFlag = 0x400000;

    /// <summary>这一组属性是不是"内容不在本地"。</summary>
    internal static bool IsPlaceholder(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | (FileAttributes)RecallOnDataAccessFlag)) != 0;

    /// <summary>
    /// 这个路径是不是云占位文件。读不到属性（文件没了 / 没权限）一律返回 false ——
    /// 后面的正常流程会自己处理"文件不存在"，这里不该替它下结论。
    /// </summary>
    internal static bool IsPlaceholder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return IsPlaceholder(File.GetAttributes(path)); }
        catch { return false; }
    }
}
