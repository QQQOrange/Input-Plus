using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace InputPlus.Core.Windows;

/// <summary>
/// 微信（4.x <c>xwechat_files</c> / 3.x <c>WeChat Files</c>）此刻正在看的图片或视频在哪儿。
///
/// ── 为什么要单独一条通道 ─────────────────────────────────────────────
/// 微信看到的图片、视频**不是用户文件**：它们落在数据目录深处的一堆哈希目录里，
/// 而且 4.x 的图片是加密的 <c>.dat</c>（同名旁边还有 <c>_t</c> 缩略图、<c>_h</c> 原图）。
/// 走通用的"扫前台进程句柄"那条路会连中两个坑：
///   ① 白名单里没有 <c>.dat</c>（也不该有 —— 那是所有程序都在用的通用后缀）；
///   ② 微信 4.x 默认**以管理员身份运行**，普通权限的 InputPlus 连它的进程都打不开
///      （OpenProcess → 错误 5），句柄表根本读不到。
///
/// 所以这条路**完全不碰进程**：从微信自己的配置里问出"文件保存在哪"，
/// 再在它那几棵媒体目录里挑"最近被写过的那一个"。
/// 用户刚点开一张图，那张图（或它的缩略图/原图副本）就是这几分钟里最新的东西。
///
/// ── 精度说明（诚实版）────────────────────────────────────────────────
/// 挑出来的是"最近写入"的那一个，不是"确实正看着的那一个" —— 微信不告诉我们它开着谁。
/// 但这已经够用：同一个人发来的图片全都躺在**同一个 <c>msg\attach\&lt;会话哈希&gt;\&lt;月份&gt;\Img</c>
/// 目录**里，就算挑到了同会话的上一条，打开的也是同一个文件夹。
///
/// 全部是只读操作，且**有预算**（默认 400ms / 2400 个目录）：菜单弹出的预热跑在后台，
/// 但后台也不能无限翻盘，机械盘上一棵 3 万文件的树能翻十秒。
/// </summary>
internal static class WeChatMedia
{
    /// <summary>只认这些进程：微信 4.x 主进程 / 小程序与内嵌浏览器 / 3.x。</summary>
    private static readonly string[] ProcessNames =
    {
        "weixin.exe", "wechat.exe", "wechatappex.exe", "wechatapp.exe",
    };

