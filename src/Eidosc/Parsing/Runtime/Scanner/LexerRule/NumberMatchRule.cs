using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace Eidosc;

/// <summary>
/// 数字字面量规则（结构层）：dec/hex/oct/bin、分隔符 `_`、后缀 i8..u64/f32/f64。
/// 只做结构校验与 token 化；宽度范围校验（含有符号最小值经一元负号）下沉到
/// AST/类型层（见 <see cref="Ast.Expressions.LiteralExpr"/> / TypeInferer）。
/// </summary>
[MemoryPackable]
public partial class NumberLiteralRule : LiteralRule
{
    public readonly NumberConfig Config;
    public SyntaxKind Kind;

    private readonly char[] _firstsCache;
    private static readonly SearchValues<char> DecDigits = SearchValues.Create("0123456789");
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly SearchValues<char> OctDigits = SearchValues.Create("01234567");
    private static readonly SearchValues<char> BinDigits = SearchValues.Create("01");

    [MemoryPackConstructor]
    public NumberLiteralRule(int terminalId, NumberConfig config, SyntaxKind kind = default) : base(terminalId)
    {
        Config = config;
        Kind = kind;

        var firsts = new HashSet<char> { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
        if (config.AllowLeadingSign)
        {
            firsts.Add('+');
            firsts.Add('-');
        }

        if (config.AllowLeadingDot)
        {
            firsts.Add('.');
        }

        _firstsCache = [.. firsts];
    }

    public override IList<char> GetFirsts() => _firstsCache;

    public override Token? Tokenize(LexerContext context)
    {
        var stream = context.Source;
        if (stream.Eof())
        {
            return null;
        }

        char first = stream.PreviewChar;
        bool isDigit = char.IsAsciiDigit(first);
        if (!isDigit && first != '.' && first != '+' && first != '-')
        {
            return null;
        }

        string text = stream.Text;
        int startPos = stream.PreviewPosition;
        int pos = startPos;
        int maxLen = text.Length;

        if (Config.AllowLeadingSign && (first == '+' || first == '-'))
        {
            pos++;
            if (pos >= maxLen || !char.IsAsciiDigit(text[pos]))
            {
                return null; // 符号后无数字 → 不是数字字面量
            }
        }

        int fromBase = 10;
        if (pos + 1 < maxLen && text[pos] == '0')
        {
            char p = text[pos + 1];
            if (p == 'x' || p == 'X')
            {
                if (!Config.EnableHex)
                {
                    return null;
                }

                fromBase = 16;
                pos += 2;
            }
            else if (p == 'o' || p == 'O')
            {
                fromBase = 8;
                pos += 2;
            }
            else if (p == 'b' || p == 'B')
            {
                if (!Config.EnableBinary)
                {
                    return null;
                }

                fromBase = 2;
                pos += 2;
            }
        }

        SearchValues<char> digits = fromBase switch
        {
            16 => HexDigits,
            8 => OctDigits,
            2 => BinDigits,
            _ => DecDigits
        };

        // 扫描数字体（允许分隔符 `_`，含进制前缀后立即 `_`）
        int bodyStart = pos;
        int bodyEnd = pos;
        bool anyDigit = false;
        while (pos < maxLen)
        {
            char c = text[pos];
            if (digits.Contains(c))
            {
                anyDigit = true;
                bodyEnd = pos + 1;
                pos++;
                continue;
            }

            if (c == '_')
            {
                bodyEnd = pos + 1;
                pos++;
                continue;
            }

            break;
        }

        if (!anyDigit)
        {
            // 例如 "0x"、"0o" 只有前缀没有数字 → 结构错误
            string prefix = fromBase switch { 16 => "0x", 8 => "0o", 2 => "0b", _ => "" };
            stream.PreviewPosition = pos > bodyStart ? pos : startPos + Math.Min(2, maxLen - startPos);
            return Token.CreateErrorToken(
                stream,
                prefix.Length > 0
                    ? $"expected a digit after '{prefix}' in integer literal"
                    : "invalid numeric literal");
        }

        // 浮点（仅十进制）：数字体后紧跟 '.' 或 'e/E'
        if (fromBase == 10 && pos < maxLen && (text[pos] == '.' || text[pos] == 'e' || text[pos] == 'E'))
        {
            if (!TryScanFloat(text, ref pos, maxLen, digits, out string? floatError))
            {
                stream.PreviewPosition = Math.Min(pos + 1, maxLen);
                return Token.CreateErrorToken(stream, floatError ?? "invalid float literal");
            }

            // 浮点后缀
            if (LiteralSuffixTable.TryMatch(text.AsSpan(), pos, out _, out int fLen))
            {
                pos += fLen;
            }

            stream.PreviewPosition = pos;
            var fValue = ParseFloatValue(text, bodyStart, pos);
            return Token.CreateContentToken(stream, Kind, context.Terminals[TerminalId], fValue);
        }

        // 整数后缀
        LiteralTypeSuffix suffix = LiteralTypeSuffix.None;
        if (LiteralSuffixTable.TryMatch(text.AsSpan(), pos, out suffix, out int suffixLen))
        {
            pos += suffixLen;
        }

        stream.PreviewPosition = pos;
        var intValue = ParseIntegerValue(text, bodyStart, bodyEnd, fromBase, suffix);
        return Token.CreateContentToken(stream, Kind, context.Terminals[TerminalId], intValue);
    }

    private static bool TryScanFloat(
        string text,
        ref int pos,
        int maxLen,
        SearchValues<char> digits,
        out string? error)
    {
        error = null;
        bool sawDot = false;
        bool sawExp = false;
        bool dotDigit = false;
        bool expDigit = false;

        while (pos < maxLen)
        {
            char c = text[pos];
            if (digits.Contains(c))
            {
                if (sawExp)
                {
                    expDigit = true;
                }
                else if (sawDot)
                {
                    dotDigit = true;
                }

                pos++;
                continue;
            }

            if (c == '_')
            {
                pos++;
                continue;
            }

            if (c == '.' && !sawDot && !sawExp)
            {
                // `..` 是 range pattern 运算符：小数点后必须紧跟数字，
                // 否则不把 '.' 吞入浮点体（保留 `1..10`、`-10..-1` 的词法形态）。
                if (pos + 1 >= maxLen || !char.IsAsciiDigit(text[pos + 1]))
                {
                    break;
                }

                sawDot = true;
                pos++;
                continue;
            }

            if ((c == 'e' || c == 'E') && !sawExp)
            {
                sawExp = true;
                pos++;
                if (pos < maxLen && (text[pos] == '+' || text[pos] == '-'))
                {
                    pos++;
                }

                continue;
            }

            break;
        }

        if (!dotDigit && !expDigit && !(sawExp && expDigit))
        {
            // 至少要有小数点后数字或指数 digit
            if (sawExp && !expDigit)
            {
                error = "expected a digit after the exponent in float literal";
                return false;
            }
        }

        return true;
    }

    private static object? ParseIntegerValue(
        string text,
        int bodyStart,
        int bodyEnd,
        int fromBase,
        LiteralTypeSuffix suffix)
    {
        var digits = StripSeparators(text.AsSpan(bodyStart, bodyEnd - bodyStart));
        if (digits.Length == 0)
        {
            return null;
        }

        ulong magnitude = 0;
        foreach (char c in digits)
        {
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1
            };
            if (d < 0 || d >= fromBase)
            {
                return null;
            }

            if (magnitude > (ulong.MaxValue - (ulong)d) / (ulong)fromBase)
            {
                return null; // 超出 u64 → AST 层报「too large」
            }

            magnitude = magnitude * (ulong)fromBase + (ulong)d;
        }

        return magnitude > long.MaxValue ? magnitude : (long)magnitude;
    }

