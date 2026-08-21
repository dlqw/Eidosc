using System.Numerics;
using Eidosc.Types;

namespace Eidosc;

/// <summary>
/// 字面量类型后缀。None 表示无后缀（走默认/上下文类型）。
/// 完整清单覆盖 Eidos 全部基元数值类型（仿 Rust：i8..u64/f32/f64），
/// 以及任意位宽整数后缀 iN/uN（N 为编译期位宽）。
/// </summary>
public enum LiteralTypeSuffix
{
    None = 0,
    Int8,
    Int16,
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Float32,
    Float64,
    IntArbitrary,
    UIntArbitrary
}

/// <summary>
/// 字面量后缀的单一事实来源：词法、AST、类型层共用。
/// 规范小写（i8/u8/f32），同时接受大写变体（C# 心智）；
/// 任意位宽使用 i&lt;N&gt;/u&lt;N&gt;，例如 i24/u512。
/// </summary>
public static class LiteralSuffixTable
{
    // 规范（小写）+ 兼容（大写）。匹配按最长命中；短于 2 字符的旧单字母后缀
    // （u/U/l/s/f/d/b）不再被接受——见 RFC §10.C。
    private static readonly (string Text, LiteralTypeSuffix Suffix)[] Suffixes =
    [
        ("i64", LiteralTypeSuffix.Int64),
        ("i32", LiteralTypeSuffix.Int32),
        ("i16", LiteralTypeSuffix.Int16),
        ("i8", LiteralTypeSuffix.Int8),
        ("u64", LiteralTypeSuffix.UInt64),
        ("u32", LiteralTypeSuffix.UInt32),
        ("u16", LiteralTypeSuffix.UInt16),
        ("u8", LiteralTypeSuffix.UInt8),
        ("f64", LiteralTypeSuffix.Float64),
        ("f32", LiteralTypeSuffix.Float32),
        ("I64", LiteralTypeSuffix.Int64),
        ("I32", LiteralTypeSuffix.Int32),
        ("I16", LiteralTypeSuffix.Int16),
        ("I8", LiteralTypeSuffix.Int8),
        ("U64", LiteralTypeSuffix.UInt64),
        ("U32", LiteralTypeSuffix.UInt32),
        ("U16", LiteralTypeSuffix.UInt16),
        ("U8", LiteralTypeSuffix.UInt8),
        ("F64", LiteralTypeSuffix.Float64),
        ("F32", LiteralTypeSuffix.Float32)
    ];

    /// <summary>
    /// 在 <paramref name="pos"/> 处尝试匹配后缀；成功后返回类型与消耗长度。
    /// 后缀后的字符不得是标识符字符（避免 7i8 之后粘连 a 之类的形似）。
    /// </summary>
    public static bool TryMatch(ReadOnlySpan<char> text, int pos, out LiteralTypeSuffix suffix, out int length)
    {
        return TryMatch(text, pos, out suffix, out length, out _);
    }

