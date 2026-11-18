# WGestures 2 手势设置项与渲染规格（对照 Keyboard enhance）

> 勘察对象：D:\Local AI\WGestures 2（src 为 ILSpy 反编译 C#，run\Resources 为运行时资源）。对照对象：D:\Local AI\git\Input-Plus源代码（下称 KE）。
> 本文只记录代码里真实存在的行号与片段；WG2 的常量字符串被混淆（B4CBD2B5-...-D61786963077 字节表非 UTF-8，无法还原），凡涉及常量文本处均标注推断依据。

## 0. 结论速览

- 偏好字段共 24 项：prefs.json 落盘 18 项（run/Resources/prefs.json:2-19）+ 仅代码默认 4 项（AutoStart / PrefWindowTopmost / DisableInFullscreen / Language，src/A/a.cs:54-117）+ 派生 2 项（ShowPathBorder / ShowLabelBorder，不落盘）。
- 四类目标：General（应用级单例，Level 10）、Group（纯 Guid 集合，Level 100）、App（exe 全路径，Level 1000）、Special（内置桌面/任务栏，按窗口类名，Level 10000）。
- 匹配只看 exe 全路径：窗口标题、窗口类名都不参与 App 目标匹配（B/F.cs:49-65）；类名只用于特殊目标与全屏判定。
- 全局允许的触发方式 = 所有目标 TriggerDef 的并集；General 只是默认清单，某个触发序列若没有被任何目标显式启用，整条序列根本不算手势（A/Q.cs:293-333 + A/m.cs:591-595）。
- 与 KE 的差距共 30 条：完全缺失 13 条、语义不同 11 条、有字段无 UI 2 条、已有等价 4 条（详见第 4 节）。

## 1. 手势偏好全表

