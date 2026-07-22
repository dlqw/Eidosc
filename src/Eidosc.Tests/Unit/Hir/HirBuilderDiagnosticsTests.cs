using Eidosc.Symbols;
using System;
using System.Linq;
using Eidosc;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Hir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Tests.Fixtures;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;
using AstRecoveryReasons = Eidosc.Ast.AstRecoveryReasons;

namespace Eidosc.Tests.Unit.Hir;

public class HirBuilderDiagnosticsTests
{
    private static readonly TestPathConfig Paths = TestPathConfig.Current;

    [Fact]
    public void Build_QuoteExpressionCrossingTypesBoundary_ReportsDedicatedInvariantDiagnostic()
    {
        var span = new SourceSpan(new SourceLocation(position: 0, line: 0, column: 0), length: 12);
        var quote = new QuoteExpr();
        quote.SetKind(QuoteKind.Expression);
        quote.SetParts([]);
        quote.SetSpan(span);

        var pattern = new VarPattern();
        pattern.SetSpan(span);
        pattern.SetName("escaped_quote");
        var declaration = new LetDecl();
        declaration.SetSpan(span);
        declaration.SetPattern(pattern);
        declaration.SetValue(quote);
        var module = new ModuleDecl();
        module.SetPath(["Main"]);
        module.SetDeclarations([declaration]);

        var builder = new HirBuilder(new SymbolTable());
        _ = builder.Build(module);

        var diagnostic = Assert.Single(builder.Diagnostics, diagnostic => diagnostic.Code == "E5121");
        Assert.Equal("hir", diagnostic.Metadata["phase"]);
        Assert.Equal("quote-crossed-types-hir-boundary", diagnostic.Metadata["reason"]);
        Assert.Equal(nameof(QuoteExpr), diagnostic.Metadata["astNodeKind"]);
    }

    [Fact]
    public void Build_MissingFunctionBranchBody_ReportsStructuredHirFallbackMetadata()
    {
        var span = new SourceSpan(new SourceLocation(position: 0, line: 0, column: 0), length: 5);
        var branch = new PatternBranch();
        branch.SetSpan(span);
        branch.SetPattern(new WildcardPattern { Span = span });

        var function = new FuncDef();
        function.SetName("broken");
        function.SetBody([branch]);

        var module = new ModuleDecl();
        module.SetPath(["Main"]);
        module.SetDeclarations([function]);

        var builder = new HirBuilder(new SymbolTable());

        _ = builder.Build(module);

        var diagnostic = Assert.Single(builder.Diagnostics, diagnostic => diagnostic.Code == "E5110");
        Assert.Equal("hir", diagnostic.Metadata["phase"]);
        Assert.Equal("expression", diagnostic.Metadata["fallbackKind"]);
        Assert.Equal("missing-expression", diagnostic.Metadata["reason"]);
        Assert.Equal("HirError", diagnostic.Metadata["hirNodeKind"]);
        Assert.Equal("function 'broken' body", diagnostic.Metadata["context"]);
    }

    [Fact]
    public void Build_RecoveredAstExpression_PropagatesRecoveryReasonToHirError()
    {
        var span = new SourceSpan(new SourceLocation(position: 0, line: 0, column: 0), length: 0);
        var literal = new LiteralExpr();
        literal.SetSpan(span);
        literal.SetLiteral("0");
        literal.MarkRecoveredError(AstRecoveryReasons.ParserMissingInitializer);

        var pattern = new VarPattern();
        pattern.SetSpan(span);
        pattern.SetName("broken");

        var declaration = new LetDecl();
        declaration.SetSpan(span);
        declaration.SetPattern(pattern);
        declaration.SetValue(literal);

        var module = new ModuleDecl();
        module.SetPath(["Main"]);
        module.SetDeclarations([declaration]);

        var builder = new HirBuilder(new SymbolTable());
        var hirModule = builder.Build(module);

        var hirVal = Assert.Single(hirModule.Declarations.OfType<HirVal>());
        var error = Assert.IsType<HirError>(hirVal.Initializer);
        Assert.True(error.IsRecovered);
        Assert.Equal(AstRecoveryReasons.ParserMissingInitializer, error.Reason);
    }

