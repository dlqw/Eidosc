using Eidosc.Ast.Types;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// Extracts declaration type annotations from CST wrapper nodes that do not have direct AST nodes.
/// </summary>
internal static class DeclarationTypeAnnotationExtractor
{
    public static bool TryExtract(NonTerminalCstNode node, out TypeNode typeAnnotation)
    {
        if (node.AstNode is TypeNode directType)
        {
            typeAnnotation = directType;
            return true;
        }

        var typeNode = FindTypeNode(node, "effectfulType") ??
                       FindTypeNode(node, "arrowType") ??
                       FindTypeNode(node, "tupleType") ??
                       FindTypeNode(node, "primaryType") ??
                       (ContainsTypeIdentifier(node) ? node : null);
        if (typeNode == null)
        {
            typeAnnotation = default!;
            return false;
        }

        typeAnnotation = CreateTypeNode(typeNode);
        return true;
    }

    private static NonTerminalCstNode? FindTypeNode(NonTerminalCstNode node, string nodeName)
    {
        if (string.Equals(GetNodeName(node), nodeName, StringComparison.Ordinal))
        {
            return node;
        }

        foreach (var child in node.Children.OfType<NonTerminalCstNode>())
        {
            var result = FindTypeNode(child, nodeName);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static TypeNode CreateTypeNode(NonTerminalCstNode node)
    {
        TypeNode result = GetNodeName(node) switch
        {
            "effectfulType" => new EffectfulType(),
            "arrowType" => new ArrowType(),
            "tupleType" => new TupleType(),
            _ => new TypePath()
        };

        result.BuildFromCst(new AstContext(), node);
        return result;
    }

    private static bool ContainsTypeIdentifier(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal &&
                string.Equals(terminal.Terminal?.ToString(), WellKnownStrings.Terminals.Identifier, StringComparison.Ordinal))
            {
                return true;
            }

            if (child is NonTerminalCstNode childNode && ContainsTypeIdentifier(childNode))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetNodeName(NonTerminalCstNode node)
    {
        return node.NonTerminal?.DebugName ?? node.NonTerminal?.ToString() ?? string.Empty;
    }
}
