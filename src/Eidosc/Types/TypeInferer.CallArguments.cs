using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private bool TryInferCandidateApplicationSpine(CallExpr outerCall, out Type resultType)
    {
        resultType = CreateErrorRecoveryType();
        var calls = new List<(CallExpr Call, MethodCallExpr? StaticMethodOrigin)>();
        EidosAstNode? root = outerCall;
        while (true)
        {
            CallExpr call;
            MethodCallExpr? staticMethodOrigin = null;
            if (root is CallExpr directCall)
            {
                call = directCall;
            }
            else if (root is MethodCallExpr
                     {
                         ResolvedAsStaticPath: true,
                         ResolvedStaticExpression: null
                     } staticMethod)
            {
                call = staticMethod.ToDesugaredCall();
                staticMethodOrigin = staticMethod;
            }
            else
            {
                break;
            }

            if (call.PositionalArgs.Count == 0 && call.NamedArgs.Count == 0)
            {
                return false;
            }

            calls.Add((call, staticMethodOrigin));
            root = call.Function;
        }

        calls.Reverse();
        var (callableName, candidateIds, resolvedSymbolId) = root switch
        {
            IdentifierExpr identifier =>
                (identifier.Name, (IEnumerable<SymbolId>)identifier.ValueCandidateSymbolIds, identifier.SymbolId),
            PathExpr path =>
                (path.Name, (IEnumerable<SymbolId>)path.ValueCandidateSymbolIds, path.SymbolId),
            _ => (string.Empty, [], SymbolId.None)
        };

        var candidates = GetTypeDirectedCallableCandidates(callableName, candidateIds, resolvedSymbolId);
        if (string.IsNullOrWhiteSpace(callableName) || candidates.Count <= 1)
        {
            return false;
        }

        var argumentExpressions = new List<EidosAstNode?>();
        var argumentSpans = new List<SourceSpan>();
        var argumentTypes = new List<Type>();
        foreach (var segment in calls)
        {
            var call = segment.Call;
            foreach (var argument in call.PositionalArgs)
            {
                argumentExpressions.Add(argument);
                argumentSpans.Add(argument.Span);
                argumentTypes.Add(argument is LambdaExpr lambda
                    ? CreateLambdaShape(lambda)
                    : SafeInferExpression(argument));
            }

            foreach (var argument in call.NamedArgs)
            {
                argumentExpressions.Add(argument.Value);
                argumentSpans.Add(argument.Value?.Span ?? argument.Span);
                if (argument.Value == null)
                {
                    ReportMissingNamedArgumentValue(argument);
                    argumentTypes.Add(CreateErrorRecoveryType());
                }
                else
                {
                    argumentTypes.Add(argument.Value is LambdaExpr lambda
                        ? CreateLambdaShape(lambda)
                        : SafeInferExpression(argument.Value));
                }
            }
        }

        if (!TryResolveTypeDirectedMethodCandidate(candidates, argumentTypes, out var resolution))
        {
            ReportCallableResolutionFailure(
                outerCall.Span,
                callableName,
                "application-spine",
                resolution,
                argumentTypes,
                DiagnosticMessages.NoImportedOverloadAcceptsArgumentTypes(callableName));
            return true;
        }

        switch (root)
        {
            case IdentifierExpr identifier:
                identifier.SymbolId = resolution.SelectedSymbolId;
                break;
            case PathExpr path:
                path.SymbolId = resolution.SelectedSymbolId;
                break;
        }

        var currentType = InferFunctionSymbolType(resolution.SelectedSymbolId, root!.Span);
        root.InferredType = _substitution.Apply(currentType);
        var argumentIndex = 0;
        foreach (var segment in calls)
        {
            var call = segment.Call;
            call.InferredEffects = null;
            call.ClearEmptyCallResolution();
            var callArgumentCount = call.PositionalArgs.Count + call.NamedArgs.Count;
            for (var localIndex = 0; localIndex < callArgumentCount; localIndex++, argumentIndex++)
            {
                var expression = argumentExpressions[argumentIndex];
                var argumentType = argumentTypes[argumentIndex];
                var resolvedFunction = _substitution.Apply(currentType);
                if (expression is LambdaExpr lambda &&
                    resolvedFunction is TyFun { Params.Count: > 0 } function)
                {
                    argumentType = InferExpressionWithExpectedType(lambda, function.Params[0]);
                    argumentTypes[argumentIndex] = argumentType;
                }

                if (localIndex < call.PositionalArgs.Count)
                {
                    argumentType = AutoAdjustCallArgumentIfNeeded(
                        call,
                        localIndex,
                        currentType,
                        argumentType);
                }

                currentType = ApplyCallArgument(
                    call,
                    currentType,
                    argumentType,
                    argumentSpans[argumentIndex]);
            }

            ResolveAccumulatedCallEffects(call);
            call.InferredType = _substitution.Apply(currentType);
            if (segment.StaticMethodOrigin is { } staticMethod)
            {
                SynchronizeStaticMethodApplication(staticMethod, call);
            }
        }

        ValidateResolvedValueGenericArguments(root, outerCall.Span);
        resultType = _substitution.Apply(currentType);
        return true;
    }

    private static void SynchronizeStaticMethodApplication(MethodCallExpr method, CallExpr desugared)
    {
        if (desugared.Function?.SymbolId is { IsValid: true } selectedCallable)
        {
            method.SymbolId = selectedCallable;
        }

        method.PositionalArgs.Clear();
        method.PositionalArgs.AddRange(desugared.PositionalArgs);
        method.NamedArgs.Clear();
        method.NamedArgs.AddRange(desugared.NamedArgs);
        method.InferredType = desugared.InferredType;
        method.InferredEffects = desugared.InferredEffects;
        if (desugared.SynthesizedUnitArgumentCount > 0)
        {
            method.MarkSyntheticUnitArguments(desugared.SynthesizedUnitArgumentCount);
        }
        else if (desugared.UsesFfiUnitArgumentElision)
        {
            method.MarkFfiUnitArgumentElision();
        }
        else
        {
            method.ClearEmptyCallResolution();
        }
    }

    private Type AutoAdjustCallArgumentIfNeeded(
        CallExpr call,
        int positionalIndex,
        Type currentFunctionType,
        Type argumentType)
    {
        if (_substitution.Apply(currentFunctionType) is not TyFun { Params.Count: > 0 } function)
        {
            return argumentType;
        }

        var parameterType = _substitution.Apply(function.Params[0]);
        var resolvedArgument = _substitution.Apply(argumentType);
        var originalArgument = call.PositionalArgs[positionalIndex];
        if (parameterType is TyRef parameterReference && resolvedArgument is not (TyRef or TyMutRef))
        {
            var borrowedInner = NormalizeClosedCaseArgumentForExpectedType(
                parameterReference.Inner,
                resolvedArgument,
                _substitution);
            var borrowedType = new TyRef { Inner = borrowedInner };
            var syntheticBorrow = new UnaryExpr();
            syntheticBorrow.SetOperator(UnaryOp.Ref);
            syntheticBorrow.SetOperand(originalArgument);
            syntheticBorrow.SetSpan(originalArgument.Span);
            syntheticBorrow.InferredType = borrowedType;
            call.PositionalArgs[positionalIndex] = syntheticBorrow;
            return borrowedType;
        }

        if (parameterType is TyRef or TyMutRef ||
            resolvedArgument is not (TyRef or TyMutRef))
        {
            return argumentType;
        }

        var innerType = resolvedArgument switch
        {
            TyRef reference => _substitution.Apply(reference.Inner),
            TyMutRef mutableReference => _substitution.Apply(mutableReference.Inner),
            _ => resolvedArgument
        };
        var syntheticDeref = new UnaryExpr();
        syntheticDeref.SetOperator(UnaryOp.Deref);
        syntheticDeref.SetOperand(originalArgument);
        syntheticDeref.SetSpan(originalArgument.Span);
        syntheticDeref.InferredType = innerType;
        call.PositionalArgs[positionalIndex] = syntheticDeref;
        return innerType;
    }

    private void ApplyEmptyCallResolution(CallExpr call, EmptyCallResolution resolution)
    {
        switch (resolution.Kind)
        {
            case EmptyCallResolutionKind.UnitSugar:
                call.MarkSyntheticUnitArguments(resolution.SynthesizedUnitArgumentCount);
                break;
            case EmptyCallResolutionKind.FfiUnitElision:
                call.MarkFfiUnitArgumentElision();
                break;
            default:
                call.ClearEmptyCallResolution();
                break;
        }
    }

    private bool IsExternalFfiCallee(EidosAstNode? callee)
    {
        var symbolId = callee switch
        {
            IdentifierExpr identifier => identifier.SymbolId,
            PathExpr path => path.SymbolId,
            _ => SymbolId.None
        };

        return symbolId.IsValid &&
               _symbolTable.GetSymbol<FuncSymbol>(symbolId) is { IsExternal: true };
    }

    private static bool IsUnitType(Type type) =>
        type is TyCon { Name: WellKnownStrings.BuiltinTypes.Unit or "()" };

    private Type InferCandidateIdentifierCall(CallExpr call, IdentifierExpr candidateIdentifier)
    {
        var argTypes = new List<Type>();
        var argSpans = new List<SourceSpan>();
        foreach (var arg in call.PositionalArgs)
        {
            argTypes.Add(SafeInferExpression(arg));
            argSpans.Add(arg.Span);
        }

        foreach (var arg in call.NamedArgs)
        {
            AddNamedArgumentType(arg, argTypes, argSpans);
        }

        var candidates = GetTypeDirectedCallableCandidates(
            candidateIdentifier.Name,
            candidateIdentifier.ValueCandidateSymbolIds,
            candidateIdentifier.SymbolId);

        if (!TryResolveTypeDirectedMethodCandidate(candidates, argTypes, out var resolution))
        {
            ReportCallableResolutionFailure(
                call.Span,
                candidateIdentifier.Name,
                "call",
                resolution,
                argTypes,
                DiagnosticMessages.NoImportedOverloadAcceptsArgumentTypes(candidateIdentifier.Name));
            return CreateErrorRecoveryType();
        }

        var selectedCandidate = resolution.SelectedSymbolId;
        candidateIdentifier.SymbolId = selectedCandidate;
        var currentType = InferFunctionSymbolType(selectedCandidate, candidateIdentifier.Span);
        if (argTypes.Count == 0 &&
            TryResolveEmptyCall(currentType, candidateIdentifier, _substitution, out var emptyResolution))
        {
            ApplyEmptyCallResolution(call, emptyResolution);
            AccumulateResolvedFunctionEffects(call, currentType);
            currentType = emptyResolution.ResultType;
        }
        else
        {
            call.ClearEmptyCallResolution();
            for (var i = 0; i < argTypes.Count; i++)
            {
                var argumentType = i < call.PositionalArgs.Count
                    ? AutoAdjustCallArgumentIfNeeded(call, i, currentType, argTypes[i])
                    : argTypes[i];
                currentType = ApplyCallArgument(call, currentType, argumentType, argSpans[i]);
            }
        }

        ResolveAccumulatedCallEffects(call);
        return _substitution.Apply(currentType);
    }

    private Type InferCandidatePathCall(CallExpr call, PathExpr candidatePath)
    {
        var argTypes = new List<Type>();
        var argSpans = new List<SourceSpan>();
        foreach (var arg in call.PositionalArgs)
        {
            argTypes.Add(SafeInferExpression(arg));
            argSpans.Add(arg.Span);
        }

        foreach (var arg in call.NamedArgs)
        {
            AddNamedArgumentType(arg, argTypes, argSpans);
        }

        var candidates = GetTypeDirectedCallableCandidates(
            candidatePath.Name,
            candidatePath.ValueCandidateSymbolIds,
            candidatePath.SymbolId);

        if (!TryResolveTypeDirectedMethodCandidate(candidates, argTypes, out var resolution))
        {
            ReportCallableResolutionFailure(
                call.Span,
                candidatePath.Name,
                "call",
                resolution,
                argTypes,
                DiagnosticMessages.NoImportedOverloadAcceptsArgumentTypes(candidatePath.Name));
            return CreateErrorRecoveryType();
        }

        var selectedCandidate = resolution.SelectedSymbolId;
        candidatePath.SymbolId = selectedCandidate;
        var currentType = InferFunctionSymbolType(selectedCandidate, candidatePath.Span);
        if (argTypes.Count == 0 &&
            TryResolveEmptyCall(currentType, candidatePath, _substitution, out var emptyResolution))
        {
            ApplyEmptyCallResolution(call, emptyResolution);
            AccumulateResolvedFunctionEffects(call, currentType);
            currentType = emptyResolution.ResultType;
        }
        else
        {
            call.ClearEmptyCallResolution();
            for (var i = 0; i < argTypes.Count; i++)
            {
                var argumentType = i < call.PositionalArgs.Count
                    ? AutoAdjustCallArgumentIfNeeded(call, i, currentType, argTypes[i])
                    : argTypes[i];
                currentType = ApplyCallArgument(call, currentType, argumentType, argSpans[i]);
            }
        }

        ResolveAccumulatedCallEffects(call);
        return _substitution.Apply(currentType);
    }

    private Type InferUnresolvedIdentifierCall(CallExpr call, IdentifierExpr unresolvedIdentifier)
    {
        var argTypes = new List<Type>();
        var argSpans = new List<SourceSpan>();
        foreach (var arg in call.PositionalArgs)
        {
            argTypes.Add(SafeInferExpression(arg));
            argSpans.Add(arg.Span);
        }

        foreach (var arg in call.NamedArgs)
        {
            AddNamedArgumentType(arg, argTypes, argSpans);
        }

        var candidates = GetTypeDirectedCallableCandidates(unresolvedIdentifier.Name);

        if (!TryResolveTypeDirectedMethodCandidate(candidates, argTypes, out var resolution))
        {
            ReportCallableResolutionFailure(
                call.Span,
                unresolvedIdentifier.Name,
                "call",
                resolution,
                argTypes,
                DiagnosticMessages.UndefinedFunction(unresolvedIdentifier.Name));
            return CreateErrorRecoveryType();
        }

        var selectedCandidate = resolution.SelectedSymbolId;
        unresolvedIdentifier.SymbolId = selectedCandidate;
        var currentType = InferFunctionSymbolType(selectedCandidate, unresolvedIdentifier.Span);
        if (argTypes.Count == 0 &&
            TryResolveEmptyCall(currentType, unresolvedIdentifier, _substitution, out var emptyResolution))
        {
            ApplyEmptyCallResolution(call, emptyResolution);
            AccumulateResolvedFunctionEffects(call, currentType);
            currentType = emptyResolution.ResultType;
        }
        else
        {
            call.ClearEmptyCallResolution();
            for (var i = 0; i < argTypes.Count; i++)
            {
                var argumentType = i < call.PositionalArgs.Count
                    ? AutoAdjustCallArgumentIfNeeded(call, i, currentType, argTypes[i])
                    : argTypes[i];
                currentType = ApplyCallArgument(call, currentType, argumentType, argSpans[i]);
            }
        }

        ResolveAccumulatedCallEffects(call);
        return _substitution.Apply(currentType);
    }

    private static bool IsPrecompiledSymbol(FuncSymbol symbol)
    {
        var filePath = symbol.Span.FilePath;
        return Eidosc.Semantic.PrecompiledModuleRegistry.IsStdlibSourcePath(filePath);
    }

    private bool InferNamedArgumentValues(IEnumerable<NamedArg> namedArgs)
    {
        var hasRecovery = false;
        foreach (var arg in namedArgs)
        {
            if (arg.Value != null)
            {
                var valueType = SafeInferExpression(arg.Value);
                hasRecovery |= ContainsErrorRecoveryType(valueType);
                continue;
            }

            ReportMissingNamedArgumentValue(arg);
            hasRecovery = true;
        }

        return hasRecovery;
    }

    private void AddNamedArgumentType(NamedArg arg, List<Type> argTypes, List<SourceSpan> argSpans)
    {
        if (arg.Value != null)
        {
            argTypes.Add(SafeInferExpression(arg.Value));
            argSpans.Add(arg.Value.Span);
            return;
        }

        ReportMissingNamedArgumentValue(arg);
        argTypes.Add(CreateErrorRecoveryType());
        argSpans.Add(arg.Span);
    }

    private void AddNamedArgument(NamedArg arg, List<EidosAstNode?> argumentExprs, List<SourceSpan> argSpans)
    {
        if (arg.Value != null)
        {
            argumentExprs.Add(arg.Value);
            argSpans.Add(arg.Value.Span);
            return;
        }

        ReportMissingNamedArgumentValue(arg);
        argumentExprs.Add(null);
        argSpans.Add(arg.Span);
    }

    private Type InferCallArgument(Type currentFunctionType, EidosAstNode? argumentExpr, SourceSpan argumentSpan)
    {
        if (argumentExpr == null)
        {
            return CreateErrorRecoveryType();
        }

        var resolvedFunctionType = _substitution.Apply(currentFunctionType);
        if (resolvedFunctionType is TyFun { Params.Count: > 0 } function)
        {
            if (_substitution.Apply(function.Params[0]) is TyRef &&
                argumentExpr is not UnaryExpr { Operator: UnaryOp.Ref or UnaryOp.MRef })
            {
                return SafeInferExpression(argumentExpr);
            }

            return InferExpressionWithExpectedType(argumentExpr, function.Params[0]);
        }

        return SafeInferExpression(argumentExpr);
    }

    private void ReportMissingNamedArgumentValue(NamedArg arg)
    {
        var name = string.IsNullOrWhiteSpace(arg.Name) ? "<missing>" : arg.Name;
        AddError(arg.Span, DiagnosticMessages.NamedArgumentRequiresValueExpression(name));
    }

    private Type ApplyFunctionArgument(Type functionType, Type argumentType, SourceSpan argumentSpan)
    {
        var resolvedFunctionType = _substitution.Apply(functionType);
        if (resolvedFunctionType is TyFun function && function.Params.Count > 0)
        {
            var parameterType = TryUnify(function.Params[0], argumentType, argumentSpan, DiagnosticMessages.CallArgumentTypeMismatch);
            if (ContainsErrorRecoveryType(parameterType))
            {
                return CreateErrorRecoveryType();
            }

            if (function.Params.Count == 1)
            {
                return _substitution.Apply(function.Result);
            }

            var remainingParams = new List<Type>(function.Params.Count - 1);
            for (var i = 1; i < function.Params.Count; i++)
            {
                remainingParams.Add(function.Params[i]);
            }

            return new TyFun
            {
                Params = remainingParams,
                Result = function.Result,
                Effects = function.Effects
            };
        }

        var nextResultType = _substitution.FreshTypeVariable();
        var expectedCallableType = new TyFun
        {
            Params = [argumentType],
            Result = nextResultType
        };
        var callableType = TryUnify(expectedCallableType, resolvedFunctionType, argumentSpan, DiagnosticMessages.CallTargetIsNotCallable);
        if (ContainsErrorRecoveryType(callableType))
        {
            return CreateErrorRecoveryType();
        }

        return _substitution.Apply(nextResultType);
    }

    private Type ApplyCallArgument(EidosAstNode call, Type functionType, Type argumentType, SourceSpan argumentSpan)
    {
        var resolvedFunction = _substitution.Apply(functionType) as TyFun;
        var result = ApplyFunctionArgument(functionType, argumentType, argumentSpan);
        if (resolvedFunction != null)
        {
            AccumulateCallEffects(call, (EffectRow)_substitution.Apply(resolvedFunction.Effects));
        }

        return result;
    }

    private void AccumulateResolvedFunctionEffects(EidosAstNode call, Type functionType)
    {
        if (_substitution.Apply(functionType) is TyFun function)
        {
            AccumulateCallEffects(call, (EffectRow)_substitution.Apply(function.Effects));
        }
    }

    private static void AccumulateCallEffects(EidosAstNode call, EffectRow effects)
    {
        if (effects.IsPure)
        {
            return;
        }

        call.InferredEffects = (call.InferredEffects ?? EffectRow.Pure).Union(effects);
    }

    private void ResolveAccumulatedCallEffects(EidosAstNode call)
    {
        if (call.InferredEffects != null)
        {
            call.InferredEffects = (EffectRow)_substitution.Apply(call.InferredEffects);
        }
    }

    private bool TryBuildCfnType(Type callbackType, out Type cfnType)
    {
        cfnType = BaseTypes.Cfn;
        var args = new List<Type>();
        var current = _substitution.Apply(callbackType);

        while (current is TyFun functionType)
        {
            foreach (var param in functionType.Params)
            {
                args.Add(_substitution.Apply(param));
            }
            current = _substitution.Apply(functionType.Result);
        }

        if (args.Count == 0)
        {
            return false;
        }

        args.Add(current);
        cfnType = new TyCon
        {
            Name = WellKnownStrings.BuiltinTypes.Cfn,
            Id = new TypeId(BaseTypes.CfnId),
            Args = args
        };
        return true;
    }

    private bool TryResolveSameNamedAdtConstructor(
        AdtSymbol adt,
        EidosAstNode? constructorTarget,
        out SymbolId constructorId)
    {
        constructorId = SymbolId.None;
        var targetName = constructorTarget switch
        {
            IdentifierExpr identifier => identifier.Name,
            PathExpr path => path.Name,
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return false;
        }

        constructorId = EnumerateAdtTreeConstructorIds(adt)
            .FirstOrDefault(candidate =>
                _symbolTable.GetSymbol<CtorSymbol>(candidate) is { } constructor &&
                string.Equals(constructor.Name, targetName, StringComparison.Ordinal));
        if (!constructorId.IsValid)
        {
            return false;
        }

        switch (constructorTarget)
        {
            case IdentifierExpr identifier:
                identifier.SymbolId = constructorId;
                identifier.IsConstructor = true;
                break;
            case PathExpr path:
                path.SymbolId = constructorId;
                path.SetIsConstructorPath(true);
                path.SetIsTypePath(false);
                break;
        }

        return true;
    }

    private IEnumerable<SymbolId> EnumerateAdtTreeConstructorIds(AdtSymbol root)
    {
        var pending = new Stack<AdtSymbol>();
        var visited = new HashSet<SymbolId>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var adt = pending.Pop();
            if (!visited.Add(adt.Id))
            {
                continue;
            }

            if (adt.CaseConstructor.IsValid)
            {
                yield return adt.CaseConstructor;
            }

            foreach (var constructor in adt.Constructors)
            {
                if (constructor.IsValid)
                {
                    yield return constructor;
                }
            }

            for (var index = adt.DirectCases.Count - 1; index >= 0; index--)
            {
                if (_symbolTable.GetSymbol<AdtSymbol>(adt.DirectCases[index]) is { } child)
                {
                    pending.Push(child);
                }
            }
        }
    }
}
