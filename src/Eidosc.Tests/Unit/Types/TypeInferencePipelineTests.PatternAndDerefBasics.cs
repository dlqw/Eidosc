using System;
using System.IO;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_ReturnExpression_UnifiesWithFunctionResultType()
    {
        const string source = """
id :: Int -> Int
{
    x => return x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Cannot unify type 'Int' with '()'", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_UnreachableExpression_JoinsWithSiblingBranchType()
    {
        const string source = """
choose :: Bool -> Int
{
    true => 1,
    false => unreachable
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_NeverReturnFunction_RequiresDivergingBody()
    {
        const string source = """
bad :: Unit -> Never
{
    _ => 1
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Cannot unify type 'Never' with 'Int'", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_NeverReturningCall_CanSatisfyOrdinaryResultContext()
    {
        const string source = """
fail :: Unit -> Never
{
    _ => unreachable
}

main :: Unit -> Int
{
    _ => fail(())
}
""";

        var result = RunPipelineWithTemporaryInput(source, CompilationPhase.Llvm);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void Types_ReturnExpression_JoinsWithSiblingBranchType()
    {
        const string source = """
choose :: Bool -> Int
{
    flag => if flag then { 1 } else { return 2 }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_UnreachableArgument_SatisfiesExpectedParameterType()
    {
        const string source = """
id :: Int -> Int
{
    x => x
}

main :: Unit -> Int
{
    _ => id(unreachable)
}
""";

        var result = RunPipelineWithTemporaryInput(source, CompilationPhase.Llvm);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void Types_BreakAndContinue_AreNeverExpressions()
    {
        const string source = """
main :: Bool -> Int
{
    flag => {
        loop {
            if flag then { break } else { continue };
            1
        };
        2
    }
}
""";

        var result = RunPipelineWithTemporaryInput(source, CompilationPhase.Llvm);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void Types_UnaryDeref_RequiresReferenceOperandAndReturnsInnerType()
    {
        const string source = """
id :: Ref[Int] -> Int
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "id");
        var inferredType = Assert.IsType<TyFun>(function.InferredType);
        var inferredResult = Assert.IsType<TyCon>(inferredType.Result);

        Assert.Equal("Int", inferredResult.Name);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_UnaryDeref_WithMRefOperand_ReturnsInnerType()
    {
        const string source = """
id :: MRef[Int] -> Int
{
    x => x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_UnaryDeref_OnNonReferenceOperand_FailsInTypes()
    {
        const string source = """
id :: Int -> Int
{
    x => *x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
    }

    [Fact]
    public void Types_TypeError_ReportsCompletedPhaseAsTypes()
    {
        const string source = """
broken :: Int -> Int
{
    _ => "oops"
}
""";

        var result = RunPipeline(source, CompilationPhase.Borrow);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Types, result.CompletedPhase);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_UnresolvedValueWhenTargetingLlvm_StopsBeforeOperandLowering()
    {
        const string source = """
id[T] :: T -> T
{
    x => x
}

main :: Unit -> Int
{
    _ => id(missing)
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Namer, result.CompletedPhase);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code is "E3000" or "E4000");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E0001");
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains(
                "Unresolved value type leaked into LLVM operand lowering",
                StringComparison.Ordinal));
    }

    private static CompilationResult RunPipeline(
        string source,
        CompilationPhase stopAt,
        Action<CompilationOptions>? configure = null)
    {
        var options = new CompilationOptions
        {
            InputFile = "types_pipeline_tests.eidos",
            StopAtPhase = stopAt,
            UseColors = false
        };
        configure?.Invoke(options);

        return new CompilationPipeline(source, options).Run();
    }

    private static CompilationResult RunPipelineWithTemporaryInput(
        string source,
        CompilationPhase stopAt,
        Action<CompilationOptions>? configure = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_types_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputFile = Path.Combine(tempDir, "main.eidos");
        File.WriteAllText(inputFile, source);

        try
        {
            return RunPipeline(source, stopAt, options =>
            {
                options.InputFile = inputFile;
                configure?.Invoke(options);
            });
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static int FindLine(string source, string needle)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(needle, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Cannot find line containing '{needle}'.");
    }
}
