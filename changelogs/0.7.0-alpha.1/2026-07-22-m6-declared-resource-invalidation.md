# M6 declared resource invalidation

- Expose declared extension resources through the typed `meta.Package` protocol without restoring the removed public Query surface.
- Require `read-declared-resources` before resource access and include resource hashes in generated identity invalidation.
- Cover content-driven output changes and denied undeclared access.
