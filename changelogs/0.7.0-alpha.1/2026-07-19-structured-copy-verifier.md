# Structured Copy verifier

- Removed Clone fallback from Copy evidence and generic Copy constraints.
- Added structured Copy checks for `Ref[T]`, `MRef[T]`, tuples, and constructor payload layouts.
- Kept by-value ownership contracts as `Own`; MIR call lowering now chooses `MirCopy` only for Copy evidence and `MirMove` otherwise.
- Removed the legacy `ParamBorrowMode.Copy` surface and direct `Ref`/`MRef` string checks from MIR reference handling.
