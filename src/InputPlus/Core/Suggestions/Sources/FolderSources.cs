using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InputPlus.Core.Qbar;

namespace InputPlus.Core.Suggestions.Sources;

/// <summary>本地缓存目录类的基类：探活 = 目录存在，采集 = 取最近 N 条。</summary>
public abstract class FolderSourceBase : IRecommendationSource
{
    public abstract string Id { get; }
    public virtual SourceKind Kind => SourceKind.Directory;
    public abstract int Weight { get; }
    public abstract string Badge { get; }

    /// <summary>要扫的目录（可以多个）。</summary>
    protected abstract IEnumerable<string> Folders { get; }

    /// <summary>枚举深度：1 = 本层，2 = 本层 + 一层子目录。</summary>
    protected virtual int Depth => 1;

    /// <summary>本来源最多贡献多少条（进合并前）。</summary>
    protected virtual int Take => 80;

    protected virtual bool IncludeFolders => true;

    public bool Probe(SuggestionContext context)
    {
        try
        {
            foreach (string folder in Folders)
                if (DirectoryScan.DirExists(folder)) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<RecItem> Collect(SuggestionContext context, SuggestionBudget budget)
    {
        var items = new List<RecItem>();
        try
        {
            foreach (string folder in Folders)
            {
                if (budget.Expired) break;
                int per = Math.Max(1, Take - items.Count);
                DirectoryScan.Newest(folder, Depth, per, items, Id, Kind, Weight, Badge, budget,
                    includeFolders: IncludeFolders);
                if (items.Count >= Take) break;
            }
        }
        catch
        {
            // 单个目录炸了不影响其它目录；上层会记 source 计数
        }
        return items;
    }
}

/// <summary>下载目录。人找文件的第一个念头通常是"我刚下过"。</summary>
public sealed class DownloadsSource : FolderSourceBase
{
    public override string Id => "downloads";
    public override int Weight => 70;
    public override string Badge => "下载";
    protected override int Depth => 2;
    protected override int Take => 120;

    protected override IEnumerable<string> Folders
    {
        get
        {
            // 下载目录经常被重定向（本机就是 D:\Downloads），
            // 所以走注册表里的已知文件夹解析，失败才退回 %UserProfile%\Downloads。
            string downloads = KnownFolders.Downloads();
            if (downloads.Length > 0) yield return downloads;
        }
    }
}

/// <summary>桌面（含公共桌面）。快捷方式也算文件，点击走统一 execute 路径。</summary>
public sealed class DesktopSource : FolderSourceBase
{
    public override string Id => "desktop";
    public override int Weight => 65;
    public override string Badge => "桌面";
    protected override int Take => 60;

    protected override IEnumerable<string> Folders
    {
        get
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrWhiteSpace(user)) yield return user;
            if (!string.IsNullOrWhiteSpace(common) && !common.Equals(user, StringComparison.OrdinalIgnoreCase))
                yield return common;
        }
    }
}

/// <summary>
/// 应用内历史：启动器自己记过的使用记录（RecentTargets）。
///
/// 这是唯一一个"App"类建议的来源：Run 里登记过的程序会以 ItemType.App 出现，
/// 点击走原有的 Run 执行路径（不在这里直接 Process.Start）。
/// </summary>
public sealed class AppHistorySource : IRecommendationSource
{
    public string Id => "app-history";
    public SourceKind Kind => SourceKind.ListaryHistory;
    public int Weight => 110;
    public string Badge => "最近";

    public bool Probe(SuggestionContext context) => true;

    public IReadOnlyList<RecItem> Collect(SuggestionContext context, SuggestionBudget budget)
    {
        var items = new List<RecItem>();
        try
        {
            foreach (var recent in RecentTargets.Top(40))
            {
                if (budget.Expired) break;
                if (string.IsNullOrWhiteSpace(recent.Target)) continue;

                RecItemType type = recent.Kind switch
                {
                    RecentTargetKind.Folder => RecItemType.Folder,
                    RecentTargetKind.Program => RecItemType.App,
                    RecentTargetKind.File => RecItemType.File,
                    _ => RecItemType.File,
                };

                items.Add(new RecItem
                {
                    ItemType = type,
                    SourceId = Id,
                    SourceKind = Kind,
                    SourceWeight = Weight,
                    Path = recent.Target,
                    Name = recent.Title,
                    Modified = new DateTime(recent.LastUsedTicks, DateTimeKind.Local),
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

/// <summary>对话框历史：文件对话框浮层每次成功回填都会记一条，这里反过来喂建议。</summary>
public sealed class DialogHistorySource : IRecommendationSource
{
    public string Id => "dialog-history";
    public SourceKind Kind => SourceKind.DialogHistory;
    public int Weight => 100;
    public string Badge => "对话框";

    public bool Probe(SuggestionContext context) => true;

    public IReadOnlyList<RecItem> Collect(SuggestionContext context, SuggestionBudget budget)
    {
        var items = new List<RecItem>();
        try
        {
            foreach (var entry in Core.FileDialog.DialogRecorder.Recent(60))
            {
                if (budget.Expired) break;
                bool isFolder = string.Equals(entry.Kind, "folder", StringComparison.OrdinalIgnoreCase)
                                || Directory.Exists(entry.Path);
                string name;
                try { name = Path.GetFileName(entry.Path.TrimEnd('\\')); }
                catch { name = entry.Path; }
                if (string.IsNullOrEmpty(name)) name = entry.Path;

                items.Add(new RecItem
                {
                    ItemType = isFolder ? RecItemType.Folder : RecItemType.File,
                    SourceId = Id,
                    SourceKind = Kind,
                    SourceWeight = Weight,
                    Path = entry.Path,
                    Name = name,
                    Modified = entry.Ts.LocalDateTime,
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
