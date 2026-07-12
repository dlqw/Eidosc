using System.Buffers;
using System.Runtime.CompilerServices;

namespace Eidosc.Utilities;

public static class CodePoints
{
    // --- 换行符定义 ---
    // 包含: \n, \r, \f (换页), \u0085 (NEL), \u2028 (Line Sep), \u2029 (Para Sep)
    // 注意：\v (Vertical Tab) 在某些语言视为换行，但通常视为空白。C# 编译器将其视为换行。
    private static readonly SearchValues<char> AsciiNewLines =
        SearchValues.Create("\n\r\f\u0085");

    // --- 空白符定义 ---
    // 包含常见的 ASCII 空白
    private static readonly SearchValues<char> AsciiWhitespace =
        SearchValues.Create(" \t\v\f\n\r");

    // --- 标识符分隔符 ---
    // 这是一个默认集合，用于错误恢复。
    // 包含：空白, 括号, 逗号, 分号, 方括号, 花括号, 空字符
    private static readonly SearchValues<char> DefaultDelimiters =
        SearchValues.Create(" \t\r\n\v()[],;{}\0");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNewLine(char c)
    {
        // 快速路径：ASCII 范围
        if (c <= 127) return c == '\n' || c == '\r' || c == '\f';

        // Unicode 换行符
        return c is '\u0085' or '\u2028' or '\u2029';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWhitespace(char c)
    {
        // 快速路径：ASCII
        if (c <= 127) return AsciiWhitespace.Contains(c);

        // Unicode 空白 (包含全角空格 \u3000 等)
        return char.IsWhiteSpace(c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDelimiter(char c)
    {
        // 快速路径：ASCII 常见分隔符
        if (DefaultDelimiters.Contains(c)) return true;

        // 所有 Unicode 空白都是分隔符
        if (c > 127 && char.IsWhiteSpace(c)) return true;

        return false;
    }
}