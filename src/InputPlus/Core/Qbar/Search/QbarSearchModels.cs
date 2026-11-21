using System;
using System.Collections.Generic;

namespace InputPlus.Core.Qbar.Search;

/// <summary>
/// 一条过滤器定义。字段名照搬用户需求文档 2.4 节，目的是让「内置过滤器」和
/// 「用户自定义过滤器」走完全一样的代码路径 —— 内置的那几条只是目录里的默认项，
/// 用户改扩展名表、改关键字、禁用都可以，唯独不能删除（删掉会让 <c>doc:</c>
/// 这种肌肉记忆失效，比不支持还难用）。
///
/// 为什么 <see cref="Extensions"/> 用分号串而不是 List：它最终要拼成
/// <c>ext:a;b;c</c> 交给查询层，也直接写进设置 JSON 给人看，分隔符一改
/// 序列化/反序列化就对不上了，所以从存储到使用都是同一个形状。
/// </summary>
public sealed class QbarFilter
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Keyword { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Icon { get; set; } = "";
    /// <summary>不带点、分号分隔的小写扩展名表，例如 <c>pdf;docx</c>。</summary>
    public string Extensions { get; set; } = "";
    public bool SearchForFolders { get; set; }
    public bool SearchForFiles { get; set; } = true;
    /// <summary>自动追加的搜索词（可以再写一套关键字语法，由翻译层递归处理一次）。</summary>
    public string ExtraQuery { get; set; } = "";
    /// <summary>限定目录，多个用 <c>;</c> 分隔，翻译成 <c>path:"..."</c>。</summary>
    public string SearchInFolders { get; set; } = "";
    /// <summary>日期范围，语法同 <c>date:</c>（today / week / 2026-07 / 2026-07-01..2026-07-31）。</summary>
    public string DateModifiedRange { get; set; } = "";

    public QbarFilter Clone() => new QbarFilter
    {
        Id = Id, Name = Name, Keyword = Keyword, Enabled = Enabled, Icon = Icon,
        Extensions = Extensions, SearchForFolders = SearchForFolders, SearchForFiles = SearchForFiles,
        ExtraQuery = ExtraQuery, SearchInFolders = SearchInFolders, DateModifiedRange = DateModifiedRange,
    };
}

/// <summary>
/// 一条优先级规则。注意它**不写回索引**：Everything 没有优先级概念，自建索引
/// 里同样不该有 —— 规则是查询结果上的一个纯函数，改一条规则立刻生效，
/// 不需要重建索引，也不会因为规则删多了把文件从索引里弄丢。
/// </summary>
public sealed class QbarPriorityRule
{
    public string Id { get; set; } = "";
    /// <summary>目录子树（含子目录）。空串表示"任意目录"，只在有正则时才有意义。</summary>
    public string FolderPath { get; set; } = "";
    /// <summary>文件名正则；空 = 目录内全部文件。</summary>
    public string NameRegex { get; set; } = "";
    /// <summary>high / normal / low / ignored / dontindex。</summary>
    public string Level { get; set; } = "low";
    public bool Enabled { get; set; } = true;
    public string Note { get; set; } = "";

    public QbarPriorityRule Clone() => new QbarPriorityRule
    {
        Id = Id, FolderPath = FolderPath, NameRegex = NameRegex, Level = Level, Enabled = Enabled, Note = Note,
    };
}

