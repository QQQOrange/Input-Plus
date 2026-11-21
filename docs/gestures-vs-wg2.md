# Input Plus 鼠标手势 vs WGestures 2：逐项对照

> 比对基准：Input-Plus源代码 工作副本（GestureDefaults.Version=12，内置手势 52 条）；WGestures 2 反编译源码（.NET7/WPF，命名空间混淆 A/a/B）+ 随包 full/Resources/prefs.json 与 gestures.json。只读比对，未改动任何代码。

## ① 一句话结论

**同源不同代，并且 v1.5.0 之后连扩展性也追平了**：Input Plus 的识别内核是 WGestures 2 那套“切线角剖面 + 路径积分”的忠实移植（阈值、歧义间距、16 变体闭环都照抄），在它外面加了三层工程化改造——可调参数、松手前判定与切尾容错、61 条触发方式 × 4 层三态继承；动作库从 7 类扩到 19 类、143 个静态动作。v1.4 之前缺的两样扩展性（**多步手势（最多 8 步的步骤序列）** 与 **Lua 脚本 / 手势菜单**）已在 v1.5.0 补齐，另外顺手补上了沿边移动 + 双击角落、同形手势层级择优、演示回放与设置窗口置顶；v1.5.2 又把手势级 .ipg 导入导出和「显示托盘图标」偏好撤了（手势随整份备份 / 导出配置同步，托盘图标一直都在、右键菜单里就能开关手势）。仍然只有一处天然差距：**没有反向导出成 WG2 的 .wg28bw**。

## ② 逐项对照表

### 2.1 识别算法与参数

| 能力 | WGestures 2 怎么做 | Input Plus 怎么做 | 差在哪 |
|---|---|---|---|
| 算法族 | 不是 $1，也不是 Protractor：自研“切线角剖面 + 沿 progress 的路径积分”。A/G.cs:300-370 | 同一套，逐字移植到 WG/WgStroke.cs:254（WgStrokeDistance 类）、Compute:256 | 同源；Input Plus 另保留了旧方向串引擎 GesturePattern.cs（389 行）但只用来给没存轨迹的存量手势合成折线（GestureModels.cs:50-56） |
| 距离度量 | 采样点归一化为 (累计弧长/总长, atan2(dy,dx)/π)，单元格代价 (Δθ)²，角度差折回 [-1,1]（A/G.cs:378） | WgStrokeDistance.SegmentCost:336 返回 acc*(pSpan+qSpan)，单元格同 (Δθ)²，初值 0.2 当 ∞ | 一致 |
| 顶点合并 | 硬编码 float 0.1：相邻方向差小于它视为还在同一条直线上，只把末点挪过去（A/P.cs:239） | WgStroke.cs:28 MergeAngle=0.1，注释逐字引 A/P.cs:239 并说明 0.1f→double 的 1.5e-9 误差从哪来；合并逻辑 :100-115 | 完全一致，连 float 字面量的坑都写在注释里 |
| 单笔点数上限 | 1024 点（A/L.cs:60），采样上限直接截断 | WgStroke.cs:31 MaxPoints=128；StrokeOf 先按 24~400 点重采样（GestureEngine.cs:179），超限则 epsilon*1.7 重压最多 8 轮（:169-173） | Input Plus 更省内存，但超长笔画会被反复压扁；两侧点数比 >10（WgStroke.cs:34）直接判不可比 |
| 采样间距 | StrokeMinMove=30px 作为相邻采样点的最小间距（A/L.cs:60 + A/G.cs:72 的 DistanceSquared 门槛），最后一步按下键时放宽 1.25 倍（A/L.cs:141） | MinMove=30 语义换了：判“这一笔总幅度够不够长”（GestureService.cs:1024/1055/1180），采样间距另由 2px 门槛管（:966） | **同名不同义**，调参时容易误解 |
| 命中阈值 | A_f11=0.05（A/m.cs:126），写死；源码里没有灵敏度开关 | WgThreshold=0.05（GestureModels.cs:302），且**暴露到界面**“匹配容差”（GestureOptionsView.xaml:445） | Input Plus 可调，WG2 只能改源码 |
| 区分/歧义阈值 | a_f=0.01（A/m.cs:129）：两个候选分差小于它就算分不清 | WgTieMargin=0.01（GestureModels.cs:315，注释直接引 A/m.cs:129）；Match 里判 best>tie 且 second-best<=tie 则返回 null（GestureEngine.cs:399） | 一致；Input Plus 另有 immediate 档把 tie 抬到 ≥0.02（:341） |
| 候选排序 | 按 target.Level() 取最优（A/m.cs:478-560），更具体的目标分数更好就替换 | 不看层级，纯按距离取最优 + 歧义拒判 | **这里是行为差异**：WG2 允许“应用手势和执行全局手势同形共存，应用内优先”，Input Plus 靠 GestureIdentity 签名去重，同形两条直接判重（GesturesView.xaml.cs:1787） |
| 闭环容错 | 无（画圆收口差一点就超阈） | GestureEngine.cs:346-377：检测 IsLoop 后生成 8 个起点 × 正反共 16 个变体，仅在原样超阈时启用 | Input Plus 独有 |
| 松手前预判 | 只有 STARTS_WITH 前缀高亮（A/m.cs:569），仍等松手才执行 | MatchRelease + TrimTail（:437/:468）算出“用户可能在这一笔末尾多半甩了 3~5px”，整条比命中率 56.0%→85.4% 的量化注释就在 :406-417；AwaitsLonger（:506）对“更长候选的前缀”也命中时不抢跑 | Input Plus 独有，是它识别体验最大的加分项 |
| 立即执行 | 无 | MouseGesture.Immediate（GestureModels.cs:82）+ TryImmediate:1052 | Input Plus 独有（默认关） |
| 参数可调面 | 界面只有 StartDragTimeout / StrokeMinMove / 外观（OptionsPageView.xaml:35-185），识别阈值全写死 | 手势参数面板：匹配容差、最小识别距离、起始超时、起手距离、换向系数（GestureOptionsView.xaml:378-494） | Input Plus 把“引擎参数”变成用户旋钮，注释也写明 WG2 没有这些（:371） |

