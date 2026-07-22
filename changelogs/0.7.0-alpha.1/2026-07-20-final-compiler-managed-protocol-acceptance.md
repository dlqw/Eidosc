# Compiler-managed protocol acceptance

- Migrated the CLI and semantic protocol fixtures to `meta.Type -> meta.Items`, `meta.Syntax[K] -> meta.Syntax[K]`, `meta.Function -> meta.Function`, package analyzer/extension protocols, and the Build host protocol.
- Removed the remaining public package-extension `meta.Transformation`/`meta.Query` path; package extensions now emit typed items or modules and are scheduled from their protocol signature.
- Kept interim `derive`, `expand`, `repr`, and `extern` forms rejected by the current parser while the migrator emits typed declaration tags.
