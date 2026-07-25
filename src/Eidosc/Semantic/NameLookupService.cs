using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Utils;

namespace Eidosc.Semantic;

[Flags]
internal enum LookupKind
{
    Value = 1,
    Type = 2,
    Constructor = 4,
    Module = 8,
    Effect = 16,
    Proof = 32
}

internal sealed record LookupContext(
    SymbolId CurrentModule,
    ImportScope? ImportScope,
    Func<IReadOnlyList<string>, PathResolutionResult>? ResolvePathWithImports = null,
    Func<string?, IReadOnlyList<string>, PathResolutionResult>? ResolvePackageQualifiedPath = null);

internal sealed record LookupCandidate(string Name, SymbolId SymbolId, ResolutionKind Kind);

internal sealed record LookupResult(
    bool IsSuccess,
    SymbolId SymbolId,
    ResolutionKind Kind,
    bool IsConstructor,
    string? ErrorMessage,
    IReadOnlyList<LookupCandidate> Candidates)
{
    public static LookupResult Found(SymbolId symbolId, ResolutionKind kind, bool isConstructor = false)
        => new(true, symbolId, kind, isConstructor, null, []);

    public static LookupResult NotFound()
        => new(false, SymbolId.None, ResolutionKind.Value, false, null, []);

    public static LookupResult Failure(string errorMessage, IReadOnlyList<LookupCandidate>? candidates = null)
        => new(false, SymbolId.None, ResolutionKind.Value, false, errorMessage, candidates ?? []);
}

internal sealed class NameLookupService
{
    private readonly SymbolTable _symbolTable;
    private readonly PathResolver _pathResolver;

    public NameLookupService(SymbolTable symbolTable, PathResolver pathResolver)
    {
        _symbolTable = symbolTable;
        _pathResolver = pathResolver;
    }

    public LookupResult Lookup(string name, LookupKind kind, LookupContext context)
    {
        if (kind.HasFlag(LookupKind.Value))
        {
            return LookupValue(name, kind.HasFlag(LookupKind.Constructor), context);
        }

        var localCandidates = new List<LookupCandidate>();
        AddLexicalCandidate(localCandidates, _symbolTable.LookupType(name), ResolutionKind.Type);
        AddLexicalCandidate(localCandidates, _symbolTable.LookupEffect(name), ResolutionKind.Effect);
        AddLexicalCandidate(localCandidates, _symbolTable.LookupConstructor(name), ResolutionKind.Constructor);
        AddLexicalCandidate(localCandidates, _symbolTable.LookupModule(name), ResolutionKind.Module);

        if (context.CurrentModule.IsValid)
        {
            foreach (var binding in _symbolTable.Modules.GetAccessibleBindingsByName(
                         context.CurrentModule,
                         name,
                         context.CurrentModule))
            {
                if (MatchesRequestedKind(binding.Kind, kind))
                {
                    localCandidates.Add(new LookupCandidate(name, binding.SymbolId, binding.Kind));
                }
            }
        }

        var matching = localCandidates
            .Where(candidate => MatchesRequestedKind(candidate.Kind, kind))
            .DistinctBy(candidate => candidate.SymbolId)
            .ToArray();
        if (matching.Length == 0 && context.ImportScope != null)
        {
            matching = context.ImportScope.GetEffectiveImportDetails(name)
                .Where(imported => imported.SymbolId.IsValid && MatchesRequestedKind(imported.Kind, kind))
                .Select(imported => new LookupCandidate(name, imported.SymbolId, imported.Kind))
                .DistinctBy(candidate => candidate.SymbolId)
                .ToArray();
        }

        if (matching.Length == 1)
        {
            var selected = matching[0];
            return LookupResult.Found(
                selected.SymbolId,
                selected.Kind,
                selected.Kind == ResolutionKind.Constructor);
        }

        if (matching.Length > 1)
        {
            return LookupResult.Failure(
                $"Identifier '{name}' is ambiguous across the requested semantic namespaces.",
                matching);
        }

        return LookupResult.NotFound();

        void AddCandidate(
            List<LookupCandidate> candidates,
            SymbolId? symbolId,
            ResolutionKind resolutionKind)
        {
            if (symbolId is { IsValid: true } id)
            {
                candidates.Add(new LookupCandidate(name, id, resolutionKind));
            }
        }

        void AddLexicalCandidate(
            List<LookupCandidate> candidates,
            SymbolId? symbolId,
            ResolutionKind resolutionKind)
        {
            if (symbolId is not { IsValid: true } id)
            {
                return;
            }

            if (_symbolTable.Modules.TryGetOwningModule(id, out var owner) &&
                (!context.CurrentModule.IsValid || owner.Id != context.CurrentModule))
            {
                return;
            }

            AddCandidate(candidates, id, resolutionKind);
        }
    }

