using System;
using InputPlus.Core.Gestures;

namespace InputPlus.Core.Settings;

/// <summary>
/// 手势名称框的外观（设置落在「鼠标手势 → 手势设置」）。
///
/// 出处是 WGestures 2 的 <c>GestureLabelView</c>：圆角色块 + 白字 + 一圈白边 +
/// 黑影，跟着光标停在手笔画完的那块屏上。v1.6.0 曾把它和右下角提示条合并成一套
/// 「通知样式」，实际用下来两边要的东西并不一样 —— 名称框要跟着手势调出现高度、
/// 要分「认出来 / 已执行」两种底色；提示条固定贴右下角、只需要一种底色。
/// 所以这里又拆回两份，字段名与出厂值沿用合并前的那一套（老配置搬过来不用换口径）。
/// </summary>
public sealed class GestureLabelStyle
{
    /// <summary>色块外面那圈白边。</summary>
    public bool ShowBorder { get; set; } = true;

    /// <summary>
    /// 描边宽度。真正画出来时乘 2：WPF 的 BorderThickness 是往内量的，
    /// 而 WGestures 2 的 LabelBorderWidth 是「主线两侧各一圈」的语义，
    /// 名称框一直按这两种口径的换算画（见 GestureOverlayWindow）。
    /// </summary>
    public double BorderWidth { get; set; } = 1;

    /// <summary>平时的底色，RGBA（WG2 写法的 <c>#RRGGBBAA</c>），默认 <c>#00000030</c>。</summary>
    public string ColorNormal { get; set; } = Wg2Color.LabelColorNormal;

    /// <summary>执行完成后的底色：动作跑完了，名称框换成这一档。</summary>
    public string ColorExecuted { get; set; } = Wg2Color.LabelColorExecuted;

    /// <summary>
    /// 贴在屏幕的哪个高度：0 = 贴底，1 = 贴顶，水平居中 ——
    /// 算式是 <c>y = top + (h - labelH) × (1 - pos)</c>（WG2 的 A/T.cs:387-393）。
    /// </summary>
    public double Position { get; set; } = 0.25;

    /// <summary>
    /// 迁移标记：0 = 还没搬过，2 = 已经按「手势名称 / 提示条」两套的结构搬完。
    ///
    /// 1 是 v1.6.0 合并期的版本号 —— 那阵子这五项已经合并进
    /// <see cref="NotificationStyle"/>，所以读一份老配置可能停在任意一档：
    /// 停在 0 就还得从 <see cref="GestureOptions"/> 上搬，停在 1 就从合并期那份搬。
    /// 搬到 2 之后不再回头看老字段，用户在界面上改的值也就不会被压回去。
    /// </summary>
    public int MigrateVersion { get; set; }

    /// <summary>当前迁移版本（见 <see cref="MigrateVersion"/>）。</summary>
    public const int MigrateVersionCurrent = 2;

    public static GestureLabelStyle Default => new();
}

/// <summary>
/// 右下角提示条的外观（设置 → 外观 → 提示条）。
///
/// 出厂值回到 v1.6.0 之前、也是 v1.0~v1.2 一直用的那一版：
/// 主题卡片（浅色半透明白卡 / 深色半透明墨卡）+ 22px 图标 + 13px 文字，
/// 圆角 12、贴工作区右下角 10px，没有阴影。那时没有可调描边与底色，
/// 所以这里的两个开关默认「关 / 最小值」，底色留空表示跟随主题 ——
/// 只有用户真去设置里调过，才把值写死。
///
/// 只有一种底色：提示条没有「已执行」这个状态 —— 它本身就是动作已经发生的结果。
/// 位置也不可调，固定贴工作区右下角，所以这里没有 Position。
/// </summary>
public sealed class ToastStyle
{
    /// <summary>
    /// 卡片外面那圈白边。默认关：旧观感里只有主题卡片自带的 1px 发丝边
    /// （<c>Overlay.CardAlt</c> 的 BorderBrush），不是这圈可调的白边。
    /// </summary>
    public bool ShowBorder { get; set; }

    /// <summary>
    /// 描边宽度，画出来时同样乘 2（口径与名称框一致）。
    /// 默认取最小值：开关关着时根本不画，打开后也从最细的一档开始。
    /// </summary>
    public double BorderWidth { get; set; } = 0.5;

