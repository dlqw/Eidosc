# OwnershipContract MIR propagation

- Signature inference now attaches the versioned `OwnershipContract` to the lowered `MirFunc`.
- Borrow verification and later call/code-generation stages can consume the same structured authority without re-inferring ownership from function bodies.
