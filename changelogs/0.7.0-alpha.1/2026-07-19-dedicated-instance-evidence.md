# Dedicated instance evidence

- Breaking: named `Name :: instance Trait { ... }` declarations are now the only source-level form that registers trait implementation evidence.
- Function-level `impl Trait` clauses and implicit registration by matching a function name to a trait method are rejected and cannot affect coherence or dispatch.
- Compiler derives now materialize named instances; method-free traits such as `Copy` produce marker instances with an explicit target type.
- Overlap diagnostics and generated MIR names identify named instances instead of the removed `@impl` surface.
- `eidosc migrate syntax` rewrites Eidos 0.6 function-level `impl` clauses into named instance blocks. Ambiguous grouping remains an atomic migration failure rather than guessing evidence ownership.
