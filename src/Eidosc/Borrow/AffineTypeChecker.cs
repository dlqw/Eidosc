using Eidosc.ErrorRecovery;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Utils;

namespace Eidosc.Borrow;

/// <summary>
/// 变量状态
/// </summary>
public enum VariableState
{
    /// <summary>
    /// 未初始化
    /// </summary>
    Uninitialized,

    /// <summary>
    /// 已初始化
    /// </summary>
    Initialized,

    /// <summary>
    /// 已移动
    /// </summary>
    Moved
}

/// <summary>
/// 仿射类型检查器 - 检查仿射类型的使用规则
/// 实现借用检查错误恢复
/// </summary>
public sealed class AffineTypeChecker
{
    private readonly MirFunc _function;
    private readonly bool _capturePointStates;
    private readonly ControlFlowGraph? _precomputedCfg;
    private readonly MirBasicBlock[] _blocks;
    private readonly MirLocal[] _locals;
    private readonly IReadOnlyDictionary<int, string>? _dynamicTypeKeys;
    private readonly Dictionary<BlockId, int> _blockIndexById;
    private readonly Dictionary<LocalId, int> _localIndexById;
    private HashSet<EdgeLocalStateSuppression> _oneShotLoopMoveBackedgeSuppressions = [];

    /// <summary>
    /// 错误恢复上下文
    /// </summary>
    private readonly ErrorRecoveryContext _recoveryContext = ErrorRecoveryContext.ForBorrowCheck();

    /// <summary>
    /// 每个程序点的变量状态
    /// </summary>
    private readonly Dictionary<(BlockId Block, int Index), Dictionary<LocalId, VariableState>> _variableStatesAtPoint = new();
    private static readonly Dictionary<LocalId, VariableState> EmptyVariableStates = [];

    /// <summary>
    /// 当前变量状态
    /// </summary>
    private VariableState[] _variableStates;

    /// <summary>
    /// 变量移动位置记录
    /// </summary>
    private readonly (BlockId Block, int Index)[] _moveLocations;

    /// <summary>
    /// 变量移动源码位置记录
    /// </summary>
    private readonly SourceSpan[] _moveSpans;
    private readonly bool[] _hasMoveInfo;

    /// <summary>
    /// 诊断信息
    /// </summary>
    public List<AffineDiagnostic> Diagnostics { get; } = [];

    public AffineTypeChecker(MirFunc function, VariableUsageAnalyzer usageAnalyzer, bool capturePointStates = true, IReadOnlyDictionary<int, string>? dynamicTypeKeys = null, ControlFlowGraph? cfg = null)
    {
        _function = function;
        _capturePointStates = capturePointStates;
        _precomputedCfg = cfg;
        _dynamicTypeKeys = dynamicTypeKeys;
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

        _variableStates = new VariableState[_locals.Length];
        _moveLocations = new (BlockId Block, int Index)[_locals.Length];
        _moveSpans = new SourceSpan[_locals.Length];
        _hasMoveInfo = new bool[_locals.Length];
    }

    /// <summary>
    /// 执行仿射类型检查
    /// 移动后使用时记录错误继续
    /// </summary>
    public void Check()
    {
        // 重置错误恢复上下文
        _recoveryContext.Reset();
        Diagnostics.Clear();
        _variableStatesAtPoint.Clear();
        Array.Clear(_variableStates);
        Array.Clear(_moveLocations);
        Array.Clear(_moveSpans);
        Array.Clear(_hasMoveInfo);

        var initialStates = CreateInitialVariableStates();
        var cfg = _precomputedCfg ?? new ControlFlowGraph(_function);
        var predecessorIndices = BuildEdgeIndexArray(cfg.GetPredecessors);
        var successorIndices = BuildEdgeIndexArray(cfg.GetSuccessors);
        _oneShotLoopMoveBackedgeSuppressions = BuildOneShotLoopMoveBackedgeSuppressions(cfg, successorIndices);
        var blockOutStates = ComputeStableBlockOutStates(initialStates, predecessorIndices, successorIndices);

        for (int blockIndex = 0; blockIndex < _blocks.Length; blockIndex++)
        {
            var block = _blocks[blockIndex];

            // 检查是否达到错误限制
            if (_recoveryContext.HasReachedLimit)
            {
                AddDiagnostic(new AffineDiagnostic
                {
                    Kind = AffineErrorKind.AffineReuse,
                    Variable = LocalId.None,
                    Span = block.Span,
                    Message = DiagnosticMessages.TooManyAffineTypeErrors(_recoveryContext.MaxErrors)
                });
                break;
            }

            if (!TrySetIncomingStates(blockIndex, initialStates, predecessorIndices, blockOutStates))
            {
                continue;
            }

            for (int i = 0; i < block.Instructions.Count; i++)
            {
                // 检查是否达到错误限制
                if (_recoveryContext.HasReachedLimit)
                {
                    break;
                }

                var instr = block.Instructions[i];
                CheckInstruction(instr, block.Id, i);
            }

            // 检查终止符
            if (block.Terminator != null)
            {
                CheckTerminator(block.Terminator, block.Id, block.Instructions.Count);
            }
        }
    }

