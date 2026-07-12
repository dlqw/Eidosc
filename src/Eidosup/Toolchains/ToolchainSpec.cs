using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosup.Toolchains;

public enum ToolchainSpecKind
{
    ExactVersion,
    Channel
}

public sealed record ToolchainSpec(
    string Canonical,
    ToolchainSpecKind Kind,
    string? Version,
    ReleaseChannel? Channel)
{
    public static ToolchainSpec Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException("A toolchain specification must be 'stable', 'preview', or an exact Eidosc SemVer.");
        }

        if (string.Equals(value, "stable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "preview", StringComparison.OrdinalIgnoreCase))
        {
            var channel = ReleaseChannelParser.Parse(value);
            return new ToolchainSpec(
                channel.ToString().ToLowerInvariant(),
                ToolchainSpecKind.Channel,
                Version: null,
                channel);
        }

        var version = ReleaseAssetLocator.NormalizeVersion(value);
        return new ToolchainSpec(
            version,
            ToolchainSpecKind.ExactVersion,
            version,
            Channel: null);
    }

    public ToolchainSelectorKind SelectorKind => Kind == ToolchainSpecKind.Channel
        ? ToolchainSelectorKind.Channel
        : ToolchainSelectorKind.ExactVersion;
}
