using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Consumes block-local constant facts at MIR decision terminators.
/// Unknown values and pattern branches with bindings remain conservative.
/// </summary>
public sealed class DecisionSimplification : IMirOptimizationPass, IMirOptimizationMetricsProvider
{
    private long _switchesSeen;
    private long _matched;
    private long _defaulted;
    private long _unreachable;
    private long _preservedUnknown;
    private long _preservedBinding;
    private long _crossBlockResolved;
    private long _joinedFacts;
    private long _edgeRefinedFacts;

    public string Name => "DecisionSimplification";

    public MirModule Run(MirModule module)
    {
        _switchesSeen = 0;
        _matched = 0;
        _defaulted = 0;
        _unreachable = 0;
        _preservedUnknown = 0;
        _preservedBinding = 0;
        _crossBlockResolved = 0;
        _joinedFacts = 0;
        _edgeRefinedFacts = 0;

        List<MirFunc>? optimizedFunctions = null;
        for (var index = 0; index < module.Functions.Count; index++)
        {
            var function = module.Functions[index];
            var optimized = OptimizeFunction(function);
            if (optimizedFunctions == null && ReferenceEquals(optimized, function))
            {
                continue;
            }

            optimizedFunctions ??= module.Functions.Take(index).ToList();
            optimizedFunctions.Add(optimized);
        }

        if (optimizedFunctions == null)
        {
            return module;
        }

        return MirOptimizationCloner.WithFunctions(module, optimizedFunctions);
    }

    public IReadOnlyDictionary<string, long> GetMetricsSnapshot() => new Dictionary<string, long>
    {
        ["switches.seen"] = _switchesSeen,
        ["decisions.matched"] = _matched,
        ["decisions.defaulted"] = _defaulted,
        ["decisions.unreachable"] = _unreachable,
        ["decisions.preserved.unknown"] = _preservedUnknown,
        ["decisions.preserved.binding"] = _preservedBinding,
        ["decisions.cross_block_resolved"] = _crossBlockResolved,
        ["facts.joined"] = _joinedFacts,
        ["facts.edge_refined"] = _edgeRefinedFacts
    };

    private MirFunc OptimizeFunction(MirFunc function)
    {
        var entryFacts = AnalyzeEntryFacts(function);
        List<MirBasicBlock>? optimizedBlocks = null;
        for (var index = 0; index < function.BasicBlocks.Count; index++)
        {
            var block = function.BasicBlocks[index];
            var optimizedTerminator = SimplifyTerminator(
                block,
                entryFacts.GetValueOrDefault(block.Id));
            if (optimizedBlocks == null && ReferenceEquals(optimizedTerminator, block.Terminator))
            {
                continue;
            }

            optimizedBlocks ??= function.BasicBlocks.Take(index).ToList();
            optimizedBlocks.Add(ReferenceEquals(optimizedTerminator, block.Terminator)
                ? block
                : new MirBasicBlock
                {
                    Id = block.Id,
                    Instructions = block.Instructions,
                    Terminator = optimizedTerminator,
                    Span = block.Span,
                    IsEntry = block.IsEntry
                });
        }

        if (optimizedBlocks == null)
        {
            return function;
        }

        return MirOptimizationCloner.WithBlocks(function, optimizedBlocks);
    }

    private MirTerminator? SimplifyTerminator(
        MirBasicBlock block,
        IReadOnlyDictionary<LocalId, MirConstant>? entryFacts)
    {
        if (block.Terminator is not MirSwitch { } switchTerminator)
        {
            return block.Terminator;
        }

        var outcome = EvaluateDecision(switchTerminator, block.Instructions, entryFacts);
        return outcome.Kind switch
        {
            DecisionOutcomeKind.Matched or DecisionOutcomeKind.Default => new MirGoto
            {
                Target = outcome.Target!.Value,
                Span = switchTerminator.Span
            },
            DecisionOutcomeKind.Unreachable => new MirUnreachable
            {
                Span = switchTerminator.Span
            },
            _ => block.Terminator
        };
    }

    private DecisionOutcome EvaluateDecision(
        MirSwitch switchTerminator,
        IReadOnlyList<MirInstruction> instructions,
        IReadOnlyDictionary<LocalId, MirConstant>? entryFacts)
    {
        _switchesSeen++;
        if (switchTerminator.Branches.Any(static branch => branch.BoundVariable.HasValue))
        {
            _preservedBinding++;
            return new(DecisionOutcomeKind.Preserve, null);
        }

        var facts = TransferFacts(instructions, entryFacts);
        if (TryResolve(switchTerminator.Discriminant, facts) is not { } discriminant)
        {
            _preservedUnknown++;
            return new(DecisionOutcomeKind.Preserve, null);
        }

        if (entryFacts != null && TryResolve(switchTerminator.Discriminant, entryFacts) != null)
        {
            _crossBlockResolved++;
        }

        foreach (var branch in switchTerminator.Branches)
        {
            if (branch.Value.TypeId == discriminant.TypeId &&
                branch.Value.Value.Equals(discriminant.Value))
            {
                _matched++;
                return new(DecisionOutcomeKind.Matched, branch.Target);
            }
        }

        return switchTerminator.DefaultTarget is { } defaultTarget
            ? RecordDefault(defaultTarget)
            : RecordUnreachable();
    }

