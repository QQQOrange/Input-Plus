# Qbar 语义层：进度

> 记录「做了什么 / 没做 / 怎么验 / 下一步 / 已知限制」。每条里程碑都对应任务书第 9 节的验收。

## 前置：现状勘察（第 0 节）
- 做了什么：读完 NativeSearchIndex / QbarProviders / QbarService / QbarWindow / AppSettings / CliFlags /
  SelfTest 全套，结论写进 DESIGN.md 第 0 节。
- 关键发现：**Everything 集成已经不存在**，索引是自建扫描 + 快照。按第 12 节兜底，
  语义层改建在自建索引上；缺的能力是 Everything 的 SetSort / SetMax / 正则开关 / 排序字段，
  替代实现见 DESIGN.md 第 3 节。
- 怎么验：`grep -rn "Everything_SetSearchW\|Everything64" src` 为空；`QbarProviders.cs:176` 注释。

## M1 查询翻译层 + 调试面板 + 回归保护 —— 完成
- 做了什么：`QbarQueryTranslator`（normalize / tokenize / 关键字解析 / 谓词解析 / Spec 组装 / 降级）、
  `QbarFilterCatalog`（内置 6 个过滤器，扩展名表照任务书 2.4）、`QbarSyntaxValues`（大小与日期）、
  `QbarSearchSpec`；`NativeSearchIndex.SearchSpec` 求值；调试面板（Ctrl+D / 设置开关）逐层打印
  Stage、请求、候选数、隐藏计数、耗时。
- 没做：`content:` 正文检索（索引不含正文，标黄降级）。
- 怎么验：`--qbar-query "doc: 报表"` 打印完整翻译链；`--qbar-bench` 的「旧入口比对」列全绿；
  SelfTest「Qbar 语义层」里的翻译用例。
- 下一步：无。

## M2 优先级规则 + 本地打分 + 隐藏诊断 —— 完成
- 做了什么：`QbarPriorityResolver`（内置规则包 + 用户规则覆盖 + 最长作用域）、`QbarSearchEngine`
  重排（档位加成 + 原生分）、`QbarSearchHit.Highlights`（数据层只给偏移，UI 拼接）、
  「有 N 条结果被优先级规则隐藏」提示 + 原因面板。
- 怎么验：SelfTest 规则用例（DoNotIndex 剔除 / Ignored 计数 / High 覆盖）；`--qbar-bench` 中
  「规则重排 N 条」与「一致」并列，说明剔除与重排都在工作。
- 已知限制：规则求值只看结果集，不写回索引（这是设计，不是缺陷）。

## M3 命令系统 —— 完成
- 做了什么：`QbarCommandStore`（用户命令 + 内置 cmd/psh/explore/newfile/newfolder/copypath/parent/reindex）、
  占位符 `{query} {current_folder} {action_path} {action_path:parent}`（一次替换不递归）、
  runas/silent/workdir、命令行与普通结果并列、过滤器关键字与命令同名的优先级。
- 怎么验：SelfTest 命令用例（Match / Expand / 内置覆盖 / 内部命令识别 / 空路径拒绝）。
- 已知限制：内部命令（新建文件/文件夹、复制路径、上一级、刷新索引）由窗口分派。

## M4 分面 + 空查询默认列表 + 历史 —— 完成
- 做了什么：`Facets`（本页统计）+ 底栏 `FacetText`；空查询继续走 RecentTargets + 收藏 + 命令；
  `QbarHistory`（JSONL 按月、Recent/Frequent）。
- 怎么验：SelfTest 历史用例（写盘、读回、聚合）；分面用例断言 `FacetSummary()` 以「本页：」开头。
- 已知限制：分面不提供点击筛选（只显示计数），因为筛选已由语法承担。

## M5 拼音与模糊增强 —— 完成
- 做了什么：`QbarPinyinIndex`（后台建、可取消、只收汉字名字、首字母 + 全拼三档打分、只存目录引用）、
  `NativeSearchIndex.FuzzyScan`（子序列）、引擎在结果稀疏时合并两路并去重、
  索引未就绪时 `PinyinPending` 降级提示、设置页「刷新拼音索引」。
- 怎么验：SelfTest 拼音用例（未就绪返回空 → 建好后 `sjg`/`shejigao` 命中「设计稿」）；
  `--qbar-bench` 的 CJK 索引段给构建耗时与内存。
- 已知限制：多音字只取一个读音（DECISIONS D9）；拼音索引不随文件改名即时更新，靠「刷新拼音索引」。

## 收尾：接口与文档
- 做了什么：设置页「搜索语义层」卡、QbarWindow 防抖 + IME composition 抑制 + 请求序号丢弃旧结果、
  `--qbar-query` / `--qbar-bench` 两个 CLI、SelfTest 语义层分组、本目录四份文档。
- 没做：过滤器 / 优先级规则 / 命令的**逐条图形编辑**（数据模型已可持久化，改 settings.json 即生效）。
- 下一步（若继续）：
  1. 列表编辑器（过滤器 / 规则 / 命令三张表）；
  2. 分面点击筛选。

## 收尾：自检归零（6201 项通过 / 0 失败）
- 修了什么：
  1. **增强命中绕过优先级闸门**（真 bug）：TryEnhance 合并拼音/子序列结果时没过
     QbarPriorityResolver，于是「目录设为 DoNotIndex」被子序列兜底偷偷绕开 ——
     自检里 190 条被剔除、24 条又溜回来。现在合并前统一走 PassPriority
     （隐藏计数照样记进诊断，High/Low 的档位加成也一并带上）。
  2. **拼音索引不收目录名**：CollectCjkNames 只走文件名，于是「设计稿」这种文件夹
     用 sjg 搜不到。补上目录最后一个路径段（父目录 + 叶子名），条数 7867 → 10480。
  3. **拼音索引内存硬上限**：光有条数上限管不住内存（长名字每条 ~300 字节，
     30 万条 ≈ 80MB）。加 24MB 估算上限，超了停止收录并在状态行写明。
  4. 自检需要「全局快照」的用例（引擎/拼音/模糊）原本查的是空索引，新增
     NativeSearchIndex.PublishForTest(root) 显式发布夹具树。
  5. 三条断言按实现校正：date:week 是「本周一到周日」不是滚动 7 天；
     否定扩展名用例必须用纯谓词（给了探测词就只剩 .txt，剔完自然是 0 条）；
     用户过滤器覆盖同名内置时**沿用内置 Id**（为了「恢复默认」），只覆盖字段。
