using System;
using System.IO;
using InputPlus.Core.Windows;

namespace InputPlus;

/// <summary>
/// 自检：微信图片/视频的定位（v1.7.9）与「打开文件所在位置」的最后一级退路。
///
/// 两件事都**不碰真机微信**：
///   · 微信那条用一棵假数据树（<see cref="WeChatMedia.RootsOverrideForTest"/> +
///     <see cref="WeChatMedia.NowForTest"/>），文件时间由 File.SetLastWriteTime 钉死；
///   · 程序目录那条只问**自己这个进程**，任何权限身份都成立。
/// </summary>
public static partial class SelfTest
{
    private static void TestWeChatMedia()
    {
        /* ── ① 认人 ───────────────────────────────────────────── */
        CheckEqual("微信：weixin.exe 认得出", WeChatMedia.IsWeChatProcess("Weixin.exe"), true);
        CheckEqual("微信：WeChatAppEx（小程序容器）也认", WeChatMedia.IsWeChatProcess("WeChatAppEx.exe"), true);
        CheckEqual("微信：qq.exe 不认", WeChatMedia.IsWeChatProcess("qq.exe"), false);
        CheckEqual("微信：路径里带 Tencent\\xwechat 就认",
            WeChatMedia.IsWeChatPath(@"D:\software\Weixin\4.2.0.53\Weixin.exe"), true);
        CheckEqual("微信：别的程序路径不认",
            WeChatMedia.IsWeChatPath(@"C:\Windows\explorer.exe"), false);

        /* ── ② 找"最近的那一个"（假数据树）────────────────────── */
        string root = Path.Combine(Path.GetTempPath(), "inputplus-wechat-" + Environment.ProcessId);
        string account = Path.Combine(root, "xwechat_files", "wxid_selftest_0613");
        string attach = Path.Combine(account, "msg", "attach", "chat0001", "2026-11", "Img");
        string video = Path.Combine(account, "msg", "video", "2026-11");
        string head = Path.Combine(account, "temp", "head_image");
        DateTime now = new(2026, 11, 28, 20, 0, 0, DateTimeKind.Local);

        try
        {
            Directory.CreateDirectory(attach);
            Directory.CreateDirectory(video);
            Directory.CreateDirectory(head);

            string full = Path.Combine(attach, "6a9ff2777dbcb522f4b2ef279db6f5f7.dat");
            string thumb = Path.Combine(attach, "6a9ff2777dbcb522f4b2ef279db6f5f7_t.dat");
            string mp4 = Path.Combine(video, "de0af3168e9ed0f5de103155c2b9b822.mp4");
            string avatar = Path.Combine(head, "d6ac274e8821ddf40f822.png");

            WriteProbeFile(full, 120_000, now.AddMinutes(-40));
            /* 缩略图与正片是**同一秒**写出来的（实测），这里让它晚 2 秒，
             * 验证排序里的"缩略图压 30 分钟"确实起作用。 */
            WriteProbeFile(thumb, 6_000, now.AddMinutes(-40).AddSeconds(2));
            WriteProbeFile(mp4, 1_500_000, now.AddHours(-3));
            WriteProbeFile(avatar, 9_000, now);                    // 头像最新：不在 msg 子树里，绝不选它

            WeChatMedia.RootsOverrideForTest = new[] { account };
            WeChatMedia.NowForTest = now;

            string? hit = WeChatMedia.FindCurrent(2000);
            Check("微信：挑出最近的那张图片（不是缩略图、不是头像）",
                hit != null && string.Equals(Path.GetFileName(hit), Path.GetFileName(full), StringComparison.OrdinalIgnoreCase),
                hit ?? "(null)");

            /* 视频更新时应该换人：把 mp4 的时间顶到最近。 */
            File.SetLastWriteTime(mp4, now.AddMinutes(-2));
            string? hit2 = WeChatMedia.FindCurrent(2000);
            Check("微信：视频更近就挑视频",
                hit2 != null && string.Equals(Path.GetFileName(hit2), Path.GetFileName(mp4), StringComparison.OrdinalIgnoreCase),
                hit2 ?? "(null)");

            /* 一周之前的媒体不认（"最近在看的"和七天前的文件已经没关系了）。 */
            File.SetLastWriteTime(full, now.AddDays(-30));
            File.SetLastWriteTime(thumb, now.AddDays(-30));
            File.SetLastWriteTime(mp4, now.AddDays(-30));
            CheckEqual<string?>("微信：超过一周的媒体不认", WeChatMedia.FindCurrent(2000), null);

            /* v1.7.18：看图窗口那条路只认 6 小时内的媒体 —— 普通聊天窗口里点
             * 「打开文件所在位置」不该被几天前收过图的会话目录骗走（用户报的"不知名的文件夹"）。 */
            File.SetLastWriteTime(full, now.AddHours(-3));
            File.SetLastWriteTime(thumb, now.AddHours(-3));
            File.SetLastWriteTime(mp4, now.AddHours(-3));
            Check("微信：看图时只认 6 小时内的媒体（3 小时前的能找到）",
                WeChatMedia.FindCurrent(2000, default, WeChatMedia.ViewerMaxAge) != null);
            File.SetLastWriteTime(full, now.AddDays(-2));
            File.SetLastWriteTime(thumb, now.AddDays(-2));
            File.SetLastWriteTime(mp4, now.AddDays(-2));
            CheckEqual<string?>("微信：看图时 2 天前的媒体不认（会退到微信 exe 目录）",
                WeChatMedia.FindCurrent(2000, default, WeChatMedia.ViewerMaxAge), null);

            /* 数据目录里没有 msg 子树 → 一眼就返回 null（不能把整盘翻一遍）。 */
            string empty = Path.Combine(root, "xwechat_files", "wxid_empty");
            Directory.CreateDirectory(empty);
            WeChatMedia.RootsOverrideForTest = new[] { empty };
            CheckEqual<string?>("微信：空账号目录直接返回 null", WeChatMedia.FindCurrent(500), null);
        }
        catch (Exception ex)
        {
            Check("微信：假数据树里的定位", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            WeChatMedia.ResetForTest();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        /* ── ③ 程序那条退路（v1.7.14：给的是 exe，好让资源管理器选中它）── */
        string hostExe = ForegroundFileProbe.HostExeOf(Environment.ProcessId, "InputPlus.exe");
        Check("退路：能问出自己这个进程的 exe 路径",
            hostExe.Length > 0 && File.Exists(hostExe), hostExe);

        string byFamily = ForegroundFileProbe.ProcessExePathByFamily("InputPlus.exe");
        Check("退路：按同名进程也能拿到 exe 路径", byFamily.Length > 0, byFamily);

        CheckEqual("退路：不存在的进程名不返回路径",
            ForegroundFileProbe.ProcessExePathByFamily("no-such-app-xyz.exe"), "");

        /* exe 交给 RevealPath：必须"选中这个 exe、打开它的父目录"。 */
        if (hostExe.Length > 0)
        {
            var target = RevealPath.Plan(hostExe, openFolderOnly: false);
            Check("退路：exe 路径 → 打开父目录并选中它",
                target.Reachable && !target.WentUp && target.Select
                && string.Equals(target.Path, RevealPath.Clean(hostExe), StringComparison.OrdinalIgnoreCase)
                && string.Equals(target.Folder, RevealPath.Clean(Path.GetDirectoryName(hostExe) ?? ""), StringComparison.OrdinalIgnoreCase),
                $"{target.Path} @ {target.Folder} select={target.Select} wentUp={target.WentUp}");

            /* 目录本身进来时仍然"打开它自己、不选中"（v1.7.10 的规则没变）。 */
            string hostDir = Path.GetDirectoryName(hostExe) ?? "";
            if (hostDir.Length > 0)
            {
                var dirPlan = RevealPath.Plan(hostDir, openFolderOnly: true);
                Check("退路：目录路径 → 打开目录本身（不上溯）",
                    dirPlan.Reachable && !dirPlan.WentUp && !dirPlan.Select
                    && string.Equals(dirPlan.Folder, RevealPath.Clean(hostDir), StringComparison.OrdinalIgnoreCase),
                    $"{dirPlan.Folder} wentUp={dirPlan.WentUp} select={dirPlan.Select}");
            }
        }
    }

    private static void WriteProbeFile(string path, int size, DateTime written)
    {
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTime(path, written);
    }

    /// <summary>
    /// 「打开文件所在位置」的决策表（<see cref="RevealFallbackPlan.Decide"/>，纯函数）。
    ///
    /// 四条顺序不许乱：认出文件 &gt; 桌面/资源管理器 &gt; 程序目录 &gt; 什么也不做。
    /// 这里把"程序目录"注入成假函数 —— 自检不能依赖真机上跑着什么程序。
    /// </summary>
    private static void TestRevealFallback()
    {
        /* 假实现返回的是**exe 路径**（v1.7.14 起退路给的是 exe，好让资源管理器选中它）。 */
        static string FakeExe(int pid, string? proc) => @"D:\fake\" + proc;

        // ① 认出文件 → 定位文件，folderOnly 跟着设置走
        var file = new ForegroundFile(@"D:\docs\季度总结.pptx", "wps.exe", IntPtr.Zero, 11, DateTime.Now);
        var hit = RevealFallbackPlan.Decide(
            new ForegroundFileResult(file, ForegroundFileOutcome.Ok, "", 11), 11, "wps.exe", folderOnly: false, FakeExe);
        CheckEqual("退路：认出文件时走文件本身", hit.Kind, RevealFallbackKind.File);
        CheckEqual("退路：文件路径原样传给资源管理器", hit.Path, @"D:\docs\季度总结.pptx");
        CheckEqual("退路：设置是「选中」就选中", hit.FolderOnly, false);

        var folderOnlyHit = RevealFallbackPlan.Decide(
            new ForegroundFileResult(file, ForegroundFileOutcome.Ok, "", 11), 11, "wps.exe", folderOnly: true, FakeExe);
        CheckEqual("退路：设置是「只开目录」就不选中", folderOnlyHit.FolderOnly, true);

        // ② 桌面 / 资源管理器 → 什么也不做（绝不能开 C:\Windows）
        var shell = RevealFallbackPlan.Decide(
            ForegroundFileResult.Fail(ForegroundFileOutcome.NotActionable, "explorer", 99),
            99, "explorer.exe", folderOnly: false,
            (_, _) => { throw new InvalidOperationException("桌面/资源管理器不该去问程序目录"); });
        CheckEqual("退路：桌面/资源管理器不给结论", shell.Kind, RevealFallbackKind.None);
        CheckEqual("退路：桌面/资源管理器不给路径", shell.Path, "");

        // ③ 认不出文件 + 是个真程序 → 打开程序所在文件夹**并选中 exe**
        var fallback = RevealFallbackPlan.Decide(
            ForegroundFileResult.Fail(ForegroundFileOutcome.NoCandidate, "75 个句柄 0 候选", 42),
            7, "weixin.exe", folderOnly: false, FakeExe);
        CheckEqual("退路：认不出文件时退到程序本身", fallback.Kind, RevealFallbackKind.HostExe);
        CheckEqual("退路：程序路径按 result.Pid 取（不是外面临时读的那个）", fallback.Path, @"D:\fake\weixin.exe");
        CheckEqual("退路：退路必须**选中** exe（不是只开目录）", fallback.FolderOnly, false);

        // ④ 连程序目录都拿不到 → 什么也不做
        var nothing = RevealFallbackPlan.Decide(
            ForegroundFileResult.Fail(ForegroundFileOutcome.Denied, "OpenProcess 错误 5", 42),
            42, "weixin.exe", folderOnly: false, (_, _) => "");
        CheckEqual("退路：取不到程序目录时不给结论", nothing.Kind, RevealFallbackKind.None);

        /* ── v1.7.10：目录目标不能用 in-proc 选中（它会打开上级）────────────
         *
         * 现场：点"打开 exe 所在文件夹"，资源管理器开的是 D:\software 并把 Kelivo 选中 ——
         * 因为 SHOpenFolderAndSelectItems 的语义就是"在父目录里选中这一项"。
         * 这两条把"目录 → 不走 in-proc、走 explorer 那条"钉死。 */
        CheckEqual("打开位置：只开目录时跳过 in-proc 选中（那个 API 会打开上级）",
            RevealStrategy.ShouldTry(RevealChannel.InProcSelect, select: false), false);
        CheckEqual("打开位置：要选中文件时 in-proc 选中照常",
            RevealStrategy.ShouldTry(RevealChannel.InProcSelect, select: true), true);
        CheckEqual("打开位置：只开目录时仍然可以借 shell 之手",
            RevealStrategy.ShouldTry(RevealChannel.ShellLaunch, select: false), true);
        CheckEqual("打开位置：直接启 explorer 那条也照常",
            RevealStrategy.ShouldTry(RevealChannel.DirectExplorer, select: false), true);

        /* 目标本身就是目录时，RevealPath 必须给出"不选中、目录就是它自己"——
         * 上面那条 skip 规则的前提就是这三个字段。 */
        string tempDir = Path.Combine(Path.GetTempPath(), "inputplus-reveal-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(tempDir);
            var dirPlan = RevealPath.Plan(tempDir, openFolderOnly: false);
            Check("打开位置：目录输入 → 打开目录本身、不选中",
                dirPlan.Reachable && !dirPlan.WentUp && !dirPlan.Select
                && string.Equals(dirPlan.Folder, RevealPath.Clean(tempDir), StringComparison.OrdinalIgnoreCase),
                $"folder={dirPlan.Folder} select={dirPlan.Select} wentUp={dirPlan.WentUp}");
            CheckEqual("打开位置：目录输入的 explorer 参数就是目录本身",
                RevealPath.ExplorerArguments(dirPlan), "\"" + RevealPath.Clean(tempDir) + "\"");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        /* ── v1.7.10：系统组件不给退路（别把用户送到 C:\Windows\System32）── */
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        CheckEqual("退路：普通程序 → 取它的 exe 目录",
            ForegroundFileProbe.FolderForHostExe(@"D:\software\Kelivo\Kelivo.exe"), @"D:\software\Kelivo");
        CheckEqual("退路：System32 里的系统组件不给退路",
            ForegroundFileProbe.FolderForHostExe(Path.Combine(win, "System32", "notepad.exe")), "");
        CheckEqual("退路：SystemApps 里的搜索框也不给退路",
            ForegroundFileProbe.FolderForHostExe(Path.Combine(win, "SystemApps", "SearchHost", "SearchHost.exe")), "");
        CheckEqual("退路：空路径给空", ForegroundFileProbe.FolderForHostExe("   "), "");
    }
}
