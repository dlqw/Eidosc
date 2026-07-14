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
    public void Build_RangePatternMissingEnd_DoesNotExposeTrustworthyResultTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        result := match 1
        {
            1.. => {
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
            InputFile = "ide_range_pattern_missing_end_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Range pattern requires both start and end literals", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Message.Contains("Range pattern expects Int or Char scrutinee, got 't", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_NotPatternMissingInner_DoesNotExposeTrustworthyResultTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        result := match 1
        {
            ! => {
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
            InputFile = "ide_not_pattern_missing_inner_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Not-pattern is missing inner pattern", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Message.Contains("expected pattern, got '=>'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1 |", "Or-pattern requires at least two alternatives")]
    [InlineData("1 &", "And-pattern requires at least two conjuncts")]
    public void Build_LogicalPatternMissingRightHandSide_DoesNotExposeTrustworthyResultTypeText(
        string pattern,
        string expectedDiagnostic)
    {
        var source = $$"""
main :: Unit -> Int
{
    _ => {
        result := match 1
        {
            {{pattern}} => {
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
            InputFile = "ide_logical_pattern_missing_rhs_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains(expectedDiagnostic, StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Message.Contains("expected pattern, got '=>'", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_SequentialGuardTypeMismatch_DoesNotExposeTrustworthyResultTypeText()
    {
        const string source = """
OptionInt :: type { SomeInt(Int) , NoneInt }

main :: Unit -> Int
{
    _ => {
        result := match SomeInt(1)
        {
            x when SomeInt(n) <- x when n + 1 => {
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
            InputFile = "ide_sequential_guard_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Sequential guard must be Bool", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_FieldAccessError_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        bad := 1.nope;
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_field_access_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("has no readable field", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MethodReceiverMismatch_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
only_string :: String -> Int
{
    text => 1
}
main :: Int -> Int
{
    _ => {
        bad := 1.only_string();
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_method_receiver_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("No overload of method", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ReturnOutsideFunction_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
bad :: return 1;
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_return_outside_function_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Equal("A", bad.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("expected expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ReturnValueMismatch_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        bad := return "text";
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_return_value_mismatch_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Return value type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_InvalidExplicitTypeApplication_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        bad := 1[Int];
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_explicit_type_application_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Explicit type arguments can only be applied", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RefTemporaryBorrow_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        x := 1;
        bad := ref (x + 1);
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_ref_temporary_borrow_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("can only borrow from a stable place", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MRefTemporaryBorrow_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
make :: Unit -> Int
{
    _ => 1
}

main :: Int -> Int
{
    _ => {
        bad := mref make(());
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_mref_temporary_borrow_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("can only borrow from a stable place", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MissingIndexExpression_StillExposesLaterCleanLocalTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        xs := [1, 2];
        bad := xs[];
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_missing_index_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("index expression requires an index", StringComparison.Ordinal));
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Missing index expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_IndexNonListObject_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        bad := 1[0];
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_index_non_list_object_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Indexed object must be Seq", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_IndexNonIntIndex_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        bad := [1]["zero"];
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_index_non_int_index_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Index expression must be Int", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ListRestExpressionMismatch_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        bad := [1, ..2];
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_list_rest_expression_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Seq rest expression type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DoBindNonMonadicValue_DoesNotExposeRecoveredBindingTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        bad := do {
            x <- 1;
            x
        };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_do_bind_non_monadic_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var binding = Assert.Single(snapshot.Symbols, item => item.Name == "x");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(bad.TypeText);
        Assert.Null(binding.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Do bind expects a monadic value", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RecordUpdateNonAdtBase_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        value := 1;
        bad := value.{ tick: 0 };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_record_update_non_adt_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Record update shorthand requires an ADT record base", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RecordUpdateInvalidVariantField_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
Shape :: type {
    Circle {
        radius: Int,
        color: Int
    }
    , Rect {
        width: Int,
        height: Int,
        color: Int
    }
}

main :: Shape -> Int
{
    shape => {
        bad := shape.{ radius: 2 };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_record_update_invalid_variant_field_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Record update field 'radius' is not present on every constructor", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RecordUpdateSpreadUnstableBase_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
GameState :: type {
    GameState {
        snake: Int,
        tick: Int
    }
}

make :: Unit -> GameState
{
    _ => GameState { snake: 1, tick: 2 }
}

main :: Int -> Int
{
    _ => {
        bad := GameState { ..make(()), tick: 0 };
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_record_update_spread_unstable_base_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Record update spread '..base' currently requires a stable base place", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_PipeToNonCallableValue_DoesNotExposeTrustworthyTypeText()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        bad := 1 |> 2;
        good := 1;
        good
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_pipe_non_callable_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Pipe target is not callable", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_InvalidTypeAnnotation_DoesNotExposeInitializerTypeText()
    {
        const string source = """
bad :: Ref = 1;
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_invalid_type_annotation_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("Type 'Ref' expects 1 type argument", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_BinaryOperandRecovery_DoesNotExposeArithmeticResultTypeText()
    {
        const string source = """
source :: Ref = 1;
bad :: source + 1;
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_binary_operand_recovery.eidos",
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
        Assert.Contains(snapshot.Diagnostics, item => item.Severity == "error");
    }

    [Fact]
    public void Build_UnresolvedInfixCall_DoesNotExposeFreshTypeText()
    {
        const string source = """
bad :: 1 |+| 2;
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_unresolved_infix_recovery.eidos",
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
            item.Message.Contains("|+|", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_BareNullaryConstructor_ExposesAdtTypeText()
    {
        const string source = """
Color :: type {
    Red
}

bad :: Red;
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_bare_nullary_constructor.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Equal("Red", bad.TypeText);
        Assert.Equal("Int", good.TypeText);
    }

    [Fact]
    public void Build_BareNonNullaryConstructor_DoesNotExposeFreshTypeText()
    {
        const string source = """
Option[T] :: type {
    Some(T) , None
}

bad :: Some;
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_bare_non_nullary_constructor_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Equal("Some", bad.TypeText);
        Assert.Equal("Int", good.TypeText);
    }

    [Fact]
    public void Build_TypeNameExpression_DoesNotExposeFreshTypeText()
    {
        const string source = """
Option[T] :: type {
    Some(T) , None
}

bad :: Option;
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_type_name_expression_recovery.eidos",
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
            item.Message.Contains("expects 1 type argument", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_UnresolvedConstructorExpression_DoesNotExposeFreshTypeText()
    {
        const string source = """
bad :: Ghost();
good :: 1;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_unresolved_constructor_recovery.eidos",
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Types, result.CompletedPhase);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var bad = Assert.Single(snapshot.Symbols, item => item.Name == "bad");
        var good = Assert.Single(snapshot.Symbols, item => item.Name == "good");

        Assert.Null(bad.TypeText);
        Assert.Equal("Int", good.TypeText);
        Assert.Contains(snapshot.Diagnostics, item =>
            item.Severity == "error" &&
            item.Message.Contains("constructor", StringComparison.OrdinalIgnoreCase));
    }

}
