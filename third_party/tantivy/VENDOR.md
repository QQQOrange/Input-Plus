# tantivy（vendored）

这里是 **tantivy v0.27.0** 的副本，供 `src/ipidx` 以 `path` 依赖构建。

- 上游：https://github.com/quickwit-oss/tantivy
- 版本：`0.27.0`（见本目录 `Cargo.toml` 的 `version`）
- 许可证：MIT（见 `LICENSE`，原样保留）
- 变更记录：`CHANGELOG.md` 原样保留

## 与上游的差异（只有删减，没有改代码）

| 动作 | 路径 | 原因 |
|------|------|------|
| 删除 | `benches/gh.json`（2.3 MB） | 压测数据，构建不需要；仓库体积 |
| 删除 | `benches/hdfs.json`（22 MB） | 同上 |
| 删除 | `.github/` | 上游 CI 配置，vendored 副本不跑 |
| 删除 | `.claude/` | 上游本地工具目录 |

**没有修改任何 `.rs` 或 `Cargo.toml` 的语义内容。** 也就是说：这份代码编出来的行为和
crates.io 上的 `tantivy 0.27.0` 完全一致，只是不需要联网拉仓库。

`benches/*.rs` 保留着（`Cargo.toml` 里有 `[[bench]]` 声明，删掉会让清单指向不存在的
目标）；只删了两份大的 JSON 数据文件，`cargo build` / `cargo test` 都不碰它们。

## 升级怎么做

1. 把新版本上游解压到临时目录；
2. 覆盖本目录，然后重新做上表里的删减；
3. 改 `src/ipidx/Cargo.toml` 里的特性列表（如果有变化），跑 `cargo test`；
4. 更新本文件的版本号与差异表；
5. 更新 `docs/qbar/TANTIVY-MIGRATION.md` 的 schema/tokenizer 说明（API 可能变）。

## 为什么 vendored 而不是 crates.io

正式产物是随 `InputPlus.exe` 一起走的 `ipidx.dll`。构建环境可能没网，
而"能不能离线重建出同一个 dll"直接决定发布可复现。代价是本目录多几 MB 源码。
