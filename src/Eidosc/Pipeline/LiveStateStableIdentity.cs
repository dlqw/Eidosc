namespace Eidosc.Pipeline;

using Eidosc.Symbols;

public readonly record struct LiveStateModuleStableKey(
    string PackageInstanceKey,
    string ModulePath,
    string NormalizedSourcePath)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(PackageInstanceKey) &&
        string.IsNullOrWhiteSpace(ModulePath) &&
        string.IsNullOrWhiteSpace(NormalizedSourcePath);

    public override string ToString() =>
        $"{PackageInstanceKey}\0{ModulePath}\0{NormalizedSourcePath}";
}

public readonly record struct LiveStateDeclStableKey(
    LiveStateModuleStableKey Module,
    string DeclarationKind,
    string ExportedName,
    string OverloadDiscriminator,
    string SourceStableSpan)
{
    public bool IsEmpty => Module.IsEmpty ||
                           string.IsNullOrWhiteSpace(DeclarationKind) ||
                           string.IsNullOrWhiteSpace(ExportedName);

    public override string ToString() =>
        $"{Module}\0{DeclarationKind}\0{ExportedName}\0{OverloadDiscriminator}\0{SourceStableSpan}";
}

public readonly record struct LiveStateSymbolStableKey(
    LiveStateDeclStableKey Declaration,
    string SymbolRole)
{
    public bool IsEmpty => Declaration.IsEmpty || string.IsNullOrWhiteSpace(SymbolRole);

    public override string ToString() => $"{Declaration}\0{SymbolRole}";
}

public sealed record LiveStateSymbolIdentity(
    int SymbolId,
    string SymbolKind,
    string Name,
    int TypeId,
    LiveStateSymbolStableKey StableKey);

public sealed record LiveStateRemapResolution(
    bool IsValid,
    LiveStateRemapKind Kind,
    IReadOnlyList<LiveStateSymbolRemapEntry> Symbols,
    IReadOnlyList<LiveStateTypeRemapEntry> Types,
    IReadOnlyList<string> Failures)
{
    public static LiveStateRemapResolution Blocked(IReadOnlyList<string> failures) =>
        new(false, LiveStateRemapKind.NotRestorable, [], [], failures);
}

public sealed class LiveStateIdRemapper
{
    private readonly IReadOnlyDictionary<int, int> _symbols;
    private readonly IReadOnlyDictionary<int, int> _types;
    private readonly int _typeVariableOffset;
    private readonly int _valueVariableOffset;

    public LiveStateIdRemapper(
        LiveStateRemapPlan plan,
        int typeVariableOffset = 0,
        int valueVariableOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _symbols = plan.Symbols
            .GroupBy(static entry => entry.From)
            .ToDictionary(static group => group.Key, static group => group.First().To);
        _typeVariableOffset = Math.Max(0, typeVariableOffset);
        _valueVariableOffset = Math.Max(0, valueVariableOffset);
        _types = plan.Types
            .GroupBy(static entry => entry.From)
            .ToDictionary(static group => group.Key, static group => group.First().To);
    }

    public int RemapSymbol(int value) =>
        value < 0 ? value : _symbols.GetValueOrDefault(value, value);

    public SymbolId RemapSymbol(SymbolId value) =>
        new(RemapSymbol(value.Value));

    public int RemapType(int value) =>
        value < 0 ? value : _types.GetValueOrDefault(value, value);

    public TypeId RemapType(TypeId value) =>
        new(RemapType(value.Value));

    public int RemapTypeVariable(int value) =>
        value < 0 ? value : checked(value + _typeVariableOffset);

    public int RemapNextTypeVariable(int value) =>
        value <= 0 ? _typeVariableOffset : checked(value + _typeVariableOffset);

    public int RemapValueVariable(int value) =>
        value < 0 ? value : checked(value + _valueVariableOffset);

    public int RemapNextValueVariable(int value) =>
        value <= 0 ? _valueVariableOffset : checked(value + _valueVariableOffset);
}

public static class LiveStateStableIdentityBuilder
{
    private const string BuiltinModulePath = "<builtins>";
    private const string UnknownSourcePath = "<unknown>";
    private const string NoSourceSpan = "<no-source>";

