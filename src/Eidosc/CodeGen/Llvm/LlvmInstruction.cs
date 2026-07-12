namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// LLVM IR 指令基类
/// </summary>
public abstract class LlvmInstruction
{
    /// <summary>
    /// 结果变量名 (如果指令产生值)
    /// </summary>
    public string? ResultName { get; set; }

    /// <summary>
    /// 获取指令的 LLVM IR 字符串表示
    /// </summary>
    public abstract string ToIrString();

    protected static string FormatResultName(string? resultName)
    {
        if (string.IsNullOrWhiteSpace(resultName))
        {
            return "%tmp";
        }

        return resultName.StartsWith('%') ? resultName : $"%{resultName}";
    }

    public override string ToString() => ToIrString();
}

/// <summary>
/// 二元运算指令
/// </summary>
public sealed class LlvmBinOp : LlvmInstruction
{
    public string Op { get; init; } = "add";
    public LlvmValue Left { get; init; } = LlvmConstant.Zero;
    public LlvmValue Right { get; init; } = LlvmConstant.Zero;
    public LlvmType ResultType { get; init; } = LlvmIntType.I64;

    public override string ToIrString()
    {
        // 根据 ResultType 选择正确的操作符
        var op = ResultType switch
        {
            LlvmFloatType => Op switch
            {
                "add" => "fadd",
                "sub" => "fsub",
                "mul" => "fmul",
                "div" => "fdiv",
                _ => Op
            },
            _ => Op switch
            {
                "fadd" => "add",
                "fsub" => "sub",
                "fmul" => "mul",
                "fdiv" => "sdiv",
                _ => Op
            }
        };

        return $"{FormatResultName(ResultName)} = {op} {ResultType.ToIrString()} {Left.ToIrString()}, {Right.ToIrString()}";
    }
}

/// <summary>
/// 一元运算指令
/// </summary>
public sealed class LlvmUnaryOp : LlvmInstruction
{
    public string Op { get; init; } = "fneg";
    public LlvmValue Operand { get; init; } = LlvmConstant.Zero;
    public LlvmType ResultType { get; init; } = LlvmFloatType.Double;

    public override string ToIrString()
    {
        var opStr = Op switch
        {
            "fneg" => "fneg",
            "neg" => "sub",
            "not" => "xor",
            _ => Op
        };
        return $"{FormatResultName(ResultName)} = {opStr} {ResultType.ToIrString()} {Operand.ToIrString()}";
    }
}

/// <summary>
/// 内存分配指令
/// </summary>
public sealed class LlvmAlloca : LlvmInstruction
{
    public LlvmType AllocatedType { get; init; } = LlvmIntType.I64;
    public int Alignment { get; init; } = 0;

    public override string ToIrString()
    {
        var alignStr = Alignment > 0 ? $", align {Alignment}" : "";
        return $"{FormatResultName(ResultName)} = alloca {AllocatedType.ToIrString()}{alignStr}";
    }
}

/// <summary>
/// 加载指令
/// </summary>
public sealed class LlvmLoad : LlvmInstruction
{
    public LlvmValue Pointer { get; init; } = LlvmNullPointer.Instance;
    public LlvmType LoadType { get; init; } = LlvmIntType.I64;
    public bool IsVolatile { get; init; }

    public override string ToIrString()
    {
        var volatileStr = IsVolatile ? "volatile " : "";
        return $"{FormatResultName(ResultName)} = load {LoadType.ToIrString()}, {volatileStr}ptr {Pointer.ToIrString()}";
    }
}

/// <summary>
/// 存储指令
/// </summary>
public sealed class LlvmStore : LlvmInstruction
{
    public LlvmValue Value { get; init; } = LlvmConstant.Zero;
    public LlvmValue Pointer { get; init; } = LlvmNullPointer.Instance;
    public bool IsVolatile { get; init; }

    public override string ToIrString()
    {
        var volatileStr = IsVolatile ? "volatile " : "";
        return $"store {Value.Type.ToIrString()} {Value.ToIrString()}, {volatileStr}ptr {Pointer.ToIrString()}";
    }
}

/// <summary>
/// 函数调用指令
/// </summary>
public enum LlvmTailCallKind
{
    None,
    Tail,
    MustTail
}

public sealed class LlvmCall : LlvmInstruction
{
    public LlvmValue Function { get; init; } = LlvmNullPointer.Instance;
    public List<LlvmValue> Arguments { get; init; } = [];
    public LlvmType ReturnType { get; init; } = LlvmVoidType.Instance;
    public string? CallingConvention { get; set; }

