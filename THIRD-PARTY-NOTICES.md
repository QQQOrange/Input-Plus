# 第三方代码与许可声明 / Third-Party Notices

Input Plus 的「按住拖拽」(HoldDrag) 模块是从下面两个项目移植而来。移植代码保留了各自的
版权与许可要求；Input Plus 自身以仓库根目录的 `LICENSE` 为准。

## AltSnap

- 项目：AltSnap — https://github.com/RaymondGillibert/AltSnap
- 版本/来源：`AltSnap-main`（本地权威副本 `D:\Downloads\Compressed\AltSnap-main`）
- 许可：GNU General Public License v3.0 or later（`License.txt`）
- 版权：Copyright (C) 2022 Raymond Gillibert；上游 AltDrag Copyright (C) 2015 Stefan Sundin

被移植的部分（行号指 AltSnap-main 的 `hooks.c`）：

| AltSnap 位置 | 内容 | Input Plus 落点 |
|---|---|---|
| `hooks.c:454-466` | `IsSamePTT` / `IsPtDragOut`（同点判定、拖动阈值） | `Modules/HoldDrag/Core/AltDragSink.cs` `OnMove` / `StillInsideDragOut` |
| `hooks.c:3733-3775` | `ActionMove`：起拖、双击、`DragThreshold` → `DRAG_WAIT` | `AltDragSink.cs` `BeginDrag` / `ShouldDragWait` |
| `hooks.c:3776-3856` | `SetEdgeAndOffsetCF` / `SetEdgeToClosestSide` / `SetEdgeAndOffset`（`CenterFraction`、`SidesFraction`） | `Core/AltDrag.cs` 同名方法 |
| `hooks.c:4002-4044` | `ActionResize`：`ResizeCenter` 的 0/2/3 分支 | `AltDrag.cs` `ApplyResizeCenter` |
| `hooks.c:1365-1405` | `MoveWindowAsync`（resize 位反推） | `AltDragSink.cs` `WriteWindow`（`moveOnly` 判定） |
| `hooks.c:1410-1465` | `LetWindowKickBack`（`WM_SIZING` 让终端类程序按字符格回弹） | `AltDrag.cs` `LetWindowKickBack` / `WmszFlag` |
| `hooks.c:1469-1567` | `MoveResizeWindowNow_` / `MoveWindowNow`（flag 选择、`Sleep(8)`、`WM_SYNCPAINT`） | `AltDragSink.cs` `WriteWindow` |
| `hooks.c:1969-2060` | `RestoreOldWin` / `RestrictCursorToMon`（按比例重算锚点、Ctrl 夹光标） | `AltDrag.cs` `Rebase` / `AltDragSink.cs` `MoveMouse` |
| `hooks.c:2206-2392` | `MouseMoveNow`：`static RECT wnd`、`GetClipCursor`、缩放九宫格、锁轴 | `AltDragSink.cs` `MoveMouse`、`AltDrag.cs` `ResizeGeometry` |
| `hooks.c:5462-5515` | `LowLevelMouseProc` 的 WM_MOUSEMOVE：`SamePt` 去重、`DRAG_WAIT`、`RezTimer` 抽帧 | `AltDragSink.cs` `OnMove` |

## AltDrag

- 项目：AltDrag — https://github.com/stefansundin/altdrag
- 版本/来源：`altdrag-master`（本地副本 `D:\Downloads\Compressed\altdrag-master`）
- 许可：GNU General Public License v3.0
- 版权：Copyright (C) 2015 Stefan Sundin

HoldDrag 模块的早期版本是 AltDrag `hooks.c` 的逐条移植（`MouseMove`、`MoveSnap`/`ResizeSnap`、
九宫格分区、起拖流程），这部分框架仍在 `Modules/HoldDrag/Core/AltDrag.cs` 与
`AltDragSink.cs` 中沿用。

## 许可兼容性说明

AltSnap 与 AltDrag 均为 **GPLv3（或更新）**。Input Plus 仓库根目录的 `LICENSE` 是 **GPLv2**
文本（未声明 "or later"）。GPLv2-only 与 GPLv3 代码组合发布，按 FSF 的解释需要 GPLv3 授权；
二者并不等价。本条留给仓库所有者确认：

1. 若 Input Plus 整体按 GPLv3（或更高）发布 —— 只需把 `LICENSE` 更新为 GPLv3 文本并保留本文件；
2. 若 Input Plus 必须维持 GPLv2-only —— 则需要从权利人处取得 GPLv3 → GPLv2 的兼容许可，
   或移除 HoldDrag 模块中来自 AltSnap 的部分。

在本文件所声明的移植代码上，AltSnap/AltDrag 的原始版权与 GPLv3 条款继续适用。
