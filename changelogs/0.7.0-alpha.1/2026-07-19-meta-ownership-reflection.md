# Read-only ownership reflection

- Function type shapes now expose structured read-only ownership slots through the existing `borrowConstraints` reflection field.
- Each parameter/result slot reports `byValue`, `sharedBorrow`, or `mutableBorrow`, its ordinal, reflected type, and whether projection is deferred for a type parameter.
- Reflection observes signature semantics only; it cannot authorize, patch, or override ownership behavior.
