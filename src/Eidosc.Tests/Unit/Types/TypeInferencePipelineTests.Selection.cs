using System;
using System.Linq;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_BoolSelection_ProducesCommonBranchType()
    {
        const string source = """
choose :: Bool -> Int
{
    flag => flag then 1 else 2
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_OptionSelection_BindsPayloadInBothArms()
    {
        const string source = """
import std.Option

choose :: Option[Int] -> Int
{
    value => value then _0 + 1 else 0
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir, UseSelectionFixturePath);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_ResultSelection_BindsOkAndErrPayloads()
    {
        const string source = """
import std.Result

choose :: Result[Int, String] -> Int
{
    value => value then _0 + 1 else 0
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir, UseSelectionFixturePath);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_ResultElsePlaceholder_HasErrorPayloadType()
    {
        const string source = """
import std.Result

consume_error :: String -> Int
{
    _ => 0
}

choose :: Result[Int, String] -> Int
{
    value => value then _0 else consume_error(_0)
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir, UseSelectionFixturePath);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_EitherSelection_IsRightBiasedAndBindsBothPayloads()
    {
        const string source = """
import std.Either

choose :: Either[String, Int] -> Int
{
    value => value then _0 + 1 else 0
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir, UseSelectionFixturePath);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_GroupSelection_ConcatenatesPositivePayloadSlots()
    {
        const string source = """
import std.Option

consume :: Int -> Int -> Unit
{
    (left, right) => ()
}

choose :: Option[Int] -> Option[Int] -> Unit
{
    (left, right) => (left, right) then consume(_0)(_1) else ()
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir, UseSelectionFixturePath);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_Selection_DoesNotExpandTuplePayloadIntoMultipleSlots()
    {
        const string source = """
import std.Option

sum_pair :: (Int, Int) -> Int
{
    (left, right) => left + right
}

choose :: Option[(Int, Int)] -> Int
{
    value => value then sum_pair(_0) else 0
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir, UseSelectionFixturePath);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_NestedSelection_ShadowsPlaceholderButKeepsOuterPlaceholderInSubject()
    {
        const string source = """
import std.Option

choose :: Option[Option[Int]] -> Int
{
    value => value
        then (_0 then _0 + 1 else 0)
        else 0
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir, UseSelectionFixturePath);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_ImplicitUnitFunctionBody_IsAcceptedAndLowered()
    {
        const string source = """
answer :: Unit -> Int
{
    42
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_ImplicitFunctionBody_RejectsNonUnitFirstParameter()
    {
        const string source = """
bad :: Int -> Int
{
    42
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E4027");
    }

    [Fact]
    public void Types_ImplicitUnitFunctionBody_AcceptsUnitAlias()
    {
        const string source = """
Token :: type = Unit;

answer :: Token -> Int
{
    42
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_ImplicitUnitFunctionBody_ReturningLambdaRequiresNestedBlock()
    {
        const string source = """
make_adder :: Unit -> Int -> Int
{
    { value => value + 1 }
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Types_ImplicitUnitFunctionBody_AllowsNestedMatchArrows()
    {
        const string source = """
answer :: Unit -> Int
{
    match 1
    {
        1 => 42,
        _ => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Namer_SelectionPlaceholderOutsideArm_IsRejected()
    {
        const string source = """
bad :: Unit -> Int
{
    _ => _0
}
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E4020");
    }

    [Fact]
    public void Namer_GroupElsePlaceholder_IsRejectedWithDedicatedDiagnostic()
    {
        const string source = """
bad :: Bool -> Bool -> Unit
{
    (left, right) => (left, right) then () else _0
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "E4022");
        var span = Assert.Single(diagnostic.Labels).Span;
        Assert.Equal(source.IndexOf("_0", StringComparison.Ordinal), span.Position);
        Assert.Equal(2, span.Length);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4024");
    }

    [Fact]
    public void Namer_LeadingZeroPlaceholder_IsRejected()
    {
        const string source = """
bad :: Bool -> Unit
{
    value => value then _00
}
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E4021");
    }

    [Fact]
    public void Types_PlaceholderIndexOutOfRange_IsRejected()
    {
        const string source = """
import std.Option

bad :: Option[Int] -> Unit
{
    value => value then _1
}
""";

        var result = RunPipeline(source, CompilationPhase.Types, UseSelectionFixturePath);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "E4024");
        var span = Assert.Single(diagnostic.Labels).Span;
        Assert.Equal(source.IndexOf("_1", StringComparison.Ordinal), span.Position);
        Assert.Equal(2, span.Length);
    }

    [Fact]
    public void Types_BoolSelection_DoesNotProvidePayloadPlaceholder()
    {
        const string source = """
bad :: Bool -> Unit
{
    value => value then _0
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E4024");
    }

    [Fact]
    public void Types_SingleSelectionArm_MustReturnUnit()
    {
        const string source = """
bad :: Bool -> Int
{
    value => value then 1
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("without both arms must return Unit", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_SingleSelectionArm_AcceptsNever()
    {
        const string source = """
fail :: Unit -> Never
{
    unreachable
}

choose :: Bool -> Unit
{
    value => value then fail(())
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Effects_Selection_UnionsBothArmRequirements()
    {
        const string source = """
Positive :: effect;
Negative :: effect;

positive :: Unit -> Unit need Positive
{
    ()
}

negative :: Unit -> Unit need Negative
{
    ()
}

choose :: Bool -> Unit need Positive, Negative
{
    value => value then positive(()) else negative(())
}
""";

        var result = RunPipeline(source, CompilationPhase.Effects);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Effects_Selection_RejectsMissingArmRequirement()
    {
        const string source = """
Positive :: effect;
Negative :: effect;

positive :: Unit -> Unit need Positive
{
    ()
}

negative :: Unit -> Unit need Negative
{
    ()
}

choose :: Bool -> Unit need Positive
{
    value => value then positive(()) else negative(())
}
""";

        var result = RunPipeline(source, CompilationPhase.Effects);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E3003");
    }

    [Fact]
    public void Borrow_Selection_PreservesRefAndMRefPayloadModes()
    {
        const string source = """
import std.Option

read :: Ref[Int] -> Unit
{
    _ => ()
}

write :: MRef[Int] -> Unit
{
    _ => ()
}

read_option :: Option[Ref[Int]] -> Unit
{
    value => value then read(_0)
}

write_option :: Option[MRef[Int]] -> Unit
{
    value => value then write(_0)
}
""";

        var result = RunPipeline(source, CompilationPhase.Borrow, UseSelectionFixturePath);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Mir_SelectionPlaceholder_CanBeCapturedByNestedLambda()
    {
        const string source = """
import std.Option

choose :: Option[Int] -> Int
{
    value => (value
        then { _ => _0 }
        else { _ => 0 })(())
}
""";

        var result = RunPipeline(source, CompilationPhase.Mir, UseSelectionFixturePath);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Borrow_Selection_RejectsRepeatedMoveOfMRefPayload()
    {
        const string source = """
import std.Option

MoveOnly :: type
{
    writer:: MRef[Int]
}

consume :: MoveOnly -> Unit
{
    _ => ()
}

bad :: Option[MoveOnly] -> Unit
{
    value => value then {
        consume(_0);
        consume(_0)
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Borrow, UseSelectionFixturePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E1001");
    }

    [Fact]
    public void Types_UserDefinedOptionLookalike_IsNotASelectionSubject()
    {
        const string source = """
Option[T] :: type { Some:: type(T), None:: type {} }

bad :: Option[Int] -> Unit
{
    value => value then ()
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E4023");
    }

    private static void UseSelectionFixturePath(CompilationOptions options)
    {
        options.InputFile = TestSourceLoader.GetFullPath("projects/test/src/stdlib/std_option_import.eidos");
    }

    private static string FormatDiagnostics(CompilationResult result) => string.Join(
        Environment.NewLine,
        result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
