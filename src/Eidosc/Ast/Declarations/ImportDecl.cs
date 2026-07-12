using Eidosc.Symbols;
using System.Xml;
using Eidosc.Semantic;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// 导入类型
/// </summary>
public enum ImportKind
{
    /// <summary>
    /// 导入整个模块 (import A::B)
    /// </summary>
    Module,

    /// <summary>
    /// 选择性导入 (import A::B::{X, Y})
    /// </summary>
    Selective,

    /// <summary>
    /// 通配符导入 (import A::B::*)
    /// </summary>
    Wildcard
}

/// <summary>
/// 选择性导入项
/// </summary>
public sealed record SelectiveImportNode
{
    /// <summary>
    /// 原始符号名
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 别名 (可选)
    /// </summary>
    public string? Alias { get; init; }
}

/// <summary>
/// 导入声明
/// </summary>
/// <example>
/// import std/io
/// import std/collection::{List, Map}
/// C :: import std/collection;
/// import std/collection::*
/// </example>
public record ImportDecl : Declaration
{
    /// <summary>
    /// 显式 package 别名，例如 import crypto::hash/sha256 中的 crypto。
    /// </summary>
    public string? PackageAlias { get; private set; }

    /// <summary>
    /// 导入的模块路径
    /// </summary>
    public List<string> ModulePath { get; private set; } = [];

