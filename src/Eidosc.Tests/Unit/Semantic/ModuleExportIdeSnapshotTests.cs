using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public class ModuleExportIdeSnapshotTests
{
    [Fact]
    public void Build_IncludesExportKeywordCompletion()
    {
        const string source = """
id :: Int -> Int
{
    x => x
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_snapshot_export_keyword.eidos",
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        Assert.Contains(snapshot.Completions, item => item.Label == "export" && item.Kind == "keyword");
    }

    [Fact]
    public void Build_ReexportedQualifiedEffectAlias_ExposesEffectAliasCompletions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ide_export_snapshot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var capDir = Path.Combine(tempDir, "Cap");
        Directory.CreateDirectory(capDir);

        var ioFile = Path.Combine(capDir, "Io.eidos");
        var facadeFile = Path.Combine(capDir, "Facade.eidos");
        var entryFile = Path.Combine(tempDir, "main.eidos");

        const string ioSource = """
Cap.Io :: module {
    export Writer :: effect;

    export write :: String -> Int need Writer
    {
        _ => 0
    }
}
""";

        const string facadeSource = """
Cap.Facade :: module {
    export import Cap.Io.{Writer as W, write}
}
""";

        const string entrySource = """
import Cap.Facade

run :: String -> Int need Facade.W
{
    text => Facade.write(text)
}

main :: Unit -> Int need Facade.W
{
    _ => run("hello")
}
""";

        File.WriteAllText(ioFile, ioSource);
        File.WriteAllText(facadeFile, facadeSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(entrySource, new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                ImportSearchRoots = [tempDir],
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));

            var snapshot = IdeSemanticSnapshotBuilder.Build(result);
            Assert.Contains(
                snapshot.Completions,
                item => item.Label == "Facade.W" &&
                        item.Detail.Contains("qualified", StringComparison.Ordinal));
            Assert.Contains(
                snapshot.Completions,
                item => item.Label == "Cap.Facade.W" &&
                        item.Detail.Contains("qualified", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}"));
    }
}
