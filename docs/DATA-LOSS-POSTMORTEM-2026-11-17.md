# 事故复盘：每次发版都丢用户数据（2026-11-17）

## 一句话结论

不是"升级迁移写坏了"。是**发版流程里的 `--selftest` 在用户真实数据目录里跑**，
它把真实 `settings.json` 写成 27 字节的空壳（`{"Global":{"Theme":"dark"}}`），
把真实 `settings.json.bak` 写成默认设置，再撞上正在运行的托盘进程抢同一个文件，
原子替换失败，恢复没写回去 —— 用户配置就停在 81 字节的空壳上。
同时它还在用户真实的 `InputPlus Back` 里写测试备份并触发剪枝，把历史存档真删掉。

## 症状与现场

| 项目 | 现场值 |
| --- | --- |
| 数据目录 | `<安装目录>\InputPlus Data` |
| 损坏副本 | 44 个 `settings.json.broken-*`，**全部 81 字节**（个别 61 字节） |
| 部署时间点 | 18:40 / 19:25 / 19:30 / 19:56 / 20:16 / 20:19 / 20:23 / 20:28 / 20:54 / 21:11 / 22:15 / 22:25 |
| 损坏时间点 | 19:56 / 20:16 / 20:19（×2）/ 20:23 / 20:28 / 20:52（×2）/ 21:11 / 21:12 / 22:19 |

81 字节 = `"CLP2:" + base64(nonce12 + 密文27 + tag16)`，解密后正好是
`{"Global":{"Theme":"dark"}}` —— 也就是 `SelfTest.Settings.cs` 里**写死的那句测试空壳**。
时间点与 deploy 一一对应，这不是巧合。

用户目录里还留着一堆手工抢救的痕迹：`settings.json.before-good-20261117-2210`、
`settings.json.bak.before-restore-20261117-2103`、`settings-before-upgrade-20261116-1852.json`……
说明每次发版后都在手动恢复。

## 根因（三条，互相放大）

### 1. `--selftest` 直接在真实数据目录里跑破坏性用例

`src/InputPlus/SelfTest.Settings.cs`：

- 796 行起：先把真实 `settings.json` 读进内存，然后
  `File.WriteAllText(settingsFile, "{\"Global\":{\"Theme\":\"dark\"}}")` 往**真实主文件**写空壳；
- 820 行起：先 `DefaultSettings` 序列化覆盖**真实 `settings.json.bak``**，再写空壳到主文件，
  然后调 `SettingsStore.Load()` 走一遍"损坏恢复"，最后才在 `finally` 里写回原文。

只要 `finally` 没跑到，或者中途被别的进程抢了文件，用户配置就停在这个空壳上。
`App.xaml.cs:270-284` 里 `--selftest` 在**单实例互斥量（512 行）之前**就 `Environment.Exit`，
所以它能和正在运行的托盘实例同时抢 `settings.json`。

日志里的决定性一行（`logs/app.log`，用 `keys/local.key` 解出来）：

```
[ERROR] 保存设置失败
System.UnauthorizedAccessException: Access to the path is denied.
   at System.IO.FileSystem.MoveFile(...)
   at InputPlus.Core.Settings.SettingsStore.WriteJsonLocked(String json)
        in ...\SettingsStore.cs:line 699
