# HIR ownership contract cache

- Module HIR payload schema v10 serializes and restores each function's structured `OwnershipContract`.
- Module HIR artifact schema v2 rejects older artifacts that lack ownership authority.
- Restored cross-module HIR declarations preserve ownership projections before MIR and borrow analysis resume.
