# Compiler-managed meta protocol registry

- Classify compiler generators from resolved typed signatures instead of function names or stage tags.
- Recognize pure comptime, syntax, derive, body, analyzer, extension, and build-host protocol categories.
- Route `meta.Type -> meta.Items` derive outputs through typed item materialization.
- Preserve the legacy transformation path internally until the atomic public-surface migration removes it.
