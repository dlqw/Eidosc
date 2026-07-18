using Eidosc.Symbols;
using Eidosc.ErrorRecovery;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Borrow;

/// <summary>
/// 活跃借用条目
/// </summary>
public sealed class ActiveBorrow
{
    /// <summary>
    /// 借用 ID
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// 借用者变量
    /// </summary>
    public LocalId Borrower { get; init; }

    /// <summary>
    /// 被借用变量
    /// </summary>
    public LocalId Borrowee { get; init; }

    /// <summary>
    /// 被借用目标（支持字段/索引/解引用路径）
    /// </summary>
    public BorrowTarget BorrowTarget { get; init; }

    /// <summary>
    /// 是否可变借用
    /// </summary>
    public bool IsMutable { get; init; }

    /// <summary>
    /// 创建位置
    /// </summary>
    public (BlockId Block, int Index) Location { get; init; }

    /// <summary>
    /// 借用创建时的源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// 原始借用来源位置
    /// </summary>
    public (BlockId Block, int Index) OriginLocation { get; init; }

    /// <summary>
    /// 原始借用来源描述
    /// </summary>
    public string OriginSummary { get; init; } = "";

    /// <summary>
    /// alias 传播链
    /// </summary>
    public List<string> AliasTrace { get; init; } = [];

    /// <summary>
    /// alias trace 标识（用于诊断与调试关联）
    /// </summary>
    public string TraceId { get; init; } = "";

    /// <summary>
    /// 生命周期结束位置（null 表示尚未结束）
    /// </summary>
    public (BlockId Block, int Index)? EndLocation { get; set; }

    /// <summary>
    /// 生命周期结束时的源码位置
    /// </summary>
    public SourceSpan? EndSpan { get; set; }
}

/// <summary>
/// 借用检查器 - 检查借用规则
/// 实现借用检查错误恢复
/// </summary>
public sealed class BorrowChecker
{
    private readonly MirFunc _function;
    private readonly LivenessAnalyzer _livenessAnalyzer;
    private readonly ControlFlowGraph? _precomputedCfg;
    private readonly LoanSignatureCache? _loanSignatureCache;
    private readonly SymbolTable? _symbolTable;
    private readonly Func<TypeId, bool>? _hasCopyImplResolver;
    private readonly IReadOnlyDictionary<int, string>? _dynamicTypeKeys;
    private readonly BorrowCapabilitySnapshot? _capabilitySnapshot;
    private readonly Dictionary<LocalId, MirLocal> _localsById;
    private readonly bool _capturePointStates;

    /// <summary>
    /// 错误恢复上下文
    /// </summary>
    private readonly ErrorRecoveryContext _recoveryContext = ErrorRecoveryContext.ForBorrowCheck();

    /// <summary>
    /// 已报告的借用冲突（用于避免重复报告）
    /// </summary>
    private readonly HashSet<string> _reportedConflicts = [];
    private readonly HashSet<string> _reportedDiagnostics = [];
    private readonly Dictionary<BorrowAliasTrace.BorrowStateKey, int> _borrowIdsByKey = new();
    private readonly Dictionary<int, ActiveBorrow> _activeBorrowById = new();
    private int _nextBorrowId = 1;

    /// <summary>
    /// 活跃借用列表
    /// </summary>
    public List<ActiveBorrow> ActiveBorrows { get; } = [];

    /// <summary>
    /// 每个程序点的活跃借用
    /// </summary>
    private readonly Dictionary<(BlockId Block, int Index), List<ActiveBorrow>> _borrowsAtPoint = new();

    /// <summary>
    /// Last cloned snapshot — reused when borrow set is unchanged between points.
    /// </summary>
    private List<ActiveBorrow>? _lastPointSnapshot;

    /// <summary>
    /// 诊断信息
    /// </summary>
    public List<BorrowDiagnostic> Diagnostics { get; } = [];
    public BorrowCapabilitySnapshot? CapabilitySnapshot => _capabilitySnapshot;

    public BorrowChecker(
        MirFunc function,
        LivenessAnalyzer livenessAnalyzer,
        LoanSignatureCache? loanSignatureCache = null,
        SymbolTable? symbolTable = null,
        BorrowCapabilitySnapshot? capabilitySnapshot = null,
        bool capturePointStates = true,
        IReadOnlyDictionary<int, string>? dynamicTypeKeys = null,
        ControlFlowGraph? cfg = null)
    {
        _function = function;
        _livenessAnalyzer = livenessAnalyzer;
        _precomputedCfg = cfg;
        _loanSignatureCache = loanSignatureCache;
        _symbolTable = symbolTable;
        _hasCopyImplResolver = symbolTable != null
            ? CopyTypeSemantics.CreateSymbolTableCopyResolver(symbolTable)
            : null;
        _dynamicTypeKeys = dynamicTypeKeys;
        _capabilitySnapshot = capabilitySnapshot;
        _capturePointStates = capturePointStates;
        _localsById = function.Locals.ToDictionary(local => local.Id);
    }

