using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

// Expression lowering: tuple, list, handler, field/index access
public sealed partial class MirBuilder
{


    private bool ContainsOpenTypeVariable(TypeId typeId)
    {
        return MirGenericAnalysis.ContainsOpenTypeVariable(typeId, _typeDescriptorsById, _dynamicTypeKeysById);
    }

    private TypeId ResolveCallResultType(HirCall call, MirOperand functionOperand)
    {
        if (TryResolveFunctionReturnType(functionOperand, out var functionReturnType) &&
            functionReturnType.Value == BaseTypes.NeverId)
        {
            return functionReturnType;
        }

        if (call.TypeId.IsValid)
        {
            return call.TypeId;
        }

        if (functionReturnType.IsValid)
        {
            return functionReturnType;
        }

        return TypeId.None;
    }

    private bool TryResolveFunctionReturnType(MirOperand functionOperand, out TypeId returnType)
    {
        if (functionOperand is MirFunctionRef functionRef)
        {
            if (functionRef.SymbolId.IsValid &&
                _functionReturnTypesBySymbol.TryGetValue(functionRef.SymbolId, out var bySymbol) &&
                bySymbol.IsValid)
            {
                returnType = bySymbol;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(functionRef.Name) &&
                _functionReturnTypesByName.TryGetValue(functionRef.Name, out var byName) &&
                byName.IsValid)
            {
                returnType = byName;
                return true;
            }

            if (functionRef.TypeId.IsValid)
            {
                returnType = functionRef.TypeId;
                return true;
            }
        }

        returnType = TypeId.None;
        return false;
    }

    private TypeId ResolveCurrentFunctionReturnType()
    {
        return _currentFunc is { ReturnType: var returnType } && returnType.IsValid
            ? returnType
            : TypeId.None;
    }

    private MirOperand ConvertLambda(HirLambda lambda)
    {
        var lambdaName = $"{WellKnownStrings.InternalNames.LambdaPrefix}{_nextLambdaId++}";
        var lambdaFunctionId = BuildGeneratedFunctionId(
            lambda.SymbolId,
            lambdaName,
            ResolveSymbolKind(lambda.SymbolId),
            MirSyntheticFunctions.LambdaRole);
        if (lambda.Captures.Count > 0)
        {
            var closureValue = ConvertCapturedLambda(lambda, lambdaName, lambdaFunctionId);
            if (closureValue != null)
            {
                return closureValue;
            }

            return CreatePoisonOperand(lambda.TypeId, lambda.Span, DiagnosticMessages.FailedCapturedLambdaLoweringReason);
        }

        var lambdaFunc = ConvertLambdaToFunction(lambda, lambdaName, lambdaFunctionId);
        _generatedLambdaFunctions.Add(lambdaFunc);

        return new MirFunctionRef
        {
            Name = lambdaName,
            SymbolId = lambda.SymbolId,
            SymbolKind = ResolveSymbolKind(lambda.SymbolId),
            FunctionId = lambdaFunctionId,
            Span = lambda.Span,
            TypeId = lambda.TypeId
        };
    }

