# ANCHORING（对话框浮层贴合与跟随）

## 0. 现状结论（≤10 行）

1. 现有浮层窗口是 WPF WS_POPUP，owner=对话框，已有 NOACTIVATE/TOOLWINDOW。
2. 定位原本在 FileDialogBarWindow.ApplyBounds 里手算，DWM FrameBounds + GetWindowRect 回退。
3. 事件源是 FileDialogWatcher 的 SHOW/LOCATIONCHANGE/DESTROY + 300ms 低频兜底。
4. 主要缝隙来自 WPF 根 Grid 的 8DIP 阴影留白与居中/宽度计算，不是 DWM 边框。
5. 本轮抽成 DialogBarAnchor.GetTargetRect 纯函数，bar 只负责测量与 SetWindowPos。
6. 浮层空白区域通过 WM_NCHITTEST 返回 HTTRANSPARENT，点击落到对话框。
7. 窗口 chrome 已改为 WS_POPUP + 去 caption/thickframe/border + DWM NC 渲染关闭。
8. 未实现 DWM cloak（仍用 WPF Hide/Show）与独立 NoActivateKeeper；浮层在需要键盘时激活自己。
9. 未做定时器轮询位置；Watcher 的 300ms 兜底仅在 SHOW 事件丢失时补发现。
10. 本轮验收以纯函数单测 + 真机截图/矩形测量为主，跨 DPI/双屏未全跑。
11. v1.3.13：跟随改成事件驱动（无定时器/无队列），几何与摆位分离，窗口在拖动中尺寸恒定；
    分层窗口保留（圆角 + 外侧阴影），代价与理由见 §3.1 与 DECISIONS.md「浮层为什么还是分层窗口」。

## 1. 四个缝隙来源与解法

| 来源 | 解法 |
| --- | --- |
| GetWindowRect 含不可见 resize border | 用 DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)；失败回退 GetWindowRect 并写日志 |
| DPI 虚拟化 | 进程 manifest PerMonitorV2；坐标全程物理像素，Reflow 每次取 GetDpiForWindow |
| 浮层自带边框/阴影 | WS_POPUP，去掉 CAPTION/THICKFRAME/BORDER；DwmSetWindowAttribute(DWMWA_NCRENDERING_POLICY, DISABLED)；阴影只由 XAML 画在外侧 |
| 圆角方向 | 下方贴合上两角直角，上方贴合下两角直角（ApplyOrientation） |

## 2. GetTargetRect 签名

```csharp
public static DialogRect GetTargetRect(
    DialogRect frame,      // DWM 可见边框（物理像素）
    DialogRect work,       // MonitorFromWindow 工作区
    int barWidth,
    int barHeight,
    out BarPlacement placement); // Below | Above
```

规则：

```
x = frame.Left + (frame.Width - barWidth) / 2
yBelow = frame.Bottom
yAbove = frame.Top - barHeight
placement = (yBelow + barHeight <= work.Bottom) ? Below : Above
y = placement == Below ? yBelow : yAbove
再按 work 夹取 x/y
```

无 padding、无 round 补偿；返回的矩形边与 frame 边完全重合。

> 实际 SetWindowPos 时接触边再向对话框内重叠 2px（下方顶边 -2 / 上方底边 +2），抵消 DWM 透明窗口边缘抗锯齿；纯函数仍返回 gap=0。

## 3. 事件模型

- `EVENT_OBJECT_SHOW`（#32770）→ 计算 rect → SetWindowPos → ShowWindow(SW_SHOWNOACTIVATE)
- `EVENT_OBJECT_LOCATIONCHANGE` → **回调里同步摆位**（只更新一个"最新矩形"字段再 `ApplyPosition`），没有定时器、没有队列
- `EVENT_OBJECT_HIDE/DESTROY` → 隐藏并清状态
- 最小化：IsIconic 时隐藏，不销毁
- 禁止位置轮询；仅保留 300ms 低频兜底发现丢失的 SHOW

## 3.1 跟随延迟：根因与修复（v1.3.13）

