using System.Security.Cryptography;
using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class InstallManifestTests
{
    private const string AssetHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task VerifyAsync_RehashesEveryFileAndRejectsUnexpectedContent()
    {
        using var temporary = new TemporaryDirectory();
        var executablePath = Path.Combine(temporary.Path, "eidosc");
        await File.WriteAllTextAsync(executablePath, "binary");
        var manifest = CreateManifest(new InstalledFile("eidosc", 6, await HashAsync(executablePath)));
        await manifest.WriteAsync(temporary.Path, CancellationToken.None);

        Assert.True(await manifest.VerifyAsync(temporary.Path, AssetHash, CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "unexpected.dll"), "extra");
        Assert.False(await manifest.VerifyAsync(temporary.Path, AssetHash, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_RejectsDuplicateAndEscapingManifestPaths()
    {
        using var temporary = new TemporaryDirectory();
        var executablePath = Path.Combine(temporary.Path, "eidosc");
        await File.WriteAllTextAsync(executablePath, "binary");
        var installedFile = new InstalledFile("eidosc", 6, await HashAsync(executablePath));
        var duplicate = CreateManifest(installedFile, installedFile);
        var escaping = CreateManifest(installedFile with { Path = "../outside" });

        Assert.False(await duplicate.VerifyAsync(temporary.Path, AssetHash, CancellationToken.None));
        Assert.False(await escaping.VerifyAsync(temporary.Path, AssetHash, CancellationToken.None));
    }

    private static InstallManifest CreateManifest(params InstalledFile[] files) => new(
        InstallManifest.CurrentSchema,
        "eidosc-v0.4.0-alpha.2",
        "0.4.0-alpha.2",
        "test-rid",
        "test/source",
        "bundle.zip",
        AssetHash,
        123,
        DateTimeOffset.Parse("2026-07-12T00:00:00Z"),
        files);

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
