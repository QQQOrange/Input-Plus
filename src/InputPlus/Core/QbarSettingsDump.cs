using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;

namespace InputPlus.Core;

/// <summary>
/// 设置诊断：--settings-dump。
///
/// 为什么需要：真机上「过滤器关键字不生效」「命令不见了」这类问题，从界面看只能看到
/// 结果不对 —— 可能是设置没读出来（加密没解开）、可能是过滤器的扩展名表被覆盖成空、
/// 也可能是内置项被同名用户项盖掉了。这三种在界面上长得一模一样，只能把「读到了什么」摊开看。
///</summary>
internal static class QbarSettingsDump
{
    public static int Run(string[]? args = null)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        var sb = new StringBuilder();
        sb.AppendLine("=== 设置诊断 ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("数据目录: " + AppPaths.DataDir);
        sb.AppendLine("设置文件: " + AppPaths.SettingsFile);
        sb.AppendLine("  存在: " + File.Exists(AppPaths.SettingsFile)
                      + " / 大小: " + (File.Exists(AppPaths.SettingsFile) ? new FileInfo(AppPaths.SettingsFile).Length : 0) + " B");
        sb.AppendLine("加密: " + (DataVault.IsEnabled ? "已启用" : "未启用") + " / 已解锁: " + DataVault.IsUnlocked);
        sb.AppendLine("缓存解锁: " + DataVault.TryUnlockCached());
        sb.AppendLine("当前进程是管理员: " + (ElevationHelper.IsElevated ? "是" : "否"));

        AppSettings s = SettingsStore.Load();
        sb.AppendLine("加载失败: " + (string.IsNullOrEmpty(SettingsStore.LoadFailure) ? "(无)" : SettingsStore.LoadFailure));
        sb.AppendLine();

        sb.AppendLine("--- 设置里的过滤器（用户层）: " + s.Qbar.Filters.Count + " 条 ---");
        foreach (QbarFilter f in s.Qbar.Filters ?? new List<QbarFilter>())
        {
            sb.AppendLine("  " + f.Id + " | 关键字=" + f.Keyword + " | 名称=" + f.Name
                          + " | ext=" + (f.Extensions ?? "").Length + " 字符"
                          + " | 文件=" + f.SearchForFiles + " 目录=" + f.SearchForFolders
                          + " | enabled=" + f.Enabled);
        }

        QbarFilterCatalog catalog = QbarFilterCatalog.Create(s.Qbar.Filters);
        sb.AppendLine();
        sb.AppendLine("--- 合并后的过滤器（内置 + 用户）: " + catalog.All.Count + " 条 ---");
        foreach (QbarFilter f in catalog.All)
        {
            sb.AppendLine("  " + f.Keyword + " → ext=" + (f.Extensions ?? "").Length + " 字符"
                          + " / 文件=" + f.SearchForFiles + " / 目录=" + f.SearchForFolders
                          + " / 附加=\"" + (f.ExtraQuery ?? "") + "\" / 范围=\"" + (f.SearchInFolders ?? "") + "\"");
        }

        sb.AppendLine();
        sb.AppendLine("--- 命令: " + s.Qbar.Commands.Count + " 条 ---");
        foreach (QbarCommand c in s.Qbar.Commands ?? new List<QbarCommand>())
        {
            sb.AppendLine("  " + c.Keyword + " | " + c.Title + " | path=" + (c.Path ?? "(空)")
                          + " | 参数=" + (c.Parameter ?? "") + " | 内部动作=" + QbarCommandStore.IsInternalAction(c.Id));
        }

        sb.AppendLine();
        sb.AppendLine("--- 优先级规则: " + s.Qbar.Priority.Count + " 条 ---");
        foreach (QbarPriorityRule r in s.Qbar.Priority ?? new List<QbarPriorityRule>())
        {
            sb.AppendLine("  " + r.Level + " | " + (string.IsNullOrEmpty(r.FolderPath) ? "(任意目录)" : r.FolderPath)
                          + " | 正则=" + (r.NameRegex ?? "") + " | enabled=" + r.Enabled);
        }

        QbarSearchOptions o = s.Qbar.Search ?? new QbarSearchOptions();
        sb.AppendLine();
        sb.AppendLine("--- 选项 ---");
        sb.AppendLine("  MaxCandidates=" + o.MaxCandidates + " / RulesEnabled=" + o.RulesEnabled
                      + " / BuiltinRulesEnabled=" + o.BuiltinRulesEnabled
                      + " / Backend=" + o.Backend
                      + " / FuzzyWhenSparse=" + o.FuzzyWhenSparse + " / PinyinEnabled=" + o.PinyinEnabled);

        string text = sb.ToString();
        string path = "";
        foreach (string dir in new[] { Path.GetTempPath(), AppPaths.DataDir })
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) continue;
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "clp-settings-dump.txt");
                File.WriteAllText(path, text, Encoding.UTF8);
                break;
            }
            catch { /* 目录不可写就换下一个 */ }
        }
        Console.Out.WriteLine(text);
        if (path.Length > 0) Console.Out.WriteLine("（结果也写到 " + path + "）");
        return 0;
    }
}
