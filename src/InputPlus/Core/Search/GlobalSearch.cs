using InputPlus.Core.Actions;
using InputPlus.Core.Settings;
using InputPlus.Core.Localization;

namespace InputPlus.Core.Search;

/// <summary>
/// 一条全局搜索结果。
///
/// 全部是**导航型**结果（跳到某个页面 / 选中某条别名），不直接执行动作 ——
/// 搜索框里误按回车就执行"打开程序"太危险了。
/// </summary>
/// <param name="Kind">左侧小徽章上的分类（页面 / 功能 / 动作 / 手势 …）。</param>
/// <param name="Title">主标题。</param>
/// <param name="Detail">右侧灰字：它在哪、是什么。</param>
/// <param name="Page">回车跳到哪个页面；空 = 只做展示。</param>
/// <param name="AliasTrigger">别名结果：跳过去后顺手选中这一条。</param>
/// <param name="Keywords">额外的搜索词（英文名、同义词），不显示。</param>
public sealed record GlobalHit(
    string Kind,
    string Title,
    string Detail,
    string? Page = null,
    string? AliasTrigger = null,
    string? Keywords = null);

/// <summary>
/// 全局搜索（Ctrl+K）。
///
/// 索引在打开面板时现建：数据量小（几十条别名 + 几百条绑定 + 两百来条动作），
/// 建索引比维护增量索引可靠得多 —— 不会出现"改了配置但搜不到"。
///
/// v1.6.0 起索引范围 = **软件的全部功能**，不只是页面：
///   页面 / 页面里的功能（设置项、开关、按钮）/ 动作目录 / 手势 / 宏 / Lua 脚本 /
///   手势菜单 / Qbar 条目 / 别名 / 触发键 / 层内绑定。
/// 每条都带 <see cref="GlobalHit.Keywords"/>，中文名搜不到时可以用英文和同义词找
///（"dark"「夜间」都能找到主题，「索引」能找到 Qbar 的文件搜索）。
/// </summary>
public static class GlobalSearch
{
    /// <summary>页面清单：和左侧导航一致（顺序也一致）。</summary>
    private static readonly (string Key, string Title, string Keywords)[] Pages =
    {
        ("guide", "快速上手", "guide quick start 上手 教程 新手 第一次"),
        ("keys", "按键映射", "key remap 按键 映射 改键 绑定 层"),
        ("hotstrings", "别名替换", "alias snippet 别名 缩写 展开 文本片段"),
        ("qbar", "Qbar 面板", "qbar launcher 面板 搜索 启动 计算"),
        ("clipboard", "剪贴板", "clipboard 剪贴板 历史 粘贴"),
        ("macros", "宏", "macro 宏 录制 回放 循环"),
        ("layers", "触发键与层", "layer trigger 触发键 层 切换"),
        ("gestures", "鼠标手势", "gesture 手势 鼠标 画 笔画"),
        ("scroll", "平滑滚动", "scroll smooth 滚动 滚轮 缓动"),
        ("holddrag", "按住拖拽", "holddrag altdrag 拖拽 拖动 缩放 窗口 按住"),
        ("backup", "同步与备份", "sync backup webdav 同步 备份 导出 导入"),
        ("settings", "设置", "settings 设置 选项 全局 外观 主题 通知"),
        ("test", "测试", "test 测试 诊断 按键 钩子"),
        ("help", "快捷键速查", "help 帮助 速查 快捷键"),
        ("about", "关于", "about version 关于 版本 更新"),
    };

