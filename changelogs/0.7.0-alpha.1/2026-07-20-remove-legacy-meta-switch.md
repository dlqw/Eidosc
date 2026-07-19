# Remove the legacy Meta schema switch

- Remove the internal `AllowLegacyMetaSurface` option from compilation, symbol-table restore, and Namer-state merging.
- Current compiler state always registers the compiler-managed Meta schema; historical `Target`/`Transformation`/`Query` values are not re-enabled through an internal test or migration flag.
