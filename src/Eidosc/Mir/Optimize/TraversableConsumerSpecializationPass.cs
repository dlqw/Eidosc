using Eidosc.Semantic;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Specializes a traversable implementation for the consumer-driven
/// <c>sequence</c> identity callback. The callback remains in the ABI so the
/// clone is a safe drop-in replacement, while proven callback invocations are
/// replaced by a move/copy of the input value. Unknown callbacks and escaped
/// function values keep the original implementation.
/// </summary>
public sealed class TraversableConsumerSpecializationPass :
    IMirOptimizationPass,
    IMirOptimizationMetricsProvider
{
    private readonly Dictionary<string, MirFunctionRef> _identityClones = new(StringComparer.Ordinal);
    private long _consumersScanned;
    private long _identityConsumers;
    private long _clonesCreated;
    private long _callbackCallsElided;
    private long _fallbackUnknownCallback;
    private long _fallbackEscapedCallback;

    public string Name => "TraversableConsumerSpecialization";

    public IReadOnlyDictionary<string, long> GetMetricsSnapshot() => new Dictionary<string, long>
    {
        ["traversable.consumers_scanned"] = _consumersScanned,
        ["traversable.identity_consumers"] = _identityConsumers,
        ["traversable.identity_clones_created"] = _clonesCreated,
        ["traversable.callback_calls_elided"] = _callbackCallsElided,
        ["traversable.fallback.unknown_callback"] = _fallbackUnknownCallback,
        ["traversable.fallback.escaped_callback"] = _fallbackEscapedCallback
    };

    public MirModule Run(MirModule module)
    {
        _identityClones.Clear();
        _consumersScanned = 0;
        _identityConsumers = 0;
        _clonesCreated = 0;
        _callbackCallsElided = 0;
        _fallbackUnknownCallback = 0;
        _fallbackEscapedCallback = 0;

        var functions = module.Functions.ToList();
        var functionsByName = functions
            .GroupBy(static function => function.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var changed = false;

        foreach (var consumer in functions.ToArray())
        {
            if (!IsSequenceConsumer(consumer))
            {
                continue;
            }

            foreach (var block in consumer.BasicBlocks)
            {
                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    if (block.Instructions[index] is not MirCall call ||
                        call.Function is not MirFunctionRef calleeRef ||
                        call.Arguments.Count < 2 ||
                        calleeRef.Name.Contains("__consumer_identity", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    _consumersScanned++;
                    var callback = call.Arguments[^1];
                    if (callback is not MirFunctionRef identityRef || !IsIdentityApplicative(identityRef))
                    {
                        if (callback is MirPlace)
                        {
                            _fallbackEscapedCallback++;
                        }
                        else
                        {
                            _fallbackUnknownCallback++;
                        }

                        continue;
                    }

                    if (!functionsByName.TryGetValue(calleeRef.Name, out var callee) ||
                        !TryGetCallbackParameter(callee, call.Arguments.Count - 1, out var callbackParameter))
                    {
                        _fallbackUnknownCallback++;
                        continue;
                    }

                    _identityConsumers++;
                    var cloneRef = GetOrCreateIdentityClone(
                        functions,
                        functionsByName,
                        callee,
                        calleeRef,
                        callbackParameter,
                        ref changed);
                    if (!string.Equals(cloneRef.Name, calleeRef.Name, StringComparison.Ordinal))
                    {
                        block.Instructions[index] = call with { Function = cloneRef };
                    }
                }
            }
        }

        return changed ? module.WithFunctions(functions) : module;
    }

    private MirFunctionRef GetOrCreateIdentityClone(
        List<MirFunc> functions,
        Dictionary<string, MirFunc> functionsByName,
        MirFunc callee,
        MirFunctionRef originalRef,
        LocalId callbackParameter,
        ref bool changed)
    {
        if (_identityClones.TryGetValue(callee.Name, out var existing))
        {
            return existing;
        }

        var cloneName = $"{callee.Name}__consumer_identity";
        var suffix = 0;
        while (functionsByName.ContainsKey(cloneName))
        {
            suffix++;
            cloneName = $"{callee.Name}__consumer_identity_{suffix}";
        }

        var clone = CloneFunction(callee, cloneName);
        var aliases = BuildCallbackAliases(clone, callbackParameter);
        var rewrittenInstructions = 0L;
        foreach (var block in clone.BasicBlocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                if (block.Instructions[index] is not MirCall call)
                {
                    continue;
                }

                if (call.Function is MirPlace { Kind: PlaceKind.Local, Local: var local })
                {
                    if (!aliases.IsAvailable(local, new InstructionSite(block.Id, index)))
                    {
                        continue;
                    }

                    if (!TryRewriteIdentityCall(call, out var replacement))
                    {
                        _fallbackUnknownCallback++;
                        continue;
                    }

                    block.Instructions[index] = replacement;
                    rewrittenInstructions++;
                    continue;
                }

                if (call.Function is MirFunctionRef recursiveRef &&
                    string.Equals(recursiveRef.Name, callee.Name, StringComparison.Ordinal))
                {
                    block.Instructions[index] = call with
                    {
                        Function = CreateCloneReference(recursiveRef, clone)
                    };
                }
            }
        }

        if (rewrittenInstructions == 0)
        {
            _fallbackUnknownCallback++;
            return originalRef;
        }

        var cloneRef = CreateCloneReference(originalRef, clone);
        functions.Add(clone);
        functionsByName[clone.Name] = clone;
        _identityClones[callee.Name] = cloneRef;
        _clonesCreated++;
        _callbackCallsElided += rewrittenInstructions;
        changed = true;
        return cloneRef;
    }

    private static bool TryRewriteIdentityCall(MirCall call, out MirInstruction replacement)
    {
        if (call.Arguments.Count != 1)
        {
            replacement = null!;
            return false;
        }

        var argument = call.Arguments[0];
        if (call.Target is MirPlace target)
        {
            replacement = argument is MirPlace source
                ? new MirMove
                {
                    Target = target,
                    Source = source,
                    Span = call.Span
                }
                : new MirAssign
                {
                    Target = target,
                    Source = argument,
                    Span = call.Span
                };
            return true;
        }

        replacement = new MirDrop
        {
            Value = argument,
            Span = call.Span
        };
        return true;
    }

    private static bool IsSequenceConsumer(MirFunc function) =>
        function.Name.Contains("__Traversable__sequence__spec_", StringComparison.Ordinal);

    private static bool IsIdentityApplicative(MirFunctionRef functionRef) =>
        functionRef.Name.Contains("__Traversable__identity_applicative__spec_", StringComparison.Ordinal);

    private static bool TryGetCallbackParameter(MirFunc function, int argumentIndex, out LocalId parameter)
    {
        var parameters = function.Locals.Where(static local => local.IsParameter).ToArray();
        if ((uint)argumentIndex < (uint)parameters.Length)
        {
            parameter = parameters[argumentIndex].Id;
            return true;
        }

        parameter = default;
        return false;
    }

    private static CallbackAliasProof BuildCallbackAliases(MirFunc function, LocalId callbackParameter)
    {
        var controlFlow = new ControlFlowGraph(function);
        var definitionCounts = CountLocalDefinitions(function);
        var storageRoots = BuildStorageRoots(function, definitionCounts);
        var storesByKey = BuildStoresByKey(function, storageRoots);
        var aliases = new Dictionary<LocalId, InstructionSite?>
        {
            [callbackParameter] = null
        };

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in function.BasicBlocks)
            {
                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    var instruction = block.Instructions[index];
                    var site = new InstructionSite(block.Id, index);
                    if (TryGetLocalAlias(instruction, out var target, out var source))
                    {
                        if (IsSingleDefinition(target, definitionCounts) &&
                            IsAliasAvailable(source, site, aliases, controlFlow))
                        {
                            changed |= aliases.TryAdd(target, site);
                        }

                        continue;
                    }

                    if (instruction is not MirLoad load ||
                        load.Target.Kind != PlaceKind.Local ||
                        load.Source is not MirPlace loadSource ||
                        !IsSingleDefinition(load.Target.Local, definitionCounts))
                    {
                        continue;
                    }

                    var loadKey = GetPlaceKey(loadSource, storageRoots);
                    if (!storesByKey.TryGetValue(loadKey, out var stores) ||
                        stores.Count == 0 ||
                        stores.Any(store => store.Value is not MirPlace
                        {
                            Kind: PlaceKind.Local,
                            Local: var valueLocal
                        } || !IsAliasAvailable(valueLocal, store.Site, aliases, controlFlow)))
                    {
                        continue;
                    }

                    if (stores.Any(store =>
                            Dominates(store.Site, site, controlFlow) &&
                            StorageRootAliasesFollowStore(loadSource, store.Site, storageRoots, controlFlow)))
                    {
                        changed |= aliases.TryAdd(load.Target.Local, site);
                    }
                }
            }
        }

        return new CallbackAliasProof(aliases, controlFlow);
    }

    private static IReadOnlyDictionary<LocalId, StorageRootFact> BuildStorageRoots(
        MirFunc function,
        IReadOnlyDictionary<LocalId, int> definitionCounts)
    {
        var roots = new Dictionary<LocalId, StorageRootFact>();
        foreach (var block in function.BasicBlocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                LocalId target;
                LocalId source;
                if (instruction is MirLoad
                    {
                        Target: { Kind: PlaceKind.Local, Local: var loadTarget },
                        Source: MirPlace { Kind: PlaceKind.Local, Local: var loadSource }
                    })
                {
                    target = loadTarget;
                    source = loadSource;
                }
                else if (!TryGetLocalAlias(instruction, out target, out source))
                {
                    continue;
                }

                if (IsSingleDefinition(target, definitionCounts))
                {
                    roots.TryAdd(target, new StorageRootFact(source, new InstructionSite(block.Id, index)));
                }
            }
        }

        return roots;
    }

    private static IReadOnlyDictionary<string, List<StorageWrite>> BuildStoresByKey(
        MirFunc function,
        IReadOnlyDictionary<LocalId, StorageRootFact> storageRoots)
    {
        var stores = new Dictionary<string, List<StorageWrite>>(StringComparer.Ordinal);
        foreach (var block in function.BasicBlocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                if (block.Instructions[index] is not MirStore store)
                {
                    continue;
                }

                var key = GetPlaceKey(store.Target, storageRoots);
                if (!stores.TryGetValue(key, out var writes))
                {
                    writes = [];
                    stores[key] = writes;
                }

                writes.Add(new StorageWrite(store.Value, new InstructionSite(block.Id, index)));
            }
        }

        return stores;
    }

    private static IReadOnlyDictionary<LocalId, int> CountLocalDefinitions(MirFunc function)
    {
        var counts = new Dictionary<LocalId, int>();
        foreach (var instruction in function.BasicBlocks.SelectMany(static block => block.Instructions))
        {
            if (!TryGetDefinedLocal(instruction, out var local))
            {
                continue;
            }

            counts[local] = counts.GetValueOrDefault(local) + 1;
        }

        return counts;
    }

    private static bool TryGetDefinedLocal(MirInstruction instruction, out LocalId local)
    {
        MirOperand? target = instruction switch
        {
            MirAssign assign => assign.Target,
            MirCaseInject inject => inject.Target,
            MirCall call => call.Target,
            MirBinOp binary => binary.Target,
            MirUnaryOp unary => unary.Target,
            MirSelect select => select.Target,
            MirLoad load => load.Target,
            MirCopy copy => copy.Target,
            MirMove move => move.Target,
            MirAlloc alloc => alloc.Target,
            _ => null
        };
        if (target is MirPlace { Kind: PlaceKind.Local, Local: var defined })
        {
            local = defined;
            return true;
        }

        local = default;
        return false;
    }

    private static bool IsSingleDefinition(
        LocalId local,
        IReadOnlyDictionary<LocalId, int> definitionCounts) =>
        definitionCounts.GetValueOrDefault(local) == 1;

    private static bool IsAliasAvailable(
        LocalId local,
        InstructionSite useSite,
        IReadOnlyDictionary<LocalId, InstructionSite?> aliases,
        ControlFlowGraph controlFlow) =>
        aliases.TryGetValue(local, out var definitionSite) &&
        (definitionSite == null || Dominates(definitionSite.Value, useSite, controlFlow));

    private static bool Dominates(
        InstructionSite definition,
        InstructionSite use,
        ControlFlowGraph controlFlow) =>
        definition.BlockId == use.BlockId
            ? definition.InstructionIndex < use.InstructionIndex
            : controlFlow.GetDominators(use.BlockId).Contains(definition.BlockId);

    private static bool StorageRootAliasesFollowStore(
        MirPlace loadSource,
        InstructionSite storeSite,
        IReadOnlyDictionary<LocalId, StorageRootFact> storageRoots,
        ControlFlowGraph controlFlow)
    {
        var root = GetRootLocal(loadSource);
        if (root == null)
        {
            return false;
        }

        var current = root.Value;
        var visited = new HashSet<LocalId>();
        while (visited.Add(current) && storageRoots.TryGetValue(current, out var fact))
        {
            if (!Dominates(storeSite, fact.Site, controlFlow))
            {
                return false;
            }

            current = fact.Source;
        }

        return true;
    }

    private static LocalId? GetRootLocal(MirPlace place)
    {
        var current = place;
        while (current.Kind != PlaceKind.Local)
        {
            if (current.Base == null)
            {
                return null;
            }

            current = current.Base;
        }

        return current.Local;
    }

    private static LocalId ResolveStorageRoot(
        LocalId local,
        IReadOnlyDictionary<LocalId, StorageRootFact> roots)
    {
        var current = local;
        var visited = new HashSet<LocalId>();
        while (visited.Add(current) && roots.TryGetValue(current, out var fact) && fact.Source != current)
        {
            current = fact.Source;
        }

        return current;
    }

    private static string GetPlaceKey(
        MirPlace place,
        IReadOnlyDictionary<LocalId, StorageRootFact> storageRoots) =>
        place.Kind switch
        {
            PlaceKind.Local => $"local:{ResolveStorageRoot(place.Local, storageRoots).Value}",
            PlaceKind.Field => $"field:{GetPlaceKey(place.Base!, storageRoots)}:{place.FieldName}",
            PlaceKind.Index => $"index:{GetPlaceKey(place.Base!, storageRoots)}:{GetOperandKey(place.Index, storageRoots)}",
            PlaceKind.Deref => $"deref:{GetPlaceKey(place.Base!, storageRoots)}",
            _ => place.ToString()
        };

    private static string GetOperandKey(
        MirOperand? operand,
        IReadOnlyDictionary<LocalId, StorageRootFact> storageRoots) =>
        operand switch
        {
            MirPlace place => GetPlaceKey(place, storageRoots),
            MirConstant constant => $"constant:{constant.Value}",
            MirFunctionRef functionRef => $"function:{functionRef.Name}",
            _ => operand?.ToString() ?? "<null>"
        };

    private static bool TryGetLocalAlias(
        MirInstruction instruction,
        out LocalId target,
        out LocalId source)
    {
        var matched = instruction switch
        {
            MirCopy
            {
                Target: { Kind: PlaceKind.Local, Local: var copyTarget },
                Source: MirPlace { Kind: PlaceKind.Local, Local: var copySource }
            } => (true, copyTarget, copySource),
            MirMove
            {
                Target: { Kind: PlaceKind.Local, Local: var moveTarget },
                Source: MirPlace { Kind: PlaceKind.Local, Local: var moveSource }
            } => (true, moveTarget, moveSource),
            _ => (false, default, default)
        };

        target = matched.Item2;
        source = matched.Item3;
        return matched.Item1;
    }

    private readonly record struct InstructionSite(BlockId BlockId, int InstructionIndex);

    private readonly record struct StorageRootFact(LocalId Source, InstructionSite Site);

    private readonly record struct StorageWrite(MirOperand Value, InstructionSite Site);

    private sealed class CallbackAliasProof(
        IReadOnlyDictionary<LocalId, InstructionSite?> aliases,
        ControlFlowGraph controlFlow)
    {
        public bool IsAvailable(LocalId local, InstructionSite useSite) =>
            IsAliasAvailable(local, useSite, aliases, controlFlow);
    }

    private static MirFunc CloneFunction(MirFunc source, string name)
    {
        var clone = MirFunctionTransform.CloneWithBody(
            source,
            source.Locals.Select(static local => new MirLocal
            {
                Id = local.Id,
                Name = local.Name,
                TypeId = local.TypeId,
                IsMutable = local.IsMutable,
                IsParameter = local.IsParameter,
                BindingMode = local.BindingMode,
                Span = local.Span
            }).ToList(),
            source.BasicBlocks.Select(CloneBlock).ToList());

        return new MirFunc
        {
            Name = name,
            SourceName = clone.SourceName,
            Locals = clone.Locals,
            BasicBlocks = clone.BasicBlocks,
            EntryBlockId = clone.EntryBlockId,
            ReturnType = clone.ReturnType,
            GenericParameterCount = clone.GenericParameterCount,
            GenericParameters = clone.GenericParameters,
            GenericTypeParameterIds = clone.GenericTypeParameterIds,
            IsRuntimeWordAbi = clone.IsRuntimeWordAbi,
            IsEntry = false,
            Span = clone.Span,
            SymbolId = SymbolId.None,
            FunctionId = clone.FunctionId with
            {
                SymbolId = SymbolId.None,
                Name = name,
                QualifiedName = string.IsNullOrWhiteSpace(clone.FunctionId.QualifiedName)
                    ? name
                    : $"{clone.FunctionId.QualifiedName}__consumer_identity",
                StableIdentityKey = string.IsNullOrWhiteSpace(clone.FunctionId.StableIdentityKey)
                    ? $"name:{name}"
                    : $"{clone.FunctionId.StableIdentityKey}\0consumer-identity"
            },
            TraitInvokeHelper = clone.TraitInvokeHelper,
            TraitInvokeHelperTraitId = clone.TraitInvokeHelperTraitId,
            IsExternal = clone.IsExternal,
            ExternalSymbolName = clone.ExternalSymbolName,
            ExternalLibrary = clone.ExternalLibrary,
            IntrinsicName = clone.IntrinsicName,
            BuiltinIntrinsicRole = clone.BuiltinIntrinsicRole,
            CallerOwnedAggregateAbi = clone.CallerOwnedAggregateAbi
        };
    }

    private static MirBasicBlock CloneBlock(MirBasicBlock block) => new()
    {
        Id = block.Id,
        Span = block.Span,
        IsEntry = block.IsEntry,
        Instructions = block.Instructions.Select(CloneInstruction).ToList(),
        Terminator = block.Terminator
    };

    private static MirInstruction CloneInstruction(MirInstruction instruction) => instruction switch
    {
        MirCall call => call with { Arguments = call.Arguments.ToList() },
        MirAssign assign => assign with { },
        MirCaseInject inject => inject with { },
        MirBinOp binOp => binOp with { },
        MirUnaryOp unaryOp => unaryOp with { },
        MirSelect select => select with { },
        MirLoad load => load with { },
        MirStore store => store with { },
        MirDrop drop => drop with { },
        MirCopy copy => copy with { },
        MirMove move => move with { },
        MirAlloc alloc => alloc with { },
        _ => instruction
    };

    private static MirFunctionRef CreateCloneReference(MirFunctionRef source, MirFunc callee)
    {
        return source with
        {
            Name = callee.Name,
            SymbolId = SymbolId.None,
            FunctionId = callee.FunctionId,
            TypeId = source.TypeId,
            SignatureTypeId = source.SignatureTypeId
        };
    }
}
