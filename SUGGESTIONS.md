# SUGGESTIONS（搜索前建议）

启动器唤出后、用户还没输入时，展示一组「最近用过的文件 / 文件夹 / 应用」；开始输入后建议让位给正常搜索结果。
分层照 Listary 7：**Sources → Item → Merger → Rules → Blacklist → Presenter**，建在既有 Everything 内核之上。

- 代码根：src/InputPlus/Core/Suggestions/
- Presenter：src/InputPlus/Core/Qbar/QbarService.cs 的 DefaultResults + UI/Windows/QbarWindow.xaml
- 设置页：Qbar 面板 → 右栏最下方「搜索前建议」
- 诊断：--suggestions-probe、--selftest

## 1. 数据流

~~~
                     ┌──────────────── SuggestionContext(Settings, Rules, MaxItems) ────────────────┐
                     │                                                                            │
  ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌──────────┐
  │ downloads│   │ desktop  │   │ wechat   │   │ wecom    │   │ feishu   │   │ windows- │   │ dialog-  │
  │          │   │          │   │          │   │          │   │          │   │ recent   │   │ history  │
  └────┬─────┘   └────┬─────┘   └────┬─────┘   └────┬─────┘   └────┬─────┘   └────┬─────┘   └────┬─────┘
       │              │              │              │              │              │              │
       │  Probe(): Directory.Exists（毫秒级，不存在计 0）              │  .lnk → IShellLink      │ DialogRecorder
       │  Collect(): 后台线程懒枚举 + SuggestionBudget                  │  (STA + StringBuilder)   │ JSONL
       └──────────────┴──────────────┴──────┬───────┴──────────────┴──────────────┴──────────────┘
                                            ▼
                                    IReadOnlyList<RecItem>
                          { ItemType, SourceId, SourceKind, Path, Name,
                            Modified, Badges, MergedSources }
                                            │
                                            ▼
                                   RecMerger.Merge
                     · 按 RecItem.Key（规范化小写路径）去重，权重高者胜
                     · Badges / MergedSources 合并
                     · OrderByDescending(Modified) → SourceWeight → 名称长度
                     · 候选上限 2000（AppSettings.Suggestions.MaxCandidates）
                                            │
                                            ▼
                                     RecRules.IsExcluded
                     空路径 → 黑名单 → 系统目录（目录边界）→ 排除扩展名 → 白名单
                                            │
                                            ▼
                            SuggestionsService.Snapshot（后台线程发布）
                                            │ Changed 事件
                                            ▼
                         QbarService.DefaultResults（空态唯一入口）
                     · SuggestionsService.Current?.FirstScreen(ShowCount)
                     · 与 RecentTargets 用同一 seen 去重
                     · QbarResult { Score=0, Suggestion=sourceId, Badge=来源徽标 }
                                            │
                                            ▼
                       QbarWindow：分组标题「最近文件」+ 同一个虚拟化 ListBox
~~~

**互斥**：建议和搜索结果共用同一个 ListBox；`QbarService.EmptyStateGroup` 只在空态真的带建议时非空，
输入后 `QueryInternal` 立即清空它。

## 2. 八个来源

| id | 徽标 | 权重 | 位置 | 说明 |
| --- | --- | --- | --- | --- |
| downloads | 下载 | 70 | `KnownFolders.Downloads()`（注册表已知文件夹，支持重定向） | 深 2 层 |
| desktop | 桌面 | 65 | 用户桌面 + 公共桌面 | 深 1 层 |
| wechat | 微信 | 80 | `Documents\WeChat Files\*\FileStorage\File`、`xwechat_files\*\msg\file` | 不存在计 0 |
| wecom | 企业微信 | 75 | `Documents\WXWork\*\Cache\File`、`...\Cache\Image` | 不存在计 0 |
| feishu | 飞书 | 75 | `Documents\Feishu\*`、`Documents\飞书\*`、`%UserProfile%\Feishu\*` 等 | 不存在计 0 |
| windows-recent | 最近 | 90 | `%APPDATA%\Microsoft\Windows\Recent\*.lnk` → `IShellLink.GetPath` | 最多 240 个 lnk，按 lnk mtime 取新 |
| dialog-history | 对话框 | 100 | `DataDir\dialog-history.jsonl`（文件对话框浮层的记录） | Task 1 的 Recorder |
| app-history | 最近 | 110 | `RecentTargets`（qbar 自己打开过的程序/文件夹） | Program → App |

不引入「猜你喜欢」模型；默认排序主键是文件修改时间。

**来源顺序**：直接在「搜索前建议」的来源表里**按住一行上下拖**（复选框以外区域，行本身是拖拽手柄，没有额外图标）。拖完立刻保存到 `Suggestions.SourceOrder`，建议服务自动重采；Qbar 空态先按来源顺序分组，同一来源内仍按修改时间倒序。没有单独的排序窗口，也没有上移/下移按钮。

## 3. 配置模型