    /// <summary>媒体后缀。微信 4.x 的图片是加密 .dat；视频是 .mp4（旁边带 .jpg 封面与 _thumb 缩略图）。</summary>
    private static readonly string[] MediaExtensions =
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm", ".flv",   // 视频
        ".dat", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp",   // 图片
    };

    /// <summary>再旧就不认了：一周前的"最近图片"和用户此刻在看的东西毫无关系。</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>一次识别最多翻多少个目录 / 多少个文件。</summary>
    internal const int MaxDirs = 2400;
    internal const int MaxFiles = 60000;

    /// <summary>数据目录缓存时长：微信换账号/改存储位置都要重启，5 分钟足够新鲜。</summary>
    private const int RootsTtlMs = 300_000;

    private static readonly object Gate = new();
    private static string[] _roots = Array.Empty<string>();
    private static long _rootsAt;

    /// <summary>仅供自检：把"数据目录"和"当前时间"钉死，不去读真机配置。</summary>
    internal static string[]? RootsOverrideForTest { get; set; }

    /// <summary>仅供自检：把"现在"钉死，构造"三天前写过的文件"这类场景。</summary>
    internal static DateTime? NowForTest { get; set; }

    // ───────────────────────── 认人 ─────────────────────────

    /// <summary>这个前台进程是不是微信（按进程名；exe 路径拿不到时也能用）。</summary>
    internal static bool IsWeChatProcess(string? processName)
    {
        string name = (processName ?? "").Trim();
        if (name.Length == 0) return false;
        foreach (string p in ProcessNames)
            if (string.Equals(name, p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>进程名认不出来时按 exe 路径兜底（装在哪都认得出是微信家族的目录）。</summary>
    internal static bool IsWeChatPath(string? exePath)
    {
        string p = (exePath ?? "").Trim();
        if (p.Length == 0) return false;
        if (p.Contains(@"\Tencent\xwechat\", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Contains(@"\Tencent\WeChat\", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Contains(@"\Weixin\", StringComparison.OrdinalIgnoreCase)) return true;
        return IsWeChatProcess(Path.GetFileName(p));
    }

    // ───────────────────────── 数据目录 ─────────────────────────

    /// <summary>
    /// 微信的数据根目录（每个账号一个：<c>…\xwechat_files\wxid_xxx_0613</c>）。
    ///
    /// 4.x 把"文件保存位置"写在 <c>%APPDATA%\Tencent\xwechat\config\*.ini</c> 里，
    /// 整个文件就是一行路径（实测 12 字节：<c>D:\something</c>）。用户完全可以把
    /// 它放到任意盘，所以**不能猜**，只能问。
    /// 读不到配置时才退回几个常见位置扫一眼；再不行就当"没有微信"。
    /// </summary>
    internal static IReadOnlyList<string> DataRoots()
    {
        if (RootsOverrideForTest is { } fake) return fake;

        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (_rootsAt != 0 && now - _rootsAt < RootsTtlMs) return _roots;

            var found = new List<string>(4);
            foreach (string baseDir in BaseDirectories())
            {
                if (baseDir.Length == 0) continue;
                AddV4Roots(found, baseDir);
                AddV3Roots(found, baseDir);
            }

            _roots = found.ToArray();
            _rootsAt = now;
            return _roots;
        }
    }

    /// <summary>微信有可能把"文件保存位置"放在这些目录下。</summary>
    private static IEnumerable<string> BaseDirectories()
    {
        string appData = SafeFolder(Environment.SpecialFolder.ApplicationData);
        var list = new List<string>(4);

        // 4.x：配置文件里那一行
        if (appData.Length > 0)
        {
            string cfg = Path.Combine(appData, "Tencent", "xwechat", "config");
            try
            {
                if (Directory.Exists(cfg))
                {
                    foreach (string ini in Directory.EnumerateFiles(cfg, "*.ini"))
                    {
                        string line = FirstLine(ini);
                        if (line.Length > 0) list.Add(line);
                    }
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "微信：读文件保存位置"); }
        }

        /* 兜底：配置读不到（权限 / 旧版本 / 用户手改）时的常见位置。
         * 只查两处 —— 把一个 disk-wide 的搜索放在菜单弹出的热路径上是不可接受的。 */
        string documents = SafeFolder(Environment.SpecialFolder.MyDocuments);
        if (documents.Length > 0) list.Add(documents);

        string profile = SafeFolder(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0) list.Add(profile);

        return list;
    }

    /// <summary><c>&lt;base&gt;\xwechat_files\&lt;账号&gt;</c>（4.x）。</summary>
    private static void AddV4Roots(List<string> into, string baseDir)
    {
        try
        {
            string files = Path.Combine(baseDir.Trim(), "xwechat_files");
            if (!Directory.Exists(files)) return;
            foreach (string account in Directory.EnumerateDirectories(files))
            {
                if (LooksLikeAccount(account)) Add(into, account);
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "微信：找 4.x 数据目录"); }
    }

    /// <summary><c>&lt;base&gt;\WeChat Files\&lt;账号&gt;</c>（3.x）。</summary>
    private static void AddV3Roots(List<string> into, string baseDir)
    {
        try
        {
            string files = Path.Combine(baseDir.Trim(), "WeChat Files");
            if (!Directory.Exists(files)) return;
            foreach (string account in Directory.EnumerateDirectories(files))
            {
                if (LooksLikeAccount(account)) Add(into, account);
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "微信：找 3.x 数据目录"); }
    }

    /// <summary>账号目录的判据不是名字（用户能改），而是"里面有微信的数据子树"。</summary>
    private static bool LooksLikeAccount(string dir)
    {
        try
        {
            return Directory.Exists(Path.Combine(dir, "msg"))
                || Directory.Exists(Path.Combine(dir, "FileStorage"))
                || Directory.Exists(Path.Combine(dir, "MsgAttach"));
        }
        catch { return false; }
    }

    private static void Add(List<string> list, string dir)
    {
        foreach (string existing in list)
            if (string.Equals(existing, dir, StringComparison.OrdinalIgnoreCase)) return;
        list.Add(dir);
    }

    // ───────────────────────── 找那一个文件 ─────────────────────────

    /// <summary>
    /// 微信此刻（最近）打开的那张图片 / 那个视频。找不到返回 null。
    /// </summary>
    /// <param name="budgetMs">最多翻多久；调用方那 800ms 的识别预算要分它一部分。</param>
    internal static string? FindCurrent(int budgetMs = 400, CancellationToken ct = default)
    {
        IReadOnlyList<string> roots = DataRoots();
        if (roots.Count == 0) return null;

        DateTime now = NowForTest ?? DateTime.Now;
        var watch = Stopwatch.StartNew();
        string? best = null;
        long bestKey = long.MinValue;

        foreach (string root in roots)
        {
            if (ct.IsCancellationRequested) break;
            int left = budgetMs - (int)watch.ElapsedMilliseconds;
            if (left < 40) break;

            foreach (string subtree in MediaSubtrees(root))
            {
                if (ct.IsCancellationRequested) break;
                if (!SafeDirExists(subtree)) continue;

                int remaining = budgetMs - (int)watch.ElapsedMilliseconds;
                if (remaining < 40) break;

                ScanSubtree(subtree, now, remaining, ct, ref bestKey, ref best);
            }
        }

        return best;
    }

    /// <summary>要翻的几棵子树（4.x 与 3.x 各一套）。</summary>
    private static IEnumerable<string> MediaSubtrees(string root)
    {
        // 4.x
        yield return Path.Combine(root, "msg", "attach");
        yield return Path.Combine(root, "msg", "video");
        yield return Path.Combine(root, "msg", "file");
        // 3.x
        yield return Path.Combine(root, "FileStorage", "Image");
        yield return Path.Combine(root, "FileStorage", "Video");
        yield return Path.Combine(root, "FileStorage", "File");
        yield return Path.Combine(root, "MsgAttach");
    }

    /// <summary>
    /// 深度优先翻一棵子树，顺便把"目前最优"写进 <paramref name="best"/>。
    ///
    /// 剪枝靠**目录自己的写入时间**：一个 9 月之后再没被动过的月份目录，
    /// 里面不可能有"最近"的文件，直接整棵跳过。这条让 3 万文件的树通常只碰几十个目录。
    /// </summary>
    private static void ScanSubtree(string subtree, DateTime now, int budgetMs, CancellationToken ct,
        ref long bestKey, ref string? best)
    {
        var watch = Stopwatch.StartNew();
        var stack = new Stack<string>();
        stack.Push(subtree);
        int dirs = 0;
        int files = 0;
        DateTime cutoff = now - MaxAge;

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) break;
            if (watch.ElapsedMilliseconds > budgetMs) break;
            if (++dirs > MaxDirs) break;

            string dir = stack.Pop();
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.LastWriteTime < cutoff) continue;

                foreach (FileInfo file in info.EnumerateFiles())
                {
                    if (++files > MaxFiles) break;
                    if (!IsMediaExtension(file.Extension)) continue;

                    DateTime written = file.LastWriteTime;
                    if (written < cutoff) continue;

                    long key = Score(file.FullName, written);
                    if (key <= bestKey) continue;
                    bestKey = key;
                    best = file.FullName;
                }

                foreach (DirectoryInfo sub in info.EnumerateDirectories())
                    stack.Push(sub.FullName);
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "微信：翻媒体目录"); }
        }
    }

    /// <summary>
    /// 排序键：**写得越晚越优先**，缩略图 / 封面往后压半小时。
    ///
    /// 为什么缩略图要压：微信每张图旁边都有 <c>xxx_t.dat</c>（缩略图）与 <c>xxx_h.dat</c>（原图），
    /// 视频旁边有 <c>_thumb.jpg</c> 与封面 <c>.jpg</c> —— 它们和正片同一秒写入，
    /// 单纯比时间会随机挑到缩略图，打开的位置虽然对，选中的却是个看不清的小文件。
    /// </summary>
    private static long Score(string path, DateTime written)
    {
        long key = written.Ticks;
        string leaf = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (leaf.EndsWith("_t", StringComparison.Ordinal) || leaf.EndsWith("_thumb", StringComparison.Ordinal))
            key -= TimeSpan.FromMinutes(30).Ticks;
        else if (leaf.EndsWith("_h", StringComparison.Ordinal))
            key -= TimeSpan.FromMinutes(1).Ticks;

        return key;
    }

    private static bool IsMediaExtension(string ext)
    {
        if (ext.Length == 0) return false;
        foreach (string e in MediaExtensions)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static string FirstLine(string file)
    {
        try
        {
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line = reader.ReadLine();
            return (line ?? "").Trim().Trim('"');
        }
        catch { return ""; }
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder) ?? ""; } catch { return ""; }
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    /// <summary>仅供自检：清掉数据目录缓存。</summary>
    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _roots = Array.Empty<string>();
            _rootsAt = 0;
        }
        RootsOverrideForTest = null;
        NowForTest = null;
    }
}