    [Fact]
    public void Build_IndexExpression_DoesNotReportUnsupportedIndexDiagnostic()
    {
        var relativePath = Paths.Fixture("index/list_index.eidos");
        var result = RunPipeline(relativePath, CompilationPhase.Hir);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E51", StringComparison.Ordinal) == true &&
                          diagnostic.Message?.Contains("IndexExpr", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E0001");

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var indexInitializers = hirModule.Declarations
            .OfType<HirVal>()
            .Where(val => val.Initializer is HirIndexAccess)
            .Select(val => (HirIndexAccess)val.Initializer)
            .ToList();

        Assert.NotEmpty(indexInitializers);
        Assert.All(indexInitializers, index => Assert.Equal(HirIndexAccessKind.RuntimeArray, index.TargetKind));
    }

    [Fact]
    public void Build_ListComprehension_SimpleGenerator_LowersWithoutUnsupportedDiagnostic()
    {
        var relativePath = Paths.Fixture("control/list_comp.eidos");
        var result = RunPipeline(relativePath, CompilationPhase.Hir);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E51", StringComparison.Ordinal) == true &&
                          diagnostic.Message?.Contains("ListComprehension", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E0001");

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var doubled = Assert.Single(hirModule.Declarations.OfType<HirVal>(), val => val.Name == "doubled");
        var loweredComp = Assert.IsType<HirListComprehension>(doubled.Initializer);
        Assert.Single(loweredComp.Qualifiers, q => q.Kind == HirQualifierKind.Generator);
        Assert.IsType<HirBinOp>(loweredComp.Output);
    }

    [Fact]
    public void Build_ListComprehension_NonLiteralGeneratorSource_PreservesGeneratorInHir()
    {
        const string source = """
nums :: [1, 2, 3];
doubled :: [x * 2 | x <- nums];
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E51", StringComparison.Ordinal) == true &&
                          diagnostic.Message?.Contains("ListComprehension", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var doubled = Assert.Single(hirModule.Declarations.OfType<HirVal>(), val => val.Name == "doubled");
        var loweredComp = Assert.IsType<HirListComprehension>(doubled.Initializer);
        var generator = Assert.Single(loweredComp.Qualifiers, q => q.Kind == HirQualifierKind.Generator);
        Assert.IsType<HirVar>(generator.GeneratorSource);
    }

    [Fact]
    public void Build_MatchLiteralPattern_DoesNotReportUnsupportedLiteralPattern()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        0 => 1,
        n => n
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E51", StringComparison.Ordinal) == true &&
                          diagnostic.Message?.Contains("LiteralPattern", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E0001");
    }

    [Fact]
    public void Build_MatchLiteralPattern_PreservesScrutineeAndAllBranches()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        0 => 1,
        n => n
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);

        var function = Assert.Single(result.HirModule!.Declarations.OfType<HirFunc>(), f => f.Name == "classify");
        var match = Assert.IsType<HirMatch>(function.Body);
        var scrutinee = Assert.IsType<HirVar>(match.Scrutinee);

        Assert.Equal("x", scrutinee.Name);
        Assert.Equal(2, match.Branches.Count);
        Assert.IsType<HirLiteralPattern>(match.Branches[0].Pattern);
        Assert.IsType<HirVarPattern>(match.Branches[1].Pattern);
    }

    [Fact]
    public void Build_MatchGuard_PreservesGuardInHir()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        n when n > 0 => 1,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var function = Assert.Single(result.HirModule!.Declarations.OfType<HirFunc>(), f => f.Name == "classify");
        var match = Assert.IsType<HirMatch>(function.Body);
        var guardedBranch = match.Branches[0];
        var guard = Assert.IsType<HirBinOp>(guardedBranch.Guard);

        Assert.Equal(BinaryOp.Gt, guard.Operator);
    }

    [Fact]
    public void Build_MatchPatternGuard_PreservesPatternGuardAndBindingInHir()
    {
        const string source = """
OptionI :: type
{
    Some:: type(Int) , None :: type {}
}

classify :: OptionI -> Int
{
    x => match x
    {
        _ when Some(n) <- x => n,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var function = Assert.Single(result.HirModule!.Declarations.OfType<HirFunc>(), f => f.Name == "classify");
        var match = Assert.IsType<HirMatch>(function.Body);
        var guardedBranch = match.Branches[0];

        var guard = Assert.IsType<HirPatternGuard>(guardedBranch.Guard);
        var guardPattern = Assert.IsType<HirCtorPattern>(guard.Pattern);
        Assert.Equal("Some", guardPattern.ConstructorName);
        var ctorField = Assert.Single(guardPattern.Fields);
        var guardBinding = Assert.IsType<HirVarPattern>(ctorField.Pattern);
        Assert.Equal("n", guardBinding.Name);

        var guardSource = Assert.IsType<HirVar>(guard.SourceExpression);
        Assert.Equal("x", guardSource.Name);

        var bodyVar = Assert.IsType<HirVar>(guardedBranch.Body);
        Assert.Equal(guardBinding.SymbolId, bodyVar.SymbolId);
    }

    [Fact]
    public void Build_FunctionPatternBranchesWithGuard_LowersToHirMatch()
    {
        const string source = """
abs :: Int -> Int
{
    n when n >= 0 => n,
    n => 0 - n
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var abs = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), function => function.Name == "abs");
        var match = Assert.IsType<HirMatch>(abs.Body);
        Assert.Equal(2, match.Branches.Count);

        var guard = Assert.IsType<HirBinOp>(match.Branches[0].Guard);
        Assert.Equal(BinaryOp.Ge, guard.Operator);
        Assert.Null(match.Branches[1].Guard);
    }

    [Fact]
    public void Build_FunctionPatternBranchesWithPatternGuardBinding_LowersToHirPatternGuard()
    {
        const string source = """
OptionI :: type
{
    Some:: type(Int) , None :: type {}
}

classify :: OptionI -> Int
{
    x when Some(n) <- x => n,
    _ => 0
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var classify = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), function => function.Name == "classify");
        var match = Assert.IsType<HirMatch>(classify.Body);
        var guardedBranch = match.Branches[0];

        var guard = Assert.IsType<HirPatternGuard>(guardedBranch.Guard);
        var guardPattern = Assert.IsType<HirCtorPattern>(guard.Pattern);
        Assert.Equal("Some", guardPattern.ConstructorName);
        var guardField = Assert.Single(guardPattern.Fields);
        var guardBinding = Assert.IsType<HirVarPattern>(guardField.Pattern);
        Assert.Equal("n", guardBinding.Name);
        var guardSource = Assert.IsType<HirVar>(guard.SourceExpression);
        Assert.Equal("x", guardSource.Name);

        var bodyVar = Assert.IsType<HirVar>(guardedBranch.Body);
        Assert.Equal(guardBinding.SymbolId, bodyVar.SymbolId);
    }

    [Fact]
    public void Build_FunctionPatternBranchesWithTuplePatternGuardBinding_LowersToHirPatternGuard()
    {
        const string source = """
second_if_first_one :: (Int, Int) -> Int
{
    pair when (1, n) <- pair => n,
    _ => 0
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "second_if_first_one");
        var match = Assert.IsType<HirMatch>(function.Body);
        var guardedBranch = match.Branches[0];

        var guard = Assert.IsType<HirPatternGuard>(guardedBranch.Guard);
        var tuplePattern = Assert.IsType<HirTuplePattern>(guard.Pattern);
        Assert.Equal(2, tuplePattern.Elements.Count);
        Assert.IsType<HirLiteralPattern>(tuplePattern.Elements[0]);
        var guardBinding = Assert.IsType<HirVarPattern>(tuplePattern.Elements[1]);
        Assert.Equal("n", guardBinding.Name);
        var guardSource = Assert.IsType<HirVar>(guard.SourceExpression);
        Assert.Equal("pair", guardSource.Name);

        var bodyVar = Assert.IsType<HirVar>(guardedBranch.Body);
        Assert.Equal(guardBinding.SymbolId, bodyVar.SymbolId);
    }

    [Fact]
    public void Build_LegacyCtorViewPattern_FailsBeforeHir()
    {
        const string source = """
to_digit :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        View(to_digit, 7) => 30,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.False(result.Success);
    }

    [Fact]
    public void Build_KeywordViewPattern_FailsBeforeHir()
    {
        const string source = """
to_digit :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        view(to_digit -> 7) => 30,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.False(result.Success);
    }

    [Fact]
    public void Build_MatchNativeViewPattern_LowersToViewHirPattern()
    {
        const string source = """
to_digit :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        (to_digit -> 7) => 30,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var classify = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), function => function.Name == "classify");
        var match = Assert.IsType<HirMatch>(classify.Body);
        Assert.Equal(2, match.Branches.Count);
        var viewPattern = Assert.IsType<HirViewPattern>(match.Branches[0].Pattern);
        Assert.True(viewPattern.ViewResultTypeId.IsValid);
    }

    [Fact]
    public void Build_MatchNativeViewPatternWithGeneralExpression_LowersExpressionViewInHir()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        (if true then { normalize } else { normalize } -> 7) => 30,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var classify = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), function => function.Name == "classify");
        var match = Assert.IsType<HirMatch>(classify.Body);
        var viewPattern = Assert.IsType<HirViewPattern>(match.Branches[0].Pattern);
        Assert.IsType<HirIf>(viewPattern.View);
    }

    [Fact]
    public void Build_MatchNativeViewPatternWithCallOnGeneralExpression_LowersCallViewInHir()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x
}

