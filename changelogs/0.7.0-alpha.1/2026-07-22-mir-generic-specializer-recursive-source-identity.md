---
title: Preserve source identity for recursive generic specializations
component: Eidosc
version: 0.7.0-alpha.1
type: fix
---

Generic MIR specialization now keeps the callee's structured symbol and function identity when a specialized wrapper calls another generic function with the same source name. Recursive and cross-module call chains no longer resolve to the caller's specialization by name alone.
