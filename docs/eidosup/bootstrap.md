# Eidosup bootstrap guide

Eidosup installs an Eidosc release for the current host, checks LLVM/Clang,
and can configure the user environment. It is a prerelease bootstrap tool; the
multi-toolchain commands described by mature toolchain managers are not part of
the current public contract.

## Release source and credentials

The default source is the public GitHub repository `dlqw/Eidosc`. Public
releases require no credentials. A custom GitHub repository can be selected
with `--repo owner/name`.

For a private custom source, Eidosup reads a token from `GITHUB_TOKEN`, then
`GH_TOKEN`. Tokens are used only in the GitHub HTTP authorization header. They
are not written to configuration, install state, output, or diagnostic stacks.

The new public repository does not contain releases copied from the private
archive. Installation becomes available when a new public Eidosc candidate is
published with an `eidosc-v<SemVer>` tag, host assets, and `SHA256SUMS`.

## Verified and atomic installation

Eidosup requires the selected release to publish `SHA256SUMS`. It downloads the
checksum file with a bounded size, then verifies the host bundle's declared
size and SHA-256 digest before the bundle can enter the content-addressed cache.
Interrupted bundle downloads use HTTP range requests when the release host
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

## Version and channel selection

Exact versions accept one of these equivalent forms:

```text
0.4.0-alpha.2
v0.4.0-alpha.2
eidosc-v0.4.0-alpha.2
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
eidosup setup --version 0.4.0-alpha.2
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
| `20`-`24` | integrity, archive, conflict, transaction, or lock failure |
| `30`-`31` | local permission or file failure |
| `50` | doctor found an error-level readiness failure |
| `70` | unexpected internal failure |
| `130` | cancellation |

`eidosup doctor` emits stable check IDs such as `command.eidosc`,
`command.clang`, `install.root`, and `toolchains.installed`. Missing Eidosc is
an error and returns `50`; advisory LLVM or environment findings remain
warnings. Use JSON for automation:

```powershell
eidosup doctor --json
```
