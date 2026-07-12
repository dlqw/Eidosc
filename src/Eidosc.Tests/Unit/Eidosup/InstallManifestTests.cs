using System.Security.Cryptography;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class InstallManifestTests
{
    private const string AssetHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task VerifyAsync_RehashesEveryFileAndRejectsUnexpectedContent()
    {
        using var temporary = new TemporaryDirectory();
        var toolchainDirectory = CreateToolchainDirectory(temporary.Path);
        var executablePath = Path.Combine(toolchainDirectory, "eidosc");
        await File.WriteAllTextAsync(executablePath, "binary");
        var manifest = CreateManifest(new InstalledFile("eidosc", 6, await HashAsync(executablePath)));
        await manifest.WriteAsync(toolchainDirectory, CancellationToken.None);

        Assert.True(await manifest.VerifyAsync(toolchainDirectory, AssetHash, CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(toolchainDirectory, "unexpected.dll"), "extra");
        Assert.False(await manifest.VerifyAsync(toolchainDirectory, AssetHash, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_AllowsCompilerOwnedGrammarCacheWithoutTrustingItAsPayload()
    {
        using var temporary = new TemporaryDirectory();
        var toolchainDirectory = CreateToolchainDirectory(temporary.Path);
        var executablePath = Path.Combine(toolchainDirectory, "eidosc");
        await File.WriteAllTextAsync(executablePath, "binary");
        var manifest = CreateManifest(new InstalledFile("eidosc", 6, await HashAsync(executablePath)));
        await manifest.WriteAsync(toolchainDirectory, CancellationToken.None);
        var cacheDirectory = Path.Combine(toolchainDirectory, "cache");
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "grammar.bin"), "generated cache");

        Assert.True(await manifest.VerifyAsync(toolchainDirectory, AssetHash, CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "unexpected.bin"), "extra");
        Assert.False(await manifest.VerifyAsync(toolchainDirectory, AssetHash, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_RejectsDuplicateAndEscapingManifestPaths()
    {
        using var temporary = new TemporaryDirectory();
        var toolchainDirectory = CreateToolchainDirectory(temporary.Path);
        var executablePath = Path.Combine(toolchainDirectory, "eidosc");
        await File.WriteAllTextAsync(executablePath, "binary");
        var installedFile = new InstalledFile("eidosc", 6, await HashAsync(executablePath));
        var duplicate = CreateManifest(installedFile, installedFile);
        var escaping = CreateManifest(installedFile with { Path = "../outside" });

        Assert.False(await duplicate.VerifyAsync(toolchainDirectory, AssetHash, CancellationToken.None));
        Assert.False(await escaping.VerifyAsync(toolchainDirectory, AssetHash, CancellationToken.None));
    }

    private static InstallManifest CreateManifest(params InstalledFile[] files)
    {
        var platform = PlatformContext.Detect();
        var identity = ToolchainIdentity.Create(
            "0.4.0-alpha.2",
            platform.Rid,
            "test/source",
            "eidosc-v0.4.0-alpha.2",
            "bundle.zip",
            AssetHash,
            123);
        return new InstallManifest(
            InstallManifest.CurrentSchema,
            identity.Id,
            identity.ManifestSha256,
            "eidosc-v0.4.0-alpha.2",
            "0.4.0-alpha.2",
            platform.Rid,
            "test/source",
            "bundle.zip",
            AssetHash,
            123,
            DateTimeOffset.Parse("2026-07-12T00:00:00Z"),
            files);
    }

    private static string CreateToolchainDirectory(string root)
    {
        var manifest = CreateManifest();
        var directory = Path.Combine(root, manifest.ToolchainId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidosup-manifest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
