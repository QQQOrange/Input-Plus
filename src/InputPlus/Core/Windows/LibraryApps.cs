using System.IO;

namespace InputPlus.Core.Windows;

/// <summary>
/// 库型应用的适配：内容被导入程序自己的库里，磁盘上没有"用户正在看的那个文件"。
///
/// ## 为什么需要
///
/// Reeden 就是典型：它把每本书解包成
/// <c>%APPDATA%\app.reeden\reeden\books\&lt;32 位 HEX&gt;\</c>
/// （正文、toc.json、插图全在里面），进程句柄里只有自己的 sqlite / hive，
/// 窗口标题只有 "reeden"。于是 <see cref="ForegroundFileProbe.ScanProcess"/> 的三条路
/// 全部空手：句柄快照挑不出候选、命令行没有路径、标题里没有文件名 ——
/// "打开文件所在位置"永远是灰的。
///
/// 2026-11-26 实测（pid 576，FLUTTER_RUNNER_WIN32_WINDOW）：
/// 75 个文件句柄、68 条原始命中、**0 个候选**，全是 dll / 数据库 / 字库。
///
/// 这里补一条降级：找"最近活跃"的那本书目录。活跃信号是 Reeden 自己的三个标记文件：
/// <c>statistics.lock</c> / <c>statistics.hive</c> / <c>book_config.json</c> ——
/// 它在读写某本书时会重写它们，而**只被读过的旧书**不会这么新。
/// 命中后把这个目录交给"打开文件所在位置"，资源管理器会跳到那本书的目录（能看到正文与插图）。
///
/// ## 为什么不是"解析数据库拿书名和原始路径"
///
/// Reeden 的 reeden.db 是 SQLCipher 加密的（它自带 sqlcipher_flutter_libs），
/// 而且导入时原文件就已经解包进库、不保证还留着用户双击的那个 epub。
/// 与其猜，不如把"它此刻真的在用的那个目录"给用户 —— 这是磁盘上真实存在、
/// 且和当前内容一一对应的东西。
/// </summary>
internal static class LibraryApps
{
    /// <summary>
    /// 多久之内算"正在看"。
    ///
    /// 取 3 小时：Reeden 的标记文件只在真正读写那本书时才动，但用户可能读完一章
    /// 就去干别的，回来再点菜单。太短（比如 10 分钟）会把"刚放下"的书判成没有，
    /// 太长则会在打开一本新书之前误报上次那本 —— 3 小时是这两个方向的折中。
    /// </summary>
    private const int FreshMinutes = 180;

    /// <summary>
    /// 前台程序是库型应用时，返回它此刻打开的那一项在磁盘上的目录；不是就返回 null。
    ///
    /// 对非库型应用是 O(1)：只比较进程名，不碰磁盘。
    /// </summary>
    internal static string? CurrentItemFolder(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return null;
        if (processName.Equals("reeden.exe", StringComparison.OrdinalIgnoreCase))
            return NewestReedenBook();
        return null;
    }

    /// <summary>Reeden 的库根：<c>%APPDATA%\app.reeden\reeden\books</c>。</summary>
    internal static string? ReedenBooksRoot()
    {
        /* 用环境变量而不是 GetFolderPath：两者在正常机器上一样，但 AppData 被
         * 重定向过的机器（本机 Roaming 就指向 D:\software\AppData\Roaming）只有
         * 环境变量是程序实际看到的那个 —— Reeden 自己也是这么找的。 */
        string? appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) return null;

        string root = Path.Combine(appData, "app.reeden", "reeden", "books");
        try { return Directory.Exists(root) ? root : null; }
        catch { return null; }
    }

    /// <summary>库根下最近活跃的那个书目录；都太老就返回 null（说明没在读任何一本）。</summary>
    private static string? NewestReedenBook()
    {
        string? root = ReedenBooksRoot();
        if (root == null) return null;

        DateTime notBefore = DateTime.UtcNow.AddMinutes(-FreshMinutes);
        string? best = null;
        long bestTicks = 0;

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                long ticks = LastActivityTicks(dir);
                if (ticks == 0) continue;
                if (new DateTime(ticks, DateTimeKind.Utc) < notBefore) continue;
                if (ticks <= bestTicks) continue;
                bestTicks = ticks;
                best = dir;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[库型应用] 枚举 Reeden 书库失败：" + ex.Message);
            return null;
        }

        if (best != null) CoreLog.Info("[库型应用] Reeden 当前书目录：" + best);
        return best;
    }

    /// <summary>目录里那几个"在读"标记文件的最新写入时刻（UtcTicks；一个都没有就是 0）。</summary>
    private static long LastActivityTicks(string dir)
    {
        long best = 0;
        foreach (string name in ActivityMarkers)
        {
            try
            {
                var file = new FileInfo(Path.Combine(dir, name));
                if (!file.Exists) continue;
                if (file.LastWriteTimeUtc.Ticks > best) best = file.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                /* 单个标记读不到不影响其它标记 —— 书目录随时可能被 Reeden 重写 */
            }
        }
        return best;
    }

    /// <summary>
    /// "这本书正在被读写"的三个信号。
    ///
    /// statistics.lock 是阅读统计的锁（打开书时建、切书时换）；
    /// statistics.hive 是阅读进度；book_config.json 是这本书的阅读设置。
    /// 三者只要有一个够新，就说明它刚刚被用过。
    /// </summary>
    private static readonly string[] ActivityMarkers =
    {
        "statistics.lock",
        "statistics.hive",
        "book_config.json",
    };
}
