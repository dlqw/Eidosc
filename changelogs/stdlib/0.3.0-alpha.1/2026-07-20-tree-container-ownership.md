# Eidos Std 0.3.0-alpha.1 — ordered tree ownership

- Make `TreeMap.len`/`height` and `TreeSet.len` shared-reference observers.
- Clone trees recursively from shared references and remove entries through ownership-safe reconstruction.
- Preserve balancing metadata and ordered traversal without borrowing consumed tree values.
