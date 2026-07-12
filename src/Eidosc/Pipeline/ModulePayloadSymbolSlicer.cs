namespace Eidosc.Pipeline;

using Eidosc.Symbols;

internal static class ModulePayloadSymbolSlicer
{
    public static IReadOnlySet<int> CreateNamerSymbolClosure(
        SymbolTable symbolTable,
        IReadOnlyList<LiveStateSymbolIdentity> identities,
        AstNamerStatePayload astState,
        string moduleIdentityKey,
        IReadOnlyList<string> sourcePaths)
    {
        var normalizedSourcePaths = sourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .ToHashSet(PathComparer);
        var localSymbolIds = identities
            .Where(identity => BelongsToCompilationUnit(identity, moduleIdentityKey, normalizedSourcePaths))
            .Select(static identity => identity.SymbolId)
            .Where(static id => id > 0)
            .ToHashSet();
        var allowed = new HashSet<int>(localSymbolIds);
        foreach (var entry in astState.Entries)
        {
            Add(allowed, entry.SymbolId);
            Add(allowed, entry.FunctionSymbolId);
            Add(allowed, entry.EvidenceSymbolId);
            Add(allowed, entry.TargetSymbolId);
            Add(allowed, entry.ProofIntroSymbolId);
            Add(allowed, entry.ResolvedModule);
            AddRange(allowed, entry.IdentifierValueCandidateSymbolIds);
            AddRange(allowed, entry.PathValueCandidateSymbolIds);
            AddRange(allowed, entry.FunctionCandidateSymbolIds);
            AddRange(allowed, entry.MethodCandidateSymbolIds);
            AddRange(allowed, entry.EffectSymbolIds);
            AddRange(allowed, entry.ResolvedSymbols?.Select(static symbol => symbol.SymbolId));
        }

        var sourceModuleIds = symbolTable.Modules.Modules
            .Where(entry =>
                string.Equals(entry.Value.Identity.ToIdentityKey(), moduleIdentityKey, StringComparison.Ordinal) ||
                entry.Value.Members.Any(member => localSymbolIds.Contains(member.Value)))
            .Select(static entry => entry.Key.Value)
            .ToHashSet();
        allowed.UnionWith(sourceModuleIds);
        ExpandSymbolClosure(symbolTable, allowed, sourceModuleIds);
        return allowed;
    }

    public static SymbolTablePayload SliceSymbolTable(
        SymbolTablePayload payload,
        IReadOnlySet<int> allowedSymbolIds)
    {
        var sliced = payload with
        {
            Symbols = payload.Symbols
                .Where(symbol => allowedSymbolIds.Contains(symbol.Id))
                .OrderBy(static symbol => symbol.Id)
                .ToArray(),
            Scopes = payload.Scopes
                .OrderBy(static scope => scope.Index)
                .Select(scope => SliceScope(scope, allowedSymbolIds))
                .Where(static scope => HasBindings(scope))
                .ToArray(),
            GlobalTypes = FilterMap(payload.GlobalTypes, allowedSymbolIds),
            GlobalTraits = FilterMap(payload.GlobalTraits, allowedSymbolIds),
            GlobalConstructors = FilterMap(payload.GlobalConstructors, allowedSymbolIds),
            GlobalAbilities = FilterMap(payload.GlobalAbilities, allowedSymbolIds),
            Hash = ""
        };
        return sliced with { Hash = ModuleArtifactHash.ComputeJsonHash(sliced with { Hash = "" }) };
    }

    public static ModuleRegistryPayload SliceModuleRegistry(
        ModuleRegistryPayload payload,
        IReadOnlySet<int> allowedSymbolIds)
    {
        var allowedModuleIds = payload.Modules
            .Where(module => allowedSymbolIds.Contains(module.Id))
            .Select(static module => module.Id)
            .ToHashSet();
        var sliced = payload with
        {
            RootModules = FilterMap(payload.RootModules, allowedModuleIds),
            ModulePaths = FilterMap(payload.ModulePaths, allowedModuleIds),
            ModuleIdentityKeys = FilterMap(payload.ModuleIdentityKeys, allowedModuleIds),
            ModuleCandidatesByPath = FilterListMap(payload.ModuleCandidatesByPath, allowedModuleIds),
            MemberOwnerModules = payload.MemberOwnerModules
                .Where(entry => allowedSymbolIds.Contains(entry.Key))
                .Select(entry => new KeyValuePair<int, IReadOnlyList<int>>(
                    entry.Key,
                    entry.Value.Where(allowedModuleIds.Contains).Order().ToArray()))
                .Where(static entry => entry.Value.Count > 0)
                .OrderBy(static entry => entry.Key)
                .ToDictionary(static entry => entry.Key, static entry => entry.Value),
            Modules = payload.Modules
                .Where(module => allowedModuleIds.Contains(module.Id))
                .OrderBy(static module => module.Id)
                .Select(module => module with
                {
                    Members = module.Members.Where(allowedSymbolIds.Contains).ToArray(),
                    Exports = module.Exports
                        .Where(binding => allowedSymbolIds.Contains(binding.SymbolId))
                        .ToArray(),
                    Imports = module.Imports.Where(allowedModuleIds.Contains).ToArray(),
                    ParentModule = allowedModuleIds.Contains(module.ParentModule)
                        ? module.ParentModule
                        : SymbolId.None.Value
                })
                .ToArray(),
            Hash = ""
        };
        return sliced with { Hash = ModuleArtifactHash.ComputeJsonHash(sliced with { Hash = "" }) };
    }

