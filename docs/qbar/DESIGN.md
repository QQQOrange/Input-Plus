# Qbar 文件搜索：语义层 + 交互层 设计

> 适用代码：src/InputPlus/Core/Qbar/Search/*、src/InputPlus/Core/Qbar/NativeSearchIndex.cs、
> src/InputPlus/Core/Qbar/{QbarService,QbarProviders}.cs、src/InputPlus/UI/Windows/QbarWindow.*
> 目标：把「输入一句话就搜文件名」升级成 Listary 那种「关键字 + 谓词 + 优先级 + 命令 + 分面 + 历史」。

## 0. 现状勘察（改造前）

改造要求里写的是「Everything 已内嵌」，实际仓库不是这样 —— **Everything 集成已经被移除**：

| 项目 | 实际情况 | 证据 |
| --- | --- | --- |
| 搜索引擎 | 自建索引（本进程扫描目录 → 快照落盘 → 查询时字节级匹配） | src/InputPlus/Core/Qbar/NativeSearchIndex.cs:17 注释 |
| Everything SDK / IPC | 不存在。没有 Everything64.dll、没有 Everything_SetSearchW、没有 es.exe 调用 | 全仓 grep 为空 |
| 查询调用点 | QbarProviders.MatchFiles → NativeSearchIndex.Search | QbarProviders.cs:176 |
| 排序 / 分页 | 索引内按整数分排序（Score 降序→路径长度→序数比较），limit 上限 4096，无 Offset | NativeSearchIndex.cs SortAndTrim |
| 请求字段 | 文件路径、是否目录、分值；**快照原本不存大小与修改时间** | SearchCore / ScoreFile |
| UI | ListBox（默认 VirtualizingStackPanel），每行 = 图标 + 名字 + 副标题 + 徽标；Enter 执行、Tab 补全、右键菜单 | QbarWindow.xaml:183 |
| 配置 | %DataDir%\settings.json（AES），QbarOptions 一节；设置页 QbarSettingsView | AppSettings.cs:1151 |
| 中文输入法 | 有 IME 状态切换，但**没有 composition 判断**，上屏过程中就会查询 | QbarWindow.xaml.cs OnInputChanged |
| 测试 | SelfTest（--selftest，SelfTest.NativeIndex.cs 有夹具树）+ 新增 --qbar-bench / --qbar-query | CliFlags.cs |

**这条勘察后来被用户要求推翻**（2026-11-21）：任务的前提是"用 Everything 做本地检索"，
所以按 https://www.voidtools.com/support/everything/sdk/ 把 SDK 接了回来，形成**双后端**：

| 后端 | 何时使用 | 实现 |
| --- | --- | --- |
| Everything（首选） | `Everything64.dll` 能加载 + 客户端在跑 + 索引库已加载 + `PreferEverything` 未关 | `EverythingApi`（P/Invoke）→ `QbarEverythingBackend`（重建查询串 + 本地复核） |
| 自建索引（兜底） | 上面任一条不满足（没装、没运行、索引在建、用户关掉） | `NativeSearchIndex.SearchSpec` |

翻译层不变：仍然产出 `QbarSearchSpec`（结构化查询）。`EverythingText` 从"只作诊断"升级为
**Everything 后端的真实查询串**（见 §6）；自建索引后端仍按 Spec 求值。两条路得到的结果集
必须一致 —— 靠 `SpecFilter` 逐条复核保证（§6.3）。

## 1. 分层流水线

```
用户输入
  → QbarQueryTranslator.Normalize      全角→半角、折叠空白、去首尾
  → QbarQueryTranslator.Tokenize       token{Text,Kind(term|phrase|predicate),Negated,Quoted}
  → resolveKeyword                     首词命中命令表 → 命令行；命中过滤器表 → 展开 ext 列表 + 附加约束
  → translate                          每个 token → QbarSearchSpec 字段（并生成可读的 Everything 风格串）
  → QbarSearchEngine.Run               窗口取候选（MaxCandidates）
  → NativeSearchIndex.SearchSpec       字节级遍历索引快照，谓词过滤 + 原生打分
  → postFilter                         QbarPriorityResolver：DoNotIndex 剔除、Ignored 记数后剔除
  → rerank                             档位加成（High +120 / Low -120）+ 原生分 + 名字长度/深度
  → TryEnhance                         命中过少时补拼音索引 / 子序列模糊，合并去重
  → present                            QbarWindow 虚拟化列表 + TitleSegments 高亮 + 分面 + 调试面板
```

分层理由：每一层都能单独打印中间结果（查询调试面板逐行显示 Stage），出问题时能指出
是「解析错了」还是「索引里就没有」，而不是盯着最终结果猜。

## 2. 语法规格（实现即文档）

| 输入写法 | 解析结果（QbarSearchSpec） | 说明 |
| --- | --- | --- |
| `doc:` `pic:` `video:` `audio:` `file:` `folder:` | Extensions 或 FilesOnly/FoldersOnly | 内置过滤器，扩展名表见 QbarFilterCatalog（doc 49 项 / pic 16 项 / video / audio） |
| `ext:pdf;docx` | Extensions = {pdf,docx} | 分号 = OR |
| `-ext:tmp` `-doc: 报表` | NegExtensions / 否定谓词 | 前缀 `-` = NOT |
| `size:>1gb` `size:10mb..1gb` `size:<10mb` | MinSize / MaxSize（字节） | 1024 进制，kb/mb/gb/tb |
| `date:today|week|month|year` `date:2026-07` `date:2026-07-01..2026-07-31` | MinDay / MaxDay（自 2000-01-01 的天数） | 周 = 周一起；`yesterday`/`lastweek`/`lastmonth`/`lastyear` 同义 |
| `"年度 总结"` | Phrases | 空格不拆词 |
| `wfn:*.pdf` / `glob:` | Glob | 整名通配 |
| `regex:^a.*\.txt$` | RegexText | 只对这一条编译正则，不开全局正则 |
| `path:D:\work` / `in:` | PathPins | 目录前缀 |
| `content:关键字` | ContentRequested（标注不支持） | 索引不含正文，调试面板标黄 |
| 过滤器 `doc: 报表` | Extensions + Terms={报表} | 过滤器与关键词叠加 |
| 其他任意词 | Terms | 每个词必须出现在名字或所在目录里（与旧行为一致） |

**转义**：当作普通关键词的 `" ! | : ( ) * ? $ \` 会被加引号（NeedsQuoting/QuoteForEverything），
所以输入 `(` 或 `a|b` 不会把查询拆坏。解析不了的谓词（如 `foo:bar`）**降级成普通词**并在
调试面板标黄，不整条丢弃。

## 3. 索引调用约定

- 入口：`NativeSearchIndex.SearchSpec(QbarSearchSpec, limit, timeoutMs)`（纯关键词时直接转 SearchCore，
  保证旧行为一字不差）；测试入口 `SearchSpecInForTest(root, spec, limit, timeoutMs)`。
- 快照 v2 新增 `NameSizeKb`（uint，KB 饱和）与 `NameDay`（ushort，天；65535 = 未知）：
  大小/时间谓词不必回盘 stat。SchemaVersion 升到 2，旧快照自动重建。
- 候选窗口：`QbarSearchOutcome.RawCount` = 取进重排的条数；默认 2000（设置 200–20000）；
  只有谓词没有关键词时窗口压到 600（没有相关性可排，多取纯浪费）。
- 扫描早退：谓词-only 分支收满 `max(limit*2, 600)` 条样本即停（分值只差名字长度/深度）。
- 线程模型（红线 2）：UI 线程只跑翻译 + 非文件结果；文件检索在 `Task.Run` 里跑，
  回来必须校验 `seq == _querySeq` 且输入框内容未变，旧结果直接丢。

## 4. 优先级与打分

- 规则模型：`{ 目录子树, 可选文件名正则, 档位 High|Normal|Low|Ignored|DoNotIndex }`；
  求值只看结果集，最长作用域优先、同作用域后出现者覆盖（用户规则排在最后）。
- 内置规则包（全是 Low）：系统目录、Program Files、ProgramData、AppData、Windows.old、
  $WINDOWS.~BT、开始菜单；名字正则 `^\.` `^~` `^[^.]+$` `^node_modules$` `^\$`。
- 档位不是单独排序阶段，而是分值的加数（防止 High 里的烂匹配压住 Normal 里的精确匹配）。
- 分值全部整数：原生分 / 拼音质量分 + 档位加成；`Score` 越大越靠前，无匹配 = 1 也保留在末尾。

## 5. 降级策略

| 情况 | 表现 |
| --- | --- |
| 索引未就绪 / 正在建 | 一条说明行 + 拼音/模糊自动降级为「窗口内模糊」，不弹错误风暴（每进程一次） |
| 查询超时 | 返回已收候选，调试面板标「超时」 |
| 拼音索引未建好 | `PinyinPending` → 提示行「拼音索引正在建立」，本次只做子序列 |
| 谓词不支持（content:） | 调试面板标黄 + 结果按名字搜 |
| 规则把所有结果都藏了 | 底栏提示「有 N 条结果被优先级规则隐藏」+ 可查看原因 |

## 6. Everything 后端（2026-11-21 接入）

### 6.1 加载与调用约定

- 动态加载（`NativeLibrary.TryLoad` + `TryGetExport`），**不用 static DllImport**：
  没装 Everything 的机器上 DllImport 会在首次调用时抛 `DllNotFoundException`，
  而这里要求"没装就安静降级"，所以导出全部按需解析，核心四个
  （SetSearchW / QueryW / GetNumResults / GetResultFullPathNameW）缺一个就判定不可用。
- 候选 DLL：程序目录 `Everything64.dll`/`Everything32.dll` → 环境变量 `INPUTPLUS_EVERYTHING_DLL`
  → `%ProgramFiles%\Everything\Everything64.dll` → `%ProgramFiles(x86)%`（64/32）→
  `%LocalAppData%\Programs\Everything`。
- 请求字段：`FILE_NAME(0x1) | PATH(0x2) | FULL_PATH_AND_FILE_NAME(0x4) | SIZE(0x10) | DATE_MODIFIED(0x40)`；
  排序 `EVERYTHING_SORT_DATE_MODIFIED_DESCENDING(14)`；`SetMax(候选窗口)`、`SetOffset(0)`；
  `MatchPath=TRUE`（与自建索引"词可命中所在目录"的语义对齐）、`MatchCase/WholeWord/Regex=FALSE`。
- `Everything_QueryW(TRUE)` 同步等待；SDK 的查询状态是**进程级全局**，整段用 `lock(Gate)` 串行化，
  且必须跑在后台线程（红线 2）。SDK 没有取消接口 —— 调用方只能丢弃过期结果（`_querySeq`）。

### 6.2 Spec → Everything 查询串（`QbarEverythingBackend.BuildQuery`）

| Spec 字段 | Everything 语法 | 备注 |
| --- | --- | --- |
| Terms / Phrases | 逐词加引号（含特殊字符时） | 空格 = AND；短语整体加引号并转义内部引号 |
| NegTerms | `!词` | |
| Extensions | `ext:pdf;docx` | 分号 = OR |
| NegExtensions | `!ext:tmp` | |
| PathPins | `path:"D:\work"` | 含空格必须引号 |
| FilesOnly / FoldersOnly | `file:` / `folder:` | |
| MinSize/MaxSize | `size:>=1mb` / `size:<=1gb` / `size:10mb..1gb` | 1 MB = 1024 KB |
| MinDay/MaxDay | `dm:>=2026-07-01` / `dm:2026-07-01..2026-07-31` | 天 → 本地日期串 |
| Glob（wfn:） | **不下发** | Everything 的 `wfn:` 不认我们的 glob 语义，交给本地复核 |
| RegexText | **不下发** | 全局开正则会污染普通词，本地 `Regex` 复核 |
| ContentRequested | **不下发** | 索引不含正文 |

### 6.3 为什么拿回来还要"本地复核"（`NativeSearchIndex.SpecFilter`）

Everything 的匹配语义与自建索引有三处**必然**偏差，不复核就会"换个后端结果就变"：

1. `MatchPath=TRUE` 时词可以命中**路径任意位置**，而自建索引只允许命中"文件名 + 所在目录"；
2. `ext:` 之后 `!` 否定的组合、`size:` 的边界解释（`>` 是否含等号）与我们的 Spec 不完全同口径；
3. `dm:` 用的是本地时间字符串，跨时区/夏令时的边界日会漂。

所以流程是「Everything 出候选窗口（默认 2000，最多 20000）→ `SpecFilter.Accept` 逐条复核 →
交引擎打分重排」。复核谓词与自建索引**共用同一份代码**（`BuildSpecTokens`/`CompileSpecRegex`/
`PathPinMatch`/`GlobMatch`/`ScoreFile`），这也顺带保证了 `wfn:`/`regex:` 两条本地能力在
Everything 后端下照样能用。目录的资格判定（`dirsEligible`）也必须一致：带 `ext:`/`size:`
或 `file:` 时目录不出现。

### 6.4 状态与降级

| 状态 | `StatusText` | 行为 |
| --- | --- | --- |
| 找不到 DLL | "Everything 未安装（找不到 Everything64.dll）" | 后端 = 本地索引，不提示错误 |
| DLL 在、客户端没跑（版本 0.0.0） | "Everything 客户端没在运行（SDK DLL 已加载：…）" | 后端 = 本地索引；`Query` 会返回错误码 2 |
| 客户端在跑、索引未加载 | "Everything x.y.z 正在建立索引（索引库未加载）" | 后端 = 本地索引（否则会把"索引在建"显示成"没有这个文件"） |
| 正常 | "Everything x.y.z（管理员）索引已加载" | 后端 = Everything |
| 查询失败 | `LastErrorText`（错误码 0–9 按 Everything.h 翻译） | 本次回落到本地索引，`BackendDetail` 写明原因 |

错误码文案照抄头文件：0 OK / 1 内存 / 2 IPC（客户端没运行）/ 3 注册窗口类 / 4 创建监听窗口 /
5 创建监听线程 / 6 下标越界 / 7 调用顺序（SetSearch 与 Query 必须成对）/ 8 请求数据无效
（SetRequestFlags 必须在 Query 之前）/ 9 参数无效。诊断命令：`--everything-status`。

### 6.5 管理员模式与 Everything

`RunAsAdmin` 默认改为 `true`（设置页仍可关）。两个后果必须说清：

- Everything 的 IPC 允许普通进程查询（SDK 不要求管理员），所以**提权不是为了 Everything**，
  而是为了键盘钩子能收到提权窗口的按键（UIPI）。
- 提权后子进程（如 `cmd`）也在管理员上下文里启动，`ShellExecuteEx` 的 `runas` 不会重复弹 UAC。
- 自检/诊断开关一律不触发提权重启：`ElevationHelper` 除了显式白名单，还按
  `CliFlags.IsDiagnostic` **类别**豁免 —— 新增诊断开关不必再同步第二份清单
  （历史上就是漏同步导致默认管理员模式下跑基准先弹 UAC）。

### 6.6 真机结论（2026-11-21，Everything 1.4.1.1028 便携版 / 索引约 115 万条）

首次真正连上客户端后才拿到的运行时证据，逐条对应 §6.4 里原先只有代码依据的四点：

- **索引前提**：Everything 必须以管理员运行才会有索引库。非管理员时 `Everything.db` 只有 177 字节、
  `IsDBLoaded=false`、任何查询都返回 0 条 —— 与本程序默认管理员启动（D21）正好契合。
- **`SetSort(14)`** 行为符合预期：窗口按修改时间降序，最近动过的文件先进入候选窗口，对
  "打开我刚弄的那个文件"这类主要场景是正确取舍。
- **`size:` / `dm:` / `file:` / `!ext:`** 四个谓词实测都按预期生效；`MatchPath=TRUE` 的匹配面
  确实比自建索引宽（"词命中路径任意一段"），这正是复核层（6.3）要拦的部分。
- **`GetResultFullPathNameW` 两段式长度查询**（先传 0 取所需长度、再取内容）在 2 万条窗口下正常，无截断。
- **延迟拆解**：SDK 单次 9–43 ms（全库子串扫描，随索引规模线性增长），本地层（翻译 + 复核 + 重排）
  ≤ 8 ms。所以在 115 万条索引的机器上端到端 p95 是 54.5 ms，比 50 ms 预算多 4.5 ms，全部在 SDK 内。
  对策不是放弃 Everything，而是给用户一个开关（设置 → 搜索语义层 → "优先用 Everything 取候选"）：
  关掉就回到本地索引路径（实测 p95 39.2 ms）。

### 6.7 候选窗口语义（D22）

`SetMax` 在 Everything 侧的含义是"只要排序后的前 N 条"，不是"分页取全"：SDK 有 `SetOffset`
但没有"下一页完成后还能保证与上一页一致"的概念，索引随时在变。因此：

- 本地打分/优先级只能作用在窗口内。窗口外的命中**不可能**被排到前面 —— 这是设计选择，
  不是缺陷，必须在诊断里显式可见（`--qbar-bench` 的"缺失 / 多出"列就是为此）。
- 窗口默认 2000（可配 200–20000），排序键选修改时间降序。真机对比过名字序（sort=1）：
  `report` 延迟没有优势（57.6 ms vs 53.7 ms），而且窗口按字母截断更容易把近期文件挤出去。
- 明确不做"先本地排序再回 Everything 分页"：既省不了 SDK 扫描，又会漏结果（§3.4）。

## 7. 命令标签（keyword + 空格）与「直接搜索」里的命令

### 7.1 命令也能变成标签

`ResolveChip` 现在认四类，**顺序固定**：网址 → 程序 → 搜索 → 命令。命令排在最后的
理由是一个兼容性问题：`bd`、`gh` 这类老关键字必须先保持原义，否则用户升级后按空格
得到的是"执行某条命令"而不是搜索引擎（D26）。

`ChipKind.Command` 与 `ChipKind.Run` 的区别不是外观而是执行路径：

| | Run（启动程序） | Command（自定义命令） |
|---|---|---|
| 数据 | `QEntry{Key,Value}` | `QbarCommand{Id,Keyword,Title,Path,Parameter,WorkingDirectory,RunAsAdmin,Silent}` |
| 参数 | exe 后面拼输入 | `{query}` 模板展开，空格加引号由用户自己写 |
| 提权/静默/工作目录 | 无 | 有，交给 `QbarCommandStore.Execute` |

标签态下查询（`QueryInternal` 的 `ChipKind.Command` 分支）**按 CommandId 重新取一次命令**，
不用 chip 里的快照 —— 用户在设置里改了参数模板，下一次回车就该用新模板，不必重建 chip。
结果行固定一条：`Action="qcmd:<id>"`、`Arguments=tail`，沿用已有的
`QbarWindow.ExecuteQbarCommand` 执行；输入为空时标题是「用 X 查询…」。

### 7.2 「直接搜索」里的命令

选中文字按一个键直接查词，走的是 `ActionCatalog` 里的 `qcmdsearch:<id>` 动作：

- 只给 **Path 非空**的命令注册。`newFile` / `newFolder` / `copyPath` 这些内置内部动作没有 exe，
  塞进「直接搜索」只会让用户下拉里多出一堆按了没反应的项。
- 执行体是 `ActionContext.SearchSelectionWithCommand(idOrKeyword, overrideText)`：
  先按 Id 再按 Keyword 找命令（绑定里存的 Id 可能在设置里被删掉 → 明确提示"找不到这条命令"），
  然后读选区（UIA → 选区监听器 → 请用户 Ctrl+C 三级兜底），`Trim()` 后超过 2000 字符截断
  （参数不是网址，这个上限只防手滑全选），最后交 `QbarCommandStore.Execute`。
- 拦截钩子 `RunQbarCommandInterceptForTest` 只替换"真正启动程序"这一步，成功文案照发 ——
  否则自检只看到一半流程，用户看到的那句反馈反而没人测。

## 8. 设置界面：把搜索的旋钮集中到「文件搜索」标签

标签顺序：**搜索指令 / 文件搜索 / 启动程序 / 常用网址 / 固定项**。

- `Tag="files"` 的标签切过去时，左侧条目列表与筛选栏整体隐藏，`FileSearchPanel` 跨两列铺满，
  右栏（编辑条目 360px）折叠。理由：过滤器（扩展名表、限定目录）与优先级（目录/正则/档位）
  都不是"一行一个输入框"的形态，塞在 360px 的右栏里只能靠弹窗，反而更难找（D27）。
- 面板内的卡片，顺序＝用户排障时的思考顺序：
  1. **索引与后端**：状态点、索引规模、优先用 Everything 取候选、刷新状态 / 重建索引；
  2. **过滤器（表格）**：一行 = 名称+图标 / 关键字 chip / 扩展名表 / 搜索范围 / 开关。
     内置 6 条（doc/pic/video/audio/file/folder）分在「文件类型」组、不可删除但可覆盖，
     自定义在「自定义过滤器」组；下面是选中项的编辑器（扩展名、附加搜索词、限定目录、
     时间范围、启用、包含文件/目录）。非法正则在保存时就拒绝。
     行高 44px、扩展名单行截断：49 项扩展名不可能全展示，能一眼认出是哪一类就够了。
  3. **优先级（分组表）**：高优先 / 正常 / 低优先 / 从搜索结果中排除 四组，
     内置 16 条照 Listary 默认表逐条列出、每条一个开关（写 `DisabledBuiltins`）；
     「低优先」组首行是「默认不展示系统及隐藏文件的搜索结果」（写 `HideLowResults`，默认关）；
     高优先为空时给提示行。下面仍是目录 / 文件名正则 / 档位 / 备注 / 启用的编辑器。
  4. **命令**：内置命令（cmd/psh/explore/ol/…）与自己的命令两组，行内显示关键字 + 标题 + 程序/参数，
     右侧标「内置 / 自定义」；下面是关键字、标题、程序、参数、工作目录、管理员、静默的编辑器。
     内置项不可删除，用同关键字新建即覆盖（`Effective()` 的规则）。
  5. **语法与调试**：六个开关、候选窗口滑块、输入防抖滑块、刷新拼音索引、规则与命令清单入口。
- 渲染回归钩子（`RenderTools`）：`INPUTPLUS_RENDER_PAGES=qbar` 只出这一页；
  `QbarSettingsView.SelectTabForRender("files")` + `ScrollFilesToForRender(ratio)` 让
  `--render` 能直接拍到该标签的上/中/下三段 + 0.75 处的命令卡，不用手点。
  `ScrollViewer` 为此命名为 `FileScroll`。

## 9. 展开窗口：把结果摊成 Listary 那张表

Qbar 面板只有十来行，扫一眼可以，比名字/比日期不行。右上角「展开」按钮开
`QbarExpandWindow`（1120×720，可缩放），它**复用同一个** `QbarSearchEngine`，
不是第二条搜索路径：

- 左栏分面：类型 7 桶（全部/文件夹/文档/图片/视频/音频/其他）+ 时间 6 桶
  （全部/1 天/7 天/30 天/365 天/更早）；计数基于**本页候选窗口**，不是全库。
- 右栏表格：名称（图标 + 文件名）/ 路径（父目录）/ 修改日期 / 大小，
  `VirtualizingStackPanel` + `Recycling`，几千行也只建可视区的容器。
- 交互：140ms 防抖 + `_seq` 丢弃旧结果；双击或 Enter 打开；Esc 清掉类型/时间筛选。
- 数据：`Row` / `FacetVm` 是 **public 嵌套类 + public 属性**（WPF 绑定的硬要求，见 D31），
  `ListView` 用 `RowItem.Grid`（`GridViewRowPresenter`）而不是 ListBox 的 `RowItem`。
- 渲染：`--render` 会开一次该窗口、查 `report`、拍 `qbar-expand.png` 再关掉 ——
  这个窗口平时要靠点按钮才出现，没有这张图就等于没人验过；随后切到「大小降序」再拍
  `qbar-expand-sorted.png`，表头箭头与数值序一起验。

### 7.5 列排序（表头点击）

排序只作用于**已经取回并筛过的这一屏**，不触发新查询 —— 排序不该让 Everything 再跑一遍。

- 键：`name` / `folder` / `date` / `size` / `rank`（默认，相关度序）。
- 比较用的是 `Row` 上的**原始值** `DayValue`（天序号）与 `SizeBytes`，不是
  `DateText`/`SizeText`。显示文本排出来的顺序是错的：`"2026-1-9"` 会排在
  `"2026-07-08"` 后面、"10 MB" 会排在 "9 KB" 前面。
- 每次比较都以 `Rank`（原始候选窗口里的下标）兜底，等价于稳定排序：
  重新筛选/切换分面时同值行不会换位置。
- 点一次按该列排（日期、大小默认降序 —— 用户第一眼要看的是"最新/最大"；
  名称、路径默认升序），再点切反向，**第三次回到 rank**（总要能绕回默认序）。
- 表头 `HorizontalContentAlignment=Left`，与左对齐的内容列一致；`Cursor=Hand` 提示可点。
- 局限：Everything 侧仍按"修改时间降序取前 N 条"取窗口，按名称排序只在这 N 条内有序。
  要全量名称序只能把窗口提到 20000，代价是查询变慢，暂不做（见 D32）。

## 6.8 候选窗口的两趟取法（MatchPath 分级）

Everything 的 `Everything_SetMatchPath(TRUE)` 让每个词也能命中路径，召回与本地索引一致，
但 Everything 要为每个候选多扫一遍路径：本机 115 万条索引实测端到端 p95 从 34.7ms 涨到 56.0ms。
所以窗口分两趟取（QbarEverythingBackend.TryRun）：

1. **第一趟 MatchPath=FALSE**：只让词命中文件名。绝大多数查询在这一趟就够（6–21ms）。
2. **第二趟 MatchPath=TRUE**：仅当第一趟候选 < 8 条时补扫，把结果按路径去重并进来
   （名字命中必然也是路径命中，所以第二趟是第一趟的超集，并集即全量召回）。
   补扫会让这一次查询多花约 25–30ms，但它只发生在"几乎什么都没搜到"的时候 ——
   这时用户确实是在找一个具体文件，值得多等 30ms。

调试面板会在后端一行注明「（含路径补扫）」，所以"为什么这次慢"不用猜。

## 6.9 三个特殊谓词的去向

| 用户写法 | 去向 | 理由 |
| --- | --- | --- |
| `wfn:*.pdf` | 下发 `wfn:*.pdf` 给 Everything，同时留在 spec 里给本地复核 | 不下发就等于发空查询，窗口里没候选，复核再严也筛不出来（实测 0 条） |
| `regex:^rep.*\.docx$`（整条只有正则） | 原样下发给 Everything，并只对这一条开 `Everything_SetRegex(TRUE)` | 1.4 没有逐词 `regex:` 语法（实测恒 0 命中）；整条查询就是一条正则时，全局开关不会误伤别的词 |
| `regex:` + 其它条件 | 由本地快照执行 | 全局正则开关会把普通词也当正则解释，结果必错，只能换后端 |
| `content:关键词` | 降级成普通文件名检索，调试面板标黄说明 | 两条后端都没有内容索引；假装接受了这个词却不筛，用户会以为全盘都匹配 |

注意 `wfn:`/`regex:` 加引号时**不能**把反斜杠加倍（`QuoteRaw`）：正则里的 `\.`
和 glob 里的 `C:\dir\*` 都带反斜杠，加倍后 Everything 收到字面双反斜杠，实测 0 命中。

## 7.6 命中高亮来自 SDK

`RequestFlags` 增加 `Everything_GetResultHighlightedFileNameW`（0x00002000），
结果名里用 U+0001/U+0002 包住命中片段；`EverythingApi.ParseHighlightedName` 解析成
`[起点,长度]` 交错数组，交给 `QbarTitleHighlight.Segments` 切段。
本地索引兜底时才用自己的子串匹配算区间；两处都只返回区间，字符串拼接在 UI 层做。


## 7.7 展开窗口：真图标、紧凑行高、列排序（2026-11-21）

- **图标**：名称列 18×18 图标位，`Image` 绑定 `Row.IconSource`（`ShellIcons` 解析），
  Segoe 字形保留作兜底；`HasIcon` 为真时字形隐藏。只为容器已生成的行取（`ItemContainerGenerator`
  `StatusChanged` + `ScrollChanged`），缓存命中同步设、否则后台回调。
- **行高**：`RowItem.Compact`（分面 ListBoxItem，Padding 9,2 + 内层 22）与
  `RowItem.Grid.Compact`（结果 ListViewItem，同一套 Padding）；分面/结果行实际约 30px，
  1180×600 小窗能装下 7 类型桶 + 6 时间桶。
- **排序**：点表头切换 `_sortKey`（rank/name/folder/date/size），首次点日期与大小默认降序，
  第三次点回 rank；比较用原始值（`DayValue`/`SizeBytes`），相等时回落查询顺序 `Rank`。
- **高亮**：`QbarTitleHighlight.Segments` 把 SDK 给的命中区间切成 [普通, 命中, 普通…]，
  由模板决定样式，数据层不拼字符串。

### 7.8 展开窗口的布局与表头（2026-11-21）

- 左栏 Grid：`Auto / * / Auto / *` —— 「筛选」「最近修改」各吃一个星号，小窗也是各占一半；
  旧定义 `Auto / Auto / * / Auto / Auto` 会把空白全挤在类型表下面、时间桶推到底部。
- 表头：共享样式 `GridView.Header`（Theme/Controls.xaml）透明底 + 0,0,0,1 下边框 + 11.5px `Brush.TextTertiary` + 左对齐，
  数字列用 `GridView.Header.Right` 右对齐。拖动把手 `PART_HeaderGripper` 自带模板：平时透明，悬停显一条 `Brush.Border`。
- 列宽常量必须与 `FitPathColumn` 同步：Name 280 / Path 动态 / Date 118 / Size 84 / 其余 34。
  `GridViewColumn` 不是 `FrameworkElement`，写 `MinWidth` 会 MC3072，宽度下限只能在代码里夹。

### 7.9 过滤器的触发路径（2026-11-21）

```
输入 "doc"          → Key.Space → TryRunQuickCommand（引擎关键字，如 cmd/ol）
                               → TryMakeChip（搜索标签）
                               → TryMakeFilterChip → QbarService.ResolveFilterTrigger
                               → ApplyFilter(id) → chip + 文本去掉触发词 → RunQuery
输入 "doc:"（老写法）→ 同样的 ResolveFilterTrigger，只是由回车/点行触发
```

过滤器只改"查询"，chip 由 `_appliedFilter` 持有，`EffectiveInput` 在查询前补回 `keyword:` 前缀，
所以翻译层与 CLI 看到的规格完全一样（`--qbar-query "doc: 报表"` 与面板里 chip + 报表等价）。


### 7.8 展开窗口：外观与分面布局

```
Window (QbarExpandWindow)
  WindowChrome  CaptionHeight=46  ResizeBorderThickness=6  GlassFrameThickness=0  CornerRadius=12
  ctor → WindowChrome.AttachTo(this, "Brush.Background")
           ├ DWMWA_USE_IMMERSIVE_DARK_MODE  ← ThemeManager.IsDarkTheme()
           ├ DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
           ├ DWMWA_BORDER_COLOR = COLOR_NONE
           └ 订阅 ThemeManager.ThemeChanged，系统切换时重设一次
  Grid 46 / Auto / * / Auto
    ├ 标题行（自绘标题 + 关闭）
    ├ 搜索行（QueryBox + 刷新）
    ├ 主体 列 236 / *
    │    ├ 左：筛选 ListBox（UniformGrid Rows=Items.Count，行高均分）
    │    │      最近修改 ListBox（同上）
    │    └ 右：ListView（虚拟化，列 = 名称/路径/修改日期/大小，可点头排序）
    └ 状态行（N 个项目 · 双击或 Enter 打开 · Esc 清除筛选）
```

- **为什么分面用 UniformGrid**：固定行高 + MaxHeight 的组合在小窗里必然裁掉时间桶
  （用户报「只有全屏才看得到最近 30 天」），大窗里又留一大片空白。均分之后两个问题一起消失。
- **为什么圆角不自己画**：见 DECISIONS D48 —— DWM 的圆角参与 Mica 与系统阴影，
  自绘要开 `AllowsTransparency`，代价是硬件加速与阴影。
- **怎么验证**：`--render` 会额外调 `SaveOnScreen`（`src\InputPlus\UI\RenderTools.cs`），
  把窗口摆到屏幕上用 `CopyFromScreen` 取窗口外扩 12px 的矩形 —— 圆角外侧就是桌面像素，
  `RenderTargetBitmap` 拍不到的那一层只能这么看。

### 7.9 过滤器身份：Id 优先，关键字其次

```
Create(filters):
  catalog ← 6 条内置（Id = builtin.doc / builtin.pic / …）
  for item in filters:            # 第一轮：认 Id
      if item.Id 命中内置 → Override(内置, item)   # 关键字、扩展名、范围、开关全取用户值
  for item in leftovers:          # 第二轮：认关键字（兼容老配置）
      if item.Keyword 命中内置 → Override(内置, item)
      else → catalog.Add(item)
```

`Override` 会先把旧关键字从 `_byKeyword` 里摘掉（`ReferenceEquals` 判定），再写入新关键字。
这样「把 doc 改成 WD」之后：列表里只有一条、`TryFind("doc")` 返回 null、
`TryFind("WD")` 返回同一条；面板里 `WD + 空格` 变 chip 时也能按 `filter.Keyword` 把触发词切掉。

### 6.10 过滤器 → 动作 → 面板 chip

```
按键绑定  qfilter:<Id>                          fsearch:<Id>
             │                                     │
             │                                     ├─ 取选区（复用 SearchSelection 的取词链）
             │                                     │    三层提示 / Ctrl+C 保底 / 截 300 字
             │                                     ↓
        ActionContext.RequestQbarFileSearch(Id, 词?)
             │  事件 QbarFileSearchRequested
             ↓
     AppHost.ShowQbarFileSearch  →  QbarWindow.OpenFileSearch(Id, 词)
             │  写触发词进输入框 → ApplyFilter(Id)（清触发词、亮 chip、重查）
             ↓
        过滤器 chip + 查询词 → 翻译层 → Everything
```

- 为什么不走 `Open(prefill)`：预填协议只认引擎表，`TryMakeChip` 不认过滤器；chip 必须在面板里由 `ApplyFilter` 建立。
- 为什么先写触发词再 `ApplyFilter`：`ApplyFilter` 要靠 `Input.Text` 判断「要不要把触发词从查询里切掉」，
  空着直接调它会把上一次留在框里的内容整段当成搜索词。

### 6.11 查词：一个出口，两个分类（2026-11-21）

```
「直接搜索」dict.lookup            「Qbar 搜索」qdict.lookup
   取选区（TryCaptureWord）            取选区（同一个 TryCaptureWord）
        ↓                                    ↓
   DictionaryLauncher.Launch(词, 命令)   ActionContext.RequestQbarDictionary(词)
        │  程序 = 用户命令.Path            │  事件 QbarDictionaryRequested
        │        ?? EudicLocator.Find()    ↓
        │  参数 = 用户模板（留空补 -w "{query}"）   AppHost.ShowQbarDictionary
        ↓                                        ↓
   QbarCommandStore.Execute                 QbarWindow.OpenDictionary(词)
   （runas / silent / workdir 一套）          关键字 → chip → 词 → 查
```

- 网页兜底：定位不到程序时开 `https://dict.eudic.net/dicts/en/<已转义词>`，并明确告知「没装欧路」，
  不假装查词成功。
- 取词只有一处（`ActionContext.TryCaptureWord`）：三层失败提示按权限分级、超长按代理对截断、
  Ctrl+C 保底。搜文件与查词共用，避免「同一个选区两条路给出不同提示」。
- 命令身份：`EffectiveCommand` 先按 `QbarCommandStore.BuiltinDictId` 认，再按关键字认 ——
  用户把 `ol` 改名成别的，动作仍然指向同一条命令（D46/D51 的同一教训）。
- 内置 `ol` 的 `Path` 故意留空：定位在运行期做（装没装欧路、装在 D:\software\eudic 之类的非默认目录），
  写死路径的后果是换机器就失效。

### 6.12 过滤器关键字别名与「停用」语义（2026-11-21 / v1.7.7）

真实配置里六个内置过滤器的关键字被改成了拼音缩写（wd / tp / sp / yp / wj / wjj），于是
`doc:` / `file:` / `folder:` 全部 `resolveKeyword (none)` → 降级成搜字面量 → 命中 0（`file: report`
更明显：Everything 命中 1922 条，本地复核把它们全剔了，因为它们在找含 `file:` 字面量的文件名）。
这不是翻译层写错，而是「改名即删旧名」这条规则在真实数据上太脆：肌肉记忆长在手指上，用户改关键字是为了更快，
不是为了废掉 `doc`。

| 用户关键字 | 出厂别名 | 解析结果 |
|---|---|---|
| wd | doc | 同一条 builtin.doc（ext 51 项：用户加过 epub;mobi） |
| tp | pic | builtin.pic |
| sp | video | builtin.video |
| yp | audio | builtin.audio |
| wj | file | builtin.file（files-only） |
| wjj | folder | builtin.folder（dirs-only） |

实现：`QbarFilterCatalog` 里有 `_byKeyword`（用户可见关键字）与 `_byAlias`（只装内置项的出厂关键字）。
`TryFind` 先查前者再查后者；别名不进设置，所以设置文件不会多出一条「看不见的过滤器」，
「恢复默认」也只需清空覆盖值。自定义过滤器没有别名（用户自建项的关键字就是它唯一的名字）。

停用语义：合并时不再跳过 `Enabled=false` 的项 —— 跳过会让 `Override` 把 `Enabled` 写死成 true，
设置页那个开关就成了「点了没用、刷新又弹回来」，而且内置项还会继续参与查询。现在停用项留在列表里、
`Enabled` 听用户的；翻译层遇到停用的过滤器把整条 token 当没写（不展开扩展名、也不降级成普通词），
并在 resolveKeyword 里留一行「xx: 已停用 -> 忽略」。理由：降级成普通词会让 `doc: 报表` 去搜带冒号的字面量，
命中 0 —— 用户只会觉得坏了。

现场诊断入口（都只读，故意不执行任何动作）：

- `--settings-dump`：数据目录 / 设置文件大小 / 加密与解锁状态 / `SettingsStore.LoadFailure` / 用户层过滤器 / 合并后的过滤器 / 命令 / 规则 / 选项。
  排「设置页看着配好了、搜索却不认」时，第一个该跑的就是它 —— 它打印的是**程序实际读到的**那一份。
- `--qbar-command "ol hello"`：命令表、命中项、模板原文、占位符展开结果、程序是否存在、裸程序名能否被 PATH 解析。
  这条把「没命中命令」「命中了但找不到程序」「参数没展开」三种故障分开，修法完全不同；不执行是有意的（RunAsAdmin 会弹 UAC）。
