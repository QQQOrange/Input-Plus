using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using InputPlus.Core.Input;
using InputPlus.Core.Settings;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// Backfill 层：把浮层里选中的路径**塞回**对话框，让对话框像用户自己选的一样返回。
///
/// 这一层不认识 UI、不认识 WPF：它只拿 <see cref="DialogWindowInfo"/>、
/// <see cref="DialogClassification"/> 和一个路径字符串，返回
/// <see cref="BackfillOutcome"/>。Bar 与 Service 只看结果。
///
/// 三条路线（按家族分派，绝不按 exe 分派）：
///   · VistaDialog  → UIA ValuePattern.SetValue（主）→ 键盘 + 剪贴板（兜底）
///   · LegacyDialog → 键盘 + 剪贴板（跨进程 WM_SETTEXT 在多版本上不可靠）
///   · FolderPicker → 键盘 + 剪贴板，目标是**目录**（选到文件时取它的父目录）
///
/// 红线：目标进程权限更高（UIPI）时直接判失败并记日志，不做任何绕过。
/// </summary>
internal static class DialogBackfill
{
    /// <summary>VK 'A'（Ctrl+A 清空原内容）。</summary>
    private const int VkA = 0x41;

    /// <summary>Vista 对话框文件名编辑框的 Win32 控件 ID（0x03E9 = 1001）。</summary>
    private const int IdFileNameEdit = 0x03E9;

    /// <summary>老式 GetOpenFileName 的 edt1。</summary>
    private const int IdLegacyEdit = 0x0480;

    /// <summary>提交后最多等多久校验结果。</summary>
    private const int VerifyTimeoutMs = 2000;

    /// <summary>
    /// 一次完整的回填。**必须在 STA 线程调用**（剪贴板通道要求），
    /// 并且不要放在 WPF UI 线程上（最长会阻塞约 4 秒）。
    /// </summary>
    internal static BackfillOutcome Submit(
        DialogWindowInfo dialog,
        DialogClassification classification,
        DialogSelection item,
        FileDialogBarOptions options)
    {
        if (dialog == null || dialog.Hwnd == 0) return BackfillOutcome.Fail(BackfillRoute.None, "没有对话框");
        if (!DialogNative.IsWindow(dialog.Hwnd)) return BackfillOutcome.Fail(BackfillRoute.None, "对话框已经关闭");
        if (item == null || string.IsNullOrWhiteSpace(item.Path)) return BackfillOutcome.Fail(BackfillRoute.None, "没有选中任何项");

        /* 选文件夹模式有两种长相：老式 FolderDialog（ShellNameSpace / SysTreeView32），
         * 以及 Win11 上长得和保存对话框一模一样的文件夹选择框（UIA 编辑框叫「文件夹:」）。
         * 后者的提交动作是「选择文件夹」而不是回车，所以也要按文件夹目标走。 */
        bool folderTarget = classification.Kind == DialogKind.FolderDialog
                            || (item.IsFolder && DialogUia.IsFolderPicker(dialog.Hwnd));
        string path = ResolvePath(item, folderTarget);
        if (path.Length == 0) return BackfillOutcome.Fail(BackfillRoute.None, "选中项没有可用路径");

        if (InputSender.DryRun)
            return BackfillOutcome.Fail(BackfillRoute.None, "干跑模式：不向任何窗口发输入");

        /* UIPI：目标进程以管理员运行、本程序没有 —— 系统会把按键静默丢弃。
         * 红线要求"直接判定不可用并记日志，不许尝试任何绕过"。
         * IsForegroundHigherIntegrity 用一个 WM_NULL 探路，被拦时 GetLastError=5。 */
        if (InputSender.IsForegroundHigherIntegrity())
        {
            CoreLog.Warn($"[文件对话框] {dialog.ProcessName} 权限高于本程序（UIPI），本次不尝试回填");
            return BackfillOutcome.Fail(BackfillRoute.None, "目标进程完整性级别更高（UIPI），按红线不做任何绕过");
        }

        if (path.Length > 259)
            CoreLog.Info($"[文件对话框] 路径长度 {path.Length}，超过 MAX_PATH；对话框是否接受取决于目标程序的长路径支持");

        BackfillOutcome? first = null;

        /* ① Vista 家族：先试 UIA。失败不是错误，只是说明这条对话框不给 ValuePattern。 */
        if (options.UiaBackfill && classification.Family == DialogFamily.VistaDialog)
        {
            first = TryUia(dialog, path, folderTarget);
            if (first.Success) return first;
            CoreLog.Info($"[文件对话框] UIA 路线未成功（{first.Detail}），改走键盘 + 剪贴板");
        }

        /* ② 键盘 + 剪贴板。老式对话框直接从这里开始。 */
        var route = classification.Family == DialogFamily.LegacyDialog ? BackfillRoute.Editor : BackfillRoute.Keyboard;
        var second = TryKeyboard(dialog, path, folderTarget, route);
        if (second.Success) return second;

        /* ③ 只重试一次 —— 连按回车会把对话框点成"取消"或者重复提交，比失败更糟。 */
        Thread.Sleep(120);
        var third = TryKeyboard(dialog, path, folderTarget, route);
        if (third.Success) return BackfillOutcome.Ok(third.Route, third.Detail + "（重试 1 次后成功）");

        string detail = (first != null ? "UIA：" + first.Detail + "；" : "") +
                        "键盘：" + second.Detail + "；重试：" + third.Detail;
        CoreLog.Warn($"[文件对话框] 回填失败（{dialog.ProcessName}）：{detail}");
        return BackfillOutcome.Fail(route, detail);
    }

