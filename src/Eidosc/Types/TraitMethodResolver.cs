using Eidosc.Symbols;
using Eidosc.Semantic;

namespace Eidosc.Types;

/// <summary>
/// Trait 方法解析结果
/// </summary>
public sealed class MethodResolutionResult
{
    /// <summary>
    /// 方法符号 ID
    /// </summary>
    public required SymbolId MethodId { get; init; }

    /// <summary>
    /// 方法名称
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// 所属 Trait（如果是 Trait 方法）
    /// </summary>
    public SymbolId? RequiredTrait { get; init; }

    /// <summary>
    /// 接收者类型
    /// </summary>
    public required Type ReceiverType { get; init; }

    /// <summary>
    /// 方法类型
    /// </summary>
    public Type? MethodType { get; init; }
}

/// <summary>
/// Trait 方法解析器 - 查找类型上的方法
/// </summary>
public sealed class TraitMethodResolver(SymbolTable symbolTable)
{
    /// <summary>
    /// 查找类型上的方法
    /// </summary>
    /// <param name="methodName">方法名称</param>
    /// <param name="receiverType">接收者类型</param>
    /// <returns>解析结果，如果找不到则返回 null</returns>
    public MethodResolutionResult? ResolveMethod(string methodName, Type receiverType)
    {
        // 1. 查找类型的固有方法（暂不支持）
        // 2. 查找 Trait 方法
        return LookupTraitMethod(methodName, receiverType);
    }

    /// <summary>
    /// 在 Trait 中查找方法
    /// </summary>
    private MethodResolutionResult? LookupTraitMethod(string methodName, Type receiverType)
    {
        // 遍历所有 Trait，查找匹配的方法
        foreach (var symbol in symbolTable.Symbols.Values)
        {
            if (symbol is TraitSymbol trait)
            {
                var method = FindMethodInTrait(trait, methodName);
                if (method != null)
                {
                    return new MethodResolutionResult
                    {
                        MethodId = method.Value,
                        MethodName = methodName,
                        RequiredTrait = trait.Id,
                        ReceiverType = receiverType
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 在 Trait 中查找指定名称的方法
    /// </summary>
    private SymbolId? FindMethodInTrait(TraitSymbol trait, string methodName)
    {
        foreach (var methodId in trait.Methods)
        {
            var method = symbolTable.GetSymbol(methodId);
            if (method != null && method.Name == methodName)
            {
                return methodId;
            }
        }

        return null;
    }

    /// <summary>
    /// 检查类型是否实现了包含指定方法的 Trait
    /// </summary>
    public bool HasTraitWithMethod(Type type, string methodName)
    {
        // 对于内置类型，检查是否实现了相关 Trait
        if (type is TyCon con && BuiltinTraits.IsBuiltinType(con))
        {
            // 内置类型通常有默认实现
            return true;
        }

        return false;
    }

    /// <summary>
    /// 获取 Trait 方法的约束
    /// </summary>
    public TraitConstraint? GetMethodConstraint(MethodResolutionResult result)
    {
        if (result.RequiredTrait == null)
            return null;

        return new TraitConstraint
        {
            Type = result.ReceiverType,
            Trait = result.RequiredTrait.Value,
            TraitName = GetTraitName(result.RequiredTrait.Value)
        };
    }

    private string GetTraitName(SymbolId traitId)
    {
        var symbol = symbolTable.GetSymbol(traitId);
        return symbol?.Name ?? "<unknown>";
    }
}
