# Generated dedicated instances

- Materialize `meta.implementation(...)` as one named `InstanceDecl` containing its generated methods.
- Stop encoding generated trait evidence as top-level functions carrying hidden `impl` clauses.
- Preserve target generics, trait/module identity, generated origin propagation, and stable generation-slot identity on the dedicated instance tree.
