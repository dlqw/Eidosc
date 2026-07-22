# Typed Meta kind and generic domains

- Replaced compiler-internal `MetaTypeRef` kind and generic-argument domain strings with exhaustive enum values.
- Preserved the existing schema, canonical identity, cache payload, and reflection tokens at serialization and IDE-facing boundaries.
- Rejects unknown cached kind or domain tokens instead of allowing them into compiler control flow.
