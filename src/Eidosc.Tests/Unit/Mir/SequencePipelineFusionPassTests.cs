using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class SequencePipelineFusionPassTests
{
    [Fact]
    public void Run_DirectFoldOverCopyValues_LowersToSingleLoop()
    {
        var sequenceType = new TypeId(7001);
        var intType = new TypeId(BaseTypes.IntId);
        var source = Local(1, sequenceType);
        var folded = Local(2, intType);
        var reducer = Function("reducer");
        var reducerLeft = Local(10, intType);
        var reducerRight = Local(11, intType);
        var reducerResult = Local(12, intType);

        var function = new MirFunc
        {
            Name = "main",
            ReturnType = intType,
            Locals = [Decl(source, "source"), Decl(folded, "folded")],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        RoleCall(
                            folded,
                            CompilerSemanticRole.SequenceFoldLeft,
                            source,
                            Constant(0),
                            reducer)
                    ],
                    Terminator = new MirReturn { Value = folded }
                }
            ]
        };
        var reducerFunction = new MirFunc
        {
            Name = "reducer",
            FunctionId = reducer.FunctionId,
            ReturnType = intType,
            Locals =
            [
                Decl(reducerLeft, "left", isParameter: true),
                Decl(reducerRight, "right", isParameter: true),
                Decl(reducerResult, "result")
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirBinOp
                        {
                            Target = reducerResult,
                            Operator = BinaryOp.Add,
                            Left = reducerLeft,
                            Right = reducerRight
                        }
                    ],
                    Terminator = new MirReturn { Value = reducerResult }
                }
            ]
        };
        var module = new MirModule { Name = "direct_fold", Functions = [function, reducerFunction] };
        var pass = new SequencePipelineFusionPass();

        var result = pass.Run(module);

        Assert.NotSame(module, result);
        Assert.Equal(1, pass.Stats.DirectFoldsLowered);
        Assert.Equal(0, pass.Stats.PipelinesFormed);
        Assert.DoesNotContain(
            function.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(),
            static call => call.Function is MirFunctionRef
            {
                CompilerSemanticRole: CompilerSemanticRole.SequenceFoldLeft
            });
        Assert.Contains(function.Locals, static local => local.Name == "__sequence_fold_index");
    }

    [Fact]
    public void Run_DirectFoldWithNonCopyAccumulator_FallsBackWithoutMutation()
    {
        var sequenceType = new TypeId(7001);
        var aggregateType = new TypeId(7002);
        var intType = new TypeId(BaseTypes.IntId);
        var source = Local(1, sequenceType);
        var initial = Local(2, aggregateType);
        var folded = Local(3, aggregateType);
        var reducer = Function("aggregate_reducer");
        var reducerAccumulator = Local(10, aggregateType);
        var reducerElement = Local(11, intType);

        var function = new MirFunc
        {
            Name = "main",
            ReturnType = aggregateType,
            Locals =
            [
                Decl(source, "source"),
                Decl(initial, "initial"),
                Decl(folded, "folded")
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        RoleCall(
                            folded,
                            CompilerSemanticRole.SequenceFoldLeft,
                            source,
                            initial,
                            reducer)
                    ],
                    Terminator = new MirReturn { Value = folded }
                }
            ]
        };
        var reducerFunction = new MirFunc
        {
            Name = "aggregate_reducer",
            FunctionId = reducer.FunctionId,
            ReturnType = aggregateType,
            Locals =
            [
                Decl(reducerAccumulator, "accumulator", isParameter: true),
                Decl(reducerElement, "element", isParameter: true)
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = reducerAccumulator }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "direct_fold_non_copy",
            Functions = [function, reducerFunction]
        };
        var pass = new SequencePipelineFusionPass();

        var result = pass.Run(module);

        Assert.Same(module, result);
        Assert.Equal(0, pass.Stats.DirectFoldsLowered);
        Assert.Equal(1, pass.Stats.FallbackOwnership);
        Assert.Single(function.BasicBlocks[0].Instructions);
    }

    [Fact]
    public void Run_MapIntermediateWithSecondReader_FallsBackWithoutMutation()
    {
        var sequenceType = new TypeId(7001);
        var intType = new TypeId(BaseTypes.IntId);
        var source = Local(1, sequenceType);
        var mapped = Local(2, sequenceType);
        var filtered = Local(3, sequenceType);
        var folded = Local(4, intType);
        var secondReader = Local(5, sequenceType);
        var mapper = Function("mapper");
        var predicate = Function("predicate");
        var reducer = Function("reducer");

        var function = new MirFunc
        {
            Name = "main",
            ReturnType = intType,
            Locals =
            [
                Decl(source, "source"),
                Decl(mapped, "mapped"),
                Decl(filtered, "filtered"),
                Decl(folded, "folded"),
                Decl(secondReader, "second_reader")
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        RoleCall(mapped, CompilerSemanticRole.SequenceMap, source, mapper),
                        RoleCall(filtered, CompilerSemanticRole.SequenceFilter, mapped, predicate),
                        RoleCall(
                            folded,
                            CompilerSemanticRole.SequenceFoldLeft,
                            filtered,
                            Constant(0),
                            reducer),
                        new MirCopy { Target = secondReader, Source = mapped }
                    ],
                    Terminator = new MirReturn { Value = folded }
                }
            ]
        };
        var module = new MirModule { Name = "multi_use", Functions = [function] };
        var pass = new SequencePipelineFusionPass();

        var result = pass.Run(module);

        Assert.Same(module, result);
        Assert.Equal(0, pass.Stats.PipelinesFormed);
        Assert.Equal(1, pass.Stats.FallbackMultiUse);
        Assert.Equal(4, function.BasicBlocks[0].Instructions.Count);
    }

    private static MirCall RoleCall(
        MirPlace target,
        CompilerSemanticRole role,
        params MirOperand[] arguments) => new()
    {
        Target = target,
        Function = Function(role.ToString()) with { CompilerSemanticRole = role },
        Arguments = [.. arguments]
    };

    private static MirFunctionRef Function(string name) => new()
    {
        Name = name,
        SymbolKind = SymbolKind.Function,
        FunctionId = new FunctionId
        {
            Name = name,
            QualifiedName = $"test:{name}",
            Module = "test",
            Kind = SymbolKind.Function
        }
    };

    private static MirPlace Local(int id, TypeId type) => new()
    {
        Kind = PlaceKind.Local,
        Local = new LocalId { Value = id },
        TypeId = type
    };

    private static MirLocal Decl(MirPlace place, string name, bool isParameter = false) => new()
    {
        Id = place.Local,
        Name = name,
        TypeId = place.TypeId,
        IsParameter = isParameter
    };

    private static MirConstant Constant(long value) => new()
    {
        Value = new MirConstantValue.IntValue(value),
        TypeId = new TypeId(BaseTypes.IntId)
    };
}
