# Eidosup toolchain management

Eidosup installs verified Eidosc releases into immutable toolchain directories
and selects them through stable commands in `<EIDOS_HOME>/bin`. Toolchain
selection changes state only; it never adds version-specific directories to
PATH. Commands use `EIDOS_HOME` when present and otherwise use the platform
default install root; `--install-root` remains available for explicit isolated
operations and release verification.

## Toolchain specifications

WP1 supports three public specification forms:

```text
stable
preview
0.4.0-alpha.2
```

Exact versions also accept the documented `v` and `eidosc-v` prefixes. They are
canonicalized to plain SemVer in state. `stable` and `preview` are movable
channel selectors. Nightly, custom toolchains, environment selection, directory
overrides, and project files are not enabled by this command contract.

## Install, list, and uninstall

```powershell
eidosup toolchain install preview
eidosup toolchain install 0.4.0-alpha.2
eidosup toolchain list --verbose
eidosup toolchain list --json
eidosup toolchain uninstall 0.4.0-alpha.2
```

Installation uses the same release validation, checksum, content-addressed
cache, safe extraction, immutable identity, atomic rename, and state
registration path as `setup`. Installing a channel moves that channel selector
to the resolved verified release. Installing an exact version creates or
reuses only its immutable selector. The same exact SemVer cannot be rebound to
a different source or manifest identity; publishers must use a new version
instead of replacing an existing release identity.

Uninstall resolves every supplied selector before changing files. Eidosup
refuses to uninstall the toolchain referenced by the global default. Select a
different default or explicitly clear it first:

```powershell
eidosup default preview
eidosup default none
```

An explicit `none` is retained across later installs and channel updates;
Eidosup only chooses a default automatically when initializing a previously
unconfigured installation.

An uninstall first moves all selected immutable directories to transaction
backups, then atomically reconciles state. Its durable journal restores the
directories when state was not committed, or finishes owned backup cleanup when
state was committed. It never deletes an external or unverified directory.

## Update and check

```powershell
eidosup check
eidosup check preview --json
eidosup update
eidosup update preview --dry-run
```

With no specifications, `check` and `update` operate on every installed channel
selector in ordinal order. Exact specifications can be supplied explicitly;
they remain immutable and therefore either install the requested release or
report it as current. `check` performs release resolution without downloading
or changing state. `update` installs the new immutable release before moving a
channel selector, so a download, verification, extraction, or state failure
does not activate a partial toolchain.

Toolchain lifecycle operations share a per-root management lock. Low-level
installation and state files retain their own locks, so concurrent Eidosup
processes serialize lifecycle commits without weakening download or atomic
state guarantees. Checking an exact selector against a different configured
source reports a conflict instead of presenting the source change as an
ordinary update.

## Selection and explicit execution

```powershell
eidosup show
eidosup show active-toolchain --verbose
eidosup show home
eidosup show profile
eidosup which eidosc
eidosup which eidosc --toolchain 0.4.0-alpha.2 --json
eidosup run 0.4.0-alpha.2 -- eidosc build ./project
```

`show active-toolchain` and `which` fully verify the selected install before
reporting it. `run` uses the same resolver and process-forwarding contract as
the stable shim: arguments, working directory, standard streams, signals,
toolchain environment, and exact child exit code are preserved. WP1 toolchains
currently provide the `eidosc` command; command paths and arbitrary external
programs are rejected.

`show profile` reports `default` as a fixed bootstrap profile. Configurable
minimal/default/complete profiles remain gated on the component artifact
contract and are not simulated by WP1.

## Rollback

Every channel movement records activation history. A rollback moves the channel
selector to the most recent different toolchain that is still installed and
verified:

```powershell
eidosup rollback preview
eidosup rollback                 # active default channel
eidosup rollback preview --dry-run
```

If the channel is also the global default, both pointers move in one atomic
state write. Exact-version selectors cannot be rolled back. Removing an older
toolchain also removes it as a rollback candidate. Repeated rollback can move
between retained verified channel activations; it never downloads or guesses a
release.

## Automation contract

Management read commands and mutation results support global `--json`. JSON is
written to standard output; structured errors continue to use standard error
and the stable Eidosup error/exit-code catalog. Write commands that can change
toolchains or selectors support `--dry-run`; a dry run resolves and validates
its requested operation without creating locks, journals, downloads, install
directories, or state changes.
