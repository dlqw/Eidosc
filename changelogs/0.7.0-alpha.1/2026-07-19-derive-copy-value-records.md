# Copy derivation for value records

- Added explicit `derive Copy` to the GameMath value records whose fields are all intrinsic Copy types.
- Keeps repeated by-value vector and rectangle operations affine-safe without Clone fallback.