select_view :: Bool -> Int -> Int
{
    (b, value) => if b then { normalize(value) } else { normalize(value) }
}

classify :: Int -> Int
{
    x => match x
    {
        ((if true then { select_view } else { select_view })(true) -> 7) => 30,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var classify = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), function => function.Name == "classify");
        var match = Assert.IsType<HirMatch>(classify.Body);
        var viewPattern = Assert.IsType<HirViewPattern>(match.Branches[0].Pattern);
        var callView = Assert.IsType<HirCall>(viewPattern.View);
        Assert.IsType<HirIf>(callView.Function);
        Assert.Single(callView.Arguments);
    }

    [Fact]
    public void Build_KeywordCommaViewPatternWithGeneralExpression_FailsBeforeHir()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        view(if true then { normalize } else { normalize }, 7) => 30,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.False(result.Success);
    }

    [Fact]
    public void Build_MatchOrAndRangePatterns_LowersToHirPatterns()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        1 | 2 => 10,
        3..5 => 20,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var classify = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), function => function.Name == "classify");
        var match = Assert.IsType<HirMatch>(classify.Body);
        Assert.Equal(3, match.Branches.Count);
        Assert.IsType<HirOrPattern>(match.Branches[0].Pattern);
        Assert.IsType<HirRangePattern>(match.Branches[1].Pattern);
    }

    [Fact]
    public void Build_MatchListPatterns_LowersToHirListPattern()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1, 2, 3]
    {
        [head, ..tail] => head,
        [] => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var classify = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), function => function.Name == "classify");
        var match = Assert.IsType<HirMatch>(classify.Body);
        Assert.Equal(2, match.Branches.Count);

        var listPattern = Assert.IsType<HirListPattern>(match.Branches[0].Pattern);
        Assert.Single(listPattern.Elements);
        Assert.True(listPattern.HasRest);
        var restBinding = Assert.IsType<HirVarPattern>(listPattern.RestPattern);
        Assert.Equal("tail", restBinding.Name);

        var emptyPattern = Assert.IsType<HirListPattern>(match.Branches[1].Pattern);
        Assert.Empty(emptyPattern.Elements);
        Assert.False(emptyPattern.HasRest);
    }

    [Fact]
    public void Build_MatchAndPattern_LowersToHirAndPattern()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        (1 as n) & 1..3 => n,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var classify = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), function => function.Name == "classify");
        var match = Assert.IsType<HirMatch>(classify.Body);
        Assert.Equal(2, match.Branches.Count);

        var andPattern = Assert.IsType<HirAndPattern>(match.Branches[0].Pattern);
        Assert.IsType<HirAsPattern>(andPattern.Left);
        Assert.IsType<HirRangePattern>(andPattern.Right);
    }

    [Fact]
    public void Build_MatchNotPattern_LowersToHirNotPattern()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        !0 => 1,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var classify = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), function => function.Name == "classify");
        var match = Assert.IsType<HirMatch>(classify.Body);
        Assert.Equal(2, match.Branches.Count);

        var notPattern = Assert.IsType<HirNotPattern>(match.Branches[0].Pattern);
        Assert.IsType<HirLiteralPattern>(notPattern.InnerPattern);
    }

    [Fact]
    public void Build_MatchOrPatternWithBindings_UsesSharedSymbolAcrossAlternatives()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        (1 as n) | (2 as n) => n,
        _ => 0
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var classify = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), function => function.Name == "classify");
        var match = Assert.IsType<HirMatch>(classify.Body);
        var orPattern = Assert.IsType<HirOrPattern>(match.Branches[0].Pattern);
        var left = Assert.IsType<HirAsPattern>(orPattern.Left);
        var right = Assert.IsType<HirAsPattern>(orPattern.Right);
        var bodyVar = Assert.IsType<HirVar>(match.Branches[0].Body);

        Assert.True(left.SymbolId.IsValid);
        Assert.Equal(left.SymbolId, right.SymbolId);
        Assert.Equal(left.SymbolId, bodyVar.SymbolId);
    }

    [Fact]
    public void Build_IfLetExpression_LowersToHirMatchWithWildcardElse()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }

