using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public sealed class CurriedBinderListSyntaxTests
{
    [Fact]
    public void Parser_UnparenthesizedBinderList_IsDistinctFromSingleTupleParameter()
    {
        const string source = """
add :: Int -> Int -> Int {
    left, right => left + right
}

sum_pair :: (Int, Int) -> Int {
    (left, right) => left + right
}

sum_two_pairs :: (Int, Int) -> (Int, Int) -> Int {
    (left_a, left_b), (right_a, right_b) => left_a + left_b + right_a + right_b
}

add_parenthesized :: Int -> Int -> Int {
    left => (right) => left + right
}
""";

        var result = Run(source, CompilationPhase.Parser);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);

        var add = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "add");
        var addBranch = Assert.Single(add.Body);
        Assert.Collection(
            addBranch.ParameterPatterns,
            pattern => Assert.Equal("left", Assert.IsType<VarPattern>(pattern).Name),
            pattern => Assert.Equal("right", Assert.IsType<VarPattern>(pattern).Name));
        Assert.IsType<TuplePattern>(addBranch.Pattern);

        var sumPair = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "sum_pair");
        var pairBranch = Assert.Single(sumPair.Body);
        var tupleParameter = Assert.IsType<TuplePattern>(Assert.Single(pairBranch.ParameterPatterns));
        Assert.Same(tupleParameter, pairBranch.Pattern);
        Assert.Equal(2, tupleParameter.Elements.Count);

        var sumTwoPairs = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "sum_two_pairs");
        var twoPairBranch = Assert.Single(sumTwoPairs.Body);
        Assert.Collection(
            twoPairBranch.ParameterPatterns,
            pattern => Assert.Equal(2, Assert.IsType<TuplePattern>(pattern).Elements.Count),
            pattern => Assert.Equal(2, Assert.IsType<TuplePattern>(pattern).Elements.Count));

        var addParenthesized = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "add_parenthesized");
        var parenthesizedBranch = Assert.Single(addParenthesized.Body);
        Assert.Collection(
            parenthesizedBranch.ParameterPatterns,
            pattern => Assert.Equal("left", Assert.IsType<VarPattern>(pattern).Name),
            pattern => Assert.Equal("right", Assert.IsType<VarPattern>(pattern).Name));
    }

    [Fact]
    public void Types_BinderListPatternMatrix_BindsAllParametersBeforeGuard()
    {
        const string source = """
MaybeInt :: type { SomeInt :: type(Int), NoInt :: type {} }

same_value :: MaybeInt -> MaybeInt -> Bool {
    SomeInt(left), SomeInt(right) when left == right => true,
    NoInt(), NoInt()                                 => true,
    _, _                                             => false
}
""";

        var result = Run(source, CompilationPhase.Types);

        Assert.Equal(CompilationPhase.Types, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void Borrow_BinderListLambda_SupportsPartialAndSaturatedApplication()
    {
        const string source = """
add :: Int -> Int -> Int {
    left, right => left + right
}

apply :: (Int -> Int -> Int) -> Int {
    operation => operation(3)(4)
}

main :: Unit -> Int {
    _ => {
        add_one := add(1);
        direct  := add(1)(2);
        indirect := apply({ left, right => left + right });
        add_one(direct + indirect)
    }
}
""";

        var result = Run(source, CompilationPhase.Borrow);

        Assert.Equal(CompilationPhase.Borrow, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void Parser_BinderListLambda_StoresOnePatternPerCurriedParameter()
    {
        const string source = """
apply :: (Int -> Int -> Int) -> Int {
    operation => operation(1)(2)
}

main :: Unit -> Int {
    _ => apply({ left, right => left + right })
}
""";

        var result = Run(source, CompilationPhase.Parser);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var main = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "main");
        var call = Assert.IsType<CallExpr>(Assert.Single(main.Body).Expression);
        var lambdaBlock = Assert.IsType<BlockExpr>(Assert.Single(call.PositionalArgs));
        var lambda = Assert.IsType<LambdaExpr>(lambdaBlock.ResultExpression);
        Assert.Collection(
            lambda.Parameters,
            pattern => Assert.Equal("left", Assert.IsType<VarPattern>(pattern).Name),
            pattern => Assert.Equal("right", Assert.IsType<VarPattern>(pattern).Name));
    }

    private static CompilationResult Run(string source, CompilationPhase phase)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "curried_binder_list_syntax_tests.eidos",
            StopAtPhase = phase,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
