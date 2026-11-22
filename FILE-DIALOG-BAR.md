# FILE-DIALOG-BAR

在**任意第三方程序**的「打开 / 保存」对话框下沿浮出一条与 qbar 同风格的搜索栏：
输入关键词 → 选中文件 → 回车，文件被这个对话框接受，对话框照常返回。

- 代码根：src/InputPlus/Core/FileDialog/、src/InputPlus/UI/Windows/FileDialogBarWindow.xaml(.cs)
- 设置页：Qbar 面板 →「文件对话框浮层」
- 诊断：--dialogprobe（控件清单 + 判型）、--filedialog-e2e（真对话框回填矩阵）、--selftest

---

## 1. 非目标

- 只处理文件/文件夹对话框；资源管理器窗口（CabinetWClass）只判型、不挂浮层。
- 不做 QuickSwitch / 在资源管理器里跳目录。
- 不常驻全局键盘钩子（只在焦点阶梯第 4 级临时装，15 秒无输入即卸）。
- 不注入第三方进程（UIA 走跨进程 COM，键盘走 SendInput）。

## 2. 五层与数据流

~~~
 WinEventHook(OUTOFCONTEXT|SKIPOWNPROCESS)        300ms 顶层枚举（兜底）
        │                                               │
        └──────────────► FileDialogWatcher ◄────────────┘
                         │ Appeared / BoundsChanged / Destroyed（同 HWND 500ms 去抖）
                         ▼
                    DialogProbe.Children ──► DialogClassifier.Classify   （纯函数，a→e 命中即停）
                         │                          │
                         │                          ▼
                         │                 DialogPolicy.ShouldAttach   （总开关/类型/进程禁用，判定阶段短路）
                         ▼                          │
                 DialogSearchProvider               │ 允许
                  （QbarSearchEngine）               ▼
                         ▲                 FileDialogBarWindow（唯一一套浮层）
                         │                   · owner=对话框，TOOLWINDOW|NOACTIVATE
                         │                   · 钉在对话框下沿外侧、16ms 合帧跟随、40–48px
                         │                   · 焦点阶梯 5 级、Esc 只隐藏
                         │                          │ 用户回车/点击
                         │                          ▼
                         │                 DialogBackfill.Submit（STA 线程）
                         │                   · UIA ValuePattern → 键盘+剪贴板
                         │                   · 提交 → 2s 内校验对话框反应 → 最多重试 1 次
                         │                          │
                         └──────── 记录 ◄───────────┘
                              DialogRecorder（JSONL，surface=file_dialog:<exe>）
~~~

层间约定：

| 层 | 对外唯一接口 | 不认识什么 |
| --- | --- | --- |
| Watcher | FileDialogWatcher.Appeared/Destroyed/BoundsChanged | 判型、UI、回填 |
| Classifier | DialogClassifier.Classify(className, children)、DialogPolicy.ShouldAttach(...) | 窗口创建、输入 |
| Bar | FileDialogBarWindow.ShowFor/UpdateBounds/SetRows/HideBar | Win32 子控件、回填 |
| Backfill | DialogBackfill.Submit(dialog, classification, item, options) | 搜索、浮层 |
| Recorder | DialogRecorder.Record/RecentSelections | 其它一切 |

编排在 FileDialogBarService（AppHost.FileDialogBar）：订阅 Watcher、开关与热键、把 Bar 的
QueryChanged/CommitRequested/Dismissed 接到搜索与回填。

## 3. 判型表（禁止按 exe 名写分支）

| 顺序 | 条件 | 结果 |
| --- | --- | --- |
| a | 类名 #32770 + 子窗口含 DirectUIHWND 或 SHELLDLL_DefView | **FileDialog / VistaDialog**（IFileDialog） |
| b | 类名 #32770 + ComboBoxEx32 + Edit | FileDialog / LegacyDialog（GetOpenFileName） |
| c | 类名 #32770 + ShellNameSpace Control 或 SysTreeView32 | FolderDialog / FolderPicker |
| d | CabinetWClass / ExploreWClass + SHELLDLL_DefView | ExplorerWindow（占位，不挂 Bar） |
| e | 其它 | Unknown（放弃） |

**顺序不能换**。实测 WinForms「另存为」同时有可见的 DirectUIHWND 和**不可见**的
ComboBoxEx32/ComboBox/Edit id=0xA205（地址栏），b 先判会把 Vista 对话框认成老式。

实测控件（--dialogprobe 输出，Win11 24H2 / 96dpi）：

