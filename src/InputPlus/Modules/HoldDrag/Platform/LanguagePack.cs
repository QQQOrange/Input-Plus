#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
using System.Collections.Generic;

namespace InputPlus.Modules.HoldDrag;

/// <summary>
/// 外置语言包的占位实现（移植进 Input Plus 时删掉了 Platform/LanguagePack.cs）。
/// Input Plus 自己的界面文案走 src/InputPlus.Localization，这里只保留 Loc 需要的形状：
/// 扫描结果恒为空，因此 Loc 只会用内置的 zh / en 两张表。
/// </summary>
internal sealed class LanguagePack
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public Dictionary<string, string> Map { get; set; } = new();

    public static List<LanguagePack> LoadAll() => new();
}
