using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Automation;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// Backfill 的 UIA 通道：在对话框里找"文件名 / 文件夹"编辑框，并读/写它的值。
///
/// 为什么单独一层：
///   · Bar 不认识 Win32，Backfill 也不该认识 UI 树 —— 两个家族（Vista / 老式）
///     的差别在**控件结构**上，把这部分收在一个类里，回填流程只剩"聚焦 → 写入 → 提交"。
///   · UIA 是跨进程 COM 调用，目标程序卡 UI 线程时会一直阻塞。所有调用都必须
///     跑在独立 STA 线程上并带硬超时（借 UiaSelectionReader 的结论），
///     绝不占用 WPF UI 线程。
///
/// 探测结论（本机实测，见 FILE-DIALOG-BAR.md）：Vista 通用对话框的文件名框
/// AutomationId 是 "1001"（Win32 控件 ID 0x03E9 = 1001）；老式 GetOpenFileName
/// 的编辑框是 edt1 = 0x0480，UIA 侧没有稳定 AutomationId，只能靠 Name
/// （"文件名:" / "File name:"）与"父级是 ComboBox"来定位。
/// </summary>
internal static class DialogUia
{
    /// <summary>单次 UIA 调用的默认预算。正常对话框查询在 20–120ms 内返回。</summary>
    internal const int DefaultTimeoutMs = 900;

    /// <summary>文件名编辑框的探测结果。</summary>
    internal readonly record struct EditInfo(bool Found, bool CanSetValue, bool ReadOnly, string Value, string Name, string AutomationId)
    {
        internal static EditInfo Missing => new(false, false, false, "", "", "");
    }

    /// <summary>找编辑框并读出当前值（不改动任何东西）。</summary>
    internal static EditInfo Inspect(nint dialog, bool folderTarget, int timeoutMs = DefaultTimeoutMs)
    {
        if (dialog == 0) return EditInfo.Missing;
        var result = EditInfo.Missing;
        RunSta(() =>
        {
            var edit = FindEdit(dialog, folderTarget);
            if (edit == null) return true;
            result = Describe(edit);
            return true;
        }, timeoutMs);
        return result;
    }

