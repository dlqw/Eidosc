# Make text and sequence reads ownership-safe

- Borrow text and sequence values for internal length, indexing, comparison, search, and traversal helpers that read the same owned value more than once.
- Keep public by-value APIs explicit by cloning values only where a caller intentionally retains ownership, including text joining and fixture-visible sequence reuse.
- Add reference-based internal intrinsics for string equality and sequence length so standard-library implementations no longer depend on body-derived Copy behavior.
