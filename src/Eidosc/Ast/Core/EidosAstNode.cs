using System.Xml;
using Eidosc.Utilities;
using Eidosc.Utils;
using Eidosc.Symbols;

namespace Eidosc.Ast;

public enum SyntaxIdentityKind
{
    Hygiene,
    Declaration,
    Type,
    Identifier
}

public sealed record SyntaxIdentity(
    SyntaxIdentityKind Kind,
    string StableIdentity,
    SymbolId SymbolId,
    TypeId TypeId,
    string Category = "");

/// <summary>
/// Eidos 语言 AST 节点的抽象基类
/// 所有 Eidos 特定的 AST 节点都应继承此类
/// </summary>
public abstract record EidosAstNode : IXmlSerializable
{
    public SourceSpan Span { get; internal set; }

    public bool IsRecovered { get; private set; }

    public string? RecoveryReason { get; private set; }

    /// <summary>
    /// 解析后的符号 ID（名称解析阶段填充）
    /// </summary>
    public SymbolId SymbolId { get; set; } = SymbolId.None;

    public SyntaxIdentity? AttachedSyntaxIdentity { get; private set; }

    public IReadOnlyList<GeneratedDeclarationOrigin> GeneratedOriginChain { get; private set; } = [];

    internal void AttachSyntaxIdentity(SyntaxIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        AttachedSyntaxIdentity = identity;
        if (identity.SymbolId.IsValid)
        {
            SymbolId = identity.SymbolId;
        }
    }

    internal void AttachGeneratedOriginChain(IEnumerable<GeneratedDeclarationOrigin> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        GeneratedOriginChain = origins.ToArray();
    }

    /// <summary>
    /// 推断出的类型（类型推断阶段填充）
    /// 实际类型为 Eidosc.Types.Type
    /// </summary>
    public object? InferredType { get; set; }

    public Eidosc.Types.EffectRow? InferredEffects { get; set; }

    /// <summary>
    /// 从 CST 节点构建 AST
    /// </summary>
    public abstract void BuildFromCst(AstContext context, ConcreteSyntaxNode node);

    public abstract XmlElement ToXmlElement(XmlDocument doc);

    public void MarkRecovered(string recoveryReason)
    {
        IsRecovered = true;
        RecoveryReason = string.IsNullOrWhiteSpace(recoveryReason)
            ? AstRecoveryReasons.ParserRecoveredLiteral
            : recoveryReason;
    }

    public override string ToString() => GetType().Name;

    /// <summary>
    /// 转换为 XML 字符串，用于可视化
    /// </summary>
    public string ToXml()
    {
        var doc = new XmlDocument();
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", "yes"));
        doc.AppendChild(ToXmlElement(doc));
        return doc.OuterXml;
    }

    /// <summary>
    /// 格式化的 XML 字符串，带缩进
    /// </summary>
    public string ToXmlFormatted()
    {
        var doc = new XmlDocument();
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", "yes"));
        doc.AppendChild(ToXmlElement(doc));

        using var sw = new StringWriter();
        using var xmlWriter = new XmlTextWriter(sw)
        {
            Formatting = Formatting.Indented,
            Indentation = 2,
            IndentChar = ' '
        };
        doc.Save(xmlWriter);
        return sw.ToString();
    }

