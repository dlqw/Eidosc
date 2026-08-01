using Eidosc;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirOptimizerTests
{
    [Fact]
    public void RuntimeArrayFusion_ConditionalDropLastChain_BecomesSingleShiftPrepend()
    {
        var fixture = CreateShiftPrependFixture();

        new RuntimeArrayFusionPass().Run(fixture.Module);

        Assert.Empty(fixture.GrowBlock.Instructions);
        Assert.Empty(fixture.TrimBlock.Instructions);
        var fused = Assert.Single(
            fixture.MergeBlock.Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    MirRuntimeFunctions.HasIdentity(
                        functionRef,
                        WellKnownStrings.InternalNames.ArrayShiftPrepend));
        Assert.Equal(fixture.Result.Id, fused.Target!.Local);
        Assert.Equal(fixture.Rest.Id, Assert.IsType<MirPlace>(fused.Arguments[0]).Local);
        Assert.Equal(fixture.NewHeadCopy.Id, Assert.IsType<MirPlace>(fused.Arguments[1]).Local);
        Assert.Equal(fixture.OldHeadCopy.Id, Assert.IsType<MirPlace>(fused.Arguments[2]).Local);
        Assert.Equal(fixture.Grow.Id, Assert.IsType<MirPlace>(fused.Arguments[3]).Local);
        Assert.Equal(new MirConstantValue.IntValue(8), Assert.IsType<MirConstant>(fused.Arguments[4]).Value);
        Assert.Equal(2, fixture.MergeBlock.Instructions.OfType<MirCopy>().Count());
    }

    [Fact]
    public void RuntimeArrayFusion_ConditionalDropLastWithDifferentRest_RemainsUnfused()
    {
        var fixture = CreateShiftPrependFixture(differentRest: true);

        new RuntimeArrayFusionPass().Run(fixture.Module);

        Assert.NotEmpty(fixture.GrowBlock.Instructions);
        Assert.NotEmpty(fixture.TrimBlock.Instructions);
        Assert.DoesNotContain(
            fixture.MergeBlock.Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    MirRuntimeFunctions.HasIdentity(
                        functionRef,
                        WellKnownStrings.InternalNames.ArrayShiftPrepend));
    }

    [Fact]
    public void RuntimeArrayFusion_ConditionalDropLastWithDifferentOldHead_RemainsUnfused()
    {
        var fixture = CreateShiftPrependFixture(differentOldHead: true);

        new RuntimeArrayFusionPass().Run(fixture.Module);

        Assert.NotEmpty(fixture.GrowBlock.Instructions);
        Assert.NotEmpty(fixture.TrimBlock.Instructions);
        Assert.DoesNotContain(
            fixture.MergeBlock.Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    MirRuntimeFunctions.HasIdentity(
                        functionRef,
                        WellKnownStrings.InternalNames.ArrayShiftPrepend));
    }

    private static ShiftPrependFixture CreateShiftPrependFixture(
        bool differentRest = false,
        bool differentOldHead = false)
    {
        var sequenceType = new TypeId(7200);
        var elementType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var rest = ShiftLocal(1, "rest", sequenceType, parameter: true);
        var otherRest = ShiftLocal(2, "other_rest", sequenceType, parameter: true);
        var oldHead = ShiftLocal(3, "old_head", elementType, parameter: true);
        var otherOldHead = ShiftLocal(4, "other_old_head", elementType, parameter: true);
        var newHead = ShiftLocal(5, "new_head", elementType, parameter: true);
        var grow = ShiftLocal(6, "grow", boolType, parameter: true);
        var growOldHeadCopy = ShiftLocal(7, "grow_old_head", elementType);
        var growRest = ShiftLocal(8, "grow_rest", sequenceType);
        var growResult = ShiftLocal(9, "grow_result", sequenceType);
        var mergedTail = ShiftLocal(10, "merged_tail", sequenceType);
        var trimOldHeadCopy = ShiftLocal(11, "trim_old_head", elementType);
        var trimRest = ShiftLocal(12, "trim_rest", sequenceType);
        var dropResult = ShiftLocal(13, "drop_result", sequenceType);
        var dropMove = ShiftLocal(14, "drop_move", sequenceType);
        var trimResult = ShiftLocal(15, "trim_result", sequenceType);
        var mergedTailMove = ShiftLocal(16, "merged_tail_move", sequenceType);
        var newHeadCopy = ShiftLocal(17, "new_head_copy", elementType);
        var result = ShiftLocal(18, "result", sequenceType);
        var returned = ShiftLocal(19, "returned", sequenceType);
        var size = new MirConstant
        {
            TypeId = intType,
            Value = new MirConstantValue.IntValue(8)
        };

        var take = new MirFunc
        {
            Name = "fixture_array_take",
            IntrinsicName = WellKnownStrings.InternalNames.ArrayTake,
            ReturnType = sequenceType
        };
        var dropLast = CreateDropLastFixture(take.Name, sequenceType, intType);
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Terminator = new MirSwitch
            {
                Discriminant = ShiftPlace(grow),
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            TypeId = boolType,
                            Value = new MirConstantValue.BoolValue(true)
                        },
                        Target = new BlockId { Value = 2 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 3 }
            }
        };
        var growBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions =
            [
                new MirCopy
                {
                    Target = ShiftPlace(growOldHeadCopy),
                    Source = ShiftPlace(oldHead)
                },
                new MirMove
                {
                    Target = ShiftPlace(growRest),
                    Source = ShiftPlace(rest)
                },
                new MirCall
                {
                    Target = ShiftPlace(growResult),
                    Function = MirRuntimeFunctions.CreateFunctionRef(
                        WellKnownStrings.InternalNames.ArrayPrepend,
                        sequenceType,
                        SourceSpan.Empty),
                    Arguments = [ShiftPlace(growRest), ShiftPlace(growOldHeadCopy), size]
                },
                new MirMove
                {
                    Target = ShiftPlace(mergedTail),
                    Source = ShiftPlace(growResult)
                }
            ],
            Terminator = new MirGoto { Target = new BlockId { Value = 4 } }
        };
        var trimBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Instructions =
            [
                new MirCopy
                {
                    Target = ShiftPlace(trimOldHeadCopy),
                    Source = ShiftPlace(differentOldHead ? otherOldHead : oldHead)
                },
                new MirMove
                {
                    Target = ShiftPlace(trimRest),
                    Source = ShiftPlace(differentRest ? otherRest : rest)
                },
                new MirCall
                {
                    Target = ShiftPlace(dropResult),
                    Function = new MirFunctionRef { Name = dropLast.Name, TypeId = sequenceType },
                    Arguments = [ShiftPlace(trimRest)]
                },
                new MirMove
                {
                    Target = ShiftPlace(dropMove),
                    Source = ShiftPlace(dropResult)
                },
                new MirCall
                {
                    Target = ShiftPlace(trimResult),
                    Function = MirRuntimeFunctions.CreateFunctionRef(
                        WellKnownStrings.InternalNames.ArrayPrepend,
                        sequenceType,
                        SourceSpan.Empty),
                    Arguments = [ShiftPlace(dropMove), ShiftPlace(trimOldHeadCopy), size]
                },
                new MirMove
                {
                    Target = ShiftPlace(mergedTail),
                    Source = ShiftPlace(trimResult)
                }
            ],
            Terminator = new MirGoto { Target = new BlockId { Value = 4 } }
        };
        var mergeBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 4 },
            Instructions =
            [
                new MirCopy
                {
                    Target = ShiftPlace(newHeadCopy),
                    Source = ShiftPlace(newHead)
                },
                new MirMove
                {
                    Target = ShiftPlace(mergedTailMove),
                    Source = ShiftPlace(mergedTail)
                },
                new MirCall
                {
                    Target = ShiftPlace(result),
                    Function = MirRuntimeFunctions.CreateFunctionRef(
                        WellKnownStrings.InternalNames.ArrayPrepend,
                        sequenceType,
                        SourceSpan.Empty),
                    Arguments = [ShiftPlace(mergedTailMove), ShiftPlace(newHeadCopy), size]
                },
                new MirMove
                {
                    Target = ShiftPlace(returned),
                    Source = ShiftPlace(result)
                }
            ],
            Terminator = new MirReturn { Value = ShiftPlace(returned) }
        };
        var function = new MirFunc
        {
            Name = "conditional_shift",
            ReturnType = sequenceType,
            Locals =
            [
                rest,
                otherRest,
                oldHead,
                otherOldHead,
                newHead,
                grow,
                growOldHeadCopy,
                growRest,
                growResult,
                mergedTail,
                trimOldHeadCopy,
                trimRest,
                dropResult,
                dropMove,
                trimResult,
                mergedTailMove,
                newHeadCopy,
                result,
                returned
            ],
            EntryBlockId = entry.Id,
            BasicBlocks = [entry, growBlock, trimBlock, mergeBlock]
        };
        var module = new MirModule
        {
            Name = "shift_prepend_fixture",
            Functions = [take, dropLast, function]
        };
        return new ShiftPrependFixture(
            module,
            growBlock,
            trimBlock,
            mergeBlock,
            rest,
            oldHead,
            newHeadCopy,
            growOldHeadCopy,
            grow,
            returned);
    }

    private static MirFunc CreateDropLastFixture(
        string takeName,
        TypeId sequenceType,
        TypeId intType)
    {
        var parameter = ShiftLocal(1, "values", sequenceType, parameter: true);
        var length = ShiftLocal(2, "length", intType);
        var count = ShiftLocal(3, "count", intType);
        var result = ShiftLocal(4, "result", sequenceType);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = ShiftPlace(length),
                    Function = MirRuntimeFunctions.CreateFunctionRef(
                        WellKnownStrings.InternalNames.ArrayLength,
                        intType,
                        SourceSpan.Empty),
                    Arguments = [ShiftPlace(parameter)]
                },
                new MirBinOp
                {
                    Target = ShiftPlace(count),
                    Operator = BinaryOp.Sub,
                    Left = ShiftPlace(length),
                    Right = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(1)
                    }
                },
                new MirCall
                {
                    Target = ShiftPlace(result),
                    Function = new MirFunctionRef { Name = takeName, TypeId = sequenceType },
                    Arguments = [ShiftPlace(parameter), ShiftPlace(count)]
                }
            ],
            Terminator = new MirReturn { Value = ShiftPlace(result) }
        };
        return new MirFunc
        {
            Name = "fixture_drop_last",
            ReturnType = sequenceType,
            Locals = [parameter, length, count, result],
            EntryBlockId = block.Id,
            BasicBlocks = [block]
        };
    }

    private static MirLocal ShiftLocal(
        int id,
        string name,
        TypeId typeId,
        bool parameter = false) => new()
        {
            Id = new LocalId { Value = id },
            Name = name,
            TypeId = typeId,
            IsParameter = parameter
        };

    private static MirPlace ShiftPlace(MirLocal local) => new()
    {
        Kind = PlaceKind.Local,
        Local = local.Id,
        TypeId = local.TypeId
    };

    private sealed record ShiftPrependFixture(
        MirModule Module,
        MirBasicBlock GrowBlock,
        MirBasicBlock TrimBlock,
        MirBasicBlock MergeBlock,
        MirLocal Rest,
        MirLocal OldHead,
        MirLocal NewHeadCopy,
        MirLocal OldHeadCopy,
        MirLocal Grow,
        MirLocal Result);
}
