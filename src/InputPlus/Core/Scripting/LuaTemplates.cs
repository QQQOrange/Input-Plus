using System;
using System.Collections.Generic;
using System.Linq;

namespace InputPlus.Core.Scripting;

/// <summary>
/// 一条内置脚本模板：分类 / 名字 / 一句话说明 / 直接能跑的代码，可选要不要参数。
///
/// 模板本身不属于任何用户配置 —— 它活在代码里，动作目录里以 <c>luaTpl:&lt;id&gt;</c> 出现，
/// 装好软件就能在按键、手势、手势菜单里直接选（不用先"新建脚本→写代码"）。
/// 想改就在 Lua 脚本管理窗里「从模板新建」复制一份出来改，模板那份永远保持出厂。
/// </summary>
/// <param name="Id">稳定 id：只加不改，改了就断老配置里的绑定。</param>
/// <param name="Category">分类（菜单里按它分组的那个标题）。</param>
/// <param name="Name">显示名（中文原文，运行期走 Loc.T）。</param>
/// <param name="Description">一句话说明，也是动作说明与脚本注释。</param>
/// <param name="Code">Lua 代码 —— 只用 <c>ip.*</c> 通道，碰不到文件系统和 os/io。</param>
/// <param name="NeedsArg">这条模板要不要动作参数（<c>ip.arg</c>）。</param>
/// <param name="ArgHint">参数提示；不需要参数时留空。</param>
public sealed record LuaTemplate(string Id, string Category, string Name, string Description, string Code,
                                bool NeedsArg = false, string ArgHint = "");

/// <summary>
/// 出厂自带的一百多条 Lua 脚本。
///
/// 为什么这么多：Lua 动作分类以前只有「打开脚本管理」一条 —— 用户点开发现是空编辑器，
/// 等于没有这个功能。这里按用途分成 10 类：窗口、文本、打开、文件夹、搜索、时间、键鼠、网络、开发者、示例，
/// 既有每天用得上的小工具，也有"照着改"的示例；要改就复制出来改，不会污染模板。
/// </summary>
public static class LuaTemplates
{

    /// <summary>
    /// 与原生动作重复的模板（v1.6.1 审计出来的 49 条）。
    ///
    /// 判据：**不写脚本、只用软件自己的动作就能做到同一件事** ——
    /// 其中 14 条跟专用动作 1:1 等价（`topmost` ↔ `winPin`、`win-close` ↔ `winClose`…），
    /// 另外 35 条是「原生动作 + 一个固定参数」的启动器预设（`open-calc` = `runProgram` + `calc.exe`、
    /// `dir-*` = `openFolder` + 那个路径、`key-copy` = `customKey` + Ctrl+C…）。
    ///
    /// 处理方式是**从菜单与动作目录里藏起来，不删代码**：id 是稳定契约
    /// （LuaTemplates.cs:11 那句「只加不改」），删掉会让老配置里的 `luaTpl:&lt;id&gt;` 直接失效。
    /// 藏起来之后 `Find(id)` 照样找得到，已经绑在键 / 手势上的照旧能跑。
    /// </summary>
    public static IReadOnlySet<string> Hidden { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "topmost", "win-close", "win-min", "win-max",
        "win-center", "key-esc", "key-alttab", "key-win-d",
        "key-lock", "key-screenshot", "open-calc", "open-taskmgr",
        "text-paste", "mouse-wheel", "open-notepad", "open-explorer-home",
        "open-downloads", "open-cmd", "open-powershell", "open-settings",
        "open-recycle", "open-regedit", "open-control", "open-github",
        "open-vscode", "open-terminal", "dir-documents", "dir-desktop",
        "dir-fonts", "dir-recent", "dir-startup", "dir-appdata",
        "dir-localappdata", "dir-programfiles", "dir-hosts", "dir-onedrive",
        "net-flushdns", "net-ping", "net-ipconfig", "net-settings",
        "net-proxy", "net-wifi", "net-inputplus", "net-weather",
        "key-copy", "key-paste", "key-undo", "key-save",
        "key-close-tab",
    };

    /// <summary>这条模板是不是「与原生动作重复」被藏起来了。</summary>
    public static bool IsHidden(string? id) => !string.IsNullOrEmpty(id) && Hidden.Contains(id);

