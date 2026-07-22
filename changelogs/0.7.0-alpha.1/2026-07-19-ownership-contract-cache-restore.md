# Ownership contract cache restore

- Module MIR cache payload schema v11 serializes the complete versioned `OwnershipContract`, including structured parameter/result projections and stable type identities.
- Restored functions retain the same callable identity and ownership authority after JSON and incremental-cache round trips.
- The module MIR artifact schema advances to v11 so stale v10 payloads are rejected instead of silently restoring incomplete ownership facts.
