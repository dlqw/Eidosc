using System.Security.Cryptography;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

internal sealed class EidosupToolchainTestFixture : IDisposable
{
    public static readonly DateTimeOffset FixedTime = DateTimeOffset.Parse("2026-07-12T00:00:00Z");

    public EidosupToolchainTestFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"eidosup-management-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Layout = ToolInstallLayout.Create(
            PlatformContext.Detect(),
            Path.Combine(Root, "install"),
            Path.Combine(Root, "downloads"));
    }

    public string Root { get; }

    public ToolInstallLayout Layout { get; }

    public ToolchainManagementOptions Options => new(
        "test/source",
        Layout.RootDirectory,
        Layout.DownloadDirectory);

    public async Task<(string Directory, InstallManifest Manifest)> CreateToolchainAsync(
        string version,
        string assetHash,
        string source = "test/source")
    {
        var platform = PlatformContext.Detect();
        var identity = ToolchainIdentity.Create(
            version,
            platform.Rid,
            source,
            $"eidosc-v{version}",
            $"eidosc-v{version}-{platform.Rid}.zip",
            assetHash,
            123);
        var directory = Layout.GetToolchainDirectory(identity.Id);
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
            source,
            $"eidosc-v{version}-{platform.Rid}.zip",
            assetHash,
            123,
            FixedTime,
            [
                new InstalledFile(platform.ExecutableName, 6, await HashAsync(executablePath)),
                new InstalledFile("runtime/runtime.h", 6, await HashAsync(runtimePath))
            ]);
        await manifest.WriteAsync(directory, CancellationToken.None);
        return (directory, manifest);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
