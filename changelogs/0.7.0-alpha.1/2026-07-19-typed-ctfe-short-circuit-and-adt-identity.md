# Typed CTFE short-circuit and canonical ADT identity

- Pure comptime `&&` and `||` now preserve short-circuit evaluation and do not evaluate an unreachable operand.
- Comptime ADT values include stable constructor identity in canonical hashes and cache payloads, preventing same-name constructors from different namespaces from colliding.