    public static IReadOnlyList<LiveStateSymbolIdentity> BuildSymbolIdentities(
        SymbolTable symbolTable,
        ProjectModuleGraphSnapshot? graph = null)
    {
        var sourcePathByModuleKey = BuildSourcePathMap(graph);
        var moduleBySourcePath = BuildModuleBySourcePath(symbolTable, sourcePathByModuleKey);
        return symbolTable.Symbols.Values
            .OrderBy(static symbol => symbol.Id.Value)
            .Select(symbol => CreateSymbolIdentity(symbolTable, symbol, sourcePathByModuleKey, moduleBySourcePath))
            .Where(static identity => !identity.StableKey.IsEmpty)
            .ToArray();
    }

    public static LiveStateRemapResolution PlanRemap(
        IReadOnlyList<LiveStateSymbolIdentity> previous,
        IReadOnlyList<LiveStateSymbolIdentity> current)
    {
        var failures = new List<string>();
        var previousByKey = BuildPreviousMap(previous, failures);
        var currentByKey = BuildUniqueMap(current, "current", failures);
        if (failures.Count > 0)
        {
            return LiveStateRemapResolution.Blocked(failures);
        }

        var symbolRemaps = new List<LiveStateSymbolRemapEntry>();
        var typeRemaps = new SortedDictionary<int, int>();
        foreach (var (key, previousIdentities) in previousByKey.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!currentByKey.TryGetValue(key, out var currentIdentity))
            {
                failures.Add($"missing-current-symbol:{key}");
                continue;
            }

            foreach (var previousIdentity in previousIdentities
                         .GroupBy(static identity => identity.SymbolId)
                         .Select(static group => group.First()))
            {
                symbolRemaps.Add(new LiveStateSymbolRemapEntry(previousIdentity.SymbolId, currentIdentity.SymbolId));
                if (previousIdentity.TypeId > 0 && currentIdentity.TypeId > 0)
                {
                    if (typeRemaps.TryGetValue(previousIdentity.TypeId, out var existing) &&
                        existing != currentIdentity.TypeId)
                    {
                        failures.Add($"ambiguous-type-remap:{previousIdentity.TypeId}->{existing}|{currentIdentity.TypeId}");
                        continue;
                    }

                    typeRemaps[previousIdentity.TypeId] = currentIdentity.TypeId;
                }
            }
        }

        if (failures.Count > 0)
        {
            return LiveStateRemapResolution.Blocked(failures);
        }

