using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

public sealed partial class SequencePipelineFusionPass
{
    private static void ApplyPlan(MirModule module, MirFunc function, SequencePipelinePlan plan)
    {
        if (plan.UnifiedPlan is { } unified)
        {
            if (unified.StoragePlan.PreferSourceReuse &&
                (!unified.ProofSummary.OwnershipSafe ||
                 !unified.OwnershipRoute.SourceMustOwned ||
                 !unified.OwnershipRoute.SourceMustUnique ||
                 !unified.OwnershipRoute.NoAlias ||
                 !unified.OwnershipRoute.NoActiveBorrow ||
                 !unified.OwnershipRoute.NoEscape))
            {
                throw new InvalidOperationException("Sequence storage reuse requires a complete ownership proof.");
            }
        }

        switch (plan)
        {
            case DropDropPlan dropDrop:
                ApplyDropDropPlan(dropDrop);
                break;
            case TakeTakePlan takeTake:
                ApplyTakeTakePlan(takeTake);
                break;
            case TakeHeadPlan takeHead:
                ApplyTakeHeadPlan(takeHead);
                break;
            case FilterHeadPlan filterHead:
                ApplyFilterHeadPlan(filterHead);
                break;
            case FilterTakeHeadPlan filterTakeHead:
                ApplyFilterTakeHeadPlan(filterTakeHead);
                break;
            case ZipWithFoldPlan zipWithFold:
                ApplyZipWithFoldPlan(function, zipWithFold);
                break;
            case FlatMapCountPlan flatMapCount:
                ApplyFlatMapCountPlan(function, flatMapCount);
                break;
            case FlatMapDirectSinkPlan flatMapSink:
                ApplyFlatMapDirectSinkPlan(module, function, flatMapSink);
                break;
            case FlatMapFoldPlan flatMapFold:
                ApplyFlatMapFoldPlan(function, flatMapFold);
                break;
            case FlatMapCollectPlan flatMapCollect:
                ApplyFlatMapCollectPlan(module, function, flatMapCollect);
                break;
            case DirectZipSequenceSinkPlan zipSink:
                ApplyDirectZipSequenceSinkPlan(module, function, zipSink);
                break;
            case DirectPartitionPlan partition:
                ApplyDirectPartitionPlan(module, function, partition);
                break;
            case MapFilterFoldPlan fold:
                ApplyFoldPlan(function, fold);
                break;
            case MapFoldPlan mapFold:
                ApplyMapFoldPlan(function, mapFold);
                break;
            case MapFilterCollectPlan collect:
                ApplyCollectPlan(function, collect);
                break;
            case DirectFoldPlan directFold:
                ApplyDirectFoldPlan(function, directFold);
                break;
            case DirectSequenceSinkPlan directSink:
                ApplyDirectSequenceSinkPlan(module, function, directSink);
                break;
            default:
                throw new InvalidOperationException($"Unsupported sequence pipeline plan '{plan.GetType().Name}'.");
        }
    }

    private static void ApplyDropDropPlan(DropDropPlan plan)
    {
        var block = plan.Block;
        var removeCount = plan.SecondInstructionIndex - plan.FirstInstructionIndex + 1;
        block.Instructions.RemoveRange(plan.FirstInstructionIndex, removeCount);
        block.Instructions.Insert(
            plan.FirstInstructionIndex,
            new MirCall
            {
                Target = plan.ResultTarget,
                Function = plan.DropFunction,
                Arguments = [plan.Source, IntConstant(plan.Bound, plan.SecondSpan)],
                Span = plan.SecondSpan
            });
    }

    private static void ApplyTakeTakePlan(TakeTakePlan plan)
    {
        var block = plan.Block;
        var removeCount = plan.SecondInstructionIndex - plan.FirstInstructionIndex + 1;
        block.Instructions.RemoveRange(plan.FirstInstructionIndex, removeCount);
        block.Instructions.Insert(
            plan.FirstInstructionIndex,
            new MirCall
            {
                Target = plan.ResultTarget,
                Function = plan.TakeFunction,
                Arguments = [plan.Source, IntConstant(plan.Bound, plan.SecondSpan)],
                Span = plan.SecondSpan
            });
    }

