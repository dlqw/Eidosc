using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class SequencePipelineFusionPassTests
{
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

    private static MirLocal Decl(MirPlace place, string name) => new()
    {
        Id = place.Local,
        Name = name,
        TypeId = place.TypeId
    };

    private static MirConstant Constant(long value) => new()
    {
        Value = new MirConstantValue.IntValue(value),
        TypeId = new TypeId(BaseTypes.IntId)
    };
}
