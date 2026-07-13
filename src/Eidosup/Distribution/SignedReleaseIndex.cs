using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Installation;
using NSec.Cryptography;

namespace Eidosup.Distribution;

public enum DistributionSourceKind
{
    GitHub,
    SignedIndex,
    OfflineBundle
}

public sealed record DistributionSourceDescriptor(
    DistributionSourceKind Kind,
    string Value,
    string Canonical)
{
    public static DistributionSourceDescriptor Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException("A distribution source cannot be empty or contain surrounding whitespace.");
        }

        if (value.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            var repository = GitHubRepositoryId.Parse(value["github:".Length..]);
            return new DistributionSourceDescriptor(DistributionSourceKind.GitHub, repository.ToString(), $"github:{repository}");
        }

        if (!value.Contains(':'))
        {
            var repository = GitHubRepositoryId.Parse(value);
            return new DistributionSourceDescriptor(DistributionSourceKind.GitHub, repository.ToString(), $"github:{repository}");
        }

        if (value.StartsWith("index:", StringComparison.OrdinalIgnoreCase))
        {
            var uri = ParseIndexUri(value["index:".Length..]);
            return new DistributionSourceDescriptor(DistributionSourceKind.SignedIndex, uri.AbsoluteUri, $"index:{uri.AbsoluteUri}");
        }

        if (value.StartsWith("offline:", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(value["offline:".Length..]);
            return new DistributionSourceDescriptor(DistributionSourceKind.OfflineBundle, path, $"offline:{path}");
        }

        throw new FormatException("A distribution source must be github:<owner/repo>, index:<https-url>, or offline:<bundle-path>.");
    }

    private static Uri ParseIndexUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "file") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new FormatException("A signed index source must use an absolute HTTPS or file URI without credentials, query, or fragment.");
        }

        return uri;
    }
}

public sealed record SignedReleaseIndexEnvelope(
    SignedReleaseIndex Payload,
    IReadOnlyList<MetadataSignature> Signatures);

public sealed record SignedReleaseIndex(
    int Schema,
    long Version,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<MetadataPublicKey> NextKeys,
    IReadOnlyList<SignedReleaseEntry> Releases);

public sealed record SignedReleaseEntry(
    string TagName,
    string Name,
    bool PreRelease,
    DateTimeOffset PublishedAt,
    IReadOnlyList<SignedReleaseAsset> Assets);

public sealed record SignedReleaseAsset(
    string Name,
    string Url,
    long Size,
    string Sha256);

public sealed record MetadataSignature(string KeyId, string Algorithm, string Signature);

public sealed record MetadataPublicKey(string KeyId, string Algorithm, string PublicKey);

