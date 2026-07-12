using System.Xml;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 路径表达式
/// </summary>
/// <example>
/// std/io::print
/// std::io::print
/// </example>
public record PathExpr : Expression
{
    /// <summary>
    /// 显式 package 别名。
    /// </summary>
    public string? PackageAlias { get; private set; }

    /// <summary>
    /// 模块路径部分
    /// </summary>
    public List<string> ModulePath { get; private set; } = [];

    /// <summary>
    /// 完整路径（模块路径 + 名称）
    /// </summary>
    public List<string> Path
    {
        get
        {
            var result = new List<string>();
            if (!string.IsNullOrWhiteSpace(PackageAlias))
            {
                result.Add(PackageAlias);
            }

            result.AddRange(ModulePath);
            if (!string.IsNullOrEmpty(Name))
                result.Add(Name);
            return result;
        }
    }

    /// <summary>
    /// 最终名称（函数名、类型名等）
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>
    /// 显式类型参数（用于 `f[Int]` 形式）
    /// </summary>
    public List<TypeNode> TypeArgs { get; private set; } = [];

    public List<SymbolId> ValueCandidateSymbolIds { get; private set; } = [];

    /// <summary>
    /// 反糖化时重写路径名称和类型参数
    /// </summary>
    internal void Desugar(string newName)
    {
        Name = newName;
        TypeArgs.Clear();
        ValueCandidateSymbolIds.Clear();
    }

    public void ClearValueCandidates() => ValueCandidateSymbolIds.Clear();

    public void AddValueCandidate(SymbolId symbolId)
    {
        if (symbolId.IsValid && !ValueCandidateSymbolIds.Contains(symbolId))
        {
            ValueCandidateSymbolIds.Add(symbolId);
        }
    }

    /// <summary>
    /// 是否是类型路径
    /// </summary>
    public bool IsTypePath { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            var parts = new List<string>();
            CollectPathParts(ntNode, parts, inTypeArgs: false);
            CollectTypeArguments(ntNode);

            if (parts.Count > 0)
            {
                Name = parts[^1];
                if (parts.Count > 1)
                {
                    ModulePath = parts.Take(parts.Count - 1).ToList();
                }
            }

            // 判断是否是类型路径（首字母大写）
            IsTypePath = Name.Length > 0 && char.IsUpper(Name[0]);
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
        var element = CreateElement(doc, WellKnownStrings.XmlElements.PathExpr);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);
        element.SetAttribute(WellKnownStrings.XmlAttributes.IsTypePath, IsTypePath.ToString());

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

        return element;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetName(string name) => Name = name;
    internal void SetPackageAlias(string? packageAlias) => PackageAlias = string.IsNullOrWhiteSpace(packageAlias) ? null : packageAlias;
    internal void SetModulePath(List<string> path) => ModulePath = path;
    internal void SetIsTypePath(bool value) => IsTypePath = value;
    internal void SetTypeArgs(List<TypeNode> args) => TypeArgs = args;
}
