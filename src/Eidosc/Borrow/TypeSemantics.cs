using Eidosc.Symbols;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Borrow;

/// <summary>
/// 共享的类型语义工具方法，替代分散在 ReuseAnalysis、StackPromotionAnalysis、
/// UnifiedStackPromotionAnalyzer 中的重复实现。
/// </summary>
public static class TypeSemantics
{
    /// <summary>
    /// 判断 TypeId 是否为托管 RC 类型（需要堆分配 + 引用计数）。
    /// 非托管基础类型：Int, Float, Bool, Char, Unit。
    /// 其他类型（String, ADT, 函数等）均为托管类型。
    /// </summary>
    public static bool IsManagedType(TypeId typeId)
    {
        if (!typeId.IsValid)
            return false;

        var id = typeId.Value;
        return id != BaseTypes.IntId &&
               id != BaseTypes.FloatId &&
               id != BaseTypes.BoolId &&
               id != BaseTypes.CharId &&
               id != BaseTypes.UnitId;
    }

    /// <summary>
    /// 判断 MirFunctionRef 是否为 ADT 构造器调用。
    /// 优先使用结构化 SymbolKind 判断，SymbolId 无效时 fallback 到名称启发式。
    /// </summary>
    public static bool IsAdtConstructorCall(MirFunctionRef func)
    {
        return func.SymbolId.IsValid switch
        {
            true when func.SymbolKind == SymbolKind.Constructor => true,
            // Fallback: SymbolId 无效时使用名称启发式
            false => IsLikelyAdtConstructorByName(func.Name),
            _ => false
        };
    }

    /// <summary>
    /// 判断 MirCall 是否为堆分配的构造器调用。
    /// </summary>
    public static bool IsHeapAllocatingConstructorCall(
        MirInstruction instr,
        out TypeId targetTypeId)
    {
        targetTypeId = default;

        if (instr is not MirCall call)
            return false;

        if (call.Function is not MirFunctionRef funcRef)
            return false;

        if (!IsAdtConstructorCall(funcRef))
            return false;

        if (call.Target is not MirPlace { Kind: PlaceKind.Local, TypeId: var tid } ||
            !tid.IsValid)
        {
            return false;
        }

        targetTypeId = tid;
        return true;
    }

    /// <summary>
    /// 名称启发式判断是否为 ADT 构造器。
    /// 仅在 SymbolId 无效时作为 fallback 使用。
    /// 适用于后端（LLVM 层）构造的合成 MirFunctionRef。
    /// </summary>
    public static bool IsLikelyAdtConstructorByName(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return false;

        // Module-qualified names (containing WellKnownStrings.Separators.Path, i.e. "::") are regular function calls.
        if (functionName.Contains(WellKnownStrings.Separators.Path, StringComparison.Ordinal))
            return false;

        // ADT constructor names are simple identifiers starting with uppercase
        return char.IsUpper(functionName[0]);
    }

    /// <summary>
    /// 判断 mangled 符号名是否可能为 ADT 构造器（LLVM 层 fallback）。
    /// 仅在后端 TryCreateAdtConstructorStub 等场景使用，
    /// 此时只有 mangled 名称，没有 MirFunctionRef 可用。
    ///
    /// 注意：此方法故意宽松（不检查 "__"），因为 LLVM 后端需要为
    /// 某些无法解析的函数生成合成 stub 以避免链接错误。
    /// 精确的 ADT 检测应使用 IsAdtConstructorCall(MirFunctionRef)。
    /// </summary>
    public static bool IsLikelyAdtConstructorByMangledName(string symbolName)
    {
        const string prefix = WellKnownStrings.Mangling.Prefix;
        if (!symbolName.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var rawName = symbolName[prefix.Length..];
        return rawName.Length > 0 && char.IsUpper(rawName[0]);
    }
}
