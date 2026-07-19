# Structured foreign contracts

- Replace flat `extern c`, `link_library`, and `link_name` clauses with one `extern(c, library: ..., name: ...)` contract.
- Carry the validated ABI, optional library, and optional symbol name through `DeclarationAttachmentIR`, semantic binding, FFI validation, Bindgen output, and the precompiled standard library.
- Reject unknown, duplicate, malformed, and legacy flat foreign-contract fields without a compatibility parser path.