`AppSettings.Suggestions`（`SuggestionsOptions`）：

~~~
Suggestions {
  Show            = true
  ShowCount       = 20          // 空态展示条数
  MaxCandidates   = 2000        // 采集硬上限
  CollectBudgetMs = 1200
  Sources         = []          // 兼容字段，空 = 全开
  DisabledSources = []          // 真正生效的禁用列表
  FileTypeFilter  = []          // 只保留这些扩展名（空 = 全部）
  Exclusions {
    Files          = []          // 黑名单：文件（用户"不建议此文件"）
    Folders        = []          // 黑名单：文件夹
    FileExtensions = [tmp,bak,log]
  }
  HideFileHotkey  = Ctrl+Alt+H
}
~~~

落盘：`DataDir\suggestions.delta.json`，只追加 delta：

~~~json
{"version":1,"ops":[{"op":"insert","key":"hide-file:d:\\tmp\\a.txt","value":"D:\\tmp\\a.txt","ts":...}]}
~~~

超过 200 条时按 key 压成每条最后一次。启动时 `Replay(SuggestionsOptions)`，再回填设置页。

## 4. 性能与降级（实测）

| 指标 | 实测 |
| --- | --- |
| 温启动 `Start()` | 73ms（先读缓存） |
| 首屏（缓存命中） | **0.3ms / 20 条** |
| 冷启动 `Start()` | 51ms，首屏 0 条，后台 297ms 补齐 |
| 全量采集 | 185–297ms（本机 8 来源，原始 197 → 合并 167） |
| 采集线程 | `BelowNormal`，STA（ShellLink COM 要求） |

降级：

- 单来源 `Probe` false → 该来源计 0，不记失败；`Collect` 抛异常 → 只记该来源 `Failures[id]` + 日志，
  其它来源照常进入 Merger。
- 目录不存在、解析失败、配置写失败都不会打断启动器；配置写失败会让 `Hide` 返回 false 并保持内存不变。
- 首屏先返回缓存；`Changed` 只触发一次空态重查（输入非空时忽略）。

## 5. 加第 9 个来源：改三处

1. **写实现**：在 `src/InputPlus/Core/Suggestions/Sources/` 新建类，实现
   `IRecommendationSource`（`Id`、`Kind`、`Weight`、`Badge`、`Probe`、`Collect`）。
   目录型来源可直接继承 `FolderSourceBase` 或 `PatternFolderSourceBase`，只填 `Folders`/`Patterns`。
2. **注册**：`src/InputPlus/Core/Suggestions/SuggestionsService.cs` 的 `CreateSources()` 里加一行，
   决定顺序（顺序只影响日志，不影响排序）。
3. **设置页**：`src/InputPlus/UI/Views/QbarSettingsView.xaml` 加一个复选框（Tag = 新 id），
   并在 `QbarSettingsView.xaml.cs` 的 `AllSuggestionSourceIds` 数组里加同一个 id。
   （勾选状态由 `DisabledSources` 推导，不需要改落盘逻辑。）

自检：在 `SelfTest.Suggestions.cs` 的 `TestSuggestionSourceDegrade` 里把新 id 加进
`CreateSources()` 唯一性与 `Probe` 不抛的断言。

## 6. 设置页与空态

- 设置页：Qbar 面板 →「文件搜索」→ 页内目录「搜索前建议」。这是一张完整的设置卡：
  总开关、8 个来源的启用复选框 + 权重 + 上次采集状态（条数 / 失败原因 / 已关闭 / 没有数据）、
  空态显示条数、候选硬上限、采集时间预算、文件类型白名单、排除扩展名、隐藏热键、
  已隐藏列表（还原选中 / 还原全部）、「立即重新采集」和采集统计。
  每个来源独立降级，状态表能直接看出哪个来源没采到。
  截图：`D:\iprender\qbar-suggestions-settings.png`（文件搜索页的最后一张卡）
- 空态：唤出启动器（不输入）→ 标题「最近文件」+ 每行来源徽标。
  截图：`D:\iprender\qbar-empty-suggestions.png`
- 可复现渲染命令：`InputPlus.exe --render D:\iprender`（`INPUTPLUS_RENDER_PAGES=qbar`）。

## 7. 隐藏 / 还原

- 右键建议行 → 「不建议此文件 / 不建议此文件夹」；
- 或选中后按 `Ctrl+Alt+H`（可在设置页改）；
- 立即从列表移除 + toast「已隐藏，可在设置里还原」；delta 落盘失败会提示错误且内存回滚；
- 设置页「已隐藏的建议」→ 还原选中 / 还原全部。

## 8. 诊断

~~~powershell
InputPlus.exe --suggestions-probe   # 首屏/采集耗时 + 各来源条数 + lnk 抽样，logs\suggestions-probe.txt
InputPlus.exe --selftest            # 「搜索前建议」节 77 条
~~~

退出码：0 正常；2 采集未完成；3 首屏 >80ms 且非空；1 异常。
