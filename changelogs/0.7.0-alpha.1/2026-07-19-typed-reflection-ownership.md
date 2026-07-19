# Typed reflection ownership

- 新增只读、可序列化的 `meta.Ownership` 元值，函数 shape 的每个参数和结果槽均暴露稳定的 passing kind、类型、deferred、Copy、Clone、drop、borrow 与 mutability facts。
- Copy/Clone facts 使用内建 trait、泛型约束与结构化 impl identity 判定；共享借用与可变借用保持不同的复制语义。
- 修正 compiler meta TypeId 判定，使后期 Meta 类型被保留，同时不把 Build 类型误归为 Meta；Meta schema 升级到版本 6。
