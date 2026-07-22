# Typed `meta.Type` generator input

- `meta.Type -> meta.Items` generators now receive a stable typed type value rather than the removed target wrapper.
- The input carries canonical nominal identity and supports ordinary read-only reflection such as `meta.name_of`.
- Type values include the public `meta.Type` static type in canonical serialization and cache fingerprints.