    /// <summary>
    /// 是否为尾调用（由 MirCall.IsTailCall 传播而来）。
    /// 当为 true 时发射 LLVM "tail call" 而非 "call"。
    /// </summary>
    public bool IsTailCall
    {
        get => TailCallKind != LlvmTailCallKind.None;
        set => TailCallKind = value ? LlvmTailCallKind.Tail : LlvmTailCallKind.None;
    }

    public LlvmTailCallKind TailCallKind { get; set; }

    public override string ToIrString()
    {
        var args = string.Join(", ", Arguments.Select(a => $"{a.Type.ToIrString()} {a.ToIrString()}"));
        var callee = GetFunctionRef();
        var tail = ReferenceEquals(ReturnType, LlvmVoidType.Instance)
            ? $"void {callee}"
            : $"{ReturnType.ToIrString()} {callee}";

        var callKeyword = TailCallKind switch
        {
            LlvmTailCallKind.MustTail => "musttail call",
            LlvmTailCallKind.Tail => "tail call",
            _ => "call"
        };
        var callExpr = $"{callKeyword} {tail}({args})";
        var canAssign = !ReferenceEquals(ReturnType, LlvmVoidType.Instance) && !string.IsNullOrWhiteSpace(ResultName);
        return canAssign ? $"{FormatResultName(ResultName)} = {callExpr}" : callExpr;
    }

    private string GetFunctionRef()
    {
        if (Function is LlvmGlobal global)
            return global.ToIrString();
        return Function.ToIrString();
    }
}

/// <summary>
/// 类型转换指令
/// </summary>
public sealed class LlvmCast : LlvmInstruction
{
    public string Op { get; init; } = WellKnownStrings.InternalNames.Bitcast;
    public LlvmValue Value { get; init; } = LlvmConstant.Zero;
    public LlvmType TargetType { get; init; } = LlvmIntType.I64;

    public override string ToIrString()
    {
        return $"{FormatResultName(ResultName)} = {Op} {Value.Type.ToIrString()} {Value.ToIrString()} to {TargetType.ToIrString()}";
    }
}

/// <summary>
/// 整数扩展指令
/// </summary>
public sealed class LlvmZext : LlvmInstruction
{
    public LlvmValue Value { get; init; } = LlvmConstant.Zero;
    public LlvmType TargetType { get; init; } = LlvmIntType.I64;

    public override string ToIrString()
    {
        return $"{FormatResultName(ResultName)} = zext {Value.Type.ToIrString()} {Value.ToIrString()} to {TargetType.ToIrString()}";
    }
}

/// <summary>
/// 整数截断指令
/// </summary>
public sealed class LlvmTrunc : LlvmInstruction
{
    public LlvmValue Value { get; init; } = LlvmConstant.Zero;
    public LlvmType TargetType { get; init; } = LlvmIntType.I32;

    public override string ToIrString()
    {
        return $"{FormatResultName(ResultName)} = trunc {Value.Type.ToIrString()} {Value.ToIrString()} to {TargetType.ToIrString()}";
    }
}

/// <summary>
/// 浮点转换指令
/// </summary>
public sealed class LlvmFpExt : LlvmInstruction
{
    public LlvmValue Value { get; init; } = LlvmConstant.Zero;
    public LlvmType TargetType { get; init; } = LlvmFloatType.Double;

    public override string ToIrString()
    {
        return $"{FormatResultName(ResultName)} = fpext {Value.Type.ToIrString()} {Value.ToIrString()} to {TargetType.ToIrString()}";
    }
}

/// <summary>
/// 浮点截断指令
/// </summary>
public sealed class LlvmFpTrunc : LlvmInstruction
{
    public LlvmValue Value { get; init; } = LlvmConstant.Zero;
    public LlvmType TargetType { get; init; } = LlvmFloatType.Float;

    public override string ToIrString()
    {
        return $"{FormatResultName(ResultName)} = fptrunc {Value.Type.ToIrString()} {Value.ToIrString()} to {TargetType.ToIrString()}";
    }
}

/// <summary>
/// 比较指令
/// </summary>
public sealed class LlvmIcmp : LlvmInstruction
{
    public string Predicate { get; init; } = "eq";
    public LlvmValue Left { get; init; } = LlvmConstant.Zero;
    public LlvmValue Right { get; init; } = LlvmConstant.Zero;