public sealed class SignedReleaseIndexVerifier
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    private readonly MetadataTrustStore _trustStore;
    private readonly Func<DateTimeOffset> _clock;

    public SignedReleaseIndexVerifier(MetadataTrustStore trustStore, Func<DateTimeOffset>? clock = null)
    {
        _trustStore = trustStore;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task VerifyAsync(
        string sourceIdentity,
        SignedReleaseIndexEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload == null || envelope.Signatures == null ||
            envelope.Payload.NextKeys == null || envelope.Payload.Releases == null ||
            envelope.Payload.Schema != 1 || envelope.Payload.Version < 1 ||
            envelope.Payload.GeneratedAt == default || envelope.Payload.ExpiresAt <= envelope.Payload.GeneratedAt ||
            envelope.Signatures.Count is 0 or > 32 ||
            envelope.Payload.NextKeys.Count > 32 ||
            envelope.Payload.Releases.Count > 1_000)
        {
            throw Invalid("The signed release index has an invalid schema, version, lifetime, or signature set.");
        }

        ValidatePayload(envelope.Payload);

        var now = _clock();
        if (now > envelope.Payload.ExpiresAt)
        {
            throw Invalid($"Signed release metadata expired at {envelope.Payload.ExpiresAt:O}.");
        }

        await using var trustLock = await _trustStore.AcquireAsync(sourceIdentity, cancellationToken);
        var trust = await _trustStore.ReadAsync(sourceIdentity, cancellationToken);
        if (envelope.Payload.Version < trust.HighestVersion)
        {
            throw Invalid(
                $"Signed release metadata version {envelope.Payload.Version} is older than trusted version {trust.HighestVersion}.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, CanonicalJsonOptions);
        string? verifiedKeyId = null;
        foreach (var signature in envelope.Signatures)
        {
            if (signature == null ||
                !string.Equals(signature.Algorithm, "ed25519", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(signature.KeyId) ||
                !trust.Keys.TryGetValue(signature.KeyId, out var encodedKey))
            {
                continue;
            }

            try
            {
                var publicKey = PublicKey.Import(
                    SignatureAlgorithm.Ed25519,
                    Convert.FromBase64String(encodedKey),
                    KeyBlobFormat.RawPublicKey);
                if (SignatureAlgorithm.Ed25519.Verify(
                    publicKey,
                    payload,
                    Convert.FromBase64String(signature.Signature)))
                {
                    verifiedKeyId = signature.KeyId;
                    break;
                }
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or CryptographicException)
            {
                throw Invalid("Signed release metadata contains malformed Ed25519 key material.", exception);
            }
        }

        if (verifiedKeyId == null)
        {
            throw Invalid("No trusted Ed25519 signature verified the release index.");
        }

        var persistedKeys = envelope.Payload.NextKeys.Count == 0
            ? trust.Keys
            : AdvanceTrustKeys(
                trust.Keys,
                ValidateNextKeys(envelope.Payload.NextKeys),
                verifiedKeyId);
        await _trustStore.WriteAsync(
            sourceIdentity,
            new MetadataTrustState(MetadataTrustState.CurrentSchema, envelope.Payload.Version, persistedKeys),
            cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> AdvanceTrustKeys(
        IReadOnlyDictionary<string, string> currentKeys,
        IReadOnlyDictionary<string, string> nextKeys,
        string verifiedKeyId)
    {
        foreach (var next in nextKeys)
        {
            if (currentKeys.TryGetValue(next.Key, out var current) &&
                !string.Equals(current, next.Value, StringComparison.Ordinal))
            {
                throw Invalid($"Signed release metadata attempts to rebind trusted key ID '{next.Key}'.");
            }
        }

        if (nextKeys.ContainsKey(verifiedKeyId))
        {
            return nextKeys;
        }

        var transition = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var current in currentKeys)
        {
            transition.Add(current.Key, current.Value);
        }
        foreach (var next in nextKeys)
        {
            transition[next.Key] = next.Value;
        }

        return transition;
    }

    private static IReadOnlyDictionary<string, string> ValidateNextKeys(IReadOnlyList<MetadataPublicKey> keys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (key == null || !IsValidKeyId(key.KeyId) ||
                !string.Equals(key.Algorithm, "ed25519", StringComparison.OrdinalIgnoreCase) ||
                !result.TryAdd(key.KeyId, key.PublicKey))
            {
                throw Invalid("Signed release metadata contains an invalid next-key rotation set.");
            }

            try
            {
                var publicKey = PublicKey.Import(
                    SignatureAlgorithm.Ed25519,
                    Convert.FromBase64String(key.PublicKey),
                    KeyBlobFormat.RawPublicKey);
                if (!string.Equals(
                        Convert.ToBase64String(publicKey.Export(KeyBlobFormat.RawPublicKey)),
                        key.PublicKey,
                        StringComparison.Ordinal))
                {
                    throw new FormatException("The public key is not canonical base64.");
                }
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or CryptographicException)
            {
                throw Invalid("Signed release metadata contains an invalid next Ed25519 public key.", exception);
            }
        }

        return result;
    }

    private static void ValidatePayload(SignedReleaseIndex payload)
    {
        if (payload.Releases == null || payload.NextKeys == null || payload.Releases.Count == 0)
        {
            throw Invalid("Signed release metadata contains no releases.");
        }

        var tags = new HashSet<string>(StringComparer.Ordinal);
        var totalAssets = 0;
        foreach (var release in payload.Releases)
        {
            if (release == null || release.Assets == null ||
                string.IsNullOrWhiteSpace(release.Name) || release.PublishedAt == default ||
                !tags.Add(release.TagName) || release.Assets.Count is 0 or > 1_000)
            {
                throw Invalid("Signed release metadata contains an invalid or duplicate release.");
            }
            totalAssets = checked(totalAssets + release.Assets.Count);
            if (totalAssets > 10_000)
            {
                throw Invalid("Signed release metadata contains more than 10000 assets.");
            }

            try
            {
                _ = SemanticVersion.Parse(Installation.ReleaseAssetLocator.NormalizeVersion(release.TagName));
            }
            catch (FormatException exception)
            {
                throw Invalid($"Signed release tag '{release.TagName}' is invalid.", exception);
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in release.Assets)
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.Name) || Path.GetFileName(asset.Name) != asset.Name ||
                    !names.Add(asset.Name) || asset.Size <= 0 ||
                    !Installation.ChecksumManifest.IsSha256(asset.Sha256) ||
                    !string.Equals(asset.Sha256, asset.Sha256.ToLowerInvariant(), StringComparison.Ordinal) ||
                    !Uri.TryCreate(asset.Url, UriKind.RelativeOrAbsolute, out var uri) ||
                    uri.IsAbsoluteUri && (!string.IsNullOrEmpty(uri.UserInfo) || uri.Scheme is not ("https" or "file")))
                {
                    throw Invalid($"Signed release asset '{asset?.Name ?? "<null>"}' is invalid.");
                }
            }
        }
    }

    private static bool IsValidKeyId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static EidosupException Invalid(string message, Exception? inner = null) => new(
        EidosupErrorCode.InvalidReleaseMetadata,
        EidosupExitCodes.InvalidRelease,
        message,
        "Use current metadata from a trusted source; do not bypass expiration, rollback, or signature verification.",
        inner);
}

