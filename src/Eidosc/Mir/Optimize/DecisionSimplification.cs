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

    public string Name => "DecisionSimplification";

    public MirModule Run(MirModule module)
    {
        _switchesSeen = 0;
        _matched = 0;
        _defaulted = 0;
        _unreachable = 0;
        _preservedUnknown = 0;
        _preservedBinding = 0;

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
        ["decisions.preserved.binding"] = _preservedBinding
    };

    private MirFunc OptimizeFunction(MirFunc function)
    {
        List<MirBasicBlock>? optimizedBlocks = null;
        for (var index = 0; index < function.BasicBlocks.Count; index++)
        {
            var block = function.BasicBlocks[index];
            var optimizedTerminator = SimplifyTerminator(block);
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

    private MirTerminator? SimplifyTerminator(MirBasicBlock block)
    {
        if (block.Terminator is not MirSwitch { } switchTerminator)
        {
            return block.Terminator;
        }

        var outcome = EvaluateDecision(switchTerminator, block.Instructions);
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
        IReadOnlyList<MirInstruction> instructions)
    {
        _switchesSeen++;
        if (switchTerminator.Branches.Any(static branch => branch.BoundVariable.HasValue))
        {
            _preservedBinding++;
            return new(DecisionOutcomeKind.Preserve, null);
        }

        var facts = CollectFacts(instructions);
        if (TryResolve(switchTerminator.Discriminant, facts) is not { } discriminant)
        {
            _preservedUnknown++;
            return new(DecisionOutcomeKind.Preserve, null);
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

    private static Dictionary<LocalId, MirConstant> CollectFacts(
        IReadOnlyList<MirInstruction> instructions)
    {
        var facts = new Dictionary<LocalId, MirConstant>();
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
        _ => null
    };
}
