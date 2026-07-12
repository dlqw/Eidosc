using System.Xml;
using Eidosc.Utils;

namespace Eidosc.Ast.Types;

/// <summary>
/// 效应类型
/// </summary>
/// <example>
/// Int ->{Console} -> Unit
/// String ->{Io}
/// </example>
public record EffectfulType : TypeNode
{
    /// <summary>
    /// 输入类型
    /// </summary>
    public TypeNode InputType { get; internal set; } = null!;

    /// <summary>
    /// 效应路径
    /// </summary>
    public List<string> EffectPath { get; internal set; } = [];

    /// <summary>
    /// 效应路径集合（用于 `->{A, B}` 语法）。
    /// </summary>
    public List<List<string>> EffectPaths { get; internal set; } = [];

    /// <summary>
    /// 每个 effect path 在源码中的精确跨度，与 <see cref="EffectPaths"/> 顺序对应。
    /// </summary>
    public List<SourceSpan> EffectPathSpans { get; internal set; } = [];

    /// <summary>
    /// 输出类型（可选）
    /// </summary>
    public TypeNode? OutputType { get; internal set; }

    /// <summary>
    /// 每个 effect path 对应的已解析能力符号。
    /// 与 <see cref="EnumerateEffectPaths"/> 顺序一一对应；未解析位置为 <see cref="SymbolId.None"/>。
    /// </summary>
    public List<SymbolId> EffectSymbolIds { get; set; } = [];