    /// <summary>
    /// 执行借用检查
    /// 收集所有借用相关问题，不在第一个错误停止
    /// </summary>
    public void Check()
    {
        // 重置错误恢复上下文
        _recoveryContext.Reset();
        Diagnostics.Clear();
        ActiveBorrows.Clear();
        _borrowsAtPoint.Clear();
        _lastPointSnapshot = null;
        _reportedConflicts.Clear();
        _reportedDiagnostics.Clear();
        _borrowIdsByKey.Clear();
        _activeBorrowById.Clear();
        _nextBorrowId = 1;

        var cfg = _precomputedCfg ?? new ControlFlowGraph(_function);
        var blockById = _function.BasicBlocks.ToDictionary(block => block.Id);
        var blockOutStates = new Dictionary<BlockId, BorrowFlowState>();
        var pendingBlocks = new Queue<BlockId>(_function.BasicBlocks.Select(block => block.Id));
        var queuedBlocks = _function.BasicBlocks
            .Select(block => block.Id)
            .ToHashSet();

        while (pendingBlocks.Count > 0)
        {
            var blockId = pendingBlocks.Dequeue();
            queuedBlocks.Remove(blockId);
            var block = blockById[blockId];

            // 检查是否达到错误限制
            if (_recoveryContext.HasReachedLimit)
            {
                AddDiagnostic(new BorrowDiagnostic
                {
                    Kind = BorrowErrorKind.BorrowedWhileReturned,
                    Message = DiagnosticMessages.TooManyBorrowErrors(_recoveryContext.MaxErrors),
                    Location = (block.Id, 0),
                    Span = block.Span
                });
                break;
            }

            // 合并前驱块状态作为入口状态
            var currentState = GetIncomingState(block.Id, cfg, blockOutStates);
            currentState.ConsumeDirty(); // Clear construction dirty flag
            _lastPointSnapshot = null;   // Force fresh clone for new block

            for (int i = 0; i < block.Instructions.Count; i++)
            {
                // 借用冲突时记录错误继续分析
                var instr = block.Instructions[i];
                CheckInstruction(instr, block.Id, i, currentState);

                // 记录当前点的活跃借用
                if (_capturePointStates)
                {
                    CapturePointState(block.Id, i, currentState);
                }
            }

            // 检查终止符
            if (block.Terminator != null)
            {
                CheckTerminator(block.Terminator, block.Id, block.Instructions.Count, currentState);
            }

            // Capture state at terminator point (after all instructions + terminator)
            if (_capturePointStates)
            {
                CapturePointState(block.Id, block.Instructions.Count, currentState);
            }

            if (!blockOutStates.TryGetValue(block.Id, out var existingState) ||
                !existingState.SemanticallyEquals(currentState))
            {
                blockOutStates[block.Id] = currentState;

                foreach (var successor in cfg.GetSuccessors(block.Id))
                {
                    if (queuedBlocks.Add(successor))
                    {
                        pendingBlocks.Enqueue(successor);
                    }
                }
            }
        }

        // 检查借用生命周期（继续收集错误）
        CheckBorrowLifetimes();
    }

    /// <summary>
    /// 获取指定点的活跃借用
    /// </summary>
    public List<ActiveBorrow> GetBorrowsAtPoint(BlockId block, int index)
    {
        var key = (block, index);
        return _borrowsAtPoint.TryGetValue(key, out var borrows)
            ? borrows
            : [];
    }

    /// <summary>
    /// 枚举每个程序点的借用状态
    /// </summary>
    public IEnumerable<((BlockId Block, int Index) Point, IReadOnlyList<ActiveBorrow> Borrows)> EnumerateBorrowStates()
    {
        return _borrowsAtPoint
            .OrderBy(entry => entry.Key.Block.Value)
            .ThenBy(entry => entry.Key.Index)
            .Select(entry => (entry.Key, (IReadOnlyList<ActiveBorrow>)entry.Value));
    }

