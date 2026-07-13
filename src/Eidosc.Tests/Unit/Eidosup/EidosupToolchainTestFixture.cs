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
            $"eidos-toolchain-v{version}-{platform.Rid}.json",
            assetHash,
            ["eidosc-core", "eidos-std", $"eidos-runtime@{platform.Rid}"]);
        var directory = Layout.GetToolchainDirectory(identity.Id);
        Directory.CreateDirectory(Path.Combine(directory, "runtime"));
        Directory.CreateDirectory(Path.Combine(directory, "stdlib", "Std"));
        var executablePath = Path.Combine(directory, platform.ExecutableName);
        var runtimePath = Path.Combine(directory, "runtime", "runtime.h");
        var stdlibPath = Path.Combine(directory, "stdlib", "Std", "Core.eidos");
        await File.WriteAllTextAsync(executablePath, "binary");
        await File.WriteAllTextAsync(runtimePath, "header");
        await File.WriteAllTextAsync(stdlibPath, "module Std::Core");
        var manifest = new InstallManifest(
            InstallManifest.CurrentSchema,
            identity.Id,
            identity.IdentitySha256,
            identity.CompositionSha256,
            $"eidos-toolchain-v{version}-{platform.Rid}.json",
            assetHash,
            $"eidosc-v{version}",
            version,
            platform.Rid,
            source,
            "default",
            [],
            [],
            [
                new InstalledComponent("eidosc-core", "eidosc-core", version, true, null, [platform.ExecutableName]),
                new InstalledComponent("eidos-std", "eidos-std", "0.1.0-alpha.1", true, null, ["stdlib/Std/Core.eidos"]),
                new InstalledComponent($"eidos-runtime@{platform.Rid}", "eidos-runtime", "0.1.0-alpha.1", false, platform.Rid, ["runtime/runtime.h"])
            ],
            [platform.Rid],
            [new InstalledArtifact($"eidosc-v{version}-{platform.Rid}.zip", assetHash, 123)],
            FixedTime,
            [
                new InstalledFile(platform.ExecutableName, 6, await HashAsync(executablePath)),
                new InstalledFile("stdlib/Std/Core.eidos", 16, await HashAsync(stdlibPath)),
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
