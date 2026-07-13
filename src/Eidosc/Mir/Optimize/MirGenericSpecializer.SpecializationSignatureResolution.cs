using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private bool TryResolveSignature(
        MirCall call,
        MirFunc template,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out SpecializationSignature signature)
    {
        signature = default!;

        var templateParameters = GetCachedTemplateParameters(template);
        if (templateParameters.Count != call.Arguments.Count)
        {
            return false;
        }

        var inferenceBindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());
        if (!TryApplyExplicitTypeArgumentBindings(call, template, inferenceBindings))
        {
            return false;
        }
        var targetBindingsSatisfied = TryCollectTargetBindingsForTemplateCall(
            call,
            templateParameters,
            template.ReturnType,
            localTypes,
            ref inferenceBindings);

        var deferredArgumentBindingIndices = new List<int>();
        for (var i = 0; i < templateParameters.Count; i++)
        {
            var resolvedArgumentType = ResolveOperandType(call.Arguments[i], localTypes);
            if (templateParameters[i].TypeId.IsValid &&
                ShouldUseArgumentTypeForTemplateInference(
                    call.Arguments[i],
                    resolvedArgumentType,
                    templateParameters[i].TypeId))
            {
                if (!TryCollectTypeBindingsForInference(templateParameters[i].TypeId, resolvedArgumentType, inferenceBindings))
                {
                    deferredArgumentBindingIndices.Add(i);
                }
            }
        }

        foreach (var i in deferredArgumentBindingIndices)
        {
            var resolvedArgumentType = ResolveOperandType(call.Arguments[i], localTypes);
            if (TryCollectTypeBindingsForInference(templateParameters[i].TypeId, resolvedArgumentType, inferenceBindings))
            {
                continue;
            }

            var substitutedArgumentType = SubstituteTypeId(templateParameters[i].TypeId, inferenceBindings);
            if (ShouldUseExpectedFunctionValueTypeForArgument(call.Arguments[i], substitutedArgumentType))
            {
                continue;
            }

            return false;
        }

        if (!targetBindingsSatisfied &&
            !TryCollectTargetBindingsForTemplateCall(
                call,
                templateParameters,
                template.ReturnType,
                localTypes,
                ref inferenceBindings))
        {
            return false;
        }

        var returnType = ResolveReturnType(call, template, localTypes);
        if (template.ReturnType.IsValid)
        {
            var substitutedReturnType = SubstituteTypeId(template.ReturnType, inferenceBindings);
            if (substitutedReturnType.IsValid &&
                substitutedReturnType != template.ReturnType &&
                !ContainsOpenTypeVariable(substitutedReturnType) &&
                call.Target == null)
            {
                returnType = substitutedReturnType;
            }
            else if ((!returnType.IsValid || ContainsOpenTypeVariable(returnType)) && substitutedReturnType.IsValid)
            {
                returnType = substitutedReturnType;
            }
        }

        if (returnType.IsValid &&
            template.ReturnType.IsValid &&
            !TryCollectTypeBindings(template.ReturnType, returnType, inferenceBindings))
        {
            if (!BaseTypes.IsBuiltIn(template.ReturnType) &&
                !ContainsOpenTypeVariable(returnType))
            {
                inferenceBindings.TypeBindings[template.ReturnType.Value] = returnType;
            }
            else
            {
                return false;
            }
        }

        var parameterTypes = new List<TypeId>(templateParameters.Count);
        for (var i = 0; i < templateParameters.Count; i++)
        {
            var templateParameter = templateParameters[i];
            var resolvedArgumentType = ResolveOperandType(call.Arguments[i], localTypes);
            var substitutedParameterType = templateParameter.TypeId.IsValid
                ? SubstituteTypeId(templateParameter.TypeId, inferenceBindings)
                : TypeId.None;
            var resolvedParameterType =
                substitutedParameterType.IsValid &&
                (!ContainsOpenTypeVariable(substitutedParameterType) ||
                 substitutedParameterType != templateParameter.TypeId)
                ? substitutedParameterType
                : (template.GenericParameterCount > 0 || ContainsOpenTypeVariable(templateParameter.TypeId)) &&
                  resolvedArgumentType.IsValid &&
                  !ContainsOpenTypeVariable(resolvedArgumentType)
                ? resolvedArgumentType
                : templateParameter.TypeId.IsValid
                ? templateParameter.TypeId
            : resolvedArgumentType;
            if (!resolvedParameterType.IsValid)
            {
                return false;
            }

            parameterTypes.Add(resolvedParameterType);
        }

        if (!returnType.IsValid)
        {
            return false;
        }

        signature = new SpecializationSignature(returnType, parameterTypes, GetCallValueArguments(call));
        if (TryDefaultErasedOpenTypeVariables(template, signature, out var defaultedSignature))
        {
            signature = defaultedSignature;
        }

        return true;
    }

    private bool TryResolveFunctionValueSignature(
        MirFunctionRef functionRef,
        MirFunc template,
        TypeId expectedFunctionTypeId,
        out SpecializationSignature signature)
    {
        signature = default!;

        var templateParameterCount = GetTemplateParameterCount(template);
        if (!expectedFunctionTypeId.IsValid ||
            !TryResolveFlattenedFunctionType(expectedFunctionTypeId, out var parameterTypes, out var returnType) ||
            parameterTypes.Count < templateParameterCount ||
            !returnType.IsValid)
        {
            return false;
        }

        var directParameterTypes = new List<TypeId>(templateParameterCount);
        for (var i = 0; i < templateParameterCount; i++)
        {
            directParameterTypes.Add(parameterTypes[i]);
        }
        var directReturnType = returnType;
        if (parameterTypes.Count > templateParameterCount &&
            !TryRebuildFunctionValueReturnType(
                template.ReturnType,
                CopyTypeIdSuffix(parameterTypes, templateParameterCount),
                returnType,
                out directReturnType))
        {
            return false;
        }

        signature = new SpecializationSignature(
            directReturnType,
            directParameterTypes,
            functionRef.ValueArguments);
        if (TryDefaultErasedOpenTypeVariables(template, signature, out var defaultedSignature))
        {
            signature = defaultedSignature;
        }

        return true;
    }

    private bool TryResolveConcreteCallShapeSignature(
        MirCall call,
        MirFunc template,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out SpecializationSignature signature)
    {
        signature = default!;

        var templateParameterCount = GetTemplateParameterCount(template);
        if (templateParameterCount != call.Arguments.Count)
        {
            return false;
        }

        if (ContainsOpenConstructorBinding(template.ReturnType) ||
            GetCachedTemplateParameters(template).Any(parameter => ContainsOpenConstructorBinding(parameter.TypeId)))
        {
            return false;
        }

        var parameterTypes = new List<TypeId>(templateParameterCount);
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var argumentType = ResolveOperandType(call.Arguments[i], localTypes);
            if (!argumentType.IsValid || ContainsOpenTypeVariable(argumentType))
            {
                return false;
            }

            parameterTypes.Add(argumentType);
        }

        var returnType = ResolveReturnType(call, template, localTypes);
        if (!returnType.IsValid || ContainsOpenTypeVariable(returnType))
        {
            return false;
        }

        signature = new SpecializationSignature(returnType, parameterTypes, GetCallValueArguments(call));
        if (TryDefaultErasedOpenTypeVariables(template, signature, out var defaultedSignature))
        {
            signature = defaultedSignature;
        }

        return true;
    }

    private bool TryResolvePartialBindingFunctionValueSignature(
        MirFunctionRef functionRef,
        MirFunc template,
        IReadOnlyList<MirOperand> boundArguments,
        TypeId expectedFunctionTypeId,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out SpecializationSignature signature)
    {
        signature = default!;

        var templateParameters = GetCachedTemplateParameters(template);
        if (!expectedFunctionTypeId.IsValid ||
            !TryResolveFlattenedFunctionType(expectedFunctionTypeId, out var remainingParameterTypes, out var returnType) ||
            boundArguments.Count + remainingParameterTypes.Count != templateParameters.Count ||
            !returnType.IsValid)
        {
            return false;
        }

        var inferenceBindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());
        var bindingCall = new MirCall
        {
            Function = functionRef,
            Arguments = CloneOperands(boundArguments)
        };
        if (!TryApplyExplicitTypeArgumentBindings(bindingCall, template, inferenceBindings))
        {
            return false;
        }

        for (var index = 0; index < boundArguments.Count; index++)
        {
            var resolvedArgumentType = ResolveOperandType(boundArguments[index], localTypes);
            if (!ShouldUseArgumentTypeForTemplateInference(
                    boundArguments[index],
                    resolvedArgumentType,
                    templateParameters[index].TypeId))
            {
                continue;
            }

            if (!TryCollectTypeBindingsForInference(
                    templateParameters[index].TypeId,
                    resolvedArgumentType,
                    inferenceBindings))
            {
                return false;
            }
        }

        for (var parameterIndex = boundArguments.Count; parameterIndex < templateParameters.Count; parameterIndex++)
        {
            var remainingIndex = parameterIndex - boundArguments.Count;
            if (!TryCollectTypeBindingsForInference(
                    templateParameters[parameterIndex].TypeId,
                    remainingParameterTypes[remainingIndex],
                    inferenceBindings))
            {
                return false;
            }
        }

        if (template.ReturnType.IsValid &&
            !TryCollectTypeBindingsForInference(template.ReturnType, returnType, inferenceBindings))
        {
            return false;
        }

        var parameterTypes = new List<TypeId>(templateParameters.Count);
        foreach (var templateParameter in templateParameters)
        {
            var parameterType = SubstituteTypeId(templateParameter.TypeId, inferenceBindings);
            if (!parameterType.IsValid)
            {
                return false;
            }

            parameterTypes.Add(parameterType);
        }

        var resolvedReturnType = SubstituteTypeId(template.ReturnType, inferenceBindings);
        if (!resolvedReturnType.IsValid)
        {
            return false;
        }

        signature = new SpecializationSignature(
            resolvedReturnType,
            parameterTypes,
            functionRef.ValueArguments);
        if (TryDefaultErasedOpenTypeVariables(template, signature, out var defaultedSignature))
        {
            signature = defaultedSignature;
        }

        return true;
    }

    private bool TryRebuildFunctionValueReturnType(
        TypeId templateReturnType,
        IReadOnlyList<TypeId> remainingParameterTypes,
        TypeId finalReturnType,
        out TypeId returnType)
    {
        returnType = TypeId.None;
        if (remainingParameterTypes.Count == 0)
        {
            returnType = finalReturnType;
            return returnType.IsValid;
        }

        if (!finalReturnType.IsValid ||
            remainingParameterTypes.Any(static typeId => !typeId.IsValid))
        {
            return false;
        }

        if (templateReturnType.IsValid &&
            TryGetTypeDescriptor(templateReturnType, out var descriptor) &&
            descriptor is TypeDescriptor.Function templateFunction &&
            templateFunction.ParamTypes.Length > 0)
        {
            if (remainingParameterTypes.Count < templateFunction.ParamTypes.Length)
            {
                return false;
            }

            var directParameterTypes = remainingParameterTypes
                .Take(templateFunction.ParamTypes.Length)
                .ToArray();
            var directFinalReturnType = finalReturnType;
            if (remainingParameterTypes.Count > templateFunction.ParamTypes.Length)
            {
                if (!TryRebuildFunctionValueReturnType(
                        templateFunction.ReturnType,
                        remainingParameterTypes.Skip(templateFunction.ParamTypes.Length).ToList(),
                        finalReturnType,
                        out directFinalReturnType))
                {
                    return false;
                }
            }

            returnType = GetOrCreateDynamicTypeId(
                new TypeDescriptor.Function(directParameterTypes, directFinalReturnType, templateFunction.Effects));
            return returnType.IsValid;
        }

        returnType = GetOrCreateDynamicTypeId(
            new TypeDescriptor.Function([.. remainingParameterTypes], finalReturnType));
        return returnType.IsValid;
    }

    private void CollectOpenTypeVariables(TypeId typeId, ISet<int> openTypeVariables)
    {
        MirGenericAnalysis.CollectOpenTypeVariables(
            typeId,
            openTypeVariables,
            _dynamicTypes.DescriptorByIdDict,
            _dynamicTypes.KeyByIdDict,
            IsOpenUninternedType);
    }

    private bool TryResolvePartialSignature(
        MirCall call,
        MirFunc template,
        IReadOnlyList<MirOperand> combinedArguments,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out SpecializationSignature signature)
    {
        signature = default!;
        var templateParameterCount = GetTemplateParameterCount(template);
        if (combinedArguments.Count > templateParameterCount)
        {
            return false;
        }

        if (call.Target is { Kind: PlaceKind.Local } targetLocal)
        {
            var targetFunctionTypeId = ResolvePlaceType(targetLocal, localTypes);
            if (TryResolveFlattenedFunctionType(targetFunctionTypeId, out var remainingParameterTypes, out var remainingResultType) &&
                combinedArguments.Count + remainingParameterTypes.Count == templateParameterCount &&
                remainingResultType.IsValid)
            {
                var parameterTypes = new List<TypeId>(templateParameterCount);
                foreach (var argument in combinedArguments)
                {
                    var resolvedArgumentType = ResolveOperandType(argument, localTypes);
                    if (!resolvedArgumentType.IsValid)
                    {
                        return false;
                    }

                    parameterTypes.Add(resolvedArgumentType);
                }

                parameterTypes.AddRange(remainingParameterTypes);
                signature = new SpecializationSignature(
                    remainingResultType,
                    parameterTypes,
                    GetCallValueArguments(call));
                if (TryDefaultErasedOpenTypeVariables(template, signature, out var defaultedSignature))
                {
                    signature = defaultedSignature;
                }

                return true;
            }
        }

        var templateParameters = GetCachedTemplateParameters(template);
        var bindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());
        if (!TryApplyExplicitTypeArgumentBindings(call, template, bindings))
        {
            return false;
        }

        for (var index = 0; index < combinedArguments.Count; index++)
        {
            var resolvedArgumentType = ResolveOperandType(combinedArguments[index], localTypes);
            if (!resolvedArgumentType.IsValid ||
                !TryCollectTypeBindings(templateParameters[index].TypeId, resolvedArgumentType, bindings))
            {
                return false;
            }
        }

        if (call.Target is { Kind: PlaceKind.Local } fallbackTargetLocal)
        {
            var fallbackTargetFunctionTypeId = ResolvePlaceType(fallbackTargetLocal, localTypes);
            if (TryResolveFlattenedFunctionType(
                    fallbackTargetFunctionTypeId,
                    out var fallbackRemainingParameterTypes,
                    out var fallbackRemainingResultType) &&
                combinedArguments.Count + fallbackRemainingParameterTypes.Count == templateParameterCount)
            {
                for (var parameterIndex = combinedArguments.Count; parameterIndex < templateParameterCount; parameterIndex++)
                {
                    var remainingIndex = parameterIndex - combinedArguments.Count;
                    if (remainingIndex >= fallbackRemainingParameterTypes.Count)
                    {
                        return false;
                    }

                    if (!TryCollectTypeBindings(
                            templateParameters[parameterIndex].TypeId,
                            fallbackRemainingParameterTypes[remainingIndex],
                            bindings))
                    {
                        return false;
                    }
                }

                if (fallbackRemainingResultType.IsValid)
                {
                    if (!TryCollectTypeBindings(template.ReturnType, fallbackRemainingResultType, bindings))
                    {
                        return false;
                    }
                }
            }
        }

        var resolvedParameterTypes = new List<TypeId>(templateParameterCount);
        foreach (var templateParameter in templateParameters)
        {
            var resolvedParameterType = SubstituteTypeId(templateParameter.TypeId, bindings);
            if (!resolvedParameterType.IsValid)
            {
                return false;
            }

            resolvedParameterTypes.Add(resolvedParameterType);
        }

        var resolvedReturnType = SubstituteTypeId(template.ReturnType, bindings);
        if (!resolvedReturnType.IsValid)
        {
            return false;
        }

        signature = new SpecializationSignature(
            resolvedReturnType,
            resolvedParameterTypes,
            GetCallValueArguments(call));
        if (TryDefaultErasedOpenTypeVariables(template, signature, out var defaultedPartialSignature))
        {
            signature = defaultedPartialSignature;
        }

        return true;
    }

    private bool TryApplyExplicitTypeArgumentBindings(
        MirCall call,
        MirFunc template,
        SpecializationBindings bindings)
    {
        if (call.Function is not MirFunctionRef { TypeArgumentIds.Count: > 0 } functionRef)
        {
            return true;
        }

        var templateTypeParameters = GetTemplateTypeParameterIds(template);
        if (functionRef.TypeArgumentIds.Count != templateTypeParameters.Count)
        {
            return true;
        }

        for (var index = 0; index < functionRef.TypeArgumentIds.Count; index++)
        {
            var typeArgument = functionRef.TypeArgumentIds[index];
            if (!typeArgument.IsValid ||
                !TryCollectTypeBindings(templateTypeParameters[index], typeArgument, bindings))
            {
                return false;
            }
        }

        return true;
    }

    private List<TypeId> GetTemplateTypeParameterIds(MirFunc template)
    {
        return template.GenericTypeParameterIds.Count > 0
            ? template.GenericTypeParameterIds
                .Where(static typeId => typeId.IsValid)
                .ToList()
            : CollectTemplateTypeParameterIds(template);
    }

    private List<TypeId> CollectTemplateTypeParameterIds(MirFunc template)
    {
        var result = new List<TypeId>();
        var seen = new HashSet<int>();
        var visited = new HashSet<int>();

        foreach (var parameter in GetCachedTemplateParameters(template))
        {
            CollectTemplateTypeParameterIds(parameter.TypeId, result, seen, visited);
        }

        CollectTemplateTypeParameterIds(template.ReturnType, result, seen, visited);
        return result;
    }

    private void CollectTemplateTypeParameterIds(
        TypeId typeId,
        List<TypeId> result,
        HashSet<int> seen,
        HashSet<int> visited)
    {
        if (!typeId.IsValid || !visited.Add(typeId.Value))
        {
            return;
        }

        if (!TryGetTypeDescriptor(typeId, out var descriptor))
        {
            if (IsMirGenericTypeParameter(typeId) && seen.Add(typeId.Value))
            {
                result.Add(typeId);
            }

            return;
        }

        switch (descriptor)
        {
            case TypeDescriptor.TypeVar:
                if (seen.Add(typeId.Value))
                {
                    result.Add(typeId);
                }
                break;
            case TypeDescriptor.Function function:
                foreach (var parameterType in function.ParamTypes)
                {
                    CollectTemplateTypeParameterIds(parameterType, result, seen, visited);
                }
                CollectTemplateTypeParameterIds(function.ReturnType, result, seen, visited);
                break;
            case TypeDescriptor.Tuple tuple:
                foreach (var fieldType in tuple.FieldTypes)
                {
                    CollectTemplateTypeParameterIds(fieldType, result, seen, visited);
                }
                break;
            case TypeDescriptor.TyCon tyCon:
                foreach (var typeArgument in tyCon.TypeArgs)
                {
                    CollectTemplateTypeParameterIds(typeArgument, result, seen, visited);
                }
                break;
            case TypeDescriptor.Ref reference:
                CollectTemplateTypeParameterIds(reference.Inner, result, seen, visited);
                break;
            case TypeDescriptor.MutRef reference:
                CollectTemplateTypeParameterIds(reference.Inner, result, seen, visited);
                break;
        }
    }

    private bool HasMeaningfulSpecializationBindings(MirFunc template, SpecializationSignature signature)
    {
        if (template.GenericParameterCount <= 0)
        {
            return true;
        }

        return CollectTypeBindings(template, signature).Count > 0;
    }

    private bool HasMeaningfulSpecializationBindings(TemplateInfo template, SpecializationSignature signature)
    {
        if (template.TemplateSource.GenericParameterCount <= 0)
        {
            return true;
        }

        return CollectTypeBindings(template, signature).Count > 0;
    }

    private bool HasMeaningfulSpecializationSignature(MirFunc template, SpecializationSignature signature)
    {
        if (signature.GenericValueArguments is { Count: > 0 })
        {
            return true;
        }

        if (HasMeaningfulSpecializationBindings(template, signature))
        {
            return true;
        }

        if (template.GenericParameterCount > 0 && IsMonomorphicSignature(signature))
        {
            return true;
        }

        var templateParameters = GetCachedTemplateParameters(template);
        if (templateParameters.Count != signature.ParameterTypes.Count)
        {
            return true;
        }

        return template.ReturnType != signature.ReturnType ||
               templateParameters
                   .Zip(signature.ParameterTypes)
                   .Any(static pair => pair.First.TypeId != pair.Second);
    }

    private bool HasMeaningfulSpecializationSignature(TemplateInfo template, SpecializationSignature signature)
    {
        var key = CreateSpecializationCacheKey(template, signature);
        if (_meaningfulSignatureByTemplateAndSignature.TryGetValue(key, out var cached))
        {
            _stats.MeaningfulSignatureCacheHits++;
            return cached;
        }

        _stats.MeaningfulSignatureCacheMisses++;
        var templateSource = template.TemplateSource;
        var meaningful = signature.GenericValueArguments is { Count: > 0 } ||
                         HasMeaningfulSpecializationBindings(template, signature);
        if (!meaningful && templateSource.GenericParameterCount > 0 && IsMonomorphicSignature(signature))
        {
            meaningful = true;
        }

        if (!meaningful)
        {
            var templateParameters = GetCachedTemplateParameters(templateSource);
            meaningful = templateParameters.Count != signature.ParameterTypes.Count ||
                         templateSource.ReturnType != signature.ReturnType ||
                         HasDifferentParameterTypes(templateParameters, signature.ParameterTypes);
        }

        _meaningfulSignatureByTemplateAndSignature[key] = meaningful;
        return meaningful;
    }

    private static bool HasDifferentParameterTypes(
        IReadOnlyList<MirLocal> templateParameters,
        IReadOnlyList<TypeId> signatureParameterTypes)
    {
        for (var i = 0; i < templateParameters.Count; i++)
        {
            if (templateParameters[i].TypeId != signatureParameterTypes[i])
            {
                return true;
            }
        }

        return false;
    }

    private static List<TypeId> CopyTypeIdSuffix(IReadOnlyList<TypeId> typeIds, int startIndex)
    {
        var result = new List<TypeId>(Math.Max(0, typeIds.Count - startIndex));
        for (var i = startIndex; i < typeIds.Count; i++)
        {
            result.Add(typeIds[i]);
        }

        return result;
    }

    private bool TryDefaultErasedOpenTypeVariables(
        MirFunc template,
        SpecializationSignature signature,
        out SpecializationSignature defaultedSignature)
    {
        defaultedSignature = signature;
        if (IsMonomorphicSignature(signature) ||
            SignatureContainsOpenConstructorBinding(signature) ||
            !CanDefaultErasedOpenTypeVariables(signature.ReturnType) ||
            signature.ParameterTypes.Any(typeId => !CanDefaultErasedOpenTypeVariables(typeId)))
        {
            return false;
        }

        var openTypeVariables = new HashSet<int>();
        CollectOpenTypeVariables(signature.ReturnType, openTypeVariables);
        foreach (var parameterType in signature.ParameterTypes)
        {
            CollectOpenTypeVariables(parameterType, openTypeVariables);
        }

        if (openTypeVariables.Count == 0)
        {
            return false;
        }

        var templateBindings = CollectTypeBindings(template, signature);
        if (!CanDefaultErasedOpenTypeVariablesInTemplateBody(template, templateBindings))
        {
            return false;
        }

        var bindings = CloneBindings(templateBindings);
        foreach (var typeVariable in openTypeVariables)
        {
            var defaultType = new TypeId(BaseTypes.RawPtrId);
            bindings.TypeBindings.TryAdd(typeVariable, defaultType);
            if (TryGetTypeDescriptor(new TypeId(typeVariable), out var descriptor) &&
                descriptor is TypeDescriptor.TypeVar typeVariableDescriptor)
            {
                bindings.TypeBindings.TryAdd(GetTypeVariableIndexBindingKey(typeVariableDescriptor.Index), defaultType);
            }
        }

        var returnType = SubstituteTypeId(signature.ReturnType, bindings);
        var parameterTypes = signature.ParameterTypes
            .Select(parameterType => SubstituteTypeId(parameterType, bindings))
            .ToList();
        var candidate = new SpecializationSignature(
            returnType,
            parameterTypes,
            signature.GenericValueArguments);
        if (!IsMonomorphicSignature(candidate))
        {
            return false;
        }

        defaultedSignature = candidate;
        return true;
    }

    private static IReadOnlyList<GenericValueArgumentDescriptor> GetCallValueArguments(MirCall call)
    {
        return call.Function is MirFunctionRef functionRef
            ? functionRef.ValueArguments
            : [];
    }

    private bool CanDefaultErasedOpenTypeVariablesInTemplateBody(
        MirFunc template,
        SpecializationBindings bindings)
    {
        foreach (var local in template.Locals)
        {
            var localType = SubstituteTypeId(local.TypeId, bindings);
            if (!CanDefaultErasedOpenTypeVariables(localType))
            {
                return false;
            }
        }

        return true;
    }

    private bool CanDefaultErasedOpenTypeVariables(TypeId typeId)
    {
        return CanDefaultErasedOpenTypeVariables(typeId, openVariablesAreErased: false, []);
    }

    private bool CanDefaultErasedOpenTypeVariables(
        TypeId typeId,
        bool openVariablesAreErased,
        HashSet<int> visited)
    {
        if (!typeId.IsValid || !visited.Add(typeId.Value))
        {
            return true;
        }

        if (!TryGetTypeDescriptor(typeId, out var descriptor))
        {
            return !IsMirGenericTypeParameter(typeId) || openVariablesAreErased;
        }

        return descriptor switch
        {
            TypeDescriptor.TypeVar => openVariablesAreErased,
            TypeDescriptor.Builtin => true,
            TypeDescriptor.Function => true,
            TypeDescriptor.TyCon tyCon when TryParseConstructorVarIndex(tyCon.Constructor, out _) => false,
            TypeDescriptor.TyCon tyCon => tyCon.TypeArgs.All(argument =>
                CanDefaultErasedOpenTypeVariables(argument, openVariablesAreErased: true, visited)),
            TypeDescriptor.Ref reference => CanDefaultErasedOpenTypeVariables(
                reference.Inner,
                openVariablesAreErased: true,
                visited),
            TypeDescriptor.MutRef reference => CanDefaultErasedOpenTypeVariables(
                reference.Inner,
                openVariablesAreErased: true,
                visited),
            TypeDescriptor.Tuple tuple => tuple.FieldTypes.All(field =>
                CanDefaultErasedOpenTypeVariables(field, openVariablesAreErased, visited)),
            _ => true
        };
    }

    private bool TryResolveFunctionSignatureTypeId(MirFunc function, out TypeId functionTypeId)
    {
        functionTypeId = TypeId.None;

        var parameterTypes = function.Locals
            .Where(local => local.IsParameter)
            .Select(local => local.TypeId)
            .ToList();
        if (parameterTypes.Any(typeId => !typeId.IsValid) || !function.ReturnType.IsValid)
        {
            return false;
        }

        var exactDescriptor = new TypeDescriptor.Function([.. parameterTypes], function.ReturnType);
        if (TryGetInternedDynamicTypeId(exactDescriptor, out functionTypeId))
        {
            return true;
        }

        foreach (var (descriptor, dynamicTypeId) in _dynamicTypes.IdByDescriptorDict)
        {
            if (descriptor is not TypeDescriptor.Function functionDescriptor ||
                functionDescriptor.ReturnType != function.ReturnType ||
                functionDescriptor.ParamTypes.Length != parameterTypes.Count)
            {
                continue;
            }

            var parametersMatch = true;
            for (var index = 0; index < functionDescriptor.ParamTypes.Length; index++)
            {
                if (functionDescriptor.ParamTypes[index] != parameterTypes[index])
                {
                    parametersMatch = false;
                    break;
                }
            }

            if (!parametersMatch)
            {
                continue;
            }

            functionTypeId = dynamicTypeId;
            return true;
        }

        functionTypeId = GetOrCreateDynamicTypeId(exactDescriptor);
        return true;
    }
}
