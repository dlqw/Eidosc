namespace Eidosc.Pipeline;

using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

public sealed record SymbolTableStateBuildResult(
    bool IsApplied,
    SymbolTable? SymbolTable,
    LiveStateRemapPlan? RemapPlan,
    LiveStateRemapPlan? SourceRemapPlan,
    IReadOnlyList<AstNamerStatePayload> NormalizedAstStates,
    int AppliedSymbols,
    int AppliedModules,
    int AppliedModuleMembers,
    int AppliedExports,
    int AppliedScopes,
    int AppliedGlobalBindings,
    IReadOnlyList<string> Failures);

public static class SymbolTableStateBuilder
{
    public static SymbolTableStateBuildResult BuildFromNamerPayload(
        ModuleNamerStatePayload payload) =>
        BuildFromNamerPayloads([payload]);

    public static SymbolTableStateBuildResult BuildFromNamerPayloads(
        IReadOnlyList<ModuleNamerStatePayload> payloads)
    {
        if (payloads.Count == 0)
        {
            return SymbolTableStateBuildResultBlocked(["missing-namer-state-payload"]);
        }

        var sourcePayloads = payloads
            .OrderBy(static payload => payload.ModuleKey, StringComparer.Ordinal)
            .ThenBy(static payload => payload.ModuleIdentityKey, StringComparer.Ordinal)
            .ToArray();
        var orderedPayloads = HaveCoherentSourceIds(sourcePayloads)
            ? sourcePayloads
            : NormalizePayloadIds(sourcePayloads);
        var symbolTable = new SymbolTable();
        var failures = new List<string>();
        ValidateCompatiblePayloads(orderedPayloads, failures);
        var previousIdentities = BuildUniqueSymbolIdentities(orderedPayloads, failures);
        if (failures.Count > 0)
        {
            return SymbolTableStateBuildResultBlocked(failures);
        }

        var currentIdentities = AllocateCurrentIdentities(symbolTable, previousIdentities);
        var remapResolution = LiveStateStableIdentityBuilder.PlanRemap(previousIdentities, currentIdentities);
        if (!remapResolution.IsValid)
        {
            return SymbolTableStateBuildResultBlocked(remapResolution.Failures);
        }

        var symbolRemap = remapResolution.Symbols.ToDictionary(
            static entry => entry.From,
            static entry => new SymbolId(entry.To));
        var typeRemap = remapResolution.Types.ToDictionary(
            static entry => entry.From,
            static entry => new TypeId(entry.To));
        var modulePayloads = ResolveModulePayloads(orderedPayloads, failures);
        if (failures.Count > 0)
        {
            return SymbolTableStateBuildResultBlocked(failures);
        }

        var identitiesByModule = orderedPayloads
            .GroupBy(static payload => payload.ModuleIdentityKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<LiveStateSymbolIdentity>)group
                    .SelectMany(static payload => payload.SymbolIdentities)
                    .Where(identity => BelongsToModule(identity, group.Key) &&
                                       identity.StableKey.SymbolRole == "module-member")
                    .GroupBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
                    .Select(static group => group.First())
                    .OrderBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var modulePayloadIds = modulePayloads.Select(static module => module.Id).ToHashSet();
        var moduleMemberIds = modulePayloads
            .SelectMany(static module => module.Members)
            .Concat(modulePayloads.SelectMany(static module => module.Exports.Select(static export => export.SymbolId)))
            .Concat(modulePayloads.SelectMany(static module => module.Imports))
            .Concat(identitiesByModule.Values.SelectMany(static identities => identities.Select(static identity => identity.SymbolId)))
            .ToHashSet();
        var symbolPayloadsById = orderedPayloads
            .SelectMany(static payload => payload.SymbolTable.Symbols)
            .GroupBy(static symbol => symbol.Id)
            .ToDictionary(static group => group.Key, static group => group.First());
        moduleMemberIds.UnionWith(orderedPayloads.SelectMany(static payload =>
            payload.SymbolIdentities.Select(static identity => identity.SymbolId)));
        moduleMemberIds.UnionWith(orderedPayloads.SelectMany(payload => payload.SymbolIdentities
            .Where(identity => IsImplForModule(identity, symbolPayloadsById, moduleMemberIds))
            .Select(static identity => identity.SymbolId)));
        moduleMemberIds.UnionWith(orderedPayloads.SelectMany(static payload =>
            ExtractScopeSymbolIds(payload.SymbolTable.Scopes)));
        var allowedSymbolIds = ExpandAllowedSymbolIds(
            moduleMemberIds.Concat(modulePayloadIds).ToHashSet(),
            symbolPayloadsById);
        var symbolPayloads = MergeSymbolPayloads(orderedPayloads, allowedSymbolIds);

        foreach (var symbol in symbolPayloads.Values
                     .OrderBy(static symbol => symbol.Id))
        {
            if (!TryCreateSymbol(symbol, symbolRemap, typeRemap, out var restored, out var failure))
            {
                failures.Add(failure);
                continue;
            }

            symbolTable.RegisterSymbol(restored);
        }

        foreach (var module in modulePayloads)
        {
            if (!symbolRemap.TryGetValue(module.Id, out var moduleId))
            {
                failures.Add($"missing-module-remap:{module.Id}");
                continue;
            }

            if (symbolTable.GetSymbol(moduleId) is not ModuleSymbol moduleSymbol)
            {
                failures.Add($"missing-module-symbol:{module.Id}");
                continue;
            }

            symbolTable.Modules.RegisterModule(moduleSymbol, moduleId);
            identitiesByModule.TryGetValue(module.IdentityKey, out var moduleIdentities);
            var moduleMembers = module.Members
                .Concat((moduleIdentities ?? [])
                    .Select(static identity => identity.SymbolId)
                    .Where(id => !modulePayloadIds.Contains(id)))
                .Distinct()
                .Where(id => id != module.Id)
                .Select(id => RemapSymbolId(id, symbolRemap))
                .Where(static id => id.IsValid);
            foreach (var member in moduleMembers)
            {
                symbolTable.AddMemberToModule(moduleId, member);
            }

            foreach (var export in module.Exports)
            {
                var remappedExport = RemapBinding(export, symbolRemap);
                if (remappedExport.SymbolId.IsValid)
                {
                    symbolTable.Modules.TryAddExportToModule(moduleId, remappedExport);
                }
            }
        }

        var primaryPayload = orderedPayloads[0];
        var restoredPayloadScopes = RestoreScopes(primaryPayload.SymbolTable.Scopes, symbolRemap, allowedSymbolIds);
        var restoredScopeSignatures = restoredPayloadScopes
            .Where(static scope => scope.Kind == ScopeKind.Module)
            .Select(CreateScopeBindingSignature)
            .ToHashSet(StringComparer.Ordinal);
        var syntheticModuleScopes = CreateModuleSurfaceScopes(symbolTable, modulePayloads, identitiesByModule, symbolRemap)
            .Where(scope => !restoredScopeSignatures.Contains(CreateScopeBindingSignature(scope)));
        var restoredScopes = restoredPayloadScopes
            .Concat(syntheticModuleScopes)
            .ToArray();
        var globalTypes = RestoreGlobalMap(primaryPayload.SymbolTable.GlobalTypes, symbolRemap, allowedSymbolIds);
        var globalTraits = RestoreGlobalMap(primaryPayload.SymbolTable.GlobalTraits, symbolRemap, allowedSymbolIds);
        var globalConstructors = RestoreGlobalMap(primaryPayload.SymbolTable.GlobalConstructors, symbolRemap, allowedSymbolIds);
        var globalAbilities = RestoreGlobalMap(primaryPayload.SymbolTable.GlobalAbilities, symbolRemap, allowedSymbolIds);
        AddModuleSurfaceGlobals(symbolTable, modulePayloads, symbolRemap, globalTypes, globalTraits, globalConstructors, globalAbilities);
        symbolTable.RestoreNamerBindings(restoredScopes, globalTypes, globalTraits, globalConstructors, globalAbilities);

        if (failures.Count > 0)
        {
            return new SymbolTableStateBuildResult(false, null, null, null, [], 0, 0, 0, 0, 0, 0, failures);
        }

        var remapPlan = LiveStateRemapPlan.FromResolution(remapResolution);
        var sourceRemapPlan = CreateSourceRemapPlan(
            sourcePayloads,
            previousIdentities,
            remapResolution,
            failures);
        if (sourceRemapPlan == null || failures.Count > 0)
        {
            return SymbolTableStateBuildResultBlocked(failures);
        }

        return new SymbolTableStateBuildResult(
            true,
            symbolTable,
            remapPlan,
            sourceRemapPlan,
            orderedPayloads.Select(static payload => payload.AstState).ToArray(),
            AppliedSymbols: allowedSymbolIds.Count(id => symbolRemap.ContainsKey(id)),
            AppliedModules: modulePayloads.Count,
            AppliedModuleMembers: modulePayloads.Sum(static module => module.Members.Count),
            AppliedExports: modulePayloads.Sum(static module => module.Exports.Count),
            AppliedScopes: restoredScopes.Length,
            AppliedGlobalBindings: globalTypes.Count() + globalTraits.Count() + globalConstructors.Count() + globalAbilities.Count(),
            []);
    }

    private static SymbolTableStateBuildResult SymbolTableStateBuildResultBlocked(IReadOnlyList<string> failures) =>
        new(false, null, null, null, [], 0, 0, 0, 0, 0, 0, failures);