    public override string ToIrString()
    {
        return $"{FormatResultName(ResultName)} = icmp {Predicate} {Left.Type.ToIrString()} {Left.ToIrString()}, {Right.ToIrString()}";
    }
}

/// <summary>
/// 浮点比较指令
/// </summary>
public sealed class LlvmFcmp : LlvmInstruction
{
    public string Predicate { get; init; } = "oeq";
    public LlvmValue Left { get; init; } = LlvmConstant.Zero;
    public LlvmValue Right { get; init; } = LlvmConstant.Zero;

    public override string ToIrString()
    {
        return $"{FormatResultName(ResultName)} = fcmp {Predicate} {Left.Type.ToIrString()} {Left.ToIrString()}, {Right.ToIrString()}";
    }
}

/// <summary>
/// 获取元素指针指令
/// </summary>
public sealed class LlvmGetElementPtr : LlvmInstruction
{
    public LlvmValue Pointer { get; init; } = LlvmNullPointer.Instance;
    public LlvmValue Index { get; init; } = LlvmConstant.Zero;
    public LlvmType ElementType { get; init; } = LlvmIntType.I64;

    /// <summary>
    /// 结构体字段 GEP 模式。
    /// 当非 null 时，输出 getelementptr %struct.Name, ptr %p, i32 0, i32 {StructFieldIndex}。
    /// </summary>
    public LlvmStructType? StructType { get; init; }

    /// <summary>
    /// 结构体字段索引（仅在 StructType 非 null 时使用）。
    /// </summary>
    public int StructFieldIndex { get; init; }

    public override string ToIrString()
    {
        if (StructType != null)
        {
            return $"{FormatResultName(ResultName)} = getelementptr {StructType.ToIrString()}, ptr {Pointer.ToIrString()}, i32 0, i32 {StructFieldIndex}";
        }
        return $"{FormatResultName(ResultName)} = getelementptr {ElementType.ToIrString()}, ptr {Pointer.ToIrString()}, {Index.Type.ToIrString()} {Index.ToIrString()}";
    }
}

/// <summary>
/// 提取值指令
/// </summary>
public sealed class LlvmExtractValue : LlvmInstruction
{
    public LlvmValue Aggregate { get; init; } = LlvmNullPointer.Instance;
    public int[] Indices { get; init; } = [];

    public override string ToIrString()
    {
        var indicesStr = string.Join(", ", Indices.Select(i => i.ToString()));
        return $"{FormatResultName(ResultName)} = extractvalue {Aggregate.Type.ToIrString()} {Aggregate.ToIrString()}, {indicesStr}";
    }
}

/// <summary>
/// 插入值指令
/// </summary>
public sealed class LlvmInsertValue : LlvmInstruction
{
    public LlvmValue Aggregate { get; init; } = LlvmNullPointer.Instance;
    public LlvmValue Element { get; init; } = LlvmConstant.Zero;
    public int[] Indices { get; init; } = [];

    public override string ToIrString()
    {
        var indicesStr = string.Join(", ", Indices.Select(i => i.ToString()));
        return $"{FormatResultName(ResultName)} = insertvalue {Element.Type.ToIrString()} {Element.ToIrString()}, {Aggregate.Type.ToIrString()} {Aggregate.ToIrString()}, {indicesStr}";
    }
}

/// <summary>
/// 选择指令 (用于三元运算)
/// </summary>
public sealed class LlvmSelect : LlvmInstruction
{
    public LlvmValue Condition { get; init; } = LlvmConstant.Zero;
    public LlvmValue TrueValue { get; init; } = LlvmConstant.Zero;
    public LlvmValue FalseValue { get; init; } = LlvmConstant.Zero;

    public override string ToIrString()
    {
        return $"{FormatResultName(ResultName)} = select i1 {Condition.ToIrString()}, {TrueValue.Type.ToIrString()} {TrueValue.ToIrString()}, {FalseValue.Type.ToIrString()} {FalseValue.ToIrString()}";
    }
}

/// <summary>
/// Phi 指令 (SSA)
/// </summary>
public sealed class LlvmPhi : LlvmInstruction
{
    public LlvmType PhiType { get; init; } = LlvmIntType.I64;
    public List<(LlvmValue Value, LlvmBasicBlock Block)> IncomingValues { get; init; } = [];

    public override string ToIrString()
    {
        var values = string.Join(", ", IncomingValues.Select(p => $"[ {p.Value.ToIrString()}, %{p.Block.Label} ]"));
        return $"{FormatResultName(ResultName)} = phi {PhiType.ToIrString()} {values}";
    }
}
