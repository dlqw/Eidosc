using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

/// <summary>
/// Constant folding of pure calls with all-constant arguments, including
/// recursive calls (bounded by depth/step budgets) and the negative cases
/// (effectful callees, non-constant arguments, missing summaries).
/// </summary>
public sealed class ConstantFoldingCallTests
{
    private static readonly TypeId IntType = new(BaseTypes.IntId);

    [Fact]
    public void ConstantFolding_PureIdentityCallWithConstantArgument_Folds()
    {
        var callee = CreateIdentityFunction("pure", new SymbolId(301));
        var caller = CreateConstantCallFunction(callee, new SymbolId(302), IntConstant(7));
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(callee, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        var instruction = Assert.IsType<MirAssign>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
        Assert.Equal(7L, Assert.IsType<MirConstantValue.IntValue>(Assert.IsType<MirConstant>(instruction.Source).Value).Value);
    }

    [Fact]
    public void ConstantFolding_RecursiveFactorialCall_Folds()
    {
        var factorial = CreateFactorialFunction("factorial", new SymbolId(303));
        var caller = CreateConstantCallFunction(factorial, new SymbolId(304), IntConstant(5));
        var module = new MirModule { Name = "Main", Functions = [factorial, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(factorial, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        var instruction = Assert.IsType<MirAssign>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
        Assert.Equal(120L, Assert.IsType<MirConstantValue.IntValue>(Assert.IsType<MirConstant>(instruction.Source).Value).Value);
    }

    [Fact]
    public void ConstantFolding_FibonacciWithinStepBudget_Folds()
    {
        var fibonacci = CreateFibonacciFunction("fibonacci", new SymbolId(305));
        var caller = CreateConstantCallFunction(fibonacci, new SymbolId(306), IntConstant(20));
        var module = new MirModule { Name = "Main", Functions = [fibonacci, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(fibonacci, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        var instruction = Assert.IsType<MirAssign>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
        Assert.Equal(6765L, Assert.IsType<MirConstantValue.IntValue>(Assert.IsType<MirConstant>(instruction.Source).Value).Value);
    }

    [Fact]
    public void ConstantFolding_RecursiveCallBeyondDepthLimit_NotFolded()
    {
        var factorial = CreateFactorialFunction("factorial", new SymbolId(307));
        var caller = CreateConstantCallFunction(factorial, new SymbolId(308), IntConstant(100));
        var module = new MirModule { Name = "Main", Functions = [factorial, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(factorial, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        Assert.IsType<MirCall>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
    }

    [Fact]
    public void ConstantFolding_FibonacciWithRepeatedSubproblems_UsesMemoizedResults()
    {
        var fibonacci = CreateFibonacciFunction("fibonacci", new SymbolId(309));
        var caller = CreateConstantCallFunction(fibonacci, new SymbolId(310), IntConstant(32));
        var module = new MirModule { Name = "Main", Functions = [fibonacci, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(fibonacci, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        var instruction = Assert.IsType<MirAssign>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
        Assert.Equal(2178309L, Assert.IsType<MirConstantValue.IntValue>(Assert.IsType<MirConstant>(instruction.Source).Value).Value);
    }

    [Fact]
    public void ConstantFolding_EffectfulCallee_NotFolded()
    {
        var callee = CreateIdentityFunction("effectful", new SymbolId(311));
        var caller = CreateConstantCallFunction(callee, new SymbolId(312), IntConstant(7));
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var effects = new EffectRow([new EffectTag(new SymbolId(601), "IO")]);
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(callee, effects));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        Assert.IsType<MirCall>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
    }

    [Fact]
    public void ConstantFolding_CalleeWithoutTrustedSummary_NotFolded()
    {
        var callee = CreateIdentityFunction("unknown", SymbolId.None);
        var caller = CreateConstantCallFunction(callee, new SymbolId(313), IntConstant(7));
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var optimizer = new MirOptimizer(effectSummaries: new Dictionary<SymbolId, FunctionEffectSummary>());
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        Assert.IsType<MirCall>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
    }

    [Fact]
    public void ConstantFolding_NonConstantArgument_NotFolded()
    {
        var callee = CreateIdentityFunction("pure", new SymbolId(314));
        var argument = Local(1, "argument", isParameter: true);
        var result = Local(2, "result");
        var caller = new MirFunc
        {
            Name = "caller",
            SymbolId = new SymbolId(315),
            FunctionId = new FunctionId { SymbolId = new SymbolId(315), Name = "caller", QualifiedName = "Main.caller" },
            Locals = [argument, result],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions = [Call(result.Id, CreateFunctionRef(callee), Place(argument.Id))],
                    Terminator = new MirReturn { Value = Place(result.Id) }
                }
            ]
        };
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(callee, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        Assert.IsType<MirCall>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
    }

    [Fact]
    public void ConstantFolding_NestedPureCallsWithCopyInBetween_Folds()
    {
        var fibonacci = CreateFibonacciFunction("fibonacci", new SymbolId(316));
        var square = CreateSquareFunction("square", new SymbolId(317));
        var first = Local(1, "first");
        var copied = Local(2, "copied");
        var result = Local(3, "result");
        var caller = new MirFunc
        {
            Name = "caller",
            SymbolId = new SymbolId(318),
            FunctionId = new FunctionId { SymbolId = new SymbolId(318), Name = "caller", QualifiedName = "Main.caller" },
            Locals = [first, copied, result],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions =
                    [
                        Call(first.Id, CreateFunctionRef(fibonacci), IntConstant(20)),
                        Copy(copied.Id, Place(first.Id)),
                        Call(result.Id, CreateFunctionRef(square), Place(copied.Id))
                    ],
                    Terminator = new MirReturn { Value = Place(result.Id) }
                }
            ]
        };
        var module = new MirModule { Name = "Main", Functions = [fibonacci, square, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(
            (fibonacci, EffectRow.Pure),
            (square, EffectRow.Pure)));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        var instructions = optimized.Functions[2].BasicBlocks.Single().Instructions;
        Assert.Equal(3, instructions.Count);
        Assert.DoesNotContain(instructions, static instruction => instruction is MirCall);
        var firstAssign = Assert.IsType<MirAssign>(instructions[0]);
        Assert.Equal(6765L, Assert.IsType<MirConstantValue.IntValue>(Assert.IsType<MirConstant>(firstAssign.Source).Value).Value);
        var copyAssign = Assert.IsType<MirAssign>(instructions[1]);
        Assert.Equal(6765L, Assert.IsType<MirConstantValue.IntValue>(Assert.IsType<MirConstant>(copyAssign.Source).Value).Value);
        var resultAssign = Assert.IsType<MirAssign>(instructions[2]);
        Assert.Equal(45765225L, Assert.IsType<MirConstantValue.IntValue>(Assert.IsType<MirConstant>(resultAssign.Source).Value).Value);
    }

    [Fact]
    public void ConstantFolding_EmptySelfLoop_StopsAtSharedBudget()
    {
        var loop = CreateSelfLoopFunction("self_loop", new SymbolId(319));
        var caller = CreateConstantCallFunction(loop, new SymbolId(320), IntConstant(0));
        var module = new MirModule { Name = "Main", Functions = [loop, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(loop, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        Assert.IsType<MirCall>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
    }

    [Fact]
    public void ConstantFolding_EmptyTwoBlockLoop_StopsAtSharedBudget()
    {
        var loop = CreateTwoBlockLoopFunction("two_block_loop", new SymbolId(321));
        var caller = CreateConstantCallFunction(loop, new SymbolId(322), IntConstant(0));
        var module = new MirModule { Name = "Main", Functions = [loop, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(loop, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        Assert.IsType<MirCall>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
    }

    [Fact]
    public void ConstantFolding_ConstantSwitchLoop_StopsAtSharedBudget()
    {
        var loop = CreateConstantSwitchLoopFunction("switch_loop", new SymbolId(323));
        var caller = CreateConstantCallFunction(loop, new SymbolId(324), IntConstant(0));
        var module = new MirModule { Name = "Main", Functions = [loop, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(loop, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        Assert.IsType<MirCall>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
    }

    [Fact]
    public void ConstantFolding_RecursiveCallWithUnchangedArguments_DetectsInProgressCycle()
    {
        var recursive = CreateNonProgressingRecursiveFunction("recursive_loop", new SymbolId(325));
        var caller = CreateConstantCallFunction(recursive, new SymbolId(326), IntConstant(0));
        var module = new MirModule { Name = "Main", Functions = [recursive, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(recursive, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        Assert.IsType<MirCall>(optimized.Functions[1].BasicBlocks.Single().Instructions[0]);
    }

    [Fact]
    public void ConstantFolding_RepeatedIdenticalCalls_ReusesMemoizedResult()
    {
        var fibonacci = CreateFibonacciFunction("fibonacci", new SymbolId(327));
        var caller = CreateRepeatedConstantCallFunction(fibonacci, new SymbolId(328), IntConstant(32));
        var module = new MirModule { Name = "Main", Functions = [fibonacci, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(fibonacci, EffectRow.Pure));
        optimizer.RegisterPass(new ConstantFolding());

        var optimized = optimizer.Optimize(module);

        var instructions = optimized.Functions[1].BasicBlocks.Single().Instructions;
        Assert.All(instructions, static instruction => Assert.IsType<MirAssign>(instruction));
        Assert.All(
            instructions.Cast<MirAssign>(),
            static instruction => Assert.Equal(
                2178309L,
                Assert.IsType<MirConstantValue.IntValue>(Assert.IsType<MirConstant>(instruction.Source).Value).Value));
    }

    private static MirFunc CreateSquareFunction(string name, SymbolId symbolId)
    {
        var parameter = Local(1, "x", isParameter: true);
        var result = Local(2, "result");
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name, QualifiedName = $"Main.{name}" },
            Locals = [parameter, result],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions = [BinOp(result.Id, BinaryOp.Mul, Place(parameter.Id), Place(parameter.Id))],
                    Terminator = new MirReturn { Value = Place(result.Id) }
                }
            ]
        };
    }

    private static MirFunc CreateSelfLoopFunction(string name, SymbolId symbolId)
    {
        var parameter = Local(1, "value", isParameter: true);
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name, QualifiedName = $"Main.{name}" },
            Locals = [parameter],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Terminator = new MirGoto { Target = Block(1) }
                }
            ]
        };
    }

    private static MirFunc CreateTwoBlockLoopFunction(string name, SymbolId symbolId)
    {
        var parameter = Local(1, "value", isParameter: true);
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name, QualifiedName = $"Main.{name}" },
            Locals = [parameter],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Terminator = new MirGoto { Target = Block(2) }
                },
                new MirBasicBlock
                {
                    Id = Block(2),
                    Terminator = new MirGoto { Target = Block(1) }
                }
            ]
        };
    }

    private static MirFunc CreateConstantSwitchLoopFunction(string name, SymbolId symbolId)
    {
        var parameter = Local(1, "value", isParameter: true);
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name, QualifiedName = $"Main.{name}" },
            Locals = [parameter],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Terminator = new MirSwitch
                    {
                        Discriminant = BoolConstant(true),
                        Branches = [new MirSwitchBranch { Value = BoolConstant(true), Target = Block(1) }],
                        DefaultTarget = Block(2)
                    }
                },
                new MirBasicBlock
                {
                    Id = Block(2),
                    Terminator = new MirReturn { Value = Place(parameter.Id) }
                }
            ]
        };
    }

    private static MirFunc CreateNonProgressingRecursiveFunction(string name, SymbolId symbolId)
    {
        var parameter = Local(1, "value", isParameter: true);
        var result = Local(2, "result");
        var functionId = new FunctionId { SymbolId = symbolId, Name = name, QualifiedName = $"Main.{name}" };
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = functionId,
            Locals = [parameter, result],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions =
                    [
                        Call(
                            result.Id,
                            new MirFunctionRef { Name = name, SymbolId = symbolId, FunctionId = functionId },
                            Place(parameter.Id))
                    ],
                    Terminator = new MirReturn { Value = Place(result.Id) }
                }
            ]
        };
    }

    private static Dictionary<SymbolId, FunctionEffectSummary> CreateSummaries(params (MirFunc Function, EffectRow Effects)[] bindings) =>
        bindings.ToDictionary(
            static binding => binding.Function.SymbolId,
            static binding => new FunctionEffectSummary(binding.Effects, binding.Effects));

    private static MirFunc CreateIdentityFunction(string name, SymbolId symbolId)
    {
        var parameter = Local(1, "value", isParameter: true);
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name, QualifiedName = $"Main.{name}" },
            Locals = [parameter],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Terminator = new MirReturn { Value = Place(parameter.Id) }
                }
            ]
        };
    }

