namespace Eidosup.Installation;

using Eidosup.Diagnostics;
using Eidosup.Distribution;

public sealed class ReleaseAssetLocator
{
    public const string EidoscTagPrefix = "eidosc-v";

    public string GetEidoscBundleAssetName(string version, PlatformContext platform)
    {
        var normalizedVersion = NormalizeVersion(version);
        return $"eidosc-v{normalizedVersion}-{platform.Rid}.zip";
    }

    public EidosReleaseAsset ResolveEidoscBundleAsset(EidosReleaseInfo release, PlatformContext platform)
    {
        var expected = GetEidoscBundleAssetName(release.NormalizedVersion, platform);
        return release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, expected, StringComparison.Ordinal))
            ?? throw new EidosupException(
                EidosupErrorCode.MissingReleaseAsset,
                EidosupExitCodes.MissingAsset,
                $"Release '{release.TagName}' does not contain asset '{expected}'.",
                "Select a release that publishes an Eidosc bundle for the detected host RID.");
    }

    public static string NormalizeVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var normalized = version;
        if (normalized.StartsWith(EidoscTagPrefix, StringComparison.Ordinal))
        {
            normalized = normalized[EidoscTagPrefix.Length..];
        }
        else if (normalized.StartsWith('v'))
        {
            normalized = normalized[1..];
        }

        var semanticVersion = SemanticVersion.Parse(normalized);
        return semanticVersion.ToString();
    }
}
