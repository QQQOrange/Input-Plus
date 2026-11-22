using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using InputPlus.Core.Windows;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// QuickSwitch 的"当前目录"探测：资源管理器前台窗口 / 文件对话框。
/// 全部走 UI Automation 的地址栏，取不到就返回 null —— 不报错、不弹窗。
/// </summary>
internal static class QuickSwitchFolderProbe
{
    private static readonly Regex DrivePath = new(@"^[A-Za-z]:\\", RegexOptions.Compiled);
    private static readonly Regex UncPath = new(@"^\\\\", RegexOptions.Compiled);

    private static readonly object Gate = new();
    private static readonly Dictionary<nint, (string Path, long At)> Cache = new();
    private const long CacheMs = 800;

    /// <summary>最前台或第一个资源管理器窗口正在浏览的目录。</summary>
    public static string? ExplorerFolder()
    {
        try
        {
            var windows = DialogProbe.TopLevelExplorerWindows();
            if (windows.Count == 0) return null;

            nint fg = DialogNative.GetForegroundWindow();
            nint target = windows.Contains(fg) ? fg : windows[0];

            // 地址栏在 Windows 11 里可能是面包屑（"文档"），拿不到完整路径；
            // Shell.Application 的 Document.Folder.Self.Path 一定能给出真实目录。
            if (TryExplorerCom(target, out string comPath)) return comPath;
            return FolderOf(target);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[QuickSwitch] 取资源管理器目录失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>对话框当前目录；取不到返回 null（chip 就不显示，或按未知处理）。</summary>
    public static string? DialogFolder(nint dialog)
    {
        try { return FolderOf(dialog); }
        catch (Exception ex)
        {
            CoreLog.Warn("[QuickSwitch] 取对话框目录失败：" + ex.Message);
            return null;
        }
    }

    private static bool TryExplorerCom(nint preferred, out string path)
    {
        path = "";
        string? found = null;
        var thread = new System.Threading.Thread(() => found = ComWorker(preferred))
        {
            IsBackground = true,
            Name = "InputPlus.QuickSwitch.ShellCom",
        };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        try
        {
            thread.Start();
            if (!thread.Join(1200)) return false;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[QuickSwitch] 启动 Shell.Application 线程失败：" + ex.Message);
            return false;
        }

        if (string.IsNullOrEmpty(found)) return false;
        path = found!;
        return true;
    }

    /// <summary>Shell.Application 遍历所有 shell 窗口；单个窗口取不到就跳过（桌面/IE 没有 Document）。</summary>
    private static string? ComWorker(nint preferred)
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return null;
            object? shell = Activator.CreateInstance(type);
            if (shell == null) return null;

            object? windows = type.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
            if (windows == null) return null;
            int count = Convert.ToInt32(windows.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, windows, null));
            string? first = null;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    object? window = windows.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                    if (window == null) continue;

                    int hwnd = Convert.ToInt32(window.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, window, null));
                    object? document = window.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, window, null);
                    object? folder = document?.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, document, null);
                    object? self = folder?.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, folder, null);
                    string candidate = self?.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, self, null) as string ?? "";
                    if (candidate.Length == 0 || !SafeDir(candidate)) continue;
                    candidate = candidate.TrimEnd('\\');
                    if (preferred != 0 && hwnd == (int)preferred) return candidate;
                    first ??= candidate;
                }
                catch { /* 单个 shell 窗口没有 Document，继续下一个 */ }
            }
            return first;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[QuickSwitch] Shell.Application 取资源管理器目录失败：" + ex.Message);
            return null;
        }
    }

    private static string? FolderOf(nint hwnd)
    {
        if (hwnd == 0 || !DialogNative.IsWindow(hwnd)) return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(hwnd, out var hit) && Environment.TickCount64 - hit.At < CacheMs)
                return hit.Path.Length == 0 ? null : hit.Path;
        }

        string path = "";

        /* 文件对话框的地址栏是 ToolbarWindow32 id=0x03E9，窗口文本就是
         * "地址: C:..."。比 UIA 可靠得多（UIA 那边 ValuePattern 经常是空的）。 */
        try
        {
            foreach (var child in DialogProbe.Children(hwnd))
            {
                if (!child.Visible || string.IsNullOrWhiteSpace(child.Text)) continue;
                string text = child.Text.Trim();
                if (!text.StartsWith("地址", StringComparison.OrdinalIgnoreCase)
                    && !text.StartsWith("Address", StringComparison.OrdinalIgnoreCase)) continue;
                string parsed = ExtractPath(text);
                if (parsed.Length > 0) { path = parsed; break; }
            }
        }
        catch { }

        try
        {
            if (path.Length > 0) goto done;
            var root = AutomationElement.FromHandle(hwnd);
            var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);

            // 1) 地址栏编辑框：Name 含「地址 / Address」，或 AutomationId 叫 AddressDisplay
            foreach (AutomationElement element in all)
            {
                try
                {
                    if (element.Current.ControlType != ControlType.Edit) continue;
                    string name = element.Current.Name ?? "";
                    string id = element.Current.AutomationId ?? "";
                    string candidate = "";
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern)
                        && pattern is ValuePattern value)
                        candidate = value.Current.Value ?? "";
                    if (candidate.Length == 0) candidate = name;

                    bool looksAddress = name.Contains("地址", StringComparison.OrdinalIgnoreCase)
                                         || name.Contains("Address", StringComparison.OrdinalIgnoreCase)
                                         || id.Contains("Address", StringComparison.OrdinalIgnoreCase);
                    if (!looksAddress && !IsPath(candidate)) continue;
                    string parsed = ExtractPath(candidate);
                    if (parsed.Length > 0) { path = parsed; break; }
                }
                catch { }
            }

            // 2) 地址栏 ToolBar / 面包屑按钮的 Name
            if (path.Length == 0)
            {
                foreach (AutomationElement element in all)
                {
                    try
                    {
                        if (element.Current.ControlType != ControlType.ToolBar
                            && element.Current.ControlType != ControlType.Text
                            && element.Current.ControlType != ControlType.Button) continue;
                        string parsed = ExtractPath(element.Current.Name ?? "");
                        if (parsed.Length > 0) { path = parsed; break; }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[QuickSwitch] UIA 读取目录失败：" + ex.Message);
        }

    done:
        if (path.Length > 0 && !SafeDir(path)) path = "";
        lock (Gate)
        {
            if (Cache.Count > 32) Cache.Clear();
            Cache[hwnd] = (path, Environment.TickCount64);
        }
        return path.Length == 0 ? null : path;
    }

    private static bool IsPath(string text)
    {
        string t = (text ?? "").Trim();
        return DrivePath.IsMatch(t) || UncPath.IsMatch(t);
    }

    private static string ExtractPath(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return "";

        int colon = t.IndexOfAny(new[] { ':', '：' });
        if (colon >= 0)
        {
            string tail = t[(colon + 1)..].Trim();
            if (IsPath(tail)) t = tail;
        }

        // 只取到第一个路径分隔后的非法字符（面包屑会把路径拼成 "D:\foo > bar"）
        int cut = t.IndexOf(" > ", StringComparison.Ordinal);
        if (cut > 0) t = t[..cut].Trim();
        t = t.Trim('"', ' ', '\\');
        if (t.Length <= 2) return ""; // "D:" / "C:" 不是目录

        try
        {
            string full = Path.GetFullPath(t).TrimEnd('\\');
            return SafeDir(full) ? full : "";
        }
        catch { return ""; }
    }

    private static bool SafeDir(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }
}
