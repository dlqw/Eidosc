using Eidosc.Cli.Lsp;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspDependencyFingerprintCacheTests
{
    [Fact]
    public void GetDirectoryFingerprint_ReusesCachedRootFingerprint()
    {
        var tempDir = CreateTempDirectory();
        var sourceFile = Path.Combine(tempDir, "Main.eidos");
        File.WriteAllText(sourceFile, "x :: 1;");

        try
        {
            using var cache = new LspDependencyFingerprintCache();

            var first = cache.GetDirectoryFingerprint(tempDir, Directory.GetCurrentDirectory());
            var second = cache.GetDirectoryFingerprint(tempDir, Directory.GetCurrentDirectory());

            Assert.Equal(first, second);
            Assert.Equal(1, cache.DirectoryScanCount);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void InvalidateDirectory_ForcesNextDirectoryScan()
    {
        var tempDir = CreateTempDirectory();
        var sourceFile = Path.Combine(tempDir, "Main.eidos");
        File.WriteAllText(sourceFile, "x :: 1;");

        try
        {
            using var cache = new LspDependencyFingerprintCache();

            var first = cache.GetDirectoryFingerprint(tempDir, Directory.GetCurrentDirectory());
            File.WriteAllText(sourceFile, "x :: 12;");
            cache.InvalidateDirectory(tempDir);
            var second = cache.GetDirectoryFingerprint(tempDir, Directory.GetCurrentDirectory());

            Assert.NotEqual(first, second);
            Assert.Equal(2, cache.DirectoryScanCount);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void GetDirectoryFingerprint_ReportsMissingRootWithoutScanning()
    {
        using var cache = new LspDependencyFingerprintCache();
        var missingRoot = Path.Combine(Path.GetTempPath(), $"eidosc_missing_{Guid.NewGuid():N}");

        var fingerprint = cache.GetDirectoryFingerprint(missingRoot, Directory.GetCurrentDirectory());

        Assert.Contains("missing-root:", fingerprint, StringComparison.Ordinal);
        Assert.Equal(0, cache.DirectoryScanCount);
    }

    [Fact]
    public void GetIndexedFiles_ReusesCachedRootIndex()
    {
        var tempDir = CreateTempDirectory();
        var sourceFile = Path.Combine(tempDir, "Main.eidos");
        var nestedDir = Path.Combine(tempDir, "Nested");
        Directory.CreateDirectory(nestedDir);
        var nestedFile = Path.Combine(nestedDir, "Helper.eidos");
        File.WriteAllText(sourceFile, "x :: 1;");
        File.WriteAllText(nestedFile, "y :: 2;");

        try
        {
            using var cache = new LspDependencyFingerprintCache();

            var first = cache.GetIndexedFiles(tempDir, Directory.GetCurrentDirectory());
            var second = cache.GetIndexedFiles(tempDir, Directory.GetCurrentDirectory());

            Assert.Equal(2, first.Length);
            Assert.Equal(first, second);
            Assert.Equal(1, cache.DirectoryScanCount);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_lsp_fingerprint_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static void DeleteTempDirectory(string tempDir)
    {
        const int maxAttempts = 12;
        var delayMs = 25;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 500);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 500);
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
        }
    }
}
