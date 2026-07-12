using Eidosc.Symbols;
using Eidosc.Semantic;

namespace Eidosc.Mir;

internal static class ConstructorRuntimeTypeId
{
    public static int Compute(SymbolTable? symbolTable, SymbolId constructorSymbolId, string constructorName)
    {
        if (TryComputeModuleAware(symbolTable, constructorSymbolId, constructorName, out var runtimeTypeId))
        {
            return runtimeTypeId;
        }

        return AdtConstructorTypeId.Compute(constructorName);
    }

    private static bool TryComputeModuleAware(
        SymbolTable? symbolTable,
        SymbolId constructorSymbolId,
        string constructorName,
        out int runtimeTypeId)
    {
        if (TryGetStableIdentityKey(symbolTable, constructorSymbolId, constructorName, out var stableIdentityKey))
        {
            runtimeTypeId = AdtConstructorTypeId.Compute(stableIdentityKey);
            return true;
        }

        runtimeTypeId = 0;
        return false;
    }

    internal static bool TryGetStableIdentityKey(
        SymbolTable? symbolTable,
        SymbolId constructorSymbolId,
        string constructorName,
        out string stableIdentityKey)
    {
        stableIdentityKey = "";
        if (symbolTable == null || !constructorSymbolId.IsValid)
        {
            return false;
        }

        if (symbolTable.GetSymbol(constructorSymbolId) is not CtorSymbol { OwnerAdt.IsValid: true } ctor ||
            symbolTable.GetSymbol<AdtSymbol>(ctor.OwnerAdt) is not { } ownerAdt)
        {
            return false;
        }

        var module = symbolTable.Modules.ModulePaths.Values
            .Distinct()
            .Select(symbolTable.Modules.GetModule)
            .Where(candidate => candidate?.Members.Contains(ctor.OwnerAdt) == true)
            .OrderBy(static candidate => candidate!.Identity.ToIdentityKey(), StringComparer.Ordinal)
            .FirstOrDefault();
        if (module == null)
        {
            return false;
        }

        var stableConstructorName = string.IsNullOrWhiteSpace(ctor.Name)
            ? constructorName
            : ctor.Name;
        stableIdentityKey =
            $"module-ctor:{module.Identity.ToIdentityKey()}::{ownerAdt.Name}::{stableConstructorName}";

        return true;
    }
}
