<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/eidos-lockup-dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="docs/assets/eidos-lockup.svg">
    <img src="docs/assets/eidos-lockup.svg" width="330" alt="Eidos — owl symbol and wordmark">
  </picture>
</p>

<p align="center">
  <strong>An experimental, statically typed native language with a functional core and typed metaprogramming.</strong>
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <a href="https://github.com/dlqw/Eidosc/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/dlqw/Eidosc/actions/workflows/ci.yml/badge.svg?branch=main"></a>
  <img alt="Status: prerelease" src="https://img.shields.io/badge/status-prerelease-c9654f">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-7aa2f7"></a>
</p>

Eidos combines an expression-oriented language, native compilation through
LLVM, and a coherent set of first-party development tools. This repository
contains **Eidosc**, the reference compiler and command-line toolchain, together
with the standard library, Eidosup toolchain manager, C binding generator, and
language services.

> [!IMPORTANT]
> Eidos is prerelease software. Language and tooling interfaces may change
> between prereleases, including incompatible changes. The current language
> baseline is **Eidos 0.6.0-alpha.1**; Eidos, Eidosc, Std, Eidosup, and Bindgen
> are versioned independently.

## Why Eidos?

- **An expressive functional core.** Algebraic data types, pattern matching,
  traits, higher-kinded generics, and expression-oriented control flow are
  designed to work together.
- **Typed compile-time programming.** Pure CTFE, value-level const generics,
  read-only reflection, and structured user-defined derives generate checked
  declarations instead of source strings.
- **Explicit capability boundaries.** Build programs declare their inputs,
  tools, steps, and outputs; ordinary compile-time evaluation does not inherit
  ambient host access.
- **One integrated toolchain.** The compiler, project model, formatter,
  documentation generator, diagnostics, and language services share the same
  language semantics.
- **Native output.** Eidosc lowers through HIR and MIR to LLVM, then uses the
  Eidos runtime and Clang/LLVM to produce native programs.

## A first look

Eidos uses name-first declarations and pattern branches:

```eidos
Shape :: type
{
    Circle(Int),
    Rectangle(Int, Int)
}

area_hint :: Shape -> Int
{
    Circle(radius) => radius * radius,
    Rectangle(width, height) => width * height
}

main :: Unit -> Int
{
    _ => area_hint(Rectangle(6, 7))
}
```

See the [English tutorial][tutorial-en] or
[简体中文教程][tutorial-zh] for a guided introduction to the language.

## Build and try Eidosc

### Requirements

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)
- Clang/LLVM for native code generation and native integration tests

### From source

```powershell
git clone https://github.com/dlqw/Eidosc.git
cd Eidosc
dotnet build Eidosc.sln --nologo

# Create and run a minimal project.
dotnet run --project src/Eidosc.Cli -- new hello-eidos --name dev.eidos.hello
dotnet run --project src/Eidosc.Cli -- run hello-eidos
```

Use `dotnet run --project src/Eidosc.Cli -- --help` to inspect the current CLI.
For prerelease distribution, installation, channels, integrity verification,
and environment setup, read the [Eidosup bootstrap guide](docs/eidosup/bootstrap.md).

Common development commands:

| Task | Command |
| --- | --- |
| Build the solution | `dotnet build Eidosc.sln --nologo` |
| Run the test suite | `dotnet test src/Eidosc.Tests/Eidosc.Tests.csproj --nologo` |
| Analyze a file or project | `dotnet run --project src/Eidosc.Cli -- analyze <path>` |
| Format a file | `dotnet run --project src/Eidosc.Cli -- fmt <file>` |
| Start the language server | `dotnet run --project src/Eidosc.Cli -- lsp` |
| Inspect compiler and Std information | `dotnet run --project src/Eidosc.Cli -- info --stdlib` |

## Toolchain

| Component | Purpose |
| --- | --- |
| **Eidosc** | Compiler frontend, type system, HIR/MIR pipeline, borrow analysis, LLVM backend, formatter, documentation generator, and language services |
| **Eidosc CLI** | The `eidosc` command-line interface, project/package workflows, IDE snapshots, and LSP server |
| **Eidos Std** | The versioned standard library distributed with Eidos toolchains |
| **Eidosup** | Installer and manager for verified, immutable Eidos toolchains, components, profiles, and targets |
| **Eidosc.Bindgen** | C header binding generator for Eidos packages |

## Repository layout

| Path | Contents |
| --- | --- |
| [`src/Eidosc`](src/Eidosc) | Compiler, project system, standard library sources, runtime sources, and native code generation |
| [`src/Eidosc.Cli`](src/Eidosc.Cli) | CLI, project commands, REPL/TUI, IDE services, and LSP server |
| [`src/Eidosup`](src/Eidosup) | Toolchain installer and manager |
| [`src/Eidosc.Bindgen`](src/Eidosc.Bindgen) | C binding generator |
| [`src/Eidosc.Tests`](src/Eidosc.Tests) | Compiler, CLI, runtime, and tooling tests |
| [`src/Eidosc.Benchmarks`](src/Eidosc.Benchmarks) | Repeatable compiler and language-service benchmarks |
| [`docs`](docs) | Compiler architecture and version-matched Eidosup documentation |
| [`eng`](eng) | Authoritative component version properties |
| [`scripts`](scripts) | Verification, release, and performance automation |
| [`changelogs`](changelogs) | Versioned release notes and in-development changelog fragments |

## Learn and explore

| Resource | English | 简体中文 |
| --- | --- | --- |
| Language tutorial | [Read the tutorial][tutorial-en] | [阅读教程][tutorial-zh] |
| Grammar reference | [BNF][grammar-en] | [BNF][grammar-zh] |
| Compiler architecture | [Overview](docs/architecture/compiler-overview.md) | — |
| Eidosup | [Bootstrap](docs/eidosup/bootstrap.md) · [Toolchain management](docs/eidosup/toolchain-management.md) · [Components, profiles, and targets](docs/eidosup/components-profiles-targets.md) | — |
| Editor support | [Visual Studio Code](https://github.com/dlqw/vscode-eidosc) | [Visual Studio Code](https://github.com/dlqw/vscode-eidosc) |
| Release notes | [Changelogs](changelogs) | [Changelogs](changelogs) |

## Contributing

Contributions that improve the language, compiler, tooling, tests, and
documentation are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md) for the
branch, code style, testing, and pull-request workflow.

For a typical change:

1. create a focused, short-lived branch from the latest `main`;
2. add or update tests for observable behavior;
3. run the relevant local verification commands;
4. open a pull request against `main` and describe the motivation, behavior,
   and verification performed.

Use [GitHub Issues](https://github.com/dlqw/Eidosc/issues) for reproducible bugs
and feature proposals. Please report suspected vulnerabilities through the
private process in [SECURITY.md](SECURITY.md), not through a public issue.

## License

Eidosc is available under the [MIT License](LICENSE).

Copyright © 2026 rdququ.

[tutorial-en]: https://github.com/dlqw/eidos-tutorial/blob/main/README.en.md
[tutorial-zh]: https://github.com/dlqw/eidos-tutorial/blob/main/README.zh-CN.md
[grammar-en]: https://github.com/dlqw/eidos-tutorial/blob/main/BNF.en.md
[grammar-zh]: https://github.com/dlqw/eidos-tutorial/blob/main/BNF.zh-CN.md
