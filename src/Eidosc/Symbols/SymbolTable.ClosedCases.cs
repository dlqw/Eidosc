namespace Eidosc.Symbols;

/// <summary>
/// Authoritative symbol-level queries for lexical closed case hierarchies.
/// </summary>
public sealed partial class SymbolTable
{
    public IReadOnlyList<SymbolId> GetClosedCaseAncestors(SymbolId type, bool includeSelf = true)
    {
        var result = new List<SymbolId>();
        var current = includeSelf
            ? type
            : GetSymbol<AdtSymbol>(type)?.ParentAdt ?? SymbolId.None;
        var visited = new HashSet<SymbolId>();
        while (current.IsValid && visited.Add(current) && GetSymbol<AdtSymbol>(current) is { } symbol)
        {
            result.Add(current);
            current = symbol.ParentAdt;
        }

        return result;
    }

    public IReadOnlyList<SymbolId> GetClosedCaseLeafCases(SymbolId owner)
    {
        var result = new List<SymbolId>();
        var visiting = new HashSet<SymbolId>();
        Collect(owner);
        return result;

        void Collect(SymbolId current)
        {
            if (!visiting.Add(current) || GetSymbol<AdtSymbol>(current) is not { } symbol)
            {
                return;
            }

            foreach (var caseId in symbol.DirectCases)
            {
                if (GetSymbol<AdtSymbol>(caseId) is { DirectCases.Count: > 0 })
                {
                    Collect(caseId);
                }
                else if (GetSymbol<AdtSymbol>(caseId) != null)
                {
                    result.Add(caseId);
                }
            }

            visiting.Remove(current);
        }
    }

    public IReadOnlyList<SymbolId> GetClosedCaseEffectiveGenericParameterIds(SymbolId type)
    {
        var path = GetClosedCaseAncestors(type).Reverse();
        return path
            .SelectMany(id => GetSymbol<AdtSymbol>(id)?.TypeParams ?? [])
            .ToArray();
    }

    public IReadOnlyList<SymbolId> GetClosedCaseEffectiveFieldIds(SymbolId type)
    {
        var path = GetClosedCaseAncestors(type).Reverse();
        return path
            .SelectMany(id => GetSymbol<AdtSymbol>(id)?.Fields ?? [])
            .ToArray();
    }
}
