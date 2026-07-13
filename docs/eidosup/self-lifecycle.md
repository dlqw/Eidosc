# Eidosup self lifecycle

```powershell
eidosup self update --check-only
eidosup self update
eidosup set auto-self-update enable|disable|check-only
eidosup self uninstall --yes
eidosup self uninstall --yes --keep-toolchains
```

Self-update resolves the newest Eidosup release for the current host, refuses
downgrades, verifies `SHA256SUMS`, downloads through the content-addressed
cache, and only then replaces the stable manager and shim. Unix uses atomic
replacement. Windows starts the verified new binary as a delayed helper; it
waits for the old process to exit before installing the manager and shim.
Interrupted replacement leaves the old stable binary usable, and stale staged
files are cleaned on the next normal start.

Self-uninstall requires `--yes`. It removes the managed shell/profile block,
the exact `EIDOS_HOME/bin` PATH entry, version-bound legacy variables, and only
paths proven owned by Eidosup. The shim ownership manifest is mandatory.
Managed immutable toolchain directories are removed by state identity; custom
external paths and unverified/unmanaged directories are never deleted.

`--keep-toolchains` preserves verified toolchains and their state so a later
Eidosup installation can resume management. Downloads, lifecycle locks,
transactions, trust cache, and the installed manager/shim are still removed.
