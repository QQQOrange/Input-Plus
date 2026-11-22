using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InputPlus.Core.Settings;

namespace InputPlus.Core.FileDialog;

/// <summary>QuickSwitch 三个按钮的纯数据层：收藏/菜单行的构造与收藏去重。</summary>
internal static class QuickSwitchCatalog
{
    public static List<QuickSwitchRow> Favorites(IEnumerable<FavoriteDirectory>? favorites)
    {
        var rows = new List<QuickSwitchRow>();
        foreach (var favorite in favorites ?? Enumerable.Empty<FavoriteDirectory>())
        {
            if (favorite == null || string.IsNullOrWhiteSpace(favorite.Path)) continue;
            rows.Add(new QuickSwitchRow
            {
                Title = string.IsNullOrWhiteSpace(favorite.Name) ? favorite.Path : favorite.Name,
                Subtitle = favorite.Path,
                Glyph = "\uE734",
                Path = favorite.Path,
                IconPath = favorite.Path,
            });
        }
        if (rows.Count == 0)
            rows.Add(new QuickSwitchRow { Title = "暂无收藏", Subtitle = "用下面的入口添加", Glyph = "\uE734", Enabled = false });
        return rows;
    }

    public static bool AddFavorite(FileDialogBarOptions options, string? folder, out string error)
    {
        error = "";
        string path = (folder ?? "").Trim();
        if (path.Length == 0 || !Directory.Exists(path)) { error = "目录不存在"; return false; }

        options.Favorites ??= new List<FavoriteDirectory>();
        if (options.Favorites.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            error = "这个目录已经在收藏里";
            return false;
        }

        string name = Path.GetFileName(path.TrimEnd('\\'));
        options.Favorites.Add(new FavoriteDirectory { Name = string.IsNullOrEmpty(name) ? path : name, Path = path });
        return true;
    }

    public static List<QuickSwitchRow> Menu() => new()
    {
        new QuickSwitchRow { Title = "切换到文件管理器中正在浏览的文件夹", Subtitle = "等价 Ctrl+G", Glyph = "\uE8B7", Action = QuickSwitchAction.RevealExplorerFolder },
        new QuickSwitchRow { Title = "设置", Subtitle = "打开文件对话框相关设置", Glyph = "\uE713", Action = QuickSwitchAction.OpenSettings },
        new QuickSwitchRow { Title = "隐藏此搜索栏", Subtitle = "仅本次 / 在此应用中禁用 / 全局关闭", Glyph = "\uE7B3", Action = QuickSwitchAction.ShowHideMenu },
        new QuickSwitchRow { Title = "关闭", Subtitle = "收起搜索栏（等价 Esc）", Glyph = "\uE711", Action = QuickSwitchAction.CloseBar },
    };

    public static List<QuickSwitchRow> HideMenu() => new()
    {
        new QuickSwitchRow { Title = "仅本次", Subtitle = "当前对话框收起，新建对话框重新出现", Glyph = "\uE7B3", Action = QuickSwitchAction.HideSession },
        new QuickSwitchRow { Title = "在此应用中禁用", Subtitle = "按当前进程名记住", Glyph = "\uE77B", Action = QuickSwitchAction.HideProcess },
        new QuickSwitchRow { Title = "全局关闭", Subtitle = "所有文件对话框都不再出现，可在设置里重新打开", Glyph = "\uE7B8", Action = QuickSwitchAction.HideGlobal },
    };
}
