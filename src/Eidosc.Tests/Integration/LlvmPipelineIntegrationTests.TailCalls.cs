using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void LlvmTailCalls_MutualRecursionWithDefaultMirOpt_EmitsMustTail()
    {
        const string Source = """
            even :: Int -> Bool
            {
                n => if n == 0 then { true } else { odd(n - 1) }
            }

            odd :: Int -> Bool
            {
                n => if n == 0 then { false } else { even(n - 1) }
            }
            """;

        var result = RunSourceAtLlvmWithDefaultMirOpt(Source, "tail_mutual_even_odd.eidos");

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.Matches(@"musttail call i1 @.*odd(?:_i[A-Za-z0-9_]+)?\(i64", result.LlvmIrText!);
        Assert.Matches(@"musttail call i1 @.*even(?:_i[A-Za-z0-9_]+)?\(i64", result.LlvmIrText!);
    }

    private static CompilationResult RunSourceAtLlvmWithDefaultMirOpt(string source, string inputFile)
    {
        var options = new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = CompilationPhase.Llvm,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }
}
