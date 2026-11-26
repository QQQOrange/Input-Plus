# DECISIONS

本文件记录两个功能的每一个技术选择与理由。格式：**决定 / 理由 / 备选与放弃原因**。
时间顺序追加，不回头改写已落地的决定（改主意就追加一条「推翻」）。

---

## 一、工程勘察结论（2026-11-21）

### 1. 技术栈
- **语言/框架**：C# / WPF（.NET），`src/InputPlus/InputPlus.csproj` 多目标 `net10.0-windows;net7.0-windows`，
  `UseWPF + UseWindowsForms`，`WinExe`，`PublishSingleFile`，RID `win-x64`。
- **依赖**：Interop.UIAutomationClient 10.16299.0（UIA 已有）、System.Management、TinyPinyin.Net、MoonSharp。
- **构建**：SDK 10.0.402；`dotnet build src/InputPlus/InputPlus.csproj -c Debug -f net10.0-windows` 基线 0 警告 0 错误。
- **DPI**：app.manifest 已声明 PerMonitorV2，新增窗口不需要再动清单。

### 2. qbar 窗口怎么创建
- `UI/Windows/QbarWindow.xaml(.cs)`（约 3400 行），`WindowStyle=None`、`AllowsTransparency=True`、
  `ShowInTaskbar=False`、`Topmost=True`、`SizeToContent=Height`；根 `Grid Margin=18`，
  内部 `Border x:Name="Panel" CornerRadius=18`，画刷走 `Brush.OverlayCard/OverlayBorder/OverlayText`，
  字体 `StaticResource Font.Ui`。
- 入口：`Open(string?)` / `OpenFileSearch` / `OpenDictionary` / `HideQbar` / `ResetPosition` / `InvalidateStyle`。
- 服务层 `Core/Qbar/QbarService.cs:87 QbarService`，由 `UI/AppHost.cs:116` 装配：
  `Qbar = new QbarService(() => Settings, () => Clipboard.History, () => Actions.Items)`；
  `ShowQbar` 在 `UI/AppHost.Windows.cs:114`，`EnsureQbar` 在 `:196`。

### 3. 现有文件搜索入口
- 内核：`Core/Qbar/Search/QbarSearchEngine.cs`。
  `FromSettings(AppSettings)`（:206）建 `QbarQueryContext`，`Run(context, input, limit)`（:219）返回
  `QbarSearchOutcome { Hits, Translation, RawCount, HiddenIgnored, ... }`，`QbarSearchHit(Path, IsDir, Score, Tier, SizeBytes, Day)`。
- 选项 `QbarSearchOptions`（`Core/Qbar/Search/QbarSearchModels.cs:90`）：
  `MaxCandidates=2000`、`PreferEverything`、`PinyinEnabled`、`DebounceMs=45`、`QueryTimeoutMs=1200` 等。
- 空态入口唯一：`QbarService.cs:708 DefaultResults(AppSettings)` —— 只有一个地方要接「建议」。
- Everything：已有封装（`--everything-status` 可自检），搜索内核内部按 `PreferEverything` 走，**不新起进程、不建第二份索引**。

### 4. 全局热键与窗口监听
- 热键：`UI/GestureService.cs:956` 一带 `RegisterHotKey(_hotkeySource.Handle, id, mods, vk)` + `HwndSource.Hook`。
- 窗口监听：`Core/Windows/ForegroundWatcher.cs:23` 已有 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, OUTOFCONTEXT|SKIPOWNPROCESS)`，
  可直接照抄其「委托保活 + 钩子卸载」写法；`ProcessNameOf` / `TitleOf` 已封装跨进程读取。
- 输入合成：`Core/Input/InputSender.cs`（Tap/Chord/Ctrl/Text）、`Core/Text/ThreadKeyboardSender.cs`（AttachThreadInput + 键盘状态）。
- 剪贴板：`Core/Text/ClipboardBackup.cs`（Capture/Restore，带文本守卫与序号校验）。

### 5. 风险点（决定实现取舍）
| 风险 | 结论 |
| --- | --- |
| 跨进程 `WM_SETTEXT` 写控件文本 | 不可靠（Vista 对话框是 DirectUI 自绘，老式的会校验内部状态）→ 只走 UIA ValuePattern 或键盘+剪贴板 |
| 抢前台被系统拒绝 | 前台锁 `SetForegroundWindow` 会失败 → 必须做 5 级降级阶梯，失败只记日志 |
| 常驻键盘钩子 | 会拖慢整机输入、易被杀软标记 → 只在第 4 级临时装、立刻卸 |
| 目标提权（UIPI） | 未提权进程拿不到 UIA/按键通道 → 判定不可用并记日志，不做任何绕过 |
| 目录枚举阻塞 UI | 建议来源必须在专用后台线程，首屏先出缓存 |
| 老配置缺字段 | 新选项默认值同时写在属性初始值上，反序列化缺字段即取默认 |
| 单元测试依赖真实桌面 | 判型/策略/合并/排序全部做成纯函数，真实窗口只做少量烟雾测试 |

---

## 二、Task 1：文件对话框搜索栏

### D1-01 五层拆分，每层只暴露一个窄接口
- **决定**：`FileDialogWatcher`（发现）→ `DialogClassifier`（判型，纯函数）→ `FileDialogBarWindow`（浮层）
  → `DialogBackfill`（回填）→ `DialogRecorder`（记录），由 `FileDialogBarService` 编排。
- **理由**：判型和策略是纯函数，能穷举自检；Win32 细节只在 Watcher/Backfill；Bar 不认识 Win32 句柄操作，
  回填不认识 UI。三层互相替换不影响其他层（例如 Bar 换成 WinUI 也不用碰回填）。
- **放弃**：一个 `FileDialogBarManager` 包干 —— 判型、定位、注入、剪贴板全糊在一起，没法单测。

### D1-02 判型按「类名 + 子控件组合」，绝不为 exe 写分支
- **决定**：`DialogClassifier.Classify(className, children)` 按 a→e 顺序命中即停。
- **理由**：同一程序会用不同家族的对话框（微信 4.x 用 Vista 对话框，老下载工具用 comdlg32），
  而同一家族被成百上千程序共用；按 exe 写分支等于每支持一个程序发一次版。
- **实测佐证**：WinForms `SaveFileDialog` 的窗口是 `#32770`，含 `DirectUIHWND` + `SHELLDLL_DefView`，
  文件名框是 `Edit id=0x03E9`（=1001，与 UIA AutomationId 一致），同时**还藏着**一个不可见的
  `ComboBoxEx32/ComboBox/Edit id=0xA205`（地址栏）。所以规则顺序必须是 a 先于 b，否则会把 Vista 对话框误判成老式。

### D1-03 Watcher 只用 OUTOFCONTEXT 钩子，不注入
- **决定**：`SetWinEventHook(EVENT_OBJECT_CREATE..LOCATIONCHANGE, WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS)`，
  只处理 `idObject=0 && idChild=0`。
- **理由**：OUTOFCONTEXT 的回调由系统投递到安装线程的消息循环，目标进程完全不被碰；SKIPOWNPROCESS 保证
  不会把自己的 WPF 对话框当成候选（否则自己套自己）。
- **放弃**：`SetWindowsHookEx(WH_CBT)` 全局钩子 —— 要注入所有进程，崩溃风险与红线冲突。

### D1-04 去抖 500ms + 销毁清理，兜底用 300ms 顶层枚举
- **决定**：同 HWND 500ms 内只处理一次；`Forget` 从跟踪表移除；`PollOnce()` 只枚举**顶层** `#32770`。
- **理由**：CREATE 与 SHOW 前后脚到达，不去抖会建两次浮层并互相覆盖状态；SHOW 事件在远程桌面/跨会话会丢，
  低频枚举是唯一不依赖注入的补偿手段。轮询里递归子窗口会卡 UI，明确禁止。