    private void CheckInstruction(
        MirInstruction instr,
        BlockId blockId,
        int index,
        BorrowFlowState currentState)
    {
        // 检查是否达到错误限制
        if (_recoveryContext.HasReachedLimit)
        {
            return;
        }

        switch (instr)
        {
            case MirLoad load:
                // 加载创建借用（共享/可变）。
                OverwriteBorrower(
                    load.Target.Local,
                    blockId,
                    index,
                    currentState,
                    ResolveSpan(load.Target.Span, load.Span));
                var createsBorrowAlias = load.IsMutableBorrow || load.CreatesBorrowAlias;
                if (MirLocalTransferAnalysis.TryGetBinding(load, out var loadBinding))
                {
                    var sourcePlace = (MirPlace)load.Source;
                    var sourceLocal = loadBinding.Source;
                    var targetLocal = loadBinding.Target;
                    var isMutableLoad = load.IsMutableBorrow;
                    var borrowSpan = ResolveSpan(sourcePlace.Span, load.Source.Span, load.Span);
                    var loadBindings = createsBorrowAlias
                        ? BorrowPropagationAnalysis.CollectLoadBindings(
                            sourceLocal,
                            localId => currentState.GetBorrowsByBorrower(localId),
                            borrow => borrow.BorrowTarget)
                        : BorrowPropagationAnalysis.CollectTransferBindings(
                            sourceLocal,
                            localId => currentState.GetBorrowsByBorrower(localId),
                            borrow => borrow.BorrowTarget,
                            borrow => borrow.IsMutable);
                    foreach (var binding in loadBindings)
                    {
                        var effectiveIsMutable = createsBorrowAlias
                            ? binding.SourceBorrow?.IsMutable == true || isMutableLoad
                            : binding.IsMutable || isMutableLoad;
                        if (!RequireLoadCapability(
                                binding.BorrowTarget,
                                effectiveIsMutable,
                                blockId,
                                index,
                                borrowSpan))
                        {
                            continue;
                        }

                        if (binding.SourceBorrow == null)
                        {
                            if (!createsBorrowAlias)
                            {
                                continue;
                            }

                            CheckAndAddBorrow(
                                binding.BorrowTarget,
                                binding.Borrowee,
                                targetLocal,
                                isMutableLoad,
                                blockId,
                                index,
                                currentState,
                                span: borrowSpan,
                                originSummary: BorrowDiagnosticFormatter.BuildLoadTrace(sourceLocal, targetLocal, blockId, index));
                            continue;
                        }

                        CheckAndAddBorrow(
                            binding.BorrowTarget,
                            binding.Borrowee,
                            targetLocal,
                            effectiveIsMutable,
                            blockId,
                            index,
                            currentState,
                            borrowSpan,
                            binding.SourceBorrow,
                            BorrowDiagnosticFormatter.BuildLoadTrace(sourceLocal, targetLocal, blockId, index));
                    }
                }
                else if (load.Target is { Kind: PlaceKind.Local, Local: var loadTargetLocal } &&
                         BorrowTarget.TryResolve(load.Source, out var borrowTarget))
                {
                    var loadSpan = ResolveSpan(load.Source.Span, load.Target.Span, load.Span);
                    var isMutableLoad = load.IsMutableBorrow;
                    if (!RequireLoadCapability(borrowTarget, isMutableLoad, blockId, index, loadSpan))
                    {
                        break;
                    }

                    if (!createsBorrowAlias)
                    {
                        break;
                    }

                    CheckAndAddBorrow(
                        borrowTarget,
                        borrowTarget.BaseLocal,
                        loadTargetLocal,
                        isMutableLoad,
                        blockId,
                        index,
                        currentState,
                        loadSpan,
                        originSummary: BorrowDiagnosticFormatter.BuildLoadTrace(borrowTarget.BaseLocal, loadTargetLocal, blockId, index));
                }
                break;

            case MirStore store:
                // 存储可能需要可变借用
                if (BorrowTarget.TryResolve(store.Target, out var storeTarget))
                {
                    var storeSpan = ResolveSpan(store.Target.Span, store.Span);
                    if (store.Target.Kind == PlaceKind.Local &&
                        currentState.IsBorrower(store.Target.Local))
                    {
                        EndBorrowsByBorrower(store.Target.Local, blockId, index, currentState, storeSpan);
                        break;
                    }

                    if (!RequireWriteCapability(storeTarget, blockId, index, storeSpan))
                    {
                        break;
                    }

                    // 检查是否有冲突的借用
                    CheckBorrowConflict(storeTarget, true, blockId, index, currentState, storeSpan);
                }
                break;

            case MirMove move:
                if (move.Source.Kind == PlaceKind.Local)
                {
                    var moveSourceSpan = ResolveSpan(move.Source.Span, move.Span);
                    var moveTargetSpan = ResolveSpan(move.Target.Span, move.Span);
                    if (!RequireMoveCapability(move.Source.Local, blockId, index, moveSourceSpan))
                    {
                        break;
                    }

                    var transferBindings = BorrowPropagationAnalysis.CollectTransferBindings(
                        move.Source.Local,
                        localId => currentState.GetBorrowsByBorrower(localId),
                        borrow => borrow.BorrowTarget,
                        borrow => borrow.IsMutable);
                    if (transferBindings.Count > 0 && move.Target.Kind == PlaceKind.Local)
                    {
                        if (!move.Target.Local.Equals(move.Source.Local))
                        {
                            OverwriteBorrower(move.Target.Local, blockId, index, currentState, moveTargetSpan);
                        }

                        EndBorrowsByBorrower(move.Source.Local, blockId, index, currentState, moveSourceSpan);

                        foreach (var binding in transferBindings)
                        {
                            CheckAndAddBorrow(
                                binding.BorrowTarget,
                                binding.Borrowee,
                                move.Target.Local,
                                binding.IsMutable,
                                blockId,
                                index,
                                currentState,
                                moveTargetSpan,
                                binding.SourceBorrow,
                                BorrowDiagnosticFormatter.BuildMoveTrace(move.Source.Local, move.Target.Local, blockId, index));
                        }
                    }
                    else
                    {
                        OverwriteBorrower(move.Target.Local, blockId, index, currentState, moveTargetSpan);
                        EndBorrowsByBorrowee(move.Source.Local, blockId, index, currentState, moveSourceSpan);
                    }
                }
                break;

            case MirDrop drop:
                // 丢弃使所有相关借用失效
                if (drop.Value is MirPlace dropPlace &&
                    BorrowTarget.TryResolve(dropPlace, out var dropTarget))
                {
                    var dropSpan = ResolveSpan(dropPlace.Span, drop.Value.Span, drop.Span);
                    if (dropPlace.Kind == PlaceKind.Local &&
                        currentState.IsBorrower(dropPlace.Local))
                    {
                        EndBorrowsByBorrower(dropPlace.Local, blockId, index, currentState, dropSpan);
                    }
                    else
                    {
                        EndBorrowsByBorrowTarget(dropTarget, blockId, index, currentState, dropSpan);
                    }
                }
                break;

            case MirCopy copy:
                if (MirLocalTransferAnalysis.TryGetBinding(copy, out var copyBinding))
                {
                    var copyTargetSpan = ResolveSpan(copy.Target.Span, copy.Span);
                    OverwriteBorrower(copyBinding.Target, blockId, index, currentState, copyTargetSpan);

                    var transferBindings = BorrowPropagationAnalysis.CollectTransferBindings(
                        copyBinding.Source,
                        localId => currentState.GetBorrowsByBorrower(localId),
                        borrow => borrow.BorrowTarget,
                        borrow => borrow.IsMutable);
                    if (transferBindings.Count > 0 &&
                        ShouldDetachBorrowAliasOnCopy(copyBinding.Target))
                    {
                        break;
                    }

                    foreach (var binding in transferBindings)
                    {
                        CheckAndAddBorrow(
                            binding.BorrowTarget,
                            binding.Borrowee,
                            copyBinding.Target,
                            binding.IsMutable,
                            blockId,
                            index,
                            currentState,
                            copyTargetSpan,
                            binding.SourceBorrow,
                            BorrowDiagnosticFormatter.BuildCopyTrace(copyBinding.Source, copyBinding.Target, blockId, index));
                    }
                }
                break;

            case MirAssign assign when assign.Target.Kind == PlaceKind.Local:
                OverwriteBorrower(
                    assign.Target.Local,
                    blockId,
                    index,
                    currentState,
                    ResolveSpan(assign.Target.Span, assign.Span));
                break;

            case MirCaseInject { Target: MirPlace { Kind: PlaceKind.Local } target } injection:
                OverwriteBorrower(
                    target.Local,
                    blockId,
                    index,
                    currentState,
                    ResolveSpan(target.Span, injection.Span));
                break;

            case MirBinOp binOp when binOp.Target is MirPlace { Kind: PlaceKind.Local, Local: var local }:
                OverwriteBorrower(local, blockId, index, currentState, ResolveSpan(binOp.Target.Span, binOp.Span));
                break;

            case MirUnaryOp unaryOp when unaryOp.Target is MirPlace { Kind: PlaceKind.Local, Local: var local }:
                OverwriteBorrower(local, blockId, index, currentState, ResolveSpan(unaryOp.Target.Span, unaryOp.Span));
                break;

            case MirAlloc alloc when alloc.Target.Kind == PlaceKind.Local:
                OverwriteBorrower(
                    alloc.Target.Local,
                    blockId,
                    index,
                    currentState,
                    ResolveSpan(alloc.Target.Span, alloc.Span));
                break;

            case MirCall call:
                ApplyCallEffects(call, blockId, index, currentState);
                break;

        }
    }

