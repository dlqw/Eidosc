# Proof-driven sequence fusion

Eidosc optimizes ordinary eager `Seq` code without requiring a lazy sequence
type, a builder API, capacity tuning, or optimizer annotations. This page
explains how the first sequence-fusion path preserves source semantics and how
to inspect its decisions.

## Semantic identity

Canonical `Std.Seq` operations carry compiler-owned semantic roles such as
`sequence.map`, `sequence.filter`, and `sequence.fold_left`. A role identifies
the operation across generic specialization and module snapshots; it does not
authorize an optimization by itself. User source and generated source cannot
forge these roles.

Roles are part of MIR function identity, fingerprints, serialized payloads,
and convergence checks. Losing a role during specialization is therefore a
compiler defect rather than a reason to match a function by its name.

## Proof boundary

`SequencePipelineFusionPass` runs after generic specialization, when concrete
sequence operations and direct callback identities are available. It consumes
the shared `FunctionOptimizationProofIndex` used by the other MIR optimizers.

Cross-stage fusion is allowed only when every reordered callback is proven to
be trusted and pure, with no observable memory access, allocation, panic,
divergence, suspension, blocking, synchronization, or nondeterminism. The pass
also proves that intermediate sequences have a single reader and that the
source, mapped element, and accumulator types satisfy the current ownership
requirements.

The implemented shapes are direct `map -> filter -> fold_left` and
`map -> filter -> collect` pipelines over `Copy` source and mapped elements.
The fold path additionally requires a `Copy` accumulator. The filter callback
must have the canonical `Ref[T] -> Bool` contract.

The fold path becomes one ordered source loop with a scalar accumulator and no
eager `map` or `filter` result. The collect path becomes one ordered source
loop and one compiler-managed result collector, eliminating the eager `map`
result. If a following fold cannot be fused but the producer proof succeeds,
Eidosc can still apply the collect plan and leave the fold call unchanged.

## Capacity and construction planning

Compiler-generated sequence construction uses one
`RuntimeSequenceBuildLowering` path for `array_new`, `array_length`, and
`array_push`. A known source size becomes the initial result upper bound; a
guard may reduce the final length but does not invalidate that bound. Unknown
sizes use the runtime growth policy with an initial capacity of 8. Products of
multiple known generator lengths saturate at `Int32.MaxValue` instead of
overflowing.

Ordinary `Seq` code does not expose this capacity plan. Programmers do not
write a capacity constant, choose a different container, or import
`SeqBuilder` for the compiler to use it.

## Local unique storage

`RuntimeSequenceStoragePromotionPass` runs after ownership finalization and
caller-owned aggregate specialization. It promotes a RuntimeArray only when:

- its capacity and positive element size are compile-time constants;
- its inline storage, including the 64-byte runtime header allowance, is at
  most 4096 bytes;
- the allocation has one construction site in the function;
- the value and all ownership-transfer aliases are local and non-escaping;
- no candidate or ownership-transfer alias local is directly overwritten or
  redefined outside the proved construction/update path;
- no `Copy`, shared assignment, borrow alias, unknown/FFI call, return, or
  non-local store can retain the storage.

Accepted arrays receive fixed byte storage in the LLVM function entry block
and are initialized with `eidos_array_new_in_storage`. Indexed reads/writes,
push, and length operations keep their normal source semantics. If a push
exceeds the inline capacity, the runtime materializes heap storage and updates
the array value. Optimized length reads therefore use the live array pointer,
not the original stack header, so growth cannot expose stale length data.

Any failed proof retains `eidos_array_new_with_policy` and ordinary heap
ownership. The pass never asks the programmer to change source code.

## SeqBuilder boundary

`SeqBuilder` remains the explicit API for genuinely staged mutable
construction, in-place slot updates, and a deliberate freeze boundary. It is
not the performance-required spelling of ordinary `Seq` transformations.
Compiler-generated collectors use RuntimeArray primitives directly and do not
insert user-visible `SeqBuilder` calls.

## Conservative fallback

The original eager calls remain unchanged when any proof is missing. Common
fallback reasons include:

- an effectful, panic-capable, divergent, recursive, or unknown callback;
- a closure whose direct target is not available at the fusion site;
- an intermediate sequence with multiple readers or an unsupported CFG shape;
- element or accumulator ownership that the current lowering cannot preserve.
- a collector with dynamic or oversized inline storage;
- a local array with a shared alias, borrow escape, repeated construction site,
  or unknown retaining call.

Fallback is normal compiler behavior and does not produce a warning. Eidosc
does not recommend source rewrites, builders, capacity constants, or optimizer
hints to bypass a failed proof.

## Profiling and diagnostics

Detailed profiling records `sequence.analyze`, `sequence.plan`, and
`sequence.rewrite` optimizer subphases. MIR optimization counters use the
`Mir.optimizer.sequence.*` prefix and report formed pipelines, eliminated
intermediates, categorized fallback reasons, and
`sequence.collectors_stack_promoted`. Diagnostic MIR output includes the same
aggregated counters.

These metrics explain optimizer coverage; they are not a stable source-level
API and cannot change program semantics.
