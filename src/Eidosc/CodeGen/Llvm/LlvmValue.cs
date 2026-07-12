using System.Text;

namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// LLVM IR 值基类
/// </summary>
public abstract class LlvmValue
{
    /// <summary>
    /// 值的类型
    /// </summary>
    public LlvmType Type { get; init; } = LlvmVoidType.Instance;

    /// <summary>
    /// 获取值的 LLVM IR 字符串表示
    /// </summary>
    public abstract string ToIrString();

    public override string ToString() => ToIrString();
}


/// <summary>
/// 局部值 (函数参数、局部变量)
/// </summary>
public sealed class LlvmLocal : LlvmValue
{
    /// <summary>
    /// 局部名称
    /// </summary>
    public string Name { get; init; } = "";

    public override string ToIrString() => Name.StartsWith('%') ? Name : $"%{Name}";

    public override string ToString() => ToIrString();
}

/// <summary>
/// 常量值
/// </summary>
public sealed class LlvmConstant : LlvmValue
{
    /// <summary>
    /// 零值常量 (用于默认初始化)
    /// </summary>
    public static readonly LlvmConstant Zero = new()
    {
        Value = 0,
        Type = LlvmIntType.I64
    };

    /// <summary>
    /// 常量值 (int, float, bool, string, null)
    /// </summary>
    public object? Value { get; init; }

    public override string ToIrString()
    {
                return Value switch
                {
                    null => "null",
                    bool b => b ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False,
                    int i => i.ToString(),
                    long l => l.ToString(),
                    float f => FormatFloat(f),
                    double d => FormatFloat(d),
                    string s => $"\"{EscapeString(s)}\"",
                    _ => Value?.ToString() ?? "null"
                };
    }

    private static string FormatFloat(double value)
    {
        if (double.IsNaN(value))
            return "0x7FF8000000000000"; // NaN
        if (double.IsPositiveInfinity(value))
            return "0x7FF0000000000000"; // +Inf
        if (double.IsNegativeInfinity(value))
            return "0xFFF0000000000000"; // -Inf
        var formatted = value.ToString("G17");
        // LLVM 要求浮点常量包含小数点或指数表示法，否则会被解析为整数
        if (!formatted.Contains('.') && !formatted.Contains('E') && !formatted.Contains('e'))
        {
            formatted += ".0";
        }
        return formatted;
    }

    private static string EscapeString(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        return s.Replace("\\", "\\\\")
                 .Replace("\"", "\\\"")
                 .Replace("\n", "\\n")
                 .Replace("\r", "\\r")
                 .Replace("\t", "\\t");
    }

    public override string ToString() => $"{Type.ToIrString()} {ToIrString()}";
}

/// <summary>
/// Represents a typed LLVM zero initializer constant.
/// </summary>
public sealed class LlvmZeroInitializer : LlvmValue
{
    public override string ToIrString() => "zeroinitializer";

    public override string ToString() => $"{Type.ToIrString()} {ToIrString()}";
}

/// <summary>
/// 字节数组常量（用于字符串全局常量池）
/// </summary>
public sealed class LlvmByteArrayConstant : LlvmValue
{
    public byte[] Bytes { get; init; } = [];

    public override string ToIrString()
    {
        var builder = new StringBuilder();
        builder.Append("c\"");

        foreach (var b in Bytes)
        {
            // LLVM c"..."
            // 可打印 ASCII 直接输出，其余统一转 \XX
            if (b >= 0x20 && b <= 0x7e && b != (byte)'\\' && b != (byte)'"')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('\\');
                builder.Append(b.ToString("X2"));
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    public override string ToString() => $"{Type.ToIrString()} {ToIrString()}";
}

/// <summary>
/// 指令结果引用 (占位符)
/// </summary>
public sealed class LlvmInstructionRef : LlvmValue
{
    /// <summary>
    /// 引用的指令
    /// </summary>
    public LlvmInstruction? Instruction { get; init; }

    public override string ToIrString()
    {
        var resultName = Instruction?.ResultName ?? "tmp";
        return resultName.StartsWith('%') ? resultName : $"%{resultName}";
    }

    public override string ToString() => ToIrString();
}

/// <summary>
/// 未定义值 (用于未初始化变量)
/// </summary>
public sealed class LlvmUndef : LlvmValue
{
    public static readonly LlvmUndef Instance = new();

    public override string ToIrString() => "undef";

    public override string ToString() => ToIrString();
}

/// <summary>
/// 空值 (用于 void 函数调用)
/// </summary>
public sealed class LlvmVoid : LlvmValue
{
    public static readonly LlvmVoid Instance = new();

    public override string ToIrString() => "";

    public override string ToString() => "void";
}

/// <summary>
/// 指针空值
/// </summary>
public sealed class LlvmNullPointer : LlvmValue
{
    public static readonly LlvmNullPointer Instance = new()
    {
        Type = LlvmPointerType.Void()
    };

    public override string ToIrString() => "null";

    public override string ToString() => ToIrString();
}

/// <summary>
/// 指针强制转换
/// </summary>
public sealed class LlvmPtrToInt : LlvmValue
{
    public LlvmValue Pointer { get; init; } = LlvmNullPointer.Instance;
    public LlvmIntType TargetType { get; init; } = LlvmIntType.I64;

    public override string ToIrString() => $"ptrtoint ({Pointer.Type.ToIrString()} {Pointer.ToIrString()} to {TargetType.ToIrString()})";

    public override string ToString() => ToIrString();
}

/// <summary>
/// 整数强制转换
/// </summary>
public sealed class LlvmIntToPtr : LlvmValue
{
    public LlvmValue Integer { get; init; } = LlvmConstant.Zero;
    public LlvmPointerType TargetType { get; init; } = LlvmPointerType.VoidPtr();

    public override string ToIrString() => $"inttoptr ({Integer.Type.ToIrString()} {Integer.ToIrString()} to {TargetType.ToIrString()})";

    public override string ToString() => ToIrString();
}
