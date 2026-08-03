using System.Text.RegularExpressions;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class MirInliningShapeTests
{
    [Fact]
    public void DefaultOptimizer_SmallScalarHelper_IsInlined()
    {
        var result = CompilationHelper.Source(
            """
            increment :: Int -> Int { value => value + 1 }
            main :: Int -> Int { value => increment(value) }
            """).ToPhase(CompilationPhase.Llvm).ShouldSucceed();

        var mainBody = ExtractFunctionBody(result.LlvmIrText!, "eidos_main");

        Assert.DoesNotMatch(@"call i64 @[^\s(]*increment", mainBody);
    }

    private static string ExtractFunctionBody(string ir, string functionName)
    {
        var match = Regex.Match(
            ir,
            $@"define[^\n]*@{Regex.Escape(functionName)}\([^\n]*\)[^\n]*\n(?<body>.*?)(?=\ndefine|\z)",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"LLVM function '{functionName}' was not emitted.");
        return match.Groups["body"].Value;
    }
}
