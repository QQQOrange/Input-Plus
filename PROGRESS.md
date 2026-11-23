# PROGRESS

时间 / 里程碑 / 改动文件 / 验证方式。只追加，不改旧行。

| 时间 | 里程碑 | 改动文件 | 验证 |
| --- | --- | --- | --- |
| 2026-11-21 21:30 | 勘察 | 无（只读） | `dotnet build src/InputPlus/InputPlus.csproj -c Debug -f net10.0-windows` 基线 0 警告 0 错误 |
| 2026-11-21 21:36 | T1-M1 Watcher + Classifier + 探测日志 | `Core/FileDialog/DialogModels.cs`、`DialogNative.cs`、`DialogProbe.cs`、`DialogClassifier.cs`、`FileDialogWatcher.cs`、`DialogSearchQuery.cs`、`DialogProbeCli.cs`、`SelfTest.FileDialogBar.cs`、`SelfTest.cs`、`App.xaml.cs`、`Core/CliFlags.cs` | `dotnet build` 0 错误；`InputPlus.exe --selftest` 退出码 0，报告「文件对话框」节 49 条全 PASS（`selftest.log` 6682-6731 行）；对真实 WinForms「另存为」对话框跑 `--dialogprobe`：判型 FileDialog/VistaDialog，文件名框 `Edit id=0x03E9`，见 `logs\dialog-probe.txt` |
| 2026-11-21 21:44 | T1-M2 Bar 骨架（贴下沿 / 跟随 / 显隐 / 取键盘） | `UI/Windows/FileDialogBarWindow.xaml(.cs)`、`Core/FileDialog/DialogFocusLadder.cs`、`TemporaryKeyboardRedirect.cs`、`Core/Settings/AppSettings.cs``(FileDialogBarOptions)` | `dotnet build` 0 警告 0 错误；owner=`GWLP_HWNDPARENT`+NOACTIVATE 路径编译；截图/真机浮层见 ACCEPTANCE 11–17（未自动跑，附手工步骤） |
| 2026-11-21 21:52 | T1-M3 接通搜索（输入→列表→回调） | `DialogSearchProvider.cs`、`DialogSearchQuery.cs`、`FileDialogBarService.cs`（`OnQueryChanged`/`OnCommitRequested`） | 自检「文件对话框」节新增 Provider 5 条 + 搜索范围 4 条全 PASS；`QbarSearchEngine.Run` 复用既有 Everything 内核，未新建索引 |
| 2026-11-21 22:02 | T1-M4 回填 v1（键盘 + 剪贴板） | `DialogBackfill.cs`（FocusEdit/Ctrl+A/ClipboardPaster/Enter/Verify/重试 1 次） | `--filedialog-e2e`：Vista 保存 + 键盘 PASS，对话框返回 `...\回填 测试 (emoji😀).txt` |
| 2026-11-21 22:10 | T1-M5 回填 v2（UIA + 老式 + FolderDialog）+ Recorder | `DialogUia.cs`、`DialogRecorder.cs`、`DialogBackfill.cs`、`DialogNative.cs` | e2e：Vista + UIA PASS（AutomationId=1001）、老式 PASS；文件夹用例经 `IsFolderPicker`（AutomationId 1152 / 「文件夹:」）修好后 PASS，返回 `...\ip-filedialog-e2e`；自检 Recorder 8 条 PASS |
| 2026-11-21 22:18 | T1-M6 开关 / 设置页 / 回归 | `UI/Views/QbarSettingsView.xaml(.cs)`、`FileDialogBarService.cs`（Apply/热键应用/轮询关闭）、`Core/CliFlags.cs`、`App.xaml.cs` | 设置页 5 类开关 + 热键校验 + 进程禁用列表；`--dialogprobe` 与 `--filedialog-e2e` 均可用；全量自检 6703 项 0 失败 |
| 2026-11-21 22:24 | T2-M1 数据层（SourceKind / IRecommendationSource / RecItem / RecRules）+ 单测 | `Core/Suggestions/SuggestionModels.cs`、`IRecommendationSource.cs`、`RecMerger.cs`、`SelfTest.Suggestions.cs` | 自检「搜索前建议」77 条 PASS：NormalizeKey、去重+徽标合并、扩展名、系统目录边界、黑名单、排序、2000 上限 |
| 2026-11-21 22:27 | T2-M2 四个来源（下载 / 桌面 / Windows 最近 / 应用内历史） | `Sources/FolderSources.cs`、`Sources/ChatAndRecentSources.cs`、`ShellLinkReader.cs` | `--suggestions-probe`：downloads 95、desktop 25、windows-recent 77（修 `char[]` 封送前为 0）、app-history 0（本机 RecentTargets 只有文件/文件夹）；无失败来源 |
| 2026-11-21 22:31 | T2-M3 Merger + 排序 + Presenter 空态 | `Core/Qbar/QbarService.cs`（`DefaultResults` 实例化 + `EmptyStateGroup`）、`UI/Windows/QbarWindow.xaml(.cs)`、`SuggestionsSelection.cs` | 自检空态集成 12 条 PASS；渲染 `qbar-empty-suggestions.png`：标题「最近文件」、5 行真实建议带「下载」徽标 |
| 2026-11-21 22:34 | T2-M4 微信 / 企业微信 / 飞书探测 + .lnk 解析 | `Sources/ChatAndRecentSources.cs`、`ShellLinkReader.cs`、`KnownFolders.cs`、`Sources/FolderSources.cs` | 探针：未安装项计 0 且无错误日志；本机 Downloads 重定向到 D:\Downloads，改读注册表后 downloads 0→95；.lnk 抽样全部解析出真实目标 |
| 2026-11-21 22:37 | T2-M5 设置页四项 + 黑名单热键 + 还原 + delta 落盘 | `UI/Views/QbarSettingsView.xaml(.cs)`、`SuggestionConfigStore.cs`、`Core/Settings/SuggestionsOptions.cs`、`SuggestionsService.cs`（`ApplyHotkey`/`HideSelected`） | 自检 delta 往返 / 写失败回滚 / 重启后仍隐藏；渲染 `qbar-suggestions-settings.png` 见整卡 |
| 2026-11-21 22:41 | T2-M6 性能与降级（首屏埋点 / 来源失败降级 / 2000 上限） | `SuggestionsService.cs`（缓存计数、预算、Changed）、`SuggestionsProbeCli.cs`、`App.xaml.cs`、`CliFlags.cs` | 探针：冷启动 Start 51ms / 首屏 0.1ms（0 条）/ IsWarm 297ms；温启动首屏 0.3ms 出 20 条（缓存命中）；单来源异常只影响自身并写日志；上限 2000 自检 PASS |
| 2026-11-21 22:44 | 文档收尾 | `FILE-DIALOG-BAR.md`、`SUGGESTIONS.md`、`ACCEPTANCE.md`、`DECISIONS.md`、`PROGRESS.md` | 全量构建 0 警告 0 错误；自检 6703 项 0 失败；e2e 4/5 PASS + 超长路径按预期 |
| 2026-11-21 23:15 | 1.3.5 反馈修复（浮层默认 / 位置 / 聚焦 / 建议设置搬家） | `AppSettings.cs`（ShowOnAppear 默认 true + DefaultsVersion）、`SettingsStore.cs`（迁移+夹取）、`FileDialogBarWindow.xaml(.cs)`（输入行到底部、挂下沿、点按聚焦、Alt+Enter、物理工作区）、`FileDialogBarService.cs`（首屏只出输入栏、热键备选、失败定位）、`DialogSearchProvider.cs`（空查询用建议补）、`QbarSettingsView.xaml(.cs)`（建议卡搬进文件搜索页 + 来源状态表 + 预算 + 立即采集）、`DialogReveal.cs` | 自检 6705 项 0 失败；e2e 4 PASS + 超长路径按预期（退出码 0）；真机记事本「另存为」：浮层自动出现、输入 test 出 200 条、不挡保存/取消；渲染 `qbar-suggestions-settings.png` 确认新设置页 |
| 2026-11-22 09:25 | 1.3.8 修拖拽父级遍历 | `QbarSettingsView.xaml.cs`（ParentOf 安全遍历）、版本号/CHANGELOG | 发布产物自检 6712 项 0 失败；已覆盖安装 1.3.8 |
| 2026-11-22 09:10 | 1.3.7 来源拖拽排序 + 浮层居中 + 真实图标 | `QbarSettingsView.xaml(.cs)`（来源表改 ItemsControl + 拖拽）、`FileDialogBarWindow.xaml(.cs)`（居中、BarRow.Icon/IconCache）、`SuggestionsOptions.cs`/`RecMerger.cs` 沿用顺序 | 构建 0 警告 0 错误；渲染确认来源表无独立排序区、每行无手柄图标；图标与来源排序待发布后真机复核 |
| 2026-11-22 08:20 | 1.3.6 浮层宽度改 1/3 + 来源排序 | `FileDialogBarWindow.xaml.cs`（宽度/左对齐）、`SuggestionsOptions.cs`（SourceOrder/ResolveSourceOrder）、`RecMerger.cs`（来源优先排序）、`SuggestionsService.cs`（传顺序）、`QbarSettingsView.xaml(.cs)`（来源顺序列表/上移/下移/恢复默认）、`SelfTest.Suggestions.cs` | 构建 0 警告 0 错误；自检 6711 项通过 / 1 项既有 HoldDrag 时序抖动；渲染确认顺序列表为中文来源名；Qbar 空态排序由 RecMerger 单测覆盖 |
| 2026-11-21 23:20 | 1.3.5 顺带修 Qbar 鼠标移动崩溃 + 平滑滚动可观测性 | `QbarWindow.xaml.cs`（ParentOf 安全遍历）、`ScrollView.xaml.cs`（状态行每秒刷新）、`SmoothScrollService.cs`（首次接管日志/注入失败计数）、`GestureService.cs`（EnsureMouseHook）、`InputSender.cs`（WheelDelta 返回 bool）、`ScrollProbeCli.cs` | `--scroll-probe` 真机：钩子收到手拨 8 次、带标记注入 44 次，链路正常；崩溃栈修复后自检不再触发界面线程异常 |


