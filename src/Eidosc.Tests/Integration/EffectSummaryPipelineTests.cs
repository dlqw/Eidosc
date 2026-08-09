using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class EffectSummaryPipelineTests
{
    [Fact]
    public void MirPhase_PureCallWithConstantArgument_IsFolded()
    {
        var result = CompilationHelper.Source(
            """
            add_one :: Int -> Int { x => x + 1 }

            main :: Int -> Int { x => add_one(x) + add_one(10) }
            """).ToPhase(CompilationPhase.Mir).ShouldSucceed();

        // add_one(10) folds to 11; the remaining pure scalar call is inlined.
        Assert.Equal(0, CountCalls(result, "add_one"));
        Assert.Contains(11L, FindIntConstants(result));
    }

    [Fact]
    public void MirPhase_FoldedUnusedPureCall_IsEliminated()
    {
        var result = CompilationHelper.Source(
            """
            add_one :: Int -> Int { x => x + 1 }

            main :: Int -> Int { _ => { ignored := add_one(10); 0 } }
            """).ToPhase(CompilationPhase.Mir).ShouldSucceed();

        // The call folds to a constant that DCE then removes.
        Assert.Equal(0, CountCalls(result, "add_one"));
    }

    [Fact]
    public void MirPhase_EffectfulCall_IsPreserved()
    {
        var result = CompilationHelper.Source(
            """
            ConsoleOutput :: effect;

            log :: String -> Unit need ConsoleOutput
            {
                _ => ()
            }

            main :: Int -> Int need ConsoleOutput
            {
                _ => { log("hello"); 0 }
            }
            """).ToPhase(CompilationPhase.Mir).ShouldSucceed();

        // Declared effects block both folding and elimination.
        Assert.Equal(1, CountCalls(result, "log"));
    }

    private static int CountCalls(CompilationResult result, string calleeName)
    {
        var calls = 0;
        foreach (var function in result.MirModule!.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is MirCall
                        {
                            Function: MirFunctionRef { Name: var name }
                        } &&
                        name.Contains(calleeName, StringComparison.Ordinal))
                    {
                        calls++;
                    }
                }
            }
        }

        return calls;
    }

    private static List<long> FindIntConstants(CompilationResult result)
    {
        var constants = new List<long>();
        foreach (var function in result.MirModule!.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    switch (instruction)
                    {
                        case MirAssign { Source: var source }:
                            AddIntConstant(source, constants);
                            break;
                        case MirBinOp { Left: var left, Right: var right }:
                            AddIntConstant(left, constants);
                            AddIntConstant(right, constants);
                            break;
                    }
                }
            }
        }

        return constants;
    }

    private static void AddIntConstant(MirOperand operand, List<long> constants)
    {
        if (operand is MirConstant { Value: MirConstantValue.IntValue intValue })
        {
            constants.Add(intValue.Value);
        }
    }
}
