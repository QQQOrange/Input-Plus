# 构建与运行（Windows 10 1809+ / Windows 11 x64 / .NET 10）

构建需要 **.NET 10 SDK** 和 **Rust 1.91+**（文件索引内核是 Rust cdylib）；
**运行只需要 .NET 10 桌面运行时** —— 单文件发布里已经包含我们自己的原生库。

## 1. 先编原生索引内核（ipidx.dll）

```powershell
cd src\ipidx
cargo build --release        # 产物：target\release\ipidx.dll（约 5.5 MB）
```

- 依赖是 **vendored** 的：`third_party\tantivy`（v0.27.0），不需要联网拉仓库；
- 首次构建约 30~60 秒（tantivy 本体），之后增量几秒；
- 自测：`cargo test`（18 个单元测试 + 27 个夹具集成测试，覆盖中文子串 / 拼音 /
  ext / size / date / path / 取反 / 目录 vs 文件 / 正则 / glob / 删除 / 统计）；
- `ipidx.dll` 由主工程 csproj 以 `Content` 拷进输出目录与发布目录，
  单文件发布时靠 `IncludeNativeLibrariesForSelfExtract` 一起打包、运行时解压加载；
- **Release 构建缺这个 dll 会直接失败**（`EnsureIpIdxNative` 目标），
  Debug 只警告 —— 少一个 dll 的包"能启动、搜索永远没结果"，那种错必须在构建期拦住。

## 2. 再编主程序

```powershell
# 还原 & 构建
dotnet build src\InputPlus\InputPlus.csproj

# 单文件发布（框架依赖；运行前需装 .NET 10 桌面运行时）
dotnet publish src\InputPlus\InputPlus.csproj -c Release -f net10.0-windows -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist

# 需要更小（放弃内置自检与离屏渲染，再省约 0.5 MB）
dotnet publish src\InputPlus\InputPlus.csproj -c Release -f net10.0-windows -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeDiagnostics=false -o dist-lean
```

### 发布形态只有一个

v1.5.8.2 起支持 **Windows 10 1809+（build 17763）与 Windows 11，x64**（TFM `net10.0-windows` / `win-x64`）。
目标框架**故意不带平台版本**：带 `10.0.xxxxx` 会让 SDK 引用 25.7 MB 的 WinRT 托管投影
（`Microsoft.Windows.SDK.NET.dll`），而本程序一个 WinRT 类型都没用。实测单文件 12.7 MiB vs 37.7 MiB。
代价是失去编译期的系统版本约束，最低版本改在 `App.EnsureSupportedOs()`（运行期检查）里。
Win7/Win10 与 x86 的形态已经删除：Rust 侧的 tantivy 要求 1.91+ 工具链，
两条线共用一份源码的代价已经大过收益。

```powershell
# 出产物 + 哈希清单 + 发布说明 + manifest（产物在 dist\<版本>\）
powershell -ExecutionPolicy Bypass -File tools\release.ps1

# 不重新构建，只按目录里已有的 exe 重算哈希与清单（上传前复核）
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -SkipBuild
```

产物：

