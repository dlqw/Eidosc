using System;
using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_ApplicationSpineOverloads_SelectUsingAllAppliedArguments()
    {
        const string source = """
pick[A, B] :: A -> B -> Int
{
    _ => _ => 1
}

pick[A] :: A -> Int -> Int
{
    _ => value => value
}

main :: Unit -> Int
{
    _ => pick("x")(2)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "application_spine_overloads.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false,
            NoImplicitPrelude = true
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

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
    export pick :: Int -> Int
    {
        value => value + 1
    }

    export pick :: String -> Int
    {
        _ => 2
    }
}

main :: Unit -> Int
{
    _ => A.pick(1) + A.pick("s")
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

        var qualifiedCalls = AstStableNodeTraversal
            .Enumerate(Assert.IsType<ModuleDecl>(result.Ast))
            .Select(static entry => entry.Node)
            .OfType<PathExpr>()
            .Where(static path => path.Name == "pick")
            .OrderBy(static call => call.Span.Position)
            .ToArray();
        Assert.Equal(2, qualifiedCalls.Length);
        Assert.All(qualifiedCalls, static call => Assert.Equal(2, call.ValueCandidateSymbolIds.Count));
        Assert.Equal(qualifiedCalls[0].ValueCandidateSymbolIds[0], qualifiedCalls[0].SymbolId);
        Assert.Equal(qualifiedCalls[1].ValueCandidateSymbolIds[1], qualifiedCalls[1].SymbolId);
    }

    [Fact]
    public void CompilationPipeline_QualifiedOverloadInsideOwner_UsesCompleteCurriedApplicationSpine()
    {
        const string source = """
A :: module {
    export choose :: Int -> String -> Int
    {
        _ => _ => 1
    }

    export choose :: Int -> Bool -> Int
    {
        _ => _ => 2
    }

    export choose_bool :: Int -> Bool -> Int
    {
        left => right => A.choose(left)(right)
    }
}

main :: Unit -> Int
{
    _ => A.choose_bool(1)(true)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "qualified_owner_application_spine.eidos",
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false,
            NoImplicitPrelude = true
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));

        var path = Assert.Single(
            AstStableNodeTraversal
                .Enumerate(Assert.IsType<ModuleDecl>(result.Ast))
                .Select(static entry => entry.Node)
                .OfType<PathExpr>(),
            static candidate => candidate.Name == "choose");
        Assert.Equal(2, path.ValueCandidateSymbolIds.Count);
        Assert.Equal(path.ValueCandidateSymbolIds[1], path.SymbolId);
    }

    [Fact]
    public void CompilationPipeline_RefParameter_ImplicitlySharedBorrowsWithoutConsumingValue()
    {
        const string source = """
Box :: type { Box:: type(Int) }

read :: Ref[Box] -> Int
{
    value => match *value {
        Box(number) => number
    }
}

main :: Unit -> Int
{
    _ => {
        box := Box(7);
        first := read(box);
        fluent := box.read();
        second := match box {
            Box(number) => number
        };
        first + fluent + second
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "implicit_shared_borrow_call_argument.eidos",
            StopAtPhase = CompilationPhase.Borrow,
            UseColors = false,
            NoImplicitPrelude = true
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
    }

    [Fact]
    public void CompilationPipeline_ImplicitBorrowOverloads_PreferExactValueAndSaturatedCandidates()
    {
        const string source = """
pick :: String -> Int
{
    _ => 1
}

pick :: Ref[String] -> Bool
{
    _ => false
}

write[T] :: Ref[T] -> Int
{
    _ => 1
}

write[T] :: String -> Ref[T] -> Int
{
    _ => _ => 2
}

main :: Unit -> Int
{
    _ => pick("value") + write("single")
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "implicit_borrow_overload_ranking.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false,
            NoImplicitPrelude = true
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
    }
}