### 2.2 录制与回放

| 能力 | WGestures 2 怎么做 | Input Plus 怎么做 | 差在哪 |
|---|---|---|---|
| 录制方式 | 全局录制态：进录制后钩子把笔画实时追加到正在编辑的 Intent，可在任意位置画（A/T.cs:57、A/m.cs:105 的 isRecording 标记；录制生命周期见 IntentListViewModel.cs:201-228） | 专用窗口 GestureRecordWindow（358 行）：窗口自己收鼠标事件，侧键额外挂 WM_XBUTTONDOWN（:21-22），画完回传起始键+归一化轨迹+方向串（:357 起） | WG2 是“边画边长”，Input Plus 是“画一笔交一次”；Input Plus 不抢焦点、键位确定，WG2 能连续叠步骤 |
| 多步 | 支持：手势 = J 步骤序列，最多 8 步（A/L.cs:230），步骤可为 Stroke / MoveToEdgeCorner / KeyDown / Scroll 四种（A/P.cs:110、:18-60、A/Q.cs:19-100、:103-160） | v1.5.0 起支持：MouseGesture.Steps（GestureStep：kind=stroke/scroll/corner/edge/key，上限 8，GestureModels.cs 的 MaxSteps），识别时按 300ms 停顿切段、逐项比类型与方向，笔画步仍走 WG 引擎 | 已追平；输入侧的步骤编辑器就在详情卡里（画一笔 / 按一组键 / 滚轮 / 角落 / 贴边），比 WG2 多一种“贴边”步 |
| 确认/预览 | 画完弹 AddGestureWindow 只做缩略图确认（AddGestureWindow.xaml.cs:10-31），列表里用 ThumbnailPathView 把整条步骤序列画出来 | 列表用 GlyphFor / IconFor 现画方向图标（GesturesView.xaml.cs:839/:892），详情页有“按住鼠标键画一笔”重录 | Input Plus 无“整条手势动画预览”；WG2 无“重录但保留动作配置”的独立入口 |
| 回放 | 无 | v1.5.0 起：详情卡「回放」按钮；v1.5.2 起不再弹独立舞台，直接在详情卡的笔画框里重画（12ms 一点，笔画淡为底色，末端小圆点跟着走，画完 700ms 收掉） | Input Plus 反超：WG2 一直没有“演示一遍” |
| 录制健壮性 | 依赖全局钩子，抬键丢失时暂无兜底 | 看门狗定时读物理键状态收尾（:223），Watchdog 覆盖 Alt+Tab / 全屏 / 远程桌面吞掉 MouseUp 的情况 | Input Plus 更稳 |

### 2.3 动作数量与种类

