using System.Text.Json;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Qbar;

/// <summary>
/// Qbar 的内置内容源：开始菜单应用、浏览器书签、Windows 设置、固定项和本地文件。
///
/// 这一层刻意和 <see cref="QbarService"/> 分开：QbarService 负责“怎么把结果排好”，
/// 这里只负责“有哪些东西可以搜”。所有索引都有缓存，Query 每个字符都会调用，
/// 不能每次都去扫开始菜单或者读一遍书签文件。
/// </summary>
internal static class QbarProviders
{
    internal sealed record AppItem(string Title, string Path);

    internal sealed record SettingItem(string Title, string Uri, params string[] Aliases);

    private static readonly object Gate = new();
    private static List<AppItem>? _apps;
    private static DateTime _appsAt;
    /// <summary>文件搜索不可用的提示只提一次，别让用户每个字符都看到同一条。</summary>
    private static bool _fileHintShown;
    private static List<QbarResult>? _bookmarks;
    private static DateTime _bookmarksAt;

    /// <summary>Windows 设置 / 控制面板的常用入口。别名同时收中英文，输入“显示”或“display”都能命中。</summary>
    private static readonly SettingItem[] Settings =
    {
        new("显示设置", "ms-settings:display", "显示", "屏幕", "分辨率", "display", "screen", "resolution"),
        new("声音设置", "ms-settings:sound", "声音", "音量", "输出", "sound", "audio", "volume"),
        new("通知和操作", "ms-settings:notifications", "通知", "notification"),
        new("电源和睡眠", "ms-settings:powersleep", "电源", "睡眠", "power", "sleep", "battery"),
        new("存储感知", "ms-settings:storagesense", "存储", "空间", "storage"),
        new("已安装的应用", "ms-settings:appsfeatures", "应用", "卸载", "程序", "apps", "uninstall", "programs"),
        new("启动应用", "ms-settings:startupapps", "开机启动", "startup"),
        new("Wi-Fi", "ms-settings:network-wifi", "无线", "wifi", "wireless", "网络"),
        new("网络和 Internet", "ms-settings:network", "网络", "internet", "network"),
        new("VPN", "ms-settings:network-vpn", "vpn", "代理", "proxy"),
        new("蓝牙和其他设备", "ms-settings:bluetooth", "蓝牙", "bluetooth", "设备", "devices"),
        new("打印机和扫描仪", "ms-settings:printers", "打印机", "扫描仪", "printer", "scanner"),
        new("鼠标设置", "ms-settings:mousetouchpad", "鼠标", "指针", "mouse", "pointer"),
        new("键盘设置", "ms-settings:keyboard", "键盘", "keyboard"),
        new("笔和 Windows Ink", "ms-settings:pen", "手写笔", "pen", "ink"),
        new("区域和语言", "ms-settings:regionlanguage", "区域", "语言", "输入法", "language", "region", "ime"),
        new("日期和时间", "ms-settings:dateandtime", "时间", "日期", "时区", "date", "time", "timezone"),
        new("隐私和安全性", "ms-settings:privacy", "隐私", "权限", "privacy", "security"),
        new("摄像头", "ms-settings:privacy-webcam", "相机", "摄像头", "camera", "webcam"),
        new("麦克风", "ms-settings:privacy-microphone", "话筒", "麦克风", "microphone"),
        new("Windows 更新", "ms-settings:windowsupdate", "更新", "补丁", "update", "windows update"),
        new("恢复", "ms-settings:recovery", "重置", "恢复", "recovery", "reset"),
        new("开发者选项", "ms-settings:developers", "开发", "开发者", "developer"),
        new("关于本机", "ms-settings:about", "关于", "系统信息", "about", "system info"),
        new("任务栏设置", "ms-settings:taskbar", "任务栏", "taskbar"),
        new("个性化", "ms-settings:personalization", "主题", "颜色", "壁纸", "personalization", "theme", "color"),
        new("锁屏界面", "ms-settings:lockscreen", "锁屏", "lockscreen"),
        new("账户设置", "ms-settings:yourinfo", "账户", "账号", "account", "profile"),
        new("登录选项", "ms-settings:signinoptions", "登录", "密码", "pin", "sign in"),
        new("默认应用", "ms-settings:defaultapps", "默认程序", "default apps"),
        new("字体", "ms-settings:fonts", "字体", "font"),
        new("控制面板", "control.exe", "控制面板", "control panel", "control"),
        new("设备管理器", "devmgmt.msc", "设备管理器", "驱动", "device manager", "driver"),
        new("服务", "services.msc", "服务", "services"),
        new("磁盘管理", "diskmgmt.msc", "磁盘", "分区", "disk", "partition"),
        new("电源选项", "powercfg.cpl", "电源计划", "power options"),
        new("程序和功能", "appwiz.cpl", "卸载程序", "programs and features"),
        new("系统属性", "sysdm.cpl", "环境变量", "系统属性", "system properties", "environment"),
        new("网络连接", "ncpa.cpl", "网卡", "网络连接", "network connections", "adapter"),
        new("Windows 功能", "optionalfeatures.exe", "功能", "windows features", "optional features"),
    };

