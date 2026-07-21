# Resolve clone dispatch through explicit reference receivers

- Lower builtin `Clone.clone` calls whose receiver remains `Ref[T]` in specialized MIR by loading the referenced builtin value instead of emitting an unresolved external call.
- Treat erased MIR `Ref`/`MRef` arguments as their typed inner value when collecting generic specialization bindings.
- Unwrap one typed reference layer when selecting a trait dispatch carrier, preserving the declared `Ref[Self]` ownership contract while resolving the implementation for `Self`.
- Propagate concrete element types through open tuple projections in specialized MIR and rewrite monomorphic self-recursion back to the active specialization.
- Bound local-type refinement convergence so conflicting generic carrier evidence cannot leave MIR optimization cycling indefinitely.
- Add LLVM regressions covering `TraitInvoke.clone_value(ref value)` through the generic `AsyncExtra` path and recursive `SeqBuilder.filled[Int]` materialization.