    public IEnumerable<IReadOnlyList<string>> EnumerateEffectPaths()
    {
        if (EffectPaths.Count > 0)
        {
            foreach (var effectPath in EffectPaths)
            {
                if (effectPath.Count > 0)
                {
                    yield return effectPath;
                }
            }

            yield break;
        }

        if (EffectPath.Count > 0)
        {
            yield return EffectPath;
        }
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            if (TryGetInputType(ntNode, out var inputType))
            {
                InputType = inputType;
            }

            if (TryGetEffectPaths(ntNode, out var effectPaths, out var effectPathSpans))
            {
                EffectPaths = effectPaths;
                EffectPathSpans = effectPathSpans;
                EffectPath = effectPaths.Count > 0 ? [.. effectPaths[0]] : [];
            }

            if (TryGetOutputType(ntNode, out var outputType))
            {
                OutputType = outputType;
            }
        }
    }

    private static bool TryGetInputType(NonTerminalCstNode node, out TypeNode inputType)
    {
        string? fallbackSimpleTypeName = null;

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode effectfulTail && IsNamed(effectfulTail, "effectfulTypeTail"))
            {
                break;
            }

            if (child is NonTerminalCstNode { AstNode: TypeNode typeNode })
            {
                inputType = typeNode;
                return true;
            }

            if (fallbackSimpleTypeName == null &&
                TryFindFirstTypeIdentifierToken(child, out var simpleTypeName))
            {
                fallbackSimpleTypeName = simpleTypeName;
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackSimpleTypeName))
        {
            inputType = CreateSimpleTypePath(fallbackSimpleTypeName!, node.Span);
            return true;
        }

        inputType = null!;
        return false;
    }

    private static bool TryGetEffectPaths(
        NonTerminalCstNode node,
        out List<List<string>> effectPaths,
        out List<SourceSpan> effectPathSpans)
    {
        if (!TryFindNonTerminal(node, "effectfulTypeTail", out var effectfulTypeTailNode))
        {
            effectPaths = [];
            effectPathSpans = [];
            return false;
        }

        if (!TryFindNonTerminal(effectfulTypeTailNode, "effectSet", out var effectSetNode))
        {
            effectPaths = [];
            effectPathSpans = [];
            return false;
        }

        var entries = ParseEffectSet(effectSetNode);
        effectPaths = entries.Select(static entry => entry.Path).ToList();
        effectPathSpans = entries.Select(static entry => entry.Span).ToList();
        return true; // allow empty set: `->{}` 
    }

    private sealed record EffectPathEntry(List<string> Path, SourceSpan Span);

    private static List<EffectPathEntry> ParseEffectSet(NonTerminalCstNode effectSetNode)
    {
        if (!TryFindNonTerminal(effectSetNode, "effectSetBody", out var effectSetBody))
        {
            return [];
        }

        var paths = new List<EffectPathEntry>();
        foreach (var child in effectSetBody.Children)
        {
            if (child is NonTerminalCstNode tailNode && IsNamed(tailNode, "effectSetTail"))
            {
                foreach (var tailChild in tailNode.Children)
                {
                    var tailPath = ParseEffectPathEntry(tailChild);
                    if (tailPath != null)
                    {
                        paths.Add(tailPath);
                        break;
                    }
                }

                continue;
            }

            var firstPath = ParseEffectPathEntry(child);
            if (firstPath != null)
            {
                paths.Add(firstPath);
            }
        }

        return paths;
    }

    private static EffectPathEntry? ParseEffectPathEntry(ConcreteSyntaxNode node)
    {
        if (node is TerminalCstNode terminal)
        {
            var token = GetTokenText(terminal);
            if (string.IsNullOrWhiteSpace(token) || IsPunctuation(token))
            {
                return null;
            }

            return new EffectPathEntry([token.Trim()], terminal.Span);
        }

        if (node is NonTerminalCstNode nonTerminal)
        {
            if (IsNamed(nonTerminal, WellKnownStrings.XmlAttributes.EffectPath))
            {
                var parts = ExtractPath(nonTerminal)
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => part.Trim())
                    .ToList();
                return parts.Count == 0
                    ? null
                    : new EffectPathEntry(parts, nonTerminal.Span);
            }

            foreach (var child in nonTerminal.Children)
            {
                var childPath = ParseEffectPathEntry(child);
                if (childPath != null)
                {
                    return childPath;
                }
            }
        }

        return null;
    }

    private static bool TryGetOutputType(NonTerminalCstNode node, out TypeNode outputType)
    {
        if (!TryFindNonTerminal(node, "typeTail", out var typeTailNode))
        {
            outputType = null!;
            return false;
        }

        if (TryFindFirstTypeNode(typeTailNode, out outputType))
        {
            return true;
        }

        if (TryFindFirstTypeIdentifierToken(typeTailNode, out var fallbackSimpleTypeName))
        {
            outputType = CreateSimpleTypePath(fallbackSimpleTypeName, typeTailNode.Span);
            return true;
        }

        outputType = null!;
        return false;
    }

    private static bool TryFindNonTerminal(NonTerminalCstNode node, string targetName, out NonTerminalCstNode matchedNode)
    {
        if (IsNamed(node, targetName))
        {
            matchedNode = node;
            return true;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt && TryFindNonTerminal(childNt, targetName, out matchedNode))
            {
                return true;
            }
        }

        matchedNode = null!;
        return false;
    }

    private static bool TryFindFirstTypeNode(NonTerminalCstNode node, out TypeNode typeNode)
    {
        if (node.AstNode is TypeNode selfTypeNode)
        {
            typeNode = selfTypeNode;
            return true;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                if (childNt.AstNode is TypeNode directTypeNode)
                {
                    typeNode = directTypeNode;
                    return true;
                }

                if (TryFindFirstTypeNode(childNt, out typeNode))
                {
                    return true;
                }
            }
        }

        typeNode = null!;
        return false;
    }

    private static bool TryFindFirstTypeIdentifierToken(ConcreteSyntaxNode node, out string typeName)
    {
        if (node is TerminalCstNode terminal)
        {
            var text = GetTokenText(terminal);
            if (!IsPunctuation(text))
            {
                typeName = text;
                return true;
            }

            typeName = string.Empty;
            return false;
        }

        if (node is NonTerminalCstNode nonTerminal)
        {
            foreach (var child in nonTerminal.Children)
            {
                if (TryFindFirstTypeIdentifierToken(child, out typeName))
                {
                    return true;
                }
            }
        }

        typeName = string.Empty;
        return false;
    }

    private static TypePath CreateSimpleTypePath(string typeName, Eidosc.Utils.SourceSpan span)
    {
        var typePath = new TypePath();
        typePath.SetTypeName(typeName);
        typePath.SetSpan(span);
        return typePath;
    }

    private static bool IsNamed(NonTerminalCstNode node, string targetName)
    {
        var debugName = node.NonTerminal?.DebugName;
        if (!string.IsNullOrWhiteSpace(debugName) &&
            string.Equals(debugName, targetName, StringComparison.Ordinal))
        {
            return true;
        }

        var fallbackName = node.NonTerminal?.ToString();
        return string.Equals(fallbackName, targetName, StringComparison.Ordinal);
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.EffectfulType);

        if (InputType != null)
        {
            var inputElement = doc.CreateElement(WellKnownStrings.XmlElements.InputType);
            inputElement.AppendChild(InputType.ToXmlElement(doc));
            element.AppendChild(inputElement);
        }

        var serializedPaths = EnumerateEffectPaths()
            .Select(path => string.Join(WellKnownStrings.Separators.Path, path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        if (serializedPaths.Count > 0)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.EffectPath, serializedPaths[0]);
        }

        if (serializedPaths.Count > 1)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.EffectPaths, string.Join(", ", serializedPaths));
        }

        if (OutputType != null)
        {
            var outputElement = doc.CreateElement(WellKnownStrings.XmlElements.OutputType);
            outputElement.AppendChild(OutputType.ToXmlElement(doc));
            element.AppendChild(outputElement);
        }

        return element;
    }
}