    /// <summary>
    /// 这个 #32770 是不是"选文件夹"模式。
    ///
    /// 判型的五条规则只能看到控件结构：Win11 的 FolderBrowserDialog 也是
    /// DirectUIHWND 家族，和保存对话框长得一样，唯一区别是 UIA 里那个编辑框
    /// 叫「文件夹:」（AutomationId 1152）而不是「文件名:」（AutomationId 1001）。
    /// 提交动作两者完全不同（前者要按「选择文件夹」，后者要回车），所以必须分开。
    /// </summary>
    internal static bool IsFolderPicker(nint dialog, int timeoutMs = DefaultTimeoutMs)
    {
        if (dialog == 0) return false;
        bool found = false;
        RunSta(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(dialog);
                if (root == null) return true;
                var edits = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                foreach (AutomationElement edit in edits)
                {
                    string name;
                    try { name = edit.Current.Name ?? ""; } catch { continue; }
                    if (Contains(name, "文件夹") || Contains(name, "Folder"))
                    {
                        found = true;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "识别文件夹选择框");
            }
            return true;
        }, timeoutMs);
        return found;
    }

    /// <summary>
    /// 用 ValuePattern 把完整路径写进文件名框。返回 false 表示不可用，
    /// 调用方应退化为"键盘 + 剪贴板"（见 DialogBackfill）。
    /// </summary>
    internal static bool TrySetValue(nint dialog, string path, bool folderTarget, out string detail, int timeoutMs = DefaultTimeoutMs)
    {
        string why = "";
        bool ok = false;
        if (dialog == 0) { detail = "对话框句柄为空"; return false; }

        RunSta(() =>
        {
            var edit = FindEdit(dialog, folderTarget);
            if (edit == null) { why = "没找到文件名编辑框"; return true; }

            var info = Describe(edit);
            if (info.ReadOnly) { why = "编辑框是只读的"; return true; }

            try
            {
                if (edit.GetCurrentPattern(ValuePattern.Pattern) is not ValuePattern pattern)
                {
                    why = "编辑框不支持 ValuePattern";
                    return true;
                }
                pattern.SetValue(path);
                ok = true;
                why = $"UIA ValuePattern 写入（AutomationId={info.AutomationId}, Name={info.Name}）";
            }
            catch (Exception ex)
            {
                why = "ValuePattern.SetValue 失败：" + ex.Message;
            }
            return true;
        }, timeoutMs);

        detail = why;
        return ok;
    }

    /// <summary>把焦点移到文件名编辑框（ValuePattern 不可用时的前置动作）。</summary>
    internal static bool TryFocus(nint dialog, bool folderTarget, out string detail, int timeoutMs = DefaultTimeoutMs)
    {
        string why = "";
        bool ok = false;
        if (dialog == 0) { detail = "对话框句柄为空"; return false; }

        RunSta(() =>
        {
            var edit = FindEdit(dialog, folderTarget);
            if (edit == null) { why = "没找到文件名编辑框"; return true; }
            try
            {
                edit.SetFocus();
                ok = true;
                why = "UIA SetFocus 成功";
            }
            catch (Exception ex)
            {
                why = "UIA SetFocus 失败：" + ex.Message;
            }
            return true;
        }, timeoutMs);

        detail = why;
        return ok;
    }

    /// <summary>
    /// 按**按钮文字**点对话框里的确认按钮（UIA InvokePattern）。
    ///
    /// 为什么不用"控件 ID == 1"：文件夹选择框的按钮布局和文件对话框不同，
    /// 同一套 ID 在那边可能落在「取消」上 —— 按下就变成"用户点了取消"。
    /// 按文字匹配只用于文件夹目标（「选择文件夹」/「Select Folder」），
    /// 文件目标仍然走回车，不碰按钮。
    /// </summary>
    internal static bool TryInvokeConfirm(nint dialog, IReadOnlyList<string> names, out string detail, int timeoutMs = DefaultTimeoutMs)
    {
        string why = "对话框句柄为空";
        bool ok = false;
        if (dialog == 0) { detail = why; return false; }

        RunSta(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(dialog);
                var buttons = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                for (int i = 0; i < buttons.Count; i++)
                {
                    AutomationElement button = buttons[i];
                    string name = button.Current.Name ?? "";
                    bool match = false;
                    foreach (string candidate in names)
                        if (name.Contains(candidate, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                    if (!match) continue;
                    if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern)) continue;
                    ((InvokePattern)pattern).Invoke();
                    why = "UIA 按文字点击「" + name + "」";
                    ok = true;
                    return true;
                }
                why = "没找到可点击的确认按钮（" + string.Join("/", names) + "）";
            }
            catch (Exception ex)
            {
                why = "UIA 点击确认按钮失败：" + ex.Message;
            }
            return true;
        }, timeoutMs);

        detail = why;
        return ok;
    }

    /// <summary>诊断用：把对话框里编辑框与按钮的 UIA 身份摊平成一个字符串。</summary>
    internal static string DescribeTree(nint dialog, int timeoutMs = DefaultTimeoutMs)
    {
        string text = "（UIA 树读取超时）";
        if (dialog == 0) return "（对话框句柄为空）";
        RunSta(() =>
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var root = AutomationElement.FromHandle(dialog);
                var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                int shown = 0;
                for (int i = 0; i < all.Count && shown < 40; i++)
                {
                    AutomationElement e = all[i];
                    var type = e.Current.ControlType;
                    if (type != ControlType.Edit && type != ControlType.Button) continue;
                    shown++;
                    sb.Append(type.ProgrammaticName.Replace("ControlType.", ""))
                      .Append(" name=[").Append(e.Current.Name ?? "")
                      .Append("] id=[").Append(e.Current.AutomationId ?? "")
                      .Append("] enabled=").Append(e.Current.IsEnabled)
                      .Append("; ");
                }
                if (shown == 0) sb.Append("（没有编辑框/按钮）");
            }
            catch (Exception ex) { sb.Append("UIA 树读取失败：").Append(ex.Message); }
            text = sb.ToString();
            return true;
        }, timeoutMs);
        return text;
    }

    /// <summary>读文件名框当前文本，读不到返回 null。</summary>
    internal static string? ReadValue(nint dialog, bool folderTarget, int timeoutMs = DefaultTimeoutMs)
    {
        string? value = null;
        if (dialog == 0) return null;
        RunSta(() =>
        {
            var edit = FindEdit(dialog, folderTarget);
            if (edit == null) return true;
            try
            {
                if (edit.GetCurrentPattern(ValuePattern.Pattern) is ValuePattern pattern)
                    value = pattern.Current.Value;
            }
            catch
            {
                /* 预期内：探测型读取，读不到就当没有 */
            }
            return true;
        }, timeoutMs);
        return value;
    }

    /// <summary>
    /// 在 STA 线程上跑一段 UIA 代码并等它结束。
    /// 超时就直接返回（后台线程自己会结束），调用方只当这次 UIA 不可用。
    /// </summary>
    private static void RunSta(Func<bool> work, int timeoutMs)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "InputPlus.FileDialog.UIA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(Math.Max(150, timeoutMs)))
        {
            CoreLog.Warn($"[文件对话框] UIA 调用 {timeoutMs}ms 未返回（目标可能无响应），本次跳过 UIA");
            return;
        }

        if (failure != null) CoreLog.Swallow(failure, "文件对话框 UIA");
    }

    /// <summary>
    /// 找文件名 / 文件夹编辑框。
    ///
    /// 打分而不是"取第一个"：Vista 对话框里还有搜索框、地址栏编辑框，
    /// 位置顺序在不同 Windows 版本上会变；只有 AutomationId=1001 与
    /// Name 命中才是稳定特征。
    /// </summary>
    private static AutomationElement? FindEdit(nint dialog, bool folderTarget)
    {
        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(dialog);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "UIA 取对话框根元素");
            return null;
        }
        if (root == null) return null;

        AutomationElementCollection edits;
        try
        {
            edits = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "UIA 枚举编辑框");
            return null;
        }

        AutomationElement? best = null;
        int bestScore = int.MinValue;
        foreach (AutomationElement edit in edits)
        {
            try
            {
                if (edit.Current.IsOffscreen) continue;

                string id = edit.Current.AutomationId ?? "";
                string name = edit.Current.Name ?? "";
                int score = 0;

                if (id == "1001") score += 100;
                if (Contains(name, "文件名") || Contains(name, "File name")) score += 60;
                if (folderTarget && (Contains(name, "文件夹") || Contains(name, "Folder"))) score += 60;
                if (Contains(name, "搜索") || Contains(name, "Search")) score -= 80;
                if (Contains(name, "地址") || Contains(name, "Address")) score -= 40;
                if (score == 0) continue;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = edit;
                }
            }
            catch
            {
                /* 单个元素在遍历途中被销毁是常态，跳过即可 */
            }
        }
        return best;
    }

    private static EditInfo Describe(AutomationElement edit)
    {
        string value = "";
        bool canSet = false;
        bool readOnly = false;
        try
        {
            if (edit.GetCurrentPattern(ValuePattern.Pattern) is ValuePattern pattern)
            {
                value = pattern.Current.Value ?? "";
                readOnly = pattern.Current.IsReadOnly;
                canSet = !readOnly;
            }
        }
        catch
        {
            /* 预期内：不是所有编辑框都支持 ValuePattern */
        }

        string id = "", name = "";
        try { id = edit.Current.AutomationId ?? ""; } catch { }
        try { name = edit.Current.Name ?? ""; } catch { }
        return new EditInfo(true, canSet, readOnly, value, name, id);
    }

    private static bool Contains(string text, string part)
        => text.Length > 0 && text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
