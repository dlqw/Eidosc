using System.Security.Cryptography;
using System.Text.Json;
using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosup.Toolchains;

public sealed record ToolchainIdentity(
    string Id,
    string IdentitySha256,
    string CompositionSha256)
{
    public const int CurrentSchema = 2;

    public static ToolchainIdentity Create(
        string version,
        string rid,
        string source,
        string releaseTag,
        string distributionManifestName,
        string distributionManifestSha256,
        IEnumerable<string> componentIds,
        ToolchainProfile profile = ToolchainProfile.Default,
        IEnumerable<string>? explicitComponents = null,
        IEnumerable<string>? explicitTargets = null)
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
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionManifestName);
        if (Path.GetFileName(distributionManifestName) != distributionManifestName)
        {
            throw new ArgumentException("Distribution manifest name must be a file name.", nameof(distributionManifestName));
        }

        if (!IsCanonicalSha256(distributionManifestSha256))
        {
            throw new ArgumentException(
                "Distribution manifest digest must be a lowercase SHA-256 value.",
                nameof(distributionManifestSha256));
        }

        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown toolchain profile.");
        }

        ArgumentNullException.ThrowIfNull(componentIds);
        var components = componentIds.Order(StringComparer.Ordinal).ToArray();
        if (components.Length == 0 ||
            components.Distinct(StringComparer.Ordinal).Count() != components.Length ||
            components.Any(static component => string.IsNullOrWhiteSpace(component)))
        {
            throw new ArgumentException("Toolchain composition must contain unique component IDs.", nameof(componentIds));
        }

        var explicitComponentIds = NormalizeSelection(explicitComponents, nameof(explicitComponents));
        if (explicitComponentIds.Any(component => !components.Contains(component, StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "Explicit components must be present in the selected component set.",
                nameof(explicitComponents));
        }

        var explicitTargetNames = NormalizeSelection(explicitTargets, nameof(explicitTargets));

        var compositionSha256 = HashCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("profile", profile.ToString().ToLowerInvariant());
            writer.WriteStartArray("components");
            foreach (var component in components)
            {
                writer.WriteStringValue(component);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("explicitComponents");
            foreach (var component in explicitComponentIds)
            {
                writer.WriteStringValue(component);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("explicitTargets");
            foreach (var target in explicitTargetNames)
            {
                writer.WriteStringValue(target);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        var identitySha256 = HashCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", CurrentSchema);
            writer.WriteString("version", version);
            writer.WriteString("rid", rid);
            writer.WriteString("source", source);
            writer.WriteString("releaseTag", releaseTag);
            writer.WriteString("distributionManifestName", distributionManifestName);
            writer.WriteString("distributionManifestSha256", distributionManifestSha256);
            writer.WriteString("compositionSha256", compositionSha256);
            writer.WriteEndObject();
        });
        return new ToolchainIdentity(
            $"eidosc-{version}-{rid}-{identitySha256}",
            identitySha256,
            compositionSha256);
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

    private static string HashCanonical(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            write(writer);
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))
            .ToLowerInvariant();
    }

    private static string[] NormalizeSelection(IEnumerable<string>? values, string parameterName)
    {
        var normalized = values?.Order(StringComparer.Ordinal).ToArray() ?? [];
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length ||
            normalized.Any(static value => string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException("Toolchain selections must contain unique non-empty identifiers.", parameterName);
        }

        return normalized;
    }

    private static bool IsCanonicalSha256(ReadOnlySpan<char> value) =>
        value.Length == 64 && value.IndexOfAnyExcept("0123456789abcdef") < 0;
}
