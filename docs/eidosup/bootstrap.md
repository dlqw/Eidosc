# Eidosup bootstrap guide

Eidosup installs Eidosc releases for the current host, checks LLVM/Clang, and
can configure the user environment. It also manages multiple immutable
toolchains through a stable `eidosc` shim, movable channel selectors, an
explicit global default, verified update and uninstall operations, and channel
rollback. See [Toolchain management](toolchain-management.md) for the complete
command contract.

## Release source and credentials

The default source is the public GitHub repository `dlqw/Eidosc`. Public
releases require no credentials. A custom GitHub repository can be selected
with `--repo owner/name`.

For a private custom source, Eidosup reads a token from `GITHUB_TOKEN`, then
`GH_TOKEN`. Tokens are used only in the GitHub HTTP authorization header. They
are not written to configuration, install state, output, or diagnostic stacks.

The public repository does not contain releases copied from the private archive.
Only newly built public releases with an `eidosc-v<SemVer>` tag, host assets,
and `SHA256SUMS` are installable.

Eidosc releases are assembled as short-lived CI artifacts before publication.
The candidate must contain all six Windows, Linux, and macOS x64/arm64 bundles,
the .NET tool package, deterministic `SHA256SUMS`, `eidosc-release.json`, and six
host component manifests. CI validates every digest and ZIP path, then performs
native clean installations on Windows, Linux, and macOS x64/arm64 runners with
the matching candidate Eidosup binary. Each installed compiler must pass
`eidosc info`, external Std inspection, native executable generation, opposite-
architecture LLVM object generation, and management-command smoke checks for
components, profiles, targets, docs, list, which, explicit run, check,
convergent update, default clearing/restoration, active-uninstall refusal, and
transactional uninstall. The GitHub tag and prerelease are created only after
all candidate gates succeed. A second six-RID job then removes GitHub tokens and
repeats installation from the public release, so CI-only access cannot satisfy
the gate. Eidosup continues to reject draft releases during normal selection.

## Verified and atomic installation

Eidosup requires the selected release to publish `SHA256SUMS` and a
host-specific `eidos-toolchain-v<version>-<rid>.json`. It verifies the
manifest digest against signed release metadata and checksums, validates
component ownership/dependencies/profiles/targets, and then verifies every
selected artifact's size and SHA-256 before it can enter the content-addressed
cache. Interrupted downloads use HTTP range requests when the release host
supports them. Transient transfers have a finite retry limit.

Verified bundles are cached below `downloads/sha256/<prefix>/<digest>`. A cache
hit is rehashed before use; a path or filename is never trusted as proof of
integrity.

Archives are extracted into a unique staging directory. Absolute paths, parent
traversal, links, duplicate or conflicting paths, excessive expansion, and
unsupported special files are rejected. Eidosup records every installed file,
size, and digest in `.eidosup-install.json` and rehashes the complete installed
file set before treating an existing toolchain as valid.

Installation changes are serialized by a per-root lock. The previous target is
moved to a transaction-specific rollback directory before the staged tree is
renamed atomically into place. A durable journal lets the next setup operation
finish cleanup or restore the previous target after interruption. `--force`
uses the same transaction path; it does not overwrite an existing directory in
place. `--dry-run` resolves and validates release metadata but creates no install,
download, cache, lock, staging, or journal directory.

## Toolchain identity and state

Each verified install uses an immutable internal ID containing the resolved
Eidosc version, host RID, and a full SHA-256 identity digest. The digest covers
the release tag, source, distribution-manifest name/digest, profile, selected
component IDs, explicit components, and explicit targets. A typical directory
has this shape:

```text
toolchains/eidosc-0.4.0-alpha.3-win-x64-<sha256>/
```

The schema 3 `.eidosup-install.json` records that identity, component
ownership, artifacts, profile, and targets, and remains inside
the immutable directory. Eidosup will not register a directory unless the ID,
manifest identity, host, version, complete file list, executable modes, sizes,
and file digests all verify.

The private state file is `state/toolchains.json`, currently schema 3. It
defines installed variants and composition metadata, movable selectors, the
global default, activation history, custom links, directory overrides,
transaction records, and unmanaged-directory diagnostics. Schema 1 and 2 state
is migrated through verified install manifests. State writes use a separate
state lock, write-through temporary file, atomic replacement, and
`toolchains.json.bak`. Repeated initialization is idempotent. If the primary
state is malformed, Eidosup reconciles a supported backup with verified
immutable install manifests. An unknown or future schema is never overwritten;
the user must upgrade Eidosup first.

The older `toolchains/eidosc/<version>` layout is not imported or activated,
even if its directory name looks like a release. Reinstall required toolchains
into the immutable layout, verify they work, and then remove the legacy directory
manually.

## Stable shim and default selection

`setup` installs the running Eidosup executable into the stable command
directory and materializes the `eidosc` shim from the same multi-call binary:

```text
<EIDOS_HOME>/bin/eidosup[.exe]
<EIDOS_HOME>/bin/eidosc[.exe]
<EIDOS_HOME>/bin/.eidosup-shims.json
```

