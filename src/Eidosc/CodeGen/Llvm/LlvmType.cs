namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// LLVM IR 类型基类
/// </summary>
public abstract class LlvmType
{
    /// <summary>
    /// 获取类型的 LLVM IR 字符串表示
    /// </summary>
    public abstract string ToIrString();

    public override abstract bool Equals(object? obj);
    public override abstract int GetHashCode();

    public static bool operator ==(LlvmType? left, LlvmType? right) => Equals(left, right);
    public static bool operator !=(LlvmType? left, LlvmType? right) => !Equals(left, right);
}

/// <summary>
/// void 类型
/// </summary>
public sealed class LlvmVoidType : LlvmType
{
    public static readonly LlvmVoidType Instance = new();

    public override string ToIrString() => "void";
    public override bool Equals(object? obj) => obj is LlvmVoidType;
    public override int GetHashCode() => HashCode.Combine(typeof(LlvmVoidType));
    public override string ToString() => ToIrString();
}

/// <summary>
/// 整数类型
/// </summary>
public sealed class LlvmIntType : LlvmType
{
    /// <summary>
    /// 位数 (1, 8, 16, 32, 64, 128)
    /// </summary>
    public int Bits { get; init; }

    public override string ToIrString() => $"i{Bits}";
    public override bool Equals(object? obj) => obj is LlvmIntType other && Bits == other.Bits;
    public override int GetHashCode() => HashCode.Combine(typeof(LlvmIntType), Bits);
    public override string ToString() => ToIrString();

    // 预定义类型
    public static readonly LlvmIntType I1 = new() { Bits = 1 };
    public static readonly LlvmIntType I8 = new() { Bits = 8 };
    public static readonly LlvmIntType I16 = new() { Bits = 16 };
    public static readonly LlvmIntType I32 = new() { Bits = 32 };
    public static readonly LlvmIntType I64 = new() { Bits = 64 };

    /// <summary>
    /// 创建 void 指针类型 (ptr)
    /// </summary>
    public static LlvmPointerType Void() => new() { ElementType = LlvmVoidType.Instance };

    /// <summary>
    /// 创建 void 指针类型 (int*)
    /// </summary>
    public static LlvmPointerType VoidPtr() => new() { ElementType = LlvmVoidType.Instance };
}

/// <summary>
/// 浮点类型
/// </summary>
public sealed class LlvmFloatType : LlvmType
{
    /// <summary>
    /// 位数 (16 = half, 32 = float, 64 = double, 128 = fp128)
    /// </summary>
    public int Bits { get; init; }

    public override string ToIrString() => Bits switch
    {
        16 => "half",
        32 => "float",
        64 => "double",
        128 => "fp128",
        _ => $"float{Bits}"
    };

    public override bool Equals(object? obj) => obj is LlvmFloatType other && Bits == other.Bits;
    public override int GetHashCode() => HashCode.Combine(typeof(LlvmFloatType), Bits);
    public override string ToString() => ToIrString();

    // 预定义类型
    public static readonly LlvmFloatType Half = new() { Bits = 16 };
    public static readonly LlvmFloatType Float = new() { Bits = 32 };
    public static readonly LlvmFloatType Double = new() { Bits = 64 };
}

/// <summary>
/// 指针类型
/// </summary>
public sealed class LlvmPointerType : LlvmType
{
    private static readonly LlvmPointerType DefaultVoidPtr = new() { ElementType = null, AddressSpace = 0 };

    /// <summary>
    /// 元素类型 (void* 用 null)
    /// </summary>
    public LlvmType? ElementType { get; init; }

    /// <summary>
    /// 地址空间 (0 = default)
    /// </summary>
    public int AddressSpace { get; init; }

    public override string ToIrString()
    {
        if (ElementType == null)
        {
            return AddressSpace == 0 ? "ptr" : $"ptr addrspace({AddressSpace})";
        }
        var elemStr = ElementType.ToIrString();
        return AddressSpace == 0 ? $"{elemStr}*" : $"{elemStr}* addrspace({AddressSpace})";
    }

    public override bool Equals(object? obj)
    {
        return obj is LlvmPointerType other &&
               AddressSpace == other.AddressSpace &&
               Equals(ElementType, other.ElementType);
    }

    public override int GetHashCode() => HashCode.Combine(typeof(LlvmPointerType), ElementType, AddressSpace);
    public override string ToString() => ToIrString();

    /// <summary>
    /// 创建 void 指针
    /// </summary>
    public static LlvmPointerType VoidPtr(int addressSpace = 0) =>
        addressSpace == 0
            ? DefaultVoidPtr
            : new LlvmPointerType { ElementType = null, AddressSpace = addressSpace };

    /// <summary>
    /// 创建 void 指针类型 (ptr) - 别名
    /// </summary>
    public static LlvmPointerType Void() => VoidPtr();
}

/// <summary>
/// 数组类型
/// </summary>
public sealed class LlvmArrayType : LlvmType
{
    /// <summary>
    /// 元素类型
    /// </summary>
    public LlvmType Element { get; init; } = LlvmVoidType.Instance;