~~~
#32770  title="InputPlus DialogProbe Test"
  DUIViewWndClassName
  DirectUIHWND            (多个)
  SHELLDLL_DefView        id=0x0461
  Edit                    id=0x03E9  ← 文件名框（=1001，UIA AutomationId 1001）
  NamespaceTreeControl / SysTreeView32 id=0x0064   ← 导航窗格
  Button                  id=0x0001  text="&Save"
  Button                  id=0x0002  text="Cancel"
  ToolbarWindow32         id=0x03E9  text="Address: ..."
  ComboBoxEx32/ComboBox/Edit id=0xA205  visible=False  ← 地址栏，判型时必须忽略
  → FileDialog / VistaDialog
~~~

选文件夹模式（Win11 FolderBrowserDialog）与保存对话框的控件结构**完全一样**，
区别只在 UIA：编辑框 Name 是「文件夹:」、AutomationId 1152。DialogUia.IsFolderPicker
负责这个区分，回填据此选「回车」还是「选择文件夹」。

## 4. 回填：三条路线

### 4.1 Vista 家族（UIA 优先）

1. DialogUia.TrySetValue：按 AutomationId 1001（+100）、Name 含「文件名/File name」（+60）、
   文件夹模式含「文件夹/Folder」（+60）、排除「搜索/地址」（-80/-40）打分选编辑框，
   ValuePattern.SetValue(完整路径)。
2. 聚焦：TryFocus → 失败 ClickEdit（GetWindowRect 中心 → SetCursorPos → 点击；物理像素）。
3. 提交：文件目标回车；文件夹目标回车进目录 → UIA 按名字 Invoke「选择文件夹」→ 失败点 ControlId=1 的按钮。
4. 校验：2 秒内「对话框已关闭」/「弹出了确认框」/「文件夹框内容被清空」→ 成功，否则退键盘路线。

### 4.2 键盘 + 剪贴板（所有路线的兜底）

FocusEdit → Ctrl+A → ClipboardPaster.TryPaste(path)（内部备份/还原+序号守卫）→ 等 150ms →
提交 → 校验。失败等 120ms 重试 **1 次**，不再多按回车（多按会被当成「取消/重复提交」）。

### 4.3 老式 comdlg32 与 FolderDialog

老式走 editor 路线（edt1=0x0480、cmb13=0x047C 仅作兜底），文件夹走键盘路线 +
「选择文件夹」。**不做跨进程 WM_SETTEXT**：DirectUI 自绘控件不认，老式对话框还会校验内部状态。

### 4.4 用户可见行为（1.3.5）

- 自动显示：宽度是对话框的 **3/5**（面板本体，窗口含左右阴影略宽），水平居中；**始终贴住对话框下端**。
  下方空间够时在下方（输入在上、列表向下）；不够时收进对话框内侧底部（输入在下、列表向上），不会翻到上方。
  接触边 **gap=0、直角**，外侧两角圆角；不挡输入框，列表/浮层最大高度 320px。
- 右侧三个按钮（收藏/历史/菜单）与推荐文件夹 chip 的语义见 [QUICK-SWITCH-BUTTONS.md](QUICK-SWITCH-BUTTONS.md)。
- 结果行显示**资源管理器同款图标**（shell 图标 + 快捷方式叠加箭头），异步加载，未到时先显示通用字形。
- 点输入框才拿键盘：自动显示时窗口带 `WS_EX_NOACTIVATE`，不抢对话框焦点；点击后临时去掉该样式，
  走同一套焦点阶梯并显式聚焦。拿不到键盘就在状态栏写原因，不弹框。
- Enter = 写入对话框；Alt+Enter = 不碰对话框，直接在资源管理器里选中；回填失败自动定位文件所在文件夹。
- 热键被别的程序占用时自动尝试 `Ctrl+Shift+Alt+F` / `Ctrl+Alt+Shift+F` / `Ctrl+Alt+D`，失败只记日志（不影响自动显示）。

### 4.5 红线相关

- UIPI：InputSender.IsForegroundHigherIntegrity() 为真 → 直接记失败日志，不尝试任何绕过。
- 剪贴板：只走 ClipboardPaster，它保证备份/还原；还原失败只写日志。
- 失败：一律 BackfillOutcome.Fail + 日志 + 浮层状态行，不弹模态框。

## 5. 焦点阶梯

