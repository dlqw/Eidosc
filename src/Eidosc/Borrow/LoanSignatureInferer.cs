using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Borrow;

/// <summary>
/// Builds signature-derived ownership requirements and infers body-derived loan facts.
/// </summary>
public sealed class LoanSignatureInferer
{
    private readonly record struct ReturnedBorrowOrigin(
        IReadOnlyList<int> BoundParams,
        bool IsMutable);

    private readonly MirFunc _function;
    private readonly LoanSignatureCache _cache;
    private readonly ControlFlowGraph? _precomputedCfg;
    private readonly SymbolTable _symbolTable;
    private readonly Func<TypeId, bool> _hasCopyImplResolver;
    private readonly IReadOnlyDictionary<int, string>? _dynamicTypeKeys;
    private readonly IReadOnlyDictionary<int, TypeDescriptor>? _typeDescriptors;

    /// <summary>
    /// 下一个生命周期 ID
    /// </summary>
    private int _nextLifetimeId = 1;

    /// <summary>
    /// 参数到生命周期的映射
    /// </summary>
    private readonly Dictionary<int, LifetimeId> _paramLifetimes = new();

    /// <summary>
    /// 参数局部变量列表
    /// </summary>
    private readonly List<MirLocal> _paramLocals = [];

    /// <summary>
    /// 局部变量到参数索引的映射
    /// </summary>
    private readonly Dictionary<LocalId, int> _paramLocalIndex = new();

    /// <summary>
    /// 临时变量到源变量的别名映射
    /// </summary>
    private readonly Dictionary<LocalId, LocalId> _localAliases = new();

    /// <summary>
    /// 每个程序点的参数别名来源状态（CFG/dataflow）
    /// </summary>
    private readonly Dictionary<(BlockId Block, int Index), LoanInferState> _originStatesAtPoint = new();
    /// <summary>
    /// 是否启用调用点生命周期传播
    /// </summary>
    private bool _includeCallConstraints = true;
    private bool _analysisPrepared;
    private OwnershipContract _ownershipContract = OwnershipContract.Empty;

    /// <summary>
    /// 诊断信息列表
    /// </summary>
    public List<BorrowDiagnostic> Diagnostics { get; } = [];

    public LoanSignatureInferer(
        MirFunc function,
        LoanSignatureCache cache,
        SymbolTable symbolTable,
        IReadOnlyDictionary<int, string>? dynamicTypeKeys = null,
        ControlFlowGraph? cfg = null,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors = null)
    {
        _function = function;
        _cache = cache;
        _precomputedCfg = cfg;
        _symbolTable = symbolTable;
        _hasCopyImplResolver = CopyTypeSemantics.CreateSymbolTableCopyResolver(symbolTable);
        _dynamicTypeKeys = dynamicTypeKeys;
        _typeDescriptors = typeDescriptors ?? ParseLegacyTypeDescriptors(dynamicTypeKeys);
    }

    private static IReadOnlyDictionary<int, TypeDescriptor>? ParseLegacyTypeDescriptors(
        IReadOnlyDictionary<int, string>? dynamicTypeKeys)
    {
        if (dynamicTypeKeys == null || dynamicTypeKeys.Count == 0)
        {
            return null;
        }

        var descriptors = new Dictionary<int, TypeDescriptor>();
        foreach (var (typeId, typeKey) in dynamicTypeKeys)
        {
            if (TypeKeyParsing.TryParseTypeDescriptor(typeKey, out var descriptor))
            {
                descriptors[typeId] = descriptor;
            }
        }

        return descriptors;
    }

    /// <summary>
    /// 推断函数的借用签名
    /// </summary>
    public LoanSignature Infer(bool includeCallConstraints = true, bool force = false)
    {
        // 如果已缓存，直接返回
        if (!force && _function.SymbolId.IsValid && _cache.HasSignature(_function.SymbolId))
        {
            return _cache.GetSignature(_function.SymbolId)!;
        }

        _includeCallConstraints = includeCallConstraints;
        _nextLifetimeId = 1;
        _paramLifetimes.Clear();
        Diagnostics.Clear();
        ParamRequirements = [];
        ReturnConstraint = new ReturnBorrowConstraint();
        LifetimeConstraints = [];

        EnsurePreparedAnalysis();
        _ownershipContract = OwnershipContract.Create(
            _function.SymbolId,
            _function.SourceName.Length > 0 ? _function.SourceName : _function.Name,
            _paramLocals.Select(static local => (local.Name, local.TypeId)).ToArray(),
            _function.ReturnType,
            _typeDescriptors,
            _symbolTable);

        // 1. 推断参数借用要求
        ParamRequirements = InferParamRequirements();

        // 2. 推断返回值借用约束
        ReturnConstraint = InferReturnConstraint();

        // 3. 收集生命周期约束
        LifetimeConstraints = InferLifetimeConstraints();

        // 4. 构建生命周期参数
        var lifetimeParams = BuildLifetimeParams();

        // 5. 构建借用签名
        var signature = new LoanSignature
        {
            FunctionName = _function.Name,
            FunctionSymbol = _function.SymbolId,
            OwnershipContract = _ownershipContract,
            LifetimeParams = lifetimeParams,
            ParamRequirements = ParamRequirements,
            ReturnConstraint = ReturnConstraint,
            LifetimeConstraints = LifetimeConstraints,
            Span = _function.Span
        };

        // 缓存签名
        if (_function.SymbolId.IsValid)
        {
            _cache.SetSignature(_function.SymbolId, signature);
        }

        return signature;
    }