    /// <summary>
    /// 获取指定点的变量状态
    /// </summary>
    public IReadOnlyDictionary<LocalId, VariableState> GetVariableStatesAtPoint(BlockId block, int index)
    {
        var key = (block, index);
        return _variableStatesAtPoint.TryGetValue(key, out var states)
            ? states
            : EmptyVariableStates;
    }

    private VariableState[] CreateInitialVariableStates()
    {
        var states = new VariableState[_locals.Length];
        for (int i = 0; i < _locals.Length; i++)
        {
            states[i] = _locals[i].IsParameter
                ? VariableState.Initialized
                : VariableState.Uninitialized;
        }

        return states;
    }

    private bool TrySetIncomingStates(
        int blockIndex,
        VariableState[] initialStates,
        int[][] predecessorIndices,
        VariableState[][] blockOutStates)
    {
        if (_blocks[blockIndex].Id.Equals(_function.EntryBlockId))
        {
            Array.Copy(initialStates, _variableStates, _locals.Length);
            return true;
        }

        var predecessors = predecessorIndices[blockIndex];
        if (predecessors.Length == 0)
        {
            return false;
        }

        VariableState[]? firstReachableState = null;
        VariableState[]? secondReachableState = null;
        VariableState[][]? additionalStates = null;
        int[]? additionalStatePredecessorIndices = null;
        var firstReachableIndex = -1;
        var secondReachableIndex = -1;
        var reachableCount = 0;

        foreach (var predecessorIndex in predecessors)
        {
            var predecessorState = blockOutStates[predecessorIndex];
            if (predecessorState == null)
            {
                continue;
            }

            reachableCount++;
            if (reachableCount == 1)
            {
                firstReachableState = predecessorState;
                firstReachableIndex = predecessorIndex;
                continue;
            }

            if (reachableCount == 2)
            {
                secondReachableState = predecessorState;
                secondReachableIndex = predecessorIndex;
                continue;
            }

            additionalStates ??= new VariableState[predecessors.Length - 2][];
            additionalStatePredecessorIndices ??= new int[predecessors.Length - 2];
            additionalStates[reachableCount - 3] = predecessorState;
            additionalStatePredecessorIndices[reachableCount - 3] = predecessorIndex;
        }

        if (reachableCount == 0)
        {
            return false;
        }

        if (reachableCount == 1)
        {
            CopyIncomingState(blockIndex, firstReachableIndex, firstReachableState!, _variableStates);
            return true;
        }

        for (int localIndex = 0; localIndex < _locals.Length; localIndex++)
        {
            var mergedState = MergeState(
                GetIncomingState(blockIndex, firstReachableIndex, localIndex, firstReachableState![localIndex]),
                GetIncomingState(blockIndex, secondReachableIndex, localIndex, secondReachableState![localIndex]));
            if (mergedState == VariableState.Moved)
            {
                _variableStates[localIndex] = VariableState.Moved;
                continue;
            }

            if (additionalStates != null)
            {
                for (int i = 0; i < reachableCount - 2; i++)
                {
                    mergedState = MergeState(
                        mergedState,
                        GetIncomingState(
                            blockIndex,
                            additionalStatePredecessorIndices![i],
                            localIndex,
                            additionalStates[i][localIndex]));
                    if (mergedState == VariableState.Moved)
                    {
                        break;
                    }
                }
            }

            _variableStates[localIndex] = mergedState;
        }

        return true;
    }

