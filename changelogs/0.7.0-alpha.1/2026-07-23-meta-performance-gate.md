# Eidosc 0.7.0-alpha.1 meta performance gate

- Skip deferred meta stages that have no invocation for the requested stage.
- Avoid fixed-point graph serialization for deterministic compiler-owned derives while retaining it for user generators.
- Stream canonical graph hashing instead of materializing complete XML strings.
- Skip syntax-site discovery when no compilation source contains the `expand` marker.
- Build incremental types-entry payloads only when module state payloads are requested.
