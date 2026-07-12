using System.Security.Cryptography;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ToolchainStateStoreTests
{
    private static readonly DateTimeOffset FixedTime = DateTimeOffset.Parse("2026-07-12T00:00:00Z");
    private const string AssetHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task InitializeAsync_IsIdempotentAndWritesExplicitSchema()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var store = new ToolchainStateStore(static () => FixedTime);

        var first = await store.InitializeAsync(layout, CancellationToken.None);
        var statePath = Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName);
        var firstBytes = await File.ReadAllBytesAsync(statePath);
        var second = await store.InitializeAsync(layout, CancellationToken.None);
        var secondBytes = await File.ReadAllBytesAsync(statePath);

        Assert.Equal(ToolchainState.CurrentSchema, first.Schema);
        Assert.Equal(1, first.Revision);
        Assert.Equal(first.Schema, second.Schema);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(first.UpdatedAt, second.UpdatedAt);
        Assert.Equal(first.Toolchains, second.Toolchains);
        Assert.Equal(first.Selectors, second.Selectors);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Empty(first.Toolchains);
    }

    [Fact]
    public async Task InitializeAsync_RefusesFutureSchemaWithoutModifyingIt()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        Directory.CreateDirectory(layout.StateDirectory);
        var statePath = Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName);
        await File.WriteAllTextAsync(statePath, "{\"schema\":99,\"sentinel\":\"preserve\"}");
        var original = await File.ReadAllBytesAsync(statePath);
        var store = new ToolchainStateStore(static () => FixedTime);

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            store.InitializeAsync(layout, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.StateUnsupported, exception.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(statePath));
        Assert.False(File.Exists(Path.Combine(layout.StateDirectory, ToolchainStateStore.BackupFileName)));
    }

    [Fact]
    public async Task InitializeAsync_RefusesFutureBackupWhenPrimaryIsCorrupt()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        Directory.CreateDirectory(layout.StateDirectory);
        var statePath = Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName);
        var backupPath = Path.Combine(layout.StateDirectory, ToolchainStateStore.BackupFileName);
        await File.WriteAllTextAsync(statePath, "{corrupt");
        await File.WriteAllTextAsync(backupPath, "{\"schema\":99,\"sentinel\":\"preserve\"}");
        var originalPrimary = await File.ReadAllBytesAsync(statePath);
        var originalBackup = await File.ReadAllBytesAsync(backupPath);
        var store = new ToolchainStateStore(static () => FixedTime);

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            store.InitializeAsync(layout, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.StateUnsupported, exception.Code);
        Assert.Equal(originalPrimary, await File.ReadAllBytesAsync(statePath));
        Assert.Equal(originalBackup, await File.ReadAllBytesAsync(backupPath));
    }

    [Fact]
    public async Task InitializeAsync_RefusesFutureBackupBeforeAnyWrite()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var store = new ToolchainStateStore(static () => FixedTime);
        await store.InitializeAsync(layout, CancellationToken.None);
        var statePath = Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName);
        var backupPath = Path.Combine(layout.StateDirectory, ToolchainStateStore.BackupFileName);
        await File.WriteAllTextAsync(backupPath, "{\"schema\":99,\"sentinel\":\"preserve\"}");
        var originalPrimary = await File.ReadAllBytesAsync(statePath);
        var originalBackup = await File.ReadAllBytesAsync(backupPath);

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            store.InitializeAsync(layout, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.StateUnsupported, exception.Code);
        Assert.Equal(originalPrimary, await File.ReadAllBytesAsync(statePath));
        Assert.Equal(originalBackup, await File.ReadAllBytesAsync(backupPath));
    }

    [Fact]
    public async Task InitializeAsync_RebuildsCorruptPrimaryFromVerifiedManifestAndBackup()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var toolchain = await CreateVerifiedToolchainAsync(layout);
        var store = new ToolchainStateStore(static () => FixedTime);
        await store.InitializeAsync(layout, CancellationToken.None);
        await store.RegisterInstallAsync(layout, toolchain.Directory, ReleaseChannel.Preview, CancellationToken.None);
        var statePath = Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName);
        Assert.True(File.Exists(Path.Combine(layout.StateDirectory, ToolchainStateStore.BackupFileName)));
        await File.WriteAllTextAsync(statePath, "{not-json");

        var recovered = await store.InitializeAsync(layout, CancellationToken.None);
        var reread = await ToolchainStateStore.ReadAsync(layout, CancellationToken.None);

        var installed = Assert.Single(recovered.Toolchains);
        Assert.Equal(toolchain.Manifest.ToolchainId, installed.Id);
        Assert.Equal(recovered.Schema, reread.Schema);
        Assert.Equal(recovered.Revision, reread.Revision);
        Assert.Equal(recovered.Toolchains, reread.Toolchains);
        Assert.Equal(recovered.Selectors, reread.Selectors);
        Assert.Empty(recovered.UnmanagedDirectories);
        Assert.True(File.Exists(Path.Combine(layout.StateDirectory, ToolchainStateStore.CorruptFileName)));

        await File.WriteAllTextAsync(statePath, "{corrupt-again");
        var recoveredAgain = await store.InitializeAsync(layout, CancellationToken.None);
        Assert.Equal(toolchain.Manifest.ToolchainId, Assert.Single(recoveredAgain.Toolchains).Id);
    }

    [Fact]
    public async Task InitializeAsync_DoesNotImportLegacyVersionDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var legacyDirectory = Path.Combine(layout.ToolchainsDirectory, "eidosc", "0.4.0-alpha.2");
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(legacyDirectory, InstallManifest.FileName),
            "{\"schema\":1,\"version\":\"0.4.0-alpha.2\"}");
        var store = new ToolchainStateStore(static () => FixedTime);

        var state = await store.InitializeAsync(layout, CancellationToken.None);

        Assert.Empty(state.Toolchains);
        var unmanaged = Assert.Single(state.UnmanagedDirectories);
        Assert.Equal("eidosc", unmanaged.DirectoryName);
        Assert.Equal(UnmanagedToolchainReason.LegacyLayout, unmanaged.Reason);
        Assert.Contains("Reinstall", unmanaged.Guidance, StringComparison.Ordinal);
        Assert.Contains("remove", unmanaged.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterInstallAsync_RecordsImmutableToolchainAndSelectorsIdempotently()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var toolchain = await CreateVerifiedToolchainAsync(layout);
        var store = new ToolchainStateStore(static () => FixedTime);

        var first = await store.RegisterInstallAsync(
            layout,
            toolchain.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);
        var second = await store.RegisterInstallAsync(
            layout,
            toolchain.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);

        Assert.Equal(first.Revision, second.Revision);
        var installed = Assert.Single(second.Toolchains);
        Assert.Equal(toolchain.Manifest.ToolchainId, installed.Id);
        Assert.Equal(toolchain.Manifest.ManifestSha256, installed.ManifestSha256);
        Assert.Collection(
            second.Selectors,
            selector =>
            {
                Assert.Equal("0.4.0-alpha.2", selector.Selector);
                Assert.Equal(ToolchainSelectorKind.ExactVersion, selector.Kind);
            },
            selector =>
            {
                Assert.Equal("preview", selector.Selector);
                Assert.Equal(ToolchainSelectorKind.Channel, selector.Kind);
            });
        Assert.NotNull(second.Default);
        Assert.Equal("preview", second.Default.Selector);
        Assert.Equal(installed.Id, second.Default.ToolchainId);
        var activation = Assert.Single(second.ActivationHistory);
        Assert.Equal("preview", activation.Selector);
        Assert.Equal(installed.Id, activation.ToolchainId);
        Assert.Equal(ToolchainActivationReason.DefaultChanged, activation.Reason);
        Assert.Empty(second.Transactions);
    }

    [Fact]
    public async Task RegisterInstallAsync_AdvancesDefaultChannelToNewImmutableToolchain()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var firstToolchain = await CreateVerifiedToolchainAsync(layout);
        var secondToolchain = await CreateVerifiedToolchainAsync(
            layout,
            "0.4.0-alpha.3",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
        var store = new ToolchainStateStore(static () => FixedTime);
        await store.RegisterInstallAsync(
            layout,
            firstToolchain.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);

        var state = await store.RegisterInstallAsync(
            layout,
            secondToolchain.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);

        Assert.Equal(secondToolchain.Manifest.ToolchainId, state.Default?.ToolchainId);
        Assert.Equal("preview", state.Default?.Selector);
        Assert.Collection(
            state.ActivationHistory,
            activation => Assert.Equal(ToolchainActivationReason.DefaultChanged, activation.Reason),
            activation =>
            {
                Assert.Equal(ToolchainActivationReason.ChannelUpdated, activation.Reason);
                Assert.Equal(secondToolchain.Manifest.ToolchainId, activation.ToolchainId);
            });
    }

    [Fact]
    public async Task InitializeAsync_RejectsTamperedToolchainFromInstalledSet()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var toolchain = await CreateVerifiedToolchainAsync(layout);
        await File.WriteAllTextAsync(Path.Combine(toolchain.Directory, PlatformContext.Detect().ExecutableName), "tampered");
        var store = new ToolchainStateStore(static () => FixedTime);

        var state = await store.InitializeAsync(layout, CancellationToken.None);

        Assert.Empty(state.Toolchains);
        Assert.Equal(UnmanagedToolchainReason.InvalidManifest, Assert.Single(state.UnmanagedDirectories).Reason);
    }

    [Fact]
    public async Task ReadVerifiedAsync_DetectsStateDriftWithoutRewritingState()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var toolchain = await CreateVerifiedToolchainAsync(layout);
        var store = new ToolchainStateStore(static () => FixedTime);
        await store.RegisterInstallAsync(layout, toolchain.Directory, ReleaseChannel.Preview, CancellationToken.None);
        var statePath = Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName);
        var original = await File.ReadAllBytesAsync(statePath);
        await File.WriteAllTextAsync(Path.Combine(toolchain.Directory, PlatformContext.Detect().ExecutableName), "tampered");

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            ToolchainStateStore.ReadVerifiedAsync(layout, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.StateCorrupt, exception.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(statePath));
    }

    private static ToolInstallLayout CreateLayout(string root) => ToolInstallLayout.Create(
        PlatformContext.Detect(),
        Path.Combine(root, "install"),
        Path.Combine(root, "downloads"));

    private static async Task<VerifiedToolchainFixture> CreateVerifiedToolchainAsync(
        ToolInstallLayout layout,
        string version = "0.4.0-alpha.2",
        string assetHash = AssetHash)
    {
        var platform = PlatformContext.Detect();
        var identity = ToolchainIdentity.Create(
            version,
            platform.Rid,
            "test/source",
            $"eidosc-v{version}",
            $"eidosc-v{version}-{platform.Rid}.zip",
            assetHash,
            123);
        var directory = layout.GetToolchainDirectory(identity.Id);
        Directory.CreateDirectory(Path.Combine(directory, "runtime"));
        var executablePath = Path.Combine(directory, platform.ExecutableName);
        var runtimePath = Path.Combine(directory, "runtime", "runtime.h");
        await File.WriteAllTextAsync(executablePath, "binary");
        await File.WriteAllTextAsync(runtimePath, "header");
        var manifest = new InstallManifest(
            InstallManifest.CurrentSchema,
            identity.Id,
            identity.ManifestSha256,
            $"eidosc-v{version}",
            version,
            platform.Rid,
            "test/source",
            $"eidosc-v{version}-{platform.Rid}.zip",
            assetHash,
            123,
            FixedTime,
            [
                new InstalledFile(platform.ExecutableName, 6, await HashAsync(executablePath)),
                new InstalledFile("runtime/runtime.h", 6, await HashAsync(runtimePath))
            ]);
        await manifest.WriteAsync(directory, CancellationToken.None);
        return new VerifiedToolchainFixture(directory, manifest);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private sealed record VerifiedToolchainFixture(string Directory, InstallManifest Manifest);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidosup-state-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }
}
