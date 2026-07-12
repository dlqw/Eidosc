using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.Semantic;

/// <summary>
/// C 结构体字段布局信息
/// </summary>
public sealed record CStructFieldInfo
{
    /// <summary>
    /// 字段名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 字段类型的 TypeId
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// 字段在结构体中的字节偏移量
    /// </summary>
    public int Offset { get; init; }

    /// <summary>
    /// 字段的字节大小
    /// </summary>
    public int Size { get; init; }

    /// <summary>
    /// 字段的对齐要求
    /// </summary>
    public int Alignment { get; init; }
}

/// <summary>
/// C 结构体完整布局信息
/// </summary>
public sealed record CStructLayout
{
    /// <summary>
    /// 结构体名称
    /// </summary>
    public string StructName { get; init; } = "";

    /// <summary>
    /// 字段布局列表（按声明顺序）
    /// </summary>
    public List<CStructFieldInfo> Fields { get; init; } = [];

    /// <summary>
    /// 结构体总字节大小（含末尾填充）
    /// </summary>
    public int TotalSize { get; init; }

    /// <summary>
    /// 结构体的对齐要求（等于最大字段对齐）
    /// </summary>
    public int Alignment { get; init; }

    /// <summary>
    /// 按字段名查找布局信息
    /// </summary>
    public CStructFieldInfo? FindField(string fieldName)
    {
        foreach (var field in Fields)
        {
            if (field.Name == fieldName)
            {
                return field;
            }
        }
        return null;
    }
}

/// <summary>
/// C 结构体布局计算器。
/// 遵循标准 C ABI 规则：自然对齐，字段间自动填充。
/// </summary>
public static class CStructLayoutComputer
{
    /// <summary>
    /// 计算 C 结构体字段布局。
    /// </summary>
    /// <param name="structName">结构体名称</param>
    /// <param name="fields">字段列表（名称 + TypeId），按声明顺序</param>
    /// <param name="getTypeInfo">根据 TypeId 返回 (size, alignment) 的委托</param>
    /// <returns>完整的结构体布局信息</returns>
    public static CStructLayout Compute(
        string structName,
        IReadOnlyList<(string Name, TypeId TypeId)> fields,
        Func<TypeId, (int Size, int Alignment)> getTypeInfo)
    {
        var fieldLayouts = new List<CStructFieldInfo>(fields.Count);
        var currentOffset = 0;
        var structAlignment = 1;

        for (var i = 0; i < fields.Count; i++)
        {
            var (name, typeId) = fields[i];
            var (size, alignment) = getTypeInfo(typeId);

            // 更新结构体对齐为最大字段对齐
            if (alignment > structAlignment)
            {
                structAlignment = alignment;
            }

            // 对齐当前偏移量
            currentOffset = AlignUp(currentOffset, alignment);

            fieldLayouts.Add(new CStructFieldInfo
            {
                Name = name,
                TypeId = typeId,
                Offset = currentOffset,
                Size = size,
                Alignment = alignment
            });

            currentOffset += size;
        }

        // 结构体总大小对齐到结构体对齐要求
        var totalSize = AlignUp(currentOffset, structAlignment);

        return new CStructLayout
        {
            StructName = structName,
            Fields = fieldLayouts,
            TotalSize = totalSize,
            Alignment = structAlignment
        };
    }

    /// <summary>
    /// 获取 FFI 安全类型的 C ABI 大小和对齐要求。
    /// 返回 null 表示类型不是 FFI 安全的。
    /// </summary>
    public static (int Size, int Alignment)? GetFfiTypeInfo(TypeId typeId)
    {
        return typeId.Value switch
        {
            BaseTypes.IntId => (8, 8),       // i64
            BaseTypes.FloatId => (8, 8),     // f64
            BaseTypes.BoolId => (1, 1),      // i1 (C _Bool 通常 1 字节对齐)
            BaseTypes.UnitId => (0, 1),      // void — 零大小
            BaseTypes.RawPtrId => (8, 8),    // ptr (64-bit)
            BaseTypes.CfnId => (8, 8),       // 函数指针 = ptr
            _ => null
        };
    }

    /// <summary>
    /// 获取类型名称对应的 C ABI 大小和对齐。
    /// 用于泛型类型如 Int32, Float32, Ptr[T] 等。
    /// </summary>
    public static (int Size, int Alignment)? GetTypeInfoByName(string typeName)
    {
        return typeName switch
        {
            WellKnownStrings.BuiltinTypes.Int or WellKnownStrings.BuiltinTypes.Int64 => (8, 8),
            WellKnownStrings.BuiltinTypes.Int32 => (4, 4),
            WellKnownStrings.BuiltinTypes.Int16 => (2, 2),
            WellKnownStrings.BuiltinTypes.Int8 => (1, 1),
            WellKnownStrings.BuiltinTypes.Float or WellKnownStrings.BuiltinTypes.Float64 => (8, 8),
            WellKnownStrings.BuiltinTypes.Float32 => (4, 4),
            WellKnownStrings.BuiltinTypes.Float16 => (2, 2),
            WellKnownStrings.BuiltinTypes.Bool => (1, 1),
            WellKnownStrings.BuiltinTypes.Unit => (0, 1),
            WellKnownStrings.BuiltinTypes.RawPtr or WellKnownStrings.BuiltinTypes.Ptr or WellKnownStrings.BuiltinTypes.Cfn => (8, 8),
            _ => null
        };
    }

    private static int AlignUp(int offset, int alignment)
    {
        return (offset + alignment - 1) & ~(alignment - 1);
    }
}
