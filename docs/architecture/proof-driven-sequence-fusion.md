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

The initial implemented shape is a direct
`map -> filter -> fold_left` pipeline over `Copy` source elements, mapped
elements, and accumulators. Its filter callback must have the canonical
`Ref[T] -> Bool` contract. Accepted pipelines become one ordered source loop
with a scalar accumulator and no eager `map` or `filter` result.

## Conservative fallback

The original eager calls remain unchanged when any proof is missing. Common
fallback reasons include:

- an effectful, panic-capable, divergent, recursive, or unknown callback;
- a closure whose direct target is not available at the fusion site;
- an intermediate sequence with multiple readers or an unsupported CFG shape;
- element or accumulator ownership that the current lowering cannot preserve.

Fallback is normal compiler behavior and does not produce a warning. Eidosc
does not recommend source rewrites, builders, capacity constants, or optimizer
hints to bypass a failed proof.

## Profiling and diagnostics

Detailed profiling records `sequence.analyze`, `sequence.plan`, and
`sequence.rewrite` optimizer subphases. MIR optimization counters use the
`Mir.optimizer.sequence.*` prefix and report formed pipelines, eliminated
intermediates, and categorized fallback reasons. Diagnostic MIR output includes
the same aggregated counters.

These metrics explain optimizer coverage; they are not a stable source-level
API and cannot change program semantics.
