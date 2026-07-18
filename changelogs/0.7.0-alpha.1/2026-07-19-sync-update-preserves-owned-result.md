# Preserve owned results from runtime-handle updates

- `Mutex.try_update` and `RwLock.try_update` now unbox the updated payload for the returned `Option[A]` after installing the boxed value.
- This keeps non-Copy payloads affine-safe: the updated value is no longer moved both into the runtime box and into the return value.
