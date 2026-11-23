using System;
using InputPlus.Core.FileDialog;
using InputPlus.Core.Interop;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Windows;

/// <summary>
/// 中键这一下落在**哪种界面**上。它决定两件事：这一下要不要接管、以及菜单右上角
/// 那颗「收藏」星标该收藏什么（网页 / 文件夹 / 程序）。
///
/// v1.5.0 起从"只有桌面"扩成四种落点，顺序是硬的（先判桌面，再判资源管理器，
/// 最后才问对话框），理由见 <see cref="MiddleMenuSurfaces.Classify"/>。
/// </summary>
internal enum MiddleSurface
{
    /// <summary>不接管：别的程序（浏览器正文、编辑器、终端…）或判定不出。</summary>
    None = 0,

    /// <summary>系统桌面（Progman / WorkerW / shell 宿主）。</summary>
    Desktop = 1,

    /// <summary>资源管理器文件夹窗口的客户区（文件/文件夹上，或列表空白处）。</summary>
    ExplorerItems = 2,

    /// <summary>「打开 / 保存 / 选文件夹」这类系统对话框的客户区。</summary>
    FileDialog = 3,
}

/// <summary>
/// 一次"中键落点"探测的结果。
///
/// <see cref="Blank"/> 只对桌面有意义（桌面上压在图标上时不弹菜单的既有语义），
/// 资源管理器与文件对话框**一律可弹** —— 那是用户明确要的"在这两个地方也能唤出"。
/// </summary>
internal readonly record struct MiddleSurfaceProbe(
    MiddleSurface Surface,
    IntPtr Hwnd,
    IntPtr Root,
    string ClassName,
    string RootClass,
    string Why)
{
    /// <summary>这一下归不归我们管。</summary>
    public bool IsUsable => Surface != MiddleSurface.None;

    public bool IsDesktop => Surface == MiddleSurface.Desktop;

    /// <summary>资源管理器文件夹窗口（含文件列表与空白处）。</summary>
    public bool IsExplorer => Surface == MiddleSurface.ExplorerItems;

    /// <summary>系统文件对话框 / 文件夹选择对话框。</summary>
    public bool IsDialog => Surface == MiddleSurface.FileDialog;

    /// <summary>"不是我们要的地方"的一条结论，带原因。</summary>
    internal static MiddleSurfaceProbe NotMine(string why, IntPtr hwnd = default, IntPtr root = default,
        string cls = "", string rootCls = "")
        => new(MiddleSurface.None, hwnd, root, cls, rootCls, why);
}

/// <summary>
/// 中键落点判定（v1.5.0）。
///
/// ── 为什么要单独一层 ─────────────────────────────────────────────
/// 以前钩子里只有一句 <c>BlankHit.DesktopShellAtPoint</c>：不在桌面就整下放行。
/// 现在要覆盖"资源管理器文件列表 / 资源管理器空白 / 桌面空白 / 打开保存对话框 /
/// 选文件夹对话框"，于是把"指针底下是哪种界面"抽成这个纯判定：
///   · 钩子回调里只做几十微秒的窗口查询（不发 UIA、不问图标列表）；
///   · 桌面要不要弹仍然交给 <see cref="BlankHit.AtPoint"/> 判空白，
///     资源管理器与对话框**不需要**判空白 —— 判空白的代价是一次跨进程 UIA，
///     而在这两处"点在文件上"本来也该弹菜单。
///
/// ── 三条"永远不接管"的红线 ──────────────────────────────────────
/// ① 指针在某类**文本输入控件**上（地址栏、搜索框、文件名框）：那里的中键是"粘贴"，
///    接管了就是抢键。资源管理器的地址栏、对话框的文件名框都靠这一条排除。
/// ② 根窗口是本程序自己（菜单、设置页、Qbar）：不套娃。
/// ③ 不是 #32770 的对话框（消息框、属性表）：一律 `None`。
/// </summary>
internal static class MiddleMenuSurfaces
{
    private const string DialogClass = "#32770";

