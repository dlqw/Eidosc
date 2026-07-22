using Eidosc.Ast.Declarations;
using Eidosc.Symbols;

namespace Eidosc.Types;

internal static partial class ComptimeEvaluator
{
    private static bool TryResolveComptimeTraitDispatch(
        SymbolId calleeSymbolId,
        IReadOnlyList<ComptimeValue> arguments,
        ComptimeEvaluationContext context,
        out FuncDef function)
    {
        function = null!;
        var symbolTable = context.Meta?.SymbolTable ?? context.Build?.SymbolTable;
        if (symbolTable?.GetSymbol<FuncSymbol>(calleeSymbolId) is not { OwnerTrait: { } ownerTrait } traitMethod ||
            !ownerTrait.IsValid)
        {
            return false;
        }

        foreach (var dispatchIndex in GetTraitDispatchArgumentIndices(traitMethod, arguments.Count))
        {
            if (arguments[dispatchIndex].StaticType is not TyCon dispatchType)
            {
                continue;
            }

            foreach (var dispatchSymbol in GetDispatchTypeAncestors(symbolTable, dispatchType))
            {
                var dispatchTypeId = dispatchSymbol.TypeId.IsValid
                    ? dispatchSymbol.TypeId
                    : dispatchType.Id;
                var dispatchTypeRef = dispatchType with
                {
                    Symbol = dispatchSymbol.Id,
                    Id = dispatchTypeId,
                    Name = dispatchSymbol.Name
                };
                var implementingKey = ImplLookupCanonicalizer.BuildTypeRefKey(symbolTable, dispatchTypeRef);
                var implementation = symbolTable.LookupImplForTraitByKeys(
                    dispatchTypeId,
                    ownerTrait,
                    implementingKey,
                    traitTypeArgKeys: null);
                if (implementation == null ||
                    !TryGetImplementationMethod(implementation, traitMethod, symbolTable, out var methodId) ||
                    !context.Functions.TryGetValue(methodId, out var candidate) ||
                    candidate.Body.Count == 0)
                {
                    continue;
                }

                function = candidate;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<int> GetTraitDispatchArgumentIndices(FuncSymbol method, int argumentCount)
    {
        var explicitIndices = method.TraitSelfParameterIndices
            .Where(index => index >= 0 && index < argumentCount)
            .Distinct()
            .ToArray();
        if (explicitIndices.Length > 0)
        {
            return explicitIndices;
        }

        return argumentCount > 0 && method.TraitSelfPosition is SelfPosition.InParameter or SelfPosition.Both or SelfPosition.Unknown
            ? [0]
            : [];
    }

    private static IEnumerable<AdtSymbol> GetDispatchTypeAncestors(SymbolTable symbolTable, TyCon dispatchType)
    {
        var symbolId = dispatchType.Symbol;
        if (!symbolId.IsValid && dispatchType.Id.IsValid)
        {
            symbolId = symbolTable.GetSymbolByTypeId(dispatchType.Id)?.Id ?? SymbolId.None;
        }

        foreach (var ancestor in symbolTable.GetClosedCaseAncestors(symbolId))
        {
            if (symbolTable.GetSymbol<AdtSymbol>(ancestor) is { } type)
            {
                yield return type;
            }
        }
    }

    private static bool TryGetImplementationMethod(
        ImplSymbol implementation,
        FuncSymbol traitMethod,
        SymbolTable symbolTable,
        out SymbolId methodId)
    {
        if (implementation.TraitMethodImplementations.TryGetValue(traitMethod.Id, out methodId) && methodId.IsValid)
        {
            return true;
        }

        methodId = implementation.Methods.FirstOrDefault(candidate =>
            symbolTable.GetSymbol<FuncSymbol>(candidate) is { } method &&
            string.Equals(method.Name, traitMethod.Name, StringComparison.Ordinal));
        return methodId.IsValid;
    }
}
