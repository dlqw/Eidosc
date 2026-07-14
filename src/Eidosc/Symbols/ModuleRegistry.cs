using Eidosc.Utils;

namespace Eidosc.Symbols;

/// <summary>
/// 模块注册表 - 管理所有已知模块
/// </summary>
public sealed class ModuleRegistry
{
    /// <summary>
    /// 根模块映射 (顶级模块名 -> 模块符号)
    /// </summary>
    private readonly Dictionary<string, SymbolId> _rootModules = new();

    /// <summary>
    /// 所有模块符号 (符号 ID -> 模块符号)
    /// </summary>
    private readonly Dictionary<SymbolId, ModuleSymbol> _modules = new();

    /// <summary>
    /// 用户可见模块显示路径 -> 符号 ID。
    /// Namespace 路径统一使用点号；依赖 package alias 作为首段，例如 "pkg.Module.Path"。
    /// </summary>
    private readonly Dictionary<string, SymbolId> _modulePaths = new();

    /// <summary>
    /// 结构化 module identity key -> 符号 ID。
    /// 包含 package alias、package instance key 和 module path。
    /// </summary>
    private readonly Dictionary<string, SymbolId> _moduleIdentityKeys = new();
    private readonly Dictionary<string, List<SymbolId>> _moduleCandidatesByPath = new();

    /// <summary>
    /// 符号表引用
    /// </summary>
    private readonly SymbolTable _symbolTable;
    private readonly Dictionary<SymbolId, List<SymbolId>> _memberOwnerModules = new();
    private readonly Dictionary<AccessibleBindingCacheKey, AccessibleBindingIndex> _accessibleBindingsCache = new();
    private int _accessibleBindingsCacheHits;
    private int _accessibleBindingsCacheMisses;
    private int _accessibleBindingNameIndexHits;
    private int _accessibleBindingNameIndexMisses;
    private int _memberOwnerIndexHits;
    private int _memberOwnerIndexMisses;

    private readonly record struct AccessibleBindingCacheKey(
        SymbolId ModuleId,
        AccessibleBindingVisibility Visibility);

    private enum AccessibleBindingVisibility
    {
        External,
        SameModuleOrPackage,
        ExplicitExportsOnly
    }

    private sealed record AccessibleBindingIndex(
        ModuleBindingEntry[] Bindings,
        IReadOnlyDictionary<string, ModuleBindingEntry[]> ByName);

    public ModuleRegistry(SymbolTable symbolTable)
    {
        _symbolTable = symbolTable;
    }

    /// <summary>
    /// 注册模块
    /// </summary>
    public void RegisterModule(ModuleSymbol module, SymbolId id)
    {
        InvalidateAccessibleBindingCache();
        _modules[id] = module;

        // 注册路径
        var pathKey = module.Identity.ToDisplayKey();
        _modulePaths[pathKey] = id;
        _moduleIdentityKeys[module.Identity.ToIdentityKey()] = id;
        var unqualifiedPathKey = ToModuleKey(null, module.Path);
        if (!_moduleCandidatesByPath.TryGetValue(unqualifiedPathKey, out var candidates))
        {
            candidates = [];
            _moduleCandidatesByPath[unqualifiedPathKey] = candidates;
        }

        if (!candidates.Contains(id))
        {
            candidates.Add(id);
            candidates.Sort((left, right) => string.Compare(
                FormatModuleFullName(left),
                FormatModuleFullName(right),
                StringComparison.Ordinal));
        }

        // 注册根模块
        if (module.Path.Count > 0)
        {
            var rootName = module.Path[0];
            if (!_rootModules.ContainsKey(rootName))
            {
                _rootModules[rootName] = id;
            }
        }

        foreach (var memberId in module.Members)
        {
            AddMemberOwner(moduleId: id, memberId);
        }
    }

    /// <summary>
    /// 按路径查找模块
    /// </summary>
    public SymbolId? LookupModuleByPath(IReadOnlyList<string> path)
    {
        var pathKey = ToModuleKey(null, path);
        return _modulePaths.TryGetValue(pathKey, out var id) ? id : null;
    }

    public SymbolId? LookupModuleByPath(string? packageAlias, IReadOnlyList<string> path)
    {
        var pathKey = ToModuleKey(packageAlias, path);
        return _modulePaths.TryGetValue(pathKey, out var id) ? id : null;
    }

