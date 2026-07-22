# DeclarationAttachmentIR

- Clause binding now materializes one versioned `DeclarationAttachmentIR` per declaration.
- The attachment carries source-ordered typed clauses and lowered meta invocations together, while the legacy public Meta surface remains untouched until the planned atomic removal.
