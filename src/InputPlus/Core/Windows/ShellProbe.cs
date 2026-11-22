using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Windows;

/// <summary>
/// shell 浮层诊断（--shell-probe [文件] [--dismiss]）：回答"开始菜单开着的时候，
/// 手势引擎会不会认出来"。手势在开始菜单上起笔之所以看着没反应，就是因为
/// 轨迹窗在 Z 序带 1、开始菜单在带 6，轨迹被整块盖住（见 <see cref="ShellOverlay"/>）。
/// 这个入口把判定结果与"收起一次能不能成功"写进文件，排障时不用猜。
/// </summary>
internal static class ShellProbe
{
    public static int Run(string[] args)
    {
        bool dismiss = args.Any(a => string.Equals(a, "--dismiss", StringComparison.OrdinalIgnoreCase));
        string outPath = Path.Combine(AppPaths.DataDir, "shell-probe.txt");
        var positional = args.Skip(1)
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .ToList();
        if (positional.Count > 0) outPath = positional[0];

        var sb = new StringBuilder();
        try
        {
            IntPtr fg = WindowService.Foreground;
            string cls = WindowService.ClassNameOf(fg) ?? "?";
            sb.AppendLine($"前台 hwnd=0x{fg.ToInt64():X} 类名={cls}");
            sb.AppendLine($"前台 = {ShellOverlay.Describe(fg)}");
            sb.AppendLine($"ShellOverlay.IsUp(前台) = {ShellOverlay.IsUp(fg)}");
            sb.AppendLine($"ShellOverlay.IsUp()     = {ShellOverlay.IsUp()}");

            if (dismiss)
            {
                bool acted = ShellOverlay.Dismiss();
                sb.AppendLine($"ShellOverlay.Dismiss()  = {acted}");
                Thread.Sleep(500);
                IntPtr after = WindowService.Foreground;
                sb.AppendLine($"收起后前台 = {ShellOverlay.Describe(after)}");
                sb.AppendLine($"收起后 IsUp(前台) = {ShellOverlay.IsUp(after)}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("诊断本身出错：" + ex);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
        return 0;
    }
}
