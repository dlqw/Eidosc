# Typed body expansion slice

- `meta.Function -> meta.Function` is accepted as a compiler-managed body protocol.
- The compiler supplies a typed function handle, preserves the public signature contract, and reuses the ordinary body validation pipeline.
- Identity body transforms are materialized atomically without exposing target or transformation values.
