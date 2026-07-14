using System.Linq;
using Eidosc.Ast.Expressions;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Xunit;
using AstRecoveryReasons = Eidosc.Ast.AstRecoveryReasons;

namespace Eidosc.Tests.Unit.Semantic;

public partial class IdeSemanticSnapshotTests
{
    [Fact]
    public void Build_ErrorRecoveryType_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
a :: missing;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_type_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var symbol = Assert.Single(snapshot.Symbols, item => item.Name == "a");

        Assert.Equal("TypedRecovered", snapshot.SnapshotConfidence);
        Assert.Null(symbol.TypeText);
        Assert.Null(symbol.TypeConfidence);
        Assert.Contains(snapshot.Diagnostics, item => item.Severity == "error");
    }

    [Fact]
    public void Build_SyntaxRecoveredInitializer_ExposesRecoveredNodeContract()
    {
        const string source = """
bad :: Int = ;
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_recovered_initializer.eidos",
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var recovered = Assert.Single(
            snapshot.RecoveredNodes,
            node => node.Reason == AstRecoveryReasons.ParserExpectedExpression);

        Assert.Equal(nameof(LiteralExpr), recovered.Kind);
        Assert.NotNull(recovered.Span);
        Assert.True(snapshot.SnapshotContract.HasRecoveredNodes);
        Assert.Contains(nameof(IdeSemanticSnapshot.RecoveredNodes), snapshot.SnapshotContract.GuaranteedFields);
    }

    [Fact]
    public void Build_RecoveredSymbolDoesNotHideLaterCleanSymbolTypeText()
    {
        const string source = """
bad :: missing;
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_recovered_and_clean_symbols.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Equal("TypedRecovered", snapshot.SnapshotConfidence);
        Assert.Null(bad.TypeText);
        Assert.Null(bad.TypeConfidence);
        Assert.Equal("Int", good.TypeText);
        Assert.Equal("TypedClean", good.TypeConfidence);
        Assert.Contains(snapshot.Diagnostics, item => item.Severity == "error");
    }

    [Fact]
    public void Build_TypeMismatchRecovery_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
a :: Int = "text";
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_type_mismatch_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var symbol = Assert.Single(snapshot.Symbols, item => item.Name == "a");

        Assert.Null(symbol.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Severity == "error");
    }

    [Fact]
    public void Build_CfnCallRecovery_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Int -> Int {
    _ => {
        not_fn := 1;
        result := cfn_call(not_fn, 42);
        result
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_cfn_call_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var symbol = Assert.Single(snapshot.Symbols, item => item.Name == "result");

        Assert.Null(symbol.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Severity == "error");
    }

    [Fact]
    public void Build_ManyTypeMismatchDeclarations_StillExposesLaterCleanTypeText()
    {
        var badDeclarations = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 25).Select(i => $"bad{i} :: Int = \"text\";"));
        var source = $"""
{badDeclarations}
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_many_type_mismatch_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.DoesNotContain(result.Diagnostics, item => item.Message.Contains("Too many type errors", StringComparison.Ordinal));

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var symbol = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Equal("TypedRecovered", snapshot.SnapshotConfidence);
        Assert.Equal("Int", symbol.TypeText);
        Assert.Equal("TypedClean", symbol.TypeConfidence);
    }

    [Fact]
    public void Build_ForwardLetReferenceWithoutTypeScheme_DoesNotExposeFreshTypeText()
    {
        const string source = """
