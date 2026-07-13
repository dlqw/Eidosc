using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// 死代码消除优化 - 移除不可达基本块 + 未使用的局部变量赋值
/// </summary>
public sealed class DeadCodeElimination : IMirOptimizationPass, IFunctionOptimizationSummaryConsumer
{
    private FunctionOptimizationSummaryIndex? _functionSummaries;
    private IReadOnlyDictionary<string, int> _parameterCounts = new Dictionary<string, int>(StringComparer.Ordinal);

    FunctionOptimizationSummaryIndex IFunctionOptimizationSummaryConsumer.FunctionSummaries
    {
        set => _functionSummaries = value;
    }

    public string Name => "DeadCodeElimination";

    public MirModule Run(MirModule module)
    {
        _parameterCounts = module.Functions
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Locals.Count(static local => local.IsParameter),
                StringComparer.Ordinal);
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

    private MirFunc OptimizeFunction(MirFunc func)
    {
        if (func.IsExternal)
            return func;

        // Phase 1: Remove unreachable blocks
        var reachableBlocks = new HashSet<BlockId>();
        var originalBlockMap = CreateBlockMap(func.BasicBlocks);
        CollectReachableBlocks(func, originalBlockMap, reachableBlocks);

        var removedUnreachableBlocks = reachableBlocks.Count != func.BasicBlocks.Count;
        var blocks = removedUnreachableBlocks
            ? FilterReachableBlocks(func.BasicBlocks, reachableBlocks)
            : func.BasicBlocks;

        // Phase 2: Remove dead assignments (iterative)
        bool changed;
        var removedDeadInstructions = false;
        do
        {
            changed = false;
            var liveness = ComputeLiveness(blocks);

            List<MirBasicBlock>? newBlocks = null;
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                var (newBlock, didRemove) = RemoveDeadInBlock(block, liveness);
                if (newBlocks != null)
                {
                    newBlocks.Add(newBlock);
                }
                else if (didRemove)
                {
                    newBlocks = new List<MirBasicBlock>(blocks.Count);
                    for (var previous = 0; previous < i; previous++)
                    {
                        newBlocks.Add(blocks[previous]);
                    }

                    newBlocks.Add(newBlock);
                }

                if (didRemove)
                {
                    changed = true;
                    removedDeadInstructions = true;
                }
            }

            if (newBlocks != null)
            {
                blocks = newBlocks;
            }
        } while (changed);

        if (!removedUnreachableBlocks && !removedDeadInstructions)
        {
            return func;
        }

        return new MirFunc
        {
            Name = func.Name,
            SourceName = func.SourceName,
            Locals = func.Locals,
            BasicBlocks = blocks,
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

    // ---- Phase 1: Unreachable block removal (unchanged) ----

    private static Dictionary<BlockId, MirBasicBlock> CreateBlockMap(IReadOnlyList<MirBasicBlock> blocks)
    {
        var blockMap = new Dictionary<BlockId, MirBasicBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            blockMap[block.Id] = block;
        }

        return blockMap;
    }

    private static List<MirBasicBlock> FilterReachableBlocks(
        IReadOnlyList<MirBasicBlock> blocks,
        HashSet<BlockId> reachableBlocks)
    {
        var filtered = new List<MirBasicBlock>(reachableBlocks.Count);
        foreach (var block in blocks)
        {
            if (reachableBlocks.Contains(block.Id))
            {
                filtered.Add(block);
            }
        }

        return filtered;
    }

    private static void CollectReachableBlocks(
        MirFunc func,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blockMap,
        HashSet<BlockId> reachable)
    {
        if (func.BasicBlocks.Count == 0) return;

        var entryBlockId = func.EntryBlockId;
        if (!entryBlockId.IsValid || !blockMap.ContainsKey(entryBlockId))
        {
            entryBlockId = func.BasicBlocks[0].Id;
        }

        var queue = new Queue<BlockId>();
        queue.Enqueue(entryBlockId);
        reachable.Add(entryBlockId);

        while (queue.Count > 0)
        {
            var blockId = queue.Dequeue();
            blockMap.TryGetValue(blockId, out var block);
            if (block?.Terminator == null) continue;

            foreach (var succ in GetSuccessors(block.Terminator))
            {
                if (reachable.Add(succ))
                {
                    queue.Enqueue(succ);
                }
            }
        }
    }

    private static List<BlockId> GetSuccessors(MirTerminator terminator)
    {
        var successors = new List<BlockId>();

        switch (terminator)
        {
            case MirGoto gotoTerm:
                successors.Add(gotoTerm.Target);
                break;
            case MirReturn:
                break;
            case MirSwitch sw:
                foreach (var branch in sw.Branches)
                {
                    successors.Add(branch.Target);
                }
                if (sw.DefaultTarget.HasValue)
                {
                    successors.Add(sw.DefaultTarget.Value);
                }
                break;
        }

        return successors;
    }

    // ---- Phase 2: Dead assignment removal ----