[WARN] 从备份恢复后修复主设置文件失败；本次仍使用内存中的恢复配置
```

即：自检刚把主文件写成空壳，托盘那边正保存，`File.Move(tmp, primary, true)` 被拒；
自检的写回也失败 —— 主文件就留在空壳状态。下次启动时 `SettingsStore.Load()`
（223/272-293 行）判空 → 存 `settings.json.broken-<时间戳>` → 从 .bak 恢复，
而 .bak 早已被自检写成"默认设置"，于是**看起来就是"发版后数据变空"**。

### 2. 备份目录钉死在 exe 上，隔离数据目录不等于隔离备份

`src/InputPlus/Core/Sync/SyncService.cs:168-191`：

```csharp
public static string BackupDir => _backupDirCache ??= ResolveBackupDir();
// ResolveBackupDir() 第一行：
string primary = Path.Combine(AppPaths.AppDir, BackupFolderName);   // exe 同级，和 DataDir 无关
```

所以就算 `INPUTPLUS_DATA` 指到临时目录，自检/测试脚本写出的"测试备份"
仍然落在**用户真实的 `InputPlus Back`**，随后按份数/天数剪枝
（`SyncService.cs:538`）把用户的历史存档真删掉。日志：
`清理旧备份：InputPlus(2026-11-07 20-17-34).keyboardenhance`。

### 3. 发版脚本强杀进程

`dist/install-591.ps1`（每版一份）：`Stop-Process -Force` → 等 800ms → 覆盖 exe → 重启。
强杀会掐断 400ms 防抖保存和自检 `finally` 的写回窗口；目录里 40 个
`InputPlus.exe.bak-YYYYMMDD-HHMM` 说明这套动作做了 40 次。

## 已做的修复

| 位置 | 改动 |
| --- | --- |
| `Core/ActionQueue.cs` | 新增 `DataDirIsRedirected`（自检目录 || `INPUTPLUS_DATA`）；自检临时目录超过 24 小时自动清理 |
| `Core/Sync/SyncService.cs` | `ResolveBackupDir()` 在 `DataDirIsRedirected` 时改用 `<DataDir>\InputPlus Back`，且不做 legacy 迁移（那是 `File.Move`，会把真实备份搬进临时目录） |
| `Core/Settings/SettingsStore.cs` | `.bak` 轮换改成"先写 `.tmp` 再原子改名"，强杀时不会把唯一好备份写成半截 |
| `SelfTest.Settings.cs` | 破坏性用例前加硬闸门：数据目录没隔离直接判失败并跳过，绝不在真实目录写空壳 |
| `dist/deploy.ps1`（新） | 优雅退出 → 数据快照 → 换 exe → 临时目录跑自检 → 真实数据指纹复核 → 失败自动回滚 |
| `dist/check-data-loss.ps1`（新） | 回归脚本：跑两轮自检，比对数据目录与备份目录的指纹，变了就还原并退出码 1 |

## 验证（可复跑）

沙箱：把真实 `settings.json/.bak/.good/keys/vault.*` 与 10 份真实备份复制到
`%TEMP%\ke-repro-*`，在沙箱里跑 `--selftest`，比对指纹。

**旧版（v5.9.6 之前的线上 exe）**：

```
FAIL：自检改动了真实用户数据
  [内置隔离]  => backups|9|2823579   <= backups|10|3134610
```

实际后果：自检往真实备份目录写了一份测试备份，并按份数把**最早的真实存档
`InputPlus(2026-11-17 20-53-04).keyboardenhance` 删掉了** —— 恢复脚本只能删掉
多出来的那份，删掉的那份回不来。

**修好的版本**：

```
PASS：自检没有碰真实数据目录
```

备份仍是 10 份，`settings.json` 尺寸与 SHA256 均未变化。

## 以后怎么防止再犯

1. **发版只走 `dist/deploy.ps1`**，不要再用 `install-*.ps1` 里的 `Stop-Process -Force`。
   它会在换 exe 前后各取一次数据指纹，不一致就自动还原并报错。
2. **每次发版前跑一次 `dist/check-data-loss.ps1 -Exe <新 exe>`**，必须 PASS 才能发。
   这条已经进了 BUILD.md 的发版清单。
3. **任何会构造/保存大量测试配置的代码，先问 `AppPaths.DataDirIsRedirected`**
   （备份目录、清理、迁移同理）。真实目录里只允许读，不允许写。
4. **不要手工删 `settings.json.good`**：主文件与 `.bak` 同时坏掉时，它是最后一层。
5. 用户侧习惯：把 `InputPlus Back` 里最近的 `.keyboardenhance` 另存一份到别的盘，
   备份目录本身会被剪枝策略清理。
