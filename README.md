# Eidosc

Eidosc is the compiler and command-line toolchain for the Eidos programming
language. The project is in an early prerelease stage and its language and
tooling interfaces may still change between prereleases.

## Components

- `Eidosc`: compiler frontend, type system, MIR, borrow analysis, LLVM backend,
  project model, formatter, documentation generator, and language services.
- `Eidosc.Cli`: the `eidosc` command-line entry point and LSP server.
- `Eidosup`: installer and toolchain manager for Eidos development environments.
- `Eidosc.Bindgen`: C header binding generator.
- `Eidosc.Tests`: compiler, CLI, runtime, and tooling tests.
- `Eidosc.Benchmarks`: repeatable compiler and LSP benchmarks.

See [Compiler architecture](docs/architecture/compiler-overview.md) for a
source-level overview. See [Eidosup bootstrap guide](docs/eidosup/bootstrap.md)
for installer channels, release sources, diagnostics, and exit behavior.

## Requirements

- .NET SDK 10
- Clang/LLVM for native code generation and native integration tests

## Build and test

```powershell
dotnet build Eidosc.sln --nologo
dotnet test src/Eidosc.Tests/Eidosc.Tests.csproj --nologo
```

Run the CLI from source:

```powershell
dotnet run --project src/Eidosc.Cli -- --help
dotnet run --project src/Eidosc.Cli -- info --stdlib
```

## Common CLI commands

```text
eidosc new <directory>
eidosc build <file-or-project>
eidosc run <file-or-project>
eidosc analyze <file-or-project>
eidosc fmt <file>
eidosc lsp
```

Use `eidosc <command> --help` for the current command contract.

## Project status

Eidosc currently targets Eidos `0.4.0-alpha.1`. Release versions for Eidosc,
Eidosup, the standard library, and Bindgen are managed independently. Exact
release notes are stored under `changelogs/`.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security reports should follow
[SECURITY.md](SECURITY.md).

## License

MIT
