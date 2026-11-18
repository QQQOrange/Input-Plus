using System;
using System.Collections.Generic;
using System.Linq;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 一条鼠标手势。
///
/// 多种触发方式共用一张表，靠 <see cref="Kind"/> 区分：
/// <list type="bullet">
/// <item><c>stroke</c>：按住 <see cref="Button"/> 画一笔，形状匹配 <see cref="Path"/>；</item>
/// <item><c>corner</c>：把指针甩到屏幕角上单击 <see cref="Button"/>；</item>
/// <item><c>wheel</c>：按住 <see cref="Button"/> 时滚轮朝 <see cref="Wheel"/> 方向拨；</item>
/// <item><c>hotcorner</c>：指针悬停在屏幕角落就触发（不用按键，照 WGestures 的热角）；</item>
/// <item><c>edgewheel</c>：指针贴在屏幕边缘时滚轮朝 <see cref="Wheel"/> 方向拨。</item>
/// </list>
/// 都用同一套动作执行管线，所以"手势 → 宏/按键/启动程序"的能力天然一致。
/// </summary>
public sealed class MouseGesture
{
    /// <summary>稳定 ID：界面用它定位，导入时用它去重。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。空的就按动作标题显示。</summary>
    public string Name { get; set; } = "";

    /// <summary>起始键：left / right / middle / x1 / x2。</summary>
    public string Button { get; set; } = "right";

    /// <summary>
    /// 组合键：按住 <see cref="Button"/> 的同时再点这一颗。
    /// 空 = 普通手势。WGestures 里「按住右键再点左键」这类特殊手势就靠它，
    /// 导入时第二颗鼠标键落在这里，不丢。
    /// </summary>
    public string Chord { get; set; } = "";

    /// <summary>触发类型：stroke / corner / wheel / hotcorner / edgewheel。</summary>
    public string Kind { get; set; } = "stroke";

    /// <summary>
    /// 笔画模板：x0,y0,x1,y1… 的扁平数组，已归一化到 0~1000。
    ///
    /// 归一化发生在录制/导入时，识别时只做同样的归一化再比距离 ——
    /// 这样模板与"用户这次画多大"无关，拇指画一小段和手臂甩一大段算同一笔。
    /// </summary>
    public List<int> Path { get; set; } = new();

    /// <summary>
    /// 方向串：<c>↑→↓←</c> 的组合。v5.13 起它只是「人话摘要」：
    /// 识别比的是 <see cref="Path"/> 那一笔的形状（WGestures 引擎），方向串不参与判定；
    /// 只有没存过轨迹的存量手势才由 <see cref="GesturePattern.Sketch"/> 把它画成
    /// 等长折线补进 <see cref="Path"/>，进的是同一个引擎。
    /// 老数据没有这个字段，读的时候用 <see cref="EnsurePattern"/> 从 Path 反推。
    /// </summary>
    public string Pattern { get; set; } = "";

    /// <summary>滚轮方向：-1 上、+1 下、-2 左、+2 右（仅 Kind=wheel）。</summary>
    public int Wheel { get; set; } = -1;

    /// <summary>角落：0 任意角、1 左上、2 右上、3 左下、4 右下（Kind=corner / hotcorner）。</summary>
    public int Corner { get; set; }

    /// <summary>屏幕边缘：0 无、1 上、2 右、3 下、4 左（仅 Kind=edgewheel）。</summary>
    public int Edge { get; set; }

    /// <summary>动作 ID（见 ActionCatalog）。</summary>
    public string Action { get; set; } = "";

    /// <summary>动作参数（启动程序路径、文本、宏 ID…）。</summary>
    public string Arg { get; set; } = "";

