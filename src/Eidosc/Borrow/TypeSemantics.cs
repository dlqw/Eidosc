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
    /// 只使用结构化 SymbolKind / FunctionId.Kind 判断。
    /// </summary>
    public static bool IsAdtConstructorCall(MirFunctionRef func)
    {
        if (func.SymbolKind == SymbolKind.Constructor ||
            func.FunctionId.Kind == SymbolKind.Constructor)
        {
            return true;
        }

        return false;
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

}
