using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

public sealed partial class SequencePipelineFusionPass
{
    /// <summary>
    /// Lowers a canonical direct sink into one source loop.  The callback is
    /// invoked in source order, and short-circuit sinks branch directly to the
    /// continuation block on their decisive result.
    /// </summary>
    private static void ApplyDirectSequenceSinkPlan(
        MirModule module,
        MirFunc function,
        DirectSequenceSinkPlan plan)
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

        var length = NewLocal("__sequence_sink_length", intType);
        var viewLength = plan.Stages.Count == 0
            ? length
            : NewLocal("__sequence_sink_view_length", intType);
        var viewOffset = plan.Stages.Count == 0
            ? null
            : NewLocal("__sequence_sink_view_offset", intType);
        var index = NewLocal("__sequence_sink_index", intType);
        var exhausted = NewLocal("__sequence_sink_exhausted", boolType);
        var element = NewLocal("__sequence_sink_element", plan.ElementType);
        var callbackResult = plan.Kind == DirectSequenceSinkKind.ForEach
            ? null
            : NewLocal("__sequence_sink_callback_result", boolType);
        var accumulator = plan.Kind switch
        {
            DirectSequenceSinkKind.Any => NewLocal("__sequence_sink_any", boolType),
            DirectSequenceSinkKind.All => NewLocal("__sequence_sink_all", boolType),
            DirectSequenceSinkKind.Count => NewLocal("__sequence_sink_count", intType),
            _ => null
        };
        var nextIndex = NewLocal("__sequence_sink_next_index", intType);
        var physicalIndex = plan.Stages.Count == 0
            ? index
            : NewLocal("__sequence_sink_physical_index", intType);
        var nextCount = plan.Kind == DirectSequenceSinkKind.Count
            ? NewLocal("__sequence_sink_next_count", intType)
            : null;