    /// <summary>限定应用（可执行文件全路径）。空 = 全局。</summary>
    public string AppPath { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 画到“已经能认出来”就立刻执行，不必等松开鼠标键。
    /// 默认关：正常手势在松手时执行，和以前一样。仅对 stroke 生效。
    /// </summary>
    public bool Immediate { get; set; }

    /// <summary>执行这条手势时不在覆盖层显示手势名称（轨迹仍然可以显示）。</summary>
    public bool HideLabel { get; set; }

    /// <summary>来源标记：从 WGestures 导入的会带上，方便用户知道哪些是搬过来的。</summary>
    public bool FromWGestures { get; set; }

    /// <summary>导入时无法自动翻译的说明（例如 Lua 脚本），显示在列表里提醒用户。</summary>
    public string Note { get; set; } = "";

    public int PathPointCount => Path.Count / 2;

    public bool HasPath => Path.Count >= 4;

    /// <summary>通配手势：任何方向都能触发（FlowMouse 的 <c>*</c>）。</summary>
    public bool IsWildcard => Pattern == GesturePattern.Wildcard;

    /// <summary>
    /// 保证 <see cref="Pattern"/> 有值：老数据 / 导入时漏算的，从归一化路径反推一次。
    /// 空值才补齐，不按路径重算已有方向串（用户可能手工改过）。
    ///
    /// 但已有值**也会过一遍** <see cref="GesturePattern.Clean"/>：老版本把一条斜线
    /// 记成 ↗↑↗↑↗↑↗ 这种，光靠显示层合并会跟存档不一致，这里就地收敛，
    /// 保存时就写成一条了。
    /// </summary>
    public void EnsurePattern()
    {
        if (IsWildcard) return;
        if (!string.IsNullOrEmpty(Pattern))
        {
            if (Kind == "stroke") Pattern = GesturePattern.Clean(Pattern);
            return;
        }

        if (Kind != "stroke" || !HasPath) return;
        Pattern = GesturePattern.FromNormalized(Points());
    }

    /// <summary>把扁平数组还原成点列（识别时用）。</summary>
    public List<GestureMath.Pt> Points()
    {
        var list = new List<GestureMath.Pt>(Path.Count / 2);
        for (int i = 0; i + 1 < Path.Count; i += 2)
            list.Add(new GestureMath.Pt(Path[i], Path[i + 1]));
        return list;
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>显示名：没填名字就用动作标题，再不行用触发方式的通用叫法。</summary>
    public string Title(string? actionTitle = null)
    {
        if (!string.IsNullOrWhiteSpace(Name)) return Name;
        if (!string.IsNullOrWhiteSpace(actionTitle)) return actionTitle!;
        return Kind switch
        {
            "corner" => "角落触发",
            "hotcorner" => "热角触发",
            "wheel" => "滚轮触发",
            "edgewheel" => "边缘滚轮",
            _ => "手势"
        };
    }

    /// <summary>复制一份（界面里"取消"要能还原，不能改到列表里的活对象）。</summary>
    public MouseGesture Clone() => new()
    {
        Id = Id,
        Name = Name,
        Button = Button,
        Chord = Chord,
        Kind = Kind,
        Path = new List<int>(Path),
        Pattern = Pattern,
        Wheel = Wheel,
        Corner = Corner,
        Edge = Edge,
        Action = Action,
        Arg = Arg,
        AppPath = AppPath,
        Enabled = Enabled,
        Immediate = Immediate,
        HideLabel = HideLabel,
        FromWGestures = FromWGestures,
        Note = Note
    };

    /// <summary>把所有字段用 <paramref name="other"/> 覆盖（保持对象引用不变，列表绑定不会跳）。</summary>
    public void CopyFrom(MouseGesture other)
    {
        Name = other.Name;
        Button = other.Button;
        Chord = other.Chord;
        Kind = other.Kind;
        Path = new List<int>(other.Path);
        Pattern = other.Pattern;
        Wheel = other.Wheel;
        Corner = other.Corner;
        Edge = other.Edge;
        Action = other.Action;
        Arg = other.Arg;
        AppPath = other.AppPath;
        Enabled = other.Enabled;
        Immediate = other.Immediate;
        HideLabel = other.HideLabel;
        FromWGestures = other.FromWGestures;
        Note = other.Note;
    }
}

/// <summary>手势功能的全局选项。</summary>
public sealed class GestureOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>手势作用目标：指针下方的应用（默认）。</summary>
    public const string TargetPointer = "pointer";

    /// <summary>手势作用目标：当前活动的应用窗口。</summary>
    public const string TargetActive = "active";

    /// <summary>
    /// 手势作用目标（见 <see cref="TargetPointer"/> / <see cref="TargetActive"/>）：
    ///   · pointer（默认）= 指针下方的应用：手势画在哪个程序上就作用在哪个程序上，
    ///     执行前还会把那个窗口切到前面，快捷键才落得进去；
    ///   · active = 活动的应用窗口：只作用在还拿着焦点的那个程序上，不切窗口。
    ///
    /// 存字符串而不是枚举：配置里写了不认识的词也只退回默认值，
    /// 不会在反序列化时把整份设置弄丢。
    /// </summary>
    public string TargetMode { get; set; } = TargetPointer;

    /// <summary>这份设置是不是"作用于指针下方的应用"。空值、未知值都按默认（指针下方）处理。</summary>
    public bool UsesPointerTarget =>
        !string.Equals(TargetMode, TargetActive, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 拖动时在屏幕上画轨迹。5.12 起这个开关的"正主"是 <see cref="ShowPath"/>
    /// （WGestures 2 的字段名），这里保留成同一份值的别名：老配置里关过轨迹的，
    /// 升级后仍然是关的，不必再跑一次迁移。
    /// </summary>
    public bool ShowTrail { get; set; } = true;

    /// <summary>轨迹颜色（#RRGGBB）。</summary>
    public string TrailColor { get; set; } = "#3B82F6";

    public double TrailWidth { get; set; } = 3.0;

    /// <summary>轨迹不透明度，1=完全不透明，0.2=很淡。</summary>
    public double TrailOpacity { get; set; } = 1.0;

    /// <summary>
    /// 分按键轨迹颜色：#RRGGBB。键为 left / right / middle / x1 / x2。
    /// 没配的键回退到 <see cref="TrailColor"/>；清空字典就是“所有按键统一颜色”。
    /// </summary>
    public Dictionary<string, string> ButtonTrailColors { get; set; } = new();

    /// <summary>
    /// 这些程序里不触发任何手势（包括全局手势、边缘手势）。
    /// 存 exe 全路径；比较时也允许按文件名匹配，和 AppMatches 一致。
    /// </summary>
    public List<string> DisabledTargets { get; set; } = new();

    /// <summary>取某颗鼠标键的轨迹颜色；没单独配过就用统一颜色。</summary>
    public string TrailColorFor(string? button)
    {
        string key = (button ?? "").Trim().ToLowerInvariant();
        if (key.Length > 0 && ButtonTrailColors != null &&
            ButtonTrailColors.TryGetValue(key, out string? custom) &&
            !string.IsNullOrWhiteSpace(custom))
            return custom;

        return string.IsNullOrWhiteSpace(TrailColor) ? "#3B82F6" : TrailColor;
    }

    /// <summary>
    /// 最小识别距离（像素）。起点到当前点超过这么多才算"画了一笔"，
    /// 否则当单击处理；手势页上叫「最小识别距离」。
    /// </summary>
    public int MinMove { get; set; } = 30;

    /// <summary>
    /// 起始超时（毫秒）：按下鼠标后这么久还没画出最小识别距离，
    /// 就取消这一笔并在鼠标尖端闪一个红点。默认 500，可在手势页调整。
    /// </summary>
    public int StartTimeoutMs { get; set; } = 500;

    /// <summary>
    /// 鼠标手势总开关的快捷键（例如 <c>ctrl+alt+g</c>）。空 = 不注册。
    /// 关闭后所有鼠标手势都不再开始，但快捷键仍然有效，便于再打开。
    /// </summary>
    public string ToggleHotkey { get; set; } = "";

    /// <summary>起手距离：走出这么多像素才开始记方向（FlowMouse 默认 20）。</summary>
    public int ActivationDistance { get; set; } = 20;

    /// <summary>
    /// 转折系数：换方向要越过 起手距离 + 上一段长度 × 这个系数。
    /// 越大越"懒得拐弯"，适合手抖；越小越容易切出短段落。
    /// </summary>
    public double TurnTolerance { get; set; } = 0.10;

    /// <summary>最大转折距离：再长的一笔，换方向也不该超过这么多像素。</summary>
    public int MaxTurnDistance { get; set; } = 120;

    /* ───────── 识别引擎（WGestures 2 移植，唯一引擎） ─────────
     *
     * 手势识别只有 WGestures 2 这一套：笔画压成 (累计弧长 s, 方向角 θ) 序列，
     * 再用带 ε 的动态规划量两条笔画的形状距离。画快画慢、画大画小、稍微歪一点
     * 都算同一条手势，"↑→"与"↗→"这种只差 22.5° 的歧义串也不会互相误判。
     *
     * 5.13 起老的方向串 + 形状距离双层判定（FlowMouse）**整个删掉**：
     * 没有"识别引擎"开关，也没有留下备用通路 —— 只跑这一套。
     */

    /// <summary>
    /// WGestures 引擎的匹配阈值（形状距离，0 = 完全一样）。
    /// 小于等于它才算同一条手势；越大越容易认中，也越容易认错。
    /// </summary>
    public double WgThreshold { get; set; } = 0.05;

    /// <summary>
    /// 「画到一半就执行」用的阈值：必须比 <see cref="WgThreshold"/> 更严，
    /// 否则一条长手势的前半截就能把后半截的动作抢走。
    /// </summary>
    public double WgImmediateThreshold { get; set; } = 0.035;

    /// <summary>
    /// 歧义容差（形状距离）：最佳与次佳相差不到这么多时不下结论。
    /// 0.01 是 WGestures 自己的择优常量（反编译源码 A/m.cs:129），
    /// 在那里它是"两个候选互相太像就先别执行"的判据，这里沿用同一把尺子。
    /// </summary>
    public double WgTieMargin { get; set; } = 0.01;

    /// <summary>角落感应区边长（像素）。</summary>
    public int CornerSize { get; set; } = 24;

    /// <summary>
    /// 热角感应区：指针离屏幕角不超过这么多像素就算"贴到角上"。
    /// WGestures 用 2px，这里默认 8px，手指不用像素级瞄准。
    /// </summary>
    public int HotCornerSize { get; set; } = 8;

    /// <summary>离开热角这么远之后，同一条热角手势才允许再次触发。</summary>
    public int HotCornerReset { get; set; } = 80;

    /// <summary>贴边滚轮的边缘厚度（像素）：指针进入边缘这么近时滚轮算边缘手势。</summary>
    public int EdgeSize { get; set; } = 3;

    /// <summary>
    /// 内置边角手势的播种版本。0 = 还没播过；升到 1 后用户删掉默认项也不会再塞回来。
    /// </summary>
    public int BuiltinsVersion { get; set; }

    /// <summary>
    /// 上一次从 WGestures 自动导入的配置路径。
    /// 空 = 还没导过；非空 = 只导一次，用户删光手势也不会被重新塞回来。
    /// </summary>
    public string AutoImportSource { get; set; } = "";

    /// <summary>
    /// 手势页左侧「作用目标」里手动加过、但还没配手势的应用。
    /// 只有这些路径也列出来，用户才能先建目标再画手势。
    /// </summary>
    public List<string> Targets { get; set; } = new();

    /// <summary>画完后多少毫秒内仍显示识别出的手势名。</summary>
    public int LabelLingerMs { get; set; } = 1200;

    /* ───────── WGestures 2 偏好（prefs.json 的同名项） ─────────
     *
     * 这一段是照着 WGestures 2 的偏好页抄的：一条不多、一条不少。
     * 字段名用 WG2 的叫法（Path/Label/GestureName），值也用它出厂默认值，
     * 这样两边截图放在一起能一一对上。颜色串是 RGBA（见 <see cref="Wg2Color"/>）。
     */

    /// <summary>
    /// 手势轨迹总开关（WG2 的 ShowPath）。和 <see cref="ShowTrail"/> 是同一份值：
    /// 两个都开着才画 —— 老配置里 ShowTrail=false 就当作关。
    /// </summary>
    public bool ShowPath
    {
        get => _showPath && ShowTrail;
        set => _showPath = value;
    }

    private bool _showPath = true;

    /// <summary>轨迹描边（WG2 的 ShowPathBorder）。关掉就是一条没有白边的实线。</summary>
    public bool ShowPathBorder { get; set; } = true;

    /// <summary>未识别时的轨迹颜色，RGBA。默认 #00000030 = 19% 的黑。</summary>
    public string PathColorNormal { get; set; } = Wg2Color.PathColorNormal;

    /// <summary>识别出结果后的轨迹颜色，RGBA。默认 #FF5900FF = WGestures 的橙红。</summary>
    public string PathColorRecognized { get; set; } = Wg2Color.PathColorRecognized;

    /// <summary>轨迹粗细（WG2 的 PathLineWidth，界面范围 1~4，默认 1.5）。</summary>
    public double PathLineWidth { get; set; } = 1.5;

    /// <summary>画完后显示手势名称（WG2 的 ShowGestureName）。</summary>
    public bool ShowGestureName { get; set; } = true;

    /// <summary>执行后淡出标签（WG2 的 FadeOutExecuted）。关掉就按普通淡出慢慢消失。</summary>
    public bool FadeOutExecuted { get; set; } = true;

    /// <summary>标签描边（WG2 的 ShowLabelBorder）。</summary>
    public bool ShowLabelBorder { get; set; } = true;

    /// <summary>标签描边宽度（WG2 的 LabelBorderWidth，默认 1）。</summary>
    public double LabelBorderWidth { get; set; } = 1;

    /// <summary>未识别标签的颜色，RGBA（WG2 的 LabelColorNormal）。</summary>
    public string LabelColorNormal { get; set; } = Wg2Color.LabelColorNormal;

    /// <summary>已执行标签的颜色，RGBA（WG2 的 LabelColorExecuted）。</summary>
    public string LabelColorExecuted { get; set; } = Wg2Color.LabelColorExecuted;

    /// <summary>手势名称在屏幕上的位置：0 = 贴底，1 = 贴顶，默认 0.25（距底 25%）。</summary>
    public double GestureNamePos { get; set; } = 0.25;

    /// <summary>起始超时后显示倒计时标志（WG2 的 ShowStartDragTimeoutIndicator）。</summary>
    public bool ShowStartTimeoutIndicator { get; set; } = true;

    /// <summary>手势作用目标里的边角/贴边触发总开关（WG2 的 TriggerList 顶层）。</summary>
    public List<GestureTarget> TargetList { get; set; } = GestureTargetDefaults.All();

    /// <summary>取一份补齐过的目标清单（读配置时统一走这里，老配置也能用）。</summary>
    public List<GestureTarget> Targets2()
    {
        TargetList ??= new List<GestureTarget>();
        GestureTargetDefaults.Ensure(TargetList);
        return TargetList;
    }

    public static GestureOptions Default => new();
}

/// <summary>按钮名 ↔ 虚拟键码。设置里存名字而不是数字，配置读起来才像人话。</summary>
public static class GestureButtons
{
    public const string Left = "left";
    public const string Right = "right";
    public const string Middle = "middle";
    public const string X1 = "x1";
    public const string X2 = "x2";

    public static readonly string[] All = { Right, Middle, X2, X1, Left };

    public static int ToVk(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "left" or "mouseleft" => 0x01,
        "right" or "mouseright" => 0x02,
        "middle" or "mousemiddle" => 0x04,
        "x1" or "mousex1" => 0x05,
        "x2" or "mousex2" => 0x06,
        _ => 0
    };

    public static string FromVk(int vk) => vk switch
    {
        0x01 => Left,
        0x02 => Right,
        0x04 => Middle,
        0x05 => X1,
        0x06 => X2,
        _ => ""
    };

    public static string Display(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "left" => "鼠标左键",
        "right" => "鼠标右键",
        "middle" => "鼠标中键",
        "x1" => "侧键 1",
        "x2" => "侧键 2",
        _ => "鼠标右键"
    };
}
