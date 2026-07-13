using Eidosc.Mir.Optimize;
using Eidosc.Mir;

using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Borrow;

/// <summary>
/// Drop Insertion Pass — 在变量死亡点自动插入 MirDrop 指令。
///
/// Perceus 引用计数模型要求编译器在引用不再需要时显式释放。
/// MirBuilder 生成 MirCopy（incref）和 MirMove（所有权转移），
/// 但不生成 MirDrop。此 pass 利用活跃性分析在变量最后一次使用后
/// 插入 MirDrop（对应运行时的 eidos_decref）。
///
/// 算法：
/// 1. 使用 LivenessAnalyzer 计算每个块的 LiveOut 集合
/// 2. 反向遍历每个块的指令，维护逐指令的活跃集合
/// 3. 当一个 RC 管理类型的局部变量在指令后变为不活跃时，
///    在该指令后插入 MirDrop
/// </summary>
public sealed class DropInsertionPass : IMirOptimizationPass
{
    public string Name => "DropInsertion";

    private readonly HashSet<int> _nonRcBaseTypeIds =
    [
        BaseTypes.IntId,
        BaseTypes.FloatId,
        BaseTypes.BoolId,
        BaseTypes.CharId,
        BaseTypes.UnitId
    ];

    public MirModule Run(MirModule module)
    {
        var optimizedFunctions = new List<MirFunc>();

        foreach (var func in module.Functions)
        {
            optimizedFunctions.Add(ProcessFunction(func, module.TypeDescriptors, module.DynamicTypeKeys));
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

    private MirFunc ProcessFunction(
        MirFunc func,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys)
    {
        // 外部 FFI 函数无需 drop 插入，直接传递
        if (func.IsExternal)
        {
            return func;
        }

        // 分析活跃性
        var usageAnalyzer = new VariableUsageAnalyzer(func);
        usageAnalyzer.Analyze();

        var livenessAnalyzer = new LivenessAnalyzer(func, usageAnalyzer);
        livenessAnalyzer.Analyze();

        // 收集需要 RC 管理的局部变量（排除基本类型）
        var rcLocals = new HashSet<LocalId>();
        var localTypes = new Dictionary<LocalId, TypeId>();
        foreach (var local in func.Locals)
        {
            localTypes[local.Id] = local.TypeId;
            if (local.TypeId.IsValid &&
                !_nonRcBaseTypeIds.Contains(local.TypeId.Value) &&
                !MirGenericAnalysis.ContainsOpenTypeVariable(local.TypeId, typeDescriptors, dynamicTypeKeys))

            {
                rcLocals.Add(local.Id);
            }
        }

        // 处理每个基本块
        var newBlocks = new List<MirBasicBlock>();
        foreach (var block in func.BasicBlocks)
        {
            newBlocks.Add(ProcessBlock(block, livenessAnalyzer, rcLocals, localTypes));
        }

        return new MirFunc
        {
            Name = func.Name,
            SourceName = func.SourceName,
            Locals = func.Locals,
            BasicBlocks = newBlocks,
            EntryBlockId = func.EntryBlockId,
            ReturnType = func.ReturnType,
            GenericParameterCount = func.GenericParameterCount,
            GenericParameters = func.GenericParameters.ToList(),
            GenericTypeParameterIds = func.GenericTypeParameterIds.ToList(),
            Span = func.Span,
            SymbolId = func.SymbolId,
            FunctionId = func.FunctionId,
            TraitInvokeHelper = func.TraitInvokeHelper,
            TraitInvokeHelperTraitId = func.TraitInvokeHelperTraitId,
            IsEntry = func.IsEntry,
            IsExternal = func.IsExternal,
            ExternalSymbolName = func.ExternalSymbolName,
            ExternalLibrary = func.ExternalLibrary
        };
    }

    private MirBasicBlock ProcessBlock(
        MirBasicBlock block,
        LivenessAnalyzer livenessAnalyzer,
        HashSet<LocalId> rcLocals,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        // 获取块末尾的 LiveOut 作为初始活跃集合
        if (!livenessAnalyzer.TryGetLiveOutSet(block.Id, out var liveOut))
        {
            return block;
        }

        var live = new HashSet<LocalId>(liveOut);
        var newInstructions = new List<MirInstruction>();

        // 反向遍历指令，计算活跃集合并插入 drop
        for (int i = block.Instructions.Count - 1; i >= 0; i--)
        {
            var instr = block.Instructions[i];

            // live 现在代表指令 i 之后的活跃集合
            // 检查哪些 RC 变量刚刚变为不活跃（在此指令之前活跃，之后不活跃）
            var dropsToInsert = ComputeDropsForInstruction(instr, live, rcLocals, localTypes);

            // 反向构造指令列表；这里先写入 drop，最终 Reverse 后会落在原始指令之后。
            foreach (var drop in dropsToInsert)
            {
                newInstructions.Add(drop);
            }

            newInstructions.Add(instr);

            // 更新活跃集合：liveBefore = (liveAfter - def) ∪ use
            UpdateLivenessForInstruction(instr, live);
        }

        // 反转回正向顺序
        newInstructions.Reverse();

        return new MirBasicBlock
        {
            Id = block.Id,
            Instructions = newInstructions,
            Terminator = block.Terminator,
            Span = block.Span,
            IsEntry = block.IsEntry
        };
    }

    /// <summary>
    /// 计算在指令后需要插入的 MirDrop。
    /// 一个变量在此指令后变为不活跃，意味着它的最后一次使用就是此指令。
    /// 但如果此指令本身已经消费了该变量（MirMove、MirDrop），
    /// 则不需要额外 drop——所有权已转移。
    ///
    /// 仅在变量被此指令的 use 引入但在此指令之后不再活跃时插入 drop。
    /// </summary>
    private List<MirInstruction> ComputeDropsForInstruction(
        MirInstruction instr,
        HashSet<LocalId> liveAfter,
        HashSet<LocalId> rcLocals,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        var drops = new List<MirInstruction>();

        // 收集此指令使用的 RC 变量
        var usedRcVars = new HashSet<LocalId>();
        CollectUsedRcVariables(instr, usedRcVars, rcLocals);

        foreach (var varId in usedRcVars)
        {
            // 变量在此指令之后不活跃 → 它的最后一次使用就是此指令
            if (!liveAfter.Contains(varId))
            {
                // 但如果此指令已经通过 MirMove 转移了所有权，或已经是 MirDrop，
                // 不需要额外 drop
                if (IsOwnershipTransfer(instr, varId))
                {
                    continue;
                }

                drops.Add(new MirDrop
                {
                    Value = new MirPlace
                    {
                        Kind = PlaceKind.Local,
                        Local = varId,
                        TypeId = GetTypeIdForLocal(instr, varId, localTypes)
                    },
                    Span = GetSpanForInstruction(instr)
                });
            }
        }

        return drops;
    }

    /// <summary>
    /// 判断指令是否已转移变量所有权（不需要额外 drop）
    /// </summary>
    private static bool IsOwnershipTransfer(MirInstruction instr, LocalId varId)
    {
        if (instr is MirCall { IsTailCall: true })
        {
            return true;
        }

        // MirMove 转移所有权
        if (instr is MirMove move)
        {
            if (move.Source is MirPlace { Kind: PlaceKind.Local, Local: var localId } && localId.Equals(varId))
            {
                return true;
            }
            if (MirLocalTransferAnalysis.TryGetBinding(move, out var binding) && binding.Source.Equals(varId))
            {
                return true;
            }
        }

        // MirDrop 已经是 drop
        if (instr is MirDrop)
        {
            return true;
        }

        return false;
    }

    private void CollectUsedRcVariables(
        MirInstruction instr,
        HashSet<LocalId> result,
        HashSet<LocalId> rcLocals)
    {
        var allUsed = new HashSet<LocalId>();
        AddUsedVariables(instr, allUsed);

        foreach (var varId in allUsed)
        {
            if (rcLocals.Contains(varId))
            {
                result.Add(varId);
            }
        }
    }

    private void AddUsedVariables(MirInstruction instr, HashSet<LocalId> result)
    {
        switch (instr)
        {
            case MirAssign assign:
                CollectOperand(assign.Source, result);
                break;

            case MirCall call:
                CollectOperand(call.Function, result);
                foreach (var arg in call.Arguments)
                {
                    CollectOperand(arg, result);
                }
                break;

            case MirBinOp binOp:
                CollectOperand(binOp.Left, result);
                CollectOperand(binOp.Right, result);
                break;

            case MirUnaryOp unaryOp:
                CollectOperand(unaryOp.Operand, result);
                break;

            case MirLoad load:
                if (MirLocalTransferAnalysis.TryGetBinding(load, out var loadBinding))
                {
                    result.Add(loadBinding.Source);
                }
                else
                {
                    CollectOperand(load.Source, result);
                }
                break;

            case MirStore store:
                CollectOperand(store.Value, result);
                if (store.Target?.Kind == PlaceKind.Local)
                {
                    result.Add(store.Target.Local);
                }
                break;

            case MirDrop drop:
                CollectOperand(drop.Value, result);
                break;

            case MirCopy copy:
                if (MirLocalTransferAnalysis.TryGetBinding(copy, out var copyBinding))
                {
                    result.Add(copyBinding.Source);
                }
                else if (copy.Source?.Kind == PlaceKind.Local)
                {
                    result.Add(copy.Source.Local);
                }
                break;

            case MirMove move:
                if (MirLocalTransferAnalysis.TryGetBinding(move, out var moveBinding))
                {
                    result.Add(moveBinding.Source);
                }
                else if (move.Source?.Kind == PlaceKind.Local)
                {
                    result.Add(move.Source.Local);
                }
                break;

        }
    }

    private static void CollectOperand(MirOperand? operand, HashSet<LocalId> result)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var localId })
        {
            result.Add(localId);
        }
    }

    private static void UpdateLivenessForInstruction(MirInstruction instr, HashSet<LocalId> live)
    {
        // 移除 def
        var definedVar = GetDefinedVariable(instr);
        if (definedVar != null)
        {
            live.Remove(definedVar.Value);
        }

        // 添加 use
        switch (instr)
        {
            case MirAssign assign:
                CollectPlace(assign.Source, live);
                break;
            case MirCall call:
                CollectOperand(call.Function, live);
                foreach (var arg in call.Arguments) CollectOperand(arg, live);
                break;
            case MirBinOp binOp:
                CollectOperand(binOp.Left, live);
                CollectOperand(binOp.Right, live);
                break;
            case MirUnaryOp unaryOp:
                CollectOperand(unaryOp.Operand, live);
                break;
            case MirLoad load:
                if (MirLocalTransferAnalysis.TryGetBinding(load, out var lb))
                    live.Add(lb.Source);
                else
                    CollectOperand(load.Source, live);
                break;
            case MirStore store:
                CollectOperand(store.Value, live);
                if (store.Target?.Kind == PlaceKind.Local) live.Add(store.Target.Local);
                break;
            case MirDrop drop:
                CollectOperand(drop.Value, live);
                break;
            case MirCopy copy:
                if (MirLocalTransferAnalysis.TryGetBinding(copy, out var cb))
                    live.Add(cb.Source);
                else if (copy.Source?.Kind == PlaceKind.Local) live.Add(copy.Source.Local);
                break;
            case MirMove move:
                if (MirLocalTransferAnalysis.TryGetBinding(move, out var mb))
                    live.Add(mb.Source);
                else if (move.Source?.Kind == PlaceKind.Local) live.Add(move.Source.Local);
                break;
        }
    }

    private static LocalId? GetDefinedVariable(MirInstruction instr)
    {
        return instr switch
        {
            MirAssign assign => assign.Target is MirPlace { Kind: PlaceKind.Local, Local: var a } ? a : null,
            MirCall call => call.Target is MirPlace { Kind: PlaceKind.Local, Local: var c } ? c : null,
            MirBinOp bin => bin.Target is MirPlace { Kind: PlaceKind.Local, Local: var b } ? b : null,
            MirUnaryOp unary => unary.Target is MirPlace { Kind: PlaceKind.Local, Local: var u } ? u : null,
            MirLoad load when MirLocalTransferAnalysis.TryGetBinding(load, out var lb) => lb.Target,
            MirLoad load => load.Target is MirPlace { Kind: PlaceKind.Local, Local: var l } ? l : null,
            MirCopy copy when MirLocalTransferAnalysis.TryGetBinding(copy, out var cb) => cb.Target,
            MirCopy copy => copy.Target is MirPlace { Kind: PlaceKind.Local, Local: var cp } ? cp : null,
            MirMove move when MirLocalTransferAnalysis.TryGetBinding(move, out var mb) => mb.Target,
            MirMove move => move.Target is MirPlace { Kind: PlaceKind.Local, Local: var mv } ? mv : null,
            MirAlloc alloc => alloc.Target is MirPlace { Kind: PlaceKind.Local, Local: var al } ? al : null,
            _ => null
        };
    }

    private static void CollectPlace(MirOperand? operand, HashSet<LocalId> result)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var id })
        {
            result.Add(id);
        }
    }

    private static TypeId GetTypeIdForLocal(
        MirInstruction instr,
        LocalId varId,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        if (localTypes.TryGetValue(varId, out var localType))
        {
            return localType;
        }

        // 尝试从指令的操作数中获取类型信息
        switch (instr)
        {
            case MirCopy copy:
                if (copy.Source is MirPlace { Kind: PlaceKind.Local, Local: var srcLocal, TypeId: var srcType } && srcLocal.Equals(varId))
                    return srcType;
                break;
            case MirMove move:
                if (move.Source is MirPlace { Kind: PlaceKind.Local, Local: var mSrc, TypeId: var mType } && mSrc.Equals(varId))
                    return mType;
                break;
        }

        // 回退到指令的源类型
        return TypeId.None;
    }

    private static SourceSpan GetSpanForInstruction(MirInstruction instr)
    {
        return instr.Span;
    }
}
