using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class EffectAwareMirOptimizationTests
{
    private static readonly TypeId IntType = new(BaseTypes.IntId);

    [Fact]
    public void Analyze_TrustedPureLeaf_AllowsEliminationAndReuse()
    {
        var callee = CreateIdentityFunction("pure", new SymbolId(101));
        var module = new MirModule { Name = "Main", Functions = [callee] };
        var summaries = CreateSummaries(callee, EffectRow.Pure);

        var index = FunctionOptimizationSummaryAnalyzer.Analyze(module, summaries);

        Assert.True(index.TryGet(CreateFunctionRef(callee), out var summary));
        Assert.True(summary.IsTrusted);
        Assert.True(summary.CanEliminateUnusedCall);
        Assert.True(summary.CanReuseCallResult);
        Assert.Equal(FunctionMemoryBehavior.None, summary.Memory);
        Assert.Equal(FunctionDeterminism.Deterministic, summary.Determinism);
    }

    [Fact]
    public void Analyze_DeclaredEffect_ConservativelyMarksUnknownRuntimeBehavior()
    {
        var callee = CreateIdentityFunction("effectful", new SymbolId(108));
        var effects = new EffectRow([new EffectTag(new SymbolId(503), "ExternalState")]);
        var module = new MirModule { Name = "Main", Functions = [callee] };

        var index = FunctionOptimizationSummaryAnalyzer.Analyze(
            module,
            CreateSummaries(callee, effects));

        Assert.True(index.TryGet(CreateFunctionRef(callee), out var summary));
        Assert.True(summary.IsTrusted);
        Assert.Equal(effects, summary.Effects);
        Assert.Equal(FunctionMemoryBehavior.Unknown, summary.Memory);
        Assert.True(summary.MayPanic);
        Assert.True(summary.MayDiverge);
        Assert.True(summary.MaySuspend);
        Assert.True(summary.MayBlock);
        Assert.True(summary.MayAllocate);
        Assert.True(summary.MaySynchronize);
        Assert.Equal(FunctionDeterminism.Unknown, summary.Determinism);
        Assert.False(summary.CanEliminateUnusedCall);
        Assert.False(summary.CanReuseCallResult);
    }

    [Fact]
    public void DeadCodeElimination_EffectfulCallee_PreservesUnusedCall()
    {
        var callee = CreateIdentityFunction("effectful", new SymbolId(102));
        var caller = CreateUnusedCallFunction(callee);
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var summaries = CreateSummaries(
            callee,
            new EffectRow([new EffectTag(new SymbolId(501), "IO")]));
        var optimizer = new MirOptimizer(effectSummaries: summaries);
        optimizer.RegisterPass(new DeadCodeElimination());

        var optimized = optimizer.Optimize(module);

        Assert.IsType<MirCall>(Assert.Single(optimized.Functions[1].BasicBlocks.Single().Instructions));
    }

    [Fact]
    public void DeadCodeElimination_TrustedPureCallee_RemovesUnusedCall()
    {
        var callee = CreateIdentityFunction("pure", new SymbolId(103));
        var caller = CreateUnusedCallFunction(callee);
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(callee, EffectRow.Pure));
        optimizer.RegisterPass(new DeadCodeElimination());

        var optimized = optimizer.Optimize(module);

        Assert.Empty(optimized.Functions[1].BasicBlocks.Single().Instructions);
    }

    [Fact]
    public void CommonSubexpressionElimination_TrustedPureCallee_ReusesResult()
    {
        var callee = CreateIdentityFunction("pure", new SymbolId(104));
        var argument = Local(1, "argument", isParameter: true);
        var first = Local(2, "first");
        var second = Local(3, "second");
        var functionRef = CreateFunctionRef(callee);
        var caller = new MirFunc
        {
            Name = "caller",
            Locals = [argument, first, second],
            EntryBlockId = Block(1),
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions =
                    [
                        Call(first.Id, functionRef, Place(argument.Id)),
                        Call(second.Id, functionRef, Place(argument.Id))
                    ],
                    Terminator = new MirReturn { Value = Place(second.Id) }
                }
            ]
        };
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(callee, EffectRow.Pure));
        optimizer.RegisterPass(new CommonSubexpressionElimination());

        var optimized = optimizer.Optimize(module);

        var instructions = optimized.Functions[1].BasicBlocks.Single().Instructions;
        Assert.IsType<MirCall>(instructions[0]);
        var replacement = Assert.IsType<MirAssign>(instructions[1]);
        Assert.Equal(first.Id, Assert.IsType<MirPlace>(replacement.Source).Local);
        Assert.Equal(second.Id, replacement.Target.Local);
    }

    [Fact]
    public void CommonSubexpressionElimination_UntrustedCallee_DoesNotReuseResult()
    {
        var callee = CreateIdentityFunction("unknown", new SymbolId(105));
        var argument = Local(1, "argument", isParameter: true);
        var first = Local(2, "first");
        var second = Local(3, "second");
        var functionRef = CreateFunctionRef(callee);
        var caller = new MirFunc
        {
            Name = "caller",
            Locals = [argument, first, second],
            EntryBlockId = Block(1),
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions =
                    [
                        Call(first.Id, functionRef, Place(argument.Id)),
                        Call(second.Id, functionRef, Place(argument.Id))
                    ],
                    Terminator = new MirReturn { Value = Place(second.Id) }
                }
            ]
        };
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var optimizer = new MirOptimizer();
        optimizer.RegisterPass(new CommonSubexpressionElimination());

        var optimized = optimizer.Optimize(module);

        Assert.All(
            optimized.Functions[1].BasicBlocks.Single().Instructions,
            instruction => Assert.IsType<MirCall>(instruction));
    }

    [Fact]
    public void LoopInvariantCodeMotion_TrustedPureCallee_HoistsFromNaturalLoopHeader()
    {
        var callee = CreateIdentityFunction("pure", new SymbolId(106));
        var result = Local(1, "result");
        var caller = new MirFunc
        {
            Name = "caller",
            Locals = [result],
            EntryBlockId = Block(1),
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
                    Instructions =
                    [
                        Call(
                            result.Id,
                            CreateFunctionRef(callee),
                            new MirConstant
                            {
                                TypeId = IntType,
                                Value = new MirConstantValue.IntValue(7)
                            })
                    ],
                    Terminator = new MirGoto { Target = Block(3) }
                },
                new MirBasicBlock
                {
                    Id = Block(3),
                    Terminator = new MirGoto { Target = Block(2) }
                }
            ]
        };
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var optimizer = new MirOptimizer(effectSummaries: CreateSummaries(callee, EffectRow.Pure));
        optimizer.RegisterPass(new LoopInvariantCodeMotion());

        var optimized = optimizer.Optimize(module);

        var blocks = optimized.Functions[1].BasicBlocks;
        Assert.IsType<MirCall>(Assert.Single(blocks[0].Instructions));
        Assert.Empty(blocks[1].Instructions);
    }

    [Fact]
    public void LoopInvariantCodeMotion_EffectfulCallee_RemainsInsideLoop()
    {
        var callee = CreateIdentityFunction("effectful", new SymbolId(107));
        var result = Local(1, "result");
        var caller = CreateSingleBlockLoop(callee, result);
        var summaries = CreateSummaries(
            callee,
            new EffectRow([new EffectTag(new SymbolId(502), "Clock")]));
        var module = new MirModule { Name = "Main", Functions = [callee, caller] };
        var optimizer = new MirOptimizer(effectSummaries: summaries);
        optimizer.RegisterPass(new LoopInvariantCodeMotion());

        var optimized = optimizer.Optimize(module);

        var blocks = optimized.Functions[1].BasicBlocks;
        Assert.Empty(blocks[0].Instructions);
        Assert.IsType<MirCall>(Assert.Single(blocks[1].Instructions));
    }

    private static MirFunc CreateSingleBlockLoop(MirFunc callee, MirLocal result) => new()
    {
        Name = "caller",
        Locals = [result],
        EntryBlockId = Block(1),
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
                Instructions =
                [
                    Call(
                        result.Id,
                        CreateFunctionRef(callee),
                        new MirConstant
                        {
                            TypeId = IntType,
                            Value = new MirConstantValue.IntValue(7)
                        })
                ],
                Terminator = new MirGoto { Target = Block(2) }
            }
        ]
    };

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

    private static MirFunc CreateUnusedCallFunction(MirFunc callee)
    {
        var argument = Local(1, "argument", isParameter: true);
        var result = Local(2, "result");
        return new MirFunc
        {
            Name = "caller",
            Locals = [argument, result],
            EntryBlockId = Block(1),
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions = [Call(result.Id, CreateFunctionRef(callee), Place(argument.Id))],
                    Terminator = new MirReturn()
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
