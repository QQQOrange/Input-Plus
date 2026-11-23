# 附加菜单（GestureMenuWindow）的版式约束

> 2026-11-23 连续改了三版才稳定，把"为什么这么写"记下来。

## 1. 二级菜单和一级在**同一棵视觉树**里

二级不是 `Popup`，是窗口里的第二列（`Grid.Column=1`）。原因：

- `Placement=Right` 在屏幕边缘会翻边，翻过去正好压住一级菜单；
- `Placement=Custom` 的回调第一次拿到的尺寸不是 DIP（实测 160 DIP 报成 320），
  甚至会拿到 0，摆出来的位置不可信；
- 放进同一棵树后，"拼接、等宽"由布局保证，不再依赖手工算坐标。

## 2. 一级卡片必须 `VerticalAlignment="Top"`

窗口是 `SizeToContent`。二级展开会把窗口撑高，Grid 里的子元素默认 `Stretch` ——
一级卡片会跟着被拉到整个窗口那么高（用户看到的就是"一级菜单上下被拉长"）。
一级永远是内容高度，只有窗口变大。

## 3. 两块卡片之间零间隙

`SubCard.Margin` 的左边是 **0**（不是 2）。2 DIP 在 200% 缩放下是 4 个像素，
看起来就是一条缝。纵向 `Margin.Top = 14 + 父项行的 Y`。

## 4. 行样式

- 一级：`MenuRow`（Padding 8,2 / MinHeight 24）；
- 二级：`SubMenuRow`（继承 `MenuRow`，Padding 6,2）—— 二级没有右侧箭头、没有禁用态；
- `MenuRow` 的模板内边距必须写 `{TemplateBinding Padding}`，否则派生样式改不动它。

## 5. 自检钉住的东西

`TestAttachMenuLayout`：等宽、行高相等且 ≤28、二级不显示 URL、显示名称、
二级在一级外侧、**展开二级前后一级高度不变**、**间隙 ≤ 1 DIP**。
出图：`--render` 产出 `attach-menu-sub.png`（历史记录 + 命令两张二级展开态）。
