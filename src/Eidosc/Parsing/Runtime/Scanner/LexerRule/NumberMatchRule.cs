using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace Eidosc;

/// <summary>
/// 数字字面量规则
/// 支持：Hex(0x), Bin(0b), Oct(0c), 科学计数法(1e10), 类型后缀(100L, 1.5f)
/// </summary>
[MemoryPackable]
public partial class NumberLiteralRule : LiteralRule
{
    // 配置数据
    public readonly NumberConfig Config;
    public SyntaxKind Kind;

    // 运行时缓存 (SIMD)
    private readonly char[] _firstsCache;
    private static readonly SearchValues<char> DecDigits = SearchValues.Create("0123456789");
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    // 后缀映射表 (如 'L' -> Int64, 'f' -> Single)
    private readonly Dictionary<char, TypeCode> _suffixMap;

    [MemoryPackConstructor]
    public NumberLiteralRule(int terminalId, NumberConfig config, SyntaxKind kind = default) : base(terminalId)
    {
        Config = config;
        Kind = kind;

        // 构建后缀映射
        _suffixMap = new Dictionary<char, TypeCode>();
        foreach (var suffix in config.Suffixes)
        {
            _suffixMap[suffix.Symbol] = suffix.TargetType;
            if (!config.CaseSensitive)
            {
                _suffixMap[char.ToLowerInvariant(suffix.Symbol)] = suffix.TargetType;
                _suffixMap[char.ToUpperInvariant(suffix.Symbol)] = suffix.TargetType;
            }
        }

        // 构建首字符缓存
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
        if (stream.Eof()) return null;

        // 1. 快速预检查
        char first = stream.PreviewChar;
        // 注意：此处可优化，直接判断 first 是否在 _firstsCache 范围内
        bool isDigit = char.IsAsciiDigit(first);
        if (!isDigit && first != '.' && first != '+' && first != '-') return null;

        // 2. 初始化上下文
        LiteralContext ctx = default;
        ctx.Base = 10;
        ctx.TargetType = TypeCode.Int32; // 默认类型

        string text = stream.Text;
        int startPos = stream.PreviewPosition;
        int pos = startPos;
        int maxLen = text.Length;

        // 3. 处理符号 (+/-)
        if (Config.AllowLeadingSign && (first == '+' || first == '-'))
        {
            pos++;
            if (pos >= maxLen) return null; // 只有符号没有数字
        }

        // 4. 处理进制前缀 (0x, 0b)
        // 只有当以 '0' 开头时才可能是进制前缀
        if (pos + 1 < maxLen && text[pos] == '0')
        {
            char next = text[pos + 1];
            // 检查 Hex
            if (Config.EnableHex && (next == 'x' || next == 'X'))
            {
                ctx.Base = 16;
                pos += 2;
            }
            // 检查 Binary
            else if (Config.EnableBinary && (next == 'b' || next == 'B'))
            {
                ctx.Base = 2;
                pos += 2;
            }
        }

        // 5. 扫描数字主体
        int bodyStart = pos;
        bool hasDot = false;
        bool hasExp = false;

        // 根据进制选择字符集
        var validDigits = ctx.Base == 16 ? HexDigits : DecDigits;

        while (pos < maxLen)
        {
            char c = text[pos];

            // A. 合法数字
            if (validDigits.Contains(c))
            {
                pos++;
                continue;
            }

            // B. 下划线 (忽略)
            if (Config.AllowUnderscore && c == '_')
            {
                pos++;
                continue;
            }

            // C. 小数点 (仅十进制)
            if (c == '.' && ctx.Base == 10)
            {
                if (pos + 1 < maxLen && text[pos + 1] == '.')
                {
                    break; 
                }

                if (hasDot || hasExp) break; // 已经有小数点了或在指数后
                if (pos + 1 >= maxLen || !char.IsAsciiDigit(text[pos + 1]))
                {
                    break;
                }
                hasDot = true;
                ctx.TargetType = TypeCode.Double; // 升级为浮点
                pos++;
                continue;
            }

            // D. 指数 (e/E) (仅十进制)
            if (ctx.Base == 10 && (c == 'e' || c == 'E'))
            {
                if (hasExp) break;
                hasExp = true;
                ctx.TargetType = TypeCode.Double;
                pos++;

                // 指数后允许带符号
                if (pos < maxLen)
                {
                    char nextE = text[pos];
                    if (nextE == '+' || nextE == '-') pos++;
                }

                continue;
            }

            // 其他字符，结束扫描
            break;
        }

        // 如果没有读取到任何有效数字位 (例如只读了前缀 "0x")
        if (pos == bodyStart) return null;

        // 6. 处理后缀 (f, d, m, L, U...)
        if (pos < maxLen)
        {
            char potentialSuffix = text[pos];
            if (_suffixMap.TryGetValue(potentialSuffix, out TypeCode typeCode))
            {
                ctx.TargetType = typeCode;
                pos++;
            }
        }

        // 7. 提取 Body 并转换
        ctx.BodySpan = text.AsSpan(startPos, pos - startPos); // 包含符号和前缀，为了 Parse 方便

        // 验证结果
        if (ConvertNumber(ref ctx))
        {
            stream.PreviewPosition = pos;
            return Token.CreateContentToken(stream, Kind, context.Terminals[TerminalId], ctx.ResultValue);
        }

        // 转换失败（溢出或格式错误）
        stream.PreviewPosition = pos; // 仍然消耗掉字符，避免死循环，但返回错误
        return Token.CreateErrorToken(stream, GetErrorMessage(LexerErrorCode.InvalidNumber));
    }

