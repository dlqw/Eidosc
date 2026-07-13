using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Utils;
using Eidosc.Types;

namespace Eidosc.Mir;

/// <summary>
/// MIR 操作数基类
/// </summary>
public abstract record MirOperand
{
    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// 类型 ID
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;
}

/// <summary>
/// Poison operand that marks MIR produced from an upstream error.
/// </summary>
public sealed record MirPoison : MirOperand
{
    public string Reason { get; init; } = "";

    public override string ToString() => string.IsNullOrWhiteSpace(Reason)
        ? "<mir-poison>"
        : $"<mir-poison: {Reason}>";
}

/// <summary>
/// 常量操作数
/// </summary>
public sealed record MirConstant : MirOperand
{
    /// <summary>
    /// 常量值
    /// </summary>
    public MirConstantValue Value { get; init; } = null!;

    public override string ToString() => Value?.ToString() ?? "null";
}

/// <summary>
/// Symbolic reference to a value-domain generic parameter in a generic MIR body.
/// The generic specializer must replace it with a concrete constant before code generation.
/// </summary>
public sealed record MirConstGenericValue : MirOperand
{
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    public string Name { get; init; } = "";

    public int ParameterIndex { get; init; } = -1;

    public override string ToString() => $"comptime({Name}@{ParameterIndex})";
}

/// <summary>
/// 函数引用（模块级函数符号）
/// </summary>
public sealed record MirFunctionRef : MirOperand
{
    /// <summary>
    /// 函数符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 函数名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 符号类别（Function, Constructor, Module 等）。
    /// 用于替代字符串启发式检测 ADT 构造器。
    /// 默认为 Function，与未设置时的行为一致。
    /// </summary>
    public SymbolKind SymbolKind { get; init; } = SymbolKind.Function;

    /// <summary>
    /// 结构化函数标识（含模块路径、限定名、LLVM mangled 名）。
    /// 用于精确函数解析，替代 Contains/EndsWith 模糊匹配。
    /// 默认为空 FunctionId（SymbolId 无效），需要显式设置。
    /// </summary>
    public FunctionId FunctionId { get; init; } = new();

    /// <summary>
    /// 函数签名类型 ID。与 <see cref="MirOperand.TypeId" /> 分离，避免函数引用的可见值类型
    /// 和调用点函数签名互相覆盖。
    /// </summary>
    public TypeId SignatureTypeId { get; init; } = TypeId.None;

    /// <summary>
    /// 显式类型实参（def + type arguments 中的 type arguments）。
    /// </summary>
    public IReadOnlyList<TypeId> TypeArgumentIds { get; init; } = [];

    public IReadOnlyList<GenericValueArgumentDescriptor> ValueArguments { get; init; } = [];

    /// <summary>
    /// Gets the owner trait when this function reference names a trait method.
    /// </summary>
    public SymbolId TraitOwnerId { get; init; } = SymbolId.None;

    /// <summary>
    /// Gets the method-level Self position for trait dispatch.
    /// </summary>
    public SelfPosition TraitSelfPosition { get; init; } = SelfPosition.Unknown;

    /// <summary>
    /// Gets the exact parameter indices that contain Self for trait dispatch.
    /// </summary>
    public IReadOnlyList<int> TraitSelfParameterIndices { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether Self appears in the trait method result.
    /// </summary>
    public bool TraitSelfInResult { get; init; }

    /// <summary>
    /// Gets the structured role of a known trait method.
    /// </summary>
    public TraitMethodRole TraitMethodRole { get; init; } = TraitMethodRole.None;

    public override string ToString() => $"@{Name}";
}

/// <summary>
/// 常量值
/// </summary>
public abstract record MirConstantValue
{
    /// <summary>
    /// 整数常量
    /// </summary>
    public sealed record IntValue(long Value) : MirConstantValue
    {
        public override string ToString() => Value.ToString();
    }

    /// <summary>
    /// 浮点数常量
    /// </summary>
    public sealed record FloatValue(double Value) : MirConstantValue
    {
        public override string ToString() => Value.ToString();
    }

    /// <summary>
    /// 字符串常量
    /// </summary>
    public sealed record StringValue(string Value) : MirConstantValue
    {
        public override string ToString() => $"\"{Value}\"";
    }

