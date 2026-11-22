namespace InputPlus.Core.FileDialog;

/// <summary>
/// 候选窗口的类型。判定顺序见 <see cref="DialogClassifier"/>。
///
/// 这里刻意只有四种：文件对话框、文件夹对话框、资源管理器窗口、未知。
/// 新增类型必须同时回答"谁来挂浮层、谁来负责回填"，否则只会变成一堆没人管的窗口。
/// </summary>
public enum DialogKind
{
    Unknown = 0,

    /// <summary>选文件/存文件（Vista+ IFileDialog 或老式 GetOpenFileName）。</summary>
    FileDialog = 1,

    /// <summary>只选文件夹（SHBrowseForFolder / 文件夹选择器）。</summary>
    FolderDialog = 2,

    /// <summary>资源管理器窗口。本次只占位：不挂浮层，留给 QuickSwitch。</summary>
    ExplorerWindow = 3,
}

/// <summary>同一类对话框的实现家族。回填路线由它决定，而不是由进程名决定。</summary>
public enum DialogFamily
{
    Unknown = 0,

    /// <summary>Vista+ 通用文件对话框（DirectUIHWND / SHELLDLL_DefView）。</summary>
    VistaDialog = 1,

    /// <summary>老式 comdlg32 对话框（ComboBoxEx32 + Edit）。</summary>
    LegacyDialog = 2,

    /// <summary>文件夹选择对话框（ShellNameSpace Control / SysTreeView32）。</summary>
    FolderPicker = 3,

    /// <summary>资源管理器窗口（CabinetWClass / ExploreWClass）。</summary>
    Explorer = 4,
}

/// <summary>把选中项塞回对话框走的路线，记录在 Recorder 里用于排错与排序。</summary>
public enum BackfillRoute
{
    None = 0,

    /// <summary>UI Automation：ValuePattern.SetValue + 提交按钮。</summary>
    Uia = 1,

    /// <summary>老式对话框：聚焦编辑框 + 键盘输入。</summary>
    Editor = 2,

    /// <summary>键盘 + 剪贴板（所有家族共用的兜底）。</summary>
    Keyboard = 3,
}

/// <summary>一次回填的结果。失败也要有原因，调用方只写日志、不弹窗。</summary>
public sealed record BackfillOutcome(bool Success, BackfillRoute Route, string Detail)
{
    public static BackfillOutcome Fail(BackfillRoute route, string detail) => new(false, route, detail);
    public static BackfillOutcome Ok(BackfillRoute route, string detail) => new(true, route, detail);
}

/// <summary>浮层里选中的一项。Bar 只认识这个结构，不认识 Win32。</summary>
public sealed record DialogSelection(string Path, bool IsFolder, string DisplayName, string Source = "")
{
    public string Kind => IsFolder ? "folder" : "file";
}

/// <summary>屏幕矩形（物理像素，左上右下）。Core 层不引用 WPF 的 Rect，避免把 UI 类型带进来。</summary>
public readonly record struct DialogRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public static readonly DialogRect Empty = new(0, 0, 0, 0);
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// 一个候选对话框的静态信息快照。
///
/// 只存"拿到之后不会再变"的字段 + 发现时间；标题、进程名这类要跨进程读的
/// 字段在构造时读一次，之后不再重读 —— 目标进程卡住时不能再碰它。
/// </summary>
public sealed class DialogWindowInfo
{
    public nint Hwnd { get; init; }
    public uint ThreadId { get; init; }
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string ProcessPath { get; init; } = "";
    public string ClassName { get; init; } = "";

    /// <summary>窗口标题。取不到就是空串（跨进程读取会失败，属正常）。</summary>
    public string Title { get; init; } = "";

    public DateTimeOffset FirstSeen { get; init; } = DateTimeOffset.Now;

    public override string ToString()
        => $"{ClassName} hwnd=0x{Hwnd:X} pid={ProcessId} exe={ProcessName} title=\"{Title}\"";
}

/// <summary>枚举到的子窗口。探测与判型都只依赖它，方便自检直接喂合成数据。</summary>
public sealed record DialogChild(nint Hwnd, string ClassName, int ControlId, string Text, bool Visible)
{
    public override string ToString()
        => $"{ClassName} id=0x{ControlId:X4} visible={Visible} text=\"{Text}\"";
}

/// <summary>判型结果：类型 + 家族 + 理由（理由会写进日志，排错时不用猜）。</summary>
public sealed record DialogClassification(DialogKind Kind, DialogFamily Family, string Reason)
{
    public static readonly DialogClassification Unknown =
        new(DialogKind.Unknown, DialogFamily.Unknown, "未命中任何判据");

    public bool WantsBar => Kind is DialogKind.FileDialog or DialogKind.FolderDialog;
}

/// <summary>设置里按类型开关的键。用字符串是为了老配置文件能读（未知键忽略）。</summary>
public static class DialogTypeKeys
{
    public const string FileDialog = "FileDialog";
    public const string FolderDialog = "FolderDialog";
    public const string ExplorerWindow = "ExplorerWindow";

    public static string Of(DialogKind kind) => kind switch
    {
        DialogKind.FileDialog => FileDialog,
        DialogKind.FolderDialog => FolderDialog,
        DialogKind.ExplorerWindow => ExplorerWindow,
        _ => "",
    };
}
