using Eidosc.Mir;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void LlvmPhase_FunctionalInfixChainStyleTutorial_PartialOperatorCallsHaveMirTypes()
    {
        var result = RunFixtureAtLlvm(Paths.TutorialExample("55_functional_infix_chain_style.eidos"));

        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == MirValidator.UnknownTypeIdCode);
    }

    [Fact]
    public void Operators_CustomSymbolicOperator_NativeSmoke_ReturnsExpectedValue()
    {
        const string source = """
            (|+|) :: Int -> Int -> Int
            {
                left => right => left + right + 10
            }

            main :: Unit -> Int
            {
                _ => {
                    infixed := 1 |+| 2;
                    prefixed := (|+|)(3, 4);
                    infixed + prefixed
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "custom_symbolic_operator.eidos",
            "custom_symbolic_operator");

        Assert.Equal(30, execution.ExitCode);
    }

    [Fact]
    public void Operators_ComplexStdlibOperators_NativeSmoke_ReturnsExpectedValue()
    {
        const string source = """
            import std.Applicative
            import std.Functions
            import std.Functor
            import std.Monad
            import std.Option
            import std.Semigroup

            inc :: Int -> Int
            {
                x => x + 1
            }

            double :: Int -> Int
            {
                x => x + x
            }

            lift_plus_ten :: Int -> Option[Int]
            {
                x => Some(x + 10)
            }

            collapse :: Option[Int] -> Int
            {
                Some(value) => value,
                None() => 0
            }

            main :: Unit -> Int
            {
                _ => {
                    piped := 4 |> inc;
                    composedRight := (inc >>> double)(3);
                    composedLeft := (double <<< inc)(3);
                    appended := 20 <> 3;
                    mapped := collapse(inc <$> Some(5));
                    applied := collapse(Some(inc) <*> Some(5));
                    bound := collapse(Some(5) >>= lift_plus_ten);
                    piped + composedRight + composedLeft + appended + mapped + applied + bound
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "complex_stdlib_operators.eidos",
            "complex_stdlib_operators");

        Assert.Equal(71, execution.ExitCode);
    }
}

