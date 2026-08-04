using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class RuntimeSequenceStoragePromotionPassTests
{
    private static readonly TypeId ArrayType = new(9200);
    private static readonly TypeId IntType = new(BaseTypes.IntId);

    [Fact]
    public void Run_ConstantNonEscapingArray_PromotesLocalStorage()
    {
        var fixture = CreateFixture(Capacity(4));
        var pass = new RuntimeSequenceStoragePromotionPass();

        var result = pass.Run(fixture.Module);

        Assert.NotSame(fixture.Module, result);
        var storage = Assert.Single(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
        Assert.Equal(fixture.ArrayLocal, storage.ArrayLocal);
        Assert.Equal(4, storage.Capacity);
        Assert.Equal(8, storage.ElementSize);
        Assert.Equal(96, storage.StorageBytes);
        Assert.True(storage.PromoteInline);
        Assert.Equal(1, pass.StoragesPromoted);
        Assert.Equal(1, pass.GetMetricsSnapshot()["sequence.collectors_stack_promoted"]);
    }

    [Fact]
    public void Run_ReturnEscape_KeepsHeapStorage()
    {
        var fixture = CreateFixture(Capacity(4), returnsArray: true);

        var result = new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        Assert.Same(fixture.Module, result);
        Assert.Empty(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
    }

    [Fact]
    public void Run_CopyAlias_KeepsHeapStorage()
    {
        var fixture = CreateFixture(Capacity(4));
        var copyLocal = new LocalId { Value = 3 };
        fixture.Function.Locals.Add(new MirLocal { Id = copyLocal, Name = "copy", TypeId = ArrayType });
        fixture.Block.Instructions.Add(new MirCopy
        {
            Target = Place(copyLocal, ArrayType),
            Source = Place(fixture.ArrayLocal, ArrayType)
        });

        new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        Assert.Empty(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
    }

    [Fact]
    public void Run_SharedAssignmentAlias_KeepsHeapStorage()
    {
        var fixture = CreateFixture(Capacity(4));
        var aliasLocal = new LocalId { Value = 3 };
        fixture.Function.Locals.Add(new MirLocal { Id = aliasLocal, Name = "alias", TypeId = ArrayType });
        fixture.Block.Instructions.Add(new MirAssign
        {
            Target = Place(aliasLocal, ArrayType),
            Source = Place(fixture.ArrayLocal, ArrayType)
        });

        new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        Assert.Empty(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
    }

    [Fact]
    public void Run_NonAliasAssignmentOverwritesCandidate_KeepsHeapStorage()
    {
        var fixture = CreateFixture(Capacity(4));
        var replacementLocal = new LocalId { Value = 3 };
        fixture.Function.Locals.Add(new MirLocal
        {
            Id = replacementLocal,
            Name = "replacement",
            TypeId = ArrayType
        });
        fixture.Block.Instructions.Add(new MirAssign
        {
            Target = Place(fixture.ArrayLocal, ArrayType),
            Source = Place(replacementLocal, ArrayType)
        });

        new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        Assert.Empty(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
    }

    [Fact]
    public void Run_DirectStoreOverwritesCandidate_KeepsHeapStorage()
    {
        var fixture = CreateFixture(Capacity(4));
        var replacementLocal = new LocalId { Value = 3 };
        fixture.Function.Locals.Add(new MirLocal
        {
            Id = replacementLocal,
            Name = "replacement",
            TypeId = ArrayType
        });
        fixture.Block.Instructions.Add(new MirStore
        {
            Target = Place(fixture.ArrayLocal, ArrayType),
            Value = Place(replacementLocal, ArrayType)
        });

        new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        Assert.Empty(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
    }

    [Fact]
    public void Run_BorrowAlias_KeepsHeapStorage()
    {
        var fixture = CreateFixture(Capacity(4));
        var borrowLocal = new LocalId { Value = 3 };
        fixture.Function.Locals.Add(new MirLocal { Id = borrowLocal, Name = "borrow", TypeId = ArrayType });
        fixture.Block.Instructions.Add(new MirLoad
        {
            Target = Place(borrowLocal, ArrayType),
            Source = Place(fixture.ArrayLocal, ArrayType),
            CreatesBorrowAlias = true
        });

        new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        Assert.Empty(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
    }

    [Fact]
    public void Run_ReusedAllocationLocal_KeepsHeapStorage()
    {
        var fixture = CreateFixture(Capacity(4));
        fixture.Block.Instructions.Add(new MirCall
        {
            Target = Place(fixture.ArrayLocal, ArrayType),
            Function = MirRuntimeFunctions.CreateFunctionRef(
                WellKnownStrings.InternalNames.ArrayNew,
                ArrayType,
                default),
            Arguments = [Capacity(4), Capacity(8)]
        });

        new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        Assert.Empty(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
    }

    [Fact]
    public void Run_UnknownCallEscape_KeepsHeapStorage()
    {
        var fixture = CreateFixture(Capacity(4));
        fixture.Block.Instructions.Add(new MirCall
        {
            Function = new MirFunctionRef
            {
                Name = "retain_unknown",
                SymbolId = new SymbolId(991),
                TypeId = IntType
            },
            Arguments = [Place(fixture.ArrayLocal, ArrayType)]
        });

        new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        Assert.Empty(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_DynamicOrOversizedCapacity_KeepsHeapStorage(bool oversized)
    {
        var fixture = CreateFixture(oversized ? Capacity(1000) : Place(new LocalId { Value = 2 }, IntType));

        var result = new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        Assert.Same(fixture.Module, result);
        Assert.Empty(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
    }

    [Fact]
    public void Run_KnownInlineCapacity_AllowsRuntimeHeapGrowthFallback()
    {
        var fixture = CreateFixture(Capacity(2));
        for (var value = 1; value <= 3; value++)
        {
            fixture.Block.Instructions.Add(new MirCall
            {
                Target = Place(fixture.ArrayLocal, ArrayType),
                Function = MirRuntimeFunctions.CreateFunctionRef(
                    WellKnownStrings.InternalNames.ArrayPush,
                    ArrayType,
                    default),
                Arguments =
                [
                    Place(fixture.ArrayLocal, ArrayType),
                    Capacity(value),
                    Capacity(8)
                ]
            });
        }

        new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        var storage = Assert.Single(fixture.Function.CallerOwnedAggregateAbi.LocalArrayStorages);
        Assert.Equal(2, storage.Capacity);
        Assert.Equal(80, storage.StorageBytes);
        Assert.True(storage.PromoteInline);
    }

    [Fact]
    public void Run_NoCandidate_PreservesModuleIdentity()
    {
        var fixture = CreateFixture(Place(new LocalId { Value = 2 }, IntType));

        var result = new RuntimeSequenceStoragePromotionPass().Run(fixture.Module);

        Assert.Same(fixture.Module, result);
    }

    private static Fixture CreateFixture(MirOperand capacity, bool returnsArray = false)
    {
        var arrayLocal = new LocalId { Value = 1 };
        var capacityLocal = new LocalId { Value = 2 };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = Place(arrayLocal, ArrayType),
                    Function = MirRuntimeFunctions.CreateFunctionRef(
                        WellKnownStrings.InternalNames.ArrayNew,
                        ArrayType,
                        default),
                    Arguments = [capacity, Capacity(8)]
                }
            ],
            Terminator = new MirReturn
            {
                Value = returnsArray ? Place(arrayLocal, ArrayType) : Capacity(0)
            }
        };
        var function = new MirFunc
        {
            Name = "local_array",
            FunctionId = new FunctionId
            {
                SymbolId = new SymbolId(990),
                Name = "local_array",
                QualifiedName = "Test.local_array"
            },
            ReturnType = returnsArray ? ArrayType : IntType,
            EntryBlockId = block.Id,
            Locals =
            [
                new MirLocal { Id = arrayLocal, Name = "array", TypeId = ArrayType },
                new MirLocal { Id = capacityLocal, Name = "capacity", TypeId = IntType }
            ],
            BasicBlocks = [block]
        };
        return new Fixture(new MirModule { Functions = [function] }, function, block, arrayLocal);
    }

    private static MirPlace Place(LocalId local, TypeId typeId) => new()
    {
        Kind = PlaceKind.Local,
        Local = local,
        TypeId = typeId
    };

    private static MirConstant Capacity(long value) => new()
    {
        Value = new MirConstantValue.IntValue(value),
        TypeId = IntType
    };

    private sealed record Fixture(
        MirModule Module,
        MirFunc Function,
        MirBasicBlock Block,
        LocalId ArrayLocal);
}
