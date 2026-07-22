# Strong metaprogramming stage and atomicity increment

- Added centralized `Syntax`/`Semantic`/`Body`/`Layout` transformation permission validation, including sealed-case and field shape freeze rules.
- Prepared generated identities, declaration collisions, clause stages, target mutations, budgets, and member legality before committing any transformation output.
- Buffered generator diagnostics until the complete transformation validates, so failed edits leave no partial declarations, members, identities, or reports.
- Restricted Body insertions to private helper functions and Layout insertions to late comptime constants and tests without reopening name, coherence, MIR, or layout decisions.
