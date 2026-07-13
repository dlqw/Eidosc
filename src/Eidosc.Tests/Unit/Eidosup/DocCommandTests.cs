using System.Security.Cryptography;
using Eidosup.Commands;
using Eidosup.Diagnostics;
using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class DocCommandTests
{
    [Fact]
    public async Task ResolveAsync_ResolvesVersionMatchedOfflineTopic()
    {
        using var fixture = await DocumentationFixture.CreateAsync("index.md", "0.4.0-alpha.3");

        var document = await DocCommand.ResolveAsync(fixture.Root, "index", CancellationToken.None);

        Assert.Equal("index", document.Topic);
        Assert.Equal("0.4.0-alpha.3", document.EidoscVersion);
        Assert.Equal(Path.Combine(fixture.Root, "docs", "index.md"), document.Path);
    }

    [Theory]
    [InlineData("../outside.md", "0.4.0-alpha.3")]
    [InlineData("index.md", "0.4.0-alpha.2")]
    public async Task ResolveAsync_RejectsEscapingOrVersionMismatchedIndex(string path, string indexVersion)
    {
        using var fixture = await DocumentationFixture.CreateAsync(path, indexVersion);

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            DocCommand.ResolveAsync(fixture.Root, "index", CancellationToken.None));

        Assert.Equal(EidosupErrorCode.ToolchainUnavailable, exception.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsUnknownIndexFields()
    {
        using var fixture = await DocumentationFixture.CreateAsync("index.md", "0.4.0-alpha.3");
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "docs", "index.json"),
            "{\"schema\":1,\"eidoscVersion\":\"0.4.0-alpha.3\",\"topics\":{\"index\":\"index.md\"},\"unknown\":true}");

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            DocCommand.ResolveAsync(fixture.Root, "index", CancellationToken.None));

        Assert.Equal(EidosupErrorCode.ToolchainUnavailable, exception.Code);
    }

    private sealed class DocumentationFixture(string root) : IDisposable
    {
        public string Root { get; } = root;

        public static async Task<DocumentationFixture> CreateAsync(string topicPath, string indexVersion)
        {
            var root = Path.Combine(Path.GetTempPath(), $"eidosup-doc-{Guid.NewGuid():N}");
            var docs = Path.Combine(root, "docs");
            Directory.CreateDirectory(docs);
            await File.WriteAllTextAsync(Path.Combine(docs, "index.md"), "# Eidos docs");
            await File.WriteAllTextAsync(
                Path.Combine(docs, "index.json"),
                $"{{\"schema\":1,\"eidoscVersion\":\"{indexVersion}\",\"topics\":{{\"index\":\"{topicPath}\"}}}}");
            var manifest = new InstallManifest(
                InstallManifest.CurrentSchema,
                "test-toolchain",
                new string('a', 64),
                new string('b', 64),
                "eidos-toolchain-v0.4.0-alpha.3-test.json",
                new string('c', 64),
                "eidosc-v0.4.0-alpha.3",
                "0.4.0-alpha.3",
                "win-x64",
                "test/source",
                "complete",
                [],
                [],
                [new InstalledComponent("eidos-docs", "eidos-docs", "0.4.0-alpha.3", false, null, ["docs/index.json", "docs/index.md"])],
                [],
                [new InstalledArtifact("bundle.zip", new string('d', 64), 1)],
                EidosupToolchainTestFixture.FixedTime,
                [
                    await InstalledFileAsync(root, "docs/index.json"),
                    await InstalledFileAsync(root, "docs/index.md")
                ]);
            await manifest.WriteAsync(root, CancellationToken.None);
            return new DocumentationFixture(root);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static async Task<InstalledFile> InstalledFileAsync(string root, string relativePath)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var bytes = await File.ReadAllBytesAsync(path);
            return new InstalledFile(
                relativePath,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}
