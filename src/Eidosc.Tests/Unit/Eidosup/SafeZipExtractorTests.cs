using System.IO.Compression;
using System.Security.Cryptography;
using Eidosup.Diagnostics;
using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class SafeZipExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ExtractsFilesAndRecordsContentDigests()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "bundle.zip");
        CreateArchive(archivePath, ("eidosc", "compiler"), ("runtime/runtime.h", "header"));
        var destination = Path.Combine(temporary.Path, "stage");

        var result = await new SafeZipExtractor().ExtractAsync(archivePath, destination, CancellationToken.None);

        Assert.Equal(2, result.Files.Count);
        var executable = Assert.Single(result.Files, file => file.Path == "eidosc");
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData("compiler"u8.ToArray())).ToLowerInvariant(),
            executable.Sha256);
        Assert.Equal("header", await File.ReadAllTextAsync(Path.Combine(destination, "runtime", "runtime.h")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    [InlineData("safe/../../escape.txt")]
    public async Task ExtractAsync_RejectsUnsafePaths(string entryName)
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "unsafe.zip");
        CreateArchive(archivePath, (entryName, "bad"));

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            new SafeZipExtractor().ExtractAsync(
                archivePath,
                Path.Combine(temporary.Path, "stage"),
                CancellationToken.None));

        Assert.Equal(EidosupErrorCode.UnsafeArchive, exception.Code);
        Assert.False(File.Exists(Path.Combine(temporary.Path, "escape.txt")));
    }

    [Fact]
    public async Task ExtractAsync_RejectsSymbolicLinks()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "link.zip");
        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link");
            entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("target");
        }

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            new SafeZipExtractor().ExtractAsync(
                archivePath,
                Path.Combine(temporary.Path, "stage"),
                CancellationToken.None));

        Assert.Equal(EidosupErrorCode.UnsafeArchive, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_RejectsFileDirectoryPathCollisionsBeforeWriting()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "collision.zip");
        CreateArchive(archivePath, ("runtime", "file"), ("runtime/header.h", "header"));
        var destination = Path.Combine(temporary.Path, "stage");

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            new SafeZipExtractor().ExtractAsync(archivePath, destination, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.UnsafeArchive, exception.Code);
        Assert.Empty(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExtractAsync_RejectsReservedInstallManifestPath()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "reserved.zip");
        CreateArchive(archivePath, (InstallManifest.FileName, "untrusted"));

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            new SafeZipExtractor().ExtractAsync(
                archivePath,
                Path.Combine(temporary.Path, "stage"),
                CancellationToken.None));

        Assert.Equal(EidosupErrorCode.UnsafeArchive, exception.Code);
    }

    [Fact]
    public async Task ExtractAsync_RejectsExcessivePathLength()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "long-path.zip");
        CreateArchive(archivePath, ($"{new string('a', 256)}/file", "content"));

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            new SafeZipExtractor().ExtractAsync(
                archivePath,
                Path.Combine(temporary.Path, "stage"),
                CancellationToken.None));

        Assert.Equal(EidosupErrorCode.UnsafeArchive, exception.Code);
    }

    private static void CreateArchive(string path, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name, CompressionLevel.SmallestSize);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(item.Content);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidosup-zip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
