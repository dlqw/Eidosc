using Eidosc.Mir;
using System.Numerics;

namespace Eidosc.Borrow;

/// <summary>
/// 活性分析器 - 计算变量的活跃范围
/// </summary>
public sealed class LivenessAnalyzer
{
    private readonly MirFunc _function;
    private readonly VariableUsageAnalyzer _usageAnalyzer;
    private readonly ControlFlowGraph? _precomputedCfg;
    private readonly MirBasicBlock[] _blocks;
    private readonly MirLocal[] _locals;
    private readonly Dictionary<BlockId, int> _blockIndexById;
    private readonly Dictionary<LocalId, int> _localIndexById;
    private readonly int _bitWordCount;

    /// <summary>
    /// 每个基本块的 LiveIn 集合
    /// </summary>
    private readonly Dictionary<BlockId, HashSet<LocalId>> _liveIn = new();
    private bool _liveInMaterialized;

    public Dictionary<BlockId, HashSet<LocalId>> LiveIn
    {
        get
        {
            EnsureLiveInMaterialized();
            return _liveIn;
        }
    }

    /// <summary>
    /// 每个基本块的 LiveOut 集合
    /// </summary>
    private readonly Dictionary<BlockId, HashSet<LocalId>> _liveOut = new();
    private bool _liveOutMaterialized;

    public Dictionary<BlockId, HashSet<LocalId>> LiveOut
    {
        get
        {
            EnsureLiveOutMaterialized();
            return _liveOut;
        }
    }

    /// <summary>
    /// 每个基本块的 use 集合
    /// </summary>
    private readonly BitSet[] _blockUse;

    /// <summary>
    /// 每个基本块的 def 集合
    /// </summary>
    private readonly BitSet[] _blockDef;
    private readonly BitSet[] _liveInBits;
    private readonly BitSet[] _liveOutBits;
    private BitSet? _scratchLiveOut;
    private BitSet? _scratchLiveIn;

    /// <summary>
    /// 变量的活跃范围
    /// </summary>
    public Dictionary<LocalId, LiveRange> LiveRanges { get; } = new();

    public LivenessAnalyzer(MirFunc function, VariableUsageAnalyzer usageAnalyzer, ControlFlowGraph? cfg = null)
    {
        _function = function;
        _usageAnalyzer = usageAnalyzer;
        _precomputedCfg = cfg;
        _blocks = function.BasicBlocks.ToArray();
        _locals = function.Locals.ToArray();
        _blockIndexById = new Dictionary<BlockId, int>(_blocks.Length);
        for (int i = 0; i < _blocks.Length; i++)
        {
            _blockIndexById[_blocks[i].Id] = i;
        }

        _localIndexById = new Dictionary<LocalId, int>(_locals.Length);
        for (int i = 0; i < _locals.Length; i++)
        {
            _localIndexById[_locals[i].Id] = i;
        }

        _bitWordCount = (_locals.Length + 63) >> 6;
        _blockUse = CreateBitSetArray(_blocks.Length);
        _blockDef = CreateBitSetArray(_blocks.Length);
        _liveInBits = CreateBitSetArray(_blocks.Length);
        _liveOutBits = CreateBitSetArray(_blocks.Length);
    }

    /// <summary>
    /// 执行活性分析
    /// </summary>
    public void Analyze()
    {
        // 1. 计算每个基本块的 use/def 集合
        ComputeUseDef();

        // 2. 使用 worklist 算法计算 LiveIn/LiveOut
        ComputeLiveInLiveOut();

        // 3. 计算变量的活跃范围
        ComputeLiveRanges();
    }

    private void ComputeUseDef()
    {
        for (int blockIndex = 0; blockIndex < _blocks.Length; blockIndex++)
        {
            var use = CreateBitSet();
            var def = CreateBitSet();
            var block = _blocks[blockIndex];

            foreach (var instr in block.Instructions)
            {
                CollectUseDef(instr, use, def);
            }

            CollectTerminatorUseDef(block.Terminator, use, def);
            _blockUse[blockIndex] = use;
            _blockDef[blockIndex] = def;
        }
    }