| WG2 字段 | tr.json key:行 / 中文（zh_CN） | 默认值（prefs / 代码） | 类型·范围 | 语义 | 生效位置（文件:行 + 片段） |
| --- | --- | --- | --- | --- | --- |
| ShowPath | ShowGesturePath:90 手势轨迹 | true / false | bool | 显示轨迹总开关；同时是超时指示器更新的闸门（历史耦合） | R.cs:1282 a2.ShowPath.Subscribe(P=>A_f3.A(P)) 到 T.cs:244-247 A_f9；读取 T.cs:100、476 |
| PathColorNormal | 无独立 key；预览卡文字借用 GestureRecognized:97 已识别 / GestureUnRecognized:98 未识别（OptionsPageView.xaml:114、123） | #00000030 / Rgba(.5,.5,.5,.5) | 颜色（含 alpha） | 未命中任何 intent 时的轨迹主色 | R.cs:1294 到 T.cs:312-315 A_f10(T.cs:213)；使用点 T.cs:130、397 |
| PathColorRecognized | 同上 | #FF5900FF / Rgba(.4,.8,.9,.5) | 颜色 | 命中 intent 时的轨迹主色 | R.cs:1298 到 T.cs:326-329 a_f2(T.cs:216)；使用点 T.cs:130 |
| PathLineWidth | 滑块两端 PathThinner:93 细 / PathThicker:94 粗（OptionsPageView.xaml:129-132） | 1.5 / 2f | float，滑块 1..4 | 轨迹主线宽（渲染乘 DPI 缩放） | R.cs:1310 到 T.cs:286-289 A(float) 到 B/X.cs:82-85 到 A/U.cs:109-117 |
| PathBorderWidth | ShowPathBorder:22 描边（勾选框，值为派生属性） | 1 / 1f | float 0..4 | 轨迹描边宽，画在主线两侧各一份 | R.cs:1326 到 T.cs:298-301 a(float) 到 B/X.cs:94-97 到 A/U.cs:115 |
| LabelBorderWidth | ShowLabelBorder:21 描边（同上） | 1 / 1f | float | 名称标签边框宽；渲染值 = 值 × 2 | R.cs:1322 T.A((double)(P_0*2f)) 到 T.cs:274-277 到 GestureLabelWindow.BorderWidth |
| ShowGestureName | ShowGestureName:91 手势名称 | true / false | bool | 名称标签总开关（关掉仍切轨迹颜色，但不显示名字） | R.cs:1286 到 T.cs:332-335 a(bool)=a_f3；读取 T.cs:135、143、163、509、517、538 |
| GestureNamePos | 无独立 key；滑块两端 ScreenBottom:102 屏幕底部 / ScreenTop:103 屏幕顶部（OptionsPageView.xaml:161-165） | 0.25 / 0.75f | float 0..1 | 标签在屏幕竖向的位置：0=贴底、1=贴顶；不是路径上的比例 | R.cs:1314 到 T.cs:356-360；公式 T.cs:390-392 |
| LabelColorNormal | 无独立 key（预览卡 GestureRecognized:97） | #00000030 / Color(128,50,50,50) | 颜色 | 标签底色：预览、未识别、以及未开启 FadeOutExecuted 的执行态 | R.cs:1302 到 T.cs:250-259 A_f5(T.cs:187-193) |
| LabelColorExecuted | 无独立 key（预览卡 GestureExecuted:96 已执行） | #FF5900FF / Color(255,0,180,200) | 颜色 | 标签底色：执行且 FadeOutExecuted 开启时 | R.cs:1306 到 T.cs:262-271 a_f(T.cs:195-201) |
| FadeOutExecuted | FadeOutExecuted:20 执行后淡出 | true / false | bool | 执行后「停 100ms 后 1ms 瞬间消失」还是「停 250ms + 250ms 淡出」 | R.cs:1318 到 T.cs:371-374 b_f(T.cs:225)；分支 T.cs:145、165 |
| StartDragTimeout | StartDragTimeout:86 手势起始超时；StartDragTimeoutDesc:87 说明；StartDragTimeoutNever:76 永不 | 500 / 0 | int 毫秒，slider 0..3000 步进 10（OptionsPageView.xaml:62-73） | 按下后超过该时间未移动，放行成普通点击/拖拽；≤0 表示永不 | R.cs:1260 到 A/m.cs:384-387（A_f19，默认 250 见 m.cs:148，getter m.cs:377）；启动计时 T.cs:65、79-88 |
| ShowStartDragTimeoutIndicator | ShowStartDragTimeoutIndicator:88 超时后显示此标志 | true / false | bool | 是否 arm 超时计时器；不勾则完全不出现 24×24 指示器 | R.cs:1290 到 T.cs:344-347 B_f；闸门 T.cs:65、445 |
| StrokeMinMove | StrokeMinMove:89 手势最小识别距离 | 30 / 30 | int 像素，slider 10..100 步进 5（OptionsPageView.xaml:81-92） | 相邻采样点间距阈值（不是整条轨迹长度）；上一步是键盘键时用 ×1.25 | R.cs:1276 到 A/m.cs:352-355 到 A/L.cs:130-133；判定 L.cs:263-268 |
| TargetMode | GestureTargetMode:99 手势作用目标；TargetModeFocused:77 活动的应用程序和窗口；TargetModeUnderCursor:78 鼠标指针下方的应用程序和窗口（OptionsPageView.xaml:93-99） | UnderCursor / UnderCursor | enum Focused, UnderCursor | 手势作用到前台 exe 还是指针下 exe | R.cs:1264 到 A/m.cs:291-294；判定 m.cs:597（首步是边/角步时强制 Focused） |
| ShowStatusIcon | ShowStatusIcon:92 在系统托盘中显示 WGestures | true / false | bool | 托盘图标显示 | R.cs:1268 到 B(bool)（托盘宿主） |
| SkipVersion | 无 key | null / null | string | 升级提示里「跳过此版本」记下的版本号 | A/a.cs:269；写入 Views/NewVersionWindow.xaml.cs:37；比较 A/R.cs:979 |
| PauseResumeHotkey | PauseResumeHotkey:13 暂停/继续 WGestures | #({ESC}) / 空 | 热键字符串 | 暂停/恢复全部手势的全局热键 | A/a.cs:287；UI OptionsPageView.xaml:47-61（HotkeyTextBoxBehavior） |
| AutoStart | AutoStart:25 自动运行 WGestures | false | bool | 开机自启 | A/a.cs:54、122；订阅 A/R.cs:1256-1259 |
| DisableInFullscreen | DisableInFullscreen:14 全屏时自动暂停 | false | bool | 全屏应用下整条手势判定直接返回 false | R.cs:1272 到 A/m.cs:340-343；判定 m.cs:586-590 |
| PrefWindowTopmost | 无 key（标题栏图钉） | false | bool | 设置窗口置顶 | A/a.cs:57；PrefWindow.xaml:83-99 |
| Language | Language:2 界面语言 | （空）/ （空） | string | 界面语言；改动后提示重启（Lang_Restart:7） | A/a.cs:117；OptionsPageView.xaml:41-46 |
| ShowPathBorder（派生） | 沿用 ShowPathBorder:22 描边 | =PathBorderWidth>0 | bool | 只写不存；true 写 1，false 写 0 | Views/OptionsPageViewModel.cs:64-74；通知订阅 110-113 |
| ShowLabelBorder（派生） | 沿用 ShowLabelBorder:21 描边 | =LabelBorderWidth>0 | bool | 同上 | Views/OptionsPageViewModel.cs:52-62；通知订阅 106-109 |

> 命名错位提醒：T.cs 里三个开关的字段名与偏好项并不一一同名——A_f9 来自 ShowPath（T.cs:244）、a_f3 来自 ShowGestureName（T.cs:332）、B_f 来自 ShowStartDragTimeoutIndicator（T.cs:344）。按上面的「读取点」为准，不要按字段名猜。

