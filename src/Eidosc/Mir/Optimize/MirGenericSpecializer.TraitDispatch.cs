using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private readonly record struct TraitDispatchMethodSignature(
        string Name,
        IReadOnlyList<TypeId> ParamTypes,
        TypeId ReturnType);

    private readonly record struct TraitDispatchLookupKey(
        SymbolId OwnerTrait,
        string TraitMethodName,
        TypeId ReceiverTypeId,
        SymbolId TraitMethodId);

    private readonly record struct TraitDispatchLookupResult(
        bool Found,
        SymbolId ImplMethodId,
        string ImplMethodName);

    private IEnumerable<ImplSymbol> EnumerateTraitImpls()
    {
        return _moduleTraitImpls.Where(static impl => impl.HasRuntimeMethods);
    }

    private IEnumerable<ImplSymbol> EnumerateTraitImpls(SymbolId ownerTrait)
    {
        if (!ownerTrait.IsValid)
        {
            return EnumerateTraitImpls();
        }

        return EnumerateTraitImpls().Where(impl => impl.Trait == ownerTrait);
    }

    private bool TryRewriteTraitMethodCall(
        MirFunc containingFunction,
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out MirCall rewrittenCall)
    {
        return new TraitDispatchResolverService(this).TryRewriteTraitMethodCall(
            containingFunction,
            call,
            localTypes,
            out rewrittenCall);
    }

    private List<TypeId> EnumerateTraitDispatchTypeIds(
        MirFunc containingFunction,
        MirFunctionRef functionRef,
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        var result = new List<TypeId>(4);
        var includeFirstArgument = ShouldUseFirstArgumentForTraitDispatch(functionRef);
        var includeTargetType = ShouldUseTargetTypeForTraitDispatch(functionRef);
        var parameterDispatchIndices = GetTraitDispatchParameterIndices(functionRef, includeFirstArgument);
        var hasConcreteDispatchType = false;
        var hasConcreteTargetCarrierType = false;

        // 1. 参数中的 Self 类型 - method metadata can point to any parameter, not only receiver position.
        foreach (var parameterIndex in parameterDispatchIndices)
        {
            if (parameterIndex < 0 || parameterIndex >= call.Arguments.Count)
            {
                continue;
            }

            var receiverTypeId = ResolveOperandType(call.Arguments[parameterIndex], localTypes);
            if (IsConcreteTraitDispatchType(receiverTypeId))
            {
                hasConcreteDispatchType = true;
                AddUniqueTypeId(result, receiverTypeId);
            }
        }

        foreach (var inferredDispatchTypeId in EnumerateTraitDispatchTypeIdsFromFunctionSignature(
                     functionRef,
                     call,
                     localTypes,
                     includeFirstArgument,
                     includeTargetType))
        {
            if (IsConcreteTraitDispatchType(inferredDispatchTypeId))
            {
                hasConcreteDispatchType = true;
                AddUniqueTypeId(result, inferredDispatchTypeId);
            }
        }

        // 2. 返回值/目标类型 — 适用于高阶 trait（如 Applicative[F], Functor[F]），
        //    其中实现类型构造器 F 只出现在返回类型 F[A] 中
        if (includeTargetType && call.Target != null)
        {
            var targetTypeId = ResolvePlaceType(call.Target, localTypes);
            var targetCarrierTypeId = ResolveTraitDispatchCarrierType(targetTypeId);
            if (IsConcreteTraitDispatchType(targetCarrierTypeId))
            {
                hasConcreteDispatchType = true;
                hasConcreteTargetCarrierType = true;
                AddUniqueTypeId(result, targetCarrierTypeId);
            }
        }

        // 3. 当返回值类型包含开放变量时，尝试通过匹配函数类型参数推断返回类型，
        //    再从推断后的返回类型中提取具体的 trait dispatch 类型
        if (includeTargetType &&
            !hasConcreteTargetCarrierType &&
            call.Target != null &&
            TryResolveFunctionRefType(functionRef, out var functionTypeId) &&
            TryResolveFlattenedFunctionType(functionTypeId, out var declaredParamTypes, out var declaredResultType) &&
            ContainsOpenTypeVariable(declaredResultType))
        {
            var inferenceBindings = new SpecializationBindings(
                new Dictionary<int, TypeId>(),
                new Dictionary<int, ConstructorBinding>());

            for (var i = 0; i < call.Arguments.Count && i < declaredParamTypes.Count; i++)
            {
                var concreteArgType = ResolveOperandType(call.Arguments[i], localTypes);
                if (concreteArgType.IsValid &&
                    !TryCollectTypeBindingsForInference(declaredParamTypes[i], concreteArgType, inferenceBindings))
                {
                    return result;
                }
            }

            // Also match target type against declared result type to capture constructor variables
            var targetType = ResolvePlaceType(call.Target, localTypes);
            if (targetType.IsValid &&
                !TryCollectTypeBindingsForInference(declaredResultType, targetType, inferenceBindings))
            {
                return result;
            }

            var resolvedResult = SubstituteTypeId(declaredResultType, inferenceBindings);
            var inferredCarrierTypeId = ResolveTraitDispatchCarrierType(resolvedResult);
            if (inferredCarrierTypeId.IsValid && IsConcreteTraitDispatchType(inferredCarrierTypeId))
            {
                hasConcreteDispatchType = true;
                hasConcreteTargetCarrierType = true;
                AddUniqueTypeId(result, inferredCarrierTypeId);
            }
        }

        // 4. 对于 `pure` / `apply` 这类 carrier 只在外层返回值中稳定可见的 helper，
        //    直接回退到包含函数的返回容器形状。
        if (includeTargetType && !hasConcreteDispatchType && !hasConcreteTargetCarrierType)
        {
            var containingReturnCarrierTypeId = ResolveTraitDispatchCarrierType(containingFunction.ReturnType);
            if (IsConcreteTraitDispatchType(containingReturnCarrierTypeId))
            {
                AddUniqueTypeId(result, containingReturnCarrierTypeId);
            }
        }

        return result;
    }

    private static void AddUniqueTypeId(List<TypeId> typeIds, TypeId typeId)
    {
        for (var i = 0; i < typeIds.Count; i++)
        {
            if (typeIds[i] == typeId)
            {
                return;
            }
        }

        typeIds.Add(typeId);
    }

    private List<TypeId> CollectMostSpecificTraitDispatchTypeIds(
        MirFunc containingFunction,
        MirFunctionRef functionRef,
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        var allCandidateDispatchTypeIds = EnumerateTraitDispatchTypeIds(
            containingFunction,
            functionRef,
            call,
            localTypes);
        if (allCandidateDispatchTypeIds.Count <= 1)
        {
            return allCandidateDispatchTypeIds;
        }

        List<TypeId>? filtered = null;
        for (var i = 0; i < allCandidateDispatchTypeIds.Count; i++)
        {
            var typeId = allCandidateDispatchTypeIds[i];
            if (!IsMostSpecificTraitDispatchType(typeId, allCandidateDispatchTypeIds))
            {
                continue;
            }

            filtered ??= new List<TypeId>(allCandidateDispatchTypeIds.Count);
            filtered.Add(typeId);
        }

        return filtered ?? [];
    }

    private TypeId ResolveTraitDispatchCarrierType(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return TypeId.None;
        }

        if (TryResolveFlattenedFunctionType(typeId, out _, out var resultType) &&
            resultType.IsValid &&
            resultType != typeId)
        {
            return resultType;
        }

        return typeId;
    }

    private bool IsConcreteTraitDispatchType(TypeId typeId)
    {
        return typeId.IsValid && IsConcreteTraitDispatchType(typeId, []);
    }

    private bool IsConcreteTraitDispatchType(TypeId typeId, HashSet<int> visitedTypeIds)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (!visitedTypeIds.Add(typeId.Value))
        {
            return true;
        }

        if (!TryGetTypeDescriptor(typeId, out var descriptor))
        {
            return BaseTypes.IsBuiltIn(typeId) ||
                   !IsMirGenericTypeParameter(typeId);
        }

        switch (descriptor)
        {
            case TypeDescriptor.TypeVar:
                return false;
            case TypeDescriptor.Function function:
                return function.ParamTypes.All(parameterType => IsConcreteTraitDispatchType(parameterType, visitedTypeIds)) &&
                       IsConcreteTraitDispatchType(function.ReturnType, visitedTypeIds);
            case TypeDescriptor.Tuple tuple:
                return tuple.FieldTypes.All(elementType => IsConcreteTraitDispatchType(elementType, visitedTypeIds));
            case TypeDescriptor.TyCon tyCon:
                // Higher-kinded trait dispatch only needs a concrete outer constructor
                // (for example Option[A] can resolve Applicative[Option] even when A is open).
                return !TryParseConstructorVarIndex(tyCon.Constructor, out _);
            default:
                return true;
        }
    }

    private bool TryResolveTraitDispatchTarget(
        MirFunc containingFunction,
        MirFunctionRef functionRef,
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out SymbolId implMethodId,
        out string implMethodName)
    {
        implMethodId = SymbolId.None;
        implMethodName = string.Empty;

        var candidateDispatchTypeIds = CollectMostSpecificTraitDispatchTypeIds(
            containingFunction,
            functionRef,
            call,
            localTypes);
        if (candidateDispatchTypeIds.Count == 0)
        {
            return TryResolveTraitDispatchTargetBySignature(
                containingFunction,
                functionRef,
                call,
                localTypes,
                out implMethodId,
                out implMethodName);
        }

        SymbolId resolvedMethodId = SymbolId.None;
        string? resolvedMethodName = null;

        foreach (var dispatchTypeId in candidateDispatchTypeIds)
        {
            if (!TryResolveTraitDispatchTarget(functionRef, dispatchTypeId, out var candidateMethodId, out var candidateMethodName) &&
                !TryResolveTraitInvokeHelperDispatchTarget(
                    containingFunction,
                    functionRef,
                    dispatchTypeId,
                    out candidateMethodId,
                    out candidateMethodName))
            {
                continue;
            }

            if (!resolvedMethodId.IsValid)
            {
                resolvedMethodId = candidateMethodId;
                resolvedMethodName = candidateMethodName;
                continue;
            }

            if (resolvedMethodId != candidateMethodId)
            {
                return false;
            }
        }

        if (!resolvedMethodId.IsValid || string.IsNullOrWhiteSpace(resolvedMethodName))
        {
            return TryResolveTraitDispatchTargetBySignature(
                containingFunction,
                functionRef,
                call,
                localTypes,
                out implMethodId,
                out implMethodName);
        }

        implMethodId = resolvedMethodId;
        implMethodName = resolvedMethodName;
        return true;
    }

    private bool TryRewriteResultCarrierTraitCallFromConsumer(
        MirBasicBlock block,
        int instructionIndex,
        MirCall call,
        Dictionary<LocalId, TypeId> localTypes,
        out MirCall rewrittenCall)
    {
        rewrittenCall = call;

        if (call.Function is not MirFunctionRef functionRef ||
            !IsPureFunctionName(functionRef.Name) ||
            call.Target is not { Kind: PlaceKind.Local } targetLocal)
        {
            return false;
        }

        for (var consumerIndex = instructionIndex + 1; consumerIndex < block.Instructions.Count; consumerIndex++)
        {
            if (block.Instructions[consumerIndex] is not MirCall consumerCall ||
                consumerCall.Function is not MirFunctionRef consumerRef ||
                !consumerRef.Name.Contains("apply", StringComparison.Ordinal))
            {
                continue;
            }

            var targetArgumentIndex = -1;
            for (var argumentIndex = 0; argumentIndex < consumerCall.Arguments.Count; argumentIndex++)
            {
                if (consumerCall.Arguments[argumentIndex] is MirPlace { Kind: PlaceKind.Local } argumentPlace &&
                    (argumentPlace.Local == targetLocal.Local ||
                     IsForwardedLocalAlias(block, instructionIndex, consumerIndex, argumentPlace.Local, targetLocal.Local)))
                {
                    targetArgumentIndex = argumentIndex;
                    break;
                }
            }

            if (targetArgumentIndex < 0)
            {
                continue;
            }

            RefineProducerTargetTypeFromConsumer(
                block,
                instructionIndex,
                consumerCall,
                targetArgumentIndex,
                targetLocal.Local,
                localTypes);

            if (TryResolveSiblingImplMethodFromConsumer(
                    functionRef,
                    consumerRef,
                    out var siblingImplMethodId,
                    out var siblingImplMethodName))
            {
                var signatureTypeId = TryResolveImplMethodSignatureTypeId(siblingImplMethodId, out var implMethodSignatureTypeId)
                    ? implMethodSignatureTypeId
                    : TryResolveConcreteFunctionRefType(functionRef, out var concreteFunctionTypeId)
                        ? concreteFunctionTypeId
                        : TypeId.None;

                rewrittenCall = call with
                {
                    Function = RewriteFunctionReference(
                        functionRef,
                        siblingImplMethodId,
                        siblingImplMethodName,
                        functionRef.TypeId,
                        signatureTypeId)
                };
                return true;
            }

            for (var argumentIndex = 0; argumentIndex < consumerCall.Arguments.Count; argumentIndex++)
            {
                if (argumentIndex == targetArgumentIndex)
                {
                    continue;
                }

                var carrierTypeId = ResolveTraitDispatchCarrierType(
                    ResolveOperandType(consumerCall.Arguments[argumentIndex], localTypes));
                if (!IsConcreteTraitDispatchType(carrierTypeId) ||
                    !TryResolveTraitDispatchTarget(functionRef, carrierTypeId, out var implMethodId, out var implMethodName))
                {
                    continue;
                }

                var signatureTypeId = TryResolveConcreteFunctionRefType(functionRef, out var concreteFunctionTypeId)
                    ? concreteFunctionTypeId
                    : TryResolveImplMethodSignatureTypeId(implMethodId, out var implMethodSignatureTypeId)
                        ? implMethodSignatureTypeId
                        : TypeId.None;

                rewrittenCall = call with
                {
                    Function = RewriteFunctionReference(
                        functionRef,
                        implMethodId,
                        implMethodName,
                        functionRef.TypeId,
                        signatureTypeId)
                };
                return true;
            }
        }

        return false;
    }

    private static bool IsPureFunctionName(string name)
    {
        return string.Equals(name, "pure", StringComparison.Ordinal) ||
               name.Contains("__pure", StringComparison.Ordinal) ||
               name.EndsWith("_pure", StringComparison.Ordinal);
    }

    private void RefineProducerTargetTypeFromConsumer(
        MirBasicBlock block,
        int producerIndex,
        MirCall consumerCall,
        int consumerArgumentIndex,
        LocalId producerLocal,
        Dictionary<LocalId, TypeId> localTypes)
    {
        if (!TryInferCallArgumentTypeFromSignature(
                consumerCall,
                consumerArgumentIndex,
                localTypes,
                out var expectedArgumentType) ||
            !expectedArgumentType.IsValid ||
            ContainsOpenTypeVariable(expectedArgumentType))
        {
            return;
        }

        localTypes[producerLocal] = expectedArgumentType;

        if (consumerCall.Arguments[consumerArgumentIndex] is MirPlace { Kind: PlaceKind.Local } consumerArgumentPlace)
        {
            localTypes[consumerArgumentPlace.Local] = expectedArgumentType;
            RefineForwardedAliasChain(block, producerIndex, consumerArgumentPlace.Local, producerLocal, expectedArgumentType, localTypes);
        }
    }

    private static void RefineForwardedAliasChain(
        MirBasicBlock block,
        int producerIndex,
        LocalId aliasLocal,
        LocalId sourceLocal,
        TypeId expectedType,
        Dictionary<LocalId, TypeId> localTypes)
    {
        var current = aliasLocal;
        for (var index = block.Instructions.Count - 1; index > producerIndex; index--)
        {
            switch (block.Instructions[index])
            {
                case MirCopy
                {
                    Target: { Kind: PlaceKind.Local } target,
                    Source: { Kind: PlaceKind.Local } source
                } when target.Local == current:
                    localTypes[target.Local] = expectedType;
                    current = source.Local;
                    localTypes[current] = expectedType;
                    if (current == sourceLocal)
                    {
                        return;
                    }
                    break;

                case MirMove
                {
                    Target: { Kind: PlaceKind.Local } target,
                    Source: { Kind: PlaceKind.Local } source
                } when target.Local == current:
                    localTypes[target.Local] = expectedType;
                    current = source.Local;
                    localTypes[current] = expectedType;
                    if (current == sourceLocal)
                    {
                        return;
                    }
                    break;
            }
        }
    }

    private static bool IsForwardedLocalAlias(
        MirBasicBlock block,
        int producerIndex,
        int consumerIndex,
        LocalId candidateLocal,
        LocalId sourceLocal)
    {
        var current = candidateLocal;
        for (var index = consumerIndex - 1; index > producerIndex; index--)
        {
            switch (block.Instructions[index])
            {
                case MirCopy
                {
                    Target: { Kind: PlaceKind.Local } target,
                    Source: { Kind: PlaceKind.Local } source
                } when target.Local == current:
                    current = source.Local;
                    if (current == sourceLocal)
                    {
                        return true;
                    }
                    break;

                case MirMove
                {
                    Target: { Kind: PlaceKind.Local } target,
                    Source: { Kind: PlaceKind.Local } source
                } when target.Local == current:
                    current = source.Local;
                    if (current == sourceLocal)
                    {
                        return true;
                    }
                    break;
            }
        }

        return false;
    }

    private bool TryResolveSiblingImplMethodFromConsumer(
        MirFunctionRef traitMethodRef,
        MirFunctionRef consumerRef,
        out SymbolId implMethodId,
        out string implMethodName)
    {
        implMethodId = SymbolId.None;
        implMethodName = string.Empty;

        if (!traitMethodRef.SymbolId.IsValid || !consumerRef.SymbolId.IsValid)
        {
            return false;
        }

        foreach (var impl in _moduleTraitImpls)
        {
            if (!impl.HasRuntimeMethods ||
                (traitMethodRef.TraitOwnerId.IsValid && impl.Trait != traitMethodRef.TraitOwnerId) ||
                !impl.Methods.Contains(consumerRef.SymbolId))
            {
                continue;
            }

            if (impl.TraitMethodImplementations.TryGetValue(traitMethodRef.SymbolId, out var mappedMethodId) &&
                TryResolveImplMethodName(mappedMethodId, out var mappedMethodName))
            {
                implMethodId = mappedMethodId;
                implMethodName = mappedMethodName;
                return true;
            }

            foreach (var methodId in impl.Methods)
            {
                if (TryResolveImplMethodName(methodId, out var methodName) &&
                    methodName.EndsWith("__" + traitMethodRef.Name, StringComparison.Ordinal))
                {
                    implMethodId = methodId;
                    implMethodName = methodName;
                    return true;
                }
            }
        }

        return false;
    }

    private IEnumerable<TypeId> EnumerateTraitDispatchTypeIdsFromFunctionSignature(
        MirFunctionRef functionRef,
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        bool includeParameterTypes,
        bool includeResultType)
    {
        if (!TryResolveFunctionRefType(functionRef, out var functionTypeId) ||
            !TryResolveFlattenedFunctionType(functionTypeId, out var declaredParameterTypes, out var declaredResultType) ||
            declaredParameterTypes.Count == 0)
        {
            yield break;
        }

        var inferenceBindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());

        for (var index = 0; index < call.Arguments.Count && index < declaredParameterTypes.Count; index++)
        {
            var concreteArgumentType = ResolveOperandType(call.Arguments[index], localTypes);
            if (!ShouldUseArgumentTypeForTemplateInference(call.Arguments[index], concreteArgumentType))
            {
                continue;
            }

            if (!TryCollectTypeBindingsForInference(declaredParameterTypes[index], concreteArgumentType, inferenceBindings))
            {
                yield break;
            }
        }

        if (call.Arguments.Count == declaredParameterTypes.Count + 1 &&
            declaredResultType.IsValid)
        {
            var continuationType = ResolveOperandType(call.Arguments[^1], localTypes);
            if (TryResolveFlattenedFunctionType(
                    continuationType,
                    out var continuationParameterTypes,
                    out _) &&
                continuationParameterTypes.Count > 0)
            {
                var continuationValueType = continuationParameterTypes[0];
                if (continuationValueType.IsValid &&
                    !TryCollectTypeBindingsForInference(declaredResultType, continuationValueType, inferenceBindings))
                {
                    yield break;
                }
            }
        }

        if (call.Target != null)
        {
            var targetType = ResolvePlaceType(call.Target, localTypes);
            if (call.Arguments.Count == declaredParameterTypes.Count)
            {
                if (targetType.IsValid && declaredResultType.IsValid)
                {
                    if (!TryCollectTypeBindingsForInference(declaredResultType, targetType, inferenceBindings))
                    {
                        yield break;
                    }
                }
            }
            else if (call.Target is { Kind: PlaceKind.Local } &&
                     targetType.IsValid &&
                     TryResolveFlattenedFunctionType(targetType, out var remainingParameterTypes, out var remainingResultType) &&
                     call.Arguments.Count + remainingParameterTypes.Count == declaredParameterTypes.Count)
            {
                for (var parameterIndex = call.Arguments.Count; parameterIndex < declaredParameterTypes.Count; parameterIndex++)
                {
                    var remainingIndex = parameterIndex - call.Arguments.Count;
                    if (remainingIndex >= remainingParameterTypes.Count)
                    {
                        break;
                    }

                    if (!TryCollectTypeBindingsForInference(
                        declaredParameterTypes[parameterIndex],
                        remainingParameterTypes[remainingIndex],
                        inferenceBindings))
                    {
                        yield break;
                    }
                }

                if (declaredResultType.IsValid && remainingResultType.IsValid)
                {
                    if (!TryCollectTypeBindingsForInference(declaredResultType, remainingResultType, inferenceBindings))
                    {
                        yield break;
                    }
                }
            }
        }

        if (includeParameterTypes)
        {
            for (var index = 0; index < declaredParameterTypes.Count; index++)
            {
                if (!ShouldUseParameterPositionForTraitDispatch(functionRef, index))
                {
                    continue;
                }

                var substitutedType = SubstituteTypeId(declaredParameterTypes[index], inferenceBindings);
                var dispatchTypeId = ResolveTraitDispatchCarrierType(substitutedType);
                if (dispatchTypeId.IsValid)
                {
                    yield return dispatchTypeId;
                }
            }
        }

        if (includeResultType)
        {
            var substitutedResultType = SubstituteTypeId(declaredResultType, inferenceBindings);
            var resultDispatchTypeId = ResolveTraitDispatchCarrierType(substitutedResultType);
            if (resultDispatchTypeId.IsValid)
            {
                yield return resultDispatchTypeId;
            }
        }
    }

    private bool ShouldUseFirstArgumentForTraitDispatch(MirFunctionRef functionRef)
    {
        if (TryGetTraitSelfPosition(functionRef, out var selfPosition))
        {
            return selfPosition switch
            {
                SelfPosition.InParameter => true,
                SelfPosition.InResult => false,
                SelfPosition.Both => true,
                SelfPosition.Unknown => false,
                _ => true
            };
        }

        return false;
    }

    private bool ShouldUseTargetTypeForTraitDispatch(MirFunctionRef functionRef)
    {
        if (string.Equals(functionRef.Name, "pure", StringComparison.Ordinal))
        {
            return true;
        }

        if (TryGetTraitSelfParameterIndices(functionRef, out var selfParameterIndices) &&
            selfParameterIndices.Count > 0)
        {
            return false;
        }

        if (TryGetTraitSelfPosition(functionRef, out var selfPosition))
        {
            return selfPosition switch
            {
                SelfPosition.InParameter => false,
                SelfPosition.InResult => true,
                SelfPosition.Both => true,
                SelfPosition.Unknown => false,
                _ => true
            };
        }

        return false;
    }

    private bool ShouldUseParameterPositionForTraitDispatch(MirFunctionRef functionRef, int parameterIndex)
    {
        if (parameterIndex < 0)
        {
            return false;
        }

        if (TryGetTraitSelfParameterIndices(functionRef, out var selfParameterIndices))
        {
            return selfParameterIndices.Contains(parameterIndex);
        }

        if (TryGetTraitSelfPosition(functionRef, out var selfPosition))
        {
            return selfPosition switch
            {
                SelfPosition.InParameter or SelfPosition.Both => parameterIndex == 0,
                SelfPosition.Unknown => false,
                _ => false
            };
        }

        return false;
    }

    private IReadOnlyList<int> GetTraitDispatchParameterIndices(MirFunctionRef functionRef, bool includeFirstArgument)
    {
        if (TryGetTraitSelfParameterIndices(functionRef, out var selfParameterIndices))
        {
            return selfParameterIndices;
        }

        return includeFirstArgument ? [0] : [];
    }

    private bool TryGetTraitSelfPosition(MirFunctionRef functionRef, out SelfPosition selfPosition)
    {
        selfPosition = SelfPosition.Unknown;

        if (functionRef.TraitSelfPosition != SelfPosition.Unknown)
        {
            selfPosition = functionRef.TraitSelfPosition;
            return true;
        }

        if (functionRef.TraitSelfInResult)
        {
            selfPosition = functionRef.TraitSelfParameterIndices.Count > 0
                ? SelfPosition.Both
                : SelfPosition.InResult;
            return true;
        }

        if (functionRef.TraitSelfParameterIndices.Count > 0)
        {
            selfPosition = SelfPosition.InParameter;
            return true;
        }

        if (TryGetTraitInfoSelfPosition(functionRef.TraitOwnerId, out selfPosition))
        {
            return true;
        }

        if (TryGetTraitMethodInfo(functionRef, out var methodInfo))
        {
            if (methodInfo.SelfPosition != SelfPosition.Unknown)
            {
                selfPosition = methodInfo.SelfPosition;
                return true;
            }

            if (TryGetTraitInfoSelfPosition(methodInfo.TraitId, out selfPosition))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetTraitInfoSelfPosition(SymbolId ownerTrait, out SelfPosition selfPosition)
    {
        selfPosition = SelfPosition.Unknown;
        if (!ownerTrait.IsValid ||
            !_traitInfoById.TryGetValue(ownerTrait, out var traitInfo) ||
            traitInfo.SelfPosition == SelfPosition.Unknown)
        {
            return false;
        }

        selfPosition = traitInfo.SelfPosition;
        return true;
    }

    private bool TryGetTraitSelfParameterIndices(
        MirFunctionRef functionRef,
        out IReadOnlyList<int> parameterIndices)
    {
        parameterIndices = [];

        if (functionRef.TraitSelfParameterIndices.Count > 0)
        {
            parameterIndices = functionRef.TraitSelfParameterIndices;
            return true;
        }

        if (TryGetTraitMethodInfo(functionRef, out var methodInfo) &&
            methodInfo.SelfParameterIndices.Count > 0)
        {
            parameterIndices = methodInfo.SelfParameterIndices;
            return true;
        }

        return false;
    }

    private bool TryGetTraitMethodInfo(MirFunctionRef functionRef, out MirTraitMethodInfo methodInfo)
    {
        methodInfo = null!;
        return functionRef.SymbolId.IsValid &&
               _traitMethodInfoById.TryGetValue(functionRef.SymbolId, out methodInfo!);
    }

    private bool TryResolveTraitDispatchTargetBySignature(
        MirFunc containingFunction,
        MirFunctionRef functionRef,
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out SymbolId implMethodId,
        out string implMethodName)
    {
        implMethodId = SymbolId.None;
        implMethodName = string.Empty;

        SymbolId resolvedMethodId = SymbolId.None;
        string? resolvedMethodName = null;
        var candidateDispatchTypeIds = CollectMostSpecificTraitDispatchTypeIds(
            containingFunction,
            functionRef,
            call,
            localTypes);

        var helperTraitId = containingFunction.TraitInvokeHelper == TraitInvokeHelperKind.None
            ? SymbolId.None
            : containingFunction.TraitInvokeHelperTraitId;
        foreach (var (ownerTrait, traitMethodName, traitMethodId) in EnumerateTraitDispatchCandidates(
                     containingFunction,
                     functionRef))
        {
            if (helperTraitId.IsValid && ownerTrait != helperTraitId)
            {
                continue;
            }

            var applicableImplIds = new HashSet<SymbolId>();
            foreach (var dispatchTypeId in candidateDispatchTypeIds)
            {
                foreach (var applicableImpl in ResolveApplicableImplsForReceiverType(ownerTrait, dispatchTypeId))
                {
                    applicableImplIds.Add(applicableImpl.Id);
                }
            }

            foreach (var impl in EnumerateTraitImpls(ownerTrait))
            {
                if (impl.Trait != ownerTrait)
                {
                    continue;
                }

                if (candidateDispatchTypeIds.Count > 0 &&
                    applicableImplIds.Count == 0 &&
                    containingFunction.TraitInvokeHelper == TraitInvokeHelperKind.None)
                {
                    continue;
                }

                if (applicableImplIds.Count > 0 && !applicableImplIds.Contains(impl.Id))
                {
                    continue;
                }

                foreach (var methodId in impl.Methods)
                {
                    if (!TraitMethodMatchesImplMethod(impl, traitMethodId, methodId, traitMethodName) ||
                        !TryResolveImplMethodSignature(methodId, out var candidateMethod) ||
                        !IsTraitDispatchCallCompatible(candidateMethod, call, localTypes))
                    {
                        continue;
                    }

                    if (!resolvedMethodId.IsValid)
                    {
                        resolvedMethodId = methodId;
                        resolvedMethodName = ResolveLoweredFunctionName(methodId, candidateMethod.Name);
                        continue;
                    }

                    if (resolvedMethodId != methodId)
                    {
                        return false;
                    }
                }
            }
        }

        if (!resolvedMethodId.IsValid || string.IsNullOrWhiteSpace(resolvedMethodName))
        {
            return false;
        }

        implMethodId = resolvedMethodId;
        implMethodName = resolvedMethodName;
        return true;
    }

    private IEnumerable<(SymbolId OwnerTrait, string TraitMethodName, SymbolId TraitMethodId)> EnumerateTraitDispatchCandidates(
        MirFunc containingFunction,
        MirFunctionRef functionRef)
    {
        if (functionRef.TraitOwnerId.IsValid &&
            !string.IsNullOrWhiteSpace(functionRef.Name))
        {
            yield return (functionRef.TraitOwnerId, functionRef.Name, functionRef.SymbolId);
        }

        if (containingFunction.TraitInvokeHelper != TraitInvokeHelperKind.None &&
            containingFunction.TraitInvokeHelperTraitId.IsValid &&
            !string.IsNullOrWhiteSpace(functionRef.Name) &&
            containingFunction.TraitInvokeHelperTraitId != functionRef.TraitOwnerId)
        {
            yield return (containingFunction.TraitInvokeHelperTraitId, functionRef.Name, functionRef.SymbolId);
        }

        if (TryGetTraitMethodInfo(functionRef, out var methodInfo) &&
            methodInfo.TraitId.IsValid &&
            !string.IsNullOrWhiteSpace(methodInfo.Name) &&
            methodInfo.TraitId != functionRef.TraitOwnerId &&
            methodInfo.TraitId != containingFunction.TraitInvokeHelperTraitId)
        {
            yield return (methodInfo.TraitId, methodInfo.Name, methodInfo.MethodId);
        }

        if (!functionRef.TraitOwnerId.IsValid &&
            containingFunction.TraitInvokeHelper == TraitInvokeHelperKind.None &&
            !string.IsNullOrWhiteSpace(functionRef.Name))
        {
            var emitted = new HashSet<SymbolId>();
            foreach (var candidate in _traitMethodInfoById.Values)
            {
                if (!candidate.TraitId.IsValid ||
                    !candidate.MethodId.IsValid ||
                    !emitted.Add(candidate.MethodId) ||
                    !string.Equals(candidate.Name, functionRef.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return (candidate.TraitId, candidate.Name, candidate.MethodId);
            }
        }
    }

    private bool IsTraitDispatchCallCompatible(
        TraitDispatchMethodSignature candidateMethod,
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        var argumentCount = call.Arguments.Count;
        if (candidateMethod.ParamTypes.Count < argumentCount)
        {
            return false;
        }

        var bindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());

        for (var i = 0; i < argumentCount; i++)
        {
            var concreteArgumentType = ResolveOperandType(call.Arguments[i], localTypes);
            if (!concreteArgumentType.IsValid)
            {
                return false;
            }

            if (!TryCollectTypeBindingsForInference(candidateMethod.ParamTypes[i], concreteArgumentType, bindings))
            {
                return false;
            }

            if (!IsTraitDispatchTypeCompatible(candidateMethod.ParamTypes[i], concreteArgumentType, bindings))
            {
                return false;
            }
        }

        if (argumentCount == candidateMethod.ParamTypes.Count)
        {
            if (call.Target != null)
            {
                var targetType = ResolvePlaceType(call.Target, localTypes);
                if (targetType.IsValid)
                {
                    if (!TryCollectTypeBindingsForInference(candidateMethod.ReturnType, targetType, bindings))
                    {
                        return false;
                    }

                    if (!IsTraitDispatchTypeCompatible(candidateMethod.ReturnType, targetType, bindings))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        if (call.Target is not { Kind: PlaceKind.Local } partialTarget)
        {
            return false;
        }

        var targetFunctionTypeId = ResolvePlaceType(partialTarget, localTypes);
        if (!TryResolveFlattenedFunctionType(
                targetFunctionTypeId,
                out var remainingParameterTypes,
                out var remainingResultType) ||
            argumentCount + remainingParameterTypes.Count != candidateMethod.ParamTypes.Count)
        {
            return false;
        }

        for (var parameterIndex = argumentCount; parameterIndex < candidateMethod.ParamTypes.Count; parameterIndex++)
        {
            var remainingIndex = parameterIndex - argumentCount;
            var declaredRemainingType = candidateMethod.ParamTypes[parameterIndex];
            var concreteRemainingType = remainingParameterTypes[remainingIndex];
            if (!concreteRemainingType.IsValid)
            {
                return false;
            }

            if (!TryCollectTypeBindingsForInference(declaredRemainingType, concreteRemainingType, bindings))
            {
                return false;
            }

            if (!IsTraitDispatchTypeCompatible(declaredRemainingType, concreteRemainingType, bindings))
            {
                return false;
            }
        }

        if (remainingResultType.IsValid)
        {
            if (!TryCollectTypeBindingsForInference(candidateMethod.ReturnType, remainingResultType, bindings))
            {
                return false;
            }

            if (!IsTraitDispatchTypeCompatible(candidateMethod.ReturnType, remainingResultType, bindings))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsTraitDispatchTypeCompatible(
        TypeId declaredType,
        TypeId concreteType,
        SpecializationBindings bindings)
    {
        if (!declaredType.IsValid || !concreteType.IsValid)
        {
            return false;
        }

        var resolvedDeclaredType = SubstituteTypeId(declaredType, bindings);
        var probeBindings = CloneBindings(bindings);
        return TryCollectTypeBindings(resolvedDeclaredType, concreteType, probeBindings);
    }

    private bool TryResolveTraitDispatchTarget(
        MirFunctionRef functionRef,
        TypeId receiverTypeId,
        out SymbolId implMethodId,
        out string implMethodName)
    {
        implMethodId = SymbolId.None;
        implMethodName = string.Empty;

        if (functionRef.TraitOwnerId.IsValid &&
            !string.IsNullOrWhiteSpace(functionRef.Name) &&
            TryResolveImplMethod(
                functionRef.TraitOwnerId,
                functionRef.Name,
                receiverTypeId,
                functionRef.SymbolId,
                out implMethodId,
                out implMethodName) &&
            implMethodId != functionRef.SymbolId)
        {
            return true;
        }

        if (TryGetTraitMethodInfo(functionRef, out var methodInfo) &&
            methodInfo.TraitId.IsValid &&
            !string.IsNullOrWhiteSpace(methodInfo.Name) &&
            TryResolveImplMethod(
                methodInfo.TraitId,
                methodInfo.Name,
                receiverTypeId,
                methodInfo.MethodId,
                out implMethodId,
                out implMethodName) &&
            implMethodId != functionRef.SymbolId)
        {
            return true;
        }

        return false;
    }

    private bool TryResolveTraitInvokeHelperDispatchTarget(
        MirFunc containingFunction,
        MirFunctionRef functionRef,
        TypeId receiverTypeId,
        out SymbolId implMethodId,
        out string implMethodName)
    {
        implMethodId = SymbolId.None;
        implMethodName = string.Empty;

        if (containingFunction.TraitInvokeHelper == TraitInvokeHelperKind.None ||
            !containingFunction.TraitInvokeHelperTraitId.IsValid ||
            string.IsNullOrWhiteSpace(functionRef.Name))
        {
            return false;
        }

        return TryResolveImplMethod(
            containingFunction.TraitInvokeHelperTraitId,
            functionRef.Name,
            receiverTypeId,
            functionRef.SymbolId,
            out implMethodId,
            out implMethodName);
    }

    private bool IsMostSpecificTraitDispatchType(TypeId candidateTypeId, IEnumerable<TypeId> allCandidateTypeIds)
    {
        if (!candidateTypeId.IsValid)
        {
            return false;
        }

        var candidateShape = BuildImplementingTypeShape(candidateTypeId);
        foreach (var otherTypeId in allCandidateTypeIds)
        {
            if (otherTypeId == candidateTypeId || !otherTypeId.IsValid)
            {
                continue;
            }

            var otherShape = BuildImplementingTypeShape(otherTypeId);
            if (ImplSpecializationComparer.CompareNodes(otherShape, candidateShape) ==
                ImplSpecializationRelation.MoreSpecific)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryResolveImplMethod(
        SymbolId ownerTrait,
        string traitMethodName,
        TypeId receiverTypeId,
        SymbolId traitMethodId,
        out SymbolId implMethodId,
        out string implMethodName)
    {
        var key = new TraitDispatchLookupKey(ownerTrait, traitMethodName, receiverTypeId, traitMethodId);
        if (_traitDispatchLookupCache.TryGetValue(key, out var cached))
        {
            implMethodId = cached.ImplMethodId;
            implMethodName = cached.ImplMethodName;
            return cached.Found;
        }

        if (!TryResolveImplMethodCore(
                ownerTrait,
                traitMethodName,
                receiverTypeId,
                traitMethodId,
                out implMethodId,
                out implMethodName))
        {
            _traitDispatchLookupCache[key] = new TraitDispatchLookupResult(false, SymbolId.None, string.Empty);
            return false;
        }

        _traitDispatchLookupCache[key] = new TraitDispatchLookupResult(true, implMethodId, implMethodName);
        return true;
    }

    private bool TryResolveImplMethodCore(
        SymbolId ownerTrait,
        string traitMethodName,
        TypeId receiverTypeId,
        SymbolId traitMethodId,
        out SymbolId implMethodId,
        out string implMethodName)
    {
        implMethodId = SymbolId.None;
        implMethodName = string.Empty;

        var applicableImpls = ResolveApplicableImplsForReceiverType(ownerTrait, receiverTypeId);

        // Supertrait chain fallback: if no direct impl found, try child traits
        // whose supertrait chain includes ownerTrait.
        // E.g., looking up Eq for a type that has @impl(Ord) where Ord: Eq.
        if (applicableImpls.Count == 0)
        {
            applicableImpls = ResolveApplicableImplsViaSupertraitChain(ownerTrait, receiverTypeId);
        }

        if (applicableImpls.Count == 0)
        {
            // Default implementation fallback: if the trait method has a default body,
            // dispatch directly to the trait method's own FuncSymbol.
            if (TryResolveDefaultImplMethod(ownerTrait, traitMethodId, out implMethodId, out implMethodName))
            {
                return true;
            }

            return false;
        }

        var methodCandidates = new List<(ImplSymbol Impl, SymbolId MethodId, string LoweredName)>();
        foreach (var impl in applicableImpls)
        {
            if (TryResolveMappedImplMethod(
                    impl,
                    traitMethodId,
                    out var mappedMethodId,
                    out var mappedLoweredName))
            {
                methodCandidates.Add((impl, mappedMethodId, mappedLoweredName));
                continue;
            }

            if (traitMethodId.IsValid)
            {
                continue;
            }

            foreach (var methodId in impl.Methods)
            {
                if (!TryResolveImplMethodName(methodId, out var candidateMethodName) ||
                    !string.Equals(candidateMethodName, traitMethodName, StringComparison.Ordinal))
                {
                    continue;
                }

                methodCandidates.Add((impl, methodId, ResolveLoweredFunctionName(methodId, candidateMethodName)));
            }
        }

        if (methodCandidates.Count == 0)
        {
            return false;
        }

        var distinctMethodIds = methodCandidates
            .Select(static candidate => candidate.MethodId)
            .Distinct()
            .ToList();
        if (distinctMethodIds.Count == 1)
        {
            var candidate = methodCandidates[0];
            implMethodId = candidate.MethodId;
            implMethodName = candidate.LoweredName;
            return true;
        }

        var bestImpl = TryChooseMostSpecificImpl(methodCandidates.Select(static candidate => candidate.Impl).ToList());
        if (bestImpl == null)
        {
            return false;
        }

        var bestCandidate = methodCandidates.FirstOrDefault(candidate => candidate.Impl.Id == bestImpl.Id);
        if (!bestCandidate.MethodId.IsValid || string.IsNullOrWhiteSpace(bestCandidate.LoweredName))
        {
            return false;
        }

        implMethodId = bestCandidate.MethodId;
        implMethodName = bestCandidate.LoweredName;
        return true;
    }

    private bool TryResolveMappedImplMethod(
        ImplSymbol impl,
        SymbolId traitMethodId,
        out SymbolId implMethodId,
        out string implMethodName)
    {
        implMethodId = SymbolId.None;
        implMethodName = string.Empty;
        if (!traitMethodId.IsValid ||
            !impl.TraitMethodImplementations.TryGetValue(traitMethodId, out var mappedMethodId) ||
            !mappedMethodId.IsValid ||
            !impl.Methods.Contains(mappedMethodId) ||
            !TryResolveImplMethodName(mappedMethodId, out var mappedMethodName))
        {
            return false;
        }

        implMethodId = mappedMethodId;
        implMethodName = ResolveLoweredFunctionName(mappedMethodId, mappedMethodName);
        return true;
    }

    private bool TraitMethodMatchesImplMethod(
        ImplSymbol impl,
        SymbolId traitMethodId,
        SymbolId implMethodId,
        string traitMethodName)
    {
        if (traitMethodId.IsValid &&
            impl.TraitMethodImplementations.TryGetValue(traitMethodId, out var mappedMethodId))
        {
            return mappedMethodId == implMethodId ||
                   TryResolveImplMethodName(implMethodId, out var mappedFallbackName) &&
                   string.Equals(mappedFallbackName, traitMethodName, StringComparison.Ordinal);
        }

        if (traitMethodId.IsValid)
        {
            return false;
        }

        return TryResolveImplMethodName(implMethodId, out var candidateMethodName) &&
               string.Equals(candidateMethodName, traitMethodName, StringComparison.Ordinal);
    }

    private bool TryResolveImplMethodName(SymbolId methodId, out string methodName)
    {
        methodName = string.Empty;

        if (methodId.IsValid &&
            _functionBySymbol.TryGetValue(methodId, out var function) &&
            !string.IsNullOrWhiteSpace(function.SourceName))
        {
            methodName = function.SourceName;
            return true;
        }

        if (methodId.IsValid &&
            _functionBySymbol.TryGetValue(methodId, out function) &&
            !string.IsNullOrWhiteSpace(function.Name))
        {
            methodName = function.Name;
            return true;
        }

        return false;
    }

    private bool TryResolveImplMethodSignature(SymbolId methodId, out TraitDispatchMethodSignature signature)
    {
        signature = default;

        if (methodId.IsValid &&
            _functionBySymbol.TryGetValue(methodId, out var function))
        {
            var methodName = !string.IsNullOrWhiteSpace(function.SourceName)
                ? function.SourceName
                : function.Name;
            if (!string.IsNullOrWhiteSpace(methodName))
            {
                var parameterTypes = function.Locals
                    .Where(static local => local.IsParameter)
                    .OrderBy(static local => local.Id.Value)
                    .Select(static local => local.TypeId)
                    .ToList();

                signature = new TraitDispatchMethodSignature(
                    methodName,
                    parameterTypes,
                    function.ReturnType);
                return true;
            }
        }

        return false;
    }

    private bool TryResolveImplMethodSignatureTypeId(SymbolId methodId, out TypeId signatureTypeId)
    {
        signatureTypeId = TypeId.None;

        if (!methodId.IsValid)
        {
            return false;
        }

        if (_functionBySymbol.TryGetValue(methodId, out var function) &&
            TryResolveFunctionSignatureTypeId(function, out signatureTypeId))
        {
            return true;
        }

        if (_functionTypeIdBySymbol.TryGetValue(methodId, out signatureTypeId) &&
            signatureTypeId.IsValid)
        {
            return true;
        }

        return false;
    }

}
