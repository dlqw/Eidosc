using Eidosc.Mir;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 0)]
    public void EitherDo_RightBiasedMonad_NativeSmokePreservesFirstLeft(
        bool fail,
        int expectedExitCode)
    {
        var second = fail ? "Left(\"failed\")" : "Right(3)";
        var source = $$"""
            import std.Either
            import std.Monad

            right_int :: Int -> Either.WithLeft[String, Int]
            {
                value => Right(value)
            }

            main :: Unit -> Int
            {
                result := do {
                    left <- right_int(2)
                    right <- {{second}}
                    right_int(left + right)
                };
                Either.unwrap_or(result)(0)
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            $"either_do_{fail}.eidos",
            $"either_do_{fail}");

        Assert.Equal(expectedExitCode, execution.ExitCode);
    }

    [Fact]
    public void EitherTraverse_WithOptionApplicative_NativeSmokePreservesRight()
    {
        const string source = """
            import std.Either
            import std.Option

            positive :: Int -> Option[Int]
            {
                value => if value > 0 then { Some(value + 1) } else { None() }
            }

            main :: Unit -> Int
            {
                input: Either.WithLeft[String, Int] := Right(4);
                traversed := Either.sequence(Either.map(input)(positive));
                match traversed {
                    Some(Right(value)) => value,
                    _ => 0
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "either_traverse_option.eidos",
            "either_traverse_option");

        Assert.Equal(5, execution.ExitCode);
    }

    [Fact]
    public void FoldableFoldMap_SeqUsesMonoidEvidence_NativeSmokeCombinesValues()
    {
        const string source = """
            import std.Foldable
            import std.Seq

            keep :: Int -> Int
            {
                value => value
            }

            main :: Unit -> Int
            {
                Foldable.fold_map([1, 2, 3, 4])(keep)
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "foldable_fold_map_seq.eidos",
            "foldable_fold_map_seq");

        Assert.Equal(10, execution.ExitCode);
    }

    [Fact]
    public void FoldableFoldMap_CurriedReducerSpecializationKeepsConcreteFunctionAbi()
    {
        const string source = """
            import std.Foldable
            import std.Seq

            keep :: Int -> Int
            {
                value => value
            }

            main :: Unit -> Int
            {
                Foldable.fold_map([1, 2, 3, 4])(keep)
            }
            """;

        var result = RunSourceAtMir(source, "foldable_fold_map_seq_mir.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var module = Assert.IsType<MirModule>(result.MirModule);
        var reducer = Assert.Single(module.Functions, static function => function.Name.StartsWith("__lambda_10__spec_", StringComparison.Ordinal));
        var parameters = reducer.Locals.Where(static local => local.IsParameter).Select(static local => local.TypeId).ToArray();
        var intType = new TypeId(BaseTypes.IntId);

        Assert.Equal(3, parameters.Length);
        Assert.Equal(intType, parameters[0]);
        Assert.Equal(intType, parameters[2]);
        Assert.Equal(intType, reducer.ReturnType);
        var projectType = Assert.IsType<TypeDescriptor.Function>(module.TypeDescriptors[parameters[1].Value]);
        Assert.Equal([intType], projectType.ParamTypes);
        Assert.Equal(intType, projectType.ReturnType);
    }

    [Fact]
    public void TaskMapAndThen_CompileWithPendingContinuationContract()
    {
        const string source = """
            import std.Task

            increment :: Int -> Task[Int]
            {
                value => Task.completed_value(value + 1)
            }

            main :: Unit -> Task[Int]
            {
                Task.and_then(Task.completed_value(4))(increment)
            }
            """;

        var result = RunSourceAtMir(source, "task_map_and_then_contract.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

}