Eidosup prefers a hard link so the two command names share one on-disk binary;
filesystems that do not support hard links use a verified copy. Updates stage
both commands in the destination directory, preserve owned previous files, and
restore them if the pair cannot be committed. Existing command files without a
valid ownership manifest are never overwritten.

The first verified installation becomes the global default. A channel install
such as `preview` moves that channel selector and, when active, its default
pointer to the new immutable toolchain without changing PATH. Exact-version
selectors continue to point at their original immutable installs. Use
`eidosup default <selector>` or `eidosup default none` to change the global
selection explicitly.

When invoked as `eidosc`, the multi-call binary derives `EIDOS_HOME` from its
managed `bin` location, reads the state schema, resolves the default selector,
and fully verifies the selected install manifest and files before starting the
compiler. Arguments, working directory, standard streams, console signal group,
and child exit code are preserved. The child receives version-specific
`EIDOSC_HOME` and `EIDOS_RUNTIME_PATH` values only for that process.

User environment configuration now adds only the stable `bin` directory and
LLVM directory to PATH. It keeps `EIDOS_HOME`, removes the old global
`EIDOSC_HOME` and `EIDOS_RUNTIME_PATH` bindings, and therefore does not need to
change when the default selector moves.

The release clean-install gate compares direct and shim exit codes and measures
the median incremental shim startup cost after warmup. The current native-runner
upper bounds are 200 ms for Linux x64/ARM64 and macOS ARM64, 300 ms for Windows
x64/ARM64, and 600 ms for macOS x64. The gate prints the direct, shim, overhead,
and applicable baseline medians for every RID so future changes remain
measurable rather than relying on a cross-runner uniform assumption. See
[Shim architecture and forwarding contract](shim.md) for the detailed contract.

## LLVM dependency providers

Eidosc currently supports LLVM/Clang major versions 18 through 22 and requires
both `clang` and `llvm-ar` for the complete native toolchain contract. Eidosup
probes required commands and parses `clang --version`; finding a command on PATH
alone is not treated as compatibility proof.

`eidosup setup` is the explicit dependency-install entry point. If LLVM is
missing, it builds a platform provider plan before running anything:

- Windows: winget, then Chocolatey, then Scoop;
- Linux: apt, DNF, Yum, Pacman, or Zypper, with sudo only when required;
- macOS: Homebrew LLVM.

All package-manager arguments are fixed and non-interactive. An existing but
unsupported LLVM installation is never replaced implicitly; setup stops with a
version-range error and remediation. `--dry-run` prints every planned system
command without executing it. `--skip-clang` skips both validation and provider
actions. The dependency coordinator also exposes a diagnose-only policy for
future update operations; that policy cannot install system software.

`eidosup doctor` reports the combined dependency contract as
`dependency.llvm`. Missing commands are warnings; an installed but unsupported
or unverifiable LLVM is an error.

Doctor also reads `toolchains.state` without repairing it and compares recorded
toolchains with fully verified install manifests. Schema incompatibility,
corrupt state, stale entries, and modified installed files are error-level
failures; legacy layout detection remains a warning with manual cleanup steps.

## Version and channel selection

Exact versions accept one of these equivalent forms:

```text
0.4.0-alpha.3
v0.4.0-alpha.3
eidosc-v0.4.0-alpha.3
```

The version after the optional prefix must be valid SemVer 2.0.0. Unsafe path,
URI, incomplete version, leading-zero, or malformed prerelease input is
rejected before a request or filesystem operation.

When `--version` is omitted, `--channel` controls deterministic selection:

- `stable`: highest published non-prerelease SemVer;
- `preview`: highest published SemVer, including prereleases.

GitHub response order and publication time do not override SemVer precedence.
The default is `preview` while Eidos publishes prerelease toolchains.

```powershell
eidosup setup --channel preview --dry-run --skip-clang --skip-env
eidosup setup --version 0.4.0-alpha.3
```

## Errors and diagnostics

Expected failures use stable error identifiers and exit codes. Default output
does not include stack traces. Use `--verbose` to add redacted diagnostics or
`--json` to obtain a machine-readable error object on standard error.

Network, authentication, authorization, rate-limit, missing release, invalid
metadata, and missing host asset failures have distinct identifiers. The most
important exit-code ranges are:

| Range | Meaning |
| --- | --- |
| `2` | invalid command or release input |
| `10`-`16` | source, release, or asset failure |
| `20`-`29` | integrity, transaction, lock, dependency-provider, or state failure |
| `30`-`34` | local permission, file, active-toolchain, or proxy failure |
| `50` | doctor found an error-level readiness failure |
| `70` | unexpected internal failure |
| `130` | cancellation |

`eidosup doctor` emits stable check IDs such as `command.eidosc`,
`command.clang`, `install.root`, `shims.installed`, `shims.path`,
`toolchains.installed`, and `toolchains.default`. Missing Eidosc is
an error and returns `50`; advisory LLVM or environment findings remain
warnings. An explicitly cleared default is an informational warning rather than
state corruption; the stable shim remains inactive until `eidosup default
<selector>` is used. Use JSON for automation:

```powershell
eidosup doctor --json
```