    /// <summary>
    /// 裸 C 字符串常量（不包装为 EidosString，直接作为 const char* 传递给运行时）。
    /// 用于 handler 描述符的 ability/op name 槽位和 effect dispatch 的 op_name 参数。
    /// </summary>
    public sealed record RawStringValue(string Value) : MirConstantValue
    {
        public override string ToString() => $"raw\"{Value}\"";
    }

    /// <summary>
    /// 字符常量
    /// </summary>
    public sealed record CharValue(char Value) : MirConstantValue
    {
        public override string ToString() => $"'{Value}'";
    }

    /// <summary>
    /// 布尔常量
    /// </summary>
    public sealed record BoolValue(bool Value) : MirConstantValue
    {
        public override string ToString() => Value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False;
    }

    /// <summary>
    /// Unit 常量
    /// </summary>
    public sealed record UnitValue : MirConstantValue
    {
        public override string ToString() => "()";
    }
}

/// <summary>
/// 位置操作数（变量、字段、索引）
/// </summary>
public sealed record MirPlace : MirOperand
{
    /// <summary>
    /// 位置类型
    /// </summary>
    public PlaceKind Kind { get; init; }

    /// <summary>
    /// 局部变量 ID（用于 Local 类型）
    /// </summary>
    public LocalId Local { get; init; }

    /// <summary>
    /// 基础位置（用于 Field/Index/Deref 类型）
    /// </summary>
    public MirPlace? Base { get; init; }

    /// <summary>
    /// 字段名（用于 Field 类型）
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// 索引（用于 Index 类型）
    /// </summary>
    public MirOperand? Index { get; init; }

    /// <summary>
    /// 索引访问语义（仅用于 Index 类型）
    /// </summary>
    public MirIndexAccessKind IndexAccessKind { get; init; } = MirIndexAccessKind.Aggregate;

    public override string ToString()
    {
        return Kind switch
        {
            PlaceKind.Local => $"%{Local.Value}",
            PlaceKind.Field => $"{Base}.{FieldName}",
            PlaceKind.Index => $"{Base}[{Index}]",
            PlaceKind.Deref => $"*{Base}",
            _ => "?"
        };
    }
}

/// <summary>
/// 索引访问语义
/// </summary>
public enum MirIndexAccessKind
{
    /// <summary>
    /// 普通聚合内存索引（GEP/load/store）
    /// </summary>
    Aggregate,

    /// <summary>
    /// runtime 列表 API 索引（array_get/array_set）
    /// </summary>
    RuntimeArray
}

/// <summary>
/// 位置类型
/// </summary>
public enum PlaceKind
{
    /// <summary>
    /// 局部变量
    /// </summary>
    Local,

    /// <summary>
    /// 字段访问
    /// </summary>
    Field,

    /// <summary>
    /// 索引访问
    /// </summary>
    Index,

    /// <summary>
    /// 解引用
    /// </summary>
    Deref
}

/// <summary>
/// 临时变量（SSA 风格）
/// </summary>
public sealed record MirTemp : MirOperand
{
    /// <summary>
    /// 临时变量 ID
    /// </summary>
    public TempId Id { get; init; }

    public override string ToString() => $"t{Id.Value}";
}

/// <summary>
/// 局部变量 ID
/// </summary>
public readonly record struct LocalId
{
    /// <summary>
    /// 变量编号
    /// </summary>
    public int Value { get; init; }

    /// <summary>
    /// 无效变量 ID
    /// </summary>
    public static LocalId None => new() { Value = 0 };

    /// <summary>
    /// 是否有效
    /// </summary>
    public bool IsValid => Value > 0;

    public override string ToString() => $"%{Value}";
}

/// <summary>
/// 临时变量 ID
/// </summary>
public readonly record struct TempId
{
    /// <summary>
    /// 临时变量编号
    /// </summary>
    public int Value { get; init; }

    /// <summary>
    /// 无效临时变量 ID
    /// </summary>
    public static TempId None => new() { Value = 0 };

    /// <summary>
    /// 是否有效
    /// </summary>
    public bool IsValid => Value > 0;

    public override string ToString() => $"t{Value}";
}