    /// <summary>把固定项、应用、书签、设置、文件全部并进 Qbar 候选。</summary>
    /// <param name="fileOutcome">后台算好的文件结果（null = 就地同步算）。</param>
    /// <param name="skipFiles">后台还在算时先不碰文件，避免 UI 线程被扫盘拖住。</param>
    public static IEnumerable<QbarResult> Search(AppSettings settings, string text, int limit,
        QbarSearchOutcome? fileOutcome = null, bool skipFiles = false)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        string q = text.Trim();

        var other = new List<QbarResult>();
        other.AddRange(MatchPinned(settings.Qbar.Pinned, q));
        other.AddRange(MatchApps(q));
        other.AddRange(MatchBookmarks(q));
        other.AddRange(MatchSettings(q));

        foreach (var r in other) yield return r;

        /* 内核不可用时 MatchFiles 会带一条“文件搜索不可用”的提示（Payload 为空）。
         * 如果应用/书签/设置已经命中了，这条提示只会把列表撑乱——
         * 用户看着“显示设置”下面挂一条“找不到文件索引”，并不知道这是
         * 给文件搜索用的。只在完全没有其它结果时才展示它。 */
        foreach (var r in MatchFiles(settings, q, limit, fileOutcome, skipFiles))
        {
            if (r.Payload.Length == 0 && other.Count > 0) continue;
            yield return r;
        }
    }

    /// <summary>固定项：用户主动放进 Qbar 的文件、文件夹、网址或程序。</summary>
    public static IEnumerable<QbarResult> MatchPinned(IEnumerable<QEntry>? pinned, string text)
    {
        if (pinned == null) yield break;
        foreach (var entry in pinned)
        {
            if (!entry.Enabled) continue;
            if (!QbarTextMatch.Matches(entry.Key, text) &&
                !QbarTextMatch.Matches(entry.Comment, text) &&
                !QbarTextMatch.Matches(entry.Value, text)) continue;

            string title = string.IsNullOrWhiteSpace(entry.Comment) ? entry.Key : entry.Comment;
            if (Uri.TryCreate(entry.Value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                yield return new QbarResult(QbarKind.Web, title, entry.Value, "固定", entry.Value)
                { Score = -2, IconPath = entry.IconPath };
                continue;
            }

            if (Directory.Exists(entry.Value) || File.Exists(entry.Value))
            {
                yield return new QbarResult(QbarKind.File, title, entry.Value, "固定", entry.Value)
                { Score = -2, IconPath = entry.IconPath };
                continue;
            }

            yield return new QbarResult(QbarKind.Run, title, entry.Value, "固定", entry.Value.Trim('"'),
                entry.IconPath, entry.RunAsAdmin, entry.Arguments)
            { Score = -2 };
        }
    }

    private static IEnumerable<QbarResult> MatchApps(string text)
    {
        var apps = Apps();
        int shown = 0;
        foreach (var app in apps)
        {
            bool direct = app.Title.Contains(text, StringComparison.OrdinalIgnoreCase);
            bool initials = QbarTextMatch.Matches(app.Title, text);
            if (!direct && !initials) continue;
            yield return new QbarResult(QbarKind.Run, app.Title, "开始菜单应用", "应用", app.Path)
            { Score = app.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 4 : direct ? 12 : 8 };
            if (++shown >= 5) yield break;
        }
    }

    private static IEnumerable<QbarResult> MatchSettings(string text)
    {
        foreach (var item in Settings)
        {
            bool direct = item.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                          item.Aliases.Any(a => a.Contains(text, StringComparison.OrdinalIgnoreCase));
            bool initials = QbarTextMatch.Matches(item.Title, text) ||
                            item.Aliases.Any(a => QbarTextMatch.Matches(a, text));
            if (!direct && !initials) continue;
            yield return new QbarResult(QbarKind.Run, item.Title, "Windows 设置", "设置", item.Uri)
            { Score = item.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 16 : direct ? 22 : 18 };
        }
    }

    private static IEnumerable<QbarResult> MatchBookmarks(string text)
    {
        var marks = Bookmarks();
        int shown = 0;
        foreach (var mark in marks)
        {
            if (!QbarTextMatch.Matches(mark.Title, text) &&
                !QbarTextMatch.Matches(mark.Subtitle, text)) continue;
            yield return mark with { Score = 14 };
            if (++shown >= 4) yield break;
        }
    }

    /// <summary>
    /// <summary>
    /// 文件搜索：Input Plus 自己的索引（见 <see cref="NativeSearchIndex"/>）。
    ///
    /// 以前这里挂的是一个进程外的索引程序（SDK 优先、命令行兜底）。实际用起来有两个问题：
    /// 一是机器上本来就装着它的，会直接用那一份 —— 用户看到的就是"搜个文件把别的程序叫出来了"；
    /// 二是没装的机器上等于没有文件搜索。
    /// 现在索引与查询都在本进程里，不启动任何外部程序。
    /// </summary>
    private static IEnumerable<QbarResult> MatchFiles(AppSettings settings, string text, int limit,
        QbarSearchOutcome? prepared, bool skipFiles)
    {
        if (skipFiles) yield break;
        if (text.Length < 2) yield break;

        /* 幂等：有缓存时这里只是一次字段检查，没有才在后台开始建索引（绝不阻塞这次输入）。 */
        QbarIndex.Start();

        /* prepared 来自界面层的后台线程；为 null 说明这次是自检/命令行工具，
         * 就地同步跑一遍（那些场景没有 UI 线程可卡）。 */
        QbarSearchOutcome outcome = prepared ??
            QbarSearchEngine.Run(QbarSearchEngine.FromSettings(settings), text, Math.Min(6, limit));

        List<QbarSearchHit> hits = outcome.Hits;
        for (int i = 0; i < hits.Count; i++)
        {
            QbarSearchHit hit = hits[i];

            /* Score 是"越小越靠前"，引擎那边是"越大越靠前"，所以这里按名次折算：
             * 名次本身已经是排序结果，别再做一次映射，否则并列分会被打散。 */
            yield return new QbarResult(QbarKind.File, hit.Name, hit.Path,
                hit.IsDir ? "文件夹" : "文件", hit.Path)
            {
                Score = 21 + i,
                Highlights = hit.Highlights,
            };
        }

        if (hits.Count > 0) yield break;

        /* 索引没就绪：0 条不代表"没有这个文件"，而是"没人能回答"。
         * 必须给一条能点的提示 —— 否则索引还在建的时候，用户搜什么都是空的，
         * 只会以为搜索坏了、或者以为文件被删了。 */
        if (string.Equals(outcome.Backend, "off", StringComparison.Ordinal))
        {
            yield return new QbarResult(QbarKind.Command,
                Loc.T("文件索引还没就绪"),
                Loc.T("点这里重建文件索引（索引在本程序里，不需要另装任何东西）"), "文件", "reindex")
            {
                Score = 27,
                Action = "qbar:rebuild-index",
            };
            yield break;
        }

        /* 拼音索引还没建好：说一句就够，别让用户对着 0 结果猜"是不是我打错了"。
         * 索引自己在后台建，建完下一次输入就正常。 */
        if (outcome.PinyinPending)
        {
            yield return new QbarResult(QbarKind.Command, Loc.T("拼音索引正在建立"),
                Loc.T("建好之后 yh / yinhang 这类输入就能命中中文文件名"), "拼音", "pending")
            { Score = 27 };
            yield break;
        }

        /* 被优先级规则挡掉的结果：数量要给出来，还要能点开看原因。
         * 静默吞掉是这一层最容易招骂的行为 —— 用户会以为文件丢了。 */
        if (outcome.HiddenCount > 0)
        {
            yield return new QbarResult(QbarKind.Command,
                Loc.F("有 {0} 条结果被优先级规则隐藏", outcome.HiddenCount),
                Loc.T("点开查看被隐藏的原因与样例"), "优先级", "hidden")
            {
                Score = 26,
                Action = "qbar:hidden",
            };
            yield break;
        }

        /* 一条都没有分两种：索引答了"真没有"，或者索引还没就绪。后者必须说一句，
         * 否则用户会一直以为自己记错了文件名。每个进程只提一次，
         * 别让每敲一个字符都冒一条同样的提示。 */
        string problem = QbarIndex.Problem;
        bool notReady = !QbarIndex.Ready || QbarIndex.Building;
        if (problem.Length == 0 && !notReady) yield break;
        if (_fileHintShown) yield break;

        _fileHintShown = true;
        yield return new QbarResult(QbarKind.Command, "文件索引还没就绪",
            problem.Length > 0 ? problem : QbarIndex.Status, "文件", "", null) { Score = 27 };
    }

    private static List<AppItem> Apps()
    {
        lock (Gate)
        {
            if (_apps != null && DateTime.UtcNow - _appsAt < TimeSpan.FromMinutes(10)) return _apps;
        }

        var list = new List<AppItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                 })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                };
                foreach (var file in Directory.EnumerateFiles(root, "*", options))
                {
                    string ext = Path.GetExtension(file);
                    if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".url", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)) continue;
                    string title = Path.GetFileNameWithoutExtension(file);
                    if (title.Length == 0 || !seen.Add(title)) continue;
                    list.Add(new AppItem(title, file));
                    if (list.Count >= 6000) break;
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "索引开始菜单"); }
        }

        lock (Gate)
        {
            _apps = list;
            _appsAt = DateTime.UtcNow;
        }
        return list;
    }

    private static List<QbarResult> Bookmarks()
    {
        lock (Gate)
        {
            if (_bookmarks != null && DateTime.UtcNow - _bookmarksAt < TimeSpan.FromMinutes(5)) return _bookmarks;
        }

        var list = new List<QbarResult>();
        foreach (var path in BookmarkFiles())
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("roots", out var roots)) continue;
                foreach (var root in roots.EnumerateObject()) WalkBookmark(root.Value, list);
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "读取浏览器书签"); }
        }

        lock (Gate)
        {
            _bookmarks = list;
            _bookmarksAt = DateTime.UtcNow;
        }
        return list;
    }

    private static void WalkBookmark(JsonElement node, List<QbarResult> list)
    {
        if (list.Count >= 4000) return;
        if (node.ValueKind != JsonValueKind.Object) return;

        string type = node.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        if (type.Equals("url", StringComparison.OrdinalIgnoreCase) &&
            node.TryGetProperty("url", out var urlEl) &&
            node.TryGetProperty("name", out var nameEl))
        {
            string url = urlEl.GetString() ?? "";
            string name = nameEl.GetString() ?? url;
            if (url.Length > 0)
                list.Add(new QbarResult(QbarKind.Web, name, url, "书签", url));
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray()) WalkBookmark(child, list);
        }
    }

    private static IEnumerable<string> BookmarkFiles()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (browser, profileRoot) in new[]
                 {
                     ("Google/Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
                     ("Microsoft/Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
                     ("BraveSoftware/Brave-Browser", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
                 })
        {
            if (!Directory.Exists(profileRoot)) continue;
            yield return Path.Combine(profileRoot, "Default", "Bookmarks");
            foreach (var dir in Directory.EnumerateDirectories(profileRoot, "Profile *"))
                yield return Path.Combine(dir, "Bookmarks");
        }
    }
}