    public static string ResolveModuleIdentityKey(SymbolTable? symbolTable, string moduleKey)
    {
        if (symbolTable != null &&
            symbolTable.Modules.ModulePaths.TryGetValue(moduleKey, out var moduleId) &&
            symbolTable.Modules.GetModule(moduleId) is { } module)
        {
            return module.Identity.ToIdentityKey();
        }

        return moduleKey;
    }

    private static void ExpandSymbolClosure(
        SymbolTable symbolTable,
        HashSet<int> allowed,
        IReadOnlySet<int> sourceModuleIds)
    {
        var typeOwners = symbolTable.Symbols.Values
            .Where(static symbol => symbol.TypeId.IsValid)
            .GroupBy(static symbol => symbol.TypeId)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static symbol => symbol.Id).ToArray());
        var queue = new Queue<int>(allowed.Order());
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (symbolTable.GetSymbol(new SymbolId(id)) is not { } symbol)
            {
                continue;
            }

            foreach (var referenced in EnumerateReferencedSymbols(symbol, sourceModuleIds.Contains(id)))
            {
                if (referenced.IsValid && allowed.Add(referenced.Value))
                {
                    queue.Enqueue(referenced.Value);
                }
            }

            foreach (var typeId in EnumerateReferencedTypes(symbol))
            {
                if (!typeId.IsValid || !typeOwners.TryGetValue(typeId, out var owners))
                {
                    continue;
                }

                foreach (var owner in owners)
                {
                    if (owner.IsValid && allowed.Add(owner.Value))
                    {
                        queue.Enqueue(owner.Value);
                    }
                }
            }

            foreach (var ownerModule in symbolTable.Modules.GetOwningModuleIds(symbol.Id))
            {
                if (ownerModule.IsValid && allowed.Add(ownerModule.Value))
                {
                    queue.Enqueue(ownerModule.Value);
                }
            }
        }
    }

    private static IEnumerable<SymbolId> EnumerateReferencedSymbols(Symbol symbol, bool includeModuleMembers)
    {
        switch (symbol)
        {
            case FuncSymbol function:
                foreach (var id in function.TypeParams.Concat(function.Parameters)) yield return id;
                if (function.OwnerTrait is { } ownerTrait) yield return ownerTrait;
                break;
            case AdtSymbol adt:
                foreach (var id in adt.TypeParams.Concat(adt.Constructors).Concat(adt.Fields)) yield return id;
                break;
            case CtorSymbol constructor:
                yield return constructor.OwnerAdt;
                foreach (var id in constructor.TypeParams.Concat(constructor.NamedFields)) yield return id;
                break;
            case FieldSymbol field:
                yield return field.OwnerType;
                break;
            case TraitSymbol trait:
                foreach (var id in trait.TypeParams
                             .Concat(trait.Methods)
                             .Concat(trait.AssociatedTypes)
                             .Concat(trait.ParentTraits)) yield return id;
                break;
            case EffectSymbol:
                break;
            case TypeParamSymbol typeParameter:
                foreach (var id in typeParameter.TraitConstraints) yield return id;
                break;
            case ImplSymbol implementation:
                yield return implementation.Trait;
                foreach (var id in implementation.Methods
                             .Concat(implementation.TraitMethodImplementations.Keys)
                             .Concat(implementation.TraitMethodImplementations.Values)) yield return id;
                foreach (var id in EnumerateImplTypeSymbols(implementation.ImplementingTypeKey)) yield return id;
                foreach (var id in implementation.TraitTypeArgKeys
                             .Concat(implementation.CanonicalTraitTypeArgKeys)
                             .SelectMany(EnumerateImplTypeSymbols)) yield return id;
                foreach (var requirement in implementation.ImplementingTypeRequirements)
                {
                    yield return requirement.Trait;
                    foreach (var id in requirement.TraitTypeArgKeys.SelectMany(EnumerateImplTypeSymbols)) yield return id;
                }
                break;
            case ModuleSymbol module:
                if (includeModuleMembers)
                {
                    foreach (var id in module.Members) yield return id;
                    foreach (var binding in module.ExportedBindings) yield return binding.SymbolId;
                }

                foreach (var id in module.Imports) yield return id;
                if (module.ParentModule is { } parent) yield return parent;
                break;
        }
    }

    private static IEnumerable<TypeId> EnumerateReferencedTypes(Symbol symbol)
    {
        if (symbol.TypeId.IsValid)
        {
            yield return symbol.TypeId;
        }

        switch (symbol)
        {
            case FuncSymbol function:
                foreach (var id in function.ParamTypes) yield return id;
                yield return function.ReturnType;
                yield return function.CStructFieldTypeId;
                break;
            case VarSymbol variable:
                yield return variable.Type;
                break;
            case AdtSymbol { AliasTarget: { } aliasTarget }:
                yield return aliasTarget;
                break;
            case CtorSymbol constructor:
                foreach (var id in constructor.PositionalArgs) yield return id;
                break;
            case FieldSymbol field:
                yield return field.FieldType;
                break;
            case ImplSymbol implementation:
                yield return implementation.ImplementingType;
                foreach (var entry in implementation.TypeArguments)
                {
                    yield return entry.Key;
                    yield return entry.Value;
                }
                break;
        }
    }

    private static IEnumerable<SymbolId> EnumerateImplTypeSymbols(ImplTypeRefKey key)
    {
        if (key.SymbolId.IsValid)
        {
            yield return key.SymbolId;
        }

        foreach (var argument in key.TypeArguments)
        {
            foreach (var symbol in EnumerateImplTypeSymbols(argument))
            {
                yield return symbol;
            }
        }
    }

    private static bool BelongsToCompilationUnit(
        LiveStateSymbolIdentity identity,
        string moduleIdentityKey,
        IReadOnlySet<string> normalizedSourcePaths)
    {
        var module = identity.StableKey.Declaration.Module;
        if (normalizedSourcePaths.Count > 0 &&
            normalizedSourcePaths.Contains(NormalizePath(module.NormalizedSourcePath)))
        {
            return true;
        }

        var expected = ParseModuleIdentityKey(moduleIdentityKey);
        return string.Equals(module.PackageInstanceKey, expected.PackageInstanceKey, StringComparison.Ordinal) &&
               string.Equals(module.ModulePath, expected.ModulePath, StringComparison.Ordinal);
    }

    private static (string PackageInstanceKey, string ModulePath) ParseModuleIdentityKey(string moduleIdentityKey)
    {
        var pathSeparator = moduleIdentityKey.IndexOf("::", StringComparison.Ordinal);
        if (pathSeparator < 0)
        {
            return (ModuleIdentity.CurrentPackageInstanceKey, moduleIdentityKey);
        }

        var packagePart = moduleIdentityKey[..pathSeparator];
        var instanceSeparator = packagePart.IndexOf('@', StringComparison.Ordinal);
        var packageInstanceKey = instanceSeparator >= 0
            ? packagePart[(instanceSeparator + 1)..]
            : ModuleIdentity.CurrentPackageInstanceKey;
        return (
            string.IsNullOrWhiteSpace(packageInstanceKey)
                ? ModuleIdentity.CurrentPackageInstanceKey
                : packageInstanceKey,
            moduleIdentityKey[(pathSeparator + 2)..]);
    }

    private static ScopePayload SliceScope(ScopePayload scope, IReadOnlySet<int> allowedSymbolIds) =>
        scope with
        {
            Bindings = FilterMap(scope.Bindings, allowedSymbolIds),
            FunctionOverloads = FilterListMap(scope.FunctionOverloads, allowedSymbolIds),
            Types = FilterMap(scope.Types, allowedSymbolIds),
            Traits = FilterMap(scope.Traits, allowedSymbolIds),
            Effects = FilterMap(scope.Effects, allowedSymbolIds),
            Constructors = FilterMap(scope.Constructors, allowedSymbolIds)
        };

    private static bool HasBindings(ScopePayload scope) =>
        scope.Bindings.Count > 0 ||
        scope.FunctionOverloads.Count > 0 ||
        scope.Types.Count > 0 ||
        scope.Traits.Count > 0 ||
        scope.Effects.Count > 0 ||
        scope.Constructors.Count > 0;

    private static IReadOnlyDictionary<string, int> FilterMap(
        IReadOnlyDictionary<string, int> source,
        IReadOnlySet<int> allowedIds) =>
        source
            .Where(entry => allowedIds.Contains(entry.Value))
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> FilterListMap(
        IReadOnlyDictionary<string, IReadOnlyList<int>> source,
        IReadOnlySet<int> allowedIds) =>
        source
            .Select(entry => new KeyValuePair<string, IReadOnlyList<int>>(
                entry.Key,
                entry.Value.Where(allowedIds.Contains).Order().ToArray()))
            .Where(static entry => entry.Value.Count > 0)
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);

    private static void Add(HashSet<int> values, int? value)
    {
        if (value is > 0)
        {
            values.Add(value.Value);
        }
    }

    private static void AddRange(HashSet<int> values, IEnumerable<int>? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (var value in source)
        {
            Add(values, value);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('<'))
        {
            return path.Replace('\\', '/');
        }

        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
