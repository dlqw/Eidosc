using System.Security.Cryptography;
using System.Text.Json;
using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosup.Toolchains;

public sealed record ToolchainIdentity(string Id, string ManifestSha256)
{
    public const int CurrentSchema = 1;

    public static ToolchainIdentity Create(
        string version,
        string rid,
        string source,
        string releaseTag,
        string assetName,
        string assetSha256,
        long assetSize)
    {
        var semanticVersion = SemanticVersion.Parse(version);
        if (!string.Equals(semanticVersion.ToString(), version, StringComparison.Ordinal))
        {
            throw new ArgumentException("Toolchain version must use canonical SemVer spelling.", nameof(version));
        }

        if (!PlatformContext.IsSupportedRid(rid))
        {
            throw new ArgumentException($"Unsupported toolchain RID '{rid}'.", nameof(rid));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        if (!IsCanonicalSha256(assetSha256))
        {
            throw new ArgumentException("Asset digest must be a lowercase SHA-256 value.", nameof(assetSha256));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(assetSize);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", CurrentSchema);
            writer.WriteString("version", version);
            writer.WriteString("rid", rid);
            writer.WriteString("source", source);
            writer.WriteString("releaseTag", releaseTag);
            writer.WriteString("assetName", assetName);
            writer.WriteString("assetSha256", assetSha256);
            writer.WriteNumber("assetSize", assetSize);
            writer.WriteEndObject();
        }

        var digest = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))
            .ToLowerInvariant();
        return new ToolchainIdentity($"eidosc-{version}-{rid}-{digest}", digest);
    }

    public static bool IsValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("eidosc-", StringComparison.Ordinal))
        {
            return false;
        }

        var digestSeparator = value.LastIndexOf('-');
        if (digestSeparator <= "eidosc-".Length ||
            !IsCanonicalSha256(value.AsSpan(digestSeparator + 1)))
        {
            return false;
        }

        var prefix = value["eidosc-".Length..digestSeparator];
        foreach (var rid in PlatformContext.SupportedRids)
        {
            var ridSuffix = $"-{rid}";
            if (!prefix.EndsWith(ridSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var version = prefix[..^ridSuffix.Length];
            return SemanticVersion.TryParse(version, out var parsed) &&
                   parsed != null &&
                   string.Equals(parsed.ToString(), version, StringComparison.Ordinal);
        }

        return false;
    }

    public static bool IsCanonicalSha256(string? value) =>
        value != null && IsCanonicalSha256(value.AsSpan());

    private static bool IsCanonicalSha256(ReadOnlySpan<char> value) =>
        value.Length == 64 && value.IndexOfAnyExcept("0123456789abcdef") < 0;
}
