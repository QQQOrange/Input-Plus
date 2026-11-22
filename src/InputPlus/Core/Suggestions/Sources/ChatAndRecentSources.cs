using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InputPlus.Core.Suggestions.Sources;

/// <summary>
/// 按"通配模式"找目录的来源基类（微信/企业微信/飞书的账号目录都是这种形态：
/// 一层账号文件夹 + 固定尾巴）。
///
/// 探活阶段只展开模式、判断目录在不在，**不枚举文件**；目录不存在就计 0，不报错。
/// </summary>
public abstract class PatternFolderSourceBase : IRecommendationSource
{
    public abstract string Id { get; }
    public virtual SourceKind Kind => SourceKind.Directory;
    public abstract int Weight { get; }
    public abstract string Badge { get; }

    /// <summary>带环境变量与单层 '*' 的目录模式。</summary>
    protected abstract IEnumerable<string> Patterns { get; }

    protected virtual int Depth => 2;
    protected virtual int Take => 100;

    public bool Probe(SuggestionContext context)
    {
        try { return ExpandFolders().Count > 0; }
        catch { return false; }
    }

    public IReadOnlyList<RecItem> Collect(SuggestionContext context, SuggestionBudget budget)
    {
        var items = new List<RecItem>();
        try
        {
            foreach (string folder in ExpandFolders())
            {
                if (budget.Expired || items.Count >= Take) break;
                DirectoryScan.Newest(folder, Depth, Math.Max(1, Take - items.Count), items,
                    Id, Kind, Weight, Badge, budget);
            }
        }
        catch
        {
        }
        return items;
    }

    protected List<string> ExpandFolders()
    {
        var list = new List<string>();
        foreach (string pattern in Patterns)
        {
            foreach (string folder in DirectoryScan.Expand(pattern))
                if (!list.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    list.Add(folder);
        }
        return list;
    }
}

/// <summary>
/// 微信文件。两个世代都要认：
///   · 3.x：Documents\WeChat Files\&lt;wxid&gt;\FileStorage\File
///   · 4.x：Documents\xwechat_files\&lt;wxid&gt;\msg\file
/// 目录不存在只是"没装/没用过"，计 0。
/// </summary>
public sealed class WeChatSource : PatternFolderSourceBase
{
    public override string Id => "wechat";
    public override int Weight => 80;
    public override string Badge => "微信";
    protected override int Take => 100;

    protected override IEnumerable<string> Patterns
    {
        get
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(docs, "WeChat Files", "*", "FileStorage", "File");
            yield return Path.Combine(docs, "xwechat_files", "*", "msg", "file");
            yield return Path.Combine(profile, "xwechat_files", "*", "msg", "file");
        }
    }
}

/// <summary>企业微信：Documents\WXWork\&lt;corp&gt;\Cache\File。</summary>
public sealed class WeComSource : PatternFolderSourceBase
{
    public override string Id => "wecom";
    public override int Weight => 75;
    public override string Badge => "企业微信";
    protected override int Take => 80;

    protected override IEnumerable<string> Patterns
    {
        get
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return Path.Combine(docs, "WXWork", "*", "Cache", "File");
            yield return Path.Combine(docs, "WXWork", "*", "Cache", "Image");
        }
    }
}

/// <summary>飞书：下载目录式落盘，账号目录名带版本与哈希，用一层通配兜住。</summary>
public sealed class FeishuSource : PatternFolderSourceBase
{
    public override string Id => "feishu";
    public override int Weight => 75;
    public override string Badge => "飞书";
    protected override int Take => 80;

    protected override IEnumerable<string> Patterns
    {
        get
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(docs, "Feishu", "*");
            yield return Path.Combine(docs, "飞书", "*");
            yield return Path.Combine(profile, "Feishu", "*");
            yield return Path.Combine(profile, "Downloads", "Feishu");
        }
    }
}

/// <summary>
/// Windows 最近记录：枚举 shell:recent（%APPDATA%\Microsoft\Windows\Recent）里的 .lnk，
/// 用 IShellLink 解析 TargetPath。
///
/// 刻意不看 .lnk 自己的 mtime —— 系统会把同名条目改名（"报表 (2).xlsx.lnk"），
/// mtime 只反映"什么时候被打开过"，用它反推目标文件的时间会错。
/// </summary>
public sealed class WindowsRecentSource : IRecommendationSource
{
    public string Id => "windows-recent";
    public SourceKind Kind => SourceKind.WindowsRecent;
    public int Weight => 90;
    public string Badge => "最近";

    private const int MaxLinks = 240;

    private static string RecentFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Recent");

    public bool Probe(SuggestionContext context) => DirectoryScan.DirExists(RecentFolder);

    public IReadOnlyList<RecItem> Collect(SuggestionContext context, SuggestionBudget budget)
    {
        var items = new List<RecItem>();
        try
        {
            var files = Directory.EnumerateFiles(RecentFolder, "*.lnk", SearchOption.TopDirectoryOnly)
                .OrderByDescending(DirectoryScan.SafeModified)
                .Take(MaxLinks)
                .ToList();

            foreach (string lnk in files)
            {
                if (budget.Expired) break;
                string? target = ShellLinkReader.TargetOf(lnk);
                if (string.IsNullOrWhiteSpace(target)) continue;

                bool isDir = DirectoryScan.DirExists(target);
                bool isFile = !isDir && DirectoryScan.FileExists(target);
                if (!isDir && !isFile) continue;      // 目标已删除/在离线卷上

                string name;
                try { name = Path.GetFileName(target.TrimEnd('\\')); }
                catch { name = target; }
                if (string.IsNullOrEmpty(name)) name = target;

                items.Add(new RecItem
                {
                    ItemType = isDir ? RecItemType.Folder : RecItemType.File,
                    SourceId = Id,
                    SourceKind = Kind,
                    SourceWeight = Weight,
                    Path = target,
                    Name = name,
                    Modified = isDir ? Directory.GetLastWriteTime(target) : DirectoryScan.SafeModified(target),
                    Badges = new[] { Badge },
                    MergedSources = new[] { Id },
                });
            }
        }
        catch
        {
        }
        return items;
    }
}
