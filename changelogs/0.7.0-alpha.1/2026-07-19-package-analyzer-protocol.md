# Compiler-managed package analyzer protocol

- Package checks can use `meta.Package -> Seq[meta.Diagnostic]` without constructing `meta.Query` or scope values.
- The compiler supplies the package handle and keeps analyzer access read-only through the existing query context.
- Historical query-based package fixtures remain isolated behind the migration-only schema switch.
