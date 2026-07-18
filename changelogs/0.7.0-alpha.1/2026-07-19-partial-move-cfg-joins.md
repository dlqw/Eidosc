# Partial-move CFG joins

- Partial place state now participates in affine block dataflow and is conservatively joined across branches.
- Reinitialization clears the affected place before successor analysis, preventing branch-local move facts from leaking into unrelated paths.
