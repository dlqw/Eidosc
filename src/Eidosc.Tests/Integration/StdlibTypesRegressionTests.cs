using System.Text.RegularExpressions;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed partial class StdlibTypesRegressionTests
{
    private static readonly string[] RepresentativeStdlibFileNames =
    [
        "Alternative.eidos",
        "Applicative.eidos",
        "FFI.eidos",
        "Fn.eidos",
        "Functor.eidos",
        "GameMath.eidos",
        "Option.eidos",
        "Result.eidos",
        "Seq.eidos",
        "SeqBuilder.eidos",
        "Text.eidos",
        "Trait.eidos",
        "Traversable.eidos"
    ];

    private static readonly string[] StandaloneTypeCheckExclusions =
    [
        "AsyncRuntime.eidos",
        "CommandLine.eidos",
        "Promise.eidos",
        "TaskGroup.eidos"
    ];

    public static IEnumerable<object[]> StandaloneStdlibFiles() =>
        EidosFixtureInventory.StdlibPrecompiledFiles()
            .Where(static file => !StandaloneTypeCheckExclusions.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            .Select(static file => new object[] { file });

    public static IEnumerable<object[]> RepresentativeStdlibFiles() =>
        EidosFixtureInventory.StdlibPrecompiledFiles()
            .Where(static file => RepresentativeStdlibFileNames.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            .Select(static file => new object[] { file });

    public static IEnumerable<object[]> StdlibFixtureFiles()
    {
        foreach (var file in EidosFixtureInventory.StdlibFixtureFiles())
        {
            yield return [file];
        }
    }

    [Theory]
    [MemberData(nameof(StandaloneStdlibFiles))]
    public void StandaloneStdlibFile_TypesWithoutErrors(string filePath)
    {
        var result = CompileFile(filePath, CompilationPhase.Types);
        var errors = result.Diagnostics
            .Where(static diagnostic => diagnostic.Level == DiagnosticLevel.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
            .ToArray();

        Assert.True(result.Success, FormatDiagnostics(filePath, errors));
        Assert.Empty(errors);
        Assert.Equal(CompilationPhase.Types, result.CompletedPhase);
    }

    [Fact]
    public void StdlibRegressionFileLists_PointToExistingPrecompiledFiles()
    {
        var fileNames = EidosFixtureInventory.StdlibPrecompiledFiles()
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = RepresentativeStdlibFileNames
            .Concat(StandaloneTypeCheckExclusions)
            .Where(fileName => !fileNames.Contains(fileName))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(RepresentativeStdlibFiles))]
    public void RepresentativeStdlibFile_LlvmWithoutErrors(string filePath)
    {
        var result = CompileFile(filePath, CompilationPhase.Llvm);

        AssertLlvmSuccess(filePath, result);
    }

    [Theory]
    [MemberData(nameof(StdlibFixtureFiles))]
    public void StdlibFixture_LlvmWithoutErrors(string filePath)
    {
        var result = CompileFile(filePath, CompilationPhase.Llvm, noImplicitPrelude: false);
        var errors = result.Diagnostics
            .Where(static diagnostic => diagnostic.Level == DiagnosticLevel.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
            .ToArray();

        Assert.True(result.Success, FormatDiagnostics(filePath, errors));
        Assert.Empty(errors);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.NotNull(result.LlvmModule);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        AssertLlvmIrHasNoVoidStorage(filePath, result.LlvmIrText);
    }

    private static CompilationResult CompileFile(
        string filePath,
        CompilationPhase phase,
        bool noImplicitPrelude = true)
    {
        var source = File.ReadAllText(filePath);
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = filePath,
            StopAtPhase = phase,
            NoImplicitPrelude = noImplicitPrelude,
            UseColors = false
        }).Run();
    }

    private static string FormatDiagnostics(string filePath, IReadOnlyList<string> errors)
    {
        return $"{Path.GetFileName(filePath)}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}";
    }

    private static void AssertLlvmSuccess(string filePath, CompilationResult result)
    {
        var errors = result.Diagnostics
            .Where(static diagnostic => diagnostic.Level == DiagnosticLevel.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
            .ToArray();

        Assert.True(result.Success, FormatDiagnostics(filePath, errors));
        Assert.Empty(errors);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.NotNull(result.LlvmModule);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        AssertLlvmIrHasNoVoidStorage(filePath, result.LlvmIrText);
    }

    private static void AssertLlvmIrHasNoVoidStorage(string filePath, string? llvmIr)
    {
        Assert.False(string.IsNullOrWhiteSpace(llvmIr), $"{Path.GetFileName(filePath)} produced empty LLVM IR.");

        var match = VoidStorageRegex().Match(llvmIr);
        Assert.False(
            match.Success,
            $"{Path.GetFileName(filePath)} contains invalid LLVM storage type: {match.Value}");
    }

    [GeneratedRegex(@"(?:alloca|load|store)\s+(?:void\b|\{[^\r\n}]*\bvoid\b[^\r\n}]*\})", RegexOptions.CultureInvariant)]
    private static partial Regex VoidStorageRegex();
}
