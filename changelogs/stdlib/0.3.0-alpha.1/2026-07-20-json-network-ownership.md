# Eidos Std 0.3.0-alpha.1 — JSON and network ownership

- Make `JsonParser` cursor helpers observe their source through `Ref[String]` so recursive parsing does not consume it repeatedly.
- Make network URL, hexadecimal, and header scanners clone only individual text observations while threading one owned source through recursion.
