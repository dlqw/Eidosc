using System;
using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public class NeedClauseParsingTests
{
    [Fact]
    public void Parser_NeedClauseWithSingleEffect_ParsesRequiredEffect()
    {
        const string source = """
helper :: Unit -> Unit need Emitter
{
    _ => 0
}
""";

        var result = RunParser(source, "need_clause_single_ability_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var helper = Assert.Single(module.Declarations.OfType<FuncDef>(), func => func.Name == "helper");
        var signature = Assert.IsType<ArrowType>(Assert.Single(helper.Signature));
        var requirement = Assert.Single(helper.RequiredAbilities);

        Assert.Equal("Unit", Assert.IsType<TypePath>(signature.ParamType).TypeName);
        Assert.Equal("Unit", Assert.IsType<TypePath>(signature.ReturnType).TypeName);
        Assert.Equal(["Emitter"], requirement.Path);
    }

    [Fact]
    public void Parser_NeedClauseWithEffectList_ParsesMultipleEffectPaths()
    {
        const string source = """
helper :: Unit -> Unit need Core::Emitter, Core::Io::Logger
{
    _ => 0
}
""";

        var result = RunParser(source, "need_clause_ability_list_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var helper = Assert.Single(module.Declarations.OfType<FuncDef>(), func => func.Name == "helper");

        Assert.Equal(2, helper.RequiredAbilities.Count);
        Assert.Equal(["Core", "Emitter"], helper.RequiredAbilities[0].Path);
        Assert.Equal(["Core", "Io", "Logger"], helper.RequiredAbilities[1].Path);
    }

    [Fact]
    public void Parser_NeedClauseWithTypeParameter_ParsesPolymorphicRequirement()
    {
        const string source = """
helper[T: Emitter] :: Unit -> Int need T
{
    _ => 1
}
""";

        var result = RunParser(source, "need_clause_type_parameter_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var helper = Assert.Single(module.Declarations.OfType<FuncDef>(), func => func.Name == "helper");

        Assert.Equal(["T"], Assert.Single(helper.RequiredAbilities).Path);
    }

    [Fact]
    public void Parser_NeedClauseWithoutEffect_Fails()
    {
        const string source = """
helper :: Unit -> Unit need
{
    _ => 0
}
""";

        var result = RunParser(source, "need_clause_missing_ability_tests.eidos");

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
    }

    [Fact]
    public void Parser_NeedClauseWithValueExpression_Fails()
    {
        const string source = """
helper :: Unit -> Unit need emit("x")
{
    _ => 0
}
""";

        var result = RunParser(source, "need_clause_value_expression_tests.eidos");

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
    }

    [Fact]
    public void Parser_NeedClauseWithLowercaseEffectPath_Fails()
    {
        const string source = """
helper :: Unit -> Unit need emitter
{
    _ => 0
}
""";

        var result = RunParser(source, "need_clause_lowercase_ability_tests.eidos");

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
    }

    [Fact]
    public void Parser_EffectfulSignatureBraceSyntax_FailsWithMigrationHint()
    {
        var removedSyntax = "->" + "{Emitter}";
        var source = $$"""
helper :: Unit {{removedSyntax}} -> Unit
{
    _ => 0
}
""";

        var result = RunParser(source, "effectful_signature_removed_tests.eidos");

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
        var removedSyntaxExample = "->" + "{Effect}";
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("need", StringComparison.Ordinal) &&
            diagnostic.Message.Contains(removedSyntaxExample, StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_TupleParameterSignature_ParsesAsArrowType()
    {
        const string source = """
pair_sum :: (Int, Int) -> Int
{
    (left, right) => left + right
}
""";

        var result = RunParser(source, "tuple_parameter_signature_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var pairSum = Assert.Single(module.Declarations.OfType<FuncDef>(), func => func.Name == "pair_sum");
        var signature = Assert.IsType<ArrowType>(Assert.Single(pairSum.Signature));
        var parameter = Assert.IsType<TupleType>(signature.ParamType);

        Assert.Equal(2, parameter.Elements.Count);
        Assert.All(parameter.Elements, element => Assert.IsType<TypePath>(element));
        Assert.Equal("Int", Assert.IsType<TypePath>(signature.ReturnType).TypeName);
    }

    private static CompilationResult RunParser(string source, string inputFile)
    {
        var options = new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }
}