    private MirOperand? ConvertCapturedLambda(HirLambda lambda, string lambdaName, FunctionId lambdaFunctionId)
    {
        var captureParameters = new List<HirParam>(lambda.Captures.Count);
        var captureArguments = new List<MirOperand>(lambda.Captures.Count);
        RecursiveClosureBindingContext? recursiveBinding = null;

        foreach (var capture in lambda.Captures)
        {
            if (TryMatchRecursiveClosureSelfCapture(capture, out var matchedBinding))
            {
                recursiveBinding ??= matchedBinding;
                continue;
            }

            if (!TryCreateClosureCaptureBinding(capture, lambda.Span, out var parameter, out var argument))
            {
                return null;
            }

            captureParameters.Add(parameter);
            captureArguments.Add(argument);
        }

        var loweredLambda = lambda with
        {
            Parameters = [.. captureParameters, .. lambda.Parameters],
            Captures = []
        };

        var recursiveContext = recursiveBinding == null
            ? null
            : new RecursiveClosureBodyContext(
                [new RecursiveClosureBodyBinding(
                    recursiveBinding.Name,
                    recursiveBinding.SymbolId,
                    recursiveBinding.TypeId,
                    lambdaName,
                    lambda.SymbolId,
                    lambdaFunctionId)],
                captureParameters);

        var loweredFunction = ConvertLambdaToFunction(loweredLambda, lambdaName, lambdaFunctionId, recursiveContext);
        _generatedLambdaFunctions.Add(loweredFunction);

        var closureValue = NewTemp(lambda.TypeId);
        _currentBlock!.Instructions.Add(new MirCall
        {
            Target = closureValue,
            Function = new MirFunctionRef
            {
                Name = lambdaName,
                SymbolId = lambda.SymbolId,
                SymbolKind = ResolveSymbolKind(lambda.SymbolId),
                FunctionId = lambdaFunctionId,
                Span = lambda.Span,
                TypeId = lambda.TypeId
            },
            Arguments = captureArguments,
            Span = lambda.Span
        });

        return closureValue;
    }

    private bool TryMatchRecursiveClosureSelfCapture(
        HirCapture capture,
        out RecursiveClosureBindingContext binding)
    {
        if (_recursiveClosureBindings.Count == 0)
        {
            binding = default!;
            return false;
        }

        var currentBinding = _recursiveClosureBindings.Peek();
        var symbolMatches = capture.SymbolId.IsValid &&
                            currentBinding.SymbolId.IsValid &&
                            capture.SymbolId == currentBinding.SymbolId;
        var nameMatches = !string.IsNullOrWhiteSpace(capture.Name) &&
                          string.Equals(capture.Name, currentBinding.Name, StringComparison.Ordinal);

        if (!symbolMatches && !nameMatches)
        {
            binding = default!;
            return false;
        }

        binding = currentBinding;
        return true;
    }

    private bool TryCreateClosureCaptureBinding(
        HirCapture capture,
        SourceSpan span,
        out HirParam parameter,
        out MirOperand argument)
    {
        parameter = default!;
        argument = default!;

        if (!TryResolveCaptureOperand(capture, span, out var captureOperand))
        {
            var captureName = string.IsNullOrWhiteSpace(capture.Name) ? "<anonymous>" : capture.Name;
            var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.CapturedVariableResolutionFailed(captureName),
                "E5320");
            if (HasSpan(span))
            {
                diagnostic.WithLabel(span, DiagnosticMessages.CapturedLambdaLabel);
            }

            Diagnostics.Add(diagnostic);
            return false;
        }

        var captureType = captureOperand.TypeId.IsValid
            ? captureOperand.TypeId
            : ResolveCaptureTypeId(capture);
        if (!captureType.IsValid)
        {
            var captureName = string.IsNullOrWhiteSpace(capture.Name) ? "<anonymous>" : capture.Name;
            var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.CaptureTypeInferenceFailed(captureName),
                "E5320");
            if (HasSpan(span))
            {
                diagnostic.WithLabel(span, DiagnosticMessages.CapturedLambdaLabel);
            }

