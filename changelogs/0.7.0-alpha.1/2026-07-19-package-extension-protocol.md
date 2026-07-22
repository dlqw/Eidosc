# Compiler-managed package extension protocol

- Package extensions can use `meta.Package -> meta.Items` and receive a compiler-owned package handle.
- Empty item output is accepted as an emit-only no-op; no query, scope, capability, or transformation value is exposed to the generator.
- Legacy query-based extension fixtures remain migration-only.