    private void EnsurePreparedAnalysis()
    {
        if (_analysisPrepared)
        {
            return;
        }

        _paramLocals.Clear();
        _paramLocalIndex.Clear();
        _localAliases.Clear();
        _originStatesAtPoint.Clear();
        InitializeParamMetadata();
        BuildLocalAliasMap();
        BuildOriginStates();
        _analysisPrepared = true;
    }

    /// <summary>
    /// 推断参数的借用要求
    /// </summary>
    private List<ParamBorrowRequirement> InferParamRequirements()
    {
        var requirements = new List<ParamBorrowRequirement>();

        var paramIndex = 0;
        foreach (var local in _paramLocals)
        {
            var mode = InferParamBorrowMode(local, paramIndex);
            var lifetime = LifetimeId.None;

            // 如果是借用类型，分配生命周期
            if (mode is ParamBorrowMode.BorrowShared or ParamBorrowMode.BorrowMutable)
            {
                lifetime = AllocateLifetime();
                _paramLifetimes[paramIndex] = lifetime;
            }

            requirements.Add(new ParamBorrowRequirement
            {
                ParamIndex = paramIndex,
                Name = local.Name,
                Mode = mode,
                Lifetime = lifetime,
                Span = local.Span
            });

            paramIndex++;
        }

        return requirements;
    }

    private void InitializeParamMetadata()
    {
        _paramLocals.AddRange(_function.Locals.Where(local => local.IsParameter));
        for (int i = 0; i < _paramLocals.Count; i++)
        {
            _paramLocalIndex[_paramLocals[i].Id] = i;
        }
    }

    /// <summary>
    /// 推断单个参数的借用模式
    /// </summary>
    private ParamBorrowMode InferParamBorrowMode(MirLocal local, int paramIndex)
    {
        return _ownershipContract.GetParameter(paramIndex).Projection.Kind switch
        {
            OwnershipPassingKind.SharedBorrow => ParamBorrowMode.BorrowShared,
            OwnershipPassingKind.MutableBorrow => ParamBorrowMode.BorrowMutable,
            OwnershipPassingKind.ByValue => IsCopyType(local.TypeId)
                ? ParamBorrowMode.Copy
                : ParamBorrowMode.Own,
            _ => ParamBorrowMode.Own
        };
    }

    /// <summary>
    /// 推断返回值的借用约束
    /// </summary>
    private ReturnBorrowConstraint InferReturnConstraint()
    {
        var resultKind = _ownershipContract.Result.Projection.Kind;
        var returnsBorrow = resultKind is OwnershipPassingKind.SharedBorrow or OwnershipPassingKind.MutableBorrow;
        var returnsMutableBorrow = resultKind == OwnershipPassingKind.MutableBorrow;
        var returnSites = _function.BasicBlocks
            .Where(block => block.Terminator is MirReturn { Value: not null })
            .Select(block => (Block: block, Return: (MirReturn)block.Terminator!))
            .ToList();

        if (returnSites.Count == 0)
        {
            return CreateReturnBorrowConstraint(
                returnsBorrow,
                returnsMutableBorrow,
                [],
                returnsBorrow ? AllocateLifetime() : LifetimeId.None,
                _function.Span);
        }

        var boundParams = new HashSet<int>();
        SourceSpan span = _function.Span;
        var returnInstructionIndexByBlock = returnSites.ToDictionary(
            item => item.Block.Id,
            item => item.Block.Instructions.Count);

        foreach (var (block, ret) in returnSites)
        {
            if (ret.Value is not MirPlace place)
            {
                continue;
            }

            if (span.Equals(_function.Span))
            {
                span = ret.Span;
            }

            if (!returnsBorrow)
            {
                continue;
            }

            if (!TryResolveReturnedBorrowOrigin(
                    place,
                    block.Id,
                    block.Instructions.Count,
                    out var origin))
            {
                if (RequiresExplicitReturnedBorrowSource(place))
                {
                    AddReturnedBorrowEscapeDiagnostic(
                        ret.Span,
                        block.Id,
                        returnInstructionIndexByBlock[block.Id]);
                }

                continue;
            }

            boundParams.UnionWith(origin.BoundParams);
        }

        if (!returnsBorrow)
        {
            return CreateReturnBorrowConstraint(isBorrow: false, isMutable: false, [], LifetimeId.None, span);
        }

        var normalizedBoundParams = boundParams.OrderBy(index => index).ToList();
        var lifetime = GetOrCreateReturnLifetime(normalizedBoundParams);

        return CreateReturnBorrowConstraint(
            isBorrow: true,
            isMutable: returnsMutableBorrow,
            normalizedBoundParams,
            lifetime,
            span);
    }