    private void CheckTerminator(
        MirTerminator terminator,
        BlockId blockId,
        int index,
        BorrowFlowState currentState)
    {
        switch (terminator)
        {
            case MirReturn ret:
                // 返回值不能有活跃借用
                if (ret.Value is MirPlace place &&
                    BorrowTarget.TryResolve(place, out var returnTarget))
                {
                    var borrowsOnReturn = currentState.GetBorrowsByBorrowTarget(returnTarget);
                    if (borrowsOnReturn.Count > 0)
                    {
                        var returnSpan = ResolveSpan(place.Span, ret.Value?.Span ?? SourceSpan.Empty, ret.Span, terminator.Span);
                        AddDiagnostic(new BorrowDiagnostic
                        {
                            Kind = BorrowErrorKind.BorrowedWhileReturned,
                            Message = DiagnosticMessages.BorrowReturnValueStillActive,
                            Location = (blockId, index),
                            RelatedLocation = borrowsOnReturn[0].Location,
                            Span = returnSpan,
                            RelatedSpan = borrowsOnReturn[0].Span,
                            RelatedAliasTrace = [.. borrowsOnReturn[0].AliasTrace],
                            RelatedAliasTraceId = borrowsOnReturn[0].TraceId,
                            Hint = BorrowAliasTrace.BuildConflictHint(
                                borrowsOnReturn[0].AliasTrace,
                                borrowsOnReturn[0].TraceId,
                                null)
                        });
                    }

                    if (place.Kind == PlaceKind.Local &&
                        currentState.IsBorrower(place.Local))
                    {
                        EndBorrowsByBorrower(
                            place.Local,
                            blockId,
                            index,
                            currentState,
                            ResolveSpan(place.Span, ret.Value?.Span ?? SourceSpan.Empty, ret.Span, terminator.Span));
                    }
                    else
                    {
                        EndBorrowsByBorrowTarget(
                            returnTarget,
                            blockId,
                            index,
                            currentState,
                            ResolveSpan(place.Span, ret.Value?.Span ?? SourceSpan.Empty, ret.Span, terminator.Span));
                    }
                }
                break;
        }
    }

    private void CheckAndAddBorrow(
        BorrowTarget borrowTarget,
        LocalId borrowee,
        LocalId borrower,
        bool isMutable,
        BlockId blockId,
        int index,
        BorrowFlowState currentState,
        SourceSpan span,
        ActiveBorrow? sourceBorrow = null,
        string? traceStep = null,
        string? originSummary = null)
    {
        // 检查借用冲突
        CheckBorrowConflict(borrowTarget, isMutable, blockId, index, currentState, span);

        var effectiveOriginLocation = sourceBorrow?.OriginLocation ?? (blockId, index);
        var effectiveOriginSummary = sourceBorrow?.OriginSummary ?? originSummary ?? "";
        var effectiveTrace = sourceBorrow != null
            ? new List<string>(sourceBorrow.AliasTrace)
            : [];

        if (!string.IsNullOrEmpty(traceStep))
        {
            effectiveTrace.Add(traceStep);
        }
        else if (effectiveTrace.Count == 0 && !string.IsNullOrEmpty(effectiveOriginSummary))
        {
            effectiveTrace.Add(effectiveOriginSummary);
        }

        var canReuseSourceTraceId =
            sourceBorrow != null &&
            !string.IsNullOrEmpty(sourceBorrow.TraceId) &&
            string.IsNullOrEmpty(traceStep);
        var traceId = canReuseSourceTraceId
            ? sourceBorrow!.TraceId
            : BorrowAliasTrace.BuildTraceId(
                effectiveOriginLocation,
                effectiveTrace,
                borrower,
                borrowee,
                borrowTarget,
                isMutable);

        var borrowId = GetOrCreateBorrowId(
            borrower,
            borrowee,
            borrowTarget,
            isMutable,
            blockId,
            index,
            effectiveOriginLocation,
            effectiveOriginSummary,
            effectiveTrace,
            traceId,
            span);
        if (currentState.ContainsBorrowId(borrowId))
        {
            return;
        }

        // 创建新借用（即使有冲突也继续添加，以便后续检查）
        var borrow = new ActiveBorrow
        {
            Id = borrowId,
            Borrower = borrower,
            Borrowee = borrowee,
            BorrowTarget = borrowTarget,
            IsMutable = isMutable,
            Location = (blockId, index),
            Span = span,
            OriginLocation = effectiveOriginLocation,
            OriginSummary = effectiveOriginSummary,
            AliasTrace = effectiveTrace,
            TraceId = traceId
        };

        currentState.TryAddBorrow(borrow);
    }

