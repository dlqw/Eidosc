using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class ConditionalValueSelectionTests
{
    private static readonly TypeId BoolType = new(BaseTypes.BoolId);
    private static readonly TypeId IntType = new(BaseTypes.IntId);
    private static readonly TypeId StringType = new(BaseTypes.StringId);

    [Fact]
    public void RewritesPureScalarDiamondToSelect()
    {
        var module = BuildDiamond(IntType, IntConstant(1), IntConstant(2));
        var pass = new ConditionalValueSelection();

        var optimized = pass.Run(module);

        var entry = Assert.Single(Assert.Single(optimized.Functions).BasicBlocks, block => block.IsEntry);
        var select = Assert.IsType<MirSelect>(Assert.Single(entry.Instructions));
        Assert.Equal(IntConstant(1).Value, Assert.IsType<MirConstant>(select.TrueValue).Value);
        Assert.Equal(IntConstant(2).Value, Assert.IsType<MirConstant>(select.FalseValue).Value);
        Assert.Equal(BlockIdOf(4), Assert.IsType<MirGoto>(entry.Terminator).Target);
        Assert.Equal(1, pass.GetMetricsSnapshot()["decisions.representation.select"]);
        Assert.Equal(1, pass.GetMetricsSnapshot()["decisions.representation.conditional_branch_candidate"]);
    }

    [Fact]
    public void PreservesDiamondWithEffectfulArm()
    {
        var module = BuildDiamond(IntType, IntConstant(1), IntConstant(2));
        module.Functions[0].BasicBlocks.Single(block => block.Id == BlockIdOf(2)).Instructions.Insert(
            0,
            new MirCall
            {
                Function = new MirFunctionRef { Name = "effect", TypeId = IntType },
                Arguments = []
            });
        var pass = new ConditionalValueSelection();

        var optimized = pass.Run(module);

        Assert.Same(module, optimized);
        Assert.Equal(1, pass.GetMetricsSnapshot()["decisions.representation.preserved.effect"]);
    }

    [Fact]
    public void PreservesDiamondWithOwnedValues()
    {
        var module = BuildDiamond(
            StringType,
            new MirConstant { TypeId = StringType, Value = new MirConstantValue.StringValue("left") },
            new MirConstant { TypeId = StringType, Value = new MirConstantValue.StringValue("right") });
        var pass = new ConditionalValueSelection();

        var optimized = pass.Run(module);

        Assert.Same(module, optimized);
        Assert.Equal(1, pass.GetMetricsSnapshot()["decisions.representation.preserved.ownership"]);
    }

    [Fact]
    public void LlvmLoweringEmitsSelectInstruction()
    {
        var module = BuildDiamond(IntType, IntConstant(1), IntConstant(2));
        var optimizer = new MirOptimizer();
        optimizer.RegisterPass(new ConditionalValueSelection());
        optimizer.RegisterPass(new DeadCodeElimination());
        var optimized = optimizer.Optimize(module);

        var ir = new LlvmEmitter().Emit(new MirToLlvmConverter().Convert(optimized));

        Assert.Contains("select i1", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsDenseMultiwaySwitchAsJumpTableCandidate()
    {
        var module = new MirModule
        {
            Name = "Multiway",
            Functions =
            [
                new MirFunc
                {
                    Name = "classify",
                    ReturnType = IntType,
                    EntryBlockId = BlockIdOf(1),
                    Locals = [new MirLocal { Id = new LocalId { Value = 1 }, Name = "tag", TypeId = IntType, IsParameter = true }],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = BlockIdOf(1),
                            IsEntry = true,
                            Terminator = new MirSwitch
                            {
                                Discriminant = LocalPlace(1, IntType),
                                Branches =
                                [
                                    new MirSwitchBranch { Value = IntConstant(1), Target = BlockIdOf(2) },
                                    new MirSwitchBranch { Value = IntConstant(2), Target = BlockIdOf(3) },
                                    new MirSwitchBranch { Value = IntConstant(3), Target = BlockIdOf(4) }
                                ],
                                DefaultTarget = BlockIdOf(5)
                            }
                        },
                        new MirBasicBlock { Id = BlockIdOf(2), Terminator = new MirReturn { Value = IntConstant(1) } },
                        new MirBasicBlock { Id = BlockIdOf(3), Terminator = new MirReturn { Value = IntConstant(2) } },
                        new MirBasicBlock { Id = BlockIdOf(4), Terminator = new MirReturn { Value = IntConstant(3) } },
                        new MirBasicBlock { Id = BlockIdOf(5), Terminator = new MirReturn { Value = IntConstant(0) } }
                    ]
                }
            ]
        };

        var pass = new ConditionalValueSelection();
        _ = pass.Run(module);

        Assert.Equal(1, pass.GetMetricsSnapshot()["decisions.representation.jump_table_candidate"]);
        Assert.Equal(0, pass.GetMetricsSnapshot()["decisions.representation.binary_tree_candidate"]);
    }

    private static MirModule BuildDiamond(TypeId resultType, MirOperand trueValue, MirOperand falseValue)
    {
        var condition = LocalPlace(1, BoolType);
        var result = LocalPlace(2, resultType);
        return new MirModule
        {
            Name = "Select",
            Functions =
            [
                new MirFunc
                {
                    Name = "choose",
                    ReturnType = resultType,
                    EntryBlockId = BlockIdOf(1),
                    Locals =
                    [
                        new MirLocal { Id = condition.Local, Name = "condition", TypeId = BoolType, IsParameter = true },
                        new MirLocal { Id = result.Local, Name = "result", TypeId = resultType }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = BlockIdOf(1),
                            IsEntry = true,
                            Terminator = new MirSwitch
                            {
                                Discriminant = condition,
                                Branches =
                                [
                                    new MirSwitchBranch
                                    {
                                        Value = BoolConstant(true),
                                        Target = BlockIdOf(2)
                                    }
                                ],
                                DefaultTarget = BlockIdOf(3)
                            }
                        },
                        ArmBlock(2, result, trueValue),
                        ArmBlock(3, result, falseValue),
                        new MirBasicBlock
                        {
                            Id = BlockIdOf(4),
                            Terminator = new MirReturn { Value = result }
                        }
                    ]
                }
            ]
        };
    }

    private static MirBasicBlock ArmBlock(int id, MirPlace target, MirOperand value) => new()
    {
        Id = BlockIdOf(id),
        Instructions = [new MirAssign { Target = target, Source = value }],
        Terminator = new MirGoto { Target = BlockIdOf(4) }
    };

    private static MirPlace LocalPlace(int id, TypeId typeId) => new()
    {
        Kind = PlaceKind.Local,
        Local = new LocalId { Value = id },
        TypeId = typeId
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

    private static BlockId BlockIdOf(int value) => new() { Value = value };
}
