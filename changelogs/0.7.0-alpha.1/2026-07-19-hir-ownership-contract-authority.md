# HIR ownership contract authority

- HIR lowering now derives the versioned `OwnershipContract` directly from the typed callable signature before MIR and borrow analysis.
- MIR lowering propagates that contract unchanged; loan inference consumes the attached authority and only synthesizes one for standalone synthetic MIR fixtures.
- Contract identity remains stable across function-body and parameter-binding renames while preserving structured `Ref` and `MRef` projections.
