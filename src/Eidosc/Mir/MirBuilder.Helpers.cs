using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// Utility methods, aggregate conversion, diagnostics helpers, and CStruct accessors.
/// </summary>
public sealed partial class MirBuilder
{
    private bool IsCopyType(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (_copyTypeCache.TryGetValue(typeId.Value, out var cached))
        {
            return cached;
        }

        var isCopy = _extraCopyLikeTypeIds.Contains(typeId.Value) ||
                     CopyTypeSemantics.IsCopyTypeForMirBuilding(
                         typeId,
                         _hasCopyImplResolver,
                         _typeDescriptorsById,
                         _dynamicTypeKeysById);
        _copyTypeCache[typeId.Value] = isCopy;
        return isCopy;
    }

    private bool ShouldCopyLocalValue(LocalId localId, TypeId typeId)
    {
        return _comprehensionElementLocals.Contains(localId) || IsCopyType(typeId);
    }

    private void EmitInitialization(MirPlace target, MirOperand value, SourceSpan span)
    {
        if (!CanContinueCurrentBlock())
        {
            return;
        }

        if (value is MirPlace { Kind: PlaceKind.Local } place)
        {
            if (!TryResolveOperandTypeId(value, target.TypeId, span, "initialization", out var typeId))
            {
                _currentBlock!.Instructions.Add(new MirAssign
                {
                    Target = target,
                    Source = CreatePoisonOperand(TypeId.None, span, DiagnosticMessages.MissingMirTypeForInitializationReason),
                    Span = span
                });
                ClearKnownListLength(target);
                ClearRuntimeArrayLocal(target);
                return;
            }

            var source = place.TypeId.Equals(typeId)
                ? place
                : place with { TypeId = typeId };

            if (ShouldCopyLocalValue(place.Local, typeId))
            {
                _currentBlock!.Instructions.Add(new MirCopy
                {
                    Target = target,
                    Source = source,
                    Span = span
                });
            }
            else
            {
                _currentBlock!.Instructions.Add(new MirMove
                {
                    Target = target,
                    Source = source,
                    Span = span
                });
            }

            PropagateKnownListLength(target, place);
            PropagateRuntimeArrayLocal(target, place);
            return;
        }

        ClearKnownListLength(target);
        ClearRuntimeArrayLocal(target);
        _currentBlock!.Instructions.Add(new MirAssign
        {
            Target = target,
            Source = value,
            Span = span
        });
    }

    private void EmitStore(MirPlace target, MirOperand value, SourceSpan span)
    {
        if (value is MirPlace { Kind: PlaceKind.Local } sourcePlace)
        {
            PropagateKnownListLength(target, sourcePlace);
            PropagateRuntimeArrayLocal(target, sourcePlace);
        }
        else
        {
            ClearKnownListLength(target);
            ClearRuntimeArrayLocal(target);
        }

        var storeValue = PrepareStoreValue(value, target.TypeId, span);
        _currentBlock!.Instructions.Add(new MirStore
        {
            Target = target,
            Value = storeValue,
            Span = span
        });
    }

    private bool CanContinueCurrentBlock()
    {
        return _currentBlock?.Terminator == null;
    }

    private void EmitInitializationAndGoto(
        MirPlace target,
        MirOperand value,
        SourceSpan span,
        BlockId destination)
    {
        if (!CanContinueCurrentBlock())
        {
            return;
        }

        EmitInitialization(target, value, span);
        _currentBlock!.Terminator = new MirGoto { Target = destination, Span = span };
    }

    private MirOperand PrepareStoreValue(MirOperand value, TypeId fallbackType, SourceSpan span)
    {
        if (value is MirPlace { Kind: PlaceKind.Local } place)
        {
            if (!TryResolveOperandTypeId(value, fallbackType, span, "store value", out var typeId))
            {
                return CreatePoisonOperand(TypeId.None, span, DiagnosticMessages.MissingMirTypeForStoreValueReason);
            }

            var temp = NewTemp(typeId);
            var source = place.TypeId.Equals(typeId)
                ? place
                : place with { TypeId = typeId };

            if (ShouldCopyLocalValue(place.Local, typeId))
            {
                _currentBlock!.Instructions.Add(new MirCopy
                {
                    Target = temp,
                    Source = source,
                    Span = span
                });
            }
            else
            {
                _currentBlock!.Instructions.Add(new MirMove
                {
                    Target = temp,
                    Source = source,
                    Span = span
                });
            }

            return temp;
        }

        return value;
    }

