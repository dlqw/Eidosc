# Generic runtime-handle Copy evidence

- Runtime-backed handles now derive `Copy` from their raw-pointer representation without imposing Copy on payload type parameters.
- Closed constructor layouts are propagated to dynamic type IDs only when all fields are closed, allowing `Mutex[A]`, `RwLock[A]`, `Channel[A]`, `Promise[A]`, `Task[A]`, `Barrier`, and `TaskGroup` handles to be copied independently of `A`.
- `derive Copy` constraints are generated only for type parameters used by the derived value representation; phantom parameters remain unconstrained.
