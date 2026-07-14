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

        var newBlocks = new List<MirBasicBlock>();
        foreach (var block in func.BasicBlocks)
        {
            newBlocks.Add(InlineCallsInBlock(block, candidatesBySymbol, candidatesByIdentity, candidatesByName, newLocals, ref nextLocalId));
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

    private MirBasicBlock InlineCallsInBlock(
        MirBasicBlock block,
        IReadOnlyDictionary<SymbolId, MirFunc> candidatesBySymbol,
        IReadOnlyDictionary<string, MirFunc> candidatesByIdentity,
        IReadOnlyDictionary<string, MirFunc> candidatesByName,
        List<MirLocal> newLocals,
        ref int nextLocalId)
    {
        var newInstructions = new List<MirInstruction>();

        foreach (var instr in block.Instructions)
        {
            if (instr is MirCall call &&
                TryResolveInlineCandidate(call, candidatesBySymbol, candidatesByIdentity, candidatesByName, out var callee))
            {
                var inlined = InlineSingleBlockCall(call, callee, newLocals, ref nextLocalId);
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
        ref int nextLocalId)
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

        var result = new List<MirInstruction>();

        // 2. Argument bindings: assign caller arguments to remapped parameter locals
        var parameters = callee.Locals
            .Where(l => l.IsParameter)
            .OrderBy(l => l.Id.Value)
            .ToList();

        for (int i = 0; i < parameters.Count && i < call.Arguments.Count; i++)
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
        var calleeBlock = callee.BasicBlocks[0];
        foreach (var ci in calleeBlock.Instructions)
        {
            result.Add(RemapInstruction(ci, localMap));
        }

        // 4. Return value: MirReturn → MirAssign to call target
        if (calleeBlock.Terminator is MirReturn ret && call.Target != null && ret.Value != null)
        {
            result.Add(new MirAssign
            {
                Target = call.Target,
                Source = RemapOperand(ret.Value, localMap),
                Span = ret.Span
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

    private static MirOperand RemapOperand(MirOperand operand, Dictionary<LocalId, LocalId> map)
    {
        return operand switch
        {
            MirPlace place => RemapPlace(place, map),
            _ => operand // MirConstant, MirFunctionRef, MirTemp don't need local remapping
        };
    }

    private static MirPlace RemapPlace(MirPlace place, Dictionary<LocalId, LocalId> map)
    {
        return place.Kind switch
        {
            PlaceKind.Local when map.TryGetValue(place.Local, out var newId) =>
                place with { Local = newId },
            PlaceKind.Field when place.Base != null =>
                place with { Base = RemapPlace(place.Base, map) },
            PlaceKind.Index when place.Base != null =>
                place with
                {
                    Base = RemapPlace(place.Base, map),
                    Index = place.Index != null ? RemapOperand(place.Index, map) : null
                },
            PlaceKind.Deref when place.Base != null =>
                place with { Base = RemapPlace(place.Base, map) },
            _ => place
        };
    }

    private static MirInstruction RemapInstruction(MirInstruction instr, Dictionary<LocalId, LocalId> map)
    {
        return instr switch
        {
            MirAssign assign => assign with
            {
                Target = RemapPlace(assign.Target, map),
                Source = RemapOperand(assign.Source, map)
            },
            MirCall call => call with
            {
                Target = call.Target != null ? RemapPlace(call.Target, map) : null,
                Function = RemapOperand(call.Function, map),
                Arguments = call.Arguments.Select(a => RemapOperand(a, map)).ToList()
            },
            MirBinOp binOp => binOp with
            {
                Target = RemapOperand(binOp.Target, map),
                Left = RemapOperand(binOp.Left, map),
                Right = RemapOperand(binOp.Right, map)
            },
            MirUnaryOp unaryOp => unaryOp with
            {
                Target = RemapOperand(unaryOp.Target, map),
                Operand = RemapOperand(unaryOp.Operand, map)
            },
            MirLoad load => load with
            {
                Target = RemapPlace(load.Target, map),
                Source = RemapOperand(load.Source, map)
            },
            MirStore store => store with
            {
                Target = RemapPlace(store.Target, map),
                Value = RemapOperand(store.Value, map)
            },
            MirDrop drop => drop with { Value = RemapOperand(drop.Value, map) },
            MirCopy copy => copy with
            {
                Target = RemapPlace(copy.Target, map),
                Source = RemapPlace(copy.Source, map)
            },
            MirMove move => move with
            {
                Target = RemapPlace(move.Target, map),
                Source = RemapPlace(move.Source, map)
            },
            MirAlloc alloc => alloc with { Target = RemapPlace(alloc.Target, map) },
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
