using System.Xml;
using Eidosc.Utils;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 构造器模式
/// </summary>
/// <example>
/// Some(x)
/// Cons{head: h, tail: t}
/// Person{name: n, age: _}
/// </example>
public record CtorPattern : Pattern
{
    /// <summary>
    /// 显式 package 别名。
    /// </summary>
    public string? PackageAlias { get; private set; }

    /// <summary>
    /// 构造器名称
    /// </summary>
    public string ConstructorName { get; private set; } = "";

    /// <summary>
    /// 模块路径（可选）
    /// </summary>
    public List<string> ModulePath { get; private set; } = [];

    /// <summary>
    /// 位置参数模式
    /// </summary>
    public List<Pattern> PositionalPatterns { get; private set; } = [];

    /// <summary>
    /// 命名字段模式
    /// </summary>
    public List<FieldPattern> NamedPatterns { get; private set; } = [];

    /// <summary>
    /// Whether a record-style constructor pattern contains a trailing rest marker,
    /// for example <c>GameState { dir: d, .. }</c>.
    /// </summary>
    public bool HasRecordRest { get; private set; }

    internal void SetSpan(SourceSpan span) => Span = span;

    internal void SetConstructorName(string name) => ConstructorName = name;

    internal void SetPackageAlias(string? packageAlias)
    {
        PackageAlias = string.IsNullOrWhiteSpace(packageAlias) ? null : packageAlias;
    }

    internal void SetModulePath(IEnumerable<string> modulePath)
    {
        ModulePath = modulePath.ToList();
    }

    internal void AddPositionalPattern(Pattern pattern)
    {
        PositionalPatterns.Add(NormalizePatternNode(pattern));
    }

    internal void AddNamedPattern(FieldPattern fieldPattern)
    {
        NamedPatterns.Add(fieldPattern);
    }

    internal void SetRecordRest(bool value) => HasRecordRest = value;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ConstructorName = "";
        ModulePath.Clear();
        PositionalPatterns.Clear();
        NamedPatterns.Clear();
        HasRecordRest = false;

        if (node is NonTerminalCstNode ntNode)
        {
            ExtractCtorPatternData(ntNode);
        }
    }

    private void ExtractCtorPatternData(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (TryAssignConstructorName(term))
                {
                    continue;
                }

                if (TryCreatePositionalPatternFromTerminal(term, out var positionalPattern))
                {
                    PositionalPatterns.Add(positionalPattern);
                }
            }
            else if (child is NonTerminalCstNode { AstNode: FieldPattern fieldPattern })
            {
                NamedPatterns.Add(fieldPattern);
            }
            else if (child is NonTerminalCstNode { AstNode: Pattern pattern })
            {
                PositionalPatterns.Add(NormalizePatternNode(pattern));
            }
            else if (child is NonTerminalCstNode childNt)
            {
                ExtractCtorPatternData(childNt);
            }
        }
    }

    private bool TryAssignConstructorName(TerminalCstNode term)
    {
        if (term.Terminal?.ToString() != WellKnownStrings.Terminals.Identifier || !string.IsNullOrEmpty(ConstructorName))
        {
            return false;
        }

        ConstructorName = GetTokenText(term);
        return true;
    }

    private static bool TryCreatePositionalPatternFromTerminal(TerminalCstNode term, out Pattern pattern)
    {
        pattern = null!;
        var text = GetTokenText(term);
        if (string.IsNullOrWhiteSpace(text) || IsPunctuation(text))
        {
            return false;
        }

        if (IsIdentifierTerminal(term))
        {
            pattern = CreateVarPatternFromTerminal(term);
            return true;
        }

        if (IsLiteralTerminal(term))
        {
            pattern = CreateLiteralPatternFromTerminal(term);
            return true;
        }

        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.CtorPattern);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, ConstructorName);

        if (ModulePath.Count > 0)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.ModulePath, string.Join(WellKnownStrings.Separators.Path, ModulePath));
        }

        if (!string.IsNullOrWhiteSpace(PackageAlias))
        {
            element.SetAttribute("packageAlias", PackageAlias);
        }

        if (PositionalPatterns.Count > 0)
        {
            var posElement = doc.CreateElement(WellKnownStrings.XmlElements.PositionalPatterns);
            foreach (var pattern in PositionalPatterns)
            {
                posElement.AppendChild(pattern.ToXmlElement(doc));
            }
            element.AppendChild(posElement);
        }

        if (NamedPatterns.Count > 0)
        {
            var namedElement = doc.CreateElement(WellKnownStrings.XmlElements.NamedPatterns);
            foreach (var field in NamedPatterns)
            {
                namedElement.AppendChild(field.ToXmlElement(doc));
            }
            element.AppendChild(namedElement);
        }

        if (HasRecordRest)
        {
            element.SetAttribute("recordRest", "true");
        }

        return element;
    }
}
