using System.Xml;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 构造器实例化表达式
/// </summary>
/// <example>
/// Some(42)
/// Some{value: 42}
/// Person{name: "Alice", age: 30}
/// </example>
public record CtorExpr : Expression
{
    /// <summary>
    /// 类型路径
    /// </summary>
    public TypePath? ConstructorPath { get; private set; }

    /// <summary>
    /// 构造器名称（简化访问）
    /// </summary>
    public string ConstructorName { get; private set; } = "";

    /// <summary>
    /// 位置参数
    /// </summary>
    public List<EidosAstNode> PositionalArgs { get; private set; } = [];

    /// <summary>
    /// 命名字段
    /// </summary>
    public List<FieldInit> NamedArgs { get; private set; } = [];

    /// <summary>
    /// record update 的基准值，例如 <c>Point { ..p, x: 1 }</c> 中的 <c>p</c>。
    /// </summary>
    public EidosAstNode? UpdateBase { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    // 类型标识符作为构造器名称
                    if (IsTypeIdentifier(term) && string.IsNullOrEmpty(ConstructorName))
                    {
                        ConstructorName = text;
                        ConstructorPath = CreateTypePath(text, term.Span);
                    }
                }
                else if (child is NonTerminalCstNode { AstNode: TypePath typePath })
                {
                    ConstructorPath = typePath;
                    ConstructorName = typePath.TypeName ?? "";
                }
                else if (child is NonTerminalCstNode childNt)
                {
                    var childName = childNt.NonTerminal?.DebugName ?? "";

                    // 处理 typePath 节点（即使没有 AstNode）
                    if (childName == "typePath" && ConstructorPath == null)
                    {
                        ConstructorPath = ExtractTypePath(childNt);
                        if (ConstructorPath != null)
                            ConstructorName = ConstructorPath.TypeName ?? "";
                    }
                    else if (childName == "ctorUpdateBase")
                    {
                        var updateBase = ExtractUpdateBaseExpression(childNt);
                        if (updateBase != null)
                        {
                            UpdateBase = updateBase;
                        }
                    }
                    else if (childNt.AstNode is FieldInit fieldInit)
                    {
                        NamedArgs.Add(fieldInit);
                    }
                    else if (childNt.AstNode is EidosAstNode expr && expr is not TypePath and not FieldInit)
                    {
                        PositionalArgs.Add(expr);
                    }
                    else
                    {
                        ExtractArgsFromNode(childNt);
                    }
                }
            }
        }
    }

    private void ExtractArgsFromNode(NonTerminalCstNode node)
    {
        if (string.Equals(node.NonTerminal?.DebugName, "ctorUpdateBase", StringComparison.Ordinal))
        {
            var updateBase = ExtractUpdateBaseExpression(node);
            if (updateBase != null)
            {
                UpdateBase = updateBase;
            }

            return;
        }

        if (node.AstNode is FieldInit fieldInit)
        {
            NamedArgs.Add(fieldInit);
            return;
        }

        if (node.AstNode is EidosAstNode expr && expr is not TypePath and not FieldInit)
        {
            PositionalArgs.Add(expr);
            return;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode nestedNode)
            {
                ExtractArgsFromNode(nestedNode);
                continue;
            }

            if (child is not TerminalCstNode term)
            {
                continue;
            }

            if (TryCreatePositionalArgFromTerminal(term, out var terminalExpr))
            {
                PositionalArgs.Add(terminalExpr);
            }
        }
    }

    private static EidosAstNode? ExtractUpdateBaseExpression(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term &&
                TryCreatePositionalArgFromTerminal(term, out var terminalExpr))
            {
                return terminalExpr;
            }

            if (child is NonTerminalCstNode { AstNode: EidosAstNode expr } &&
                expr is not TypePath and not FieldInit)
            {
                return expr;
            }

            if (child is NonTerminalCstNode nested)
            {
                var nestedExpr = ExtractUpdateBaseExpression(nested);
                if (nestedExpr != null)
                {
                    return nestedExpr;
                }
            }
        }

        return null;
    }

    private static bool TryCreatePositionalArgFromTerminal(TerminalCstNode term, out EidosAstNode expr)
    {
        expr = null!;
        var text = GetTokenText(term);
        if (string.IsNullOrWhiteSpace(text) || IsPunctuation(text))
        {
            return false;
        }

        if (IsIdentifierTerminal(term))
        {
            var ident = new IdentifierExpr();
            ident.SetSpan(term.Span);
            ident.SetName(text);
            expr = ident;
            return true;
        }

        if (IsLiteralTerminal(term))
        {
            var literal = new LiteralExpr();
            literal.SetSpan(term.Span);
            literal.SetLiteral(text);
            expr = literal;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 检查是否是类型标识符
    /// </summary>
    private static bool IsTypeIdentifier(TerminalCstNode term)
    {
        if (term.Terminal == null) return false;
        var terminalName = term.Terminal.ToString();
        return terminalName == WellKnownStrings.Terminals.TypeIdentifier;
    }

    private static bool IsIdentifierTerminal(TerminalCstNode term)
    {
        return term.Terminal?.ToString() == WellKnownStrings.Terminals.Identifier;
    }

    private static bool IsLiteralTerminal(TerminalCstNode term)
    {
        return term.Terminal?.ToString() is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean;
    }

    /// <summary>
    /// 创建 TypePath
    /// </summary>
    private static TypePath CreateTypePath(string name, Utils.SourceSpan span)
    {
        var path = new TypePath();
        path.SetTypeName(name);
        path.SetSpan(span);
        return path;
    }

    /// <summary>
    /// 从 CST 节点提取 TypePath
    /// </summary>
    private static TypePath? ExtractTypePath(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term && IsTypeIdentifier(term))
            {
                return CreateTypePath(GetTokenText(term), term.Span);
            }
            else if (child is NonTerminalCstNode { AstNode: TypePath typePath })
            {
                return typePath;
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var extracted = ExtractTypePath(childNt);
                if (extracted != null)
                    return extracted;
            }
        }
        return null;
    }

    /// <summary>
    /// 设置构造器名称
    /// </summary>
    public void SetConstructorName(string name)
    {
        ConstructorName = name;
        if (ConstructorPath == null)
        {
            ConstructorPath = new TypePath();
            ConstructorPath.SetTypeName(name);
        }
    }

    internal void SetConstructorPath(TypePath path)
    {
        ConstructorPath = path;
        ConstructorName = path.TypeName;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void AddPositionalArg(EidosAstNode arg) => PositionalArgs.Add(arg);
    internal void AddNamedArg(FieldInit field) => NamedArgs.Add(field);
    internal void SetUpdateBase(EidosAstNode updateBase) => UpdateBase = updateBase;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.CtorExpr);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Ctor, ConstructorName ?? "?");

        if (ConstructorPath != null)
        {
            var pathElement = doc.CreateElement(WellKnownStrings.XmlElements.ConstructorPath);
            pathElement.AppendChild(ConstructorPath.ToXmlElement(doc));
            element.AppendChild(pathElement);
        }

        if (PositionalArgs.Count > 0)
        {
            var argsElement = doc.CreateElement(WellKnownStrings.XmlElements.PositionalArgs);
            foreach (var arg in PositionalArgs)
            {
                argsElement.AppendChild(arg.ToXmlElement(doc));
            }
            element.AppendChild(argsElement);
        }

        if (UpdateBase != null)
        {
            var baseElement = doc.CreateElement("UpdateBase");
            baseElement.AppendChild(UpdateBase.ToXmlElement(doc));
            element.AppendChild(baseElement);
        }

        if (NamedArgs.Count > 0)
        {
            var argsElement = doc.CreateElement(WellKnownStrings.XmlElements.NamedArgs);
            foreach (var field in NamedArgs)
            {
                argsElement.AppendChild(field.ToXmlElement(doc));
            }
            element.AppendChild(argsElement);
        }

        return element;
    }
}

