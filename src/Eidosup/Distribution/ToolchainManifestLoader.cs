using Eidosup.Diagnostics;
using Eidosup.Installation;

namespace Eidosup.Distribution;

public sealed record LoadedToolchainDistribution(
    ToolchainDistributionManifest Manifest,
    EidosReleaseAsset ManifestAsset,
    string ManifestSha256,
    ChecksumManifest Checksums,
    bool CacheHit,
    bool Resumed);

public interface IToolchainManifestLoader : IDisposable
{
    Task<LoadedToolchainDistribution> LoadAsync(
        EidosReleaseInfo release,
        EidosReleaseAsset manifestAsset,
        EidosReleaseAsset checksumAsset,
        PlatformContext platform,
        ToolInstallLayout layout,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class ToolchainManifestLoader : IToolchainManifestLoader
{
    private readonly DownloadManager _downloadManager;
    private readonly bool _ownsDownloadManager;

    public ToolchainManifestLoader(DownloadManager? downloadManager = null)
    {
        _downloadManager = downloadManager ?? new DownloadManager();
        _ownsDownloadManager = downloadManager == null;
    }

    public async Task<LoadedToolchainDistribution> LoadAsync(
        EidosReleaseInfo release,
        EidosReleaseAsset manifestAsset,
        EidosReleaseAsset checksumAsset,
        PlatformContext platform,
        ToolInstallLayout layout,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(manifestAsset);
        ArgumentNullException.ThrowIfNull(checksumAsset);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(layout);

        var checksumText = await _downloadManager.DownloadChecksumManifestAsync(checksumAsset, cancellationToken);
        var checksums = ChecksumManifest.Parse(checksumText);
        var expectedManifestSha256 = checksums.GetRequiredChecksum(manifestAsset.Name);
        EnsureSignedDigestMatches(manifestAsset, expectedManifestSha256);
        var download = await _downloadManager.DownloadArtifactAsync(
            manifestAsset,
            layout.CacheDirectory,
            expectedManifestSha256,
            cancellationToken,
            progress,
            ToolchainDistributionManifest.MaximumManifestBytes);
        var manifest = await ToolchainDistributionManifest.ReadAsync(
            download.Path,
            release.NormalizedVersion,
            platform.Rid,
            cancellationToken);
        ValidateArtifactBindings(release, manifest, checksums);
        return new LoadedToolchainDistribution(
            manifest,
            manifestAsset,
            expectedManifestSha256,
            checksums,
            download.CacheHit,
            download.Resumed);
    }

    public void Dispose()
    {
        if (_ownsDownloadManager)
        {
            _downloadManager.Dispose();
        }
    }

    private static void ValidateArtifactBindings(
        EidosReleaseInfo release,
        ToolchainDistributionManifest manifest,
        ChecksumManifest checksums)
    {
        var releaseAssets = new Dictionary<string, EidosReleaseAsset>(StringComparer.Ordinal);
        foreach (var asset in release.Assets)
        {
            if (asset == null || !releaseAssets.TryAdd(asset.Name, asset))
            {
                throw Invalid("Release metadata contains a null or duplicate asset name.");
            }
        }

        foreach (var artifact in manifest.Components.Select(static component => component.Artifact)
                     .DistinctBy(static artifact => artifact.Name, StringComparer.Ordinal))
        {
            if (!releaseAssets.TryGetValue(artifact.Name, out var releaseAsset) ||
                releaseAsset.Size != artifact.Size)
            {
                throw Invalid($"Component artifact '{artifact.Name}' is absent from the release or has a different size.");
            }

            var checksum = checksums.GetRequiredChecksum(artifact.Name);
            if (!string.Equals(checksum, artifact.Sha256, StringComparison.Ordinal))
            {
                throw Invalid($"Component artifact '{artifact.Name}' disagrees with SHA256SUMS.");
            }

            EnsureSignedDigestMatches(releaseAsset, artifact.Sha256);
        }
    }

    private static void EnsureSignedDigestMatches(EidosReleaseAsset asset, string expectedSha256)
    {
        if (asset.Sha256 != null && !string.Equals(asset.Sha256, expectedSha256, StringComparison.Ordinal))
        {
            throw Invalid($"Signed metadata and SHA256SUMS disagree for '{asset.Name}'.");
        }
    }

    private static EidosupException Invalid(string message) => new(
        EidosupErrorCode.InvalidReleaseMetadata,
        EidosupExitCodes.InvalidRelease,
        message,
        "Reject the release and publish matching signed asset identity, checksum, size, and component metadata.");
}
