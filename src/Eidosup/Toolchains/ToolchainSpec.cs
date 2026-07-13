using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosup.Toolchains;

public enum ToolchainSpecKind
{
    ExactVersion,
    Channel,
    Custom
}

public sealed record ToolchainSpec(
    string Canonical,
    ToolchainSpecKind Kind,
    string? Version,
    ReleaseChannel? Channel,
    string? HostRid)
{
    public static ToolchainSpec Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException("A toolchain specification must be 'stable', 'preview', or an exact Eidosc SemVer.");
        }

        string? hostRid = null;
        var hostSeparator = value.LastIndexOf('@');
        if (hostSeparator >= 0)
        {
            hostRid = value[(hostSeparator + 1)..];
            if (!PlatformContext.IsSupportedRid(hostRid))
            {
                throw new FormatException($"Toolchain host RID '{hostRid}' is unsupported.");
            }

            value = value[..hostSeparator];
        }

        if (string.Equals(value, "stable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "preview", StringComparison.OrdinalIgnoreCase))
        {
            var channel = ReleaseChannelParser.Parse(value);
            return new ToolchainSpec(
                AddHost(channel.ToString().ToLowerInvariant(), hostRid),
                ToolchainSpecKind.Channel,
                Version: null,
                channel,
                hostRid);
        }

        if (value.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
        {
            if (hostRid != null)
            {
                throw new FormatException("Custom toolchain selectors cannot specify a host RID.");
            }

            var name = value["custom:".Length..];
            if (!CustomToolchain.IsValidName(name))
            {
                throw new FormatException("A custom toolchain selector must use the form 'custom:<name>'.");
            }

            return new ToolchainSpec(
                CustomToolchain.GetSelector(name),
                ToolchainSpecKind.Custom,
                Version: null,
                Channel: null,
                HostRid: null);
        }

        var version = ReleaseAssetLocator.NormalizeVersion(value);
        return new ToolchainSpec(
            AddHost(version, hostRid),
            ToolchainSpecKind.ExactVersion,
            version,
            Channel: null,
            hostRid);
    }

    public ToolchainSelectorKind SelectorKind => Kind switch
    {
        ToolchainSpecKind.Channel => ToolchainSelectorKind.Channel,
        ToolchainSpecKind.Custom => ToolchainSelectorKind.Custom,
        _ => ToolchainSelectorKind.ExactVersion
    };

    private static string AddHost(string selector, string? hostRid) =>
        hostRid == null ? selector : $"{selector}@{hostRid}";
}