| 能力 | WGestures 2 怎么做 | Input Plus 怎么做 | 差在哪 |
|---|---|---|---|
| 命令/动作数 | 7 类命令：SendKeysCommand、CmdCommand、RunAppCommand、WindowControlCommand、WebSearchCommand、MenuCommand、ScriptCommand（YingDev.WGApp.Command/） | ActionCatalog.cs:77 起，146 处 Add、143 个静态 ID + 动态 macro:<id>，17 个分类（窗口功能 26、文本编辑 26、系统功能 18、基础 17、鼠标功能 15、光标移动 15、剪贴板 9…） | 数量约 20 倍；粒度是“具体功能”（winSnapLeft、titleCase、paste1）而非“命令类型” |
| 窗口控制 | WindowControlCommand 一个命令 + WinCmd 枚举（MAXIMIZE/RESTORE/TOGGLE_MAXIMIZE/MINIMIZE/CLOSE/TOGGLE_TOPMOST）+ AllowChild | winMaximize/winMinimize/winClose/winPin/winTransparent/winCenter/winSnap 九向/winSendToBottom/switchNextApp 等 26 个独立动作 | Input Plus 更细（吸附九向、透明度、置底），WG2 更省条目 |
| 脚本扩展 | ScriptCommand + NLua，WG.cs 785 行 40+ API（FindWinByTitle、GetClipboardText、SetBrightness、GetExplorerPath…） | v1.5.0 起：动作分类「Lua 脚本」（MoonSharp 2.0.0 硬沙盒），LuaRunner 暴露 ip.* 约 30 个入口（action / run / open* / text.* / key* / click / wheel / move / win.* / sleep / now / arg / dryrun），编辑器带中文速查表，5 秒超时 | 已追平主干；比 WG2 少 ip.pause（暂停开关没有脚本面）、没有窗口查找类 API |
| 手势菜单 | MenuCommand + MenuCommandWindow：一个手势弹出二三级动作菜单（Columns 可调） | v1.5.0 起：动作分类「手势菜单」，GestureMenuDef（最多 12 条）+ GestureMenuWindow 弹层（1..9 快选、方向键、回车、Esc）+ 三栏管理窗 | 已追平；条目是「动作 ID + 参数」，复用同一套动作目录，不做二级子菜单 |
| 带参动作 | 每类命令自己有强类型字段（SendKeys.Keys、RunApp.MatchWinTitle+Regex、WebSearch.Engine 占位符 {WG_SELECTED_TEXT}） | 统一 (Action, Arg) 二元组，Arg 由动作声明 NeedsArg + ArgHint（ActionCatalog.cs:129） | Input Plus 扩展新动作只需注册一行，但参数是字符串，缺强类型校验 |
| 默认手势表 | gestures.json 49KB：General 53 个 Intent / 61 条 Trigger；全局键计数 KeyDown 118、MoveToEdgeCorner 70、Stroke 41、Scroll 8 | 内置表 52 条（Assets/gestures-builtin.json：stroke 50 + wheel 2；右键 41、中键 9、x1 2），另由 GestureDefaults 补种热角/贴边滚轮（GestureDefaults.cs:548/565） | 默认覆盖面相当；WG2 默认表里 1/3 是边缘步骤，Input Plus 把它们拆成独立 Kind |

### 2.4 作用目标与三态继承

| 能力 | WGestures 2 怎么做 | Input Plus 怎么做 | 差在哪 |
|---|---|---|---|
| 目标层级 | General / Group / App(S) / Special(s) 四层，Level() 分别 10（A/R.cs:54）、100（A/R.cs:94）、1000（A/S.cs:22）、10000（A/S.cs:45） | GestureTargetKind：Global / Group / App / Desktop / Taskbar（Wg2Triggers.cs:170-186） | 层级语义对齐；Input Plus 把 WG2 内置的 WinDesktopTarget/WinTaskbarTarget 提升为一级 Kind |
| 解析策略 | 逐层并集收集候选 + 分数择优（A/m.cs:478-560），Level 只做兜底 | GestureTriggerMap.cs:264，Special(任务栏→桌面)→App→Group(后建优先, :316)→Global，**首个显式值胜**（:288-328） | Input Plus 是“覆盖”，WG2 是“并集+择优”。覆盖更可预测，但对“同形手势在不同层”不宽容 |
| 三态 | 只有 TriggerDef.Enabled（默认 true），没有“继承”态 | Dictionary<string,bool>（:205），键缺失 = 跟随上层；UI 三态胶囊（GesturesView.xaml.cs:1435 起 PillOn/PillOff/PillAuto，BuildTriggers:1453） | **Input Plus 独有**，粒度细到 61 条触发方式各自三态 |
| 触发方式枚举 | 触发即手势第一步（MOUSE:1/2/3/4、VSCROLL:±120），没有独立列表 | Wg2Triggers.All 61 条（:58-79）：5 个裸键 + 8 个位置(1/2/4/8/3/6/9/12) × 7 种组合；Label/MaskOfCorner 负责显示 | Input Plus 把“允许哪些触发方式”做成可勾选矩阵，WG2 只能在手势上表达 |
| 整块禁用 | 无（只能逐条删 Intent） | GestureTarget.NoGestures + IsBlocked（:331），命中即该目标下所有手势失效 | Input Plus 独有 |
| 应用识别 | App 目标按可执行文件；Group.Children 引用 App/Special 的 Id，删除时清孤儿（A/Q.cs:245-270） | SameApp：全路径或去扩展名比较（:345）；DisabledTargets 名单（GestureModels.cs:244） | 思路一致；Input Plus 允许“同名不同路径”单独禁 |