    public List<string> ToQualifiedModulePath()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(PackageAlias))
        {
            parts.Add(PackageAlias);
        }

        parts.AddRange(ModulePath);
        return parts;
    }

    /// <summary>
    /// 导入类型
    /// </summary>
    public ImportKind Kind { get; private set; } = ImportKind.Module;

    /// <summary>
    /// 选择性导入的符号 (选择性导入时使用)
    /// </summary>
    public List<SelectiveImportNode> SelectiveImports { get; private set; } = [];

    /// <summary>
    /// 别名 (别名导入时使用)
    /// </summary>
    public string? Alias { get; private set; }

    /// <summary>
    /// 解析后的模块符号 ID
    /// </summary>
    public SymbolId ResolvedModule { get; set; } = SymbolId.None;

    /// <summary>
    /// 解析后导入的符号
    /// </summary>
    public List<ImportedSymbol> ResolvedSymbols { get; set; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ExtractExportModifier(node);

        if (node is NonTerminalCstNode ntNode)
        {
            ModulePath = ExtractPath(ntNode);

            Kind = ImportKind.Module;
            SelectiveImports = [];
            Alias = null;

            // 优先检测选择性导入，再检测通配符导入，最后解析模块别名。
            var selectiveImportsNode = FindFirstDescendantByName(ntNode, "selectiveImports")
                                       ?? FindFirstDescendantByName(ntNode, "importList");
            if (selectiveImportsNode != null)
            {
                Kind = ImportKind.Selective;
                ParseSelectiveImports(selectiveImportsNode);
            }
            else
            {
                var wildcardNode = FindFirstDescendantByName(ntNode, "wildcardImport")
                                   ?? FindFirstDescendantByName(ntNode, "wildcard");
                if (wildcardNode != null || ContainsToken(ntNode, WellKnownStrings.Operators.Multiply))
                {
                    Kind = ImportKind.Wildcard;
                }
            }

            var importAliasNode = FindTopLevelImportAliasNode(ntNode);
            if (importAliasNode != null)
            {
                Alias = ExtractAlias(importAliasNode);
            }
        }
    }

    /// <summary>
    /// 解析选择性导入列表
    /// </summary>
    private void ParseSelectiveImports(NonTerminalCstNode node)
    {
        var importItemNodes = new List<NonTerminalCstNode>();
        CollectDescendantsByName(node, "importItem", importItemNodes);

        if (importItemNodes.Count > 0)
        {
            foreach (var importItemNode in importItemNodes)
            {
                var name = ExtractIdentifier(importItemNode);
                if (!string.IsNullOrEmpty(name))
                {
                    var alias = ExtractAliasFromSelectiveImport(importItemNode);
                    SelectiveImports.Add(new SelectiveImportNode
                    {
                        Name = name,
                        Alias = alias
                    });
                }
            }
            return;
        }

        // 兼容旧 CST 结构
        foreach (var child in node.Children)
        {
            if (child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            var name = ExtractIdentifier(childNt);
            if (!string.IsNullOrEmpty(name))
            {
                var alias = ExtractAliasFromSelectiveImport(childNt);
                SelectiveImports.Add(new SelectiveImportNode
                {
                    Name = name,
                    Alias = alias
                });
            }
        }
    }

    /// <summary>
    /// 从节点提取标识符
    /// </summary>
    private static string? ExtractIdentifier(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal &&
                terminal.Token is ContentToken contentToken)
            {
                return contentToken.ToString();
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var id = ExtractIdentifier(childNt);
                if (id != null) return id;
            }
        }
        return null;
    }

    /// <summary>
    /// 从选择性导入项提取别名
    /// </summary>
    private static string? ExtractAliasFromSelectiveImport(NonTerminalCstNode node)
    {
        return ExtractAlias(node);
    }

    /// <summary>
    /// 提取别名
    /// </summary>
    private static string? ExtractAlias(NonTerminalCstNode node)
    {
        bool foundAs = false;
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal)
            {
                var tokenStr = terminal.Token.ToString();
                if (tokenStr == WellKnownStrings.AdditionalKeywords.As)
                {
                    foundAs = true;
                }
                else if (foundAs && terminal.Token is ContentToken contentToken)
                {
                    return contentToken.ToString();
                }
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var alias = ExtractAlias(childNt);
                if (alias != null) return alias;
            }
        }
        return null;
    }

    /// <summary>
    /// 检查节点是否包含特定 token
    /// </summary>
    private static bool ContainsToken(NonTerminalCstNode node, string tokenValue)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal &&
                terminal.Token is ContentToken contentToken &&
                contentToken.ToString() == tokenValue)
            {
                return true;
            }
            else if (child is NonTerminalCstNode childNt)
            {
                if (ContainsToken(childNt, tokenValue)) return true;
            }
        }
        return false;
    }

    private static NonTerminalCstNode? FindTopLevelImportAliasNode(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            if (string.Equals(childNt.NonTerminal.DebugName, "importAlias", StringComparison.Ordinal))
            {
                return childNt;
            }

            if (!string.Equals(childNt.NonTerminal.DebugName, "importTail", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var tailChild in childNt.Children)
            {
                if (tailChild is NonTerminalCstNode tailNode &&
                    string.Equals(tailNode.NonTerminal.DebugName, "importAlias", StringComparison.Ordinal))
                {
                    return tailNode;
                }
            }
        }

        return null;
    }

    private static NonTerminalCstNode? FindFirstDescendantByName(NonTerminalCstNode node, string debugName)
    {
        if (string.Equals(node.NonTerminal.DebugName, debugName, StringComparison.Ordinal))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            var found = FindFirstDescendantByName(childNt, debugName);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static void CollectDescendantsByName(
        NonTerminalCstNode node,
        string debugName,
        List<NonTerminalCstNode> collector)
    {
        if (string.Equals(node.NonTerminal.DebugName, debugName, StringComparison.Ordinal))
        {
            collector.Add(node);
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                CollectDescendantsByName(childNt, debugName, collector);
            }
        }
    }

    internal void SetModulePath(List<string> path) => ModulePath = path;
    internal void SetPackageAlias(string? packageAlias) => PackageAlias = string.IsNullOrWhiteSpace(packageAlias) ? null : packageAlias;
    internal void SetImportKind(ImportKind kind) => Kind = kind;
    internal void SetAlias(string? alias) => Alias = alias;
    internal void AddSelectiveImport(SelectiveImportNode node) => SelectiveImports.Add(node);

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.ImportDecl);
        element.SetAttribute(WellKnownStrings.XmlAttributes.ModulePath, string.Join(WellKnownStrings.Operators.Divide, ModulePath));
        element.SetAttribute(WellKnownStrings.XmlAttributes.Kind, Kind.ToString());

        if (!string.IsNullOrWhiteSpace(PackageAlias))
        {
            element.SetAttribute("packageAlias", PackageAlias);
        }

        if (Alias != null)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.Alias, Alias);
        }

        foreach (var selective in SelectiveImports)
        {
            var child = doc.CreateElement(WellKnownStrings.XmlElements.SelectiveImport);
            child.SetAttribute(WellKnownStrings.XmlAttributes.Name, selective.Name);
            if (selective.Alias != null)
            {
                child.SetAttribute(WellKnownStrings.XmlAttributes.Alias, selective.Alias);
            }
            element.AppendChild(child);
        }

        return element;
    }

    private static List<string> ExtractPath(NonTerminalCstNode node)
    {
        var importKeywordSeen = false;
        NonTerminalCstNode? modulePathNode = null;

        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal)
            {
                var text = terminal.Token.ToString();
                if (string.Equals(text, WellKnownStrings.Keywords.Import, StringComparison.Ordinal))
                {
                    importKeywordSeen = true;
                }
                continue;
            }

            if (!importKeywordSeen || child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            if (string.Equals(childNt.NonTerminal.DebugName, WellKnownStrings.XmlAttributes.ModulePath, StringComparison.Ordinal))
            {
                modulePathNode = childNt;
                break;
            }
        }

        if (modulePathNode != null)
        {
            var modulePath = new List<string>();
            CollectPathSegments(modulePathNode, modulePath);
            return modulePath;
        }

        var path = new List<string>();
        importKeywordSeen = false;

        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal)
            {
                var text = terminal.Token.ToString();
                if (!importKeywordSeen)
                {
                    if (string.Equals(text, WellKnownStrings.Keywords.Import, StringComparison.Ordinal))
                    {
                        importKeywordSeen = true;
                    }
                    continue;
                }

                if (terminal.Token is ContentToken contentToken)
                {
                    var segment = contentToken.ToString();
                    if (!string.IsNullOrWhiteSpace(segment) &&
                        !string.Equals(segment, WellKnownStrings.Operators.Divide, StringComparison.Ordinal) &&
                        !string.Equals(segment, WellKnownStrings.Separators.Path, StringComparison.Ordinal))
                    {
                        path.Add(segment);
                    }
                }
            }
        }

        return path;
    }

    private static void CollectPathSegments(ConcreteSyntaxNode node, List<string> path)
    {
        if (node is TerminalCstNode terminal)
        {
            if (terminal.Token is ContentToken contentToken)
            {
                var text = contentToken.ToString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    !string.Equals(text, WellKnownStrings.Operators.Divide, StringComparison.Ordinal) &&
                    !string.Equals(text, WellKnownStrings.Separators.Path, StringComparison.Ordinal))
                {
                    path.Add(text);
                }
            }
            return;
        }

        foreach (var child in node.Children)
        {
            CollectPathSegments(child, path);
        }
    }
}