    private void CheckBorrowConflict(
        BorrowTarget borrowTarget,
        bool isNewMutable,
        BlockId blockId,
        int index,
        BorrowFlowState currentState,
        SourceSpan span)
    {
        // 找到对该变量的所有活跃借用
        var existingBorrows = currentState.GetBorrowsByBorrowTarget(borrowTarget);

        foreach (var existing in existingBorrows)
        {
            // 可变借用必须唯一
            if (isNewMutable)
            {
                // 避免重复报告相同的冲突
                var conflictKey = $"{borrowTarget.StableKey}:{existing.Location.Block.Value}:{existing.Location.Index}";
                if (!_reportedConflicts.Contains(conflictKey))
                {
                    _reportedConflicts.Add(conflictKey);
                    AddDiagnostic(new BorrowDiagnostic
                    {
                        Kind = existing.IsMutable ? BorrowErrorKind.MultipleMutableBorrows : BorrowErrorKind.MutableWhileImmutableBorrowed,
                        Message = DiagnosticMessages.BorrowCreateMutableConflict,
                        Location = (blockId, index),
                        RelatedLocation = existing.Location,
                        Span = span,
                        RelatedSpan = existing.Span,
                        RelatedAliasTrace = [.. existing.AliasTrace],
                        RelatedAliasTraceId = existing.TraceId,
                        Hint = BorrowAliasTrace.BuildConflictHint(
                            existing.AliasTrace,
                            existing.TraceId,
                            null)
                    });
                }
            }
            else if (existing.IsMutable)
            {
                // 不可变借用不能与可变借用共存
                var conflictKey = $"{borrowTarget.StableKey}:{existing.Location.Block.Value}:{existing.Location.Index}:imm";
                if (!_reportedConflicts.Contains(conflictKey))
                {
                    _reportedConflicts.Add(conflictKey);
                    AddDiagnostic(new BorrowDiagnostic
                    {
                        Kind = BorrowErrorKind.ImmutableWhileMutableBorrowed,
                        Message = DiagnosticMessages.BorrowCreateImmutableWhileMutable,
                        Location = (blockId, index),
                        RelatedLocation = existing.Location,
                        Span = span,
                        RelatedSpan = existing.Span,
                        RelatedAliasTrace = [.. existing.AliasTrace],
                        RelatedAliasTraceId = existing.TraceId,
                        Hint = BorrowAliasTrace.BuildConflictHint(
                            existing.AliasTrace,
                            existing.TraceId,
                            null)
                    });
                }
            }
        }
    }

    private void EndBorrowsByBorrower(
        LocalId localId,
        BlockId blockId,
        int index,
        BorrowFlowState currentState,
        SourceSpan endSpan)
    {
        currentState.EndBorrowsByBorrower(
            localId,
            borrow => MarkBorrowEnded(borrow, blockId, index, endSpan));
    }

    private void EndBorrowsByBorrowee(
        LocalId localId,
        BlockId blockId,
        int index,
        BorrowFlowState currentState,
        SourceSpan endSpan)
    {
        currentState.EndBorrowsByBorrowee(
            localId,
            borrow => MarkBorrowEnded(borrow, blockId, index, endSpan));
    }

    private void EndBorrowsByBorrowTarget(
        BorrowTarget borrowTarget,
        BlockId blockId,
        int index,
        BorrowFlowState currentState,
        SourceSpan endSpan)
    {
        currentState.EndBorrowsByBorrowTarget(
            borrowTarget,
            borrow => MarkBorrowEnded(borrow, blockId, index, endSpan));
    }

    private void OverwriteBorrower(
        LocalId localId,
        BlockId blockId,
        int index,
        BorrowFlowState currentState,
        SourceSpan endSpan)
    {
        if (!localId.IsValid || !currentState.IsBorrower(localId))
        {
            return;
        }

        EndBorrowsByBorrower(localId, blockId, index, currentState, endSpan);
    }