### D1-05 判型阶段短路开关，不先建窗口再隐藏
- **决定**：`DialogPolicy.ShouldAttach(info, classification, masterEnabled, typeEnabled, disabledProcesses, out reason)`
  在判定阶段返回 false，并在日志里写清理由（总开关/类型开关/进程禁用/Unknown）。
- **理由**：先建后隐会留下 WPF 窗口与句柄，验收用例 18/19 要求「禁用后不再出现」且「无后台轮询残留」。

### D1-06 新增 `--dialogprobe` 诊断入口
- **决定**：`InputPlus.exe --dialogprobe` 打印所有顶层 `#32770` 的子控件清单与判型结论，写 `logs\dialog-probe.txt`，
  **不弹窗**。
- **理由**：M1 的验收就是「对任意对话框正确打印类型与子控件」；探针必须能在真实第三方对话框上跑，
  且红线禁止用模态框打断用户。实测已跑通（见 ACCEPTANCE.md M1）。

---

### D1-07 浮层只有一套，owner 指向对话框，NOACTIVATE 起步
- **决定**：`FileDialogBarWindow` 一套 XAML；`ShowFor` 里用 `SetWindowLongPtr(GWLP_HWNDPARENT, dialog)` 认 owner，
  加 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`，`Topmost=False`。
- **理由**：owner 关系让浮层永远压在对话框之上、跟着对话框最小化，且不进 Alt+Tab；
  不用 Topmost 是因为红线明确禁止"覆盖所有窗口"的 always-on-top。
  先 NOACTIVATE 保证不抢对话框的输入，只有走到焦点阶梯才尝试取键盘。

### D1-08 取键盘做五级阶梯，最后一级才临时装钩子
- **决定**：`DialogFocusLadder.TryTakeKeyboard`：SetForegroundWindow → AttachThreadInput 重试 →
  模拟 Alt 解锁 → 返回 `NeedsKeyboardHook`（由 Bar 装 `WH_KEYBOARD_LL` 把字符重定向进输入框，
  状态栏显示"输入中"，15 秒无输入或失焦即卸）。
- **理由**：Windows 的前台锁不是"能不能抢"的二元问题，逐级降级能把绝大多数场景留在前三级（无钩子）；
  钩子只在第 4 级、且用完立即卸载，满足红线"不常驻全局键盘钩子"。
- **放弃**：一上来就装钩子 —— 会拖慢整机输入，且被杀软重点关照。

### D1-09 回填三条路线，全部收在 DialogBackfill 里
- **决定**：Vista 家族先 UIA `ValuePattern.SetValue`；失败退"聚焦 + Ctrl+A + 剪贴板粘贴 + 提交"；
  老式家族从"编辑器/键盘"起步；文件目标回车提交，文件夹目标回车进目录后点/Invoke「选择文件夹」。
- **理由**：UIA 最稳（不动剪贴板、不受输入法影响），但不是每个对话框都给 ValuePattern；
  键盘路线是通用兜底。文件夹目标的回车语义是"导航"而非"确认"，必须再按确认按钮。
- **放弃**：跨进程 `WM_SETTEXT` —— DirectUI 自绘控件不认，老式对话框还会校验内部状态。

### D1-10 成功判据看"对话框的反应"，不看"框里的字"
- **决定**：`Verify` 在 2 秒内接受三种证据之一：对话框已销毁 / 对话框上弹出了确认框 /
  文件夹框内容已被清空（说明已进入该目录）。否则失败。
- **理由**：写完 ValuePattern 再读回来等于同义反复 —— 值本来就是自己写进去的。
  用户视角的"被接受"只有对话框真的响应。实测中这条判据把"写完没提交"和"真的成功"分开了。

### D1-11 剪贴板备份/还原直接复用 ClipboardPaster
- **决定**：不另写剪贴板逻辑，`DialogBackfill` 调 `ClipboardPaster.TryPaste`（内部 Capture → SetText →
  Ctrl+V → Restore，带文本守卫与序号校验）。
- **理由**：这套逻辑已在 qbar 的粘贴路径上跑了很久，重复实现只会多一份 bug；
  红线要求"剪贴板必须备份恢复"，集中在一处才能保证每条路线都遵守。

### D1-12 记录用 JSONL，只记路径与来源
- **决定**：`DialogRecorder` 追加 `DataDir\dialog-history.jsonl`，一行一条
  `{ts, surface:"file_dialog:<exe>", path, kind, route}`，内存保留最近 200 条。
- **理由**：追加写不会因为半行损坏整个文件；排查"某程序为什么失败"时 surface 直接指向进程。
  只记路径/类型/路线，不碰文件内容，符合红线。

### D1-13 文件夹选择框靠 UIA 文案识别，而不是只靠判型
- **决定**：`DialogUia.IsFolderPicker` 检查是否存在 Name 含"文件夹/Folder"的 Edit；
  `DialogBackfill` 把"选中的是文件夹 + 该窗口是文件夹选择框"也当作 folderTarget。
- **理由**：Win11 的 `FolderBrowserDialog` 实际是 Vista 家族（DirectUIHWND + SHELLDLL_DefView），
  五条判型规则只会判成 FileDialog；两者提交动作不同（回车 vs 选择文件夹），只靠判型会点错。
  实测：识别后 e2e 的文件夹用例从 FAIL 变 PASS（返回 `...\ip-filedialog-e2e`）。

### D1-14 用 `--filedialog-e2e` 跑真对话框矩阵
- **决定**：命令行入口起 5 个真对话框（Vista 保存 ×2、老式、文件夹选择、超长路径），
  每个用例以"对话框自己返回的路径"为判据，报告写 `logs\filedialog-e2e.txt`。
- **理由**：回填是唯一真的操作别人窗口的一段，单测只能覆盖路径推算；
  矩阵跑在 STA 线程里、不装浮层、不进托盘，可随时回归。
- **踩坑**：用例之间必须等对话框真的消失（`CloseAndWait`），否则下一个用例会抓到上一个残留窗口；
  超长路径用例还会先弹系统确认框，一条 WM_CLOSE 只关确认框。

---

## 三、Task 2：搜索前建议（Suggestions）

### D2-01 六层单向依赖：Sources → Item → Merger → Rules → Blacklist → Presenter
- **决定**：`IRecommendationSource` 只产出 `RecItem`；`RecMerger` 只做去重/排序；
  `RecRules` 只做过滤；黑名单在 Service 层落盘后重放；Presenter 就是 `QbarService.DefaultResults`。
- **理由**：每层都能单独喂数据自检（77 条断言里大部分是纯函数），加来源不用碰排序，改规则不用碰 UI。
- **放弃**：一个 `SuggestionManager` 全包 —— 目录枚举和 WPF 列表混在一起，没法在无 UI 的进程里测。

### D2-02 路径规范化 + 权重胜者去重，徽标合并
- **决定**：`RecItem.NormalizeKey`：Trim、`/`→`\`、去掉尾部 `\`（盘根保留）、`ToLowerInvariant`；
  同 key 保留 `SourceWeight` 最高者，其余来源并入 `MergedSources`，`Badges` 去重合并。
- **理由**：同一文件常同时出现在"下载"和"Windows 最近"，不去重会出现两行；
  只保留权重高的那条，徽标显示"下载·最近"，用户仍知道它是多条来源命中的。

### D2-03 八个来源、固定权重，不做"猜你喜欢"
- **决定**：downloads 70 / desktop 65 / wechat 80 / wecom 75 / feishu 75 / windows-recent 90 /
  dialog-history 100 / app-history 110；排序主键 `Modified` 倒序，次键权重。
- **理由**：用户能理解"最近改过的排前面"；权重只用于同一时间戳的次序与去重取舍。
  引入模型会让排序不可解释，也违背参考实现"不引入猜你喜欢"。

### D2-04 探测只查目录是否存在，枚举在后台线程懒加载
- **决定**：`Probe` 只做 `Directory.Exists`（毫秒级）；`Collect` 在专用 STA 后台线程，
  `SuggestionBudget` 限制条目数与耗时，`DirectoryScan.Newest` 逐层枚举、边枚举边看预算。
- **理由**：启动器唤出路径上不能有磁盘 IO；某来源目录不存在时直接计 0，不报错。
  线程 `Priority=BelowNormal`，不影响前台输入。

### D2-05 采集线程设 STA
- **决定**：`SuggestionsService` 的采集线程 `SetApartmentState(ApartmentState.STA)`。
- **理由**：`IShellLink`/`IPersistFile` 是 apartment-threaded COM，MTA 线程上 `Load` 行为不稳定；
  STA 是解析 .lnk 的最低成本正确做法。

### D2-06 ShellLinkReader 用 StringBuilder 而不是 char[]
- **决定**：`IShellLinkW.GetPath` 等接口的缓冲区参数统一用 `StringBuilder`（1024），`SLGP_RAWPATH`。
- **理由**：实测 `[Out, MarshalAs(UnmanagedType.LPWStr)] char[]` 在 .NET 10 上封送不回收，
  228 个 .lnk 全部解析成 null；换 StringBuilder 后 windows-recent 从 0 条变 77 条。
  新增 `TryTargetOf(..., out error)` 让失败原因可见，而不是静默 0。

### D2-07 合并排序：Modified 倒序 → 权重 → 名称长度
- **决定**：`OrderByDescending(Modified).ThenByDescending(SourceWeight).ThenBy(DisplayName.Length).ThenBy(DisplayName)`；
  候选硬上限 2000，展示 20。
- **理由**：名称长度作为最后的稳定键，让短名（更像用户想找的）在同刻同权重时靠前；
  2000 上限防止大目录把采集线程拖死（验收 17）。

### D2-08 规则顺序：空路径 → 黑名单 → 系统目录 → 扩展名 → 白名单
- **决定**：`RecRules.IsExcluded` 按上述顺序返回第一个命中理由；系统目录用**目录边界**比较。
- **理由**：黑名单优先于系统目录，用户手动隐藏的项永远不出现；
  边界比较避免 `C:\WindowsApps` 被 `C:\Windows` 误伤（自检有专门用例）。

### D2-09 配置用 delta 增量落盘，默认值写在代码里
- **决定**：`suggestions.delta.json` 存 `{version, ops:[{op,key,value,ts}]}`，
  超过 200 条时按 key 压缩成最后一条；启动时 `Replay` 到 `SuggestionsOptions` 上。
- **理由**：设置项会越来越多，整份快照写盘容易把旧版本字段覆盖掉；delta 只表达"用户改了什么"。
  默认值硬编码在属性初始值，旧配置缺字段即取默认，红线 8（无配置时行为不变）自然成立。

### D2-10 来源开关的持久化语义是"禁用列表"
- **决定**：`SuggestionsOptions.DisabledSources`；`Sources` 保留为兼容字段，空 = 全开。
  设置页勾选状态 = 全部来源 − DisabledSources。
- **理由**：一开始把"勾了哪些"写进 `Sources`，结果关掉一个来源重启后失效（空列表被当成全开）。
  改成禁用列表后，新增第 9 个来源在旧配置里默认启用 —— 这是"来源集合会增长"的正确默认。

### D2-11 首屏先缓存后采集，缓存连来源计数一起存
- **决定**：`suggestions-cache.json` 存最多 40 条 + `CollectMs` + `SourceCounts` + `Failures`；
  `Start()` 先 `LoadCache` 再 `Rebuild`，后台线程采集完再 `Changed`。
- **理由**：红线要求唤出到建议可见 ≤80ms。实测缓存命中时 `FirstScreen` 0.3ms、20 条；
  冷启动 `Start()` 51ms、首屏 0 条、297ms 后台补上。计数一起缓存让设置页统计行在温启动时也有内容。

### D2-12 建议与搜索结果共用同一个 ListBox，靠分组标题切换
- **决定**：不新增第二个列表；`QbarService.EmptyStateGroup` 只在"本次空态真的带建议"时非空，
  QbarWindow 用它显示"最近文件"标题；`QueryInternal` 入口无条件清空它。
- **理由**：红线 6 要求两套列表互斥 —— 同一个 ListBox 从结构上就不可能同时出现。
  踩过的坑：只在空态路径重置标题，导致先空态再输入时标题粘在搜索结果上，已修。

### D2-13 建议行徽标用来源徽标，不用 KindLabel
- **决定**：`QbarRow` 构造时，若 `result.Suggestion` 非空且 `Badge` 非空，徽标用 `result.Badge`
  （下载 / 桌面 / 下载·最近）。
- **理由**：否则所有建议行都显示"文件"，用户看不出"这是最近用过的"。

### D2-14 已知文件夹（下载）读注册表而不是拼路径
- **决定**：`KnownFolders.Downloads()` 读
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders` 的
  `{374DE290-123F-4565-9164-39C4925E467B}`，展开环境变量，失败退回 `%UserProfile%\Downloads`。
