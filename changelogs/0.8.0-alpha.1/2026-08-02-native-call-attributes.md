# Native call attributes: nounwind + noundef

- Classified as a compiler performance feature targeting the unreleased `0.8.0-alpha.1` Eidosc line.
- Every Eidos function is compiled against `eidos_panic` (abort), so no internal function unwinds: a `nounwind` attribute group is now attached to all internal function definitions and declarations, letting LLVM drop Win64 unwind metadata and assume calls cannot throw.
- Scalar parameters (integer/float) of non-runtime-word functions carry `noundef`: Eidos always passes a defined value, so LLVM may assume arguments are never undef/poison.
- Supporting infra: `LlvmParameterAttribute.Noundef` + parameter attribute emission; multi-attribute-group emission (`#0 #1`) on function signatures.
- fastcc was evaluated for internal definitions/call sites (Win64 shadow-space saving) but A/B showed no measurable benefit (iter 15.4 vs 14.4 ms without, within noise), so it was reverted per the evaluation plan; the musttail-degradation guard for C-convention callees added during the experiment is kept as defensive hardening.

## Measured effect (fib-bench, Eidos vs Rust vs Zig, same machine)

| kind | after P1 (no attributes) | after P2 (nounwind+noundef) | Rust | Zig |
| --- | --- | --- | --- | --- |
| iter | 15.30 ms | 15.29 ms | 12.73 ms | 12.77 ms |
| tail | 14.12 ms | 13.23 ms | 11.77 ms | 11.90 ms |
| naive | 12.02 ms | 10.62 ms | 10.30 ms | 8.85 ms |

Checksums unchanged (`998459` / `998459` / `640752`).
