using System;
using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public class ListComprehensionParsingTests
{
    [Fact]
    public void Parser_ListComprehension_MultipleGeneratorsAndGuard_ParsesQualifierKinds()
    {
        const string source = """
xs :: [a + b | a <- [1, 2], b <- [10, 20], a + b > 15];
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetLetValue(result, "xs");
        var comp = Assert.IsType<ListComprehension>(value);

        Assert.Equal(3, comp.Qualifiers.Count);
        Assert.Equal(QualifierKind.Generator, comp.Qualifiers[0].Kind);
        Assert.Equal(QualifierKind.Generator, comp.Qualifiers[1].Kind);
        Assert.Equal(QualifierKind.Guard, comp.Qualifiers[2].Kind);

        Assert.NotNull(comp.Qualifiers[0].GeneratorPattern);
        Assert.NotNull(comp.Qualifiers[0].GeneratorExpression);
        Assert.NotNull(comp.Qualifiers[1].GeneratorPattern);
        Assert.NotNull(comp.Qualifiers[1].GeneratorExpression);
        Assert.NotNull(comp.Qualifiers[2].GuardExpression);
        var guardExpr = Assert.IsType<BinaryExpr>(comp.Qualifiers[2].GuardExpression);
        Assert.Equal(Eidosc.Ast.BinaryOp.Greater, guardExpr.Operator);
    }

    [Fact]
    public void Parser_ListComprehension_CallGuard_DoesNotEmitSpeculativePatternError()
    {
        const string source = """
xs :: [x | x <- [1, 2], pred(x)];
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4001" &&
                          diagnostic.Message.Contains("expected pattern", StringComparison.OrdinalIgnoreCase));

        var value = GetLetValue(result, "xs");
        var comp = Assert.IsType<ListComprehension>(value);
        Assert.Equal(QualifierKind.Guard, comp.Qualifiers[1].Kind);
        Assert.IsType<CallExpr>(comp.Qualifiers[1].GuardExpression);
    }

    [Fact]
    public void Namer_ListComprehension_BindingsVisibleInLaterQualifiersAndOutput()
    {
        const string source = """
xs :: [a + b | a <- [1, 2], b <- [10, 20], a + b > 15];
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Undefined variable", StringComparison.Ordinal));
    }

    [Fact]
    public void Namer_ListComprehension_BindingDoesNotLeakOutside()
    {
        const string source = """
xs :: [x | x <- [1, 2]];
y :: x;
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Undefined", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("'x'", StringComparison.Ordinal));
    }

    private static CompilationResult RunPipeline(string source, CompilationPhase stopAt)
    {
        var options = new CompilationOptions
        {
            InputFile = "list_comprehension_parser_tests.eidos",
            StopAtPhase = stopAt,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static Eidosc.Ast.EidosAstNode GetLetValue(CompilationResult result, string letName)
    {
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var decl = Assert.Single(
            module.Declarations.OfType<LetDecl>(),
            value => value.Pattern is VarPattern { Name: var name } && name == letName);
        Assert.NotNull(decl.Value);
        return decl.Value!;
    }
}
