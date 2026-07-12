using System.Xml;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// 模块定义
/// </summary>
/// <example>
/// module std/collection { ... }
/// module std/io
/// </example>
public record ModuleDecl : Declaration
{
    /// <summary>
    /// 模块所属的显式 package alias。为空表示当前 package。
    /// </summary>
    public string? PackageAlias { get; private set; }

    /// <summary>
    /// Concrete package instance key, usually derived from the resolved package/source root.
    /// </summary>
    public string? PackageInstanceKey { get; private set; }

    /// <summary>
    /// 模块路径 (如 ["std", "collection"])
    /// </summary>
    public List<string> Path { get; private set; } = [];

    /// <summary>
    /// 模块内的声明列表
    /// </summary>
    public List<Declaration> Declarations { get; private set; } = [];

    /// <summary>
    /// 是否是声明式模块（无 body）
    /// </summary>
    public bool IsDeclarationOnly { get; private set; } = true;

    /// <summary>
    /// 当前模块是否启用显式 export 模式。
    /// </summary>
    public bool UsesExplicitExports { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            var modulePath = ExtractModulePath(ntNode);
            if (modulePath.Count > 0)
            {
                Path = modulePath;
            }

            foreach (var child in ntNode.Children)
            {
                if (child is NonTerminalCstNode { AstNode: Declaration decl })
                {
                    Declarations.Add(decl);
                }
                else if (child is NonTerminalCstNode childNt)
                {
                    // 递归查找嵌套的声明
                    CollectDeclarations(childNt);
                }
            }

            IsDeclarationOnly = Declarations.Count == 0;
            UsesExplicitExports = Declarations.Any(static declaration => declaration.IsExported);
        }
    }

    private static List<string> ExtractModulePath(NonTerminalCstNode node)
    {
        var hasModuleKeyword = false;
        var path = new List<string>();

        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal)
            {
                var tokenText = GetTokenText(terminal);
                if (!hasModuleKeyword)
                {
                    if (string.Equals(tokenText, WellKnownStrings.Keywords.Module, StringComparison.Ordinal))
                    {
                        hasModuleKeyword = true;
                    }
                    continue;
                }

                if (string.Equals(tokenText, "{", StringComparison.Ordinal) ||
                    string.Equals(tokenText, WellKnownStrings.Punctuation.Semicolon, StringComparison.Ordinal))
                {
                    break;
                }

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

                continue;
            }

            if (!hasModuleKeyword || child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            if (string.Equals(childNt.NonTerminal.DebugName, "moduleBody", StringComparison.Ordinal))
            {
                break;
            }

            if (string.Equals(childNt.NonTerminal.DebugName, WellKnownStrings.XmlAttributes.ModulePath, StringComparison.Ordinal))
            {
                CollectPathSegments(childNt, path);
                break;
            }
        }

        if (!hasModuleKeyword)
        {
            return [];
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

    private void CollectDeclarations(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode { AstNode: Declaration decl })
            {
                Declarations.Add(decl);
            }
            else if (child is NonTerminalCstNode childNt)
            {
                CollectDeclarations(childNt);
            }
        }
    }

    internal void SetPath(List<string> path) => Path = path;
    internal void SetPackageAlias(string? packageAlias) => PackageAlias = string.IsNullOrWhiteSpace(packageAlias) ? null : packageAlias;
    internal void SetPackageInstanceKey(string? packageInstanceKey) => PackageInstanceKey = string.IsNullOrWhiteSpace(packageInstanceKey) ? null : packageInstanceKey;
    internal void SetDeclarations(List<Declaration> decls)
    {
        Declarations = decls;
        IsDeclarationOnly = false;
        UsesExplicitExports = Declarations.Any(static declaration => declaration.IsExported);
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.ModuleDecl);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Path, string.Join(WellKnownStrings.Operators.Divide, Path));
        element.SetAttribute(WellKnownStrings.XmlAttributes.IsDeclarationOnly, IsDeclarationOnly.ToString());

        if (Declarations.Count > 0)
        {
            var declsElement = doc.CreateElement(WellKnownStrings.XmlElements.Declarations);
            foreach (var decl in Declarations)
            {
                declsElement.AppendChild(decl.ToXmlElement(doc));
            }
            element.AppendChild(declsElement);
        }

        return element;
    }
}
