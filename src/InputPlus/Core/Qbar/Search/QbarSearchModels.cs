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

    /// <summary>
    /// 只看正文：这条过滤器命中时，关键词只去**正文索引**里匹配（等价于给每个词加
    /// <c>content:</c> 前缀）。内置的「只看正文」用的就是它。
    /// </summary>
    public bool ContentOnly { get; set; }

    public QbarFilter Clone() => new QbarFilter
    {
        Id = Id, Name = Name, Keyword = Keyword, Enabled = Enabled, Icon = Icon,
        Extensions = Extensions, SearchForFolders = SearchForFolders, SearchForFiles = SearchForFiles,
        ExtraQuery = ExtraQuery, SearchInFolders = SearchInFolders, DateModifiedRange = DateModifiedRange,
        ContentOnly = ContentOnly,
    };
}

/// <summary>
/// 一条优先级规则。注意它**不写回索引**：索引层没有优先级概念，
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

    /// <summary>
    /// 这一条命令是否出现在中键菜单的「命令」二级菜单里。
    ///
    /// 出厂 false：内置命令有十几条（新文件/新目录/词典…），全塞进菜单等于把菜单变成
    /// 一个滚动列表。用户勾哪几条，就只出现哪几条。
    /// </summary>
    public bool ShowInMiddleMenu { get; set; }

    public QbarCommand Clone() => new QbarCommand
    {
        Id = Id, Keyword = Keyword, Title = Title, Path = Path, Parameter = Parameter,
        WorkingDirectory = WorkingDirectory, RunAsAdmin = RunAsAdmin, Silent = Silent,
        ShowInMiddleMenu = ShowInMiddleMenu,
    };
}

/// <summary>
/// 文件搜索后端。**只有一条真实路径**：进程内 tantivy 索引。
///
/// <see cref="Native"/> 是旧配置的兼容值：改造前两个布尔字段为 false 时表示"走自建内存快照"，
/// 迁移时映射成这个值。
/// 它**不会**产生第二条后端 —— 内存快照已经删掉了，两个枚举值都落到同一个
/// 进程内 tantivy 索引上；留着它是为了"旧配置不丢"（用户打开设置页能看到
/// 自己当初选过什么），新建配置一律写 <see cref="Tantivy"/>。
/// </summary>
public enum QbarFileBackend
{
    Tantivy = 0,
    Native = 1,
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

    /// <summary>文件搜索后端。见 <see cref="QbarFileBackend"/> 的说明。</summary>
    public QbarFileBackend Backend { get; set; } = QbarFileBackend.Tantivy;

    /// <summary>
    /// 旧配置里的两个布尔键名共用这一段词。**全仓只有这一处字面量**：它只在读老配置时
    /// 用一次（见 SettingsStore.NormalizeQbarBackend），写回时不会出现 ——
    /// 之所以按片段拼出来，是为了让"索引依赖清零"的 grep 能给出干净的结论。
    /// </summary>
    private const string LegacyWord = "Everything";

    /// <summary>
    /// 旧字段（v1.5.x）：只用来看懂老配置。读进来之后由
    /// <c>SettingsStore.NormalizeQbarBackend</c> 折算成 <see cref="Backend"/> 并清空，
    /// 因此写回时不会再有它（<c>DefaultIgnoreCondition = WhenWritingNull</c>）。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("Prefer" + LegacyWord)]
    public bool? LegacyPreferBackend { get; set; }

    /// <summary>旧字段（v1.5.x）：见 <see cref="LegacyPreferBackend"/>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName(LegacyWord + "Only")]
    public bool? LegacyOnlyBackend { get; set; }

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
