using System;

namespace InputPlus.Core.Settings;

/// <summary>
/// 窗口槽位的读写：槽位号 ↔ 组合键 ↔ 第 1 层绑定。
///
/// 这里只做"记账 + 写一条绑定"，真正的切换/启动仍然走
/// <see cref="InputPlus.Core.Actions.WindowBindingService.ActivateExe"/> ——
/// 槽位不是第二套窗口逻辑，只是帮用户把 <c>winBind</c> 的参数填好。
/// </summary>
public static class WindowSlotBinder
{
    /// <summary>槽位数（Caps+1~0）。</summary>
    public const int SlotCount = 10;

    /// <summary>槽位落点用的动作 id。</summary>
    public const string WinBindAction = "winBind";

    /// <summary>
    /// 解析动作参数里的槽位号。
    /// 合法返回 1~10，其它一切（空、非数字、越界）返回 0 —— **不抛异常**。
    /// </summary>
    public static int ParseSlot(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return 0;
        if (!int.TryParse(arg.Trim(), out int slot)) return 0;
        return slot >= 1 && slot <= SlotCount ? slot : 0;
    }

    /// <summary>槽位对应的组合键 id：1~9 → caps_1~caps_9，10 → caps_0。</summary>
    public static string ComboFor(int slot)
    {
        if (slot < 1 || slot > SlotCount) return "";
        return "caps_" + (slot == SlotCount ? "0" : slot.ToString());
    }

    /// <summary>槽位号在键盘上显示的那个字符（1~9、0）。</summary>
    public static string DigitFor(int slot)
        => slot < 1 || slot > SlotCount ? "" : (slot == SlotCount ? "0" : slot.ToString());

    /// <summary>找这个槽位已经记下的程序（没记过返回 null）。</summary>
    public static WindowSlot? Find(AppSettings? settings, int slot)
    {
        if (settings?.WindowSlots == null) return null;
        foreach (var s in settings.WindowSlots)
        {
            if (s != null && s.Slot == slot) return s;
        }
        return null;
    }

    /// <summary>
    /// 把 <paramref name="exePath"/> 记进槽位，并把**第 1 层**的
    /// <c>caps_&lt;N&gt;</c> 写成 <c>winBind</c> + 该路径。
    ///
    /// 只改第 1 层：Capslox 的 <c>cx-alt-1..0</c> 只定义了基础层的那一次按键，
    /// 其它层（Ctrl/Shift/Win 子层）里用户可能自己绑过别的意思，不替他改。
    ///
    /// 幂等：同一个槽位重复绑定 = 覆盖（不会越绑越多条）。
    /// 非法输入返回 false 并给出中文原因，**不抛异常**。
    /// </summary>
    public static bool TryBind(AppSettings? settings, int slot, string? exePath, string? name, out string message)
    {
        message = "";
        if (settings == null)
        {
            message = "设置还没加载好";
            return false;
        }
        if (slot < 1 || slot > SlotCount)
        {
            message = Loc.F("槽位号必须是 1~{0}", SlotCount);
            return false;
        }
        if (string.IsNullOrWhiteSpace(exePath))
        {
            message = "没有拿到前台窗口的程序路径";
            return false;
        }

        string path = exePath.Trim();
        string display = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name.Trim();

        // 1) 记槽位（同号覆盖，顺手清掉历史重复条目）
        settings.WindowSlots ??= new List<WindowSlot>();
        for (int i = settings.WindowSlots.Count - 1; i >= 0; i--)
        {
            if (settings.WindowSlots[i] == null || settings.WindowSlots[i].Slot == slot)
                settings.WindowSlots.RemoveAt(i);
        }
        settings.WindowSlots.Add(new WindowSlot { Slot = slot, ExePath = path, Name = display });

        // 2) 第 1 层的 caps_<N> 指向这个程序
        string combo = ComboFor(slot);
        var keys = settings.LayerKeys(1);
        KeyBinding? entry = null;
        foreach (var k in keys)
        {
            if (k != null && string.Equals(k.Combo, combo, StringComparison.OrdinalIgnoreCase))
            {
                entry = k;
                break;
            }
        }
        if (entry == null)
        {
            entry = new KeyBinding { Combo = combo };
            keys.Add(entry);
        }
        entry.Action = WinBindAction;
        entry.Arg = path;

        message = Loc.F("槽位 {0} 已绑定：{1}", DigitFor(slot), display);
        return true;
    }
}
