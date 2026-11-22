using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using InputPlus.Core.Qbar;

namespace InputPlus.Core.FileDialog;

/// <summary>一条"最近用过的目录"（QuickSwitch 🕘 的展示项）。</summary>
internal sealed class QuickSwitchHistoryEntry
{
    public string Path { get; set; } = "";
    public long LastUsedTicks { get; set; }
    public int Uses { get; set; }
}

/// <summary>
/// QuickSwitch 的目录历史。
///
/// 数据来源只读现有的 RecentTargets（文件记录取父目录），首次使用时聚合成一份
/// 本地缓存；之后的导航成功再追加。**不写回 RecentTargets / 不删原始历史** ——
/// 清空只清这份展示缓存。
/// </summary>
internal static class QuickSwitchHistory
{
    private sealed class Store
    {
        public bool Seeded { get; set; }
        public List<QuickSwitchHistoryEntry> Items { get; set; } = new();
    }

    private static readonly object Gate = new();
    private static Store? _store;
    internal static string? StorePathForTest { get; set; }

    private static string StorePath => StorePathForTest ?? Path.Combine(AppPaths.DataDir, "quick-switch-history.json");

    public static IReadOnlyList<QuickSwitchHistoryEntry> Top(int max)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _store!.Items
                .Where(e => !string.IsNullOrWhiteSpace(e.Path))
                .OrderByDescending(e => e.LastUsedTicks)
                .ThenByDescending(e => e.Uses)
                .Take(Math.Max(0, max))
                .ToList();
        }
    }

    public static void Record(string? path)
    {
        string norm = Normalize(path);
        if (norm.Length == 0 || !SafeExists(norm)) return;

        lock (Gate)
        {
            EnsureLoaded();
            var hit = _store!.Items.FirstOrDefault(e => Same(e.Path, norm));
            long now = DateTime.UtcNow.Ticks;
            if (hit == null)
            {
                _store.Items.Add(new QuickSwitchHistoryEntry { Path = norm, LastUsedTicks = now, Uses = 1 });
            }
            else
            {
                hit.Path = norm;
                hit.LastUsedTicks = now;
                hit.Uses++;
            }

            // 缓存最多 100 条，展示时只取 20；防止文件无限增长
            if (_store.Items.Count > 100)
            {
                var keep = _store.Items
                    .OrderByDescending(e => e.LastUsedTicks)
                    .ThenByDescending(e => e.Uses)
                    .Take(100)
                    .ToList();
                _store.Items = keep;
            }
            SaveLocked();
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            EnsureLoaded();
            _store!.Items.Clear();
            _store.Seeded = true;
            SaveLocked();
        }
    }

    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _store = null;
            StorePathForTest = null;
        }
    }

    private static void EnsureLoaded()
    {
        if (_store != null) return;
        _store = Load() ?? new Store();
        if (!_store.Seeded)
        {
            SeedFromRecentTargets(_store);
            _store.Seeded = true;
            SaveLocked();
        }
    }

    private static Store? Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            return JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath));
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[QuickSwitch] 读历史缓存失败，按空处理：" + ex.Message);
            return null;
        }
    }

    private static void SeedFromRecentTargets(Store store)
    {
        try
        {
            foreach (RecentTarget target in RecentTargets.Top(200))
            {
                string path = target.Target?.Trim() ?? "";
                if (path.Length == 0) continue;

                if (target.Kind == RecentTargetKind.File)
                {
                    try { path = Path.GetDirectoryName(path) ?? ""; }
                    catch { path = ""; }
                }
                else if (target.Kind != RecentTargetKind.Folder)
                {
                    continue;
                }

                string norm = Normalize(path);
                if (norm.Length == 0 || !SafeExists(norm)) continue;

                var hit = store.Items.FirstOrDefault(e => Same(e.Path, norm));
                if (hit == null)
                {
                    store.Items.Add(new QuickSwitchHistoryEntry
                    {
                        Path = norm,
                        LastUsedTicks = target.LastUsedTicks,
                        Uses = Math.Max(1, target.Uses),
                    });
                }
                else
                {
                    if (target.LastUsedTicks > hit.LastUsedTicks) hit.LastUsedTicks = target.LastUsedTicks;
                    hit.Uses += Math.Max(1, target.Uses);
                }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[QuickSwitch] 从最近记录聚合目录失败：" + ex.Message);
        }
    }

    private static void SaveLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[QuickSwitch] 写历史缓存失败：" + ex.Message);
        }
    }

    private static string Normalize(string? path)
    {
        string text = (path ?? "").Trim();
        if (text.Length == 0) return "";
        try { text = Path.GetFullPath(text).TrimEnd('\\', '/'); }
        catch { text = text.TrimEnd('\\', '/'); }
        return text;
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
