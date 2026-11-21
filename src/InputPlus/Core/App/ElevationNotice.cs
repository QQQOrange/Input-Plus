using System;
using System.IO;

namespace InputPlus.Core.App;

/// <summary>
/// "以管理员身份运行"的一次性说明。
///
/// 管理员模式下有一部分行为是**权限边界决定的**，不是 bug：钩子只看得见同级或更低
/// 完整性级别的输入，往更高级别的窗口注入会被 UIPI 挡回来。现在的实现把这两件事
/// 拆开了（钩子留在提升进程、注入下沉给普通权限中继），所以多数功能在管理员下
/// 也能用；但用户仍然有权知道"哪里会不一样"，以及"怎么切回普通权限"。
///
/// 只提示一次：标记文件落在数据目录里（--selftest 用 INPUTPLUS_DATA 的临时目录，
/// 天然隔离，不会污染真实安装）。
/// </summary>
internal static class ElevationNotice
{
    /// <summary>提示"已经看过"的标记文件名。</summary>
    internal const string FileName = "elevation-hint.txt";

    /// <summary>标记文件的完整路径（数据目录 + 文件名）。</summary>
    internal static string MarkerPath
    {
        get
        {
            string dir = "";
            try { dir = AppPaths.DataDir; } catch { }
            return Path.Combine(dir ?? "", FileName);
        }
    }

    /// <summary>该不该提示：只有"我们是提升的"并且"没提示过"才提示。</summary>
    internal static bool ShouldShow(bool elevated) => elevated && !HasShown();

    /// <summary>标记文件是否已经存在（提示过就为 true）。</summary>
    internal static bool HasShown()
    {
        try
        {
            string path = MarkerPath;
            return path.Length > 0 && File.Exists(path);
        }
        catch { return false; }
    }

    /// <summary>写下标记。写不进去也不要紧 —— 最坏是下次启动再提示一遍。</summary>
    internal static void MarkShown()
    {
        try
        {
            string path = MarkerPath;
            if (path.Length == 0) return;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "写入权限提示标记"); }
    }

    /// <summary>自检用：清掉标记，模拟"从没提示过"。</summary>
    internal static void ResetForTest()
    {
        try
        {
            string path = MarkerPath;
            if (path.Length > 0 && File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>
    /// 一次性判定 + 落标记。返回 true 表示"这次该弹提示"。
    ///
    /// 抽成一个方法是为了让自检能在不建界面的前提下验完整语义
    /// （首次 true、第二次 false、未提升永远 false）。
    /// </summary>
    internal static bool ConsumeOnce(bool elevated)
    {
        if (!ShouldShow(elevated)) return false;
        MarkShown();
        return true;
    }

    /// <summary>提示正文（中文原文，翻译在 Notify 那一层做）。</summary>
    internal static string Text =>
        "已以管理员身份运行：全局钩子照常工作，能看到所有程序的按键；"
        + "发送按键改由普通权限的中继进程代发，普通程序和以管理员运行的程序都能收到。"
        + "仍然做不到的是系统保护窗口（UAC 安全桌面、登录界面）—— 任何进程都注入不进去。"
        + "如果热键在你机器上完全不响应（个别安全软件会拦全局钩子），"
        + "请用托盘菜单或设置里的「以普通权限重启」回到普通权限。";
}
