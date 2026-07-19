# IDE and LSP ownership contract surface

- IDE semantic snapshots now attach the versioned structured ownership contract to function symbols whenever HIR or MIR facts are available.
- Parameter/result slots expose stable type identity, passing kind, ordinal, and deferred projection without compiler tags or body-derived inference.
- LSP hover renders the contract's parameter-to-result ownership projection for compiler and user diagnostics.
