using InputPlus.Core.Input;

namespace InputPlus.Core;

/// <summary>
/// 全局热键串的解析（"Ctrl+Alt+H" → 修饰键 + 虚拟键码）。
///
/// 单独抽出来是因为它有两处用户：文件对话框浮层的显隐热键、建议的隐藏热键。
/// 两份解析器迟早会不一致（大小写、AltGr、Win 键别名），到那时用户看到的是
/// "同一个写法在设置页 A 能用、在设置页 B 不能用"。
/// </summary>
internal static class HotkeyCombo
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>注册时用的修饰键（不含 MOD_NOREPEAT，由调用方决定）。</summary>
    public static (uint Mods, int Vk)? Parse(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;

        uint mods = 0;
        int vk = 0;
        foreach (string raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= ModControl;
                    continue;
                case "shift":
                    mods |= ModShift;
                    continue;
                case "alt":
                    mods |= ModAlt;
                    continue;
                case "win":
                case "windows":
                case "meta":
                    mods |= ModWin;
                    continue;
            }

            int? code = KeyName.VkOf(part);
            if (code is null or 0) return null;
            vk = code.Value;
        }

        /* 只按修饰键不算热键：RegisterHotKey 会成功注册但永远不触发，
         * 用户以为设好了，其实是死键。 */
        if (vk == 0 || mods == 0) return null;
        return (mods, vk);
    }

    /// <summary>规范化写法（Ctrl+Shift+Alt+Win 顺序），设置页回显用。</summary>
    public static string Normalize(string? combo)
    {
        var parsed = Parse(combo);
        if (parsed == null) return "";
        var parts = new List<string>(4);
        if ((parsed.Value.Mods & ModControl) != 0) parts.Add("Ctrl");
        if ((parsed.Value.Mods & ModShift) != 0) parts.Add("Shift");
        if ((parsed.Value.Mods & ModAlt) != 0) parts.Add("Alt");
        if ((parsed.Value.Mods & ModWin) != 0) parts.Add("Win");
        string key = KeyName.NameOrGenerated(parsed.Value.Vk);
        if (key.Length == 1) key = key.ToUpperInvariant();
        parts.Add(key);
        return string.Join("+", parts);
    }
}
