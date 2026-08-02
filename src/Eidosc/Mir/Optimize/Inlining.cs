using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// 函数内联优化 - 内联小型单块函数以减少调用开销。
/// 支持局部变量重映射、参数绑定和返回值处理。
/// </summary>
public sealed class Inlining : IMirOptimizationPass
{
    private readonly int _maxInlineSize;

    public string Name => "Inlining";

    public Inlining() : this(30) { }

    public Inlining(int maxInlineSize)
    {
        _maxInlineSize = maxInlineSize;
    }

    public MirModule Run(MirModule module)
    {
        // Find inline candidates: non-recursive, single-block, small functions
        var inlineCandidatesBySymbol = new Dictionary<SymbolId, MirFunc>();
        var inlineCandidatesByIdentity = new Dictionary<string, MirFunc>(StringComparer.Ordinal);
        var ambiguousInlineCandidateIdentities = new HashSet<string>(StringComparer.Ordinal);
        var inlineCandidatesByName = new Dictionary<string, MirFunc>(StringComparer.Ordinal);
        var ambiguousInlineCandidateNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in module.Functions.Where(ShouldInline))
        {
            if (function.SymbolId.IsValid)
            {
                inlineCandidatesBySymbol[function.SymbolId] = function;
            }

            if (TryRegisterInlineCandidateIdentity(
                    function,
                    inlineCandidatesByIdentity,
                    ambiguousInlineCandidateIdentities))
            {
                continue;
            }

            if (function.SymbolId.IsValid ||
                string.IsNullOrWhiteSpace(function.Name) ||
                ambiguousInlineCandidateNames.Contains(function.Name))
            {
                continue;
            }

            if (inlineCandidatesByName.TryGetValue(function.Name, out var existing) &&
                !ReferenceEquals(existing, function))
            {
                inlineCandidatesByName.Remove(function.Name);
                ambiguousInlineCandidateNames.Add(function.Name);
                continue;
            }

            inlineCandidatesByName[function.Name] = function;
        }

        if (inlineCandidatesBySymbol.Count == 0 &&
            inlineCandidatesByIdentity.Count == 0 &&
            inlineCandidatesByName.Count == 0)
            return module;

        var optimizedFunctions = new List<MirFunc>();
        foreach (var func in module.Functions)
        {
            // Don't inline into the candidate itself (prevents duplication issues)
            if (!IsInlineCandidate(func, inlineCandidatesBySymbol, inlineCandidatesByIdentity, inlineCandidatesByName))
            {
                optimizedFunctions.Add(InlineCalls(func, inlineCandidatesBySymbol, inlineCandidatesByIdentity, inlineCandidatesByName));
            }
            else
            {
                optimizedFunctions.Add(func);
            }
        }