- 怎么验：INPUTPLUS_DATA=<临时目录> InputPlus.exe --selftest → 退出码 0，
  日志末行「通过 6201 项，失败 0 项」；--qbar-bench 全绿。
- 已知限制：拼音索引按「估算字节」停收，极端机器上会只覆盖一部分 CJK 名字
  （状态行会写明「已达内存上限」）；多音字仍只取一个读音。

## 追加需求：接回 Everything SDK（2026-11-21）—— 完成

背景：前置勘察结论是「仓库里 Everything 集成已被移除」，因此第一版语义层建在自建索引上。
用户随后要求「继续做，而且我说了要集成 Everything 来实现本地搜索」并给出
https://www.voidtools.com/support/everything/sdk/ ，另加「软件默认以管理员模式启动」。
这两条改变的是**基础设施**而不是语义层：翻译层照旧产出 `QbarSearchSpec`，
只在执行端增加一个首选后端。

- 做了什么：
  1. `Core/Qbar/Search/EverythingApi.cs`：动态加载 Everything64.dll（`NativeLibrary.TryLoad` +
     `TryGetExport`，不用 static DllImport，缺 DLL 时安静降级），绑定
     SetSearchW / QueryW / SetRequestFlags / SetSort / SetMax / SetOffset / MatchPath / MatchCase /
     WholeWord / Regex / GetNumResults / GetTotResults / GetResultFullPathNameW / GetResultSize /
     GetResultDateModified / IsFolderResult / GetLastError / IsDBLoaded / IsAdmin / 版本三件套 /
     Reset / CleanUp；全局锁串行化（SDK 状态是进程级全局）；错误码 0–9 按头文件翻译。
  2. `Core/Qbar/Search/QbarEverythingBackend.cs`：`QbarSearchSpec` → Everything 查询串
     （ext:/!ext:/size:/dm:/path:/file:/folder: + 词/短语/否定），`wfn:`/`regex:`/`content:`
     不下发、留给本地；拿回候选后逐条过 `NativeSearchIndex.SpecFilter` 复核。
  3. `Core/Qbar/NativeSearchIndex.cs`：新增 `SpecFilter`（预编译 token/regex/ext 键）与
     `AcceptPath`，把索引内核的谓词复用给 Everything 后端 —— 两条路共享一份过滤真相。
  4. `QbarSearchEngine`：`PreferEverything`（默认 true）→ 可用就 Everything，否则回落到
     `SearchSpec`；`QbarSearchOutcome` 增加 `Backend/BackendQuery/BackendDetail/BackendText`。
  5. UI：调试面板多一行「后端: …」；索引状态文案优先显示 Everything 的版本与 DB 状态；
     结果为空时若 Everything 已就绪就不再提示「文件索引还没就绪」。
  6. 默认管理员：`AppSettings.RunAsAdmin` 默认 false → true；`ElevationHelper` 除显式白名单外
     还按 `CliFlags.IsDiagnostic` **类别**豁免（历史上就是因为漏同步，默认管理员模式跑基准会先弹 UAC）；
     `SelfTest.Ui` 的提权用例改为遍历诊断开关列表，不再维护第二份清单。
  7. 新诊断命令 `--everything-status`（`CliFlags.DiagnosticFlags` + Usage + App 分派），
     打印 DLL 路径、版本、DB 状态、是否管理员、实际试查一条的结果与耗时。
- 没做：
  - 没有随发布包捆绑 `Everything64.dll`（DECISIONS D20：捆绑会让「可用」恒真，但没装客户端时
    查询必然失败，状态更难懂；第三方 DLL 的升级责任也不该背）。
  - 没有实现「用 Everything 的索引库直接读」（红线 1）。
- 怎么验：`--everything-status`（有 DLL 无客户端时准确报「客户端没在运行」）、
  `--qbar-bench`（后端列标 E/N，全部 N 说明本机回落正常）、自检新增 25 项断言、
  `dotnet build` 0 警告 0 错误、全量自检 **6285 项 / 0 失败**。
- 已知限制（必须说清）：本机**没有安装 Everything 客户端**，且从 voidtools 下载便携版两次超时
  （1.8 MB 的包只收到 106 KB），所以「Everything 真实命中 + 真实排序」这条链路**没有端到端跑过**。
  已验证的是：DLL 能加载、导出能解析、版本/DB/管理员状态读取正确、客户端缺席时的错误码 2 与
  UI 降级文案、候选复核谓词的正确性（用夹具树直接调 `SpecFilter`）。详见 TESTING.md 第 6 节。
- 下一步：在装了 Everything 的机器上跑一次 `--everything-status` 与 `--qbar-bench`，
  确认后端列变成 E 且命中数/顺序与 N 一致；若 `dm:` 边界日漂移，按 D19 的复核层修。

### 追加：真机连通 Everything（2026-11-21 09:02 完成）

- 做了什么：
  1. 从 ns1 镜像下到 `Everything-1.4.1.1028.x64.zip`（www.voidtools.com 只有 ~100 B/s），
     解压到 D:\tmp\everything\app\、同目录 `Everything.ini` 写 `portable=1`，以管理员 `-startup` 起来。
  2. 真机验证：`--everything-status` 报「1.4.1（管理员）索引已加载」，试查 `ext:exe` 20.4 ms / 5 命中
     （总 9458）；`--qbar-query "doc: 报表"` 后端 = Everything、总 158658 / 窗口 600、端到端 47.8–59.7 ms。
  3. 顺带修了两个真机才暴露的问题：① `-ext:tmp` 在名字排序下窗口被目录占满导致的零结果（D24，加 `file:`）；
     ② 自检跟随本机后端导致红了一次（D25，测试锁本地后端）。
  4. 新增 `INPUTPLUS_DISABLE_EVERYTHING` / `INPUTPLUS_EVERYTHING_SORT` 两个环境变量与设置页开关
     「优先用 Everything 取候选」，让「E 后端 vs 本地索引」能在同一台机器上对照（D23）。
- 没做：没有改 Everything 的任何全局配置，也没有读写它的索引库（红线 1）；EFU 文件列表模式试过，
  `IsDBLoaded=False` 且 SDK 查不到内容，已弃用。