    /// <summary>
    /// 底色，RGBA（<c>#RRGGBBAA</c>）。默认空串 = 跟随主题：
    /// 用 <c>Brush.OverlayCardAlt</c>，浅色是半透明白卡、深色是半透明墨卡，
    /// 这就是 v1.6.0 之前提示条的底色。只有用户在设置里挑过颜色才写死 RGBA。
    /// </summary>
    public string Color { get; set; } = "";

    /// <summary>
    /// 迁移标记：0 = 还没按「回退到旧观感」搬过；1 = 已经搬完。
    ///
    /// 只看一次：老配置里三项如果还是 v1.6.3 的出厂值（白边开 / 1.0 / #00000030），
    /// 说明用户从没调过，搬回旧观感；只要动过一项就原样保留。
    /// 搬完置 1，用户以后就算手动调回同样的值，也不会再被重置。
    /// </summary>
    public int MigrateVersion { get; set; }

    /// <summary>当前迁移版本（见 <see cref="MigrateVersion"/>）。</summary>
    public const int MigrateVersionCurrent = 1;

    public static ToastStyle Default => new();
}

/// <summary>
/// v1.6.0 合并期的「通知样式」：**只为读老配置保留**。
///
/// 现在没有任何界面往里写。<see cref="SettingsStore.Normalize"/> 把值搬给
/// <see cref="GestureLabelStyle"/> 与 <see cref="ToastStyle"/> 之后就把这份对象置空，
/// 于是它也不会再被写回文件 —— 配置里只留新的两段，免得以后改哪一份都说不清。
/// </summary>
public sealed class NotificationStyle
{
    /// <summary>色块外面那圈白边。</summary>
    public bool ShowBorder { get; set; } = true;

    /// <summary>描边宽度（画出来时乘 2）。</summary>
    public double BorderWidth { get; set; } = 1;

    /// <summary>平时的底色，RGBA。</summary>
    public string ColorNormal { get; set; } = Wg2Color.LabelColorNormal;

    /// <summary>执行完成后的底色。</summary>
    public string ColorExecuted { get; set; } = Wg2Color.LabelColorExecuted;

    /// <summary>贴在屏幕的哪个高度（0 贴底、1 贴顶）。</summary>
    public double Position { get; set; } = 0.25;

    /// <summary>迁移标记：1 = v1.6.0 已经把这些值从 GestureOptions 搬过来了。</summary>
    public int MigrateVersion { get; set; }

    /// <summary>合并期的版本号（判断这份值是不是用户真调过的依据）。</summary>
    public const int MigrateVersionCurrent = 1;

    public static NotificationStyle Default => new();
}

/// <summary>
/// 手势名称框外观的实时取值口。
///
/// 为什么要一个取值器而不是直接存一份对象：托盘「重新加载配置」会把整份
/// <see cref="AppSettings"/> 换掉（AppHost.Settings 是 <c>set</c> 属性），
/// 缓存下来的旧对象从此不会再被读；取值器每次都问当前设置，换多少次都是新的。
/// 自检 / 渲染工具里没有 AppHost，<see cref="Current"/> 就回落到一份出厂值。
///
/// 与 <see cref="ToastStyles"/> 分开挂，是因为两套配置本来就没有关系：
/// 名称框改了颜色，右下角的提示条不该跟着变。
/// </summary>
public static class GestureLabelStyles
{
    private static Func<GestureLabelStyle?>? _provider;
    private static readonly GestureLabelStyle Fallback = new();

    /// <summary>由 AppHost 在构造时挂上：<c>() => Settings?.GestureLabel</c>。</summary>
    public static void Attach(Func<GestureLabelStyle?>? provider) => _provider = provider;

    /// <summary>当前生效的名称框外观（永远非 null）。</summary>
    public static GestureLabelStyle Current
    {
        get
        {
            try { return _provider?.Invoke() ?? Fallback; }
            catch { return Fallback; }
        }
    }
}

/// <summary>右下角提示条外观的实时取值口（理由与 <see cref="GestureLabelStyles"/> 完全一致）。</summary>
public static class ToastStyles
{
    private static Func<ToastStyle?>? _provider;
    private static readonly ToastStyle Fallback = new();

    /// <summary>由 AppHost 在构造时挂上：<c>() => Settings?.Toasts</c>。</summary>
    public static void Attach(Func<ToastStyle?>? provider) => _provider = provider;

    /// <summary>当前生效的提示条外观（永远非 null）。</summary>
    public static ToastStyle Current
    {
        get
        {
            try { return _provider?.Invoke() ?? Fallback; }
            catch { return Fallback; }
        }
    }
}
