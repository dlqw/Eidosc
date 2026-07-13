using Eidosup.Diagnostics;
using Eidosup.Installation;
using Eidosup.Proxies;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ShimInstallerTests
{
    private static readonly DateTimeOffset FixedTime = DateTimeOffset.Parse("2026-07-12T00:00:00Z");

    [Fact]
    public async Task InstallAsync_DryRunDoesNotCreateBinDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var source = await CreateSourceAsync(temporary.Path, "manager-v1");
        var layout = CreateLayout(temporary.Path);
        var installer = new ShimInstaller(source, static () => FixedTime);

        var result = await installer.InstallAsync(layout, dryRun: true, CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.True(result.Changed);
        Assert.False(Directory.Exists(layout.BinDirectory));
    }

    [Fact]
    public async Task InstallAsync_CreatesOwnedManagerAndShimIdempotently()
    {
        using var temporary = new TemporaryDirectory();
        var source = await CreateSourceAsync(temporary.Path, "manager-v1");
        var layout = CreateLayout(temporary.Path);
        var installer = new ShimInstaller(source, static () => FixedTime);

        var first = await installer.InstallAsync(layout, dryRun: false, CancellationToken.None);
        var second = await installer.InstallAsync(layout, dryRun: false, CancellationToken.None);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal("manager-v1", await File.ReadAllTextAsync(first.ManagerPath));
        Assert.Equal("manager-v1", await File.ReadAllTextAsync(first.ShimPath));
        Assert.True(File.Exists(Path.Combine(layout.BinDirectory, ShimInstaller.ManifestFileName)));
    }

    [Fact]
    public async Task InstallAsync_UpdatesBothStableCommandsFromNewManager()
    {
        using var temporary = new TemporaryDirectory();
        var source = await CreateSourceAsync(temporary.Path, "manager-v1");
        var layout = CreateLayout(temporary.Path);
        await new ShimInstaller(source, static () => FixedTime)
            .InstallAsync(layout, dryRun: false, CancellationToken.None);
        await File.WriteAllTextAsync(source, "manager-v2");

        var updated = await new ShimInstaller(source, static () => FixedTime.AddMinutes(1))
            .InstallAsync(layout, dryRun: false, CancellationToken.None);

        Assert.True(updated.Changed);
        Assert.Equal("manager-v2", await File.ReadAllTextAsync(updated.ManagerPath));
        Assert.Equal("manager-v2", await File.ReadAllTextAsync(updated.ShimPath));
    }

    [Fact]
    public async Task InstallAsync_RefusesUnownedStableCommand()
    {
        using var temporary = new TemporaryDirectory();
        var source = await CreateSourceAsync(temporary.Path, "manager-v1");
        var layout = CreateLayout(temporary.Path);
        Directory.CreateDirectory(layout.BinDirectory);
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        await File.WriteAllTextAsync(Path.Combine(layout.BinDirectory, $"eidosc{extension}"), "unowned");

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            new ShimInstaller(source, static () => FixedTime)
                .InstallAsync(layout, dryRun: false, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.InstallConflict, exception.Code);
    }

    [Fact]
    public async Task InstallAsync_RefusesToOverwriteModifiedOwnedCommands()
    {
        using var temporary = new TemporaryDirectory();
        var source = await CreateSourceAsync(temporary.Path, "manager-v1");
        var layout = CreateLayout(temporary.Path);
        var installed = await new ShimInstaller(source, static () => FixedTime)
            .InstallAsync(layout, dryRun: false, CancellationToken.None);
        await File.WriteAllTextAsync(installed.ShimPath, "locally-modified");
        await File.WriteAllTextAsync(source, "manager-v2");

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            new ShimInstaller(source, static () => FixedTime.AddMinutes(1))
                .InstallAsync(layout, dryRun: false, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.InstallConflict, exception.Code);
        Assert.Equal("locally-modified", await File.ReadAllTextAsync(installed.ShimPath));
    }

    private static ToolInstallLayout CreateLayout(string root) => ToolInstallLayout.Create(
        PlatformContext.Detect(),
        Path.Combine(root, "install"),
        Path.Combine(root, "downloads"));

    private static async Task<string> CreateSourceAsync(string root, string content)
    {
        var path = Path.Combine(root, OperatingSystem.IsWindows() ? "source.exe" : "source");
        await File.WriteAllTextAsync(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidosup-shim-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
