using System;

namespace InputPlus.Core.Windows;

/// <summary>「打开文件所在位置」这次到底要打开什么。</summary>
internal enum RevealFallbackKind
{
    /// <summary>认出了前台程序打开的文件 → 定位这个文件（原来的行为）。</summary>
    File = 0,

    /// <summary>没认出文件 → 打开**这个程序自己的 exe 所在文件夹并选中它**（v1.7.9 起，v1.7.14 补选中）。</summary>
    HostExe = 1,

    /// <summary>什么也打不开（桌面 / 资源管理器 / 取不到程序路径）。</summary>
    None = 2,
}

/// <summary>一次"要打开哪里"的决定（纯数据，便于自检）。</summary>
/// <param name="Path">要交给资源管理器的路径（文件或 exe）。</param>
/// <param name="FolderOnly">true = 只开目录、不选中；给的是 exe 时必须是 false（要选中它）。</param>
/// <param name="Kind">走到哪一层。</param>
/// <param name="Why">人话原因（只进日志）。</param>
internal readonly record struct RevealFallbackPlan(string Path, bool FolderOnly, RevealFallbackKind Kind, string Why)
{
    /// <summary>
    /// 这一条"用户在看的那个东西在哪儿"该怎么答。
    ///
    /// 顺序是硬的，改顺序等于改行为：
    ///   ① 认出文件 → 定位文件（**这个优先级最高，任何情况下都不许被退路抢走**）；
    ///   ② 前台是桌面 / 资源管理器 / 任务栏 → 什么也不做（那不是"某个程序打开的文件"，
    ///      退路也不能开 C:\Windows，那是 explorer 自己的目录）；
    ///   ③ 认不出文件、但前台确实是个程序 → 打开这个程序 exe 所在的文件夹**并选中这个 exe**
    ///      （微信、Electron、浏览器这些程序手里根本没有用户文件的句柄）；
    ///   ④ 连程序路径都拿不到（提升进程且没有同名兄弟进程 / 系统组件）→ 什么也不做。
    ///
    /// ⚠ v1.7.14 把 ③ 从"只开文件夹"改成"开文件夹 + 选中 exe"：用户点的是
    /// 「打开文件所在位置」，窗口开出来却谁也没选中，看起来就是只做了一半。
    /// 选中 exe 走的是 `SHOpenFolderAndSelectItems(exe)` / `explorer /select,"<exe>"`，
    /// 不是拿目录去调那个 API（那样会打开上级，见 v1.7.10 的现场）。
    /// </summary>
    /// <param name="result">识别结果（可能失败）。</param>
    /// <param name="pid">前台进程号（result.Pid 为 0 时用它）。</param>
    /// <param name="processName">前台进程名（退路要按名字找同名进程）。</param>
    /// <param name="folderOnly">设置里"只开文件夹"（只对**文件**生效；退路必须选中 exe）。</param>
    /// <param name="hostExeOf">可注入的"程序 exe 路径"查询（自检用；null 走真实实现）。</param>
    internal static RevealFallbackPlan Decide(
        ForegroundFileResult result,
        int pid,
        string processName,
        bool folderOnly,
        Func<int, string?, string>? hostExeOf = null)
    {
        if (result.File is { } file)
            return new RevealFallbackPlan(file.Path, folderOnly, RevealFallbackKind.File, "认出文件：" + file.ProcessName);

        if (result.Outcome == ForegroundFileOutcome.NotActionable)
            return new RevealFallbackPlan("", false, RevealFallbackKind.None,
                "前台是桌面/资源管理器/任务栏，不是某个程序打开的文件");

        int target = result.Pid > 0 ? result.Pid : pid;
        string exe = (hostExeOf ?? ForegroundFileProbe.HostExeOf)(target, processName);
        if (exe.Length == 0)
            return new RevealFallbackPlan("", false, RevealFallbackKind.None,
                $"没认出文件（outcome={result.Outcome}），也取不到程序路径（pid={target} proc={processName}）");

        return new RevealFallbackPlan(exe, false, RevealFallbackKind.HostExe,
            $"没认出文件（outcome={result.Outcome}），退一步打开程序所在文件夹并选中它");
    }
}
