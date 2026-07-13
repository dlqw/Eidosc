# Eidosup shim architecture and forwarding contract

## Purpose

Eidosup keeps PATH independent from any concrete Eidosc release. The stable
`<EIDOS_HOME>/bin/eidosc` command is a multi-call entry point to Eidosup; the
selected compiler remains in an immutable `toolchains/<toolchain-id>` directory.
Changing a selector therefore updates state rather than shell configuration.

## Installation and ownership

`eidosup setup` installs these owned files:

```text
bin/eidosup[.exe]
bin/eidosc[.exe]
bin/.eidosup-shims.json
```

The manager and shim are prepared beside their final paths. A hard link is used
when supported, with a durable copy as the fallback. Existing owned files are
moved to transaction-specific backups before replacement and restored if any
commit step fails. Eidosup refuses to replace an unowned command or an ownership
manifest with an unknown schema.

The implementation currently uses the same self-contained binary for manager
and proxy modes. A NativeAOT spike demonstrated lower size and startup cost, but
the full manager still contains JSON paths that require source-generation work
and the six release RIDs do not yet share one proven cross-AOT build contract.
The stable path, state, and forwarding contracts allow a future dedicated AOT
proxy without changing user projects or selector data.

## Selection

Proxy resolution uses explicit `+toolchain`/`--toolchain` selection,
`EIDOSUP_TOOLCHAIN`, the nearest project file or directory override, and then
the global default. The precedence and project composition rules are documented
in [Project toolchain selection](project-selection.md).

Resolution rejects a missing default, missing selector, host RID mismatch,
unknown command, unsupported state schema, changed install manifest, modified
payload file or executable mode, link/reparse point, missing Std, or unsatisfied
project components/targets. The compiler-owned `cache/grammar.bin` is the only
generated file excluded from the payload file set; any other added file still
invalidates the installation. Only the selected immutable payload is
reverified, so additional installed versions do not add proxy startup work.

## Process forwarding

The proxy starts the resolved compiler path directly with shell execution
disabled. It preserves:

- every argument as a separate operating-system argument;
- the current working directory;
- inherited standard input, output, and error handles;
- the existing console/process signal group;
- the compiler's exact exit code.

The child process receives the resolved values of `EIDOS_HOME`, `EIDOSC_HOME`,
`EIDOS_STDLIB_PATH`, `EIDOS_TARGETS_PATH`, and, when installed,
`EIDOS_RUNTIME_PATH`. These values are not persisted as version-bound user
environment variables.

## Release gate

All six native clean-install jobs invoke the installed shim for external Std
inspection, native tutorial compilation, component/profile changes, offline
docs, and opposite-architecture LLVM IR/object generation. They also compare an
intentional compiler failure through direct and shim paths and measure nine
post-warmup startup samples.

The incremental median gate uses native-runner upper bounds that account for
the full activation-time manifest verification and the runner's process-start
cost: 200 ms for Linux x64/ARM64 and macOS ARM64, 300 ms for Windows x64/ARM64,
and 600 ms for macOS x64. Every job prints its direct, shim, overhead, and
baseline medians. Eidosup release artifacts use ReadyToRun compilation, and all
six RID artifacts use the same multi-call source path before publication.
