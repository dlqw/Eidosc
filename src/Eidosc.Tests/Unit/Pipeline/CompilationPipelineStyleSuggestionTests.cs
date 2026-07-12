using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public class CompilationPipelineStyleSuggestionTests
{
    [Fact]
    public void Run_StyleSuggestionsEnabled_AppendsHelpDiagnosticsToCompilationResult()
    {
        var result = RunStylePipeline(emitStyleSuggestions: true);

        Assert.True(result.Success);

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "S1002");
        Assert.Equal(Eidosc.Diagnostic.DiagnosticLevel.Help, diagnostic.Level);
        Assert.Contains(diagnostic.Suggestions, suggestion => suggestion.Replacement == "append(a, b)");
        Assert.Contains(diagnostic.Suggestions, suggestion => suggestion.Replacement == "a.append(b)");
    }

    [Fact]
    public void Run_StyleSuggestionsEnabled_AppendsPatternGuardBranchHintForPredicateSplit()
    {
        const string source = """
is_digit_code :: Int -> Bool
{
    code => code >= 48 && code <= 57
}

is_hex_digit_code :: Int -> Bool
{
    code => is_digit_code(code) ||
        (code >= 97 && code <= 102) ||
        (code >= 65 && code <= 70)
}
""";

        var result = RunStylePipeline(source, emitStyleSuggestions: true);

        Assert.True(result.Success, FormatDiagnostics(result));
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "S1004");

        Assert.Equal(Eidosc.Diagnostic.DiagnosticLevel.Help, diagnostic.Level);
        Assert.Contains("pattern guard", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(diagnostic.Metadata.TryGetValue("style", out var style));
        Assert.Equal("pattern-guard-branches", style);
    }

    [Fact]
    public void Run_StyleSuggestionsDisabled_DoesNotChangeCompilationDiagnostics()
    {
        var result = RunStylePipeline(emitStyleSuggestions: false);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code is "S1001" or "S1002" or "S1003" or "S1004");
    }

    [Fact]
    public void Run_StyleSuggestionsDisabled_DoesNotAppendPatternGuardBranchHint()
    {
        const string source = """
is_hex_digit_code :: Int -> Bool
{
    code => (code >= 48 && code <= 57) ||
        (code >= 97 && code <= 102)
}
""";

        var result = RunStylePipeline(source, emitStyleSuggestions: false);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "S1004");
    }

    [Fact]
    public void Run_StyleSuggestionsEnabled_DoesNotAppendNestedUnaryChainRewrite()
    {
        const string source = """
trim :: Int -> Int { value => value }
normalize :: Int -> Int { value => value }
wrap :: Int -> Int { value => value }

input :: 1;
cleaned :: wrap(normalize(trim(input)));
""";

        var result = RunStylePipeline(source, emitStyleSuggestions: true);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Code == "S1001");
    }

    [Fact]
    public void Run_StyleSuggestionsEnabled_DoesNotAppendBacktickInfixRewriteForGroupedCall()
    {
        const string source = """
combine :: Int -> Int -> Int { (x, y) => x + y }

a :: 1;
b :: 2;
combined :: combine(a, b);
""";

        var result = RunStylePipeline(source, emitStyleSuggestions: true);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "S1003");
    }

    [Fact]
    public void Run_StyleSuggestionsEnabled_LocalCurriedCallableOnlyOffersGroupedRewrite()
    {
        const string source = """
use_local :: Int -> Int {
    seed => {
        local := a => b => a + b;
        local(seed)(2)
    }
}
""";

        var result = RunStylePipeline(source, emitStyleSuggestions: true);

        Assert.True(result.Success, FormatDiagnostics(result));
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "S1002");
        var replacements = diagnostic.Suggestions.Select(suggestion => suggestion.Replacement).ToArray();

        Assert.Contains("local(seed, 2)", replacements);
        Assert.DoesNotContain("seed.local(2)", replacements);
        Assert.DoesNotContain("seed `local` 2", replacements);
    }

    [Fact]
    public void Run_StyleSuggestionsEnabled_IgnoresImportedStdlibAstSpans()
    {
        const string source = """
append :: Int -> Int -> Int { (x, y) => x + y }

a :: 1;
b :: 2;
combined :: append(a)(b);
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pipeline_style_suggestions.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false,
            EmitStyleSuggestions = true
        }).Run();

        Assert.True(result.Success);
        Assert.Single(result.Diagnostics, item => item.Code == "S1002");
        Assert.DoesNotContain(result.Diagnostics, item =>
            item.Code == "S1001" &&
            item.Suggestions.Any(suggestion => suggestion.Replacement?.Contains(").", StringComparison.Ordinal) == true));
    }

    private static CompilationResult RunStylePipeline(bool emitStyleSuggestions)
    {
        const string source = """
append :: Int -> Int -> Int { (x, y) => x + y }

a :: 1;
b :: 2;
combined :: append(a)(b);
""";

        return RunStylePipeline(source, emitStyleSuggestions);
    }

    private static CompilationResult RunStylePipeline(string source, bool emitStyleSuggestions)
    {

        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pipeline_style_suggestions.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false,
            EmitStyleSuggestions = emitStyleSuggestions
        }).Run();
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}"));
    }

    private static void ConfigureProjectContext(string inputFile, CompilationOptions options)
    {
        var projectDirectory = FindNearestProjectDirectory(inputFile);
        if (projectDirectory == null)
        {
            return;
        }

        var resolvedTarget = EidosProjectGraphResolver.ResolveTarget(projectDirectory);
        options.ImportSearchRoots = resolvedTarget.EffectiveSearchRoots;
        options.PackageImportRoots = resolvedTarget.PackageImportRoots;
    }

    private static string? FindNearestProjectDirectory(string inputFile)
    {
        for (var directory = Path.GetDirectoryName(inputFile);
             !string.IsNullOrWhiteSpace(directory);
             directory = Directory.GetParent(directory)?.FullName)
        {
            if (File.Exists(Path.Combine(directory, EidosProjectConfigurationLoader.DefaultFileName)))
            {
                return directory;
            }
        }

        return null;
    }
}
