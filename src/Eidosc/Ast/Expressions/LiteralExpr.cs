using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 字面量表达式。
/// </summary>
/// <example>
/// 42, 7u8, 0xFFi16, 1_000_000, 0o777, 0b1010
/// 3.14, 1.5f32, 1e10
/// "hello", r"raw \n", 'c', '中', '😀', b'a'
/// true, false, ()
/// </example>
public record LiteralExpr : Expression
{
    /// <summary>
    /// 字面量值。正整数字面量保存为幅度（不超 long 用 <see cref="long"/>，否则 <see cref="ulong"/>，
    /// 超过 64 位用 <see cref="BigInteger"/>）；带符号 token 路径（如 `-1`、`-128i8`）可直接保存负值；
    /// 浮点为 <see cref="float"/>/<see cref="double"/>；字符串 string；字符 char；
    /// 字节字符 byte（TypeSuffix=UInt8）；布尔 bool。
    /// </summary>
    public object? Value { get; private set; }

    /// <summary>
    /// 字面量种类。
    /// </summary>
    public LiteralKind Kind { get; private set; }

    /// <summary>
    /// 字面量类型后缀（i8..u64/f32/f64 或 iN/uN），None=无后缀。
    /// </summary>
    public LiteralTypeSuffix TypeSuffix { get; private set; }

    /// <summary>
    /// 任意位宽整数后缀的位宽（i24/u512 等）；固定宽度后缀为 null。
    /// </summary>
    public int? IntegerSuffixWidth { get; private set; }

    /// <summary>
    /// 原始文本（保留进制/分隔符/后缀/原始转义），供 formatter 复现。
    /// </summary>
    public string RawText { get; private set; } = "";