    private MirPlace EmitRuntimeArrayLength(MirPlace sourcePlace, SourceSpan span)
    {
        var intType = new TypeId(BaseTypes.IntId);
        var lengthTemp = NewTemp(intType);
        _currentBlock!.Instructions.Add(new MirCall
        {
            Target = lengthTemp,
            Function = MirRuntimeFunctions.CreateFunctionRef(WellKnownStrings.InternalNames.ArrayLength, intType, span),
            Arguments = [sourcePlace],
            Span = span
        });
        ClearKnownListLength(lengthTemp);
        ClearRuntimeArrayLocal(lengthTemp);
        return lengthTemp;
    }

    private MirPlace EmitRuntimeArrayNew(TypeId arrayTypeId, int capacity, int elementSize, SourceSpan span)
    {
        var target = NewTemp(arrayTypeId);
        _currentBlock!.Instructions.Add(new MirCall
        {
            Target = target,
            Function = MirRuntimeFunctions.CreateFunctionRef(WellKnownStrings.InternalNames.ArrayNew, arrayTypeId, span),
            Arguments =
            [
                CreateIntConstant(Math.Max(capacity, 0), span),
                CreateIntConstant(Math.Max(elementSize, 0), span)
            ],
            Span = span
        });
        RegisterRuntimeArrayLocal(target);

        return target;
    }

    private MirPlace EmitRuntimeArrayExtend(MirPlace dstArray, MirPlace srcArray, int elementSize, SourceSpan span)
    {
        var arrayTypeId = dstArray.TypeId;
        var target = NewTemp(arrayTypeId);
        _currentBlock!.Instructions.Add(new MirCall
        {
            Target = target,
            Function = MirRuntimeFunctions.CreateFunctionRef(
                WellKnownStrings.InternalNames.ArrayExtend, arrayTypeId, span),
            Arguments =
            [
                dstArray,
                srcArray,
                CreateIntConstant(Math.Max(elementSize, 0), span)
            ],
            Span = span
        });
        RegisterRuntimeArrayLocal(target);

        return target;
    }

    private void EmitRuntimeArraySlotStore(
        MirPlace array,
        int index,
        MirOperand value,
        TypeId slotType,
        SourceSpan span)
    {
        var slot = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = array,
            Index = CreateIntConstant(index, span),
            IndexAccessKind = MirIndexAccessKind.RuntimeArray,
            TypeId = slotType.IsValid ? slotType : TypeId.None,
            Span = span
        };