            Diagnostics.Add(diagnostic);
            return false;
        }

        parameter = new HirParam
        {
            Name = capture.Name,
            SymbolId = capture.SymbolId,
            TypeId = captureType
        };
        argument = PrepareClosureCaptureArgument(captureOperand, captureType, span);
        return true;
    }

    private bool TryResolveCaptureOperand(HirCapture capture, SourceSpan span, out MirOperand operand)
    {
        if (TryResolveVariableLocal(capture.Name, capture.SymbolId, out var localId))
        {
            operand = new MirPlace
            {
                Kind = PlaceKind.Local,
                Local = localId,
                Span = span,
                TypeId = ResolveLocalType(localId)
            };
            return true;
        }

        operand = default!;
        return false;
    }

    private bool TryResolveVariableLocal(string name, SymbolId symbolId, out LocalId localId)
    {
        if (symbolId.IsValid && _symbolLocals.TryGetValue(symbolId, out localId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(name) &&
            _variableLocals.TryGetValue(name, out localId))
        {
            return true;
        }

        localId = default;
        return false;
    }

    private MirOperand PrepareClosureCaptureArgument(MirOperand operand, TypeId fallbackType, SourceSpan span)
    {
        return PrepareCallArgument(operand, fallbackType, span, forceCopy: false);
    }

    private TypeId ResolveCaptureTypeId(HirCapture capture)
    {
        if (capture.TypeId.IsValid)
        {
            return capture.TypeId;
        }

        if (capture.SymbolId.IsValid &&
            _symbolLocals.TryGetValue(capture.SymbolId, out var symbolLocal))
        {
            var localType = ResolveLocalType(symbolLocal);
            if (localType.IsValid)
            {
                return localType;
            }
        }

        if (!string.IsNullOrWhiteSpace(capture.Name) &&
            _variableLocals.TryGetValue(capture.Name, out var variableLocal))
        {
            var localType = ResolveLocalType(variableLocal);
            if (localType.IsValid)
            {
                return localType;
            }
        }

        if (capture.SymbolId.IsValid &&
            _symbolTable?.GetSymbol(capture.SymbolId) is { } symbol)
        {
            if (symbol is VarSymbol varSymbol && varSymbol.Type.IsValid)
            {
                return varSymbol.Type;
            }
        }

        return TypeId.None;
    }

    private TypeId ResolveLocalType(LocalId localId)
    {
        if (_currentFunc == null)
        {
            return TypeId.None;
        }

        var local = _currentFunc.Locals.FirstOrDefault(entry => entry.Id.Equals(localId));
        return local?.TypeId ?? TypeId.None;
    }

    private MirOperand ConvertBlock(HirBlock block)
    {
        var savedVariableLocals = _variableLocals;
        var savedSymbolLocals = _symbolLocals;
        _variableLocals = new Dictionary<string, LocalId>(_variableLocals, StringComparer.Ordinal);
        _symbolLocals = new Dictionary<SymbolId, LocalId>(_symbolLocals);

        RecursiveClosureGroupContext? recursiveGroup = null;

        try
        {
            recursiveGroup = TryPredeclareRecursiveClosureGroup(block);
            if (recursiveGroup != null)
            {
                _recursiveClosureGroups.Push(recursiveGroup);
            }

            MirOperand? lastResult = null;
            MirPoison? statementPoison = null;

            foreach (var stmt in block.Statements)
            {
                lastResult = ConvertStatement(stmt);
                if (lastResult is MirPoison poison)
                {
                    statementPoison ??= poison;
                }

                if (!CanContinueCurrentBlock())
                {
                    break;
                }
            }

            if (block.Result != null && CanContinueCurrentBlock())
            {
                var result = ConvertExpr(block.Result);
                return statementPoison != null
                    ? CreatePoisonOperand(block.TypeId, block.Span, DiagnosticMessages.BlockContainsPoisonedStatementReason)
                    : result;
            }

            return statementPoison is not null
                ? statementPoison
                : new MirConstant
                {
                    Value = new MirConstantValue.UnitValue(),
                    TypeId = new TypeId(BaseTypes.UnitId),
                    Span = block.Span
                };
        }
        finally
        {
            if (recursiveGroup != null)
            {
                _recursiveClosureGroups.Pop();
            }

            _variableLocals = savedVariableLocals;
            _symbolLocals = savedSymbolLocals;
        }
    }

    private MirOperand? ConvertStatement(HirStatement stmt)
    {
        switch (stmt)
        {
            case HirDeclStatement declStmt:
                return ConvertDeclStatement(declStmt);

            case HirExprStatement exprStmt:
                return ConvertExpr(exprStmt.Expression);

            case HirAssignStatement assignStmt:
                return ConvertAssignStatement(assignStmt);

            default:
                return ReportUnsupportedStatement(stmt);
        }
    }

    private MirOperand? ConvertDeclStatement(HirDeclStatement declStmt)
    {
        if (declStmt.Declaration is HirVal val)
        {
            if (val.IsComptime)
            {
                return new MirConstant
                {
                    Value = new MirConstantValue.UnitValue(),
                    TypeId = new TypeId(BaseTypes.UnitId),
                    Span = val.Span
                };
            }

            if (TryGetSimplePatternBinding(val.Pattern, out var binding))
            {
                if (val.Initializer is HirLambda lambda &&
                    TryGetRecursiveClosureGroupBinding(binding.Name, binding.SymbolId, out var groupBinding))
                {
                    return InitializePredeclaredRecursiveClosureBinding(groupBinding, lambda, val.Span);
                }

                var bindingType = binding.TypeId.IsValid ? binding.TypeId : val.TypeId;
                var init = ConvertInitializerWithRecursiveClosureBinding(
                    val.Initializer,
                    new RecursiveClosureBindingContext(binding.Name, binding.SymbolId, bindingType));

                // 创建局部变量
                var localId = NewLocal(
                    binding.Name,
                    bindingType,
                    isMutable: binding.IsMutable || binding.BindingMode == PatternBindingMode.MutableBorrow,
                    bindingMode: binding.BindingMode);
                _variableLocals[binding.Name] = localId;
                if (binding.SymbolId.IsValid)
                {
                    _symbolLocals[binding.SymbolId] = localId;
                }

                // 赋值
                var target = new MirPlace
                {
                    Kind = PlaceKind.Local,
                    Local = localId,
                    Span = val.Span,
                    TypeId = bindingType
                };

                if (binding.BindingMode == PatternBindingMode.ByValue)
                {
                    EmitInitialization(target, init, val.Span);
                }
                else
                {
                    var borrowSourcePlace = EnsurePlaceOperand(init, bindingType, val.Span);
                    _currentBlock!.Instructions.Add(new MirLoad
                    {
                        Target = target,
                        Source = borrowSourcePlace,
                        IsMutableBorrow = binding.BindingMode == PatternBindingMode.MutableBorrow,
                        Span = val.Span
                    });
                }
                return target;
            }

            var initValue = ConvertExpr(val.Initializer);
            var sourcePlace = EnsurePlaceOperand(initValue, val.TypeId, val.Span);

            if (!IsIrrefutablePattern(val.Pattern))
            {
                var diagnostic = Diagnostic.Diagnostic.Error(
                    DiagnosticMessages.RefutableLetPatternUnsupportedInMir,
                    "E5111");
                if (HasSpan(val.Span))
                {
                    diagnostic.WithLabel(val.Span, DiagnosticMessages.RefutableLetPatternLabel);
                }
                Diagnostics.Add(diagnostic);
                return sourcePlace;
            }

            BindMatchPatternVariables(val.Pattern, sourcePlace, new PatternLoweringContext());
            return sourcePlace;
        }
        else if (declStmt.Declaration is HirVarDecl varDecl)
        {
            if (varDecl.Initializer is HirLambda lambda &&
                TryGetRecursiveClosureGroupBinding(varDecl.Name, varDecl.SymbolId, out var groupBinding))
            {
                return InitializePredeclaredRecursiveClosureBinding(groupBinding, lambda, varDecl.Span);
            }

            var init = ConvertInitializerWithRecursiveClosureBinding(
                varDecl.Initializer,
                new RecursiveClosureBindingContext(varDecl.Name, varDecl.SymbolId, varDecl.TypeId));

            // 创建局部变量
            var localId = NewLocal(varDecl.Name, varDecl.TypeId, isMutable: true);
            _variableLocals[varDecl.Name] = localId;
            if (varDecl.SymbolId.IsValid)
            {
                _symbolLocals[varDecl.SymbolId] = localId;
            }

            // 赋值
            var target = new MirPlace
            {
                Kind = PlaceKind.Local,
                Local = localId,
                Span = varDecl.Span,
                TypeId = varDecl.TypeId
            };

            EmitInitialization(target, init, varDecl.Span);

            return target;
        }

        ReportUnsupportedDeclaration(declStmt.Declaration, declStmt.Span);
        return CreatePoisonOperand(
            declStmt.Declaration.TypeId,
            declStmt.Span,
            DiagnosticMessages.UnsupportedHirDeclarationReason(declStmt.Declaration.GetType().Name));
    }

    private MirOperand ConvertInitializerWithRecursiveClosureBinding(
        HirNode initializer,
        RecursiveClosureBindingContext binding)
    {
        if (initializer is not HirLambda)
        {
            return ConvertExpr(initializer);
        }

        _recursiveClosureBindings.Push(binding);
        try
        {
            return ConvertExpr(initializer);
        }
        finally
        {
            _recursiveClosureBindings.Pop();
        }
    }

    private RecursiveClosureGroupContext? TryPredeclareRecursiveClosureGroup(HirBlock block)
    {
        var bindings = new List<RecursiveClosureGroupBindingContext>();

        foreach (var statement in block.Statements)
        {
            if (!TryCollectRecursiveClosureGroupBinding(statement, bindings))
            {
                continue;
            }
        }

        if (bindings.Count == 0)
        {
            return null;
        }

        var sharedCaptures = CollectRecursiveClosureGroupSharedCaptures(bindings);
        return new RecursiveClosureGroupContext
        {
            Bindings = bindings,
            SharedCaptures = sharedCaptures
        };
    }

    private bool TryCollectRecursiveClosureGroupBinding(
        HirStatement statement,
        List<RecursiveClosureGroupBindingContext> bindings)
    {
        if (statement is not HirDeclStatement { Declaration: var declaration })
        {
            return false;
        }

        if (declaration is HirVal { Initializer: HirLambda lambda } val &&
            TryGetSimplePatternBinding(val.Pattern, out var binding))
        {
            return TryAddRecursiveClosureGroupBinding(
                bindings,
                binding.Name,
                binding.SymbolId,
                binding.TypeId.IsValid ? binding.TypeId : val.TypeId,
                lambda,
                binding.BindingMode,
                isMutable: binding.IsMutable || binding.BindingMode == PatternBindingMode.MutableBorrow);
        }

        if (declaration is HirVarDecl { Initializer: HirLambda lambdaInitializer } varDecl)
        {
            return TryAddRecursiveClosureGroupBinding(
                bindings,
                varDecl.Name,
                varDecl.SymbolId,
                varDecl.TypeId,
                lambdaInitializer,
                PatternBindingMode.ByValue,
                isMutable: true);
        }

        return false;
    }

    private bool TryAddRecursiveClosureGroupBinding(
        List<RecursiveClosureGroupBindingContext> bindings,
        string name,
        SymbolId symbolId,
        TypeId typeId,
        HirLambda lambda,
        PatternBindingMode bindingMode,
        bool isMutable)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (bindings.Any(existing =>
                (symbolId.IsValid && existing.SymbolId.IsValid && existing.SymbolId == symbolId) ||
                string.Equals(existing.Name, name, StringComparison.Ordinal)))
        {
            return false;
        }

        var localId = NewLocal(name, typeId, isMutable: isMutable, bindingMode: bindingMode);
        _variableLocals[name] = localId;
        if (symbolId.IsValid)
        {
            _symbolLocals[symbolId] = localId;
        }

        var lambdaName = $"{WellKnownStrings.InternalNames.LambdaPrefix}{_nextLambdaId++}";
        bindings.Add(new RecursiveClosureGroupBindingContext(
            name,
            symbolId,
            typeId,
            localId,
            lambdaName,
            lambda.SymbolId,
            BuildGeneratedFunctionId(lambda.SymbolId, lambdaName, ResolveSymbolKind(lambda.SymbolId), MirSyntheticFunctions.RecursiveClosureRole),
            lambda,
            bindingMode,
            isMutable));
        return true;
    }

    private List<HirCapture> CollectRecursiveClosureGroupSharedCaptures(
        IReadOnlyList<RecursiveClosureGroupBindingContext> bindings)
    {
        var captures = new List<HirCapture>();
        foreach (var binding in bindings)
        {
            foreach (var capture in binding.Lambda.Captures)
            {
                if (IsRecursiveClosureGroupMemberCapture(bindings, capture))
                {
                    continue;
                }

                if (captures.Any(existing => AreEquivalentCaptures(existing, capture)))
                {
                    continue;
                }

                captures.Add(capture);
            }
        }

        return captures;
    }

    private bool TryCreateRecursiveClosureGroupSharedEnvironment(
        IReadOnlyList<HirCapture> captures,
        SourceSpan span,
        out IReadOnlyList<HirParam> parameters,
        out IReadOnlyList<MirOperand> arguments)
    {
        var captureParameters = new List<HirParam>(captures.Count);
        var captureArguments = new List<MirOperand>(captures.Count);

        foreach (var capture in captures)
        {
            if (!TryCreateClosureCaptureBinding(capture, span, out var parameter, out var argument))
            {
                parameters = [];
                arguments = [];
                return false;
            }

            captureParameters.Add(parameter);
            captureArguments.Add(argument);
        }

        parameters = captureParameters;
        arguments = captureArguments;
        return true;
    }

    private bool TryGetRecursiveClosureGroupBinding(
        string name,
        SymbolId symbolId,
        out RecursiveClosureGroupBindingContext binding)
    {
        if (_recursiveClosureGroups.Count > 0)
        {
            var group = _recursiveClosureGroups.Peek();
            foreach (var candidate in group.Bindings)
            {
                if (candidate.SymbolId.IsValid && symbolId.IsValid && candidate.SymbolId == symbolId)
                {
                    binding = candidate;
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(name) &&
                    string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    binding = candidate;
                    return true;
                }
            }
        }

        binding = default!;
        return false;
    }

    private MirOperand InitializePredeclaredRecursiveClosureBinding(
        RecursiveClosureGroupBindingContext binding,
        HirLambda lambda,
        SourceSpan span)
    {
        var group = _recursiveClosureGroups.Peek();
        if (!EnsureRecursiveClosureGroupEnvironment(group, span))
        {
            return CreatePoisonOperand(
                binding.TypeId,
                span,
                DiagnosticMessages.FailedRecursiveClosureGroupEnvironmentReason);
        }

        var loweredFunction = ConvertRecursiveClosureGroupLambda(lambda, binding, group);
        _generatedLambdaFunctions.Add(loweredFunction);

        var target = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = binding.LocalId,
            Span = span,
            TypeId = binding.TypeId
        };

        _currentBlock!.Instructions.Add(new MirCall
        {
            Target = target,
            Function = new MirFunctionRef
            {
                Name = binding.LambdaName,
                SymbolId = lambda.SymbolId,
                SymbolKind = ResolveSymbolKind(lambda.SymbolId),
                FunctionId = binding.LambdaFunctionId,
                Span = span,
                TypeId = binding.TypeId
            },
            Arguments = [.. group.SharedCaptureArguments],
            Span = span
        });

        return target;
    }

    private bool EnsureRecursiveClosureGroupEnvironment(RecursiveClosureGroupContext group, SourceSpan span)
    {
        if (group.IsEnvironmentInitialized)
        {
            return true;
        }

        if (!TryCreateRecursiveClosureGroupSharedEnvironment(group.SharedCaptures, span, out var sharedParameters, out var sharedArguments))
        {
            return false;
        }

        group.SharedCaptureParameters = sharedParameters;
        group.SharedCaptureArguments = sharedArguments;
        group.IsEnvironmentInitialized = true;
        return true;
    }

    private MirFunc ConvertRecursiveClosureGroupLambda(
        HirLambda lambda,
        RecursiveClosureGroupBindingContext binding,
        RecursiveClosureGroupContext group)
    {
        var loweredLambda = lambda with
        {
            Parameters = [.. group.SharedCaptureParameters, .. lambda.Parameters],
            Captures = []
        };

        var bodyContext = new RecursiveClosureBodyContext(
            group.Bindings.Select(entry =>
                new RecursiveClosureBodyBinding(
                    entry.Name,
                    entry.SymbolId,
                    entry.TypeId,
                    entry.LambdaName,
                    entry.LambdaSymbolId,
                    entry.LambdaFunctionId)).ToList(),
            group.SharedCaptureParameters);

        return ConvertLambdaToFunction(loweredLambda, binding.LambdaName, binding.LambdaFunctionId, bodyContext);
    }

    private static bool IsRecursiveClosureGroupMemberCapture(
        IReadOnlyList<RecursiveClosureGroupBindingContext> bindings,
        HirCapture capture)
    {
        foreach (var binding in bindings)
        {
            if (binding.SymbolId.IsValid &&
                capture.SymbolId.IsValid &&
                binding.SymbolId == capture.SymbolId)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(binding.Name) &&
                !string.IsNullOrWhiteSpace(capture.Name) &&
                string.Equals(binding.Name, capture.Name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AreEquivalentCaptures(HirCapture left, HirCapture right)
    {
        if (left.SymbolId.IsValid && right.SymbolId.IsValid)
        {
            return left.SymbolId == right.SymbolId;
        }

        return !string.IsNullOrWhiteSpace(left.Name) &&
               !string.IsNullOrWhiteSpace(right.Name) &&
               string.Equals(left.Name, right.Name, StringComparison.Ordinal);
    }

    private sealed record SimplePatternBinding(
        string Name,
        SymbolId SymbolId,
        TypeId TypeId,
        PatternBindingMode BindingMode,
        bool IsMutable);

    private static bool TryGetSimplePatternBinding(HirPattern pattern, out SimplePatternBinding binding)
    {
        if (pattern is HirVarPattern { IsWildcard: false } varPattern &&
            !string.IsNullOrWhiteSpace(varPattern.Name))
        {
            binding = new SimplePatternBinding(
                varPattern.Name,
                varPattern.SymbolId,
                varPattern.TypeId,
                varPattern.BindingMode,
                varPattern.IsMutableBinding);
            return true;
        }

        binding = null!;
        return false;
    }

    private MirOperand? ConvertAssignStatement(HirAssignStatement assignStmt)
    {
        var value = ConvertExpr(assignStmt.Value);

        if (assignStmt.Target is HirVar varNode &&
            TryGetLocalForVariable(varNode, out var localId))
        {
            var target = new MirPlace
            {
                Kind = PlaceKind.Local,
                Local = localId,
                Span = assignStmt.Span,
                TypeId = varNode.TypeId.IsValid ? varNode.TypeId : ResolveLocalType(localId)
            };

            EmitStore(target, value, assignStmt.Span);

            return CreateUnitOperand(assignStmt.Span);
        }

        if (TryConvertPlaceShapedExprPlace(assignStmt.Target, out var placeTarget))
        {
            EmitStore(placeTarget, value, assignStmt.Span);
            return CreateUnitOperand(assignStmt.Span);
        }

        ReportUnsupportedAssignTarget(assignStmt.Target, assignStmt.Span);
        return null;
    }

    private static MirConstant CreateUnitOperand(SourceSpan span)
    {
        return new MirConstant
        {
            Value = new MirConstantValue.UnitValue(),
            TypeId = new TypeId(BaseTypes.UnitId),
            Span = span
        };
    }

    private MirOperand ConvertTuple(HirTuple tuple)
    {
        return ConvertTupleAggregate(tuple.Elements, tuple.TypeId, tuple.Span);
    }

    private MirOperand ConvertList(HirList list)
    {
        if (list.HasRest && list.Elements.Count > 0)
        {
            return ConvertListWithSpread(list);
        }

        var aggregate = ConvertListAggregate(list.Elements, list.TypeId, list.Span);
        RegisterKnownListLength(aggregate, list.Elements.Count);
        return aggregate;
    }

    private MirOperand ConvertListWithSpread(HirList list)
    {
        // Last element is the spread (..rest) expression — a list to be concatenated.
        var leadingCount = list.Elements.Count - 1;
        var spreadNode = list.Elements[^1];

        // Compute element size from all elements (leading + spread share element type).
        var aggregateElementSize = list.Elements.Max(element =>
        {
            var elementTypeId = element.TypeId.IsValid ? element.TypeId : TypeId.None;
            return GetRuntimeElementSize(elementTypeId);
        });

        // Create array with just the leading elements' capacity.
        var aggregate = EmitRuntimeArrayNew(list.TypeId, leadingCount, aggregateElementSize, list.Span);

        // Store each leading element at its index.
        for (int i = 0; i < leadingCount; i++)
        {
            var elementNode = list.Elements[i];
            var elementOperand = ConvertExpr(elementNode);
            var storeValue = PrepareStoreValue(elementOperand, elementNode.TypeId, elementNode.Span);
            var elementTypeId = elementNode.TypeId.IsValid ? elementNode.TypeId : elementOperand.TypeId;

            var slot = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = aggregate,
                Index = new MirConstant
                {
                    Value = new MirConstantValue.IntValue(i),
                    TypeId = new TypeId(BaseTypes.IntId),
                    Span = elementNode.Span
                },
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = elementTypeId,
                Span = elementNode.Span
            };

            _currentBlock!.Instructions.Add(new MirStore
            {
                Target = slot,
                Value = storeValue,
                Span = elementNode.Span
            });
        }

        // Evaluate the spread expression → source array.
        var spreadOperand = ConvertExpr(spreadNode);
        var spreadPlace = EnsurePlaceOperand(spreadOperand, list.TypeId, spreadNode.Span);

        // Extend the destination array with all elements from the source.
        var extendedArray = EmitRuntimeArrayExtend(aggregate, spreadPlace, aggregateElementSize, list.Span);

        // Length is dynamic — don't register a known length.
        ClearKnownListLength(extendedArray);
        ClearRuntimeArrayLocal(extendedArray);

        return extendedArray;
    }

    private MirOperand ConvertFieldAccess(HirFieldAccess fieldAccess)
    {
        var baseOperand = ConvertExpr(fieldAccess.Target);
        var basePlace = EnsureReadableProjectionBasePlace(baseOperand, fieldAccess.Target.TypeId, fieldAccess.Span);

        var fieldPlace = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = basePlace,
            FieldName = NormalizeFieldAccessName(fieldAccess),
            Span = fieldAccess.Span,
            TypeId = fieldAccess.TypeId
        };

        return EnsureReadValue(fieldPlace, fieldAccess.TypeId, fieldAccess.Span);
    }

    private MirOperand ConvertIndexAccess(HirIndexAccess indexAccess)
    {
        var baseOperand = ConvertExpr(indexAccess.Target);
        var basePlace = EnsureReadableProjectionBasePlace(baseOperand, indexAccess.Target.TypeId, indexAccess.Span);

        var indexOperand = ConvertExpr(indexAccess.Index);
        indexOperand = EnsureReadValue(indexOperand, indexAccess.Index.TypeId, indexAccess.Span);

        var indexedPlace = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = basePlace,
            Index = indexOperand,
            IndexAccessKind = ResolveIndexAccessKind(basePlace, indexAccess.TargetKind),
            Span = indexAccess.Span,
            TypeId = indexAccess.TypeId
        };

        return EnsureReadValue(indexedPlace, indexAccess.TypeId, indexAccess.Span);
    }
}
