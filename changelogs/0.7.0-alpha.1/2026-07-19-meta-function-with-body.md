# Typed `meta.Function.with_body`

- `meta.Function -> meta.Function` body generators can return `function.with_body(quote expr { ... })`.
- The compiler replaces only the authorized function body while preserving name, generic parameters, signature, effects, and ownership contract.
- Replacement expressions are materialized and re-enter the ordinary semantic pipeline; malformed or non-expression output is rejected before the expansion is committed.
- The versioned Meta schema is advanced to invalidate stale cache values after the new protocol member is registered.