### 2.5 外观偏好（逐字段对比 prefs.json 与 GestureOptions）

| 能力 | WGestures 2 怎么做 | Input Plus 怎么做 | 差在哪 |
|---|---|---|---|
| 轨迹开关/描边 | ShowPath=true、PathBorderWidth=1（prefs.json） | ShowPath（与 ShowTrail 做与运算，GestureModels.cs:372）、ShowPathBorder=true（GestureModels.cs:388）；描边粗细没有独立字段，overlay 里直接算成轨迹粗细 + 2（GestureOverlayWindow.xaml.cs:135） | 视觉一致（都是 1.5 的线配一圈描边）；prefs.json 的 PathBorderWidth 在 Input Plus 没有对应字段，改粗细就是改线宽 |
| 轨迹颜色 | PathColorNormal=#00000030（≈19% 黑）、PathColorRecognized=#FF5900FF（紫） | PathColorNormal/PathColorRecognized 同名同默认（GestureModels.cs:384、:387，常量在 Wg2Triggers.cs:18/:21），实际绘制走这两个值（GestureOverlayWindow.xaml.cs:107-119） | 默认视觉一致；Input Plus 额外把 alpha 乘上 TrailOpacity（默认 1.0，overlay 里 clamp 0.05~1，:126），界面上有 0.2~1 的透明度滑块（GestureOptionsView.xaml:111） |
| 轨迹粗细 | PathLineWidth=1.5 | PathLineWidth=1.5（:390），UI 滑块 1~4（xaml:94） | 一致；Input Plus 还留着 TrailWidth=3.0（:229）但全仓没有消费者，是死字段 |
| 分键配色 | 无 | ButtonTrailColors 字典（:238）+ 5 个色板（右键/中键/侧键1/侧键2/左键，xaml:169-219） | Input Plus 独有 |
| 名称显示 | ShowGestureName=true、GestureNamePos=0.25（0=屏幕底 1=顶） | ShowGestureName=true（:393）、GestureNamePos=0.25（:411） | 一致，语义都是屏幕纵向百分位 |
| 名称颜色/描边 | LabelColorNormal=#00000030、LabelColorExecuted=#FF5900FF、LabelBorderWidth=1 | ShowLabelBorder=true（:399）、LabelBorderWidth=1（:402）、LabelColorNormal/Executed（:405、:408）+ LabelLingerMs=1200（:359） | Input Plus 多一个“名称停留时长” |
| 执行后淡出 | FadeOutExecuted=true | FadeOutExecuted=true（:396）；SettleOverlay:1752 复刻：勾选→换已执行色停 100ms 消失，不勾→停 250ms 再 250ms 淡出 | 行为对齐，时长写死在代码里 |
| 起始超时 | StartDragTimeout=500、ShowStartDragTimeoutIndicator=true | StartTimeoutMs=500（:268，clamp 100~3000）、ShowStartTimeoutIndicator=true（:414）+ 红点提示开关（xaml:345） | 一致 |
| 作用目标模式 | TargetMode=UnderCursor（另一值 Focused） | TargetMode=pointer（另一值 active，:199-213） | 同构命名不同；active 明确“不切窗口” |
| 轨迹/标签之外的偏好 | ShowStatusIcon=true（托盘状态图标）、DisableInFullscreen=false、AutoStart=false、PrefWindowTopmost=false | **DisableInFullscreen 已对齐**（v1.4.1，手势参数卡「全屏程序里不接管」，默认关；v1.5.2 起只认无边框盖满屏幕的真全屏，最大化窗口不算）；AutoStart 在设置页「开机自启」里；PrefWindowTopmost 也已对齐（v1.5.0）；ShowStatusIcon 在 v1.5.2 删掉 —— 托盘图标一直显示，右键菜单本来就有「鼠标手势：已开启 / 关闭」 | 除「显示 / 隐藏托盘图标」这个冗余偏好外全部对齐 |
| 参数面板 | 只在 OptionsPage 露出 StartDragTimeout/StrokeMinMove | 另有 CornerSize=24、HotCornerSize=8、HotCornerReset=80、EdgeSize=3、WgThreshold、WgImmediateThreshold、WgTieMargin（:302-330） | Input Plus 把 WG2 硬编码的几何常量也做成了偏好 |

### 2.6 边缘 / 滚轮 / 热角

