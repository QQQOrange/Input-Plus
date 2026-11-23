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

    /// <summary>探针用：把"实际展开到的目录"摊开，0 条时能分清"没装"和"路径算错"。</summary>
    internal IReadOnlyList<string> FoldersForProbe() => ExpandFolders();
}

/// <summary>
/// 微信文件。两个世代都要认：
///   · 3.x：Documents\WeChat Files\&lt;wxid&gt;\FileStorage\File
///   · 4.x：Documents\xwechat_files\&lt;wxid&gt;\msg\file
///
/// 还要认**用户改过的保存位置**：新版微信把文件根目录写在
/// <c>%APPDATA%\Tencent\xwechat\config\*.ini</c>（内容就是一行路径，本机实测
/// <c>D:\something</c>），真实文件在 <c>&lt;它&gt;\xwechat_files\&lt;wxid&gt;\msg\file</c>。
/// 只认 Documents 的话，把聊天记录挪到别的盘的用户会看到"微信来源永远是 0 条"。
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

            foreach (string root in ConfiguredRoots())
            {
                /* 4.x：root 是"文件保存位置"，账号目录在它下面的 xwechat_files\<wxid>；
                 * 也兼容把 root 直接指到 xwechat_files 的写法。 */
                yield return Path.Combine(root, "xwechat_files", "*", "msg", "file");
                yield return Path.Combine(root, "*", "msg", "file");

                /* 3.x：保存位置本身下面就是账号目录。 */
                yield return Path.Combine(root, "*", "FileStorage", "File");
                yield return Path.Combine(root, "FileStorage", "File");
            }
        }
    }

    /// <summary>
    /// 微信自己记的"文件保存位置"。
    ///
    /// 4.x：<c>%APPDATA%\Tencent\xwechat\config\*.ini</c>，每个文件内容是一行路径；
    /// 3.x：<c>HKCU\Software\Tencent\WeChat</c> 的 <c>FileSavePath</c>。
    /// 读不到就返回空（默认位置那几条已经覆盖"装在默认目录"的情况），任何异常都静默。
    /// </summary>
    private static IEnumerable<string> ConfiguredRoots()
    {
        var roots = new List<string>();

        void Add(string? raw)
        {
            string path = (raw ?? "").Trim().Trim('"');
            if (path.Length == 0 || !Path.IsPathRooted(path)) return;
            try { if (!Directory.Exists(path)) return; } catch { return; }
            if (!roots.Contains(path, StringComparer.OrdinalIgnoreCase)) roots.Add(path);
        }

        try
        {
            string config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Tencent", "xwechat", "config");

            if (Directory.Exists(config))
            {
                foreach (string ini in Directory.EnumerateFiles(config, "*.ini"))
                {
                    try { Add(File.ReadAllText(ini)); }
                    catch (Exception ex) { CoreLog.Swallow(ex, "建议：读微信保存位置"); }
                }
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "建议：枚举微信配置"); }

        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Tencent\WeChat");
            if (key != null)
            {
                Add(key.GetValue("FileSavePath") as string);
                Add(key.GetValue("FileSavePath2") as string);
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "建议：读微信注册表保存位置"); }

        return roots;
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
/// **排序用 .lnk 自己的 mtime**，也就是"什么时候被打开过" —— 这一栏叫「最近使用」，
/// 用户刚打开一张图，它就该排第一。目标文件自己的 mtime 是"内容改过的时间"，
/// 半年前拍的照片今天打开，mtime 还是半年前，拿它排序会把刚打开的文件压到最底下
/// （用户实测：`123.jpg` 打开于 21:13，文件 mtime 是 7 月，于是没出现在历史记录前面）。
///
/// 系统会把同名条目改名（"报表 (2).xlsx.lnk"），但改名不会改 LastWriteTime，
/// 所以用 .lnk 的 mtime 当"打开时间"是安全的；同一个目标有多条链接时取最新的那条。
/// </summary>
public sealed class WindowsRecentSource : IRecommendationSource
{
    public string Id => "windows-recent";
    public SourceKind Kind => SourceKind.WindowsRecent;
    public int Weight => 90;
    public string Badge => "最近";

    private const int MaxLinks = 240;

    /// <summary>WPS / Office 最近文件最多补多少条（它们没有系统 .lnk，只能单独读）。</summary>
    private const int MaxOfficeItems = 60;

    private static string RecentFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Recent");

    public bool Probe(SuggestionContext context) => DirectoryScan.DirExists(RecentFolder);

