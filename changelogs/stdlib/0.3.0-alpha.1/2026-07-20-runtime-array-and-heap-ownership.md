# Eidos Std 0.3.0-alpha.1 — runtime array and heap ownership

- Require `MRef` for runtime-array mutation primitives.
- Make binary heap and priority queue peek/pop paths consume their values without implicit element cloning.
- Rebuild priority queues through owned sequences after insertion and removal.