        EmitStore(slot, value, span);
    }

    private MirBasicBlock NewBlock(bool isEntry = false)
    {
        return new MirBasicBlock
        {
            Id = new BlockId { Value = _nextBlockId++ },
            IsEntry = isEntry
        };
    }

    private LocalId NewLocal(
        string name = "",
        TypeId? typeId = null,
        bool isParameter = false,
        bool isMutable = false,
        PatternBindingMode bindingMode = PatternBindingMode.ByValue)
    {
        var normalizedTypeId = typeId is { IsValid: true } validTypeId
            ? validTypeId
            : TypeId.None;

        var id = new LocalId { Value = _nextLocalId++ };
        _currentFunc?.Locals.Add(new MirLocal
        {
            Id = id,
            Name = name,
            TypeId = normalizedTypeId,
            IsParameter = isParameter,
            IsMutable = isMutable,
            BindingMode = bindingMode
        });
        return id;
    }

    private MirPlace NewTemp(TypeId? typeId = null)
    {
        var normalizedTypeId = typeId is { IsValid: true } validTypeId
            ? validTypeId
            : TypeId.None;

        var localId = NewLocal($"$t{_nextTempId++}", normalizedTypeId);
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = localId,
            TypeId = normalizedTypeId
        };
    }

    private MirOperand AssignToTemp(MirOperand value)
    {
        if (value is MirPlace { Kind: PlaceKind.Local })
            return value;

        var temp = NewTemp(value.TypeId);
        _currentBlock!.Instructions.Add(new MirAssign
        {
            Target = temp,
            Source = value
        });
        return temp;
    }

    private static string NormalizeIdentifierSegment(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var normalized = new string(
            raw
                .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_')
                .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = fallback;
        }

        if (char.IsDigit(normalized[0]))
        {
            normalized = $"_{normalized}";
        }

        return normalized;
    }

    private MirFunc ConvertLambdaToFunction(
        HirLambda lambda,
        string lambdaName,
        FunctionId? functionId = null,
        RecursiveClosureBodyContext? recursiveClosureContext = null)
    {
        var savedFunc = _currentFunc;
        var savedBlock = _currentBlock;
        var savedNextBlockId = _nextBlockId;
        var savedNextLocalId = _nextLocalId;
        var savedNextTempId = _nextTempId;
        var savedVariableLocals = _variableLocals;
        var savedSymbolLocals = _symbolLocals;
        var savedKnownListLengths = _knownListLengths;
        var savedRuntimeArrayLocals = _runtimeArrayLocals;
        var savedComprehensionElementLocals = _comprehensionElementLocals;
        var savedLoopContexts = _loopContextStack;

        try
        {
            _nextBlockId = 1;
            _nextLocalId = 1;
            _nextTempId = 1;
            _variableLocals = new Dictionary<string, LocalId>();
            _symbolLocals = new Dictionary<SymbolId, LocalId>();
            _knownListLengths = new Dictionary<LocalId, int>();
            _runtimeArrayLocals = new HashSet<LocalId>();
            _comprehensionElementLocals = new HashSet<LocalId>();
            _loopContextStack = new Stack<LoopLoweringContext>();

            var entryBlock = NewBlock(isEntry: true);
            _currentBlock = entryBlock;

            var returnType = lambda.ReturnType.IsValid
                ? lambda.ReturnType
                : lambda.Body.TypeId;

            _currentFunc = new MirFunc
            {
                Name = lambdaName,
                SourceName = lambdaName,
                Span = lambda.Span,
                SymbolId = lambda.SymbolId,
                FunctionId = functionId ??
                             BuildGeneratedFunctionId(
                                 lambda.SymbolId,
                                 lambdaName,
                                 ResolveSymbolKind(lambda.SymbolId),
                                 MirSyntheticFunctions.LambdaRole),
                ReturnType = returnType,
                EntryBlockId = entryBlock.Id
            };
            _currentFunc.BasicBlocks.Add(entryBlock);

            var lambdaParameterTypes = ResolveFlattenedLambdaParameterTypes(lambda.TypeId);
            var lambdaParameterTypeOffset = Math.Max(0, lambda.Parameters.Count - lambdaParameterTypes.Count);
            for (var parameterIndex = 0; parameterIndex < lambda.Parameters.Count; parameterIndex++)
            {
                var param = lambda.Parameters[parameterIndex];
                var parameterType = param.TypeId.IsValid
                    ? param.TypeId
                    : parameterIndex >= lambdaParameterTypeOffset &&
                      parameterIndex - lambdaParameterTypeOffset < lambdaParameterTypes.Count
                    ? lambdaParameterTypes[parameterIndex - lambdaParameterTypeOffset]
                    : TypeId.None;
                var localId = NewLocal(param.Name, parameterType, isParameter: true, isMutable: param.IsMutable);
                _variableLocals[param.Name] = localId;
                if (param.SymbolId.IsValid)
                {
                    _symbolLocals[param.SymbolId] = localId;
                }
            }

            EmitRecursiveClosureBindingsPrelude(lambda, recursiveClosureContext);

            var bodyResult = ConvertExpr(lambda.Body);
            if (_currentBlock!.Terminator == null)
            {
                _currentBlock.Terminator = new MirReturn
                {
                    Value = bodyResult,
                    Span = lambda.Span
                };
            }

            return _currentFunc;
        }
        finally
        {
            _currentFunc = savedFunc;
            _currentBlock = savedBlock;
            _nextBlockId = savedNextBlockId;
            _nextLocalId = savedNextLocalId;
            _nextTempId = savedNextTempId;
            _variableLocals = savedVariableLocals;
            _symbolLocals = savedSymbolLocals;
            _knownListLengths = savedKnownListLengths;
            _runtimeArrayLocals = savedRuntimeArrayLocals;
            _comprehensionElementLocals = savedComprehensionElementLocals;
            _loopContextStack = savedLoopContexts;
        }
    }

    private static TypeId GetLambdaReturnType(HirLambda lambda)
    {
        return lambda.ReturnType.IsValid ? lambda.ReturnType : lambda.Body.TypeId;
    }

    private static HirLambda FlattenCurriedLambdaBody(HirLambda lambda)
    {
        var parameters = new List<HirParam>(lambda.Parameters);
        var captures = new List<HirCapture>(lambda.Captures);
        var parameterSymbols = new HashSet<SymbolId>(lambda.Parameters
            .Where(static parameter => parameter.SymbolId.IsValid)
            .Select(static parameter => parameter.SymbolId));
        var parameterNames = new HashSet<string>(
            lambda.Parameters
                .Select(static parameter => parameter.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.Ordinal);
        var body = lambda.Body;
        var returnType = GetLambdaReturnType(lambda);

        while (body is HirLambda nested)
        {
            parameters.AddRange(nested.Parameters);
            foreach (var parameter in nested.Parameters)
            {
                if (parameter.SymbolId.IsValid)
                {
                    parameterSymbols.Add(parameter.SymbolId);
                }

                if (!string.IsNullOrWhiteSpace(parameter.Name))
                {
                    parameterNames.Add(parameter.Name);
                }
            }

            foreach (var capture in nested.Captures)
            {
                var isParameterCapture = capture.SymbolId.IsValid
                    ? parameterSymbols.Contains(capture.SymbolId)
                    : parameterNames.Contains(capture.Name);
                if (!isParameterCapture)
                {
                    captures.Add(capture);
                }
            }

            body = nested.Body;
            returnType = GetLambdaReturnType(nested);
        }

        return lambda with
        {
            Parameters = parameters,
            Body = body,
            Captures = captures,
            ReturnType = returnType
        };
    }

    private List<TypeId> ResolveFlattenedLambdaParameterTypes(TypeId lambdaTypeId)
    {
        var parameterTypes = new List<TypeId>();
        if (!TryResolveFunctionValueSignatureTypeId(lambdaTypeId, out var signatureTypeId))
        {
            return parameterTypes;
        }

        CollectFlattenedFunctionParameterTypes(signatureTypeId, parameterTypes, []);
        return parameterTypes;
    }

    private void CollectFlattenedFunctionParameterTypes(
        TypeId functionTypeId,
        List<TypeId> parameterTypes,
        HashSet<int> visited)
    {
        if (!functionTypeId.IsValid ||
            !visited.Add(functionTypeId.Value) ||
            !TryGetTypeDescriptor(functionTypeId, out var descriptor) ||
            descriptor is not TypeDescriptor.Function function)
        {
            return;
        }

        parameterTypes.AddRange(function.ParamTypes);
        CollectFlattenedFunctionParameterTypes(function.ReturnType, parameterTypes, visited);
    }

    private bool TryGetTypeDescriptor(TypeId typeId, out TypeDescriptor descriptor)
    {
        if (_typeDescriptorsById.TryGetValue(typeId.Value, out descriptor!))
        {
            return true;
        }

        if (_dynamicTypeKeysById.TryGetValue(typeId.Value, out var typeKey) &&
            TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor!))
        {
            _typeDescriptorsById[typeId.Value] = descriptor;
            _dynamicTypeIdByDescriptor[descriptor] = typeId;
            return true;
        }

        descriptor = null!;
        return false;
    }

    private void EmitRecursiveClosureBindingsPrelude(
        HirLambda lambda,
        RecursiveClosureBodyContext? recursiveClosureContext)
    {
        if (recursiveClosureContext == null)
        {
            return;
        }

        var captureArguments = new List<MirOperand>(recursiveClosureContext.PreboundCaptureParameters.Count);
        foreach (var parameter in recursiveClosureContext.PreboundCaptureParameters)
        {
            if (!TryResolveVariableLocal(parameter.Name, parameter.SymbolId, out var captureLocalId))
            {
                var parameterName = string.IsNullOrWhiteSpace(parameter.Name) ? "<capture>" : parameter.Name;
                var diagnostic = Diagnostic.Diagnostic.Error(
                    DiagnosticMessages.RecursiveClosureCaptureParameterResolutionFailed(parameterName),
                    "E5320");
                if (HasSpan(lambda.Span))
                {
                    diagnostic.WithLabel(lambda.Span, DiagnosticMessages.RecursiveClosureSelfBindingLabel);
                }

                Diagnostics.Add(diagnostic);
                return;
            }

            captureArguments.Add(new MirPlace
            {
                Kind = PlaceKind.Local,
                Local = captureLocalId,
                Span = lambda.Span,
                TypeId = ResolveLocalType(captureLocalId)
            });
        }

        foreach (var binding in recursiveClosureContext.Bindings)
        {
            var localId = NewLocal(binding.Name, binding.TypeId);
            _variableLocals[binding.Name] = localId;
            if (binding.SymbolId.IsValid)
            {
                _symbolLocals[binding.SymbolId] = localId;
            }

            var closureTarget = new MirPlace
            {
                Kind = PlaceKind.Local,
                Local = localId,
                Span = lambda.Span,
                TypeId = binding.TypeId
            };

            _currentBlock!.Instructions.Add(new MirCall
            {
                Target = closureTarget,
                Function = new MirFunctionRef
                {
                    Name = binding.LambdaName,
                    SymbolId = binding.LambdaSymbolId,
                    SymbolKind = ResolveSymbolKind(binding.LambdaSymbolId),
                    FunctionId = binding.LambdaFunctionId,
                    Span = lambda.Span,
                    TypeId = binding.TypeId.IsValid ? binding.TypeId : lambda.TypeId
                },
                Arguments = [.. captureArguments],
                Span = lambda.Span
            });
        }
    }

    private MirPlace ConvertTupleAggregate(IReadOnlyList<HirNode> elements, TypeId aggregateTypeId, SourceSpan aggregateSpan)
    {
        // Empty tuple () is Unit — emit a constant, not an alloca (void alloca is invalid in LLVM IR).
        if (elements.Count == 0)
        {
            var unitPlace = NewTemp(new TypeId(BaseTypes.UnitId));
            _currentBlock!.Instructions.Add(new MirAssign
            {
                Target = unitPlace,
                Source = new MirConstant
                {
                    Value = new MirConstantValue.UnitValue(),
                    TypeId = new TypeId(BaseTypes.UnitId),
                    Span = aggregateSpan
                },
                Span = aggregateSpan
            });
            return unitPlace;
        }

        var aggregate = NewTemp(aggregateTypeId);

        _currentBlock!.Instructions.Add(new MirAlloc
        {
            Target = aggregate,
            TypeId = aggregateTypeId,
            Span = aggregateSpan
        });

        for (int i = 0; i < elements.Count; i++)
        {
            var elementNode = elements[i];
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
                IndexAccessKind = MirIndexAccessKind.Aggregate,
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

        return aggregate;
    }

    private MirPlace ConvertListAggregate(IReadOnlyList<HirNode> elements, TypeId aggregateTypeId, SourceSpan aggregateSpan)
    {
        var aggregateElementSize = elements.Count == 0
            ? 0
            : elements.Max(element =>
            {
                var elementTypeId = element.TypeId.IsValid ? element.TypeId : TypeId.None;
                return GetRuntimeElementSize(elementTypeId);
            });
        var aggregate = EmitRuntimeArrayNew(
            aggregateTypeId,
            elements.Count,
            aggregateElementSize,
            aggregateSpan);

        for (int i = 0; i < elements.Count; i++)
        {
            var elementNode = elements[i];
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

        return aggregate;
    }

    private static MirPoison CreatePoisonOperand(TypeId typeId, SourceSpan span, string reason)
    {
        return new MirPoison
        {
            TypeId = typeId,
            Span = span,
            Reason = reason
        };
    }

    private bool TryResolveOperandTypeId(
        MirOperand operand,
        TypeId fallbackType,
        SourceSpan span,
        string context,
        out TypeId typeId)
    {
        if (operand is MirPoison)
        {
            typeId = TypeId.None;
            EmitError(
                DiagnosticMessages.CannotResolveMirTypePreparingPoison(context),
                "E5333",
                span,
                DiagnosticMessages.MirPoisonTypeLabel);
            return false;
        }

        typeId = operand.TypeId.IsValid ? operand.TypeId : fallbackType;
        if (!typeId.IsValid)
        {
            EmitError(
                DiagnosticMessages.CannotResolveMirTypePreparingMissingFallback(context),
                "E5333",
                span,
                DiagnosticMessages.MissingMirTypeLabel);
            return false;
        }

        return true;
    }

    private bool TryGetLocalForVariable(HirVar variable, out LocalId localId)
    {
        if (variable.SymbolId.IsValid && _symbolLocals.TryGetValue(variable.SymbolId, out localId))
        {
            return true;
        }

        return _variableLocals.TryGetValue(variable.Name, out localId);
    }

    private MirPlace EnsurePlaceOperand(MirOperand operand, TypeId fallbackType, SourceSpan span)
    {
        if (operand is MirPlace place)
        {
            return place;
        }

        if (!TryResolveOperandTypeId(operand, fallbackType, span, "place operand", out var typeId))
        {
            var poison = CreatePoisonOperand(TypeId.None, span, DiagnosticMessages.MissingMirTypeForPlaceOperandReason);
            var poisonTemp = NewTemp(TypeId.None);
            EmitInitialization(poisonTemp, poison, span);
            return poisonTemp;
        }

        var temp = NewTemp(typeId);
        EmitInitialization(temp, operand, span);
        return temp;
    }

    private static bool TryConvertHirBinaryOpEnum(Eidosc.Hir.BinaryOp op, out BinaryOp mirOperator)
    {
        switch (op)
        {
            case Eidosc.Hir.BinaryOp.Add:
                mirOperator = BinaryOp.Add;
                return true;
            case Eidosc.Hir.BinaryOp.Sub:
                mirOperator = BinaryOp.Sub;
                return true;
            case Eidosc.Hir.BinaryOp.Mul:
                mirOperator = BinaryOp.Mul;
                return true;
            case Eidosc.Hir.BinaryOp.Div:
                mirOperator = BinaryOp.Div;
                return true;
            case Eidosc.Hir.BinaryOp.Mod:
                mirOperator = BinaryOp.Mod;
                return true;
            case Eidosc.Hir.BinaryOp.Eq:
                mirOperator = BinaryOp.Eq;
                return true;
            case Eidosc.Hir.BinaryOp.Ne:
                mirOperator = BinaryOp.Ne;
                return true;
            case Eidosc.Hir.BinaryOp.Lt:
                mirOperator = BinaryOp.Lt;
                return true;
            case Eidosc.Hir.BinaryOp.Le:
                mirOperator = BinaryOp.Le;
                return true;
            case Eidosc.Hir.BinaryOp.Gt:
                mirOperator = BinaryOp.Gt;
                return true;
            case Eidosc.Hir.BinaryOp.Ge:
                mirOperator = BinaryOp.Ge;
                return true;
            case Eidosc.Hir.BinaryOp.And:
                mirOperator = BinaryOp.And;
                return true;
            case Eidosc.Hir.BinaryOp.Or:
                mirOperator = BinaryOp.Or;
                return true;
            case Eidosc.Hir.BinaryOp.Concat:
                mirOperator = BinaryOp.Concat;
                return true;
            default:
                mirOperator = default;
                return false;
        }
    }

    private static bool TryConvertHirUnaryOpEnum(Eidosc.Hir.UnaryOp op, out UnaryOp mirOperator)
    {
        switch (op)
        {
            case Eidosc.Hir.UnaryOp.Neg:
                mirOperator = UnaryOp.Neg;
                return true;
            case Eidosc.Hir.UnaryOp.Not:
                mirOperator = UnaryOp.Not;
                return true;
            default:
                mirOperator = default;
                return false;
        }
    }

    private MirOperand ReportUnsupportedExpr(HirNode node)
    {
        var expressionType = node.GetType().Name;
        var diag = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.UnsupportedHirExpressionDuringMirLowering(expressionType),
            "E5330");
        if (HasSpan(node.Span))
        {
            diag.WithLabel(node.Span, DiagnosticMessages.MirFallbackLabel);
        }

        Diagnostics.Add(diag);
        return CreatePoisonOperand(node.TypeId, node.Span, DiagnosticMessages.UnsupportedHirExpressionReason(expressionType));
    }

    private MirOperand ReportUnsupportedUnaryOperator(HirUnaryOp unaryOp)
    {
        var diag = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.UnsupportedHirUnaryOperatorDuringMirLowering(unaryOp.Operator),
            "E5330");
        if (HasSpan(unaryOp.Span))
        {
            diag.WithLabel(unaryOp.Span, DiagnosticMessages.UnsupportedUnaryOperatorLabel);
        }

        Diagnostics.Add(diag);
        return CreatePoisonOperand(
            unaryOp.TypeId,
            unaryOp.Span,
            DiagnosticMessages.UnsupportedHirUnaryOperatorReason(unaryOp.Operator));
    }

    private MirOperand ReportUnsupportedBinaryOperator(HirBinOp binOp)
    {
        var diag = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.UnsupportedHirBinaryOperatorDuringMirLowering(binOp.Operator),
            "E5330");
        if (HasSpan(binOp.Span))
        {
            diag.WithLabel(binOp.Span, DiagnosticMessages.UnsupportedBinaryOperatorLabel);
        }

        Diagnostics.Add(diag);
        return CreatePoisonOperand(
            binOp.TypeId,
            binOp.Span,
            DiagnosticMessages.UnsupportedHirBinaryOperatorReason(binOp.Operator));
    }

    private MirOperand ReportHirErrorExpr(HirError error)
    {
        var reason = string.IsNullOrWhiteSpace(error.Reason)
            ? DiagnosticMessages.HirErrorNodeReachedMirLowering
            : error.Reason;
        EmitError(
            DiagnosticMessages.CannotLowerHirErrorNode(reason),
            "E5331",
            error.Span,
            DiagnosticMessages.MirPoisonLabel);
        return CreatePoisonOperand(error.TypeId, error.Span, reason);
    }

    private MirOperand ReportUnsupportedStatement(HirStatement statement)
    {
        var statementType = statement.GetType().Name;
        EmitError(
            DiagnosticMessages.UnsupportedHirStatementDuringMirLowering(statementType),
            "E5330", statement.Span, DiagnosticMessages.UnsupportedStatementLabel);
        return CreatePoisonOperand(
            TypeId.None,
            statement.Span,
            DiagnosticMessages.UnsupportedHirStatementReason(statementType));
    }

    private void ReportUnsupportedDeclaration(HirDecl declaration, SourceSpan span)
    {
        EmitError(
            DiagnosticMessages.UnsupportedDeclarationInMirBlockStatement(declaration.GetType().Name),
            "E5330", span, DiagnosticMessages.UnsupportedDeclarationLabel);
    }

    private void ReportUnsupportedAssignTarget(HirNode target, SourceSpan span)
    {
        EmitError(
            DiagnosticMessages.UnsupportedAssignmentTargetDuringMirLowering(target.GetType().Name),
            "E5330", span, DiagnosticMessages.UnsupportedAssignmentTargetLabel);
    }

    private void EmitError(string message, string code, SourceSpan span, string label)
    {
        var diagnostic = Diagnostic.Diagnostic.Error(message, code);
        if (HasSpan(span))
        {
            diagnostic.WithLabel(span, label);
        }

        Diagnostics.Add(diagnostic);
    }

    private static bool HasSpan(SourceSpan span)
    {
        return span.Length > 0 ||
               span.Location.Position > 0 ||
               span.Location.Line > 0 ||
               span.Location.Column > 0;
    }

    /// <summary>
    /// 将所有 Clone trait impl 方法的 SymbolId 加入 copy-first-argument 集合，
    /// 使 clone 调用不消费原值。
    /// </summary>
    private void PopulateCloneMethodSymbols()
    {
        if (_symbolTable == null)
        {
            return;
        }

        var cloneTraitId = _symbolTable.LookupType("Clone");
        if (!cloneTraitId.HasValue || !cloneTraitId.Value.IsValid)
        {
            return;
        }

        var cloneTraitSymbol = _symbolTable.GetSymbol<TraitSymbol>(cloneTraitId.Value);
        if (cloneTraitSymbol == null)
        {
            return;
        }

        foreach (var symbol in _symbolTable.Symbols.Values)
        {
            if (symbol is not ImplSymbol implSymbol)
            {
                continue;
            }

            if (implSymbol.Trait != cloneTraitId.Value)
            {
                continue;
            }

            foreach (var methodId in implSymbol.Methods)
            {
                _copyFirstArgumentFunctionSymbols.Add(methodId.Value);
            }
        }
    }

    /// <summary>
    /// 从 SymbolTable 收集 @cstruct 字段访问器元数据到 MirModule
    /// </summary>
    private void CollectCStructAccessors(MirModule module)
    {
        if (_symbolTable == null)
        {
            return;
        }

        foreach (var symbol in _symbolTable.Symbols.Values)
        {
            if (symbol is FuncSymbol { IsCStructAccessor: true } funcSymbol)
            {
                module.CStructAccessors[funcSymbol.Name] = new CStructAccessorInfo
                {
                    FieldOffset = funcSymbol.CStructFieldOffset,
                    FieldTypeId = funcSymbol.CStructFieldTypeId.Value,
                    IsGetter = funcSymbol.IsCStructGetter
                };
            }
        }
    }
}
