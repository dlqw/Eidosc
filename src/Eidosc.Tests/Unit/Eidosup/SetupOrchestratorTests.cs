using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Proxies;
using Eidosup.Toolchains;

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
        var shimInstaller = new StubShimInstaller();
        var orchestrator = new SetupOrchestrator(
            _ => new StubReleaseSource(release),
            shimInstaller: shimInstaller);

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
        Assert.True(shimInstaller.Called);
        Assert.True(shimInstaller.DryRun);
    }

    [Fact]
    public async Task RunAsync_VerifiedInstallRegistersImmutableState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"eidosup-setup-state-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(root, "install");
        var downloadRoot = Path.Combine(root, "downloads");
        try
        {
            var platform = PlatformContext.Detect();
            var bundle = CreateBundle(platform.ExecutableName);
            var bundleSha256 = Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant();
            var bundleName = new ReleaseAssetLocator().GetEidoscBundleAssetName("0.4.0-alpha.2", platform);
            var checksum = Encoding.UTF8.GetBytes($"{bundleSha256}  {bundleName}\n");
            var release = new EidosReleaseInfo(
                "eidosc-v0.4.0-alpha.2",
                "Eidosc 0.4.0-alpha.2",
                Draft: false,
                PreRelease: true,
                DateTimeOffset.Parse("2026-07-12T00:00:00Z"),
                [
                    new EidosReleaseAsset(bundleName, "https://example.invalid/bundle.zip", bundle.Length),
                    new EidosReleaseAsset(ReleaseAssetLocator.ChecksumAssetName, "https://example.invalid/SHA256SUMS", checksum.Length)
                ]);
            using var handler = new AssetHandler(bundle, checksum);
            using var httpClient = new HttpClient(handler);
            using var downloadManager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
            var clock = DateTimeOffset.Parse("2026-07-12T00:00:00Z");
            var stateStore = new ToolchainStateStore(() => clock);
            var orchestrator = new SetupOrchestrator(
                _ => new StubReleaseSource(release),
                stateStore: stateStore,
                installerFactory: () => new VerifiedToolchainInstaller(downloadManager, clock: () => clock),
                shimInstaller: new StubShimInstaller());

            var exitCode = await orchestrator.RunAsync(
                new SetupOptions
                {
                    InstallRoot = installRoot,
                    DownloadRoot = downloadRoot,
                    SkipClang = true,
                    SkipEnvironmentConfiguration = true
                },
                CancellationToken.None);

            var layout = ToolInstallLayout.Create(platform, installRoot, downloadRoot);
            var state = await ToolchainStateStore.ReadAsync(layout, CancellationToken.None);
            var installed = Assert.Single(state.Toolchains);
            Assert.Equal("0.4.0-alpha.2", installed.Version);
            Assert.StartsWith($"eidosc-0.4.0-alpha.2-{platform.Rid}-", installed.Id, StringComparison.Ordinal);
            Assert.Contains(state.Selectors, selector =>
                selector.Selector == "preview" && selector.ToolchainId == installed.Id);
            Assert.Equal(installed.Id, state.Default?.ToolchainId);
            Assert.Equal("preview", state.Default?.Selector);
            Assert.True(File.Exists(Path.Combine(layout.GetToolchainDirectory(installed.Id), platform.ExecutableName)));
            Assert.Equal(0, exitCode);

            await stateStore.SetDefaultAsync(layout, selector: null, CancellationToken.None);
            var repeatedExitCode = await orchestrator.RunAsync(
                new SetupOptions
                {
                    InstallRoot = installRoot,
                    DownloadRoot = downloadRoot,
                    SkipClang = true,
                    SkipEnvironmentConfiguration = true
                },
                CancellationToken.None);
            var repeatedState = await ToolchainStateStore.ReadAsync(layout, CancellationToken.None);

            Assert.Equal(0, repeatedExitCode);
            Assert.True(repeatedState.DefaultConfigured);
            Assert.Null(repeatedState.Default);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] CreateBundle(string executableName)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, executableName, "binary");
            WriteEntry(archive, "runtime/runtime.h", "header");
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
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

    private sealed class AssetHandler(byte[] bundle, byte[] checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var payload = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.Ordinal)
                ? checksum
                : bundle;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private sealed class StubShimInstaller : IShimInstaller
    {
        public bool Called { get; private set; }

        public bool DryRun { get; private set; }

        public Task<ShimInstallResult> InstallAsync(
            ToolInstallLayout layout,
            bool dryRun,
            CancellationToken cancellationToken)
        {
            Called = true;
            DryRun = dryRun;
            var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            return Task.FromResult(new ShimInstallResult(
                Path.Combine(layout.BinDirectory, $"eidosup{extension}"),
                Path.Combine(layout.BinDirectory, $"eidosc{extension}"),
                ShimMaterialization.HardLink,
                Changed: true,
                DryRun: dryRun));
        }
    }
}