## 2. 作用目标 / 分组 / 应用程序 / 特殊目标

### 2.1 数据模型（四类）

| 类 | 基类/位置 | 持久化 TypeId | Level | 字段 |
| --- | --- | --- | --- | --- |
| General | A/R.cs 内 R : P | 单例，不进列表 | 10（R.cs:52-55） | Intents、Triggers（全应用共享） |
| Group | A/R.cs:78-110 r : P | 随 settings 落盘为 Groups 列表 | 100（R.cs:92-95） | RxSet<Guid> Children（只存 App/Special 的 Id）、Name、Intents、Triggers |
| App | A/S.cs:16-40 抽象 S，实现 B/Y.cs:16-128 Y | WinAppTarget | 1000（S.cs:20-23） | RxProp<string> Path（可执行文件全路径）、Name、Intents、Triggers |
| Special | A/S.cs:41-56 抽象 s，实现 WGApp/WinDesktopTarget.cs:10、WinTaskbarTarget.cs:10 | WinDesktopTarget / WinTaskbarTarget | 10000（S.cs:41-46） | 无额外字段；IsBuiltInTarget() 返回 true |

目标基类 A/P.cs:350-424：Guid Id、RxProp<string> Name、RxList<GestureIntent> Intents、RxList<TriggerDef> Triggers、abstract int Level()、virtual bool IsBuiltInTarget()。
设置根 A/Q.cs:169-426：General、Groups、Apps、Specials 四个集合；Q.cs:259-277 每次加载清掉 Groups.Children 里已不存在的 Guid（分组只存 Id 的直接后果）。

### 2.2 候选值从哪来

- 应用程序：不是「枚举进程列表」。用户用 TargetPickerWindow 的十字准星对准任意窗口，WG2 取该窗口进程的 exe 全路径 + 文件说明，构造 Y（B/Y.cs:111 a(path,name)）。命中时 B/F.cs:49-65：

      (string, string)? tuple = H.A((int)query.Pid, A_f3, A_f2);   // exe 全路径 + FileDescription（带缓存）
      for (int i = Apps.Count - 1; i >= 0; i--)                      // 后加入者优先
          if (Apps[i] is Y y && y.Path.Value == tuple.Item2) return ...;

- 特殊目标：代码内置两个类，没有「选择」流程——桌面 = WinDesktopTarget（A/H.cs:1466-1479：根祖先窗口类名 == Progman/WorkerW，或窗口矩形 == GetShellWindow() 矩形）；任务栏 = WinTaskbarTarget（H.cs:1481-1484：类名属于 {Shell_TrayWnd, Shell_SecondaryTrayWnd}）。开始菜单在 H.cs:1529-1532（G(nint)，类名表见 H.cs:1204-1208），但它不是目标类型，只用于点击穿透判断。
- 分组：纯容器，添加时默认名叫 group + Groups.Count（TargetListViewModel.cs:148-158）。
- General：恒存在，不能删。

### 2.3 UI 结构

- 外壳 Views/PrefWindow.xaml（107 行）：TabControl:62，标题栏 Import/Export/Reset:77-81，置顶图钉:83-99。
- 手势页 Views/GestureListPageView.xaml（45 行）：Grid 三列 4*/6*/5*（10-14）、两行（15-18）；左列跨两行 = TargetListView(25)，中上 = DefsListView(26)，中下 = IntentsListView(27)，右 = IntentDetailView(28)；未选中目标时盖占位（41-43）。
- 目标树 Views/TargetListView.xaml（32 行）：YDTreeView targets(4)，AllowDrop + TreeViewDragDropBehavior(5-7)，三层 DataTemplate——Header 高 24（16-18）、Group 高 26（19-21）、BaseTargetItem（22-24）；标题文字 tr TargetList:32（28-30）。
- 列表构造 TargetListViewModel.cs:54-60：GeneralTargetItemViewModel、header(Groups)、header(Apps)、header(Special Targets)。
- 添加应用 94-126（已存在则选中并滚动，否则 Apps.Insert(0, ...) 插到最前，:115）；新分组 148-158；拖拽重排 160-204（按 header.Source 调 RxList.Move）；拖进分组 206-213（Children.Add(Id)）；删除 246-270；重定位 129-146。
- 目标行 Views/TargetItemView.xaml.cs：点图标重新定位 83-122；拖拽提示文案 AppTargetItemDragTooltip:266 / SpecialTargetItemDragTooltip:267。
- 默认数据（run/Resources/gestures.json）：General.Triggers 61 条、Groups 0 个、Apps 2 个（含 cmd.exe）、Specials 1 个（Desktop）。

### 2.4 匹配语义与优先级