bad :: later;
later :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_forward_let_reference_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var later = Assert.Single(snapshot.Symbols, item => item.Name == "later");

        Assert.Null(bad.TypeText);
        Assert.Equal("Int", later.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("binding type is unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_TypeErrorLimit_ExposesTypeAnalysisIncomplete()
    {
        var badDeclarations = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 105).Select(i => $"bad{i} :: Int = \"text\";"));
        var source = $"""
{badDeclarations}
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_type_analysis_incomplete.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.True(result.TypeAnalysisIncomplete);
        Assert.NotNull(result.TypeAnalysisIncompleteReason);
        Assert.Contains("Too many type errors", result.TypeAnalysisIncompleteReason);
        Assert.Equal(100, result.TypeErrorLimit);
        Assert.True(result.SuppressedTypeDiagnosticCount > 0);
        Assert.True(result.SuppressedTypeConstraintCount >= 0);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.True(snapshot.TypeAnalysisIncomplete);
        Assert.NotNull(snapshot.TypeAnalysisIncompleteReason);
        Assert.Contains("Too many type errors", snapshot.TypeAnalysisIncompleteReason);
        Assert.Equal(100, snapshot.TypeErrorLimit);
        Assert.True(snapshot.SuppressedTypeDiagnosticCount > 0);
        Assert.True(snapshot.SuppressedTypeConstraintCount >= 0);
        Assert.Equal("Int", good.TypeText);
        Assert.Equal("TypedClean", good.TypeConfidence);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Too many type errors", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AsPatternMismatchRecovery_DoesNotExposeAliasTypeText()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        ("a" as s) => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_as_pattern_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var symbol = Assert.Single(snapshot.Symbols, item => item.Name == "s");

        Assert.Null(symbol.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Severity == "error");
    }

    [Fact]
    public void Build_AsPatternMissingBindingName_DoesNotExposeTrustworthyResultTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        result := match 1
        {
            1 as => {
                inside := 1;
                inside
            },
            _ => 0
        };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_as_pattern_missing_binding_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var resultSymbol = Assert.Single(snapshot.Symbols, item => item.Name == "result");
        var inside = Assert.Single(snapshot.Symbols, item => item.Name == "inside");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(resultSymbol.TypeText);
        Assert.Null(resultSymbol.TypeConfidence);
        Assert.Equal("Int", inside.TypeText);
        Assert.Equal("TypedClean", inside.TypeConfidence);
        Assert.Equal("Int", good.TypeText);
        Assert.Equal("TypedClean", good.TypeConfidence);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("As-pattern requires a binding name after 'as'", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Message.Contains("expected pattern, got '=>'", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ViewPatternInvalidExpressionRecovery_DoesNotExposeInnerBindingTypeText()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        (1 -> y) => 0,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_view_pattern_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var symbol = Assert.Single(snapshot.Symbols, item => item.Name == "y");

        Assert.Null(symbol.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Severity == "error");
    }

    [Fact]
    public void Build_ViewPatternInnerMismatch_DoesNotExposeTrustworthyResultTypeText()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x
}

main :: Unit -> Int
{
    _ => {
        result := match 1
        {
            (normalize -> "text") => 1,
            _ => 0
        };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_view_pattern_inner_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var resultSymbol = Assert.Single(snapshot.Symbols, item => item.Name == "result");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(resultSymbol.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Literal pattern type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_LocalLetPatternMismatch_StillExposesLaterCleanLocalTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        (left, right) := 1;
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_local_let_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var symbol = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Equal("Int", symbol.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Severity == "error");
    }

    [Fact]
    public void Build_ListPatternShapeMismatch_DoesNotExposeElementFreshTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        [x] := 1;
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_list_pattern_shape_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item =>
            item.Severity == "error" &&
            item.Message.Contains("Seq-pattern expected type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_TuplePatternShapeMismatch_DoesNotExposeElementFreshTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        (x, y) := 1;
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_tuple_pattern_shape_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var x = Assert.Single(snapshot.Symbols, item => item.Name == "x");
        var y = Assert.Single(snapshot.Symbols, item => item.Name == "y");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(x.TypeText);
        Assert.Null(y.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item =>
            item.Severity == "error" &&
            item.Message.Contains("Tuple pattern type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_UnresolvedConstructorPattern_DoesNotExposeArgumentFreshTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        Ghost(x) := 1;
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_unresolved_constructor_pattern_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item =>
            item.Severity == "error" &&
            item.Message.Contains("expected expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_IfConditionTypeError_StillInfersBranchLocalTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        result := if "not bool" then {
            good := 1;
            good
        } else {
            0
        };
        result
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_if_condition_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var resultSymbol = Assert.Single(snapshot.Symbols, item => item.Name == "result");
        var symbol = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(resultSymbol.TypeText);
        Assert.Equal("Int", symbol.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("If condition must be Bool", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_IfLetPatternMismatch_DoesNotExposeTrustworthyResultTypeText()
    {
        const string source = """
OptionInt :: type { SomeInt(Int) , NoneInt }

main :: Unit -> Int
{
    _ => {
        result := if let SomeInt(n) = 1 then {
            inside := n;
            inside
        } else {
            0
        };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_if_let_pattern_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var resultSymbol = Assert.Single(snapshot.Symbols, item => item.Name == "result");
        var inside = Assert.Single(snapshot.Symbols, item => item.Name == "inside");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(resultSymbol.TypeText);
        Assert.Equal("Int", inside.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Constructor pattern type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WhileLetPatternMismatch_DoesNotExposeTrustworthyResultTypeText()
    {
        const string source = """
OptionInt :: type { SomeInt(Int) , NoneInt }

main :: Unit -> Int
{
    _ => {
        bad := while let SomeInt(n) = 1 then {
            inside := n;
            inside
        };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_while_let_pattern_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var inside = Assert.Single(snapshot.Symbols, item => item.Name == "inside");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(bad.TypeText);
        Assert.Equal("Int", inside.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Constructor pattern type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WhileLetMissingScrutinee_DoesNotExposeUnitTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        bad := while x := then {
            inside := 1;
            inside
        };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_while_let_missing_scrutinee_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(bad.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item =>
            item.Severity == "error" &&
            item.Message.Contains("expected expression, got 'then'", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MissingTopLevelLetInitializer_DoesNotExposeIntTypeText()
    {
        const string source = """
bad :: Int = ;
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_missing_top_level_let_initializer_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(bad.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item =>
            item.Severity == "error" &&
            item.Message.Contains("expected expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MissingLocalLetInitializer_DoesNotExposeIntTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        bad := ;
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_missing_local_let_initializer_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(bad.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item =>
            item.Severity == "error" &&
            item.Message.Contains("expected expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ListComprehensionGuardTypeError_StillInfersOutputLocalTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        xs := [{ after := 1; after } | x <- [1, 2], x + 1];
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_list_comprehension_guard_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var xs = Assert.Single(snapshot.Symbols, item => item.Name == "xs");
        var symbol = Assert.Single(snapshot.Symbols, item => item.Name == "after");

        Assert.Null(xs.TypeText);
        Assert.Equal("Int", symbol.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Seq comprehension guard must be Bool", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ListComprehensionGeneratorNonList_DoesNotExposeResultTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        xs := [x | x <- 1];
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_list_comprehension_generator_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var xs = Assert.Single(snapshot.Symbols, item => item.Name == "xs");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(xs.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Seq comprehension generator must iterate a Seq", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CallArgumentTypeMismatch_DoesNotExposeCallResultTypeText()
    {
        const string source = """
inc :: Int -> Int
{
    x => x + 1
}

main :: Int -> Int
{
    _ => {
        bad := inc("text");
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_call_argument_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(bad.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Call argument type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_NonCallableCallTarget_DoesNotExposeFreshTypeText()
    {
        const string source = """
f :: 1;
bad :: f(1);
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_non_callable_call_target_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(bad.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item =>
            item.Severity == "error" &&
            item.Message.Contains("Call target is not callable", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_LambdaBodyResultMismatch_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        bad := _ => {
            return 1;
            "text"
        };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_lambda_body_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(bad.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Lambda body result type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CurriedFunctionBodyMismatch_PreservesDeclaredSignature()
    {
        const string source = """
bad :: Int -> Int -> Int
{
    x => "text"
}

good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_curried_function_body_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Equal("Int -> Int -> Int", bad.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Function 'bad' body result type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AssignmentTypeMismatch_StillExposesLaterCleanLocalTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        mut slot := 0;
        slot := "text";
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_assignment_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Assignment type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_LiteralPatternMismatch_StillInfersBranchLocalTypeText()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        "text" => {
            good := 1;
            good
        },
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_literal_pattern_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Literal pattern type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MatchPatternMismatch_DoesNotExposeTrustworthyResultTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        result := match 1
        {
            "text" => {
                inside := 1;
                inside
            },
            _ => 0
        };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_match_pattern_result_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var resultSymbol = Assert.Single(snapshot.Symbols, item => item.Name == "result");
        var inside = Assert.Single(snapshot.Symbols, item => item.Name == "inside");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(resultSymbol.TypeText);
        Assert.Equal("Int", inside.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Literal pattern type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_PatternGuardSourceMismatch_DoesNotExposeTrustworthyResultTypeText()
    {
        const string source = """
OptionInt :: type { SomeInt(Int) , NoneInt }

main :: Unit -> Int
{
    _ => {
        result := match 1
        {
            _ when SomeInt(n) <- 1 => {
                inside := n;
                inside
            },
            _ => 0
        };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_pattern_guard_source_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var resultSymbol = Assert.Single(snapshot.Symbols, item => item.Name == "result");
        var inside = Assert.Single(snapshot.Symbols, item => item.Name == "inside");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(resultSymbol.TypeText);
        Assert.Equal("Int", inside.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Constructor pattern type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_PatternGuardMissingSource_DoesNotExposeFreshBindingTypeText()
    {
        const string source = """
OptionInt :: type { SomeInt(Int) , NoneInt }

main :: Unit -> Int
{
    _ => {
        result := match SomeInt(1)
        {
            _ when SomeInt(n) <- => {
                inside := 1;
                inside
            },
            _ => 0
        };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_pattern_guard_missing_source_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var resultSymbol = Assert.Single(snapshot.Symbols, item => item.Name == "result");
        var n = Assert.Single(snapshot.Symbols, item => item.Name == "n");
        var inside = Assert.Single(snapshot.Symbols, item => item.Name == "inside");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(resultSymbol.TypeText);
        Assert.Null(resultSymbol.TypeConfidence);
        Assert.Null(n.TypeText);
        Assert.Null(n.TypeConfidence);
        Assert.Equal("Int", inside.TypeText);
        Assert.Equal("TypedClean", inside.TypeConfidence);
        Assert.Equal("Int", good.TypeText);
        Assert.Equal("TypedClean", good.TypeConfidence);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Pattern guard requires a source expression after '<-'", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Message.Contains("expected expression, got '=>'", StringComparison.Ordinal));
    }

}
