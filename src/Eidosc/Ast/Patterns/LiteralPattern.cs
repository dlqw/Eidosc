using System.Numerics;
using System.Xml;
using Eidosc.Ast.Expressions;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 字面量模式
/// </summary>
/// <example>
/// 0
/// "hello"
/// true
/// -1
/// 0xFF
/// 255u8
/// </example>
public record LiteralPattern : Pattern
{
    /// <summary>
    /// 字面量值。整数统一保存为 long（负数直接入值）；超出 long 的无符号幅度保存为 ulong。
    /// </summary>
    public object? Value { get; private set; }

    /// <summary>
    /// 字面量类型
    /// </summary>
    public LiteralType Type { get; private set; }

    /// <summary>
    /// 字面量类型后缀（i8..u64/f32/f64 或 iN/uN），None=无后缀。
    /// </summary>
    public LiteralTypeSuffix TypeSuffix { get; private set; }

    /// <summary>
    /// 任意位宽整数后缀的位宽（i24/u512 等）；固定宽度后缀为 null。
    /// </summary>
    public int? IntegerSuffixWidth { get; private set; }

    /// <summary>
    /// 原始文本（含负号/进制/分隔符/后缀），供诊断与 formatter 复现。
    /// </summary>
    public string RawText { get; private set; } = "";

    /// <summary>
    /// 解析期错误信息（整数越界/未知转义/非法字符等）；非空时由类型层转诊断 E4016。
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// 设置位置
    /// </summary>
    internal void SetSpan(Utils.SourceSpan span) => Span = span;

    /// <summary>
    /// 设置字面量值
    /// </summary>
    internal void SetLiteral(string text)
    {
        RawText = text;
        ParseLiteralValue(text);
    }

    /// <summary>
    /// 类型层上下文适配时回写值、种类与后缀（后缀即类型）。
    /// </summary>
    internal void SetValueAndKind(
        object? value,
        LiteralType type,
        LiteralTypeSuffix suffix = LiteralTypeSuffix.None,
        int? integerSuffixWidth = null)
    {
        Value = value;
        Type = type;
        TypeSuffix = suffix;
        IntegerSuffixWidth = integerSuffixWidth;
        ErrorMessage = null;
    }

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
                    ParseLiteralValue(text);
                }
            }
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.LiteralPattern);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Type, Type.ToString());
        element.SetAttribute(WellKnownStrings.XmlAttributes.RawText, RawText);
        if (TypeSuffix != LiteralTypeSuffix.None)
        {
            element.SetAttribute("suffix", TypeSuffix.ToString());
        }

        if (Value != null)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.Value, Value.ToString() ?? "");
        }
        return element;
    }

    // ──────────────────────────────────────────────────────────────
    //  单一解释器：与 LiteralExpr 共用同一套字面量解析（后缀 RFC）
    // ──────────────────────────────────────────────────────────────

    private void ParseLiteralValue(string text)
    {
        Value = null;
        Type = LiteralType.Integer;
        TypeSuffix = LiteralTypeSuffix.None;
        IntegerSuffixWidth = null;
        ErrorMessage = null;

        var isNegative = text.Length > 1 && text[0] == '-';
        var body = isNegative ? text.Substring(1) : text;

        var expr = new LiteralExpr();
        expr.SetLiteral(body);
        if (expr.ErrorMessage != null)
        {
            ErrorMessage = expr.ErrorMessage;
        }

        if (isNegative)
        {
            ParseNegativeLiteral(expr, body);
            return;
        }

        CopyFromExpression(expr);
    }

    private void CopyFromExpression(LiteralExpr expr)
    {
        TypeSuffix = expr.TypeSuffix;
        IntegerSuffixWidth = expr.IntegerSuffixWidth;
        Value = expr.Value is int intValue ? (long)intValue : expr.Value;

        Type = expr.Kind switch
        {
            LiteralKind.Integer => LiteralType.Integer,
            LiteralKind.Float => LiteralType.Float,
            LiteralKind.String => LiteralType.String,
            LiteralKind.Char => LiteralType.Char,
            LiteralKind.Boolean => LiteralType.Boolean,
            LiteralKind.ByteString => LiteralType.String,
            _ => LiteralType.String
        };

        if (expr.Kind == LiteralKind.ByteString)
        {
            ErrorMessage ??= "byte string literals are not supported as match patterns";
        }
    }

    private void ParseNegativeLiteral(LiteralExpr expr, string positiveBody)
    {
        TypeSuffix = expr.TypeSuffix;
        IntegerSuffixWidth = expr.IntegerSuffixWidth;

        switch (expr.Kind)
        {
            case LiteralKind.Integer:
            {
                Type = LiteralType.Integer;
                if (expr.TryGetBigIntegerMagnitude(out var magnitude))
                {
                    Value = NegateIntegerMagnitude(expr.TypeSuffix, magnitude);
                }
                else if (Value == null)
                {
                    ErrorMessage ??= $"invalid integer literal '-{positiveBody}'";
                }

                return;
            }

            case LiteralKind.Float:
            {
                Type = LiteralType.Float;
                Value = expr.Value switch
                {
                    float f => -f,
                    double d => -d,
                    _ => null
                };
                if (Value == null)
                {
                    ErrorMessage ??= $"invalid float literal '-{positiveBody}'";
                }

                return;
            }

            default:
                ErrorMessage ??= $"unary minus requires a numeric literal, found '{RawText}'";
                return;
        }
    }

    private object? NegateIntegerMagnitude(LiteralTypeSuffix suffix, BigInteger magnitude)
    {
        BigInteger longMax = long.MaxValue;
        BigInteger ulongMax = ulong.MaxValue;
        int? width = IntegerSuffixWidth;

        if (suffix == LiteralTypeSuffix.None)
        {
            if (magnitude == ulongMax + 1)
            {
                return long.MinValue;
            }

            if (magnitude <= longMax)
            {
                return -(long)magnitude;
            }

            ErrorMessage ??= $"integer literal '{RawText}' is out of range for type 'Int'";
            return long.MinValue;
        }

        if (LiteralSuffixTable.IsSigned(suffix))
        {
            if (LiteralSuffixTable.TryGetBigIntegerSignedMinMagnitude(suffix, width ?? 0, out var minMagnitude) &&
                magnitude <= minMagnitude)
            {
                // 正数幅度超 Max 但 ≤ |Min| 时，负号路径合法（-128i8 / long.MinValue）。
                ErrorMessage = null;
                if (suffix == LiteralTypeSuffix.IntArbitrary)
                {
                    return -magnitude;
                }

                if (magnitude > longMax)
                {
                    return suffix == LiteralTypeSuffix.Int64 && magnitude == ulongMax + 1
                        ? long.MinValue
                        : unchecked((long)magnitude);
                }

                return -(long)magnitude;
            }

            ErrorMessage ??=
                $"integer literal '{RawText}' is out of range for type '{LiteralExpr.SuffixDisplay(suffix, width)}'";
            return magnitude <= longMax ? -(long)magnitude : long.MinValue;
        }

        // 无符号后缀允许负号（与表达式侧一致：负号在类型层按无符号位型解释）。
        if (magnitude <= longMax)
        {
            return -(long)magnitude;
        }

        if (magnitude <= ulongMax)
        {
            return unchecked((long)magnitude);
        }

        ErrorMessage ??=
            $"integer literal '{RawText}' is out of range for type '{LiteralExpr.SuffixDisplay(suffix, width)}'";
        return -magnitude;
    }
}

/// <summary>
/// 字面量类型
/// </summary>
public enum LiteralType
{
    Integer,
    Float,
    String,
    Char,
    Boolean
}