- 判定顺序（A/Q.cs:359-413）：Special 到 App 到 App 所属 Group（Q.cs:335-357）到 General；每个目标内 Triggers.FindBy(..., TriggerDef.HIT_TEST) 取第一个命中。
- 优先级数值只用于识别阶段排序去重：General 10 < Group 100 < App 1000 < Special 10000；A/m.cs:501-505 同一 Level 只保留得分最优的一个候选，m.cs:513 按 Level 升序排。
- 窗口标题 / 窗口类名 / 全屏 / 最大化都不参与「哪个目标」的判定（App 只比 exe 全路径）。它们的作用是：
  - 全屏：B/F.cs:170-181 IsFullScreen——取鼠标点的根祖先窗口，桌面类（Progman/WorkerW）直接 false；否则窗口矩形与显示器工作区矩形相等才算全屏（最大化因任务栏占位通常不算）。用于 DisableInFullscreen 与「边角手势强制 Focused」两条路径。
  - 类名：只用于特殊目标（桌面/任务栏/开始菜单）与穿透到子窗口（H.cs:1515-1527 EnumChildWindows）。
  - 标题：仅作为命令行变量 WG_TARGET_WIN_NAME:45 暴露给动作，不参与匹配。
- 目标定位（B/F.cs:126-168）：Focused 用 GetForegroundWindow()；UnderCursor 用 WindowFromPoint；随后跳过自身覆盖层（GWL_USERDATA 魔术值，F.cs:108/141-146）、按 WS_DISABLED/WS_VISIBLE/WS_EX_TRANSPARENT 过滤、GetWindowRect 包含点、再用 H.f 穿透到内容窗口、取 pid。

### 2.5 触发方式模型 TriggerDef

- 结构 A/TriggerDef.cs:11-56：J Def（一条触发序列）+ RxProp<bool> Enabled（默认 true）+ Clone/Equals（按 Def 比）。静态谓词 TriggerDef.cs:13-17：HIT_TEST（Def.A(raw)==0 全等）、STARTS_WITH（Def.a(raw) 前缀，录制时用）、FIND（Equals）。
- 触发序列 J 是 RxList<o>（A/J.cs:9），步骤类型只有四种：

| 步骤类型 | 位置 | 含义 | 匹配 | 用户数据 |
| --- | --- | --- | --- | --- |
| P（MoveToEdgeCorner） | A/P.cs:16 | 屏幕边缘或角落 | RectEdgeCorner 相等 | {"$type":"MoveToEdgeCorner","EdgeCorner":{"Value":1}} |
| p（Stroke） | A/P.cs:110 | 一笔鼠标轨迹 | 曲线距离（识别引擎） | {"$type":"Stroke","P":[x,y,...]} |
| Q（KeyDown） | A/Q.cs:16 | 鼠标键/键盘键 | Key 相等 | {"$type":"KeyDown","Key":"MOUSE:1"} |
| q（Scroll） | A/Q.cs:96 | 滚轮 | 纵向比上/下，横向比左/右 | {"$type":"Scroll","IsHorizontal":false} |

- 触发方式全集（gestures.json 默认 61 条推得）：鼠标键 5 种（MOUSE:0 右键 / 1 左键 / 2 中键 / 3 侧键1 / 4 侧键2，tr 117-121）；滚轮纵/横（Scroll，tr 122-123）；屏幕四边 RectEdge Top=1 Right=2 Bottom=4 Left=8 与四角 RectCorner 3/6/9/12（tr 124-131）。每个边缘/角落展开 7 条：裸触发 1 + Scroll 1 + 鼠标键 5，故 5 + 8×7 = 61。键盘键可作修饰键（tr KeyboardModifierTip:239、ModifierRepeatTip:240）。
- 边缘/角落判定（A/O.cs:8-145）：到四边距离 ≤6px 记为边，两边同时 ≤6 记为角（按位或）；不在边角时要求两轴都 ≥100px 才算「远离」。已有边/角状态时，离开超过 16（角）/20（边）px 才清空。A/B/E.cs:37-136：进入点记录、贴边滑动 >20px 触发 MoveOnEdge。
- 滚轮聚合（A/B/E.cs:145-246）：累计 delta，|y|≥110 或（y≠0 且距上次 >250ms）产生一次纵向滚动；横向同理。
- UI 呈现：DefsListView.xaml:9 的 defs 树；行样式 TriggerDefItemView.xaml.cs:23-52 三态（Auto=LightGray、Off=红、On=黑+LimeGreen 指示块）；手势名逐字符着色（57-68）；双击或点指示块切换（76-82）；右键 CopyTriggerId:71 复制 Def.ToString()（84-91）。

