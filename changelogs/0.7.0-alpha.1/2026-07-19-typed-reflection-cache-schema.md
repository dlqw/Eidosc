# Typed reflection cache schema

- Meta schema 6 现在同步提升 `ComptimeValuesPayload` 到 v7、`MetaQueryStatePayload` 到 v3，旧的 reflection value/query cache 会确定性失效，不会恢复缺少 typed ownership/function facts 的值。
