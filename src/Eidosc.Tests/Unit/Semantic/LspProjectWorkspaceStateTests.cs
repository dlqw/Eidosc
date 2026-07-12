using Eidosc.Cli.Commands;
using Eidosc.Cli.Lsp;
using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspProjectWorkspaceStateTests
{
    [Fact]
    public void Create_NormalizesRootsAndIndexesEidosFiles()
    {
        var tempDir = CreateTempDirectory();
        var srcDir = Path.Combine(tempDir, "src");
        var importDir = Path.Combine(tempDir, "imports");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(importDir);
        var sourceFile = Path.Combine(srcDir, "Main.eidos");
        var importFile = Path.Combine(importDir, "Dep.eidos");
        File.WriteAllText(sourceFile, "main :: 1;");
        File.WriteAllText(importFile, "dep :: 2;");

        try
        {
            using var cache = new LspDependencyFingerprintCache();
            var importResolution = new ProjectImportSearchResolution(
                [srcDir],
                [importDir],
                [srcDir, importDir],
                Path.Combine(tempDir, "eidos.toml"),
                UsesExplicitImportRoots: false);
            var input = new ProjectCommandInputResolution(sourceFile, importResolution, null);

            var state = LspProjectWorkspaceState.Create(
                sourceFile,
                input,
                new Dictionary<string, string[]>(StringComparer.Ordinal),
                [],
                cache);

            Assert.Contains(Path.GetFullPath(srcDir), state.Roots);
            Assert.Contains(Path.GetFullPath(importDir), state.Roots);
            Assert.Contains(Path.GetFullPath(sourceFile), state.IndexedFiles);
            Assert.Contains(Path.GetFullPath(importFile), state.IndexedFiles);
            Assert.Equal(2, cache.DirectoryScanCount);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void Create_UsesProjectLevelKeyAcrossFilesInSameWorkspace()
    {
        var tempDir = CreateTempDirectory();
        var srcDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(srcDir);
        var firstFile = Path.Combine(srcDir, "Main.eidos");
        var secondFile = Path.Combine(srcDir, "Other.eidos");
        File.WriteAllText(firstFile, "main :: 1;");
        File.WriteAllText(secondFile, "other :: 2;");

        try
        {
            using var cache = new LspDependencyFingerprintCache();
            var importResolution = new ProjectImportSearchResolution(
                [srcDir],
                [],
                [srcDir],
                Path.Combine(tempDir, "eidos.toml"),
                UsesExplicitImportRoots: false);
            var firstInput = new ProjectCommandInputResolution(firstFile, importResolution, null);
            var secondInput = new ProjectCommandInputResolution(secondFile, importResolution, null);

            var first = LspProjectWorkspaceState.Create(
                firstFile,
                firstInput,
                new Dictionary<string, string[]>(StringComparer.Ordinal),
                [],
                cache);
            var second = LspProjectWorkspaceState.Create(
                secondFile,
                secondInput,
                new Dictionary<string, string[]>(StringComparer.Ordinal),
                [],
                cache);

            Assert.Equal(first.Key, second.Key);
            Assert.Equal(2, first.IndexedFiles.Length);
            Assert.Equal(first.IndexedFiles, second.IndexedFiles);
            Assert.Equal(1, cache.DirectoryScanCount);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_lsp_workspace_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static void DeleteTempDirectory(string tempDir)
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
