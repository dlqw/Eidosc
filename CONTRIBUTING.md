# Contributing to Eidosc

## Development workflow

1. Create a short-lived branch from the latest `main`.
2. Keep changes focused and include tests for observable behavior.
3. Use a Conventional Commits subject.
4. Open a pull request targeting `main` and wait for required checks.
5. Use squash merge after review.

Suggested branch prefixes are `feat/`, `fix/`, `chore/`, `docs/`, `perf/`,
`refactor/`, and `test/`.

## Local verification

At minimum, run:

```powershell
dotnet build Eidosc.sln --nologo
dotnet test src/Eidosc.Tests/Eidosc.Tests.csproj --nologo
dotnet run --project src/Eidosc.Cli -- --help
```

Changes to native code generation or the runtime require a working Clang/LLVM
installation. Changes to syntax, semantics, diagnostics, manifests, or language
services should include the corresponding compiler, CLI/LSP, and editor-facing
tests.

## Code style

- Use four spaces in C# files.
- Keep nullable reference types enabled.
- Prefer file-scoped namespaces.
- Add comments for public API contracts and non-obvious invariants, not for the
  editing process.
- Do not commit generated binaries, local IDE settings, debug output, or secrets.

## Reporting problems

Use GitHub Issues for reproducible bugs and feature requests. Include the Eidosc
version, host platform, input or project shape, expected result, actual result,
and a minimal reproduction when possible.
