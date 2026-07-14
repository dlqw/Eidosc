using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class IdeSemanticSnapshotTests
{
    [Fact]
    public void Build_ImportModule_OccurrenceTargetsImportedModuleDefinition()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ide_import_definition_{Guid.NewGuid():N}");
        var appDir = Path.Combine(tempDir, "app");
        var packageRoot = Path.Combine(tempDir, "pkg", "src");
        var entryFile = Path.Combine(appDir, "Main.eidos");
        var featureFile = Path.Combine(packageRoot, "Feature.eidos");

        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(featureFile, """
Feature :: module {
}
""");

        const string source = """
Main :: module {
    import pkg.Feature
}
""";
        File.WriteAllText(entryFile, source);

        try
        {
            var result = new CompilationPipeline(source, new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["pkg"] = [packageRoot]
                }
            }).Run();

            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            var snapshot = IdeSemanticSnapshotBuilder.Build(result);
            var importOccurrence = Assert.Single(snapshot.Occurrences, occurrence =>
                occurrence.Source == "ImportDecl" &&
                occurrence.Span?.FilePath == entryFile &&
                occurrence.Span.StartLine == 1 &&
                occurrence.Span.StartCharacter == 15 &&
                occurrence.Span.EndCharacter == 22);

            var targetSymbol = Assert.Single(snapshot.Symbols, symbol => symbol.SymbolId == importOccurrence.SymbolId);
            Assert.Equal("Feature", targetSymbol.Name);
            Assert.Equal("module", targetSymbol.Kind);
            Assert.Equal(Path.GetFullPath(featureFile), targetSymbol.Span?.FilePath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
