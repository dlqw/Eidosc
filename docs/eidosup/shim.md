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

Proxy resolution currently has two defined sources:

1. an explicit selector supplied by an internal caller;
2. the global default stored in `state/toolchains.json`.

The command-line shim uses the global default. Environment, directory override,
and project-file selection remain reserved for the project-selection phase and
do not silently affect this release.

Resolution rejects a missing default, missing selector, host RID mismatch,
unknown command, unsupported state schema, changed install manifest, modified
payload file, link/reparse point, or incomplete compiler/runtime layout. The
compiler-owned `cache/grammar.bin` is the only generated file excluded from the
payload file set; any other added file still invalidates the installation. Only
the selected immutable payload is reverified, so additional installed versions
do not add proxy startup work.

## Process forwarding

The proxy starts the resolved compiler path directly with shell execution
disabled. It preserves:

- every argument as a separate operating-system argument;
- the current working directory;
- inherited standard input, output, and error handles;
- the existing console/process signal group;
- the compiler's exact exit code.

The child process receives the resolved values of `EIDOS_HOME`, `EIDOSC_HOME`,
and `EIDOS_RUNTIME_PATH`. These values are not persisted as version-bound user
environment variables.

## Release gate

Native Windows x64 and Linux x64 clean-install jobs invoke the installed shim
for `eidosc info` and tutorial HIR compilation. They also compare an intentional
compiler failure through direct and shim paths, and measure nine post-warmup
startup samples. The median incremental shim overhead must not exceed 200 ms.
Eidosup release artifacts use ReadyToRun compilation so the full verification
path remains within that baseline. All six published RID artifacts use the same
multi-call source path and receive binary/version smoke validation before
publication.