    /// <summary>
    /// QuickSwitch：把对话框**导航到目录**，不关闭对话框、不选任何文件。
    ///
    /// 优先 UIA 写入文件名框（SetFolder 需要拿到对话框进程里的 IFileDialog COM 指针；
    /// 本程序不做注入，所以没有指针时走同一条 UIA/键盘路径 —— Windows 通用对话框
    /// 对"文件名框里的目录 + 回车"就是导航语义）。老式对话框直接走键盘。
    /// </summary>
    internal static BackfillOutcome Navigate(
        DialogWindowInfo dialog,
        DialogClassification classification,
        string folder,
        FileDialogBarOptions options)
    {
        if (dialog == null || dialog.Hwnd == 0) return BackfillOutcome.Fail(BackfillRoute.None, "没有对话框");
        if (!DialogNative.IsWindow(dialog.Hwnd)) return BackfillOutcome.Fail(BackfillRoute.None, "对话框已经关闭");
        folder = (folder ?? "").Trim();
        if (folder.Length == 0) return BackfillOutcome.Fail(BackfillRoute.None, "目录为空");
        try { if (!Directory.Exists(folder)) return BackfillOutcome.Fail(BackfillRoute.None, "目录不存在：" + folder); }
        catch { return BackfillOutcome.Fail(BackfillRoute.None, "目录不可访问：" + folder); }

        /* 以反斜杠结尾：Windows 通用对话框把 "D:\foo" 当文件名、把 "D:\foo\" 当目录。
         * 不带结尾斜杠时回车可能只清空编辑框，看起来就是"点了没跳转"。 */
        folder = folder.TrimEnd('\\', '/') + "\\";

        if (InputSender.DryRun) return BackfillOutcome.Fail(BackfillRoute.None, "干跑模式：不向任何窗口发输入");
        if (InputSender.IsForegroundHigherIntegrity())
            return BackfillOutcome.Fail(BackfillRoute.None, "目标进程完整性级别更高（UIPI），按红线不做任何绕过");

        bool folderTarget = classification.Kind == DialogKind.FolderDialog;

        if (options.UiaBackfill && classification.Family == DialogFamily.VistaDialog)
        {
            var uia = TryNavigateUia(dialog, folder, folderTarget);
            if (uia.Success) return uia;
            CoreLog.Info($"[QuickSwitch] UIA 导航未成功（{uia.Detail}），改走键盘");
        }

        var route = classification.Family == DialogFamily.LegacyDialog ? BackfillRoute.Editor : BackfillRoute.Keyboard;
        var keyboard = TryNavigateKeyboard(dialog, folder, folderTarget, route);
        if (keyboard.Success) return keyboard;

        Thread.Sleep(120);
        var retry = TryNavigateKeyboard(dialog, folder, folderTarget, route);
        if (retry.Success) return BackfillOutcome.Ok(retry.Route, retry.Detail + "（重试 1 次后成功）");
        return BackfillOutcome.Fail(route, keyboard.Detail + "；重试：" + retry.Detail);
    }