    private void CopyIncomingState(
        int blockIndex,
        int predecessorIndex,
        VariableState[] source,
        VariableState[] destination)
    {
        for (int localIndex = 0; localIndex < source.Length; localIndex++)
        {
            destination[localIndex] = GetIncomingState(blockIndex, predecessorIndex, localIndex, source[localIndex]);
        }
    }

    private VariableState GetIncomingState(
        int blockIndex,
        int predecessorIndex,
        int localIndex,
        VariableState state)
    {
        if (state != VariableState.Moved)
        {
            return state;
        }

        return _oneShotLoopMoveBackedgeSuppressions.Contains(new EdgeLocalStateSuppression(predecessorIndex, blockIndex, localIndex))
            ? VariableState.Initialized
            : state;
    }

    private static VariableState MergeState(VariableState left, VariableState right)
    {
        if (left == VariableState.Moved || right == VariableState.Moved)
        {
            return VariableState.Moved;
        }

        if (left == VariableState.Initialized && right == VariableState.Initialized)
        {
            return VariableState.Initialized;
        }

        return VariableState.Uninitialized;
    }

    private HashSet<EdgeLocalStateSuppression> BuildOneShotLoopMoveBackedgeSuppressions(
        ControlFlowGraph cfg,
        int[][] successorIndices)
    {
        var suppressions = new HashSet<EdgeLocalStateSuppression>();
        var byEdge = OneShotLoopMoveAnalysis.CollectBackedgeSuppressions(_function, cfg);
        for (var predecessorIndex = 0; predecessorIndex < successorIndices.Length; predecessorIndex++)
        {
            var predecessor = _blocks[predecessorIndex].Id;
            foreach (var successorIndex in successorIndices[predecessorIndex])
            {
                var successor = _blocks[successorIndex].Id;
                if (!byEdge.TryGetValue((predecessor, successor), out var locals))
                {
                    continue;
                }

                foreach (var local in locals)
                {
                    if (_localIndexById.TryGetValue(local, out var localIndex))
                    {
                        suppressions.Add(new EdgeLocalStateSuppression(predecessorIndex, successorIndex, localIndex));
                    }
                }
            }
        }

        return suppressions;
    }

    private void CheckInstruction(MirInstruction instr, BlockId blockId, int index)
    {
        // 保存当前点的状态
        SaveVariableStates(blockId, index);

        switch (instr)
        {
            case MirAssign assign:
                CheckAssign(assign, blockId, index);
                break;

            case MirCall call:
                CheckCall(call, blockId, index);
                break;

            case MirBinOp binOp:
                CheckBinOp(binOp, blockId, index);
                break;

            case MirUnaryOp unaryOp:
                CheckUnaryOp(unaryOp, blockId, index);
                break;

            case MirLoad load:
                CheckLoad(load, blockId, index);
                break;

            case MirStore store:
                CheckStore(store, blockId, index);
                break;

            case MirDrop drop:
                CheckDrop(drop, blockId, index);
                break;

            case MirCopy copy:
                CheckCopy(copy, blockId, index);
                break;

            case MirMove move:
                CheckMove(move, blockId, index);
                break;

            case MirAlloc alloc:
                CheckAlloc(alloc, blockId, index);
                break;
        }
    }

    private void CheckTerminator(MirTerminator terminator, BlockId blockId, int index)
    {
        // 保存当前点的状态
        SaveVariableStates(blockId, index);

        switch (terminator)
        {
            case MirReturn ret:
                if (ret.Value != null)
                {
                    CheckOperandUse(ret.Value, blockId, index);
                }
                break;

            case MirSwitch sw:
                CheckOperandUse(sw.Discriminant, blockId, index);
                break;

            // MirGoto 和 MirUnreachable 没有操作数需要检查
        }
    }

    private void SaveVariableStates(BlockId blockId, int index)
    {
        if (!_capturePointStates)
        {
            return;
        }

        var key = (blockId, index);
        var snapshot = new Dictionary<LocalId, VariableState>(_locals.Length);
        for (int i = 0; i < _locals.Length; i++)
        {
            snapshot[_locals[i].Id] = _variableStates[i];
        }

        _variableStatesAtPoint[key] = snapshot;
    }