    public List<SymbolId> LookupModuleCandidatesByPath(IReadOnlyList<string> path)
    {
        var pathKey = ToModuleKey(null, path);
        return _moduleCandidatesByPath.TryGetValue(pathKey, out var candidates)
            ? [.. candidates]
            : [];
    }

    public List<SymbolId> LookupImportModuleCandidates(string? packageAlias, IReadOnlyList<string> path)
    {
        if (!string.IsNullOrWhiteSpace(packageAlias))
        {
            var explicitModule = LookupModuleByPath(packageAlias, path);
            return explicitModule is { IsValid: true }
                ? [explicitModule.Value]
                : [];
        }

        return LookupModuleCandidatesByPath(path);
    }

    /// <summary>
    /// 按名称查找根模块
    /// </summary>
    public SymbolId? LookupRootModule(string name)
    {
        return _rootModules.TryGetValue(name, out var id) ? id : null;
    }

    /// <summary>
    /// 获取模块符号
    /// </summary>
    public ModuleSymbol? GetModule(SymbolId id)
    {
        return _modules.TryGetValue(id, out var module) ? module : null;
    }

    /// <summary>
    /// 获取模块成员
    /// </summary>
    public List<SymbolId> GetModuleMembers(SymbolId moduleId)
    {
        if (_modules.TryGetValue(moduleId, out var module))
        {
            return module.Members;
        }
        return [];
    }

    /// <summary>
    /// 获取模块显式导出作用域。
    /// </summary>
    public List<ModuleBindingEntry> GetModuleExports(SymbolId moduleId)
    {
        if (_modules.TryGetValue(moduleId, out var module))
        {
            return module.ExportedBindings;
        }

        return [];
    }

    /// <summary>
    /// 添加成员到模块
    /// </summary>
    public void AddMemberToModule(SymbolId moduleId, SymbolId memberId)
    {
        if (_modules.TryGetValue(moduleId, out var module))
        {
            if (!module.Members.Contains(memberId))
            {
                InvalidateAccessibleBindingCache();
                module.Members.Add(memberId);
            }

            if (!_memberOwnerModules.TryGetValue(memberId, out var ownerModules))
            {
                ownerModules = AddMemberOwner(moduleId, memberId);
            }

            if (!ownerModules.Contains(moduleId))
            {
                ownerModules.Add(moduleId);
            }
        }
    }

    private List<SymbolId> AddMemberOwner(SymbolId moduleId, SymbolId memberId)
    {
        if (!moduleId.IsValid || !memberId.IsValid)
        {
            return [];
        }

        if (!_memberOwnerModules.TryGetValue(memberId, out var ownerModules))
        {
            ownerModules = [];
            _memberOwnerModules[memberId] = ownerModules;
        }

        if (!ownerModules.Contains(moduleId))
        {
            ownerModules.Add(moduleId);
        }

        return ownerModules;
    }

    public bool TryGetOwningModuleId(SymbolId memberId, out SymbolId moduleId)
    {
        if (TryGetOwningModuleIds(memberId, out var moduleIds))
        {
            moduleId = moduleIds[0];
            return true;
        }

        moduleId = SymbolId.None;
        return false;
    }

    public bool TryGetOwningModule(SymbolId memberId, out ModuleSymbol module)
    {
        module = null!;
        if (!TryGetOwningModuleIds(memberId, out var moduleIds))
        {
            return false;
        }

        foreach (var moduleId in moduleIds)
        {
            if (_modules.TryGetValue(moduleId, out var candidateModule))
            {
                module = candidateModule;
                return true;
            }
        }

        module = null!;
        return false;
    }

    public IReadOnlyList<SymbolId> GetOwningModuleIds(SymbolId memberId)
    {
        return TryGetOwningModuleIds(memberId, out var moduleIds)
            ? moduleIds
            : [];
    }

    public void SetUsesExplicitExports(SymbolId moduleId, bool usesExplicitExports)
    {
        if (_modules.TryGetValue(moduleId, out var module))
        {
            InvalidateAccessibleBindingCache();
            _modules[moduleId] = module with { UsesExplicitExports = usesExplicitExports };
        }
    }

    public bool TryAddExportToModule(SymbolId moduleId, ModuleBindingEntry binding)
    {
        if (!_modules.TryGetValue(moduleId, out var module) ||
            string.IsNullOrWhiteSpace(binding.Name) ||
            !binding.SymbolId.IsValid)
        {
            return false;
        }

        var existing = module.ExportedBindings.FirstOrDefault(entry =>
            string.Equals(entry.Name, binding.Name, StringComparison.Ordinal));
        if (existing != null)
        {
            if (existing.SymbolId == binding.SymbolId &&
                existing.Kind == binding.Kind)
            {
                return true;
            }

            if (!CanMergeSameNameExport(existing, binding))
            {
                return false;
            }
        }

        InvalidateAccessibleBindingCache();
        module.ExportedBindings.Add(binding);
        return true;
    }