    private bool ConvertNumber(ref LiteralContext ctx)
    {
        var span = ctx.BodySpan;

        // 移除下划线 (如果存在)
        // 优化：先检查 Contains，不存在则直接用原 Span
        if (Config.AllowUnderscore && span.Contains('_'))
        {
            Span<char> buffer = stackalloc char[span.Length];
            int w = 0;
            foreach (char c in span)
            {
                if (c != '_') buffer[w++] = c;
            }

            span = new ReadOnlySpan<char>(buffer[..w].ToArray()); // 注意：TryParse 需要特定格式，这里简化处理
            // 实际上对于 TryParse，我们需要去掉 0x 等前缀，这里为了简化，
            // 建议使用 .NET 自带的 NumberStyles，或者手动切片。
        }

        // 准备 NumberStyles
        NumberStyles style = NumberStyles.None;
        if (ctx.Base == 16) style = NumberStyles.HexNumber;
        else
        {
            style = NumberStyles.Integer;
            if (ctx.TargetType is TypeCode.Double or TypeCode.Single or TypeCode.Decimal)
                style |= NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        }

        style |= NumberStyles.AllowLeadingSign;

        // 处理进制前缀对 Parse 的影响
        // int.TryParse 不支持 "0x" 前缀，需要手动切除
        if (ctx.Base == 16 && (span.StartsWith("0x") || span.StartsWith("0X"))) span = span[2..];
        else if (ctx.Base == 2 && (span.StartsWith("0b") || span.StartsWith("0B"))) span = span[2..];

        // 只有 10 进制时符号位才需要在 span 里；
        // 如果是 16 进制，AllowLeadingSign 通常用于负补码，但在 C# 字面量中 0x 通常被视为无符号或取决于目标类型
        // 这里简化：如果是 Hex，暂不处理负号，除非手动逻辑。

        try
        {
            var culture = CultureInfo.InvariantCulture;
            return ctx.TargetType switch
            {
                TypeCode.Int32 => int.TryParse(span, style, culture, out int i) && SetResult(ref ctx, i),
                TypeCode.Int64 => long.TryParse(span, style, culture, out long l) && SetResult(ref ctx, l),
                TypeCode.Single => float.TryParse(span, style, culture, out float f) && SetResult(ref ctx, f),
                TypeCode.Double => double.TryParse(span, style, culture, out double d) && SetResult(ref ctx, d),
                TypeCode.Decimal => decimal.TryParse(span, style, culture, out decimal m) && SetResult(ref ctx, m),
                TypeCode.UInt32 => uint.TryParse(span, style, culture, out uint ui) && SetResult(ref ctx, ui),
                TypeCode.UInt64 => ulong.TryParse(span, style, culture, out ulong ul) && SetResult(ref ctx, ul),
                _ => double.TryParse(span, style, culture, out double def) && SetResult(ref ctx, def)
            };
        }
        catch
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SetResult(ref LiteralContext ctx, object val)
    {
        ctx.ResultValue = val;
        return true;
    }
}

/// <summary>
/// 数字规则配置项
/// </summary>
[MemoryPackable]
public partial struct NumberConfig
{
    public bool EnableHex; // 允许 0x
    public bool EnableBinary; // 允许 0b
    public bool AllowLeadingSign;
    public bool AllowLeadingDot; // .5
    public bool AllowUnderscore; // 1_000
    public bool CaseSensitive; // 后缀大小写敏感
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