    private VariableState[][] ComputeStableBlockOutStates(
        VariableState[] initialStates,
        int[][] predecessorIndices,
        int[][] successorIndices)
    {
        var blockOutStates = new VariableState[_blocks.Length][];
        var pendingBlocks = new Queue<int>(_blocks.Length);
        var queuedBlocks = new bool[_blocks.Length];
        for (int blockIndex = 0; blockIndex < _blocks.Length; blockIndex++)
        {
            pendingBlocks.Enqueue(blockIndex);
            queuedBlocks[blockIndex] = true;
        }

        while (pendingBlocks.Count > 0)
        {
            var blockIndex = pendingBlocks.Dequeue();
            queuedBlocks[blockIndex] = false;
            var block = _blocks[blockIndex];

            if (!TrySetIncomingStates(blockIndex, initialStates, predecessorIndices, blockOutStates))
            {
                continue;
            }
            foreach (var instr in block.Instructions)
            {
                SimulateInstruction(instr);
            }

            var existingState = blockOutStates[blockIndex];
            if (existingState == null || !StatesEqual(existingState, _variableStates))
            {
                blockOutStates[blockIndex] = CloneStates(_variableStates);
                foreach (var successorIndex in successorIndices[blockIndex])
                {
                    if (!queuedBlocks[successorIndex])
                    {
                        queuedBlocks[successorIndex] = true;
                        pendingBlocks.Enqueue(successorIndex);
                    }
                }
            }
        }

        return blockOutStates;
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

    private void SimulateInstruction(MirInstruction instr)
    {
        switch (instr)
        {
            case MirAssign assign:
                MarkInitialized(assign.Target);
                break;

            case MirCall call:
                MarkInitialized(call.Target);
                break;

            case MirBinOp binOp:
                MarkInitialized(binOp.Target);
                break;

            case MirUnaryOp unaryOp:
                MarkInitialized(unaryOp.Target);
                break;

            case MirLoad load:
                if (MirLocalTransferAnalysis.TryGetBinding(load, out var loadBinding))
                {
                    SetState(loadBinding.Target, VariableState.Initialized);
                }
                else
                {
                    MarkInitialized(load.Target);
                }

                break;

            case MirCopy copy:
                if (MirLocalTransferAnalysis.TryGetBinding(copy, out var copyBinding))
                {
                    SetState(copyBinding.Target, VariableState.Initialized);
                }
                else
                {
                    MarkInitialized(copy.Target);
                }

                break;

            case MirMove move:
                if (MirLocalTransferAnalysis.TryGetBinding(move, out var moveBinding))
                {
                    SetState(moveBinding.Source, VariableState.Moved);
                    SetState(moveBinding.Target, VariableState.Initialized);
                }
                else
                {
                    if (move.Source?.Kind == PlaceKind.Local)
                    {
                        SetState(move.Source.Local, VariableState.Moved);
                    }

                    MarkInitialized(move.Target);
                }

                break;

            case MirStore store:
                if (store.Target is { Kind: PlaceKind.Local, Local: var storeTarget })
                {
                    SetState(storeTarget, VariableState.Initialized);
                }

                break;

            case MirAlloc alloc:
                MarkInitialized(alloc.Target);
                break;
        }
    }

    private void CheckAssign(MirAssign assign, BlockId blockId, int index)
    {
        // 检查源操作数
        CheckOperandUse(assign.Source, blockId, index);

        // 标记目标为已初始化
        MarkInitialized(assign.Target);
    }

    private void CheckCall(MirCall call, BlockId blockId, int index)
    {
        // 检查函数操作数
        CheckOperandUse(call.Function, blockId, index);

        // 检查参数
        foreach (var arg in call.Arguments)
        {
            CheckOperandUse(arg, blockId, index);
        }

        // 标记目标为已初始化
        MarkInitialized(call.Target);
    }

    private void CheckBinOp(MirBinOp binOp, BlockId blockId, int index)
    {
        CheckOperandUse(binOp.Left, blockId, index);
        CheckOperandUse(binOp.Right, blockId, index);

        MarkInitialized(binOp.Target);
    }

    private void CheckUnaryOp(MirUnaryOp unaryOp, BlockId blockId, int index)
    {
        CheckOperandUse(unaryOp.Operand, blockId, index);

        MarkInitialized(unaryOp.Target);
    }

    private void CheckLoad(MirLoad load, BlockId blockId, int index)
    {
        if (MirLocalTransferAnalysis.TryGetBinding(load, out var binding))
        {
            CheckVariableUse(
                binding.Source,
                blockId,
                index,
                ResolveSpan((load.Source as MirPlace)?.Span ?? SourceSpan.Empty, load.Source.Span, load.Span));
            SetState(binding.Target, VariableState.Initialized);
            return;
        }

        CheckOperandUse(load.Source, blockId, index);
        MarkInitialized(load.Target);
    }

    private void CheckStore(MirStore store, BlockId blockId, int index)
    {
        CheckOperandUse(store.Value, blockId, index);

        if (store.Target is { Kind: PlaceKind.Local, Local: var targetLocal })
        {
            SetState(targetLocal, VariableState.Initialized);
            return;
        }

        if (store.Target is not null)
        {
            CheckOperandUse(store.Target, blockId, index);
        }
    }

    private void CheckDrop(MirDrop drop, BlockId blockId, int index)
    {
        CheckOperandUse(drop.Value, blockId, index);
    }

    private void CheckCopy(MirCopy copy, BlockId blockId, int index)
    {
        if (MirLocalTransferAnalysis.TryGetBinding(copy, out var binding))
        {
            CheckVariableUse(
                binding.Source,
                blockId,
                index,
                ResolveSpan(copy.Source.Span, copy.Span));
            SetState(binding.Target, VariableState.Initialized);
            return;
        }

        // Copy 不改变源变量状态
        if (copy.Source?.Kind == PlaceKind.Local)
        {
            CheckVariableUse(copy.Source.Local, blockId, index, ResolveSpan(copy.Source.Span, copy.Span));
        }

        MarkInitialized(copy.Target);
    }

    private void CheckMove(MirMove move, BlockId blockId, int index)
    {
        if (MirLocalTransferAnalysis.TryGetBinding(move, out var binding))
        {
            CheckMoveSource(binding.Source, ResolveSpan(move.Source.Span, move.Span), blockId, index);
            SetState(binding.Target, VariableState.Initialized);
            return;
        }

        // Move 将源标记为已移动
        if (move.Source?.Kind == PlaceKind.Local)
        {
            CheckMoveSource(move.Source.Local, ResolveSpan(move.Source.Span, move.Span), blockId, index);
        }

        // 标记目标为已初始化
        MarkInitialized(move.Target);
    }

    private void CheckMoveSource(LocalId localId, SourceSpan moveSpan, BlockId blockId, int index)
    {
        if (TryGetState(localId, out var state) && state == VariableState.Moved)
        {
            GetMoveInfo(localId, out var prevMoveLoc, out var prevMoveSpan);

            AddDiagnostic(new AffineDiagnostic
            {
                Kind = AffineErrorKind.DoubleMove,
                Variable = localId,
                FirstLocation = prevMoveLoc,
                SecondLocation = (blockId, index),
                Span = moveSpan,
                RelatedSpan = prevMoveSpan,
                Message = DiagnosticMessages.AffineVariableMovedTwice
            });
            return;
        }

        // 标记为已移动
        SetState(localId, VariableState.Moved);
        if (TryGetLocalIndex(localId, out var localIndex))
        {
            _moveLocations[localIndex] = (blockId, index);
            _moveSpans[localIndex] = moveSpan;
            _hasMoveInfo[localIndex] = true;
        }
    }

    private void CheckAlloc(MirAlloc alloc, BlockId blockId, int index)
    {
        if (alloc.Target != null)
        {
            MarkInitialized(alloc.Target);
        }
    }

    private void MarkInitialized(MirPlace? place)
    {
        if (place is { Kind: PlaceKind.Local, Local: var localId })
        {
            SetState(localId, VariableState.Initialized);
        }
    }

    private void MarkInitialized(MirOperand? operand)
    {
        if (operand is MirPlace place)
        {
            MarkInitialized(place);
        }
    }

    private void CheckOperandUse(MirOperand? operand, BlockId blockId, int index)
    {
        if (operand is MirPlace place && place.Kind == PlaceKind.Local)
        {
            CheckVariableUse(place.Local, blockId, index, ResolveSpan(place.Span, operand.Span));
        }
    }

    private void CheckVariableUse(LocalId localId, BlockId blockId, int index, SourceSpan useSpan)
    {
        if (!TryGetState(localId, out var state))
        {
            return;
        }

        if (state == VariableState.Moved)
        {
            // 移动后使用时记录错误继续
            // 获取移动位置
            GetMoveInfo(localId, out var moveLoc, out var moveSpan);

            AddDiagnostic(new AffineDiagnostic
            {
                Kind = AffineErrorKind.UseAfterMove,
                Variable = localId,
                FirstLocation = moveLoc,
                SecondLocation = (blockId, index),
                Span = useSpan,
                RelatedSpan = moveSpan,
                Message = DiagnosticMessages.AffineUseAfterMove
            });
        }
        else if (state == VariableState.Uninitialized)
        {
            AddDiagnostic(new AffineDiagnostic
            {
                Kind = AffineErrorKind.UseBeforeInit,
                Variable = localId,
                FirstLocation = (blockId, index),
                SecondLocation = default,
                Span = useSpan,
                Message = DiagnosticMessages.AffineUseBeforeInit
            });
        }
    }

    private static SourceSpan ResolveSpan(params SourceSpan[] spans)
    {
        foreach (var span in spans)
        {
            if (HasSpan(span))
            {
                return span;
            }
        }

        return SourceSpan.Empty;
    }

    private static bool HasSpan(SourceSpan span)
    {
        return span.Length > 0 ||
               span.Location.Position > 0 ||
               span.Location.Line > 0 ||
               span.Location.Column > 0;
    }

    /// <summary>
    /// 添加诊断信息并记录错误
    /// </summary>
    private void AddDiagnostic(AffineDiagnostic diagnostic)
    {
        Diagnostics.Add(diagnostic);
        _recoveryContext.RecordError();
    }

    private bool TryGetState(LocalId localId, out VariableState state)
    {
        if (_localIndexById.TryGetValue(localId, out var index))
        {
            state = _variableStates[index];
            return true;
        }

        state = default;
        return false;
    }

    private bool TryGetLocalIndex(LocalId localId, out int index)
    {
        return _localIndexById.TryGetValue(localId, out index);
    }

    private void SetState(LocalId localId, VariableState state)
    {
        if (_localIndexById.TryGetValue(localId, out var index))
        {
            _variableStates[index] = state;
        }
    }

    private void GetMoveInfo(
        LocalId localId,
        out (BlockId Block, int Index) moveLocation,
        out SourceSpan moveSpan)
    {
        if (TryGetLocalIndex(localId, out var localIndex) && _hasMoveInfo[localIndex])
        {
            moveLocation = _moveLocations[localIndex];
            moveSpan = _moveSpans[localIndex];
            return;
        }

        moveLocation = (BlockId.None, 0);
        moveSpan = SourceSpan.Empty;
    }

    private static VariableState[] CloneStates(VariableState[] states)
    {
        return (VariableState[])states.Clone();
    }

    private static bool StatesEqual(VariableState[] left, VariableState[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// 仿射类型错误类型
/// </summary>
public enum AffineErrorKind
{
    /// <summary>
    /// 移动后使用
    /// </summary>
    UseAfterMove,

    /// <summary>
    /// 重复移动
    /// </summary>
    DoubleMove,

    /// <summary>
    /// 初始化前使用
    /// </summary>
    UseBeforeInit,

    /// <summary>
    /// 仿射类型重复使用
    /// </summary>
    AffineReuse
}

/// <summary>
/// 仿射类型诊断信息
/// </summary>
public sealed class AffineDiagnostic
{
    /// <summary>
    /// 错误类型
    /// </summary>
    public AffineErrorKind Kind { get; init; }

    /// <summary>
    /// 相关变量
    /// </summary>
    public LocalId Variable { get; init; }

    /// <summary>
    /// 第一个位置（如移动位置）
    /// </summary>
    public (BlockId Block, int Index) FirstLocation { get; init; }

    /// <summary>
    /// 第二个位置（如使用位置）
    /// </summary>
    public (BlockId Block, int Index) SecondLocation { get; init; }

    /// <summary>
    /// 主诊断源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// 相关源码位置（如第一次 move）
    /// </summary>
    public SourceSpan? RelatedSpan { get; init; }

    /// <summary>
    /// 错误消息
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

internal readonly record struct EdgeLocalStateSuppression(
    int PredecessorIndex,
    int SuccessorIndex,
    int LocalIndex);
