using System.Text;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// 子窗口探测：把 dialog 里所有后代窗口的「类名 + 控件 ID + 文本」打出来。
///
/// 这是 M1 的唯一产出，也是判型的事实来源。控件 ID 一律实测，不照抄常量：
/// comdlg32 的 cmb13=0x047C / edt1=0x0480 只当参考值写在文档里，
/// 真正决定走哪条路线的是 <see cref="DialogClassifier"/> 看到的类名组合。
/// </summary>
public static class DialogProbe
{
    /// <summary>Win32 通用对话框窗口类名。</summary>
    public const string DialogClass = "#32770";

    /// <summary>资源管理器窗口类名（本次只占位，不挂浮层）。</summary>
    public static readonly string[] ExplorerClasses = { "CabinetWClass", "ExploreWClass" };

    /// <summary>枚举一个窗口的全部后代控件（顺序为系统枚举顺序）。</summary>
    public static IReadOnlyList<DialogChild> Children(nint hwnd)
    {
        var list = new List<DialogChild>();
        foreach (nint child in DialogNative.Children(hwnd))
        {
            string cls = DialogNative.ClassOf(child);
            if (cls.Length == 0) continue;      // 正在销毁
            list.Add(new DialogChild(
                child,
                cls,
                DialogNative.GetDlgCtrlID(child),
                SafeText(child),
                DialogNative.IsWindowVisible(child)));
        }
        return list;
    }

    /// <summary>读控件文本。跨进程走 WM_GETTEXT，失败一律空串。</summary>
    public static string SafeText(nint hwnd)
    {
        try
        {
            if (DialogNative.IsHungAppWindow(hwnd)) return "";
            string t = DialogNative.TitleOf(hwnd);
            if (t.Length > 120) t = t[..120] + "…";
            return t.Replace('\r', ' ').Replace('\n', ' ');
        }
        catch { return ""; }
    }

    /// <summary>当前所有可见的顶层 #32770 窗口（兜底轮询用，只枚举顶层）。</summary>
    public static IReadOnlyList<nint> TopLevelDialogWindows()
    {
        var list = new List<nint>();
        try
        {
            DialogNative.EnumWindows((hwnd, _) =>
            {
                if (!DialogNative.IsWindowVisible(hwnd)) return true;
                if (DialogNative.ClassOf(hwnd) != DialogClass) return true;
                list.Add(hwnd);
                return true;
            }, 0);
        }
        catch { /* 枚举期间有窗口消失，忽略 */ }
        return list;
    }

    /// <summary>当前所有可见的顶层资源管理器窗口（M1 判型用，不挂浮层）。</summary>
    public static IReadOnlyList<nint> TopLevelExplorerWindows()
    {
        var list = new List<nint>();
        try
        {
            DialogNative.EnumWindows((hwnd, _) =>
            {
                if (!DialogNative.IsWindowVisible(hwnd)) return true;
                string cls = DialogNative.ClassOf(hwnd);
                if (Array.IndexOf(ExplorerClasses, cls) >= 0) list.Add(hwnd);
                return true;
            }, 0);
        }
        catch { }
        return list;
    }

    public static bool HasClass(IEnumerable<DialogChild> children, params string[] names)
    {
        foreach (var c in children)
            foreach (var n in names)
                if (string.Equals(c.ClassName, n, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    public static DialogChild? FindByClass(IEnumerable<DialogChild> children, params string[] names)
    {
        foreach (var c in children)
            foreach (var n in names)
                if (string.Equals(c.ClassName, n, StringComparison.OrdinalIgnoreCase))
                    return c;
        return null;
    }

    public static DialogChild? FindById(IEnumerable<DialogChild> children, int id)
    {
        foreach (var c in children)
            if (c.ControlId == id) return c;
        return null;
    }

    /// <summary>按类名找第一个**直接子窗口**；找不到返回 0。</summary>
    public static nint FirstDirectChildOfClass(nint parent, string className)
    {
        foreach (nint child in DialogNative.Children(parent))
        {
            if (string.Equals(DialogNative.ClassOf(child), className, StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return 0;
    }

    /// <summary>
    /// 探测报告：一行窗口信息 + 每个子控件的类名/ID/文本。
    /// 直接写日志，M1 验收就靠它。
    /// </summary>
    public static string Describe(nint hwnd)
    {
        var sb = new StringBuilder();
        string cls = DialogNative.ClassOf(hwnd);
        DialogNative.GetWindowThreadProcessId(hwnd, out uint pid);
        sb.AppendLine($"窗口 0x{hwnd:X} 类名={cls} pid={pid} 标题=\"{DialogNative.TitleOf(hwnd)}\" " +
                      $"可见={DialogNative.IsWindowVisible(hwnd)} 启用={DialogNative.IsWindowEnabled(hwnd)}");

        DialogRect r = DialogNative.FrameBounds(hwnd);
        sb.AppendLine($"  边框 L={r.Left} T={r.Top} R={r.Right} B={r.Bottom} ({r.Width}x{r.Height}) " +
                      $"dpi={DialogNative.GetDpiForWindow(hwnd)}");

        var children = Children(hwnd);
        sb.AppendLine($"  子窗口 {children.Count} 个：");
        foreach (var c in children)
            sb.AppendLine($"    0x{c.Hwnd:X}  {c.ClassName,-24} id=0x{c.ControlId:X4} " +
                          $"visible={c.Visible} text=\"{c.Text}\"");
        return sb.ToString().TrimEnd();
    }

    /// <summary>判型并给出探测报告（M1 日志一行搞定）。</summary>
    public static string ClassifyAndDescribe(nint hwnd)
    {
        var children = Children(hwnd);
        var result = DialogClassifier.Classify(DialogNative.ClassOf(hwnd), children);
        return Describe(hwnd) + Environment.NewLine +
               $"  判型：{result.Kind} / {result.Family} —— {result.Reason}";
    }
}
