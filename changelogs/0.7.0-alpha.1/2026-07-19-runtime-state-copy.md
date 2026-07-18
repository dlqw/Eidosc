# Copyable runtime state snapshots

- Runtime status/result ADTs now derive `Copy` when their representations contain only intrinsic copyable fields.
- Promise, channel, barrier, task, and task-group fixtures can reuse status snapshots across predicates and selectors without implicit Clone or ownership violations.
