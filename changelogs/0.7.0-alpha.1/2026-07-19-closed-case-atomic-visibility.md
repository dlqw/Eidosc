# Closed-case atomic visibility

- Made the root declaration the single visibility authority for its complete closed-case hierarchy.
- Added E3061 when a public closed root contains a toolchain-internal descendant instead of silently publishing that case.
- Verified that internal roots keep their case, constructor, and field symbols private, while exported roots expose the complete hierarchy for exhaustive matching.
