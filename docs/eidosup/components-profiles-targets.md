# Components, profiles, targets, and offline documentation

Eidosup installs each Eidosc release from a host-specific toolchain manifest.
The manifest is bound to the signed release index, `SHA256SUMS`, release asset
sizes, and the exact files owned by every component. Unknown fields, unsafe
paths, duplicate ownership, dependency cycles, conflicts, and mismatched
artifact identities are rejected before installation.

## Components

Published toolchains currently define:

- `eidosc-core`: the host compiler and compatibility metadata;
- `eidos-std`: the independently versioned external Std source component;
- `eidos-runtime@<rid>`: runtime sources for one host or cross target;
- `eidos-docs`: version-matched offline Eidos/Eidosc documentation;
- `eidos-bindgen`: the independently versioned bindgen executable.

List all published components or only the installed set:

```powershell
eidosup component list
eidosup component list --installed --toolchain preview
eidosup component list --json
```

Add and remove optional components with:

```powershell
eidosup component add eidos-docs eidos-bindgen
eidosup component remove eidos-docs
```

Every change creates or reuses an immutable variant. The variant identity binds
the release manifest digest, profile, selected component IDs, explicit
components, and explicit targets. Eidosup atomically moves the selector to the
new variant; the previous directory is not edited. Removing an optional
component never deletes a file owned by another component because ownership is
exclusive in the signed manifest.

Profile components, required components, dependencies, and selected target
runtimes cannot be removed. Select a smaller profile or remove the dependent
selection first. Uninstalling a release selector removes all of its retained
composition variants after the active default has been cleared.

## Profiles

`minimal`, `default`, and `complete` are monotonic manifest-defined
component sets:

- `minimal`: compiler plus Std;
- `default`: minimal plus the host runtime;
- `complete`: default plus docs and bindgen.

```powershell
eidosup show profile
eidosup set profile minimal
eidosup set profile complete --toolchain 0.4.0-alpha.3
```

A profile affects initial installation and an explicit profile change. Manual
component and target additions remain explicit when the profile changes, even
when the new profile also contains the same files. Updates preserve the active
profile and explicit selections. Channel rollback skips variants of the current
release and returns to the previous retained release rather than treating a
component change as a version rollback.

## Targets and readiness

Each target entry declares its compiler triple, runtime component, host or
cross-compile support, linker command, and whether an external SDK/sysroot is
still required.

```powershell
eidosup target list
eidosup target list --installed --json
eidosup target add linux-arm64
eidosup target remove linux-arm64
```

`target list` reports compiler support, runtime installation, linker command
availability, and external SDK readiness separately. A cross runtime can be
installed and LLVM IR/object generation can work while final linking still
requires a target SDK; such a target is not reported as fully ready.

The stable compiler shim passes `EIDOS_TARGETS_PATH`,
`EIDOS_RUNTIME_PATH`, and `EIDOS_STDLIB_PATH`. Eidosc resolves a selected
cross runtime from `targets/<rid>/runtime` before falling back to the host
runtime.

Release gates install and start the matching native Eidosup/Eidosc pair on all
six Windows, Linux, and macOS x64/ARM64 RIDs. Every runner also installs the
opposite-architecture runtime for its platform and produces LLVM IR plus an
object file, while native compilation must emit a host executable. The same
six-RID install smoke repeats anonymously after the public release is created.

## Project requirements

`eidos-toolchain.toml` may request a profile, components, and targets. A
larger installed profile satisfies a smaller project requirement. With
auto-install enabled, Eidosup installs a missing selector and adds missing
requirements without deleting existing explicit components or targets.

```toml
[toolchain]
channel = "preview"
profile = "default"
components = ["eidos-docs"]
targets = ["linux-arm64"]
```

## Offline documentation

The docs component contains a strict `docs/index.json` whose Eidosc version
must match the selected install manifest.

```powershell
eidosup doc
eidosup doc compiler
eidosup doc project --path
eidosup doc index --json
```

Without `--path` or `--json`, Eidosup opens the resolved local file with the
platform shell. Topic paths must remain inside the installed docs component and
work without network access.

The stable JSON contracts are published as
[`component-list-v1`](schemas/component-list-v1.schema.json),
[`target-list-v1`](schemas/target-list-v1.schema.json),
[`composition-change-v1`](schemas/composition-change-v1.schema.json), and
[`doc-v1`](schemas/doc-v1.schema.json).
