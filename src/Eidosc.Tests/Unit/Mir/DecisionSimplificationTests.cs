using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class DecisionSimplificationTests
{
    private static readonly TypeId BoolType = new(BaseTypes.BoolId);
    private static readonly TypeId IntType = new(BaseTypes.IntId);

    [Fact]
    public void SimplifiesDirectConstantSwitchToMatchingGoto()
    {
        var optimized = Optimize(
            discriminant: BoolConstant(true),
            branches: [Branch(true, 2)],
            defaultTarget: new BlockId { Value = 3 });

        var terminator = Assert.IsType<MirGoto>(Assert.Single(optimized.BasicBlocks).Terminator);
        Assert.Equal(new BlockId { Value = 2 }, terminator.Target);
    }

    [Fact]
    public void SimplifiesCopyPropagatedConstantSwitchToDefaultGoto()
    {
        var condition = Local(2, "condition");
        var optimized = Optimize(
            instructions: [new MirCopy
            {
                Target = Place(condition.Id),
                Source = Place(Local(1, "source").Id)
            }],
            locals: [Local(1, "source"), condition],
            discriminant: Place(condition.Id),
            branches: [Branch(true, 2)],
            defaultTarget: new BlockId { Value = 3 },
            sourceInitializer: new MirAssign
            {
                Target = Place(new LocalId { Value = 1 }),
                Source = BoolConstant(false)
            });

        var terminator = Assert.IsType<MirGoto>(Assert.Single(optimized.BasicBlocks).Terminator);
        Assert.Equal(new BlockId { Value = 3 }, terminator.Target);
    }

    [Fact]
    public void SimplifiesComputedConstantSwitch()
    {
        var condition = Local(1, "condition");
        var optimized = Optimize(
            instructions: [new MirBinOp
            {
                Target = Place(condition.Id),
                Operator = BinaryOp.Eq,
                Left = IntConstant(1),
                Right = IntConstant(1)
            }],
            locals: [condition],
            discriminant: Place(condition.Id),
            branches: [Branch(true, 2)],
            defaultTarget: new BlockId { Value = 3 });

        var terminator = Assert.IsType<MirGoto>(Assert.Single(optimized.BasicBlocks).Terminator);
        Assert.Equal(new BlockId { Value = 2 }, terminator.Target);
    }

    [Fact]
    public void SimplifiesKnownSwitchWithoutDefaultToUnreachable()
    {
        var optimized = Optimize(
            discriminant: BoolConstant(false),
            branches: [Branch(true, 2)]);

        Assert.IsType<MirUnreachable>(Assert.Single(optimized.BasicBlocks).Terminator);
    }

    [Fact]
    public void PreservesSwitchWhenDiscriminantIsUnknown()
    {
        var source = Local(1, "source", isParameter: true);
        var condition = Local(2, "condition");
        var optimized = Optimize(
            instructions: [new MirCopy
            {
                Target = Place(condition.Id),
                Source = Place(source.Id)
            }],
            locals: [source, condition],
            discriminant: Place(condition.Id),
            branches: [Branch(true, 2)],
            defaultTarget: new BlockId { Value = 3 });

        Assert.IsType<MirSwitch>(Assert.Single(optimized.BasicBlocks).Terminator);
    }

    [Fact]
    public void PreservesSwitchWhenBranchBindsPatternValue()
    {
        var optimized = Optimize(
            discriminant: BoolConstant(true),
            branches: [new MirSwitchBranch
            {
                Value = BoolConstant(true),
                Target = new BlockId { Value = 2 },
                BoundVariable = new LocalId { Value = 4 }
            }],
            defaultTarget: new BlockId { Value = 3 });

        Assert.IsType<MirSwitch>(Assert.Single(optimized.BasicBlocks).Terminator);
    }

    [Fact]
    public void SimplifiesSwitchFromConstantPropagatedAcrossGoto()
    {
        var condition = Local(1, "condition");
        var entry = Block(1,
            [new MirAssign { Target = Place(condition.Id), Source = BoolConstant(true) }],
            new MirGoto { Target = BlockIdOf(2) });
        var decision = Block(2, [], new MirSwitch
        {
            Discriminant = Place(condition.Id),
            Branches = [Branch(true, 3)],
            DefaultTarget = BlockIdOf(4)
        });

        var optimized = OptimizeFunction([entry, decision], [condition]);

        var terminator = Assert.IsType<MirGoto>(optimized.BasicBlocks.Single(block => block.Id == BlockIdOf(2)).Terminator);
        Assert.Equal(BlockIdOf(3), terminator.Target);
    }

    [Fact]
    public void JoinsEqualConstantsFromMultiplePredecessors()
    {
        var route = Local(1, "route", isParameter: true);
        var condition = Local(2, "condition");
        var entry = Block(1, [], new MirSwitch
        {
            Discriminant = Place(route.Id),
            Branches = [Branch(true, 2)],
            DefaultTarget = BlockIdOf(3)
        });
        var left = Block(2,
            [new MirAssign { Target = Place(condition.Id), Source = BoolConstant(true) }],
            new MirGoto { Target = BlockIdOf(4) });
        var right = Block(3,
            [new MirAssign { Target = Place(condition.Id), Source = BoolConstant(true) }],
            new MirGoto { Target = BlockIdOf(4) });
        var decision = Block(4, [], new MirSwitch
        {
            Discriminant = Place(condition.Id),
            Branches = [Branch(true, 5)],
            DefaultTarget = BlockIdOf(6)
        });

        var optimized = OptimizeFunction([entry, left, right, decision], [route, condition]);

        var terminator = Assert.IsType<MirGoto>(optimized.BasicBlocks.Single(block => block.Id == BlockIdOf(4)).Terminator);
        Assert.Equal(BlockIdOf(5), terminator.Target);
    }

    [Fact]
    public void PreservesSwitchWhenPredecessorConstantsConflict()
    {
        var route = Local(1, "route", isParameter: true);
        var condition = Local(2, "condition");
        var entry = Block(1, [], new MirSwitch
        {
            Discriminant = Place(route.Id),
            Branches = [Branch(true, 2)],
            DefaultTarget = BlockIdOf(3)
        });
        var left = Block(2,
            [new MirAssign { Target = Place(condition.Id), Source = BoolConstant(true) }],
            new MirGoto { Target = BlockIdOf(4) });
        var right = Block(3,
            [new MirAssign { Target = Place(condition.Id), Source = BoolConstant(false) }],
            new MirGoto { Target = BlockIdOf(4) });
        var decision = Block(4, [], new MirSwitch
        {
            Discriminant = Place(condition.Id),
            Branches = [Branch(true, 5)],
            DefaultTarget = BlockIdOf(6)
        });

        var optimized = OptimizeFunction([entry, left, right, decision], [route, condition]);

        Assert.IsType<MirSwitch>(optimized.BasicBlocks.Single(block => block.Id == BlockIdOf(4)).Terminator);
    }

    [Fact]
    public void RefinesBooleanFactsAlongSwitchEdges()
    {
        var condition = Local(1, "condition", isParameter: true);
        var entry = Block(1, [], new MirSwitch
        {
            Discriminant = Place(condition.Id),
            Branches = [Branch(true, 2)],
            DefaultTarget = BlockIdOf(3)
        });
        var trueDecision = Block(2, [], new MirSwitch
        {
            Discriminant = Place(condition.Id),
            Branches = [Branch(true, 4)],
            DefaultTarget = BlockIdOf(5)
        });
        var falseDecision = Block(3, [], new MirSwitch
        {
            Discriminant = Place(condition.Id),
            Branches = [Branch(true, 6)],
            DefaultTarget = BlockIdOf(7)
        });

        var optimized = OptimizeFunction([entry, trueDecision, falseDecision], [condition]);

        Assert.Equal(
            BlockIdOf(4),
            Assert.IsType<MirGoto>(optimized.BasicBlocks.Single(block => block.Id == BlockIdOf(2)).Terminator).Target);
        Assert.Equal(
            BlockIdOf(7),
            Assert.IsType<MirGoto>(optimized.BasicBlocks.Single(block => block.Id == BlockIdOf(3)).Terminator).Target);
    }

    private static MirFunc Optimize(
        MirOperand discriminant,
        IReadOnlyList<MirSwitchBranch> branches,
        BlockId? defaultTarget = null,
        IReadOnlyList<MirInstruction>? instructions = null,
        IReadOnlyList<MirLocal>? locals = null,
        MirInstruction? sourceInitializer = null)
    {
        var entryInstructions = new List<MirInstruction>();
        if (sourceInitializer != null)
        {
            entryInstructions.Add(sourceInitializer);
        }

        if (instructions != null)
        {
            entryInstructions.AddRange(instructions);
        }

        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions = entryInstructions,
            Terminator = new MirSwitch
            {
                Discriminant = discriminant,
                Branches = branches.ToList(),
                DefaultTarget = defaultTarget
            }
        };
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    Locals = locals?.ToList() ?? [],
                    EntryBlockId = entry.Id,
                    BasicBlocks = [entry]
                }
            ]
        };

        var optimizer = new MirOptimizer();
        optimizer.RegisterPass(new DecisionSimplification());
        return Assert.Single(optimizer.Optimize(module).Functions);
    }

    private static MirFunc OptimizeFunction(
        IReadOnlyList<MirBasicBlock> blocks,
        IReadOnlyList<MirLocal> locals)
    {
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    Locals = locals.ToList(),
                    EntryBlockId = blocks[0].Id,
                    BasicBlocks = blocks.ToList()
                }
            ]
        };
        var optimizer = new MirOptimizer();
        optimizer.RegisterPass(new DecisionSimplification());
        return Assert.Single(optimizer.Optimize(module).Functions);
    }

    private static MirBasicBlock Block(
        int id,
        IReadOnlyList<MirInstruction> instructions,
        MirTerminator terminator) => new()
        {
            Id = BlockIdOf(id),
            IsEntry = id == 1,
            Instructions = instructions.ToList(),
            Terminator = terminator
        };

    private static BlockId BlockIdOf(int value) => new() { Value = value };

    private static MirLocal Local(int id, string name, bool isParameter = false) => new()
    {
        Id = new LocalId { Value = id },
        Name = name,
        TypeId = BoolType,
        IsParameter = isParameter
    };

    private static MirPlace Place(LocalId local) => new()
    {
        Kind = PlaceKind.Local,
        Local = local,
        TypeId = BoolType
    };

    private static MirSwitchBranch Branch(bool value, int target) => new()
    {
        Value = BoolConstant(value),
        Target = new BlockId { Value = target }
    };

    private static MirConstant BoolConstant(bool value) => new()
    {
        TypeId = BoolType,
        Value = new MirConstantValue.BoolValue(value)
    };

    private static MirConstant IntConstant(long value) => new()
    {
        TypeId = IntType,
        Value = new MirConstantValue.IntValue(value)
    };
}
