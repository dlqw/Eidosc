using Eidosc;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public sealed class ConditionalExpressionParsingTests
{
    [Fact]
    public void Parser_ShortIfBranches_ParseAsExpressions()
    {
        const string source = """
choose :: Bool -> Int {
    ok => if ok == true then next_value(1) else fallback
}
""";

        var result = RunParser(source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var ifExpr = Assert.IsType<IfExpr>(GetFuncBodyValue(result, "choose"));
        Assert.IsType<BinaryExpr>(ifExpr.Condition);
        Assert.IsType<CallExpr>(ifExpr.ThenBranch);
        var elseBranch = Assert.IsType<IdentifierExpr>(ifExpr.ElseBranch);
        Assert.Equal("fallback", elseBranch.Name);
    }

    [Fact]
    public void Parser_ShortIfIdentifierBranches_ParseAsThenAndElseExpressions()
    {
        const string source = """
choose :: Bool -> Int -> Int -> Int {
    ok => left => right => if ok then left else right
}
""";

        var result = RunParser(source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var ifExpr = Assert.IsType<IfExpr>(GetFuncBodyValue(result, "choose"));
        var thenBranch = Assert.IsType<IdentifierExpr>(ifExpr.ThenBranch);
        var elseBranch = Assert.IsType<IdentifierExpr>(ifExpr.ElseBranch);
        Assert.Equal("left", thenBranch.Name);
        Assert.Equal("right", elseBranch.Name);
    }

    [Fact]
    public void Parser_IfLetShortBranches_ParseAsExpressions()
    {
        const string source = """
OptionI :: type { Some(Int) | None }
value_or :: OptionI -> Int {
    value => if let Some(n) = value then n else 0
}
""";

        var result = RunParser(source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var ifLet = Assert.IsType<IfLetExpr>(GetFuncBodyValue(result, "value_or"));
        var thenBranch = Assert.IsType<IdentifierExpr>(ifLet.ThenBranch);
        Assert.Equal("n", thenBranch.Name);
        Assert.IsType<LiteralExpr>(ifLet.ElseBranch);
    }

    [Fact]
    public void Parser_ContextualProofBy_AllowsByAsRuntimeIdentifier()
    {
        const string source = """
echo_by :: Int -> Int {
    by => by
}
""";

        var result = RunParser(source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "echo_by");
        var branch = Assert.Single(func.Body);

        var binding = Assert.IsType<VarPattern>(branch.Pattern);
        Assert.Equal("by", binding.Name);

        var resultExpr = Assert.IsType<IdentifierExpr>(branch.Expression);
        Assert.Equal("by", resultExpr.Name);
    }

    private static CompilationResult RunParser(string source)
    {
        var options = new CompilationOptions
        {
            InputFile = "conditional_expression_parsing_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static Eidosc.Ast.EidosAstNode GetFuncBodyValue(CompilationResult result, string funcName)
    {
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var decl = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == funcName);
        var branch = Assert.Single(decl.Body);
        Assert.NotNull(branch.Expression);
        return branch.Expression!;
    }
}