        var isIdentity = symbolRemaps.All(static entry => entry.From == entry.To) &&
                         typeRemaps.All(static entry => entry.Key == entry.Value);
        return new LiveStateRemapResolution(
            true,
            isIdentity ? LiveStateRemapKind.Identity : LiveStateRemapKind.StableKey,
            symbolRemaps.OrderBy(static entry => entry.From).ToArray(),
            typeRemaps.Select(static entry => new LiveStateTypeRemapEntry(entry.Key, entry.Value)).ToArray(),
            []);
    }

    public static LiveStateRemapResolution PlanRemap(
        IReadOnlyList<LiveStateSymbolIdentity> previous,
        IReadOnlyList<LiveStateSymbolIdentity> current,
        LiveStateRemapPlan seed)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(seed);

        var failures = new List<string>();
        if (seed.SchemaVersion != LiveStateRemapPlan.CurrentSchemaVersion ||
            seed.Kind == LiveStateRemapKind.NotRestorable)
        {
            return LiveStateRemapResolution.Blocked(["invalid-seed-remap-plan"]);
        }

        var previousById = BuildUniqueIdMap(previous, "previous", failures);
        var currentById = BuildUniqueIdMap(current, "current", failures);
        var seedSymbols = new Dictionary<int, int>();
        foreach (var entry in seed.Symbols.OrderBy(static entry => entry.From))
        {
            if (!seedSymbols.TryAdd(entry.From, entry.To))
            {
                failures.Add($"duplicate-seed-symbol:{entry.From}");
            }
        }

        var symbolRemaps = new List<LiveStateSymbolRemapEntry>();
        var seededPreviousIds = new HashSet<int>();
        var reservedCurrentIds = new HashSet<int>();
        foreach (var (previousId, currentId) in seedSymbols.OrderBy(static entry => entry.Key))
        {
            if (!previousById.TryGetValue(previousId, out var previousIdentity))
            {
                continue;
            }

            if (!currentById.TryGetValue(currentId, out var currentIdentity))
            {
                failures.Add($"missing-seeded-current-symbol:{previousId}->{currentId}");
                continue;
            }

            if (!string.Equals(previousIdentity.SymbolKind, currentIdentity.SymbolKind, StringComparison.Ordinal) ||
                !string.Equals(previousIdentity.Name, currentIdentity.Name, StringComparison.Ordinal))
            {
                failures.Add($"seeded-symbol-mismatch:{previousId}->{currentId}");
                continue;
            }

            symbolRemaps.Add(new LiveStateSymbolRemapEntry(previousId, currentId));
            seededPreviousIds.Add(previousId);
            reservedCurrentIds.Add(currentId);
        }

        if (failures.Count > 0)
        {
            return LiveStateRemapResolution.Blocked(failures);
        }

        var residualPrevious = previous
            .Where(identity => !seededPreviousIds.Contains(identity.SymbolId))
            .ToArray();
        var residualKeys = residualPrevious
            .Select(static identity => identity.StableKey.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var residualCurrent = current
            .Where(identity => !reservedCurrentIds.Contains(identity.SymbolId) &&
                               residualKeys.Contains(identity.StableKey.ToString()))
            .ToArray();
        var residual = PlanRemap(residualPrevious, residualCurrent);
        if (!residual.IsValid)
        {
            return residual;
        }

        symbolRemaps.AddRange(residual.Symbols);
        var typeRemaps = new SortedDictionary<int, int>();
        foreach (var entry in seed.Types.Concat(residual.Types))
        {
            if (typeRemaps.TryGetValue(entry.From, out var existing) && existing != entry.To)
            {
                failures.Add($"ambiguous-type-remap:{entry.From}->{existing}|{entry.To}");
                continue;
            }

            typeRemaps[entry.From] = entry.To;
        }

        if (failures.Count > 0)
        {
            return LiveStateRemapResolution.Blocked(failures);
        }

        var isIdentity = symbolRemaps.All(static entry => entry.From == entry.To) &&
                         typeRemaps.All(static entry => entry.Key == entry.Value);
        return new LiveStateRemapResolution(
            true,
            isIdentity ? LiveStateRemapKind.Identity : LiveStateRemapKind.StableKey,
            symbolRemaps.OrderBy(static entry => entry.From).ToArray(),
            typeRemaps.Select(static entry => new LiveStateTypeRemapEntry(entry.Key, entry.Value)).ToArray(),
            []);
    }

    private static Dictionary<string, string> BuildSourcePathMap(ProjectModuleGraphSnapshot? graph)
    {
        if (graph == null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return graph.Nodes.ToDictionary(
            static node => node.ModuleKey,
            static node => node.SourcePaths.Count == 0
                ? UnknownSourcePath
                : NormalizeSourcePath(node.SourcePaths.OrderBy(static path => path, StringComparer.Ordinal).First()),
            StringComparer.Ordinal);
    }

    private static LiveStateSymbolIdentity CreateSymbolIdentity(
        SymbolTable symbolTable,
        Symbol symbol,
        IReadOnlyDictionary<string, string> sourcePathByModuleKey,
        IReadOnlyDictionary<string, ModuleSymbol> moduleBySourcePath)
    {
        var module = ResolveModule(symbolTable, symbol) ?? ResolveModuleBySourcePath(symbol, moduleBySourcePath);
        var moduleKey = module?.Identity.ToDisplayKey() ?? BuiltinModulePath;
        var moduleStableKey = new LiveStateModuleStableKey(
            NormalizePackageInstanceKey(module?.PackageInstanceKey),
            NormalizeModulePath(module?.Path ?? []),
            sourcePathByModuleKey.TryGetValue(moduleKey, out var sourcePath) ? sourcePath : UnknownSourcePath);
        var declKey = new LiveStateDeclStableKey(
            moduleStableKey,
            symbol.Kind.ToString(),
            symbol.Name,
            GetOverloadDiscriminator(symbolTable, symbol),
            GetSourceStableSpan(symbol));
        var symbolKey = new LiveStateSymbolStableKey(declKey, GetSymbolRole(symbolTable, symbol));
        return new LiveStateSymbolIdentity(
            symbol.Id.Value,
            symbol.Kind.ToString(),
            symbol.Name,
            symbol.TypeId.Value,
            symbolKey);
    }

    private static ModuleSymbol? ResolveModule(SymbolTable symbolTable, Symbol symbol)
    {
        return ResolveModule(symbolTable, symbol, []);
    }

    private static ModuleSymbol? ResolveModule(
        SymbolTable symbolTable,
        Symbol symbol,
        HashSet<SymbolId> visited)
    {
        if (!visited.Add(symbol.Id))
        {
            return null;
        }

        if (symbol is ModuleSymbol moduleSymbol)
        {
            return moduleSymbol;
        }

        if (symbolTable.Modules.TryGetOwningModule(symbol.Id, out var indexedOwner))
        {
            return indexedOwner;
        }

        var listedOwner = symbolTable.Modules.Modules
            .OrderBy(static entry => entry.Value.Identity.ToIdentityKey(), StringComparer.Ordinal)
            .FirstOrDefault(entry => entry.Value.Members.Contains(symbol.Id));
        if (listedOwner.Value != null)
        {
            return listedOwner.Value;
        }

        var semanticOwnerId = GetSemanticOwnerId(symbol);
        return semanticOwnerId.IsValid && symbolTable.GetSymbol(semanticOwnerId) is { } semanticOwner
            ? ResolveModule(symbolTable, semanticOwner, visited)
            : null;
    }

    private static IReadOnlyDictionary<string, ModuleSymbol> BuildModuleBySourcePath(
        SymbolTable symbolTable,
        IReadOnlyDictionary<string, string> sourcePathByModuleKey)
    {
        if (sourcePathByModuleKey.Count == 0)
        {
            return new Dictionary<string, ModuleSymbol>(StringComparer.Ordinal);
        }

        var modulesByDisplayKey = symbolTable.Modules.Modules.Values
            .GroupBy(static module => module.Identity.ToDisplayKey(), StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var result = new Dictionary<string, ModuleSymbol>(StringComparer.Ordinal);
        foreach (var module in symbolTable.Modules.Modules.Values)
        {
            if (!string.IsNullOrWhiteSpace(module.Span.FilePath))
            {
                result[NormalizeSourcePath(module.Span.FilePath)] = module;
            }
        }

        foreach (var (moduleKey, sourcePath) in sourcePathByModuleKey)
        {
            if (modulesByDisplayKey.TryGetValue(moduleKey, out var module))
            {
                result[sourcePath] = module;
            }
        }

        return result;
    }

    private static ModuleSymbol? ResolveModuleBySourcePath(
        Symbol symbol,
        IReadOnlyDictionary<string, ModuleSymbol> moduleBySourcePath)
    {
        if (string.IsNullOrWhiteSpace(symbol.Span.FilePath))
        {
            return null;
        }

        var sourcePath = NormalizeSourcePath(symbol.Span.FilePath);
        return moduleBySourcePath.TryGetValue(sourcePath, out var module)
            ? module
            : null;
    }

    private static string NormalizePackageInstanceKey(string? packageInstanceKey) =>
        string.IsNullOrWhiteSpace(packageInstanceKey) ? ModuleIdentity.CurrentPackageInstanceKey : packageInstanceKey.Trim();

    private static string NormalizeModulePath(IReadOnlyList<string> path) =>
        path.Count == 0 ? BuiltinModulePath : string.Join("/", path);

    private static string NormalizeSourcePath(string path) =>
        path.Replace('\\', '/').Trim();

    private static string GetSourceStableSpan(Symbol symbol)
    {
        if (symbol.Span.Equals(default) || symbol.Span.Length <= 0)
        {
            return NoSourceSpan;
        }

        var filePath = string.IsNullOrWhiteSpace(symbol.Span.FilePath)
            ? ""
            : NormalizeSourcePath(symbol.Span.FilePath);
        return $"{filePath}:{symbol.Span.Position}:{symbol.Span.Length}";
    }

    private static string GetOverloadDiscriminator(SymbolTable symbolTable, Symbol symbol)
    {
        var parts = new List<string>();
        if (symbol is FuncSymbol function)
        {
            var arity = function.Parameters.Count > 0
                ? function.Parameters.Count
                : function.ParamTypes.Count;
            parts.Add($"arity:{arity}:typeParams:{function.TypeParams.Count}:hasBody:{function.HasBody}");
        }

        var ownerId = GetSemanticOwnerId(symbol);
        if (ownerId.IsValid)
        {
            parts.Add($"owner:{BuildSemanticOwnerPath(symbolTable, ownerId, [])}");
        }

        return string.Join(";", parts);
    }

    private static SymbolId GetSemanticOwnerId(Symbol symbol) => symbol switch
    {
        AdtSymbol { ParentAdt.IsValid: true } adt => adt.ParentAdt,
        CtorSymbol { OwnerAdt.IsValid: true } constructor => constructor.OwnerAdt,
        FieldSymbol { OwnerType.IsValid: true } field => field.OwnerType,
        FuncSymbol { OwnerTrait: { IsValid: true } ownerTrait } => ownerTrait,
        AssociatedItemSymbol { OwnerImpl.IsValid: true } item => item.OwnerImpl,
        AssociatedItemSymbol { OwnerTrait.IsValid: true } item => item.OwnerTrait,
        _ => SymbolId.None
    };

    private static string BuildSemanticOwnerPath(
        SymbolTable symbolTable,
        SymbolId ownerId,
        HashSet<SymbolId> visited)
    {
        if (!visited.Add(ownerId))
        {
            return "<owner-cycle>";
        }

        if (symbolTable.GetSymbol(ownerId) is not { } owner)
        {
            return "<missing-owner>";
        }

        var parentId = GetSemanticOwnerId(owner);
        var current = $"{owner.Kind}:{owner.Name}";
        return parentId.IsValid
            ? $"{BuildSemanticOwnerPath(symbolTable, parentId, visited)}/{current}"
            : current;
    }

    private static string GetSymbolRole(SymbolTable symbolTable, Symbol symbol)
    {
        if (symbol is ModuleSymbol)
        {
            return "module";
        }

        if (symbolTable.Modules.TryGetOwningModule(symbol.Id, out _))
        {
            return "module-member";
        }

        return symbol switch
        {
            TypeParamSymbol => "type-parameter",
            VarSymbol { IsParameter: true } => "parameter",
            FieldSymbol => "field",
            FuncSymbol { OwnerTrait: { IsValid: true } } => "trait-method",
            AssociatedTypeSymbol => "associated-type",
            AssociatedConstSymbol => "associated-const",
            ImplSymbol => "impl",
            _ => "symbol"
        };
    }

    private static Dictionary<string, LiveStateSymbolIdentity> BuildUniqueMap(
        IReadOnlyList<LiveStateSymbolIdentity> identities,
        string label,
        List<string> failures)
    {
        var result = new Dictionary<string, LiveStateSymbolIdentity>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            var key = identity.StableKey.ToString();
            if (result.TryGetValue(key, out var existing))
            {
                if (string.Equals(identity.SymbolKind, nameof(SymbolKind.Module), StringComparison.Ordinal) &&
                    string.Equals(existing.SymbolKind, nameof(SymbolKind.Module), StringComparison.Ordinal))
                {
                    if (identity.SymbolId < existing.SymbolId)
                    {
                        result[key] = identity;
                    }

                    continue;
                }

                failures.Add($"duplicate-{label}-symbol-key:{key}");
                continue;
            }

            result[key] = identity;
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyList<LiveStateSymbolIdentity>> BuildPreviousMap(
        IReadOnlyList<LiveStateSymbolIdentity> identities,
        List<string> failures)
    {
        var result = new Dictionary<string, IReadOnlyList<LiveStateSymbolIdentity>>(StringComparer.Ordinal);
        foreach (var group in identities.GroupBy(
                     static identity => identity.StableKey.ToString(),
                     StringComparer.Ordinal))
        {
            var entries = group.ToArray();
            if (entries.Length > 1 &&
                entries.Any(static identity =>
                    !string.Equals(identity.SymbolKind, nameof(SymbolKind.Module), StringComparison.Ordinal)))
            {
                failures.Add($"duplicate-previous-symbol-key:{group.Key}");
                continue;
            }

            result[group.Key] = entries;
        }

        return result;
    }

    private static Dictionary<int, LiveStateSymbolIdentity> BuildUniqueIdMap(
        IReadOnlyList<LiveStateSymbolIdentity> identities,
        string label,
        List<string> failures)
    {
        var result = new Dictionary<int, LiveStateSymbolIdentity>();
        foreach (var identity in identities)
        {
            if (!result.TryAdd(identity.SymbolId, identity))
            {
                failures.Add($"duplicate-{label}-symbol-id:{identity.SymbolId}");
            }
        }

        return result;
    }
}
