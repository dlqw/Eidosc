using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void StdFunctorFoldableTraitChainFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_functor_foldable_trait_chain.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.DoesNotContain("eidos_fold_left", result.LlvmIrText, StringComparison.Ordinal);
        Assert.DoesNotContain("call ptr @eidos_Std__Seq__fmap(ptr", result.LlvmIrText, StringComparison.Ordinal);
        AssertFunctionalAbstractionLoweringInvariants(result.LlvmIrText);
    }

    private static void AssertFunctionalAbstractionLoweringInvariants(string llvmIrText)
    {
        Assert.Contains("eidos_Std__Seq__sum", llvmIrText, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved_ref__", llvmIrText, StringComparison.Ordinal);
        Assert.DoesNotContain("declare ptr @eidos___lambda_", llvmIrText, StringComparison.Ordinal);
        Assert.DoesNotContain("declare i64 @eidos___lambda_", llvmIrText, StringComparison.Ordinal);
        Assert.DoesNotContain("declare i1 @eidos___lambda_", llvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void SeqFoldLeft_WithLocalCurriedClosure_NativeSmoke_InvokesClosureOneLayerAtATime()
    {
        const string source = """
            import Std.Seq

            main :: Unit -> Int
            {
                _ => {
                    add := acc => value => acc + value;
                    Seq.fold_left([21, 22, 23])(0)(add)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "fold_left_local_curried_closure.eidos",
            "fold_left_local_curried_closure");

        Assert.Equal(66, execution.ExitCode);
    }

    [Fact]
    public void ListFoldLeft_WithNestedClosureCaptureAndStringConcat_NativeSmoke_RendersRow()
    {
        const string source = """
            import Std.Seq
            import Std.Text

            cell :: Int -> Int -> String
            {
                food => x => if x == food then { "*" } else { "." }
            }

            row :: Int -> Seq[Int] -> String
            {
                food => xs => {
                    render_cell := x => cell(food)(x);
                    Seq.fold_left(xs)("")(acc => x => acc ++ render_cell(x))
                }
            }

            main :: Unit -> Int
            {
                _ => Text.len(row(2)([0, 1, 2, 3]))
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "fold_left_nested_capture_string_row.eidos",
            "fold_left_nested_capture_string_row");

        Assert.Equal(4, execution.ExitCode);
    }
}

