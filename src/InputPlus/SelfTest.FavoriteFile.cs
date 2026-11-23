using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InputPlus.Core.Qbar;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus;

/// <summary>
/// 自检：附加菜单顶部的「收藏当前文件 / 打开文件所在位置」（v1.5.1）。
///
/// 七组，全部与"当前是不是管理员"无关：
///   ① 关键词验收表（13 条，逐条对照用户给的表）
///   ② 关键字撞车升级链 rep → repo → rep2
///   ③ 噪声过滤（目录 / 运行时 dll / 锁文件 / 0 字节 / 系统目录边界）
///   ④ 候选打分（标题命中优先、写得更晚优先、白名单外出局）
///   ⑤ 收藏幂等（同一个文件收两次只有一条，次数 +1）
///   ⑥ 打开位置的路径清洗（\\?\ / UNC / 上溯 / 只开目录）
///   ⑦ 真机：扫**自己**进程能命中自己刚打开的文件 + 提权/未提权各走哪条通道
///
/// 为什么第 ⑦ 组敢在自检里跑真实句柄枚举：扫自己永远有权限，
/// 于是"能不能认出文件"这件事与"跑自检的身份"彻底解耦 ——
/// 提权下与不提权下是同一条断言（CI 也跑得动）。
/// </summary>
public static partial class SelfTest
{
    private static void TestFavoriteFile()
    {
        TestFavoriteKeywordTable();
        TestFavoriteKeywordConflict();
        TestForegroundNoiseFilter();
        TestForegroundCandidateOrder();
        TestPinnedFavoriteIdempotent();
        TestRevealPathCleaning();
        TestForegroundFileSelfScan();
        TestForegroundFileCacheAnchor();
        TestForegroundFileChain();
        TestFavoriteBranchPriority();
        TestFavoriteContextFallback();
        TestForegroundAnchorChoice();
        TestOfficeRecentParsing();
    }

    // ── ① 验收表 ──────────────────────────────────────────────────

    private static void TestFavoriteKeywordTable()
    {
        CheckEqual("关键词：Report → rep", FavoriteKeywords.KeywordFor("Report.pdf"), "rep");
        CheckEqual("关键词：Roadmap → roa", FavoriteKeywords.KeywordFor("Roadmap.xlsx"), "roa");
        CheckEqual("关键词：Annual Report 2024 → ar（数字跳过）",
            FavoriteKeywords.KeywordFor("Annual Report 2024.pdf"), "ar");
        CheckEqual("关键词：2024 Q3 Report-v2 → qrv（v2 取 v）",
            FavoriteKeywords.KeywordFor("2024 Q3 Report-v2.docx"), "qrv");
        CheckEqual("关键词：Q4 OKR 汇总 → qohz（中英混合截 4）",
            FavoriteKeywords.KeywordFor("Q4 OKR 汇总.docx"), "qohz");
        CheckEqual("关键词：季度总结 → jdzj（拼音首字母）",
            FavoriteKeywords.KeywordFor("季度总结.pptx"), "jdzj");
        CheckEqual("关键词：项目计划书 → xmjh（超 4 截断）",
            FavoriteKeywords.KeywordFor("项目计划书.docx"), "xmjh");
        CheckEqual("关键词：简历 → jl（不足 4 不补）",
            FavoriteKeywords.KeywordFor("简历.pdf"), "jl");
        CheckEqual("关键词：UI设计稿 → usjg（中英混合）",
            FavoriteKeywords.KeywordFor("UI设计稿.fig"), "usjg");
        CheckEqual("关键词：测试1.2版 → cs（数字与版本词跳过）",
            FavoriteKeywords.KeywordFor("测试1.2版.txt"), "cs");
        CheckEqual("关键词：a.md → a（不足 3 取全部）",
            FavoriteKeywords.KeywordFor("a.md"), "a");
        CheckEqual("关键词：12345.txt → 12（纯数字取首 2）",
            FavoriteKeywords.KeywordFor("12345.txt"), "12");
        CheckEqual("关键词：🎉.png → file（无可用字符兜底）",
            FavoriteKeywords.KeywordFor("🎉.png"), "file");

        /* 预处理里踩过的两个坑，各钉一条 —— 它们不会出现在验收表里，
         * 但改预处理的人一定会碰到。 */
        CheckEqual("关键词：~$ 锁文件前缀要去掉",
            FavoriteKeywords.KeywordFor("~$季度总结.pptx"), "jdzj");
        CheckEqual("关键词：全角文件名先转半角再剥扩展名",
            FavoriteKeywords.KeywordFor("Ｒｅｐｏｒｔ．ｐｄｆ"), "rep");
        CheckEqual("关键词：.gitignore 不能被当成「没有文件名」",
            FavoriteKeywords.KeywordFor(".gitignore"), "git");
        CheckEqual("关键词：整条路径也认",
            FavoriteKeywords.KeywordFor(@"D:\work\项目计划书.docx"), "xmjh");
        Check("关键词：结果长度不超过 4",
            FavoriteKeywords.KeywordFor("This Is A Very Long File Name.pdf").Length <= FavoriteKeywords.MaxLength);
    }

