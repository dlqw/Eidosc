# M3 unified derive protocol registry

- Classify compiler-owned built-in derives and user `meta.Type -> meta.Items` derives through the same compiler protocol kind and stage registry.
- Keep the built-in derive allowlist centralized and reject unknown compiler-owned derive names before execution.
