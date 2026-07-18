using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private sealed class SpecializationBindingCollectorService(MirGenericSpecializer owner)
    {
        public SpecializationBindings Collect(MirFunc template, SpecializationSignature signature)
        {
            var bindings = new SpecializationBindings(
                new Dictionary<int, TypeId>(),
                new Dictionary<int, ConstructorBinding>());
            var templateParameters = owner.GetCachedTemplateParameters(template);
            for (var i = 0; i < templateParameters.Count && i < signature.ParameterTypes.Count; i++)
            {
                owner.CollectTypeBindings(templateParameters[i].TypeId, signature.ParameterTypes[i], bindings);
            }

            owner.CollectTypeBindings(template.ReturnType, signature.ReturnType, bindings);
            CollectBodyTypeBindings(template, bindings);
            PropagateConstructorBindingsFromResolvedTypes(template, bindings);
            return bindings;
        }

        private void PropagateConstructorBindingsFromResolvedTypes(MirFunc template, SpecializationBindings bindings)
        {
            var maxIterations = 10;
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var changed = false;

                foreach (var local in template.Locals)
                {
                    if (!owner.ContainsOpenTypeVariable(local.TypeId))
                    {
                        continue;
                    }

                    var subType = owner.SubstituteTypeId(local.TypeId, bindings);
                    if (!subType.IsValid || subType.Equals(local.TypeId) || owner.ContainsOpenTypeVariable(subType))
                    {
                        continue;
                    }

                    if (TryCollectBindingFromResolvedType(local.TypeId, subType, bindings))
                    {
                        changed = true;
                    }
                }

                if (!changed)
                {
                    break;
                }
            }
        }

        private void CollectBodyTypeBindings(MirFunc template, SpecializationBindings bindings)
        {
            if (template.BasicBlocks.Count == 0)
            {
                return;
            }

            var templateLocalTypes = template.Locals
                .GroupBy(local => local.Id)
                .ToDictionary(group => group.Key, group => group.Last().TypeId);

            var changed = true;
            while (changed)
            {
                changed = false;

                foreach (var block in template.BasicBlocks)
                {
                    foreach (var instruction in block.Instructions)
                    {
                        if (TryCollectInstructionTypeBindings(instruction, templateLocalTypes, bindings))
                        {
                            changed = true;
                        }
                    }

                    if (block.Terminator is MirReturn { Value: { } returnValue } &&
                        TryCollectBindingFromResolvedType(
                            ResolveOperandTemplateType(returnValue, templateLocalTypes),
                            SubstituteOperandType(returnValue, templateLocalTypes, bindings),
                            bindings))
                    {
                        changed = true;
                    }
                }
            }
        }

        private bool TryCollectInstructionTypeBindings(
            MirInstruction instruction,
            IReadOnlyDictionary<LocalId, TypeId> templateLocalTypes,
            SpecializationBindings bindings)
        {
            return instruction switch
            {
                MirAssign assign => TryCollectBindingFromResolvedType(
                    owner.ResolvePlaceType(assign.Target, templateLocalTypes),
                    SubstituteOperandType(assign.Source, templateLocalTypes, bindings),
                    bindings),

                MirCaseInject injection =>
                    TryCollectBindingFromResolvedType(
                        injection.SourceTypeId,
                        SubstituteOperandType(injection.Operand, templateLocalTypes, bindings),
                        bindings) |
                    TryCollectBindingFromResolvedType(
                        injection.TargetTypeId,
                        SubstituteOperandType(injection.Target, templateLocalTypes, bindings),
                        bindings),

                MirLoad load => TryCollectBindingFromResolvedType(
                    owner.ResolvePlaceType(load.Target, templateLocalTypes),
                    SubstituteOperandType(load.Source, templateLocalTypes, bindings),
                    bindings),

                MirStore store => TryCollectBindingFromResolvedType(
                    owner.ResolvePlaceType(store.Target, templateLocalTypes),
                    SubstituteOperandType(store.Value, templateLocalTypes, bindings),
                    bindings),

                MirCopy copy => TryCollectBindingFromResolvedType(
                    owner.ResolvePlaceType(copy.Target, templateLocalTypes),
                    SubstitutePlaceType(copy.Source, templateLocalTypes, bindings),
                    bindings),

                MirMove move => TryCollectBindingFromResolvedType(
                    owner.ResolvePlaceType(move.Target, templateLocalTypes),
                    SubstitutePlaceType(move.Source, templateLocalTypes, bindings),
                    bindings),

                MirCall call => TryCollectCallTypeBindings(call, templateLocalTypes, bindings),
                _ => false
            };
        }

        private bool TryCollectCallTypeBindings(
            MirCall call,
            IReadOnlyDictionary<LocalId, TypeId> templateLocalTypes,
            SpecializationBindings bindings)
        {
            var changed = false;

            if (call.Target != null)
            {
                var templateTargetType = owner.ResolvePlaceType(call.Target, templateLocalTypes);
                var substitutedTargetType = owner.SubstituteTypeId(templateTargetType, bindings);
                if (TryResolveCallResultType(call.Function, substitutedTargetType, templateLocalTypes, bindings, out var resolvedResultType) &&
                    TryCollectBindingFromResolvedType(templateTargetType, resolvedResultType, bindings))
                {
                    changed = true;
                }
                else if (TryResolveCallResultViaArgumentInference(call, templateLocalTypes, bindings, out resolvedResultType) &&
                         TryCollectBindingFromResolvedType(templateTargetType, resolvedResultType, bindings))
                {
                    changed = true;
                }
            }

            if (TryResolveCallParameterTypes(call.Function, templateLocalTypes, bindings, out var resolvedParameterTypes))
            {
                for (var i = 0; i < call.Arguments.Count && i < resolvedParameterTypes.Count; i++)
                {
                    if (TryCollectBindingFromResolvedType(
                            ResolveOperandTemplateType(call.Arguments[i], templateLocalTypes),
                            resolvedParameterTypes[i],
                            bindings))
                    {
                        changed = true;
                    }
                }
            }
            else if (TryResolveCallParametersViaArgumentInference(call, templateLocalTypes, bindings, out resolvedParameterTypes))
            {
                for (var i = 0; i < call.Arguments.Count && i < resolvedParameterTypes.Count; i++)
                {
                    if (TryCollectBindingFromResolvedType(
                            ResolveOperandTemplateType(call.Arguments[i], templateLocalTypes),
                            resolvedParameterTypes[i],
                            bindings))
                    {
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private bool TryResolveCallResultViaArgumentInference(
            MirCall call,
            IReadOnlyDictionary<LocalId, TypeId> templateLocalTypes,
            SpecializationBindings bindings,
            out TypeId resultType)
        {
            resultType = TypeId.None;

            var functionTypeId = ResolveOperandTemplateType(call.Function, templateLocalTypes);
            if (!functionTypeId.IsValid ||
                !owner.TryResolveFlattenedFunctionType(functionTypeId, out var declaredParamTypes, out var declaredResultType))
            {
                return false;
            }

            if (!owner.ContainsOpenTypeVariable(declaredResultType) &&
                declaredParamTypes.All(p => !owner.ContainsOpenTypeVariable(p)))
            {
                return false;
            }

            var inferenceBindings = new SpecializationBindings(
                new Dictionary<int, TypeId>(bindings.TypeBindings),
                new Dictionary<int, ConstructorBinding>(bindings.ConstructorBindings));

            for (var i = 0; i < call.Arguments.Count && i < declaredParamTypes.Count; i++)
            {
                var concreteArgType = SubstituteOperandType(call.Arguments[i], templateLocalTypes, bindings);
                if (!owner.TryCollectTypeBindings(declaredParamTypes[i], concreteArgType, inferenceBindings))
                {
                    return false;
                }
            }

            var resolvedResult = owner.SubstituteTypeId(declaredResultType, inferenceBindings);
            if (!resolvedResult.IsValid || owner.ContainsOpenTypeVariable(resolvedResult))
            {
                return false;
            }

            resultType = resolvedResult;
            return true;
        }

        private bool TryResolveCallParametersViaArgumentInference(
            MirCall call,
            IReadOnlyDictionary<LocalId, TypeId> templateLocalTypes,
            SpecializationBindings bindings,
            out List<TypeId> parameterTypes)
        {
            parameterTypes = [];

            var functionTypeId = ResolveOperandTemplateType(call.Function, templateLocalTypes);
            if (!functionTypeId.IsValid ||
                !owner.TryResolveFlattenedFunctionType(functionTypeId, out var declaredParamTypes, out _))
            {
                return false;
            }

            var inferenceBindings = new SpecializationBindings(
                new Dictionary<int, TypeId>(bindings.TypeBindings),
                new Dictionary<int, ConstructorBinding>(bindings.ConstructorBindings));

            for (var i = 0; i < call.Arguments.Count && i < declaredParamTypes.Count; i++)
            {
                var concreteArgType = SubstituteOperandType(call.Arguments[i], templateLocalTypes, bindings);
                if (!owner.TryCollectTypeBindings(declaredParamTypes[i], concreteArgType, inferenceBindings))
                {
                    return false;
                }
            }

            foreach (var declaredParam in declaredParamTypes)
            {
                var resolvedParam = owner.SubstituteTypeId(declaredParam, inferenceBindings);
                parameterTypes.Add(resolvedParam);
            }

            return parameterTypes.Count > 0;
        }

        private bool TryResolveCallParameterTypes(
            MirOperand functionOperand,
            IReadOnlyDictionary<LocalId, TypeId> templateLocalTypes,
            SpecializationBindings bindings,
            out List<TypeId> parameterTypes)
        {
            parameterTypes = [];

            var functionTypeId = SubstituteOperandType(functionOperand, templateLocalTypes, bindings);
            return owner.TryResolveFlattenedFunctionType(functionTypeId, out parameterTypes, out _);
        }

        private bool TryResolveCallResultType(
            MirOperand functionOperand,
            TypeId preferredResultType,
            IReadOnlyDictionary<LocalId, TypeId> templateLocalTypes,
            SpecializationBindings bindings,
            out TypeId resultType)
        {
            resultType = TypeId.None;

            var functionTypeId = SubstituteOperandType(functionOperand, templateLocalTypes, bindings);
            if (!owner.TryResolveFlattenedFunctionType(functionTypeId, out _, out resultType))
            {
                return false;
            }

            if (preferredResultType.IsValid &&
                !owner.ContainsOpenTypeVariable(preferredResultType) &&
                resultType.IsValid &&
                !owner.ContainsOpenTypeVariable(resultType))
            {
                resultType = preferredResultType;
            }

            return resultType.IsValid;
        }

        private bool TryCollectBindingFromResolvedType(
            TypeId templateType,
            TypeId concreteType,
            SpecializationBindings bindings)
        {
            if (!templateType.IsValid || !concreteType.IsValid)
            {
                return false;
            }

            var snapshot = CloneBindings(bindings);
            if (!owner.TryCollectTypeBindings(templateType, concreteType, bindings))
            {
                return false;
            }

            return !AreSameBindings(snapshot, bindings);
        }

        private TypeId ResolveOperandTemplateType(
            MirOperand operand,
            IReadOnlyDictionary<LocalId, TypeId> templateLocalTypes)
        {
            return operand switch
            {
                MirPlace place => owner.ResolvePlaceType(place, templateLocalTypes),
                MirFunctionRef functionRef when owner.TryResolveFunctionRefType(functionRef, out var functionTypeId) => functionTypeId,
                _ when operand.TypeId.IsValid => operand.TypeId,
                _ => TypeId.None
            };
        }

        private TypeId SubstituteOperandType(
            MirOperand operand,
            IReadOnlyDictionary<LocalId, TypeId> templateLocalTypes,
            SpecializationBindings bindings)
        {
            return owner.SubstituteTypeId(ResolveOperandTemplateType(operand, templateLocalTypes), bindings);
        }

        private TypeId SubstitutePlaceType(
            MirPlace place,
            IReadOnlyDictionary<LocalId, TypeId> templateLocalTypes,
            SpecializationBindings bindings)
        {
            return owner.SubstituteTypeId(owner.ResolvePlaceType(place, templateLocalTypes), bindings);
        }

        private static bool AreSameBindings(SpecializationBindings left, SpecializationBindings right)
        {
            if (left.TypeBindings.Count != right.TypeBindings.Count ||
                left.ConstructorBindings.Count != right.ConstructorBindings.Count)
            {
                return false;
            }

            foreach (var (typeVariable, boundType) in left.TypeBindings)
            {
                if (!right.TypeBindings.TryGetValue(typeVariable, out var otherType) ||
                    otherType != boundType)
                {
                    return false;
                }
            }

            foreach (var (constructorVariable, binding) in left.ConstructorBindings)
            {
                if (!right.ConstructorBindings.TryGetValue(constructorVariable, out var otherBinding) ||
                    otherBinding.Constructor != binding.Constructor ||
                    !otherBinding.Slots.SequenceEqual(binding.Slots))
                {
                    return false;
                }
            }

            return true;
        }

        private static SpecializationBindings CloneBindings(SpecializationBindings source)
        {
            return new SpecializationBindings(
                new Dictionary<int, TypeId>(source.TypeBindings),
                source.ConstructorBindings.ToDictionary(
                    entry => entry.Key,
                    entry => new ConstructorBinding(
                        entry.Value.Constructor,
                        entry.Value.Slots
                            .Select(slot => new ConstructorArgSlot(slot.PlaceholderIndex, slot.FixedTypeId))
                            .ToList())));
        }
    }
}