| 能力 | WGestures 2 怎么做 | Input Plus 怎么做 | 差在哪 |
|---|---|---|---|
| 表达方式 | 是手势序列里的**步骤**：MoveToEdgeCorner（A/P.cs:18-60，只存 RectEdgeCorner 位掩码）、Scroll（a.q，只有 IsHorizontal 布尔） | 是独立的 Kind：corner / hotcorner / wheel / edgewheel（GestureModels.cs:71-90），各自带 Corner(1左上2右上3左下4右下)、Edge(1上2右3下4左)、Wheel(-1上+1下-2左+2右) | Input Plus 可以“边缘 + 61 条触发方式”自由组合；WG2 只能和笔画串成序列 |
| 屏幕几何 | RectEdge [Flags] Top=1/Right=2/Bottom=4/Left=8；RectCorner LeftTop=9/RightTop=3/RightBottom=6/LeftBottom=12（RectEdge/RectCorner.cs），合并成 8 个热区 | CornerSize=24 判“角落手势”半径、HotCornerSize=8 判热角、EdgeSize=3 判贴边（:318/:324/:330） | WG2 用位掩码表达 8 个面，Input Plus 用像素半径做命中判定 |
| 热角触发 | 全局检测器 B/E.cs（B.E）+ 多屏 Rectangle 缓存，尺寸硬编码（约 2px，Input Plus 注释亦称 WG2 用 2px，GestureModels.cs:324） | OnHotWatch:854：仅无按键、非绘制、真贴角时触发，需离开 HotCornerReset=80px 才复位 | Input Plus 半径更大、带防抖复位，误触更少但“贴角即触发”的灵敏度不如 WG2 |
| 沿边移动 | A/L.cs:272 用 ClosestAxis4 判定：贴垂直边只收上下移动，不生成笔画；A/K.cs:505-530 里 250ms 内连划两次 → 升级为 MoveToEdgeCorner + Edge 组合 | 无沿边移动语义；贴边只用于 edgewheel | 缺口：WG2 的“边缘双击转角落”在 Input Plus 没有对应物 |
| 滚轮手势 | Scroll 步骤只区分横/纵，方向靠 VSCROLL:-120 这类键值表达 | OnWheel:610 四路：按住键的 wheel 手势、角落无键滚轮、贴边滚轮、物理键 IsPhysicallyDown 兜底；Wheel 四方向显式枚举 | Input Plus 更明确；WG2 更灵活（滚轮可作为任意步骤） |

### 2.7 快捷键开关

| 能力 | WGestures 2 怎么做 | Input Plus 怎么做 | 差在哪 |
|---|---|---|---|
| 总开关热键 | PauseResumeHotkey=#({ESC})：WG 键语法，支持 Ctrl/Shift/Alt/Win（HotKeyModifierKeys），界面有录制控件（HotkeyTextBoxBehavior） | ToggleHotkey=""（默认不占用，GestureModels.cs:274）+ RegisterHotKey(id 0x4B47, MOD_NOREPEAT)（GestureService.cs:747/:813），界面“录制/清除”（xaml:27-34） | Input Plus 出厂不抢键更友好；但只有单组合键，没有“按住临时暂停” |
| 触发键/修饰键体系 | 触发键就是手势第一步 KeyDown（MOUSE:1/2/3/4、KEYBOARD:87），修饰键只服务全局热键 | Button left/right/middle/x1/x2（GestureButtons.cs:431 ↔ VK 0x01/02/04/05/06）+ Chord 第二键 | 模型一致；Input Plus 多了组合键（Chord）维度 |
| 暂停语义 | IsPaused / SetPaused 也在 Lua API 里暴露（WG.cs） | ToggleGestures:803 只切总开关，无脚本面 | 缺口：脚本无法调用暂停 |

### 2.8 导入导出与迁移