    /// <summary>
    /// 尝试匹配后缀；任意位宽后缀 iN/uN 的位宽通过 <paramref name="arbitraryWidth"/> 返回。
    /// </summary>
    public static bool TryMatch(
        ReadOnlySpan<char> text,
        int pos,
        out LiteralTypeSuffix suffix,
        out int length,
        out int arbitraryWidth)
    {
        suffix = LiteralTypeSuffix.None;
        length = 0;
        arbitraryWidth = 0;

        int bestLength = 0;
        LiteralTypeSuffix bestSuffix = LiteralTypeSuffix.None;

        foreach (var (candidate, candidateSuffix) in Suffixes)
        {
            if (candidate.Length <= bestLength ||
                pos + candidate.Length > text.Length)
            {
                continue;
            }

            if (!text.Slice(pos, candidate.Length).SequenceEqual(candidate.AsSpan()))
            {
                continue;
            }

            int after = pos + candidate.Length;
            if (after < text.Length &&
                (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
            {
                continue; // 后缀后紧跟标识符字符 → 不是独立后缀
            }

            bestLength = candidate.Length;
            bestSuffix = candidateSuffix;
        }

        if (bestLength > 0)
        {
            suffix = bestSuffix;
            length = bestLength;
            return true;
        }

        // 任意位宽：i/u + 纯数字。固定后缀优先，因此 i8/i16/... 不会走到这里。
        if (pos >= text.Length)
        {
            return false;
        }

        char prefix = text[pos];
        if (prefix is not ('i' or 'I' or 'u' or 'U'))
        {
            return false;
        }

        int digitStart = pos + 1;
        int digitEnd = digitStart;
        while (digitEnd < text.Length && char.IsAsciiDigit(text[digitEnd]))
        {
            digitEnd++;
        }

        if (digitEnd == digitStart)
        {
            return false;
        }

        int afterSuffix = digitEnd;
        if (afterSuffix < text.Length &&
            (char.IsLetterOrDigit(text[afterSuffix]) || text[afterSuffix] == '_'))
        {
            return false;
        }

        if (!int.TryParse(text.Slice(digitStart, digitEnd - digitStart), out int width) || width <= 0)
        {
            return false;
        }

        suffix = prefix is 'i' or 'I' ? LiteralTypeSuffix.IntArbitrary : LiteralTypeSuffix.UIntArbitrary;
        length = digitEnd - pos;
        arbitraryWidth = width;
        return true;
    }

    public static bool IsInteger(LiteralTypeSuffix suffix) => suffix is
        LiteralTypeSuffix.Int8 or LiteralTypeSuffix.Int16 or LiteralTypeSuffix.Int32 or LiteralTypeSuffix.Int64 or
        LiteralTypeSuffix.UInt8 or LiteralTypeSuffix.UInt16 or LiteralTypeSuffix.UInt32 or LiteralTypeSuffix.UInt64 or
        LiteralTypeSuffix.IntArbitrary or LiteralTypeSuffix.UIntArbitrary;

    public static bool IsFloat(LiteralTypeSuffix suffix) => suffix is LiteralTypeSuffix.Float32 or LiteralTypeSuffix.Float64;

    public static bool IsSigned(LiteralTypeSuffix suffix) => suffix is
        LiteralTypeSuffix.Int8 or LiteralTypeSuffix.Int16 or LiteralTypeSuffix.Int32 or LiteralTypeSuffix.Int64 or
        LiteralTypeSuffix.IntArbitrary;

    /// <summary>
    /// 后缀的位宽（整数 8..64；浮点按 32/64）。任意位宽请使用
    /// <see cref="TryGetMagnitudeLimit(LiteralTypeSuffix,int,out ulong)"/> 或直接读取字面量宽度。
    /// </summary>
    public static int BitWidth(LiteralTypeSuffix suffix) => suffix switch
    {
        LiteralTypeSuffix.Int8 or LiteralTypeSuffix.UInt8 => 8,
        LiteralTypeSuffix.Int16 or LiteralTypeSuffix.UInt16 => 16,
        LiteralTypeSuffix.Int32 or LiteralTypeSuffix.UInt32 => 32,
        LiteralTypeSuffix.Int64 or LiteralTypeSuffix.UInt64 or LiteralTypeSuffix.Float64 => 64,
        LiteralTypeSuffix.Float32 => 32,
        _ => 0
    };

    /// <summary>
    /// 正数幅度上限（对无符号即自身；对有符号即 Max，负数由一元负号承担，
    /// 与 Rust / Eidos 的「- 是一元运算符」一致；signed min 需「负号 + 类型层负向校验」）。
    /// </summary>
    public static bool TryGetMagnitudeLimit(LiteralTypeSuffix suffix, out ulong max)
    {
        return TryGetMagnitudeLimit(suffix, arbitraryWidth: 0, out max);
    }

    /// <summary>
    /// 正数幅度上限。任意位宽按 <paramref name="arbitraryWidth"/> 计算；
    /// 超过 64 位时以 ulong.MaxValue 作为安全上限（AST 当前只能承载 u64 以内的字面量）。
    /// </summary>
    public static bool TryGetMagnitudeLimit(LiteralTypeSuffix suffix, int arbitraryWidth, out ulong max)
    {
        if (suffix is LiteralTypeSuffix.IntArbitrary or LiteralTypeSuffix.UIntArbitrary)
        {
            int bits = arbitraryWidth;
            if (bits <= 0)
            {
                max = 0;
                return false;
            }

            // Bool/I1/U1 统一为 0..1。
            if (bits == 1)
            {
                max = 1;
                return true;
            }

            if (suffix == LiteralTypeSuffix.UIntArbitrary)
            {
                max = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
                return true;
            }

            max = bits switch
            {
                64 => (ulong)long.MaxValue,
                > 64 => ulong.MaxValue,
                _ => (1UL << (bits - 1)) - 1
            };
            return true;
        }

        max = suffix switch
        {
            LiteralTypeSuffix.UInt8 => byte.MaxValue,
            LiteralTypeSuffix.UInt16 => ushort.MaxValue,
            LiteralTypeSuffix.UInt32 => uint.MaxValue,
            LiteralTypeSuffix.UInt64 => ulong.MaxValue,
            LiteralTypeSuffix.Int8 => (ulong)sbyte.MaxValue,
            LiteralTypeSuffix.Int16 => (ulong)short.MaxValue,
            LiteralTypeSuffix.Int32 => (ulong)int.MaxValue,
            LiteralTypeSuffix.Int64 => (ulong)long.MaxValue,
            _ => 0
        };
        return max != 0 || suffix is LiteralTypeSuffix.Int8 or LiteralTypeSuffix.Int16 or LiteralTypeSuffix.Int32 or LiteralTypeSuffix.Int64 or LiteralTypeSuffix.UInt8 or LiteralTypeSuffix.UInt16 or LiteralTypeSuffix.UInt32 or LiteralTypeSuffix.UInt64;
    }

    /// <summary>
    /// 正数幅度上限（BigInteger）。任意位宽按 <paramref name="arbitraryWidth"/> 精确计算，
    /// 支持超过 64 位的字面量范围校验。
    /// </summary>
    public static bool TryGetBigIntegerMagnitudeLimit(
        LiteralTypeSuffix suffix,
        int arbitraryWidth,
        out BigInteger max)
    {
        if (suffix is LiteralTypeSuffix.IntArbitrary or LiteralTypeSuffix.UIntArbitrary)
        {
            int bits = arbitraryWidth;
            if (bits <= 0 || bits > BaseTypes.MaxIntegerWidth)
            {
                max = BigInteger.Zero;
                return false;
            }

            if (bits == 1)
            {
                max = BigInteger.One;
                return true;
            }

            if (suffix == LiteralTypeSuffix.UIntArbitrary)
            {
                max = (BigInteger.One << bits) - 1;
                return true;
            }

            max = (BigInteger.One << (bits - 1)) - 1;
            return true;
        }

        if (TryGetMagnitudeLimit(suffix, arbitraryWidth, out ulong ulongMax))
        {
            max = ulongMax;
            return true;
        }

        max = BigInteger.Zero;
        return false;
    }

    /// <summary>
    /// 有符号最小值（|min| 作为幅度上限，供「负号 + 字面量」路径使用）。
    /// BigInteger 版本支持超过 64 位的任意位宽。
    /// </summary>
    public static bool TryGetBigIntegerSignedMinMagnitude(
        LiteralTypeSuffix suffix,
        int arbitraryWidth,
        out BigInteger minMagnitude)
    {
        if (suffix == LiteralTypeSuffix.IntArbitrary)
        {
            int bits = arbitraryWidth;
            if (bits <= 0 || bits > BaseTypes.MaxIntegerWidth)
            {
                minMagnitude = BigInteger.Zero;
                return false;
            }

            minMagnitude = bits == 1 ? BigInteger.One : BigInteger.One << (bits - 1);
            return true;
        }

        if (TryGetSignedMinMagnitude(suffix, arbitraryWidth, out ulong ulongMin))
        {
            minMagnitude = ulongMin;
            return true;
        }

        minMagnitude = BigInteger.Zero;
        return false;
    }

    /// <summary>
    /// 有符号最小值（|min| 作为幅度上限，供「负号 + 字面量」路径使用）。
    /// </summary>
    public static bool TryGetSignedMinMagnitude(LiteralTypeSuffix suffix, out ulong minMagnitude)
    {
        return TryGetSignedMinMagnitude(suffix, arbitraryWidth: 0, out minMagnitude);
    }

    /// <summary>
    /// 有符号最小值（|min| 作为幅度上限）。任意位宽超过 64 位时返回 ulong.MaxValue，
    /// 表示当前 AST 可承载的任意 u64 幅度都合法（min 已超出可表示范围）。
    /// </summary>
    public static bool TryGetSignedMinMagnitude(LiteralTypeSuffix suffix, int arbitraryWidth, out ulong minMagnitude)
    {
        if (suffix == LiteralTypeSuffix.IntArbitrary)
        {
            int bits = arbitraryWidth;
            if (bits <= 0)
            {
                minMagnitude = 0;
                return false;
            }

            if (bits == 1)
            {
                minMagnitude = 1;
                return true;
            }

            minMagnitude = bits switch
            {
                64 => 9223372036854775808UL,
                > 64 => ulong.MaxValue,
                _ => 1UL << (bits - 1)
            };
            return true;
        }

        minMagnitude = suffix switch
        {
            LiteralTypeSuffix.Int8 => 128UL,
            LiteralTypeSuffix.Int16 => 32768UL,
            LiteralTypeSuffix.Int32 => 2147483648UL,
            LiteralTypeSuffix.Int64 => 9223372036854775808UL,
            _ => 0
        };
        return minMagnitude != 0;
    }
}