- **理由**：本机 Downloads 被重定向到 `D:\Downloads`，硬拼 `%UserProfile%\Downloads` 恒为 0 条。
  只读注册表，不写任何系统配置。

### D2-15 自检 + 探针两条腿
- **决定**：`SelfTest.Suggestions.cs` 覆盖纯函数与降级（77 条）；
  `--suggestions-probe` 打真实首屏/采集耗时与各来源条数，报告写 `logs\suggestions-probe.txt`。
- **理由**：纯函数自检能穷举边界，但"这台机器上到底有几个来源有数据"只能实测；
  探针不弹窗、不启动 UI，退出码 0/2/3 可脚本化。


### D1-15 1.3.5：浮层默认显示与位置（用户反馈修正）
- **决定**：`ShowOnAppear` 默认改为 true，老配置用 `DefaultsVersion` 迁移一次；位置改为钉在对话框下沿**外侧**，列表向下展开并按物理工作区封顶；自动显示带 `WS_EX_NOACTIVATE`，鼠标按下时才去掉并聚焦输入框；Alt+Enter 在资源管理器定位，回填失败也自动定位。
- **理由**：1.3.4 默认 false 让用户打开「另存为」看不到任何东西；"放不下就翻到上方"在列表展开时会把浮层顶到屏幕顶部；NOACTIVATE 窗口点上去不激活，不显式聚焦就永远打不了字。
- **备选**：贴在对话框内部底边 —— 会盖住「保存 / 取消」和文件名框，放弃。

### D2-16 1.3.5：建议设置放进「文件搜索」页
- **决定**：新增页内目录「搜索前建议」，卡里放来源表（启用 + 权重 + 上次采集状态）、空态条数、候选上限、采集预算、白名单/排除扩展名、隐藏热键、已隐藏列表、立即重采。
- **理由**：用户原话"应该在文件搜索下面的子目录里面得到体现"；它和过滤器/优先级同属"搜什么、怎么排"，右栏放不下且信息架构不对。


### D1-16 1.3.6：浮层宽度取对话框 1/3、左下对齐
- **决定**：`widthPx = dialogW / 3`，`x = dialog.Left`（减阴影边距让面板左缘贴住），`y = dialog.Bottom` 下方展开；列表向下，空间不够向上。
- **理由**：用户明确要"左下角下面的一条"，铺满整宽会挡住对话框底部的文件名/按钮，也不像 qbar。

### D2-17 1.3.6：来源顺序
- **决定**：`Suggestions.SourceOrder` 保存 id 顺序；Merge 在顺序非空时先按来源 rank 分组、组内 modified 倒序；空顺序保持全局 modified 倒序。
- **理由**：用户要求"设置里排好，Qbar 就按这个顺序展示"；保留空顺序的旧行为，避免升级后排序突变。UI 用独立"来源顺序"列表 + 上移/下移/恢复默认，不动态重排来源表。