| 能力 | WGestures 2 怎么做 | Input Plus 怎么做 | 差在哪 |
|---|---|---|---|
| 格式 | 自家备份 .wg28bw（zip 包 gestures.json+prefs），导入兼容 .wg2bw/.wg27bw/.wg28bw（PrefWindow.xaml.cs:146-200） | 命令行 --gesture-import <gestures.json 或 .wg28bw> [--keep]（GestureImport.cs:29），默认清空再导入，日志 logs/gesture-import.txt，退出码 0/1/2/3 | Input Plus 是单向消费 WG2 备份，没有反向导出 |
| 手势级 .ipg | 无（只有整包 .wg28bw） | v1.5.0 加过（GestureExport.ToJson / Import），v1.5.2 撤掉：手势随「同步与备份」与「导出配置」整份走，再留单表入口只是多一条会走岔的路 | 不再是差距；反向喂给 WG2 仍然没有 |
| 翻译覆盖 | — | WGesturesImporter.cs:94/:112/:127 支持 KeyDown/Stroke/MoveToEdgeCorner 步骤；:215 TranslateCommand 覆盖 SendKeys/RunApp/WebSearch/Cmd/WindowControl 五类；:265 Script 跳过并计数 Note；AHK 键语法转换 :314/:401（^v、%{F4}、{LCtrl}(c)、{VOLUME_DOWN 2}） | Lua 与 MenuCommand 会丢，其余可一一对上；每条带 FromWGestures 徽章 |
| 重复导入 | 整体覆盖，无去重概念 | Signature 去重（:451）+ GestureIdentity.Find/FindSimilar（相似会提示“可能互相抢”，GesturesView.xaml.cs:1799） | Input Plus 更谨慎 |
| 自动迁移 | 旧字段标 [Obsolete("only for migrating. since 2.2.0")]，启动时静默修正 | AutoImportOnce（GestureService.cs:223）只在“没导过+自己没手势+找到 WG 配置”时导一次；每源只一次 | 都是“只跑一次”策略；Input Plus 有可追溯的导入日志 |
| 整机备份 | 单文件导出，无同步 | BackupView：加密/明文导出 + WebDAV 自动同步（UI/Views/BackupView.xaml:14 注释、:256-354） | Input Plus 明显更强；但手势表本身没有单独导出入口 |

### 2.9 性能与钩子架构

| 能力 | WGestures 2 怎么做 | Input Plus 怎么做 | 差在哪 |
|---|---|---|---|
| 钩子 | 自建 WH_MOUSE_LL + WH_KEYBOARD_LL，带 SetWindowsHookEx 失败重试与钩子线程看护（A/D.cs:285/291、550/556、702/708） | 全程序唯一的 WH_MOUSE_LL（Core/HookHost.cs，:269 有“改用自身钩子”回退日志）；GestureService.cs:168-173 注释写明“装两条只会互相拖慢” | Input Plus 少一层钩子、与其他模块共享；代价是接管能力受宿主约束 |
| 采样方式 | 钩子回调里按 30px 间距追加点（A/G.cs:72） | DispatcherTimer 8ms 轮询（:925）+ 2px 一处（:966）+ 8 秒 stale 兜底（:944）+ 每 3 点预览（:1021） | 轮询降低钩子负担，但极快甩笔时可能丢点 |
| 误触发工程 | 无频率限制；靠 Level 择优 | GestureBurstLimit=20 / GestureBurstWindowMs=10_000 / Cooldown 3_000（:67-70）；注入事件需 TakeGesturePass 通行证（:342-350）；同键二次按下修抬键丢失（:390-398）；任务栏/托盘不接管（:329）；空白起笔吞事件 CanStart（:1409） | **Input Plus 明显更强**，这些都是实战误触才长出来的补丁 |
| 覆盖层 | A/T.cs 轨迹窗口，淡出 100/250/50ms（T.cs:145/165/519-578） | GestureOverlayWindow + SettleOverlay:1752（同款时长），HitLabelLingerMs=0（:1744） | 对齐 |
| 诊断 | 无内置自检 | GestureProbe.cs（319 行）、SelfTest.Gestures.Wg2.cs、Core/HookSelfTest.cs、导出诊断日志 | Input Plus 独有 |

### 2.10 v1.5.0 补齐情况（原「缺失能力一览」）

v1.4.2 时这张表列了 7 项缺口，v1.5.0 一次全做完：

| 能力 | 现在在哪 | 做到什么程度 |
|---|---|---|
| 多步手势（≤8 步） | GestureModels.GestureStep + GestureService.CloseStepLocked / PickSteps / StepDistance；UI 在详情卡「步骤」 | 五种步骤（画一笔 / 按键 / 滚轮 / 角落 / 贴边），步数类型方向逐项比；自检覆盖模型、距离、挑选 |
| Lua 脚本动作 | Core/Scripting/LuaRunner.cs + UI/Windows/LuaScriptsWindow；动作 id lua:<scriptId> | MoonSharp 硬沙盒、独立线程、5 秒超时、ip.* 约 30 个入口、中文速查表、结果回写 |
| 手势菜单 | Core/Scripting/ScriptModels.GestureMenuDef + GestureMenuWindow / GestureMenusWindow；动作 id menu:<menuId> | 最多 12 条、1..9 快选、点别处就收、三栏管理窗、复制菜单换新 Id |
| 全屏禁用 / 开机自启 | DisableInFullscreen（v1.4.1，v1.5.2 收紧为真全屏 / 无边框全屏，最大化不算）；自启在设置页 | 齐了；「显示托盘图标」偏好 v1.5.2 删掉（图标始终在，右键菜单能开关手势） |
| 沿边移动 / 边缘双击转角落 | GestureService.HugsEdge / EdgeAt / IsEdgeDoubleTap | 贴边滑一段触发「沿边移动」；四角 420ms 内双击同一个角 |
| 手势级导出 / 导入 | v1.5.2 已删（原 Core/Gestures/GestureExport.cs）；手势随整份备份 / 导出配置走 | 不再提供单表 .ipg；要搬手势就整份导出勾「鼠标手势」 |
| 同形手势按目标层级择优 | GestureService.MatchWithScope / ScopeRank | 全局 0 / 分组 1 / 具体程序 2，程序专属不比全局差 0.02 就赢 |
| 手势演示回放 | 详情卡「回放」（v1.5.2 起画在笔画框里，不再用 GestureService.Replay 的光标旁舞台） | 框内重画一遍，笔画淡作底色，画完自动收 |