    private static object? ParseFloatValue(string text, int bodyStart, int pos)
    {
        var body = StripSeparators(text.AsSpan(bodyStart, pos - bodyStart));
        var culture = CultureInfo.InvariantCulture;
        if (double.TryParse(body, NumberStyles.Float, culture, out double d))
        {
            return d;
        }

        return null;
    }

    private static string StripSeparators(ReadOnlySpan<char> span)
    {
        if (!span.Contains('_'))
        {
            return span.ToString();
        }

        return new string(span.ToArray().Where(static c => c != '_').ToArray());
    }
}

/// <summary>
/// 数字规则配置项。
/// </summary>
[MemoryPackable]
public partial struct NumberConfig
{
    public bool EnableHex; // 允许 0x
    public bool EnableBinary; // 允许 0b
    public bool AllowLeadingSign;
    public bool AllowLeadingDot; // .5
    public bool AllowUnderscore; // 1_000
    public bool CaseSensitive; // 后缀大小写敏感（本方案统一接受大小写）
    public List<NumberSuffix> Suffixes;
}

[MemoryPackable]
public readonly partial struct NumberSuffix
{
    public readonly char Symbol;
    public readonly TypeCode TargetType;

    public NumberSuffix(char symbol, TypeCode targetType)
    {
        Symbol = symbol;
        TargetType = targetType;
    }
}