    /// <summary>
    /// Block-level liveness info computed via backward dataflow.
    /// </summary>
    private sealed class BlockLiveness
    {
        public HashSet<LocalId> Use { get; } = [];
        public HashSet<LocalId> Def { get; } = [];
        public HashSet<LocalId> LiveIn { get; } = [];
        public HashSet<LocalId> LiveOut { get; } = [];
    }

    private Dictionary<BlockId, BlockLiveness> ComputeLiveness(List<MirBasicBlock> blocks)
    {
        var livenessMap = new Dictionary<BlockId, BlockLiveness>(blocks.Count);
        foreach (var block in blocks)
        {
            livenessMap[block.Id] = new BlockLiveness();
        }

        // Step 1: Compute USE and DEF per block
        foreach (var block in blocks)
        {
            var info = livenessMap[block.Id];
            var defined = new HashSet<LocalId>();

            foreach (var instr in block.Instructions)
            {
                // Add uses before checking defs (USE = used before defined)
                AddUsedLocalsFromInstruction(instr, info.Use, defined);

                // Add defs
                var defLocal = GetDefinedLocal(instr);
                if (defLocal.HasValue)
                {
                    defined.Add(defLocal.Value);
                    info.Def.Add(defLocal.Value);
                }
            }

            AddUsedLocalsFromTerminator(block.Terminator, info.Use, defined);
            info.Def.UnionWith(defined);
        }

        // Step 2: Backward dataflow: LiveIn = (LiveOut ∪ Use) - Def
        //         LiveOut = union LiveIn[succ]
        var liveOutScratch = new HashSet<LocalId>();
        var liveInScratch = new HashSet<LocalId>();
        bool changed;
        do
        {
            changed = false;
            for (var blockIndex = blocks.Count - 1; blockIndex >= 0; blockIndex--)
            {
                var block = blocks[blockIndex];
                var info = livenessMap[block.Id];

                liveOutScratch.Clear();
                AddSuccessorLiveIns(block.Terminator, livenessMap, liveOutScratch);
                if (!liveOutScratch.SetEquals(info.LiveOut))
                {
                    info.LiveOut.Clear();
                    info.LiveOut.UnionWith(liveOutScratch);
                    changed = true;
                }

                liveInScratch.Clear();
                liveInScratch.UnionWith(info.LiveOut);
                liveInScratch.UnionWith(info.Use);
                liveInScratch.ExceptWith(info.Def);
                if (!liveInScratch.SetEquals(info.LiveIn))
                {
                    info.LiveIn.Clear();
                    info.LiveIn.UnionWith(liveInScratch);
                    changed = true;
                }
            }
        } while (changed);

        return livenessMap;
    }

    private static void AddSuccessorLiveIns(
        MirTerminator? terminator,
        IReadOnlyDictionary<BlockId, BlockLiveness> livenessMap,
        HashSet<LocalId> liveOut)
    {
        switch (terminator)
        {
            case MirGoto jump:
                AddSuccessorLiveIn(jump.Target, livenessMap, liveOut);
                break;
            case MirSwitch sw:
                foreach (var branch in sw.Branches)
                {
                    AddSuccessorLiveIn(branch.Target, livenessMap, liveOut);
                }
                if (sw.DefaultTarget.HasValue)
                {
                    AddSuccessorLiveIn(sw.DefaultTarget.Value, livenessMap, liveOut);
                }
                break;
        }
    }

    private static void AddSuccessorLiveIn(
        BlockId blockId,
        IReadOnlyDictionary<BlockId, BlockLiveness> livenessMap,
        HashSet<LocalId> liveOut)
    {
        if (livenessMap.TryGetValue(blockId, out var info))
        {
            liveOut.UnionWith(info.LiveIn);
        }
    }

    private (MirBasicBlock Block, bool Changed) RemoveDeadInBlock(
        MirBasicBlock block,
        Dictionary<BlockId, BlockLiveness> liveness)
    {
        if (!liveness.TryGetValue(block.Id, out var info))
            return (block, false);

        // Start with live-out plus values consumed by the block terminator.
        var live = new HashSet<LocalId>(info.LiveOut);
        AddUsedLocalsFromTerminator(block.Terminator, live, []);

        var kept = new List<MirInstruction>();
        bool changed = false;

        for (int i = block.Instructions.Count - 1; i >= 0; i--)
        {
            var instr = block.Instructions[i];
            var defLocal = GetDefinedLocal(instr);

            // Check if this is a dead definition (defined local not live after this instruction)
            if (defLocal.HasValue && !live.Contains(defLocal.Value) && !HasSideEffects(instr))
            {
                changed = true;
                // Skip this instruction (don't add to kept, don't update live set)
                continue;
            }

            // Update live set: remove defs, add uses
            if (defLocal.HasValue)
                live.Remove(defLocal.Value);
            AddUsedLocalsFromInstruction(instr, live, []);

            kept.Add(instr);
        }

        if (!changed)
            return (block, false);

        kept.Reverse();

        return (new MirBasicBlock
        {
            Id = block.Id,
            Instructions = kept,
            Terminator = block.Terminator,
            Span = block.Span,
            IsEntry = block.IsEntry
        }, true);
    }

