using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Symbols;
using Xunit;

namespace Eidosc.Tests.Fixtures;

public sealed class CompilationHelper
{
    private readonly string _source;
    private readonly CompilationOptions _options;

    public string SourceText => _source;

    public SymbolTable SymbolTable { get; } = new();

    private CompilationHelper(string source, CompilationOptions options)
    {
        _source = source;
        _options = options;
    }

    public static CompilationHelper Create(string source) => Source(source);

    public static CompilationHelper Source(string source, string inputFile = "inline.eidos") =>
        new(
            source,
            new CompilationOptions
            {
                InputFile = inputFile,
                AllowVirtualInputFile = true,
                LanguageVersion = EidosLanguageVersions.Current,
                UseColors = false
            });

    public static CompilationHelper Fixture(string relativePath, bool isolateSingleFile = false)
    {
        var source = TestSourceLoader.Load(relativePath);
        var inputFile = isolateSingleFile
            ? Path.GetFileName(relativePath)
            : TestSourceLoader.GetFullPath(relativePath);

        return new CompilationHelper(
            source,
            new CompilationOptions
            {
                InputFile = inputFile,
                LanguageVersion = TestSourceLoader.GetLanguageVersion(relativePath),
                UseColors = false
            });
    }

    public CompilationHelper ToPhase(CompilationPhase phase)
    {
        _options.StopAtPhase = phase;
        return this;
    }

    public CompilationHelper WithInputFile(string inputFile, bool allowVirtual = true)
    {
        _options.InputFile = inputFile;
        _options.AllowVirtualInputFile = allowVirtual;
        return this;
    }

    public CompilationHelper WithOptions(Action<CompilationOptions> configure)
    {
        configure(_options);
        return this;
    }

    public string GetSource() => _source;

    public CompilationResult Run() => new CompilationPipeline(_source, _options).Run();

    public CompilationResult ShouldSucceed()
    {
        var result = Run();
        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        return result;
    }

    public CompilationResult ShouldCompletePhaseWithoutErrors(CompilationPhase expectedPhase)
    {
        var result = Run();
        Assert.Equal(expectedPhase, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        return result;
    }

    public CompilationResult ShouldReport(params string[] expectedCodes)
    {
        var result = Run();
        var errors = result.Diagnostics.Where(static diagnostic => diagnostic.Level == DiagnosticLevel.Error).ToArray();
        Assert.NotEmpty(errors);

        foreach (var expectedCode in expectedCodes)
        {
            Assert.Contains(errors, diagnostic => string.Equals(diagnostic.Code, expectedCode, StringComparison.Ordinal));
        }

        return result;
    }

    private static string FormatDiagnostics(CompilationResult result) => TestDiagnosticFormatter.Format(result);
}
