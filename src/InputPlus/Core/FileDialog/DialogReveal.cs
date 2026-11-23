using System;
using InputPlus.Core.Windows;

namespace InputPlus.Core.FileDialog;

/// <summary>在资源管理器里定位一个文件/文件夹。回填失败或用户按 Alt+Enter 时用。</summary>
internal static class DialogReveal
{
    /// <summary>
    /// 定位并返回"请求已经发出去"。
    ///
    /// v1.5.1 起不再自己拼 <c>explorer.exe /select,"…"</c>：那是仓库里第三份同样的代码了，
    /// 各拼各的必然有一条漏引号。统一走 <see cref="WindowService.RevealInForeground"/> ——
    /// 它还带 <c>SHOpenFolderAndSelectItems</c>（复用已开窗口、自带选中）、
    /// 提权时改借 shell 之手、以及"文件被删了逐级上溯"。
    ///
    /// 为什么一定放后台：in-proc 那条通道最多等 3 秒（shell 忙 / 网络位置离线），
    /// 而这里是在对话框回填链路里被调用的，堵住界面线程就是"整个另存为卡死"。
    /// </summary>
    public static bool TryReveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        RevealTarget target = RevealPath.Plan(path, openFolderOnly: false);
        if (!target.Reachable)
        {
            CoreLog.Warn($"[文件对话框] 在资源管理器里定位失败：位置不可达（{path}）");
            return false;
        }

        _ = Task.Run(() => WindowService.RevealInForeground(path, openFolderOnly: false));
        return true;
    }
}
