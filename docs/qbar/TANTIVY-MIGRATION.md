# 文件索引迁移：外挂索引 → 进程内 tantivy

> 适用版本：v1.5.8.1 起（只支持 Windows 11 x64）
> 结论先说：文件搜索的**索引构建、增量维护、查询、排序全部由本程序自己完成**，
> 进程外索引依赖已彻底删除，全仓 grep 无残留（见文末「清零自证」）。

---

## 1. 调用链

```
用户输入
  └─ QbarQueryTranslator.Translate(input)                    ← 语句 → QbarSearchSpec
       └─ QbarSearchEngine.Run(context, input, limit)
            ├─ window = clamp(MaxCandidates, 200, 20000)     ← 无关键词时降到 600
            ├─ QbarTantivyBackend.TryRun(spec, window, …)    ← 取候选窗口
            │    ├─ BuildQuery(spec) → IndexQuery            ← 谓词全部下推
            │    ├─ QbarIndex.Search(query, window×2)        ← P/Invoke → ipidx.dll → tantivy
            │    └─ QbarSpecFilter.Accept(每一条)            ← 语义复核 + 打分（唯一实现）
            ├─ QbarPriorityResolver.Resolve                  ← 优先级 / 忽略 / 不索引
            ├─ keep.Sort(CompareRanked)                       ← 分 → 路径长度 → 序数
            └─ QbarSearchEngine.BuildHighlights               ← 高亮（唯一实现）
```

后端**只有一个**：`QbarIndex`（`Core/Qbar/QbarIndex.cs`）→ `TantivyApi`（P/Invoke）
→ `src/ipidx`（Rust cdylib）→ vendored tantivy。
索引没就绪时 `Backend = "off"`，界面给明确提示（`qbar:rebuild-index` 一键重建），
**不静默换源**。

## 2. schema 字段表

| 字段 | 类型 | tokenizer / 选项 | 用途 |
|------|------|------------------|------|
| `path` | TEXT stored | `cjk_ngram` | 全路径；片段/前缀匹配 |
| `pathid` | STRING (raw) | `raw` | 小写全路径；按路径删除、精确匹配 |
| `name` | TEXT stored | `cjk_ngram` | 文件名子串（中文/英文都走这里） |
| `name_raw` | TEXT stored | `raw` | 精确 / 前缀 / `wfn` 通配 / 正则 |
| `dir` | TEXT stored | `cjk_ngram` | 父目录；`path:` 谓词下推（P3 评估过删除，见 §8） |
| `dirfacet` | FACET | — | 目录分层（保留；侧栏改用 `typefacet`/`extfacet`，见 D69） |
| `extfacet` | FACET | — | 扩展名分面（`/pdf`、`/docx`…）：F9 分面直出 |
| `typefacet` | FACET | — | 文件/目录分面（`/file`、`/dir`）：F9 的"文件夹"计数 |
| `ext` | STRING | `raw` | 扩展名过滤与分面 |
| `isdir` | u64 fast+stored+indexed | — | 目录 vs 文件 |
| `size` | u64 fast+stored | — | 大小区间（闭区间） |
| `mtime` | i64 fast+stored | — | 距 2000-01-01 的天数（与 `QbarSyntaxValues` 同口径） |
| `depth` | u64 fast+stored | — | 路径深度（诊断 / 排序辅助） |
| `pinyin` | TEXT | `cjk_ngram` | 全拼（TinyPinyin，无分隔、小写） |
| `pinyin_initials` | TEXT | `cjk_ngram` | 拼音首字母 |
| `content` | TEXT（默认关，P1） | `cjk_ngram` | 正文全文检索（未启用，见 §8） |

`cjk_ngram` = `NgramTokenizer(1,2)` + `LowerCaser` + `RemoveLongFilter(64)`，自建注册
（`src/ipidx/src/lib.rs::register_tokenizers`）。

