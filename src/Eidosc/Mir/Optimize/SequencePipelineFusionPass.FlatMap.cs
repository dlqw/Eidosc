using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class SequencePipelineFusionPass
{
    private static void ApplyFlatMapCountPlan(MirFunc function, FlatMapCountPlan plan)
    {
        var span = plan.CountSpan;
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var nextLocalValue = function.Locals.Count == 0
            ? 1
            : function.Locals.Max(static local => local.Id.Value) + 1;
        var nextBlockValue = function.BasicBlocks.Count == 0
            ? 1
            : function.BasicBlocks.Max(static block => block.Id.Value) + 1;

        MirPlace NewLocal(string name, TypeId type)
        {
            var id = new LocalId { Value = nextLocalValue++ };
            function.Locals.Add(new MirLocal
            {
                Id = id,
                Name = name,
                TypeId = type,
                Span = span
            });
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

        var outerLength = NewLocal("__sequence_flat_map_outer_length", intType);
        var outerIndex = NewLocal("__sequence_flat_map_outer_index", intType);
        var outerExhausted = NewLocal("__sequence_flat_map_outer_exhausted", boolType);
        var outerElement = NewLocal("__sequence_flat_map_outer_element", plan.OuterElementType);
        var innerSequence = NewLocal("__sequence_flat_map_inner_sequence", plan.InnerSequenceType);
        var innerLength = NewLocal("__sequence_flat_map_inner_length", intType);
        var innerIndex = NewLocal("__sequence_flat_map_inner_index", intType);
        var innerExhausted = NewLocal("__sequence_flat_map_inner_exhausted", boolType);
        var innerElement = NewLocal("__sequence_flat_map_inner_element", plan.InnerElementType);
        var accepted = NewLocal("__sequence_flat_map_count_accepted", boolType);
        var count = NewLocal("__sequence_flat_map_count", intType);
        var nextCount = NewLocal("__sequence_flat_map_next_count", intType);
        var nextInnerIndex = NewLocal("__sequence_flat_map_next_inner_index", intType);
        var nextOuterIndex = NewLocal("__sequence_flat_map_next_outer_index", intType);

        var continuation = NewBlock();
        continuation.Instructions.AddRange(plan.Block.Instructions.Skip(plan.CountInstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;

        var outerHeader = NewBlock();
        var mapOuter = NewBlock();
        var innerHeader = NewBlock();
        var visitInner = NewBlock();
        var countAccepted = NewBlock();
        var countRejected = NewBlock();
        var incrementInner = NewBlock();
        var finishInner = NewBlock();
        var incrementOuter = NewBlock();
        var exit = NewBlock();

        plan.Block.Instructions.RemoveRange(
            plan.FlatMapInstructionIndex,
            plan.Block.Instructions.Count - plan.FlatMapInstructionIndex);
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(
            outerLength,
            plan.Source,
            plan.FlatMapSpan));
        plan.Block.Instructions.Add(new MirAssign
        {
            Target = outerIndex,
            Source = IntConstant(0, plan.FlatMapSpan),
            Span = plan.FlatMapSpan
        });
        plan.Block.Instructions.Add(new MirAssign
        {
            Target = count,
            Source = IntConstant(0, span),
            Span = span
        });
        plan.Block.Terminator = new MirGoto { Target = outerHeader.Id, Span = span };

        outerHeader.Instructions.Add(new MirBinOp
        {
            Target = outerExhausted,
            Operator = BinaryOp.Ge,
            Left = outerIndex,
            Right = outerLength,
            Span = span
        });
        outerHeader.Terminator = BoolSwitch(outerExhausted, exit.Id, mapOuter.Id, span);

        mapOuter.Instructions.Add(new MirLoad
        {
            Target = outerElement,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = plan.Source,
                Index = outerIndex,
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = plan.OuterElementType,
                Span = plan.FlatMapSpan
            },
            CreatesBorrowAlias = false,
            MovesOutOfSource = false,
            Span = plan.FlatMapSpan
        });
        mapOuter.Instructions.Add(new MirCall
        {
            Target = innerSequence,
            Function = plan.Mapper,
            Arguments = [outerElement],
            Span = plan.FlatMapSpan
        });
        mapOuter.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(
            innerLength,
            innerSequence,
            plan.FlatMapSpan));
        mapOuter.Instructions.Add(new MirAssign
        {
            Target = innerIndex,
            Source = IntConstant(0, plan.FlatMapSpan),
            Span = plan.FlatMapSpan
        });
        mapOuter.Terminator = new MirGoto { Target = innerHeader.Id, Span = span };

        innerHeader.Instructions.Add(new MirBinOp
        {
            Target = innerExhausted,
            Operator = BinaryOp.Ge,
            Left = innerIndex,
            Right = innerLength,
            Span = span
        });
        innerHeader.Terminator = BoolSwitch(innerExhausted, finishInner.Id, visitInner.Id, span);

        visitInner.Instructions.Add(new MirLoad
        {
            Target = innerElement,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = innerSequence,
                Index = innerIndex,
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = plan.InnerElementType,
                Span = plan.CountSpan
            },
            CreatesBorrowAlias = false,
            MovesOutOfSource = false,
            Span = plan.CountSpan
        });
        visitInner.Instructions.Add(new MirCall
        {
            Target = accepted,
            Function = plan.Predicate,
            Arguments = [innerElement with { TypeId = plan.PredicateParameterType }],
            Span = plan.CountSpan
        });
        visitInner.Terminator = BoolSwitch(accepted, countAccepted.Id, countRejected.Id, span);

        countAccepted.Instructions.Add(new MirBinOp
        {
            Target = nextCount,
            Operator = BinaryOp.Add,
            Left = count,
            Right = IntConstant(1, span),
            Span = span
        });
        countAccepted.Instructions.Add(new MirStore
        {
            Target = count,
            Value = nextCount,
            Span = span
        });
        countAccepted.Terminator = new MirGoto { Target = incrementInner.Id, Span = span };
        countRejected.Terminator = new MirGoto { Target = incrementInner.Id, Span = span };

        incrementInner.Instructions.Add(new MirBinOp
        {
            Target = nextInnerIndex,
            Operator = BinaryOp.Add,
            Left = innerIndex,
            Right = IntConstant(1, span),
            Span = span
        });
        incrementInner.Instructions.Add(new MirStore
        {
            Target = innerIndex,
            Value = nextInnerIndex,
            Span = span
        });
        incrementInner.Terminator = new MirGoto { Target = innerHeader.Id, Span = span };

        AppendOwnedCleanup(finishInner, span, innerSequence);
        finishInner.Terminator = new MirGoto { Target = incrementOuter.Id, Span = span };

        incrementOuter.Instructions.Add(new MirBinOp
        {
            Target = nextOuterIndex,
            Operator = BinaryOp.Add,
            Left = outerIndex,
            Right = IntConstant(1, span),
            Span = span
        });
        incrementOuter.Instructions.Add(new MirStore
        {
            Target = outerIndex,
            Value = nextOuterIndex,
            Span = span
        });
        incrementOuter.Terminator = new MirGoto { Target = outerHeader.Id, Span = span };

        exit.Instructions.Add(new MirMove
        {
            Target = plan.CountTarget,
            Source = count,
            Span = span
        });
        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };
    }

    private static void ApplyFlatMapDirectSinkPlan(
        MirModule module,
        MirFunc function,
        FlatMapDirectSinkPlan plan)
    {
        var span = plan.SinkSpan;
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var nextLocalValue = function.Locals.Count == 0
            ? 1
            : function.Locals.Max(static local => local.Id.Value) + 1;
        var nextBlockValue = function.BasicBlocks.Count == 0
            ? 1
            : function.BasicBlocks.Max(static block => block.Id.Value) + 1;

        MirPlace NewLocal(string name, TypeId type)
        {
            var id = new LocalId { Value = nextLocalValue++ };
            function.Locals.Add(new MirLocal { Id = id, Name = name, TypeId = type, Span = span });
            return new MirPlace { Kind = PlaceKind.Local, Local = id, TypeId = type, Span = span };
        }

        MirBasicBlock NewBlock()
        {
            var block = new MirBasicBlock { Id = new BlockId { Value = nextBlockValue++ }, Span = span };
            function.BasicBlocks.Add(block);
            return block;
        }

        var outerLength = NewLocal("__sequence_flat_map_outer_length", intType);
        var outerIndex = NewLocal("__sequence_flat_map_outer_index", intType);
        var outerExhausted = NewLocal("__sequence_flat_map_outer_exhausted", boolType);
        var outerElement = NewLocal("__sequence_flat_map_outer_element", plan.OuterElementType);
        var innerSequence = NewLocal("__sequence_flat_map_inner_sequence", plan.InnerSequenceType);
        var innerLength = NewLocal("__sequence_flat_map_inner_length", intType);
        var innerIndex = NewLocal("__sequence_flat_map_inner_index", intType);
        var innerExhausted = NewLocal("__sequence_flat_map_inner_exhausted", boolType);
        var innerElement = NewLocal("__sequence_flat_map_inner_element", plan.InnerElementType);
        var callbackResult = plan.Kind == DirectSequenceSinkKind.ForEach
            ? null
            : NewLocal("__sequence_flat_map_callback_result", boolType);
        var accumulator = plan.Kind switch
        {
            DirectSequenceSinkKind.Any => NewLocal("__sequence_flat_map_any", boolType),
            DirectSequenceSinkKind.All => NewLocal("__sequence_flat_map_all", boolType),
            _ => null
        };
        var nextInnerIndex = NewLocal("__sequence_flat_map_next_inner_index", intType);
        var nextOuterIndex = NewLocal("__sequence_flat_map_next_outer_index", intType);

        var continuation = NewBlock();
        continuation.Instructions.AddRange(plan.Block.Instructions.Skip(plan.SinkInstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;
        var outerHeader = NewBlock();
        var mapOuter = NewBlock();
        var innerHeader = NewBlock();
        var visitInner = NewBlock();
        var incrementInner = NewBlock();
        var finishInner = NewBlock();
        var incrementOuter = NewBlock();
        var exit = NewBlock();
        var decisive = plan.Kind is DirectSequenceSinkKind.Find or DirectSequenceSinkKind.Any
            ? NewBlock()
            : null;
        var reject = plan.Kind is DirectSequenceSinkKind.Find or DirectSequenceSinkKind.All
            ? NewBlock()
            : null;

        plan.Block.Instructions.RemoveRange(
            plan.FlatMapInstructionIndex,
            plan.Block.Instructions.Count - plan.FlatMapInstructionIndex);
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(
            outerLength,
            plan.Source,
            plan.FlatMapSpan));
        plan.Block.Instructions.Add(new MirAssign
        {
            Target = outerIndex,
            Source = IntConstant(0, plan.FlatMapSpan),
            Span = plan.FlatMapSpan
        });
        if (accumulator != null)
        {
            plan.Block.Instructions.Add(new MirAssign
            {
                Target = accumulator,
                Source = BoolConstant(plan.Kind == DirectSequenceSinkKind.All, span),
                Span = span
            });
        }
        plan.Block.Terminator = new MirGoto { Target = outerHeader.Id, Span = span };

        outerHeader.Instructions.Add(new MirBinOp
        {
            Target = outerExhausted,
            Operator = BinaryOp.Ge,
            Left = outerIndex,
            Right = outerLength,
            Span = span
        });
        outerHeader.Terminator = BoolSwitch(outerExhausted, exit.Id, mapOuter.Id, span);

        mapOuter.Instructions.Add(new MirLoad
        {
            Target = outerElement,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = plan.Source,
                Index = outerIndex,
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = plan.OuterElementType,
                Span = plan.FlatMapSpan
            },
            CreatesBorrowAlias = false,
            MovesOutOfSource = false,
            Span = plan.FlatMapSpan
        });
        mapOuter.Instructions.Add(new MirCall
        {
            Target = innerSequence,
            Function = plan.Mapper,
            Arguments = [outerElement],
            Span = plan.FlatMapSpan
        });
        mapOuter.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(
            innerLength,
            innerSequence,
            plan.FlatMapSpan));
        mapOuter.Instructions.Add(new MirAssign
        {
            Target = innerIndex,
            Source = IntConstant(0, plan.FlatMapSpan),
            Span = plan.FlatMapSpan
        });
        mapOuter.Terminator = new MirGoto { Target = innerHeader.Id, Span = span };

        innerHeader.Instructions.Add(new MirBinOp
        {
            Target = innerExhausted,
            Operator = BinaryOp.Ge,
            Left = innerIndex,
            Right = innerLength,
            Span = span
        });
        innerHeader.Terminator = BoolSwitch(innerExhausted, finishInner.Id, visitInner.Id, span);

        visitInner.Instructions.Add(new MirLoad
        {
            Target = innerElement,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = innerSequence,
                Index = innerIndex,
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = plan.InnerElementType,
                Span = span
            },
            CreatesBorrowAlias = false,
            MovesOutOfSource = plan.Kind == DirectSequenceSinkKind.Find,
            Span = span
        });
        if (plan.Kind == DirectSequenceSinkKind.ForEach)
        {
            visitInner.Instructions.Add(new MirCall
            {
                Target = null,
                Function = plan.Predicate,
                Arguments = [innerElement with { TypeId = plan.PredicateParameterType }],
                Span = span
            });
            visitInner.Terminator = new MirGoto { Target = incrementInner.Id, Span = span };
        }
        else
        {
            visitInner.Instructions.Add(new MirCall
            {
                Target = callbackResult,
                Function = plan.Predicate,
                Arguments = [innerElement with { TypeId = plan.PredicateParameterType }],
                BorrowedArgumentIndices = plan.Kind == DirectSequenceSinkKind.Find
                    ? new HashSet<int> { 0 }
                    : [],
                Span = span
            });
            visitInner.Terminator = plan.Kind switch
            {
                DirectSequenceSinkKind.Find => BoolSwitch(callbackResult!, decisive!.Id, reject!.Id, span),
                DirectSequenceSinkKind.Any => BoolSwitch(callbackResult!, decisive!.Id, incrementInner.Id, span),
                DirectSequenceSinkKind.All => BoolSwitch(callbackResult!, incrementInner.Id, reject!.Id, span),
                _ => throw new InvalidOperationException()
            };
        }

        if (plan.Kind == DirectSequenceSinkKind.Find)
        {
            AppendOwnedCleanup(reject!, span, innerElement);
            reject!.Terminator = new MirGoto { Target = incrementInner.Id, Span = span };
            decisive!.Instructions.Add(new MirCall
            {
                Target = plan.ResultTarget,
                Function = CreateOptionConstructor(module, plan.ResultTarget.TypeId, "Some", span),
                Arguments = [innerElement],
                Span = span
            });
            AppendOwnedCleanup(decisive, span, innerSequence);
            decisive.Terminator = new MirGoto { Target = continuation.Id, Span = span };
        }
        else if (plan.Kind == DirectSequenceSinkKind.Any)
        {
            decisive!.Instructions.Add(new MirAssign
            {
                Target = accumulator!,
                Source = BoolConstant(true, span),
                Span = span
            });
            AppendOwnedCleanup(decisive, span, innerSequence);
            decisive.Terminator = new MirGoto { Target = exit.Id, Span = span };
        }
        else if (plan.Kind == DirectSequenceSinkKind.All)
        {
            reject!.Instructions.Add(new MirAssign
            {
                Target = accumulator!,
                Source = BoolConstant(false, span),
                Span = span
            });
            AppendOwnedCleanup(reject, span, innerSequence);
            reject.Terminator = new MirGoto { Target = exit.Id, Span = span };
        }

        incrementInner.Instructions.Add(new MirBinOp
        {
            Target = nextInnerIndex,
            Operator = BinaryOp.Add,
            Left = innerIndex,
            Right = IntConstant(1, span),
            Span = span
        });
        incrementInner.Instructions.Add(new MirStore
        {
            Target = innerIndex,
            Value = nextInnerIndex,
            Span = span
        });
        incrementInner.Terminator = new MirGoto { Target = innerHeader.Id, Span = span };

        AppendOwnedCleanup(finishInner, span, innerSequence);
        finishInner.Terminator = new MirGoto { Target = incrementOuter.Id, Span = span };
        incrementOuter.Instructions.Add(new MirBinOp
        {
            Target = nextOuterIndex,
            Operator = BinaryOp.Add,
            Left = outerIndex,
            Right = IntConstant(1, span),
            Span = span
        });
        incrementOuter.Instructions.Add(new MirStore
        {
            Target = outerIndex,
            Value = nextOuterIndex,
            Span = span
        });
        incrementOuter.Terminator = new MirGoto { Target = outerHeader.Id, Span = span };

        if (plan.Kind == DirectSequenceSinkKind.Find)
        {
            exit.Instructions.Add(new MirCall
            {
                Target = plan.ResultTarget,
                Function = CreateOptionConstructor(module, plan.ResultTarget.TypeId, "None", span),
                Arguments = [],
                Span = span
            });
        }
        else if (plan.Kind == DirectSequenceSinkKind.ForEach)
        {
            exit.Instructions.Add(new MirAssign
            {
                Target = plan.ResultTarget,
                Source = new MirConstant
                {
                    Value = new MirConstantValue.UnitValue(),
                    TypeId = unitType,
                    Span = span
                },
                Span = span
            });
        }
        else
        {
            exit.Instructions.Add(new MirMove { Target = plan.ResultTarget, Source = accumulator!, Span = span });
        }
        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };
    }

    private static void ApplyFlatMapFoldPlan(MirFunc function, FlatMapFoldPlan plan)
    {
        var span = plan.FoldSpan;
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var nextLocalValue = function.Locals.Count == 0
            ? 1
            : function.Locals.Max(static local => local.Id.Value) + 1;
        var nextBlockValue = function.BasicBlocks.Count == 0
            ? 1
            : function.BasicBlocks.Max(static block => block.Id.Value) + 1;

        MirPlace NewLocal(string name, TypeId type)
        {
            var id = new LocalId { Value = nextLocalValue++ };
            function.Locals.Add(new MirLocal { Id = id, Name = name, TypeId = type, Span = span });
            return new MirPlace { Kind = PlaceKind.Local, Local = id, TypeId = type, Span = span };
        }
        MirBasicBlock NewBlock()
        {
            var block = new MirBasicBlock { Id = new BlockId { Value = nextBlockValue++ }, Span = span };
            function.BasicBlocks.Add(block);
            return block;
        }

        var outerLength = NewLocal("__sequence_flat_map_fold_outer_length", intType);
        var outerIndex = NewLocal("__sequence_flat_map_fold_outer_index", intType);
        var outerExhausted = NewLocal("__sequence_flat_map_fold_outer_exhausted", boolType);
        var outerElement = NewLocal("__sequence_flat_map_fold_outer_element", plan.OuterElementType);
        var innerSequence = NewLocal("__sequence_flat_map_fold_inner_sequence", plan.InnerSequenceType);
        var innerLength = NewLocal("__sequence_flat_map_fold_inner_length", intType);
        var innerIndex = NewLocal("__sequence_flat_map_fold_inner_index", intType);
        var innerExhausted = NewLocal("__sequence_flat_map_fold_inner_exhausted", boolType);
        var innerElement = NewLocal("__sequence_flat_map_fold_inner_element", plan.InnerElementType);
        var accumulator = NewLocal("__sequence_flat_map_fold_accumulator", plan.AccumulatorType);
        var accumulatorArgument = NewLocal("__sequence_flat_map_fold_accumulator_argument", plan.AccumulatorType);
        var nextAccumulator = NewLocal("__sequence_flat_map_fold_next_accumulator", plan.AccumulatorType);
        var nextAccumulatorValue = NewLocal("__sequence_flat_map_fold_next_accumulator_value", plan.AccumulatorType);
        var nextInnerIndex = NewLocal("__sequence_flat_map_fold_next_inner_index", intType);
        var nextOuterIndex = NewLocal("__sequence_flat_map_fold_next_outer_index", intType);

        var continuation = NewBlock();
        continuation.Instructions.AddRange(plan.Block.Instructions.Skip(plan.FoldInstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;
        var outerHeader = NewBlock();
        var mapOuter = NewBlock();
        var innerHeader = NewBlock();
        var reduce = NewBlock();
        var incrementInner = NewBlock();
        var finishInner = NewBlock();
        var incrementOuter = NewBlock();
        var exit = NewBlock();

        plan.Block.Instructions.RemoveRange(plan.FlatMapInstructionIndex, plan.Block.Instructions.Count - plan.FlatMapInstructionIndex);
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(outerLength, plan.Source, plan.FlatMapSpan));
        plan.Block.Instructions.Add(new MirAssign { Target = outerIndex, Source = IntConstant(0, span), Span = span });
        plan.Block.Instructions.Add(CreateTransfer(accumulator, plan.Initial, span));
        plan.Block.Terminator = new MirGoto { Target = outerHeader.Id, Span = span };

        outerHeader.Instructions.Add(new MirBinOp { Target = outerExhausted, Operator = BinaryOp.Ge, Left = outerIndex, Right = outerLength, Span = span });
        outerHeader.Terminator = BoolSwitch(outerExhausted, exit.Id, mapOuter.Id, span);
        mapOuter.Instructions.Add(new MirLoad
        {
            Target = outerElement,
            Source = new MirPlace { Kind = PlaceKind.Index, Base = plan.Source, Index = outerIndex, IndexAccessKind = MirIndexAccessKind.RuntimeArray, TypeId = plan.OuterElementType, Span = plan.FlatMapSpan },
            CreatesBorrowAlias = false,
            Span = plan.FlatMapSpan
        });
        mapOuter.Instructions.Add(new MirCall { Target = innerSequence, Function = plan.Mapper, Arguments = [outerElement], Span = plan.FlatMapSpan });
        mapOuter.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(innerLength, innerSequence, plan.FlatMapSpan));
        mapOuter.Instructions.Add(new MirAssign { Target = innerIndex, Source = IntConstant(0, span), Span = span });
        mapOuter.Terminator = new MirGoto { Target = innerHeader.Id, Span = span };

        innerHeader.Instructions.Add(new MirBinOp { Target = innerExhausted, Operator = BinaryOp.Ge, Left = innerIndex, Right = innerLength, Span = span });
        innerHeader.Terminator = BoolSwitch(innerExhausted, finishInner.Id, reduce.Id, span);
        reduce.Instructions.Add(new MirLoad
        {
            Target = innerElement,
            Source = new MirPlace { Kind = PlaceKind.Index, Base = innerSequence, Index = innerIndex, IndexAccessKind = MirIndexAccessKind.RuntimeArray, TypeId = plan.InnerElementType, Span = span },
            CreatesBorrowAlias = false,
            Span = span
        });
        reduce.Instructions.Add(new MirMove { Target = accumulatorArgument, Source = accumulator, Span = span });
        reduce.Instructions.Add(new MirCall { Target = nextAccumulator, Function = plan.Reducer, Arguments = [accumulatorArgument, innerElement], Span = span });
        reduce.Instructions.Add(new MirMove { Target = nextAccumulatorValue, Source = nextAccumulator, Span = span });
        reduce.Instructions.Add(new MirStore { Target = accumulator, Value = nextAccumulatorValue, Span = span });
        reduce.Terminator = new MirGoto { Target = incrementInner.Id, Span = span };

        incrementInner.Instructions.Add(new MirBinOp { Target = nextInnerIndex, Operator = BinaryOp.Add, Left = innerIndex, Right = IntConstant(1, span), Span = span });
        incrementInner.Instructions.Add(new MirStore { Target = innerIndex, Value = nextInnerIndex, Span = span });
        incrementInner.Terminator = new MirGoto { Target = innerHeader.Id, Span = span };
        AppendOwnedCleanup(finishInner, span, innerSequence);
        finishInner.Terminator = new MirGoto { Target = incrementOuter.Id, Span = span };
        incrementOuter.Instructions.Add(new MirBinOp { Target = nextOuterIndex, Operator = BinaryOp.Add, Left = outerIndex, Right = IntConstant(1, span), Span = span });
        incrementOuter.Instructions.Add(new MirStore { Target = outerIndex, Value = nextOuterIndex, Span = span });
        incrementOuter.Terminator = new MirGoto { Target = outerHeader.Id, Span = span };
        exit.Instructions.Add(new MirMove { Target = plan.ResultTarget, Source = accumulator, Span = span });
        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };
    }

    private static void ApplyFlatMapCollectPlan(
        MirModule module,
        MirFunc function,
        FlatMapCollectPlan plan)
    {
        var span = plan.FlatMapSpan;
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var nextLocalValue = function.Locals.Count == 0
            ? 1
            : function.Locals.Max(static local => local.Id.Value) + 1;
        var nextBlockValue = function.BasicBlocks.Count == 0
            ? 1
            : function.BasicBlocks.Max(static block => block.Id.Value) + 1;

        MirPlace NewLocal(string name, TypeId type)
        {
            var id = new LocalId { Value = nextLocalValue++ };
            function.Locals.Add(new MirLocal { Id = id, Name = name, TypeId = type, Span = span });
            return new MirPlace { Kind = PlaceKind.Local, Local = id, TypeId = type, Span = span };
        }

        MirBasicBlock NewBlock()
        {
            var block = new MirBasicBlock { Id = new BlockId { Value = nextBlockValue++ }, Span = span };
            function.BasicBlocks.Add(block);
            return block;
        }

        var outerLength = NewLocal("__sequence_flat_map_collect_outer_length", intType);
        var outerIndex = NewLocal("__sequence_flat_map_collect_outer_index", intType);
        var outerExhausted = NewLocal("__sequence_flat_map_collect_outer_exhausted", boolType);
        var outerElement = NewLocal("__sequence_flat_map_collect_outer_element", plan.OuterElementType);
        var innerSequence = NewLocal("__sequence_flat_map_collect_inner_sequence", plan.InnerSequenceType);
        var innerLength = NewLocal("__sequence_flat_map_collect_inner_length", intType);
        var innerIndex = NewLocal("__sequence_flat_map_collect_inner_index", intType);
        var innerExhausted = NewLocal("__sequence_flat_map_collect_inner_exhausted", boolType);
        var innerElement = NewLocal("__sequence_flat_map_collect_inner_element", plan.InnerElementType);
        var collector = NewLocal("__sequence_flat_map_collector", plan.ResultTarget.TypeId);
        var nextInnerIndex = NewLocal("__sequence_flat_map_collect_next_inner_index", intType);
        var nextOuterIndex = NewLocal("__sequence_flat_map_collect_next_outer_index", intType);

        var continuation = NewBlock();
        continuation.Instructions.AddRange(plan.Block.Instructions.Skip(plan.CollectInstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;
        var outerHeader = NewBlock();
        var mapOuter = NewBlock();
        var innerHeader = NewBlock();
        var appendInner = NewBlock();
        var incrementInner = NewBlock();
        var finishInner = NewBlock();
        var incrementOuter = NewBlock();
        var exit = NewBlock();

        plan.Block.Instructions.RemoveRange(
            plan.FlatMapInstructionIndex,
            plan.Block.Instructions.Count - plan.FlatMapInstructionIndex);
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(
            outerLength,
            plan.Source,
            span));
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayNewCall(
            collector,
            IntConstant(RuntimeSequenceBuildLowering.DefaultUnknownCapacity, span),
            IntConstant(plan.InnerElementSize, span),
            span));
        plan.Block.Instructions.Add(new MirAssign { Target = outerIndex, Source = IntConstant(0, span), Span = span });
        plan.Block.Terminator = new MirGoto { Target = outerHeader.Id, Span = span };

        outerHeader.Instructions.Add(new MirBinOp
        {
            Target = outerExhausted,
            Operator = BinaryOp.Ge,
            Left = outerIndex,
            Right = outerLength,
            Span = span
        });
        outerHeader.Terminator = BoolSwitch(outerExhausted, exit.Id, mapOuter.Id, span);

        mapOuter.Instructions.Add(new MirLoad
        {
            Target = outerElement,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = plan.Source,
                Index = outerIndex,
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = plan.OuterElementType,
                Span = span
            },
            CreatesBorrowAlias = false,
            MovesOutOfSource = plan.OuterMovesOutOfSource,
            Span = span
        });
        mapOuter.Instructions.Add(new MirCall
        {
            Target = innerSequence,
            Function = plan.Mapper,
            Arguments = [outerElement],
            Span = span
        });
        mapOuter.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(innerLength, innerSequence, span));
        mapOuter.Instructions.Add(new MirAssign { Target = innerIndex, Source = IntConstant(0, span), Span = span });
        mapOuter.Terminator = new MirGoto { Target = innerHeader.Id, Span = span };

        innerHeader.Instructions.Add(new MirBinOp
        {
            Target = innerExhausted,
            Operator = BinaryOp.Ge,
            Left = innerIndex,
            Right = innerLength,
            Span = span
        });
        innerHeader.Terminator = BoolSwitch(innerExhausted, finishInner.Id, appendInner.Id, span);

        appendInner.Instructions.Add(new MirLoad
        {
            Target = innerElement,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = innerSequence,
                Index = innerIndex,
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = plan.InnerElementType,
                Span = span
            },
            CreatesBorrowAlias = false,
            MovesOutOfSource = !IsCopyType(module, plan.InnerElementType),
            Span = span
        });
        appendInner.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayPushCall(
            collector,
            collector,
            innerElement,
            IntConstant(plan.InnerElementSize, span),
            span));
        appendInner.Terminator = new MirGoto { Target = incrementInner.Id, Span = span };

        incrementInner.Instructions.Add(new MirBinOp
        {
            Target = nextInnerIndex,
            Operator = BinaryOp.Add,
            Left = innerIndex,
            Right = IntConstant(1, span),
            Span = span
        });
        incrementInner.Instructions.Add(new MirStore { Target = innerIndex, Value = nextInnerIndex, Span = span });
        incrementInner.Terminator = new MirGoto { Target = innerHeader.Id, Span = span };

        AppendOwnedCleanupAndGoto(finishInner, incrementOuter.Id, span, innerSequence);
        incrementOuter.Instructions.Add(new MirBinOp
        {
            Target = nextOuterIndex,
            Operator = BinaryOp.Add,
            Left = outerIndex,
            Right = IntConstant(1, span),
            Span = span
        });
        incrementOuter.Instructions.Add(new MirStore { Target = outerIndex, Value = nextOuterIndex, Span = span });
        incrementOuter.Terminator = new MirGoto { Target = outerHeader.Id, Span = span };

        exit.Instructions.Add(new MirMove { Target = plan.ResultTarget, Source = collector, Span = span });
        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };
    }
}