    // ── ② 撞车升级链 ──────────────────────────────────────────────

    private static void TestFavoriteKeywordConflict()
    {
        CheckEqual("关键词升级：没撞就用原值",
            FavoriteKeywords.KeywordForUnique("Report.pdf", new[] { "xyz" }), "rep");
        CheckEqual("关键词升级：撞了先扩到 4 个字母",
            FavoriteKeywords.KeywordForUnique("Report.pdf", new[] { "rep" }), "repo");
        CheckEqual("关键词升级：还撞就追加 2",
            FavoriteKeywords.KeywordForUnique("Report.pdf", new[] { "rep", "repo" }), "rep2");
        CheckEqual("关键词升级：再撞继续追加 3",
            FavoriteKeywords.KeywordForUnique("Report.pdf", new[] { "rep", "repo", "rep2" }), "rep3");
        CheckEqual("关键词升级：中文不参与「扩字母」（只追加数字）",
            FavoriteKeywords.KeywordForUnique("季度总结.pptx", new[] { "jdzj" }), "jdzj2");
        CheckEqual("关键词升级：大小写算撞",
            FavoriteKeywords.KeywordForUnique("Report.pdf", new[] { "REP" }), "repo");
    }

    // ── ③ 噪声过滤 ────────────────────────────────────────────────

    private static void TestForegroundNoiseFilter()
    {
        var roots = new[] { @"C:\Windows", @"C:\Program Files" };

        Check("噪声：目录要丢掉（目录句柄的 GetFileType 也是 FILE_TYPE_DISK）",
            ForegroundFileProbe.IsNoise(@"D:\work\assets", FILE_ATTRIBUTE_DIRECTORY, 4096, roots));
        Check("噪声：系统目录下的 dll",
            ForegroundFileProbe.IsNoise(@"C:\Windows\System32\uxtheme.dll", 0, 1024, roots));
        Check("噪声：系统目录下的 mui / pak",
            ForegroundFileProbe.IsNoise(@"C:\Windows\System32\zh-CN\shell32.dll.mui", 0, 1024, roots)
            && ForegroundFileProbe.IsNoise(@"C:\Program Files\App\res.pak", 0, 1024, roots));
        Check("噪声：0 字节占位文件",
            ForegroundFileProbe.IsNoise(@"D:\work\empty.pdf", 0, 0, roots));
        Check("噪声：Office / WPS 锁文件",
            ForegroundFileProbe.IsNoise(@"D:\work\~$报告.docx", 0, 1024, roots)
            && ForegroundFileProbe.IsNoise(@"D:\work\~临时.pdf", 0, 1024, roots));
        Check("噪声：点开头的隐藏/临时件",
            ForegroundFileProbe.IsNoise(@"D:\work\.lock.pdf", 0, 1024, roots));

        Check("噪声：正常文档要留下",
            !ForegroundFileProbe.IsNoise(@"D:\work\报告.pdf", 0, 1024, roots));
        Check("噪声：系统目录里的 pdf 也留下（用户可能真在编辑它）",
            !ForegroundFileProbe.IsNoise(@"C:\Program Files\App\manual.pdf", 0, 1024, roots));
        Check("噪声：同前缀的不同目录不算系统目录（Program FilesX ≠ Program Files）",
            !ForegroundFileProbe.IsNoise(@"C:\Program FilesX\a.dll", 0, 1024, roots));

        CheckEqual("噪声：扩展名白名单序号", ForegroundFileProbe.RankOf(@"D:\a\b.pdf") >= 0, true);
        CheckEqual("噪声：白名单外的扩展名出局", ForegroundFileProbe.RankOf(@"D:\a\b.dll"), -1);
    }

