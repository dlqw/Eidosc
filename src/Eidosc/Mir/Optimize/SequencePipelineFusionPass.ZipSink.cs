using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class SequencePipelineFusionPass
{
    private static void ApplyDirectZipSequenceSinkPlan(
        MirModule module,
        MirFunc function,
        DirectZipSequenceSinkPlan plan)
    {
        var span = plan.SinkSpan;
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);
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

        var leftLength = NewLocal("__sequence_zip_left_length", intType);
        var rightLength = NewLocal("__sequence_zip_right_length", intType);
        var index = NewLocal("__sequence_zip_index", intType);
        var exhausted = NewLocal("__sequence_zip_exhausted", boolType);
        var leftElement = NewLocal("__sequence_zip_left_element", plan.LeftElementType);
        var rightElement = NewLocal("__sequence_zip_right_element", plan.RightElementType);
        var pair = NewLocal("__sequence_zip_pair", plan.PairType);
        var callbackResult = plan.Kind == DirectSequenceSinkKind.ForEach ? null : NewLocal("__sequence_zip_callback_result", boolType);
        var accumulator = plan.Kind switch
        {
            DirectSequenceSinkKind.Any => NewLocal("__sequence_zip_any", boolType),
            DirectSequenceSinkKind.All => NewLocal("__sequence_zip_all", boolType),
            DirectSequenceSinkKind.Count => NewLocal("__sequence_zip_count", intType),
            _ => null
        };
        var nextIndex = NewLocal("__sequence_zip_next_index", intType);
        var nextCount = plan.Kind == DirectSequenceSinkKind.Count ? NewLocal("__sequence_zip_next_count", intType) : null;

        var continuation = NewBlock();
        continuation.Instructions.AddRange(plan.Block.Instructions.Skip(plan.SinkInstructionIndex + 1));
        continuation.Terminator = plan.Block.Terminator;
        var header = NewBlock();
        var body = NewBlock();
        var increment = NewBlock();
        var exit = NewBlock();
        var decisive = plan.Kind is DirectSequenceSinkKind.Find or DirectSequenceSinkKind.Any ? NewBlock() : null;
        var reject = plan.Kind is DirectSequenceSinkKind.Find or DirectSequenceSinkKind.All ? NewBlock() : null;
        var countTrue = plan.Kind == DirectSequenceSinkKind.Count ? NewBlock() : null;
        var countFalse = plan.Kind == DirectSequenceSinkKind.Count ? NewBlock() : null;

        plan.Block.Instructions.RemoveRange(plan.FirstInstructionIndex, plan.Block.Instructions.Count - plan.FirstInstructionIndex);
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(leftLength, plan.LeftSource, span));
        plan.Block.Instructions.Add(RuntimeSequenceBuildLowering.CreateArrayLengthCall(rightLength, plan.RightSource, span));
        var minLength = NewLocal("__sequence_zip_min_length", intType);
        var leftIsShorter = NewLocal("__sequence_zip_left_is_shorter", boolType);
        plan.Block.Instructions.Add(new MirBinOp { Target = leftIsShorter, Operator = BinaryOp.Lt, Left = leftLength, Right = rightLength, Span = span });
        plan.Block.Instructions.Add(new MirSelect { Target = minLength, Condition = leftIsShorter, TrueValue = leftLength, FalseValue = rightLength, Span = span });
        plan.Block.Instructions.Add(new MirAssign { Target = index, Source = IntConstant(0, span), Span = span });
        if (accumulator != null)
            plan.Block.Instructions.Add(new MirAssign { Target = accumulator, Source = plan.Kind == DirectSequenceSinkKind.All ? BoolConstant(true, span) : IntOrBoolZero(plan.Kind, span), Span = span });
        plan.Block.Terminator = new MirGoto { Target = header.Id, Span = span };

        header.Instructions.Add(new MirBinOp { Target = exhausted, Operator = BinaryOp.Ge, Left = index, Right = minLength, Span = span });
        header.Terminator = BoolSwitch(exhausted, exit.Id, body.Id, span);
        body.Instructions.Add(new MirLoad { Target = leftElement, Source = new MirPlace { Kind = PlaceKind.Index, Base = plan.LeftSource, Index = index, IndexAccessKind = MirIndexAccessKind.RuntimeArray, TypeId = plan.LeftElementType, Span = span }, CreatesBorrowAlias = false, MovesOutOfSource = plan.Kind == DirectSequenceSinkKind.Find, Span = span });
        body.Instructions.Add(new MirLoad { Target = rightElement, Source = new MirPlace { Kind = PlaceKind.Index, Base = plan.RightSource, Index = index, IndexAccessKind = MirIndexAccessKind.RuntimeArray, TypeId = plan.RightElementType, Span = span }, CreatesBorrowAlias = false, MovesOutOfSource = plan.Kind == DirectSequenceSinkKind.Find, Span = span });
        body.Instructions.Add(new MirAlloc { Target = pair, TypeId = plan.PairType, Span = span });
        body.Instructions.Add(new MirStore { Target = new MirPlace { Kind = PlaceKind.Index, Base = pair, Index = IntConstant(0, span), IndexAccessKind = MirIndexAccessKind.Aggregate, TypeId = plan.LeftElementType, Span = span }, Value = leftElement, Span = span });
        body.Instructions.Add(new MirStore { Target = new MirPlace { Kind = PlaceKind.Index, Base = pair, Index = IntConstant(1, span), IndexAccessKind = MirIndexAccessKind.Aggregate, TypeId = plan.RightElementType, Span = span }, Value = rightElement, Span = span });
        if (plan.Kind == DirectSequenceSinkKind.ForEach)
        {
            body.Instructions.Add(new MirCall { Target = null, Function = plan.Callback, Arguments = [pair with { TypeId = plan.CallbackParameterType }], Span = span });
            body.Terminator = new MirGoto { Target = increment.Id, Span = span };
        }
        else
        {
            body.Instructions.Add(new MirCall { Target = callbackResult, Function = plan.Callback, Arguments = [pair with { TypeId = plan.CallbackParameterType }], Span = span });
            body.Terminator = plan.Kind switch
            {
                DirectSequenceSinkKind.Find => BoolSwitch(callbackResult!, decisive!.Id, rejectOrIncrement(reject, increment), span),
                DirectSequenceSinkKind.Any => BoolSwitch(callbackResult!, decisive!.Id, increment.Id, span),
                DirectSequenceSinkKind.All => BoolSwitch(callbackResult!, increment.Id, reject!.Id, span),
                DirectSequenceSinkKind.Count => BoolSwitch(callbackResult!, countTrue!.Id, countFalse!.Id, span),
                _ => throw new InvalidOperationException()
            };
        }

        if (plan.Kind == DirectSequenceSinkKind.Find)
        {
            AppendOwnedCleanup(reject!, span, pair);
            reject!.Terminator = new MirGoto { Target = increment.Id, Span = span };
            decisive!.Instructions.Add(new MirCall { Target = plan.ResultTarget, Function = CreateOptionConstructor(module, plan.ResultTarget.TypeId, "Some", span), Arguments = [pair], Span = span });
            decisive.Terminator = new MirGoto { Target = continuation.Id, Span = span };
        }
        else if (plan.Kind == DirectSequenceSinkKind.Any)
        {
            AppendOwnedCleanup(decisive!, span, pair);
            decisive!.Instructions.Add(new MirAssign { Target = accumulator!, Source = BoolConstant(true, span), Span = span });
            decisive.Terminator = new MirGoto { Target = exit.Id, Span = span };
        }
        else if (plan.Kind == DirectSequenceSinkKind.All)
        {
            AppendOwnedCleanup(reject!, span, pair);
            reject!.Instructions.Add(new MirAssign { Target = accumulator!, Source = BoolConstant(false, span), Span = span });
            reject.Terminator = new MirGoto { Target = exit.Id, Span = span };
        }
        else if (plan.Kind == DirectSequenceSinkKind.Count)
        {
            countTrue!.Instructions.Add(new MirBinOp { Target = nextCount!, Operator = BinaryOp.Add, Left = accumulator!, Right = IntConstant(1, span), Span = span });
            countTrue.Instructions.Add(new MirStore { Target = accumulator!, Value = nextCount!, Span = span });
            countTrue.Terminator = new MirGoto { Target = increment.Id, Span = span };
            countFalse!.Terminator = new MirGoto { Target = increment.Id, Span = span };
        }

        AppendOwnedCleanup(increment, span, pair);
        increment.Instructions.Add(new MirBinOp { Target = nextIndex, Operator = BinaryOp.Add, Left = index, Right = IntConstant(1, span), Span = span });
        increment.Instructions.Add(new MirStore { Target = index, Value = nextIndex, Span = span });
        increment.Terminator = new MirGoto { Target = header.Id, Span = span };

        if (plan.Kind == DirectSequenceSinkKind.Find)
            exit.Instructions.Add(new MirCall { Target = plan.ResultTarget, Function = CreateOptionConstructor(module, plan.ResultTarget.TypeId, "None", span), Arguments = [], Span = span });
        else if (plan.Kind == DirectSequenceSinkKind.ForEach)
            exit.Instructions.Add(new MirAssign { Target = plan.ResultTarget, Source = new MirConstant { Value = new MirConstantValue.UnitValue(), TypeId = unitType, Span = span }, Span = span });
        else
            exit.Instructions.Add(new MirMove { Target = plan.ResultTarget, Source = accumulator!, Span = span });
        exit.Terminator = new MirGoto { Target = continuation.Id, Span = span };

        static BlockId rejectOrIncrement(MirBasicBlock? rejectBlock, MirBasicBlock incrementBlock) => rejectBlock?.Id ?? incrementBlock.Id;
    }
}