    private static BackfillOutcome TryNavigateUia(DialogWindowInfo dialog, string folder, bool folderTarget)
    {
        if (!DialogUia.TrySetValue(dialog.Hwnd, folder, folderTarget, out string setDetail))
            return BackfillOutcome.Fail(BackfillRoute.Uia, setDetail);

        bool focused = DialogUia.TryFocus(dialog.Hwnd, folderTarget, out string focusDetail);
        if (!focused) focused = ClickEdit(dialog.Hwnd, folderTarget, out focusDetail);
        if (!focused) return BackfillOutcome.Fail(BackfillRoute.Uia, setDetail + "；无法聚焦：" + focusDetail);

        Thread.Sleep(80);
        InputSender.Tap(VK.Return);
        Thread.Sleep(420);
        if (!DialogNative.IsWindow(dialog.Hwnd)) return BackfillOutcome.Fail(BackfillRoute.Uia, "导航后对话框被关闭");
        return BackfillOutcome.Ok(BackfillRoute.Uia, setDetail + "；聚焦成功；已回车导航（对话框保持打开）");
    }

    private static BackfillOutcome TryNavigateKeyboard(DialogWindowInfo dialog, string folder, bool folderTarget, BackfillRoute route)
    {
        if (!FocusEdit(dialog.Hwnd, folderTarget, out string focusDetail))
            return BackfillOutcome.Fail(route, "无法聚焦文件名框：" + focusDetail);

        Thread.Sleep(60);
        InputSender.Ctrl(VkA);
        Thread.Sleep(40);
        if (!PasteText(folder, out string pasteDetail))
            return BackfillOutcome.Fail(route, focusDetail + "；" + pasteDetail);

        Thread.Sleep(140);
        InputSender.Tap(VK.Return);
        Thread.Sleep(420);
        if (!DialogNative.IsWindow(dialog.Hwnd)) return BackfillOutcome.Fail(route, "导航后对话框被关闭");
        return BackfillOutcome.Ok(route, focusDetail + "；" + pasteDetail + "；已回车导航（对话框保持打开）");
    }

    /// <summary>FolderDialog 要目录；选到文件时退到它的父目录。</summary>
    private static string ResolvePath(DialogSelection item, bool folderTarget)
    {
        string path = item.Path?.Trim() ?? "";
        if (path.Length == 0) return "";
        if (!folderTarget) return path;
        if (Directory.Exists(path) || item.IsFolder) return path;

        try
        {
            string? parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(parent) ? path : parent;
        }
        catch
        {
            return path;
        }
    }

    // ---------------- 路线 1：UIA ----------------

    private static BackfillOutcome TryUia(DialogWindowInfo dialog, string path, bool folderTarget)
    {
        if (!DialogUia.TrySetValue(dialog.Hwnd, path, folderTarget, out string setDetail))
            return BackfillOutcome.Fail(BackfillRoute.Uia, setDetail);

        /* 写入之后必须把焦点交还给对话框：否则我们发的回车会被浮层自己吃掉，
         * 对话框一直停在"文件名已填、没人按确定"的状态。 */
        bool focused = DialogUia.TryFocus(dialog.Hwnd, folderTarget, out string focusDetail);
        if (!focused) focused = ClickEdit(dialog.Hwnd, folderTarget, out focusDetail);
        if (!focused)
            return BackfillOutcome.Fail(BackfillRoute.Uia, setDetail + "；但无法聚焦文件名框（" + focusDetail + "）");

        Thread.Sleep(80);
        string submitDetail = Submit(dialog.Hwnd, folderTarget);

        if (Verify(dialog.Hwnd, path, folderTarget, sawOurs: true, out string verify))
            return BackfillOutcome.Ok(BackfillRoute.Uia, setDetail + "；聚焦成功；" + submitDetail + "；" + verify);

        return BackfillOutcome.Fail(BackfillRoute.Uia, setDetail + "；" + submitDetail + "；" + verify);
    }

    // ---------------- 路线 2：键盘 + 剪贴板 ----------------

    private static BackfillOutcome TryKeyboard(DialogWindowInfo dialog, string path, bool folderTarget, BackfillRoute route)
    {
        if (!FocusEdit(dialog.Hwnd, folderTarget, out string focusDetail))
            return BackfillOutcome.Fail(route, "无法聚焦文件名框：" + focusDetail);

        Thread.Sleep(60);
        InputSender.Ctrl(VkA);          // 清掉原来填了一半的内容
        Thread.Sleep(40);

        if (!PasteText(path, out string pasteDetail))
            return BackfillOutcome.Fail(route, focusDetail + "；" + pasteDetail);

        Thread.Sleep(150);
        string submitDetail = Submit(dialog.Hwnd, folderTarget);

        if (Verify(dialog.Hwnd, path, folderTarget, sawOurs: true, out string verify))
            return BackfillOutcome.Ok(route, focusDetail + "；" + pasteDetail + "；" + submitDetail + "；" + verify);

        return BackfillOutcome.Fail(route, focusDetail + "；" + pasteDetail + "；" + submitDetail + "；" + verify);
    }

