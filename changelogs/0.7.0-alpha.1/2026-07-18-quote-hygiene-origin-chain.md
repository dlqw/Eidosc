# Hygienic syntax and generated provenance increment

- Added stable definition-site identities for qualified syntax paths, nested bindings, lambda parameters, and logical or-pattern bindings.
- Completed structural quote traversal, deep cloning, trivia/origin/identity payload round-trips, and the dedicated Types-to-HIR quote boundary diagnostic.
- Attached ordered generated-origin chains to every committed generated AST node, propagated them through HIR declarations, expressions, and patterns, and preserved them in the versioned HIR state payload.
- Serialized complete generated provenance in AST XML while excluding provenance metadata from semantic function-contract comparison.
- Kept compile-time function bodies out of runtime HIR so evaluated quote syntax cannot leak into MIR lowering.
