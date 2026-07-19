# Ownership contract and loan summaries

Eidosc treats ownership as type semantics, not as compiler-tag authorization. A typed callable signature is normalized into `ownership-contract-v1` before MIR and borrow analysis:

| Signature type | Passing kind | Caller behavior |
| --- | --- | --- |
| `T` | `ByValue` | Structured Copy verification chooses Copy; otherwise the place is moved. |
| `Ref[T]` | `SharedBorrow` | The reference handle is Copy; the pointee is not copied. |
| `MRef[T]` | `MutableBorrow` | The handle is non-Copy and reborrow rules preserve exclusivity. |

An open top-level type parameter is `ByValue` with a deferred projection. Instantiation resolves its concrete Copy and place behavior. Copy is not a passing kind, and Clone is never a fallback: Clone remains an explicit ordinary call with a `Ref[Self]` receiver.

## Compiler responsibilities

The compiler must:

1. derive the contract only from structured typed signatures and stable symbol/type identity;
2. preserve the same contract through HIR, MIR, module interfaces, cache restore, reflection, IDE snapshots, and LSP output;
3. select Copy, Move, or reborrow at each call from the contract, structured Copy verifier, and current place state;
4. track partial moves and explicit reinitialization at field/index places across CFG joins;
5. prove returned-reference provenance and lifetime, reject escaping locals, and release each owned value exactly once;
6. keep body-derived facts in a separate versioned loan summary;
7. expose ownership only as read-only tooling data; metadata and compiler tags cannot grant or override it.

The `loan-summary-v1` payload contains returned-borrow provenance, lifetime/outlives constraints, inference confidence, and validation facts. It is keyed by the MIR body fingerprint and can change when a body changes without changing the signature-derived ownership contract.

## User responsibilities

Users choose ownership through types and explicit operations:

- use `T` when a call consumes a value unless the concrete type is Copy;
- use `Ref[T]` for shared access and `MRef[T]` for exclusive mutable access;
- call Clone explicitly when a new owned value is required;
- reinitialize a moved field before reading the aggregate again;
- return references only when their provenance remains tied to an input;
- do not drop, move, or otherwise consume the same ownership twice.

The 0.7 migrator does not convert legacy `@borrow(read/write/move)` into a new authorization tag. Because those capabilities cannot determine `Ref` versus `MRef`, migration stops before writing and asks the user to update the definition and all typed call sites atomically. It never inserts Clone.

## Diagnostics and cache boundaries

- `E1001` covers use/double-consumption after move, including duplicate drop.
- HIR and MIR payload schemas reject older artifacts that omit the ownership contract.
- Borrow diagnostic snapshot v3 restores `loan-summary-v1`; clean Borrow and LLVM cache hits retain provenance and lifetime facts.
- Meta reflection, IDE snapshots, and LSP hover report structured slots but cannot mutate them.
