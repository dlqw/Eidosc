using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

public sealed class TailCallOptimization : IMirOptimizationPass
{
    private readonly bool _convertSelfRecursionToLoop;

    public TailCallOptimization(bool convertSelfRecursionToLoop = true)
    {
        _convertSelfRecursionToLoop = convertSelfRecursionToLoop;
    }

    public string Name => "TailCallOptimization";

    public MirModule Run(MirModule module)
    {
        List<MirFunc>? optimizedFunctions = null;

        for (var i = 0; i < module.Functions.Count; i++)
        {
            var func = module.Functions[i];
            var optimized = OptimizeFunction(func);
            if (optimizedFunctions != null)
            {
                optimizedFunctions.Add(optimized);
                continue;
            }

            if (!ReferenceEquals(optimized, func))
            {
                optimizedFunctions = new List<MirFunc>(module.Functions.Count);
                for (var previous = 0; previous < i; previous++)
                {
                    optimizedFunctions.Add(module.Functions[previous]);
                }

                optimizedFunctions.Add(optimized);
            }
        }

        if (optimizedFunctions == null)
        {
            return module;
        }

        return CloneModuleWithFunctions(module, optimizedFunctions);
    }

    private MirFunc OptimizeFunction(MirFunc func)
    {
        if (func.IsExternal || func.BasicBlocks.Count == 0)
            return func;

        var blocks = func.BasicBlocks.ToList();
        var locals = func.Locals.ToList();
        var nextLocalId = locals.Select(static local => local.Id.Value).DefaultIfEmpty(0).Max() + 1;
        var loopEntryBlockId = func.EntryBlockId;
        var needsEntryShim = false;
        var modified = NormalizeSameBlockTailReturnAliases(blocks);
        modified |= NormalizeTailReturnGotos(blocks);

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Terminator is not MirReturn ret ||
                !TryGetTailCall(block, ret, out var callIdx, out var call))
            {
                continue;
            }