| 2026-11-22 09:40 | 1.3.9 QuickSwitch 按钮/浮层/导航 | QuickSwitchModels.cs、QuickSwitchHistory.cs、QuickSwitchFolderProbe.cs、QuickSwitchCatalog.cs、DialogBackfill.Navigate、FileDialogBarService、FileDialogBarWindow.xaml(.cs)、AppSettings/SettingsStore、QbarSettingsView | 构建 0 警告 0 错误；自检 6729 项 0 失败；真机 chip 导航、菜单/收藏浮层、宽度/零间隙测量 |


| 2026-11-22 09:55 | 1.3.10 QuickSwitch 点击/固定项/建议/图标/贴边 | FileDialogBarWindow.xaml(.cs)、FileDialogBarService.cs、QuickSwitchModels.cs、QuickSwitchCatalog.cs、DialogBackfill.cs、QbarSettingsView.xaml(.cs) | 构建 0 警告 0 错误；自检 6729 项 0 失败；物理点击按钮、目录导航地址栏、真实图标 PrintWindow 验证 |


| 2026-11-22 10:15 | 1.3.11 DialogBarAnchor + 按钮语义/图标/菜单折叠 | DialogBarAnchor.cs、FileDialogBarWindow.xaml(.cs)、FileDialogBarService.cs、QuickSwitchModels/Catalog、DialogBackfill.cs、ANCHORING.md | 构建 0 警告 0 错误；自检 6738 项 0 失败；物理点击菜单两下折叠、按钮首点、历史目录导航、真实图标验证 |