### 2.6 「全局允许的触发方式」= 并集，不是白名单

    // A/Q.cs:293-333 —— 返回 true 表示「这条触发序列没被任何目标启用」
    foreach (Special/App/Group/General 中的每个 target)
        if (trigger.Enabled.Value && trigger.Def.A(P_0) == 0.0) return false;
    return true;

    // A/m.cs:591-595
    if (d().Value.A(e.raw)) { ...; return false; }   // A = 上面那个方法

含义：General 的 Triggers 是默认清单，每个非 General 目标把 General 的行派生出来显示（TriggerDefListViewModel.cs:33-55），用户改的是 per-target 的 Override；Auto/On/Off 三态在非 General 上写 Target.Triggers（64-90，Auto 移除覆盖）。只要某个目标把某触发序列设为启用，这条序列在整个应用范围内都合法，具体落到哪个目标由 2.4 的顺序决定。

首步类型影响目标模式（A/m.cs:597）：

    a_f2 = ((!(e.raw.A() is global::a.P)) ? a() : TargetMode.Focused);

即首步是边/角步（a.P，见 A/P.cs:16 的 namespace a 段）时强制 Focused，否则用偏好 TargetMode。J.A() = 首步（A/J.cs:11-14）。

## 3. 轨迹与标签渲染

### 3.1 颜色：整条轨迹只切一次

    // A/T.cs:130  识别回调（(intent, execute, recording) 元组）
    A_f.A_f3.A((gestureIntent != null) ? A_f.c() : A_f.C());   // c()=Recognized, C()=Normal

    // A/T.cs:395-400 起手时先按 Normal 画出来
    public void a(Rectangle P_0) { A_f3.A(C()); ((a.E)A_f3).A(B(P_0)); A_f3.c(true); }

- 起手用 Normal 色；识别回调里一旦 intent 非 null 换成 Recognized 色（同一帧整体换色，不按段）。
- 描边与阴影的 alpha 由主色 alpha 派生（A/U.cs:132-144）：描边 A = 主色 A×0.75，阴影 A = 主色 A×0.075。所以 #00000030（A=0x30）实际描边几乎不可见。
- 未识别时颜色不会变成第三种灰：WG2 只有 Normal/Recognized 两色，未识别 = 保持 Normal + 标签显示「未识别」文案（tr GestureUnRecognized:98）。

### 3.2 粗细与描边

    // A/U.cs:109-117  A(主线, 描边, 阴影, DPI缩放)
    a_f  = P_0 * P_3;                 // 主线
    B_f  = P_1 * P_3;                 // 描边
    b_f  = P_2 * P_3;                 // 阴影，调用处固定传 1f（B/X.cs:84、96）
    A_f4.Width = a_f;
    a_f2.Width = A_f4.Width + B_f * 2f;    // 描边画在主线两侧
    B_f2.Width = a_f2.Width + b_f * 2f;

- 线宽只在偏好变化时设置（R.cs:1310/1326），识别成功不改变粗细，只改颜色。
- 绘制顺序（A/U.cs:317-325）：阴影、描边（B_f>0.1 才画）、主线。采样丢弃阈值 C_f=20px（U.cs:227-239）。

### 3.3 名称位置：GestureNamePos 是屏幕竖向比例

    // A/T.cs:387-393
    int num  = P_0.Left + P_0.Width / 2 - nativeFrame.Width / 2;          // 水平恒居中
    float num2 = (float)P_0.Top + (float)(P_0.Height - nativeFrame.Height) * (1f - A_f11);
    A_f4.NativeFrame = new Rectangle(num, (int)num2, nativeFrame.Width, nativeFrame.Height);

- A_f11 = GestureNamePos（T.cs:222 默认 0.75；prefs 0.25；R.cs:1314 写入并立即重定位 T.cs:356-360）。
- 公式是 (1 - 值)：0 贴屏幕底部，1 贴顶部；0.25 即距底约 25%（1080p 下标签顶边约 735px）。不是路径上的 0.25 位置，与「名称跟着笔画走」无关。
- 标签窗本身是 500×100 的透明子窗口（GestureLabelWindow.xaml），水平居中到屏幕中线。

### 3.4 标签框

- GestureLabelView.xaml：Border CornerRadius=16、BorderThickness=2、Background #55000000、DropShadow(Opacity .5, Blur 8)、TextBlock FontSize 24 / Microsoft YaHei UI / Padding 24,14,24,16 / 白字。
- 边框宽 = LabelBorderWidth × 2（R.cs:1322）；底色 = LabelColorNormal 或 LabelColorExecuted（T.cs:250-271）。
- 宿主窗 GestureLabelWindow.xaml.cs：WS_DISABLED|WS_CHILD + WS_EX_TOOLWINDOW|WS_EX_TRANSPARENT（91-102），不抢焦点。

### 3.5 淡出（FadeOutExecuted）到底多快

    // A/T.cs:141-150 识别回调里（flag = execute）
    if (A_f.D() && !gestureIntent.DontShowName.Value)
        A_f.A(gestureIntent?.Name.Value, A_f.E() ? 100 : 250, (!A_f.E()) ? 1 : 250, A_f.E() & flag);

