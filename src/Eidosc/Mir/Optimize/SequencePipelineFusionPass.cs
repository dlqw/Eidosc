using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Fuses canonical eager sequence stages only when callback reordering and
/// intermediate ownership transfer are proven safe. The first vertical slice
/// handles a direct map/filter/fold-left spine over Copy element types.
/// </summary>
public sealed class SequencePipelineFusionPass :
    IMirOptimizationPass,
    IFunctionOptimizationProofConsumer,
    IMirOptimizationMetricsProvider
{
    private readonly Func<string, IDisposable>? _measureSubphase;
    private FunctionOptimizationProofIndex _functionProofs = FunctionOptimizationProofIndex.Empty;

    public SequencePipelineFusionPass(Func<string, IDisposable>? measureSubphase = null)
    {
        _measureSubphase = measureSubphase;
    }

    public string Name => "SequencePipelineFusion";

    public SequencePipelineFusionStats Stats { get; } = new();

    public IReadOnlyDictionary<string, long> GetMetricsSnapshot() => Stats.ToMetricsSnapshot();

    FunctionOptimizationProofIndex IFunctionOptimizationProofConsumer.FunctionProofs
    {
        set => _functionProofs = value;
    }

    public MirModule Run(MirModule module)
    {
        Stats.Reset();
        IReadOnlyDictionary<string, MirFunc> functionsByKey;
        using (MeasureSubphase("sequence.analyze"))
        {
            functionsByKey = module.Functions
                .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        }

        var candidates = module.Functions
            .Where(static function => !function.IsExternal && function.BasicBlocks.Count > 0)
            .ToArray();
        Stats.FunctionsScanned = candidates.Length;

        var plans = new List<(MirFunc Function, SequencePipelinePlan Plan)>();
        using (MeasureSubphase("sequence.plan"))
        {
            foreach (var function in candidates)
            {
                if (TryFindPlan(module, function, functionsByKey, out var plan))
                {
                    plans.Add((function, plan));
                }
            }
        }

        if (plans.Count == 0)
        {
            return module;
        }

        using (MeasureSubphase("sequence.rewrite"))
        {
            foreach (var (function, plan) in plans)
            {
                ApplyPlan(function, plan);
                if (plan is DirectFoldPlan)
                {
                    Stats.DirectFoldsLowered++;
                }
                else
                {
                    Stats.PipelinesFormed++;
                }
                Stats.IntermediatesElided += plan.IntermediatesElided;
            }
        }

        return module.WithFunctions(module.Functions.ToList());
    }

    private IDisposable MeasureSubphase(string name) =>
        _measureSubphase?.Invoke(name) ?? NoopDisposable.Instance;

    private bool TryFindPlan(
        MirModule module,
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out SequencePipelinePlan plan)
    {
        plan = null!;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            var instructions = block.Instructions;
            for (var mapIndex = 0; mapIndex < instructions.Count; mapIndex++)
            {
                if (instructions[mapIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } mapTarget,
                        Function: MirFunctionRef
                        {
                            CompilerSemanticRole: CompilerSemanticRole.SequenceMap
                        } mapFunction,
                        Arguments.Count: 2
                    } mapCall ||
                    mapCall.Arguments[0] is not MirPlace { Kind: PlaceKind.Local } source)
                {
                    continue;
                }

                Stats.RoleCalls++;
                var cursor = mapIndex + 1;
                var mapOutput = FollowSingleMove(instructions, ref cursor, mapTarget);
                if (cursor >= instructions.Count ||
                    instructions[cursor] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } filterTarget,
                        Function: MirFunctionRef
                        {
                            CompilerSemanticRole: CompilerSemanticRole.SequenceFilter
                        } filterFunction,
                        Arguments.Count: 2
                    } filterCall ||
                    !IsLocal(filterCall.Arguments[0], mapOutput.Local))
                {
                    Stats.FallbackShapeAfterMap++;
                    continue;
                }

                Stats.RoleCalls++;
                var filterInstructionIndex = cursor;
                cursor++;
                var filterOutput = FollowSingleMove(instructions, ref cursor, filterTarget);
                if (!HasSingleRead(function, mapTarget.Local) ||
                    !HasSingleRead(function, mapOutput.Local))
                {
                    Stats.FallbackMultiUse++;
                    continue;
                }

                if (mapCall.Arguments[1] is not MirFunctionRef mapper ||
                    filterCall.Arguments[1] is not MirFunctionRef predicate)
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                if (!TryResolveCallback(functionsByKey, mapper, out var mapperFunction) ||
                    !TryResolveCallback(functionsByKey, predicate, out var predicateFunction))
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                var mapperParameters = mapperFunction.Locals.Where(static local => local.IsParameter).ToArray();
                var predicateParameters = predicateFunction.Locals.Where(static local => local.IsParameter).ToArray();
                if (mapperParameters.Length != 1 || predicateParameters.Length != 1 ||
                    !mapperFunction.ReturnType.IsValid)
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                var sourceElementType = mapperParameters[0].TypeId;
                var mappedElementType = mapperFunction.ReturnType;
                if (!IsCopyType(module, sourceElementType) ||
                    !IsCopyType(module, mappedElementType) ||
                    !IsSharedReferenceTo(module, predicateParameters[0].TypeId, mappedElementType))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                if (!AllowsCallbackReordering(mapper, mapperFunction) ||
                    !AllowsCallbackReordering(predicate, predicateFunction))
                {
                    RecordCallbackProofFallback(mapper, predicate);
                    continue;
                }

                if (cursor < instructions.Count &&
                    instructions[cursor] is MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } foldTarget,
                        Function: MirFunctionRef
                        {
                            CompilerSemanticRole: CompilerSemanticRole.SequenceFoldLeft
                        } foldFunction,
                        Arguments.Count: 3
                    } foldCall &&
                    IsLocal(foldCall.Arguments[0], filterOutput.Local))
                {
                    Stats.RoleCalls++;
                    if (HasSingleRead(function, filterTarget.Local) &&
                        HasSingleRead(function, filterOutput.Local) &&
                        foldCall.Arguments[2] is MirFunctionRef reducer &&
                        TryResolveCallback(functionsByKey, reducer, out var reducerFunction))
                    {
                        var reducerParameters = reducerFunction.Locals
                            .Where(static local => local.IsParameter)
                            .ToArray();
                        var accumulatorType = reducerFunction.ReturnType;
                        if (reducerParameters.Length == 2 &&
                            accumulatorType.IsValid &&
                            IsCopyType(module, accumulatorType) &&
                            AllowsCallbackReordering(reducer, reducerFunction))
                        {
                            plan = new MapFilterFoldPlan(
                                block,
                                mapIndex,
                                cursor,
                                source,
                                mapper,
                                predicate,
                                reducer,
                                foldCall.Arguments[1],
                                foldTarget,
                                sourceElementType,
                                mappedElementType,
                                predicateParameters[0].TypeId,
                                accumulatorType,
                                mapFunction.Span,
                                filterFunction.Span,
                                foldFunction.Span);
                            return true;
                        }

                        if (reducerParameters.Length == 2 && accumulatorType.IsValid &&
                            !AllowsCallbackReordering(reducer, reducerFunction))
                        {
                            RecordCallbackProofFallback(reducer);
                        }
                        else if (!IsCopyType(module, accumulatorType))
                        {
                            Stats.FallbackOwnership++;
                        }
                        else
                        {
                            Stats.FallbackUnknownCallback++;
                        }
                    }
                    else if (!HasSingleRead(function, filterTarget.Local) ||
                             !HasSingleRead(function, filterOutput.Local))
                    {
                        Stats.FallbackMultiUse++;
                    }
                    else
                    {
                        Stats.FallbackUnknownCallback++;
                    }
                }

                plan = new MapFilterCollectPlan(
                    block,
                    mapIndex,
                    filterInstructionIndex,
                    source,
                    filterTarget,
                    mapper,
                    predicate,
                    sourceElementType,
                    mappedElementType,
                    predicateParameters[0].TypeId,
                    GetRuntimeElementSize(module, mappedElementType),
                    TryResolveStaticArrayCapacity(function, source, out var staticCapacity)
                        ? staticCapacity
                        : null,
                    mapFunction.Span,
                    filterFunction.Span);
                return true;
            }
        }

        return TryFindDirectFoldPlan(module, function, functionsByKey, out plan);
    }

    private bool TryFindDirectFoldPlan(
        MirModule module,
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out SequencePipelinePlan plan)
    {
        plan = null!;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                if (block.Instructions[instructionIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } foldTarget,
                        Function: MirFunctionRef
                        {
                            CompilerSemanticRole: CompilerSemanticRole.SequenceFoldLeft
                        } foldFunction,
                        Arguments.Count: 3
                    } foldCall ||
                    foldCall.Arguments[0] is not MirPlace { Kind: PlaceKind.Local } source)
                {
                    continue;
                }

                Stats.RoleCalls++;
                if (foldCall.Arguments[2] is not MirFunctionRef reducer ||
                    !TryResolveCallback(functionsByKey, reducer, out var reducerFunction))
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                var reducerParameters = reducerFunction.Locals
                    .Where(static local => local.IsParameter)
                    .ToArray();
                var accumulatorType = reducerFunction.ReturnType;
                if (reducerParameters.Length != 2 ||
                    !accumulatorType.IsValid ||
                    reducerParameters[0].TypeId != accumulatorType)
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                var sourceElementType = reducerParameters[1].TypeId;
                if (!sourceElementType.IsValid ||
                    !IsCopyType(module, sourceElementType) ||
                    !IsCopyType(module, accumulatorType))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                plan = new DirectFoldPlan(
                    block,
                    instructionIndex,
                    source,
                    reducer,
                    foldCall.Arguments[1],
                    foldTarget,
                    sourceElementType,
                    accumulatorType,
                    foldFunction.Span);
                return true;
            }
        }

        return false;
    }

    private bool AllowsCallbackReordering(MirFunctionRef callback, MirFunc function)
    {
        if (_functionProofs.Allows(callback, FunctionOptimizationCapability.ReorderSequenceCallback))
        {
            return true;
        }

        return _functionProofs.Allows(callback, FunctionOptimizationCapability.InlineSequenceCallback) &&
               !_functionProofs.IsRecursive(function) &&
               IsLocallyReorderSafe(function);
    }

    private void RecordCallbackProofFallback(params MirFunctionRef[] callbacks)
    {
        var summaries = new List<FunctionOptimizationSummary>(callbacks.Length);
        foreach (var callback in callbacks)
        {
            if (!_functionProofs.TryGetSummary(callback, out var summary))
            {
                Stats.FallbackUnknownCallback++;
                return;
            }

            summaries.Add(summary);
        }

        if (summaries.Any(static summary => !summary.IsTrusted))
        {
            Stats.FallbackUnknownCallback++;
            return;
        }

        if (summaries.Any(static summary => !summary.Effects.IsPure))
        {
            Stats.FallbackEffect++;
            return;
        }

        if (summaries.Any(static summary => summary.MayPanic || summary.MayDiverge))
        {
            Stats.FallbackPanicOrDivergence++;
            return;
        }

        Stats.FallbackEffect++;
    }

    private bool IsLocallyReorderSafe(MirFunc function)
    {
        if (function.BasicBlocks.Count != 1 || function.BasicBlocks[0].Terminator is not MirReturn)
        {
            return false;
        }

        var aggregateAliases = new HashSet<LocalId>();

        foreach (var instruction in function.BasicBlocks[0].Instructions)
        {
            if (instruction is MirAlloc { Target: { Kind: PlaceKind.Local } allocationTarget })
            {
                aggregateAliases.Add(allocationTarget.Local);
                continue;
            }

            if (TryTrackCompilerLocalAggregateAlias(instruction, aggregateAliases))
            {
                continue;
            }

            switch (instruction)
            {
                case MirAssign or MirCopy or MirMove or MirUnaryOp:
                    break;
                case MirBinOp { Operator: BinaryOp.Div or BinaryOp.Mod or BinaryOp.Concat }:
                    return false;
                case MirBinOp:
                    break;
                case MirLoad load when IsSafeLocalRead(load.Source, function, aggregateAliases):
                    break;
                case MirStore store when IsSafeLocalWrite(store.Target, function, aggregateAliases):
                    break;
                case MirDrop drop when IsCompilerLocalAggregate(drop.Value, aggregateAliases):
                    break;
                case MirCall { Function: MirFunctionRef callee } when
                    _functionProofs.Allows(callee, FunctionOptimizationCapability.ReorderSequenceCallback):
                    break;
                default:
                    return false;
            }
        }

        if (function.BasicBlocks[0].Terminator is MirReturn { Value: MirPlace returned } &&
            TryGetRootLocal(returned, out var returnedRoot) &&
            aggregateAliases.Contains(returnedRoot))
        {
            return false;
        }

        return true;
    }

    private static bool TryTrackCompilerLocalAggregateAlias(
        MirInstruction instruction,
        ISet<LocalId> aggregateAliases)
    {
        MirPlace? target;
        MirOperand? source;
        switch (instruction)
        {
            case MirAssign assign:
                target = assign.Target;
                source = assign.Source;
                break;
            case MirCopy copy:
                target = copy.Target;
                source = copy.Source;
                break;
            case MirMove move:
                target = move.Target;
                source = move.Source;
                break;
            default:
                return false;
        }

        if (target.Kind != PlaceKind.Local)
        {
            return false;
        }

        if (source is MirPlace { Kind: PlaceKind.Local } sourceLocal &&
            aggregateAliases.Contains(sourceLocal.Local))
        {
            aggregateAliases.Add(target.Local);
            return true;
        }

        aggregateAliases.Remove(target.Local);
        return false;
    }

    private static bool IsSafeLocalRead(
        MirOperand operand,
        MirFunc function,
        IReadOnlySet<LocalId> allocatedLocals)
    {
        if (operand is not MirPlace place || !TryGetRootLocal(place, out var root))
        {
            return false;
        }

        if (allocatedLocals.Contains(root))
        {
            return true;
        }

        var local = function.Locals.FirstOrDefault(candidate => candidate.Id == root);
        return place.Kind == PlaceKind.Local || local?.IsParameter == true;
    }

    private static bool IsSafeLocalWrite(
        MirPlace target,
        MirFunc function,
        IReadOnlySet<LocalId> allocatedLocals)
    {
        if (!TryGetRootLocal(target, out var root))
        {
            return false;
        }

        if (allocatedLocals.Contains(root))
        {
            return true;
        }

        var local = function.Locals.FirstOrDefault(candidate => candidate.Id == root);
        return target.Kind == PlaceKind.Local && local?.IsParameter == false;
    }

    private static bool IsCompilerLocalAggregate(MirOperand operand, IReadOnlySet<LocalId> allocatedLocals) =>
        operand is MirPlace place &&
        TryGetRootLocal(place, out var root) &&
        allocatedLocals.Contains(root);

    private static bool TryGetRootLocal(MirPlace place, out LocalId root)
    {
        var current = place;
        while (current.Kind != PlaceKind.Local)
        {
            if (current.Base is not MirPlace parent)
            {
                root = default;
                return false;
            }

            current = parent;
        }

        root = current.Local;
        return root.IsValid;
    }

    private static bool TryResolveCallback(
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        MirFunctionRef callback,
        out MirFunc function) =>
        functionsByKey.TryGetValue(MirFunctionIdentity.GetStableKey(callback), out function!);

    private static bool IsCopyType(MirModule module, TypeId typeId) =>
        CopyTypeSemantics.IsCopyType(
            typeId,
            null,
            module.TypeDescriptors,
            module.DynamicTypeKeys,
            module.ConstructorLayouts);

    private static int GetRuntimeElementSize(MirModule module, TypeId typeId)
    {
        if (module.TypeDescriptors.TryGetValue(typeId.Value, out var descriptor) &&
            descriptor is TypeDescriptor.Tuple tuple)
        {
            return tuple.FieldTypes.Length * IntPtr.Size;
        }

        return typeId.Value switch
        {
            BaseTypes.BoolId => 1,
            BaseTypes.CharId => 4,
            BaseTypes.UnitId or BaseTypes.NeverId => 0,
            BaseTypes.IntId => sizeof(long),
            BaseTypes.FloatId => sizeof(double),
            _ => IntPtr.Size
        };
    }

    private static bool IsSharedReferenceTo(MirModule module, TypeId referenceType, TypeId innerType)
    {
        if (module.TypeDescriptors.TryGetValue(referenceType.Value, out var descriptor))
        {
            return descriptor is TypeDescriptor.Ref reference && reference.Inner == innerType;
        }

        return module.DynamicTypeKeys.TryGetValue(referenceType.Value, out var typeKey) &&
               TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor) &&
               descriptor is TypeDescriptor.Ref dynamicReference &&
               dynamicReference.Inner == innerType;
    }

    private static MirPlace FollowSingleMove(
        IReadOnlyList<MirInstruction> instructions,
        ref int cursor,
        MirPlace source)
    {
        if (cursor < instructions.Count &&
            instructions[cursor] is MirMove
            {
                Target: MirPlace { Kind: PlaceKind.Local } target,
                Source: MirPlace { Kind: PlaceKind.Local } moveSource
            } &&
            moveSource.Local == source.Local)
        {
            cursor++;
            return target;
        }

        return source;
    }

    private static bool HasSingleRead(MirFunc function, LocalId local)
    {
        var reads = 0;
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                reads += CountInstructionReads(instruction, local);
            }

            reads += CountTerminatorReads(block.Terminator, local);
        }

        return reads == 1;
    }

    private static int CountInstructionReads(MirInstruction instruction, LocalId local) => instruction switch
    {
        MirAssign assign => CountOperandReads(assign.Source, local),
        MirCaseInject injection => CountOperandReads(injection.Operand, local),
        MirCall call => CountOperandReads(call.Function, local) +
                        call.Arguments.Sum(argument => CountOperandReads(argument, local)),
        MirBinOp binary => CountOperandReads(binary.Left, local) + CountOperandReads(binary.Right, local),
        MirUnaryOp unary => CountOperandReads(unary.Operand, local),
        MirLoad load => CountOperandReads(load.Source, local),
        MirStore store => CountOperandReads(store.Value, local) + CountProjectionAddressReads(store.Target, local),
        MirDrop drop => CountOperandReads(drop.Value, local),
        MirCopy copy => CountOperandReads(copy.Source, local),
        MirMove move => CountOperandReads(move.Source, local),
        _ => 0
    };

    private static int CountTerminatorReads(MirTerminator? terminator, LocalId local) => terminator switch
    {
        MirReturn { Value: not null } returned => CountOperandReads(returned.Value, local),
        MirSwitch switched => CountOperandReads(switched.Discriminant, local),
        _ => 0
    };

    private static int CountOperandReads(MirOperand operand, LocalId local)
    {
        if (operand is not MirPlace place)
        {
            return 0;
        }

        var count = place.Kind == PlaceKind.Local && place.Local == local ? 1 : 0;
        if (place.Base is MirPlace parent)
        {
            count += CountOperandReads(parent, local);
        }
        if (place.Index != null)
        {
            count += CountOperandReads(place.Index, local);
        }
        return count;
    }

    private static int CountProjectionAddressReads(MirPlace place, LocalId local)
    {
        if (place.Kind == PlaceKind.Local)
        {
            return 0;
        }

        var count = place.Base is MirPlace parent ? CountOperandReads(parent, local) : 0;
        if (place.Index != null)
        {
            count += CountOperandReads(place.Index, local);
        }
        return count;
    }

    private static bool IsLocal(MirOperand operand, LocalId local) =>
        operand is MirPlace { Kind: PlaceKind.Local, Local: var candidate } && candidate == local;

    private static bool TryResolveStaticArrayCapacity(
        MirFunc function,
        MirPlace source,
        out long capacity)
    {
        capacity = 0;
        var current = source.Local;
        var visited = new HashSet<LocalId>();
        while (visited.Add(current))
        {
            var definitions = function.BasicBlocks
                .SelectMany(static block => block.Instructions)
                .Where(instruction => instruction switch
                {
                    MirCall { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == current,
                    MirAssign { Target: { Kind: PlaceKind.Local, Local: var target } } => target == current,
                    MirMove { Target: { Kind: PlaceKind.Local, Local: var target } } => target == current,
                    MirLoad { Target: { Kind: PlaceKind.Local, Local: var target } } => target == current,
                    _ => false
                })
                .ToArray();
            if (definitions.Length != 1)
            {
                return false;
            }

            if (definitions[0] is MirCall
                {
                    Function: MirFunctionRef functionRef,
                    Arguments: [MirConstant { Value: MirConstantValue.IntValue(var value) }, ..]
                } && MirRuntimeFunctions.HasIdentity(
                    functionRef,
                    WellKnownStrings.InternalNames.ArrayNew) && value >= 0)
            {
                capacity = value;
                return true;
            }

            var next = definitions[0] switch
            {
                MirAssign { Source: MirPlace { Kind: PlaceKind.Local, Local: var assignSource } } => assignSource,
                MirMove { Source: { Kind: PlaceKind.Local, Local: var moveSource } } => moveSource,
                MirLoad
                {
                    Source: MirPlace { Kind: PlaceKind.Local, Local: var loadSource },
                    CreatesBorrowAlias: false
                } => loadSource,
                _ => LocalId.None
            };
            if (!next.IsValid)
            {
                return false;
            }

            current = next;
        }

        return false;
    }

    private static void ApplyPlan(MirFunc function, SequencePipelinePlan plan)
    {
        switch (plan)
        {
            case MapFilterFoldPlan fold:
                ApplyFoldPlan(function, fold);
                break;
            case MapFilterCollectPlan collect:
                ApplyCollectPlan(function, collect);
                break;
            case DirectFoldPlan directFold:
                ApplyDirectFoldPlan(function, directFold);
                break;
            default:
                throw new InvalidOperationException($"Unsupported sequence pipeline plan '{plan.GetType().Name}'.");
        }
    }

    private static void ApplyDirectFoldPlan(MirFunc function, DirectFoldPlan plan)
    {
        var span = plan.FoldSpan;
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var nextLocalValue = function.Locals.Count == 0
            ? 1
            : function.Locals.Max(static local => local.Id.Value) + 1;
        var nextBlockValue = function.BasicBlocks.Max(static block => block.Id.Value) + 1;

        MirPlace NewLocal(string name, TypeId type)
        {
            var id = new LocalId { Value = nextLocalValue++ };
            function.Locals.Add(new MirLocal { Id = id, Name = name, TypeId = type });
            return new MirPlace { Kind = PlaceKind.Local, Local = id, TypeId = type, Span = span };
        }

        MirBasicBlock NewBlock()
        {
            var block = new MirBasicBlock
            {
                Id = new BlockId { Value = nextBlockValue++ },
                Span = span
            };
            function.BasicBlocks.Add(block);
            return block;
        }

        var length = NewLocal("__sequence_fold_length", intType);
        var index = NewLocal("__sequence_fold_index", intType);
        var accumulator = NewLocal("__sequence_fold_accumulator", plan.AccumulatorType);
        var exhausted = NewLocal("__sequence_fold_exhausted", boolType);
        var element = NewLocal("__sequence_fold_element", plan.SourceElementType);
        var accumulatorArgument = NewLocal("__sequence_fold_accumulator_argument", plan.AccumulatorType);
        var nextAccumulator = NewLocal("__sequence_fold_next_accumulator", plan.AccumulatorType);
        var nextAccumulatorValue = NewLocal("__sequence_fold_next_accumulator_value", plan.AccumulatorType);
        var nextIndex = NewLocal("__sequence_fold_next_index", intType);

        var continuation = NewBlock();
        continuation.Instructions.AddRange(
            plan.Block.Instructions.Skip(plan.InstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;

        var header = NewBlock();
        var reduce = NewBlock();
        var increment = NewBlock();
        var exit = NewBlock();

        plan.Block.Instructions.RemoveRange(
            plan.InstructionIndex,
            plan.Block.Instructions.Count - plan.InstructionIndex);
        plan.Block.Instructions.Add(new MirCall
        {
            Target = length,
            Function = MirRuntimeFunctions.CreateFunctionRef(
                WellKnownStrings.InternalNames.ArrayLength,
                intType,
                span),
            Arguments = [plan.Source],
            BorrowedArgumentIndices = new HashSet<int> { 0 },
            Span = span
        });
        plan.Block.Instructions.Add(new MirAssign
        {
            Target = index,
            Source = IntConstant(0, span),
            Span = span
        });
        plan.Block.Instructions.Add(CreateTransfer(accumulator, plan.Initial, span));
        plan.Block.Terminator = new MirGoto { Target = header.Id, Span = span };

        header.Instructions.Add(new MirBinOp
        {
            Target = exhausted,
            Operator = BinaryOp.Ge,
            Left = index,
            Right = length,
            Span = span
        });
        header.Terminator = BoolSwitch(exhausted, exit.Id, reduce.Id, span);

        reduce.Instructions.Add(new MirLoad
        {
            Target = element,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = plan.Source,
                Index = index,
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = plan.SourceElementType,
                Span = span
            },
            CreatesBorrowAlias = false,
            Span = span
        });
        reduce.Instructions.Add(new MirMove
        {
            Target = accumulatorArgument,
            Source = accumulator,
            Span = span
        });
        reduce.Instructions.Add(new MirCall
        {
            Target = nextAccumulator,
            Function = plan.Reducer,
            Arguments = [accumulatorArgument, element],
            Span = span
        });
        reduce.Instructions.Add(new MirMove
        {
            Target = nextAccumulatorValue,
            Source = nextAccumulator,
            Span = span
        });
        reduce.Instructions.Add(new MirStore
        {
            Target = accumulator,
            Value = nextAccumulatorValue,
            Span = span
        });
        reduce.Terminator = new MirGoto { Target = increment.Id, Span = span };

        increment.Instructions.Add(new MirBinOp
        {
            Target = nextIndex,
            Operator = BinaryOp.Add,
            Left = index,
            Right = IntConstant(1, span),
            Span = span
        });
        increment.Instructions.Add(new MirStore
        {
            Target = index,
            Value = nextIndex,
            Span = span
        });
        increment.Terminator = new MirGoto { Target = header.Id, Span = span };

        exit.Instructions.Add(new MirMove
        {
            Target = plan.FoldTarget,
            Source = accumulator,
            Span = span
        });
        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };
    }

    private static void ApplyFoldPlan(MirFunc function, MapFilterFoldPlan plan)
    {
        var span = plan.FoldSpan;
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var nextLocalValue = function.Locals.Count == 0
            ? 1
            : function.Locals.Max(static local => local.Id.Value) + 1;
        var nextBlockValue = function.BasicBlocks.Max(static block => block.Id.Value) + 1;

        MirPlace NewLocal(string name, TypeId type)
        {
            var id = new LocalId { Value = nextLocalValue++ };
            function.Locals.Add(new MirLocal { Id = id, Name = name, TypeId = type });
            return new MirPlace { Kind = PlaceKind.Local, Local = id, TypeId = type, Span = span };
        }

        MirBasicBlock NewBlock()
        {
            var block = new MirBasicBlock
            {
                Id = new BlockId { Value = nextBlockValue++ },
                Span = span
            };
            function.BasicBlocks.Add(block);
            return block;
        }

        var length = NewLocal("__sequence_length", intType);
        var index = NewLocal("__sequence_index", intType);
        var accumulator = NewLocal("__sequence_accumulator", plan.AccumulatorType);
        var exhausted = NewLocal("__sequence_exhausted", boolType);
        var element = NewLocal("__sequence_element", plan.SourceElementType);
        var mapped = NewLocal("__sequence_mapped", plan.MappedElementType);
        var accepted = NewLocal("__sequence_accepted", boolType);
        var accumulatorArgument = NewLocal("__sequence_accumulator_argument", plan.AccumulatorType);
        var nextAccumulator = NewLocal("__sequence_next_accumulator", plan.AccumulatorType);
        var nextAccumulatorValue = NewLocal("__sequence_next_accumulator_value", plan.AccumulatorType);
        var nextIndex = NewLocal("__sequence_next_index", intType);

        var continuation = NewBlock();
        continuation.Instructions.AddRange(
            plan.Block.Instructions.Skip(plan.EndInstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;

        var header = NewBlock();
        var map = NewBlock();
        var reduce = NewBlock();
        var reject = NewBlock();
        var increment = NewBlock();
        var exit = NewBlock();

        plan.Block.Instructions.RemoveRange(
            plan.StartInstructionIndex,
            plan.Block.Instructions.Count - plan.StartInstructionIndex);
        plan.Block.Instructions.Add(new MirCall
        {
            Target = length,
            Function = MirRuntimeFunctions.CreateFunctionRef(
                WellKnownStrings.InternalNames.ArrayLength,
                intType,
                plan.MapSpan),
            Arguments = [plan.Source],
            BorrowedArgumentIndices = new HashSet<int> { 0 },
            Span = plan.MapSpan
        });
        plan.Block.Instructions.Add(new MirAssign
        {
            Target = index,
            Source = IntConstant(0, plan.MapSpan),
            Span = plan.MapSpan
        });
        plan.Block.Instructions.Add(CreateTransfer(accumulator, plan.Initial, plan.FoldSpan));
        plan.Block.Terminator = new MirGoto { Target = header.Id, Span = span };

        header.Instructions.Add(new MirBinOp
        {
            Target = exhausted,
            Operator = BinaryOp.Ge,
            Left = index,
            Right = length,
            Span = span
        });
        header.Terminator = BoolSwitch(exhausted, exit.Id, map.Id, span);

        map.Instructions.Add(new MirLoad
        {
            Target = element,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = plan.Source,
                Index = index,
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = plan.SourceElementType,
                Span = plan.MapSpan
            },
            CreatesBorrowAlias = false,
            Span = plan.MapSpan
        });
        map.Instructions.Add(new MirCall
        {
            Target = mapped,
            Function = plan.Mapper,
            Arguments = [element],
            Span = plan.MapSpan
        });
        map.Instructions.Add(new MirCall
        {
            Target = accepted,
            Function = plan.Predicate,
            Arguments = [mapped with { TypeId = plan.PredicateParameterType }],
            Span = plan.FilterSpan
        });
        map.Terminator = BoolSwitch(accepted, reduce.Id, reject.Id, plan.FilterSpan);

        reduce.Instructions.Add(new MirMove
        {
            Target = accumulatorArgument,
            Source = accumulator,
            Span = plan.FoldSpan
        });
        reduce.Instructions.Add(new MirCall
        {
            Target = nextAccumulator,
            Function = plan.Reducer,
            Arguments = [accumulatorArgument, mapped],
            Span = plan.FoldSpan
        });
        reduce.Instructions.Add(new MirMove
        {
            Target = nextAccumulatorValue,
            Source = nextAccumulator,
            Span = plan.FoldSpan
        });
        reduce.Instructions.Add(new MirStore
        {
            Target = accumulator,
            Value = nextAccumulatorValue,
            Span = plan.FoldSpan
        });
        reduce.Terminator = new MirGoto { Target = increment.Id, Span = span };

        reject.Terminator = new MirGoto { Target = increment.Id, Span = span };

        increment.Instructions.Add(new MirBinOp
        {
            Target = nextIndex,
            Operator = BinaryOp.Add,
            Left = index,
            Right = IntConstant(1, span),
            Span = span
        });
        increment.Instructions.Add(new MirStore
        {
            Target = index,
            Value = nextIndex,
            Span = span
        });
        increment.Terminator = new MirGoto { Target = header.Id, Span = span };

        exit.Instructions.Add(new MirMove
        {
            Target = plan.FoldTarget,
            Source = accumulator,
            Span = plan.FoldSpan
        });
        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };
    }

    private static void ApplyCollectPlan(MirFunc function, MapFilterCollectPlan plan)
    {
        var span = plan.FilterSpan;
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var nextLocalValue = function.Locals.Count == 0
            ? 1
            : function.Locals.Max(static local => local.Id.Value) + 1;
        var nextBlockValue = function.BasicBlocks.Max(static block => block.Id.Value) + 1;

        MirPlace NewLocal(string name, TypeId type)
        {
            var id = new LocalId { Value = nextLocalValue++ };
            function.Locals.Add(new MirLocal { Id = id, Name = name, TypeId = type });
            return new MirPlace { Kind = PlaceKind.Local, Local = id, TypeId = type, Span = span };
        }

        MirBasicBlock NewBlock()
        {
            var block = new MirBasicBlock
            {
                Id = new BlockId { Value = nextBlockValue++ },
                Span = span
            };
            function.BasicBlocks.Add(block);
            return block;
        }

        var length = NewLocal("__sequence_length", intType);
        var index = NewLocal("__sequence_index", intType);
        var exhausted = NewLocal("__sequence_exhausted", boolType);
        var element = NewLocal("__sequence_element", plan.SourceElementType);
        var mapped = NewLocal("__sequence_mapped", plan.MappedElementType);
        var accepted = NewLocal("__sequence_accepted", boolType);
        var nextIndex = NewLocal("__sequence_next_index", intType);

        var continuation = NewBlock();
        continuation.Instructions.AddRange(
            plan.Block.Instructions.Skip(plan.EndInstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;

        var header = NewBlock();
        var map = NewBlock();
        var append = NewBlock();
        var reject = NewBlock();
        var increment = NewBlock();
        var exit = NewBlock();

        plan.Block.Instructions.RemoveRange(
            plan.StartInstructionIndex,
            plan.Block.Instructions.Count - plan.StartInstructionIndex);
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(
            length,
            plan.Source,
            plan.MapSpan));
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayNewCall(
            plan.ResultTarget,
            plan.StaticCapacityUpperBound is { } staticCapacity
                ? IntConstant(staticCapacity, plan.FilterSpan)
                : length,
            IntConstant(plan.MappedElementSize, plan.FilterSpan),
            plan.FilterSpan));
        plan.Block.Instructions.Add(new MirAssign
        {
            Target = index,
            Source = IntConstant(0, plan.MapSpan),
            Span = plan.MapSpan
        });
        plan.Block.Terminator = new MirGoto { Target = header.Id, Span = span };

        header.Instructions.Add(new MirBinOp
        {
            Target = exhausted,
            Operator = BinaryOp.Ge,
            Left = index,
            Right = length,
            Span = span
        });
        header.Terminator = BoolSwitch(exhausted, exit.Id, map.Id, span);

        map.Instructions.Add(new MirLoad
        {
            Target = element,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = plan.Source,
                Index = index,
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = plan.SourceElementType,
                Span = plan.MapSpan
            },
            CreatesBorrowAlias = false,
            Span = plan.MapSpan
        });
        map.Instructions.Add(new MirCall
        {
            Target = mapped,
            Function = plan.Mapper,
            Arguments = [element],
            Span = plan.MapSpan
        });
        map.Instructions.Add(new MirCall
        {
            Target = accepted,
            Function = plan.Predicate,
            Arguments = [mapped with { TypeId = plan.PredicateParameterType }],
            Span = plan.FilterSpan
        });
        map.Terminator = BoolSwitch(accepted, append.Id, reject.Id, plan.FilterSpan);

        append.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayPushCall(
            plan.ResultTarget,
            plan.ResultTarget,
            mapped,
            IntConstant(plan.MappedElementSize, plan.FilterSpan),
            plan.FilterSpan));
        append.Terminator = new MirGoto { Target = increment.Id, Span = span };

        reject.Terminator = new MirGoto { Target = increment.Id, Span = span };

        increment.Instructions.Add(new MirBinOp
        {
            Target = nextIndex,
            Operator = BinaryOp.Add,
            Left = index,
            Right = IntConstant(1, span),
            Span = span
        });
        increment.Instructions.Add(new MirStore
        {
            Target = index,
            Value = nextIndex,
            Span = span
        });
        increment.Terminator = new MirGoto { Target = header.Id, Span = span };

        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };
    }

    private static MirInstruction CreateTransfer(MirPlace target, MirOperand source, SourceSpan span) =>
        source is MirPlace { Kind: PlaceKind.Local } sourcePlace
            ? new MirMove { Target = target, Source = sourcePlace, Span = span }
            : new MirAssign { Target = target, Source = source, Span = span };

    private static MirSwitch BoolSwitch(
        MirOperand discriminant,
        BlockId trueTarget,
        BlockId falseTarget,
        SourceSpan span) => new()
    {
        Discriminant = discriminant,
        Branches =
        [
            new MirSwitchBranch
            {
                Value = new MirConstant
                {
                    Value = new MirConstantValue.BoolValue(true),
                    TypeId = new TypeId(BaseTypes.BoolId),
                    Span = span
                },
                Target = trueTarget
            }
        ],
        DefaultTarget = falseTarget,
        Span = span
    };

    private static MirConstant IntConstant(long value, SourceSpan span) => new()
    {
        Value = new MirConstantValue.IntValue(value),
        TypeId = new TypeId(BaseTypes.IntId),
        Span = span
    };

    private abstract record SequencePipelinePlan(int IntermediatesElided);

    private sealed record MapFilterFoldPlan(
        MirBasicBlock Block,
        int StartInstructionIndex,
        int EndInstructionIndex,
        MirPlace Source,
        MirFunctionRef Mapper,
        MirFunctionRef Predicate,
        MirFunctionRef Reducer,
        MirOperand Initial,
        MirPlace FoldTarget,
        TypeId SourceElementType,
        TypeId MappedElementType,
        TypeId PredicateParameterType,
        TypeId AccumulatorType,
        SourceSpan MapSpan,
        SourceSpan FilterSpan,
        SourceSpan FoldSpan) : SequencePipelinePlan(2);

    private sealed record MapFilterCollectPlan(
        MirBasicBlock Block,
        int StartInstructionIndex,
        int EndInstructionIndex,
        MirPlace Source,
        MirPlace ResultTarget,
        MirFunctionRef Mapper,
        MirFunctionRef Predicate,
        TypeId SourceElementType,
        TypeId MappedElementType,
        TypeId PredicateParameterType,
        int MappedElementSize,
        long? StaticCapacityUpperBound,
        SourceSpan MapSpan,
        SourceSpan FilterSpan) : SequencePipelinePlan(1);

    private sealed record DirectFoldPlan(
        MirBasicBlock Block,
        int InstructionIndex,
        MirPlace Source,
        MirFunctionRef Reducer,
        MirOperand Initial,
        MirPlace FoldTarget,
        TypeId SourceElementType,
        TypeId AccumulatorType,
        SourceSpan FoldSpan) : SequencePipelinePlan(0);

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        private NoopDisposable()
        {
        }

        public void Dispose()
        {
        }
    }
}

