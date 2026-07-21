# Migrate the record-update ownership fixture

- Destructure the owned record explicitly and reconstruct updated fields so retained non-Copy fields are transferred exactly once.
- Clone text fields explicitly when the native smoke fixture needs to read multiple owned fields through by-value APIs.
