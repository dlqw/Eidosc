# Real typed syntax-site input

- Compiler-managed syntax protocols now receive the captured source syntax for the expansion site instead of an empty placeholder value.
- The input canonical value includes real tokens, origin, identities, and hygiene metadata for deterministic cache keys.
- Removed the temporary placeholder-syntax constructor.