    public void InvalidateAccessibleBindingCache()
    {
        _accessibleBindingsCache.Clear();
    }

    public IReadOnlyDictionary<string, long> GetProfilingCounters()
    {
        return new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["Namer.moduleRegistry.accessibleBindingsCache.entries"] = _accessibleBindingsCache.Count,
            ["Namer.moduleRegistry.accessibleBindingsCache.hits"] = _accessibleBindingsCacheHits,
            ["Namer.moduleRegistry.accessibleBindingsCache.misses"] = _accessibleBindingsCacheMisses,
            ["Namer.moduleRegistry.accessibleBindingNameIndex.hits"] = _accessibleBindingNameIndexHits,
            ["Namer.moduleRegistry.accessibleBindingNameIndex.misses"] = _accessibleBindingNameIndexMisses,
            ["Namer.moduleRegistry.memberOwnerIndex.entries"] = _memberOwnerModules.Count,
            ["Namer.moduleRegistry.memberOwnerIndex.hits"] = _memberOwnerIndexHits,
            ["Namer.moduleRegistry.memberOwnerIndex.misses"] = _memberOwnerIndexMisses
        };
    }

    private bool TryGetOwningModuleIds(SymbolId memberId, out List<SymbolId> moduleIds)
    {
        if (memberId.IsValid &&
            _memberOwnerModules.TryGetValue(memberId, out moduleIds!) &&
            moduleIds.Count > 0)
        {
            _memberOwnerIndexHits++;
            return true;
        }

        _memberOwnerIndexMisses++;
        moduleIds = null!;
        return false;
    }

    private bool CanMergeSameNameExport(ModuleBindingEntry existing, ModuleBindingEntry candidate)
    {
        return existing.Kind == ResolutionKind.Value &&
               candidate.Kind == ResolutionKind.Value &&
               _symbolTable.GetSymbol(existing.SymbolId) is FuncSymbol &&
               _symbolTable.GetSymbol(candidate.SymbolId) is FuncSymbol;
    }

    public bool TryLookupAccessibleBinding(
        SymbolId moduleId,
        string name,
        SymbolId? requesterModuleId,
        out ModuleBindingEntry binding)
    {
        return TryLookupAccessibleBinding(moduleId, name, requesterModuleId, allowedKinds: null, out binding);
    }

    public bool TryLookupAccessibleBinding(
        SymbolId moduleId,
        string name,
        SymbolId? requesterModuleId,
        IReadOnlySet<ResolutionKind>? allowedKinds,
        out ModuleBindingEntry binding)
    {
        binding = null!;
        foreach (var candidate in GetAccessibleBindingsByName(moduleId, name, requesterModuleId))
        {
            if (allowedKinds != null && !allowedKinds.Contains(candidate.Kind))
            {
                continue;
            }

            binding = candidate;
            return true;
        }

        return false;
    }

    public IReadOnlyList<ModuleBindingEntry> GetAccessibleBindingsByName(
        SymbolId moduleId,
        string name,
        SymbolId? requesterModuleId)
    {
        return GetAccessibleBindingsByName(moduleId, name, requesterModuleId, allowedKinds: null);
    }

    public IReadOnlyList<ModuleBindingEntry> GetAccessibleBindingsByName(
        SymbolId moduleId,
        string name,
        SymbolId? requesterModuleId,
        IReadOnlySet<ResolutionKind>? allowedKinds)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var index = GetAccessibleBindingIndex(moduleId, requesterModuleId);
        if (index.ByName.TryGetValue(name, out var bindings))
        {
            _accessibleBindingNameIndexHits++;
            if (allowedKinds == null)
            {
                return bindings;
            }

            var filtered = bindings
                .Where(binding => allowedKinds.Contains(binding.Kind))
                .ToArray();
            return filtered;
        }

        _accessibleBindingNameIndexMisses++;
        return [];
    }

    public IReadOnlyList<ModuleBindingEntry> GetAccessibleBindings(
        SymbolId moduleId,
        SymbolId? requesterModuleId)
    {
        return GetAccessibleBindingIndex(moduleId, requesterModuleId).Bindings;
    }

    private AccessibleBindingIndex GetAccessibleBindingIndex(
        SymbolId moduleId,
        SymbolId? requesterModuleId)
    {
        if (!_modules.TryGetValue(moduleId, out var module))
        {
            return new AccessibleBindingIndex([], new Dictionary<string, ModuleBindingEntry[]>(StringComparer.Ordinal));
        }

        var key = CreateAccessibleBindingCacheKey(moduleId, module, requesterModuleId);
        if (_accessibleBindingsCache.TryGetValue(key, out var cached))
        {
            _accessibleBindingsCacheHits++;
            return cached;
        }

        _accessibleBindingsCacheMisses++;
        var (sameModule, samePackage) = GetRequesterVisibility(moduleId, module, requesterModuleId);
        var bindings = BuildAccessibleBindings(module, sameModule, samePackage);
        var index = new AccessibleBindingIndex(bindings, BuildAccessibleBindingNameIndex(bindings));
        _accessibleBindingsCache[key] = index;
        return index;
    }

    private static IReadOnlyDictionary<string, ModuleBindingEntry[]> BuildAccessibleBindingNameIndex(
        ModuleBindingEntry[] bindings)
    {
        if (bindings.Length == 0)
        {
            return new Dictionary<string, ModuleBindingEntry[]>(StringComparer.Ordinal);
        }

        return bindings
            .GroupBy(static binding => binding.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
    }

    private AccessibleBindingCacheKey CreateAccessibleBindingCacheKey(
        SymbolId moduleId,
        ModuleSymbol module,
        SymbolId? requesterModuleId)
    {
        var (sameModule, samePackage) = GetRequesterVisibility(moduleId, module, requesterModuleId);
        var visibility = sameModule || samePackage
            ? AccessibleBindingVisibility.SameModuleOrPackage
            : module.UsesExplicitExports
                ? AccessibleBindingVisibility.ExplicitExportsOnly
                : AccessibleBindingVisibility.External;
        return new AccessibleBindingCacheKey(moduleId, visibility);
    }

    private (bool SameModule, bool SamePackage) GetRequesterVisibility(
        SymbolId moduleId,
        ModuleSymbol module,
        SymbolId? requesterModuleId)
    {
        var sameModule = requesterModuleId.HasValue &&
                         requesterModuleId.Value.IsValid &&
                         requesterModuleId.Value == moduleId;
        var samePackage = IsSamePackage(module, requesterModuleId);
        return (sameModule, samePackage);
    }

    private ModuleBindingEntry[] BuildAccessibleBindings(
        ModuleSymbol module,
        bool sameModule,
        bool samePackage)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ModuleBindingEntry>();

        if (!sameModule && !samePackage && module.UsesExplicitExports)
        {
            foreach (var binding in module.ExportedBindings)
            {
                if (seen.Add(GetAccessibleBindingDedupKey(binding)))
                {
                    result.Add(binding);
                }
            }

            return [.. result];
        }

        foreach (var binding in EnumerateDirectMemberBindings(module))
        {
            if (!sameModule &&
                !samePackage &&
                (module.UsesExplicitExports || !IsPublicSymbol(binding.SymbolId)))
            {
                continue;
            }

            if (seen.Add(GetAccessibleBindingDedupKey(binding)))
            {
                result.Add(binding);
            }
        }

        foreach (var binding in EnumerateSyntheticPackageBindings(module))
        {
            if (seen.Add(GetAccessibleBindingDedupKey(binding)))
            {
                result.Add(binding);
            }
        }

        if (sameModule || samePackage || module.UsesExplicitExports)
        {
            foreach (var binding in module.ExportedBindings)
            {
                if (seen.Add(GetAccessibleBindingDedupKey(binding)))
                {
                    result.Add(binding);
                }
            }
        }

        return [.. result];
    }

    private string GetAccessibleBindingDedupKey(ModuleBindingEntry binding)
    {
        return _symbolTable.GetSymbol(binding.SymbolId) is FuncSymbol
            ? $"{binding.Name}#{binding.Kind}#{binding.SymbolId.Value}"
            : $"{binding.Name}#{binding.Kind}";
    }

    private bool IsSamePackage(ModuleSymbol module, SymbolId? requesterModuleId)
    {
        if (!requesterModuleId.HasValue ||
            !requesterModuleId.Value.IsValid ||
            !_modules.TryGetValue(requesterModuleId.Value, out var requesterModule))
        {
            return false;
        }

        return string.Equals(module.PackageAlias, "Std", StringComparison.Ordinal) &&
               string.Equals(requesterModule.PackageAlias, "Std", StringComparison.Ordinal) &&
               string.Equals(module.PackageAlias, requesterModule.PackageAlias, StringComparison.Ordinal) &&
               string.Equals(module.PackageInstanceKey, requesterModule.PackageInstanceKey, StringComparison.Ordinal);
    }

    private IEnumerable<ModuleBindingEntry> EnumerateSyntheticPackageBindings(ModuleSymbol module)
    {
        if (!string.Equals(module.PackageAlias, "Std", StringComparison.Ordinal) ||
            module.Path.Count != 1 ||
            !string.Equals(module.Path[0], WellKnownStrings.BuiltinTypes.Seq, StringComparison.Ordinal))
        {
            yield break;
        }

        var listType = _symbolTable.Symbols
            .Where(static entry => entry.Value is AdtSymbol { Name: WellKnownStrings.BuiltinTypes.Seq } symbol &&
                                   symbol.Span.Equals(SourceSpan.Empty))
            .Select(static entry => entry.Key)
            .FirstOrDefault();
        if (!listType.IsValid)
        {
            yield break;
        }

        yield return new ModuleBindingEntry
        {
            Name = WellKnownStrings.BuiltinTypes.Seq,
            SymbolId = listType,
            Kind = ResolutionKind.Type
        };
    }

    private IEnumerable<ModuleBindingEntry> EnumerateDirectMemberBindings(ModuleSymbol module)
    {
        foreach (var memberId in module.Members)
        {
            var symbol = _symbolTable.GetSymbol(memberId);
            if (symbol == null || string.IsNullOrWhiteSpace(symbol.Name))
            {
                continue;
            }

            yield return new ModuleBindingEntry
            {
                Name = symbol.Name,
                SymbolId = memberId,
                Kind = GetResolutionKind(symbol)
            };
        }
    }

    private bool IsPublicSymbol(SymbolId symbolId)
    {
        return _symbolTable.GetSymbol(symbolId)?.IsPublic == true;
    }

    private static ResolutionKind GetResolutionKind(Symbol symbol)
    {
        return symbol switch
        {
            FuncSymbol => ResolutionKind.Value,
            VarSymbol => ResolutionKind.Value,
            AdtSymbol => ResolutionKind.Type,
            CtorSymbol => ResolutionKind.Constructor,
            TraitSymbol => ResolutionKind.Type,
            EffectSymbol => ResolutionKind.Effect,
            ModuleSymbol => ResolutionKind.Module,
            // ProofSymbol removed
            _ => ResolutionKind.Value
        };
    }

    /// <summary>
    /// 获取所有根模块
    /// </summary>
    public IReadOnlyDictionary<string, SymbolId> RootModules => _rootModules;

    /// <summary>
    /// 获取所有模块路径
    /// </summary>
    public IReadOnlyDictionary<string, SymbolId> ModulePaths => _modulePaths;

    public IReadOnlyDictionary<string, SymbolId> ModuleIdentityKeys => _moduleIdentityKeys;

    public IReadOnlyDictionary<SymbolId, ModuleSymbol> Modules => _modules;

    public IReadOnlyDictionary<string, IReadOnlyList<SymbolId>> ModuleCandidatesByPath =>
        _moduleCandidatesByPath.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<SymbolId>)entry.Value,
            StringComparer.Ordinal);

    public IReadOnlyDictionary<SymbolId, IReadOnlyList<SymbolId>> MemberOwnerModules =>
        _memberOwnerModules.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<SymbolId>)entry.Value);

    public string FormatModuleFullName(SymbolId moduleId)
    {
        return _modules.TryGetValue(moduleId, out var module)
            ? FormatModuleFullName(module)
            : $"<module#{moduleId.Value}>";
    }

    public static string FormatModuleFullName(ModuleSymbol module)
    {
        return module.Identity.ToDisplayKey();
    }

    public static string ToModuleKey(string? packageAlias, IReadOnlyList<string> modulePath)
    {
        return ModuleIdentity.Create(packageAlias, packageInstanceKey: null, modulePath).ToDisplayKey();
    }

    public static string ToModuleIdentityKey(
        string? packageAlias,
        string? packageInstanceKey,
        IReadOnlyList<string> modulePath)
    {
        return ModuleIdentity.Create(packageAlias, packageInstanceKey, modulePath).ToIdentityKey();
    }
}