仍然没有的只有一项：**反向导出成 WGestures 2 的 .wg28bw**（迁移是单向的）。

## ③ Input Plus 明显更强的地方

1. **动作库粒度**：143 个静态动作 + 17 个分类（ActionCatalog.cs:77 起），文本编辑 26 个（大小写转换、扩选、包裹）、窗口 26 个（九向吸附、置顶、透明度）都是 WG2 的 WindowControlCommand 枚举给不出的。
2. **触发方式 × 三态继承**：61 条触发方式 × 4 层显式覆盖（Wg2Triggers.cs:58-79/:288-328），比 WG2 的“逐层并集 + Level 分数”可预测得多，且 UI 用三态胶囊直接看得出“这一条是继承还是显式”。
3. **识别工程化**：闭环 16 变体（GestureEngine.cs:346-377）、松手切尾（:437/:468，注释里带 1800 组样本量化 56.0%→85.4%）、AwaitsLonger 防抢跑（:506）、immediate 阈值分档（:341）——这些 WG2 一个都没有。
4. **误触发抑制**：频率限制 20 次/10 秒 + 3 秒冷却、注入事件通行证、任务栏不接管、空白起笔吞事件（GestureService.cs:67-70/:329/:342-350/:1409）。
5. **可调参**：匹配容差、起手距离、换向系数、几何半径全部暴露（GestureOptionsView.xaml:378-494），WG2 只能改源码。
6. **录制不抢焦点**：专用 GestureRecordWindow + WM_XBUTTONDOWN 兜底侧键（:21-22），比“全局录制态”更少误操作。
7. **可诊断**：GestureProbe、自检、导入日志、相似手势预警（“和『X』很像，识别时可能互相抢”）。
8. **备份能力**：加密导出 + WebDAV 同步（BackupView.xaml:256-354），WG2 只有单文件。

## ④ 还剩什么（v1.5.0 之后）

v1.4.2 那张「按性价比排序」的补课表，八项已经在 v1.5.0 全部落地（多步手势、手势菜单、沿边 + 双击角落、层级择优、Lua 脚本、演示回放，以及 v1.5.2 撤掉的托盘图标开关与手势级导入导出），所以这里改成"还剩什么"：

| 项 | 现状 | 值不值得做 |
|---|---|---|
| 反向导出给 WGestures 2（.wg28bw） | 只能读不能写 | 低：WG2 已停更，搬回去的人少 |
| Lua 里控制手势总开关（ip.pause） | ToggleGestures 只在 UI 与托盘 | 中：脚本想"临时关掉手势"时有用，一行包一层即可 |
| 按标题找窗口 / 取资源管理器路径这类 WG.cs 专有 API | ip.win.* 只操作前台窗口 | 中：真正常用的就"切到某个标题的窗口" |
| 菜单的二级子菜单 | 单层，最多 12 条 | 低：WG2 的二级菜单很少被用满 |
| 多步手势的可视化编辑器（拖步骤、预览整条序列） | 只能加 / 删 / 清空 | 中：步骤一多就想调顺序 |

## ⑤ 附：关键源码位置

### WGestures 2（D:\Local AI\_归档\WGestures 2\src）

