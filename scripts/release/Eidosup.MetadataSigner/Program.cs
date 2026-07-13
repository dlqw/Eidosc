using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;

if (args.Length is >= 4 and <= 6 && args[0] == "verify")
{
    var indexPath = args[1];
    var trustedKeyId = args[2];
    var trustedPublicKey = args[3];
    var expectedVersion = args.Length >= 5 ? args[4] : null;
    var verifyAssetDirectory = args.Length == 6 ? Path.GetFullPath(args[5]) : null;
    var verifyOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };
    var verifyEnvelope = JsonSerializer.Deserialize<SignedReleaseIndexEnvelope>(
                       await File.ReadAllTextAsync(indexPath),
                       verifyOptions)
                   ?? throw new InvalidDataException("Signed index is empty.");
    var verifyPayload = verifyEnvelope.Payload
                        ?? throw new InvalidDataException("Signed index payload is missing.");
    var verifySignature = verifyEnvelope.Signatures?.SingleOrDefault(candidate =>
                        candidate.KeyId == trustedKeyId && candidate.Algorithm == "ed25519")
                    ?? throw new CryptographicException("Trusted signature is missing.");
    var publicKey = PublicKey.Import(
        SignatureAlgorithm.Ed25519,
        Convert.FromBase64String(trustedPublicKey),
        KeyBlobFormat.RawPublicKey);
    if (!SignatureAlgorithm.Ed25519.Verify(
            publicKey,
            JsonSerializer.SerializeToUtf8Bytes(verifyPayload, verifyOptions),
            Convert.FromBase64String(verifySignature.Signature)))
    {
        throw new CryptographicException("Signed index verification failed.");
    }

    if (expectedVersion != null)
    {
        ValidateExpectedRelease(verifyPayload, expectedVersion, verifyAssetDirectory);
    }

    Console.WriteLine($"Verified signed Eidosup index v{verifyPayload.Version}.");
    return 0;
}

if (args.Length != 7)
{
    Console.Error.WriteLine("usage: Eidosup.MetadataSigner <version> <asset-dir> <repository> <metadata-version> <key-id> <public-key-base64> <output>");
    Console.Error.WriteLine("       Eidosup.MetadataSigner verify <index> <key-id> <public-key-base64> [expected-version [asset-dir]]");
    return 2;
}

var version = args[0];
var assetDirectory = Path.GetFullPath(args[1]);
var repository = args[2];
if (!long.TryParse(args[3], out var metadataVersion) || metadataVersion < 1)
{
    throw new ArgumentException("metadata-version must be a positive integer.");
}

var keyId = args[4];
var publicKeyText = args[5];
var output = Path.GetFullPath(args[6]);
var privateKeyText = Environment.GetEnvironmentVariable("EIDOSUP_METADATA_ED25519_PRIVATE_KEY");
if (string.IsNullOrWhiteSpace(privateKeyText))
{
    throw new InvalidOperationException("EIDOSUP_METADATA_ED25519_PRIVATE_KEY is required.");
}

_ = SemanticVersion.Parse(version);
_ = GitHubRepository.Parse(repository);
var publicKeyBytes = Convert.FromBase64String(publicKeyText);
var declaredPublicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKeyBytes, KeyBlobFormat.RawPublicKey);
using var privateKey = Key.Import(
    SignatureAlgorithm.Ed25519,
    Convert.FromBase64String(privateKeyText),
    KeyBlobFormat.RawPrivateKey,
    new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None });
if (!CryptographicOperations.FixedTimeEquals(
        declaredPublicKey.Export(KeyBlobFormat.RawPublicKey),
        privateKey.PublicKey.Export(KeyBlobFormat.RawPublicKey)))
{
    throw new InvalidOperationException("The signing private key does not match the declared public key.");
}
var nextKeys = ParseNextKeys(
    Environment.GetEnvironmentVariable("EIDOSUP_METADATA_NEXT_ED25519_KEYS"),
    keyId,
    publicKeyText);

var tag = $"eidosc-v{version}";
var generatedAt = DateTimeOffset.UtcNow;
var assets = Directory.EnumerateFiles(assetDirectory)
    .Select(path => new FileInfo(path))
    .Where(file => file.Name != "eidosc-release.json" && file.Name != "eidosup-index.json")
    .OrderBy(static file => file.Name, StringComparer.Ordinal)
    .Select(file => new SignedReleaseAsset(
        file.Name,
        $"https://github.com/{repository}/releases/download/{tag}/{Uri.EscapeDataString(file.Name)}",
        file.Length,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))).ToLowerInvariant()))
    .ToArray();
if (!assets.Any(static asset => asset.Name == "SHA256SUMS") ||
    assets.Count(static asset => asset.Name.StartsWith("eidosc-v", StringComparison.Ordinal) && asset.Name.EndsWith(".zip", StringComparison.Ordinal)) != 6 ||
    assets.Count(static asset => asset.Name.StartsWith("eidos-toolchain-v", StringComparison.Ordinal) && asset.Name.EndsWith(".json", StringComparison.Ordinal)) != 6)
{
    throw new InvalidOperationException("The signed index requires SHA256SUMS, six Eidosc host bundles, and six host component manifests.");
}

