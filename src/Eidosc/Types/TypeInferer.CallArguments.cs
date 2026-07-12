using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
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
                currentType = ApplyCallArgument(call, currentType, argTypes[i], argSpans[i]);
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
                currentType = ApplyCallArgument(call, currentType, argTypes[i], argSpans[i]);
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
                currentType = ApplyCallArgument(call, currentType, argTypes[i], argSpans[i]);
            }
        }

        ResolveAccumulatedCallEffects(call);
        return _substitution.Apply(currentType);
    }

    private static bool IsLowerIdentifierName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && char.IsLower(name[0]);
    }

    private static bool IsPrecompiledSymbol(FuncSymbol symbol)
    {
        var filePath = symbol.Span.FilePath;
        return !string.IsNullOrWhiteSpace(filePath) &&
               filePath.Replace('\\', '/').Contains("/Stdlib/Precompiled/", StringComparison.Ordinal);
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
}
