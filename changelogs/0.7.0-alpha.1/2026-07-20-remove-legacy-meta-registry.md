# Remove legacy Meta registry entries

- Delete `Stage`, `Target`, `Transformation`, `GenerationSlot`, `Scope`, `ScopeKind`, and `Query` from the Meta type registry.
- Delete target-edit, manual-slot, manual-scope, package-query, and transformation helper registrations and their type signatures.
- Keep only compiler-managed typed reflection, structured item constructors, diagnostics, syntax, body-function, package, and build protocol values.
