using Eidosup.Diagnostics;
using Eidosup.Installation;

namespace Eidosup.Distribution;

public static class ReleaseSelector
{
    public static EidosReleaseInfo Select(
        IReadOnlyList<EidosReleaseInfo> releases,
        ReleaseChannel channel,
        string source)
    {
        var candidates = releases
            .Where(static release => !release.Draft)
            .Select(TryCreateCandidate)
            .Where(static candidate => candidate != null)
            .Select(static candidate => candidate!)
            .Where(candidate => channel == ReleaseChannel.Preview || !candidate.Version.IsPreRelease)
            .OrderByDescending(static candidate => candidate.Version)
            .ThenByDescending(static candidate => candidate.Release.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenBy(static candidate => candidate.Release.TagName, StringComparer.Ordinal)
            .ToArray();

        return candidates.FirstOrDefault()?.Release ?? throw new EidosupException(
            EidosupErrorCode.NoMatchingRelease,
            EidosupExitCodes.ReleaseNotFound,
            $"Release source '{source}' has no published Eidosc release for channel '{channel.ToString().ToLowerInvariant()}'.",
            "Publish a release tagged eidosc-v<SemVer> or select another source/version.");
    }

    public static EidosReleaseInfo ValidateExact(
        EidosReleaseInfo release,
        SemanticVersion expectedVersion,
        string source)
    {
        var candidate = TryCreateCandidate(release);
        if (release.Draft || candidate == null || !candidate.Version.Equals(expectedVersion))
        {
            throw new EidosupException(
                EidosupErrorCode.InvalidReleaseMetadata,
                EidosupExitCodes.InvalidRelease,
                $"Release metadata for '{release.TagName}' from source '{source}' does not match the requested version.",
                "Use a published release with an exact eidosc-v<SemVer> tag and consistent prerelease metadata.");
        }

        return release;
    }

    private static ReleaseCandidate? TryCreateCandidate(EidosReleaseInfo release)
    {
        if (!release.TagName.StartsWith(ReleaseAssetLocator.EidoscTagPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var versionText = release.TagName[ReleaseAssetLocator.EidoscTagPrefix.Length..];
        if (!SemanticVersion.TryParse(versionText, out var version) || version == null || version.IsPreRelease != release.PreRelease)
        {
            return null;
        }

        return new ReleaseCandidate(release, version);
    }

    private sealed record ReleaseCandidate(EidosReleaseInfo Release, SemanticVersion Version);
}