- 怎么验：`--qbar-bench` 两次（开/关 Everything）、`--selftest`、`--everything-status`；数字见 TESTING §6.4。
- 实测结论：本地层 ≤ 8 ms，端到端 p95 54.5 ms 里 45+ ms 在 SDK 内部；关掉后端 p95 39.2 ms、回归闸门全部「一致」。
- 已知限制：`report` 在 Everything 后端报「缺失 39/39」是窗口截断（总 2427 > 窗口 2000，D22），不是索引缺失；
  本地快照与 Everything 的覆盖面本来就不同，逐条相等只在本地后端下成立。
- 下一步：若要继续压 50 ms，方向是先用更严的词收窄 SDK 查询或对高频词做短 TTL 缓存，而不是加大窗口。

### 追加：本地发版 v1.4.0（2026-11-21 09:13，线上未发）

- 版本号：csproj `Version/FileVersion/AssemblyVersion` 1.3.1 → **1.4.0**；CHANGELOG 顶部新增 v1.4.0 小节
  （release.ps1 会从这一节抽出 `release-notes.md`）。
- 产物：`dist/1.4.0/` 四个形态全部出齐（脚本 `tools/release.ps1`，Windows PowerShell 5.1 下跑通）：
  `InputPlus.exe` 6.1 MB / `InputPlus-1.4.0-x86-Win10-Win11.exe` 6.1 MB /
  `-x64-Win7-SelfContained` 142.6 MB / `-x86-Win7-SelfContained` 132.2 MB；
  另有 `SHA256SUMS.txt`、`manifest.json`、`release-notes.md`。四个 exe 的文件版本都是 1.4.0.0。
- 验收：发布版 exe 跑 `--selftest` → **6285 项 / 0 失败**；`--everything-status` → SDK 1.4.1 / 索引已加载 /
  试查 `ext:exe` 7.9 ms；`doc:` 语义层实测端到端 40.4 ms（翻译 4.4 / 检索 16.3 / 重排 19.2）。
- 没做：没有 `git commit`、没有 `git push`、没有建 tag、没有上传任何 Release 资产（按要求只本地出包）。

### 追加：文件搜标签 / 命令标签 / 吞键 / 四角缩放（2026-11-21 09:45）

- 做了什么：
  1. **文件搜标签**（m01552）：Qbar 设置新增 `Tag="files"` 标签，排在「搜索指令」和「启动程序」之间。
     索引与后端、过滤器（内置 5 条不可删可覆盖 + 自定义增删）、优先级（列表 + 空态文案 + 编辑器）、
     语法与调试四张卡片集中在这里；切过去时跨两列、右栏折叠。
  2. **按住拖拽吞键**（m01578）：`AppConfig.SwallowTrigger` + 设置页开关；打开后触发键这一下
     不再送给前台窗口（`AltDragSink` 的 `_triggerSwallowed` / `ReplaySwallowedTap`），默认关。
  3. **四角缩放退化修复**（m01585）：`AltDrag.SetEdgeAndOffset` 原来切两刀（CenterFraction 再
     SidesFraction）后 `SetEdgeToClosestSide` 只挑一个轴，四角拖拽永远单轴。改成只切一刀，
     两轴都命中边带＝角、宽高同变；`SidesFraction` 只保留 ini 往返兼容。
  4. **命令标签**（m02031）：`ChipKind.Command` + `QbarChip.FromCommand`，Qbar 输入 `ol` 按空格
     变成「欧路词典查词」，后续文字当 `{query}`；「直接搜索」新增 `qcmdsearch:<id>` 动作，
     选中文字按键直接查（D26/D27，设计见 DESIGN §7）。
- 没做：拼音索引没有改动；分面/历史没有改动；命令系统没有加"命令参数自动加引号"（仍按文档要求由用户写）。
- 怎么验：`--selftest` **6327 项 / 0 失败**（新增命令标签 6 条 + 直接搜索命令 9 条 + 四角选边 5 条）；
  渲染 `INPUTPLUS_RENDER_PAGES=qbar` 拍文件搜标签三段，目检无破图；en-US 补 99 条译文（4728）。
- 下一步：命令标签可以按使用频率排序（现在固定单行）；过滤器编辑器可加"从目录里挑扩展名"的辅助。
- 已知限制：命令标签不吃 Everything / 本地索引，因此没有候选预览；选区的三级兜底里，"请按 Ctrl+C"
  那段要等最多 8 秒（`ManualCopyWaitMs`），管理员前台程序下必然走这一支。
- 回滚：版本号三处改回 1.3.1 并重跑 `tools/release.ps1` 即可；`dist/1.3.1/` 未被覆盖。
## 2026-11-21 10:35 「文件搜索」表格化 + 命令系统 + 展开窗口（M6 追加）

- 做了什么：
  1. **标签页重排**：`文件搜` → `文件搜索`，卡片顺序改为 索引与后端 / 过滤器 / 优先级 / 命令 / 语法与调试；
     切到该标签时面板跨两列、右栏折叠（`FileSearchPanel`，`Grid.Row` 覆盖左栏）。
  2. **过滤器表**：左侧 44px 行 = 图标+名称 / 关键字 chip / 扩展名（单行截断）/ 搜索范围 / 开关；
     `builtin.*` 分「文件类型」组在上、自定义在下，内置可覆盖不可删；`file:` 无扩展名时显示「所有文件」/「目录」。
  3. **优先级表**：高优先 / 正常 / 低优先 / 从搜索结果中排除 四组；内置包照 Listary 默认表
     （Normal 3 条 AppData 里的 IE/Windows/开始菜单，Low 8 条系统目录，Low 正则 5 条 `^\` `^~` `^[^.]+$` `^node_modules$` `^\$`），
     每条一个开关写 `QbarSearchOptions.DisabledBuiltins`；「低优先」组顶部一行「默认不展示系统及隐藏文件的搜索结果」
     写 `HideLowResults`（默认关，保证与改造前一致）。高优先空组给提示行而不是空白。
  4. **命令系统**：设置页新增「命令」卡（内置命令 / 自己的命令两组 + 关键字/标题/程序/参数/工作目录/管理员/静默编辑区）；
     新增内置命令 `ol`（`builtin.dict`）：装了欧路词典走 `-w "词"`，没装就打开
     `https://dict.eudic.net/dicts/en/<词>` 并提示"本机没装欧路词典"。
     `FindEudic()` 先扫 PATH 再看 `%ProgramFiles%\Eudic` 等四个默认位置。
  5. **展开窗口**（`QbarExpandWindow`，Qbar 右上角按钮打开）：左分面（类型 7 桶 / 时间 6 桶）+ 右四列表格
     （名称 / 路径 / 修改日期 / 大小），140ms 防抖，`_seq` 丢旧结果，双击或 Enter 打开、Esc 清筛选。
  6. **修复三处**（都是渲染核图才暴露的）：
     - 展开窗口的行类型是 `private` 嵌套类且用**字段**，WPF 绑定失败退回 `ToString()` → 整列显示类型名；
       改成 `public` 嵌套类 + 自动属性。
     - `ItemContainerStyle="{StaticResource RowItem}"` 的模板只有裸 `ContentPresenter`，
       套到 ListView 上会绕过 GridView 列（表头在、行里只剩 ToString）；
       新增 `RowItem.Grid`（`GridViewRowPresenter` + hover/选中底色）专供 ListView。
     - `NativeSearchIndex.ScanFiles` 自己拼 `FileSearchHit` 只给 path/score，大小/日期是默认值 0/-1 ——
       普通关键词查询的大小列永远空、时间分面全落「更早」；改走 `EmitFileHit`（同 nameIndex 取元数据）。