| 级 | 动作 | 结果 |
| --- | --- | --- |
| 1 | SetForegroundWindow(bar) + 80ms 轮询校验 | Foreground |
| 2 | AttachThreadInput(前台线程, 本线程) 后重试，再 detach | AttachThreadInput |
| 3 | InputSender.Tap(VK.Menu) 模拟 Alt 解锁前台限制后重试 | AltTap |
| 4 | 临时 WH_KEYBOARD_LL：字符/退格/回车/Esc 重定向进 Bar，状态栏「输入中」；15s 无输入自动卸 | NeedsKeyboardHook |
| 5 | 仍失败：只写日志，本次不显示 Bar | Failed |

## 6. 配置（AppSettings.FileDialogBar）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| Enabled | true | 总开关；关掉即卸载 watcher/热键/定时器/浮层 |
| ShowOnAppear | true | 对话框一出现就显示（1.3.5 起默认开；老配置迁移一次） |
| ShowOnHotkey | true | 按热键显示（默认，避免打扰） |
| Hotkey | Ctrl+Alt+F | 空串 = 关闭热键 |
| Types | FileDialog=true / FolderDialog=true / ExplorerWindow=false | 按类型开关 |
| DisabledProcesses | 空 | 进程名（小写 exe）禁用 |
| UiaBackfill | true | Vista 家族先走 UIA |
| InjectCom | false | COM 注入备选路线开关（见限制） |
| MaxResults | 200 | 结果上限（虚拟化列表） |
| BarHeight | 44 | 40–48 |
| PollFallbackMs | 300 | 顶层枚举兜底间隔；0 = 关闭 |

## 7. 已知限制（如实记录）

1. **老式 comdlg32 家族在 Win11 上无法复现**：即使 AutoUpgradeEnabled=false，系统仍给出 Vista 对话框。
   判型规则 b 与 editor 路线已实现并有自检，但没有真机证据。
2. **>260 字符路径**：本机未启用长路径策略，系统弹确认框后拒绝。浮层已给出路径并提交，
   是否接受取决于目标程序与系统策略，属于系统行为。
3. **COM 注入备选路线未实现**：InjectCom 只是开关占位。理由：UIA 路线已覆盖实测的全部
   Vista 对话框；注入要编 x86/x64 两个 DLL + 两个宿主进程，风险/收益不划算（参考实现也把它当兜底）。
4. **跨会话/远程桌面**：WinEventHook 在跨会话时可能丢事件，靠 300ms 顶层枚举兜底；
   但跨会话 UIA/输入不可用，按失败记日志。
5. **提权目标**：遵守 UIPI，直接判定不可用，不绕过。
6. **浮层可见性未自动化**：见 ACCEPTANCE.md 的 11–17，附手工步骤。

## 8. 实测兼容性清单

| 程序 / 对话框 | 家族 | 结果 | 证据 |
| --- | --- | --- | --- |
| WinForms SaveFileDialog（PowerShell 独立进程） | Vista | 通过：判型 + 探测 | --dialogprobe，logs\dialog-probe.txt |
| WinForms 保存对话框 + UIA | Vista | 通过：返回目标路径（含中文/空格/括号/emoji） | --filedialog-e2e 用例 1 |
| WinForms 保存对话框 + 键盘（UIA 关闭） | Vista | 通过：返回目标路径 | 用例 2 |
| AutoUpgradeEnabled=false 保存 | 仍 Vista | 通过：返回目标路径 | 用例 3 |
| WinForms FolderBrowserDialog | Vista + 文件夹模式 | 通过：返回目录 | 用例 4（修 IsFolderPicker 后） |
| 记事本「另存为」（真机、非 e2e） | Vista | 通过：浮层自动出现在下沿、输入 test 出 200 条结果、不挡保存按钮 | 1.3.5 真机截图与 app.log |
| >260 字符路径 | Vista | 预期内失败：系统拒绝 | 用例 5 |
| 微信 / 企业微信 / Edge / 记事本 / IDM | — | 未在本机自动实测 | 手工步骤见 ACCEPTANCE.md 1–5 |

> 这些程序用的都是同一套系统对话框（#32770 + DirectUIHWND 或 comdlg32），
> 判型与回填路径不依赖 exe 名；但「没跑过就是没跑过」，验收表里按未实测记。

## 9. 诊断与复现

~~~powershell
# 1) 打开任意程序的「另存为」，然后：
InputPlus.exe --dialogprobe          # 控制台 + 数据目录\logs\dialog-probe.txt

# 2) 回填矩阵（会真的操作 5 个对话框；不需要浮层）
InputPlus.exe --filedialog-e2e       # logs\filedialog-e2e.txt，退出码 0 = 全通过

# 3) 自检（含判型/策略/记录/搜索/热键校验）
InputPlus.exe --selftest
~~~

设置页复现：Qbar 面板 → 右栏最下方「文件对话框浮层」。
