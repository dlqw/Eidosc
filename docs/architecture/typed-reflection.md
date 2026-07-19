# Typed reflection values

Eidosc's Meta domain is a compiler-managed, read-only value domain. Reflection results are ordinary canonical `ComptimeValue` instances with a stable `StaticType`; they never expose C# AST objects, CLR objects, or internal symbol IDs as public fields.

The current Meta schema is version 6. The core values are:

- `meta.Field`, `meta.Constructor`, `meta.Span`, and `meta.Layout` for source and target-dependent declaration facts;
- `meta.Function` handles with identity, declaration, signature type, typed `meta.Parameter` values, result type, effects, ownership slots, and a stage-gated body handle;
- `meta.Ownership` slots containing `role`, `ordinal`, `kind`, `type`, `deferred`, `copy`, `clone`, `drop`, `borrowed`, and `mutable` facts;
- `meta.Type` and `meta.Declaration` shapes, including source-order fields/constructors and generated-origin data.

Copy and Clone facts are derived from structured type identity, built-in trait evidence, and explicit impl/constraint evidence. A shared `Ref[T]` slot is copyable and cloneable as a reference handle; `MRef[T]` is neither. By-value slots use `dropOnce`, while borrow slots use `borrowed`. An open type parameter is marked `deferred` and only reports facts proven by its constraints.

Reflection availability is phase- and privacy-controlled. `meta.layout_of` always requires an explicit supported target triple and returns a typed `meta.Layout` only when the requested layout is complete. Function bodies are absent before Body stage. Body analysis produces an independent loan summary (provenance, lifetime, reborrow, escape, and verification facts); it does not rewrite or enrich the signature-derived ownership value.

All nested reflection values, including ownership slots, are serialized through the versioned module/live-state payloads. Restoring a payload rehydrates the same static Meta types and canonical hashes; a schema change invalidates older query and value caches.