    /// <summary>
    /// 元素数量
    /// </summary>
    public int Size { get; init; }

    public override string ToIrString() => $"[{Size} x {Element.ToIrString()}]";
    public override bool Equals(object? obj) => obj is LlvmArrayType other && Size == other.Size && Element.Equals(other.Element);
    public override int GetHashCode() => HashCode.Combine(typeof(LlvmArrayType), Element, Size);
    public override string ToString() => ToIrString();
}

/// <summary>
/// 结构体类型
/// </summary>
public sealed class LlvmStructType : LlvmType
{
    /// <summary>
    /// 字段类型列表
    /// </summary>
    public List<LlvmType> Fields { get; init; } = [];

    /// <summary>
    /// 是否是字面量结构体 (匿名)
    /// </summary>
    public bool IsLiteral { get; init; } = true;

    /// <summary>
    /// 结构体名称 (非字面量时使用)
    /// </summary>
    public string? Name { get; init; }

    public override string ToIrString()
    {
        if (!IsLiteral && !string.IsNullOrEmpty(Name))
        {
            return $"%struct.{Name}";
        }

        var fields = string.Join(", ", Fields.Select(f => f.ToIrString()));
        return $"{{{fields}}}";
    }

    public override bool Equals(object? obj)
    {
        if (obj is not LlvmStructType other) return false;
        if (IsLiteral != other.IsLiteral) return false;
        if (!IsLiteral && !string.Equals(Name, other.Name, StringComparison.Ordinal)) return false;
        if (Fields.Count != other.Fields.Count) return false;
        for (var i = 0; i < Fields.Count; i++)
        {
            if (!Fields[i].Equals(other.Fields[i])) return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(typeof(LlvmStructType));
        hash.Add(IsLiteral);
        if (!IsLiteral && Name != null) hash.Add(Name);
        foreach (var f in Fields) hash.Add(f);
        return hash.ToHashCode();
    }

    public override string ToString() => ToIrString();
}

/// <summary>
/// 函数类型
/// </summary>
public sealed class LlvmFunctionType : LlvmType
{
    /// <summary>
    /// 返回类型
    /// </summary>
    public LlvmType ReturnType { get; init; } = LlvmVoidType.Instance;

    /// <summary>
    /// 参数类型列表
    /// </summary>
    public List<LlvmType> ParameterTypes { get; init; } = [];

    /// <summary>
    /// 是否可变参数
    /// </summary>
    public bool IsVarArg { get; init; }

    public override string ToIrString()
    {
        var paramStr = string.Join(", ", ParameterTypes.Select(p => p.ToIrString()));
        if (IsVarArg)
        {
            paramStr = string.IsNullOrEmpty(paramStr) ? "..." : $"{paramStr}, ...";
        }
        return $"{ReturnType.ToIrString()} ({paramStr})";
    }

    public override bool Equals(object? obj)
    {
        if (obj is not LlvmFunctionType other) return false;
        if (IsVarArg != other.IsVarArg) return false;
        if (!ReturnType.Equals(other.ReturnType)) return false;
        if (ParameterTypes.Count != other.ParameterTypes.Count) return false;
        for (var i = 0; i < ParameterTypes.Count; i++)
        {
            if (!ParameterTypes[i].Equals(other.ParameterTypes[i])) return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(typeof(LlvmFunctionType));
        hash.Add(ReturnType);
        hash.Add(IsVarArg);
        foreach (var p in ParameterTypes) hash.Add(p);
        return hash.ToHashCode();
    }

    public override string ToString() => ToIrString();
}

/// <summary>
/// 标签类型 (用于基本块引用)
/// </summary>
public sealed class LlvmLabelType : LlvmType
{
    public static readonly LlvmLabelType Instance = new();

    public override string ToIrString() => "label";
    public override bool Equals(object? obj) => obj is LlvmLabelType;
    public override int GetHashCode() => HashCode.Combine(typeof(LlvmLabelType));
    public override string ToString() => ToIrString();
}

/// <summary>
/// 元数据类型
/// </summary>
public sealed class LlvmMetadataType : LlvmType
{
    public static readonly LlvmMetadataType Instance = new();

    public override string ToIrString() => "metadata";
    public override bool Equals(object? obj) => obj is LlvmMetadataType;
    public override int GetHashCode() => HashCode.Combine(typeof(LlvmMetadataType));
    public override string ToString() => ToIrString();
}

/// <summary>
/// 向量类型 (SIMD)
/// </summary>
public sealed class LlvmVectorType : LlvmType
{
    /// <summary>
    /// 元素类型
    /// </summary>
    public LlvmType ElementType { get; init; } = LlvmVoidType.Instance;

    /// <summary>
    /// 元素数量
    /// </summary>
    public int Size { get; init; }

    public override string ToIrString() => $"<{Size} x {ElementType.ToIrString()}>";
    public override bool Equals(object? obj) => obj is LlvmVectorType other && Size == other.Size && ElementType.Equals(other.ElementType);
    public override int GetHashCode() => HashCode.Combine(typeof(LlvmVectorType), ElementType, Size);
    public override string ToString() => ToIrString();
}