| # | 根因（改前） | 修复（改后） |
| --- | --- | --- |
| R1 | `_followTimer`（16ms）把位置事件攒着，tick 到了才 `ApplyBounds` | 删除跟随定时器；`UpdateBounds()` 在事件回调里同步 `ApplyPosition()` |
| R2 | `ApplyBounds` 每个 tick 都 `Measure` + `UpdateLayout` + 改 `Width`/`MaxHeight` | 拆成 `ComputeGeometry()`（只在尺寸变化/内容变化/停手 200ms/DPI/翻转/设置时跑）与 `ApplyPosition()`（纯算术 + 一次 SetWindowPos，不 Measure、不改尺寸） |
| R3 | `SizeToContent="Height"` + 分层窗口，尺寸一变就整窗重合成 | 改 `SizeToContent="Manual"`，高度只在 `ComputeGeometry` 里定；拖动中窗口尺寸恒定 |
| R4 | `MaxHeight` 由剩余空间推出，垂直拖动时每帧变 → 尺寸每帧变 | 同上：列表高度只在 `ComputeGeometry` 里算，拖动中不变 |
| R5 | `SizeChanged → ScheduleFollow` 自激，松手后振荡几帧 | `_inApply` 重入闸：应用期间的 `SizeChanged` 只计数、不再排几何；自检断言计数为 0 |
| R6 | `SetWindowPos` 只带 `SWP_NOACTIVATE`，等于每 tick 改一次 Z 序 | 统一 `SWP_NOACTIVATE | SWP_NOZORDER`；拖动中再加 `SWP_NOSIZE`（只挪不改尺寸） |
| R7 | 前台事件投递在 `DispatcherPriority.Background`；收起后等 250ms 再显示 | 提到 `Input`；收起后**立刻**重试一次、30ms 再试一次；拖动中前台短暂离开不收起 |
| R8 | 自检只测 `GetTargetRect` 算术，运行时延迟零覆盖 | 新增 `TestDialogBarFollow`：真 #32770 + 真浮层窗，连挪 60 次，断言误差/延迟/尺寸/自激 |

实测（自检 `TestDialogBarFollow`，60 次 +3px 连挪，本机 100% DPI）：

| 指标 | 改前（模型） | 改后（实测） |
| --- | --- | --- |
| 事件 → 摆位 p50 | ≈16.7ms（等一个 16ms tick） | **0.35ms** |
| 事件 → 摆位 p99 | ≈33ms（跨两个 tick）+ 重排 4.4ms | **1.01ms** |
| 相对位置误差 | 拖动中随 tick 抖动（16.7ms × 速度） | **0px**（60 次全为 0） |
| 拖动中窗口尺寸变化 | 每次 tick 都可能变 | **0 次**（438×128 全程不变） |
| 拖动中几何重算 | 每 tick 一次（含 Measure×2 + UpdateLayout×2，4.4ms） | **0 次** |
| 自激（_inApply 期间再排几何） | 有（SizeChanged → follow） | **0 次** |

> 「改前」一栏是按旧代码结构算出来的：`p50 = 半个 16ms tick`、`p99 = 跨两个 tick`，再加上旧 `ApplyBounds` 每 tick 的重排成本 4.4ms（同一台机器实测）。旧代码已被替换，无法再跑一遍原始路径；这两个数字是模型值，不是实测值。

## 4. 层级与焦点

- owner = dialog（GWLP_HWNDPARENT），不上 TOPMOST，不抢焦点
- WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
- 需要键盘时临时去掉 NOACTIVATE 并聚焦输入框/浮层；关闭浮层后焦点回输入框
- WM_NCHITTEST：输入框/按钮/列表返回 HTCLIENT，其余返回 HTTRANSPARENT，点击穿透到对话框

## 5. 降级

- 拿不到 DWM 边框/工作区：不显示，日志 `bounds unavailable`
- 目标提权（UIPI）：不显示，不绕过
- 对话框强杀：DESTROY 隐藏并清当前 HWND
- 多显示器：每次 Reflow 重新取工作区与 DPI，不缓存
- 连续开关：切换时先隐藏旧浮层再挂新浮层，状态按 HWND 隔离

## 6. 自检与验收

自检新增 `TestDialogBarAnchorGeometry`：
- 下方够 → Below、gap=0、居中
- 下方不够 → Above、gap=0
- 负坐标 → 夹到 work.Left
- 超宽 → 不越界
- 高 DPI 物理尺寸 → Below、gap=0

手工记录：

