namespace Eidosc;

/// <summary>
/// ADT 构造器的稳定 type-id 计算工具。
/// 用于在 MIR 模式匹配与 LLVM 构造器桩生成间保持一致。
/// </summary>
public static class AdtConstructorTypeId
{
    private const string MangledPrefix = WellKnownStrings.Mangling.Prefix;

    /// <summary>
    /// FNV-1a 偏移基础值。
    /// </summary>
    internal const uint FnvOffset = 2166136261;

    /// <summary>
    /// FNV-1a 质数。
    /// </summary>
    internal const uint FnvPrime = 16777619;

    /// <summary>
    /// 由原始构造器名（如 TkInt / TokCons）计算稳定 type-id。
    /// </summary>
    public static int Compute(string constructorName)
    {
        if (string.IsNullOrWhiteSpace(constructorName))
        {
            return 1;
        }

        var hash = FnvOffset;
        foreach (var ch in constructorName)
        {
            hash ^= ch;
            hash *= FnvPrime;
        }

        var value = (int)(hash & 0x7fffffff);
        return value == 0 ? 1 : value;
    }

    public static int Compute(SymbolId constructorSymbolId, string constructorName)
    {
        return constructorSymbolId.IsValid
            ? Compute($"sym:{constructorSymbolId.Value}")
            : Compute(constructorName);
    }

    public static int Compute(FunctionId? functionId, SymbolId constructorSymbolId, string constructorName)
    {
        if (Mir.MirFunctionIdentity.TryGetStableKey(functionId, out var functionIdentityKey))
        {
            return Compute(functionIdentityKey);
        }

        if (constructorSymbolId.IsValid)
        {
            return Compute(constructorSymbolId, constructorName);
        }

        if (!string.IsNullOrWhiteSpace(functionId?.ModuleIdentityKey) &&
            !string.IsNullOrWhiteSpace(functionId?.Name))
        {
            return Compute($"module-id:{functionId.ModuleIdentityKey}::{functionId.Name}");
        }

        if (!string.IsNullOrWhiteSpace(functionId?.QualifiedName))
        {
            return Compute($"qualified:{functionId.QualifiedName}");
        }

        return Compute(constructorName);
    }

    /// <summary>
    /// 由 mangled 符号（如 eidos_TkInt）计算稳定 type-id。
    /// </summary>
    public static int ComputeFromSymbol(string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            return 1;
        }

        var rawName = symbolName.StartsWith(MangledPrefix, StringComparison.Ordinal)
            ? symbolName[MangledPrefix.Length..]
            : symbolName;

        return Compute(rawName);
    }
}