    private void CollectUseDef(MirInstruction instr, BitSet use, BitSet def)
    {
        switch (instr)
        {
            case MirAssign assign:
                // 源是 use
                CollectOperandUse(assign.Source, use, def);
                // 目标是 def
                if (assign.Target?.Kind == PlaceKind.Local &&
                    TryGetLocalIndex(assign.Target.Local, out var assignTargetIndex))
                {
                    def.Add(assignTargetIndex);
                }
                break;

            case MirCaseInject injection:
                CollectOperandUse(injection.Operand, use, def);
                if (injection.Target is MirPlace { Kind: PlaceKind.Local, Local: var injectionLocal } &&
                    TryGetLocalIndex(injectionLocal, out var injectionTargetIndex))
                {
                    def.Add(injectionTargetIndex);
                }
                break;

            case MirCall call:
                // 函数和参数是 use
                CollectOperandUse(call.Function, use, def);
                foreach (var arg in call.Arguments)
                {
                    CollectOperandUse(arg, use, def);
                }
                // 目标是 def
                if (call.Target?.Kind == PlaceKind.Local &&
                    TryGetLocalIndex(call.Target.Local, out var callTargetIndex))
                {
                    def.Add(callTargetIndex);
                }
                break;

            case MirBinOp binOp:
                CollectOperandUse(binOp.Left, use, def);
                CollectOperandUse(binOp.Right, use, def);
                if (binOp.Target is MirPlace { Kind: PlaceKind.Local, Local: var binOpLocal } &&
                    TryGetLocalIndex(binOpLocal, out var binOpTargetIndex))
                {
                    def.Add(binOpTargetIndex);
                }
                break;

            case MirUnaryOp unaryOp:
                CollectOperandUse(unaryOp.Operand, use, def);
                if (unaryOp.Target is MirPlace { Kind: PlaceKind.Local, Local: var unaryOpLocal } &&
                    TryGetLocalIndex(unaryOpLocal, out var unaryTargetIndex))
                {
                    def.Add(unaryTargetIndex);
                }
                break;

            case MirLoad load:
                CollectOperandUse(load.Source, use, def);
                if (MirLocalTransferAnalysis.TryGetBinding(load, out var loadBinding) &&
                    TryGetLocalIndex(loadBinding.Target, out var loadTargetIndex))
                {
                    def.Add(loadTargetIndex);
                }
                else if (load.Target?.Kind == PlaceKind.Local &&
                         TryGetLocalIndex(load.Target.Local, out var loadLocalIndex))
                {
                    def.Add(loadLocalIndex);
                }
                break;

            case MirStore store:
                CollectOperandUse(store.Value, use, def);
                if (store.Target?.Kind == PlaceKind.Local &&
                    TryGetLocalIndex(store.Target.Local, out var storeTargetIndex))
                {
                    use.Add(storeTargetIndex);
                }
                break;

            case MirDrop drop:
                CollectOperandUse(drop.Value, use, def);
                break;

            case MirCopy copy:
                if (MirLocalTransferAnalysis.TryGetBinding(copy, out var copyBinding))
                {
                    if (TryGetLocalIndex(copyBinding.Source, out var copySourceIndex))
                    {
                        use.Add(copySourceIndex);
                    }

                    if (TryGetLocalIndex(copyBinding.Target, out var copyTargetIndex))
                    {
                        def.Add(copyTargetIndex);
                    }
                }
                else
                {
                    if (copy.Source?.Kind == PlaceKind.Local &&
                        TryGetLocalIndex(copy.Source.Local, out var copySourceLocalIndex))
                    {
                        use.Add(copySourceLocalIndex);
                    }
                    if (copy.Target?.Kind == PlaceKind.Local &&
                        TryGetLocalIndex(copy.Target.Local, out var copyTargetLocalIndex))
                    {
                        def.Add(copyTargetLocalIndex);
                    }
                }
                break;

            case MirMove move:
                if (MirLocalTransferAnalysis.TryGetBinding(move, out var moveBinding))
                {
                    if (TryGetLocalIndex(moveBinding.Source, out var moveSourceIndex))
                    {
                        use.Add(moveSourceIndex);
                    }

                    if (TryGetLocalIndex(moveBinding.Target, out var moveTargetIndex))
                    {
                        def.Add(moveTargetIndex);
                    }
                }
                else
                {
                    if (move.Source?.Kind == PlaceKind.Local &&
                        TryGetLocalIndex(move.Source.Local, out var moveSourceLocalIndex))
                    {
                        use.Add(moveSourceLocalIndex);
                    }
                    if (move.Target?.Kind == PlaceKind.Local &&
                        TryGetLocalIndex(move.Target.Local, out var moveTargetLocalIndex))
                    {
                        def.Add(moveTargetLocalIndex);
                    }
                }
                break;

            case MirAlloc alloc:
                if (alloc.Target?.Kind == PlaceKind.Local &&
                    TryGetLocalIndex(alloc.Target.Local, out var allocTargetIndex))
                {
                    def.Add(allocTargetIndex);
                }
                break;
        }
    }

