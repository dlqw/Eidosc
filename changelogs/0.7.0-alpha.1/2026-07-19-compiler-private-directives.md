# Compiler-private directives

- Replace flat `internal`, `intrinsic`, and `llvm_abi` clauses with structured `compiler(...)` directives.
- Keep compiler directives restricted to exact toolchain-owned source grants and carry their structured fields through declaration attachments, visibility, intrinsic registration, LLVM ABI metadata, and precompiled Std auditing.
- Reject the removed flat spellings and migrate legacy attributes directly to structured compiler fields.
