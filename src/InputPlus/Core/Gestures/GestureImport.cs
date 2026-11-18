using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 手势表整表替换（--gesture-import &lt;文件&gt; [--keep]）。
///
/// 回答的问题："我不想一条条手点，能不能把 WGestures 2 那套手势原样搬过来？"
/// 输入可以是 .wg28bw（WGestures 2 的备份包，本质是 zip，里面装着 gestures.json），
/// 也可以直接是解出来的 gestures.json。
///
/// 默认**先清空再导入**：用户要的是"换成 WG2 那一套"，不是"在旧表上再叠一套"——
/// 叠上去只会让两条同形手势互相抢，正是识别不稳的一大来源。
/// 加 --keep 才退回追加模式（按签名去重）。
///
/// 输出：控制台（如果能挂上）、数据目录 logs\gesture-import.txt。
/// 退出码：0 成功；1 导入 0 条；2 参数/文件有问题；3 异常。
/// </summary>
internal static class GestureImport
{
    private const string Flag = "--gesture-import";

    public static int Run(string[] args)
    {
        string outPath = Path.Combine(AppPaths.DataDir, "gesture-import.txt");
        var sb = new StringBuilder();
        sb.AppendLine("Input Plus · 手势表导入");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"数据目录：{AppPaths.DataDir}");
        sb.AppendLine();

        string? temp = null;
        try
        {
            string? source = Value(args);
            bool keep = CliFlags.Has(args, "--keep");

            if (string.IsNullOrWhiteSpace(source))
            {
                sb.AppendLine($"缺少参数。用法：--gesture-import <gestures.json 或 .wg28bw> [--keep]");
                return Finish(sb, outPath, 2);
            }
            if (!File.Exists(source))
            {
                sb.AppendLine($"文件不存在：{source}");
                return Finish(sb, outPath, 2);
            }

            sb.AppendLine($"来源：{source}（{new FileInfo(source).Length} 字节）");
            string json = Unwrap(source, ref temp, sb);

            var settings = SettingsStore.Load();
            settings.Gestures ??= new List<MouseGesture>();
            int before = settings.Gestures.Count;
            sb.AppendLine($"替换前：{before} 条手势");
            if (!keep)
            {
                settings.Gestures.Clear();
                sb.AppendLine("已清空原有手势（--keep 可改成追加）");
            }

            var (imported, skipped) = WGesturesImporter.ImportFile(json, settings);
            sb.AppendLine($"导入：{imported} 条；跳过：{skipped} 条（翻译不了的动作类型）");
            sb.AppendLine($"替换后：{settings.Gestures.Count} 条手势");

            /* 启动时 GestureDefaults.Ensure 会按"同按钮 + 同方向串"去重，
             * 这里先把会被它吃掉的条目点出来，免得用户以为导入丢了。 */
            var dupes = settings.Gestures
                .Select(g => (g, sig: GestureIdentity.Signature(g)))
                .GroupBy(x => x.sig, StringComparer.OrdinalIgnoreCase)
                .Where(grp => grp.Count() > 1)
                .ToList();
            if (dupes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("注意：下面这些条目触发方式完全相同，启动时只会保留第一条：");
                foreach (var grp in dupes)
                    sb.AppendLine("  " + string.Join(" / ", grp.Select(x => x.g.Name)));
            }

            sb.AppendLine();
            sb.AppendLine("== 导入结果 ==");
            foreach (var g in settings.Gestures)
            {
                string where = string.IsNullOrWhiteSpace(g.AppPath) ? "全局" : Path.GetFileNameWithoutExtension(g.AppPath);
                string trigger = g.Kind switch
                {
                    "stroke" => $"笔画 {g.Button}{(string.IsNullOrEmpty(g.Chord) ? "" : "+" + g.Chord)} {g.Pattern}",
                    "corner" => $"角落 {g.Corner}",
                    "hotcorner" => $"热角 {g.Corner}",
                    "wheel" => $"滚轮 {g.Wheel}",
                    "edgewheel" => $"贴边滚轮 {g.Edge}/{g.Wheel}",
                    _ => g.Kind
                };
                sb.AppendLine($"  {g.Name}　[{where}]　{trigger}　→ {g.Action}");
            }

            /* 导入器已经不再写老动作了（见 WGesturesImporter），这里再兜一次底：
             * 保证落盘的每一条都是界面上选得出来的正式动作，不会留下"旧功能"。 */
            int migrated = InputPlus.Core.Actions.LegacyActionMigration.Apply(settings);
            if (migrated > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"老动作已换成正式动作：{migrated} 处（参数不变）");
            }

            bool saved = SettingsStore.Save(settings);
            sb.AppendLine();
            sb.AppendLine(saved ? "已写盘：settings.json" : "写盘失败（见日志）");
            return Finish(sb, outPath, imported > 0 ? 0 : 1);
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("导入异常：" + ex);
            return Finish(sb, outPath, 3);
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch { /* 临时文件删不掉不影响结果 */ }
            }
        }
    }

    /// <summary>取开关后面的那个参数（跳过其他以 -- 开头的开关）。</summary>
    private static string? Value(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        for (int j = i + 1; j < args.Length; j++)
        {
            if (args[j].StartsWith("--", StringComparison.Ordinal)) continue;
            return args[j];
        }
        return null;
    }

    /// <summary>
    /// WGestures 2 的备份是 zip（.wg28bw）。把里面的 gestures.json 解到临时文件，
    /// 再交给导入器 —— 这样用户直接给备份包就行，不用自己去解压。
    /// </summary>
    private static string Unwrap(string path, ref string? temp, StringBuilder sb)
    {
        using var probe = File.OpenRead(path);
        if (probe.Length < 4) return path;

        Span<byte> magic = stackalloc byte[2];
        probe.ReadExactly(magic);
        probe.Position = 0;
        if (magic[0] != (byte)'P' || magic[1] != (byte)'K') return path;   // 不是 zip，按 json 处理

        using var zip = new ZipArchive(probe, ZipArchiveMode.Read);
        var entry = zip.Entries
            .FirstOrDefault(e => e.Name.Equals("gestures.json", StringComparison.OrdinalIgnoreCase))
            ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                               && e.Length < 8 * 1024 * 1024);
        if (entry == null)
        {
            sb.AppendLine("压缩包里没有 gestures.json");
            throw new InvalidDataException("压缩包里没有 gestures.json");
        }

        temp = Path.Combine(Path.GetTempPath(), "ke-import-" + Guid.NewGuid().ToString("N") + ".json");
        entry.ExtractToFile(temp, overwrite: true);
        sb.AppendLine($"压缩包：{Path.GetFileName(path)} → 解出 {entry.FullName}（{entry.Length} 字节）");
        return temp;
    }

    private static int Finish(StringBuilder sb, string outPath, int code)
    {
        string text = sb.ToString();
        try { File.WriteAllText(outPath, text, new UTF8Encoding(false)); } catch { /* 写不出报告也不影响退出码 */ }
        Console.WriteLine(text);
        return code;
    }
}