    /// <summary>菜单与动作目录用：过滤掉重复项之后的模板（顺序不变）。</summary>
    /// <remarks>
    /// 写成属性而不是静态字段：字段初始化按声明顺序跑，这个位置在 <see cref="All"/> 前面，
    /// 当字段用会先拿到 null（编译能过、运行期炸）。调用点都是开菜单 / 建目录，
    /// 一次 Where 的开销可以忽略。
    /// </remarks>
    public static IEnumerable<LuaTemplate> Visible => All.Where(t => !IsHidden(t.Id));

    /// <summary>全部模板，顺序就是动作目录里的顺序（按分类成组）。</summary>
    public static IReadOnlyList<LuaTemplate> All { get; } = new List<LuaTemplate>
    {
        // ---------------- 窗口 ----------------
        new("title-copy", "窗口",
            "复制当前窗口标题",
            "把前台窗口的标题放进剪贴板 —— 贴到聊天框、工单里正好。",
            """
            local title = ip.win.title()
            if title == "" then
                ip.notify("取不到窗口标题")
                return false
            end
            ip.text.set(title)
            ip.notify("已复制：" .. title)
            """),
        new("topmost", "窗口",
            "当前窗口置顶开 / 关",
            "再触发一次就取消置顶。绑在手势上，随手把窗口钉在最前面。",
            """
            ip.win.topmost()
            ip.log("置顶已切换")
            """),
        new("win-close", "窗口",
            "关掉当前窗口",
            "跟点右上角的叉一样，只是可以绑到键或者手势上。",
            """
            ip.win.close()
            """),
        new("win-min", "窗口",
            "最小化当前窗口",
            "把前台窗口收进任务栏。",
            """
            ip.win.minimize()
            """),
        new("win-max", "窗口",
            "最大化 / 还原当前窗口",
            "已经最大化就还原，否则最大化。",
            """
            ip.win.maximize()
            """),
        new("win-center", "窗口",
            "把窗口挪到屏幕中间",
            "大屏上摆窗口最常用的一下。",
            """
            ip.win.center()
            """),
        new("win-center-topmost", "窗口",
            "居中并置顶",
            "先摆到中间，再钉在最前面 —— 看教程时把视频窗口钉住。",
            """
            ip.win.center()
            ip.win.topmost()
            ip.notify("居中并置顶")
            """),
        new("win-title-clean", "窗口",
            "标题去掉尾部「 - 程序名」再复制",
            "浏览器、记事本的标题后面都挂着程序名，复制前先削掉。",
            """
            local title = ip.win.title()
            if title == "" then
                ip.notify("没有前台窗口")
                return false
            end
            title = string.gsub(title, "%s+%-%s+[^-]*$", "")
            ip.text.set(title)
            ip.notify("已复制：" .. title)
            """),
        new("win-close-notify", "窗口",
            "关窗口并说一句关了什么",
            "关掉之前先把标题念一遍，手滑关错时至少知道关的是什么。",
            """
            local title = ip.win.title()
            ip.win.close()
            ip.notify("关掉了：" .. title)
            """),
        new("win-min-notify", "窗口",
            "最小化并说一句",
            "收起前台窗口，顺便提示是哪一个。",
            """
            local title = ip.win.title()
            ip.win.minimize()
            ip.notify("收起了：" .. title)
            """),
        new("win-title-length", "窗口",
            "数一数标题有几个字",
            "一秒钟验证「这段文案到底多长」的小工具。",
            """
            local title = ip.win.title()
            ip.notify("标题 " .. #title .. " 个字")
            """),
        new("win-close-guard", "窗口",
            "标题含「重要」就不关",
            "演示「先判断再动手」：拦一下，别把正在写的东西关掉。",
            """
            local title = ip.win.title()
            if string.find(title, "重要") then
                ip.notify("标题里有「重要」，这次不关")
                return false
            end
            ip.win.close()
            """),
        new("win-slug", "窗口",
            "标题转成文件名",
            "空格换横线、去掉路径里不能用的字符，复制出来直接能当文件名。",
            """
            local title = ip.win.title()
            title = string.gsub(title, "%s+", "-")
            title = string.gsub(title, "[\\/:*?\"<>|]", "")
            ip.text.set(title)
            ip.notify("文件名：" .. title)
            """),
        new("win-time-log", "窗口",
            "给当前窗口记一条时间日志",
            "「几点几分在哪个窗口」—— 记录自己一整天干了什么的第一步。",
            """
            ip.log(ip.now() .. "  " .. ip.win.title())
            """),
        new("win-info-notify", "窗口",
            "标题太长就截断再提示",
            "标题几十个字的时候，提示框里只显示前 40 个字。",
            """
            local title = ip.win.title()
            if #title > 40 then
                title = string.sub(title, 1, 40) .. "…"
            end
            ip.notify(title)
            """),
        new("win-max-center", "窗口",
            "最大化再居中（铺满工作区）",
            "有些程序最大化之后没居中，这一下把它摆正。",
            """
            ip.win.maximize()
            ip.win.center()
            """),

        // ---------------- 文本 ----------------
        new("upper", "文本",
            "剪贴板转大写",
            "读剪贴板、全部转成大写再写回去。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            ip.text.set(string.upper(text))
            ip.notify("已转大写")
            """),
        new("trim", "文本",
            "剪贴板去掉多余空白",
            "去掉首尾空白、把三行以上的空行压成一行 —— 从网页复制出来的文字常用。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local cleaned = string.gsub(text, "^%s+", "")
            cleaned = string.gsub(cleaned, "%s+$", "")
            cleaned = string.gsub(cleaned, "\n\n\n+", "\n\n")
            ip.text.set(cleaned)
            ip.notify("整理好了")
            """),
        new("text-lower", "文本",
            "剪贴板转小写",
            "和大写那条一对，处理邮箱、代码时省事。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            ip.text.set(string.lower(text))
            """),
        new("text-copy", "文本",
            "复制选中内容",
            "发 Ctrl+Insert —— 比 Ctrl+C 更不容易被程序拦掉。",
            """
            ip.text.copy()
            """),
        new("text-paste", "文本",
            "粘贴",
            "发 Shift+Insert，跟复制那条配成一对。",
            """
            ip.text.paste()
            """),
        new("text-upper-paste", "文本",
            "选中内容转大写直接粘贴",
            "复制 → 转大写 → 粘回去，一步到位。",
            """
            ip.text.copy()
            ip.sleep(120)
            local text = ip.text.get()
            if text == "" then
                ip.notify("没选中东西")
                return false
            end
            ip.text.set(string.upper(text))
            ip.text.paste()
            """),
        new("text-trim-paste", "文本",
            "选中内容去掉空白再粘贴",
            "从 PDF 里复制出来的文字常带一堆空格，粘回去之前先理顺。",
            """
            ip.text.copy()
            ip.sleep(120)
            local text = ip.text.get()
            text = string.gsub(text, "^%s+", "")
            text = string.gsub(text, "%s+$", "")
            ip.text.set(text)
            ip.text.paste()
            """),
        new("text-noblank", "文本",
            "删掉剪贴板里的空行",
            "只留真正有内容的行。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local out = {}
            for line in string.gmatch(text, "[^\n]+") do
                if string.match(line, "%S") then
                    table.insert(out, line)
                end
            end
            ip.text.set(table.concat(out, "\n"))
            ip.notify("去掉了空行")
            """),
        new("text-number", "文本",
            "每行加序号",
            "整理步骤、列清单的时候最省事。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local out = {}
            local n = 0
            for line in string.gmatch(text, "[^\n]+") do
                n = n + 1
                table.insert(out, n .. ". " .. line)
            end
            ip.text.set(table.concat(out, "\n"))
            """),
        new("text-md-list", "文本",
            "每行前面加「- 」",
            "一段文字两秒变 Markdown 列表。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local out = {}
            for line in string.gmatch(text, "[^\n]+") do
                table.insert(out, "- " .. line)
            end
            ip.text.set(table.concat(out, "\n"))
            """),
        new("text-quote", "文本",
            "每行前面加「> 」",
            "回复邮件、写引用块的时候用。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local out = {}
            for line in string.gmatch(text, "[^\n]+") do
                table.insert(out, "> " .. line)
            end
            ip.text.set(table.concat(out, "\n"))
            """),
        new("text-reverse", "文本",
            "把行序倒过来",
            "日志一般是新的在下面，倒过来就能先看最新的。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local out = {}
            for line in string.gmatch(text, "[^\n]+") do
                table.insert(out, 1, line)
            end
            ip.text.set(table.concat(out, "\n"))
            """),
        new("text-join", "文本",
            "多行合并成一行（逗号分隔）",
            "把清单压成一行，填进搜索框、聊天框正好。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local out = {}
            for line in string.gmatch(text, "[^\n]+") do
                table.insert(out, line)
            end
            ip.text.set(table.concat(out, ", "))
            """),
        new("text-stats", "文本",
            "数一数几个字、几行",
            "写文案、凑字数的时候随手量一下。",
            """
            local text = ip.text.get()
            local lines = 0
            for line in string.gmatch(text, "[^\n]+") do
                lines = lines + 1
            end
            ip.notify(#text .. " 个字符 · " .. lines .. " 行")
            """),
        new("text-strip-tags", "文本",
            "去掉剪贴板里的 HTML 标签",
            "从网页复制出来带着 <div>、<span>，一键剥掉只留文字。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            text = string.gsub(text, "<[^>]+>", "")
            ip.text.set(text)
            ip.notify("标签去掉了")
            """),
        new("text-sql-in", "文本",
            "每行包成 SQL 的 in 列表",
            "一行一个值，直接变成 'a', 'b', 'c', —— 写查询时省事。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local out = {}
            for line in string.gmatch(text, "[^\n]+") do
                table.insert(out, "'" .. line .. "',")
            end
            ip.text.set(table.concat(out, "\n"))
            """),
        new("text-csv-quote", "文本",
            "每行加双引号（CSV 用）",
            "导出成 CSV 时，每行套一层双引号，里面的引号自动转义。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local out = {}
            for line in string.gmatch(text, "[^\n]+") do
                local quoted = string.gsub(line, "\"", "\"\"")
                table.insert(out, "\"" .. quoted .. "\"")
            end
            ip.text.set(table.concat(out, "\n"))
            """),
        new("text-titlecase", "文本",
            "每个单词首字母大写",
            "英文标题的常规写法，处理变量名、标题时用。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            text = string.gsub(text, "(%a)([%w']*)", function(first, rest)
                return string.upper(first) .. string.lower(rest)
            end)
            ip.text.set(text)
            """),
        new("text-dedup", "文本",
            "删掉重复的行",
            "日志粘了两次、清单里有重样的，一键去重。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local seen = {}
            local out = {}
            for line in string.gmatch(text, "[^\n]+") do
                if not seen[line] then
                    seen[line] = true
                    table.insert(out, line)
                end
            end
            ip.text.set(table.concat(out, "\n"))
            ip.notify("剩 " .. #out .. " 行")
            """),
        new("text-sort", "文本",
            "按字母排序每一行",
            "整理清单、排别名的时候用。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local out = {}
            for line in string.gmatch(text, "[^\n]+") do
                table.insert(out, line)
            end
            table.sort(out)
            ip.text.set(table.concat(out, "\n"))
            """),
        new("text-halfwidth", "文本",
            "中文标点换成英文标点",
            "写代码、填表单时最常撞的一堵墙：全角逗号、冒号、分号。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            text = string.gsub(text, "　", " ")
            text = string.gsub(text, "，", ",")
            text = string.gsub(text, "：", ":")
            text = string.gsub(text, "；", ";")
            text = string.gsub(text, "（", "(")
            text = string.gsub(text, "）", ")")
            ip.text.set(text)
            ip.notify("标点换好了")
            """),
        new("text-semicolon", "文本",
            "每行末尾加分号",
            "把一列值改成语句、把清单改成 CSS —— 每行补个分号。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            local out = {}
            for line in string.gmatch(text, "[^\n]+") do
                table.insert(out, line .. ";")
            end
            ip.text.set(table.concat(out, "\n"))
            """),
        new("text-path-open", "文本",
            "打开剪贴板里的路径",
            "剪贴板里是文件就打开它，是文件夹就进文件夹。",
            """
            local path = ip.text.get()
            if path == "" then
                ip.notify("剪贴板里没有路径")
                return false
            end
            if string.match(path, "%.[%w]+$") then
                ip.openFile(path)
            else
                ip.openFolder(path)
            end
            """),
        new("text-url-open", "文本",
            "剪贴板里是网址就用浏览器打开",
            "复制一条链接，按一下就打开，不用再粘进地址栏。",
            """
            local url = ip.text.get()
            if string.match(url, "^https?://") then
                ip.open(url)
            else
                ip.notify("剪贴板里不是网址")
            end
            """),
        new("text-time-prefix", "文本",
            "剪贴板前面加上当前时间",
            "记笔记、写工单时先把时间戳钉在最前面。",
            """
            ip.text.set(ip.now() .. " " .. ip.text.get())
            """),
        new("text-json-escape", "文本",
            "把剪贴板转义成 JSON 字符串",
            "引号、反斜杠、换行一次性转好，直接能塞进 JSON 字段里。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            text = string.gsub(text, "\\", "\\\\")
            text = string.gsub(text, "\"", "\\\"")
            text = string.gsub(text, "\n", "\\n")
            ip.text.set(text)
            """),

        // ---------------- 打开 ----------------
        new("open-arg", "打开",
            "打开参数里的路径或网址",
            "动作参数里填一个网址或文件路径，触发就打开 —— 一条脚本能当好几个快捷方式用。",
            """
            local target = ip.arg or ""
            if target == "" then
                ip.notify("先在动作参数里填要打开的东西")
                return false
            end
            if string.match(target, "^https?://") then
                ip.open(target)
            else
                ip.openFile(target)
            end
            ip.log("已打开：" .. target)
            """,
            true, "网址（https://…）或完整路径（D:/…）"),
        new("notepad-paste", "打开",
            "记事本 + 粘贴剪贴板",
            "开一个记事本，把剪贴板里的东西贴进去 —— 临时看一眼复制的是什么。",
            """
            ip.run("notepad.exe")
            ip.sleep(400)
            ip.text.paste()
            """),
        new("open-notepad", "打开",
            "打开记事本",
            "最常用的那个白板。",
            """
            ip.run("notepad.exe")
            """),
        new("open-calc", "打开",
            "打开计算器",
            "算个数不用去翻开始菜单。",
            """
            ip.run("calc.exe")
            """),
        new("open-taskmgr", "打开",
            "打开任务管理器",
            "卡住了、想看看谁在吃 CPU 的时候。",
            """
            ip.run("taskmgr.exe")
            """),
        new("open-explorer-home", "打开",
            "打开用户目录",
            "%USERPROFILE% 那一层，桌面、文档、下载都在它下面。",
            """
            ip.openFolder("%USERPROFILE%")
            """),
        new("open-downloads", "打开",
            "打开下载文件夹",
            "刚下载完的东西在哪，一击即到。",
            """
            ip.openFolder("shell:Downloads")
            """),
        new("open-cmd", "打开",
            "打开命令提示符",
            "在用户目录起一个 cmd。",
            """
            ip.run("cmd.exe")
            """),
        new("open-powershell", "打开",
            "打开 PowerShell",
            "比 cmd 顺手一点的那个。",
            """
            ip.run("powershell.exe")
            """),
        new("open-settings", "打开",
            "打开 Windows 设置",
            "ms-settings: 的首页。",
            """
            ip.open("ms-settings:")
            """),
        new("open-recycle", "打开",
            "打开回收站",
            "找刚删掉的那个文件。",
            """
            ip.openFolder("shell:RecycleBinFolder")
            """),
        new("open-regedit", "打开",
            "打开注册表编辑器",
            "改注册表之前记得先导出备份。",
            """
            ip.run("regedit.exe")
            """),
        new("open-control", "打开",
            "打开控制面板",
            "老一点的设置项还在控制面板里。",
            """
            ip.run("control.exe")
            """),
        new("open-github", "打开",
            "打开 GitHub",
            "首页，随手开工。",
            """
            ip.open("https://github.com")
            """),
        new("open-vscode", "打开",
            "打开 VS Code",
            "要求 PATH 里有 code 命令。",
            """
            ip.run("code")
            """),
        new("open-terminal", "打开",
            "打开 Windows 终端",
            "要求装过 Windows Terminal（wt.exe）。",
            """
            ip.run("wt.exe")
            """),

        // ---------------- 文件夹 ----------------
        new("dir-documents", "文件夹",
            "打开「文档」",
            "shell:Personal —— 跟资源管理器左边那一栏是同一个地方。",
            """
            ip.openFolder("shell:Personal")
            """),
        new("dir-desktop", "文件夹",
            "打开桌面",
            "桌面文件太多、被窗口挡住了的时候。",
            """
            ip.openFolder("shell:Desktop")
            """),
        new("dir-fonts", "文件夹",
            "打开字体文件夹",
            "装字体、删字体都在这儿。",
            """
            ip.openFolder("shell:Fonts")
            """),
        new("dir-recent", "文件夹",
            "打开「最近使用」",
            "忘了刚才那个文件存哪了，从这儿翻。",
            """
            ip.openFolder("shell:Recent")
            """),
        new("dir-startup", "文件夹",
            "打开开机启动文件夹",
            "想让某个程序开机就跑，把快捷方式丢进来。",
            """
            ip.openFolder("shell:Startup")
            """),
        new("dir-appdata", "文件夹",
            "打开 AppData\\Roaming",
            "大部分软件的配置都藏在这一层。",
            """
            ip.openFolder("shell:AppData")
            """),
        new("dir-localappdata", "文件夹",
            "打开 AppData\\Local",
            "缓存、日志、本地数据库一般在这儿。",
            """
            ip.openFolder("shell:Local AppData")
            """),
        new("dir-programfiles", "文件夹",
            "打开 Program Files",
            "装好的程序都在这儿。",
            """
            ip.openFolder("shell:ProgramFiles")
            """),
        new("dir-hosts", "文件夹",
            "打开 hosts 文件",
            "改域名映射用；保存时需要管理员权限。",
            """
            ip.openFile("C:/Windows/System32/drivers/etc/hosts")
            """),
        new("dir-onedrive", "文件夹",
            "打开 OneDrive 目录",
            "没装 OneDrive 的话会提示路径不存在，无害。",
            """
            ip.openFolder("%USERPROFILE%/OneDrive")
            """),

        // ---------------- 搜索 ----------------
        new("search", "搜索",
            "搜一搜剪贴板里的文字",
            "把剪贴板当关键词，用默认浏览器在必应上搜一下。",
            """
            local query = ip.text.get()
            if query == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            query = string.gsub(query, "%s+", "+")
            ip.open("https://www.bing.com/search?q=" .. query)
            """),
        new("search-google", "搜索",
            "用 Google 搜剪贴板",
            "同样的关键词，换一个搜索引擎。",
            """
            local query = string.gsub(ip.text.get(), "%s+", "+")
            if query == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            ip.open("https://www.google.com/search?q=" .. query)
            """),
        new("search-github", "搜索",
            "在 GitHub 上搜剪贴板",
            "找开源实现、找报错原文。",
            """
            local query = string.gsub(ip.text.get(), "%s+", "+")
            if query == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            ip.open("https://github.com/search?q=" .. query)
            """),
        new("search-wiki", "搜索",
            "在维基百科上查剪贴板",
            "查名词、查人物背景。",
            """
            local query = string.gsub(ip.text.get(), "%s+", "+")
            if query == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            ip.open("https://zh.wikipedia.org/w/index.php?search=" .. query)
            """),
        new("search-mdn", "搜索",
            "在 MDN 上查这个 API",
            "前端的东西，MDN 比搜索引擎准。",
            """
            local query = string.gsub(ip.text.get(), "%s+", "+")
            if query == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            ip.open("https://developer.mozilla.org/zh-CN/search?q=" .. query)
            """),
        new("search-zhihu", "搜索",
            "在知乎上搜剪贴板",
            "想看看别人怎么评价 / 怎么解决。",
            """
            local query = string.gsub(ip.text.get(), "%s+", "+")
            if query == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            ip.open("https://www.zhihu.com/search?type=content&q=" .. query)
            """),
        new("search-bilibili", "搜索",
            "在 B 站上搜剪贴板",
            "找教程视频的时候用。",
            """
            local query = string.gsub(ip.text.get(), "%s+", "+")
            if query == "" then
                ip.notify("剪贴板里没有文字")
                return false
            end
            ip.open("https://search.bilibili.com/all?keyword=" .. query)
            """),

        // ---------------- 时间 ----------------
        new("now-note", "时间",
            "新建记事本，写下当前时间",
            "开记事本并把当前时间打上去，用来随手记一笔。",
            """
            ip.run("notepad.exe")
            ip.sleep(400)
            ip.text.set(ip.now())
            ip.text.paste()
            """),
        new("time-copy", "时间",
            "复制当前时间",
            "「2026-11-20 16:10:00」这种格式，贴哪都认。",
            """
            ip.text.set(ip.now())
            """),
        new("time-date", "时间",
            "复制今天的日期",
            "只要年月日，不要时分秒。",
            """
            ip.text.set(string.sub(ip.now(), 1, 10))
            """),
        new("time-unix", "时间",
            "复制 Unix 时间戳",
            "写日志、算时间差的时候用。",
            """
            ip.text.set(tostring(ip.time()))
            """),
        new("time-notify", "时间",
            "报一下现在几点",
            "不抬眼也知道时间。",
            """
            ip.notify("现在是 " .. string.sub(ip.now(), 12))
            """),
        new("time-rest", "时间",
            "提醒自己起来走两步",
            "等一秒半再弹提示，不会立刻打断手上的动作。",
            """
            ip.sleep(1500)
            ip.notify("起来走两步，看看远处")
            """),
        new("time-countdown", "时间",
            "三秒后提醒一句",
            "演示 ip.sleep：先提示一声，等三秒再提示一声。",
            """
            ip.notify("三秒后提醒")
            ip.sleep(3000)
            ip.notify("到点了")
            """),
        new("time-stamp-log", "时间",
            "把「时间 + 窗口」记进日志",
            "想复盘今天在哪些窗口里待过，就跑这条。",
            """
            ip.log(ip.now() .. " · " .. ip.win.title())
            """),
        new("time-greet", "时间",
            "按时间打招呼",
            "上午 / 下午 / 晚上，演示 if-elseif-else。",
            """
            local hour = tonumber(string.sub(ip.now(), 12, 13))
            if hour < 12 then
                ip.notify("早上好")
            elseif hour < 18 then
                ip.notify("下午好")
            else
                ip.notify("晚上好")
            end
            """),

        // ---------------- 键鼠 ----------------
        new("key-copy", "键鼠",
            "按一下 Ctrl+C",
            "有些窗口拦 Ctrl+C，用脚本发的这一下反而过得去。",
            """
            ip.key("ctrl+c")
            """),
        new("key-paste", "键鼠",
            "按一下 Ctrl+V",
            "跟上面那条配对。",
            """
            ip.key("ctrl+v")
            """),
        new("key-undo", "键鼠",
            "按一下 Ctrl+Z",
            "手滑删错了，一击撤销。",
            """
            ip.key("ctrl+z")
            """),
        new("key-save", "键鼠",
            "按一下 Ctrl+S",
            "随手存一下，比想起来再存靠谱。",
            """
            ip.key("ctrl+s")
            """),
        new("key-close-tab", "键鼠",
            "关掉当前标签页",
            "Ctrl+W，浏览器、编辑器都认。",
            """
            ip.key("ctrl+w")
            """),
        new("key-alttab", "键鼠",
            "切到下一个窗口",
            "Alt+Tab。",
            """
            ip.key("alt+tab")
            """),
        new("key-win-d", "键鼠",
            "显示桌面",
            "Win+D，一键把窗口全收起来。",
            """
            ip.key("win+d")
            """),
        new("key-esc", "键鼠",
            "按一下 Esc",
            "退全屏、关弹窗。",
            """
            ip.key("esc")
            """),
        new("key-lock", "键鼠",
            "锁屏",
            "Win+L，走开之前按一下。",
            """
            ip.key("win+l")
            """),
        new("key-screenshot", "键鼠",
            "截图（框选）",
            "Win+Shift+S，系统自带的截图条。",
            """
            ip.key("win+shift+s")
            """),
        new("mouse-wheel", "键鼠",
            "向下滚 5 格",
            "负数向下 —— 和 Windows 的滚动方向一致。",
            """
            ip.wheel(-5)
            """),
        new("mouse-double", "键鼠",
            "鼠标双击",
            "两次左键，中间隔 80 毫秒。",
            """
            ip.click("left")
            ip.sleep(80)
            ip.click("left")
            """),

        // ---------------- 网络 ----------------
        new("net-flushdns", "网络",
            "刷新 DNS 缓存",
            "改了 hosts、换了 DNS 之后跑一下，立刻就生效。",
            """
            ip.run("cmd /c ipconfig /flushdns")
            """),
        new("net-ping", "网络",
            "ping 一下本机",
            "一条命令看网络栈还活着没有。",
            """
            ip.run("cmd /c ping -n 2 127.0.0.1")
            """),
        new("net-ipconfig", "网络",
            "看本机 IP 配置",
            "查 IP、网关、DNS。",
            """
            ip.run("cmd /c ipconfig")
            """),
        new("net-settings", "网络",
            "打开网络设置",
            "ms-settings:network 的首页。",
            """
            ip.open("ms-settings:network")
            """),
        new("net-proxy", "网络",
            "打开代理设置",
            "要改系统代理、填 PAC 脚本的时候。",
            """
            ip.open("ms-settings:network-proxy")
            """),
        new("net-wifi", "网络",
            "打开 Wi-Fi 设置",
            "换网络、看信号。",
            """
            ip.open("ms-settings:network-wifi")
            """),
        new("net-inputplus", "网络",
            "打开 Input Plus 仓库",
            "看看最新版本、提个 issue。",
            """
            ip.open("https://github.com/QQQOrange/Input-Plus")
            """),
        new("net-weather", "网络",
            "打开天气",
            "出门之前扫一眼。",
            """
            ip.open("https://weather.com")
            """),

        // ---------------- 开发者 ----------------
        new("dev-log-env", "开发者",
            "打印脚本名、参数和演示状态",
            "写脚本第一步：先确认 ip.script / ip.arg / ip.dryrun 都是什么。",
            """
            ip.log("脚本：" .. ip.script)
            ip.log("参数：" .. ip.arg)
            ip.log("dryrun：" .. tostring(ip.dryrun))
            """),
        new("dev-dump-api", "开发者",
            "列出所有 ip.* 函数",
            "不知道有哪些 API 可调的时候，先把它们打出来看一遍。",
            """
            local names = {}
            for key, value in pairs(ip) do
                if type(value) == "function" then
                    table.insert(names, key)
                end
            end
            table.sort(names)
            ip.log(table.concat(names, ", "))
            """),
        new("dev-arg-required", "开发者",
            "要求必须填参数的脚本",
            "演示「参数没填就提示并退出」，动作参数里随便写点东西试试。",
            """
            if ip.arg == "" then
                ip.notify("这条脚本要参数，去动作参数里填")
                return false
            end
            ip.notify("收到：" .. ip.arg)
            """,
            true, "随便写点什么"),
        new("dev-pcall", "开发者",
            "用 pcall 兜住可能出错的调用",
            "出错不炸整段脚本，只在日志里留一行。",
            """
            local ok, result = pcall(function()
                return string.rep("x", 3)
            end)
            if ok then
                ip.log("成功：" .. result)
            else
                ip.log("失败：" .. tostring(result))
            end
            """),
        new("dev-format", "开发者",
            "用 string.format 拼一行字",
            "比 .. 拼更清楚，数字还能控制位数。",
            """
            local text = string.format("脚本 %s 的名字有 %d 个字", ip.script, #ip.script)
            ip.log(text)
            """),
        new("dev-table", "开发者",
            "遍历一张表",
            "pairs 走一遍、逐个打日志 —— 处理结构化数据的起手式。",
            """
            local row = { name = ip.script, arg = ip.arg }
            for key, value in pairs(row) do
                ip.log(key .. " = " .. tostring(value))
            end
            """),
        new("dev-function", "开发者",
            "定义函数并调用",
            "把参数转大写再写回剪贴板，演示 local function。",
            """
            local function shout(text)
                return string.upper(text)
            end
            ip.text.set(shout(ip.arg))
            """,
            true, "要转大写的文字"),
        new("dev-dryrun", "开发者",
            "判断是不是演示模式",
            "演示时只记日志、真跑时才动手 —— 写危险脚本的好习惯。",
            """
            if ip.dryrun then
                ip.log("演示模式：只记录，不动手")
                return
            end
            ip.notify("真的执行了")
            """),

        // ---------------- 示例 ----------------
        new("demo-hello", "示例",
            "你好，世界",
            "最短的一条：弹一句提示。",
            """
            ip.notify("你好，世界")
            """),
        new("demo-if", "示例",
            "if / else 示例",
            "看剪贴板空不空，分别说一句。",
            """
            local text = ip.text.get()
            if text == "" then
                ip.notify("剪贴板是空的")
            else
                ip.notify("剪贴板有 " .. #text .. " 个字")
            end
            """),
        new("demo-loop", "示例",
            "for 循环示例",
            "重复三遍，每遍打一行日志。",
            """
            for i = 1, 3 do
                ip.log("第 " .. i .. " 次")
            end
            """),
        new("demo-return", "示例",
            "提前返回示例",
            "条件不满足就 return，后面的代码不再执行。",
            """
            local text = ip.text.get()
            if text == "" then
                return false
            end
            ip.notify("继续往下做")
            """),
        new("demo-concat", "示例",
            "table.concat 示例",
            "用分隔符把一组值接成字符串。",
            """
            local parts = { "a", "b", "c" }
            ip.log(table.concat(parts, "-"))
            """),
        new("demo-string", "示例",
            "string 库示例",
            "转大写、量长度，一行看明白。",
            """
            local text = "Input Plus"
            ip.log(string.upper(text) .. " / " .. #text)
            """),
    };

    /// <summary>分类顺序（「从模板新建」菜单按这个排）。</summary>
    public static IReadOnlyList<string> Categories { get; } = new List<string>
    {
        "窗口",
        "文本",
        "打开",
        "文件夹",
        "搜索",
        "时间",
        "键鼠",
        "网络",
        "开发者",
        "示例",
    };

    /// <summary>按 id 找一条模板；找不到返回 null。</summary>
    public static LuaTemplate? Find(string id)
    {
        foreach (var tpl in All)
        {
            if (tpl.Id == id) return tpl;
        }
        return null;
    }
}
