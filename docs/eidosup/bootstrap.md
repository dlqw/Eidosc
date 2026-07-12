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
published with an `eidosc-v<SemVer>` tag and host assets.

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
