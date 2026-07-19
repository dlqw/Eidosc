# Remove legacy Meta transformation intrinsics

- Delete target-edit, target-replacement/removal, manual generation-slot, transformation-combine, and query-emission comptime intrinsic implementations.
- Keep structured `meta.Items`, `meta.Function.with_body`, category-preserving syntax values, diagnostics, and semantic constructors as the supported generation mechanisms.
