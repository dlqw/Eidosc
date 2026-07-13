# JSON schema compatibility

Eidosup JSON command results use `schemaVersion: 1`. Additive optional fields may
appear within the same schema version. Removing a field, changing its JSON type
or meaning, changing enum spelling, or restructuring a result requires a new
schema version and a documented migration. JSON progress is never mixed into
standard output.

The published schemas are:

- [`error-v1.schema.json`](schemas/error-v1.schema.json)
- [`doctor-v1.schema.json`](schemas/doctor-v1.schema.json)
- [`resolved-toolchain-v1.schema.json`](schemas/resolved-toolchain-v1.schema.json)
- [`signed-release-index-v1.schema.json`](schemas/signed-release-index-v1.schema.json)
- [`component-list-v1.schema.json`](schemas/component-list-v1.schema.json)
- [`target-list-v1.schema.json`](schemas/target-list-v1.schema.json)
- [`composition-change-v1.schema.json`](schemas/composition-change-v1.schema.json)
- [`doc-v1.schema.json`](schemas/doc-v1.schema.json)

Other command-specific list and mutation results additionally expose
`schemaVersion: 1`; their stable field contract is documented with the
command. Consumers must ignore unknown fields and must reject an unsupported
`schemaVersion`.
