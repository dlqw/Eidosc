using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public class ModuleExportResolutionTests
{
    [Fact]
    public void Run_ExplicitExportModule_HidesNonExportedSelectiveImportMember()
    {
        var result = RunWorkspaceCompilation(
            "main.eidos",
            ("Lib/Api.eidos", """
Lib.Api :: module
{
    export public_id :: Int -> Int
    {
        x => x
    }

    secret :: Int -> Int
    {
        x => x + 1
    }
}
"""),
            ("main.eidos", """
import Lib.Api.{secret}

run :: Int -> Int
{
    x => secret(x)
}
"""));

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ExplicitExportModule_HidesNonExportedQualifiedPath()
    {
        var result = RunWorkspaceCompilation(
            "main.eidos",
            ("Lib/Api.eidos", """
Lib.Api :: module
{
    export public_id :: Int -> Int
    {
        x => x
    }

    secret :: Int -> Int
    {
        x => x + 1
    }
}
"""),
            ("main.eidos", """
import Lib.Api

run :: Int -> Int
{
    x => Api.secret(x)
}
"""));

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Api.secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ExplicitExportModule_AllowsExportedSelectiveImportMember()
    {
        var result = RunWorkspaceCompilation(
            "main.eidos",
            ("Lib/Api.eidos", """
Lib.Api :: module
{
    export public_id :: Int -> Int
    {
        x => x
    }

    secret :: Int -> Int
    {
        x => x + 1
    }
}
"""),
            ("main.eidos", """
import Lib.Api.{public_id}

run :: Int -> Int
{
    x => public_id(x)
}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Run_ExplicitExportModule_AllowsOverloadGroupSelectiveImport()
    {
        var result = RunWorkspaceCompilationAtPhase(
            CompilationPhase.Types,
            "main.eidos",
            ("Lib/Api.eidos", """
Lib.Api :: module
{
    export parse :: Int -> Int
    {
        value => value
    }

    export parse :: String -> Int
    {
        _ => 2
    }
}
"""),
            ("main.eidos", """
import Lib.Api.{parse}

run :: Unit -> Int
{
    _ => parse("ok") + parse(1)
}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Run_ExplicitExportModule_AllowsOverloadGroupQualifiedPath()
    {
        var result = RunWorkspaceCompilationAtPhase(
            CompilationPhase.Types,
            "main.eidos",
            ("Lib/Api.eidos", """
Lib.Api :: module
{
    export parse :: Int -> Int
    {
        value => value
    }

    export parse :: String -> Int
    {
        _ => 2
    }
}
"""),
            ("main.eidos", """
import Lib.Api

run :: Unit -> Int
{
    _ => Api.parse("ok") + Api.parse(1)
}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Run_ExportImport_ReexportsSelectiveEffectAliasForImportAndQualifiedPath()
    {
        var result = RunWorkspaceCompilation(
            "main.eidos",
            ("Cap/Io.eidos", """
Cap.Io :: module
{
    export Writer :: effect;

    export write :: String -> Int need Writer
    {
        _ => 0
    }
}
"""),
            ("Cap/Facade.eidos", """
Cap.Facade :: module
{
    export import Cap.Io.{Writer as W, write}
}
"""),
            ("main.eidos", """
import Cap.Facade
import Cap.Facade.{W}

run_short :: String -> Int need W
{
    _ => 0
}

run_qualified :: String -> Int need Facade.W
{
    text => Facade.write(text)
}

main :: Unit -> Int need Facade.W
{
    _ => run_short("hello") + run_qualified("hello")
}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Run_SelectiveImportRuntimeValueAlias_Uppercase_ResolvesAndReportsStyle()
    {
        var result = RunWorkspaceCompilationAtPhase(
            CompilationPhase.Types,
            "main.eidos",
            ("Lib/Api.eidos", """
Lib.Api :: module
{
    export public_id :: Int -> Int
    {
        x => x
    }
}
"""),
            ("main.eidos", """
import Lib.Api.{public_id as PublicId}

run :: Int -> Int
{
    x => PublicId(x)
}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
        var style = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Code == "S1101" && diagnostic.Message.Contains("'PublicId'", StringComparison.Ordinal));
        Assert.Contains(style.Suggestions, suggestion => suggestion.Replacement == "public_id");
    }

    [Fact]
    public void Run_SelectiveImportCompileTimeAlias_Lowercase_ResolvesAndReportsStyle()
    {
        var result = RunWorkspaceCompilationAtPhase(
            CompilationPhase.Types,
            "main.eidos",
            ("Lib/Api.eidos", """
Lib.Api :: module
{
    export Box :: type { Box:: type(Int) }
}
"""),
            ("main.eidos", """
import Lib.Api.{Box as box}

run :: box -> box
{
    value => value
}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
        var style = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Code == "S1102" && diagnostic.Message.Contains("'box'", StringComparison.Ordinal));
        Assert.Contains(style.Suggestions, suggestion => suggestion.Replacement == "Box");
    }

    [Fact]
    public void Run_SelectiveImportsWithSameSpelling_ResolveByExpectedSemanticCategory()
    {
        var result = RunWorkspaceCompilationAtPhase(
            CompilationPhase.Types,
            "main.eidos",
            ("Lib/Types.eidos", """
Lib.Types :: module
{
    export Box :: type = Int;
}
"""),
            ("Lib/Values.eidos", """
Lib.Values :: module
{
    export identity :: Int -> Int
    {
        value => value
    }
}
"""),
            ("main.eidos", """
import Lib.Types.{Box as entity}
import Lib.Values.{identity as entity}

run :: entity -> entity
{
    value => entity(value)
}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
        var style = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Code == "S1102" && diagnostic.Message.Contains("'entity'", StringComparison.Ordinal));
        Assert.Contains(style.Suggestions, suggestion => suggestion.Replacement == "Entity");
    }

    [Fact]
    public void Run_SelectiveEffectAlias_ResolvesByEffectIdentityAndReportsStyle()
    {
        var result = RunWorkspaceCompilationAtPhase(
            CompilationPhase.Types,
            "main.eidos",
            ("Cap/Io.eidos", """
Cap.Io :: module
{
    export writer :: effect;
}
"""),
            ("main.eidos", """
import Cap.Io.{writer as WriterCap}

run :: Unit -> Unit need WriterCap
{
    value => value
}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
        var style = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Code == "S1101" && diagnostic.Message.Contains("'WriterCap'", StringComparison.Ordinal));
        Assert.Contains(style.Suggestions, suggestion => suggestion.Replacement == "writer_cap");
    }

    [Fact]
    public void Run_ModuleAlias_ResolvesIndependentlyOfSpellingAndReportsStyle()
    {
        var result = RunWorkspaceCompilationAtPhase(
            CompilationPhase.Types,
            "main.eidos",
            ("Lib/Api.eidos", """
Lib.Api :: module
{
    export public_id :: Int -> Int
    {
        value => value
    }
}
"""),
            ("main.eidos", """
APIClient :: import Lib.Api;

run :: Int -> Int
{
    value => APIClient.public_id(value)
}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
        var style = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Code == "S1102" && diagnostic.Message.Contains("'APIClient'", StringComparison.Ordinal));
        Assert.Contains(style.Suggestions, suggestion => suggestion.Replacement == "ApiClient");
    }

    [Fact]
    public void Run_ExplicitExportModule_ReexportedEquivalentSignaturesRemainAmbiguousAtUseSite()
    {
        var result = RunWorkspaceCompilationAtPhase(
            CompilationPhase.Types,
            "main.eidos",
            ("Core/Base.eidos", """
Core.Base :: module
{
    bar :: Int -> Int
    {
        x => x
    }
}
"""),
            ("Demo/Facade.eidos", """
Demo.Facade :: module
{
    export foo :: Int -> Int
    {
        x => x
    }

    export import Core.Base.{bar as foo}
}
"""),
            ("main.eidos", """
import Demo.Facade.{foo}

run :: Unit -> Int
{
    _ => foo(1)
}
"""));

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Ambiguous callable overload 'foo'", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ExplicitExportModule_RejectsFunctionAndValueWithSameExportName()
    {
        var result = RunWorkspaceCompilation(
            "Demo/Facade.eidos",
            ("Demo/Facade.eidos", """
Demo.Facade :: module
{
    export foo :: Int -> Int
    {
        x => x
    }

    export foo :: Int = 1;
}
"""));

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3001" &&
                          diagnostic.Message.Contains("function overload group", StringComparison.Ordinal));
    }

    private static CompilationResult RunWorkspaceCompilation(
        string entryRelativePath,
        params (string RelativePath, string Source)[] files)
    {
        return RunWorkspaceCompilationAtPhase(CompilationPhase.Namer, entryRelativePath, files);
    }

    private static CompilationResult RunWorkspaceCompilationAtPhase(
        CompilationPhase stopAtPhase,
        string entryRelativePath,
        params (string RelativePath, string Source)[] files)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_exports_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var (relativePath, source) in files)
            {
                var fullPath = Path.Combine(
                    tempDir,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullPath, source);
            }

            var entryFile = Path.Combine(
                tempDir,
                entryRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = stopAtPhase,
                ImportSearchRoots = [tempDir],
                UseColors = false,
                EmitStyleSuggestions = stopAtPhase >= CompilationPhase.Types
            }).Run();

            return result;
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