public sealed class SequencePipelineFusionStats
{
    public long FunctionsScanned { get; internal set; }
    public long RoleCalls { get; internal set; }
    public long PipelinesFormed { get; internal set; }
    public long DirectFoldsLowered { get; internal set; }
    public long IntermediatesElided { get; internal set; }
    public long FallbackEffect { get; internal set; }
    public long FallbackPanicOrDivergence { get; internal set; }
    public long FallbackUnknownCallback { get; internal set; }
    public long FallbackMultiUse { get; internal set; }
    public long FallbackEscape { get; internal set; }
    public long FallbackOwnership { get; internal set; }
    public long FallbackShapeAfterMap { get; internal set; }
    public long FallbackShapeAfterFilter { get; internal set; }
    public long CollectorsStackPromoted { get; internal set; }
    public long ClosuresElided { get; internal set; }
    public long EvidenceElided { get; internal set; }

    public IReadOnlyDictionary<string, long> ToMetricsSnapshot() =>
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["sequence.functions_scanned"] = FunctionsScanned,
            ["sequence.role_calls"] = RoleCalls,
            ["sequence.pipelines_formed"] = PipelinesFormed,
            ["sequence.direct_folds_lowered"] = DirectFoldsLowered,
            ["sequence.intermediates_elided"] = IntermediatesElided,
            ["sequence.fallback.effect"] = FallbackEffect,
            ["sequence.fallback.panic_or_divergence"] = FallbackPanicOrDivergence,
            ["sequence.fallback.unknown_callback"] = FallbackUnknownCallback,
            ["sequence.fallback.multi_use"] = FallbackMultiUse,
            ["sequence.fallback.escape"] = FallbackEscape,
            ["sequence.fallback.ownership"] = FallbackOwnership,
            ["sequence.fallback.shape_after_map"] = FallbackShapeAfterMap,
            ["sequence.fallback.shape_after_filter"] = FallbackShapeAfterFilter,
            ["sequence.collectors_stack_promoted"] = CollectorsStackPromoted,
            ["sequence.closures_elided"] = ClosuresElided,
            ["sequence.evidence_elided"] = EvidenceElided
        };

    internal void Reset()
    {
        FunctionsScanned = 0;
        RoleCalls = 0;
        PipelinesFormed = 0;
        DirectFoldsLowered = 0;
        IntermediatesElided = 0;
        FallbackEffect = 0;
        FallbackPanicOrDivergence = 0;
        FallbackUnknownCallback = 0;
        FallbackMultiUse = 0;
        FallbackEscape = 0;
        FallbackOwnership = 0;
        FallbackShapeAfterMap = 0;
        FallbackShapeAfterFilter = 0;
        CollectorsStackPromoted = 0;
        ClosuresElided = 0;
        EvidenceElided = 0;
    }
}
