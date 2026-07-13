# CLI and automation contract

Global automation options are available across the command tree:

- `--json` writes one machine-readable result to standard output and structured
  errors to standard error;
- `--quiet` suppresses non-error human output;
- `--verbose` includes diagnostic details;
- `--color auto|always|never` controls ANSI diagnostics (`auto` follows the
  error terminal, `always` forces ANSI, and JSON is always uncolored);
- mutation commands expose `--dry-run` when they can plan without changing
  files, state, downloads, or system integration.

Settings changes and named source-group add/remove operations support
`--dry-run`; the returned human or JSON value is the validated proposed state.

Enum values are lower camel case in JSON. Progress never shares JSON standard
output. A downstream broken pipe exits successfully instead of emitting an
internal error.

Generate deterministic completion scripts with:

```powershell
eidosup completions bash
eidosup completions fish
eidosup completions zsh
eidosup completions powershell
```

Exit codes and structured error codes continue to follow the catalog described
in [bootstrap](bootstrap.md). New project, source, signature, lifecycle, and
custom-link failures reuse stable `invalidArgument`, `invalidReleaseMetadata`,
`stateCorrupt`, `toolchainUnavailable`, `installFailure`, and network classes.

See the [JSON schema compatibility policy](json-schema-policy.md) and published
schemas for the in-band versioning rules.
