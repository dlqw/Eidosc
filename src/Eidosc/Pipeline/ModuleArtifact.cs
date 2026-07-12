using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eidosc.Pipeline;

public sealed record ModuleArtifactKey
{
    public string CacheSchema { get; init; } = "module-artifact-v2";
    public string CompilerBuildId { get; init; } = "";
    public string ModuleKey { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public string LanguageVersion { get; init; } = "";
    public string DependencySignatureHash { get; init; } = "";
    public string TargetTriple { get; init; } = "";
    public string FlagsHash { get; init; } = "";

    public string StableHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, CacheSchema);
        Append(hash, CompilerBuildId);
        Append(hash, ModuleKey);
        Append(hash, SourceHash);
        Append(hash, LanguageVersion);
        Append(hash, DependencySignatureHash);
        Append(hash, TargetTriple);
        Append(hash, FlagsHash);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }
}

public sealed record ModuleArtifactManifest
{
    public const string CurrentSchemaVersion = "2";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ModuleArtifactKey Key { get; init; } = new();
    public string Kind { get; init; } = "";
    public string PayloadPath { get; init; } = "";
    public long PayloadLength { get; init; }
    public string PayloadHash { get; init; } = "";
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}

public static class ModuleArtifactHash
{
    private static readonly JsonSerializerOptions JsonHashSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        // Payload graphs (HIR node trees, type shapes) can nest deeper than the
        // default 64-level limit on stdlib runtime/concurrency fixtures, which would
        // overflow the stack mid-serialization and surface as an E0001 internal error.
        // IgnoreCycles additionally guards against any accidental back-reference.
        MaxDepth = int.MaxValue,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    public static string ComputeSourceHash(string sourceText) =>
        ComputeTextHash(sourceText);

    public static string ComputeDependencySignatureHash(IEnumerable<string> dependencySignatureHashes) =>
        ComputeTextHash(string.Join('\n', dependencySignatureHashes.OrderBy(static value => value, StringComparer.Ordinal)));

    public static string ComputeFlagsHash(IEnumerable<string> flags) =>
        ComputeTextHash(string.Join('\n', flags.OrderBy(static value => value, StringComparer.Ordinal)));

    public static string ComputeTextHash(string text)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount <= 4096)
        {
            Span<byte> buffer = stackalloc byte[byteCount];
            var written = Encoding.UTF8.GetBytes(text.AsSpan(), buffer);
            return Convert.ToHexString(SHA256.HashData(buffer[..written])).ToLowerInvariant();
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(text.AsSpan(), rented);
            return Convert.ToHexString(SHA256.HashData(rented.AsSpan(0, written))).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public static string ComputeJsonHash<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonHashSerializerOptions);
        return Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();
    }
}
