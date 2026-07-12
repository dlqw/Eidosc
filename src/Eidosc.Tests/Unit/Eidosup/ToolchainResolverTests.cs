using System.Security.Cryptography;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Proxies;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ToolchainResolverTests
{
    private static readonly DateTimeOffset FixedTime = DateTimeOffset.Parse("2026-07-12T00:00:00Z");
    private const string AssetHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task ResolveAsync_UsesVerifiedDefaultToolchain()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var fixture = await CreateVerifiedToolchainAsync(layout);
        await new ToolchainStateStore(static () => FixedTime).RegisterInstallAsync(
            layout,
            fixture.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);

        var resolved = await new ToolchainResolver().ResolveAsync(
            layout,
            "eidosc",
            selector: null,
            CancellationToken.None);

        Assert.Equal("preview", resolved.Selector);
        Assert.Equal(ToolchainSelectionSource.Default, resolved.SelectionSource);
        Assert.Equal(fixture.Manifest.ToolchainId, resolved.ToolchainId);
        Assert.Equal(Path.Combine(fixture.Directory, PlatformContext.Detect().ExecutableName), resolved.CommandPath);
        Assert.Equal(Path.Combine(fixture.Directory, "runtime"), resolved.RuntimePath);
    }

    [Fact]
    public async Task ResolveAsync_UsesExplicitInstalledSelector()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var fixture = await CreateVerifiedToolchainAsync(layout);
        await new ToolchainStateStore(static () => FixedTime).RegisterInstallAsync(
            layout,
            fixture.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);

        var resolved = await new ToolchainResolver().ResolveAsync(
            layout,
            "eidosc",
            "0.4.0-alpha.2",
            CancellationToken.None);

        Assert.Equal("0.4.0-alpha.2", resolved.Selector);
        Assert.Equal(ToolchainSelectionSource.Explicit, resolved.SelectionSource);
        Assert.Equal(fixture.Manifest.ToolchainId, resolved.ToolchainId);
    }

    [Fact]
    public async Task ResolveAsync_RequiresDefaultToolchain()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        await CreateVerifiedToolchainAsync(layout);
        await new ToolchainStateStore(static () => FixedTime).InitializeAsync(
            layout,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            new ToolchainResolver().ResolveAsync(
                layout,
                "eidosc",
                selector: null,
                CancellationToken.None));

        Assert.Equal(EidosupErrorCode.NoActiveToolchain, exception.Code);
        Assert.Equal(EidosupExitCodes.NoActiveToolchain, exception.ExitCode);
    }

    [Fact]
    public async Task ResolveAsync_RejectsTamperedSelectedToolchain()
    {
        using var temporary = new TemporaryDirectory();
        var layout = CreateLayout(temporary.Path);
        var fixture = await CreateVerifiedToolchainAsync(layout);
        await new ToolchainStateStore(static () => FixedTime).RegisterInstallAsync(
            layout,
            fixture.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Directory, PlatformContext.Detect().ExecutableName),
            "tampered");

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            new ToolchainResolver().ResolveAsync(
                layout,
                "eidosc",
                selector: null,
                CancellationToken.None));

        Assert.Equal(EidosupErrorCode.StateCorrupt, exception.Code);
    }

    private static ToolInstallLayout CreateLayout(string root) => ToolInstallLayout.Create(
        PlatformContext.Detect(),
        Path.Combine(root, "install"),
        Path.Combine(root, "downloads"));

    private static async Task<VerifiedToolchainFixture> CreateVerifiedToolchainAsync(ToolInstallLayout layout)
    {
        var platform = PlatformContext.Detect();
        var identity = ToolchainIdentity.Create(
            "0.4.0-alpha.2",
            platform.Rid,
            "test/source",
            "eidosc-v0.4.0-alpha.2",
            $"eidosc-v0.4.0-alpha.2-{platform.Rid}.zip",
            AssetHash,
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
            "eidosc-v0.4.0-alpha.2",
            "0.4.0-alpha.2",
            platform.Rid,
            "test/source",
            $"eidosc-v0.4.0-alpha.2-{platform.Rid}.zip",
            AssetHash,
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidosup-resolver-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
