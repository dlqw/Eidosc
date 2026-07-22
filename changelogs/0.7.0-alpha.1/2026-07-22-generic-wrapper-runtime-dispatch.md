---
title: Preserve cross-module generic wrapper dispatch
component: Eidosc
version: 0.7.0-alpha.1
type: fix
---

Generic specialization now identifies recursive calls by the structured template identity instead of the source function name alone. Wrappers such as `Std.Async.spawn` therefore dispatch to `Std.Task.spawn` instead of lowering to a self-loop, restoring native task and async runtime execution.