    /// <summary>
    /// 把填好的路径交给对话框。
    ///
    /// 文件目标：回车 —— 文件名框里输入完整路径后回车，系统自己解析并关闭，
    /// 覆盖确认之类的判断也交给系统。
    /// 文件夹目标：先回车让对话框**进入**这个目录（文件名框里给目录路径时回车是"导航"，
    /// 不是"确认"），再点「选择文件夹」按钮。只点按钮不回车的话，当前目录还停在
    /// 默认位置，等于替用户选错了目录。
    /// </summary>
    private static string Submit(nint dialog, bool folderTarget)
    {
        if (!folderTarget)
        {
            InputSender.Tap(VK.Return);
            return "已回车";
        }

        InputSender.Tap(VK.Return);
        Thread.Sleep(400);

        /* 进目录之后按「选择文件夹」收尾。先按文字找，找不到再退回控件 ID —— 
         * ID 路线在文件夹框上可能点到「取消」，所以只当兜底。 */
        string confirmDetail;
        if (DialogUia.TryInvokeConfirm(dialog, new[] { "选择文件夹", "Select Folder" }, out string uiaConfirm))
        {
            confirmDetail = uiaConfirm;
        }
        else
        {
            ClickConfirmButton(dialog, out string clickDetail);
            confirmDetail = uiaConfirm + "；" + clickDetail;
        }

        return "回车进入目录；" + confirmDetail;
    }

    /// <summary>先 UIA SetFocus，失败再按坐标点一下编辑框（坐标已是物理像素，无需再换算 DPI）。</summary>
    private static bool FocusEdit(nint dialog, bool folderTarget, out string detail)
    {
        if (DialogUia.TryFocus(dialog, folderTarget, out string uia)) { detail = uia; return true; }
        if (ClickEdit(dialog, folderTarget, out string click)) { detail = uia + "；" + click; return true; }
        detail = uia + "；" + click;
        return false;
    }

