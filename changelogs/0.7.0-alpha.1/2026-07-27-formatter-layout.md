# Formatter layout repair

- Classified this change as a compatible formatter fix for the active, unreleased `0.7.0-alpha.1` Eidosc line; the latest immutable Eidosc release remains `0.5.0-alpha.1`.
- Restored blank lines between top-level definitions and removed whitespace-only gaps between conditional heads and `then` arms.
- Aligned adjacent `::`, `=>`, `:=`, and `:` binding operators when the aligned group fits the configured line length.
- Kept compact structural constructions, structural patterns, record updates, and single-expression `then`/`else` arms on one line.
