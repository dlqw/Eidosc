# Atomic ownership migration guard

- The 0.6-to-0.7 migrator no longer rewrites legacy `@borrow` capability attributes into public compiler clauses.
- Ambiguous ownership migrations stop with instructions to update the definition signature and all typed call sites using `Ref`/`MRef`; the migrator never inserts `clone`.
- Project migration validates every file before writing, so one ownership blocker prevents all definition and call-site edits from being partially applied.
