using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public class TailCallOptimizationTests
{
    private static readonly TypeId IntType = new(1);

    [Fact]
    public void Run_SelfTailCall_RewritesToLoopWithParallelArgumentStaging()
    {
        var entryId = new BlockId { Value = 1 };
        var functionSymbol = new SymbolId(10);
        var paramA = Local(1, "a", isParameter: true);
        var paramB = Local(2, "b", isParameter: true);
        var result = Local(3, "result");

        var callTarget = Place(result.Id);
        var block = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = callTarget,
                    Function = FunctionRef("swap", functionSymbol),
                    Arguments = [Place(paramB.Id), Place(paramA.Id)]
                }
            ],
            Terminator = new MirReturn { Value = callTarget }
        };

        var optimized = Optimize(new MirFunc
        {
            Name = "swap",
            SymbolId = functionSymbol,
            EntryBlockId = entryId,
            Locals = [paramA, paramB, result],
            BasicBlocks = [block]
        });

        Assert.NotEqual(entryId, optimized.EntryBlockId);
        var optimizedBlock = optimized.BasicBlocks.Single(block => block.Id == entryId);
        Assert.IsType<MirGoto>(optimizedBlock.Terminator);
        Assert.DoesNotContain(optimizedBlock.Instructions, static instruction => instruction is MirCall);

        Assert.Equal(5, optimized.Locals.Count);
        Assert.Equal(["__tail_arg_0", "__tail_arg_1"], optimized.Locals.Skip(3).Select(static local => local.Name).ToArray());

        Assert.True(optimized.Locals[0].IsMutable);
        Assert.True(optimized.Locals[1].IsMutable);

        var assignments = optimizedBlock.Instructions.OfType<MirAssign>().ToArray();
        Assert.Equal(2, assignments.Length);
        Assert.Equal(paramB.Id, Assert.IsType<MirPlace>(assignments[0].Source).Local);
        Assert.Equal(paramA.Id, Assert.IsType<MirPlace>(assignments[1].Source).Local);

        var stores = optimizedBlock.Instructions.OfType<MirStore>().ToArray();
        Assert.Equal(2, stores.Length);
        Assert.Equal(paramA.Id, stores[0].Target.Local);
        Assert.Equal(assignments[0].Target.Local, Assert.IsType<MirPlace>(stores[0].Value).Local);
        Assert.Equal(paramB.Id, stores[1].Target.Local);
        Assert.Equal(assignments[1].Target.Local, Assert.IsType<MirPlace>(stores[1].Value).Local);
    }

    [Fact]
    public void Run_NonSelfTailCallThroughMergeBlock_NormalizesAndMarksTailCall()
    {
        var entryId = new BlockId { Value = 1 };
        var mergeId = new BlockId { Value = 2 };
        var result = Local(1, "result");
        var callResult = Local(2, "callResult");

        var entryBlock = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = Place(callResult.Id),
                    Function = FunctionRef("callee", new SymbolId(20)),
                    Arguments = []
                },
                new MirCopy
                {
                    Target = Place(result.Id),
                    Source = Place(callResult.Id)
                }
            ],
            Terminator = new MirGoto { Target = mergeId }
        };
        var mergeBlock = new MirBasicBlock
        {
            Id = mergeId,
            Terminator = new MirReturn { Value = Place(result.Id) }
        };

        var optimized = Optimize(new MirFunc
        {
            Name = "caller",
            SymbolId = new SymbolId(21),
            EntryBlockId = entryId,
            Locals = [result, callResult],
            BasicBlocks = [entryBlock, mergeBlock]
        });

        var optimizedEntry = optimized.BasicBlocks.Single(block => block.Id == entryId);
        var tailCall = Assert.IsType<MirCall>(Assert.Single(optimizedEntry.Instructions));
        Assert.True(tailCall.IsTailCall);
        var ret = Assert.IsType<MirReturn>(optimizedEntry.Terminator);
        Assert.Equal(callResult.Id, Assert.IsType<MirPlace>(ret.Value).Local);
    }

    [Fact]
    public void Run_SelfTailCallThroughMergeBlock_RewritesBranchToLoop()
    {
        var entryId = new BlockId { Value = 1 };
        var mergeId = new BlockId { Value = 2 };
        var functionSymbol = new SymbolId(30);
        var param = Local(1, "n", isParameter: true);
        var result = Local(2, "result");
        var callResult = Local(3, "callResult");

        var entryBlock = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = Place(callResult.Id),
                    Function = FunctionRef("countdown", functionSymbol),
                    Arguments = [Place(param.Id)]
                },
                new MirCopy
                {
                    Target = Place(result.Id),
                    Source = Place(callResult.Id)
                }
            ],
            Terminator = new MirGoto { Target = mergeId }
        };
        var mergeBlock = new MirBasicBlock
        {
            Id = mergeId,
            Terminator = new MirReturn { Value = Place(result.Id) }
        };

        var optimized = Optimize(new MirFunc
        {
            Name = "countdown",
            SymbolId = functionSymbol,
            EntryBlockId = entryId,
            Locals = [param, result, callResult],
            BasicBlocks = [entryBlock, mergeBlock]
        });

        var optimizedEntry = optimized.BasicBlocks.Single(block => block.Id == entryId);
        Assert.DoesNotContain(optimizedEntry.Instructions, static instruction => instruction is MirCall);
        var jump = Assert.IsType<MirGoto>(optimizedEntry.Terminator);
        Assert.Equal(entryId, jump.Target);
        Assert.NotEqual(entryId, optimized.EntryBlockId);
    }

    [Fact]
    public void Run_SelfTailCallThroughSameBlockAlias_RewritesToLoop()
    {
        var entryId = new BlockId { Value = 1 };
        var functionSymbol = new SymbolId(35);
        var param = Local(1, "n", isParameter: true);
        var result = Local(2, "result");
        var callResult = Local(3, "callResult");

        var block = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = Place(callResult.Id),
                    Function = FunctionRef("countdown", functionSymbol),
                    Arguments = [Place(param.Id)]
                },
                new MirCopy
                {
                    Target = Place(result.Id),
                    Source = Place(callResult.Id)
                }
            ],
            Terminator = new MirReturn { Value = Place(result.Id) }
        };

        var optimized = Optimize(new MirFunc
        {
            Name = "countdown",
            SymbolId = functionSymbol,
            EntryBlockId = entryId,
            Locals = [param, result, callResult],
            BasicBlocks = [block]
        });

        var optimizedBlock = optimized.BasicBlocks.Single(block => block.Id == entryId);
        Assert.DoesNotContain(optimizedBlock.Instructions, static instruction => instruction is MirCall);
        var jump = Assert.IsType<MirGoto>(optimizedBlock.Terminator);
        Assert.Equal(entryId, jump.Target);
        Assert.NotEqual(entryId, optimized.EntryBlockId);
    }

    [Fact]
    public void Run_MutualTailCalls_MarksCallsForLlvmTailLowering()
    {
        var evenId = new BlockId { Value = 1 };
        var oddId = new BlockId { Value = 2 };
        var evenSymbol = new SymbolId(36);
        var oddSymbol = new SymbolId(37);
        var evenResult = Local(1, "evenResult");
        var oddResult = Local(2, "oddResult");

        var even = new MirFunc
        {
            Name = "even",
            SymbolId = evenSymbol,
            EntryBlockId = evenId,
            Locals = [evenResult],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = evenId,
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = Place(evenResult.Id),
                            Function = FunctionRef("odd", oddSymbol),
                            Arguments = []
                        }
                    ],
                    Terminator = new MirReturn { Value = Place(evenResult.Id) }
                }
            ]
        };

        var odd = new MirFunc
        {
            Name = "odd",
            SymbolId = oddSymbol,
            EntryBlockId = oddId,
            Locals = [oddResult],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = oddId,
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = Place(oddResult.Id),
                            Function = FunctionRef("even", evenSymbol),
                            Arguments = []
                        }
                    ],
                    Terminator = new MirReturn { Value = Place(oddResult.Id) }
                }
            ]
        };

        var optimized = new TailCallOptimization().Run(new MirModule
        {
            Name = "Test",
            Functions = [even, odd]
        });

        var optimizedEvenCall = Assert.IsType<MirCall>(
            Assert.Single(optimized.Functions.Single(function => function.Name == "even").BasicBlocks.Single().Instructions));
        var optimizedOddCall = Assert.IsType<MirCall>(
            Assert.Single(optimized.Functions.Single(function => function.Name == "odd").BasicBlocks.Single().Instructions));

        Assert.True(optimizedEvenCall.IsTailCall);
        Assert.True(optimizedOddCall.IsTailCall);
    }

    [Fact]
    public void Run_CallWithWorkAfterIt_DoesNotMarkTailCall()
    {
        var entryId = new BlockId { Value = 1 };
        var callResult = Local(1, "callResult");
        var afterCall = Local(2, "afterCall");

        var block = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = Place(callResult.Id),
                    Function = FunctionRef("callee", new SymbolId(40)),
                    Arguments = []
                },
                new MirAssign
                {
                    Target = Place(afterCall.Id),
                    Source = Place(callResult.Id)
                }
            ],
            Terminator = new MirReturn { Value = Place(callResult.Id) }
        };

        var optimized = Optimize(new MirFunc
        {
            Name = "caller",
            SymbolId = new SymbolId(41),
            EntryBlockId = entryId,
            Locals = [callResult, afterCall],
            BasicBlocks = [block]
        });

        var optimizedBlock = Assert.Single(optimized.BasicBlocks);
        var call = Assert.IsType<MirCall>(optimizedBlock.Instructions[0]);
        Assert.False(call.IsTailCall);
        Assert.Equal(2, optimizedBlock.Instructions.Count);
    }

    [Fact]
    public void Run_ClosureTailCall_MarksIndirectCallForLlvmTailLowering()
    {
        var entryId = new BlockId { Value = 1 };
        var closure = Local(1, "closure", isParameter: true);
        var argument = Local(2, "argument", isParameter: true);
        var result = Local(3, "result");
        var callTarget = Place(result.Id);

        var block = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = callTarget,
                    Function = Place(closure.Id),
                    Arguments = [Place(argument.Id)]
                }
            ],
            Terminator = new MirReturn { Value = callTarget }
        };

        var optimized = Optimize(new MirFunc
        {
            Name = "apply",
            SymbolId = new SymbolId(50),
            EntryBlockId = entryId,
            Locals = [closure, argument, result],
            BasicBlocks = [block]
        });

        var optimizedBlock = Assert.Single(optimized.BasicBlocks);
        var tailCall = Assert.IsType<MirCall>(Assert.Single(optimizedBlock.Instructions));
        Assert.True(tailCall.IsTailCall);
        Assert.IsType<MirPlace>(tailCall.Function);
    }

    private static MirFunc Optimize(MirFunc function)
    {
        var module = new MirModule
        {
            Name = "Test",
            Functions = [function]
        };

        return Assert.Single(new TailCallOptimization().Run(module).Functions);
    }

    private static MirLocal Local(int id, string name, bool isParameter = false)
    {
        return new MirLocal
        {
            Id = new LocalId { Value = id },
            Name = name,
            TypeId = IntType,
            IsParameter = isParameter
        };
    }

    private static MirPlace Place(LocalId localId)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = localId,
            TypeId = IntType
        };
    }

    private static MirFunctionRef FunctionRef(string name, SymbolId symbolId)
    {
        return new MirFunctionRef
        {
            Name = name,
            SymbolId = symbolId
        };
    }
}
