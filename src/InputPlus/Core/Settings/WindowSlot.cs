namespace InputPlus.Core.Settings;

/// <summary>
/// 一个窗口槽位：<c>Caps+数字</c> 记到的那一个程序。
///
/// ## 为什么还要槽位
///
/// 4.5.59 把 CapsLock+ 的 10 个固定槽位动作删了 —— 它们让动作下拉里多出 20 条
/// 噪声（10 个"切换到槽位 N" + 10 个"绑定槽位 N"），而 99% 的绑定根本用不上。
/// 但 Capslox 3.0 的 <c>cx-alt-1..0</c> 语义就是"把当前窗口记到槽位 N"，
/// 这是用户已经练成肌肉记忆的操作，不能丢。
///
/// 结论：**槽位数据留着，动作只留一个** —— <c>bindWindowSlot</c>（参数是槽位号）。
/// 槽位本身不是"另一套绑定"，绑定仍然是普通的 <c>winBind</c> 动作，
/// 只是 exe 路径由这个动作帮用户填进去。
/// </summary>
public sealed class WindowSlot
{
    /// <summary>槽位号 1~10（10 对应 <c>Caps+0</c>）。</summary>
    public int Slot { get; set; }

    /// <summary>可执行文件完整路径。空字符串表示这个槽位没绑过。</summary>
    public string ExePath { get; set; } = "";

    /// <summary>显示名（通常是 exe 文件名，去掉扩展名）。只用于提示和界面显示。</summary>
    public string Name { get; set; } = "";
}