### D1-17 1.3.7：浮层居中 + 真实 shell 图标
- **决定**：宽度仍 1/3，`x = dialog.Left + (dialogW - widthPx) / 2` 水平居中；结果行用 `IconCache.FromFileAsync`（底层 SHGetFileInfo/系统图像列表）取 18px 图标，只给前 60 行排队。
- **理由**：用户要求回到中间；图标必须和资源管理器一致，不能再用通用字形。前 60 行限制避免一次 200 条结果把 shell 图标线程打满。

### D2-18 1.3.7：拖拽排序取代独立排序区
- **决定**：来源表改成 `ItemsControl` + `ObservableCollection<SuggestionSourceRow>`；行本身是拖拽手柄（复选框以外区域），拖完 `Move` 集合并写入 `SourceOrder`。删除「来源顺序」列表、上移/下移/恢复默认按钮。
- **理由**：用户明确"直接在来源这里面拖，不要又搞一个排序窗口/按钮/特殊标志"；集合顺序即 UI 顺序，单一数据源，避免两套顺序不同步。


### D1-18 1.3.9：QuickSwitch 布局与导航
- **决定**：宽度 3/5、水平居中，始终贴对话框下端；下方空间 <120px 时收进内侧底部（输入在下、列表向上），绝不翻到上方。接触边 0 透明缝、直角。导航优先 SetFolder（需 IFileDialog 指针），无注入时用 UIA 写文件名框 + Enter，老式键盘+剪贴板，导航后不关对话框。
- **理由**：用户两次强调"紧贴最下端、不要最上端"；不注入进程就拿不到 COM 指针，回退路径是唯一不越红线的方式。

### D2-19 1.3.9：QuickSwitch 数据与隐藏
- **决定**：收藏放 FileDialogBar.Favorites；历史只读 RecentTargets 聚合成本地缓存，清空不删源；菜单隐藏分仅本次（HWND）/按进程/全局开关三种，全部即时持久化。
- **理由**：不新造第二套历史；清空必须可恢复原始数据；隐藏语义要和用户预期一致。

### D1-19 1.3.13：浮层为什么还是分层窗口 / 跟随为什么改成事件驱动
- **决定**：保留 `AllowsTransparency`（分层窗口）；拖动中只 `SetWindowPos(SWP_NOSIZE | SWP_NOZORDER)`，几何（宽度/列表高/翻转/圆角）只在对话框尺寸变化、内容变化、停手 200ms、DPI 变化或设置变化时重算。
- **理由**：① 去掉分层窗口就拿不到窗口外那圈柔和阴影和 12px 圆角（Win10 没有 DWMWCP_ROUND；Win11 的系统圆角也只对带边框的窗口生效），浮层会变成一块硬边白条，视觉上直接退化；② 实测旧路径每个 16ms tick 的重排成本 4.4ms，而拖动掉帧的主因是"每帧改尺寸 → 整窗重合成"，不是"移动"本身。尺寸恒定后一次移动只要 0.35ms（60 次连挪 p99 1.01ms），没必要为了省这点开销牺牲外观。
- **代价**：拖动过程中窗口仍是分层表面，DWM 每次移动要重定位一次图层（实测无掉帧、无拖影）；列表不再随可用空间"平滑长大"，改为停手 200ms 后一次到位。
- **回退**：若以后必须去掉分层窗口，把 `AllowsTransparency` 关掉、阴影改成外侧九宫格窗口即可 —— 跟随路径已经和窗口形态解耦（`ApplyPosition` 只认几何 + 矩形）。

---

## 十一、Task 3：中键在空白处弹菜单（v1.3.16，2026-11-22）

### D3-01 复用 `GestureMenuWindow`，不新建菜单窗口
- **决定**：中键菜单就是用 `new GestureMenuWindow(def, ActionCatalog.TitleOf)` 弹出来的；条目仍然是一个普通的「动作 id + 参数」，选中后走 `AppHost.InvokeAction`，和绑键、手势菜单完全同一条执行路径。
- **理由**：那个窗口已经解决了一串不好重写的问题 —— Topmost + `AllowsTransparency` 自绘卡片、光标处定位 + 贴边翻转 + 多屏 DPI 物理像素换算（`PlaceAtCursor`）、1..9 快选、↑↓ 绕回、Enter / Esc、`Deactivated` 自动收起、`ShowActivated` 拿键盘焦点、列表项 `PreviewMouseLeftButtonUp` 才执行。复制一份 Window 等于把这七件事重做一遍，还多一份要跟着主题 / DPI 改的 XAML。
- **备选与放弃**：① 复制一份 `MiddleMenuWindow` —— 只多一个入口，却要长期维护两套弹窗；② 用 WPF `ContextMenu` —— 做不了 1..9 快选，也没有自己的激活状态（点别处收起得靠全局钩子）；③ 把 `GestureMenuDef` 改成可变对象再往运行时塞条目 —— 会污染存盘结构，造一个临时实例（`Id = "middle.runtime"`，不落盘）就够了。

### D3-02 非模态 `Show()`，不用 `Pick()` 的模态 `ShowDialog()`
- **决定**：`GestureService.MiddleMenuOpen` 自己 `new` + `Show()`，`Closed` 里读 `Selected` 再执行动作；`GestureMenuWindow.Pick` 保持原样（手势菜单那条路径还在用）。
- **理由**：模态会开一个嵌套消息循环 —— 菜单开着时再按中键会**重入**，弹出第二个菜单；而"再按中键关掉"正是这个功能明确要的行为。非模态下 `Accept()` 里 `DialogResult = true` 会抛 `InvalidOperationException`，那里本来就有 `catch → Close()`，一行键盘交互都不用改。
- **备选与放弃**：给窗口加一个 `bool modal` 开关 —— 那就动了现有类，而现有类跑得好好的。

### D3-03 命中判定放在**按下**，抬起零等待
- **决定**：`MiddleMenuArm` 在按下时算一次命中并记进 `_middleTap`；抬起（路径 A）或 `Finish()` 的「没成笔」分支（路径 B）只读结论，不再做任何跨进程查询。
- **理由**：抬起路径一旦要等 UIA，菜单就会晚几十毫秒才出现，而且那是在钩子回调里等 —— 低级钩子超时（`LowLevelHooksTimeout`，默认 300ms）被摘掉的风险跟着来。按下时用户还按着，几十毫秒完全无感。两条路径共用同一次判定，以**按下点**为准。
- **代价**：路径 B（用户配了中键手势）每次按下多花一次判定（桌面 <1ms，资源管理器 5~30ms）。首帧菜单仍是 WPF 首次建窗成本，但那部分和手势菜单完全一样，不是这次新增的。

### D3-04 桌面走 `LVM_HITTEST`，资源管理器走 UIA，两条通道都判"问不出来 = 不弹"
- **决定**：桌面图标列表是真 `SysListView32`，跨进程发 `LVM_HITTEST`（远程分配 + `SendMessageTimeout` 25ms，`iItem == -1` 才算空白）；Win10/11 资源管理器是 DirectUI，用 `AutomationElement.FromPoint` 往上找三层看是否为列表项。返回 `Unknown`（超时 / `OpenProcess` 被拒 / 跨位数）一律按"非空白"处理。
- **理由**：能问控件就直接问，别自己算图标网格（字号、缩放、对齐方式全都会让它失准）；问不出来时"这次不弹"是唯一不会误伤的行为。
- **跨位数是硬门**：≥ `WM_USER` 的带指针消息不做跨位数封送，32 位进程往 64 位 Explorer 发 `LVM_HITTEST`，对方会解引用野地址。所以 x86 版（Win7 专线）在 64 位桌面上就是**没有**这个功能 —— 比让 Explorer 崩掉好。