        var continuation = NewBlock();
        continuation.Instructions.AddRange(plan.Block.Instructions.Skip(plan.InstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;

        var header = NewBlock();
        var body = NewBlock();
        var increment = NewBlock();
        var exit = NewBlock();
        var decisive = plan.Kind is DirectSequenceSinkKind.Find or DirectSequenceSinkKind.Any
            ? NewBlock()
            : null;
        var reject = plan.Kind is DirectSequenceSinkKind.Find or DirectSequenceSinkKind.All
            ? NewBlock()
            : null;

        plan.Block.Instructions.RemoveRange(
            plan.FirstInstructionIndex,
            plan.Block.Instructions.Count - plan.FirstInstructionIndex);
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(
            length,
            plan.Source,
            span));
        var isReversed = false;
        if (plan.Stages.Count > 0)
        {
            plan.Block.Instructions.Add(new MirAssign
            {
                Target = viewLength,
                Source = length,
                Span = span
            });
            plan.Block.Instructions.Add(new MirAssign
            {
                Target = viewOffset!,
                Source = IntConstant(0, span),
                Span = span
            });

            foreach (var stage in plan.Stages)
            {
                switch (stage)
                {
                    case SequenceReverseStagePlan:
                        isReversed = !isReversed;
                        break;
                    case SequenceTakeViewStagePlan take:
                    {
                        var bound = IntConstant(take.Bound, span);
                        var boundInRange = NewLocal("__sequence_sink_view_take_in_range", boolType);
                        var nextLength = NewLocal("__sequence_sink_view_take_length", intType);
                        plan.Block.Instructions.Add(new MirBinOp
                        {
                            Target = boundInRange,
                            Operator = BinaryOp.Lt,
                            Left = bound,
                            Right = viewLength,
                            Span = span
                        });
                        plan.Block.Instructions.Add(new MirSelect
                        {
                            Target = nextLength,
                            Condition = boundInRange,
                            TrueValue = bound,
                            FalseValue = viewLength,
                            Span = span
                        });
                        if (isReversed)
                        {
                            var skipped = NewLocal("__sequence_sink_view_take_skipped", intType);
                            var nextOffset = NewLocal("__sequence_sink_view_take_offset", intType);
                            plan.Block.Instructions.Add(new MirBinOp
                            {
                                Target = skipped,
                                Operator = BinaryOp.Sub,
                                Left = viewLength,
                                Right = nextLength,
                                Span = span
                            });
                            plan.Block.Instructions.Add(new MirBinOp
                            {
                                Target = nextOffset,
                                Operator = BinaryOp.Add,
                                Left = viewOffset!,
                                Right = skipped,
                                Span = span
                            });
                            plan.Block.Instructions.Add(new MirStore
                            {
                                Target = viewOffset!,
                                Value = nextOffset,
                                Span = span
                            });
                        }
                        plan.Block.Instructions.Add(new MirStore
                        {
                            Target = viewLength,
                            Value = nextLength,
                            Span = span
                        });
                        break;
                    }
                    case SequenceDropViewStagePlan drop:
                    {
                        var bound = IntConstant(drop.Bound, span);
                        var boundInRange = NewLocal("__sequence_sink_view_drop_in_range", boolType);
                        var amount = NewLocal("__sequence_sink_view_drop_amount", intType);
                        var nextLength = NewLocal("__sequence_sink_view_drop_length", intType);
                        plan.Block.Instructions.Add(new MirBinOp
                        {
                            Target = boundInRange,
                            Operator = BinaryOp.Lt,
                            Left = bound,
                            Right = viewLength,
                            Span = span
                        });
                        plan.Block.Instructions.Add(new MirSelect
                        {
                            Target = amount,
                            Condition = boundInRange,
                            TrueValue = bound,
                            FalseValue = viewLength,
                            Span = span
                        });
                        if (!isReversed)
                        {
                            var nextOffset = NewLocal("__sequence_sink_view_drop_offset", intType);
                            plan.Block.Instructions.Add(new MirBinOp
                            {
                                Target = nextOffset,
                                Operator = BinaryOp.Add,
                                Left = viewOffset!,
                                Right = amount,
                                Span = span
                            });
                            plan.Block.Instructions.Add(new MirStore
                            {
                                Target = viewOffset!,
                                Value = nextOffset,
                                Span = span
                            });
                        }
                        plan.Block.Instructions.Add(new MirBinOp
                        {
                            Target = nextLength,
                            Operator = BinaryOp.Sub,
                            Left = viewLength,
                            Right = amount,
                            Span = span
                        });
                        plan.Block.Instructions.Add(new MirStore
                        {
                            Target = viewLength,
                            Value = nextLength,
                            Span = span
                        });
                        break;
                    }
                }
            }
        }
        plan.Block.Instructions.Add(new MirAssign
        {
            Target = index,
            Source = IntConstant(0, span),
            Span = span
        });
        if (accumulator != null)
        {
            plan.Block.Instructions.Add(new MirAssign
            {
                Target = accumulator,
                Source = plan.Kind == DirectSequenceSinkKind.All
                    ? BoolConstant(true, span)
                    : IntOrBoolZero(plan.Kind, span),
                Span = span
            });
        }
        plan.Block.Terminator = new MirGoto { Target = header.Id, Span = span };

        header.Instructions.Add(new MirBinOp
        {
            Target = exhausted,
            Operator = BinaryOp.Ge,
            Left = index,
            Right = viewLength,
            Span = span
        });
        header.Terminator = BoolSwitch(exhausted, exit.Id, body.Id, span);

        if (plan.Stages.Count > 0)
        {
            if (isReversed)
            {
                var viewEnd = NewLocal("__sequence_sink_view_end", intType);
                var fromEnd = NewLocal("__sequence_sink_view_from_end", intType);
                body.Instructions.Add(new MirBinOp
                {
                    Target = viewEnd,
                    Operator = BinaryOp.Add,
                    Left = viewOffset!,
                    Right = viewLength,
                    Span = span
                });
                body.Instructions.Add(new MirBinOp
                {
                    Target = fromEnd,
                    Operator = BinaryOp.Sub,
                    Left = viewEnd,
                    Right = index,
                    Span = span
                });
                body.Instructions.Add(new MirBinOp
                {
                    Target = physicalIndex,
                    Operator = BinaryOp.Sub,
                    Left = fromEnd,
                    Right = IntConstant(1, span),
                    Span = span
                });
            }
            else
            {
                body.Instructions.Add(new MirBinOp
                {
                    Target = physicalIndex,
                    Operator = BinaryOp.Add,
                    Left = viewOffset!,
                    Right = index,
                    Span = span
                });
            }
        }
        body.Instructions.Add(new MirLoad
        {
            Target = element,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = plan.Source,
                Index = physicalIndex,
                IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                TypeId = plan.ElementType,
                Span = span
            },
            CreatesBorrowAlias = false,
            MovesOutOfSource = plan.Kind == DirectSequenceSinkKind.Find,
            Span = span
        });
        if (plan.Kind == DirectSequenceSinkKind.ForEach)
        {
            body.Instructions.Add(new MirCall
            {
                Target = null,
                Function = plan.Callback,
                Arguments = [element],
                Span = span
            });
            body.Terminator = new MirGoto { Target = increment.Id, Span = span };
        }
        else
        {
            body.Instructions.Add(new MirCall
            {
                Target = callbackResult,
                Function = plan.Callback,
                Arguments = [element with { TypeId = plan.CallbackParameterType }],
                Span = span
            });
            body.Terminator = plan.Kind switch
            {
                DirectSequenceSinkKind.Find => BoolSwitch(callbackResult!, decisive!.Id, rejectOrIncrement(reject, increment), span),
                DirectSequenceSinkKind.Any => BoolSwitch(callbackResult!, decisive!.Id, increment.Id, span),
                DirectSequenceSinkKind.All => BoolSwitch(callbackResult!, increment.Id, reject!.Id, span),
                DirectSequenceSinkKind.Count => BoolSwitch(callbackResult!, increment.Id, increment.Id, span),
                _ => throw new InvalidOperationException()
            };
        }

