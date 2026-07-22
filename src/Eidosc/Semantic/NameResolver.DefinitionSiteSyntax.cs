using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private Func<IReadOnlyList<string>, MetaDefinitionSiteLookupKind, Symbol?>
        CreateDefinitionSiteSyntaxResolver(SymbolId definitionModuleId) =>
        (path, kind) => ResolveDefinitionSiteSyntaxSymbol(definitionModuleId, path, kind);

    private Symbol? ResolveDefinitionSiteSyntaxSymbol(
        SymbolId definitionModuleId,
        IReadOnlyList<string> path,
        MetaDefinitionSiteLookupKind kind)
    {
        if (path.Count == 0 || kind == MetaDefinitionSiteLookupKind.None)
        {
            return null;
        }

        var allowedKinds = ToResolutionKinds(kind);
        if (path.Count == 1)
        {
            return ResolveSimpleDefinitionSiteSyntaxSymbol(
                definitionModuleId,
                path[0],
                kind,
                allowedKinds);
        }

        PathResolutionResult result;
        if (_importScopes.TryGetValue(definitionModuleId, out var importScope) &&
            importScope.LookupImportedModule(path[0]) is { IsValid: true } importedModule)
        {
            result = ResolveImportedModulePath(importedModule, path.Skip(1).ToArray(), allowedKinds);
            if (result.IsSuccess)
            {
                return GetDefinitionSiteResultSymbol(result, kind);
            }
        }

        if (_importScopes.TryGetValue(definitionModuleId, out importScope))
        {
            var importedCandidates = importScope.GetEffectiveImportDetails(path[0])
                .Where(candidate => candidate.SymbolId.IsValid)
                .DistinctBy(candidate => (candidate.SymbolId, candidate.Kind))
                .ToArray();
            if (importedCandidates.Length == 1)
            {
                result = ResolveImportedMemberPath(
                    importedCandidates[0].SymbolId,
                    path.Skip(1).ToArray(),
                    allowedKinds);
                if (result.IsSuccess)
                {
                    return GetDefinitionSiteResultSymbol(result, kind);
                }
            }
        }

        result = _pathResolver.Resolve(path, definitionModuleId);
        if (GetDefinitionSiteResultSymbol(result, kind) is { } resolved)
        {
            return resolved;
        }

        var qualifiedCandidates = new List<Symbol>();
        for (var splitIndex = path.Count - 1; splitIndex >= 1; splitIndex--)
        {
            var modulePath = path.Take(splitIndex).ToArray();
            var remainingPath = path.Skip(splitIndex).ToArray();
            foreach (var moduleId in _symbolTable.Modules.LookupModuleCandidatesByPath(modulePath))
            {
                var candidateResult = ResolveImportedModulePath(moduleId, remainingPath, allowedKinds);
                if (GetDefinitionSiteResultSymbol(candidateResult, kind) is { } candidate &&
                    qualifiedCandidates.All(existing => existing.Id != candidate.Id))
                {
                    qualifiedCandidates.Add(candidate);
                }
            }
        }

        return qualifiedCandidates.Count == 1 ? qualifiedCandidates[0] : null;
    }

    private Symbol? ResolveSimpleDefinitionSiteSyntaxSymbol(
        SymbolId definitionModuleId,
        string name,
        MetaDefinitionSiteLookupKind kind,
        IReadOnlySet<ResolutionKind> allowedKinds)
    {
        var candidates = new HashSet<SymbolId>();
        if (kind.HasFlag(MetaDefinitionSiteLookupKind.Type) &&
            _symbolTable.BuiltinScope?.LookupType(name) is { IsValid: true } builtinType)
        {
            candidates.Add(builtinType);
        }
        if (kind.HasFlag(MetaDefinitionSiteLookupKind.Value) &&
            _symbolTable.BuiltinScope?.LookupValue(name) is { IsValid: true } builtinValue)
        {
            candidates.Add(builtinValue);
        }

        if (definitionModuleId.IsValid && _symbolTable.Modules.GetModule(definitionModuleId) is { } module)
        {
            if (kind.HasFlag(MetaDefinitionSiteLookupKind.Module) &&
                string.Equals(module.Name, name, StringComparison.Ordinal))
            {
                candidates.Add(module.Id);
            }

            foreach (var memberId in module.Members)
            {
                if (memberId.IsValid &&
                    _symbolTable.GetSymbol(memberId) is { } member &&
                    string.Equals(member.Name, name, StringComparison.Ordinal) &&
                    MatchesDefinitionSiteLookupKind(member, kind))
                {
                    candidates.Add(memberId);
                }
            }
        }

        if (_importScopes.TryGetValue(definitionModuleId, out var importScope))
        {
            foreach (var imported in importScope.GetEffectiveImportDetails(name))
            {
                if (imported.SymbolId.IsValid && allowedKinds.Contains(imported.Kind))
                {
                    candidates.Add(imported.SymbolId);
                }
            }
            if (kind.HasFlag(MetaDefinitionSiteLookupKind.Module) &&
                importScope.LookupImportedModule(name) is { IsValid: true } importedModule)
            {
                candidates.Add(importedModule);
            }
        }

        if (kind.HasFlag(MetaDefinitionSiteLookupKind.Module))
        {
            if (_symbolTable.Modules.LookupRootModule(name) is { IsValid: true } rootModule)
            {
                candidates.Add(rootModule);
            }
            foreach (var moduleId in _symbolTable.Modules.LookupModuleCandidatesByPath([name]))
            {
                candidates.Add(moduleId);
            }
        }

        var resolved = candidates
            .Select(_symbolTable.GetSymbol)
            .Where(static candidate => candidate != null)
            .Where(candidate => MatchesDefinitionSiteLookupKind(candidate!, kind))
            .DistinctBy(static candidate => candidate!.Id)
            .ToArray();
        return resolved.Length == 1 ? resolved[0] : null;
    }

    private Symbol? GetDefinitionSiteResultSymbol(
        PathResolutionResult result,
        MetaDefinitionSiteLookupKind kind)
    {
        if (!result.IsSuccess || !result.SymbolId.IsValid ||
            _symbolTable.GetSymbol(result.SymbolId) is not { } symbol)
        {
            return null;
        }

        if (kind.HasFlag(MetaDefinitionSiteLookupKind.Constructor) &&
            symbol is AdtSymbol { IsCaseType: true, CaseConstructor.IsValid: true } caseType &&
            _symbolTable.GetSymbol(caseType.CaseConstructor) is { } constructor)
        {
            symbol = constructor;
        }

        return MatchesDefinitionSiteLookupKind(symbol, kind) ? symbol : null;
    }

    private static IReadOnlySet<ResolutionKind> ToResolutionKinds(MetaDefinitionSiteLookupKind kind)
    {
        var result = new HashSet<ResolutionKind>();
        if (kind.HasFlag(MetaDefinitionSiteLookupKind.Value)) result.Add(ResolutionKind.Value);
        if (kind.HasFlag(MetaDefinitionSiteLookupKind.Type)) result.Add(ResolutionKind.Type);
        if (kind.HasFlag(MetaDefinitionSiteLookupKind.Constructor)) result.Add(ResolutionKind.Constructor);
        if (kind.HasFlag(MetaDefinitionSiteLookupKind.Module)) result.Add(ResolutionKind.Module);
        if (kind.HasFlag(MetaDefinitionSiteLookupKind.Effect)) result.Add(ResolutionKind.Effect);
        return result;
    }

    private static bool MatchesDefinitionSiteLookupKind(Symbol symbol, MetaDefinitionSiteLookupKind kind) =>
        symbol switch
        {
            ModuleSymbol => kind.HasFlag(MetaDefinitionSiteLookupKind.Module),
            CtorSymbol => kind.HasFlag(MetaDefinitionSiteLookupKind.Constructor) ||
                          kind.HasFlag(MetaDefinitionSiteLookupKind.Value),
            AdtSymbol or TraitSymbol or TypeParamSymbol or AssociatedTypeSymbol =>
                kind.HasFlag(MetaDefinitionSiteLookupKind.Type),
            EffectSymbol => kind.HasFlag(MetaDefinitionSiteLookupKind.Effect) ||
                            kind.HasFlag(MetaDefinitionSiteLookupKind.Type),
            VarSymbol or FuncSymbol or ImplSymbol or AssociatedConstSymbol or FieldSymbol =>
                kind.HasFlag(MetaDefinitionSiteLookupKind.Value),
            _ => false
        };
}
