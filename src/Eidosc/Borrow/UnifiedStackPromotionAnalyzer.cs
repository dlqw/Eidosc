using Eidosc.Mir;
using Eidosc.Types;

namespace Eidosc.Borrow;

/// <summary>
/// 统一栈分配提升分析器。
/// </summary>
public sealed class UnifiedStackPromotionAnalyzer
{
    private static readonly IReadOnlyDictionary<string, FieldEscapeSummary> EmptySummaries =
        new Dictionary<string, FieldEscapeSummary>();

    private static readonly List<int> EmptyRcFieldIndices = [];
    private static readonly List<TypeId> EmptyCapturedTypeIds = [];

    private readonly MirFunc _function;
    private readonly IReadOnlyDictionary<string, FieldEscapeSummary> _fieldEscapeSummaries;
    private readonly BorrowModuleAnalysisContext? _context;

    public UnifiedStackPromotionHints Hints { get; } = new();

    public UnifiedStackPromotionAnalysisStats Stats { get; } = new();

    public UnifiedStackPromotionAnalyzer(MirFunc function)
        : this(function, EmptySummaries, (BorrowModuleAnalysisContext?)null)
    {
    }

    public UnifiedStackPromotionAnalyzer(
        MirFunc function,
        IReadOnlyDictionary<string, FieldEscapeSummary> fieldEscapeSummaries)
        : this(function, fieldEscapeSummaries, (BorrowModuleAnalysisContext?)null)
    {
    }

    public UnifiedStackPromotionAnalyzer(
        MirFunc function,
        IReadOnlyDictionary<string, FieldEscapeSummary> fieldEscapeSummaries,
        MirModule? module)
        : this(
            function,
            fieldEscapeSummaries,
            module == null ? null : new BorrowModuleAnalysisContext(module))
    {
    }

    public UnifiedStackPromotionAnalyzer(
        MirFunc function,
        IReadOnlyDictionary<string, FieldEscapeSummary> fieldEscapeSummaries,
        BorrowModuleAnalysisContext? context)
    {
        _function = function;
        _fieldEscapeSummaries = fieldEscapeSummaries;
        _context = context;
    }

    public void Analyze()
    {
        Stats.Reset();
        Hints.AllocInfoByLocal.Clear();
        Hints.PromotedLocals.Clear();

        var aliases = new LocalUnionFind();
        var escapedLocals = new HashSet<int>();
        List<AllocationCandidate>? candidates = null;

        foreach (var block in _function.BasicBlocks)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                Stats.InstructionsScanned++;
                AnalyzeInstruction(block.Id, i, block.Instructions[i], escapedLocals, aliases, ref candidates);
            }

            if (block.Terminator is MirReturn { Value: not null } ret)
            {
                CollectLocalValue(ret.Value, escapedLocals);
            }

