# Eidosc 0.7 naming diagnostics increment

- Added manifest package, dependency alias, module directory, and module file naming rules with stable S1105/S1107/S1108/S1110 diagnostics.
- Exposed manifest naming diagnostics through the LSP diagnostic channel and provided semantic code actions for package/dependency identity normalization.
- Kept external FFI `link_name` spelling independent from the canonical Eidos binding name.

This fragment records the implemented vertical slice; the remaining RFC 0.7 naming migration and cross-repository synchronization stay tracked in the implementation plan.
