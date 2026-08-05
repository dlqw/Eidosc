using Eidosc.Symbols;
using Eidosc.Mir;
using Eidosc.Borrow;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    private const long ClosureInvokeOffset = 8;
    private const long ClosureReleaseOffset = 16;
    private const long ClosurePayloadWordCountOffset = 24;
    private const long ClosurePayloadOffset = 32;

    private readonly record struct ClosurePayloadEntry(LlvmValue Value, LlvmType Type, bool IsManagedRc)
    {
        public TypeId TypeId { get; init; } = TypeId.None;
        public long Offset { get; init; }
    }

    private LlvmValue MaterializeFunctionReference(MirFunctionRef funcRef)
    {
        return MaterializeFunctionReference(funcRef, funcRef.TypeId);
    }

    private LlvmValue MaterializeFunctionReference(MirFunctionRef funcRef, TypeId visibleTypeId)
    {
        if (TryMaterializeBuiltinFunctionReference(funcRef, visibleTypeId, out var builtinReference))
        {
            return builtinReference;
        }

        if (!TryResolveClosureValueSignature(visibleTypeId, out var visibleSignature) &&
            !TryResolveClosureValueSignature(funcRef.TypeId, out visibleSignature))
        {
            if (!ShouldMaterializeFunctionReferenceWithResolvedSignature(funcRef, visibleTypeId) ||
                ResolveFunctionType(funcRef) is not { } functionSignature)
            {
                return ConvertFunctionRef(funcRef);
            }

            visibleSignature = functionSignature;
        }

        var functionName = ResolveFunctionLlvmName(funcRef, visibleSignature);
        var functionType = _funcCache.FunctionTypeByName.TryGetValue(functionName, out var resolvedFunctionType)
            ? resolvedFunctionType
            : TryResolveFunctionTypeByTypeId(funcRef.Name, funcRef.TypeId, out var specializedType)
            ? specializedType
            : visibleSignature;
        return CreateDirectClosureValue(
            new LlvmGlobal
            {
                Name = functionName,
                Type = functionType
            },
            functionType,
            [],
            [],
            [],
            visibleSignature);
    }

    private LlvmValue MaterializeFunctionReferenceArgument(
        MirFunctionRef funcRef,
        TypeId visibleTypeId,
        int argumentIndex)
    {
        if (_currentUnifiedHints == null ||
            !_currentBlockId.HasValue ||
            !_currentUnifiedHints.FunctionArgumentClosureSites.Contains(
                new UnifiedFunctionArgumentClosureSite(
                    _currentBlockId.Value,
                    _currentInstructionIndex,
                    argumentIndex)))
        {
            return MaterializeFunctionReference(funcRef, visibleTypeId);
        }

        if (TryMaterializeStackFunctionReference(funcRef, visibleTypeId, out var stackReference))
        {
            return stackReference;
        }

        return MaterializeFunctionReference(funcRef, visibleTypeId);
    }

    private bool HasStackPromotedFunctionReferenceArgument(MirCall call)
    {
        if (_currentUnifiedHints == null || !_currentBlockId.HasValue)
        {
            return false;
        }

        for (var argumentIndex = 0; argumentIndex < call.Arguments.Count; argumentIndex++)
        {
            if (call.Arguments[argumentIndex] is MirFunctionRef &&
                _currentUnifiedHints.FunctionArgumentClosureSites.Contains(
                    new UnifiedFunctionArgumentClosureSite(
                        _currentBlockId.Value,
                        _currentInstructionIndex,
                        argumentIndex)))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryMaterializeStackFunctionReference(
        MirFunctionRef funcRef,
        TypeId visibleTypeId,
        out LlvmValue value)
    {
        value = default!;
        if (!TryResolveClosureValueSignature(visibleTypeId, out var visibleSignature) &&
            !TryResolveClosureValueSignature(funcRef.TypeId, out visibleSignature))
        {
            return false;
        }

        var functionName = ResolveFunctionLlvmName(funcRef, visibleSignature);
        var functionType = _funcCache.FunctionTypeByName.TryGetValue(functionName, out var resolvedFunctionType)
            ? resolvedFunctionType
            : TryResolveFunctionTypeByTypeId(funcRef.Name, funcRef.TypeId, out var specializedType)
            ? specializedType
            : visibleSignature;
        var directFunction = new LlvmGlobal
        {
            Name = functionName,
            Type = functionType
        };
        var invokeThunk = SynthesizeDirectInvokeThunk(
            directFunction,
            functionType,
            visibleSignature,
            []);
        value = EmitStackClosureValue(invokeThunk, null, [], "argument");
        return true;
    }

    private bool ShouldMaterializeFunctionReferenceWithResolvedSignature(
        MirFunctionRef funcRef,
        TypeId visibleTypeId)
    {
        return _currentMirFunction?.IsRuntimeWordAbi != true &&
               (visibleTypeId.Value == BaseTypes.ErasedCallableId ||
                IsSyntheticLambdaFunctionRef(funcRef));
    }

    private static bool IsSyntheticLambdaFunctionRef(MirFunctionRef funcRef)
    {
        return MirSyntheticFunctions.HasRole(
            funcRef,
            MirSyntheticFunctions.LambdaRole,
            MirSyntheticFunctions.RecursiveClosureRole);
    }

    private bool TryMaterializeBuiltinFunctionReference(MirFunctionRef funcRef, TypeId visibleTypeId, out LlvmValue value)
    {
        value = default!;

        if (funcRef.CompilerSemanticRole != CompilerSemanticRole.Show ||
            !TryResolveSourceVisibleSignature(visibleTypeId, out var visibleSignature) ||
            !_typeLowering.TryGetFunctionSignature(visibleTypeId, out var parameterTypeIds, out _) ||
            visibleSignature.ParameterTypes.Count != 1 ||
            visibleSignature.ReturnType is not LlvmPointerType)
        {
            return false;
        }

        LlvmValue directFunction = parameterTypeIds[0].Value switch
        {
            BaseTypes.IntId => CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.IntToString,
                LlvmPointerType.VoidPtr(),
                [LlvmIntType.I64]),
            BaseTypes.CharId => CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.StringFromChar,
                LlvmPointerType.VoidPtr(),
                [LlvmIntType.I64]),
            BaseTypes.BoolId => new LlvmGlobal
            {
                Name = GetOrCreateBuiltinShowBoolHelper().Name,
                Type = new LlvmFunctionType
                {
                    ReturnType = LlvmPointerType.VoidPtr(),
                    ParameterTypes = [LlvmIntType.I1]
                }
            },
            _ => LlvmNullPointer.Instance
        };

        if (directFunction is LlvmNullPointer)
        {
            return false;
        }

        var fullSignature = directFunction.Type switch
        {
            LlvmFunctionType functionType => functionType,
            LlvmPointerType { ElementType: LlvmFunctionType pointedFunctionType } => pointedFunctionType,
            _ => null
        };
        if (fullSignature == null)
        {
            return false;
        }

        value = CreateDirectClosureValue(
            directFunction,
            fullSignature,
            [],
            [],
            [],
            visibleSignature);
        return true;
    }

    private LlvmValue MaterializePartialClosureValue(MirPlace place, PartialCallState partial)
    {
        if (_locals.LocalMap.TryGetValue(place.Local, out var existingMaterialized) &&
            existingMaterialized.Type is LlvmPointerType)
        {
            return existingMaterialized;
        }

        LlvmValue closureValue = partial.Function switch
        {
            LlvmGlobal directFunction => CreateDirectClosureValue(
                directFunction,
                partial.Signature,
                partial.BoundArguments,
                partial.BoundArgumentManagedFlags,
                partial.BoundArgumentTypeIds,
                partial.VisibleSignature ?? BuildRemainingSignature(partial.Signature, partial.BoundArguments.Count)),
            _ => CreateNestedClosureValue(
                partial.Function,
                partial.BoundArguments,
                partial.BoundArgumentManagedFlags,
                partial.BoundArgumentTypeIds,
                partial.VisibleSignature ?? BuildRemainingSignature(partial.Signature, partial.BoundArguments.Count))
        };

        _partialCallStates.Remove(place.Local);
        _locals.RuntimeWordLocals.Remove(place.Local);
        _locals.LocalMap[place.Local] = new LlvmLocal
        {
            Name = GetAliasName(closureValue),
            Type = LlvmPointerType.VoidPtr()
        };
        return closureValue;
    }

    private LlvmValue CreateDirectClosureValue(
        LlvmValue directFunction,
        LlvmFunctionType fullSignature,
        IReadOnlyList<LlvmValue> boundArguments,
        IReadOnlyList<bool> boundArgumentManagedFlags,
        IReadOnlyList<TypeId> boundArgumentTypeIds,
        LlvmFunctionType visibleSignature)
    {
        var payload = LayoutClosurePayload(boundArguments
            .Select((argument, index) => new ClosurePayloadEntry(
                argument,
                fullSignature.ParameterTypes[index],
                boundArgumentManagedFlags.Count > index
                    ? boundArgumentManagedFlags[index]
                    : IsManagedRcPayloadValue(argument, fullSignature.ParameterTypes[index]))
            {
                TypeId = boundArgumentTypeIds.Count > index
                    ? boundArgumentTypeIds[index]
                    : TypeId.None
            })
            .ToList());

        var invokeThunk = SynthesizeDirectInvokeThunk(
            directFunction,
            fullSignature,
            visibleSignature,
            payload);
        var releaseThunk = SynthesizeReleaseThunk(payload);
        return EmitClosureAllocation(invokeThunk, releaseThunk, payload);
    }

    private LlvmValue CreateNestedClosureValue(
        LlvmValue rootClosure,
        IReadOnlyList<LlvmValue> boundArguments,
        IReadOnlyList<bool> boundArgumentManagedFlags,
        IReadOnlyList<TypeId> boundArgumentTypeIds,
        LlvmFunctionType visibleSignature)
    {
        var payload = new List<ClosurePayloadEntry>(boundArguments.Count + 1)
        {
            new(rootClosure, LlvmPointerType.VoidPtr(), true)
        };
        payload.AddRange(boundArguments.Select((argument, index) => new ClosurePayloadEntry(
            argument,
            argument.Type,
            boundArgumentManagedFlags.Count > index
                ? boundArgumentManagedFlags[index]
                : IsManagedRcPayloadValue(argument, argument.Type))
        {
            TypeId = boundArgumentTypeIds.Count > index
                ? boundArgumentTypeIds[index]
                : TypeId.None
        }));

        payload = LayoutClosurePayload(payload);
        var invokeThunk = SynthesizeNestedInvokeThunk(
            visibleSignature,
            payload);
        var releaseThunk = SynthesizeReleaseThunk(payload);
        return EmitClosureAllocation(invokeThunk, releaseThunk, payload);
    }

    private LlvmValue EmitClosureAllocation(
        LlvmFunction invokeThunk,
        LlvmFunction? releaseThunk,
        IReadOnlyList<ClosurePayloadEntry> payload)
    {
        var invokePtr = new LlvmCast
        {
            Op = WellKnownStrings.InternalNames.Bitcast,
            Value = new LlvmGlobal
            {
                Name = invokeThunk.Name,
                Type = new LlvmPointerType { ElementType = BuildFunctionTypeFromLlvmFunction(invokeThunk) }
            },
            TargetType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("closure_invoke_ptr")
        };
        _currentBlock?.Instructions.Add(invokePtr);

        LlvmValue releaseValue;
        if (releaseThunk == null)
        {
            releaseValue = LlvmNullPointer.Instance;
        }
        else
        {
            var releasePtr = new LlvmCast
            {
                Op = WellKnownStrings.InternalNames.Bitcast,
                Value = new LlvmGlobal
                {
                    Name = releaseThunk.Name,
                    Type = new LlvmPointerType
                    {
                        ElementType = new LlvmFunctionType
                        {
                            ReturnType = LlvmVoidType.Instance,
                            ParameterTypes = [LlvmPointerType.VoidPtr()]
                        }
                    }
                },
                TargetType = LlvmPointerType.VoidPtr(),
                ResultName = _nameMangler.NewTempName("closure_release_ptr")
            };
            _currentBlock?.Instructions.Add(releasePtr);
            releaseValue = new LlvmInstructionRef
            {
                Instruction = releasePtr,
                Type = LlvmPointerType.VoidPtr()
            };
        }

        var allocation = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.ClosureNew,
                LlvmPointerType.VoidPtr(),
                [LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64]),
            Arguments =
            [
                new LlvmInstructionRef { Instruction = invokePtr, Type = LlvmPointerType.VoidPtr() },
                releaseValue,
                new LlvmConstant { Value = ComputeClosurePayloadWordCount(payload), Type = LlvmIntType.I64 }
            ],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName(WellKnownStrings.InternalNames.Closure)
        };
        _currentBlock?.Instructions.Add(allocation);

        var closureRef = new LlvmInstructionRef
        {
            Instruction = allocation,
            Type = LlvmPointerType.VoidPtr()
        };

        for (var index = 0; index < payload.Count; index++)
        {
            var entry = payload[index];
            if (entry.TypeId.IsValid && PayloadContainsManagedRc(entry.TypeId))
            {
                EmitRetainManagedPayloadValue(entry.TypeId, entry.Value, entry.Type);
            }
            else if (entry.IsManagedRc)
            {
                _currentBlock?.Instructions.Add(CreateRuntimeRcCall(WellKnownStrings.Runtime.IncRefLocal, entry.Value));
            }

            var slotPtr = EmitClosureFieldPointer(closureRef, ClosurePayloadOffset + entry.Offset, $"closure_slot_{index}");
            _currentBlock?.Instructions.Add(new LlvmStore
            {
                Value = CoerceValueToType(entry.Value, entry.Type, $"closure_payload_{index}"),
                Pointer = slotPtr
            });
        }

        return closureRef;
    }

    private LlvmCall EmitClosureInvokeCall(
        MirCall call,
        LlvmValue closureValue,
        IReadOnlyList<LlvmValue> arguments,
        LlvmFunctionType visibleSignature)
    {
        var invokeSignature = BuildClosureInvokeFunctionType(visibleSignature);
        var invokePtr = LoadClosureInvokePointer(closureValue, invokeSignature, "invoke");
        var callArguments = new List<LlvmValue>(arguments.Count + 1) { CoerceToPointer(closureValue) };
        callArguments.AddRange(CoerceArgumentsForSignature(visibleSignature, arguments));
        return EmitDirectCall(call, invokePtr, callArguments, visibleSignature.ReturnType);
    }

    private LlvmInstructionRef LoadClosureInvokePointer(
        LlvmValue closureValue,
        LlvmFunctionType invokeSignature,
        string tempPrefix)
    {
        var invokeFieldPtr = EmitClosureFieldPointer(closureValue, ClosureInvokeOffset, $"{tempPrefix}_field");
        var load = new LlvmLoad
        {
            Pointer = invokeFieldPtr,
            LoadType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName($"{tempPrefix}_raw")
        };
        _currentBlock?.Instructions.Add(load);

        var cast = new LlvmCast
        {
            Op = WellKnownStrings.InternalNames.Bitcast,
            Value = new LlvmInstructionRef
            {
                Instruction = load,
                Type = LlvmPointerType.VoidPtr()
            },
            TargetType = new LlvmPointerType { ElementType = invokeSignature },
            ResultName = _nameMangler.NewTempName($"{tempPrefix}_typed")
        };
        _currentBlock?.Instructions.Add(cast);

        return new LlvmInstructionRef
        {
            Instruction = cast,
            Type = new LlvmPointerType { ElementType = invokeSignature }
        };
    }

    private LlvmInstructionRef EmitClosureFieldPointer(LlvmValue closureValue, long offset, string tempPrefix)
    {
        var gep = new LlvmGetElementPtr
        {
            Pointer = CoerceToPointer(closureValue),
            ElementType = LlvmIntType.I8,
            Index = new LlvmConstant
            {
                Value = offset,
                Type = LlvmIntType.I64
            },
            ResultName = _nameMangler.NewTempName(tempPrefix)
        };
        _currentBlock?.Instructions.Add(gep);
        return new LlvmInstructionRef
        {
            Instruction = gep,
            Type = LlvmPointerType.VoidPtr()
        };
    }

    private LlvmFunction SynthesizeDirectInvokeThunk(
        LlvmValue directFunction,
        LlvmFunctionType fullSignature,
        LlvmFunctionType visibleSignature,
        IReadOnlyList<ClosurePayloadEntry> payload)
    {
        var thunk = new LlvmFunction
        {
            Name = $"{WellKnownStrings.Mangling.Prefix}closure_invoke_{++_closureThunkCounter}",
            ReturnType = visibleSignature.ReturnType,
            Linkage = LlvmLinkage.Private
        };

        thunk.Parameters.Add(new LlvmParameter
        {
            Name = WellKnownStrings.InternalNames.Closure,
            Type = LlvmPointerType.VoidPtr()
        });

        for (var index = 0; index < visibleSignature.ParameterTypes.Count; index++)
        {
            thunk.Parameters.Add(new LlvmParameter
            {
                Name = $"arg{index}",
                Type = NormalizeParameterType(visibleSignature.ParameterTypes[index])
            });
        }

        var previousFunction = _currentFunction;
        var previousBlock = _currentBlock;
        try
        {
            _currentFunction = thunk;
            var entry = new LlvmBasicBlock { Label = WellKnownStrings.InternalNames.Entry };
            thunk.BasicBlocks.Add(entry);
            _currentBlock = entry;

            var callArguments = new List<LlvmValue>(fullSignature.ParameterTypes.Count);
            for (var index = 0; index < payload.Count; index++)
            {
                var entryInfo = payload[index];
                callArguments.Add(LoadClosurePayloadValue(entryInfo.Type, entryInfo.Offset, "direct_payload"));
            }

            for (var index = 0; index < visibleSignature.ParameterTypes.Count; index++)
            {
                callArguments.Add(new LlvmLocal
                {
                    Name = $"arg{index}",
                    Type = NormalizeParameterType(visibleSignature.ParameterTypes[index])
                });
            }

            if (callArguments.Count < fullSignature.ParameterTypes.Count &&
                visibleSignature.ReturnType is LlvmPointerType)
            {
                var boundArgumentManagedFlags = callArguments
                    .Select((argument, index) => IsManagedRcPayloadValue(argument, fullSignature.ParameterTypes[index]))
                    .ToList();
                var partialClosure = CreateDirectClosureValue(
                    directFunction,
                    fullSignature,
                    callArguments,
                    boundArgumentManagedFlags,
                    payload.Select(static entry => entry.TypeId)
                        .Concat(Enumerable.Repeat(TypeId.None, visibleSignature.ParameterTypes.Count))
                        .ToList(),
                    BuildRemainingSignature(fullSignature, callArguments.Count));
                entry.Terminator = new LlvmRet
                {
                    Value = CoerceValueToType(partialClosure, visibleSignature.ReturnType, "direct_closure_partial")
                };
            }
            else
            {
                for (var index = 0; index < payload.Count; index++)
                {
                    RetainClosurePayloadForOwnedCall(payload[index], callArguments[index]);
                }

                var result = new LlvmCall
                {
                    Function = directFunction,
                    Arguments = CoerceArgumentsForSignature(fullSignature, callArguments),
                    ReturnType = fullSignature.ReturnType,
                    ResultName = fullSignature.ReturnType is LlvmVoidType ? null : _nameMangler.NewTempName("direct_closure_call")
                };
                entry.Instructions.Add(result);
                entry.Terminator = fullSignature.ReturnType is LlvmVoidType
                    ? new LlvmRet()
                    : new LlvmRet
                    {
                        Value = CoerceValueToType(
                            new LlvmInstructionRef
                            {
                                Instruction = result,
                                Type = fullSignature.ReturnType
                            },
                            visibleSignature.ReturnType,
                            "direct_closure_ret")
                    };
            }
        }
        finally
        {
            _currentFunction = previousFunction;
            _currentBlock = previousBlock;
        }

        _synthesizedClosureHelpers.Add(thunk);
        return thunk;
    }

    private LlvmFunction SynthesizeNestedInvokeThunk(
        LlvmFunctionType visibleSignature,
        IReadOnlyList<ClosurePayloadEntry> payload)
    {
        var thunk = new LlvmFunction
        {
            Name = $"{WellKnownStrings.Mangling.Prefix}closure_invoke_{++_closureThunkCounter}",
            ReturnType = visibleSignature.ReturnType,
            Linkage = LlvmLinkage.Private
        };

        thunk.Parameters.Add(new LlvmParameter
        {
            Name = WellKnownStrings.InternalNames.Closure,
            Type = LlvmPointerType.VoidPtr()
        });

        for (var index = 0; index < visibleSignature.ParameterTypes.Count; index++)
        {
            thunk.Parameters.Add(new LlvmParameter
            {
                Name = $"arg{index}",
                Type = NormalizeParameterType(visibleSignature.ParameterTypes[index])
            });
        }

        var previousFunction = _currentFunction;
        var previousBlock = _currentBlock;
        try
        {
            _currentFunction = thunk;
            var entry = new LlvmBasicBlock { Label = WellKnownStrings.InternalNames.Entry };
            thunk.BasicBlocks.Add(entry);
            _currentBlock = entry;

            var rootEntry = payload[0];
            var nestedClosure = LoadClosurePayloadValue(rootEntry.Type, rootEntry.Offset, "nested_root");
            var nestedInvoke = LoadClosureInvokePointer(
                nestedClosure,
                BuildClosureInvokeFunctionType(visibleSignature),
                "nested_invoke");

            var callArguments = new List<LlvmValue>(payload.Count + visibleSignature.ParameterTypes.Count)
            {
                CoerceToPointer(nestedClosure)
            };

            for (var index = 1; index < payload.Count; index++)
            {
                var entryInfo = payload[index];
                var argument = LoadClosurePayloadValue(entryInfo.Type, entryInfo.Offset, "nested_payload");
                RetainClosurePayloadForOwnedCall(entryInfo, argument);
                callArguments.Add(argument);
            }

            for (var index = 0; index < visibleSignature.ParameterTypes.Count; index++)
            {
                callArguments.Add(new LlvmLocal
                {
                    Name = $"arg{index}",
                    Type = NormalizeParameterType(visibleSignature.ParameterTypes[index])
                });
            }

            var result = new LlvmCall
            {
                Function = nestedInvoke,
                Arguments = callArguments,
                ReturnType = visibleSignature.ReturnType,
                ResultName = visibleSignature.ReturnType is LlvmVoidType ? null : _nameMangler.NewTempName("nested_closure_call")
            };
            entry.Instructions.Add(result);
            entry.Terminator = visibleSignature.ReturnType is LlvmVoidType
                ? new LlvmRet()
                : new LlvmRet
                {
                    Value = CoerceValueToType(
                        new LlvmInstructionRef
                        {
                            Instruction = result,
                            Type = visibleSignature.ReturnType
                        },
                        visibleSignature.ReturnType,
                        "nested_closure_ret")
                };
        }
        finally
        {
            _currentFunction = previousFunction;
            _currentBlock = previousBlock;
        }

        _synthesizedClosureHelpers.Add(thunk);
        return thunk;
    }

    private void RetainClosurePayloadForOwnedCall(ClosurePayloadEntry entry, LlvmValue value)
    {
        RetainManagedClosureArgument(entry.TypeId, entry.IsManagedRc, value, entry.Type);
    }

    private void RetainManagedClosureArgument(
        TypeId typeId,
        bool isManagedRc,
        LlvmValue value,
        LlvmType storageType)
    {
        if (typeId.IsValid && PayloadContainsManagedRc(typeId))
        {
            EmitRetainManagedPayloadValue(typeId, value, storageType);
            return;
        }

        if (isManagedRc)
        {
            _currentBlock?.Instructions.Add(
                CreateRuntimeRcCall(WellKnownStrings.Runtime.IncRefLocal, value));
        }
    }

    private LlvmFunction? SynthesizeReleaseThunk(IReadOnlyList<ClosurePayloadEntry> payload)
    {
        var managedIndexes = payload
            .Select((entry, index) => (entry, index))
            .Where(pair => pair.entry.IsManagedRc ||
                           (pair.entry.TypeId.IsValid && PayloadContainsManagedRc(pair.entry.TypeId)))
            .ToList();
        if (managedIndexes.Count == 0)
        {
            return null;
        }

        var thunk = new LlvmFunction
        {
            Name = $"{WellKnownStrings.Mangling.Prefix}closure_release_{++_closureThunkCounter}",
            ReturnType = LlvmVoidType.Instance,
            Linkage = LlvmLinkage.Private
        };
        thunk.Parameters.Add(new LlvmParameter
        {
            Name = WellKnownStrings.InternalNames.Closure,
            Type = LlvmPointerType.VoidPtr()
        });

        var previousFunction = _currentFunction;
        var previousBlock = _currentBlock;
        try
        {
            _currentFunction = thunk;
            var entry = new LlvmBasicBlock { Label = WellKnownStrings.InternalNames.Entry };
            thunk.BasicBlocks.Add(entry);
            _currentBlock = entry;

            var closure = new LlvmLocal
            {
                Name = WellKnownStrings.InternalNames.Closure,
                Type = LlvmPointerType.VoidPtr()
            };
            foreach (var (entryInfo, index) in managedIndexes)
            {
                if (entryInfo.TypeId.IsValid && PayloadContainsManagedRc(entryInfo.TypeId))
                {
                    var slotPointer = EmitClosureFieldPointer(
                        closure,
                        ClosurePayloadOffset + entryInfo.Offset,
                        $"release_payload_{index}_slot");
                    EmitReleaseManagedPayloadFromPointer(
                        entry,
                        slotPointer,
                        entryInfo.TypeId,
                        entryInfo.Type,
                        $"closure_payload_{index}");
                }
                else
                {
                    var payloadValue = LoadClosurePayloadValue(entryInfo.Type, entryInfo.Offset, "release_payload");
                    entry.Instructions.Add(CreateRuntimeRcCall(WellKnownStrings.Runtime.DecRefLocal, payloadValue));
                }
            }

            entry.Terminator = new LlvmRet();
        }
        finally
        {
            _currentFunction = previousFunction;
            _currentBlock = previousBlock;
        }

        _synthesizedClosureHelpers.Add(thunk);
        return thunk;
    }

    private LlvmFunction GetOrCreateBuiltinShowBoolHelper()
    {
        if (_builtinShowBoolHelper != null)
        {
            return _builtinShowBoolHelper;
        }

        var helper = new LlvmFunction
        {
            Name = WellKnownStrings.Runtime.ShowBool,
            ReturnType = LlvmPointerType.VoidPtr(),
            Linkage = LlvmLinkage.Private
        };
        helper.Parameters.Add(new LlvmParameter
        {
            Name = "value",
            Type = LlvmIntType.I1
        });

        var previousFunction = _currentFunction;
        var previousBlock = _currentBlock;
        try
        {
            _currentFunction = helper;
            var entry = new LlvmBasicBlock { Label = WellKnownStrings.InternalNames.Entry };
            helper.BasicBlocks.Add(entry);
            _currentBlock = entry;

            var condition = new LlvmLocal
            {
                Name = "value",
                Type = LlvmIntType.I1
            };
            var trueCstr = CreateCStringPointer(WellKnownStrings.AdditionalKeywords.True, "show_true");
            var falseCstr = CreateCStringPointer(WellKnownStrings.AdditionalKeywords.False, "show_false");
            var select = new LlvmSelect
            {
                Condition = condition,
                TrueValue = trueCstr,
                FalseValue = falseCstr,
                ResultName = _nameMangler.NewTempName("show_bool_cstr")
            };
            entry.Instructions.Add(select);

            var runtimeCall = new LlvmCall
            {
                Function = CreateRuntimeFunctionGlobal(
                    WellKnownStrings.Runtime.StringFromCstr,
                    LlvmPointerType.VoidPtr(),
                    [LlvmPointerType.VoidPtr()]),
                Arguments =
                [
                    new LlvmInstructionRef
                    {
                        Instruction = select,
                        Type = LlvmPointerType.VoidPtr()
                    }
                ],
                ReturnType = LlvmPointerType.VoidPtr(),
                ResultName = _nameMangler.NewTempName("show_bool")
            };
            entry.Instructions.Add(runtimeCall);
            entry.Terminator = new LlvmRet
            {
                Value = new LlvmInstructionRef
                {
                    Instruction = runtimeCall,
                    Type = LlvmPointerType.VoidPtr()
                }
            };
        }
        finally
        {
            _currentFunction = previousFunction;
            _currentBlock = previousBlock;
        }

        _synthesizedClosureHelpers.Add(helper);
        _builtinShowBoolHelper = helper;
        return helper;
    }

    private LlvmFunction GetOrCreateErasedShowHelper()
    {
        if (_erasedShowHelper != null)
        {
            return _erasedShowHelper;
        }

        var helper = new LlvmFunction
        {
            Name = WellKnownStrings.Runtime.Show,
            ReturnType = LlvmPointerType.VoidPtr(),
            Linkage = LlvmLinkage.Private
        };
        helper.Parameters.Add(new LlvmParameter
        {
            Name = "value",
            Type = LlvmPointerType.VoidPtr()
        });

        var previousFunction = _currentFunction;
        var previousBlock = _currentBlock;
        try
        {
            _currentFunction = helper;
            var entry = new LlvmBasicBlock { Label = WellKnownStrings.InternalNames.Entry };
            helper.BasicBlocks.Add(entry);
            _currentBlock = entry;

            var erasedLabel = CreateCStringPointer("<opaque>", "show_erased");
            var runtimeCall = new LlvmCall
            {
                Function = CreateRuntimeFunctionGlobal(
                    WellKnownStrings.Runtime.StringFromCstr,
                    LlvmPointerType.VoidPtr(),
                    [LlvmPointerType.VoidPtr()]),
                Arguments = [erasedLabel],
                ReturnType = LlvmPointerType.VoidPtr(),
                ResultName = _nameMangler.NewTempName("show_erased")
            };
            entry.Instructions.Add(runtimeCall);
            entry.Terminator = new LlvmRet
            {
                Value = new LlvmInstructionRef
                {
                    Instruction = runtimeCall,
                    Type = LlvmPointerType.VoidPtr()
                }
            };
        }
        finally
        {
            _currentFunction = previousFunction;
            _currentBlock = previousBlock;
        }

        _synthesizedClosureHelpers.Add(helper);
        _erasedShowHelper = helper;
        return helper;
    }

    private LlvmValue LoadClosurePayloadValue(LlvmType payloadType, long payloadOffset, string tempPrefix)
    {
        var closureParam = new LlvmLocal
        {
            Name = WellKnownStrings.InternalNames.Closure,
            Type = LlvmPointerType.VoidPtr()
        };
        var slotPtr = EmitClosureFieldPointer(closureParam, ClosurePayloadOffset + payloadOffset, $"{tempPrefix}_{payloadOffset}");
        var load = new LlvmLoad
        {
            Pointer = slotPtr,
            LoadType = payloadType,
            ResultName = _nameMangler.NewTempName($"{tempPrefix}_{payloadOffset}_load")
        };
        _currentBlock?.Instructions.Add(load);
        return new LlvmInstructionRef
        {
            Instruction = load,
            Type = payloadType
        };
    }

    private static List<ClosurePayloadEntry> LayoutClosurePayload(List<ClosurePayloadEntry> payload)
    {
        var offset = 0L;
        for (var index = 0; index < payload.Count; index++)
        {
            payload[index] = payload[index] with { Offset = offset };
            offset += AlignConstructorPayloadSize(Math.Max(1L, GetLlvmStorageSize(payload[index].Type)));
        }

        return payload;
    }

    private static long ComputeClosurePayloadByteSize(IReadOnlyList<ClosurePayloadEntry> payload)
    {
        if (payload.Count == 0)
        {
            return 0;
        }

        var last = payload[^1];
        return last.Offset + AlignConstructorPayloadSize(Math.Max(1L, GetLlvmStorageSize(last.Type)));
    }

    private static long ComputeClosurePayloadWordCount(IReadOnlyList<ClosurePayloadEntry> payload) =>
        ComputeClosurePayloadByteSize(payload) / 8L;

    private bool IsManagedRcPayloadValue(MirOperand operand, LlvmValue value, LlvmType type)
    {
        if (operand.TypeId.IsValid)
        {
            return IsManagedRcType(operand.TypeId) &&
                   IsManagedRcPayloadValue(value, type);
        }

        return IsManagedRcPayloadValue(value, type);
    }

    private static bool IsManagedRcPayloadValue(LlvmValue value, LlvmType type)
    {
        if (type is not LlvmPointerType)
        {
            return false;
        }

        return value switch
        {
            LlvmNullPointer => false,
            LlvmInstructionRef { Instruction: LlvmCast { Op: "inttoptr" } } => false,
            LlvmConstant { Value: 0 or null } => false,
            _ => true
        };
    }

    private static LlvmFunctionType BuildRemainingSignature(LlvmFunctionType fullSignature, int boundArgumentCount)
    {
        return new LlvmFunctionType
        {
            ReturnType = fullSignature.ReturnType,
            ParameterTypes = fullSignature.ParameterTypes.Skip(boundArgumentCount).ToList()
        };
    }

    private static LlvmFunctionType BuildClosureInvokeFunctionType(LlvmFunctionType visibleSignature)
    {
        return new LlvmFunctionType
        {
            ReturnType = visibleSignature.ReturnType,
            ParameterTypes = [LlvmPointerType.VoidPtr(), .. visibleSignature.ParameterTypes.Select(NormalizeParameterType)]
        };
    }

    private static LlvmFunctionType BuildFunctionTypeFromLlvmFunction(LlvmFunction function)
    {
        return new LlvmFunctionType
        {
            ReturnType = function.ReturnType,
            ParameterTypes = function.Parameters.Select(parameter => parameter.Type).ToList()
        };
    }
}
