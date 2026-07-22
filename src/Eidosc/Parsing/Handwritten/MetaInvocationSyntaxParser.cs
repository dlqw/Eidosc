using Eidosc.Ast;
using Eidosc.Ast.Declarations;

namespace Eidosc.Parsing.Handwritten;

internal static class MetaInvocationSyntaxParser
{
    public static MetaInvocationSyntax Parse(
        ParserContext context,
        Func<EidosAstNode> parseArgument,
        string siteDescription)
    {
        var start = context.Current;
        var invocation = new MetaInvocationSyntax();
        var path = new List<string>();
        if (!TokenKind.IsAnyIdentifier(context.Current))
        {
            context.Error($"{siteDescription} requires a meta generator path");
            invocation.SetSpan(context.SpanFrom(start));
            return invocation;
        }

        path.Add(context.GetText());
        context.Advance();
        while (context.Match("."))
        {
            if (!TokenKind.IsAnyIdentifier(context.Current))
            {
                context.Error("expected a generator name after '.'");
                break;
            }

            path.Add(context.GetText());
            context.Advance();
        }

        invocation.SetGeneratorPath(path);
        if (context.Match("("))
        {
            if (!context.Check(")"))
            {
                invocation.AddExplicitArgument(parseArgument());
                while (context.Match(","))
                {
                    invocation.AddExplicitArgument(parseArgument());
                }
            }
            context.Expect(")");
        }

        invocation.SetSpan(context.SpanFrom(start));
        return invocation;
    }
}
