namespace Eidosup.Installation;

public sealed class ReleaseAssetLocator
{
    public const string EidoscTagPrefix = "eidosc-v";

    public string GetEidoscBundleAssetName(string version, PlatformContext platform)
    {
        var normalizedVersion = NormalizeVersion(version);
        return $"eidosc-v{normalizedVersion}-{platform.Rid}.zip";
    }

    public GitHubReleaseAsset ResolveEidoscBundleAsset(GitHubReleaseInfo release, PlatformContext platform)
    {
        var expected = GetEidoscBundleAssetName(release.NormalizedVersion, platform);
        return release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, expected, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Release '{release.TagName}' does not contain asset '{expected}'.");
    }

    public static string NormalizeVersion(string version)
    {
        if (version.StartsWith(EidoscTagPrefix, StringComparison.Ordinal))
        {
            return version[EidoscTagPrefix.Length..];
        }

        return version.StartsWith('v') ? version[1..] : version;
    }
}
