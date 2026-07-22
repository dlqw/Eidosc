# Reject legacy generator protocol internally

- Remove the `Target -> Transformation` fallback from declaration and package generator resolution.
- Package analyzers and extensions must now match the compiler-managed `meta.Package` protocol categories; old query/transformation signatures are not recognized as an alternate execution path.
