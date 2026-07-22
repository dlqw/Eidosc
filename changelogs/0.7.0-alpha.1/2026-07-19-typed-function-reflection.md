# Typed function reflection handles

- `meta.Function` handle 现在携带完整的函数类型、参数元值、结果类型、effects、ownership slots 与稳定声明/span identity。
- 函数体句柄受 Body stage 门控；Semantic stage 的 handle 明确返回 Unit，避免泄漏尚未授权的 body facts。
- 生成器入口传递真实 `FuncDef` 与 Meta context，使 body transform 使用同一 typed handle contract。