    private ReturnBorrowConstraint CreateReturnBorrowConstraint(
        bool isBorrow,
        bool isMutable,
        List<int> boundParams,
        LifetimeId lifetime,
        SourceSpan span)
    {
        return new ReturnBorrowConstraint
        {
            IsBorrow = isBorrow,
            IsMutable = isMutable,
            Lifetime = lifetime,
            BoundToParams = boundParams,
            Span = span,
            Confidence = LoanInferenceConfidence.High,
            InternalNotes = []
        };
    }

    /// <summary>
    /// 推断生命周期约束
    /// </summary>
    private List<LifetimeConstraint> InferLifetimeConstraints()
    {
        var constraints = new List<LifetimeConstraint>();

        // 如果返回值绑定了多个参数，它们的生命周期必须兼容
        if (ReturnConstraint.BoundToParams.Count > 1)
        {
            // 所有绑定的参数生命周期必须至少和返回值一样长
            var returnLifetime = ReturnConstraint.Lifetime;
            foreach (var paramIndex in ReturnConstraint.BoundToParams)
            {
                if (_paramLifetimes.TryGetValue(paramIndex, out var paramLifetime))
                {
                    constraints.Add(new LifetimeConstraint
                    {
                        Sub = paramLifetime,
                        Sup = returnLifetime,
                        Span = _function.Span
                    });
                }
            }
        }

        // 分析函数体内的生命周期关系
        AnalyzeLifetimeRelationships(constraints);

        return constraints;
    }

    /// <summary>
    /// 构建生命周期参数列表
    /// </summary>
    private List<LifetimeParam> BuildLifetimeParams()
    {
        var lifetimeParams = new List<LifetimeParam>();

        // 收集所有使用的生命周期
        var usedLifetimes = new HashSet<LifetimeId>();

        foreach (var req in ParamRequirements)
        {
            if (req.Lifetime.IsValid)
            {
                usedLifetimes.Add(req.Lifetime);
            }
        }

        if (ReturnConstraint.Lifetime.IsValid)
        {
            usedLifetimes.Add(ReturnConstraint.Lifetime);
        }

        // 创建生命周期参数
        foreach (var lifetime in usedLifetimes)
        {
            var outlives = LifetimeConstraints
                .Where(c => c.Sub.Equals(lifetime))
                .Select(c => c.Sup)
                .ToList();

            lifetimeParams.Add(new LifetimeParam
            {
                Id = lifetime,
                Name = GenerateLifetimeName(lifetime),
                Outlives = outlives,
                Span = _function.Span
            });
        }

        return lifetimeParams;
    }

    /// <summary>
    /// 分配新的生命周期 ID
    /// </summary>
    private LifetimeId AllocateLifetime()
    {
        return new LifetimeId { Value = _nextLifetimeId++ };
    }

    /// <summary>
    /// 检查类型是否实现了 Copy trait
    /// </summary>
    private bool IsCopyType(TypeId typeId)
    {
        return CopyTypeSemantics.IsCopyType(
            typeId,
            _hasCopyImplResolver,
            _typeDescriptors,
            _dynamicTypeKeys);
    }

    /// <summary>
    /// 获取参数的局部变量
    /// </summary>
    private MirLocal? GetParamLocal(int paramIndex)
    {
        if (paramIndex >= 0 && paramIndex < _paramLocals.Count)
        {
            return _paramLocals[paramIndex];
        }

        return null;
    }

    /// <summary>
    /// 查找返回值中借用的参数
    /// </summary>
    private List<int> FindBorrowedParamsInReturnValue(MirPlace place)
    {
        var result = new List<int>();

        // 检查是否直接返回参数
        if (place.Kind == PlaceKind.Local)
        {
            var paramIndex = 0;
            foreach (var local in _function.Locals.Where(l => l.IsParameter))
            {
                if (local.Id.Equals(place.Local))
                {
                    result.Add(paramIndex);
                    break;
                }
                paramIndex++;
            }
        }

        // 检查字段访问
        if (place.Base != null)
        {
            result.AddRange(FindBorrowedParamsInReturnValue(place.Base));
        }

        return result;
    }

