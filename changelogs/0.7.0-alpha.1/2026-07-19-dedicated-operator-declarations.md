# Dedicated operator declarations

- Remove function-level `operator` clauses from the 0.7 declaration schema and semantic binder.
- Keep symbolic name-first declarations such as `(|+|) :: A -> A -> A { ... }` as the only user-defined operator declaration surface.
- Reject legacy operator metadata after a function signature instead of maintaining a second registration path.