        if (plan.Kind == DirectSequenceSinkKind.Find)
        {
            AppendOwnedCleanup(reject!, span, element);
            reject!.Terminator = new MirGoto { Target = increment.Id, Span = span };
            decisive!.Instructions.Add(new MirCall
            {
                Target = plan.ResultTarget,
                Function = CreateOptionConstructor(module, plan.ResultTarget.TypeId, "Some", span),
                Arguments = [element],
                Span = span
            });
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
            reject.Terminator = new MirGoto { Target = exit.Id, Span = span };
        }

        if (plan.Kind == DirectSequenceSinkKind.Count)
        {
            var countTrue = NewBlock();
            var countFalse = NewBlock();
            body.Terminator = BoolSwitch(callbackResult!, countTrue.Id, countFalse.Id, span);
            countTrue.Instructions.Add(new MirBinOp
            {
                Target = nextCount!,
                Operator = BinaryOp.Add,
                Left = accumulator!,
                Right = IntConstant(1, span),
                Span = span
            });
            countTrue.Instructions.Add(new MirStore
            {
                Target = accumulator!,
                Value = nextCount!,
                Span = span
            });
            countTrue.Terminator = new MirGoto { Target = increment.Id, Span = span };
            countFalse.Terminator = new MirGoto { Target = increment.Id, Span = span };
        }

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
            exit.Instructions.Add(new MirMove
            {
                Target = plan.ResultTarget,
                Source = accumulator!,
                Span = span
            });
        }
        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };

        static BlockId rejectOrIncrement(MirBasicBlock? rejectBlock, MirBasicBlock incrementBlock) =>
            rejectBlock?.Id ?? incrementBlock.Id;
    }

    private static MirFunctionRef CreateOptionConstructor(
        MirModule module,
        TypeId optionType,
        string name,
        SourceSpan span)
    {
        var layout = module.ConstructorLayouts.GetValueOrDefault(optionType.Value)?
            .FirstOrDefault(candidate => string.Equals(candidate.ConstructorName, name, StringComparison.Ordinal));
        var functionId = new FunctionId
        {
            Kind = SymbolKind.Constructor,
            Name = name,
            QualifiedName = layout is null ? name : $"{layout.TypeName}.{name}",
            StableIdentityKey = layout is { RuntimeTypeId: not 0 }
                ? $"runtime-ctor:{layout.RuntimeTypeId}"
                : string.Empty
        };
        return new MirFunctionRef
        {
            Name = name,
            SymbolKind = SymbolKind.Constructor,
            FunctionId = functionId,
            TypeId = optionType,
            Span = span
        };
    }

    private static void ApplyDirectPartitionPlan(
        MirModule module,
        MirFunc function,
        DirectPartitionPlan plan)
    {
        var span = plan.SinkSpan;
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var nextLocalValue = function.Locals.Count == 0 ? 1 : function.Locals.Max(static local => local.Id.Value) + 1;
        var nextBlockValue = function.BasicBlocks.Count == 0 ? 1 : function.BasicBlocks.Max(static block => block.Id.Value) + 1;
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
        MirPlace AggregateField(int index, TypeId type) => new()
        {
            Kind = PlaceKind.Index,
            Base = plan.ResultTarget,
            Index = IntConstant(index, span),
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = type,
            Span = span
        };

        var length = NewLocal("__sequence_partition_length", intType);
        var index = NewLocal("__sequence_partition_index", intType);
        var exhausted = NewLocal("__sequence_partition_exhausted", boolType);
        var element = NewLocal("__sequence_partition_element", plan.ElementType);
        var accepted = NewLocal("__sequence_partition_accepted", boolType);
        var nextIndex = NewLocal("__sequence_partition_next_index", intType);
        var left = NewLocal("__sequence_partition_left", plan.SequenceType);
        var right = NewLocal("__sequence_partition_right", plan.SequenceType);

        var continuation = NewBlock();
        continuation.Instructions.AddRange(plan.Block.Instructions.Skip(plan.InstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;
        var header = NewBlock();
        var body = NewBlock();
        var accept = NewBlock();
        var reject = NewBlock();
        var increment = NewBlock();
        var exit = NewBlock();

        plan.Block.Instructions.RemoveRange(plan.InstructionIndex, plan.Block.Instructions.Count - plan.InstructionIndex);
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(length, plan.Source, span));
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayNewCall(left, length, IntConstant(plan.ElementSize, span), span));
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayNewCall(right, length, IntConstant(plan.ElementSize, span), span));
        plan.Block.Instructions.Add(new MirAssign { Target = index, Source = IntConstant(0, span), Span = span });
        plan.Block.Terminator = new MirGoto { Target = header.Id, Span = span };
        header.Instructions.Add(new MirBinOp { Target = exhausted, Operator = BinaryOp.Ge, Left = index, Right = length, Span = span });
        header.Terminator = BoolSwitch(exhausted, exit.Id, body.Id, span);
        body.Instructions.Add(new MirLoad
        {
            Target = element,
            Source = new MirPlace { Kind = PlaceKind.Index, Base = plan.Source, Index = index, IndexAccessKind = MirIndexAccessKind.RuntimeArray, TypeId = plan.ElementType, Span = span },
            CreatesBorrowAlias = false,
            MovesOutOfSource = !IsCopyType(module, plan.ElementType),
            Span = span
        });
        body.Instructions.Add(new MirCall
        {
            Target = accepted,
            Function = plan.Predicate,
            Arguments = [element with { TypeId = plan.PredicateParameterType }],
            BorrowedArgumentIndices = new HashSet<int> { 0 },
            Span = span
        });
        body.Terminator = BoolSwitch(accepted, accept.Id, reject.Id, span);
        accept.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayPushCall(left, left, element, IntConstant(plan.ElementSize, span), span));
        accept.Terminator = new MirGoto { Target = increment.Id, Span = span };
        reject.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayPushCall(right, right, element, IntConstant(plan.ElementSize, span), span));
        reject.Terminator = new MirGoto { Target = increment.Id, Span = span };
        increment.Instructions.Add(new MirBinOp { Target = nextIndex, Operator = BinaryOp.Add, Left = index, Right = IntConstant(1, span), Span = span });
        increment.Instructions.Add(new MirStore { Target = index, Value = nextIndex, Span = span });
        increment.Terminator = new MirGoto { Target = header.Id, Span = span };
        exit.Instructions.Add(new MirAlloc { Target = plan.ResultTarget, TypeId = plan.ResultTarget.TypeId, Span = span });
        exit.Instructions.Add(new MirMove { Target = AggregateField(0, plan.SequenceType), Source = left, Span = span });
        exit.Instructions.Add(new MirMove { Target = AggregateField(1, plan.SequenceType), Source = right, Span = span });
        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };
    }

    private static MirConstant BoolConstant(bool value, SourceSpan span) => new()
    {
        Value = new MirConstantValue.BoolValue(value),
        TypeId = new TypeId(BaseTypes.BoolId),
        Span = span
    };

    private static MirConstant IntOrBoolZero(DirectSequenceSinkKind kind, SourceSpan span) =>
        kind == DirectSequenceSinkKind.Any
            ? BoolConstant(false, span)
            : IntConstant(0, span);
}
