using Eidosc.Ast.Types;
using Eidosc.Utilities;

namespace Eidosc.Ast.Declarations;

internal static class FunctionSignatureBuilder
{
    public static TypeNode? BuildFromSignatureNode(NonTerminalCstNode signatureNode)
    {
        if (!string.Equals(signatureNode.NonTerminal?.DebugName, WellKnownStrings.Keywords.Signature, StringComparison.Ordinal))
        {
            return null;
        }

        return TryExtractSignatureTypeNode(signatureNode, out var typeNode)
            ? typeNode
            : null;
    }

    private static bool TryExtractSignatureTypeNode(ConcreteSyntaxNode node, out TypeNode typeNode)
    {
        if (node is NonTerminalCstNode ntNode)
        {
            var nodeName = ntNode.NonTerminal?.DebugName ?? string.Empty;
            if (ntNode.AstNode is TypeNode directType &&
                nodeName is WellKnownStrings.Keywords.ArrowType or WellKnownStrings.Keywords.EffectfulType or WellKnownStrings.Keywords.TupleType or WellKnownStrings.Keywords.TypePath or WellKnownStrings.Keywords.PrimaryType)
            {
                typeNode = directType;
                return true;
            }

            if (nodeName is WellKnownStrings.Keywords.Signature or WellKnownStrings.XmlAttributes.Type)
            {
                foreach (var child in ntNode.Children)
                {
                    if (TryExtractSignatureTypeNode(child, out typeNode))
                    {
                        return true;
                    }
                }
            }
        }

        return TryExtractDirectTypeNode(node, out typeNode);
    }

    private static bool TryExtractDirectTypeNode(ConcreteSyntaxNode node, out TypeNode typeNode)
    {
        if (node is NonTerminalCstNode { AstNode: TypeNode directType })
        {
            typeNode = directType;
            return true;
        }

        if (node is TerminalCstNode terminal &&
            terminal.Terminal?.ToString() is WellKnownStrings.Terminals.Identifier or WellKnownStrings.Terminals.Identifier)
        {
            var typePath = new TypePath();
            typePath.SetSpan(terminal.Span);
            typePath.SetTypeName(GetTerminalTypeName(terminal));
            typeNode = typePath;
            return true;
        }

        if (node is NonTerminalCstNode childNt)
        {
            foreach (var child in childNt.Children)
            {
                if (TryExtractDirectTypeNode(child, out typeNode))
                {
                    return true;
                }
            }
        }

        typeNode = null!;
        return false;
    }

    private static string GetTerminalTypeName(TerminalCstNode terminal)
    {
        if (terminal.Token is ContentToken contentToken)
        {
            if (contentToken.Value is string stringValue)
            {
                return stringValue;
            }

            return contentToken.TextId.Resolve();
        }

        return terminal.Token?.ToString() ?? string.Empty;
    }
}