    /// <summary>
    /// 点对话框的确定按钮（控件 ID 1）。用于文件夹选择框：那里的确定按钮是
    /// 「选择文件夹」，回车未必受理，而按钮一定受理。
    ///
    /// 只在**文件夹目标**上用它；文件目标仍然走回车 —— 保存对话框的「保存」
    /// 按钮会弹覆盖确认，多点一次不如让系统自己走默认流程。
    /// </summary>
    private static bool ClickConfirmButton(nint dialog, out string detail)
    {
        detail = "没找到确定按钮";
        try
        {
            var button = DialogProbe.Children(dialog)
                .FirstOrDefault(c => c.ClassName == "Button" && c.ControlId == 1 && c.Visible);
            if (button == null) return false;
            if (!DialogNative.GetWindowRect(button.Hwnd, out DialogNative.RECT rect)) return false;

            /* 先把对话框提到前台再点：否则鼠标点下去可能落在浮层或别的窗口上，
               表现就是"点了没反应"。SetForegroundWindow 对同进程对话框总是成功。 */
            DialogNative.SetForegroundWindow(dialog);
            Thread.Sleep(120);
            if (DialogNative.GetWindowRect(button.Hwnd, out DialogNative.RECT fresh)) rect = fresh;

            DialogNative.SetCursorPos((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            Thread.Sleep(40);
            InputSender.Click();
            detail = "点击了确定按钮（" + button.Text + "）";
            return true;
        }
        catch (Exception ex)
        {
            detail = "点击确定按钮异常：" + ex.Message;
            return false;
        }
    }

    private static bool ClickEdit(nint dialog, bool folderTarget, out string detail)
    {
        detail = "没有可点击的编辑框";
        try
        {
            var children = DialogProbe.Children(dialog);
            var edit = children.FirstOrDefault(c => c.ClassName == "Edit" && c.ControlId == IdFileNameEdit)
                       ?? children.FirstOrDefault(c => c.ClassName == "Edit" && c.ControlId == IdLegacyEdit)
                       ?? children.FirstOrDefault(c => c.ClassName == "Edit" && c.Visible);
            if (edit == null) return false;

            if (!DialogNative.GetWindowRect(edit.Hwnd, out DialogNative.RECT rect))
            {
                detail = "取编辑框坐标失败";
                return false;
            }

            int cx = (rect.Left + rect.Right) / 2;
            int cy = (rect.Top + rect.Bottom) / 2;
            if (cx <= 0 && cy <= 0)
            {
                detail = "编辑框坐标无效";
                return false;
            }

            DialogNative.SetCursorPos(cx, cy);
            Thread.Sleep(30);
            InputSender.Click();
            detail = $"按坐标点击编辑框 ({cx},{cy})";
            return true;
        }
        catch (Exception ex)
        {
            detail = "点击编辑框异常：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 粘贴完整路径。走 ClipboardPaster：它自己拍完整剪贴板快照（所有格式）、
    /// 发 Ctrl+V、按序号判断"期间用户有没有复制新东西"，最后还原。
    /// 这里绝不再自己写一遍剪贴板逻辑，避免两套还原策略打架。
    /// </summary>
    private static bool PasteText(string text, out string detail)
    {
        if (InputSender.DryRun) { detail = "干跑模式不写剪贴板"; return false; }
        if (!ClipboardPaster.OnStaThread) { detail = "当前线程不是 STA，剪贴板通道不可用"; return false; }

        try
        {
            bool ok = ClipboardPaster.TryPaste(text);
            detail = ok ? "剪贴板粘贴已发送（已备份并还原）" : "剪贴板粘贴失败（快照不可用或剪贴板被占用）";
            return ok;
        }
        catch (Exception ex)
        {
            detail = "剪贴板粘贴异常：" + ex.Message;
            return false;
        }
    }

    // ---------------- 校验 ----------------

    /// <summary>
    /// 提交后 2 秒内校验。判据只有"对话框真的响应了"，不看我们自己写了什么：
    ///
    ///   · 对话框关闭 / 消失了            → 成功（最常见的保存、打开行为）
    ///   · 对话框上弹出一个确认框（覆盖确认、找不到文件…）→ 成功（提交已受理，等用户点确认）
    ///   · 文件夹目标：文件名框从"我们的目录"变成空 → 成功（对话框已进入该目录）
    ///   · 其它情况（框里还留着我们写的路径、什么都没变）→ 失败
    ///
    /// 早先的写法是"框里的内容等于路径就算成功"—— 那是同义反复：值本来就是我们
    /// 用 SetValue / 粘贴写进去的，回车到底有没有被受理完全校验不出来。
    /// </summary>
    private static bool Verify(nint dialog, string path, bool folderTarget, bool sawOurs, out string detail)
    {
        string name = SafeName(path);
        var sw = Stopwatch.StartNew();
        /* 走到这里说明 SetValue / 粘贴已经成功：值确实被写进去过。
         * 于是"框里不再有它"就能解释成"回车让对话框进入了那个目录"。 */

        while (sw.ElapsedMilliseconds < VerifyTimeoutMs)
        {
            if (!DialogNative.IsWindow(dialog))
            {
                detail = "对话框已关闭，校验通过";
                return true;
            }

            /* 覆盖确认这类模态框挂在对话框下面：它出现就说明回车被受理了。 */
            nint popup = DialogNative.GetWindow(dialog, DialogNative.GW_ENABLEDPOPUP);
            if (popup != 0 && popup != dialog && DialogNative.IsWindowVisible(popup))
            {
                detail = "系统在对话框上弹出了确认框，回车已受理";
                return true;
            }

            string? value = DialogUia.ReadValue(dialog, folderTarget, 300);
            if (!string.IsNullOrEmpty(value))
            {
                string v = value.Trim();
                bool ours = v.Equals(path, StringComparison.OrdinalIgnoreCase)
                            || (name.Length > 0 && v.Contains(name, StringComparison.OrdinalIgnoreCase))
                            || v.Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                if (ours) sawOurs = true;
                else if (folderTarget)
                {
                    detail = "对话框已进入该目录（文件名框已被清空）";
                    return true;
                }
            }
            else if (folderTarget && sawOurs)
            {
                detail = "对话框已进入该目录（文件名框已被清空）";
                return true;
            }

            Thread.Sleep(150);
        }

        detail = folderTarget
            ? $"回车后 {VerifyTimeoutMs}ms 内对话框没有进入目录"
            : $"回车后 {VerifyTimeoutMs}ms 内对话框无变化（路径可能无效，或需要用户手动确认）";
        return false;
    }

    private static string SafeName(string path)
    {
        try
        {
            return Path.GetFileName(path.TrimEnd('\\', '/'));
        }
        catch
        {
            return "";
        }
    }
}