### D3-05 UIA 跑在常驻 STA 线程上，带超时 + 30 秒冷却
- **决定**：`BlankHit.UiaWorker` 一条 `IsBackground` + `SetApartmentState(STA)` 的常驻线程，调用方 `Monitor.Wait(timeout)`；忙（上一次还没回来）就直接放弃、不排队；超时过的窗口句柄进 30 秒冷却，这期间直接判 `Unknown`。
- **理由**：UIA 是跨进程 COM，目标卡住会一直阻塞；每次新开线程又要重新初始化 COM（首次 50~150ms）。常驻解决"慢"，不排队解决"卡"，冷却解决"反复卡同一个窗口"。
- **备选与放弃**：把 UIA 丢线程池 —— 线程池线程是 MTA，`AutomationElement` 在 MTA 上会抛（必须在 STA 上创建 / 使用）。

### D3-06 菜单独立于「鼠标手势」总开关；出厂关闭
- **决定**：`GestureOptions.Enabled = false`（关掉全部鼠标手势）时中键菜单**照样能用**；菜单自己只有 `MiddleClickMenuEnabled` 一个开关，出厂 false。
- **理由**：① 两者是不同需求 —— 有人嫌手势容易误触，但乐于在桌面点一下就要一个常用入口；② 出厂关：中键在浏览器里是自动滚动、资源管理器里能开新标签、CAD/3D 里是平移，默认接管等于悄悄改掉一颗全系统都在用的键；③ 关着时老配置升级上来**零行为变化**，不需要迁移。
- **代价**：两个开关的语义要在设置页写清楚（卡片说明里已写）。

### D3-07 菜单内容：`MiddleClickMenuId` 空 = 内置动态菜单，非空 = 手势菜单里那一份
- **决定**：空串 → 每次现拼「收藏文件夹 ≤4 / 最近打开 ≤4 / 自定义命令 ≤3 / 打开设置 1」（正好 12 = `GestureMenuDef.MaxItems`）；非空 → 用 `Settings.GestureMenus` 里那个 Id 的**克隆**；指向的菜单被删了就回落到内置，不报错、不弹空窗。
- **理由**：这是两种真实需求 —— "随手点一下，给我常去的地方"和"我配好的一份固定菜单"。存空串而不是 null：缺字段、写 null、写空都落到同一个默认。克隆是硬要求：菜单窗绝不能让用户在里面改坏设置里的那一份。
- **自定义命令为什么走 `runProgram`**：`QbarCommandStore.Execute` 是 Qbar 面板内部语义（关键字 + 尾串），中键菜单没有输入框；`runProgram` 则是绑键用的同一条动作管线。代价是命令的 RunAsAdmin / Silent / 工作目录不生效 —— 需要这些的人应该把那条命令绑到键上。

### D3-08 范围存字符串、对外给枚举
- **决定**：`AppSettings.MiddleClickMenuScope` 是 `string`（`"desktop"` / `"desktopExplorer"`），代码里用 `MiddleClickMenuScope` 枚举 + `MiddleMenuScope.ToScope/ToStorage` 互转，解析失败回落 `DesktopAndExplorer`。
- **理由**：和 `GestureOptions.TargetMode` 同一条约定 —— `SettingsStore.JsonOptions` 没挂 `JsonStringEnumConverter`，用真枚举一旦遇到脏字符串就会抛，`Load()` 会走进"读取失败 → 回退默认设置"，用户看到的是"设置全丢了"。字符串只让**这一个字段**回默认，代价小得多。
- **备选与放弃**：给枚举挂 `[JsonConverter(typeof(JsonStringEnumConverter))]` —— 未知值照样抛，问题没解决。

### D3-09 慢按（起始超时）也算一次点击
- **决定**：`OnPoll` 的 `startTimedOut` 分支在 `ResetActive()` 之前问一次菜单（`MiddleMenuOnSilentCancel`）。
- **理由**：那条分支本来就会 `ResetActive()` + 吞掉抬起。不接这一刀，用户的观感是"按住中键稍久一点松手 → 菜单没弹、原生中键也没了"，是最难解释的一种行为。
- **顺带**：`ResetActive()` 现在会清 `_middleTap`，抬起丢失（安全桌面 / 锁屏）后不会把上一次的命中结论带进下一笔。

### D3-10 菜单内容整体换成"Qbar 驱动"（固定项 / 历史记录 / 命令）
- **决定**：一级条目 = 勾了「中键」的固定项 + 「历史记录 ▸」+「命令 ▸」+ 打开设置。原来那版（收藏文件夹 / 最近目标）整体删掉。
- **理由**：这三个数据源本来就在 Qbar 里维护，用户改完立刻反映到菜单；而"收藏/最近"是另一套数据，两边都要维护等于两套真相。
  另外每个条目自己能勾，解决了"固定项里混着常用和备查"这个真实问题。
- **配额**：一级 12 条（`GestureMenuDef.MaxItems`），固定项最多占 9 条，**永远给历史/命令/设置各留一格** ——
  菜单最后一条是"打开设置"，被固定项挤掉等于把用户关在菜单里。

### D3-11 二级菜单用悬停展开的 Popup，且离开父项不立刻收
- **决定**：`GestureMenuWindow` 里一个 `Popup`（Placement=Right），行 `MouseEnter` 打开/关闭；`→`/`←` 键盘也能开合。
- **理由**：指针从父项挪到右侧子项必然经过一次 `MouseLeave`，收早了根本点不到子项。
  所以收起只由"进入别的行 / Esc / 菜单关闭"触发，不由 `MouseLeave` 触发。
- **不做三级**：菜单是拿来快速点的，三层点起来比直接打字还慢。

### D3-12 图标与备注：文件用 shell 图标，网址用 favicon + 后台抓标题
- **决定**：`GestureMenuItem` 加 `IconPath` / `Note` / `Children`；窗口按 `IconPath` 解析成 `ImageSource`
  （网址走 `IconCache.GetWebIcon`，其余走 `IconCache.FromFile/FromExecutable`）。
  网址标题在**后台**抓、缓存进内存，菜单当次先不显示标题，下次打开就在了。
- **理由**：抓标题是一次网络往返，放在弹菜单的路径上就是"点一下中键卡半秒"，与"点了就出来"直接冲突。
  解析失败一律回落到字形/序号 —— 菜单不能因为一个图标没取到就缺一块。

### D3-13 设置搬到 Qbar 面板；菜单位置钉在光标且不可移动
- **决定**：三个开关（总开关 / 用哪份 / 范围）独立成 Qbar 设置里的一页「中键菜单」；`PlaceAtCursor` 改成"左上角离光标 2px"，无标题栏、`ResizeMode=NoResize`。
- **理由**：菜单内容全部在 Qbar 里配，设置跟内容放一起才找得到（手势页留着只会让人在两处找同一个开关）。
  2px 而不是 0：正好压在指针上会盖住光标，看着像菜单卡在鼠标下面；2px 又小到看不出偏移。
---

## 五、Task 2：收藏当前文件 / 打开文件所在位置（v1.5.1）

### D2-01 用 ProcessHandleInformation 单进程快照，不用 SystemHandleInformation
- **决定**：`GetForegroundWindow → PID → OpenProcess(QUERY_INFORMATION|DUP_HANDLE) →
  NtQueryInformationProcess(ProcessHandleInformation /*51*/)`，只处理这一个进程的句柄。
- **理由**：`NtQuerySystemInformation(SystemHandleInformation / ExtendedHandleInformation)`
  在 **Win11 24H2 之后非管理员调用直接返回 STATUS_ACCESS_DENIED(0xC0000022)** ——
  Process Explorer 现在要求提权就是这条。单进程快照没有这个限制，代价是对每个进程开一次句柄。
- **代价与收口**：扫 Chrome 主进程要处理几千个句柄，所以有 4096 个 File 句柄的上限 +
  每 128 个查一次取消与 800ms 预算；到点带着已有候选收工。
- **放弃**：为了"一次看全系统"去要管理员权限或装驱动 —— 一个收藏按钮不值。

