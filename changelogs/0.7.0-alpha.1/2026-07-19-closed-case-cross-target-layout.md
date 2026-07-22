# Closed-case cross-target layout validation

- Routed explicit layout targets through the compiler target registry instead of inferring pointer width from target-name substrings.
- Rejected unsupported pseudo triples, canonicalized supported aliases, and covered closed-case layout queries across x86-64 and ARM64 targets.
