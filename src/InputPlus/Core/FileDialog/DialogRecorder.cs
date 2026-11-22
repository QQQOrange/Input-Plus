using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// 一条文件对话框使用记录。只记"选了哪个路径、从哪儿来、走的哪条回填路线"，
/// **不记文件内容** —— 记录的唯一用途是排序建议与排错。
/// </summary>
internal sealed record DialogHistoryEntry(
    DateTimeOffset Ts,
    string Surface,
    string Path,
    string Kind,
    string Route);

/// <summary>
/// Recorder 层：写使用记录。
///
/// 落盘格式是 JSON Lines（一行一条），追加写，不做整文件重写：
///   · 崩溃时最多丢最后一行，不会把历史记录整段写坏；
///   · 追加不需要先读全文件，写入成本与历史长度无关；
///   · 出问题时用户可以自己用记事本打开看（不是二进制/自定义格式）。
///
/// 写入失败一律降级成"只留在内存里"：记录是**附加价值**，不能因为它
/// 写不进磁盘就让一次成功的回填变成失败。
/// </summary>
internal static class DialogRecorder
{
    /// <summary>内存里保留最近多少条用于"空查询时的最近项"。</summary>
    private const int MemoryLimit = 200;

    private static readonly object Gate = new();
    private static readonly List<DialogHistoryEntry> Memory = new();
    private static bool _loaded;
    private static string? _storePathOverride;

    /// <summary>测试用：把存储路径指到临时目录，并清掉内存缓存。</summary>
    internal static string? StorePathForTest
    {
        get => StorePath;
        set
        {
            lock (Gate)
            {
                _storePathOverride = value;
                Memory.Clear();
                _loaded = false;
            }
        }
    }

    internal static string StorePath => _storePathOverride ?? System.IO.Path.Combine(AppPaths.DataDir, "dialog-history.jsonl");

    /// <summary>
    /// 写一条成功记录。调用方必须在**确实回填成功**之后调用。
    /// 永远不抛异常。
    /// </summary>
    internal static void Record(DialogSelection item, string processName, BackfillRoute route)
    {
        try
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;

            string surface = "file_dialog:" + Normalize(processName);
            var entry = new DialogHistoryEntry(
                DateTimeOffset.Now,
                surface,
                item.Path,
                item.Kind,
                RouteText(route));

            lock (Gate)
            {
                EnsureLoaded();
                Memory.Add(entry);
                if (Memory.Count > MemoryLimit) Memory.RemoveRange(0, Memory.Count - MemoryLimit);
            }

            AppendLine(entry);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "写文件对话框使用记录");
        }
    }

    /// <summary>空查询时展示的"最近在此类对话框里选过的项"，按时间倒序、按路径去重。</summary>
    internal static IReadOnlyList<DialogSelection> RecentSelections(int count)
    {
        if (count <= 0) return Array.Empty<DialogSelection>();
        try
        {
            List<DialogHistoryEntry> snapshot;
            lock (Gate)
            {
                EnsureLoaded();
                snapshot = new List<DialogHistoryEntry>(Memory);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<DialogSelection>(count);
            for (int i = snapshot.Count - 1; i >= 0 && result.Count < count; i--)
            {
                var entry = snapshot[i];
                if (string.IsNullOrWhiteSpace(entry.Path)) continue;
                if (!seen.Add(entry.Path)) continue;

                bool isFolder = string.Equals(entry.Kind, "folder", StringComparison.OrdinalIgnoreCase)
                                || Directory.Exists(entry.Path);
                string name;
                try
                {
                    name = System.IO.Path.GetFileName(entry.Path.TrimEnd('\\', '/'));
                }
                catch
                {
                    name = entry.Path;
                }
                if (string.IsNullOrEmpty(name)) name = entry.Path;

                result.Add(new DialogSelection(entry.Path, isFolder, name, "最近"));
            }
            return result;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读文件对话框使用记录");
            return Array.Empty<DialogSelection>();
        }
    }

    /// <summary>最近 N 条原始记录（排错用）。</summary>
    internal static IReadOnlyList<DialogHistoryEntry> Recent(int count)
    {
        lock (Gate)
        {
            EnsureLoaded();
            int take = Math.Min(Math.Max(0, count), Memory.Count);
            return Memory.GetRange(Memory.Count - take, take);
        }
    }

    /// <summary>测试用：清空内存与文件。</summary>
    internal static void ClearForTest()
    {
        lock (Gate)
        {
            Memory.Clear();
            _loaded = true;
            try
            {
                if (File.Exists(StorePath)) File.Delete(StorePath);
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "清理文件对话框使用记录");
            }
        }
    }

    private static void AppendLine(DialogHistoryEntry entry)
    {
        try
        {
            string line = JsonSerializer.Serialize(entry);
            string dir = System.IO.Path.GetDirectoryName(StorePath) ?? "";
            if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(StorePath, line + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[文件对话框] 使用记录落盘失败（仅保留内存）：" + ex.Message);
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string path = StorePath;
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path);
            var buffer = new List<DialogHistoryEntry>();
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<DialogHistoryEntry>(line);
                    if (entry != null) buffer.Add(entry);
                }
                catch
                {
                    /* 单行损坏（例如上次写到一半断电）不影响其它行 */
                }
            }

            if (buffer.Count > MemoryLimit) buffer.RemoveRange(0, buffer.Count - MemoryLimit);
            Memory.AddRange(buffer);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[文件对话框] 读取历史记录失败：" + ex.Message);
        }
    }

    private static string Normalize(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "unknown";
        return processName.Trim().ToLowerInvariant();
    }

    private static string RouteText(BackfillRoute route) => route switch
    {
        BackfillRoute.Uia => "uia",
        BackfillRoute.Editor => "editor",
        BackfillRoute.Keyboard => "keyboard",
        _ => "none",
    };
}