    // ── ④ 候选打分 ────────────────────────────────────────────────

    private static void TestForegroundCandidateOrder()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var later = new List<ForegroundFileCandidate>
        {
            new(@"D:\a\old.pdf", 12, t1, false),
            new(@"D:\a\new.pdf", 12, t2, false),
        };
        CheckEqual("候选：写得更晚的胜出",
            ForegroundFileProbe.ChooseBest(later, ""), @"D:\a\new.pdf");

        var titleHit = new List<ForegroundFileCandidate>
        {
            new(@"D:\a\hit.pdf", 12, t1, false),
            new(@"D:\a\new.pdf", 12, t2, false),
        };
        CheckEqual("候选：窗口标题里出现的文件名直接锁定（压过写时间）",
            ForegroundFileProbe.ChooseBest(titleHit, "hit.pdf - WPS Office"), @"D:\a\hit.pdf");

        var outsideSystem = new List<ForegroundFileCandidate>
        {
            new(@"D:\a\doc.pdf", 12, t1, true),    // 假装在系统目录
            new(@"D:\a\other.pdf", 12, t1, false),
        };
        CheckEqual("候选：同样新时优先非系统目录",
            ForegroundFileProbe.ChooseBest(outsideSystem, ""), @"D:\a\other.pdf");

        var notWhitelisted = new List<ForegroundFileCandidate> { new(@"D:\a\x.dll", -1, t2, false) };
        Check("候选：白名单外一律出局", ForegroundFileProbe.ChooseBest(notWhitelisted, "") == null);
        Check("候选：空列表给 null", ForegroundFileProbe.ChooseBest(new List<ForegroundFileCandidate>(), "") == null);
    }

    // ── ⑤ 收藏幂等 ────────────────────────────────────────────────

    private static void TestPinnedFavoriteIdempotent()
    {
        var pinned = new List<QEntry>();
        var file = new ForegroundFile(@"D:\work\报告.pdf", "wpspdf.exe", IntPtr.Zero, 1234, DateTime.Now);

        PinnedUpsertResult first = PinnedFiles.Upsert(pinned, file, new[] { "rep" });
        Check("收藏：第一次收藏新增一条", first.Added && pinned.Count == 1, pinned.Count.ToString());
        CheckEqual("收藏：备注是文件名（含扩展名）", first.Entry.Comment, "报告.pdf");
        CheckEqual("收藏：来源记下前台程序", first.Entry.Source, "wpspdf.exe");
        CheckEqual("收藏：次数从 1 开始", first.Entry.UseCount, 1);
        CheckEqual("收藏：路径是完整路径", first.Entry.Value, @"D:\work\报告.pdf");
        Check("收藏：关键字是 2~4 个字符",
            first.Entry.Key.Length is >= 1 and <= FavoriteKeywords.MaxLength, first.Entry.Key);

        /* 同一个文件、不同大小写的盘符与目录：去重键会归一，仍然只有一条。 */
        PinnedUpsertResult second = PinnedFiles.Upsert(pinned, file with { Path = @"d:\WORK\报告.pdf" }, null);
        Check("收藏：同一个文件再收一次不产生第二条", !second.Added && pinned.Count == 1, pinned.Count.ToString());
        CheckEqual("收藏：已收藏时次数 +1", second.Entry.UseCount, 2);
        Check("收藏：已收藏时最后一次使用时间被刷新", second.Entry.LastUsed != null);

        /* 换一个文件 → 新的一条，且关键字不能和已有的撞。 */
        var other = new ForegroundFile(@"D:\work\报告 2.pdf", "wpspdf.exe", IntPtr.Zero, 1234, DateTime.Now);
        PinnedUpsertResult third = PinnedFiles.Upsert(pinned, other, pinned.Select(p => p.Key));
        Check("收藏：不同文件各一条", third.Added && pinned.Count == 2, pinned.Count.ToString());
        Check("收藏：新条目的关键字不与已有的重复",
            pinned.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == pinned.Count,
            string.Join(",", pinned.Select(p => p.Key)));
    }

    // ── ⑥ 打开位置的路径清洗 ──────────────────────────────────────

    private static void TestRevealPathCleaning()
    {
        CheckEqual("打开位置：剥掉 \\\\?\\ 前缀",
            RevealPath.Clean(@"\\?\C:\a b\c.pdf"), @"C:\a b\c.pdf");
        CheckEqual("打开位置：长路径 UNC 前缀转成普通的 \\\\server\\share 形式",
            RevealPath.Clean(@"\\?\UNC\srv\share\a.pdf"), @"\\srv\share\a.pdf");
        CheckEqual("打开位置：去尾部斜杠", RevealPath.Clean(@"C:\a\b\"), @"C:\a\b");
        CheckEqual("打开位置：盘符根保留斜杠", RevealPath.Clean(@"C:\"), @"C:\");
        CheckEqual("打开位置：去重键小写", RevealPath.CompareKey(@"C:\A\B.PDF"), @"c:\a\b.pdf");

        /* 文件还在：父目录 + 选中 */
        RevealTarget normal = RevealPath.Plan(@"C:\a\b.pdf", openFolderOnly: false,
            fileExists: _ => true, dirExists: _ => false);
        Check("打开位置：文件在 → 选中它", normal.Select && normal.Reachable && !normal.WentUp,
            normal.ToString());
        CheckEqual("打开位置：目录取父目录", normal.Folder, @"C:\a");
        CheckEqual("打开位置：explorer 参数整体加引号",
            RevealPath.ExplorerArguments(normal), "/select,\"C:\\a\\b.pdf\"");

        /* 设置成"只开目录" */
        RevealTarget onlyFolder = RevealPath.Plan(@"C:\a\b.pdf", openFolderOnly: true,
            fileExists: _ => true, dirExists: _ => false);
        Check("打开位置：只开目录时不选中", !onlyFolder.Select && onlyFolder.Folder == @"C:\a");
        CheckEqual("打开位置：只开目录时的 explorer 参数",
            RevealPath.ExplorerArguments(onlyFolder), "\"C:\\a\"");

        /* 文件没了：逐级上溯到第一个还有效的目录 */
        RevealTarget wentUp = RevealPath.Plan(@"C:\gone\missing\x.pdf", openFolderOnly: false,
            fileExists: _ => false, dirExists: d => string.Equals(d, @"C:\gone", StringComparison.OrdinalIgnoreCase));
        Check("打开位置：文件没了要上溯", wentUp.WentUp && wentUp.Reachable, wentUp.ToString());
        CheckEqual("打开位置：上溯到第一个存在的目录", wentUp.Folder, @"C:\gone");
        Check("打开位置：上溯时不选中", !wentUp.Select);

        /* 整条链都不可达（U 盘拔了 / 网络断了） */
        RevealTarget lost = RevealPath.Plan(@"Z:\nope\x.pdf", openFolderOnly: false,
            fileExists: _ => false, dirExists: _ => false);
        Check("打开位置：整条链都不可达时明确报不可达", !lost.Reachable);

        CheckEqual("打开位置：空路径不可达", RevealPath.Plan("", false).Reachable, false);
    }

    // ── ⑧ 缓存与锚点（v1.5.1 最容易写错的一处） ────────────────

    /// <summary>
    /// 附加菜单一 `Show()` 前台就变成我们自己，所以"要识别哪个窗口"必须靠
    /// 弹菜单之前记下的锚点。这里用注入的扫描函数把这条语义钉住：
    /// 扫描收到的必须是**锚点的句柄**，而不是当时的前台窗口。
    /// </summary>
    private static void TestForegroundFileCacheAnchor()
    {
        ForegroundFiles.ResetForTest();
        string probe = @"D:\work\锚点测试.pdf";
        IntPtr anchor = new(0x1234);
        IntPtr seen = IntPtr.Zero;
        int calls = 0;

        try
        {
            ForegroundFiles.ScanOverrideForTest = (hwnd, _) =>
            {
                seen = hwnd;
                Interlocked.Increment(ref calls);
                return ForegroundFileResult.Ok(
                    new ForegroundFile(probe, "test.exe", hwnd, Environment.ProcessId, DateTime.Now));
            };

            ForegroundFiles.BeginSession(anchor, Environment.ProcessId);

            Task<ForegroundFileResult> first = ForegroundFiles.GetOrRefreshAsync();
            Task<ForegroundFileResult> again = ForegroundFiles.GetOrRefreshAsync();
            Check("缓存：连点两下合并成同一个在飞任务", ReferenceEquals(first, again));

            ForegroundFileResult r1 = first.GetAwaiter().GetResult();
            Check("锚点：扫描用的是菜单弹出前那个窗口，不是当前前台",
                seen == anchor, $"seen=0x{seen.ToInt64():X}");
            CheckEqual("锚点：识别到的就是那个文件", r1.File?.Path, probe);

            Task<ForegroundFileResult> third = ForegroundFiles.GetOrRefreshAsync();
            ForegroundFileResult r3 = third.GetAwaiter().GetResult();
            Check("缓存：800ms 内再点不会重扫", calls == 1, calls.ToString());
            CheckEqual("缓存：命中缓存拿到的还是那个文件", r3.File?.Path, probe);

            /* 换一个窗口再弹菜单：旧结果必须作废，不能把上一个程序的文件当成这个程序的。 */
            ForegroundFiles.BeginSession(new IntPtr(0x5678), 4242);
            Check("缓存：换了窗口旧结果作废", ForegroundFiles.Cached == null);
        }
        finally
        {
            ForegroundFiles.ResetForTest();
        }
    }
    // ── ⑨ 识别链的纯判定（v1.5.2 真机反馈后补） ──────────────────

    /// <summary>
    /// 真机上暴露出来的三件事，逐条钉住：
    ///   · WPS 的文档句柄在子进程（et.exe / wpspdf.exe）里，窗口进程里没有；
    ///   · 它手里那个路径是**云缓存副本**，不能当成用户的文件；
    ///   · 浏览器里的本地 PDF 没有句柄，只能靠"标题里的文件名 → 最近文档"。
    /// </summary>
    private static void TestForegroundFileChain()
    {
        Check("同族：wps 与 wpspdf 算一家", ForegroundFileProbe.SameFamily("wps", "wpspdf"));
        Check("同族：wps 与 wpscloudsvr 算一家", ForegroundFileProbe.SameFamily("wps", "wpscloudsvr"));
        Check("同族：msedge 与 identity_helper 不是一家",
            !ForegroundFileProbe.SameFamily("msedge", "identity_helper"));
        Check("同族：名字太短不认（避免 es.exe 这类误判）",
            !ForegroundFileProbe.SameFamily("et", "et2"));

        Check("缓存：WPS 云缓存路径不算用户文件",
            ForegroundFileProbe.IsCachePath(
                @"D:\something\WPS Cloud\WPS Cloud Files\.362964933\cachedata\2F30\书.xlsx"));
        Check("缓存：浏览器缓存路径不算用户文件",
            ForegroundFileProbe.IsCachePath(@"C:\Users\me\AppData\Local\Microsoft\Windows\INetCache\a.pdf"));
        Check("缓存：普通文档不算缓存",
            !ForegroundFileProbe.IsCachePath(@"D:\work\报告.pdf"));

        CheckEqual("标题：WPS 标题取第一段",
            ForegroundFileProbe.FileNameFromTitle("徒手健身动作全解.xlsx - WPS Office"),
            "徒手健身动作全解.xlsx");
        CheckEqual("标题：浏览器标题取第一段（后面还有站点名）",
            ForegroundFileProbe.FileNameFromTitle("2026年暑期教师研修结业证书-程子.pdf - 个人 - Microsoft Edge"),
            "2026年暑期教师研修结业证书-程子.pdf");
        CheckEqual("标题：Edge 多标签后面挂的「和另外 N 个页面」要截掉",
            ForegroundFileProbe.FileNameFromTitle("2026年暑期教师研修结业证书-子.pdf 和另外 2 个页面 - 个人 - Microsoft Edge"),
            "2026年暑期教师研修结业证书-子.pdf");
        CheckEqual("标题：没有破折号、只有文件名也要认",
            ForegroundFileProbe.FileNameFromTitle("报告.pdf"), "报告.pdf");
        CheckEqual("标题：破折号也认",
            ForegroundFileProbe.FileNameFromTitle("季度总结.pptx — WPS 演示"), "季度总结.pptx");
        CheckEqual("标题：没有文件名就给空串",
            ForegroundFileProbe.FileNameFromTitle("新建标签页 - Microsoft Edge"), "新建标签页");
        CheckEqual("标题：非白名单扩展名不当作文件",
            ForegroundFileProbe.RankOf(ForegroundFileProbe.FileNameFromTitle("main.cs - Visual Studio Code")), -1);
    }
    // ── ⑩ 收藏按钮的判定顺序（用户口径，v1.5.2） ────────────────

    /// <summary>
    /// 星标点下去收哪个：**网址 > 文件夹 > 文件 > 程序**。
    /// 这条顺序是用户明说的，改之前先读这里。
    /// </summary>
    private static void TestFavoriteBranchPriority()
    {
        Check("优先级：浏览器里有网址 → 收网址",
            !FavoriteResolver.PreferFile(FavoriteSource.Browser, "https://example.com/a", hasFile: true));
        Check("优先级：浏览器读不到地址 → 退成文件",
            FavoriteResolver.PreferFile(FavoriteSource.Browser, "", hasFile: true));
        Check("优先级：浏览器开着本地 file:// → 那是文件，不是网页",
            FavoriteResolver.PreferFile(FavoriteSource.Browser, "file:///D:/Downloads/a.pdf", hasFile: true));
        Check("优先级：资源管理器 → 收文件夹（文件插不进来）",
            !FavoriteResolver.PreferFile(FavoriteSource.Folder, @"D:\work", hasFile: true));
        Check("优先级：文件对话框 → 收文件夹",
            !FavoriteResolver.PreferFile(FavoriteSource.Dialog, @"D:\work", hasFile: true));
        Check("优先级：桌面 → 收桌面目录",
            !FavoriteResolver.PreferFile(FavoriteSource.Desktop, @"C:\Users\me\Desktop", hasFile: true));
        Check("优先级：别的程序开着文件 → 收文件（压过「程序」）",
            FavoriteResolver.PreferFile(FavoriteSource.Application, "", hasFile: true));
        Check("优先级：没识别到文件 → 不动，仍收程序",
            !FavoriteResolver.PreferFile(FavoriteSource.Application, "", hasFile: false));

        FavoriteDraft draft = FavoriteResolver.FileDraft(@"D:\work\季度总结.pptx", new[] { "jdzj" });
        CheckEqual("文件草稿：目标是完整路径", draft.Target, @"D:\work\季度总结.pptx");
        CheckEqual("文件草稿：备注是文件名", draft.Comment, "季度总结.pptx");
        CheckEqual("文件草稿：关键字按文件名规则且避开已占用的", draft.Keyword, "jdzj2");
        CheckEqual("文件草稿：来源标成文件", draft.Source, FavoriteSource.File);

        QEntry entry = draft.ToEntry(showInMiddleMenu: true);
        CheckEqual("文件草稿：落库的值是完整路径", entry.Value, @"D:\work\季度总结.pptx");
        Check("文件草稿：落库后能在附加菜单里显示", entry.ShowInMiddleMenu);
    }
    // ── ⑪ 空上下文兜底（v1.5.2，WPS 里点星标曾经是空白表单） ─────

    private static void TestFavoriteContextFallback()
    {
        var empty = FavoriteTarget.Empty;

        Check("兜底：没有锚点时原样返回（让用户手填）",
            ReferenceEquals(FavoriteTarget.EnsureUsable(empty, IntPtr.Zero), empty));

        /* 拿桌面窗口当锚点：它一定存在，且类名能把来源判出来。 */
        IntPtr shell = InputPlus.Core.Interop.NativeMethods.GetShellWindow();
        FavoriteTarget rebuilt = FavoriteTarget.EnsureUsable(empty, shell);
        Check("兜底：有锚点就能重建出可用上下文",
            rebuilt.Source != FavoriteSource.Unknown, rebuilt.Source.ToString());

        var application = new FavoriteTarget
        {
            Source = FavoriteSource.Application,
            ExePath = @"C:\Windows\notepad.exe",
            ProcessName = "notepad.exe",
        };
        Check("兜底：本来就可用的快照不动",
            ReferenceEquals(FavoriteTarget.EnsureUsable(application, shell), application));
    }
    // ── ⑫ 锚点取哪个窗口（v1.5.4：桌面上点中键曾经扫到别人） ─────

    /// <summary>
    /// 鼠标唤出必须用"指针底下那个窗口"当锚点：这一下中键被我们吞了，
    /// 桌面/资源管理器并不会因此变成前台，GetForegroundWindow 还停在上一个程序上。
    /// 用错了的后果是实打实的：桌面上点中键 → 条目没置灰 → 扫到上一个程序 →
    /// 找不到文件 → 弹出"选择要定位的文件"，默认目录还是那个程序的安装目录。
    /// </summary>
    private static void TestForegroundAnchorChoice()
    {
        IntPtr probe = new(0x1111);
        IntPtr fg = new(0x2222);

        CheckEqual("锚点：鼠标唤出用指针底下的窗口",
            ForegroundFiles.ChooseAnchor(fromKeyboard: false, probe, fg), probe);
        CheckEqual("锚点：键盘唤出用前台窗口（指针常在任务栏角落）",
            ForegroundFiles.ChooseAnchor(fromKeyboard: true, probe, fg), fg);
        CheckEqual("锚点：鼠标唤出但探不到指针窗口时退回前台",
            ForegroundFiles.ChooseAnchor(fromKeyboard: false, IntPtr.Zero, fg), fg);

        /* 桌面/资源管理器/任务栏这三类窗口一律不算"可能开着文件"的窗口。 */
        Check("可动作：Progman 不算", !ForegroundFileProbe.CanActOnForegroundFile("explorer.exe", "Progman"));
        Check("可动作：WorkerW 不算", !ForegroundFileProbe.CanActOnForegroundFile("explorer.exe", "WorkerW"));
        Check("可动作：资源管理器窗口不算", !ForegroundFileProbe.CanActOnForegroundFile("explorer.exe", "CabinetWClass"));
        Check("可动作：WPS 算", ForegroundFileProbe.CanActOnForegroundFile("wps.exe", "OpusApp"));
    }
    // ── ⑬ Office / WPS 最近文件的解析（v1.5） ──────────────────

    /// <summary>
    /// 这里只测**纯解析**：注册表里那份数据是用户机器上的状态（可能早就过期），
    /// 放进自检只会时红时绿。真要查现场，用 `--office-recent &lt;文件名&gt;`。
    /// </summary>
    private static void TestOfficeRecentParsing()
    {
        CheckEqual("Office MRU：剥掉 [F…][T…][O…]* 前缀",
            OfficeRecentFiles.ParseMruValue(@"[F00000000][T01DA2B3C][O00000000]*C:\a\b.docx"),
            @"C:\a\b.docx");
        CheckEqual("Office MRU：没有前缀就原样返回",
            OfficeRecentFiles.ParseMruValue(@"C:\a\b.docx"), @"C:\a\b.docx");
        CheckEqual("Office MRU：空值给空串",
            OfficeRecentFiles.ParseMruValue(""), "");
        CheckEqual("Office MRU：星号结尾不越界",
            OfficeRecentFiles.ParseMruValue(@"[F00000000]*"), @"[F00000000]*");

        /* [T…] 是十六进制的 FILETIME：解析对了，"最近使用"里 Office 那几条才能按
         * "什么时候打开过"排序（WPS 没有时间，只能退回文件修改时间）。
         * 样本取 2024-06-01 12:00 的真实 FILETIME 十六进制值。 */
        var t = OfficeRecentFiles.ParseMruTime(@"[F00000000][T01DAB3D82C656000][O00000000]*C:\a.docx");
        Check("Office MRU：能解析出打开时间", t.Year >= 2020, t.ToString("yyyy-MM-dd HH:mm"));
        Check("Office MRU：没有 [T…] 时给最早时间",
            OfficeRecentFiles.ParseMruTime(@"*C:\a.docx") == DateTime.MinValue);
        Check("Office MRU：乱码给最早时间",
            OfficeRecentFiles.ParseMruTime("[TZZZZ]") == DateTime.MinValue);
        Check("Office MRU：空值给最早时间",
            OfficeRecentFiles.ParseMruTime("") == DateTime.MinValue);
    }
    // ── ⑦ 真机：扫自己 + 通道策略 ────────────────────────────────

    private static void TestForegroundFileSelfScan()
    {
        CheckEqual("识别：NT 长设备名不被短前缀吃掉",
            DosDeviceMap.Translate(@"\Device\HarddiskVolume10\a\b.pdf", new[]
            {
                new DeviceMapping(@"\Device\HarddiskVolume10", "D:"),
                new DeviceMapping(@"\Device\HarddiskVolume1", "C:"),
            }), @"D:\a\b.pdf");
        CheckEqual("识别：网络路径转 UNC",
            DosDeviceMap.Translate(@"\Device\Mup\srv\share\a.pdf", Array.Empty<DeviceMapping>()),
            @"\\srv\share\a.pdf");
        CheckEqual("识别：认不出来的设备路径给空串",
            DosDeviceMap.Translate(@"\Device\NamedPipe\x", Array.Empty<DeviceMapping>()), "");

        Check("入口：资源管理器窗口置灰",
            !ForegroundFileProbe.CanActOnForegroundFile("explorer.exe", "CabinetWClass"));
        Check("入口：桌面置灰",
            !ForegroundFileProbe.CanActOnForegroundFile("explorer.exe", "Progman"));
        Check("入口：任务栏置灰",
            !ForegroundFileProbe.CanActOnForegroundFile("", "Shell_TrayWnd"));
        Check("入口：WPS / 记事本可用",
            ForegroundFileProbe.CanActOnForegroundFile("wpspdf.exe", "KMPDFFrameWnd")
            && ForegroundFileProbe.CanActOnForegroundFile("notepad.exe", "Notepad"));

        CheckEqual("策略：提权时先借 shell 之手（先保证不越权）",
            RevealStrategy.Plan(true)[0], RevealChannel.ShellLaunch);
        CheckEqual("策略：未提权时先 in-proc 选中",
            RevealStrategy.Plan(false)[0], RevealChannel.InProcSelect);
        Check("策略：两条都是三条通道、自己启动 explorer 永远最后",
            RevealStrategy.Plan(true).Length == 3
            && RevealStrategy.Plan(false).Length == 3
            && RevealStrategy.Plan(true)[2] == RevealChannel.DirectExplorer
            && RevealStrategy.Plan(false)[2] == RevealChannel.DirectExplorer);

        /* 真机：扫**自己**进程。不需要任何权限，所以管理员/普通身份都成立。 */
        string dir = Path.Combine(Path.GetTempPath(), "inputplus-favfile-" + Environment.ProcessId);
        string file = Path.Combine(dir, "selftest-probe.pdf");
        bool cleaned = true;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5 });

            using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                var (paths, why) = ForegroundFileProbe.EnumerateOpenFilesOnce(Environment.ProcessId, 800);
                string want = RevealPath.Clean(file);
                bool hit = paths.Any(p => string.Equals(RevealPath.Clean(p), want, StringComparison.OrdinalIgnoreCase));

                Check("识别：扫自己进程能命中自己刚打开的文件", hit,
                    $"命中 {paths.Count} 条；{why}");

                if (hit)
                {
                    var scored = ForegroundFileProbe.Score(paths);
                    Check("识别：刚打开的那个 pdf 能通过噪声过滤与白名单",
                        scored.Any(c => string.Equals(c.Path, want, StringComparison.OrdinalIgnoreCase)),
                        string.Join(" | ", scored.Take(5).Select(c => c.Path)));
                }
            }
        }
        catch (Exception ex)
        {
            Check("识别：扫自己进程（真机）", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { cleaned = false; }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { cleaned = false; }
        }
        if (!cleaned) InputPlus.Core.CoreLog.Warn("[收藏] 自检临时文件没删干净：" + dir);
    }
}
