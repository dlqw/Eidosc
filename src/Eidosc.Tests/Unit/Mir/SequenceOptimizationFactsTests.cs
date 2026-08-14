using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class SequenceOptimizationFactsTests
{
    [Fact]
    public void Analyze_KnownSequenceConsumersKeepIntermediateNonEscaping()
    {
        var sequenceType = new TypeId(7200);
        var mappedType = new TypeId(7201);
        var resultType = new TypeId(BaseTypes.IntId);
        var source = Local(1, sequenceType);
        var mapped = Local(2, sequenceType);
        var result = Local(3, resultType);
        var mapper = Function("mapper");
        var reducer = Function("reducer");
        var function = new MirFunc
        {
            Name = "facts",
            ReturnType = resultType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [Decl(source), Decl(mapped), Decl(result)],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        RoleCall(mapped, CompilerSemanticRole.SequenceMap, source, mapper),
                        RoleCall(result, CompilerSemanticRole.SequenceFoldLeft, mapped, Constant(0), reducer)
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };

        var facts = SequenceOptimizationFacts.Analyze(function);

        Assert.True(facts.IsSingleUseNonEscaping(source.Local));
        Assert.True(facts.IsSingleUseNonEscaping(mapped.Local));
        Assert.True(facts.EscapedLocals.Contains(result.Local));
    }

    [Fact]
    public void Analyze_UnknownCallArgumentEscapes()
    {
        var valueType = new TypeId(7202);
        var value = Local(1, valueType);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Function = Function("unknown"),
                    Arguments = [value]
                }
            ],
            Terminator = new MirReturn()
        };
        var function = new MirFunc
        {
            Name = "unknown_escape",
            EntryBlockId = block.Id,
            Locals = [Decl(value)],
            BasicBlocks = [block]
        };

        var facts = SequenceOptimizationFacts.Analyze(function);

        Assert.True(facts.EscapedLocals.Contains(value.Local));
        Assert.False(facts.IsSingleUseNonEscaping(value.Local));
    }

    [Fact]
    public void Analyze_LocalStoreTargetIsDefinitionNotRead()
    {
        var valueType = new TypeId(7204);
        var target = Local(1, valueType);
        var value = Local(2, valueType);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirStore { Target = target, Value = value }
            ],
            Terminator = new MirReturn()
        };
        var function = new MirFunc
        {
            Name = "local_store",
            EntryBlockId = block.Id,
            Locals = [Decl(target), Decl(value)],
            BasicBlocks = [block]
        };

        var facts = SequenceOptimizationFacts.Analyze(function);

        Assert.False(facts.IsSingleRead(target.Local));
        Assert.True(facts.IsSingleRead(value.Local));
    }

    [Fact]
    public void Analyze_ProjectionStoreCountsOnlyAddressDependencies()
    {
        var aggregateType = new TypeId(7205);
        var valueType = new TypeId(7206);
        var basePlace = Local(1, aggregateType);
        var indexPlace = Local(2, new TypeId(BaseTypes.IntId));
        var value = Local(3, valueType);
        var target = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = new MirPlace
            {
                Kind = PlaceKind.Field,
                Base = basePlace,
                FieldName = "items",
                TypeId = aggregateType
            },
            Index = indexPlace,
            TypeId = valueType
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirStore { Target = target, Value = value }
            ],
            Terminator = new MirReturn()
        };
        var function = new MirFunc
        {
            Name = "projection_store",
            EntryBlockId = block.Id,
            Locals = [Decl(basePlace), Decl(indexPlace), Decl(value)],
            BasicBlocks = [block]
        };

        var facts = SequenceOptimizationFacts.Analyze(function);

        Assert.True(facts.IsSingleRead(basePlace.Local));
        Assert.True(facts.IsSingleRead(indexPlace.Local));
        Assert.True(facts.IsSingleRead(value.Local));
    }

    [Fact]
    public void Analyze_CopyMarksBothLocalsAsAliased()
    {
        var valueType = new TypeId(7207);
        var source = Local(1, valueType);
        var copy = Local(2, valueType);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions = [new MirCopy { Target = copy, Source = source }],
            Terminator = new MirReturn { Value = copy }
        };
        var function = new MirFunc
        {
            Name = "copy_alias",
            EntryBlockId = block.Id,
            Locals = [Decl(source), Decl(copy)],
            BasicBlocks = [block]
        };

        var facts = SequenceOptimizationFacts.Analyze(function);

        Assert.Contains(source.Local, facts.AliasedLocals);
        Assert.Contains(copy.Local, facts.AliasedLocals);
        Assert.False(facts.IsSingleUseNonEscaping(source.Local));
    }

    [Fact]
    public void Analyze_BorrowLoadMarksSourceAndBorrowLocal()
    {
        var valueType = new TypeId(7208);
        var source = Local(1, valueType);
        var borrowed = Local(2, valueType);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirLoad
                {
                    Target = borrowed,
                    Source = source,
                    CreatesBorrowAlias = true
                }
            ],
            Terminator = new MirReturn { Value = borrowed }
        };
        var function = new MirFunc
        {
            Name = "borrow_alias",
            EntryBlockId = block.Id,
            Locals = [Decl(source), Decl(borrowed)],
            BasicBlocks = [block]
        };

        var facts = SequenceOptimizationFacts.Analyze(function);

        Assert.Contains(source.Local, facts.BorrowedLocals);
        Assert.Contains(borrowed.Local, facts.BorrowedLocals);
        Assert.False(facts.IsSingleUseNonEscaping(source.Local));
    }

    [Fact]
    public void Analyze_BorrowedCallArgumentMarksBorrowedRoot()
    {
        var valueType = new TypeId(7209);
        var source = Local(1, valueType);
        var target = Local(2, new TypeId(BaseTypes.UnitId));
        var consumer = Function("consumer");
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = target,
                    Function = consumer,
                    Arguments = [source],
                    BorrowedArgumentIndices = new HashSet<int> { 0 }
                }
            ],
            Terminator = new MirReturn()
        };
        var function = new MirFunc
        {
            Name = "borrowed_call",
            EntryBlockId = block.Id,
            Locals = [Decl(source), Decl(target)],
            BasicBlocks = [block]
        };

        var facts = SequenceOptimizationFacts.Analyze(function);

        Assert.Contains(source.Local, facts.BorrowedLocals);
        Assert.DoesNotContain(source.Local, facts.EscapedLocals);
        Assert.False(facts.IsSingleUseNonEscaping(source.Local));
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

    private static MirLocal Decl(MirPlace place) => new()
    {
        Id = place.Local,
        Name = $"local_{place.Local.Value}",
        TypeId = place.TypeId
    };

    private static MirConstant Constant(long value) => new()
    {
        TypeId = new TypeId(BaseTypes.IntId),
        Value = new MirConstantValue.IntValue(value)
    };
}
