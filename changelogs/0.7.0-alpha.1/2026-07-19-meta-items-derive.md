# Typed `meta.Items` derive slice

- `meta.Type -> meta.Items` generators can now be attached through `expand` on a type and are classified without stage or target values.
- `meta.Items` uses the compiler-owned sequence representation, so ordinary list literals satisfy the typed output domain.
- Derive outputs flow through the same typed item materializer used by compiler-generated declarations.
