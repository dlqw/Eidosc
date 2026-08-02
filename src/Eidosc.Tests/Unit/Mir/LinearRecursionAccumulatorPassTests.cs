using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public class LinearRecursionAccumulatorPassTests
{
    private static readonly TypeId IntType = new(1);

    [Fact]
    public void Run_FibShape_RewritesToAccumulatorLoop()
    {
        var entryId = new BlockId { Value = 1 };
        var baseId = new BlockId { Value = 2 };
        var recId = new BlockId { Value = 3 };
        var functionSymbol = new SymbolId(10);
        var n = Local(1, "n", isParameter: true);
        var cmp = Local(2, "cmp");
        var t1 = Local(3, "t1");
        var r1 = Local(4, "r1");
        var t2 = Local(5, "t2");
        var r2 = Local(6, "r2");
        var sum = Local(7, "sum");

        var nPlace = Place(n.Id);
        var entryBlock = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirBinOp
                {
                    Target = Place(cmp.Id),
                    Operator = BinaryOp.Lt,
                    Left = nPlace,
                    Right = IntConst(2)
                }
            ],
            Terminator = new MirSwitch
            {
                Discriminant = Place(cmp.Id),
                Branches = [Branch(true, baseId)],
                DefaultTarget = recId
            }
        };

        var baseBlock = new MirBasicBlock
        {
            Id = baseId,
            IsEntry = false,
            Instructions = [],
            Terminator = new MirReturn { Value = nPlace }
        };

        var recBlock = new MirBasicBlock
        {
            Id = recId,
            IsEntry = false,
            Instructions =
            [
                new MirBinOp { Target = Place(t1.Id), Operator = BinaryOp.Sub, Left = nPlace, Right = IntConst(1) },
                new MirCall
                {
                    Target = Place(r1.Id),
                    Function = FunctionRef("fib", functionSymbol),
                    Arguments = [Place(t1.Id)]
                },
                new MirBinOp { Target = Place(t2.Id), Operator = BinaryOp.Sub, Left = nPlace, Right = IntConst(2) },
                new MirCall
                {
                    Target = Place(r2.Id),
                    Function = FunctionRef("fib", functionSymbol),
                    Arguments = [Place(t2.Id)]
                },
                new MirBinOp
                {
                    Target = Place(sum.Id),
                    Operator = BinaryOp.Add,
                    Left = Place(r1.Id),
                    Right = Place(r2.Id)
                }
            ],
            Terminator = new MirReturn { Value = Place(sum.Id) }
        };

        var optimized = Optimize(new MirFunc
        {
            Name = "fib",
            SymbolId = functionSymbol,
            EntryBlockId = entryId,
            ReturnType = IntType,
            Locals = [n, cmp, t1, r1, t2, r2, sum],
            BasicBlocks = [entryBlock, baseBlock, recBlock]
        });

        // Recursion block replaced by init/loop/done; single tail call remains.
        Assert.NotSame(optimized, new MirFunc());
        Assert.Equal(5, optimized.BasicBlocks.Count);
        Assert.DoesNotContain(optimized.BasicBlocks, block => block.Id == recId);

        var calls = optimized.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>().ToArray();
        Assert.Single(calls);
        Assert.Equal("fib", ((MirFunctionRef)calls[0].Function).Name);

        // Parameter n is mutable; accumulator locals were added.
        Assert.True(optimized.Locals[0].IsMutable);
        Assert.Contains(optimized.Locals, static local => local.Name == "__fib_acc");
        Assert.Contains(optimized.Locals, static local => local.Name == "__fib_tmp");

        // Entry still guards n < 2 -> base; default now flows to init.
        var entry = optimized.BasicBlocks.Single(static block => block.IsEntry);
        var entrySwitch = Assert.IsType<MirSwitch>(entry.Terminator);
        Assert.Equal(baseId, entrySwitch.Branches[0].Target);
        Assert.NotNull(entrySwitch.DefaultTarget);
        Assert.NotEqual(recId, entrySwitch.DefaultTarget.Value);

        // Loop self-edge exists (backedge) after the init block.
        var init = optimized.BasicBlocks.Single(block => block.Id == entrySwitch.DefaultTarget.Value);
        var initGoto = Assert.IsType<MirGoto>(init.Terminator);
        var loop = optimized.BasicBlocks.Single(block => block.Id == initGoto.Target);
        var loopSwitch = Assert.IsType<MirSwitch>(loop.Terminator);
        Assert.Equal(loop.Id, loopSwitch.DefaultTarget.Value);
    }

    [Theory]
    [InlineData(1, 3)] // offsets not 1/2
    [InlineData(2, 4)]
    public void Run_NonFibonacciOffsets_ReturnsUnchanged(int offsetA, int offsetB)
    {
        var (func, _) = BuildFibLike(offsetA, offsetB, BinaryOp.Add, baseReturnsParam: true);
        var optimized = Optimize(func);
        Assert.Same(func, optimized);
    }

    [Fact]
    public void Run_MultiplyCombination_ReturnsUnchanged()
    {
        var (func, _) = BuildFibLike(1, 2, BinaryOp.Mul, baseReturnsParam: true);
        Assert.Same(func, Optimize(func));
    }

    [Fact]
    public void Run_BaseCaseNotReturningParam_ReturnsUnchanged()
    {
        var (func, _) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: false);
        Assert.Same(func, Optimize(func));
    }

    [Fact]
    public void Run_TwoParameters_ReturnsUnchanged()
    {
        var entryId = new BlockId { Value = 1 };
        var n = Local(1, "n", isParameter: true);
        var m = Local(2, "m", isParameter: true);
        var func = new MirFunc
        {
            Name = "fib",
            SymbolId = new SymbolId(10),
            EntryBlockId = entryId,
            ReturnType = IntType,
            Locals = [n, m],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = entryId,
                    IsEntry = true,
                    Instructions = [],
                    Terminator = new MirReturn { Value = Place(n.Id) }
                }
            ]
        };

        Assert.Same(func, Optimize(func));
    }

    private static (MirFunc Func, MirBasicBlock RecBlock) BuildFibLike(
        int offsetA,
        int offsetB,
        BinaryOp combineOp,
        bool baseReturnsParam)
    {
        var entryId = new BlockId { Value = 1 };
        var baseId = new BlockId { Value = 2 };
        var recId = new BlockId { Value = 3 };
        var functionSymbol = new SymbolId(10);
        var n = Local(1, "n", isParameter: true);
        var cmp = Local(2, "cmp");
        var t1 = Local(3, "t1");
        var r1 = Local(4, "r1");
        var t2 = Local(5, "t2");
        var r2 = Local(6, "r2");
        var sum = Local(7, "sum");

        var nPlace = Place(n.Id);
        var entryBlock = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirBinOp
                {
                    Target = Place(cmp.Id),
                    Operator = BinaryOp.Lt,
                    Left = nPlace,
                    Right = IntConst(2)
                }
            ],
            Terminator = new MirSwitch
            {
                Discriminant = Place(cmp.Id),
                Branches = [Branch(true, baseId)],
                DefaultTarget = recId
            }
        };

        var baseBlock = new MirBasicBlock
        {
            Id = baseId,
            IsEntry = false,
            Instructions = [],
            Terminator = new MirReturn { Value = baseReturnsParam ? nPlace : IntConst(0) }
        };

        var recBlock = new MirBasicBlock
        {
            Id = recId,
            IsEntry = false,
            Instructions =
            [
                new MirBinOp { Target = Place(t1.Id), Operator = BinaryOp.Sub, Left = nPlace, Right = IntConst(offsetA) },
                new MirCall
                {
                    Target = Place(r1.Id),
                    Function = FunctionRef("fib", functionSymbol),
                    Arguments = [Place(t1.Id)]
                },
                new MirBinOp { Target = Place(t2.Id), Operator = BinaryOp.Sub, Left = nPlace, Right = IntConst(offsetB) },
                new MirCall
                {
                    Target = Place(r2.Id),
                    Function = FunctionRef("fib", functionSymbol),
                    Arguments = [Place(t2.Id)]
                },
                new MirBinOp
                {
                    Target = Place(sum.Id),
                    Operator = combineOp,
                    Left = Place(r1.Id),
                    Right = Place(r2.Id)
                }
            ],
            Terminator = new MirReturn { Value = Place(sum.Id) }
        };

        var func = new MirFunc
        {
            Name = "fib",
            SymbolId = functionSymbol,
            EntryBlockId = entryId,
            ReturnType = IntType,
            Locals = [n, cmp, t1, r1, t2, r2, sum],
            BasicBlocks = [entryBlock, baseBlock, recBlock]
        };

        return (func, recBlock);
    }

    private static MirFunc Optimize(MirFunc func)
    {
        return new LinearRecursionAccumulatorPass().Run(new MirModule { Functions = [func] }).Functions[0];
    }

    private static MirLocal Local(int id, string name, bool isParameter = false)
    {
        return new MirLocal
        {
            Id = new LocalId { Value = id },
            Name = name,
            TypeId = IntType,
            IsMutable = false,
            IsParameter = isParameter,
            Span = default
        };
    }

    private static MirPlace Place(LocalId localId)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = localId,
            TypeId = IntType,
            Span = default
        };
    }

    private static MirConstant IntConst(long value)
    {
        return new MirConstant
        {
            Value = new MirConstantValue.IntValue(value),
            TypeId = IntType,
            Span = default
        };
    }

    private static MirSwitchBranch Branch(bool value, BlockId target)
    {
        return new MirSwitchBranch
        {
            Value = new MirConstant
            {
                Value = new MirConstantValue.BoolValue(value),
                TypeId = IntType,
                Span = default
            },
            Target = target
        };
    }

    private static MirFunctionRef FunctionRef(string name, SymbolId symbolId)
    {
        return new MirFunctionRef
        {
            Name = name,
            SymbolId = symbolId,
            Span = default
        };
    }
}
