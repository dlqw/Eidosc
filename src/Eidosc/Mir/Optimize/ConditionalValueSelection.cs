using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Converts pure scalar if diamonds into value selection while preserving
/// effectful and ownership-sensitive control flow.
/// </summary>
public sealed class ConditionalValueSelection : IMirOptimizationPass, IMirOptimizationMetricsProvider
{
    private long _diamondsSeen;
    private long _selected;
    private long _preservedShape;
    private long _preservedEffect;
    private long _preservedOwnership;
    private long _jumpTableCandidates;
    private long _binaryTreeCandidates;
    private long _conditionalBranchCandidates;

    public string Name => "ConditionalValueSelection";

    public MirModule Run(MirModule module)
    {
        _diamondsSeen = 0;
        _selected = 0;
        _preservedShape = 0;
        _preservedEffect = 0;
        _preservedOwnership = 0;
        _jumpTableCandidates = 0;
        _binaryTreeCandidates = 0;
        _conditionalBranchCandidates = 0;

        List<MirFunc>? functions = null;
        for (var index = 0; index < module.Functions.Count; index++)
        {
            var function = module.Functions[index];
            var optimized = OptimizeFunction(function);
            if (functions == null && ReferenceEquals(optimized, function))
            {
                continue;
            }

            functions ??= module.Functions.Take(index).ToList();
            functions.Add(optimized);
        }

        return functions == null
            ? module
            : MirOptimizationCloner.WithFunctions(module, functions);
    }

    public IReadOnlyDictionary<string, long> GetMetricsSnapshot() => new Dictionary<string, long>
    {
        ["decisions.representation.diamonds_seen"] = _diamondsSeen,
        ["decisions.representation.select"] = _selected,
        ["decisions.representation.preserved.shape"] = _preservedShape,
        ["decisions.representation.preserved.effect"] = _preservedEffect,
        ["decisions.representation.preserved.ownership"] = _preservedOwnership,
        ["decisions.representation.jump_table_candidate"] = _jumpTableCandidates,
        ["decisions.representation.binary_tree_candidate"] = _binaryTreeCandidates,
        ["decisions.representation.conditional_branch_candidate"] = _conditionalBranchCandidates
    };

    private MirFunc OptimizeFunction(MirFunc function)
    {
        var blocks = function.BasicBlocks.ToDictionary(static block => block.Id);
        var graph = new ControlFlowGraph(function);
        List<MirBasicBlock>? optimized = null;

        for (var index = 0; index < function.BasicBlocks.Count; index++)
        {
            var block = function.BasicBlocks[index];
            var replacement = TryRewriteBlock(block, blocks, graph);
            if (optimized == null && ReferenceEquals(replacement, block))
            {
                continue;
            }

            optimized ??= function.BasicBlocks.Take(index).ToList();
            optimized.Add(replacement);
        }

        return optimized == null
            ? function
            : MirOptimizationCloner.WithBlocks(function, optimized);
    }

    private MirBasicBlock TryRewriteBlock(
        MirBasicBlock block,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blocks,
        ControlFlowGraph graph)
    {
        if (block.Terminator is MirSwitch multiway)
        {
            RecordMultiwayRepresentationCandidate(multiway);
        }

        if (block.Terminator is not MirSwitch
            {
                Discriminant.TypeId.Value: BaseTypes.BoolId,
                Branches.Count: 1,
                DefaultTarget: { } falseBlockId
            } decision ||
            decision.Branches[0].Value.Value is not MirConstantValue.BoolValue { Value: true })
        {
            return block;
        }

        _conditionalBranchCandidates++;
        _diamondsSeen++;
        var trueBlockId = decision.Branches[0].Target;
        if (trueBlockId == falseBlockId ||
            !blocks.TryGetValue(trueBlockId, out var trueBlock) ||
            !blocks.TryGetValue(falseBlockId, out var falseBlock) ||
            graph.GetPredecessors(trueBlockId).Count != 1 ||
            graph.GetPredecessors(falseBlockId).Count != 1 ||
            !graph.GetPredecessors(trueBlockId).Contains(block.Id) ||
            !graph.GetPredecessors(falseBlockId).Contains(block.Id) ||
            trueBlock.Terminator is not MirGoto trueGoto ||
            falseBlock.Terminator is not MirGoto falseGoto ||
            trueGoto.Target != falseGoto.Target)
        {
            _preservedShape++;
            return block;
        }

        if (trueBlock.Instructions.Count != 1 || falseBlock.Instructions.Count != 1 ||
            trueBlock.Instructions[0] is not MirAssign trueAssign ||
            falseBlock.Instructions[0] is not MirAssign falseAssign ||
            trueAssign.Target != falseAssign.Target ||
            trueAssign.Target.Kind != PlaceKind.Local ||
            !IsPureScalarOperand(trueAssign.Source) ||
            !IsPureScalarOperand(falseAssign.Source))
        {
            _preservedEffect++;
            return block;
        }

        if (!IsScalarCopyType(trueAssign.Target.TypeId) ||
            trueAssign.Source.TypeId != trueAssign.Target.TypeId ||
            falseAssign.Source.TypeId != trueAssign.Target.TypeId)
        {
            _preservedOwnership++;
            return block;
        }

        _selected++;
        return new MirBasicBlock
        {
            Id = block.Id,
            IsEntry = block.IsEntry,
            Span = block.Span,
            Instructions =
            [
                .. block.Instructions,
                new MirSelect
                {
                    Target = trueAssign.Target,
                    Condition = decision.Discriminant,
                    TrueValue = trueAssign.Source,
                    FalseValue = falseAssign.Source,
                    Span = decision.Span
                }
            ],
            Terminator = new MirGoto
            {
                Target = trueGoto.Target,
                Span = decision.Span
            }
        };
    }

    private void RecordMultiwayRepresentationCandidate(MirSwitch decision)
    {
        if (decision.Branches.Count < 3)
        {
            return;
        }

        var integerValues = decision.Branches
            .Select(static branch => branch.Value.Value)
            .OfType<MirConstantValue.IntValue>()
            .Select(static value => value.Value)
            .Order()
            .ToArray();
        if (integerValues.Length != decision.Branches.Count)
        {
            _binaryTreeCandidates++;
            return;
        }

        var span = integerValues[^1] - integerValues[0] + 1;
        if (span > 0 && span <= decision.Branches.Count * 2)
        {
            _jumpTableCandidates++;
        }
        else
        {
            _binaryTreeCandidates++;
        }
    }

    private static bool IsPureScalarOperand(MirOperand operand) => operand is MirConstant or
        MirPlace { Kind: PlaceKind.Local };

    private static bool IsScalarCopyType(TypeId typeId) => typeId.Value is
        BaseTypes.IntId or
        BaseTypes.FloatId or
        BaseTypes.BoolId or
        BaseTypes.CharId or
        BaseTypes.UnitId;
}
