using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private bool BindConstructorVariable(
        int constructorVarIndex,
        IReadOnlyList<TypeId> templateTypeArgs,
        TypeConstructorKey concreteConstructor,
        IReadOnlyList<TypeId> concreteTypeArgs,
        SpecializationBindings bindings)
    {
        if (bindings.ConstructorBindings.TryGetValue(constructorVarIndex, out var existingBinding))
        {
            return AreConstructorKeysEquivalent(existingBinding.Constructor, concreteConstructor) &&
                   TryMergeExistingConstructorBinding(existingBinding, templateTypeArgs, concreteTypeArgs, bindings);
        }

        var aliasMatched = TryCreateAliasConstructorBindingSlots(
            templateTypeArgs,
            concreteConstructor,
            concreteTypeArgs,
            bindings,
            out var slots);
        var structuralMatched = aliasMatched ||
                                TryCreateConstructorBindingSlots(templateTypeArgs, concreteTypeArgs, bindings, out slots);
        if (concreteTypeArgs.Count < templateTypeArgs.Count || !structuralMatched)
        {
            return false;
        }

        var newBinding = new ConstructorBinding(concreteConstructor, slots!);
        bindings.ConstructorBindings[constructorVarIndex] = newBinding;
        return true;
    }

    private bool TryMergeExistingConstructorBinding(
        ConstructorBinding existingBinding,
        IReadOnlyList<TypeId> templateTypeArgs,
        IReadOnlyList<TypeId> concreteTypeArgs,
        SpecializationBindings bindings)
    {
        if (existingBinding.Slots.Count != concreteTypeArgs.Count)
        {
            return false;
        }

        for (var index = 0; index < existingBinding.Slots.Count; index++)
        {
            var slot = existingBinding.Slots[index];
            var concreteTypeArg = concreteTypeArgs[index];
            if (slot.PlaceholderIndex is { } placeholderIndex)
            {
                if (placeholderIndex < 0 ||
                    placeholderIndex >= templateTypeArgs.Count ||
                    !TryCollectTypeBindings(templateTypeArgs[placeholderIndex], concreteTypeArg, bindings))
                {
                    return false;
                }

                continue;
            }

            if (slot.FixedTypeId is not { } fixedTypeId ||
                !TryCollectTypeBindings(fixedTypeId, concreteTypeArg, bindings))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryCreateAliasConstructorBindingSlots(
        IReadOnlyList<TypeId> templateTypeArgs,
        TypeConstructorKey concreteConstructor,
        IReadOnlyList<TypeId> concreteTypeArgs,
        SpecializationBindings bindings,
        out List<ConstructorArgSlot>? slots)
    {
        slots = null;
        if (templateTypeArgs.Count == 0)
        {
            return false;
        }

        if (TryCreateTraitAliasConstructorBindingSlots(
                templateTypeArgs,
                concreteTypeArgs,
                bindings,
                out var sawTraitAliasCandidate,
                out slots))
        {
            return true;
        }

        if (sawTraitAliasCandidate)
        {
            return false;
        }

        var concreteDescriptor = new TypeDescriptor.TyCon(concreteConstructor, [.. concreteTypeArgs]);
        if (!TryGetInternedDynamicTypeId(concreteDescriptor, out var concreteTypeId))
        {
            return false;
        }

        foreach (var aliasInfo in EnumerateTypeAliasInfos())
        {
            if (!aliasInfo.AliasTarget.IsValid ||
                aliasInfo.TypeParameterIds.Count < templateTypeArgs.Count ||
                !TryGetTypeDescriptor(aliasInfo.AliasTarget, out var aliasTargetDescriptor) ||
                aliasTargetDescriptor is not TypeDescriptor.TyCon aliasTargetTyCon ||
                !AreConstructorKeysEquivalent(aliasTargetTyCon.Constructor, concreteConstructor) ||
                aliasTargetTyCon.TypeArgs.Length != concreteTypeArgs.Count)
            {
                continue;
            }

            var placeholderAliasStart = aliasInfo.TypeParameterIds.Count - templateTypeArgs.Count;
            var placeholderIndexByConcretePosition = new Dictionary<int, int>();
            for (var concreteIndex = 0; concreteIndex < aliasTargetTyCon.TypeArgs.Length; concreteIndex++)
            {
                if (!TryGetAliasTargetTypeParameterIndex(
                        aliasInfo,
                        aliasTargetTyCon.TypeArgs[concreteIndex],
                        out var aliasParamIndex) ||
                    aliasParamIndex < placeholderAliasStart)
                {
                    continue;
                }

                var placeholderIndex = aliasParamIndex - placeholderAliasStart;
                if (placeholderIndex < templateTypeArgs.Count)
                {
                    placeholderIndexByConcretePosition[concreteIndex] = placeholderIndex;
                }
            }

            if (placeholderIndexByConcretePosition.Count != templateTypeArgs.Count)
            {
                continue;
            }

            var aliasBindings = CloneBindings(bindings);
            if (!TryCollectTypeBindings(aliasInfo.AliasTarget, concreteTypeId, aliasBindings))
            {
                continue;
            }

            var resolvedBindings = CloneBindings(bindings);
            var matched = true;
            for (var templateIndex = 0; templateIndex < templateTypeArgs.Count; templateIndex++)
            {
                var aliasTypeParamId = aliasInfo.TypeParameterIds[placeholderAliasStart + templateIndex].Value;
                var aliasArgumentType = aliasBindings.TypeBindings.TryGetValue(aliasTypeParamId, out var boundAliasArgument)
                    ? boundAliasArgument
                    : new TypeId(aliasTypeParamId);

                if (!TryCollectTypeBindings(templateTypeArgs[templateIndex], aliasArgumentType, resolvedBindings))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            MergeBindings(bindings, resolvedBindings);

            slots = new List<ConstructorArgSlot>(concreteTypeArgs.Count);
            for (var concreteIndex = 0; concreteIndex < concreteTypeArgs.Count; concreteIndex++)
            {
                slots.Add(placeholderIndexByConcretePosition.TryGetValue(concreteIndex, out var placeholderIndex)
                    ? new ConstructorArgSlot(placeholderIndex, null)
                    : new ConstructorArgSlot(null, concreteTypeArgs[concreteIndex]));
            }

            return true;
        }

        return false;
    }

    private bool TryCreateTraitAliasConstructorBindingSlots(
        IReadOnlyList<TypeId> templateTypeArgs,
        IReadOnlyList<TypeId> concreteTypeArgs,
        SpecializationBindings bindings,
        out bool sawTraitAliasCandidate,
        out List<ConstructorArgSlot>? slots)
    {
        sawTraitAliasCandidate = false;
        slots = null;
        if (templateTypeArgs.Count == 0)
        {
            return false;
        }

        foreach (var impl in EnumerateTraitImpls())
        {
            var traitTypeArgKeys = EnumerateTraitAliasTypeArgKeys(impl);
            for (var traitTypeArgIndex = 0; traitTypeArgIndex < traitTypeArgKeys.Count; traitTypeArgIndex++)
            {
                if (!TryResolveAliasTypeArgKey(traitTypeArgKeys[traitTypeArgIndex], out var aliasInfo) ||
                    aliasInfo.TypeParameterIds.Count != traitTypeArgKeys[traitTypeArgIndex].TypeArguments.Length + templateTypeArgs.Count)
                {
                    continue;
                }

                sawTraitAliasCandidate = true;
                if (TryCreateStructuredTraitAliasConstructorBindingSlots(
                        aliasInfo,
                        traitTypeArgKeys[traitTypeArgIndex].TypeArguments,
                        templateTypeArgs,
                        concreteTypeArgs,
                        bindings,
                        out slots))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<ImplTypeRefKey> EnumerateTraitAliasTypeArgKeys(ImplSymbol impl)
    {
        var keys = new List<ImplTypeRefKey>();
        AddKeys(impl.TraitTypeArgKeys, keys);
        AddKeys(impl.CanonicalTraitTypeArgKeys, keys);
        return keys;
    }

    private static void AddKeys(IReadOnlyList<ImplTypeRefKey> source, List<ImplTypeRefKey> sink)
    {
        foreach (var key in source)
        {
            if (!key.IsEmpty && !sink.Contains(key))
            {
                sink.Add(key);
            }
        }
    }

    private bool TryResolveAliasTypeArgKey(ImplTypeRefKey traitTypeArgKey, out MirTypeAliasInfo aliasInfo)
    {
        aliasInfo = null!;

        if (traitTypeArgKey.SymbolId.IsValid)
        {
            var symbolMatch = EnumerateTypeAliasInfos()
                .FirstOrDefault(alias => alias.AliasId == traitTypeArgKey.SymbolId);
            if (symbolMatch != null)
            {
                aliasInfo = symbolMatch;
                return true;
            }
        }

        if (traitTypeArgKey.TypeId.IsValid)
        {
            var typeMatch = EnumerateTypeAliasInfos()
                .FirstOrDefault(alias => alias.TypeId == traitTypeArgKey.TypeId);
            if (typeMatch != null)
            {
                aliasInfo = typeMatch;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(traitTypeArgKey.Text))
        {
            var nameMatches = EnumerateTypeAliasInfos()
                .Where(alias => string.Equals(alias.Name, traitTypeArgKey.Text, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            if (nameMatches.Count == 1)
            {
                aliasInfo = nameMatches[0];
                return true;
            }
        }

        return false;
    }

    private bool TryCreateStructuredTraitAliasConstructorBindingSlots(
        MirTypeAliasInfo aliasInfo,
        IReadOnlyList<ImplTypeRefKey> fixedAliasArgumentKeys,
        IReadOnlyList<TypeId> templateTypeArgs,
        IReadOnlyList<TypeId> concreteTypeArgs,
        SpecializationBindings bindings,
        out List<ConstructorArgSlot>? slots)
    {
        slots = null;
        if (!aliasInfo.AliasTarget.IsValid ||
            !TryGetTypeDescriptor(aliasInfo.AliasTarget, out var aliasTargetDescriptor) ||
            aliasTargetDescriptor is not TypeDescriptor.TyCon aliasTargetTyCon ||
            aliasTargetTyCon.TypeArgs.Length != concreteTypeArgs.Count)
        {
            return false;
        }

        var fixedAliasArgumentCount = fixedAliasArgumentKeys.Count;
        if (fixedAliasArgumentCount + templateTypeArgs.Count != aliasInfo.TypeParameterIds.Count)
        {
            return false;
        }

        var fixedPositions = new Dictionary<int, int>();
        var placeholderPositions = new Dictionary<int, int>();
        for (var concreteIndex = 0; concreteIndex < aliasTargetTyCon.TypeArgs.Length; concreteIndex++)
        {
            if (!TryGetAliasTargetTypeParameterIndex(
                    aliasInfo,
                    aliasTargetTyCon.TypeArgs[concreteIndex],
                    out var aliasParamIndex))
            {
                continue;
            }

            if (aliasParamIndex < fixedAliasArgumentCount)
            {
                fixedPositions[concreteIndex] = aliasParamIndex;
                continue;
            }

            placeholderPositions[concreteIndex] = aliasParamIndex - fixedAliasArgumentCount;
        }

        if (placeholderPositions.Count != templateTypeArgs.Count ||
            fixedPositions.Values.Distinct().Count() != fixedAliasArgumentCount)
        {
            return false;
        }

        var resolvedBindings = CloneBindings(bindings);
        var fixedTextConcreteTypes = new Dictionary<string, TypeId>(StringComparer.Ordinal);
        foreach (var (concreteIndex, fixedAliasArgumentIndex) in fixedPositions)
        {
            var fixedAliasArgumentKey = fixedAliasArgumentKeys[fixedAliasArgumentIndex];
            if (TryResolveImplTypeRefKeyTypeVariableId(fixedAliasArgumentKey, out var fixedTypeVariableId))
            {
                if (!TryCollectTypeBindings(fixedTypeVariableId, concreteTypeArgs[concreteIndex], resolvedBindings))
                {
                    return false;
                }

                continue;
            }

            if (TryBindTextualFixedAliasArgument(
                    fixedAliasArgumentKey,
                    concreteTypeArgs[concreteIndex],
                    fixedTextConcreteTypes,
                    resolvedBindings))
            {
                continue;
            }

            if (!IsStructurallyIdentifiedDispatchKey(fixedAliasArgumentKey))
            {
                return false;
            }

            var requestedFixedShape = BuildImplTypeShapeNode(fixedAliasArgumentKey);
            var concreteShape = BuildImplementingTypeShape(concreteTypeArgs[concreteIndex]);
            if (ImplSpecializationComparer.CompareNodes(concreteShape, requestedFixedShape) != ImplSpecializationRelation.Equivalent)
            {
                return false;
            }
        }

        foreach (var (concreteIndex, placeholderIndex) in placeholderPositions)
        {
            if (!TryCollectTypeBindings(templateTypeArgs[placeholderIndex], concreteTypeArgs[concreteIndex], resolvedBindings))
            {
                return false;
            }
        }

        MergeBindings(bindings, resolvedBindings);

        slots = new List<ConstructorArgSlot>(concreteTypeArgs.Count);
        for (var concreteIndex = 0; concreteIndex < concreteTypeArgs.Count; concreteIndex++)
        {
            slots.Add(placeholderPositions.TryGetValue(concreteIndex, out var placeholderIndex)
                ? new ConstructorArgSlot(placeholderIndex, null)
                : new ConstructorArgSlot(null, concreteTypeArgs[concreteIndex]));
        }

        return true;
    }

    private bool TryBindTextualFixedAliasArgument(
        ImplTypeRefKey key,
        TypeId concreteType,
        Dictionary<string, TypeId> fixedTextConcreteTypes,
        SpecializationBindings bindings)
    {
        if (string.IsNullOrWhiteSpace(key.Text) ||
            !key.TypeArguments.IsDefaultOrEmpty ||
            !ImplTypeShapeFactory.IsVariableLikeName(key.Text))
        {
            return false;
        }

        if (!fixedTextConcreteTypes.TryGetValue(key.Text, out var existingConcreteType))
        {
            fixedTextConcreteTypes[key.Text] = concreteType;
            return true;
        }

        var probeBindings = CloneBindings(bindings);
        return TryCollectTypeBindings(existingConcreteType, concreteType, probeBindings);
    }

    private bool TryResolveImplTypeRefKeyTypeVariableId(ImplTypeRefKey key, out TypeId typeVariableId)
    {
        typeVariableId = TypeId.None;
        if (!key.TypeArguments.IsDefaultOrEmpty)
        {
            return false;
        }

        var keyTypeId = ResolveImplTypeRefKeyTypeId(key);
        if (keyTypeId.IsValid &&
            (IsMirGenericTypeParameter(keyTypeId) ||
             TryGetTypeDescriptor(keyTypeId, out var descriptor) && descriptor is TypeDescriptor.TypeVar))
        {
            typeVariableId = keyTypeId;
            return true;
        }

        if (key.SymbolId.IsValid)
        {
            var symbolTypeId = new TypeId(key.SymbolId.Value);
            if (IsMirGenericTypeParameter(symbolTypeId) ||
                TryGetTypeDescriptor(symbolTypeId, out descriptor) && descriptor is TypeDescriptor.TypeVar)
            {
                typeVariableId = symbolTypeId;
                return true;
            }
        }

        return false;
    }

    private bool TryGetAliasTargetTypeParameterIndex(
        MirTypeAliasInfo aliasInfo,
        TypeId aliasTargetTypeArg,
        out int aliasParamIndex)
    {
        aliasParamIndex = -1;
        if (!aliasTargetTypeArg.IsValid)
        {
            return false;
        }

        aliasParamIndex = aliasInfo.TypeParameterIds.FindIndex(
            typeParamId => typeParamId.Value == aliasTargetTypeArg.Value);
        if (aliasParamIndex >= 0)
        {
            return true;
        }

        if (!TryGetTypeDescriptor(aliasTargetTypeArg, out var descriptor) ||
            descriptor is not TypeDescriptor.TypeVar typeVariable)
        {
            return false;
        }

        aliasParamIndex = aliasInfo.TypeParameterIds.FindIndex(
            typeParamId => typeParamId.Value == typeVariable.Index);
        return aliasParamIndex >= 0;
    }

    private bool TryCreateConstructorBindingSlots(
        IReadOnlyList<TypeId> templateTypeArgs,
        IReadOnlyList<TypeId> concreteTypeArgs,
        SpecializationBindings bindings,
        out List<ConstructorArgSlot>? slots)
    {
        slots = null;

        if (templateTypeArgs.Count == 0)
        {
            slots = concreteTypeArgs
                .Select(typeId => new ConstructorArgSlot(null, typeId))
                .ToList();
            return true;
        }

        if (!TryMatchConstructorPlaceholderPositions(templateTypeArgs, concreteTypeArgs, bindings, out var positions, out var resolvedBindings))
        {
            return false;
        }

        // Merge resolved bindings into the main bindings without destructive clearing.
        // Only add new type bindings; never overwrite existing ones that are more specific
        // (i.e., already bound to a concrete or canonical type).
        var mergeAdded = 0;
        var mergeSkipped = 0;
        foreach (var (typeVar, boundType) in resolvedBindings!.TypeBindings)
        {
            if (!bindings.TypeBindings.TryGetValue(typeVar, out var existingBoundType))
            {
                bindings.TypeBindings[typeVar] = boundType;
                mergeAdded++;
            }
            else
            {
                mergeSkipped++;
            }
        }
        if (mergeAdded > 0 || mergeSkipped > 0)
        {
        }

        // Constructor bindings are only added, never overwritten (BindConstructorVariable
        // handles the dedup check upstream, but we also guard here for safety).
        foreach (var (constructorVar, binding) in resolvedBindings.ConstructorBindings)
        {
            if (!bindings.ConstructorBindings.ContainsKey(constructorVar))
            {
                bindings.ConstructorBindings[constructorVar] = binding;
            }
        }

        var placeholderIndexByCandidatePosition = new Dictionary<int, int>(positions!.Count);
        for (var templateIndex = 0; templateIndex < positions.Count; templateIndex++)
        {
            placeholderIndexByCandidatePosition[positions[templateIndex]] = templateIndex;
        }

        slots = new List<ConstructorArgSlot>(concreteTypeArgs.Count);
        for (var candidateIndex = 0; candidateIndex < concreteTypeArgs.Count; candidateIndex++)
        {
            if (placeholderIndexByCandidatePosition.TryGetValue(candidateIndex, out var placeholderIndex))
            {
                slots.Add(new ConstructorArgSlot(placeholderIndex, null));
            }
            else
            {
                slots.Add(new ConstructorArgSlot(null, concreteTypeArgs[candidateIndex]));
            }
        }

        return true;
    }

    private bool TryMatchConstructorPlaceholderPositions(
        IReadOnlyList<TypeId> templateTypeArgs,
        IReadOnlyList<TypeId> concreteTypeArgs,
        SpecializationBindings bindings,
        out List<int>? positions,
        out SpecializationBindings? resolvedBindings)
    {
        positions = null;
        resolvedBindings = null;

        if (!TryFindBestConstructorBindingMatch(
                templateTypeArgs,
                concreteTypeArgs,
                0,
                0,
                [],
                CloneBindings(bindings),
                out var match,
                out resolvedBindings))
        {
            return false;
        }

        positions = match!.PlaceholderPositions;
        return true;
    }

    private static void MergeBindings(SpecializationBindings target, SpecializationBindings source)
    {
        foreach (var (typeVar, boundType) in source.TypeBindings)
        {
            target.TypeBindings.TryAdd(typeVar, boundType);
        }

        foreach (var (constructorVar, binding) in source.ConstructorBindings)
        {
            if (!target.ConstructorBindings.ContainsKey(constructorVar))
            {
                target.ConstructorBindings[constructorVar] = binding;
            }
        }
    }

    private bool TryFindBestConstructorBindingMatch(
        IReadOnlyList<TypeId> templateTypeArgs,
        IReadOnlyList<TypeId> concreteTypeArgs,
        int templateIndex,
        int nextConcreteIndex,
        List<int> currentPositions,
        SpecializationBindings baselineBindings,
        out ConstructorBindingMatch? bestMatch,
        out SpecializationBindings? bestBindings)
    {
        bestMatch = null;
        bestBindings = null;
        var ambiguousBestMatch = false;

        if (templateIndex >= templateTypeArgs.Count)
        {
            bestMatch = new ConstructorBindingMatch([.. currentPositions], 0);
            bestBindings = CloneBindings(baselineBindings);
            return true;
        }

        var remainingTemplates = templateTypeArgs.Count - templateIndex;
        if (concreteTypeArgs.Count - nextConcreteIndex < remainingTemplates)
        {
            return false;
        }

        for (var concreteIndex = nextConcreteIndex; concreteIndex < concreteTypeArgs.Count; concreteIndex++)
        {
            var remainingConcreteAfterCurrent = concreteTypeArgs.Count - (concreteIndex + 1);
            if (remainingConcreteAfterCurrent < remainingTemplates - 1)
            {
                break;
            }

            var branchBindings = CloneBindings(baselineBindings);
            if (!TryMatchConstructorPlaceholder(templateTypeArgs[templateIndex], concreteTypeArgs[concreteIndex], branchBindings, out var matchScore))
            {
                continue;
            }

            var branchPositions = new List<int>(currentPositions) { concreteIndex };
            if (!TryFindBestConstructorBindingMatch(
                    templateTypeArgs,
                    concreteTypeArgs,
                    templateIndex + 1,
                    concreteIndex + 1,
                    branchPositions,
                    branchBindings,
                    out var tailMatch,
                    out var tailBindings))
            {
                continue;
            }

            var totalScore = matchScore + tailMatch!.Score;
            if (bestMatch == null || totalScore > bestMatch.Score)
            {
                bestMatch = new ConstructorBindingMatch(tailMatch.PlaceholderPositions, totalScore);
                bestBindings = tailBindings;
                ambiguousBestMatch = false;
                continue;
            }

            if (totalScore == bestMatch.Score &&
                !tailMatch.PlaceholderPositions.SequenceEqual(bestMatch.PlaceholderPositions))
            {
                ambiguousBestMatch = true;
            }
        }

        if (ambiguousBestMatch)
        {
            bestMatch = null;
            bestBindings = null;
            return false;
        }

        return bestMatch != null;
    }

    private bool TryMatchConstructorPlaceholder(
        TypeId templateTypeArg,
        TypeId concreteTypeArg,
        SpecializationBindings bindings,
        out int score)
    {
        score = 0;

        var originalTemplate = templateTypeArg;
        var originalConcrete = concreteTypeArg;
        if (!TryCollectTypeBindings(templateTypeArg, concreteTypeArg, bindings))
        {
            return false;
        }

        score = GetConstructorPlaceholderMatchScore(originalTemplate, originalConcrete);
        return true;
    }

    private int GetConstructorPlaceholderMatchScore(TypeId templateTypeArg, TypeId concreteTypeArg)
    {
        if (templateTypeArg == concreteTypeArg)
        {
            return IsTypeVariableTypeId(templateTypeArg) && IsTypeVariableTypeId(concreteTypeArg)
                ? 80
                : IsTypeVariableTypeId(templateTypeArg)
                ? 20
                : 100;
        }

        if (IsTypeVariableTypeId(templateTypeArg))
        {
            if (IsTypeVariableTypeId(concreteTypeArg))
            {
                return 80;
            }

            return ContainsOpenTypeVariable(concreteTypeArg) ? 60 : 20;
        }

        return 40;
    }

    private bool IsTypeVariableTypeId(TypeId typeId)
    {
        return typeId.IsValid &&
               TryGetTypeDescriptor(typeId, out var descriptor) &&
               descriptor is TypeDescriptor.TypeVar;
    }
}