### D2-02 纯数字 token 与版本词不参与首字母
- **决定**：分词后跳过纯数字（`2024`、`01`）与版本词（`版`、`版本`、`ver`、`version`）。
  `v2` 不是纯数字，仍取 `v`。
- **理由**：年份/版本号不是用户的检索锚点 —— 用户记得的是「Q3 报告」不是「2024」。
  `2024 Q3 Report-v2` 得到 `qrv` 比 `2qr` 好按也好认。
- **实测缺口**：`测试1.2版` 按"中文段逐字取首字母"会得到 `csb`，而验收口径要 `cs` ，
  多的那个"版"正是版本后缀；把它和纯数字归为同一类，规则才自洽。
- **放弃**：按"词性/位置"判断版本号（第一个 token 才跳、最后一个才跳）——
  规则越细越难解释，用户手改一次就得重新理解。

### D2-03 打开位置用 SHOpenFolderAndSelectItems，explorer /select 只做退路
- **决定**：未提权时先 in-proc 调 `SHParseDisplayName + SHOpenFolderAndSelectItems`；
  失败才退 `explorer.exe /select,"完整路径"`；两者都失败再 `SideBySide` 打开父目录。
- **理由**：`SHOpenFolderAndSelectItems` 能**复用已打开的同名文件夹窗口**（不闪新窗）、
  自带选中、对库文件夹与网络位置的处理由 shell 负责、路径里的逗号空格完全安全。
- **实测**：真机上它把已经开着的 `%TEMP%` 窗口导航到了目标子目录并保持前台，没有开第二个窗口。
- **放弃**：`Process.Start(文件路径)` / `ShellExecute(文件路径)` —— 那是**执行**文件，
  不是打开文件夹；提权之后还会变成"用管理员身份打开这个文档"。

### D2-04 不启用 SeDebugPrivilege；提权时"借 shell 之手"优先于 in-proc 选中
- **决定**：识别失败的错误码 5 一律静默降级，绝不提权、不弹 UAC；
  `--foreground-file` 与 `RevealStrategy.Plan(elevated)` 是唯一按身份分叉的两处。
- **理由**：`OpenProcess` 的权限方向是单向的 —— 提权进程能开普通进程（所以提权后识别更准），
  普通进程开提权进程会被拒。启用 `SeDebugPrivilege` 能让 SYSTEM 进程也可读，
  但那等于把全机器的进程句柄都交到这个功能手里；为收藏一个文件不值。
  打开位置在提权时先走 `Shell.Application`（out-of-proc，medium IL）而不是 in-proc 选中，
  是为了**先保证不越权**：高完整性客户端驱动 shell 时，`SHOpenFolderAndSelectItems`
  可能被拒，而一个被拒绝的"选中"比一个正常的"打开"更让人困惑。
- **放弃**：给这个功能加"以管理员身份重启"按钮 —— 用户会以为我们在拿权限换功能。

### D2-05 缓存与锚点是两个时钟：结果 800ms，锚点 5s
- **决定**：识别结果按「前台窗口句柄 + PID」缓存 800ms；菜单弹出**之前**抓的那个窗口
  作为锚点，存活 5 秒，点击时按锚点扫，绝不重新 `GetForegroundWindow`。
- **理由**：附加菜单一 `Show()` 就会 `ForceForeground`，前台立刻变成我们自己的菜单窗口 ——
  点击瞬间再问"前台是哪个程序"只会问出 InputPlus.exe，然后收藏到我们自己的 exe。
  两个时间也量的是不同的事：800ms 是结果新鲜度，5s 是"菜单还开着"。
- **放弃**：点击时才抓前台再"猜"上一个窗口 —— 猜错就是把别人的文件收进来。

### D2-06 降级链第 3 条用本模块的内存历史，不动 RecentTargets
- **决定**：`ForegroundFiles` 自己按进程名记最多 5 条"上次识别成功"（只放内存，64 个进程上限）。
- **理由**：`RecentTargets` 记的是"我们替用户打开过什么"，**没有进程维度**，
  拿它当"哪个程序打开了哪个文件"会得到错误答案。而跨启动之后"当时开着哪个文件"
  早就过期了，写盘只有坏处。
- **放弃**：解析 JumpLists / `%APPDATA%\Microsoft\Windows\Recent` —— 那是另一件事的工程量，
  且同样回答不了"此刻这个进程开着哪个文件"。
### D2-07 星标里的"收藏当前文件"是**额外一条**，不替换原来的收藏目标
- **决定**：点星标弹出的对话框，字段与「收藏」按钮的含义一个字没改；只是在顶部多一条
  「它正在打开：xxx」+「收藏这个文件」，识别不到就整条不显示。
- **理由**：星标原来的语义是"把眼前这个**位置**收起来"（网页 / 文件夹 / 程序），
  用户在这个动作上已经形成预期。把目标悄悄换成"它打开的文件"，
  等于让同一个按钮在两个场合做两件不同的事 —— 这类改动最容易让人失去信任。
  多一条、由用户自己点，才是"丰富"而不是"替换"。
- **实现上的一处坑**：识别必须在界面线程**同步**发起
  （`GetOrRefreshAsync` 在锁里就把菜单锚点读走了），否则锚点被清掉之后
  问出来的前台是我们自己的菜单窗口，认到的文件永远是 InputPlus 自己的。
- **放弃**：让星标自己判断"有文件就收文件、没文件就收位置" —— 看起来更聪明，
  但用户没法预测按下之后会得到什么。
### D2-08 收藏按钮的判定顺序：网址 > 文件夹 > 文件 > 程序
- **决定**：星标点下去收哪个，由 `FavoriteResolver.PreferFile(source, resolvedTarget, hasFile)` 决定；
  "文件"只在网址与文件夹都不成立时才顶上来。
- **理由**（用户口径）：浏览器里的**网页地址**比它顺带下载到本地的文件更接近"用户在看的东西"；
  资源管理器窗口根本没有"打开的文件"这回事；而"程序"是最弱的兜底 ——
  用户说"收藏这个"时，他要的是正在编辑的那个文档，不是 `wps.exe`。
- **细节**：浏览器地址是 `file:///…` 时按本地文件处理（那不是网页）；
  地址读不到时也让位给文件（否则一个本地 PDF 在浏览器里就永远收不进来）。
- **放弃**：让用户在三者之间选（多一步）。判定顺序确定时，默认值就该是最可能的那个，
  字段都能改，选错了改一下就行。

### D2-09 识别链四级 + 云缓存路径不算"用户的文件"
- **决定**：① 窗口进程句柄 → ② 窗口进程命令行 → ③ 同族进程（子优先）的命令行/句柄 →
  ④ 窗口标题里的文件名 → 最近文档；`cachedata` / `INetCache` / `Temporary Internet Files`
  这类缓存目录里的路径**不作为识别结果**。
- **理由**（真机实测，2026-11-23）：
  · WPS 打开 xlsx 时，窗口进程 `wps.exe` 里 91 个文件句柄**一个都不是**那个表格；
    真正的句柄在子进程 `et.exe` 里，而且指向
    `…\WPS Cloud Files\.362964933\cachedata\<hash>\xxx.xlsx` —— 云缓存副本；
  · 该 WPS 是从开始菜单启动的，命令行里也没有路径；
  · Edge 打开本地 PDF 时，十几个子进程里没有任何一个持有它（走的是流式/内存），
    但系统「最近使用的文件」里有同名 `.lnk`，标题第一段又正好是文件名 —— 一级就够。
- **代价**：多两级枚举（Toolhelp32 快照几十微秒；最近文档最多几十个 .lnk），
  仍在 800ms 预算内，且只在前面几级失败时才发生。
- **放弃**：读 WPS 自己的最近列表（`qing.db` / 私有注册表）—— 版本一变就废，
  而且那是别人的私有格式。

---