| # | 用例 | 结果/原因 |
| --- | --- | --- |
| 1 | 下沿零间隙 | 通过：bar 顶边=对话框 DWM 底边；截图待 800% 放大复核 |
| 2 | 100/125/150% DPI | 仅当前 200% 环境实测；其余待补 |
| 3 | 双屏不同 DPI | 未实测（单屏环境） |
| 4 | 拖动 5 秒 | 改后走事件驱动：自检 60 次连挪实测 p50 0.35ms / p99 1.01ms / 误差 0px；**120fps 逐帧录屏仍未做** |
| 5 | 缩放宽度同步 | 宽度取 frame.Width*3/5，每次 Reflow 重算 |
| 6 | 底部翻上方 | 纯函数单测覆盖；真机未拖到屏幕底部复核 |
| 7 | 空白点击穿透 | WM_NCHITTEST 已实现，未用 Spy++ 点击验证 |
| 8 | 最小化隐藏/还原 | IsIconic 分支在 Watcher/Service；未真机最小化 |
| 9 | 强杀对话框进程 | DESTROY 清理路径已有；未真机强杀 |
| 10 | 连开 5 个对话框 | 按 HWND 隔离；未连开 5 个验证 |
| 11 | 浮层显示时对话框可用 | 真机点列表/切目录可用（QuickSwitch 导航后仍可操作） |
| 12 | Esc 关闭 | 通过：有浮层先关浮层，再关 bar |
| 13 | 提权目标 | 代码 UIPI 判定；未真机提权目标 |
| 14 | 总开关关闭无钩子 | 自检 policy/Stop 路径；未 Spy++ 句柄数 |
| 15 | 退出无残留 | Dispose 路径卸载 watcher/hook/热键；未句柄计数 |

> 未实测项在上表写明原因，不用"应该"冒充通过。

## 7. 首帧显示时序：根因与修复（v1.3.15）

| 编号 | 根因 | 改动 |
| --- | --- | --- |
| R1 先显示后定位 | `ShowFor()` 先 `Show()` 再摆位，第一帧在默认/上次位置 | 顺序改为 建句柄 → 绑对话框 → 算几何 → **先 ApplyPosition** → 最后 `Show()` |
| R2 懒创建 | 第一次遇到对话框才 `new Window()`，XAML/模板/首次布局全算在那一瞬 | 启动 ApplicationIdle **预热**：`EnsureHandle` → 离屏 `Show()` → `UpdateLayout()` → `Hide()`；失败只记日志 |
| R3 前台门禁 | SHOW 常早于激活，`OnAppeared` 直接 return | 不再是门禁：非前台时挂 `120ms × 6` 重试，谁先到位谁显示 |
| R4 补救路径不覆盖"从未显示" | `StartReShowTimer` 只在"已打开后离开"时启动 | 出现路径自带重试；`TryReShowOnce` 仍是"已打开后回来"的快速路径 |
| R5 兜底轮询不补显 | `PollOnce` 只处理未跟踪窗口 | 被拒的窗口有独立重试；`LOCATIONCHANGE` 命中未跟踪但可见的 #32770 也当出现 |
| R6 去抖过大 | 500ms，第二次 SHOW 被吞 | 250ms；服务层对"已挂着"的重复 SHOW 幂等 |
| R7 首次布局异常被吞 | `Measure/UpdateLayout` 可能抛，吞掉后这一帧既没位置也没重试 | 显示后补三次校正（Render / 120ms / 320ms），每次都重读 DWM 边框 |
| R8 开关兜底 | `ShowOnAppear` 迁移只做一次 | 未改（保持现状）；用户配置里为 true，实测三次都出现 |

**实测（同机，`hold` 对话框连开）**：显示耗时 13.2ms / 4.6ms / 6.6ms（首次含建窗，之后纯摆位）；
另一次首测 16.3ms。浮条 406×54（= 44 输入行 + 2 边框 + 8 阴影边距），与对话框 DWM 可见底边重叠 2px。

> 本机是 DPI 虚拟化环境（我的测窗口和程序内部坐标差 2 倍），"gap" 用 GetWindowRect 量会多出 8px —— 那是
> DWM 可见边框与窗口矩形的差，不是缝隙；程序内部按 DWM frame 对齐。