        return new MirModule
        {
            Name = module.Name,
            PackageAlias = module.PackageAlias,
            PackageInstanceKey = module.PackageInstanceKey,
            Path = module.Path.ToList(),
            Functions = optimizedFunctions,
            DynamicTypeKeys = new Dictionary<int, string>(module.DynamicTypeKeys),
            TypeDescriptors = new Dictionary<int, TypeDescriptor>(module.TypeDescriptors),
            CStructAccessors = new Dictionary<string, CStructAccessorInfo>(module.CStructAccessors),
            ConstructorLayouts = module.ConstructorLayouts.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToList()),
            CopyLikeTypeIds = new HashSet<int>(module.CopyLikeTypeIds),
            TraitImpls = module.TraitImpls.ToList(),
            TraitInfos = module.TraitInfos.ToList(),
            TypeAliases = module.TypeAliases.ToList(),
            TypeConstructors = module.TypeConstructors.ToList(),
            LinkLibraries = module.LinkLibraries.ToList(),
            SpecializationFailures = module.SpecializationFailures.ToList(),
            Span = module.Span
        };
    }

    // ---- Candidate selection ----

    private bool ShouldInline(MirFunc func)
    {
        if (string.IsNullOrEmpty(func.Name)) return false;
        if (func.IsExternal) return false;
        if (IsRecursive(func)) return false;
        // ADT constructors are lowered at the call site with heap allocation
        // and layout handling; inlining their body would turn the allocation
        // into a stack alloca that later refcounting/type_id code treats as a
        // heap object.
        if (func.FunctionId.Kind == SymbolKind.Constructor) return false;
        // Callees that dereference a parameter (reference-typed arguments)
        // rely on the call-site borrow lowering passing the element pointer;
        // inlining binds the value instead and the dereference reads garbage.
        if (DereferencesParameter(func)) return false;
        // Lambda closure bodies are invoked through the closure-invoke
        // protocol with captured payloads; inlining them into call sites that
        // still pass the closure object around breaks the capture contract.
        if (func.Name.StartsWith(WellKnownStrings.InternalNames.LambdaPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        // Task/TaskGroup helpers move first-class closures between runtime
        // worker threads; inlining the helper bodies duplicates the refcount
        // bookkeeping across thread boundaries.
        if (func.Name.StartsWith("std__Task", StringComparison.Ordinal))
        {
            return false;
        }
        // Ffi/Text/Console helpers are thin wrappers over runtime intrinsics;
        // inlining them is harmless, but they interact with boxed values and
        // string payloads whose refcount handling must stay at the call site.
        if (func.Name.StartsWith("std__Ffi", StringComparison.Ordinal) ||
            func.Name.StartsWith("std__Text", StringComparison.Ordinal) ||
            func.Name.StartsWith("std__Console", StringComparison.Ordinal) ||
            func.Name.StartsWith("__eidos_prelude_core__Display", StringComparison.Ordinal))
        {
            return false;
        }
        // Curried multi-parameter functions are first-class closure values at
        // call sites (partial application, closure protocol); inlining their
        // bodies duplicates the materialization/refcount paths and breaks the
        // closure contract under concurrency. Single-parameter functions (the
        // hot fib_value-style candidates) keep inlining.
        if (func.Locals.Count(static local => local.IsParameter) > 1)
        {
            return false;
        }
        // Only inline single-block functions (avoids complex block splitting)
        if (func.BasicBlocks.Count != 1) return false;

        var instructionCount = func.BasicBlocks[0].Instructions.Count;
        return instructionCount <= _maxInlineSize;
    }

    private bool IsRecursive(MirFunc func)
    {
        foreach (var block in func.BasicBlocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr is MirCall call && CallsFunction(call, func))
                    return true;
            }
        }
        return false;
    }

    private static bool DereferencesParameter(MirFunc func)
    {
        var parameterIds = func.Locals
            .Where(static local => local.IsParameter)
            .Select(static local => local.Id)
            .ToHashSet();
        foreach (var block in func.BasicBlocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr is MirLoad load &&
                    load.Source is MirPlace { Kind: PlaceKind.Deref, Base: MirPlace { Kind: PlaceKind.Local, Local: var baseLocal } } &&
                    parameterIds.Contains(baseLocal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ---- Call-site processing ----

    private static bool IsInlineCandidate(
        MirFunc function,
        IReadOnlyDictionary<SymbolId, MirFunc> candidatesBySymbol,
        IReadOnlyDictionary<string, MirFunc> candidatesByIdentity,
        IReadOnlyDictionary<string, MirFunc> candidatesByName)
    {
        if (function.SymbolId.IsValid && candidatesBySymbol.ContainsKey(function.SymbolId))
        {
            return true;
        }

        if (TryGetInlineFunctionIdentityKey(function.FunctionId, out var identityKey) &&
            candidatesByIdentity.ContainsKey(identityKey))
        {
            return true;
        }

        if (function.SymbolId.IsValid)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(function.Name) &&
               candidatesByName.ContainsKey(function.Name);
    }

    private MirFunc InlineCalls(
        MirFunc func,
        IReadOnlyDictionary<SymbolId, MirFunc> candidatesBySymbol,
        IReadOnlyDictionary<string, MirFunc> candidatesByIdentity,
        IReadOnlyDictionary<string, MirFunc> candidatesByName)
    {
        var newLocals = new List<MirLocal>(func.Locals);
        int nextLocalId = func.Locals.Select(l => l.Id.Value).DefaultIfEmpty(0).Max() + 1;
        int nextTempId = func.BasicBlocks
            .SelectMany(static block => block.Instructions)
            .SelectMany(CollectTempIds)
            .Select(static temp => temp.Value)
            .DefaultIfEmpty(-1)
            .Max() + 1;

        var newBlocks = new List<MirBasicBlock>();
        foreach (var block in func.BasicBlocks)
        {
            newBlocks.Add(InlineCallsInBlock(block, candidatesBySymbol, candidatesByIdentity, candidatesByName, newLocals, ref nextLocalId, ref nextTempId));
        }

        return new MirFunc
        {
            Name = func.Name,
            SourceName = func.SourceName,
            Locals = newLocals,
            BasicBlocks = newBlocks,
            EntryBlockId = func.EntryBlockId,
            ReturnType = func.ReturnType,
            GenericParameterCount = func.GenericParameterCount,
            GenericParameters = func.GenericParameters.ToList(),
            GenericTypeParameterIds = func.GenericTypeParameterIds.ToList(),
            IsRuntimeWordAbi = func.IsRuntimeWordAbi,
            IsEntry = func.IsEntry,
            Span = func.Span,
            SymbolId = func.SymbolId,
            FunctionId = func.FunctionId,
            TraitInvokeHelper = func.TraitInvokeHelper,
            TraitInvokeHelperTraitId = func.TraitInvokeHelperTraitId,
            IsExternal = func.IsExternal,
            ExternalSymbolName = func.ExternalSymbolName,
            ExternalLibrary = func.ExternalLibrary,
            IntrinsicName = func.IntrinsicName,
            BuiltinIntrinsicRole = func.BuiltinIntrinsicRole
        };
    }

    private static IEnumerable<TempId> CollectTempIds(MirInstruction instruction)
    {
        switch (instruction)
        {
            case MirAssign assign:
                return CollectTempIds(assign.Target).Concat(CollectTempIds(assign.Source));
            case MirCaseInject injection:
                return CollectTempIds(injection.Target).Concat(CollectTempIds(injection.Operand));
            case MirCall call:
                return CollectTempIds(call.Function).Concat(call.Arguments.SelectMany(CollectTempIds));
            case MirBinOp binOp:
                return CollectTempIds(binOp.Target).Concat(CollectTempIds(binOp.Left)).Concat(CollectTempIds(binOp.Right));
            case MirUnaryOp unaryOp:
                return CollectTempIds(unaryOp.Target).Concat(CollectTempIds(unaryOp.Operand));
            case MirLoad load:
                return CollectTempIds(load.Target).Concat(CollectTempIds(load.Source));
            case MirStore store:
                return CollectTempIds(store.Target).Concat(CollectTempIds(store.Value));
            case MirDrop drop:
                return CollectTempIds(drop.Value);
            case MirCopy copy:
                return CollectTempIds(copy.Target).Concat(CollectTempIds(copy.Source));
            case MirMove move:
                return CollectTempIds(move.Target).Concat(CollectTempIds(move.Source));
            default:
                return [];
        }
    }

    private static IEnumerable<TempId> CollectTempIds(MirOperand operand)
    {
        return operand switch
        {
            MirTemp temp => [temp.Id],
            MirPlace place when place.Index != null => CollectTempIds(place.Index),
            _ => []
        };
    }

    private MirBasicBlock InlineCallsInBlock(
        MirBasicBlock block,
        IReadOnlyDictionary<SymbolId, MirFunc> candidatesBySymbol,
        IReadOnlyDictionary<string, MirFunc> candidatesByIdentity,
        IReadOnlyDictionary<string, MirFunc> candidatesByName,
        List<MirLocal> newLocals,
        ref int nextLocalId,
        ref int nextTempId)
    {
        var newInstructions = new List<MirInstruction>();

        foreach (var instr in block.Instructions)
        {
            if (instr is MirCall call &&
                TryResolveInlineCandidate(call, candidatesBySymbol, candidatesByIdentity, candidatesByName, out var callee))
            {
                var inlined = InlineSingleBlockCall(call, callee, newLocals, ref nextLocalId, ref nextTempId);
                newInstructions.AddRange(inlined);
            }
            else
            {
                newInstructions.Add(instr);
            }
        }

        return new MirBasicBlock
        {
            Id = block.Id,
            Instructions = newInstructions,
            Terminator = block.Terminator,
            Span = block.Span,
            IsEntry = block.IsEntry
        };
    }

    /// <summary>
    /// Inline a single-block callee at a call site.
    /// Returns the list of instructions to replace the call.
    /// </summary>
    private List<MirInstruction> InlineSingleBlockCall(
        MirCall call,
        MirFunc callee,
        List<MirLocal> newLocals,
        ref int nextLocalId,
        ref int nextTempId)
    {
        // 1. Build local ID remapping (callee local → fresh caller local)
        var localMap = new Dictionary<LocalId, LocalId>();
        foreach (var local in callee.Locals)
        {
            var freshId = new LocalId { Value = nextLocalId++ };
            localMap[local.Id] = freshId;
            newLocals.Add(new MirLocal
            {
                Id = freshId,
                Name = $"{WellKnownStrings.InternalNames.InlinePrefix}{callee.Name}.{local.Name}",
                TypeId = local.TypeId,
                IsMutable = local.IsMutable,
                IsParameter = false,
                BindingMode = local.BindingMode,
                Span = local.Span
            });
        }

        // 1b. Build temp ID remapping (callee temp → fresh caller temp); temps
        // are function-local and collide with the caller's after inlining.
        var calleeBlock = callee.BasicBlocks[0];
        IEnumerable<TempId> calleeTemps = calleeBlock.Instructions.SelectMany(CollectTempIds);
        if (calleeBlock.Terminator is MirReturn ret && ret.Value != null)
        {
            calleeTemps = calleeTemps.Concat(CollectTempIds(ret.Value));
        }

        var tempMap = new Dictionary<TempId, TempId>();
        foreach (var tempId in calleeTemps)
        {
            tempMap[tempId] = new TempId { Value = nextTempId++ };
        }

        var result = new List<MirInstruction>();

        // Partial application keeps the call: the flat callee signature expects
        // every parameter, but a partial call passes fewer arguments, so
        // inlining would read uninitialized locals. A mismatched argument
        // count also cannot be expanded into the callee body.
        var parameters = callee.Locals
            .Where(l => l.IsParameter)
            .OrderBy(l => l.Id.Value)
            .ToList();
        if (call.Arguments.Count != parameters.Count)
        {
            return [call];
        }

        // 2. Argument bindings: assign caller arguments to remapped parameter locals
        for (int i = 0; i < parameters.Count; i++)
        {
            var newParamId = localMap[parameters[i].Id];
            result.Add(new MirAssign
            {
                Target = new MirPlace
                {
                    Kind = PlaceKind.Local,
                    Local = newParamId,
                    TypeId = parameters[i].TypeId,
                    Span = call.Span
                },
                Source = call.Arguments[i],
                Span = call.Span
            });
        }

        // 3. Remapped callee body (instructions only, not terminator)
        foreach (var ci in calleeBlock.Instructions)
        {
            result.Add(RemapInstruction(ci, localMap, tempMap));
        }

        // 4. Return value: MirReturn → MirAssign to call target
        if (calleeBlock.Terminator is MirReturn returnInstr && call.Target != null && returnInstr.Value != null)
        {
            result.Add(new MirAssign
            {
                Target = call.Target,
                Source = RemapOperand(returnInstr.Value, localMap, tempMap),
                Span = returnInstr.Span
            });
        }

        return result;
    }

    // ---- Remapping helpers ----

    private static string? GetCalleeName(MirCall call)
    {
        if (call.Function is MirFunctionRef funcRef)
            return funcRef.Name;
        if (call.Function is MirConstant { Value: MirConstantValue.StringValue strVal })
            return strVal.Value;
        return null;
    }

    private static bool CallsFunction(MirCall call, MirFunc function)
    {
        if (call.Function is MirFunctionRef functionRef)
        {
            if (functionRef.SymbolId.IsValid || function.SymbolId.IsValid)
            {
                return functionRef.SymbolId.IsValid &&
                       function.SymbolId.IsValid &&
                       functionRef.SymbolId == function.SymbolId;
            }

            var refHasIdentity = TryGetInlineFunctionIdentityKey(functionRef.FunctionId, out var refIdentityKey);
            var functionHasIdentity = TryGetInlineFunctionIdentityKey(function.FunctionId, out var functionIdentityKey);
            if (refHasIdentity || functionHasIdentity)
            {
                return refHasIdentity &&
                       functionHasIdentity &&
                       string.Equals(refIdentityKey, functionIdentityKey, StringComparison.Ordinal);
            }

            return !string.IsNullOrWhiteSpace(functionRef.Name) &&
                   string.Equals(functionRef.Name, function.Name, StringComparison.Ordinal);
        }

        return GetCalleeName(call) is { } calleeName &&
               !function.SymbolId.IsValid &&
               !TryGetInlineFunctionIdentityKey(function.FunctionId, out _) &&
               string.Equals(calleeName, function.Name, StringComparison.Ordinal);
    }

    private static MirOperand RemapOperand(MirOperand operand, Dictionary<LocalId, LocalId> map, Dictionary<TempId, TempId> tempMap)
    {
        return operand switch
        {
            MirPlace place => RemapPlace(place, map, tempMap),
            MirTemp temp when tempMap.TryGetValue(temp.Id, out var newId) => temp with { Id = newId },
            _ => operand // MirConstant, MirFunctionRef don't need remapping
        };
    }

    private static MirPlace RemapPlace(MirPlace place, Dictionary<LocalId, LocalId> map, Dictionary<TempId, TempId> tempMap)
    {
        return place.Kind switch
        {
            PlaceKind.Local when map.TryGetValue(place.Local, out var newId) =>
                place with { Local = newId },
            PlaceKind.Field when place.Base != null =>
                place with { Base = RemapPlace(place.Base, map, tempMap) },
            PlaceKind.Index when place.Base != null =>
                place with
                {
                    Base = RemapPlace(place.Base, map, tempMap),
                    Index = place.Index != null ? RemapOperand(place.Index, map, tempMap) : null
                },
            PlaceKind.Deref when place.Base != null =>
                place with { Base = RemapPlace(place.Base, map, tempMap) },
            _ => place
        };
    }

    private static MirInstruction RemapInstruction(MirInstruction instr, Dictionary<LocalId, LocalId> map, Dictionary<TempId, TempId> tempMap)
    {
        return instr switch
        {
            MirAssign assign => assign with
            {
                Target = RemapPlace(assign.Target, map, tempMap),
                Source = RemapOperand(assign.Source, map, tempMap)
            },
            MirCaseInject injection => injection with
            {
                Target = RemapOperand(injection.Target, map, tempMap),
                Operand = RemapOperand(injection.Operand, map, tempMap)
            },
            MirCall call => call with
            {
                Target = call.Target != null ? RemapPlace(call.Target, map, tempMap) : null,
                Function = RemapOperand(call.Function, map, tempMap),
                Arguments = call.Arguments.Select(a => RemapOperand(a, map, tempMap)).ToList()
            },
            MirBinOp binOp => binOp with
            {
                Target = RemapOperand(binOp.Target, map, tempMap),
                Left = RemapOperand(binOp.Left, map, tempMap),
                Right = RemapOperand(binOp.Right, map, tempMap)
            },
            MirUnaryOp unaryOp => unaryOp with
            {
                Target = RemapOperand(unaryOp.Target, map, tempMap),
                Operand = RemapOperand(unaryOp.Operand, map, tempMap)
            },
            MirLoad load => load with
            {
                Target = RemapPlace(load.Target, map, tempMap),
                Source = RemapOperand(load.Source, map, tempMap)
            },
            MirStore store => store with
            {
                Target = RemapPlace(store.Target, map, tempMap),
                Value = RemapOperand(store.Value, map, tempMap)
            },
            MirDrop drop => drop with { Value = RemapOperand(drop.Value, map, tempMap) },
            MirCopy copy => copy with
            {
                Target = RemapPlace(copy.Target, map, tempMap),
                Source = RemapPlace(copy.Source, map, tempMap)
            },
            MirMove move => move with
            {
                Target = RemapPlace(move.Target, map, tempMap),
                Source = RemapPlace(move.Source, map, tempMap)
            },
            MirAlloc alloc => alloc with { Target = RemapPlace(alloc.Target, map, tempMap) },
            _ => instr
        };
    }

    private static bool TryResolveInlineCandidate(
        MirCall call,
        IReadOnlyDictionary<SymbolId, MirFunc> candidatesBySymbol,
        IReadOnlyDictionary<string, MirFunc> candidatesByIdentity,
        IReadOnlyDictionary<string, MirFunc> candidatesByName,
        out MirFunc callee)
    {
        if (call.Function is MirFunctionRef funcRef)
        {
            if (funcRef.SymbolId.IsValid &&
                candidatesBySymbol.TryGetValue(funcRef.SymbolId, out var symbolCallee) &&
                symbolCallee != null)
            {
                callee = symbolCallee;
                return true;
            }

            if (TryGetInlineFunctionIdentityKey(funcRef.FunctionId, out var identityKey))
            {
                if (candidatesByIdentity.TryGetValue(identityKey, out var identityCallee) &&
                    identityCallee != null)
                {
                    callee = identityCallee;
                    return true;
                }
            }

            if (TryGetInlineFunctionIdentityFallbackKey(funcRef.FunctionId, out var fallbackIdentityKey) &&
                candidatesByIdentity.TryGetValue(fallbackIdentityKey, out var fallbackIdentityCallee) &&
                fallbackIdentityCallee != null)
            {
                callee = fallbackIdentityCallee;
                return true;
            }

            if (funcRef.SymbolId.IsValid)
            {
                callee = null!;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(funcRef.Name) &&
                candidatesByName.TryGetValue(funcRef.Name, out var namedCallee) &&
                namedCallee != null)
            {
                callee = namedCallee;
                return true;
            }
        }

        if (GetCalleeName(call) is { } calleeName &&
            candidatesByName.TryGetValue(calleeName, out var fallbackCallee) &&
            fallbackCallee != null)
        {
            callee = fallbackCallee;
            return true;
        }

        callee = null!;
        return false;
    }

    private static bool TryRegisterInlineCandidateIdentity(
        MirFunc function,
        Dictionary<string, MirFunc> candidatesByIdentity,
        HashSet<string> ambiguousIdentities)
    {
        var registered = false;
        if (TryGetInlineFunctionIdentityKey(function.FunctionId, out var identityKey))
        {
            registered = RegisterInlineCandidateIdentityKey(
                identityKey,
                function,
                candidatesByIdentity,
                ambiguousIdentities);
        }

        if (TryGetInlineFunctionIdentityFallbackKey(function.FunctionId, out var fallbackIdentityKey) &&
            !string.Equals(fallbackIdentityKey, identityKey, StringComparison.Ordinal))
        {
            registered |= RegisterInlineCandidateIdentityKey(
                fallbackIdentityKey,
                function,
                candidatesByIdentity,
                ambiguousIdentities);
        }

        return registered;
    }

    private static bool RegisterInlineCandidateIdentityKey(
        string identityKey,
        MirFunc function,
        Dictionary<string, MirFunc> candidatesByIdentity,
        HashSet<string> ambiguousIdentities)
    {
        if (ambiguousIdentities.Contains(identityKey))
        {
            return false;
        }

        if (candidatesByIdentity.TryGetValue(identityKey, out var existing) &&
            !ReferenceEquals(existing, function))
        {
            candidatesByIdentity.Remove(identityKey);
            ambiguousIdentities.Add(identityKey);
            return true;
        }

        candidatesByIdentity[identityKey] = function;
        return true;
    }

    private static bool TryGetInlineFunctionIdentityKey(FunctionId? functionId, out string identityKey)
    {
        return MirFunctionIdentity.TryGetStableKey(functionId, out identityKey);
    }

    private static bool TryGetInlineFunctionIdentityFallbackKey(FunctionId? functionId, out string identityKey)
    {
        return MirFunctionIdentity.TryGetStableKeyIgnoringSymbolId(functionId, out identityKey);
    }
}