    private bool RequireLoadCapability(
        BorrowTarget target,
        bool isMutableBorrow,
        BlockId blockId,
        int index,
        SourceSpan span,
        string? readAction = null,
        string? mutableAction = null)
    {
        if (_capabilitySnapshot == null || !_capabilitySnapshot.IsEnforced)
        {
            return true;
        }

        if (isMutableBorrow)
        {
            return RequireWriteCapability(
                target,
                blockId,
                index,
                span,
                mutableAction ?? DiagnosticMessages.BorrowActionCreateMutableBorrow);
        }

        if (_capabilitySnapshot.CanRead(target))
        {
            return true;
        }

        var targetDisplay = BorrowDiagnosticFormatter.FormatBorrowTarget(target, _localsById);
        var resolution = _capabilitySnapshot.ExplainCapabilityResolution(target, BorrowCapabilityKind.Read);
        var effectiveReadAction = readAction ?? DiagnosticMessages.BorrowActionCreateSharedBorrow;
        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.ReadCapabilityDenied,
            Message = DiagnosticMessages.BorrowReadCapabilityDenied(effectiveReadAction, targetDisplay),
            Location = (blockId, index),
            Span = span,
            Hint = DiagnosticMessages.BorrowReadCapabilityRequiredHint(targetDisplay, resolution)
        });
        return false;
    }

    private bool RequireWriteCapability(
        BorrowTarget target,
        BlockId blockId,
        int index,
        SourceSpan span,
        string? action = null)
    {
        if (_capabilitySnapshot == null || !_capabilitySnapshot.IsEnforced)
        {
            return true;
        }

        if (_capabilitySnapshot.CanWrite(target))
        {
            return true;
        }

        var targetDisplay = BorrowDiagnosticFormatter.FormatBorrowTarget(target, _localsById);
        var resolution = _capabilitySnapshot.ExplainCapabilityResolution(target, BorrowCapabilityKind.Write);
        var effectiveAction = action ?? DiagnosticMessages.BorrowActionWrite;
        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.WriteCapabilityDenied,
            Message = DiagnosticMessages.BorrowWriteCapabilityDenied(effectiveAction, targetDisplay),
            Location = (blockId, index),
            Span = span,
            Hint = DiagnosticMessages.BorrowWriteCapabilityRequiredHint(targetDisplay, resolution)
        });
        return false;
    }

    private bool RequireMoveCapability(
        LocalId localId,
        BlockId blockId,
        int index,
        SourceSpan span,
        string? action = null)
    {
        if (_capabilitySnapshot == null || !_capabilitySnapshot.IsEnforced)
        {
            return true;
        }

        if (_capabilitySnapshot.CanMove(localId))
        {
            return true;
        }

        var localDisplay = BorrowDiagnosticFormatter.FormatLocal(localId, _localsById);
        var resolution = _capabilitySnapshot.ExplainCapabilityResolution(localId, BorrowCapabilityKind.Move);
        var effectiveAction = action ?? DiagnosticMessages.BorrowActionMoveValue;
        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.MoveCapabilityDenied,
            Message = DiagnosticMessages.BorrowMoveCapabilityDenied(effectiveAction, localDisplay),
            Location = (blockId, index),
            Span = span,
            Hint = DiagnosticMessages.BorrowMoveLocalCapabilityRequiredHint(localDisplay, resolution)
        });
        return false;
    }

    private bool RequireMoveCapability(
        BorrowTarget target,
        BlockId blockId,
        int index,
        SourceSpan span,
        string? action = null)
    {
        if (_capabilitySnapshot == null || !_capabilitySnapshot.IsEnforced)
        {
            return true;
        }

        if (_capabilitySnapshot.CanMove(target))
        {
            return true;
        }

        var targetDisplay = BorrowDiagnosticFormatter.FormatBorrowTarget(target, _localsById);
        var resolution = _capabilitySnapshot.ExplainCapabilityResolution(target, BorrowCapabilityKind.Move);
        var effectiveAction = action ?? DiagnosticMessages.BorrowActionMoveValue;
        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.MoveCapabilityDenied,
            Message = DiagnosticMessages.BorrowMoveCapabilityDenied(effectiveAction, targetDisplay),
            Location = (blockId, index),
            Span = span,
            Hint = DiagnosticMessages.BorrowMoveTargetCapabilityRequiredHint(targetDisplay, resolution)
        });
        return false;
    }

    private void ApplyCallEffects(
        MirCall call,
        BlockId blockId,
        int index,
        BorrowFlowState currentState)
    {
        var signature = TryGetCallSignature(call);

        if (call.Target is MirPlace { Kind: PlaceKind.Local, Local: var targetLocal })
        {
            OverwriteBorrower(
                targetLocal,
                blockId,
                index,
                currentState,
                ResolveSpan(call.Target.Span, call.Span));
        }

        if (signature == null)
        {
            return;
        }

        if (!VerifyCallArgumentCapabilities(call, signature, blockId, index, currentState))
        {
            return;
        }

        var returnedBorrowSources = LoanCallAnalysis.CollectReturnedBorrowSources(
            call,
            signature,
            localId => currentState.ResolveBorrowTargetPaths(localId),
            localId => currentState.GetBorrowsByBorrower(localId),
            borrow => borrow.BorrowTarget);

        LoanCallAnalysis.ForEachOwnedLocalArgument(
            call,
            signature,
            (argumentIndex, localId) =>
            {
                var consumeSpan = ResolveSpan(call.Arguments[argumentIndex].Span, call.Span);
                if (currentState.IsBorrower(localId))
                {
                    EndBorrowsByBorrower(
                        localId,
                        blockId,
                        index,
                        currentState,
                        consumeSpan);
                }
                else
                {
                    EndBorrowsByBorrowee(
                        localId,
                        blockId,
                        index,
                        currentState,
                        consumeSpan);
                }
            });

        if (!VerifyReturnedBorrowCapabilities(returnedBorrowSources, blockId, index, call.Span))
        {
            return;
        }

        var targetSpan = ResolveSpan(call.Target?.Span ?? SourceSpan.Empty, call.Span);
        LoanCallAnalysis.ApplyReturnedBorrowSources(
            returnedBorrowSources,
            binding =>
            {
                CheckAndAddBorrow(
                    binding.BorrowTarget,
                    binding.Borrowee,
                    binding.TargetLocal,
                    binding.IsMutable,
                    blockId,
                    index,
                    currentState,
                    targetSpan,
                    originSummary: BorrowDiagnosticFormatter.BuildCallTrace(call, binding.ArgumentIndex, binding.TargetLocal, binding.Borrowee, blockId, index));
            },
            (binding, sourceBorrow) =>
            {
                CheckAndAddBorrow(
                    binding.BorrowTarget,
                    binding.Borrowee,
                    binding.TargetLocal,
                    binding.IsMutable,
                    blockId,
                    index,
                    currentState,
                    targetSpan,
                    sourceBorrow,
                    BorrowDiagnosticFormatter.BuildCallTrace(call, binding.ArgumentIndex, binding.TargetLocal, binding.Borrowee, blockId, index));
            });
    }

    private bool VerifyCallArgumentCapabilities(
        MirCall call,
        LoanSignature signature,
        BlockId blockId,
        int index,
        BorrowFlowState currentState)
    {
        for (int argumentIndex = 0;
             argumentIndex < call.Arguments.Count && argumentIndex < signature.ParamRequirements.Count;
             argumentIndex++)
        {
            if (call.Arguments[argumentIndex] is not MirPlace argumentPlace)
            {
                continue;
            }

            var mode = signature.ParamRequirements[argumentIndex].Mode;
            var argumentSpan = ResolveSpan(call.Arguments[argumentIndex].Span, call.Span);
            if (!VerifyCallArgumentCapability(argumentPlace, mode, argumentSpan, blockId, index, currentState))
            {
                return false;
            }
        }

        return true;
    }

    private bool VerifyCallArgumentCapability(
        MirPlace argumentPlace,
        ParamBorrowMode mode,
        SourceSpan span,
        BlockId blockId,
        int index,
        BorrowFlowState currentState)
    {
        if (mode == ParamBorrowMode.Own)
        {
            if (argumentPlace.Kind == PlaceKind.Local)
            {
                return RequireMoveCapability(
                    argumentPlace.Local,
                    blockId,
                    index,
                    span,
                    DiagnosticMessages.BorrowActionMoveValueThroughCallArgument);
            }

            if (BorrowTarget.TryResolve(argumentPlace, out var moveTarget))
            {
                return RequireMoveCapability(
                    moveTarget,
                    blockId,
                    index,
                    span,
                    DiagnosticMessages.BorrowActionMoveValueThroughCallArgument);
            }

            return true;
        }

        return mode switch
        {
            ParamBorrowMode.BorrowMutable => RequireCallArgumentLoadCapability(
                argumentPlace,
                isMutableBorrow: true,
                span,
                blockId,
                index,
                currentState,
                readAction: DiagnosticMessages.BorrowActionCreateSharedBorrowThroughCallArgument,
                mutableAction: DiagnosticMessages.BorrowActionCreateMutableBorrowThroughCallArgument),
            ParamBorrowMode.BorrowShared => RequireCallArgumentLoadCapability(
                argumentPlace,
                isMutableBorrow: false,
                span,
                blockId,
                index,
                currentState,
                readAction: DiagnosticMessages.BorrowActionCreateSharedBorrowThroughCallArgument,
                mutableAction: DiagnosticMessages.BorrowActionCreateMutableBorrowThroughCallArgument),
            _ => true
        };
    }

    private bool RequireCallArgumentLoadCapability(
        MirPlace argumentPlace,
        bool isMutableBorrow,
        SourceSpan span,
        BlockId blockId,
        int index,
        BorrowFlowState currentState,
        string readAction,
        string mutableAction)
    {
        foreach (var target in ResolveCallArgumentBorrowTargets(argumentPlace, currentState).Distinct())
        {
            if (!RequireLoadCapability(target, isMutableBorrow, blockId, index, span, readAction, mutableAction))
            {
                return false;
            }
        }

        return true;
    }

    private IReadOnlyList<BorrowTarget> ResolveCallArgumentBorrowTargets(
        MirPlace argumentPlace,
        BorrowFlowState currentState)
    {
        if (argumentPlace.Kind == PlaceKind.Local)
        {
            return currentState.ResolveBorrowTargetPaths(argumentPlace.Local);
        }

        if (BorrowTarget.TryResolve(argumentPlace, out var target))
        {
            return [target];
        }

        return [];
    }

    private bool VerifyReturnedBorrowCapabilities(
        IEnumerable<(ReturnedCallBorrowBinding Binding, List<ActiveBorrow> Sources)> returnedBorrowSources,
        BlockId blockId,
        int index,
        SourceSpan span)
    {
        foreach (var (binding, _) in returnedBorrowSources)
        {
            if (!RequireLoadCapability(
                    binding.BorrowTarget,
                    binding.IsMutable,
                    blockId,
                    index,
                    span,
                    DiagnosticMessages.BorrowActionBindReturnedSharedBorrow,
                    DiagnosticMessages.BorrowActionBindReturnedMutableBorrow))
            {
                return false;
            }
        }

        return true;
    }

    private void CheckBorrowLifetimes()
    {
        foreach (var borrow in ActiveBorrows)
        {
            // 检查是否达到错误限制
            if (_recoveryContext.HasReachedLimit)
            {
                break;
            }

            // 获取被借用变量的活跃范围
            if (!_livenessAnalyzer.LiveRanges.TryGetValue(borrow.Borrowee, out var liveRange))
            {
                continue;
            }

            // 检查借用是否超过变量的生命周期
            var borrowEnd = borrow.EndLocation ?? (BlockId.None, int.MaxValue);

            // 如果借用结束时变量已经不活跃，则生命周期过长
            if (borrowEnd.Block.IsValid && !liveRange.LiveBlocks.Contains(borrowEnd.Block))
            {
                AddDiagnostic(new BorrowDiagnostic
                {
                    Kind = BorrowErrorKind.LifetimeTooLong,
                    Message = DiagnosticMessages.BorrowLifetimeExceedsBorrowee,
                    Location = borrow.Location,
                    RelatedLocation = borrowEnd,
                    Span = borrow.Span,
                    RelatedSpan = borrow.EndSpan,
                    AliasTrace = [.. borrow.AliasTrace]
                });
            }
        }
    }

    private BorrowFlowState GetIncomingState(
        BlockId blockId,
        ControlFlowGraph cfg,
        Dictionary<BlockId, BorrowFlowState> blockOutStates)
    {
        if (blockId.Equals(_function.EntryBlockId))
        {
            return BorrowFlowState.Empty();
        }

        var predecessors = cfg.GetPredecessors(blockId);
        if (predecessors.Count == 0)
        {
            return BorrowFlowState.Empty();
        }

        BorrowFlowState? mergedState = null;

        foreach (var pred in predecessors)
        {
            if (!blockOutStates.TryGetValue(pred, out var predState))
            {
                continue;
            }

            if (mergedState == null)
            {
                mergedState = predState.Clone();
                continue;
            }

            foreach (var borrow in predState.Borrows)
            {
                mergedState.TryAddBorrow(BorrowFlowState.CloneBorrow(borrow));
            }
        }

        return mergedState ?? BorrowFlowState.Empty();
    }

    private static List<ActiveBorrow> CloneBorrows(IEnumerable<ActiveBorrow> borrows)
    {
        return borrows.Select(BorrowFlowState.CloneBorrow).ToList();
    }

    /// <summary>
    /// Records the borrow snapshot at a program point.
    /// Skips cloning when the borrow set hasn't changed since the last snapshot
    /// (copy-on-write via dirty flag), reducing O(P×B) cloning to O(D×B) where
    /// D = number of points that actually modify borrows.
    /// </summary>
    private void CapturePointState(BlockId blockId, int index, BorrowFlowState currentState)
    {
        var isDirty = currentState.ConsumeDirty();
        if (_lastPointSnapshot == null || isDirty)
        {
            _lastPointSnapshot = CloneBorrows(currentState.Borrows);
        }
        _borrowsAtPoint[(blockId, index)] = _lastPointSnapshot!;
    }

    private int GetOrCreateBorrowId(
        LocalId borrower,
        LocalId borrowee,
        BorrowTarget borrowTarget,
        bool isMutable,
        BlockId blockId,
        int index,
        (BlockId Block, int Index) originLocation,
        string originSummary,
        List<string> aliasTrace,
        string traceId,
        SourceSpan span)
    {
        var key = BorrowAliasTrace.BuildBorrowStateKey(
            borrower,
            borrowee,
            borrowTarget,
            isMutable,
            (blockId, index),
            originLocation,
            originSummary,
            traceId,
            aliasTrace);
        if (_borrowIdsByKey.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = _nextBorrowId++;
        _borrowIdsByKey[key] = id;

        var activeBorrow = new ActiveBorrow
        {
            Id = id,
            Borrower = borrower,
            Borrowee = borrowee,
            BorrowTarget = borrowTarget,
            IsMutable = isMutable,
            Location = (blockId, index),
            Span = span,
            OriginLocation = originLocation,
            OriginSummary = originSummary,
            AliasTrace = [.. aliasTrace],
            TraceId = traceId
        };

        ActiveBorrows.Add(activeBorrow);
        _activeBorrowById[id] = activeBorrow;
        return id;
    }

    private void MarkBorrowEnded(ActiveBorrow borrow, BlockId blockId, int index, SourceSpan endSpan)
    {
        if (borrow.EndLocation != null)
        {
            return;
        }

        borrow.EndLocation = (blockId, index);
        borrow.EndSpan = endSpan;
        if (_activeBorrowById.TryGetValue(borrow.Id, out var active) &&
            active.EndLocation == null)
        {
            active.EndLocation = (blockId, index);
            active.EndSpan = endSpan;
        }
    }

    private LoanSignature? TryGetCallSignature(MirCall call)
    {
        if (_loanSignatureCache == null || _symbolTable == null)
        {
            return null;
        }

        return LoanCallAnalysis.TryResolveCalleeSignature(call, _loanSignatureCache, _symbolTable);
    }

    private bool ShouldDetachBorrowAliasOnCopy(LocalId targetLocal)
    {
        return _localsById.TryGetValue(targetLocal, out var local) &&
               CopyTypeSemantics.IsCopyType(local.TypeId, _hasCopyImplResolver, _dynamicTypeKeys);
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
    private void AddDiagnostic(BorrowDiagnostic diagnostic)
    {
        var diagnosticKey = BorrowAliasTrace.BuildDiagnosticDedupKey(diagnostic);
        if (!_reportedDiagnostics.Add(diagnosticKey))
        {
            return;
        }

        Diagnostics.Add(diagnostic);
        _recoveryContext.RecordError();
    }
}

internal sealed class BorrowFlowState
{
    private readonly IndexedBorrowState<ActiveBorrow, BorrowAliasTrace.BorrowStateKey> _state;

    public List<ActiveBorrow> Borrows => _state.Borrows;

    public BorrowFlowState(List<ActiveBorrow> borrows)
        : this(borrows, cloneInputs: true)
    {
    }

    private BorrowFlowState(List<ActiveBorrow> borrows, bool cloneInputs)
    {
        _state = new IndexedBorrowState<ActiveBorrow, BorrowAliasTrace.BorrowStateKey>(
            borrows,
            GetBorrowKey,
            borrow => borrow.Borrower,
            borrow => borrow.Borrowee,
            CloneBorrow,
            cloneInputs,
            borrow => borrow.Id,
            borrow => borrow.BorrowTarget);
    }

    public static BorrowFlowState Empty() => new([]);

    public BorrowFlowState Clone()
    {
        return new BorrowFlowState(Borrows.Select(CloneBorrow).ToList(), cloneInputs: false);
    }

    public bool SemanticallyEquals(BorrowFlowState other)
    {
        return _state.SemanticallyEquals(other._state);
    }

    /// <summary>
    /// Returns whether borrows were added/removed since the last call,
    /// and resets the flag.
    /// </summary>
    public bool ConsumeDirty() => _state.ConsumeDirty();

    public bool ContainsBorrowId(int borrowId)
    {
        return _state.ContainsBorrowId(borrowId);
    }

    public bool TryAddBorrow(ActiveBorrow borrow)
    {
        return _state.TryAddBorrow(borrow);
    }

    public bool IsBorrower(LocalId localId)
    {
        return _state.IsBorrower(localId);
    }

    public List<ActiveBorrow> GetBorrowsByBorrower(LocalId localId)
    {
        return _state.GetBorrowsByBorrower(localId);
    }

    public List<ActiveBorrow> GetBorrowsByBorrowee(LocalId localId)
    {
        return _state.GetBorrowsByBorrowee(localId);
    }

    public List<ActiveBorrow> GetBorrowsByBorrowTarget(BorrowTarget target)
    {
        return _state.GetBorrowsByBorrowTarget(target);
    }

    public List<LocalId> ResolveBorrowTargets(LocalId localId)
    {
        return _state.ResolveBorrowTargets(localId);
    }

    public List<BorrowTarget> ResolveBorrowTargetPaths(LocalId localId)
    {
        return _state.ResolveBorrowTargetPaths(localId);
    }

    public void EndBorrowsByBorrower(LocalId localId, Action<ActiveBorrow> onBorrowEnded)
    {
        _state.EndBorrowsByBorrower(localId, onBorrowEnded);
    }

    public void EndBorrowsByBorrowee(LocalId localId, Action<ActiveBorrow> onBorrowEnded)
    {
        _state.EndBorrowsByBorrowee(localId, onBorrowEnded);
    }

    public void EndBorrowsByBorrowTarget(BorrowTarget target, Action<ActiveBorrow> onBorrowEnded)
    {
        _state.EndBorrowsByBorrowTarget(target, onBorrowEnded);
    }

    public static ActiveBorrow CloneBorrow(ActiveBorrow borrow)
    {
        return new ActiveBorrow
        {
            Id = borrow.Id,
            Borrower = borrow.Borrower,
            Borrowee = borrow.Borrowee,
            BorrowTarget = borrow.BorrowTarget,
            IsMutable = borrow.IsMutable,
            Location = borrow.Location,
            Span = borrow.Span,
            OriginLocation = borrow.OriginLocation,
            OriginSummary = borrow.OriginSummary,
            AliasTrace = borrow.AliasTrace,
            TraceId = borrow.TraceId,
            EndLocation = borrow.EndLocation,
            EndSpan = borrow.EndSpan
        };
    }

    public static BorrowAliasTrace.BorrowStateKey GetBorrowKey(ActiveBorrow borrow)
    {
        return BorrowAliasTrace.BuildBorrowStateKey(
            borrow.Borrower,
            borrow.Borrowee,
            borrow.BorrowTarget,
            borrow.IsMutable,
            borrow.Location,
            borrow.OriginLocation,
            borrow.OriginSummary,
            borrow.TraceId,
            borrow.AliasTrace);
    }
}