            if (block.Terminator is MirSwitch { Discriminant: var disc })
            {
                CollectLocalValue(disc, escapedLocals);
            }
        }

        Stats.AliasEdges = aliases.EdgeCount;
        var escapedRoots = new HashSet<int>();
        foreach (var escapedLocal in escapedLocals)
        {
            escapedRoots.Add(aliases.Find(escapedLocal));
        }
        Stats.EscapedLocals = escapedRoots.Count;

        if (candidates == null)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            if (escapedRoots.Contains(aliases.Find(candidate.TargetLocal.Value)))
            {
                continue;
            }

            if (candidate.Kind == PromotableAllocationKind.AdtConstructor)
            {
                AddConstructorHint(candidate);
            }
            else
            {
                AddClosureHint(candidate);
            }
        }
    }

    public static bool MayHavePromotableAllocations(MirFunc function, MirModule? module = null)
    {
        return MayHavePromotableAllocations(
            function,
            module == null ? null : new BorrowModuleAnalysisContext(module));
    }

    public static bool MayHavePromotableAllocations(
        MirFunc function,
        BorrowModuleAnalysisContext? context)
    {
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (ReuseAnalyzer.IsHeapAllocatingConstructorCall(instruction, out _))
                {
                    return true;
                }

                if (instruction is not MirCall
                    {
                        Function: MirFunctionRef functionRef,
                        Target: MirPlace { Kind: PlaceKind.Local }
                    } call)
                {
                    continue;
                }

                if (context == null)
                {
                    return true;
                }

                if (context.TryGetParameterCount(functionRef, out var parameterCount) &&
                    call.Arguments.Count < parameterCount &&
                    context.TryGetFunctionIndex(functionRef, out var functionIndex) &&
                    !context.IsRuntimeWordAbi(functionIndex))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void AnalyzeInstruction(
        BlockId blockId,
        int instructionIndex,
        MirInstruction instruction,
        HashSet<int> escapedLocals,
        LocalUnionFind aliases,
        ref List<AllocationCandidate>? candidates)
    {
        switch (instruction)
        {
            case MirCall call:
                CollectEscapedFromCall(call, escapedLocals);
                CollectLocalValue(call.Function, escapedLocals);
                CollectAllocationCandidate(blockId, instructionIndex, call, ref candidates);
                break;

            case MirStore store:
                CollectLocalValue(store.Value, escapedLocals);
                break;

            case MirCopy copy:
                AddAlias(copy.Source, copy.Target, aliases);
                break;

            case MirMove move:
                AddAlias(move.Source, move.Target, aliases);
                break;

            case MirLoad load:
                CollectLocalValue(load.Source, escapedLocals);
                break;
        }
    }

    private void CollectEscapedFromCall(MirCall call, HashSet<int> escapedLocals)
    {
        if (call.Function is MirFunctionRef calleeRef &&
            _fieldEscapeSummaries.TryGetValue(GetStableKey(calleeRef), out var calleeSummary))
        {
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (!calleeSummary.ParamEscapes.TryGetValue(i, out var paramInfo))
                {
                    continue;
                }

                if (paramInfo.FullyEscapes || paramInfo.FieldEscapes.Count > 0)
                {
                    CollectLocalValue(call.Arguments[i], escapedLocals);
                }
            }
        }
        else
        {
            foreach (var arg in call.Arguments)
            {
                CollectLocalValue(arg, escapedLocals);
            }
        }
    }

    private void CollectAllocationCandidate(
        BlockId blockId,
        int instructionIndex,
        MirCall call,
        ref List<AllocationCandidate>? candidates)
    {
        if (ReuseAnalyzer.IsHeapAllocatingConstructorCall(call, out _) &&
            call.Target is MirPlace { Kind: PlaceKind.Local, Local: var constructorTarget } &&
            call.Function is MirFunctionRef)
        {
            candidates ??= [];
            candidates.Add(new AllocationCandidate(
                PromotableAllocationKind.AdtConstructor,
                blockId,
                instructionIndex,
                call,
                constructorTarget));
            Stats.ConstructorCandidates++;
            return;
        }

        if (call.Function is not MirFunctionRef { Name: not null } functionRef ||
            call.Target is not MirPlace { Kind: PlaceKind.Local, Local: var closureTarget } ||
            _context == null)
        {
            return;
        }

        Stats.ClosureLookups++;
        if (!_context.TryGetFunctionIndex(functionRef, out var calleeIndex))
        {
            Stats.ClosureLookupMisses++;
            return;
        }

        var parameterCount = _context.GetParameterCount(calleeIndex);
        if (call.Arguments.Count >= parameterCount ||
            _context.IsRuntimeWordAbi(calleeIndex))
        {
            return;
        }

        candidates ??= [];
        candidates.Add(new AllocationCandidate(
            PromotableAllocationKind.Closure,
            blockId,
            instructionIndex,
            call,
            closureTarget));
        Stats.ClosureCandidates++;
    }

    private void AddConstructorHint(AllocationCandidate candidate)
    {
        var call = candidate.Call;
        if (call.Function is not MirFunctionRef { Name: var ctorName } ctorRef)
        {
            return;
        }

        var fieldCount = call.Arguments.Count;
        var typeId = AdtConstructorTypeId.Compute(ctorRef.FunctionId, ctorRef.SymbolId, ctorName);
        var payloadSize = Math.Max(8L, fieldCount * 8L);
        var rcFieldIndices = BuildRcFieldIndices(call.Arguments);

        var info = new UnifiedStackAllocInfo(
            Kind: PromotableAllocationKind.AdtConstructor,
            Site: (candidate.BlockId, candidate.InstructionIndex),
            TargetLocal: candidate.TargetLocal,
            RcFieldIndices: rcFieldIndices)
        {
            TypeId = typeId,
            FieldCount = fieldCount,
            PayloadSize = payloadSize
        };

        Hints.AllocInfoByLocal[candidate.TargetLocal] = info;
        Hints.PromotedLocals.Add(candidate.TargetLocal);
        Stats.PromotedAllocations++;
    }

    private void AddClosureHint(AllocationCandidate candidate)
    {
        var call = candidate.Call;
        if (call.Function is not MirFunctionRef { Name: var functionName })
        {
            return;
        }

        var closureInfo = new UnifiedStackAllocInfo(
            Kind: PromotableAllocationKind.Closure,
            Site: (candidate.BlockId, candidate.InstructionIndex),
            TargetLocal: candidate.TargetLocal,
            RcFieldIndices: BuildRcFieldIndices(call.Arguments))
        {
            InvokeFunctionName = functionName,
            CapturedTypeIds = BuildCapturedTypeIds(call.Arguments)
        };

        Hints.AllocInfoByLocal[candidate.TargetLocal] = closureInfo;
        Hints.PromotedLocals.Add(candidate.TargetLocal);
        Stats.PromotedAllocations++;
    }

    private List<int> BuildRcFieldIndices(IReadOnlyList<MirOperand> arguments)
    {
        List<int>? rcFieldIndices = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            Stats.ManagedFieldChecks++;
            var isManaged = _context?.IsManagedType(arguments[i].TypeId)
                ?? TypeSemantics.IsManagedType(arguments[i].TypeId);
            if (!isManaged)
            {
                continue;
            }

            rcFieldIndices ??= [];
            rcFieldIndices.Add(i);
        }

        return rcFieldIndices ?? EmptyRcFieldIndices;
    }

    private static List<TypeId> BuildCapturedTypeIds(IReadOnlyList<MirOperand> arguments)
    {
        if (arguments.Count == 0)
        {
            return EmptyCapturedTypeIds;
        }

        var capturedTypeIds = new List<TypeId>(arguments.Count);
        for (int i = 0; i < arguments.Count; i++)
        {
            capturedTypeIds.Add(arguments[i].TypeId);
        }

        return capturedTypeIds;
    }

    private string GetStableKey(MirFunctionRef functionRef)
    {
        return _context?.GetStableKey(functionRef) ?? MirFunctionIdentity.GetStableKey(functionRef);
    }

    private static void CollectLocalValue(MirOperand? operand, HashSet<int> result)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var localId })
        {
            result.Add(localId.Value);
        }
    }

    private static void AddAlias(MirOperand? source, MirOperand? target, LocalUnionFind aliases)
    {
        if (source is not MirPlace { Kind: PlaceKind.Local, Local: var sourceLocal } ||
            target is not MirPlace { Kind: PlaceKind.Local, Local: var targetLocal })
        {
            return;
        }

        aliases.Union(sourceLocal.Value, targetLocal.Value);
    }

    private readonly record struct AllocationCandidate(
        PromotableAllocationKind Kind,
        BlockId BlockId,
        int InstructionIndex,
        MirCall Call,
        LocalId TargetLocal);
}
