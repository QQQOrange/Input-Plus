using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InputPlus.Core.Qbar.Search;

/// <summary>一条历史事件。字段名短，是为了让 jsonl 在几十万行时也别太占地方。</summary>
internal sealed class QbarHistoryEvent
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("t")] public string Time { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "select";
    [JsonPropertyName("surface")] public string Surface { get; set; } = "qbar";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("query")] public string Query { get; set; } = "";
    [JsonPropertyName("rank")] public int Rank { get; set; }
}

/// <summary>
/// 追加式历史：一月一个 jsonl，只追加、不改写。
/// 它只喂"建议"和"最近使用"，绝不参与文件结果重排 —— 一旦让历史影响排序，
/// 用户就再也解释不清"为什么这条排第一"了。
/// </summary>
internal static class QbarHistory
{
    private static readonly object Gate = new object();
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>测试用：把事件目录指到临时目录（null = 恢复真实目录）。</summary>
    private static string? _overrideDir;

    public static string Directory => _overrideDir ?? Path.Combine(AppPaths.DataDir, "qbar-history");

    public static void OverrideDirectoryForTest(string? dir)
    {
        lock (Gate) _overrideDir = dir;
    }

    public static void Record(string kind, string name, string path, string query, int rank)
    {
        try
        {
            var evt = new QbarHistoryEvent
            {
                Time = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                Kind = kind ?? "select",
                Name = name ?? "",
                Path = path ?? "",
                Query = query ?? "",
                Rank = rank,
            };

            string dir = Directory;
            System.IO.Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "events-" + DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
            string line = JsonSerializer.Serialize(evt, Json);

            lock (Gate)
            {
                using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "写 Qbar 历史");
        }
    }

    /// <summary>读最近 twoMonths 个月的事件（新的在前）。读不出来就当空历史，绝不抛。</summary>
    public static List<QbarHistoryEvent> Recent(int max)
    {
        var list = new List<QbarHistoryEvent>();
        if (max <= 0) return list;

        try
        {
            string dir = Directory;
            if (!System.IO.Directory.Exists(dir)) return list;

            string current = DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            string previous = DateTime.Now.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            string[] files =
            {
                Path.Combine(dir, "events-" + current + ".jsonl"),
                Path.Combine(dir, "events-" + previous + ".jsonl"),
            };

            for (int i = 0; i < files.Length; i++)
            {
                if (!File.Exists(files[i])) continue;
                foreach (string line in SafeLines(files[i]))
                {
                    QbarHistoryEvent? evt = Parse(line);
                    if (evt != null) list.Add(evt);
                }
            }

            list.Sort((a, b) => string.CompareOrdinal(b.Time, a.Time));
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读 Qbar 历史");
            return new List<QbarHistoryEvent>();
        }

        return list;
    }

    /// <summary>按名字聚合出"常用"，供空查询默认列表用。</summary>
    public static List<(string Name, string Path, int Count)> Frequent(int limit)
    {
        var map = new Dictionary<string, (string Name, string Path, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (QbarHistoryEvent evt in Recent(2000))
        {
            if (evt.Path.Length == 0) continue;
            if (map.TryGetValue(evt.Path, out var entry))
                map[evt.Path] = (entry.Name, entry.Path, entry.Count + 1);
            else
                map[evt.Path] = (evt.Name, evt.Path, 1);
        }

        var list = new List<(string Name, string Path, int Count)>(map.Values);
        list.Sort((a, b) => b.Count != a.Count
            ? b.Count.CompareTo(a.Count)
            : string.CompareOrdinal(a.Name, b.Name));

        if (list.Count > limit) list.RemoveRange(limit, list.Count - limit);
        return list;
    }

    private static IEnumerable<string> SafeLines(string file)
    {
        FileStream? stream = null;
        StreamReader? reader = null;
        try
        {
            stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null) yield return line;
        }
        finally
        {
            reader?.Dispose();
            stream?.Dispose();
        }
    }

    private static QbarHistoryEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            return JsonSerializer.Deserialize<QbarHistoryEvent>(line, Json);
        }
        catch (JsonException)
        {
            return null;   // 半行/坏行：跳过，别让一行脏数据毁掉整份历史
        }
    }
}