    /// <summary>
    /// 页面里的功能：设置项、开关、按钮都算一条。
    ///
    /// 这些是"搜得到但以前搜不到"的主体 —— 用户想找"通知样式""长按阈值""全屏不接管"，
    /// 以前只能在页面里翻，现在 Ctrl+K 直接跳过去。
    /// </summary>
    private static readonly (string Page, string Title, string Detail, string Keywords)[] Features =
    {
        // ---- 按键映射 ----
        ("keys", "改键：单击出什么、长按出什么", "按键映射", "key remap 改键 映射 单击 长按 动作"),
        ("keys", "复合键与组合键", "按键映射 · 触发键 + 字母", "combo chord 组合键 复合键 连按"),
        ("keys", "新建层 / 切换层", "按键映射 · 层", "layer 层 切换 多层"),
        ("keys", "设备方案：按键盘分别配置", "按键映射 · 设备", "device profile 设备 键盘 方案"),
        ("keys", "长按多久才算长按", "按键映射 · 长按", "hold long press 长按 毫秒 阈值"),

        // ---- 别名替换 ----
        ("hotstrings", "别名替换：打缩写自动展开", "别名替换", "alias snippet 别名 缩写 展开 空格"),
        ("hotstrings", "别名前缀（触发词前面加符号）", "别名替换 · 前缀", "prefix 前缀 触发词"),

        // ---- Qbar ----
        ("qbar", "Qbar 面板：搜索 / 启动 / 计算", "Qbar 面板", "qbar launcher 面板 搜索 启动 计算器"),
        ("qbar", "Qbar 的文件搜索（内置索引）", "Qbar · 文件搜索", "文件 搜索 全盘 文件名 索引 index search"),
        ("qbar", "Qbar 外观与位置", "Qbar · 外观", "qbar appearance 外观 主题 位置 宽度 圆角"),

        // ---- 剪贴板 ----
        ("clipboard", "剪贴板历史", "剪贴板", "clipboard 剪贴板 历史 粘贴 图片"),
        ("clipboard", "固定常用条目", "剪贴板 · 固定", "pin 固定 收藏 常用"),

        // ---- 宏 ----
        ("macros", "宏：录制、编辑、回放", "宏", "macro 宏 录制 回放 循环 步骤"),

        // ---- 触发键与层 ----
        ("layers", "触发键：按住进第 N 层", "触发键与层", "trigger 触发键 层 按住 松开"),

        // ---- 鼠标手势 ----
        ("gestures", "鼠标手势：按住鼠标键画一笔", "鼠标手势", "gesture 手势 画 笔画 鼠标键"),
        ("gestures", "手势动作目录（140+ 种动作）", "鼠标手势 · 动作", "action 动作 目录 分类 列表"),
        ("gestures", "多步手势（最多 8 步）", "鼠标手势 · 多步", "step 多步 分段 停顿 连续"),
        ("gestures", "手势菜单（画完弹一列快选）", "鼠标手势 · 菜单", "menu 菜单 快选 数字 选项"),
        ("gestures", "Lua 脚本动作", "鼠标手势 · Lua", "lua script 脚本 模板 moonSharp 代码"),
        ("gestures", "手势设置：外观与参数", "鼠标手势 · 右上角齿轮", "settings 设置 外观 参数 轨迹 颜色 名称 描边 位置 容差 起手 换向"),
        ("gestures", "全屏程序里不接管", "鼠标手势 · 设置 · 手势参数", "fullscreen 全屏 游戏 不接管 禁用"),
        ("gestures", "录制新手势 / 回放看效果", "鼠标手势 · 顶部按钮", "record replay 录制 回放 演示 预览"),

        // ---- 平滑滚动 ----
        ("scroll", "平滑滚动：滚轮缓动", "平滑滚动", "scroll smooth 滚动 滚轮 缓动 曲线 步长"),
        ("scroll", "按程序单独配置滚动", "平滑滚动 · 程序例外", "app 程序 例外 每个程序 单独"),

        // ---- 按住拖拽 ----
        ("holddrag", "按住拖拽：按住触发键拖窗口", "按住拖拽", "holddrag altdrag 拖拽 拖动 缩放 窗口 enter"),
        ("holddrag", "按住拖拽的触发键（默认 Enter）", "按住拖拽 · 触发键", "trigger 触发键 enter 回车 自定义"),
        ("holddrag", "长按多久才算数", "按住拖拽 · 长按", "hold 长按 毫秒 阈值 误触"),
        ("holddrag", "只让勾上的键盘发出触发键", "按住拖拽 · 设备", "device 键盘 设备 只认 多选"),
        ("holddrag", "程序例外 / 窗口例外", "按住拖拽 · 当前状态", "exception 例外 黑名单 程序 窗口"),
        ("holddrag", "窗口吸附", "按住拖拽 · 吸附", "snap 吸附 aero 网格 距离 多显示器"),

        // ---- 同步与备份 ----
        ("backup", "WebDAV 同步", "同步与备份", "webdav sync 同步 坚果云 网盘"),
        ("backup", "导出 / 导入配置", "同步与备份", "export import 导出 导入 备份 配置"),

        // ---- 设置 ----
        ("settings", "主题：浅色 / 深色 / 跟随系统", "设置 · 外观", "theme dark light 主题 深色 浅色 夜间 白天"),
        ("settings", "提示条：描边、底色", "设置 · 外观 · 提示条", "toast notification 通知 提示 描边 底色 颜色 字号"),
        ("settings", "界面语言（中文 / English）", "设置 · 外观", "language 语言 english 中文 界面"),
        ("settings", "触发键星标", "设置 · 外观", "star 星标 触发键 强调 图标"),
        ("settings", "数据与维护：打开数据目录 / 重置", "设置", "data 数据 目录 重置 维护 清理"),
        ("settings", "暂停 / 恢复全部功能", "标题栏右侧的「暂停」按钮 · 托盘菜单", "pause 暂停 恢复 全部 停止 一键"),
        ("settings", "托盘菜单与开机启动", "设置 · 托盘图标右键", "tray 托盘 开机启动 autostart 自动"),

        // ---- 测试 / 帮助 / 关于 ----
        ("test", "按键测试与诊断", "测试", "test 测试 诊断 按键 钩子 权限"),
        ("help", "快捷键速查", "快捷键速查", "help 帮助 速查 快捷键 说明"),
        ("about", "版本与更新", "关于", "about version 版本 更新 日志"),
    };