unwrap_or_zero :: Option[Int] -> Int
{
    value => if let Some(n) = value then { n } else { 0 }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "unwrap_or_zero");
        var hirMatch = Assert.IsType<HirMatch>(function.Body);

        Assert.Equal(2, hirMatch.Branches.Count);
        var ctorPattern = Assert.IsType<HirCtorPattern>(hirMatch.Branches[0].Pattern);
        Assert.Equal("Some", ctorPattern.ConstructorName);
        Assert.IsType<HirVarPattern>(ctorPattern.Fields[0].Pattern);

        var fallbackPattern = Assert.IsType<HirVarPattern>(hirMatch.Branches[1].Pattern);
        Assert.True(fallbackPattern.IsWildcard);
    }

    [Fact]
    public void Build_WhileLetExpression_LowersToHirLoopWithMatchAndBreakFallback()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }

accumulate :: Option[Int] -> Int
{
    value => {
        mut total := 0;
        while let Some(n) = value then {
            total := total + n;
        };
        total
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "accumulate");
        var bodyBlock = Assert.IsType<HirBlock>(function.Body);
        var whileStmt = Assert.IsType<HirExprStatement>(bodyBlock.Statements[1]);
        var loop = Assert.IsType<HirLoop>(whileStmt.Expression);
        var loopMatch = Assert.IsType<HirMatch>(loop.Body);

        Assert.Equal(2, loopMatch.Branches.Count);
        var ctorPattern = Assert.IsType<HirCtorPattern>(loopMatch.Branches[0].Pattern);
        Assert.Equal("Some", ctorPattern.ConstructorName);

        var fallbackPattern = Assert.IsType<HirVarPattern>(loopMatch.Branches[1].Pattern);
        Assert.True(fallbackPattern.IsWildcard);
        Assert.IsType<HirBreak>(loopMatch.Branches[1].Body);
    }

    [Fact]
    public void Build_ReturnExpression_LowersToHirReturnWithoutUnsupportedDiagnostic()
    {
        const string source = """
id :: Int -> Int
{
    x => return x
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E51", StringComparison.Ordinal) == true &&
                          diagnostic.Message?.Contains("ReturnExpr", StringComparison.Ordinal) == true);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "id");
        var hirReturn = Assert.IsType<HirReturn>(function.Body);
        var returnValue = Assert.IsType<HirVar>(hirReturn.Value);
        Assert.Equal("x", returnValue.Name);
    }

    [Fact]
    public void Build_UnaryDerefExpression_LowersToHirUnaryDeref()
    {
        const string source = """
id :: Ref[Int] -> Int
{
    x => x
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "id");

        // Auto-deref via TryUnifyAutoDeref produces type-compatible code without explicit deref node
        // The body is the variable itself (type unification handles Ref[Int] -> Int)
        var body = function.Body;
        Assert.True(body is HirVar or HirUnaryOp);
    }

    [Fact]
    public void Build_ExplicitUnaryDerefInLetInitializer_LowersToHirUnaryDeref()
    {
        const string source = """
id :: Ref[Int] -> Int
{
    r => {
        value := *r;
        value
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "id");
        var bodyBlock = Assert.IsType<HirBlock>(function.Body);
        var refDecl = Assert.IsType<HirDeclStatement>(bodyBlock.Statements[0]);
        var value = Assert.IsType<HirVal>(refDecl.Declaration);
        var unary = Assert.IsType<HirUnaryOp>(value.Initializer);
        Assert.Equal(Eidosc.Hir.UnaryOp.Deref, unary.Operator);
    }

    [Fact]
    public void Build_ExplicitRefExpression_LowersToHirUnaryRef()
    {
        const string source = """
id :: Int -> Ref[Int]
{
    x => ref x
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "id");
        var unary = Assert.IsType<HirUnaryOp>(function.Body);
        Assert.Equal(Eidosc.Hir.UnaryOp.Ref, unary.Operator);
    }

    [Fact]
    public void Build_ExplicitDerefAssignment_LowersTargetToHirUnaryDeref()
    {
        const string source = """
replace :: MRef[Int] -> Int -> Int
{
    target => value => {
        *target := value;
        *target
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "replace");
        var assignment = Assert.Single(EnumerateHirAssignments(function.Body));
        var target = Assert.IsType<HirUnaryOp>(assignment.Target);
        Assert.Equal(Eidosc.Hir.UnaryOp.Deref, target.Operator);
    }

    [Fact]
    public void Build_LetRefMrefPatternBindings_PropagateBindingModesToHir()
    {
        const string source = """
demo :: Int -> Int
{
    x => {
        ref y := x;
        mref z := x;
        x
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "demo");
        var bodyBlock = Assert.IsType<HirBlock>(function.Body);

        var refDecl = Assert.IsType<HirDeclStatement>(bodyBlock.Statements[0]);
        var refVal = Assert.IsType<HirVal>(refDecl.Declaration);
        var refPattern = Assert.IsType<HirVarPattern>(refVal.Pattern);
        Assert.Equal("y", refPattern.Name);
        Assert.Equal(PatternBindingMode.SharedBorrow, refPattern.BindingMode);

        var mutDecl = Assert.IsType<HirDeclStatement>(bodyBlock.Statements[1]);
        var mutVal = Assert.IsType<HirVal>(mutDecl.Declaration);
        var mutPattern = Assert.IsType<HirVarPattern>(mutVal.Pattern);
        Assert.Equal("z", mutPattern.Name);
        Assert.Equal(PatternBindingMode.MutableBorrow, mutPattern.BindingMode);
    }

    [Fact]
    public void Build_AsMrefPatternBinding_PropagatesMutableBorrowModeToHir()
    {
        const string source = """
demo :: Int -> MRef[Int]
{
    x => match x
    {
        (x as mref keep) => keep,
        _ => mref x
    }
}
""";

        var result = RunSource(source, CompilationPhase.Hir);
        Assert.True(result.Success);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "demo");
        var match = Assert.IsType<HirMatch>(function.Body);
        var asPattern = Assert.IsType<HirAsPattern>(match.Branches[0].Pattern);

        Assert.Equal("keep", asPattern.Name);
        Assert.Equal(PatternBindingMode.MutableBorrow, asPattern.BindingMode);
        Assert.Contains("mref", asPattern.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AdtConstructorWithNamedField_PreservesHirCtorFieldMetadata()
    {
        const string source = """
Option :: type { None :: type {} , Some:: type{value:: Int} }
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var adt = Assert.Single(hirModule.Declarations.OfType<HirAdt>(), declaration => declaration.Name == "Option");
        var someCtor = Assert.Single(adt.Constructors, ctor => ctor.Name == "Some");
        var field = Assert.Single(someCtor.Fields);
        Assert.Equal("value", field.Name);
        Assert.True(adt.TypeId.IsValid);
    }

    [Fact]
    public void Build_UnitWildcardFunctionSignature_UsesDeclaredSymbolTypesWhenInferenceLeavesParameterUnresolved()
    {
        const string source = """
read_key :: Unit -> Int
{
    _ => 1
}

main :: Unit -> Int
{
    _ => read_key(())
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);

        var readKey = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "read_key");
        var main = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "main");

        Assert.Collection(
            readKey.Parameters,
            parameter => Assert.Equal(new TypeId(BaseTypes.UnitId), parameter.TypeId));
        Assert.Collection(
            main.Parameters,
            parameter => Assert.Equal(new TypeId(BaseTypes.UnitId), parameter.TypeId));
        Assert.Equal(new TypeId(BaseTypes.IntId), readKey.ReturnType);
        Assert.Equal(new TypeId(BaseTypes.IntId), main.ReturnType);
    }

    [Fact]
    public void Build_DoExpression_SyntheticContinuationLambdasCarryCallableTypes()
    {
        const string source = """
import std.Option
import std.Monad

main :: Unit -> Int
{
    _ => {
        result := do {
            x <- Some(2)
            y <- Some(3)
            Some(x + y)
        };
        Option.unwrap_or(result)(0)
    }
}
""";

        var result = RunSourceWithFixtureInputPath(source, CompilationPhase.Hir);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == Eidosc.Diagnostic.DiagnosticLevel.Error);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var main = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "main");
        var body = Assert.IsType<HirBlock>(main.Body);
        var resultDecl = Assert.IsType<HirDeclStatement>(Assert.Single(body.Statements));
        var resultValue = Assert.IsType<HirVal>(resultDecl.Declaration);
        var firstBind = Assert.IsType<HirCall>(resultValue.Initializer);
        var firstContinuation = Assert.IsType<HirLambda>(firstBind.Arguments[1]);
        var secondBind = Assert.IsType<HirCall>(firstContinuation.Body);
        var secondContinuation = Assert.IsType<HirLambda>(secondBind.Arguments[1]);

        Assert.True(firstBind.TypeId.IsValid);
        Assert.True(firstContinuation.TypeId.IsValid);
        Assert.True(firstContinuation.ReturnType.IsValid);
        Assert.True(secondBind.TypeId.IsValid);
        Assert.True(secondContinuation.TypeId.IsValid);
        Assert.True(secondContinuation.ReturnType.IsValid);
        Assert.Contains(secondContinuation.Captures, capture => capture.Name == "x" && capture.TypeId.IsValid);
    }

    private static CompilationResult RunPipeline(string relativePath, CompilationPhase stopAt)
    {
        var source = TestSourceLoader.Load(relativePath);
        var options = new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(relativePath),
            LanguageVersion = TestSourceLoader.GetLanguageVersion(relativePath),
            StopAtPhase = stopAt,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static CompilationResult RunSource(string source, CompilationPhase stopAt)
    {
        var options = new CompilationOptions
        {
            InputFile = "hir_builder_diagnostics_tests.eidos",
            LanguageVersion = Eidosc.ProjectSystem.EidosLanguageVersions.Current,
            StopAtPhase = stopAt,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static CompilationResult RunSourceWithFixtureInputPath(string source, CompilationPhase stopAt)
    {
        var options = new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.Fixture("basic/literals.eidos")),
            LanguageVersion = TestSourceLoader.GetLanguageVersion(Paths.Fixture("basic/literals.eidos")),
            StopAtPhase = stopAt,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }
    [Fact]
    public void Build_UnaryRefExpression_LowersToHirUnaryRef()
    {
        const string source = """
id :: Int -> Ref[Int]
{
    x => ref x
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "id");
        var unary = Assert.IsType<HirUnaryOp>(function.Body);
        var operand = Assert.IsType<HirVar>(unary.Operand);

        Assert.Equal(Eidosc.Hir.UnaryOp.Ref, unary.Operator);
        Assert.Equal("x", operand.Name);
    }

    [Fact]
    public void Build_UnaryMRefExpression_LowersToHirUnaryMRef()
    {
        const string source = """
id :: Int -> MRef[Int]
{
    x => mref x
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "id");
        var unary = Assert.IsType<HirUnaryOp>(function.Body);
        var operand = Assert.IsType<HirVar>(unary.Operand);

        Assert.Equal(Eidosc.Hir.UnaryOp.MRef, unary.Operator);
        Assert.Equal("x", operand.Name);
    }

    [Fact]
    public void Build_IndexExpression_OnRefList_UsesRuntimeArrayTargetKindWithoutExplicitDeref()
    {
        const string source = """
first :: Ref[Seq[Int]] -> Int
{
    xs => xs[0]
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "first");
        var index = Assert.IsType<HirIndexAccess>(function.Body);

        Assert.Equal(HirIndexAccessKind.RuntimeArray, index.TargetKind);
        Assert.IsType<HirVar>(index.Target);
    }

    [Fact]
    public void Build_CallAcceptingRef_KeepsMRefArgumentAsUnaryBorrowExpression()
    {
        const string source = """
read :: Ref[Int] -> Int
{
    r => r
}

use :: Int -> Int
{
    x => read(mref x)
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "use");
        var call = Assert.IsType<HirCall>(function.Body);
        var argument = Assert.Single(call.Arguments);
        var unary = Assert.IsType<HirUnaryOp>(argument);

        Assert.Equal(Eidosc.Hir.UnaryOp.MRef, unary.Operator);
        Assert.IsType<HirVar>(unary.Operand);
    }

    [Fact]
    public void Build_FieldRead_OnRefRecord_LowersToHirFieldAccessWithoutExplicitDeref()
    {
        const string source = """
Range :: type
{
    start:: Int, end:: Int
}

read :: Ref[Range] -> Int
{
    r => r.start
}
""";

        var result = RunSource(source, CompilationPhase.Hir);

        Assert.True(result.Success);
        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var function = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), item => item.Name == "read");
        var field = Assert.IsType<HirFieldAccess>(function.Body);

        Assert.Equal("start", field.FieldName);
        Assert.IsType<HirVar>(field.Target);
    }

    private static IEnumerable<HirAssignStatement> EnumerateHirAssignments(HirNode? node)
    {
        if (node == null)
        {
            yield break;
        }

        switch (node)
        {
            case HirLambda lambda:
                foreach (var nested in EnumerateHirAssignments(lambda.Body))
                    yield return nested;
                break;
            case HirMatch match:
                foreach (var branch in match.Branches)
                foreach (var nested in EnumerateHirAssignments(branch.Body))
                    yield return nested;
                break;
            case HirBlock block:
                foreach (var statement in block.Statements)
                foreach (var nested in EnumerateHirStatementAssignments(statement))
                    yield return nested;

                foreach (var nested in EnumerateHirAssignments(block.Result))
                    yield return nested;
                break;
        }
    }

    private static IEnumerable<HirAssignStatement> EnumerateHirStatementAssignments(HirStatement statement)
    {
        switch (statement)
        {
            case HirAssignStatement assign:
                yield return assign;
                break;
            case HirExprStatement expr:
                foreach (var nested in EnumerateHirAssignments(expr.Expression))
                    yield return nested;
                break;
            case HirDeclStatement { Declaration: HirVal val }:
                foreach (var nested in EnumerateHirAssignments(val.Initializer))
                    yield return nested;
                break;
            case HirDeclStatement { Declaration: HirVarDecl variable }:
                foreach (var nested in EnumerateHirAssignments(variable.Initializer))
                    yield return nested;
                break;
        }
    }
}