**schema 版本 = 2**（`QbarIndex.SchemaVersion` 与 `lib.rs::SCHEMA_VERSION` 必须同值）。
1 → 2 的差异是 F9 新增的 `extfacet` / `typefacet` 两个 facet 字段，老索引会被整体重建。

### 与需求文本的两处偏差（都是必须的，见 DECISIONS）

1. **`path` / `dir` 用 `cjk_ngram` 而不是 `raw`。**
   `raw` 只能整串精确匹配，`path:Documents` 这种"目录片段"谓词就完全无法下推
   （要么全表扫、要么窗口里根本没有候选）。精确匹配由 `pathid` / `name_raw` 承担。
2. **`pinyin` / `pinyin_initials` 用 `cjk_ngram` 而不是 `raw`。**
   拼音检索要的是"前缀/子串命中"（`ndzj` 命中「年度总结报告」），`raw` 只能整串相等。

## 3. 查询构造

- **中文/英文子串一律 2-gram AND**，不用 `PhraseQuery`。
  实测证据：tantivy 的 `NgramTokenizer` 把每个 token 的 `position` 都置 0，
  `PhraseQuery(name,"report")` 恒 0 命中（spike 实测，见 §5）。
  2-gram AND 是**真子串的超集**：不漏召回，多出来的假阳性由 `QbarSpecFilter` 逐条复核。
  输入长度 ≤ 1 时用 1-gram。
- **词命中「文件名 或 目录」** → `SHOULD(name_ngram, dir_ngram)`，与 `QbarSpecFilter` 同构。
- **正则**用 `RegexQuery`（`name_raw`）。tantivy 的自动机**不接受 `^` 与 `$`**
  （报 `Empty match operators are not allowed`，spike 实测 `^report$` / `^report` / `\.docx$` 全失败）。
  所以先剥掉一对最外层锚点当**超集预筛**，锚定语义由 C# 的真 .NET 正则复核补回。
  自动机编不出来（`\b`、环视、反向引用）时，扫描正则取出**全部**长度 ≥2 的字面量片段，
  每个片段各自转成 `name_or_dir` 的 gram 查询并**全部以 MUST 下推**（F5）。
  `^rep(ort)?_\d{4}\.docx$` 这类多字面量正则因此能靠 `rep` + `.docx` 两段取窗口，
  而不是只留最长那一段。一个片段都没有才明确报错，不静默返回空。
- **`wfn` 通配**：glob → 正则（`*`→`.*`，`?`→`.`，转义其余元字符），同样走上面的预筛。
- **大小 / 日期**：`FastFieldRangeQuery`（闭区间），不下推到 C# 再筛。
- **否定词 / 否定扩展名 / `-path`**：全部 `MUST_NOT`，且**挂在最外层**——
  不能塞进"名字优先"那层 OR 里，否则只在名字里命中、却不符合 `ext:` 的文档会顺着另一条腿溜进来
  （fixture 用例一开始就抓到这个错）。
- **排序**：默认按 BM25 + 「名字命中优先」加权取窗口；`size` / `mtime` 走
  `TopDocs::order_by_fast_field`。最终排序与打分在 C# 侧（唯一实现）。

## 4. 增量与崩溃恢复

### 4.0 唯一的写入入口（F1 / F3）

```
QbarIndex.ApplyUpsert(paths)          ← watcher 的"新建/改名"、校准、快照 diff、整根重扫
  └─ 去重(HashSet, OrdinalIgnoreCase) → 收集 IndexDoc → 一次 lock(WriteGate) 内：
       TantivyApi.DeletePaths(旧路径) + AddBatch(文档) + Commit
QbarIndex.ApplyDelete(paths)          ← watcher 的"删除"
  └─ 去重 → 一次 lock(WriteGate) 内：DeletePaths + Commit
```

三条硬要求（每条都对应一个真实故障）：