    private static bool MatchesRequestedKind(ResolutionKind resolutionKind, LookupKind lookupKind)
    {
        return resolutionKind switch
        {
            ResolutionKind.Value => lookupKind.HasFlag(LookupKind.Value),
            ResolutionKind.Type => lookupKind.HasFlag(LookupKind.Type),
            ResolutionKind.Constructor => lookupKind.HasFlag(LookupKind.Constructor),
            ResolutionKind.Module => lookupKind.HasFlag(LookupKind.Module),
            ResolutionKind.Effect => lookupKind.HasFlag(LookupKind.Effect),
            ResolutionKind.Proof => lookupKind.HasFlag(LookupKind.Proof),
            _ => false
        };
    }

    public LookupResult LookupPath(
        IReadOnlyList<string> path,
        LookupKind kind,
        LookupContext context,
        string? packageAlias = null,
        IReadOnlyList<string>? packageQualifiedPath = null)
    {
        PathResolutionResult result;
        if (!string.IsNullOrWhiteSpace(packageAlias) &&
            context.ResolvePackageQualifiedPath != null &&
            packageQualifiedPath is { Count: > 0 })
        {
            result = context.ResolvePackageQualifiedPath(packageAlias, packageQualifiedPath);
            if (!result.IsSuccess && context.ResolvePathWithImports != null)
            {
                result = context.ResolvePathWithImports(path);
            }
        }
        else if (context.ResolvePathWithImports != null)
        {
            result = context.ResolvePathWithImports(path);
        }
        else
        {
            result = _pathResolver.Resolve(path, context.CurrentModule);
        }

        if (!result.IsSuccess)
        {
            return LookupResult.Failure(
                result.ErrorMessage ??
                DiagnosticMessages.CannotResolvePath(string.Join(WellKnownStrings.Separators.Path, path)));
        }

        return LookupResult.Found(result.SymbolId, result.Kind, result.Kind == ResolutionKind.Constructor);
    }

    public bool TryCollectAmbiguousImportedValueCandidates(
        string name,
        LookupContext context,
        out IReadOnlyList<SymbolId> candidates)
    {
        candidates = [];
        if (context.ImportScope == null)
        {
            return false;
        }

        var importedCandidates = CollectImportedValueCandidates(
            context.ImportScope,
            name,
            allowConstructors: true);
        if (importedCandidates.Count <= 1)
        {
            return false;
        }

        var symbolIds = new List<SymbolId>(importedCandidates.Count);
        for (var i = 0; i < importedCandidates.Count; i++)
        {
            var symbolId = importedCandidates[i].SymbolId;
            if (!symbolId.IsValid || symbolIds.Contains(symbolId))
            {
                continue;
            }

            symbolIds.Add(symbolId);
        }

        candidates = symbolIds;
        return candidates.Count > 1;
    }

    private LookupResult LookupValue(string name, bool allowConstructors, LookupContext context)
    {
        var ambientSymbol = _symbolTable.LookupValue(name);
        if (ambientSymbol.HasValue && IsLocalValueSymbol(ambientSymbol.Value))
        {
            return LookupResult.Found(ambientSymbol.Value, ResolutionKind.Value);
        }

        if (context.CurrentModule.IsValid && context.ImportScope != null)
        {
            var currentModuleValues = _symbolTable.Modules.GetAccessibleBindingsByName(
                context.CurrentModule,
                name,
                context.CurrentModule,
                new HashSet<ResolutionKind> { ResolutionKind.Value });
            if (currentModuleValues.Count == 1)
            {
                return LookupResult.Found(currentModuleValues[0].SymbolId, ResolutionKind.Value);
            }

            if (currentModuleValues.Count > 1)
            {
                return LookupResult.Failure(
                    DiagnosticMessages.AmbiguousCallableOverload(
                        name,
                        string.Join(", ", currentModuleValues.Select(candidate => candidate.Name))));
            }

            if (allowConstructors)
            {
                var currentModuleConstructors = _symbolTable.Modules.GetAccessibleBindingsByName(
                    context.CurrentModule,
                    name,
                    context.CurrentModule,
                    new HashSet<ResolutionKind> { ResolutionKind.Constructor });
                if (currentModuleConstructors.Count == 1)
                {
                    return LookupResult.Found(
                        currentModuleConstructors[0].SymbolId,
                        ResolutionKind.Constructor,
                        isConstructor: true);
                }
            }

            var preferredTraitMethod = TryGetSingleDistinctTraitMethod(context.ImportScope.GetImportDetails(name));
            if (preferredTraitMethod != null &&
                (!ambientSymbol.HasValue || IsPrecompiledSymbol(ambientSymbol.Value)))
            {
                return preferredTraitMethod.SymbolId.IsValid
                    ? LookupResult.Found(preferredTraitMethod.SymbolId, ResolutionKind.Value)
                    : LookupResult.NotFound();
            }

            var importedCandidates = CollectImportedValueCandidates(context.ImportScope, name, allowConstructors);
            if (importedCandidates.Count == 1)
            {
                var imported = importedCandidates[0];
                return LookupResult.Found(
                    imported.SymbolId,
                    imported.Kind,
                    imported.Kind == ResolutionKind.Constructor);
            }

            if (importedCandidates.Count > 1)
            {
                return LookupResult.Failure(BuildAmbiguousValueImportDiagnostic(name, importedCandidates));
            }
        }

        var symbol = _symbolTable.LookupValue(name);
        if (symbol != null)
        {
            return LookupResult.Found(symbol.Value, ResolutionKind.Value);
        }

        if (allowConstructors)
        {
            var ctor = _symbolTable.LookupConstructor(name);
            if (ctor != null)
            {
                return LookupResult.Found(ctor.Value, ResolutionKind.Constructor, isConstructor: true);
            }
        }

        return LookupResult.NotFound();
    }

