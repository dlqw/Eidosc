# Signature-derived ownership contract

- Added a versioned, structured `OwnershipContract` for callable parameters and results with by-value, shared-borrow, mutable-borrow, and deferred generic projections.
- Stopped function-body reads, writes, moves, and nested calls from changing public parameter ownership modes.
- Kept returned `Ref` and `MRef` results classified as borrows when provenance validation fails, so invalid bodies report an escape without rewriting the signature contract.
- Replaced borrow-signature reference-name heuristics with structured type descriptors and stable semantic contract identities.
