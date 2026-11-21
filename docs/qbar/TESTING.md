# Qbar 语义层：测试与基准

> 数字都来自本机实测（Windows 11 / .NET 10 / Release）。换机器请重跑命令，别抄数字。

## 1. 怎么跑

```powershell
dotnet build src/InputPlus/InputPlus.csproj -f net10.0-windows -c Release -v q
cd src/InputPlus/bin/Release/net10.0-windows/win-x64

# 全量自检（会跑既有 6000+ 项 + 新增语义层 54 项）
$env:INPUTPLUS_DATA='D:\tmp\ipst'; ./InputPlus.exe --selftest; echo EXIT=$?
Get-Content D:\tmp\ipst\selftest.log -Tail 3

# 单条查询：打印每一层翻译结果与命中
./InputPlus.exe --qbar-query "doc: 报表"

# 20 条基准 + 本地重排 + CJK 名字索引
./InputPlus.exe --qbar-bench
```

注意三点，否则数字没意义：
1. `INPUTPLUS_DATA` 必须显式给，自检才会把日志写进可预期目录；
2. 跑基准前关掉其它占 CPU 的活（基准取中位数，但抖动仍会污染 p95）；
3. 改完代码必须先 `dotnet build` 再跑，`InputPlus.exe` 被自检/基准占用时构建会锁文件。

输出：`%TEMP%\clp-qbar-query.txt`、`%TEMP%\clp-qbar-bench.txt`，同时各写一份到数据目录。

## 2. 自动化用例清单

分组入口：`SelfTest.cs` 里 `Section("Qbar 语义层"); TestQbarSemantics();`，实现位于
`src/InputPlus/SelfTest.QbarSearch.cs`（54 项断言）。全套自检目前 **通过 6201 项 / 失败 0 项**。

| 组 | 方法 | 项数 | 覆盖什么 |
| --- | --- | --- | --- |
| 翻译 | TestTranslator | 17 | doc:/ext:/size:/date:/-ext:/短语/特殊字符/降级/归一化/内置表项数/folder: 只搜目录/用户覆盖内置 |
| 优先级 | TestPriorityRules | 8 | 系统目录默认降权、普通目录 Normal、DoNotIndex/Ignored 分档、High 覆盖内置、禁用规则、身份串稳定、总开关 |
| 结构化检索 | TestSpecSearchAndEngine（前半） | 6 | 夹具树发布成全局快照、只 docx、否定扩展名、只目录、size 下界、date 下界 |
| 引擎 | TestSpecSearchAndEngine（后半） | 5 | DoNotIndex 彻底剔除、剔除计数进诊断、命中区间、分面本页统计、High 前置 |
| 命令 | TestCommandStore | 8 | Match 取参数、四个占位符、无占位符原样、同名覆盖内置、内部动作识别、空路径文案 |
| 历史 | TestHistoryLog | 5 | 追加、常用聚合、按月文件、每行一条事件、读写不抛 |
| 拼音 | TestPinyinFallback | 4 | 未就绪返回空、构建完成、首字母命中、全拼命中 |
| 模糊 | TestPinyinFallback（末） | 1 | chrme → chrome 子序列 |

夹具：`SelfTest.NativeIndex.cs` 的 `BuildIndexFixture(root)`（197 文件 / 18 目录，含 Chrome.exe、
notes.txt、报告-2026.docx、设计稿/首页.png、项目A/文档/说明.md、a/b/c/DeepSeekHarness.log、
190 个 批量/NN/filler-NNN.txt）。结构化查询用 `NativeSearchIndex.SearchSpecInForTest`，
引擎/拼音/模糊用 `NativeSearchIndex.PublishForTest` 把夹具树发布成全局快照。

## 3. 实测数字（2026-11-21 08:21，385726 文件 / 69072 目录 / 规则 16 条 / 候选窗口 2000）

| 指标 | 预算 | 实测 | 结论 |
| --- | --- | --- | --- |
| 翻译层（2000 次平均） | ≤ 5000 us | **15.57 us/次** | 通过 |
| 20 条查询端到端中位数 p95 | ≤ 50 ms | **26.0 ms**（另一轮 42.5 ms，最慢单条 47.6） | 通过 |
| 本地重排 2000 候选 RankMs | ≤ 50 ms | **中位 2.69 ms / 最大 14.99 ms** | 通过 |
| 感知延迟（输入到列表更新） | ≤ 100 ms | 翻译 0.016 + 检索 10~47 + 重排 3 + UI 一次本页刷新 | 通过 |
| CJK 名字索引构建 | —— | **10480 条 / 61 ms** | 通过 |
| CJK 名字索引内存 | ≤ 24 MB（硬上限） | **2.89 MB**（估算口径；GC 增量 0.0 MB 说明埋在大堆里看不出来） | 通过 |
| 拼音查询耗时 | —— | **1.02–1.61 ms**（yh / yinhang / bg 各 5 条） | 通过 |

内存口径说明：`EstimatedBytes` = 每条 32 字节对象开销 + 名字/首字母/全拼字符数 × 2 + 3 × 26 字节的定长开销。
`GC.GetTotalMemory(true)` 的差值只能当参考（托管堆几百 MB 时几 MB 分辨不出来）。按同一口径外推到 300000 条约 82.8 MB，
所以代码里另有 24 MB 硬上限，超了停止收录并在状态行写明 —— 宁可少收，不许吃掉用户内存。

