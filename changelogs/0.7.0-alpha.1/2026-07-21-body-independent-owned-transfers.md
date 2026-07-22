# Keep owned transfers independent of function bodies

- Stop rewriting MIR moves into copies from callee-body parameter-effect heuristics in full, restored-module, and query-driven compilation paths.
- Preserve the signature-derived ownership contract across clean builds and mixed MIR restoration, with structural Copy evidence as the only authority for by-value copying.
- Add a mixed-restore regression that requires a non-Copy owned argument to remain a move while retaining strict MIR, LLVM, and fingerprint equivalence checks.