    private static LiveStateRemapPlan? CreateSourceRemapPlan(
        IReadOnlyList<ModuleNamerStatePayload> sourcePayloads,
        IReadOnlyList<LiveStateSymbolIdentity> normalizedIdentities,
        LiveStateRemapResolution normalizedResolution,
        List<string> failures)
    {
        var normalizedByKey = normalizedIdentities.ToDictionary(
            static identity => identity.StableKey.ToString(),
            StringComparer.Ordinal);
        var currentSymbolByNormalized = normalizedResolution.Symbols.ToDictionary(
            static entry => entry.From,
            static entry => entry.To);
        var currentTypeByNormalized = normalizedResolution.Types.ToDictionary(
            static entry => entry.From,
            static entry => entry.To);
        var symbolRemaps = new SortedDictionary<int, int>();
        var typeRemaps = new SortedDictionary<int, int>();

        // Numeric source ids are local to one compilation and can collide across mixed payloads.
        // Normalized AST states already use merged ids; the primary payload supplies the next-phase seed map.
        var primarySourceIdentities = sourcePayloads[0].SymbolIdentities;
        foreach (var sourceIdentity in primarySourceIdentities
                     .GroupBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
                     .Select(static group => group.First())
                     .OrderBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal))
        {
            var stableKey = sourceIdentity.StableKey.ToString();
            if (!normalizedByKey.TryGetValue(stableKey, out var normalizedIdentity) ||
                !currentSymbolByNormalized.TryGetValue(normalizedIdentity.SymbolId, out var currentSymbolId))
            {
                failures.Add($"missing-source-symbol-remap:{sourceIdentity.SymbolId}");
                continue;
            }

            if (symbolRemaps.TryGetValue(sourceIdentity.SymbolId, out var existingSymbolId) &&
                existingSymbolId != currentSymbolId)
            {
                failures.Add($"ambiguous-source-symbol-remap:{sourceIdentity.SymbolId}->{existingSymbolId}|{currentSymbolId}");
            }
            else
            {
                symbolRemaps[sourceIdentity.SymbolId] = currentSymbolId;
            }

            if (sourceIdentity.TypeId <= 0 || normalizedIdentity.TypeId <= 0)
            {
                continue;
            }

            if (!currentTypeByNormalized.TryGetValue(normalizedIdentity.TypeId, out var currentTypeId))
            {
                failures.Add($"missing-source-type-remap:{sourceIdentity.TypeId}");
                continue;
            }

            if (typeRemaps.TryGetValue(sourceIdentity.TypeId, out var existingTypeId) &&
                existingTypeId != currentTypeId)
            {
                failures.Add($"ambiguous-source-type-remap:{sourceIdentity.TypeId}->{existingTypeId}|{currentTypeId}");
            }
            else
            {
                typeRemaps[sourceIdentity.TypeId] = currentTypeId;
            }
        }

        if (failures.Count > 0)
        {
            return null;
        }