public sealed record MetadataTrustState(
    int Schema,
    long HighestVersion,
    IReadOnlyDictionary<string, string> Keys)
{
    public const int CurrentSchema = 1;
}

public sealed class MetadataTrustStore
{
    public const string OfficialKeyId = "eidos-official-2026-01";
    public const string OfficialPublicKey = "uI3NaPeqfaueCrteGQUAXDXc/FxNWqWaUVo3NrLLXiw=";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _stateDirectory;

    public MetadataTrustStore(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
    }

    public Task<InstallOperationLock> AcquireAsync(
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        var digest = SourceDigest(sourceIdentity);
        return InstallOperationLock.AcquireAsync(
            Path.Combine(_stateDirectory, "locks"),
            TimeSpan.FromSeconds(30),
            cancellationToken,
            operationName: $"metadata-{digest[..16]}");
    }

    public async Task<MetadataTrustState> ReadAsync(string sourceIdentity, CancellationToken cancellationToken)
    {
        var path = GetPath(sourceIdentity);
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var state = await JsonSerializer.DeserializeAsync<MetadataTrustState>(stream, JsonOptions, cancellationToken);
                return Validate(state) ? state! : throw Invalid(path);
            }
            catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or CryptographicException)
            {
                throw Invalid(path, exception);
            }
        }

        var keys = ParseBootstrapKeys(Environment.GetEnvironmentVariable("EIDOSUP_TRUSTED_ED25519_KEYS"));
        if (keys.Count == 0)
        {
            throw new EidosupException(
                EidosupErrorCode.InvalidReleaseMetadata,
                EidosupExitCodes.InvalidRelease,
                $"Signed source '{sourceIdentity}' has no pinned Ed25519 trust root.",
                "Set EIDOSUP_TRUSTED_ED25519_KEYS to key-id=base64-public-key entries for initial enrollment; verified rotations are persisted afterward.");
        }

        var initial = new MetadataTrustState(MetadataTrustState.CurrentSchema, 0, keys);
        return Validate(initial) ? initial : throw Invalid(path);
    }

    public async Task WriteAsync(
        string sourceIdentity,
        MetadataTrustState state,
        CancellationToken cancellationToken)
    {
        if (!Validate(state))
        {
            throw new ArgumentException("Metadata trust state is invalid.", nameof(state));
        }

        Directory.CreateDirectory(_stateDirectory);
        var path = GetPath(sourceIdentity);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string GetPath(string sourceIdentity)
    {
        var digest = SourceDigest(sourceIdentity);
        return Path.Combine(_stateDirectory, $"metadata-trust-{digest}.json");
    }

    private static string SourceDigest(string sourceIdentity) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sourceIdentity)))
            .ToLowerInvariant();

    private static IReadOnlyDictionary<string, string> ParseBootstrapKeys(string? value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OfficialKeyId] = OfficialPublicKey
        };
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0 || !result.TryAdd(item[..separator], item[(separator + 1)..]))
            {
                throw new FormatException("EIDOSUP_TRUSTED_ED25519_KEYS must use key-id=base64 entries separated by ';'.");
            }
        }

        return result;
    }

    private static bool Validate(MetadataTrustState? state)
    {
        if (state is not { Schema: MetadataTrustState.CurrentSchema, HighestVersion: >= 0 } ||
            state.Keys == null || state.Keys.Count == 0)
        {
            return false;
        }

        foreach (var key in state.Keys)
        {
            if (!IsValidKeyId(key.Key))
            {
                return false;
            }

            try
            {
                var publicKey = PublicKey.Import(
                    SignatureAlgorithm.Ed25519,
                    Convert.FromBase64String(key.Value),
                    KeyBlobFormat.RawPublicKey);
                if (!string.Equals(
                        Convert.ToBase64String(publicKey.Export(KeyBlobFormat.RawPublicKey)),
                        key.Value,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or CryptographicException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidKeyId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static EidosupException Invalid(string path, Exception? inner = null) => new(
        EidosupErrorCode.StateCorrupt,
        EidosupExitCodes.StateCorrupt,
        $"Metadata trust state '{path}' is invalid.",
        "Preserve the file for inspection and restore it from a trusted backup before using the source.",
        inner);
}
