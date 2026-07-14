using System.Xml;

namespace Eidosc.Ast.Types;

/// <summary>
/// 类型路径引用
/// </summary>
/// <example>
/// Int
/// Seq[Int]
/// std/collection.List
/// Std.Collection.Seq.List
/// </example>
public record TypePath : TypeNode
{
    /// <summary>
    /// 包别名部分。为空表示当前 package 或未显式限定的导入模块。
    /// </summary>
    public string? PackageAlias { get; internal set; }

    /// <summary>
    /// 模块路径部分
    /// </summary>
    public List<string> ModulePath { get; internal set; } = [];

    /// <summary>
    /// 类型名称
    /// </summary>
    public string TypeName { get; private set; } = "";

    /// <summary>
    /// 类型参数
    /// </summary>
    public List<TypeNode> TypeArgs { get; internal set; } = [];

    /// <summary>
    /// Ordered generic arguments with an explicit type/value/effect domain after name resolution.
    /// </summary>
    public List<GenericArgumentNode> GenericArguments { get; internal set; } = [];

    /// <summary>
    /// 设置类型名称
    /// </summary>
    public void SetTypeName(string name) => TypeName = name;

    /// <summary>
    /// 设置包别名
    /// </summary>
    public void SetPackageAlias(string? packageAlias) => PackageAlias = string.IsNullOrWhiteSpace(packageAlias) ? null : packageAlias;

    public List<string> ToQualifiedPathParts()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(PackageAlias))
        {
            parts.Add(PackageAlias);
        }

        parts.AddRange(ModulePath);
        if (!string.IsNullOrWhiteSpace(TypeName))
        {
            parts.Add(TypeName);
        }

        return parts;
    }

    /// <summary>
    /// 设置位置
    /// </summary>
    public void SetSpan(Utils.SourceSpan span) => Span = span;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            if (TryBuildOptionSuffixType(ntNode))
            {
                return;
            }

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

            // 最后一个部分是类型名，其余是模块路径
            if (parts.Count > 0)
            {
                TypeName = parts[^1];
                if (parts.Count > 1)
                {
                    ModulePath = parts.Take(parts.Count - 1).ToList();
                }
            }
        }
    }

    private bool TryBuildOptionSuffixType(NonTerminalCstNode node)
    {
        var suffixCount = CountOptionSuffixes(node);
        if (suffixCount == 0)
        {
            return false;
        }

        var innerType = FindFirstChildType(node) ?? FindFirstTypeIdentifier(node);
        if (innerType == null)
        {
            return false;
        }

        TypeNode current = innerType;
        for (var i = 0; i < suffixCount; i++)
        {
            current = CreateOptionType(current, node.Span);
        }

        if (current is not TypePath optionType)
        {
            return false;
        }

        ModulePath = optionType.ModulePath;
        TypeName = optionType.TypeName;
        TypeArgs = optionType.TypeArgs;
        GenericArguments = optionType.GenericArguments;
        Span = node.Span;
        return true;
    }

    private static TypePath CreateOptionType(TypeNode innerType, Utils.SourceSpan span)
    {
        var optionType = new TypePath
        {
            ModulePath = ["Option"],
            TypeArgs = [innerType]
        };
        optionType.SetPackageAlias("Std");
        optionType.SetTypeName("Option");
        optionType.SetSpan(span);
        return optionType;
    }

    private static int CountOptionSuffixes(ConcreteSyntaxNode node)
    {
        var count = 0;
        Visit(node);
        return count;

        void Visit(ConcreteSyntaxNode current)
        {
            if (current is TerminalCstNode term)
            {
                count += GetTokenText(term) switch
                {
                    "?" => 1,
                    "??" => 2,
                    _ => 0
                };
                return;
            }

            if (current is NonTerminalCstNode ntNode)
            {
                if (string.Equals(ntNode.NonTerminal?.ToString(), "typeArgs", StringComparison.Ordinal))
                {
                    return;
                }

                foreach (var child in ntNode.Children)
                {
                    Visit(child);
                }
            }
        }
    }

    private static TypeNode? FindFirstChildType(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode { AstNode: TypeNode typeNode })
            {
                return typeNode;
            }

            if (child is NonTerminalCstNode childNt)
            {
                var nested = FindFirstChildType(childNt);
                if (nested != null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static TypeNode? FindFirstTypeIdentifier(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term &&
                string.Equals(term.Terminal?.ToString(), WellKnownStrings.Terminals.TypeIdentifier, StringComparison.Ordinal))
            {
                var typePath = new TypePath();
                typePath.SetTypeName(GetTokenText(term));
                typePath.SetSpan(term.Span);
                return typePath;
            }

            if (child is NonTerminalCstNode childNt)
            {
                var nested = FindFirstTypeIdentifier(childNt);
                if (nested != null)
                {
                    return nested;
                }
            }
        }

        return null;
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
            var enteringTypeArgs = inTypeArgs || ntNode.NonTerminal.ToString() == "typeArgs";
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

        if (ntNode.NonTerminal.ToString() == "typeArgs")
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
        var element = CreateElement(doc, WellKnownStrings.XmlElements.TypePath);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, TypeName);

        if (ModulePath.Count > 0)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.ModulePath, string.Join(WellKnownStrings.Separators.Path, ModulePath));
        }

        if (!string.IsNullOrWhiteSpace(PackageAlias))
        {
            element.SetAttribute("packageAlias", PackageAlias);
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

        if (GenericArguments.Count > 0)
        {
            var genericArgsElement = doc.CreateElement("GenericArguments");
            foreach (var argument in GenericArguments)
            {
                genericArgsElement.AppendChild(argument.ToXmlElement(doc));
            }

            element.AppendChild(genericArgsElement);
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