    private DecisionOutcome RecordDefault(BlockId target)
    {
        _defaulted++;
        return new(DecisionOutcomeKind.Default, target);
    }

    private DecisionOutcome RecordUnreachable()
    {
        _unreachable++;
        return new(DecisionOutcomeKind.Unreachable, null);
    }

    private readonly record struct DecisionOutcome(DecisionOutcomeKind Kind, BlockId? Target);

    private enum DecisionOutcomeKind
    {
        Preserve,
        Matched,
        Default,
        Unreachable
    }

    private Dictionary<BlockId, IReadOnlyDictionary<LocalId, MirConstant>> AnalyzeEntryFacts(
        MirFunc function)
    {
        var blocks = function.BasicBlocks.ToDictionary(static block => block.Id);
        var graph = new ControlFlowGraph(function);
        var entryFacts = function.BasicBlocks.ToDictionary(
            static block => block.Id,
            static _ => new Dictionary<LocalId, MirConstant>());
        var exitFacts = function.BasicBlocks.ToDictionary(
            static block => block.Id,
            static _ => new Dictionary<LocalId, MirConstant>());
        var worklist = new Queue<BlockId>(function.BasicBlocks.Select(static block => block.Id));
        var queued = function.BasicBlocks.Select(static block => block.Id).ToHashSet();

        while (worklist.Count > 0)
        {
            var blockId = worklist.Dequeue();
            queued.Remove(blockId);
            var block = blocks[blockId];
            var incoming = blockId == function.EntryBlockId
                ? new Dictionary<LocalId, MirConstant>()
                : JoinPredecessorFacts(blockId, graph, blocks, exitFacts);
            if (!FactsEqual(entryFacts[blockId], incoming))
            {
                entryFacts[blockId] = incoming;
            }

            var outgoing = TransferFacts(block.Instructions, incoming);
            if (FactsEqual(exitFacts[blockId], outgoing))
            {
                continue;
            }

            exitFacts[blockId] = outgoing;
            foreach (var successor in graph.GetSuccessors(blockId))
            {
                if (blocks.ContainsKey(successor) && queued.Add(successor))
                {
                    worklist.Enqueue(successor);
                }
            }
        }

        _joinedFacts += function.BasicBlocks
            .Where(block => graph.GetPredecessors(block.Id).Count > 1)
            .Sum(block => entryFacts[block.Id].Count);
        _edgeRefinedFacts += CountRefinableEdges(function);

        return entryFacts.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyDictionary<LocalId, MirConstant>)pair.Value);
    }

    private Dictionary<LocalId, MirConstant> JoinPredecessorFacts(
        BlockId blockId,
        ControlFlowGraph graph,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blocks,
        IReadOnlyDictionary<BlockId, Dictionary<LocalId, MirConstant>> exitFacts)
    {
        Dictionary<LocalId, MirConstant>? joined = null;
        foreach (var predecessor in graph.GetPredecessors(blockId))
        {
            var edgeFacts = RefineEdgeFacts(
                blocks[predecessor],
                blockId,
                exitFacts[predecessor]);
            if (joined == null)
            {
                joined = edgeFacts;
                continue;
            }

            foreach (var local in joined.Keys.ToList())
            {
                if (!edgeFacts.TryGetValue(local, out var candidate) ||
                    !SameConstant(joined[local], candidate))
                {
                    joined.Remove(local);
                }
            }
        }

        return joined ?? [];
    }

    private Dictionary<LocalId, MirConstant> RefineEdgeFacts(
        MirBasicBlock predecessor,
        BlockId successor,
        IReadOnlyDictionary<LocalId, MirConstant> exitFacts)
    {
        var refined = new Dictionary<LocalId, MirConstant>(exitFacts);
        if (predecessor.Terminator is not MirSwitch
            {
                Discriminant: MirPlace { Kind: PlaceKind.Local, Local: var local }
            } switchTerminator)
        {
            return refined;
        }

        var matchingBranches = switchTerminator.Branches
            .Where(branch => branch.Target == successor)
            .ToList();
        if (matchingBranches.Count == 1)
        {
            SetFact(local, matchingBranches[0].Value, refined);
            return refined;
        }

        if (matchingBranches.Count == 0 &&
            switchTerminator.DefaultTarget == successor &&
            TryInferBooleanDefault(switchTerminator, out var defaultValue))
        {
            SetFact(local, defaultValue, refined);
        }

        return refined;
    }

    private static int CountRefinableEdges(MirFunc function)
    {
        var count = 0;
        foreach (var block in function.BasicBlocks)
        {
            if (block.Terminator is not MirSwitch
                {
                    Discriminant: MirPlace { Kind: PlaceKind.Local }
                } switchTerminator)
            {
                continue;
            }

            count += switchTerminator.Branches
                .Select(static branch => branch.Target)
                .Distinct()
                .Count();
            if (switchTerminator.DefaultTarget.HasValue &&
                TryInferBooleanDefault(switchTerminator, out _))
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryInferBooleanDefault(
        MirSwitch switchTerminator,
        out MirConstant defaultValue)
    {
        if (switchTerminator.Branches.Count == 1 &&
            switchTerminator.Branches[0].Value is
            {
                TypeId.Value: BaseTypes.BoolId,
                Value: MirConstantValue.BoolValue { Value: var branchValue }
            } branchConstant)
        {
            defaultValue = branchConstant with
            {
                Value = new MirConstantValue.BoolValue(!branchValue)
            };
            return true;
        }

        defaultValue = null!;
        return false;
    }

    private static bool FactsEqual(
        IReadOnlyDictionary<LocalId, MirConstant> left,
        IReadOnlyDictionary<LocalId, MirConstant> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (local, constant) in left)
        {
            if (!right.TryGetValue(local, out var candidate) ||
                !SameConstant(constant, candidate))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameConstant(MirConstant left, MirConstant right) =>
        left.TypeId == right.TypeId && left.Value.Equals(right.Value);

    private static Dictionary<LocalId, MirConstant> TransferFacts(
        IReadOnlyList<MirInstruction> instructions,
        IReadOnlyDictionary<LocalId, MirConstant>? initialFacts = null)
    {
        var facts = initialFacts == null
            ? new Dictionary<LocalId, MirConstant>()
            : new Dictionary<LocalId, MirConstant>(initialFacts);
        foreach (var instruction in instructions)
        {
            switch (instruction)
            {
                case MirAssign { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } assign:
                    SetFact(target, TryResolve(assign.Source, facts), facts);
                    break;
                case MirCopy { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } copy:
                    SetFact(target, TryResolve(copy.Source, facts), facts);
                    break;
                case MirBinOp { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } binOp:
                    SetFact(target, TryFoldBinary(binOp, facts), facts);
                    break;
                case MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } unaryOp:
                    SetFact(target, TryFoldUnary(unaryOp, facts), facts);
                    break;
                case MirSelect { Target: { Kind: PlaceKind.Local, Local: var target } } select:
                    SetFact(target, TryFoldSelect(select, facts), facts);
                    break;
                case MirMove { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } move:
                    facts.Remove(target);
                    if (move.Source is { Kind: PlaceKind.Local, Local: var source })
                    {
                        facts.Remove(source);
                    }

                    break;
                case MirDrop { Value: MirPlace { Kind: PlaceKind.Local, Local: var dropped } }:
                    facts.Remove(dropped);
                    break;
                default:
                    if (GetDefinedLocal(instruction) is { } definedLocal)
                    {
                        facts.Remove(definedLocal);
                    }

                    break;
            }
        }

        return facts;
    }

    private static MirConstant? TryFoldBinary(
        MirBinOp instruction,
        IReadOnlyDictionary<LocalId, MirConstant> facts)
    {
        if (TryResolve(instruction.Left, facts) is not { } left ||
            TryResolve(instruction.Right, facts) is not { } right)
        {
            return null;
        }

        return ConstantFolding.TryFoldConstants(instruction.Operator, left, right);
    }

    private static MirConstant? TryFoldUnary(
        MirUnaryOp instruction,
        IReadOnlyDictionary<LocalId, MirConstant> facts)
    {
        if (TryResolve(instruction.Operand, facts) is not { } operand)
        {
            return null;
        }

        return ConstantFolding.TryFoldUnary(instruction.Operator, operand);
    }

    private static MirConstant? TryFoldSelect(
        MirSelect instruction,
        IReadOnlyDictionary<LocalId, MirConstant> facts)
    {
        var trueValue = TryResolve(instruction.TrueValue, facts);
        var falseValue = TryResolve(instruction.FalseValue, facts);
        if (trueValue != null && falseValue != null && SameConstant(trueValue, falseValue))
        {
            return trueValue;
        }

        return TryResolve(instruction.Condition, facts) is
        {
            Value: MirConstantValue.BoolValue { Value: var condition }
        }
            ? condition ? trueValue : falseValue
            : null;
    }

    private static MirConstant? TryResolve(
        MirOperand operand,
        IReadOnlyDictionary<LocalId, MirConstant> facts) => operand switch
        {
            MirConstant constant => constant,
            MirPlace { Kind: PlaceKind.Local, Local: var local } => facts.GetValueOrDefault(local),
            _ => null
        };

    private static void SetFact(
        LocalId local,
        MirConstant? constant,
        Dictionary<LocalId, MirConstant> facts)
    {
        if (constant == null)
        {
            facts.Remove(local);
            return;
        }

        facts[local] = constant;
    }

    private static LocalId? GetDefinedLocal(MirInstruction instruction) => instruction switch
    {
        MirAssign { Target: { Kind: PlaceKind.Local, Local: var local } } => local,
        MirCaseInject { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
        MirCall { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
        MirLoad { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
        MirAlloc { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
        MirCopy { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
        MirMove { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
        MirStore { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
        MirBinOp { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
        MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
        MirSelect { Target: { Kind: PlaceKind.Local, Local: var local } } => local,
        _ => null
    };
}