    /// <summary>
    /// 采集**之后**才打开的文件（Recent 里 mtime 比快照更新的 .lnk）。
    ///
    /// 用在首屏读取时补头：后台采集是几十秒一轮，用户"刚打开一张图就来看历史记录"时
    /// 快照里还没有它。只解析比快照新的那几条（正常 0～3 条），代价可以忽略。
    /// </summary>
    internal static IReadOnlyList<RecItem> NewSince(DateTime since, int max)
    {
        var list = new List<RecItem>();
        if (max <= 0) return list;

        try
        {
            var folder = new DirectoryInfo(RecentFolder);
            if (!folder.Exists) return list;

            /* 留 2 秒余量：采集进行中打开的条目，CompletedAt 可能比 .lnk 的 mtime 略晚。 */
            DateTime floor = since == DateTime.MinValue ? DateTime.MinValue : since.AddSeconds(-2);

            var files = folder.EnumerateFiles("*.lnk", SearchOption.TopDirectoryOnly)
                .Where(f => f.LastWriteTime > floor)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(max)
                .ToList();

            foreach (var file in files)
            {
                string? target = ShellLinkReader.TargetOf(file.FullName);
                if (string.IsNullOrWhiteSpace(target)) continue;

                bool isDir = DirectoryScan.DirExists(target);
                if (!isDir && !DirectoryScan.FileExists(target)) continue;

                list.Add(ItemFor(target, file.LastWriteTime, isDir));
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "建议：补采集后新打开的文件"); }
        return list;
    }

    /// <summary>文件是不是就在这个文件夹里（只比一层，大小写不敏感）。</summary>
    private static bool DirectlyUnder(string file, string folder)
    {
        try
        {
            string? dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir)) return false;

            return string.Equals(
                dir!.TrimEnd('\\', '/'),
                folder.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>把一条 .lnk 的目标做成 RecItem（采集与增量补头共用）。</summary>
    private static RecItem ItemFor(string target, DateTime openedAt, bool isDir)
    {
        string name;
        try { name = Path.GetFileName(target.TrimEnd('\\')); }
        catch { name = target; }
        if (string.IsNullOrEmpty(name)) name = target;

        return new RecItem
        {
            ItemType = isDir ? RecItemType.Folder : RecItemType.File,
            SourceId = "windows-recent",
            SourceKind = SourceKind.WindowsRecent,
            SourceWeight = 90,
            Path = target,
            Name = name,
            /* 排序键 = 打开时间（.lnk 的 mtime），不是目标文件的修改时间。 */
            Modified = openedAt,
            Badges = new[] { "最近" },
            MergedSources = new[] { "windows-recent" },
        };
    }

    public IReadOnlyList<RecItem> Collect(SuggestionContext context, SuggestionBudget budget)
    {
        var items = new List<RecItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                if (!seen.Add(RecItem.NormalizeKey(target))) continue;

                /* 时间取 .lnk 的（= 什么时候打开过）。目标文件的 mtime 只说明内容改过没有，
                 * 拿它排序会让"半年前的旧照片今天打开"排到最后。 */
                items.Add(ItemFor(target, DirectoryScan.SafeModified(lnk), isDir));
            }

            /* 打开一个文件时，资源管理器会顺手更新它所在**文件夹**的 .lnk ——
             * 于是"刚打开 桌面\123.jpg"会让「桌面」这个文件夹也以同一时刻冲上来，
             * 把真正的文件挤到第二。同一时刻（±2 秒）既有文件夹、又有它里面的文件时，
             * 丢掉文件夹那条：用户刚打开的是文件，不是"又去了一趟那个文件夹"。 */
            var recentFiles = items.Where(i => i.ItemType == RecItemType.File).ToList();
            items.RemoveAll(i => i.ItemType == RecItemType.Folder && recentFiles.Any(f =>
                Math.Abs((f.Modified - i.Modified).TotalSeconds) <= 2 && DirectlyUnder(f.Path, i.Path)));

            /* 补上 WPS / Office 自己记的「最近打开的文件」。
             *
             * 为什么需要：用户从 WPS 里打开的文件，系统的 shell:recent 里往往**没有** .lnk
             * （WPS 只写自己的 MRU），于是这一栏看起来"只有文件夹、没有刚打开的文件"。
             * 这份名单是"最近打开过"的直接证据，和 .lnk 一样属于「最近使用」。 */
            foreach (var (path, at) in Core.Windows.OfficeRecentFiles.Recent(MaxOfficeItems))
            {
                if (budget.Expired) break;
                if (!seen.Add(RecItem.NormalizeKey(path))) continue;

                string name;
                try { name = Path.GetFileName(path); }
                catch { name = path; }
                if (string.IsNullOrEmpty(name)) name = path;

                items.Add(new RecItem
                {
                    ItemType = RecItemType.File,
                    SourceId = Id,
                    SourceKind = Kind,
                    SourceWeight = Weight,
                    Path = path,
                    Name = name,
                    /* 能读到"什么时候打开过"就用它；读不到退回文件修改时间
                     * （排序会略偏，但"收录"不受影响 —— 用户要的是能看见它）。 */
                    Modified = at == DateTime.MinValue ? DirectoryScan.SafeModified(path) : at,
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