| FadeOutExecuted | 停留 | 淡出时长 | 底色 |
| --- | --- | --- | --- |
| true（prefs 默认） | 100ms | 1ms（瞬间消失） | LabelColorExecuted |
| false | 250ms | 250ms CubicEaseIn | LabelColorNormal |

- 实现：T.cs:584-590 先把 ExOpacity 置 1，再补间到 0（t 类 T.cs:592-688，延迟 >30ms 走定时器）。
- 只是预览（未执行）时另一条重载 T.cs:165 用 50ms 停留（FadeOutExecuted=false）。
- 淡出期间只淡标签，轨迹保持显示，直到下一次起手（T.cs:57-64）或结束回调 D()（T.cs:117）才清。
- 关掉 ShowGestureName（a_f3=false）时，T.cs:143/163 走 else 分支 a(0.0) 立即隐藏标签。

### 3.6 超时与最小距离的判定顺序

| 顺序 | 判定 | 位置 | 说明 |
| --- | --- | --- | --- |
| 1 | 起手是否「点击类」手势 | T.cs:74-75 | 单步鼠标键，或「边/角 + 鼠标键」两步；左右互换键与自身触发键排除（70-73） |
| 2 | 是否 arm 超时计时器 | T.cs:65 | recording 或 ShowStartDragTimeoutIndicator=false 或 StartDragTimeout≤0 时不 arm |
| 3 | 计时值 | T.cs:79-83 | StartDragTimeout；侧键 X1/X2 取 max(500, 值) |
| 4 | 计时器 tick | T.cs:92-113 | 手势构建器已结束时返回；ShowPath 打开时，若已采样点数 < 1024（L.cs:55 单笔上限）则 24×24 圆点居中跟随光标，否则显示红色提示；最后 e() 关指示器 |
| 5 | 采样间距 | A/L.cs:263-268 | num = 上一步是键盘键 ? StrokeMinMove×1.25 : StrokeMinMove；距离平方 < num 平方则丢点（逐点间距，非总长） |
| 6 | 步数上限 | L.cs:57、202、254 | 最多 8 步、单笔 1024 点 |
| 7 | 识别阈值 | A/m.cs:126、129 | 得分 ≤0.05 收录；近似重复 <0.01 只留一条（m.cs:522-527） |

## 4. 与 Keyboard enhance 现状对照

> KE 行号基准：src/InputPlus/。状态口径：已有等价 / 有字段无 UI / 语义不同 / 完全缺失。