    private void CollectOperandUse(MirOperand? operand, BitSet use, BitSet def)
    {
        if (operand is MirPlace place)
        {
            CollectPlaceUse(place, use, def);
        }
    }

    private void CollectTerminatorUseDef(MirTerminator? terminator, BitSet use, BitSet def)
    {
        switch (terminator)
        {
            case MirReturn { Value: { } retVal }:
                CollectOperandUse(retVal, use, def);
                break;
            case MirSwitch sw:
                CollectOperandUse(sw.Discriminant, use, def);
                foreach (var branch in sw.Branches)
                {
                    if (branch.BoundVariable is { } boundVariable &&
                        TryGetLocalIndex(boundVariable, out var boundIndex))
                    {
                        def.Add(boundIndex);
                    }
                }
                break;
        }
    }

    private void CollectPlaceUse(MirPlace place, BitSet use, BitSet def)
    {
        if (place.Kind == PlaceKind.Local &&
            TryGetLocalIndex(place.Local, out var localIndex) &&
            !def.Contains(localIndex))
        {
            // 如果变量未在此块中定义，则是 use
            use.Add(localIndex);
        }

        if (place.Base is MirPlace basePlace)
        {
            CollectPlaceUse(basePlace, use, def);
        }
        else if (place.Base != null)
        {
            CollectOperandUse(place.Base, use, def);
        }

        if (place.Index != null)
        {
            CollectOperandUse(place.Index, use, def);
        }
    }

