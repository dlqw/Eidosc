# Custom toolchains

Custom toolchains provide read-only references to local Eidosc development
builds:

```powershell
eidosup toolchain link local ./artifacts/eidosc
eidosup toolchain list --verbose
eidosup run custom:local -- eidosc info
eidosup override set custom:local --path ./compiler-test-project
eidosup toolchain unlink local
```

The linked root must be a regular directory containing `eidosc[.exe]` at the
root or in `bin/`, plus a regular `runtime/` directory. Names use ASCII letters,
digits, `.`, `_`, and `-`. Eidosup revalidates the layout whenever it resolves
the link.

Links may be selected by the global default, environment, project files,
directory overrides, `run`, `which`, and the shim. They are never updated,
downloaded, copied into `EIDOS_HOME`, or deleted by Eidosup. Unlinking an active
global default is rejected, and unlinking removes dependent directory
overrides while preserving the external build.
