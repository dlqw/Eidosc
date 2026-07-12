using Eidosc.Semantic;
namespace Eidosc.Symbols;

/// <summary>
/// 导入作用域 - 管理当前模块的导入
/// </summary>
public sealed class ImportScope
{
    /// <summary>
    /// 导入的符号 (本地名 -> 符号 ID)
    /// </summary>
    private readonly Dictionary<string, SymbolId> _importedSymbols = new();

    /// <summary>
    /// 导入的模块 (别名 -> 模块 ID)
    /// </summary>
    private readonly Dictionary<string, SymbolId> _importedModules = new();

    /// <summary>
    /// 符号详情 (用于跟踪解析类型)
    /// </summary>
    private readonly Dictionary<string, ImportedSymbol> _importDetails = new();

    /// <summary>
    /// 同名导入详情（用于歧义检测）
    /// </summary>
    private readonly Dictionary<string, List<ImportedSymbol>> _importDetailsByName = new();
    private readonly Dictionary<string, ImportedSymbolLookup> _lookupCache = new();

    private sealed record ImportedSymbolLookup(
        SymbolId SymbolId,
        bool IsAmbiguous,
        IReadOnlyList<ImportedSymbol> Candidates,
        bool IsFound);

    /// <summary>
    /// 添加导入的符号
    /// </summary>
    public void AddImport(ImportedSymbol symbol)
    {
        var hasExplicitImport = _importDetails.TryGetValue(symbol.Name, out var currentDetail) &&
                                !currentDetail.IsImplicitModuleMember;
        var shouldUpdatePrimary = !symbol.IsImplicitModuleMember || !hasExplicitImport;
        if (shouldUpdatePrimary)
        {
            _importedSymbols[symbol.Name] = symbol.SymbolId;
            _importDetails[symbol.Name] = symbol;
        }

        if (!_importDetailsByName.TryGetValue(symbol.Name, out var details))
        {
            details = [];
            _importDetailsByName[symbol.Name] = details;
        }

        if (!details.Any(existing =>
                existing.SymbolId.Equals(symbol.SymbolId) &&
                existing.Kind == symbol.Kind &&
                string.Equals(existing.Name, symbol.Name, StringComparison.Ordinal)))
        {
            details.Add(symbol);
            _lookupCache.Remove(symbol.Name);
        }
    }

    /// <summary>
    /// 添加导入的模块
    /// </summary>
    public void AddModuleImport(string alias, SymbolId moduleId)
    {
        _importedModules[alias] = moduleId;
    }

    /// <summary>
    /// 查找导入的符号
    /// </summary>
    public SymbolId? LookupImportedSymbol(string name)
    {
        return _importedSymbols.TryGetValue(name, out var id) ? id : null;
    }

    public bool TryLookupImportedSymbol(
        string name,
        out SymbolId symbolId,
        out bool isAmbiguous,
        out IReadOnlyList<ImportedSymbol> candidates)
    {
        symbolId = SymbolId.None;
        isAmbiguous = false;
        candidates = [];

        if (_lookupCache.TryGetValue(name, out var cached))
        {
            symbolId = cached.SymbolId;
            isAmbiguous = cached.IsAmbiguous;
            candidates = cached.Candidates;
            return cached.IsFound;
        }

        if (!_importDetailsByName.TryGetValue(name, out var details) || details.Count == 0)
        {
            _lookupCache[name] = new ImportedSymbolLookup(SymbolId.None, IsAmbiguous: false, [], IsFound: false);
            return false;
        }

        var effectiveDetails = SelectEffectiveDetails(details);
        var distinctCandidates = CollectDistinctCandidates(effectiveDetails);

        if (distinctCandidates.Count > 1)
        {
            isAmbiguous = true;
            candidates = distinctCandidates;
            _lookupCache[name] = new ImportedSymbolLookup(SymbolId.None, IsAmbiguous: true, distinctCandidates, IsFound: false);
            return false;
        }

        symbolId = distinctCandidates[0].SymbolId;
        var isFound = symbolId.IsValid;
        _lookupCache[name] = new ImportedSymbolLookup(symbolId, IsAmbiguous: false, distinctCandidates, isFound);
        return isFound;
    }

    private static IReadOnlyList<ImportedSymbol> SelectEffectiveDetails(List<ImportedSymbol> details)
    {
        var explicitCount = 0;
        for (var i = 0; i < details.Count; i++)
        {
            if (!details[i].IsImplicitModuleMember)
            {
                explicitCount++;
            }
        }

        if (explicitCount > 0)
        {
            var explicitDetails = new List<ImportedSymbol>(explicitCount);
            for (var i = 0; i < details.Count; i++)
            {
                if (!details[i].IsImplicitModuleMember)
                {
                    explicitDetails.Add(details[i]);
                }
            }

            return explicitDetails;
        }

        var traitMethodCount = 0;
        for (var i = 0; i < details.Count; i++)
        {
            if (details[i].IsTraitMethod)
            {
                traitMethodCount++;
            }
        }

        if (traitMethodCount == 0)
        {
            return details;
        }

        var traitMethodDetails = new List<ImportedSymbol>(traitMethodCount);
        for (var i = 0; i < details.Count; i++)
        {
            if (details[i].IsTraitMethod)
            {
                traitMethodDetails.Add(details[i]);
            }
        }

        return traitMethodDetails;
    }

    private static List<ImportedSymbol> CollectDistinctCandidates(IReadOnlyList<ImportedSymbol> details)
    {
        var result = new List<ImportedSymbol>(details.Count);
        for (var i = 0; i < details.Count; i++)
        {
            var candidate = details[i];
            var exists = false;
            for (var j = 0; j < result.Count; j++)
            {
                var existing = result[j];
                if (existing.SymbolId == candidate.SymbolId &&
                    existing.Kind == candidate.Kind &&
                    string.Equals(existing.Name, candidate.Name, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>
    /// 查找导入的模块
    /// </summary>
    public SymbolId? LookupImportedModule(string name)
    {
        return _importedModules.TryGetValue(name, out var id) ? id : null;
    }

    /// <summary>
    /// 获取导入符号的详情
    /// </summary>
    public ImportedSymbol? GetImportDetail(string name)
    {
        return _importDetails.TryGetValue(name, out var detail) ? detail : null;
    }

    /// <summary>
    /// 获取同名导入详情（用于歧义检测）
    /// </summary>
    public List<ImportedSymbol> GetImportDetails(string name)
    {
        return _importDetailsByName.TryGetValue(name, out var details)
            ? details
            : [];
    }

    /// <summary>
    /// 获取所有导入的符号
    /// </summary>
    public IReadOnlyDictionary<string, SymbolId> GetAllImports()
    {
        return _importedSymbols;
    }

    /// <summary>
    /// 获取所有导入的模块
    /// </summary>
    public IReadOnlyDictionary<string, SymbolId> GetAllModuleImports()
    {
        return _importedModules;
    }
}
