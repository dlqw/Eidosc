using Eidosc.Symbols;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Tests.Fixtures;
using Eidosc.Cli.Lsp;
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
    public void Build_CurriedPrefixCall_WithGenericNestedReceiver_PreservesCalleeAndTypeArguments()
    {
        const string source = """
result :: SeqBuilder.push(SeqBuilder.empty[Int](1))(41);
""";

        var (snapshot, result) = BuildGenericStyleSnapshot(source);

        var diagnostic = Assert.Single(snapshot.Diagnostics, item => item.Code == "S1002");
        var replacements = diagnostic.Suggestions.Select(item => item.Replacement).ToArray();

        Assert.Contains("SeqBuilder.push(SeqBuilder.empty[Int](1), 41)", replacements);
        Assert.DoesNotContain("SeqBuilder.empty[Int](1).push(41)", replacements);
        Assert.DoesNotContain(replacements, replacement => replacement!.StartsWith("[Int]", StringComparison.Ordinal));
        AssertPublishedStyleReplacementsRemainTyped(snapshot, result);
    }

    [Fact]
    public void Build_CurriedQualifiedCall_WithGenericUnaryReceiver_PreservesWholeApplicationSpine()
    {
        const string source = """
result :: Seq.get_or(snapshot[Int](vec))(0)(0);
""";

        var (snapshot, result) = BuildGenericStyleSnapshot(source);

        var diagnostic = Assert.Single(snapshot.Diagnostics, item => item.Code == "S1002");
        var replacements = diagnostic.Suggestions.Select(item => item.Replacement).ToArray();

        Assert.Contains("Seq.get_or(snapshot[Int](vec), 0, 0)", replacements);
        Assert.DoesNotContain("vec.snapshot[Int]().get_or(0, 0)", replacements);
        Assert.DoesNotContain(replacements, replacement => replacement!.Contains("([Int](vec)", StringComparison.Ordinal));
        AssertPublishedStyleReplacementsRemainTyped(snapshot, result);
    }

    [Fact]
    public void Build_GenericCurriedRoot_PreservesTypeArgumentsInFluentAndGroupedFixes()
    {
        const string source = """
result :: combine[Int](a)(b);
""";

        var (snapshot, result) = BuildGenericStyleSnapshot(source);

        var diagnostic = Assert.Single(snapshot.Diagnostics, item => item.Code == "S1002");
        var replacements = diagnostic.Suggestions.Select(item => item.Replacement).ToArray();

        Assert.Contains("combine[Int](a, b)", replacements);
        Assert.DoesNotContain("a.combine[Int](b)", replacements);
        AssertPublishedStyleReplacementsRemainTyped(snapshot, result);
    }

    [Theory]
    [InlineData("// owl\nresult :: combine[Int](a)(b);\n")]
    [InlineData("// owl\r\nresult :: combine[Int](a)(b);\r\n")]
    [InlineData("// 猫头鹰 🦉\r\nresult :: combine[Int](a)(b);\r\n")]
    public void Build_GenericCurriedRoot_PublishedEditsUseExactCoordinatesAndRemainTyped(string source)
    {
        var (snapshot, result) = BuildGenericStyleSnapshot(source);

        var diagnostic = Assert.Single(snapshot.Diagnostics, item => item.Code == "S1002");
        Assert.Contains(diagnostic.Suggestions, suggestion => suggestion.Replacement == "combine[Int](a, b)");
        AssertPublishedStyleReplacementsRemainTyped(snapshot, result);
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
    public void Build_RewritePreviewKeepsSemanticallyValidFluentAndGroupedFixes()
    {
        const string source = """
combined :: append(a)(b);
""";

        var snapshot = BuildStyleSnapshot(source);

        Assert.True(snapshot.Success);
        var diagnostic = Assert.Single(snapshot.Diagnostics, item => item.Code == "S1002");
        Assert.Contains(diagnostic.Suggestions, suggestion => suggestion.Replacement == "a.append(b)");
        Assert.Contains(diagnostic.Suggestions, suggestion => suggestion.Replacement == "append(a, b)");
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

    [Fact]
    public void Build_Selection_OffersValidatedExplicitMatchMigration()
    {
        const string source = """
import std.Option

consume :: Int -> Int { value => value + 1 }

main :: Option[Int] -> Int {
    option => option then consume(_0) else 0
}
""";

        var snapshot = BuildSelectionStyleSnapshot(source);

        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Code == "S1005");
        var diagnostic = Assert.Single(snapshot.Refactors, item => item.Code == "S1005");
        var suggestion = Assert.Single(diagnostic.Suggestions);
        Assert.Equal("StyleRewrite", suggestion.Kind);
        Assert.Equal("high", suggestion.Confidence);
        Assert.True(suggestion.RequiresCleanTypes);
        Assert.Contains("match option", suggestion.Replacement, StringComparison.Ordinal);
        Assert.Contains("Some(selected_value_0)", suggestion.Replacement, StringComparison.Ordinal);
        Assert.Contains("consume(selected_value_0)", suggestion.Replacement, StringComparison.Ordinal);
        Assert.Contains("None() => 0", suggestion.Replacement, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SelectionMigration_LspEditAppliesToExactSpanAndRemainsTyped()
    {
        const string source = """
import std.Option

consume :: Int -> Int { value => value + 1 }

main :: Option[Int] -> Int {
    option => option then consume(_0) else 0
}
""";
        var snapshot = BuildSelectionStyleSnapshot(source);
        var filePath = snapshot.InputFile;
        var actions = LspSemanticMapper.MapCodeActions(
            snapshot,
            new Uri(filePath).AbsoluteUri,
            filePath,
            new LspRange
            {
                Start = new LspPosition(),
                End = new LspPosition { Line = 20, Character = 200 }
            },
            source,
            documentVersion: 71);
        var action = Assert.Single(actions, static item => item.Kind == "refactor.rewrite");
        var documentEdit = Assert.IsType<LspTextDocumentEdit>(Assert.Single(action.Edit!.DocumentChanges!));
        Assert.Equal(71, documentEdit.TextDocument.Version);
        var edit = Assert.Single(documentEdit.Edits);
        var start = PositionToOffset(source, edit.Range.Start);
        var end = PositionToOffset(source, edit.Range.End);
        var rewritten = source.Remove(start, end - start).Insert(start, edit.NewText);

        var result = new CompilationPipeline(rewritten, new CompilationOptions
        {
            InputFile = filePath,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = false,
            UseColors = false,
            PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [WellKnownStrings.Std.Module] = []
            }
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static item => item.Message)));
        Assert.Contains("match option", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("option then", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CanonicalMatch_OffersValidatedSelectionMigration()
    {
        const string source = """
import std.Result

main :: Result[Int, String] -> Int {
    result => match result {
        Err(_) => 0,
        Ok(value) => value + 1
    }
}
""";

        var snapshot = BuildSelectionStyleSnapshot(source);

        var diagnostic = Assert.Single(snapshot.Refactors, item => item.Code == "S1006");
        var suggestion = Assert.Single(diagnostic.Suggestions);
        Assert.Contains("result\n    then _0 + 1\n    else 0", suggestion.Replacement, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SelectionToMatch_UsesWildcardForUnusedPayload()
    {
        const string source = """
import std.Either

main :: Either[String, Int] -> Int {
    value => value then _0 else 0
}
""";

        var snapshot = BuildSelectionStyleSnapshot(source);

        var diagnostic = Assert.Single(snapshot.Refactors, item => item.Code == "S1005");
        var suggestion = Assert.Single(diagnostic.Suggestions);
        Assert.Contains("Right(selected_value_0)", suggestion.Replacement, StringComparison.Ordinal);
        Assert.Contains("Left(_) => 0", suggestion.Replacement, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UserDefinedOptionMatch_DoesNotOfferSelectionMigration()
    {
        const string source = """
Option[T] :: type { Some :: type(T), None :: type {} }

main :: Option[Int] -> Int {
    option => match option {
        Some(value) => value,
        None() => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_selection_migration_local_option.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var module = Assert.IsType<Eidosc.Ast.Declarations.ModuleDecl>(result.Ast);
        var diagnostics = IdeStyleSuggestionBuilder.Build(module, result.SourceText, symbolTable: result.SymbolTable);
        Assert.DoesNotContain(diagnostics, item => item.Code == "S1006");
    }

    private static IdeSemanticSnapshot BuildStyleSnapshot(string source)
        => BuildStyleSnapshot(source, assertSuccess: true);

    private static IdeSemanticSnapshot BuildSelectionStyleSnapshot(string source)
    {
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath("projects/test/src/stdlib/std_option_import.eidos"),
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = false,
            UseColors = false,
            PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [WellKnownStrings.Std.Module] = []
            }
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return IdeSemanticSnapshotBuilder.Build(result);
    }

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

    private static (IdeSemanticSnapshot Snapshot, CompilationResult Result) BuildGenericStyleSnapshot(string source)
    {
        var result = BuildGenericStyleResult(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return (IdeSemanticSnapshotBuilder.Build(result), result);
    }

    private static void AssertPublishedStyleReplacementsRemainTyped(
        IdeSemanticSnapshot snapshot,
        CompilationResult originalResult)
    {
        var publishedSuggestions = snapshot.Diagnostics
            .Concat(snapshot.Refactors)
            .SelectMany(static diagnostic => diagnostic.Suggestions)
            .Where(static suggestion => suggestion.Kind == SuggestionKind.StyleRewrite.ToString())
            .ToArray();

        Assert.NotEmpty(publishedSuggestions);
        Assert.All(publishedSuggestions, suggestion =>
        {
            var span = Assert.IsType<IdeSpan>(suggestion.Span);
            var replacement = Assert.IsType<string>(suggestion.Replacement);
            Assert.InRange(span.Start, 0, originalResult.SourceText.Length);
            Assert.InRange(span.Length, 1, originalResult.SourceText.Length - span.Start);

            var rewrittenSource = originalResult.SourceText
                .Remove(span.Start, span.Length)
                .Insert(span.Start, replacement);
            var rewritten = new CompilationPipeline(rewrittenSource, new CompilationOptions
            {
                InputFile = originalResult.InputFile,
                ImportSearchRoots = [.. originalResult.ImportSearchRoots],
                PackageImportRoots = originalResult.PackageImportRoots.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.ToArray(),
                    StringComparer.Ordinal),
                LanguageVersion = originalResult.LanguageVersion,
                NoImplicitPrelude = originalResult.NoImplicitPrelude,
                StopAtPhase = CompilationPhase.Types,
                UseColors = false
            }).Run();

            Assert.True(
                rewritten.Success,
                $"Published replacement did not remain typed: {replacement}{Environment.NewLine}" +
                string.Join(Environment.NewLine, rewritten.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        });
    }

    private static CompilationResult BuildGenericStyleResult(string source)
    {
        var fullSource = $$"""
SeqBuilder :: module {
    export empty[T] :: T -> T { value => value }
    export push :: Int -> Int -> Int { value => _ => value }
}

Seq :: module {
    export get_or[T] :: T -> Int -> Int -> T { value => _ => _ => value }
}

snapshot[T] :: T -> T { value => value }
combine[T] :: T -> T -> T { left => _ => left }

vec :: 1;
a :: 1;
b :: 2;

{{source}}
""";
        return new CompilationPipeline(fullSource, new CompilationOptions
        {
            InputFile = "ide_generic_style_suggestions.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();
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
    export append :: Int -> Int -> Int { x, y => x + y }
    export map :: Int -> Int -> Int { items, inc => items + inc }
}

append :: Int -> Int -> Int { x, y => x + y }
map :: Int -> Int -> Int { items, inc => items + inc }
combine :: Int -> Int -> Int { x, y => x + y }
range_list :: Int -> Int -> Seq[Int] { start, stop => [] }

a :: 1;
b :: 2;
items :: 1;
inc :: 1;
start :: 1;
stop :: 2;

{{source}}
""";
    }

    private static int PositionToOffset(string source, LspPosition position)
    {
        var line = 0;
        var offset = 0;
        while (offset < source.Length && line < position.Line)
        {
            if (source[offset++] == '\n')
            {
                line++;
            }
        }

        return Math.Min(source.Length, offset + position.Character);
    }
}
