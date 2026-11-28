namespace InputPlus.Core.FileDialog;

/// <summary>
/// 判型：只按「窗口类名 + 子控件类名组合」判定，**绝不看 exe 名**。
///
/// 为什么必须这样：同一个程序会用不同家族的对话框（微信 4.x 用 Vista 对话框、
/// 老版下载工具用 comdlg32），而同一家族的对话框会被成百上千个程序共用。
/// 按 exe 写分支的结果是"每支持一个程序就要发一次版"。
///
/// 判定顺序命中即停，顺序本身是有意义的：
///   a) Vista 文件对话框 —— 有 DirectUIHWND / SHELLDLL_DefView
///   b) 老式文件对话框 —— ComboBoxEx32 + Edit
///   c) 文件夹对话框   —— ShellNameSpace Control / SysTreeView32
///   d) 资源管理器窗口 —— CabinetWClass / ExploreWClass + SHELLDLL_DefView
///   e) 其它           —— Unknown，不挂任何东西
/// </summary>
public static class DialogClassifier
{
    public static DialogClassification Classify(string className, IReadOnlyList<DialogChild> children)
    {
        className ??= "";
        children ??= Array.Empty<DialogChild>();

        bool isDialogClass = string.Equals(className, DialogProbe.DialogClass, StringComparison.OrdinalIgnoreCase);

        if (isDialogClass)
        {
            /* a) Vista+ IFileDialog：DirectUIHWND 是 DirectUI 的宿主，
             *    SHELLDLL_DefView 是文件列表视图（部分版本只有后者）。
             *
             * ⚠ v1.7.14 加严：光有 DirectUIHWND **不够**。
             * 现场（用户报的）：报错窗口上也会冒出这条浮层，可它没有选文件的功能。
             * 原因是 TaskDialog / 某些现代消息框同样是 `#32770 + DirectUIHWND`
             * （实测 WinForms 的 MessageBox 只有 Button+Static，不会误判；
             * 出错的是 DirectUI 画的那一类任务对话框）。
             *
             * 真正的文件对话框一定还带这几样**只有它才有**的控件（实测 Win11 23H2 的
             * IFileDialog 子控件：DUIViewWndClassName / DirectUIHWND / CtrlNotifySink ×4 /
             * NamespaceTreeControl / SysTreeView32 / Shell Preview Extension Host /
             * SHELLDLL_DefView / Static「文件名(&N):」）：
             *   · CtrlNotifySink      —— 导航栏/工具栏的通知汇
             *   · SHELLDLL_DefView    —— 文件列表
             *   · DUIViewWndClassName —— DirectUI 视图壳
             * 这三样都没有、只有 DirectUIHWND 的，一律按"任务对话框/消息框"处理。 */
            bool hasDirectUi = DialogProbe.HasClass(children, "DirectUIHWND");
            bool hasShellView = DialogProbe.HasClass(children, "SHELLDLL_DefView");
            bool hasNotifySink = DialogProbe.HasClass(children, "CtrlNotifySink");
            bool hasDuiView = DialogProbe.HasClass(children, "DUIViewWndClassName");

            if ((hasDirectUi || hasShellView) && (hasNotifySink || hasShellView || hasDuiView))
            {
                string which = hasNotifySink ? "CtrlNotifySink"
                    : (hasShellView ? "SHELLDLL_DefView" : "DUIViewWndClassName");
                return new DialogClassification(DialogKind.FileDialog, DialogFamily.VistaDialog,
                    $"#32770 且含 DirectUIHWND + {which}（Vista+ 通用文件对话框）");
            }

            if (hasDirectUi)
            {
                /* 只有 DirectUIHWND：DirectUI 画的任务对话框、错误框、某些安装器提示。
                 * **绝不能挂浮层** —— 它没有文件名输入框，回填无处可去。 */
                return new DialogClassification(DialogKind.Unknown, DialogFamily.Unknown,
                    "#32770 + DirectUIHWND 但没有文件视图/通知汇（任务对话框或消息框，不挂浮层）");
            }

            // b) 老式 GetOpenFileName：文件名组合框（ComboBoxEx32）里套一个 Edit。
            //    只要这两者同时存在就按老式处理 —— 老式对话框没有 DirectUIHWND，
            //    所以不会和 a) 抢。
            if (DialogProbe.HasClass(children, "ComboBoxEx32") && DialogProbe.HasClass(children, "Edit"))
            {
                return new DialogClassification(DialogKind.FileDialog, DialogFamily.LegacyDialog,
                    "#32770 且含 ComboBoxEx32 + Edit（老式 comdlg32 对话框）");
            }

            // c) 文件夹选择对话框：ShellNameSpace Control 或 SysTreeView32。
            //    注意 SHBrowseForFolder 的树是 SysTreeView32；新版文件夹选择器是 ShellNameSpace。
            if (DialogProbe.HasClass(children, "ShellNameSpace Control", "SysTreeView32"))
            {
                string which = DialogProbe.HasClass(children, "ShellNameSpace Control")
                    ? "ShellNameSpace Control" : "SysTreeView32";
                return new DialogClassification(DialogKind.FolderDialog, DialogFamily.FolderPicker,
                    $"#32770 且含 {which}（文件夹选择对话框）");
            }
        }

        // d) 资源管理器窗口：本次只占位，不挂 Bar。
        if (DialogProbe.ExplorerClasses.Contains(className, StringComparer.OrdinalIgnoreCase))
        {
            if (DialogProbe.HasClass(children, "SHELLDLL_DefView"))
                return new DialogClassification(DialogKind.ExplorerWindow, DialogFamily.Explorer,
                    $"{className} 且含 SHELLDLL_DefView（资源管理器窗口）");

            return new DialogClassification(DialogKind.Unknown, DialogFamily.Unknown,
                $"{className} 但没有 SHELLDLL_DefView（可能是别的窗口壳）");
        }

        return DialogClassification.Unknown with { Reason = $"类名 {className} 未命中任何判据" };
    }
}

/// <summary>
/// 判定阶段的开关/黑名单短路。
///
/// 单独拆出来是为了让"要不要管这个窗口"变成一个纯函数：
/// 自检可以穷举组合，服务层只负责按结果建/不建 Bar。
/// **命中禁用就在判定阶段返回 false，绝不先建窗口再隐藏。**
/// </summary>
public static class DialogPolicy
{
    public static bool ShouldAttach(
        DialogWindowInfo info,
        DialogClassification classification,
        bool masterEnabled,
        IReadOnlyDictionary<string, bool>? typeEnabled,
        IReadOnlyCollection<string>? disabledProcesses,
        out string reason)
    {
        reason = "";

        if (!masterEnabled)
        {
            reason = "总开关关闭";
            return false;
        }

        if (!classification.WantsBar)
        {
            reason = $"类型 {classification.Kind} 不挂浮层";
            return false;
        }

        string key = DialogTypeKeys.Of(classification.Kind);
        if (typeEnabled != null && typeEnabled.TryGetValue(key, out bool on) && !on)
        {
            reason = $"类型开关关闭：{key}";
            return false;
        }

        string exe = info.ProcessName;
        if (!string.IsNullOrEmpty(exe) && disabledProcesses != null)
        {
            foreach (string blocked in disabledProcesses)
            {
                if (string.Equals(blocked?.Trim(), exe, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(blocked?.Trim(), Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"进程被禁用：{exe}";
                    return false;
                }
            }
        }

        return true;
    }
}
