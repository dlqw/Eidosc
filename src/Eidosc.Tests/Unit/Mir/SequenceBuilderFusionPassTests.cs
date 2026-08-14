using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class SequenceBuilderFusionPassTests
{
    [Fact]
    public void Run_SingleUseFreeze_ReplacesWrapperCallWithDestructiveFieldLoad()
    {
        var builderType = new TypeId(9800);
        var sequenceType = new TypeId(9801);
        var builder = Place(1, builderType);
        var result = Place(2, sequenceType);
        var function = new MirFunc
        {
            Name = "builder_freeze",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = sequenceType,
            Locals =
            [
                new MirLocal { Id = builder.Local, Name = "builder", TypeId = builderType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = sequenceType }
            ],
            BasicBlocks = [new MirBasicBlock
            {
                Id = new BlockId { Value = 1 },
                IsEntry = true,
                Instructions = [new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "std__SeqBuilder__freeze__spec_TEST",
                        SymbolId = new SymbolId(9802),
                        TypeId = sequenceType,
                        CompilerSemanticRole = CompilerSemanticRole.SequenceBuilderFreeze
                    },
                    Arguments = [builder]
                }],
                Terminator = new MirReturn { Value = result }
            }]
        };
        var module = new MirModule
        {
            Functions = [function],
            ConstructorLayouts =
            {
                [builderType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "SeqBuilder",
                        ConstructorName = "SeqBuilder",
                        FieldTypeIds = [sequenceType]
                    }
                ]
            }
        };
        var pass = new SequenceBuilderFusionPass();
        ((IOwnershipAnalysisSnapshotConsumer)pass).OwnershipSnapshots =
            OwnershipAnalysisSnapshot.BuildForOptimization(module);

        var rewritten = pass.Run(module);

        Assert.NotSame(module, rewritten);
        Assert.Equal(1, pass.FreezesElided);
        var load = Assert.IsType<MirLoad>(Assert.Single(function.BasicBlocks[0].Instructions));
        Assert.True(load.MovesOutOfSource);
        var source = Assert.IsType<MirPlace>(load.Source);
        Assert.Equal(MirIndexAccessKind.Aggregate, source.IndexAccessKind);
        Assert.Equal(builder.Local, Assert.IsType<MirPlace>(source.Base).Local);
    }

    [Fact]
    public void Run_MultiUseBuilder_PreservesFreezeCall()
    {
        var builderType = new TypeId(9810);
        var sequenceType = new TypeId(9811);
        var builder = Place(1, builderType);
        var first = Place(2, sequenceType);
        var second = Place(3, sequenceType);
        var freeze = new MirFunctionRef
        {
            Name = "std__SeqBuilder__freeze__spec_TEST",
            SymbolId = new SymbolId(9812),
            TypeId = sequenceType,
            CompilerSemanticRole = CompilerSemanticRole.SequenceBuilderFreeze
        };
        var function = new MirFunc
        {
            Name = "builder_freeze_twice",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = sequenceType,
            Locals =
            [
                new MirLocal { Id = builder.Local, Name = "builder", TypeId = builderType, IsParameter = true },
                new MirLocal { Id = first.Local, Name = "first", TypeId = sequenceType },
                new MirLocal { Id = second.Local, Name = "second", TypeId = sequenceType }
            ],
            BasicBlocks = [new MirBasicBlock
            {
                Id = new BlockId { Value = 1 },
                IsEntry = true,
                Instructions =
                [
                    new MirCall { Target = first, Function = freeze, Arguments = [builder] },
                    new MirCall { Target = second, Function = freeze, Arguments = [builder] }
                ],
                Terminator = new MirReturn { Value = second }
            }]
        };
        var module = new MirModule
        {
            Functions = [function],
            ConstructorLayouts =
            {
                [builderType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "SeqBuilder",
                        ConstructorName = "SeqBuilder",
                        FieldTypeIds = [sequenceType]
                    }
                ]
            }
        };
        var pass = new SequenceBuilderFusionPass();
        ((IOwnershipAnalysisSnapshotConsumer)pass).OwnershipSnapshots =
            OwnershipAnalysisSnapshot.BuildForOptimization(module);

        var rewritten = pass.Run(module);

        Assert.Same(module, rewritten);
        Assert.Equal(0, pass.FreezesElided);
        Assert.All(function.BasicBlocks[0].Instructions, instruction => Assert.IsType<MirCall>(instruction));
    }

    [Fact]
    public void Run_FreezeLikeNameWithoutCompilerRole_PreservesCall()
    {
        var builderType = new TypeId(9820);
        var sequenceType = new TypeId(9821);
        var builder = Place(1, builderType);
        var result = Place(2, sequenceType);
        var call = new MirCall
        {
            Target = result,
            Function = new MirFunctionRef
            {
                Name = "user__SeqBuilder__freeze__spec_FAKE",
                SymbolId = new SymbolId(9822),
                TypeId = sequenceType
            },
            Arguments = [builder]
        };
        var function = new MirFunc
        {
            Name = "fake_builder_freeze",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = sequenceType,
            Locals =
            [
                new MirLocal { Id = builder.Local, Name = "builder", TypeId = builderType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = sequenceType }
            ],
            BasicBlocks = [new MirBasicBlock
            {
                Id = new BlockId { Value = 1 },
                IsEntry = true,
                Instructions = [call],
                Terminator = new MirReturn { Value = result }
            }]
        };
        var module = new MirModule
        {
            Functions = [function],
            ConstructorLayouts =
            {
                [builderType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "SeqBuilder",
                        ConstructorName = "SeqBuilder",
                        FieldTypeIds = [sequenceType]
                    }
                ]
            }
        };
        var pass = new SequenceBuilderFusionPass();
        ((IOwnershipAnalysisSnapshotConsumer)pass).OwnershipSnapshots =
            OwnershipAnalysisSnapshot.BuildForOptimization(module);

        var rewritten = pass.Run(module);

        Assert.Same(module, rewritten);
        Assert.Same(call, Assert.Single(function.BasicBlocks[0].Instructions));
    }

    private static MirPlace Place(int id, TypeId typeId) => new()
    {
        Kind = PlaceKind.Local,
        Local = new LocalId { Value = id },
        TypeId = typeId
    };
}
