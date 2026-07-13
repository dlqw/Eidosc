using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Eidosup.Configuration;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Proxies;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ToolchainManagerTests
{
    private const string FirstHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string SecondHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task UninstallAsync_RefusesActiveDefaultThenRemovesInactiveToolchainAndSelectors()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var second = await fixture.CreateToolchainAsync("0.4.0-alpha.3", SecondHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.RegisterInstallAsync(fixture.Layout, second.Directory, requestedChannel: null, CancellationToken.None);
        var manager = new ToolchainManager(stateStore: store, clock: () => EidosupToolchainTestFixture.FixedTime);

        var activeException = await Assert.ThrowsAsync<EidosupException>(() => manager.UninstallAsync(
            fixture.Options,
            [ToolchainSpec.Parse("preview")],
            dryRun: false,
            CancellationToken.None));
        await manager.SetDefaultAsync(
            fixture.Options,
            ToolchainSpec.Parse("preview"),
            dryRun: false,
            CancellationToken.None);
        var removed = await manager.UninstallAsync(
            fixture.Options,
            [ToolchainSpec.Parse("0.4.0-alpha.3")],
            dryRun: false,
            CancellationToken.None);
        var state = await ToolchainStateStore.ReadVerifiedAsync(fixture.Layout, CancellationToken.None);

        Assert.Equal(EidosupErrorCode.InstallConflict, activeException.Code);
        Assert.Equal(second.Manifest.ToolchainId, Assert.Single(removed.ToolchainIds));
        Assert.False(Directory.Exists(second.Directory));
        Assert.DoesNotContain(state.Toolchains, toolchain => toolchain.Id == second.Manifest.ToolchainId);
        Assert.DoesNotContain(state.Selectors, selector => selector.ToolchainId == second.Manifest.ToolchainId);
        Assert.Equal(first.Manifest.ToolchainId, state.Default?.ToolchainId);
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.TransactionDirectory, "uninstall-*.json"));
    }

    [Fact]
    public async Task UninstallAsync_DryRunDoesNotMoveFilesOrChangeState()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var second = await fixture.CreateToolchainAsync("0.4.0-alpha.3", SecondHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.RegisterInstallAsync(fixture.Layout, second.Directory, requestedChannel: null, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(Path.Combine(fixture.Layout.StateDirectory, ToolchainStateStore.FileName));
        var manager = new ToolchainManager(stateStore: store);

        var result = await manager.UninstallAsync(
            fixture.Options,
            [ToolchainSpec.Parse("0.4.0-alpha.3")],
            dryRun: true,
            CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.True(Directory.Exists(second.Directory));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(fixture.Layout.StateDirectory, ToolchainStateStore.FileName)));
        Assert.False(Directory.Exists(fixture.Layout.TransactionDirectory));
    }

    [Fact]
    public async Task UninstallAsync_FaultAfterMoveRestoresToolchainAndState()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var second = await fixture.CreateToolchainAsync("0.4.0-alpha.3", SecondHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.RegisterInstallAsync(fixture.Layout, second.Directory, requestedChannel: null, CancellationToken.None);
        var manager = new ToolchainManager(
            stateStore: store,
            uninstallFaultInjector: new ThrowingUninstallFaultInjector(ToolchainUninstallCheckpoint.TargetsMoved));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UninstallAsync(
            fixture.Options,
            [ToolchainSpec.Parse("0.4.0-alpha.3")],
            dryRun: false,
            CancellationToken.None));
        var state = await ToolchainStateStore.ReadVerifiedAsync(fixture.Layout, CancellationToken.None);

        Assert.True(Directory.Exists(second.Directory));
        Assert.Contains(state.Toolchains, toolchain => toolchain.Id == second.Manifest.ToolchainId);
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.TransactionDirectory, "uninstall-*.json"));
    }

    [Fact]
    public async Task UninstallAsync_FaultAfterStateCommitFinishesCommittedCleanup()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var second = await fixture.CreateToolchainAsync("0.4.0-alpha.3", SecondHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.RegisterInstallAsync(fixture.Layout, second.Directory, requestedChannel: null, CancellationToken.None);
        var manager = new ToolchainManager(
            stateStore: store,
            uninstallFaultInjector: new ThrowingUninstallFaultInjector(ToolchainUninstallCheckpoint.StateCommitted));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UninstallAsync(
            fixture.Options,
            [ToolchainSpec.Parse("0.4.0-alpha.3")],
            dryRun: false,
            CancellationToken.None));
        var state = await ToolchainStateStore.ReadVerifiedAsync(fixture.Layout, CancellationToken.None);

        Assert.False(Directory.Exists(second.Directory));
        Assert.DoesNotContain(state.Toolchains, toolchain => toolchain.Id == second.Manifest.ToolchainId);
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.TransactionDirectory, "uninstall-*.json"));
    }

    [Fact]
    public async Task RunAsync_UsesExplicitSelectorAndReturnsChildExitCode()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var toolchain = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, toolchain.Directory, ReleaseChannel.Preview, CancellationToken.None);
        var runner = new RecordingRunner(37);
        var manager = new ToolchainManager(stateStore: store, processRunner: runner);

        var exitCode = await manager.RunAsync(
            fixture.Options,
            ToolchainSpec.Parse("0.4.0-alpha.2"),
            "eidosc",
            ["build", "project with spaces"],
            CancellationToken.None);

        Assert.Equal(37, exitCode);
        Assert.Equal(toolchain.Manifest.ToolchainId, runner.Toolchain?.ToolchainId);
        Assert.Equal(ToolchainSelectionSource.Explicit, runner.Toolchain?.SelectionSource);
        Assert.Equal(new[] { "build", "project with spaces" }, runner.Arguments);
    }

    [Fact]
    public async Task CheckAsync_DistinguishesMissingCurrentAndAvailableUpdate()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        var latest = CreateRelease("0.4.0-alpha.3", bundleSize: 123);
        var exact = CreateRelease("0.4.0-alpha.4", bundleSize: 123);
        var manager = new ToolchainManager(
            releaseSourceFactory: _ => new MappingReleaseSource(latest, exact),
            stateStore: store);

        var results = await manager.CheckAsync(
            fixture.Options,
            [ToolchainSpec.Parse("preview"), ToolchainSpec.Parse("0.4.0-alpha.4")],
            CancellationToken.None);

        Assert.Equal(ToolchainCheckStatus.UpdateAvailable, results[0].Status);
        Assert.Equal("0.4.0-alpha.2", results[0].InstalledVersion);
        Assert.Equal("0.4.0-alpha.3", results[0].AvailableVersion);
        Assert.Equal(ToolchainCheckStatus.Missing, results[1].Status);
    }

    [Fact]
    public async Task InstallAsync_ConcurrentExactVersionsRemainVerifiedAndCoexist()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var platform = PlatformContext.Detect();
        var firstAsset = CreateReleaseAssetSet("0.4.0-alpha.2", platform);
        var secondAsset = CreateReleaseAssetSet("0.4.0-alpha.3", platform);
        using var handler = new MappingAssetHandler(firstAsset, secondAsset);
        using var httpClient = new HttpClient(handler);
        using var downloadManager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        var manager = new ToolchainManager(
            releaseSourceFactory: _ => new MappingReleaseSource(firstAsset.Release, secondAsset.Release),
            stateStore: new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime),
            manifestLoaderFactory: () => CreateManifestLoader(firstAsset, secondAsset),
            installerFactory: () => new VerifiedToolchainInstaller(
                downloadManager,
                clock: () => EidosupToolchainTestFixture.FixedTime));

        await Task.WhenAll(
            manager.InstallAsync(
                fixture.Options,
                ToolchainSpec.Parse("0.4.0-alpha.2"),
                force: false,
                dryRun: false,
                progress: null,
                CancellationToken.None),
            manager.InstallAsync(
                fixture.Options,
                ToolchainSpec.Parse("0.4.0-alpha.3"),
                force: false,
                dryRun: false,
                progress: null,
                CancellationToken.None));
        var state = await ToolchainStateStore.ReadVerifiedAsync(fixture.Layout, CancellationToken.None);

        Assert.Equal(2, state.Toolchains.Count);
        Assert.Contains(state.Toolchains, toolchain => toolchain.Version == "0.4.0-alpha.2");
        Assert.Contains(state.Toolchains, toolchain => toolchain.Version == "0.4.0-alpha.3");
        Assert.Contains(state.Selectors, selector => selector.Selector == "0.4.0-alpha.2");
        Assert.Contains(state.Selectors, selector => selector.Selector == "0.4.0-alpha.3");
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.TransactionDirectory, "*.json"));
    }

    [Fact]
    public async Task InstallAsync_ExactSelectorRefusesDifferentManifestIdentityAndCleansNewInstall()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var platform = PlatformContext.Detect();
        var firstAsset = CreateReleaseAssetSet("0.4.0-alpha.2", platform, "source-a", "binary-a");
        var secondAsset = CreateReleaseAssetSet("0.4.0-alpha.2", platform, "source-b", "binary-b");
        using var handler = new MappingAssetHandler(firstAsset, secondAsset);
        using var httpClient = new HttpClient(handler);
        using var downloadManager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        var currentRelease = firstAsset.Release;
        var manager = new ToolchainManager(
            releaseSourceFactory: _ => new SingleReleaseSource(currentRelease),
            stateStore: new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime),
            manifestLoaderFactory: () => CreateManifestLoader(firstAsset, secondAsset),
            installerFactory: () => new VerifiedToolchainInstaller(
                downloadManager,
                clock: () => EidosupToolchainTestFixture.FixedTime));
        await manager.InstallAsync(
            fixture.Options,
            ToolchainSpec.Parse("0.4.0-alpha.2"),
            force: false,
            dryRun: false,
            progress: null,
            CancellationToken.None);
        currentRelease = secondAsset.Release;

        var exception = await Assert.ThrowsAsync<EidosupException>(() => manager.InstallAsync(
            fixture.Options,
            ToolchainSpec.Parse("0.4.0-alpha.2"),
            force: false,
            dryRun: false,
            progress: null,
            CancellationToken.None));
        var state = await ToolchainStateStore.ReadVerifiedAsync(fixture.Layout, CancellationToken.None);

        Assert.Equal(EidosupErrorCode.InstallConflict, exception.Code);
        var installed = Assert.Single(state.Toolchains);
        Assert.Equal("test/source", installed.Source);
        Assert.Single(
            Directory.EnumerateDirectories(fixture.Layout.ToolchainsDirectory),
            path => Path.GetFileName(path) is not ".staging" and not ".backup");
    }

    [Fact]
    public async Task CompositionOperations_CreateImmutableVariantsAndPreserveExplicitProjectRequirements()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var platform = PlatformContext.Detect();
        var asset = CreateReleaseAssetSet("0.4.0-alpha.2", platform);
        using var handler = new MappingAssetHandler(asset);
        using var httpClient = new HttpClient(handler);
        using var downloadManager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        var manager = new ToolchainManager(
            releaseSourceFactory: _ => new SingleReleaseSource(asset.Release),
            stateStore: new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime),
            manifestLoaderFactory: () => CreateManifestLoader(asset),
            installerFactory: () => new VerifiedToolchainInstaller(
                downloadManager,
                clock: () => EidosupToolchainTestFixture.FixedTime));
        var spec = ToolchainSpec.Parse("0.4.0-alpha.2");
        var initial = await manager.InstallAsync(
            fixture.Options,
            spec,
            force: false,
            dryRun: false,
            progress: null,
            CancellationToken.None);
        var initialId = initial.Install!.ToolchainId;
        var initialManifestBytes = await File.ReadAllBytesAsync(
            Path.Combine(initial.Install.ToolchainDirectory, InstallManifest.FileName));

        var withDocs = await manager.AddCompositionAsync(
            fixture.Options,
            spec,
            ["eidos-docs"],
            [],
            dryRun: false,
            progress: null,
            CancellationToken.None);

        Assert.NotEqual(initialId, withDocs.Install!.ToolchainId);
        Assert.True(File.Exists(Path.Combine(withDocs.Install.ToolchainDirectory, "docs", "index.json")));
        Assert.False(Directory.Exists(Path.Combine(initial.Install.ToolchainDirectory, "docs")));
        Assert.Equal(
            initialManifestBytes,
            await File.ReadAllBytesAsync(Path.Combine(initial.Install.ToolchainDirectory, InstallManifest.FileName)));

        var minimalWithDocs = await manager.SetProfileAsync(
            fixture.Options,
            spec,
            ToolchainProfile.Minimal,
            dryRun: false,
            progress: null,
            CancellationToken.None);
        Assert.Contains("eidos-docs", minimalWithDocs.Plan.ExplicitComponents);
        Assert.DoesNotContain($"eidos-runtime@{platform.Rid}", minimalWithDocs.Plan.ComponentIds);

        var complete = await manager.SetProfileAsync(
            fixture.Options,
            spec,
            ToolchainProfile.Complete,
            dryRun: false,
            progress: null,
            CancellationToken.None);
        Assert.Contains("eidos-bindgen", complete.Plan.ComponentIds);
        await Assert.ThrowsAsync<EidosupException>(() => manager.RemoveCompositionAsync(
            fixture.Options,
            spec,
            ["eidos-docs"],
            [],
            dryRun: false,
            progress: null,
            CancellationToken.None));

        await manager.SetProfileAsync(
            fixture.Options,
            spec,
            ToolchainProfile.Minimal,
            dryRun: false,
            progress: null,
            CancellationToken.None);
        var withoutDocs = await manager.RemoveCompositionAsync(
            fixture.Options,
            spec,
            ["eidos-docs"],
            [],
            dryRun: false,
            progress: null,
            CancellationToken.None);
        Assert.DoesNotContain("eidos-docs", withoutDocs.Plan.ComponentIds);

        var withTarget = await manager.AddCompositionAsync(
            fixture.Options,
            spec,
            [],
            [platform.Rid],
            dryRun: false,
            progress: null,
            CancellationToken.None);
        Assert.Contains(platform.Rid, withTarget.Plan.ExplicitTargets);
        var withoutTarget = await manager.RemoveCompositionAsync(
            fixture.Options,
            spec,
            [],
            [platform.Rid],
            dryRun: false,
            progress: null,
            CancellationToken.None);
        Assert.DoesNotContain($"eidos-runtime@{platform.Rid}", withoutTarget.Plan.ComponentIds);

        await manager.AddCompositionAsync(
            fixture.Options,
            spec,
            ["eidos-bindgen"],
            [],
            dryRun: false,
            progress: null,
            CancellationToken.None);
        var synchronized = await manager.SyncProjectConfigurationAsync(
            fixture.Options,
            new ProjectToolchainConfiguration(
                Path.Combine(fixture.Root, ProjectToolchainConfigurationReader.FileName),
                spec,
                "default",
                ["eidos-docs"],
                []),
            dryRun: false,
            progress: null,
            CancellationToken.None);
        var updated = Assert.Single(await manager.UpdateAsync(
            fixture.Options,
            [spec],
            dryRun: false,
            progress: null,
            CancellationToken.None));
        var state = await ToolchainStateStore.ReadVerifiedAsync(fixture.Layout, CancellationToken.None);
        var selector = Assert.Single(state.Selectors, candidate => candidate.Selector == spec.Canonical);

        Assert.Equal(ToolchainProfile.Default, synchronized.Plan.Profile);
        Assert.Contains("eidos-bindgen", synchronized.Plan.ExplicitComponents);
        Assert.Contains("eidos-docs", synchronized.Plan.ExplicitComponents);
        Assert.Equal(synchronized.Install!.ToolchainId, selector.ToolchainId);
        Assert.Equal(synchronized.Install.ToolchainId, updated.Install!.ToolchainId);
        Assert.Contains("eidos-bindgen", updated.Plan.ExplicitComponents);
        Assert.Contains("eidos-docs", updated.Plan.ExplicitComponents);
        Assert.Contains(state.Toolchains, toolchain => toolchain.Id == initialId);
        Assert.True(Directory.Exists(initial.Install.ToolchainDirectory));

        await manager.SetDefaultAsync(
            fixture.Options,
            spec: null,
            dryRun: false,
            CancellationToken.None);
        var uninstall = await manager.UninstallAsync(
            fixture.Options,
            [spec],
            dryRun: false,
            CancellationToken.None);
        var empty = await ToolchainStateStore.ReadVerifiedAsync(fixture.Layout, CancellationToken.None);
        Assert.Equal(state.Toolchains.Count, uninstall.ToolchainIds.Count);
        Assert.Empty(empty.Toolchains);
    }

    [Fact]
    public async Task RollbackAsync_SkipsCompositionVariantsAndReturnsToPreviousRelease()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var platform = PlatformContext.Detect();
        var first = CreateReleaseAssetSet("0.4.0-alpha.2", platform);
        var second = CreateReleaseAssetSet("0.4.0-alpha.3", platform);
        using var handler = new MappingAssetHandler(first, second);
        using var httpClient = new HttpClient(handler);
        using var downloadManager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        var currentRelease = first.Release;
        var manager = new ToolchainManager(
            releaseSourceFactory: _ => new SingleReleaseSource(currentRelease),
            stateStore: new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime),
            manifestLoaderFactory: () => CreateManifestLoader(first, second),
            installerFactory: () => new VerifiedToolchainInstaller(
                downloadManager,
                clock: () => EidosupToolchainTestFixture.FixedTime));
        var preview = ToolchainSpec.Parse("preview");
        var initial = await manager.InstallAsync(
            fixture.Options,
            preview,
            force: false,
            dryRun: false,
            progress: null,
            CancellationToken.None);
        currentRelease = second.Release;
        var updated = Assert.Single(await manager.UpdateAsync(
            fixture.Options,
            [preview],
            dryRun: false,
            progress: null,
            CancellationToken.None));
        var composition = await manager.AddCompositionAsync(
            fixture.Options,
            preview,
            ["eidos-docs"],
            [],
            dryRun: false,
            progress: null,
            CancellationToken.None);

        var rollback = await manager.RollbackAsync(
            fixture.Options,
            preview,
            dryRun: false,
            CancellationToken.None);

        Assert.NotEqual(updated.Install!.ToolchainId, composition.Install!.ToolchainId);
        Assert.Equal(composition.Install.ToolchainId, rollback.FromToolchainId);
        Assert.Equal(initial.Install!.ToolchainId, rollback.ToToolchainId);
        Assert.Equal(initial.Install.ToolchainId, rollback.State.Default?.ToolchainId);
    }

    private static ReleaseAssetSet CreateReleaseAssetSet(
        string version,
        PlatformContext platform,
        string? key = null,
        string executableContent = "binary")
    {
        key ??= version;
        var bundle = CreateBundle(platform.ExecutableName, version, executableContent);
        var bundleSha256 = Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant();
        var bundleName = new ReleaseAssetLocator().GetEidoscBundleAssetName(version, platform);
        var checksum = Encoding.UTF8.GetBytes($"{bundleSha256}  {bundleName}\n");
        var manifestName = new ReleaseAssetLocator().GetToolchainManifestAssetName(version, platform);
        var manifestSha256 = key == version ? new string('d', 64) : bundleSha256;
        var release = new EidosReleaseInfo(
            $"eidosc-v{version}",
            $"Eidosc {version}",
            Draft: false,
            PreRelease: true,
            EidosupToolchainTestFixture.FixedTime,
            [
                new EidosReleaseAsset(bundleName, $"https://example.invalid/{key}/{bundleName}", bundle.Length),
                new EidosReleaseAsset(manifestName, $"https://example.invalid/{key}/{manifestName}", 100, manifestSha256),
                new EidosReleaseAsset(ReleaseAssetLocator.ChecksumAssetName, $"https://example.invalid/{key}/SHA256SUMS", checksum.Length)
            ]);
        return new ReleaseAssetSet(key, release, bundle, checksum, bundleSha256, executableContent);
    }

    private static EidosReleaseInfo CreateRelease(string version, long bundleSize)
    {
        var platform = PlatformContext.Detect();
        var name = new ReleaseAssetLocator().GetEidoscBundleAssetName(version, platform);
        var manifestName = new ReleaseAssetLocator().GetToolchainManifestAssetName(version, platform);
        return new EidosReleaseInfo(
            $"eidosc-v{version}",
            $"Eidosc {version}",
            Draft: false,
            PreRelease: true,
            EidosupToolchainTestFixture.FixedTime,
            [
                new EidosReleaseAsset(name, $"https://example.invalid/{name}", bundleSize),
                new EidosReleaseAsset(manifestName, $"https://example.invalid/{manifestName}", 100, new string('d', 64)),
                new EidosReleaseAsset(ReleaseAssetLocator.ChecksumAssetName, $"https://example.invalid/{version}/SHA256SUMS", 100)
            ]);
    }

    private static IToolchainManifestLoader CreateManifestLoader(params ReleaseAssetSet[] assets) =>
        new StubToolchainManifestLoader((release, manifestAsset, platform) =>
        {
            var asset = assets.Single(candidate => ReferenceEquals(candidate.Release, release));
            return EidosupDistributionTestFixture.Create(
                release,
                manifestAsset,
                platform,
                asset.BundleSha256,
                asset.ExecutableContent);
        });

    private static byte[] CreateBundle(string executableName, string version, string executableContent = "binary")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, executableName, executableContent);
            WriteEntry(archive, "stdlib/Std/Core.eidos", "module Std::Core");
            WriteEntry(archive, "runtime/runtime.h", "header");
            WriteEntry(archive, "docs/index.json", $"{{\"schema\":1,\"eidoscVersion\":\"{version}\",\"topics\":{{\"index\":\"index.md\"}}}}");
            WriteEntry(archive, "docs/index.md", "# docs");
            WriteEntry(
                archive,
                OperatingSystem.IsWindows()
                    ? "tools/eidos-bindgen/eidos-bindgen.exe"
                    : "tools/eidos-bindgen/eidos-bindgen",
                "bindgen");
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class RecordingRunner(int exitCode) : IProxyProcessRunner
    {
        public ResolvedToolchain? Toolchain { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<int> RunAsync(
            ResolvedToolchain toolchain,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Toolchain = toolchain;
            Arguments = arguments;
            return Task.FromResult(exitCode);
        }
    }

    private sealed class ThrowingUninstallFaultInjector(
        ToolchainUninstallCheckpoint checkpoint) : IToolchainUninstallFaultInjector
    {
        public Task OnCheckpointAsync(
            ToolchainUninstallCheckpoint current,
            CancellationToken cancellationToken) => current == checkpoint
            ? Task.FromException(new InvalidOperationException($"fault at {current}"))
            : Task.CompletedTask;
    }

    private sealed class MappingReleaseSource(
        EidosReleaseInfo first,
        EidosReleaseInfo second) : IEidosReleaseSource
    {
        public Task<EidosReleaseInfo> ResolveReleaseAsync(
            string? version,
            ReleaseChannel channel,
            CancellationToken cancellationToken)
        {
            if (version == null)
            {
                return Task.FromResult(first);
            }

            return Task.FromResult(string.Equals(first.NormalizedVersion, version, StringComparison.Ordinal)
                ? first
                : second);
        }

        public void Dispose()
        {
        }
    }

    private sealed class SingleReleaseSource(EidosReleaseInfo release) : IEidosReleaseSource
    {
        public Task<EidosReleaseInfo> ResolveReleaseAsync(
            string? version,
            ReleaseChannel channel,
            CancellationToken cancellationToken) => Task.FromResult(release);

        public void Dispose()
        {
        }
    }

    private sealed class MappingAssetHandler(params ReleaseAssetSet[] assets) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            var asset = assets.Single(candidate => uri.Contains($"/{candidate.Key}/", StringComparison.Ordinal));
            var payload = request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.Ordinal)
                ? asset.Checksum
                : asset.Bundle;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private sealed record ReleaseAssetSet(
        string Key,
        EidosReleaseInfo Release,
        byte[] Bundle,
        byte[] Checksum,
        string BundleSha256,
        string ExecutableContent);
}
