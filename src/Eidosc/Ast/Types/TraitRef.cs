using System.Xml;
using Eidosc.Utils;

namespace Eidosc.Ast.Types;

/// <summary>
/// Trait 引用
/// </summary>
/// <example>
/// Eq
/// Ord
/// Show
/// Std.Io.Readable
/// </example>
public record TraitRef : EidosAstNode
{
    /// <summary>
    /// 模块路径
    /// </summary>
    public List<string> ModulePath { get; internal set; } = [];

    /// <summary>
    /// Trait 名称
    /// </summary>
    public string TraitName { get; private set; } = "";

    /// <summary>
    /// Trait 类型参数
    /// </summary>
    public List<TypeNode> TypeArgs { get; internal set; } = [];

    public List<GenericArgumentNode> GenericArguments { get; internal set; } = [];

    /// <summary>
    /// 设置 span
    /// </summary>
    public void SetSpan(SourceSpan span) => Span = span;

    /// <summary>
    /// 设置 Trait 名称
    /// </summary>
    public void SetTraitName(string name) => TraitName = name;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ModulePath.Clear();
        TraitName = "";
        TypeArgs.Clear();
        GenericArguments.Clear();

        if (node is NonTerminalCstNode ntNode)
        {
            var parts = new List<string>();
            CollectPathParts(ntNode, parts, inTypeArgs: false);
            CollectTypeArguments(ntNode);
            GenericArguments = TypeArgs
                .Select(type => (GenericArgumentNode)new UnresolvedGenericArgumentNode
                {
                    TypeCandidate = type,
                    Span = type.Span
                })
                .ToList();

            if (parts.Count > 0)
            {
                TraitName = parts[^1];
                if (parts.Count > 1)
                {
                    ModulePath = parts.Take(parts.Count - 1).ToList();
                }
            }
        }
    }

    private void CollectPathParts(ConcreteSyntaxNode node, List<string> parts, bool inTypeArgs)
    {
        if (node is TerminalCstNode term)
        {
            var text = GetTokenText(term);
            if (!inTypeArgs && !IsPunctuation(text))
            {
                parts.Add(text);
            }

            return;
        }

        if (node is NonTerminalCstNode ntNode)
        {
            var enteringTypeArgs = inTypeArgs || string.Equals(ntNode.NonTerminal?.DebugName, "typeArgs", StringComparison.Ordinal);
            foreach (var child in ntNode.Children)
            {
                CollectPathParts(child, parts, enteringTypeArgs);
            }
        }
    }

    private void CollectTypeArguments(ConcreteSyntaxNode node)
    {
        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        if (string.Equals(ntNode.NonTerminal?.DebugName, "typeArgs", StringComparison.Ordinal))
        {
            CollectTypeArgumentsFromTypeArgsNode(ntNode);
            return;
        }

        foreach (var child in ntNode.Children)
        {
            CollectTypeArguments(child);
        }
    }

    private void CollectTypeArgumentsFromTypeArgsNode(NonTerminalCstNode typeArgsNode)
    {
        foreach (var child in typeArgsNode.Children)
        {
            if (child is NonTerminalCstNode { AstNode: TypeNode typeNode })
            {
                TypeArgs.Add(typeNode);
                continue;
            }

            if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (!IsPunctuation(text))
                {
                    var simpleType = new TypePath();
                    simpleType.SetTypeName(text);
                    simpleType.SetSpan(term.Span);
                    TypeArgs.Add(simpleType);
                }

                continue;
            }

            if (child is NonTerminalCstNode childNt)
            {
                CollectTypeArgumentsFromTypeArgsNode(childNt);
            }
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.TraitRef);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, TraitName);

        if (ModulePath.Count > 0)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.ModulePath, string.Join(WellKnownStrings.Separators.Path, ModulePath));
        }

        if (TypeArgs.Count > 0)
        {
            var argsElement = doc.CreateElement(WellKnownStrings.XmlElements.TypeArgs);
            foreach (var arg in TypeArgs)
            {
                argsElement.AppendChild(arg.ToXmlElement(doc));
            }

            element.AppendChild(argsElement);
        }

        return element;
    }

    internal void SetGenericArguments(IEnumerable<GenericArgumentNode> arguments)
    {
        GenericArguments = [.. arguments];
        TypeArgs = GenericArguments
            .Select(static argument => argument switch
            {
                UnresolvedGenericArgumentNode { TypeCandidate: { } type } => type,
                TypeGenericArgumentNode typeArgument => typeArgument.Type,
                _ => null
            })
            .OfType<TypeNode>()
            .ToList();
    }
}
