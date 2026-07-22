using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

internal sealed class MirConstructorLayoutSpecializer(
    IReadOnlyDictionary<int, TypeDescriptor> typeDescriptorById,
    IReadOnlyDictionary<int, MirTypeConstructorInfo> typeConstructorInfoByTypeId,
    MirConstructorKeyMatcher constructorKeyMatcher,
    MirConstructorLayoutSpecializer.TryGetTypeDescriptorFunc tryGetTypeDescriptor,
    MirConstructorLayoutSpecializer.GetOrCreateDynamicTypeIdFunc getOrCreateDynamicTypeId,
    Func<IEnumerable<MirTypeAliasInfo>> enumerateTypeAliasInfos,
    Func<TypeId, bool> isMirGenericTypeParameter)
{
    internal delegate bool TryGetTypeDescriptorFunc(TypeId typeId, out TypeDescriptor descriptor);

    internal delegate TypeId GetOrCreateDynamicTypeIdFunc(TypeDescriptor descriptor);

    public void Populate(
        MirModule module,
        Dictionary<int, List<ConstructorTypeLayout>> outputLayouts)
    {
        var visited = new HashSet<int>();
        var queued = new HashSet<int>();
        var pending = new Queue<KeyValuePair<int, TypeDescriptor>>();
        var lastScannedDescriptorCount = -1;
        EnqueueKnownDescriptors();

        while (pending.Count > 0)
        {
            var (typeIdValue, descriptor) = pending.Dequeue();
            if (!visited.Add(typeIdValue))
            {
                continue;
            }

            if (descriptor is not TypeDescriptor.TyCon tyCon)
            {
                EnqueueKnownDescriptors();
                continue;
            }

            if (TryParseConstructorVarIndex(tyCon.Constructor, out _))
            {
                EnqueueKnownDescriptors();
                continue;
            }

            if (outputLayouts.ContainsKey(typeIdValue))
            {
                EnqueueKnownDescriptors();
                continue;
            }

            if (!TryFindBaseConstructorLayout(module, typeIdValue, tyCon, out var baseMatch))
            {
                EnqueueKnownDescriptors();
                continue;
            }

            var typeParamMapping = new Dictionary<int, TypeId>();
            for (var i = 0; i < baseMatch.Descriptor.TypeArgs.Length && i < baseMatch.SpecializedDescriptor.TypeArgs.Length; i++)
            {
                typeParamMapping[baseMatch.Descriptor.TypeArgs[i].Value] = baseMatch.SpecializedDescriptor.TypeArgs[i];
            }

            var clonedLayouts = new List<ConstructorTypeLayout>(baseMatch.Layouts.Count);
            var specializedTypeName = BuildSpecializedTypeName(
                ResolveConstructorDisplayName(tyCon.Constructor),
                tyCon.TypeArgs,
                tyCon.ValueArgs,
                tyCon.EffectArgs);

            foreach (var layout in baseMatch.Layouts)
            {
                var substitutedFieldTypes = new List<TypeId>(layout.FieldTypeIds.Count);
                foreach (var fieldTypeId in layout.FieldTypeIds)
                {
                    substitutedFieldTypes.Add(SubstituteFieldTypeId(fieldTypeId, typeParamMapping));
                }

                clonedLayouts.Add(new ConstructorTypeLayout
                {
                    TypeName = specializedTypeName,
                    ConstructorName = layout.ConstructorName,
                    TagValue = layout.TagValue,
                    RuntimeTypeId = layout.RuntimeTypeId,
                    FieldTypeIds = substitutedFieldTypes
                });
            }

            outputLayouts[typeIdValue] = clonedLayouts;
            EnqueueKnownDescriptors();
        }

        void EnqueueKnownDescriptors()
        {
            if (lastScannedDescriptorCount == typeDescriptorById.Count)
            {
                return;
            }

            lastScannedDescriptorCount = typeDescriptorById.Count;
            foreach (var entry in typeDescriptorById)
            {
                if (!visited.Contains(entry.Key) && queued.Add(entry.Key))
                {
                    pending.Enqueue(entry);
                }
            }
        }
    }

    private bool TryFindBaseConstructorLayout(
        MirModule module,
        int specializedTypeIdValue,
        TypeDescriptor.TyCon specializedTyCon,
        out BaseConstructorLayoutMatch match)
    {
        var matchingSpecializedDescriptors = new List<TypeDescriptor.TyCon> { specializedTyCon };
        if (TryExpandAliasTyCon(specializedTyCon, out var expandedSpecializedTyCon))
        {
            matchingSpecializedDescriptors.Add(expandedSpecializedTyCon);
        }

        foreach (var (candidateTypeIdValue, candidateLayouts) in module.ConstructorLayouts)
        {
            if (candidateLayouts.Count == 0 ||
                candidateTypeIdValue == specializedTypeIdValue ||
                !TryGetConstructorLayoutDescriptor(new TypeId(candidateTypeIdValue), out var candidateTyCon) ||
                !CanUseAsGenericLayoutBase(candidateTyCon))
            {
                continue;
            }

            foreach (var matchingSpecializedTyCon in matchingSpecializedDescriptors)
            {
                if (!constructorKeyMatcher.AreEquivalent(
                        candidateTyCon.Constructor,
                        matchingSpecializedTyCon.Constructor) ||
                    candidateTyCon.TypeArgs.Length != matchingSpecializedTyCon.TypeArgs.Length)
                {
                    continue;
                }

                match = new BaseConstructorLayoutMatch(
                    new TypeId(candidateTypeIdValue),
                    candidateTyCon,
                    matchingSpecializedTyCon,
                    candidateLayouts);
                return true;
            }
        }

        match = default!;
        return false;
    }

    private bool TryGetConstructorLayoutDescriptor(TypeId typeId, out TypeDescriptor.TyCon descriptor)
    {
        if (tryGetTypeDescriptor(typeId, out var existingDescriptor) &&
            existingDescriptor is TypeDescriptor.TyCon existingTyCon)
        {
            if (typeConstructorInfoByTypeId.TryGetValue(typeId.Value, out var existingTypeConstructor) &&
                existingTypeConstructor.TypeParameterIds.Count > existingTyCon.TypeArgs.Length)
            {
                descriptor = new TypeDescriptor.TyCon(
                    existingTyCon.Constructor,
                    existingTypeConstructor.TypeParameterIds
                        .Where(static typeParameterId => typeParameterId.IsValid)
                        .Select(static typeParameterId => new TypeId(typeParameterId.Value))
                        .ToArray())
                {
                    ValueArgs = existingTyCon.ValueArgs,
                    EffectArgs = existingTyCon.EffectArgs
                };
                return true;
            }

            descriptor = existingTyCon;
            return true;
        }

        if (typeConstructorInfoByTypeId.TryGetValue(typeId.Value, out var typeConstructor) &&
            typeConstructor.SymbolId.IsValid)
        {
            descriptor = new TypeDescriptor.TyCon(
                $"sym:{typeConstructor.SymbolId.Value}",
                typeConstructor.TypeParameterIds
                    .Where(static typeParameterId => typeParameterId.IsValid)
                    .Select(static typeParameterId => new TypeId(typeParameterId.Value))
                    .ToArray());
            return true;
        }

        descriptor = null!;
        return false;
    }

    private bool TryExpandAliasTyCon(TypeDescriptor.TyCon tyCon, out TypeDescriptor.TyCon expandedTyCon)
    {
        expandedTyCon = tyCon;
        if (!TryFindTypeAliasInfoForConstructor(tyCon.Constructor, out var aliasInfo) ||
            aliasInfo.TypeParameterIds.Count != tyCon.TypeArgs.Length ||
            !aliasInfo.AliasTarget.IsValid ||
            !tryGetTypeDescriptor(aliasInfo.AliasTarget, out var aliasTargetDescriptor) ||
            aliasTargetDescriptor is not TypeDescriptor.TyCon)
        {
            return false;
        }

        var typeParamMapping = new Dictionary<int, TypeId>();
        for (var i = 0; i < aliasInfo.TypeParameterIds.Count; i++)
        {
            typeParamMapping[aliasInfo.TypeParameterIds[i].Value] = tyCon.TypeArgs[i];
        }

        if (!TrySubstituteFieldTypeDescriptor(
                aliasTargetDescriptor,
                typeParamMapping,
                out var substitutedDescriptor,
                out _) ||
            substitutedDescriptor is not TypeDescriptor.TyCon substitutedTyCon)
        {
            return false;
        }

        expandedTyCon = substitutedTyCon;
        return true;
    }

    private bool TryFindTypeAliasInfoForConstructor(
        TypeConstructorKey constructor,
        out MirTypeAliasInfo aliasInfo)
    {
        aliasInfo = null!;
        if (!constructorKeyMatcher.TryGetIdentity(constructor, out var identity))
        {
            return false;
        }

        foreach (var candidate in enumerateTypeAliasInfos())
        {
            if ((identity.SymbolId.IsValid && candidate.AliasId == identity.SymbolId) ||
                (identity.TypeId.IsValid && candidate.TypeId == identity.TypeId))
            {
                aliasInfo = candidate;
                return true;
            }
        }

        return false;
    }

    private string ResolveConstructorDisplayName(TypeConstructorKey constructor)
    {
        if (constructorKeyMatcher.TryGetIdentity(constructor, out var identity))
        {
            foreach (var typeConstructor in typeConstructorInfoByTypeId.Values)
            {
                if ((identity.SymbolId.IsValid && typeConstructor.SymbolId == identity.SymbolId) ||
                    (identity.TypeId.IsValid && typeConstructor.TypeId == identity.TypeId))
                {
                    return typeConstructor.Name;
                }
            }
        }

        return constructor.ToDescriptorString();
    }

    private bool CanUseAsGenericLayoutBase(TypeDescriptor.TyCon tyCon)
    {
        return tyCon.TypeArgs.Length == 0 ||
               tyCon.TypeArgs.All(IsLayoutTypeParameter);
    }

    private bool IsLayoutTypeParameter(TypeId typeId)
    {
        return typeId.IsValid &&
               ((tryGetTypeDescriptor(typeId, out var descriptor) && descriptor is TypeDescriptor.TypeVar) ||
                isMirGenericTypeParameter(typeId));
    }

    private static string BuildSpecializedTypeName(
        string constructorDescriptor,
        IReadOnlyList<TypeId> typeArgs,
        IReadOnlyList<GenericValueArgumentDescriptor> valueArgs,
        IReadOnlyList<GenericEffectArgumentDescriptor> effectArgs)
    {
        if (typeArgs.Count == 0 && valueArgs.Count == 0 && effectArgs.Count == 0)
        {
            return constructorDescriptor;
        }

        var argTokens = string.Join(
            "_",
            typeArgs.Select(static type => $"t{type.Value}")
                .Concat(valueArgs.Select(static value => value.ValueVariableIndex >= 0
                    ? $"vv{value.ValueVariableIndex}"
                    : $"v{value.CanonicalHash[..Math.Min(12, value.CanonicalHash.Length)]}"))
                .Concat(effectArgs.Select(static effect => $"e{effect.TypeId.Value}")));
        return $"{constructorDescriptor}_{argTokens}";
    }

    private TypeId SubstituteFieldTypeId(TypeId fieldTypeId, IReadOnlyDictionary<int, TypeId> typeParamMapping)
    {
        if (!fieldTypeId.IsValid)
        {
            return fieldTypeId;
        }

        if (typeParamMapping.TryGetValue(fieldTypeId.Value, out var substituted))
        {
            return substituted;
        }

        if (!tryGetTypeDescriptor(fieldTypeId, out var descriptor))
        {
            return fieldTypeId;
        }

        if (!TrySubstituteFieldTypeDescriptor(descriptor, typeParamMapping, out var substitutedDescriptor, out var changed) ||
            !changed)
        {
            return fieldTypeId;
        }

        return getOrCreateDynamicTypeId(substitutedDescriptor);
    }

    private bool TrySubstituteFieldTypeDescriptor(
        TypeDescriptor descriptor,
        IReadOnlyDictionary<int, TypeId> typeParamMapping,
        out TypeDescriptor substitutedDescriptor,
        out bool changed)
    {
        substitutedDescriptor = TypeDescriptorRewriter.RewriteTypeIds(
            descriptor,
            typeId => SubstituteFieldTypeId(typeId, typeParamMapping));
        changed = !ReferenceEquals(substitutedDescriptor, descriptor);
        return true;
    }

    private static bool TryParseConstructorVarIndex(TypeConstructorKey constructor, out int constructorVarIndex)
    {
        constructorVarIndex = -1;
        if (constructor.Kind != TypeConstructorKeyKind.Variable)
        {
            return false;
        }

        constructorVarIndex = constructor.Id;
        return true;
    }

    private sealed record BaseConstructorLayoutMatch(
        TypeId TypeId,
        TypeDescriptor.TyCon Descriptor,
        TypeDescriptor.TyCon SpecializedDescriptor,
        List<ConstructorTypeLayout> Layouts);
}
