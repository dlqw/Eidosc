# Migrate fixtures and generated declarations to compiler-managed protocol

- Replaced the legacy `Target`/`Transformation`/`Query` semantic fixture suite with focused tests for typed `meta.Type`, `meta.Syntax[K]`, `meta.Function`, package, extension, and build-host protocol signatures.
- Reject interim pre-body `derive`, `expand`, `repr`, and `extern` forms in current syntax; declaration generators now use `@[derive(...)]`, `@[expand(...)]`, `@[repr(...)]`, and `@[extern(...)]`.
- Migrated precompiled Std sources and Bindgen output to typed declaration tags while retaining `where`, `case`, `need`, and toolchain-owned `compiler(...)` as signature/compiler components.
- LSP completion and hover now describe typed declaration tags rather than exposing removed pre-body attachment clauses.