## 六、Task 3：中键手势与"附加菜单作用范围"解耦（v1.5）

### D4-01 「范围」只回答"菜单能不能弹"，不回答"这颗键归不归我们"
- **决定**：`MiddleMenuPolicy.Decide` 里 `InScope` 下移到"没配手势"那条路。
  配了中键（或侧键）手势时，按下在任何窗口都 `StartGesture`；`InScope && Takeable`
  只决定"松手没成笔时弹菜单，还是补发原生中键"。
- **理由**：v1.5.0 把两件事合成一条 if，`OnButton` 里那句
  `!MiddleMenuInScope() → return false` 排在手势引擎之前，于是 WPS / Edge / Electron
  程序里中键**连手势都开始不了**（现场日志已确认：`hasGesture=True` 紧跟"原样放行"）。
  范围是用来保护别的程序的原生中键的，不是用来决定这颗键属谁；
  真配了手势的用户，要的就是"在哪儿都能画"。
- **代价**：范围外的一次中键点击会走"吞掉 → 40ms 后补发"，理论上存在极短的自动滚动闪一下；
  换来的是手势能用、单击不丢。补发发不出去时 `InjectionBlocked` 会整体 fail-open。
- **放弃**：给"范围外是否接管中键"再加一个设置开关 —— 用户要的是"手势在哪都能用"，
  多一个开关只会多一种"为什么我这里不生效"。

### D4-02 "吞了不还"的三条收尾一律补发，中键不再例外
- **决定**：起始超时、划动太多、抬起丢失这三条，中键与右键走同一条
  "菜单没接手就 `QueueReplayTap`"；`MiddleMenuOnSilentCancel` 返回 bool 表示菜单是否接手。
- **理由**：以前这三条都写 `if (!IsMenuButton(vk))`，等于中键"按下被吞、抬起被吞、什么都不发"，
  表现出来就是"慢按一下中键，全世界都没反应"，是最难解释的一种。
- **代价**：菜单真的接手时**不能**再补发（否则菜单 + 自动滚动一起出现），
  所以判据必须是"菜单接手了没有"，而不是"是不是中键"。

### D4-03 顶部入口的"能不能点"= 有没有认出文件，不再等于"前台不是桌面"
- **决定**：`MiddleMenuPolicy.Build` 里 `Enabled = actionable && FileName.Length > 0`；
  预热（识别）回来无论成败都回写一次（`MarkForegroundFile(fileName, favorited, hasFile)`）；
  动作层删掉"识别不到就弹手动选择"这条兜底。
- **理由**：`actionable` 只回答"前台不是桌面 / 资源管理器 / 任务栏"，它对 Electron、浏览器
  一律返回 true —— 而那些进程的句柄表里根本没有用户文件（现场：98 个文件句柄、NoCandidate）。
  于是那一条永远亮着，点下去识别失败，再"贴心"地弹一个「选择要定位的文件」。
  用户要的是"没有文件就灰"，而不是"点了以后再问我要哪个文件"。
- **实现上的两个硬约束**：
  ① 行样式必须绑**带通知**的属性（`GestureMenuEntry.IsEnabled`）—— 菜单已经上屏，
     绑普通属性 `Item.Enabled` 刷不出来，用户会看到"灰着却是亮的"；
  ② `SetEnabled` 必须同时改 `Item.Enabled`：外观和点击判据（`Accept`）不能分家，
     否则会出现"看着灰、点下去却执行"。
- **代价**：菜单刚弹出的一两百毫秒里那一条是灰的（识别还没回来）。换来的是它永远不会
  指向一个不存在的东西 —— 这个交换是用户明确要的。
- **放弃**：保留手动选择兜底、只在识别失败时提示"没找到，要不要自己选" ——
  那正是用户这次报上来的那一幕。

---

## 七、Task 4：杀软误报（v1.5.2）

### D5-01 内嵌的第三方二进制一律压成 .gz 再进程序集
- **决定**：`Assets\Everything` 的三份 PE（Everything.exe / 64.dll / 32.dll）由
  `tools/pack-everything.ps1` 压成 `.gz` 再 `EmbeddedResource`，运行期
  `EverythingBundle` 解压到数据目录；释放判据用 `.bundle` 戳记（程序集版本 + 三份资源长度）。
- **理由**（2026-11-23 真机）：火绒把编译中间产物 `obj\...\InputPlus.dll` 判成
  `Trojan/Bladabindi.c`（病毒ID `3AA5B68CC265BEFE`，操作进程 `VBCSCompiler.exe`）并删除，
  编译直接断。程序集里原本躺着三份**完整 PE**，而"托管程序夹带可执行体"正是通用
  .NET 引擎的头号特征；这个程序本身又有键盘钩子 / 截屏 / 剪贴板 / 开机自启 / 句柄枚举，
  等于把 RAT 画像凑齐了。
- **实测**：`PE\0\0` 从 11 处降到 1 处（只剩自己的 PE 头），程序集 8.37→7.47 MB（Debug）、
  单文件 exe 9.95→8.48 MB；`--everything-status` 仍显示 SDK 可用、`ext:exe` 查询 7.7 ms 命中。
- **代价**：启动首次用到 Everything 时多一次 gzip 解压（< 20 ms，且只在资源变化时发生）。
- **放弃**：索性不内置 Everything（让用户自己装）—— 那会把"搜出来永远是空的"这个
  已经被修好的坑重新挖回来；也放弃改签名/加壳（那才是真往恶意软件画像上靠）。
- **补一句**：签名误报最终要靠**信任区 + 上报**，代码只能把"看起来像木马"的部分减到最少。
## 索引内核从进程外换成进程内 tantivy（v1.6.0）

本轮与文件索引相关的自决项共 10 条（D56–D65），逐条背景/决定/被否方案写在
`docs/qbar/DECISIONS.md`。这里只留索引：范围是"文件搜索的索引与检索"，
不涉及手势、剪贴板、更新模块。

## 八、Qbar 磨砂对齐 liquiddx11 运行期（v1.6.6）

参照物是 `D:\Downloads\Compressed\liquidDX11-main`（MIT）。原则：**能抄的数值一律照抄运行期那套**
（`glass_config.inc` → `SetGlobalMaterial` → `main.cpp:380-398` 的 GlassEdgeConfig），
只有和"用户明确要求"冲突时才偏离，并且要在代码里写清楚怎么改回去。

### D6-01 tint 归 liquiddx11：0.306 + 深蓝灰，不归 Files 的 0.44 / 0.64
- **决定**：喂给 `PS_Glass` 的 `tint_opacity = 0.306 × 面板不透明度倍率`，`tint_color` 取
  `kMaterials[Thin].tint_rgb {0.17,0.17,0.21}`；亮色主题用 `SetAppearance(1)` 的
  `{0.95,0.96,0.99}` + `brightness 0.34`。Files 的 0.44 / 0.64 只留在"GPU 起不来"的自绘兜底。
- **理由**：v1.6.5 把两套口径接在了一起 —— 颜色分层按 Files（暗 0.64），模糊按 liquiddx11，
  于是 shader 拿到的 tint 是 `0.64 × 0.96 = 0.6144`：在模糊上又盖了六成纯黑。
  "面板里看不出底下在动"就是这么来的，不是 shader 抄错了。
- **代价**：透光多了，浅色壁纸/白底网页上白字对比度掉一档（原项目同样是这个透光度）。
  「面板不透明度」上限因此放到 200%（≈0.61，= 老观感），给"嫌太透"留回旋。
- **放弃**：继续叠 Files 的亮度层 —— 那正是这一轮要修的东西。

### D6-02 边缘光恢复运行期强度，但描边仍然不画
- **决定**：`highlight 0.485` / `ambient_rim 2.0` / `tbl_fill 2.0` / `sheen 0.45` /
  `sheen_amt 1.056` / 呼吸光带 `0.388` 默认全开（`SurfaceEdgeGlow = 100`）；
  `border_intensity` 恒 0。