| WG2 项 | KE 现有对应项（字段:行号） | 状态 | 差距说明 |
| --- | --- | --- | --- |
| ShowPath | ShowTrail:GestureModels.cs:221；UI GesturesView.xaml:107 | 已有等价 | 无 |
| PathColorNormal | TrailColor:224 单色；未识别灰硬编码 GestureOverlayWindow.xaml.cs:41-42 | 语义不同 | KE 没有 Normal 色偏好，未识别被强制成灰；用户改色时灰不跟随 |
| PathColorRecognized | TrailColor:224 + ButtonTrailColors:235 / TrailColorFor:244 | 语义不同 | KE 按鼠标键分色（6 个 swatch，xaml:181-229），不区分识别/未识别 |
| PathLineWidth | TrailWidth:226 | 有字段无 UI | 只能改 json；默认 3，WG2 默认 1.5 |
| PathBorderWidth | 无；用 DropShadow 白晕替代（GestureOverlayWindow.xaml:22-35） | 完全缺失 | 没有描边笔，也没有 alpha×0.75 派生机制 |
| LabelBorderWidth | 无；BorderThickness 固定 1（GestureOverlayWindow.xaml:50-72） | 完全缺失 | 无 |
| ShowGestureName | 无 | 完全缺失 | 只能靠 HideLabel:GestureModels.cs:86 逐条隐藏，无全局开关 |
| GestureNamePos | 无；固定工作区底部居中 y=bottom-h-64（GestureOverlayWindow.xaml.cs:198-215） | 完全缺失 | 等价于 WG2 的 0 附近（贴底），无滑杆 |
| LabelColorNormal | DynamicResource Brush.GestureLabelBackground（xaml:50-72） | 语义不同 | 走主题资源（跟随明暗主题），不是用户偏好 |
| LabelColorExecuted | 无独立执行色 | 完全缺失 | 执行与非执行同色 |
| FadeOutExecuted | HideOverlay(HitLabelLingerMs=0) 立即清除（GestureService.cs:1781、1198-1207） | 语义不同 | KE 命中即消失（0ms），WG2 默认停 100ms；KE 无淡出补间 |
| StartDragTimeout | StartTimeoutMs:265；UI GesturesView.xaml:131-136 | 已有等价 | 默认值同为 500 |
| ShowStartDragTimeoutIndicator | 无开关；ShowTimeoutDot 总显示（GestureService.cs:966-973、1694-1703） | 语义不同 | KE 无法关掉红点 |
| StrokeMinMove | MinMove:259；UI xaml:121-126 | 语义不同 | WG2 是逐点采样间距；KE 用总位移 Extent（GestureService.cs:946-948、988、1149） |
| TargetMode | TargetPointer:200 / TargetActive:203 / TargetMode:214；UI xaml:243、247 | 已有等价 | 无 |
| ShowStatusIcon | AppSettings.ShowTrayIcon:AppSettings.cs:495（默认 true） | 有字段无 UI | 设置页没有开关（xaml 无命中） |
| SkipVersion | 无；BuiltinsVersion:332 是迁移版本号，不同义 | 完全缺失 | 无 |
| PauseResumeHotkey | ToggleHotkey:271；UI xaml:113 | 已有等价 | 无 |
| AutoStart | 无 | 完全缺失 | KE 未做开机自启 |
| DisableInFullscreen | 无 | 完全缺失 | ResolveTarget（GestureService.cs:1613-1625）没有全屏短路 |
| PrefWindowTopmost | 无 | 完全缺失 | 无 |
| Language | 无；本地化资源在 InputPlus.Localization/Core/Localization/Loc.cs:231 | 完全缺失 | 设置页无语言选择 |
| ShowPathBorder（派生） | 无 | 完全缺失 | 无 |
| ShowLabelBorder（派生） | 无 | 完全缺失 | 无 |
| 四类目标树 General/Groups/Apps/Specials | GestureOptions.Targets:344（平铺 exe 列表）+ DisabledTargets:241 | 语义不同 | 无分组、无特殊目标、无层级 |
| per-target 触发三态（TriggerDef On/Off/Auto） | 无 | 完全缺失 | 只有 DisabledTargets 整应用禁用，不能按触发方式粒度控制 |
| Special Targets（桌面/任务栏按类名） | ShellClasses 硬编码 GestureService.cs:1599-1602，仅作取值回退 | 语义不同 | KE 里桌面/任务栏不是可配置目标，也不能挂手势 |
| App 目标按 exe 全路径 | SameApp:GestureService.cs:1537-1549（全路径或去扩展名文件名） | 语义不同 | KE 文件名级匹配，同名程序会串 |
| 录制窗口（AddGestureWindow + ReplaceSimilarGestures:236） | 录制入口 GesturesView.xaml:94、596、707 | 语义不同 | KE 有录制，但没有「替换所有相似手势轨迹」选项与缩略图预览 |
| 边缘手势独立分区（EdgeTargetButton） | GesturesView.xaml:272-304 + EdgeTargetCount:300 | 语义不同 | KE 用「当前边缘目标应用」过滤同一张列表，不是 per-target 触发组 |

## 5. 落地建议（最小改动 + 复用 KE 现有 XAML 风格）

### 5.1 直接加进 GestureOptions（不改判定链就能生效）

| 新字段 | 类型/默认 | 接线点 | 风险 | 工作量 |
| --- | --- | --- | --- | --- |
| ShowGestureName（管 LabelBox 是否显示） | bool，true | GestureService.ShowLabel 调用点（1722-1760）加闸门 | 低 | 0.5h |
| GestureNamePos | double 0..1，0.9 | GestureOverlayWindow.ShowLabel 的 y 计算（xaml.cs:208-209）改成 (1-pos) | 中：现有 UX 是底部 64px，改成比例会动到截图与自检 | 1h |
| LabelColorExecuted | string hex，取现有主题色 | ShowLabel 增加 executed 参数，LabelBox.Background 换色 | 低 | 1h |
| PathBorderWidth | double，0 | 在 xaml:22-35 后叠一条外层 Polyline，或 Begin 里再挂一条 | 中：多一条线重绘可忽略，需同步 ResetStyle | 2h |
| LabelBorderWidth | double，1 | LabelBox.BorderThickness 绑定（xaml:50-72） | 低 | 0.5h |
| ShowTimeoutIndicator | bool，true | GestureService.cs:966-973 与 1694-1703 加闸门 | 低 | 0.5h |
| FadeOutExecuted | bool，false | HideOverlay(HitLabelLingerMs) 改成 executed?100:0，并在 Clear 前跑淡出 Storyboard | 中：现在执行后立刻清，改延迟会破坏「名称与轨迹同时消失」不变量（GestureService.cs:1762-1781 注释） | 2h |
| Language / AutoStart / PrefWindowTopmost | 与手势无关，建议放 AppSettings.Global | 无 | 低 | 各 2h（AutoStart 需写注册表 Run 键） |

