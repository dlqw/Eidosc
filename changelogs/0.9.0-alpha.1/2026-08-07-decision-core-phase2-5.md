# Decision Core Phase 2-5

- 将 `HirIf`、`HirMatch`、selection、`if let` 和 `while let` 的来源计划传递到 MIR `MirSwitch`，并纳入 MIR fingerprint、收敛 hash 与模块缓存恢复。
- 将路径事实扩展为 CFG forward dataflow，使用保守的多前驱 join 和 Bool switch edge refinement，继续在无法证明时保留原控制流。
- 为纯 scalar diamond 增加 `MirSelect` 与 LLVM `select i1` lowering；effectful、owned 和复杂值继续保留 CFG。
- 增加跳转表/二分树候选计数与结构化 MIR debug 输出，供 profiling、debug artifact 和后续 LSP 展示使用。

本变更仅影响编译器内部优化与调试数据，不改变 Eidos 公开语法、运行时语义或 ABI。
