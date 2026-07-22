# Borrowed reference native drop

- Stop classifying `Ref[T]` and `MRef[T]` values as managed RC owners during LLVM drop lowering.
- Cover borrowed recursive `Shared[T]` payloads with a native regression fixture so observing a payload cannot release its backing allocation.