| 主题 | 位置 |
|---|---|
| 手势=步骤序列、8 步上限、前缀匹配 | A/J.cs（:85-105 前缀）；A/L.cs:230 |
| 笔画步骤、1024 点、StrokeMinMove=30、末步 ×1.25 | A/P.cs:110 起；A/L.cs:60、:141 |
| 边缘/角落步骤 | A/P.cs:18-60（RectEdgeCorner） |
| 按键步骤 / 滚轮步骤 | A/Q.cs:19-100、A/Q.cs:103-160 |
| 距离（切线角剖面+路径积分）、角度折回 | A/G.cs:300-370、:378；采样间距 A/G.cs:72 |
| 识别主循环、阈值 0.05 / 0.01 / 250 | A/m.cs:126、:129、:148、:478-560、:569 |
| 目标 Level：General 10 / Group 100 / App 1000 / Special 10000 | A/R.cs:54、A/R.cs:94、A/S.cs:22、A/S.cs:45 |
| 目标占用检测、孤儿清理 | A/Q.cs:270-310、:245-270 |
| 偏好默认值（代码内兜底） | A/a.cs:60-300 |
| 偏好默认值（随包） | full/Resources/prefs.json |
| 默认手势表 | full/Resources/gestures.json（General 53 Intent / 61 Trigger） |
| 钩子 | A/D.cs:285/291、550/556、702/708 |
| 轨迹窗口/淡出 | A/T.cs:57、:145、:165、:519-578 |
| 沿边移动判定、250ms 双击转角落 | A/L.cs:272；A/K.cs:505-530 |
| 热角检测器、多屏缓存 | B/E.cs（B.E，A/R.cs:790 装配） |
| 录制态 | A/m.cs:105、:157、:172；YingDev.WGApp.Views/IntentListViewModel.cs:201-228 |
| 偏好 UI | YingDev.WGApp.Views/OptionsPageView.xaml:35-185 |
| 命令实现 | YingDev.WGApp.Command/（SendKeys/Cmd/RunApp/WindowControl/WebSearch/Menu/Script） |
| Lua API | YingDev.WGApp.LuaApi/WG.cs（785 行） |
| 备份导入导出 | YingDev.WGApp.Views/PrefWindow.xaml.cs:146-200 |

### Input Plus（D:\Local AI\git\Input-Plus源代码\src\InputPlus）

| 主题 | 位置 |
|---|---|
| 识别入口、采样加工、RDP 简化 | Core/Gestures/GestureEngine.cs:32、:147、:164、:169-173、:179、:196、:231 |
| 匹配、歧义、闭环、切尾、防抢跑 | GestureEngine.cs:331、:341、:346-377、:399、:406-417、:437、:468、:506、:545 |
| 笔画类型与距离移植 | Core/Gestures/WG/WgStroke.cs:28、:31、:34、:212、:256、:336；WG/WgPath.cs:80、:108、:160-167 |
| 阈值/几何默认值 | Core/Gestures/GestureModels.cs:262、:268、:302、:308、:315、:318、:324、:327、:330 |
| WG2 同名偏好段 | GestureModels.cs:361-417（ShowPath/PathColor*/PathLineWidth/FadeOutExecuted/GestureNamePos…） |
| 手势模型（Button/Chord/Kind/Path/Wheel/Corner/Edge） | GestureModels.cs:20-140；GestureButtons :431 |
| 61 条触发方式、三态解析 | Core/Gestures/Wg2Triggers.cs:58-79、:205、:264、:288-328、:331、:361 |
| 运行时：钩子复用、起手、采样、收尾、执行 | UI/GestureService.cs:67-70、:168-173、:317、:329、:437-443、:925、:933、:966、:1136、:1248、:1409 |
| 热角/滚轮 | GestureService.cs:854、:610 |
| 总开关热键 | GestureService.cs:747、:813、:803 |
| 覆盖层时序 | GestureService.cs:1731、:1744、:1752 |
| 动作库 | Core/Actions/ActionCatalog.cs:77、:129 起（146 处 Add） |
| 录制窗口 | UI/Windows/GestureRecordWindow.xaml.cs:21-22、:109、:185、:223、:249、:266 |
| WG2 导入翻译 | Core/Gestures/WGesturesImporter.cs:94、:112、:127、:215、:265、:314、:401、:451 |
| 整表导入 CLI | Core/Gestures/GestureImport.cs:29 |
| 外观/参数 UI | UI/Views/GestureOptionsView.xaml:27-34、:57、:73、:94、:111、:133、:169、:233、:249、:265、:281、:315、:345、:378-494 |
| 手势页（目标/触发方式/编辑器） | UI/Views/GesturesView.xaml:86、:94、:135-172、:184、:310、:412、:569-789 |
| 覆盖层取色/描边/阴影 | UI/Windows/GestureOverlayWindow.xaml.cs:107-131 |
| 目标/触发方式构建 | UI/Views/GesturesView.xaml.cs:117、:402、:563、:1335、:1435、:1453、:1787 |
| 内置手势与补种 | Core/Gestures/GestureDefaults.cs:28、:216、:548、:565；Assets/gestures-builtin.json（52 条） |
| 诊断/自检 | Core/Gestures/GestureProbe.cs（319 行）；SelfTest.Gestures.cs；SelfTest.Gestures.Wg2.cs；Core/HookSelfTest.cs |
| 备份 | UI/Views/BackupView.xaml:14、:256-354 |
