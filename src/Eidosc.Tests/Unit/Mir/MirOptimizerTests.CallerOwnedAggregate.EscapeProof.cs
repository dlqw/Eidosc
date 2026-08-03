using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Utils;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirOptimizerTests
{
    [Fact]
    public void CallerOwnedAggregate_NestedManagedFieldReturnEscape_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = LocalPlace(new LocalId { Value = 95 }, fixture.ArrayType) },
            extraFunctions: []);

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name == "make_record__out");
        Assert.Empty(caller.CallerOwnedAggregateAbi.LocalGroups);
    }

    [Fact]
    public void CallerOwnedAggregate_NestedManagedFieldStoreEscape_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var scratch = new LocalId { Value = 90 };
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = IntConstant(0) },
            extraFunctions: [],
            extraInstructions:
            [
                new MirStore
                {
                    Value = LocalPlace(new LocalId { Value = 95 }, fixture.ArrayType),
                    Target = LocalPlace(scratch, fixture.ArrayType)
                }
            ]);
        caller.Locals.Add(new MirLocal { Id = scratch, Name = "scratch", TypeId = fixture.ArrayType });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name == "make_record__out");
    }

    [Fact]
    public void CallerOwnedAggregate_NestedManagedFieldFfiEscape_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var external = new MirFunc
        {
            Name = "external_sink",
            FunctionId = Identity("external_sink"),
            ReturnType = new TypeId(BaseTypes.UnitId),
            IsExternal = true
        };
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = IntConstant(0) },
            extraFunctions: [external],
            extraInstructions:
            [
                new MirCall
                {
                    Target = LocalPlace(new LocalId { Value = 91 }, new TypeId(BaseTypes.UnitId)),
                    Function = new MirFunctionRef
                    {
                        Name = external.Name,
                        FunctionId = external.FunctionId,
                        TypeId = new TypeId(BaseTypes.UnitId)
                    },
                    Arguments = [LocalPlace(new LocalId { Value = 95 }, fixture.ArrayType)]
                }
            ]);

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name == "make_record__out");
    }

    [Fact]
    public void CallerOwnedAggregate_NestedManagedFieldRetainingCallee_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var helper = CreateRetainingHelper();
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = IntConstant(0) },
            extraFunctions: [helper],
            extraInstructions:
            [
                new MirCall
                {
                    Target = LocalPlace(new LocalId { Value = 92 }, new TypeId(BaseTypes.UnitId)),
                    Function = new MirFunctionRef
                    {
                        Name = helper.Name,
                        FunctionId = helper.FunctionId,
                        TypeId = new TypeId(BaseTypes.UnitId)
                    },
                    Arguments = [LocalPlace(new LocalId { Value = 95 }, fixture.ArrayType)]
                }
            ]);

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name == "make_record__out");
    }

    [Fact]
    public void CallerOwnedAggregate_NestedManagedFieldReadOnlyCallee_KeepsPromotion()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var reader = CreateReadOnlyHelper();
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = IntConstant(0) },
            extraFunctions: [reader],
            extraInstructions:
            [
                new MirCall
                {
                    Target = LocalPlace(new LocalId { Value = 93 }, new TypeId(BaseTypes.IntId)),
                    Function = new MirFunctionRef
                    {
                        Name = reader.Name,
                        FunctionId = reader.FunctionId,
                        TypeId = new TypeId(BaseTypes.IntId)
                    },
                    Arguments = [LocalPlace(new LocalId { Value = 95 }, fixture.ArrayType)]
                }
            ]);

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        var variant = Assert.Single(fixture.Module.Functions, function => function.Name == "make_record__out");
        Assert.True(variant.CallerOwnedAggregateAbi.HasOutReturn);
    }

    [Fact]
    public void CallerOwnedAggregate_NestedManagedFieldReturnThroughCallee_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var identity = CreateIdentityHelper();
        var returned = new LocalId { Value = 94 };
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = LocalPlace(returned, fixture.ArrayType) },
            extraFunctions: [identity],
            extraInstructions:
            [
                new MirCall
                {
                    Target = LocalPlace(returned, fixture.ArrayType),
                    Function = new MirFunctionRef
                    {
                        Name = identity.Name,
                        FunctionId = identity.FunctionId,
                        TypeId = fixture.ArrayType
                    },
                    Arguments = [LocalPlace(new LocalId { Value = 95 }, fixture.ArrayType)]
                }
            ]);
        caller.Locals.Add(new MirLocal { Id = returned, Name = "returned", TypeId = fixture.ArrayType });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name == "make_record__out");
    }

    [Fact]
    public void CallerOwnedAggregate_ParamStoredInReturnedAggregate_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var containerType = new TypeId(8140);
        var wrapper = CreateAggregateWrapperHelper("wrap_array", containerType, useBranch: false);
        var returned = new LocalId { Value = 96 };
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = LocalPlace(returned, containerType) },
            extraFunctions: [wrapper],
            extraInstructions:
            [
                CallHelper(wrapper, returned, containerType, new LocalId { Value = 95 }, fixture.ArrayType)
            ],
            returnType: containerType);
        caller.Locals.Add(new MirLocal { Id = returned, Name = "wrapped", TypeId = containerType });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name == "make_record__out");
    }

    [Fact]
    public void CallerOwnedAggregate_ParamStoredInAggregateThenPassedToFfi_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var containerType = new TypeId(8141);
        var wrapper = CreateAggregateWrapperHelper("wrap_array_for_ffi", containerType, useBranch: false);
        var external = new MirFunc
        {
            Name = "external_container_sink",
            FunctionId = Identity("external_container_sink"),
            ReturnType = new TypeId(BaseTypes.UnitId),
            IsExternal = true
        };
        var wrapped = new LocalId { Value = 96 };
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = IntConstant(0) },
            extraFunctions: [wrapper, external],
            extraInstructions:
            [
                CallHelper(wrapper, wrapped, containerType, new LocalId { Value = 95 }, fixture.ArrayType),
                new MirCall
                {
                    Target = LocalPlace(new LocalId { Value = 97 }, new TypeId(BaseTypes.UnitId)),
                    Function = new MirFunctionRef
                    {
                        Name = external.Name,
                        FunctionId = external.FunctionId,
                        TypeId = new TypeId(BaseTypes.UnitId)
                    },
                    Arguments = [LocalPlace(wrapped, containerType)]
                }
            ]);
        caller.Locals.Add(new MirLocal { Id = wrapped, Name = "wrapped", TypeId = containerType });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name == "make_record__out");
    }

    [Fact]
    public void CallerOwnedAggregate_ParamStoredAcrossBranchAndReturned_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var containerType = new TypeId(8142);
        var wrapper = CreateAggregateWrapperHelper("branch_wrap_array", containerType, useBranch: true);
        var returned = new LocalId { Value = 96 };
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = LocalPlace(returned, containerType) },
            extraFunctions: [wrapper],
            extraInstructions:
            [
                CallHelper(wrapper, returned, containerType, new LocalId { Value = 95 }, fixture.ArrayType)
            ],
            returnType: containerType);
        caller.Locals.Add(new MirLocal { Id = returned, Name = "wrapped", TypeId = containerType });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name == "make_record__out");
    }

    [Fact]
    public void CallerOwnedAggregate_ParamReloadedFromAggregateAndReturned_KeepsOrdinaryAbi()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var helper = CreateAggregateFieldReturnHelper();
        var returned = new LocalId { Value = 96 };
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = LocalPlace(returned, fixture.ArrayType) },
            extraFunctions: [helper],
            extraInstructions:
            [
                CallHelper(helper, returned, fixture.ArrayType, new LocalId { Value = 95 }, fixture.ArrayType)
            ],
            returnType: fixture.ArrayType);
        caller.Locals.Add(new MirLocal { Id = returned, Name = "returned", TypeId = fixture.ArrayType });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(fixture.Module.Functions, function => function.Name == "make_record__out");
    }

    [Fact]
    public void CallerOwnedAggregate_ParamStoredBesideReadField_KeepsPromotion()
    {
        var fixture = CreateCallerOwnedNestedArrayFixture(IntConstant(3), IntConstant(8));
        var helper = CreateUnrelatedAggregateFieldReaderHelper();
        var result = new LocalId { Value = 96 };
        var caller = CreateEscapeCaller(
            fixture,
            terminator: new MirReturn { Value = LocalPlace(result, new TypeId(BaseTypes.IntId)) },
            extraFunctions: [helper],
            extraInstructions:
            [
                CallHelper(helper, result, new TypeId(BaseTypes.IntId), new LocalId { Value = 95 }, fixture.ArrayType)
            ]);
        caller.Locals.Add(new MirLocal { Id = result, Name = "length_hint", TypeId = new TypeId(BaseTypes.IntId) });

        new CallerOwnedAggregateSpecializationPass().Run(fixture.Module);

        var variant = Assert.Single(fixture.Module.Functions, function => function.Name == "make_record__out");
        Assert.True(variant.CallerOwnedAggregateAbi.HasOutReturn);
    }

    private static MirFunc CreateEscapeCaller(
        CallerOwnedNestedArrayFixture fixture,
        MirTerminator terminator,
        IReadOnlyList<MirFunc> extraFunctions,
        IReadOnlyList<MirInstruction>? extraInstructions = null,
        TypeId? returnType = null)
    {
        var instructions = new List<MirInstruction>
        {
            new MirCall
            {
                Target = LocalPlace(new LocalId { Value = 10 }, fixture.RecordType),
                Function = new MirFunctionRef
                {
                    Name = fixture.Factory.Name,
                    FunctionId = fixture.Factory.FunctionId,
                    TypeId = fixture.RecordType
                }
            },
            new MirLoad
            {
                Target = LocalPlace(new LocalId { Value = 95 }, fixture.ArrayType),
                Source = new MirPlace
                {
                    Kind = PlaceKind.Field,
                    FieldName = "_0",
                    Base = LocalPlace(new LocalId { Value = 10 }, fixture.RecordType),
                    TypeId = fixture.ArrayType
                }
            }
        };
        if (extraInstructions != null)
        {
            instructions.AddRange(extraInstructions);
        }

        var caller = new MirFunc
        {
            Name = "escape_caller",
            FunctionId = Identity("escape_caller"),
            ReturnType = returnType ?? new TypeId(BaseTypes.IntId),
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = new LocalId { Value = 10 }, Name = "owned", TypeId = fixture.RecordType },
                new MirLocal { Id = new LocalId { Value = 95 }, Name = "items", TypeId = fixture.ArrayType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions = instructions,
                    Terminator = terminator
                }
            ]
        };
        foreach (var function in extraFunctions)
        {
            fixture.Module.Functions.Add(function);
        }

        fixture.Module.Functions[1] = caller;
        return caller;
    }

    private static MirFunc CreateAggregateWrapperHelper(
        string name,
        TypeId containerType,
        bool useBranch)
    {
        var parameter = new LocalId { Value = 1 };
        var container = new LocalId { Value = 2 };
        var arrayType = new TypeId(8130);
        var store = new MirStore
        {
            Value = LocalPlace(parameter, arrayType),
            Target = AggregateField(container, containerType, 0, arrayType)
        };
        var blocks = useBranch
            ? new List<MirBasicBlock>
            {
                new()
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirSwitch
                    {
                        Discriminant = EscapeBoolConstant(true),
                        Branches =
                        [
                            new MirSwitchBranch
                            {
                                Value = EscapeBoolConstant(true),
                                Target = new BlockId { Value = 2 }
                            }
                        ],
                        DefaultTarget = new BlockId { Value = 3 }
                    }
                },
                new()
                {
                    Id = new BlockId { Value = 2 },
                    Instructions = [store],
                    Terminator = new MirGoto { Target = new BlockId { Value = 3 } }
                },
                new()
                {
                    Id = new BlockId { Value = 3 },
                    Terminator = new MirReturn { Value = LocalPlace(container, containerType) }
                }
            }
            :
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions = [store],
                    Terminator = new MirReturn { Value = LocalPlace(container, containerType) }
                }
            ];

        return new MirFunc
        {
            Name = name,
            FunctionId = Identity(name),
            ReturnType = containerType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = parameter, Name = "xs", TypeId = arrayType, IsParameter = true },
                new MirLocal { Id = container, Name = "container", TypeId = containerType }
            ],
            BasicBlocks = blocks
        };
    }

    private static MirFunc CreateAggregateFieldReturnHelper()
    {
        var parameter = new LocalId { Value = 1 };
        var container = new LocalId { Value = 2 };
        var returned = new LocalId { Value = 3 };
        var arrayType = new TypeId(8130);
        var containerType = new TypeId(8143);
        return new MirFunc
        {
            Name = "aggregate_field_identity",
            FunctionId = Identity("aggregate_field_identity"),
            ReturnType = arrayType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = parameter, Name = "xs", TypeId = arrayType, IsParameter = true },
                new MirLocal { Id = container, Name = "container", TypeId = containerType },
                new MirLocal { Id = returned, Name = "returned", TypeId = arrayType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirStore
                        {
                            Value = LocalPlace(parameter, arrayType),
                            Target = AggregateField(container, containerType, 0, arrayType)
                        },
                        new MirLoad
                        {
                            Target = LocalPlace(returned, arrayType),
                            Source = AggregateField(container, containerType, 0, arrayType)
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(returned, arrayType) }
                }
            ]
        };
    }

    private static MirFunc CreateUnrelatedAggregateFieldReaderHelper()
    {
        var parameter = new LocalId { Value = 1 };
        var container = new LocalId { Value = 2 };
        var result = new LocalId { Value = 3 };
        var arrayType = new TypeId(8130);
        var containerType = new TypeId(8144);
        var intType = new TypeId(BaseTypes.IntId);
        return new MirFunc
        {
            Name = "read_unrelated_aggregate_field",
            FunctionId = Identity("read_unrelated_aggregate_field"),
            ReturnType = intType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = parameter, Name = "xs", TypeId = arrayType, IsParameter = true },
                new MirLocal { Id = container, Name = "container", TypeId = containerType },
                new MirLocal { Id = result, Name = "result", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirStore
                        {
                            Value = LocalPlace(parameter, arrayType),
                            Target = AggregateField(container, containerType, 0, arrayType)
                        },
                        new MirStore
                        {
                            Value = IntConstant(42),
                            Target = AggregateField(container, containerType, 1, intType)
                        },
                        new MirLoad
                        {
                            Target = LocalPlace(result, intType),
                            Source = AggregateField(container, containerType, 1, intType)
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(result, intType) }
                }
            ]
        };
    }

    private static MirCall CallHelper(
        MirFunc helper,
        LocalId target,
        TypeId targetType,
        LocalId argument,
        TypeId argumentType) => new()
        {
            Target = LocalPlace(target, targetType),
            Function = new MirFunctionRef
            {
                Name = helper.Name,
                FunctionId = helper.FunctionId,
                TypeId = targetType
            },
            Arguments = [LocalPlace(argument, argumentType)]
        };

    private static MirPlace AggregateField(
        LocalId container,
        TypeId containerType,
        int index,
        TypeId fieldType) => new()
        {
            Kind = PlaceKind.Index,
            Base = LocalPlace(container, containerType),
            Index = IntConstant(index),
            TypeId = fieldType,
            IndexAccessKind = MirIndexAccessKind.Aggregate
        };

    private static MirConstant EscapeBoolConstant(bool value) => new()
    {
        Value = new MirConstantValue.BoolValue(value),
        TypeId = new TypeId(BaseTypes.BoolId)
    };

    private static MirFunc CreateRetainingHelper()
    {
        var parameter = new LocalId { Value = 1 };
        var scratch = new LocalId { Value = 2 };
        var arrayType = new TypeId(8130);
        return new MirFunc
        {
            Name = "retaining_helper",
            FunctionId = Identity("retaining_helper"),
            ReturnType = new TypeId(BaseTypes.IntId),
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = parameter, Name = "xs", TypeId = arrayType, IsParameter = true },
                new MirLocal { Id = scratch, Name = "sink", TypeId = arrayType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirStore
                        {
                            Value = LocalPlace(parameter, arrayType),
                            Target = new MirPlace
                            {
                                Kind = PlaceKind.Index,
                                Base = LocalPlace(scratch, arrayType),
                                Index = IntConstant(0),
                                TypeId = arrayType,
                                IndexAccessKind = MirIndexAccessKind.RuntimeArray
                            }
                        }
                    ],
                    Terminator = new MirReturn { Value = IntConstant(0) }
                }
            ]
        };
    }

    private static MirFunc CreateReadOnlyHelper()
    {
        var parameter = new LocalId { Value = 1 };
        var length = new LocalId { Value = 2 };
        var arrayType = new TypeId(8130);
        return new MirFunc
        {
            Name = "read_only_helper",
            FunctionId = Identity("read_only_helper"),
            ReturnType = new TypeId(BaseTypes.IntId),
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = parameter, Name = "xs", TypeId = arrayType, IsParameter = true },
                new MirLocal { Id = length, Name = "len", TypeId = new TypeId(BaseTypes.IntId) }
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
                            Target = LocalPlace(length, new TypeId(BaseTypes.IntId)),
                            Function = MirRuntimeFunctions.CreateFunctionRef(
                                WellKnownStrings.InternalNames.ArrayLength,
                                new TypeId(BaseTypes.IntId),
                                SourceSpan.Empty),
                            Arguments = [LocalPlace(parameter, arrayType)]
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(length, new TypeId(BaseTypes.IntId)) }
                }
            ]
        };
    }

    private static MirFunc CreateIdentityHelper()
    {
        var parameter = new LocalId { Value = 1 };
        var arrayType = new TypeId(8130);
        return new MirFunc
        {
            Name = "identity_helper",
            FunctionId = Identity("identity_helper"),
            ReturnType = arrayType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [new MirLocal { Id = parameter, Name = "xs", TypeId = arrayType, IsParameter = true }],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions = [],
                    Terminator = new MirReturn { Value = LocalPlace(parameter, arrayType) }
                }
            ]
        };
    }
}
