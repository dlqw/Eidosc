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

        return LookupResult.NotFound();
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

        if (context.ImportScope.TryLookupImportedSymbol(name, out _, out var isAmbiguous, out var importedCandidates) ||
            !isAmbiguous)
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
            if (ambientSymbol.HasValue &&
                IsCurrentModuleMember(context.CurrentModule, ambientSymbol.Value) &&
                TryCollectImportedValueCandidates(context.ImportScope, name, out var importedValueCandidates))
            {
                // Trait method imports are synthetic — they should not shadow or
                // conflict with the module's own direct definitions.
                var nonTraitImportCount = CountNonTraitImports(importedValueCandidates);
                if (nonTraitImportCount == 0)
                {
                    // All imports are trait methods; module's own definition wins.
                    return LookupResult.Found(ambientSymbol.Value, ResolutionKind.Value);
                }

                var valueCandidates = new List<ImportedSymbol>(nonTraitImportCount + 1)
                {
                    new()
                    {
                        Name = name,
                        SymbolId = ambientSymbol.Value,
                        Kind = ResolutionKind.Value
                    }
                };
                for (var i = 0; i < importedValueCandidates.Count; i++)
                {
                    var candidate = importedValueCandidates[i];
                    if (!candidate.IsTraitMethod)
                    {
                        valueCandidates.Add(candidate);
                    }
                }

                return LookupResult.Failure(BuildAmbiguousValueImportDiagnostic(name, valueCandidates));
            }

            var preferredTraitMethod = TryGetSingleDistinctTraitMethod(context.ImportScope.GetImportDetails(name));
            if (preferredTraitMethod != null &&
                (!ambientSymbol.HasValue || IsPrecompiledSymbol(ambientSymbol.Value)))
            {
                return preferredTraitMethod.SymbolId.IsValid
                    ? LookupResult.Found(preferredTraitMethod.SymbolId, ResolutionKind.Value)
                    : LookupResult.NotFound();
            }

            if (context.ImportScope.TryLookupImportedSymbol(name, out var importedSymbol, out var isAmbiguous, out var candidates))
            {
                return LookupResult.Found(importedSymbol, ResolutionKind.Value);
            }

            if (isAmbiguous)
            {
                return LookupResult.Failure(BuildAmbiguousValueImportDiagnostic(name, candidates));
            }
        }

        var symbol = _symbolTable.LookupValue(name);
        if (symbol != null)
        {
            return LookupResult.Found(symbol.Value, ResolutionKind.Value);
        }

        if (allowConstructors && name.Length > 0 && char.IsUpper(name[0]))
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

    private bool IsCurrentModuleMember(SymbolId currentModule, SymbolId symbolId)
    {
        return symbolId.IsValid &&
               currentModule.IsValid &&
               _symbolTable.Modules.GetModuleMembers(currentModule).Contains(symbolId);
    }

    private static bool TryCollectImportedValueCandidates(
        ImportScope importScope,
        string name,
        out IReadOnlyList<ImportedSymbol> candidates)
    {
        var details = importScope.GetImportDetails(name);
        var result = new List<ImportedSymbol>(details.Count);
        for (var i = 0; i < details.Count; i++)
        {
            var detail = details[i];
            if (detail.Kind is not (ResolutionKind.Value or ResolutionKind.Constructor) ||
                ContainsImportedSymbol(result, detail))
            {
                continue;
            }

            result.Add(detail);
        }

        candidates = result;
        return candidates.Count > 0;
    }

    private static int CountNonTraitImports(IReadOnlyList<ImportedSymbol> candidates)
    {
        var count = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates[i].IsTraitMethod)
            {
                count++;
            }
        }

        return count;
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
