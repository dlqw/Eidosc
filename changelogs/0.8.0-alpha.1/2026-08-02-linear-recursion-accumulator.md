# Linear recursion accumulator transform

- Classified as a compiler performance feature targeting the unreleased `0.8.0-alpha.1` Eidosc line.
- New MIR pass `LinearRecursionAccumulatorPass` rewrites the exact shape `F(n) = F(n-1) + F(n-2)` (base `F(k) = k` for `k < 2`) into an accumulator loop with a single recursive call per level: `acc += F(n-1); n -= 2; return acc + n`, reducing recursive call count by roughly a third (mirrors the transform Rust tooling applies before LLVM; see `docs/research/compiler-call-layer-2026-08-02`).
- Registered at the end of the default MIR pipeline (after dead-block cleanup so the strict three-block shape holds). Strict v1 shape matching; any deviation leaves the function unchanged.
- The generated call is intentionally not marked as a tail call: LLVM treats calls that later touch slot-backed loop locals (accumulator/parameter) conservatively, which regressed performance in A/B measurements.
- Same-machine A/B (`projects/fib-bench` naive, medians of 7): 13.87 ms with the transform vs 16.22 ms without (-14.5%); checksums identical. Cross-round benchmark deltas stay within machine-load noise; the remaining gap to Rust/Zig is the call-convention layer (P2: C convention shadow space vs fastcc), not this pass.