/// <summary>
/// 字段初始化
/// </summary>
public record FieldInit : EidosAstNode
{
    /// <summary>
    /// 字段名称
    /// </summary>
    public string FieldName { get; private set; } = "";

    /// <summary>
    /// 初始化表达式
    /// </summary>
    public EidosAstNode? Value { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        FieldName = "";
        Value = null;

        if (node is NonTerminalCstNode ntNode)
        {
            var foundName = false;
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (!IsPunctuation(text) && !foundName)
                    {
                        FieldName = text;
                        foundName = true;
                    }
                    else if (foundName && Value == null && TryCreateValueFromTerminal(term, out var terminalExpr))
                    {
                        Value = terminalExpr;
                    }
                }
                else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
                {
                    Value = expr;
                }
            }
        }
    }

    private static bool TryCreateValueFromTerminal(TerminalCstNode term, out EidosAstNode expr)
    {
        expr = null!;
        var text = GetTokenText(term);
        if (string.IsNullOrWhiteSpace(text) || IsPunctuation(text))
        {
            return false;
        }

        if (term.Terminal?.ToString() == WellKnownStrings.Terminals.Identifier)
        {
            var identifier = new IdentifierExpr();
            identifier.SetSpan(term.Span);
            identifier.SetName(text);
            expr = identifier;
            return true;
        }

        if (term.Terminal?.ToString() is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean)
        {
            var literal = new LiteralExpr();
            literal.SetSpan(term.Span);
            literal.SetLiteral(text);
            expr = literal;
            return true;
        }

        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.FieldInit);
        element.SetAttribute(WellKnownStrings.XmlAttributes.FieldName, FieldName);

        if (Value != null)
        {
            var valueElement = doc.CreateElement(WellKnownStrings.XmlElements.Value);
            valueElement.AppendChild(Value.ToXmlElement(doc));
            element.AppendChild(valueElement);
        }

        return element;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetFieldName(string name) => FieldName = name;
    internal void SetValue(EidosAstNode value) => Value = value;
}
