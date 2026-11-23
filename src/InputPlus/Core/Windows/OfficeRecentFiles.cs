using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace InputPlus.Core.Windows;

/// <summary>
/// 「最近打开的文件」里 WPS / Office 自己记的那一份（v1.5）。
///
/// 为什么要它：WPS 打开一个文档时，句柄可能是云缓存副本、命令行里也可能没有路径
/// （从开始菜单启动、再在程序里打开），系统的「最近使用的文件」里也不一定有。
/// 但 WPS 自己一定记得 —— 它就写在这儿：
/// <code>
/// HKCU\Software\Kingsoft\Office\6.0\&lt;app&gt;\RecentFiles\files\&lt;n&gt;\path = C:\...\书.xlsx
/// </code>
/// 实测（本机 WPS 12.1）：窗口标题 `徒手健身动作全解….xlsx - WPS Office` 对应的真实路径
/// 就在 `et\RecentFiles\files\3` 里，而它的句柄只指向 `…\cachedata\&lt;hash&gt;\…xlsx`。
///
/// 另外顺手读一下 Microsoft Office 的 `File MRU`（值形如
/// <c>[F00000000][T01DA…][O00000000]*C:\a\b.docx</c>，取最后一个 <c>*</c> 之后的部分）。
///
/// 三条纪律：
///   · 只认白名单扩展名，且**要求文件现在还存在** —— 注册表里全是历史记录；
///   · 按文件名精确匹配（大小写不敏感），不做模糊匹配；
///   · 读注册表失败一律静默（权限、键不存在都是常态），绝不能因此让动作失败。
/// </summary>
internal static class OfficeRecentFiles
{
    /// <summary>按文件名在 WPS / Office 的最近列表里找真实路径；找不到返回 null。</summary>
    internal static string? FindByFileName(string? fileName)
    {
        string name = (fileName ?? "").Trim();
        if (name.Length == 0) return null;
        if (ForegroundFileProbe.RankOf(name) < 0) return null;   // 只要白名单里的类型

        return FindInKingsoft(name) ?? FindInMicrosoftOffice(name);
    }

    /// <summary>
    /// WPS：<c>Office\6.0\&lt;app&gt;\RecentFiles\files\&lt;n&gt;\path</c>。
    /// 子键里的编号越大越新，所以从大到小试（编号不保证连续）。
    /// </summary>
    private static string? FindInKingsoft(string fileName)
    {
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(@"Software\Kingsoft\Office\6.0");
            if (root == null) return null;

            foreach (string app in root.GetSubKeyNames())
            {
                using RegistryKey? files = root.OpenSubKey($@"{app}\RecentFiles\files");
                if (files == null) continue;

                var indexes = new List<string>(files.GetSubKeyNames());
                indexes.Sort((a, b) => NumberOf(b).CompareTo(NumberOf(a)));

                foreach (string index in indexes)
                {
                    using RegistryKey? item = files.OpenSubKey(index);
                    if (item?.GetValue("path") is not string path) continue;
                    if (!Matches(path, fileName)) continue;
                    CoreLog.Info($"[收藏] WPS 最近文件里命中：{path}");
                    return RevealPath.Clean(path);
                }
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：读 WPS 最近文件"); }
        return null;
    }

    /// <summary>Microsoft Office：<c>Office\&lt;ver&gt;\&lt;app&gt;\File MRU</c> 的每个值。</summary>
    private static string? FindInMicrosoftOffice(string fileName)
    {
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office");
            if (root == null) return null;

            foreach (string version in root.GetSubKeyNames())
            {
                using RegistryKey? versionKey = root.OpenSubKey(version);
                if (versionKey == null) continue;

                foreach (string app in versionKey.GetSubKeyNames())
                {
                    using RegistryKey? mru = versionKey.OpenSubKey($@"{app}\File MRU");
                    if (mru == null) continue;

                    foreach (string valueName in mru.GetValueNames())
                    {
                        if (mru.GetValue(valueName) is not string raw) continue;
                        string path = ParseMruValue(raw);
                        if (!Matches(path, fileName)) continue;
                        CoreLog.Info($"[收藏] Office 最近文件里命中：{path}");
                        return RevealPath.Clean(path);
                    }
                }
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：读 Office 最近文件"); }
        return null;
    }

    /// <summary>
    /// Office 的 MRU 值 → 路径：<c>[F00000000][T01DA…][O00000000]*C:\a\b.docx</c>。
    /// 取最后一个 <c>*</c> 之后的部分；没有 <c>*</c> 就原样返回（老版本可能直接存路径）。
    /// </summary>
    internal static string ParseMruValue(string? raw)
    {
        string text = (raw ?? "").Trim();
        if (text.Length == 0) return "";
        int star = text.LastIndexOf('*');
        return star >= 0 && star + 1 < text.Length ? text[(star + 1)..].Trim() : text;
    }

    /// <summary>文件名相同且文件真的还在。</summary>
    private static bool Matches(string? path, string fileName)
    {
        string candidate = (path ?? "").Trim();
        if (candidate.Length == 0) return false;

        string leaf;
        try { leaf = Path.GetFileName(candidate); } catch { return false; }
        if (!string.Equals(leaf, fileName, StringComparison.OrdinalIgnoreCase)) return false;

        try { return File.Exists(candidate); } catch { return false; }
    }

    /// <summary>子键编号 → 数字（不是数字就当 0，排在最后）。</summary>
    private static int NumberOf(string text)
        => int.TryParse(text, out int value) ? value : 0;
}
