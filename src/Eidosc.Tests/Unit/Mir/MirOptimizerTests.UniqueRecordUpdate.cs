using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirOptimizerTests
{
    [Fact]
    public void ReadOnlyProjectionFusion_NestedRead_RemovesTransientManagedOwner()
    {
        var recordType = new TypeId(7088);
        var sequenceType = new TypeId(7089);
        var intType = new TypeId(BaseTypes.IntId);
        var owner = new MirLocal { Id = new LocalId { Value = 1 }, Name = "owner", TypeId = recordType, IsParameter = true };
        var sequence = new MirLocal { Id = new LocalId { Value = 2 }, Name = "sequence", TypeId = sequenceType };
        var item = new MirLocal { Id = new LocalId { Value = 3 }, Name = "item", TypeId = intType };
        var ownerPlace = LocalPlace(owner.Id, recordType);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirLoad
                {
                    Target = LocalPlace(sequence.Id, sequenceType),
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = ownerPlace,
                        FieldName = "_0",
                        TypeId = sequenceType
                    }
                },
                new MirLoad
                {
                    Target = LocalPlace(item.Id, intType),
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = LocalPlace(sequence.Id, sequenceType),
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                        TypeId = intType
                    }
                },
                new MirDrop { Value = LocalPlace(sequence.Id, sequenceType) }
            ],
            Terminator = new MirReturn { Value = LocalPlace(item.Id, intType) }
        };
        var function = new MirFunc
        {
            Name = "read_head",
            Locals = [owner, sequence, item],
            EntryBlockId = block.Id,
            BasicBlocks = [block],
            ReturnType = intType
        };
        var module = new MirModule { Functions = [function] };

        new ReadOnlyProjectionFusionPass().Run(module);

        var fused = Assert.IsType<MirLoad>(Assert.Single(block.Instructions));
        var index = Assert.IsType<MirPlace>(fused.Source);
        var field = Assert.IsType<MirPlace>(index.Base);
        Assert.Equal(PlaceKind.Field, field.Kind);
        Assert.Equal(owner.Id, field.Base!.Local);
        Assert.DoesNotContain(block.Instructions, instruction => instruction is MirDrop);
    }

    [Fact]
    public void UniqueRecordUpdate_DirectUniqueCall_CreatesSpecializedVariant()
    {
        var recordType = new TypeId(7090);
        var intType = new TypeId(BaseTypes.IntId);
        var identity = new FunctionId
        {
            StableIdentityKey = "test:advance",
            Name = "advance",
            QualifiedName = "test.advance"
        };
        var source = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "source",
            TypeId = recordType,
            IsParameter = true
        };
        var updated = new MirLocal
        {
            Id = new LocalId { Value = 2 },
            Name = "updated",
            TypeId = recordType
        };
        var advance = new MirFunc
        {
            Name = "advance",
            SourceName = "advance",
            FunctionId = identity,
            Locals = [source, updated],
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = recordType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(updated.Id, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = "Record",
                                SymbolKind = SymbolKind.Constructor,
                                TypeId = recordType
                            },
                            Arguments =
                            [
                                LocalPlace(source.Id, recordType),
                                new MirConstant
                                {
                                    TypeId = intType,
                                    Value = new MirConstantValue.IntValue(1)
                                }
                            ],
                            RecordUpdate = new MirRecordUpdateInfo
                            {
                                Source = LocalPlace(source.Id, recordType),
                                UpdatedFieldIndices = [0]
                            }
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(updated.Id, recordType) }
                }
            ]
        };

        var created = new MirLocal { Id = new LocalId { Value = 10 }, Name = "created", TypeId = recordType };
        var result = new MirLocal { Id = new LocalId { Value = 11 }, Name = "result", TypeId = recordType };
        var caller = new MirFunc
        {
            Name = "main",
            IsEntry = true,
            Locals = [created, result],
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = recordType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(created.Id, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = "Record",
                                SymbolKind = SymbolKind.Constructor,
                                TypeId = recordType
                            },
                            Arguments =
                            [
                                new MirConstant
                                {
                                    TypeId = intType,
                                    Value = new MirConstantValue.IntValue(0)
                                }
                            ]
                        },
                        new MirCall
                        {
                            Target = LocalPlace(result.Id, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = advance.Name,
                                FunctionId = identity,
                                TypeId = recordType
                            },
                            Arguments = [LocalPlace(created.Id, recordType)]
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(result.Id, recordType) }
                }
            ]
        };
        var module = new MirModule { Functions = [advance, caller] };

        new UniqueRecordUpdateSpecializationPass().Run(module);

        var specialized = Assert.Single(module.Functions, function => function.Name.Contains("__unique_0"));
        var specializedUpdate = Assert.IsType<MirCall>(Assert.Single(specialized.BasicBlocks[0].Instructions));
        Assert.True(specializedUpdate.RecordUpdate!.IsKnownUnique);
        var rewrittenCall = Assert.IsType<MirCall>(caller.BasicBlocks[0].Instructions[1]);
        Assert.Equal(specialized.FunctionId.StableIdentityKey,
            Assert.IsType<MirFunctionRef>(rewrittenCall.Function).FunctionId.StableIdentityKey);
        Assert.False(Assert.IsType<MirCall>(advance.BasicBlocks[0].Instructions[0]).RecordUpdate!.IsKnownUnique);
    }

    [Fact]
    public void UniqueRecordUpdate_SharedCopyRemainsLive_KeepsGeneralCowCall()
    {
        var recordType = new TypeId(7091);
        var identity = new FunctionId
        {
            StableIdentityKey = "test:shared-advance",
            Name = "advance",
            QualifiedName = "test.shared_advance"
        };
        var source = new MirLocal { Id = new LocalId { Value = 1 }, Name = "source", TypeId = recordType, IsParameter = true };
        var updated = new MirLocal { Id = new LocalId { Value = 2 }, Name = "updated", TypeId = recordType };
        var advance = new MirFunc
        {
            Name = "advance",
            FunctionId = identity,
            Locals = [source, updated],
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = recordType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(updated.Id, recordType),
                            Function = new MirFunctionRef { Name = "Record", SymbolKind = SymbolKind.Constructor },
                            Arguments = [LocalPlace(source.Id, recordType)],
                            RecordUpdate = new MirRecordUpdateInfo
                            {
                                Source = LocalPlace(source.Id, recordType),
                                UpdatedFieldIndices = [0]
                            }
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(updated.Id, recordType) }
                }
            ]
        };
        var created = new MirLocal { Id = new LocalId { Value = 10 }, Name = "created", TypeId = recordType };
        var shared = new MirLocal { Id = new LocalId { Value = 11 }, Name = "shared", TypeId = recordType };
        var result = new MirLocal { Id = new LocalId { Value = 12 }, Name = "result", TypeId = recordType };
        var caller = new MirFunc
        {
            Name = "caller",
            Locals = [created, shared, result],
            EntryBlockId = new BlockId { Value = 1 },
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(created.Id, recordType),
                            Function = new MirFunctionRef { Name = "Record", SymbolKind = SymbolKind.Constructor }
                        },
                        new MirCopy
                        {
                            Target = LocalPlace(shared.Id, recordType),
                            Source = LocalPlace(created.Id, recordType)
                        },
                        new MirCall
                        {
                            Target = LocalPlace(result.Id, recordType),
                            Function = new MirFunctionRef { Name = advance.Name, FunctionId = identity },
                            Arguments = [LocalPlace(created.Id, recordType)]
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(result.Id, recordType) }
                }
            ]
        };
        var module = new MirModule { Functions = [advance, caller] };

        new UniqueRecordUpdateSpecializationPass().Run(module);

        Assert.Equal(2, module.Functions.Count);
        Assert.False(Assert.IsType<MirCall>(advance.BasicBlocks[0].Instructions[0]).RecordUpdate!.IsKnownUnique);
        Assert.Equal(identity.StableIdentityKey,
            Assert.IsType<MirFunctionRef>(Assert.IsType<MirCall>(caller.BasicBlocks[0].Instructions[2]).Function)
                .FunctionId.StableIdentityKey);
    }
}