/// <summary>一条外部命令。<c>keyword 参数</c> 的首词命中它时，列表顶部出现命令行。</summary>
public sealed class QbarCommand
{
    public string Id { get; set; } = "";
    public string Keyword { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>可执行文件；可以只写裸程序名，交给 shell 的 PATH / App Paths 解析。</summary>
    public string Path { get; set; } = "";
    public string Parameter { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public bool RunAsAdmin { get; set; }
    public bool Silent { get; set; }

    public QbarCommand Clone() => new QbarCommand
    {
        Id = Id, Keyword = Keyword, Title = Title, Path = Path, Parameter = Parameter,
        WorkingDirectory = WorkingDirectory, RunAsAdmin = RunAsAdmin, Silent = Silent,
    };
}

/// <summary>
/// 语义层的可调参数。全部落在设置里，默认值按"普通机器上感觉不到延迟"来定：
/// 候选窗口 2000 条足够覆盖一次查询的相关结果，重排 2000 条在本机是微秒级，
/// 真跑不满再从设置页往上调（上限 20000）。
/// </summary>
public sealed class QbarSearchOptions
{
    /// <summary>交给查询层的候选条数。200–20000，超界自动夹紧。</summary>
    public int MaxCandidates { get; set; } = 2000;

    /// <summary>
    /// 优先用 Everything 取候选。关掉或 Everything 不可用时自动走自建索引快照，
    /// 两条路的过滤语义完全一致（见 QbarEverythingBackend 的本地复核）。
    /// </summary>
    public bool PreferEverything { get; set; } = true;

    /// <summary>
    /// 只用 Everything：Everything 拿不到候选时**不回退**到本地索引快照。
    /// 文件搜索的索引与检索都交给 Everything（用户要求"只用 Everything"），
    /// 本地快照只在自检 / --qbar-bench 的对照路径里显式开启（PreferEverything=false）。
    /// </summary>
    public bool EverythingOnly { get; set; } = true;

    /// <summary>候选不够时是否放大窗口做一次模糊匹配。</summary>
    public bool FuzzyWhenSparse { get; set; } = true;

    /// <summary>少于这个条数才算"候选稀疏"，才值得放大窗口。</summary>
    public int FuzzyThreshold { get; set; } = 12;

    /// <summary>拼音索引总开关。关掉后 <c>yh</c> 这类查询退回窗口内模糊。</summary>
    public bool PinyinEnabled { get; set; } = true;

    /// <summary>优先级规则总开关（对应验收用例 11/24 的"关闭规则包"）。</summary>
    public bool RulesEnabled { get; set; } = true;

    /// <summary>内置规则包是否生效；用户规则永远优先于内置规则。</summary>
    public bool BuiltinRulesEnabled { get; set; } = true;

    /// <summary>
    /// 内置规则里被用户单独关掉的那些（键是规则身份串，例如 <c>low:c:\windows</c>）。
    /// 为什么要逐条存：设置页照 Listary 的样子把默认包摊开给用户看，
    /// 每一行都能拨开关 —— 关掉一行必须是"这一条不生效"，而不是把整包关掉。
    /// </summary>
    public List<string> DisabledBuiltins { get; set; } = new List<string>();

    /// <summary>
    /// 系统目录/临时文件是否直接不出现（对应设置页「默认不展示系统及隐藏文件的搜索结果」）。
    /// 默认关：默认行为必须和改造前一致，隐藏是用户显式打开才算数。
    /// 打开后 Low 档结果在 postFilter 阶段就被剔除，底部会写明剔了多少条。
    /// </summary>
    public bool HideLowResults { get; set; }

    /// <summary>历史事件（最近/常用）。</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>分面侧栏。</summary>
    public bool FacetsEnabled { get; set; } = true;

    /// <summary>查询调试面板：显示每一层中间结果与耗时。</summary>
    public bool DebugPanel { get; set; }

    /// <summary>输入防抖毫秒数。40–60 是用户需求给的区间，超界夹紧。</summary>
    public int DebounceMs { get; set; } = 45;

    /// <summary>单个查询最长等待（毫秒）。到点就返回"部分结果 + 提示"，不无限转圈。</summary>
    public int QueryTimeoutMs { get; set; } = 1200;

    /// <summary>子序列模糊匹配的最短输入长度。<c>rp</c> 这种两字符查全盘只会捞垃圾。</summary>
    public int MinFuzzyLength { get; set; } = 3;
}