    /// <summary>建立一个搜索索引。</summary>
    /// <param name="settings">当前设置。</param>
    /// <param name="translate">翻译函数（一般传 <c>Loc.T</c>）。</param>
    /// <param name="actions">动作目录；传 null 时不索引动作。</param>
    public static List<GlobalHit> Build(
        AppSettings settings,
        Func<string, string> translate,
        IReadOnlyList<ActionInfo>? actions = null)
    {
        var hits = new List<GlobalHit>();

        foreach (var (key, title, keywords) in Pages)
        {
            hits.Add(new GlobalHit("页面", translate(title), Loc.T("打开这个页面"), Page: key, Keywords: keywords));
        }

        foreach (var (page, title, detail, keywords) in Features)
        {
            hits.Add(new GlobalHit("功能", translate(title), translate(detail), Page: page, Keywords: keywords));
        }

        /* 动作目录：两百来条，是"这个软件能干什么"的最全清单。 */
        foreach (var action in actions ?? Array.Empty<ActionInfo>())
        {
            if (action.Hidden) continue;

            string category = translate(action.Category);
            string detail = string.IsNullOrWhiteSpace(action.Description)
                ? category
                : category + " · " + Shorten(action.Description, 40);

            hits.Add(new GlobalHit("动作", translate(action.Title), detail, Page: "keys",
                Keywords: action.Title + " " + action.Category + " " + action.ShortTitle + " " + action.Description));
        }

        /* 鼠标手势：一条一条列出来，搜名字就能定位（页面里再自己找）。 */
        foreach (var gesture in settings.Gestures)
        {
            string name = string.IsNullOrWhiteSpace(gesture.Name) ? Loc.T("未命名手势") : gesture.Name.Trim();
            string where = string.IsNullOrWhiteSpace(gesture.Chord) ? gesture.Button : gesture.Button + " + " + gesture.Chord;
            string action = string.IsNullOrWhiteSpace(gesture.Action) ? Loc.T("还没绑动作") : actionTitle(gesture.Action);

            hits.Add(new GlobalHit("手势", name, Loc.F("{0} · {1}", where, Shorten(action, 36)),
                Page: "gestures", Keywords: gesture.Note + " " + gesture.Pattern + " " + where));
        }

        /* 宏。 */
        foreach (var macro in settings.Macros)
        {
            string name = string.IsNullOrWhiteSpace(macro.Name) ? Loc.T("未命名宏") : macro.Name.Trim();
            string detail = Loc.F("宏 · {0} 步 · 循环 {1}", macro.Steps.Count, macro.Repeat <= 0 ? Loc.T("无限") : macro.Repeat.ToString());
            hits.Add(new GlobalHit("宏", name, detail, Page: "macros", Keywords: macro.Comment));
        }

        /* Lua 脚本。 */
        foreach (var script in settings.Scripts)
        {
            hits.Add(new GlobalHit("脚本", script.DisplayName,
                Loc.F("Lua 脚本 · {0}", Shorten(script.Comment, 32)),
                Page: "gestures", Keywords: "lua script 脚本 " + script.Name + " " + script.Comment));
        }

        /* 手势菜单。 */
        foreach (var menu in settings.GestureMenus)
        {
            hits.Add(new GlobalHit("菜单", menu.DisplayName,
                Loc.F("手势菜单 · {0} 项", menu.Items.Count),
                Page: "gestures", Keywords: "menu 菜单 " + menu.Hint + " " + menu.Name));
        }

        /* Qbar 里的条目：搜索 / 运行 / 网址 / 文本片段。 */
        AddQbar(hits, "qbar", "Qbar", Loc.T("Qbar · 搜索"), settings.Search);
        AddQbar(hits, "qbar", "Qbar", Loc.T("Qbar · 运行"), settings.Run);
        AddQbar(hits, "qbar", "Qbar", Loc.T("Qbar · 网址"), settings.Web);
        AddQbar(hits, "qbar", "Qbar", Loc.T("Qbar · 固定"), settings.Qbar.Pinned);

        /* 别名：显示时去掉前缀（和列表一致）。 */
        var prefixes = settings.Hotstring?.EffectivePrefixes ?? new List<string>();
        foreach (var snippet in settings.Global.Snippets)
        {
            string name = SnippetPrefixOps.StripAny(snippet.Trigger, prefixes);
            if (name.Length == 0) continue;

            string detail = !string.IsNullOrWhiteSpace(snippet.Comment)
                ? snippet.Comment!
                : Shorten(snippet.Text);

            hits.Add(new GlobalHit("别名", name, detail, Page: "hotstrings", AliasTrigger: snippet.Trigger,
                Keywords: snippet.Trigger + " " + snippet.Group));
        }

        /* 触发键。 */
        foreach (var trigger in settings.Triggers.Where(t => t.Enabled))
        {
            hits.Add(new GlobalHit("触发键", trigger.Key,
                Loc.F("{0} 毫秒后进入第 {1} 层", trigger.HoldMs, trigger.LayerId), Page: "layers",
                Keywords: "trigger 触发键 " + trigger.Key));
        }

        /* 层内绑定。 */
        foreach (var layer in settings.Layers)
        {
            foreach (var binding in layer.Keys)
            {
                if (!binding.IsBound) continue;

                string action = string.IsNullOrWhiteSpace(binding.Arg)
                    ? binding.Action
                    : binding.Action + " " + binding.Arg;

                hits.Add(new GlobalHit("绑定", binding.Combo,
                    Loc.F("第 {0} 层 · {1}", layer.Id, Shorten(action, 40)), Page: "layers",
                    Keywords: binding.Combo + " " + action));
            }
        }

        return hits;

        string actionTitle(string id) => ActionCatalog.TitleOf(id) ?? id;
    }

