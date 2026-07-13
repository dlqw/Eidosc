# Distribution sources, mirrors, and offline bundles

## Sources

An active source is stored in `settings.toml` and may be a direct descriptor:

```text
github:dlqw/Eidosc
index:https://dist.example.org/eidos/index.json
offline:/absolute/path/to/imported-bundle
```

Signed index URLs must use HTTPS or `file:` and cannot contain credentials,
query strings, or fragments. HTTP proxy behavior uses the platform/.NET
`HTTP_PROXY`, `HTTPS_PROXY`, and `NO_PROXY` contract; credentials are not
written to settings, state, errors, or progress output.

Named groups configure multiple mirrors:

```powershell
eidosup source add corp index:https://primary.example/eidos/index.json --priority 200
eidosup source add corp index:https://backup.example/eidos/index.json --priority 100
eidosup source add corp github:dlqw/Eidosc --priority 1000
eidosup set source corp
eidosup source list
```

Use `--dry-run` with `source add`, `source remove`, or any `set` subcommand to
validate and display the proposed configuration without writing settings or
source state.

Trust dominates priority. If a group contains signed index/offline sources,
unsigned GitHub discovery is not eligible for fallback, even with a higher
numeric priority. Fallback occurs only between equally trusted sources.

## Signed metadata

`eidosup-index.json` is an Ed25519-signed envelope with:

- monotonically increasing metadata version;
- generation and expiration times;
- exact release, asset URL, size, and SHA-256 identities;
- a signed next-key set for root rotation;
- one or more signatures identified by key ID.

Eidosup pins the official `eidos-official-2026-01` public key. Initial private
or enterprise sources may add bootstrap public keys through
`EIDOSUP_TRUSTED_ED25519_KEYS` using `key-id=base64` entries separated by `;`.
Only a currently trusted signature may introduce a signed next-key set. During
a transition Eidosup retains the current and next keys so the transition index
remains repeatable. The first later index signed by a next key commits that set
and revokes keys omitted from it. A key ID cannot be rebound to different key
material. Release operators set `EIDOSUP_METADATA_NEXT_ED25519_KEYS` to
semicolon-separated `key-id=base64` public keys while signing the transition
index with the current private key. Signed mirrors must keep a stable source
identity so version rollback and rotation state apply across index updates.

Expired metadata, invalid signatures, unknown keys, malformed rotations, and
versions older than the highest trusted metadata version fail. SHA-256 remains
the content-integrity layer; the signature establishes metadata trust.

## Complete offline bundles

An offline ZIP contains `index.json` plus every relative asset named by the
signed index. Import validates the signature, expiration, rollback version,
safe ZIP paths, duplicate and special-file rejection, path and per-file limits,
file count, compression ratio, expanded size, asset size, and SHA-256 before
committing it under the content-addressed download root:

```powershell
eidosup cache import ./eidos-offline.zip
eidosup set source offline:/path/printed/by/import
eidosup toolchain install preview
eidosup cache export <bundle-sha256> ./copied-bundle.zip
```

`cache clean --max-size 2GiB` removes least-recently-used content until the
limit is met. `cache clean --all` removes cached artifacts and imported bundles;
both support `--dry-run`.
