using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public sealed class OwnershipAnalysisSnapshotTests
{
    [Fact]
    public void Snapshot_AliasAndReturnEscape_BlockDropObligationAreUnified()
    {
        var x = Local(1, "x", isParameter: true);
        var y = Local(2, "y");
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCopy { Source = Place(x), Target = Place(y) },
                new MirDrop { Value = Place(y) }
            ],
            Terminator = new MirReturn { Value = Place(x) }
        };
        var function = Function("alias_return", [x, y], [block]);
        var snapshot = Build(function);

        Assert.Contains(y.Id, snapshot.AliasSets[x.Id]);
        Assert.Equal(OwnershipEscapeKind.Return, snapshot.EscapeFacts[x.Id]);
        Assert.Contains(y.Id, snapshot.DropObligations);
        Assert.False(snapshot.IsMustUnique(x.Id, block.Id, 0));
    }

    [Fact]
    public void Snapshot_IfElseJoinUsesMaybeOwnedWhenOnlyOnePathMoves()
    {
        var x = Local(1, "x", isParameter: true);
        var condition = Local(2, "condition");
        var moved = Local(3, "moved");
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Terminator = new MirSwitch
            {
                Discriminant = Place(condition, new TypeId(BaseTypes.BoolId)),
                Branches = [new MirSwitchBranch { Target = new BlockId { Value = 2 }, Value = new MirConstant { Value = new MirConstantValue.BoolValue(true) } }],
                DefaultTarget = new BlockId { Value = 3 }
            }
        };
        var movedBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions = [new MirMove { Source = Place(x), Target = Place(moved) }],
            Terminator = new MirGoto { Target = new BlockId { Value = 4 } }
        };
        var keptBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Terminator = new MirGoto { Target = new BlockId { Value = 4 } }
        };
        var merge = new MirBasicBlock
        {
            Id = new BlockId { Value = 4 },
            Terminator = new MirReturn { Value = null }
        };
        var function = Function("if_join", [x, condition, moved], [entry, movedBlock, keptBlock, merge]);
        var snapshot = Build(function);

        Assert.Equal(OwnershipPlaceState.MaybeOwned, snapshot.PerBlockInFacts[merge.Id].States[x.Id]);
    }

    [Fact]
    public void Snapshot_LoopBackEdgeRecordsCarriedRoot()
    {
        var x = Local(1, "x", isParameter: true);
        var condition = Local(2, "condition");
        var header = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Terminator = new MirSwitch
            {
                Discriminant = Place(condition, new TypeId(BaseTypes.BoolId)),
                Branches = [new MirSwitchBranch { Target = new BlockId { Value = 2 }, Value = new MirConstant { Value = new MirConstantValue.BoolValue(true) } }],
                DefaultTarget = new BlockId { Value = 3 }
            }
        };
        var body = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions = [new MirAssign { Target = Place(x), Source = new MirConstant { Value = new MirConstantValue.IntValue(1) } }],
            Terminator = new MirGoto { Target = header.Id }
        };
        var exit = new MirBasicBlock { Id = new BlockId { Value = 3 }, Terminator = new MirReturn { Value = Place(x) } };
        var function = Function("loop_carried", [x, condition], [header, body, exit]);
        var snapshot = Build(function);

        Assert.Contains(x.Id, snapshot.LoopCarriedLocals);
        Assert.Contains(x.Id, snapshot.EscapeFacts.Keys);
    }

    [Fact]
    public void Snapshot_ActiveBorrowBlocksDestructiveUpdate()
    {
        var source = Local(1, "source", isParameter: true);
        var borrowed = Local(2, "borrowed");
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirLoad { Source = Place(source), Target = Place(borrowed), CreatesBorrowAlias = true },
                new MirAssign { Target = Place(source), Source = new MirConstant { Value = new MirConstantValue.IntValue(2) } }
            ],
            Terminator = new MirReturn { Value = null }
        };
        var function = Function("active_borrow", [source, borrowed], [block]);
        var snapshot = Build(function);

        Assert.True(snapshot.PerInstructionFacts[(block.Id, 1)].ActiveBorrowRoots.Contains(source.Id));
        Assert.False(snapshot.CanDestructivelyUpdate(source.Id, block.Id, 1));
    }

    [Fact]
    public void Snapshot_TracksEarlyReturnCleanupAndPanicCleanupSeparately()
    {
        var owned = Local(1, "owned", isParameter: true);
        var returned = Local(2, "returned", isParameter: true);
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Terminator = new MirGoto { Target = new BlockId { Value = 2 } }
        };
        var earlyReturn = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Terminator = new MirReturn { Value = Place(returned) }
        };
        var panic = new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Terminator = new MirUnreachable()
        };
        var function = Function("exit_cleanup", [owned, returned], [entry, earlyReturn, panic]);
        var snapshot = Build(function);

        Assert.Contains(earlyReturn.Id, snapshot.EarlyReturnBlocks);
        Assert.Contains(owned.Id, snapshot.ExitCleanupFacts[earlyReturn.Id].LocalsRequiringCleanup);
        Assert.Contains(panic.Id, snapshot.PanicBlocks);
        Assert.True(snapshot.ExitCleanupFacts[panic.Id].IsPanicPath);
    }

    [Fact]
    public void Snapshot_TracksPartialMoveReinitializeAndDropExactlyOnce()
    {
        var aggregate = Local(1, "aggregate", isParameter: true);
        var field = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = Place(aggregate),
            FieldName = "payload",
            TypeId = aggregate.TypeId
        };
        var replacement = new MirConstant
        {
            Value = new MirConstantValue.IntValue(7),
            TypeId = aggregate.TypeId
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirMove { Source = field, Target = Place(Local(2, "moved")) },
                new MirStore { Target = field, Value = replacement },
                new MirDrop { Value = Place(aggregate) }
            ],
            Terminator = new MirReturn { Value = null }
        };
        var moved = Local(2, "moved");
        block.Instructions[0] = new MirMove { Source = field, Target = Place(moved) };
        var function = Function("partial_move", [aggregate, moved], [block]);
        var snapshot = Build(function);
        var key = new OwnershipPlaceKey(aggregate.Id, ".payload");

        Assert.True(snapshot.IsPartialMoveReinitialized(key));
        Assert.Equal(1, snapshot.DropCounts[aggregate.Id]);
        Assert.True(snapshot.HasExactlyOneDrop(aggregate.Id));
    }

    private static OwnershipAnalysisSnapshot Build(MirFunc function)
    {
        var usage = new VariableUsageAnalyzer(function);
        usage.Analyze();
        var cfg = new ControlFlowGraph(function);
        var liveness = new LivenessAnalyzer(function, usage, cfg);
        liveness.Analyze();
        var checker = new BorrowChecker(function, liveness, capturePointStates: true, cfg: cfg);
        checker.Check();
        var perceus = new PerceusAnalyzer(function, liveness, usage);
        perceus.Analyze();
        var reuse = new ReuseAnalyzer(function, perceus.Hints);
        reuse.Analyze();
        var verifier = new LoanConstraintVerifier(new LoanSignatureCache(), new SymbolTable(), capturePointStates: true);
        var loanResults = verifier.VerifyFunction(function, cfg);
        return OwnershipAnalysisSnapshot.Build(function, cfg, usage, liveness, checker, verifier, perceus, reuse, loanResults);
    }

    private static MirFunc Function(string name, IReadOnlyList<MirLocal> locals, IReadOnlyList<MirBasicBlock> blocks) =>
        new()
        {
            Name = name,
            EntryBlockId = blocks[0].Id,
            ReturnType = new TypeId(BaseTypes.UnitId),
            Locals = [.. locals],
            BasicBlocks = [.. blocks]
        };

    private static MirLocal Local(int id, string name, bool isParameter = false) =>
        new() { Id = new LocalId { Value = id }, Name = name, TypeId = new TypeId(BaseTypes.IntId), IsParameter = isParameter };

    private static MirPlace Place(MirLocal local, TypeId? type = null) =>
        new() { Kind = PlaceKind.Local, Local = local.Id, TypeId = type ?? local.TypeId };

    private static MirPlace Place(LocalId local, TypeId? type = null) =>
        new() { Kind = PlaceKind.Local, Local = local, TypeId = type ?? new TypeId(BaseTypes.IntId) };
}