- **理由**：v1.6.5 把这一族全压成 0，玻璃只剩"糊 + 色散"，看着像磨砂塑料；
  而 1.4px 一圈实线描边是用户前后提过三次"不要"的东西 —— 这一条压过"逐字复刻原项目"。
- **代价**：深色壁纸上，面板轮廓基本只剩投影在定形。
- **放弃**：连描边一起复刻。想复原只改一行（`AcrylicMaterial.Create` 里 `0.18f * t`）。

### D6-03 合成跟 WPF 合成节拍，抓屏 30Hz 上限
- **决定**：`DispatcherTimer(33ms)` → `CompositionTarget.Rendering`（用 `RenderingTime` 去重）；
  抓屏 ≥33ms 一次，且只在新桌面帧上跑那 13 趟模糊。
- **理由**：定时器会拍在 DWM 合成的中间，颗粒/呼吸光一顿一顿；原项目是 `Present(1,0)`、
  每帧都跑合成。抓屏 10Hz 是原项目的 `blurFps`，但 100ms 的采样延迟在"跟手"上是能看出来的。
- **代价**：UI 线程每帧多一次合成调用（4K 实测 0.2–0.4 ms）；桌面在动时抓屏从 10 次/秒涨到 30 次/秒。
- **放弃**：抓屏也拉到 60Hz —— 4K 上 13 趟 pass 纯烧 GPU，观感没有对应收益。

### D6-04 输出画布 = 面板 + 投影余量，不再开整块显示器
- **决定**：D3D9Ex 共享纹理按"面板 + 2×24 DIP"分配，1.25 倍 + 128 对齐、**只增不减**；
  shader 的 `window_size` / `window_origin` / `widget_center` 换成画布口径
  （= 原项目 `main.cpp:299-308 BeginFrame(cw, ch, origin_x, origin_y, …)`）。
- **理由**：原来开的是整块显示器（4K = 8.3M px），每帧清屏、WPF 采样、`AddDirtyRect`
  全按 4K 走，而真正出图只有 1424×544；原项目这块本来就是它自己的 backbuffer。
- **代价**：面板尺寸跨档时重建一次纹理（~10 ms，一次唤出最多一两次），靠 1.25 倍冗余摊薄。
- **放弃**：按精确尺寸分配 —— `SizeToContent` 一次唤出会改十几次高度，每次重建会看见闪。

### D6-05 排查工具：INPUTPLUS_GLASS_DUMP 导出画布
- **决定**：设了 `INPUTPLUS_GLASS_DUMP=<目录>` 就把第 30/60/90 拍的画布倒成 PNG
  （预乘 alpha 先除回直通 alpha）。
- **理由**：面板开着时整块窗口带 `WDA_EXCLUDEFROMCAPTURE`，任何截图/录屏都拍不到它，
  "改完到底长什么样"否则只能靠猜。
- **代价**：不设变量时一行都不跑。
- **放弃**：临时关掉捕获排除再截屏 —— 自反馈会把面板糊成万花筒，判断不了材质。

## 九、推翻 D6-01～D6-05：撤掉自建 GPU 玻璃（v1.6.6）

用户在真机上看过之后判定"不如 15:20 那版"，要求把源码和程序都退回那个构建。
**这一节推翻第八节的全部决定**，第八节保留作历史记录，别再照着它改回去。

### D7-01 模糊交回操作系统，源码里不再有自建抓屏
- **决定**：删除 `Core/Glass/GlassBackdrop.cs`、`GlassSurface.cs`、`SpringEase.cs`；
  移除 `Vortice.Direct3D11 / Direct3D9 / D3DCompiler`；`QbarWindow` 里整段玻璃底
  （StartGlass / PumpGlass / 画布重建 / 抓屏排除 / WM_NCHITTEST 穿透）一并删除。
  面板回到「Files 的底 + 亮度层 + 颗粒」+ 系统亚克力（`SystemAcrylic.cs`）。
- **理由**：15:20 那个构建就是这个形态，用户明确要它。系统亚克力本身就是实时合成的，
  "跟着桌面变"这件事它天然满足；自建链路带来的额外收益（可调半径、边缘折射）
  在 712×140 的横条上没有兑现成观感。
- **代价**：模糊半径/质量不再可调；面板失去 GPU 投影，只剩圆角 + 半透明边界。
  面板开着时**重新可以被截图/录屏拍到**（不再设 `WDA_EXCLUDEFROMCAPTURE`）。
- **放弃**：在自建玻璃上继续调参 —— 已经调过一轮（v1.6.5 → v1.6.6 第一次尝试），
  用户看完成品直接否掉，说明问题不在参数。

## 十、外部程序启动：工作目录统一走 ExternalLaunch（2026-11-26）

**起因**：把 `wb2api.exe` 绑到按键上启动，它读不到自己目录里的 `config.json`；
在资源管理器里双击同一个 exe 却一切正常。盘上留下的现场：

- `D:\software\myself\config.json`（`api_key` 与真身不同）+ `D:\software\myself\data\`
  —— 孤儿配置和被写歪的日志/统计，时间戳 11-24 22:35；
- wb2api 的 README 自己写着「工作目录**就是**放 config.json 的地方」，
  而它的 `auth_dir: "./auths"`、`state_file: "./data/state.json"` 也都是相对路径；
- 拿同一个 exe、换一个空目录当工作目录跑一次，它当场打印
  「config.json 不存在，已生成推荐配置」——机制当场复现。

### D10-01 起外部进程只留一个入口，路径类目标一律带上它自己的目录
- **决定**：新增 `Core/Windows/ExternalLaunch.cs`（`ForTarget` / `ForProgram` / `WorkDirFor` /
  `IsLocalPath`），把仓库里"把路径交给系统去打开/执行"的 11 处调用点全部改走它：
  `WindowService.LaunchInForeground`、`OpenFolderInForeground`、`QbarCommandStore.Execute`、
  `QbarService.OpenFileTarget`、`QbarWindow`（OpenTarget / RunTarget / RunAsAdmin）、
  `QbarExpandWindow.OpenSelected`、`WindowBindingService.LaunchAndTrackExe`、
  `ActionContext.RunProgram`、`CustomKeyRunner.Launch`、`ElevationHelper.Relaunch`、
  `UpdateService` 起新版本、`SettingsView` 打开配置/日志。
- **理由**：不显式给 `WorkingDirectory` 时子进程**继承父进程的当前目录**，而我们是常驻程序，
  当前目录常年停在安装目录。这跟"某个程序的怪癖"无关 —— git、ffmpeg、node、各类面板
  都按工作目录找自己的东西。"被键启动"和"被双击"唯一的差别就是谁给的当前目录，
  所以修在"给当前目录"这一层，而不是给 wb2api 打特例。
- **代价**：多一层间接。反面清单必须写清，否则以后有人给网址/协议也塞工作目录：
  `http(s)://`、`ms-settings:`、`shell:…`、PATH 里的裸命令名（`notepad`）、
  以及**目录**本身，这四类一律不设（`WorkDirFor` 返回空串）。
  `.lnk` 快捷方式自带"起始位置"，shell 会用快捷方式里的值覆盖我们给的 `lpDirectory` —— 那是对的，不要纠正。
- **放弃**：只在出事的那个调用点补一行 `WorkingDirectory`。同一个坑当时还有
  `WindowBindingService` / `QbarCommandStore`（工作目录留空）/ `UpdateService` 三处敞着，
  补一处等于等下一次事故。
- **验证**：`SelfTest.ExternalLaunch.cs` 29 项（判定规则、取值、拼出的 `ProcessStartInfo`、
  以及"目标的工作目录绝不等于我们的当前目录"这条回归守卫），
  Release 构建 0 错误，`artifacts\InputPlus.exe --selftest --only=ExternalLaunch` 29/29 通过。
