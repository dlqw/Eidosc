using Eidosc;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public class LocalTransferAnalyzerRegressionTests
{
    [Fact]
    public void LivenessAnalyzer_LoadCopyMoveLocalTransfer_PreservesLiveInSet()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var a = new LocalId { Value = 2 };
        var b = new LocalId { Value = 3 };
        var c = new LocalId { Value = 4 };
        var blockId = new BlockId { Value = 1 };

        var func = new MirFunc
        {
            Name = "liveness_local_transfer",
            EntryBlockId = blockId,
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = a, Name = "a", TypeId = intType },
                new MirLocal { Id = b, Name = "b", TypeId = intType },
                new MirLocal { Id = c, Name = "c", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = blockId,
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = a, TypeId = intType }
                        },
                        new MirCopy
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = a, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = intType }
                        },
                        new MirMove
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = c, TypeId = intType }
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = c, TypeId = intType }
                    }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        Assert.True(liveness.LiveIn.TryGetValue(blockId, out var liveIn));
        Assert.Contains(x, liveIn);
        Assert.Contains(a, liveIn);
        Assert.Contains(b, liveIn);
        Assert.DoesNotContain(c, liveIn);
    }

    [Fact]
    public void PerceusAnalyzer_CopyLastUseAfterLoad_StillMarksOmitDup()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var a = new LocalId { Value = 2 };
        var b = new LocalId { Value = 3 };
        var blockId = new BlockId { Value = 1 };

        var func = new MirFunc
        {
            Name = "perceus_local_transfer",
            EntryBlockId = blockId,
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = a, Name = "a", TypeId = intType },
                new MirLocal { Id = b, Name = "b", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = blockId,
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = a, TypeId = intType }
                        },
                        new MirCopy
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = a, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = intType }
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = intType }
                    }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var perceus = new PerceusAnalyzer(func, liveness, usage);
        perceus.Analyze();

        Assert.Contains((blockId, 1), perceus.Hints.OmitDup);
    }
}