1. **先删后加，且在同一个锁里连提交一起做。** tantivy 是 append-only，同一路径 add 两次就是两条
   文档。锁只包到 add 也不行：另一个线程的 commit 会把**我的**这一批一起取走，它成功了、
   我这边什么都没提交上（实测"两个线程各写 200 条最后只有 200 条"）。
2. **批内去重，且同一批"既增又删"以删为准。** `ReadDirectoryChangesW` 对一次写操作会同时报
   ADDED 与 MODIFIED；目录整棵搬入时补出来的子树会和批次重叠；改名的旧名/新名是两条不同路径
   （正常），但**同一路径**同时出现在两批里就是交错 —— 那时按删处理。
3. **提交失败就重开索引重放这一批**（D71）。失败的 commit 之后 writer 状态不可信，
   原地重试会"成功"却什么都没写进去。

### 4.1 全量建索引

- 根目录来自 `QbarRoots.Compute()`（用户目录、被重定向的桌面/文档/下载/图片/视频、
  Public、Program Files(x86)、ProgramData，**外加固定磁盘盘根** —— D61；已含关系折叠）。
- 后台扫描，`DirectoryInfo.EnumerateFileSystemInfos()` 取每条（一次系统调用带齐
  类型/大小/修改时间）。跳过 `WinSxS` / `node_modules` / `.git` / `obj` / `bin` /
  `$Recycle.Bin` / `System Volume Information` / `servicing`（按目录名整段比较）。
- 上限沿用旧语义：文件 80 万、目录 20 万。
- 攒 **20000 条**（`BuildBatchDocs`）或满 2 秒提交一次；增量路径仍是 5000 条 / 2 秒。
  批大小的差别是量出来的（D72）：批太小 → 段合并次数太多，67 万条要 135 次合并。
- 写入走**二进制协议** `ipidx_add_docs_bin`（见 4.4）；JSON 入口 `ipidx_add_docs` 保留给
  夹具构建与调试，两条路径共用同一个 `write_doc`。

### 4.2 实时增量

- 每个根目录一个 `ReadDirectoryChangesW` 线程（递归、64 KB 缓冲、`FILE_NOTIFY_CHANGE_*`）。
  `StartWatchers` 会等每个线程**把句柄真的打开**（`WaitArmed`）再返回，否则启动那一两百毫秒里
  新建的文件内核不会补发（实测 `--increment-check` 出现过"新建后可见=否"）。
- 变更按 250 ms 去抖成批次，交给 4.0 的两个入口。
- 目录被整棵搬进来/改名时，内核只报一条目录记录，所以见到目录变更就补扫它的子树
  （上限 5000 条，超了交给整根重扫）。
- **缓冲溢出 / 句柄失效** → 该根标 dirty，后台整根重扫（走 `idempotent: true`，即 4.0 的入口；
  早期版本这里直接 `AddDocs`，一次重扫就把整棵子树写成两份）。
- 根目录打不开句柄（网络盘 / 权限）→ 该根退化为 **30 秒 mtime + 条数对比**轮询。
- 每 6 小时校准一次（走 4.0 的入口，因此**幂等**）。
- **停机期间的增删**：目录 mtime 快照 + 启动 diff（4.3）。

### 4.3 目录快照（`dirstamps.json`）

- 全量建索引时顺手记"枚举过且 children > 0"的目录：`{目录: {mtime, children}}`，
  上限 5 万条，超了按"层数浅优先 + 最近 mtime 优先"截断，截断事实写进 `--index-status`。
- 启动走"沿用已建好的索引"分支时，**先**跑一次 diff，**再**挂 watcher，**最后**才置就绪：
  - mtime 变了 → 只重扫**该目录一层**，产出 upsert 批次；盘上新增的子目录继续走一层（递归点）；
  - 快照里有、盘上已经没了的目录 → 该根整体重扫（`MarkDirtyAndRescan`）；
  - 全量重建成功后重写快照。