    protected XmlElement CreateElement(XmlDocument doc, string name)
    {
        var element = doc.CreateElement(name);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Span, Span.ToString());
        if (IsRecovered)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.IsRecovered, WellKnownStrings.AdditionalKeywords.True);
            element.SetAttribute(
                WellKnownStrings.XmlAttributes.RecoveryReason,
                RecoveryReason ?? AstRecoveryReasons.ParserRecoveredLiteral);
        }
        if (GeneratedOriginChain.Count > 0)
        {
            var chainElement = doc.CreateElement("GeneratedOriginChain");
            foreach (var origin in GeneratedOriginChain)
            {
                var originElement = doc.CreateElement("Origin");
                originElement.SetAttribute("identity", origin.StableIdentity);
                originElement.SetAttribute("generator", origin.GeneratorIdentity);
                originElement.SetAttribute("target", origin.TargetIdentity);
                originElement.SetAttribute("generatorSymbolId", origin.GeneratorSymbolId.Value.ToString());
                originElement.SetAttribute("targetSymbolId", origin.TargetSymbolId.Value.ToString());
                originElement.SetAttribute("clauseOccurrenceIndex", origin.ClauseOccurrenceIndex.ToString());
                originElement.SetAttribute("clause", origin.ClauseOccurrenceIdentity);
                originElement.SetAttribute("clauseArgumentSubIndex", origin.ClauseArgumentSubIndex.ToString());
                originElement.SetAttribute("outputIndex", origin.ExpansionOutputIndex.ToString());
                originElement.SetAttribute("argumentsHash", origin.CanonicalArgumentsHash);
                originElement.SetAttribute("metaSchemaVersion", origin.MetaSchemaVersion.ToString());
                originElement.SetAttribute("clauseSpan", origin.ClauseSpan.ToString());
                originElement.SetAttribute("virtualDocument", origin.VirtualDocumentPath);
                chainElement.AppendChild(originElement);
            }
            element.AppendChild(chainElement);
        }
        return element;
    }

    #region CST 辅助方法

    /// <summary>
    /// 从 ContentToken 获取文本
    /// </summary>
    protected static string GetTokenText(TerminalCstNode node)
    {
        if (node.Token is ContentToken contentToken)
        {
            // 对于字符串/字符字面量，Value 包含实际内容
            if (contentToken.Value is string stringValue)
            {
                // 返回带引号的形式，以便 ParseLiteral 正确解析
                var terminalName = node.Terminal?.ToString() ?? "";
                return terminalName switch
                {
                    WellKnownStrings.Terminals.String => $"\"{EscapeLiteralContent(stringValue, quote: '\"')}\"",
                    WellKnownStrings.Terminals.Char => $"'{EscapeLiteralContent(stringValue, quote: '\'')}'",
                    _ => stringValue
                };
            }

            if (contentToken.Value is char charValue)
            {
                var terminalName = node.Terminal?.ToString() ?? "";
                if (terminalName == WellKnownStrings.Terminals.Char)
                {
                    return $"'{EscapeLiteralContent(charValue.ToString(), quote: '\'')}'";
                }
            }

            return contentToken.TextId.Resolve();
        }
        return node.Token?.ToString() ?? "";
    }

    private static string EscapeLiteralContent(string value, char quote)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\0':
                    builder.Append(@"\0");
                    break;
                default:
                    if (ch == quote)
                    {
                        builder.Append('\\').Append(ch);
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 提取第一个 token 的文本
    /// </summary>
    protected static string? ExtractFirstTokenText(ConcreteSyntaxNode node)
    {
        switch (node)
        {
            case TerminalCstNode term:
                return GetTokenText(term);
            case NonTerminalCstNode ntNode:
            {
                foreach (var text in ntNode.Children.Select(ExtractFirstTokenText).OfType<string>())
                {
                    return text;
                }

                break;
            }
        }

        return null;
    }

    /// <summary>
    /// 提取模块路径
    /// </summary>
    protected static List<string> ExtractPath(ConcreteSyntaxNode node)
    {
        var path = new List<string>();
        ExtractPathRecursive(node, path);
        return path;
    }

    private static void ExtractPathRecursive(ConcreteSyntaxNode node, List<string> path)
    {
        switch (node)
        {
            case TerminalCstNode term:
            {
                var text = GetTokenText(term);
                if (!IsPunctuation(text))
                {
                    path.Add(text);
                }

                break;
            }
            case NonTerminalCstNode ntNode:
            {
                foreach (var child in ntNode.Children)
                {
                    ExtractPathRecursive(child, path);
                }

                break;
            }
        }
    }

    /// <summary>
    /// 检查是否是标点符号
    /// </summary>
    protected static bool IsPunctuation(string text)
    {
        return text is WellKnownStrings.Operators.Divide
            or WellKnownStrings.Separators.Path
            or WellKnownStrings.Punctuation.Dot
            or WellKnownStrings.Punctuation.DotDot
            or WellKnownStrings.Punctuation.Colon
            or WellKnownStrings.Punctuation.OpenParen
            or WellKnownStrings.Punctuation.CloseParen
            or WellKnownStrings.Punctuation.OpenBrace
            or WellKnownStrings.Punctuation.CloseBrace
            or WellKnownStrings.Punctuation.OpenBracket
            or WellKnownStrings.Punctuation.CloseBracket
            or WellKnownStrings.Punctuation.Comma
            or WellKnownStrings.Punctuation.Semicolon
            or WellKnownStrings.Punctuation.FatArrow
            or WellKnownStrings.Punctuation.RightArrow
            or WellKnownStrings.Punctuation.LeftArrow
            or WellKnownStrings.Punctuation.Pipe;
    }

    /// <summary>
    /// 获取指定索引的子节点
    /// </summary>
    protected static ConcreteSyntaxNode? GetChild(ConcreteSyntaxNode node, int index)
    {
        if (node is not NonTerminalCstNode ntNode || index >= ntNode.Children.Count)
            return null;
        return ntNode.Children[index];
    }

    /// <summary>
    /// 获取指定索引的子节点并转换为指定类型
    /// </summary>
    protected static T? GetChildAs<T>(ConcreteSyntaxNode node, int index) where T : ConcreteSyntaxNode
    {
        return GetChild(node, index) as T;
    }

    /// <summary>
    /// 获取所有子节点
    /// </summary>
    protected static IReadOnlyList<ConcreteSyntaxNode> GetChildren(ConcreteSyntaxNode node)
    {
        if (node is NonTerminalCstNode ntNode)
            return ntNode.Children;
        return [];
    }

    #endregion
}