        var isIdentity = symbolRemaps.All(static entry => entry.Key == entry.Value) &&
                         typeRemaps.All(static entry => entry.Key == entry.Value);
        return LiveStateRemapPlan.FromResolution(new LiveStateRemapResolution(
            true,
            isIdentity ? LiveStateRemapKind.Identity : LiveStateRemapKind.StableKey,
            symbolRemaps.Select(static entry => new LiveStateSymbolRemapEntry(entry.Key, entry.Value)).ToArray(),
            typeRemaps.Select(static entry => new LiveStateTypeRemapEntry(entry.Key, entry.Value)).ToArray(),
            []));
    }

    private static IReadOnlyList<ModuleNamerStatePayload> NormalizePayloadIds(
        IReadOnlyList<ModuleNamerStatePayload> payloads)
    {
        var nextSymbolId = 1;
        var nextTypeId = 1;
        var symbolIdsByStableKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var typeIdsByStableKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var symbolIdMaps = new List<Dictionary<int, int>>(payloads.Count);
        var typeIdMaps = new List<Dictionary<int, int>>(payloads.Count);

        for (var payloadIndex = 0; payloadIndex < payloads.Count; payloadIndex++)
        {
            var symbolIdMap = new Dictionary<int, int>();
            var typeIdMap = new Dictionary<int, int>();
            foreach (var identity in payloads[payloadIndex].SymbolIdentities
                         .OrderBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal))
            {
                var stableKey = identity.StableKey.ToString();
                if (!symbolIdsByStableKey.TryGetValue(stableKey, out var syntheticSymbolId))
                {
                    syntheticSymbolId = nextSymbolId++;
                    symbolIdsByStableKey[stableKey] = syntheticSymbolId;
                }

                symbolIdMap[identity.SymbolId] = syntheticSymbolId;
                if (identity.TypeId > 0)
                {
                    if (!typeIdsByStableKey.TryGetValue(stableKey, out var syntheticTypeId))
                    {
                        syntheticTypeId = nextTypeId++;
                        typeIdsByStableKey[stableKey] = syntheticTypeId;
                    }

                    typeIdMap[identity.TypeId] = syntheticTypeId;
                }
            }

            symbolIdMaps.Add(symbolIdMap);
            typeIdMaps.Add(typeIdMap);
        }

        for (var payloadIndex = 0; payloadIndex < payloads.Count; payloadIndex++)
        {
            var symbolIdMap = symbolIdMaps[payloadIndex];
            var typeIdMap = typeIdMaps[payloadIndex];
            foreach (var symbol in payloads[payloadIndex].SymbolTable.Symbols.OrderBy(static symbol => symbol.Id))
            {
                if (symbol.Id > 0 && !symbolIdMap.ContainsKey(symbol.Id))
                {
                    symbolIdMap[symbol.Id] = nextSymbolId++;
                }

                if (symbol.TypeId > 0 && !typeIdMap.ContainsKey(symbol.TypeId))
                {
                    typeIdMap[symbol.TypeId] = nextTypeId++;
                }

                foreach (var typeId in ExtractTypeIds(symbol.Facts))
                {
                    if (typeId > 0 && !typeIdMap.ContainsKey(typeId))
                    {
                        typeIdMap[typeId] = nextTypeId++;
                    }
                }
            }
        }

        return payloads
            .Select((payload, index) => NormalizePayloadIds(payload, symbolIdMaps[index], typeIdMaps[index]))
            .ToArray();
    }

    private static bool HaveCoherentSourceIds(IReadOnlyList<ModuleNamerStatePayload> payloads)
    {
        var identityByStableKey = new Dictionary<string, (int SymbolId, int TypeId)>(StringComparer.Ordinal);
        var stableKeyBySymbolId = new Dictionary<int, string>();
        foreach (var identity in payloads.SelectMany(static payload => payload.SymbolIdentities))
        {
            if (identity.SymbolId <= 0)
            {
                continue;
            }

            var stableKey = identity.StableKey.ToString();
            if (identityByStableKey.TryGetValue(stableKey, out var existingIdentity) &&
                existingIdentity != (identity.SymbolId, identity.TypeId))
            {
                return false;
            }

            identityByStableKey[stableKey] = (identity.SymbolId, identity.TypeId);
            if (stableKeyBySymbolId.TryGetValue(identity.SymbolId, out var existingStableKey) &&
                !string.Equals(existingStableKey, stableKey, StringComparison.Ordinal))
            {
                return false;
            }

            stableKeyBySymbolId[identity.SymbolId] = stableKey;
        }

        return payloads
            .SelectMany(static payload => payload.SymbolTable.Symbols)
            .All(symbol => stableKeyBySymbolId.ContainsKey(symbol.Id));
    }

    private static ModuleNamerStatePayload NormalizePayloadIds(
        ModuleNamerStatePayload payload,
        IReadOnlyDictionary<int, int> symbolIdMap,
        IReadOnlyDictionary<int, int> typeIdMap)
    {
        var normalizedSymbolTable = NormalizeSymbolTablePayload(payload.SymbolTable, symbolIdMap, typeIdMap);
        var normalizedModuleRegistry = NormalizeModuleRegistryPayload(payload.ModuleRegistry, symbolIdMap);
        var normalizedIdentitiesBySymbolId = payload.SymbolIdentities
            .Select(identity => identity with
            {
                SymbolId = RemapSyntheticSymbolId(identity.SymbolId, symbolIdMap),
                TypeId = RemapSyntheticTypeId(identity.TypeId, typeIdMap)
            })
            .Where(static identity => identity.SymbolId > 0)
            .GroupBy(static identity => identity.SymbolId)
            .ToDictionary(static group => group.Key, static group => group.First());
        foreach (var symbol in normalizedSymbolTable.Symbols.OrderBy(static symbol => symbol.Id))
        {
            if (symbol.Id <= 0 ||
                normalizedIdentitiesBySymbolId.ContainsKey(symbol.Id))
            {
                continue;
            }

            normalizedIdentitiesBySymbolId[symbol.Id] = CreateSyntheticIdentity(payload, symbol);
        }

        return payload with
        {
            SymbolIdentities = normalizedIdentitiesBySymbolId.Values
                .OrderBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
                .ToArray(),
            SymbolTable = normalizedSymbolTable,
            ModuleRegistry = normalizedModuleRegistry,
            AstState = payload.AstState.RemapSymbolIds(symbolIdMap)
        };
    }

    private static LiveStateSymbolIdentity CreateSyntheticIdentity(
        ModuleNamerStatePayload payload,
        SymbolPayload symbol)
    {
        var moduleKey = new LiveStateModuleStableKey(
            ModuleIdentity.CurrentPackageInstanceKey,
            payload.ModuleIdentityKey,
            "<payload>");
        var declKey = new LiveStateDeclStableKey(
            moduleKey,
            symbol.Kind,
            symbol.Name,
            $"synthetic:{symbol.Id}",
            $"{symbol.Span.FilePath}:{symbol.Span.Position}:{symbol.Span.Length}");
        var stableKey = new LiveStateSymbolStableKey(declKey, "payload-local");
        return new LiveStateSymbolIdentity(
            symbol.Id,
            symbol.Kind,
            symbol.Name,
            ShouldCreateSyntheticTypeRemap(symbol) ? symbol.TypeId : 0,
            stableKey);
    }

    private static bool ShouldCreateSyntheticTypeRemap(SymbolPayload symbol) =>
        symbol.Kind is nameof(SymbolKind.Adt) or
            nameof(SymbolKind.Constructor) or
            nameof(SymbolKind.Field) or
            nameof(SymbolKind.Trait) or
            nameof(SymbolKind.Effect) or
            nameof(SymbolKind.TypeParameter);

    private static SymbolTablePayload NormalizeSymbolTablePayload(
        SymbolTablePayload payload,
        IReadOnlyDictionary<int, int> symbolIdMap,
        IReadOnlyDictionary<int, int> typeIdMap) =>
        payload with
        {
            NextSymbolId = Math.Max(payload.NextSymbolId, symbolIdMap.Values.DefaultIfEmpty(0).Max() + 1),
            NextTypeId = Math.Max(payload.NextTypeId, typeIdMap.Values.DefaultIfEmpty(0).Max() + 1),
            Symbols = payload.Symbols
                .Select(symbol => NormalizeSymbolPayload(symbol, symbolIdMap, typeIdMap))
                .GroupBy(static symbol => symbol.Id)
                .Select(MergeNormalizedSymbolPayloads)
                .OrderBy(static symbol => symbol.Id)
                .ToArray(),
            Scopes = payload.Scopes
                .Select(scope => NormalizeScopePayload(scope, symbolIdMap))
                .ToArray(),
            GlobalTypes = NormalizeSymbolMap(payload.GlobalTypes, symbolIdMap),
            GlobalTraits = NormalizeSymbolMap(payload.GlobalTraits, symbolIdMap),
            GlobalConstructors = NormalizeSymbolMap(payload.GlobalConstructors, symbolIdMap),
            GlobalAbilities = NormalizeSymbolMap(payload.GlobalAbilities, symbolIdMap)
        };

    private static ModuleRegistryPayload NormalizeModuleRegistryPayload(
        ModuleRegistryPayload payload,
        IReadOnlyDictionary<int, int> symbolIdMap) =>
        payload with
        {
            RootModules = NormalizeSymbolMap(payload.RootModules, symbolIdMap),
            ModulePaths = NormalizeSymbolMap(payload.ModulePaths, symbolIdMap),
            ModuleIdentityKeys = NormalizeSymbolMap(payload.ModuleIdentityKeys, symbolIdMap),
            ModuleCandidatesByPath = NormalizeSymbolListMap(payload.ModuleCandidatesByPath, symbolIdMap),
            MemberOwnerModules = NormalizeMemberOwnerModules(payload.MemberOwnerModules, symbolIdMap),
            Modules = payload.Modules
                .Select(module => NormalizeModuleRegistryModulePayload(module, symbolIdMap))
                .GroupBy(static module => module.Id)
                .Select(MergeNormalizedModuleRegistryPayloads)
                .OrderBy(static module => module.Id)
                .ToArray()
        };

    private static SymbolPayload MergeNormalizedSymbolPayloads(IGrouping<int, SymbolPayload> group)
    {
        var payloads = group.ToArray();
        var preferred = payloads
            .OrderByDescending(static payload => GetSymbolPayloadInformationScore(payload))
            .ThenBy(static payload => payload.Span.FilePath, StringComparer.Ordinal)
            .ThenBy(static payload => payload.Span.Position)
            .First();
        if (!string.Equals(preferred.Kind, nameof(SymbolKind.Module), StringComparison.Ordinal))
        {
            return preferred;
        }

        var facts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in preferred.Facts)
        {
            facts[fact.Key] = fact.Value;
        }

        facts["members"] = MergeNormalizedIdFacts(payloads, "members", preferred.Id);
        facts["imports"] = MergeNormalizedIdFacts(payloads, "imports", preferred.Id);
        facts["exports"] = string.Join(",", payloads
            .SelectMany(static payload => SplitList(payload.Facts.GetValueOrDefault("exports", ""), ','))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
        facts["usesExplicitExports"] = payloads.Any(static payload =>
            ParseBool(payload.Facts.GetValueOrDefault("usesExplicitExports", ""))).ToString();
        facts["parentModule"] = payloads
            .Select(static payload => GetFactInt(payload, "parentModule"))
            .FirstOrDefault(parent => parent > 0 && parent != preferred.Id)
            .ToString();
        return preferred with
        {
            IsTypeResolved = payloads.Any(static payload => payload.IsTypeResolved),
            IsModuleLevel = payloads.Any(static payload => payload.IsModuleLevel),
            IsPublic = payloads.Any(static payload => payload.IsPublic),
            Facts = facts
        };
    }

    private static int GetSymbolPayloadInformationScore(SymbolPayload payload) =>
        payload.Facts.Sum(static fact => string.IsNullOrWhiteSpace(fact.Value) ? 0 : fact.Value.Length + 1) +
        (payload.IsTypeResolved ? 4 : 0) +
        (payload.IsModuleLevel ? 2 : 0) +
        (payload.IsPublic ? 1 : 0);

    private static string MergeNormalizedIdFacts(
        IReadOnlyList<SymbolPayload> payloads,
        string name,
        int selfId) =>
        string.Join(",", payloads
            .SelectMany(payload => ParseInts(payload.Facts.GetValueOrDefault(name, "")))
            .Where(id => id != selfId)
            .Distinct()
            .Order());

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> NormalizeMemberOwnerModules(
        IReadOnlyDictionary<int, IReadOnlyList<int>> values,
        IReadOnlyDictionary<int, int> symbolIdMap) =>
        values
            .Select(entry => new
            {
                MemberId = RemapSyntheticSymbolId(entry.Key, symbolIdMap),
                OwnerIds = entry.Value
                    .Select(id => RemapSyntheticSymbolId(id, symbolIdMap))
                    .Where(static id => id > 0)
                    .ToArray()
            })
            .Where(static entry => entry.MemberId > 0)
            .GroupBy(static entry => entry.MemberId)
            .Select(group => new KeyValuePair<int, IReadOnlyList<int>>(
                group.Key,
                group.SelectMany(static entry => entry.OwnerIds)
                    .Where(ownerId => ownerId != group.Key)
                    .Distinct()
                    .Order()
                    .ToArray()))
            .Where(static entry => entry.Value.Count > 0)
            .OrderBy(static entry => entry.Key)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value);

    private static SymbolPayload NormalizeSymbolPayload(
        SymbolPayload payload,
        IReadOnlyDictionary<int, int> symbolIdMap,
        IReadOnlyDictionary<int, int> typeIdMap) =>
        payload with
        {
            Id = RemapSyntheticSymbolId(payload.Id, symbolIdMap),
            TypeId = RemapSyntheticTypeId(payload.TypeId, typeIdMap),
            Facts = NormalizeSymbolFacts(payload.Facts, symbolIdMap, typeIdMap)
        };

    private static ScopePayload NormalizeScopePayload(
        ScopePayload payload,
        IReadOnlyDictionary<int, int> symbolIdMap) =>
        payload with
        {
            Bindings = NormalizeSymbolMap(payload.Bindings, symbolIdMap),
            FunctionOverloads = NormalizeSymbolListMap(payload.FunctionOverloads, symbolIdMap),
            Types = NormalizeSymbolMap(payload.Types, symbolIdMap),
            Traits = NormalizeSymbolMap(payload.Traits, symbolIdMap),
            Effects = NormalizeSymbolMap(payload.Effects, symbolIdMap),
            Constructors = NormalizeSymbolMap(payload.Constructors, symbolIdMap)
        };

    private static ModuleRegistryModulePayload NormalizeModuleRegistryModulePayload(
        ModuleRegistryModulePayload payload,
        IReadOnlyDictionary<int, int> symbolIdMap)
    {
        var id = RemapSyntheticSymbolId(payload.Id, symbolIdMap);
        return payload with
        {
            Id = id,
            Members = payload.Members
                .Select(memberId => RemapSyntheticSymbolId(memberId, symbolIdMap))
                .Where(memberId => memberId > 0 && memberId != id)
                .Distinct()
                .Order()
                .ToArray(),
            Exports = payload.Exports
                .Select(binding => binding with { SymbolId = RemapSyntheticSymbolId(binding.SymbolId, symbolIdMap) })
                .Where(static binding => binding.SymbolId > 0)
                .ToArray(),
            Imports = payload.Imports
                .Select(importId => RemapSyntheticSymbolId(importId, symbolIdMap))
                .Where(importId => importId > 0 && importId != id)
                .Distinct()
                .Order()
                .ToArray(),
            ParentModule = RemapSyntheticSymbolId(payload.ParentModule, symbolIdMap) is var parentId && parentId != id
                ? parentId
                : 0
        };
    }

    private static ModuleRegistryModulePayload MergeNormalizedModuleRegistryPayloads(
        IGrouping<int, ModuleRegistryModulePayload> group)
    {
        var payloads = group.ToArray();
        var preferred = payloads
            .OrderByDescending(static payload => payload.Members.Count + payload.Exports.Count + payload.Imports.Count)
            .ThenBy(static payload => payload.IdentityKey, StringComparer.Ordinal)
            .First();
        return preferred with
        {
            Members = payloads
                .SelectMany(static payload => payload.Members)
                .Where(memberId => memberId != preferred.Id)
                .Distinct()
                .Order()
                .ToArray(),
            Exports = payloads
                .SelectMany(static payload => payload.Exports)
                .Distinct()
                .OrderBy(static binding => binding.Name, StringComparer.Ordinal)
                .ThenBy(static binding => binding.Kind, StringComparer.Ordinal)
                .ThenBy(static binding => binding.SymbolId)
                .ToArray(),
            Imports = payloads
                .SelectMany(static payload => payload.Imports)
                .Where(importId => importId != preferred.Id)
                .Distinct()
                .Order()
                .ToArray(),
            ParentModule = payloads
                .Select(static payload => payload.ParentModule)
                .FirstOrDefault(parentId => parentId > 0 && parentId != preferred.Id),
            UsesExplicitExports = payloads.Any(static payload => payload.UsesExplicitExports)
        };
    }

    private static IReadOnlyDictionary<string, int> NormalizeSymbolMap(
        IReadOnlyDictionary<string, int> values,
        IReadOnlyDictionary<int, int> symbolIdMap) =>
        values
            .Select(entry => new KeyValuePair<string, int>(
                entry.Key,
                RemapSyntheticSymbolId(entry.Value, symbolIdMap)))
            .Where(static entry => entry.Value > 0)
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> NormalizeSymbolListMap(
        IReadOnlyDictionary<string, IReadOnlyList<int>> values,
        IReadOnlyDictionary<int, int> symbolIdMap) =>
        values
            .Select(entry => new KeyValuePair<string, IReadOnlyList<int>>(
                entry.Key,
                entry.Value.Select(id => RemapSyntheticSymbolId(id, symbolIdMap)).Where(static id => id > 0).Distinct().Order().ToArray()))
            .Where(static entry => entry.Value.Count > 0)
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> NormalizeSymbolFacts(
        IReadOnlyDictionary<string, string> facts,
        IReadOnlyDictionary<int, int> symbolIdMap,
        IReadOnlyDictionary<int, int> typeIdMap)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in facts)
        {
            result[name] = name switch
            {
                "typeParams" or "parameters" =>
                    NormalizePositionalSymbolFact(value, symbolIdMap),
                "ownerEffect" or "ownerTrait" or "constructors" or
                    "fields" or "ownerAdt" or "namedFields" or "ownerType" or "methods" or
                    "associatedTypes" or "parentTraits" or "operations" or "requiredAbilities" or
                    "traitConstraints" or "trait" =>
                    NormalizeSymbolFact(value, symbolIdMap),
                "members" or "imports" or "parentModule" =>
                    NormalizeSymbolFact(value, symbolIdMap),
                "exports" =>
                    NormalizeModuleBindingFact(value, symbolIdMap),
                "traitMethodImplementations" =>
                    NormalizeSymbolPairFact(value, symbolIdMap),
                "positionalArgs" or "paramTypes" =>
                    NormalizePositionalTypeFact(value, typeIdMap),
                "returnType" or "type" or "aliasTarget" or "fieldType" or "implementingType" =>
                    NormalizeTypeFact(value, typeIdMap),
                "typeArguments" =>
                    NormalizeTypePairFact(value, typeIdMap),
                _ => value
            };
        }

        return result;
    }

    private static IEnumerable<int> ExtractTypeIds(IReadOnlyDictionary<string, string> facts)
    {
        foreach (var (name, value) in facts)
        {
            switch (name)
            {
                case "returnType":
                case "type":
                case "aliasTarget":
                case "positionalArgs":
                case "fieldType":
                case "implementingType":
                case "paramTypes":
                    foreach (var id in ParseInts(value))
                    {
                        yield return id;
                    }

                    break;
                case "typeArguments":
                    foreach (var entry in SplitList(value, ','))
                    {
                        var parts = entry.Split("->", StringSplitOptions.None);
                        if (parts.Length != 2)
                        {
                            continue;
                        }

                        if (int.TryParse(parts[0], out var from))
                        {
                            yield return from;
                        }

                        if (int.TryParse(parts[1], out var to))
                        {
                            yield return to;
                        }
                    }

                    break;
            }
        }
    }

    private static string NormalizeSymbolFact(string value, IReadOnlyDictionary<int, int> symbolIdMap) =>
        string.Join(",", ParseInts(value)
            .Select(id => RemapSyntheticSymbolId(id, symbolIdMap))
            .Where(static id => id > 0));

    private static string NormalizePositionalSymbolFact(
        string value,
        IReadOnlyDictionary<int, int> symbolIdMap) =>
        string.Join(",", ParsePositionalIds(value)
            .Select(id => RemapSyntheticSymbolId(id, symbolIdMap)));

    private static string NormalizeTypeFact(string value, IReadOnlyDictionary<int, int> typeIdMap) =>
        string.Join(",", ParseInts(value)
            .Select(id => RemapSyntheticTypeId(id, typeIdMap))
            .Where(static id => id > 0));

    private static string NormalizePositionalTypeFact(
        string value,
        IReadOnlyDictionary<int, int> typeIdMap) =>
        string.Join(",", ParsePositionalIds(value)
            .Select(id => RemapSyntheticTypeId(id, typeIdMap)));

    private static string NormalizeSymbolPairFact(string value, IReadOnlyDictionary<int, int> symbolIdMap)
    {
        var pairs = new List<string>();
        foreach (var entry in SplitList(value, ','))
        {
            var parts = entry.Split("->", StringSplitOptions.None);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var from) ||
                !int.TryParse(parts[1], out var to))
            {
                continue;
            }

            var remappedFrom = RemapSyntheticSymbolId(from, symbolIdMap);
            var remappedTo = RemapSyntheticSymbolId(to, symbolIdMap);
            if (remappedFrom > 0 && remappedTo > 0)
            {
                pairs.Add($"{remappedFrom}->{remappedTo}");
            }
        }

        return string.Join(",", pairs.Order(StringComparer.Ordinal));
    }

    private static string NormalizeTypePairFact(string value, IReadOnlyDictionary<int, int> typeIdMap)
    {
        var pairs = new List<string>();
        foreach (var entry in SplitList(value, ','))
        {
            var parts = entry.Split("->", StringSplitOptions.None);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var from) ||
                !int.TryParse(parts[1], out var to))
            {
                continue;
            }

            var remappedFrom = RemapSyntheticTypeId(from, typeIdMap);
            var remappedTo = RemapSyntheticTypeId(to, typeIdMap);
            if (remappedFrom > 0 && remappedTo > 0)
            {
                pairs.Add($"{remappedFrom}->{remappedTo}");
            }
        }

        return string.Join(",", pairs.Order(StringComparer.Ordinal));
    }

    private static string NormalizeModuleBindingFact(string value, IReadOnlyDictionary<int, int> symbolIdMap)
    {
        var bindings = new List<string>();
        foreach (var part in SplitList(value, ','))
        {
            var fields = part.Split(':');
            if (fields.Length != 3 || !int.TryParse(fields[2], out var symbolId))
            {
                continue;
            }

            var remapped = RemapSyntheticSymbolId(symbolId, symbolIdMap);
            if (remapped > 0)
            {
                bindings.Add($"{fields[0]}:{fields[1]}:{remapped}");
            }
        }

        return string.Join(",", bindings.Order(StringComparer.Ordinal));
    }

    private static int RemapSyntheticSymbolId(int value, IReadOnlyDictionary<int, int> symbolIdMap) =>
        value <= 0 ? 0 : symbolIdMap.GetValueOrDefault(value);

    private static int RemapSyntheticTypeId(int value, IReadOnlyDictionary<int, int> typeIdMap) =>
        value <= 0 ? 0 : typeIdMap.GetValueOrDefault(value);

    private static void ValidateCompatiblePayloads(
        IReadOnlyList<ModuleNamerStatePayload> payloads,
        List<string> failures)
    {
        foreach (var payload in payloads)
        {
            if (!string.Equals(payload.SchemaVersion, ModuleNamerStatePayload.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                failures.Add($"unsupported-namer-payload-schema:{payload.ModuleKey}:{payload.SchemaVersion}");
            }
        }

    }

    private static IReadOnlyList<LiveStateSymbolIdentity> BuildUniqueSymbolIdentities(
        IReadOnlyList<ModuleNamerStatePayload> payloads,
        List<string> failures)
    {
        var byStableKey = new Dictionary<string, LiveStateSymbolIdentity>(StringComparer.Ordinal);
        foreach (var identity in payloads
                     .SelectMany(static payload => payload.SymbolIdentities)
                     .OrderBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal))
        {
            var key = identity.StableKey.ToString();
            if (!byStableKey.TryGetValue(key, out var existing))
            {
                byStableKey[key] = identity;
                continue;
            }

            if (existing.SymbolId != identity.SymbolId ||
                existing.TypeId != identity.TypeId ||
                !string.Equals(existing.SymbolKind, identity.SymbolKind, StringComparison.Ordinal) ||
                !string.Equals(existing.Name, identity.Name, StringComparison.Ordinal))
            {
                failures.Add($"duplicate-namer-symbol-key:{key}");
            }
        }

        var rawIds = new Dictionary<int, string>();
        foreach (var identity in byStableKey.Values.OrderBy(static identity => identity.SymbolId))
        {
            if (rawIds.TryGetValue(identity.SymbolId, out var existingKey) &&
                !string.Equals(existingKey, identity.StableKey.ToString(), StringComparison.Ordinal))
            {
                failures.Add($"ambiguous-namer-symbol-id:{identity.SymbolId}");
                continue;
            }

            rawIds[identity.SymbolId] = identity.StableKey.ToString();
        }

        return byStableKey.Values
            .OrderBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static bool BelongsToModule(
        LiveStateSymbolIdentity identity,
        string moduleIdentityKey)
    {
        var expected = ParseModuleIdentityKey(moduleIdentityKey);
        var module = identity.StableKey.Declaration.Module;
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
        var modulePath = moduleIdentityKey[(pathSeparator + 2)..];
        return (
            string.IsNullOrWhiteSpace(packageInstanceKey) ? ModuleIdentity.CurrentPackageInstanceKey : packageInstanceKey,
            modulePath);
    }

    private static bool IsImplForModule(
        LiveStateSymbolIdentity identity,
        IReadOnlyDictionary<int, SymbolPayload> symbolPayloads,
        IReadOnlySet<int> moduleSymbolIds)
    {
        if (identity.SymbolKind != SymbolKind.Impl.ToString() ||
            !symbolPayloads.TryGetValue(identity.SymbolId, out var impl))
        {
            return false;
        }

        if (impl.Facts.TryGetValue("trait", out var trait) &&
            int.TryParse(trait, out var traitId) &&
            moduleSymbolIds.Contains(traitId))
        {
            return true;
        }

        if (!impl.Facts.TryGetValue("methods", out var methods))
        {
            return false;
        }

        return ParseInts(methods).Any(moduleSymbolIds.Contains);
    }

    private static IReadOnlySet<int> ExpandAllowedSymbolIds(
        HashSet<int> allowedSymbolIds,
        IReadOnlyDictionary<int, SymbolPayload> symbolPayloads)
    {
        var queue = new Queue<int>(allowedSymbolIds.Order());
        while (queue.Count > 0)
        {
            var symbolId = queue.Dequeue();
            if (!symbolPayloads.TryGetValue(symbolId, out var symbol))
            {
                continue;
            }

            foreach (var referenced in ExtractSymbolIds(symbol.Facts))
            {
                if (referenced > 0 && allowedSymbolIds.Add(referenced))
                {
                    queue.Enqueue(referenced);
                }
            }
        }

        return allowedSymbolIds;
    }

    private static IEnumerable<int> ExtractSymbolIds(IReadOnlyDictionary<string, string> facts)
    {
        foreach (var (name, value) in facts)
        {
            switch (name)
            {
                case "typeParams":
                case "parameters":
                case "ownerEffect":
                case "ownerTrait":
                case "constructors":
                case "fields":
                case "ownerAdt":
                case "namedFields":
                case "ownerType":
                case "methods":
                case "associatedTypes":
                case "parentTraits":
                case "operations":
                case "requiredAbilities":
                case "traitConstraints":
                case "trait":
                case "members":
                case "imports":
                case "parentModule":
                    foreach (var id in ParseInts(value))
                    {
                        yield return id;
                    }

                    break;
                case "exports":
                    foreach (var binding in SplitList(value, ','))
                    {
                        var fields = binding.Split(':');
                        if (fields.Length == 3 && int.TryParse(fields[2], out var id))
                        {
                            yield return id;
                        }
                    }

                    break;
                case "traitMethodImplementations":
                    foreach (var entry in SplitList(value, ','))
                    {
                        var parts = entry.Split("->", StringSplitOptions.None);
                        if (parts.Length != 2)
                        {
                            continue;
                        }

                        if (int.TryParse(parts[0], out var from))
                        {
                            yield return from;
                        }

                        if (int.TryParse(parts[1], out var to))
                        {
                            yield return to;
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<int> ExtractScopeSymbolIds(IReadOnlyList<ScopePayload> scopes)
    {
        foreach (var scope in scopes)
        {
            foreach (var id in scope.Bindings.Values)
            {
                yield return id;
            }

            foreach (var id in scope.FunctionOverloads.Values.SelectMany(static ids => ids))
            {
                yield return id;
            }

            foreach (var id in scope.Types.Values)
            {
                yield return id;
            }

            foreach (var id in scope.Traits.Values)
            {
                yield return id;
            }

            foreach (var id in scope.Effects.Values)
            {
                yield return id;
            }

            foreach (var id in scope.Constructors.Values)
            {
                yield return id;
            }
        }
    }

    private static IReadOnlyList<ModuleRegistryModulePayload> ResolveModulePayloads(
        IReadOnlyList<ModuleNamerStatePayload> payloads,
        List<string> failures)
    {
        var modules = new Dictionary<string, ModuleRegistryModulePayload>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            var matches = payload.ModuleRegistry.Modules
                .Where(module =>
                    string.Equals(module.IdentityKey, payload.ModuleIdentityKey, StringComparison.Ordinal) ||
                    string.Equals(module.DisplayKey, payload.ModuleKey, StringComparison.Ordinal) ||
                    string.Equals(module.IdentityKey, payload.ModuleKey, StringComparison.Ordinal))
                .OrderBy(static module => module.IdentityKey, StringComparer.Ordinal)
                .ThenBy(static module => module.Id)
                .ToArray();
            if (matches.Length == 0)
            {
                failures.Add($"missing-module-payload:{payload.ModuleKey}");
                continue;
            }

            var expectedMemberNames = payload.MemberIndex.Members
                .Select(static member => member.Name)
                .ToHashSet(StringComparer.Ordinal);
            var symbolById = payload.SymbolTable.Symbols
                .ToDictionary(static symbol => symbol.Id);
            var module = matches
                .OrderByDescending(candidate => candidate.Members.Count(id =>
                    symbolById.TryGetValue(id, out var symbol) &&
                    expectedMemberNames.Contains(symbol.Name)))
                .ThenBy(candidate => candidate.Members.Count(id =>
                    !symbolById.TryGetValue(id, out var symbol) ||
                    !expectedMemberNames.Contains(symbol.Name)))
                .ThenBy(static candidate => candidate.Id)
                .First();
            if (!modules.TryAdd(module.IdentityKey, module) &&
                modules[module.IdentityKey].Id != module.Id)
            {
                failures.Add($"duplicate-module-payload:{module.IdentityKey}");
            }
        }

        return modules.Values
            .OrderBy(static module => module.IdentityKey, StringComparer.Ordinal)
            .ThenBy(static module => module.Id)
            .ToArray();
    }

    private static IReadOnlyDictionary<int, SymbolPayload> MergeSymbolPayloads(
        IReadOnlyList<ModuleNamerStatePayload> payloads,
        IReadOnlySet<int> allowedSymbolIds)
    {
        var symbols = new Dictionary<int, SymbolPayload>();
        foreach (var symbol in payloads
                     .SelectMany(static payload => payload.SymbolTable.Symbols)
                     .Where(symbol => allowedSymbolIds.Contains(symbol.Id))
                     .OrderBy(static symbol => symbol.Id))
        {
            symbols.TryAdd(symbol.Id, symbol);
        }

        return symbols;
    }

    private static IReadOnlyList<LiveStateSymbolIdentity> AllocateCurrentIdentities(
        SymbolTable symbolTable,
        IReadOnlyList<LiveStateSymbolIdentity> previous)
    {
        var nextSymbolId = symbolTable.NextSymbolIdValue;
        var nextTypeId = symbolTable.NextTypeIdValue;
        var existingByStableKey = LiveStateStableIdentityBuilder
            .BuildSymbolIdentities(symbolTable)
            .GroupBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        var allocatedTypeIds = new Dictionary<int, int>();
        return previous
            .OrderBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
            .Select(identity =>
            {
                var stableKey = identity.StableKey.ToString();
                if (existingByStableKey.TryGetValue(stableKey, out var existing))
                {
                    if (identity.TypeId > 0 && existing.TypeId > 0)
                    {
                        allocatedTypeIds.TryAdd(identity.TypeId, existing.TypeId);
                    }

                    return identity with
                    {
                        SymbolId = existing.SymbolId,
                        TypeId = existing.TypeId
                    };
                }

                var currentSymbolId = nextSymbolId++;
                var currentTypeId = 0;
                if (identity.TypeId > 0 &&
                    !allocatedTypeIds.TryGetValue(identity.TypeId, out currentTypeId))
                {
                    currentTypeId = nextTypeId++;
                    allocatedTypeIds[identity.TypeId] = currentTypeId;
                }

                return identity with
                {
                    SymbolId = currentSymbolId,
                    TypeId = currentTypeId
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<RestoredScopeBinding> RestoreScopes(
        IReadOnlyList<ScopePayload> scopes,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlySet<int> allowedSymbolIds) =>
        scopes
            .OrderBy(static scope => scope.Index)
            .Select(scope => new RestoredScopeBinding(
                scope.Index,
                scope.ParentIndex,
                ParseEnum(scope.Kind, ScopeKind.Block),
                RestoreMap(scope.Bindings, symbolRemap, allowedSymbolIds),
                RestoreFunctionOverloads(scope.FunctionOverloads, symbolRemap, allowedSymbolIds),
                RestoreMap(scope.Types, symbolRemap, allowedSymbolIds),
                RestoreMap(scope.Traits, symbolRemap, allowedSymbolIds),
                RestoreMap(scope.Effects, symbolRemap, allowedSymbolIds),
                RestoreMap(scope.Constructors, symbolRemap, allowedSymbolIds)))
            .Where(static scope => scope.Bindings.Count > 0 ||
                                   scope.FunctionOverloads.Count > 0 ||
                                   scope.Types.Count > 0 ||
                                   scope.Traits.Count > 0 ||
                                   scope.Effects.Count > 0 ||
                                   scope.Constructors.Count > 0)
            .ToArray();

    private static string CreateScopeBindingSignature(RestoredScopeBinding scope)
    {
        static string FormatMap(IReadOnlyDictionary<string, SymbolId> map) =>
            string.Join(",", map
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => $"{entry.Key}:{entry.Value.Value}"));

        var overloads = string.Join(",", scope.FunctionOverloads
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => $"{entry.Key}:[{string.Join("|", entry.Value.Select(static id => id.Value).Order())}]"));
        return $"{scope.Kind}|v={FormatMap(scope.Bindings)}|o={overloads}|t={FormatMap(scope.Types)}|tr={FormatMap(scope.Traits)}|a={FormatMap(scope.Effects)}|c={FormatMap(scope.Constructors)}";
    }

    private static IReadOnlyList<RestoredScopeBinding> CreateModuleSurfaceScopes(
        SymbolTable symbolTable,
        IReadOnlyList<ModuleRegistryModulePayload> modules,
        IReadOnlyDictionary<string, IReadOnlyList<LiveStateSymbolIdentity>> identitiesByModule,
        IReadOnlyDictionary<int, SymbolId> symbolRemap)
    {
        var result = new List<RestoredScopeBinding>(modules.Count);
        foreach (var module in modules.OrderBy(static module => module.IdentityKey, StringComparer.Ordinal))
        {
            var bindings = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
            var overloads = new Dictionary<string, List<SymbolId>>(StringComparer.Ordinal);
            var types = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
            var traits = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
            var abilities = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
            var constructors = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
            identitiesByModule.TryGetValue(module.IdentityKey, out var symbolIdentities);

            foreach (var oldMemberId in module.Members
                         .Concat((symbolIdentities ?? []).Select(static identity => identity.SymbolId))
                         .Distinct()
                         .Where(id => id != module.Id))
            {
                var memberId = RemapSymbolId(oldMemberId, symbolRemap);
                if (!memberId.IsValid || symbolTable.GetSymbol(memberId) is not { } symbol)
                {
                    continue;
                }

                if (!symbol.IsModuleLevel && symbol is not TypeParamSymbol)
                {
                    continue;
                }

                AddSymbolToScopeMaps(symbol, bindings, overloads, types, traits, abilities, constructors);
            }

            result.Add(new RestoredScopeBinding(
                Index: result.Count,
                ParentIndex: -1,
                ScopeKind.Module,
                bindings,
                overloads.ToDictionary(
                    static entry => entry.Key,
                    static entry => (IReadOnlyList<SymbolId>)entry.Value.OrderBy(static id => id.Value).ToArray(),
                    StringComparer.Ordinal),
                types,
                traits,
                abilities,
                constructors));
        }

        return result;
    }

    private static void AddSymbolToScopeMaps(
        Symbol symbol,
        Dictionary<string, SymbolId> bindings,
        Dictionary<string, List<SymbolId>> overloads,
        Dictionary<string, SymbolId> types,
        Dictionary<string, SymbolId> traits,
        Dictionary<string, SymbolId> abilities,
        Dictionary<string, SymbolId> constructors)
    {
        if (string.IsNullOrWhiteSpace(symbol.Name))
        {
            return;
        }

        switch (symbol)
        {
            case FuncSymbol:
                if (!overloads.TryGetValue(symbol.Name, out var candidates))
                {
                    candidates = [];
                    overloads[symbol.Name] = candidates;
                }

                if (!candidates.Contains(symbol.Id))
                {
                    candidates.Add(symbol.Id);
                }

                bindings.TryAdd(symbol.Name, symbol.Id);
                break;
            case VarSymbol:
                bindings.TryAdd(symbol.Name, symbol.Id);
                break;
            case AdtSymbol:
                types.TryAdd(symbol.Name, symbol.Id);
                break;
            case TraitSymbol:
                traits.TryAdd(symbol.Name, symbol.Id);
                types.TryAdd(symbol.Name, symbol.Id);
                break;
            case EffectSymbol:
                abilities.TryAdd(symbol.Name, symbol.Id);
                break;
            case CtorSymbol:
                constructors.TryAdd(symbol.Name, symbol.Id);
                break;
            case ModuleSymbol:
                bindings.TryAdd(symbol.Name, symbol.Id);
                break;
        }
    }

    private static IReadOnlyDictionary<string, SymbolId> RestoreGlobalMap(
        IReadOnlyDictionary<string, int> values,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlySet<int> allowedSymbolIds) =>
        RestoreMap(values, symbolRemap, allowedSymbolIds);

    private static void AddModuleSurfaceGlobals(
        SymbolTable symbolTable,
        IReadOnlyList<ModuleRegistryModulePayload> modules,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlyDictionary<string, SymbolId> globalTypes,
        IReadOnlyDictionary<string, SymbolId> globalTraits,
        IReadOnlyDictionary<string, SymbolId> globalConstructors,
        IReadOnlyDictionary<string, SymbolId> globalAbilities)
    {
        var mutableTypes = (Dictionary<string, SymbolId>)globalTypes;
        var mutableTraits = (Dictionary<string, SymbolId>)globalTraits;
        var mutableConstructors = (Dictionary<string, SymbolId>)globalConstructors;
        var mutableAbilities = (Dictionary<string, SymbolId>)globalAbilities;
        foreach (var memberId in modules.SelectMany(static module => module.Members).Select(id => RemapSymbolId(id, symbolRemap)))
        {
            if (!memberId.IsValid || symbolTable.GetSymbol(memberId) is not { } symbol)
            {
                continue;
            }

            switch (symbol)
            {
                case AdtSymbol:
                    mutableTypes.TryAdd(symbol.Name, memberId);
                    break;
                case TraitSymbol:
                    mutableTraits.TryAdd(symbol.Name, memberId);
                    mutableTypes.TryAdd(symbol.Name, memberId);
                    break;
                case EffectSymbol:
                    mutableAbilities.TryAdd(symbol.Name, memberId);
                    break;
                case CtorSymbol:
                    mutableConstructors.TryAdd(symbol.Name, memberId);
                    break;
            }
        }
    }

    private static IReadOnlyDictionary<string, SymbolId> RestoreMap(
        IReadOnlyDictionary<string, int> values,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlySet<int> allowedSymbolIds) =>
        values
            .Where(entry => allowedSymbolIds.Contains(entry.Value))
            .Select(entry => new KeyValuePair<string, SymbolId>(entry.Key, RemapSymbolId(entry.Value, symbolRemap)))
            .Where(static entry => entry.Value.IsValid)
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<SymbolId>> RestoreFunctionOverloads(
        IReadOnlyDictionary<string, IReadOnlyList<int>> values,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlySet<int> allowedSymbolIds) =>
        values
            .Select(entry => new KeyValuePair<string, IReadOnlyList<SymbolId>>(
                entry.Key,
                entry.Value
                    .Where(allowedSymbolIds.Contains)
                    .Select(id => RemapSymbolId(id, symbolRemap))
                    .Where(static id => id.IsValid)
                    .ToArray()))
            .Where(static entry => entry.Value.Count > 0)
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);

    private static bool TryCreateSymbol(
        SymbolPayload payload,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlyDictionary<int, TypeId> typeRemap,
        out Symbol symbol,
        out string failure)
    {
        var id = RemapSymbolId(payload.Id, symbolRemap);
        if (!id.IsValid)
        {
            symbol = null!;
            failure = $"missing-symbol-remap:{payload.Id}:{payload.Kind}:{payload.Name}";
            return false;
        }

        var span = CreateSourceSpan(payload.Span);
        var typeId = RemapTypeId(payload.TypeId, typeRemap);
        symbol = payload.Kind switch
        {
            nameof(SymbolKind.Module) => CreateModuleSymbol(payload, id, span, typeId, symbolRemap),
            nameof(SymbolKind.Function) => CreateFunctionSymbol(payload, id, span, typeId, symbolRemap, typeRemap),
            nameof(SymbolKind.Variable) => CreateVariableSymbol(payload, id, span, typeId, typeRemap),
            nameof(SymbolKind.Adt) => CreateAdtSymbol(payload, id, span, typeId, symbolRemap, typeRemap),
            nameof(SymbolKind.Constructor) => CreateCtorSymbol(payload, id, span, typeId, symbolRemap, typeRemap),
            nameof(SymbolKind.Field) => CreateFieldSymbol(payload, id, span, typeId, symbolRemap, typeRemap),
            nameof(SymbolKind.Trait) => CreateTraitSymbol(payload, id, span, typeId, symbolRemap),
            nameof(SymbolKind.Effect) => CreateEffectSymbol(payload, id, span, typeId, symbolRemap),
            nameof(SymbolKind.TypeParameter) => CreateTypeParamSymbol(payload, id, span, typeId, symbolRemap),
            nameof(SymbolKind.Impl) => CreateImplSymbol(payload, id, span, typeId, symbolRemap, typeRemap),
            _ => null!
        };

        if (symbol == null)
        {
            failure = $"unsupported-symbol-kind:{payload.Kind}:{payload.Id}";
            return false;
        }

        failure = "";
        return true;
    }

    private static ModuleSymbol CreateModuleSymbol(
        SymbolPayload payload,
        SymbolId id,
        SourceSpan span,
        TypeId typeId,
        IReadOnlyDictionary<int, SymbolId> symbolRemap) =>
        new()
        {
            Id = id,
            Name = payload.Name,
            Span = span,
            IsTypeResolved = payload.IsTypeResolved,
            IsModuleLevel = payload.IsModuleLevel,
            IsPublic = payload.IsPublic,
            TypeId = typeId,
            PackageAlias = GetFact(payload, "packageAlias"),
            PackageInstanceKey = GetFact(payload, "packageInstanceKey"),
            Path = SplitList(GetFact(payload, "path"), '/').ToList(),
            Members = RemapSymbolIds(GetFact(payload, "members"), symbolRemap).ToList(),
            ExportedBindings = ParseModuleBindings(GetFact(payload, "exports"), symbolRemap).ToList(),
            UsesExplicitExports = ParseBool(GetFact(payload, "usesExplicitExports")),
            Imports = RemapSymbolIds(GetFact(payload, "imports"), symbolRemap).ToList(),
            ParentModule = RemapOptionalSymbolId(GetFact(payload, "parentModule"), symbolRemap)
        };

    private static FuncSymbol CreateFunctionSymbol(
        SymbolPayload payload,
        SymbolId id,
        SourceSpan span,
        TypeId typeId,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlyDictionary<int, TypeId> typeRemap) =>
        new()
        {
            Id = id,
            Name = payload.Name,
            Span = span,
            IsTypeResolved = payload.IsTypeResolved,
            IsModuleLevel = payload.IsModuleLevel,
            IsPublic = payload.IsPublic,
            TypeId = typeId,
            TypeParams = RemapPositionalSymbolIds(GetFact(payload, "typeParams"), symbolRemap).ToList(),
            Parameters = RemapPositionalSymbolIds(GetFact(payload, "parameters"), symbolRemap).ToList(),
            ParamTypes = RemapPositionalTypeIds(GetFact(payload, "paramTypes"), typeRemap).ToList(),
            ReturnType = RemapTypeId(GetFactInt(payload, "returnType"), typeRemap),
            Effects = ParseEffectIds(GetFact(payload, "abilities")).ToList(),
            ImplicitAbilities = SplitList(GetFact(payload, "implicitAbilities"), ',').ToList(),
            IsComptime = ParseBool(GetFact(payload, "isComptime")),
            OwnerTrait = RemapOptionalSymbolId(GetFact(payload, "ownerTrait"), symbolRemap),
            TraitSelfPosition = ParseEnum(GetFact(payload, "traitSelfPosition"), SelfPosition.Unknown),
            TraitSelfParameterIndices = ParseNonNegativeInts(GetFact(payload, "traitSelfParameterIndices")).ToList(),
            TraitSelfInResult = ParseBool(GetFact(payload, "traitSelfInResult")),
            TraitMethodRole = ParseEnum(GetFact(payload, "traitMethodRole"), TraitMethodRole.None),
            HasBody = ParseBool(GetFact(payload, "hasBody")),
            IsDefaultImplementation = ParseBool(GetFact(payload, "isDefaultImplementation")),
            IsTraitImplementation = ParseBool(GetFact(payload, "isTraitImplementation")),
            IsExternal = ParseBool(GetFact(payload, "isExternal")),
            ExternalSymbolName = EmptyToNull(GetFact(payload, "externalSymbolName")),
            ExternalLibrary = EmptyToNull(GetFact(payload, "externalLibrary")),
            CStructFieldTypeId = RemapTypeId(GetFactInt(payload, "cStructFieldTypeId"), typeRemap),
            IsCStructAccessor = ParseBool(GetFact(payload, "isCStructAccessor")),
            CStructFieldOffset = GetFactInt(payload, "cStructFieldOffset"),
            IsCStructGetter = ParseBool(GetFact(payload, "isCStructGetter")),
            IntrinsicName = EmptyToNull(GetFact(payload, "intrinsicName")),
            BuiltinIntrinsicRole = ParseEnum(GetFact(payload, "builtinIntrinsicRole"), BuiltinIntrinsicRole.None)
        };

    private static VarSymbol CreateVariableSymbol(
        SymbolPayload payload,
        SymbolId id,
        SourceSpan span,
        TypeId typeId,
        IReadOnlyDictionary<int, TypeId> typeRemap) =>
        new()
        {
            Id = id,
            Name = payload.Name,
            Span = span,
            IsTypeResolved = payload.IsTypeResolved,
            IsModuleLevel = payload.IsModuleLevel,
            IsPublic = payload.IsPublic,
            TypeId = typeId,
            IsMutable = ParseBool(GetFact(payload, "isMutable")),
            IsComptime = ParseBool(GetFact(payload, "isComptime")),
            Type = RemapTypeId(GetFactInt(payload, "type"), typeRemap),
            IsParameter = ParseBool(GetFact(payload, "isParameter")),
            IsPatternBound = ParseBool(GetFact(payload, "isPatternBound")),
            BindingMode = ParseEnum(GetFact(payload, "bindingMode"), PatternBindingMode.ByValue)
        };

    private static AdtSymbol CreateAdtSymbol(
        SymbolPayload payload,
        SymbolId id,
        SourceSpan span,
        TypeId typeId,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlyDictionary<int, TypeId> typeRemap)
    {
        var aliasTarget = RemapTypeId(GetFactInt(payload, "aliasTarget"), typeRemap);
        return new AdtSymbol
        {
            Id = id,
            Name = payload.Name,
            Span = span,
            IsTypeResolved = payload.IsTypeResolved,
            IsModuleLevel = payload.IsModuleLevel,
            IsPublic = payload.IsPublic,
            TypeId = typeId,
            TypeParams = RemapPositionalSymbolIds(GetFact(payload, "typeParams"), symbolRemap).ToList(),
            Constructors = RemapSymbolIds(GetFact(payload, "constructors"), symbolRemap).ToList(),
            Fields = RemapSymbolIds(GetFact(payload, "fields"), symbolRemap).ToList(),
            AliasTarget = aliasTarget.IsValid ? aliasTarget : null,
            IsCStruct = ParseBool(GetFact(payload, "isCStruct"))
        };
    }

    private static CtorSymbol CreateCtorSymbol(
        SymbolPayload payload,
        SymbolId id,
        SourceSpan span,
        TypeId typeId,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlyDictionary<int, TypeId> typeRemap) =>
        new()
        {
            Id = id,
            Name = payload.Name,
            Span = span,
            IsTypeResolved = payload.IsTypeResolved,
            IsModuleLevel = payload.IsModuleLevel,
            IsPublic = payload.IsPublic,
            TypeId = typeId,
            OwnerAdt = RemapSymbolId(GetFactInt(payload, "ownerAdt"), symbolRemap),
            TypeParams = RemapPositionalSymbolIds(GetFact(payload, "typeParams"), symbolRemap).ToList(),
            PositionalArgs = RemapPositionalTypeIds(GetFact(payload, "positionalArgs"), typeRemap).ToList(),
            NamedFields = RemapSymbolIds(GetFact(payload, "namedFields"), symbolRemap).ToList(),
            SignatureText = EmptyToNull(GetFact(payload, "signatureText"))
        };

    private static FieldSymbol CreateFieldSymbol(
        SymbolPayload payload,
        SymbolId id,
        SourceSpan span,
        TypeId typeId,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlyDictionary<int, TypeId> typeRemap) =>
        new()
        {
            Id = id,
            Name = payload.Name,
            Span = span,
            IsTypeResolved = payload.IsTypeResolved,
            IsModuleLevel = payload.IsModuleLevel,
            IsPublic = payload.IsPublic,
            TypeId = typeId,
            FieldType = RemapTypeId(GetFactInt(payload, "fieldType"), typeRemap),
            OwnerType = RemapSymbolId(GetFactInt(payload, "ownerType"), symbolRemap),
            Index = GetFactInt(payload, "index", -1)
        };

    private static TraitSymbol CreateTraitSymbol(
        SymbolPayload payload,
        SymbolId id,
        SourceSpan span,
        TypeId typeId,
        IReadOnlyDictionary<int, SymbolId> symbolRemap) =>
        new()
        {
            Id = id,
            Name = payload.Name,
            Span = span,
            IsTypeResolved = payload.IsTypeResolved,
            IsModuleLevel = payload.IsModuleLevel,
            IsPublic = payload.IsPublic,
            TypeId = typeId,
            TypeParams = RemapPositionalSymbolIds(GetFact(payload, "typeParams"), symbolRemap).ToList(),
            Methods = RemapSymbolIds(GetFact(payload, "methods"), symbolRemap).ToList(),
            AssociatedTypes = RemapSymbolIds(GetFact(payload, "associatedTypes"), symbolRemap).ToList(),
            ParentTraits = RemapSymbolIds(GetFact(payload, "parentTraits"), symbolRemap).ToList(),
            SelfPosition = ParseEnum(GetFact(payload, "selfPosition"), SelfPosition.Unknown)
        };

    private static EffectSymbol CreateEffectSymbol(
        SymbolPayload payload,
        SymbolId id,
        SourceSpan span,
        TypeId typeId,
        IReadOnlyDictionary<int, SymbolId> symbolRemap) =>
        new()
        {
            Id = id,
            Name = payload.Name,
            Span = span,
            IsTypeResolved = payload.IsTypeResolved,
            IsModuleLevel = payload.IsModuleLevel,
            IsPublic = payload.IsPublic,
            TypeId = typeId
        };

    private static TypeParamSymbol CreateTypeParamSymbol(
        SymbolPayload payload,
        SymbolId id,
        SourceSpan span,
        TypeId typeId,
        IReadOnlyDictionary<int, SymbolId> symbolRemap) =>
        new()
        {
            Id = id,
            Name = payload.Name,
            Span = span,
            IsTypeResolved = payload.IsTypeResolved,
            IsModuleLevel = payload.IsModuleLevel,
            IsPublic = payload.IsPublic,
            TypeId = typeId,
            KindAnnotation = GetFact(payload, "kindAnnotation", "kind1"),
            ParameterKind = ParseGenericParameterKind(GetFact(payload, "parameterKind")),
            IsComptime = ParseBool(GetFact(payload, "isComptime")),
            ComptimeTypeAnnotation = EmptyToNull(GetFact(payload, "comptimeTypeAnnotation")),
            TraitConstraints = RemapSymbolIds(GetFact(payload, "traitConstraints"), symbolRemap).ToList()
        };

    private static GenericParameterKind ParseGenericParameterKind(string? value) =>
        Enum.TryParse<GenericParameterKind>(value, ignoreCase: false, out var parsed)
            ? parsed
            : GenericParameterKind.Type;

    private static ImplSymbol CreateImplSymbol(
        SymbolPayload payload,
        SymbolId id,
        SourceSpan span,
        TypeId typeId,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        IReadOnlyDictionary<int, TypeId> typeRemap) =>
        new()
        {
            Id = id,
            Name = payload.Name,
            Span = span,
            IsTypeResolved = payload.IsTypeResolved,
            IsModuleLevel = payload.IsModuleLevel,
            IsPublic = payload.IsPublic,
            TypeId = typeId,
            Trait = RemapSymbolId(GetFactInt(payload, "trait"), symbolRemap),
            ImplementingType = RemapTypeId(GetFactInt(payload, "implementingType"), typeRemap),
            CanonicalImplementingType = GetFact(payload, "canonicalImplementingType"),
            ImplementingTypeDisplay = GetFact(payload, "implementingTypeDisplay"),
            ImplementingTypeKey = ParseImplTypeRefKey(GetFact(payload, "implementingTypeKey")),
            Methods = RemapSymbolIds(GetFact(payload, "methods"), symbolRemap).ToList(),
            TraitTypeArgs = SplitList(GetFact(payload, "traitTypeArgs"), ',').ToList(),
            TraitTypeArgKeys = ParseImplTypeRefKeys(GetFact(payload, "traitTypeArgKeys")).ToList(),
            CanonicalTraitTypeArgs = SplitList(GetFact(payload, "canonicalTraitTypeArgs"), ',').ToList(),
            CanonicalTraitTypeArgKeys = ParseImplTypeRefKeys(GetFact(payload, "canonicalTraitTypeArgKeys")).ToList(),
            TraitMethodImplementations = ParseSymbolMap(GetFact(payload, "traitMethodImplementations"), symbolRemap),
            TypeArguments = ParseTypeMap(GetFact(payload, "typeArguments"), typeRemap),
            IsAutoDerived = ParseBool(GetFact(payload, "isAutoDerived"))
        };

    private static ModuleBindingEntry RemapBinding(
        ModuleBindingPayload binding,
        IReadOnlyDictionary<int, SymbolId> symbolRemap) =>
        new()
        {
            Name = binding.Name,
            SymbolId = RemapSymbolId(binding.SymbolId, symbolRemap),
            Kind = ParseEnum(binding.Kind, ResolutionKind.Value)
        };

    private static IEnumerable<ModuleBindingEntry> ParseModuleBindings(
        string value,
        IReadOnlyDictionary<int, SymbolId> symbolRemap)
    {
        foreach (var part in SplitList(value, ','))
        {
            var fields = part.Split(':');
            if (fields.Length != 3 || !int.TryParse(fields[2], out var symbolId))
            {
                continue;
            }

            yield return new ModuleBindingEntry
            {
                Name = fields[0],
                Kind = ParseEnum(fields[1], ResolutionKind.Value),
                SymbolId = RemapSymbolId(symbolId, symbolRemap)
            };
        }
    }

    private static SourceSpan CreateSourceSpan(SourceSpanPayload payload) =>
        new(
            new SourceLocation(payload.Position, payload.Line, payload.Column, payload.FilePath),
            payload.Length);

    private static string GetFact(SymbolPayload payload, string name, string fallback = "") =>
        payload.Facts.TryGetValue(name, out var value) ? value : fallback;

    private static int GetFactInt(SymbolPayload payload, string name, int fallback = 0) =>
        int.TryParse(GetFact(payload, name), out var value) ? value : fallback;

    private static SymbolId RemapSymbolId(int value, IReadOnlyDictionary<int, SymbolId> symbolRemap) =>
        value <= 0 ? SymbolId.None : symbolRemap.GetValueOrDefault(value, SymbolId.None);

    private static SymbolId? RemapOptionalSymbolId(string value, IReadOnlyDictionary<int, SymbolId> symbolRemap)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            return null;
        }

        var remapped = RemapSymbolId(parsed, symbolRemap);
        return remapped.IsValid ? remapped : null;
    }

    private static TypeId RemapTypeId(int value, IReadOnlyDictionary<int, TypeId> typeRemap) =>
        value <= 0 ? TypeId.None : typeRemap.GetValueOrDefault(value, new TypeId(value));

    private static IEnumerable<SymbolId> RemapSymbolIds(string value, IReadOnlyDictionary<int, SymbolId> symbolRemap) =>
        ParseInts(value).Select(id => RemapSymbolId(id, symbolRemap)).Where(static id => id.IsValid);

    private static IEnumerable<SymbolId> RemapPositionalSymbolIds(
        string value,
        IReadOnlyDictionary<int, SymbolId> symbolRemap) =>
        ParsePositionalIds(value).Select(id => RemapSymbolId(id, symbolRemap));

    private static IEnumerable<TypeId> RemapTypeIds(string value, IReadOnlyDictionary<int, TypeId> typeRemap) =>
        ParseInts(value).Select(id => RemapTypeId(id, typeRemap)).Where(static id => id.IsValid);

    private static IEnumerable<TypeId> RemapPositionalTypeIds(
        string value,
        IReadOnlyDictionary<int, TypeId> typeRemap) =>
        ParsePositionalIds(value).Select(id => RemapTypeId(id, typeRemap));

    private static IEnumerable<EffectId> ParseEffectIds(string value) =>
        ParseInts(value).Where(static id => id > 0).Select(static id => new EffectId(id));

    private static Dictionary<SymbolId, SymbolId> ParseSymbolMap(
        string value,
        IReadOnlyDictionary<int, SymbolId> symbolRemap)
    {
        var result = new Dictionary<SymbolId, SymbolId>();
        foreach (var entry in SplitList(value, ','))
        {
            var parts = entry.Split("->", StringSplitOptions.None);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var from) ||
                !int.TryParse(parts[1], out var to))
            {
                continue;
            }

            var fromId = RemapSymbolId(from, symbolRemap);
            var toId = RemapSymbolId(to, symbolRemap);
            if (fromId.IsValid && toId.IsValid)
            {
                result[fromId] = toId;
            }
        }

        return result;
    }

    private static Dictionary<TypeId, TypeId> ParseTypeMap(
        string value,
        IReadOnlyDictionary<int, TypeId> typeRemap)
    {
        var result = new Dictionary<TypeId, TypeId>();
        foreach (var entry in SplitList(value, ','))
        {
            var parts = entry.Split("->", StringSplitOptions.None);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var from) ||
                !int.TryParse(parts[1], out var to))
            {
                continue;
            }

            var fromId = RemapTypeId(from, typeRemap);
            var toId = RemapTypeId(to, typeRemap);
            if (fromId.IsValid && toId.IsValid)
            {
                result[fromId] = toId;
            }
        }

        return result;
    }

    private static bool ParseBool(string value) =>
        bool.TryParse(value, out var parsed) && parsed;

    private static T ParseEnum<T>(string value, T fallback) where T : struct =>
        Enum.TryParse<T>(value, out var parsed) ? parsed : fallback;

    private static IEnumerable<int> ParseInts(string value) =>
        SplitList(value, ',')
            .Select(static item => int.TryParse(item, out var parsed) ? parsed : 0)
            .Where(static value => value > 0);

    private static IEnumerable<int> ParseNonNegativeInts(string value) =>
        SplitList(value, ',')
            .Select(static item => int.TryParse(item, out var parsed) ? parsed : -1)
            .Where(static value => value >= 0);

    private static IEnumerable<int> ParsePositionalIds(string value) =>
        SplitList(value, ',')
            .Select(static item => int.TryParse(item, out var parsed) ? parsed : int.MinValue)
            .Where(static value => value >= SymbolId.None.Value);

    private static IEnumerable<string> SplitList(string value, char separator) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ImplTypeRefKey ParseImplTypeRefKey(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? ImplTypeRefKey.Empty
            : ImplTypeRefKey.FromCanonicalText(value);

    private static IEnumerable<ImplTypeRefKey> ParseImplTypeRefKeys(string value) =>
        SplitList(value, '|')
            .Select(ParseImplTypeRefKey)
            .Where(static key => !key.IsEmpty);

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