| 2026-11-22 10:30 | 1.3.12 去掉状态文字/按钮稳定/接触边 2px | FileDialogBarWindow.xaml(.cs) | 构建 0 警告 0 错误；自检 6738 项 0 失败；按钮 X 前后一致、菜单两下折叠、gap 重叠测量 |
| 2026-11-22 12:00 | 跟随 R1/R2：事件驱动 + 几何/摆位分离 | `UI/Windows/FileDialogBarWindow.xaml.cs`（删 `_followTimer`；`UpdateBounds` 同回调 `ApplyPosition`；新增 `ComputeGeometry`/`ApplyPosition`） | 构建 0 警告 0 错误；自检 6718 项 0 失败 |
| 2026-11-22 12:00 | 跟随 R3/R4：尺寸恒定 | `FileDialogBarWindow.xaml`（`SizeToContent=Manual`）、`.xaml.cs`（高度只在 `ComputeGeometry` 定；拖动中 `SWP_NOSIZE`） | 自检「拖动中窗口尺寸零变化」PASS（438×128 全程不变） |
| 2026-11-22 12:00 | 跟随 R5/R6：去自激 + 统一 NOZORDER | `FileDialogBarWindow.xaml.cs`（`_inApply` 重入闸、`SizeChanged` 不再排几何、`SWP_NOZORDER`） | 自检「拖动中没有再算几何」「_inApply 期间没有排跟随」均 PASS |
| 2026-11-22 12:00 | 跟随 R7：前台事件链 | `Core/FileDialog/FileDialogBarService.cs`（BeginInvoke 提到 Input；收起后立刻重试 + 30ms 重试；拖动中不因前台离开收起） | 构建通过；日志新增「浮层立即重显示」分支 |
| 2026-11-22 12:00 | 跟随 R8：度量与自检 a–d | `SelfTest.FileDialogBar.cs`（`TestDialogBarFollow`）、`FileDialogBarWindow`（p50/p99/误差/计数）、`AppSettings.cs`（`FollowMetrics`） | 60 次连挪：p50 0.35ms / p99 1.01ms / 误差 0px / 尺寸不变 / 几何重算 0 / 自激 0 |
| 2026-11-22 12:10 | 设置项搬家：「文件对话框浮层」进「文件搜索」页 | `UI/Views/QbarSettingsView.xaml(.cs)`（卡片从右栏移到文件搜索页末尾，加页内目录 chip、跟随度量开关）、`RenderTools.cs`、`QbarSettingsView.ScrollToFileDialogBar` | 渲染 `qbar-filedialog-settings.png` / `qbar-files-tab-bottom.png` 确认卡片独立成项、目录 chip 对齐 |
| 2026-11-22 12:15 | 文档：跟随根因与修复 | `ANCHORING.md` §3.1、`DECISIONS.md`（分层窗口保留理由）、`PROGRESS.md` | 与上表同批 |

## v1.6.0 — 文件索引换内核（结论）

文件搜索的索引构建、增量、查询、排序全部搬进本进程（Rust cdylib + vendored tantivy），
进程外索引依赖清零；只支持 Windows 11 x64。详见 `docs/qbar/TANTIVY-MIGRATION.md`
与 `docs/qbar/DECISIONS.md` 的 D56–D65。
实测：67.7 万条建索引 23.1 s、索引 206 MB、查询 p50 7.4 ms / p95 21.1 ms、
增量 230 ms / 1656 ms；`--selftest` 7086 项全过。