```
dist\<版本>\
  InputPlus.exe       Windows 10 1809+ / Windows 11（x64，框架依赖）
  SHA256SUMS.txt      每行「哈希  文件名」，certutil / 7-Zip 都能校验
  manifest.json       版本 / 大小 / 哈希（CI 与上传脚本用）
  release-notes.md    从 CHANGELOG.md 抽出的本版说明（Release 正文）
```
```

> 版本号只认 `src\InputPlus\InputPlus.csproj` 里的 `<Version>`；
> 文件名、页脚版本、更新检查全都跟它走，所以**发版第一步是改 csproj**。
> 标签和 csproj 版本不一致时，CI 会直接失败（避免用错版本号覆盖已发布资产）。

### 持续集成

`.github/workflows/build.yml`：每次推 main / 开 PR 跑两件事 ——
两个 TFM 各构建一遍（必过），再用发布产物跑 `--selftest`（提醒性，
CI 机器没有真实桌面，个别用例会因环境限制跳过）。

`.github/workflows/release.yml`：推 `v*` 标签或手动 Run workflow 时，
用同一份 `tools/release.ps1` 出齐四个形态并创建 / 更新 GitHub Release，
附带 `SHA256SUMS.txt` 与 `manifest.json`。

> **只有一个 exe**。曾经拆成"启动器 + 主程序"两个文件来解决
> "没装 .NET 点了没反应"，但那让用户不知道该双击哪个；
> 也试过把运行时整个打进去，体积从 5 MB 涨到 75 MB。
> 现在是一个**框架依赖的单文件**（约 3.8 MB）：体积和"只有一个 exe"都保住了，
> 代价是机器上要先装 .NET 10 桌面运行时（README 顶部有下载地址）。

## 自检

程序内置 2000+ 项自检，覆盖按键模型、表达式求值、Qbar 检索与标签、
设置读写、旧版 ini 迁移、窗口与进程、输入注入、键盘钩子、组合键状态机、
加密与备份、横条与连续调节。

```powershell
dist\InputPlus.exe --selftest
```

退出码 0 表示全部通过。自检日志写在数据目录（默认在 exe 同级的
`InputPlus Data`，不可写时回落 `%LocalAppData%\InputPlus`），
可用环境变量 `INPUTPLUS_DATA` 覆盖（便于测试与截图，互不干扰）。

### 部署到本机（发版前必做）

**不要**用 `Stop-Process -Force` 直接覆盖 exe（旧版 `dist\install-*.ps1` 就是这么干的）：
强杀会掐断 400ms 防抖保存和自检 `finally` 的写回窗口，2026-11-17 连续丢配置
就是这条链路，复盘见 `docs\DATA-LOSS-POSTMORTEM-2026-11-17.md`。

```powershell
# 1) 数据安全回归：必须 PASS 才能发（旧版会 FAIL）
dist\check-data-loss.ps1 -Exe .\artifacts\InputPlus.exe

# 2) 部署：优雅退出 → 数据快照 → 换 exe → 临时目录自检 → 指纹复核 → 失败自动回滚
dist\deploy.ps1 -Exe .\artifacts\InputPlus.exe
```

> `--selftest` 现在自动使用 `%TEMP%\InputPlus-selftest-<pid>`，
> 备份目录也跟着数据目录走；即便如此，发版流程里也不要指到用户真实目录。

## 界面截图 / 回归

离屏渲染所有页面与浮层，结果不受桌面遮挡、DPI 缩放和焦点动画影响，可复现：

```powershell
dist\InputPlus.exe --render docs\screenshots
```

## 性能基准

```powershell
tools\bench.ps1
```

## 命令行参数

| 参数 | 作用 |
| --- | --- |
| `--selftest` | 跑自检并退出 |
| `--render <目录>` | 离屏渲染全部界面 |
| `--page=<页面>` | 启动并直接打开某页（guide/keys/qbar/clipboard/macros/layers/backup/settings/test/help/about） |
| `--qbar [--text=关键词]` | 直接打开 Qbar |
| `--tray` | 只驻留托盘，不显示窗口 |
| `--noelevate` | 不请求管理员权限（调试用） |
| `--elevated` | 内部使用：标记已提权，避免反复弹 UAC |

## 目录结构

```
src/InputPlus/
├─ Core/                  与界面无关的核心逻辑（可无界面自检）
│  ├─ Input/              键盘钩子、Caps 组合键状态机、按键名映射、输入注入
│  ├─ Actions/            动作目录、动作上下文、剪贴板、窗口绑定
│  ├─ Windows/            窗口枚举 / 激活 / 贴边 / 置顶 / 透明度
│  ├─ Text/               选区读写、表达式求值
│  ├─ Qbar/               Qbar 检索、标签（Chip）
│  ├─ Icons/              图标缓存、极简 SVG 渲染器
│  ├─ Settings/           JSON 设置、旧版 ini 迁移、开机自启
│  ├─ Update/             检查 / 下载 / 替换更新
├─ UI/                    WPF 界面
│  ├─ Windows/            主窗口、Qbar、计算器、引导、Toast、更新提示
│  ├─ Views/              9 个页面
│  ├─ Theme/              主题、Win11 窗口外观
│  └─ AppHost.cs          装配中心：引擎 + 托盘 + 窗口
├─ Theme/                 浅色/深色调色板与控件模板
└─ SelfTest.cs            自检
```

新增一个可绑定动作只需在 `Core/Actions/ActionCatalog.cs` 里加一行，
界面（按键映射页、快捷键速查页）会自动出现该动作。
