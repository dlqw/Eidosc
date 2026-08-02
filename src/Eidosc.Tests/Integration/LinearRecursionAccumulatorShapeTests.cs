using System.Text.RegularExpressions;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

/// <summary>
/// Regression gate for the linear-recursion accumulator transform (Eidosc #40):
/// a naive double-recursive fib must emit a single self call inside a loop with
/// a back edge instead of two recursive calls. Uses the Benchmark category so
/// the shape stays part of the call-layer performance gates.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Category, TestCategories.Benchmark)]
public sealed class LinearRecursionAccumulatorShapeTests
{
    [Fact]
    public void FibonacciNaive_EmittedIrHasSingleSelfCallAndLoopBackEdge()
    {
        var result = CompilationHelper.Source(
            """
            fib :: Int -> Int
            {
                n => if n < 2 then { n } else { fib(n - 1) + fib(n - 2) }
            }

            main :: Int -> Int { n => fib(n) }
            """).ToPhase(CompilationPhase.Llvm).ShouldSucceed();

        var fibBody = ExtractFunctionBody(result.LlvmIrText!, "eidos_fib");

        // The double recursion must be reduced to a single self call.
        Assert.Equal(1, Regex.Matches(fibBody, @"call i64 @eidos_fib").Count);
        Assert.True(HasLoopBackEdge(fibBody), "expected a loop back edge in fib IR");
    }

    private static string ExtractFunctionBody(string ir, string functionName)
    {
        var start = ir.IndexOf($"define", StringComparison.Ordinal);
        while (start >= 0)
        {
            var bodyStart = start;
            var headerEnd = ir.IndexOf('\n', bodyStart);
            if (headerEnd >= 0 && ir[bodyStart..headerEnd].Contains(functionName, StringComparison.Ordinal))
            {
                var next = ir.IndexOf("define", headerEnd, StringComparison.Ordinal);
                return next < 0 ? ir[bodyStart..] : ir[bodyStart..next];
            }

            start = headerEnd < 0 ? -1 : ir.IndexOf("define", headerEnd, StringComparison.Ordinal);
        }

        return "";
    }

    private static bool HasLoopBackEdge(string functionBody)
    {
        foreach (Match branch in Regex.Matches(functionBody, @"br i1[^\n]*"))
        {
            foreach (Match target in Regex.Matches(branch.Value, @"label %(bb\d+)"))
            {
                var targetId = target.Groups[1].Value;
                var blockDefinition = $"\n  {targetId}:";
                var definitionIndex = functionBody.IndexOf(blockDefinition, StringComparison.Ordinal);
                if (definitionIndex >= 0 && definitionIndex < branch.Index)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
