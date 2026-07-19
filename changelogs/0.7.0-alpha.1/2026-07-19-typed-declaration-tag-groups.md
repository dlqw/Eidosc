# Typed declaration tag groups

- Parse `@[repr(...), derive(...), expand(...)]` as a grouped declaration-tag surface on the Eidos 0.7 language line.
- Lower typed tags into the shared declaration attachment clauses while keeping removed standalone `@attribute` syntax rejected.
- Reject signature, foreign-contract, dedicated-declaration, and compiler-private adapters when they are placed inside a typed tag group.
