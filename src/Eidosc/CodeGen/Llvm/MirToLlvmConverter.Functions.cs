using Eidosc.Symbols;
using System.Security.Cryptography;
using System.Text;
using Eidosc.Borrow;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Ast.Types;
using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    private void SeedGenericFunctionLocalsForBlock(BlockId blockId)
    {
        _genericFunctionLocals.Clear();

        if (!_incomingGenericFunctionLocalsByBlock.TryGetValue(blockId, out var incoming))
        {
            return;
        }

        foreach (var (localId, remainingArity) in incoming)
        {
            _genericFunctionLocals[localId] = remainingArity;
        }
    }

    private void AnalyzeGenericFunctionLocalFlow(MirFunc func)
    {
        _incomingGenericFunctionLocalsByBlock.Clear();
        if (func.BasicBlocks.Count == 0 || !HasGenericFunctionFlowSeeds(func))
        {
            return;
        }

        var blocksById = func.BasicBlocks.ToDictionary(block => block.Id);
        var predecessorsByBlock = BuildPredecessorMap(func.BasicBlocks);
        var outgoingByBlock = new Dictionary<BlockId, Dictionary<LocalId, int>>();
        var pending = new Queue<BlockId>(func.BasicBlocks.Select(block => block.Id));
        var queued = new HashSet<BlockId>(pending);

        while (pending.Count > 0)
        {
            var blockId = pending.Dequeue();
            queued.Remove(blockId);
            if (!blocksById.TryGetValue(blockId, out var block))
            {
                continue;
            }

            var incoming = ComputeIncomingGenericFunctionState(blockId, func.EntryBlockId, predecessorsByBlock, outgoingByBlock);
            _incomingGenericFunctionLocalsByBlock[blockId] = incoming;
            var outgoing = ApplyGenericFunctionTransfer(block, incoming);

            if (!outgoingByBlock.TryGetValue(blockId, out var previousOutgoing) ||
                !GenericStateEquals(previousOutgoing, outgoing))
            {
                outgoingByBlock[blockId] = outgoing;
                foreach (var successor in GetSuccessors(block.Terminator))
                {
                    if (queued.Add(successor))
                    {
                        pending.Enqueue(successor);
                    }
                }
            }
        }
    }

    private static Dictionary<BlockId, List<BlockId>> BuildPredecessorMap(IReadOnlyList<MirBasicBlock> blocks)
    {
        var result = new Dictionary<BlockId, List<BlockId>>();
        foreach (var block in blocks)
        {
            if (!result.ContainsKey(block.Id))
            {
                result[block.Id] = [];
            }

            foreach (var successor in GetSuccessors(block.Terminator))
            {
                if (!result.TryGetValue(successor, out var predecessors))
                {
                    predecessors = [];
                    result[successor] = predecessors;
                }

                predecessors.Add(block.Id);
            }
        }

        return result;
    }

    private static Dictionary<LocalId, int> ComputeIncomingGenericFunctionState(
        BlockId blockId,
        BlockId entryBlockId,
        IReadOnlyDictionary<BlockId, List<BlockId>> predecessorsByBlock,
        IReadOnlyDictionary<BlockId, Dictionary<LocalId, int>> outgoingByBlock)
    {
        if (blockId.Equals(entryBlockId))
        {
            return [];
        }

        if (!predecessorsByBlock.TryGetValue(blockId, out var predecessors) || predecessors.Count == 0)
        {
            return [];
        }

        Dictionary<LocalId, int>? incoming = null;
        foreach (var predecessor in predecessors)
        {
            if (!outgoingByBlock.TryGetValue(predecessor, out var predecessorOutgoing))
            {
                return [];
            }

            if (incoming == null)
            {
                incoming = new Dictionary<LocalId, int>(predecessorOutgoing);
                continue;
            }

            MergeGenericStateIntersection(incoming, predecessorOutgoing);
            if (incoming.Count == 0)
            {
                break;
            }
        }

        return incoming ?? [];
    }

    private static bool GenericStateEquals(
        IReadOnlyDictionary<LocalId, int> left,
        IReadOnlyDictionary<LocalId, int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (localId, remainingArity) in left)
        {
            if (!right.TryGetValue(localId, out var rightRemainingArity) ||
                rightRemainingArity != remainingArity)
            {
                return false;
            }
        }

        return true;
    }

    private static void MergeGenericStateIntersection(
        Dictionary<LocalId, int> incoming,
        IReadOnlyDictionary<LocalId, int> predecessorOutgoing)
    {
        List<LocalId>? removals = null;
        foreach (var (localId, incomingRemainingArity) in incoming)
        {
            if (!predecessorOutgoing.TryGetValue(localId, out var predecessorRemainingArity) ||
                predecessorRemainingArity != incomingRemainingArity)
            {
                (removals ??= []).Add(localId);
            }
        }

        if (removals == null)
        {
            return;
        }

        foreach (var localId in removals)
        {
            incoming.Remove(localId);
        }
    }

    private Dictionary<LocalId, int> ApplyGenericFunctionTransfer(
        MirBasicBlock block,
        IReadOnlyDictionary<LocalId, int> incoming)
    {
        var state = new Dictionary<LocalId, int>(incoming);
        foreach (var instruction in block.Instructions)
        {
            ApplyGenericFunctionTransfer(instruction, state);
        }

        return state;
    }

    private bool HasGenericFunctionFlowSeeds(MirFunc func)
    {
        if (_genericFunctionSymbols.Count == 0 && _genericFunctionNames.Count == 0)
        {
            return false;
        }

        foreach (var block in func.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction switch
                    {
                        MirAssign assign => IsGenericFunctionReference(assign.Source),
                        MirStore store => IsGenericFunctionReference(store.Value),
                        MirLoad load => IsGenericFunctionReference(load.Source),
                        MirCall call => IsGenericFunctionReference(call.Function),
                        _ => false
                    })
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsGenericFunctionReference(MirOperand operand)
    {
        return operand is MirFunctionRef functionRef && IsGenericFunctionReference(functionRef);
    }

    private void ApplyGenericFunctionTransfer(MirInstruction instruction, Dictionary<LocalId, int> state)
    {
        switch (instruction)
        {
            case MirAssign { Target.Kind: PlaceKind.Local } assign:
                SetGenericLocalBindingForFlow(assign.Target.Local, assign.Source, state);
                return;
            case MirStore { Target.Kind: PlaceKind.Local } store:
                SetGenericLocalBindingForFlow(store.Target.Local, store.Value, state);
                return;
            case MirLoad { Target.Kind: PlaceKind.Local } load:
                SetGenericLocalBindingForFlow(load.Target.Local, load.Source, state);
                return;
            case MirCopy copy:
                SetGenericLocalBindingForFlow(copy.Target.Local, copy.Source, state);
                return;
            case MirMove move:
                SetGenericLocalBindingForFlow(move.Target.Local, move.Source, state);
                state.Remove(move.Source.Local);
                return;
            case MirCall { Target: { Kind: PlaceKind.Local } target } call:
                if (TryResolveGenericPartialCallForFlow(call, state, out var remainingArity))
                {
                    if (remainingArity is { } trackedArity)
                    {
                        state[target.Local] = trackedArity;
                    }
                    else
                    {
                        state.Remove(target.Local);
                    }
                }
                else
                {
                    state.Remove(target.Local);
                }
                return;
            case MirAlloc { Target.Kind: PlaceKind.Local } alloc:
                state.Remove(alloc.Target.Local);
                return;
            default:
                return;
        }
    }

    private bool TryResolveGenericPartialCallForFlow(
        MirCall call,
        IReadOnlyDictionary<LocalId, int> state,
        out int? remainingArity)
    {
        remainingArity = null;

        switch (call.Function)
        {
            case MirFunctionRef functionRef:
                if (!TryResolveGenericFunctionOperandForFlow(functionRef, state, out var functionRemainingArity))
                {
                    return false;
                }

                if (functionRemainingArity is { } trackedFunctionArity)
                {
                    remainingArity = ComputeRemainingArityAfterCall(trackedFunctionArity, call.Arguments.Count);
                }
                return true;

            case MirPlace { Kind: PlaceKind.Local } localFunction:
                if (!state.TryGetValue(localFunction.Local, out var localRemainingArity))
                {
                    return true;
                }

                remainingArity = ComputeRemainingArityAfterCall(localRemainingArity, call.Arguments.Count);
                return true;

            default:
                return false;
        }
    }

    private void SetGenericLocalBindingForFlow(
        LocalId targetLocal,
        MirOperand sourceOperand,
        Dictionary<LocalId, int> state)
    {
        if (TryResolveGenericFunctionOperandForFlow(sourceOperand, state, out var remainingArity))
        {
            if (remainingArity is { } trackedArity)
            {
                state[targetLocal] = trackedArity;
            }
            else
            {
                state.Remove(targetLocal);
            }

            return;
        }

        state.Remove(targetLocal);
    }

    private bool TryResolveGenericFunctionOperandForFlow(
        MirOperand operand,
        IReadOnlyDictionary<LocalId, int> state,
        out int? remainingArity)
    {
        switch (operand)
        {
            case MirFunctionRef functionRef:
                if (!IsGenericFunctionReference(functionRef))
                {
                    remainingArity = null;
                    return true;
                }

                remainingArity = ResolveInitialGenericRemainingArity(functionRef);
                return true;
            case MirPlace { Kind: PlaceKind.Local } localPlace:
                remainingArity = state.TryGetValue(localPlace.Local, out var localRemainingArity)
                    ? localRemainingArity
                    : null;
                return true;
            default:
                remainingArity = null;
                return false;
        }
    }

    private LlvmInstruction? TryMaterializeImmediateAssignment(MirPlace target, LlvmValue sourceValue)
    {
        if (target.Kind != PlaceKind.Local || sourceValue is not LlvmConstant constant)
        {
            return null;
        }

        var targetLocal = GetOrCreateLocal(target);

        if (constant.Type is LlvmIntType intType)
        {
            _locals.LocalMap[target.Local] = new LlvmLocal
            {
                Name = targetLocal.Name,
                Type = intType
            };

            return new LlvmBinOp
            {
                Op = "add",
                Left = new LlvmConstant
                {
                    Value = 0L,
                    Type = intType
                },
                Right = new LlvmConstant
                {
                    Value = constant.Value ?? 0L,
                    Type = intType
                },
                ResultType = intType,
                ResultName = targetLocal.Name
            };
        }

        if (constant.Type is LlvmFloatType floatType)
        {
            _locals.LocalMap[target.Local] = new LlvmLocal
            {
                Name = targetLocal.Name,
                Type = floatType
            };

            return new LlvmBinOp
            {
                Op = "fadd",
                Left = new LlvmConstant
                {
                    Value = 0.0d,
                    Type = floatType
                },
                Right = new LlvmConstant
                {
                    Value = constant.Value ?? 0.0d,
                    Type = floatType
                },
                ResultType = floatType,
                ResultName = targetLocal.Name
            };
        }

        return null;
    }

    private static string GetAliasName(LlvmValue value)
    {
        return value switch
        {
            LlvmLocal local => local.Name,
            LlvmInstructionRef { Instruction.ResultName: { Length: > 0 } resultName } => resultName,
            _ => "tmp_alias"
        };
    }

    private LlvmType InferCallReturnType(MirCall call, LlvmValue callee)
    {
        if (callee.Type is LlvmFunctionType functionType)
        {
            return functionType.ReturnType;
        }

        if (callee.Type is LlvmPointerType { ElementType: LlvmFunctionType pointeeFunctionType })
        {
            return pointeeFunctionType.ReturnType;
        }

        if (call.Target is MirPlace target)
        {
            var targetType = LowerStorageTypeIdOrReport(target.TypeId, "infer call target result");
            if (targetType is not LlvmVoidType)
            {
                return targetType;
            }

            if (_locals.LocalTypeById.TryGetValue(target.Local, out var localTypeId))
            {
                var localType = LowerStorageTypeIdOrReport(localTypeId, "infer call local result");
                if (localType is not LlvmVoidType)
                {
                    return localType;
                }
            }

            // Dynamic/indirect calls may carry erased TypeId metadata (for example TypeId.None).
            // In this case we still materialize a value result to avoid invalid `call void` IR for value-typed call sites.
            return LlvmPointerType.VoidPtr();
        }

        return LlvmVoidType.Instance;
    }

    private void RegisterFunctionType(MirFunc func)
    {
        if (string.IsNullOrEmpty(func.Name))
        {
            return;
        }

        if (func.IsExternal)
        {
            RegisterFfiFunction(func);
        }

        if (func.SymbolId.IsValid)
        {
            _funcCache.MirFunctionBySymbol[func.SymbolId] = func;
        }

        RegisterMirFunctionName(func);

        if (IsGenericSignature(func))
        {
            if (func.SymbolId.IsValid)
            {
                _genericFunctionSymbols.Add(func.SymbolId);
            }

            if (!string.IsNullOrWhiteSpace(func.Name))
            {
                _genericFunctionNames.Add(func.Name);
            }
        }

        var functionType = func.IsRuntimeWordAbi
            ? BuildRuntimeWordAbiFunctionType(func)
            : BuildFunctionType(func);
        var signatureKey = ComputeFunctionSignatureKey(functionType);
        var llvmName = ResolveOrAllocateFunctionInstanceName(func, functionType, signatureKey);
        if (func.SymbolId.IsValid && IsLlvmFunctionNameRegisteredForDifferentSymbol(llvmName, func.SymbolId))
        {
            llvmName = ResolveOrAllocateSymbolFunctionInstanceName(func, functionType, signatureKey, func.SymbolId);
        }

        if (TryGetFunctionIdLookupKey(func, out var functionIdKey))
        {
            _funcCache.FunctionTypeByFunctionId[functionIdKey] = functionType;
            _funcCache.FunctionLlvmNameByFunctionId[functionIdKey] = llvmName;
        }

        _funcCache.FunctionTypeByName[llvmName] = functionType;
        RegisterFunctionSourceInstance(func.Name, signatureKey, functionType, llvmName);
        RefreshSourceNameLookup(func.Name);
        var identitySourceName = GetFunctionSourceLookupName(func.FunctionId, func.Name);
        if (!string.Equals(identitySourceName, func.Name, StringComparison.Ordinal))
        {
            RegisterFunctionSourceInstance(identitySourceName, signatureKey, functionType, llvmName);
            RefreshSourceNameLookup(identitySourceName);
        }
        if (TryGetShortSourceFunctionName(func.Name, out var shortSourceName))
        {
            RegisterFunctionSourceInstance(shortSourceName, signatureKey, functionType, llvmName);
            RefreshSourceNameLookup(shortSourceName);
        }

        if (func.SymbolId.IsValid)
        {
            if (_funcCache.FunctionTypeBySymbol.TryGetValue(func.SymbolId, out var existingBySymbol) &&
                existingBySymbol != functionType)
            {
                Diagnostics.Add(
                    Diagnostic.Diagnostic.Warning(
                            DiagnosticMessages.FunctionSymbolMultipleLlvmSignatures(func.SymbolId.Value),
                            "W5001")
                        .WithNote(DiagnosticMessages.SourceFunctionNote(func.Name)));
            }

            _funcCache.FunctionTypeBySymbol[func.SymbolId] = functionType;
            _funcCache.FunctionLlvmNameBySymbol[func.SymbolId] = llvmName;
        }
    }

    private bool IsLlvmFunctionNameRegisteredForDifferentSymbol(string llvmName, SymbolId symbolId)
    {
        return _funcCache.FunctionLlvmNameBySymbol.Any(entry =>
            entry.Key != symbolId &&
            string.Equals(entry.Value, llvmName, StringComparison.Ordinal));
    }

    private string ResolveOrAllocateSymbolFunctionInstanceName(
        MirFunc func,
        LlvmFunctionType functionType,
        string signatureKey,
        SymbolId symbolId)
    {
        return _symbolNameAllocator.AllocateSymbolFunctionName(
            BuildFunctionNameAllocationRequest(func, functionType, signatureKey),
            _funcCache.FunctionTypeByName,
            symbolId);
    }

    private string BuildFunctionLlvmName(string sourceName, SymbolId symbolId)
    {
        if (string.IsNullOrEmpty(sourceName))
        {
            sourceName = WellKnownStrings.Keywords.Fn;
        }

        return _nameMangler.MangleFunctionName("", sourceName);
    }

    private string BuildFunctionLlvmName(MirFunc func)
    {
        var (moduleName, sourceName) = GetFunctionLlvmNameParts(func);
        return _nameMangler.MangleFunctionName(moduleName, sourceName);
    }

    private string BuildFunctionLlvmName(MirFunctionRef funcRef)
    {
        var (moduleName, sourceName) = GetFunctionLlvmNameParts(funcRef.Name);
        return _nameMangler.MangleFunctionName(moduleName, sourceName);
    }

    private string ResolveFunctionLlvmName(MirFunc func)
    {
        if (TryGetFunctionIdLookupKey(func, out var functionIdKey) &&
            _funcCache.FunctionLlvmNameByFunctionId.TryGetValue(functionIdKey, out var byFunctionId))
        {
            return byFunctionId;
        }

        if (func.SymbolId.IsValid &&
            _funcCache.FunctionLlvmNameBySymbol.TryGetValue(func.SymbolId, out var bySymbol))
        {
            return bySymbol;
        }

        if (!string.IsNullOrEmpty(func.Name) &&
            func.Name != "<anonymous>" &&
            !_funcCache.AmbiguousFunctionSourceNames.Contains(func.Name) &&
            _funcCache.FunctionLlvmNameBySourceName.TryGetValue(func.Name, out var byName))
        {
            return byName;
        }

        if (!string.IsNullOrEmpty(func.Name) && func.Name != "<anonymous>")
        {
            return BuildFunctionLlvmName(func);
        }

        return _nameMangler.NewTempName(WellKnownStrings.Keywords.Func);
    }

    private string ResolveFunctionLlvmName(MirFunctionRef funcRef)
    {
        // FFI 外部函数：使用原始 C 符号名（不加 eidos_ 前缀）
        if (TryGetExternalFfiSymbolName(funcRef.Name, funcRef.SymbolId, out var ffiName))
        {
            return ffiName;
        }

        if (TryGetRuntimeFunctionType(funcRef, out var runtimeName, out _))
        {
            return _nameMangler.MangleFunctionName("", runtimeName);
        }

        // @cstruct 字段访问器
        if (_cstructAccessors.ContainsKey(funcRef.Name))
        {
            return _nameMangler.MangleFunctionName("", funcRef.Name);
        }

        if (TryGetFunctionIdLookupKey(funcRef, out var functionIdKey) &&
            _funcCache.FunctionLlvmNameByFunctionId.TryGetValue(functionIdKey, out var byFunctionId))
        {
            return byFunctionId;
        }

        if (funcRef.SymbolId.IsValid &&
            _funcCache.FunctionLlvmNameBySymbol.TryGetValue(funcRef.SymbolId, out var bySymbol))
        {
            return bySymbol;
        }

        return BuildUnresolvedFunctionRefLlvmName(funcRef);
    }

    private string ResolveFunctionLlvmName(MirFunctionRef funcRef, LlvmFunctionType preferredType)
    {
        // FFI 外部函数：使用原始 C 符号名
        if (TryGetExternalFfiSymbolName(funcRef.Name, funcRef.SymbolId, out var ffiName))
        {
            return ffiName;
        }

        if (TryGetRuntimeFunctionType(funcRef, out var runtimeName, out _))
        {
            return _nameMangler.MangleFunctionName("", runtimeName);
        }

        // @cstruct 字段访问器
        if (_cstructAccessors.ContainsKey(funcRef.Name))
        {
            return _nameMangler.MangleFunctionName("", funcRef.Name);
        }

        if (TryGetFunctionIdLookupKey(funcRef, out var functionIdKey) &&
            _funcCache.FunctionLlvmNameByFunctionId.TryGetValue(functionIdKey, out var byFunctionId))
        {
            return byFunctionId;
        }

        if (funcRef.SymbolId.IsValid &&
            _funcCache.FunctionLlvmNameBySymbol.TryGetValue(funcRef.SymbolId, out var bySymbol))
        {
            return bySymbol;
        }

        return ResolveFunctionLlvmName(funcRef);
    }

    private static LlvmFunctionType BuildRuntimeWordAbiFunctionType(MirFunc func)
    {
        return new LlvmFunctionType
        {
            ReturnType = LlvmIntType.I64,
            ParameterTypes = func.Locals
                .Where(static local => local.IsParameter)
                .Select(static _ => (LlvmType)LlvmIntType.I64)
                .ToList()
        };
    }

    private LlvmFunctionType BuildFunctionType(MirFunc func)
    {
        var allowUnresolvedSignatureTypes = IsGenericSignature(func);

        return new LlvmFunctionType
        {
            ReturnType = LowerFunctionSignatureType(func.ReturnType, func, "return type", allowUnresolvedSignatureTypes),
            ParameterTypes = func.Locals
                .Where(local => local.IsParameter)
                .Select(local =>
                    NormalizeParameterType(
                        LowerFunctionSignatureType(
                            local.TypeId,
                            func,
                            $"parameter '{local.Name}'",
                            allowUnresolvedSignatureTypes)))
                .ToList()
        };
    }

    private LlvmType LowerFunctionSignatureType(
        TypeId typeId,
        MirFunc function,
        string role,
        bool allowUnresolvedSignatureTypes)
    {
        if (typeId.IsValid || allowUnresolvedSignatureTypes)
        {
            var lowered = LowerTypeIdOrReport(typeId, $"function signature {role}", allowUnresolvedSignatureTypes);
            return lowered is LlvmVoidType
                ? lowered
                : TypeLowering.NormalizeStorageType(lowered);
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.UnresolvedFunctionSignatureRole(role),
                "E5302")
            .WithNote(DiagnosticMessages.FunctionNote(function.Name))
            .WithNote(DiagnosticMessages.EnsureInferenceMonomorphizationBeforeLlvm);

        if (HasSpan(function.Span))
        {
            diagnostic.WithLabel(function.Span, DiagnosticMessages.UnresolvedFunctionSignatureTypeLabel);
        }

        Diagnostics.Add(diagnostic);
        return LlvmPointerType.VoidPtr();
    }

    private static LlvmType NormalizeParameterType(LlvmType type)
    {
        return type is LlvmVoidType ? LlvmIntType.I1 : type;
    }

    private static LlvmType NormalizeSignatureReturnType(LlvmType type)
    {
        return type is LlvmVoidType ? type : TypeLowering.NormalizeStorageType(type);
    }

    private LlvmFunctionType? ResolveFunctionType(MirFunctionRef funcRef)
    {
        if (TryGetRuntimeFunctionType(funcRef, out _, out var runtimeType))
        {
            return runtimeType;
        }

        if (TryGetFunctionIdLookupKey(funcRef, out var functionIdKey) &&
            _funcCache.FunctionTypeByFunctionId.TryGetValue(functionIdKey, out var byFunctionId))
        {
            return byFunctionId;
        }

        if (funcRef.SymbolId.IsValid &&
            _funcCache.FunctionTypeBySymbol.TryGetValue(funcRef.SymbolId, out var bySymbol))
        {
            return bySymbol;
        }

        var mangledName = ResolveFunctionLlvmName(funcRef);
        if (_funcCache.FunctionTypeByName.TryGetValue(mangledName, out var byMangledName))
        {
            return byMangledName;
        }

        return null;
    }

    private static bool HasStructuredFunctionIdentity(MirFunctionRef funcRef)
    {
        if (funcRef.SymbolId.IsValid)
        {
            return true;
        }

        var functionId = funcRef.FunctionId;
        return functionId?.SymbolId.IsValid == true ||
               !string.IsNullOrWhiteSpace(functionId?.QualifiedName) ||
               (!string.IsNullOrWhiteSpace(functionId?.Module) &&
                !string.IsNullOrWhiteSpace(functionId?.Name));
    }

    private string BuildUnresolvedFunctionRefLlvmName(MirFunctionRef funcRef)
    {
        var defaultName = BuildFunctionLlvmName(funcRef);
        var collidesWithInternalSourceName =
            !string.IsNullOrWhiteSpace(funcRef.Name) &&
            (_funcCache.FunctionLlvmNameBySourceName.ContainsKey(funcRef.Name) ||
             _funcCache.AmbiguousFunctionSourceNames.Contains(funcRef.Name) ||
             _funcCache.FunctionTypeBySourceAndSignature.ContainsKey(funcRef.Name));
        if (IsPermittedUnresolvedFunction(funcRef) ||
            (!_funcCache.FunctionTypeByName.ContainsKey(defaultName) && !collidesWithInternalSourceName))
        {
            return defaultName;
        }

        var unresolvedName = string.IsNullOrWhiteSpace(funcRef.Name)
            ? "anonymous"
            : SanitizeUnresolvedFunctionNameSegment(funcRef.Name);
        var identitySuffix = funcRef.SymbolId.IsValid
            ? $"s{funcRef.SymbolId.Value}"
            : funcRef.TypeId.IsValid
                ? $"t{funcRef.TypeId.Value}"
                : "noid";
        var moduleName = funcRef.FunctionId?.Module ?? string.Empty;
        return _nameMangler.MangleFunctionName(
            moduleName,
            $"__unresolved_ref__{unresolvedName}_{identitySuffix}");
    }

    private static string SanitizeUnresolvedFunctionNameSegment(string name)
    {
        var chars = name
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_')
            .ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "anonymous" : sanitized;
    }

    private static bool TryGetFunctionIdLookupKey(MirFunc function, out string key)
    {
        return TryGetFunctionIdLookupKey(function.FunctionId, function.SymbolId, out key);
    }

    private static bool TryGetFunctionIdLookupKey(MirFunctionRef functionRef, out string key)
    {
        return TryGetFunctionIdLookupKey(functionRef.FunctionId, functionRef.SymbolId, out key);
    }

    private static bool TryGetFunctionIdLookupKey(FunctionId? functionId, SymbolId fallbackSymbolId, out string key)
    {
        key = string.Empty;
        if (MirFunctionIdentity.TryGetStableKey(functionId, out key))
        {
            return true;
        }

        if (fallbackSymbolId.IsValid)
        {
            key = $"sym:{fallbackSymbolId.Value}";
            return true;
        }

        return false;
    }

    private string ResolveOrAllocateFunctionInstanceName(
        MirFunc func,
        LlvmFunctionType functionType,
        string signatureKey)
    {
        var hasFunctionIdKey = TryGetFunctionIdLookupKey(func, out var functionIdKey);
        if (hasFunctionIdKey &&
            _funcCache.FunctionLlvmNameByFunctionId.TryGetValue(functionIdKey, out var existingByFunctionId))
        {
            return existingByFunctionId;
        }

        return _symbolNameAllocator.AllocateFunctionName(
            BuildFunctionNameAllocationRequest(func, functionType, signatureKey),
            _funcCache.FunctionTypeByName);
    }

    private LlvmFunctionNameAllocationRequest BuildFunctionNameAllocationRequest(
        MirFunc func,
        LlvmFunctionType functionType,
        string signatureKey)
    {
        var (moduleName, sourceName) = GetFunctionLlvmNameParts(func);
        var hasStructuredIdentity = HasStructuredFunctionIdentity(func);
        var hasFunctionIdKey = TryGetFunctionIdLookupKey(func, out var functionIdKey);
        string? existingSourceSignatureName = null;
        if (!hasStructuredIdentity &&
            TryGetFunctionNamesBySignature(sourceName, out var namesBySignature) &&
            namesBySignature.TryGetValue(signatureKey, out var existingName))
        {
            existingSourceSignatureName = existingName;
        }

        return new LlvmFunctionNameAllocationRequest
        {
            ModuleName = moduleName,
            SourceName = sourceName,
            SignatureKey = signatureKey,
            FunctionIdKey = hasFunctionIdKey ? functionIdKey : string.Empty,
            HasStructuredIdentity = hasStructuredIdentity,
            FunctionType = functionType,
            Linkage = LlvmLinkage.External,
            ExistingSourceSignatureName = existingSourceSignatureName
        };
    }

    private static bool HasStructuredFunctionIdentity(MirFunc func)
    {
        if (func.SymbolId.IsValid)
        {
            return true;
        }

        var functionId = func.FunctionId;
        return functionId?.SymbolId.IsValid == true ||
               !string.IsNullOrWhiteSpace(functionId?.QualifiedName) ||
               (!string.IsNullOrWhiteSpace(functionId?.ModuleIdentityKey) &&
                !string.IsNullOrWhiteSpace(functionId?.Name)) ||
               (!string.IsNullOrWhiteSpace(functionId?.Module) &&
                !string.IsNullOrWhiteSpace(functionId?.Name));
    }

    private static (string ModuleName, string SourceName) GetFunctionLlvmNameParts(MirFunc func)
    {
        return GetFunctionLlvmNameParts(func.Name);
    }

    private static (string ModuleName, string SourceName) GetFunctionLlvmNameParts(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = WellKnownStrings.Keywords.Fn;
        }

        return (string.Empty, sourceName);
    }

    private static string GetFunctionSourceLookupName(FunctionId? functionId, string sourceName)
    {
        return string.IsNullOrWhiteSpace(functionId?.QualifiedName)
            ? sourceName
            : functionId!.QualifiedName;
    }

    private void RegisterFunctionSourceInstance(
        string sourceName,
        string signatureKey,
        LlvmFunctionType functionType,
        string llvmName)
    {
        if (!_funcCache.FunctionTypeBySourceAndSignature.TryGetValue(sourceName, out var typesBySignature))
        {
            typesBySignature = new Dictionary<string, LlvmFunctionType>(StringComparer.Ordinal);
            _funcCache.FunctionTypeBySourceAndSignature[sourceName] = typesBySignature;
        }

        if (!_funcCache.FunctionLlvmNameBySourceAndSignature.TryGetValue(sourceName, out var namesBySignature))
        {
            namesBySignature = new Dictionary<string, string>(StringComparer.Ordinal);
            _funcCache.FunctionLlvmNameBySourceAndSignature[sourceName] = namesBySignature;
        }

        typesBySignature[signatureKey] = functionType;
        namesBySignature[signatureKey] = llvmName;
    }

    private void RefreshSourceNameLookup(string sourceName)
    {
        if (!TryGetFunctionSourceSignatureMaps(sourceName, out var typesBySignature, out var namesBySignature))
        {
            _funcCache.FunctionLlvmNameBySourceName.Remove(sourceName);
            _funcCache.FunctionTypeByName.Remove(sourceName);
            _funcCache.AmbiguousFunctionSourceNames.Remove(sourceName);
            return;
        }

        if (typesBySignature.Count == 1 && namesBySignature.Count == 1)
        {
            var onlyType = typesBySignature.Values.First();
            var onlyName = namesBySignature.Values.First();
            _funcCache.FunctionLlvmNameBySourceName[sourceName] = onlyName;
            _funcCache.FunctionTypeByName[sourceName] = onlyType;
            _funcCache.AmbiguousFunctionSourceNames.Remove(sourceName);
            return;
        }

        _funcCache.FunctionLlvmNameBySourceName.Remove(sourceName);
        _funcCache.FunctionTypeByName.Remove(sourceName);
        _funcCache.AmbiguousFunctionSourceNames.Add(sourceName);
    }

    private bool TryResolveFunctionTypeByTypeId(string sourceName, TypeId expectedTypeId, out LlvmFunctionType functionType)
    {
        functionType = default!;

        if (!TryGetFunctionTypesBySignature(sourceName, out var typesBySignature))
        {
            return false;
        }

        if (typesBySignature.Count == 1)
        {
            functionType = typesBySignature.Values.First();
            return true;
        }

        if (!expectedTypeId.IsValid)
        {
            return false;
        }

        var candidates = FindFunctionTypeCandidatesByExpectedType(typesBySignature.Values, expectedTypeId);
        if (candidates.Count == 1)
        {
            functionType = candidates[0];
            return true;
        }

        return false;
    }

    private bool TryResolveFunctionInstanceNameByTypeId(string sourceName, TypeId expectedTypeId, out string llvmName)
    {
        llvmName = string.Empty;

        if (!TryGetFunctionSourceSignatureMaps(sourceName, out var typesBySignature, out var namesBySignature))
        {
            return false;
        }

        if (typesBySignature.Count == 1)
        {
            llvmName = namesBySignature.Values.First();
            return true;
        }

        var matchingKeys = FindFunctionSignatureKeysByExpectedType(typesBySignature, namesBySignature, expectedTypeId);
        if (matchingKeys.Count == 1)
        {
            llvmName = namesBySignature[matchingKeys[0]];
            return true;
        }

        return false;
    }

    private bool TryGetFunctionTypesBySignature(
        string sourceName,
        out Dictionary<string, LlvmFunctionType> typesBySignature)
    {
        if (_funcCache.FunctionTypeBySourceAndSignature.TryGetValue(sourceName, out var resolvedTypesBySignature) &&
            resolvedTypesBySignature.Count > 0)
        {
            typesBySignature = resolvedTypesBySignature;
            return true;
        }

        typesBySignature = default!;
        return false;
    }

    private bool TryGetFunctionNamesBySignature(
        string sourceName,
        out Dictionary<string, string> namesBySignature)
    {
        if (_funcCache.FunctionLlvmNameBySourceAndSignature.TryGetValue(sourceName, out var resolvedNamesBySignature) &&
            resolvedNamesBySignature.Count > 0)
        {
            namesBySignature = resolvedNamesBySignature;
            return true;
        }

        namesBySignature = default!;
        return false;
    }

    private bool TryGetFunctionSourceSignatureMaps(
        string sourceName,
        out Dictionary<string, LlvmFunctionType> typesBySignature,
        out Dictionary<string, string> namesBySignature)
    {
        if (TryGetFunctionTypesBySignature(sourceName, out typesBySignature) &&
            TryGetFunctionNamesBySignature(sourceName, out namesBySignature))
        {
            return true;
        }

        typesBySignature = default!;
        namesBySignature = default!;
        return false;
    }

    private bool TryResolveFunctionInstanceNameBySignature(
        string sourceName,
        LlvmFunctionType preferredType,
        out string llvmName)
    {
        llvmName = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceName) ||
            !TryGetFunctionNamesBySignature(sourceName, out var namesBySignature))
        {
            return false;
        }

        var signatureKey = ComputeFunctionSignatureKey(preferredType);
        if (!namesBySignature.TryGetValue(signatureKey, out var bySignature))
        {
            return false;
        }

        llvmName = bySignature;
        return true;
    }

    private bool TryResolveFunctionInstanceNameByRegisteredType(
        string sourceName,
        LlvmFunctionType preferredType,
        out string llvmName)
    {
        llvmName = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceName) ||
            _funcCache.AmbiguousFunctionSourceNames.Contains(sourceName) ||
            !_funcCache.FunctionLlvmNameBySourceName.TryGetValue(sourceName, out var registeredName) ||
            !_funcCache.FunctionTypeByName.TryGetValue(registeredName, out var registeredType) ||
            registeredType != preferredType)
        {
            return false;
        }

        llvmName = registeredName;
        return true;
    }

    private bool TryResolveFunctionInstanceNameByLlvmName(
        string sourceName,
        LlvmFunctionType preferredType,
        out string llvmName)
    {
        llvmName = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceName) ||
            !_funcCache.FunctionTypeByName.TryGetValue(sourceName, out var registeredType) ||
            registeredType != preferredType)
        {
            return false;
        }

        llvmName = sourceName;
        return true;
    }

    private List<LlvmFunctionType> FindFunctionTypeCandidatesByExpectedType(
        IEnumerable<LlvmFunctionType> candidateTypes,
        TypeId expectedTypeId)
    {
        if (!expectedTypeId.IsValid)
        {
            return [];
        }

        if (TryResolveSourceVisibleSignature(expectedTypeId, out var expectedFunctionType))
        {
            var exactFunctionMatches = candidateTypes
                .Where(type => type == expectedFunctionType)
                .ToList();
            if (exactFunctionMatches.Count > 0)
            {
                return exactFunctionMatches;
            }

            var compatibleShapeMatches = candidateTypes
                .Where(type =>
                    type.ReturnType == expectedFunctionType.ReturnType &&
                    type.ParameterTypes.Count == expectedFunctionType.ParameterTypes.Count &&
                    type.ParameterTypes.Zip(expectedFunctionType.ParameterTypes).All(pair =>
                        pair.First == pair.Second))
                .ToList();
            if (compatibleShapeMatches.Count > 0)
            {
                return compatibleShapeMatches;
            }
        }

        var expectedLoweredType = LowerTypeIdOrReport(expectedTypeId, "expected function type");
        if (TryFindExactFunctionTypeMatches(candidateTypes, expectedLoweredType, out var exactMatches))
        {
            return exactMatches;
        }

        var expectedReturnType = expectedLoweredType is LlvmPointerType { ElementType: LlvmFunctionType expectedPointee }
            ? expectedPointee.ReturnType
            : expectedLoweredType;
        return candidateTypes
            .Where(type => type.ReturnType == expectedReturnType)
            .ToList();
    }

    private static bool TryFindExactFunctionTypeMatches(
        IEnumerable<LlvmFunctionType> candidateTypes,
        LlvmType expectedLoweredType,
        out List<LlvmFunctionType> matches)
    {
        matches = expectedLoweredType switch
        {
            LlvmFunctionType directExpected => candidateTypes
                .Where(type => type == directExpected)
                .ToList(),
            LlvmPointerType { ElementType: LlvmFunctionType pointeeExpected } => candidateTypes
                .Where(type => type == pointeeExpected)
                .ToList(),
            _ => []
        };

        return matches.Count == 1;
    }

    private List<string> FindFunctionSignatureKeysByExpectedType(
        IReadOnlyDictionary<string, LlvmFunctionType> typesBySignature,
        IReadOnlyDictionary<string, string> namesBySignature,
        TypeId expectedTypeId)
    {
        if (!expectedTypeId.IsValid)
        {
            return [];
        }

        var matchingTypes = FindFunctionTypeCandidatesByExpectedType(typesBySignature.Values, expectedTypeId);
        if (matchingTypes.Count == 0)
        {
            return [];
        }

        var matchingTypeSet = new HashSet<LlvmFunctionType>(matchingTypes);
        return typesBySignature
            .Where(entry =>
                matchingTypeSet.Contains(entry.Value) &&
                namesBySignature.ContainsKey(entry.Key))
            .Select(entry => entry.Key)
            .ToList();
    }

    private static string ComputeFunctionSignatureKey(LlvmFunctionType functionType)
    {
        var signature = functionType.ToIrString();
        var bytes = Encoding.UTF8.GetBytes(signature);
        var hash = SHA256.HashData(bytes);
        return System.Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static bool TryGetRuntimeFunctionType(MirFunctionRef functionRef, out string name, out LlvmFunctionType type)
    {
        name = string.Empty;
        if (MirRuntimeFunctions.TryGetFunctionName(functionRef, out name))
        {
            return TryResolveRuntimeFunctionType(name, out name, out type);
        }

        if (!MirBuiltinFunctions.TryGetIntrinsicName(functionRef, out name) ||
            !IntrinsicRegistry.IsKnownIntrinsicName(name))
        {
            type = default!;
            return false;
        }

        return TryResolveRuntimeFunctionType(name, out name, out type);
    }

    private static bool TryResolveRuntimeFunctionType(string logicalName, out string name, out LlvmFunctionType type)
    {
        if (!RuntimeFunctionTypes.TryGetValue(logicalName, out type!))
        {
            name = logicalName;
            return false;
        }

        name = ResolveRuntimeFunctionLlvmSourceName(logicalName);
        return true;
    }

    private static string ResolveRuntimeFunctionLlvmSourceName(string logicalName) => logicalName switch
    {
        "cstr_to_string" => "string_from_cstr_raw",
        _ => logicalName
    };

    private static readonly Dictionary<string, LlvmFunctionType> RuntimeFunctionTypes = BuildRuntimeFunctionTypeMap();

    private static Dictionary<string, LlvmFunctionType> BuildRuntimeFunctionTypeMap()
    {
        var ptr = LlvmPointerType.VoidPtr();
        var i64 = LlvmIntType.I64;
        var f64 = LlvmFloatType.Double;
        var map = new Dictionary<string, LlvmFunctionType>(StringComparer.Ordinal);

        foreach (var declaration in IntrinsicRegistry.EmbeddedStdlibDeclarations.Values)
        {
            if (declaration.Role != BuiltinIntrinsicRole.None ||
                !TryBuildIntrinsicRuntimeFunctionType(declaration, out var functionType))
            {
                continue;
            }

            map.TryAdd(declaration.Name, functionType);
        }

        // Compiler-generated runtime helpers that are not user-facing Std intrinsics.
        Add("type_id", i64, [ptr]);
        Add("array_get", ptr, [ptr, i64]);
        Add("string_intern",  ptr, [ptr, i64]);
        Add("int_to_float",   f64, [i64]);
        Add("string_from_cstr", ptr, [ptr]);
        Add("cstr_to_string", ptr, [ptr]);
        Add("http_get_text",  ptr, [ptr]);
        Add("http_request_text", ptr, [ptr, ptr, ptr, ptr]);
        Add("http_request_text_with_headers", ptr, [ptr, ptr, ptr, ptr, ptr]);
        Add("time_format",    i64, [i64, ptr, i64, ptr]);
        Add("regex_find",     i64, [ptr, ptr, ptr, i64]);

        return map;

        void Add(string name, LlvmType ret, List<LlvmType> paramTypes)
        {
            map[name] = new LlvmFunctionType { ReturnType = ret, ParameterTypes = paramTypes };
        }
    }

    private static bool TryBuildIntrinsicRuntimeFunctionType(IntrinsicDeclaration declaration, out LlvmFunctionType functionType)
    {
        functionType = default!;
        if (!string.IsNullOrWhiteSpace(declaration.LlvmAbi) &&
            TryParseIntrinsicLlvmAbi(declaration.LlvmAbi, out functionType))
        {
            return true;
        }

        var signature = declaration.Signature;
        if (signature is not ArrowType arrow)
        {
            return false;
        }

        var parameters = new List<LlvmType>();
        var current = arrow;
        while (true)
        {
            if (!IsUnitTypeNode(current.ParamType))
            {
                parameters.Add(LowerIntrinsicAbiType(current.ParamType));
            }

            if (current.ReturnType is not ArrowType next)
            {
                functionType = new LlvmFunctionType
                {
                    ReturnType = IsUnitTypeNode(current.ReturnType)
                        ? LlvmVoidType.Instance
                        : LowerIntrinsicAbiType(current.ReturnType),
                    ParameterTypes = parameters
                };
                return true;
            }

            current = next;
        }
    }

    private static bool TryParseIntrinsicLlvmAbi(string abi, out LlvmFunctionType functionType)
    {
        functionType = default!;
        var arrowIndex = abi.IndexOf("->", StringComparison.Ordinal);
        if (arrowIndex < 0)
        {
            return false;
        }

        var parameterText = abi[..arrowIndex].Trim();
        var returnText = abi[(arrowIndex + 2)..].Trim();
        if (!TryParseIntrinsicLlvmAbiType(returnText, out var returnType))
        {
            return false;
        }

        var parameterTypes = new List<LlvmType>();
        if (!string.IsNullOrWhiteSpace(parameterText) &&
            !string.Equals(parameterText, "void", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var parameter in parameterText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParseIntrinsicLlvmAbiType(parameter, out var parameterType) ||
                    parameterType is LlvmVoidType)
                {
                    return false;
                }

                parameterTypes.Add(parameterType);
            }
        }

        functionType = new LlvmFunctionType
        {
            ReturnType = returnType,
            ParameterTypes = parameterTypes
        };
        return true;
    }

    private static bool TryParseIntrinsicLlvmAbiType(string text, out LlvmType type)
    {
        type = text.Trim().ToLowerInvariant() switch
        {
            "void" => LlvmVoidType.Instance,
            "ptr" => LlvmPointerType.VoidPtr(),
            "i1" => LlvmIntType.I1,
            "i8" => LlvmIntType.I8,
            "i32" => LlvmIntType.I32,
            "i64" => LlvmIntType.I64,
            "f64" or "double" => LlvmFloatType.Double,
            _ => null!
        };

        return type != null;
    }

    private static LlvmType LowerIntrinsicAbiType(TypeNode typeNode)
    {
        if (typeNode is TypePath typePath)
        {
            return typePath.TypeName switch
            {
                WellKnownStrings.BuiltinTypes.Bool => LlvmIntType.I1,
                WellKnownStrings.BuiltinTypes.Int => LlvmIntType.I64,
                WellKnownStrings.BuiltinTypes.Int32 => LlvmIntType.I32,
                WellKnownStrings.BuiltinTypes.Int8 => LlvmIntType.I8,
                WellKnownStrings.BuiltinTypes.Char => LlvmIntType.I32,
                WellKnownStrings.BuiltinTypes.Float => LlvmFloatType.Double,
                WellKnownStrings.BuiltinTypes.Unit => LlvmVoidType.Instance,
                _ => LlvmPointerType.VoidPtr()
            };
        }

        return LlvmPointerType.VoidPtr();
    }

    private static bool IsUnitTypeNode(TypeNode typeNode)
    {
        return typeNode is TypePath { TypeName: WellKnownStrings.BuiltinTypes.Unit };
    }

    private void ReportUnresolvedGenericCall(MirCall call, MirFunctionRef functionRef)
    {
        if (!IsGenericFunctionReference(functionRef))
        {
            return;
        }

        var initialRemainingArity = ResolveInitialGenericRemainingArity(functionRef);
        var remainingArityAfterCall = ComputeRemainingArityAfterCall(initialRemainingArity, call.Arguments.Count);
        if (remainingArityAfterCall is null)
        {
            return;
        }

        var siteKey = $"{_currentFunction?.Name}:{call.Span.Location.Position}:{call.Span.Length}:{functionRef.SymbolId.Value}:{functionRef.Name}";
        if (!_reportedGenericCallSites.Add(siteKey))
        {
            return;
        }

        var functionName = string.IsNullOrWhiteSpace(functionRef.Name) ? "<anonymous>" : functionRef.Name;
        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.GenericCallEscapedMirSpecialization(functionName),
                "E5301")
            .WithNote(DiagnosticMessages.SpecializeCallWithConcreteTypesNote)
            .WithNote(FormatRemainingGenericArityNote(remainingArityAfterCall.Value));

        if (call.Arguments.Count == 0)
        {
            diagnostic.WithNote(DiagnosticMessages.ZeroArgumentGenericPartialCannotMonomorphize);
        }

        if (TryGetSpecializationFailure(functionRef, out var specializationFailure))
        {
            AppendSpecializationFailureNotes(diagnostic, specializationFailure);
        }

        if (HasSpan(call.Span))
        {
            diagnostic.WithLabel(call.Span, DiagnosticMessages.GenericCallLabel);
        }

        if (_currentFunction != null)
        {
            diagnostic.WithNote(DiagnosticMessages.FunctionNote(_currentFunction.Name));
        }

        Diagnostics.Add(diagnostic);
    }

    private bool TryGetSpecializationFailure(
        MirFunctionRef functionRef,
        out MirSpecializationFailureInfo failure)
    {
        if (functionRef.SymbolId.IsValid &&
            _specializationFailureByTemplateKey.TryGetValue($"sym:{functionRef.SymbolId.Value}", out var symbolFailure))
        {
            failure = symbolFailure;
            return true;
        }

        if (functionRef.TraitOwnerId.IsValid &&
            !string.IsNullOrWhiteSpace(functionRef.Name) &&
            _specializationFailureByTemplateKey.TryGetValue(
                $"trait:{functionRef.TraitOwnerId.Value}:{functionRef.Name}",
                out var traitFailure))
        {
            failure = traitFailure;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(functionRef.Name) &&
            _specializationFailureByTemplateKey.TryGetValue($"name:{functionRef.Name}", out var nameFailure))
        {
            failure = nameFailure;
            return true;
        }

        failure = null!;
        return false;
    }

    private static void AppendSpecializationFailureNotes(
        Diagnostic.Diagnostic diagnostic,
        MirSpecializationFailureInfo failure)
    {
        diagnostic
            .WithNote(DiagnosticMessages.MirSpecializationFailureReasonNote(failure.Reason))
            .WithNote(DiagnosticMessages.MirSpecializationFailureSignatureNote(
                failure.TemplateKey,
                failure.SignatureKey,
                failure.PreviewName))
            .WithNote(DiagnosticMessages.MirSpecializationFailureSuggestionNote(failure.Reason))
            .WithMetadata("specialization.phase", "mir-specialization")
            .WithMetadata("specialization.reason", failure.Reason)
            .WithMetadata("specialization.templateKey", failure.TemplateKey)
            .WithMetadata("specialization.templateName", failure.TemplateName)
            .WithMetadata("specialization.signatureKey", failure.SignatureKey)
            .WithMetadata("specialization.previewName", failure.PreviewName);
    }

    private void ReportUnresolvedGenericCall(MirCall call, LocalId functionLocalId)
    {
        if (!_genericFunctionLocals.TryGetValue(functionLocalId, out var remainingArity))
        {
            return;
        }

        var siteKey = $"{_currentFunction?.Name}:{call.Span.Location.Position}:{call.Span.Length}:local:{functionLocalId.Value}";
        if (!_reportedGenericCallSites.Add(siteKey))
        {
            return;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.IndirectGenericCallEscapedMirSpecialization(functionLocalId.Value),
                "E5301")
            .WithNote(DiagnosticMessages.BindLocalFunctionToConcreteSpecializationNote)
            .WithNote(FormatRemainingGenericArityNote(remainingArity));

        if (call.Arguments.Count == 0)
        {
            diagnostic.WithNote(DiagnosticMessages.ZeroArgumentGenericPartialCannotMonomorphize);
        }

        if (HasSpan(call.Span))
        {
            diagnostic.WithLabel(call.Span, DiagnosticMessages.GenericIndirectCallLabel);
        }

        if (_currentFunction != null)
        {
            diagnostic.WithNote(DiagnosticMessages.FunctionNote(_currentFunction.Name));
        }

        Diagnostics.Add(diagnostic);
    }

    private void ReportUnresolvedDirectFunctionReference(MirCall call, MirFunctionRef functionRef)
    {
        var functionName = string.IsNullOrWhiteSpace(functionRef.Name) ? "<anonymous>" : functionRef.Name;
        var siteKey = $"{_currentFunction?.Name}:{call.Span.Location.Position}:{call.Span.Length}:{functionRef.SymbolId.Value}:{functionName}";
        if (!_reportedUnresolvedFunctionSites.Add(siteKey))
        {
            return;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.FunctionReferenceUnresolvedDuringLlvm(functionName),
                "E5304")
            .WithNote(DiagnosticMessages.OnlyRuntimeIntrinsicsMayRemainUnresolvedNote)
            .WithNote(DiagnosticMessages.EnsureResolutionMonomorphizationBeforeLlvmNote);

        if (functionRef.SymbolId.IsValid)
        {
            diagnostic.WithNote(DiagnosticMessages.SymbolIdNote(functionRef.SymbolId.Value));
        }

        if (HasSpan(call.Span))
        {
            diagnostic.WithLabel(call.Span, DiagnosticMessages.UnresolvedFunctionReferenceLabel);
        }

        if (_currentFunction != null)
        {
            diagnostic.WithNote(DiagnosticMessages.FunctionNote(_currentFunction.Name));
        }

        Diagnostics.Add(diagnostic);
    }

    private void UpdateGenericFunctionLocalBinding(LocalId targetLocal, MirOperand source)
    {
        if (TryResolveGenericFunctionOperand(source, out var remainingArity))
        {
            if (remainingArity is { } trackedArity)
            {
                SetGenericLocal(targetLocal, trackedArity);
            }
            else
            {
                ClearGenericLocal(targetLocal);
            }

            return;
        }

        ClearGenericLocal(targetLocal);
    }

    private bool TryResolveGenericFunctionOperand(MirOperand operand, out int? remainingArity)
    {
        switch (operand)
        {
            case MirFunctionRef functionRef:
                if (!IsGenericFunctionReference(functionRef))
                {
                    remainingArity = null;
                    return true;
                }

                remainingArity = ResolveInitialGenericRemainingArity(functionRef);
                return true;
            case MirPlace { Kind: PlaceKind.Local } localPlace:
                remainingArity = _genericFunctionLocals.TryGetValue(localPlace.Local, out var localRemainingArity)
                    ? localRemainingArity
                    : null;
                return true;
            default:
                remainingArity = null;
                return false;
        }
    }

    private bool IsGenericLocal(LocalId localId)
    {
        return _genericFunctionLocals.ContainsKey(localId);
    }

    private void SetGenericLocal(LocalId localId, int remainingArity)
    {
        _genericFunctionLocals[localId] = NormalizeGenericRemainingArity(remainingArity);
    }

    private void ClearGenericLocal(LocalId localId)
    {
        _genericFunctionLocals.Remove(localId);
        _borrowedProjectionLocals.Remove(localId);
    }

    private void CopyGenericLocal(LocalId targetLocal, LocalId sourceLocal)
    {
        if (_genericFunctionLocals.TryGetValue(sourceLocal, out var remainingArity))
        {
            SetGenericLocal(targetLocal, remainingArity);
        }
        else
        {
            ClearGenericLocal(targetLocal);
        }
    }

    private int ResolveInitialGenericRemainingArity(MirFunctionRef functionRef)
    {
        var functionType = ResolveFunctionType(functionRef);
        if (functionType == null)
        {
            return UnknownGenericRemainingArity;
        }

        return functionType.ParameterTypes.Count;
    }

    private static int NormalizeGenericRemainingArity(int remainingArity)
    {
        return remainingArity < 0 ? UnknownGenericRemainingArity : remainingArity;
    }

    private static string FormatRemainingGenericArityNote(int remainingArity)
    {
        return remainingArity == UnknownGenericRemainingArity
            ? DiagnosticMessages.RemainingGenericArityUnknown
            : DiagnosticMessages.RemainingGenericArity(remainingArity);
    }

    private static int? ComputeRemainingArityAfterCall(int currentRemainingArity, int argumentCount)
    {
        if (currentRemainingArity == UnknownGenericRemainingArity)
        {
            // Unknown arity generic aliases are conservatively retained only for zero-argument calls.
            return argumentCount == 0 ? UnknownGenericRemainingArity : null;
        }

        var nextRemainingArity = currentRemainingArity - argumentCount;
        return nextRemainingArity > 0 ? nextRemainingArity : null;
    }

    private bool IsGenericFunctionReference(MirFunctionRef functionRef)
    {
        if (functionRef.SymbolId.IsValid && _genericFunctionSymbols.Contains(functionRef.SymbolId))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(functionRef.Name) &&
               _genericFunctionNames.Contains(functionRef.Name);
    }

    private void RegisterMirFunctionName(MirFunc func)
    {
        if (string.IsNullOrWhiteSpace(func.Name) ||
            _funcCache.AmbiguousMirFunctionNames.Contains(func.Name))
        {
            return;
        }

        if (_funcCache.MirFunctionByName.TryGetValue(func.Name, out var existing) &&
            !ReferenceEquals(existing, func))
        {
            _funcCache.MirFunctionByName.Remove(func.Name);
            _funcCache.AmbiguousMirFunctionNames.Add(func.Name);
            return;
        }

        _funcCache.MirFunctionByName[func.Name] = func;
    }

    private static bool TryGetShortSourceFunctionName(string functionName, out string shortName)
    {
        shortName = string.Empty;
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return false;
        }

        var separator = WellKnownStrings.InternalNames.ModuleSeparator;
        var separatorIndex = functionName.LastIndexOf(separator, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return false;
        }

        var candidate = functionName[(separatorIndex + separator.Length)..];
        if (string.IsNullOrWhiteSpace(candidate) ||
            string.Equals(candidate, functionName, StringComparison.Ordinal))
        {
            return false;
        }

        shortName = candidate;
        return true;
    }

    private bool IsGenericSignature(MirFunc function)
    {
        return function.IsGenericSignature(
            _typeLowering.TypeDescriptors,
            _typeLowering.DynamicTypeKeys,
            MirGenericLocalScope.ParametersOnly);
    }

    private bool ContainsOpenLocalTypes(MirFunc function)
    {
        return function.IsGenericSignature(
            _typeLowering.TypeDescriptors,
            _typeLowering.DynamicTypeKeys,
            MirGenericLocalScope.AllLocals);
    }

    /// <summary>
    /// 从 MirPlace 获取 LocalId
    /// </summary>
    private static LocalId GetLocalId(MirPlace place)
    {
        return place.Kind == PlaceKind.Local ? place.Local : LocalId.None;
    }

    /// <summary>
    /// 获取或创建基本块映射
    /// </summary>
    private LlvmBasicBlock GetOrCreateBlock(BlockId blockId)
    {
        if (_blockMap.TryGetValue(blockId, out var llvmBlock))
        {
            return llvmBlock;
        }

        llvmBlock = new LlvmBasicBlock
        {
            Label = $"bb{blockId.Value}"
        };
        _blockMap[blockId] = llvmBlock;
        return llvmBlock;
    }

    /// <summary>
    /// 添加运行时函数声明
    /// </summary>
    private static void AddRuntimeDeclarations(LlvmModule module)
    {
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.Alloc, LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I32);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.IncRef, LlvmVoidType.Instance, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.DecRef, LlvmVoidType.Instance, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.IncRefLocal, LlvmVoidType.Instance, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.DecRefLocal, LlvmVoidType.Instance, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.IncRefShared, LlvmVoidType.Instance, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.DecRefShared, LlvmVoidType.Instance, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.AllocReuse, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I32);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.DropReuse, LlvmVoidType.Instance, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());

        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.PrintInt, LlvmVoidType.Instance, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.PrintFloat, LlvmVoidType.Instance, LlvmFloatType.Double);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.PrintString, LlvmVoidType.Instance, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.PrintNewline, LlvmVoidType.Instance);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.PrintChar, LlvmVoidType.Instance, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TypeId, LlvmIntType.I64, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ClosureNew, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64);

        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ArrayNew, LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ArrayNewWithPolicy, LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ArrayGet, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ArraySet, LlvmVoidType.Instance, LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmPointerType.VoidPtr(), LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ArrayPush, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ArrayExtend, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ArrayPop, LlvmVoidType.Instance, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ArraySwap, LlvmVoidType.Instance, LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ArrayLength, LlvmIntType.I64, LlvmPointerType.VoidPtr());

        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringLength, LlvmIntType.I64, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringFromCstr, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringIntern, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringCharAt, LlvmIntType.I64, LlvmPointerType.VoidPtr(), LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringSlice, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringEquals, LlvmIntType.I1, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringFromChar, LlvmPointerType.VoidPtr(), LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringConcat, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.IntToString, LlvmPointerType.VoidPtr(), LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.IntToFloat, LlvmFloatType.Double, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringToFloat, LlvmFloatType.Double, LlvmPointerType.VoidPtr());

        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ReadLine, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.ReadChar, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TerminalSetRaw, LlvmVoidType.Instance);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TerminalRestore, LlvmVoidType.Instance);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.SleepMs, LlvmVoidType.Instance, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.IoLastSuccess, LlvmIntType.I1);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.IoLastError, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.FileExists, LlvmIntType.I1, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.FileReadAllText, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.FileWriteAllText, LlvmIntType.I1, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());

        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpGetText, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpRequestText, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpRequestTextWithHeaders, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpRequestTextWithOptions, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpRequestBodyHexWithOptions, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpRequestTextWithBinaryBodyOptions, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpRequestBodyHexWithBinaryBodyOptions, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpLastStatusCode, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpLastEffectiveUrl, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpLastContentType, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.HttpLastHeaders, LlvmPointerType.VoidPtr());

        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.RegisterDestructor, LlvmVoidType.Instance, LlvmIntType.I32, LlvmPointerType.VoidPtr());

        // 时间操作
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TimeNow, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TimeNowMs, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TimeFormat, LlvmIntType.I64, LlvmIntType.I64, LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TimeYear, LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TimeMonth, LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TimeDay, LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TimeHour, LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TimeMinute, LlvmIntType.I64, LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.TimeSecond, LlvmIntType.I64, LlvmIntType.I64);

        // FFI 辅助函数
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringToCstr, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.StringFromCstrRaw, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.PtrNull, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.PtrIsNull, LlvmIntType.I1, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.PtrEquals, LlvmIntType.I1, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());

        // 正则操作
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.RegexCompile, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.RegexFree, LlvmVoidType.Instance, LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.RegexIsMatch, LlvmIntType.I64, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.RegexFind, LlvmIntType.I64, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64);
        AddRuntimeDeclaration(module, WellKnownStrings.Runtime.RegexFindString, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr());

        // Floating-point LLVM intrinsics. Trigonometric, logarithmic, exponential,
        // and pow operations are emitted as libm calls instead of llvm.* names.
        var mathIntrinsics1Param = new[] { "sqrt", "fabs", "ceil", "floor", "round", "trunc" };
        foreach (var fn in mathIntrinsics1Param)
        {
            AddLlvmIntrinsicDeclaration(module, $"llvm.{fn}.f64", LlvmFloatType.Double, LlvmFloatType.Double);
        }

        var mathIntrinsics2Param = new[] { "copysign", "minnum", "maxnum" };
        foreach (var fn in mathIntrinsics2Param)
        {
            AddLlvmIntrinsicDeclaration(module, $"llvm.{fn}.f64", LlvmFloatType.Double, LlvmFloatType.Double, LlvmFloatType.Double);
        }

        AddLlvmIntrinsicDeclaration(module, "llvm.fma.f64", LlvmFloatType.Double, LlvmFloatType.Double, LlvmFloatType.Double, LlvmFloatType.Double);
    }

    private static void AddRuntimeDeclaration(
        LlvmModule module,
        string name,
        LlvmType returnType,
        params LlvmType[] parameterTypes)
    {
        module.Declarations.Add(new LlvmDeclaration
        {
            Name = name,
            Origin = LlvmDeclarationOrigin.RuntimeIntrinsic,
            Type = new LlvmFunctionType
            {
                ReturnType = returnType,
                ParameterTypes = [.. parameterTypes]
            }
        });
    }

    /// <summary>
    /// 添加 LLVM intrinsic 函数声明（如 llvm.sin.f64, llvm.cos.f64 等）。
    /// 这些声明对应 LLVM 内置的 intrinsic 函数，由 LLVM 优化器和代码生成器处理。
    /// </summary>
    private static void AddLlvmIntrinsicDeclaration(
        LlvmModule module,
        string intrinsicName,
        LlvmType returnType,
        params LlvmType[] parameterTypes)
    {
        module.Declarations.Add(new LlvmDeclaration
        {
            Name = intrinsicName,
            Origin = LlvmDeclarationOrigin.LlvmIntrinsic,
            Type = new LlvmFunctionType
            {
                ReturnType = returnType,
                ParameterTypes = [.. parameterTypes]
            }
        });
    }

    private void AddRecordedExternalDeclarations(LlvmModule module)
    {
        if (_externalFunctionDeclarations.Count == 0)
        {
            return;
        }

        var existingDeclarations = new HashSet<string>(
            module.Declarations.Select(declaration => declaration.Name),
            StringComparer.Ordinal);
        var existingFunctions = new HashSet<string>(
            module.Functions.Select(function => function.Name),
            StringComparer.Ordinal);

        foreach (var (name, declarationInfo) in _externalFunctionDeclarations.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (existingDeclarations.Contains(name) || existingFunctions.Contains(name))
            {
                continue;
            }

            module.Declarations.Add(new LlvmDeclaration
            {
                Name = name,
                Type = declarationInfo.FunctionType,
                Origin = declarationInfo.Origin
            });
        }
    }

    private void ReportInvalidUnresolvedExternalDeclarations(LlvmModule module)
    {
        var unresolvedDeclarations = module.Declarations
            .Where(declaration => declaration.Origin == LlvmDeclarationOrigin.UnresolvedExternal)
            .OrderBy(declaration => declaration.Name, StringComparer.Ordinal)
            .ToList();
        if (unresolvedDeclarations.Count == 0)
        {
            return;
        }

        foreach (var declaration in unresolvedDeclarations)
        {
            var diagnostic = Diagnostic.Diagnostic.Error(
                    DiagnosticMessages.UnresolvedExternalDeclarationRetained(declaration.Name),
                    "E5305")
                .WithNote(DiagnosticMessages.InternalFunctionReferencesMustResolveBeforeLlvmNote)
                .WithNote(DiagnosticMessages.OnlyRuntimeIntrinsicsMayRemainAsDeclarationsNote)
                .WithNote(DiagnosticMessages.MissedResolutionSpecializationOrRewritingNote);

            Diagnostics.Add(diagnostic);
        }
    }
}
