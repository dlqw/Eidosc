# Eidos Std 0.3.0-alpha.1 — sequence and combinator ownership

- Rewrite `Seq.take`, `Seq.drop`, `Seq.reverse`, and cloning as consuming or explicit-reference traversals.
- Rewrite `AsyncExtra.chunk`, `select_at`, and `take_while` so values are consumed once or cloned through shared references.
- Make `Functions.repeat`, predicate combinators, `juxt`, and `converge` obey the affine ownership contract.
- Add explicit clone support for `Option.filter` and `Result[T, E]`, and mark `Range` and `Monoid.LawUnit` as structurally copyable.
- Make `SeqBuilder` mutations return the consumed builder and use explicit shared references for snapshots.
- Pass explicit shared references to `PersistentMap` shared-owner cloning.