    /// <summary>
    /// 解析期错误信息（整数越界/未知转义/非法字符等）；非空时由解析器转诊断。
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Indicates that this literal was synthesized only to keep parsing after an invalid expression.
    /// </summary>
    public bool IsRecoveredError { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    RawText = GetTokenText(term);
                    ParseAndSet(RawText);
                }
            }
        }
    }

    public void SetSpan(SourceSpan span) => Span = span;

    /// <summary>
    /// 文本即唯一语义来源。
    /// </summary>
    public void SetLiteral(string rawText)
    {
        RawText = rawText;
        ParseAndSet(rawText);
    }

    public void SetValueAndKind(
        object? value,
        LiteralKind kind,
        LiteralTypeSuffix suffix = LiteralTypeSuffix.None,
        int? integerSuffixWidth = null)
    {
        Value = value;
        Kind = kind;
        TypeSuffix = suffix;
        IntegerSuffixWidth = integerSuffixWidth;
        ErrorMessage = null;
    }

    /// <summary>
    /// 解析器在 `-` + 有符号字面量折叠时使用（Rust 风格：允许 -128i8 表示 Int8 最小值）。
    /// </summary>
    public void SetNegativeLiteral(
        string rawText,
        object value,
        LiteralTypeSuffix suffix,
        LiteralKind kind,
        int? integerSuffixWidth = null)
    {
        RawText = rawText;
        Value = value;
        Kind = kind;
        TypeSuffix = suffix;
        IntegerSuffixWidth = integerSuffixWidth;
        ErrorMessage = null;
        IsRecoveredError = false;
    }

    /// <summary>
    /// 取整数字面量的正数幅度（负数不可直接取，见 TryGetMagnitudeOrNegated）。
    /// </summary>
    public bool TryGetMagnitude(out ulong magnitude)
    {
        switch (Value)
        {
            case ulong u:
                magnitude = u;
                return true;
            case long l when l >= 0:
                magnitude = (ulong)l;
                return true;
            case byte b:
                magnitude = b;
                return true;
            case ushort us:
                magnitude = us;
                return true;
            case uint ui:
                magnitude = ui;
                return true;
            default:
                magnitude = 0;
                return false;
        }
    }

    /// <summary>
    /// 取整数字面量的正数幅度（BigInteger 版本，支持任意位宽字面量）。
    /// 负数仍由外层一元负号承担。
    /// </summary>
    public bool TryGetBigIntegerMagnitude(out BigInteger magnitude)
    {
        switch (Value)
        {
            case BigInteger big:
                magnitude = big;
                return true;
            case ulong u:
                magnitude = u;
                return true;
            case long l when l >= 0:
                magnitude = l;
                return true;
            case int i when i >= 0:
                magnitude = i;
                return true;
            case byte b:
                magnitude = b;
                return true;
            case ushort us:
                magnitude = us;
                return true;
            case uint ui:
                magnitude = ui;
                return true;
            default:
                magnitude = BigInteger.Zero;
                return false;
        }
    }

    public void MarkRecoveredError(string recoveryReason = AstRecoveryReasons.ParserRecoveredLiteral)
    {
        IsRecoveredError = true;
        MarkRecovered(recoveryReason);
    }

    // ──────────────────────────────────────────────────────────────
    //  单一解释器
    // ──────────────────────────────────────────────────────────────

    private void ParseAndSet(string text)
    {
        ErrorMessage = null;
        Value = null;
        Kind = LiteralKind.Integer;
        TypeSuffix = LiteralTypeSuffix.None;
        IntegerSuffixWidth = null;

        if (text == "()")
        {
            Kind = LiteralKind.Unit;
            return;
        }

        if (text == "true" || text == "false")
        {
            Kind = LiteralKind.Boolean;
            Value = text == "true";
            return;
        }

        if (text.Length >= 2)
        {
            if (text[0] == '"')
            {
                Kind = LiteralKind.String;
                Value = UnescapeQuoted(text, 1, text.Length - 1);
                return;
            }

            if (text[0] == 'r' && text.Length >= 3 && text[1] == '"')
            {
                Kind = LiteralKind.String;
                Value = ParseRawString(text);
                return;
            }

            if (text[0] == 'b' && text.Length >= 3)
            {
                if (text[1] == '"')
                {
                    // 字节字符串 → 由解析器降级为 ListExpr[UInt8]（一个值都没有的字节串为空）。
                    Kind = LiteralKind.ByteString;
                    return;
                }

                if (text[1] == '\'')
                {
                    // byte char：'bX' → UInt8
                    if (TryParseCharContent(text, 2, out int code, out string? err) && code <= 0xFF)
                    {
                        Kind = LiteralKind.Integer;
                        TypeSuffix = LiteralTypeSuffix.UInt8;
                        Value = (byte)code;
                    }
                    else
                    {
                        ErrorMessage = err ?? $"byte character '{text}' must be in range 0..255";
                    }

                    return;
                }
            }

            if (text[0] == '\'')
            {
                if (TryParseCharContent(text, 1, out int code, out string? err))
                {
                    if (code > 0xFFFF)
                    {
                        ErrorMessage =
                            $"character literal '\\u{{{code:X}}}" +
                            $"' is outside the Char range (U+0000..U+FFFF); Char currently holds one UTF-16 code unit";
                    }
                    else
                    {
                        Kind = LiteralKind.Char;
                        Value = (char)code;
                    }
                }
                else
                {
                    ErrorMessage = err;
                }

                return;
            }
        }

        ParseNumeric(text);
    }

    // ── 数值：扫描器先按进制吃数字体，再判定浮点/后缀 ──────────────

    private void ParseNumeric(string text)
    {
        int len = text.Length;
        if (len == 0)
        {
            ErrorMessage ??= "empty literal";
            return;
        }

        // 带符号 token 路径可能直接把 `-1`/`-2^511i512` 交给字面量解析；
        // 先解析正数部分，再按符号类型折叠为负值。
        if (text[0] == '-')
        {
            ParseNumeric(text[1..]);
            if (Kind == LiteralKind.Integer && TryGetBigIntegerMagnitude(out var negMagnitude))
            {
                var negSuffix = TypeSuffix;
                if (negSuffix == LiteralTypeSuffix.None)
                {
                    var int64MinMagnitude = BigInteger.One << 63;
                    if (negMagnitude <= int64MinMagnitude)
                    {
                        ErrorMessage = null;
                        Value = negMagnitude == int64MinMagnitude
                            ? long.MinValue
                            : -(long)negMagnitude;
                    }
                    else
                    {
                        ErrorMessage ??= $"integer literal '{text}' is out of range for type 'Int'";
                    }
                }
                else if (LiteralSuffixTable.IsSigned(negSuffix) &&
                    LiteralSuffixTable.TryGetBigIntegerSignedMinMagnitude(negSuffix, IntegerSuffixWidth ?? 0, out var negMin) &&
                    negMagnitude <= negMin)
                {
                    ErrorMessage = null;
                    Value = negSuffix == LiteralTypeSuffix.IntArbitrary
                        ? -negMagnitude
                        : negMagnitude == long.MaxValue + (BigInteger)1
                            ? long.MinValue
                            : -(long)negMagnitude;
                }
                else
                {
                    ErrorMessage ??= LiteralSuffixTable.IsInteger(negSuffix)
                        ? $"integer literal '{text}' is out of range for type '{SuffixDisplay(negSuffix, IntegerSuffixWidth)}'"
                        : $"invalid numeric literal '{text}'";
                }
            }
            else if (Kind == LiteralKind.Float)
            {
                if (Value is float negFloat)
                {
                    Value = -negFloat;
                }
                else if (Value is double negDouble)
                {
                    Value = -negDouble;
                }
            }

            return;
        }

        int pos = 0;
        int fromBase = 10;
        if (len >= 2 && text[0] == '0')
        {
            switch (text[1])
            {
                case 'x' or 'X':
                    fromBase = 16;
                    pos = 2;
                    break;
                case 'o' or 'O':
                    fromBase = 8;
                    pos = 2;
                    break;
                case 'b' or 'B':
                    fromBase = 2;
                    pos = 2;
                    break;
            }
        }

        // 1) 吃数字体（含分隔符 `_`；支持进制前缀后立即 `_`，如 0x_FF）
        int bodyEnd = pos;
        bool anyDigit = false;
        for (; pos < len; pos++)
        {
            char c = text[pos];
            if (IsDigitForBase(c, fromBase))
            {
                anyDigit = true;
                bodyEnd = pos + 1;
                continue;
            }

            if (c == '_')
            {
                bodyEnd = pos + 1;
                continue;
            }

            break;
        }

        if (!anyDigit)
        {
            ErrorMessage ??= $"invalid numeric literal '{text}'";
            return;
        }

        // 浮点（仅十进制）
        if (bodyEnd < len && fromBase == 10 && text[bodyEnd] is '.' or 'e' or 'E')
        {
            ParseFloatBody(text, bodyEnd);
            return;
        }

        // 2) 整数后缀
        LiteralTypeSuffix suffix = LiteralTypeSuffix.None;
        int arbitraryWidth = 0;
        if (LiteralSuffixTable.TryMatch(text.AsSpan(), bodyEnd, out suffix, out int suffixLength, out arbitraryWidth))
        {
            // 校验字符串恰以 body + suffix 结束
            if (bodyEnd + suffixLength != len)
            {
                ErrorMessage ??= $"trailing characters after integer literal '{text}'";
                return;
            }

            if (suffix is LiteralTypeSuffix.IntArbitrary or LiteralTypeSuffix.UIntArbitrary &&
                arbitraryWidth > BaseTypes.MaxIntegerWidth)
            {
                TypeSuffix = suffix;
                IntegerSuffixWidth = arbitraryWidth;
                Kind = LiteralKind.Integer;
                ErrorMessage ??=
                    $"arbitrary-width integer literal suffix '{text}' has width {arbitraryWidth}, " +
                    $"which exceeds the maximum supported width {BaseTypes.MaxIntegerWidth}";
                return;
            }
        }
        else if (bodyEnd < len)
        {
            ErrorMessage ??= $"unsupported integer literal suffix in '{text}'";
            return;
        }

        var digits = StripSeparators(text, posOfDigits(text, fromBase), bodyEnd);
        if (digits.Length == 0)
        {
            ErrorMessage ??= $"invalid integer literal '{text}'";
            return;
        }

        if (!TryParseBigIntegerDigits(digits, fromBase, out BigInteger magnitude))
        {
            ErrorMessage ??= $"integer literal '{text}' is too large";
            return;
        }

        Value = suffix == LiteralTypeSuffix.None && magnitude <= int.MaxValue
            ? (object)(int)magnitude
            : magnitude <= long.MaxValue
                ? (object)(long)magnitude
                : magnitude <= ulong.MaxValue
                    ? (object)(ulong)magnitude
                    : magnitude;
        TypeSuffix = suffix;
        IntegerSuffixWidth = arbitraryWidth > 0 ? arbitraryWidth : null;
        Kind = LiteralKind.Integer;

        // 后缀宽度即时校验（正数幅度）
        if (!ValidateMagnitude(suffix, arbitraryWidth > 0 ? arbitraryWidth : null, magnitude, negated: false, out string? rangeError))
        {
            ErrorMessage = rangeError;
        }
    }

    private static int posOfDigits(string text, int fromBase)
    {
        if (fromBase == 10)
        {
            return 0;
        }

        return 2;
    }

    private void ParseFloatBody(string text, int bodyStart)
    {
        // 扫描到后缀（f32/f64）或末尾
        int len = text.Length;
        int bodyEnd = len;

        LiteralTypeSuffix suffix = LiteralTypeSuffix.None;
        for (int i = bodyStart; i < len; i++)
        {
            if (LiteralSuffixTable.TryMatch(text.AsSpan(), i, out suffix, out int suffixLength, out _) &&
                i + suffixLength == len)
            {
                bodyEnd = i;
                break;
            }
        }

        var body = StripSeparators(text, 0, bodyEnd);
        var culture = CultureInfo.InvariantCulture;

        if (suffix == LiteralTypeSuffix.Float32 &&
            float.TryParse(body, NumberStyles.Float, culture, out float f))
        {
            Value = f;
            TypeSuffix = LiteralTypeSuffix.Float32;
            Kind = LiteralKind.Float;
            return;
        }

        if (double.TryParse(body, NumberStyles.Float, culture, out double d))
        {
            Value = d;
            TypeSuffix = suffix; // 无后缀浮点保持 None（默认 Float，可被上下文适配）
            Kind = LiteralKind.Float;
            return;
        }

        ErrorMessage ??= $"invalid float literal '{text}'";
    }

    private static bool ValidateMagnitude(
        LiteralTypeSuffix suffix,
        int? arbitraryWidth,
        BigInteger magnitude,
        bool negated,
        out string? error)
    {
        error = null;

        if (!LiteralSuffixTable.TryGetBigIntegerMagnitudeLimit(suffix, arbitraryWidth ?? 0, out BigInteger positiveMax))
        {
            return true; // 无后缀（None）不在这里校验
        }

        // 负数：有符号类型允许 |min|，无符号不允许负
        if (negated)
        {
            if (LiteralSuffixTable.IsSigned(suffix) &&
                LiteralSuffixTable.TryGetBigIntegerSignedMinMagnitude(suffix, arbitraryWidth ?? 0, out BigInteger minMag))
            {
                if (magnitude <= minMag)
                {
                    return true;
                }

                error = $"literal '{magnitude}' is out of range for the negated type of '{SuffixDisplay(suffix, arbitraryWidth)}'";
                return false;
            }

            // 无符号负数：由 NegExpr 在类型层拒绝；这里允许（符号错误在类型层报）
            return true;
        }

        if (magnitude > positiveMax)
        {
            error = $"integer literal '{magnitude}' is out of range for type '{SuffixDisplay(suffix, arbitraryWidth)}' (0..{positiveMax})";
            return false;
        }

        return true;
    }

    public static string SuffixDisplay(LiteralTypeSuffix suffix, int? arbitraryWidth = null) => suffix switch
    {
        LiteralTypeSuffix.Int8 => "Int8",
        LiteralTypeSuffix.Int16 => "Int16",
        LiteralTypeSuffix.Int32 => "Int32",
        LiteralTypeSuffix.Int64 => "Int64",
        LiteralTypeSuffix.UInt8 => "UInt8",
        LiteralTypeSuffix.UInt16 => "UInt16",
        LiteralTypeSuffix.UInt32 => "UInt32",
        LiteralTypeSuffix.UInt64 => "UInt64",
        LiteralTypeSuffix.Float32 => "Float32",
        LiteralTypeSuffix.Float64 => "Float",
        LiteralTypeSuffix.IntArbitrary => arbitraryWidth is > 0 ? $"I{arbitraryWidth}" : "I<N>",
        LiteralTypeSuffix.UIntArbitrary => arbitraryWidth is > 0 ? $"U{arbitraryWidth}" : "U<N>",
        _ => "Int"
    };

    private static bool IsDigitForBase(char c, int fromBase)
    {
        int v = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };
        return v >= 0 && v < fromBase;
    }

    private static string StripSeparators(string text, int start, int end)
    {
        ReadOnlySpan<char> span = text.AsSpan(start, end - start);
        if (!span.Contains('_'))
        {
            return text.Substring(start, end - start);
        }

        var sb = new StringBuilder(end - start);
        foreach (char c in span)
        {
            if (c != '_')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static bool TryParseBigIntegerDigits(string digits, int fromBase, out BigInteger value)
    {
        value = BigInteger.Zero;
        BigInteger bigBase = fromBase;
        foreach (char c in digits)
        {
            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1
            };

            if (digit < 0 || digit >= fromBase)
            {
                return false;
            }

            value = value * bigBase + digit;
        }

        return true;
    }

    // ── 字符串 / 字符 ──────────────────────────────────────────────

    private static string ParseRawString(string text)
    {
        var builder = new StringBuilder(text.Length - 2);
        for (int i = 2; i < text.Length - 1; i++)
        {
            if (text[i] == '"' && i + 1 < text.Length - 1 && text[i + 1] == '"')
            {
                builder.Append('"');
                i++;
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 解析 "..." 的主体（含完整转义与 \u{...}）。
    /// </summary>
    private static string UnescapeQuoted(string text, int contentStart, int contentEnd)
    {
        var builder = new StringBuilder(contentEnd - contentStart);
        int i = contentStart;
        while (i < contentEnd)
        {
            char c = text[i];
            if (c != '\\')
            {
                builder.Append(c);
                i++;
                continue;
            }

            if (i + 1 >= contentEnd)
            {
                builder.Append('\\');
                i++;
                continue;
            }

            char esc = text[i + 1];
            switch (esc)
            {
                case 'n': builder.Append('\n'); i += 2; break;
                case 'r': builder.Append('\r'); i += 2; break;
                case 't': builder.Append('\t'); i += 2; break;
                case '0': builder.Append('\0'); i += 2; break;
                case '\\': builder.Append('\\'); i += 2; break;
                case '"': builder.Append('"'); i += 2; break;
                case '\'': builder.Append('\''); i += 2; break;
                case 'a': builder.Append('\a'); i += 2; break;
                case 'b': builder.Append('\b'); i += 2; break;
                case 'v': builder.Append('\v'); i += 2; break;
                case 'f': builder.Append('\f'); i += 2; break;
                case 'u' when i + 2 < contentEnd && text[i + 2] == '{':
                {
                    int close = text.IndexOf('}', i + 3);
                    if (close >= 0 && close < contentEnd &&
                        int.TryParse(
                            text.AsSpan(i + 3, close - (i + 3)),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out int ucode) &&
                        ucode is >= 0 and <= 0x10FFFF)
                    {
                        builder.Append(char.ConvertFromUtf32(ucode));
                        i = close + 1;
                    }
                    else
                    {
                        builder.Append("<invalid \\u{}>");
                        i = close >= 0 ? close + 1 : contentEnd;
                    }

                    break;
                }
                default:
                    builder.Append(esc);
                    i += 2;
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 解析字符内容。text 以 openIndex 为内容起点（例：'a' → openIndex=1；b'a' → openIndex=2）。
    /// 内容必须是恰好一个码点（代理对算一个），或一个转义序列。
    /// </summary>
    private static bool TryParseCharContent(string text, int openIndex, out int code, out string? error)
    {
        error = null;
        code = 0;

        int end = text.Length - 1;
        if (openIndex >= end)
        {
            error = "empty character literal";
            return false;
        }

        int contentLen = end - openIndex;

        // 转义序列
        if (contentLen >= 2 && text[openIndex] == '\\')
        {
            char esc = text[openIndex + 1];
            switch (esc)
            {
                case 'n': code = '\n'; return true;
                case 'r': code = '\r'; return true;
                case 't': code = '\t'; return true;
                case '0': code = '\0'; return true;
                case '\\': code = '\\'; return true;
                case '\'': code = '\''; return true;
                case '"': code = '"'; return true;
                case 'a': code = '\a'; return true;
                case 'b': code = '\b'; return true;
                case 'v': code = '\v'; return true;
                case 'f': code = '\f'; return true;
                case 'u' when openIndex + 2 < end && text[openIndex + 2] == '{':
                {
                    int close = text.IndexOf('}', openIndex + 3);
                    if (close == end - 1 &&
                        int.TryParse(
                            text.AsSpan(openIndex + 3, close - (openIndex + 3)),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out int ucode) &&
                        ucode is >= 0 and <= 0x10FFFF)
                    {
                        code = ucode;
                        return true;
                    }

                    error = "invalid unicode escape \\u{...}";
                    return false;
                }
                default:
                    error = $"unknown escape sequence '\\{esc}' in character literal";
                    return false;
            }
        }

        // 普通内容：恰好一个码点
        int runeCount = CountRunes(text, openIndex, end);
        if (runeCount != 1)
        {
            error = $"character literal must contain exactly one character: '{text}'";
            return false;
        }

        code = char.ConvertToUtf32(text, openIndex);
        return true;
    }

    private static int CountRunes(string text, int start, int end)
    {
        int count = 0;
        int i = start;
        while (i < end)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                i += 2;
            }
            else
            {
                i++;
            }

            count++;
        }

        return count;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.LiteralExpr);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Kind, Kind.ToString());
        element.SetAttribute(WellKnownStrings.XmlAttributes.RawText, RawText);
        if (Value != null)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.Value, Value.ToString() ?? "");
        }

        return element;
    }
}

/// <summary>
/// 字面量种类。
/// </summary>
public enum LiteralKind
{
    Integer,
    Float,
    String,
    Char,
    Boolean,
    Unit,
    ByteString
}