    private static void AddQbar(List<GlobalHit> hits, string page, string kind, string prefix, List<QEntry>? entries)
    {
        if (entries == null) return;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key)) continue;

            string detail = string.IsNullOrWhiteSpace(entry.Comment)
                ? Loc.F("{0} · {1}", prefix, Shorten(entry.Value, 40))
                : Loc.F("{0} · {1}", prefix, Shorten(entry.Comment, 40));

            hits.Add(new GlobalHit(kind, entry.Key.Trim(), detail, Page: page,
                Keywords: entry.Value + " " + entry.Comment + " " + entry.Group));
        }
    }

    /// <summary>
    /// 过滤 + 排序。规则简单但可预期：
    ///   前缀命中 > 标题包含 > 关键词命中 > 说明包含；多词是"且"的关系。
    ///   同类里按标题长度（越短越可能是你要的）。
    /// </summary>
    public static List<GlobalHit> Query(IEnumerable<GlobalHit> all, string? query, int max = 40)
    {
        string q = (query ?? "").Trim();

        if (q.Length == 0)
        {
            /* 空查询：给最近最常用的入口 —— 页面 + 功能。 */
            return all.Take(max).ToList();
        }

        var terms = q.Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(GlobalHit Hit, int Score)>();

        foreach (var hit in all)
        {
            int score = Score(hit, terms);
            if (score > 0) scored.Add((hit, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Hit.Title.Length)
            .ThenBy(x => x.Hit.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.Hit)
            .ToList();
    }

    private static int Score(GlobalHit hit, string[] terms)
    {
        int total = 0;

        foreach (string term in terms)
        {
            int one = ScoreOne(hit, term);
            if (one == 0) return 0;   /* 有一个词没命中就整条不算 */
            total += one;
        }

        return total;
    }

    private static int ScoreOne(GlobalHit hit, string term)
    {
        const StringComparison C = StringComparison.CurrentCultureIgnoreCase;

        if (hit.Title.Equals(term, C)) return 160;
        if (hit.Title.StartsWith(term, C)) return 120;
        if (hit.Title.Contains(term, C)) return 80;
        if (hit.Keywords != null && hit.Keywords.Contains(term, C)) return 55;
        if (hit.Detail.Contains(term, C)) return 35;

        return 0;
    }

    private static string Shorten(string? text, int max = 48)
    {
        string s = (text ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