    // ---- Side effect detection ----

    private bool HasSideEffects(MirInstruction instr)
    {
        return instr switch
        {
            MirCall { Function: MirFunctionRef functionRef } call =>
                !_parameterCounts.TryGetValue(MirFunctionIdentity.GetStableKey(functionRef), out var parameterCount) ||
                call.Arguments.Count < parameterCount ||
                _functionSummaries == null ||
                !_functionSummaries.TryGet(functionRef, out var summary) ||
                !summary.CanEliminateUnusedCall,
            MirCall => true,
            MirStore => true,             // Memory write
            MirDrop => true,              // RC decrement
            // No side effects:
            MirAssign => false,
            MirBinOp => false,
            MirUnaryOp => false,
            MirLoad => false,
            MirAlloc => false,            // Stack alloc, safe to remove if unused
            MirCopy => false,
            MirMove => true,              // Ownership transfer invalidates the source alias
            _ => true                     // Unknown → conservative
        };
    }

    // ---- Local ID extraction helpers ----

    private static LocalId? GetDefinedLocal(MirInstruction instr)
    {
        return instr switch
        {
            MirAssign { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirCall { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirLoad { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirAlloc { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirCopy { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirMove { Target: { Kind: PlaceKind.Local } place } => place.Local,
            MirBinOp { Target: MirPlace { Kind: PlaceKind.Local } place } => place.Local,
            MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local } place } => place.Local,
            _ => null
        };
    }

    private static void AddUsedLocalsFromInstruction(
        MirInstruction instr,
        HashSet<LocalId> useSet,
        HashSet<LocalId> defined)
    {
        switch (instr)
        {
            case MirAssign assign:
                AddUsedLocalsFromOperand(assign.Source, useSet, defined);
                break;
            case MirCall call:
                AddUsedLocalsFromOperand(call.Function, useSet, defined);
                AddUsedLocalsFromOperands(call.Arguments, useSet, defined);
                break;
            case MirBinOp binOp:
                AddUsedLocalsFromOperand(binOp.Left, useSet, defined);
                AddUsedLocalsFromOperand(binOp.Right, useSet, defined);
                break;
            case MirUnaryOp unaryOp:
                AddUsedLocalsFromOperand(unaryOp.Operand, useSet, defined);
                break;
            case MirLoad load:
                AddUsedLocalsFromOperand(load.Source, useSet, defined);
                break;
            case MirStore store:
                AddUsedLocalsFromPlace(store.Target, useSet, defined);
                AddUsedLocalsFromOperand(store.Value, useSet, defined);
                break;
            case MirDrop drop:
                AddUsedLocalsFromOperand(drop.Value, useSet, defined);
                break;
            case MirCopy copy:
                AddUsedLocalsFromPlace(copy.Source, useSet, defined);
                break;
            case MirMove move:
                AddUsedLocalsFromPlace(move.Source, useSet, defined);
                break;
        }
    }

    private static void AddUsedLocalsFromOperands(
        IReadOnlyList<MirOperand> operands,
        HashSet<LocalId> useSet,
        HashSet<LocalId> defined)
    {
        foreach (var operand in operands)
        {
            AddUsedLocalsFromOperand(operand, useSet, defined);
        }
    }

    private static void AddUsedLocalsFromOperand(
        MirOperand operand,
        HashSet<LocalId> useSet,
        HashSet<LocalId> defined)
    {
        if (operand is MirPlace place)
        {
            AddUsedLocalsFromPlace(place, useSet, defined);
        }
    }

    private static void AddUsedLocalsFromPlace(
        MirPlace place,
        HashSet<LocalId> useSet,
        HashSet<LocalId> defined)
    {
        switch (place.Kind)
        {
            case PlaceKind.Local:
                if (!defined.Contains(place.Local))
                {
                    useSet.Add(place.Local);
                }
                break;
            case PlaceKind.Field:
            case PlaceKind.Deref:
                if (place.Base != null)
                {
                    AddUsedLocalsFromPlace(place.Base, useSet, defined);
                }
                break;
            case PlaceKind.Index:
                if (place.Base != null)
                {
                    AddUsedLocalsFromPlace(place.Base, useSet, defined);
                }
                if (place.Index != null)
                {
                    AddUsedLocalsFromOperand(place.Index, useSet, defined);
                }
                break;
        }
    }

    private static void AddUsedLocalsFromTerminator(MirTerminator? terminator, HashSet<LocalId> useSet, HashSet<LocalId> defined)
    {
        switch (terminator)
        {
            case MirReturn { Value: { } retVal }:
                AddUsedLocalsFromOperand(retVal, useSet, defined);
                break;
            case MirSwitch sw:
                AddUsedLocalsFromOperand(sw.Discriminant, useSet, defined);
                foreach (var branch in sw.Branches)
                {
                    if (branch.BoundVariable.HasValue)
                        defined.Add(branch.BoundVariable.Value);
                }
                break;
        }
    }
}