    private void ComputeLiveInLiveOut()
    {
        _liveIn.Clear();
        _liveOut.Clear();
        _liveInMaterialized = false;
        _liveOutMaterialized = false;
        for (int blockIndex = 0; blockIndex < _blocks.Length; blockIndex++)
        {
            _liveOutBits[blockIndex] = CreateBitSet();
            _liveInBits[blockIndex] = CreateBitSet();
        }

        var cfg = _precomputedCfg ?? new ControlFlowGraph(_function);
        var successorIndices = BuildEdgeIndexArray(cfg.GetSuccessors);
        var predecessorIndices = BuildEdgeIndexArray(cfg.GetPredecessors);

        var worklist = new Queue<int>(_blocks.Length);
        var queuedBlocks = new bool[_blocks.Length];
        for (int blockIndex = 0; blockIndex < _blocks.Length; blockIndex++)
        {
            worklist.Enqueue(blockIndex);
            queuedBlocks[blockIndex] = true;
        }

        // C4: Reuse scratch BitSet across worklist iterations via swap pattern
        _scratchLiveOut ??= CreateBitSet();
        _scratchLiveIn ??= CreateBitSet();

        while (worklist.Count > 0)
        {
            var blockIndex = worklist.Dequeue();
            queuedBlocks[blockIndex] = false;

            _scratchLiveOut.Clear();
            foreach (var succIndex in successorIndices[blockIndex])
            {
                _scratchLiveOut.UnionWith(_liveInBits[succIndex]);
            }

            _scratchLiveIn.Clear();
            var def = _blockDef[blockIndex];
            if (!def.IsEmpty)
            {
                _scratchLiveIn.UnionWith(_scratchLiveOut);
                _scratchLiveIn.ExceptWith(def);
            }
            else
            {
                _scratchLiveIn.UnionWith(_scratchLiveOut);
            }

            var use = _blockUse[blockIndex];
            if (!use.IsEmpty)
            {
                _scratchLiveIn.UnionWith(use);
            }

            if (!_liveOutBits[blockIndex].SetEquals(_scratchLiveOut) || !_liveInBits[blockIndex].SetEquals(_scratchLiveIn))
            {
                // Swap: move scratch values into the array, old array values become scratch
                (_liveOutBits[blockIndex], _scratchLiveOut) = (_scratchLiveOut, _liveOutBits[blockIndex]);
                (_liveInBits[blockIndex], _scratchLiveIn) = (_scratchLiveIn, _liveInBits[blockIndex]);

                foreach (var predIndex in predecessorIndices[blockIndex])
                {
                    if (!queuedBlocks[predIndex])
                    {
                        queuedBlocks[predIndex] = true;
                        worklist.Enqueue(predIndex);
                    }
                }
            }
        }

    }

    private void ComputeLiveRanges()
    {
        LiveRanges.Clear();

        var liveBlocksByLocalIndex = new List<int>?[_locals.Length];
        for (int blockIndex = 0; blockIndex < _blocks.Length; blockIndex++)
        {
            var blockValue = _blocks[blockIndex].Id.Value;
            AddLiveBlocks(_liveInBits[blockIndex], blockValue, liveBlocksByLocalIndex);
            AddLiveBlocks(_liveOutBits[blockIndex], blockValue, liveBlocksByLocalIndex, _liveInBits[blockIndex]);
        }

        foreach (var local in _locals)
        {
            var firstDef = _usageAnalyzer.GetFirstDef(local.Id);
            var lastUse = _usageAnalyzer.GetLastUse(local.Id);
            List<(BlockId Block, int Index)> lastUses = lastUse != null ? [lastUse.Value] : [];
            var liveBlocks = liveBlocksByLocalIndex[_localIndexById[local.Id]];

            LiveRanges[local.Id] = new LiveRange
            {
                Variable = local.Id,
                Definition = firstDef ?? default,
                LastUses = lastUses,
                LiveBlocks = liveBlocks is not { Count: > 0 }
                    ? CompactBlockIdSet.Empty
                    : new CompactBlockIdSet(liveBlocks)
            };
        }
    }

    private bool TryGetLocalIndex(LocalId localId, out int index)
    {
        return _localIndexById.TryGetValue(localId, out index);
    }

    private BitSet CreateBitSet()
    {
        return new BitSet(_bitWordCount);
    }

    private BitSet[] CreateBitSetArray(int count)
    {
        var bitSets = new BitSet[count];
        for (int i = 0; i < count; i++)
        {
            bitSets[i] = CreateBitSet();
        }

        return bitSets;
    }

    private int[][] BuildEdgeIndexArray(Func<BlockId, IReadOnlySet<BlockId>> edgeProvider)
    {
        var edges = new int[_blocks.Length][];
        for (int blockIndex = 0; blockIndex < _blocks.Length; blockIndex++)
        {
            var blockId = _blocks[blockIndex].Id;
            var mapped = new List<int>();
            foreach (var targetBlockId in edgeProvider(blockId))
            {
                if (_blockIndexById.TryGetValue(targetBlockId, out var targetIndex))
                {
                    mapped.Add(targetIndex);
                }
            }

            edges[blockIndex] = [.. mapped];
        }

        return edges;
    }

