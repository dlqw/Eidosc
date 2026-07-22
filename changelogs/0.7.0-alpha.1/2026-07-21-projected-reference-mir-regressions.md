# Cover projected reference call arguments

- Require field- and index-projected `Ref`/`MRef` call arguments to preserve their complete MIR place shape without creating an intermediate load.
- Verify the projected argument retains its dereferenced local base so LLVM lowering receives the address required by the reference contract.
