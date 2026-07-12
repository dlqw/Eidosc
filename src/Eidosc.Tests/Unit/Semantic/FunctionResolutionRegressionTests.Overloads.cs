using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_SameScopeOverloads_SelectByArgumentType()
    {
        const string source = """
pick :: Int -> Int
{
    value => value + 1
}

pick :: String -> Int
{
    _ => 2
}

main :: Unit -> Int
{
    _ => pick(1) + pick("s")
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "same_scope_overloads_select_by_argument_type.eidos",
            StopAtPhase = CompilationPhase.Llvm,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_SameScopeOverloads_RejectDuplicateParameterSignature()
    {
        const string source = """
parse :: String -> Int
{
    _ => 1
}

parse :: String -> String
{
    text => text
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "same_scope_overloads_duplicate_signature.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3001" &&
                          diagnostic.Message.Contains("Duplicate overload for function 'parse'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_SameScopeOverloads_RejectAlphaEquivalentGenericSignature()
    {
        const string source = """
id[T] :: T -> T
{
    value => value
}

id[U] :: U -> U
{
    value => value
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "same_scope_overloads_alpha_equivalent_generic_signature.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3001" &&
                          diagnostic.Message.Contains("Duplicate overload for function 'id'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_InstanceMethods_DoNotEnterOrdinaryOverloadDuplicateSet()
    {
        const string source = """
Label :: trait
{
    label :: Int -> String;
}

Caption :: trait
{
    label :: Int -> String;
}

IntLabel :: instance Label
{
    label :: Int -> String
    {
        _ => "label"
    }
}

IntCaption :: instance Caption
{
    label :: Int -> String
    {
        _ => "caption"
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "instance_methods_not_ordinary_overload_duplicates.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Duplicate overload for function 'label'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_SameScopeOverloads_MethodCallSelectsByReceiverType()
    {
        const string source = """
score :: Int -> Int
{
    value => value + 1
}

score :: String -> Int
{
    _ => 5
}

main :: Unit -> Int
{
    _ => 1.score() + "abc".score()
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "same_scope_overloads_method_call.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Ambiguous callable overload", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_SameScopeOverloads_InfixCallSelectsByArgumentTypes()
    {
        const string source = """
join :: Int -> Int -> Int
{
    left => right => left + right
}

join :: String -> String -> Int
{
    _ => _ => 3
}

main :: Unit -> Int
{
    _ => (1 `join` 2) + ("a" `join` "b")
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "same_scope_overloads_infix_call.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_BareSameScopeOverloadReference_RequiresCallSiteTypeInfo()
    {
        const string source = """
pick :: Int -> Int
{
    value => value + 1
}

pick :: String -> Int
{
    _ => 2
}

f :: pick;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "bare_same_scope_overload_reference.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("requires call-site type information", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BareSameScopeOverloadReference_UsesExpectedFunctionType()
    {
        const string source = """
format :: Int -> String
{
    _ => "int"
}

format :: Bool -> String
{
    value => if value then { "true" } else { "false" }
}

formatter :: Int -> String = format;

main :: Unit -> String
{
    _ => formatter(1)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "bare_same_scope_overload_reference_expected_type.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_BareSameScopeOverloadReference_PrefersConcreteExpectedFunctionType()
    {
        const string source = """
format :: Int -> String
{
    _ => "int"
}

format[T] :: T -> String
{
    _ => "generic"
}

formatter :: Int -> String = format;

main :: Unit -> String
{
    _ => formatter(1)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "bare_same_scope_overload_reference_prefers_concrete_expected_type.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_SameScopeOverloads_PipeCallSelectsByLeftOperandType()
    {
        const string source = """
score :: Int -> Int
{
    value => value + 1
}

score :: String -> Int
{
    _ => 5
}

main :: Unit -> Int
{
    _ => (1 |> score) + ("abc" |> score)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "same_scope_overloads_pipe_call.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_QualifiedOverloads_SelectByArgumentType()
    {
        const string source = """
A :: module {
    pick :: Int -> Int
    {
        value => value + 1
    }

    pick :: String -> Int
    {
        _ => 2
    }
}

main :: Unit -> Int
{
    _ => A::pick(1) + A::pick("s")
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "qualified_overloads_select_by_argument_type.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }
}
