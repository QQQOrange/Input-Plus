using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using InputPlus.Core.Qbar.Content;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 正文索引的命令行入口（`--content-status` / `--content-build` / `--content-query`）。
///
/// 为什么单独给三个开关：正文索引的失败模式全在**后台**（抽不出来、PDF 加密、
/// 目录没权限、索引目录写不进去），GUI 上只能看到一句状态。用户报"搜正文没结果"时，
/// 让他在命令行跑一次，结果直接落成文件，比远程猜快得多。
///
/// 输出同时写 stdout 与 `%TEMP%\clp-content-*.txt`：WinExe 从资源管理器启动时没有
/// 控制台，但持久化到文件的那一份永远拿得到（与 `--index-status` 同一条约定）。
/// </summary>
internal static class ContentCli
{
    internal static int RunStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Input Plus 正文索引体检");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();

        ContentIndex.Start();
        var watch = Stopwatch.StartNew();
        while (!ContentIndex.Ready && ContentIndex.Building && watch.Elapsed < TimeSpan.FromSeconds(20))
            Thread.Sleep(200);

        sb.AppendLine("开关: " + (ContentIndex.Enabled ? "开" : "关"));
        sb.AppendLine("状态: " + ContentIndex.Status);
        if (ContentIndex.Problem.Length > 0) sb.AppendLine("问题: " + ContentIndex.Problem);
        sb.AppendLine("规模: " + ContentIndex.FormatCount(ContentIndex.Docs) + " 条，"
                      + ContentIndex.FormatSize(ContentIndex.Bytes));
        sb.AppendLine("目录: " + ContentIndex.IndexDir);
        sb.AppendLine("建立中: " + ContentIndex.Building + (ContentIndex.Progress >= 0 ? "（" + ContentIndex.Progress + "%）" : ""));
        sb.AppendLine("失败: " + ContentIndex.Failed + " 个（抽不出正文），跳过: " + ContentIndex.Skipped + " 个（不在白名单/超上限）");
        if (ContentIndex.FailReasons.Length > 0) sb.AppendLine("失败原因: " + ContentIndex.FailReasons);
        sb.AppendLine("最近提交: " + ContentIndex.LastCommitMs + " ms");
        sb.AppendLine("就绪: " + ContentIndex.Ready);
        sb.AppendLine("根目录(" + ContentIndex.Roots.Count + "): " + string.Join(" ；", ContentIndex.Roots));

        var stats = ContentNative.Stats();
        if (stats.Ok)
        {
            sb.AppendLine();
            sb.AppendLine("索引层: schema=" + stats.SchemaVersion + " 段=" + stats.Segments + " 条=" + stats.Docs);        }
        else if (stats.Error.Length > 0)
        {
            sb.AppendLine("索引层错误: " + stats.Error);
        }

        return Write("clp-content-status.txt", sb);
    }

    internal static int RunBuild(bool rebuild)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Input Plus 正文索引构建");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        ContentIndex.Start();
        if (rebuild)
        {
            sb.AppendLine("模式: 丢弃现有索引并重建");
            ContentIndex.RequestRebuild();
        }
        else
        {
            sb.AppendLine("模式: 沿用/继续（没有索引时自动建）");
        }

        var watch = Stopwatch.StartNew();
        int lastPct = -1;
        while (ContentIndex.Building && watch.Elapsed < TimeSpan.FromMinutes(60))
        {
            Thread.Sleep(500);
            int pct = ContentIndex.Progress;
            if (pct != lastPct && pct >= 0)
            {
                lastPct = pct;
                Console.WriteLine("正文索引 " + pct + "%");
            }
        }

        sb.AppendLine("耗时: " + (int)watch.Elapsed.TotalSeconds + " s");
        sb.AppendLine("结果: " + ContentIndex.Status);
        if (ContentIndex.Problem.Length > 0) sb.AppendLine("问题: " + ContentIndex.Problem);
        sb.AppendLine("条数: " + ContentIndex.Docs);
        sb.AppendLine("体积: " + ContentIndex.FormatSize(ContentIndex.Bytes));
        sb.AppendLine("失败: " + ContentIndex.Failed + " 个，跳过: " + ContentIndex.Skipped + " 个");
        if (ContentIndex.FailReasons.Length > 0) sb.AppendLine("失败原因: " + ContentIndex.FailReasons);
        sb.AppendLine("根目录(" + ContentIndex.Roots.Count + "): " + string.Join(" ；", ContentIndex.Roots));
        sb.AppendLine("就绪: " + ContentIndex.Ready);
        return Write("clp-content-build.txt", sb);
    }

    internal static int RunQuery(string text)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Input Plus 正文检索");
        sb.AppendLine("查询: " + text);
        sb.AppendLine();

        ContentIndex.Start();
        var watch = Stopwatch.StartNew();
        while (!ContentIndex.Ready && ContentIndex.Building && watch.Elapsed < TimeSpan.FromMinutes(10))
            Thread.Sleep(300);

        if (!ContentIndex.Ready)
        {
            sb.AppendLine("正文索引未就绪: " + ContentIndex.Status);
            if (ContentIndex.Problem.Length > 0) sb.AppendLine("问题: " + ContentIndex.Problem);
            return Write("clp-content-query.txt", sb);
        }

        var query = new ContentQuery();
        query.Terms.Add(text);
        ContentSearchResult result = ContentIndex.Search(query, 50);
        sb.AppendLine("成功: " + result.Ok + " · " + result.Ms.ToString("0.0") + " ms · 命中 " + result.Total + " 条");
        if (result.Error.Length > 0) sb.AppendLine("错误: " + result.Error);
        foreach (ContentHit hit in result.Hits)
        {
            sb.AppendLine("  " + (hit.IsDir == 1 ? "[目录] " : "[文件] ") + hit.Path);
        }
        return Write("clp-content-query.txt", sb);
    }

    internal static int RunExtract(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Input Plus 正文抽取测试");
        sb.AppendLine("文件: " + path);

        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        sb.AppendLine("类型: " + (ext.Length == 0 ? "(无扩展名)" : ext)
                      + "，支持: " + TextExtractor.Supports(ext));

        var watch = Stopwatch.StartNew();
        bool ok = TextExtractor.TryExtract(path, ext, 8L * 1024 * 1024, 200, out string text, out string reason);
        watch.Stop();

        sb.AppendLine("结果: " + (ok ? "成功" : "失败") + " · " + watch.ElapsedMilliseconds + " ms · " + text.Length + " 字");
        if (!ok) sb.AppendLine("原因: " + reason);
        if (ok)
        {
            string head = text.Length <= 300 ? text : text.Substring(0, 300);
            sb.AppendLine("开头: " + head.Replace("\r", " ").Replace("\n", " "));
        }
        return Write("clp-content-extract.txt", sb);
    }

    private static int Write(string fileName, StringBuilder sb)
    {
        string text = sb.ToString();
        try { Console.WriteLine(text); } catch { /* 没有控制台 */ }
        try
        {
            string path = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllText(path, text, Encoding.UTF8);
        }
        catch { /* 写不出来也不影响退出码 */ }
        return 0;
    }
}
