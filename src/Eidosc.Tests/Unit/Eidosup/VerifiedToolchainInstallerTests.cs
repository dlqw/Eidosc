using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class VerifiedToolchainInstallerTests
{
    [Fact]
    public async Task InstallAsync_CommitsVerifiedToolchainAndRecognizesRepeatInstall()
    {
        using var temporary = new TemporaryDirectory();
        var fixture = CreateFixture(temporary.Path);
        using var httpClient = new HttpClient(fixture.Handler);
        using var downloadManager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        using var installer = new VerifiedToolchainInstaller(
            downloadManager,
            clock: static () => DateTimeOffset.Parse("2026-07-12T00:00:00Z"));

        var first = await installer.InstallAsync(fixture.Request, progress: null, CancellationToken.None);
        var second = await installer.InstallAsync(fixture.Request, progress: null, CancellationToken.None);

        Assert.Equal(InstallDisposition.Installed, first.Disposition);
        Assert.Equal(InstallDisposition.AlreadyInstalled, second.Disposition);
        Assert.True(File.Exists(Path.Combine(fixture.ToolchainDirectory, fixture.Platform.ExecutableName)));
        var manifest = await InstallManifest.TryReadAsync(fixture.ToolchainDirectory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(fixture.ManifestSha256, manifest.DistributionManifestSha256);
        Assert.Equal(fixture.Platform.Rid, manifest.Rid);
        Assert.Equal(3, manifest.Files.Count);
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.TransactionDirectory));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.StagingDirectory));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.BackupDirectory));
        Assert.Equal(1, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task InstallAsync_FaultAfterMovingPreviousVersionRestoresIt()
    {
        using var temporary = new TemporaryDirectory();
        var fixture = CreateFixture(temporary.Path, force: true);
        Directory.CreateDirectory(fixture.ToolchainDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.ToolchainDirectory, "old.marker"), "old");
        using var httpClient = new HttpClient(fixture.Handler);
        using var downloadManager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        using var installer = new VerifiedToolchainInstaller(
            downloadManager,
            faultInjector: new ThrowingFaultInjector(InstallCheckpoint.PreviousMoved));

        await Assert.ThrowsAsync<InjectedInstallFault>(() =>
            installer.InstallAsync(fixture.Request, progress: null, CancellationToken.None));

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(fixture.ToolchainDirectory, "old.marker")));
        Assert.False(File.Exists(Path.Combine(fixture.ToolchainDirectory, InstallManifest.FileName)));
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.TransactionDirectory));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.StagingDirectory));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.BackupDirectory));
    }

    [Fact]
    public async Task InstallAsync_FaultAfterStagingLeavesPreviousVersionUntouched()
    {
        using var temporary = new TemporaryDirectory();
        var fixture = CreateFixture(temporary.Path, force: true);
        Directory.CreateDirectory(fixture.ToolchainDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.ToolchainDirectory, "old.marker"), "old");
        using var httpClient = new HttpClient(fixture.Handler);
        using var downloadManager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        using var installer = new VerifiedToolchainInstaller(
            downloadManager,
            faultInjector: new ThrowingFaultInjector(InstallCheckpoint.Staged));

        await Assert.ThrowsAsync<InjectedInstallFault>(() =>
            installer.InstallAsync(fixture.Request, progress: null, CancellationToken.None));

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(fixture.ToolchainDirectory, "old.marker")));
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.TransactionDirectory));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.StagingDirectory));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.BackupDirectory));
    }

    [Fact]
    public async Task InstallAsync_DoesNotTrustTamperedInstalledFiles()
    {
        using var temporary = new TemporaryDirectory();
        var fixture = CreateFixture(temporary.Path);
        using var httpClient = new HttpClient(fixture.Handler);
        using var downloadManager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        using var installer = new VerifiedToolchainInstaller(downloadManager);
        await installer.InstallAsync(fixture.Request, progress: null, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.ToolchainDirectory, fixture.Platform.ExecutableName),
            "tampered");

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            installer.InstallAsync(fixture.Request, progress: null, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.InstallConflict, exception.Code);
    }

    [Fact]
    public async Task InstallAsync_FaultAfterCommitLeavesRecoverableVerifiedTarget()
    {
        using var temporary = new TemporaryDirectory();
        var fixture = CreateFixture(temporary.Path, force: true);
        Directory.CreateDirectory(fixture.ToolchainDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.ToolchainDirectory, "old.marker"), "old");
        using var httpClient = new HttpClient(fixture.Handler);
        using var downloadManager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        using (var faultingInstaller = new VerifiedToolchainInstaller(
                   downloadManager,
                   faultInjector: new ThrowingFaultInjector(InstallCheckpoint.TargetCommitted)))
        {
            var exception = await Assert.ThrowsAsync<EidosupException>(() =>
                faultingInstaller.InstallAsync(fixture.Request, progress: null, CancellationToken.None));
            Assert.Equal(EidosupErrorCode.InstallFailure, exception.Code);
        }

        Assert.True(File.Exists(Path.Combine(fixture.ToolchainDirectory, fixture.Platform.ExecutableName)));
        Assert.Single(Directory.EnumerateFiles(fixture.Layout.TransactionDirectory));
        Assert.Single(Directory.EnumerateDirectories(fixture.Layout.BackupDirectory));
        using var recoveringInstaller = new VerifiedToolchainInstaller(downloadManager);
        await recoveringInstaller.RecoverAsync(fixture.Layout, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(fixture.ToolchainDirectory, "old.marker")));
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.TransactionDirectory));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.BackupDirectory));
    }

    [Fact]
    public async Task RecoverAsync_RestoresBackupFromInterruptedJournal()
    {
        using var temporary = new TemporaryDirectory();
        var fixture = CreateFixture(temporary.Path);
        Directory.CreateDirectory(fixture.Layout.TransactionDirectory);
        Directory.CreateDirectory(fixture.Layout.StagingDirectory);
        Directory.CreateDirectory(fixture.Layout.BackupDirectory);
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(fixture.Layout.StagingDirectory, $"install-{id}");
        var backup = Path.Combine(fixture.Layout.BackupDirectory, $"install-{id}");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(backup, "old.marker"), "old");
        var journalPath = Path.Combine(fixture.Layout.TransactionDirectory, $"install-{id}.json");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(new
        {
            schema = 2,
            id,
            state = "previousMoved",
            targetDirectory = fixture.ToolchainDirectory,
            stageDirectory = stage,
            backupDirectory = backup,
            expectedSha256 = fixture.ManifestSha256,
            rid = fixture.Platform.Rid,
            version = "0.4.0-alpha.2",
            toolchainId = fixture.ToolchainId
        }));
        using var installer = new VerifiedToolchainInstaller();

        await installer.RecoverAsync(fixture.Layout, CancellationToken.None);

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(fixture.ToolchainDirectory, "old.marker")));
        Assert.False(File.Exists(journalPath));
        Assert.False(Directory.Exists(stage));
        Assert.False(Directory.Exists(backup));
    }

    [Theory]
    [InlineData("started")]
    [InlineData("staged")]
    public async Task RecoverAsync_LeavesUntouchedPreviousTargetBeforeBackupMove(string state)
    {
        using var temporary = new TemporaryDirectory();
        var fixture = CreateFixture(temporary.Path);
        Directory.CreateDirectory(fixture.Layout.TransactionDirectory);
        Directory.CreateDirectory(fixture.Layout.StagingDirectory);
        Directory.CreateDirectory(fixture.Layout.BackupDirectory);
        Directory.CreateDirectory(fixture.ToolchainDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.ToolchainDirectory, "old.marker"), "old");
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(fixture.Layout.StagingDirectory, $"install-{id}");
        var backup = Path.Combine(fixture.Layout.BackupDirectory, $"install-{id}");
        Directory.CreateDirectory(stage);
        var journalPath = Path.Combine(fixture.Layout.TransactionDirectory, $"install-{id}.json");
        await WriteJournalAsync(fixture, id, state, stage, backup, journalPath, hadPreviousTarget: true);
        using var installer = new VerifiedToolchainInstaller();

        await installer.RecoverAsync(fixture.Layout, CancellationToken.None);

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(fixture.ToolchainDirectory, "old.marker")));
        Assert.False(File.Exists(journalPath));
        Assert.False(Directory.Exists(stage));
    }

    [Fact]
    public async Task RecoverAsync_RejectsJournalWhoseTransactionPathsDoNotMatchId()
    {
        using var temporary = new TemporaryDirectory();
        var fixture = CreateFixture(temporary.Path);
        Directory.CreateDirectory(fixture.Layout.TransactionDirectory);
        var id = Guid.NewGuid().ToString("N");
        var journalPath = Path.Combine(fixture.Layout.TransactionDirectory, $"install-{id}.json");
        await WriteJournalAsync(
            fixture,
            id,
            "previousMoved",
            Path.Combine(fixture.Layout.StagingDirectory, "install-wrong"),
            Path.Combine(fixture.Layout.BackupDirectory, $"install-{id}"),
            journalPath,
            hadPreviousTarget: true);
        using var installer = new VerifiedToolchainInstaller();

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            installer.RecoverAsync(fixture.Layout, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.InstallFailure, exception.Code);
        Assert.True(File.Exists(journalPath));
    }

    [Fact]
    public async Task InstallOperationLock_RejectsConcurrentWriterAfterTimeout()
    {
        using var temporary = new TemporaryDirectory();
        await using var first = await InstallOperationLock.AcquireAsync(
            temporary.Path,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            InstallOperationLock.AcquireAsync(
                temporary.Path,
                TimeSpan.FromMilliseconds(150),
                CancellationToken.None));

        Assert.Equal(EidosupErrorCode.LockTimeout, exception.Code);
    }

    private static InstallFixture CreateFixture(string root, bool force = false)
    {
        var platform = PlatformContext.Detect();
        var bundle = CreateBundle(platform.ExecutableName);
        var bundleSha256 = Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant();
        var manifestSha256 = new string('d', 64);
        var bundleName = $"eidosc-v0.4.0-alpha.2-{platform.Rid}.zip";
        var handler = new AssetHandler(bundle);
        var manifestName = $"eidos-toolchain-v0.4.0-alpha.2-{platform.Rid}.json";
        var artifact = new ToolchainComponentArtifact(bundleName, bundle.Length, bundleSha256);
        var core = new ToolchainComponentDefinition(
            "eidosc-core",
            "eidosc-core",
            "0.4.0-alpha.2",
            Required: true,
            Target: null,
            Dependencies: [],
            Conflicts: [],
            artifact,
            [new ToolchainComponentFile(platform.ExecutableName, 6, HashText("binary"), Executable: true)]);
        var runtime = new ToolchainComponentDefinition(
            $"eidos-runtime@{platform.Rid}",
            "eidos-runtime",
            "0.1.0-alpha.1",
            Required: false,
            Target: platform.Rid,
            Dependencies: ["eidosc-core"],
            Conflicts: [],
            artifact,
            [new ToolchainComponentFile("runtime/runtime.h", 6, HashText("header"))]);
        var std = new ToolchainComponentDefinition(
            "eidos-std",
            "eidos-std",
            "0.1.0-alpha.1",
            Required: true,
            Target: null,
            Dependencies: [core.Id],
            Conflicts: [],
            artifact,
            [new ToolchainComponentFile("stdlib/Std/Core.eidos", 15, HashText("module Std.Core"))]);
        var manifest = new ToolchainDistributionManifest(
            ToolchainDistributionManifest.CurrentSchema,
            $"eidosc-0.4.0-alpha.2-{platform.Rid}",
            "preview",
            platform.Rid,
            new ToolchainProductIdentity("0.4.0-alpha.2", new string('a', 40)),
            new ToolchainLanguageIdentity("0.6.0-alpha.1"),
            [
                new ToolchainProfileDefinition("minimal", ["eidosc-core", std.Id]),
                new ToolchainProfileDefinition("default", ["eidosc-core", std.Id, runtime.Id]),
                new ToolchainProfileDefinition("complete", ["eidosc-core", std.Id, runtime.Id])
            ],
            [core, std, runtime],
            [new ToolchainTargetDefinition(platform.Rid, "test-triple", runtime.Id, ToolchainTargetSupport.Host, new ToolchainLinkerRequirement("clang", false))],
            new ToolchainRequirementSet(new ToolchainLlvmRequirement(">=20.1.0 <22.0.0")),
            DateTimeOffset.Parse("2026-07-12T00:00:00Z"));
        var release = new EidosReleaseInfo(
            "eidosc-v0.4.0-alpha.2",
            "Eidosc 0.4.0-alpha.2",
            Draft: false,
            PreRelease: true,
            DateTimeOffset.Parse("2026-07-12T00:00:00Z"),
            [new EidosReleaseAsset(bundleName, "https://example.invalid/bundle", bundle.Length, bundleSha256)]);
        var bundleAsset = release.Assets[0];
        var manifestAsset = new EidosReleaseAsset(manifestName, "https://example.invalid/manifest", 100, manifestSha256);
        var layout = ToolInstallLayout.Create(
            platform,
            Path.Combine(root, "install"),
            Path.Combine(root, "downloads"));
        var identity = ToolchainIdentity.Create(
            release.NormalizedVersion,
            platform.Rid,
            "test/source",
            release.TagName,
            manifestName,
            manifestSha256,
            [core.Id, std.Id, runtime.Id]);
        var toolchainDirectory = layout.GetToolchainDirectory(identity.Id);
        var request = new VerifiedInstallRequest(
            release,
            new LoadedToolchainDistribution(
                manifest,
                manifestAsset,
                manifestSha256,
                ChecksumManifest.Parse($"{bundleSha256}  {bundleName}"),
                CacheHit: false,
                Resumed: false),
            new ToolchainComponentPlan(ToolchainProfile.Default, [core, std, runtime], manifest.Targets, [], []),
            platform,
            layout,
            "test/source",
            force);
        return new InstallFixture(platform, layout, toolchainDirectory, identity.Id, request, bundleSha256, manifestSha256, handler);
    }

    private static byte[] CreateBundle(string executableName)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, executableName, "binary");
            WriteEntry(archive, "stdlib/Std/Core.eidos", "module Std.Core");
            WriteEntry(archive, "runtime/runtime.h", "header");
        }

        return stream.ToArray();
    }

    private static Task WriteJournalAsync(
        InstallFixture fixture,
        string id,
        string state,
        string stage,
        string backup,
        string journalPath,
        bool hadPreviousTarget) =>
        File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(new
        {
            schema = 2,
            id,
            state,
            targetDirectory = fixture.ToolchainDirectory,
            stageDirectory = stage,
            backupDirectory = backup,
            expectedSha256 = fixture.ManifestSha256,
            rid = fixture.Platform.Rid,
            version = "0.4.0-alpha.2",
            toolchainId = fixture.ToolchainId,
            hadPreviousTarget
        }));

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string HashText(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private sealed record InstallFixture(
        PlatformContext Platform,
        ToolInstallLayout Layout,
        string ToolchainDirectory,
        string ToolchainId,
        VerifiedInstallRequest Request,
        string BundleSha256,
        string ManifestSha256,
        AssetHandler Handler);

    private sealed class AssetHandler(byte[] bundle) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bundle)
            });
        }
    }

    private sealed class ThrowingFaultInjector(InstallCheckpoint target) : IInstallFaultInjector
    {
        public Task OnCheckpointAsync(InstallCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            if (checkpoint == target)
            {
                throw new InjectedInstallFault();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InjectedInstallFault : Exception;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidosup-install-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
