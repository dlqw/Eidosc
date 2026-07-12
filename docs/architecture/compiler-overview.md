# Compiler architecture

This document describes the stable source-level boundaries of Eidosc. It is an
orientation guide, not a roadmap or an exhaustive language specification.

## Compilation flow

Eidosc lowers source through these major stages:

1. **Lexer and parser** produce syntax and AST nodes.
2. **Naming and semantic analysis** establish declarations, imports, modules,
   symbols, traits, abilities, and pattern semantics.
3. **Type and effect analysis** infer and validate types, generics, constraints,
   abilities, handlers, and compile-time values.
4. **HIR and MIR lowering** convert source constructs into progressively more
   explicit intermediate representations.
5. **Borrow analysis and MIR optimization** validate ownership and references,
   then prepare executable control flow.
6. **LLVM lowering and native code generation** emit LLVM IR, compile objects,
   build the Eidos runtime when needed, and link native outputs.

The main orchestration code lives under `src/Eidosc/Pipeline`. Phase-specific
implementations live in the corresponding `Parsing`, `Semantic`, `Types`,
`Hir`, `Mir`, `Borrow`, and `CodeGen` directories.

## Project and package model

`src/Eidosc/ProjectSystem` loads `eidos.toml`, resolves source and import roots,
builds package graphs, and maintains `eidos.lock.json`. CLI build, run, analyze,
format, package, and language-service commands share this project model.

The precompiled standard library is embedded from
`src/Eidosc/Stdlib/Precompiled/Std`. Native runtime sources are under
`src/Eidosc/Runtime` and are copied into published CLI bundles.

## Command-line and language services

`src/Eidosc.Cli` hosts the user-facing command line. The LSP server and the
structured IDE snapshot path reuse the compiler pipeline so diagnostics,
completion, hover, definitions, references, semantic tokens, and formatting use
the same language and project semantics as command-line builds.

## Incremental and query-driven execution

The query-driven pipeline maintains dependency-aware artifacts for repeated IDE
and build requests. Cache keys include the language version, project inputs,
compiler options, imported modules, standard-library identity, and relevant
native/runtime inputs. Cache reuse must never change diagnostics or generated
program behavior.

## Native boundary

Eidosc emits LLVM IR and relies on Clang/LLVM tools for object generation and
linking. The runtime is written in C and provides memory, task, synchronization,
and other native services used by generated programs. Host and target support
must be validated end to end; accepting a target triple alone does not prove a
complete cross-compilation toolchain.