    private bool IsLocalValueSymbol(SymbolId symbolId)
    {
        return symbolId.IsValid &&
               _symbolTable.GetSymbol(symbolId) is VarSymbol { IsModuleLevel: false };
    }

    private static List<ImportedSymbol> CollectImportedValueCandidates(
        ImportScope importScope,
        string name,
        bool allowConstructors)
    {
        var details = importScope.GetEffectiveImportDetails(name);
        var result = new List<ImportedSymbol>(details.Count);
        for (var i = 0; i < details.Count; i++)
        {
            var detail = details[i];
            if ((detail.Kind != ResolutionKind.Value &&
                 (!allowConstructors || detail.Kind != ResolutionKind.Constructor)) ||
                ContainsImportedSymbol(result, detail))
            {
                continue;
            }

            result.Add(detail);
        }

        return result;
    }

    private static ImportedSymbol? TryGetSingleDistinctTraitMethod(IReadOnlyList<ImportedSymbol> details)
    {
        ImportedSymbol? selected = null;
        for (var i = 0; i < details.Count; i++)
        {
            var detail = details[i];
            if (!detail.IsTraitMethod)
            {
                continue;
            }

            if (selected == null)
            {
                selected = detail;
                continue;
            }

            if (!IsSameImportedSymbol(selected, detail))
            {
                return null;
            }
        }

        return selected;
    }

    private static bool ContainsImportedSymbol(List<ImportedSymbol> candidates, ImportedSymbol candidate)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (IsSameImportedSymbol(candidates[i], candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameImportedSymbol(ImportedSymbol left, ImportedSymbol right)
    {
        return left.SymbolId == right.SymbolId &&
               left.Kind == right.Kind &&
               string.Equals(left.Name, right.Name, StringComparison.Ordinal);
    }

    private bool IsPrecompiledSymbol(SymbolId symbolId)
    {
        var symbol = _symbolTable.GetSymbol(symbolId);
        var filePath = symbol?.Span.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return PrecompiledModuleRegistry.IsStdlibSourcePath(filePath);
    }

    private string BuildAmbiguousValueImportDiagnostic(
        string name,
        IReadOnlyList<ImportedSymbol> candidates)
    {
        var displays = candidates
            .Select(candidate => TryFormatQualifiedValueName(candidate.SymbolId))
            .Where(static display => !string.IsNullOrWhiteSpace(display))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static display => display, StringComparer.Ordinal)
            .ToList();

        if (displays.Count == 0)
        {
            return DiagnosticMessages.AmbiguousImportedValue(name);
        }

        return DiagnosticMessages.AmbiguousImportedValueWithCandidates(name, string.Join(", ", displays));
    }

    private string TryFormatQualifiedValueName(SymbolId symbolId)
    {
        if (!symbolId.IsValid || _symbolTable.GetSymbol(symbolId) is not { } symbol)
        {
            return string.Empty;
        }

        if (_symbolTable.Modules.TryGetOwningModule(symbolId, out var module))
        {
            return string.Join(WellKnownStrings.Separators.ModulePath, module.Path) +
                   WellKnownStrings.Separators.Path +
                   symbol.Name;
        }

        return symbol.Name;
    }
}