    private static MirFunc CreateFactorialFunction(string name, SymbolId symbolId)
    {
        var parameter = Local(1, "n", isParameter: true);
        var cond = Local(2, "cond");
        var m = Local(3, "m");
        var r = Local(4, "r");
        var result = Local(5, "result");
        var reference = new MirFunctionRef
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name, QualifiedName = $"Main.{name}" }
        };
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = reference.FunctionId,
            Locals = [parameter, cond, m, r, result],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions = [BinOp(cond.Id, BinaryOp.Lt, Place(parameter.Id), IntConstant(2))],
                    Terminator = Switch(Place(cond.Id), baseBlock: Block(2), recursiveBlock: Block(3))
                },
                new MirBasicBlock
                {
                    Id = Block(2),
                    Terminator = new MirReturn { Value = IntConstant(1) }
                },
                new MirBasicBlock
                {
                    Id = Block(3),
                    Instructions =
                    [
                        BinOp(m.Id, BinaryOp.Sub, Place(parameter.Id), IntConstant(1)),
                        Call(r.Id, reference, Place(m.Id)),
                        BinOp(result.Id, BinaryOp.Mul, Place(parameter.Id), Place(r.Id))
                    ],
                    Terminator = new MirReturn { Value = Place(result.Id) }
                }
            ]
        };
    }

    private static MirFunc CreateFibonacciFunction(string name, SymbolId symbolId)
    {
        var parameter = Local(1, "n", isParameter: true);
        var cond = Local(2, "cond");
        var m1 = Local(3, "m1");
        var a = Local(4, "a");
        var m2 = Local(5, "m2");
        var b = Local(6, "b");
        var sum = Local(7, "sum");
        var reference = new MirFunctionRef
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name, QualifiedName = $"Main.{name}" }
        };
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = reference.FunctionId,
            Locals = [parameter, cond, m1, a, m2, b, sum],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions = [BinOp(cond.Id, BinaryOp.Lt, Place(parameter.Id), IntConstant(2))],
                    Terminator = Switch(Place(cond.Id), baseBlock: Block(2), recursiveBlock: Block(3))
                },
                new MirBasicBlock
                {
                    Id = Block(2),
                    Terminator = new MirReturn { Value = Place(parameter.Id) }
                },
                new MirBasicBlock
                {
                    Id = Block(3),
                    Instructions =
                    [
                        BinOp(m1.Id, BinaryOp.Sub, Place(parameter.Id), IntConstant(1)),
                        Call(a.Id, reference, Place(m1.Id)),
                        BinOp(m2.Id, BinaryOp.Sub, Place(parameter.Id), IntConstant(2)),
                        Call(b.Id, reference, Place(m2.Id)),
                        BinOp(sum.Id, BinaryOp.Add, Place(a.Id), Place(b.Id))
                    ],
                    Terminator = new MirReturn { Value = Place(sum.Id) }
                }
            ]
        };
    }

    private static MirFunc CreateConstantCallFunction(MirFunc callee, SymbolId symbolId, MirConstant argument)
    {
        var result = Local(1, "result");
        return new MirFunc
        {
            Name = "caller",
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = "caller", QualifiedName = "Main.caller" },
            Locals = [result],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions = [Call(result.Id, CreateFunctionRef(callee), argument)],
                    Terminator = new MirReturn { Value = Place(result.Id) }
                }
            ]
        };
    }

    private static MirFunc CreateRepeatedConstantCallFunction(MirFunc callee, SymbolId symbolId, MirConstant argument)
    {
        var first = Local(1, "first");
        var second = Local(2, "second");
        return new MirFunc
        {
            Name = "caller",
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = "caller", QualifiedName = "Main.caller" },
            Locals = [first, second],
            EntryBlockId = Block(1),
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions =
                    [
                        Call(first.Id, CreateFunctionRef(callee), argument),
                        Call(second.Id, CreateFunctionRef(callee), argument)
                    ],
                    Terminator = new MirReturn { Value = Place(second.Id) }
                }
            ]
        };
    }

    private static Dictionary<SymbolId, FunctionEffectSummary> CreateSummaries(
        MirFunc function,
        EffectRow effects) => new()
        {
            [function.SymbolId] = new FunctionEffectSummary(effects, effects)
        };

    private static MirCall Call(LocalId target, MirFunctionRef function, params MirOperand[] arguments) => new()
    {
        Target = Place(target),
        Function = function,
        Arguments = [.. arguments]
    };

    private static MirBinOp BinOp(LocalId target, BinaryOp op, MirOperand left, MirOperand right) => new()
    {
        Target = Place(target),
        Operator = op,
        Left = left,
        Right = right
    };

    private static MirCopy Copy(LocalId target, MirPlace source) => new()
    {
        Target = Place(target),
        Source = source
    };

    private static MirSwitch Switch(MirOperand discriminant, BlockId baseBlock, BlockId recursiveBlock) => new()
    {
        Discriminant = discriminant,
        Branches = [new MirSwitchBranch { Value = BoolConstant(true), Target = baseBlock }],
        DefaultTarget = recursiveBlock
    };

    private static MirConstant IntConstant(long value) => new()
    {
        Value = new MirConstantValue.IntValue(value),
        TypeId = IntType
    };

    private static MirConstant BoolConstant(bool value) => new()
    {
        Value = new MirConstantValue.BoolValue(value),
        TypeId = new TypeId(BaseTypes.BoolId)
    };

    private static MirFunctionRef CreateFunctionRef(MirFunc function) => new()
    {
        Name = function.Name,
        SymbolId = function.SymbolId,
        FunctionId = function.FunctionId
    };

    private static MirLocal Local(int id, string name, bool isParameter = false) => new()
    {
        Id = new LocalId { Value = id },
        Name = name,
        TypeId = IntType,
        IsParameter = isParameter
    };

    private static MirPlace Place(LocalId local) => new()
    {
        Kind = PlaceKind.Local,
        Local = local,
        TypeId = IntType
    };

    private static BlockId Block(int value) => new() { Value = value };
}
