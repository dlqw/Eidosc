# Project toolchain selection

Eidosup 0.3 defines project selection independently from the Eidos language
version in `eidos.toml`. Compiler selection never rewrites project language
semantics.

## Precedence

The stable `eidosc` shim resolves a selector in this order:

1. `+toolchain` on the shim or an explicit `eidosup run/which --toolchain` value;
2. `EIDOSUP_TOOLCHAIN`;
3. the nearest selector while walking from the working directory to its
   ancestors;
4. the global default.

At each ancestor, `eidos-toolchain.toml` wins over a directory override at the
same path. A closer directory override wins over a project file in a more
distant ancestor. Missing selectors fail with the selector source and an
install command; Eidosup does not silently choose another installed version.

## `eidos-toolchain.toml` contract

```toml
[toolchain]
channel = "0.4.0-alpha.2"
profile = "default"
components = []
targets = []
```

`channel` accepts `stable`, `preview`, an exact Eidosc SemVer, an explicit host
form such as `0.4.0-alpha.2@linux-arm64`, or `custom:<name>`. Unqualified
downloadable selectors use the validated `set default-host <rid>` setting. The
file uses strict UTF-8 TOML scalar and string-array parsing. Assignments outside
`[toolchain]`, unknown sections or keys, duplicate sections or keys,
non-default profiles, and non-empty component or target requests fail. The
`profile` field must remain `"default"` until profiles, components, and targets
become active under the WP3 artifact contract.

When the project has `eidos.toml [language].version` and the selected toolchain
contains `compatibility.json`, Eidosup verifies `language.supported` before
starting the compiler. The compatibility file must declare schema 1, component
`eidosc`, its canonical Eidosc version, and the supported language range; a
managed file whose declared version differs from its install manifest fails.
An incompatible pair fails without changing either version. Toolchains
published before compatibility metadata report an unknown compatibility status
but remain usable.

## Directory overrides

```powershell
eidosup override set preview --path ./project
eidosup override list
eidosup override unset --path ./project
eidosup override unset --nonexistent
```

Override paths are stored as canonical absolute paths in the versioned,
atomic toolchain state. The selected toolchain must already be installed or
linked. Removing nonexistent overrides is explicit; Eidosup never repoints an
override because a directory moved.

## Automatic installation

```powershell
eidosup set auto-install enable
eidosup set auto-install prompt
eidosup set auto-install disable
```

`prompt` only installs after confirmation on an interactive terminal. It does
not mutate non-interactive CI. `enable` may install a missing downloadable
selector through the configured verified source. Custom links are never
downloaded automatically.