- 实测：5 万条快照 diff 在"什么都没有变"时 0.9 s，"2976 个目录变了"时 2.1 s。
  检测是一次并行遍历（`DirectoryInfo` 的 Exists 与 LastWriteTimeUtc 共用一次 FindFirstFile），
  早先"两次遍历 + 两条 syscall + 逐目录数 children"的版本是 9.1 s。

### 4.4 二进制写入协议（F8）

```
u32 magic = 0x49504458 ("IPDX")
u32 count
count × 48 字节定长头（小端）：
  u32 path_off, u32 path_len
  u8  is_dir, u8 pad[3]
  u64 size
  i64 day                      (-1 = 未知 → 存 UNKNOWN_DAY)
  u32 depth
  u32 pinyin_off, u32 pinyin_len
  u32 initials_off, u32 initials_len
u32 pool_len
pool_len 字节 UTF-8 字符串池（空串统一指到池首的 0 长度段）
```

头的实际宽度是 **48**（需求文本写 40，与字段表自相矛盾，按字段表实现，见 D67）。
C# 侧用 `ArrayBufferWriter<byte>` + `BinaryPrimitives` 构造（D68），Rust 侧一个函数解包，
`write_doc` 是 JSON / 二进制两条入口唯一的字段映射。
**验证方式**：写 40 条（大小 0/1/1023/i×37，日期各不相同）→ 逐条按名字查回 →
比对 `path / isdir / size / day` 全等（自检 `索引：二进制写入的字段逐条读回全等`）。只比条数不算过。

### 4.5 索引损坏自愈（F2）

`ipidx_open` 的返回码分档：`-4` 目录/元数据坏、`-6` 创建写入器失败（多为另一个进程占着索引或权限）、
`-7` 创建读取器失败（段文件缺失最常见）。

- **只有 -4 / -7 才清空重建**：`Close()` → 清空索引目录 → 删 `state.json` → 重新 `Open`；
  成功则从零全量重建，并留痕（`CoreLog.Warn("索引损坏，已清空重建：…")` +
  `--index-status` 的「问题」一行写"本次启动因索引损坏重建过"）。
- `-6` 直接报错：那种情况下清空目录等于删掉用户那份还能用的索引。
实测：手工删掉一个 `.term`（131 KB）后重跑 `--index-status`，返回码 -7 → 自愈 → 全量重建 → 68 万条正常。

### 4.6 崩溃一致性

- 写索引前落 `state.json`：`{Version, Done, Roots[], Completed[], StartedAt, FinishedAt, Generation}`。
- 每个根扫完就更新一次 `Completed`；全部完成后置 `Done=true`。
- 异常退出后重启：`Done=false` → 整轮重扫（索引目录会被清空，不会留半份脏数据）；
  `Done=true` 且根目录一致 → 直接沿用，由 4.3 的快照 diff 补上宕机期间的变化。
## 5. 实测数据

### 5.1 spike（`D:\Local AI\_spike\ipidx-spike`，300,000 条枚举）

| 指标 | 数值 |
|------|------|
| 枚举 300,000 条 | 2.74 s |
| 建索引 300,000 条 | 3.79 s（**79,162 条/秒**，只含纯 Rust `add_document`） |
| 索引体积 | 71.2 MB（**249 字节/文档**） |
| 中文 2 字子串「银行」p95 | 0.01 ms |
| 中文单字「银」p95 | 0.01 ms |
| 英文子串「rep」p95 | 0.15 ms |
| 路径片段 `Documents\报表` p95 | 0.09 ms |
| `AllQuery` 取 2000 p95 | 0.43 ms |
| `PhraseQuery(name,"report")` | **0 命中**（position 全 0，方案否决） |
| `RegexQuery` | `^`/`$` 被拒；去锚点后全部可用；`\b` 被拒（→ 字面量片段预筛） |

### 5.2 正式实现（本轮终态：67.7 万条，schema=2）

