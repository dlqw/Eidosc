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
        BaseTypes.UnitId,
        BaseTypes.RawPtrId,
        BaseTypes.CfnId,
        BaseTypes.NeverId
    ];

    public MirModule Run(MirModule module)
    {
        var optimizedFunctions = new List<MirFunc>();
        var scalarTagTypeIds = PayloadlessAdtRepresentationAnalysis.Analyze(module);

        foreach (var func in module.Functions)
        {
            optimizedFunctions.Add(ProcessFunction(
                func,
                module.TypeDescriptors,
                module.DynamicTypeKeys,
                scalarTagTypeIds));
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
            CopyLikeTypeIds = new HashSet<int>(module.CopyLikeTypeIds),
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
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        IReadOnlySet<int> scalarTagTypeIds)
    {
        // Declarations have no CFG to transform. This covers FFI declarations,
        // compiler intrinsics and other bodyless functions without relying on a
        // particular declaration flag being present.
        if (func.IsExternal || func.BasicBlocks.Count == 0 || func.EntryBlock is null)
        {
            return func;
        }

        // 收集需要 RC 管理的局部变量（排除基本类型）
        var rcLocals = new HashSet<LocalId>();
        var localTypes = new Dictionary<LocalId, TypeId>();
        foreach (var local in func.Locals)
        {
            localTypes[local.Id] = local.TypeId;
            if (IsManagedRcType(local.TypeId, typeDescriptors, dynamicTypeKeys, scalarTagTypeIds))

            {
                rcLocals.Add(local.Id);
            }
        }

        var usageAnalyzer = new VariableUsageAnalyzer(func);
        usageAnalyzer.Analyze();
        var livenessAnalyzer = new LivenessAnalyzer(func, usageAnalyzer);
        livenessAnalyzer.Analyze();
        var earlyDroppableLocals = CollectEarlyDroppableLocals(func, rcLocals);
        var borrowAliasValueLocals = func.Locals
            .Where(local => rcLocals.Contains(local.Id) ||
                            typeDescriptors.TryGetValue(local.TypeId.Value, out var descriptor) &&
                            descriptor is TypeDescriptor.Ref or TypeDescriptor.MutRef)
            .Select(static local => local.Id)
            .ToHashSet();
        var borrowAliasesByBase = CollectBorrowAliasesByBase(func, borrowAliasValueLocals);
        var ownershipAliasesByBase = CollectOwnershipAliasesByBase(func);
        var referenceTypeIds = CollectReferenceTypeIds(typeDescriptors, dynamicTypeKeys);
        var ownedAtBlockEntry = AnalyzeOwnedLocals(
            func,
            rcLocals,
            livenessAnalyzer,
            earlyDroppableLocals,
            borrowAliasesByBase,
            ownershipAliasesByBase,
            referenceTypeIds);

        // 处理每个基本块
        var newBlocks = new List<MirBasicBlock>();
        foreach (var block in func.BasicBlocks)
        {
            newBlocks.Add(ProcessBlock(
                block,
                rcLocals,
                localTypes,
                livenessAnalyzer,
                earlyDroppableLocals,
                borrowAliasesByBase,
                ownershipAliasesByBase,
                referenceTypeIds,
                ownedAtBlockEntry.GetValueOrDefault(block.Id, [])));
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
            IsRuntimeWordAbi = func.IsRuntimeWordAbi,
            Span = func.Span,
            SymbolId = func.SymbolId,
            FunctionId = func.FunctionId,
            OwnershipContract = func.OwnershipContract,
            TraitInvokeHelper = func.TraitInvokeHelper,
            TraitInvokeHelperTraitId = func.TraitInvokeHelperTraitId,
            IsEntry = func.IsEntry,
            IsExternal = func.IsExternal,
            ExternalSymbolName = func.ExternalSymbolName,
            ExternalLibrary = func.ExternalLibrary,
            IntrinsicName = func.IntrinsicName,
            BuiltinIntrinsicRole = func.BuiltinIntrinsicRole
        };
    }

    private MirBasicBlock ProcessBlock(
        MirBasicBlock block,
        HashSet<LocalId> rcLocals,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        LivenessAnalyzer livenessAnalyzer,
        IReadOnlySet<LocalId> earlyDroppableLocals,
        IReadOnlyDictionary<LocalId, HashSet<LocalId>> borrowAliasesByBase,
        IReadOnlyDictionary<LocalId, HashSet<LocalId>> ownershipAliasesByBase,
        IReadOnlySet<int> referenceTypeIds,
        IReadOnlySet<LocalId> ownedAtEntry)
    {
        var owned = new HashSet<LocalId>(ownedAtEntry);
        var newInstructions = new List<MirInstruction>(block.Instructions.Count);
        var liveAfterInstruction = ComputeLiveAfterInstructions(block, livenessAnalyzer);
        for (int i = 0; i < block.Instructions.Count; i++)
        {
            MirInstruction instruction = block.Instructions[i];
            if (instruction is MirCall call)
            {
                instruction = AnnotateReferenceArgumentsAsBorrowed(call, referenceTypeIds);
            }

            if (GetDefinedVariable(instruction) is { } overwritten &&
                !DefinitionReusesSameLocal(instruction, overwritten) &&
                owned.Remove(overwritten))
            {
                newInstructions.Add(CreateDrop(overwritten, localTypes, instruction.Span));
            }

            if (instruction is MirCall { IsTailCall: true } tailCall)
            {
                var transferredArguments = tailCall.Arguments
                    .Select((argument, index) => (argument, index))
                    .Where(pair => !tailCall.BorrowedArgumentIndices.Contains(pair.index))
                    .Select(static pair => pair.argument)
                    .OfType<MirPlace>()
                    .Where(static argument => argument.Kind == PlaceKind.Local)
                    .Select(static argument => argument.Local)
                    .ToHashSet();
                var requiresCleanupAfterCall = owned.Any(local => !transferredArguments.Contains(local));
                if (requiresCleanupAfterCall ||
                    tailCall.Function is MirPlace { Kind: PlaceKind.Local, Local: var callable } &&
                    owned.Contains(callable))
                {
                    instruction = tailCall with { IsTailCall = false };
                }
            }

            newInstructions.Add(instruction);
            ApplyOwnershipTransfer(
                instruction,
                owned,
                rcLocals,
                ownershipAliasesByBase);

            var dropCandidates = owned
                .Where(local => earlyDroppableLocals.Contains(local) &&
                                !HasLiveBorrowAlias(local, liveAfterInstruction[i], borrowAliasesByBase) &&
                                !liveAfterInstruction[i].Contains(local))
                .OrderBy(static local => local.Value)
                .ToArray();
            foreach (var local in dropCandidates)
            {
                newInstructions.Add(CreateDrop(local, localTypes, instruction.Span));
                owned.Remove(local);
            }
        }

        if (block.Terminator is MirReturn { Value: { } returnValue } &&
            returnValue is MirPlace { Kind: PlaceKind.Local, Local: var returnedLocal })
        {
            ConsumeOwnedLocalOrAlias(returnedLocal, owned, borrowAliasesByBase);
        }

        if (block.Terminator is MirReturn or MirUnreachable)
        {
            foreach (var local in owned.OrderBy(static local => local.Value))
            {
                newInstructions.Add(CreateDrop(local, localTypes, block.Terminator?.Span ?? block.Span));
            }
        }
        else if (livenessAnalyzer.TryGetLiveOutSet(block.Id, out var liveOut))
        {
            foreach (var local in owned
                         .Where(local => earlyDroppableLocals.Contains(local) &&
                                         !HasLiveBorrowAlias(local, liveOut, borrowAliasesByBase) &&
                                         !liveOut.Contains(local))
                         .OrderBy(static local => local.Value)
                         .ToArray())
            {
                newInstructions.Add(CreateDrop(local, localTypes, block.Terminator?.Span ?? block.Span));
                owned.Remove(local);
            }
        }

        return new MirBasicBlock
        {
            Id = block.Id,
            Instructions = newInstructions,
            Terminator = block.Terminator,
            Span = block.Span,
            IsEntry = block.IsEntry
        };
    }

    private static HashSet<LocalId> CollectEarlyDroppableLocals(
        MirFunc function,
        IReadOnlySet<LocalId> rcLocals)
    {
        _ = function;
        // The forward ownership state is the proof that a local currently owns
        // a value. Restricting early drops to direct allocation targets loses
        // ownership after MirMove/MirAssign (notably in TCO-generated loops),
        // so every managed local is eligible once it is present in that state.
        return [.. rcLocals];
    }

    private static Dictionary<LocalId, HashSet<LocalId>> CollectBorrowAliasesByBase(
        MirFunc function,
        IReadOnlySet<LocalId> borrowAliasValueLocals)
    {
        var dependenciesByAlias = new Dictionary<LocalId, HashSet<LocalId>>();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var instruction in function.BasicBlocks.SelectMany(static block => block.Instructions))
            {
                LocalId target;
                MirOperand? source;
                var isProjectedBorrow = false;
                switch (instruction)
                {
                    case MirLoad
                    {
                        CreatesBorrowAlias: true,
                        Source: MirPlace { Kind: not PlaceKind.Local } loadSource,
                        Target: MirPlace { Kind: PlaceKind.Local, Local: var loadTarget }
                    }:
                        target = loadTarget;
                        source = loadSource;
                        isProjectedBorrow = true;
                        break;
                    case MirLoad
                    {
                        Source: MirPlace { Kind: PlaceKind.Local } loadSource,
                        Target: MirPlace { Kind: PlaceKind.Local, Local: var loadTarget }
                    }:
                        target = loadTarget;
                        source = loadSource;
                        isProjectedBorrow = true;
                        break;
                    case MirMove
                    {
                        Source: MirPlace { Kind: PlaceKind.Local } moveSource,
                        Target: MirPlace { Kind: PlaceKind.Local, Local: var moveTarget }
                    }:
                        target = moveTarget;
                        source = moveSource;
                        break;
                    case MirAssign
                    {
                        Source: MirPlace { Kind: PlaceKind.Local } assignSource,
                        Target: MirPlace { Kind: PlaceKind.Local, Local: var assignTarget }
                    }:
                        target = assignTarget;
                        source = assignSource;
                        break;
                    default:
                        continue;
                }

                if (!borrowAliasValueLocals.Contains(target))
                {
                    continue;
                }

                var dependencies = dependenciesByAlias.GetValueOrDefault(target);
                if (dependencies == null)
                {
                    dependencies = [];
                    dependenciesByAlias[target] = dependencies;
                }

                var previousCount = dependencies.Count;
                CollectPlaceLocals(source, local =>
                {
                    if (isProjectedBorrow)
                    {
                        dependencies.Add(local);
                    }

                    if (dependenciesByAlias.TryGetValue(local, out var inherited))
                    {
                        dependencies.UnionWith(inherited);
                    }
                });
                changed |= dependencies.Count != previousCount;
            }
        }

        var aliasesByBase = new Dictionary<LocalId, HashSet<LocalId>>();
        foreach (var (alias, dependencies) in dependenciesByAlias)
        {
            foreach (var dependency in dependencies.Where(dependency => dependency != alias))
            {
                if (!aliasesByBase.TryGetValue(dependency, out var aliases))
                {
                    aliases = [];
                    aliasesByBase[dependency] = aliases;
                }

                aliases.Add(alias);
            }
        }

        return aliasesByBase;
    }

    private static Dictionary<LocalId, HashSet<LocalId>> CollectOwnershipAliasesByBase(
        MirFunc function)
    {
        var ownersByAlias = new Dictionary<LocalId, HashSet<LocalId>>();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var instruction in function.BasicBlocks.SelectMany(static block => block.Instructions))
            {
                LocalId target;
                LocalId source;
                var introducesIdentityAlias = false;
                switch (instruction)
                {
                    case MirLoad
                    {
                        Target: { Kind: PlaceKind.Local, Local: var loadTarget },
                        Source: MirPlace { Kind: PlaceKind.Local, Local: var loadSource }
                    }:
                        target = loadTarget;
                        source = loadSource;
                        introducesIdentityAlias = true;
                        break;
                    case MirMove
                    {
                        Target: { Kind: PlaceKind.Local, Local: var moveTarget },
                        Source: { Kind: PlaceKind.Local, Local: var moveSource }
                    }:
                        target = moveTarget;
                        source = moveSource;
                        break;
                    case MirAssign
                    {
                        Target: { Kind: PlaceKind.Local, Local: var assignTarget },
                        Source: MirPlace { Kind: PlaceKind.Local, Local: var assignSource }
                    }:
                        target = assignTarget;
                        source = assignSource;
                        break;
                    default:
                        continue;
                }

                if (!introducesIdentityAlias && !ownersByAlias.ContainsKey(source))
                {
                    continue;
                }

                if (!ownersByAlias.TryGetValue(target, out var owners))
                {
                    owners = [];
                    ownersByAlias[target] = owners;
                }

                var previousCount = owners.Count;
                if (introducesIdentityAlias)
                {
                    owners.Add(source);
                }

                if (ownersByAlias.TryGetValue(source, out var inheritedOwners))
                {
                    owners.UnionWith(inheritedOwners);
                }

                changed |= owners.Count != previousCount;
            }
        }

        var aliasesByOwner = new Dictionary<LocalId, HashSet<LocalId>>();
        foreach (var (alias, owners) in ownersByAlias)
        {
            foreach (var owner in owners.Where(owner => owner != alias))
            {
                if (!aliasesByOwner.TryGetValue(owner, out var aliases))
                {
                    aliases = [];
                    aliasesByOwner[owner] = aliases;
                }

                aliases.Add(alias);
            }
        }

        return aliasesByOwner;
    }

    private static bool HasLiveBorrowAlias(
        LocalId local,
        IReadOnlySet<LocalId> live,
        IReadOnlyDictionary<LocalId, HashSet<LocalId>> borrowAliasesByBase) =>
        borrowAliasesByBase.TryGetValue(local, out var aliases) && aliases.Overlaps(live);

    private static void CollectPlaceLocals(MirOperand? operand, Action<LocalId> collect)
    {
        if (operand is not MirPlace place)
        {
            return;
        }

        if (place.Kind == PlaceKind.Local)
        {
            collect(place.Local);
        }

        CollectPlaceLocals(place.Base, collect);
        CollectPlaceLocals(place.Index, collect);
    }

    private static IReadOnlyList<HashSet<LocalId>> ComputeLiveAfterInstructions(
        MirBasicBlock block,
        LivenessAnalyzer livenessAnalyzer)
    {
        var result = new HashSet<LocalId>[block.Instructions.Count];
        var live = livenessAnalyzer.TryGetLiveOutSet(block.Id, out var liveOut)
            ? new HashSet<LocalId>(liveOut)
            : [];
        AddTerminatorUses(block.Terminator, live);

        for (int i = block.Instructions.Count - 1; i >= 0; i--)
        {
            result[i] = new HashSet<LocalId>(live);
            UpdateLivenessForInstruction(block.Instructions[i], live);
        }

        return result;
    }

    private static bool DefinitionReusesSameLocal(MirInstruction instruction, LocalId target) =>
        instruction switch
        {
            MirAssign { Source: MirPlace { Kind: PlaceKind.Local, Local: var source } } => source == target,
            MirCaseInject { Operand: MirPlace { Kind: PlaceKind.Local, Local: var source } } => source == target,
            MirCall call => call.Arguments
                .Select((argument, index) => (argument, index))
                .Any(pair => !call.BorrowedArgumentIndices.Contains(pair.index) &&
                             pair.argument is MirPlace { Kind: PlaceKind.Local, Local: var source } &&
                             source == target),
            MirLoad { Source: MirPlace { Kind: PlaceKind.Local, Local: var source } } => source == target,
            MirCopy { Source: { Kind: PlaceKind.Local, Local: var source } } => source == target,
            MirMove { Source: { Kind: PlaceKind.Local, Local: var source } } => source == target,
            _ => false
        };

    private bool IsManagedRcType(
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        IReadOnlySet<int> scalarTagTypeIds)
    {
        if (!typeId.IsValid ||
            _nonRcBaseTypeIds.Contains(typeId.Value) ||
            scalarTagTypeIds.Contains(typeId.Value) ||
            MirGenericAnalysis.ContainsOpenTypeVariable(typeId, typeDescriptors, dynamicTypeKeys))
        {
            return false;
        }

        return !typeDescriptors.TryGetValue(typeId.Value, out var descriptor) ||
               descriptor is not TypeDescriptor.Ref and not TypeDescriptor.MutRef;
    }

    private static Dictionary<BlockId, HashSet<LocalId>> AnalyzeOwnedLocals(
        MirFunc function,
        HashSet<LocalId> rcLocals,
        LivenessAnalyzer livenessAnalyzer,
        IReadOnlySet<LocalId> earlyDroppableLocals,
        IReadOnlyDictionary<LocalId, HashSet<LocalId>> borrowAliasesByBase,
        IReadOnlyDictionary<LocalId, HashSet<LocalId>> ownershipAliasesByBase,
        IReadOnlySet<int> referenceTypeIds)
    {
        var cfg = new ControlFlowGraph(function);
        var entryOwnership = function.Locals
            .Where(local => local.IsParameter && rcLocals.Contains(local.Id))
            .Select(static local => local.Id)
            .ToHashSet();
        var ownedIn = function.BasicBlocks.ToDictionary(
            static block => block.Id,
            _ => new HashSet<LocalId>(rcLocals));
        var ownedOut = function.BasicBlocks.ToDictionary(
            static block => block.Id,
            _ => new HashSet<LocalId>(rcLocals));

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in function.BasicBlocks)
            {
                var incoming = new List<IReadOnlySet<LocalId>>();
                if (block.Id == function.EntryBlockId)
                {
                    incoming.Add(entryOwnership);
                }

                foreach (var predecessor in cfg.GetPredecessors(block.Id))
                {
                    if (ownedOut.TryGetValue(predecessor, out var predecessorOut))
                    {
                        incoming.Add(predecessorOut);
                    }
                }

                var nextIn = incoming.Count == 0
                    ? []
                    : new HashSet<LocalId>(incoming[0]);
                for (int i = 1; i < incoming.Count; i++)
                {
                    nextIn.IntersectWith(incoming[i]);
                }

                var nextOut = new HashSet<LocalId>(nextIn);
                var liveAfterInstruction = ComputeLiveAfterInstructions(block, livenessAnalyzer);
                for (int i = 0; i < block.Instructions.Count; i++)
                {
                    var instruction = block.Instructions[i] is MirCall call
                        ? AnnotateReferenceArgumentsAsBorrowed(call, referenceTypeIds)
                        : block.Instructions[i];
                    if (GetDefinedVariable(instruction) is { } overwritten &&
                        !DefinitionReusesSameLocal(instruction, overwritten))
                    {
                        nextOut.Remove(overwritten);
                    }

                    ApplyOwnershipTransfer(
                        instruction,
                        nextOut,
                        rcLocals,
                        ownershipAliasesByBase);
                    nextOut.RemoveWhere(local =>
                        earlyDroppableLocals.Contains(local) &&
                        !HasLiveBorrowAlias(local, liveAfterInstruction[i], borrowAliasesByBase) &&
                        !liveAfterInstruction[i].Contains(local));
                }

                if (block.Terminator is MirReturn { Value: MirPlace { Kind: PlaceKind.Local, Local: var returned } })
                {
                    ConsumeOwnedLocalOrAlias(returned, nextOut, ownershipAliasesByBase);
                }
                else if (livenessAnalyzer.TryGetLiveOutSet(block.Id, out var liveOut))
                {
                    nextOut.RemoveWhere(local =>
                        earlyDroppableLocals.Contains(local) &&
                        !HasLiveBorrowAlias(local, liveOut, borrowAliasesByBase) &&
                        !liveOut.Contains(local));
                }

                if (!ownedIn[block.Id].SetEquals(nextIn))
                {
                    ownedIn[block.Id] = nextIn;
                    changed = true;
                }

                if (!ownedOut[block.Id].SetEquals(nextOut))
                {
                    ownedOut[block.Id] = nextOut;
                    changed = true;
                }
            }
        }

        return ownedIn;
    }

    private static HashSet<int> CollectReferenceTypeIds(
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys)
    {
        var result = typeDescriptors
            .Where(static pair => pair.Value is TypeDescriptor.Ref or TypeDescriptor.MutRef)
            .Select(static pair => pair.Key)
            .ToHashSet();

        foreach (var (typeId, typeKey) in dynamicTypeKeys)
        {
            if (!result.Contains(typeId) &&
                TypeKeyParsing.TryParseTypeDescriptor(typeKey, out var descriptor) &&
                descriptor is TypeDescriptor.Ref or TypeDescriptor.MutRef)
            {
                result.Add(typeId);
            }
        }

        return result;
    }

    private static MirCall AnnotateReferenceArgumentsAsBorrowed(
        MirCall call,
        IReadOnlySet<int> referenceTypeIds)
    {
        HashSet<int>? borrowed = null;
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            if (!referenceTypeIds.Contains(call.Arguments[index].TypeId.Value) ||
                call.BorrowedArgumentIndices.Contains(index))
            {
                continue;
            }

            borrowed ??= new HashSet<int>(call.BorrowedArgumentIndices);
            borrowed.Add(index);
        }

        return borrowed == null
            ? call
            : call with { BorrowedArgumentIndices = borrowed };
    }

    private static void ApplyOwnershipTransfer(
        MirInstruction instruction,
        HashSet<LocalId> owned,
        IReadOnlySet<LocalId> rcLocals,
        IReadOnlyDictionary<LocalId, HashSet<LocalId>> ownershipAliasesByBase)
    {
        void Consume(MirOperand? operand, HashSet<LocalId> state)
        {
            if (operand is MirPlace { Kind: PlaceKind.Local, Local: var local })
            {
                ConsumeOwnedLocalOrAlias(local, state, ownershipAliasesByBase);
            }
        }

        bool Transfer(MirOperand? operand, HashSet<LocalId> state)
        {
            return operand is MirPlace { Kind: PlaceKind.Local, Local: var local } &&
                   ConsumeOwnedLocalOrAlias(local, state, ownershipAliasesByBase);
        }

        static void Define(MirOperand? operand, HashSet<LocalId> state, IReadOnlySet<LocalId> managed)
        {
            if (operand is MirPlace { Kind: PlaceKind.Local, Local: var local } && managed.Contains(local))
            {
                state.Add(local);
            }
        }

        switch (instruction)
        {
            case MirAssign assign:
                if (assign.Source is MirPlace { Kind: PlaceKind.Local })
                {
                    if (Transfer(assign.Source, owned))
                    {
                        Define(assign.Target, owned, rcLocals);
                    }
                }
                else
                {
                    Define(assign.Target, owned, rcLocals);
                }
                break;
            case MirCaseInject injection:
                if (Transfer(injection.Operand, owned))
                {
                    Define(injection.Target, owned, rcLocals);
                }
                break;
            case MirCall call:
                for (var index = 0; index < call.Arguments.Count; index++)
                {
                    var argument = call.Arguments[index];
                    if (!call.BorrowedArgumentIndices.Contains(index))
                    {
                        Consume(argument, owned);
                    }
                }
                Define(call.Target, owned, rcLocals);
                break;
            case MirLoad { Source: MirPlace { Kind: PlaceKind.Local } }:
                // Local loads are pointer aliases in LLVM. The source remains
                // the sole owner; an alias-end MirDrop is emitted immediately
                // before an owner drop when both lifetimes end at one use.
                break;
            case MirLoad
            {
                CreatesBorrowAlias: false,
                Source: MirPlace { Kind: not PlaceKind.Deref }
            } load:
                // A non-aliasing projected load is a by-value copy. LLVM
                // lowering retains managed pointer values for this form, so
                // the result owns an independent reference.
                Define(load.Target, owned, rcLocals);
                break;
            case MirLoad:
                // Borrow-alias projection/dereference loads remain backed by
                // another owner. A following MirCopy acquires independent
                // ownership when the value must escape.
                break;
            case MirStore store:
                Consume(store.Value, owned);
                if (store.Target.Kind == PlaceKind.Local)
                {
                    Define(store.Target, owned, rcLocals);
                }
                break;
            case MirCopy copy:
                Define(copy.Target, owned, rcLocals);
                break;
            case MirMove move:
                if (Transfer(move.Source, owned))
                {
                    Define(move.Target, owned, rcLocals);
                }
                break;
            case MirDrop drop:
                Consume(drop.Value, owned);
                break;
            case MirAlloc alloc:
                Define(alloc.Target, owned, rcLocals);
                break;
            case MirBinOp binOp:
                Define(binOp.Target, owned, rcLocals);
                break;
            case MirUnaryOp unaryOp:
                Define(unaryOp.Target, owned, rcLocals);
                break;
        }
    }

    private static bool ConsumeOwnedLocalOrAlias(
        LocalId local,
        HashSet<LocalId> owned,
        IReadOnlyDictionary<LocalId, HashSet<LocalId>> borrowAliasesByBase)
    {
        if (owned.Remove(local))
        {
            return true;
        }

        var aliasedOwners = owned
            .Where(owner => borrowAliasesByBase.TryGetValue(owner, out var aliases) && aliases.Contains(local))
            .ToArray();
        foreach (var owner in aliasedOwners)
        {
            owned.Remove(owner);
        }

        return aliasedOwners.Length > 0;
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

        // A newly produced owned value that has no later use must still be
        // released. Liveness-only last-use insertion otherwise leaks unused
        // constructor/call results.
        if (GetDefinedVariable(instr) is { } defined &&
            rcLocals.Contains(defined) &&
            !liveAfter.Contains(defined) &&
            ProducesOwnedValue(instr))
        {
            drops.Add(CreateDrop(defined, localTypes, instr.Span));
        }

        return drops;
    }

    private static bool ProducesOwnedValue(MirInstruction instruction) => instruction switch
    {
        MirLoad { CreatesBorrowAlias: true } => false,
        MirStore { Target.Kind: PlaceKind.Local } => true,
        MirStore => false,
        _ => true
    };

    /// <summary>
    /// 判断指令是否已转移变量所有权（不需要额外 drop）
    /// </summary>
    private static bool IsOwnershipTransfer(MirInstruction instr, LocalId varId)
    {
        if (instr is MirCall { IsTailCall: true })
        {
            return true;
        }

        if (instr is MirCall call &&
            call.Arguments.Any(argument => IsLocalOperand(argument, varId)))
        {
            return true;
        }

        if (instr is MirStore store && IsLocalOperand(store.Value, varId))
        {
            return true;
        }

        if (instr is MirCaseInject injection && IsLocalOperand(injection.Operand, varId))
        {
            return true;
        }

        if (instr is MirAssign assign && IsLocalOperand(assign.Source, varId))
        {
            return true;
        }

        if (instr is MirLoad load &&
            MirLocalTransferAnalysis.TryGetBinding(load, out var loadBinding) &&
            loadBinding.Source.Equals(varId))
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

    private static bool IsLocalOperand(MirOperand? operand, LocalId localId) =>
        operand is MirPlace { Kind: PlaceKind.Local, Local: var operandLocal } &&
        operandLocal.Equals(localId);

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

            case MirCaseInject injection:
                CollectOperand(injection.Operand, result);
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
                CollectProjectionBase(store.Target, result);
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
        if (operand is not MirPlace place)
        {
            return;
        }

        if (place.Kind == PlaceKind.Local)
        {
            result.Add(place.Local);
        }

        if (place.Base != null)
        {
            CollectOperand(place.Base, result);
        }

        if (place.Index != null)
        {
            CollectOperand(place.Index, result);
        }
    }

    private static void CollectProjectionBase(MirPlace? place, HashSet<LocalId> result)
    {
        if (place is not { Kind: not PlaceKind.Local })
        {
            return;
        }

        CollectOperand(place.Base, result);
        CollectOperand(place.Index, result);
    }

    private static void AddTerminatorUses(MirTerminator? terminator, HashSet<LocalId> result)
    {
        switch (terminator)
        {
            case MirReturn { Value: { } value }:
                CollectOperand(value, result);
                break;
            case MirSwitch { Discriminant: { } discriminant }:
                CollectOperand(discriminant, result);
                break;
        }
    }

    private static MirDrop CreateDrop(
        LocalId local,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        SourceSpan span) =>
        new()
        {
            Value = new MirPlace
            {
                Kind = PlaceKind.Local,
                Local = local,
                TypeId = localTypes.GetValueOrDefault(local, TypeId.None)
            },
            Span = span
        };

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
            case MirCaseInject injection:
                CollectOperand(injection.Operand, live);
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
                CollectProjectionBase(store.Target, live);
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
            MirCaseInject injection => injection.Target is MirPlace { Kind: PlaceKind.Local, Local: var ci } ? ci : null,
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
            MirStore store => store.Target is MirPlace { Kind: PlaceKind.Local, Local: var st } ? st : null,
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