### 5.2 必须改 GestureOverlayWindow 的

1. 轨迹两段式：Begin() 仍用 Normal 色，新增 SetRecognizedColor(string) 供识别命中时调用，等价 T.cs:130。落点 xaml.cs:88-109（Begin）+ 新增方法。
2. 描边：在 Trail 之后叠一条 BorderLine Polyline，宽度 = TrailWidth + PathBorderWidth*2，颜色 = TrailColor 的 0.75 alpha。落点 xaml:22-35 区域。
3. 标签位置：ShowLabel 的 (x,y) 计算改比例式（公式见 3.3），落点 xaml.cs:201-215。
4. 执行态底色：ShowLabel 增加 bool executed。

### 5.3 GesturesView 要加的控件（按现有卡片分组）

- 顶部开关区（xaml:104-175）：加「显示手势名称」CheckBox（挨着 TrailBox:107），加「超时显示红点」CheckBox（挨着 StartTimeoutBox:136）。
- 轨迹卡（xaml:181-229）：TrailWidth 滑杆（1..6，参照 TrailOpacitySlider:165-172）、PathBorderWidth 滑杆、「已识别/未识别」两个色块（复用 UniformColorButton:183 与 Swatch 模式）。
- 名称卡（新增，放轨迹卡之后）：LabelColorNormal/LabelColorExecuted 两个色块、LabelBorderWidth 滑杆、「执行后淡出」CheckBox、GestureNamePos 滑杆（0..1，两端「底部/顶部」）。
- 作用目标卡（xaml:311-381）：把平铺 TargetList 扩成两层（应用列表 + 分组），每行加三态触发开关按钮。工作量大，建议单独立项；先加「分组」下拉比先加树省事。

### 5.4 需要动 GestureService 判定链的地方

| 目标行为 | 现有行号 | 改法 |
| --- | --- | --- |
| StrokeMinMove 语义对齐（逐点间距） | 929-948、988、1149 | 拆两层：采样间距用 MinMove（OnPoll:935 的 2px 阈值改为 MinMove），总位移只用于「是否算一笔」 |
| 全屏暂停 | 401-404（OnButton 起手门控） | ResolveTarget 之后加 IsFullScreen 短路 |
| 桌面/任务栏作为显式目标 | 1599-1625 | 把 ShellClasses 回退提升成 GestureOptions.SpecialTargets 枚举，命中后走专门分支而非回退到 ForegroundExe |
| 分组优先级 | 267-317（LoadList 过滤） | 先在 GestureOptions 加 GroupId 字段，MatchBucket 前按 Special>App>Group>Global 排序 |

### 5.5 工作量与风险汇总

| 批次 | 内容 | 估时 | 主要风险 |
| --- | --- | --- | --- |
| A | 5.1 中低风险项（名称开关、执行色、边框宽、超时开关、标签位置、语言/自启） | 6h | 低；纯 UI 与渲染参数 |
| B | 轨迹两段色 + 描边 + 淡出 | 6h | 中；动到 overlay 生命周期与 self-test 不变量 |
| C | StrokeMinMove 语义 + 全屏暂停 | 4h | 中；会改变现有手感，需保留旧配置迁移开关 |
| D | 目标树/分组/per-target 触发 | 3-5 天 | 高；等于重做 GesturesView 左栏与 MatchBucket |

### 5.6 未找到 / 存疑（附搜索范围）

- PathColor* / LabelColor* / PathLineWidth / GestureNamePos 都没有独立 tr key：grep tr.json 全部 271 行，只命中 PathThinner:93、PathThicker:94、ShowPathBorder:22、ShowLabelBorder:21；颜色与位置项的可见文字复用 GestureRecognized:97、GestureUnRecognized:98、GestureExecuted:96、ScreenBottom:102、ScreenTop:103。
- WG2 常量字符串无法还原：src/-PrivateImplementationDetails--80D605AE.../B4CBD2B5-...cs 里 636 项偏移表 + 8941 字节数组不是 UTF-8，按 UTF-8 解码为乱码，单字节 XOR 也不成立；涉及类名常量（Progman/Shell_TrayWnd 等）靠 B/F.cs:25 的明文 "Progman" 与比较逻辑反推。
- TimeoutDot 的 1024 比较：T.cs:95 读的是手势构建器 A_f4（L.cs:55 单笔点数上限 1024），语义上更像「笔迹缓冲满」而不是「超时」；A_f18 包装层（m.cs:384-397 往同一通道写 int.MaxValue / StartDragTimeout）未逐行展开，实现 KE 时按「超时后圆点跟手」处理即可。
- 最大化 vs 全屏：B/F.cs:170-181 只做「窗口矩形 == 显示器工作区」比较，最大化窗口因任务栏占位通常不等于工作区，故 WG2 把最大化当普通窗口处理；grep IsZoomed 无命中。
