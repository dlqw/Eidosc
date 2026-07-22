# Typed syntax expansion slice

- Syntax-site expansion accepts compiler-managed `meta.Syntax[K] -> meta.Syntax[K]` protocols and enforces category preservation.
- The compiler supplies a typed syntax input without exposing `meta.Site` or stage values.
- Generated syntax continues through the existing hygienic materializer and normal type pipeline.
