using System.Xml;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 字段模式
/// </summary>
/// <example>
/// head: h
/// tail: _
/// name      -- 简写形式，等价于 name: name
/// </example>
public record FieldPattern : EidosAstNode
{
    /// <summary>
    /// 字段名称
    /// </summary>
    public string FieldName { get; private set; } = "";

    /// <summary>
    /// 模式（可选，如果省略则使用字段名作为变量名）
    /// </summary>
    public Pattern? Pattern { get; private set; }

    /// <summary>
    /// 是否是简写形式 (name 等价于 name: name)
    /// </summary>
    public bool IsShorthand { get; private set; }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;

    internal void SetFieldName(string fieldName)
    {
        FieldName = fieldName;

        if (IsShorthand && Pattern is VarPattern varPattern)
        {
            varPattern.SetName(fieldName);
            varPattern.SetSpan(Span);
        }
    }

    internal void SetPattern(Pattern? pattern)
    {
        if (pattern == null)
        {
            Pattern = CreateShorthandBindingPattern();
            IsShorthand = true;
            return;
        }

        Pattern = Pattern.NormalizePatternNode(pattern);
        IsShorthand = false;
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            var foundFieldName = false;

            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (!IsPunctuation(text) && !foundFieldName)
                    {
                        FieldName = text;
                        foundFieldName = true;
                    }
                }
                else if (child is NonTerminalCstNode { AstNode: Pattern pattern })
                {
                    Pattern = Pattern.NormalizePatternNode(pattern);
                }
            }

            if (Pattern == null && foundFieldName)
            {
                Pattern = CreateShorthandBindingPattern();
                IsShorthand = true;
            }
            else
            {
                IsShorthand = false;
            }
        }
    }

    private VarPattern? CreateShorthandBindingPattern()
    {
        if (string.IsNullOrWhiteSpace(FieldName))
        {
            return null;
        }

        var binding = new VarPattern();
        binding.SetSpan(Span);
        binding.SetName(FieldName);
        binding.SetBindingMode(PatternBindingMode.ByValue);
        return binding;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.FieldPattern);
        element.SetAttribute(WellKnownStrings.XmlAttributes.FieldName, FieldName);
        element.SetAttribute(WellKnownStrings.XmlAttributes.IsShorthand, IsShorthand.ToString());

        if (Pattern != null)
        {
            var patternElement = doc.CreateElement(WellKnownStrings.XmlElements.Pattern);
            patternElement.AppendChild(Pattern.ToXmlElement(doc));
            element.AppendChild(patternElement);
        }

        return element;
    }
}