    private static void ApplyTakeHeadPlan(TakeHeadPlan plan)
    {
        var block = plan.Block;
        var removeCount = plan.HeadInstructionIndex - plan.TakeInstructionIndex + 1;
        block.Instructions.RemoveRange(plan.TakeInstructionIndex, removeCount);
        block.Instructions.Insert(
            plan.TakeInstructionIndex,
            new MirCall
            {
                Target = plan.HeadTarget,
                Function = plan.HeadFunction,
                Arguments = [plan.Source],
                Span = plan.HeadSpan
            });
    }

    private static void ApplyFilterHeadPlan(FilterHeadPlan plan)
    {
        var block = plan.Block;
        var removeCount = plan.HeadInstructionIndex - plan.FilterInstructionIndex + 1;
        block.Instructions.RemoveRange(plan.FilterInstructionIndex, removeCount);
        block.Instructions.Insert(
            plan.FilterInstructionIndex,
            new MirCall
            {
                Target = plan.HeadTarget,
                Function = plan.FindFunction,
                Arguments = [plan.Source, plan.Predicate],
                Span = plan.HeadSpan
        });
    }

    private static void ApplyFilterTakeHeadPlan(FilterTakeHeadPlan plan)
    {
        var block = plan.Block;
        var removeCount = plan.HeadInstructionIndex - plan.FilterInstructionIndex + 1;
        block.Instructions.RemoveRange(plan.FilterInstructionIndex, removeCount);
        block.Instructions.Insert(
            plan.FilterInstructionIndex,
            new MirCall
            {
                Target = plan.HeadTarget,
                Function = plan.FindFunction,
                Arguments = [plan.Source, plan.Predicate],
                Span = plan.HeadSpan
        });
    }