var payload = new SignedReleaseIndex(
    1,
    metadataVersion,
    generatedAt,
    generatedAt.AddDays(45),
    nextKeys,
    [new SignedReleaseEntry(tag, tag, version.Contains('-'), generatedAt, assets)]);
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false
};
var canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions);
var signature = SignatureAlgorithm.Ed25519.Sign(privateKey, canonicalPayload);
var envelope = new SignedReleaseIndexEnvelope(
    payload,
    [new MetadataSignature(keyId, "ed25519", Convert.ToBase64String(signature))]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(envelope, new JsonSerializerOptions(jsonOptions)
{
    WriteIndented = true
}) + "\n");
Console.WriteLine($"Wrote signed Eidosup distribution index v{metadataVersion} to {output}.");
return 0;

static IReadOnlyList<MetadataPublicKey> ParseNextKeys(
    string? configured,
    string signingKeyId,
    string signingPublicKey)
{
    if (string.IsNullOrWhiteSpace(configured))
    {
        configured = $"{signingKeyId}={signingPublicKey}";
    }

    var keys = new List<MetadataPublicKey>();
    var identities = new HashSet<string>(StringComparer.Ordinal);
    foreach (var item in configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var separator = item.IndexOf('=');
        var keyId = separator > 0 ? item[..separator] : string.Empty;
        var encodedKey = separator > 0 ? item[(separator + 1)..] : string.Empty;
        if (!IsValidKeyId(keyId) || !identities.Add(keyId))
        {
            throw new FormatException("EIDOSUP_METADATA_NEXT_ED25519_KEYS contains an invalid or duplicate key ID.");
        }

        var publicKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            Convert.FromBase64String(encodedKey),
            KeyBlobFormat.RawPublicKey);
        if (!string.Equals(
                Convert.ToBase64String(publicKey.Export(KeyBlobFormat.RawPublicKey)),
                encodedKey,
                StringComparison.Ordinal))
        {
            throw new FormatException("EIDOSUP_METADATA_NEXT_ED25519_KEYS must use canonical raw-public-key base64.");
        }

        keys.Add(new MetadataPublicKey(keyId, "ed25519", encodedKey));
    }

    if (keys.Count == 0)
    {
        throw new FormatException("EIDOSUP_METADATA_NEXT_ED25519_KEYS must contain at least one key.");
    }

    return keys;
}

static bool IsValidKeyId(string value) =>
    value.Length is > 0 and <= 64 &&
    value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

static void ValidateExpectedRelease(
    SignedReleaseIndex payload,
    string expectedVersion,
    string? assetDirectory)
{
    _ = SemanticVersion.Parse(expectedVersion);
    var expectedTag = $"eidosc-v{expectedVersion}";
    var release = payload.Releases?.SingleOrDefault()
                  ?? throw new InvalidDataException("The signed index must contain exactly one release entry.");
    if (!string.Equals(release.TagName, expectedTag, StringComparison.Ordinal) ||
        !string.Equals(release.Name, expectedTag, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"The signed index does not describe '{expectedTag}'.");
    }

    if (assetDirectory == null)
    {
        return;
    }
    if (!Directory.Exists(assetDirectory))
    {
        throw new DirectoryNotFoundException($"Release asset directory '{assetDirectory}' does not exist.");
    }

    var files = Directory.EnumerateFiles(assetDirectory)
        .Select(path => new FileInfo(path))
        .Where(static file => file.Name is not "eidosc-release.json" and not "eidosup-index.json")
        .OrderBy(static file => file.Name, StringComparer.Ordinal)
        .ToArray();
    if (release.Assets == null || release.Assets.Count != files.Length)
    {
        throw new InvalidDataException("The signed index asset count does not match the release directory.");
    }

    var signedAssets = release.Assets.ToDictionary(static asset => asset.Name, StringComparer.Ordinal);
    foreach (var file in files)
    {
        if (!signedAssets.TryGetValue(file.Name, out var signed) ||
            signed.Size != file.Length ||
            !string.Equals(
                signed.Sha256,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))).ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Signed release asset '{file.Name}' does not match the release directory.");
        }
    }
}

internal sealed record SignedReleaseIndexEnvelope(SignedReleaseIndex Payload, IReadOnlyList<MetadataSignature> Signatures);
internal sealed record SignedReleaseIndex(int Schema, long Version, DateTimeOffset GeneratedAt, DateTimeOffset ExpiresAt, IReadOnlyList<MetadataPublicKey> NextKeys, IReadOnlyList<SignedReleaseEntry> Releases);
internal sealed record SignedReleaseEntry(string TagName, string Name, bool PreRelease, DateTimeOffset PublishedAt, IReadOnlyList<SignedReleaseAsset> Assets);
internal sealed record SignedReleaseAsset(string Name, string Url, long Size, string Sha256);
internal sealed record MetadataSignature(string KeyId, string Algorithm, string Signature);
internal sealed record MetadataPublicKey(string KeyId, string Algorithm, string PublicKey);

internal sealed record GitHubRepository(string Owner, string Name)
{
    public static GitHubRepository Parse(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || parts.Any(static part => string.IsNullOrWhiteSpace(part) || part.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
        {
            throw new FormatException("repository must use owner/name.");
        }

        return new GitHubRepository(parts[0], parts[1]);
    }
}

internal sealed record SemanticVersion
{
    public static SemanticVersion Parse(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$"))
        {
            throw new FormatException("version must be SemVer 2.0.0.");
        }

        return new SemanticVersion();
    }
}
