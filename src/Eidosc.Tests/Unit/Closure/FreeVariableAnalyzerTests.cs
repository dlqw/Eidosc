using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Mir.Closure;
using Xunit;

namespace Eidosc.Tests.Unit.Closure;

public class FreeVariableAnalyzerTests
{
    [Fact]
    public void Analyze_LambdaBlockLetInitializer_ReportsOuterCapture()
    {
        var localPattern = new VarPattern();
        localPattern.SetName("local");

        var localDecl = new LetDecl();
        localDecl.SetPattern(localPattern);
        localDecl.SetValue(Identifier("outer"));

        var block = new BlockExpr();
        block.AddStatement(localDecl);
        block.SetResultExpression(Identifier("local"));

        var lambda = Lambda(block, "arg");

        var freeVariables = new FreeVariableAnalyzer().Analyze(lambda);

        Assert.Contains("outer", freeVariables);
        Assert.DoesNotContain("arg", freeVariables);
        Assert.DoesNotContain("local", freeVariables);
    }

    [Fact]
    public void Analyze_MethodAndInfixCalls_ReportReceiverAndOperandCaptures()
    {
        var methodCall = new MethodCallExpr();
        methodCall.SetReceiver(Identifier("receiver"));
        methodCall.SetMethodName("select");
        methodCall.AddPositionalArg(Identifier("fallback"));

        var infixCall = new InfixCallExpr();
        infixCall.SetFunctionName("combine");
        infixCall.SetLeft(methodCall);
        infixCall.SetRight(Identifier("right"));

        var lambda = Lambda(infixCall, "arg");

        var freeVariables = new FreeVariableAnalyzer().Analyze(lambda);

        Assert.Contains("receiver", freeVariables);
        Assert.Contains("fallback", freeVariables);
        Assert.Contains("right", freeVariables);
        Assert.DoesNotContain("combine", freeVariables);
        Assert.DoesNotContain("arg", freeVariables);
    }

    private static LambdaExpr Lambda(EidosAstNode body, params string[] parameters)
    {
        var lambda = new LambdaExpr();
        foreach (var parameter in parameters)
        {
            var pattern = new VarPattern();
            pattern.SetName(parameter);
            lambda.AddParameter(pattern);
        }

        lambda.SetBody(body);
        return lambda;
    }

    private static IdentifierExpr Identifier(string name)
    {
        var identifier = new IdentifierExpr();
        identifier.SetName(name);
        return identifier;
    }
}
