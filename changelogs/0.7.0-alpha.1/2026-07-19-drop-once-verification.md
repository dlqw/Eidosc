# Drop-once ownership verification

- Borrow dataflow now rejects dropping a local after it was moved or already dropped.
- The check follows control-flow joins, so a value consumed on any incoming path cannot be released again at the join.
- Duplicate ownership consumption reports the existing `E1001` double-move diagnostic instead of permitting a second release.
