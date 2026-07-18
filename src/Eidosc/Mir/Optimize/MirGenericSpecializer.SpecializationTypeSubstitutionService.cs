using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private readonly struct SpecializationTypeSubstitutionService(MirGenericSpecializer owner)
    {
        public TypeId SubstituteTypeId(TypeId typeId, SpecializationBindings bindings)
        {
            return SubstituteTypeId(typeId, bindings, []);
        }

        public TypeId SubstituteTypeId(TypeId typeId, SpecializationBindings bindings, HashSet<int> resolvingTypeIds)
        {
            if (!typeId.IsValid)
            {
                return typeId;
            }

            if (!resolvingTypeIds.Add(typeId.Value))
            {
                return typeId;
            }

            try
            {
                if (bindings.TypeBindings.TryGetValue(typeId.Value, out var boundType))
                {
                    return SubstituteTypeId(boundType, bindings, resolvingTypeIds);
                }

                if (!owner.TryGetTypeDescriptor(typeId, out var descriptor))
                {
                    return typeId;
                }

                if (descriptor is TypeDescriptor.TypeVar typeVariable &&
                    bindings.TypeBindings.TryGetValue(GetTypeVariableIndexBindingKey(typeVariable.Index), out boundType))
                {
                    return SubstituteTypeId(boundType, bindings, resolvingTypeIds);
                }

                return TrySubstituteTypeDescriptor(descriptor, bindings, resolvingTypeIds, out var substitutedDescriptor, out var changed) && changed
                    ? owner.GetOrCreateDynamicTypeId(substitutedDescriptor)
                    : typeId;
            }
            finally
            {
                resolvingTypeIds.Remove(typeId.Value);
            }
        }

        public TypeId[] SubstituteTypeIds(
            IReadOnlyList<TypeId> typeIds,
            SpecializationBindings bindings,
            HashSet<int> resolvingTypeIds,
            out bool changed)
        {
            changed = false;
            var substituted = new TypeId[typeIds.Count];
            for (var i = 0; i < typeIds.Count; i++)
            {
                var next = SubstituteTypeId(typeIds[i], bindings, resolvingTypeIds);
                substituted[i] = next;
                changed |= next != typeIds[i];
            }

            return substituted;
        }

        private bool TrySubstituteTypeDescriptor(
            TypeDescriptor descriptor,
            SpecializationBindings bindings,
            HashSet<int> resolvingTypeIds,
            out TypeDescriptor substitutedDescriptor,
            out bool changed)
        {
            substitutedDescriptor = descriptor;
            changed = false;

            switch (descriptor)
            {
                case TypeDescriptor.TypeVar:
                    return true;
                case TypeDescriptor.Function function:
                    var substitutedParameters = SubstituteTypeIds(function.ParamTypes, bindings, resolvingTypeIds, out var parametersChanged);
                    var substitutedReturn = SubstituteTypeId(function.ReturnType, bindings, resolvingTypeIds);
                    changed = parametersChanged || substitutedReturn != function.ReturnType;
                    substitutedDescriptor = changed
                        ? new TypeDescriptor.Function(substitutedParameters, substitutedReturn, function.Effects)
                        : descriptor;
                    return true;
                case TypeDescriptor.Tuple tuple:
                    var substitutedFields = SubstituteTypeIds(tuple.FieldTypes, bindings, resolvingTypeIds, out changed);
                    substitutedDescriptor = changed
                        ? new TypeDescriptor.Tuple(substitutedFields)
                        : descriptor;
                    return true;
                case TypeDescriptor.TyCon tyCon:
                    return TrySubstituteTyConDescriptor(tyCon, bindings, resolvingTypeIds, out substitutedDescriptor, out changed);
                case TypeDescriptor.Ref reference:
                    var substitutedInner = SubstituteTypeId(reference.Inner, bindings, resolvingTypeIds);
                    changed = substitutedInner != reference.Inner;
                    substitutedDescriptor = changed ? new TypeDescriptor.Ref(substitutedInner) : descriptor;
                    return true;
                case TypeDescriptor.MutRef reference:
                    substitutedInner = SubstituteTypeId(reference.Inner, bindings, resolvingTypeIds);
                    changed = substitutedInner != reference.Inner;
                    substitutedDescriptor = changed ? new TypeDescriptor.MutRef(substitutedInner) : descriptor;
                    return true;
                default:
                    return true;
            }
        }

        private bool TrySubstituteTyConDescriptor(
            TypeDescriptor.TyCon tyCon,
            SpecializationBindings bindings,
            HashSet<int> resolvingTypeIds,
            out TypeDescriptor substitutedDescriptor,
            out bool changed)
        {
            var substitutedConstructor = tyCon.Constructor;
            var substitutedTypeArgs = SubstituteTypeIds(tyCon.TypeArgs, bindings, resolvingTypeIds, out changed);

            if (TryParseConstructorVarIndex(tyCon.Constructor, out var constructorVarIndex) &&
                bindings.ConstructorBindings.TryGetValue(constructorVarIndex, out var constructorBinding))
            {
                substitutedConstructor = constructorBinding.Constructor;
                var reboundTypeArgs = new List<TypeId>(constructorBinding.Slots.Count);
                foreach (var slot in constructorBinding.Slots)
                {
                    if (slot.PlaceholderIndex is { } placeholderIndex)
                    {
                        if (placeholderIndex < 0 || placeholderIndex >= substitutedTypeArgs.Length)
                        {
                            substitutedDescriptor = tyCon;
                            changed = false;
                            return false;
                        }

                        reboundTypeArgs.Add(substitutedTypeArgs[placeholderIndex]);
                        continue;
                    }

                    if (slot.FixedTypeId is not { } fixedTypeId)
                    {
                        substitutedDescriptor = tyCon;
                        changed = false;
                        return false;
                    }

                    reboundTypeArgs.Add(SubstituteTypeId(fixedTypeId, bindings, resolvingTypeIds));
                }

                substitutedTypeArgs = [.. reboundTypeArgs];
                changed = true;
            }

            var substitutedEffectArgs = new GenericEffectArgumentDescriptor[tyCon.EffectArgs.Length];
            for (var index = 0; index < tyCon.EffectArgs.Length; index++)
            {
                var effectArgument = tyCon.EffectArgs[index];
                var substitutedTypeId = SubstituteTypeId(effectArgument.TypeId, bindings, resolvingTypeIds);
                substitutedEffectArgs[index] = effectArgument with { TypeId = substitutedTypeId };
                changed |= substitutedTypeId != effectArgument.TypeId;
            }

            substitutedDescriptor = changed
                ? new TypeDescriptor.TyCon(substitutedConstructor, substitutedTypeArgs)
                {
                    ValueArgs = tyCon.ValueArgs,
                    EffectArgs = substitutedEffectArgs
                }
                : tyCon;
            return true;
        }
    }
}
