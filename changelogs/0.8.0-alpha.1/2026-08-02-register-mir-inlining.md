# Register MIR inlining pass

- Classified as a compiler performance feature targeting the unreleased `0.8.0-alpha.1` Eidosc line.
- Registers the existing `Mir/Optimize/Inlining` pass (single-block, non-recursive, <= 30 instruction functions) at the end of MIR round 1, before tail-call formation and ownership finalization so inserted drops cover inlined bodies.
- Previously the pass was fully implemented but never registered, so small pure helpers were only inline-able by LLVM at the IR level.

## Correctness fixes required by registration

- `Inlining`: remap `MirTemp` ids in inlined callee bodies (temps are function-local and collided with caller temps), including temps referenced by the return terminator and nested in index places.
- `Inlining`: skip partial-application call sites (argument count != callee parameter count) so a partially applied curried call keeps the call instead of inlining a flat body that reads uninitialized parameters.
- `Inlining`: exclude ADT constructors, parameter-dereferencing callees, lambda closure bodies, `std::Task`/`std::TaskGroup`/`std::Ffi`/`std::Text`/`std::Console`/`Display` helpers, and multi-parameter curried functions (their bodies are first-class closure values at call sites; the closure-invoke protocol and refcount paths must stay intact).
- `StackPromotionAnalysis`: treat `MirAssign` as an alias edge; inlined bodies assign through plain assignments and the escape chain was broken, so escaping constructor results were wrongly promoted to the stack and later refcounted as heap objects.
- `MirToLlvmConverter`: materialize function references assigned to locals as first-class closure objects (with an invoke header) using the caller-visible curried signature; a bare function pointer was dereferenced as a closure header by the indirect-call path, and the specialized flat signature produced invoke thunks whose arity mismatched closure-protocol call sites.
- `LinearRecursionAccumulatorPass`: resolve plain `MirCopy` chains in the entry guard, the `n - 1`/`n - 2` subtractions, the call arguments, and the sum operands (later passes insert copies between the original binops and their uses); the pass had silently stopped matching. Also fix the synthesized loop condition to use the Bool type id (the Int-typed discriminant was lowered to a `zext i1 to i64` compared against an `i1` true constant, which `llc` rejects), and fix the slot-backed binop result name (nullable `LocalId.Value` stringified as `%1`, producing invalid IR names).

## Measured effect (fib-bench, Eidos vs Rust vs Zig, same machine)

| kind | before P1 (no inlining) | after P1 | Rust | Zig |
| --- | --- | --- | --- | --- |
| iter | 22.95 ms | 15.30 ms | 14.84 ms | 15.58 ms |
| tail | 16.42 ms | 14.12 ms | 15.38 ms | 17.58 ms |
| naive | 16.10 ms | 12.02 ms | 10.19 ms | 9.00 ms |

`naive` also recovers the linear-recursion accumulator transform (16.10 ms without the transform vs 12.02 ms with it); checksums unchanged (`998459` / `998459` / `640752`).
