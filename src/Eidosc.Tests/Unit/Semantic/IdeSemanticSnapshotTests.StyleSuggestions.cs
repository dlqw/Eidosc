using Eidosc.Symbols;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class IdeSemanticSnapshotTests
{
    [Fact]
    public void Build_CurriedPrefixCall_OffersFluentAndGroupedStyleFixes()
    {
        const string source = """
combined :: append(a)(b);
""";

        var diagnostics = BuildRawStyleDiagnostics(source);

        var diagnostic = Assert.Single(diagnostics, item => item.Code == "S1002");
        Assert.Equal(DiagnosticLevel.Help, diagnostic.Level);
        Assert.Contains("curried prefix calls", diagnostic.Message);

        var replacements = diagnostic.Suggestions.Select(item => item.Replacement).ToArray();
        Assert.Contains("a.append(b)", replacements);
        Assert.Contains("append(a, b)", replacements);
        Assert.All(diagnostic.Suggestions, suggestion =>
        {
            Assert.Equal(SuggestionKind.StyleRewrite, suggestion.Kind);
            var expectedMessage = suggestion.Replacement?.Contains('`', StringComparison.Ordinal) == true
                ? DiagnosticMessages.RewriteAsInfixSuggestion
                : DiagnosticMessages.RewriteAsSuggestion(suggestion.Replacement!);
            Assert.Equal(expectedMessage, suggestion.Message);
            Assert.NotNull(suggestion.Span);
            Assert.Equal("medium", suggestion.Confidence);
            Assert.True(suggestion.RequiresCleanTypes);
        });
    }

    [Fact]
    public void Build_GroupedPrefixCall_DoesNotOfferFluentStyleFix()
    {
        const string source = """
mapped :: map(items, inc);
""";

        var diagnostics = BuildRawStyleDiagnostics(source);

        Assert.DoesNotContain(diagnostics, item => item.Code is "S1001" or "S1003");
    }

    [Fact]
    public void Build_BinaryGroupedPrefixCall_DoesNotOfferBacktickInfixStyleFix()
    {
        const string source = """
combined :: combine(a, b);
""";

        var diagnostics = BuildRawStyleDiagnostics(source);

        Assert.DoesNotContain(diagnostics, item => item.Code == "S1003");
    }

    [Fact]
    public void Build_QualifiedBinaryPrefixCall_DoesNotOfferBacktickInfixStyleFix()
    {
        const string source = """
combined :: combine(a, b);
""";

        var diagnostics = BuildRawStyleDiagnostics(source);

        Assert.DoesNotContain(diagnostics, item => item.Code == "S1003");
    }

    [Fact]
    public void Build_NestedUnaryPrefixCalls_DoesNotOfferFluentChainFix()
    {
        const string source = """
cleaned :: wrap(normalize(trim(input)));
""";

        var diagnostics = BuildRawStyleDiagnostics(source);

        Assert.DoesNotContain(diagnostics, item => item.Code == "S1001");
    }

    [Fact]
    public void Build_QualifiedCurriedPrefixCall_OffersFluentAndGroupedStyleFixes()
    {
        const string source = """
combined :: append(a)(b);
""";

        var diagnostics = BuildRawStyleDiagnostics(source);

        var diagnostic = Assert.Single(diagnostics, item => item.Code == "S1002");
        var replacements = diagnostic.Suggestions.Select(item => item.Replacement).ToArray();

        Assert.Contains("a.append(b)", replacements);
        Assert.Contains("append(a, b)", replacements);
    }

    [Fact]
    public void Build_QualifiedGroupedPrefixCall_DoesNotOfferFluentStyleFix()
    {
        const string source = """
mapped :: map(items, inc);
""";

        var diagnostics = BuildRawStyleDiagnostics(source);

        Assert.DoesNotContain(diagnostics, item => item.Code == "S1001");
    }

    [Fact]
    public void Build_CurriedPrefixCall_WithBinaryReceiver_ParenthesizesReceiver()
    {
        const string source = """
combined :: range_list(start + 1)(stop);
""";

        var diagnostics = BuildRawStyleDiagnostics(source);

        var diagnostic = Assert.Single(diagnostics, item => item.Code == "S1002");
        var replacements = diagnostic.Suggestions.Select(item => item.Replacement).ToArray();

        Assert.Contains("(start + 1).range_list(stop)", replacements);
        Assert.DoesNotContain("start + 1.range_list(stop)", replacements);
    }

    [Fact]
    public void Build_OperatorExpression_DoesNotCreateSemanticStyleRewrite()
    {
        const string source = """
value :: a + b;
""";

        var diagnostics = BuildRawStyleDiagnostics(source);

        Assert.DoesNotContain(diagnostics, item =>
            item.Suggestions.Any(suggestion => suggestion.Kind == SuggestionKind.StyleRewrite));
    }

    [Fact]
    public void Build_TypedQualifiedPrefixCall_SuppressesFluentRewriteWhenTargetFingerprintChanges()
    {
        const string source = """
mapped :: map(items, inc);
""";

        var snapshot = BuildStyleSnapshot(source);

        Assert.True(snapshot.Success);
        Assert.DoesNotContain(snapshot.Diagnostics, item =>
            item.Code == "S1001" &&
            item.Suggestions.Any(suggestion => suggestion.Replacement == "items.map(inc)"));
    }

    [Fact]
    public void Build_RewritePreviewRejectsTypeInvalidStyleFixes()
    {
        const string source = """
combined :: append(a)(b);
""";

        var snapshot = BuildStyleSnapshot(source);

        Assert.True(snapshot.Success);
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Code is "S1001" or "S1002");
    }

    [Fact]
    public void Build_TypedStyleRewrite_CarriesOriginalSymbolId()
    {
        const string source = """
combined :: append(a)(b);
""";

        var diagnostics = BuildTypedRawStyleDiagnostics(source);

        var diagnostic = Assert.Single(diagnostics, item => item.Code == "S1002");
        Assert.All(diagnostic.Suggestions, suggestion => Assert.NotNull(suggestion.OriginalSymbolId));
        Assert.Single(diagnostic.Suggestions.Select(suggestion => suggestion.OriginalSymbolId).Distinct());
    }

    [Fact]
    public void Build_TypedStyleRewrite_CarriesOriginalFingerprint()
    {
        const string source = """
mapped :: map(items, inc);
""";

        var baseResult = BuildStylePipelineResult(source, assertSuccess: true);
        var mapSymbol = baseResult.SymbolTable!.Symbols.Values.First(symbol =>
            symbol.Name == "map" && symbol.Kind == SymbolKind.Function && symbol.IsModuleLevel);
        var diagnostic = Eidosc.Diagnostic.Diagnostic.Help("synthetic style suggestion", "S1999")
            .WithSuggestion(
                "Rewrite as fluent call",
                SuggestionKind.StyleRewrite,
                replacement: "items.map(inc)",
                requiresCleanTypes: true,
                originalSymbolId: mapSymbol.Id.Value);

        var snapshot = IdeSemanticSnapshotBuilder.Build(new CompilationResult
        {
            Success = baseResult.Success,
            CompletedPhase = baseResult.CompletedPhase,
            Diagnostics = [diagnostic],
            InputFile = baseResult.InputFile,
            ImportSearchRoots = baseResult.ImportSearchRoots,
            NoImplicitPrelude = baseResult.NoImplicitPrelude,
            SourceText = baseResult.SourceText,
            Tokens = baseResult.Tokens,
            CstRoot = baseResult.CstRoot,
            Ast = baseResult.Ast,
            SymbolTable = baseResult.SymbolTable,
            TypeInferer = baseResult.TypeInferer,
            TypeAnalysisIncomplete = baseResult.TypeAnalysisIncomplete,
            TypeAnalysisIncompleteReason = baseResult.TypeAnalysisIncompleteReason,
            EffectInferer = baseResult.EffectInferer,
            HirModule = baseResult.HirModule,
            MirModule = baseResult.MirModule,
            BorrowCheckResult = baseResult.BorrowCheckResult,
            LlvmModule = baseResult.LlvmModule,
            LlvmIrText = baseResult.LlvmIrText,
            Documentation = baseResult.Documentation,
            TotalTime = baseResult.TotalTime,
            PhaseTimes = baseResult.PhaseTimes,
            PhaseAllocations = baseResult.PhaseAllocations,
            SubphaseMetrics = baseResult.SubphaseMetrics
        });

        var convertedDiagnostic = Assert.Single(snapshot.Diagnostics, item => item.Code == "S1999");
        var suggestion = Assert.Single(convertedDiagnostic.Suggestions);
        Assert.Equal(mapSymbol.Id.Value, suggestion.OriginalSymbolId);
        Assert.NotNull(suggestion.OriginalFingerprint);
        Assert.Equal("SessionOnly", suggestion.OriginalFingerprintScope);
        Assert.StartsWith("eidos-ide-fp-v2:", suggestion.OriginalFingerprint, StringComparison.Ordinal);
        Assert.Contains(snapshot.Symbols, symbol =>
        {
            return symbol.SymbolId == mapSymbol.Id.Value &&
                   symbol.DefinitionFingerprint == suggestion.OriginalFingerprint &&
                   symbol.DefinitionFingerprintScope == suggestion.OriginalFingerprintScope;
        });
    }

    [Fact]
    public void Build_SameNameFunctionsInDifferentModules_GetDifferentDefinitionFingerprints()
    {
        const string source = """
combined :: append(a)(b);
""";

        var snapshot = BuildStyleSnapshot(source);

        var appendSymbols = snapshot.Symbols
            .Where(symbol => symbol.Name == "append" && symbol.Kind == "function")
            .ToArray();

        Assert.Equal(2, appendSymbols.Length);
        Assert.All(appendSymbols, symbol => Assert.NotNull(symbol.DefinitionFingerprint));
        Assert.All(appendSymbols, symbol => Assert.Equal("SessionOnly", symbol.DefinitionFingerprintScope));
        Assert.Equal(2, appendSymbols.Select(symbol => symbol.DefinitionFingerprint).Distinct().Count());
    }

    [Fact]
    public void Build_TypeError_SuppressesTypeSensitiveStyleFixes()
    {
        const string source = """
combined :: append(a)(missing);
""";

        var snapshot = BuildStyleSnapshot(source, assertSuccess: false);

        Assert.False(snapshot.Success);
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Code is "S1001" or "S1002");
        Assert.All(snapshot.Symbols, symbol => Assert.Null(symbol.DefinitionFingerprint));
        Assert.All(snapshot.Symbols, symbol => Assert.Null(symbol.DefinitionFingerprintScope));
    }

    [Fact]
    public void Build_TypeIncompleteSnapshot_DropsCleanTypeRequiredSuggestions()
    {
        var diagnostic = Eidosc.Diagnostic.Diagnostic.Help("synthetic suggestions", "S1998")
            .WithSuggestion(
                "Add import",
                SuggestionKind.AddImport,
                replacement: "import Std.Seq.{map}\n",
                requiresCleanTypes: false)
            .WithSuggestion(
                "Rewrite as fluent call",
                SuggestionKind.StyleRewrite,
                replacement: "items.map(inc)",
                requiresCleanTypes: true);

        var snapshot = IdeSemanticSnapshotBuilder.Build(new CompilationResult
        {
            Success = true,
            CompletedPhase = CompilationPhase.Types,
            Diagnostics = [diagnostic],
            InputFile = "ide_style_type_incomplete.eidos",
            SourceText = "",
            TypeAnalysisIncomplete = true,
            TypeAnalysisIncompleteReason = "synthetic recovered type boundary"
        });

        var convertedDiagnostic = Assert.Single(snapshot.Diagnostics, item => item.Code == "S1998");
        var suggestion = Assert.Single(convertedDiagnostic.Suggestions);
        Assert.Equal("AddImport", suggestion.Kind);
        Assert.False(suggestion.RequiresCleanTypes);
        Assert.Equal("TypedRecovered", snapshot.SnapshotConfidence);
        Assert.False(snapshot.SnapshotContract.AllowsTypeSensitiveRewrites);
    }

    private static IdeSemanticSnapshot BuildStyleSnapshot(string source)
        => BuildStyleSnapshot(source, assertSuccess: true);

    private static IReadOnlyList<Eidosc.Diagnostic.Diagnostic> BuildRawStyleDiagnostics(string source)
    {
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_style_suggestions.eidos",
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var module = Assert.IsType<Eidosc.Ast.Declarations.ModuleDecl>(result.Ast);
        return IdeStyleSuggestionBuilder.Build(module, result.SourceText);
    }

    private static IReadOnlyList<Eidosc.Diagnostic.Diagnostic> BuildTypedRawStyleDiagnostics(string source)
    {
        var fullSource = CreateFullStyleSource(source);
        var result = new CompilationPipeline(fullSource, new CompilationOptions
        {
            InputFile = "ide_style_suggestions.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<Eidosc.Ast.Declarations.ModuleDecl>(result.Ast);
        return IdeStyleSuggestionBuilder.Build(module, result.SourceText, symbolTable: result.SymbolTable);
    }

    private static IdeSemanticSnapshot BuildStyleSnapshot(string source, bool assertSuccess)
    {
        var result = BuildStylePipelineResult(source, assertSuccess);

        return IdeSemanticSnapshotBuilder.Build(result);
    }

    private static CompilationResult BuildStylePipelineResult(string source, bool assertSuccess)
    {
        var fullSource = CreateFullStyleSource(source);

        var result = new CompilationPipeline(fullSource, new CompilationOptions
        {
            InputFile = "ide_style_suggestions.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        if (assertSuccess)
        {
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        }

        return result;
    }

    private static string CreateFullStyleSource(string source)
    {
        return $$"""
List :: module {
    export append :: Int -> Int -> Int { (x, y) => x + y }
    export map :: Int -> Int -> Int { (items, inc) => items + inc }
}

append :: Int -> Int -> Int { (x, y) => x + y }
map :: Int -> Int -> Int { (items, inc) => items + inc }
combine :: Int -> Int -> Int { (x, y) => x + y }
range_list :: Int -> Int -> Seq[Int] { (start, stop) => [] }

a :: 1;
b :: 2;
items :: 1;
inc :: 1;
start :: 1;
stop :: 2;

{{source}}
""";
    }
}