    private void BuildOriginStates()
    {
        if (_function.BasicBlocks.Count == 0)
        {
            return;
        }

        var cfg = _precomputedCfg ?? new ControlFlowGraph(_function);
        var blockById = _function.BasicBlocks.ToDictionary(block => block.Id);
        var blockOutStates = new Dictionary<BlockId, LoanInferState>();
        var pendingBlocks = new Queue<BlockId>(_function.BasicBlocks.Select(block => block.Id));
        var queuedBlocks = _function.BasicBlocks.Select(block => block.Id).ToHashSet();

        while (pendingBlocks.Count > 0)
        {
            var blockId = pendingBlocks.Dequeue();
            queuedBlocks.Remove(blockId);

            var block = blockById[blockId];
            var currentState = GetIncomingOriginState(blockId, cfg, blockOutStates);

            for (int instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                if (ShouldCaptureOriginState(block.Instructions[instructionIndex]))
                {
                    _originStatesAtPoint[(block.Id, instructionIndex)] = currentState.Clone();
                }

                ApplyOriginTransfer(block.Instructions[instructionIndex], block.Id, instructionIndex, currentState);
            }

            if (block.Terminator is MirReturn)
            {
                _originStatesAtPoint[(block.Id, block.Instructions.Count)] = currentState.Clone();
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
    }

    private static bool ShouldCaptureOriginState(MirInstruction instruction)
    {
        return instruction is MirCall;
    }

    private LoanInferState GetIncomingOriginState(
        BlockId blockId,
        ControlFlowGraph cfg,
        Dictionary<BlockId, LoanInferState> blockOutStates)
    {
        if (blockId.Equals(_function.EntryBlockId))
        {
            return BuildEntryOriginState();
        }

        var predecessors = cfg.GetPredecessors(blockId);
        if (predecessors.Count == 0)
        {
            return LoanInferState.Empty();
        }

        LoanInferState? singleState = null;
        List<LoanInferState>? predecessorStates = null;
        foreach (var predecessor in predecessors)
        {
            if (blockOutStates.TryGetValue(predecessor, out var predecessorState))
            {
                if (singleState == null && predecessorStates == null)
                {
                    singleState = predecessorState;
                }
                else
                {
                    predecessorStates ??= [singleState!];
                    predecessorStates.Add(predecessorState);
                }
            }
        }

        if (predecessorStates != null)
        {
            return LoanInferState.Merge(predecessorStates);
        }

        return singleState?.Clone() ?? LoanInferState.Empty();
    }

    private LoanInferState BuildEntryOriginState()
    {
        return LoanInferState.Empty();
    }

    private void ApplyOriginTransfer(
        MirInstruction instruction,
        BlockId blockId,
        int instructionIndex,
        LoanInferState state)
    {
        switch (instruction)
        {
            case MirMove move
                when MirLocalTransferAnalysis.TryGetBinding(move, out var moveBinding):
                TransferAlias(
                    state,
                    moveBinding.Target,
                    moveBinding.Source,
                    forceMutable: false,
                    allowParamFallback: ShouldAllowDirectParamReferenceOriginFallback(moveBinding.Source));
                break;

            case MirCopy copy
                when MirLocalTransferAnalysis.TryGetBinding(copy, out var copyBinding):
                TransferAlias(
                    state,
                    copyBinding.Target,
                    copyBinding.Source,
                    forceMutable: false,
                    allowParamFallback: ShouldAllowDirectParamReferenceOriginFallback(copyBinding.Source));
                break;

            case MirLoad load
                when MirLocalTransferAnalysis.TryGetBinding(load, out var loadBinding):
                TransferAlias(
                    state,
                    loadBinding.Target,
                    loadBinding.Source,
                    forceMutable: false,
                    allowParamFallback: load.IsMutableBorrow || load.CreatesBorrowAlias);
                break;

            case MirAssign assign when assign.Target.Kind == PlaceKind.Local:
                if (assign.Source is MirPlace { Kind: PlaceKind.Local, Local: var assignSourceLocal })
                {
                    TransferAlias(
                        state,
                        assign.Target.Local,
                        assignSourceLocal,
                        forceMutable: false,
                        allowParamFallback: ShouldAllowDirectParamReferenceOriginFallback(assignSourceLocal));
                }
                else
                {
                    state.Clear(assign.Target.Local);
                }
                break;

            case MirCaseInject { Target: MirPlace { Kind: PlaceKind.Local } target } injection:
                if (injection.Operand is MirPlace { Kind: PlaceKind.Local, Local: var sourceLocal })
                {
                    TransferAlias(
                        state,
                        target.Local,
                        sourceLocal,
                        forceMutable: false,
                        allowParamFallback: ShouldAllowDirectParamReferenceOriginFallback(sourceLocal));
                }
                else
                {
                    state.Clear(target.Local);
                }
                break;

            case MirStore store when store.Target.Kind == PlaceKind.Local:
                state.Clear(store.Target.Local);
                break;

            case MirBinOp binOp when binOp.Target is MirPlace { Kind: PlaceKind.Local, Local: var binOpTarget }:
                state.Clear(binOpTarget);
                break;

            case MirUnaryOp unaryOp when unaryOp.Target is MirPlace { Kind: PlaceKind.Local, Local: var unaryTarget }:
                state.Clear(unaryTarget);
                break;

            case MirAlloc alloc when alloc.Target.Kind == PlaceKind.Local:
                state.Clear(alloc.Target.Local);
                break;

            case MirDrop drop when drop.Value is MirPlace { Kind: PlaceKind.Local, Local: var dropLocal }:
                state.Clear(dropLocal);
                break;

            case MirCall call:
                ApplyCallOriginTransfer(call, blockId, instructionIndex, state);
                break;

        }
    }

    private void ApplyCallOriginTransfer(
        MirCall call,
        BlockId blockId,
        int instructionIndex,
        LoanInferState state)
    {
        if (call.Target is MirPlace { Kind: PlaceKind.Local, Local: var targetLocal })
        {
            state.Clear(targetLocal);
        }

        var signature = TryGetCalleeSignature(call);
        if (signature == null)
        {
            return;
        }

        for (int argIndex = 0; argIndex < call.Arguments.Count && argIndex < signature.ParamRequirements.Count; argIndex++)
        {
            if (signature.ParamRequirements[argIndex].Mode != ParamBorrowMode.Own ||
                call.Arguments[argIndex] is not MirPlace { Kind: PlaceKind.Local, Local: var argLocal })
            {
                continue;
            }

            state.Clear(argLocal);
        }

        if (!signature.ReturnsBorrow() ||
            call.Target is not MirPlace { Kind: PlaceKind.Local, Local: var returnLocal })
        {
            return;
        }

        var boundParams = MapBoundParams(call, signature, blockId, instructionIndex);
        if (boundParams.Count == 0)
        {
            return;
        }

        state.SetOrigin(returnLocal, boundParams, signature.ReturnConstraint.IsMutable);
    }

    private void TransferAlias(
        LoanInferState state,
        LocalId target,
        LocalId source,
        bool forceMutable,
        bool allowParamFallback)
    {
        if (state.TryGet(source, out var origin))
        {
            state.SetOrigin(target, origin.BoundParams, origin.IsMutable || forceMutable);
            return;
        }

        if (allowParamFallback)
        {
            var resolved = ResolveAlias(source);
            if (_paramLocalIndex.TryGetValue(resolved, out var resolvedParamIndex))
            {
                state.SetOrigin(target, [resolvedParamIndex], forceMutable);
                return;
            }
        }

        state.Clear(target);
    }

    private bool TryResolveBoundParamsAt(
        LocalId localId,
        BlockId blockId,
        int instructionIndex,
        out HashSet<int> boundParams)
    {
        if (TryResolveBoundParamsAt(localId, blockId, instructionIndex, out boundParams, out _))
        {
            return true;
        }

        return false;
    }

    private bool TryResolveBoundParamsAt(
        LocalId localId,
        BlockId blockId,
        int instructionIndex,
        out HashSet<int> boundParams,
        out bool isMutable)
    {
        if (TryGetOriginAt(localId, blockId, instructionIndex, out var origin))
        {
            boundParams = [.. origin.BoundParams];
            isMutable = origin.IsMutable;
            return true;
        }

        boundParams = [];
        isMutable = false;
        return false;
    }

    private bool TryGetOriginAt(
        LocalId localId,
        BlockId blockId,
        int instructionIndex,
        out LoanInferOrigin origin)
    {
        if (_originStatesAtPoint.TryGetValue((blockId, instructionIndex), out var state) &&
            state.TryGet(localId, out origin))
        {
            return true;
        }

        origin = null!;
        return false;
    }

    private IEnumerable<int> ResolveCallerParamIndices(
        MirOperand operand,
        BlockId blockId,
        int instructionIndex)
    {
        if (operand is not MirPlace { Kind: PlaceKind.Local, Local: var localId })
        {
            return [];
        }

        if (TryResolveBoundParamsAt(localId, blockId, instructionIndex, out var boundParams))
        {
            return boundParams.OrderBy(index => index).ToList();
        }

        if (TryGetCallerParamIndex(operand, out var paramIndex))
        {
            return [paramIndex];
        }

        return [];
    }

    private List<int> ResolveReturnBoundParams(
        MirPlace place,
        BlockId blockId,
        int instructionIndex,
        out bool mutableFromOrigin)
    {
        var boundParams = new HashSet<int>();
        mutableFromOrigin = false;

        CollectReturnBoundParams(place, blockId, instructionIndex, boundParams, ref mutableFromOrigin);
        return boundParams.OrderBy(index => index).ToList();
    }

    private bool TryResolveReturnedBorrowOrigin(
        MirPlace place,
        BlockId blockId,
        int instructionIndex,
        out ReturnedBorrowOrigin origin)
    {
        var boundParams = ResolveReturnBoundParams(place, blockId, instructionIndex, out var mutableFromOrigin);
        if (boundParams.Count > 0)
        {
            origin = new ReturnedBorrowOrigin(boundParams, mutableFromOrigin);
            return true;
        }

        if (TryResolveDirectReferenceParamReturn(place, out var directParamIndex, out var mutableFromParam))
        {
            origin = new ReturnedBorrowOrigin([directParamIndex], mutableFromParam);
            return true;
        }

        origin = default;
        return false;
    }

    private bool TryResolveDirectReferenceParamReturn(
        MirPlace place,
        out int paramIndex,
        out bool isMutable)
    {
        if (place.Kind == PlaceKind.Local)
        {
            if (_paramLocalIndex.TryGetValue(place.Local, out paramIndex))
            {
                var local = _function.Locals.FirstOrDefault(item => item.Id.Equals(place.Local));
                if (local != null && IsReferenceTypeId(local.TypeId))
                {
                    isMutable = IsMutableReferenceTypeId(local.TypeId);
                    return true;
                }
            }
        }

        if (place.Base != null)
        {
            return TryResolveDirectReferenceParamReturn(place.Base, out paramIndex, out isMutable);
        }

        paramIndex = -1;
        isMutable = false;
        return false;
    }

    private bool ShouldAllowDirectParamReferenceOriginFallback(LocalId source)
    {
        var resolved = ResolveAlias(source);
        if (!_paramLocalIndex.ContainsKey(resolved))
        {
            return false;
        }

        var local = _function.Locals.FirstOrDefault(item => item.Id.Equals(resolved));
        return local != null && IsReferenceTypeId(local.TypeId);
    }

    private bool RequiresExplicitReturnedBorrowSource(MirPlace place)
    {
        if (IsReferenceTypeId(_function.ReturnType) || IsReferenceTypeId(place.TypeId))
        {
            return true;
        }

        return false;
    }

    private bool IsReferenceTypeId(TypeId typeId)
    {
        return typeId.IsValid &&
               _typeDescriptors != null &&
               _typeDescriptors.TryGetValue(typeId.Value, out var descriptor) &&
               descriptor is TypeDescriptor.Ref or TypeDescriptor.MutRef;
    }

    private bool IsMutableReferenceTypeId(TypeId typeId)
    {
        return typeId.IsValid &&
               _typeDescriptors != null &&
               _typeDescriptors.TryGetValue(typeId.Value, out var descriptor) &&
               descriptor is TypeDescriptor.MutRef;
    }

    private void AddReturnedBorrowEscapeDiagnostic(SourceSpan span, BlockId blockId, int instructionIndex)
    {
        Diagnostics.Add(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.BorrowedWhileReturned,
            Message = DiagnosticMessages.BorrowReturnedBorrowMustComeFromInputParameter,
            Span = span,
            Location = (blockId, instructionIndex),
            Hint = DiagnosticMessages.BorrowReturnReferenceFromParameterHint
        });
    }

    private void CollectReturnBoundParams(
        MirPlace place,
        BlockId blockId,
        int instructionIndex,
        HashSet<int> boundParams,
        ref bool mutableFromOrigin)
    {
        if (place.Kind == PlaceKind.Local)
        {
            if (TryResolveBoundParamsAt(place.Local, blockId, instructionIndex, out var resolvedBound, out var isMutable))
            {
                boundParams.UnionWith(resolvedBound);
                mutableFromOrigin = mutableFromOrigin || isMutable;
            }

            var callBorrow = FindCallReturningBorrow(place.Local);
            if (callBorrow != null)
            {
                var mapped = MapBoundParams(
                    callBorrow.Value.Call,
                    callBorrow.Value.Signature,
                    callBorrow.Value.BlockId,
                    callBorrow.Value.InstructionIndex);
                boundParams.UnionWith(mapped);
                mutableFromOrigin = mutableFromOrigin || callBorrow.Value.Signature.ReturnConstraint.IsMutable;
            }
        }

        if (place.Base != null)
        {
            CollectReturnBoundParams(place.Base, blockId, instructionIndex, boundParams, ref mutableFromOrigin);
        }
    }

    /// <summary>
    /// 获取或创建返回值的生命周期
    /// </summary>
    private LifetimeId GetOrCreateReturnLifetime(List<int> boundParams)
    {
        // 如果只绑定一个参数，使用该参数的生命周期
        if (boundParams.Count == 1 && _paramLifetimes.TryGetValue(boundParams[0], out var lifetime))
        {
            return lifetime;
        }

        // 否则创建新的生命周期
        return AllocateLifetime();
    }

    /// <summary>
    /// 分析函数体内的生命周期关系
    /// </summary>
    private void AnalyzeLifetimeRelationships(List<LifetimeConstraint> constraints)
    {
        if (!_includeCallConstraints)
        {
            return;
        }

        foreach (var block in _function.BasicBlocks)
        {
            for (int instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                AnalyzeInstructionLifetimes(block.Instructions[instructionIndex], block.Id, instructionIndex, constraints);
            }
        }
    }

    /// <summary>
    /// 分析指令中的生命周期关系
    /// </summary>
    private void AnalyzeInstructionLifetimes(
        MirInstruction instr,
        BlockId blockId,
        int instructionIndex,
        List<LifetimeConstraint> constraints)
    {
        switch (instr)
        {
            case MirCall call:
                AnalyzeCallLifetimes(call, blockId, instructionIndex, constraints);
                break;
        }
    }

    /// <summary>
    /// 分析函数调用的生命周期
    /// </summary>
    private void AnalyzeCallLifetimes(
        MirCall call,
        BlockId blockId,
        int instructionIndex,
        List<LifetimeConstraint> constraints)
    {
        var signature = TryGetCalleeSignature(call);
        if (signature == null)
        {
            return;
        }

        var lifetimeMap = new Dictionary<LifetimeId, HashSet<LifetimeId>>();

        for (int i = 0; i < signature.ParamRequirements.Count && i < call.Arguments.Count; i++)
        {
            var requirement = signature.ParamRequirements[i];
            if (!requirement.Lifetime.IsValid)
            {
                continue;
            }

            foreach (var callerParamIndex in ResolveCallerParamIndices(call.Arguments[i], blockId, instructionIndex))
            {
                if (!_paramLifetimes.TryGetValue(callerParamIndex, out var callerLifetime))
                {
                    continue;
                }

                if (!lifetimeMap.TryGetValue(requirement.Lifetime, out var mappedLifetimes))
                {
                    mappedLifetimes = [];
                    lifetimeMap[requirement.Lifetime] = mappedLifetimes;
                }

                mappedLifetimes.Add(callerLifetime);
            }
        }

        foreach (var constraint in signature.LifetimeConstraints)
        {
            if (!lifetimeMap.TryGetValue(constraint.Sub, out var mappedSubs) ||
                !lifetimeMap.TryGetValue(constraint.Sup, out var mappedSups))
            {
                continue;
            }

            foreach (var mappedSub in mappedSubs)
            {
                foreach (var mappedSup in mappedSups)
                {
                    if (constraints.Any(c => c.Sub.Equals(mappedSub) && c.Sup.Equals(mappedSup)))
                    {
                        continue;
                    }

                    constraints.Add(new LifetimeConstraint
                    {
                        Sub = mappedSub,
                        Sup = mappedSup,
                        Span = call.Span
                    });
                }
            }
        }
    }

    private ParamBorrowMode? TryGetCallArgMode(MirCall call, int argIndex)
    {
        if (!_includeCallConstraints)
        {
            return null;
        }

        var signature = TryGetCalleeSignature(call);
        if (signature == null)
        {
            return null;
        }

        if (argIndex >= 0 && argIndex < signature.ParamRequirements.Count)
        {
            return signature.ParamRequirements[argIndex].Mode;
        }

        return null;
    }

    private LoanSignature? TryGetCalleeSignature(MirCall call)
    {
        return LoanCallAnalysis.TryResolveCalleeSignature(call, _cache, _symbolTable);
    }

    private (MirCall Call, LoanSignature Signature, BlockId BlockId, int InstructionIndex)? FindCallReturningBorrow(LocalId localId)
    {
        foreach (var block in _function.BasicBlocks)
        {
            for (int instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instr = block.Instructions[instructionIndex];
                if (instr is not MirCall call)
                {
                    continue;
                }

                if (call.Target is not MirPlace { Kind: PlaceKind.Local, Local: var targetId })
                {
                    continue;
                }

                if (!targetId.Equals(localId))
                {
                    continue;
                }

                var signature = TryGetCalleeSignature(call);
                if (signature != null && signature.ReturnsBorrow())
                {
                    return (call, signature, block.Id, instructionIndex);
                }
            }
        }

        return null;
    }

    private List<int> MapBoundParams(MirCall call, LoanSignature signature, BlockId blockId, int instructionIndex)
    {
        var bound = new HashSet<int>();

        foreach (var calleeParamIndex in signature.ReturnConstraint.BoundToParams)
        {
            if (calleeParamIndex < 0 || calleeParamIndex >= call.Arguments.Count)
            {
                continue;
            }

            foreach (var callerParamIndex in ResolveCallerParamIndices(call.Arguments[calleeParamIndex], blockId, instructionIndex))
            {
                bound.Add(callerParamIndex);
            }
        }

        return bound.OrderBy(index => index).ToList();
    }

    private void BuildLocalAliasMap()
    {
        foreach (var block in _function.BasicBlocks)
        {
            foreach (var instr in block.Instructions)
            {
                switch (instr)
                {
                    case MirMove move
                        when move.Target?.Kind == PlaceKind.Local &&
                             move.Source?.Kind == PlaceKind.Local:
                        _localAliases[move.Target.Local] = move.Source.Local;
                        break;

                    case MirCopy copy
                        when copy.Target?.Kind == PlaceKind.Local &&
                             copy.Source?.Kind == PlaceKind.Local:
                        _localAliases[copy.Target.Local] = copy.Source.Local;
                        break;
                }
            }
        }
    }

    private LocalId ResolveAlias(LocalId localId)
    {
        var current = localId;
        var visited = new HashSet<LocalId>();

        while (_localAliases.TryGetValue(current, out var next) && visited.Add(current))
        {
            current = next;
        }

        return current;
    }

    private bool TryGetCallerParamIndex(MirOperand operand, out int paramIndex)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var localId })
        {
            var resolved = ResolveAlias(localId);
            return _paramLocalIndex.TryGetValue(resolved, out paramIndex);
        }

        paramIndex = -1;
        return false;
    }

    /// <summary>
    /// 生成生命周期名称
    /// </summary>
    private string GenerateLifetimeName(LifetimeId lifetime)
    {
        // 使用字母命名生命周期
        var index = lifetime.Value - 1;
        if (index < 26)
        {
            return ((char)('a' + index)).ToString();
        }

        return $"l{lifetime.Value}";
    }

    /// <summary>
    /// 获取参数借用要求（暴露给外部使用）
    /// </summary>
    public List<ParamBorrowRequirement> ParamRequirements { get; private set; } = [];

    /// <summary>
    /// 获取返回值约束（暴露给外部使用）
    /// </summary>
    public ReturnBorrowConstraint ReturnConstraint { get; private set; } = new();

    /// <summary>
    /// 获取生命周期约束（暴露给外部使用）
    /// </summary>
    public List<LifetimeConstraint> LifetimeConstraints { get; private set; } = [];
}