    private static void ApplyZipWithFoldPlan(MirFunc function, ZipWithFoldPlan plan)
    {
        var span = plan.FoldSpan;
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var nextLocalValue = function.Locals.Count == 0 ? 1 : function.Locals.Max(static local => local.Id.Value) + 1;
        var nextBlockValue = function.BasicBlocks.Max(static block => block.Id.Value) + 1;
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

        var leftLength = NewLocal("__sequence_zip_left_length", intType);
        var rightLength = NewLocal("__sequence_zip_right_length", intType);
        var length = NewLocal("__sequence_zip_length", intType);
        var shorter = NewLocal("__sequence_zip_left_shorter", boolType);
        var index = NewLocal("__sequence_zip_index", intType);
        var exhausted = NewLocal("__sequence_zip_exhausted", boolType);
        var leftElement = NewLocal("__sequence_zip_left_element", plan.LeftElementType);
        var rightElement = NewLocal("__sequence_zip_right_element", plan.RightElementType);
        var combined = NewLocal("__sequence_zip_combined", plan.CombinedElementType);
        var accumulator = NewLocal("__sequence_zip_accumulator", plan.AccumulatorType);
        var accumulatorArgument = NewLocal("__sequence_zip_accumulator_argument", plan.AccumulatorType);
        var nextAccumulator = NewLocal("__sequence_zip_next_accumulator", plan.AccumulatorType);
        var nextAccumulatorValue = NewLocal("__sequence_zip_next_accumulator_value", plan.AccumulatorType);
        var nextIndex = NewLocal("__sequence_zip_next_index", intType);

        var continuation = NewBlock();
        continuation.Instructions.AddRange(plan.Block.Instructions.Skip(plan.EndInstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;
        var header = NewBlock();
        var zip = NewBlock();
        var reduce = NewBlock();
        var increment = NewBlock();
        var exit = NewBlock();

        plan.Block.Instructions.RemoveRange(plan.StartInstructionIndex, plan.Block.Instructions.Count - plan.StartInstructionIndex);
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(leftLength, plan.LeftSource, plan.ZipSpan));
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(rightLength, plan.RightSource, plan.ZipSpan));
        plan.Block.Instructions.Add(new MirBinOp { Target = shorter, Operator = BinaryOp.Lt, Left = leftLength, Right = rightLength, Span = span });
        plan.Block.Instructions.Add(new MirSelect { Target = length, Condition = shorter, TrueValue = leftLength, FalseValue = rightLength, Span = span });
        plan.Block.Instructions.Add(new MirAssign { Target = index, Source = IntConstant(0, span), Span = span });
        plan.Block.Instructions.Add(CreateTransfer(accumulator, plan.Initial, span));
        plan.Block.Terminator = new MirGoto { Target = header.Id, Span = span };

        header.Instructions.Add(new MirBinOp { Target = exhausted, Operator = BinaryOp.Ge, Left = index, Right = length, Span = span });
        header.Terminator = BoolSwitch(exhausted, exit.Id, zip.Id, span);
        zip.Instructions.Add(new MirLoad { Target = leftElement, Source = new MirPlace { Kind = PlaceKind.Index, Base = plan.LeftSource, Index = index, IndexAccessKind = MirIndexAccessKind.RuntimeArray, TypeId = plan.LeftElementType, Span = span }, CreatesBorrowAlias = false, MovesOutOfSource = plan.MovesLeftOutOfSource, Span = span });
        zip.Instructions.Add(new MirLoad { Target = rightElement, Source = new MirPlace { Kind = PlaceKind.Index, Base = plan.RightSource, Index = index, IndexAccessKind = MirIndexAccessKind.RuntimeArray, TypeId = plan.RightElementType, Span = span }, CreatesBorrowAlias = false, MovesOutOfSource = plan.MovesRightOutOfSource, Span = span });
        zip.Instructions.Add(new MirCall { Target = combined, Function = plan.Combiner, Arguments = [leftElement, rightElement], Span = plan.ZipSpan });
        zip.Terminator = new MirGoto { Target = reduce.Id, Span = span };
        reduce.Instructions.Add(plan.MovesAccumulatorOutOfSource
            ? new MirMove { Target = accumulatorArgument, Source = accumulator, Span = span }
            : new MirCopy { Target = accumulatorArgument, Source = accumulator, Span = span });
        reduce.Instructions.Add(new MirCall { Target = nextAccumulator, Function = plan.Reducer, Arguments = [accumulatorArgument, combined], Span = span });
        reduce.Instructions.Add(new MirMove { Target = nextAccumulatorValue, Source = nextAccumulator, Span = span });
        reduce.Instructions.Add(new MirStore { Target = accumulator, Value = nextAccumulatorValue, Span = span });
        reduce.Terminator = new MirGoto { Target = increment.Id, Span = span };
        increment.Instructions.Add(new MirBinOp { Target = nextIndex, Operator = BinaryOp.Add, Left = index, Right = IntConstant(1, span), Span = span });
        increment.Instructions.Add(new MirStore { Target = index, Value = nextIndex, Span = span });
        increment.Terminator = new MirGoto { Target = header.Id, Span = span };
        exit.Instructions.Add(new MirMove { Target = plan.FoldTarget, Source = accumulator, Span = span });
        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };
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

    private static void ApplyMapFoldPlan(MirFunc function, MapFoldPlan plan)
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
        var increment = NewBlock();
        var exit = NewBlock();

        plan.Block.Instructions.RemoveRange(
            plan.StartInstructionIndex,
            plan.Block.Instructions.Count - plan.StartInstructionIndex);
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(
            length,
            plan.Source,
            plan.MapSpan));
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
        map.Terminator = new MirGoto { Target = reduce.Id, Span = plan.MapSpan };

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

}
