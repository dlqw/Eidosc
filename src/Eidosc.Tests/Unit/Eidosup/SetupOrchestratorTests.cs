using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class SetupOrchestratorTests
{
    [Fact]
    public async Task RunAsync_DryRunDoesNotCreateInstallOrDownloadDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"eidosup-dry-run-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(root, "install");
        var downloadRoot = Path.Combine(root, "downloads");
        var platform = PlatformContext.Detect();
        var bundleName = new ReleaseAssetLocator().GetEidoscBundleAssetName("0.4.0-alpha.2", platform);
        var release = new EidosReleaseInfo(
            "eidosc-v0.4.0-alpha.2",
            "Eidosc 0.4.0-alpha.2",
            Draft: false,
            PreRelease: true,
            DateTimeOffset.Parse("2026-07-12T00:00:00Z"),
            [
                new EidosReleaseAsset(bundleName, "https://example.invalid/bundle.zip", 100),
                new EidosReleaseAsset(ReleaseAssetLocator.ChecksumAssetName, "https://example.invalid/SHA256SUMS", 100)
            ]);
        var orchestrator = new SetupOrchestrator(_ => new StubReleaseSource(release));

        var exitCode = await orchestrator.RunAsync(
            new SetupOptions
            {
                InstallRoot = installRoot,
                DownloadRoot = downloadRoot,
                SkipClang = true,
                SkipEnvironmentConfiguration = true,
                DryRun = true
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(root));
    }

    private sealed class StubReleaseSource(EidosReleaseInfo release) : IEidosReleaseSource
    {
        public Task<EidosReleaseInfo> ResolveReleaseAsync(
            string? version,
            ReleaseChannel channel,
            CancellationToken cancellationToken) => Task.FromResult(release);

        public void Dispose()
        {
        }
    }
}