internal sealed class LoanInferState
{
    private readonly Dictionary<LocalId, LoanInferOrigin> _origins;

    private LoanInferState(Dictionary<LocalId, LoanInferOrigin> origins)
    {
        _origins = origins;
    }

    public static LoanInferState Empty() => new([]);

    public static LoanInferState Merge(IEnumerable<LoanInferState> states)
    {
        var merged = Empty();
        foreach (var state in states)
        {
            foreach (var (localId, origin) in state._origins)
            {
                if (!merged._origins.TryGetValue(localId, out var existing))
                {
                    merged._origins[localId] = origin.Clone();
                    continue;
                }

                existing.BoundParams.UnionWith(origin.BoundParams);
                existing.IsMutable = existing.IsMutable || origin.IsMutable;
            }
        }

        return merged;
    }

    public LoanInferState Clone()
    {
        return new LoanInferState(_origins.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Clone()));
    }

    public bool SemanticallyEquals(LoanInferState other)
    {
        if (_origins.Count != other._origins.Count)
        {
            return false;
        }

        foreach (var (localId, origin) in _origins)
        {
            if (!other._origins.TryGetValue(localId, out var otherOrigin))
            {
                return false;
            }

            if (origin.IsMutable != otherOrigin.IsMutable)
            {
                return false;
            }

            if (!origin.BoundParams.SetEquals(otherOrigin.BoundParams))
            {
                return false;
            }
        }

        return true;
    }

    public bool TryGet(LocalId localId, out LoanInferOrigin origin)
    {
        return _origins.TryGetValue(localId, out origin!);
    }

    public void SetOrigin(LocalId localId, IEnumerable<int> boundParams, bool isMutable)
    {
        var normalized = boundParams.Distinct().OrderBy(index => index).ToHashSet();
        if (normalized.Count == 0)
        {
            _origins.Remove(localId);
            return;
        }

        _origins[localId] = new LoanInferOrigin(normalized, isMutable);
    }

    public void Clear(LocalId localId)
    {
        _origins.Remove(localId);
    }
}

internal sealed class LoanInferOrigin
{
    public HashSet<int> BoundParams { get; }
    public bool IsMutable { get; set; }

    public LoanInferOrigin(HashSet<int> boundParams, bool isMutable)
    {
        BoundParams = boundParams;
        IsMutable = isMutable;
    }

    public LoanInferOrigin Clone() => new([.. BoundParams], IsMutable);
}