            // Self-recursion check
            if (IsSelfRecursiveCall(func, call))
            {
                if (!_convertSelfRecursionToLoop)
                {
                    blocks[i] = MarkTailCall(block, callIdx, call);
                    modified = true;
                    continue;
                }

                if (!CanConvertSelfTailCall(func, call))
                {
                    continue;
                }

                MarkTailLoopParametersMutable(locals, func);
                blocks[i] = ConvertSelfTailCallToLoop(func, block, callIdx, call, locals, ref nextLocalId, loopEntryBlockId);
                needsEntryShim = true;
                modified = true;
            }
            else
            {
                // Non-self tail call: mark for LLVM tail call
                blocks[i] = MarkTailCall(block, callIdx, call);
                modified = true;
            }
        }

        if (!modified)
            return func;

        var entryBlockId = func.EntryBlockId;
        if (needsEntryShim)
        {
            entryBlockId = InsertEntryShim(blocks, loopEntryBlockId);
        }

        return new MirFunc
        {
            Name = func.Name,
            SourceName = func.SourceName,
            Locals = locals,
            BasicBlocks = blocks,
            EntryBlockId = entryBlockId,
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

    private static MirBasicBlock MarkTailCall(MirBasicBlock block, int callIdx, MirCall call)
    {
        var newInstructions = block.Instructions.ToList();
        newInstructions[callIdx] = new MirCall
        {
            Target = call.Target,
            Function = call.Function,
            Arguments = call.Arguments,
            IsTailCall = true,
            Span = call.Span
        };

        return new MirBasicBlock
        {
            Id = block.Id,
            Instructions = newInstructions,
            Terminator = block.Terminator,
            Span = block.Span,
            IsEntry = block.IsEntry
        };
    }

    private static MirModule CloneModuleWithFunctions(MirModule module, List<MirFunc> functions)
    {
        return new MirModule
        {
            Name = module.Name,
            PackageAlias = module.PackageAlias,
            PackageInstanceKey = module.PackageInstanceKey,
            Path = module.Path.ToList(),
            Functions = functions,
            DynamicTypeKeys = new Dictionary<int, string>(module.DynamicTypeKeys),
            TypeDescriptors = new Dictionary<int, TypeDescriptor>(module.TypeDescriptors),
            LinkLibraries = module.LinkLibraries.ToList(),
            CStructAccessors = new Dictionary<string, CStructAccessorInfo>(module.CStructAccessors),
            ConstructorLayouts = module.ConstructorLayouts.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToList()),
            TraitImpls = module.TraitImpls.ToList(),
            TraitInfos = module.TraitInfos.ToList(),
            TypeAliases = module.TypeAliases.ToList(),
            TypeConstructors = module.TypeConstructors.ToList(),
            SpecializationFailures = module.SpecializationFailures.ToList(),
            Span = module.Span
        };
    }

    private static bool NormalizeTailReturnGotos(List<MirBasicBlock> blocks)
    {
        var modified = false;
        var changed = true;

        while (changed)
        {
            changed = false;
            var blockMap = blocks.ToDictionary(static block => block.Id);

            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (block.Terminator is not MirGoto jump ||
                    jump.Target.Equals(block.Id) ||
                    !blockMap.TryGetValue(jump.Target, out var targetBlock) ||
                    targetBlock.Instructions.Count != 0 ||
                    targetBlock.Terminator is not MirReturn ret ||
                    !TryConvertGotoToReturn(block, ret, out var replacement))
                {
                    continue;
                }

                blocks[i] = replacement;
                changed = true;
                modified = true;
            }
        }

        return modified;
    }

    private static bool NormalizeSameBlockTailReturnAliases(List<MirBasicBlock> blocks)
    {
        var modified = false;

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Terminator is not MirReturn ret ||
                block.Instructions.Count < 2 ||
                ret.Value is not MirPlace returnPlace ||
                !TryRemoveReturnAlias(block.Instructions[^1], returnPlace, out var source) ||
                block.Instructions[^2] is not MirCall call ||
                !ReturnValueComesFromCall(source, call.Target))
            {
                continue;
            }

            blocks[i] = CloneBlock(
                block,
                block.Instructions.Take(block.Instructions.Count - 1).ToList(),
                new MirReturn { Value = source, Span = ret.Span });
            modified = true;
        }

        return modified;
    }

    private static bool TryConvertGotoToReturn(MirBasicBlock block, MirReturn ret, out MirBasicBlock replacement)
    {
        var instructions = block.Instructions;
        var returnValue = ret.Value;

        if (returnValue is MirPlace returnPlace &&
            instructions.Count > 0 &&
            TryRemoveReturnAlias(instructions[^1], returnPlace, out var source))
        {
            replacement = CloneBlock(
                block,
                instructions.Take(instructions.Count - 1).ToList(),
                new MirReturn { Value = source, Span = ret.Span });
            return true;
        }

        if (instructions.Count > 0 &&
            instructions[^1] is MirCall call &&
            ReturnValueComesFromCall(returnValue, call.Target))
        {
            replacement = CloneBlock(
                block,
                instructions.ToList(),
                new MirReturn { Value = returnValue, Span = ret.Span });
            return true;
        }

        replacement = null!;
        return false;
    }

    private static bool TryRemoveReturnAlias(
        MirInstruction instruction,
        MirPlace returnPlace,
        out MirOperand source)
    {
        switch (instruction)
        {
            case MirAssign assign when IsSamePlace(assign.Target, returnPlace):
                source = assign.Source;
                return true;
            case MirCopy copy when IsSamePlace(copy.Target, returnPlace):
                source = copy.Source;
                return true;
            case MirMove move when IsSamePlace(move.Target, returnPlace):
                source = move.Source;
                return true;
            default:
                source = null!;
                return false;
        }
    }

    private static bool TryGetTailCall(
        MirBasicBlock block,
        MirReturn ret,
        out int callIdx,
        out MirCall call)
    {
        callIdx = block.Instructions.Count - 1;
        if (callIdx < 0 || block.Instructions[callIdx] is not MirCall tailCall)
        {
            call = null!;
            return false;
        }

        if (!ReturnValueComesFromCall(ret.Value, tailCall.Target))
        {
            call = null!;
            return false;
        }

        call = tailCall;
        return true;
    }

    private static bool ReturnValueComesFromCall(MirOperand? returnValue, MirPlace? callTarget)
    {
        if (returnValue == null)
        {
            return callTarget == null;
        }

        return callTarget != null &&
               returnValue is MirPlace returnPlace &&
               IsSamePlace(returnPlace, callTarget);
    }

    private static bool IsSelfRecursiveCall(MirFunc func, MirCall call)
    {
        if (call.Function is not MirFunctionRef funcRef ||
            funcRef.SymbolKind != SymbolKind.Function)
        {
            return false;
        }

        if (func.SymbolId.IsValid && funcRef.SymbolId.IsValid)
        {
            return func.SymbolId.Equals(funcRef.SymbolId);
        }

        return !string.IsNullOrWhiteSpace(func.Name) &&
               string.Equals(funcRef.Name, func.Name, StringComparison.Ordinal);
    }

    private static bool CanConvertSelfTailCall(MirFunc func, MirCall call)
    {
        var parameterCount = func.Locals.Count(static local => local.IsParameter);
        return call.Arguments.Count == parameterCount;
    }

    private static void MarkTailLoopParametersMutable(List<MirLocal> locals, MirFunc func)
    {
        var parameterIds = func.Locals
            .Where(static local => local.IsParameter)
            .Select(static local => local.Id)
            .ToHashSet();

        for (var i = 0; i < locals.Count; i++)
        {
            if (!parameterIds.Contains(locals[i].Id) || locals[i].IsMutable)
            {
                continue;
            }

            locals[i] = new MirLocal
            {
                Id = locals[i].Id,
                Name = locals[i].Name,
                TypeId = locals[i].TypeId,
                IsMutable = true,
                IsParameter = locals[i].IsParameter,
                BindingMode = locals[i].BindingMode,
                Span = locals[i].Span
            };
        }
    }

    private MirBasicBlock ConvertSelfTailCallToLoop(
        MirFunc func,
        MirBasicBlock block,
        int callIdx,
        MirCall call,
        List<MirLocal> locals,
        ref int nextLocalId,
        BlockId loopEntryBlockId)
    {
        var paramLocals = func.Locals
            .Where(static local => local.IsParameter)
            .OrderBy(static local => local.Id.Value)
            .ToList();
        var newInstructions = new List<MirInstruction>();

        for (var i = 0; i < callIdx; i++)
        {
            newInstructions.Add(block.Instructions[i]);
        }

        var stagedArguments = new List<MirPlace>(call.Arguments.Count);

        for (var i = 0; i < call.Arguments.Count && i < paramLocals.Count; i++)
        {
            var arg = call.Arguments[i];
            var paramLocal = paramLocals[i];
            var stagedArgument = CreateSyntheticLocal(
                locals,
                ref nextLocalId,
                $"__tail_arg_{i}",
                paramLocal.TypeId,
                call.Span);

            stagedArguments.Add(stagedArgument);
            newInstructions.Add(new MirAssign
            {
                Target = stagedArgument,
                Source = arg,
                Span = call.Span
            });
        }

        for (var i = 0; i < stagedArguments.Count; i++)
        {
            var paramLocal = paramLocals[i];
            newInstructions.Add(new MirStore
            {
                Target = CreateLocalPlace(paramLocal.Id, paramLocal.TypeId, call.Span),
                Value = stagedArguments[i],
                Span = call.Span
            });
        }

        return new MirBasicBlock
        {
            Id = block.Id,
            Instructions = newInstructions,
            Terminator = new MirGoto { Target = loopEntryBlockId, Span = block.Span },
            Span = block.Span,
            IsEntry = block.IsEntry
        };
    }

    private static BlockId InsertEntryShim(List<MirBasicBlock> blocks, BlockId loopEntryBlockId)
    {
        var shimId = new BlockId
        {
            Value = blocks.Select(static block => block.Id.Value).DefaultIfEmpty(0).Max() + 1
        };

        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].IsEntry)
            {
                blocks[i] = CloneBlock(
                    blocks[i],
                    blocks[i].Instructions.ToList(),
                    blocks[i].Terminator ?? new MirReturn(),
                    isEntry: false);
            }
        }

        blocks.Insert(0, new MirBasicBlock
        {
            Id = shimId,
            IsEntry = true,
            Terminator = new MirGoto { Target = loopEntryBlockId }
        });

        return shimId;
    }

    private static MirPlace CreateSyntheticLocal(
        List<MirLocal> locals,
        ref int nextLocalId,
        string name,
        TypeId typeId,
        SourceSpan span)
    {
        var localId = new LocalId { Value = nextLocalId++ };
        locals.Add(new MirLocal
        {
            Id = localId,
            Name = name,
            TypeId = typeId,
            IsMutable = true,
            IsParameter = false,
            Span = span
        });

        return CreateLocalPlace(localId, typeId, span);
    }

    private static MirPlace CreateLocalPlace(LocalId localId, TypeId typeId, SourceSpan span)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = localId,
            TypeId = typeId,
            Span = span
        };
    }

    private static MirBasicBlock CloneBlock(
        MirBasicBlock block,
        List<MirInstruction> instructions,
        MirTerminator terminator,
        bool? isEntry = null)
    {
        return new MirBasicBlock
        {
            Id = block.Id,
            Instructions = instructions,
            Terminator = terminator,
            Span = block.Span,
            IsEntry = isEntry ?? block.IsEntry
        };
    }

    private static bool IsSamePlace(MirPlace left, MirPlace right)
    {
        if (left.Kind != right.Kind)
        {
            return false;
        }

        return left.Kind switch
        {
            PlaceKind.Local => left.Local.Equals(right.Local),
            PlaceKind.Field => string.Equals(left.FieldName, right.FieldName, StringComparison.Ordinal) &&
                               left.Base != null &&
                               right.Base != null &&
                               IsSamePlace(left.Base, right.Base),
            PlaceKind.Index => left.Base != null &&
                               right.Base != null &&
                               IsSamePlace(left.Base, right.Base) &&
                               Equals(left.Index, right.Index),
            PlaceKind.Deref => left.Base != null &&
                               right.Base != null &&
                               IsSamePlace(left.Base, right.Base),
            _ => false
        };
    }
}