    /// <summary>
    /// 落点探测。<paramref name="scope"/> 只影响资源管理器：
    /// 设置里选"仅桌面"时，文件夹窗口里的中键原样放行（老行为，不动）。
    /// </summary>
    internal static MiddleSurfaceProbe AtPoint(NativeMethods.POINT pt, MiddleClickMenuScope scope)
    {
        IntPtr hwnd;
        try { hwnd = NativeMethods.WindowFromPoint(pt); }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "中键菜单：取指针窗口");
            return MiddleSurfaceProbe.NotMine("WindowFromPoint 失败");
        }
        if (hwnd == IntPtr.Zero) return MiddleSurfaceProbe.NotMine("指针下没有窗口");

        IntPtr root;
        string cls, rootCls;
        try
        {
            cls = WindowService.ClassNameOf(hwnd) ?? "";
            root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;
            rootCls = WindowService.ClassNameOf(root) ?? "";

            /* 指针压在本程序自己的窗口上：菜单/设置页/Qbar 都不该被自己的中键再弹一次。
             * 判在类名之前 —— 我们的窗口类名和下面几条不可能撞，但先排除更省事。 */
            if (WindowSummonAnimator.IsOwnProcess(hwnd))
                return MiddleSurfaceProbe.NotMine("本程序自己的窗口", hwnd, root, cls, rootCls);

            bool rootIsShell = root == NativeMethods.GetShellWindow();
            bool isFileDialog = false;

            /* 只有根是 #32770 才值得去枚举子控件。资源管理器/桌面/浏览器都不会是它，
             * 所以这次枚举对绝大多数点击是零成本。 */
            if (string.Equals(rootCls, DialogClass, StringComparison.OrdinalIgnoreCase))
            {
                isFileDialog = IsFileDialogWindow(root);
                if (!isFileDialog)
                    return MiddleSurfaceProbe.NotMine("不是文件/文件夹对话框（" + rootCls + "）", hwnd, root, cls, rootCls);
            }

            var surface = Classify(cls, rootCls, rootIsShell,
                MiddleMenuScope.IncludesExplorer(scope), isFileDialog, out string why);

            return new MiddleSurfaceProbe(surface, hwnd, root, cls, rootCls, why);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "中键菜单：判定落点");
            return MiddleSurfaceProbe.NotMine("判定异常");
        }
    }

    /// <summary>
    /// 判定本体（纯函数，自检直接喂类名）。
    ///
    /// 顺序就是语义：
    ///   1. 文本输入控件 → None（那里的中键是粘贴）；
    ///   2. 桌面这一族 → Desktop；
    ///   3. 资源管理器窗口 + 在范围内 → ExplorerItems；
    ///   4. 已确认是文件/文件夹对话框 → FileDialog；
    ///   5. 其余 → None。
    /// </summary>
    internal static MiddleSurface Classify(
        string cls,
        string rootCls,
        bool rootIsShell,
        bool includesExplorer,
        bool isFileDialog,
        out string why)
    {
        cls ??= "";
        rootCls ??= "";

        if (IsTextInputClass(cls))
        {
            why = "文本输入控件（中键是粘贴）：" + cls;
            return MiddleSurface.None;
        }

        if (BlankHit.IsDesktopShellClasses(cls, rootCls, rootIsShell))
        {
            why = "桌面（" + cls + " / root=" + rootCls + "）";
            return MiddleSurface.Desktop;
        }

        if (IsExplorerRoot(rootCls))
        {
            if (!includesExplorer)
            {
                why = "范围仅桌面（" + cls + " / root=" + rootCls + "）";
                return MiddleSurface.None;
            }
            why = "资源管理器（" + cls + " / root=" + rootCls + "）";
            return MiddleSurface.ExplorerItems;
        }

        if (isFileDialog)
        {
            /* 对话框跟着"范围"走：设成"仅桌面"的人要的就是最小接手面积，
             * 那里连资源管理器都不碰，更不该碰别的程序的对话框。 */
            if (!includesExplorer)
            {
                why = "范围仅桌面（对话框也不接管）：" + rootCls;
                return MiddleSurface.None;
            }
            why = "系统对话框（" + cls + " / root=" + rootCls + "）";
            return MiddleSurface.FileDialog;
        }

        why = "不是桌面/资源管理器/文件对话框（" + cls + " / root=" + rootCls + "）";
        return MiddleSurface.None;
    }

    /// <summary>资源管理器顶层窗口类名。</summary>
    internal static bool IsExplorerRoot(string? rootCls) =>
        rootCls is not null
        && Array.IndexOf(DialogProbe.ExplorerClasses, rootCls) >= 0;

    /// <summary>
    /// 这个控件的类名是不是"打字的地方"。
    ///
    /// 白名单而不是黑名单：只有这几类是确定会把中键当粘贴用的。
    /// 认不出来的控件照旧交给我们（否则资源管理器的 DirectUIHWND 就认不出来）。
    /// </summary>
    internal static bool IsTextInputClass(string? cls) => cls switch
    {
        "Edit" => true,
        "RichEdit20W" => true,
        "RichEdit50W" => true,
        "RICHEDIT60W" => true,
        "ComboBox" => true,
        "ComboBoxEx32" => true,
        "SearchBox" => true,
        "SearchEditBox" => true,
        "ms-autocomplete-list" => true,
        _ => false,
    };

    /// <summary>
    /// #32770 到底是不是「打开 / 保存 / 选文件夹」（也可以喂资源管理器以外的顶层窗口，非对话框直接 false）。
    ///
    /// 复用 <see cref="DialogClassifier"/>，但**不读控件文本** ——
    /// 读文本是跨进程 SendMessage(WM_GETTEXT)，而这里跑在中键按下的路径上。
    /// 判型本来也只依赖类名组合，文本对结果没有影响。
    /// </summary>
    internal static bool IsFileDialogWindow(IntPtr dialog)
    {
        try
        {
            var children = new System.Collections.Generic.List<DialogChild>(8);
            foreach (IntPtr child in DialogNative.Children(dialog))
            {
                string childCls = DialogNative.ClassOf(child);
                if (childCls.Length == 0) continue;
                children.Add(new DialogChild(child, childCls, 0, "", true));
            }
            var result = DialogClassifier.Classify(DialogClass, children);
            return result.Kind is DialogKind.FileDialog or DialogKind.FolderDialog;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "中键菜单：判对话框类型");
            return false;
        }
    }
}