    private static void AddLiveBlocks(BitSet source, int blockValue, List<int>?[] liveBlocksByLocalIndex, BitSet? exclude = null)
    {
        var words = source.Words;
        for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
        {
            var word = words[wordIndex];
            if (exclude != null)
            {
                word &= ~exclude.Words[wordIndex];
            }

            while (word != 0)
            {
                var bitIndex = BitOperations.TrailingZeroCount(word);
                var localIndex = (wordIndex << 6) + bitIndex;
                var liveBlocks = liveBlocksByLocalIndex[localIndex] ??= [];
                liveBlocks.Add(blockValue);
                word &= word - 1;
            }
        }
    }

    internal bool TryGetLiveOutSet(BlockId blockId, out HashSet<LocalId> liveOut)
    {
        if (_blockIndexById.TryGetValue(blockId, out var blockIndex))
        {
            liveOut = ToLocalIdSet(_liveOutBits[blockIndex]);
            return true;
        }

        liveOut = [];
        return false;
    }

    private void EnsureLiveInMaterialized()
    {
        if (_liveInMaterialized)
        {
            return;
        }

        _liveIn.Clear();
        for (int blockIndex = 0; blockIndex < _blocks.Length; blockIndex++)
        {
            _liveIn[_blocks[blockIndex].Id] = ToLocalIdSet(_liveInBits[blockIndex]);
        }

        _liveInMaterialized = true;
    }

    private void EnsureLiveOutMaterialized()
    {
        if (_liveOutMaterialized)
        {
            return;
        }

        _liveOut.Clear();
        for (int blockIndex = 0; blockIndex < _blocks.Length; blockIndex++)
        {
            _liveOut[_blocks[blockIndex].Id] = ToLocalIdSet(_liveOutBits[blockIndex]);
        }

        _liveOutMaterialized = true;
    }

    private HashSet<LocalId> ToLocalIdSet(BitSet bitSet)
    {
        var result = new HashSet<LocalId>(bitSet.Count);
        var words = bitSet.Words;
        for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
        {
            var word = words[wordIndex];
            while (word != 0)
            {
                var bitIndex = BitOperations.TrailingZeroCount(word);
                var localIndex = (wordIndex << 6) + bitIndex;
                result.Add(_locals[localIndex].Id);
                word &= word - 1;
            }
        }

        return result;
    }
}

internal sealed class BitSet
{
    private readonly ulong[] _words;

    public BitSet(int wordCount)
    {
        _words = new ulong[wordCount];
    }

    public void Clear()
    {
        Array.Clear(_words);
    }

    public bool IsEmpty
    {
        get
        {
            for (int i = 0; i < _words.Length; i++)
            {
                if (_words[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public int Count
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _words.Length; i++)
            {
                total += (int)ulong.PopCount(_words[i]);
            }

            return total;
        }
    }

    public ReadOnlySpan<ulong> Words => _words;

    public void Add(int index)
    {
        var wordIndex = index >> 6;
        var bitIndex = index & 63;
        _words[wordIndex] |= 1UL << bitIndex;
    }

    public bool Contains(int index)
    {
        var wordIndex = index >> 6;
        var bitIndex = index & 63;
        return (_words[wordIndex] & (1UL << bitIndex)) != 0;
    }

    public void UnionWith(BitSet other)
    {
        for (int i = 0; i < _words.Length; i++)
        {
            _words[i] |= other._words[i];
        }
    }

    public void ExceptWith(BitSet other)
    {
        for (int i = 0; i < _words.Length; i++)
        {
            _words[i] &= ~other._words[i];
        }
    }

    public bool SetEquals(BitSet other)
    {
        if (_words.Length != other._words.Length)
        {
            return false;
        }

        for (int i = 0; i < _words.Length; i++)
        {
            if (_words[i] != other._words[i])
            {
                return false;
            }
        }

        return true;
    }
}
