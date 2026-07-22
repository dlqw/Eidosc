# Typed reflection core values

- 将 `meta.Field`、`meta.Constructor`、`meta.Span` 与 `meta.Layout` 的反射结果改为携带稳定静态类型的只读编译期值。
- Meta schema 升级到版本 5，使旧 schema 的查询与生成缓存自动失效。