`--index-status --rebuild --increment-check`（Release，根 = `C:\` + `D:\`）：

| 指标 | v1.6.0（迁移当轮） | 本轮终态 | 说明 |
|------|--------------------|----------|------|
| 文档总数 | 677,248 | 676,551 | 两边都在变（磁盘在动），量级一致 |
| 索引体积 | 206.2 MB（319 字节/文档） | 233.7 MB（362 字节/文档） | +13%，D72 的 4 写入线程换吞吐 |
| 建索引 | 23.1 s / 29,346 条/秒 | **16.6 s / 40,643 条/秒** | 二进制协议 + 4 写入线程 + 2 万批 |
| 建索引分段 | 枚举 210 ms · 写索引 27.0 s | 枚举 448 ms · 写索引 16.9 s · 拼音 74 ms | 写索引含 commit/段合并 |
| 查询 p50 / p95（`--index-status`） | 7.4 / 21.1 ms | 9.1 / 16.9 ms | 同量级，无回归 |
| `--qbar-bench` 20 条 p95 | 21.9 ms | 17.4 ms（分面开）/ 19.2 ms（分面关） | 预算 ≤ 50 ms |
| `--qbar-bench` 单条最大中位数 | 22.8 ms | 21.6 ms | 预算 ≤ 40 ms |
| 增量（新建可见 / 删除不可见） | 230 / 1656 ms | 610 / 477 ms（3 轮：610/477、605/995、486/561） | 预算 ≤ 2 s |
| `cargo test` | 45 项 | **54 项**（26 单元 + 28 夹具） | +9 |
| `--selftest` | 7089 通过 / 1 失败 | 见 §5.4 | 含本轮新增用例 |

**F8 的三段耗时占比**（决策规则的输入）：

| 版本 | 写索引段 / 墙钟 | 拼音 / 墙钟 | 端到端吞吐 | 结论 |
|------|-----------------|-------------|------------|------|
| JSON 入口，1 写入线程 | 25.7 s / 23.3 s = **110.4%** | 61 ms / 23.3 s = 0.3% | 29,010 条/秒 | 规则 1 成立（≥55%）→ 上二进制协议 |
| 二进制入口，1 写入线程 | 25.7 s / 23.1 s = **111.0%** | 56 ms = 0.2% | 29,215 条/秒 | 协议**没有**解决问题 |
| 二进制入口，4 写入线程 + 2 万批 | 16.9 s / 16.6 s = **101.7%** | 74 ms = 0.4% | **40,643 条/秒** | 达标 |

结论：瓶颈是 tantivy 的 `add_document` + 段合并（受写入线程数限制），不是入参编码。
拼音只占 0.3%，所以"优先优化拼音"那条分支不成立（也印证了 `MakeDoc` 里的 `HasCjk` 短路有效）。

### 5.3 F9 分面直出的延迟增量（A/B，同一份索引）

| 版本 | `doc:` 中位 | `doc:` 最大 | 20 条 p95 |
|------|-------------|-------------|-----------|
| 分面开（`MultiCollector` 收 ext/type/mtime） | 10.0 ms | 10.5 ms | 17.4 ms |
| 分面关（`INPUTPLUS_QBAR_NOFACETS=1`） | 10.9 ms | 12.0 ms | 19.2 ms |

增量 **≈ 0 ms（噪声内）≤ 10 ms** → 按决策规则**上线**，文案改成「全部：」。
`--qbar-query "doc:"` 输出 `全部：文档 67995 · 近7天 32241`；`--qbar-query "filler"` 输出
`全部：文档 409 · 图片 150 · 文件夹 1246 · 其他 6056 · 近7天 1303`。

### 5.4 golden 对照（`tools\qbar-golden-compare.ps1`，24 条基线）

| 版本 | 缺失合计 | 新增合计 |
|------|----------|----------|
| 本轮终态（schema=2，保留 `dir`） | **661** | 701 |
| F12 实验（去掉 `dir`，schema=3） | 657 | 697 |

（任务书写的历史值是 656；那是另一天的磁盘内容，本轮基线以实测 661 为准。
两条"缺失"里 `regex:^rep.*\.docx$` 与 `file: 报表` 命中的是 `D:\tmp\qbar-efu\*` —— 目标文件已不存在，
属于正确行为。见 §8。）
## 6. 行为差异

| # | 差异 | 是不是"新能力/新限制" | 处理 |
|---|------|------------------------|------|
| 1 | 拼音命中现在包含**目录**（旧实现只返回路径、`IsDir` 恒 false） | 修正 | 目录按真实类型返回 |
| 2 | 正则不再要求"整条查询只有正则" | 新能力 | 可与其他谓词自由组合；锚定由复核保证 |
| 3 | 正则里含 `\b`/环视时改为"全部字面量片段 AND 预筛"（F5），不再是"最长一段" | 修正 | 召回变宽、不需额外参数 |
| 4 | 大小谓词按**精确字节**比较（F4）。早期版本 `/1024`→`*1024` 把 1~1023 字节抹成 0 | **修正（旧行为是 bug）** | `size:>0` 现在能命中 500 字节的文件 |
| 5 | 命中数超过候选窗口时，"窗口内取哪 2000 条"由 BM25 + 名字优先加权决定 | 排序细节差异 | 后端多取一倍候选再按真分数裁到窗口 |
| 6 | 索引未就绪时结果为空并明确提示（旧版会静默降级到内存快照） | 语义变更 | 界面给"重建索引"入口 |
| 7 | 侧栏分面的口径从「本页」变成「全部」（F9，索引直出覆盖全部命中） | 能力增强 | `FacetSummary()` 按 `FacetsAreGlobal` 输出「全部：」/「本页：」 |
| 8 | 内容检索（`content:`）仍未实现 | 原有缺口 | 见 §8 |

## 7. P1 / 本轮（F0～F12）完成情况

| # | 项 | 状态 | 说明 |
|---|----|------|------|
| F0 | 盘根语义显式化 | **完成** | 两条用例守住（`索引：根目录包含固定磁盘盘根` / `索引：根目录互不包含`）；D61 扩写成完整决策 |
| F1 | 校准幂等（不重复写每个文件） | **完成** | 校准改走 `ApplyUpsert`（先删后加 + 批内去重）；用例连跑两次 `DocCount` 不翻倍 |
| F2 | 索引损坏自愈 | **完成** | 只在 `-4`/`-7` 时清空重建；`--index-status` 的「问题」留痕；实测删段文件后自动恢复 |
| F3 | 写入路径竞态 | **完成** | `ApplyUpsert`/`ApplyDelete` 是唯一入口（delete+add+commit 同锁）；watcher 批次去重 + 删优先；三条用例 |
| F4 | `size:` 按字节比较 | **完成** | `QbarSpecFilter.Accept(..., sizeBytes)`；500 字节 / 0 字节两条用例 |
| F5 | 正则多片段预筛 | **完成** | `literal_fragments` + 全片段 MUST 下推；Rust 单测 5 条 + 端到端用例 |
| F6 | 停机期间的增删 | **完成** | `dirstamps.json` 快照 + 启动 diff（只比 mtime，一次并行遍历）；两条用例 |
| F7 | `按住拖拽` 断言分层 | **完成** | 解算层硬断言 + 落地层 `WaitUntil` 轮询；`--only=HoldDrag` 连跑 3 次 181/181 |
| F8 | 建索引吞吐 | **完成（走了协议分支）** | 二进制协议 + 4 写入线程 + 2 万批 → 42,668 条/秒；写后读回逐字段比对用例 |
| F9 | 侧栏分面直出 | **完成（增量 ≈0 ms → 上线）** | `MultiCollector` 收 `extfacet`/`typefacet`/mtime 直方图；文案「全部：」 |
| F10 | 缺 `ipidx.dll` 的可见性 | **完成** | 一次性 Toast（`NotifySink`）+ `--index-status` 明确原因；改名 dll 实测过 |
| F11 | 单字符查询：只写文档 | **完成** | 见 §8 第 5 条 |
| F12 | 去掉冗余 `dir` 字段 | **评估过、未实施** | 见 §8 第 9 条与 D73（体积 186.7 MB > 165 MB 门槛） |

## 8. 已知限制与 v2

1. **内容全文检索（`content:`）**：schema 里留了 `content` 字段但没启用。
   开启需要"扩展名白名单 + 单文件 ≤ 2 MB + 后台索引不阻塞查询"三件事一起做，
   且会显著抬高索引体积与建索引时间。留 v2。
2. **USN Journal**：本轮用 `ReadDirectoryChangesW` + 目录 mtime 快照（F6）；USN 需要卷句柄 + 提权，
   能在"程序没运行期间产生的变更"上也做到 O(变更数)。留 v2。
3. **网络盘 / 移动盘实时索引**：显式不做（P2 禁止项）。
4. **正则能力受限于 tantivy 的自动机**：`\b`、环视、反向引用都不支持；自动机编不出来时
   退到"全部字面量片段 AND 预筛"（F5），一个片段都没有才明确报错。
5. **单字符查询（F11）**：1-gram 命中的是"任意包含该字符"的文档，命中数可能远超窗口，
   窗口由 BM25 决定，**理论上会漏掉真正的短名匹配**（例如搜 `a` 找不到 `a.txt`）。
   UI 路径 `QbarProviders.MatchFiles` 里 `if (text.Length < 2) yield break;` ——
   **界面根本不发单字符文件查询**，这个风险只存在于 `--qbar-query "a"` 这类命令行诊断，
   诊断时把输入加长到一个词即可。不加 `prefix_ngram` 字段的理由：schema 变更 + 全量重建 +
   体积上涨，换来的只是诊断路径的正确性。
6. **`size:` 的口径**：已改成**精确字节**（F4）。`FileSearchHit.SizeKb` 仍按 KB 显示，
   但谓词比较不再经过 KB 截断。
7. **快照截断**：`dirstamps.json` 上限 5 万条目录，超出按"层数浅优先 + 最近 mtime 优先"截断。
   被截掉的深层目录在**停机期间**的变化要等 6 小时校准才补上（`--index-status` 会写明截断了多少）。
   本机 11 万目录 → 截断约 6 万。
8. **校准只增不删"盘上已消失的条目"**：`ApplyUpsert` 是"先删后加"，但它只处理**枚举到的路径**；
   停机期间被删掉的条目在 watch 漏掉之后不会被清掉（要发现它们需要一张"索引里有什么"的全量清单，
   而那正是当初迁走的内存快照）。全量重建或 `--rebuild` 会清干净。
9. **`dir` 字段保留**：评估见 D73。去掉它能省 47 MB（-20.1%）且召回不降（golden 缺失 661→657），
   但相对 206 MB 基线只降 9.4%（< 20% 门槛）、实测 186.7 MB > 165 MB 验收线，
   所以不改。真正的下一步是连 `name`(ngram) 一起收掉（它与 `path` 也是包含关系）。
10. **建索引把索引目录自己也算进索引**：根是 `C:\`/`D:\` 盘根，`InputPlus Data\file-index-tantivy`
    落在其中，所以索引自己的段文件也会被索引进去（几十条到几百条）。它们会被 `QbarFilterCatalog`
    当作普通文件展示；影响面仅限"搜 tantivy/segment 这类词时会看到索引自己的文件名"。
    不排除的理由：排除它需要把"程序自己的数据目录"硬编码进 `QbarRoots.Skip`，
    而用户在便携模式下可能把数据目录放到任何地方 —— 收益不抵风险。