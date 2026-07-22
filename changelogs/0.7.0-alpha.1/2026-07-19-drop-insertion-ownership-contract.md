# Drop insertion ownership contract preservation

- MIR drop insertion now preserves each function's signature-derived `OwnershipContract` when rebuilding control-flow blocks.
- Later borrow, cache, reflection, and tooling stages continue to observe the same ownership authority after drop insertion.
