# Clone reference receiver

- Changed the built-in `Clone` trait receiver to `Ref[Self] -> Self`.
- Updated derive-generated Clone implementations to dereference the receiver and explicitly borrow fields for nested cloning.
- Migrated Std Clone implementations, TraitInvoke, text/container clone calls, and intrinsic shared clone signatures to explicit reference receivers.
- Clone implementation target inference now unwraps `Ref[T]` to register the implementation for `T`.
