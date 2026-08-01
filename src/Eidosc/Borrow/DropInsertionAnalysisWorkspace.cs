using Eidosc.Mir;

namespace Eidosc.Borrow;

/// <summary>
/// Reusable analysis storage shared by the ownership-finalization rounds.
/// Immutable function shape data is retained across rounds while instruction-
/// dependent sets are cleared and recomputed for the current MIR body.
/// </summary>
public sealed class DropInsertionAnalysisWorkspace
{
    private readonly Dictionary<string, FunctionStorage> _storageByFunction = new(StringComparer.Ordinal);
    private IReadOnlySet<int>? _scalarTagTypeIds;

    internal IReadOnlySet<int> GetScalarTagTypeIds(MirModule module)
    {
        return _scalarTagTypeIds ??= PayloadlessAdtRepresentationAnalysis.Analyze(module);
    }

    internal FunctionStorage Prepare(MirFunc function)
    {
        var key = MirFunctionIdentity.GetStableKey(function);
        var shape = FunctionShape.Create(function);
        if (!_storageByFunction.TryGetValue(key, out var storage) || storage.Shape != shape)
        {
            storage = new FunctionStorage(function, shape);
            _storageByFunction[key] = storage;
        }

        storage.ResetDynamicState(function);
        return storage;
    }

    internal sealed class FunctionStorage
    {
        private readonly Dictionary<BlockId, HashSet<LocalId>[]> _liveAfterByBlock = [];

        public FunctionStorage(MirFunc function, FunctionShape shape)
        {
            Shape = shape;
            ControlFlow = new ControlFlowGraph(function);
            LocalTypes = function.Locals.ToDictionary(static local => local.Id, static local => local.TypeId);
            OwnedIn = function.BasicBlocks.ToDictionary(static block => block.Id, static _ => new HashSet<LocalId>());
            OwnedOut = function.BasicBlocks.ToDictionary(static block => block.Id, static _ => new HashSet<LocalId>());
            ScratchIn = function.BasicBlocks.ToDictionary(static block => block.Id, static _ => new HashSet<LocalId>());
            ScratchOut = function.BasicBlocks.ToDictionary(static block => block.Id, static _ => new HashSet<LocalId>());
        }

        public FunctionShape Shape { get; }

        public ControlFlowGraph ControlFlow { get; }

        public IReadOnlyDictionary<LocalId, TypeId> LocalTypes { get; }

        public Dictionary<BlockId, HashSet<LocalId>> OwnedIn { get; }

        public Dictionary<BlockId, HashSet<LocalId>> OwnedOut { get; }

        public Dictionary<BlockId, HashSet<LocalId>> ScratchIn { get; }

        public Dictionary<BlockId, HashSet<LocalId>> ScratchOut { get; }

        public void ResetDynamicState(MirFunc function)
        {
            foreach (var state in OwnedIn.Values)
            {
                state.Clear();
            }

            foreach (var state in OwnedOut.Values)
            {
                state.Clear();
            }

            foreach (var state in ScratchIn.Values)
            {
                state.Clear();
            }

            foreach (var state in ScratchOut.Values)
            {
                state.Clear();
            }

            foreach (var block in function.BasicBlocks)
            {
                if (!_liveAfterByBlock.TryGetValue(block.Id, out var sets) ||
                    sets.Length != block.Instructions.Count)
                {
                    sets = Enumerable.Range(0, block.Instructions.Count)
                        .Select(static _ => new HashSet<LocalId>())
                        .ToArray();
                    _liveAfterByBlock[block.Id] = sets;
                    continue;
                }

                foreach (var set in sets)
                {
                    set.Clear();
                }
            }
        }

        public IReadOnlyList<HashSet<LocalId>> ComputeLiveAfter(
            MirBasicBlock block,
            LivenessAnalyzer livenessAnalyzer)
        {
            var result = _liveAfterByBlock[block.Id];
            var live = livenessAnalyzer.TryGetLiveOutSet(block.Id, out var liveOut)
                ? new HashSet<LocalId>(liveOut)
                : [];
            DropInsertionPass.AddTerminatorUsesForWorkspace(block.Terminator, live);

            for (var index = block.Instructions.Count - 1; index >= 0; index--)
            {
                result[index].UnionWith(live);
                DropInsertionPass.UpdateLivenessForWorkspace(block.Instructions[index], live);
            }

            return result;
        }
    }

    internal readonly record struct FunctionShape(
        int LocalCount,
        int BlockCount,
        int ShapeHash)
    {
        public static FunctionShape Create(MirFunc function)
        {
            var hash = new HashCode();
            hash.Add(function.EntryBlockId.Value);
            foreach (var local in function.Locals)
            {
                hash.Add(local.Id.Value);
                hash.Add(local.TypeId.Value);
                hash.Add(local.IsParameter);
            }

            foreach (var block in function.BasicBlocks)
            {
                hash.Add(block.Id.Value);
                switch (block.Terminator)
                {
                    case MirGoto jump:
                        hash.Add(1);
                        hash.Add(jump.Target.Value);
                        break;
                    case MirSwitch branch:
                        hash.Add(2);
                        foreach (var arm in branch.Branches)
                        {
                            hash.Add(arm.Target.Value);
                        }
                        hash.Add(branch.DefaultTarget?.Value ?? int.MinValue);
                        break;
                    case MirReturn:
                        hash.Add(3);
                        break;
                    case MirUnreachable:
                        hash.Add(4);
                        break;
                    default:
                        hash.Add(0);
                        break;
                }
            }

            return new FunctionShape(function.Locals.Count, function.BasicBlocks.Count, hash.ToHashCode());
        }
    }
}
