using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirOptimizerTests
{
    [Fact]
    public void CallerOwnedAggregate_DirectConstructorResult_CreatesOutVariantAndCallerStorageGroup()
    {
        var fixture = CreateCallerOwnedAggregateFixture();

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        var variant = Assert.Single(fixture.Module.Functions, function => function.Name == "make_record__out");
        Assert.True(variant.CallerOwnedAggregateAbi.HasOutReturn);
        Assert.Contains(fixture.ReturnLocal, variant.CallerOwnedAggregateAbi.OutReturnLocals);
        var group = Assert.Single(fixture.Caller.CallerOwnedAggregateAbi.LocalGroups);
        Assert.Equal(fixture.RecordType, group.TypeId);
        Assert.Contains(fixture.ResultLocal, group.Locals);
        var rewritten = Assert.IsType<MirCall>(fixture.Caller.BasicBlocks[0].Instructions[0]);
        Assert.Contains("|caller-out", Assert.IsType<MirFunctionRef>(rewritten.Function).FunctionId.StableIdentityKey,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CallerOwnedAggregate_Reset_DropsOldFieldsBeforeOutReinitialization()
    {
        var fixture = CreateCallerOwnedAggregateFixture(includeDropAfterCall: true);
        fixture.Caller.Locals[0] = new MirLocal
        {
            Id = fixture.ResultLocal,
            Name = "result",
            TypeId = fixture.RecordType,
            IsParameter = true
        };

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.IsType<MirDrop>(fixture.Caller.BasicBlocks[0].Instructions[0]);
        var rewritten = Assert.IsType<MirCall>(fixture.Caller.BasicBlocks[0].Instructions[1]);
        Assert.Contains("|caller-out", Assert.IsType<MirFunctionRef>(rewritten.Function).FunctionId.StableIdentityKey,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CallerOwnedAggregate_FreshAliasDrop_RemainsAfterInitialization()
    {
        var fixture = CreateCallerOwnedAggregateFixture();
        var alias = new LocalId { Value = 11 };
        fixture.Caller.Locals.Add(new MirLocal
        {
            Id = alias,
            Name = "alias",
            TypeId = fixture.RecordType
        });
        fixture.Caller.BasicBlocks[0].Instructions.AddRange(
        [
            new MirMove
            {
                Target = LocalPlace(alias, fixture.RecordType),
                Source = LocalPlace(fixture.ResultLocal, fixture.RecordType)
            },
            new MirDrop { Value = LocalPlace(alias, fixture.RecordType) }
        ]);

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.IsType<MirCall>(fixture.Caller.BasicBlocks[0].Instructions[0]);
        Assert.IsType<MirMove>(fixture.Caller.BasicBlocks[0].Instructions[1]);
        Assert.IsType<MirDrop>(fixture.Caller.BasicBlocks[0].Instructions[2]);
    }

    [Fact]
    public void CallerOwnedAggregate_CopyRecord_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedAggregateFixture();
        fixture.Module.CopyLikeTypeIds.Add(fixture.RecordType.Value);

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.Equal(2, fixture.Module.Functions.Count);
        Assert.True(fixture.Caller.CallerOwnedAggregateAbi.IsEmpty);
        Assert.Equal(fixture.Factory.FunctionId.StableIdentityKey,
            Assert.IsType<MirFunctionRef>(Assert.IsType<MirCall>(fixture.Caller.BasicBlocks[0].Instructions[0]).Function)
                .FunctionId.StableIdentityKey);
    }

    [Fact]
    public void CallerOwnedAggregate_UnknownExternalEscape_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedAggregateFixture();
        var externalIdentity = Identity("external_escape");
        var external = new MirFunc
        {
            Name = "external_escape",
            FunctionId = externalIdentity,
            IsExternal = true,
            ReturnType = new TypeId(BaseTypes.IntId),
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = fixture.RecordType,
                    IsParameter = true
                }
            ]
        };
        fixture.Module.Functions.Add(external);
        fixture.Caller.BasicBlocks[0].Instructions.Add(new MirCall
        {
            Target = LocalPlace(new LocalId { Value = 11 }, new TypeId(BaseTypes.IntId)),
            Function = new MirFunctionRef { Name = external.Name, FunctionId = externalIdentity },
            Arguments = [LocalPlace(fixture.ResultLocal, fixture.RecordType)]
        });
        fixture.Caller.Locals.Add(new MirLocal
        {
            Id = new LocalId { Value = 11 },
            Name = "external_result",
            TypeId = new TypeId(BaseTypes.IntId)
        });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name.EndsWith("__out", StringComparison.Ordinal));
        Assert.True(fixture.Caller.CallerOwnedAggregateAbi.IsEmpty);
    }

    [Fact]
    public void CallerOwnedAggregate_SharedBorrowCall_PreservesBorrowAbiAndPromotion()
    {
        var fixture = CreateCallerOwnedAggregateFixture();
        var refType = new TypeId(8121);
        fixture.Module.TypeDescriptors[refType.Value] = new TypeDescriptor.Ref(fixture.RecordType);
        var inspectIdentity = Identity("inspect");
        var inspectParameter = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "value",
            TypeId = refType,
            IsParameter = true
        };
        var inspect = new MirFunc
        {
            Name = "inspect",
            FunctionId = inspectIdentity,
            ReturnType = new TypeId(BaseTypes.IntId),
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [inspectParameter],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = IntConstant(0) }
                }
            ]
        };
        fixture.Module.Functions.Add(inspect);
        fixture.Caller.BasicBlocks[0].Instructions.Add(new MirCall
        {
            Target = LocalPlace(new LocalId { Value = 11 }, new TypeId(BaseTypes.IntId)),
            Function = new MirFunctionRef { Name = inspect.Name, FunctionId = inspectIdentity },
            Arguments = [LocalPlace(fixture.ResultLocal, fixture.RecordType)]
        });
        fixture.Caller.Locals.Add(new MirLocal
        {
            Id = new LocalId { Value = 11 },
            Name = "inspect_result",
            TypeId = new TypeId(BaseTypes.IntId)
        });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.Contains(fixture.Module.Functions, function => function.Name == "make_record__out");
        var borrowCall = Assert.IsType<MirCall>(fixture.Caller.BasicBlocks[0].Instructions[1]);
        Assert.Equal(inspectIdentity.StableIdentityKey,
            Assert.IsType<MirFunctionRef>(borrowCall.Function).FunctionId.StableIdentityKey);
        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name.StartsWith("inspect__caller_owned", StringComparison.Ordinal));
    }

    [Fact]
    public void CallerOwnedAggregate_SameBlockDestructiveCarrierTransfer_PreservesPromotion()
    {
        var fixture = CreateCallerOwnedAggregateFixture();
        var carrierType = new TypeId(8122);
        var carrier = new LocalId { Value = 11 };
        var recovered = new LocalId { Value = 12 };
        fixture.Caller.Locals.Add(new MirLocal { Id = carrier, Name = "carrier", TypeId = carrierType });
        fixture.Caller.Locals.Add(new MirLocal { Id = recovered, Name = "recovered", TypeId = fixture.RecordType });
        var projection = CarrierProjection(carrier, carrierType, fixture.RecordType);
        fixture.Caller.BasicBlocks[0].Instructions.AddRange(
        [
            new MirAlloc { Target = LocalPlace(carrier, carrierType) },
            new MirStore { Target = projection, Value = LocalPlace(fixture.ResultLocal, fixture.RecordType) },
            new MirLoad
            {
                Target = LocalPlace(recovered, fixture.RecordType),
                Source = CarrierProjection(carrier, carrierType, fixture.RecordType),
                MovesOutOfSource = true
            },
            new MirDrop { Value = LocalPlace(carrier, carrierType) },
            new MirDrop { Value = LocalPlace(recovered, fixture.RecordType) }
        ]);

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.Contains(fixture.Module.Functions, function => function.Name == "make_record__out");
        Assert.Contains(recovered, Assert.Single(fixture.Caller.CallerOwnedAggregateAbi.LocalGroups).Locals);
    }

    [Fact]
    public void CallerOwnedAggregate_CrossBlockCarrierTransfer_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedAggregateFixture();
        var carrierType = new TypeId(8123);
        var carrier = new LocalId { Value = 11 };
        var recovered = new LocalId { Value = 12 };
        fixture.Caller.Locals.Add(new MirLocal { Id = carrier, Name = "carrier", TypeId = carrierType });
        fixture.Caller.Locals.Add(new MirLocal { Id = recovered, Name = "recovered", TypeId = fixture.RecordType });
        fixture.Caller.BasicBlocks[0].Instructions.AddRange(
        [
            new MirAlloc { Target = LocalPlace(carrier, carrierType) },
            new MirStore
            {
                Target = CarrierProjection(carrier, carrierType, fixture.RecordType),
                Value = LocalPlace(fixture.ResultLocal, fixture.RecordType)
            }
        ]);
        fixture.Caller.BasicBlocks[0].Terminator = new MirGoto { Target = new BlockId { Value = 2 } };
        fixture.Caller.BasicBlocks.Add(new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions =
            [
                new MirLoad
                {
                    Target = LocalPlace(recovered, fixture.RecordType),
                    Source = CarrierProjection(carrier, carrierType, fixture.RecordType),
                    MovesOutOfSource = true
                },
                new MirDrop { Value = LocalPlace(carrier, carrierType) },
                new MirDrop { Value = LocalPlace(recovered, fixture.RecordType) }
            ],
            Terminator = new MirReturn { Value = IntConstant(0) }
        });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.Equal(2, fixture.Module.Functions.Count);
        Assert.True(fixture.Caller.CallerOwnedAggregateAbi.IsEmpty);
    }

    [Fact]
    public void CallerOwnedAggregate_SmallConstantNestedArray_CarriesInlineStorage()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        var variant = Assert.Single(fixture.Module.Functions, function => function.Name == "make_record__out");
        var storage = Assert.Single(variant.CallerOwnedAggregateAbi.OutArrayStorages);
        Assert.Equal(fixture.ArrayLocal, storage.ArrayLocal);
        Assert.Equal(fixture.ArrayType, storage.ArrayTypeId);
        Assert.Equal(3, storage.Capacity);
        Assert.Equal(8, storage.ElementSize);
        Assert.Equal(88, storage.StorageBytes);
        Assert.Equal(storage, Assert.Single(fixture.Caller.CallerOwnedAggregateAbi.LocalGroups).ArrayStorages.Single());
    }

    [Fact]
    public void CallerOwnedAggregate_DynamicOrOversizedNestedArray_KeepsHeapArrayFallback()
    {
        var dynamicFixture = CreateCallerOwnedNestedArrayFixture(
            LocalPlace(new LocalId { Value = 3 }, new TypeId(BaseTypes.IntId)),
            IntConstant(8));
        dynamicFixture.Factory.Locals.Add(new MirLocal
        {
            Id = new LocalId { Value = 3 },
            Name = "capacity",
            TypeId = new TypeId(BaseTypes.IntId),
            IsParameter = true
        });
        var oversizedFixture = CreateCallerOwnedNestedArrayFixture(IntConstant(505), IntConstant(8));

        new CallerOwnedAggregateSpecializationPass().Run(dynamicFixture.Module);
        new CallerOwnedAggregateSpecializationPass().Run(oversizedFixture.Module);

        Assert.Empty(dynamicFixture.Module.Functions.Single(function => function.Name == "make_record__out")
            .CallerOwnedAggregateAbi.OutArrayStorages);
        Assert.Empty(oversizedFixture.Module.Functions.Single(function => function.Name == "make_record__out")
            .CallerOwnedAggregateAbi.OutArrayStorages);
    }

    [Fact]
    public void CallerOwnedAggregate_NestedArrayCopyOrUnknownCall_KeepsHeapArrayFallback()
    {
        var copyFixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        copyFixture.Factory.BasicBlocks[0].Instructions.Insert(2, new MirCopy
        {
            Target = LocalPlace(new LocalId { Value = 3 }, copyFixture.ArrayType),
            Source = LocalPlace(copyFixture.ArrayLocal, copyFixture.ArrayType)
        });
        copyFixture.Factory.Locals.Add(new MirLocal
        {
            Id = new LocalId { Value = 3 },
            Name = "copy",
            TypeId = copyFixture.ArrayType
        });

        var escapeFixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        escapeFixture.Factory.BasicBlocks[0].Instructions.Insert(2, new MirCall
        {
            Function = new MirFunctionRef { Name = "unknown" },
            Arguments = [LocalPlace(escapeFixture.ArrayLocal, escapeFixture.ArrayType)],
            BorrowedArgumentIndices = new HashSet<int> { 0 }
        });

        new CallerOwnedAggregateSpecializationPass().Run(copyFixture.Module);
        new CallerOwnedAggregateSpecializationPass().Run(escapeFixture.Module);

        Assert.Empty(copyFixture.Module.Functions.Single(function => function.Name == "make_record__out")
            .CallerOwnedAggregateAbi.OutArrayStorages);
        Assert.Empty(escapeFixture.Module.Functions.Single(function => function.Name == "make_record__out")
            .CallerOwnedAggregateAbi.OutArrayStorages);
    }

    [Fact]
    public void CallerOwnedAggregate_MutualRecursion_PropagatesNestedStorageToFixedPoint()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var first = CreateRecursiveCarrier("first", "second", fixture.RecordType);
        var second = CreateRecursiveCarrier("second", "first", fixture.RecordType);
        first.BasicBlocks[0].Instructions.Add(new MirCall
        {
            Target = LocalPlace(new LocalId { Value = 2 }, fixture.RecordType),
            Function = new MirFunctionRef
            {
                Name = fixture.Factory.Name,
                FunctionId = fixture.Factory.FunctionId,
                TypeId = fixture.RecordType
            }
        });
        fixture.Module.Functions.AddRange([first, second]);
        fixture.Caller.BasicBlocks[0].Instructions.Add(new MirCall
        {
            Target = LocalPlace(new LocalId { Value = 10 }, fixture.RecordType),
            Function = new MirFunctionRef
            {
                Name = first.Name,
                FunctionId = first.FunctionId,
                TypeId = fixture.RecordType
            },
            Arguments = [LocalPlace(new LocalId { Value = 10 }, fixture.RecordType)]
        });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        var firstVariant = Assert.Single(fixture.Module.Functions, function =>
            function.Name.StartsWith("first__caller_owned", StringComparison.Ordinal));
        var secondVariant = Assert.Single(fixture.Module.Functions, function =>
            function.Name.StartsWith("second__caller_owned", StringComparison.Ordinal));
        Assert.Single(Assert.Single(firstVariant.CallerOwnedAggregateAbi.LocalGroups).ArrayStorages);
        Assert.Single(Assert.Single(secondVariant.CallerOwnedAggregateAbi.LocalGroups).ArrayStorages);
    }

    [Fact]
    public void CallerOwnedAggregate_IndependentResults_CreateDistinctLocalGroups()
    {
        var fixture = CreateCallerOwnedAggregateFixture();
        var secondResult = new LocalId { Value = 11 };
        fixture.Caller.Locals.Add(new MirLocal
        {
            Id = secondResult,
            Name = "second_result",
            TypeId = fixture.RecordType
        });
        fixture.Caller.BasicBlocks[0].Instructions.Add(new MirCall
        {
            Target = LocalPlace(secondResult, fixture.RecordType),
            Function = new MirFunctionRef
            {
                Name = fixture.Factory.Name,
                FunctionId = fixture.Factory.FunctionId,
                TypeId = fixture.RecordType
            }
        });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.Equal(2, fixture.Caller.CallerOwnedAggregateAbi.LocalGroups.Count);
        Assert.All(
            fixture.Caller.BasicBlocks[0].Instructions.OfType<MirCall>(),
            call => Assert.Contains(
                "|caller-out",
                Assert.IsType<MirFunctionRef>(call.Function).FunctionId.StableIdentityKey,
                StringComparison.Ordinal));
    }

    private static CallerOwnedAggregateFixture CreateCallerOwnedAggregateFixture(bool includeDropAfterCall = false)
    {
        var recordType = new TypeId(8120);
        var factoryIdentity = Identity("make_record");
        var returnLocal = new LocalId { Value = 1 };
        var factory = new MirFunc
        {
            Name = "make_record",
            SourceName = "make_record",
            FunctionId = factoryIdentity,
            ReturnType = recordType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [new MirLocal { Id = returnLocal, Name = "result", TypeId = recordType }],
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
                            Target = LocalPlace(returnLocal, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = "Record",
                                SymbolKind = SymbolKind.Constructor,
                                TypeId = recordType
                            },
                            Arguments = [IntConstant(7)]
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(returnLocal, recordType) }
                }
            ]
        };
        var resultLocal = new LocalId { Value = 10 };
        var instructions = new List<MirInstruction>
        {
            new MirCall
            {
                Target = LocalPlace(resultLocal, recordType),
                Function = new MirFunctionRef
                {
                    Name = factory.Name,
                    FunctionId = factoryIdentity,
                    TypeId = recordType
                }
            }
        };
        if (includeDropAfterCall)
        {
            instructions.Add(new MirDrop { Value = LocalPlace(resultLocal, recordType) });
        }

        var caller = new MirFunc
        {
            Name = "caller",
            FunctionId = Identity("caller"),
            ReturnType = new TypeId(BaseTypes.IntId),
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [new MirLocal { Id = resultLocal, Name = "result", TypeId = recordType }],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions = instructions,
                    Terminator = new MirReturn { Value = IntConstant(0) }
                }
            ]
        };
        var module = new MirModule
        {
            Functions = [factory, caller],
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [recordType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Record",
                        ConstructorName = "Record",
                        FieldTypeIds = [new TypeId(BaseTypes.IntId)]
                    }
                ]
            }
        };
        return new CallerOwnedAggregateFixture(module, factory, caller, recordType, returnLocal, resultLocal);
    }

    private static CallerOwnedNestedArrayFixture CreateCallerOwnedNestedArrayFixture(
        MirOperand capacity,
        MirOperand elementSize)
    {
        var fixture = CreateCallerOwnedAggregateFixture();
        var arrayType = new TypeId(8130);
        var arrayLocal = new LocalId { Value = 2 };
        fixture.Factory.Locals.Add(new MirLocal { Id = arrayLocal, Name = "items", TypeId = arrayType });
        fixture.Factory.BasicBlocks[0].Instructions.Insert(0, new MirCall
        {
            Target = LocalPlace(arrayLocal, arrayType),
            Function = MirRuntimeFunctions.CreateFunctionRef(
                WellKnownStrings.InternalNames.ArrayNew,
                arrayType,
                SourceSpan.Empty),
            Arguments = [capacity, elementSize]
        });
        fixture.Factory.BasicBlocks[0].Instructions.Insert(1, new MirStore
        {
            Target = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = LocalPlace(arrayLocal, arrayType),
                Index = IntConstant(0),
                TypeId = new TypeId(BaseTypes.IntId),
                IndexAccessKind = MirIndexAccessKind.RuntimeArray
            },
            Value = IntConstant(7)
        });
        var constructor = Assert.IsType<MirCall>(fixture.Factory.BasicBlocks[0].Instructions[2]);
        fixture.Factory.BasicBlocks[0].Instructions[2] = constructor with
        {
            Arguments = [LocalPlace(arrayLocal, arrayType)]
        };
        fixture.Module.ConstructorLayouts[fixture.RecordType.Value][0] =
            fixture.Module.ConstructorLayouts[fixture.RecordType.Value][0] with
            {
                FieldTypeIds = [arrayType]
            };
        return new CallerOwnedNestedArrayFixture(
            fixture.Module,
            fixture.Factory,
            fixture.Caller,
            fixture.RecordType,
            arrayType,
            arrayLocal);
    }

    private static MirFunc CreateRecursiveCarrier(string name, string calleeName, TypeId recordType)
    {
        var parameter = new LocalId { Value = 1 };
        var result = new LocalId { Value = 2 };
        return new MirFunc
        {
            Name = name,
            FunctionId = Identity(name),
            ReturnType = recordType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = parameter, Name = "value", TypeId = recordType, IsParameter = true },
                new MirLocal { Id = result, Name = "result", TypeId = recordType }
            ],
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
                            Target = LocalPlace(result, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = calleeName,
                                FunctionId = Identity(calleeName),
                                TypeId = recordType
                            },
                            Arguments = [LocalPlace(parameter, recordType)]
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(result, recordType) }
                }
            ]
        };
    }

    private static FunctionId Identity(string name) => new()
    {
        StableIdentityKey = $"test:{name}",
        Name = name,
        QualifiedName = $"test.{name}"
    };

    private static MirConstant IntConstant(long value) => new()
    {
        TypeId = new TypeId(BaseTypes.IntId),
        Value = new MirConstantValue.IntValue(value)
    };

    private static MirPlace CarrierProjection(LocalId carrier, TypeId carrierType, TypeId valueType) => new()
    {
        Kind = PlaceKind.Index,
        Base = LocalPlace(carrier, carrierType),
        Index = IntConstant(0),
        TypeId = valueType
    };

    private sealed record CallerOwnedAggregateFixture(
        MirModule Module,
        MirFunc Factory,
        MirFunc Caller,
        TypeId RecordType,
        LocalId ReturnLocal,
        LocalId ResultLocal);

    private sealed record CallerOwnedNestedArrayFixture(
        MirModule Module,
        MirFunc Factory,
        MirFunc Caller,
        TypeId RecordType,
        TypeId ArrayType,
        LocalId ArrayLocal);

    [Fact]
    public void CallerOwnedAggregate_ReportsChangeWhenVariantsAreCreated()
    {
        var fixture = CreateCallerOwnedAggregateFixture();
        var inputFunctionCount = fixture.Module.Functions.Count;
        var optimizer = new MirOptimizer();
        optimizer.RegisterPass(new CallerOwnedAggregateSpecializationPass());

        var result = optimizer.OptimizeWithResult(fixture.Module);

        Assert.True(result.Changed, "in-place variant creation must be reported as a change");
        var stats = Assert.Single(result.PassStats);
        Assert.True(stats.Changed);
        Assert.Equal(inputFunctionCount + 1, stats.OutputFunctionCount);
        Assert.NotEqual(MirOptimizationChangeKind.None, stats.ChangeKind);
    }

    [Fact]
    public void CallerOwnedAggregate_NoCandidates_ReportsNoChange()
    {
        var fixture = CreateCallerOwnedAggregateFixture();
        var module = new MirModule
        {
            Name = "plain",
            Functions =
            [
                new MirFunc
                {
                    Name = "plain_fn",
                    FunctionId = Identity("plain_fn"),
                    ReturnType = new TypeId(BaseTypes.IntId),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals = [new MirLocal { Id = new LocalId { Value = 1 }, Name = "x", TypeId = new TypeId(BaseTypes.IntId) }],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions = [],
                            Terminator = new MirReturn
                            {
                                Value = new MirConstant
                                {
                                    TypeId = new TypeId(BaseTypes.IntId),
                                    Value = new MirConstantValue.IntValue(0)
                                }
                            }
                        }
                    ]
                }
            ]
        };
        var optimizer = new MirOptimizer();
        optimizer.RegisterPass(new CallerOwnedAggregateSpecializationPass());

        var result = optimizer.OptimizeWithResult(module);

        Assert.False(result.Changed);
        var stats = Assert.Single(result.PassStats);
        Assert.False(stats.Changed);
        Assert.Equal(MirOptimizationChangeKind.None, stats.ChangeKind);
    }
}