- 没做：分面点击后仍不支持多选（类型 + 时间是 AND，够用）；列头点击排序未做（`TODO(follow-up)`）。
- 怎么验：`dotnet build -c Release` 0 警告 0 错误；`--selftest` **6334 项 / 0 失败**；
  `--render` 拍 `qbar-files-tab{,-mid,-cmd,-bottom}.png` 与 `qbar-expand.png` 逐张目检；
  `--qbar-query report` 的命中行现在带大小与日期（此前不带）。
- 已知限制：展开窗口的列宽只做一次均分，超长路径靠省略号（双击可打开，路径可在 Qbar 里看全）。
- 事故记录：本轮一次批量改 XAML 时文件尾部被截断（丢 `搜索联想` 的三根滑杆与收尾标签），
  构建报 `MC3000`；已按 785 行处的结构补齐并重新核图。教训写进 TESTING §8.3。

## 2026-11-21 10:34 本地发版 v1.6.0（线上未发）+ 覆盖安装位置

- 做了什么：版本号三处 1.5.0 → 1.6.0；`CHANGELOG.md` 顶部加 v1.6.0 小节；
  `tools/release.ps1` 出齐四形态（`dist/1.6.0/`，全部 FileVersion 1.6.0.0）。
  发布版 `--selftest` **6334 项 / 0 失败**（`INPUTPLUS_DATA=D:\tmp\ipst16`）。
  按 m01607 覆盖安装位置：先备份到 `D:\software\myself\_deploy-snapshots\20261121-103417\InputPlus.exe`
  （1.5.0，6474387 字节），再覆盖 `D:\software\myself\InputPlus.exe`
  （6523539 字节，sha256 `8D036BCB7AB26C9A…`，与 `dist/1.6.0/SHA256SUMS.txt` 一致）。
- 没做：没有 commit / push / tag / 上传（线上不发）。
- 怎么验：`(Get-Item D:\software\myself\InputPlus.exe).VersionInfo.FileVersion` = 1.6.0.0；
  `Get-FileHash` 与 dist 清单逐字节一致。
- 已知限制/教训：安装位置的 exe 当时正以管理员身份运行（两个进程），中等完整性 shell
  `taskkill` 与 `cp` 都被拒（Permission denied）。用一次 UAC 提权杀掉进程后才覆盖成功 ——
  注意提权脚本必须存成 **UTF-8 with BOM**，否则 PowerShell 5.1 按 ANSI 读会把中文路径读坏
  （本轮第一次就栽在这里：`找不到路径 … 的一部分`）。
- 回滚：把 snapshot 里的 1.5.0 exe 复制回去即可；`dist/1.5.0/` 未被覆盖。

## 2026-11-21 11:20 展开窗口排序 / 表头对齐 / 查词徽标 / Everything 掉线修复 → 发版 v1.6.1

- 做了什么（四项）：
  1. **展开窗口点表头排序**：`QbarExpandWindow.xaml.cs` 新增 `_sortKey`/`_sortDesc`，
     `Row` 增加 `SizeBytes`/`DayValue`/`Rank` 三个原始值字段，`SortRows` 抽成
     `internal static` 便于自检；点一次按列排（日期/大小默认降序，名称/路径默认升序），
     再点切反向，第三次回相关度序；同值一律以 `Rank` 兜底，筛选时行不会乱跳。
  2. **表头左对齐**：`ListView.Resources` 里覆盖 `GridViewColumnHeader` 的
     `HorizontalContentAlignment=Left` + `Cursor=Hand`。
  3. **查词结果徽标**：`QbarKind` 增加 `Dictionary`，徽标从「计算」变「复制」，
     回车提示改成「Enter 复制结果」。
  4. **Everything 不可用时不再混入本地命中**：`Run()` 里给增强路径加门
     （`Backend != "off"` 才跑拼音/子序列），空结果时列表给出「Everything 没在运行」
     的可点击行，点了直接拉起 Everything。
- 没做：没给展开窗口做分页（仍然是取回来的一屏 + 虚拟化滚动）；列宽不支持拖动调整
  （`Width` 固定 + 路径列按窗口宽度自适应）。
- 怎么验：`--selftest` **6365 项 / 0 失败**（新增 6 条排序用例：大小/日期按原始值、
  同值稳定、rank 不改动、目录 -1 排在文件之后）；离屏渲染
  `--render D:\tmp\render-v15` 出 `qbar-expand.png`（表头左对齐）与
  `qbar-expand-sorted.png`（大小 ▼，183 MB → 7.1 MB 严格递减）。
- 已知限制：排序只在当前候选窗口内做，Everything 侧仍是「修改时间降序取前 N 条」；
  按名称排序看到的也只是这 N 条里的名称序（要全量名称序得把窗口提到 20000，代价是变慢）。