单条查询中位（ms）：report 12.6 / rprt 13.6 / yh 10.0 / yinhang 10.6 / doc: 3.2 / doc: 报表 10.9 /
ext:pdf;docx size:>1mb 5.4 / date:week 3.6 / -ext:tmp 2.5 / "年度 总结" 47.6（0 命中）/ ( 0.0 /
C:\Windows 20.3 / pic: 4.0 / video: 6.2 / audio 30.2 / folder: 2.6 / size:10mb..1gb 3.1 /
wfn:*.pdf 29.5 / reporter 10.3 / 说明 20.7。

回归闸门（`--qbar-bench` 的「旧入口比对」列，规则全关后与 `NativeSearchIndex.Search` 比相对顺序）：
report「一致（39 条）；规则重排 39 条」、yh「一致（40 条）；规则重排 39 条」、
reporter「一致（29 条）；规则重排 23 条」、说明「一致（40 条）；规则重排 18 条」；
rprt / yinhang / ( / C:\Windows「一致（0 条）」。

## 4. 任务书 26 条验收用例对照

「自动」= 已进 `--selftest`；「CLI」= 用 `--qbar-query` 看翻译链；「手工」= 需要真人看界面。

| # | 用例 | 覆盖 | 怎么验 |
| --- | --- | --- | --- |
| 1 | `report` 与改造前一致 + 名字内高亮 | 自动（回归闸门 + 引擎高亮）+ 手工 | CLI：看「旧入口比对」列为「一致」；界面看下划线 |
| 2 | `rprt` 子序列命中 | 自动（模糊 chrme→chrome）+ 手工 | 界面输入 rprt，出现「模糊结果」 |
| 3 | `yh` / `yinhang` 命中「银行」类名字 | 自动（拼音 sjg/shejigao）+ 手工 | 界面输入 yh；未就绪时提示「拼音索引正在建立」 |
| 4 | `doc:` 只剩 49 类扩展名，chip 可 Esc 清除 | 自动（翻译 49 项）+ 手工 | 输入 doc: → 顶部「过滤器：文档」→ Tab/Enter 变 chip → Esc 复原 |
| 5 | `doc: 报表` 叠加，调试面板看到展开串 | 自动（翻译）+ CLI | `--qbar-query "doc: 报表"` 看「索引查询」行；界面 Ctrl+D 看调试面板 |
| 6 | `ext:pdf;docx size:>1mb` 组合 | 自动（两个谓词各一项）+ CLI | 同上 |
| 7 | `date:week` → 只含本周 | 自动（MinDay/MaxDay + dm:thisweek） | `--qbar-query date:week` |
| 8 | `-ext:tmp` → 否定 | 自动（NegExtensions） | `--qbar-query -ext:tmp` |
| 9 | 短语不被拆词、内部引号转义 | 自动（短语整体保留） | `--qbar-query '"年度 总结"'` |
| 10 | `(` / `a|b` 不崩且能搜到 | 自动（特殊字符当普通词） | CLI 同左 |
| 11 | `C:\Windows` 结果后置，关规则包还原 | 自动（系统目录降权 + 总开关）+ 手工 | 设置页取消「启用优先级规则」再搜 |
| 12 | DoNotIndex 彻底消失 / Ignored 消失但提示 N 条 | 自动（引擎两档 + 计数） | 设置页改规则 → 底栏「有 N 条结果被优先级规则隐藏」 |
| 13 | `^~` 改 High → `~$` 文件前置 | 自动（High 覆盖内置）+ 手工 | 加规则 FolderPath 空 + NameRegex `^~` + high |
| 14 | 规则改动即时生效 | 自动（每次查询现算 Resolver）+ 手工 | 改规则不重启，再搜 |
| 15 | 2000 候选本地重排 p95 ≤ 50ms | 自动（基准） | `--qbar-bench` 看「本地重排」行 |
| 16 | `ol hello` 命令行与结果并列，执行收到 `-w hello` | 自动（Match + Expand）+ 手工 | 配命令 ol，输入看两行都在 |
| 17 | `ol hello world` 多参数 + UI 提示 | 自动（Expand 的 {query} 一次替换）+ 手工 | 参数模板 `-w "{query}"` |
| 18 | RunAsAdmin 取消 → 「提权被取消」 | 自动（错误码文案分支）+ 手工 | 配 runas 命令，UAC 点取消 |
| 19 | Silent 不闪黑框 | 手工 | 配 Silent 命令执行 |
| 20 | Path 只写 `notepad` 走 PATH | 自动（UseShellExecute 语义）+ 手工 | 配 notepad 命令 |
| 21 | 分面计数与可见结果一致（本页口径） | 自动（FacetSummary 以「本页：」开头） | 底栏右侧看分面串 |
| 22 | 空查询显示最近/收藏/命令，不列全盘 | 自动（既有 DefaultResults）+ 手工 | 清空输入 |
| 23 | 历史 JSONL 追加 + 月度聚合 | 自动（5 项） | 数据目录 `qbar-history\events-yyyy-MM.jsonl` |
| 24 | 规则全关 + 临时文件目录 → 不静默丢弃 | 自动（总开关 → 一律 Normal）+ 手工 | 设置页关规则后搜 tmp 目录 |
| 25 | 未运行/未加载 → 提示 + 重试，不崩 | 自动（降级提示分支）+ 手工 | 首次启动即搜，看「文件索引还没就绪」 |
| 26 | 输入法 composition 期间不查，上屏查一次 | 手工（`TextCompositionManager` 三个事件） | 中文输入法打「baobiao」，观察只在选词后刷新一次 |

## 5. 已知测试盲区
- 手工项（18/19/20/26）依赖真实 UAC、真实输入法，自动化只验到分支文案与事件挂接。
- 分面只统计当前页候选（设计如此），不是精确全库计数；用例 21 已按「本页统计」口径覆盖。
- 拼音索引内存上限触发路径没有单测（需要构造 30 万 CJK 名字），靠 `DebugSummary` 与状态行观测。

## 6. Everything 后端（2026-11-21 追加）

### 6.1 怎么跑

```powershell
# 1) 有客户端：直接跑，状态行会显示版本与「索引已加载」
./InputPlus.exe --everything-status

# 2) 没客户端但手上有 SDK 的 DLL：用环境变量指过去，验证绑定与降级
$env:INPUTPLUS_EVERYTHING_DLL='D:\tmp\esdk2\dll\Everything64.dll'
./InputPlus.exe --everything-status      # 期望：可用 True / 版本 0.0.0 / 客户端没在运行
./InputPlus.exe --qbar-bench             # 期望：后端列全 N（回落本地索引），数字不变

# 3) 同一台机器上做「Everything vs 本地索引」对照（回归红线 7 的实测法）
$env:INPUTPLUS_DISABLE_EVERYTHING='1'     # 或者取消设置里的「优先用 Everything 取候选」
./InputPlus.exe --qbar-bench              # 后端列全 N；log 里「一致 / 缺失 / 多出」列即差异
$env:INPUTPLUS_EVERYTHING_SORT='1'        # 可选 1..26，默认 14 = 修改时间降序
```

退出码：`--everything-status` 只在「DLL 可用且索引库已加载」时返回 0 —— 便于脚本判断。

### 6.2 自动化用例（SelfTest.QbarEverything.cs，25 项）

| 组 | 断言 |
| --- | --- |
| 查询串生成 | `doc: 报表` 含 `ext:` 与 `报表`；`-ext:tmp` → `!ext:tmp`；`size:>1mb` → `size:>=1mb`；`size:10mb..1gb` → 区间；`date:2026-07-01..2026-07-31` → `dm:` 区间；`folder:` 透传 |
| 转义 | `(` 被引号包住；`a|b` 不被当成 OR；短语整体加引号 |
| 不下发 | `wfn:*.pdf` 不出现在查询串里但仍留在 Spec；`regex:` 同理 |
| 复核 | `SpecFilter` 接受 pdf、拒绝 tmp、拒绝目录（带 `ext:` 时）；`-ext:tmp` 剔除 tmp 保留 txt；`wfn:*.pdf` 只收 pdf；size 下界 10 MB 命中、9 MB 剔除 |
| 状态 | `StatusText`/`VersionText` 非空且不抛；`Run(context,"doc:",5)` 的 `Backend ∈ {everything, native}` 且 `BackendDetail` 非空 |
| 默认管理员 | `new AppSettings().Global.RunAsAdmin == true` |
| 提权豁免 | `SelfTest.Ui.TestElevation` 遍历 `CliFlags.DiagnosticFlagsForTest`：每个诊断开关都被识别为诊断模式、不触发提权重启 |

### 6.3 复测数字（2026-11-21 08:48，带 SDK DLL、无客户端 → 强制走本地索引）

| 指标 | 预算 | 实测 | 结论 |
| --- | --- | --- | --- |
| 翻译层（2000 次平均） | ≤ 5000 us | **19.66 us/次** | 通过 |
| 20 条端到端中位 p95 | ≤ 50 ms | **32.9 ms**（中位最小/最大 0.0 / 43.7） | 通过 |
| 本地重排 2000 候选 | ≤ 50 ms | **中位 2.45 ms / 最大 6.00 ms** | 通过 |
| CJK 索引 | —— | **10480 条 / 62 ms / 2.89 MB** | 通过 |
| 全量自检 | 0 失败 | **6285 项 / 0 失败**（较上轮 +84 项：25 项 Everything + 提权豁免遍历 + 复核用例） | 通过 |
| 回归闸门 | 全部「一致」 | report/yh/reporter/说明 一致；rprt/yinhang/(/C:\Windows 一致（0 条） | 通过 |

单条最慢是 `"年度 总结"` 43.7 ms（0 命中，全盘扫一遍否定）、`wfn:*.pdf` 32.9、`说明` 30.6 ——
都仍在 50 ms 内。

### 6.4 真机实测（2026-11-21 09:02，Everything 1.4.1.1028 便携版 x64，管理员权限，索引约 115 万条）

安装：`https://ns1.voidtools.com/Everything-1.4.1.1028.x64.zip`（www.voidtools.com 只有 ~100 B/s，
ns1 镜像 ~46 KB/s，1.87 MB 直下成功）→ 解压到 `D:\tmp\everything\app\`，同目录写 `Everything.ini`
内容 `portable=1` → `everything.exe -startup`。以管理员运行时索引库才真正加载（非管理员时
`Everything.db` 只有 177 字节，任何查询都返回 0 条）。

| 命令 | 实测 |
| --- | --- |
| `--everything-status` | SDK 可用 True / 版本 1.4.1 / 索引已加载 True / Everything 以管理员运行 True；试查 `ext:exe` 20.4 ms、命中 5 条（总 9458，窗口截断），首条是本项目刚编出的 `InputPlus.exe` |
| `--qbar-query "doc: 报表"` | 后端 Everything；查询串 `ext:c;…;xml 报表 file:`；总 158658 / 窗口 600 / SDK 9–12 ms / 端到端 47.8–59.7 ms；首位 `D:\tmp\qbar-efu\报表-2026.pdf` |
| `--qbar-bench`（默认优先 Everything） | 翻译 **3.0 us/次**；端到端 p95 **54.5 ms**（中位最小/最大 8.3 / 55.3）；重排中位 **4.23 ms**、最大 **6.24 ms**；单查询 SDK 自身 9–43 ms |
| `INPUTPLUS_DISABLE_EVERYTHING=1 --qbar-bench` | 翻译 **1.6 us/次**；端到端 p95 **39.2 ms**；重排中位 **2.31 ms**；回归闸门**全部「一致」** |
| `--selftest`（Everything 在跑） | **通过 6286 项，失败 0 项** |

结论三条：
1. 端到端比预算多出的 4.5 ms 全在 `Everything_QueryW` 里（115 万条的子串扫描，一次 30–43 ms）；
   本地层（翻译 + 复核 + 重排）合计 ≤ 8 ms，仍在预算内。老机器上要稳过 50 ms，就把设置里
   「优先用 Everything 取候选」关掉，回到 39.2 ms 的本地索引路径。
2. 回归闸门在**本地后端**下全部「一致」；Everything 后端下 `report` 报「缺失 39/39」，
   原因是候选窗口截断（见 6.5），不是翻译或复核出错。
3. 真机跑出一个缺陷并已修：`-ext:tmp` 在按名字排序时窗口被**目录**填满（目录没有扩展名，
   复核阶段按 `dirsEligible` 全剔），表现为「零结果」。修法是 spec 带 `ext:`/`size:` 时
   给 Everything 查询追加 `file:`（与 `SpecFilter` 的目录分支同一条规则）；
   修后两种排序都是 600 候选 / 0 剔除。

### 6.5 已知边界（窗口截断 + 覆盖面差异，不是缺陷）

- Everything 只给「排序后的前 N 条」：`report` 总命中 2427、窗口 2000（按修改时间降序），
  本机那 39 个旧文件命中落在窗口外 —— 所以基准的回归列报「缺失 39/39」。用 `report.py` 复查能
  在 Everything 里查到 `C:\Users\64381\.dsh\…\report.py`，证明索引里有、只是没进窗口（D22）。
- 本地快照只覆盖用户选的根（本机 38.6 万文件 / 6.9 万目录），Everything 覆盖整机：
  两个后端的「全集」本来就不同，逐条相等只在本地后端下成立。
- `Everything_SetMax` 上限 20000；窗口从 2000 加大会让复核/重排线性变慢（2000 条约 4 ms），
  对 SDK 侧扫描成本几乎没有影响。

## 7. 命令标签、直接搜索命令、文件搜标签（2026-11-21 09:45 追加）

### 7.1 怎么跑

```powershell
# 全量自检（本轮数字：6327 项 / 0 失败）
$env:INPUTPLUS_DATA='D:\tmp\ipst61'; $env:CLP_SKIP_ELEVATE='1'
$env:INPUTPLUS_EVERYTHING_DLL='D:\tmp\esdk2\dll\Everything64.dll'
.\InputPlus.exe --selftest

# 只看设置页渲染（qbar 一页，含文件搜标签上/中/下三段）
$env:INPUTPLUS_RENDER_PAGES='qbar'
.\InputPlus.exe --render 'D:\tmp\rend6'
```

### 7.2 自动化用例

- `SelfTest.Qbar.cs::TestChips`（+6 条）：`ol` → `ChipKind.Command`、标签名用 Title、
  查询行 `Action="qcmd:user.ol"`、`Arguments="hello"`、副标题展开成 `-w hello`。
- `SelfTest.Qbar.cs::TestCommandSearchAction`（+9 条）：动作表里有 `qcmdsearch:user.ol` 且归「直接搜索」；
  内部命令（`builtin.newfile`）**不**注册；按 Id 执行、按关键字执行、文字只去首尾空白；
  成功后有「用「欧路词典查词」查「…」」反馈；未知 Id 给「找不到这条命令」且不执行；
  没有选区时只提示、不执行。
- `SelfTest.Qbar.cs::TestDirectSearch`（改 1 条）：「动作数 = 引擎数」现在只数 `search:` 前缀，
  自定义命令不再把这条断言顶红（D27）。
- `SelfTest.HoldDrag.cs`（+5 条，v1.7.0）：九宫格选边 —— 四角必须两轴同时命中、边上只有一个轴、
  正中间两轴都是 Center。这是"右键缩放只能单轴"那个 bug 的回归锁。
- i18n：「英文译文里没有残留汉字」重新变绿（补译 `D:\项目` 与 `yinhang → 银行` 两条示例）。

### 7.3 实测数字（Windows 11 / net10.0-windows Release / 本机）

| 项目 | 数字 |
|---|---|
| `--selftest` | **6327 项通过 / 0 失败**（本轮起点 6285） |
| 构建 | 0 警告 0 错误 |
| en-US 译文 | 4728 条（本轮新增 99） |
| 命令标签查询 | 单条结果行，无额外索引开销（不碰 Everything） |

### 7.4 手工验收

1. Qbar 输入 `ol` 按空格 → 变成「欧路词典查词」标签，输入 `hello` 回车 → 欧路词典打开并查 hello。
2. 设置 → 快捷键 → 任意键绑「直接搜索 → 命令：欧路词典查词」；在浏览器里选中一个词按该键 → 词典带词打开。
3. 把命令的 Path 删空 → 该命令从「直接搜索」下拉里消失（内部命令也不会出现）。
4. 设置 → Qbar → 文件搜：过滤器增删、优先级增删、非法正则被拒；切回「搜索指令」条目列表正常。
5. 按住拖拽：右键拖四角 → 宽高同时变；拖四边 → 只变一个方向。

## 8. 第六轮（设置表格化 / 命令 / 展开窗口）

### 8.1 本轮数字

| 项目 | 数字 |
|---|---|
| `dotnet build -c Release` | 0 警告 0 错误（13–15 s） |
| `--selftest` | **6334 项通过 / 0 失败**（上一轮 6328） |
| 离屏渲染 | `qbar-files-tab*.png` 四张 + `qbar-expand.png`，1280×860 下无破图 |
| `--qbar-query report` | 389 条候选 / 端到端 34.7 ms（翻译 2.7 / 检索 22.8 / 重排 8.1），命中行带日期 |

### 8.2 新增回归点

- `TestCommandStore`：内置 `ol` 存在、是内置动作、`Path` 留空、`Match("ol hello")` 取到 `hello`。
- `SelfTest.Qbar`：`Query("ol hello")` 里出现 `qcmd:builtin.dict` 行，文案含「网页词典」，参数原样。
- 渲染工具新增 `qbar-files-tab-cmd.png`（命令卡）与 `qbar-expand.png`（展开窗口）——
  这两块以前没有任何图，坏了也没人知道。

### 8.3 本轮踩到的坑（写给下一次改 XAML 的人）

1. **整文件 read + write 会把长文件截断**（XAML 700+ 行只读回约 45 KB）。批量插入一律用
   `old_string/new_string` 定位，改完立刻 `grep` 复核，不要 read-all/write-all。
2. **WPF 绑定只认 public 类型 + public 属性**：private 嵌套类、公开字段都会静默失败并退回
   `ToString()`，界面上表现成"整列都是类型名"。改完必须离屏渲染看图，单测覆盖不到这里。
3. **`RowItem` 是给 ListBox 的**：它的模板里只有一个 `ContentPresenter`，套到 `ListView` 上
   会绕过 GridView 的列。ListView 用 `RowItem.Grid`。
4. **索引快照里的元数据只在 `EmitFileHit` 里取**：自己 `new FileSearchHit(path, false, score)`
   会静默丢掉大小/日期，症状是"大小列为空 + 时间分面全在更早"。

### 8.4 发版验证（v1.6.0，2026-11-21 10:34）

| 项目 | 数字 |
|---|---|
| `dist/1.6.0/InputPlus.exe` | 6523539 字节 / 1.6.0.0 / sha256 `8D036BCB…` |
| 发布版 `--selftest` | **6334 项通过 / 0 失败** |
| 覆盖安装位置 | `D:\software\myself\InputPlus.exe` 同名同哈希；旧 1.5.0 备份在 `_deploy-snapshots\20261121-103417\` |
| 其余三形态 | x64 Win7 自包含 149580876 B / x86 Win10 6490771 B / x86 Win7 自包含 138742860 B |

### 8.5 发版验证（v1.6.1，2026-11-21 11:19）

| 项目 | 数字 |
|---|---|
| `dist/1.6.1/InputPlus.exe` | 6568595 字节 / 1.6.1.0 / sha256 `35AFF966BADF6C26…` |
| 发布版 `--selftest` | **6365 项通过 / 0 失败**（`INPUTPLUS_DATA=D:\tmp\ipst61`） |
| 覆盖安装位置 | `D:\software\myself\InputPlus.exe` 同名同哈希；旧 1.6.0 备份在 `_deploy-snapshots\20261121-111905\` |
| 其余三形态 | x64 Win7 自包含 149621836 B / x86 Win10 6535827 B / x86 Win7 自包含 138787916 B |

## 9. 展开窗口（Qbar 结果展开）的验收

| 编号 | 用例 | 结果 | 证据 |
|---|---|---|---|
| E1 | 左栏类型/时间分面计数与右栏行数一致 | 通过 | `qbar-expand.png`：全部 1967 = 文档 442 + 图片 115 + 文件夹 10 + 其余；时间桶合计 1967 |
| E2 | 名称/路径/修改日期/大小四列都有值 | 通过 | 同上，Report.dll 2.1 MB / 2026-07-08 |
| E3 | 点「大小」表头 → 按字节值降序，再点升序，第三次回相关度序 | 通过 | 自检 6 条 + `qbar-expand-sorted.png`（183 MB → 7.1 MB 递减，表头带 ▼） |
| E4 | 点「修改日期」→ 按天序号排，不受 "2026-1-9" 文本序影响 | 通过 | 自检「日期降序按天序号而不是格式化文本」 |
| E5 | 同值行不因重新筛选而换位 | 通过 | 自检「同值时保持原相关度序」 |
| E6 | 目录（大小 -1）在降序里不冒充最大、升序里不冒充最小 | 通过 | 自检「目录按 -1 排在小文件之后（降序）」 |
| E7 | Everything 没在运行时，列表只有一条可点击提示，不混入本地命中 | 通过 | `--qbar-query report` 在 `INPUTPLUS_DISABLE_EVERYTHING=1` 下后端显示"不可用，未回退本地快照"，无本地行 |

> E7 的历史坑：后端 `off` 时 `TryEnhance`（拼音/子序列）仍在读本地快照，于是界面一边写
> "不可用"、一边列出 24 条没有大小/日期的命中，分面全落到「更早」。修法是给增强路径加门：
> 只有 `Backend != "off"` 才跑。两个索引不同源，混着显示等于骗人。

## 10 2026-11-21 窗口两趟取法与谓词去向（补测）

| 项 | 数字 | 说明 |
| --- | --- | --- |
| 端到端 p95（Everything 在线，两趟取法） | 37.3ms / 58.1ms | 同一参数跑两次：机器负载不同；20 条里 17 条在 6–21ms |
| 端到端 p95（MatchPath 一直开，改造前行为） | 56.0–63.1ms | 与上一行的差就是"每候选多扫一遍路径"的成本 |
| 端到端 p95（MatchPath 一直关） | 34.7ms | 最快但按文件夹名搜不到，不能作为默认 |
| 本地重排（候选 ≤2000） | 中位 1.26ms / 最大 4.97ms | 预算 50ms |
| 翻译层 | 0.3–13.8us/次 | 预算 5000us；13.8us 的是 `doc:` 展开 49 项扩展名 |
| 正则（整条查询） | 1 条命中 / 端到端 69.8ms（SDK 42ms） | `regex:^rep.*\.docx$` → report-2026.docx |
| 自检 | 6382 项 / 0 失败 | `--selftest` |

### 10.1 手工验收（接第 9 节编号）

| 编号 | 用例 | 结果 | 证据 |
| --- | --- | --- | --- |
| E8 | `wfn:*.pdf` 能出结果（此前恒 0） | 通过 | `--qbar-query wfn:*.pdf` → 总 3764、窗口 600，后端串 `wfn:*.pdf` |
| E9 | `regex:^rep.*\.docx$` 只出符合正则的文件 | 通过 | 命中 1 条 report-2026.docx；后端串原样、无引号 |
| E10 | 正则 + 普通词不走 Everything 且说明原因 | 通过 | 后端显示「本地索引 · 正则查询由本地快照执行」 |
| E11 | `content:报表` 按文件名检索并标黄 | 通过 | 调试面板「content: 无内容索引，已按文件名检索」，串变成 `报表` |
| E12 | 稀疏查询补扫后结果不再"几乎为空" | 通过 | `rprt` 40.0ms / 24 条；不补扫时 0 条 |


## 11. 2026-11-21 12:45 五件事验收（图标 / 分面 / 查词 / 内置 Everything / 命令编辑）

| 编号 | 用户要的 | 判定方式 | 实测结果 |
| --- | --- | --- | --- |
| F1 | 展开窗口用资源管理器真图标 | 渲染图 `render-v19/qbar-expand.png` | docx 显示 Word 图标、py 显示 Python、svg 显示浏览器、ts 显示 TS，目录仍用文件夹字形 |
| F2 | 小窗也要看到全部时间桶 | 渲染图 `render-v19s/qbar-expand.png`（1180×600） | 全部/文件夹/文档/图片/视频/音频/其他 + 6 个时间桶全部可见，无滚动条 |
| F3 | 欧路查词 | `--dict hello` + 真机拉起 | 定位表命中 `D:\software\eudic\eudic.exe`（开始菜单快捷方式层）；`eudic.exe -w hello` 打开欧路并显示 hello；`--dict hello` 解析出 6 条释义 |
| F4 | ol 不打空格也能查 | 渲染图 `render-v20/qbar-dict-nolabel.png` | 输入 `ol hello` 不生成标签，直接出音标 + 释义行，徽标「复制」 |
| F5 | 内置 Everything 时删掉外部客户端 | `--everything-status` | `D:\tmp\everything` 已删；DB 63,930,995 B 迁到 `D:\software\myself\InputPlus Data\everything`；1.4.1 / DB True / 管理员 True / ext:exe 5 条（总 11222） |
| F6 | 内置命令可编辑 | 设置页命令卡 + 代码路径 | 程序/参数/工作目录/选择程序全开；内部动作填了程序即改为启动它；程序为空走内置实现 |

单测：`--selftest` **6388 项 / 0 失败**（新增 4 条：快捷方式与文件名能认出欧路、非 exe 路径不算、
找到的路径一定存在、解析不存在的 .lnk 返回 null 而不抛）。
构建：`dotnet build src/InputPlus/InputPlus.csproj -c Release --no-incremental` **0 警告 0 错误**。

## 12. 2026-11-21 13:10 过滤器触发 / 展开窗口布局与表头 / 内置 SDK 复核

| 编号 | 用户要的 | 判定方式 | 实测结果 |
| --- | --- | --- | --- |
| G1 | 过滤器改成「关键字 + 空格」触发 | 单测 + 渲染 `render-v23/qbar-filter-chip.png` | 打 `doc` 按空格即生成「文档 ×」chip 并立刻筛成文档；输入框剩「报表」，结果全是 pdf/doc/wps |
| G2 | 老写法不回归 | 单测 6 条 | `doc` / `doc:` / `DOC` 都解析到 `builtin.doc`；`doc 报表`、`report` 返回 null（不抢空格） |
| G3 | 筛选与最近修改各占一半 | 渲染 `render-v21s/qbar-expand.png`（1180×600） | 7 个类型桶 + 6 个时间桶全部可见，两段高度基本相等，无滚动条 |
| G4 | 列样式与全局 UI 统一 | 渲染 `render-v22/qbar-expand.png`（1280×860） | 表头透明底 + 下边框 + 11.5px 三级色 + 左对齐，「大小」右对齐；无系统灰渐变、无多余竖条 |
| G5 | 列宽不被路径挤坏 | 同上 | Name 280 / Path 480 / Date 118 / Size 84，路径过长走省略号，纵向滚动条不被压掉 |

内置 Everything SDK 复核（`--everything-status`，数据目录 = 安装位置的 `InputPlus Data`）：

   内置目录: D:\software\myself\InputPlus Data\everything
   内置客户端: 已释放
   SDK 可用: True
   DLL: ...\everything\Everything64.dll
   DLL 来源: 内置
   版本: 1.4.1 / 索引已加载: True / Everything 以管理员运行: True
   实测查询 ext:exe：成功，10.9 ms，5 条（总 11234）
   语义层 doc: → 命中 600 条（总 269997）· SDK 12 ms · 翻译 4.3 / 检索 19.5 / 重排 13.4 = 37.8 ms

单测：`--selftest` **6394 项 / 0 失败**（v1.7.1 的 6388 + 6 条过滤器触发断言）。

## 13 查词命令参数（v1.7.3）

| 编号 | 步骤 | 期望 |
| --- | --- | --- |
| H1 | 设置 → 文件搜索 → 命令 → 选中 ol | 参数框显示 `-w "{query}"`，程序框为空（并提示已检测到的欧路路径） |
| H2 | Qbar 输入 `ol hello` 回车 | 欧路词典打开并查 hello（不是只打开窗口） |
| H3 | 把参数改成 `-w "{query}" -p` 再查 | 原样展开，模板由用户说了算 |
| H4 | 用 1.7.2 的配置启动 1.7.3 | 参数框不是空的：加载时补成默认模板 |
| H5 | 把程序改成别的程序（如 notepad） | 走同一套 Execute：参数、工作目录、提权、静默都按命令卡生效 |

单测：`--selftest` **6397 项 / 0 失败**（6394 + 3 条查词参数断言：出厂值、模板展开、老配置迁移）。
发布产物 `dist/1.7.3/InputPlus.exe` 自检 **6390 项 / 0 失败**（少 8 条是剪贴板快照用例在「剪贴板被别的进程占用」时按设计跳过，日志里写明「（环境限制）剪贴板被占用，跳过快照验证」）。
构建：`dotnet publish -c Release -f net10.0-windows -r win-x64 --self-contained false` 0 警告 0 错误。


## 15 展开窗口外观 / 分面自适应 / 过滤器改名验收

| 编号 | 步骤 | 期望 |
| --- | --- | --- |
| I1 | 打开文件搜索窗口（任意结果） | 四角为圆角，标题栏与正文同一深浅 |
| I2 | 系统切到深色（或 `INPUTPLUS_RENDER_THEME=dark` 出图） | 标题栏、边框、正文一起变深，没有一条白杠 |
| I3 | 窗口缩到 860×520 | 7 个类型桶 + 6 个时间桶全部可见，不出现滚动条、底部无大片空白 |
| I4 | 设置里把内置「文档」关键字改成 WD，回到过滤器列表 | 仍然只有一条，显示 WD；旧 doc 不再命中 |
| I5 | Qbar 输入 WD + 空格 | 变 chip、WD 从输入框消失、结果只剩文档类 |
| I6 | 关掉 Qbar 再打开 | 空的输入框、没有过滤器 chip（不再记忆上次搜索） |
| I7 | Everything 已就绪时看索引卡 | 按钮是禁用的「Everything 已启动」，不是「启动 Everything」 |
| I8 | 点开内置命令 ol | 参数框里能看到 `-w "{query}"`，且程序/参数/工作目录都可编辑 |

**实测数字（2026-11-21 13:45，本机 Win11 28000 / 约 115 万条索引）**

| 项 | 值 |
| --- | --- |
| 构建 | `dotnet build src/InputPlus/InputPlus.csproj -c Release -f net10.0-windows` → 0 警告 0 错误（12.7s） |
| 自检 | `--selftest` → **6401 项 / 0 失败** |
| 展开窗口离屏出图 | render-v25 / v29，2480×1600 与 2360×1200，分面均分无裁切 |
| 展开窗口屏幕出图 | render-v26（浅色）、render-v28（深色），窗口矩形外扩 12px，圆角处可见桌面 |
| 深色标题栏 | `INPUTPLUS_RENDER_THEME=dark` 出图，标题栏 RGB 与正文同色阶（无白条） |

单测（`src\InputPlus\SelfTest.QbarSearch.cs`）新增 4 条：改内置关键字后仍是 6 条且 wd 仅 1 条、
旧 doc 不再命中且 WD 指向 IdDoc、`ResolveFilterTrigger("WD")` 命中同一条、用户自建的 `ol`
（Id 非内置）也会补上默认参数。

## 2026-11-21 13:48 发版 v1.7.4（本地，未上传）

- 版本号 1.7.3 → **1.7.4**（`src\InputPlus\InputPlus.csproj` 三处），`CHANGELOG.md` 顶部新增 v1.7.4 小节。
- `tools/release.ps1` 产出 `dist\1.7.4` 四形态（全部 1.7.4.0）：InputPlus.exe 9,046,675 B、
  x64-Win7-SelfContained 152,104,012 B、x86-Win10-Win11 9,013,907 B、x86-Win7-SelfContained 141,265,996 B。
  发布版 `--selftest` **6401 项 / 0 失败**。
- 已覆盖 `D:\software\myself\InputPlus.exe`（sha256 `c7316743…`，旧 1.7.3 备份在
  `D:\software\myself\_deploy-snapshots\20261121-134811\`），应用已重启（管理员，两个进程）。
  未 commit / push / tag，线上未发。
- **顺手修了状态探针的写死值**：`--everything-status` 里原来固定跑 `doc:`，而过滤器关键字是用户资产 ——
  本机用户已经把「文档」改成 `WD`，探针于是在人家的配置上显示「命中 0 条」，看着像后端坏了。
  改成「取第一个带扩展名的过滤器，用它的真实关键字探」。实测安装位置：`wd:` → 49 个扩展名、600 条、
  端到端 27.6ms（翻译 5.4 / 检索 14.3 / 重排 7.4）。
- 同一份数据目录里 `doc:` 现在确实不命中（`"doc:"` 当普通词搜、0 条）—— 这正是「改了关键字要同步」的
  正确结果，也是本轮 D46 的现场验证。
## 2026-11-21 14:40 按住拖拽缩放验收（J1–J6）

| 编号 | 场景 | 期望 | 实测 |
| --- | --- | --- | --- |
| J1 | 抓上边带、左右微动 30px（< 阈值 60） | 只变高 | 只变高，宽不变 |
| J2 | 继续沿该轴拖到 70px | 收养成 Right，仍是 400×300 | 收养成功、那一帧尺寸不跳 |
| J3 | 收养后继续拖到 (420,160) | 宽 460、高 244（两轴同变） | 一致 |
| J4 | 关掉 `ResizeBothAxes` | 完全退回九宫格（只变高） | 一致 |
| J5 | DragE2E 场景⑥：抓右边带斜拖 10 步 (+40,+100) | 宽 +40、高 +40（越过阈值的部分） | 420×320 → 460×360 |
| J6 | `--holddrag-config` 打印现场配置与九宫格演算 | 9 个抓点各自的选边结果可读 | 四角=两轴、边带=单轴、中心带=四向扩张 |

- 构建：`dotnet build src/InputPlus/InputPlus.csproj -c Release -f net10.0-windows` → 0 警告 0 错误。
- 自检：`--selftest` **6411 项 / 0 失败**（新增 6 条几何断言 + 1 个 E2E 场景）。

## 2026-11-21 14:52 过滤器动作验收（K1–K6）

| 编号 | 检查 | 结果 |
| --- | --- | --- |
| K1 | `qfilter:builtin.doc` 存在且分类是「Qbar 搜索」 | 通过（自检断言） |
| K2 | `fsearch:builtin.doc` 存在且分类是「直接搜索」 | 通过 |
| K3 | 关掉的过滤器不注册动作 | 通过（`user.off` 两个 Id 都取不到） |
| K4 | 执行 `qfilter:builtin.doc` 请求的是 `builtin.doc`、不带词 | 通过 |
| K5 | `fsearch` 取到的选区文字被 trim 后带进面板（`  报表  ` → `报表`） | 通过 |
| K6 | 过滤器不存在时给「找不到这个过滤器」提示，且吞掉这次按键 | 通过 |

- 自检：`--selftest` **6435 项 / 0 失败**。
- 出图：`INPUTPLUS_RENDER_PAGES=qbar` + `--render` 产出 `qbar-filter-action.png`（动作打开时 chip 已在框外、
  查询词已在框内），与 `qbar-filter-chip.png`（用户手打关键字那条路）并列留档。

## 2026-11-21 14:55 发版 v1.7.5（本地，未上传）

- 版本号 1.7.4 → **1.7.5**（`src\InputPlus\InputPlus.csproj` 三处），`CHANGELOG.md` 顶部新增 v1.7.5 小节。
- `tools/release.ps1` 产出 `dist\1.7.5` 四形态（全部 1.7.5.0）：InputPlus.exe 9,063,059 B、
  x64-Win7-SelfContained 152,120,396 B、x86-Win10-Win11 9,030,291 B、x86-Win7-SelfContained 141,282,380 B。
  发布版 `--selftest` **6436 项 / 0 失败**。
- 已覆盖 `D:\software\myself\InputPlus.exe`（sha256 `7e68edac…`，旧 1.7.4 →
  `_deploy-snapshots\20261121-145538\`），应用已以管理员重启（两个进程）。
  装好的 exe 跑 `--holddrag-config` 能看到「同时两轴 / 收养阈值」两行，`--everything-status` 仍是
  「DLL 来源: 内置 / 1.4.1 / 索引已加载 / 管理员」，`wd:` 命中 600 条、端到端 27.1ms。未 commit / push / tag，线上未发。
- 中途踩坑：应用是管理员进程，medium 权限的 `taskkill` 杀不掉，覆盖 exe 报 `Device or resource busy`；
  改成 `Start-Process taskkill -Verb RunAs -Wait` 才结束掉。

## 2026-11-21 15:02 v1.7.6 验收：查词动作与单一出口

| 项 | 命令 | 结果 |
| --- | --- | --- |
| 构建 | `dotnet build src/InputPlus/InputPlus.csproj -c Release -f net10.0-windows` | 0 警告 0 错误 |
| 自检 | `INPUTPLUS_DATA=D:/tmp/ipst76 CLP_SKIP_ELEVATE=1 INPUTPLUS_EVERYTHING_DLL=D:/tmp/esdk2/dll/Everything64.dll InputPlus.exe --selftest` | 6449 项 / 0 失败（EXIT=0） |
| 渲染 | `INPUTPLUS_RENDER_SIZE=1180x760 InputPlus.exe --render D:/tmp/render-v33` | EXIT=0，qbar-dict-action.png 1520×867 / 102001 B |

手工步骤（按用例编号）：

1. 选中一段带空格的中文 → 按绑给「直接搜索 / 词：欧路词典」的键：欧路若在，弹出并显示该词；把词改了名再按，行为不变。
2. 把欧路卸载或把命令卡里的 Path 清空 → 同一个键变成浏览器打开网页词典，提示语含「网页词典」。
3. 「Qbar 搜索 / Qbar：欧路词典」：选中 `hello` → 面板打开且 chip 为「查词 ×」、词已填好；接着打空格再打 `world` 变成两个词查。
4. 不选任何东西按这两个键 → 只出一条「先选中要查的词，或者输入要查的词再回车」，不启动任何进程。
5. 命令卡里把参数改成 `--word {query}` 指到别的词典 → 展开后确实是 `--word 词`，没被出厂模板覆盖。

- 已知限制：网页兜底依赖外网；欧路若装在非常规目录且未登记 App Paths / 开始菜单，仍会走网页（提示可见，不静默）。

## 2026-11-21 15:23 v1.7.7 验收：改名别名 / 停用语义 / 两个现场诊断开关

### 构建与自检

| 项 | 命令 | 结果 |
|---|---|---|
| 构建 | `dotnet build src/InputPlus/InputPlus.csproj -c Release -f net10.0-windows` | 0 警告 0 错误 |
| 自检（dev） | `INPUTPLUS_DATA=D:/tmp/ipst77 … --selftest` | 6462 项 / 0 失败（条数随环境热键/别名浮动） |
| 自检（发布版） | `INPUTPLUS_DATA=D:/tmp/ipst78 … dist/1.7.7/InputPlus.exe --selftest` | 6464 项 / 0 失败 |
| 自检（最终 dev 构建） | `INPUTPLUS_DATA=D:/tmp/ipst79 … --selftest` | 6465 项 / 0 失败 |

### 真实配置（D:\software\myself\InputPlus Data，关键字被改成拼音缩写）

| 查询 | 1.7.6（修前） | 1.7.7（修后） |
|---|---|---|
| `doc:` | `[degrade] unparsed predicate doc:` → 命中 0 | `filter wd -> 51 exts`，候选 600，端到端 47–165 ms |
| `doc: 报表` | 同上，命中 0 | ext 51 + 报表，9 条，50.7–199.8 ms（冷启动含 Everything 首查） |
| `file: report` | Everything 命中 1922，本地复核全剔 → 0 | 候选 1922，展示 40，103.2 ms |
| `wd: 报表` | 正常 | 正常，与 `doc: 报表` 同一结果集 |

### 命令诊断（`--qbar-command`，只解析不执行）

| 输入 | 结果 |
|---|---|
| `ol hello` | 命中 `builtin.dict`，尾词 `hello`，参数 `-w "hello"`，程序 `D:\software\eudic\eudic.exe` 存在 |
| `ol hello world` | 尾词 `hello world` → 参数 `-w "hello world"`（模板自带引号；不加引号就是多参数，符合文档） |
| `psh` | 命中内置 PowerShell，参数含 `cd -LiteralPath '<当前目录>'`，`powershell.exe` 按 PATH 解析到 System32 |
| `notepad x` | 未命中（首个词不是命令关键字），提示会按「打开选中项」处理 |

### 手工步骤

1. 运行 `InputPlus.exe --settings-dump`，确认打印的是安装位置的数据目录、`加密: 已启用 / 缓存解锁: True`、六个过滤器关键字与设置页一致。
2. 在 Qbar 里敲 `doc:`，chip 显示「文档」，结果只剩文档类扩展名；再敲 `wd:` 应得到同样结果。
3. 设置页把「文档」的开关关掉 → 回到 Qbar 敲 `doc:`：列表不再筛文档（整条忽略），Ctrl+D 调试面板的 resolveKeyword 行出现「已停用 -> 忽略」。
4. 重新打开开关，确认刷新后不会自己弹回来。
5. `InputPlus.exe --qbar-command "ol hello"`：确认参数展开为 `-w "hello"`、程序存在；再到 Qbar 里敲 `ol hello`，首行是命令动作行，下面仍是普通文件结果。

### 已知限制

- 别名只覆盖内置六条；自定义过滤器改名后没有别名。
- `--qbar-command` 的 `{current_folder}` 用进程当前目录演示；真实执行时取前台资源管理器目录，取不到就为空。
- 冷启动第一条查询的检索阶段可达 138–176 ms（Everything 首次查询 + 路径补扫），warm 后回到 20–33 ms；预算 p95 ≤ 50 ms 仍是 warm 口径。
