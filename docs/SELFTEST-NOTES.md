# 自检（`--selftest`）的两个已知坑

> 2026-11-23 修 v1.5.9.2 那批缺陷时踩到的，写下来免得下次再查一遍。

## 1. 文件索引那组用例在「整跑」时会串状态

现象（全量 `--selftest`）：

```
[FAIL] 索引：校准确实走了一遍夹具树   → 第一次 19 条 / 第二次 0 条
[FAIL] 索引：校准后统计可读           → commit 失败 … PermissionDenied … .idx / .fieldnorm
```

但 `--selftest --only=QbarIndex` 单跑 **116 项全绿**。

结论：不是索引实现坏了，是**用例之间的状态耦合** —— 前面的用例动过同一个索引
实例（同一进程内只有一个索引目录与 writer），轮到 `TestQbarIndex` 时上一段
writer/reader 还没放干净，commit 就撞 `PermissionDenied`。

**排查顺序**：先 `--only=QbarIndex` 单跑；绿 = 别在这上面浪费时间，
要修就修用例之间的句柄释放，不要动索引逻辑。

## 2. 程序正在运行时，索引用例会「看起来坏了」

`QbarIndex` 用的是用户数据目录下的真实索引。主程序在跑（它持有那段索引）时，
自检里的 `UpsertForTest` 会写不进去，报的也是 `PermissionDenied`。

**正确姿势**：先关掉 Input Plus 再跑全量自检；或者只跑与索引无关的子集
（`--only=SnippetStream` / `--only=HoldDrag` / …）。

## 3. 子集跑法

`--selftest --only=<方法名里的片段>` —— 过滤的是**用例方法名**，不是断言名。
例：`--only=SnippetStream`、`--only=QbarIndex`、`--only=HoldDrag`、`--only=GestureWheel`。
