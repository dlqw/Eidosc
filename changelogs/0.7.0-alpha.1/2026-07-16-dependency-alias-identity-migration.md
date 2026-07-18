# Dependency alias identity migration

- Added `DependencyAliasRenamePlanner` and `eidosc migrate names`.
- Dependency aliases are renamed from manifest identity through parsed AST paths, typed clause paths, and local path-dependency manifests; comments, strings, and unrelated local bindings are left unchanged.
- Plans validate alias collisions, source/manifest hashes, and reparse rewritten documents before applying the complete workspace edit set.
- LSP dependency-alias quick fixes now return a cross-file workspace edit, include saved and newly created unsaved source documents, and refuse unsafe manifest-only fixes when the source graph cannot be planned.