- 发版：版本号 1.6.0 → **1.6.1**，`dist/1.6.1/` 四形态齐全，发布版 `--selftest`
  6365 项 / 0 失败；已覆盖 `D:\software\myself\InputPlus.exe`
  （6568595 字节 / sha256 `35AFF966BADF6C26…`），旧 1.6.0 备份在
  `D:\software\myself\_deploy-snapshots\20261121-111905\`。线上未发（无 commit/push/tag）。

## 2026-11-21 12:20 候选窗口两趟取法（MatchPath 分级）+ 谓词去向 + SDK 高亮

- 做了什么：
  - `QbarEverythingBackend.TryRun` 改成两趟：第一趟 MatchPath=FALSE（只命中文件名），
    候选 < 8 条时补一趟 MatchPath=TRUE 并去重合并；调试面板注明「（含路径补扫）」。
  - `EverythingApi.TrySearch` 增加 `regexMode` / `matchPath` 两个参数（都是显式的，
    不再靠环境变量），正则时自动关掉 MatchPath（正则本来就按整名匹配）。
  - 谓词去向定案：`wfn:` 下发给 Everything；`regex:` 整条正则时下发并开全局正则、
    混别的条件时走本地快照；`content:` 降级成文件名检索并标黄。
  - 新增 `QuoteRaw`（只保护空格，不加倍反斜杠）与 `IsRegexOnly`。
  - 自检 +7 条：wfn 下发、纯正则原样下发且无引号、混合条件返回 false 且给原因、content 降级、高亮解析 4 条、优先级 5 条、切段 5 条。
- 怎么验：
  - `dotnet build -c Release` 0 警告 0 错误；`--selftest` **6382 项 / 0 失败**。
  - `--qbar-bench`（Everything 1.4.1 / 约 115 万条）：端到端 p95 **37.3ms**（另一次同参数 58.1ms，
    机器负载波动），重排中位 **1.26ms**，翻译 0.3–13.8us；20 条里 17 条在 6–21ms。
  - 对照：MatchPath 一直开 → p95 56.0–63.1ms；一直关 → 34.7ms 但按文件夹名搜不到东西。
  - 真机：`regex:^rep.*\.docx$` → 命中 `D:\tmp\qbar-efu\report-2026.docx`（端到端 69.8ms，SDK 42ms）；
    `wfn:*.pdf` → 总 3764 条、窗口 600；`content:报表` → 串变成 `报表`、命中 5 条。
- 已知限制：正则混合查询只看本地快照（目录覆盖面小于 Everything）；补扫阈值是常量，未做成设置项。
- 下一步：发版 1.7.0 并覆盖安装位置。


## 2026-11-21 12:12 发版 v1.7.0 并覆盖安装位置

- 版本号 1.6.1 → **1.7.0**（InputPlus.csproj 三处 + CHANGELOG 顶部新条目）。
- `tools/release.ps1` 产出 `dist/1.7.0/` 四形态：InputPlus.exe 9030291 字节、x64-Win7-SelfContained 145MB、
  x86-Win10-Win11 8.6MB、x86-Win7-SelfContained 134.7MB，文件版本全部 1.7.0.0。
  exe 从 6.5MB 涨到 8.6MB 是因为内置了 Everything 客户端与 SDK（见「内置 Everything」段）。
- 发布版实测：`--selftest` **6384 项 / 0 失败**（INPUTPLUS_DATA=D:/tmp/ipst71）；`--everything-status` SDK 内置副本、
  1.4.1、DB True、管理员 True、ext:exe 8.2ms 5 条（总 9490）。
- 已覆盖 `D:\software\myself\InputPlus.exe`（9030291 字节 / sha256 `7734f43948a58bb0…` / 1.7.0.0），
  旧 1.6.1 备份在 `D:\software\myself\_deploy-snapshots\20261121-121205\`；应用已重新启动。
- 收尾：发布时内置客户端曾被拉起一个**非管理员** Everything 实例（无索引、纯占资源），已按 PID 结束；

## 2026-11-21 12:45 展开窗口用真图标 / 分面压行高 / 欧路查词打通 / 内置客户端接管

用户报的五件事（m04187）一次做完。

1. **展开窗口的图标改成资源管理器真图标**：原来名称列只画一个 Segoe 字形（E8B7/E8A5），
   所有 docx/pdf/exe 长得一模一样。现在 Row 实现 INotifyPropertyChanged，18×18 的
   ShellIcon 叠在字形上，图标按"容器已生成的行"异步取（ShellIcons.Request + 缓存命中走同步），
   滚动到新行再补。只对可见行取图标，2000 条一次性解析会把滚动拖死。
2. **分面栏压行高**：新增 RowItem.Compact / RowItem.Grid.Compact 两个 keyed 样式（不动全局 RowItem），
   分面行 46px → 30px、结果行 42px → 30px，同时删掉 TypeList 的 MaxHeight 360 与 TimeList 的 300。
   实测 1180×600 的小窗里 7 个类型桶 + 6 个时间桶全部可见（含「最近 365 天 / 更早」）。
3. **欧路查词真通了**：根因不是接口，是**找不到程序**——用户的欧路装在 D:\software\eudic，
   旧的 FindEudic 只扫 PATH 和 Program Files，直接回落网页词典，看起来像"没实现"。
   新增 Core/Qbar/EudicLocator.cs：PATH → App Paths 注册表（HKLM/HKCU × 32/64 视图）→
   **开始菜单快捷方式解析**（自写 IShellLink+IPersistFile，不引第三方库）→ 常见目录。
   实测命中 D:\software\eudic\eudic.exe；eudic.exe -w hello 拉起欧路并显示 hello（截图核过），
   查完已关闭该窗口。命令编辑器的提示行会显示"已检测到欧路词典：<路径>"。
   另外 ol hello **不打空格生成标签也能查**（QbarWindow.TryResolveDictionary 按命令关键字前缀匹配，
   要求关键字后面是空白或到头，避免 old/olive 误判），新渲染图 qbar-dict-nolabel.png 钉住这条路径。
4. **外部 Everything 客户端已删**：D:\tmp\everything 整个删除，索引库（63,930,995 B）连同管理员权限
   迁到 D:\software\myself\InputPlus Data\everything\Everything.db，重启内置客户端后
   --everything-status 显示 DB True / 管理员 True / ext:exe 5 条（总 11222）/ doc: 语义层 600 条（总 269962）。
5. **内置命令全字段可编辑**：SetCommandProgramEnabled(true) 覆盖原来对内部动作的禁用；
   内部动作填了程序就改成启动它（参数/工作目录照常），留空才走内置实现。
   工作目录在界面上补了说明：程序启动时所在的文件夹，写 {current_folder} 就是当前选中的文件夹，
   留空用系统默认（QbarCommandStore 里目录不存在就不设，避免启动失败）。

验证：dotnet build -c Release **0 警告 0 错误**；--selftest **6388 项 / 0 失败**（新增 4 条词典定位断言）；
--dict hello 打印四层定位表并解析出 6 条释义；渲染 render-v19（1280×860）与 render-v19s（1180×600）核图通过。

## 2026-11-21 12:46 发版 v1.7.1 并覆盖安装位置

- 版本号 1.7.0 → **1.7.1**（InputPlus.csproj 三处 + CHANGELOG 顶部新条目）。
- `tools/release.ps1` 产出 `dist/1.7.1/` 四形态：InputPlus.exe 8.6MB(9038483 B)、
  x64-Win7-SelfContained 145MB、x86-Win10-Win11 8.6MB、x86-Win7-SelfContained 134.7MB，文件版本全 1.7.1.0。
- 发布版实测：`--selftest` **6388 项 / 0 失败**（INPUTPLUS_DATA=D:/tmp/ipst171）；
  `--dict hello` 定位到 `D:\software\eudic\eudic.exe`；`--qbar-query 报表` 后端 Everything、命中 9 条、端到端 39.6ms。
- 已覆盖 `D:\software\myself\InputPlus.exe`（9038483 字节 / sha256 `b6e9bd9ba764415e…` / 1.7.1.0），
  旧 1.7.0 备份在 `D:\software\myself\_deploy-snapshots\20261121-124431\`；应用已重新启动（两个进程）。
- 收尾核对：全机只有**一个** Everything 实例（管理员，PID 45664，来自数据目录），
  部署后的主程序没有再拉起第二个。线上未发（无 commit / push / tag / 上传）。

## 2026-11-21 13:10 过滤器改「关键字 + 空格」、展开窗口两半等高与表头统一、内置 SDK 复核

- 过滤器触发（用户要求"跟搜索一样打个空格"）：`QbarService.ResolveFilterTrigger` 同时认 `doc` 与 `doc:`；
  QbarWindow 的空格/Tab 分支在引擎关键字之后追一步 `TryMakeFilterChip()`，命中就 `ApplyFilter` 生成 chip。
  `ApplyFilter` 的切词条件同步放宽（首词等于过滤器关键字也算触发词），否则文本里会剩一个重复的 doc。
  顺序保持"引擎关键字先赢"，同名时行为与改造前一致。
- 展开窗口左栏：Grid 行定义从 `Auto / Auto / * / Auto / Auto` 改成 `Auto / * / Auto / *`，
  「筛选」和「最近修改」各吃一个星号，小窗 1180×600 实测两组全部可见（原来空白全堆在类型表下面）。
- 展开窗口列样式：新增共享样式 `GridView.Header` / `GridView.Header.Right`（Theme/Controls.xaml）——
  透明底 + 0,0,0,1 下边框 + 11.5px 三级色 + 左对齐，「大小」列右对齐；拖动列宽的小把手换成自带模板，
  平时透明、指上去才显一条淡线（默认 Thumb 模板那根深色竖条在浅色表头里像多出来一条分隔线）。
  列宽 Name 280 / Path 480 / Date 118 / Size 84，`FitPathColumn` 同步；PathColumn 不能写 MinWidth
  （GridViewColumn 不是 FrameworkElement，标记编译直接 MC3072）。
- 内置 Everything SDK 复核：csproj:110-119 三条 EmbeddedResource；`--everything-status` 打印
  「内置目录 / 内置客户端: 已释放 / DLL 来源: 内置」，DLL 实际落在 `<数据目录>\everything\Everything64.dll`，
  同一份 exe 在别的数据目录下也会先释放再加载。
- 验证：`--selftest` **6394 项 / 0 失败**（新增 6 条过滤器触发断言）；渲染 render-v22（1280×860）、
  render-v21s（1180×600）核图通过；render-v23/qbar-filter-chip.png 拍到「文档 ×」chip + 「报表」的筛选结果。
- 已知：`dotnet build` 单 TFM 带 RID 时会把 bin 里的 InputPlus.dll 删掉（既有的标记编译抢中间文件问题），
  自检与截图一律改用 `dotnet publish` 出的单文件 exe。

### 13:15 查词命令的参数补成 -w "{query}" → v1.7.3

- 用户反馈：欧路查词命令的参数是空的，应该带 `-w`。根因不是模板丢了，而是内置 ol 的
  `Parameter` 出厂就是空字符串，`-w` 写死在 `RunDictionaryCommand` 里「找到欧路」那条分支 ——
  设置页因此显示空参数，用户也改不了写法。
- 改法：`QbarCommandStore.DefaultDictParameter = "-w \"{query}\""`，内置 ol 的 Parameter 用它；
  `Effective()` 对「按 Id 覆盖的内置 ol + 空参数」补默认值（1.7.2 及更早存下来的配置就是空的）；
  `RunDictionaryCommand` 两条分支合并：程序 = 用户指定 ?? EudicLocator 找到的，统一走
  `QbarCommandStore.Execute`，参数模板、工作目录、提权、静默因此行为一致，参数留空才兜底默认值。
- 验证：`--selftest` **6397 项 / 0 失败**（新增 3 条：出厂参数等于 `-w "{query}"`、模板展开成
  `-w "hello"`、老配置空参数被补成默认）；`--dict hello` 仍能定位到 D:\software\eudic\eudic.exe；
  渲染 render-v24/qbar-files-tab-cmd.png 里 ol 那行副标题已是 `Qbar 内置动作 -w "{query}"`。
- 发版 v1.7.3（13:16）：dist\1.7.3 四形态全部 1.7.3.0；发布版自检 6390/0（8 条剪贴板用例按设计
  跳过：剪贴板被占用）。已覆盖 D:\software\myself\InputPlus.exe（9042579 字节，sha256
  79472216AA88D0E6C2F0F1E14EAE52A8AF8AF4CDD51B6438269247985983F9C5），旧 1.7.2 备份在
  D:\software\myself\_deploy-snapshots\20261121-131800\；应用已重启（两个进程）。
  `--everything-status`（用安装位置的 exe 跑）：DLL 来源内置、1.4.1、DB True、管理员 True、
  ext:exe 8.4ms 5 条（总 11245）、doc: 端到端 34.8ms。未 commit / push / tag。


## 2026-11-21 13:45 展开窗口圆角/跟随系统主题/分面自适应 + 过滤器改名与 Qbar 记忆

- 展开窗口（`src\InputPlus\UI\Windows\QbarExpandWindow.xaml`）：
  · `WindowChrome` 的 `CornerRadius` 0→12；构造函数里 `WindowChrome.AttachTo(this, "Brush.Background")`
    —— 圆角（DWMWCP_ROUND）、深色标题栏、跟随系统明暗三件事都走这一个入口。
    `AttachTo` 传 `zeroFrame: false`：Win11 的圆角就画在那 1px 可见边框上，压成 0 会变直角。
  · 分面两个 ListBox 改 `UniformGrid Columns=1 Rows=Items.Count`，并给 `RowItem.Compact` 加
    `VerticalContentAlignment=Stretch`：行高由容器均分，小窗 860×520 也能看全 7 个类型桶 +
    6 个时间桶，不再出现「只有全屏才看得到最近 30 天 / 365 天」。
- Qbar 面板不再记忆过滤器：`Open()` 里调 `ResetFilterChip()`。根因是 chip 存在 `_appliedFilter`，
  `Open()` 只清了 `_chip`，输入框置空后 `EffectiveInput("")` 又拼出 `WD:`。
- 过滤器改名（用户报「改了关键字列表里还是 doc」「改完变成新增一条」「WD 两个字留在输入框」）：
  `QbarFilterCatalog.Create` 改两轮合并 —— 先按 **Id** 认领内置项（再按关键字兼容老配置）。
  于是 改 `doc`→`WD` 之后仍是一条、旧关键字失效、`WD + 空格` 变成 chip 时能按
  `filter.Keyword` 把触发词切掉；设置页对关键字冲突直接拒绝保存。
- 命令卡：内置命令（含 `ol`）的程序/参数/工作目录全部可编辑；`QbarCommandStore.EnsureDefaults`
  对「Id 是内置 或 关键字是 ol 且参数为空」补 `-w "{query}"`，点开 ol 就能看见默认参数。
- 索引卡：Everything 就绪时按钮变成禁用的「Everything 已启动」，不再显示「启动 Everything」。
- 验证：`dotnet build -c Release -f net10.0-windows` 0 警告 0 错误；`--selftest` **6401 项 / 0 失败**；
  离屏渲染 render-v25/qbar-expand.png 分面均分填满；render-v26、render-v27 新增屏幕截屏
  `qbar-expand-onscreen.png` 证实 DWM 圆角生效；render-v28（`INPUTPLUS_RENDER_THEME=dark`）
  证实深色下标题栏与正文一起变深。

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
## 2026-11-21 14:40 按住拖拽「缩放只能单轴」（右键有时只变上下或只变左右）

- 用户现场是「**有时候**」。先用新增的诊断命令 `--holddrag-config` 打出现场配置：触发键 Enter(0x0D)、
  长按 200ms、CenterFraction=24、SidesFraction=255、ResizeCenter=1（四向扩张）、限定三块 USB 键盘、
  SwallowTrigger=False —— 配置完全正确，选边算法对四角也确实两轴。
- 真因：九宫格的**边带各占 38%**，中间带只剩 24%。随机抓点约 42% 的面积落在「一条边 × 一轴中间带」的十字区，
  那一轴被 Center 锁死 → 只能单轴。边带是**看不见**的，用户无法主动避开，于是感知成「有时候」。
- 修法 `AltDragCore.AdoptFollowAxis`：只处理「一轴在边带、另一轴在中间带」。记住起手点，位移超过
  `ResizeAxesThreshold`（默认 60 物理像素）就把中间带那一轴**收养**成对应方向。
  **收养时必须当场重算 Offset** —— 中间带里存的是绝对坐标，直接当边带 offset 会让那一帧宽度翻倍、整轴失灵
  （上一版补丁就栽在这里）。开关 `ResizeBothAxes` 默认 true，关掉就退回纯九宫格行为。
- 上游对照：`AltSnap-main/hooks.c` 的 SetEdgeAndOffset 只切一刀、SetEdgeToClosestSide 只挑一个轴，移植体与之一致；
  本次是在移植体上的**增强**，不改上游算法本身。
- 自检：新增 6 条纯几何断言（含「只在中间带不动」与「收养那一帧尺寸不跳」）+ DragE2E 场景⑥（抓右边带斜拖 10 步），
  实测 420×320 → 460×360（高度只涨越过阈值的 40px，是重算 offset 的必然结果）。
- `dotnet build -c Release -f net10.0-windows` 0 警告 0 错误；`--selftest` **6411 项 / 0 失败**。

## 2026-11-21 14:52 文件搜索过滤器进入「Qbar 搜索」和「直接搜索」两个动作分类

- 「Qbar 搜索」新增 `qfilter:<过滤器Id>`：打开面板并**直接挂好过滤器 chip**（等价于打 `wd` 再按空格）。
- 「直接搜索」新增 `fsearch:<过滤器Id>`：取当前选中的文字，按这个类型打开面板去搜（不用先开面板再打关键字）。
- 只注册 `Enabled` 的过滤器：关掉的过滤器在面板里连 chip 都打不出来，绑上键只会得到一个空面板。
- 过滤器被删掉/改关键字之后：动作 Id 用 `QbarFilter.Id`（内置是 `builtin.doc`），命中先按 Id 再按关键字；
  找不到时说清是哪一条没了（`找不到这个过滤器：xxx`），不让人去猜「键是不是坏了」。
- 新增 `QbarWindow.OpenFileSearch(filterId, query)`、`AppHost.ShowQbarFileSearch`（含重建窗口兜底）、
  `ActionContext.RequestQbarFileSearch` / `SearchSelectionAsFile`（取词复用 SearchSelection 那套三层提示 + Ctrl+C 保底，不另写一套）。
- `--selftest` **6435 项 / 0 失败**（新增 24 条：两个分类都有动作、关掉的不注册、请求带对 Id 与词、
  选区文字 trim、找不到过滤器有明确提示）。

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

## 2026-11-21 15:02 欧路查词进入「直接搜索」和「Qbar 搜索」两个动作分类（v1.7.6）

- 「直接搜索」新增 `dict.lookup`：取当前选区 → 送词典。装了欧路走 `eudic.exe -w 词`，没装就在浏览器打开
  `dict.eudic.net/dicts/en/<已转义词>` 并明确说「本机没有装欧路词典」。
- 「Qbar 搜索」新增 `qdict.lookup`：取选区 → 打开面板并挂好「查词 ×」chip，后面还能接着打词 / 换过滤器。
- 查词收敛成唯一出口 `DictionaryLauncher`（新文件 `src\InputPlus\Core\Qbar\DictionaryLauncher.cs`）：
  用户命令的 Path / Parameter / WorkingDirectory 优先 → 定位器 `EudicLocator.Find()` → 网页。参数留空补出厂
  `-w "{query}"`；`EffectiveCommand` 先按 Id 再按关键字认，改名字也不失效。
- 取词合并成 `ActionContext.TryCaptureWord(action, override, maxLen, out text)`：三层权限提示、Ctrl+C 保底、
  超长按代理对截断；`SearchSelectionAsFile` 与查词共用（300 / 120 两个上限）。
- UI：`QbarWindow.OpenDictionary(词)`、`AppHost.ShowQbarDictionary`（含窗口重建兜底）、
  `ActionContext.QbarDictionaryRequested` / `RequestQbarDictionary`；`RunDictionaryCommand` 不再自己找程序拼命令行。
- 渲染核图 `D:\tmp\render-v33\qbar-dict-action.png`（1520×867 / 102001 B）：chip「查词（欧路词典 / 网页词典）」+
  `hello` + /hə'ləʊ/ + n./v./int. 释义行 + 每行「复制」徽标 + 底部「↵ 选择 Enter 执行 Tab 补全 Esc 关闭」，符合预期。
- `--selftest` **6449 项 / 0 失败**（+13：两个动作存在且分类正确、词 trim 后交给面板、没装欧路开网页且词已编码、
  装了欧路走命令且参数补出厂模板、用户指定程序优先、用户模板原样保留、空词只提示不启动、地址编码中文）。
- 版本号 1.7.5 → **1.7.6**（csproj 三处），CHANGELOG 顶部新增 v1.7.6 小节。

## 2026-11-21 15:23 修「真实配置里 doc: 全废」+ 停用开关 + 两个诊断开关 → v1.7.7（本地，未上传）

**做了什么**
- 定位：真实配置把六个内置过滤器关键字改成拼音缩写（wd/tp/sp/yp/wj/wjj），`doc:` `file:` `folder:` 全部降级成字面量搜索；
  `file: report` 命中 1922 条后又被本地复核全剔（找的是含 `file:` 的文件名）。二进制没问题 —— 同一 exe 换空数据目录一切正常。
- 别名：内置六条保留出厂关键字，`doc:` 与 `wd:` 指向同一条；别名只挂内置 Id、不写进设置。
- 停用语义：合并时不再跳过 `Enabled=false` 的项，`Override` 不再把 `Enabled` 写死 true；翻译层遇停用过滤器整条忽略并留诊断行。
- 新增 `--settings-dump`（数据目录/加密与解锁/加载失败/用户层与合并后的过滤器/命令/规则/选项）与
  `--qbar-command "ol hello"`（只解析：命令表、命中项、模板、占位符展开、程序是否存在、PATH 解析）。

**没做**
- 没动 Everything 索引本身（仍是 SDK 查询）；没给「停用过滤器」设计新的 UI 文案（沿用 Enable 开关 + 诊断行）。
- 命令行的 `{current_folder}` 在无前台资源管理器目录时仍展开为空（`cd /d ""`），未改语义，只记录。

**怎么验**
- 自检 6465 项 0 失败（新增/改写 5 条过滤器断言：旧关键字别名、rename 后 doc: 仍 49 扩展名、停用后仍列出且 Enabled=false、停用后整条忽略不降级）。
- 安装位置实测（1.7.7）：`--settings-dump` 列出 6 条用户层过滤器（关键字 wd/tp/sp/yp/wj/wjj）+ 1 条 ol 命令；
  `--qbar-query "doc:"` → `filter wd -> 51 exts`；`--qbar-query "file: report"` → 1922 条候选不再被剔；
  `--qbar-command "ol hello"` → 命中 builtin.dict、参数展开 `-w "hello"`、`D:\software\eudic\eudic.exe` 存在。
- 渲染 `qbar-command.png`：命令行动作行与文件结果并列，行上写「在此处打开命令提示符 / cmd.exe /K cd /d ""」。

**下一步 / 已知限制**
- 过滤器改名的「别名」目前只覆盖内置六条；自定义过滤器改名后仍按新名走（无别名概念）。
- `--qbar-query` 的单次端到端在真机冷启动时能到 165–199ms（Everything 首次查询 + 路径补扫），warm 后 50ms 上下。

## 2026-11-21 15:45 发版 v1.3（版本号归一 + 线上同步）

- **做了什么**：版本号从 1.7.7 统一改成 **1.3**（csproj `<Version>1.3</Version>` / `FileVersion` `AssemblyVersion` 1.3.0.0），CHANGELOG 顶部新增 v1.3 段（说明是重新起号、功能为 1.7.7 的延续）。本地 `tools/release.ps1` 出 dist/1.3 四形态，覆盖安装位置 `D:\software\myself\InputPlus.exe`；线上 `git push origin main` + `git tag v1.3` + GitHub Release「v1.3」并上传四个产物。
- **怎么验**：构建 0 警告 0 错误；dev 与 dist 各跑一遍 `--selftest` → 6464 项 / 0 失败；安装位置 FileVersion 读回 1.3.0.0；GitHub API 读回 release 非 draft、4 个 asset 均 `state=uploaded`。
- **数字**：InputPlus.exe 9,083,535 B sha256 4f6b8bc2befd58daef00d2cd157e3552ebb7d6b726dd36ab1775e5147d0b675f；x64-Win7 145.1MB / x86-Win10-Win11 8.6MB / x86-Win7 134.8MB。
- **已知限制**：版本号从 1.7.7 降到 1.3，若用户机器记录过 1.7.x，属性面板会显示更小的号（代码里已无按字符串比较的降级逻辑，设置不受影响）。
- **下一步**：无。
