using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Semantic;
using Eidosc.Utils;

using Eidosc.Diagnostic;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private Type CreateAdtTypeFromBinding(CtorTypeBinding binding, Dictionary<string, Type> typeVarEnv, SourceSpan span)
    {
        if (_symbolTable.GetSymbol<AdtSymbol>(binding.AdtId) is not { } adtSymbol)
        {
            AddError(span, DiagnosticMessages.CannotConstructAdtTypeMissingAdtSymbol(binding.CtorId));
            return CreateErrorRecoveryType();
        }

        var typeArgs = new List<Type>(binding.AdtTypeParamNames.Count);
        foreach (var name in binding.AdtTypeParamNames)
        {
            if (!typeVarEnv.TryGetValue(name, out var typeArg))
            {
                AddError(span, DiagnosticMessages.CannotConstructAdtTypeMissingTypeParameter(adtSymbol.Name, name));
                return CreateErrorRecoveryType();
            }

            typeArgs.Add(typeArg);
        }

        var valueArgs = new List<GenericValueArgument>();
        if (_valueGenericArgumentsByTypeEnv.TryGetValue(typeVarEnv, out var scopedValueArguments))
        {
            for (var parameterIndex = 0; parameterIndex < binding.AdtGenericParameters.Count; parameterIndex++)
            {
                var parameter = binding.AdtGenericParameters[parameterIndex];
                if (parameter.ParameterKind != GenericParameterKind.Value)
                {
                    continue;
                }

                if (!scopedValueArguments.TryGetValue(parameter.SymbolId, out var valueArgument))
                {
                    AddError(span, $"Cannot construct type '{adtSymbol.Name}': missing value parameter '{parameter.Name}'.");
                    return CreateErrorRecoveryType();
                }

                valueArgs.Add(valueArgument with { ParameterIndex = parameterIndex });
            }
        }

        var effectArgs = new List<GenericEffectArgument>();
        for (var parameterIndex = 0; parameterIndex < binding.AdtGenericParameters.Count; parameterIndex++)
        {
            var parameter = binding.AdtGenericParameters[parameterIndex];
            if (parameter.ParameterKind != GenericParameterKind.EffectRow)
            {
                continue;
            }

            if (!typeVarEnv.TryGetValue(parameter.Name, out var effectArgument))
            {
                AddError(span, $"Cannot construct type '{adtSymbol.Name}': missing effect-row parameter '{parameter.Name}'.");
                return CreateErrorRecoveryType();
            }

            effectArgs.Add(new GenericEffectArgument(parameterIndex, effectArgument));
        }

        var rootType = new TyCon
        {
            Name = adtSymbol.Name,
            Symbol = adtSymbol.Id,
            Id = adtSymbol.TypeId,
            Args = typeArgs,
            ValueArgs = valueArgs,
            EffectArgs = effectArgs
        };

        if (binding.ResultAdtId == binding.AdtId)
        {
            return _substitution.Apply(rootType);
        }

        if (!_closedCaseDefinitionsBySymbol.TryGetValue(binding.ResultAdtId, out var caseDefinition))
        {
            AddError(span, DiagnosticMessages.CannotConstructAdtTypeMissingAdtSymbol(binding.ResultAdtId));
            return CreateErrorRecoveryType();
        }

        return CreateExactClosedCaseType(binding, caseDefinition, rootType, typeVarEnv, span);
    }

    private Type CreateExactClosedCaseType(
        CtorTypeBinding binding,
        ClosedCaseDefinition caseDefinition,
        TyCon rootType,
        Dictionary<string, Type> typeVarEnv,
        SourceSpan span)
    {
        var kindEnvByName = CreateTypeParamKindMapForCtorBinding(
            binding.AdtId,
            binding.AdtTypeParamNames,
            binding.CtorId,
            binding.CtorTypeParamNames);

        return CreateExactClosedCaseType(caseDefinition, rootType, typeVarEnv, kindEnvByName, span);
    }

    private Type CreateExactClosedCaseType(
        ClosedCaseDefinition caseDefinition,
        TyCon rootType,
        Dictionary<string, Type> typeVarEnv,
        IReadOnlyDictionary<string, Kind> kindEnvByName,
        SourceSpan span)
    {
        TyCon current = rootType;

        foreach (var caseType in caseDefinition.Path)
        {
            if (_symbolTable.GetSymbol<AdtSymbol>(caseType.SymbolId) is not { } caseSymbol ||
                current.Symbol != caseSymbol.ParentAdt)
            {
                AddError(span, $"Invalid closed case path for '{caseType.Name}'.");
                return CreateErrorRecoveryType();
            }

            var parentType = current;
            if (caseType.ParentSpecialization != null)
            {
                var resolvedParent = _substitution.Apply(ConvertTypeWithAdditionalKindContext(
                    caseType.ParentSpecialization,
                    typeVarEnv,
                    kindEnvByName,
                    allowTypeConstructorReference: false));
                if (resolvedParent is not TyCon specializedParent ||
                    specializedParent.Symbol != caseSymbol.ParentAdt)
                {
                    AddError(
                        caseType.ParentSpecialization.Span,
                        DiagnosticMessages.GadtConstructorReturnTypeMustTargetOwnAdt(
                            _symbolTable.GetSymbol<AdtSymbol>(caseSymbol.ParentAdt)?.Name ?? caseDefinition.Root.Name));
                    return CreateErrorRecoveryType();
                }

                parentType = specializedParent;
            }

            var exactTypeArguments = parentType.Args.ToList();
            foreach (var parameter in caseType.TypeParams)
            {
                if (parameter.ParameterKind != GenericParameterKind.Type)
                {
                    continue;
                }

                if (!typeVarEnv.TryGetValue(parameter.Name, out var typeArgument))
                {
                    AddError(span, DiagnosticMessages.CannotConstructAdtTypeMissingTypeParameter(caseSymbol.Name, parameter.Name));
                    return CreateErrorRecoveryType();
                }
                exactTypeArguments.Add(typeArgument);
            }

            var exactValueArguments = parentType.ValueArgs.ToList();
            if (_valueGenericArgumentsByTypeEnv.TryGetValue(typeVarEnv, out var scopedValues))
            {
                var parameterOffset = GetClosedCaseEffectiveGenericParameterCount(caseSymbol.ParentAdt);
                for (var parameterIndex = 0; parameterIndex < caseType.TypeParams.Count; parameterIndex++)
                {
                    var parameter = caseType.TypeParams[parameterIndex];
                    if (parameter.ParameterKind == GenericParameterKind.Value &&
                        scopedValues.TryGetValue(parameter.SymbolId, out var valueArgument))
                    {
                        exactValueArguments.Add(valueArgument with { ParameterIndex = parameterOffset + parameterIndex });
                    }
                }
            }

            var exactEffectArguments = parentType.EffectArgs.ToList();
            var effectParameterOffset = GetClosedCaseEffectiveGenericParameterCount(caseSymbol.ParentAdt);
            for (var parameterIndex = 0; parameterIndex < caseType.TypeParams.Count; parameterIndex++)
            {
                var parameter = caseType.TypeParams[parameterIndex];
                if (parameter.ParameterKind != GenericParameterKind.EffectRow)
                {
                    continue;
                }

                if (!typeVarEnv.TryGetValue(parameter.Name, out var effectArgument))
                {
                    AddError(span, $"Cannot construct type '{caseSymbol.Name}': missing effect-row parameter '{parameter.Name}'.");
                    return CreateErrorRecoveryType();
                }

                exactEffectArguments.Add(new GenericEffectArgument(
                    effectParameterOffset + parameterIndex,
                    effectArgument));
            }

            current = new TyCon
            {
                Name = GetQualifiedCaseTypeName(caseSymbol.Id),
                Symbol = caseSymbol.Id,
                Id = caseSymbol.TypeId,
                Args = exactTypeArguments,
                ValueArgs = exactValueArguments,
                EffectArgs = exactEffectArguments
            };
        }

        return _substitution.Apply(current);
    }

    private Type InferMethodCall(MethodCallExpr method)
    {
        method.InferredEffects = null;

        if (string.IsNullOrWhiteSpace(method.MethodName))
        {
            AddError(method.Span, DiagnosticMessages.MethodCallMissingMethodName);
            return CreateErrorRecoveryType();
        }

        if (method.ResolvedStaticExpression != null)
        {
            var staticType = SafeInferExpression(method.ResolvedStaticExpression);
            method.InferredType = staticType;
            method.InferredEffects = method.ResolvedStaticExpression.InferredEffects;
            return staticType;
        }

        if (CanInferFieldAccess(method))
        {
            if (TryInferFieldAccess(method, out var fieldType))
            {
                return fieldType;
            }

            if (!method.SymbolId.IsValid && TryInferCStructFieldAccess(method, out var cstructFieldType))
            {
                method.InferredType = cstructFieldType;
                return cstructFieldType;
            }

            if (!method.SymbolId.IsValid)
            {
                var receiverType = method.Receiver != null
                    ? SafeInferExpression(method.Receiver)
                    : CreateErrorRecoveryType();
                var readableReceiverType = UnwrapReadableReferenceType(receiverType);
                AddError(method.Span, DiagnosticMessages.TypeHasNoReadableField(readableReceiverType, method.MethodName));
                return CreateErrorRecoveryType();
            }
        }

        if (!method.ResolvedAsStaticPath &&
            method.Receiver != null &&
            method.HasExplicitCallSyntax &&
            TryInferTypeDirectedMethodCall(method, out var typedMethodResult))
        {
            method.InferredType = typedMethodResult;
            return typedMethodResult;
        }

        var desugared = method.ToDesugaredCall();
        var resultType = InferCall(desugared);
        method.InferredType = desugared.InferredType ?? resultType;
        method.InferredEffects = desugared.InferredEffects;
        return resultType;
    }

    private bool TryInferTypeDirectedMethodCall(MethodCallExpr method, out Type resultType)
    {
        resultType = _substitution.FreshTypeVariable();
        if (method.Receiver == null)
        {
            return false;
        }

        var candidates = GetTypeDirectedMethodCandidates(method);
        if (candidates.Count == 0)
        {
            return false;
        }

        var receiverType = SafeInferExpression(method.Receiver);
        AddReceiverOwnedPrecompiledMethodCandidates(method.MethodName, receiverType, candidates);
        var argumentTypes = new List<Type>(method.PositionalArgs.Count + method.NamedArgs.Count + 1)
        {
            receiverType
        };

        foreach (var arg in method.PositionalArgs)
        {
            argumentTypes.Add(arg is LambdaExpr lambda
                ? CreateLambdaShape(lambda)
                : SafeInferExpression(arg));
        }

        foreach (var arg in method.NamedArgs)
        {
            if (arg.Value != null)
            {
                argumentTypes.Add(arg.Value is LambdaExpr lambda
                    ? CreateLambdaShape(lambda)
                    : SafeInferExpression(arg.Value));
                continue;
            }

            ReportMissingNamedArgumentValue(arg);
            argumentTypes.Add(CreateErrorRecoveryType());
        }

        if (argumentTypes.Any(ContainsErrorRecoveryType))
        {
            resultType = CreateErrorRecoveryType();
            return true;
        }

        if (TrySelectConstrainedTraitMethodCandidate(method.MethodName, receiverType, out var constrainedTraitMethod))
        {
            method.SymbolId = constrainedTraitMethod;
            var constrainedMethodType = TryCreateConstrainedTraitMethodType(
                constrainedTraitMethod,
                receiverType,
                out var traitMethodType)
                ? traitMethodType
                : InferFunctionSymbolType(constrainedTraitMethod, method.Span);
            constrainedMethodType = ApplyMethodArguments(method, constrainedMethodType, argumentTypes);

            resultType = _substitution.Apply(constrainedMethodType);
            return true;
        }

        if (!TryResolveTypeDirectedMethodCandidate(candidates, argumentTypes, out var resolution))
        {
            ReportCallableResolutionFailure(
                method.Span,
                method.MethodName,
                "method",
                resolution,
                argumentTypes,
                DiagnosticMessages.NoMethodOverloadAcceptsReceiverType(
                    method.MethodName,
                    UnwrapReadableReferenceType(receiverType)));
            resultType = CreateErrorRecoveryType();
            return true;
        }

        var selectedCandidate = resolution.SelectedSymbolId;
        method.SymbolId = selectedCandidate;
        var currentType = InferFunctionSymbolType(selectedCandidate, method.Span);
        currentType = ApplyMethodArguments(method, currentType, argumentTypes);

        resultType = _substitution.Apply(currentType);
        return true;
    }

    private bool TrySelectConstrainedTraitMethodCandidate(
        string methodName,
        Type receiverType,
        out SymbolId selectedMethod)
    {
        selectedMethod = SymbolId.None;
        if (!TryGetConstrainedReceiverVariableIndex(receiverType, out var receiverVarIndex))
        {
            return false;
        }

        var constraints = _constraintGenerator.Constraints.GetTraitConstraintsForVar(receiverVarIndex);
        if (constraints.Count == 0)
        {
            return false;
        }

        foreach (var constraint in constraints)
        {
            if (_symbolTable.GetSymbol<TraitSymbol>(constraint.Trait) is not { } trait)
            {
                continue;
            }

            foreach (var methodId in trait.Methods)
            {
                if (_symbolTable.GetSymbol(methodId) is not FuncSymbol { Name: var candidateName } ||
                    !string.Equals(candidateName, methodName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (selectedMethod.IsValid && selectedMethod != methodId)
                {
                    selectedMethod = SymbolId.None;
                    return false;
                }

                selectedMethod = methodId;
            }
        }

        return selectedMethod.IsValid;
    }

    private bool TryCreateConstrainedTraitMethodType(SymbolId methodId, Type receiverType, out Type methodType)
    {
        methodType = _substitution.FreshTypeVariable();
        if (!_functionDefinitionsBySymbol.TryGetValue(methodId, out var methodDef) ||
            methodDef.Signature.Count == 0)
        {
            return false;
        }

        var typeVarEnv = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [WellKnownStrings.Keywords.Self] = receiverType
        };
        var kindEnvByName = CreateTypeParamKindMap(methodDef.TypeParams);
        var kindEnvByTypeVar = new Dictionary<int, Kind>();
        RegisterSignatureTypeParams(methodDef.TypeParams, kindEnvByName, typeVarEnv, kindEnvByTypeVar);

        _typeParamKindStack.Push(kindEnvByName);
        _typeParamVarKindStack.Push(kindEnvByTypeVar);
        try
        {
            methodType = ConvertFunctionSignatureType(methodDef.Signature, typeVarEnv);
            if (methodType is TyFun { Params.Count: > 0 } functionType)
            {
                functionType.Params[0] = receiverType;
            }

            return true;
        }
        finally
        {
            _typeParamVarKindStack.Pop();
            _typeParamKindStack.Pop();
        }
    }

    private bool TryGetConstrainedReceiverVariableIndex(Type receiverType, out int variableIndex)
    {
        var resolvedReceiver = _substitution.Apply(receiverType);
        if (resolvedReceiver is TyVar receiverVar)
        {
            variableIndex = receiverVar.Index;
            return true;
        }

        if (resolvedReceiver is TyCon { ConstructorVarIndex: { } index })
        {
            variableIndex = index;
            return true;
        }

        variableIndex = -1;
        return false;
    }

    private List<SymbolId> GetTypeDirectedMethodCandidates(MethodCallExpr method)
    {
        return GetTypeDirectedCallableCandidates(
            method.MethodName,
            method.MethodCandidateSymbolIds,
            method.SymbolId);
    }

    private List<SymbolId> GetTypeDirectedCallableCandidates(
        string name,
        IEnumerable<SymbolId>? candidateIds = null,
        SymbolId resolvedSymbolId = default)
    {
        var candidates = new List<SymbolId>();
        if (candidateIds != null)
        {
            foreach (var candidate in candidateIds)
            {
                AddDistinctFunctionCandidate(candidates, candidate);
            }
        }

        AddDistinctFunctionCandidate(candidates, resolvedSymbolId);
        if (candidates.Count > 0)
        {
            return candidates;
        }

        foreach (var candidate in GetPrecompiledCallableCandidatesByName(name))
        {
            AddDistinctFunctionCandidate(candidates, candidate);
        }

        return candidates;
    }

    private IReadOnlyList<SymbolId> GetPrecompiledCallableCandidatesByName(string name)
    {
        if (_precompiledCallableCandidateCache.TryGetValue(name, out var cached))
        {
            IncrementProfilingCounter("Types.callableCandidateCache.hits");
            return cached;
        }

        IncrementProfilingCounter("Types.callableCandidateCache.misses");
        var candidates = new List<SymbolId>();
        foreach (var function in _symbolTable.Symbols.Values.OfType<FuncSymbol>())
        {
            if (string.Equals(function.Name, name, StringComparison.Ordinal) &&
                IsPrecompiledSymbol(function))
            {
                candidates.Add(function.Id);
            }
        }

        var result = candidates.ToArray();
        _precompiledCallableCandidateCache[name] = result;
        IncrementProfilingCounter("Types.callableCandidateCache.entries");
        IncrementProfilingCounter("Types.callableCandidateCache.candidates", result.Length);
        return result;
    }

    private void AddDistinctFunctionCandidate(List<SymbolId> candidates, SymbolId candidate)
    {
        if (!candidate.IsValid || candidates.Contains(candidate))
        {
            return;
        }

        if (_symbolTable.GetSymbol(candidate) is FuncSymbol)
        {
            candidates.Add(candidate);
        }
    }

    private bool TrySelectTypeDirectedMethodCandidate(
        IReadOnlyList<SymbolId> candidates,
        IReadOnlyList<Type> argumentTypes,
        out SymbolId selectedCandidate)
    {
        var resolution = ResolveTypeDirectedMethodCandidate(candidates, argumentTypes);
        selectedCandidate = resolution.SelectedSymbolId;
        return resolution.IsResolved && !resolution.IsAmbiguous;
    }

    private bool TryResolveTypeDirectedMethodCandidate(
        IReadOnlyList<SymbolId> candidates,
        IReadOnlyList<Type> argumentTypes,
        out TypeDirectedCandidateResolution resolution)
    {
        resolution = ResolveTypeDirectedMethodCandidate(candidates, argumentTypes);
        return resolution.IsResolved && !resolution.IsAmbiguous;
    }

    private TypeDirectedCandidateResolution ResolveTypeDirectedMethodCandidate(
        IReadOnlyList<SymbolId> candidates,
        IReadOnlyList<Type> argumentTypes)
    {
        if (TryCreateTypeDirectedCallableResolutionCacheKey(candidates, argumentTypes, out var cacheKey))
        {
            if (TryRestorePreviousTypeDirectedCallableResolution(cacheKey, candidates, out var restored))
            {
                IncrementProfilingCounter("Types.callableResolutionPreviousCache.hits");
                IncrementProfilingCounter("Types.callableResolutionPreviousCache.restoreHits");
                IncrementProfilingCounter("Types.callableResolutionPreviousCache.validatedHits");
                _typeDirectedCallableResolutionCache[cacheKey] = restored;
                StoreTypeDirectedCallableResolutionSnapshotEntry(cacheKey, restored);
                return restored;
            }

            if (_typeDirectedCallableResolutionCache.TryGetValue(cacheKey, out var cached))
            {
                IncrementProfilingCounter("Types.callableResolutionCache.hits");
                return cached;
            }

            IncrementProfilingCounter("Types.callableResolutionCache.misses");
            var computed = ResolveTypeDirectedMethodCandidateUncached(candidates, argumentTypes);
            _typeDirectedCallableResolutionCache[cacheKey] = computed;
            StoreTypeDirectedCallableResolutionSnapshotEntry(cacheKey, computed);
            IncrementProfilingCounter("Types.callableResolutionCache.entries");
            return computed;
        }

        IncrementProfilingCounter("Types.callableResolutionCache.skipped");
        return ResolveTypeDirectedMethodCandidateUncached(candidates, argumentTypes);
    }

    private bool TryRestorePreviousTypeDirectedCallableResolution(
        TypeDirectedCallableResolutionCacheKey cacheKey,
        IReadOnlyList<SymbolId> candidates,
        out TypeDirectedCandidateResolution resolution)
    {
        resolution = default;
        if (!_previousTypeDirectedCallableResolutionCache.TryGetValue(cacheKey, out var previous))
        {
            IncrementProfilingCounter("Types.callableResolutionPreviousCache.misses");
            return false;
        }

        var selected = SymbolId.None;
        foreach (var candidate in candidates)
        {
            if (!string.Equals(
                    CreateCallableResolutionCacheCandidateKey(candidate),
                    previous.SelectedCandidate,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (selected.IsValid)
            {
                IncrementProfilingCounter("Types.callableResolutionPreviousCache.remapAmbiguous");
                return false;
            }

            selected = candidate;
        }
        if (!selected.IsValid)
        {
            IncrementProfilingCounter("Types.callableResolutionPreviousCache.remapMisses");
            return false;
        }

        resolution = TypeDirectedCandidateResolution.Resolved(
            selected,
            candidates.Count,
            previous.ViableCandidateCount,
            previous.BestScore);
        return true;
    }

    private void StoreTypeDirectedCallableResolutionSnapshotEntry(
        TypeDirectedCallableResolutionCacheKey cacheKey,
        TypeDirectedCandidateResolution resolution)
    {
        if (!resolution.IsResolved || resolution.IsAmbiguous)
        {
            return;
        }

        var selectedCandidate = CreateCallableResolutionCacheCandidateKey(resolution.SelectedSymbolId);
        if (string.IsNullOrWhiteSpace(selectedCandidate) ||
            selectedCandidate.All(char.IsDigit))
        {
            return;
        }

        _typeDirectedCallableResolutionSnapshotEntries[cacheKey] =
            new TypeDirectedCallableResolutionSnapshotEntry(
                cacheKey.Candidates,
                cacheKey.ArgumentTypes,
                selectedCandidate,
                resolution.CandidateCount,
                resolution.ViableCandidateCount,
                resolution.BestScore);
    }

    private TypeDirectedCandidateResolution ResolveTypeDirectedMethodCandidateUncached(
        IReadOnlyList<SymbolId> candidates,
        IReadOnlyList<Type> argumentTypes)
    {
        var bestScore = int.MinValue;
        var bestCandidates = new List<SymbolId>();
        var viableCandidateCount = 0;

        foreach (var candidate in candidates)
        {
            if (!TryScoreTypeDirectedMethodCandidate(candidate, argumentTypes, out var score))
            {
                continue;
            }

            viableCandidateCount++;
            if (score > bestScore)
            {
                bestScore = score;
                bestCandidates.Clear();
                bestCandidates.Add(candidate);
            }
            else if (score == bestScore)
            {
                bestCandidates.Add(candidate);
            }
        }

        if (bestCandidates.Count == 1)
        {
            return TypeDirectedCandidateResolution.Resolved(
                bestCandidates[0],
                candidates.Count,
                viableCandidateCount,
                bestScore);
        }

        if (bestCandidates.Count > 1)
        {
            return TypeDirectedCandidateResolution.Ambiguous(
                bestCandidates,
                candidates.Count,
                viableCandidateCount,
                bestScore);
        }

        return TypeDirectedCandidateResolution.NoMatch(candidates.Count, viableCandidateCount);
    }

    private bool TryCreateTypeDirectedCallableResolutionCacheKey(
        IReadOnlyList<SymbolId> candidates,
        IReadOnlyList<Type> argumentTypes,
        out TypeDirectedCallableResolutionCacheKey key)
    {
        key = default;
        if (candidates.Count == 0)
        {
            return false;
        }

        var appliedArgumentTypes = new string[argumentTypes.Count];
        for (var i = 0; i < argumentTypes.Count; i++)
        {
            var applied = _substitution.Apply(argumentTypes[i]);
            if (ContainsTypeVariable(applied))
            {
                return false;
            }

            appliedArgumentTypes[i] = CreateCallableResolutionArgumentTypeKey(applied);
        }

        key = new TypeDirectedCallableResolutionCacheKey(
            string.Join(",", candidates.Select(CreateCallableResolutionCacheCandidateKey)),
            string.Join("|", appliedArgumentTypes));
        return true;
    }

    private string CreateCallableResolutionCacheCandidateKey(SymbolId candidate)
    {
        var qualifiedName = TryFormatQualifiedSymbolName(candidate);
        return string.IsNullOrWhiteSpace(qualifiedName)
            ? candidate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : qualifiedName;
    }

    private bool TryScoreTypeDirectedMethodCandidate(
        SymbolId candidate,
        IReadOnlyList<Type> argumentTypes,
        out int score)
    {
        score = IsModuleMember(candidate) ? 16 : 0;
        var trial = _substitution.Clone();
        if (!TryGetFunctionTypeForTrial(candidate, trial, out var currentType))
        {
            return false;
        }

        if (argumentTypes.Count == 0)
        {
            if (!TryResolveEmptyCall(currentType, null, trial, out var emptyResolution))
            {
                return false;
            }

            score += emptyResolution.Kind == EmptyCallResolutionKind.ZeroArgument ? 32 : 8;
            return true;
        }

        for (var i = 0; i < argumentTypes.Count; i++)
        {
            var resolvedFunctionType = trial.Apply(currentType);
            if (resolvedFunctionType is TyFun { Params.Count: > 0 } functionType)
            {
                var paramType = trial.Apply(functionType.Params[0]);
                var argumentType = trial.Apply(argumentTypes[i]);
                var receiverScore = i == 0
                    ? GetReceiverMatchScore(paramType, argumentType)
                    : 0;
                try
                {
                    trial.Unify(
                        paramType,
                        NormalizeClosedCaseArgumentForExpectedType(paramType, argumentType, trial));
                }
                catch (TypeInferenceException)
                {
                    return false;
                }

                if (i == 0)
                {
                    score += receiverScore;
                    score += GetReceiverOwnerModuleMatchScore(candidate, argumentType);
                }

                currentType = functionType.Params.Count == 1
                    ? trial.Apply(functionType.Result)
                    : new TyFun
                    {
                        Params = functionType.Params.Skip(1).ToList(),
                        Result = functionType.Result,
                        Effects = functionType.Effects
                    };
                continue;
            }

            var param = trial.FreshTypeVariable();
            var ret = trial.FreshTypeVariable();
            try
            {
                trial.Unify(resolvedFunctionType, new TyFun { Params = [param], Result = ret });
                var expectedParameter = trial.Apply(param);
                var actualArgument = trial.Apply(argumentTypes[i]);
                trial.Unify(
                    expectedParameter,
                    NormalizeClosedCaseArgumentForExpectedType(expectedParameter, actualArgument, trial));
            }
            catch (TypeInferenceException)
            {
                return false;
            }

            currentType = trial.Apply(ret);
        }

        return true;
    }

    private Type NormalizeClosedCaseArgumentForExpectedType(
        Type expected,
        Type actual,
        Substitution substitution)
    {
        var resolvedExpected = substitution.Apply(expected);
        var resolvedActual = substitution.Apply(actual);
        if (resolvedExpected is TyCon expectedConstructor &&
            resolvedActual is TyCon actualConstructor)
        {
            if (expectedConstructor is
                {
                    ConstructorVarIndex: not null,
                    IsGenericInstantiationConstructor: true
                } &&
                TryPromoteClosedCaseToRoot(actualConstructor, out var promotedActual))
            {
                return promotedActual with
                {
                    Args = expectedConstructor.Args.Count == promotedActual.Args.Count
                        ? NormalizeClosedCaseArguments(
                            expectedConstructor.Args,
                            promotedActual.Args,
                            substitution)
                        : promotedActual.Args
                };
            }

            if (TryResolveClosedCaseTypeSymbol(expectedConstructor, out var expectedSymbol) &&
                TryResolveClosedCaseTypeSymbol(actualConstructor, out var actualSymbol) &&
                actualSymbol != expectedSymbol &&
                _symbolTable.IsClosedCaseSubtype(actualSymbol, expectedSymbol))
            {
                return actualConstructor with
                {
                    Name = expectedConstructor.Name,
                    Symbol = expectedSymbol,
                    Id = expectedConstructor.Id,
                    Args = NormalizeClosedCaseArguments(
                        expectedConstructor.Args,
                        actualConstructor.Args,
                        substitution)
                };
            }

            if (HaveSameTypeConstructorIdentity(expectedConstructor, actualConstructor) &&
                expectedConstructor.Args.Count == actualConstructor.Args.Count)
            {
                return actualConstructor with
                {
                    Args = NormalizeClosedCaseArguments(
                        expectedConstructor.Args,
                        actualConstructor.Args,
                        substitution)
                };
            }
        }

        if (resolvedExpected is TyTuple expectedTuple &&
            resolvedActual is TyTuple actualTuple &&
            expectedTuple.Elements.Count == actualTuple.Elements.Count)
        {
            return actualTuple with
            {
                Elements = NormalizeClosedCaseArguments(
                    expectedTuple.Elements,
                    actualTuple.Elements,
                    substitution)
            };
        }

        return resolvedActual;
    }

    private List<Type> NormalizeClosedCaseArguments(
        IReadOnlyList<Type> expected,
        IReadOnlyList<Type> actual,
        Substitution substitution)
    {
        var normalized = new List<Type>(expected.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            normalized.Add(index < actual.Count
                ? NormalizeClosedCaseArgumentForExpectedType(expected[index], actual[index], substitution)
                : substitution.Apply(expected[index]));
        }
        return normalized;
    }

    private bool IsModuleMember(SymbolId candidateId)
    {
        if (!candidateId.IsValid)
        {
            return false;
        }

        return TryGetOwningModuleId(candidateId, out _);
    }

    private int GetReceiverOwnerModuleMatchScore(SymbolId candidateId, Type receiverType)
    {
        var resolvedReceiverType = UnwrapReadableReferenceType(receiverType);
        if (resolvedReceiverType is not TyCon receiverTyCon ||
            !TryGetOwningModule(candidateId, out var candidateModule))
        {
            return 0;
        }

        var receiverSymbol = receiverTyCon.Symbol.IsValid
            ? _symbolTable.GetClosedCaseRoot(receiverTyCon.Symbol)
            : SymbolId.None;
        if (receiverSymbol.IsValid &&
            TryGetOwningModuleId(receiverSymbol, out var receiverModuleId) &&
            candidateModule.Id.Equals(receiverModuleId))
        {
            return 8;
        }

        var moduleLeaf = candidateModule.Path.LastOrDefault();
        var receiverName = receiverSymbol.IsValid
            ? _symbolTable.GetSymbol(receiverSymbol)?.Name ?? receiverTyCon.Name
            : receiverTyCon.Name;
        if (string.Equals(moduleLeaf, receiverName, StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }

        if (_symbolTable.GetSymbol(candidateId) is FuncSymbol function &&
            !string.IsNullOrWhiteSpace(function.Span.FilePath))
        {
            var sourceModuleLeaf = Path.GetFileNameWithoutExtension(function.Span.FilePath);
            if (string.Equals(sourceModuleLeaf, receiverName, StringComparison.OrdinalIgnoreCase))
            {
                return 8;
            }
        }

        return 0;
    }

    private void AddReceiverOwnedPrecompiledMethodCandidates(
        string methodName,
        Type receiverType,
        List<SymbolId> candidates)
    {
        foreach (var candidate in GetPrecompiledCallableCandidatesByName(methodName))
        {
            if (GetReceiverOwnerModuleMatchScore(candidate, receiverType) > 0)
            {
                AddDistinctFunctionCandidate(candidates, candidate);
            }
        }
    }

    private bool TryGetOwningModuleId(SymbolId memberId, out SymbolId moduleId)
    {
        return _symbolTable.Modules.TryGetOwningModuleId(memberId, out moduleId);
    }

    private bool TryGetOwningModule(SymbolId memberId, out ModuleSymbol module)
    {
        return _symbolTable.Modules.TryGetOwningModule(memberId, out module);
    }

    private bool TryGetFunctionTypeForTrial(SymbolId candidate, Substitution trial, out Type functionType)
    {
        functionType = trial.FreshTypeVariable();
        var scheme = _env.Lookup(candidate);
        if (scheme != null)
        {
            functionType = trial.InstantiateScheme(scheme).Type;
            return true;
        }

        if (_symbolTable.GetSymbol(candidate) is FuncSymbol funcSymbol)
        {
            functionType = CreateFunctionType(funcSymbol, trial);
            return true;
        }

        return false;
    }

    private static int GetReceiverMatchScore(Type parameterType, Type receiverType)
    {
        return (parameterType, receiverType) switch
        {
            (TyCon left, TyCon right) when left.Symbol.IsValid && left.Symbol.Equals(right.Symbol) => 4,
            (TyCon left, TyCon right) when string.Equals(left.Name, right.Name, StringComparison.Ordinal) => 3,
            (TyVar, _) => 1,
            _ => 2
        };
    }

    private Type ApplyMethodArguments(MethodCallExpr method, Type functionType, List<Type> argumentTypes)
    {
        var currentType = _substitution.Apply(functionType);
        for (var i = 0; i < argumentTypes.Count; i++)
        {
            var resolvedFunc = _substitution.Apply(currentType);
            if (resolvedFunc is TyFun { Params.Count: > 0 } fn)
            {
                var resolvedParam = _substitution.Apply(fn.Params[0]);
                var originalArgument = i switch
                {
                    0 => method.Receiver,
                    _ when i - 1 < method.PositionalArgs.Count => method.PositionalArgs[i - 1],
                    _ => null
                };

                if (i > 0 && originalArgument is LambdaExpr lambdaArgument)
                {
                    argumentTypes[i] = InferExpressionWithExpectedType(lambdaArgument, resolvedParam);
                }

                var resolvedArg = _substitution.Apply(argumentTypes[i]);

                if (resolvedParam is not (TyRef or TyMutRef) &&
                    resolvedArg is TyRef or TyMutRef)
                {
                    var innerType = resolvedArg switch
                    {
                        TyRef reference => _substitution.Apply(reference.Inner),
                        TyMutRef mutReference => _substitution.Apply(mutReference.Inner),
                        _ => resolvedArg
                    };

                    if (originalArgument != null)
                    {
                        var syntheticDeref = new UnaryExpr();
                        syntheticDeref.SetOperator(UnaryOp.Deref);
                        syntheticDeref.SetOperand(originalArgument);
                        syntheticDeref.SetSpan(originalArgument.Span);
                        syntheticDeref.InferredType = innerType;

                        if (i == 0)
                        {
                            method.SetReceiver(syntheticDeref);
                        }
                        else if (i - 1 < method.PositionalArgs.Count)
                        {
                            method.PositionalArgs[i - 1] = syntheticDeref;
                        }
                    }

                    argumentTypes[i] = innerType;
                }
            }

            var argumentSpan = i == 0
                ? method.Receiver?.Span ?? method.Span
                : i - 1 < method.PositionalArgs.Count
                    ? method.PositionalArgs[i - 1].Span
                    : method.Span;
            currentType = ApplyCallArgument(method, currentType, argumentTypes[i], argumentSpan);
        }

        if (method.HasExplicitCallSyntax &&
            method.PositionalArgs.Count == 0 &&
            method.NamedArgs.Count == 0 &&
            TryResolveEmptyCall(currentType, null, _substitution, out var emptyResolution))
        {
            ApplyEmptyMethodCallResolution(method, emptyResolution);
            AccumulateResolvedFunctionEffects(method, currentType);
            ResolveAccumulatedCallEffects(method);
            return _substitution.Apply(emptyResolution.ResultType);
        }

        method.ClearEmptyCallResolution();
        ResolveAccumulatedCallEffects(method);
        return currentType;
    }

    private static void ApplyEmptyMethodCallResolution(MethodCallExpr method, EmptyCallResolution resolution)
    {
        switch (resolution.Kind)
        {
            case EmptyCallResolutionKind.UnitSugar:
                method.MarkSyntheticUnitArguments(resolution.SynthesizedUnitArgumentCount);
                break;
            case EmptyCallResolutionKind.FfiUnitElision:
                method.MarkFfiUnitArgumentElision();
                break;
            default:
                method.ClearEmptyCallResolution();
                break;
        }
    }

    private static bool CanInferFieldAccess(MethodCallExpr method)
    {
        return !method.ResolvedAsStaticPath &&
               !method.HasExplicitCallSyntax &&
               method.Receiver != null &&
               method.PositionalArgs.Count == 0 &&
               method.NamedArgs.Count == 0;
    }

    private bool TryInferFieldAccess(MethodCallExpr method, out Type fieldType)
    {
        fieldType = _substitution.FreshTypeVariable();

        if (method.HasExplicitCallSyntax ||
            method.Receiver == null ||
            method.PositionalArgs.Count > 0 ||
            method.NamedArgs.Count > 0)
        {
            return false;
        }

        var receiverType = SafeInferExpression(method.Receiver);
        if (ContainsErrorRecoveryType(receiverType))
        {
            fieldType = CreateErrorRecoveryType();
            return true;
        }

        if (!TryResolveReadableFieldType(receiverType, method.MethodName, out fieldType, out var fieldSymbolId))
        {
            fieldType = _substitution.FreshTypeVariable();
            return false;
        }

        method.MarkResolvedAsFieldAccess(fieldSymbolId);
        return true;
    }

    private bool TryResolveReadableFieldType(
        Type receiverType,
        string fieldName,
        out Type fieldType,
        out SymbolId fieldSymbolId)
    {
        fieldType = _substitution.FreshTypeVariable();
        fieldSymbolId = SymbolId.None;

        var readableType = UnwrapReadableReferenceType(receiverType);
        if (readableType is not TyCon receiverCon)
        {
            return false;
        }

        if (!receiverCon.Symbol.IsValid)
        {
            return false;
        }

        var definitionSymbol = GetClosedCaseRoot(receiverCon.Symbol);
        if (!_adtDefinitionsBySymbol.TryGetValue(definitionSymbol, out var adtDefinition))
        {
            return false;
        }

        var casePath = _closedCaseDefinitionsBySymbol.TryGetValue(receiverCon.Symbol, out var caseDefinition)
            ? caseDefinition.Path
            : [];
        var fieldOwner = adtDefinition.SymbolId;
        var fieldDefinition = adtDefinition.Fields.FirstOrDefault(field =>
            string.Equals(field.Name, fieldName, StringComparison.Ordinal) &&
            field.Type != null);
        if (fieldDefinition == null)
        {
            foreach (var caseType in casePath)
            {
                fieldDefinition = caseType.Fields.FirstOrDefault(field =>
                    string.Equals(field.Name, fieldName, StringComparison.Ordinal) &&
                    field.Type != null);
                if (fieldDefinition != null)
                {
                    fieldOwner = caseType.SymbolId;
                    break;
                }
            }
        }
        if (fieldDefinition?.Type != null)
        {
            var typeVarEnv = CreateClosedCaseTypeVarEnv(
                adtDefinition,
                casePath,
                receiverCon.Args,
                receiverCon.ValueArgs,
                receiverCon.EffectArgs);
            var kindEnvByName = CreateTypeParamKindMapForOwner(adtDefinition.SymbolId, GetAdtTypeParamNames(adtDefinition));
            fieldType = _substitution.Apply(ConvertTypeWithAdditionalKindContext(fieldDefinition.Type, typeVarEnv, kindEnvByName));
            fieldSymbolId = TryResolveAdtFieldSymbolId(fieldOwner, fieldName);
            return true;
        }

        var recordCtorBindings = GetRecordCtorTypeBindings(receiverCon.Symbol);
        if (recordCtorBindings.Count != 1 ||
            !recordCtorBindings[0].NamedArgTypes.TryGetValue(fieldName, out var ctorFieldType))
        {
            return false;
        }

        var ctorTypeVarEnv = CreateCtorTypeVarEnv(
            recordCtorBindings[0],
            receiverCon.Args,
            receiverCon.ValueArgs,
            receiverCon.EffectArgs);
        var ctorKindEnvByName = CreateTypeParamKindMapForOwner(adtDefinition.SymbolId, GetAdtTypeParamNames(adtDefinition));
        fieldType = _substitution.Apply(ConvertTypeWithAdditionalKindContext(ctorFieldType, ctorTypeVarEnv, ctorKindEnvByName));
        return true;
    }

    private Dictionary<string, Type> CreateClosedCaseTypeVarEnv(
        AdtDef root,
        IReadOnlyList<CaseTypeDef> casePath,
        IReadOnlyList<Type> typeArgs,
        IReadOnlyList<GenericValueArgument> valueArgs,
        IReadOnlyList<GenericEffectArgument> effectArgs)
    {
        var typeVarEnv = new Dictionary<string, Type>(StringComparer.Ordinal);
        var parameters = root.TypeParams
            .Concat(casePath.SelectMany(static caseType => caseType.TypeParams))
            .ToArray();
        var typeArgumentIndex = 0;
        foreach (var parameter in parameters)
        {
            if (parameter.ParameterKind != GenericParameterKind.Type)
            {
                continue;
            }

            typeVarEnv[parameter.Name] = typeArgumentIndex < typeArgs.Count
                ? typeArgs[typeArgumentIndex++]
                : _substitution.FreshTypeVariable();
        }

        var scopedValueArguments = new Dictionary<SymbolId, GenericValueArgument>();
        var valueArgumentsByParameterIndex = valueArgs.ToDictionary(static argument => argument.ParameterIndex);
        var effectArgumentsByParameterIndex = effectArgs.ToDictionary(static argument => argument.ParameterIndex);
        for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
        {
            var parameter = parameters[parameterIndex];
            if (parameter.ParameterKind == GenericParameterKind.Value &&
                parameter.SymbolId.IsValid &&
                valueArgumentsByParameterIndex.TryGetValue(parameterIndex, out var valueArgument))
            {
                scopedValueArguments[parameter.SymbolId] = valueArgument;
            }
            else if (parameter.ParameterKind == GenericParameterKind.EffectRow &&
                     effectArgumentsByParameterIndex.TryGetValue(parameterIndex, out var effectArgument))
            {
                typeVarEnv[parameter.Name] = effectArgument.Argument;
            }
        }

        if (scopedValueArguments.Count > 0)
        {
            _valueGenericArgumentsByTypeEnv[typeVarEnv] = scopedValueArguments;
        }

        return typeVarEnv;
    }

    private SymbolId GetClosedCaseRoot(SymbolId symbolId)
    {
        var current = symbolId;
        var visited = new HashSet<SymbolId>();
        while (current.IsValid && visited.Add(current) &&
               _symbolTable.GetSymbol<AdtSymbol>(current) is { ParentAdt.IsValid: true } caseSymbol)
        {
            current = caseSymbol.ParentAdt;
        }

        return current;
    }

    /// <summary>
    /// 尝试将 RawPtr 上的裸点访问解析为 CStruct 字段 getter。
    /// 成功时设置 method 的 CStructGetterName/CStructGetterSymbolId 和 SymbolId。
    /// </summary>
    private bool TryInferCStructFieldAccess(MethodCallExpr method, out Type fieldType)
    {
        fieldType = BaseTypes.Unit;
        if (method.Receiver == null)
        {
            return false;
        }

        var receiverType = _substitution.Apply(InferExpression(method.Receiver));
        var unwrappedType = UnwrapReadableReferenceType(receiverType);

        if (!IsRawPtrType(unwrappedType))
        {
            return false;
        }

        var fieldName = method.MethodName;
        var matches = FindCStructFieldMatches(fieldName);

        if (matches.Count == 0)
        {
            return false;
        }

        if (matches.Count > 1)
        {
            var structNames = matches.Select(m => m.StructName).OrderBy(n => n);
            AddError(
                method.Span,
                DiagnosticMessages.AmbiguousCStructFieldAccess(
                    fieldName,
                    string.Join(", ", structNames),
                    matches[0].GetterName));
            return false;
        }

        var match = matches[0];
        var getterSymbol = FindCStructGetterSymbol(match.GetterName);
        if (getterSymbol == null)
        {
            return false;
        }

        method.MarkResolvedAsCStructAccess(match.GetterName, getterSymbol.Id);
        method.SymbolId = getterSymbol.Id;
        fieldType = ResolveCStructAccessorFieldType(getterSymbol);
        return true;
    }

    private Type ResolveCStructAccessorFieldType(FuncSymbol getterSymbol)
    {
        return getterSymbol.CStructFieldTypeId.Value switch
        {
            BaseTypes.IntId => BaseTypes.Int,
            BaseTypes.FloatId => BaseTypes.Float,
            BaseTypes.BoolId => BaseTypes.Bool,
            BaseTypes.StringId => BaseTypes.String,
            BaseTypes.CharId => BaseTypes.Char,
            BaseTypes.UnitId => BaseTypes.Unit,
            BaseTypes.RawPtrId => new TyCon
            {
                Name = WellKnownStrings.BuiltinTypes.RawPtr,
                Id = new TypeId(BaseTypes.RawPtrId)
            },
            BaseTypes.CfnId => BaseTypes.Cfn,
            BaseTypes.NeverId => BaseTypes.Never,
            _ => new TyCon
            {
                Name = getterSymbol.CStructFieldTypeId.ToString(),
                Id = getterSymbol.CStructFieldTypeId
            }
        };
    }

    private static bool IsRawPtrType(Type type)
    {
        return type is TyCon { Name: WellKnownStrings.BuiltinTypes.RawPtr };
    }

    private List<(string StructName, string GetterName)> FindCStructFieldMatches(string fieldName)
    {
        var results = new List<(string StructName, string GetterName)>();

        foreach (var symbol in _symbolTable.Symbols.Values)
        {
            if (symbol is not AdtSymbol { IsCStruct: true, CStructLayoutInfo: { } layout })
            {
                continue;
            }

            if (layout.FindField(fieldName) != null)
            {
                var getterName = $"{layout.StructName.ToLowerInvariant()}_{fieldName}";
                results.Add((layout.StructName, getterName));
            }
        }

        return results;
    }

    private FuncSymbol? FindCStructGetterSymbol(string getterName)
    {
        foreach (var symbol in _symbolTable.Symbols.Values)
        {
            if (symbol is FuncSymbol { IsCStructAccessor: true, IsCStructGetter: true } func &&
                string.Equals(func.Name, getterName, StringComparison.Ordinal))
            {
                return func;
            }
        }

        return null;
    }

    private Dictionary<string, Type> CreateAdtTypeVarEnv(
        AdtDef adtDefinition,
        IReadOnlyList<Type> typeArgs,
        IReadOnlyList<GenericValueArgument> valueArgs,
        IReadOnlyList<GenericEffectArgument>? effectArgs = null)
    {
        var typeVarEnv = new Dictionary<string, Type>(StringComparer.Ordinal);
        var typeParamNames = GetAdtTypeParamNames(adtDefinition);
        var count = Math.Min(typeParamNames.Count, typeArgs.Count);
        for (var i = 0; i < count; i++)
        {
            typeVarEnv[typeParamNames[i]] = typeArgs[i];
        }

        var scopedValueArguments = new Dictionary<SymbolId, GenericValueArgument>();
        var valueArgumentsByParameterIndex = valueArgs.ToDictionary(static argument => argument.ParameterIndex);
        var effectArgumentsByParameterIndex = (effectArgs ?? [])
            .ToDictionary(static argument => argument.ParameterIndex);
        for (var parameterIndex = 0; parameterIndex < adtDefinition.TypeParams.Count; parameterIndex++)
        {
            var parameter = adtDefinition.TypeParams[parameterIndex];
            if (parameter.ParameterKind == GenericParameterKind.Value &&
                valueArgumentsByParameterIndex.TryGetValue(parameterIndex, out var valueArgument))
            {
                scopedValueArguments[parameter.SymbolId] = valueArgument;
            }
            else if (parameter.ParameterKind == GenericParameterKind.EffectRow &&
                     effectArgumentsByParameterIndex.TryGetValue(parameterIndex, out var effectArgument))
            {
                typeVarEnv[parameter.Name] = effectArgument.Argument;
            }
        }

        if (scopedValueArguments.Count > 0)
        {
            _valueGenericArgumentsByTypeEnv[typeVarEnv] = scopedValueArguments;
        }

        return typeVarEnv;
    }

    private SymbolId TryResolveAdtFieldSymbolId(SymbolId adtSymbolId, string fieldName)
    {
        if (_symbolTable.GetSymbol<AdtSymbol>(adtSymbolId) is not { } adtSymbol)
        {
            return SymbolId.None;
        }

        foreach (var symbolId in adtSymbol.Fields)
        {
            if (_symbolTable.GetSymbol<FieldSymbol>(symbolId) is { Name: { Length: > 0 } name } &&
                string.Equals(name, fieldName, StringComparison.Ordinal))
            {
                return symbolId;
            }
        }

        return SymbolId.None;
    }

    private Type InferListPattern(ListPattern listPattern, Type? expectedType = null)
    {
        Type elementType = _substitution.FreshTypeVariable();
        Type listType = new TyCon { Name = WellKnownStrings.BuiltinTypes.Seq, Args = [elementType] };
        var hasRecovery = false;

        if (expectedType != null)
        {
            listType = TryUnify(expectedType, listType, listPattern.Span, DiagnosticMessages.ListPatternExpectedTypeMismatch);
            if (ContainsErrorRecoveryType(listType))
            {
                elementType = CreateErrorRecoveryType();
                hasRecovery = true;
            }
        }

        var resolvedListType = _substitution.Apply(listType);
        if (resolvedListType is TyCon { Name: WellKnownStrings.BuiltinTypes.Seq, Args.Count: > 0 } resolvedListCon)
        {
            elementType = resolvedListCon.Args[0];
        }

        foreach (var element in listPattern.Elements)
        {
            var elementPatternType = InferPattern(element, elementType);
            hasRecovery |= ContainsErrorRecoveryType(elementPatternType);
            elementType = TryUnify(
                elementType,
                elementPatternType,
                element.Span,
                DiagnosticMessages.ListPatternElementTypeMismatch);
            hasRecovery |= ContainsErrorRecoveryType(elementType);
        }

        foreach (var element in listPattern.SuffixElements)
        {
            var elementPatternType = InferPattern(element, elementType);
            hasRecovery |= ContainsErrorRecoveryType(elementPatternType);
            elementType = TryUnify(
                elementType,
                elementPatternType,
                element.Span,
                DiagnosticMessages.ListPatternElementTypeMismatch);
            hasRecovery |= ContainsErrorRecoveryType(elementType);
        }

        if (listPattern.HasRestMarker && listPattern.RestPattern != null)
        {
            Type expectedRestType = new TyCon
            {
                Name = WellKnownStrings.BuiltinTypes.Seq,
                Args = [elementType]
            };
            var restType = InferPattern(listPattern.RestPattern, expectedRestType);
            hasRecovery |= ContainsErrorRecoveryType(restType);
            listType = TryUnify(
                listType,
                restType,
                listPattern.RestPattern.Span,
                DiagnosticMessages.ListPatternRestBindingTypeMismatch);
            hasRecovery |= ContainsErrorRecoveryType(listType);
        }

        var resolved = hasRecovery
            ? CreateErrorRecoveryType()
            : _substitution.Apply(listType);
        listPattern.InferredType = resolved;
        return resolved;
    }

    private Type InferIndex(IndexExpr index)
    {
        if (TryInferTypeApplicationIndex(index, out var explicitTypeApplication))
        {
            return explicitTypeApplication;
        }

        if (index.Object == null)
        {
            return CreateMissingShapeRecoveryType(index.Span, DiagnosticMessages.MissingIndexedObject);
        }

        if (index.Index == null)
        {
            if (!index.IsRecoveredMissingIndex && index.Object != null)
            {
                return SafeInferExpression(index.Object);
            }

            if (index.Object != null)
            {
                _ = SafeInferExpression(index.Object);
            }

            return CreateMissingShapeRecoveryType(index.Span, DiagnosticMessages.MissingIndexExpression);
        }

        var objectType = SafeInferExpression(index.Object);
        var indexType = SafeInferExpression(index.Index);

        var unifiedIndexType = TryUnify(BaseTypes.Int, indexType, index.Index.Span, DiagnosticMessages.IndexExpressionMustBeInt);
        objectType = UnwrapReadableReferenceType(objectType);

        var elementType = _substitution.FreshTypeVariable();
        var expectedListType = new TyCon { Name = WellKnownStrings.BuiltinTypes.Seq, Args = [elementType] };
        var unifiedObjectType = TryUnify(expectedListType, objectType, index.Object.Span, DiagnosticMessages.IndexedObjectMustBeList);
        if (ContainsErrorRecoveryType(unifiedIndexType) || ContainsErrorRecoveryType(unifiedObjectType))
        {
            return CreateErrorRecoveryType();
        }

        return _substitution.Apply(elementType);
    }

    private Type UnwrapReadableReferenceType(Type type)
    {
        var current = _substitution.Apply(type);
        while (true)
        {
            switch (current)
            {
                case TyVar { Instance: not null } tyVar:
                    current = _substitution.Apply(tyVar.Instance);
                    continue;

                case TyRef reference:
                    current = _substitution.Apply(reference.Inner);
                    continue;

                case TyMutRef mutReference:
                    current = _substitution.Apply(mutReference.Inner);
                    continue;

                default:
                    return current;
            }
        }
    }

    private bool TryInferTypeApplicationIndex(IndexExpr index, out Type inferredType)
    {
        inferredType = _substitution.FreshTypeVariable();
        if (!index.IsTypeApplication)
        {
            return false;
        }

        if (index.Object == null)
        {
            AddError(index.Span, DiagnosticMessages.ExplicitTypeApplicationMissingTarget);
            inferredType = CreateErrorRecoveryType();
            return true;
        }

        if (index.Index != null)
        {
            AddError(index.Span, DiagnosticMessages.ExplicitTypeApplicationCannotMixWithIndexExpression);
            _ = SafeInferExpression(index.Object);
            _ = SafeInferExpression(index.Index);
            inferredType = CreateErrorRecoveryType();
            return true;
        }

        var scheme = LookupTypeSchemeForExplicitTypeApplication(index.Object);
        if (scheme == null)
        {
            AddError(index.Span, DiagnosticMessages.ExplicitTypeApplicationRequiresNamedPolymorphicValue);
            _ = SafeInferExpression(index.Object);
            inferredType = CreateErrorRecoveryType();
            return true;
        }

        var targetSymbolId = index.Object switch
        {
            IdentifierExpr identifier => identifier.SymbolId,
            PathExpr path => path.SymbolId,
            _ => SymbolId.None
        };
        inferredType = targetSymbolId.IsValid &&
                       (index.GenericArguments.Count > 0 || HasValueGenericParameters(targetSymbolId))
            ? InstantiateSchemeWithGenericArgumentsAndConstraints(
                scheme,
                index.GenericArguments,
                targetSymbolId,
                index,
                index.Span)
            : InstantiateSchemeWithExplicitTypeArgsAndConstraints(scheme, index.TypeArgs, index.Span);
        return true;
    }

    private TypeScheme? LookupTypeSchemeForExplicitTypeApplication(EidosAstNode target)
    {
        return target switch
        {
            IdentifierExpr identifier when identifier.SymbolId.IsValid => _env.Lookup(identifier.SymbolId),
            PathExpr path when path.SymbolId.IsValid => _env.Lookup(path.SymbolId),
            _ => null
        };
    }

    private Type InferLoop(LoopExpr loop)
    {
        var hasRecovery = false;
        if (loop.Body == null)
        {
            return CreateMissingShapeRecoveryType(loop.Span, DiagnosticMessages.LoopExpressionRequiresBody);
        }

        _loopDepth++;
        try
        {
            var bodyType = SafeInferExpression(loop.Body);
            hasRecovery = ContainsErrorRecoveryType(bodyType);
        }
        finally
        {
            _loopDepth--;
        }

        return hasRecovery
            ? CreateErrorRecoveryType()
            : BaseTypes.Unit;
    }

    private Type InferListComprehension(ListComprehension comp)
    {
        var savedEnv = _env;
        var hasRecovery = false;

        try
        {
            foreach (var qualifier in comp.Qualifiers)
            {
                switch (qualifier.Kind)
                {
                    case QualifierKind.Generator:
                        {
                            Type elementType = _substitution.FreshTypeVariable();
                            if (qualifier.GeneratorExpression != null)
                            {
                                var sourceType = SafeInferExpression(qualifier.GeneratorExpression);
                                var expectedSourceType = new TyCon
                                {
                                    Name = WellKnownStrings.BuiltinTypes.Seq,
                                    Args = [elementType]
                                };
                                var sourceResult = TryUnify(expectedSourceType, sourceType, qualifier.GeneratorExpression.Span, DiagnosticMessages.ListComprehensionGeneratorMustIterateList);
                                hasRecovery |= ContainsErrorRecoveryType(sourceResult);
                            }
                            else
                            {
                                hasRecovery |= ReportMissingShape(qualifier.Span, DiagnosticMessages.ListComprehensionGeneratorRequiresSourceExpression);
                            }

                            if (qualifier.GeneratorPattern != null)
                            {
                                var patternType = InferPattern(qualifier.GeneratorPattern);
                                var patternResult = TryUnify(patternType, elementType, qualifier.GeneratorPattern.Span, DiagnosticMessages.ListComprehensionGeneratorPatternTypeMismatch);
                                hasRecovery |= ContainsErrorRecoveryType(patternType) ||
                                               ContainsErrorRecoveryType(patternResult);
                            }
                            else
                            {
                                hasRecovery |= ReportMissingShape(qualifier.Span, DiagnosticMessages.ListComprehensionGeneratorRequiresPattern);
                            }

                            break;
                        }
                    case QualifierKind.Guard:
                        if (qualifier.GuardExpression != null)
                        {
                            var guardType = SafeInferExpression(qualifier.GuardExpression);
                            var guardResult = TryUnify(BaseTypes.Bool, guardType, qualifier.GuardExpression.Span, DiagnosticMessages.ListComprehensionGuardMustBeBool);
                            hasRecovery |= ContainsErrorRecoveryType(guardResult);
                        }
                        else
                        {
                            hasRecovery |= ReportMissingShape(qualifier.Span, DiagnosticMessages.ListComprehensionGuardRequiresExpression);
                        }

                        break;
                    default:
                        AddError(qualifier.Span, DiagnosticMessages.UnsupportedListComprehensionQualifierKind(qualifier.Kind));
                        hasRecovery = true;
                        break;
                }
            }

            Type outputType;
            if (comp.Output != null)
            {
                outputType = SafeInferExpression(comp.Output);
            }
            else
            {
                outputType = CreateMissingShapeRecoveryType(comp.Span, DiagnosticMessages.ListComprehensionRequiresOutputExpression);
            }

            if (hasRecovery || ContainsErrorRecoveryType(outputType))
            {
                return CreateErrorRecoveryType();
            }

            return new TyCon
            {
                Name = WellKnownStrings.BuiltinTypes.Seq,
                Args = [_substitution.Apply(outputType)]
            };
        }
        finally
        {
            _env = savedEnv;
        }
    }

    private void UpdateEffectOperationSymbolSignature(
        SymbolId operationSymbolId,
        IReadOnlyList<Type> argumentTypes,
        Type returnType)
    {
        if (!operationSymbolId.IsValid ||
            _symbolTable.GetSymbol<FuncSymbol>(operationSymbolId) is not { } functionSymbol)
        {
            return;
        }

        var parameterTypeIds = argumentTypes
            .Select(ResolveSymbolMetadataTypeId)
            .ToList();
        var returnTypeId = ResolveSymbolMetadataTypeId(returnType);

        _symbolTable.UpdateSymbol(functionSymbol with
        {
            ParamTypes = parameterTypeIds.Count > 0 ? parameterTypeIds : functionSymbol.ParamTypes,
            ReturnType = returnTypeId.IsValid ? returnTypeId : functionSymbol.ReturnType
        });
    }

    private TypeId ResolveSymbolMetadataTypeId(Type type)
    {
        var resolvedType = _substitution.Apply(type);
        while (resolvedType is TyVar { Instance: { } instance })
        {
            resolvedType = _substitution.Apply(instance);
        }

        return resolvedType.Id;
    }

    private Type InferFunctionType(FuncDef funcDef)
    {
        return InferFunctionSignatureType(funcDef.Signature, funcDef.TypeParams, requiredAbilities: funcDef.RequiredAbilities);
    }

    private TypeScheme InferFunctionSignatureScheme(FuncDef funcDef)
    {
        if (funcDef.Signature.Count == 0)
        {
            return _env.Generalize(BaseTypes.Unit);
        }

        var typeVarEnv = new Dictionary<string, Type>(StringComparer.Ordinal);
        var kindEnvByName = CreateTypeParamKindMap(funcDef.TypeParams);
        var kindEnvByTypeVar = new Dictionary<int, Kind>();

        RegisterSignatureTypeParams(funcDef.TypeParams, kindEnvByName, typeVarEnv, kindEnvByTypeVar);
        ResolveValueGenericParameterTypes(funcDef.TypeParams, typeVarEnv);

        _typeParamKindStack.Push(kindEnvByName);
        _typeParamVarKindStack.Push(kindEnvByTypeVar);
        try
        {
            var signatureConstraintGenerator = new ConstraintGenerator(_symbolTable, _substitution);
            foreach (var typeParam in funcDef.TypeParams)
            {
                if (!typeVarEnv.TryGetValue(typeParam.Name, out var typeVar))
                {
                    continue;
                }

                signatureConstraintGenerator.CollectTypeParamConstraints(
                    typeParam,
                    typeVar,
                    typeNode => ConvertType(typeNode, typeVarEnv, allowTypeConstructorReference: true));
            }

            var functionType = ConvertFunctionSignatureType(funcDef.Signature, typeVarEnv);
            if (funcDef.Body.Count == 0 && functionType is TyFun function)
            {
                functionType = StripLeadingUnitParams(function);
            }

            functionType = ApplyRequiredAbilitiesToFunction(
                functionType,
                ResolveRequiredAbilities(funcDef.RequiredAbilities ?? [], typeVarEnv));

            return _env.Generalize(functionType, signatureConstraintGenerator.Constraints.Constraints.ToList());
        }
        finally
        {
            _typeParamVarKindStack.Pop();
            _typeParamKindStack.Pop();
        }
    }

    private Type InferFunctionSignatureType(
        IReadOnlyList<TypeNode> signature,
        IReadOnlyList<TypeParam> typeParams,
        IReadOnlyList<TypeParam>? outerTypeParams = null,
        IReadOnlyDictionary<string, Kind>? outerKindEnvByName = null,
        IReadOnlyList<EffectRequirementNode>? requiredAbilities = null,
        Type? selfType = null)
    {
        if (signature.Count == 0)
        {
            return BaseTypes.Unit;
        }

        var typeVarEnv = new Dictionary<string, Type>(StringComparer.Ordinal);
        var kindEnvByName = outerKindEnvByName == null
            ? []
            : new Dictionary<string, Kind>(outerKindEnvByName, StringComparer.Ordinal);
        foreach (var pair in CreateTypeParamKindMap(typeParams))
        {
            kindEnvByName[pair.Key] = pair.Value;
        }

        var kindEnvByTypeVar = new Dictionary<int, Kind>();

        RegisterSignatureTypeParams(outerTypeParams ?? [], kindEnvByName, typeVarEnv, kindEnvByTypeVar);
        RegisterSignatureTypeParams(typeParams, kindEnvByName, typeVarEnv, kindEnvByTypeVar);
        ResolveValueGenericParameterTypes(outerTypeParams ?? [], typeVarEnv);
        ResolveValueGenericParameterTypes(typeParams, typeVarEnv);
        if (selfType != null)
        {
            typeVarEnv[WellKnownStrings.Keywords.Self] = selfType;
        }

        _typeParamKindStack.Push(kindEnvByName);
        _typeParamVarKindStack.Push(kindEnvByTypeVar);
        try
        {
            var functionType = ConvertFunctionSignatureType(signature, typeVarEnv);
            return ApplyRequiredAbilitiesToFunction(
                functionType,
                ResolveRequiredAbilities(requiredAbilities ?? [], typeVarEnv));
        }
        finally
        {
            _typeParamVarKindStack.Pop();
            _typeParamKindStack.Pop();
        }
    }

    private Type ConvertFunctionSignatureType(
        IReadOnlyList<TypeNode> signature,
        Dictionary<string, Type> typeVarEnv)
    {
        if (signature.Count == 1)
        {
            return ConvertType(signature[0], typeVarEnv);
        }

        var paramTypes = signature.Take(signature.Count - 1)
            .Select(typeNode => ConvertType(typeNode, typeVarEnv))
            .ToList();
        var returnType = ConvertType(signature[^1], typeVarEnv);

        return new TyFun
        {
            Params = paramTypes,
            Result = returnType
        };
    }

    private void RegisterSignatureTypeParams(
        IReadOnlyList<TypeParam> typeParams,
        IReadOnlyDictionary<string, Kind> kindEnvByName,
        Dictionary<string, Type> typeVarEnv,
        Dictionary<int, Kind> kindEnvByTypeVar)
    {
        foreach (var typeParam in typeParams)
        {
            if (string.IsNullOrWhiteSpace(typeParam.Name) ||
                typeVarEnv.ContainsKey(typeParam.Name))
            {
                continue;
            }

            var typeVar = _substitution.FreshTypeVariable();
            typeVarEnv[typeParam.Name] = typeVar;
            if (typeVar is TyVar typeVariable &&
                kindEnvByName.TryGetValue(typeParam.Name, out var typeParamKind))
            {
                kindEnvByTypeVar[typeVariable.Index] = typeParamKind;
            }
        }
    }

    private static List<string> SplitQualifiedName(string name)
    {
        return name
            .Replace(WellKnownStrings.Separators.ModulePath, WellKnownStrings.Separators.Path, StringComparison.Ordinal)
            .Split(WellKnownStrings.Separators.Path, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
    }
}
