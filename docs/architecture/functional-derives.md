# Functional ADT derives

Eidos supports structural `@[derive(Functor)]`, `@[derive(Foldable)]`, and `@[derive(Traversable)]` for algebraic data types. The final ordinary (`kind1`) type parameter is the mapped slot; preceding parameters remain fixed in the generated higher-kinded instance.

Derivation is proof-directed:

- A field equal to the mapped parameter is handled directly by the callback or reducer.
- A covariant occurrence under the final type argument recursively uses the corresponding trait operation (`fmap`, `fold_left`, or `traverse`). This includes recursive ADTs and nested standard-library containers.
- Occurrences in a non-final argument, a function/contravariant position, tuple, or effectful shape are rejected with a constructor-and-field diagnostic. No unsound implementation is synthesized.

`Traversable` uses `Applicative.pure` for constants and combines effects left to right. A one-field constructor uses `map`; a two-field constructor stages a saturated unary tuple helper, and constructors with three or more fields continue with `map` plus `apply` accumulation. This keeps every intermediate callback unary at the source level while preserving constructor order. Generated callback arrows retain the declared effect row `E`; the method also exposes `E` as its required effect row and `G: kind2 : Applicative[G]` evidence.

Erased function-value boundaries retain the specialized source signature in MIR. LLVM therefore reloads aggregate arguments before an indirect closure call instead of treating an aggregate as a single pointer; the fallback is the existing erased callable path when no source signature proof is available.

The generated implementation does not require callers to choose a container, builder, capacity, or optimizer hint. Representation and specialization remain compiler responsibilities.
