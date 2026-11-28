using System;
using System.Collections.Generic;
using InputPlus.Core.FileDialog;

namespace InputPlus;

/// <summary>
/// 自检：文件对话框浮层的判型（v1.7.14）。
///
/// 现场两条：
///   ① 微信里发送文件，浮层**不出现**（微信整个进程以管理员身份运行，
///      owner 被 UIPI 拒 —— 那条在 FileDialogBarWindow.AttachTo 里退回置顶无主窗口）；
///   ② **报错窗口上反而浮出来了** —— 那种窗口没有选文件的功能，回填无处可去。
///
/// ② 的根因是判型太松：`#32770 + DirectUIHWND` 就算文件对话框，而 DirectUI 画的
/// 任务对话框/错误框同样是这两样。这里用实测到的子控件组合把它钉死：
///   真 IFileDialog（Win11 23H2 实测）= DirectUIHWND + CtrlNotifySink ×4 + SHELLDLL_DefView
///                                       + DUIViewWndClassName + NamespaceTreeControl + SysTreeView32
///   经典 MessageBox（实测）            = Button + Static
/// </summary>
public static partial class SelfTest
{
    private static DialogChild DlgChild(string cls, string text = "")
        => new(IntPtr.Zero, cls, 0, text, true);

    private static void TestDialogClassifier()
    {
        /* ① 真文件对话框（按实测子控件组合） → FileDialog，要挂浮层 */
        var realFile = DialogClassifier.Classify("#32770", new List<DialogChild>
        {
            DlgChild("DUIViewWndClassName"), DlgChild("DirectUIHWND"), DlgChild("CtrlNotifySink"),
            DlgChild("NamespaceTreeControl"), DlgChild("SysTreeView32"), DlgChild("SHELLDLL_DefView"),
            DlgChild("ScrollBar"), DlgChild("Static", "文件名(&N):"),
        });
        CheckEqual("判型：真 Vista 文件对话框", realFile.Kind, DialogKind.FileDialog);
        CheckEqual("判型：真文件对话框的家族", realFile.Family, DialogFamily.VistaDialog);
        Check("判型：真文件对话框要挂浮层", realFile.WantsBar);

        /* ② 只有 DirectUIHWND 的任务对话框 / 错误框 → Unknown（**不挂**） */
        var errorBox = DialogClassifier.Classify("#32770", new List<DialogChild>
        {
            DlgChild("DirectUIHWND"), DlgChild("Static", "保存失败"), DlgChild("Button", "确定"),
        });
        CheckEqual("判型：DirectUI 画的错误框不挂浮层", errorBox.Kind, DialogKind.Unknown);
        Check("判型：错误框不能挂浮层", !errorBox.WantsBar, errorBox.Reason);
        Check("判型：理由里说清是任务对话框/消息框",
            errorBox.Reason.Contains("任务对话框") || errorBox.Reason.Contains("消息框"), errorBox.Reason);

        /* ③ 经典 MessageBox（实测 Button+Static） → Unknown */
        var classicBox = DialogClassifier.Classify("#32770", new List<DialogChild>
        {
            DlgChild("Button", "确定"), DlgChild("Static", "出错了"),
        });
        CheckEqual("判型：经典 MessageBox 不挂浮层", classicBox.Kind, DialogKind.Unknown);

        /* ④ 老式 comdlg32：ComboBoxEx32 + Edit → FileDialog/Legacy */
        var legacy = DialogClassifier.Classify("#32770", new List<DialogChild>
        {
            DlgChild("ComboBoxEx32"), DlgChild("Edit"), DlgChild("Button", "打开"),
        });
        CheckEqual("判型：老式文件对话框", legacy.Kind, DialogKind.FileDialog);
        CheckEqual("判型：老式文件对话框的家族", legacy.Family, DialogFamily.LegacyDialog);

        /* ⑤ 文件夹选择器：ShellNameSpace / SysTreeView32 → FolderDialog */
        var folder = DialogClassifier.Classify("#32770", new List<DialogChild>
        {
            DlgChild("SysTreeView32"), DlgChild("Button", "确定"),
        });
        CheckEqual("判型：文件夹选择对话框", folder.Kind, DialogKind.FolderDialog);

        /* ⑥ 非 #32770（Qt / WPF 自绘对话框） → Unknown */
        var qt = DialogClassifier.Classify("Qt51514QWindowIcon", new List<DialogChild>
        {
            DlgChild("DirectUIHWND"),
        });
        CheckEqual("判型：非 #32770 一律不挂", qt.Kind, DialogKind.Unknown);

        /* ⑦ 空子控件列表也不能崩（对话框刚 SHOW、子控件还没建出来） */
        var empty = DialogClassifier.Classify("#32770", new List<DialogChild>());
        CheckEqual("判型：子控件还没建出来时按未知处理", empty.Kind, DialogKind.Unknown);
    }
}
