# Retire legacy Meta surface by default

- Compiler-owned `meta.Target`, `meta.Stage`, `meta.Transformation`, `meta.Query`, `meta.ScopeKind`, and manual generation-slot types are no longer registered in normal compilations.
- Legacy transformation helpers and query target helpers are unavailable to ordinary source; protocol generators are identified from typed signatures.
- Existing migration fixtures can opt into an internal legacy schema solely while converting historical syntax; this switch is not a language or CLI compatibility mode.
- Live-state and symbol-table schema versions advance to invalidate snapshots built with the removed public surface.
